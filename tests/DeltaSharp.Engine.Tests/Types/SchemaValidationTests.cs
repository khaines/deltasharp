using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

public class SchemaValidationTests
{
    [Fact]
    public void Struct_RejectsDuplicateFieldNames_WithPreciseMessage()
    {
        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new StructType(new[]
            {
                new StructField("id", IntegerType.Instance),
                new StructField("name", StringType.Instance),
                new StructField("id", LongType.Instance),
            }));

        Assert.Contains("id", ex.Message);
        Assert.Contains("0", ex.Message);
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void Struct_AllowsCaseDifferingFieldNames()
    {
        // Spark parity: case-only ambiguity is a name-resolution concern, not a type error.
        var type = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance),
            new StructField("ID", LongType.Instance),
        });

        Assert.Equal(2, type.Count);
    }

    [Fact]
    public void Map_RejectsNullTypeKey()
    {
        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new MapType(NullType.Instance, IntegerType.Instance));
        Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_RejectsMapTypeKey()
    {
        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new MapType(new MapType(StringType.Instance, IntegerType.Instance), IntegerType.Instance));
        Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(39, 0)]
    [InlineData(-1, 0)]
    public void Decimal_RejectsPrecisionOutOfRange(int precision, int scale)
    {
        Assert.Throws<SchemaValidationException>(() => new DecimalType(precision, scale));
    }

    [Theory]
    [InlineData(10, -1)]
    [InlineData(10, 11)]
    public void Decimal_RejectsScaleOutOfRange(int precision, int scale)
    {
        Assert.Throws<SchemaValidationException>(() => new DecimalType(precision, scale));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(38, 38)]
    [InlineData(10, 2)]
    public void Decimal_AcceptsValidPrecisionAndScale(int precision, int scale)
    {
        var type = new DecimalType(precision, scale);
        Assert.Equal(precision, type.Precision);
        Assert.Equal(scale, type.Scale);
    }

    [Fact]
    public void StructField_RejectsNullOrEmptyName()
    {
        Assert.Throws<ArgumentNullException>(() => new StructField(null!, IntegerType.Instance));
        Assert.Throws<ArgumentException>(() => new StructField(string.Empty, IntegerType.Instance));
    }

    [Fact]
    public void StructField_RejectsNullType()
    {
        Assert.Throws<ArgumentNullException>(() => new StructField("x", null!));
    }

    [Fact]
    public void Array_RejectsNullElementType()
    {
        Assert.Throws<ArgumentNullException>(() => new ArrayType(null!));
    }

    [Fact]
    public void Map_RejectsNullKeyOrValueType()
    {
        Assert.Throws<ArgumentNullException>(() => new MapType(null!, IntegerType.Instance));
        Assert.Throws<ArgumentNullException>(() => new MapType(StringType.Instance, null!));
    }

    [Fact]
    public void Struct_RejectsNullFieldsArgument()
    {
        Assert.Throws<ArgumentNullException>(() => new StructType(null!));
    }

    [Fact]
    public void Metadata_RejectsNullKeyOrValue()
    {
        // A null key/value is an ArgumentNullException (consistent with MetadataValue.Array and
        // BCL dictionary null-key handling); ArgumentNullException derives from ArgumentException,
        // so callers catching the base type are unaffected.
        Assert.Throws<ArgumentNullException>(() =>
            FieldMetadata.FromEntries(new[] { new KeyValuePair<string, string>(null!, "v") }));
        Assert.Throws<ArgumentNullException>(() =>
            FieldMetadata.FromEntries(new[] { new KeyValuePair<string, string>("k", null!) }));
    }
}
