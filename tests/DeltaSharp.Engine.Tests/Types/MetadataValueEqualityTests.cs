using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

/// <summary>
/// AC4: typed metadata equality and hashing are structural and process-stable. Equal metadata built
/// independently compares equal and hashes identically; differing typed values are not equal; and
/// <c>Long(5)</c>, <c>Double(5.0)</c>, and <c>String("5")</c> are all distinct.
/// </summary>
public sealed class MetadataValueEqualityTests
{
    [Fact]
    public void LongDoubleString_WithSameNumericFace_AreAllDistinct()
    {
        MetadataValue asLong = MetadataValue.Long(5);
        MetadataValue asDouble = MetadataValue.Double(5.0);
        MetadataValue asString = MetadataValue.String("5");

        Assert.NotEqual(asLong, asDouble);
        Assert.NotEqual(asLong, asString);
        Assert.NotEqual(asDouble, asString);
    }

    [Fact]
    public void EqualValues_AreEqual_AndHashIdentically()
    {
        Assert.Equal(MetadataValue.Long(5), MetadataValue.Long(5));
        Assert.Equal(MetadataValue.Long(5).GetHashCode(), MetadataValue.Long(5).GetHashCode());
        Assert.Equal(MetadataValue.Double(5.0), MetadataValue.Double(5.0));
        Assert.Equal(MetadataValue.Double(5.0).GetHashCode(), MetadataValue.Double(5.0).GetHashCode());
        Assert.Equal(MetadataValue.Boolean(true), MetadataValue.Boolean(true));
        Assert.Same(MetadataValue.Null, MetadataValue.Null);
    }

    [Fact]
    public void Arrays_CompareElementWise_AndOrderSensitively()
    {
        MetadataValue a = MetadataValue.Array(new[] { MetadataValue.Long(1), MetadataValue.Long(2) });
        MetadataValue same = MetadataValue.Array(new[] { MetadataValue.Long(1), MetadataValue.Long(2) });
        MetadataValue reordered = MetadataValue.Array(new[] { MetadataValue.Long(2), MetadataValue.Long(1) });

        Assert.Equal(a, same);
        Assert.Equal(a.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(a, reordered);
    }

    [Fact]
    public void NestedMetadata_IsStructural()
    {
        MetadataValue first = MetadataValue.Nested(FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("k", MetadataValue.Long(1)),
        }));
        MetadataValue second = MetadataValue.Nested(FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("k", MetadataValue.Long(1)),
        }));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void FieldMetadata_EqualityIsOrderIndependent_AndHashStable()
    {
        // Built in different key orders and from independent instances.
        FieldMetadata first = FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(5)),
            new KeyValuePair<string, MetadataValue>("comment", MetadataValue.String("pk")),
        });
        FieldMetadata second = FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("comment", MetadataValue.String("pk")),
            new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(5)),
        });

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void FieldMetadata_DifferingTypedValue_IsNotEqual()
    {
        FieldMetadata asLong = FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("id", MetadataValue.Long(5)),
        });
        FieldMetadata asString = FieldMetadata.FromEntries(new[]
        {
            new KeyValuePair<string, string>("id", "5"),
        });

        Assert.NotEqual(asLong, asString);
    }

    [Fact]
    public void Accessors_ThrowOnWrongKind()
    {
        Assert.Throws<InvalidOperationException>(() => MetadataValue.Long(5).AsString());
        Assert.Throws<InvalidOperationException>(() => MetadataValue.String("x").AsLong());
        Assert.False(MetadataValue.Long(5).TryGetString(out _));
        Assert.True(MetadataValue.String("x").TryGetString(out string? s));
        Assert.Equal("x", s);
    }
}
