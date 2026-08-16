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
        (CdfPreRangeConcurrencyProbeBackend p32, Exception? e32) = await RunAsync(floor: 6, bound: 32);
        Assert.Null(e1);
        Assert.Null(e32);

        long[] subFloor1 = p1.CommitVersionsRead.Where(v => v < 6).ToArray();
        long[] subFloor32 = p32.CommitVersionsRead.Where(v => v < 6).ToArray();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, subFloor1);
        Assert.Equal(subFloor1, subFloor32); // identical sub-floor set across bounds
        for (long v = 1; v <= 5; v++)
        {
            Assert.Equal(1, p1.CommitReadsOf(v));  // exactly once
            Assert.Equal(1, p32.CommitReadsOf(v));
        }
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
            }

            Dispose();
            Directory.CreateDirectory(_root);
        }
    }

    [Fact]
    public async Task CallerCancel_FailsClosed_AndIsNotOverwrittenByAnOffender()
    {
        // A forged offender at v1 AND the caller cancels mid-fan-out (every read is delayed). The gate must
        // surface OperationCanceledException — NOT the offender's exception (the min-fault re-throw is on the
        // normal path after the drain, never from a finally) — and must drain (no orphaned read).
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
}

