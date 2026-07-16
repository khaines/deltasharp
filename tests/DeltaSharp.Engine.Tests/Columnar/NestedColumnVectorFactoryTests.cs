using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers <see cref="ColumnVectors.Create"/> building the nested reference vectors (including
/// recursion into nested children) and <see cref="ManagedColumnBatch"/> accepting and type-checking
/// a nested column (#570).
/// </summary>
public class NestedColumnVectorFactoryTests
{
    private static readonly StructType PersonType = new(new[]
    {
        new StructField("id", IntegerType.Instance),
        new StructField("name", StringType.Instance),
    });

    private static MutableColumnVector Ints(params int[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, Math.Max(values.Length, 1));
        foreach (int value in values)
        {
            v.AppendValue(value);
        }

        return v;
    }

    private static MutableColumnVector Strings(params string[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(StringType.Instance, Math.Max(values.Length, 1));
        foreach (string value in values)
        {
            v.AppendBytes(Encoding.UTF8.GetBytes(value));
        }

        return v;
    }

    [Fact]
    public void Create_BuildsEmptyBuilderForEachNestedType()
    {
        var s = Assert.IsType<StructColumnVector>(ColumnVectors.Create(PersonType, 4));
        Assert.Equal(0, s.Length);
        Assert.Equal(2, s.FieldCount);
        Assert.Equal(PersonType, s.Type);

        var list = Assert.IsType<ListColumnVector>(ColumnVectors.Create(new ArrayType(IntegerType.Instance), 4));
        Assert.Equal(0, list.Length);

        var map = Assert.IsType<MapColumnVector>(ColumnVectors.Create(StringToInt(), 4));
        Assert.Equal(0, map.Length);
    }

    [Fact]
    public void Create_RecursesIntoNestedChildren()
    {
        // struct<tags: array<int>> -> the field child is itself a list vector.
        var structOfList = (StructColumnVector)ColumnVectors.Create(
            new StructType(new[] { new StructField("tags", new ArrayType(IntegerType.Instance)) }), 2);
        Assert.IsType<ListColumnVector>(structOfList.Child(0));

        // array<struct<...>> -> the element child is a struct vector.
        var listOfStruct = (ListColumnVector)ColumnVectors.Create(new ArrayType(PersonType), 2);
        Assert.IsType<StructColumnVector>(listOfStruct.Elements);

        // map<string, array<int>> -> the value child is a list vector.
        var mapOfList = (MapColumnVector)ColumnVectors.Create(
            new MapType(StringType.Instance, new ArrayType(IntegerType.Instance)), 2);
        Assert.IsType<ListColumnVector>(mapOfList.Values);
    }

    [Fact]
    public void CreateForSchema_BuildsNestedColumns()
    {
        var schema = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance),
            new StructField("person", PersonType),
            new StructField("scores", new ArrayType(IntegerType.Instance)),
        });

        MutableColumnVector[] columns = ColumnVectors.CreateForSchema(schema, 4);

        Assert.IsType<ManagedFixedWidthColumnVector<int>>(columns[0]);
        Assert.IsType<StructColumnVector>(columns[1]);
        Assert.IsType<ListColumnVector>(columns[2]);
    }

    [Fact]
    public void NestedChildren_ComposeForReadAcrossLevels()
    {
        // struct<id:int, tags:array<string>> built from an int child and a list child.
        var tagsType = new ArrayType(StringType.Instance);
        var structType = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance),
            new StructField("tags", tagsType),
        });

        var tags = new ListColumnVector(tagsType, Strings("x", "y"), new[] { 0, 2, 2 }); // row0 [x,y], row1 []
        var s = new StructColumnVector(structType, new ColumnVector[] { Ints(100, 200), tags });

        Assert.Equal(2, s.Length);
        Assert.Equal(200, s.Child(0).GetValue<int>(1));

        var childList = Assert.IsType<ListColumnVector>(s.Child("tags"));
        Assert.Equal(2, childList.ElementLength(0));
        Assert.Equal(0, childList.ElementLength(1));
        Assert.Equal("x", Encoding.UTF8.GetString(childList.ElementsAt(0).GetBytes(0)));
        Assert.Equal("y", Encoding.UTF8.GetString(childList.ElementsAt(0).GetBytes(1)));
    }

    [Fact]
    public void ManagedColumnBatch_AcceptsAndExposesNestedColumns()
    {
        var mapType = StringToInt();
        var arrayType = new ArrayType(IntegerType.Instance);
        var schema = new StructType(new[]
        {
            new StructField("person", PersonType),
            new StructField("scores", arrayType),
            new StructField("props", mapType),
        });

        ColumnVector person = new StructColumnVector(PersonType, new ColumnVector[] { Ints(1, 2, 3), Strings("a", "b", "c") });
        ColumnVector scores = new ListColumnVector(arrayType, Ints(7, 8), new[] { 0, 1, 1, 2 }); // 3 rows
        ColumnVector props = new MapColumnVector(mapType, Strings("k"), Ints(9), new[] { 0, 1, 1, 1 }); // 3 rows

        var batch = new ManagedColumnBatch(schema, new[] { person, scores, props }, rowCount: 3);

        Assert.Equal(3, batch.RowCount);
        Assert.Equal(3, batch.ColumnCount);
        Assert.Equal(schema, batch.Schema);
        Assert.Same(person, batch.Column(0));
        Assert.Equal(PersonType, batch.Column("person").Type);
        Assert.IsType<ListColumnVector>(batch.Column("scores"));
        Assert.IsType<MapColumnVector>(batch.Column("props"));
    }

    [Fact]
    public void ManagedColumnBatch_RejectsNestedTypeMismatch()
    {
        var schema = new StructType(new[] { new StructField("person", PersonType) });

        // A different (non-equal) struct type must be rejected by the batch's type-equality check.
        var otherStruct = new StructType(new[] { new StructField("id", LongType.Instance) });
        ColumnVector wrong = new StructColumnVector(otherStruct, new ColumnVector[] { LongColumn(1L, 2L) });

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            new ManagedColumnBatch(schema, new[] { wrong }, rowCount: 2));
        Assert.Contains("type", ex.Message, StringComparison.OrdinalIgnoreCase);

        // A flat column where a struct is declared is likewise rejected.
        Assert.Throws<ArgumentException>(() =>
            new ManagedColumnBatch(schema, new ColumnVector[] { Ints(1, 2) }, rowCount: 2));
    }

    [Fact]
    public void ManagedColumnBatch_SlicesNestedColumns()
    {
        var arrayType = new ArrayType(IntegerType.Instance);
        var schema = new StructType(new[] { new StructField("scores", arrayType) });
        // 3 rows: [10], [], [20,30]
        ColumnVector scores = new ListColumnVector(arrayType, Ints(10, 20, 30), new[] { 0, 1, 1, 3 });
        var batch = new ManagedColumnBatch(schema, new[] { scores }, rowCount: 3);

        ColumnBatch sliced = batch.Slice(1, 2); // rows 1..2
        var slicedList = Assert.IsType<ListColumnVector>(sliced.Column(0));

        Assert.Equal(2, sliced.RowCount);
        Assert.Equal(0, slicedList.ElementLength(0)); // was row 1 (empty)
        Assert.Equal(2, slicedList.ElementLength(1)); // was row 2
        Assert.Equal(30, slicedList.ElementsAt(1).GetValue<int>(1));
    }

    private static MapType StringToInt() => new(StringType.Instance, IntegerType.Instance);

    private static MutableColumnVector LongColumn(params long[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(LongType.Instance, Math.Max(values.Length, 1));
        foreach (long value in values)
        {
            v.AppendValue(value);
        }

        return v;
    }

    [Fact]
    public void Create_RecursesThreeLevelsDeep()
    {
        // struct<entries: array<map<string,int>>> — factory recursion must reach the 3rd-level leaf types
        // without misalignment or stack issues (schema-bounded depth).
        var schema = new StructType(new[]
        {
            new StructField("entries",
                new ArrayType(new MapType(StringType.Instance, IntegerType.Instance))),
        });
        var s = Assert.IsType<StructColumnVector>(ColumnVectors.Create(schema, 2));
        var list = Assert.IsType<ListColumnVector>(s.Child(0));
        var map = Assert.IsType<MapColumnVector>(list.Elements);
        Assert.Equal(StringType.Instance, map.Keys.Type);
        Assert.Equal(IntegerType.Instance, map.Values.Type);
    }
}
