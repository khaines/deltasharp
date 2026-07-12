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

    // ---- Item 1: Double equality/hash contract (bitwise) -------------------------------------------

    [Fact]
    public void Double_PositiveAndNegativeZero_AreDistinct()
    {
        // System.Double.Equals treats 0.0 == -0.0, but the bit-based hash distinguishes them; the
        // bitwise Equals keeps them distinct so the IEquatable/GetHashCode contract holds.
        MetadataValue positiveZero = MetadataValue.Double(0.0);
        MetadataValue negativeZero = MetadataValue.Double(-0.0);

        Assert.NotEqual(positiveZero, negativeZero);
        Assert.False(positiveZero.Equals(negativeZero));
    }

    [Fact]
    public void Double_NaN_WithSameBitPayload_IsEqual_AndHashesIdentically()
    {
        MetadataValue nan = MetadataValue.Double(double.NaN);
        MetadataValue sameNan = MetadataValue.Double(double.NaN);

        Assert.Equal(nan, sameNan);
        Assert.Equal(nan.GetHashCode(), sameNan.GetHashCode());
    }

    [Fact]
    public void Double_DistinctNaNPayloads_AreDistinct()
    {
        // A signalling/negative NaN bit-pattern is a different value from canonical NaN under
        // exact-bitwise equality (and hashes differently), matching the documented intent.
        MetadataValue canonicalNaN = MetadataValue.Double(double.NaN);
        MetadataValue otherNaN = MetadataValue.Double(BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000001UL)));

        Assert.NotEqual(canonicalNaN, otherNaN);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(1.5, 1.5)]
    [InlineData(1.5, 2.5)]
    public void Double_EqualsImpliesHashEqual(double left, double right)
    {
        // The core contract: Equals => GetHashCode equal, exercised across ±0.0, NaN, and ordinary
        // values. (The converse is not required; only Equals ⇒ equal-hash must hold.)
        MetadataValue a = MetadataValue.Double(left);
        MetadataValue b = MetadataValue.Double(right);

        if (a.Equals(b))
        {
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }
    }

    [Fact]
    public void PositiveAndNegativeZeroDouble_UsableAsDistinctDictionaryKeys()
    {
        // The original bug corrupted a Dictionary/HashSet: equal-by-Equals keys with unequal hashes.
        // With bitwise equality, +0.0 and -0.0 are distinct keys and both round-trip.
        var set = new HashSet<MetadataValue>
        {
            MetadataValue.Double(0.0),
            MetadataValue.Double(-0.0),
        };

        Assert.Equal(2, set.Count);
        Assert.Contains(MetadataValue.Double(0.0), set);
        Assert.Contains(MetadataValue.Double(-0.0), set);
    }

    // ---- Item 10: hash stability golden constant ---------------------------------------------------

    [Fact]
    public void MetadataValue_HashIsProcessStable_GoldenConstant()
    {
        // StableHash is deterministic and process-stable, so a known value hashes to a fixed golden
        // constant across builds/runs. Two independently-built equal values also hash identically.
        Assert.Equal(-781357387, MetadataValue.Long(5).GetHashCode());
        Assert.Equal(MetadataValue.Long(5).GetHashCode(), MetadataValue.Long(5).GetHashCode());
    }

    // ---- Item 3: symmetric typed accessors ---------------------------------------------------------

    [Fact]
    public void TypedAccessors_ReturnValueOnMatchingKind()
    {
        Assert.True(MetadataValue.Long(7).TryGetLong(out long l));
        Assert.Equal(7L, l);

        Assert.True(MetadataValue.Double(1.5).TryGetDouble(out double d));
        Assert.Equal(1.5, d);

        Assert.True(MetadataValue.Boolean(true).TryGetBoolean(out bool b));
        Assert.True(b);

        FieldMetadata nested = FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("k", MetadataValue.Long(1)),
        });
        Assert.True(MetadataValue.Nested(nested).TryGetNested(out FieldMetadata? gotNested));
        Assert.Equal(nested, gotNested);

        MetadataValue array = MetadataValue.Array(new[] { MetadataValue.Long(1) });
        Assert.True(array.TryGetArray(out IReadOnlyList<MetadataValue>? gotArray));
        Assert.Single(gotArray!);
    }

    [Fact]
    public void TypedAccessors_ReturnFalseOnWrongKind()
    {
        MetadataValue s = MetadataValue.String("x");

        Assert.False(s.TryGetLong(out long l));
        Assert.Equal(0L, l);
        Assert.False(s.TryGetDouble(out double d));
        Assert.Equal(0d, d);
        Assert.False(s.TryGetBoolean(out bool b));
        Assert.False(b);
        Assert.False(s.TryGetNested(out FieldMetadata? nested));
        Assert.Null(nested);
        Assert.False(s.TryGetArray(out IReadOnlyList<MetadataValue>? array));
        Assert.Null(array);
    }

    // ---- Items 7 & 8: array immutability and null-element rejection --------------------------------

    [Fact]
    public void Array_NullElement_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MetadataValue.Array(new MetadataValue[] { MetadataValue.Long(1), null! }));
    }

    [Fact]
    public void AsArray_IsNotMutableViaDowncast()
    {
        MetadataValue value = MetadataValue.Array(new[] { MetadataValue.Long(1) });
        IReadOnlyList<MetadataValue> array = value.AsArray();

        // The immutability contract holds: the returned instance is a ReadOnlyCollection, so a
        // caller cannot downcast to MetadataValue[] or a mutable IList<> and mutate it.
        Assert.IsNotType<MetadataValue[]>(array);
        var asList = Assert.IsAssignableFrom<IList<MetadataValue>>(array);
        Assert.True(asList.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asList[0] = MetadataValue.Long(99));

        // The original array value is unchanged.
        Assert.Equal(1L, value.AsArray()[0].AsLong());
    }
}
