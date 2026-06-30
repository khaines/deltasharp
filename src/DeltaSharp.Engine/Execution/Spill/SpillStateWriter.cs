using System.Buffers.Binary;

namespace DeltaSharp.Engine.Execution.Spill;

/// <summary>
/// A growable little-endian byte writer for serializing an aggregate group's partial state to a spill
/// record (STORY-03.6.2 AC1). Each aggregator appends its own fixed sequence of primitives; the matching
/// <see cref="SpillStateReader"/> reads them back in the same order to merge a spilled partial into the
/// recovery table. It is a cold-path helper (spill only fires under pressure), so it favors simplicity
/// over zero-allocation; the buffer is reused across records via <see cref="Reset"/>.
/// </summary>
internal sealed class SpillStateWriter
{
    private byte[] _buffer = new byte[64];
    private int _length;

    /// <summary>The bytes written so far.</summary>
    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    /// <summary>Resets the writer to empty, retaining the backing buffer.</summary>
    internal void Reset() => _length = 0;

    internal void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    internal void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_length++] = value;
    }

    internal void WriteInt(int value)
    {
        EnsureCapacity(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += sizeof(int);
    }

    internal void WriteLong(long value)
    {
        EnsureCapacity(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += sizeof(long);
    }

    internal void WriteDouble(double value)
    {
        EnsureCapacity(sizeof(double));
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_length), value);
        _length += sizeof(double);
    }

    internal void WriteSingle(float value)
    {
        EnsureCapacity(sizeof(float));
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_length), value);
        _length += sizeof(float);
    }

    internal void WriteShort(short value)
    {
        EnsureCapacity(sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(_length), value);
        _length += sizeof(short);
    }

    internal void WriteInt128(Int128 value)
    {
        EnsureCapacity(16);
        BinaryPrimitives.WriteInt128LittleEndian(_buffer.AsSpan(_length), value);
        _length += 16;
    }

    /// <summary>Writes a length-prefixed byte payload (e.g. a serialized key frame or a string value).</summary>
    internal void WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteInt(value.Length);
        EnsureCapacity(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    private void EnsureCapacity(int extra)
    {
        if (_length + extra <= _buffer.Length)
        {
            return;
        }

        int capacity = _buffer.Length * 2;
        while (capacity < _length + extra)
        {
            capacity *= 2;
        }

        Array.Resize(ref _buffer, capacity);
    }
}

/// <summary>
/// The forward-only little-endian reader paired with <see cref="SpillStateWriter"/>: reads a spilled
/// group record's fields back in the order they were written so each aggregator can merge its partial
/// state. It is a <see langword="ref struct"/> over the record span, so it never copies the payload.
/// </summary>
internal ref struct SpillStateReader
{
    private readonly ReadOnlySpan<byte> _span;
    private int _position;

    internal SpillStateReader(ReadOnlySpan<byte> span)
    {
        _span = span;
        _position = 0;
    }

    /// <summary>Whether any unread bytes remain.</summary>
    internal readonly bool HasRemaining => _position < _span.Length;

    internal bool ReadBool() => ReadByte() != 0;

    internal byte ReadByte() => _span[_position++];

    internal int ReadInt()
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(_span[_position..]);
        _position += sizeof(int);
        return value;
    }

    internal long ReadLong()
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(_span[_position..]);
        _position += sizeof(long);
        return value;
    }

    internal double ReadDouble()
    {
        double value = BinaryPrimitives.ReadDoubleLittleEndian(_span[_position..]);
        _position += sizeof(double);
        return value;
    }

    internal float ReadSingle()
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(_span[_position..]);
        _position += sizeof(float);
        return value;
    }

    internal short ReadShort()
    {
        short value = BinaryPrimitives.ReadInt16LittleEndian(_span[_position..]);
        _position += sizeof(short);
        return value;
    }

    internal Int128 ReadInt128()
    {
        Int128 value = BinaryPrimitives.ReadInt128LittleEndian(_span[_position..]);
        _position += 16;
        return value;
    }

    /// <summary>Reads a length-prefixed byte payload written by <see cref="SpillStateWriter.WriteBytes"/>.</summary>
    internal ReadOnlySpan<byte> ReadBytes()
    {
        int length = ReadInt();
        ReadOnlySpan<byte> value = _span.Slice(_position, length);
        _position += length;
        return value;
    }
}
