using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

public class DataTypeEqualityTests
{
    public static IEnumerable<object[]> AtomicInstances() => new[]
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
    [MemberData(nameof(AtomicInstances))]
    public void Atomic_EqualsItself_AndHasStableHash(DataType type)
    {
        DataType same = type;
        Assert.True(type.Equals(type));
        Assert.True(type == same);
        Assert.Equal(type.GetHashCode(), type.GetHashCode());
    }

    [Fact]
    public void DistinctAtomicTypes_AreNotEqual()
    {
        Assert.NotEqual<DataType>(IntegerType.Instance, LongType.Instance);
        Assert.NotEqual<DataType>(StringType.Instance, BinaryType.Instance);
        Assert.NotEqual<DataType>(DateType.Instance, TimestampType.Instance);
        Assert.True(IntegerType.Instance != LongType.Instance);
        Assert.False(IntegerType.Instance == LongType.Instance);
    }

    [Fact]
    public void Decimal_EqualityDependsOnPrecisionAndScale()
    {
        Assert.Equal(new DecimalType(10, 2), new DecimalType(10, 2));
        Assert.Equal(new DecimalType(10, 2).GetHashCode(), new DecimalType(10, 2).GetHashCode());
        Assert.NotEqual(new DecimalType(10, 2), new DecimalType(10, 3));
        Assert.NotEqual(new DecimalType(10, 2), new DecimalType(11, 2));
    }

    [Fact]
    public void Array_EqualityDependsOnElementAndContainsNull()
    {
        Assert.Equal(new ArrayType(IntegerType.Instance), new ArrayType(IntegerType.Instance));
        Assert.NotEqual(new ArrayType(IntegerType.Instance), new ArrayType(LongType.Instance));
        Assert.NotEqual(
            new ArrayType(IntegerType.Instance, containsNull: true),
            new ArrayType(IntegerType.Instance, containsNull: false));
    }

    [Fact]
    public void Map_EqualityDependsOnKeyValueAndValueContainsNull()
    {
        var a = new MapType(StringType.Instance, IntegerType.Instance);
        var b = new MapType(StringType.Instance, IntegerType.Instance);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new MapType(StringType.Instance, LongType.Instance));
        Assert.NotEqual(a, new MapType(LongType.Instance, IntegerType.Instance));
        Assert.NotEqual(
            new MapType(StringType.Instance, IntegerType.Instance, valueContainsNull: true),
            new MapType(StringType.Instance, IntegerType.Instance, valueContainsNull: false));
    }

    [Fact]
    public void Struct_EqualityIsStructural_AndOrderSensitive()
    {
        var a = new StructType(new[]
        {
            new StructField("x", IntegerType.Instance),
            new StructField("y", StringType.Instance),
        });
        var b = new StructType(new[]
        {
            new StructField("x", IntegerType.Instance),
            new StructField("y", StringType.Instance),
        });
        var reordered = new StructType(new[]
        {
            new StructField("y", StringType.Instance),
            new StructField("x", IntegerType.Instance),
        });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, reordered);
    }

    [Fact]
    public void Struct_EqualityConsidersNullabilityAndMetadata()
    {
        var nullable = new StructType(new[] { new StructField("x", IntegerType.Instance, nullable: true) });
        var notNullable = new StructType(new[] { new StructField("x", IntegerType.Instance, nullable: false) });
        Assert.NotEqual(nullable, notNullable);

        var withComment = new StructType(new[]
        {
            new StructField(
                "x",
                IntegerType.Instance,
                nullable: true,
                FieldMetadata.FromEntries(new[] { new KeyValuePair<string, string>("comment", "id") })),
        });
        Assert.NotEqual(nullable, withComment);
    }

    [Fact]
    public void NestedTypes_CompareDeeply()
    {
        DataType Build() => new StructType(new[]
        {
            new StructField("a", new ArrayType(new MapType(StringType.Instance, new DecimalType(20, 4)))),
        });

        Assert.Equal(Build(), Build());
        Assert.Equal(Build().GetHashCode(), Build().GetHashCode());
    }

    [Fact]
    public void Equals_Object_And_Null_BehaveCorrectly()
    {
        DataType type = IntegerType.Instance;
        Assert.False(type.Equals((object?)null));
        Assert.False(type.Equals("not a type"));
        Assert.True(type.Equals((object)IntegerType.Instance));
        Assert.False(type == null);
        Assert.True((DataType?)null == (DataType?)null);
    }

    [Fact]
    public void FieldMetadata_EqualityIsOrderIndependent()
    {
        var first = FieldMetadata.FromEntries(new[]
        {
            new KeyValuePair<string, string>("a", "1"),
            new KeyValuePair<string, string>("b", "2"),
        });
        var second = FieldMetadata.FromEntries(new[]
        {
            new KeyValuePair<string, string>("b", "2"),
            new KeyValuePair<string, string>("a", "1"),
        });

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, FieldMetadata.Empty);
    }

    [Fact]
    public void StableHash_IsConsistentForEqualTypes()
    {
        // Two independently constructed but equal schemas must hash identically (determinism).
        DataType left = new StructType(new[]
        {
            new StructField("id", new DecimalType(18, 0), nullable: false),
            new StructField("tags", new ArrayType(StringType.Instance, containsNull: false)),
        });
        DataType right = new StructType(new[]
        {
            new StructField("id", new DecimalType(18, 0), nullable: false),
            new StructField("tags", new ArrayType(StringType.Instance, containsNull: false)),
        });

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
}
