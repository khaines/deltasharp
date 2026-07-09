using System.Collections.Immutable;
using DeltaSharp.Types;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Why a column's <c>min</c>/<c>max</c> is (un)available for pruning in a given file, inferred from the
/// snapshot schema and the file's parsed <see cref="FileStatistics"/> (STORY-05.6.3 AC4). An optimizer
/// uses this to tell a <b>missing-because-omitted</b> statistic (a pruning hint it simply does not have)
/// apart from a data condition — never a correctness signal.
/// </summary>
internal enum StatisticColumnState
{
    /// <summary>An exact, usable <c>min</c> and <c>max</c> are present (tight bounds).</summary>
    Available,

    /// <summary>The file carries no statistics at all (<c>add.stats</c> absent).</summary>
    AbsentNoStatistics,

    /// <summary>Statistics exist but this column has no <c>min</c>/<c>max</c> (e.g. beyond the write-time
    /// indexing horizon).</summary>
    AbsentNotIndexed,

    /// <summary>The column is entirely null in this file, so <c>min</c>/<c>max</c> are legitimately
    /// omitted (the <c>nullCount</c> equals the record count).</summary>
    AbsentAllNull,

    /// <summary>The file's bounds are not tight (e.g. a truncated string), so <c>min</c>/<c>max</c> are
    /// present-but-unusable for range pruning.</summary>
    AbsentNonTightBounds,

    /// <summary>The column is <b>indexed with a lower bound only</b>: a tight <c>min</c> is present but the
    /// <c>max</c> was legitimately omitted because the column's true max has no JSON-number encoding — a
    /// <c>NaN</c> or <c>+Infinity</c> value (<c>NaN</c> is Spark's greatest value), or a truncated string.
    /// The column retains one-sided skip capability for lower-bound predicates (<c>&lt;</c>/<c>&lt;=</c>,
    /// and the lower half of <c>=</c>); the upper side cannot be pruned. Advisory only — the optimizer
    /// learns the column has partial (not absent) skip capability.</summary>
    AbsentUpperBound,

    /// <summary>The column is <b>indexed with an upper bound only</b>: a tight <c>max</c> is present but the
    /// <c>min</c> was legitimately omitted because the column's true min has no JSON-number encoding — a
    /// <c>-Infinity</c> value. The column retains one-sided skip capability for upper-bound predicates
    /// (<c>&gt;</c>/<c>&gt;=</c>, and the upper half of <c>=</c>); the lower side cannot be pruned. Advisory
    /// only — the optimizer learns the column has partial (not absent) skip capability.</summary>
    AbsentLowerBound,

    /// <summary>The column is a nested/complex type (struct/array/map) and is never indexed.</summary>
    OmittedNestedType,

    /// <summary>The column is a scalar type not eligible for statistics (e.g. binary) and is never
    /// indexed.</summary>
    OmittedUnsupportedType,
}

/// <summary>Per-file statistics availability for the optimizer: the file identity, its size and record
/// count, whether it carried any statistics, and the per-top-level-column <see cref="StatisticColumnState"/>
/// (absence reasons). Read-only and derived purely from the immutable snapshot.</summary>
internal sealed record FileStatisticsReport(
    string Path,
    long SizeInBytes,
    long? RecordCount,
    bool HasStatistics,
    ImmutableSortedDictionary<string, StatisticColumnState> Columns)
{
    /// <summary>The availability state of <paramref name="column"/> (defaults to
    /// <see cref="StatisticColumnState.AbsentNoStatistics"/> for a column not in the schema).</summary>
    public StatisticColumnState StateOf(string column) =>
        Columns.TryGetValue(column, out StatisticColumnState state) ? state : StatisticColumnState.AbsentNoStatistics;
}

/// <summary>
/// The immutable, side-effect-free statistics view an optimizer requests from storage (STORY-05.6.3 AC4).
/// It carries a <b>freshness/version token</b> — the snapshot <see cref="Version"/>, which uniquely and
/// monotonically identifies the table state the numbers describe — the table aggregates, and per-file
/// availability with explicit <b>absence reasons</b>. <see cref="RecordCount"/> is populated only when
/// <see cref="HasCompleteRecordCounts"/> (every active file recorded its <c>numRecords</c>); otherwise it
/// is <see langword="null"/> so the optimizer never treats a partial count as exact.
/// </summary>
internal sealed record TableStatisticsReport(
    long Version,
    int FileCount,
    long TotalSizeInBytes,
    long? RecordCount,
    bool HasCompleteRecordCounts,
    ImmutableArray<FileStatisticsReport> Files)
{
    /// <summary>The freshness token for these statistics — the snapshot version they were computed from.
    /// Two reports with the same token describe the same table state.</summary>
    public long FreshnessToken => Version;
}

/// <summary>
/// Builds a <see cref="TableStatisticsReport"/> from an immutable snapshot's schema + active files
/// (STORY-05.6.3 AC4). Pure and deterministic: it only reads already-parsed state, inferring each
/// column's <see cref="StatisticColumnState"/> from the schema type and the file's
/// <see cref="FileStatistics"/> — it never re-reads data files or mutates anything.
/// </summary>
internal static class SnapshotStatisticsReporter
{
    public static TableStatisticsReport Build(
        long version, StructType schema, ImmutableArray<AddFileAction> files, StatisticsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(policy);

        long totalSize = 0;
        long recordTotal = 0;
        bool completeRecordCounts = true;
        ImmutableArray<FileStatisticsReport>.Builder fileReports =
            ImmutableArray.CreateBuilder<FileStatisticsReport>(files.Length);

        foreach (AddFileAction file in files)
        {
            totalSize += file.Size;
            long? numRecords = file.Stats?.NumRecords;
            if (numRecords is { } records)
            {
                recordTotal += records;
            }
            else
            {
                completeRecordCounts = false;
            }

            fileReports.Add(BuildFileReport(schema, file, policy));
        }

        return new TableStatisticsReport(
            version,
            files.Length,
            totalSize,
            completeRecordCounts ? recordTotal : null,
            completeRecordCounts,
            fileReports.ToImmutable());
    }

    private static FileStatisticsReport BuildFileReport(
        StructType schema, AddFileAction file, StatisticsPolicy policy)
    {
        FileStatistics? stats = file.Stats;
        var columns = ImmutableSortedDictionary.CreateBuilder<string, StatisticColumnState>(StringComparer.Ordinal);
        foreach (StructField field in schema)
        {
            columns[field.Name] = InferState(field, stats, policy);
        }

        return new FileStatisticsReport(file.Path, file.Size, stats?.NumRecords, stats is not null, columns.ToImmutable());
    }

    private static StatisticColumnState InferState(StructField field, FileStatistics? stats, StatisticsPolicy policy)
    {
        // Type-omission reasons are independent of any file's statistics.
        if (field.DataType is ArrayType or MapType or StructType)
        {
            return StatisticColumnState.OmittedNestedType;
        }

        if (!policy.IsSupportedForStatistics(field.DataType))
        {
            return StatisticColumnState.OmittedUnsupportedType;
        }

        if (stats is null)
        {
            return StatisticColumnState.AbsentNoStatistics;
        }

        bool hasMin = stats.MinValues.ContainsKey(field.Name);
        bool hasMax = stats.MaxValues.ContainsKey(field.Name);

        // A non-tight file's bounds are present-but-unusable for range pruning (a truncated string sets the
        // file-wide flag false), matching FilePruner's boundsUsable gate — report that whenever any bound is
        // present, whether one or both survive.
        if ((hasMin || hasMax) && stats.TightBounds == false)
        {
            return StatisticColumnState.AbsentNonTightBounds;
        }

        if (hasMin && hasMax)
        {
            return StatisticColumnState.Available;
        }

        // Exactly one tight bound present (partial / one-sided skip capability). The counterpart was
        // legitimately omitted because its true value has no JSON-number encoding: a NaN/+Infinity raises
        // the true max beyond encoding (finite min kept → lower-only, AbsentUpperBound) and a -Infinity min
        // is unencodable (finite max kept → upper-only, AbsentLowerBound). (A truncated string also drops
        // its max but sets tightBounds=false, handled above.) Reporting the one-sided state — rather than
        // AbsentNotIndexed — tells the optimizer the column still has lower-only / upper-only skip
        // capability, mirroring FilePruner's per-bound pruning.
        if (hasMin)
        {
            return StatisticColumnState.AbsentUpperBound;
        }

        if (hasMax)
        {
            return StatisticColumnState.AbsentLowerBound;
        }

        // No min/max at all: distinguish an all-null column (legitimate omission) from an un-indexed one.
        // This classification (like FilePruner's all-null skip) is sound ONLY while nullCount and numRecords
        // are EXACT counts; revisit when deletion vectors, which over-count, land.
        if (stats.NumRecords is { } records && records > 0
            && stats.NullCount.TryGetValue(field.Name, out long nulls) && nulls == records)
        {
            return StatisticColumnState.AbsentAllNull;
        }

        return StatisticColumnState.AbsentNotIndexed;
    }
}
