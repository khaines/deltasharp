using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

public class StructTypeTests
{
    private static StructType Sample() => new(new[]
    {
        new StructField("id", LongType.Instance, nullable: false),
        new StructField("name", StringType.Instance),
        new StructField("score", new DecimalType(10, 2)),
    });

    [Fact]
    public void Indexer_ByPosition_ReturnsFieldsInDeclaredOrder()
    {
        StructType schema = Sample();

        Assert.Equal(3, schema.Count);
        Assert.Equal("id", schema[0].Name);
        Assert.Equal("name", schema[1].Name);
        Assert.Equal("score", schema[2].Name);
    }

    [Fact]
    public void Indexer_ByName_ResolvesCaseSensitively()
    {
        StructType schema = Sample();

        Assert.Equal(StringType.Instance, schema["name"].DataType);
        Assert.Throws<KeyNotFoundException>(() => schema["NAME"]);
        Assert.Throws<KeyNotFoundException>(() => schema["missing"]);
    }

    [Fact]
    public void TryGetField_ReportsPresenceAndAbsence()
    {
        StructType schema = Sample();

        Assert.True(schema.TryGetField("score", out StructField found));
        Assert.Equal("score", found.Name);
        Assert.False(schema.TryGetField("nope", out _));
    }

    [Fact]
    public void IndexOf_ReturnsPositionOrNegativeOne()
    {
        StructType schema = Sample();

        Assert.Equal(0, schema.IndexOf("id"));
        Assert.Equal(2, schema.IndexOf("score"));
        Assert.Equal(-1, schema.IndexOf("absent"));
    }

    [Fact]
    public void Enumeration_YieldsFieldsInOrder()
    {
        StructType schema = Sample();

        Assert.Equal(new[] { "id", "name", "score" }, schema.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void SimpleString_MatchesSparkCatalogForm()
    {
        StructType schema = Sample();

        Assert.Equal("struct<id:bigint,name:string,score:decimal(10,2)>", schema.SimpleString);
        Assert.Equal(schema.SimpleString, schema.ToString());
    }

    [Fact]
    public void SimpleString_ForNestedTypes()
    {
        Assert.Equal("array<int>", new ArrayType(IntegerType.Instance).SimpleString);
        Assert.Equal(
            "map<string,array<tinyint>>",
            new MapType(StringType.Instance, new ArrayType(ByteType.Instance)).SimpleString);
    }

    [Fact]
    public void Empty_HasNoFields()
    {
        Assert.Empty(StructType.Empty);
        Assert.Equal("struct<>", StructType.Empty.SimpleString);
        Assert.Equal(StructType.Empty, new StructType(Array.Empty<StructField>()));
    }

    [Fact]
    public void Fields_ExposesUnderlyingList()
    {
        StructType schema = Sample();
        Assert.Equal(3, schema.Fields.Count);
        Assert.Same(schema[1], schema.Fields[1]);
    }
}
