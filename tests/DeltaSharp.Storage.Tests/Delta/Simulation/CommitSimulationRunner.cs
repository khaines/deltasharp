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

    // ---- Fault-injection knobs (efficacy demos; design §3.4.1). Each falsifies exactly one invariant. ----

    /// <summary>Ghost paths appended to the recorded <b>read-file-set</b> that are NOT in the snapshot the
    /// writer actually pinned — a "read leaked across the pin" fault. Makes the writer's read diverge from
    /// the reconstructed snapshot at its read version, so the checker's <b>I4</b> snapshot-isolation
    /// predicate fires. Empty ⇒ no leak.</summary>
    public ImmutableArray<string> ReadSetGhosts { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>When true, the writer commits its log <c>add</c> entries but does <b>not</b> stage the
    /// backing data objects — a <b>torn write</b> (a committed reference to data that was never durably
    /// written). The checker's <b>I8</b> phantom-active predicate fires when such a file is active in the
    /// final snapshot.</summary>
    public bool SkipDataWrite { get; init; }

    /// <summary>Forces every commit attempt of this writer to embed a fixed idempotency nonce instead of a
    /// per-attempt-unique one. Two writers sharing an override land the same nonce in two versions, so the
    /// checker's <b>I6</b> nonce-uniqueness predicate fires. Null ⇒ the default unique per-writer nonce.</summary>
    public string? NonceOverride { get; init; }

    /// <summary>Overrides the SUT-reported <c>isBlindAppend</c> flag recorded in the manifest (normally
    /// derived from <see cref="Scope"/>). Setting it to contradict the checker's <b>computed</b> value (empty
    /// read-set) makes the §3.3.5 blind-append cross-check fire. Null ⇒ the honest scope-derived value.</summary>
    public bool? ReportedBlindOverride { get; init; }

    /// <summary>If set, this writer spin-yields until writer <see cref="DependsOnWriterId"/> has completed
    /// before it reads — a scripted <b>happens-before</b> so it observes that writer's committed effect
    /// deterministically (e.g. to read a file a prior writer added, then legally remove it).</summary>
    public int? DependsOnWriterId { get; init; }

    /// <summary>Forces the recorded <see cref="HistoryEvent.ConflictClass"/> for an aborted commit to a wrong
    /// value, so the checker's conflict-class re-derivation flags the mismatch. Null ⇒ the honest exception
    /// type name.</summary>
    public string? ConflictClassOverride { get; init; }
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
        int minWriter = 2,
        bool contended = false)
    {
        ArgumentNullException.ThrowIfNull(writers);

        // Livelock guard (design §3.4.3): the contention ReadBarrier holds EVERY writer at its post-read gate
        // until all writers have read, while a DependsOnWriterId writer spin-yields before its read until its
        // dependency has COMPLETED (committed). Combining the two deadlocks the interleaving — the barrier
        // will not release until the dependent reads, but the dependent will not read until its dependency
        // completes, which cannot happen because the dependency is itself parked at the barrier. Every writer
        // stays runnable, so the stall detector never fires; reject the combination up front with a clear
        // message rather than hang.
        if (contended && writers.Any(w => w.DependsOnWriterId is not null))
        {
            throw new ArgumentException(
                "A contended run (ReadBarrier) cannot be combined with a DependsOnWriterId writer: the barrier "
                + "holds every writer until all have read, but a dependent writer will not read until its "
                + "dependency completes — a livelock the stall detector cannot observe (all writers stay "
                + "runnable). Use either the contention barrier or a scripted happens-before dependency, not both.",
                nameof(writers));
        }

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
        var log = new DeltaLog(backend);

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

        // Stage backing data objects for every file the background (trusted) setup committed, modelling
        // "the data files were written before the log referenced them". Sim writers stage their own on a
        // successful commit; together this lets the I8 phantom-active predicate assert every active file has
        // a backing object (a torn write leaves one absent). Done out-of-band (scheduler idle) so it never
        // perturbs the interleaving.
        foreach (long version in background)
        {
            foreach (DeltaAction action in await log.ReadCommitActionsAsync(version, default).ConfigureAwait(false))
            {
                if (action is AddFileAction add)
                {
                    backend.StageDataFileDirect(add.Path);
                }
            }
        }

        var recorder = new HistoryRecorder();
        var manifestLines = new string[writers.Count];

        // A read barrier makes contention deterministic (design §3.4.3): when enabled, no writer proceeds to
        // its commit (put-if-absent) until EVERY writer has completed its read, so all reads observe the same
        // base version regardless of seed — a guaranteed same-version race, not a seed-dependent one.
        ReadBarrier? barrier = contended ? new ReadBarrier(writers.Count, scheduler.YieldAsync) : null;

        var bodies = new List<Func<Task>>(writers.Count);
        foreach (WriterSpec spec in writers)
        {
            WriterSpec writer = spec;
            manifestLines[writer.Id] = DescribeManifest(writer);
            bodies.Add(() => RunWriterAsync(writer, scheduler, backend, log, recorder, barrier));
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
        HistoryRecorder recorder,
        ReadBarrier? barrier)
    {
        // Scripted happens-before: wait for a prior writer's committed effect before reading (design §3.4.3).
        if (spec.DependsOnWriterId is { } dep)
        {
            while (!scheduler.IsCompleted(dep))
            {
                await scheduler.YieldAsync("await-dep:w" + spec.Id.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }
        }

        long invoke = recorder.Tick();
        Snapshot snapshot = await log.LoadSnapshotAsync().ConfigureAwait(false);
        var observed = snapshot.ActiveFiles.Select(a => a.Path).ToArray();
        recorder.RecordRead(spec.Id, snapshot.Version, observed, invoke);

        // Contention barrier: block here until every writer has read, so the ensuing put-if-absent race is a
        // guaranteed same-version race independent of the seed.
        if (barrier is not null)
        {
            await barrier.ArriveAndWaitAsync(spec.Id).ConfigureAwait(false);
        }

        ImmutableArray<string> readFileSet = spec.WholeTableReadsAllActive
            ? observed.ToImmutableArray()
            : spec.ReadFileSet;

        // Fault knob: leak ghost paths into the recorded read-set so it diverges from the pinned snapshot (I4).
        if (!spec.ReadSetGhosts.IsEmpty)
        {
            readFileSet = readFileSet.AddRange(spec.ReadSetGhosts);
        }

        ActionManifest manifest = BuildManifest(spec, readFileSet);

        // Fault knob: model the writer's data files being written before the commit (unless SkipDataWrite,
        // which leaves a torn write — a committed add referencing data that was never durably staged, I8).
        if (!spec.SkipDataWrite)
        {
            foreach (ManifestFile add in manifest.Adds)
            {
                backend.StageDataFileDirect(add.Path);
            }
        }

        // A deterministic nonce per writer/attempt (single-threaded ⇒ the counter is reproducible), and a
        // transient backoff that yields to the scheduler instead of sleeping (interleaving point 5, no clock).
        // A NonceOverride forces a fixed nonce so two writers can land the same nonce in two versions (I6).
        int nonceCounter = 0;
        var committer = new DeltaCommitter(
            backend,
            DeltaCommitter.DefaultMaxAttempts,
            nonceFactory: () => spec.NonceOverride
                ?? ("w" + spec.Id.ToString(CultureInfo.InvariantCulture) + "-" + (nonceCounter++).ToString(CultureInfo.InvariantCulture)),
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
            // Capture the observed latest-at-abort M (no yield) so the checker bounds the winner scan to (R, M];
            // -1 attempts honestly records "no successful attempt count" (design: honest history records).
            recorder.RecordCommit(spec.Id, snapshot.Version, manifest, CommitOutcome.Conflict, committedVersion: null, attempts: -1, ClassifyConflict(spec, ex), commitInvoke, "unchanged (aborted: " + ex.GetType().Name + ")", backend.LatestCommittedVersion);
        }
        catch (DeltaCommitContentionException)
        {
            recorder.RecordCommit(spec.Id, snapshot.Version, manifest, CommitOutcome.Contention, committedVersion: null, attempts: -1, conflictClass: null, commitInvoke, "unchanged (contention)", backend.LatestCommittedVersion);
        }
        catch (DeltaCommitUnknownStateException)
        {
            recorder.RecordCommit(spec.Id, snapshot.Version, manifest, CommitOutcome.UnknownState, committedVersion: null, attempts: -1, conflictClass: null, commitInvoke, "unknown state", backend.LatestCommittedVersion);
        }
    }

    // Fault knob: force a wrong reported conflict class so the checker's conflict-class re-derivation fires.
    private static string ClassifyConflict(WriterSpec spec, DeltaConcurrentModificationException ex) =>
        spec.ConflictClassOverride ?? ex.GetType().Name;

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
            SutReportedBlindAppend: spec.ReportedBlindOverride ?? (spec.Scope is DeltaReadScope.BlindAppendScope),
            ReadScope: spec.Scope switch
            {
                DeltaReadScope.BlindAppendScope => ManifestReadScope.BlindAppend,
                DeltaReadScope.ReadFilesScope => ManifestReadScope.ReadFiles,
                _ => ManifestReadScope.WholeTable,
            });
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

/// <summary>
/// A cooperative-scheduler-aware barrier that releases only once every participant has arrived. Because the
/// scheduler advances exactly one logical writer at a time, a waiting writer spin-yields (parking on the
/// scheduler's interleaving point) until the last arrival lifts the gate — so "all writers have read before
/// any commits" holds deterministically, regardless of the seed. It performs no real blocking and no
/// wall-clock waiting.
/// </summary>
internal sealed class ReadBarrier
{
    private readonly int _target;
    private readonly Func<string, Task> _yield;
    private int _arrived;

    public ReadBarrier(int target, Func<string, Task> yield)
    {
        _target = target;
        _yield = yield;
    }

    public async Task ArriveAndWaitAsync(int writerId)
    {
        _arrived++;
        while (_arrived < _target)
        {
            await _yield("read-barrier:w" + writerId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
    }
}
