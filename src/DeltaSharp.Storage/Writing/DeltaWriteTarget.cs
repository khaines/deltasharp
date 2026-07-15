using System.Collections.Immutable;
using System.Security.Cryptography;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;

namespace DeltaSharp.Storage;

/// <summary>How an overwrite replaces prior data, mirroring Spark's
/// <c>spark.sql.sources.partitionOverwriteMode</c>. This is the PUBLIC write-facade counterpart of the
/// internal <c>PartitionOverwriteMode</c> (#487): <see cref="Static"/> replaces the whole table;
/// <see cref="Dynamic"/> replaces only the partitions the new write touches.</summary>
public enum DeltaPartitionOverwriteMode
{
    /// <summary>Full-table overwrite (the default): every prior active file is removed.</summary>
    Static,

    /// <summary>Dynamic partition overwrite: only prior files in touched partitions are removed.</summary>
    Dynamic,
}

/// <summary>The outcome of a committed Delta write: the log <see cref="Version"/> that became visible, the
/// number of Parquet data <see cref="FilesWritten"/>, and the total <see cref="RowsWritten"/>.</summary>
public readonly record struct DeltaWriteResult(long Version, int FilesWritten, long RowsWritten);

/// <summary>
/// The PUBLIC storage-side write facade the Executor's Delta sink drives (#487, STORY-05.3.3 follow-up).
/// It resolves the storage backend for a table path (local filesystem for now), stages a
/// <see cref="ColumnBatch"/> — partitioned by the declared partition columns — into Parquet data
/// file(s) under the table directory (reusing <c>ParquetFileWriter</c>, computing size/mtime/statistics),
/// and commits the write (Append or Overwrite) through the internal Delta commit engine
/// (<c>DeltaTableWriter</c>), creating the table on first write. It also exposes a cheap existence check so
/// a caller can honor Ignore/ErrorIfExists save modes before executing the query.
///
/// <para>No Core/Executor type crosses this seam: the facade takes only <see cref="StructType"/> and
/// <see cref="ColumnBatch"/> (Engine) plus partition-column names and a save-mode-neutral write shape. The
/// caller (the Executor's Delta sink) maps Spark's <c>SaveMode</c> onto <see cref="AppendAsync"/> /
/// <see cref="OverwriteAsync"/> / <see cref="TableExistsAsync"/>.</para>
///
/// <para><b>Failure atomicity.</b> Staged Parquet files are published before the log commit; if the commit
/// fails (a mode conflict, a concurrent-write abort) the staged files are never referenced by any
/// <c>add</c> action — they become orphans reclaimable by VACUUM, never a partial commit.</para>
/// </summary>
public sealed class DeltaWriteTarget : IDisposable
{
    private readonly LocalFileSystemBackend _backend;
    private readonly DeltaLog _log;
    private readonly DeltaTableWriter _writer;
    private readonly ParquetFileWriter _parquetWriter = new();
    private readonly ParquetFileReader _reader = new();
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _fileNameFactory;

    private DeltaWriteTarget(
        LocalFileSystemBackend backend,
        TimeProvider timeProvider,
        Func<string> fileNameFactory,
        IColumnPhysicalNameSource? nameSource = null)
    {
        _backend = backend;
        _log = new DeltaLog(backend);

        // Construct the committer with an optional injectable physical-name source (null ⇒ the production
        // crypto RNG). This is byte-for-byte equivalent to `new DeltaTableWriter(backend)` when nameSource is
        // null; a test injects a deterministic source so a name-mode evolution (#556) mints golden physical
        // names. TimeProvider.System matches the pre-#556 committer clock (the door's own _timeProvider drives
        // only staged-file mtime).
        _writer = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend), TimeProvider.System, nameSource);
        _timeProvider = timeProvider;
        _fileNameFactory = fileNameFactory;
    }

    /// <summary>Opens a write target over a local-filesystem table directory (created if absent).</summary>
    /// <param name="tablePath">The table root path.</param>
    /// <exception cref="ArgumentException"><paramref name="tablePath"/> is null or empty.</exception>
    public static DeltaWriteTarget ForLocalPath(string tablePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(tablePath);
        return new DeltaWriteTarget(new LocalFileSystemBackend(tablePath), TimeProvider.System, DefaultFileNameFactory);
    }

    // A deterministic test factory: injects a fixed clock (createdTime/mtime) and a reproducible data-file
    // name source so a golden column-mapping fixture is byte-for-byte stable.
    internal static DeltaWriteTarget ForLocalPath(
        string tablePath, TimeProvider timeProvider, Func<string> fileNameFactory)
    {
        ArgumentException.ThrowIfNullOrEmpty(tablePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(fileNameFactory);
        return new DeltaWriteTarget(new LocalFileSystemBackend(tablePath), timeProvider, fileNameFactory);
    }

    // A deterministic test factory that ALSO injects the committer's physical-name source, so a name-mode
    // schema-evolution / overwriteSchema-add through the door (#556) mints golden physical names. Sharing one
    // seeded source instance across the create call and this writer keeps the minted col-<uuid> sequence
    // monotonic (no collision between the create-time and evolution-time mints).
    internal static DeltaWriteTarget ForLocalPath(
        string tablePath, TimeProvider timeProvider, Func<string> fileNameFactory, IColumnPhysicalNameSource nameSource)
    {
        ArgumentException.ThrowIfNullOrEmpty(tablePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(fileNameFactory);
        ArgumentNullException.ThrowIfNull(nameSource);
        return new DeltaWriteTarget(new LocalFileSystemBackend(tablePath), timeProvider, fileNameFactory, nameSource);
    }

    // A collision-resistant data-file name from the sanctioned deterministic RNG (never the banned
    // Guid.NewGuid), so two concurrent writers never stage the same physical path (mirrors DeltaOptimize).
    private static string DefaultFileNameFactory() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    // Whether any batch carries at least one logical row — the write actually has data to stage. An
    // empty append (no batches, or only zero-row batches) is a benign no-op on an existing table.
    private static bool HasRows(IReadOnlyList<ColumnBatch> batches)
    {
        foreach (ColumnBatch batch in batches)
        {
            if (batch.LogicalRowCount > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a Delta table already exists at this path (any committed version). Used to honor
    /// Ignore/ErrorIfExists before the write query executes.</summary>
    public async Task<bool> TableExistsAsync(CancellationToken cancellationToken = default) =>
        await _log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Appends <paramref name="batches"/> to the table (creating it on first write).</summary>
    /// <param name="writeSchema">The full write schema (partition + data columns).</param>
    /// <param name="partitionColumns">The partition column names, in order (a subset of the schema).</param>
    /// <param name="batches">The full-schema batches to write.</param>
    /// <param name="mergeSchema">Whether an incompatible-but-additive write may EVOLVE the schema (Spark's
    /// <c>mergeSchema</c> write option — add a new nullable column / apply a sanctioned type widening the
    /// table enables) or must strictly conform to it (<see langword="false"/>, the default). Under
    /// column-mapping name mode a new column is minted a fresh physical name ONCE and staged under it, so the
    /// door and the commit agree on the physical identity (#556).</param>
    /// <param name="cancellationToken">Cancels staging and the commit.</param>
    public async Task<DeltaWriteResult> AppendAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        bool mergeSchema = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);

        SchemaEvolutionMode evolutionMode = mergeSchema ? SchemaEvolutionMode.MergeSchema : SchemaEvolutionMode.None;

        if (await _log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            // Fresh path: the write CREATES a plain (none-mode) table — logical==physical, nothing to mint,
            // and evolutionMode is moot (the declared schema becomes version 0). A name-mode table is created
            // via CreateNameMappedTableAsync, not this facade path.
            (IReadOnlyList<StagedDataFile> createFiles, long createRows) =
                await StageAsync(writeSchema, partitionColumns, batches, cancellationToken).ConfigureAwait(false);
            DeltaCommitResult created = await _writer
                .CreateOrAppendAsync(writeSchema, partitionColumns, createFiles, cancellationToken)
                .ConfigureAwait(false);
            return new DeltaWriteResult(created.Version, createFiles.Count, createRows);
        }

        // #525/#541/#556: resolve the physical staging shape against the loaded snapshot, minting a name-mode
        // additive/widening column's physicalName+id EXACTLY ONCE, then stage under those physical names and
        // commit the SAME plan — so the appended Parquet bytes and the committed metaData never disagree on a
        // column's physical identity (no independent door-vs-committer mint). For a `none`-mode table or a
        // compatible (same-schema) write this reduces to the pre-#556 staging behavior.
        Snapshot snapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);

        // An empty append to an existing table adds nothing — a benign no-op (Spark parity), mirroring
        // DeltaTableWriter.CreateOrAppendAsync (whose 0-file branch returns Skipped BEFORE any schema
        // enforcement). Short-circuit BEFORE planning so an empty append neither runs enforcement nor mints
        // (an empty write carries no rows to define a new column) — #556 council: Architect/Reliability R1.
        if (!HasRows(batches))
        {
            return new DeltaWriteResult(snapshot.Version, 0, 0L);
        }

        DeltaWritePlan plan = _writer.PlanAppend(snapshot, writeSchema, evolutionMode);

        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(plan.PhysicalWriteSchema, plan.PhysicalPartitionColumns, batches, cancellationToken)
                .ConfigureAwait(false);

        DeltaCommitResult commit = await _writer
            .CommitAppendAsync(snapshot, plan, files, cancellationToken).ConfigureAwait(false);
        return new DeltaWriteResult(commit.Version, files.Count, rows);
    }

    /// <summary>Overwrites the table with <paramref name="batches"/> (creating it on first write), replacing
    /// either the whole table (<see cref="DeltaPartitionOverwriteMode.Static"/>) or only the touched
    /// partitions (<see cref="DeltaPartitionOverwriteMode.Dynamic"/>).
    /// <para>When <paramref name="overwriteSchema"/> is <see langword="true"/> (the connector's
    /// <c>overwriteSchema</c> option, #496) a full (Static) overwrite <b>replaces the table schema wholesale</b>
    /// — it may drop, narrow, reorder, add, or change the type of columns, and change the partition columns —
    /// because every prior file is rewritten. It is rejected for a Dynamic partition overwrite (files in
    /// untouched partitions would still carry the old schema). The staged files are gated against the new
    /// schema, so the committed metadata matches the real bytes.</para>
    /// <para>For a <b>name-mode column-mapped</b> table, this door supports an <c>overwriteSchema</c> that
    /// keeps the same columns, drops / reorders / retypes them, <b>or ADDS a new column</b> (#556): the door
    /// reconciles the columnMapping (minting a new column's physical name+id ONCE), stages the Parquet files
    /// under the resulting physical names, and commits that same mapping — so the staged bytes and the
    /// committed <c>metaData</c> agree on every column's physical identity. <c>id</c> mode stays fail-closed
    /// (#523, rejected at snapshot load).</para></summary>
    /// <exception cref="ArgumentException"><paramref name="overwriteSchema"/> is combined with
    /// <see cref="DeltaPartitionOverwriteMode.Dynamic"/> (only a full/Static overwrite may replace the schema).</exception>
    public async Task<DeltaWriteResult> OverwriteAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        DeltaPartitionOverwriteMode partitionOverwriteMode,
        bool overwriteSchema = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);

        // #556: a wholesale overwriteSchema replacement on an EXISTING table (Static/full overwrite only)
        // routes through the plan/commit split so a name-mode ADD mints the new column's physical name+id
        // ONCE and stages under it. A fresh path, a `none`-mode drop/retype/reorder, and — crucially — the
        // dynamic+overwriteSchema REJECT all keep the pre-#556 route below (CreateOrOverwriteAsync validates
        // overwriteSchema, including throwing for a dynamic partition overwrite).
        if (overwriteSchema
            && partitionOverwriteMode == DeltaPartitionOverwriteMode.Static
            && await _log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            Snapshot snapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
            DeltaWritePlan plan = _writer.PlanOverwriteReplaceSchema(snapshot, writeSchema, partitionColumns);

            (IReadOnlyList<StagedDataFile> replaceFiles, long replaceRows) =
                await StageAsync(plan.PhysicalWriteSchema, plan.PhysicalPartitionColumns, batches, cancellationToken)
                    .ConfigureAwait(false);
            DeltaCommitResult replaced = await _writer
                .CommitOverwriteReplaceSchemaAsync(snapshot, plan, replaceFiles, cancellationToken)
                .ConfigureAwait(false);
            return new DeltaWriteResult(replaced.Version, replaceFiles.Count, replaceRows);
        }

        // #525: stage under the table's PHYSICAL schema for an EXISTING name-mode table (see AppendAsync); a
        // fresh path or a `none`-mode table returns the logical schema unchanged. The mode-aware overwrite
        // (incl. the dynamic+overwriteSchema reject and fresh-create) is applied by CreateOrOverwriteAsync.
        (StructType stagingSchema, IReadOnlyList<string> stagingPartitions) =
            await ResolvePhysicalStagingAsync(writeSchema, partitionColumns, cancellationToken).ConfigureAwait(false);

        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(stagingSchema, stagingPartitions, batches, cancellationToken).ConfigureAwait(false);
        DeltaCommitResult commit = await _writer
            .CreateOrOverwriteAsync(
                writeSchema, partitionColumns, files, Map(partitionOverwriteMode), overwriteSchema, cancellationToken)
            .ConfigureAwait(false);
        return new DeltaWriteResult(commit.Version, files.Count, rows);
    }

    /// <summary>
    /// Creates a fresh Delta table with column mapping <c>name</c> mode enabled (STORY-05.4.3 / #191). Each
    /// top-level column is assigned a stable physical name (<c>col-&lt;uuid&gt;</c> from
    /// <paramref name="physicalNameSource"/>) and id; the Parquet data files are written under those
    /// <b>physical</b> names, <c>add.partitionValues</c> are keyed by physical name, the data-file path is
    /// physical, and the committed <c>metaData</c> carries the logical schema (with per-field id/physicalName),
    /// the <b>LOGICAL</b> <c>partitionColumns</c> (matching the Spark golden <c>dv-with-columnmapping</c>: name
    /// mode records partition IDENTITY logically while partition VALUE KEYS stay physical), the
    /// <c>delta.columnMapping.mode=name</c> / <c>maxColumnId</c> configuration, and the table-features
    /// protocol declaring the <c>columnMapping</c> feature. Enablement is scoped to a fresh table (there is
    /// nothing to read through), so every file is physical-named from version 0.
    /// </summary>
    /// <exception cref="InvalidOperationException">A table already exists at this path (enabling column
    /// mapping on an existing non-empty table is out of scope in this build).</exception>
    internal async Task<DeltaWriteResult> CreateNameMappedTableAsync(
        StructType logicalSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        IColumnPhysicalNameSource physicalNameSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logicalSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(physicalNameSource);

        if (await TableExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Enabling column mapping on an existing table is out of scope in this build; column mapping "
                + "'name' mode can only be enabled on a fresh table (first write).");
        }

        (StructType mappedSchema, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(logicalSchema, physicalNameSource);

        // Route the staging into PHYSICAL name space: renaming the write schema + partition columns to their
        // physical names makes the existing partitioner/Parquet writer emit files with physical column names,
        // physical-keyed partition values, and a physical data-file path — no new staging path needed.
        StructType physicalSchema = ColumnMapping.ToPhysicalSchema(mappedSchema, ColumnMappingMode.Name);
        IReadOnlyList<string> physicalPartitions =
            ColumnMapping.PhysicalPartitionColumns(mappedSchema, partitionColumns, ColumnMappingMode.Name);

        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(physicalSchema, physicalPartitions, batches, cancellationToken).ConfigureAwait(false);

        // metaData.partitionColumns are the LOGICAL names (HIGH#1 / Spark golden); the staged files carry
        // PHYSICAL partition-value keys, so CreateMappedTableAsync validates coverage against the physical
        // set while committing the logical names into the metaData.
        DeltaCommitResult commit = await _writer
            .CreateMappedTableAsync(
                mappedSchema,
                partitionColumns,
                physicalPartitions,
                ColumnMapping.NameModeConfiguration(maxColumnId),
                ColumnMapping.NameModeProtocol(),
                files,
                cancellationToken)
            .ConfigureAwait(false);
        return new DeltaWriteResult(commit.Version, files.Count, rows);
    }

    /// <summary>
    /// Creates a fresh Delta table that BOTH uses column mapping <c>name</c> mode AND enables deletion
    /// vectors (#529 test seam): each top-level column is assigned a stable physical name / id (like
    /// <see cref="CreateNameMappedTableAsync"/>) and the committed <c>protocol</c>/<c>configuration</c>
    /// additionally declare the <c>deletionVectors</c> feature (reader v3 / writer v7) and set
    /// <c>delta.enableDeletionVectors=true</c>, so a subsequent <see cref="DeltaDelete"/> passes the
    /// protocol gate and exercises the WRITE-path column-mapping relabel. Data files are written under the
    /// PHYSICAL names; <c>metaData.partitionColumns</c> carry the LOGICAL names and the staged partition
    /// values are physical-keyed — identical to the pure name-mode create.
    /// </summary>
    /// <exception cref="InvalidOperationException">A table already exists at this path.</exception>
    internal async Task<DeltaWriteResult> CreateNameMappedDeletionVectorTableAsync(
        StructType logicalSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        IColumnPhysicalNameSource physicalNameSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logicalSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(physicalNameSource);

        if (await TableExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Enabling column mapping + deletion vectors on an existing table is out of scope in this "
                + "build; both can only be enabled on a fresh table (first write).");
        }

        (StructType mappedSchema, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(logicalSchema, physicalNameSource);

        StructType physicalSchema = ColumnMapping.ToPhysicalSchema(mappedSchema, ColumnMappingMode.Name);
        IReadOnlyList<string> physicalPartitions =
            ColumnMapping.PhysicalPartitionColumns(mappedSchema, partitionColumns, ColumnMappingMode.Name);

        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(physicalSchema, physicalPartitions, batches, cancellationToken).ConfigureAwait(false);

        // Merge the name-mode and deletion-vector enablement into ONE protocol (reader v3 / writer v7 with
        // BOTH features declared) and ONE configuration (columnMapping mode/maxColumnId + enableDeletionVectors).
        ImmutableSortedDictionary<string, string> configuration = ColumnMapping.NameModeConfiguration(maxColumnId)
            .Add(DeletionVectorsFeature.EnablePropertyKey, "true");
        var protocol = new ProtocolAction(
            DeletionVectorsFeature.ReaderVersion,
            DeletionVectorsFeature.WriterVersion,
            ImmutableArray.Create(ColumnMapping.Feature, DeletionVectorsFeature.Feature),
            ImmutableArray.Create(ColumnMapping.Feature, DeletionVectorsFeature.Feature));

        DeltaCommitResult commit = await _writer
            .CreateMappedTableAsync(
                mappedSchema,
                partitionColumns,
                physicalPartitions,
                configuration,
                protocol,
                files,
                cancellationToken)
            .ConfigureAwait(false);
        return new DeltaWriteResult(commit.Version, files.Count, rows);
    }

    /// <summary>
    /// Creates a fresh Delta table with <b>deletion vectors enabled</b> (STORY-05.5.1 / #192): the committed
    /// <c>protocol</c> declares the <c>deletionVectors</c> table feature (reader v3 / writer v7) and the
    /// <c>metaData.configuration</c> sets <c>delta.enableDeletionVectors=true</c>, so a subsequent
    /// <see cref="DeltaDelete"/> passes the protocol gate. Data files are written normally (no column
    /// mapping); this is the write-side enablement seam the AC2/AC3/AC4 tests build a table with.
    /// </summary>
    /// <exception cref="InvalidOperationException">A table already exists at this path (enabling deletion
    /// vectors on an existing table is out of scope in this build).</exception>
    internal async Task<DeltaWriteResult> CreateDeletionVectorTableAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);

        if (await TableExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Enabling deletion vectors on an existing table is out of scope in this build; deletion "
                + "vectors can only be enabled on a fresh table (first write).");
        }

        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(writeSchema, partitionColumns, batches, cancellationToken).ConfigureAwait(false);

        DeltaCommitResult commit = await _writer
            .CreateMappedTableAsync(
                writeSchema,
                partitionColumns,
                partitionColumns,
                DeletionVectorsFeature.EnabledConfiguration(),
                DeletionVectorsFeature.Protocol(),
                files,
                cancellationToken,
                // #497: the DV-create path is logical==physical (no column mapping), so gate the version-0
                // metaData schema on the real staged bytes' footer schema too.
                validatePhysicalWriteSchema: true)
            .ConfigureAwait(false);
        return new DeltaWriteResult(commit.Version, files.Count, rows);
    }

    // #525: for an EXISTING name-mode column-mapped table, resolve the PHYSICAL schema + partition columns
    // the staged Parquet must physically carry (so an append/overwrite writes col-<uuid> names + physical-
    // keyed partitionValues, IDENTICAL to the fresh-create path). The physical names are the table's EXISTING
    // per-field delta.columnMapping.physicalName — reused verbatim, NEVER re-minted. For a fresh path (create
    // door) or a `none`-mode table this is logical==physical, so the caller's write schema / partition columns
    // pass through unchanged (byte-for-byte identical staging to prior behavior). `id` mode is fail-closed at
    // snapshot load (#523). This is a STAGING concern only — the commit call still passes the LOGICAL write
    // schema to DeltaTableWriter, which re-derives the physical form for its own commit-time validation.
    private async Task<(StructType StagingSchema, IReadOnlyList<string> StagingPartitions)>
        ResolvePhysicalStagingAsync(
            StructType writeSchema, IReadOnlyList<string> partitionColumns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);

        if (await _log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            // Fresh path: the write creates the table (logical==physical for a plain create; a name-mode
            // create goes through CreateNameMappedTableAsync, not this facade path).
            return (writeSchema, partitionColumns);
        }

        Snapshot snapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
        ColumnMappingMode mode = ColumnMapping.ResolveMode(snapshot.Metadata.Configuration);
        if (mode != ColumnMappingMode.Name)
        {
            return (writeSchema, partitionColumns);
        }

        StructType physicalSchema = ColumnMapping.MapWriteSchemaToPhysical(writeSchema, snapshot.Schema, mode);
        IReadOnlyList<string> physicalPartitions =
            ColumnMapping.PhysicalPartitionColumns(snapshot.Schema, partitionColumns, mode);
        return (physicalSchema, physicalPartitions);
    }

    // Partition the batches, write one Parquet data file per non-empty partition (data columns only), and
    // return the staged-file descriptors plus the total row count.
    private async Task<(IReadOnlyList<StagedDataFile> Files, long Rows)> StageAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);

        IReadOnlyList<ColumnBatchPartitioner.PartitionGroup> groups =
            ColumnBatchPartitioner.Partition(writeSchema, partitionColumns, batches, cancellationToken);

        // TRACKED DEFERRAL (#442 unbounded materialization; columnar sink-contract #443): this stages the
        // whole write in memory — rows→ColumnBatch (upstream), then per-partition ColumnBatches here, then a
        // per-file MemoryStream + ToArray() below — a triple materialization with no spill/streaming bound.
        // A streaming/columnar sink contract that writes each partition file incrementally is #442/#443.
        var files = new List<StagedDataFile>(groups.Count);
        long totalRows = 0;
        long modificationTime = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        foreach (ColumnBatchPartitioner.PartitionGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = DataFilePath(partitionColumns, group.PartitionValues, _fileNameFactory());

            using var buffer = new MemoryStream();
            ParquetFileWriter.WriteResult result = await _parquetWriter
                .WriteWithStatisticsAsync(buffer, group.DataSchema, group.Batches, StatisticsPolicy.Default, cancellationToken)
                .ConfigureAwait(false);
            byte[] bytes = buffer.ToArray();

            // #497: derive the ACTUAL physical data schema from the file we just wrote by reading its footer
            // back — NOT the declared group.DataSchema. Recording the real bytes' schema makes the commit-time
            // enforcement (DeltaTableWriter.ValidateStagedWriteSchema) gate the true written columns/types
            // rather than trusting the caller's declaration, closing the trusted-declaration gap flagged on
            // #492/#190. (The footer parse decodes no data pages.) In column-mapping name mode this is the
            // PHYSICAL-named schema — correct, and unvalidated because the mapped create path deliberately
            // does not call ValidateStagedWriteSchema (deferred to #525).
            StructType writtenSchema;
            using (var footer = new MemoryStream(bytes, writable: false))
            {
                writtenSchema = await _reader.ReadDataSchemaAsync(footer, cancellationToken).ConfigureAwait(false);
            }

            await _backend.PutIfAbsentAsync(relativePath, bytes, cancellationToken).ConfigureAwait(false);

            files.Add(new StagedDataFile(
                relativePath,
                group.PartitionValues,
                Size: bytes.LongLength,
                ModificationTime: modificationTime,
                Stats: result.Statistics,
                DataSchema: writtenSchema));
            totalRows += result.RowCount;
        }

        return (files, totalRows);
    }

    // The table-relative data-file path: Hive-style `col=value/...` partition directories (a null value uses
    // the __HIVE_DEFAULT_PARTITION__ sentinel directory; the log still records a real null), then a unique
    // `part-<guid>.parquet`. Partition truth lives in the committed add.partitionValues, so the physical
    // directory encoding never affects read correctness.
    private static string DataFilePath(
        IReadOnlyList<string> partitionColumns,
        System.Collections.Immutable.ImmutableSortedDictionary<string, string?> partitionValues,
        string fileNameToken)
    {
        string fileName = "part-" + fileNameToken + ".parquet";
        if (partitionColumns.Count == 0)
        {
            return fileName;
        }

        var segments = new List<string>(partitionColumns.Count + 1);
        foreach (string column in partitionColumns)
        {
            partitionValues.TryGetValue(column, out string? value);
            string encoded = value is null
                ? DeltaWriteEncoding.HiveDefaultPartition
                : Uri.EscapeDataString(value);
            segments.Add(column + "=" + encoded);
        }

        segments.Add(fileName);
        return string.Join('/', segments);
    }

    private static PartitionOverwriteMode Map(DeltaPartitionOverwriteMode mode) => mode switch
    {
        DeltaPartitionOverwriteMode.Static => PartitionOverwriteMode.Static,
        DeltaPartitionOverwriteMode.Dynamic => PartitionOverwriteMode.Dynamic,
        _ => throw new ArgumentOutOfRangeException(
            nameof(mode), mode, "Unknown partition overwrite mode."),
    };

    /// <inheritdoc/>
    public void Dispose() => _backend.Dispose();
}
