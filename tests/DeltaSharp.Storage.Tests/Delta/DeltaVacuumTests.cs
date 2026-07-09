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

    // ---- helpers ----

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
