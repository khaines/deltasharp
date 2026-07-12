using System.Collections.Immutable;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
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
/// <para><b>Scope (#499).</b> Base + time-travel reads of non-evolved / current-schema files. An active
/// file physically narrower than the snapshot schema (additive schema evolution, #190) needs read-side
/// null-fill (#497), which is not implemented here: such a file fails <b>closed</b> with a clear
/// <see cref="DeltaReadSchemaEvolutionException"/> (mirroring OPTIMIZE's schema-evolution guard) rather
/// than fabricating values. Predicate/column pushdown into the scan, <c>commitInfo.timestamp</c>
/// resolution (#500), path authorization (#431), and CDF/deletion-vector reads (#192/#193) are out of
/// scope.</para>
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
    /// <exception cref="DeltaReadSchemaEvolutionException">An active file is physically narrower than the
    /// snapshot schema (an evolved file needing #497 read-side null-fill) — fails closed.</exception>
    public async Task<IReadOnlyList<ColumnBatch>> ReadBatchesAsync(
        long version, CancellationToken cancellationToken = default)
    {
        Snapshot snapshot;
        try
        {
            snapshot = await _log.LoadSnapshotAsync(version, cancellationToken).ConfigureAwait(false);
        }
        catch (DeltaProtocolException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }

        StructType tableSchema = snapshot.Schema;
        ImmutableArray<string> partitionColumns = snapshot.Metadata.PartitionColumns;
        StructType dataSchema = BuildDataSchema(tableSchema, partitionColumns);
        int[] dataOrdinalByField = MapDataOrdinals(tableSchema, dataSchema);

        var batches = new List<ColumnBatch>();
        try
        {
            foreach (AddFileAction add in snapshot.ActiveFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadFileAsync(add, tableSchema, dataSchema, dataOrdinalByField, batches, cancellationToken)
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
        StructType dataSchema,
        int[] dataOrdinalByField,
        List<ColumnBatch> batches,
        CancellationToken cancellationToken)
    {
        Stream stream = await _backend.OpenReadAsync(add.Path, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                await foreach (ColumnBatch dataBatch in _reader
                    .ReadAsync(stream, dataSchema, keepRowGroup: null, cancellationToken)
                    .ConfigureAwait(false))
                {
                    batches.Add(BuildFullBatch(add, tableSchema, dataOrdinalByField, dataBatch));
                }
            }
            catch (DeltaStorageException ex) when (IsNarrowSchemaEvolutionInput(ex))
            {
                // FIX #497 (fails closed): the file was written under an older, narrower schema, so a
                // current-schema data column is absent. Read-side null-fill is #497 and out of scope here;
                // translate the misleading "column not present" corruption error into a clear, actionable
                // schema-evolution error (mirrors OPTIMIZE's OptimizeSchemaEvolutionException).
                throw new DeltaReadSchemaEvolutionException(add.Path, ex);
            }
        }
    }

    // Assembles one output batch in table-schema order: partition columns are const/null-filled from the
    // add's partitionValues (they are NOT in the physical file), data columns are taken from the projected
    // Parquet batch. Column vectors are reused (no copy); the const columns are freshly built.
    private static ColumnBatch BuildFullBatch(
        AddFileAction add, StructType tableSchema, int[] dataOrdinalByField, ColumnBatch dataBatch)
    {
        int rowCount = dataBatch.RowCount;
        var columns = new ColumnVector[tableSchema.Count];
        for (int i = 0; i < tableSchema.Count; i++)
        {
            int dataOrdinal = dataOrdinalByField[i];
            if (dataOrdinal >= 0)
            {
                columns[i] = dataBatch.Column(dataOrdinal);
                continue;
            }

            StructField field = tableSchema[i];
            add.PartitionValues.TryGetValue(field.Name, out string? value);
            columns[i] = DeltaReadEncoding.BuildConstantColumn(field.DataType, value, rowCount);
        }

        return new ManagedColumnBatch(tableSchema, columns, rowCount);
    }

    // The table schema minus the partition columns (order-preserving) — the exact shape a Delta Parquet
    // data file stores. For an unpartitioned table this is the full schema.
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

    // For each table-schema field, its ordinal in the data (Parquet) schema, or -1 for a partition column.
    private static int[] MapDataOrdinals(StructType tableSchema, StructType dataSchema)
    {
        var map = new int[tableSchema.Count];
        for (int i = 0; i < tableSchema.Count; i++)
        {
            map[i] = dataSchema.IndexOf(tableSchema[i].Name);
        }

        return map;
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
