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
    /// <summary>Materializes every logical row of every batch into a <see cref="Row"/>, honoring result bounds.</summary>
    /// <param name="result">The executed schema + batches.</param>
    /// <param name="maxRows">The maximum rows to materialize, or <see langword="null"/> for unbounded.</param>
    /// <param name="maxBytes">The maximum estimated bytes to materialize, or <see langword="null"/> for unbounded.</param>
    /// <param name="cancellationToken">The effective cancellation token (user cancel linked with any timeout).</param>
    /// <returns>All rows, in batch-then-row order.</returns>
    /// <exception cref="ResultLimitExceededException">A configured row/byte bound would be exceeded (checked
    /// <b>before</b> the offending batch is materialized — bounded, not OOM).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<Row> Materialize(
        BatchResult result, long? maxRows, long? maxBytes, CancellationToken cancellationToken)
    {
        StructType schema = result.Schema;
        var rows = new List<Row>();
        long rowsSoFar = 0;
        long bytesSoFar = 0;
        foreach (ColumnBatch batch in result.Batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int rowCount = batch.LogicalRowCount;
            int columnCount = schema.Count;
            var columns = new ColumnVector[columnCount];
            for (int c = 0; c < columnCount; c++)
            {
                columns[c] = batch.SelectedColumn(c);
            }

            // Enforce the result bounds BEFORE materializing this batch's rows, so the row list is never
            // grown past the bound: a deterministic fail-fast rather than an OOM (criterion 3).
            if (maxRows is { } rowCap && rowsSoFar + rowCount > rowCap)
            {
                throw ResultLimitExceededException.Rows(rowCap, rowsSoFar + rowCount);
            }

            if (maxBytes is { } byteCap)
            {
                long batchBytes = EstimateBatchBytes(columns, schema, rowCount);
                if (bytesSoFar + batchBytes > byteCap)
                {
                    throw ResultLimitExceededException.Bytes(byteCap, bytesSoFar + batchBytes);
                }

                bytesSoFar += batchBytes;
            }

            for (int r = 0; r < rowCount; r++)
            {
                if ((r & CancellationPollMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var values = new object?[columnCount];
                for (int c = 0; c < columnCount; c++)
                {
                    values[c] = columns[c].IsNull(r) ? null : ReadValue(columns[c], schema[c], r);
                }

                rows.Add(new Row(schema, values));
            }

            rowsSoFar += rowCount;
        }

        return rows;
    }

    /// <summary>Sums the logical row counts across the result's batches without materializing values.</summary>
    /// <param name="result">The executed schema + batches.</param>
    /// <param name="cancellationToken">The effective cancellation token, polled per batch so a
    /// cancel/timeout stops a long count promptly (criterion 1).</param>
    /// <returns>The total logical row count.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static long CountRows(BatchResult result, CancellationToken cancellationToken = default)
    {
        long count = 0;
        foreach (ColumnBatch batch in result.Batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count += batch.LogicalRowCount;
        }

        return count;
    }

    private static object ReadValue(ColumnVector column, StructField field, int index)
    {
        DataType type = field.DataType;
        return type switch
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
            DateType => ReadDate(column, field, index),
            TimestampType => ReadTimestamp(column, field, index),
            StringType => Encoding.UTF8.GetString(column.GetBytes(index)),
            BinaryType => column.GetBytes(index).ToArray(),
            _ => throw new UnsupportedPlanException(
                QueryExecutionStage.Materialize,
                $"Row materialization has no CLR mapping for type '{type.SimpleString}'."),
        };
    }

    // The driver polls the effective cancellation token every 1024 materialized rows (a power-of-two
    // mask keeps the check branch-cheap) so a cancel/timeout stops a large single-batch result promptly.
    private const int CancellationPollMask = 1023;

    // A best-effort estimate of the driver bytes materializing this batch would hold. It sums the columnar
    // value bytes (fixed-width: rows × width; variable-width: sum of non-null value lengths, mirroring the
    // Engine scan's EstimateBatchBytes) PLUS a conservative per-row/per-value CLR object-overhead term:
    // Materialize builds a List<Row> of BOXED CLR values (each value a heap object with a header, each Row
    // an object plus an object?[] array), which the coarse columnar figure alone under-counts by roughly an
    // order of magnitude. This is NOT an exact driver-heap figure (see MaxResultBytes doc / #176 #9) — it
    // is a deliberately pessimistic safety proxy the result byte-bound (criterion 3) is enforced against so
    // the cap trips before, not after, an over-optimistic estimate would have let the heap balloon.
    private static long EstimateBatchBytes(ColumnVector[] columns, StructType schema, int rowCount)
    {
        long bytes = 0;
        for (int c = 0; c < columns.Length; c++)
        {
            ColumnVector column = columns[c];
            DataType type = schema[c].DataType;
            if (type is StringType or BinaryType)
            {
                for (int r = 0; r < rowCount; r++)
                {
                    if (!column.IsNull(r))
                    {
                        bytes += column.GetBytes(r).Length;
                    }
                }
            }
            else
            {
                bytes += (long)rowCount * FixedWidthBytes(type);
            }
        }

        // Add the boxed-value / Row-object overhead the columnar figure omits (per value: a boxed heap
        // object + its object?[] slot; per row: the Row object + its array). Conservative on a 64-bit CLR.
        bytes += (long)rowCount * columns.Length * PerValueObjectOverheadBytes;
        bytes += (long)rowCount * PerRowObjectOverheadBytes;
        return bytes;
    }

    // Conservative 64-bit CLR overhead for a boxed value (object header + padding + the enclosing
    // object?[] reference slot) and for a materialized Row (the Row object + its object?[] array header).
    // These make the byte estimate pessimistic rather than wildly optimistic (#176 #9); they are a safety
    // proxy, not an exact driver-heap measurement.
    private const long PerValueObjectOverheadBytes = 32;
    private const long PerRowObjectOverheadBytes = 48;

    private static int FixedWidthBytes(DataType type) => type switch
    {
        BooleanType or ByteType => 1,
        ShortType => 2,
        IntegerType or FloatType or DateType => 4,
        LongType or DoubleType or TimestampType => 8,
        DecimalType decimalType => decimalType.IsCompact ? 8 : 16,
        _ => 8,
    };

    // A DateType lane stores the Spark epoch-day (days since 1970-01-01) as an int; surface it as the
    // CLR DateOnly that lit(DateOnly) round-trips (Functions.DateLiteral uses the same epoch), so
    // Collect()/GetAs<DateOnly> and Show render a calendar date rather than the raw epoch number.
    // An epoch-day whose date falls outside DateOnly's representable range (0001-01-01..9999-12-31) is a
    // deterministic UnsupportedPlanException (mirrors the timestamp/decimal paths) rather than a raw
    // ArgumentOutOfRangeException leaked from DateOnly.AddDays.
    private static DateOnly ReadDate(ColumnVector column, StructField field, int index)
    {
        int epochDay = column.GetValue<int>(index);
        try
        {
            return UnixEpochDate.AddDays(epochDay);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw OutOfRangeDate(field);
        }
    }

    // The out-of-range message names the offending column and type but deliberately does NOT embed the raw
    // cell value (the epoch-day), which would leak row-level data into logs/diagnostics (#176 #8).
    private static UnsupportedPlanException OutOfRangeDate(StructField field) =>
        new(QueryExecutionStage.Materialize,
            $"Row materialization cannot surface column '{field.Name}' of type "
            + $"'{field.DataType.SimpleString}' as System.DateOnly: the date falls outside the "
            + "representable DateOnly range.");

    // A TimestampType lane stores the Spark epoch-microsecond instant as a long; surface it as a UTC
    // DateTime — the inverse of lit(DateTime)/lit(DateTimeOffset), which normalize to epoch-micros —
    // so Collect()/GetAs<DateTime> round-trips and Show renders an instant, not the raw epoch number.
    // A micros value whose ticks overflow long, or whose instant falls outside DateTime's range, is a
    // deterministic UnsupportedPlanException (mirrors the decimal path) rather than a raw
    // ArgumentOutOfRangeException or a silent mis-decode.
    private static DateTime ReadTimestamp(ColumnVector column, StructField field, int index)
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
            throw OutOfRangeTimestamp(field);
        }

        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            throw OutOfRangeTimestamp(field);
        }

        return new DateTime(ticks, DateTimeKind.Utc);
    }

    // Names the offending column and type but deliberately does NOT embed the raw epoch-microsecond cell
    // value, which would leak row-level data into logs/diagnostics (#176 #8).
    private static UnsupportedPlanException OutOfRangeTimestamp(StructField field) =>
        new(QueryExecutionStage.Materialize,
            $"Row materialization cannot surface column '{field.Name}' of type "
            + $"'{field.DataType.SimpleString}' as System.DateTime: the instant falls outside the "
            + "representable DateTime range.");

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
                QueryExecutionStage.Materialize,
                $"Row materialization cannot surface '{type.SimpleString}' as System.Decimal: scale "
                + $"{type.Scale} exceeds the System.Decimal maximum of {MaxDecimalScale}.");
        }

        Int128 unscaled = type.IsCompact ? column.GetValue<long>(index) : column.GetValue<Int128>(index);
        bool isNegative = unscaled < 0;
        UInt128 magnitude = isNegative ? (UInt128)(-unscaled) : (UInt128)unscaled;
        if (magnitude > MaxDecimalMagnitude)
        {
            throw new UnsupportedPlanException(
                QueryExecutionStage.Materialize,
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
