using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet.Serialization;
using Parquet.Serialization.Attributes;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// Read-path decode tests for the three single-level nested Parquet shapes (#571): struct-of-scalar,
/// array-of-scalar, and map(scalar → scalar). Real nested Parquet is authored with Parquet.Net's typed
/// <see cref="ParquetSerializer"/> (which emits the standard Dremel 3-level shapes) — the DeltaSharp
/// <c>ParquetFileWriter</c> is still scalar-only — then decoded through <see cref="ParquetFileReader"/>
/// into the #570 nested column vectors. Each case asserts values <b>and</b> the null mask at every level:
/// a null struct vs a null field, a null list vs an empty list vs a null element, a null map vs an empty
/// map vs a null value. Out-of-scope nested shapes must fail closed (never a silent/partial read).
/// </summary>
public sealed class NestedParquetReadTests
{
    private sealed class Inner
    {
        public int A { get; set; }

        public string? B { get; set; }
    }

    private sealed class StructRow
    {
        public int Id { get; set; }

        public Inner? S { get; set; }
    }

    private sealed class Wide
    {
        public long L { get; set; }

        public double D { get; set; }

        public bool Flag { get; set; }
    }

    private sealed class WideRow
    {
        public int Id { get; set; }

        public Wide? W { get; set; }
    }

    private sealed class TwoStructRow
    {
        public int Id { get; set; }

        public Wide? A { get; set; }

        public Wide? B { get; set; }
    }

    private sealed class ListRow
    {
        public int Id { get; set; }

        public List<int?>? Arr { get; set; }
    }

    private sealed class StrListRow
    {
        public int Id { get; set; }

        public List<string?>? Names { get; set; }
    }

    private sealed class MapRow
    {
        public int Id { get; set; }

        public Dictionary<string, int?>? M { get; set; }
    }

    private sealed class StrMapRow
    {
        public int Id { get; set; }

        public Dictionary<string, string?>? Sm { get; set; }
    }

    // A file column that is array<struct> (a nested type within a nested type), for the A8 decode-path guard.
    private sealed class NestedListRow
    {
        public int Id { get; set; }

        public List<Inner>? Items { get; set; }
    }

    // All-nullable struct fields, so a PRESENT struct can have every field null — distinct from a NULL struct.
    private sealed class AllNullableInner
    {
        public int? A { get; set; }

        public string? B { get; set; }
    }

    private sealed class AllNullableRow
    {
        public int Id { get; set; }

        public AllNullableInner? S { get; set; }
    }

    // DATE / TIMESTAMP / DECIMAL leaves inside a struct (highest-value untested nested-leaf conversions).
    private sealed class DateInner
    {
        public DateOnly D { get; set; }

        public DateTime Ts { get; set; }

        [ParquetDecimal(18, 4)]
        public decimal Dec { get; set; }
    }

    private sealed class DateRow
    {
        public int Id { get; set; }

        public DateInner? S { get; set; }
    }

    [Fact]
    public async Task Struct_ReadsFields_WithNullFieldAndNullStructRow()
    {
        var rows = new List<StructRow>
        {
            new() { Id = 1, S = new Inner { A = 10, B = "x" } },
            new() { Id = 2, S = new Inner { A = 20, B = null } },
            new() { Id = 3, S = null },
        };
        byte[] bytes = await WriteAsync(rows);

        StructType structType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: false),
            DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[]
        {
            new StructField("Id", DataTypes.IntegerType, nullable: false),
            new StructField("S", structType, nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        Assert.Equal(3, batch.RowCount);

        // A scalar sibling stays on the existing fast path and coexists with the nested column (name mode).
        ColumnVector id = batch.Column("Id");
        Assert.Equal(new[] { 1, 2, 3 }, id.GetValues<int>().ToArray());

        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));
        Assert.False(s.IsNull(0));
        Assert.False(s.IsNull(1));
        Assert.True(s.IsNull(2)); // the whole struct is null on row 3

        ColumnVector a = s.Child("A");
        Assert.Equal(10, a.GetValue<int>(0));
        Assert.Equal(20, a.GetValue<int>(1));
        Assert.True(a.IsNull(2)); // a null struct materializes null children

        ColumnVector b = s.Child("B");
        Assert.Equal("x", Utf8(b, 0));
        Assert.True(b.IsNull(1)); // a present struct with a null field
        Assert.True(b.IsNull(2)); // a null struct materializes null children
    }

    [Fact]
    public async Task Struct_DecodesLongDoubleBoolLeaves()
    {
        var rows = new List<WideRow>
        {
            new() { Id = 1, W = new Wide { L = 5_000_000_000L, D = 3.5, Flag = true } },
            new() { Id = 2, W = null },
        };
        byte[] bytes = await WriteAsync(rows);

        StructType wideType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("L", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("D", DataTypes.DoubleType, nullable: false),
            DataTypes.CreateStructField("Flag", DataTypes.BooleanType, nullable: false),
        });
        var requested = new StructType(new[] { new StructField("W", wideType, nullable: true) });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var w = Assert.IsType<StructColumnVector>(batch.Column("W"));

        Assert.Equal(5_000_000_000L, w.Child("L").GetValue<long>(0));
        Assert.Equal(3.5, w.Child("D").GetValue<double>(0));
        Assert.True(w.Child("Flag").GetValue<bool>(0));

        Assert.True(w.IsNull(1));
        Assert.True(w.Child("L").IsNull(1));
        Assert.True(w.Child("D").IsNull(1));
        Assert.True(w.Child("Flag").IsNull(1));
    }

    [Fact]
    public async Task Array_ReadsElements_WithEmptyNullListAndNullElement()
    {
        var rows = new List<ListRow>
        {
            new() { Id = 1, Arr = new List<int?> { 10, 20 } },
            new() { Id = 2, Arr = new List<int?>() },
            new() { Id = 3, Arr = null },
            new() { Id = 4, Arr = new List<int?> { 40, null } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));

        Assert.False(arr.IsNull(0));
        Assert.False(arr.IsNull(1)); // an empty list is NOT a null list
        Assert.True(arr.IsNull(2)); // a null list
        Assert.False(arr.IsNull(3));

        Assert.Equal(2, arr.ElementLength(0));
        Assert.Equal(0, arr.ElementLength(1)); // empty
        Assert.Equal(0, arr.ElementLength(2)); // null contributes no elements
        Assert.Equal(2, arr.ElementLength(3));

        ColumnVector e0 = arr.ElementsAt(0);
        Assert.Equal(10, e0.GetValue<int>(0));
        Assert.Equal(20, e0.GetValue<int>(1));

        ColumnVector e3 = arr.ElementsAt(3);
        Assert.Equal(40, e3.GetValue<int>(0));
        Assert.True(e3.IsNull(1)); // a null element inside a present list
    }

    [Fact]
    public async Task Array_OfStrings_DecodesAndNullElement()
    {
        var rows = new List<StrListRow>
        {
            new() { Id = 1, Names = new List<string?> { "a", "b" } },
            new() { Id = 2, Names = null },
            new() { Id = 3, Names = new List<string?> { "c", null } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Names", DataTypes.CreateArrayType(DataTypes.StringType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var names = Assert.IsType<ListColumnVector>(batch.Column("Names"));

        Assert.Equal("a", Utf8(names.ElementsAt(0), 0));
        Assert.Equal("b", Utf8(names.ElementsAt(0), 1));
        Assert.True(names.IsNull(1));

        ColumnVector e3 = names.ElementsAt(2);
        Assert.Equal("c", Utf8(e3, 0));
        Assert.True(e3.IsNull(1));
    }

    [Fact]
    public async Task Map_ReadsEntries_WithEmptyNullMapAndNullValue()
    {
        var rows = new List<MapRow>
        {
            new() { Id = 1, M = new Dictionary<string, int?>(StringComparer.Ordinal) { ["k1"] = 100, ["k2"] = 200 } },
            new() { Id = 2, M = new Dictionary<string, int?>(StringComparer.Ordinal) },
            new() { Id = 3, M = null },
            new() { Id = 4, M = new Dictionary<string, int?>(StringComparer.Ordinal) { ["k4"] = null } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "M",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var m = Assert.IsType<MapColumnVector>(batch.Column("M"));

        Assert.False(m.IsNull(0));
        Assert.False(m.IsNull(1)); // an empty map is NOT a null map
        Assert.True(m.IsNull(2)); // a null map
        Assert.False(m.IsNull(3));

        Assert.Equal(2, m.EntryLength(0));
        Assert.Equal(0, m.EntryLength(1));
        Assert.Equal(0, m.EntryLength(2));
        Assert.Equal(1, m.EntryLength(3));

        // Map entry ordering is not part of the contract, so assert entries as a set.
        Dictionary<string, int?> entries0 = ReadIntMap(m, 0);
        Assert.Equal(100, entries0["k1"]);
        Assert.Equal(200, entries0["k2"]);

        ColumnVector k3 = m.KeysAt(3);
        ColumnVector v3 = m.ValuesAt(3);
        Assert.Equal("k4", Utf8(k3, 0));
        Assert.True(v3.IsNull(0)); // present key, null value
    }

    [Fact]
    public async Task Map_OfStringToString_DecodesValuesAndNull()
    {
        var rows = new List<StrMapRow>
        {
            new()
            {
                Id = 1,
                Sm = new Dictionary<string, string?>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = null },
            },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Sm",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.StringType, valueContainsNull: true),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var sm = Assert.IsType<MapColumnVector>(batch.Column("Sm"));
        Assert.Equal(2, sm.EntryLength(0));

        ColumnVector keys = sm.KeysAt(0);
        ColumnVector vals = sm.ValuesAt(0);
        var read = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int i = 0; i < 2; i++)
        {
            read[Utf8(keys, i)] = vals.IsNull(i) ? null : Utf8(vals, i);
        }

        Assert.Equal("1", read["a"]);
        Assert.Null(read["b"]);
    }

    [Fact]
    public async Task Map_MismatchedValueRepetition_FailsClosed_CorruptData()
    {
        // F1 (Critical, red-team): a crafted 3-level map whose VALUE repetition stream disagrees with the
        // KEY's — same TOTAL entry count (4), different per-row distribution — must fail closed, never
        // silently mis-pair values across rows/keys. The reader consumes the value child positionally against
        // the key-driven offsets, so before the fix this decoded WITHOUT error as {10:100,20:200},{30:300,
        // 40:400} even though the value stream [0,1,1,0] declares row0=3 values / row1=1 value.
        //   key   rep [0,1,0,1] => row0{k10,k20}, row1{k30,k40}
        //   value rep [0,1,1,0] => row0 3 values, row1 1 value  (DIVERGENT, equal total)
        byte[] bytes = await ParquetTestHelpers.WriteIntMapWithRepLevelsAsync(
            ids: new int?[] { 1, 2 },
            keys: new int?[] { 10, 20, 30, 40 }, keyRep: new[] { 0, 1, 0, 1 },
            values: new int?[] { 100, 200, 300, 400 }, valueRep: new[] { 0, 1, 1, 0 });

        var requested = new StructType(new[]
        {
            new StructField(
                "M",
                DataTypes.CreateMapType(DataTypes.IntegerType, DataTypes.IntegerType, valueContainsNull: true),
                nullable: true),
        });

        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("repetition levels diverge", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Map_MatchingValueRepetition_DecodesCorrectly()
    {
        // F1 regression: a well-formed low-level-authored map (matching key/value reps [0,1,0,1], INCLUDING a
        // null value) must still decode — the rep-equality guard accepts a valid shared-group repetition
        // stream, proving it does not false-positive on the same low-level authoring door the crafted test
        // uses. (Empty-map / null-map coverage is in Map_ReadsEntries_WithEmptyNullMapAndNullValue.)
        byte[] bytes = await ParquetTestHelpers.WriteIntMapWithRepLevelsAsync(
            ids: new int?[] { 1, 2 },
            keys: new int?[] { 10, 20, 30, 40 }, keyRep: new[] { 0, 1, 0, 1 },
            values: new int?[] { 100, null, 300, 400 }, valueRep: new[] { 0, 1, 0, 1 });

        var requested = new StructType(new[]
        {
            new StructField(
                "M",
                DataTypes.CreateMapType(DataTypes.IntegerType, DataTypes.IntegerType, valueContainsNull: true),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var m = Assert.IsType<MapColumnVector>(batch.Column("M"));

        Assert.Equal(2, m.EntryLength(0));
        Assert.Equal(2, m.EntryLength(1));

        Assert.Equal(100, ReadIntMapEntry(m, row: 0, key: 10));
        Assert.Null(ReadIntMapEntry(m, row: 0, key: 20)); // present key, null value
        Assert.Equal(300, ReadIntMapEntry(m, row: 1, key: 30));
        Assert.Equal(400, ReadIntMapEntry(m, row: 1, key: 40));
    }

    [Fact]
    public void ValidateParallelDefinition_RejectsEntryPresenceDisagreement_CorruptData()
    {
        // R6 (Critical, red-team): the DEFINITION-level analog of the R4 map rep-parity guard. A crafted map
        // where key and value DEF disagree on which slots are present entries — passing rep-parity and
        // level-range — must fail closed, never silently mis-pair values. mapMaxDef = 2 (key required, its max
        // def == the map's own level): keyDef=[2,1] says slot0 is a present entry and slot1 an empty-map
        // placeholder; valueDef=[1,2] says the opposite. Front-filling the value child from slot1 would then
        // pair value(slot1) with key(slot0). This crafted stream is not authorable via the released
        // Parquet.Net write door (definition levels are derived from value nullability), so the guard is pinned
        // by a direct unit test of the now-internal helper.
        DeltaStorageException mismatch = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateParallelDefinition(
                keyDef: new[] { 2, 1 }, valueDef: new[] { 1, 2 }, mapMaxDef: 2, "col"));
        Assert.Equal(StorageErrorKind.CorruptData, mismatch.Kind);
        Assert.Contains("disagree on entry presence", mismatch.Message, StringComparison.Ordinal);

        // A length disagreement (key and value declare different slot counts) also fails closed.
        DeltaStorageException lengths = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateParallelDefinition(
                keyDef: new[] { 2, 2 }, valueDef: new[] { 2 }, mapMaxDef: 2, "col"));
        Assert.Equal(StorageErrorKind.CorruptData, lengths.Kind);
    }

    [Fact]
    public void ValidateParallelDefinition_RejectsContainerStateDisagreement_CorruptData()
    {
        // R7 (Critical, red-team): the container-state sub-case of the map def contract. When BOTH key and
        // value def sit BELOW mapMaxDef the slot is a non-entry placeholder — but the SPECIFIC state must still
        // agree: null-map (def 0) vs empty-map (def 1). Entry-presence parity passes (both "not present"), yet
        // the file is self-contradictory (key says empty, value says null). A decoder of untrusted input must
        // fail closed here rather than silently resolve it to the key's authoritative view. mapMaxDef = 2.
        DeltaStorageException emptyVsNull = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateParallelDefinition(
                keyDef: new[] { 1 }, valueDef: new[] { 0 }, mapMaxDef: 2, "col")); // key empty, value null
        Assert.Equal(StorageErrorKind.CorruptData, emptyVsNull.Kind);
        Assert.Contains("disagree on container state", emptyVsNull.Message, StringComparison.Ordinal);

        DeltaStorageException nullVsEmpty = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateParallelDefinition(
                keyDef: new[] { 0 }, valueDef: new[] { 1 }, mapMaxDef: 2, "col")); // key null, value empty
        Assert.Equal(StorageErrorKind.CorruptData, nullVsEmpty.Kind);
        Assert.Contains("disagree on container state", nullVsEmpty.Message, StringComparison.Ordinal);

        // Mixed: a valid present entry followed by a contradictory non-entry slot still fails closed.
        DeltaStorageException mixed = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateParallelDefinition(
                keyDef: new[] { 2, 1 }, valueDef: new[] { 2, 0 }, mapMaxDef: 2, "col"));
        Assert.Equal(StorageErrorKind.CorruptData, mixed.Kind);
        Assert.Contains("container state", mixed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateParallelDefinition_AcceptsWellFormedStreams_NoOverRejection()
    {
        // R6/R7 no-over-rejection: every VALID key/value definition combination still passes. A present value
        // may carry a HIGHER def than the required key (a nullable value: def 3 present vs def 2 null, both >=
        // mapMaxDef 2), and empty-map (def 1) / null-map (def 0) placeholders agree EXACTLY on both leaves.
        // Two present entries, the second value present-but-null (valueDef 2, still an entry).
        NestedParquetColumnReader.ValidateParallelDefinition(
            keyDef: new[] { 2, 2 }, valueDef: new[] { 3, 2 }, mapMaxDef: 2, "col");
        // Empty map (both placeholders at def 1) and null map (both at def 0) — container states match exactly.
        NestedParquetColumnReader.ValidateParallelDefinition(new[] { 1 }, new[] { 1 }, mapMaxDef: 2, "col");
        NestedParquetColumnReader.ValidateParallelDefinition(new[] { 0 }, new[] { 0 }, mapMaxDef: 2, "col");
        // Mixed rows — present entry, empty map, null map — all agreeing slot-by-slot on presence AND state.
        NestedParquetColumnReader.ValidateParallelDefinition(
            keyDef: new[] { 2, 1, 0 }, valueDef: new[] { 3, 1, 0 }, mapMaxDef: 2, "col");
        // Null level arrays are vacuously parallel (defensive; real map leaves always carry def streams).
        NestedParquetColumnReader.ValidateParallelDefinition(null, null, mapMaxDef: 2, "col");
    }

    [Fact]
    public async Task StructField_RepeatedScalarLeaf_FailsClosed_CorruptData()
    {
        // R8 (High, red-team): a struct whose scalar field 'A' is a 1-level repeated primitive — its FILE leaf
        // declares MaxRepetitionLevel=1 (structurally an array), not 0. ReadStructAsync discards a field's
        // repetition stream, so without the leaf-structural-level guard the leaf's two element occurrences
        // [10,20] (one real row, rep [0,1]) would masquerade as two struct rows. The reader must reject the
        // repeated leaf at shape resolution — BEFORE any reconstruction — as it contradicts the requested
        // struct-scalar shape. Authorable end-to-end via the low-level writer (ParquetSerializer only emits
        // 3-level lists, which are caught earlier as "file column is itself nested").
        byte[] bytes = await ParquetTestHelpers.WriteStructWithRepeatedFieldAsync(
            new int?[] { 1 }, new int?[] { 10, 20 }, new[] { 0, 1 });
        var requested = new StructType(new[]
        {
            new StructField(
                "S",
                DataTypes.CreateStructType(new[] { DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: true) }),
                nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        Assert.Contains("max repetition level", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructField_RepeatedScalarLeaf_ForgedRowCount_FailsClosed_CorruptData()
    {
        // R8: the full masquerade — the repeated leaf's two element values PLUS a footer NumRows forged from
        // 1 to 2, so the flat "numValues == rowCount" struct-field check would otherwise pass and yield two
        // phantom struct rows. The leaf-structural-level guard fires at shape resolution, BEFORE the row-count
        // logic, closing the masquerade regardless of the forged count.
        byte[] bytes = await ParquetTestHelpers.WriteStructWithRepeatedFieldAsync(
            new int?[] { 1 }, new int?[] { 10, 20 }, new[] { 0, 1 });
        byte[] forged = await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(bytes, rowGroup: 0, forgedNumRows: 2);
        var requested = new StructType(new[]
        {
            new StructField(
                "S",
                DataTypes.CreateStructType(new[] { DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: true) }),
                nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(forged, requested));
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        Assert.Contains("max repetition level", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLeafStructuralLevels_RejectsWrongRepetition_CorruptData()
    {
        // R8 unit pin: a leaf whose MaxRepetitionLevel contradicts its navigated position fails closed. The
        // list/map positions (expected maxRep 1) can't be authored with a wrong-maxRep leaf end-to-end
        // (Parquet.Net's ListField/MapField ctors force element/key/value maxRep=1), so the guard is pinned
        // directly. A repeated primitive (isArray -> maxRep 1) at a struct-field position (expects 0):
        var repeatedLeaf = new global::Parquet.Schema.DataField("x", typeof(int), isArray: true); // maxRep 1
        DeltaStorageException struApt = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateLeafStructuralLevels(
                repeatedLeaf, expectedMaxRepetition: 0, containerMaxDef: 0, "struct field 'x'"));
        Assert.Equal(StorageErrorKind.CorruptData, struApt.Kind);
        Assert.Contains("max repetition level", struApt.Message, StringComparison.Ordinal);

        // A non-repeated primitive (maxRep 0) at a list-element / map-key/value position (expects 1):
        var scalarLeaf = new global::Parquet.Schema.DataField("x", typeof(int)); // maxRep 0
        DeltaStorageException listApt = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateLeafStructuralLevels(
                scalarLeaf, expectedMaxRepetition: 1, containerMaxDef: 0, "array column 'x' element"));
        Assert.Equal(StorageErrorKind.CorruptData, listApt.Kind);
        Assert.Contains("max repetition level", listApt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLeafStructuralLevels_RejectsWrongDefinition_CorruptData()
    {
        // R8 unit pin: a leaf whose MaxDefinitionLevel sits outside [containerMaxDef, containerMaxDef+1] fails
        // closed (a phantom optional/repeated ancestor, or fewer than its own container's) — it would shift
        // the null-classification thresholds. Real leaves with known levels from a map schema: key (maxDef 2),
        // value (maxDef 3), both maxRep 1.
        global::Parquet.Schema.DataField[] mapLeaves = MapLeaves();
        global::Parquet.Schema.DataField valueLeaf = mapLeaves[1]; // maxRep 1, maxDef 3

        // maxDef 3 ABOVE a container whose own level is 1 (would allow at most 2): reject.
        DeltaStorageException above = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateLeafStructuralLevels(
                valueLeaf, expectedMaxRepetition: 1, containerMaxDef: 1, "array column 'x' element"));
        Assert.Equal(StorageErrorKind.CorruptData, above.Kind);
        Assert.Contains("max definition level", above.Message, StringComparison.Ordinal);

        // maxDef 0 BELOW a container whose own level is 2 (impossible: a leaf can't have fewer levels than its
        // parent): reject.
        var scalarLeaf = new global::Parquet.Schema.DataField("x", typeof(int)); // maxRep 0, maxDef 0
        DeltaStorageException below = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateLeafStructuralLevels(
                scalarLeaf, expectedMaxRepetition: 0, containerMaxDef: 2, "struct field 'x'"));
        Assert.Equal(StorageErrorKind.CorruptData, below.Kind);
        Assert.Contains("max definition level", below.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLeafStructuralLevels_AcceptsValidPositions_NoOverRejection()
    {
        // R8 no-over-rejection: every VALID single-level position passes, with nullability advisory (both the
        // required = containerMaxDef and optional = containerMaxDef+1 definition levels accepted).
        global::Parquet.Schema.DataField[] mapLeaves = MapLeaves();
        global::Parquet.Schema.DataField keyLeaf = mapLeaves[0];   // maxRep 1, maxDef 2 (required)
        global::Parquet.Schema.DataField valueLeaf = mapLeaves[1]; // maxRep 1, maxDef 3 (optional)
        var scalarLeaf = new global::Parquet.Schema.DataField("x", typeof(int)); // maxRep 0, maxDef 0

        // Struct required scalar field: maxRep 0, maxDef 0 == containerMaxDef 0.
        NestedParquetColumnReader.ValidateLeafStructuralLevels(scalarLeaf, 0, 0, "struct field 'x'");
        // Map key (required): maxRep 1, maxDef 2 == containerMaxDef 2.
        NestedParquetColumnReader.ValidateLeafStructuralLevels(keyLeaf, 1, 2, "map column 'x' key");
        // Map value (optional): maxRep 1, maxDef 3 == containerMaxDef 2 + 1.
        NestedParquetColumnReader.ValidateLeafStructuralLevels(valueLeaf, 1, 2, "map column 'x' value");
        // A repeated leaf used as a required list element (maxDef == containerMaxDef): maxRep 1, maxDef 2.
        NestedParquetColumnReader.ValidateLeafStructuralLevels(keyLeaf, 1, 2, "array column 'x' element");
    }

    private static global::Parquet.Schema.DataField[] MapLeaves()
    {
        var mapSchema = new global::Parquet.Schema.ParquetSchema(
            new global::Parquet.Schema.MapField(
                "M",
                new global::Parquet.Schema.DataField<int>("key"),
                new global::Parquet.Schema.DataField<int?>("value")));
        return mapSchema.GetDataFields(); // [M/key (RL 1, DL 2), M/value (RL 1, DL 3)]
    }

    [Fact]
    public async Task NestedLeafDecodeCeiling_FoldsReconstructedChild_FailsClosed()
    {
        // F2 (High, red-team): the eager-decode ceiling must bound the RAW leaf decode PLUS the reconstructed
        // #570 child ColumnVector, not the raw buffers alone. A list of 7000 nullable ints has an int element
        // leaf whose raw decode is 7000*(4 value + 4 def + 4 rep) = 84,000 bytes (< the 100,000-byte ceiling)
        // but whose raw+child is 7000*(12 + 4 value + 1 null-mask) = 119,000 bytes (> the ceiling). Without the
        // reconstruction fold it would pass; with it, the leaf is rejected before allocation.
        byte[] bytes = await WriteAsync(new List<ListRow>
        {
            new() { Id = 1, Arr = Enumerable.Range(0, 7000).Select(i => (int?)i).ToList() },
        });

        var requested = new StructType(new[]
        {
            new StructField("Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 100_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        // The LeafNumValues (per-leaf) guard fired — not the flat EnsureDecodeCeiling — proving the fold is in
        // the leaf ceiling: its message names the leaf and the raw+reconstruction overrun.
        Assert.Contains("Nested leaf", error.Message, StringComparison.Ordinal);
        Assert.Contains("eager decode would exceed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedLeafDecodeCeiling_FoldsVariableWidthChildPayload_FailsClosed()
    {
        // R5-F1 (High, red-team): for a VARIABLE-width leaf (string/binary) the reconstructed #570 child copies
        // the actual UTF-8 payload, not just the per-value handle — so the leaf ceiling must budget that
        // payload too. A 1000-element list of UNIQUE ~61-byte strings has an element leaf whose:
        //   raw + fixed-handle = 1000 * (16 handle + 4 def + 4 rep + 16 child-handle + 1 null-mask) = 41,000
        //     (< the 100,000-byte ceiling — WITHOUT the payload term the leaf passes),
        //   TotalUncompressedSize U ~= 65,000, so the child byte store (doubling) is budgeted at 2*U ~= 130,000
        //     (> the ceiling on its own) — WITH the payload term the leaf is rejected before allocation.
        // The flat EnsureDecodeCeiling (sum of leaf U ~= 65,000) still passes, so the LeafNumValues guard is
        // the gate (its "Nested leaf" message confirms which guard fired). Strings are unique so U reflects the
        // real payload (a dictionary-encoded repeat column is a separate, reader-wide residual).
        byte[] bytes = await WriteAsync(new List<StrListRow>
        {
            new()
            {
                Id = 1,
                Names = Enumerable.Range(0, 1000)
                    .Select(i => (string?)$"str-{i:D6}-{new string('x', 50)}").ToList(),
            },
        });

        var requested = new StructType(new[]
        {
            new StructField(
                "Names", DataTypes.CreateArrayType(DataTypes.StringType, containsNull: true), nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 100_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("Nested leaf", error.Message, StringComparison.Ordinal);
        Assert.Contains("eager decode would exceed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedLeafDecodeCeiling_FixedWidthUnaffectedByPayloadTerm_Decodes()
    {
        // R5-F1 regression: the variable-width payload term must NOT change fixed-width behavior. The same
        // 1000-element int list — raw+child = 1000 * (4 + 4 + 4 + 4 + 1) = 17,000 — decodes cleanly under the
        // very ceiling that rejects the string list above, proving the payload budget is variable-width only.
        byte[] bytes = await WriteAsync(new List<ListRow>
        {
            new() { Id = 1, Arr = Enumerable.Range(0, 1000).Select(i => (int?)i).ToList() },
        });

        var requested = new StructType(new[]
        {
            new StructField("Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 100_000));

        ColumnBatch batch = await ReadSingleAsync(reader, bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));
        Assert.Equal(1000, arr.ElementLength(0));
        Assert.Equal(0, arr.ElementsAt(0).GetValue<int>(0));
        Assert.Equal(999, arr.ElementsAt(0).GetValue<int>(999));
    }

    [Fact]
    public async Task NestedDecodeCeiling_AggregatesLeafBudgetsAcrossStructFields_FailsClosed()
    {
        // R8 (Critical, red-team): the eager-decode ceiling must bound a nested column's leaves CUMULATIVELY,
        // not each leaf independently. A struct<L:long, D:double, Flag:bool> over 2000 present rows reconstructs
        // three leaf children whose per-leaf raw+child budgets are ~42,000 / ~42,000 / ~14,000 bytes: each is
        // individually under a 60,000-byte ceiling (so a PER-LEAF-only check passes all three), but their
        // COMBINED peak (~98,000) exceeds it. The flat EnsureDecodeCeiling (sum of the leaves' raw
        // UncompressedBytes, ~34,000) also passes, so only the shared NestedDecodeBudget catches the overrun —
        // proving a wide struct can no longer allocate K x the ceiling.
        var rows = new List<WideRow>(2000);
        for (int i = 0; i < 2000; i++)
        {
            rows.Add(new WideRow { Id = i, W = new Wide { L = i, D = i * 1.5, Flag = (i & 1) == 0 } });
        }

        byte[] bytes = await WriteAsync(rows);

        StructType wideType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("L", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("D", DataTypes.DoubleType, nullable: false),
            DataTypes.CreateStructField("Flag", DataTypes.BooleanType, nullable: false),
        });
        var requested = new StructType(new[] { new StructField("W", wideType, nullable: true) });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 60_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("Nested leaf", error.Message, StringComparison.Ordinal);
        Assert.Contains("eager decode would exceed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedDecodeCeiling_StructWithinAggregateBudget_Decodes()
    {
        // R8 regression (no over-reject): the SAME struct<L,D,Flag> over 2000 rows decodes cleanly under a
        // ceiling comfortably above its cumulative ~98,000-byte reconstruction peak — the shared budget only
        // rejects the genuine aggregate overrun above, never a within-budget wide struct.
        var rows = new List<WideRow>(2000);
        for (int i = 0; i < 2000; i++)
        {
            rows.Add(new WideRow { Id = i, W = new Wide { L = i, D = i * 1.5, Flag = (i & 1) == 0 } });
        }

        byte[] bytes = await WriteAsync(rows);

        StructType wideType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("L", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("D", DataTypes.DoubleType, nullable: false),
            DataTypes.CreateStructField("Flag", DataTypes.BooleanType, nullable: false),
        });
        var requested = new StructType(new[] { new StructField("W", wideType, nullable: true) });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 400_000));

        ColumnBatch batch = await ReadSingleAsync(reader, bytes, requested);
        var w = Assert.IsType<StructColumnVector>(batch.Column("W"));
        Assert.Equal(2000, batch.RowCount);
        Assert.Equal(0L, w.Child("L").GetValue<long>(0));
        Assert.Equal(1999L, w.Child("L").GetValue<long>(1999));
        Assert.Equal(1999 * 1.5, w.Child("D").GetValue<double>(1999));
    }

    [Fact]
    public async Task NestedDecodeCeiling_ChargesListStructuralArrays_FailsClosed()
    {
        // R9 finding 1 (Critical, red-team): a list/map's OWN per-row structural arrays (offsets + null mask)
        // must be charged to the reconstruction budget too, not just the element leaf. 10,000 single-element
        // int lists have an element leaf budget of ~170,000 bytes (10,000 * 17) and a structural cost of
        // ~50,000 (10,000 * (4 offset + 1 null)); each is individually under a 180,000-byte ceiling (so the
        // element-leaf-only budget passes), but their combined ~220,000 exceeds it. The flat EnsureDecodeCeiling
        // (raw U ~40,000, and its (iii) structural bound ~50,000) also passes, so only charging the structure
        // to the shared budget catches the overrun.
        var rows = new List<ListRow>(10_000);
        for (int i = 0; i < 10_000; i++)
        {
            rows.Add(new ListRow { Id = i, Arr = new List<int?> { i } });
        }

        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField("Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 180_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("would exceed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedDecodeCeiling_SharesBudgetAcrossNestedColumns_FailsClosed()
    {
        // R9 finding 2 (Critical, red-team): the budget must be ONE per row-group read, shared across every
        // nested column — not a fresh ceiling per column. Two struct<L,D,Flag> columns over 2000 rows each
        // reconstruct ~98,000 bytes apiece; each is under a 110,000-byte ceiling (so a per-column budget passes
        // both), but their combined ~196,000 exceeds it. The flat EnsureDecodeCeiling (raw U of all six leaves
        // ~65,000) also passes, so only a shared row-group budget catches the combined overrun.
        var rows = new List<TwoStructRow>(2000);
        for (int i = 0; i < 2000; i++)
        {
            rows.Add(new TwoStructRow
            {
                Id = i,
                A = new Wide { L = i, D = i * 1.5, Flag = (i & 1) == 0 },
                B = new Wide { L = -i, D = i * 2.5, Flag = (i & 1) == 1 },
            });
        }

        byte[] bytes = await WriteAsync(rows);

        StructType wideType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("L", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("D", DataTypes.DoubleType, nullable: false),
            DataTypes.CreateStructField("Flag", DataTypes.BooleanType, nullable: false),
        });
        var requested = new StructType(new[]
        {
            new StructField("A", wideType, nullable: true),
            new StructField("B", wideType, nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 110_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("would exceed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedDecodeCeiling_TwoNestedColumnsWithinBudget_Decodes()
    {
        // R9 finding 2 regression (no over-reject): the SAME two struct columns over 2000 rows decode cleanly
        // under a ceiling above their combined ~196,000-byte reconstruction peak — the shared budget rejects
        // only a genuine combined overrun, never within-budget multi-column reads.
        var rows = new List<TwoStructRow>(2000);
        for (int i = 0; i < 2000; i++)
        {
            rows.Add(new TwoStructRow
            {
                Id = i,
                A = new Wide { L = i, D = i * 1.5, Flag = (i & 1) == 0 },
                B = new Wide { L = -i, D = i * 2.5, Flag = (i & 1) == 1 },
            });
        }

        byte[] bytes = await WriteAsync(rows);

        StructType wideType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("L", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("D", DataTypes.DoubleType, nullable: false),
            DataTypes.CreateStructField("Flag", DataTypes.BooleanType, nullable: false),
        });
        var requested = new StructType(new[]
        {
            new StructField("A", wideType, nullable: true),
            new StructField("B", wideType, nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 600_000));

        ColumnBatch batch = await ReadSingleAsync(reader, bytes, requested);
        Assert.Equal(2000, batch.RowCount);
        var a = Assert.IsType<StructColumnVector>(batch.Column("A"));
        var b = Assert.IsType<StructColumnVector>(batch.Column("B"));
        Assert.Equal(0L, a.Child("L").GetValue<long>(0));
        Assert.Equal(-1999L, b.Child("L").GetValue<long>(1999));
    }

    [Fact]
    public async Task ArrayOfStruct_FailsClosed_UnsupportedFeature()
    {
        // A nested type within a nested type (array-of-struct) is out of scope for #571: it must be rejected
        // deterministically before any decode, never read partially.
        StructType element =
            DataTypes.CreateStructType(new[] { DataTypes.CreateStructField("A", DataTypes.IntegerType) });
        var requested = new StructType(new[]
        {
            new StructField("X", DataTypes.CreateArrayType(element), nullable: true),
        });

        byte[] bytes = await WriteAsync(new List<StructRow> { new() { Id = 1, S = null } });
        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
    }

    [Fact]
    public async Task WrongContainerKind_FailsClosed_SchemaMismatch()
    {
        // The file column 'S' is physically a struct; requesting it as an array is a structural mismatch and
        // must fail closed rather than mis-decode.
        var rows = new List<StructRow> { new() { Id = 1, S = new Inner { A = 10, B = "x" } } };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField("S", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true),
        });

        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    [Fact]
    public async Task ListRow_ForgedNumRows_FailsClosed_BeforeOverCeilingAllocation()
    {
        // A1 (HIGH DoS): a forged footer NumRows must be rejected by the eager-decode ceiling BEFORE the
        // rowCount-scaled offsets/nulls arrays are allocated. The nested container's per-row structural width
        // (int offset + bool null = 5 bytes) is folded into the first leaf's row-count bound, so a tiny file
        // claiming 50,000,000 rows fails closed without the ~250 MB allocation.
        byte[] bytes = await WriteAsync(new List<ListRow>
        {
            new() { Id = 1, Arr = new List<int?> { 1, 2 } },
            new() { Id = 2, Arr = new List<int?> { 3 } },
        });
        byte[] forged = await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(bytes, rowGroup: 0, forgedNumRows: 50_000_000);

        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });
        // A 4 MiB ceiling: 50,000,000 rows × 5 structural bytes = 250 MB ≫ ceiling, so the row-count bound
        // rejects it. The default 4 GiB ceiling only rejects rowCount > ~858M, so real row groups are unaffected.
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 4L * 1024 * 1024));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, forged, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        // The ceiling message (from EnsureDecodeCeiling, which runs BEFORE any per-column decode/allocation)
        // proves the rejection is PRE-allocation — not the post-allocation cross-check, which would also throw
        // CorruptData but only after the 250 MB offsets buffer had already been allocated.
        Assert.Contains("eager-decode ceiling", error.Message, StringComparison.Ordinal);
        Assert.Contains("5-byte column", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapRow_ForgedNumRows_FailsClosed_BeforeOverCeilingAllocation()
    {
        // A1 (HIGH DoS), map variant: the key leaf drives the entry structure and carries the folded 5-byte
        // structural width, so a forged NumRows is rejected before the map's offsets/nulls allocation.
        byte[] bytes = await WriteAsync(new List<MapRow>
        {
            new() { Id = 1, M = new Dictionary<string, int?>(StringComparer.Ordinal) { ["k1"] = 1, ["k2"] = 2 } },
        });
        byte[] forged = await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(bytes, rowGroup: 0, forgedNumRows: 50_000_000);

        var requested = new StructType(new[]
        {
            new StructField(
                "M",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true),
                nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 4L * 1024 * 1024));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, forged, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("eager-decode ceiling", error.Message, StringComparison.Ordinal);
        Assert.Contains("5-byte column", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructRow_ForgedNumRows_FailsClosed_BeforeOverCeilingAllocation()
    {
        // A1 (HIGH DoS), struct variant: a struct's per-row structural width is 1 byte (the null mask only —
        // no offsets), folded into the first field leaf's row-count bound. This pins the struct arm of
        // NestedContainerStructuralWidth (list/map assert "5-byte column"; struct must assert "1-byte column"),
        // which the list/map-only forge tests leave unexercised.
        byte[] bytes = await WriteAsync(new List<StructRow>
        {
            new() { Id = 1, S = new Inner { A = 1, B = "x" } },
            new() { Id = 2, S = new Inner { A = 2, B = "y" } },
        });
        byte[] forged = await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(bytes, rowGroup: 0, forgedNumRows: 50_000_000);

        StructType inner = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: false),
            DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[] { new StructField("S", inner, nullable: true) });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 4L * 1024 * 1024));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, forged, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        // Pre-allocation ceiling rejection (EnsureDecodeCeiling); "1-byte column" proves it is the struct
        // structural-width fold that fired, not a list/map path.
        Assert.Contains("eager-decode ceiling", error.Message, StringComparison.Ordinal);
        Assert.Contains("1-byte column", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nested_UnderIdMode_FailsClosed_UnsupportedFeature()
    {
        // A2: a nested column under column-mapping id mode is not supported (BuildFieldIdMap is flat/leaf-only).
        // Without the guard, the nested column would silently resolve BY NAME under id mode — a wrong read.
        byte[] bytes = await WriteAsync(new List<ListRow> { new() { Id = 1, Arr = new List<int?> { 1, 2 } } });
        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            await foreach (ColumnBatch _ in new ParquetFileReader().ReadAsync(
                stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
                resolveByFieldId: true, CancellationToken.None))
            {
            }
        });

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
    }

    [Fact]
    public async Task NestedLeafDecodeCeiling_FailsClosed_CorruptData()
    {
        // A3: the per-leaf eager-decode ceiling bounds a nested leaf's declared value count independently of
        // the row count. 20,000 element slots under a 120,000-byte ceiling clears the container's row-count
        // bound but must trip the leaf guard (the "Nested leaf" message confirms which guard fired).
        byte[] bytes = await WriteAsync(new List<ListRow>
        {
            new() { Id = 1, Arr = Enumerable.Range(0, 20_000).Select(i => (int?)i).ToList() },
        });
        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 120_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("Nested leaf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedLeafTypeMismatch_FailsClosed_SchemaMismatch()
    {
        // A4: a nested leaf whose physical type disagrees with the requested type must fail closed (no widening
        // for nested leaves — that is #546). File 'A' is int32; requesting it as long is a SchemaMismatch.
        byte[] bytes = await WriteAsync(new List<StructRow> { new() { Id = 1, S = new Inner { A = 10, B = "x" } } });
        StructType wrong = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[] { new StructField("S", wrong, nullable: true) });

        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    [Fact]
    public async Task NestedLeafDateTimestampConfusion_FailsClosed_SchemaMismatch()
    {
        // A4 (silent-corruption case): DATE and TIMESTAMP both decode as a CLR DateTime, so mis-reading one as
        // the other would land in the wrong epoch lane (day vs micros) with NO exception unless the physical-
        // type guard distinguishes the logical annotations. Both directions must fail closed.
        byte[] bytes = await WriteAsync(new List<DateRow>
        {
            new()
            {
                Id = 1,
                S = new DateInner
                {
                    D = new DateOnly(2024, 1, 2),
                    Ts = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    Dec = 1.5m,
                },
            },
        });

        // File 'D' is a DATE leaf; requesting it as TIMESTAMP must be rejected.
        var dateAsTimestamp = new StructType(new[]
        {
            new StructField(
                "S",
                DataTypes.CreateStructType(new[]
                    { DataTypes.CreateStructField("D", DataTypes.TimestampType, nullable: false) }),
                nullable: true),
        });
        DeltaStorageException e1 =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, dateAsTimestamp));
        Assert.Equal(StorageErrorKind.SchemaMismatch, e1.Kind);

        // File 'Ts' is a TIMESTAMP leaf; requesting it as DATE must be rejected.
        var timestampAsDate = new StructType(new[]
        {
            new StructField(
                "S",
                DataTypes.CreateStructType(new[]
                    { DataTypes.CreateStructField("Ts", DataTypes.DateType, nullable: false) }),
                nullable: true),
        });
        DeltaStorageException e2 =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, timestampAsDate));
        Assert.Equal(StorageErrorKind.SchemaMismatch, e2.Kind);
    }

    [Fact]
    public void ValidateLevelRange_RejectsOutOfRangeLevel_CorruptData()
    {
        // A5: an out-of-range Dremel level cannot be produced by a conforming writer, so the guard is unit-
        // tested directly. A def level above the leaf max would otherwise be silently coerced to a spurious
        // present-null (a WRONG read), so it must fail closed.
        DeltaStorageException over = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateLevelRange(new[] { 0, 1, 5 }, maxLevel: 3, "col.leaf", "definition"));
        Assert.Equal(StorageErrorKind.CorruptData, over.Kind);
        Assert.Contains("definition level 5", over.Message, StringComparison.Ordinal);

        // The unsigned compare also rejects a negative level.
        DeltaStorageException negative = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateLevelRange(new[] { -1 }, maxLevel: 2, "col.leaf", "repetition"));
        Assert.Equal(StorageErrorKind.CorruptData, negative.Kind);

        // In-range levels (including exactly maxLevel) and a null array are accepted with no throw.
        NestedParquetColumnReader.ValidateLevelRange(new[] { 0, 1, 2, 3 }, maxLevel: 3, "col.leaf", "definition");
        NestedParquetColumnReader.ValidateLevelRange(null, maxLevel: 3, "col.leaf", "definition");
    }

    [Fact]
    public void BuildRepeatedStructure_RejectsInvalidStateTransitions_CorruptData()
    {
        // R5-F2 (Critical, red-team): a structurally-invalid list Dremel stream that passes ValidateLevelRange
        // must fail closed rather than decode a phantom element. containerMaxDef = 2 for a standard optional
        // list-of-optional-element (element leaf maxDef 3): def 0 = null list, 1 = empty list, 2 = null
        // element, 3 = present element. These crafted streams cannot be authored via the released Parquet.Net
        // write door (definition levels are derived from value nullability, never below the element's own null
        // level), so the guard is pinned by a direct unit test of BuildRepeatedStructure.

        // Empty-list marker (def=1, rep=0 opening row0) then a continuation (rep=1) of that same row: a row
        // whose container is empty has NO element occurrence and must never be continued.
        DeltaStorageException emptyThenContinue = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildRepeatedStructure(
                def: new[] { 1, 2 }, rep: new[] { 0, 1 }, numValues: 2, containerMaxDef: 2, rowCount: 1,
                offsets: new int[2], nulls: new bool[1], columnName: "col"));
        Assert.Equal(StorageErrorKind.CorruptData, emptyThenContinue.Kind);
        Assert.Contains("has no continuation", emptyThenContinue.Message, StringComparison.Ordinal);

        // Present opener (def=3) then a continuation slot that is itself a sub-container placeholder (def=1 <
        // containerMaxDef) — a continuation must be a real element occurrence, not an empty/null marker.
        DeltaStorageException continuationIsMarker = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildRepeatedStructure(
                def: new[] { 3, 1 }, rep: new[] { 0, 1 }, numValues: 2, containerMaxDef: 2, rowCount: 1,
                offsets: new int[2], nulls: new bool[1], columnName: "col"));
        Assert.Equal(StorageErrorKind.CorruptData, continuationIsMarker.Kind);

        // A leading non-zero repetition level cannot open a row.
        DeltaStorageException leadingRep = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildRepeatedStructure(
                def: new[] { 3 }, rep: new[] { 1 }, numValues: 1, containerMaxDef: 2, rowCount: 1,
                offsets: new int[2], nulls: new bool[1], columnName: "col"));
        Assert.Equal(StorageErrorKind.CorruptData, leadingRep.Kind);
    }

    [Fact]
    public void BuildRepeatedStructure_AcceptsAllValidPermutations_NoOverRejection()
    {
        // R5-F2 no-over-rejection: every VALID single-level list permutation must still decode unchanged. The
        // guard rejects only genuine state-transition contradictions, not any well-formed null/empty/present
        // stream (containerMaxDef = 2).
        AssertRepeated(def: new[] { 3 }, rep: new[] { 0 }, rowCount: 1,
            expectedOffsets: new[] { 0, 1 }, expectedNulls: new[] { false }); // [42]
        AssertRepeated(def: new[] { 1 }, rep: new[] { 0 }, rowCount: 1,
            expectedOffsets: new[] { 0, 0 }, expectedNulls: new[] { false }); // [] empty
        AssertRepeated(def: new[] { 0 }, rep: new[] { 0 }, rowCount: 1,
            expectedOffsets: new[] { 0, 0 }, expectedNulls: new[] { true }); // null list
        AssertRepeated(def: new[] { 2 }, rep: new[] { 0 }, rowCount: 1,
            expectedOffsets: new[] { 0, 1 }, expectedNulls: new[] { false }); // [null] one null element
        AssertRepeated(def: new[] { 3, 3 }, rep: new[] { 0, 1 }, rowCount: 1,
            expectedOffsets: new[] { 0, 2 }, expectedNulls: new[] { false }); // [10,20]
        AssertRepeated(def: new[] { 3, 2 }, rep: new[] { 0, 1 }, rowCount: 1,
            expectedOffsets: new[] { 0, 2 }, expectedNulls: new[] { false }); // [10,null]
        AssertRepeated(def: new[] { 3, 1 }, rep: new[] { 0, 0 }, rowCount: 2,
            expectedOffsets: new[] { 0, 1, 1 }, expectedNulls: new[] { false, false }); // [10],[]
        AssertRepeated(def: new[] { 1, 3 }, rep: new[] { 0, 0 }, rowCount: 2,
            expectedOffsets: new[] { 0, 0, 1 }, expectedNulls: new[] { false, false }); // [],[10] (rowComplete reset)
        AssertRepeated(def: new[] { 0, 3, 1 }, rep: new[] { 0, 0, 0 }, rowCount: 3,
            expectedOffsets: new[] { 0, 0, 1, 1 }, expectedNulls: new[] { true, false, false }); // null,[10],[]
    }

    private static void AssertRepeated(
        int[] def, int[] rep, int rowCount, int[] expectedOffsets, bool[] expectedNulls)
    {
        var offsets = new int[rowCount + 1];
        var nulls = new bool[rowCount];
        int total = NestedParquetColumnReader.BuildRepeatedStructure(
            def, rep, def.Length, containerMaxDef: 2, rowCount, offsets, nulls, "col");
        Assert.Equal(expectedOffsets, offsets);
        Assert.Equal(expectedNulls, nulls);
        Assert.Equal(expectedOffsets[^1], total);
    }

    [Fact]
    public void BuildStructNullMask_RejectsCrossFieldDefDivergence_CorruptData()
    {
        // R5-F2 (Critical, red-team): a crafted struct Dremel stream where fields DISAGREE on the struct's
        // presence at the same row must fail closed rather than decode a phantom field under a null struct.
        // structMaxDef = 1 (optional struct; required field A maxDef 1, optional field B maxDef 2): field def
        // < 1 means the struct is absent. Such divergent streams cannot be authored via the released write door
        // (definition levels are derived from value nullability), so the guard is pinned by a direct unit test.

        // Field A says "null struct" (def 0) while field B says "present" (def 1) at the same row.
        int[]?[] aNullBPresent = { new[] { 0 }, new[] { 1 } };
        DeltaStorageException e1 = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildStructNullMask(aNullBPresent, structMaxDef: 1, rowCount: 1, "col"));
        Assert.Equal(StorageErrorKind.CorruptData, e1.Kind);
        Assert.Contains("disagree on the struct's presence", e1.Message, StringComparison.Ordinal);

        // The reverse divergence (A present, B null-struct) is caught either driving direction.
        int[]?[] aPresentBNull = { new[] { 1 }, new[] { 0 } };
        DeltaStorageException e2 = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildStructNullMask(aPresentBNull, structMaxDef: 1, rowCount: 1, "col"));
        Assert.Equal(StorageErrorKind.CorruptData, e2.Kind);
    }

    [Fact]
    public void BuildStructNullMask_AcceptsAgreeingFields_NoOverRejection()
    {
        // R5-F2 no-over-rejection: every VALID struct permutation still yields the correct null mask. The guard
        // rejects only genuine cross-field divergence, not a present struct with a null field.
        // Null struct (both fields def 0).
        Assert.Equal(new[] { true }, NestedParquetColumnReader.BuildStructNullMask(
            new int[]?[] { new[] { 0 }, new[] { 0 } }, structMaxDef: 1, rowCount: 1, "col"));

        // Present struct, field B null (A def 1 present, B def 1 struct-present-field-null) — fields agree.
        Assert.Equal(new[] { false }, NestedParquetColumnReader.BuildStructNullMask(
            new int[]?[] { new[] { 1 }, new[] { 1 } }, structMaxDef: 1, rowCount: 1, "col"));

        // Present struct, both fields present (A def 1, B def 2).
        Assert.Equal(new[] { false }, NestedParquetColumnReader.BuildStructNullMask(
            new int[]?[] { new[] { 1 }, new[] { 2 } }, structMaxDef: 1, rowCount: 1, "col"));

        // Multi-row: row0 present (A 1, B 2), row1 null (A 0, B 0) — per-row agreement.
        Assert.Equal(new[] { false, true }, NestedParquetColumnReader.BuildStructNullMask(
            new int[]?[] { new[] { 1, 0 }, new[] { 2, 0 } }, structMaxDef: 1, rowCount: 2, "col"));

        // A required struct (structMaxDef 0) has no null mask.
        Assert.Null(NestedParquetColumnReader.BuildStructNullMask(
            new int[]?[] { new[] { 0 } }, structMaxDef: 0, rowCount: 1, "col"));
    }

    [Fact]
    public async Task ZeroFieldStruct_FailsClosed_UnsupportedFeature()
    {
        // A6: a zero-field struct reconstructs a length-0 vector and (pre-fix) surfaced a raw ArgumentException
        // from the batch ctor rather than the DeltaStorageException contract. Reject it fail-closed.
        byte[] bytes = await WriteAsync(new List<StructRow> { new() { Id = 1, S = new Inner { A = 1, B = "x" } } });
        var requested = new StructType(new[]
        {
            new StructField("S", DataTypes.CreateStructType(Array.Empty<StructField>()), nullable: true),
        });

        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
    }

    [Fact]
    public async Task Array_EmptyListAdjacentToNullElementList_DisambiguatesLevels()
    {
        // arch adjacency trap: an EMPTY list [] immediately followed by a list holding a single NULL element
        // [null] must decode to distinct shapes — 0 elements vs 1 present-but-null element — even though a
        // naive length delta would conflate them.
        var rows = new List<ListRow>
        {
            new() { Id = 1, Arr = new List<int?>() },
            new() { Id = 2, Arr = new List<int?> { null } },
            new() { Id = 3, Arr = new List<int?> { 7 } },
        };
        byte[] bytes = await WriteAsync(rows);
        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));

        Assert.False(arr.IsNull(0));
        Assert.Equal(0, arr.ElementLength(0)); // [] — empty, not null

        Assert.False(arr.IsNull(1));
        Assert.Equal(1, arr.ElementLength(1)); // [null] — one present-but-null element
        Assert.True(arr.ElementsAt(1).IsNull(0));

        Assert.Equal(1, arr.ElementLength(2));
        Assert.Equal(7, arr.ElementsAt(2).GetValue<int>(0));
    }

    [Fact]
    public async Task Struct_NullStructAdjacentToPresentAllNullFields_Disambiguated()
    {
        // arch adjacency trap: a NULL struct (the whole struct absent) vs a PRESENT struct whose every field is
        // null must decode to distinct struct-level null masks — IsNull(struct) true vs false — even though both
        // leave all child leaves null.
        var rows = new List<AllNullableRow>
        {
            new() { Id = 1, S = null },
            new() { Id = 2, S = new AllNullableInner { A = null, B = null } },
        };
        byte[] bytes = await WriteAsync(rows);
        StructType st = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: true),
            DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[] { new StructField("S", st, nullable: true) });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));

        Assert.True(s.IsNull(0)); // null struct
        Assert.True(s.Child("A").IsNull(0));
        Assert.True(s.Child("B").IsNull(0));

        Assert.False(s.IsNull(1)); // present struct with all-null fields
        Assert.True(s.Child("A").IsNull(1));
        Assert.True(s.Child("B").IsNull(1));
    }

    [Fact]
    public async Task ZeroRowFile_NestedColumn_YieldsNoRows()
    {
        // quality zero-row-file edge: a nested column in a file with no rows must decode to zero rows without
        // error (no batch, or an empty batch — both acceptable).
        byte[] bytes = await WriteAsync(new List<ListRow>());
        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });

        using var stream = new MemoryStream(bytes, writable: false);
        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
            stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Equal(0, batches.Sum(b => b.RowCount));
        foreach (ColumnBatch batch in batches)
        {
            Assert.Equal(0, Assert.IsType<ListColumnVector>(batch.Column("Arr")).Length);
        }
    }

    [Fact]
    public async Task AllNullListColumn_DecodesAllNull()
    {
        // quality all-null-column edge: every row's list is null. The column decodes to all-null with zero
        // elements — distinct from all-empty (asserted elsewhere).
        var rows = new List<ListRow>
        {
            new() { Id = 1, Arr = null },
            new() { Id = 2, Arr = null },
            new() { Id = 3, Arr = null },
        };
        byte[] bytes = await WriteAsync(rows);
        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));

        Assert.Equal(3, arr.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(arr.IsNull(i));
            Assert.Equal(0, arr.ElementLength(i));
        }

        Assert.Equal(0, arr.ElementsAt(0).Length); // no elements materialized at all
    }

    [Fact]
    public async Task Struct_DecodesDateTimestampDecimalLeaves()
    {
        // A7: nested-leaf coverage for the highest-value untested conversions — DATE (epoch-day int lane),
        // TIMESTAMP (epoch-micros long lane), and DECIMAL (unscaled reconstruction) — plus a null struct row.
        var date = new DateOnly(2024, 1, 2);
        var timestamp = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var rows = new List<DateRow>
        {
            new() { Id = 1, S = new DateInner { D = date, Ts = timestamp, Dec = 12.3400m } },
            new() { Id = 2, S = null },
        };
        byte[] bytes = await WriteAsync(rows);

        DecimalType decimalType = DataTypes.CreateDecimalType(18, 4);
        StructType st = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("D", DataTypes.DateType, nullable: false),
            DataTypes.CreateStructField("Ts", DataTypes.TimestampType, nullable: false),
            DataTypes.CreateStructField("Dec", decimalType, nullable: false),
        });
        var requested = new StructType(new[] { new StructField("S", st, nullable: true) });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));

        int expectedEpochDay = date.DayNumber - new DateOnly(1970, 1, 1).DayNumber;
        Assert.Equal(expectedEpochDay, s.Child("D").GetValue<int>(0));

        long expectedMicros = (timestamp.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;
        Assert.Equal(expectedMicros, s.Child("Ts").GetValue<long>(0));

        Assert.Equal(12.3400m, ParquetTypeMapping.ReadDecimal(s.Child("Dec"), decimalType, 0));

        Assert.True(s.IsNull(1)); // null struct → all leaves null
        Assert.True(s.Child("D").IsNull(1));
        Assert.True(s.Child("Ts").IsNull(1));
        Assert.True(s.Child("Dec").IsNull(1));
    }

    [Fact]
    public async Task NestedWithinNested_PresentColumn_FailsClosed_DecodePathGuard()
    {
        // A8: request a PRESENT array<int> against a file column that is actually array<struct>. The requested
        // scalar-element array clears the front-line EnsureReadSupported, so the rejection comes from the
        // decode-path shape guard ("the file column is itself nested") — proving that guard is exercised, not
        // shadowed by the front line (as the absent-column ArrayOfStruct_FailsClosed case is).
        var rows = new List<NestedListRow>
        {
            new() { Id = 1, Items = new List<Inner> { new() { A = 1, B = "x" } } },
        };
        byte[] bytes = await WriteAsync(rows);
        var requested = new StructType(new[]
        {
            new StructField(
                "Items", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });

        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("file column is itself nested", error.Message, StringComparison.Ordinal);
    }

    private static async Task<byte[]> WriteAsync<T>(IReadOnlyList<T> rows)
        where T : class, new()
    {
        using var stream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, stream, cancellationToken: CancellationToken.None);
        return stream.ToArray();
    }

    private static async Task EnumerateAsync(ParquetFileReader reader, byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await foreach (ColumnBatch _ in reader.ReadAsync(
            stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
        }
    }

    private static async Task<ColumnBatch> ReadSingleAsync(byte[] bytes, StructType requested) =>
        await ReadSingleAsync(new ParquetFileReader(), bytes, requested);

    private static async Task<ColumnBatch> ReadSingleAsync(
        ParquetFileReader reader, byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch batch in reader.ReadAsync(
            stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
            Assert.Null(only); // the serializer writes a single row group for these small inputs
            only = batch;
        }

        Assert.NotNull(only);
        return only!;
    }

    private static Dictionary<string, int?> ReadIntMap(MapColumnVector map, int row)
    {
        ColumnVector keys = map.KeysAt(row);
        ColumnVector values = map.ValuesAt(row);
        var result = new Dictionary<string, int?>(StringComparer.Ordinal);
        for (int i = 0; i < map.EntryLength(row); i++)
        {
            result[Utf8(keys, i)] = values.IsNull(i) ? null : values.GetValue<int>(i);
        }

        return result;
    }

    // Reads the value for <paramref name="key"/> in an int→int map row (null when the value cell is null),
    // asserting the key is present. Map entry ordering is not part of the contract, so the entry is located
    // by key rather than by position.
    private static int? ReadIntMapEntry(MapColumnVector map, int row, int key)
    {
        ColumnVector keys = map.KeysAt(row);
        ColumnVector values = map.ValuesAt(row);
        for (int i = 0; i < map.EntryLength(row); i++)
        {
            if (keys.GetValue<int>(i) == key)
            {
                return values.IsNull(i) ? null : values.GetValue<int>(i);
            }
        }

        throw new KeyNotFoundException($"key {key} not found in map row {row}");
    }

    private static string Utf8(ColumnVector vector, int index) => Encoding.UTF8.GetString(vector.GetBytes(index));
}
