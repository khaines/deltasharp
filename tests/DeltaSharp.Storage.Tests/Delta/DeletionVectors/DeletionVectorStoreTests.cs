using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta.DeletionVectors;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta.DeletionVectors;

/// <summary>
/// Round-trip + fail-closed tests for the on-disk <c>.bin</c> deletion-vector frame
/// (<see cref="DeletionVectorStore"/>): version byte + big-endian dataSize + raw
/// <see cref="RoaringBitmapArray"/> + big-endian CRC-32, addressed by the descriptor's <c>offset</c>/
/// <c>sizeInBytes</c>. These exercise a real <see cref="LocalFileSystemBackend"/> over a temp directory, so
/// they are isolated in a non-parallel collection to avoid a shared-filesystem flake.
/// </summary>
[Collection(DeletionVectorFileTestCollection.Name)]
public sealed class DeletionVectorStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dv-store-" + Guid.NewGuid().ToString("N"));

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

    private LocalFileSystemBackend Backend()
    {
        Directory.CreateDirectory(_root);
        return new LocalFileSystemBackend(_root);
    }

    private static (string RelativePath, DeletionVectorDescriptor Descriptor) DescribeFor(
        Guid uuid, int offset, int sizeInBytes, long cardinality)
    {
        string pathOrInlineDv = DeletionVectorDescriptor.BuildRelativePathOrInlineDv(string.Empty, uuid);
        DeletionVectorDescriptor descriptor =
            DeletionVectorDescriptor.ForRelativePath(pathOrInlineDv, offset, sizeInBytes, cardinality);
        return (descriptor.ResolveRelativePath(), descriptor);
    }

    // Deterministic DV-file UUIDs for tests (src/ bans Guid.NewGuid for DV-id generation; tests prefer the
    // seeded source). The UUID only names the on-disk .bin, so a fixed seed keeps runs reproducible.
    private static Guid SeededUuid(string seed) => new SeededDeletionVectorIdSource(seed).NextId();

    // Real Spark on-disk deletion-vector .bin golden — verified byte-for-byte against Spark's
    // dv-with-columnmapping/deletion_vector_10ffbe3a-...bin. Full 43-byte single-DV frame that deletes EXACTLY
    // physical row {0} (cardinality 1): version 0x01, big-endian dataSize 0x00000022 (34), then a 34-byte
    // portable-LE RoaringBitmapArray, then big-endian CRC-32 0xf7a6b4b5. Embedded inline so the interop test
    // is hermetic/offline (filesize == 9 + dataSize).
    private const string RealSparkGoldenBinHex =
        "0100000022d1d339640100000000000000000000003a3000000100000000000000100000000000f7a6b4b5";

    // The CRC-covered payload (golden bytes 5..38): the serialized RoaringBitmapArray Spark writes for {0}.
    // magic d1 d3 39 64 = 1681511377 read little-endian; numBuckets = 1 (8B LE); bucket key = 0 (4B LE);
    // then a standard little-endian 32-bit RoaringBitmap (cookie 0x303a) holding the single value 0.
    private const string RealSparkGoldenPayloadHex =
        "d1d339640100000000000000000000003a3000000100000000000000100000000000";

    [Fact]
    public async Task Load_RealSparkGoldenBin_DecodesExactlyPositionZero()
    {
        using LocalFileSystemBackend backend = Backend();
        Guid uuid = SeededUuid("golden-load");
        (string relativePath, _) = DescribeFor(uuid, 1, 34, 1);

        // Land the REAL Spark bytes on disk verbatim (no DeltaSharp writer involved), then decode them through
        // the production read path — proving DeltaSharp reads genuine Spark deletion vectors.
        byte[] golden = Convert.FromHexString(RealSparkGoldenBinHex);
        Assert.Equal(43, golden.Length); // filesize == 9 + dataSize(34)
        Assert.True(await backend.PutIfAbsentAsync(relativePath, golden, CancellationToken.None));

        DeletionVectorDescriptor descriptor =
            DeletionVectorDescriptor.ForRelativePath(
                DeletionVectorDescriptor.BuildRelativePathOrInlineDv(string.Empty, uuid),
                offset: 1, sizeInBytes: 34, cardinality: 1);

        long[] decoded = await DeletionVectorStore.LoadAsync(
            backend, descriptor, numRecords: 3, CancellationToken.None);

        // Ground truth: this DV deletes exactly physical row 0, cardinality 1 — and en route the strict
        // dataSize(34)==sizeInBytes(34) equality and the CRC-32 0xf7a6b4b5 both validated.
        Assert.Equal(new long[] { 0 }, decoded);
    }

    [Fact]
    public async Task WriteOnDisk_PositionZero_IsByteIdenticalToRealSparkGolden()
    {
        using LocalFileSystemBackend backend = Backend();
        Guid uuid = SeededUuid("golden-write");
        (string relativePath, _) = DescribeFor(uuid, 1, 0, 0);

        (int offset, int sizeInBytes) = await DeletionVectorStore.WriteOnDiskAsync(
            backend, relativePath, new long[] { 0 }, CancellationToken.None);

        Assert.Equal(1, offset);
        Assert.Equal(34, sizeInBytes); // matches the real Spark descriptor's sizeInBytes

        // The bytes DeltaSharp put on disk must be byte-for-byte the real Spark golden — the proof that Spark
        // (and any conformant Delta reader) can read DeltaSharp's deletion vectors.
        byte[] onDisk = await File.ReadAllBytesAsync(Path.Combine(_root, relativePath));
        Assert.Equal(Convert.FromHexString(RealSparkGoldenBinHex), onDisk);

        // And the CRC-covered payload (bytes 5..38) equals Spark's serialized RoaringBitmapArray exactly.
        Assert.Equal(Convert.FromHexString(RealSparkGoldenPayloadHex), onDisk[5..39]);
    }


    [Fact]
    public async Task WriteThenLoad_ReconstructsExactPositions_OffsetIsOne()
    {
        using LocalFileSystemBackend backend = Backend();
        var uuid = SeededUuid("write-then-load");
        string pathOrInlineDv = DeletionVectorDescriptor.BuildRelativePathOrInlineDv(string.Empty, uuid);
        string relativePath =
            DeletionVectorDescriptor.ForRelativePath(pathOrInlineDv, 1, 0, 0).ResolveRelativePath();

        long[] positions = { 0, 3, 4, 7, 11, 18, 29, 1000 };
        (int offset, int sizeInBytes) = await DeletionVectorStore.WriteOnDiskAsync(
            backend, relativePath, positions, CancellationToken.None);

        // The single-DV frame places the dataSize field immediately after the 1-byte version.
        Assert.Equal(1, offset);

        DeletionVectorDescriptor descriptor =
            DeletionVectorDescriptor.ForRelativePath(pathOrInlineDv, offset, sizeInBytes, positions.Length);
        long[] decoded = await DeletionVectorStore.LoadAsync(
            backend, descriptor, numRecords: 1001, CancellationToken.None);

        Assert.Equal(positions, decoded);
    }

    [Fact]
    public async Task Load_CorruptedBitmapByte_FailsChecksumClosed()
    {
        using LocalFileSystemBackend backend = Backend();
        var uuid = SeededUuid("corrupt-bitmap");
        (string relativePath, DeletionVectorDescriptor stub) = DescribeFor(uuid, 1, 0, 0);

        long[] positions = { 2, 5, 9 };
        (int offset, int sizeInBytes) = await DeletionVectorStore.WriteOnDiskAsync(
            backend, relativePath, positions, CancellationToken.None);
        _ = stub;

        // Flip a byte inside the bitmap payload on disk — the CRC-32 must catch it and fail closed rather than
        // decode a wrong (and therefore wrong-deleted-row-set) bitmap.
        string absolute = Path.Combine(_root, relativePath);
        byte[] bytes = await File.ReadAllBytesAsync(absolute);
        bytes[1 + 4 + 4] ^= 0xFF; // first bitmap content byte (past version + dataSize)
        await File.WriteAllBytesAsync(absolute, bytes);

        DeletionVectorDescriptor descriptor =
            DeletionVectorDescriptor.ForRelativePath(
                DeletionVectorDescriptor.BuildRelativePathOrInlineDv(string.Empty, uuid),
                offset, sizeInBytes, positions.Length);

        await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeletionVectorStore.LoadAsync(backend, descriptor, numRecords: 100, CancellationToken.None));
    }

    [Fact]
    public async Task Load_DeclaredSizeDisagreesWithFile_FailsClosed()
    {
        using LocalFileSystemBackend backend = Backend();
        var uuid = SeededUuid("declared-size-disagrees");
        (string relativePath, _) = DescribeFor(uuid, 1, 0, 0);

        long[] positions = { 1, 2, 3 };
        (int offset, int sizeInBytes) = await DeletionVectorStore.WriteOnDiskAsync(
            backend, relativePath, positions, CancellationToken.None);

        // Lie about the size in the descriptor: the on-disk dataSize field will disagree → fail closed.
        DeletionVectorDescriptor descriptor =
            DeletionVectorDescriptor.ForRelativePath(
                DeletionVectorDescriptor.BuildRelativePathOrInlineDv(string.Empty, uuid),
                offset, sizeInBytes + 4, positions.Length);

        await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeletionVectorStore.LoadAsync(backend, descriptor, numRecords: 100, CancellationToken.None));
    }

    [Fact]
    public async Task Load_PositionAtOrAboveNumRecords_FailsClosed()
    {
        using LocalFileSystemBackend backend = Backend();
        var uuid = SeededUuid("position-out-of-range");
        (string relativePath, _) = DescribeFor(uuid, 1, 0, 0);

        long[] positions = { 3, 99 };
        (int offset, int sizeInBytes) = await DeletionVectorStore.WriteOnDiskAsync(
            backend, relativePath, positions, CancellationToken.None);

        DeletionVectorDescriptor descriptor =
            DeletionVectorDescriptor.ForRelativePath(
                DeletionVectorDescriptor.BuildRelativePathOrInlineDv(string.Empty, uuid),
                offset, sizeInBytes, positions.Length);

        // The file only has 50 records, but the DV claims row 99 is deleted — an out-of-range index that
        // fails closed (never silently drops the offending position).
        await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeletionVectorStore.LoadAsync(backend, descriptor, numRecords: 50, CancellationToken.None));
    }

    [Fact]
    public async Task Load_OversizedDeclaredSize_FailsClosedBeforeAllocation()
    {
        using LocalFileSystemBackend backend = Backend();
        var uuid = SeededUuid("oversized-declared-size");
        (string relativePath, _) = DescribeFor(uuid, 1, 0, 0);
        _ = await DeletionVectorStore.WriteOnDiskAsync(
            backend, relativePath, new long[] { 1 }, CancellationToken.None);

        // An attacker-inflated sizeInBytes (1 GiB) against a tiny file must be rejected by the allocation
        // bound, not attempted.
        DeletionVectorDescriptor descriptor =
            DeletionVectorDescriptor.ForRelativePath(
                DeletionVectorDescriptor.BuildRelativePathOrInlineDv(string.Empty, uuid),
                1, 1 << 30, 1);

        await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeletionVectorStore.LoadAsync(backend, descriptor, numRecords: 10, CancellationToken.None));
    }
}

/// <summary>Serializes the deletion-vector tests that touch a real temp-directory backend so they never race
/// on the shared filesystem (mirrors <c>ColumnMappingTestCollection</c>).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeletionVectorFileTestCollection
{
    /// <summary>The shared collection name for filesystem-touching deletion-vector tests.</summary>
    public const string Name = "DeletionVectorFileTests";
}
