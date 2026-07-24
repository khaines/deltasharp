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
/// (<c>nullFillMissingColumns:true → false</c> in <see cref="DeltaDelete"/>) turns the null-fill test below
/// RED with <c>DeltaStorageException: Requested column 'extra' is not present in the Parquet file schema.</c>
/// — the exact bug — so that test is load-bearing on the fix.</para>
///
/// <para>Coverage: (1) the POSITIVE null-fill test — an <c>extra IS NULL</c> predicate matches the null-filled
/// rows and the DELETE succeeds; (2) a NEGATIVE oracle — an <c>IsNull</c>-guarded VALUE predicate
/// <c>extra == 100</c> must NOT false-match a null-filled row (the null-fill surfaces a genuine
/// <c>IsNull==true</c>; three-valued logic itself is the query layer's contract, tracked as #657); and (3) a
/// type-widening COMPLEMENT on the re-scan's sibling knob <c>allowTypeWideningPromotion</c> — a resident
/// narrow-<c>int</c> file promotes <c>int→long</c> (orthogonal to the null-fill flag, so it does not gate on
/// this fix).</para>
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

    // Widening axis (#645 Quality complement): an int `amount` and its widened long form. A file written under
    // IntAmountSchema and left resident by a partial DV-delete must promote (int→long) when a later DELETE
    // re-scans it against LongAmountSchema.
    private static readonly StructType IntAmountSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("amount", DataTypes.IntegerType, nullable: true),
    });

    private static readonly StructType LongAmountSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("amount", DataTypes.LongType, nullable: true),
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
        // Setup: DV table under schema A + file f, PARTIAL DV-delete (f retained ACTIVE and physically NARROW),
        // then ADD COLUMN via mergeSchema → schema B (+ nullable `extra`, wide file g). The helper asserts the
        // #645 re-scan precondition: schema B, active set {narrow DV-retained f WITHOUT `extra`, wide g}.
        await SetupNarrowDvRetainedFilePlusWideAsync();

        // DELETE with a predicate over the LATER-ADDED `extra` column, forcing a re-scan of the still-active
        // NARROW f against schema B. Pre-#645 (nullFillMissingColumns:false) this failed CLOSED on
        // ColumnNotPresentInFile('extra'); the fix null-fills the absent column so the predicate sees `extra`
        // as NULL for f's rows (schema-on-read) — exactly like the read/CDF/optimize paths. `extra IS NULL AND
        // id == 4` matches ONLY a row of the narrow file (g's `extra` is 100), proving the predicate genuinely
        // observed the null-filled column.
        DeleteResult evolvedDelete = await NewDelete("issue-645-evolved").DeleteAsync(
            DeltaDeletePredicate.FromRowPredicate((batch, row) =>
                batch.SelectedColumn(2).IsNull(row)                     // extra IS NULL (null-filled for f's rows)
                && batch.SelectedColumn(0).GetValue<long>(row) == 4));  // id == 4 (a row present only in narrow f)

        // The DELETE SUCCEEDS (the bug was a HARD failure on the re-scan) and removes EXACTLY id 4 from f.
        Assert.NotNull(evolvedDelete.CommittedVersion);
        Assert.Equal(1, evolvedDelete.RowsDeleted);
        Assert.Equal(1, evolvedDelete.FilesWithDeletionVector);   // f re-vectored (positions of id2 ∪ id4)
        Assert.Equal(0, evolvedDelete.FilesFullyDeleted);

        // Read-back through the reconciled schema B: id 2 (setup) and id 4 (this delete) are gone, and every
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

    [Fact]
    public async Task Delete_AfterAddColumn_IsNullGuardedValuePredicate_DoesNotFalseMatchNullFilledRows_Issue645()
    {
        // NEGATIVE oracle (#645 council, query-execution seat): the re-scan null-fill must surface a GENUINE
        // IsNull==true for the narrow file's absent column, so an IsNull-GUARDED VALUE predicate
        // (`extra == 100`) evaluates correct three-valued logic — it matches ONLY the wide file g's real value
        // and NEVER false-positives on f's null-filled rows. (Three-valued logic is the QUERY layer's contract,
        // tracked separately as #657; this test only pins that the storage re-scan feeds a TRUTHFUL null mask —
        // no production 3VL code is added here.)
        await SetupNarrowDvRetainedFilePlusWideAsync();

        // `extra IS NOT NULL AND extra == 100`: the IsNull guard EXCLUDES f's null-filled rows; the value match
        // then hits ONLY g's real `extra` (100). A broken null-fill (garbage payload behind a null slot, or a
        // wrong null mask) would either throw here or false-match one of f's rows.
        DeleteResult result = await NewDelete("issue-645-negative").DeleteAsync(
            DeltaDeletePredicate.FromRowPredicate((batch, row) =>
                !batch.SelectedColumn(2).IsNull(row)                      // extra IS NOT NULL (skips f's null-fills)
                && batch.SelectedColumn(2).GetValue<long>(row) == 100L)); // extra == 100 (only g's real value)

        // EXACTLY g's one row is deleted; the narrow file f (every `extra` null-filled) is entirely untouched.
        Assert.NotNull(result.CommittedVersion);
        Assert.Equal(1, result.RowsDeleted);
        Assert.Equal(0, result.FilesWithDeletionVector);   // g is a single-row file → removed outright, no DV
        Assert.Equal(1, result.FilesFullyDeleted);

        // f's four survivors (id 2 removed during setup) ALL read `extra` == null; g's only row is gone.
        List<(long Id, string? Name, long? Extra)> survivors = await ReadRowsAsync();
        Assert.Equal(
            new (long, string?, long?)[]
            {
                (1L, "a", null),
                (3L, "c", null),
                (4L, "d", null),
                (5L, "e", null),
            },
            survivors.OrderBy(r => r.Id).ToList());
    }

    [Fact]
    public async Task Delete_AfterTypeWidening_ReScansDvRetainedNarrowFile_PromotesWidenedColumn_Issue645()
    {
        // COMPLEMENT axis (#645 council, Quality seat): the DELETE re-scan's OTHER schema-evolution knob,
        // `allowTypeWideningPromotion`. A column type-WIDENED (int→long) after a file was written narrow — that
        // file left resident+narrow by a prior partial DV-delete — must re-scan through the DELETE by PROMOTING
        // its narrow physical int values to long, not fail on the type mismatch. This is ORTHOGONAL to the
        // null-fill flag (`amount` is PRESENT in the narrow file, just narrower — no column is missing), so it
        // pins the sibling axis on the same re-scan path; reverting the #645 null-fill flag does NOT affect it.
        var backend = new LocalFileSystemBackend(_root);

        // 1) A deletion-vector + typeWidening table under (id, amount:int); append the narrow-int file f.
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateDeletionVectorTableAsync(
                IntAmountSchema, Array.Empty<string>(),
                new[] { IntAmountBatch((1, 10), (2, 20), (3, 30), (4, 40)) });
        }

        await new DeltaTableWriter(backend).EnableTypeWideningAsync();

        // 2) PARTIAL DV-delete (id==2) — f retained ACTIVE and physically NARROW (int `amount`). At this point
        //    the table schema is still (id, amount:int), so this delete re-scans f against int (no promotion).
        DeleteResult partial = await NewDelete("issue-645-widen-partial").DeleteAsync(WhereId(id => id == 2));
        Assert.Equal(1, partial.RowsDeleted);
        Assert.Equal(1, partial.FilesWithDeletionVector);

        // 3) WIDEN `amount` int→long via mergeSchema; appends a wide file g carrying a value only long holds
        //    (3_000_000_000 > int.MaxValue). f is untouched (still narrow-int, still DV-retained).
        using (DeltaWriteTarget append = DeltaWriteTarget.ForLocalPath(_root))
        {
            await append.AppendAsync(
                LongAmountSchema, Array.Empty<string>(),
                new[] { LongAmountBatch((10, 3_000_000_000L)) }, mergeSchema: true);
        }

        // PRECONDITION: the still-active DV-retained file f is physically NARROW — its `amount` is INT, NOT yet
        // widened to long (proving the re-scan below genuinely promotes rather than reading an already-long file).
        Snapshot before = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        AddFileAction narrow = Assert.Single(before.ActiveFiles, a => a.DeletionVector is not null);
        StructType narrowSchema = await ReadFileSchemaAsync(narrow.Path);
        Assert.Equal(new[] { "id", "amount" }, narrowSchema.Select(f => f.Name).ToArray());
        Assert.Equal(DataTypes.IntegerType, narrowSchema["amount"].DataType);   // f's `amount` is physically INT

        // 4) DELETE re-scans the still-narrow f (int) against the widened (long) schema: it SUCCEEDS by
        //    PROMOTING f's int values to long, and removes id 4 by its promoted long value (amount == 40).
        DeleteResult result = await NewDelete("issue-645-widen-delete").DeleteAsync(
            DeltaDeletePredicate.FromRowPredicate((batch, row) =>
                !batch.SelectedColumn(1).IsNull(row) && batch.SelectedColumn(1).GetValue<long>(row) == 40L));

        Assert.NotNull(result.CommittedVersion);
        Assert.Equal(1, result.RowsDeleted);

        // Read-back: f's narrow int values promoted to long (10, 30 survive after id 2 & id 4 removed); g's long
        // value (3_000_000_000, unrepresentable as int — proves genuine widening, not truncation) is intact.
        List<(long Id, long? Amount)> survivors = await ReadIdAmountRowsAsync();
        Assert.Equal(
            new (long, long?)[] { (1L, 10L), (3L, 30L), (10L, 3_000_000_000L) },
            survivors.OrderBy(r => r.Id).ToList());
    }

    // ------------------------------------------------------------------ helpers

    private async Task CreateDvTableAsync(StructType schema, params ColumnBatch[] batches)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        await target.CreateDeletionVectorTableAsync(schema, Array.Empty<string>(), batches);
    }

    // Shared setup for the null-fill re-scan tests: a DV table under the NARROW schema A with one data file f;
    // a PARTIAL DV-delete (id==2) that retains f ACTIVE and physically NARROW; then an ADD COLUMN (mergeSchema)
    // to schema B (+ nullable `extra`) that appends a single-row WIDE file g (extra=100). Asserts the #645
    // re-scan precondition: schema B, active set {narrow DV-retained f WITHOUT `extra`, wide g WITH `extra`}.
    private async Task SetupNarrowDvRetainedFilePlusWideAsync()
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
    private async Task<string[]> ReadFileColumnNamesAsync(string relativePath) =>
        (await ReadFileSchemaAsync(relativePath)).Select(f => f.Name).ToArray();

    // The PHYSICAL Parquet data schema (names + TYPES) of a data file — proves a resident file's narrow shape
    // (e.g. its column is still INT, not yet widened to long) at re-scan time.
    private async Task<StructType> ReadFileSchemaAsync(string relativePath)
    {
        await using FileStream stream = File.OpenRead(Path.Combine(_root, relativePath));
        return await new ParquetFileReader().ReadDataSchemaAsync(stream, CancellationToken.None);
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

    // Reads the latest snapshot through the READ door into logical (id, amount:long) rows — the widened
    // `amount` (int values in the resident narrow file promoted to long, plus g's genuine long value).
    private async Task<List<(long Id, long? Amount)>> ReadIdAmountRowsAsync()
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        var rows = new List<(long, long?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector amount = batch.SelectedColumn(1);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((id.GetValue<long>(r), amount.IsNull(r) ? null : amount.GetValue<long>(r)));
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

    // A widening-int (id:long non-null, amount:int?) batch — the narrow file f, written BEFORE the int→long widening.
    private static ColumnBatch IntAmountBatch(params (long Id, int? Amount)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector amount = ColumnVectors.Create(DataTypes.IntegerType, rows.Length);
        foreach ((long i, int? a) in rows)
        {
            id.AppendValue(i);
            if (a is null)
            {
                amount.AppendNull();
            }
            else
            {
                amount.AppendValue(a.Value);
            }
        }

        return new ManagedColumnBatch(IntAmountSchema, new ColumnVector[] { id, amount }, rows.Length);
    }

    // A widening-long (id:long non-null, amount:long?) batch — the wide file g that carries the int→long widening.
    private static ColumnBatch LongAmountBatch(params (long Id, long? Amount)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector amount = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        foreach ((long i, long? a) in rows)
        {
            id.AppendValue(i);
            if (a is null)
            {
                amount.AppendNull();
            }
            else
            {
                amount.AppendValue(a.Value);
            }
        }

        return new ManagedColumnBatch(LongAmountSchema, new ColumnVector[] { id, amount }, rows.Length);
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
