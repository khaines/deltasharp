using System.Collections.Immutable;
using System.Globalization;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Builds a Delta <see cref="FileStatistics"/> from the in-memory <see cref="ColumnBatch"/>es a
/// <see cref="ParquetFileWriter"/> is about to write (STORY-05.6.3 AC1; design §2.9.2 "writers collect
/// count/size/min/max/null while in memory"). It records the file's record count and, for each
/// indexed, statistic-eligible top-level column, the <c>minValues</c>/<c>maxValues</c>/<c>nullCount</c>,
/// applying the <see cref="StatisticsPolicy"/> omit/bound rules (AC2).
///
/// <para>Statistics are <b>advisory</b> (design §2.10.5): any value that cannot be represented as an
/// exact, JSON-encodable bound is omitted rather than approximated, so the pruner can only ever forfeit
/// a skip — never drop a matching row. The collected values are never logged (they are literal cell
/// data; AC2 privacy rule).</para>
/// </summary>
internal static class ParquetStatisticsCollector
{
    /// <summary>
    /// Collects the write-time <see cref="FileStatistics"/> for <paramref name="batches"/> (each
    /// conforming to <paramref name="schema"/>) under <paramref name="policy"/>. The batches must be the
    /// exact rows the writer emits; selection vectors are applied identically to the writer so the
    /// statistics describe the written file.
    /// </summary>
    public static FileStatistics Collect(
        StructType schema, IReadOnlyList<ColumnBatch> batches, StatisticsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(policy);

        long numRecords = 0;
        foreach (ColumnBatch batch in batches)
        {
            numRecords += batch.LogicalRowCount;
        }

        var minValues = ImmutableSortedDictionary.CreateBuilder<string, DeltaStatValue>(StringComparer.Ordinal);
        var maxValues = ImmutableSortedDictionary.CreateBuilder<string, DeltaStatValue>(StringComparer.Ordinal);
        var nullCounts = ImmutableSortedDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
        bool anyTruncated = false;

        for (int c = 0; c < schema.Count; c++)
        {
            StructField field = schema[c];

            // Rule 1 (indexing horizon) + Rule 2 (type eligibility): a column past the horizon or of a
            // non-eligible type is omitted entirely — no min/max/nullCount.
            if (!policy.IsIndexedPosition(c) || !policy.IsSupportedForStatistics(field.DataType))
            {
                continue;
            }

            ColumnStat stat = CollectColumn(field, batches, c, policy);
            anyTruncated |= stat.Truncated;
            nullCounts[field.Name] = stat.NullCount;
            if (stat.Min is not null)
            {
                minValues[field.Name] = stat.Min;
            }

            if (stat.Max is not null)
            {
                maxValues[field.Name] = stat.Max;
            }
        }

        // tightBounds is false only when a bound was truncated (its prefix is not an exact bound); an
        // exact-bound file is tight. A non-tight file disables min/max range pruning (FilePruner honors it).
        return new FileStatistics(numRecords, minValues.ToImmutable(), maxValues.ToImmutable(),
            nullCounts.ToImmutable(), TightBounds: !anyTruncated);
    }

    private static ColumnStat CollectColumn(
        StructField field, IReadOnlyList<ColumnBatch> batches, int columnIndex, StatisticsPolicy policy) =>
        field.DataType switch
        {
            BooleanType => CollectBoolean(batches, columnIndex),
            ByteType => CollectInteger(batches, columnIndex,
                static (vector, row) => unchecked((sbyte)vector.GetValue<byte>(row))),
            ShortType => CollectInteger(batches, columnIndex, static (vector, row) => vector.GetValue<short>(row)),
            IntegerType => CollectInteger(batches, columnIndex, static (vector, row) => vector.GetValue<int>(row)),
            LongType => CollectInteger(batches, columnIndex, static (vector, row) => vector.GetValue<long>(row)),
            DateType => CollectInteger(batches, columnIndex, static (vector, row) => vector.GetValue<int>(row)),
            TimestampType => CollectInteger(batches, columnIndex, static (vector, row) => vector.GetValue<long>(row)),
            FloatType => CollectDouble(batches, columnIndex, static (vector, row) => vector.GetValue<float>(row)),
            DoubleType => CollectDouble(batches, columnIndex, static (vector, row) => vector.GetValue<double>(row)),
            DecimalType decimalType => CollectDecimal(batches, columnIndex, decimalType),
            StringType => CollectString(batches, columnIndex, policy.StringTruncationLength),
            _ => new ColumnStat(null, null, NullCount: 0, Truncated: false),
        };

    private static ColumnStat CollectInteger(
        IReadOnlyList<ColumnBatch> batches, int columnIndex, Func<ColumnVector, int, long> read)
    {
        long nulls = 0;
        bool any = false;
        long min = 0;
        long max = 0;
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector vector = batch.SelectedColumn(columnIndex);
            int rows = batch.LogicalRowCount;
            for (int r = 0; r < rows; r++)
            {
                if (vector.IsNull(r))
                {
                    nulls++;
                    continue;
                }

                long value = read(vector, r);
                if (!any)
                {
                    min = value;
                    max = value;
                    any = true;
                }
                else if (value < min)
                {
                    min = value;
                }
                else if (value > max)
                {
                    max = value;
                }
            }
        }

        return any
            ? new ColumnStat(DeltaStatValue.OfLong(min), DeltaStatValue.OfLong(max), nulls, Truncated: false)
            : new ColumnStat(null, null, nulls, Truncated: false);
    }

    private static ColumnStat CollectDouble(
        IReadOnlyList<ColumnBatch> batches, int columnIndex, Func<ColumnVector, int, double> read)
    {
        long nulls = 0;
        bool any = false;
        double min = 0;
        double max = 0;
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector vector = batch.SelectedColumn(columnIndex);
            int rows = batch.LogicalRowCount;
            for (int r = 0; r < rows; r++)
            {
                if (vector.IsNull(r))
                {
                    nulls++;
                    continue;
                }

                double value = read(vector, r);

                // NaN never satisfies an ordered/equality comparison, so excluding it from the bounds
                // keeps pruning sound while avoiding a NaN-poisoned (unusable) bound. -0.0 canonicalizes
                // to +0.0 so the encoded bound is stable.
                if (double.IsNaN(value))
                {
                    continue;
                }

                if (value == 0.0)
                {
                    value = 0.0;
                }

                if (!any)
                {
                    min = value;
                    max = value;
                    any = true;
                }
                else
                {
                    if (value < min)
                    {
                        min = value;
                    }

                    if (value > max)
                    {
                        max = value;
                    }
                }
            }
        }

        // A ±Infinity bound has no JSON-number encoding, so omit it (forfeits that bound, stays sound).
        DeltaStatValue? minValue = any && double.IsFinite(min) ? DeltaStatValue.OfDouble(min) : null;
        DeltaStatValue? maxValue = any && double.IsFinite(max) ? DeltaStatValue.OfDouble(max) : null;
        return new ColumnStat(minValue, maxValue, nulls, Truncated: false);
    }

    private static ColumnStat CollectBoolean(IReadOnlyList<ColumnBatch> batches, int columnIndex)
    {
        long nulls = 0;
        bool any = false;
        bool sawFalse = false;
        bool sawTrue = false;
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector vector = batch.SelectedColumn(columnIndex);
            int rows = batch.LogicalRowCount;
            for (int r = 0; r < rows; r++)
            {
                if (vector.IsNull(r))
                {
                    nulls++;
                    continue;
                }

                any = true;
                if (vector.GetValue<bool>(r))
                {
                    sawTrue = true;
                }
                else
                {
                    sawFalse = true;
                }
            }
        }

        if (!any)
        {
            return new ColumnStat(null, null, nulls, Truncated: false);
        }

        // false sorts below true.
        DeltaStatValue min = DeltaStatValue.OfBoolean(!sawFalse && sawTrue);
        DeltaStatValue max = DeltaStatValue.OfBoolean(sawTrue);
        return new ColumnStat(min, max, nulls, Truncated: false);
    }

    private static ColumnStat CollectDecimal(
        IReadOnlyList<ColumnBatch> batches, int columnIndex, DecimalType decimalType)
    {
        long nulls = 0;
        bool any = false;
        decimal min = 0m;
        decimal max = 0m;
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector vector = batch.SelectedColumn(columnIndex);
            int rows = batch.LogicalRowCount;
            for (int r = 0; r < rows; r++)
            {
                if (vector.IsNull(r))
                {
                    nulls++;
                    continue;
                }

                decimal value = ParquetTypeMapping.ReadDecimal(vector, decimalType, r);
                if (!any)
                {
                    min = value;
                    max = value;
                    any = true;
                }
                else if (value < min)
                {
                    min = value;
                }
                else if (value > max)
                {
                    max = value;
                }
            }
        }

        if (!any)
        {
            return new ColumnStat(null, null, nulls, Truncated: false);
        }

        // Delta encodes a decimal bound as its literal numeric text (e.g. "123.45"); it is stored as a
        // String stat and therefore never used for range pruning (matches the pruner's string handling).
        DeltaStatValue minValue = DeltaStatValue.OfString(min.ToString(CultureInfo.InvariantCulture));
        DeltaStatValue maxValue = DeltaStatValue.OfString(max.ToString(CultureInfo.InvariantCulture));
        return new ColumnStat(minValue, maxValue, nulls, Truncated: false);
    }

    private static ColumnStat CollectString(
        IReadOnlyList<ColumnBatch> batches, int columnIndex, int truncationLength)
    {
        long nulls = 0;
        byte[]? min = null;
        byte[]? max = null;
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector vector = batch.SelectedColumn(columnIndex);
            int rows = batch.LogicalRowCount;
            for (int r = 0; r < rows; r++)
            {
                if (vector.IsNull(r))
                {
                    nulls++;
                    continue;
                }

                ReadOnlySpan<byte> value = vector.GetBytes(r);
                if (min is null || value.SequenceCompareTo(min) < 0)
                {
                    min = value.ToArray();
                }

                if (max is null || value.SequenceCompareTo(max) > 0)
                {
                    max = value.ToArray();
                }
            }
        }

        if (min is null || max is null)
        {
            return new ColumnStat(null, null, nulls, Truncated: false);
        }

        (string minText, bool minTruncated) = TruncateUtf8(min, truncationLength);
        (string maxText, bool maxTruncated) = TruncateUtf8(max, truncationLength);
        return new ColumnStat(
            DeltaStatValue.OfString(minText),
            DeltaStatValue.OfString(maxText),
            nulls,
            Truncated: minTruncated || maxTruncated);
    }

    // Decode UTF-8 bytes to a string and prefix-truncate to at most `maxChars` Unicode characters without
    // splitting a surrogate pair. Returns whether truncation removed any character.
    private static (string Text, bool Truncated) TruncateUtf8(byte[] utf8, int maxChars)
    {
        string text = System.Text.Encoding.UTF8.GetString(utf8);
        if (text.Length <= maxChars)
        {
            return (text, false);
        }

        int cut = maxChars;

        // Do not split a surrogate pair straddling the cut point: pull the cut back before the high half.
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return (text[..cut], true);
    }

    private readonly record struct ColumnStat(
        DeltaStatValue? Min, DeltaStatValue? Max, long NullCount, bool Truncated);
}
