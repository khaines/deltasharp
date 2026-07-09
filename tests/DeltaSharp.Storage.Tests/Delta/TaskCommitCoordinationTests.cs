using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Tests for <see cref="TaskCommitCoordination.SelectWinningOutputs"/> (STORY-05.3.2 AC3): only one
/// attempt's output per task is committed, so a speculated/retried task never double-commits its rows.
/// </summary>
public sealed class TaskCommitCoordinationTests
{
    private static readonly ImmutableSortedDictionary<string, string?> NoPartition =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static AddFileAction Add(string path, string? taskId = null, int? attempt = null)
    {
        var tags = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (taskId is not null)
        {
            tags[TaskCommitCoordination.TaskIdTag] = taskId;
        }

        if (attempt is { } a)
        {
            tags[TaskCommitCoordination.AttemptNumberTag] = a.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new AddFileAction(path, NoPartition, 1L, 1L, DataChange: true, Stats: null, Tags: tags.ToImmutable());
    }

    // A task output whose delta.attemptNumber tag is an arbitrary raw string (to exercise the parse path
    // with malformed / non-numeric values).
    private static AddFileAction AddRawAttempt(string path, string taskId, string rawAttempt)
    {
        var tags = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        tags[TaskCommitCoordination.TaskIdTag] = taskId;
        tags[TaskCommitCoordination.AttemptNumberTag] = rawAttempt;
        return new AddFileAction(path, NoPartition, 1L, 1L, DataChange: true, Stats: null, Tags: tags.ToImmutable());
    }

    private static string[] Paths(IReadOnlyList<AddFileAction> adds) => adds.Select(a => a.Path).ToArray();

    [Fact]
    public void KeepsOnlyHighestAttempt_PerTask()
    {
        // Speculative execution ran task t0 twice; only attempt 1's output commits.
        IReadOnlyList<AddFileAction> selected = TaskCommitCoordination.SelectWinningOutputs(new[]
        {
            Add("t0-a0.parquet", taskId: "t0", attempt: 0),
            Add("t0-a1.parquet", taskId: "t0", attempt: 1),
        });

        Assert.Equal(new[] { "t0-a1.parquet" }, Paths(selected));
    }

    [Fact]
    public void KeepsAllFilesOfTheWinningAttempt()
    {
        // A task's winning attempt produced two files (e.g. two partitions) — both are kept.
        IReadOnlyList<AddFileAction> selected = TaskCommitCoordination.SelectWinningOutputs(new[]
        {
            Add("t0-a0-p1.parquet", taskId: "t0", attempt: 0),
            Add("t0-a1-p1.parquet", taskId: "t0", attempt: 1),
            Add("t0-a1-p2.parquet", taskId: "t0", attempt: 1),
        });

        Assert.Equal(new[] { "t0-a1-p1.parquet", "t0-a1-p2.parquet" }, Paths(selected).OrderBy(p => p).ToArray());
    }

    [Fact]
    public void DeduplicatesEachTaskIndependently()
    {
        IReadOnlyList<AddFileAction> selected = TaskCommitCoordination.SelectWinningOutputs(new[]
        {
            Add("t0-a0.parquet", taskId: "t0", attempt: 0),
            Add("t0-a1.parquet", taskId: "t0", attempt: 1),
            Add("t1-a0.parquet", taskId: "t1", attempt: 0),
            Add("t1-a2.parquet", taskId: "t1", attempt: 2),
        });

        Assert.Equal(new[] { "t0-a1.parquet", "t1-a2.parquet" }, Paths(selected).OrderBy(p => p).ToArray());
    }

    [Fact]
    public void PassesThroughUntaggedAdds()
    {
        // Adds without a task tag are not speculative outputs and are always kept.
        var untagged = Add("plain.parquet");
        IReadOnlyList<AddFileAction> selected = TaskCommitCoordination.SelectWinningOutputs(new[]
        {
            untagged,
            Add("t0-a0.parquet", taskId: "t0", attempt: 0),
            Add("t0-a1.parquet", taskId: "t0", attempt: 1),
        });

        Assert.Equal(new[] { "plain.parquet", "t0-a1.parquet" }, Paths(selected).OrderBy(p => p).ToArray());
    }

    [Fact]
    public void NoTaskTags_ReturnsInputUnchanged()
    {
        var input = new[] { Add("a.parquet"), Add("b.parquet") };
        IReadOnlyList<AddFileAction> selected = TaskCommitCoordination.SelectWinningOutputs(input);

        Assert.Same(input, selected);
    }

    [Fact]
    public void PreservesInputOrder_OfKeptFiles()
    {
        // The contract promises stable order: kept files (winners + untagged) appear in their original
        // relative input order. Asserted WITHOUT sorting so a regrouping/reordering regression is caught.
        IReadOnlyList<AddFileAction> selected = TaskCommitCoordination.SelectWinningOutputs(new[]
        {
            Add("z-plain.parquet"),                          // untagged → kept
            Add("t1-a2.parquet", taskId: "t1", attempt: 2),  // t1 winner
            Add("t0-a1.parquet", taskId: "t0", attempt: 1),  // t0 winner
            Add("t1-a0.parquet", taskId: "t1", attempt: 0),  // t1 superseded → dropped
            Add("a-plain.parquet"),                          // untagged → kept
            Add("t0-a0.parquet", taskId: "t0", attempt: 0),  // t0 superseded → dropped
        });

        Assert.Equal(
            new[] { "z-plain.parquet", "t1-a2.parquet", "t0-a1.parquet", "a-plain.parquet" },
            Paths(selected));
    }

    [Fact]
    public void UnparseableAttemptNumber_TreatedAsZero_LosesToHigherAttempt()
    {
        // A malformed (non-numeric) delta.attemptNumber defaults to attempt 0 FOR ITS TASK, so it loses to
        // an explicit attempt 1 of the same task and is DROPPED. The drop (not pass-through) proves it is
        // treated as a task output coerced to attempt 0 — not as an untagged add, which would be kept.
        IReadOnlyList<AddFileAction> selected = TaskCommitCoordination.SelectWinningOutputs(new[]
        {
            AddRawAttempt("t0-garbage.parquet", taskId: "t0", rawAttempt: "not-a-number"),
            Add("t0-a1.parquet", taskId: "t0", attempt: 1),
        });

        Assert.Equal(new[] { "t0-a1.parquet" }, Paths(selected)); // garbage dropped as the attempt-0 loser
    }

    [Fact]
    public void NegativeAttemptNumber_TreatedAsZero_LosesToHigherAttempt()
    {
        // A negative attempt number is rejected (guard `parsed >= 0`) and coerced to attempt 0 FOR ITS TASK,
        // so it loses to an explicit attempt 1 and is DROPPED — proving the coercion keeps it a task output
        // (a negative value is not silently accepted as a huge/low attempt, nor treated as untagged).
        IReadOnlyList<AddFileAction> selected = TaskCommitCoordination.SelectWinningOutputs(new[]
        {
            Add("t0-neg.parquet", taskId: "t0", attempt: -1),
            Add("t0-a1.parquet", taskId: "t0", attempt: 1),
        });

        Assert.Equal(new[] { "t0-a1.parquet" }, Paths(selected)); // negative dropped as the attempt-0 loser
    }

    [Fact]
    public void MissingAttemptNumber_TreatedAsZero()
    {
        // A tagged output with no attempt number defaults to attempt 0, so an explicit attempt 1 wins.
        IReadOnlyList<AddFileAction> selected = TaskCommitCoordination.SelectWinningOutputs(new[]
        {
            Add("t0-none.parquet", taskId: "t0"),
            Add("t0-a1.parquet", taskId: "t0", attempt: 1),
        });

        Assert.Equal(new[] { "t0-a1.parquet" }, Paths(selected));
    }
}
