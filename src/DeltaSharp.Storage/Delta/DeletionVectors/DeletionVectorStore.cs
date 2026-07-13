using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Delta.DeletionVectors;

/// <summary>
/// Reads and writes the on-disk / inline bytes of a deletion vector, framing a raw
/// <see cref="RoaringBitmapArray"/> per the protocol "Deletion Vector File Storage Format" and decoding it
/// into the sorted, bounded, validated set of deleted row indexes.
///
/// <para><b>On-disk <c>.bin</c> frame (big-endian, matching Delta's <c>DeletionVectorStore</c>).</b> A file
/// is a 1-byte format version (<c>1</c>) followed by one or more DV blocks; each block is a 4-byte
/// <c>dataSize</c>, that many raw bitmap bytes, then a 4-byte CRC-32 of the bitmap. A descriptor's
/// <c>offset</c> points at the <c>dataSize</c> field and its <c>sizeInBytes</c> equals <c>dataSize</c>; the
/// reader validates both the size and the checksum before decoding.</para>
///
/// <para><b>Fail-closed loads (design §2.14).</b> The descriptor and the file bytes come from a poisoned
/// table. A load rejects an over-large declared size (bounded to the data file's legitimate scale, never an
/// attacker's arbitrary size field), a size/checksum mismatch, a truncated range, an absolute-path
/// (<c>'p'</c>) DV that could escape the confined table root, and every malformed-bitmap case
/// <see cref="RoaringBitmapArray.Deserialize"/> guards. A failure is a typed
/// <see cref="DeltaStorageException"/> and the read fails — the DV is never silently dropped.</para>
/// </summary>
internal static class DeletionVectorStore
{
    /// <summary>The DV file format version this build writes (protocol: <c>1</c>).</summary>
    public const byte FileFormatVersion = 1;

    private const int DataSizeLength = 4;
    private const int ChecksumLength = 4;

    /// <summary>
    /// Loads and decodes the deletion vector <paramref name="descriptor"/> into the sorted set of deleted row
    /// indexes, validating every index against <paramref name="numRecords"/> (the data file's total record
    /// count) and the descriptor's cardinality.
    /// </summary>
    /// <exception cref="DeltaStorageException">The DV is malformed, over-large, out of range, its checksum or
    /// size disagrees, or it uses the unsupported absolute-path storage (fail closed).</exception>
    public static async Task<long[]> LoadAsync(
        IStorageBackend backend,
        DeletionVectorDescriptor descriptor,
        long numRecords,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(descriptor);

        ValidateDeclaredSize(descriptor.SizeInBytes, numRecords);

        if (descriptor.IsInline)
        {
            byte[] inlineBytes = descriptor.DecodeInlineBytes();
            return RoaringBitmapArray.Deserialize(inlineBytes, numRecords, descriptor.Cardinality);
        }

        if (descriptor.StorageType == DeletionVectorDescriptor.StorageTypeAbsolutePath)
        {
            throw DeltaStorageException.CorruptData(
                "This Delta table stores a deletion vector by absolute path ('p'), which the confined local "
                + "backend cannot resolve safely in this build. The read fails closed rather than risk "
                + "escaping the table root or returning deleted rows.");
        }

        string relativePath = descriptor.ResolveRelativePath();
        int offset = descriptor.Offset ?? 0;
        long frameLength = (long)descriptor.SizeInBytes + DataSizeLength + ChecksumLength;
        byte[] frame = await ReadRangeAsync(backend, relativePath, offset, frameLength, cancellationToken)
            .ConfigureAwait(false);

        int dataSize = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(0, DataSizeLength));
        if (dataSize != descriptor.SizeInBytes)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector's on-disk size disagrees with its descriptor sizeInBytes; the DV file is corrupt.");
        }

        ReadOnlySpan<byte> bitmap = frame.AsSpan(DataSizeLength, dataSize);
        int expectedChecksum = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(DataSizeLength + dataSize, ChecksumLength));
        int actualChecksum = Crc32(bitmap);
        if (expectedChecksum != actualChecksum)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector's CRC-32 checksum does not match its bitmap bytes; the DV file is corrupt.");
        }

        return RoaringBitmapArray.Deserialize(bitmap, numRecords, descriptor.Cardinality);
    }

    /// <summary>
    /// Serializes <paramref name="sortedDistinctPositions"/> to a raw <see cref="RoaringBitmapArray"/>, wraps
    /// it in a single-DV <c>.bin</c> frame (version byte + size + bitmap + CRC-32), atomically writes the file
    /// at <paramref name="relativePath"/>, and returns the descriptor fields (<c>offset</c>, <c>sizeInBytes</c>)
    /// a caller records on the <c>add</c> action.
    /// </summary>
    /// <returns>The byte offset of the DV block (always <c>1</c> for a single-DV file) and its raw size.</returns>
    public static async Task<(int Offset, int SizeInBytes)> WriteOnDiskAsync(
        IStorageBackend backend,
        string relativePath,
        ReadOnlyMemory<long> sortedDistinctPositions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        byte[] bitmap = RoaringBitmapArray.Serialize(sortedDistinctPositions.Span);
        int checksum = Crc32(bitmap);

        int frameLength = 1 + DataSizeLength + bitmap.Length + ChecksumLength;
        var file = new byte[frameLength];
        file[0] = FileFormatVersion;
        int offset = 1; // the DV block (dataSize field) begins right after the version byte
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(offset, DataSizeLength), bitmap.Length);
        bitmap.CopyTo(file.AsSpan(offset + DataSizeLength));
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(offset + DataSizeLength + bitmap.Length, ChecksumLength), checksum);

        bool written = await backend.PutIfAbsentAsync(relativePath, file, cancellationToken).ConfigureAwait(false);
        if (!written)
        {
            // A DV file name is derived from a fresh UUID, so a pre-existing name means a UUID collision or a
            // retried write landing on the same deterministic name — fail closed rather than trust bytes we
            // did not just write.
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector file already exists at its derived path; refusing to overwrite (fail closed).");
        }

        return (offset, bitmap.Length);
    }

    // Bounds the declared DV size to the data file's legitimate scale so an attacker size field cannot force
    // an unbounded allocation. A raw DV never needs more than ~2 bytes per deleted row (array containers) or
    // ~1 bit per row (bitset), so 4 bytes/record + 1 MiB headroom is a safe, generous ceiling; an absolute
    // 512 MiB cap guards a degenerate huge-file case.
    private static void ValidateDeclaredSize(int sizeInBytes, long numRecords)
    {
        if (sizeInBytes < 0)
        {
            throw DeltaStorageException.CorruptData("A Delta deletion vector declares a negative size; the descriptor is corrupt.");
        }

        long allowed = Math.Min(512L * 1024 * 1024, (numRecords * 4L) + (1L << 20));
        if (sizeInBytes > allowed)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector declares a size far larger than the data file's record count could "
                + "produce; refusing to allocate (fail closed).");
        }
    }

    private static async Task<byte[]> ReadRangeAsync(
        IStorageBackend backend, string relativePath, long offset, long length, CancellationToken cancellationToken)
    {
        Stream stream = await backend.ReadRangeAsync(relativePath, offset, length, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[length];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream
                    .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw DeltaStorageException.CorruptData(
                        "A Delta deletion vector file is shorter than its descriptor's offset+size declares; the DV is corrupt.");
                }

                total += read;
            }

            return buffer;
        }
    }

    // CRC-32 (IEEE 802.3, reflected, polynomial 0xEDB88320) matching java.util.zip.CRC32, cast to a signed
    // int exactly as Delta does (the high bytes are zero; only equality matters).
    private static int Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                uint mask = (uint)(-(int)(crc & 1));
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }

        return unchecked((int)~crc);
    }
}

/// <summary>Supplies the UUID a fresh relative-path (<c>'u'</c>) deletion vector <c>.bin</c> file name is
/// derived from. Abstracted (like column mapping's physical-name source) so the banned nondeterministic
/// <c>Guid.NewGuid</c> is never used and a golden fixture can inject reproducible file names.</summary>
internal interface IDeletionVectorIdSource
{
    /// <summary>Returns the UUID for the next DV file.</summary>
    Guid NextId();
}

/// <summary>The production id source: a fresh cryptographically-random UUID per DV file (never the banned
/// <c>Guid.NewGuid</c>).</summary>
internal sealed class RandomDeletionVectorIdSource : IDeletionVectorIdSource
{
    /// <summary>The shared instance.</summary>
    public static RandomDeletionVectorIdSource Instance { get; } = new();

    /// <inheritdoc/>
    public Guid NextId() => new(RandomNumberGenerator.GetBytes(16));
}

/// <summary>A deterministic id source deriving each UUID from a seed + monotonic counter via SHA-256, so a
/// golden DV fixture assigns byte-for-byte reproducible file names (no ambient state, no banned symbols).
/// <b>Not thread-safe.</b></summary>
internal sealed class SeededDeletionVectorIdSource : IDeletionVectorIdSource
{
    private readonly string _seed;
    private int _counter;

    /// <summary>Creates a deterministic source seeded by <paramref name="seed"/>.</summary>
    public SeededDeletionVectorIdSource(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        _seed = seed;
    }

    /// <inheritdoc/>
    public Guid NextId()
    {
        int index = _counter++;
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{_seed}:{index}")));
        return new Guid(digest.AsSpan(0, 16));
    }
}
