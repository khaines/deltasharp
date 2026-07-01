using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Memory;
using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Spill;

/// <summary>
/// Serializes a single logical row of an operator's columns to a self-describing spill frame and reads
/// it back into output column vectors. It is the bridge between the columnar data path and the
/// row-oriented spill medium, and it deliberately <b>reuses the EPIC-02 row format</b> rather than
/// inventing a second serialization: <see cref="BinaryRowEncoder"/> lays out the row, and
/// <see cref="RowSpillSerializer"/> wraps it in the schema-fingerprinted, bounds-checked frame that sort,
/// join, spill, and shuffle already share. Decode validates every frame against the expected schema, so a
/// corrupt or foreign frame fails with a <see cref="RowValidationException"/> instead of reading out of
/// bounds.
/// </summary>
/// <remarks>
/// Boxing each lane through <see cref="KeyBoxing.ToRowDataValue"/> reuses the one CLR-shape mapping every
/// key path already uses (<c>byte→sbyte</c>, <c>decimal→Int128</c>, <c>string→string</c>,
/// <c>binary→byte[]</c>), so a spilled-then-recovered value is bit-identical to the in-memory one. v1
/// spill covers the atomic column types the managed vectors support; nested columns have no managed
/// vector (so no operator produces them) and are out of scope here.
/// </remarks>
internal sealed class RowSpillCodec
{
    private readonly StructType _schema;
    private readonly BinaryRowEncoder _encoder;
    private readonly object?[] _scratch;

    /// <summary>Builds a codec for <paramref name="schema"/>; one per spilling operator (or per spilled run group).</summary>
    internal RowSpillCodec(StructType schema)
    {
        _schema = schema;
        _encoder = new BinaryRowEncoder(new NativeMemoryAllocator());
        _scratch = new object?[schema.Count];
    }

    /// <summary>The schema rows are framed against (its fingerprint is checked on every decode).</summary>
    internal StructType Schema => _schema;

    /// <summary>
    /// Serializes logical row <paramref name="row"/> of <paramref name="columns"/> into a spill frame.
    /// The columns are in <see cref="_schema"/> order. Returns the framed bytes (header + payload).
    /// </summary>
    internal byte[] Encode(ColumnVector[] columns, int row)
    {
        for (int c = 0; c < _scratch.Length; c++)
        {
            _scratch[c] = KeyBoxing.ToRowDataValue(columns[c], row);
        }

        // RowData copies _scratch, so the buffer is safely reused across rows.
        var rowData = new RowData(_schema, _scratch);
        BinaryRow encoded = _encoder.Encode(rowData);
        try
        {
            return RowSpillSerializer.WriteFrame(encoded);
        }
        finally
        {
            encoded.Dispose();
        }
    }

    /// <summary>
    /// Decodes a spill frame into a fresh single-row <see cref="ManagedColumnBatch"/> over
    /// <see cref="_schema"/>. Used by the join/exchange recover paths that materialize a recovered row.
    /// </summary>
    /// <exception cref="RowValidationException">The frame is truncated, foreign, or fails schema validation.</exception>
    internal RowData Decode(ReadOnlySpan<byte> frame) =>
        RowSpillSerializer.ReadFrame(frame, _schema, out _);

    /// <summary>
    /// Appends the values of a decoded frame into <paramref name="destination"/> (one mutable vector per
    /// field, in schema order), growing each vector by one logical row.
    /// </summary>
    /// <exception cref="RowValidationException">The frame is truncated, foreign, or fails schema validation.</exception>
    internal void DecodeInto(MutableColumnVector[] destination, ReadOnlySpan<byte> frame)
    {
        RowData row = RowSpillSerializer.ReadFrame(frame, _schema, out _);
        for (int c = 0; c < destination.Length; c++)
        {
            AppendValue(destination[c], row[c]);
        }
    }

    /// <summary>Appends one decoded <see cref="RowData"/> value (its binary-row CLR shape) to <paramref name="dest"/>.</summary>
    internal static void AppendValue(MutableColumnVector dest, object? value)
    {
        if (value is null)
        {
            dest.AppendNull();
            return;
        }

        switch (dest.Type)
        {
            case BooleanType:
                dest.AppendValue((bool)value);
                break;
            case ByteType:
                // RowData stores tinyint as sbyte; the managed vector stores it as byte (same bits).
                dest.AppendValue(unchecked((byte)(sbyte)value));
                break;
            case ShortType:
                dest.AppendValue((short)value);
                break;
            case IntegerType or DateType:
                dest.AppendValue((int)value);
                break;
            case LongType or TimestampType:
                dest.AppendValue((long)value);
                break;
            case FloatType:
                dest.AppendValue((float)value);
                break;
            case DoubleType:
                dest.AppendValue((double)value);
                break;
            case DecimalType { IsCompact: true }:
                dest.AppendValue((long)(Int128)value);
                break;
            case DecimalType:
                dest.AppendValue((Int128)value);
                break;
            case StringType:
                dest.AppendBytes(System.Text.Encoding.UTF8.GetBytes((string)value));
                break;
            case BinaryType:
                dest.AppendBytes((byte[])value);
                break;
            default:
                throw new UnsupportedTypeException(
                    $"Spill recovery has no append for type '{dest.Type.SimpleString}'.");
        }
    }
}
