using System.Globalization;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// Shared harness for the Change Data Feed <b>hardening</b> increment (increment 4, design §8.1 / §3.3):
/// the model-based state-machine oracle (<see cref="ChangeFeedModelReplayTests"/>), the CDF-folds-to-snapshot
/// differential (INV C6), the curated goldens (<see cref="ChangeFeedGoldenTests"/>) and the cdc read-path
/// fail-closed fuzz (<see cref="ChangeFeedCdcFuzzTests"/>) all drive a real CDF-enabled, deletion-vector-backed,
/// <b>partitioned</b> table through the production write door (append / overwrite / DV DELETE / OPTIMIZE) and
/// the production CDF read door (<see cref="DeltaReadSource.LoadChangeFeedAsync"/> +
/// <see cref="DeltaReadSource.ReadChangeBatchesAsync"/>), then classify the produced <see cref="ColumnBatch"/>es
/// with mechanical oracles. This file holds the reusable plumbing so each oracle expresses only its invariant.
/// </summary>
/// <remarks>
/// <para>The base table schema is <c>(id long non-null, region string [partition], val long nullable)</c> —
/// deliberately partitioned so every change row must surface its partition value (INV C9) on BOTH the explicit
/// (cdc) and implicit (add/remove) reconstruction paths, and so a single DELETE can straddle files (one file
/// fully removed, another retained with a new DV — INV C2 both branches). <c>id</c> is a globally-unique key
/// minted monotonically, so a change multiset keyed by <c>(version, changeType, id)</c> has no accidental
/// collisions.</para>
/// <para><b>Column mapping + schema evolution (#650).</b> The harness additionally creates the table in a chosen
/// column-mapping mode (<c>none</c>/<c>name</c>/<c>id</c>) and can EVOLVE the schema mid-history: add a
/// later-added nullable <c>amt</c> (int) or <c>extra</c> (long) column, and widen <c>amt</c> int→long. The two
/// optional evolving values <see cref="CdfRow.Amt"/>/<see cref="CdfRow.Extra"/> default to <c>null</c> so the
/// fixed-schema golden/fuzz oracles construct <c>CdfRow(id, region, val)</c> exactly as before; a row written
/// before a column was added carries <c>null</c> for it (schema-on-read null-fill at the reconciled end schema,
/// §2.8). A widened column's numeric value is unchanged (an <c>int</c> promotes to the same <c>long</c>), so the
/// value multiset is type-agnostic while the reconciled OUTPUT SCHEMA (asserted separately) proves the physical
/// col-&lt;uuid&gt; / field-id storage surfaced under its LOGICAL name and widened type.</para>
/// </remarks>
internal readonly record struct CdfRow(long Id, string Region, long? Val, long? Amt = null, long? Extra = null);

/// <summary>One expected change-feed row: the table data (<see cref="Id"/>/<see cref="Region"/>/<see cref="Val"/>
/// plus the optional evolving <see cref="Amt"/>/<see cref="Extra"/>) plus the synthesized change metadata
/// (<see cref="ChangeType"/>/<see cref="Version"/>). <see cref="TsMicros"/> is the observed
/// <c>_commit_timestamp</c> (epoch micros); the model leaves it 0 (it is asserted separately against the
/// per-version commit-file mtime, CDF-HP-05). <see cref="Amt"/>/<see cref="Extra"/> are <c>null</c> for a row
/// written before that column existed (or when the fixed-schema oracles never add it).</summary>
internal readonly record struct CdfChange(
    long Version, string ChangeType, long Id, string Region, long? Val, long TsMicros = 0, long? Amt = null, long? Extra = null)
{
    /// <summary>Projects away <see cref="TsMicros"/> so a model-built change (no timestamp) compares equal to a
    /// read-decoded change by data + change-type + version alone.</summary>
    public CdfChange WithoutTimestamp() => this with { TsMicros = 0 };
}

/// <summary>The decoded outcome of a CDF read: the flattened change rows, the ascending per-batch
/// <c>_commit_version</c> sequence (for the commit-order / INV C5 / INV C8 assertions), and the reconciled CDF
/// output schema (data columns under their LOGICAL names + the three metadata columns) the read surfaced — the
/// physical↔logical / type-widening fidelity witness for the #650 model oracle.</summary>
internal sealed record CdfReadResult(
    IReadOnlyList<CdfChange> Changes, IReadOnlyList<long> BatchVersions, StructType OutputSchema);

/// <summary>
/// A real CDF-enabled, deletion-vector-backed, partitioned Delta table over a temp-directory backend. Wraps
/// the production write/read doors so an oracle drives commits and reads without re-implementing plumbing.
/// Deterministic throughout: DV ids and cdc file names come from seeded sources (no <c>Guid.NewGuid</c>), and
/// commit timestamps are set through the <c>&lt;N&gt;.json</c> mtime seam (no wall-clock dependence).
/// </summary>
internal sealed class CdfTable : IDisposable
{
    // The base (v0) column names. `id`/`region`/`val` exist from creation; `amt`/`extra` are OPTIONAL columns a
    // #650 evolving history adds mid-stream (mergeSchema ADD COLUMN), and `amt` may later widen int→long.
    private const string IdColumn = "id";
    private const string RegionColumn = "region";
    private const string ValColumn = "val";
    private const string AmtColumn = "amt";
    private const string ExtraColumn = "extra";

    /// <summary>The LOGICAL name of the evolving int→long <c>amt</c> column — so the #650 model oracle builds its
    /// expected reconciled end schema against the SAME identifier the harness writes.</summary>
    public const string AmtColumnName = AmtColumn;

    /// <summary>The LOGICAL name of the evolving <c>extra</c> (long) column (see <see cref="AmtColumnName"/>).</summary>
    public const string ExtraColumnName = ExtraColumn;

    /// <summary>The base (v0) table (logical) data schema — partitioned on <c>region</c>. A #650 evolving history
    /// grows this via <see cref="AddAmtColumnAsync"/>/<see cref="AddExtraColumnAsync"/>/<see cref="WidenAmtColumnAsync"/>;
    /// the fixed-schema golden/fuzz oracles never evolve it, so it stays this shape for them.</summary>
    public static readonly StructType Schema = new(new[]
    {
        new StructField(IdColumn, DataTypes.LongType, nullable: false),
        new StructField(RegionColumn, DataTypes.StringType, nullable: true),
        new StructField(ValColumn, DataTypes.LongType, nullable: true),
    });

    private static readonly string[] PartitionColumns = { RegionColumn };

    private readonly string _root;
    private readonly ColumnMappingMode _mappingMode;
    private readonly string _physicalNameSeed;

    // The evolving optional columns (beyond the base id/region/val), in the ORDER they were added — Delta's
    // mergeSchema appends a new column LAST, so this list's order is the physical/logical column order. `amt`'s
    // Type mutates int→long in place on widen. Empty for the fixed-schema (golden/fuzz) oracles.
    private readonly List<EvolvingColumn> _addedColumns = [];
    private int _deleteCounter;

    /// <summary>Creates a harness over a fresh temp-directory table. The fixed-schema golden/fuzz oracles use
    /// this default (none mode, no evolution); the #650 model oracle passes a random <paramref name="mappingMode"/>
    /// and a per-history <paramref name="physicalNameSeed"/> so a name/id-mapped create assigns deterministic
    /// physical <c>col-&lt;uuid&gt;</c> names.</summary>
    public CdfTable(string root, ColumnMappingMode mappingMode = ColumnMappingMode.None, string physicalNameSeed = "cdf-scenario")
    {
        _root = root;
        _mappingMode = mappingMode;
        _physicalNameSeed = physicalNameSeed;
        Directory.CreateDirectory(root);
    }

    public string Root => _root;

    /// <summary>The column-mapping mode this table was created in (INV physical↔logical fidelity axis, #650).</summary>
    public ColumnMappingMode MappingMode => _mappingMode;

    /// <summary>The CURRENT logical table schema (base + every column added so far, with <c>amt</c>'s current
    /// int/long type) — the shape an append/overwrite writes and the model reconciles the latest snapshot to.</summary>
    public StructType CurrentSchema() => new(BuildFieldList());

    /// <summary>Whether <c>amt</c> has been added (so it may be widened / appended with a value).</summary>
    public bool HasAmt => _addedColumns.Exists(c => c.Name == AmtColumn);

    /// <summary>Whether <c>amt</c> exists AND is still <c>int</c> (a legal widen target).</summary>
    public bool AmtIsInt => _addedColumns.Find(c => c.Name == AmtColumn) is { } amt && amt.Type == DataTypes.IntegerType;

    /// <summary>Whether <c>extra</c> has been added.</summary>
    public bool HasExtra => _addedColumns.Exists(c => c.Name == ExtraColumn);

    public LocalFileSystemBackend Backend() => new(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    /// <summary>v0: creates an EMPTY deletion-vector-enabled table (no data files) so snapshot(0) is the empty
    /// multiset — which lets the folds-to-snapshot oracle (INV C6) start the CDF-readable range from the empty
    /// baseline. CDF is enabled separately (writer-feature enablement is a distinct commit).</summary>
    public async Task<long> CreateEmptyAsync(CancellationToken cancellationToken = default)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        DeltaWriteResult result = _mappingMode switch
        {
            ColumnMappingMode.Name => await target.CreateNameMappedDeletionVectorTableAsync(
                Schema, PartitionColumns, Array.Empty<ColumnBatch>(),
                new SeededPhysicalNameSource(_physicalNameSeed), cancellationToken),
            ColumnMappingMode.Id => await target.CreateIdMappedDeletionVectorTableAsync(
                Schema, PartitionColumns, Array.Empty<ColumnBatch>(),
                new SeededPhysicalNameSource(_physicalNameSeed), cancellationToken),
            _ => await target.CreateDeletionVectorTableAsync(
                Schema, PartitionColumns, Array.Empty<ColumnBatch>(), cancellationToken),
        };
        return result.Version;
    }

    /// <summary>v1: enables Change Data Feed (the <c>changeDataFeed</c> writer feature +
    /// <c>delta.enableChangeDataFeed</c> property). Metadata-only — contributes no change rows.</summary>
    public async Task<long> EnableCdfAsync(CancellationToken cancellationToken = default)
    {
        DeltaCommitResult result = await new DeltaTableWriter(Backend()).EnableChangeDataFeedAsync(cancellationToken);
        return result.Version;
    }

    /// <summary>Enables the <c>typeWidening</c> table feature (a metadata-only <c>protocol</c>+<c>metaData</c>
    /// upgrade — contributes NO change rows) so a later <see cref="WidenAmtColumnAsync"/> may widen <c>amt</c>
    /// int→long (§2.8). Used by the #650 evolving-history bootstrap; harmless if no widen ever occurs.</summary>
    public async Task<long> EnableTypeWideningAsync(CancellationToken cancellationToken = default)
    {
        DeltaCommitResult result = await new DeltaTableWriter(Backend()).EnableTypeWideningAsync(cancellationToken);
        return result.Version;
    }

    /// <summary>Appends rows (one data file per distinct region), written under the CURRENT schema (base + every
    /// column added so far). Every appended row is an <c>insert</c> change derived implicitly from the committed
    /// <c>add(dataChange=true)</c> actions (no cdc file).</summary>
    public async Task<long> AppendAsync(IReadOnlyList<CdfRow> rows, CancellationToken cancellationToken = default)
    {
        StructType schema = CurrentSchema();
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        DeltaWriteResult result = await target.AppendAsync(
            schema, PartitionColumns, new[] { BuildBatch(schema, rows) }, cancellationToken: cancellationToken);
        return result.Version;
    }

    /// <summary>Static (full-table) overwrite with rows under the CURRENT schema: removes every active file and
    /// adds the new rows. Derived at read time as <c>delete</c> (the removed files' live rows) + <c>insert</c>
    /// (the new rows) — no cdc file.</summary>
    public async Task<long> OverwriteAsync(IReadOnlyList<CdfRow> rows, CancellationToken cancellationToken = default)
    {
        StructType schema = CurrentSchema();
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        DeltaWriteResult result = await target.OverwriteAsync(
            schema, PartitionColumns, new[] { BuildBatch(schema, rows) }, DeltaPartitionOverwriteMode.Static,
            cancellationToken: cancellationToken);
        return result.Version;
    }

    /// <summary>ADD COLUMN <c>amt</c> (a later-added NULLABLE <c>int</c>) via a Spark <c>mergeSchema</c> append
    /// (§2.8). Rows written before this version read <c>amt</c> as <b>null</b> at the reconciled end schema; the
    /// appended rows carry their <c>int</c> <see cref="CdfRow.Amt"/> (an <c>insert</c> change each). The added
    /// column takes a fresh physical name (name mode) / field-id (id mode).</summary>
    public Task<long> AddAmtColumnAsync(IReadOnlyList<CdfRow> rows, CancellationToken cancellationToken = default) =>
        EvolveAsync(new EvolvingColumn(AmtColumn, DataTypes.IntegerType), rows, cancellationToken);

    /// <summary>ADD COLUMN <c>extra</c> (a later-added NULLABLE <c>long</c>) via a <c>mergeSchema</c> append
    /// (§2.8). Rows written before this version read <c>extra</c> as <b>null</b> at the reconciled end schema.</summary>
    public Task<long> AddExtraColumnAsync(IReadOnlyList<CdfRow> rows, CancellationToken cancellationToken = default) =>
        EvolveAsync(new EvolvingColumn(ExtraColumn, DataTypes.LongType), rows, cancellationToken);

    /// <summary>WIDEN <c>amt</c> int→long via a <c>mergeSchema</c> append writing a <c>long</c> <c>amt</c> column
    /// (§2.8 type widening). Earlier narrow <c>int</c> values promote to <c>long</c> at the reconciled end schema;
    /// the appended rows may carry a value beyond <c>int.MaxValue</c> (a genuine long). Requires <c>amt</c> to
    /// exist as <c>int</c> and type widening to be enabled.</summary>
    public Task<long> WidenAmtColumnAsync(IReadOnlyList<CdfRow> rows, CancellationToken cancellationToken = default)
    {
        EvolvingColumn amt = _addedColumns.Find(c => c.Name == AmtColumn)
            ?? throw new InvalidOperationException("amt must be added (as int) before it can be widened to long.");
        // The widened schema is the current shape with amt retyped long, in place (order preserved).
        StructType target = new(BuildFieldList(overrideAmtType: DataTypes.LongType));
        return CommitEvolvingAppendAsync(target, rows, () => amt.Type = DataTypes.LongType, cancellationToken);
    }

    // Adds a new nullable column at the END of the schema (mergeSchema ADD COLUMN) and commits an append of the
    // target-schema rows. The column is recorded (so CurrentSchema grows) only AFTER the commit succeeds.
    private Task<long> EvolveAsync(EvolvingColumn column, IReadOnlyList<CdfRow> rows, CancellationToken cancellationToken)
    {
        var targetFields = new List<StructField>(BuildFieldList()) { new(column.Name, column.Type, nullable: true) };
        return CommitEvolvingAppendAsync(new StructType(targetFields), rows, () => _addedColumns.Add(column), cancellationToken);
    }

    private async Task<long> CommitEvolvingAppendAsync(
        StructType targetSchema, IReadOnlyList<CdfRow> rows, Action onCommitted, CancellationToken cancellationToken)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        DeltaWriteResult result = await target.AppendAsync(
            targetSchema, PartitionColumns, new[] { BuildBatch(targetSchema, rows) },
            mergeSchema: true, cancellationToken: cancellationToken);
        onCommitted();
        return result.Version;
    }

    /// <summary>Merge-on-read DELETE of every row whose <c>id</c> is in <paramref name="ids"/>. Materializes a
    /// cdc file per affected data file (explicit path); the returned <see cref="DeleteResult"/> reports whether
    /// each file was partially (new DV) or fully (bare remove) deleted. A no-op (no id matched) returns a null
    /// committed version.</summary>
    public async Task<DeleteResult> DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default)
    {
        var idSet = new HashSet<long>(ids);
        string seed = string.Create(CultureInfo.InvariantCulture, $"cdf-model-del{_deleteCounter++}");
        LocalFileSystemBackend backend = Backend();
        var delete = new DeltaDelete(
            backend,
            new DeltaLog(backend),
            idSource: new SeededDeletionVectorIdSource(seed),
            cdcFileNameFactory: SequentialCdcTokens(seed + "-"));
        return await delete.DeleteAsync(
            DeltaDeletePredicate.FromRowPredicate(
                (batch, row) => idSet.Contains(batch.SelectedColumn(0).GetValue<long>(row))),
            cancellationToken);
    }

    /// <summary>OPTIMIZE (compaction) — <c>dataChange=false</c>, so it contributes ZERO change rows (INV C4).
    /// A no-op (fewer than two compactable non-DV files in a partition) returns a null committed version.</summary>
    public async Task<OptimizeResult> OptimizeAsync(CancellationToken cancellationToken = default) =>
        await new DeltaOptimize(Backend()).OptimizeAsync(cancellationToken: cancellationToken);

    /// <summary>Resolves + reads a CDF range through the production door and decodes it, asserting per batch:
    /// exactly one <c>_commit_version</c> (INV C8), non-null + valid-domain <c>_change_type</c>, non-null
    /// metadata, and ascending commit order across batches (INV C5 / AC2). The decoded result carries the
    /// reconciled OUTPUT SCHEMA (<see cref="CdfReadResult.OutputSchema"/>) so the #650 oracle can assert
    /// physical↔logical / type-widening fidelity against the model's expected end schema.</summary>
    public async Task<CdfReadResult> ReadRangeAsync(DeltaChangeFeedRange range, CancellationToken cancellationToken = default)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(range, cancellationToken);
        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in source.ReadChangeBatchesAsync(info, cancellationToken))
        {
            batches.Add(batch);
        }

        return DecodeChanges(batches, info.Schema);
    }

    /// <summary>A normal (non-CDF) snapshot read of <paramref name="version"/> — the differential baseline for
    /// the folds-to-snapshot oracle (INV C6), independent of the change-feed read path. Decodes each column by
    /// its LOGICAL name from the batch schema, so it reconstructs the evolving <c>amt</c>/<c>extra</c> columns
    /// (null-filled for files written before they were added) under any column-mapping mode.</summary>
    public async Task<IReadOnlyList<CdfRow>> ReadSnapshotAsync(long version, CancellationToken cancellationToken = default)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(version, null, cancellationToken);
        var rows = new List<CdfRow>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnLocator cols = ColumnLocator.ForData(batch.Schema, dataColumnCount: batch.Schema.Count);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add(cols.ReadRow(batch, r));
            }
        }

        return rows;
    }

    /// <summary>The latest committed version of the table.</summary>
    public async Task<long> LatestVersionAsync(CancellationToken cancellationToken = default) =>
        (await new DeltaLog(Backend()).LoadSnapshotAsync(version: null, cancellationToken)).Version;

    /// <summary>Every <c>*.parquet</c> under <c>_change_data/</c>, as table-root-relative '/'-separated paths
    /// (empty when no DELETE has materialized a cdc file yet). Used by the fuzz to locate a cdc file to mutate
    /// and by the goldens to assert the implicit paths write none.</summary>
    public IReadOnlyList<string> CdcFilePaths()
    {
        string dir = Path.Combine(_root, ChangeDataWriter.ChangeDataDirectory);
        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(dir, "*.parquet", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The absolute on-disk path of a table-root-relative file (a cdc/data file).</summary>
    public string AbsolutePath(string relativePath) =>
        Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Stamps DETERMINISTIC, strictly-increasing mtimes on every <c>&lt;N&gt;.json</c> in
    /// <c>[0, latestVersion]</c> — the exact seam <see cref="DeltaLog"/> reads commit timestamps from. Strictly
    /// increasing ⇒ the effective monotonic timeline equals the raw mtimes, so the expected
    /// <c>_commit_timestamp</c> for version <c>v</c> is <see cref="ExpectedCommitMicros"/>.</summary>
    public void SetCommitMtimes(DateTimeOffset baseTime, TimeSpan step, long latestVersion)
    {
        for (long v = 0; v <= latestVersion; v++)
        {
            string path = Path.Combine(
                _root, "_delta_log", v.ToString("D20", CultureInfo.InvariantCulture) + ".json");
            if (File.Exists(path))
            {
                File.SetLastWriteTimeUtc(path, (baseTime + step * v).UtcDateTime);
            }
        }
    }

    /// <summary>The <c>_commit_timestamp</c> (epoch micros) the reader stamps for version <paramref name="version"/>
    /// after <see cref="SetCommitMtimes"/> — the millis lane × 1000 (design §2.8; CDF-HP-05).</summary>
    public static long ExpectedCommitMicros(DateTimeOffset baseTime, TimeSpan step, long version) =>
        (baseTime + step * version).ToUnixTimeMilliseconds() * 1000L;

    // ------------------------------------------------------------------ decode + batch-build helpers

    /// <summary>Decodes CDF batches into flat change rows AND asserts, per batch: exactly ONE
    /// <c>_commit_version</c> (INV C8), non-null + valid-domain <c>_change_type</c>, non-null metadata, and a
    /// reconstructed (never null-dropped) partition column (INV C9). Across batches it asserts ascending commit
    /// order (INV C5 / AC2). Data columns are located by their LOGICAL name in the reconciled
    /// <paramref name="outputSchema"/> (the physical col-&lt;uuid&gt; / field-id storage relabelled), so a
    /// mislabel would drop the column here and the value multiset would diverge from the model (#650).</summary>
    private static CdfReadResult DecodeChanges(IReadOnlyList<ColumnBatch> batches, StructType outputSchema)
    {
        int dataColumnCount = outputSchema.Count - 3;
        Assert.True(dataColumnCount >= Schema.Count, "CDF output must carry at least the base data columns");

        // The three engine-synthesized metadata columns are always the LAST three, in order (design §2.4).
        Assert.Equal(ChangeDataWriter.ChangeTypeColumn, outputSchema[dataColumnCount].Name);
        Assert.Equal(ChangeDataWriter.CommitVersionColumn, outputSchema[dataColumnCount + 1].Name);
        Assert.Equal(ChangeDataWriter.CommitTimestampColumn, outputSchema[dataColumnCount + 2].Name);
        ColumnLocator cols = ColumnLocator.ForData(outputSchema, dataColumnCount);

        var changes = new List<CdfChange>();
        var batchVersions = new List<long>();
        long previousBatchVersion = long.MinValue;
        foreach (ColumnBatch batch in batches)
        {
            Assert.Equal(outputSchema.Count, batch.ColumnCount);
            int n = batch.ColumnCount;
            ColumnVector changeType = batch.SelectedColumn(n - 3);
            ColumnVector version = batch.SelectedColumn(n - 2);
            ColumnVector timestamp = batch.SelectedColumn(n - 1);

            // INV C8 totality: every yielded batch carries EXACTLY ONE _commit_version — never zero. A version
            // with no changes (OPTIMIZE / ENABLE CDF / enable typeWidening) yields NO batch, not an empty one, so
            // this holds for the production reader. Forbidding an empty batch closes the blind spot where an
            // out-of-order ALL-EMPTY batch would be invisible to the ascending-order check below.
            Assert.True(batch.LogicalRowCount > 0, "CDF read must not yield an empty batch (INV C8)");

            long? single = null;
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                Assert.False(changeType.IsNull(r), "_change_type must never be null");
                Assert.False(version.IsNull(r), "_commit_version must never be null");
                Assert.False(timestamp.IsNull(r), "_commit_timestamp must never be null");
                string type = Utf8(changeType, r);
                Assert.True(
                    ChangeDataWriter.ChangeTypeDomain.Contains(type),
                    "_change_type must be a valid change-type token");

                long v = version.GetValue<long>(r);
                if (single is null)
                {
                    single = v;
                }
                else
                {
                    Assert.Equal(single.Value, v); // INV C8: exactly one _commit_version per batch
                }

                changes.Add(cols.ReadChange(batch, r, v, type, timestamp.GetValue<long>(r)));
            }

            long batchVersion = single!.Value;
            Assert.True(
                batchVersion >= previousBatchVersion,
                $"batches must arrive in ascending commit order (saw {batchVersion} after {previousBatchVersion})");
            previousBatchVersion = batchVersion;
            batchVersions.Add(batchVersion);
        }

        return new CdfReadResult(changes, batchVersions, outputSchema);
    }

    // Builds a batch whose columns match `schema` (base id/region/val + any evolving amt/extra), filling each
    // cell from the corresponding CdfRow field. `amt` is written as int or long per the schema field's type.
    private static ColumnBatch BuildBatch(StructType schema, IReadOnlyList<CdfRow> rows)
    {
        var vectors = new ColumnVector[schema.Count];
        for (int c = 0; c < schema.Count; c++)
        {
            StructField field = schema[c];
            MutableColumnVector vector = ColumnVectors.Create(field.DataType, rows.Count);
            foreach (CdfRow row in rows)
            {
                AppendCell(vector, field, row);
            }

            vectors[c] = vector;
        }

        return new ManagedColumnBatch(schema, vectors, rows.Count);
    }

    private static void AppendCell(MutableColumnVector vector, StructField field, CdfRow row)
    {
        switch (field.Name)
        {
            case IdColumn:
                vector.AppendValue(row.Id);
                break;
            case RegionColumn:
                vector.AppendBytes(Encoding.UTF8.GetBytes(row.Region));
                break;
            case ValColumn:
                AppendLongOrNull(vector, row.Val);
                break;
            case AmtColumn:
                AppendAmt(vector, field, row.Amt);
                break;
            case ExtraColumn:
                AppendLongOrNull(vector, row.Extra);
                break;
            default:
                throw new InvalidOperationException($"Unexpected column '{field.Name}' in CdfTable batch build.");
        }
    }

    private static void AppendLongOrNull(MutableColumnVector vector, long? value)
    {
        if (value is null)
        {
            vector.AppendNull();
        }
        else
        {
            vector.AppendValue(value.Value);
        }
    }

    // `amt` is stored int (before widening) or long (after) — write the value into whichever the current schema
    // field declares. `checked` fails fast if the generator ever handed an out-of-int-range value to an int amt.
    private static void AppendAmt(MutableColumnVector vector, StructField field, long? amt)
    {
        if (amt is null)
        {
            vector.AppendNull();
        }
        else if (field.DataType == DataTypes.IntegerType)
        {
            vector.AppendValue(checked((int)amt.Value));
        }
        else
        {
            vector.AppendValue(amt.Value);
        }
    }

    // The base fields + every evolving column (in add order), optionally retyping `amt` (used to compute the
    // widened target schema before the widen is recorded).
    private List<StructField> BuildFieldList(DataType? overrideAmtType = null)
    {
        var fields = new List<StructField>(Schema.Count + _addedColumns.Count);
        for (int i = 0; i < Schema.Count; i++)
        {
            fields.Add(Schema[i]);
        }

        foreach (EvolvingColumn column in _addedColumns)
        {
            DataType type = overrideAmtType is not null && column.Name == AmtColumn ? overrideAmtType : column.Type;
            fields.Add(new StructField(column.Name, type, nullable: true));
        }

        return fields;
    }

    /// <summary>A deterministic cdc-file-name factory (a monotonic <c>prefix + NNNN</c> token stream) — the
    /// injected seam that keeps cdc file names reproducible (no <c>Guid.NewGuid</c>) and collision-free across
    /// the many DELETEs in a generated history.</summary>
    private static Func<string> SequentialCdcTokens(string prefix)
    {
        int n = 0;
        return () => prefix + (n++).ToString("D4", CultureInfo.InvariantCulture);
    }

    private static string Utf8(ColumnVector vector, int row) => Encoding.UTF8.GetString(vector.GetBytes(row));

    // A mutable descriptor for an optional evolving column (amt/extra). `amt`'s Type mutates int→long on widen.
    private sealed class EvolvingColumn(string name, DataType type)
    {
        public string Name { get; } = name;

        public DataType Type { get; set; } = type;
    }

    /// <summary>Locates the base + evolving data columns by their LOGICAL name in a decoded batch/output schema,
    /// so decode is column-mapping-mode-agnostic (name/id/none all surface the same logical names) AND
    /// schema-evolution-aware (<c>amt</c>/<c>extra</c> present only once added; <c>amt</c> read as int or long
    /// per its reconciled type). A physical-vs-logical mislabel would leave <c>id</c>/<c>region</c> unlocated and
    /// fail closed here (#650).</summary>
    private sealed class ColumnLocator
    {
        private readonly int _id;
        private readonly int _region;
        private readonly int _val;
        private readonly int _amt;
        private readonly bool _amtIsLong;
        private readonly int _extra;

        private ColumnLocator(int id, int region, int val, int amt, bool amtIsLong, int extra)
        {
            _id = id;
            _region = region;
            _val = val;
            _amt = amt;
            _amtIsLong = amtIsLong;
            _extra = extra;
        }

        public static ColumnLocator ForData(StructType schema, int dataColumnCount)
        {
            int id = -1, region = -1, val = -1, amt = -1, extra = -1;
            bool amtIsLong = false;
            for (int i = 0; i < dataColumnCount; i++)
            {
                StructField field = schema[i];
                switch (field.Name)
                {
                    case IdColumn: id = i; break;
                    case RegionColumn: region = i; break;
                    case ValColumn: val = i; break;
                    case AmtColumn: amt = i; amtIsLong = field.DataType == DataTypes.LongType; break;
                    case ExtraColumn: extra = i; break;
                }
            }

            Assert.True(id >= 0, "output must surface the LOGICAL 'id' column (physical→logical relabel)");
            Assert.True(region >= 0, "output must surface the LOGICAL 'region' partition column (physical→logical relabel)");
            return new ColumnLocator(id, region, val, amt, amtIsLong, extra);
        }

        public CdfRow ReadRow(ColumnBatch batch, int r)
        {
            ColumnVector region = batch.SelectedColumn(_region);
            Assert.False(region.IsNull(r), "partition column must be reconstructed (never null-dropped)");
            return new CdfRow(
                batch.SelectedColumn(_id).GetValue<long>(r),
                Utf8(region, r),
                ReadLong(batch, _val, r),
                ReadAmt(batch, r),
                ReadLong(batch, _extra, r));
        }

        public CdfChange ReadChange(ColumnBatch batch, int r, long version, string changeType, long tsMicros)
        {
            CdfRow row = ReadRow(batch, r);
            return new CdfChange(version, changeType, row.Id, row.Region, row.Val, tsMicros, row.Amt, row.Extra);
        }

        private static long? ReadLong(ColumnBatch batch, int position, int r)
        {
            if (position < 0)
            {
                return null;
            }

            ColumnVector column = batch.SelectedColumn(position);
            return column.IsNull(r) ? null : column.GetValue<long>(r);
        }

        // `amt` promotes int→long transparently: read whichever physical width the reconciled schema declares
        // and surface it as a long, so the value multiset is width-agnostic (the widen TYPE is asserted via the
        // output schema separately).
        private long? ReadAmt(ColumnBatch batch, int r)
        {
            if (_amt < 0)
            {
                return null;
            }

            ColumnVector column = batch.SelectedColumn(_amt);
            if (column.IsNull(r))
            {
                return null;
            }

            return _amtIsLong ? column.GetValue<long>(r) : column.GetValue<int>(r);
        }
    }
}
