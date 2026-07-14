using System.Collections.Immutable;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Types;

namespace DeltaSharp.Storage;

/// <summary>The resolved identity of a Delta snapshot the read facade opened: the <see cref="Version"/>
/// that was pinned (the exact version for <c>versionAsOf</c>, the resolved version for
/// <c>timestampAsOf</c>, or the latest committed version for a base read) and the table
/// <see cref="Schema"/> at that version.</summary>
/// <param name="Version">The pinned snapshot version.</param>
/// <param name="Schema">The table schema at <paramref name="Version"/>.</param>
public readonly record struct DeltaSnapshotInfo(long Version, StructType Schema);

/// <summary>
/// The PUBLIC storage-side <b>read</b> facade the Executor's Delta scan-source drives (#499) — the
/// symmetric counterpart of the write door's <see cref="DeltaWriteTarget"/>. It resolves the storage
/// backend for a table path (local filesystem for now), <see cref="LoadSnapshotAsync">loads a snapshot</see>
/// (latest / <c>versionAsOf</c> / <c>timestampAsOf</c>, reporting the resolved version), and
/// <see cref="ReadBatchesAsync">reads a snapshot's active data files</see> into full-schema
/// <see cref="ColumnBatch"/>es — re-deriving each partition column from the committed
/// <c>add.partitionValues</c> and const/null-filling it into the output batch (the inverse of the write
/// door's <c>ColumnBatchPartitioner</c>; partition columns live only on the add action and the Hive
/// directory path, never inside the Parquet data file). It reuses the internal
/// <see cref="DeltaLog"/>/<c>Snapshot</c>/<see cref="ParquetFileReader"/> and surfaces only Engine
/// (<see cref="ColumnBatch"/>/<see cref="StructType"/>) types across the seam — no Core/Executor type
/// crosses it (ADR-0014).
///
/// <para><b>Snapshot pinning (no analysis→execution TOCTOU).</b> The caller resolves the version once via
/// <see cref="LoadSnapshotAsync"/> (at analysis) and later reads that exact version via
/// <see cref="ReadBatchesAsync"/> (at execution), so a concurrent commit between the two can never shift
/// the data read.</para>
///
/// <para><b>Scope (#499).</b> Base + time-travel reads of current-schema and <b>additively schema-evolved</b>
/// files (#190/#497), including committed <b>deletion vectors</b> (#192): an active file's DV is decoded and
/// its deleted physical row positions are excluded on read (both inline and on-disk <c>.bin</c> forms). An
/// active file physically narrower than the snapshot schema (a pre-evolution file that predates a later-added
/// column) is read back with the absent, later-added <b>nullable</b> columns <b>null-filled</b> (read-side
/// null-fill, #497). A genuinely incompatible mismatch still fails <b>closed</b>: an absent <i>non-nullable</i>
/// (required) column cannot be null-filled and surfaces a clear <see cref="DeltaReadSchemaEvolutionException"/>
/// (mirroring OPTIMIZE's schema-evolution guard) rather than fabricating values. Predicate/column pushdown
/// into the scan, <c>commitInfo.timestamp</c> resolution (#500), path authorization (#431), and CDF reads
/// (#193) are out of scope.</para>
/// </summary>
public sealed class DeltaReadSource : IDisposable
{
    private readonly LocalFileSystemBackend _backend;
    private readonly DeltaLog _log;
    private readonly ParquetFileReader _reader = new();

    private DeltaReadSource(LocalFileSystemBackend backend)
    {
        _backend = backend;
        _log = new DeltaLog(backend);
    }

    /// <summary>Opens a read facade over a local-filesystem Delta table directory.</summary>
    /// <param name="tablePath">The table root path.</param>
    /// <exception cref="ArgumentException"><paramref name="tablePath"/> is null or empty.</exception>
    public static DeltaReadSource ForLocalPath(string tablePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(tablePath);
        return new DeltaReadSource(new LocalFileSystemBackend(tablePath));
    }

    /// <summary>
    /// Resolves and loads the snapshot to read, honoring a pinned <paramref name="versionAsOf"/> XOR
    /// <paramref name="timestampAsOf"/> (both null = latest), and reports the resolved version + schema.
    /// A <c>timestampAsOf</c> strictly after the latest commit fails closed (Spark batch-read parity —
    /// <c>canReturnLastCommit=false</c>).
    /// </summary>
    /// <param name="versionAsOf">A pinned exact version, or <see langword="null"/>.</param>
    /// <param name="timestampAsOf">A pinned UTC timestamp, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the log listing/reconstruction I/O.</param>
    /// <returns>The resolved version + table schema.</returns>
    /// <exception cref="ArgumentException">Both a version and a timestamp were supplied.</exception>
    /// <exception cref="DeltaReadException">Not a Delta table, an out-of-range/retention-gap version, a
    /// timestamp out of range, or a malformed log.</exception>
    public async Task<DeltaSnapshotInfo> LoadSnapshotAsync(
        long? versionAsOf, DateTimeOffset? timestampAsOf, CancellationToken cancellationToken = default)
    {
        if (versionAsOf is not null && timestampAsOf is not null)
        {
            throw new ArgumentException(
                "Specify at most one of versionAsOf / timestampAsOf, never both.", nameof(timestampAsOf));
        }

        try
        {
            if (timestampAsOf is { } asOf)
            {
                TimeTravelResult result = await _log
                    .LoadSnapshotAsOfTimestampAsync(asOf, canReturnLatest: false, cancellationToken)
                    .ConfigureAwait(false);
                return new DeltaSnapshotInfo(result.ResolvedVersion, result.Snapshot.Schema);
            }

            Snapshot snapshot = await _log
                .LoadSnapshotAsync(versionAsOf, cancellationToken)
                .ConfigureAwait(false);
            return new DeltaSnapshotInfo(snapshot.Version, snapshot.Schema);
        }
        catch (DeltaProtocolException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Reads the snapshot at the pinned <paramref name="version"/> into full-schema, partition-filled
    /// batches, in active-file order. The output batches carry the table schema; each partition column is
    /// const/null-filled from the file's committed <c>add.partitionValues</c>.
    /// </summary>
    /// <param name="version">The exact version to read (as returned by <see cref="LoadSnapshotAsync"/>).</param>
    /// <param name="cancellationToken">Cancels the log reconstruction and per-file Parquet reads.</param>
    /// <returns>The active data as full-schema batches (an empty list for an empty snapshot).</returns>
    /// <exception cref="DeltaReadException">The version cannot be reconstructed (out of range/gap/malformed),
    /// or an active file could not be read at execution — a between-phase missing/deleted file or a poisoned
    /// <c>add.path</c> confinement rejection — wrapped as this one typed, documented read failure.</exception>
    /// <exception cref="DeltaReadSchemaEvolutionException">An active file is missing a <b>required</b>
    /// (non-nullable) column the snapshot schema requests — a genuine incompatibility that read-side null-fill
    /// cannot satisfy (only absent nullable columns are null-filled, #497) — fails closed.</exception>
    public async Task<IReadOnlyList<ColumnBatch>> ReadBatchesAsync(
        long version, CancellationToken cancellationToken = default)
    {
        Snapshot snapshot;
        StructType tableSchema;
        string[] physicalNames;
        StructType dataSchema;
        int[] dataOrdinalByField;
        try
        {
            snapshot = await _log.LoadSnapshotAsync(version, cancellationToken).ConfigureAwait(false);

            // Column-mapping resolution (§2.12.3; STORY-05.4.3 AC1). In name mode a table's Parquet columns
            // are stored under their PHYSICAL names and add.partitionValues are keyed by physical name, so we
            // read by physical name and relabel the result to the LOGICAL schema for the caller. In none mode
            // the physical name IS the logical name, so this is exactly the prior behavior. (id mode never
            // reaches here — it is rejected fail-closed at snapshot load, deferred to #523.) FIX #6: these
            // mapping/BuildDataSchema calls run INSIDE the try so a name-mode resolution fault
            // (DeltaProtocolException from ColumnMapping, or a SchemaValidationException from a malformed
            // physical data schema) surfaces as the documented DeltaReadException — never leaks un-normalized
            // past the facade. The #497 DeltaReadSchemaEvolutionException stays distinct (thrown per-file below).
            tableSchema = snapshot.Schema;
            ColumnMappingMode mappingMode = ColumnMapping.ResolveMode(snapshot.Metadata.Configuration);
            ImmutableArray<string> partitionColumns = snapshot.Metadata.PartitionColumns;
            physicalNames = ColumnMappingProjection.ResolvePhysicalNames(tableSchema, mappingMode);
            dataSchema = ColumnMappingProjection.BuildDataSchema(tableSchema, physicalNames, partitionColumns);
            dataOrdinalByField = ColumnMappingProjection.MapDataOrdinals(physicalNames, dataSchema);
        }
        catch (DeltaProtocolException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }
        catch (SchemaValidationException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }

        var batches = new List<ColumnBatch>();
        // Read-side type-widening promotion gate (#495): only when the snapshot's protocol declares the
        // `typeWidening` feature may the reader promote a narrow-physical file into the current (widened)
        // schema. We know the protocol here (the scan layer), so we pass it explicitly; the stream-level
        // ParquetFileReader cannot see the protocol and trusts this gate. A wide schema over narrow files
        // WITHOUT the feature (a tampered/malformed log) fails closed rather than being silently promoted.
        bool allowTypeWideningPromotion = TypeWideningFeature.Supports(snapshot.Protocol);
        try
        {
            foreach (AddFileAction add in snapshot.ActiveFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadFileAsync(
                        add, tableSchema, physicalNames, dataSchema, dataOrdinalByField, batches, allowTypeWideningPromotion, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (DeltaStorageException ex)
        {
            // A between-phase missing/deleted active file (NotFound) or a poisoned add.path confinement
            // rejection (PathNotConfined) — or any other internal storage fault raised while reading a file
            // — surfaces the storage layer's internal DeltaStorageException. Wrap it as the facade's
            // documented DeltaReadException so the pinned-version-vanished / poisoned-path window has ONE
            // typed, catchable failure mode across the seam (its message is already free of raw storage
            // detail — the backend discloses only the confined path). The #497 schema-evolution case is a
            // distinct DeltaReadSchemaEvolutionException (not a DeltaStorageException) and escapes this catch.
            throw new DeltaReadException(ex.Message, ex);
        }

        return batches;
    }

    private async Task ReadFileAsync(
        AddFileAction add,
        StructType tableSchema,
        string[] physicalNames,
        StructType dataSchema,
        int[] dataOrdinalByField,
        List<ColumnBatch> batches,
        bool allowTypeWideningPromotion,
        CancellationToken cancellationToken)
    {
        // Load the committed deletion vector (if any) BEFORE reading data, so a poisoned/malformed DV fails
        // the read closed here rather than after emitting rows (the cardinal DV safety rule: never return a
        // row a DV invalidated because the DV failed to decode). The DV's row positions are PHYSICAL,
        // file-relative ordinals validated against the file's PHYSICAL record count.
        DeletionVectorPositions? deletionVector = null;
        if (add.DeletionVector is { } descriptor)
        {
            long? declared = add.Stats?.NumRecords;
            if (declared is not { } declaredPhysicalRecords)
            {
                // A DV-carrying add MUST record numRecords in stats (Delta writer requirement). Without the
                // physical record count we cannot cross-check the file, so fail closed.
                throw new DeltaReadException(
                    $"Active file '{add.Path}' carries a deletion vector but its add action has no "
                    + "stats.numRecords, so the deleted row positions cannot be validated. The read fails "
                    + "closed rather than risk returning rows a deletion vector invalidated.");
            }

            // FIX #4 (DoS): bound the DV decode by the file's REAL physical row count — read from the Parquet
            // footer (metadata only, no page decode), never the attacker-controlled descriptor/stats. A
            // malicious 512 MiB .bin can no longer force an allocation: the ceiling is the true row count, so
            // any oversized declared size / out-of-range position / over-large cardinality fails closed first.
            long physicalRecords;
            try
            {
                Stream metaStream = await _backend.OpenReadAsync(add.Path, cancellationToken).ConfigureAwait(false);
                await using (metaStream.ConfigureAwait(false))
                {
                    physicalRecords = await _reader.GetRowCountAsync(metaStream, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (DeltaStorageException ex)
            {
                throw new DeltaReadException(ex.Message, ex);
            }

            // FIX #2 (numRecords semantics): on a DV-carrying add, stats.numRecords IS the PHYSICAL data-file
            // row count (matching Spark), NOT the residual (post-deletion) count. It must therefore equal the
            // file's real row count; a disagreement is a corrupt/lying stat → fail closed. (The residual
            // logical count, if ever needed, is numRecords − cardinality.)
            if (declaredPhysicalRecords != physicalRecords)
            {
                throw new DeltaReadException(
                    $"Active file '{add.Path}' declares stats.numRecords={declaredPhysicalRecords} but its "
                    + $"Parquet file contains {physicalRecords} physical row(s); a DV-carrying add's numRecords "
                    + "must equal the physical row count, so the read fails closed.");
            }

            long[] positions;
            try
            {
                positions = await DeletionVectorStore
                    .LoadAsync(_backend, descriptor, physicalRecords, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DeltaStorageException ex)
            {
                // A malformed/oversized bitmap, bad magic/version, CRC/size/cardinality mismatch, out-of-range
                // position, invalid Z85, or an out-of-file .bin offset/length — all surface as a typed
                // storage fault. Fail the read closed (never silently ignore the DV and return deleted rows).
                throw new DeltaReadException(ex.Message, ex);
            }

            deletionVector = new DeletionVectorPositions(positions, physicalRecords);
        }

        long fileRowOffset = 0;
        Stream stream = await _backend.OpenReadAsync(add.Path, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                await foreach (ColumnBatch dataBatch in _reader
                    .ReadAsync(stream, dataSchema, keepRowGroup: null, nullFillMissingColumns: true, allowTypeWideningPromotion, cancellationToken)
                    .ConfigureAwait(false))
                {
                    ColumnBatch fullBatch = ColumnMappingProjection.BuildFullBatch(
                        add, tableSchema, physicalNames, dataOrdinalByField, dataBatch);

                    if (deletionVector is null)
                    {
                        batches.Add(fullBatch);
                    }
                    else if (ApplyDeletionVector(fullBatch, deletionVector, fileRowOffset) is { } survived)
                    {
                        // A fully-deleted batch (every physical row invalidated) contributes no rows, so it
                        // is dropped rather than added as an empty batch.
                        batches.Add(survived);
                    }

                    fileRowOffset = checked(fileRowOffset + dataBatch.RowCount);
                }
            }
            catch (DeltaStorageException ex) when (IsNarrowSchemaEvolutionInput(ex))
            {
                // #497: absent NULLABLE columns are null-filled by the reader (nullFillMissingColumns: true
                // above), so this only fires when a file is missing a REQUIRED (non-nullable) column the
                // current schema demands — a genuine incompatibility read-side null-fill cannot satisfy (a
                // required lane cannot carry null). Translate the low-level "column not present" corruption
                // error into a clear, actionable schema-evolution error (mirrors OPTIMIZE's
                // OptimizeSchemaEvolutionException) rather than fabricating values or leaking a misleading
                // corruption message.
                throw new DeltaReadSchemaEvolutionException(add.Path, ex);
            }
        }

        if (deletionVector is not null)
        {
            deletionVector.EnsureFullyConsumed(fileRowOffset, add.Path);
        }
    }

    // The decoded deletion-vector positions for one active file: the sorted, distinct set of PHYSICAL,
    // file-relative row ordinals to exclude, plus the file's physical record count for a post-read
    // consistency check. FIX #4: a SINGLE materialization — the sorted long[] the decoder already produced —
    // with binary-search membership, so the read path never holds both a long[] AND a HashSet of the set.
    private sealed class DeletionVectorPositions
    {
        private readonly long[] _deleted;

        public DeletionVectorPositions(long[] sortedDistinctPositions, long physicalRecords)
        {
            // RoaringBitmapArray.Deserialize guarantees ascending, distinct positions, so the array is a
            // ready-made sorted-set membership structure (Array.BinarySearch) with no second copy.
            _deleted = sortedDistinctPositions;
            PhysicalRecords = physicalRecords;
        }

        public long PhysicalRecords { get; }

        public bool IsDeleted(long filePosition) => Array.BinarySearch(_deleted, filePosition) >= 0;

        // The Parquet file's actual physical row count (summed across row groups) must match the record count
        // the DV was validated against — a mismatch means the file changed under the DV, so the positions
        // cannot be trusted to map to the right rows. Fail closed.
        public void EnsureFullyConsumed(long physicalRowsRead, string path)
        {
            if (physicalRowsRead != PhysicalRecords)
            {
                throw new DeltaReadException(
                    $"Active file '{path}' carries a deletion vector validated against {PhysicalRecords} "
                    + $"physical records, but the Parquet file produced {physicalRowsRead} on read. The "
                    + "deletion vector disagrees with the data file, so the read fails closed.");
            }
        }
    }

    // Applies a deletion vector to one full-schema batch by building a SelectionVector of the surviving
    // physical rows (those whose file-relative ordinal `fileRowOffset + r` is NOT in the DV). Returns null
    // when every row in the batch is deleted (the caller drops it). The DV positions are file-relative, so
    // the running `fileRowOffset` maps this batch's physical rows [0, RowCount) onto the file ordinal space.
    private static ColumnBatch? ApplyDeletionVector(
        ColumnBatch batch, DeletionVectorPositions deletionVector, long fileRowOffset)
    {
        int rowCount = batch.RowCount;
        var survivors = new List<int>(rowCount);
        for (int r = 0; r < rowCount; r++)
        {
            if (!deletionVector.IsDeleted(fileRowOffset + r))
            {
                survivors.Add(r);
            }
        }

        if (survivors.Count == rowCount)
        {
            // No row in this batch was deleted — return it unchanged (identity selection is pure overhead).
            return batch;
        }

        if (survivors.Count == 0)
        {
            return null;
        }

        return batch.WithSelection(new SelectionVector(survivors.ToArray()));
    }

    // True iff the read failed because the input Parquet file is missing a column the current data schema
    // requests — the additive schema-evolution (#190) narrow-file case that needs read-side null-fill (#497).
    // A genuine corruption or a real type mismatch carries a different message/kind and does NOT match.
    // TRACKED DEFERRAL (#513): this classification is string-coupled to ParquetFileReader's "is not present
    // in the Parquet file schema" message (the same coupling exists at OPTIMIZE's guard site in
    // DeltaOptimize.IsNarrowSchemaEvolutionInput). A shared, message-independent error-kind that both guard
    // sites match on is #513.
    private static bool IsNarrowSchemaEvolutionInput(DeltaStorageException ex) =>
        ex.Kind == StorageErrorKind.CorruptData
        && ex.Message.Contains("is not present in the Parquet file schema", StringComparison.Ordinal);

    /// <inheritdoc/>
    public void Dispose() => _backend.Dispose();
}
