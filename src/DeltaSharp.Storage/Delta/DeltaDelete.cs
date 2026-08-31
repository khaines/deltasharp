using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DeltaSharp.Diagnostics;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Diagnostics;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// A row-level predicate for a merge-on-read <see cref="DeltaDelete"/>: given a full-schema, LOGICAL
/// <see cref="ColumnBatch"/> read from one data file and a row index within it, returns <see langword="true"/>
/// when that row must be deleted. The predicate is evaluated over the file's PHYSICAL rows (a previously
/// deletion-vectored row is still presented so the union with the existing DV stays idempotent), so its
/// verdict maps directly to a file-relative physical position recorded in the new deletion vector.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three-valued-logic (3VL) contract — MANDATORY.</b> A predicate MUST <see cref="ColumnVector.IsNull"/>-guard
/// EVERY column reference it reads. A null — including a <b>schema-evolution null-fill</b>, where a DELETE
/// re-scans a still-active NARROW data file under a wider table schema and null-fills a later-added nullable
/// column (#645) — is three-valued-logic <b>NOT-TRUE</b>: an <c>IS NULL</c> test matches it, but every value
/// comparison over it (<c>== v</c>, <c>&gt; 0</c>, …) does NOT match. A null must NEVER be read as
/// <c>default(T)</c> and compared as if it were a real value.
/// </para>
/// <para>
/// <b>Footgun.</b> <see cref="ColumnVector.GetValue{T}"/> and <see cref="ColumnVector.GetBytes"/> return
/// <c>default(T)</c> / an empty span on a null slot — they do <b>NOT</b> throw. So a value comparison authored
/// WITHOUT an <see cref="ColumnVector.IsNull"/> guard (e.g. <c>col.GetValue&lt;long&gt;(row) == 0</c>) silently
/// matches every null-filled row and deletes the WRONG rows — a data-loss footgun surfaced by #645/#656 and
/// tracked by #657. Author each column reference as a guarded value test
/// (<c>!col.IsNull(row) &amp;&amp; col.GetValue&lt;T&gt;(row) == v</c>) or an <c>IS NULL</c> test
/// (<c>col.IsNull(row)</c>).
/// </para>
/// <para>
/// <b>Result invariant (SQL 3VL).</b> Equivalently: <see cref="Matches"/> MUST return <see langword="true"/>
/// <b>iff</b> the SQL predicate evaluates to <b>TRUE</b> for the row — never for UNKNOWN or FALSE. Guarded
/// leaves compose safely under <c>AND</c>/<c>OR</c> (the delete-if-TRUE collapse {FALSE, UNKNOWN}→don't-delete
/// is a Kleene homomorphism), but <b>NOT under negation</b>: <c>NOT (col = v)</c> must be lowered to a POSITIVE
/// guarded leaf (<c>!col.IsNull(row) &amp;&amp; col.GetValue&lt;T&gt;(row) != v</c>), NEVER as the C# negation
/// <c>!(!col.IsNull(row) &amp;&amp; col.GetValue&lt;T&gt;(row) == v)</c> — the latter deletes null rows
/// (<c>NOT UNKNOWN = UNKNOWN</c>, not TRUE) AND slips past the DEBUG guard (the <c>&amp;&amp;</c> short-circuits
/// on <see cref="ColumnVector.IsNull"/> before any poisoned read). Push negation / <c>&lt;&gt;</c> to the leaf.
/// </para>
/// <para>
/// <b>Guard scope (residuals).</b> The DEBUG poison catches only the per-row <see cref="ColumnVector.GetValue{T}"/>
/// / <see cref="ColumnVector.GetBytes"/> footgun. It does NOT cover (a) the negation shape above, nor (b) a
/// <b>vectorized</b> predicate that reads the bulk span <see cref="ColumnVector.GetValues{T}"/> and masks with
/// the validity bitmap — the natural performant lowering — which must itself fold
/// <see cref="ColumnVector.TryGetValidity"/> / <see cref="ColumnVector.IsNull"/> into its selection (an
/// un-masked bulk read of a null slot yields <c>default(T)</c> and silently mis-deletes, uncaught even in
/// DEBUG), nor (c) data-skipping / predicate-pushdown evaluation over file statistics (its own 3VL surface).
/// Structural, release-safe enforcement of all three — a null-guarded predicate IR with negation normalized to
/// leaves, plus vectorized validity-folding and interpreted/vectorized parity tests — is tracked in #673, to
/// land before the first SQL/DataFrame DELETE lowering.
/// </para>
/// <para>
/// A DEBUG-only null-poison wrapper (<c>NullPoisonColumnBatch</c>, wired at the <see cref="DeltaDelete"/>
/// predicate call site) enforces this in tests: a <see cref="ColumnVector.GetValue{T}"/> /
/// <see cref="ColumnVector.GetBytes"/> read of a null slot THROWS instead of returning <c>default</c>, so an
/// un-guarded value predicate FAILS LOUD rather than silently mis-deleting. Release builds pass the batch
/// through unchanged (zero overhead).
/// </para>
/// </remarks>
internal abstract class DeltaDeletePredicate
{
    /// <summary>Returns <see langword="true"/> when the row at <paramref name="rowIndex"/> in the
    /// full-schema logical <paramref name="batch"/> should be deleted. The implementation MUST honor the
    /// class-level three-valued-logic (3VL) contract: <see cref="ColumnVector.IsNull"/>-guard every column
    /// reference so a null (incl. a schema-evolution null-fill, #645) is treated as NOT-TRUE, never as
    /// <c>default(T)</c>.</summary>
    public abstract bool Matches(ColumnBatch batch, int rowIndex);

    /// <summary>Builds a predicate from a delegate over a full-schema logical batch and row index. The delegate
    /// MUST honor the class-level three-valued-logic (3VL) contract: <see cref="ColumnVector.IsNull"/>-guard
    /// every column reference (a null — incl. a schema-evolution null-fill, #645 — is NOT-TRUE, never
    /// <c>default(T)</c>). An un-guarded value read (e.g. <c>col.GetValue&lt;long&gt;(row) == 0</c>) silently
    /// matches null-filled rows and deletes the wrong rows; a DEBUG-only null-poison guard makes that FAIL LOUD
    /// in tests (#657).</summary>
    public static DeltaDeletePredicate FromRowPredicate(Func<ColumnBatch, int, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new DelegateDeletePredicate(predicate);
    }

    private sealed class DelegateDeletePredicate(Func<ColumnBatch, int, bool> predicate) : DeltaDeletePredicate
    {
        public override bool Matches(ColumnBatch batch, int rowIndex) => predicate(batch, rowIndex);
    }
}

/// <summary>The outcome of a merge-on-read DELETE: the read snapshot version, the committed version (null on
/// a no-op), the number of files given a new/updated deletion vector, the number of files whose every row
/// was deleted (removed outright, no residual add), and the total rows logically deleted.</summary>
internal sealed record DeleteResult(
    long ReadVersion,
    long? CommittedVersion,
    int FilesWithDeletionVector,
    int FilesFullyDeleted,
    long RowsDeleted);

/// <summary>
/// Delta <b>merge-on-read DELETE</b> (STORY-05.5.1 / #192). It logically deletes the rows a
/// <see cref="DeltaDeletePredicate"/> matches by writing a <b>deletion vector</b> per affected data file —
/// the data file is <b>never rewritten</b>. Each affected file's prior <c>add</c> is superseded in ONE
/// commit by a <c>remove</c> (carrying the file's PRIOR deletion vector, so it tombstones the exact prior
/// logical file) plus a fresh <c>add</c> on the SAME path carrying the NEW deletion vector and a
/// <c>stats.numRecords</c> that stays the <b>physical</b> data-file row count (matching Spark — the total
/// rows in the Parquet file, NOT the residual; the residual logical count is <c>numRecords − cardinality</c>).
/// A file whose every row is deleted is <c>remove</c>d outright (no residual add, no wasted DV).
///
/// <para><b>Protocol gate (AC3).</b> The DELETE fails closed via
/// <see cref="DeletionVectorsFeature.EnsureWriteEnabled"/> unless the table protocol declares the
/// <c>deletionVectors</c> feature (reader v3 / writer v7) AND the <c>delta.enableDeletionVectors</c>
/// property is <c>true</c>. It never silently upgrades an unprepared table's protocol or drops the delete.</para>
///
/// <para><b>Conflict scope (AC2).</b> The commit is scoped with <see cref="DeltaReadScope.ReadFiles"/> over
/// exactly the files it rewrote the DV of, so a concurrent commit that removed/re-added one of those files
/// aborts this DELETE (no lost delete). <see cref="DeltaConflictChecker"/> additionally enforces a
/// scope-independent deletion-vector exclusivity rule for defense in depth.</para>
///
/// <para><b>Scope.</b> Column mapping is resolved on the WRITE path for <c>name</c> mode (#529): the
/// physically-named data is read and relabeled to the LOGICAL schema so the predicate sees logical column
/// names/values, while the emitted deletion vector stays POSITIONAL over the PHYSICAL data file (column
/// mapping never changes a row's physical position). <c>id</c> mode stays fail-closed (#523). Nested
/// (struct/array/map) mapped columns are supported for read/DELETE via the shared column-mapping projection
/// (#676); a CDF-active DELETE that would rewrite a nested-column data file is gated separately by
/// <c>ChangeDataWriter.EnsureWritableDataSchema</c>. On-disk
/// (<c>'u'</c>) deletion vectors are written; the bin-packing/inlining policy for tiny DVs is a follow-up.
/// Predicate/partition pushdown to prune scanned files is a follow-up — every active file is scanned.</para>
/// </summary>
internal sealed class DeltaDelete
{
    private static readonly ImmutableSortedDictionary<string, long> EmptyNullCount =
        ImmutableSortedDictionary<string, long>.Empty.WithComparers(StringComparer.Ordinal);

    // A cdc file (like an add) carries a tags map; a DELETE-generated cdc file has no tags to record.
    private static readonly ImmutableSortedDictionary<string, string> EmptyTags =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private readonly IStorageBackend _backend;
    private readonly DeltaLog _log;
    private readonly DeltaCommitter _committer;
    private readonly TimeProvider _timeProvider;
    private readonly ParquetFileReader _reader;
    private readonly IDeletionVectorIdSource _idSource;
    private readonly ChangeDataWriter _changeDataWriter;
    private readonly Func<string> _cdcFileNameFactory;
    private readonly ILogger<DeltaDelete> _logger;
    private readonly DeltaStorageTelemetry _telemetry;

    private static readonly KeyValuePair<string, object?>[] DeleteLogScope =
    {
        new(DeltaSharpTelemetry.ComponentKey, DeltaStorageTelemetry.DeltaComponent),
        new(DeltaSharpTelemetry.OperationKey, DeltaStorageTelemetry.DeleteOperation),
    };

    /// <summary>Test seam (null/inert in production): awaited once after every deletion-vector file has been
    /// written to storage and <b>before</b> the single Delta commit, so a test can inject a concurrent
    /// commit and assert the read-scope conflict/abort behavior deterministically (AC2).</summary>
    internal volatile Func<CancellationToken, Task>? BeforeCommitProbe;

    /// <summary>Creates a DELETE over <paramref name="backend"/> (rooted at the Delta table directory),
    /// constructing its own log reader + committer and using the system clock and a cryptographic id source.</summary>
    public DeltaDelete(IStorageBackend backend)
        : this(backend, new DeltaLog(backend))
    {
    }

    /// <summary>Creates a DELETE over an explicit reader + optional committer (tests inject a committer with a
    /// race probe, a deterministic clock for tombstone/modification timestamps, and a deterministic DV id
    /// source so on-disk DV file names are predictable), plus optional injected logger/telemetry. When
    /// <paramref name="committer"/> is null the committer is built from <paramref name="timeProvider"/> so the
    /// injected clock also drives <c>commitInfo.timestamp</c> (#510). When Change Data Feed is enabled on the
    /// read snapshot (§2.5), the DELETE materializes its deleted rows as <c>cdc</c> files via
    /// <paramref name="changeDataWriter"/>, whose file names come from <paramref name="cdcFileNameFactory"/>
    /// (a deterministic seam a golden fixture injects, mirroring the data-file naming seam — never the banned
    /// <c>Guid.NewGuid</c>).</summary>
    internal DeltaDelete(
        IStorageBackend backend,
        DeltaLog log,
        DeltaCommitter? committer = null,
        TimeProvider? timeProvider = null,
        ParquetFileReader? reader = null,
        IDeletionVectorIdSource? idSource = null,
        ILogger<DeltaDelete>? logger = null,
        DeltaStorageTelemetry? telemetry = null,
        ChangeDataWriter? changeDataWriter = null,
        Func<string>? cdcFileNameFactory = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(log);
        _backend = backend;
        _log = log;
        // Assign the clock BEFORE building a default committer so both share the injected TimeProvider.
        _timeProvider = timeProvider ?? TimeProvider.System;
        _committer = committer ?? new DeltaCommitter(backend, _timeProvider);
        _reader = reader ?? new ParquetFileReader();
        _idSource = idSource ?? new RandomDeletionVectorIdSource();
        _changeDataWriter = changeDataWriter ?? new ChangeDataWriter(backend);
        _cdcFileNameFactory = cdcFileNameFactory ?? ChangeDataWriter.DefaultFileNameFactory;
        _logger = logger ?? NullLogger<DeltaDelete>.Instance;
        _telemetry = telemetry ?? DeltaStorageTelemetry.Shared;
    }

    /// <summary>Runs DELETE against the latest committed snapshot.</summary>
    public async Task<DeleteResult> DeleteAsync(
        DeltaDeletePredicate predicate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Snapshot snapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
        return await DeleteAsync(snapshot, predicate, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs DELETE against an explicit <paramref name="readSnapshot"/> (the test seam that lets a
    /// caller commit a concurrent writer before DELETE commits, so the read-scope conflict/abort behavior is
    /// exercised deterministically — AC2).</summary>
    /// <exception cref="DeltaProtocolException">The table does not support/enable deletion-vector writes (AC3).</exception>
    /// <exception cref="DeltaConcurrentModificationException">A concurrent commit changed a file this DELETE
    /// removed rows from since <paramref name="readSnapshot"/> (AC2).</exception>
    internal async Task<DeleteResult> DeleteAsync(
        Snapshot readSnapshot, DeltaDeletePredicate predicate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(predicate);

        using IDisposable? logScope = _logger.BeginScope(DeleteLogScope);
        DeltaDeleteLog.DeleteStarted(_logger, _backend.Kind.ToLabel());

        long startTimestamp = Stopwatch.GetTimestamp();
        using Activity? activity = _telemetry.StartDeleteActivity(_backend.Kind);
        try
        {
            DeleteResult result = await RunDeleteAsync(readSnapshot, predicate, cancellationToken)
                .ConfigureAwait(false);

            double seconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
            DeleteOutcome outcome = result.CommittedVersion is null ? DeleteOutcome.NoOp : DeleteOutcome.Completed;
            _telemetry.RecordDeleteTerminal(outcome, seconds, result.FilesWithDeletionVector, result.RowsDeleted);
            SetOutcomeTag(activity, outcome);
            if (outcome == DeleteOutcome.NoOp)
            {
                DeltaDeleteLog.DeleteNoOp(_logger, result.ReadVersion, seconds * 1000);
            }
            else
            {
                DeltaDeleteLog.DeleteCompleted(
                    _logger,
                    result.ReadVersion,
                    result.CommittedVersion ?? result.ReadVersion,
                    result.RowsDeleted,
                    result.FilesWithDeletionVector,
                    seconds * 1000);
            }

            return result;
        }
        catch (DeltaConcurrentModificationException ex)
        {
            // AC2 fail-closed abort: a concurrent commit changed a file this DELETE removed rows from. No
            // delete was lost — the table is unchanged and any written DV file is an orphan. A domain
            // outcome (Warning), not a failure.
            _telemetry.RecordDeleteTerminal(
                DeleteOutcome.Aborted, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, 0, 0);
            SetOutcomeTag(activity, DeleteOutcome.Aborted);
            DeltaDeleteLog.DeleteAborted(_logger, ex.GetType().Name);
            throw;
        }
        catch (OperationCanceledException)
        {
            _telemetry.RecordDeleteTerminal(
                DeleteOutcome.Cancelled, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, 0, 0);
            SetOutcomeTag(activity, DeleteOutcome.Cancelled);
            DeltaDeleteLog.DeleteCanceled(_logger);
            throw;
        }
        catch (Exception ex)
        {
            _telemetry.RecordDeleteTerminal(
                DeleteOutcome.Failure, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, 0, 0);
            SetOutcomeTag(activity, DeleteOutcome.Failure);
            activity?.SetStatus(ActivityStatusCode.Error);
            DeltaDeleteLog.DeleteFailed(_logger, ex.GetType().Name);
            throw;
        }
    }

    private async Task<DeleteResult> RunDeleteAsync(
        Snapshot readSnapshot, DeltaDeletePredicate predicate, CancellationToken cancellationToken)
    {
        // AC3 protocol gate: fail closed unless the table declares AND enables deletion vectors.
        DeletionVectorsFeature.EnsureWriteEnabled(readSnapshot);

        // Column-mapping resolution for the WRITE path (#529/#572). All three modes (none/name/id) are
        // resolved through the shared ColumnMappingProjection seam EXACTLY as the READ path does, so DELETE
        // and DeltaReadSource resolve identically. In `id` mode DATA columns resolve by the Parquet field_id
        // (resolveByFieldId below, #523) rather than by physical name — the file's field_ids are matched
        // against each dataSchema field's delta.columnMapping.id (preserved by BuildDataSchema). In BOTH
        // mapped modes the emitted deletion vector stays POSITIONAL over the PHYSICAL data file (column
        // mapping never changes a row's physical position — the DV row-index semantics are unaffected), and
        // partition values are const/null-filled by PHYSICAL name. `none` mode is unchanged (physical name ==
        // logical name). Nested (struct/array/map) mapped columns are supported for read/DELETE via the shared
        // column-mapping projection (#676) — no longer rejected here; a CDF-active DELETE that would rewrite a
        // nested-column data file is gated by ChangeDataWriter.EnsureWritableDataSchema instead.
        ColumnMappingMode mappingMode = ColumnMapping.ResolveMode(readSnapshot.Metadata.Configuration);
        bool resolveByFieldId = mappingMode == ColumnMappingMode.Id;

        StructType tableSchema = readSnapshot.Schema;
        ImmutableArray<string> partitionColumns = readSnapshot.Metadata.PartitionColumns;
        string[] physicalNames = ColumnMappingProjection.ResolvePhysicalNames(tableSchema, mappingMode);
        StructType dataSchema = ColumnMappingProjection.BuildDataSchema(tableSchema, physicalNames, partitionColumns);
        int[] dataOrdinalByField = ColumnMappingProjection.MapDataOrdinals(physicalNames, dataSchema);
        long timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        // Read-side type-widening promotion gate (#495): a narrow-physical file is promoted into the current
        // (widened) schema only when the snapshot's protocol declares the `typeWidening` feature. We know the
        // protocol here, so we pass it to the reader; without the feature a narrow file fails closed rather
        // than being silently promoted.
        bool allowTypeWideningPromotion = TypeWideningFeature.Supports(readSnapshot.Protocol);

        // Change Data Feed generation gate (§2.5/§2.7). CDF is ACTIVE for writes only when the read snapshot's
        // protocol negotiates the `changeDataFeed` writer feature AND its metadata sets
        // delta.enableChangeDataFeed=true — BOTH are required (the property is honored only when backed by the
        // feature), so a MALFORMED property-without-feature table generates NO cdc (fail-closed via
        // ChangeDataFeedFeature.IsActive; single-sourced with the enable check so the two gates cannot drift).
        // ALL new behavior is gated on this, so a CDF-inactive DELETE is byte-identical to before (INV C1 — no
        // cdc rows captured, no cdc files, no cdc actions). When active, EVERY affected file's newly-deleted
        // rows are materialized as a cdc file in the SAME commit (completeness, INV C2/C3). Fail closed EARLY
        // (before any DV/cdc side effect) on a schema cdc generation cannot support: (a) a reserved CDF
        // metadata column name (`_change_type` etc.) the enable guard could not see because it was added by
        // schema evolution AFTER CDF was enabled — a `_change_type` data column would collide with the
        // synthesized cdc column and yield an ambiguous footer (#642); or (b) a nested data column the
        // selection-gather + scalar Parquet writer cannot materialize — so we never publish an incomplete cdc
        // set that read-time precedence would make silently lossy.
        bool changeDataFeedActive =
            ChangeDataFeedFeature.IsActive(readSnapshot.Protocol, readSnapshot.Metadata.Configuration);
        if (changeDataFeedActive)
        {
            // The reserved-name check runs over the LOGICAL schema (covers all mapping modes: in none mode
            // the collision is literal; in name/id mode the logical name is still reserved).
            ChangeDataWriter.EnsureNoReservedColumnNames(tableSchema);
            ChangeDataWriter.EnsureWritableDataSchema(dataSchema);
        }

        var actions = new List<DeltaAction>();
        var inputPaths = new List<string>();
        int filesWithDeletionVector = 0;
        int filesFullyDeleted = 0;
        int numAddedChangeFiles = 0;
        long rowsDeleted = 0;

        foreach (AddFileAction add in readSnapshot.ActiveFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileDeletionPlan plan = await PlanFileDeletionAsync(
                add, tableSchema, physicalNames, dataSchema, dataOrdinalByField, resolveByFieldId, predicate, allowTypeWideningPromotion, changeDataFeedActive, cancellationToken)
                .ConfigureAwait(false);

            if (plan.NewlyDeletedCount == 0)
            {
                // No row in this file newly matched the predicate — leave its add untouched. An idempotent
                // re-delete (every match already masked by the prior DV) lands here and emits NO cdc file.
                continue;
            }

            inputPaths.Add(add.Path);
            rowsDeleted += plan.NewlyDeletedCount;

            // The remove carries the file's PRIOR deletion vector so it tombstones the EXACT prior logical
            // file (SnapshotState keys active/tombstone by path + DV uniqueId). dataChange=true: a delete
            // changes the visible data.
            actions.Add(ToRemove(add, timestamp));

            // CDF materialization (§2.5), BEFORE the partial-vs-full branch split so BOTH branches publish
            // their newly-deleted rows as cdc (INV C2/C3 completeness — a mixed commit that materialized only
            // the partial branch would silently lose the fully-deleted file's rows, because a cdc-bearing
            // version suppresses ALL implicit derivation, §2.2). The cdc action rides the SAME `actions` list,
            // so it is published atomically in this DELETE commit — never a separate commit.
            if (changeDataFeedActive)
            {
                ChangeDataWriter.ChangeDataFile cdc = await _changeDataWriter
                    .WriteAsync(
                        dataSchema, plan.NewlyDeletedRows, ChangeDataWriter.DeleteChange,
                        _cdcFileNameFactory(), cancellationToken)
                    .ConfigureAwait(false);

                // Hardening (defense in depth): the rows the cdc writer actually wrote MUST equal the count
                // of newly-deleted positions the plan reported — the captured-row selection views and the
                // position set are derived from the SAME scan pass, so any divergence is a capture/planning
                // bug that would emit an incomplete cdc set (INV C2/C3). Debug-only; the write is otherwise
                // correct even without it.
                Debug.Assert(
                    cdc.RowCount == plan.NewlyDeletedCount,
                    // #667 message hygiene: the file path is not interpolated (it can carry
                    // attacker-controllable text from a poisoned log); the bounded row counts suffice.
                    $"cdc row count {cdc.RowCount} != newly-deleted count {plan.NewlyDeletedCount}.");

                actions.Add(new AddCdcFileAction(cdc.Path, add.PartitionValues, cdc.Size, EmptyTags));
                numAddedChangeFiles++;
            }

            long cardinality = plan.AllDeletedPositions.Length;
            if (cardinality >= plan.PhysicalRecords)
            {
                // Every physical row is deleted: remove the file outright (no residual add, no wasted DV).
                filesFullyDeleted++;
                continue;
            }

            DeletionVectorDescriptor descriptor = await WriteDeletionVectorAsync(
                plan.AllDeletedPositions, cardinality, cancellationToken).ConfigureAwait(false);
            filesWithDeletionVector++;

            // FIX (numRecords semantics): a DV-carrying add's stats.numRecords is the PHYSICAL data-file row
            // count (the total rows in the Parquet file), matching Spark — NOT the residual (post-deletion)
            // count. The residual logical count is derivable as numRecords − cardinality. TightBounds stays
            // false (a delete only removes rows, so the prior min/max remain valid but loose).
            actions.Add(new AddFileAction(
                add.Path,
                add.PartitionValues,
                add.Size,
                timestamp,
                DataChange: true,
                BuildPhysicalStatistics(add.Stats, plan.PhysicalRecords),
                add.Tags,
                descriptor));
        }

        if (actions.Count == 0)
        {
            return new DeleteResult(readSnapshot.Version, CommittedVersion: null, 0, 0, 0);
        }

        // AC2 seam: fires after every DV file is durably written but before the commit, so a test (or a real
        // crash) at this point leaves the table unchanged and the DV files as ignorable orphans.
        if (BeforeCommitProbe is { } probe)
        {
            await probe(cancellationToken).ConfigureAwait(false);
        }

        // ONE commit removing every affected file's prior add and adding its residual (DV-carrying) add,
        // scoped to exactly the affected paths so a concurrent change to any of them aborts (no lost delete).
        // Prepend the DELETE provenance (operation="DELETE" + operationMetrics) so DESCRIBE HISTORY records
        // the deleted-row and cdc-file counts (commitInfo is informational; the committer stamps
        // timestamp/engineInfo/txnId). numAddedChangeFiles is 0 when CDF is disabled.
        actions.Insert(0, DeltaCommitInfo.Delete(rowsDeleted, numAddedChangeFiles));
        DeltaCommitResult commit = await _committer
            .CommitAsync(readSnapshot, actions, DeltaReadScope.ReadFiles(inputPaths), cancellationToken)
            .ConfigureAwait(false);

        return new DeleteResult(
            readSnapshot.Version, commit.Version, filesWithDeletionVector, filesFullyDeleted, rowsDeleted);
    }

    // Reads one file's PHYSICAL rows (never applying its existing DV — every physical row is presented so the
    // union stays idempotent), evaluates the predicate to collect the newly-deleted file-relative positions,
    // and unions them with the file's existing DV to form the complete new DV position set.
    private async Task<FileDeletionPlan> PlanFileDeletionAsync(
        AddFileAction add,
        StructType tableSchema,
        string[] physicalNames,
        StructType dataSchema,
        int[] dataOrdinalByField,
        bool resolveByFieldId,
        DeltaDeletePredicate predicate,
        bool allowTypeWideningPromotion,
        bool captureChangeData,
        CancellationToken cancellationToken)
    {
        // Seed with the file's existing DV positions (a prior delete on the same file), so a second delete
        // superseding it never resurrects the earlier deletes.
        var deleted = new SortedSet<long>();
        if (add.DeletionVector is { } existing)
        {
            long? declared = add.Stats?.NumRecords;
            if (declared is not { } physicalRecords)
            {
                throw DeltaStorageException.CorruptData(
                    // #667 message hygiene (the DELETE twin of the #663 read-path DeltaReadSource fix): the
                    // file path is attacker-controllable (a poisoned log can inject arbitrary text) and is not
                    // interpolated — the diagnosis is the missing stats.numRecords, not the path.
                    "An active data file carries a deletion vector but its add action has no stats.numRecords; "
                    + "the DELETE cannot compute the file's physical record count, so it fails closed.");
            }

            // A DV-carrying add's stats.numRecords IS the physical row count (matching Spark), so the DV's
            // positions are validated directly against it — never numRecords + cardinality.
            long[] existingPositions = await DeletionVectorStore
                .LoadAsync(_backend, existing, physicalRecords, cancellationToken).ConfigureAwait(false);
            foreach (long position in existingPositions)
            {
                deleted.Add(position);
            }
        }

        long newlyDeleted = 0;
        long fileRowOffset = 0;
        // CDF capture (§2.5): when enabled, collect the physical data rows of exactly the NEWLY-deleted
        // positions as selection views over the batches we're ALREADY reading here — the SAME scan pass, so
        // no second full-file read (§4.3 memory budget). The reader hands back a fresh batch per row group
        // (no buffer recycling — the pinned ParquetFileReader.ReadAsync contract), so retaining a selection
        // view across row groups is safe. Stays null (never allocated) when CDF is disabled, so the INV C1
        // path is byte-for-byte the prior behavior.
        //
        // TRADEOFF (§4.3): avoiding a second scan means every row group that contributes a newly-deleted row
        // stays RESIDENT (its full batch is retained via the selection view, not just the matched rows)
        // until the post-scan cdc write — a peak-memory increase over the non-CDF path, which streams each
        // batch and lets it be collected as soon as its positions are unioned. Bounded by the file's size
        // (one file scanned at a time) and paid only when CDF is enabled AND the file has a newly-deleted
        // row. The alternative — a second full-file scan to re-read the matched rows — trades this bounded
        // memory for a second pass over object storage (higher latency + I/O), which §4.3 rejects.
        List<ColumnBatch>? changeRows = captureChangeData ? new List<ColumnBatch>() : null;
        Stream stream = await PartitionPathResolver.OpenReadAsync(_backend, add.Path, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await foreach (ColumnBatch dataBatch in _reader
                // #645: null-fill absent columns (like the read/CDF/optimize paths — DeltaReadSource /
                // ChangeFeedReader / DeltaOptimize all pass nullFillMissingColumns:true, #497/#530). dataSchema
                // is the CURRENT (reconciled) table schema, so a still-active file written under an OLDER,
                // NARROWER schema (e.g. left resident by a prior partial DV-delete, then ADD COLUMN) is missing
                // a later-added NULLABLE column; null-filling it lets the DELETE predicate re-scan that file
                // faithfully (absent column reads as null) instead of failing closed on ColumnNotPresentInFile.
                .ReadAsync(stream, dataSchema, keepRowGroup: null, nullFillMissingColumns: true, allowTypeWideningPromotion, resolveByFieldId, cancellationToken)
                .ConfigureAwait(false))
            {
                ColumnBatch fullBatch = ColumnMappingProjection.BuildFullBatch(
                    add, tableSchema, physicalNames, dataOrdinalByField, dataBatch);
#if DEBUG
                // #657: in DEBUG, evaluate the predicate against a NULL-POISON view of fullBatch so a DELETE
                // delegate that reads a value WITHOUT an IsNull guard (the 3VL footgun #645/#656 exposed on a
                // schema-evolution null-fill) FAILS LOUD instead of silently matching a null slot's default(T).
                // ONLY the predicate call is wrapped — the DV-position bookkeeping below stays keyed to the
                // ORIGINAL fullBatch/row. Release builds set predicateBatch = fullBatch (zero prod overhead).
                ColumnBatch predicateBatch = new NullPoisonColumnBatch(fullBatch);
#else
                ColumnBatch predicateBatch = fullBatch;
#endif
                List<int>? batchSelection = null;
                for (int row = 0; row < fullBatch.RowCount; row++)
                {
                    if (predicate.Matches(predicateBatch, row))
                    {
                        long position = fileRowOffset + row;
                        if (deleted.Add(position))
                        {
                            newlyDeleted++;
                            if (captureChangeData)
                            {
                                // Batch-relative index of a NEWLY-deleted row. fullBatch shares dataBatch's
                                // physical row positions (BuildFullBatch relabels columns without reordering
                                // rows), so `row` indexes dataBatch directly for the cdc selection view.
                                (batchSelection ??= new List<int>()).Add(row);
                            }
                        }
                    }
                }

                if (changeRows is not null && batchSelection is not null)
                {
                    // Physical data columns (dataSchema) of this batch's newly-deleted rows, zero-copy. The
                    // ChangeDataWriter appends the constant `_change_type` and writes exactly these rows.
                    changeRows.Add(
                        dataBatch.WithSelection(new SelectionVector(CollectionsMarshal.AsSpan(batchSelection))));
                }

                fileRowOffset = checked(fileRowOffset + dataBatch.RowCount);
            }
        }

        // The authoritative physical record count is what we actually read; cross-check the file's declared
        // stats.numRecords (now the PHYSICAL count, matching Spark) against it so a lying stat fails closed
        // rather than writing a count that disagrees with the data.
        if (add.Stats?.NumRecords is { } declaredPhysical && declaredPhysical != fileRowOffset)
        {
            throw DeltaStorageException.CorruptData(
                // #667 message hygiene (the DELETE twin of the #663 read-path numRecords-mismatch fix): the
                // file path is not interpolated; the bounded record counts are the diagnosis.
                $"An active data file declares stats.numRecords={declaredPhysical} but the Parquet file "
                + $"contains {fileRowOffset} physical record(s); the DELETE fails closed rather than write a "
                + "count that disagrees with the data.");
        }

        long[] all = new long[deleted.Count];
        deleted.CopyTo(all);
        IReadOnlyList<ColumnBatch> changeRowsResult = changeRows ?? (IReadOnlyList<ColumnBatch>)Array.Empty<ColumnBatch>();
        return new FileDeletionPlan(all, fileRowOffset, newlyDeleted, changeRowsResult);
    }

    // Writes the new DV positions to an on-disk 'u' (relative-path-via-UUID) .bin at the table root and
    // returns the descriptor recorded on the residual add. The UUID comes from the injected id source
    // (deterministic in tests, cryptographic in production — never the banned Guid.NewGuid).
    private async Task<DeletionVectorDescriptor> WriteDeletionVectorAsync(
        long[] sortedDistinctPositions, long cardinality, CancellationToken cancellationToken)
    {
        Guid uuid = _idSource.NextId();
        string pathOrInlineDv = DeletionVectorDescriptor.BuildRelativePathOrInlineDv(string.Empty, uuid);
        string relativePath = DeletionVectorDescriptor.ResolveRelativePath(pathOrInlineDv);

        (int offset, int sizeInBytes) = await DeletionVectorStore
            .WriteOnDiskAsync(_backend, relativePath, sortedDistinctPositions, cancellationToken)
            .ConfigureAwait(false);

        return DeletionVectorDescriptor.ForRelativePath(pathOrInlineDv, offset, sizeInBytes, cardinality);
    }

    // Tombstone the prior logical file, carrying its PRIOR deletion vector so the remove's identity key
    // matches the active add's (SnapshotState keys by path + DV uniqueId). dataChange=true (a delete changes
    // visible data); ExtendedFileMetadata=true round-trips partitionValues/size/tags (the extended trio,
    // including the tombstoned add's tags) for checkpoint fidelity.
    private static RemoveFileAction ToRemove(AddFileAction input, long timestamp) =>
        new(
            input.Path,
            DeletionTimestamp: timestamp,
            DataChange: true,
            ExtendedFileMetadata: true,
            input.PartitionValues,
            input.Size,
            input.Tags,
            input.DeletionVector);

    // The stats for a DV-carrying add: numRecords is the PHYSICAL data-file row count (matching Spark — the
    // total rows in the Parquet file, NOT the residual), which is authoritative; the prior min/max are kept
    // as still-valid LOOSE bounds (a delete only removes rows, so they remain conservative for pruning) with
    // tightBounds=false; null counts are cleared (now stale).
    private static FileStatistics BuildPhysicalStatistics(FileStatistics? prior, long physicalRecords)
    {
        if (prior is null)
        {
            return FileStatistics.Empty with { NumRecords = physicalRecords, TightBounds = false };
        }

        return prior with
        {
            NumRecords = physicalRecords,
            NullCount = EmptyNullCount,
            TightBounds = false,
        };
    }

    private static void SetOutcomeTag(Activity? activity, DeleteOutcome outcome) =>
        activity?.SetTag(DeltaSharpTelemetry.OutcomeKey, DeltaStorageTelemetry.ToLabel(outcome));

    // One file's delete plan: the complete sorted-distinct set of file-relative physical positions to delete
    // (existing DV ∪ newly matched), the file's physical record count (rows actually read), how many of the
    // deleted positions are NEW (not already in the prior DV) — a file with zero new deletes is skipped — and,
    // when CDF is enabled, the physical data rows of exactly those newly-deleted positions (selection views
    // over the batches read during planning), captured in the SAME scan pass so no second full-file read
    // occurs (§4.3). NewlyDeletedRows is empty when CDF is disabled.
    private readonly record struct FileDeletionPlan(
        long[] AllDeletedPositions,
        long PhysicalRecords,
        long NewlyDeletedCount,
        IReadOnlyList<ColumnBatch> NewlyDeletedRows);
}

#if DEBUG
/// <summary>
/// DEBUG-only null-poison view over a <see cref="ColumnBatch"/> that enforces the <see cref="DeltaDeletePredicate"/>
/// three-valued-logic (3VL) contract (#657). It wraps the full-schema logical batch handed to a predicate so an
/// un-<see cref="ColumnVector.IsNull"/>-guarded value read of a null slot (incl. a schema-evolution null-fill,
/// #645) FAILS LOUD instead of silently returning <c>default(T)</c> and mis-deleting. Every member delegates to
/// the inner batch; <see cref="Column(int)"/> (and <see cref="ColumnBatch.Column(string)"/> via the base)
/// returns a <see cref="NullPoisonColumnVector"/>. Compiled only in DEBUG — Release passes the batch through
/// unchanged (zero production overhead).
/// </summary>
internal sealed class NullPoisonColumnBatch : ColumnBatch
{
    private readonly ColumnBatch _inner;

    public NullPoisonColumnBatch(ColumnBatch inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc/>
    public override StructType Schema => _inner.Schema;

    /// <inheritdoc/>
    public override int RowCount => _inner.RowCount;

    /// <inheritdoc/>
    public override int ColumnCount => _inner.ColumnCount;

    /// <inheritdoc/>
    public override SelectionVector? Selection => _inner.Selection;

    /// <inheritdoc/>
    public override ColumnVector Column(int ordinal) => new NullPoisonColumnVector(_inner.Column(ordinal));

    /// <inheritdoc/>
    public override ColumnBatch Slice(int offset, int length) =>
        new NullPoisonColumnBatch(_inner.Slice(offset, length));

    /// <inheritdoc/>
    public override ColumnBatch WithSelection(SelectionVector selection) =>
        new NullPoisonColumnBatch(_inner.WithSelection(selection));
}

/// <summary>
/// DEBUG-only null-poison view over a <see cref="ColumnVector"/> (#657). Members delegate to the inner vector
/// (<see cref="Slice"/> re-wraps so poison propagates; <see cref="ColumnVector.Select"/> is intentionally NOT
/// overridden — see the note there; <see cref="GetValues{T}"/> is a documented, by-design residual) EXCEPT
/// <see cref="GetValue{T}"/> and <see cref="GetBytes"/>: when the inner slot is null those THROW a fixed
/// 3VL-violation <see cref="InvalidOperationException"/> instead of returning <c>default(T)</c> / an empty
/// span, so a DELETE predicate that reads a value without an <see cref="IsNull"/> guard fails loud (the #657
/// footgun). The message carries NO row values, column names, or paths (#653 hygiene).
/// </summary>
internal sealed class NullPoisonColumnVector : ColumnVector
{
    // Fixed, value-free message (#653 hygiene: no row values / column names / paths in a failure message).
    internal const string PoisonMessage =
        "DELETE predicate read a null value at a column without an IsNull guard — three-valued-logic (3VL) "
        + "violation (#657): a null (incl. a schema-evolution null-fill) must be treated as NOT-TRUE, never "
        + "default(T). IsNull-guard every column reference.";

    private readonly ColumnVector _inner;

    public NullPoisonColumnVector(ColumnVector inner)
        : base((inner ?? throw new ArgumentNullException(nameof(inner))).Type) => _inner = inner;

    /// <inheritdoc/>
    public override int Length => _inner.Length;

    /// <inheritdoc/>
    public override int Offset => _inner.Offset;

    /// <inheritdoc/>
    public override bool HasNulls => _inner.HasNulls;

    /// <inheritdoc/>
    public override int NullCount => _inner.NullCount;

    /// <inheritdoc/>
    public override bool IsNull(int index) => _inner.IsNull(index);

    /// <inheritdoc/>
    public override bool TryGetValidity(out Validity validity) => _inner.TryGetValidity(out validity);

    /// <summary>
    /// Delegates to the inner bulk span and is intentionally NOT poisoned: it returns EVERY slot at once, so a
    /// per-slot null throw is impossible here (a caller reading the whole span must pair it with
    /// <see cref="IsNull"/>, per the base contract). The common per-row <see cref="GetValue{T}"/> /
    /// <see cref="GetBytes"/> footgun — the one a naive DELETE delegate hits — IS caught; this residual is by design.
    /// </summary>
    public override ReadOnlySpan<T> GetValues<T>() => _inner.GetValues<T>();

    /// <summary>Poisoned (#657): THROWS on a null slot instead of returning <c>default(T)</c>.</summary>
    public override T GetValue<T>(int index)
    {
        if (_inner.IsNull(index))
        {
            throw new InvalidOperationException(PoisonMessage);
        }

        return _inner.GetValue<T>(index);
    }

    /// <summary>Poisoned (#657): THROWS on a null slot instead of returning an empty span.</summary>
    public override ReadOnlySpan<byte> GetBytes(int index)
    {
        if (_inner.IsNull(index))
        {
            throw new InvalidOperationException(PoisonMessage);
        }

        return _inner.GetBytes(index);
    }

    // Slice re-wraps so poison survives a sub-range view. NOTE: Select is intentionally NOT overridden — the
    // base ColumnVector.Select wraps `this` (a SelectedColumnVector that routes every read back through THIS
    // poison vector), so poison propagates through a selection. A future refactor MUST NOT delegate Select to
    // `_inner` (that would silently drop the poison for a selection-carrying batch).
    /// <inheritdoc/>
    public override ColumnVector Slice(int offset, int length) =>
        new NullPoisonColumnVector(_inner.Slice(offset, length));
}
#endif
