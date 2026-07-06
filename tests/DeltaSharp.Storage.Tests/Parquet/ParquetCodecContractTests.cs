using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Codec-contract tests for the deterministic temporal/decimal conversions in
/// <see cref="ParquetTypeMapping"/>: an out-of-range or over-precision value maps to a deterministic
/// <see cref="StorageErrorKind.CorruptData"/> rather than letting a raw runtime exception escape the
/// codec boundary (CF-5, CF-7, CF-9). Each guard is exercised <b>non-vacuously</b> — removing it lets a
/// raw exception escape or a bad value through, reddening the corresponding test.
/// </summary>
public sealed class ParquetCodecContractTests
{
    [Fact]
    public void EpochDayToDateTime_OutOfRange_MapsToCorruptData()
    {
        // CF-9: a DATE epoch-day far outside DateTime's range maps to CorruptData, not a raw
        // ArgumentOutOfRangeException (mirroring EpochMicrosToDateTime's contract).
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.EpochDayToDateTime(int.MaxValue));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public void AppendDecimal_ScaleMultiplyOverflow_MapsToCorruptData()
    {
        // CF-7: an overflow of `value * 10^scale` maps to CorruptData (mirroring the engine's
        // LocalRelationBatches.AppendDecimal), not a raw OverflowException escaping the codec contract.
        var type = new DecimalType(28, 28); // scale factor 10^28
        MutableColumnVector vector = ColumnVectors.Create(type, 1);
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.AppendDecimal(vector, type, 100m)); // 100 * 1e28 overflows decimal
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public void AppendDecimal_ExceedsDeclaredPrecision_MapsToCorruptData()
    {
        // CF-5: the over-precision guard (using the hoisted 10^precision ceiling) still rejects a value
        // that does not fit the declared decimal(P,S). Proves the CF-5 Pow10 hoist preserved the guard.
        var type = new DecimalType(5, 0); // precision ceiling 10^5
        MutableColumnVector vector = ColumnVectors.Create(type, 1);
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.AppendDecimal(vector, type, 100000m)); // magnitude 100000 >= 10^5
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public void AppendDecimal_WithinDeclaredPrecision_Appends()
    {
        // Positive control so the over-precision test is not vacuous by rejecting everything: a value that
        // DOES fit is appended.
        var type = new DecimalType(5, 0);
        MutableColumnVector vector = ColumnVectors.Create(type, 1);
        ParquetTypeMapping.AppendDecimal(vector, type, 99999m); // magnitude 99999 < 10^5
        Assert.Equal(1, vector.Length);
    }
}
