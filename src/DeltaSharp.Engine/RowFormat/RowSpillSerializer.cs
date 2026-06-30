using System.Buffers.Binary;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.RowFormat;

/// <summary>
/// The fixed-size header that precedes a serialized row in a spill/shuffle frame: the
/// schema-version metadata and payload length that <see cref="RowSpillSerializer"/> writes and
/// validates (STORY-02.4.2 AC3/AC4).
/// </summary>
public readonly struct RowFrameHeader : IEquatable<RowFrameHeader>
{
    internal RowFrameHeader(ushort formatVersion, int schemaFingerprint, int payloadLength)
    {
        FormatVersion = formatVersion;
        SchemaFingerprint = schemaFingerprint;
        PayloadLength = payloadLength;
    }

    /// <summary>The serialization format version (so the on-disk frame can evolve compatibly).</summary>
    public ushort FormatVersion { get; }

    /// <summary>
    /// The deterministic, process-independent fingerprint of the row's schema — the schema-version
    /// metadata a reader checks against its expected schema before trusting the payload.
    /// </summary>
    public int SchemaFingerprint { get; }

    /// <summary>The byte length of the row payload that follows the header.</summary>
    public int PayloadLength { get; }

    /// <inheritdoc/>
    public bool Equals(RowFrameHeader other) =>
        FormatVersion == other.FormatVersion
        && SchemaFingerprint == other.SchemaFingerprint
        && PayloadLength == other.PayloadLength;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RowFrameHeader other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(FormatVersion, SchemaFingerprint, PayloadLength);

    /// <summary>Value equality.</summary>
    public static bool operator ==(RowFrameHeader left, RowFrameHeader right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(RowFrameHeader left, RowFrameHeader right) => !left.Equals(right);
}

/// <summary>
/// Serializes a binary row to a self-describing spill/shuffle frame and reads it back with bounded
/// validation (STORY-02.4.2 AC3/AC4). A frame is a fixed 16-byte header — magic, format version,
/// schema fingerprint, and payload length — followed by the row's encoded bytes. The deserialize
/// path range-checks every field before reading, so malformed or truncated input fails with a
/// <see cref="RowValidationException"/> and never reads out of bounds.
/// </summary>
/// <remarks>
/// The frame is the byte contract that lets sort, join, spill, and shuffle share one row
/// representation: the writer emits straight into a caller-provided span (no allocation), and the
/// reader decodes from a read-only span without ever slicing past its end. Actual file/stream I/O
/// belongs to the execution/memory layers; this type owns only the byte frame.
/// </remarks>
public static class RowSpillSerializer
{
    /// <summary>The size in bytes of the fixed frame header (8-byte aligned, like the payload).</summary>
    public const int HeaderSize = 16;

    /// <summary>The current serialization format version.</summary>
    public const ushort CurrentFormatVersion = 1;

    private const int MagicOffset = 0;
    private const int VersionOffset = 4;
    private const int ReservedOffset = 6;
    private const int FingerprintOffset = 8;
    private const int PayloadLengthOffset = 12;

    // "DSR1" — DeltaSharp Row. A FIXED brand tag, not a version counter: the trailing '1' is part of
    // the brand and is NEVER bumped on a format change. Format evolution happens only through the
    // VersionOffset field, so a v1 reader reports a newer frame as an unsupported *version* rather than
    // misdiagnosing it as foreign bytes (bad magic).
    private static ReadOnlySpan<byte> Magic => "DSR1"u8;

    /// <summary>The total frame size (header + payload) for <paramref name="row"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    public static int GetFrameSize(BinaryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return HeaderSize + row.Length;
    }

    /// <summary>
    /// Writes the framed <paramref name="row"/> into <paramref name="destination"/> and returns the
    /// number of bytes written. The destination must be at least <see cref="GetFrameSize(BinaryRow)"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public static int WriteFrame(BinaryRow row, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(row);
        ReadOnlySpan<byte> payload = row.AsSpan();
        int frameSize = HeaderSize + payload.Length;
        if (destination.Length < frameSize)
        {
            throw new ArgumentException(
                $"Destination span ({destination.Length}) is smaller than the frame ({frameSize}).",
                nameof(destination));
        }

        Magic.CopyTo(destination[MagicOffset..]);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[VersionOffset..], CurrentFormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[ReservedOffset..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(destination[FingerprintOffset..], SchemaFingerprint(row.Schema));
        BinaryPrimitives.WriteInt32LittleEndian(destination[PayloadLengthOffset..], payload.Length);
        payload.CopyTo(destination[HeaderSize..]);
        return frameSize;
    }

    /// <summary>Convenience overload that allocates a right-sized frame array (for tests and cold paths).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    public static byte[] WriteFrame(BinaryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        byte[] frame = new byte[GetFrameSize(row)];
        WriteFrame(row, frame);
        return frame;
    }

    /// <summary>
    /// Reads and validates only the frame header at the start of <paramref name="source"/>, returning
    /// its schema-version metadata. Does not touch the payload.
    /// </summary>
    /// <exception cref="RowValidationException">The header is truncated, has a bad magic, or an unsupported version.</exception>
    public static RowFrameHeader ReadHeader(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
        {
            throw new RowValidationException(
                $"Frame is truncated: need {HeaderSize} header bytes but have {source.Length}.");
        }

        if (!source[MagicOffset..(MagicOffset + Magic.Length)].SequenceEqual(Magic))
        {
            throw new RowValidationException("Frame has an invalid magic; not a DeltaSharp row frame.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source[VersionOffset..]);
        if (version != CurrentFormatVersion)
        {
            throw new RowValidationException(
                $"Unsupported frame format version {version}; expected {CurrentFormatVersion}.");
        }

        int fingerprint = BinaryPrimitives.ReadInt32LittleEndian(source[FingerprintOffset..]);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(source[PayloadLengthOffset..]);
        if (payloadLength < 0)
        {
            throw new RowValidationException($"Frame declares a negative payload length {payloadLength}.");
        }

        return new RowFrameHeader(version, fingerprint, payloadLength);
    }

    /// <summary>
    /// Reads a framed row from <paramref name="source"/>, validating it against
    /// <paramref name="schema"/>, and returns the decoded values (with nulls). The number of bytes
    /// consumed (header + payload) is returned in <paramref name="bytesConsumed"/> so frames can be
    /// read back-to-back from a spill segment.
    /// </summary>
    /// <param name="source">The bytes to read a frame from.</param>
    /// <param name="schema">The expected schema; its fingerprint must match the frame's.</param>
    /// <param name="bytesConsumed">The total frame length consumed.</param>
    /// <returns>The decoded row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <exception cref="RowValidationException">
    /// The frame is truncated, has a bad magic/version, declares a payload length that runs past the
    /// buffer, carries a schema fingerprint that does not match <paramref name="schema"/>, or has a
    /// structurally invalid payload. The buffer is never read out of bounds.
    /// </exception>
    public static RowData ReadFrame(ReadOnlySpan<byte> source, StructType schema, out int bytesConsumed)
    {
        ArgumentNullException.ThrowIfNull(schema);

        RowFrameHeader header = ReadHeader(source);
        long frameSize = (long)HeaderSize + header.PayloadLength;
        if (frameSize > source.Length)
        {
            throw new RowValidationException(
                $"Frame payload ({header.PayloadLength} bytes) runs past the buffer "
                + $"({source.Length - HeaderSize} available).");
        }

        int expectedFingerprint = SchemaFingerprint(schema);
        if (header.SchemaFingerprint != expectedFingerprint)
        {
            throw new RowValidationException(
                $"Frame schema fingerprint 0x{header.SchemaFingerprint:X8} does not match the expected "
                + $"schema 0x{expectedFingerprint:X8} ({schema.SimpleString}).");
        }

        ReadOnlySpan<byte> payload = source.Slice(HeaderSize, header.PayloadLength);

        // Validate before decoding: after this passes, every offset/length the decoder follows is
        // proven in-bounds, so decoding cannot read past the payload.
        BinaryRowValidator.ValidateStruct(payload, schema);
        RowData decoded = RowDecoder.DecodeStruct(payload, schema);
        bytesConsumed = (int)frameSize;
        return decoded;
    }

    /// <summary>
    /// The schema-version fingerprint written into a frame — the schema's deterministic,
    /// process-independent hash. Exposed so callers can pre-check compatibility.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    public static int SchemaFingerprint(StructType schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.GetHashCode();
    }
}
