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
        // A simple ASCII partition value round-trips: Inc-B writes 'region%3DUS' (layer 2), the resolver
        // decodes it to the on-disk 'region=US' key, and the rows read back.
        await WritePartitionedAsync(("US", 1L), ("US", 2L));
        List<(string Region, long Id)> rows = await ReadRowsAsync();
        Assert.Equal(new[] { ("US", 1L), ("US", 2L) }, rows.OrderBy(r => r.Id).ToArray());
    }

    [Fact]
    public async Task TwoLayerWrite_ProducesUriEncodedAddPath_ReadsViaDecode_806IncB()
    {
        // #806 Inc-B: the write door now emits a two-layer path — an escapePathName on-disk key (layer 1) and a
        // URI-encoded add.path (layer 2). A partition value 'a/b' lands on disk as 'region=a%2Fb'; its committed
        // add.path is 'region%3Da%252Fb' (the '=' separator and the layer-1 '%2F' re-encoded). The Inc-A
        // resolver decodes the add.path back to the on-disk key and reads the rows (the go-forward L2 shape,
        // interoperable with Spark/delta-rs).
        await WritePartitionedAsync(("a/b", 5L));
        Snapshot snap = await LoadSnapshotAsync();
        AddFileAction add = Assert.Single(snap.ActiveFiles);

        // Layer 2: add.path is URI-encoded.
        Assert.Contains("region%3Da%252Fb/", add.Path, StringComparison.Ordinal);
        // Layer 1: the on-disk key is the single decode of add.path.
        string physical = Uri.UnescapeDataString(add.Path);
        Assert.Contains("region=a%2Fb/", physical, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, physical)));
        Assert.False(File.Exists(Path.Combine(_root, add.Path))); // the encoded key is not a real on-disk path

        List<(string Region, long Id)> rows = await ReadRowsAsync();
        (string Region, long Id) row = Assert.Single(rows);
        Assert.Equal(("a/b", 5L), row); // partition truth from add.partitionValues (raw 'a/b')
    }

    [Fact]
    public async Task LegacyLiteralPercentAddPath_ReadsViaResolverFallback_806IncB()
    {
        // A pre-#806 DeltaSharp table stored add.path LITERALLY (no layer-2 URI encoding): on-disk 'region=a%2Fb'
        // with add.path 'region=a%2Fb'. Simulate it by rewriting the two-layer add.path back to the legacy
        // literal form while the on-disk file stays at 'region=a%2Fb'. The resolver decodes 'region=a%2Fb' to
        // 'region=a/b' (a miss) and falls back to the literal key — a naive decode-always read would corrupt it
        // (the #806 L1 migration trap).
        await WritePartitionedAsync(("a/b", 7L));
        Snapshot before = await LoadSnapshotAsync();
        string encoded = Assert.Single(before.ActiveFiles).Path;   // region%3Da%252Fb/part-*
        string legacyLiteral = Uri.UnescapeDataString(encoded);    // region=a%2Fb/part-*  (the real on-disk key)
        Assert.NotEqual(encoded, legacyLiteral);
        RewriteCommittedAddPath(encoded, legacyLiteral);

        // On disk unchanged; the committed add.path is now the legacy literal.
        Assert.True(File.Exists(Path.Combine(_root, legacyLiteral)));
        Snapshot after = await LoadSnapshotAsync();
        Assert.Equal(legacyLiteral, Assert.Single(after.ActiveFiles).Path);

        List<(string Region, long Id)> rows = await ReadRowsAsync();
        (string Region, long Id) row = Assert.Single(rows);
        Assert.Equal(("a/b", 7L), row); // read via decoded-miss -> literal fallback; raw 'a/b' from partitionValues
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
