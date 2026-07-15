using System.Text;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.RowFormat;

/// <summary>
/// The scalar comparator oracle for byte-sortable rows (STORY-02.4.2 AC2): it compares two
/// <see cref="RowData"/> rows field-by-field using ordinary typed comparisons and the documented
/// Spark null/NaN/−0.0 rules. It is the correctness reference the byte encoding
/// (<see cref="SortKeyEncoder"/>) must match — a parity test sorts the same rows under a bytewise
/// comparison of the encoding and under this comparator and asserts identical order.
/// </summary>
/// <remarks>
/// Comparison rules, all mirroring Spark:
/// <list type="bullet">
/// <item><description>Nulls sort first or last per each field's <see cref="NullSortOrder"/>,
/// independent of direction.</description></item>
/// <item><description>Non-null values compare by their natural order; the result is negated for a
/// descending field.</description></item>
/// <item><description>Floating point: −0.0 equals +0.0, and NaN equals NaN and sorts greater than
/// every other value (including +∞).</description></item>
/// <item><description>Strings and binary compare lexicographically as unsigned UTF-8/byte sequences,
/// matching the encoder and Spark's <c>UTF8String</c> ordering.</description></item>
/// </list>
/// </remarks>
public sealed class RowOrderingComparer : IComparer<RowData>
{
    private readonly StructType _schema;
    private readonly int[] _keyFields;
    private readonly SortKeyOrdering[] _orderings;
    private readonly DataType[] _keyTypes;

    /// <summary>
    /// Builds a comparator for the <paramref name="keyFields"/> of <paramref name="schema"/>, each
    /// ordered by the matching entry of <paramref name="orderings"/> — the same arguments a
    /// <see cref="SortKeyEncoder"/> takes, so the two stay in lockstep.
    /// </summary>
    /// <param name="schema">The row schema the keys index into.</param>
    /// <param name="keyFields">The field indices to compare on, in priority order.</param>
    /// <param name="orderings">One ordering per key field.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="keyFields"/> is empty or a different length from <paramref name="orderings"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A field index is outside the schema.</exception>
    /// <exception cref="RowFormatException">A key field's type is not byte-sortable.</exception>
    public RowOrderingComparer(StructType schema, IReadOnlyList<int> keyFields, IReadOnlyList<SortKeyOrdering> orderings)
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

    /// <summary>
    /// Compares two rows by the configured sort keys, returning a negative number, zero, or a
    /// positive number when <paramref name="x"/> sorts before, equal to, or after <paramref name="y"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either row is null.</exception>
    /// <exception cref="ArgumentException">A row's schema does not match this comparator's schema.</exception>
    /// <exception cref="RowFormatException">A value's CLR type does not match its field type.</exception>
    public int Compare(RowData? x, RowData? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        EnsureSchema(x);
        EnsureSchema(y);

        for (int k = 0; k < _keyFields.Length; k++)
        {
            int field = _keyFields[k];
            SortKeyOrdering ordering = _orderings[k];
            object? a = x[field];
            object? b = y[field];

            if (a is null || b is null)
            {
                if (a is null && b is null)
                {
                    continue;
                }

                // Null placement is absolute (not flipped by direction). A null that sorts first is
                // "less"; a null that sorts last is "greater".
                int nullIsBefore = ordering.NullsFirst ? -1 : 1;
                return a is null ? nullIsBefore : -nullIsBefore;
            }

            int cmp = CompareValue(_keyTypes[k], a, b);
            if (ordering.IsDescending)
            {
                cmp = -cmp;
            }

            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }

    private static int CompareValue(DataType type, object a, object b) => type switch
    {
        BooleanType => Cast<bool>(a, type).CompareTo(Cast<bool>(b, type)),
        ByteType => Cast<sbyte>(a, type).CompareTo(Cast<sbyte>(b, type)),
        ShortType => Cast<short>(a, type).CompareTo(Cast<short>(b, type)),
        IntegerType or DateType => Cast<int>(a, type).CompareTo(Cast<int>(b, type)),
        LongType or TimestampType or TimestampNtzType => Cast<long>(a, type).CompareTo(Cast<long>(b, type)),
        FloatType => CompareSingle(Cast<float>(a, type), Cast<float>(b, type)),
        DoubleType => CompareDouble(Cast<double>(a, type), Cast<double>(b, type)),
        DecimalType => Cast<Int128>(a, type).CompareTo(Cast<Int128>(b, type)),
        StringType => CompareUtf8(Cast<string>(a, type), Cast<string>(b, type)),
        BinaryType => Cast<byte[]>(a, type).AsSpan().SequenceCompareTo(Cast<byte[]>(b, type)),
        _ => throw new RowFormatException($"Type {type.SimpleString} is not a supported byte-sortable sort key."),
    };

    // Spark's nan-safe ordering: −0.0 == +0.0, NaN == NaN, and NaN is greater than everything else.
    private static int CompareSingle(float a, float b)
    {
        if (a == 0f)
        {
            a = 0f; // collapse −0.0
        }

        if (b == 0f)
        {
            b = 0f;
        }

        bool aNaN = float.IsNaN(a);
        bool bNaN = float.IsNaN(b);
        if (aNaN || bNaN)
        {
            return aNaN && bNaN ? 0 : aNaN ? 1 : -1;
        }

        return a < b ? -1 : a > b ? 1 : 0;
    }

    private static int CompareDouble(double a, double b)
    {
        if (a == 0d)
        {
            a = 0d;
        }

        if (b == 0d)
        {
            b = 0d;
        }

        bool aNaN = double.IsNaN(a);
        bool bNaN = double.IsNaN(b);
        if (aNaN || bNaN)
        {
            return aNaN && bNaN ? 0 : aNaN ? 1 : -1;
        }

        return a < b ? -1 : a > b ? 1 : 0;
    }

    // Compare by UTF-8 bytes so the oracle matches the encoder and Spark's UTF8String order (which
    // can differ from .NET's UTF-16 ordinal order for supplementary characters).
    private static int CompareUtf8(string a, string b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    private void EnsureSchema(RowData row)
    {
        if (!_schema.Equals(row.Schema))
        {
            throw new ArgumentException(
                $"Row schema {row.Schema.SimpleString} does not match the comparator's schema {_schema.SimpleString}.",
                nameof(row));
        }
    }

    private static T Cast<T>(object value, DataType type) =>
        value is T t
            ? t
            : throw new RowFormatException(
                $"Sort key of type {type.SimpleString} expected {typeof(T).Name} but got {value.GetType().Name}.");
}
