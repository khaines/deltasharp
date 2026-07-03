using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// Materializes an execution <see cref="BatchResult"/> into Core <see cref="Row"/>s, converting each
/// EPIC-03 <see cref="ColumnVector"/> lane to the natural CLR value for its logical
/// <see cref="DataType"/> (ADR-0002 column format → Spark-compatible <see cref="Row"/> values),
/// null-aware. It reads through <see cref="ColumnBatch.SelectedColumn"/> so any selection vector a
/// filter/limit left on a batch is honored.
/// </summary>
internal static class RowMaterializer
{
    /// <summary>Materializes every logical row of every batch into a <see cref="Row"/>.</summary>
    /// <param name="result">The executed schema + batches.</param>
    /// <returns>All rows, in batch-then-row order.</returns>
    public static IReadOnlyList<Row> Materialize(BatchResult result)
    {
        StructType schema = result.Schema;
        var rows = new List<Row>();
        foreach (ColumnBatch batch in result.Batches)
        {
            int rowCount = batch.LogicalRowCount;
            int columnCount = schema.Count;
            var columns = new ColumnVector[columnCount];
            for (int c = 0; c < columnCount; c++)
            {
                columns[c] = batch.SelectedColumn(c);
            }

            for (int r = 0; r < rowCount; r++)
            {
                var values = new object?[columnCount];
                for (int c = 0; c < columnCount; c++)
                {
                    values[c] = columns[c].IsNull(r) ? null : ReadValue(columns[c], schema[c].DataType, r);
                }

                rows.Add(new Row(schema, values));
            }
        }

        return rows;
    }

    /// <summary>Sums the logical row counts across the result's batches without materializing values.</summary>
    /// <param name="result">The executed schema + batches.</param>
    /// <returns>The total logical row count.</returns>
    public static long CountRows(BatchResult result)
    {
        long count = 0;
        foreach (ColumnBatch batch in result.Batches)
        {
            count += batch.LogicalRowCount;
        }

        return count;
    }

    private static object ReadValue(ColumnVector column, DataType type, int index) => type switch
    {
        BooleanType => column.GetValue<bool>(index),

        // Spark ByteType is a signed tinyint; the Engine stores it as an unsigned byte lane.
        ByteType => unchecked((sbyte)column.GetValue<byte>(index)),
        ShortType => column.GetValue<short>(index),
        IntegerType => column.GetValue<int>(index),
        LongType => column.GetValue<long>(index),
        FloatType => column.GetValue<float>(index),
        DoubleType => column.GetValue<double>(index),
        DecimalType decimalType => ReadDecimal(column, decimalType, index),
        DateType => ReadDate(column, index),
        TimestampType => ReadTimestamp(column, index),
        StringType => Encoding.UTF8.GetString(column.GetBytes(index)),
        BinaryType => column.GetBytes(index).ToArray(),
        _ => throw new UnsupportedPlanException(
            $"Row materialization has no CLR mapping for type '{type.SimpleString}'."),
    };

    // A DateType lane stores the Spark epoch-day (days since 1970-01-01) as an int; surface it as the
    // CLR DateOnly that lit(DateOnly) round-trips (Functions.DateLiteral uses the same epoch), so
    // Collect()/GetAs<DateOnly> and Show render a calendar date rather than the raw epoch number.
    // An epoch-day whose date falls outside DateOnly's representable range (0001-01-01..9999-12-31) is a
    // deterministic UnsupportedPlanException (mirrors the timestamp/decimal paths) rather than a raw
    // ArgumentOutOfRangeException leaked from DateOnly.AddDays.
    private static DateOnly ReadDate(ColumnVector column, int index)
    {
        int epochDay = column.GetValue<int>(index);
        try
        {
            return UnixEpochDate.AddDays(epochDay);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw OutOfRangeDate(epochDay);
        }
    }

    private static UnsupportedPlanException OutOfRangeDate(int epochDay) =>
        new($"Row materialization cannot surface the date epoch-day value {epochDay} as "
            + "System.DateOnly: the date falls outside the representable DateOnly range.");

    // A TimestampType lane stores the Spark epoch-microsecond instant as a long; surface it as a UTC
    // DateTime — the inverse of lit(DateTime)/lit(DateTimeOffset), which normalize to epoch-micros —
    // so Collect()/GetAs<DateTime> round-trips and Show renders an instant, not the raw epoch number.
    // A micros value whose ticks overflow long, or whose instant falls outside DateTime's range, is a
    // deterministic UnsupportedPlanException (mirrors the decimal path) rather than a raw
    // ArgumentOutOfRangeException or a silent mis-decode.
    private static DateTime ReadTimestamp(ColumnVector column, int index)
    {
        long micros = column.GetValue<long>(index);
        long ticks;
        try
        {
            ticks = checked(micros * TimeSpan.TicksPerMicrosecond);
            ticks = checked(DateTime.UnixEpoch.Ticks + ticks);
        }
        catch (OverflowException)
        {
            throw OutOfRangeTimestamp(micros);
        }

        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            throw OutOfRangeTimestamp(micros);
        }

        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static UnsupportedPlanException OutOfRangeTimestamp(long micros) =>
        new($"Row materialization cannot surface the timestamp epoch-microsecond value {micros} as "
            + "System.DateTime: the instant falls outside the representable DateTime range.");

    private static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    private static decimal ReadDecimal(ColumnVector column, DecimalType type, int index)
    {
        // System.Decimal is a sign + 96-bit magnitude + scale in [0, 28]. Reconstruct it directly from
        // the unscaled integer preserving the declared scale (so decimal(5,2) 100.00 keeps scale 2 and
        // renders "100.00", instead of dividing by 10^scale — which both overflows for wide values
        // representable at their scale and normalizes trailing zeros away). A genuinely unrepresentable
        // value (scale > 28, or a magnitude wider than 96 bits) is a deterministic UnsupportedPlanException.
        if (type.Scale > MaxDecimalScale)
        {
            throw new UnsupportedPlanException(
                $"Row materialization cannot surface '{type.SimpleString}' as System.Decimal: scale "
                + $"{type.Scale} exceeds the System.Decimal maximum of {MaxDecimalScale}.");
        }

        Int128 unscaled = type.IsCompact ? column.GetValue<long>(index) : column.GetValue<Int128>(index);
        bool isNegative = unscaled < 0;
        UInt128 magnitude = isNegative ? (UInt128)(-unscaled) : (UInt128)unscaled;
        if (magnitude > MaxDecimalMagnitude)
        {
            throw new UnsupportedPlanException(
                $"Row materialization cannot surface a '{type.SimpleString}' value as System.Decimal: "
                + "its unscaled magnitude exceeds the 96-bit System.Decimal range.");
        }

        int lo = unchecked((int)(uint)magnitude);
        int mid = unchecked((int)(uint)(magnitude >> 32));
        int hi = unchecked((int)(uint)(magnitude >> 64));
        return new decimal(lo, mid, hi, isNegative, (byte)type.Scale);
    }

    // System.Decimal supports at most 28 fractional digits and a 96-bit magnitude.
    private const int MaxDecimalScale = 28;
    private static readonly UInt128 MaxDecimalMagnitude = UInt128.MaxValue >> 32;
}
