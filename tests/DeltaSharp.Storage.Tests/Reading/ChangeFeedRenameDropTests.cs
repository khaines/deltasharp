using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Tests.Delta.DeletionVectors;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// Curated Change Data Feed goldens for metadata-only column <b>RENAME</b> / <b>DROP</b> across a CDF history
/// (name-mode column mapping, §2.8 reconciliation) — the highest-value axis of the #661 CDF-oracle hardening.
/// Both operations are <c>dataChange=false</c> (ZERO change rows), yet they change how the WHOLE range's
/// columns surface: a CDF range read resolves EVERY version's files through the END-snapshot logical schema, so
/// after a rename the entire range surfaces the column under its END (renamed) name, and after a drop the
/// dropped column is absent from the whole range's output — while pre-rename/pre-drop Parquet is NEVER
/// rewritten (physical↔logical divergence; time travel still exposes the old shape).
/// </summary>
/// <remarks>
/// <para>These mirror <see cref="ChangeFeedGoldenTests"/> (curated history + explicit expected outcome), driving
/// the REAL production doors: <see cref="DeltaTableWriter.RenameColumnAsync"/> /
/// <see cref="DeltaTableWriter.DropColumnAsync"/> (via the harness thin wrappers) and the CDF read door
/// (<see cref="DeltaReadSource.LoadChangeFeedAsync"/> + <see cref="DeltaReadSource.ReadChangeBatchesAsync"/>).
/// The random state-machine model oracle keeps its FIXED <c>CdfRow</c> shape and is intentionally NOT wired for
/// rename/drop (that needs a schema-generalizing refactor); these curated goldens are the substitute.</para>
/// <para><b>Load-bearing.</b> The rename golden asserts the END output schema surfaces <c>amount</c> (not
/// <c>val</c>) AND the physical <c>col-&lt;uuid&gt;</c> is UNCHANGED across the rename — so a production
/// regression that rewrote data on rename, relabelled the physical name, or resolved the range under the START
/// schema would go RED. The drop golden asserts the dropped column is absent from the end schema/output yet a
/// pre-drop time-travel snapshot still exposes it + its data — so a regression that physically purged the
/// column (or leaked it into the post-drop feed) would go RED.</para>
/// </remarks>
[Collection(DeletionVectorFileTestCollection.Name)]
public sealed class ChangeFeedRenameDropTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    /// <summary>
    /// Rename mid-history, CDF read-through. A name-mapped CDF table appends rows (physical <c>val</c>), renames
    /// <c>val</c>→<c>amount</c> (metadata-only, ZERO change rows), then appends more rows carrying
    /// <c>amount</c>. The CDF range spanning the rename must (a) surface <c>amount</c> (not <c>val</c>) for the
    /// WHOLE range, (b) keep the physical name UNCHANGED, (c) read every pre-rename value through unchanged
    /// under <c>amount</c>, and (d) contribute no change rows at the rename version. A pre-rename snapshot still
    /// shows <c>val</c> (time travel).
    /// </summary>
    [Fact]
    public async Task Golden_RenameMidHistory_WholeRangeSurfacesRenamedColumn_ZeroChangeRows()
    {
        using CdfTable table = NewTable();
        await table.CreateEmptyAsync();                                             // v0 (name mode)
        await table.EnableCdfAsync();                                               // v1
        long preRename = await table.AppendAsync([Row(1, "east", 10), Row(2, "west", 20)]); // v2 (physical `val`)
        Assert.Equal(2L, preRename);

        string physicalBefore = await table.PhysicalNameOfAsync("val");
        Assert.StartsWith("col-", physicalBefore, StringComparison.Ordinal); // name mode: physical != logical

        long renameVersion = await table.RenameColumnAsync("val", "amount");        // v3 (metadata-only)
        Assert.Equal(3L, renameVersion);

        // The rename PRESERVED the physical column identity (physical↔logical divergence) — no data rewrite.
        string physicalAfter = await table.PhysicalNameOfAsync("amount");
        Assert.Equal(physicalBefore, physicalAfter);

        long postRename = await table.AppendUnderCurrentSchemaAsync(
            [(3, "east", 30), (4, "north", 40)]);                                  // v4 (carries `amount`)
        Assert.Equal(4L, postRename);

        (StructType outputSchema, List<Change> changes) =
            await ReadChangesAsync(table.Root, DeltaChangeFeedRange.FromVersion(2, 4));

        // (a) the WHOLE range's data column surfaces under the END logical name `amount`, never `val`.
        Assert.Equal(new[] { "id", "region", "amount" }, DataColumnNames(outputSchema));
        Assert.DoesNotContain("val", outputSchema.Select(f => f.Name));

        // (c) every pre-rename insert reads through UNCHANGED under `amount`; post-rename inserts join them.
        Assert.Equal(
            new (long, string, long?, string, long)[]
            {
                (1, "east", 10, ChangeDataWriter.InsertChange, 2), // pre-rename, physical `val` → `amount`
                (2, "west", 20, ChangeDataWriter.InsertChange, 2),
                (3, "east", 30, ChangeDataWriter.InsertChange, 4), // post-rename inserts
                (4, "north", 40, ChangeDataWriter.InsertChange, 4),
            },
            changes.OrderBy(c => c.Version).ThenBy(c => c.Id)
                .Select(c => (c.Id, c.Region, c.Data, c.ChangeType, c.Version)).ToArray());

        // (d) the rename version (v3) contributed NO change rows (dataChange=false).
        Assert.DoesNotContain(changes, c => c.Version == renameVersion);

        // Time travel: a snapshot at the pre-rename version (v2) STILL shows the original `val` (not `amount`).
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(table.Root);
        DeltaSnapshotInfo preRenameSnapshot = await source.LoadSnapshotAsync(preRename, null);
        Assert.Equal(new[] { "id", "region", "val" }, preRenameSnapshot.Schema.Select(f => f.Name).ToArray());
    }

    /// <summary>
    /// Drop mid-history. A name-mapped CDF table appends rows carrying a droppable non-partition column
    /// (<c>val</c>), drops it (metadata-only, ZERO change rows), then appends more rows. The CDF range must
    /// show <c>val</c> ABSENT from the end schema/output (other columns intact); a pre-drop time-travel
    /// snapshot still exposes <c>val</c> and its data (the physical column was never purged).
    /// </summary>
    [Fact]
    public async Task Golden_DropMidHistory_WholeRangeOmitsDroppedColumn_TimeTravelStillExposesIt()
    {
        using CdfTable table = NewTable();
        await table.CreateEmptyAsync();                                             // v0 (name mode)
        await table.EnableCdfAsync();                                               // v1
        long preDrop = await table.AppendAsync([Row(1, "east", 10), Row(2, "west", 20)]); // v2 (with `val`)
        Assert.Equal(2L, preDrop);

        long dropVersion = await table.DropColumnAsync("val");                       // v3 (metadata-only)
        Assert.Equal(3L, dropVersion);

        long postDrop = await table.AppendUnderCurrentSchemaAsync(
            [(3, "east", null), (4, "north", null)]);                              // v4 (no data column now)
        Assert.Equal(4L, postDrop);

        (StructType outputSchema, List<Change> changes) =
            await ReadChangesAsync(table.Root, DeltaChangeFeedRange.FromVersion(2, 4));

        // The dropped column is ABSENT from the END schema/output; id + region survive.
        Assert.Equal(new[] { "id", "region" }, DataColumnNames(outputSchema));
        Assert.DoesNotContain("val", outputSchema.Select(f => f.Name));

        // Every insert surfaces id + region only (Data is null: there is no data column post-drop).
        Assert.Equal(
            new (long, string, string, long)[]
            {
                (1, "east", ChangeDataWriter.InsertChange, 2),
                (2, "west", ChangeDataWriter.InsertChange, 2),
                (3, "east", ChangeDataWriter.InsertChange, 4),
                (4, "north", ChangeDataWriter.InsertChange, 4),
            },
            changes.OrderBy(c => c.Version).ThenBy(c => c.Id)
                .Select(c => (c.Id, c.Region, c.ChangeType, c.Version)).ToArray());
        Assert.All(changes, c => Assert.Null(c.Data));

        // The drop version (v3) contributed NO change rows (dataChange=false).
        Assert.DoesNotContain(changes, c => c.Version == dropVersion);

        // Time travel: a snapshot at the pre-drop version (v2) STILL exposes `val` and its data.
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(table.Root);
        DeltaSnapshotInfo preDropSnapshot = await source.LoadSnapshotAsync(preDrop, null);
        Assert.Equal(new[] { "id", "region", "val" }, preDropSnapshot.Schema.Select(f => f.Name).ToArray());
        int valIdx = preDropSnapshot.Schema.IndexOf("val");
        int idIdx = preDropSnapshot.Schema.IndexOf("id");
        var recovered = new List<(long Id, long? Val)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(preDropSnapshot.Version))
        {
            ColumnVector id = batch.SelectedColumn(idIdx);
            ColumnVector val = batch.SelectedColumn(valIdx);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                recovered.Add((id.GetValue<long>(r), val.IsNull(r) ? null : val.GetValue<long>(r)));
            }
        }

        Assert.Equal(
            new (long, long?)[] { (1, 10), (2, 20) },
            recovered.OrderBy(r => r.Id).ToArray());
    }

    // ------------------------------------------------------------------ helpers

    private readonly record struct Change(long Version, string ChangeType, long Id, string Region, long? Data);

    // Reads a CDF range through the production door and flattens it into (id, region, data) change rows,
    // locating the data columns by their LOGICAL name in the reconciled END output schema — so a rename/drop
    // is observed exactly as the reader surfaces it. Returns the reconciled output schema too, so the caller
    // can assert the physical→logical column set.
    private static async Task<(StructType Schema, List<Change> Changes)> ReadChangesAsync(
        string root, DeltaChangeFeedRange range)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(root);
        DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(range);
        StructType schema = info.Schema;
        int idIdx = schema.IndexOf("id");
        int regionIdx = schema.IndexOf("region");
        int dataIdx = FindDataColumn(schema);
        int changeTypeIdx = schema.IndexOf(ChangeDataWriter.ChangeTypeColumn);
        int versionIdx = schema.IndexOf(ChangeDataWriter.CommitVersionColumn);

        var changes = new List<Change>();
        await foreach (ColumnBatch batch in source.ReadChangeBatchesAsync(info))
        {
            ColumnVector id = batch.SelectedColumn(idIdx);
            ColumnVector region = batch.SelectedColumn(regionIdx);
            ColumnVector? data = dataIdx >= 0 ? batch.SelectedColumn(dataIdx) : null;
            ColumnVector changeType = batch.SelectedColumn(changeTypeIdx);
            ColumnVector version = batch.SelectedColumn(versionIdx);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                changes.Add(new Change(
                    version.GetValue<long>(r),
                    Encoding.UTF8.GetString(changeType.GetBytes(r)),
                    id.GetValue<long>(r),
                    Encoding.UTF8.GetString(region.GetBytes(r)),
                    data is not null && !data.IsNull(r) ? data.GetValue<long>(r) : null));
            }
        }

        return (schema, changes);
    }

    // The logical names of the DATA columns (everything before the three trailing engine metadata columns).
    private static string[] DataColumnNames(StructType outputSchema) =>
        outputSchema.Take(outputSchema.Count - 3).Select(f => f.Name).ToArray();

    // The single non-key, non-partition data column's index in a CDF output schema, or -1 when the table has
    // none (e.g. after `val` is dropped). Scans only the data columns (excludes the 3 trailing metadata cols).
    private static int FindDataColumn(StructType schema)
    {
        for (int i = 0; i < schema.Count - 3; i++)
        {
            string name = schema[i].Name;
            if (!string.Equals(name, "id", StringComparison.Ordinal)
                && !string.Equals(name, "region", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static CdfRow Row(long id, string region, long? val) => new(id, region, val);

    private CdfTable NewTable()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "ds-cdf-renamedrop-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return new CdfTable(root, ColumnMappingMode.Name, physicalNameSeed: "cdf-renamedrop");
    }
}
