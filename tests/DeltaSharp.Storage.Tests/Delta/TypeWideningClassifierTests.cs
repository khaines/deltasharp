using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Direct unit tests of the <see cref="TypeWidening.IsSanctionedWidening"/> classifier — the single
/// authoritative allowlist shared by the write-side enforcer and the read-side promoter (#495, #535). These
/// pin the exact boundary (identity/same-rank → not a change; each sanctioned widening → true; narrowings and
/// deferred date→timestamp → false; decimal grow-only vs shrink; the cross-family integral→double /
/// integral→decimal cases #535, including the decimal-fit boundary) so the two call sites can never diverge
/// from the classifier's contract.
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
    // Cross-family integral→double is now an APPLIED widening (#535): byte/short/int → double → true.
    [InlineData(nameof(DataTypes.ByteType), nameof(DataTypes.DoubleType), true)]
    [InlineData(nameof(DataTypes.ShortType), nameof(DataTypes.DoubleType), true)]
    [InlineData(nameof(DataTypes.IntegerType), nameof(DataTypes.DoubleType), true)]
    // long → double is LOSSY (64-bit int exceeds double's 53-bit mantissa) and NOT Delta-sanctioned → false.
    [InlineData(nameof(DataTypes.LongType), nameof(DataTypes.DoubleType), false)]
    // Deferred date→timestamp (#533) is NOT a sanctioned APPLIED widening → false.
    [InlineData(nameof(DataTypes.DateType), nameof(DataTypes.TimestampType), false)]
    public void IsSanctionedWidening_ScalarPairs(string from, string to, bool expected)
    {
        Assert.Equal(expected, TypeWidening.IsSanctionedWidening(Resolve(from), Resolve(to)));
    }

    [Theory]
    // Cross-family integral→decimal (#535) — true only when the decimal's integer-digit capacity (p−s) holds
    // the full integral range: byte 3, short 5, int 10, long 19 digits.
    [InlineData(nameof(DataTypes.ByteType), 3, 0, true)]   // decimal(3,0) holds byte [-128,127]
    [InlineData(nameof(DataTypes.ByteType), 2, 0, false)]  // decimal(2,0) cannot hold 127
    [InlineData(nameof(DataTypes.ShortType), 5, 0, true)]  // decimal(5,0) holds short
    [InlineData(nameof(DataTypes.ShortType), 4, 0, false)] // decimal(4,0) cannot hold 32767
    [InlineData(nameof(DataTypes.IntegerType), 10, 0, true)]  // decimal(10,0) holds int
    [InlineData(nameof(DataTypes.IntegerType), 9, 0, false)]  // decimal(9,0) cannot hold 2147483647
    [InlineData(nameof(DataTypes.IntegerType), 12, 2, true)]  // p−s = 10 ≥ 10 → holds int
    [InlineData(nameof(DataTypes.IntegerType), 11, 2, false)] // p−s = 9 < 10 → too narrow
    [InlineData(nameof(DataTypes.LongType), 19, 0, true)]  // decimal(19,0) holds long
    [InlineData(nameof(DataTypes.LongType), 18, 0, false)] // decimal(18,0) cannot hold long
    [InlineData(nameof(DataTypes.LongType), 20, 0, true)]  // wider decimal also holds long
    public void IsSanctionedWidening_IntegralToDecimal(string from, int toP, int toS, bool expected)
    {
        DataType to = DataTypes.CreateDecimalType(toP, toS);
        Assert.Equal(expected, TypeWidening.IsSanctionedWidening(Resolve(from), to));
    }

    [Theory]
    // double→decimal is NOT Delta-sanctioned (double is not an integral source) → false.
    [InlineData(nameof(DataTypes.DoubleType), 20, 0, false)]
    [InlineData(nameof(DataTypes.FloatType), 20, 0, false)]
    public void IsSanctionedWidening_FloatingToDecimal_IsNeverSanctioned(string from, int toP, int toS, bool expected)
    {
        DataType to = DataTypes.CreateDecimalType(toP, toS);
        Assert.Equal(expected, TypeWidening.IsSanctionedWidening(Resolve(from), to));
    }

    [Theory]
    // Same-family classifier: the partition guard's applied subset EXCLUDES cross-family (#537).
    [InlineData(nameof(DataTypes.IntegerType), nameof(DataTypes.LongType), true)]   // integral same-family
    [InlineData(nameof(DataTypes.FloatType), nameof(DataTypes.DoubleType), true)]   // float→double same-family
    [InlineData(nameof(DataTypes.IntegerType), nameof(DataTypes.DoubleType), false)] // cross-family → NOT same-family
    [InlineData(nameof(DataTypes.ByteType), nameof(DataTypes.DoubleType), false)]    // cross-family → NOT same-family
    public void IsSameFamilyWidening_ExcludesCrossFamily(string from, string to, bool expected)
    {
        Assert.Equal(expected, TypeWidening.IsSameFamilyWidening(Resolve(from), Resolve(to)));
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
