using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using DeltaSharp.Storage.Delta;
using DeltaSharp.TestSupport;

namespace DeltaSharp.Storage.Tests.Delta.Simulation;

/// <summary>
/// Declares one logical writer in a simulation: the <see cref="Actions"/> it commits under
/// <see cref="Scope"/>, plus an optional <see cref="TxnKey"/> for an idempotent (retry) writer. The
/// read-file-set the checker uses to <b>compute</b> <c>isBlindAppend</c> is derived from the scope — empty
/// for a blind append, the read paths for a whole-table/read-files scope — never a self-reported flag.
/// </summary>
internal sealed record WriterSpec
{
    public required int Id { get; init; }

    public required ImmutableArray<DeltaAction> Actions { get; init; }

    public required DeltaReadScope Scope { get; init; }

    /// <summary>The manifest's read-file-set. For <see cref="DeltaReadScope.WholeTable"/> the runner fills
    /// this from the read snapshot's active files at commit time (a whole-table read depends on all of them).</summary>
    public ImmutableArray<string> ReadFileSet { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>True for a whole-table scope so the runner sets <see cref="ReadFileSet"/> to the snapshot's
    /// active files (its read-set is "every active file").</summary>
    public bool WholeTableReadsAllActive { get; init; }

    public TxnKey? Txn { get; init; }
}

/// <summary>The outcome of a full simulation run: the recorded history, the backend to reload the log from,
/// the interleaving/decision log, the set of pre-seeded background versions, and the reproduction bundle.</summary>
internal sealed record SimulationResult(
    ImmutableArray<HistoryEvent> History,
    InMemoryStorageBackend Backend,
    CooperativeScheduler Scheduler,
    ImmutableHashSet<long> BackgroundVersions,
    ReproductionBundle Bundle);

/// <summary>
/// Wires the cooperative <see cref="CooperativeScheduler"/>, the deterministic
/// <see cref="InMemoryStorageBackend"/>, the real <see cref="DeltaCommitter"/> (driven only through its
/// existing <c>BeforePutProbe</c> + injected transient-backoff seams — <b>no production change</b>), and the
/// <see cref="HistoryRecorder"/> into one reproducible run. Every writer reads a snapshot, commits, and its
/// outcome is recorded; the scheduler enumerates the interleaving from the seed.
/// </summary>
internal static class CommitSimulationRunner
{
    /// <summary>The empty-struct table schema used by the sim (matches <see cref="DeltaTestHarness"/>).</summary>
    public const string Schema = "struct<>";

    public static async Task<SimulationResult> RunAsync(
        int baseSeed,
        string scope,
        IReadOnlyList<WriterSpec> writers,
        Func<int, IBackendFaultSchedule>? faultFactory = null,
        bool disableSingleWinner = false,
        IReadOnlyList<IReadOnlyList<string>>? preCommits = null,
        int minWriter = 2)
    {
        ArgumentNullException.ThrowIfNull(writers);

        // Honor DELTASHARP_TEST_SEED for byte-for-byte replay (design §3.0): an explicitly set, valid seed
        // overrides the matrix seed so the reproduction line forces a specific failing interleaving; when
        // unset we keep the per-row matrix seed (not TestSeed.Default) so coverage stays broad.
        string? seedOverride = Environment.GetEnvironmentVariable(TestSeed.EnvironmentVariable);
        int resolvedBaseSeed = int.TryParse(seedOverride, System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out int overridden)
            ? overridden
            : baseSeed;

        int effectiveSeed = TestSeed.Combine(resolvedBaseSeed, scope);
        var scheduler = new CooperativeScheduler(effectiveSeed);
        IBackendFaultSchedule faults = faultFactory?.Invoke(effectiveSeed) ?? NoFaults.Instance;
        var backend = new InMemoryStorageBackend(scheduler.YieldAsync, faults) { DisableSingleWinner = disableSingleWinner };

        // Seed v0 (protocol + metadata) out-of-band: the scheduler is not running, so the backend does not
        // interleave — this establishes the table before the concurrent phase.
        await DeltaTestHarness.WriteCommitAsync(backend, 0, DeltaTestHarness.Protocol(minWriter: minWriter), DeltaTestHarness.Metadata());

        var background = ImmutableHashSet.CreateBuilder<long>();
        background.Add(0);
        if (preCommits is not null)
        {
            for (int i = 0; i < preCommits.Count; i++)
            {
                long version = i + 1;
                await DeltaTestHarness.WriteCommitAsync(backend, version, preCommits[i].ToArray());
                background.Add(version);
            }
        }

        var recorder = new HistoryRecorder();
        var log = new DeltaLog(backend);
        var manifestLines = new string[writers.Count];

        var bodies = new List<Func<Task>>(writers.Count);
        foreach (WriterSpec spec in writers)
        {
            WriterSpec writer = spec;
            manifestLines[writer.Id] = DescribeManifest(writer);
            bodies.Add(() => RunWriterAsync(writer, scheduler, backend, log, recorder));
        }

        // Faults are inert during out-of-band seeding above; arm them only for the interleaved phase.
        backend.FaultsActive = true;
        await scheduler.RunAsync(bodies);

        Snapshot final = await log.LoadSnapshotAsync();
        string expectedState = string.Format(
            CultureInfo.InvariantCulture,
            "version={0} active=[{1}]",
            final.Version,
            string.Join(", ", final.ActiveFiles.Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal)));

        var bundle = new ReproductionBundle
        {
            BaseSeed = resolvedBaseSeed,
            EffectiveSeed = effectiveSeed,
            Scope = scope,
            Schema = Schema,
            PartitionSpec = "unpartitioned",
            WriterCount = writers.Count,
            FaultSchedule = disableSingleWinner ? "DISABLED-SINGLE-WINNER (efficacy demo)" : faults.GetType().Name,
            Interleaving = scheduler.InterleavingSummary,
            WriterManifests = manifestLines.ToImmutableArray(),
            ExpectedState = expectedState,
        };

        return new SimulationResult(recorder.Events, backend, scheduler, background.ToImmutable(), bundle);
    }

    private static async Task RunWriterAsync(
        WriterSpec spec,
        CooperativeScheduler scheduler,
        InMemoryStorageBackend backend,
        DeltaLog log,
        HistoryRecorder recorder)
    {
        long invoke = recorder.Tick();
        Snapshot snapshot = await log.LoadSnapshotAsync().ConfigureAwait(false);
        var observed = snapshot.ActiveFiles.Select(a => a.Path).ToArray();
        recorder.RecordRead(spec.Id, snapshot.Version, observed, invoke);

        ImmutableArray<string> readFileSet = spec.WholeTableReadsAllActive
            ? observed.ToImmutableArray()
            : spec.ReadFileSet;

        ActionManifest manifest = BuildManifest(spec, readFileSet);

        // A deterministic nonce per writer/attempt (single-threaded ⇒ the counter is reproducible), and a
        // transient backoff that yields to the scheduler instead of sleeping (interleaving point 5, no clock).
        int nonceCounter = 0;
        var committer = new DeltaCommitter(
            backend,
            DeltaCommitter.DefaultMaxAttempts,
            nonceFactory: () => "w" + spec.Id.ToString(CultureInfo.InvariantCulture) + "-" + (nonceCounter++).ToString(CultureInfo.InvariantCulture),
            transientBackoff: (_, _) => scheduler.YieldAsync("backoff:w" + spec.Id.ToString(CultureInfo.InvariantCulture)))
        {
            // The one existing production seam: yield right before every put-if-absent so the scheduler can
            // interleave a racing writer at the check→publish window (design §3.4.3).
            BeforePutProbe = (_, _, _) => scheduler.YieldAsync("beforePut:w" + spec.Id.ToString(CultureInfo.InvariantCulture)),
        };

        long commitInvoke = recorder.Tick();
        try
        {
            DeltaCommitResult result = await committer.CommitAsync(snapshot, spec.Actions, spec.Scope).ConfigureAwait(false);
            CommitOutcome outcome = result.Skipped ? CommitOutcome.Skipped : CommitOutcome.Committed;
            string expected = result.Skipped
                ? "unchanged (idempotent skip)"
                : "active += {" + string.Join(", ", manifest.Adds.Select(a => a.Path)) + "}";
            recorder.RecordCommit(spec.Id, snapshot.Version, manifest, outcome, result.Version, result.Attempts, conflictClass: null, commitInvoke, expected);
        }
        catch (DeltaConcurrentModificationException ex)
        {
            recorder.RecordCommit(spec.Id, snapshot.Version, manifest, CommitOutcome.Conflict, committedVersion: null, attempts: 0, ex.GetType().Name, commitInvoke, "unchanged (aborted: " + ex.GetType().Name + ")");
        }
        catch (DeltaCommitContentionException)
        {
            recorder.RecordCommit(spec.Id, snapshot.Version, manifest, CommitOutcome.Contention, committedVersion: null, attempts: 0, conflictClass: null, commitInvoke, "unchanged (contention)");
        }
        catch (DeltaCommitUnknownStateException)
        {
            recorder.RecordCommit(spec.Id, snapshot.Version, manifest, CommitOutcome.UnknownState, committedVersion: null, attempts: 0, conflictClass: null, commitInvoke, "unknown state");
        }
    }

    private static ActionManifest BuildManifest(WriterSpec spec, ImmutableArray<string> readFileSet)
    {
        var adds = ImmutableArray.CreateBuilder<ManifestFile>();
        var removes = ImmutableArray.CreateBuilder<ManifestFile>();
        bool hasMetadata = false;
        bool hasProtocol = false;
        TxnKey? txn = spec.Txn;

        foreach (DeltaAction action in spec.Actions)
        {
            switch (action)
            {
                case AddFileAction add:
                    adds.Add(new ManifestFile(add.Path, add.DataChange));
                    break;
                case RemoveFileAction remove:
                    removes.Add(new ManifestFile(remove.Path, remove.DataChange));
                    break;
                case MetadataAction:
                    hasMetadata = true;
                    break;
                case ProtocolAction:
                    hasProtocol = true;
                    break;
                case TxnAction t:
                    txn ??= new TxnKey(t.AppId, t.Version);
                    break;
                default:
                    break;
            }
        }

        string digest = string.Join(
            "|",
            adds.Select(a => "add:" + a.Path).Concat(removes.Select(r => "rm:" + r.Path)).OrderBy(s => s, StringComparer.Ordinal));

        return new ActionManifest(
            readFileSet,
            adds.ToImmutable(),
            removes.ToImmutable(),
            hasMetadata,
            hasProtocol,
            txn,
            digest,
            SutReportedBlindAppend: spec.Scope is DeltaReadScope.BlindAppendScope);
    }

    private static string DescribeManifest(WriterSpec spec)
    {
        IEnumerable<string> adds = spec.Actions.OfType<AddFileAction>().Select(a => a.Path);
        string scope = spec.Scope switch
        {
            DeltaReadScope.BlindAppendScope => "blind-append",
            DeltaReadScope.WholeTableScope => "whole-table",
            _ => "read-files",
        };
        string txn = spec.Txn is { } t ? " txn=" + t : string.Empty;
        return string.Format(CultureInfo.InvariantCulture, "w{0}: scope={1} adds=[{2}]{3}", spec.Id, scope, string.Join(",", adds), txn);
    }
}
