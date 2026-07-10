using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The deterministic, documented policy that governs which write-time <c>add.stats</c> a data file
/// carries and how bounded values are truncated (STORY-05.6.3 AC2, design §2.9.2/§2.10.1). It is the
/// single source of truth shared by the write path (<see cref="ParquetStatisticsCollector"/>) and the
/// read/optimizer path (<see cref="SnapshotStatisticsReporter"/>) so both agree on which columns are
/// <i>indexable</i> and which types are statistic-eligible.
///
/// <para><b>The omit / bound rules (AC2).</b> Statistics are <b>advisory</b> — an omitted or bounded
/// statistic forfeits a pruning opportunity, never correctness (design §2.10.5), so every rule below
/// resolves any uncertainty by recording <i>less</i>:</para>
/// <list type="number">
/// <item><b>Indexing horizon.</b> Only the first <see cref="MaxIndexedColumns"/> top-level columns are
/// indexed (Delta <c>delta.dataSkippingNumIndexedCols</c>, default 32); a column past the horizon has no
/// <c>min</c>/<c>max</c>/<c>nullCount</c> recorded.</item>
/// <item><b>Type eligibility.</b> Only the scalar types in <see cref="IsSupportedForStatistics"/> get
/// statistics; nested (struct/array/map), <c>void</c>, binary, and out-of-range decimal columns are
/// written but <b>omitted</b> from statistics entirely.</item>
/// <item><b>String truncation.</b> A string <c>min</c>/<c>max</c> is prefix-truncated to
/// <see cref="StringTruncationLength"/> UTF-16 code units (Delta default 32). A byte-prefix sorts at or
/// before the full value, so a truncated <c>min</c> is a valid (loose) lower bound and is kept; but a
/// truncated <c>max</c> prefix is strictly <i>less</i> than the true max — an invalid upper bound — so a
/// truncated <c>max</c> is <b>omitted entirely</b> (a prefix is never emitted as a <c>max</c>). Any
/// truncation also marks the whole file's bounds as <b>not tight</b> (<c>tightBounds=false</c>), which
/// disables <c>min</c>/<c>max</c> range pruning for the file — matching the pruner's existing rule that
/// string bounds are never used for range skipping (Delta may truncate them). This is deliberately
/// conservative; Delta's per-value <c>min</c>-down/<c>max</c>-up increment (which would restore a valid,
/// tight-ish <c>max</c> and re-enable string skipping) is the deferred optimization tracked in #493.</item>
/// <item><b>All-null / empty columns.</b> A column with no non-null value records its <c>nullCount</c>
/// but omits <c>min</c>/<c>max</c> (there is no representative bound).</item>
/// <item><b>Non-finite floats.</b> Under Spark's float total order <c>NaN</c> is the <i>greatest</i>
/// value (engine <c>KernelScalars.CompareDouble</c>), so a <c>NaN</c>-bearing column's true max is
/// <c>NaN</c> — which has no JSON-number encoding — and the <c>max</c> is <b>omitted</b> (the finite min
/// is unaffected and kept). <c>-0.0</c> is canonicalized to <c>+0.0</c>, and a <c>±Infinity</c>
/// bound is omitted (it has no JSON-number encoding).</item>
/// </list>
///
/// <para><b>Privacy.</b> Statistic values are literal cell values and inherit the data's classification
/// (design §5.1); they are recorded only in the <c>add.stats</c> the log author controls and are
/// <b>never</b> emitted to logs/telemetry (STORY-05.6.3 AC2; Security CRD-I2).</para>
/// </summary>
internal sealed class StatisticsPolicy
{
    /// <summary>The default per-string <c>min</c>/<c>max</c> truncation horizon in UTF-16 code units
    /// (Delta <c>DATA_SKIPPING_STRING_PREFIX_LENGTH</c>).</summary>
    public const int DefaultStringTruncationLength = 32;

    /// <summary>The default number of leading top-level columns that are indexed
    /// (Delta <c>delta.dataSkippingNumIndexedCols</c>).</summary>
    public const int DefaultMaxIndexedColumns = 32;

    /// <summary>The shared default policy (32-character string prefix, 32 indexed columns).</summary>
    public static StatisticsPolicy Default { get; } = new();

    /// <summary>Creates a policy, validating that both horizons are positive.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A horizon is not positive.</exception>
    public StatisticsPolicy(
        int stringTruncationLength = DefaultStringTruncationLength,
        int maxIndexedColumns = DefaultMaxIndexedColumns)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stringTruncationLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIndexedColumns);
        StringTruncationLength = stringTruncationLength;
        MaxIndexedColumns = maxIndexedColumns;
    }

    /// <summary>The maximum number of UTF-16 code units retained in a string <c>min</c>/<c>max</c>.</summary>
    public int StringTruncationLength { get; }

    /// <summary>The number of leading top-level columns for which statistics are collected.</summary>
    public int MaxIndexedColumns { get; }

    /// <summary>Whether the top-level column at <paramref name="columnPosition"/> is within the indexing
    /// horizon.</summary>
    public bool IsIndexedPosition(int columnPosition) =>
        columnPosition >= 0 && columnPosition < MaxIndexedColumns;

    /// <summary>
    /// Whether <paramref name="type"/> is a scalar for which a Delta <c>min</c>/<c>max</c>/<c>nullCount</c>
    /// can be produced. Boolean, the integral types (tinyint/smallint/int/bigint), float/double, date,
    /// timestamp, string, and decimal within the writable precision are supported; nested/complex types,
    /// <c>void</c>, and binary are omitted (advisory — omission only forfeits pruning).
    /// </summary>
    public bool IsSupportedForStatistics(DataType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            BooleanType or ByteType or ShortType or IntegerType or LongType
                or FloatType or DoubleType or DateType or TimestampType or StringType => true,
            DecimalType decimalType => decimalType.Precision <= ParquetTypeMapping.MaxSupportedDecimalPrecision,
            _ => false,
        };
    }
}
