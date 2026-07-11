using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DeltaSharp.Diagnostics;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Diagnostics;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Thrown when OPTIMIZE encounters a compaction input that was written under an <b>older</b> table schema
/// (an additively schema-evolved table, #190) so the file is physically narrower than the current snapshot
/// data schema. Compacting such a file requires read-side null-fill of the absent, later-added columns,
/// which is not yet implemented (issue <b>#497</b> — "Evolved-column read null-fill"). Rather than surface
/// the misleading raw <see cref="DeltaStorageException"/> ("column … not present in the Parquet file
/// schema", which reads like file corruption), OPTIMIZE fails <b>closed</b> with this clear, actionable
/// error: it is thrown <b>before</b> any commit, so the inputs stay active, the version is unchanged, and
/// any already-written outputs are ignorable orphans (never a partial commit).
/// </summary>
internal sealed class OptimizeSchemaEvolutionException : Exception
{
    internal OptimizeSchemaEvolutionException(string filePath, Exception innerException)
        : base(
            $"OPTIMIZE cannot compact file '{filePath}' because it was written under an older table schema " +
            "and read-side null-fill for the later-added column(s) is not yet implemented (issue #497). " +
            "OPTIMIZE fails closed rather than compacting a file written under an older schema; the table is " +
            "left unchanged. Re-run OPTIMIZE once #497 (evolved-column read null-fill) lands.",
            innerException) => FilePath = filePath;

    /// <summary>The compaction input whose physical schema is narrower than the current data schema.</summary>
    internal string FilePath { get; }
}

/// <summary>
/// One partition's contribution to an <see cref="DeltaOptimize.OptimizeAsync(System.Func{System.Collections.Generic.IReadOnlyDictionary{string, string?}, bool}?, long?, bool, System.Threading.CancellationToken)"/>
/// run (STORY-05.6.1 AC3): the canonical <see cref="PartitionValues"/> (empty for an unpartitioned table),
/// how many small input files were <see cref="FilesRemoved"/> and how many compacted files were
/// <see cref="FilesAdded"/> in their place, and the before/after byte totals plus the row counts.
/// <see cref="RowCount"/> is the <b>measured</b> compacted row count on a real run and <c>0</c> on a dry run
/// (nothing is written or measured); <see cref="EstimatedRowCount"/> is the best-effort advisory sum of the
/// inputs' recorded record counts (a stats-less input contributes <c>0</c>), populated on both a dry run and
/// a real run so a caller can compare the plan estimate to the measured actual. For a dry run
/// <see cref="BytesAfter"/> is also <c>0</c> (nothing is written).
/// </summary>
internal sealed record OptimizePartitionSummary(
    ImmutableSortedDictionary<string, string?> PartitionValues,
    int FilesRemoved,
    int FilesAdded,
    long BytesBefore,
    long BytesAfter,
    long RowCount,
    long EstimatedRowCount);

/// <summary>
/// The structured outcome of a <see cref="DeltaOptimize.OptimizeAsync(System.Func{System.Collections.Generic.IReadOnlyDictionary{string, string?}, bool}?, long?, bool, System.Threading.CancellationToken)"/>
/// run: the <see cref="ReadVersion"/> the plan was computed against, the <see cref="CommittedVersion"/> the
/// compaction became visible at (<see langword="null"/> for a dry run or a no-op — nothing to compact), the
/// effective <see cref="TargetFileSize"/>, the total files removed/added and bytes before/after, the
/// <see cref="RowCount"/>/<see cref="EstimatedRowCount"/> (see below), and the per-partition
/// <see cref="Partitions"/> summary (AC3).
///
/// <para><b>Row counts — two distinct fidelities.</b> <see cref="RowCount"/> is the <b>measured</b> row
/// count actually read and rewritten by a real compaction run (AC1 row-count equivalence); it is <c>0</c> on
/// a dry run or a no-op, where nothing is written or measured. <see cref="EstimatedRowCount"/> is the
/// best-effort <b>advisory</b> plan estimate — the sum of the selected inputs' recorded
/// <see cref="FileStatistics.NumRecords"/> (a stats-less input contributes <c>0</c>, so a stats-less table's
/// estimate can under-report). It is populated on a dry run (so a dry-run caller still gets a row-count
/// signal) <b>and</b> on a real run (so a caller can compare the plan estimate to the measured actual). Read
/// <see cref="RowCount"/> for the ground truth of a committed run; read <see cref="EstimatedRowCount"/> for a
/// dry-run advisory.</para>
/// </summary>
internal sealed record OptimizeResult(
    long ReadVersion,
    long? CommittedVersion,
    bool DryRun,
    long TargetFileSize,
    int FilesRemoved,
    int FilesAdded,
    long BytesBefore,
    long BytesAfter,
    long RowCount,
    long EstimatedRowCount,
    ImmutableArray<OptimizePartitionSummary> Partitions);

/// <summary>
/// Delta <b>OPTIMIZE</b> / small-file compaction (design §2.9.2/§2.11.2, STORY-05.6.1 / #195). It rewrites
/// a table's many small active files into fewer, larger ones <b>without changing the data</b> — the row
/// <b>multiset</b> is preserved exactly (every row appears once, unchanged), only its physical file layout
/// is rearranged. Row <i>order</i> is <b>not</b> a contract: within a compacted output rows appear in the
/// group's packing order (input-by-input), not the table's logical order, so correctness is asserted as a
/// sorted-multiset content oracle, never a positional one. The whole operation publishes as <b>one</b> Delta
/// commit that <c>remove</c>s every compacted input and <c>add</c>s the compacted outputs, and both sides
/// carry <c>dataChange=false</c> (§2.11.2). It mirrors the shape of <see cref="DeltaVacuum"/> (constructed
/// over an <see cref="IStorageBackend"/> with an injected log, committer, <see cref="TimeProvider"/>, logger,
/// and telemetry) and reuses the exact write/commit machinery of <see cref="DeltaTableWriter"/> rather than
/// reinventing it.
///
/// <para><b>Why <c>dataChange=false</c> (the correctness core).</b> Compaction only relocates existing
/// rows, so its <c>add</c>/<c>remove</c> actions are marked <c>dataChange=false</c>. This is precisely what
/// lets OPTIMIZE run concurrently with blind appends: the conflict matrix's Compaction row (§2.11.2) says a
/// concurrent append creates <b>new</b> files that can never be in compaction's remove/read set, so the two
/// rebase past each other. A streaming reader likewise ignores a <c>dataChange=false</c> rewrite.</para>
///
/// <para><b>Conflict scope (AC2).</b> The commit is scoped with
/// <see cref="DeltaReadScope.ReadFiles(System.Collections.Generic.IEnumerable{string})"/> over exactly the
/// compaction <b>input</b> paths. If a concurrent writer <c>remove</c>s or re-<c>add</c>s any input since the
/// read snapshot, that scope aborts the compaction (its inputs changed underneath it) — the destructive
/// selection is exact, never <see cref="Snapshot.PruneFiles"/>'s read-oriented over-approximation. A
/// concurrent blind append of a <i>new</i> file is not in the read set, so the committer rebases past it.</para>
///
/// <para><b>Candidate selection (AC3).</b> An optional partition predicate scopes OPTIMIZE to a subset of
/// partitions; only files whose partition values satisfy it are eligible, and every unselected active file
/// is left untouched. Within each eligible partition (keyed by the canonical
/// <see cref="PartitionKeyBuilder"/> key — the same exact-match keying a dynamic overwrite uses) the
/// <b>small</b> files (byte size below the target) are bin-packed into compaction groups whose combined size
/// is at most the target; a group of a single file is skipped (rewriting one file into one file gains
/// nothing).</para>
///
/// <para><b>Schema-evolution limitation (fails closed).</b> OPTIMIZE reads every input under the
/// <i>current</i> snapshot data schema. On an additively schema-evolved table (#190) a pre-evolution Parquet
/// file is physically narrower than that schema; compacting it needs read-side null-fill of the later-added
/// columns, which is not yet implemented (issue <b>#497</b>). Until #497 lands, OPTIMIZE detects the narrow
/// input and fails closed with a clear <see cref="OptimizeSchemaEvolutionException"/> (thrown before any
/// commit — the table is unchanged) rather than compacting or surfacing a misleading "column not present"
/// corruption error.</para>
///
/// <para><b>Orphan-safe failure (AC4).</b> Compacted files are written to storage <b>before</b> the commit.
/// If the commit fails (a conflict, a crash, or the injected <see cref="BeforeCommitProbe"/>), the table's
/// state is unchanged — the inputs stay active and the written-but-uncommitted outputs are simply orphans,
/// invisible to readers (the log is truth) and reclaimable by <see cref="DeltaVacuum"/>/
/// <see cref="OrphanCleanup"/> like any other staged-but-never-committed file. No special rollback hook is
/// needed.</para>
///
/// <para><b>Memory (eager materialization, v1).</b> A compaction group is materialized <b>decompressed</b>
/// in memory (a <c>List&lt;ColumnBatch&gt;</c> of every input's decoded rows) <i>and</i> re-serialized into
/// an in-memory output <see cref="System.IO.MemoryStream"/> before being published. The <b>decoded</b> peak
/// is therefore not bounded by the ≈128&#160;MiB target file-size knob — that knob bounds the packed
/// <i>compressed</i> input size, and highly compressible data decodes far larger, so a group's transient
/// footprint can exceed the target. It is bounded <b>per group</b> (each group's batches and output buffer
/// are group-local and released for GC each loop iteration), never accumulated across the whole run.
/// Streaming (chunked) rewrite that removes the eager full-group materialization, plus clustering-aware /
/// Z-order compaction, are tracked follow-ups.</para>
/// </summary>
internal sealed class DeltaOptimize
{
    /// <summary>The default target compacted-file size, ≈128&#160;MiB (design §2.9.2, Spark
    /// <c>parquet.block.size</c>). A file smaller than the target is a compaction candidate; a compaction
    /// group's combined input size is packed to at most the target.</summary>
    public const long DefaultTargetFileSize = 128L * 1024 * 1024;

    /// <summary>Spark's sentinel partition-directory segment for a null partition value, used when composing
    /// a Hive-style output path.</summary>
    private const string HiveDefaultPartition = "__HIVE_DEFAULT_PARTITION__";

    private static readonly ImmutableSortedDictionary<string, string?> EmptyPartition =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly ImmutableSortedDictionary<string, string> NoTags =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private readonly IStorageBackend _backend;
    private readonly DeltaLog _log;
    private readonly DeltaCommitter _committer;
    private readonly TimeProvider _timeProvider;
    private readonly StatisticsPolicy _statisticsPolicy;
    private readonly ParquetFileReader _reader;
    private readonly ParquetFileWriter _writer;
    private readonly Func<string> _fileNameFactory;
    private readonly ILogger<DeltaOptimize> _logger;
    private readonly DeltaStorageTelemetry _telemetry;

    /// <summary>The shared correlation scope attached to every OPTIMIZE log line (design §7.2.1), cached so
    /// <see cref="ILogger.BeginScope"/> allocates no new state array per run.</summary>
    private static readonly KeyValuePair<string, object?>[] OptimizeLogScope =
    {
        new(DeltaSharpTelemetry.ComponentKey, DeltaStorageTelemetry.DeltaComponent),
        new(DeltaSharpTelemetry.OperationKey, DeltaStorageTelemetry.OptimizeOperation),
    };

    /// <summary>Test seam (null/inert in production): awaited once after every compacted file has been
    /// written to storage and <b>before</b> the single Delta commit, so a test can inject a pre-commit
    /// failure and assert the table is left unchanged with the compacted files orphaned (AC4).</summary>
    internal volatile Func<CancellationToken, Task>? BeforeCommitProbe;

    /// <summary>Creates an OPTIMIZE over <paramref name="backend"/> (rooted at the Delta table directory),
    /// constructing its own log reader + committer and using the system clock.</summary>
    public DeltaOptimize(IStorageBackend backend)
        : this(backend, new DeltaLog(backend), new DeltaCommitter(backend))
    {
    }

    /// <summary>Creates an OPTIMIZE over an explicit reader + committer (tests inject a committer with a
    /// race probe, a deterministic clock for tombstone/modification timestamps, and a deterministic
    /// compacted-file name factory so output paths are predictable), plus an optional injected
    /// <paramref name="logger"/> / <paramref name="telemetry"/> so a test can subscribe to the OPTIMIZE
    /// span and metrics in isolation (mirroring <see cref="DeltaVacuum"/>).</summary>
    internal DeltaOptimize(
        IStorageBackend backend,
        DeltaLog log,
        DeltaCommitter committer,
        TimeProvider? timeProvider = null,
        StatisticsPolicy? statisticsPolicy = null,
        ParquetFileReader? reader = null,
        ParquetFileWriter? writer = null,
        Func<string>? compactedFileNameFactory = null,
        ILogger<DeltaOptimize>? logger = null,
        DeltaStorageTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(committer);
        _backend = backend;
        _log = log;
        _committer = committer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _statisticsPolicy = statisticsPolicy ?? StatisticsPolicy.Default;
        _reader = reader ?? new ParquetFileReader();
        _writer = writer ?? new ParquetFileWriter();
        _fileNameFactory = compactedFileNameFactory ?? DefaultFileNameFactory;
        _logger = logger ?? NullLogger<DeltaOptimize>.Instance;
        _telemetry = telemetry ?? DeltaStorageTelemetry.Shared;
    }

    /// <summary>The production compacted-file name source: 128 bits from a cryptographic RNG, hex-encoded
    /// (never the banned <c>Guid.NewGuid</c>/<c>System.Random</c>), so two concurrent OPTIMIZE runs never
    /// collide on an output path while a deterministic factory can be injected in tests.</summary>
    internal static string DefaultFileNameFactory() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Runs OPTIMIZE against the latest committed snapshot.
    /// </summary>
    /// <param name="partitionFilter">An optional predicate over a file's partition values; when supplied,
    /// only files whose partition satisfies it are eligible for compaction and every unselected active file
    /// is left untouched (AC3). <see langword="null"/> considers every partition.</param>
    /// <param name="targetFileSize">The target compacted-file size in bytes; a file smaller than this is a
    /// candidate and a compaction group is packed to at most this size. Defaults to
    /// <see cref="DefaultTargetFileSize"/>.</param>
    /// <param name="dryRun">When <see langword="true"/>, the plan (which files <i>would</i> be compacted) is
    /// reported without writing any file or committing.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetFileSize"/> is not positive.</exception>
    public async Task<OptimizeResult> OptimizeAsync(
        Func<IReadOnlyDictionary<string, string?>, bool>? partitionFilter = null,
        long? targetFileSize = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        Snapshot snapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
        return await OptimizeAsync(snapshot, partitionFilter, targetFileSize, dryRun, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs OPTIMIZE against an explicit <paramref name="readSnapshot"/> (the test seam that lets a caller
    /// commit a concurrent writer before OPTIMIZE commits, so the read-scope conflict/rebase behavior is
    /// exercised deterministically — the same pattern <see cref="DeltaTableWriter"/> exposes).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetFileSize"/> is not positive.</exception>
    /// <exception cref="DeltaConcurrentModificationException">A concurrent commit changed one of the
    /// compaction inputs since <paramref name="readSnapshot"/> (AC2).</exception>
    internal async Task<OptimizeResult> OptimizeAsync(
        Snapshot readSnapshot,
        Func<IReadOnlyDictionary<string, string?>, bool>? partitionFilter = null,
        long? targetFileSize = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        long target = targetFileSize ?? DefaultTargetFileSize;
        if (target <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFileSize), target, "Target file size must be positive.");
        }

        using IDisposable? logScope = _logger.BeginScope(OptimizeLogScope);
        DeltaOptimizeLog.OptimizeStarted(_logger, _backend.Kind.ToLabel(), target, dryRun);

        long startTimestamp = Stopwatch.GetTimestamp();
        using Activity? activity = _telemetry.StartOptimizeActivity(_backend.Kind);
        try
        {
            OptimizeResult result = await RunOptimizeAsync(
                readSnapshot, partitionFilter, target, dryRun, cancellationToken).ConfigureAwait(false);

            double seconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
            OptimizeOutcome outcome = result.DryRun
                ? OptimizeOutcome.DryRun
                : result.CommittedVersion is null ? OptimizeOutcome.NoOp : OptimizeOutcome.Completed;
            _telemetry.RecordOptimizeTerminal(
                outcome, seconds, result.FilesRemoved, result.FilesAdded, result.BytesBefore);
            SetOutcomeTag(activity, outcome);
            if (outcome == OptimizeOutcome.NoOp)
            {
                DeltaOptimizeLog.OptimizeNoOp(_logger, result.ReadVersion, seconds * 1000);
            }
            else
            {
                DeltaOptimizeLog.OptimizeCompleted(
                    _logger,
                    result.ReadVersion,
                    result.CommittedVersion ?? result.ReadVersion,
                    result.FilesRemoved,
                    result.FilesAdded,
                    result.DryRun,
                    seconds * 1000);
            }

            return result;
        }
        catch (DeltaConcurrentModificationException ex)
        {
            // AC2 fail-closed abort: a concurrent commit changed one of the compaction inputs. The inputs
            // stay active and any written outputs are orphans — a domain outcome (Warning), not a failure.
            _telemetry.RecordOptimizeTerminal(
                OptimizeOutcome.Aborted, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, 0, 0, 0);
            SetOutcomeTag(activity, OptimizeOutcome.Aborted);
            DeltaOptimizeLog.OptimizeAborted(_logger, ex.GetType().Name);
            throw;
        }
        catch (OperationCanceledException)
        {
            _telemetry.RecordOptimizeTerminal(
                OptimizeOutcome.Cancelled, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, 0, 0, 0);
            SetOutcomeTag(activity, OptimizeOutcome.Cancelled);
            DeltaOptimizeLog.OptimizeCanceled(_logger);
            throw;
        }
        catch (Exception ex)
        {
            // Any other pre-commit failure (a schema-evolution guard, a torn read, a pre-commit probe): the
            // table is unchanged and any written outputs are orphaned (AC4). Fail-closed (Error).
            _telemetry.RecordOptimizeTerminal(
                OptimizeOutcome.Failure, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, 0, 0, 0);
            SetOutcomeTag(activity, OptimizeOutcome.Failure);
            activity?.SetStatus(ActivityStatusCode.Error);
            DeltaOptimizeLog.OptimizeFailed(_logger, ex.GetType().Name);
            throw;
        }
    }

    private async Task<OptimizeResult> RunOptimizeAsync(
        Snapshot readSnapshot,
        Func<IReadOnlyDictionary<string, string?>, bool>? partitionFilter,
        long target,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ImmutableArray<string> partitionColumns = readSnapshot.Metadata.PartitionColumns;
        IReadOnlyList<CompactionGroup> groups = PlanCompaction(readSnapshot, partitionColumns, partitionFilter, target);

        if (dryRun || groups.Count == 0)
        {
            return BuildDryRunOrEmptyResult(readSnapshot.Version, dryRun, target, partitionColumns, groups);
        }

        StructType dataSchema = BuildDataSchema(readSnapshot.Schema, partitionColumns);
        long timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var actions = new List<DeltaAction>();
        var inputPaths = new List<string>();
        var summaries = new Dictionary<string, PartitionAccumulator>(StringComparer.Ordinal);

        foreach (CompactionGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ImmutableSortedDictionary<string, string?> outputPartition =
                CanonicalPartitionValues(group.PartitionValues, partitionColumns);
            string outputPath = BuildOutputPath(outputPartition, partitionColumns);

            List<ColumnBatch> batches = await ReadGroupBatchesAsync(group, dataSchema, cancellationToken)
                .ConfigureAwait(false);
            CompactedOutput output = await WriteCompactedFileAsync(outputPath, dataSchema, batches, cancellationToken)
                .ConfigureAwait(false);

            // Security defense-in-depth (debug-only, never a release throw): the rows actually read into this
            // group's batches must equal the rows written to (and recorded on) its single output. This catches
            // a read/write mismatch (a dropped batch, a truncated write) directly from measured data, not from
            // the inputs' add.stats (which may be absent). The real teeth are the AC1 content/row-count oracle.
            long groupRowsRead = 0;
            foreach (ColumnBatch batch in batches)
            {
                groupRowsRead += batch.LogicalRowCount;
            }

            Debug.Assert(
                output.RowCount == groupRowsRead && (output.Statistics.NumRecords ?? -1L) == groupRowsRead,
                $"OPTIMIZE row-count cross-check failed: read {groupRowsRead} row(s) but wrote " +
                $"{output.RowCount} (add.stats NumRecords {output.Statistics.NumRecords?.ToString(CultureInfo.InvariantCulture) ?? "∅"}).");

            foreach (AddFileAction input in group.Inputs)
            {
                actions.Add(ToRemove(input, timestamp));
                inputPaths.Add(input.Path);
            }

            actions.Add(new AddFileAction(
                output.Path,
                outputPartition,
                output.ByteSize,
                timestamp,
                DataChange: false,
                output.Statistics,
                NoTags));

            Accumulate(summaries, group, output);
        }

        // AC4 seam: fires only after every compacted file is durably published but before the commit, so a
        // test (or a real crash) at this point leaves the inputs active and the outputs as ignorable orphans.
        if (BeforeCommitProbe is { } probe)
        {
            await probe(cancellationToken).ConfigureAwait(false);
        }

        // AC1 + AC2: ONE commit removing every compacted input and adding every compacted output, both
        // dataChange=false, scoped to exactly the input paths so a concurrent change to an input aborts.
        DeltaCommitResult commit = await _committer.CommitAsync(
            readSnapshot, actions, DeltaReadScope.ReadFiles(inputPaths), cancellationToken).ConfigureAwait(false);

        return BuildCommittedResult(readSnapshot.Version, commit.Version, target, partitionColumns, summaries);
    }

    /// <summary>Sets the bounded <c>deltasharp.outcome</c> tag on the OPTIMIZE span (a no-op when no listener
    /// sampled it), mirroring <see cref="DeltaVacuum"/>.</summary>
    private static void SetOutcomeTag(Activity? activity, OptimizeOutcome outcome) =>
        activity?.SetTag(DeltaSharpTelemetry.OutcomeKey, DeltaStorageTelemetry.ToLabel(outcome));

    // Groups the eligible small active files by canonical partition key and bin-packs each partition's small
    // files into compaction groups of combined size <= target, skipping any single-file group (nothing to
    // gain). An active file is eligible iff it passes the optional partition predicate (AC3) AND is smaller
    // than the target (a file already at/above target is left as-is). Selection is deliberately exact-match
    // on the partition key (never Snapshot.PruneFiles, which over-approximates for reads and would compact —
    // then remove — files in an unselected partition).
    private static IReadOnlyList<CompactionGroup> PlanCompaction(
        Snapshot snapshot,
        ImmutableArray<string> partitionColumns,
        Func<IReadOnlyDictionary<string, string?>, bool>? partitionFilter,
        long target)
    {
        var byPartition = new SortedDictionary<string, List<AddFileAction>>(StringComparer.Ordinal);
        var partitionSample = new Dictionary<string, ImmutableSortedDictionary<string, string?>>(StringComparer.Ordinal);

        foreach (AddFileAction file in snapshot.ActiveFiles)
        {
            if (partitionFilter is not null && !partitionFilter(file.PartitionValues))
            {
                continue; // AC3: an unselected partition's files are never compacted.
            }

            if (file.Size >= target)
            {
                continue; // already large enough — not a small-file candidate.
            }

            string key = PartitionKeyBuilder.Build(file.PartitionValues, partitionColumns);
            if (!byPartition.TryGetValue(key, out List<AddFileAction>? bucket))
            {
                bucket = new List<AddFileAction>();
                byPartition[key] = bucket;
                partitionSample[key] = file.PartitionValues;
            }

            bucket.Add(file);
        }

        var groups = new List<CompactionGroup>();
        foreach ((string key, List<AddFileAction> files) in byPartition)
        {
            BinPackPartition(key, partitionSample[key], files, target, groups);
        }

        return groups;
    }

    // Next-fit bin-packing over a single partition's small files (sorted by ascending size, then path for a
    // deterministic, stable plan): accumulate files into the current bin until the next file would push the
    // bin's combined size past the target, then close the bin and start a new one. A closed bin becomes a
    // compaction group only if it holds more than one file — compacting a lone file into a single file is
    // pure I/O with no benefit (documented rule).
    private static void BinPackPartition(
        string partitionKey,
        ImmutableSortedDictionary<string, string?> partitionValues,
        List<AddFileAction> files,
        long target,
        List<CompactionGroup> groups)
    {
        files.Sort(static (a, b) =>
        {
            int bySize = a.Size.CompareTo(b.Size);
            return bySize != 0 ? bySize : string.CompareOrdinal(a.Path, b.Path);
        });

        var bin = new List<AddFileAction>();
        long binSize = 0;
        foreach (AddFileAction file in files)
        {
            if (bin.Count > 0 && binSize + file.Size > target)
            {
                FlushBin(partitionKey, partitionValues, bin, groups);
                bin = new List<AddFileAction>();
                binSize = 0;
            }

            bin.Add(file);
            binSize += file.Size;
        }

        FlushBin(partitionKey, partitionValues, bin, groups);
    }

    private static void FlushBin(
        string partitionKey,
        ImmutableSortedDictionary<string, string?> partitionValues,
        List<AddFileAction> bin,
        List<CompactionGroup> groups)
    {
        if (bin.Count > 1)
        {
            groups.Add(new CompactionGroup(partitionKey, partitionValues, bin.ToImmutableArray()));
        }
    }

    // Reads every input file of a compaction group into ColumnBatches, in the group's (packing) order, so
    // the rewritten file preserves the row multiset (AC1 row-count / content equivalence; order is the
    // group's packing order, not the table's logical order). The read projects the table's CURRENT DATA
    // schema (partition columns are not stored in the Parquet files; their values ride on the add action),
    // which is exactly what the compacted file is written with.
    //
    // FIX #195/#497 (schema evolution, fails closed): an input written under an older schema (#190) is
    // physically narrower than the current data schema, so ParquetFileReader.ResolveFileFields raises a
    // "column '<x>' is not present in the Parquet file schema" DeltaStorageException that reads like
    // corruption. Read-side null-fill of the absent columns is issue #497 and out of scope here; until it
    // lands, translate that specific defect into a clear, actionable OptimizeSchemaEvolutionException. It is
    // thrown BEFORE any commit (nothing has been committed yet), so OPTIMIZE fails closed: inputs stay
    // active, the version is unchanged, any written outputs are orphans.
    private async Task<List<ColumnBatch>> ReadGroupBatchesAsync(
        CompactionGroup group, StructType dataSchema, CancellationToken cancellationToken)
    {
        var batches = new List<ColumnBatch>();
        foreach (AddFileAction input in group.Inputs)
        {
            Stream stream = await _backend.OpenReadAsync(input.Path, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                try
                {
                    await foreach (ColumnBatch batch in _reader
                        .ReadAsync(stream, dataSchema, keepRowGroup: null, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        batches.Add(batch);
                    }
                }
                catch (DeltaStorageException ex) when (IsNarrowSchemaEvolutionInput(ex))
                {
                    throw new OptimizeSchemaEvolutionException(input.Path, ex);
                }
            }
        }

        return batches;
    }

    // True iff the read failed because the input Parquet file is missing a column the current data schema
    // requests — the additive schema-evolution (#190) narrow-file case that needs read-side null-fill (#497).
    // A genuine byte-level corruption or a real type mismatch does NOT match (it carries a different message
    // / kind), so it is not masked by the OPTIMIZE-specific schema-evolution error.
    private static bool IsNarrowSchemaEvolutionInput(DeltaStorageException ex) =>
        ex.Kind == StorageErrorKind.CorruptData
        && ex.Message.Contains("is not present in the Parquet file schema", StringComparison.Ordinal);

    // Writes the group's batches into one compacted Parquet file with write-time statistics. The bytes are
    // first serialized to a seekable buffer (Parquet writes a trailing footer and the byte size is measured
    // from the buffer), then published through the backend's staged write door — the destination is only
    // visible after CompleteAsync, so a faulted write never leaves a torn object (design §2.13.2).
    private async Task<CompactedOutput> WriteCompactedFileAsync(
        string path, StructType dataSchema, IReadOnlyList<ColumnBatch> batches, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        ParquetFileWriter.WriteResult writeResult = await _writer
            .WriteWithStatisticsAsync(buffer, dataSchema, batches, _statisticsPolicy, cancellationToken)
            .ConfigureAwait(false);

        Stream target = await _backend.OpenWriteAsync(path, cancellationToken).ConfigureAwait(false);
        await using (target.ConfigureAwait(false))
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            await ((ICompletableWriteStream)target).CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return new CompactedOutput(path, writeResult.ByteSize, writeResult.RowCount, writeResult.Statistics);
    }

    // Tombstone a compacted input. dataChange=false marks a byte-rearranging remove (§2.11.2), and
    // ExtendedFileMetadata=true round-trips partitionValues/size so the remove survives checkpoint
    // reconstruction with full fidelity (design §2.10.1).
    private RemoveFileAction ToRemove(AddFileAction input, long timestamp) =>
        new(
            input.Path,
            DeletionTimestamp: timestamp,
            DataChange: false,
            ExtendedFileMetadata: true,
            input.PartitionValues,
            input.Size);

    // The table's DATA schema: the full schema minus the partition columns (Delta does not store partition
    // columns in the Parquet data files — their values live on the add action). For an unpartitioned table
    // this is the full schema.
    private static StructType BuildDataSchema(StructType tableSchema, ImmutableArray<string> partitionColumns)
    {
        if (partitionColumns.IsDefaultOrEmpty)
        {
            return tableSchema;
        }

        var partitionSet = partitionColumns.ToImmutableHashSet(StringComparer.Ordinal);
        var dataFields = new List<StructField>(tableSchema.Count);
        foreach (StructField field in tableSchema)
        {
            if (!partitionSet.Contains(field.Name))
            {
                dataFields.Add(field);
            }
        }

        return new StructType(dataFields);
    }

    // The canonical partition values for a compacted output: a value (possibly null) for EVERY partition
    // column, so the add satisfies the partition-coverage contract. All inputs in a group share the same
    // partition key, so any input is a valid source.
    private static ImmutableSortedDictionary<string, string?> CanonicalPartitionValues(
        ImmutableSortedDictionary<string, string?> source, ImmutableArray<string> partitionColumns)
    {
        if (partitionColumns.IsDefaultOrEmpty)
        {
            return EmptyPartition;
        }

        ImmutableSortedDictionary<string, string?>.Builder builder =
            ImmutableSortedDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        foreach (string column in partitionColumns)
        {
            source.TryGetValue(column, out string? value);
            builder[column] = value;
        }

        return builder.ToImmutable();
    }

    // Composes the compacted output's storage path: a unique "part-<nonce>.parquet" under the Hive-style
    // partition directory ("col=value/"), using Spark's __HIVE_DEFAULT_PARTITION__ sentinel for a null
    // value. Partition membership is authoritative from the add action, not the path; the directory only
    // keeps the object layout tidy and consistent with the write path.
    private string BuildOutputPath(
        ImmutableSortedDictionary<string, string?> partitionValues, ImmutableArray<string> partitionColumns)
    {
        string name = "part-" + _fileNameFactory() + ".parquet";
        if (partitionColumns.IsDefaultOrEmpty)
        {
            return name;
        }

        var sb = new StringBuilder();
        foreach (string column in partitionColumns)
        {
            partitionValues.TryGetValue(column, out string? value);
            sb.Append(column).Append('=').Append(value ?? HiveDefaultPartition).Append('/');
        }

        sb.Append(name);
        return sb.ToString();
    }

    private static void Accumulate(
        Dictionary<string, PartitionAccumulator> summaries, CompactionGroup group, CompactedOutput output)
    {
        if (!summaries.TryGetValue(group.PartitionKey, out PartitionAccumulator? accumulator))
        {
            accumulator = new PartitionAccumulator(group.PartitionValues);
            summaries[group.PartitionKey] = accumulator;
        }

        accumulator.FilesRemoved += group.Inputs.Length;
        accumulator.FilesAdded += 1;
        accumulator.BytesBefore += group.InputBytes;
        accumulator.BytesAfter += output.ByteSize;
        accumulator.RowCount += output.RowCount;                 // measured (ground truth).
        accumulator.EstimatedRowCount += group.InputRecordCount; // advisory (input add.stats, may be 0).
    }

    private OptimizeResult BuildCommittedResult(
        long readVersion,
        long committedVersion,
        long target,
        ImmutableArray<string> partitionColumns,
        Dictionary<string, PartitionAccumulator> summaries)
    {
        int filesRemoved = 0;
        int filesAdded = 0;
        long bytesBefore = 0;
        long bytesAfter = 0;
        long rowCount = 0;
        long estimatedRowCount = 0;
        var partitions = ImmutableArray.CreateBuilder<OptimizePartitionSummary>(summaries.Count);
        foreach (PartitionAccumulator accumulator in summaries.Values.OrderBy(a => a.SortKey, StringComparer.Ordinal))
        {
            filesRemoved += accumulator.FilesRemoved;
            filesAdded += accumulator.FilesAdded;
            bytesBefore += accumulator.BytesBefore;
            bytesAfter += accumulator.BytesAfter;
            rowCount += accumulator.RowCount;
            estimatedRowCount += accumulator.EstimatedRowCount;
            partitions.Add(new OptimizePartitionSummary(
                CanonicalPartitionValues(accumulator.PartitionValues, partitionColumns),
                accumulator.FilesRemoved,
                accumulator.FilesAdded,
                accumulator.BytesBefore,
                accumulator.BytesAfter,
                accumulator.RowCount,
                accumulator.EstimatedRowCount));
        }

        return new OptimizeResult(
            readVersion,
            committedVersion,
            DryRun: false,
            target,
            filesRemoved,
            filesAdded,
            bytesBefore,
            bytesAfter,
            rowCount,
            estimatedRowCount,
            partitions.ToImmutable());
    }

    // Reports the plan for a dry run (or a no-op when no partition has more than one small file). Nothing is
    // written or committed, so BytesAfter is 0 and the MEASURED RowCount is 0 (nothing was read/written);
    // the advisory EstimatedRowCount is the best-effort sum of the inputs' recorded record counts (an input
    // without add.stats contributes 0). See OptimizeResult's remarks on the two row-count fidelities.
    private OptimizeResult BuildDryRunOrEmptyResult(
        long readVersion,
        bool dryRun,
        long target,
        ImmutableArray<string> partitionColumns,
        IReadOnlyList<CompactionGroup> groups)
    {
        var summaries = new Dictionary<string, PartitionAccumulator>(StringComparer.Ordinal);
        foreach (CompactionGroup group in groups)
        {
            if (!summaries.TryGetValue(group.PartitionKey, out PartitionAccumulator? accumulator))
            {
                accumulator = new PartitionAccumulator(group.PartitionValues);
                summaries[group.PartitionKey] = accumulator;
            }

            accumulator.FilesRemoved += group.Inputs.Length;
            accumulator.FilesAdded += 1; // one predicted output file per group.
            accumulator.BytesBefore += group.InputBytes;
            accumulator.EstimatedRowCount += group.InputRecordCount; // advisory only; measured RowCount stays 0.
        }

        int filesRemoved = 0;
        int filesAdded = 0;
        long bytesBefore = 0;
        long estimatedRowCount = 0;
        var partitions = ImmutableArray.CreateBuilder<OptimizePartitionSummary>(summaries.Count);
        foreach (PartitionAccumulator accumulator in summaries.Values.OrderBy(a => a.SortKey, StringComparer.Ordinal))
        {
            filesRemoved += accumulator.FilesRemoved;
            filesAdded += accumulator.FilesAdded;
            bytesBefore += accumulator.BytesBefore;
            estimatedRowCount += accumulator.EstimatedRowCount;
            partitions.Add(new OptimizePartitionSummary(
                CanonicalPartitionValues(accumulator.PartitionValues, partitionColumns),
                accumulator.FilesRemoved,
                accumulator.FilesAdded,
                accumulator.BytesBefore,
                BytesAfter: 0,
                RowCount: 0,
                accumulator.EstimatedRowCount));
        }

        return new OptimizeResult(
            readVersion,
            CommittedVersion: null,
            dryRun,
            target,
            filesRemoved,
            filesAdded,
            bytesBefore,
            BytesAfter: 0,
            RowCount: 0,
            estimatedRowCount,
            partitions.ToImmutable());
    }

    // One compaction group: the small input files (all sharing PartitionKey) that will be rewritten into a
    // single compacted output for PartitionValues.
    private sealed record CompactionGroup(
        string PartitionKey,
        ImmutableSortedDictionary<string, string?> PartitionValues,
        ImmutableArray<AddFileAction> Inputs)
    {
        public long InputBytes
        {
            get
            {
                long total = 0;
                foreach (AddFileAction input in Inputs)
                {
                    total += input.Size;
                }

                return total;
            }
        }

        // Best-effort input row count from add.stats (for dry-run reporting only). An input without
        // recorded statistics contributes 0 — dry-run row counts are advisory, the real run measures rows.
        public long InputRecordCount
        {
            get
            {
                long total = 0;
                foreach (AddFileAction input in Inputs)
                {
                    total += input.Stats?.NumRecords ?? 0;
                }

                return total;
            }
        }
    }

    // A written compacted file: its storage path, byte size (measured from the seekable write buffer), row
    // count, and write-time statistics recorded on the add action.
    private readonly record struct CompactedOutput(string Path, long ByteSize, long RowCount, FileStatistics Statistics);

    // A mutable per-partition tally folded into an OptimizePartitionSummary.
    private sealed class PartitionAccumulator
    {
        public PartitionAccumulator(ImmutableSortedDictionary<string, string?> partitionValues)
        {
            PartitionValues = partitionValues;
            SortKey = string.Join('\u0001', partitionValues.Select(kv => kv.Key + "=" + (kv.Value ?? "\u0000")));
        }

        public ImmutableSortedDictionary<string, string?> PartitionValues { get; }

        public string SortKey { get; }

        public int FilesRemoved { get; set; }

        public int FilesAdded { get; set; }

        public long BytesBefore { get; set; }

        public long BytesAfter { get; set; }

        public long RowCount { get; set; }

        public long EstimatedRowCount { get; set; }
    }
}
