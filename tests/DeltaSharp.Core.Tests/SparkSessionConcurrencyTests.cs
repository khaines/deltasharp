using System.Threading;
using Xunit;

namespace DeltaSharp.Core.Tests;

/// <summary>
/// Concurrency coverage for the <see cref="SparkSession"/> lifecycle/tracking model: the B2 TOCTOU
/// race between <see cref="SparkSession.Stop"/> and <see cref="SparkSessionBuilder.GetOrCreate"/>,
/// the thread-local active vs. process-wide default split, and repeated create/stop cycles proving
/// no static-state leak. See <c>docs/engineering/design/sparksession-lifecycle.md</c>.
/// </summary>
[Collection(SparkSessionTestCollection.Name)]
public sealed class SparkSessionConcurrencyTests
{
    public SparkSessionConcurrencyTests()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
    }

    // ----- B2: GetOrCreate must never reuse a session it observed as stopped (TOCTOU) -----

    [Fact]
    public void GetOrCreate_RacingStop_NeverReusesAStoppedSession()
    {
        // B2 fix: Stop() now performs its state transition under the same _globalLock that
        // GetOrCreate uses for its reuse decision, so a concurrent Stop cannot invalidate the
        // in-lock IsActive check mid-decision. GetOrCreate therefore either reuses a still-active
        // session or, seeing it stopped, creates a fresh (active) one — it never returns a session it
        // decided to REUSE while that session was stopped.
        //
        // This is a high-iteration concurrency STRESS test. A fully deterministic assertion is not
        // possible: reading IsActive after GetOrCreate returns is inherently racy against a legitimate
        // post-return Stop. With the fix the violation count is 0 across the whole run; reverting the
        // transition back outside the lock makes it spike into the thousands (non-vacuity check).
        const int iterations = 25_000;
        int violations = 0;

        for (int i = 0; i < iterations; i++)
        {
            SparkSession.ClearActiveSession();
            SparkSession.ClearDefaultSession();
            SparkSession seed = SparkSession.Builder().AppName("race").GetOrCreate();

            using var ready = new Barrier(2);
            SparkSession? returned = null;
            bool returnedActive = false;

            var getter = new Thread(() =>
            {
                ready.SignalAndWait();
                returned = SparkSession.Builder().AppName("race").GetOrCreate();
                returnedActive = returned.IsActive;
            });
            var stopper = new Thread(() =>
            {
                ready.SignalAndWait();
                seed.Stop();
            });

            getter.Start();
            stopper.Start();
            getter.Join();
            stopper.Join();

            // A violation is a REUSE of the stopped seed: GetOrCreate returned the same instance it
            // started from, yet observed it stopped. A freshly-created session is a different instance
            // and is always active, which is the correct outcome when the seed was already stopped.
            if (ReferenceEquals(returned, seed) && !returnedActive)
            {
                Interlocked.Increment(ref violations);
            }

            seed.Stop();
        }

        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
        Assert.Equal(0, violations);
    }

    // ----- F1: Conf.Set racing Stop must never mutate an already-stopped session (TOCTOU) -----

    [Fact]
    public void Set_RacingStop_NeverMutatesAStoppedSession()
    {
        // F1 fix: Conf.Set performs its stopped-check AND its dictionary write together under the
        // RuntimeConfig gate, and Stop() performs its state transition under that SAME gate (inside
        // _globalLock, preserving the _globalLock -> _gate order). The check+write is therefore atomic
        // w.r.t. the stop transition: a Set that observes the session active under the gate cannot be
        // stopped before it writes, and a Set sequenced after the transition sees STOPPED and throws.
        //
        // This test is fully DETERMINISTIC, not a probabilistic stress test. It uses the internal
        // StopRaceProbe seam to pause the writer at the precise TOCTOU window (after the stopped-check,
        // before the write) and drive a concurrent Stop on another thread. The seam reports whether the
        // Stop was able to COMPLETE before the write:
        //   * With the fix, the writer holds the gate while paused, so the racing Stop blocks on the
        //     gate and cannot complete before the write -> the probe times out (stopBeforeWrite=false),
        //     and the value the writer persists was written while the session was still active (legal).
        //   * Without the fix (revert the in-gate re-check or Stop's gate acquisition), the writer holds
        //     no gate while paused, so the Stop completes (stopBeforeWrite=true) and the writer then
        //     writes onto an already-stopped session -> a violation.
        // The oracle asserts the persisted state against write-time ordering, so it never
        // false-positives on a legitimate write-then-stop.
        const int iterations = 6;
        const int probeTimeoutMs = 400;
        const string raceKey = "race.key";

        for (int i = 0; i < iterations; i++)
        {
            SparkSession.ClearActiveSession();
            SparkSession.ClearDefaultSession();
            SparkSession seed = SparkSession.Builder().AppName("set-race").GetOrCreate();

            using var writerInWindow = new SemaphoreSlim(0, 1);
            using var stopReleased = new SemaphoreSlim(0, 1);
            int probeFired = 0;
            bool stopCompletedBeforeWrite = false;

            seed.Conf.StopRaceProbe = () =>
            {
                // Fire exactly once, for the raced write only.
                if (Interlocked.Exchange(ref probeFired, 1) != 0)
                {
                    return;
                }

                writerInWindow.Release();                              // writer is now post-check, pre-write
                stopCompletedBeforeWrite = stopReleased.Wait(probeTimeoutMs);
            };

            bool setThrew = false;
            var writer = new Thread(() =>
            {
                try
                {
                    seed.Conf.Set(raceKey, "written-during-race");
                }
                catch (SessionStoppedException)
                {
                    setThrew = true;
                }
            });
            var stopper = new Thread(() =>
            {
                writerInWindow.Wait();                                 // wait until writer is in the window
                seed.Stop();                                           // races the in-flight Set
                stopReleased.Release();                                // signal: Stop returned
            });

            writer.Start();
            stopper.Start();
            writer.Join();
            stopper.Join();

            seed.Conf.StopRaceProbe = null;

            bool persisted = seed.Conf.Get(raceKey, null) is not null;

            // Sanity: persistence and a thrown lifecycle error are mutually exclusive (only Set writes).
            Assert.Equal(persisted, !setThrew);

            // The violation: the racing Stop COMPLETED before the write, yet the value was still
            // persisted onto the (now stopped) session. With the fix this is impossible because the
            // writer holds the gate across check+write, so Stop cannot complete first.
            Assert.False(
                stopCompletedBeforeWrite && persisted,
                "Conf.Set mutated an already-stopped session (Stop completed before the write).");

            seed.Stop();
        }

        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
    }

    // ----- Active session is thread-local; default session is process-wide -----

    [Fact]
    public void ActiveSession_IsThreadLocal_WhileDefaultIsProcessWide()
    {
        using SparkSession main = SparkSession.Builder().AppName("thread-local").GetOrCreate();
        Assert.Same(main, SparkSession.GetActiveSession());
        Assert.Same(main, SparkSession.GetDefaultSession());

        SparkSession? otherThreadActiveBefore = main; // sentinel: must be overwritten to null
        SparkSession? otherThreadReused = null;
        SparkSession? otherThreadDefault = null;

        var worker = new Thread(() =>
        {
            // The active session is thread-local — a fresh thread sees none even though one is active
            // on the creating thread.
            otherThreadActiveBefore = SparkSession.GetActiveSession();
            otherThreadDefault = SparkSession.GetDefaultSession();
            // GetOrCreate falls back to the process-wide default and reuses it.
            otherThreadReused = SparkSession.Builder().GetOrCreate();
        });
        worker.Start();
        worker.Join();

        Assert.Null(otherThreadActiveBefore);
        Assert.Same(main, otherThreadDefault);
        Assert.Same(main, otherThreadReused);
    }

    // ----- Repeated create/stop cycles leave no static-state leak -----

    [Fact]
    public void RepeatedGetOrCreateStopCycles_LeaveNoStaticStateLeak()
    {
        SparkSession? previous = null;
        for (int i = 0; i < 250; i++)
        {
            SparkSession spark = SparkSession.Builder().AppName("cycle").GetOrCreate();

            Assert.True(spark.IsActive);
            Assert.Same(spark, SparkSession.GetActiveSession());
            Assert.Same(spark, SparkSession.GetDefaultSession());
            if (previous is not null)
            {
                // Each cycle yields a brand-new instance: the prior session did not leak as the
                // active/default after Stop cleared it.
                Assert.NotSame(previous, spark);
            }

            spark.Stop();

            Assert.False(spark.IsActive);
            Assert.Null(SparkSession.GetActiveSession());
            Assert.Null(SparkSession.GetDefaultSession());

            previous = spark;
        }
    }
}
