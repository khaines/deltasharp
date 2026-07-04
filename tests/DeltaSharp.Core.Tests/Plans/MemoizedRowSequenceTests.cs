using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// Concurrency / cancellation tests for <see cref="MemoizedRowSequence"/> (STORY-04.1.2 / #158 read-door,
/// made cancellation-aware by STORY-04.6.4 / #176). They exercise the deferred snapshot drain's token
/// handling: a caller blocked behind another caller's in-progress first drain still honors its own
/// cancellation (#437), and a re-enumerable source replays correctly after a cancelled first drain (the
/// common case; the single-use limitation is tracked by #438).
/// </summary>
public sealed class MemoizedRowSequenceTests
{
    private static readonly StructType Schema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
    });

    [Fact]
    public async Task Snapshot_SecondCallerBlockedOnGate_HonorsItsOwnCancellation()
    {
        var drainStarted = new ManualResetEventSlim();

        // The first caller (below) drains this source; MoveNext signals then holds the drain gate ~800 ms.
        IEnumerable<Row> SlowHoldingSource()
        {
            drainStarted.Set();
            Thread.Sleep(800);
            yield return new Row(Schema, 1);
        }

        var sequence = MemoizedRowSequence.Wrap(SlowHoldingSource());

        // Caller 1 wins the gate and drains (uncancelled) for ~800 ms.
        Task<IReadOnlyList<Row>> first = Task.Run(() => sequence.Snapshot(CancellationToken.None));
        Assert.True(drainStarted.Wait(TimeSpan.FromSeconds(5)), "the first drain never started");

        // Caller 2 blocks behind caller 1's in-progress drain with a 50 ms timeout. With a plain lock it
        // would ignore its token until caller 1 finishes (~800 ms) and then return the cached snapshot
        // WITHOUT throwing; the cancellable acquisition makes it observe its own cancellation promptly.
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => sequence.Snapshot(cts.Token));
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 600,
            $"the blocked caller waited {stopwatch.ElapsedMilliseconds} ms instead of honoring its 50 ms token");

        // Let caller 1 finish cleanly (it drains one row uncancelled).
        Assert.Single(await first);
    }

    [Fact]
    public void Snapshot_ReEnumerableSource_ReplaysAfterCancelledFirstDrain()
    {
        // A re-enumerable source (a List — the CreateDataFrame norm): a cancelled first drain publishes NO
        // snapshot, so a later action re-drains and sees every row. (A single-use source cannot be replayed
        // after a cancelled first drain — an inherent tradeoff of an interruptible drain, tracked by #438.)
        var rows = new List<Row> { new(Schema, 1), new(Schema, 2), new(Schema, 3) };
        var sequence = MemoizedRowSequence.Wrap(rows);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => sequence.Snapshot(cts.Token));

        IReadOnlyList<Row> snapshot = sequence.Snapshot(CancellationToken.None);
        Assert.Equal(3, snapshot.Count);
    }

    [Fact]
    public void Snapshot_AlreadyCancelledToken_DoesNotReturnCachedSnapshot()
    {
        // Take the snapshot once (uncancelled), then a later cancelled caller must throw rather than be
        // handed the cached rows — cancellation is honored on every return path, not just mid-drain.
        var sequence = MemoizedRowSequence.Wrap(new List<Row> { new(Schema, 1) });
        Assert.Single(sequence.Snapshot(CancellationToken.None));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => sequence.Snapshot(cts.Token));
    }
}
