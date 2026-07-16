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

    private static async Task<byte[]> WriteAsync<T>(IReadOnlyList<T> rows)
        where T : class, new()
    {
        using var stream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, stream, cancellationToken: CancellationToken.None);
        return stream.ToArray();
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
