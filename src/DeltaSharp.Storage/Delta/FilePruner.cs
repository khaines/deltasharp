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
        ImmutableArray<SkippedFile>.Builder skipped = ImmutableArray.CreateBuilder<SkippedFile>();

        foreach (AddFileAction file in files)
        {
            if (PartitionExcludes(file, request.PartitionFilters))
            {
                skipped.Add(new SkippedFile(file, FileSkipReason.PartitionMismatch));
                continue;
            }

            if (StatisticsExclude(file, request.DataFilters))
            {
                skipped.Add(new SkippedFile(file, FileSkipReason.StatisticsDisjoint));
                continue;
            }

            candidates.Add(file);
        }

        return new FilePruningResult(candidates.ToImmutable(), files.Length, skipped.ToImmutable());
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
            // An all-null column cannot satisfy any concrete comparison (NULL is never <, >, or =). This
            // skip (and the SnapshotStatistics AbsentAllNull equivalent) is sound ONLY while nullCount and
            // numRecords are EXACT counts; revisit when deletion vectors, which over-count, land.
            if (stats.NumRecords is { } rows && rows > 0
                && stats.NullCount.TryGetValue(filter.Column, out long nulls) && nulls == rows)
            {
                return true;
            }

            if (!boundsUsable)
            {
                continue;
            }

            // Per-bound skipping: min and max are consulted INDEPENDENTLY and only when present. A bound
            // omitted for an unencodable extreme — a NaN/+Infinity max (finite min kept) or a -Infinity min
            // (finite max kept) — still lets the OTHER bound prove a skip on its
            // side, while a needed-but-absent bound merely forfeits that skip (fail-open, KEEP). Passing a
            // possibly-null bound is sound: RangeProvesNoMatch reads a null bound as "no constraint on that
            // side", so it can never manufacture a skip from a missing bound.
            stats.MinValues.TryGetValue(filter.Column, out DeltaStatValue? min);
            stats.MaxValues.TryGetValue(filter.Column, out DeltaStatValue? max);

            if (RangeProvesNoMatch(filter.Op, min, max, filter.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the present per-file bounds <b>prove</b> that no non-null value can satisfy
    /// <c>column OP value</c>, so the file may be soundly skipped. Each bound is used <b>independently</b>
    /// and only when present, so a file that carries just one bound (its counterpart omitted for a
    /// NaN/±Infinity extreme) is still prunable on the side it does have:
    /// <list type="bullet">
    /// <item><c>&lt;</c> / <c>&lt;=</c> use only <c>min</c>: skip iff every value is at/above
    /// <paramref name="value"/> (<c>min &gt;= value</c> for <c>&lt;</c>, <c>min &gt; value</c> for
    /// <c>&lt;=</c>).</item>
    /// <item><c>&gt;</c> / <c>&gt;=</c> use only <c>max</c>: skip iff every value is at/below
    /// <paramref name="value"/> (<c>max &lt;= value</c> for <c>&gt;</c>, <c>max &lt; value</c> for
    /// <c>&gt;=</c>).</item>
    /// <item><c>=</c> uses either bound: skip iff <paramref name="value"/> is below <c>min</c> or above
    /// <c>max</c> — either alone proves the equality cannot hold.</item>
    /// </list>
    /// Conservative on every other axis: an absent needed bound, a non-numeric
    /// (<see cref="DeltaStatKind.String"/>/<see cref="DeltaStatKind.Boolean"/>) bound, or any un-comparable
    /// kind yields <see langword="false"/> (keep) — pruning only ever forfeits an opportunity, never drops a
    /// matching row. Because <c>NaN</c> is Spark's <i>greatest</i> value its column omits <c>max</c>, so
    /// <c>&gt;</c>/<c>&gt;=</c>/<c>=</c> against a finite literal correctly keep the file (the <c>NaN</c> row
    /// may match), while <c>&lt;</c>/<c>&lt;=</c> can still skip via the surviving finite <c>min</c>.
    /// </summary>
    private static bool RangeProvesNoMatch(DeltaPredicateOp op, DeltaStatValue? min, DeltaStatValue? max, DeltaStatValue value)
    {
        switch (op)
        {
            case DeltaPredicateOp.LessThan:
                // Matches iff some value < X ⟺ min < X. Skip iff min present and min >= X (X <= min).
                return min is not null && TryCompare(value, min, out int lt) && lt <= 0;
            case DeltaPredicateOp.LessThanOrEqual:
                // Matches iff min <= X. Skip iff min present and min > X (X < min).
                return min is not null && TryCompare(value, min, out int le) && le < 0;
            case DeltaPredicateOp.GreaterThan:
                // Matches iff some value > X ⟺ max > X. Skip iff max present and max <= X (X >= max).
                return max is not null && TryCompare(value, max, out int gt) && gt >= 0;
            case DeltaPredicateOp.GreaterThanOrEqual:
                // Matches iff max >= X. Skip iff max present and max < X (X > max).
                return max is not null && TryCompare(value, max, out int ge) && ge > 0;
            case DeltaPredicateOp.Equal:
                // Matches iff min <= X <= max. Either bound alone can prove a skip: X below min or above max.
                if (min is not null && TryCompare(value, min, out int eqMin) && eqMin < 0)
                {
                    return true; // X < min
                }

                return max is not null && TryCompare(value, max, out int eqMax) && eqMax > 0; // X > max
            default:
                return false;
        }
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
