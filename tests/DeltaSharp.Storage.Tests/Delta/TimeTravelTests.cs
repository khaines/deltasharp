using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Time-travel behavior for <see cref="DeltaLog"/> (design §2.12.1; STORY-05.4.1). AC1 reconstructs the
/// exact state at a requested version; AC2 resolves a <c>timestampAsOf</c> to the latest version whose
/// (monotonic-adjusted) commit timestamp is at or before the instant and reports it; AC3 fails closed with a
/// retention-aware error when the target is older than the earliest retained log (never returning current
/// data); AC4 proves a <b>later</b> checkpoint/commit can never leak into a historical load. Commit
/// timestamps are threaded deterministically through <see cref="DeltaTestHarness.WithCommitTimestamps"/>
/// (the <see cref="IStorageBackend.ListAsync"/> mtime seam) — never the wall clock.
/// </summary>
public sealed class TimeTravelTests : IDisposable
{
    private const string EmptySchemaUnescaped = """{"type":"struct","fields":[]}""";
    private static readonly DateTimeOffset Origin = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    private LocalFileSystemBackend NewBackend()
    {
        string root = Path.Combine(Path.GetTempPath(), "time-travel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return new LocalFileSystemBackend(root);
    }

    // v0 protocol+metadata+add(a)+add(b); v1 add(c); v2 remove(b); v3 add(d); v4 add(e)+remove(a); v5 add(f).
    // Surviving active sets: v0={a,b} v1={a,b,c} v2={a,c} v3={a,c,d} v4={c,d,e} v5={c,d,e,f}.
    private static async Task WriteBranchingHistoryAsync(IStorageBackend backend)
    {
        await DeltaTestHarness.WriteCommitAsync(backend, 0,
            DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata(), DeltaTestHarness.Add("a.parquet"), DeltaTestHarness.Add("b.parquet"));
        await DeltaTestHarness.WriteCommitAsync(backend, 1, DeltaTestHarness.Add("c.parquet"));
        await DeltaTestHarness.WriteCommitAsync(backend, 2, DeltaTestHarness.Remove("b.parquet"));
        await DeltaTestHarness.WriteCommitAsync(backend, 3, DeltaTestHarness.Add("d.parquet"));
        await DeltaTestHarness.WriteCommitAsync(backend, 4, DeltaTestHarness.Add("e.parquet"), DeltaTestHarness.Remove("a.parquet"));
        await DeltaTestHarness.WriteCommitAsync(backend, 5, DeltaTestHarness.Add("f.parquet"));
    }

    // Builds a linear table (v0 = protocol+metadata+add(f0); vN = add(fN)) and stamps each commit object with
    // a deterministic modification time, so version N's active set is exactly {f0..fN}.
    private async Task<DeltaLog> BuildLinearTimedTableAsync(params DateTimeOffset[] commitTimes)
    {
        IStorageBackend inner = NewBackend();
        for (int v = 0; v < commitTimes.Length; v++)
        {
            if (v == 0)
            {
                await DeltaTestHarness.WriteCommitAsync(inner, 0,
                    DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata(), DeltaTestHarness.Add("f0.parquet"));
            }
            else
            {
                await DeltaTestHarness.WriteCommitAsync(inner, v, DeltaTestHarness.Add("f" + v.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".parquet"));
            }
        }

        (long, DateTimeOffset)[] overrides = commitTimes.Select((ts, v) => ((long)v, ts)).ToArray();
        return new DeltaLog(DeltaTestHarness.WithCommitTimestamps(inner, overrides));
    }

    // ---- AC1: exact version reconstruction ----

    [Fact]
    public async Task LoadVersion_ReconstructsExactState_IndependentOfLaterCommits()
    {
        // AC1: the state at version 2 equals a table whose history was truncated at 2 — later commits (3..5)
        // do not influence the earlier snapshot.
        IStorageBackend full = NewBackend();
        await WriteBranchingHistoryAsync(full);
        Snapshot atV2 = await new DeltaLog(full).LoadSnapshotAsync(version: 2);

        IStorageBackend truncated = NewBackend();
        await DeltaTestHarness.WriteCommitAsync(truncated, 0,
            DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata(), DeltaTestHarness.Add("a.parquet"), DeltaTestHarness.Add("b.parquet"));
        await DeltaTestHarness.WriteCommitAsync(truncated, 1, DeltaTestHarness.Add("c.parquet"));
        await DeltaTestHarness.WriteCommitAsync(truncated, 2, DeltaTestHarness.Remove("b.parquet"));
        Snapshot latestOfTruncated = await new DeltaLog(truncated).LoadSnapshotAsync();

        Assert.Equal(2, atV2.Version);
        Assert.Equal(["a.parquet", "c.parquet"], atV2.ActiveFiles.Select(a => a.Path));
        Assert.Equal(DeltaTestHarness.Describe(latestOfTruncated), DeltaTestHarness.Describe(atV2));
    }

    [Theory]
    [InlineData(0, "a.parquet,b.parquet")]
    [InlineData(1, "a.parquet,b.parquet,c.parquet")]
    [InlineData(2, "a.parquet,c.parquet")]
    [InlineData(3, "a.parquet,c.parquet,d.parquet")]
    [InlineData(4, "c.parquet,d.parquet,e.parquet")]
    [InlineData(5, "c.parquet,d.parquet,e.parquet,f.parquet")]
    public async Task LoadVersion_ProducesActiveSetForThatVersion(long version, string expectedActive)
    {
        IStorageBackend backend = NewBackend();
        await WriteBranchingHistoryAsync(backend);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: version);

        Assert.Equal(version, snapshot.Version);
        Assert.Equal(expectedActive.Split(','), snapshot.ActiveFiles.Select(a => a.Path));
    }

    // ---- AC2: timestamp resolution + resolved-version reporting ----

    [Theory]
    [InlineData(0, 0)]              // exactly t0 → v0 (boundary, inclusive)
    [InlineData(1_800_000, 0)]     // t0+30m, before t1 → v0
    [InlineData(3_600_000, 1)]     // exactly t1 → v1 (boundary)
    [InlineData(5_400_000, 1)]     // between t1 and t2 → v1
    [InlineData(7_200_000, 2)]     // exactly t2 → v2
    [InlineData(10_800_000, 3)]    // exactly t3 (latest) → v3
    [InlineData(999_999_000, 3)]   // far after latest → clamps to latest (v3)
    public async Task LoadAsOfTimestamp_ResolvesToLatestVersionAtOrBefore_AndReportsIt(long asOfOffsetMillis, long expected)
    {
        DeltaLog log = await BuildLinearTimedTableAsync(
            Origin, Origin.AddHours(1), Origin.AddHours(2), Origin.AddHours(3));

        TimeTravelResult result = await log.LoadSnapshotAsOfTimestampAsync(Origin.AddMilliseconds(asOfOffsetMillis));

        Assert.Equal(expected, result.ResolvedVersion);
        Assert.Equal(expected, result.Snapshot.Version); // resolved version reported and matches the snapshot
        Assert.Equal(expected + 1, result.Snapshot.ActiveFiles.Length); // {f0..fN}

        // Parity: the timestamp-resolved snapshot equals an explicit versionAsOf load of the same version.
        Snapshot byVersion = await log.LoadSnapshotAsync(version: expected);
        Assert.Equal(DeltaTestHarness.Describe(byVersion), DeltaTestHarness.Describe(result.Snapshot));
    }

    [Theory]
    [InlineData(0, 0)]   // eff(v0)=t0
    [InlineData(1, 1)]   // eff(v1)=t0+1ms (monotonicity forces strictly later than equal mtime)
    [InlineData(2, 2)]   // eff(v2)=t0+2ms
    [InlineData(3, 3)]   // eff(v3)=t0+3ms
    [InlineData(50, 3)]  // after the whole (compressed) timeline → latest
    public async Task LoadAsOfTimestamp_WithEqualMtimes_ResolvesViaStrictMonotonicity(long asOfOffsetMillis, long expected)
    {
        // All four commit files share one mtime; the reader must still produce a deterministic, strictly
        // increasing timeline (eff(N) = max(mtime, eff(N-1)+1ms)) so each 1ms step maps to the next version.
        DeltaLog log = await BuildLinearTimedTableAsync(Origin, Origin, Origin, Origin);

        TimeTravelResult result = await log.LoadSnapshotAsOfTimestampAsync(Origin.AddMilliseconds(asOfOffsetMillis));

        Assert.Equal(expected, result.ResolvedVersion);
        Assert.Equal(expected, result.Snapshot.Version);
    }

    [Theory]
    [InlineData(100, 0)]   // eff(v0)=t0+100
    [InlineData(101, 1)]   // eff(v1)=max(t0+50, eff0+1)=t0+101
    [InlineData(150, 1)]   // still before eff(v2)
    [InlineData(199, 1)]
    [InlineData(200, 2)]   // eff(v2)=max(t0+200, eff1+1)=t0+200
    public async Task LoadAsOfTimestamp_WithOutOfOrderMtimes_ResolvesDeterministically(long asOfOffsetMillis, long expected)
    {
        // Version 1's file mtime is EARLIER than version 0's (clock skew / preserved-timestamp move). The
        // monotonicity adjustment pins eff(v1) strictly after eff(v0), so resolution stays deterministic and
        // never selects a later version for an earlier instant.
        DeltaLog log = await BuildLinearTimedTableAsync(
            Origin.AddMilliseconds(100), Origin.AddMilliseconds(50), Origin.AddMilliseconds(200));

        TimeTravelResult result = await log.LoadSnapshotAsOfTimestampAsync(Origin.AddMilliseconds(asOfOffsetMillis));

        Assert.Equal(expected, result.ResolvedVersion);
        Assert.Equal(expected, result.Snapshot.Version);
    }

    [Fact]
    public async Task LoadAsOfTimestamp_BeforeFirstCommit_FailsClosed_WithRetentionGap()
    {
        // AC3 (timestamp before earliest): no commit has a timestamp at or before the instant → fail closed,
        // never return the earliest/current state.
        DeltaLog log = await BuildLinearTimedTableAsync(Origin, Origin.AddHours(1), Origin.AddHours(2));

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => log.LoadSnapshotAsOfTimestampAsync(Origin.AddMilliseconds(-1)));

        Assert.Equal(DeltaProtocolErrorKind.RetentionGap, ex.Kind);
        Assert.Contains("version 0", ex.Message, StringComparison.Ordinal);
    }

    // ---- AC3: retention-aware errors (fail closed, distinguished from out-of-range future) ----

    [Fact]
    public async Task LoadVersion_BelowEarliestRetained_FailsClosed_WithRetentionGap()
    {
        // Log-cleaned table: commits 0 and 1 are gone; a complete checkpoint at v2 seeds state and commit 3
        // continues it. The earliest reconstructable version is 2.
        IStorageBackend backend = NewBackend();
        await DeltaTestHarness.WriteCheckpointAsync(backend, 2, new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: "t", schemaString: EmptySchemaUnescaped)
            .Add("a.parquet", size: 1, modificationTime: 1)
            .Add("b.parquet", size: 1, modificationTime: 1));
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 2);
        await DeltaTestHarness.WriteCommitAsync(backend, 3, DeltaTestHarness.Add("c.parquet"));
        var log = new DeltaLog(backend);

        // The latest still loads (sanity: the table is readable at/above the floor).
        Snapshot latest = await log.LoadSnapshotAsync();
        Assert.Equal(3, latest.Version);

        // Requesting a version below the retained floor is a retention gap, NOT a silent fallback to current.
        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => log.LoadSnapshotAsync(version: 1));
        Assert.Equal(DeltaProtocolErrorKind.RetentionGap, ex.Kind);
        Assert.Contains("earliest available version is 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadVersion_AboveLatest_FailsClosed_WithOutOfRangeError_NotRetentionGap()
    {
        // AC3 distinction: a FUTURE version (> latest) is an out-of-range error, kept distinct from the
        // retention-gap kind used for versions below the earliest retained.
        IStorageBackend backend = NewBackend();
        await DeltaTestHarness.WriteCommitAsync(backend, 0,
            DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata(), DeltaTestHarness.Add("a.parquet"));
        await DeltaTestHarness.WriteCommitAsync(backend, 1, DeltaTestHarness.Add("b.parquet"));

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaLog(backend).LoadSnapshotAsync(version: 5));
        Assert.Equal(DeltaProtocolErrorKind.InconsistentLog, ex.Kind);
        Assert.NotEqual(DeltaProtocolErrorKind.RetentionGap, ex.Kind);
        Assert.Contains("versions 0 through 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsOfTimestamp_BeforeEarliestRetainedCommit_FailsClosed_WithRetentionGap()
    {
        // AC3 (timestamp, log-cleaned): commits 0,1 are gone; the earliest retained commit FILE is version 2
        // (its JSON survives alongside the checkpoint). A timestamp before v2's commit time is a retention
        // gap that names the log-cleaned floor version, not version 0.
        IStorageBackend inner = NewBackend();
        await DeltaTestHarness.WriteCheckpointAsync(inner, 2, new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: "t", schemaString: EmptySchemaUnescaped)
            .Add("a.parquet", size: 1, modificationTime: 1)
            .Add("b.parquet", size: 1, modificationTime: 1));
        await DeltaTestHarness.WriteLastCheckpointAsync(inner, 2);
        await DeltaTestHarness.WriteCommitAsync(inner, 2,
            DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata(), DeltaTestHarness.Add("a.parquet"), DeltaTestHarness.Add("b.parquet"));
        await DeltaTestHarness.WriteCommitAsync(inner, 3, DeltaTestHarness.Add("c.parquet"));

        IStorageBackend backend = DeltaTestHarness.WithCommitTimestamps(
            inner, (2L, Origin.AddHours(1)), (3L, Origin.AddHours(2)));

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaLog(backend).LoadSnapshotAsOfTimestampAsync(Origin));
        Assert.Equal(DeltaProtocolErrorKind.RetentionGap, ex.Kind);
        Assert.Contains("version 2", ex.Message, StringComparison.Ordinal);
    }

    // ---- AC4: a later checkpoint/commit never mutates historical state ----

    [Fact]
    public async Task LoadVersion_WithLaterCheckpoint_DoesNotUseIt_NorLaterCommits()
    {
        // A checkpoint written at v5 (plus a _last_checkpoint hint pointing at it) must NOT be used when
        // loading v3: it is above the target, so v3 is reconstructed purely from JSON commits 0..3.
        IStorageBackend backend = NewBackend();
        await WriteBranchingHistoryAsync(backend);
        await DeltaTestHarness.WriteCheckpointAsync(backend, 5, new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: "t", schemaString: EmptySchemaUnescaped)
            .Add("c.parquet", size: 1, modificationTime: 1)
            .Add("d.parquet", size: 1, modificationTime: 1)
            .Add("e.parquet", size: 1, modificationTime: 1)
            .Add("f.parquet", size: 1, modificationTime: 1));
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 5);

        Snapshot atV3 = await new DeltaLog(backend).LoadSnapshotAsync(version: 3);

        Assert.Equal(3, atV3.Version);
        Assert.Null(atV3.Metrics.CheckpointVersion);         // the v5 checkpoint (and its hint) were ignored
        Assert.Equal(4, atV3.Metrics.ReplayedCommitCount);   // commits 0..3 replayed from JSON
        Assert.Equal(["a.parquet", "c.parquet", "d.parquet"], atV3.ActiveFiles.Select(a => a.Path));

        // Independent oracle: identical to a JSON-only table truncated at v3 (no checkpoint at all).
        IStorageBackend jsonOnly = NewBackend();
        await DeltaTestHarness.WriteCommitAsync(jsonOnly, 0,
            DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata(), DeltaTestHarness.Add("a.parquet"), DeltaTestHarness.Add("b.parquet"));
        await DeltaTestHarness.WriteCommitAsync(jsonOnly, 1, DeltaTestHarness.Add("c.parquet"));
        await DeltaTestHarness.WriteCommitAsync(jsonOnly, 2, DeltaTestHarness.Remove("b.parquet"));
        await DeltaTestHarness.WriteCommitAsync(jsonOnly, 3, DeltaTestHarness.Add("d.parquet"));
        Snapshot jsonV3 = await new DeltaLog(jsonOnly).LoadSnapshotAsync();
        Assert.Equal(DeltaTestHarness.Describe(jsonV3), DeltaTestHarness.Describe(atV3));
    }

    [Fact]
    public async Task LoadVersion_UsesNewestCheckpointAtOrBeforeTarget_NotALaterOne()
    {
        // Checkpoints at v1 AND v5. Loading v3 must seed from v1 (the newest ≤ target) and replay commits
        // 2..3 — the v5 checkpoint is above the target and must be excluded (AC4), while the v1 checkpoint is
        // used (fast path bounded to ≤ target).
        IStorageBackend backend = NewBackend();
        await WriteBranchingHistoryAsync(backend);
        await DeltaTestHarness.WriteCheckpointAsync(backend, 1, new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: "t", schemaString: EmptySchemaUnescaped)
            .Add("a.parquet", size: 1, modificationTime: 1)
            .Add("b.parquet", size: 1, modificationTime: 1)
            .Add("c.parquet", size: 1, modificationTime: 1));
        await DeltaTestHarness.WriteCheckpointAsync(backend, 5, new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: "t", schemaString: EmptySchemaUnescaped)
            .Add("c.parquet", size: 1, modificationTime: 1)
            .Add("d.parquet", size: 1, modificationTime: 1)
            .Add("e.parquet", size: 1, modificationTime: 1)
            .Add("f.parquet", size: 1, modificationTime: 1));
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 5);

        Snapshot atV3 = await new DeltaLog(backend).LoadSnapshotAsync(version: 3);

        Assert.Equal(3, atV3.Version);
        Assert.Equal(1, atV3.Metrics.CheckpointVersion);     // seeded from v1, NOT v5
        Assert.Equal(2, atV3.Metrics.ReplayedCommitCount);   // commits 2..3
        Assert.Equal(["a.parquet", "c.parquet", "d.parquet"], atV3.ActiveFiles.Select(a => a.Path));
    }
}
