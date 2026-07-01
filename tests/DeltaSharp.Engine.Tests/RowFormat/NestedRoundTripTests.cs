using DeltaSharp.Engine.Memory;
using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.RowFormat;

/// <summary>
/// STORY-02.4.1 AC3: nested array, map, and struct values in the v1 subset round-trip with element
/// order, key/value pairing, and nested nulls preserved.
/// </summary>
public class NestedRoundTripTests
{
    private static BinaryRow Encode(StructType schema, params object?[] values)
    {
        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));
        return encoder.Encode(new RowData(schema, values));
    }

    [Fact]
    public void Array_PreservesOrderAndNestedNulls()
    {
        var schema = new StructType([new StructField("xs", new ArrayType(IntegerType.Instance))]);
        var array = new ArrayData(IntegerType.Instance, containsNull: true, 3, null, 1, null, 2);
        var source = new RowData(schema, array);

        using BinaryRow row = Encode(schema, array);
        RowData decoded = row.ToRowData();

        var got = (ArrayData)decoded[0]!;
        Assert.Equal(5, got.Count);
        Assert.Equal(3, got[0]);
        Assert.Null(got[1]);
        Assert.Equal(1, got[2]);
        Assert.Null(got[3]);
        Assert.Equal(2, got[4]);
        Assert.Equal(source, decoded); // element order preserved
    }

    [Fact]
    public void Map_PreservesKeyValuePairingAndNullValues()
    {
        var mapType = new MapType(StringType.Instance, LongType.Instance);
        var schema = new StructType([new StructField("m", mapType)]);
        var map = new MapData(
            StringType.Instance, LongType.Instance,
            ["alpha", "beta", "gamma"],
            [10L, null, 30L]);
        var source = new RowData(schema, map);

        using BinaryRow row = Encode(schema, map);
        var got = (MapData)row.ToRowData()[0]!;

        Assert.Equal("alpha", got.Key(0));
        Assert.Equal(10L, got.Value(0));
        Assert.Equal("beta", got.Key(1));
        Assert.Null(got.Value(1)); // nested null value preserved, pairing intact
        Assert.Equal("gamma", got.Key(2));
        Assert.Equal(30L, got.Value(2));
        Assert.Equal(source, row.ToRowData());
    }

    [Fact]
    public void Struct_NestedFieldsAndNullsRoundTrip()
    {
        var inner = new StructType(
        [
            new StructField("name", StringType.Instance),
            new StructField("age", IntegerType.Instance),
        ]);
        var schema = new StructType([new StructField("person", inner)]);
        var person = new RowData(inner, "kai", null);
        var source = new RowData(schema, person);

        using BinaryRow row = Encode(schema, person);
        var got = (RowData)row.ToRowData()[0]!;

        Assert.Equal("kai", got[0]);
        Assert.Null(got[1]);
        Assert.Equal(source, row.ToRowData());
    }

    [Fact]
    public void ArrayOfStructs_AndStructOfArrays_RoundTrip()
    {
        var point = new StructType([new StructField("x", IntegerType.Instance), new StructField("y", IntegerType.Instance)]);
        var schema = new StructType(
        [
            new StructField("path", new ArrayType(point)),
            new StructField("tags", new ArrayType(StringType.Instance)),
        ]);
        var path = new ArrayData(point, false, new RowData(point, 1, 2), new RowData(point, 3, 4));
        var tags = new ArrayData(StringType.Instance, true, "a", null, "c");
        var source = new RowData(schema, path, tags);

        using BinaryRow row = Encode(schema, path, tags);
        Assert.Equal(source, row.ToRowData());
    }

    [Fact]
    public void MapOfArrayValues_RoundTrip()
    {
        var arr = new ArrayType(IntegerType.Instance);
        var mapType = new MapType(StringType.Instance, arr);
        var schema = new StructType([new StructField("m", mapType)]);
        var map = new MapData(
            StringType.Instance, arr,
            ["k1", "k2"],
            [new ArrayData(IntegerType.Instance, true, 1, 2, null), new ArrayData(IntegerType.Instance, false, 9)]);
        var source = new RowData(schema, map);

        using BinaryRow row = Encode(schema, map);
        Assert.Equal(source, row.ToRowData());
    }

    [Fact]
    public void EmptyAndNullCollections_RoundTrip()
    {
        var mapType = new MapType(StringType.Instance, IntegerType.Instance);
        var schema = new StructType(
        [
            new StructField("empty", new ArrayType(IntegerType.Instance)),
            new StructField("nullArr", new ArrayType(IntegerType.Instance)),
            new StructField("emptyMap", mapType),
        ]);
        var source = new RowData(
            schema,
            new ArrayData(IntegerType.Instance, true),
            null,
            new MapData(StringType.Instance, IntegerType.Instance, [], []));

        using BinaryRow row = Encode(schema, new ArrayData(IntegerType.Instance, true), null, new MapData(StringType.Instance, IntegerType.Instance, [], []));
        Assert.True(row.IsNullAt(1));
        Assert.Equal(source, row.ToRowData());
    }

    [Fact]
    public void MapData_NullKey_Rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new MapData(StringType.Instance, IntegerType.Instance, ["ok", null], [1, 2]));
        Assert.Equal("keys", ex.ParamName);
    }
}
