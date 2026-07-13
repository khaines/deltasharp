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

    [Fact]
    public async Task WriteThenLoad_ReconstructsExactPositions_OffsetIsOne()
    {
        using LocalFileSystemBackend backend = Backend();
        var uuid = Guid.NewGuid();
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
        var uuid = Guid.NewGuid();
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
        var uuid = Guid.NewGuid();
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
        var uuid = Guid.NewGuid();
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
        var uuid = Guid.NewGuid();
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
