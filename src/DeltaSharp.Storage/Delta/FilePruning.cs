using System.Collections.Immutable;
using System.Globalization;

namespace DeltaSharp.Storage.Delta;

/// <summary>A comparison operator for a data-skipping predicate (<c>column OP value</c>).</summary>
internal enum DeltaPredicateOp
{
    /// <summary><c>column = value</c>.</summary>
    Equal,

    /// <summary><c>column &lt; value</c>.</summary>
    LessThan,

    /// <summary><c>column &lt;= value</c>.</summary>
    LessThanOrEqual,

    /// <summary><c>column &gt; value</c>.</summary>
    GreaterThan,

    /// <summary><c>column &gt;= value</c>.</summary>
    GreaterThanOrEqual,
}

/// <summary>An exact partition-value filter (<c>partitionColumn = value</c>, or <c>IS NULL</c> when
/// <paramref name="Value"/> is null). Partition values are stored per file exactly (never truncated), so
/// this filter prunes soundly.</summary>
internal sealed record PartitionEqualityFilter(string Column, string? Value);

/// <summary>A data-skipping filter over a (top-level) data column, evaluated against per-file
/// <c>min</c>/<c>max</c>/<c>nullCount</c> statistics. Advisory: it can only ever prune files it proves
/// cannot match; a file it keeps may still not match (the residual predicate stays the engine's job).</summary>
internal sealed record ColumnRangeFilter(string Column, DeltaPredicateOp Op, DeltaStatValue Value);

/// <summary>A file-pruning request: a conjunction (AND) of partition and data-skipping filters.</summary>
internal sealed record FilePruningRequest(
    ImmutableArray<PartitionEqualityFilter> PartitionFilters,
    ImmutableArray<ColumnRangeFilter> DataFilters)
{
    /// <summary>An empty request (keeps every file).</summary>
    public static FilePruningRequest Empty { get; } =
        new([], []);

    /// <summary>A request with only partition filters.</summary>
    public static FilePruningRequest ForPartitions(params PartitionEqualityFilter[] filters) =>
        new([.. filters], []);

    /// <summary>A request with only data-skipping filters.</summary>
    public static FilePruningRequest ForData(params ColumnRangeFilter[] filters) =>
        new([], [.. filters]);
}

/// <summary>Why a file was excluded from the scan candidate set — an <b>optimization</b> audit trail
/// (STORY-05.6.3 AC3), never a correctness signal. A skip is emitted only when the committed partition
/// values or per-file statistics <b>prove</b> the file holds no matching row.</summary>
internal enum FileSkipReason
{
    /// <summary>The file's committed partition values differ from an exact partition filter.</summary>
    PartitionMismatch,

    /// <summary>The file's tight <c>min</c>/<c>max</c>/<c>nullCount</c> statistics prove no row can
    /// satisfy a data filter (a disjoint range or an all-null column).</summary>
    StatisticsDisjoint,
}

/// <summary>A file the pruner excluded from the scan, with the <see cref="Reason"/> it was provably
/// non-matching. Reporting a skip is an optimization decision (it means "this file need not be scanned"),
/// <b>not</b> a correctness gate: the surviving candidates are always a sound over-approximation, so a
/// missing skip only costs efficiency (STORY-05.6.3 AC3).</summary>
internal sealed record SkippedFile(AddFileAction File, FileSkipReason Reason);

/// <summary>
/// The result of <see cref="Snapshot.PruneFiles"/>: the surviving <see cref="Candidates"/> (a <b>sound
/// over-approximation</b> — every file that could match is included) plus the <see cref="Skipped"/> files
/// that were provably excluded, each with its <see cref="FileSkipReason"/>. Statistics are advisory
/// (STORY-05.2.3 AC2 / STORY-05.6.3 AC3): a file is skipped only when the log-committed partition values
/// or statistics <b>prove</b> it holds no matching row, so an absent/coarse/untrusted statistic yields a
/// forfeited skip (the file stays a candidate and is scanned) — <b>never</b> a wrong answer. Skips are
/// therefore an optimization report, and the residual predicate remains the scan's responsibility.
/// </summary>
internal sealed record FilePruningResult(
    ImmutableArray<AddFileAction> Candidates,
    int TotalFiles,
    ImmutableArray<SkippedFile> Skipped)
{
    /// <summary>The number of files skipped because their committed partition values proved non-matching.</summary>
    public int PrunedByPartition => CountReason(FileSkipReason.PartitionMismatch);

    /// <summary>The number of files skipped because their statistics proved non-matching.</summary>
    public int PrunedByStatistics => CountReason(FileSkipReason.StatisticsDisjoint);

    private int CountReason(FileSkipReason reason)
    {
        int count = 0;
        foreach (SkippedFile skipped in Skipped)
        {
            if (skipped.Reason == reason)
            {
                count++;
            }
        }

        return count;
    }
}
