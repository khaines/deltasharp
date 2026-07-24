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
/// The table schema is <c>(id long non-null, region string [partition], val long nullable)</c> — deliberately
/// partitioned so every change row must surface its partition value (INV C9) on BOTH the explicit (cdc) and
/// implicit (add/remove) reconstruction paths, and so a single DELETE can straddle files (one file fully
/// removed, another retained with a new DV — INV C2 both branches). <c>id</c> is a globally-unique key minted
/// monotonically, so a change multiset keyed by <c>(version, changeType, id)</c> has no accidental collisions.
/// </remarks>
internal readonly record struct CdfRow(long Id, string Region, long? Val);

/// <summary>One expected change-feed row: the table data (<see cref="Id"/>/<see cref="Region"/>/<see cref="Val"/>)
/// plus the synthesized change metadata (<see cref="ChangeType"/>/<see cref="Version"/>). <see cref="TsMicros"/>
/// is the observed <c>_commit_timestamp</c> (epoch micros); the model leaves it 0 (it is asserted separately
/// against the per-version commit-file mtime, CDF-HP-05).</summary>
internal readonly record struct CdfChange(long Version, string ChangeType, long Id, string Region, long? Val, long TsMicros = 0)
{
    /// <summary>Projects away <see cref="TsMicros"/> so a model-built change (no timestamp) compares equal to a
    /// read-decoded change by data + change-type + version alone.</summary>
    public CdfChange WithoutTimestamp() => this with { TsMicros = 0 };
}

/// <summary>The decoded outcome of a CDF read: the flattened change rows plus the ascending per-batch
/// <c>_commit_version</c> sequence (for the commit-order / INV C5 / INV C8 assertions).</summary>
internal sealed record CdfReadResult(IReadOnlyList<CdfChange> Changes, IReadOnlyList<long> BatchVersions);

/// <summary>
/// A real CDF-enabled, deletion-vector-backed, partitioned Delta table over a temp-directory backend. Wraps
/// the production write/read doors so an oracle drives commits and reads without re-implementing plumbing.
/// Deterministic throughout: DV ids and cdc file names come from seeded sources (no <c>Guid.NewGuid</c>), and
/// commit timestamps are set through the <c>&lt;N&gt;.json</c> mtime seam (no wall-clock dependence).
/// </summary>
internal sealed class CdfTable : IDisposable
{
    /// <summary>The table (logical) data schema — partitioned on <c>region</c>.</summary>
    public static readonly StructType Schema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("region", DataTypes.StringType, nullable: true),
        new StructField("val", DataTypes.LongType, nullable: true),
    });

    private static readonly string[] PartitionColumns = { "region" };

    private readonly string _root;
    private int _deleteCounter;

    public CdfTable(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public string Root => _root;

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
        DeltaWriteResult result = await target.CreateDeletionVectorTableAsync(
            Schema, PartitionColumns, Array.Empty<ColumnBatch>(), cancellationToken);
        return result.Version;
    }

    /// <summary>v1: enables Change Data Feed (the <c>changeDataFeed</c> writer feature +
    /// <c>delta.enableChangeDataFeed</c> property). Metadata-only — contributes no change rows.</summary>
    public async Task<long> EnableCdfAsync(CancellationToken cancellationToken = default)
    {
        DeltaCommitResult result = await new DeltaTableWriter(Backend()).EnableChangeDataFeedAsync(cancellationToken);
        return result.Version;
    }

    /// <summary>Appends rows (one data file per distinct region). Every appended row is an <c>insert</c> change
    /// derived implicitly from the committed <c>add(dataChange=true)</c> actions (no cdc file).</summary>
    public async Task<long> AppendAsync(IReadOnlyList<CdfRow> rows, CancellationToken cancellationToken = default)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        DeltaWriteResult result = await target.AppendAsync(
            Schema, PartitionColumns, new[] { BuildBatch(rows) }, cancellationToken: cancellationToken);
        return result.Version;
    }

    /// <summary>Static (full-table) overwrite: removes every active file and adds the new rows. Derived at read
    /// time as <c>delete</c> (the removed files' live rows) + <c>insert</c> (the new rows) — no cdc file.</summary>
    public async Task<long> OverwriteAsync(IReadOnlyList<CdfRow> rows, CancellationToken cancellationToken = default)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        DeltaWriteResult result = await target.OverwriteAsync(
            Schema, PartitionColumns, new[] { BuildBatch(rows) }, DeltaPartitionOverwriteMode.Static,
            cancellationToken: cancellationToken);
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
    /// metadata, and ascending commit order across batches (INV C5 / AC2).</summary>
    public async Task<CdfReadResult> ReadRangeAsync(DeltaChangeFeedRange range, CancellationToken cancellationToken = default)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(range, cancellationToken);
        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in source.ReadChangeBatchesAsync(info, cancellationToken))
        {
            batches.Add(batch);
        }

        return DecodeChanges(batches);
    }

    /// <summary>A normal (non-CDF) snapshot read of <paramref name="version"/> — the differential baseline for
    /// the folds-to-snapshot oracle (INV C6), independent of the change-feed read path.</summary>
    public async Task<IReadOnlyList<CdfRow>> ReadSnapshotAsync(long version, CancellationToken cancellationToken = default)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(version, null, cancellationToken);
        var rows = new List<CdfRow>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector region = batch.SelectedColumn(1);
            ColumnVector val = batch.SelectedColumn(2);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add(new CdfRow(
                    id.GetValue<long>(r),
                    Utf8(region, r),
                    val.IsNull(r) ? null : val.GetValue<long>(r)));
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
    /// order (INV C5 / AC2).</summary>
    private static CdfReadResult DecodeChanges(IReadOnlyList<ColumnBatch> batches)
    {
        var changes = new List<CdfChange>();
        var batchVersions = new List<long>();
        long previousBatchVersion = long.MinValue;
        foreach (ColumnBatch batch in batches)
        {
            Assert.Equal(Schema.Count + 3, batch.ColumnCount);
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector region = batch.SelectedColumn(1);
            ColumnVector val = batch.SelectedColumn(2);
            int n = batch.ColumnCount;
            ColumnVector changeType = batch.SelectedColumn(n - 3);
            ColumnVector version = batch.SelectedColumn(n - 2);
            ColumnVector timestamp = batch.SelectedColumn(n - 1);

            // INV C8 totality: every yielded batch carries EXACTLY ONE _commit_version — never zero. A version
            // with no changes (OPTIMIZE / ENABLE CDF) yields NO batch, not an empty one, so this holds for the
            // production reader. Forbidding an empty batch closes the blind spot where an out-of-order ALL-EMPTY
            // batch would be invisible to the ascending-order check below (its version is unreadable from rows).
            Assert.True(batch.LogicalRowCount > 0, "CDF read must not yield an empty batch (INV C8)");

            long? single = null;
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                Assert.False(changeType.IsNull(r), "_change_type must never be null");
                Assert.False(version.IsNull(r), "_commit_version must never be null");
                Assert.False(timestamp.IsNull(r), "_commit_timestamp must never be null");
                Assert.False(region.IsNull(r), "partition column must be reconstructed (never null-dropped)");
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

                changes.Add(new CdfChange(
                    v, type, id.GetValue<long>(r), Utf8(region, r),
                    val.IsNull(r) ? null : val.GetValue<long>(r), timestamp.GetValue<long>(r)));
            }

            long batchVersion = single!.Value;
            Assert.True(
                batchVersion >= previousBatchVersion,
                $"batches must arrive in ascending commit order (saw {batchVersion} after {previousBatchVersion})");
            previousBatchVersion = batchVersion;
            batchVersions.Add(batchVersion);
        }

        return new CdfReadResult(changes, batchVersions);
    }

    private static ColumnBatch BuildBatch(IReadOnlyList<CdfRow> rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Count);
        MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, rows.Count);
        MutableColumnVector val = ColumnVectors.Create(DataTypes.LongType, rows.Count);
        foreach (CdfRow row in rows)
        {
            id.AppendValue(row.Id);
            region.AppendBytes(Encoding.UTF8.GetBytes(row.Region));
            if (row.Val is null)
            {
                val.AppendNull();
            }
            else
            {
                val.AppendValue(row.Val.Value);
            }
        }

        return new ManagedColumnBatch(Schema, new ColumnVector[] { id, region, val }, rows.Count);
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
}
