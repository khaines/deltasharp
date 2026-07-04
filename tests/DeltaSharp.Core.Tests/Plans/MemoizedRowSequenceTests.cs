using System;
using System.Collections.Generic;
using System.Threading;
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
    public void Snapshot_SecondCallerBlockedOnGate_HonorsItsOwnCancellation()
    {
        // Deterministic (no timers / no thread pool, so it can't flake under CI scheduling): caller 1 takes
        // the drain gate and holds it on a manual signal; caller 2 blocks behind it and is then cancelled
        // EXPLICITLY while blocked. With a plain lock caller 2 would stay blocked until caller 1 released
        // and then return the cached snapshot WITHOUT throwing; the cancellable acquisition makes it observe
        // its own cancellation while still blocked, before caller 1 is released.
        var caller1HasGate = new ManualResetEventSlim();
        var releaseCaller1 = new ManualResetEventSlim();

        IEnumerable<Row> GatedSource()
        {
            caller1HasGate.Set();
            releaseCaller1.Wait();
            yield return new Row(Schema, 1);
        }

        var sequence = MemoizedRowSequence.Wrap(GatedSource());

        // Dedicated threads (NOT Task.Run) so caller 1's blocking hold cannot starve caller 2 or the token.
        var caller1 = new Thread(() => sequence.Snapshot(CancellationToken.None)) { IsBackground = true };
        caller1.Start();
        Assert.True(caller1HasGate.Wait(TimeSpan.FromSeconds(5)), "caller 1 never took the drain gate");

        using var cts = new CancellationTokenSource();
        Exception? thrown = null;
        var caller2Done = new ManualResetEventSlim();
        var caller2 = new Thread(() =>
        {
            try
            {
                sequence.Snapshot(cts.Token);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            finally
            {
                caller2Done.Set();
            }
        })
        { IsBackground = true };
        caller2.Start();

        // Give caller 2 time to block on the gate, then cancel it explicitly.
        Thread.Sleep(200);
        cts.Cancel();

        // Caller 2 must finish (by throwing) WITHOUT caller 1 ever being released.
        Assert.True(
            caller2Done.Wait(TimeSpan.FromSeconds(5)),
            "the blocked caller never observed its cancellation (it waited for caller 1's drain)");
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);

        // Clean up caller 1.
        releaseCaller1.Set();
        caller1.Join(TimeSpan.FromSeconds(5));
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
