using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet.Serialization;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// The §3 oracle for #857 — a NAME/none-mode nested <c>struct</c> child whose physical name is genuinely
/// ABSENT from a data file (the drop-then-re-add case) is NULL-FILLED when the requested child is nullable,
/// instead of fail-closing. Everything else stays fail-closed: a PRESENT-but-mismatched child still throws
/// (AC3), a DUPLICATE physical name still fails closed, and an absent NON-nullable child fails closed
/// (ColumnNotPresentInFile). The absent child's type may be scalar OR nested (struct/array/map), and the
/// absent child null-fills as an all-null subtree without reading any interior leaf.
/// </summary>
/// <remarks>
/// The physical footer is authored with the production <see cref="ParquetSerializer"/> from a POCO that
/// physically carries ONLY a subset of the struct's fields, so the omitted field's physical name is genuinely
/// absent from the file (the same on-disk shape a pre-re-add data file has for a re-added child's fresh
/// physical name). The file is then read back through the production
/// <see cref="ParquetFileReader"/>/<see cref="NestedParquetColumnReader"/> decode. Same-typed siblings draw
/// from disjoint value domains so a positional mis-bind cannot pass on equal values.
/// </remarks>
public sealed class NestedAbsentChildNullFillTests
{
    // ---- POCOs whose struct physically carries only a SUBSET of the requested fields ------------------

    // A struct physically carrying only a (nullable) long field `A` — the requested `B` is physically absent.
    private sealed class OnlyA
    {
        public long? A { get; set; }
    }

    private sealed class OnlyARow
    {
        public int Id { get; set; }

        public OnlyA? S { get; set; }
    }

    // A struct physically carrying a long `A` and an int `B` — for the PRESENT-but-type/shape-mismatch cases.
    private sealed class LongIntStruct
    {
        public long A { get; set; }

        public int B { get; set; }
    }

    private sealed class LongIntRow
    {
        public int Id { get; set; }

        public LongIntStruct? S { get; set; }
    }

    // array<struct<A:long>> — the repeated-ancestor path; the requested `B` is physically absent per element.
    private sealed class ElemOnlyA
    {
        public long? A { get; set; }
    }

    private sealed class ArrRow
    {
        public int Id { get; set; }

        public List<ElemOnlyA>? Items { get; set; }
    }

    // ---- §3.1 · AC1 — absent nullable nested struct child null-fills (not fail-closed) ----------------

    [Fact]
    public async Task NestedStructChild_NameMode_AbsentPhysicalName_NullFills()
    {
        // struct<A:long, B:string> written to a file that physically contains ONLY `A`; `B`'s physical name is
        // absent from the footer. Reading the struct requesting both `A` and `B` reads `A`'s real values and
        // null-fills `B` — the direct nested analogue of the top-level absent-column null-fill.
        var rows = new List<OnlyARow>
        {
            new() { Id = 1, S = new OnlyA { A = 90001L } },
            new() { Id = 2, S = new OnlyA { A = 90002L } },
        };
        byte[] bytes = await WriteAsync(rows);

        ColumnBatch batch = await ReadSingleAsync(bytes, RequestStructAB(bNullable: true));
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));

        Assert.False(s.IsNull(0));
        Assert.False(s.IsNull(1));
        Assert.Equal(90001L, s.Child("A").GetValue<long>(0));
        Assert.Equal(90002L, s.Child("A").GetValue<long>(1));
        Assert.True(s.Child("B").IsNull(0)); // absent physical column → all NULL
        Assert.True(s.Child("B").IsNull(1));
    }

    [Fact]
    public async Task NestedStructChild_AbsentPhysicalName_StructNullMaskCorrect_UnderNullableStruct()
    {
        // Rows mix null struct, present struct with present `A`, and present struct with null `A`. The
        // synthesized StructPresenceDefs drives BuildStructNullMask to the correct per-row mask AND the parity
        // guard does not trip; `B` is null on every present-struct row and the whole cell is null on null-struct
        // rows.
        var rows = new List<OnlyARow>
        {
            new() { Id = 1, S = new OnlyA { A = 90001L } }, // present struct, A present
            new() { Id = 2, S = new OnlyA { A = null } },   // present struct, A null
            new() { Id = 3, S = null },                     // null struct
        };
        byte[] bytes = await WriteAsync(rows);

        ColumnBatch batch = await ReadSingleAsync(bytes, RequestStructAB(bNullable: true));
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));

        Assert.False(s.IsNull(0));
        Assert.False(s.IsNull(1));
        Assert.True(s.IsNull(2)); // null struct row

        ColumnVector a = s.Child("A");
        Assert.Equal(90001L, a.GetValue<long>(0));
        Assert.True(a.IsNull(1)); // present struct, null A
        Assert.True(a.IsNull(2)); // null struct materializes null children

        ColumnVector b = s.Child("B");
        Assert.True(b.IsNull(0)); // absent → null on a present-struct row
        Assert.True(b.IsNull(1));
        Assert.True(b.IsNull(2)); // whole cell null on a null-struct row
    }

    [Fact]
    public async Task NestedStructChild_AbsentIsOnlyProjectedField_StructNullMaskStillCorrect()
    {
        // Projection requests ONLY the absent child `B`. The mask must still be correct (projection-independence)
        // — presence is read from the file struct's OWN driving leaf, not a requested sibling.
        var rows = new List<OnlyARow>
        {
            new() { Id = 1, S = new OnlyA { A = 5L } }, // present struct
            new() { Id = 2, S = null },                 // null struct
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "S",
                new StructType(new[] { new StructField("B", DataTypes.StringType, nullable: true) }),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));

        Assert.False(s.IsNull(0)); // a genuinely present struct row
        Assert.True(s.IsNull(1));  // a genuinely null struct row
        Assert.True(s.Child("B").IsNull(0)); // present-struct row → null B
        Assert.True(s.Child("B").IsNull(1)); // null-struct row → whole cell null
    }

    // ---- §3.3 · AC3 — genuine shape/type mismatch stays FAIL-CLOSED ------------------------------------

    [Fact]
    public async Task NestedStructChild_PresentButTypeMismatch_FailsClosed()
    {
        // `B`'s physical name IS present but its physical type disagrees (file int32, requested string). This
        // routes through ExpectScalarLeaf and fails closed — the absent branch is NEVER taken for a present
        // child, so absence and mismatch are never conflated.
        var rows = new List<LongIntRow> { new() { Id = 1, S = new LongIntStruct { A = 1L, B = 7 } } };
        byte[] bytes = await WriteAsync(rows);

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, RequestStructAB(bNullable: true)));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Fact]
    public async Task NestedStructChild_PresentButShapeMismatch_FailsClosed()
    {
        // `B`'s physical name is present but the file shape disagrees (file scalar, requested struct<…>). This
        // fails closed at ValidateNode ("requested a struct but the file column is not a struct"), not null-fill.
        var rows = new List<LongIntRow> { new() { Id = 1, S = new LongIntStruct { A = 1L, B = 7 } } };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "S",
                new StructType(new[]
                {
                    new StructField("A", DataTypes.LongType, nullable: true),
                    new StructField(
                        "B",
                        new StructType(new[] { new StructField("x", DataTypes.IntegerType, nullable: true) }),
                        nullable: true),
                }),
                nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Fact]
    public async Task NestedStructChild_DuplicatePhysicalName_FailsClosed()
    {
        // Two file fields share the requested physical name. A duplicate is AMBIGUOUS and fails closed — it is
        // never treated as absent. (The name/none-mode leaf-path uniqueness guard preempts the resolver's own
        // duplicate throw; both are fail-closed SchemaMismatch — a duplicate never null-fills.)
        var fileSchema = new global::Parquet.Schema.ParquetSchema(
            new global::Parquet.Schema.StructField(
                "S",
                new global::Parquet.Schema.DataField<long>("A"),
                new global::Parquet.Schema.DataField<long>("dup"),
                new global::Parquet.Schema.DataField<long>("dup")));

        var requested = new StructType(new[]
        {
            new StructField(
                "S",
                new StructType(new[]
                {
                    new StructField("A", DataTypes.LongType, nullable: true),
                    new StructField("dup", DataTypes.LongType, nullable: true),
                }),
                nullable: true),
        });

        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(() => ParquetFileReader.ResolveFileFields(
            fileSchema, requested, nullFillMissingColumns: false, allowTypeWideningPromotion: false, byFieldId: null));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Fact]
    public void NestedStructChild_CraftedDefStreamDisagreesOnStructPresence_FailsClosed()
    {
        // A synthesized absent child's presence stream (a StructPresenceDefs clone) does NOT mask a genuine
        // crafted-Dremel disagreement among PRESENT fields: with the absent clone reporting the struct present
        // (def == structMaxDef) but a crafted PRESENT sibling reporting the struct null (def < structMaxDef) at
        // the same row, BuildStructNullMask's parity guard still throws CorruptData.
        int structMaxDef = 1;

        // fieldDefs[0] = the synthesized absent child's clamped presence clone (struct present at row 0).
        // fieldDefs[1] = a crafted PRESENT sibling that disagrees (struct null at row 0).
        int[]?[] fieldDefs = { new[] { structMaxDef }, new[] { 0 } };

        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildStructNullMask(fieldDefs, structMaxDef, rowCount: 1, "col"));
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        Assert.Contains("disagree on the struct's presence", ex.Message, StringComparison.Ordinal);
    }

    // ---- §3.4 · nested-typed absent child null-fill (scalar AND nested) --------------------------------

    [Fact]
    public async Task NestedStructChild_AbsentNestedStructChild_NullFillsWholeSubtree()
    {
        // The absent child is itself a struct<X:int, Y:string> (585a). It reads as an all-null StructColumnVector
        // (every row null), and its interior children are all null — no interior leaf is read.
        var rows = new List<OnlyARow>
        {
            new() { Id = 1, S = new OnlyA { A = 10L } },
            new() { Id = 2, S = new OnlyA { A = 20L } },
        };
        byte[] bytes = await WriteAsync(rows);

        var nested = new StructType(new[]
        {
            new StructField("X", DataTypes.IntegerType, nullable: true),
            new StructField("Y", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[]
        {
            new StructField(
                "S",
                new StructType(new[]
                {
                    new StructField("A", DataTypes.LongType, nullable: true),
                    new StructField("Nested", nested, nullable: true),
                }),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));

        var absent = Assert.IsType<StructColumnVector>(s.Child("Nested"));
        for (int r = 0; r < 2; r++)
        {
            Assert.False(s.IsNull(r));    // the OUTER struct is present
            Assert.True(absent.IsNull(r)); // the absent nested subtree is null at every row
            Assert.True(absent.Child("X").IsNull(r));
            Assert.True(absent.Child("Y").IsNull(r));
        }
    }

    [Fact]
    public async Task NestedStructChild_AbsentArrayChild_NullFills()
    {
        var rows = new List<OnlyARow>
        {
            new() { Id = 1, S = new OnlyA { A = 10L } },
            new() { Id = 2, S = new OnlyA { A = 20L } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "S",
                new StructType(new[]
                {
                    new StructField("A", DataTypes.LongType, nullable: true),
                    new StructField("Arr", new ArrayType(DataTypes.IntegerType), nullable: true),
                }),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));
        var arr = Assert.IsType<ListColumnVector>(s.Child("Arr"));
        Assert.True(arr.IsNull(0));
        Assert.True(arr.IsNull(1));
        Assert.Equal(0, arr.Elements.Length); // the subtree is never decoded — no elements
    }

    [Fact]
    public async Task NestedStructChild_AbsentMapChild_NullFills()
    {
        var rows = new List<OnlyARow>
        {
            new() { Id = 1, S = new OnlyA { A = 10L } },
            new() { Id = 2, S = new OnlyA { A = 20L } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "S",
                new StructType(new[]
                {
                    new StructField("A", DataTypes.LongType, nullable: true),
                    new StructField("M", new MapType(DataTypes.StringType, DataTypes.LongType), nullable: true),
                }),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));
        var map = Assert.IsType<MapColumnVector>(s.Child("M"));
        Assert.True(map.IsNull(0));
        Assert.True(map.IsNull(1));
        Assert.Equal(0, map.Keys.Length); // never decoded — no entries
    }

    // ---- §3.5 · required (non-nullable) absent child — fail-closed (§9 Q3) -----------------------------

    [Fact]
    public async Task NestedStructChild_AbsentButNonNullable_FailsClosed()
    {
        // The absent child `B` is declared NON-nullable. A required lane cannot carry the null the older rows
        // would need, so an absent required child fails closed (ColumnNotPresentInFile) rather than null-fill.
        var rows = new List<OnlyARow> { new() { Id = 1, S = new OnlyA { A = 10L } } };
        byte[] bytes = await WriteAsync(rows);

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, RequestStructAB(bNullable: false)));
        Assert.Equal(StorageErrorKind.ColumnNotPresentInFile, ex.Kind);
    }

    // ---- §3.6 · repeated-ancestor path (underRepeatedAncestor) and budget -------------------------------

    [Fact]
    public async Task NestedStructChild_AbsentUnderRepeatedAncestor_NullFills_ParityHolds()
    {
        // An absent nullable scalar child of a struct nested UNDER a repeated ancestor
        // (array<struct<A:long, B:string>> where `B` is physically absent, structMaxRep > 0). The synthesized
        // StructPresenceDefs is built via ExtractOwnerCellDefs (clamped at structMaxDef), one cell per owner
        // element; `B` reads null per element; the owner-cell counts reconcile.
        var rows = new List<ArrRow>
        {
            new()
            {
                Id = 1,
                Items = new List<ElemOnlyA> { new() { A = 100L }, new() { A = 200L } },
            },
            new() { Id = 2, Items = new List<ElemOnlyA>() },       // empty list
            new() { Id = 3, Items = null },                        // null list
            new()
            {
                Id = 4,
                Items = new List<ElemOnlyA> { new() { A = null } }, // one element, null A
            },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Items",
                new ArrayType(new StructType(new[]
                {
                    new StructField("A", DataTypes.LongType, nullable: true),
                    new StructField("B", DataTypes.StringType, nullable: true),
                })),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var list = Assert.IsType<ListColumnVector>(batch.Column("Items"));

        Assert.False(list.IsNull(0));
        Assert.False(list.IsNull(1)); // empty list is present-but-empty
        Assert.True(list.IsNull(2));  // null list
        Assert.False(list.IsNull(3));

        var elements = Assert.IsType<StructColumnVector>(list.Elements);
        ColumnVector a = elements.Child("A");
        ColumnVector b = elements.Child("B");

        // Row 0 has 2 elements: A = 100, 200; B all null.
        (int start0, int len0) = list.RawElementSpan(0);
        Assert.Equal(2, len0);
        Assert.Equal(100L, a.GetValue<long>(start0));
        Assert.Equal(200L, a.GetValue<long>(start0 + 1));
        Assert.True(b.IsNull(start0));
        Assert.True(b.IsNull(start0 + 1));

        // Row 3 has 1 element with a null A; B null too.
        (int start3, int len3) = list.RawElementSpan(3);
        Assert.Equal(1, len3);
        Assert.True(a.IsNull(start3));
        Assert.True(b.IsNull(start3));

        // Every element's B is null (absent physical column across the whole list).
        for (int e = 0; e < b.Length; e++)
        {
            Assert.True(b.IsNull(e));
        }
    }

    [Fact]
    public async Task NestedAbsentChild_Budget_ChargedAndBounded()
    {
        // A wide projection of many absent children over a row group. A crafted (tiny) ceiling that the
        // synthesized all-null vectors + presence-leaf reads would overflow fails closed with CorruptData
        // (a NestedDecodeBudget breach), never OOMs; the same projection under the default ceiling succeeds.
        const int rowCount = 200;
        var rows = new List<OnlyARow>(rowCount);
        for (int i = 0; i < rowCount; i++)
        {
            rows.Add(new OnlyARow { Id = i, S = new OnlyA { A = i } });
        }

        byte[] bytes = await WriteAsync(rows);

        // struct<A:long, B0..B7:string> — 8 absent nullable children, each null-filled O(rows).
        var fields = new List<StructField> { new("A", DataTypes.LongType, nullable: true) };
        for (int k = 0; k < 8; k++)
        {
            fields.Add(new StructField("B" + k, DataTypes.StringType, nullable: true));
        }

        var requested = new StructType(new[]
        {
            new StructField("S", new StructType(fields), nullable: true),
        });

        // A tiny ceiling: the cumulative synthesized-vector + presence-leaf charges breach it → fail closed.
        var tight = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 6000));
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(tight, bytes, requested));
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);

        // The default ceiling comfortably admits the same projection; every absent child reads all-null.
        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));
        Assert.Equal(0L, s.Child("A").GetValue<long>(0));
        for (int k = 0; k < 8; k++)
        {
            ColumnVector b = s.Child("B" + k);
            for (int r = 0; r < rowCount; r++)
            {
                Assert.True(b.IsNull(r));
            }
        }
    }

    // ---- §3.6b · repeated-ancestor parity: a NULL struct owner cell must report ABSENT (R2 Quality F1) ----

    [Fact]
    public void ExtractOwnerCellDefs_NullStructOwnerCell_UnderRepeatedAncestor_ReportsAbsent()
    {
        // The presence stream that StructPresenceDefs clones for an absent child is produced by
        // ExtractOwnerCellDefs. Under a repeated ancestor (parentMaxRep > 0), a list element whose STRUCT is
        // null (present list element, null struct: parentMaxDef <= d < structMaxDef) MUST report def <
        // structMaxDef so BuildStructNullMask marks that owner cell null — matching a present sibling. A
        // regression that over-reports presence (clamping to structMaxDef unconditionally under a repeated
        // ancestor) would disagree with a present sibling's real def and trip the parity guard on valid data.
        // Levels for list<struct<...>>: parentMaxRep=1 (list), parentMaxDef=1 (list element present),
        // structMaxDef=2 (struct present). One row, one list of TWO elements: [present struct, null struct].
        const int structMaxDef = 2;
        const int parentMaxDef = 1;
        const int parentMaxRep = 1;
        int[] def = { 2, 1 }; // elem 0: struct present (d=2); elem 1: element present but struct null (d=1)
        int[] rep = { 0, 1 }; // elem 0 opens the row/list; elem 1 continues the same list

        int[] owned = NestedParquetColumnReader.ExtractOwnerCellDefs(
            def, rep, numValues: 2, structMaxDef, parentMaxDef, parentMaxRep, ownerCells: 2, "col");

        Assert.Equal(2, owned[0]);                 // present struct owner cell
        Assert.True(owned[1] < structMaxDef);      // NULL struct owner cell — reported absent (kills the over-report mutant)
        Assert.Equal(1, owned[1]);                 // exactly the clamped list-element def, not structMaxDef
    }

    [Fact]
    public void ExtractOwnerCellDefs_PresentFieldDefAboveStructDef_IsClampedToStructMaxDef()
    {
        // RFL-864 merge round F2: the {2,1} case above never drives d ABOVE structMaxDef, so a mutant that
        // drops the `Math.Min(d, structMaxDef)` cap survives it. Here the driving leaf is an OPTIONAL field
        // inside the struct, so a present-struct/present-field owner cell reports def = structMaxDef + 1 (=3).
        // ExtractOwnerCellDefs must CLAMP it to structMaxDef (2) — the contract is "the struct's presence, not
        // the leaf's own depth" — otherwise an absent child's synthesized presence would over-report the def
        // relative to a present sibling clamped identically. Assert the clamp bites: owned[0] == structMaxDef,
        // NOT structMaxDef + 1 (which the drop-clamp mutant would produce).
        const int structMaxDef = 2;
        const int parentMaxDef = 1;
        const int parentMaxRep = 1;
        int[] def = { 3 }; // one present list element, present struct, present optional field -> leaf def 3
        int[] rep = { 0 };

        int[] owned = NestedParquetColumnReader.ExtractOwnerCellDefs(
            def, rep, numValues: 1, structMaxDef, parentMaxDef, parentMaxRep, ownerCells: 1, "col");

        Assert.Equal(structMaxDef, owned[0]); // clamped to 2 (struct present), NOT the leaf's raw def 3
    }

    // ---- §3.6c · empty row group (rowCount == 0) — null-fill composes without throwing (R2 F2/F3) --------

    [Fact]
    public async Task NestedAbsentChild_EmptyRowGroup_NullFillComposesWithoutThrowing()
    {
        // An absent nullable child over a ZERO-row file. SynthesizeAbsentChild uses Math.Max(rowCount,1) for
        // capacity and a 0-iteration append loop; StructPresenceDefs / BuildStructNullMask see rowCount == 0.
        // The read must complete without throwing; any produced batch exposes the absent child at length 0.
        byte[] bytes = await WriteAsync(new List<OnlyARow>());

        using var stream = new MemoryStream(bytes, writable: false);
        var reader = new ParquetFileReader();
        StructType requested = RequestStructAB(bNullable: true);
        await foreach (ColumnBatch batch in reader.ReadAsync(
            stream, requested, keepRowGroup: null, nullFillMissingColumns: false,
            allowTypeWideningPromotion: false, resolveByFieldId: false, CancellationToken.None))
        {
            var s = Assert.IsType<StructColumnVector>(batch.Column("S"));
            ColumnVector b = s.Child("B");
            Assert.Equal(batch.RowCount, b.Length);
        }
    }

    // ---- harness --------------------------------------------------------------------------------------

    private static StructType RequestStructAB(bool bNullable) => new(new[]
    {
        new StructField(
            "S",
            new StructType(new[]
            {
                new StructField("A", DataTypes.LongType, nullable: true),
                new StructField("B", DataTypes.StringType, nullable: bNullable),
            }),
            nullable: true),
    });

    private static async Task<byte[]> WriteAsync<T>(IReadOnlyList<T> rows)
        where T : class, new()
    {
        using var stream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, stream, cancellationToken: CancellationToken.None);
        return stream.ToArray();
    }

    private static Task<ColumnBatch> ReadSingleAsync(byte[] bytes, StructType requested) =>
        ReadSingleAsync(new ParquetFileReader(), bytes, requested);

    private static async Task<ColumnBatch> ReadSingleAsync(
        ParquetFileReader reader, byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch batch in reader.ReadAsync(
            stream, requested, keepRowGroup: null, nullFillMissingColumns: false,
            allowTypeWideningPromotion: false, resolveByFieldId: false, CancellationToken.None))
        {
            only = batch;
        }

        Assert.NotNull(only);
        return only!;
    }
}
