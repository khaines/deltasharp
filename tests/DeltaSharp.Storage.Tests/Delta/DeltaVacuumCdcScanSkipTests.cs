using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// #809: the log-derived, fail-closed skip of VACUUM's in-window <c>cdc</c> protection scan. These oracles
/// pin the data-loss-critical contract: the scan is elided ONLY when the retained protocol history PROVES
/// Change Data Feed was inactive across the full in-window range (co-extensive with the unconditional scan on
/// every conforming table), and every trap that could delete a live <c>_change_data/</c> file — an inherited
/// CDF-on version (the derive-prevailing trap), a CDF-enabled in-window commit, a toggled window — scans and
/// protects. The kill-switch (default OFF) is byte-identical to today's unconditional scan.
/// </summary>
public sealed class DeltaVacuumCdcScanSkipTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Recent = Now.AddDays(-1);          // within the log-retention window
    private static readonly DateTime OldFile = Now.AddDays(-60).UtcDateTime;  // aged data file → only a ref protects it
    private static readonly TimeSpan Retention = TimeSpan.FromHours(168);

    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public DeltaVacuumCdcScanSkipTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vacuum-cdcskip-" + Guid.NewGuid().ToString("N"));
        _backend = new LocalFileSystemBackend(_root);
    }

    public void Dispose()
    {
        _backend.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // A vacuum over the table, with commit mtimes stamped in-window, wrapped in a read-counting backend so a
    // test can prove the scan was (or was not) run by counting the second read of an in-window commit.
    private (DeltaVacuum Vacuum, CountingStorageBackend Counting) Build(bool skipEnabled, params long[] inWindowVersions)
    {
        var timestamps = new (long Version, DateTimeOffset Modified)[inWindowVersions.Length];
        for (int i = 0; i < inWindowVersions.Length; i++)
        {
            timestamps[i] = (inWindowVersions[i], Recent);
        }

        var counting = new CountingStorageBackend(DeltaTestHarness.WithCommitTimestamps(_backend, timestamps));
        var vacuum = new DeltaVacuum(
            counting, policy: null, logger: null, telemetry: null,
            timeProvider: new FixedTimeProvider(Now), cdcScanSkipEnabled: skipEnabled);
        return (vacuum, counting);
    }

    private async Task WriteDataFileAsync(string path, DateTime modified)
    {
        await _backend.PutIfAbsentAsync(path, new byte[] { 1, 2, 3 }, CancellationToken.None);
        string full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        File.SetLastWriteTimeUtc(full, DateTime.SpecifyKind(modified, DateTimeKind.Utc));
    }

    [Fact]
    public async Task NeverCdf_FullReplay_InWindow_ElidesScan_DeletionSetMatchesUnconditionalScan()
    {
        // Never-CDF table (full JSON replay — no checkpoint, so the observer covers [0, latest]). A genuine
        // orphan is present so the deletion set is NON-EMPTY (co-extensiveness is proven non-vacuously). The
        // skip must fire (the scan's in-window re-reads are elided) AND the deletion set must be IDENTICAL to
        // the unconditional-scan control (the null-observer path). Skipping never changes what is reclaimed.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("active.parquet"));
        await WriteDataFileAsync("active.parquet", OldFile);
        await WriteDataFileAsync("old-orphan.parquet", OldFile); // referenced by nothing → reclaimable

        (DeltaVacuum scanVacuum, CountingStorageBackend scanCounting) = Build(skipEnabled: false, 0, 1);
        VacuumResult control = await scanVacuum.VacuumAsync(Retention, dryRun: true);

        (DeltaVacuum skipVacuum, CountingStorageBackend skipCounting) = Build(skipEnabled: true, 0, 1);
        VacuumResult skipped = await skipVacuum.VacuumAsync(Retention, dryRun: true);

        // Co-extensive: identical (non-empty) deletion candidate set (dry-run, so neither run mutates the
        // shared backend — the skip never changes WHAT is reclaimed, only whether the scan is read).
        Assert.Contains("old-orphan.parquet", control.DeletablePaths);
        Assert.Equal(
            control.DeletablePaths.OrderBy(p => p, StringComparer.Ordinal),
            skipped.DeletablePaths.OrderBy(p => p, StringComparer.Ordinal));

        // Skip fired: the unconditional scan re-reads each in-window commit; the skip elides that, so the skip
        // run reads the in-window commits strictly FEWER times than the control.
        Assert.True(
            skipCounting.CommitReadsOf(1) < scanCounting.CommitReadsOf(1),
            $"expected skip to elide the scan's re-read of commit 1 (skip={skipCounting.CommitReadsOf(1)}, scan={scanCounting.CommitReadsOf(1)})");
    }

    [Fact]
    public async Task InheritedCdfOn_NoInWindowMetadata_Scans_AndProtectsCdc()
    {
        // THE derive-prevailing data-loss trap. CDF is enabled at v0 and INHERITED at v1 (v1 carries a `cdc`
        // action but NO metaData of its own). A bare stored-pair lookup would find no observation at v1 →
        // score it CDF-off → wrongly SKIP → delete the live cdc file. The derive-prevailing accessor must
        // carry v0's CDF-on state forward to v1 → SCAN → protect the cdc file.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(),
            DeltaTestHarness.MetadataWithConfig(("delta.enableChangeDataFeed", "true")));
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Cdc("_change_data/x.parquet"));
        await WriteDataFileAsync("active.parquet", OldFile);
        await WriteDataFileAsync("_change_data/x.parquet", OldFile); // aged → only the cdc ref protects it

        (DeltaVacuum vacuum, _) = Build(skipEnabled: true, 0, 1);
        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.DoesNotContain("_change_data/x.parquet", result.DeletedPaths);
        Assert.DoesNotContain("_change_data/x.parquet", result.DeletablePaths);
        Assert.NotNull(await _backend.HeadAsync("_change_data/x.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task CdfEnabledAtInWindowCommit_Scans_AndProtectsCdc()
    {
        // CDF proven ON at an in-window commit (v1 carries metaData enabling CDF + a cdc file). The predicate
        // scores v1 CDF-on at a boundary → SCAN → protect.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.MetadataWithConfig(("delta.enableChangeDataFeed", "true")),
            DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Cdc("_change_data/x.parquet"));
        await WriteDataFileAsync("active.parquet", OldFile);
        await WriteDataFileAsync("_change_data/x.parquet", OldFile);

        (DeltaVacuum vacuum, _) = Build(skipEnabled: true, 0, 1);
        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.DoesNotContain("_change_data/x.parquet", result.DeletedPaths);
        Assert.NotNull(await _backend.HeadAsync("_change_data/x.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task EnableThenDisable_InWindow_Scans_AndProtectsCdc()
    {
        // AC-1: CDF on at v1 (writes a cdc file), off again at v2, both in-window. Even though the prevailing
        // state at the window boundaries is off, v1's boundary proves CDF-on → SCAN → the cdc file is protected.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.MetadataWithConfig(("delta.enableChangeDataFeed", "true")),
            DeltaTestHarness.Cdc("_change_data/x.parquet"));
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 2, DeltaTestHarness.MetadataWithConfig(("delta.enableChangeDataFeed", "false")),
            DeltaTestHarness.Add("active.parquet"));
        await WriteDataFileAsync("active.parquet", OldFile);
        await WriteDataFileAsync("_change_data/x.parquet", OldFile);

        (DeltaVacuum vacuum, _) = Build(skipEnabled: true, 0, 1, 2);
        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.DoesNotContain("_change_data/x.parquet", result.DeletedPaths);
        Assert.NotNull(await _backend.HeadAsync("_change_data/x.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task KillSwitchOff_AlwaysScans_ByteIdenticalToToday()
    {
        // Default OFF: the scan always runs (its in-window re-reads happen), and the result equals the
        // unconditional scan — the escape hatch reverts to today's behavior.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("active.parquet"));
        await WriteDataFileAsync("active.parquet", OldFile);
        await WriteDataFileAsync("old-orphan.parquet", OldFile);

        (DeltaVacuum vacuum, CountingStorageBackend counting) = Build(skipEnabled: false, 0, 1);
        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.Contains("old-orphan.parquet", result.DeletedPaths);
        // The scan ran: the in-window commit was read by BOTH the reconstruction and the scan.
        Assert.True(counting.CommitReadsOf(1) >= 2, $"expected the scan to re-read commit 1 (reads={counting.CommitReadsOf(1)})");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
