using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// A <b>memoizing snapshot wrapper</b> around the caller's <see cref="IEnumerable{Row}"/> that a
/// <see cref="LocalRelation"/> stores as its data (STORY-04.1.2 / #158). It preserves the read-door's
/// laziness — construction enumerates <b>nothing</b> (AC1) — yet gives every later enumeration a
/// <b>stable, replayable</b> view: the <b>first</b> enumeration (at the first action's execution)
/// copies the source into an immutable <see cref="IReadOnlyList{Row}"/> snapshot, and every subsequent
/// enumeration replays that snapshot.
/// </summary>
/// <remarks>
/// <para>
/// This closes the "by-reference row capture" divergence: without memoization the source was
/// re-enumerated once per action, so mutating the source between actions, or passing a single-use
/// iterator, could make <see cref="DataFrame.Count()"/> and <see cref="DataFrame.Collect()"/> disagree on
/// the <b>same</b> DataFrame (violating plan immutability and Spark's <c>createDataFrame(List, schema)</c>
/// snapshot semantics). After the first action every action sees identical rows, and multi-scans /
/// self-joins observe one stable snapshot.
/// </para>
/// <para>
/// <b>Snapshot timing.</b> The snapshot is taken at the <b>first enumeration</b> (first action), not at
/// construction — that is the strongest reading of AC1 (the call materializes no rows). Mutating the
/// source <i>before</i> the first action is therefore still observed; mutating it <i>after</i> is not.
/// </para>
/// <para>
/// <b>Reference identity.</b> <see cref="LocalRelation"/> keeps <b>one</b> wrapper instance across its
/// unresolved/resolved forms (see <see cref="Wrap"/>, which is idempotent), so
/// <see cref="LocalRelation.NodeEquals"/> can compare data by reference identity and the snapshot is
/// shared by every scan of the same relation.
/// </para>
/// </remarks>
internal sealed class MemoizedRowSequence : IEnumerable<Row>
{
    private readonly object _gate = new();
    private IEnumerable<Row>? _source;
    private IReadOnlyList<Row>? _snapshot;

    private MemoizedRowSequence(IEnumerable<Row> source) => _source = source;

    /// <summary>Wraps <paramref name="source"/> in a memoizing snapshot sequence, returning it unchanged
    /// when it is already one (idempotent, so a relation's resolved form shares the same snapshot as its
    /// unresolved form).</summary>
    /// <param name="source">The caller's row sequence.</param>
    /// <returns>A memoizing wrapper over <paramref name="source"/>.</returns>
    public static MemoizedRowSequence Wrap(IEnumerable<Row> source) =>
        source as MemoizedRowSequence ?? new MemoizedRowSequence(source);

    /// <inheritdoc/>
    public IEnumerator<Row> GetEnumerator() => Snapshot().GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Returns the memoized snapshot, taking it (on the first call) by draining the source into an
    /// immutable list. The <paramref name="cancellationToken"/> is polled <b>per source row</b> during that
    /// first drain, so a slow/large/unbounded source honors cancellation/timeout — the drain is the point
    /// at which the deferred read door (STORY-04.1.2) actually pulls the user <see cref="IEnumerable{Row}"/>,
    /// and it runs inside the executor's <c>ScanPlan.Execute</c>, outside <c>PhysicalRuntime.Run</c>'s
    /// per-batch cancellation poll (STORY-04.6.4 AC2 / #425). Subsequent calls replay the cached snapshot
    /// and never re-drain, so the token only bounds the one-time source read.
    /// </summary>
    /// <param name="cancellationToken">The run's effective token, polled per source row during the first
    /// drain. Defaults to <see cref="CancellationToken.None"/> for enumeration outside an execution run.</param>
    /// <returns>The stable, replayable row snapshot.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled
    /// mid-drain (the snapshot is not published, so a later call re-attempts the drain).</exception>
    internal IReadOnlyList<Row> Snapshot(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Row>? snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (_gate)
        {
            if (_snapshot is null)
            {
                var buffer = new List<Row>();
                foreach (Row row in _source!)
                {
                    // The eager source drain is the actual (potentially slow/unbounded) read; poll per row
                    // so cancellation/timeout is honored here rather than only after the whole drain (#425).
                    cancellationToken.ThrowIfCancellationRequested();
                    buffer.Add(row);
                }

                // Publish the immutable snapshot and release the source so a captured collection (and any
                // rows it holds beyond the snapshot) can be collected.
                Volatile.Write(ref _snapshot, buffer);
                _source = null;
            }

            return _snapshot;
        }
    }
}
