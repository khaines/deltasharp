using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Typed;

/// <summary>
/// STORY-04.2.4 (#163) AC3/AC4 — the reflection schema deriver behind <see cref="DataFrame.As{T}"/>.
/// It maps <c>T</c>'s public properties to a <see cref="StructType"/> whose per-field
/// <see cref="StructField.Nullable"/> follows ADR-0008 nullability (<see cref="System.Nullable{T}"/>
/// and reference types are nullable; non-nullable value types are not), whose CLR&#8594;
/// <see cref="DataType"/> mapping matches the read-door value contract, and which rejects an
/// unmappable property type with the deterministic <see cref="UnsupportedTypedExpressionException"/>.
/// These tests gate the derived schema shape, so a regression in nullability or type mapping fails.
/// </summary>
public sealed class DatasetSchemaDeriverTests
{
    private sealed class Person
    {
        public int Id { get; set; }

        public string? Name { get; set; }

        public int? Age { get; set; }
    }

    private sealed class AllSupportedTypes
    {
        public bool Flag { get; set; }

        public sbyte Tiny { get; set; }

        public short Small { get; set; }

        public int Medium { get; set; }

        public long Big { get; set; }

        public float Single { get; set; }

        public double Double { get; set; }

        public decimal Money { get; set; }

        public string Text { get; set; } = string.Empty;

        public byte[] Blob { get; set; } = System.Array.Empty<byte>();

        public DateOnly Day { get; set; }

        public DateTime Moment { get; set; }
    }

    private sealed class WithUnsupportedProperty
    {
        public int Id { get; set; }

        public Guid Key { get; set; }
    }

    // ----- AC3: nullability per ADR-0008 -----

    [Fact]
    public void Derive_MapsNullabilityPerAdr0008()
    {
        StructType schema = DatasetSchema.Derive<Person>();

        Assert.Collection(
            schema.Fields,
            id =>
            {
                Assert.Equal("Id", id.Name);
                Assert.Equal(IntegerType.Instance, id.DataType);
                Assert.False(id.Nullable); // non-nullable value type
            },
            name =>
            {
                Assert.Equal("Name", name.Name);
                Assert.Equal(StringType.Instance, name.DataType);
                Assert.True(name.Nullable); // reference type
            },
            age =>
            {
                Assert.Equal("Age", age.Name);
                Assert.Equal(IntegerType.Instance, age.DataType); // Nullable<int> unwraps to int
                Assert.True(age.Nullable); // Nullable<> value type
            });
    }

    // ----- AC3: CLR -> DataType mapping consistent with the read-door value contract -----

    [Fact]
    public void Derive_MapsEverySupportedClrTypeToItsAdr0008DataType()
    {
        StructType schema = DatasetSchema.Derive<AllSupportedTypes>();

        Assert.Equal(BooleanType.Instance, schema["Flag"].DataType);
        Assert.Equal(ByteType.Instance, schema["Tiny"].DataType);
        Assert.Equal(ShortType.Instance, schema["Small"].DataType);
        Assert.Equal(IntegerType.Instance, schema["Medium"].DataType);
        Assert.Equal(LongType.Instance, schema["Big"].DataType);
        Assert.Equal(FloatType.Instance, schema["Single"].DataType);
        Assert.Equal(DoubleType.Instance, schema["Double"].DataType);
        Assert.Equal(new DecimalType(38, 18), schema["Money"].DataType);
        Assert.Equal(StringType.Instance, schema["Text"].DataType);
        Assert.Equal(BinaryType.Instance, schema["Blob"].DataType);
        Assert.Equal(DateType.Instance, schema["Day"].DataType);
        Assert.Equal(TimestampType.Instance, schema["Moment"].DataType);
    }

    [Fact]
    public void Derive_PreservesDeclarationOrder()
    {
        StructType schema = DatasetSchema.Derive<Person>();

        Assert.Equal(new[] { "Id", "Name", "Age" }, schema.Fields.Select(f => f.Name).ToArray());
    }

    // ----- AC4: unsupported property type -> deterministic diagnostic -----

    [Fact]
    public void Derive_UnsupportedPropertyType_ThrowsDeterministicDiagnostic()
    {
        var ex = Assert.Throws<UnsupportedTypedExpressionException>(
            () => DatasetSchema.Derive<WithUnsupportedProperty>());

        Assert.Contains("WithUnsupportedProperty.Key", ex.Message);
        Assert.Contains("Guid", ex.Message);
    }
}
