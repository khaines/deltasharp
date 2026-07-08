using System.Globalization;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The <b>commit-coordination selection contract</b> for speculative task output (STORY-05.3.2 AC3). Under
/// speculative execution the same logical task can run as several attempts, each staging its own output
/// files; the commit coordinator must publish <b>exactly one attempt's output per task</b> so a retried or
/// speculated task never double-commits its rows. This is a pure rule over the candidate <c>add</c> set
/// (no I/O): it keeps only the <b>winning attempt</b> (highest attempt number) for each task and drops the
/// superseded attempts' duplicate files.
///
/// <para>An executor tags each staged output file's <c>add.tags</c> with its producing task identity
/// (<see cref="TaskIdTag"/>) and attempt number (<see cref="AttemptNumberTag"/>). An <c>add</c> without a
/// task tag is not a speculative task output and passes through unchanged. The executor-side tagging that
/// produces these tags lands with distributed execution; this contract is the commit-side counterpart the
/// coordinator applies before building the action set.</para>
/// </summary>
internal static class TaskCommitCoordination
{
    /// <summary>The <c>add.tags</c> key carrying the producing task's identity.</summary>
    internal const string TaskIdTag = "delta.taskId";

    /// <summary>The <c>add.tags</c> key carrying the producing task attempt number (a non-negative integer;
    /// a missing/unparseable value is treated as attempt 0).</summary>
    internal const string AttemptNumberTag = "delta.attemptNumber";

    /// <summary>
    /// Returns the subset of <paramref name="adds"/> that should be committed: for each task (grouped by
    /// <see cref="TaskIdTag"/>) only the files of its highest-attempt output are kept; superseded attempts'
    /// files are dropped. Untagged adds are preserved. Order is preserved. If no add carries a task tag the
    /// input is returned unchanged.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="adds"/> is null.</exception>
    public static IReadOnlyList<AddFileAction> SelectWinningOutputs(IReadOnlyList<AddFileAction> adds)
    {
        ArgumentNullException.ThrowIfNull(adds);

        Dictionary<string, long>? winningAttempt = null;
        foreach (AddFileAction add in adds)
        {
            if (TryGetTask(add, out string taskId, out long attempt))
            {
                winningAttempt ??= new Dictionary<string, long>(StringComparer.Ordinal);
                winningAttempt[taskId] = winningAttempt.TryGetValue(taskId, out long best)
                    ? Math.Max(best, attempt)
                    : attempt;
            }
        }

        if (winningAttempt is null)
        {
            return adds; // no speculative task output — nothing to deduplicate.
        }

        var selected = new List<AddFileAction>(adds.Count);
        foreach (AddFileAction add in adds)
        {
            if (!TryGetTask(add, out string taskId, out long attempt))
            {
                selected.Add(add); // not a task output — always kept.
            }
            else if (attempt == winningAttempt[taskId])
            {
                selected.Add(add); // the winning attempt's file.
            }

            // else: a superseded attempt's duplicate output — dropped so the task commits exactly once.
        }

        return selected;
    }

    private static bool TryGetTask(AddFileAction add, out string taskId, out long attempt)
    {
        attempt = 0;
        if (!add.Tags.TryGetValue(TaskIdTag, out string? id) || string.IsNullOrEmpty(id))
        {
            taskId = string.Empty;
            return false;
        }

        taskId = id;
        if (add.Tags.TryGetValue(AttemptNumberTag, out string? raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            && parsed >= 0)
        {
            attempt = parsed;
        }

        return true;
    }
}
