using System;
using System.Collections.Generic;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Actions;

/// <summary>
/// STORY-04.7.1 (#177): <see cref="Row"/> materialization — schema, ordinal and by-name access,
/// typed getters, and null semantics follow the documented Spark-parity contract.
/// </summary>
public sealed class RowTests
{
    private static StructType Schema() => new(new[]
    {
        new StructField("name", DataTypes.StringType),
        new StructField("age", DataTypes.IntegerType),
        new StructField("active", DataTypes.BooleanType),
    });

    private static Row SampleRow() => new(Schema(), "Alice", 30, true);

    // ----- construction / schema -----

    [Fact]
    public void Constructor_RejectsNullSchemaAndValues()
    {
        Assert.Throws<ArgumentNullException>(() => new Row(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => new Row(Schema(), (object?[])null!));
    }

    [Fact]
    public void Constructor_RejectsValueCountMismatch()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => new Row(Schema(), "Alice", 30));
        Assert.Equal("values", ex.ParamName);
    }

    [Fact]
    public void Schema_And_Length_ReflectFields()
    {
        Row row = SampleRow();
        Assert.Equal(3, row.Length);
        Assert.Equal(3, row.Size);
        Assert.Equal("name", row.Schema[0].Name);
    }

    [Fact]
    public void Constructor_CopiesValues_SoRowIsImmutable()
    {
        var values = new object?[] { "Alice", 30, true };
        var row = new Row(Schema(), values);
        values[0] = "Mallory";
        Assert.Equal("Alice", row[0]);
    }

    [Fact]
    public void IReadOnlyListConstructor_MatchesParamsConstructor()
    {
        var row = new Row(Schema(), new List<object?> { "Bob", 25, false });
        Assert.Equal("Bob", row[0]);
        Assert.Equal(25, row[1]);
        Assert.Equal(false, row[2]);
    }

    // ----- ordinal access -----

    [Fact]
    public void OrdinalIndexer_ReturnsValues()
    {
        Row row = SampleRow();
        Assert.Equal("Alice", row[0]);
        Assert.Equal(30, row[1]);
        Assert.Equal(true, row[2]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void OrdinalIndexer_OutOfRange_Throws(int ordinal)
    {
        Row row = SampleRow();
        Assert.Throws<ArgumentOutOfRangeException>(() => row[ordinal]);
    }

    // ----- by-name access -----

    [Fact]
    public void NameIndexer_ResolvesViaSchema()
    {
        Row row = SampleRow();
        Assert.Equal("Alice", row["name"]);
        Assert.Equal(30, row["age"]);
    }

    [Fact]
    public void FieldIndex_ReturnsOrdinal_AndIsCaseSensitive()
    {
        Row row = SampleRow();
        Assert.Equal(1, row.FieldIndex("age"));

        ArgumentException ex = Assert.Throws<ArgumentException>(() => row.FieldIndex("AGE"));
        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void NameIndexer_MissingField_Throws()
    {
        Row row = SampleRow();
        Assert.Throws<ArgumentException>(() => row["missing"]);
    }

    [Fact]
    public void FieldIndex_NullName_Throws()
    {
        Row row = SampleRow();
        Assert.Throws<ArgumentNullException>(() => row.FieldIndex(null!));
    }

    // ----- null semantics -----

    [Fact]
    public void IsNullAt_And_AnyNull_TrackNulls()
    {
        var row = new Row(Schema(), "Alice", null, true);
        Assert.False(row.IsNullAt(0));
        Assert.True(row.IsNullAt(1));
        Assert.True(row.AnyNull);

        Assert.False(SampleRow().AnyNull);
    }

    [Fact]
    public void IsNullAt_OutOfRange_Throws()
    {
        Row row = SampleRow();
        Assert.Throws<ArgumentOutOfRangeException>(() => row.IsNullAt(5));
    }

    // ----- typed getters -----

    [Fact]
    public void GetAs_ByOrdinalAndName_ReturnsTypedValue()
    {
        Row row = SampleRow();
        Assert.Equal("Alice", row.GetAs<string>(0));
        Assert.Equal(30, row.GetAs<int>("age"));
        Assert.True(row.GetAs<bool>(2));
    }

    [Fact]
    public void GetAs_WrongType_ThrowsInvalidCast()
    {
        Row row = SampleRow();
        Assert.Throws<InvalidCastException>(() => row.GetAs<int>(0));
    }

    [Fact]
    public void GetAs_NullValue_ReferenceType_ReturnsNull()
    {
        var row = new Row(Schema(), null, 30, true);
        Assert.Null(row.GetAs<string>(0));
    }

    [Fact]
    public void GetAs_NullValue_NullableValueType_ReturnsNull()
    {
        var row = new Row(Schema(), "Alice", null, true);
        Assert.Null(row.GetAs<int?>(1));
    }

    [Fact]
    public void GetAs_NullValue_NonNullableValueType_Throws()
    {
        var row = new Row(Schema(), "Alice", null, true);
        Assert.Throws<InvalidOperationException>(() => row.GetAs<int>(1));
    }

    // ----- ToString (Spark parity) -----

    [Fact]
    public void ToString_RendersBracketedCsv_WithNullAsLiteral()
    {
        var row = new Row(Schema(), "Alice", null, true);
        Assert.Equal("[Alice,null,true]", row.ToString());
    }

    [Fact]
    public void ToString_UsesInvariantCulture_ForNumbers()
    {
        var schema = new StructType(new[] { new StructField("d", DataTypes.DoubleType) });
        var row = new Row(schema, 1234.5);
        Assert.Equal("[1234.5]", row.ToString());
    }
}
