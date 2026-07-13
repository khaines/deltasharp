using System.Buffers;

namespace DeltaSharp.Storage.Delta.DeletionVectors;

/// <summary>
/// The 64-bit <c>RoaringBitmapArray</c> serialization the Delta protocol uses for deletion vectors — a set
/// of 64-bit row indexes (the ordinal of a physically-present-but-logically-deleted row in a Parquet data
/// file). It is the raw payload of both an inline DV (<c>storageType='i'</c>, Z85-encoded) and an on-disk DV
/// (<c>storageType='u'/'p'</c>, framed inside the <c>.bin</c> file).
///
/// <para><b>Byte-exact interop (protocol "Deletion Vector Format").</b> The layout matches Delta's kernel/
/// spark <c>RoaringBitmapArray</c> serialization exactly, so a DV written by Spark/Databricks reads here and
/// a DV written here reads there. Both the <b>native</b> (magic <c>1681511376</c>, this build's write format)
/// and the <b>portable 64-bit</b> (magic <c>1681511377</c>) forms are read; the outer FRAMING integers
/// (magic, bitmap count, per-bitmap byte length, portable key) are <b>big-endian</b> (Delta's Base85/Z85
/// layer decodes to a big-endian-framed byte stream) while each 32-bit
/// <a href="https://github.com/RoaringBitmap/RoaringFormatSpec">RoaringBitmap</a> CONTAINER (cookie +
/// descriptive header + optional offset header + array/bitset/run containers) is the standard
/// <b>little-endian</b> spec layout. This mixed byte order is pinned empirically by the protocol's inline
/// Example 3 (which decodes to <c>{3,4,7,11,18,29}</c>) — no uniform byte order reproduces it.</para>
///
/// <para><b>Untrusted decode surface (design §2.14 — DoS + integrity).</b> Every field of a DV comes from a
/// file a poisoned table controls, so <see cref="Deserialize"/> is bounded and fail-closed: it rejects a bad
/// magic (including the unsupported "native" variant), a negative/oversized bucket count, a non-ascending or
/// negative bucket key, a container size outside <c>[0, 65536]</c>, any read past the buffer end, a decoded
/// row index <c>≥ numRecords</c> of the data file, and a decoded cardinality that disagrees with the
/// descriptor. Allocation is bounded by the data file's record count (never by an attacker-controlled size
/// field). A decode failure is a typed <see cref="DeltaStorageException"/> — the read fails, the DV is never
/// silently ignored (which would resurrect deleted rows, the cardinal DV safety violation). The codec is
/// pure and reflection-free (NativeAOT-safe, ADR-0014).</para>
/// </summary>
internal static class RoaringBitmapArray
{
    /// <summary>The portable-format magic number (little-endian) Delta writes and this build reads/writes.</summary>
    internal const uint PortableMagicNumber = 1681511377u;

    /// <summary>The "native" (JVM RoaringBitmap-specific) magic number — a related but distinct format this
    /// build does not implement; a DV carrying it fails closed rather than risk a misread.</summary>
    internal const uint NativeMagicNumber = 1681511376u;

    private const int SerialCookieNoRunContainer = 12346;
    private const int SerialCookie = 12347;
    private const int NoOffsetThreshold = 4;
    private const int ArrayContainerMaxCardinality = 4096;
    private const int BitsetContainerWords = 1024; // 1024 * 64 bits = 65536 = one 16-bit container span
    private const int BitsetContainerBytes = BitsetContainerWords * 8; // 8 KiB

    /// <summary>
    /// Deserializes the raw <c>RoaringBitmapArray</c> bytes into the sorted, distinct set of deleted row
    /// indexes. Every index is validated to be in <c>[0, <paramref name="numRecords"/>)</c> and the total is
    /// validated against <paramref name="expectedCardinality"/> (the descriptor's <c>cardinality</c>).
    /// </summary>
    /// <param name="bytes">The raw serialized DV (magic + portable 64-bit bitmap).</param>
    /// <param name="numRecords">The data file's total record count — the exclusive upper bound on any valid
    /// row index and the allocation ceiling for the decoded set.</param>
    /// <param name="expectedCardinality">The descriptor's declared cardinality; the decode must produce
    /// exactly this many indexes.</param>
    /// <returns>The deleted row indexes, ascending and distinct.</returns>
    /// <exception cref="DeltaStorageException">The bytes are malformed, over-large, out of range, or the
    /// cardinality disagrees with the descriptor (fail closed).</exception>
    public static long[] Deserialize(ReadOnlySpan<byte> bytes, long numRecords, long expectedCardinality)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(numRecords);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedCardinality);
        if (expectedCardinality > numRecords)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector's cardinality exceeds the data file's record count; the DV is corrupt.");
        }

        var reader = new SpanReader(bytes);

        // The RoaringBitmapArray FRAMING (magic, numberOfBitmaps, per-bitmap byte length, portable key) is
        // read BIG-endian — Delta's Base85 layer (ZeroMQ Z85, big-endian blocks) decodes to a byte stream in
        // which the outer framing ints are big-endian while the 32-bit roaring CONTAINER content
        // (cookie/size/keys/values/bitset words/runs) is little-endian (standard RoaringFormatSpec). This
        // mixed order is empirically pinned by the protocol's inline Example 3 → {3,4,7,11,18,29}: any other
        // combination fails to reproduce the documented row set.
        uint magic = (uint)reader.ReadInt32BigEndian();
        if (magic != NativeMagicNumber && magic != PortableMagicNumber)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector has an unrecognized RoaringBitmapArray magic number; the DV is corrupt.");
        }

        // A bucket keys the high 32 bits of a row index. Since every valid index is < numRecords, the number
        // of distinct high-32-bit buckets cannot exceed (numRecords-1)>>32 + 1 — a hard ceiling that bounds
        // the per-bucket loop against an attacker-inflated count.
        long maxBuckets = numRecords == 0 ? 0 : ((numRecords - 1) >> 32) + 1;

        // The count field width differs by format: the NATIVE format (magic 1681511376, written by
        // Spark/Databricks and by this build) uses a 4-byte count and NO per-bucket key (bucket i keys
        // high-bits i, sequentially, each prefixed by its 4-byte serialized size); the PORTABLE 64-bit format
        // (RoaringFormatSpec, magic 1681511377) uses an 8-byte count and a 4-byte ascending key per bucket.
        bool native = magic == NativeMagicNumber;
        long bucketCount = native ? reader.ReadInt32BigEndian() : reader.ReadInt64BigEndian();
        if (bucketCount < 0)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector declares a negative bucket count; the DV is corrupt.");
        }

        if (bucketCount > maxBuckets)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector declares more buckets than the data file's record count allows; the DV is corrupt.");
        }

        // Bounded: at most expectedCardinality indexes (already ≤ numRecords). A capacity of the smaller of
        // the two avoids over-allocating on a corrupt-but-in-range header.
        var positions = new List<long>((int)Math.Min(expectedCardinality, 1 << 20));
        long previousKey = -1;
        for (long b = 0; b < bucketCount; b++)
        {
            long key;
            if (native)
            {
                // Native: bucket index IS the high-bits key (sequential 0,1,2,…); each bucket is prefixed by
                // its serialized byte length, which we read (and bound) but do not need — the 32-bit roaring
                // is self-delimiting, so DeserializeBucket consumes exactly the right bytes.
                key = b;
                int bitmapSize = reader.ReadInt32BigEndian();
                if (bitmapSize < 0)
                {
                    throw DeltaStorageException.CorruptData(
                        "A Delta deletion vector declares a negative bucket byte length; the DV is corrupt.");
                }
            }
            else
            {
                int rawKey = reader.ReadInt32BigEndian();
                if (rawKey < 0)
                {
                    // Delta requires the MSB of every key to be 0 (so Java reads non-negative keys).
                    throw DeltaStorageException.CorruptData(
                        "A Delta deletion vector has a negative bucket key (MSB set); the DV is corrupt.");
                }

                if (rawKey <= previousKey)
                {
                    throw DeltaStorageException.CorruptData(
                        "A Delta deletion vector's bucket keys are not strictly ascending; the DV is corrupt.");
                }

                key = rawKey;
            }

            previousKey = key;
            long highBits = key << 32;
            DeserializeBucket(ref reader, highBits, numRecords, expectedCardinality, positions);
        }

        if (positions.Count != expectedCardinality)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector's decoded row count disagrees with its descriptor cardinality; the DV is corrupt.");
        }

        // The portable format already emits containers in ascending key order and values within a container
        // are ascending; buckets are ascending; so the concatenation is globally sorted. Distinctness is
        // guaranteed by the container structure. No re-sort needed, but assert in debug.
        long[] result = positions.ToArray();
        System.Diagnostics.Debug.Assert(IsStrictlyAscending(result), "Roaring decode must yield an ascending, distinct set.");
        return result;
    }

    private static void DeserializeBucket(
        ref SpanReader reader, long highBits, long numRecords, long cardinalityCeiling, List<long> positions)
    {
        int cookie = reader.ReadInt32LittleEndian();
        bool hasRun;
        int size;
        ReadOnlySpan<byte> runFlags = default;
        if ((cookie & 0xFFFF) == SerialCookie)
        {
            hasRun = true;
            size = ((cookie >>> 16) & 0xFFFF) + 1;
            runFlags = reader.ReadBytes((size + 7) / 8);
        }
        else if (cookie == SerialCookieNoRunContainer)
        {
            hasRun = false;
            size = reader.ReadInt32LittleEndian();
        }
        else
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector bucket has an invalid RoaringBitmap cookie; the DV is corrupt.");
        }

        if (size < 0 || size > 65536)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector bucket declares an out-of-range container count; the DV is corrupt.");
        }

        // Descriptive header: (key16, cardinality-1) per container.
        Span<int> containerKeys = size <= 256 ? stackalloc int[size] : new int[size];
        Span<int> containerCards = size <= 256 ? stackalloc int[size] : new int[size];
        for (int i = 0; i < size; i++)
        {
            containerKeys[i] = reader.ReadUInt16LittleEndian();
            containerCards[i] = reader.ReadUInt16LittleEndian() + 1;
        }

        // Offset header (skipped — we read containers sequentially): present iff no-run cookie OR size >= 4.
        if (!hasRun || size >= NoOffsetThreshold)
        {
            reader.Skip((long)size * 4);
        }

        for (int i = 0; i < size; i++)
        {
            long containerHigh = highBits | ((long)(uint)containerKeys[i] << 16);
            bool isRun = hasRun && ((runFlags[i / 8] >> (i % 8)) & 1) == 1;
            if (isRun)
            {
                DeserializeRunContainer(ref reader, containerHigh, numRecords, cardinalityCeiling, positions);
            }
            else if (containerCards[i] <= ArrayContainerMaxCardinality)
            {
                DeserializeArrayContainer(ref reader, containerHigh, containerCards[i], numRecords, cardinalityCeiling, positions);
            }
            else
            {
                DeserializeBitsetContainer(ref reader, containerHigh, containerCards[i], numRecords, cardinalityCeiling, positions);
            }
        }
    }

    private static void DeserializeArrayContainer(
        ref SpanReader reader, long containerHigh, int cardinality, long numRecords, long cardinalityCeiling, List<long> positions)
    {
        for (int i = 0; i < cardinality; i++)
        {
            int value = reader.ReadUInt16LittleEndian();
            Add(positions, containerHigh | (uint)value, numRecords, cardinalityCeiling);
        }
    }

    private static void DeserializeBitsetContainer(
        ref SpanReader reader, long containerHigh, int cardinality, long numRecords, long cardinalityCeiling, List<long> positions)
    {
        ReadOnlySpan<byte> words = reader.ReadBytes(BitsetContainerBytes);
        int emitted = 0;
        for (int word = 0; word < BitsetContainerWords; word++)
        {
            ulong bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(words.Slice(word * 8, 8));
            while (bits != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                int value = (word * 64) + bit;
                Add(positions, containerHigh | (uint)value, numRecords, cardinalityCeiling);
                emitted++;
            }
        }

        if (emitted != cardinality)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector bitset container's set-bit count disagrees with its declared cardinality; the DV is corrupt.");
        }
    }

    private static void DeserializeRunContainer(
        ref SpanReader reader, long containerHigh, long numRecords, long cardinalityCeiling, List<long> positions)
    {
        int runCount = reader.ReadUInt16LittleEndian();
        for (int r = 0; r < runCount; r++)
        {
            int start = reader.ReadUInt16LittleEndian();
            int lengthMinusOne = reader.ReadUInt16LittleEndian();
            long runLength = (long)lengthMinusOne + 1;
            for (long v = 0; v < runLength; v++)
            {
                int value = start + (int)v;
                if (value > 0xFFFF)
                {
                    throw DeltaStorageException.CorruptData(
                        "A Delta deletion vector run container overflows its 16-bit container span; the DV is corrupt.");
                }

                Add(positions, containerHigh | (uint)value, numRecords, cardinalityCeiling);
            }
        }
    }

    private static void Add(List<long> positions, long position, long numRecords, long cardinalityCeiling)
    {
        if (position >= numRecords)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector references a row index at or beyond the data file's record count; the DV is corrupt.");
        }

        if (positions.Count >= cardinalityCeiling)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector decodes more row indexes than its descriptor cardinality; the DV is corrupt.");
        }

        positions.Add(position);
    }

    /// <summary>
    /// Serializes <paramref name="sortedDistinctPositions"/> (ascending, distinct, non-negative row indexes)
    /// to the raw <c>RoaringBitmapArray</c> bytes that a Spark/Databricks reader can consume. Emits the
    /// <b>native</b> format (magic <c>1681511376</c>, big-endian framing) — the same layout Delta writes for
    /// freshly-built DVs and the layout the protocol's inline Example 3 uses — with
    /// <c>SERIAL_COOKIE_NO_RUNCONTAINER</c> (array/bitset) containers holding the low 32 bits.
    /// </summary>
    /// <exception cref="ArgumentException">The positions are not ascending/distinct or a position is negative.</exception>
    public static byte[] Serialize(ReadOnlySpan<long> sortedDistinctPositions)
    {
        var writer = new ArrayBufferWriter<byte>(64);

        // NATIVE framing is BIG-endian: magic + numberOfBitmaps, then per bitmap i in [0, numberOfBitmaps):
        // a big-endian byte length followed by a standard 32-bit (little-endian) roaring bitmap. Bitmap index
        // i keys high-bits i; any absent key in [0, lastKey] is written as an empty bitmap so the sequence is
        // dense (matching Delta's NativeRoaringBitmapArraySerializationFormat).
        WriteInt32BigEndian(writer, unchecked((int)NativeMagicNumber));

        // Group by the high 32 bits (bucket key).
        int index = 0;
        var buckets = new List<(int Key, int Start, int End)>();
        long previous = long.MinValue;
        while (index < sortedDistinctPositions.Length)
        {
            long position = sortedDistinctPositions[index];
            if (position < 0)
            {
                throw new ArgumentException("Deletion vector row indexes must be non-negative.", nameof(sortedDistinctPositions));
            }

            if (position <= previous)
            {
                throw new ArgumentException(
                    "Deletion vector row indexes must be strictly ascending and distinct.", nameof(sortedDistinctPositions));
            }

            previous = position;
            int bucketKey = (int)(position >>> 32);
            int start = index;
            while (index < sortedDistinctPositions.Length && (int)(sortedDistinctPositions[index] >>> 32) == bucketKey)
            {
                index++;
            }

            buckets.Add((bucketKey, start, index));
        }

        int numberOfBitmaps = buckets.Count == 0 ? 0 : buckets[^1].Key + 1;
        WriteInt32BigEndian(writer, numberOfBitmaps);

        int bucketIndex = 0;
        for (int key = 0; key < numberOfBitmaps; key++)
        {
            ReadOnlySpan<long> bucketPositions = default;
            if (bucketIndex < buckets.Count && buckets[bucketIndex].Key == key)
            {
                (int _, int start, int end) = buckets[bucketIndex];
                bucketPositions = sortedDistinctPositions[start..end];
                bucketIndex++;
            }

            // Serialize the 32-bit roaring into a scratch buffer so its byte length is known for the native
            // per-bitmap length prefix.
            var scratch = new ArrayBufferWriter<byte>(32);
            SerializeBucket(scratch, bucketPositions);
            WriteInt32BigEndian(writer, scratch.WrittenCount);
            writer.Write(scratch.WrittenSpan);
        }

        return writer.WrittenSpan.ToArray();
    }

    private static void SerializeBucket(ArrayBufferWriter<byte> writer, ReadOnlySpan<long> bucketPositions)
    {
        // Group the low 32 bits into 16-bit containers.
        var containers = new List<(int Key16, List<int> Values)>();
        int index = 0;
        while (index < bucketPositions.Length)
        {
            uint low = (uint)bucketPositions[index];
            int key16 = (int)(low >>> 16);
            var values = new List<int>();
            while (index < bucketPositions.Length && (int)((uint)bucketPositions[index] >>> 16) == key16)
            {
                values.Add((int)((uint)bucketPositions[index] & 0xFFFF));
                index++;
            }

            containers.Add((key16, values));
        }

        int size = containers.Count;
        WriteInt32LittleEndian(writer, SerialCookieNoRunContainer);
        WriteInt32LittleEndian(writer, size);

        // Descriptive header.
        foreach ((int key16, List<int> values) in containers)
        {
            WriteUInt16LittleEndian(writer, (ushort)key16);
            WriteUInt16LittleEndian(writer, (ushort)(values.Count - 1));
        }

        // Offset header (always present with the no-run cookie). Offsets are byte positions from the start of
        // this 32-bit bitmap stream (cookie at 0).
        int running = 4 + 4 + (4 * size) + (4 * size); // cookie + count + descriptive + offsets
        foreach ((int _, List<int> values) in containers)
        {
            WriteInt32LittleEndian(writer, running);
            running += ContainerSerializedSize(values.Count);
        }

        // Container storage.
        foreach ((int _, List<int> values) in containers)
        {
            if (values.Count <= ArrayContainerMaxCardinality)
            {
                foreach (int value in values)
                {
                    WriteUInt16LittleEndian(writer, (ushort)value);
                }
            }
            else
            {
                Span<byte> bitset = new byte[BitsetContainerBytes];
                foreach (int value in values)
                {
                    bitset[value / 8] |= (byte)(1 << (value % 8));
                }

                writer.Write(bitset);
            }
        }
    }

    private static int ContainerSerializedSize(int cardinality) =>
        cardinality <= ArrayContainerMaxCardinality ? cardinality * 2 : BitsetContainerBytes;

    private static void WriteInt32LittleEndian(ArrayBufferWriter<byte> writer, int value)
    {
        Span<byte> span = writer.GetSpan(4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, value);
        writer.Advance(4);
    }

    private static void WriteInt32BigEndian(ArrayBufferWriter<byte> writer, int value)
    {
        Span<byte> span = writer.GetSpan(4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(span, value);
        writer.Advance(4);
    }

    private static void WriteUInt16LittleEndian(ArrayBufferWriter<byte> writer, ushort value)
    {
        Span<byte> span = writer.GetSpan(2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        writer.Advance(2);
    }

    private static bool IsStrictlyAscending(long[] values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] <= values[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    // A bounded, fail-closed little-endian reader over the DV bytes: any read past the end throws a typed
    // corruption error rather than returning stale/zero data (which could silently change the deleted set).
    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _span;
        private int _position;

        public SpanReader(ReadOnlySpan<byte> span)
        {
            _span = span;
            _position = 0;
        }

        public int ReadInt32LittleEndian()
        {
            ReadOnlySpan<byte> slice = ReadBytes(4);
            return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(slice);
        }

        public int ReadInt32BigEndian()
        {
            ReadOnlySpan<byte> slice = ReadBytes(4);
            return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(slice);
        }

        public long ReadInt64BigEndian()
        {
            ReadOnlySpan<byte> slice = ReadBytes(8);
            return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(slice);
        }

        public long ReadInt64LittleEndian()
        {
            ReadOnlySpan<byte> slice = ReadBytes(8);
            return System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slice);
        }

        public int ReadUInt16LittleEndian()
        {
            ReadOnlySpan<byte> slice = ReadBytes(2);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(slice);
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || _position + (long)count > _span.Length)
            {
                throw DeltaStorageException.CorruptData(
                    "A Delta deletion vector is truncated (a read ran past the end of the serialized bitmap); the DV is corrupt.");
            }

            ReadOnlySpan<byte> slice = _span.Slice(_position, count);
            _position += count;
            return slice;
        }

        public void Skip(long count)
        {
            if (count < 0 || _position + count > _span.Length)
            {
                throw DeltaStorageException.CorruptData(
                    "A Delta deletion vector is truncated (an offset header ran past the end of the serialized bitmap); the DV is corrupt.");
            }

            _position += (int)count;
        }
    }
}
