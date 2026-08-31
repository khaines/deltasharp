using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// End-to-end oracle for the Inc-A read resolver (#806, design §2.3/§3.3) through the production
/// <see cref="DeltaReadSource"/> door: a partitioned table reads back correctly whether its <c>add.path</c> is
/// stored (a) unencoded (<c>k_dec==k_lit</c>, the common case), (b) legacy literal-percent (<c>region=a%2Fb</c>
/// — the #806 L1 migration trap, resolved by the decoded-miss→literal fallback), or (c) URI-encoded
/// (<c>region%3DUS</c> — the go-forward L2 / Spark-delta-rs shape Inc-B will write, resolved by the decode).
/// Partition truth always comes from <c>add.partitionValues</c>, so the row values are identical across all
/// three layouts.
/// </summary>
[Collection(ColumnMappingTestCollection.Name)]
public sealed class PartitionPathReadResolutionTests : IDisposable
{
    private readonly string _root;

    private static readonly StructType PartitionedSchema = new(new[]
    {
        new StructField("region", DataTypes.StringType, nullable: true),
        new StructField("id", DataTypes.LongType, nullable: false),
    });

    public PartitionPathReadResolutionTests() =>
        _root = Path.Combine(Path.GetTempPath(), "ppenc-read-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task UnencodedAddPath_ReadsThroughResolver_NoOp()
    {
        // (a) k_dec == k_lit: the resolver opens the literal key directly (no fallback), unchanged behavior.
        await WritePartitionedAsync(("US", 1L), ("US", 2L));
        List<(string Region, long Id)> rows = await ReadRowsAsync();
        Assert.Equal(new[] { ("US", 1L), ("US", 2L) }, rows.OrderBy(r => r.Id).ToArray());
    }

    [Fact]
    public async Task LegacyLiteralPercentAddPath_ReadsViaLiteralFallback()
    {
        // (b) A partition VALUE containing '/', written by the current write door as an on-disk 'region=a%2Fb'
        // with a LITERAL add.path. The resolver decodes it to 'region=a/b' (a miss) and falls back to the
        // literal key — a naive decode-always read would corrupt this (the #806 L1 trap).
        await WritePartitionedAsync(("a/b", 7L));
        // Sanity: the committed add.path is the literal percent form.
        Snapshot snap = await LoadSnapshotAsync();
        Assert.Contains("region=a%2Fb/", Assert.Single(snap.ActiveFiles).Path, StringComparison.Ordinal);

        List<(string Region, long Id)> rows = await ReadRowsAsync();
        (string Region, long Id) row = Assert.Single(rows);
        Assert.Equal(("a/b", 7L), row); // partition truth from add.partitionValues (raw 'a/b')
    }

    [Fact]
    public async Task UriEncodedAddPath_ReadsViaDecode_ForwardL2Shape()
    {
        // (c) The go-forward L2 / Spark-delta-rs shape: the on-disk directory is 'region=US' but the committed
        // add.path is URI-encoded ('region%3DUS'). Inc-A does not WRITE this yet (that is Inc-B), so we author
        // it by rewriting the committed add.path to the encoded form while the file stays at 'region=US'. The
        // resolver decodes 'region%3DUS' -> 'region=US' and opens the real file on the first try.
        await WritePartitionedAsync(("US", 5L));
        Snapshot before = await LoadSnapshotAsync();
        string literal = Assert.Single(before.ActiveFiles).Path;                 // region=US/part-<token>.parquet
        string encoded = literal.Replace("=", "%3D", StringComparison.Ordinal);  // region%3DUS/part-<token>.parquet
        Assert.NotEqual(literal, encoded);
        RewriteCommittedAddPath(literal, encoded);

        // The physical file is still at the literal on-disk key; only the log's add.path is now encoded.
        Assert.True(File.Exists(Path.Combine(_root, literal)));
        Assert.False(File.Exists(Path.Combine(_root, encoded)));

        List<(string Region, long Id)> rows = await ReadRowsAsync();
        (string Region, long Id) row = Assert.Single(rows);
        Assert.Equal(("US", 5L), row);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private async Task WritePartitionedAsync(params (string? Region, long Id)[] rows)
    {
        MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        foreach ((string? r, long i) in rows)
        {
            if (r is null)
            {
                region.AppendNull();
            }
            else
            {
                region.AppendBytes(Encoding.UTF8.GetBytes(r));
            }

            id.AppendValue(i);
        }

        var batch = new ManagedColumnBatch(PartitionedSchema, new ColumnVector[] { region, id }, rows.Length);
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        await target.AppendAsync(PartitionedSchema, new[] { "region" }, new[] { batch });
    }

    private async Task<Snapshot> LoadSnapshotAsync()
    {
        using var backend = new DeltaSharp.Storage.Backends.LocalFileSystemBackend(_root);
        return await new DeltaSharp.Storage.Delta.DeltaLog(backend).LoadSnapshotAsync(version: null);
    }

    private async Task<List<(string Region, long Id)>> ReadRowsAsync()
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        int regionIdx = info.Schema.IndexOf("region");
        int idIdx = info.Schema.IndexOf("id");

        var result = new List<(string, long)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector region = batch.Column(regionIdx);
            ColumnVector id = batch.Column(idIdx);
            for (int r = 0; r < batch.RowCount; r++)
            {
                result.Add((Encoding.UTF8.GetString(region.GetBytes(r)), id.GetValue<long>(r)));
            }
        }

        return result;
    }

    private void RewriteCommittedAddPath(string literalPath, string encodedPath)
    {
        string commit = Path.Combine(_root, "_delta_log", "00000000000000000000.json");
        string text = File.ReadAllText(commit);
        string rewritten = text.Replace(
            "\"path\":\"" + literalPath + "\"",
            "\"path\":\"" + encodedPath + "\"",
            StringComparison.Ordinal);
        Assert.NotEqual(text, rewritten); // the add.path substitution actually happened
        File.WriteAllText(commit, rewritten);
    }
}
