namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The shared within-batch cancellation-granularity policy for the interpreted operators and scalar
/// expression evaluators (STORY-03.6.1, deferral (b)). Operators observe cancellation at batch
/// boundaries already; this closes the remaining window where a single <i>large</i> input batch — one
/// the producer did not chunk — would otherwise run a per-row build/buffer/assign/evaluate loop to
/// completion uncancellably. Every such loop calls <see cref="Poll"/> so the worst-case uncancellable
/// run is bounded to <see cref="RowPollInterval"/> rows regardless of upstream batch size.
/// </summary>
internal static class CancellationPolicy
{
    /// <summary>
    /// Rows between cancellation polls inside an interpreted per-row loop. A power of two so the poll
    /// predicate is a single mask-and-branch (negligible against the per-row work it guards) and the
    /// first row (index 0) polls immediately, so an already-cancelled token is seen at once.
    /// </summary>
    internal const int RowPollInterval = 1024;

    /// <summary>
    /// Polls the token every <see cref="RowPollInterval"/> rows. Cheap to call on every iteration: it
    /// only touches the token on a row whose low bits are clear.
    /// </summary>
    /// <param name="token">The execution cancellation token.</param>
    /// <param name="row">The current zero-based loop index.</param>
    /// <exception cref="System.OperationCanceledException">The token is cancelled at a poll point.</exception>
    internal static void Poll(CancellationToken token, int row)
    {
        if ((row & (RowPollInterval - 1)) == 0)
        {
            token.ThrowIfCancellationRequested();
        }
    }
}
