using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta.DeletionVectors;

/// <summary>
/// WRITE-path schema-on-read for the merge-on-read <see cref="DeltaDelete"/> re-scan (#645). A DELETE reads
/// each active data file against the CURRENT (reconciled) table schema
/// (<c>ColumnMappingProjection.BuildDataSchema(readSnapshot.Schema, …)</c>). If the table schema EVOLVED
/// (<c>ADD COLUMN</c>) after a data file was written under an OLDER, NARROWER schema — and that file is still
/// ACTIVE because a prior partial deletion-vector delete retained it (merge-on-read never rewrites the file
/// body) — the file is physically MISSING the later-added nullable column. The DELETE re-scan must NULL-FILL
/// that absent column (schema-on-read), exactly like the read/CDF/optimize paths
/// (<see cref="DeltaReadSource"/> / <c>ChangeFeedReader</c> / <c>DeltaOptimize</c> all read with
/// <c>nullFillMissingColumns:true</c>, #497/#530), rather than failing closed on
/// <c>StorageErrorKind.ColumnNotPresentInFile</c>.
///
/// <para>Pre-#645 the DELETE re-scan alone used <c>nullFillMissingColumns:false</c> — a writer-path asymmetry
/// (surfaced during PR #643's CDF council). Reverting the one-line fix
/// (<c>nullFillMissingColumns:true → false</c> in <see cref="DeltaDelete"/>) turns the single test below RED
/// with <c>DeltaStorageException: Requested column 'extra' is not present in the Parquet file schema.</c> —
/// the exact bug — so the test is load-bearing on the fix.</para>
///
/// <para>Isolated in the shared non-parallel filesystem collection (drives a real temp-directory backend).</para>
/// </summary>
[Collection(DeletionVectorFileTestCollection.Name)]
public sealed class DeltaDeleteSchemaEvolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dv-schemaevo-" + Guid.NewGuid().ToString("N"));

    // Schema A (NARROW): id (non-null key) + name (nullable string). The file f is written under this shape.
    private static readonly StructType SchemaA = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    // Schema B = A + a later-added NULLABLE `extra` column (the ADD COLUMN outcome). Rows written under A
    // (the still-active narrow file f) must null-fill `extra` at re-scan time.
    private static readonly StructType SchemaB = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
        new StructField("extra", DataTypes.LongType, nullable: true),
    });

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task Delete_AfterAddColumn_ReScansDvRetainedNarrowFile_NullFillsMissingColumn_Issue645()
    {
        // 1) Create a deletion-vector-enabled table under the NARROW schema A and append ONE data file f(A).
        await CreateDvTableAsync(SchemaA, NarrowBatch((1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e")));
        var backend = new LocalFileSystemBackend(_root);

        // 2) PARTIAL DV-delete a subset of f's rows (id==2). Merge-on-read writes a positional deletion vector
        //    and a residual add on the SAME path — f STAYS ACTIVE and PHYSICALLY NARROW (its Parquet body is
        //    NEVER rewritten). This is what leaves a still-active file under the narrow schema.
        DeleteResult partial = await NewDelete("issue-645-partial").DeleteAsync(WhereId(id => id == 2));
        Assert.Equal(1, partial.RowsDeleted);
        Assert.Equal(1, partial.FilesWithDeletionVector);   // f retained with a DV (not removed outright)
        Assert.Equal(0, partial.FilesFullyDeleted);

        // 3) ADD COLUMN → schema B, via a mergeSchema append (Spark's schema evolution). This EVOLVES the
        //    table schema to B and appends a NEW WIDE file g(B). f is untouched (still narrow, still DV-retained).
        using (DeltaWriteTarget append = DeltaWriteTarget.ForLocalPath(_root))
        {
            DeltaWriteResult evolved = await append.AppendAsync(
                SchemaB, Array.Empty<string>(), new[] { WideBatch((10, "z", 100L)) }, mergeSchema: true);
            Assert.Equal(2L, evolved.Version);              // v0 create, v1 partial delete, v2 evolve+append
        }

        // PRECONDITION (the crux of #645): at second-DELETE time the table schema is B while the active set is
        // {narrow DV-retained f, wide g}. f's Parquet body STILL LACKS `extra` — a re-scan of f against B must
        // therefore null-fill the absent column.
        Snapshot before = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        Assert.Equal(3, before.Schema.Count);               // id, name, extra (schema B)
        Assert.Equal(2, before.ActiveFiles.Length);
        AddFileAction narrow = Assert.Single(before.ActiveFiles, a => a.DeletionVector is not null);
        Assert.Equal(new[] { "id", "name" }, await ReadFileColumnNamesAsync(narrow.Path));         // f: NO `extra`
        AddFileAction wide = Assert.Single(before.ActiveFiles, a => a.DeletionVector is null);
        Assert.Equal(new[] { "id", "name", "extra" }, await ReadFileColumnNamesAsync(wide.Path));  // g: has `extra`

        // 4) Second DELETE with a predicate over the LATER-ADDED `extra` column, forcing a re-scan of the
        //    still-active NARROW f against schema B. Pre-#645 (nullFillMissingColumns:false) this failed CLOSED
        //    on ColumnNotPresentInFile('extra'); the fix null-fills the absent column so the predicate sees
        //    `extra` as NULL for f's rows (schema-on-read) — exactly like the read/CDF/optimize paths. The
        //    predicate `extra IS NULL AND id == 4` matches ONLY a row of the narrow file (g's `extra` is 100),
        //    proving the predicate genuinely observed the null-filled column.
        DeleteResult evolvedDelete = await NewDelete("issue-645-evolved").DeleteAsync(
            DeltaDeletePredicate.FromRowPredicate((batch, row) =>
                batch.SelectedColumn(2).IsNull(row)                     // extra IS NULL (null-filled for f's rows)
                && batch.SelectedColumn(0).GetValue<long>(row) == 4));  // id == 4 (a row present only in narrow f)

        // The DELETE SUCCEEDS (the bug was a HARD failure on the re-scan) and removes EXACTLY id 4 from f.
        Assert.NotNull(evolvedDelete.CommittedVersion);
        Assert.Equal(1, evolvedDelete.RowsDeleted);
        Assert.Equal(1, evolvedDelete.FilesWithDeletionVector);   // f re-vectored (positions of id2 ∪ id4)
        Assert.Equal(0, evolvedDelete.FilesFullyDeleted);

        // Read-back through the reconciled schema B: id 2 (step 2) and id 4 (step 4) are gone, and every
        // surviving row from the OLD narrow file reads `extra` == null — only possible because the DELETE
        // re-scan null-filled the absent column. g's row keeps its real `extra` value (100).
        List<(long Id, string? Name, long? Extra)> survivors = await ReadRowsAsync();
        Assert.Equal(
            new (long, string?, long?)[]
            {
                (1L, "a", null),
                (3L, "c", null),
                (5L, "e", null),
                (10L, "z", 100L),
            },
            survivors.OrderBy(r => r.Id).ToList());
    }

    // ------------------------------------------------------------------ helpers

    private async Task CreateDvTableAsync(StructType schema, params ColumnBatch[] batches)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        await target.CreateDeletionVectorTableAsync(schema, Array.Empty<string>(), batches);
    }

    // A plain (non-CDF) merge-on-read DELETE with a DETERMINISTIC deletion-vector id source (distinct seeds ⇒
    // distinct DV .bin file names, so the two deletes on the same path never collide and every run is
    // byte-for-byte reproducible — no ambient Guid.NewGuid).
    private DeltaDelete NewDelete(string idSeed)
    {
        var backend = new LocalFileSystemBackend(_root);
        return new DeltaDelete(
            backend, new DeltaLog(backend), new DeltaCommitter(backend),
            idSource: new SeededDeletionVectorIdSource(idSeed));
    }

    private static DeltaDeletePredicate WhereId(Func<long, bool> match) =>
        DeltaDeletePredicate.FromRowPredicate((batch, row) => match(batch.SelectedColumn(0).GetValue<long>(row)));

    // The PHYSICAL Parquet column names of a data file (via the DeltaSharp footer decoder) — proves the
    // still-active file f is genuinely narrow (no `extra`) at second-DELETE time.
    private async Task<string[]> ReadFileColumnNamesAsync(string relativePath)
    {
        await using FileStream stream = File.OpenRead(Path.Combine(_root, relativePath));
        StructType schema = await new ParquetFileReader().ReadDataSchemaAsync(stream, CancellationToken.None);
        return schema.Select(f => f.Name).ToArray();
    }

    // Reads the latest snapshot through the READ door (which applies deletion vectors and null-fills the
    // evolved column for the narrow file) into logical (id, name, extra) rows.
    private async Task<List<(long Id, string? Name, long? Extra)>> ReadRowsAsync()
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        var rows = new List<(long, string?, long?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector name = batch.SelectedColumn(1);
            ColumnVector extra = batch.SelectedColumn(2);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((
                    id.GetValue<long>(r),
                    name.IsNull(r) ? null : Encoding.UTF8.GetString(name.GetBytes(r)),
                    extra.IsNull(r) ? null : extra.GetValue<long>(r)));
            }
        }

        return rows;
    }

    // A schema-A (id:long non-null, name:string?) batch — the narrow file f.
    private static ColumnBatch NarrowBatch(params (long Id, string? Name)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((long i, string? n) in rows)
        {
            id.AppendValue(i);
            AppendNullableString(name, n);
        }

        return new ManagedColumnBatch(SchemaA, new ColumnVector[] { id, name }, rows.Length);
    }

    // A schema-B (id:long non-null, name:string?, extra:long?) batch — the wide file g appended by the
    // mergeSchema ADD COLUMN.
    private static ColumnBatch WideBatch(params (long Id, string? Name, long? Extra)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector extra = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        foreach ((long i, string? n, long? e) in rows)
        {
            id.AppendValue(i);
            AppendNullableString(name, n);
            if (e is null)
            {
                extra.AppendNull();
            }
            else
            {
                extra.AppendValue(e.Value);
            }
        }

        return new ManagedColumnBatch(SchemaB, new ColumnVector[] { id, name, extra }, rows.Length);
    }

    private static void AppendNullableString(MutableColumnVector vector, string? value)
    {
        if (value is null)
        {
            vector.AppendNull();
        }
        else
        {
            vector.AppendBytes(Encoding.UTF8.GetBytes(value));
        }
    }
}
