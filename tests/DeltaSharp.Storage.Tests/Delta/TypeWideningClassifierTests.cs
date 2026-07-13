using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Direct unit tests of the <see cref="TypeWidening.IsSanctionedWidening"/> classifier — the single
/// authoritative allowlist shared by the write-side enforcer and the read-side promoter (#495). These pin
/// the exact boundary (identity/same-rank → not a change; each sanctioned widening → true; narrowings,
/// cross-family, and deferred date→timestamp → false; decimal grow-only vs shrink) so the two call sites can
/// never diverge from the classifier's contract.
/// </summary>
public sealed class TypeWideningClassifierTests
{
    [Theory]
    // Identity / same-rank: an equal type is NOT a change → false.
    [InlineData(nameof(DataTypes.IntegerType), nameof(DataTypes.IntegerType), false)]
    [InlineData(nameof(DataTypes.LongType), nameof(DataTypes.LongType), false)]
    [InlineData(nameof(DataTypes.ByteType), nameof(DataTypes.ByteType), false)]
    [InlineData(nameof(DataTypes.FloatType), nameof(DataTypes.FloatType), false)]
    // Sanctioned integral widenings (any strictly-wider rank) → true.
    [InlineData(nameof(DataTypes.ByteType), nameof(DataTypes.ShortType), true)]
    [InlineData(nameof(DataTypes.ByteType), nameof(DataTypes.IntegerType), true)]
    [InlineData(nameof(DataTypes.ByteType), nameof(DataTypes.LongType), true)]
    [InlineData(nameof(DataTypes.ShortType), nameof(DataTypes.IntegerType), true)]
    [InlineData(nameof(DataTypes.ShortType), nameof(DataTypes.LongType), true)]
    [InlineData(nameof(DataTypes.IntegerType), nameof(DataTypes.LongType), true)]
    // float → double → true.
    [InlineData(nameof(DataTypes.FloatType), nameof(DataTypes.DoubleType), true)]
    // Narrowings → false.
    [InlineData(nameof(DataTypes.LongType), nameof(DataTypes.IntegerType), false)]
    [InlineData(nameof(DataTypes.ShortType), nameof(DataTypes.ByteType), false)]
    [InlineData(nameof(DataTypes.DoubleType), nameof(DataTypes.FloatType), false)]
    // Cross-family (sanctioned by Delta but NOT in the applied allowlist; deferred #535) → false here.
    [InlineData(nameof(DataTypes.IntegerType), nameof(DataTypes.DoubleType), false)]
    // Deferred date→timestamp (#533) is NOT a sanctioned APPLIED widening → false.
    [InlineData(nameof(DataTypes.DateType), nameof(DataTypes.TimestampType), false)]
    public void IsSanctionedWidening_ScalarPairs(string from, string to, bool expected)
    {
        Assert.Equal(expected, TypeWidening.IsSanctionedWidening(Resolve(from), Resolve(to)));
    }

    [Theory]
    // decimal grow-only (integer-range AND scale both non-decreasing) → true.
    [InlineData(10, 2, 12, 2, true)]  // integer range grows, scale equal
    [InlineData(10, 2, 12, 4, true)]  // both grow, integer range unchanged
    [InlineData(10, 2, 13, 3, true)]  // both grow
    // decimal identity → false (not a change).
    [InlineData(10, 2, 10, 2, false)]
    // decimal shrink (scale, integer range, or precision shrinks) → false.
    [InlineData(10, 2, 10, 1, false)] // scale shrinks
    [InlineData(10, 2, 11, 4, false)] // integer range shrinks
    [InlineData(10, 2, 9, 2, false)]  // precision shrinks
    public void IsSanctionedWidening_DecimalPairs(int fromP, int fromS, int toP, int toS, bool expected)
    {
        DecimalType from = DataTypes.CreateDecimalType(fromP, fromS);
        DecimalType to = DataTypes.CreateDecimalType(toP, toS);
        Assert.Equal(expected, TypeWidening.IsSanctionedWidening(from, to));
    }

    private static DataType Resolve(string name) => name switch
    {
        nameof(DataTypes.ByteType) => DataTypes.ByteType,
        nameof(DataTypes.ShortType) => DataTypes.ShortType,
        nameof(DataTypes.IntegerType) => DataTypes.IntegerType,
        nameof(DataTypes.LongType) => DataTypes.LongType,
        nameof(DataTypes.FloatType) => DataTypes.FloatType,
        nameof(DataTypes.DoubleType) => DataTypes.DoubleType,
        nameof(DataTypes.DateType) => DataTypes.DateType,
        nameof(DataTypes.TimestampType) => DataTypes.TimestampType,
        _ => throw new System.ArgumentOutOfRangeException(nameof(name), name, "Unhandled type name."),
    };
}
