using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Storage.Tests.Parquet.NestedValueModel;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// #873 §3.3 nested-within-nested WRITE happy-path oracle: the recursive shredder writes a real Parquet file
/// via <see cref="ParquetFileWriter"/>, the shipped 585a reader decodes it, and the test asserts value +
/// null-structure identity at EVERY level (null container vs empty container vs null element/field, present).
/// Same-typed sibling leaves draw from disjoint value domains so a positional mis-bind cannot pass on equal
/// values.
/// </summary>
public sealed class NestedWithinNestedWriteTests
{
    private static readonly StructType StructAB = DataTypes.CreateStructType(new[]
    {
        new StructField("a", DataTypes.IntegerType, nullable: true),
        new StructField("b", DataTypes.StringType, nullable: true),
    });

    private static readonly StructType InnerAL = DataTypes.CreateStructType(new[]
    {
        new StructField("a", DataTypes.IntegerType, nullable: true),
        new StructField("b", DataTypes.LongType, nullable: true),
    });

    private static readonly ArrayType IntArray = DataTypes.CreateArrayType(DataTypes.IntegerType);
    private static readonly ArrayType LongArray = DataTypes.CreateArrayType(DataTypes.LongType);
    private static readonly MapType StringLongMap = DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType);

    // 1 — array<struct<a:int,b:string>>: null list / empty list / list with a null struct element / present
    // struct with a null field — all distinguished on read-back.
    [Fact]
    public Task Write_ArrayOfStruct_RoundTripsThrough585a()
    {
        ArrayType type = DataTypes.CreateArrayType(StructAB);
        var rows = new object?[]
        {
            null,
            Arr(),
            Arr(Struct(1, "one"), null, Struct(null, "three")),
            Arr(Struct(4, null)),
        };
        return RoundTripAsync(type, rows);
    }

    // 2 — struct<xs:array<long>, name:string>: null struct / present struct with null array / empty array /
    // present array with a null element.
    [Fact]
    public Task Write_StructOfArray_RoundTrips()
    {
        StructType type = DataTypes.CreateStructType(new[]
        {
            new StructField("xs", LongArray, nullable: true),
            new StructField("name", DataTypes.StringType, nullable: true),
        });
        var rows = new object?[]
        {
            null,
            Struct(null, "no-array"),
            Struct(Arr(), "empty"),
            Struct(Arr(10L, null, 30L), "present"),
        };
        return RoundTripAsync(type, rows);
    }

    // 3 — struct<inner:struct<a:int,b:long>, c:string>: nested null-struct parity across two optional struct
    // levels (no rep stream — pure-struct path).
    [Fact]
    public Task Write_StructOfStruct_RoundTrips()
    {
        StructType type = DataTypes.CreateStructType(new[]
        {
            new StructField("inner", InnerAL, nullable: true),
            new StructField("c", DataTypes.StringType, nullable: true),
        });
        var rows = new object?[]
        {
            null,
            Struct(null, "inner-null"),
            Struct(Struct(1, 2L), "present"),
            Struct(Struct(null, null), null),
        };
        return RoundTripAsync(type, rows);
    }

    // 4 — map<string, struct<a:int,b:long>>: null map / empty map / entry with a null value-struct / present
    // value-struct with a null field.
    [Fact]
    public Task Write_MapOfStruct_RoundTrips()
    {
        MapType type = DataTypes.CreateMapType(DataTypes.StringType, InnerAL);
        var rows = new object?[]
        {
            null,
            Map(),
            Map(("k1", Struct(1, 2L)), ("k2", null)),
            Map(("k3", Struct(null, 9L))),
        };
        return RoundTripAsync(type, rows);
    }

    // 5 — array<array<int>>: the four-way null taxonomy over TWO repeated levels (null outer / empty outer /
    // outer-of-null-or-empty-inner / present with a null inner element).
    [Fact]
    public Task Write_ArrayOfArray_RoundTrips()
    {
        ArrayType type = DataTypes.CreateArrayType(IntArray);
        var rows = new object?[]
        {
            null,
            Arr(),
            Arr(null, Arr()),
            Arr(Arr(7, null), Arr(9)),
        };
        return RoundTripAsync(type, rows);
    }

    // 6 — map<string, map<string,long>>: canonical key/value names at BOTH map levels; entry with a null
    // inner-map value.
    [Fact]
    public Task Write_MapOfMap_RoundTrips()
    {
        MapType type = DataTypes.CreateMapType(DataTypes.StringType, StringLongMap);
        var rows = new object?[]
        {
            null,
            Map(),
            Map(("outer1", Map(("in1", 1L), ("in2", 2L))), ("outer2", null), ("outer3", Map())),
            Map(("outer4", Map(("in3", 3L)))),
        };
        return RoundTripAsync(type, rows);
    }

    // 7a — array<map<string,long>>: mixed repeated nesting; the value/element leaf stream is longer than the
    // key/entry stream (key/value decouple, §2.10.5).
    [Fact]
    public Task Write_ArrayOfMap_RoundTrips()
    {
        ArrayType type = DataTypes.CreateArrayType(StringLongMap);
        var rows = new object?[]
        {
            null,
            Arr(),
            Arr(null, Map(), Map(("a", 1L), ("b", 2L))),
            Arr(Map(("c", 3L))),
        };
        return RoundTripAsync(type, rows);
    }

    // 7b — map<string, array<long>>: the value-side list adds a repeated level the key side does not have.
    [Fact]
    public Task Write_MapOfArray_RoundTrips()
    {
        MapType type = DataTypes.CreateMapType(DataTypes.StringType, LongArray);
        var rows = new object?[]
        {
            null,
            Map(),
            Map(("k1", null), ("k2", Arr()), ("k3", Arr(1L, null, 3L))),
            Map(("k4", Arr(9L))),
        };
        return RoundTripAsync(type, rows);
    }

    // 8 — array<struct<xs:array<int>>>: three-level def/rep emission (depth-3 recursion).
    [Fact]
    public Task Write_ArrayOfStructOfArray_RoundTrips()
    {
        StructType inner = DataTypes.CreateStructType(new[]
        {
            new StructField("xs", IntArray, nullable: true),
        });
        ArrayType type = DataTypes.CreateArrayType(inner);
        var rows = new object?[]
        {
            null,
            Arr(),
            Arr(null, Struct((object?)null), Struct(Arr())),
            Arr(Struct(Arr(1, null)), Struct(Arr(2, 3))),
        };
        return RoundTripAsync(type, rows);
    }

    // 9 — map<string, struct<m:map<string,long>>>: depth-3 map/struct/map mix.
    [Fact]
    public Task Write_MapOfStructOfMap_RoundTrips()
    {
        StructType inner = DataTypes.CreateStructType(new[]
        {
            new StructField("m", StringLongMap, nullable: true),
        });
        MapType type = DataTypes.CreateMapType(DataTypes.StringType, inner);
        var rows = new object?[]
        {
            null,
            Map(),
            Map(("e1", null), ("e2", Struct((object?)null)), ("e3", Struct(Map()))),
            Map(("e4", Struct(Map(("in", 5L))))),
        };
        return RoundTripAsync(type, rows);
    }

    // 22 — a legitimate DEEP nested chain writes and reads back (value + null structure), pinning that the
    // #873 write depth cap (MaxNestedWriteDepth = 64) does not over-reject a writable schema. The BINDING cap
    // for a pure container chain is actually the schemaString serializer (SchemaJson.MaxDepth = 64 JSON
    // CONTAINERS: an array is 1 container, and the top struct wrapper is 3), which caps a pure-array column at
    // ~61 arrays — strictly below the 64 TYPE levels MaxNestedWriteDepth admits. This exercises the recursive
    // shredder at the deepest depth the full write path actually accepts.
    [Fact]
    public async Task Write_AtMaxNestedWriteDepth_RoundTrips_Success()
    {
        // 60 nested arrays around an int leaf — the deepest pure-array chain that serializes to a schemaString
        // (3 struct-wrapper containers + 60 array containers = 63 <= 64). One present element per level, plus a
        // null and an empty at the outermost, so every Dremel branch fires at the boundary depth.
        const int arrayLevels = 60;
        DataType type = DataTypes.IntegerType;
        for (int i = 0; i < arrayLevels; i++)
        {
            type = DataTypes.CreateArrayType(type);
        }

        object Nest(object inner)
        {
            object current = inner;
            for (int i = 0; i < arrayLevels; i++)
            {
                current = Arr(current);
            }

            return current;
        }

        var rows = new object?[] { null, Nest(42), Arr() };
        await RoundTripAsync(type, rows);
    }

    // 23 — a deep/wide nested column whose recursive slot count exceeds one row group is SPLIT across row
    // groups (not rented past the addressable ceiling), pinning the §2.10.7 recursive RowSlots generalization.
    [Fact]
    public async Task Write_NestedSlotBudget_SplitsAcrossRowGroups_AtDepth()
    {
        ArrayType type = DataTypes.CreateArrayType(IntArray);
        var rows = new object?[16];
        for (int i = 0; i < rows.Length; i++)
        {
            // Each row is an outer list of 8 inner lists, each of 8 ints — 64 leaf slots/row. With a tiny
            // budget the planner must split across several row groups.
            var outer = new object?[8];
            for (int o = 0; o < 8; o++)
            {
                var innerItems = new object?[8];
                for (int e = 0; e < 8; e++)
                {
                    innerItems[e] = (i * 1000) + (o * 10) + e;
                }

                outer[o] = Arr(innerItems);
            }

            rows[i] = new ArrVal(outer);
        }

        var schema = DataTypes.CreateStructType(new[] { new StructField("c", type, nullable: true) });
        ColumnVector column = Build(type, rows);
        var batch = new ManagedColumnBatch(schema, new[] { column }, rows.Length);

        // A tiny per-column budget forces multiple row groups; the file must still round-trip exactly.
        byte[] bytes = await SmallBudgetWriter.WriteAsync(schema, new[] { batch });
        int rowGroups = await CountRowGroupsAsync(bytes);
        Assert.True(rowGroups > 1, $"expected the deep/wide column to split across row groups, saw {rowGroups}.");

        // Read every row-group batch and compare row-by-row against the model in global row order.
        using var stream = new MemoryStream(bytes, writable: false);
        int globalRow = 0;
        await foreach (ColumnBatch decoded in new ParquetFileReader().ReadAsync(
            stream, schema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
            for (int local = 0; local < decoded.LogicalRowCount; local++)
            {
                AssertEqual(rows[globalRow], ReadValue(decoded.Column(0), local, type));
                globalRow++;
            }
        }

        Assert.Equal(rows.Length, globalRow);
    }

    private static async Task RoundTripAsync(DataType nestedType, IReadOnlyList<object?> rows)
    {
        var schema = DataTypes.CreateStructType(new[] { new StructField("c", nestedType, nullable: true) });
        ColumnVector column = Build(nestedType, rows);
        var batch = new ManagedColumnBatch(schema, new[] { column }, rows.Count);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });

        ColumnBatch decoded = await ReadSingleBatchAsync(bytes, schema);
        Assert.Equal(rows.Count, decoded.LogicalRowCount);
        for (int i = 0; i < rows.Count; i++)
        {
            AssertEqual(rows[i], ReadValue(decoded.Column(0), i, nestedType));
        }
    }

    private static async Task<ColumnBatch> ReadSingleBatchAsync(byte[] bytes, StructType schema)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        int seen = 0;
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
            stream, schema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
            only = batch;
            seen++;
        }

        Assert.NotNull(only);
        Assert.Equal(1, seen);
        return only!;
    }

    private static async Task<int> CountRowGroupsAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await using global::Parquet.ParquetReader reader =
            await global::Parquet.ParquetReader.CreateAsync(stream, null, false, default);
        return reader.RowGroupCount;
    }

    // A ParquetFileWriter subclass with a tiny per-column nested level budget, so a wide/deep nested column is
    // forced to split across row groups (cell 23).
    private sealed class SmallBudgetWriter : ParquetFileWriter
    {
        protected override long NestedLevelBufferBudgetBytes => 1024;

        public static async Task<byte[]> WriteAsync(StructType schema, IReadOnlyList<ColumnBatch> batches)
        {
            var writer = new SmallBudgetWriter();
            using var stream = new MemoryStream();
            await writer.WriteAsync(stream, schema, batches, CancellationToken.None);
            return stream.ToArray();
        }
    }
}
