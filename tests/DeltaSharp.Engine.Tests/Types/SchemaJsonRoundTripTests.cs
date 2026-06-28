using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

public class SchemaJsonRoundTripTests
{
    public static IEnumerable<object[]> AllAtomicTypes() => new[]
    {
        new object[] { BooleanType.Instance },
        new object[] { ByteType.Instance },
        new object[] { ShortType.Instance },
        new object[] { IntegerType.Instance },
        new object[] { LongType.Instance },
        new object[] { FloatType.Instance },
        new object[] { DoubleType.Instance },
        new object[] { StringType.Instance },
        new object[] { BinaryType.Instance },
        new object[] { DateType.Instance },
        new object[] { TimestampType.Instance },
        new object[] { NullType.Instance },
    };

    [Theory]
    [MemberData(nameof(AllAtomicTypes))]
    public void Atomic_RoundTrips(DataType type)
    {
        Assert.Equal(type, DataType.FromJson(type.ToJson()));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(18, 4)]
    [InlineData(38, 10)]
    public void Decimal_RoundTrips(int precision, int scale)
    {
        var type = new DecimalType(precision, scale);
        Assert.Equal(type, DataType.FromJson(type.ToJson()));
    }

    [Fact]
    public void Array_RoundTrips_PreservingContainsNull()
    {
        foreach (bool containsNull in new[] { true, false })
        {
            var type = new ArrayType(IntegerType.Instance, containsNull);
            var roundTripped = (ArrayType)DataType.FromJson(type.ToJson());
            Assert.Equal(type, roundTripped);
            Assert.Equal(containsNull, roundTripped.ContainsNull);
        }
    }

    [Fact]
    public void Map_RoundTrips_PreservingValueContainsNull()
    {
        foreach (bool valueContainsNull in new[] { true, false })
        {
            var type = new MapType(StringType.Instance, new DecimalType(20, 2), valueContainsNull);
            var roundTripped = (MapType)DataType.FromJson(type.ToJson());
            Assert.Equal(type, roundTripped);
            Assert.Equal(valueContainsNull, roundTripped.ValueContainsNull);
        }
    }

    [Fact]
    public void Struct_RoundTrips_PreservingNullabilityAndMetadata()
    {
        var schema = new StructType(new[]
        {
            new StructField(
                "id",
                new DecimalType(18, 0),
                nullable: false,
                FieldMetadata.FromEntries(new[] { new KeyValuePair<string, string>("comment", "primary key") })),
            new StructField("name", StringType.Instance, nullable: true),
        });

        var roundTripped = (StructType)DataType.FromJson(schema.ToJson());

        Assert.Equal(schema, roundTripped);
        Assert.False(roundTripped["id"].Nullable);
        Assert.Equal("primary key", roundTripped["id"].Metadata["comment"]);
        Assert.True(roundTripped["name"].Nullable);
        Assert.True(roundTripped["name"].Metadata.IsEmpty);
    }

    [Fact]
    public void DeeplyNestedSchema_RoundTrips()
    {
        var schema = new StructType(new[]
        {
            new StructField("a", IntegerType.Instance),
            new StructField(
                "b",
                new ArrayType(
                    new MapType(
                        StringType.Instance,
                        new StructType(new[]
                        {
                            new StructField("c", TimestampType.Instance, nullable: false),
                            new StructField("d", new ArrayType(new DecimalType(38, 18), containsNull: false)),
                        }),
                        valueContainsNull: false),
                    containsNull: false)),
        });

        Assert.Equal(schema, DataType.FromJson(schema.ToJson()));
    }

    [Fact]
    public void Serialization_IsDeterministic_AndMetadataKeysSorted()
    {
        var schema = new StructType(new[]
        {
            new StructField(
                "x",
                IntegerType.Instance,
                nullable: true,
                FieldMetadata.FromEntries(new[]
                {
                    new KeyValuePair<string, string>("zeta", "1"),
                    new KeyValuePair<string, string>("alpha", "2"),
                })),
        });

        string first = schema.ToJson();
        string second = schema.ToJson();

        Assert.Equal(first, second);
        Assert.True(first.IndexOf("alpha", StringComparison.Ordinal) < first.IndexOf("zeta", StringComparison.Ordinal));
    }

    [Fact]
    public void Json_UsesSparkCompatibleShapes()
    {
        Assert.Equal("\"integer\"", IntegerType.Instance.ToJson());
        Assert.Equal("\"decimal(10,2)\"", new DecimalType(10, 2).ToJson());
        Assert.Equal("\"void\"", NullType.Instance.ToJson());

        string arrayJson = new ArrayType(StringType.Instance, containsNull: false).ToJson();
        Assert.Contains("\"type\":\"array\"", arrayJson);
        Assert.Contains("\"elementType\":\"string\"", arrayJson);
        Assert.Contains("\"containsNull\":false", arrayJson);

        string structJson = new StructType(new[] { new StructField("a", IntegerType.Instance) }).ToJson();
        Assert.Contains("\"type\":\"struct\"", structJson);
        Assert.Contains("\"name\":\"a\"", structJson);
        Assert.Contains("\"nullable\":true", structJson);
    }

    [Fact]
    public void CanReadExternallyAuthoredSparkSchemaJson()
    {
        const string json =
            "{\"type\":\"struct\",\"fields\":[" +
            "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{}}," +
            "{\"name\":\"amount\",\"type\":\"decimal(12,2)\",\"nullable\":true,\"metadata\":{}}]}";

        var schema = (StructType)DataType.FromJson(json);

        Assert.Equal(2, schema.Count);
        Assert.Equal(LongType.Instance, schema["id"].DataType);
        Assert.False(schema["id"].Nullable);
        Assert.Equal(new DecimalType(12, 2), schema["amount"].DataType);
    }

    [Fact]
    public void FieldWithoutMetadataProperty_DefaultsToEmpty()
    {
        const string json =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"a\",\"type\":\"integer\",\"nullable\":true}]}";

        var schema = (StructType)DataType.FromJson(json);

        Assert.True(schema["a"].Metadata.IsEmpty);
    }

    [Fact]
    public void FromJson_RejectsMalformedJson()
    {
        Assert.Throws<SchemaValidationException>(() => DataType.FromJson("{ not json"));
    }

    [Fact]
    public void FromJson_RejectsUnknownTypeName()
    {
        Assert.Throws<SchemaValidationException>(() => DataType.FromJson("\"int128\""));
    }

    [Fact]
    public void FromJson_RejectsMissingRequiredProperty()
    {
        Assert.Throws<SchemaValidationException>(() =>
            DataType.FromJson("{\"type\":\"array\",\"containsNull\":true}"));
    }

    [Fact]
    public void FromJson_RejectsNonStringMetadataValue()
    {
        const string json =
            "{\"type\":\"struct\",\"fields\":[" +
            "{\"name\":\"a\",\"type\":\"integer\",\"nullable\":true,\"metadata\":{\"weight\":3}}]}";

        SchemaValidationException ex =
            Assert.Throws<SchemaValidationException>(() => DataType.FromJson(json));
        Assert.Contains("metadata", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromJson_PropagatesValidationErrors_ForInvalidDecimal()
    {
        Assert.Throws<SchemaValidationException>(() => DataType.FromJson("\"decimal(50,0)\""));
    }

    [Fact]
    public void FromJson_PropagatesValidationErrors_ForDuplicateFields()
    {
        const string json =
            "{\"type\":\"struct\",\"fields\":[" +
            "{\"name\":\"a\",\"type\":\"integer\",\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"a\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}]}";

        Assert.Throws<SchemaValidationException>(() => DataType.FromJson(json));
    }

    [Fact]
    public void FromJson_RejectsNullArgument()
    {
        Assert.Throws<ArgumentNullException>(() => DataType.FromJson(null!));
    }
}
