using System.Collections.Immutable;
using System.Globalization;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Diagnostics;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Tests for retention-aware <see cref="DeltaVacuum"/> (design §2.14, STORY-05.6.2). VACUUM discovers
/// candidate files under the table directory, delegates the deletion decision to the
/// <see cref="OrphanCleanup.SelectDeletable"/> contract, and either lists (dry-run) or idempotently deletes
/// the eligible ones while recording a per-candidate audit. Each test maps to an acceptance criterion:
/// AC1 dry-run lists only eligible paths; AC2 a sub-threshold retention is rejected unless the unsafe
/// override is set; AC3 protected files (active / recent tombstone / recently-staged, the listing-lag
/// fail-safe) are never deleted and the audit records why; AC4 a retry after partial deletion is idempotent.
/// </summary>
public sealed class DeltaVacuumTests : IDisposable
{
    // A fixed "now" far enough in the future that files written at real wall-clock time, then stamped with
    // explicit mtimes relative to this instant, land deterministically on either side of the retention cutoff.
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(168); // 7 days (default safety threshold)

    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;
    private readonly DeltaVacuum _vacuum;

    public DeltaVacuumTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vacuum-tests-" + Guid.NewGuid().ToString("N"));
        _backend = new LocalFileSystemBackend(_root);
        _vacuum = new DeltaVacuum(
            _backend, policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));
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

    [Fact]
    public async Task DryRun_ListsOnlyEligiblePaths_AndDeletesNothing()
    {
        // AC1: active file, an expired tombstone, an uncommitted old orphan, retained history — the dry-run
        // lists ONLY the deletion-eligible paths and touches nothing on disk.
        await WriteLogAsync(
            new[]
            {
                DeltaTestHarness.Add("active.parquet"),
                DeltaTestHarness.Add("expired-removed.parquet"),
            },
            removeLines: new[] { DeltaTestHarness.Remove("expired-removed.parquet") }); // deletionTimestamp=1 → expired

        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("expired-removed.parquet", Old);
        await WriteDataFileAsync("old-orphan.parquet", Old);
        await WriteDataFileAsync("staged-now.parquet", Recent.UtcDateTime);

        VacuumResult result = await _vacuum.VacuumAsync(Retention, dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(
            new[] { "expired-removed.parquet", "old-orphan.parquet" },
            result.DeletablePaths.OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Empty(result.DeletedPaths);

        // Nothing was deleted: every file — including the eligible ones — still exists.
        foreach (string path in new[]
                 { "active.parquet", "expired-removed.parquet", "old-orphan.parquet", "staged-now.parquet" })
        {
            Assert.NotNull(await _backend.HeadAsync(path, CancellationToken.None));
        }
    }

    [Fact]
    public async Task SubThresholdRetention_IsRejected_UnlessUnsafeOverride()
    {
        // AC2: a retention below the 168 h safety threshold is rejected fail-closed...
        await WriteLogAsync(new[] { DeltaTestHarness.Add("active.parquet") });
        await WriteDataFileAsync("old-orphan.parquet", Old);

        var ex = await Assert.ThrowsAsync<VacuumRetentionSafetyException>(
            () => _vacuum.VacuumAsync(TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.FromHours(1), ex.RequestedRetention);
        Assert.Equal(Retention, ex.SafetyThreshold);

        // ...and the rejection happens BEFORE any deletion — the orphan is untouched.
        Assert.NotNull(await _backend.HeadAsync("old-orphan.parquet", CancellationToken.None));

        // ...but an explicit unsafe override lets the same short retention through.
        VacuumResult result = await _vacuum.VacuumAsync(TimeSpan.FromHours(1), unsafeOverride: true);
        Assert.Contains("old-orphan.parquet", result.DeletedPaths);
    }

    [Fact]
    public async Task DefaultRetention_IsUsed_WhenNoneRequested()
    {
        // AC2 companion: a no-argument VACUUM applies the policy default retention (168 h) and is not rejected.
        await WriteLogAsync(new[] { DeltaTestHarness.Add("active.parquet") });
        await WriteDataFileAsync("old-orphan.parquet", Old);

        VacuumResult result = await _vacuum.VacuumAsync();

        Assert.Equal(Retention, result.Retention);
        Assert.Equal(new[] { "old-orphan.parquet" }, result.DeletedPaths.ToArray());
    }

    [Fact]
    public async Task ProtectedFiles_AreNeverDeleted_AndAuditRecordsEveryDecision()
    {
        // AC3: under a recent tombstone (a concurrent reader may still need it) and a recently-staged file
        // (the object-store listing-lag fail-safe — a just-written file must never be reclaimed), the
        // protected files survive, only the true orphan is deleted, and the audit records WHY for each.
        long recentMillis = Recent.ToUnixTimeMilliseconds();
        string recentRemove =
            "{\"remove\":{\"path\":\"recent-removed.parquet\",\"deletionTimestamp\":"
            + recentMillis.ToString(CultureInfo.InvariantCulture)
            + ",\"dataChange\":true}}";

        await WriteLogAsync(
            new[]
            {
                DeltaTestHarness.Add("active.parquet"),
                DeltaTestHarness.Add("recent-removed.parquet"),
            },
            removeLines: new[] { recentRemove });

        await WriteDataFileAsync("active.parquet", Old);          // active → protected
        await WriteDataFileAsync("recent-removed.parquet", Old);  // tombstone within retention → protected
        await WriteDataFileAsync("staged-now.parquet", Recent.UtcDateTime);   // mtime within retention → protected
        await WriteDataFileAsync("old-orphan.parquet", Old);      // unreferenced + expired → deletable

        VacuumResult result = await _vacuum.VacuumAsync(Retention);

        Assert.Equal(new[] { "old-orphan.parquet" }, result.DeletedPaths.ToArray());

        // The three protected files still exist; the orphan is gone.
        Assert.NotNull(await _backend.HeadAsync("active.parquet", CancellationToken.None));
        Assert.NotNull(await _backend.HeadAsync("recent-removed.parquet", CancellationToken.None));
        Assert.NotNull(await _backend.HeadAsync("staged-now.parquet", CancellationToken.None));
        Assert.Null(await _backend.HeadAsync("old-orphan.parquet", CancellationToken.None));

        // The audit records a decision + delete flag for EVERY discovered candidate (AC3).
        ImmutableDictionary<string, VacuumAuditEntry> byPath =
            result.Audit.ToImmutableDictionary(e => e.Path, StringComparer.Ordinal);
        Assert.Equal(VacuumDecision.Active, byPath["active.parquet"].Decision);
        Assert.Equal(VacuumDecision.RetentionProtectedTombstone, byPath["recent-removed.parquet"].Decision);
        Assert.Equal(VacuumDecision.RecentlyStaged, byPath["staged-now.parquet"].Decision);
        Assert.Equal(VacuumDecision.Deletable, byPath["old-orphan.parquet"].Decision);

        Assert.False(byPath["active.parquet"].Deleted);
        Assert.False(byPath["recent-removed.parquet"].Deleted);
        Assert.False(byPath["staged-now.parquet"].Deleted);
        Assert.True(byPath["old-orphan.parquet"].Deleted);
    }

    [Fact]
    public async Task Retry_AfterPartialDeletion_IsIdempotent()
    {
        // AC4: a VACUUM retry after a partial deletion handles already-deleted files without error, and a
        // second full run converges to nothing left to delete.
        await WriteLogAsync(new[] { DeltaTestHarness.Add("active.parquet") });
        await WriteDataFileAsync("orphan-a.parquet", Old);
        await WriteDataFileAsync("orphan-b.parquet", Old);

        // Simulate a prior VACUUM that crashed after deleting orphan-a but before recording completion.
        await _backend.DeleteAsync("orphan-a.parquet", CancellationToken.None);

        // The retry still targets both eligible files; deleting the already-gone orphan-a is a no-op success.
        VacuumResult first = await _vacuum.VacuumAsync(Retention);
        Assert.Contains("orphan-b.parquet", first.DeletablePaths);
        Assert.Contains("orphan-b.parquet", first.DeletedPaths);
        Assert.Null(await _backend.HeadAsync("orphan-b.parquet", CancellationToken.None));

        // A second full run finds nothing left — idempotent convergence.
        VacuumResult second = await _vacuum.VacuumAsync(Retention);
        Assert.Empty(second.DeletablePaths);
        Assert.Empty(second.DeletedPaths);
    }

    [Fact]
    public async Task NegativeRetention_IsRejected()
    {
        await WriteLogAsync(new[] { DeltaTestHarness.Add("active.parquet") });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _vacuum.VacuumAsync(TimeSpan.FromHours(-1)));
    }

    [Fact]
    public async Task EncodedActiveLogPath_UnencodedDiskFile_IsNeverDeleted()
    {
        // CRITICAL-1 (data-loss, end-to-end): a Spark/Delta-protocol table URI-encodes log paths. The active
        // file literally named "a b.parquet" on disk is recorded as "a%20b.parquet" in the log, but the
        // directory listing yields the RAW disk key "a b.parquet". Absent the encoding-robust matching the
        // raw candidate would not match the encoded active path → classified orphan → DELETED. VACUUM must
        // protect it. (This test FAILS on the pre-fix ordinal-only matching and PASSES after the fix.)
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("a%20b.parquet"));
        await WriteDataFileAsync("a b.parquet", Old); // raw disk key, old mtime → deletable but for the log ref

        VacuumResult result = await _vacuum.VacuumAsync(Retention);

        Assert.DoesNotContain("a b.parquet", result.DeletedPaths);
        Assert.Empty(result.DeletablePaths);
        Assert.NotNull(await _backend.HeadAsync("a b.parquet", CancellationToken.None));
        Assert.Equal(VacuumDecision.Active, result.Audit.Single(e => e.Path == "a b.parquet").Decision);
    }

    [Fact]
    public async Task ConcurrentlyCommittedActiveFile_InListLoadWindow_IsNeverDeleted()
    {
        // CRITICAL-2 (TOCTOU): a writer that commits an active file in the list/snapshot-load window must not
        // be deleted. The BeforeListProbe fires immediately before candidate listing; because listing now
        // precedes snapshot load, the raced file is either not yet listed (not a candidate) or already
        // active in the later-loaded snapshot (protected). On the pre-fix load-before-list ordering the same
        // probe fires AFTER the (older) snapshot load, so the file is listed-but-not-in-snapshot with an old
        // mtime → DELETED. (FAILS pre-fix, PASSES after the reorder.)
        await WriteLogAsync(new[] { DeltaTestHarness.Add("active.parquet") });
        await WriteDataFileAsync("active.parquet", Old);

        _vacuum.BeforeListProbe = async ct =>
        {
            // A concurrent writer commits a new active file (v2) and stages it on disk with an mtime BELOW
            // the cutoff (clock skew / preserved-timestamp move) — the recency fail-safe alone would not
            // save it; only the list-before-load ordering does.
            await DeltaTestHarness.WriteCommitAsync(_backend, 2, DeltaTestHarness.Add("raced-active.parquet"));
            await WriteDataFileAsync("raced-active.parquet", Old);
        };

        VacuumResult result = await _vacuum.VacuumAsync(Retention);

        Assert.DoesNotContain("raced-active.parquet", result.DeletedPaths);
        Assert.NotNull(await _backend.HeadAsync("raced-active.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task TableConfiguredRetention_IsHonored_OnNoArgVacuum()
    {
        // MEDIUM: a no-argument VACUUM must honor the table's delta.deletedFileRetentionDuration (30 days),
        // not the 7-day process default — otherwise a 10-day-old orphan (still within the configured window)
        // would be wrongly reclaimed. A 60-day orphan (past the window) is still deletable.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(),
            DeltaTestHarness.MetadataWithConfig(("delta.deletedFileRetentionDuration", "interval 30 days")));
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("active.parquet"));
        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("orphan-10d.parquet", Now.AddDays(-10).UtcDateTime); // within 30 d → protected
        await WriteDataFileAsync("orphan-60d.parquet", Old);                          // past 30 d → deletable

        VacuumResult result = await _vacuum.VacuumAsync();

        Assert.Equal(TimeSpan.FromDays(30), result.Retention);
        Assert.Equal(new[] { "orphan-60d.parquet" }, result.DeletedPaths.ToArray());
        Assert.NotNull(await _backend.HeadAsync("orphan-10d.parquet", CancellationToken.None));
        Assert.Equal(
            VacuumDecision.RecentlyStaged, result.Audit.Single(e => e.Path == "orphan-10d.parquet").Decision);
    }

    [Fact]
    public async Task TableConfiguredRetention_SubThreshold_IsRejected_PostLoad()
    {
        // MEDIUM (AC2 post-load, red-team R2): a no-argument VACUUM clears the PRE-load gate (which uses the
        // 168-h DEFAULT), then resolves the table's delta.deletedFileRetentionDuration. When THAT configured
        // window is itself below the safety threshold, only the POST-load guard stands between it and a
        // premature reclaim. It must reject fail-closed and delete nothing. Neutering the post-load
        // `retention < SafetyThreshold` check must fail this test (previously it killed no test).
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(),
            DeltaTestHarness.MetadataWithConfig(("delta.deletedFileRetentionDuration", "interval 1 hours")));
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("active.parquet"));
        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("old-orphan.parquet", Old); // a 1-h window would delete it; the guard must not

        VacuumRetentionSafetyException ex =
            await Assert.ThrowsAsync<VacuumRetentionSafetyException>(() => _vacuum.VacuumAsync());

        Assert.Equal(TimeSpan.FromHours(1), ex.RequestedRetention);
        Assert.Equal(Retention, ex.SafetyThreshold);
        // Fail-closed: nothing was reclaimed — the sub-threshold table config never reaches deletion.
        Assert.NotNull(await _backend.HeadAsync("old-orphan.parquet", CancellationToken.None));
        Assert.NotNull(await _backend.HeadAsync("active.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteOnMissingCandidate_IsIdempotent_AndSucceeds()
    {
        // HIGH (AC4 efficacy): force DeleteAsync on an ALREADY-DELETED candidate by removing it out-of-band
        // between selection and delete (the BeforeDeleteProbe). VACUUM must still succeed (idempotent no-op).
        // Mutating the backend's DeleteAsync-on-missing to throw would fail this test.
        await WriteLogAsync(new[] { DeltaTestHarness.Add("active.parquet") });
        await WriteDataFileAsync("old-orphan.parquet", Old);

        _vacuum.BeforeDeleteProbe = ct => _backend.DeleteAsync("old-orphan.parquet", ct).AsTask();

        VacuumResult result = await _vacuum.VacuumAsync(Retention);

        // It was selected (present at LIST time), and the delete of the now-missing file is a no-op success.
        Assert.Contains("old-orphan.parquet", result.DeletablePaths);
        Assert.Contains("old-orphan.parquet", result.DeletedPaths);
        Assert.Null(await _backend.HeadAsync("old-orphan.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task StaleListing_WithLaggingMtime_NeverDeletesProtectedFiles()
    {
        // HIGH (AC3 efficacy): inject a torn LIST via a fault backend. An ACTIVE file is reported with a
        // lagging (old) mtime AND a protected file is omitted from the listing entirely. The active file is
        // protected by the active set regardless of its listed mtime; the omitted file is never a candidate.
        // Only the genuine orphan is reclaimed. (Mutating the active-set protection to trust the listed mtime
        // would delete "active.parquet" and fail this test.)
        var stale = new StaleListingBackend(
            _backend,
            omittedFromList: new[] { "hidden-active.parquet" },
            mtimeOverrides: new Dictionary<string, DateTime>(StringComparer.Ordinal)
            {
                ["active.parquet"] = Old, // torn view: an active file looks retention-expired.
            });
        var vacuum = new DeltaVacuum(
            stale, policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));

        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Add("hidden-active.parquet"));
        await WriteDataFileAsync("active.parquet", Recent.UtcDateTime);
        await WriteDataFileAsync("hidden-active.parquet", Old);
        await WriteDataFileAsync("old-orphan.parquet", Old);

        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.Equal(new[] { "old-orphan.parquet" }, result.DeletedPaths.ToArray());
        Assert.NotNull(await _backend.HeadAsync("active.parquet", CancellationToken.None));
        Assert.NotNull(await _backend.HeadAsync("hidden-active.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task InWindowCdcFile_IsProtected_EvenThoughNotActive()
    {
        // #489: a `_change_data/x.parquet` referenced by an in-window `cdc` action must be protected by
        // VACUUM — even though a cdc file is NEVER an active file (INV C1: snapshot replay ignores it, so it
        // is not in ActiveFiles). Its path is knowable ONLY from the retained commit JSON, not the snapshot.
        // The file's mtime is Old (past the deleted-file cutoff), so nothing but the cdc reference saves it.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Cdc("_change_data/x.parquet"));

        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("_change_data/x.parquet", Old); // Old mtime → only the cdc ref protects it

        // The cdc-bearing commit (v1) is within the 30-day log-retention window; stamp its listed mtime so
        // the log scan sees it inside the window (a real wall-clock mtime would fall far before Now=2030).
        var vacuum = new DeltaVacuum(
            DeltaTestHarness.WithCommitTimestamps(_backend, (0, Recent), (1, Recent)),
            policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));

        VacuumResult result = await vacuum.VacuumAsync(Retention);

        // The change file survives and is audited as a protected referenced-change-data file, not deletable.
        Assert.DoesNotContain("_change_data/x.parquet", result.DeletablePaths);
        Assert.DoesNotContain("_change_data/x.parquet", result.DeletedPaths);
        Assert.NotNull(await _backend.HeadAsync("_change_data/x.parquet", CancellationToken.None));
        Assert.Equal(
            VacuumDecision.ReferencedChangeData,
            result.Audit.Single(e => e.Path == "_change_data/x.parquet").Decision);
    }

    [Fact]
    public async Task OrphanChangeDataFile_ReferencedByNoCdcAction_IsReclaimable()
    {
        // #489: a `_change_data/y.parquet` that no retained `cdc` action references is a genuine orphan
        // (e.g. a change file staged by a crashed writer) and, once past retention, is reclaimable — the cdc
        // protection must not blanket-protect the whole _change_data/ directory.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Cdc("_change_data/x.parquet"));

        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("_change_data/x.parquet", Old);  // referenced by v1's cdc → protected
        await WriteDataFileAsync("_change_data/y.parquet", Old);  // referenced by NO cdc action → deletable

        var vacuum = new DeltaVacuum(
            DeltaTestHarness.WithCommitTimestamps(_backend, (0, Recent), (1, Recent)),
            policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));

        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.Equal(new[] { "_change_data/y.parquet" }, result.DeletedPaths.ToArray());
        Assert.Null(await _backend.HeadAsync("_change_data/y.parquet", CancellationToken.None));
        Assert.NotNull(await _backend.HeadAsync("_change_data/x.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task CdcFile_ReferencedOnlyByCommitAgedPastLogRetention_IsReclaimable()
    {
        // #489 (window-bounded): a cdc file referenced only by a commit whose JSON has aged past
        // delta.logRetentionDuration is NOT in the retained window, so it is correctly unprotected and
        // reclaimable. v1's listed mtime is Old (60 days) — well past the 30-day log-retention cutoff — so
        // the log scan skips it and its cdc file falls through to Deletable.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Cdc("_change_data/aged.parquet"));

        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("_change_data/aged.parquet", Old);

        var vacuum = new DeltaVacuum(
            DeltaTestHarness.WithCommitTimestamps(_backend, (0, new DateTimeOffset(Old, TimeSpan.Zero)),
                (1, new DateTimeOffset(Old, TimeSpan.Zero))),
            policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));

        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.Contains("_change_data/aged.parquet", result.DeletedPaths);
        Assert.Null(await _backend.HeadAsync("_change_data/aged.parquet", CancellationToken.None));
        Assert.Equal(
            VacuumDecision.Deletable,
            result.Audit.Single(e => e.Path == "_change_data/aged.parquet").Decision);
    }

    [Fact]
    public async Task CdcAction_DoesNotBecomeActiveFile_InvariantC1()
    {
        // INV C1: a `cdc` action must not affect snapshot reconstruction — a snapshot of a table WITH a cdc
        // action has exactly the same active state (ActiveFiles / Tombstones) as one without. This pins that
        // VACUUM's new cdc protection does not leak the cdc file into active read state.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Cdc("_change_data/x.parquet"));

        var log = new DeltaLog(_backend);
        Snapshot withCdc = await log.LoadSnapshotAsync(version: null, CancellationToken.None);

        // The cdc file is neither an active file nor a tombstone; only the real add is active.
        Assert.Equal(new[] { "active.parquet" }, withCdc.ActiveFiles.Select(a => a.Path).ToArray());
        Assert.Empty(withCdc.Tombstones);

        // A parallel table with the SAME add but NO cdc action yields byte-identical active state.
        string otherRoot = Path.Combine(Path.GetTempPath(), "vacuum-c1-" + Guid.NewGuid().ToString("N"));
        using var otherBackend = new LocalFileSystemBackend(otherRoot);
        try
        {
            await DeltaTestHarness.WriteCommitAsync(
                otherBackend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
            await DeltaTestHarness.WriteCommitAsync(otherBackend, 1, DeltaTestHarness.Add("active.parquet"));
            Snapshot withoutCdc = await new DeltaLog(otherBackend).LoadSnapshotAsync(version: null, CancellationToken.None);

            Assert.Equal(
                withoutCdc.ActiveFiles.Select(a => a.Path).ToArray(),
                withCdc.ActiveFiles.Select(a => a.Path).ToArray());
            Assert.Equal(withoutCdc.Tombstones.Length, withCdc.Tombstones.Length);
        }
        finally
        {
            Directory.Delete(otherRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EncodedCdcLogPath_UnencodedDiskFile_IsNeverDeleted()
    {
        // Quality (HIGH), encoding-robust cdc protection: a Spark/Delta table URI-encodes the `cdc` path. A
        // change file literally named "_change_data/foo bar.parquet" on disk is recorded as
        // "_change_data/foo%20bar.parquet" in the commit JSON, but the directory listing yields the RAW disk
        // key "_change_data/foo bar.parquet". Absent the encoding-robust matching the raw candidate would not
        // match the encoded cdc path → classified orphan → DELETED. This mirrors
        // EncodedActiveLogPath_UnencodedDiskFile_IsNeverDeleted for a `cdc` action (#489).
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"),
            DeltaTestHarness.Cdc("_change_data/foo%20bar.parquet"));

        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("_change_data/foo bar.parquet", Old); // raw disk key, old mtime → only the cdc ref saves it

        var vacuum = new DeltaVacuum(
            DeltaTestHarness.WithCommitTimestamps(_backend, (0, Recent), (1, Recent)),
            policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));

        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.DoesNotContain("_change_data/foo bar.parquet", result.DeletablePaths);
        Assert.DoesNotContain("_change_data/foo bar.parquet", result.DeletedPaths);
        Assert.NotNull(await _backend.HeadAsync("_change_data/foo bar.parquet", CancellationToken.None));
        Assert.Equal(
            VacuumDecision.ReferencedChangeData,
            result.Audit.Single(e => e.Path == "_change_data/foo bar.parquet").Decision);
    }

    [Fact]
    public async Task EncodedChangeDataSeparator_CdcFile_IsNeverDeleted()
    {
        // Red-team (Critical, #489): a candidate whose directory SEPARATOR is URI-encoded
        // ("_change_data%2Fencoded.parquet") does NOT start with the literal "_change_data/" prefix. An earlier
        // attempt to skip the cdc scan on a `_change_data/`-prefix candidate predicate would have skipped it,
        // left the cdc set empty, and DELETED the live change file. VACUUM now scans the in-window commits
        // UNCONDITIONALLY, so OrphanCleanup's encoding-robust match protects the file regardless of how the
        // separator is encoded. (Unlike EncodedCdcLogPath above, which encodes only the filename — whose raw
        // disk key still carries the literal "_change_data/" prefix — this encodes the separator.)
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"),
            DeltaTestHarness.Cdc("_change_data%2Fencoded.parquet"));

        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("_change_data%2Fencoded.parquet", Old); // encoded-separator raw disk key, old mtime

        var vacuum = new DeltaVacuum(
            DeltaTestHarness.WithCommitTimestamps(_backend, (0, Recent), (1, Recent)),
            policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));

        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.DoesNotContain("_change_data%2Fencoded.parquet", result.DeletablePaths);
        Assert.DoesNotContain("_change_data%2Fencoded.parquet", result.DeletedPaths);
        Assert.NotNull(await _backend.HeadAsync("_change_data%2Fencoded.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task DoubleEncodedChangeDataSeparator_CdcFile_IsNeverDeleted()
    {
        // Red-team (2nd Critical, #489): the reason the cdc scan is UNCONDITIONAL. A verbatim writer can
        // reference a `cdc` file whose raw disk key is DOUBLE-encoded ("_change_data%252Fencoded.parquet").
        // OrphanCleanup protects it (raw-key match), but a one-pass `Uri.UnescapeDataString` predicate decodes
        // only "%252F"->"%2F" (still not "_change_data/"), so any candidate-path short-circuit would skip the
        // scan and DELETE it. Scanning unconditionally closes the gap: the cdc path is collected and matched
        // byte-for-byte against the raw disk key.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"),
            DeltaTestHarness.Cdc("_change_data%252Fencoded.parquet"));

        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("_change_data%252Fencoded.parquet", Old); // double-encoded raw disk key, old mtime

        var vacuum = new DeltaVacuum(
            DeltaTestHarness.WithCommitTimestamps(_backend, (0, Recent), (1, Recent)),
            policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));

        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.DoesNotContain("_change_data%252Fencoded.parquet", result.DeletablePaths);
        Assert.DoesNotContain("_change_data%252Fencoded.parquet", result.DeletedPaths);
        Assert.NotNull(await _backend.HeadAsync("_change_data%252Fencoded.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task NonCanonicalCdcPath_OutsideChangeDataDir_IsNeverDeleted()
    {
        // Red-team (2nd Critical, #489): a `cdc` action's path is NOT constrained to `_change_data/` (ParseCdc
        // accepts any path), and OrphanCleanup protects a candidate matching ANY referenced cdc path. A cdc
        // file at a non-canonical location ("cdc-blob.parquet", no `_change_data/` prefix) must therefore be
        // protected too — a candidate-path short-circuit gated on the `_change_data/` prefix would have skipped
        // the scan and DELETED it. The unconditional scan collects the path and OrphanCleanup protects it.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"),
            DeltaTestHarness.Cdc("cdc-blob.parquet"));

        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("cdc-blob.parquet", Old); // referenced cdc file outside _change_data/, old mtime

        var vacuum = new DeltaVacuum(
            DeltaTestHarness.WithCommitTimestamps(_backend, (0, Recent), (1, Recent)),
            policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));

        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.DoesNotContain("cdc-blob.parquet", result.DeletablePaths);
        Assert.DoesNotContain("cdc-blob.parquet", result.DeletedPaths);
        Assert.NotNull(await _backend.HeadAsync("cdc-blob.parquet", CancellationToken.None));
    }

    [Theory]
    [InlineData("interval 1 months")]
    [InlineData("garbage")]
    public async Task UnparseableLogRetention_FailsClosed_AndDeletesNothing(string malformed)
    {
        // Quality (HIGH): a table whose delta.logRetentionDuration is present but unparseable must fail
        // closed — VACUUM cannot know how far back to scan for in-window `cdc` files, so
        // ResolveTableLogRetention throws (FormatException) rather than silently under-protecting; nothing is
        // reclaimed (#489).
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(),
            DeltaTestHarness.MetadataWithConfig(("delta.logRetentionDuration", malformed)));
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("active.parquet"));
        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("old-orphan.parquet", Old); // deletable but for the fail-closed abort

        await Assert.ThrowsAsync<FormatException>(() => _vacuum.VacuumAsync(Retention));

        // Fail-closed: the malformed config aborts VACUUM before any deletion.
        Assert.NotNull(await _backend.HeadAsync("old-orphan.parquet", CancellationToken.None));
        Assert.NotNull(await _backend.HeadAsync("active.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task CdcCommit_AtExactLogRetentionBoundary_IsProtected()
    {
        // Quality (MEDIUM), exact window boundary: a cdc file whose referencing commit's mtime is EXACTLY at
        // `now - logRetentionDuration` is in-window (the scan skips only commits STRICTLY below the cutoff),
        // so the file is protected (#489). logRetentionDuration defaults to 30 days.
        DateTimeOffset boundary = Now - RetentionPolicyLogWindow; // exactly at the cutoff → protected
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Cdc("_change_data/x.parquet"));

        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("_change_data/x.parquet", Old); // Old mtime → only the in-window cdc ref protects it

        var vacuum = new DeltaVacuum(
            DeltaTestHarness.WithCommitTimestamps(_backend, (0, boundary), (1, boundary)),
            policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));

        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.DoesNotContain("_change_data/x.parquet", result.DeletedPaths);
        Assert.NotNull(await _backend.HeadAsync("_change_data/x.parquet", CancellationToken.None));
        Assert.Equal(
            VacuumDecision.ReferencedChangeData,
            result.Audit.Single(e => e.Path == "_change_data/x.parquet").Decision);
    }

    [Fact]
    public async Task CdcCommit_JustBeyondLogRetentionBoundary_IsReclaimable()
    {
        // Quality (MEDIUM), exact window boundary (other side): a cdc file whose referencing commit's mtime
        // is `now - logRetentionDuration - 1ms` is STRICTLY below the cutoff, so the scan skips it and the
        // change file falls through to Deletable (#489).
        DateTimeOffset justBeyond = Now - RetentionPolicyLogWindow - TimeSpan.FromMilliseconds(1);
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Cdc("_change_data/x.parquet"));

        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("_change_data/x.parquet", Old);

        var vacuum = new DeltaVacuum(
            DeltaTestHarness.WithCommitTimestamps(_backend, (0, justBeyond), (1, justBeyond)),
            policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));

        VacuumResult result = await vacuum.VacuumAsync(Retention);

        Assert.Contains("_change_data/x.parquet", result.DeletedPaths);
        Assert.Null(await _backend.HeadAsync("_change_data/x.parquet", CancellationToken.None));
        Assert.Equal(
            VacuumDecision.Deletable,
            result.Audit.Single(e => e.Path == "_change_data/x.parquet").Decision);
    }

    [Fact]
    public async Task TornInWindowCommit_FailsClosed_AndReclaimsNothing()
    {
        // SRE (MEDIUM): a torn/corrupt in-window commit JSON must fail VACUUM closed — the in-window `cdc`
        // scan reads every retained commit, and a decode failure must propagate (never silently skip and
        // under-protect). A checkpoint at v1 seeds the snapshot cleanly, so it is the SCAN that trips over
        // the malformed v0.json; a co-present deletable orphan proves nothing is reclaimed on abort (#489).
        await DeltaTestHarness.WriteCheckpointAsync(
            _backend, 1, new CheckpointFixture().Protocol(1, 2).Metadata(id: "t", schemaString: EmptySchemaUnescaped));
        await DeltaTestHarness.WriteLastCheckpointAsync(_backend, 1);
        await DeltaTestHarness.WriteRawCommitAsync(
            _backend, 0, System.Text.Encoding.UTF8.GetBytes("{ this is not valid delta json"));

        // A co-present old `_change_data/` orphan (otherwise reclaimable) confirms the abort precedes deletion.
        await WriteDataFileAsync("_change_data/orphan.parquet", Old);

        var vacuum = new DeltaVacuum(
            DeltaTestHarness.WithCommitTimestamps(_backend, (0, Recent)), // torn v0 is in the log-retention window
            policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<DeltaProtocolException>(() => vacuum.VacuumAsync(Retention));

        // Fail-closed: the abort happens before the deletion phase, so the orphan is untouched.
        Assert.NotNull(await _backend.HeadAsync("_change_data/orphan.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task DryRun_ReferencedCdcFile_ShowsProtectedDecision_AndRemainsOnDisk()
    {
        // SRE (LOW): under dryRun a referenced cdc file is audited as ReferencedChangeData with Deleted=false
        // and is never touched on disk (#489).
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Cdc("_change_data/x.parquet"));

        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("_change_data/x.parquet", Old);

        var vacuum = new DeltaVacuum(
            DeltaTestHarness.WithCommitTimestamps(_backend, (0, Recent), (1, Recent)),
            policy: null, logger: null, telemetry: null, timeProvider: new FixedTimeProvider(Now));

        VacuumResult result = await vacuum.VacuumAsync(Retention, dryRun: true);

        Assert.True(result.DryRun);
        var entry = result.Audit.Single(e => e.Path == "_change_data/x.parquet");
        Assert.Equal(VacuumDecision.ReferencedChangeData, entry.Decision);
        Assert.False(entry.Deleted);
        Assert.NotNull(await _backend.HeadAsync("_change_data/x.parquet", CancellationToken.None));
    }

    [Fact]
    public async Task NonCdfTable_NoChangeDataCandidates_ReclaimsOrphans()
    {
        // #489: a non-CDF table has no `cdc` actions in its retained commits, so the unconditional in-window
        // cdc scan collects no protected paths and VACUUM behaves exactly as a pre-CDF table — a genuine
        // orphan past retention is reclaimed and the active file is protected (no over-protection regression).
        await WriteLogAsync(new[] { DeltaTestHarness.Add("active.parquet") });
        await WriteDataFileAsync("active.parquet", Old);
        await WriteDataFileAsync("old-orphan.parquet", Old);

        VacuumResult result = await _vacuum.VacuumAsync(Retention);

        Assert.Equal(new[] { "old-orphan.parquet" }, result.DeletedPaths.ToArray());
        Assert.Null(await _backend.HeadAsync("old-orphan.parquet", CancellationToken.None));
        Assert.NotNull(await _backend.HeadAsync("active.parquet", CancellationToken.None));
    }

    // ---- helpers ----

    // The default delta.logRetentionDuration window (30 days) the boundary tests pivot the scan cutoff on.
    private static readonly TimeSpan RetentionPolicyLogWindow = TimeSpan.FromDays(30);

    private const string EmptySchemaUnescaped = """{"type":"struct","fields":[]}""";

    private static DateTime Old => Now.AddDays(-60).UtcDateTime;    // well before any tested cutoff → deletable
    private static DateTimeOffset Recent => Now.AddDays(-1);         // within the 168 h window → protected

    // Writes v0 (protocol+metadata), v1 (the add lines), and — when removals are given — v2 (the remove lines).
    private async Task WriteLogAsync(string[] adds, string[]? removeLines = null)
    {
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, adds);
        if (removeLines is { Length: > 0 })
        {
            await DeltaTestHarness.WriteCommitAsync(_backend, 2, removeLines);
        }
    }

    private async Task WriteDataFileAsync(string path, DateTime mtimeUtc)
    {
        await _backend.PutIfAbsentAsync(path, new byte[] { 1, 2, 3 }, CancellationToken.None);
        string full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        File.SetLastWriteTimeUtc(full, DateTime.SpecifyKind(mtimeUtc, DateTimeKind.Utc));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
