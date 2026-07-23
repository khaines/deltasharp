using System.Globalization;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Tests.Delta.DeletionVectors;
using DeltaSharp.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// The Change Data Feed <b>model-based state-machine oracle</b> — the core deliverable of the hardening
/// increment (increment 4 of #193; design §3.3 oracle (b) and the folds-to-snapshot oracle (c) / INV C6).
/// </summary>
/// <remarks>
/// <para>An independent abstract model <c>M</c> (<see cref="ChangeFeedModel"/>) tracks, per committed version,
/// the expected change multiset — each change row is <c>(data columns) + _change_type ∈ {insert, delete} +
/// _commit_version</c>. A seeded generator drives a random LEGAL command sequence over
/// <c>Append / Overwrite / Delete(predicate) / Optimize(dataChange=false) / EnableCdf</c> against BOTH <c>M</c>
/// AND a real deletion-vector-backed, partitioned table (real production commits). <c>M</c> folds each command
/// with trivial independent logic — it NEVER calls the production replay — so any reader OR generation bug
/// surfaces as a mismatch. Because DeltaSharp has no UPDATE/MERGE yet (update_preimage/postimage generation is
/// deferred to #637), the legal command set produces only <c>insert</c> (from <c>add(dataChange)</c>) and
/// <c>delete</c> (from DELETE); OPTIMIZE contributes ZERO change rows (INV C4). No update is ever synthesized.</para>
/// <para><b>Oracles asserted here (mechanical predicates over the produced <see cref="ColumnBatch"/>es):</b>
/// <list type="bullet">
/// <item>INV C1 completeness + INV C3 <c>_commit_version</c> stamp — the read multiset of EVERY sub-range
/// <c>[a,b]</c> equals <c>M</c>'s expected multiset for that range (data + <c>_change_type</c> +
/// <c>_commit_version</c>), so a wrong / missing / duplicated / mis-stamped row fails closed.</item>
/// <item>INV C2 both DELETE branches — a random DELETE subset straddles files (some fully removed, some
/// retained with a new DV), verified by the derived <c>delete</c> multiset equalling the model's.</item>
/// <item>INV C4 OPTIMIZE-invisibility — an OPTIMIZE version in range contributes zero change rows.</item>
/// <item>INV C5 ascending commit order + INV C8 one <c>_commit_version</c> per batch — asserted per read in
/// <see cref="CdfTable"/>'s decode.</item>
/// <item>INV C7 <c>_commit_timestamp</c> fidelity (CDF-HP-05) — each row's <c>_commit_timestamp</c> equals its
/// version's commit-file mtime lane.</item>
/// <item>INV C9 partition fidelity — the partition column is reconstructed (never null-dropped) on BOTH the
/// implicit (add/remove) and explicit (cdc) paths, checked as part of the multiset equality.</item>
/// <item>INV C6 folds-to-snapshot — net-applying the CDF read of <c>[1,N]</c> from the empty baseline
/// reconstructs the multiset of <c>snapshot(N)</c> (a pure differential, model-free).</item>
/// </list></para>
/// <para>Every randomized case emits the house <c>[deltasharp-seed]</c> reproduction line and, on failure,
/// the full <see cref="ChangeFeedReproduction"/> bundle <c>{ seed, schema, command-sequence, backend, expected
/// change-manifest }</c> (design §3.3 / checklist 21). Seeds derive from <see cref="TestSeed"/> so a run honors
/// <c>DELTASHARP_TEST_SEED</c> and is deterministically replayable.</para>
/// </remarks>
[Collection(DeletionVectorFileTestCollection.Name)]
public sealed class ChangeFeedModelReplayTests : IDisposable
{
    private const string Scope = nameof(ChangeFeedModelReplayTests);
    private const string SchemaDescription =
        "(id long not-null, region string [partition], val long nullable); DV + CDF enabled";

    private static readonly string[] Regions = ["east", "west", "south"];
    private static readonly DateTimeOffset CommitBaseTime =
        new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly TimeSpan CommitStep = TimeSpan.FromMinutes(7);

    private readonly ITestOutputHelper _output;
    private readonly List<string> _roots = [];

    public ChangeFeedModelReplayTests(ITestOutputHelper output) => _output = output;

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
                // best-effort cleanup of the temp table root
            }
        }
    }

    /// <summary>
    /// design §3.3 oracle (b) — PRIMARY hardening fuzz. For each of many seeded random legal histories, the
    /// real CDF read of the full range AND every single-version range AND several random sub-ranges must equal
    /// the independent model's expected change multiset (INV C1/C2/C3/C4/C9), with the correct
    /// <c>_commit_version</c> and <c>_commit_timestamp</c> (INV C7 / CDF-HP-05) on every row.
    /// </summary>
    [Fact]
    public async Task ModelOracle_RandomLegalHistories_ReadFeedEqualsModelMultiset()
    {
        int baseSeed = TestSeed.Resolve();
        _output.WriteLine(
            $"[deltasharp-seed] {Scope}.ModelOracle baseSeed={baseSeed} ({TestSeed.EnvironmentVariable})");
        const int caseCount = 18;
        for (int caseIndex = 0; caseIndex < caseCount; caseIndex++)
        {
            await RunModelOracleCaseAsync(baseSeed, caseIndex);
        }
    }

    /// <summary>
    /// design §3.3 oracle (c) — INV C6 CDF-folds-to-snapshot differential. Across many seeded histories (reused
    /// generator), net-applying the CDF read of <c>[1,N]</c> from the empty baseline (inserts add rows, deletes
    /// remove them) reconstructs the multiset of the independently-read <c>snapshot(N)</c>. Model-free on the
    /// authoritative side: it cross-checks the change-feed read path against the snapshot read path directly.
    /// </summary>
    [Fact]
    public async Task FoldsToSnapshot_RandomLegalHistories_C6()
    {
        int baseSeed = TestSeed.Resolve();
        _output.WriteLine(
            $"[deltasharp-seed] {Scope}.FoldsToSnapshot baseSeed={baseSeed} ({TestSeed.EnvironmentVariable})");
        const int caseCount = 36;
        for (int caseIndex = 0; caseIndex < caseCount; caseIndex++)
        {
            await RunFoldsToSnapshotCaseAsync(baseSeed, caseIndex);
        }
    }

    private async Task RunModelOracleCaseAsync(int baseSeed, int caseIndex)
    {
        int seed = TestSeed.Combine(baseSeed, $"{Scope}.ModelOracle#{caseIndex}");
        var random = new Random(seed);
        int commandCount = 6 + random.Next(7); // 6..12 data/maintenance commands after bootstrap
        using CdfTable table = NewTable();
        var model = new ChangeFeedModel();
        GeneratedHistory history = await GenerateAsync(table, model, random, commandCount);

        // Deterministic strictly-increasing commit timestamps for the _commit_timestamp oracle (CDF-HP-05).
        table.SetCommitMtimes(CommitBaseTime, CommitStep, history.LatestVersion);

        foreach ((long a, long b) in BuildRanges(random, history.EnableVersion, history.LatestVersion))
        {
            CdfReadResult read = await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(a, b));
            IReadOnlyList<CdfChange> expected = model.ChangesInRange(a, b);

            // INV C1/C2/C3/C4/C9: multiset equality over data + _change_type + _commit_version (no timestamp).
            AssertChangeMultisetEquals(expected, read.Changes, table, history, baseSeed, seed, a, b);

            // INV C7 / CDF-HP-05: each row's _commit_timestamp equals its version's commit-file mtime lane.
            foreach (CdfChange change in read.Changes)
            {
                Assert.Equal(
                    CdfTable.ExpectedCommitMicros(CommitBaseTime, CommitStep, change.Version),
                    change.TsMicros);
            }

            // INV C5 / INV C8: every batch version is single-valued (decode asserts) AND within the range.
            foreach (long batchVersion in read.BatchVersions)
            {
                Assert.InRange(batchVersion, a, b);
            }
        }
    }

    private async Task RunFoldsToSnapshotCaseAsync(int baseSeed, int caseIndex)
    {
        int seed = TestSeed.Combine(baseSeed, $"{Scope}.FoldsToSnapshot#{caseIndex}");
        var random = new Random(seed);
        int commandCount = 5 + random.Next(8); // 5..12
        using CdfTable table = NewTable();
        var model = new ChangeFeedModel();
        GeneratedHistory history = await GenerateAsync(table, model, random, commandCount);

        CdfReadResult read = await table.ReadRangeAsync(
            DeltaChangeFeedRange.FromVersion(history.EnableVersion, history.LatestVersion));

        // Net-apply the CDF read from the empty baseline (snapshot(0) is empty), then compare to snapshot(N).
        IReadOnlyList<CdfRow> folded = FoldToSnapshot(read.Changes);
        IReadOnlyList<CdfRow> snapshot = await table.ReadSnapshotAsync(history.LatestVersion);

        string foldedManifest = RenderRows(folded);
        string snapshotManifest = RenderRows(snapshot);
        if (foldedManifest != snapshotManifest)
        {
            var repro = new ChangeFeedReproduction
            {
                BaseSeed = baseSeed,
                EffectiveSeed = seed,
                Scope = Scope,
                Schema = SchemaDescription,
                Backend = "LocalFileSystemBackend@" + table.Root,
                CommandSequence = string.Join(" | ", history.Commands),
                ExpectedManifest = $"snapshot({history.LatestVersion}):\n{snapshotManifest}",
                Actual = $"CDF[{history.EnableVersion},{history.LatestVersion}] folded to snapshot:\n{foldedManifest}",
            };
            _output.WriteLine(repro.ReproductionLine);
            Assert.Fail(repro.Render());
        }

        // Guard the GENERATOR itself: the model's own snapshot must also equal the real snapshot(N).
        Assert.Equal(snapshotManifest, RenderRows(model.Snapshot()));
    }

    // ------------------------------------------------------------------ seeded legal-history generator

    /// <summary>
    /// Drives a seeded random LEGAL command sequence against BOTH the model and a real table, asserting each
    /// committing op returns exactly the next version (version-independence) and that a DELETE removes exactly
    /// the rows the model expects. Bootstrap is fixed: v0 = empty create (CDF off), v1 = enable CDF. Every
    /// append/overwrite writes ≥1 row; a DELETE only ever targets a non-empty subset of currently-live ids (so
    /// it always matches, exercising both DV branches); OPTIMIZE may legally no-op.
    /// </summary>
    private static async Task<GeneratedHistory> GenerateAsync(
        CdfTable table, ChangeFeedModel model, Random random, int commandCount)
    {
        var commands = new List<string>();

        long v0 = await table.CreateEmptyAsync();
        Assert.Equal(0L, v0);
        long v1 = await table.EnableCdfAsync();
        Assert.Equal(1L, v1);
        model.RecordNoChangeCommit(v1);
        commands.Add("v0=create(empty,dv) | v1=enableCdf");

        long expected = v1;
        long nextId = 1;

        for (int step = 0; step < commandCount; step++)
        {
            int roll = random.Next(100);
            bool hasLive = model.LiveIds.Count > 0;

            if (roll < 42)
            {
                IReadOnlyList<CdfRow> rows = NewRows(random, ref nextId, 1 + random.Next(3));
                long v = await table.AppendAsync(rows);
                expected++;
                Assert.Equal(expected, v);
                model.RecordAppend(v, rows);
                commands.Add($"v{v}=append[{Describe(rows)}]");
            }
            else if (roll < 62 && hasLive)
            {
                IReadOnlyList<long> ids = PickSubset(random, model.LiveIds);
                DeleteResult result = await table.DeleteAsync(ids);
                Assert.NotNull(result.CommittedVersion);
                Assert.Equal(ids.Count, (int)result.RowsDeleted); // DELETE matched exactly the live rows expected
                long v = result.CommittedVersion!.Value;
                expected++;
                Assert.Equal(expected, v);
                model.RecordDelete(v, ids);
                commands.Add(
                    $"v{v}=delete[ids={string.Join(',', ids)}](dv={result.FilesWithDeletionVector},full={result.FilesFullyDeleted})");
            }
            else if (roll < 82)
            {
                IReadOnlyList<CdfRow> rows = NewRows(random, ref nextId, 1 + random.Next(3));
                long v = await table.OverwriteAsync(rows);
                expected++;
                Assert.Equal(expected, v);
                model.RecordOverwrite(v, rows);
                commands.Add($"v{v}=overwrite[{Describe(rows)}]");
            }
            else
            {
                OptimizeResult result = await table.OptimizeAsync();
                if (result.CommittedVersion is long v)
                {
                    expected++;
                    Assert.Equal(expected, v);
                    model.RecordNoChangeCommit(v);
                    commands.Add($"v{v}=optimize({result.FilesRemoved}->{result.FilesAdded},dataChange=false)");
                }
                else
                {
                    commands.Add("optimize(no-op)");
                }
            }
        }

        return new GeneratedHistory(commands, v1, expected);
    }

    private static IReadOnlyList<CdfRow> NewRows(Random random, ref long nextId, int count)
    {
        var rows = new List<CdfRow>(count);
        for (int i = 0; i < count; i++)
        {
            string region = Regions[random.Next(Regions.Length)];
            long? val = random.Next(4) == 0 ? null : random.Next(0, 1000);
            rows.Add(new CdfRow(nextId++, region, val));
        }

        return rows;
    }

    private static IReadOnlyList<long> PickSubset(Random random, IReadOnlyCollection<long> liveIds)
    {
        long[] ids = [.. liveIds];

        // 1-in-4: delete EVERY live row (exercises full-file removal across all partitions at once, INV C2).
        if (random.Next(4) == 0)
        {
            return ids;
        }

        var picked = new List<long>(ids.Length);
        foreach (long id in ids)
        {
            if (random.Next(2) == 0)
            {
                picked.Add(id);
            }
        }

        if (picked.Count == 0)
        {
            picked.Add(ids[random.Next(ids.Length)]); // a DELETE must match ≥1 row to be a legal committing op
        }

        return picked;
    }

    private static IReadOnlyList<(long Start, long End)> BuildRanges(Random random, long enable, long latest)
    {
        var ranges = new List<(long, long)> { (enable, latest) }; // the full readable range [1, N]
        for (long v = enable; v <= latest; v++)
        {
            ranges.Add((v, v)); // every single-version range (INV C8 stress + per-version fidelity)
        }

        int extra = (int)Math.Min(4, latest - enable + 1);
        for (int i = 0; i < extra; i++)
        {
            long a = enable + random.Next((int)(latest - enable + 1));
            long b = a + random.Next((int)(latest - a + 1));
            ranges.Add((a, b));
        }

        return ranges;
    }

    // ------------------------------------------------------------------ folds-to-snapshot (INV C6)

    /// <summary>Net-applies change rows (ascending commit order) into a snapshot multiset: an <c>insert</c>
    /// introduces a NEW id, a <c>delete</c> removes an EXISTING id. The generator mints globally-unique ids, so
    /// these consistency checks are exact — a double-insert or a delete of an absent row fails closed here.</summary>
    private static IReadOnlyList<CdfRow> FoldToSnapshot(IReadOnlyList<CdfChange> changes)
    {
        var live = new Dictionary<long, CdfRow>();
        foreach (CdfChange change in changes) // read already yields ascending commit order (decode asserts it)
        {
            var row = new CdfRow(change.Id, change.Region, change.Val);
            if (change.ChangeType == ChangeDataWriter.InsertChange)
            {
                Assert.False(live.ContainsKey(change.Id), $"insert must introduce a new id (id={change.Id})");
                live[change.Id] = row;
            }
            else if (change.ChangeType == ChangeDataWriter.DeleteChange)
            {
                Assert.True(live.Remove(change.Id), $"delete must remove a live id (id={change.Id})");
            }
            else
            {
                Assert.Fail($"unexpected _change_type '{change.ChangeType}' (only insert/delete exist pre-#637)");
            }
        }

        return [.. live.Values];
    }

    // ------------------------------------------------------------------ oracle assertion + rendering

    private void AssertChangeMultisetEquals(
        IReadOnlyList<CdfChange> expected,
        IReadOnlyList<CdfChange> actual,
        CdfTable table,
        GeneratedHistory history,
        int baseSeed,
        int seed,
        long start,
        long end)
    {
        string expectedManifest = RenderChanges(expected);
        string actualManifest = RenderChanges(actual);
        if (expectedManifest == actualManifest)
        {
            return;
        }

        var repro = new ChangeFeedReproduction
        {
            BaseSeed = baseSeed,
            EffectiveSeed = seed,
            Scope = Scope,
            Schema = SchemaDescription,
            Backend = "LocalFileSystemBackend@" + table.Root,
            CommandSequence = string.Join(" | ", history.Commands),
            ExpectedManifest = $"range=[{start},{end}] model change multiset:\n{expectedManifest}",
            Actual = $"range=[{start},{end}] read change multiset:\n{actualManifest}",
        };
        _output.WriteLine(repro.ReproductionLine);
        Assert.Fail(repro.Render());
    }

    /// <summary>Canonical (sorted) rendering of a change multiset — timestamp-free. The sort key
    /// <c>(version, id, change-type, region, val)</c> is total, so string equality ⇔ multiset equality.</summary>
    private static string RenderChanges(IReadOnlyList<CdfChange> changes) =>
        string.Join(
            "\n",
            changes
                .Select(c => c.WithoutTimestamp())
                .OrderBy(c => c.Version)
                .ThenBy(c => c.Id)
                .ThenBy(c => c.ChangeType, StringComparer.Ordinal)
                .ThenBy(c => c.Region, StringComparer.Ordinal)
                .ThenBy(c => c.Val ?? long.MinValue)
                .Select(c => string.Create(
                    CultureInfo.InvariantCulture,
                    $"v{c.Version} {c.ChangeType} id={c.Id} region={c.Region} val={FormatVal(c.Val)}")));

    /// <summary>Canonical (sorted) rendering of a snapshot multiset. id is unique in a snapshot, so
    /// <c>(id, region, val)</c> is a total order and string equality ⇔ multiset equality.</summary>
    private static string RenderRows(IReadOnlyList<CdfRow> rows) =>
        string.Join(
            "\n",
            rows
                .OrderBy(r => r.Id)
                .ThenBy(r => r.Region, StringComparer.Ordinal)
                .ThenBy(r => r.Val ?? long.MinValue)
                .Select(r => string.Create(
                    CultureInfo.InvariantCulture,
                    $"id={r.Id} region={r.Region} val={FormatVal(r.Val)}")));

    private static string Describe(IReadOnlyList<CdfRow> rows) =>
        string.Join(
            ",",
            rows.Select(r => string.Create(
                CultureInfo.InvariantCulture, $"{r.Id}:{r.Region}:{FormatVal(r.Val)}")));

    private static string FormatVal(long? val) =>
        val is null ? "null" : val.Value.ToString(CultureInfo.InvariantCulture);

    private CdfTable NewTable()
    {
        string root = Path.Combine(Path.GetTempPath(), "ds-cdf-model-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return new CdfTable(root);
    }

    // ------------------------------------------------------------------ generated-history record + model

    /// <summary>A generated history: the version-annotated command log (for reproduction), the first CDF-readable
    /// version (v1 = enable), and the latest committed version (N).</summary>
    private sealed record GeneratedHistory(IReadOnlyList<string> Commands, long EnableVersion, long LatestVersion);

    /// <summary>
    /// The independent abstract CDF model — it folds each command into a per-version expected change multiset
    /// with trivial local logic, NEVER calling the production replay. It maintains the current live-row set
    /// (id → row) exactly as the table's active data would, so it is simultaneously the change-feed oracle AND
    /// the folds-to-snapshot oracle's snapshot source.
    /// </summary>
    private sealed class ChangeFeedModel
    {
        private readonly SortedDictionary<long, CdfRow> _live = [];
        private readonly SortedDictionary<long, List<CdfChange>> _changesByVersion = [];

        /// <summary>The ids currently live (the snapshot key set) — the legal DELETE-target universe.</summary>
        public IReadOnlyCollection<long> LiveIds => _live.Keys;

        /// <summary>append(dataChange): every appended row is an <c>insert</c> at this version.</summary>
        public void RecordAppend(long version, IReadOnlyList<CdfRow> rows)
        {
            List<CdfChange> changes = ChangesFor(version);
            foreach (CdfRow row in rows)
            {
                _live[row.Id] = row;
                changes.Add(new CdfChange(version, ChangeDataWriter.InsertChange, row.Id, row.Region, row.Val));
            }
        }

        /// <summary>static overwrite: every currently-live row is a <c>delete</c>, every new row an
        /// <c>insert</c>, at this version (derived from the removed/added files at read time).</summary>
        public void RecordOverwrite(long version, IReadOnlyList<CdfRow> rows)
        {
            List<CdfChange> changes = ChangesFor(version);
            foreach (CdfRow old in _live.Values.ToList())
            {
                changes.Add(new CdfChange(version, ChangeDataWriter.DeleteChange, old.Id, old.Region, old.Val));
            }

            _live.Clear();
            foreach (CdfRow row in rows)
            {
                _live[row.Id] = row;
                changes.Add(new CdfChange(version, ChangeDataWriter.InsertChange, row.Id, row.Region, row.Val));
            }
        }

        /// <summary>merge-on-read DELETE: each targeted (currently-live) id is a <c>delete</c> at this version.</summary>
        public void RecordDelete(long version, IReadOnlyCollection<long> ids)
        {
            List<CdfChange> changes = ChangesFor(version);
            foreach (long id in ids)
            {
                CdfRow row = _live[id]; // the generator guarantees every targeted id is currently live
                changes.Add(new CdfChange(version, ChangeDataWriter.DeleteChange, row.Id, row.Region, row.Val));
                _live.Remove(id);
            }
        }

        /// <summary>enableCdf / OPTIMIZE (dataChange=false): a committed version that contributes NO change
        /// rows (INV C4). Recorded so the version exists in the timeline with an empty change list.</summary>
        public void RecordNoChangeCommit(long version) => ChangesFor(version);

        /// <summary>The expected change multiset for the inclusive version range <c>[start, end]</c>.</summary>
        public IReadOnlyList<CdfChange> ChangesInRange(long start, long end)
        {
            var result = new List<CdfChange>();
            foreach ((long version, List<CdfChange> changes) in _changesByVersion)
            {
                if (version >= start && version <= end)
                {
                    result.AddRange(changes);
                }
            }

            return result;
        }

        /// <summary>The current live-row multiset — the folds-to-snapshot (INV C6) generator-side cross-check.</summary>
        public IReadOnlyList<CdfRow> Snapshot() => [.. _live.Values];

        private List<CdfChange> ChangesFor(long version)
        {
            if (!_changesByVersion.TryGetValue(version, out List<CdfChange>? list))
            {
                list = [];
                _changesByVersion[version] = list;
            }

            return list;
        }
    }
}
