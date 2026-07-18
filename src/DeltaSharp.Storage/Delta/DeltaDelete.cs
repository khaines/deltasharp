using System.Collections.Immutable;
using System.Diagnostics;
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
internal abstract class DeltaDeletePredicate
{
    /// <summary>Returns <see langword="true"/> when the row at <paramref name="rowIndex"/> in the
    /// full-schema logical <paramref name="batch"/> should be deleted.</summary>
    public abstract bool Matches(ColumnBatch batch, int rowIndex);

    /// <summary>Builds a predicate from a delegate over a full-schema logical batch and row index.</summary>
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
/// mapping never changes a row's physical position). <c>id</c> mode stays fail-closed (#523) and a nested
/// (struct/array/map) top-level mapped column fails closed rather than risk a wrong delete. On-disk
/// (<c>'u'</c>) deletion vectors are written; the bin-packing/inlining policy for tiny DVs is a follow-up.
/// Predicate/partition pushdown to prune scanned files is a follow-up — every active file is scanned.</para>
/// </summary>
internal sealed class DeltaDelete
{
    private static readonly ImmutableSortedDictionary<string, long> EmptyNullCount =
        ImmutableSortedDictionary<string, long>.Empty.WithComparers(StringComparer.Ordinal);

    private readonly IStorageBackend _backend;
    private readonly DeltaLog _log;
    private readonly DeltaCommitter _committer;
    private readonly TimeProvider _timeProvider;
    private readonly ParquetFileReader _reader;
    private readonly IDeletionVectorIdSource _idSource;
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
        : this(backend, new DeltaLog(backend), new DeltaCommitter(backend))
    {
    }

    /// <summary>Creates a DELETE over an explicit reader + committer (tests inject a committer with a race
    /// probe, a deterministic clock for tombstone/modification timestamps, and a deterministic DV id source
    /// so on-disk DV file names are predictable), plus optional injected logger/telemetry.</summary>
    internal DeltaDelete(
        IStorageBackend backend,
        DeltaLog log,
        DeltaCommitter committer,
        TimeProvider? timeProvider = null,
        ParquetFileReader? reader = null,
        IDeletionVectorIdSource? idSource = null,
        ILogger<DeltaDelete>? logger = null,
        DeltaStorageTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(committer);
        _backend = backend;
        _log = log;
        _committer = committer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reader = reader ?? new ParquetFileReader();
        _idSource = idSource ?? new RandomDeletionVectorIdSource();
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
        // logical name). A nested (struct/array/map) top-level column under column mapping is rejected
        // fail-closed in ColumnMappingProjection.ResolvePhysicalNames rather than risk a wrong delete.
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

        var actions = new List<DeltaAction>();
        var inputPaths = new List<string>();
        int filesWithDeletionVector = 0;
        int filesFullyDeleted = 0;
        long rowsDeleted = 0;

        foreach (AddFileAction add in readSnapshot.ActiveFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileDeletionPlan plan = await PlanFileDeletionAsync(
                add, tableSchema, physicalNames, dataSchema, dataOrdinalByField, resolveByFieldId, predicate, allowTypeWideningPromotion, cancellationToken)
                .ConfigureAwait(false);

            if (plan.NewlyDeletedCount == 0)
            {
                // No row in this file newly matched the predicate — leave its add untouched.
                continue;
            }

            inputPaths.Add(add.Path);
            rowsDeleted += plan.NewlyDeletedCount;

            // The remove carries the file's PRIOR deletion vector so it tombstones the EXACT prior logical
            // file (SnapshotState keys active/tombstone by path + DV uniqueId). dataChange=true: a delete
            // changes the visible data.
            actions.Add(ToRemove(add, timestamp));

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
                    $"Active file '{add.Path}' carries a deletion vector but its add has no stats.numRecords; "
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
        Stream stream = await _backend.OpenReadAsync(add.Path, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await foreach (ColumnBatch dataBatch in _reader
                .ReadAsync(stream, dataSchema, keepRowGroup: null, nullFillMissingColumns: false, allowTypeWideningPromotion, resolveByFieldId, cancellationToken)
                .ConfigureAwait(false))
            {
                ColumnBatch fullBatch = ColumnMappingProjection.BuildFullBatch(
                    add, tableSchema, physicalNames, dataOrdinalByField, dataBatch);
                for (int row = 0; row < fullBatch.RowCount; row++)
                {
                    if (predicate.Matches(fullBatch, row))
                    {
                        long position = fileRowOffset + row;
                        if (deleted.Add(position))
                        {
                            newlyDeleted++;
                        }
                    }
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
                $"Active file '{add.Path}' declares stats.numRecords={declaredPhysical} but the Parquet file "
                + $"contains {fileRowOffset} physical record(s); the DELETE fails closed rather than write a "
                + "count that disagrees with the data.");
        }

        long[] all = new long[deleted.Count];
        deleted.CopyTo(all);
        return new FileDeletionPlan(all, fileRowOffset, newlyDeleted);
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
    // visible data); ExtendedFileMetadata=true round-trips partitionValues/size for checkpoint fidelity.
    private static RemoveFileAction ToRemove(AddFileAction input, long timestamp) =>
        new(
            input.Path,
            DeletionTimestamp: timestamp,
            DataChange: true,
            ExtendedFileMetadata: true,
            input.PartitionValues,
            input.Size,
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
    // (existing DV ∪ newly matched), the file's physical record count (rows actually read), and how many of
    // the deleted positions are NEW (not already in the prior DV) — a file with zero new deletes is skipped.
    private readonly record struct FileDeletionPlan(
        long[] AllDeletedPositions, long PhysicalRecords, long NewlyDeletedCount);
}
