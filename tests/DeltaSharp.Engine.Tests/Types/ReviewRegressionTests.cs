using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

/// <summary>
/// Regression guards for findings raised during the STORY-02.5.1 (#141) review council.
/// </summary>
public class ReviewRegressionTests
{
    [Fact]
    public void StableHash_IsNotCommutative_NoSystematicCollision()
    {
        // Under a commutative combine, decimal(8,4) and decimal(12,0) collided (8^4 == 12^0).
        Assert.NotEqual(new DecimalType(8, 4).GetHashCode(), new DecimalType(12, 0).GetHashCode());
        Assert.NotEqual(new DecimalType(10, 2).GetHashCode(), new DecimalType(8, 0).GetHashCode());

        // Order-sensitive structural combination: array<int> vs a struct must not collide trivially.
        Assert.NotEqual(
            new ArrayType(IntegerType.Instance).GetHashCode(),
            new ArrayType(LongType.Instance).GetHashCode());
    }

    [Fact]
    public void StructHash_IsOrderSensitive_ForSwappedFields()
    {
        // Reordering fields yields a different (non-equal) type; its hash must differ too,
        // because struct hashing folds each field's position.
        var alpha = new StructField("alpha", IntegerType.Instance);
        var beta = new StructField("beta", StringType.Instance);
        var s1 = new StructType(new[] { alpha, beta });
        var s2 = new StructType(new[] { beta, alpha });

        Assert.NotEqual(s1, s2);
        Assert.NotEqual(s1.GetHashCode(), s2.GetHashCode());
    }

    [Fact]
    public void FromJson_AcceptsLegacyNullSpelling_ForNullType()
    {
        Assert.Equal(NullType.Instance, SchemaJson.FromJson("\"null\""));
        Assert.Equal(NullType.Instance, SchemaJson.FromJson("\"void\""));
    }

    [Fact]
    public void FromJson_RejectsDecimalWithTrailingGarbage()
    {
        Assert.Throws<SchemaValidationException>(() => SchemaJson.FromJson("\"decimal(10,2) junk\""));
        Assert.Throws<SchemaValidationException>(() => SchemaJson.FromJson("\"decimal(10,2)x\""));
    }

    [Fact]
    public void FromJson_RejectsNonObjectStructField_WithSchemaValidationException()
    {
        const string json = "{\"type\":\"struct\",\"fields\":[42]}";

        // Must be a SchemaValidationException, not a leaked InvalidOperationException.
        Assert.Throws<SchemaValidationException>(() => SchemaJson.FromJson(json));
    }

    [Fact]
    public void StructTypeFields_AreNotCastableToMutableArray()
    {
        var schema = new StructType(new[]
        {
            new StructField("a", IntegerType.Instance),
            new StructField("b", StringType.Instance),
        });

        Assert.IsNotType<StructField[]>(schema.Fields);
        Assert.Equal(2, schema.Fields.Count);
        Assert.Equal("a", schema.Fields[0].Name);
    }

    [Fact]
    public void DefaultPhysicalLayout_IsNone_NotFixedWidthZero()
    {
        PhysicalLayout defaultLayout = default;

        Assert.Equal(PhysicalLayoutKind.None, defaultLayout.Kind);
        Assert.False(defaultLayout.IsFixedWidth);

        // The unsupported out-value is the None sentinel, never a plausible FixedWidth(0).
        Assert.False(PhysicalLayoutResolver.TryResolve(NullType.Instance, out PhysicalLayout unsupported));
        Assert.Equal(PhysicalLayoutKind.None, unsupported.Kind);
    }
}
