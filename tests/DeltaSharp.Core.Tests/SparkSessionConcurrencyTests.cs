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
