using System.Collections.Immutable;
using System.Linq;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Tests.Delta.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// A <b>deterministic cooperative-scheduler + Jepsen-style history checker</b> for the Delta commit engine
/// (issue #480; design §3.3.5 / §3.4 / checklist 21). Unlike the seeded real-thread
/// <see cref="DeltaCommitSimulationTests"/> (kept as a regression), this suite drives multiple logical
/// writers through the <see cref="DeltaCommitter"/>'s await/interleaving points on a <b>single thread in a
/// seed-determined order</b> (no thread races, no wall-clock sleeps), records a Jepsen history, and
/// validates the safety invariants <b>I1/I2/I4/I5/I6/I8</b> + snapshot isolation over the reconstructed
/// <c>_delta_log</c>. Every failing case emits a reproduction line + bundle (honoring
/// <c>DELTASHARP_TEST_SEED</c>) so it replays byte-for-byte. The fault-injection efficacy test proves the
/// checker has teeth: disabling single-winner makes the checker fail on I2.
/// </summary>
public sealed class DeltaCommitJepsenSimulationTests
{
    private readonly ITestOutputHelper _output;

    public DeltaCommitJepsenSimulationTests(ITestOutputHelper output) => _output = output;

    private static readonly ImmutableSortedDictionary<string, string?> NoPartition =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly ImmutableSortedDictionary<string, string> NoTags =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private static AddFileAction Add(string path) =>
        new(path, NoPartition, 1L, 1L, DataChange: true, Stats: null, Tags: NoTags);

    private static RemoveFileAction Remove(string path) =>
        new(path, DeletionTimestamp: 1L, DataChange: true, ExtendedFileMetadata: false, NoPartition, Size: null, NoTags);

    private static TxnAction Txn(string appId, long version) => new(appId, version, LastUpdated: null);

    private static WriterSpec BlindAppender(int id, string path) => new()
    {
        Id = id,
        Actions = ImmutableArray.Create<DeltaAction>(Add(path)),
        Scope = DeltaReadScope.BlindAppend,
    };

    private async Task AssertLegalAsync(SimulationResult result)
    {
        HistoryCheckResult check = await JepsenHistoryChecker.CheckAsync(result.History, result.Backend, result.BackgroundVersions);
        _output.WriteLine(result.Bundle.ReproductionLine);
        if (!check.IsLegal)
        {
            _output.WriteLine(result.Bundle.Render());
            _output.WriteLine("VIOLATIONS:");
            _output.WriteLine(check.Describe());
        }

        Assert.True(check.IsLegal, "Jepsen history checker found invariant violations:" + Environment.NewLine + check.Describe() + Environment.NewLine + result.Bundle.Render());
    }

    // ---------------------------------------------------------------------------------------------------
    // AC1/AC3: contended same-version blind-append races — exactly-once over a seeded interleaving matrix.
    // Invariants: I1 (contiguous versions), I2 (single winner each version, losers rebase), I4 (reads pinned
    // per version), I5 (each acknowledged file visible), I6 (no duplicate file), I8 (no lost/phantom file).
    // ---------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(0x0DE17A5D, 2)]
    [InlineData(0x0DE17A5D, 4)]
    [InlineData(0x0DE17A5D, 6)]
    [InlineData(0x1234ABCD, 3)]
    [InlineData(0x1234ABCD, 5)]
    [InlineData(0x5EEDF00D, 4)]
    [InlineData(0x00C0FFEE, 6)]
    public async Task ContendedBlindAppends_PreserveExactlyOnce(int seed, int writers)
    {
        var specs = Enumerable.Range(0, writers).Select(i => BlindAppender(i, $"w{i}.parquet")).ToList();

        SimulationResult result = await CommitSimulationRunner.RunAsync(seed, nameof(ContendedBlindAppends_PreserveExactlyOnce), specs);

        await AssertLegalAsync(result);

        // Exactly-once end state: contiguous versions 1..K, every writer's file present exactly once.
        Snapshot final = await new DeltaLog(result.Backend).LoadSnapshotAsync();
        Assert.Equal(writers, (int)final.Version);
        Assert.Equal(
            Enumerable.Range(0, writers).Select(i => $"w{i}.parquet").OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            final.ActiveFiles.Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    // ---------------------------------------------------------------------------------------------------
    // Stale-snapshot rebases: writers read v0 while v1/v2 were already committed → deep multi-winner rebase.
    // ---------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(0x5EEDF00D)]
    public async Task StaleSnapshotWriters_RebasePastPriorWinners(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>>
        {
            new[] { DeltaTestHarness.Add("pre-a.parquet") },
            new[] { DeltaTestHarness.Add("pre-b.parquet") },
        };
        var specs = Enumerable.Range(0, 4).Select(i => BlindAppender(i, $"w{i}.parquet")).ToList();

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(StaleSnapshotWriters_RebasePastPriorWinners), specs, preCommits: preCommits);

        await AssertLegalAsync(result);

        Snapshot final = await new DeltaLog(result.Backend).LoadSnapshotAsync();
        Assert.Equal(6L, final.Version); // v0 seed + 2 pre-commits + 4 writers
        Assert.Contains("w0.parquet", final.ActiveFiles.Select(a => a.Path));
        Assert.Contains("pre-a.parquet", final.ActiveFiles.Select(a => a.Path));
    }

    // ---------------------------------------------------------------------------------------------------
    // I6 idempotent publication: two writers replay the SAME txn(appId,version) + file — one wins, the other
    // discovers its own txn among the winners and skips; the file is published exactly once.
    // ---------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(0x5EEDF00D)]
    [InlineData(0x00C0FFEE)]
    public async Task IdempotentTxnRetry_RacingItself_PublishesFileOnce(int seed)
    {
        WriterSpec Retryer(int id) => new()
        {
            Id = id,
            Actions = ImmutableArray.Create<DeltaAction>(Txn("stream", 5), Add("batch5.parquet")),
            Scope = DeltaReadScope.BlindAppend,
            Txn = new TxnKey("stream", 5),
        };

        var specs = new List<WriterSpec> { Retryer(0), Retryer(1) };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(IdempotentTxnRetry_RacingItself_PublishesFileOnce), specs);

        await AssertLegalAsync(result);

        Snapshot final = await new DeltaLog(result.Backend).LoadSnapshotAsync();
        Assert.Equal(1L, final.Version); // exactly one version published despite two racing retries
        Assert.Equal("batch5.parquet", Assert.Single(final.ActiveFiles).Path);

        // One writer committed, the other idempotently skipped — never two versions, never a duplicate.
        Assert.Equal(1, result.History.Count(e => e.Outcome == CommitOutcome.Committed));
        Assert.Equal(1, result.History.Count(e => e.Outcome == CommitOutcome.Skipped));
    }

    // ---------------------------------------------------------------------------------------------------
    // AC4 ambiguous-ack: the first put to v1 is durable but its acknowledgment is lost (§2.11.6). The winner
    // must resolve OursCommitted (nonce match) and never double-commit; the loser rebases. Invariants hold.
    // ---------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(0x5EEDF00D)]
    public async Task AmbiguousAck_OnFirstCommit_ResolvesIdempotently(int seed)
    {
        string v1Path = DeltaLogFiles.CommitPath(1);
        var specs = new List<WriterSpec> { BlindAppender(0, "w0.parquet"), BlindAppender(1, "w1.parquet") };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed,
            nameof(AmbiguousAck_OnFirstCommit_ResolvesIdempotently),
            specs,
            faultFactory: _ => new TargetedFaultSchedule(v1Path, FaultKind.AmbiguousDurable));

        await AssertLegalAsync(result);

        Snapshot final = await new DeltaLog(result.Backend).LoadSnapshotAsync();
        Assert.Equal(2L, final.Version); // both files land exactly once — no double-commit from the lost ack
        Assert.Equal(new[] { "w0.parquet", "w1.parquet" }, final.ActiveFiles.Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    // ---------------------------------------------------------------------------------------------------
    // Seeded recoverable fault schedule (bounded transient + durable-ambiguous, keyed on seed/path/call):
    // the correct committer absorbs every fault and the history stays legal across many seeds (design §3.4.1).
    // ---------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(0x5EEDF00D)]
    [InlineData(0x00C0FFEE)]
    [InlineData(0x0B0A0C0D)]
    public async Task SeededFaultSchedule_IsAbsorbed_HistoryLegal(int seed)
    {
        var specs = Enumerable.Range(0, 5).Select(i => BlindAppender(i, $"w{i}.parquet")).ToList();

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(SeededFaultSchedule_IsAbsorbed_HistoryLegal), specs, faultFactory: s => new SeededFaultSchedule(s));

        await AssertLegalAsync(result);

        Snapshot final = await new DeltaLog(result.Backend).LoadSnapshotAsync();
        Assert.Equal(5, final.ActiveFiles.Length);
    }

    // ---------------------------------------------------------------------------------------------------
    // Whole-table overwrite vs blind append: exercises conflict classification. Whichever interleaving the
    // seed picks, the history is legal — either the overwrite aborts with ConcurrentAppendException (the
    // checker re-derives that class from the winners) or it commits and the appender rebases.
    // ---------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(0x5EEDF00D)]
    [InlineData(0x00C0FFEE)]
    public async Task WholeTableOverwrite_VsBlindAppend_ConflictClassMatches(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>> { new[] { DeltaTestHarness.Add("base.parquet") } };
        var overwrite = new WriterSpec
        {
            Id = 0,
            Actions = ImmutableArray.Create<DeltaAction>(Remove("base.parquet"), Add("over.parquet")),
            Scope = DeltaReadScope.WholeTable,
            WholeTableReadsAllActive = true,
        };
        var appender = BlindAppender(1, "appended.parquet");

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(WholeTableOverwrite_VsBlindAppend_ConflictClassMatches), new[] { overwrite, appender }, preCommits: preCommits);

        await AssertLegalAsync(result);
    }

    // ---------------------------------------------------------------------------------------------------
    // Determinism: the same base seed yields the same interleaving and the same end state, twice.
    // ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task SameSeed_ProducesIdenticalInterleavingAndState()
    {
        List<WriterSpec> Specs() => Enumerable.Range(0, 5).Select(i => BlindAppender(i, $"w{i}.parquet")).ToList();

        SimulationResult a = await CommitSimulationRunner.RunAsync(0x0DE17A5D, nameof(SameSeed_ProducesIdenticalInterleavingAndState), Specs());
        SimulationResult b = await CommitSimulationRunner.RunAsync(0x0DE17A5D, nameof(SameSeed_ProducesIdenticalInterleavingAndState), Specs());

        Assert.Equal(a.Scheduler.InterleavingSummary, b.Scheduler.InterleavingSummary);
        Assert.Equal(a.Bundle.ExpectedState, b.Bundle.ExpectedState);
        await AssertLegalAsync(a);
    }

    // ---------------------------------------------------------------------------------------------------
    // FAULT-INJECTION EFFICACY (the checker has teeth): disabling the backend single-winner CAS lets two
    // writers "win" the same version. The Jepsen checker MUST fail — specifically on I2 (single-winner) —
    // and the exactly-once end-state assertion must also break. This proves the oracle is falsifiable.
    // ---------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(0x5EEDF00D)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    [InlineData(0x2A)]
    public async Task DisablingSingleWinner_IsCaughtBy_JepsenChecker(int seed)
    {
        var specs = new List<WriterSpec> { BlindAppender(0, "w0.parquet"), BlindAppender(1, "w1.parquet") };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(DisablingSingleWinner_IsCaughtBy_JepsenChecker), specs, disableSingleWinner: true, contended: true);

        HistoryCheckResult check = await JepsenHistoryChecker.CheckAsync(result.History, result.Backend, result.BackgroundVersions);

        _output.WriteLine(result.Bundle.Render());
        _output.WriteLine("VIOLATIONS (expected):");
        _output.WriteLine(check.Describe());

        Assert.False(check.IsLegal, "Broken single-winner CAS must be caught by the checker, but no violation was reported.");
        Assert.Contains("I2", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ===================================================================================================
    // PER-INVARIANT FAULT-INJECTION EFFICACY (the core value): every checked invariant has a matching fault
    // knob + test proving it is falsifiable. No-op'ing the corresponding checker predicate makes at least one
    // of these tests fail — the mechanical proof that the oracle has teeth (no silent false-negatives).
    // ===================================================================================================

    private static async Task<HistoryCheckResult> CheckAsync(SimulationResult result) =>
        await JepsenHistoryChecker.CheckAsync(result.History, result.Backend, result.BackgroundVersions);

    private void Dump(SimulationResult result, HistoryCheckResult check)
    {
        _output.WriteLine(result.Bundle.Render());
        _output.WriteLine("VIOLATIONS (expected):");
        _output.WriteLine(check.Describe());
    }

    // ---- I1 (CheckMonotonicity): a version-chain gap must be reported, not crash the checker. ----------
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task VersionGap_IsCaughtBy_JepsenChecker(int seed)
    {
        var specs = Enumerable.Range(0, 4).Select(i => BlindAppender(i, $"w{i}.parquet")).ToList();
        SimulationResult result = await CommitSimulationRunner.RunAsync(seed, nameof(VersionGap_IsCaughtBy_JepsenChecker), specs);

        // Drop a committed middle version to punch a hole in the chain (a fault the checker must survive).
        result.Backend.DropObject(DeltaLogFiles.CommitPath(2));

        HistoryCheckResult check = await CheckAsync(result);
        Dump(result, check);

        Assert.False(check.IsLegal, "A version-chain gap must be caught (I1), not silently accepted or thrown.");
        Assert.Contains("I1", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ---- I4 (CheckSnapshotIsolationAsync): a committing writer whose read-set diverges from its pinned ----
    // snapshot (a read that leaked a ghost file across the pin) must be caught — the red-team false-negative.
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task CommitWithGhostReadSet_IsCaughtBy_JepsenChecker(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>> { new[] { DeltaTestHarness.Add("base.parquet") } };
        var writer = new WriterSpec
        {
            Id = 0,
            Actions = ImmutableArray.Create<DeltaAction>(Add("over.parquet")),
            Scope = DeltaReadScope.WholeTable,
            WholeTableReadsAllActive = true,
            ReadSetGhosts = ImmutableArray.Create("ghost.parquet"),
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(CommitWithGhostReadSet_IsCaughtBy_JepsenChecker), new[] { writer }, preCommits: preCommits);

        HistoryCheckResult check = await CheckAsync(result);
        Dump(result, check);

        Assert.False(check.IsLegal, "A commit whose read-set diverges from its pinned snapshot must be caught (I4).");
        Assert.Contains("I4", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ---- I4 partial-read TEETH (round-4): a ReadFiles-scope commit whose recorded read-set contains a ----
    // file NOT in the pinned snapshot (a ghost) must be caught by the SUBSET branch (observed ⊄ active@R) —
    // pinning the partial-read predicate the round-3 whole-table teeth did not exercise.
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task ReadFilesScope_GhostReadSet_IsCaughtBy_JepsenChecker(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>> { new[] { DeltaTestHarness.Add("base.parquet") } };
        var writer = new WriterSpec
        {
            Id = 0,
            Actions = ImmutableArray.Create<DeltaAction>(Add("over.parquet")),
            Scope = DeltaReadScope.ReadFiles(new[] { "base.parquet" }),
            ReadFileSet = ImmutableArray.Create("base.parquet"),
            ReadSetGhosts = ImmutableArray.Create("ghost.parquet"), // a file never active at the pin.
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(ReadFilesScope_GhostReadSet_IsCaughtBy_JepsenChecker), new[] { writer }, preCommits: preCommits);

        HistoryCheckResult check = await CheckAsync(result);
        Dump(result, check);

        Assert.False(check.IsLegal, "A ReadFiles-scope read-set containing a ghost (⊄ snapshot@R) must be caught (I4 subset branch).");
        Assert.Contains("I4", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ---- A legal ReadFiles-scope STRICT SUBSET read (round-3 fix): reading fewer than all active files is ----
    // NOT a snapshot-isolation violation — it must not be false-flagged. (Complements the ghost teeth above.)
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task ReadFilesScope_StrictSubset_IsNotFalseFlagged(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>>
        {
            new[] { DeltaTestHarness.Add("a.parquet"), DeltaTestHarness.Add("b.parquet") },
        };
        var writer = new WriterSpec
        {
            Id = 0,
            Actions = ImmutableArray.Create<DeltaAction>(Add("c.parquet")),
            Scope = DeltaReadScope.ReadFiles(new[] { "a.parquet" }),
            ReadFileSet = ImmutableArray.Create("a.parquet"), // a strict subset of active {a, b} — legal.
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(ReadFilesScope_StrictSubset_IsNotFalseFlagged), new[] { writer }, preCommits: preCommits);

        HistoryCheckResult check = await CheckAsync(result);
        if (!check.IsLegal)
        {
            _output.WriteLine(check.Describe());
        }

        Assert.True(check.IsLegal, "A ReadFiles-scope strict-subset read is legal and must NOT be flagged I4:" + Environment.NewLine + check.Describe());
    }

    // ---- I5 (CheckReadYourWritesAsync): under a broken CAS an overwritten winner's file is not visible ----
    // at its own acknowledged version — a read-your-writes break.
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task LostUpdate_UnderBrokenCas_FiresReadYourWrites(int seed)
    {
        var specs = new List<WriterSpec> { BlindAppender(0, "w0.parquet"), BlindAppender(1, "w1.parquet") };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(LostUpdate_UnderBrokenCas_FiresReadYourWrites), specs, disableSingleWinner: true, contended: true);

        HistoryCheckResult check = await CheckAsync(result);
        Dump(result, check);

        Assert.False(check.IsLegal);
        Assert.Contains("I5", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ---- I6 (CheckIdempotentPublication): the SAME file published in two versions is a double-publish. ----
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task DoublePublishedFile_IsCaughtBy_JepsenChecker(int seed)
    {
        var specs = new List<WriterSpec> { BlindAppender(0, "dup.parquet"), BlindAppender(1, "dup.parquet") };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(DoublePublishedFile_IsCaughtBy_JepsenChecker), specs, contended: true);

        HistoryCheckResult check = await CheckAsync(result);
        Dump(result, check);

        Assert.False(check.IsLegal);
        Assert.Contains("I6", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ---- I6 (CheckIdempotentPublication): the SAME idempotency nonce in two versions is a double-publish. ----
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task DoublePublishedNonce_IsCaughtBy_JepsenChecker(int seed)
    {
        WriterSpec Writer(int id, string path, int? dependsOn) => new()
        {
            Id = id,
            Actions = ImmutableArray.Create<DeltaAction>(Add(path)),
            Scope = DeltaReadScope.BlindAppend,
            NonceOverride = "shared-nonce",
            DependsOnWriterId = dependsOn,
        };

        // Sequential (not racing) writers so neither rebases and re-encounters the shared nonce as its own
        // durable commit — each simply lands the same nonce at a distinct version, a true double-publish.
        var specs = new List<WriterSpec> { Writer(0, "a.parquet", null), Writer(1, "b.parquet", 0) };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(DoublePublishedNonce_IsCaughtBy_JepsenChecker), specs);

        HistoryCheckResult check = await CheckAsync(result);
        Dump(result, check);

        Assert.False(check.IsLegal);
        Assert.Contains("I6", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ---- I8 (CheckNoAnomalies): a torn write — a committed add whose backing data was never written — ----
    // leaves a phantom/dangling active file in the final snapshot.
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task TornWrite_IsCaughtBy_JepsenChecker(int seed)
    {
        var writer = new WriterSpec
        {
            Id = 0,
            Actions = ImmutableArray.Create<DeltaAction>(Add("torn.parquet")),
            Scope = DeltaReadScope.BlindAppend,
            SkipDataWrite = true,
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(TornWrite_IsCaughtBy_JepsenChecker), new[] { writer });

        HistoryCheckResult check = await CheckAsync(result);
        Dump(result, check);

        Assert.False(check.IsLegal, "A committed add with no backing data object must be caught (I8 phantom).");
        Assert.Contains("I8", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ---- SI (CheckConflictClassificationAsync): the checker uses the COMPUTED blind-append flag, so a ----
    // writer that misreports it (over a non-empty table) is flagged.
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task MisreportedBlindAppend_IsCaughtBy_JepsenChecker(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>> { new[] { DeltaTestHarness.Add("seed.parquet") } };
        var writer = new WriterSpec
        {
            Id = 0,
            Actions = ImmutableArray.Create<DeltaAction>(Add("app.parquet")),
            Scope = DeltaReadScope.BlindAppend,
            ReportedBlindOverride = false, // lies: claims non-blind though its read-set is empty.
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(MisreportedBlindAppend_IsCaughtBy_JepsenChecker), new[] { writer }, preCommits: preCommits);

        HistoryCheckResult check = await CheckAsync(result);
        Dump(result, check);

        Assert.False(check.IsLegal);
        Assert.Contains("SI", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ---- Conflict-class (CheckConflictClassificationAsync): a writer that reports a WRONG conflict class ----
    // for the winner it actually raced is flagged [CC]. Drives the REAL committer path via the
    // ConflictClassOverride knob (no synthetic HistoryEvent): two whole-table overwrites race the same base,
    // so exactly one loses the single-winner CAS and really raises ConcurrentAppend, but reports a bogus
    // class. Seed-robust — regardless of the interleaving exactly one writer aborts and mis-reports.
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task WrongConflictClass_IsCaughtBy_JepsenChecker(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>> { new[] { DeltaTestHarness.Add("base.parquet") } };
        WriterSpec Overwriter(int id, string path) => new()
        {
            Id = id,
            Actions = ImmutableArray.Create<DeltaAction>(Remove("base.parquet"), Add(path)),
            Scope = DeltaReadScope.WholeTable,
            WholeTableReadsAllActive = true,
            ConflictClassOverride = "BogusConflictException", // the aborted writer mis-reports its class.
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed,
            nameof(WrongConflictClass_IsCaughtBy_JepsenChecker),
            new[] { Overwriter(0, "over0.parquet"), Overwriter(1, "over1.parquet") },
            preCommits: preCommits,
            contended: true);

        HistoryCheckResult check = await CheckAsync(result);
        Dump(result, check);

        // Exactly one writer really aborted (single-winner) and it reported a bogus conflict class → [CC].
        Assert.Contains(result.History, e => e.Outcome == CommitOutcome.Conflict);
        Assert.False(check.IsLegal, "A wrong conflict class for the raced winner must be caught (CC).");
        Assert.Contains("CC", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ---- Conflict-class (CheckConflictClassificationAsync): a blind append aborted SOLELY by a concurrent ----
    // remove is a spurious conflict — a blind append must rebase past a remove too (only metadata/protocol or
    // a same-appId txn may abort it). Pins the remove-vs-meta/protocol un-bundling fix (FIX 2).
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task BlindAppendAbortedByConcurrentRemove_IsCaughtBy_JepsenChecker(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>> { new[] { DeltaTestHarness.Add("base.parquet") } };
        var remover = new WriterSpec
        {
            Id = 0,
            Actions = ImmutableArray.Create<DeltaAction>(Remove("base.parquet")),
            Scope = DeltaReadScope.ReadFiles(new[] { "base.parquet" }),
            ReadFileSet = ImmutableArray.Create("base.parquet"),
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(BlindAppendAbortedByConcurrentRemove_IsCaughtBy_JepsenChecker), new[] { remover }, preCommits: preCommits);

        // The remover committed the remove at v2. Inject a blind append that read v1 (EMPTY read-set) and
        // aborted claiming a conflict, though its only concurrent winner is that remove — it must rebase.
        var synthetic = SyntheticConflict(
            processId: 9,
            readVersion: 1,
            observedLatest: 2,
            readFileSet: ImmutableArray<string>.Empty,
            scope: ManifestReadScope.BlindAppend,
            reportedClass: "ConcurrentDeleteReadException");

        HistoryCheckResult check = await JepsenHistoryChecker.CheckAsync(
            result.History.Add(synthetic), result.Backend, result.BackgroundVersions);
        Dump(result, check);

        Assert.False(check.IsLegal, "A blind append aborted solely by a concurrent remove must be caught (CC).");
        Assert.Contains("CC", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ---- Conflict-class (CheckConflictClassificationAsync): a remove-only race MIS-reported as a concurrent ----
    // append is flagged [CC] — strengthens the positive class checks beyond ConcurrentAppend/Transaction (FIX 3).
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task RemoveOnlyRace_MisreportedAsAppend_IsCaughtBy_JepsenChecker(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>>
        {
            new[] { DeltaTestHarness.Add("base.parquet") },
            new[] { DeltaTestHarness.Remove("base.parquet") }, // v2 is a remove-only winner.
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(RemoveOnlyRace_MisreportedAsAppend_IsCaughtBy_JepsenChecker), Array.Empty<WriterSpec>(), preCommits: preCommits);

        var synthetic = SyntheticConflict(
            processId: 9,
            readVersion: 1,
            observedLatest: 2,
            readFileSet: ImmutableArray.Create("base.parquet"),
            scope: ManifestReadScope.WholeTable,
            reportedClass: "ConcurrentAppendException"); // wrong: expected ConcurrentDeleteRead.

        HistoryCheckResult check = await JepsenHistoryChecker.CheckAsync(
            result.History.Add(synthetic), result.Backend, result.BackgroundVersions);
        Dump(result, check);

        Assert.False(check.IsLegal, "A remove-only race reported as ConcurrentAppend must be caught (CC).");
        Assert.Contains("CC", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ---- Conflict-class (CheckConflictClassificationAsync): a metadata race MIS-reported as a concurrent ----
    // append is flagged [CC] — metadata/protocol classes are now positively validated (FIX 3).
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task MetadataRace_MisreportedAsAppend_IsCaughtBy_JepsenChecker(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>>
        {
            new[] { DeltaTestHarness.Add("base.parquet") },
            new[] { DeltaTestHarness.Metadata() }, // v2 is a metadata-change winner.
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(MetadataRace_MisreportedAsAppend_IsCaughtBy_JepsenChecker), Array.Empty<WriterSpec>(), preCommits: preCommits);

        var synthetic = SyntheticConflict(
            processId: 9,
            readVersion: 1,
            observedLatest: 2,
            readFileSet: ImmutableArray.Create("base.parquet"),
            scope: ManifestReadScope.WholeTable,
            reportedClass: "ConcurrentAppendException"); // wrong: expected MetadataChanged.

        HistoryCheckResult check = await JepsenHistoryChecker.CheckAsync(
            result.History.Add(synthetic), result.Backend, result.BackgroundVersions);
        Dump(result, check);

        Assert.False(check.IsLegal, "A metadata race reported as ConcurrentAppend must be caught (CC).");
        Assert.Contains("CC", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // ---- Conflict-class precedence SOUNDNESS (FIX 3): a winner that BOTH changes metadata AND shares the ----
    // loser's appId is MetadataChanged in reality (metadata/protocol outranks txn). A loser that correctly
    // reports MetadataChanged must NOT be flagged — the checker must not demand ConcurrentTransaction.
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task MetadataAndSameTxnWinner_ReportedMetadata_IsNotFalseFlagged(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>>
        {
            new[] { DeltaTestHarness.Add("base.parquet") },
            new[] { DeltaTestHarness.Metadata(), DeltaTestHarness.Txn("stream", 7) }, // metadata AND same appId.
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(MetadataAndSameTxnWinner_ReportedMetadata_IsNotFalseFlagged), Array.Empty<WriterSpec>(), preCommits: preCommits);

        var synthetic = SyntheticConflict(
            processId: 9,
            readVersion: 1,
            observedLatest: 2,
            readFileSet: ImmutableArray.Create("base.parquet"),
            scope: ManifestReadScope.WholeTable,
            reportedClass: "MetadataChangedException", // correct by precedence (metadata outranks txn).
            txn: new TxnKey("stream", 7));

        HistoryCheckResult check = await JepsenHistoryChecker.CheckAsync(
            result.History.Add(synthetic), result.Backend, result.BackgroundVersions);
        _output.WriteLine(result.Bundle.ReproductionLine);
        if (!check.IsLegal)
        {
            _output.WriteLine(check.Describe());
        }

        Assert.True(check.IsLegal, "A metadata+txn winner correctly reported as MetadataChanged must NOT be flagged:" + Environment.NewLine + check.Describe());
    }

    // ---- Conflict-class precedence SOUNDNESS (round-4): the real DeltaConflictChecker ranks a WINNER's ----
    // metadata change (step 2) ABOVE a LOSER's own protocol change (step 3). A loser that itself changes
    // protocol while racing a metadata-only winner correctly reports MetadataChangedException, so the checker
    // must accept it — not demand ProtocolChanged. (Before the 4-level precedence split this was a latent
    // false-positive: winner-metadata and loser-protocol were mis-ordered.)
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task MetadataWinner_ProtocolChangingLoser_ReportedMetadata_IsNotFalseFlagged(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>>
        {
            new[] { DeltaTestHarness.Add("base.parquet") },
            new[] { DeltaTestHarness.Metadata() }, // v2 winner = metadata only (NOT protocol).
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(MetadataWinner_ProtocolChangingLoser_ReportedMetadata_IsNotFalseFlagged), Array.Empty<WriterSpec>(), preCommits: preCommits);

        var synthetic = SyntheticConflict(
            processId: 9,
            readVersion: 1,
            observedLatest: 2,
            readFileSet: ImmutableArray.Create("base.parquet"),
            scope: ManifestReadScope.WholeTable,
            reportedClass: "MetadataChangedException", // correct: winner-metadata (step 2) outranks loser-protocol (step 3).
            hasProtocolChange: true); // the LOSER itself installs a protocol change.

        HistoryCheckResult check = await JepsenHistoryChecker.CheckAsync(
            result.History.Add(synthetic), result.Backend, result.BackgroundVersions);
        _output.WriteLine(result.Bundle.ReproductionLine);
        if (!check.IsLegal)
        {
            _output.WriteLine(check.Describe());
        }

        Assert.True(check.IsLegal, "Winner-metadata outranks loser-protocol: a MetadataChanged report must NOT be flagged:" + Environment.NewLine + check.Describe());
    }

    // ---- Conflict-class TEETH for the loser-protocol branch (round-4): a loser that itself changes ----
    // protocol takes the table exclusively (step 3, above data conflicts), so it must report ProtocolChanged.
    // Misreporting it as ConcurrentAppend must be caught — pins the newly-split loser-protocol branch.
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task LoserProtocolChange_MisreportedAsAppend_IsCaughtBy_JepsenChecker(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>>
        {
            new[] { DeltaTestHarness.Add("base.parquet") },
            new[] { DeltaTestHarness.Add("winner.parquet") }, // v2 winner = a plain concurrent add (no meta/protocol).
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(LoserProtocolChange_MisreportedAsAppend_IsCaughtBy_JepsenChecker), Array.Empty<WriterSpec>(), preCommits: preCommits);

        var synthetic = SyntheticConflict(
            processId: 9,
            readVersion: 1,
            observedLatest: 2,
            readFileSet: ImmutableArray.Create("base.parquet"),
            scope: ManifestReadScope.WholeTable,
            reportedClass: "ConcurrentAppendException", // wrong: the loser changed protocol ⇒ expected ProtocolChanged.
            hasProtocolChange: true);

        HistoryCheckResult check = await JepsenHistoryChecker.CheckAsync(
            result.History.Add(synthetic), result.Backend, result.BackgroundVersions);
        Dump(result, check);

        Assert.False(check.IsLegal, "A loser that changed protocol but reported ConcurrentAppend must be caught (CC).");
        Assert.Contains("CC", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    private static HistoryEvent SyntheticConflict(
        int processId,
        long readVersion,
        long observedLatest,
        ImmutableArray<string> readFileSet,
        ManifestReadScope scope,
        string reportedClass,
        TxnKey? txn = null,
        bool hasProtocolChange = false,
        bool hasMetadataChange = false)
    {
        var manifest = new ActionManifest(
            ReadFileSet: readFileSet,
            Adds: ImmutableArray<ManifestFile>.Empty,
            Removes: ImmutableArray<ManifestFile>.Empty,
            HasMetadataChange: hasMetadataChange,
            HasProtocolChange: hasProtocolChange,
            Txn: txn,
            ActionSetDigest: "synthetic-conflict",
            SutReportedBlindAppend: readFileSet.IsEmpty,
            ReadScope: scope);

        return new HistoryEvent
        {
            ProcessId = processId,
            OpType = "commit target " + (readVersion + 1),
            InvokeTime = 1_000,
            OkTime = 1_001,
            SnapshotReadVersion = readVersion,
            Manifest = manifest,
            Outcome = CommitOutcome.Conflict,
            ConflictClass = reportedClass,
            ObservedLatestVersion = observedLatest,
            Attempts = -1,
        };
    }

    // ===================================================================================================
    // SOUNDNESS: legal histories must NOT be false-flagged (the I8 false-positive + empty-table SI fixes).
    // ===================================================================================================

    // A file W0 adds is LEGALLY removed by a later, different writer W1 — this is NOT a lost update (I8).
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task CrossWriterLegalRemove_IsNotFalseFlaggedAsLostUpdate(int seed)
    {
        var w0 = new WriterSpec
        {
            Id = 0,
            Actions = ImmutableArray.Create<DeltaAction>(Add("shared.parquet")),
            Scope = DeltaReadScope.BlindAppend,
        };
        var w1 = new WriterSpec
        {
            Id = 1,
            Actions = ImmutableArray.Create<DeltaAction>(Remove("shared.parquet")),
            Scope = DeltaReadScope.ReadFiles(new[] { "shared.parquet" }),
            ReadFileSet = ImmutableArray.Create("shared.parquet"),
            DependsOnWriterId = 0, // read after W0 committed, so its remove targets a live file.
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(CrossWriterLegalRemove_IsNotFalseFlaggedAsLostUpdate), new[] { w0, w1 });

        HistoryCheckResult check = await CheckAsync(result);
        _output.WriteLine(result.Bundle.ReproductionLine);
        if (!check.IsLegal)
        {
            _output.WriteLine(check.Describe());
        }

        Assert.True(check.IsLegal, "A later legal cross-writer remove must NOT be flagged as a lost update:" + Environment.NewLine + check.Describe());

        Snapshot final = await new DeltaLog(result.Backend).LoadSnapshotAsync();
        Assert.DoesNotContain("shared.parquet", final.ActiveFiles.Select(a => a.Path));
    }

    // A whole-table overwrite of an EMPTY table has an empty read-set — it must NOT be false-flagged as a
    // blind-append mismatch (the computed-vs-reported SI cross-check is suppressed when the table was empty).
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task WholeTableOverwrite_OfEmptyTable_IsLegal(int seed)
    {
        var writer = new WriterSpec
        {
            Id = 0,
            Actions = ImmutableArray.Create<DeltaAction>(Add("first.parquet")),
            Scope = DeltaReadScope.WholeTable,
            WholeTableReadsAllActive = true,
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(WholeTableOverwrite_OfEmptyTable_IsLegal), new[] { writer });

        HistoryCheckResult check = await CheckAsync(result);
        _output.WriteLine(result.Bundle.ReproductionLine);
        if (!check.IsLegal)
        {
            _output.WriteLine(check.Describe());
        }

        Assert.True(check.IsLegal, "Whole-table overwrite of an empty table must be legal (no spurious SI):" + Environment.NewLine + check.Describe());
    }

    // A read-files writer that reads a STRICT SUBSET of the active set is LEGAL — the SI check must NOT
    // require whole-table equality for a partial (targeted) read (the I4 partial-read false-positive fix).
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task ReadFilesStrictSubset_IsNotFalseFlaggedAsSnapshotIsolation(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>>
        {
            new[] { DeltaTestHarness.Add("a.parquet"), DeltaTestHarness.Add("b.parquet") },
        };
        var writer = new WriterSpec
        {
            Id = 0,
            Actions = ImmutableArray.Create<DeltaAction>(Remove("a.parquet")),
            Scope = DeltaReadScope.ReadFiles(new[] { "a.parquet" }),
            ReadFileSet = ImmutableArray.Create("a.parquet"), // a STRICT subset of the active set {a, b}.
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(ReadFilesStrictSubset_IsNotFalseFlaggedAsSnapshotIsolation), new[] { writer }, preCommits: preCommits);

        HistoryCheckResult check = await CheckAsync(result);
        _output.WriteLine(result.Bundle.ReproductionLine);
        if (!check.IsLegal)
        {
            _output.WriteLine(check.Describe());
        }

        Assert.True(check.IsLegal, "A read-files writer reading a strict subset of active files must be legal (no false I4):" + Environment.NewLine + check.Describe());

        Snapshot final = await new DeltaLog(result.Backend).LoadSnapshotAsync();
        Assert.DoesNotContain("a.parquet", final.ActiveFiles.Select(a => a.Path));
        Assert.Contains("b.parquet", final.ActiveFiles.Select(a => a.Path));
    }

    // A WHOLE-TABLE read that OMITTED an active file across its pin IS a snapshot-isolation violation — the
    // equality branch must keep its teeth for whole-table reads (pins the I4 equality path post-fix).
    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(unchecked((int)0x7FFFFFFF))]
    public async Task WholeTableReadOmittingActiveFile_IsCaughtBy_JepsenChecker(int seed)
    {
        var preCommits = new List<IReadOnlyList<string>>
        {
            new[] { DeltaTestHarness.Add("a.parquet"), DeltaTestHarness.Add("b.parquet") },
        };
        var writer = new WriterSpec
        {
            Id = 0,
            Actions = ImmutableArray.Create<DeltaAction>(Add("over.parquet")),
            Scope = DeltaReadScope.WholeTable,
            ReadFileSet = ImmutableArray.Create("a.parquet"), // omits active b.parquet from a whole-table read.
            // WholeTableReadsAllActive left false so the recorded read-set is the (deficient) subset.
        };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(WholeTableReadOmittingActiveFile_IsCaughtBy_JepsenChecker), new[] { writer }, preCommits: preCommits);

        HistoryCheckResult check = await CheckAsync(result);
        Dump(result, check);

        Assert.False(check.IsLegal, "A whole-table read that omitted an active file must be caught (I4).");
        Assert.Contains("I4", check.ViolatedInvariants, StringComparison.Ordinal);
    }

    // Livelock guard (FIX 5): a contended (ReadBarrier) run combined with a DependsOnWriterId writer would
    // deadlock the interleaving invisibly to the stall detector (every writer stays runnable) — the runner
    // must reject the combination up front with a clear message rather than hang.
    [Fact]
    public async Task ContendedRun_WithDependency_IsRejected()
    {
        var specs = new List<WriterSpec>
        {
            BlindAppender(0, "w0.parquet"),
            new()
            {
                Id = 1,
                Actions = ImmutableArray.Create<DeltaAction>(Add("w1.parquet")),
                Scope = DeltaReadScope.BlindAppend,
                DependsOnWriterId = 0,
            },
        };

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => CommitSimulationRunner.RunAsync(
                0x0DE17A5D, nameof(ContendedRun_WithDependency_IsRejected), specs, contended: true));

        Assert.Contains("livelock", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
