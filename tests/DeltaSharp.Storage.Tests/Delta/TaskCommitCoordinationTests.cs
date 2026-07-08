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
