using System.Globalization;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Tests.Delta.DeletionVectors;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;
using Xunit.Abstractions;
using StructField = DeltaSharp.Types.StructField;

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
        var outcomes = new List<CaseOutcome>(caseCount);
        for (int caseIndex = 0; caseIndex < caseCount; caseIndex++)
        {
            outcomes.Add(await RunModelOracleCaseAsync(baseSeed, caseIndex, ModeForCase(baseSeed, caseIndex)));
        }

        ReportCoverage("ModelOracle", outcomes);
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
        var outcomes = new List<CaseOutcome>(caseCount);
        for (int caseIndex = 0; caseIndex < caseCount; caseIndex++)
        {
            outcomes.Add(await RunFoldsToSnapshotCaseAsync(baseSeed, caseIndex, ModeForCase(baseSeed, caseIndex)));
        }

        ReportCoverage("FoldsToSnapshot", outcomes);
    }

    private async Task<CaseOutcome> RunModelOracleCaseAsync(int baseSeed, int caseIndex, ColumnMappingMode mode)
    {
        int seed = TestSeed.Combine(baseSeed, $"{Scope}.ModelOracle#{caseIndex}");
        var random = new Random(seed);
        int commandCount = 6 + random.Next(7); // 6..12 data/maintenance/evolution commands after bootstrap
        bool forceEvolution = caseIndex < ForcedEvolutionCases; // dedicated cases run the full §2.8 chain per mode
        using CdfTable table = NewTable(mode, seed);
        var model = new ChangeFeedModel();
        GeneratedHistory history = await GenerateAsync(table, model, random, commandCount, forceEvolution);

        // Deterministic strictly-increasing commit timestamps for the _commit_timestamp oracle (CDF-HP-05).
        table.SetCommitMtimes(CommitBaseTime, CommitStep, history.LatestVersion);

        foreach ((long a, long b) in BuildRanges(random, history.EnableVersion, history.LatestVersion))
        {
            CdfReadResult read = await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(a, b));
            IReadOnlyList<CdfChange> expected = model.ChangesInRange(a, b);

            // INV C1/C2/C3/C4/C9: multiset equality over data + _change_type + _commit_version (no timestamp).
            // With #650 the data columns include the evolving amt/extra under their LOGICAL names, so a
            // physical→logical value mis-map or a mis-reconciled null-fill/promotion fails closed here.
            AssertChangeMultisetEquals(expected, read.Changes, table, history, baseSeed, seed, a, b);

            // #650 physical↔logical + type-widening SCHEMA fidelity: the reconciled CDF output schema must be the
            // model's independently-predicted end (=b) LOGICAL schema + the 3 metadata columns. A relabel, a
            // wrong widen type, or a wrong add-column presence/order fails here even if values happened to align.
            AssertExpectedEndSchema(model.ExpectedSchema(b), read.OutputSchema);

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

        return CaseOutcome.From(table);
    }

    private async Task<CaseOutcome> RunFoldsToSnapshotCaseAsync(int baseSeed, int caseIndex, ColumnMappingMode mode)
    {
        int seed = TestSeed.Combine(baseSeed, $"{Scope}.FoldsToSnapshot#{caseIndex}");
        var random = new Random(seed);
        int commandCount = 5 + random.Next(8); // 5..12
        bool forceEvolution = caseIndex < ForcedEvolutionCases;
        using CdfTable table = NewTable(mode, seed);
        var model = new ChangeFeedModel();
        GeneratedHistory history = await GenerateAsync(table, model, random, commandCount, forceEvolution);

        CdfReadResult read = await table.ReadRangeAsync(
            DeltaChangeFeedRange.FromVersion(history.EnableVersion, history.LatestVersion));

        // Net-apply the CDF read from the empty baseline (snapshot(0) is empty), then compare to snapshot(N). The
        // folded rows carry the evolving amt/extra reconciled to the LATEST schema (null-filled for files written
        // before the add; int amts promoted to long), which must match the independently-read snapshot(N).
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
                Schema = $"{SchemaDescription}; mapping={table.MappingMode}",
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
        return CaseOutcome.From(table);
    }

    // ------------------------------------------------------------------ seeded legal-history generator

    /// <summary>
    /// Drives a seeded random LEGAL command sequence against BOTH the model and a real table, asserting each
    /// committing op returns exactly the next version (version-independence) and that a DELETE removes exactly
    /// the rows the model expects. Bootstrap is fixed: v0 = empty create in the case's column-mapping mode
    /// (CDF off), v1 = enable CDF, v2 = enable type widening (both metadata-only, contributing NO change rows).
    /// Every append/overwrite writes ≥1 row under the CURRENT (possibly evolved) schema; a DELETE only ever
    /// targets a non-empty subset of currently-live ids (so it always matches, exercising both DV branches);
    /// OPTIMIZE may legally no-op. When <paramref name="forceEvolution"/> is set, a deterministic §2.8 prologue
    /// runs the full ADD amt → widen amt → ADD extra chain (with pre-add rows) so that combo is exercised in
    /// every mapping mode every run; otherwise the ADD/WIDEN commands fire at random legal points (each column
    /// added / widened at most once, guarded by the current schema state so the history stays legal).
    /// </summary>
    private static async Task<GeneratedHistory> GenerateAsync(
        CdfTable table, ChangeFeedModel model, Random random, int commandCount, bool forceEvolution)
    {
        var commands = new List<string> { $"mode={table.MappingMode}" };

        long v0 = await table.CreateEmptyAsync();
        Assert.Equal(0L, v0);
        long v1 = await table.EnableCdfAsync();
        Assert.Equal(1L, v1);
        model.RecordNoChangeCommit(v1);
        long v2 = await table.EnableTypeWideningAsync();
        Assert.Equal(2L, v2);
        model.RecordNoChangeCommit(v2);
        commands.Add("v0=create(empty,dv) | v1=enableCdf | v2=enableTypeWidening");

        long expected = v2;
        long nextId = 1;

        if (forceEvolution)
        {
            // A deterministic §2.8 evolution chain (independent of the random rolls) so EVERY forced case
            // exercises pre-add null-fill (amt AND extra) AND int→long promotion across the range, in this
            // case's mapping mode — the highest-value, least-covered combo (widen + name/id column mapping).
            IReadOnlyList<CdfRow> preAdd = NewRows(random, ref nextId, 2, table); // narrow: amt & extra null-fill later
            expected = await CommitAppendAsync(table, model, preAdd, commands, expected);

            IReadOnlyList<CdfRow> amtRows = NewRows(random, ref nextId, 2, hasAmt: true, amtIsLong: false, hasExtra: false);
            long va = await table.AddAmtColumnAsync(amtRows);
            expected = Advance(va, expected, () => model.RecordAddAmt(va, amtRows), commands,
                $"v{va}=addColumn[amt:int][{Describe(amtRows)}]");

            IReadOnlyList<CdfRow> intAmt = NewRows(random, ref nextId, 2, table); // int amt: must PROMOTE on widen
            expected = await CommitAppendAsync(table, model, intAmt, commands, expected);

            IReadOnlyList<CdfRow> wideAmt = NewRows(random, ref nextId, 2, hasAmt: true, amtIsLong: true, hasExtra: false);
            long vw = await table.WidenAmtColumnAsync(wideAmt);
            expected = Advance(vw, expected, () => model.RecordWidenAmt(vw, wideAmt), commands,
                $"v{vw}=widen[amt:int->long][{Describe(wideAmt)}]");

            IReadOnlyList<CdfRow> extraRows = NewRows(random, ref nextId, 2, hasAmt: true, amtIsLong: true, hasExtra: true);
            long ve = await table.AddExtraColumnAsync(extraRows);
            expected = Advance(ve, expected, () => model.RecordAddExtra(ve, extraRows), commands,
                $"v{ve}=addColumn[extra:long][{Describe(extraRows)}]");
        }

        for (int step = 0; step < commandCount; step++)
        {
            int roll = random.Next(100);
            bool hasLive = model.LiveIds.Count > 0;
            int count = 1 + random.Next(3);

            if (roll < 30)
            {
                IReadOnlyList<CdfRow> rows = NewRows(random, ref nextId, count, table);
                expected = await CommitAppendAsync(table, model, rows, commands, expected);
            }
            else if (roll < 44 && hasLive)
            {
                IReadOnlyList<long> ids = PickSubset(random, model.LiveIds);
                DeleteResult result = await table.DeleteAsync(ids);
                Assert.NotNull(result.CommittedVersion);
                Assert.Equal(ids.Count, (int)result.RowsDeleted); // DELETE matched exactly the live rows expected
                long v = result.CommittedVersion!.Value;
                expected = Advance(v, expected, () => model.RecordDelete(v, ids), commands,
                    $"v{v}=delete[ids={string.Join(',', ids)}](dv={result.FilesWithDeletionVector},full={result.FilesFullyDeleted})");
            }
            else if (roll < 58)
            {
                IReadOnlyList<CdfRow> rows = NewRows(random, ref nextId, count, table);
                long v = await table.OverwriteAsync(rows);
                expected = Advance(v, expected, () => model.RecordOverwrite(v, rows), commands,
                    $"v{v}=overwrite[{Describe(rows)}]");
            }
            else if (roll < 68 && table.MappingMode == ColumnMappingMode.None)
            {
                // OPTIMIZE is (by design) unsupported on a column-mapped table — it fails closed with
                // OptimizeColumnMappingUnsupportedException (name-mode-aware compaction is a tracked follow-up).
                // So a LEGAL history only emits it in `none` mode; in name/id mode this roll falls through.
                OptimizeResult result = await table.OptimizeAsync();
                if (result.CommittedVersion is long v)
                {
                    expected = Advance(v, expected, () => model.RecordNoChangeCommit(v), commands,
                        $"v{v}=optimize({result.FilesRemoved}->{result.FilesAdded},dataChange=false)");
                }
                else
                {
                    commands.Add("optimize(no-op)");
                }
            }
            else if (roll < 78 && !table.HasAmt)
            {
                IReadOnlyList<CdfRow> rows = NewRows(random, ref nextId, count, hasAmt: true, amtIsLong: false, hasExtra: table.HasExtra);
                long v = await table.AddAmtColumnAsync(rows);
                expected = Advance(v, expected, () => model.RecordAddAmt(v, rows), commands,
                    $"v{v}=addColumn[amt:int][{Describe(rows)}]");
            }
            else if (roll < 88 && table.HasAmt && table.AmtIsInt)
            {
                IReadOnlyList<CdfRow> rows = NewRows(random, ref nextId, count, hasAmt: true, amtIsLong: true, hasExtra: table.HasExtra);
                long v = await table.WidenAmtColumnAsync(rows);
                expected = Advance(v, expected, () => model.RecordWidenAmt(v, rows), commands,
                    $"v{v}=widen[amt:int->long][{Describe(rows)}]");
            }
            else if (roll < 96 && !table.HasExtra)
            {
                IReadOnlyList<CdfRow> rows = NewRows(
                    random, ref nextId, count, hasAmt: table.HasAmt, amtIsLong: table.HasAmt && !table.AmtIsInt, hasExtra: true);
                long v = await table.AddExtraColumnAsync(rows);
                expected = Advance(v, expected, () => model.RecordAddExtra(v, rows), commands,
                    $"v{v}=addColumn[extra:long][{Describe(rows)}]");
            }
            else
            {
                // Terminal fallback (an evolution roll whose precondition is already satisfied, or a DELETE with
                // no live rows): a plain append under the current schema, so every step makes deterministic progress.
                IReadOnlyList<CdfRow> rows = NewRows(random, ref nextId, count, table);
                expected = await CommitAppendAsync(table, model, rows, commands, expected);
            }
        }

        return new GeneratedHistory(commands, v1, expected);
    }

    // Commits an append under the current schema and records it, asserting version-independence. Returns the
    // advanced expected-version counter.
    private static async Task<long> CommitAppendAsync(
        CdfTable table, ChangeFeedModel model, IReadOnlyList<CdfRow> rows, List<string> commands, long expected)
    {
        long v = await table.AppendAsync(rows);
        return Advance(v, expected, () => model.RecordAppend(v, rows), commands, $"v{v}=append[{Describe(rows)}]");
    }

    // Asserts the committed version is exactly the next one (version-independence), applies the model transition,
    // logs the command, and returns the advanced counter — the shared "one legal committing op = one version" spine.
    private static long Advance(long committed, long expected, Action apply, List<string> commands, string log)
    {
        expected++;
        Assert.Equal(expected, committed);
        apply();
        commands.Add(log);
        return expected;
    }

    private static IReadOnlyList<CdfRow> NewRows(Random random, ref long nextId, int count, CdfTable table) =>
        NewRows(random, ref nextId, count, table.HasAmt, table.HasAmt && !table.AmtIsInt, table.HasExtra);

    // Generates rows for a TARGET schema state: amt is emitted only when hasAmt (int-range when narrow; a genuine
    // long — sometimes beyond int.MaxValue — once widened), extra only when hasExtra; each optional column is
    // occasionally NULL even when present, so explicit-null vs schema-on-read null-fill are both exercised.
    private static IReadOnlyList<CdfRow> NewRows(
        Random random, ref long nextId, int count, bool hasAmt, bool amtIsLong, bool hasExtra)
    {
        var rows = new List<CdfRow>(count);
        for (int i = 0; i < count; i++)
        {
            string region = Regions[random.Next(Regions.Length)];
            long? val = random.Next(4) == 0 ? null : random.Next(0, 1000);
            long? amt = NextAmt(random, hasAmt, amtIsLong);
            long? extra = hasExtra ? (random.Next(4) == 0 ? null : random.Next(0, 1_000_000)) : null;
            rows.Add(new CdfRow(nextId++, region, val, amt, extra));
        }

        return rows;
    }

    private static long? NextAmt(Random random, bool hasAmt, bool amtIsLong)
    {
        // Absent (→ null-fill at the reconciled end schema) OR an explicit null 1-in-5 even when present.
        if (!hasAmt || random.Next(5) == 0)
        {
            return null;
        }

        if (!amtIsLong)
        {
            return random.Next(0, 1000); // an int-range value (amt is still int)
        }

        // A genuine long once widened — 1-in-3 beyond int.MaxValue so the int→long widen is materially exercised.
        return random.Next(3) == 0
            ? (long)int.MaxValue + 1 + random.Next(0, 1_000_000)
            : random.Next(0, 1000);
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
            var row = new CdfRow(change.Id, change.Region, change.Val, change.Amt, change.Extra);
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
            Schema = $"{SchemaDescription}; mapping={table.MappingMode}",
            Backend = "LocalFileSystemBackend@" + table.Root,
            CommandSequence = string.Join(" | ", history.Commands),
            ExpectedManifest = $"range=[{start},{end}] model change multiset:\n{expectedManifest}",
            Actual = $"range=[{start},{end}] read change multiset:\n{actualManifest}",
        };
        _output.WriteLine(repro.ReproductionLine);
        Assert.Fail(repro.Render());
    }

    /// <summary>Canonical (sorted) rendering of a change multiset — timestamp-free. The sort key
    /// <c>(version, id, change-type, region, val, amt, extra)</c> is total, so string equality ⇔ multiset
    /// equality; rendering amt/extra makes a mis-mapped/mis-reconciled evolving column fail closed (#650).</summary>
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
                .ThenBy(c => c.Amt ?? long.MinValue)
                .ThenBy(c => c.Extra ?? long.MinValue)
                .Select(c => string.Create(
                    CultureInfo.InvariantCulture,
                    $"v{c.Version} {c.ChangeType} id={c.Id} region={c.Region} val={FormatVal(c.Val)} amt={FormatVal(c.Amt)} extra={FormatVal(c.Extra)}")));

    /// <summary>Canonical (sorted) rendering of a snapshot multiset. id is unique in a snapshot, so
    /// <c>(id, region, val, amt, extra)</c> is a total order and string equality ⇔ multiset equality.</summary>
    private static string RenderRows(IReadOnlyList<CdfRow> rows) =>
        string.Join(
            "\n",
            rows
                .OrderBy(r => r.Id)
                .ThenBy(r => r.Region, StringComparer.Ordinal)
                .ThenBy(r => r.Val ?? long.MinValue)
                .ThenBy(r => r.Amt ?? long.MinValue)
                .ThenBy(r => r.Extra ?? long.MinValue)
                .Select(r => string.Create(
                    CultureInfo.InvariantCulture,
                    $"id={r.Id} region={r.Region} val={FormatVal(r.Val)} amt={FormatVal(r.Amt)} extra={FormatVal(r.Extra)}")));

    private static string Describe(IReadOnlyList<CdfRow> rows) =>
        string.Join(
            ",",
            rows.Select(r => string.Create(
                CultureInfo.InvariantCulture,
                $"{r.Id}:{r.Region}:{FormatVal(r.Val)}:{FormatVal(r.Amt)}:{FormatVal(r.Extra)}")));

    private static string FormatVal(long? val) =>
        val is null ? "null" : val.Value.ToString(CultureInfo.InvariantCulture);

    private CdfTable NewTable(ColumnMappingMode mode, int caseSeed)
    {
        string root = Path.Combine(Path.GetTempPath(), "ds-cdf-model-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        // Physical col-<uuid> names for name/id-mapped creates are deterministic per case (seed-derived) and are
        // NEVER asserted — only the LOGICAL schema/values are — so they may differ run-to-run harmlessly.
        return new CdfTable(root, mode, string.Create(CultureInfo.InvariantCulture, $"cdf-model-{caseSeed}"));
    }

    // The three column-mapping modes fuzzed per history (#650 axis 1). The FORCED-evolution cases pin one mode
    // each (guaranteed coverage every run, independent of TestSeed); the remaining cases pick a seed-derived
    // random mode (genuine per-history fuzz).
    private static readonly ColumnMappingMode[] AllMappingModes =
        [ColumnMappingMode.None, ColumnMappingMode.Name, ColumnMappingMode.Id];

    // The first N cases run the deterministic full ADD/WIDEN/ADD §2.8 chain, one per mapping mode — so widen +
    // name/id column mapping (the least-covered, highest-risk combo) is exercised every run regardless of seed.
    private const int ForcedEvolutionCases = 3;

    private static ColumnMappingMode ModeForCase(int baseSeed, int caseIndex)
    {
        if (caseIndex < ForcedEvolutionCases)
        {
            return AllMappingModes[caseIndex]; // none / name / id — one forced-evolution case each
        }

        var rng = new Random(TestSeed.Combine(baseSeed, $"mapping-mode#{caseIndex}"));
        return AllMappingModes[rng.Next(AllMappingModes.Length)];
    }

    // The #650 physical↔logical + type-widening schema-fidelity witness: the reconciled CDF output schema must be
    // the model's independently-predicted LOGICAL end-schema data columns (name + type + nullability, verbatim)
    // followed by the three engine metadata columns (_change_type string, _commit_version long, _commit_timestamp
    // timestamp — all non-nullable). Mirrors ChangeFeedReadTests.AssertCdfOutputSchema.
    private static void AssertExpectedEndSchema(StructType expectedData, StructType actual)
    {
        Assert.Equal(expectedData.Count + 3, actual.Count);
        for (int i = 0; i < expectedData.Count; i++)
        {
            Assert.Equal(expectedData[i].Name, actual[i].Name);
            Assert.Equal(expectedData[i].DataType, actual[i].DataType);
            Assert.Equal(expectedData[i].Nullable, actual[i].Nullable);
        }

        AssertMetaField(actual[expectedData.Count], ChangeDataWriter.ChangeTypeColumn, DataTypes.StringType);
        AssertMetaField(actual[expectedData.Count + 1], ChangeDataWriter.CommitVersionColumn, DataTypes.LongType);
        AssertMetaField(actual[expectedData.Count + 2], ChangeDataWriter.CommitTimestampColumn, DataTypes.TimestampType);
    }

    private static void AssertMetaField(StructField field, string name, DataType type)
    {
        Assert.Equal(name, field.Name);
        Assert.Equal(type, field.DataType);
        Assert.False(field.Nullable, $"metadata column {name} is non-nullable");
    }

    // Emits (and asserts) the per-run column-mapping + schema-evolution coverage of a case loop — so the suite
    // both PROVES (fail-closed) all three mapping modes AND the full ADD/WIDEN/ADD evolution chain ran, and
    // REPORTS the concrete distribution to the test output for the #650 coverage summary.
    private void ReportCoverage(string label, IReadOnlyList<CaseOutcome> outcomes)
    {
        int none = outcomes.Count(o => o.Mode == ColumnMappingMode.None);
        int name = outcomes.Count(o => o.Mode == ColumnMappingMode.Name);
        int id = outcomes.Count(o => o.Mode == ColumnMappingMode.Id);
        int addedAmt = outcomes.Count(o => o.AddedAmt);
        int widened = outcomes.Count(o => o.WidenedAmt);
        int addedExtra = outcomes.Count(o => o.AddedExtra);
        _output.WriteLine(
            $"[coverage] {Scope}.{label} cases={outcomes.Count} modes(none={none},name={name},id={id}) "
            + $"evolution(addAmt={addedAmt},widenAmt={widened},addExtra={addedExtra})");

        // Every column-mapping mode is exercised every run (the forced cases pin one each — no flaky gaps).
        Assert.True(none > 0 && name > 0 && id > 0, "every column-mapping mode must be exercised each run");

        // The full ADD amt → widen amt → ADD extra chain ran (the forced cases guarantee ≥ ForcedEvolutionCases each).
        Assert.True(
            addedAmt > 0 && widened > 0 && addedExtra > 0,
            "the add-column + int→long widen evolution chain must be exercised each run");
    }

    // ------------------------------------------------------------------ generated-history record + model

    /// <summary>A generated history: the version-annotated command log (for reproduction), the first CDF-readable
    /// version (v1 = enable), and the latest committed version (N).</summary>
    private sealed record GeneratedHistory(IReadOnlyList<string> Commands, long EnableVersion, long LatestVersion);

    /// <summary>The per-case column-mapping + schema-evolution outcome, aggregated by <see cref="ReportCoverage"/>
    /// to prove/report the #650 fuzz coverage. Derived from the final table state after the history ran (amt was
    /// widened iff it exists and is no longer int).</summary>
    private sealed record CaseOutcome(ColumnMappingMode Mode, bool AddedAmt, bool WidenedAmt, bool AddedExtra)
    {
        public static CaseOutcome From(CdfTable table) =>
            new(table.MappingMode, table.HasAmt, table.HasAmt && !table.AmtIsInt, table.HasExtra);
    }

    /// <summary>
    /// The independent abstract CDF model — it folds each command into a per-version expected change multiset
    /// with trivial local logic, NEVER calling the production replay. It maintains the current live-row set
    /// (id → row) exactly as the table's active data would, so it is simultaneously the change-feed oracle AND
    /// the folds-to-snapshot oracle's snapshot source. For #650 it ALSO tracks the evolving LOGICAL schema
    /// (when <c>amt</c>/<c>extra</c> were added and when <c>amt</c> widened int→long) so it can independently
    /// predict the reconciled end schema of any range AND carry each row's <c>amt</c>/<c>extra</c> (null for a
    /// row written before that column existed — §2.8 null-fill; a widened value is numerically unchanged).
    /// </summary>
    private sealed class ChangeFeedModel
    {
        private readonly SortedDictionary<long, CdfRow> _live = [];
        private readonly SortedDictionary<long, List<CdfChange>> _changesByVersion = [];

        // The version each schema transition committed at (null = never). ExpectedSchema(end) consults these:
        // amt/extra are present iff added ≤ end; amt is long iff widened ≤ end. Independent of the reader.
        private long? _amtAddedAt;
        private long? _amtWidenedAt;
        private long? _extraAddedAt;

        /// <summary>The ids currently live (the snapshot key set) — the legal DELETE-target universe.</summary>
        public IReadOnlyCollection<long> LiveIds => _live.Keys;

        /// <summary>append(dataChange): every appended row is an <c>insert</c> at this version.</summary>
        public void RecordAppend(long version, IReadOnlyList<CdfRow> rows)
        {
            List<CdfChange> changes = ChangesFor(version);
            foreach (CdfRow row in rows)
            {
                _live[row.Id] = row;
                changes.Add(Insert(version, row));
            }
        }

        /// <summary>static overwrite: every currently-live row is a <c>delete</c>, every new row an
        /// <c>insert</c>, at this version (derived from the removed/added files at read time).</summary>
        public void RecordOverwrite(long version, IReadOnlyList<CdfRow> rows)
        {
            List<CdfChange> changes = ChangesFor(version);
            foreach (CdfRow old in _live.Values.ToList())
            {
                changes.Add(Delete(version, old));
            }

            _live.Clear();
            foreach (CdfRow row in rows)
            {
                _live[row.Id] = row;
                changes.Add(Insert(version, row));
            }
        }

        /// <summary>merge-on-read DELETE: each targeted (currently-live) id is a <c>delete</c> at this version.</summary>
        public void RecordDelete(long version, IReadOnlyCollection<long> ids)
        {
            List<CdfChange> changes = ChangesFor(version);
            foreach (long id in ids)
            {
                CdfRow row = _live[id]; // the generator guarantees every targeted id is currently live
                changes.Add(Delete(version, row));
                _live.Remove(id);
            }
        }

        /// <summary>ADD COLUMN <c>amt</c> (int) at this version: records the schema transition AND the append's
        /// <c>insert</c> rows (which carry an int <c>amt</c>). Rows written earlier keep <c>amt=null</c>, so they
        /// null-fill at any reconciled end schema that includes <c>amt</c> (§2.8).</summary>
        public void RecordAddAmt(long version, IReadOnlyList<CdfRow> rows)
        {
            _amtAddedAt = version;
            RecordAppend(version, rows);
        }

        /// <summary>WIDEN <c>amt</c> int→long at this version: records the transition AND the append's inserts
        /// (which may carry a genuine &gt;<c>int.MaxValue</c> long). Earlier int values promote to the same long
        /// at any reconciled end schema at/after this version, so their stored numeric value is unchanged.</summary>
        public void RecordWidenAmt(long version, IReadOnlyList<CdfRow> rows)
        {
            _amtWidenedAt = version;
            RecordAppend(version, rows);
        }

        /// <summary>ADD COLUMN <c>extra</c> (long) at this version: records the transition AND the append's
        /// inserts. Rows written earlier keep <c>extra=null</c> (null-fill at a reconciled end schema).</summary>
        public void RecordAddExtra(long version, IReadOnlyList<CdfRow> rows)
        {
            _extraAddedAt = version;
            RecordAppend(version, rows);
        }

        /// <summary>enableCdf / enable typeWidening / OPTIMIZE (dataChange=false): a committed version that
        /// contributes NO change rows (INV C4). Recorded so the version exists with an empty change list.</summary>
        public void RecordNoChangeCommit(long version) => ChangesFor(version);

        /// <summary>The expected change multiset for the inclusive version range <c>[start, end]</c>. Each change
        /// carries the row's <c>amt</c>/<c>extra</c> AS WRITTEN (null before the column existed); the reader's
        /// reconciliation to the end schema either null-fills (pre-add) or promotes (int→long) to the SAME value,
        /// so the value multiset is representation-agnostic — the end-schema TYPE is asserted separately via
        /// <see cref="ExpectedSchema"/>.</summary>
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

        /// <summary>The reconciled LOGICAL data schema a CDF read of a range ending at <paramref name="endVersion"/>
        /// must surface (the physical <c>col-&lt;uuid&gt;</c> / field-id storage relabelled): base
        /// <c>id/region/val</c> + <c>amt</c> (int, or long once widened ≤ end) if added ≤ end + <c>extra</c> if
        /// added ≤ end, the added columns ordered by their add-version (mergeSchema appends LAST). Predicted with
        /// trivial local state — NEVER by inspecting the reader — so a physical→logical relabel, a wrong widen
        /// type, or a wrong add-column presence/order fails the schema-fidelity assertion (#650).</summary>
        public StructType ExpectedSchema(long endVersion)
        {
            var fields = new List<StructField>
            {
                CdfTable.Schema[0], // id   (long, non-null)   — verbatim, as the reader preserves it
                CdfTable.Schema[1], // region (string, nullable, partition)
                CdfTable.Schema[2], // val  (long, nullable)
            };

            var added = new List<(long Version, StructField Field)>();
            if (_amtAddedAt is long amtAt && amtAt <= endVersion)
            {
                DataType amtType = _amtWidenedAt is long wideAt && wideAt <= endVersion
                    ? DataTypes.LongType
                    : DataTypes.IntegerType;
                added.Add((amtAt, new StructField(CdfTable.AmtColumnName, amtType, nullable: true)));
            }

            if (_extraAddedAt is long extraAt && extraAt <= endVersion)
            {
                added.Add((extraAt, new StructField(CdfTable.ExtraColumnName, DataTypes.LongType, nullable: true)));
            }

            added.Sort((a, b) => a.Version.CompareTo(b.Version));
            foreach ((_, StructField field) in added)
            {
                fields.Add(field);
            }

            return new StructType(fields);
        }

        private static CdfChange Insert(long version, CdfRow row) =>
            new(version, ChangeDataWriter.InsertChange, row.Id, row.Region, row.Val, Amt: row.Amt, Extra: row.Extra);

        private static CdfChange Delete(long version, CdfRow row) =>
            new(version, ChangeDataWriter.DeleteChange, row.Id, row.Region, row.Val, Amt: row.Amt, Extra: row.Extra);

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
