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

    private DeltaWriteTarget(LocalFileSystemBackend backend, TimeProvider timeProvider, Func<string> fileNameFactory)
    {
        _backend = backend;
        _log = new DeltaLog(backend);
        _writer = new DeltaTableWriter(backend);
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

    // A collision-resistant data-file name from the sanctioned deterministic RNG (never the banned
    // Guid.NewGuid), so two concurrent writers never stage the same physical path (mirrors DeltaOptimize).
    private static string DefaultFileNameFactory() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>Whether a Delta table already exists at this path (any committed version). Used to honor
    /// Ignore/ErrorIfExists before the write query executes.</summary>
    public async Task<bool> TableExistsAsync(CancellationToken cancellationToken = default) =>
        await _log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Appends <paramref name="batches"/> to the table (creating it on first write).</summary>
    /// <param name="writeSchema">The full write schema (partition + data columns).</param>
    /// <param name="partitionColumns">The partition column names, in order (a subset of the schema).</param>
    /// <param name="batches">The full-schema batches to write.</param>
    /// <param name="cancellationToken">Cancels staging and the commit.</param>
    public async Task<DeltaWriteResult> AppendAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        CancellationToken cancellationToken = default)
    {
        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(writeSchema, partitionColumns, batches, cancellationToken).ConfigureAwait(false);
        DeltaCommitResult commit = await _writer
            .CreateOrAppendAsync(writeSchema, partitionColumns, files, cancellationToken)
            .ConfigureAwait(false);
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
    /// schema, so the committed metadata matches the real bytes.</para></summary>
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
        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(writeSchema, partitionColumns, batches, cancellationToken).ConfigureAwait(false);
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
