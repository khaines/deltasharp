using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Microsoft.Extensions.Logging;
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

        return BuildWithStamps(skipEnabled, timestamps);
    }

    // As Build, but with EXPLICIT per-commit listed mtimes — used to place a commit at a chosen point relative
    // to the two cutoffs (log-retention vs vacuum retention) or to age a recorded transition below the window.
    private (DeltaVacuum Vacuum, CountingStorageBackend Counting) BuildWithStamps(
        bool skipEnabled, (long Version, DateTimeOffset Modified)[] stamps, ILogger<DeltaVacuum>? logger = null)
    {
        var counting = new CountingStorageBackend(DeltaTestHarness.WithCommitTimestamps(_backend, stamps));
        var vacuum = new DeltaVacuum(
            counting, policy: null, logger: logger, telemetry: null,
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

    [Fact]
    public async Task EmptyInWindowSet_AllAgedOut_Skips_ProtectsNothing()
    {
        // Every commit is aged past log retention (none stamped in-window), so the in-window set is EMPTY. The
        // skip fires vacuously (nothing to prove) and is equivalent to the unconditional scan, which also finds
        // no in-window cdc. Both protect nothing; the aged cdc file is correctly reclaimable.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Cdc("_change_data/x.parquet"));
        await WriteDataFileAsync("active.parquet", OldFile);
        await WriteDataFileAsync("_change_data/x.parquet", OldFile);

        // Build with NO in-window stamps → both commits keep their aged (real) mtime → aged out of the window.
        (DeltaVacuum vacuum, _) = Build(skipEnabled: true);
        VacuumResult result = await vacuum.VacuumAsync(Retention, dryRun: true);

        (DeltaVacuum control, _) = Build(skipEnabled: false);
        VacuumResult scan = await control.VacuumAsync(Retention, dryRun: true);

        // Aged cdc file is in-window for NEITHER, so it is a deletion candidate in BOTH (co-extensive).
        Assert.Equal(
            scan.DeletablePaths.OrderBy(p => p, StringComparer.Ordinal),
            result.DeletablePaths.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CandidateInvariance_SkipDecisionIgnoresCandidateListing()
    {
        // The skip predicate is derived SOLELY from the log — never the candidate listing. Adding a stray
        // `_change_data/` orphan candidate (referenced by no cdc action) must NOT change the skip decision on a
        // never-CDF table: it still skips, and the stray is reclaimed exactly as under the unconditional scan.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("active.parquet"));
        await WriteDataFileAsync("active.parquet", OldFile);
        await WriteDataFileAsync("_change_data/stray.parquet", OldFile); // referenced by nothing → reclaimable

        (DeltaVacuum skipVacuum, CountingStorageBackend skipCounting) = Build(skipEnabled: true, 0, 1);
        VacuumResult skipped = await skipVacuum.VacuumAsync(Retention, dryRun: true);

        (DeltaVacuum control, CountingStorageBackend scanCounting) = Build(skipEnabled: false, 0, 1);
        VacuumResult scan = await control.VacuumAsync(Retention, dryRun: true);

        // The stray _change_data/ candidate is reclaimable under BOTH (identical decision), proving the skip
        // never consulted the candidate listing to decide, and the scan was still elided.
        Assert.Contains("_change_data/stray.parquet", skipped.DeletablePaths);
        Assert.Equal(
            scan.DeletablePaths.OrderBy(p => p, StringComparer.Ordinal),
            skipped.DeletablePaths.OrderBy(p => p, StringComparer.Ordinal));
        Assert.True(
            skipCounting.CommitReadsOf(1) < scanCounting.CommitReadsOf(1),
            "expected the scan to be elided regardless of the candidate listing");
    }

    [Fact]
    public async Task TwoCutoffGap_CdfOnCommit_BetweenCutoffs_Scans_AndProtectsCdc()
    {
        // The predicate MUST key on the LOG-retention cutoff (delta.logRetentionDuration ~30d), NOT the vacuum
        // deleted-file-retention cutoff (~7d). A CDF-on/cdc commit whose mtime lands strictly BETWEEN the two
        // cutoffs is in-window for the scan (log retention) — a predicate keyed on the vacuum cutoff would age
        // it out, all-off SKIP, and delete the live change file. Stamp v1 at now−14d (inside [now−30d, now−7d)).
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.MetadataWithConfig(("delta.enableChangeDataFeed", "true")),
            DeltaTestHarness.Cdc("_change_data/gap.parquet"));
        await DeltaTestHarness.WriteCommitAsync(_backend, 2, DeltaTestHarness.Add("active.parquet"));
        await WriteDataFileAsync("active.parquet", OldFile);
        await WriteDataFileAsync("_change_data/gap.parquet", OldFile);

        (DeltaVacuum vacuum, _) = BuildWithStamps(
            skipEnabled: true,
            new[] { (0L, Recent), (1L, Now.AddDays(-14)), (2L, Recent) }); // v1 between the two cutoffs

        VacuumResult result = await vacuum.VacuumAsync(Retention, dryRun: true);

        Assert.DoesNotContain("_change_data/gap.parquet", result.DeletablePaths);
    }

    [Fact]
    public async Task CutoffEqualityBoundary_MtimeExactlyAtLogRetentionCutoff_Scans_AndProtectsCdc()
    {
        // Boundary: the scan keeps a commit whose mtime is EXACTLY at the log-retention cutoff in-window (its
        // skip is `mtime < cutoff`, strict). The predicate's complement must use the same strict `<`, so a
        // mtime == cutoff commit is in-window for the predicate too. A `<=` off-by-one would shrink the
        // predicate set below the scan's → wrong skip. Stamp v1 exactly at now − 30d (the log-retention cutoff).
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.MetadataWithConfig(("delta.enableChangeDataFeed", "true")),
            DeltaTestHarness.Cdc("_change_data/edge.parquet"));
        await DeltaTestHarness.WriteCommitAsync(_backend, 2, DeltaTestHarness.Add("active.parquet"));
        await WriteDataFileAsync("active.parquet", OldFile);
        await WriteDataFileAsync("_change_data/edge.parquet", OldFile);

        // The default log retention is 30 days; place v1's mtime exactly on that cutoff.
        (DeltaVacuum vacuum, _) = BuildWithStamps(
            skipEnabled: true,
            new[] { (0L, Recent), (1L, Now.AddDays(-30)), (2L, Recent) });

        VacuumResult result = await vacuum.VacuumAsync(Retention, dryRun: true);

        Assert.DoesNotContain("_change_data/edge.parquet", result.DeletablePaths);
    }

    [Fact]
    public async Task BelowCoverage_CheckpointSeeded_SubFloorCdcCommit_Scans_AndProtectsCdc()
    {
        // Benefit-envelope boundary (§2.4) + a critical fail-closed guard. A compacting checkpoint@2 seeds the
        // reconstruction's replay floor at v3, so the observer covers only [3, latest]. A SURVIVING sub-floor
        // commit v1.json (below the floor) enabled CDF and wrote a cdc file. It is in-window (recent mtime) but
        // BELOW coverage → TryGetProvenPrevailing returns false → ScanUnproven → SCAN → the cdc file is
        // protected. A mutant that skipped when the in-window set extends below coverage would delete it.
        await DeltaTestHarness.WriteCheckpointAsync(_backend, 2, new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: "t", schemaString: EmptySchemaUnescaped)); // checkpoint bakes CDF-off state
        await DeltaTestHarness.WriteLastCheckpointAsync(_backend, 2);
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.MetadataWithConfig(("delta.enableChangeDataFeed", "true")),
            DeltaTestHarness.Cdc("_change_data/below.parquet")); // surviving sub-floor cdc commit
        await DeltaTestHarness.WriteCommitAsync(_backend, 3, DeltaTestHarness.Add("active.parquet"));
        await WriteDataFileAsync("active.parquet", OldFile);
        await WriteDataFileAsync("_change_data/below.parquet", OldFile);

        (DeltaVacuum vacuum, _) = Build(skipEnabled: true, 1, 3);
        VacuumResult result = await vacuum.VacuumAsync(Retention, dryRun: true);

        Assert.DoesNotContain("_change_data/below.parquet", result.DeletablePaths);

        // Differential: identical to the null-observer unconditional-scan control.
        (DeltaVacuum control, _) = Build(skipEnabled: false, 1, 3);
        VacuumResult scan = await control.VacuumAsync(Retention, dryRun: true);
        Assert.Equal(
            scan.DeletablePaths.OrderBy(p => p, StringComparer.Ordinal),
            result.DeletablePaths.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task InheritedCdfOn_EnablerAgedOutBelowLow_FullReplay_Scans_AndProtectsCdc()
    {
        // Derive-prevailing path (ii): full replay (observer covers [0, latest]), CDF enabled at an AGED-OUT
        // recorded transition vₑ=0 that is below the in-window low end, and every in-window commit (v1) merely
        // INHERITS CDF-on with no metaData of its own. The step function must carry v0's recorded CDF-on
        // forward to the inheriting in-window v1 → SCAN → protect. A mutant that reads _lineageAtWindowStart
        // plus only the in-window records (dropping the recorded transition at v0 < lo) would wrongly skip.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(),
            DeltaTestHarness.MetadataWithConfig(("delta.enableChangeDataFeed", "true")));
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Cdc("_change_data/inh.parquet"));
        await WriteDataFileAsync("active.parquet", OldFile);
        await WriteDataFileAsync("_change_data/inh.parquet", OldFile);

        // v0 (the CDF-on enabler) is AGED OUT (below the log-retention cutoff), so it is NOT in the in-window
        // set — yet the observer still recorded its transition; only v1 is in-window and inherits.
        (DeltaVacuum vacuum, _) = BuildWithStamps(
            skipEnabled: true, new[] { (0L, Now.AddDays(-60)), (1L, Recent) });

        VacuumResult result = await vacuum.VacuumAsync(Retention, dryRun: true);

        Assert.DoesNotContain("_change_data/inh.parquet", result.DeletablePaths);
    }

    [Fact]
    public async Task Skip_LogsDeltaVacuumCdcScanSkipped_AndNotTheScanCompletedEvent()
    {
        // Pin skip-for-the-RIGHT-reason: a proven skip emits the distinct EventId 4109
        // DeltaVacuumCdcScanSkipped log (value-type-only) and does NOT emit the scan-completed event; the
        // in-window-commit count field matches. Guards a mutant that keeps read-counts but flips the decision.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("active.parquet"));
        await WriteDataFileAsync("active.parquet", OldFile);

        var logger = new RecordingLogger<DeltaVacuum>();
        (DeltaVacuum vacuum, _) = BuildWithStamps(
            skipEnabled: true, new[] { (0L, Recent), (1L, Recent) }, logger);
        await vacuum.VacuumAsync(Retention, dryRun: true);

        Assert.True(logger.Has("DeltaVacuumCdcScanSkipped"), "expected the proven skip to log EventId 4109");
        Assert.False(logger.Has("DeltaVacuumCdcScanCompleted"), "a skip must NOT log the scan-completed event");
        Assert.Equal(2, Convert.ToInt32(logger.Single("DeltaVacuumCdcScanSkipped").Field("InWindowCommits")));

        // A non-CDF table with the skip OFF logs the scan-completed event, never the skipped event.
        var scanLogger = new RecordingLogger<DeltaVacuum>();
        (DeltaVacuum control, _) = BuildWithStamps(
            skipEnabled: false, new[] { (0L, Recent), (1L, Recent) }, scanLogger);
        await control.VacuumAsync(Retention, dryRun: true);
        Assert.True(scanLogger.Has("DeltaVacuumCdcScanCompleted"));
        Assert.False(scanLogger.Has("DeltaVacuumCdcScanSkipped"));
    }

    private const string EmptySchemaUnescaped = """{"type":"struct","fields":[]}""";

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
