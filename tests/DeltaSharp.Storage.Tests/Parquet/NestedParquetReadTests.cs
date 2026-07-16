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

    private static async Task<ColumnBatch> ReadSingleAsync(byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
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

    private static string Utf8(ColumnVector vector, int index) => Encoding.UTF8.GetString(vector.GetBytes(index));
}
