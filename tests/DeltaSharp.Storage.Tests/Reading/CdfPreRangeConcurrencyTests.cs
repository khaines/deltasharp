using System.Text;
using System.Text.Json;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// #808: the bounded-concurrency fan-out of the below-floor CDF pre-range column-mapping identity scan. These
/// drive the gate through a <see cref="ChangeFeedReader"/> built on a <see cref="DeltaLog"/> with an explicit
/// <c>preRangeScanConcurrency</c> and a <see cref="CdfPreRangeConcurrencyProbeBackend"/>, so the achieved
/// concurrency, exactly-once coverage-neutrality, deterministic min-faulting-version, fail-closed ordering and
/// bound=1 equivalence are all observable. A compacting checkpoint at the range floor makes every commit below
/// it a SUB-FLOOR disk read (the fan-out set); the range start version is benign so the read resolves.
/// </summary>
public sealed class CdfPreRangeConcurrencyTests : IDisposable
{
    private readonly string _root;

    public CdfPreRangeConcurrencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdf-prerange-conc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // Clean end identity X: id→1, name→2. A forged sub-floor commit swaps to id→2, name→1.
    private static string SchemaJson(int idForId, int idForName) =>
        "{\"type\":\"struct\",\"fields\":["
        + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
        + "{\"delta.columnMapping.id\":" + idForId + ",\"delta.columnMapping.physicalName\":\"col-A\"}},"
        + "{\"name\":\"name\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
        + "{\"delta.columnMapping.id\":" + idForName + ",\"delta.columnMapping.physicalName\":\"col-B\"}}]}";

    private static string MetaLine(int idForId, int idForName) =>
        "{\"metaData\":{\"id\":\"rt\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
        + "\"schemaString\":" + JsonSerializer.Serialize(SchemaJson(idForId, idForName))
        + ",\"partitionColumns\":[],\"configuration\":{"
        + "\"delta.columnMapping.mode\":\"id\",\"delta.columnMapping.maxColumnId\":\"2\","
        + "\"delta.enableChangeDataFeed\":\"true\"}}}";

    // Builds a checkpoint@floor (clean identity X, CDF on) with `subFloorCount` surviving sub-floor commits
    // (v1..v{floor-1}); a forged version in `forged` swaps identity. The range-start commit v{floor} is benign.
    private async Task BuildAsync(int floor, int subFloorCount, params long[] forged)
    {
        var forgedSet = new HashSet<long>(forged);
        using var backend = new LocalFileSystemBackend(_root);
        await DeltaTestHarness.WriteCheckpointAsync(backend, floor, new CheckpointFixture()
            .Protocol(3, 7, new[] { "columnMapping" }, new[] { "columnMapping", "changeDataFeed" })
            .Metadata("rt", SchemaJson(1, 2), partitionColumns: null, configuration: new[]
            {
                ("delta.columnMapping.mode", "id"),
                ("delta.columnMapping.maxColumnId", "2"),
                ("delta.enableChangeDataFeed", "true"),
            }));
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, floor);
        for (long v = 1; v <= subFloorCount; v++)
        {
            string line = forgedSet.Contains(v) ? MetaLine(2, 1) : MetaLine(1, 2);
            await backend.PutIfAbsentAsync(
                "_delta_log/" + v.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".json",
                Encoding.UTF8.GetBytes(line + "\n"), CancellationToken.None);
        }

        // The range start (== floor) benign commit so the resolved range has a readable start version.
        await backend.PutIfAbsentAsync(
            "_delta_log/" + floor.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".json",
            Encoding.UTF8.GetBytes(MetaLine(1, 2) + "\n"), CancellationToken.None);
    }

    // Drives the CDF read of range [floor, floor] with an explicit fan-out bound over the probe backend.
    private async Task<(CdfPreRangeConcurrencyProbeBackend Probe, Exception? Error)> RunAsync(
        int floor, int bound, Action<CdfPreRangeConcurrencyProbeBackend>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var inner = new LocalFileSystemBackend(_root);
        var probe = new CdfPreRangeConcurrencyProbeBackend(inner);
        configure?.Invoke(probe);
        var log = new DeltaLog(probe, DeltaLog.MaxLogObjectBytes, preRangeScanConcurrency: bound);
        var reader = new ChangeFeedReader(probe, inner.TableIdentity, log, new ParquetFileReader());

        Exception? error = null;
        try
        {
            DeltaChangeFeedInfo info = await reader.ResolveAsync(
                DeltaChangeFeedRange.FromVersion(floor, floor), cancellationToken).ConfigureAwait(false);
            await foreach (ColumnBatch _ in reader.ReadAsync(info, cancellationToken).ConfigureAwait(false))
            {
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            probe.MarkGateReturned();
        }

        return (probe, error);
    }

    [Fact]
    public async Task MaxInFlight_EqualsMinOfBoundAndBelowFloorCount()
    {
        await BuildAsync(floor: 6, subFloorCount: 5); // v1..v5 sub-floor (clean), v6 benign start
        // Delay every sub-floor read so the in-flight window is observable.
        void Slow(CdfPreRangeConcurrencyProbeBackend p)
        {
            for (long v = 1; v <= 5; v++)
            {
                p.Delay(v, TimeSpan.FromMilliseconds(60));
            }
        }

        (CdfPreRangeConcurrencyProbeBackend p4, Exception? e4) = await RunAsync(floor: 6, bound: 4, Slow);
        Assert.Null(e4); // clean table → passes
        Assert.Equal(4, p4.MaxInFlightCommits); // min(4, 5)

        (CdfPreRangeConcurrencyProbeBackend p1, Exception? e1) = await RunAsync(floor: 6, bound: 1, Slow);
        Assert.Null(e1);
        Assert.Equal(1, p1.MaxInFlightCommits); // bound=1 → strictly sequential
    }

    [Fact]
    public async Task ExactlyOnce_CoverageNeutral_AcrossBounds()
    {
        // Coverage-neutrality: every below-floor commit is read EXACTLY ONCE, and the validated version SET is
        // identical at bound=1 and bound=32 (the schedule changes, the set does not — guards a fan-out that
        // drops or double-reads a version, the #782 double-read regression class).
        await BuildAsync(floor: 6, subFloorCount: 5);

        (CdfPreRangeConcurrencyProbeBackend p1, Exception? e1) = await RunAsync(floor: 6, bound: 1);
        (CdfPreRangeConcurrencyProbeBackend p4, Exception? e4) = await RunAsync(floor: 6, bound: 4);
        (CdfPreRangeConcurrencyProbeBackend p32, Exception? e32) = await RunAsync(floor: 6, bound: 32);
        Assert.Null(e1);
        Assert.Null(e4);
        Assert.Null(e32);

        long[] subFloor1 = p1.CommitVersionsRead.Where(v => v < 6).ToArray();
        long[] subFloor4 = p4.CommitVersionsRead.Where(v => v < 6).ToArray();
        long[] subFloor32 = p32.CommitVersionsRead.Where(v => v < 6).ToArray();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, subFloor1);
        Assert.Equal(subFloor1, subFloor4);  // identical sub-floor set across bounds
        Assert.Equal(subFloor1, subFloor32);
        for (long v = 1; v <= 5; v++)
        {
            Assert.Equal(1, p1.CommitReadsOf(v));  // exactly once
            Assert.Equal(1, p4.CommitReadsOf(v));
            Assert.Equal(1, p32.CommitReadsOf(v));
        }

        // The range-start commit (v6) is read exactly once at every bound (guards a #782-style double-read of the
        // start commit specifically, which the sub-floor filter above would not catch).
        Assert.Equal(1, p1.CommitReadsOf(6));
        Assert.Equal(1, p4.CommitReadsOf(6));
        Assert.Equal(1, p32.CommitReadsOf(6));
    }

    [Fact]
    public async Task NoBelowFloor_FanOutIssuesZeroDiskReads()
    {
        // A range whose floor == start with no surviving sub-floor commits: the fan-out set is empty, so it
        // issues zero SUB-FLOOR disk reads and adds no fan-out overhead (the range-start commit is still read).
        await BuildAsync(floor: 2, subFloorCount: 0);
        (CdfPreRangeConcurrencyProbeBackend probe, Exception? error) = await RunAsync(floor: 2, bound: 16);
        Assert.Null(error);
        Assert.DoesNotContain(probe.CommitVersionsRead, v => v < 2); // zero below-floor reads
    }

    [Fact]
    public async Task DeterministicMin_UnderLatencySkew_NamesTheSmallestOffender()
    {
        // Two forged sub-floor offenders v1 and v4. Delay v1 (the LOW offender) MORE than v4 so v4 completes
        // FIRST. A first-failure-wins fan-out would name v4; the min-faulting-version reduction must name v1,
        // at every bound. Repeated to make the skew-independence a property.
        for (int i = 0; i < 3; i++)
        {
            await BuildAsync(floor: 6, subFloorCount: 5, forged: new long[] { 1, 4 });
            void Skew(CdfPreRangeConcurrencyProbeBackend p)
            {
                p.Delay(1, TimeSpan.FromMilliseconds(120)); // low offender finishes LAST
                p.Delay(4, TimeSpan.FromMilliseconds(10));  // high offender finishes first
            }

            foreach (int bound in new[] { 4, 32 })
            {
                (CdfPreRangeConcurrencyProbeBackend probe, Exception? error) = await RunAsync(floor: 6, bound: bound, Skew);
                Assert.NotNull(error);
                Assert.Matches(@"version 1\b", error!.Message);   // the numeric minimum
                Assert.DoesNotMatch(@"version 4\b", error.Message); // never the higher, faster-completing offender
                Assert.Equal(0, probe.InFlightCommits);            // fully drained — no orphaned read
                Assert.Equal(0, probe.CommitOpensAfterGateReturned); // no read starts after the gate returns
            }

            Dispose();
            Directory.CreateDirectory(_root);
        }
    }

    [Fact]
    public async Task CallerCancel_FailsClosed_AndDrainsWithoutOrphanedRead()
    {
        // Caller cancels mid-fan-out (every read delayed past the cancel) with a forged offender present but not
        // yet validated. The gate surfaces OperationCanceledException, drains (InFlightCommits==0), and starts no
        // read after returning. (The companion test below covers the case where the offense IS already recorded.)
        await BuildAsync(floor: 6, subFloorCount: 5, forged: new long[] { 1 });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        (CdfPreRangeConcurrencyProbeBackend probe, Exception? error) = await RunAsync(
            floor: 6, bound: 16,
            configure: p =>
            {
                for (long v = 1; v <= 5; v++)
                {
                    p.Delay(v, TimeSpan.FromMilliseconds(400)); // reads outlive the cancel deadline
                }
            },
            cancellationToken: cts.Token);

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(0, probe.InFlightCommits); // drained before returning
        Assert.Equal(0, probe.CommitOpensAfterGateReturned);
    }

    [Fact]
    public async Task InfraFault_FailsClosed_AndLowerOffenseOutranksHigherInfraFault()
    {
        // A transient infra fault (IOException) at a HIGH version must fail the gate closed (never swallowed).
        // With a genuine identity offense at a LOWER version, the min-faulting-version reduction surfaces the
        // LOWER offense — the infra fault neither masks nor pre-empts a smaller genuine offender.
        await BuildAsync(floor: 6, subFloorCount: 5, forged: new long[] { 2 }); // identity offense at v2
        (CdfPreRangeConcurrencyProbeBackend _, Exception? error) = await RunAsync(
            floor: 6, bound: 16, configure: p => p.Fault(4, () => new IOException("transient")));
        Assert.NotNull(error);
        Assert.Matches(@"version 2\b", error!.Message); // the lower genuine offense, not the v4 infra fault

        // Infra fault alone (no offense) → the whole read still fails closed (not swallowed).
        Dispose();
        Directory.CreateDirectory(_root);
        await BuildAsync(floor: 6, subFloorCount: 5);
        (CdfPreRangeConcurrencyProbeBackend _, Exception? infraOnly) = await RunAsync(
            floor: 6, bound: 16, configure: p => p.Fault(3, () => new IOException("transient")));
        Assert.NotNull(infraOnly); // fails closed rather than passing with an unvalidated version
    }

    [Fact]
    public async Task Bound1_SequentialEquivalence_AscendingReadOrderAndSameVerdict()
    {
        // The kill-switch: bound=1 reads the sub-floor set in ascending order and reaches the same verdict as a
        // high bound (a clean table passes; the read set is identical).
        await BuildAsync(floor: 6, subFloorCount: 5);
        (CdfPreRangeConcurrencyProbeBackend probe, Exception? error) = await RunAsync(floor: 6, bound: 1);
        Assert.Null(error);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, probe.CommitVersionsRead.Where(v => v < 6).ToArray());
        Assert.Equal(1, probe.MaxInFlightCommits);
    }

    [Fact]
    public async Task InfraFaultAtMinVersion_SurfacesInfraException_AndReadsNoHigherVersion()
    {
        // The §3.4 discriminator the whole min-by-version UNIFICATION rests on: an infra fault at the LOW/min
        // version (v2) with a genuine offense at a HIGH version (v5). A "prioritise-offenses" scheme would surface
        // the v5 offense and, at bound=1, keep reading past v2 (an I/O storm). The unified reduction must surface
        // the v2 INFRA exception and, at bound=1, read no version > v2 (skip-not-yet-started prunes v3..v5).
        await BuildAsync(floor: 6, subFloorCount: 5, forged: new long[] { 5 });
        (CdfPreRangeConcurrencyProbeBackend probe, Exception? error) = await RunAsync(
            floor: 6, bound: 1, configure: p => p.Fault(2, () => new IOException("infra-at-min")));

        Assert.NotNull(error);
        Assert.Contains("infra-at-min", DescribeChain(error!)); // the v2 infra fault, not the v5 offense
        Assert.DoesNotMatch(@"version 5\b", DescribeChain(error!));
        Assert.DoesNotContain(probe.CommitVersionsRead, v => v is > 2 and < 6); // no read past the min → no I/O storm
    }

    [Fact]
    public async Task InfraVsInfra_DeterministicMinUnderSkew_NamesTheSmallestFaultingVersion()
    {
        // The infra path must be as deterministic as the offense path: two IOExceptions, the LOWER delayed so the
        // HIGHER completes first. A first-fault-wins infra capture would surface v4; the min reduction names v2.
        foreach (int bound in new[] { 4, 32 })
        {
            await BuildAsync(floor: 6, subFloorCount: 5);
            (CdfPreRangeConcurrencyProbeBackend _, Exception? error) = await RunAsync(
                floor: 6, bound: bound, configure: p =>
                {
                    p.Fault(2, () => new IOException("infra-low"));
                    p.Fault(4, () => new IOException("infra-high"));
                    p.Delay(2, TimeSpan.FromMilliseconds(120)); // low faults LAST
                    p.Delay(4, TimeSpan.FromMilliseconds(10));  // high faults first
                });
            Assert.NotNull(error);
            Assert.Contains("infra-low", DescribeChain(error!));
            Assert.DoesNotContain("infra-high", DescribeChain(error!));
            Dispose();
            Directory.CreateDirectory(_root);
        }
    }

    [Fact]
    public async Task CallerCancel_DoesNotOverwriteARecordedOffense_AndLeavesNoOrphanedRead()
    {
        // The §2.6 hazard the vacuous-cancel test misses: an offense IS recorded into the reduction, then the
        // caller cancels while siblings are in flight. The highest sub-floor commit (v5) offends with ZERO delay
        // so its DeltaProtocolException is genuinely recorded (min=5); v1..v4 are delayed far past the cancel.
        // The caller OCE must still surface (re-thrown from the drained fan-out, NEVER overwritten by the min
        // reduction on the normal path) — and no read may start after the gate returned.
        await BuildAsync(floor: 6, subFloorCount: 5, forged: new long[] { 1, 2, 3, 4, 5 });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        (CdfPreRangeConcurrencyProbeBackend probe, Exception? error) = await RunAsync(
            floor: 6, bound: 16,
            configure: p =>
            {
                for (long v = 1; v <= 4; v++)
                {
                    p.Delay(v, TimeSpan.FromMilliseconds(600)); // outlive the cancel deadline
                }
                // v5 has no delay → records its offense (min=5) BEFORE the cancel fires.
            },
            cancellationToken: cts.Token);

        Assert.IsAssignableFrom<OperationCanceledException>(error); // OCE wins over the recorded v5 offense
        Assert.Equal(0, probe.InFlightCommits);
        Assert.Equal(0, probe.CommitOpensAfterGateReturned); // no orphaned read after the gate returned
    }

    [Fact]
    public async Task Replenishment_CountExceedsBound_AllReadExactlyOnce_NoPermitLeak()
    {
        // 8 sub-floor reads at bound=2 REQUIRE the semaphore to replenish across four waves; a leaked permit
        // would deadlock (the test would hang). All 8 are read exactly once and peak in-flight is exactly 2.
        // Each read is delayed so the two-in-flight window is observable (else reads retire before the next launch).
        await BuildAsync(floor: 9, subFloorCount: 8);
        (CdfPreRangeConcurrencyProbeBackend probe, Exception? error) = await RunAsync(
            floor: 9, bound: 2, configure: p =>
            {
                for (long v = 1; v <= 8; v++)
                {
                    p.Delay(v, TimeSpan.FromMilliseconds(60));
                }
            });
        Assert.Null(error);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8 }, probe.CommitVersionsRead.Where(v => v < 9).ToArray());
        for (long v = 1; v <= 8; v++)
        {
            Assert.Equal(1, probe.CommitReadsOf(v));
        }

        Assert.Equal(2, probe.MaxInFlightCommits);
    }

    [Fact]
    public async Task CrossBranch_ObserverOffenderDoesNotPreemptLowerDiskOffender()
    {
        // The §3.3 cross-branch determinism oracle + the §2.2 "record-not-throw" correction. The range starts
        // ABOVE the checkpoint floor so an intermediate commit (v4) is OBSERVER-PROVEN (validated in-memory), while
        // a lower sub-floor stray (v1) is a DISK read. Both are forged. A regression that threw the observer
        // verdict INLINE would surface v4; the min-by-version reduction across branches must surface v1.
        (CdfPreRangeConcurrencyProbeBackend probe, Exception? error) = await RunCrossBranchAsync(bound: 16);

        Assert.NotNull(error);
        Assert.Matches(@"version 1\b", DescribeChain(error!)); // the numeric minimum, from the DISK branch
        Assert.DoesNotMatch(@"version 4\b", DescribeChain(error!));
        // v4 ∈ (floor=3, start=5) is OBSERVER-PROVEN: the start-snapshot reconstruction (DeltaLog.cs:739-740)
        // replays v4→v5, so the gate validates it via the in-memory record-not-throw branch (DeltaLog.cs:901),
        // never a gate disk-read. A regression that threw that observer verdict INLINE would surface v4 above,
        // because it would never reach the fan-out disk-read of the lower v1.
    }

    // Flattens an exception chain (message + type names) so an assertion is robust to the CDF read wrapping the
    // gate's surfaced exception.
    private static string DescribeChain(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            sb.Append(e.GetType().Name).Append(": ").Append(e.Message).Append(" | ");
        }

        return sb.ToString();
    }

    // Builds a table whose CDF range starts ABOVE the reconstructable checkpoint floor, so a pre-range commit is
    // observer-proven while a lower sub-floor stray is a disk read. checkpoint@3 (clean identity, CDF on);
    // sub-floor strays v1 (forged) + v2 (clean); observer-proven v4 (forged) replayed to reconstruct the start
    // snapshot; benign range-start v5 (clean) — so the end identity is clean and both forges are offenses.
    private async Task<(CdfPreRangeConcurrencyProbeBackend Probe, Exception? Error)> RunCrossBranchAsync(int bound)
    {
        using (var seed = new LocalFileSystemBackend(_root))
        {
            await DeltaTestHarness.WriteCheckpointAsync(seed, 3, new CheckpointFixture()
                .Protocol(3, 7, new[] { "columnMapping" }, new[] { "columnMapping", "changeDataFeed" })
                .Metadata("rt", SchemaJson(1, 2), partitionColumns: null, configuration: new[]
                {
                    ("delta.columnMapping.mode", "id"),
                    ("delta.columnMapping.maxColumnId", "2"),
                    ("delta.enableChangeDataFeed", "true"),
                }));
            await DeltaTestHarness.WriteLastCheckpointAsync(seed, 3);
            await WriteCommitAsync(seed, 1, MetaLine(2, 1)); // sub-floor stray, forged → DISK offender (min)
            await WriteCommitAsync(seed, 2, MetaLine(1, 2)); // sub-floor stray, clean → DISK benign
            await WriteCommitAsync(seed, 4, MetaLine(2, 1)); // observer-proven, forged → higher offender
            await WriteCommitAsync(seed, 5, MetaLine(1, 2)); // range start, clean → end identity X
        }

        var inner = new LocalFileSystemBackend(_root);
        var probe = new CdfPreRangeConcurrencyProbeBackend(inner);
        var log = new DeltaLog(probe, DeltaLog.MaxLogObjectBytes, preRangeScanConcurrency: bound);
        var reader = new ChangeFeedReader(probe, inner.TableIdentity, log, new ParquetFileReader());

        Exception? error = null;
        try
        {
            DeltaChangeFeedInfo info = await reader.ResolveAsync(
                DeltaChangeFeedRange.FromVersion(5, 5), CancellationToken.None).ConfigureAwait(false);
            await foreach (ColumnBatch _ in reader.ReadAsync(info, CancellationToken.None).ConfigureAwait(false))
            {
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            probe.MarkGateReturned();
        }

        return (probe, error);
    }

    private static Task WriteCommitAsync(IStorageBackend backend, long version, string line) =>
        backend.PutIfAbsentAsync(
            "_delta_log/" + version.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".json",
            Encoding.UTF8.GetBytes(line + "\n"), CancellationToken.None).AsTask();
}

