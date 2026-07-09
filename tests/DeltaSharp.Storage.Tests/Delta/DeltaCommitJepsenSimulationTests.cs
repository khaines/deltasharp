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
        new(path, DeletionTimestamp: 1L, DataChange: true, ExtendedFileMetadata: false, NoPartition, Size: null);

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
    public async Task DisablingSingleWinner_IsCaughtBy_JepsenChecker(int seed)
    {
        var specs = new List<WriterSpec> { BlindAppender(0, "w0.parquet"), BlindAppender(1, "w1.parquet") };

        SimulationResult result = await CommitSimulationRunner.RunAsync(
            seed, nameof(DisablingSingleWinner_IsCaughtBy_JepsenChecker), specs, disableSingleWinner: true);

        HistoryCheckResult check = await JepsenHistoryChecker.CheckAsync(result.History, result.Backend, result.BackgroundVersions);

        _output.WriteLine(result.Bundle.Render());
        _output.WriteLine("VIOLATIONS (expected):");
        _output.WriteLine(check.Describe());

        Assert.False(check.IsLegal, "Broken single-winner CAS must be caught by the checker, but no violation was reported.");
        Assert.Contains("I2", check.ViolatedInvariants, StringComparison.Ordinal);
    }
}
