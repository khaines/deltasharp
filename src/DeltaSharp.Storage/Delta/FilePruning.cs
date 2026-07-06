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

/// <summary>
/// The result of <see cref="Snapshot.PruneFiles"/>: the surviving <see cref="Candidates"/> (a <b>sound
/// over-approximation</b> — every file that could match is included) plus counters describing how many of
/// the <see cref="TotalFiles"/> active files were provably excluded. Statistics are advisory (STORY-05.2.3
/// AC2): a file is pruned only when the log-committed partition values or statistics <b>prove</b> it holds
/// no matching row, so a wrong/absent stat costs a skipped-pruning opportunity, never a wrong answer.
/// </summary>
internal sealed record FilePruningResult(
    ImmutableArray<AddFileAction> Candidates,
    int TotalFiles,
    int PrunedByPartition,
    int PrunedByStatistics);
