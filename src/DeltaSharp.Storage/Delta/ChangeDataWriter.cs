using System.Globalization;
using System.Security.Cryptography;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Types;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Writes a Change Data Feed <c>cdc</c> change file (Delta protocol "Add CDC File") under
/// <c>_change_data/</c> for the merge-on-read <see cref="DeltaDelete"/> generation door (§2.5). A cdc file
/// stores the table's <b>data</b> columns — physical/column-mapped names, exactly like a data file, and
/// <b>without</b> partition columns (their values live only on the <see cref="AddCdcFileAction"/>, §2.4) —
/// plus one synthesized, non-null <c>_change_type</c> string column that names the row change
/// (<c>delete</c> here; <c>insert</c>/<c>update_*</c> are defined for the future UPDATE/MERGE door). The
/// <c>_commit_version</c>/<c>_commit_timestamp</c> metadata columns are NOT materialized — they are constant
/// per version and are stamped at read time (§2.4/§2.8), so persisting them per row would waste space.
///
/// <para><b>Reuses existing machinery (no new assembly).</b> The body is produced by the same
/// <see cref="ParquetFileWriter"/> that writes data files, over the same physical data schema the DELETE
/// read path resolves through <see cref="ColumnMappingProjection"/>; the <c>_change_type</c> column is
/// engine-synthesized and is <b>never</b> column-mapped. The file is published through the backend's
/// staged-write door (<see cref="IStorageBackend.OpenWriteAsync"/> +
/// <see cref="ICompletableWriteStream.CompleteAsync"/>) so a faulted write never leaves a torn object; on a
/// commit failure the published file is simply an orphan reclaimable by VACUUM (never a partial commit).</para>
///
/// <para><b>Determinism (golden-fixture reproducibility).</b> File names come from an injected token factory
/// (<see cref="DefaultFileNameFactory"/> in production: 128 crypto-random bits, hex-encoded — never the
/// banned <c>Guid.NewGuid</c>/<c>DateTime.UtcNow</c>/<c>System.Random</c>), exactly like the data-file /
/// OPTIMIZE naming seams, so a test injects a deterministic factory and the produced cdc file names are
/// byte-for-byte stable.</para>
/// </summary>
internal sealed class ChangeDataWriter
{
    /// <summary>The table-root-relative directory prefix every cdc file lives under (Delta protocol).</summary>
    public const string ChangeDataDirectory = "_change_data";

    /// <summary>The synthesized, engine-owned change-type column appended to every cdc file body. It is
    /// non-null and — being engine-synthesized — is NEVER column-mapped (§2.4).</summary>
    public const string ChangeTypeColumn = "_change_type";

    /// <summary>The <c>_change_type</c> value for a row that was inserted (implicit derivation only; no cdc
    /// file is written for inserts — recorded here so the vocabulary is defined in one place).</summary>
    public const string InsertChange = "insert";

    /// <summary>The <c>_change_type</c> value for a row deleted by a merge-on-read DELETE (§2.5).</summary>
    public const string DeleteChange = "delete";

    /// <summary>The <c>_change_type</c> value for an UPDATE/MERGE pre-image (the future #637 door).</summary>
    public const string UpdatePreImageChange = "update_preimage";

    /// <summary>The <c>_change_type</c> value for an UPDATE/MERGE post-image (the future #637 door).</summary>
    public const string UpdatePostImageChange = "update_postimage";

    private readonly IStorageBackend _backend;
    private readonly ParquetFileWriter _writer;

    /// <summary>Creates a cdc writer over <paramref name="backend"/> (rooted at the Delta table directory),
    /// using the shared <see cref="ParquetFileWriter"/> (or an injected one in tests).</summary>
    public ChangeDataWriter(IStorageBackend backend, ParquetFileWriter? writer = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _writer = writer ?? new ParquetFileWriter();
    }

    /// <summary>The production cdc-file name-token source: 128 bits from a cryptographic RNG, hex-encoded
    /// (never the banned <c>Guid.NewGuid</c>/<c>System.Random</c>), identical in spirit to the data-file /
    /// OPTIMIZE naming seams so two concurrent writers never collide on a cdc path while a deterministic
    /// factory can be injected in tests.</summary>
    internal static string DefaultFileNameFactory() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Validates that <paramref name="dataSchema"/> — the physical data schema (partition columns already
    /// excluded) — can be materialized into a cdc file body. Change-data generation reuses the scalar-only
    /// <see cref="ParquetFileWriter"/> and gathers the changed rows through a selection view, neither of
    /// which supports a nested (struct/array/map) column; rather than fail mid-operation with an opaque
    /// error (or, worse, publish an incomplete cdc set and violate delete-completeness, INV C2/C3), CDF
    /// generation fails <b>closed and early</b> for a table carrying a nested data column. Scalar columns
    /// (the overwhelming common case) are unaffected. Symmetric with the scalar-only OPTIMIZE writer.
    /// </summary>
    /// <exception cref="DeltaStorageException">A data column is a nested (struct/array/map) type.</exception>
    public static void EnsureWritableDataSchema(StructType dataSchema)
    {
        ArgumentNullException.ThrowIfNull(dataSchema);
        for (int i = 0; i < dataSchema.Count; i++)
        {
            StructField field = dataSchema[i];
            if (field.DataType is StructType or ArrayType or MapType)
            {
                throw DeltaStorageException.UnsupportedFeature(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Change Data Feed generation does not support the nested "
                        + $"({field.DataType.TypeName}) data column '{field.Name}': the DELETE fails closed "
                        + $"rather than write incomplete change data (which read-time precedence would make "
                        + $"silently lossy). Only scalar data columns are supported for cdc generation."));
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="changedRows"/> — one or more <b>selection-carrying</b> batches over the
    /// physical <paramref name="dataSchema"/>, each already narrowed to the rows that changed — to a single
    /// <c>_change_data/</c> Parquet file, appending a constant non-null <c>_change_type</c> =
    /// <paramref name="changeType"/> column. The file name is
    /// <c>_change_data/cdc-&lt;fileNameToken&gt;.parquet</c> (the token comes from the caller's deterministic
    /// naming seam). Returns the published path, its byte size, and the total row count.
    /// </summary>
    /// <param name="dataSchema">The physical data schema (partition columns excluded, physical/mapped names),
    /// identical to how a data file stores the same columns. Must be scalar-only
    /// (<see cref="EnsureWritableDataSchema"/>).</param>
    /// <param name="changedRows">The changed rows, as selection views over the file's physical batches — the
    /// SAME batches read while planning the deletion, so no second scan occurs (§4.3). Must be non-empty and
    /// carry at least one row (a file with zero changed rows writes NO cdc file — the caller's contract).</param>
    /// <param name="changeType">The non-null <c>_change_type</c> value stamped on every row (e.g.
    /// <see cref="DeleteChange"/>).</param>
    /// <param name="fileNameToken">The deterministic file-name token from the caller's naming seam.</param>
    /// <exception cref="ArgumentException"><paramref name="changedRows"/> is empty or carries zero rows.</exception>
    public async Task<ChangeDataFile> WriteAsync(
        StructType dataSchema,
        IReadOnlyList<ColumnBatch> changedRows,
        string changeType,
        string fileNameToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSchema);
        ArgumentNullException.ThrowIfNull(changedRows);
        ArgumentException.ThrowIfNullOrEmpty(changeType);
        ArgumentException.ThrowIfNullOrEmpty(fileNameToken);

        StructType cdcSchema = AppendChangeTypeColumn(dataSchema);
        var stamped = new List<ColumnBatch>(changedRows.Count);
        long rowCount = 0;
        foreach (ColumnBatch rows in changedRows)
        {
            ColumnBatch batch = StampChangeType(cdcSchema, dataSchema, rows, changeType);
            stamped.Add(batch);
            rowCount = checked(rowCount + batch.RowCount);
        }

        if (rowCount == 0)
        {
            // The caller must never ask for an empty cdc file (a file with zero newly-deleted rows emits
            // NO cdc action — the idempotent re-delete case). Fail rather than publish a rows-less file that
            // would carry an AddCdcFileAction for no change.
            throw new ArgumentException(
                "A cdc file must contain at least one changed row; a file with no newly-changed rows must "
                + "emit no cdc file at all.", nameof(changedRows));
        }

        string path = ChangeDataDirectory + "/cdc-" + fileNameToken + ".parquet";
        long size = await PublishAsync(path, cdcSchema, stamped, cancellationToken).ConfigureAwait(false);
        return new ChangeDataFile(path, size, rowCount);
    }

    // The cdc file schema: the physical data columns verbatim, then the engine-synthesized non-null
    // `_change_type` string column. `_change_type` is never column-mapped (no columnMapping metadata), so it
    // keeps its literal logical name in the Parquet footer while the data columns keep their physical names.
    private static StructType AppendChangeTypeColumn(StructType dataSchema)
    {
        var fields = new List<StructField>(dataSchema.Count + 1);
        fields.AddRange(dataSchema.Fields);
        fields.Add(new StructField(ChangeTypeColumn, DataTypes.StringType, nullable: false));
        return new StructType(fields);
    }

    // Materializes one output batch: each data column is the selection-gathered view of the changed rows
    // (zero-copy over the source batch's physical buffers), and the appended `_change_type` column is a
    // constant non-null string of the changeType, length-matched to the gathered rows. The result carries NO
    // selection of its own, so the ParquetFileWriter writes exactly these rows.
    private static ColumnBatch StampChangeType(
        StructType cdcSchema, StructType dataSchema, ColumnBatch rows, string changeType)
    {
        int rowCount = rows.LogicalRowCount;
        var columns = new ColumnVector[dataSchema.Count + 1];
        for (int c = 0; c < dataSchema.Count; c++)
        {
            // SelectedColumn resolves the batch's selection to a per-row gathered view (scalar columns only,
            // guaranteed by EnsureWritableDataSchema) so only the changed rows are surfaced.
            columns[c] = rows.SelectedColumn(c);
        }

        columns[dataSchema.Count] = DeltaReadEncoding.BuildConstantColumn(DataTypes.StringType, changeType, rowCount);
        return new ManagedColumnBatch(cdcSchema, columns, rowCount);
    }

    // Writes the batches to an in-memory Parquet buffer, then publishes them through the backend's staged
    // write door (destination visible only after CompleteAsync — a faulted write never leaves a torn cdc
    // object, §2.13.2). Returns the published byte size (recorded on AddCdcFileAction.Size).
    private async Task<long> PublishAsync(
        string path, StructType cdcSchema, IReadOnlyList<ColumnBatch> batches, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await _writer.WriteAsync(buffer, cdcSchema, batches, cancellationToken).ConfigureAwait(false);

        ReadOnlyMemory<byte> content = buffer.TryGetBuffer(out ArraySegment<byte> segment)
            ? segment.AsMemory()
            : buffer.ToArray();

        Stream target = await _backend.OpenWriteAsync(path, cancellationToken).ConfigureAwait(false);
        await using (target.ConfigureAwait(false))
        {
            await target.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await ((ICompletableWriteStream)target).CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return content.Length;
    }

    /// <summary>The result of writing one cdc file: its table-root-relative <see cref="Path"/>, the published
    /// byte <see cref="Size"/> (recorded on <see cref="AddCdcFileAction.Size"/>), and the total
    /// <see cref="RowCount"/> of change rows it carries.</summary>
    internal readonly record struct ChangeDataFile(string Path, long Size, long RowCount);
}
