using System.Diagnostics;
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
        // B2 fix: GetOrCreate makes its reuse decision — reading the candidate session's IsActive —
        // UNDER _globalLock, and Stop() performs its state transition under that SAME _globalLock. The
        // reuse decision and the stop transition are therefore mutually exclusive: a session GetOrCreate
        // observed active while committing to reuse it cannot be flipped to stopped before GetOrCreate
        // returns it, so GetOrCreate never hands back a session it decided to REUSE while that session
        // was stopped — it either reuses a still-active session or, seeing it stopped, creates a fresh
        // one.
        //
        // This test is fully DETERMINISTIC, not the old 25,000-iteration probabilistic stress test. It
        // uses the internal ReuseRaceProbe seam to pause the getter at the precise reuse window (after it
        // read the candidate's IsActive under _globalLock and committed to reuse, before it returns) and
        // drive a concurrent Stop on another thread. While paused the getter still holds _globalLock, so:
        //   * With the fix, the racing Stop's state transition needs the same _globalLock and cannot run
        //     while the getter holds it — the seed stays active for the whole in-lock decision window
        //     (the getter observes IsActive == true throughout), and only the post-return Stop stops it
        //     (legal). The probe's watch deterministically times out with the seed still active.
        //   * Without the fix (Stop's transition moved outside _globalLock), the racing Stop flips the
        //     seed to stopped WHILE the getter holds the lock mid-decision — the getter's watch observes
        //     IsActive == false and GetOrCreate would return a reused-but-stopped session (a violation).
        // EXACT ORACLE: the seed the getter committed to reuse stayed active for the entire in-lock
        // decision window. This never false-positives on a legitimate post-return Stop, because such a
        // Stop can only complete once the getter releases _globalLock (i.e. after the window closes),
        // and the oracle observes state strictly inside the window rather than sampling IsActive after
        // the lock is released (which is what made the old stress oracle racy).
        const int iterations = 6;
        const int probeWatchMs = 200;

        for (int i = 0; i < iterations; i++)
        {
            SparkSession.ClearActiveSession();
            SparkSession.ClearDefaultSession();
            SparkSession seed = SparkSession.Builder().AppName("race").GetOrCreate();

            using var getterInWindow = new SemaphoreSlim(0, 1);
            int probeFired = 0;
            bool seedStoppedDuringDecision = false;
            SparkSession? returned = null;

            seed.ReuseRaceProbe = () =>
            {
                // Fire exactly once, for the raced reuse only.
                if (Interlocked.Exchange(ref probeFired, 1) != 0)
                {
                    return;
                }

                getterInWindow.Release();   // getter is now post-decision, pre-return, holding _globalLock

                // Watch the seed's active state for the whole in-lock decision window. With the fix a
                // racing Stop cannot transition it while we hold _globalLock, so it stays active until we
                // time out; without the fix the unlocked transition flips it and we observe it here.
                Stopwatch sw = Stopwatch.StartNew();
                while (seed.IsActive && sw.ElapsedMilliseconds < probeWatchMs)
                {
                    Thread.Yield();
                }

                seedStoppedDuringDecision = !seed.IsActive;
            };

            var getter = new Thread(() =>
            {
                returned = SparkSession.Builder().AppName("race").GetOrCreate();
            });
            var stopper = new Thread(() =>
            {
                getterInWindow.Wait();   // wait until the getter is in the reuse window
                seed.Stop();             // races the in-flight reuse decision
            });

            getter.Start();
            stopper.Start();
            getter.Join();
            stopper.Join();

            seed.ReuseRaceProbe = null;

            // The getter must have taken the reuse path (returned the seed) — that is the path B2 guards.
            Assert.Same(seed, returned);

            // Sanity: the race was real — the stopper actually stopped the seed once the getter released
            // the lock (a legitimate post-return Stop), so the interleaving was genuinely exercised.
            Assert.False(seed.IsActive);

            // The violation: a concurrent Stop transitioned the seed to stopped WHILE the getter held
            // _globalLock mid-decision, so GetOrCreate reused a session that became stopped before it
            // returned. With the fix this is impossible because the transition needs the same lock.
            Assert.False(
                seedStoppedDuringDecision,
                "GetOrCreate reused a session a concurrent Stop transitioned to stopped mid-decision (B2 TOCTOU).");
        }

        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
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
