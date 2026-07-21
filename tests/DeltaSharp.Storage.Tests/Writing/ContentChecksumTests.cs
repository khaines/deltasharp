using System.Collections.Immutable;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Tests.Delta;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Writing;

/// <summary>
/// Tests for the advisory per-file content checksum (#504, follow-up to STORY-05.6.1 / #195). A write
/// stamps each committed <c>add</c> with a deterministic <c>sha256:&lt;hex&gt;</c> fingerprint of the file's
/// on-disk bytes under the <c>deltaSharp.contentChecksum</c> tag, so a content-equivalence / duplication
/// audit can compare files across rewrites without re-reading them. Covers the pure helper (stability,
/// content-sensitivity, format, determinism), the write-door end-to-end path (tag on the committed add,
/// present in the raw <c>_delta_log</c> JSON, read back through the log, and equal to a re-hash of the
/// on-disk file), and the content-equivalence property (byte-identical content ⇒ same checksum; different
/// content ⇒ different checksum).
/// </summary>
[Collection(ColumnMappingTestCollection.Name)]
public sealed class ContentChecksumTests : IDisposable
{
    private readonly string _root;

    private static readonly StructType FlatSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    public ContentChecksumTests() =>
        _root = Path.Combine(Path.GetTempPath(), "content-checksum-" + Guid.NewGuid().ToString("N"));

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

    // ---------------------------------------------------------------- helper unit tests

    [Fact]
    public void TagKey_IsTheNamespacedConformanceLiteral()
    {
        // #504 R3 FIX 2: pin the LITERAL key, not the symbol. The DeltaSharp-namespaced prefix is a
        // cross-engine conformance point (an unprefixed "contentChecksum" could collide with another
        // engine's tag), so dropping/renaming the vendor prefix must fail here — a symbolic reference alone
        // would silently accept such a regression.
        Assert.Equal("deltaSharp.contentChecksum", ContentChecksum.TagKey);
    }

    [Fact]
    public void Compute_IsStableAndSelfDescribing()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello delta");

        string first = ContentChecksum.Compute(content);
        string second = ContentChecksum.Compute(content);

        // Reproducible for identical input (no wall-clock/random) and self-described as SHA-256 lowercase hex.
        Assert.Equal(first, second);
        Assert.StartsWith("sha256:", first, StringComparison.Ordinal);
        Assert.Equal("sha256:".Length + 64, first.Length);

        // Known-answer vector: a HARDCODED digest (independent of the .NET SHA-256 primitive) pins the exact
        // bytes + format hashed, so an algorithm/format swap is caught even if the primitive changed too.
        Assert.Equal("sha256:" + ExpectedHelloDeltaDigest, first);
    }

    // SHA-256("hello delta") as lowercase hex — a fixed literal, NOT recomputed from SHA256.HashData.
    private const string ExpectedHelloDeltaDigest =
        "7d719de73a098637438a6eb5d34e1061543f59b87a928ec0d81e7b486bb60a5f";

    [Fact]
    public void Compute_IsContentSensitive()
    {
        string a = ContentChecksum.Compute(Encoding.UTF8.GetBytes("payload-A"));
        string b = ContentChecksum.Compute(Encoding.UTF8.GetBytes("payload-B"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TagsFor_StampsChecksum_AndTryReadRoundTrips()
    {
        byte[] content = Encoding.UTF8.GetBytes("some bytes");
        string checksum = ContentChecksum.Compute(content);

        ImmutableSortedDictionary<string, string> stamped = ContentChecksum.TagsFor(checksum);
        Assert.Equal(checksum, ContentChecksum.TryRead(stamped));

        // A null checksum ⇒ empty tags ⇒ TryRead returns null (pre-checksum / other-engine file).
        ImmutableSortedDictionary<string, string> none = ContentChecksum.TagsFor(null);
        Assert.Empty(none);
        Assert.Null(ContentChecksum.TryRead(none));
    }

    // ---------------------------------------------------------------- end-to-end write door

    [Fact]
    public async Task Write_StampsContentChecksumTag_ThatReadsBackAndMatchesOnDiskBytes()
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a"), (2, "b")) });

        var backend = new LocalFileSystemBackend(_root);
        try
        {
            Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();
            AddFileAction add = Assert.Single(snapshot.ActiveFiles);

            // The tag is present and reads back through the log reader.
            string? checksum = ContentChecksum.TryRead(add.Tags);
            Assert.NotNull(checksum);
            Assert.StartsWith("sha256:", checksum, StringComparison.Ordinal);

            // It is the checksum of the ACTUAL on-disk file bytes (correctness of what was hashed).
            byte[] onDisk = await ReadAllAsync(backend, add.Path);
            Assert.Equal(ContentChecksum.Compute(onDisk), checksum);

            // It is present in the RAW _delta_log commit JSON (a strict Delta reader ignores the tag). Assert
            // the LITERAL namespaced key (not the symbol) so an un-namespacing regression is caught here too.
            string commitJson = await File.ReadAllTextAsync(CommitFilePath(version: 0));
            Assert.Contains("deltaSharp.contentChecksum", commitJson, StringComparison.Ordinal);
            Assert.Contains(checksum, commitJson, StringComparison.Ordinal);
        }
        finally
        {
            backend.Dispose();
        }
    }

    [Fact]
    public async Task Write_ByteIdenticalContent_YieldsSameChecksum_DifferentContent_Differs()
    {
        // Two INDEPENDENT tables, each written by a fresh write door: identical logical content must produce
        // the identical checksum across runs (determinism), and different content a different checksum
        // (the content-equivalence property the audit relies on).
        string sameA = await WriteSingleFileChecksumAsync(FlatBatch((1, "a"), (2, "b")));
        string sameB = await WriteSingleFileChecksumAsync(FlatBatch((1, "a"), (2, "b")));
        string different = await WriteSingleFileChecksumAsync(FlatBatch((1, "a"), (2, "c")));

        Assert.Equal(sameA, sameB);
        Assert.NotEqual(sameA, different);
    }

    // Writes one file through a fresh write door under a throwaway root and returns its committed checksum.
    private async Task<string> WriteSingleFileChecksumAsync(ColumnBatch batch)
    {
        string root = Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N"));
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(root))
        {
            await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { batch });
        }

        var backend = new LocalFileSystemBackend(root);
        try
        {
            Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();
            AddFileAction add = Assert.Single(snapshot.ActiveFiles);
            string? checksum = ContentChecksum.TryRead(add.Tags);
            Assert.NotNull(checksum);
            return checksum!;
        }
        finally
        {
            backend.Dispose();
        }
    }

    private string CommitFilePath(long version) =>
        Path.Combine(_root, "_delta_log", version.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".json");

    private static async Task<byte[]> ReadAllAsync(LocalFileSystemBackend backend, string path)
    {
        Stream stream = await backend.OpenReadAsync(path, CancellationToken.None);
        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, CancellationToken.None);
            return buffer.ToArray();
        }
    }

    private static ColumnBatch FlatBatch(params (long Id, string? Name)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((long i, string? n) in rows)
        {
            id.AppendValue(i);
            if (n is null)
            {
                name.AppendNull();
            }
            else
            {
                name.AppendBytes(Encoding.UTF8.GetBytes(n));
            }
        }

        return new ManagedColumnBatch(FlatSchema, new ColumnVector[] { id, name }, rows.Length);
    }
}
