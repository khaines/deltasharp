using DeltaSharp.Types;

namespace DeltaSharp.Engine.RowFormat;

/// <summary>
/// Encodes a row's sort-key fields into a single order-preserving byte sequence so that a bytewise
/// (<c>memcmp</c>) comparison of two encodings matches the configured Spark ordering
/// (STORY-02.4.2 AC1). This is the row representation sort, join, spill, and shuffle all share for
/// their keys.
/// </summary>
/// <remarks>
/// <para>
/// Per key field the encoder writes a one-byte <b>null marker</b> followed (for non-null values) by
/// the order-preserving value bytes from <see cref="ByteSortableEncoding"/>:
/// </para>
/// <list type="bullet">
/// <item><description><b>Null marker</b> — <c>0x00</c> (nulls-first) or <c>0x02</c> (nulls-last) for
/// a null, always <c>0x01</c> for a present value. Because the marker differs whenever nullness
/// differs, a null never compares against a present value's bytes; the marker alone decides, exactly
/// as the configured <see cref="NullSortOrder"/> requires — independent of direction.</description></item>
/// <item><description><b>Value bytes</b> — the ascending encoding, complemented when the field is
/// descending. Fixed-width encodings are constant length and variable-width encodings are
/// self-terminating, so two present values always compare within their own bytes before the next
/// field is reached.</description></item>
/// </list>
/// <para>
/// The encode path is allocation-free: <see cref="Encode(RowData, System.Span{byte})"/> writes
/// straight into a caller-provided span sized from <see cref="GetMaxEncodedLength(RowData)"/>. The
/// <see cref="Encode(RowData)"/> convenience overload allocates a right-sized array for tests and
/// cold paths.
/// </para>
/// </remarks>
public sealed class SortKeyEncoder
{
    private const byte NullMarkerFirst = 0x00;
    private const byte PresentMarker = 0x01;
    private const byte NullMarkerLast = 0x02;

    private readonly StructType _schema;
    private readonly int[] _keyFields;
    private readonly SortKeyOrdering[] _orderings;
    private readonly DataType[] _keyTypes;

    /// <summary>
    /// Builds an encoder for the <paramref name="keyFields"/> of <paramref name="schema"/>, each
    /// ordered by the matching entry of <paramref name="orderings"/>.
    /// </summary>
    /// <param name="schema">The row schema the keys index into.</param>
    /// <param name="keyFields">The field indices to use as the sort key, in priority order.</param>
    /// <param name="orderings">One ordering per key field (same length as <paramref name="keyFields"/>).</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="keyFields"/> is empty or its length differs from <paramref name="orderings"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A field index is outside the schema.</exception>
    /// <exception cref="RowFormatException">A key field's type is not byte-sortable (for example a nested or void type).</exception>
    public SortKeyEncoder(StructType schema, IReadOnlyList<int> keyFields, IReadOnlyList<SortKeyOrdering> orderings)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(keyFields);
        ArgumentNullException.ThrowIfNull(orderings);
        if (keyFields.Count == 0)
        {
            throw new ArgumentException("A sort key needs at least one field.", nameof(keyFields));
        }

        if (keyFields.Count != orderings.Count)
        {
            throw new ArgumentException(
                $"keyFields ({keyFields.Count}) and orderings ({orderings.Count}) must have the same length.",
                nameof(orderings));
        }

        _schema = schema;
        _keyFields = new int[keyFields.Count];
        _orderings = new SortKeyOrdering[keyFields.Count];
        _keyTypes = new DataType[keyFields.Count];
        for (int k = 0; k < keyFields.Count; k++)
        {
            int field = keyFields[k];
            if (field < 0 || field >= schema.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keyFields), field, $"Key field index is outside schema {schema.SimpleString}.");
            }

            DataType type = schema[field].DataType;
            if (!ByteSortableEncoding.IsSupportedKeyType(type))
            {
                throw new RowFormatException(
                    $"Field '{schema[field].Name}' of type {type.SimpleString} is not a supported byte-sortable sort key.");
            }

            _keyFields[k] = field;
            _orderings[k] = orderings[k];
            _keyTypes[k] = type;
        }
    }

    /// <summary>The number of fields in the sort key.</summary>
    public int KeyFieldCount => _keyFields.Length;

    /// <summary>
    /// An upper bound on the encoded key length for <paramref name="row"/>, suitable for sizing a
    /// stack or pooled buffer before calling <see cref="Encode(RowData, System.Span{byte})"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="row"/>'s schema does not match this encoder's schema.</exception>
    public int GetMaxEncodedLength(RowData row)
    {
        ArgumentNullException.ThrowIfNull(row);
        EnsureSchema(row);

        int total = 0;
        for (int k = 0; k < _keyFields.Length; k++)
        {
            total++; // null marker
            object? value = row[_keyFields[k]];
            if (value is not null)
            {
                total += ByteSortableEncoding.MaxValueLength(_keyTypes[k], value);
            }
        }

        return total;
    }

    /// <summary>
    /// Writes the order-preserving sort key for <paramref name="row"/> into
    /// <paramref name="destination"/> and returns the number of bytes written. The destination must
    /// be at least <see cref="GetMaxEncodedLength(RowData)"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="row"/>'s schema does not match, or <paramref name="destination"/> is too small.</exception>
    /// <exception cref="RowFormatException">A value's CLR type does not match its field type.</exception>
    public int Encode(RowData row, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(row);
        EnsureSchema(row);

        int pos = 0;
        for (int k = 0; k < _keyFields.Length; k++)
        {
            SortKeyOrdering ordering = _orderings[k];
            object? value = row[_keyFields[k]];
            if (value is null)
            {
                if (pos >= destination.Length)
                {
                    throw DestinationTooSmall(nameof(destination));
                }

                destination[pos++] = ordering.NullsFirst ? NullMarkerFirst : NullMarkerLast;
                continue;
            }

            if (pos >= destination.Length)
            {
                throw DestinationTooSmall(nameof(destination));
            }

            destination[pos++] = PresentMarker;
            pos += ByteSortableEncoding.EncodeValue(_keyTypes[k], value, destination[pos..], ordering.IsDescending);
        }

        return pos;
    }

    /// <summary>
    /// Convenience overload that allocates and returns a right-sized key array. Prefer
    /// <see cref="Encode(RowData, System.Span{byte})"/> with a reused buffer on hot paths.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    public byte[] Encode(RowData row)
    {
        ArgumentNullException.ThrowIfNull(row);
        byte[] buffer = new byte[GetMaxEncodedLength(row)];
        int written = Encode(row, buffer);
        if (written == buffer.Length)
        {
            return buffer;
        }

        byte[] exact = new byte[written];
        Array.Copy(buffer, exact, written);
        return exact;
    }

    private void EnsureSchema(RowData row)
    {
        if (!_schema.Equals(row.Schema))
        {
            throw new ArgumentException(
                $"Row schema {row.Schema.SimpleString} does not match the key encoder's schema {_schema.SimpleString}.",
                nameof(row));
        }
    }

    private static ArgumentException DestinationTooSmall(string paramName) =>
        new("Destination span is smaller than GetMaxEncodedLength for this row.", paramName);
}
