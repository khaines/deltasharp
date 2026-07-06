using System.Collections.Immutable;
using System.Globalization;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The sound, advisory file-pruning evaluator behind <see cref="Snapshot.PruneFiles"/> (design §2.4
/// data-skipping; STORY-05.2.3 AC2). It prunes an active file <b>only</b> when the log-committed partition
/// values or per-file statistics <b>prove</b> the file cannot contain a row satisfying the request — so
/// the surviving candidate set is always a superset of the truly-matching files (soundness), and a
/// missing/coarse/untrusted statistic merely forfeits a pruning opportunity rather than dropping data.
/// </summary>
internal static class FilePruner
{
    public static FilePruningResult Prune(ImmutableArray<AddFileAction> files, FilePruningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ImmutableArray<AddFileAction>.Builder candidates = ImmutableArray.CreateBuilder<AddFileAction>();
        int prunedByPartition = 0;
        int prunedByStatistics = 0;

        foreach (AddFileAction file in files)
        {
            if (PartitionExcludes(file, request.PartitionFilters))
            {
                prunedByPartition++;
                continue;
            }

            if (StatisticsExclude(file, request.DataFilters))
            {
                prunedByStatistics++;
                continue;
            }

            candidates.Add(file);
        }

        return new FilePruningResult(candidates.ToImmutable(), files.Length, prunedByPartition, prunedByStatistics);
    }

    /// <summary>True only if a partition filter proves this file's partition cannot match (its exact,
    /// committed partition value differs). An unknown partition column never prunes.</summary>
    private static bool PartitionExcludes(AddFileAction file, ImmutableArray<PartitionEqualityFilter> filters)
    {
        foreach (PartitionEqualityFilter filter in filters)
        {
            if (file.PartitionValues.TryGetValue(filter.Column, out string? actual)
                && !string.Equals(actual, filter.Value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True only if some data filter proves, from tight min/max/null statistics, that no row in
    /// the file can satisfy it. Absent/untrusted statistics never prune.</summary>
    private static bool StatisticsExclude(AddFileAction file, ImmutableArray<ColumnRangeFilter> filters)
    {
        FileStatistics? stats = file.Stats;
        if (stats is null || filters.IsDefaultOrEmpty)
        {
            return false;
        }

        // tightBounds == false means min/max are not exact bounds → never prune on them.
        bool boundsUsable = stats.TightBounds != false;

        foreach (ColumnRangeFilter filter in filters)
        {
            // An all-null column cannot satisfy any concrete comparison (NULL is never <, >, or =).
            if (stats.NumRecords is { } rows && rows > 0
                && stats.NullCount.TryGetValue(filter.Column, out long nulls) && nulls == rows)
            {
                return true;
            }

            if (!boundsUsable
                || !stats.MinValues.TryGetValue(filter.Column, out DeltaStatValue? min)
                || !stats.MaxValues.TryGetValue(filter.Column, out DeltaStatValue? max))
            {
                continue;
            }

            if (!MightMatch(filter.Op, min, max, filter.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <c>column OP value</c> <b>might</b> be satisfied by some value in the closed range
    /// <c>[min, max]</c>. Conservative: returns <see langword="true"/> (keep the file) whenever the values
    /// cannot be compared (mismatched or non-numeric statistic kinds), so pruning stays sound; only a
    /// provably-disjoint range returns <see langword="false"/>.
    /// </summary>
    private static bool MightMatch(DeltaPredicateOp op, DeltaStatValue min, DeltaStatValue max, DeltaStatValue value)
    {
        if (!TryCompare(value, min, out int vsMin) || !TryCompare(value, max, out int vsMax))
        {
            return true;
        }

        return op switch
        {
            DeltaPredicateOp.Equal => vsMin >= 0 && vsMax <= 0,        // min <= value <= max
            DeltaPredicateOp.LessThan => vsMin > 0,                    // min < value
            DeltaPredicateOp.LessThanOrEqual => vsMin >= 0,            // min <= value
            DeltaPredicateOp.GreaterThan => vsMax < 0,                 // value < max
            DeltaPredicateOp.GreaterThanOrEqual => vsMax <= 0,         // value <= max
            _ => true,
        };
    }

    /// <summary>Numerically compares two statistic values, returning <see langword="false"/> unless both are
    /// numeric (<see cref="DeltaStatKind.Long"/>/<see cref="DeltaStatKind.Double"/>). String/boolean bounds
    /// are not used for range skipping — Delta may truncate string statistics, so range pruning on them
    /// would be unsound.</summary>
    private static bool TryCompare(DeltaStatValue left, DeltaStatValue right, out int comparison)
    {
        comparison = 0;
        bool leftNumeric = left.Kind is DeltaStatKind.Long or DeltaStatKind.Double;
        bool rightNumeric = right.Kind is DeltaStatKind.Long or DeltaStatKind.Double;
        if (!leftNumeric || !rightNumeric)
        {
            return false;
        }

        if (left.Kind == DeltaStatKind.Long && right.Kind == DeltaStatKind.Long
            && long.TryParse(left.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long leftLong)
            && long.TryParse(right.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long rightLong))
        {
            comparison = leftLong.CompareTo(rightLong);
            return true;
        }

        if (double.TryParse(left.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double leftDouble)
            && double.TryParse(right.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double rightDouble)
            && !double.IsNaN(leftDouble) && !double.IsNaN(rightDouble))
        {
            comparison = leftDouble.CompareTo(rightDouble);
            return true;
        }

        return false;
    }
}
