using System.Collections.Generic;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// Encodes the in-memory <see cref="Row"/> sequence a Core <c>LocalRelation</c> (the STORY-04.1.2 /
/// #158 read-door for local data) carries into EPIC-03 <see cref="ColumnBatch"/>es, so the physical
/// planner can plan a scan over it without the <see cref="IScanSource"/> catalog seam. It is the exact
/// inverse of <see cref="RowMaterializer"/>: it reads each row's natural CLR value positionally and
/// writes it onto the typed <see cref="MutableColumnVector"/> lane for the column's ADR-0008
/// <see cref="DataType"/> (null-aware). Any row whose shape or a value's CLR type does not match the
/// schema — or a value that cannot be encoded (overflow, unrepresentable date/timestamp/decimal) —
/// raises the deterministic <see cref="UnsupportedPlanException"/> rather than a raw failure.
/// </summary>
internal static class LocalRelationBatches
{
    /// <summary>Encodes <paramref name="data"/> into a single columnar batch conforming to
    /// <paramref name="schema"/>. Enumerates <paramref name="data"/> exactly once (this is the point at
    /// which a lazily-created DataFrame's rows are finally read).</summary>
    /// <param name="schema">The authoritative relation schema.</param>
    /// <param name="data">The in-memory rows, read positionally against <paramref name="schema"/>.</param>
    /// <returns>A single-element list holding the encoded batch (a zero-row batch when empty).</returns>
    /// <exception cref="UnsupportedPlanException">A row's arity or a value's CLR type does not match the
    /// schema, or a value cannot be encoded onto its lane.</exception>
    public static IReadOnlyList<ColumnBatch> Build(StructType schema, IEnumerable<Row> data)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(data);

        var rows = new List<Row>();
        foreach (Row row in data)
        {
            if (row is null)
            {
                throw new UnsupportedPlanException(
                    "A LocalRelation row is null; every row supplied to CreateDataFrame must be a Row.");
            }

            if (row.Length != schema.Count)
            {
                throw new UnsupportedPlanException(
                    $"A LocalRelation row has {row.Length} value(s) but the schema declares "
                    + $"{schema.Count} column(s); every row must match the schema arity.");
            }

            rows.Add(row);
        }

        int rowCount = rows.Count;
        int columnCount = schema.Count;
        var columns = new ColumnVector[columnCount];
        for (int c = 0; c < columnCount; c++)
        {
            StructField field = schema[c];
            MutableColumnVector vector = ColumnVectors.Create(field.DataType, Math.Max(rowCount, 1));
            for (int r = 0; r < rowCount; r++)
            {
                object? value = rows[r][c];
                if (value is null)
                {
                    vector.AppendNull();
                }
                else
                {
                    Append(vector, field, value);
                }
            }

            columns[c] = vector;
        }

        return new ColumnBatch[] { new ManagedColumnBatch(schema, columns, rowCount) };
    }

    private static void Append(MutableColumnVector vector, StructField field, object value)
    {
        switch (field.DataType)
        {
            case BooleanType:
                vector.AppendValue(Expect<bool>(field, value));
                break;

            // Spark ByteType is a signed tinyint; the Engine stores it on an unsigned byte lane.
            case ByteType:
                vector.AppendValue(unchecked((byte)Expect<sbyte>(field, value)));
                break;

            case ShortType:
                vector.AppendValue(Expect<short>(field, value));
                break;

            case IntegerType:
                vector.AppendValue(Expect<int>(field, value));
                break;

            case LongType:
                vector.AppendValue(Expect<long>(field, value));
                break;

            case FloatType:
                vector.AppendValue(Expect<float>(field, value));
                break;

            case DoubleType:
                vector.AppendValue(Expect<double>(field, value));
                break;

            case StringType:
                vector.AppendBytes(Encoding.UTF8.GetBytes(Expect<string>(field, value)));
                break;

            case BinaryType:
                vector.AppendBytes(Expect<byte[]>(field, value));
                break;

            case DateType:
                vector.AppendValue(EncodeDate(field, Expect<DateOnly>(field, value)));
                break;

            case TimestampType:
                vector.AppendValue(EncodeTimestamp(Expect<DateTime>(field, value)));
                break;

            case DecimalType decimalType:
                AppendDecimal(vector, field, decimalType, Expect<decimal>(field, value));
                break;

            default:
                throw new UnsupportedPlanException(
                    $"CreateDataFrame has no CLR encoding for column '{field.Name}' of type "
                    + $"'{field.DataType.SimpleString}'.");
        }
    }

    private static T Expect<T>(StructField field, object value)
    {
        if (value is T typed)
        {
            return typed;
        }

        throw new UnsupportedPlanException(
            $"Column '{field.Name}' is '{field.DataType.SimpleString}', which expects a "
            + $"{typeof(T).Name} value, but a row supplied a {value.GetType().Name}.");
    }

    // A DateType lane stores the Spark epoch-day (days since 1970-01-01) as an int — the exact inverse
    // of RowMaterializer.ReadDate. DateOnly's whole representable range fits comfortably in an int.
    private static int EncodeDate(StructField field, DateOnly value) =>
        value.DayNumber - UnixEpochDate.DayNumber;

    // A TimestampType lane stores the Spark epoch-microsecond instant as a long — the inverse of
    // RowMaterializer.ReadTimestamp (which yields a UTC DateTime). A Local DateTime is converted to UTC;
    // a Utc/Unspecified DateTime is treated as the instant. Sub-microsecond ticks are truncated
    // (Spark timestamps carry microsecond precision).
    private static long EncodeTimestamp(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        long ticksFromEpoch = utc.Ticks - DateTime.UnixEpoch.Ticks;
        return ticksFromEpoch / TimeSpan.TicksPerMicrosecond;
    }

    private static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    private static void AppendDecimal(
        MutableColumnVector vector, StructField field, DecimalType type, decimal value)
    {
        // Encode value as an unscaled integer at the column's declared scale — the inverse of
        // RowMaterializer.ReadDecimal (which reconstructs a System.Decimal from the unscaled lane). A
        // System.Decimal carries at most 28 fractional digits, so a scale beyond that is unrepresentable.
        if (type.Scale > MaxDecimalScale)
        {
            throw new UnsupportedPlanException(
                $"Column '{field.Name}' is '{type.SimpleString}': scale {type.Scale} exceeds the "
                + $"System.Decimal maximum of {MaxDecimalScale}, so a decimal value cannot be encoded.");
        }

        decimal scaled;
        try
        {
            scaled = value * Pow10Decimal(type.Scale);
        }
        catch (OverflowException)
        {
            throw DecimalOutOfRange(field, type, value);
        }

        if (scaled != decimal.Truncate(scaled))
        {
            throw new UnsupportedPlanException(
                $"Decimal value {value} for column '{field.Name}' cannot be represented at scale "
                + $"{type.Scale} without loss of precision.");
        }

        Int128 unscaled;
        try
        {
            unscaled = Int128.CreateChecked(scaled);
        }
        catch (OverflowException)
        {
            throw DecimalOutOfRange(field, type, value);
        }

        Int128 magnitude = unscaled < 0 ? -unscaled : unscaled;
        if (magnitude >= Pow10Int128(type.Precision))
        {
            throw new UnsupportedPlanException(
                $"Decimal value {value} for column '{field.Name}' does not fit in precision "
                + $"{type.Precision} (type '{type.SimpleString}').");
        }

        if (type.IsCompact)
        {
            vector.AppendValue((long)unscaled);
        }
        else
        {
            vector.AppendValue(unscaled);
        }
    }

    private static UnsupportedPlanException DecimalOutOfRange(
        StructField field, DecimalType type, decimal value) =>
        new($"Decimal value {value} for column '{field.Name}' is out of range for type "
            + $"'{type.SimpleString}'.");

    private static decimal Pow10Decimal(int exponent)
    {
        decimal result = 1m;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }

    private static Int128 Pow10Int128(int exponent)
    {
        Int128 result = Int128.One;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }

    // System.Decimal supports at most 28 fractional digits (mirrors RowMaterializer.MaxDecimalScale).
    private const int MaxDecimalScale = 28;
}
