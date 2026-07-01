using System.Buffers.Binary;
using Apache.Arrow;
using DeltaSharp.Engine.Columnar.Arrow;
using DeltaSharp.Types;
using Xunit;
using ArrowDecimal128Type = Apache.Arrow.Types.Decimal128Type;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// Decimal128 fail-closed boundary (council Security F-DEC1, F-DEC2). Apache.Arrow admits decimal128
/// types and values DeltaSharp cannot represent: a precision above the Spark-parity cap of 38, and a
/// compact (precision &lt;= 18) column whose 128-bit unscaled value carries more significant digits
/// than its precision. Both must raise <see cref="UnsupportedTypeException"/> at the boundary rather
/// than leak a foreign exception (F-DEC1) or silently truncate on the narrowing <c>(long)</c> cast
/// (F-DEC2).
/// </summary>
public class ArrowBatchConverterDecimalTests
{
    [Fact]
    public void FromArrow_Decimal128PrecisionAbove38_ThrowsUnsupportedType()
    {
        Decimal128Array column = new Decimal128Array.Builder(new ArrowDecimal128Type(40, 2)).Build();
        using RecordBatch source = ArrowConverterTestSupport.RecordBatchOf(("d", column, true));

        // FromArrow resolves the decimal type through the mapper, so the precision>38 gap fails closed
        // here too (not just via the mapper directly).
        Assert.Throws<UnsupportedTypeException>(() => ArrowBatchConverter.FromArrow(source));
    }

    [Fact]
    public void FromArrow_CompactDecimalUnscaledExceedsPrecision_ThrowsUnsupportedType()
    {
        // A compact decimal128(10, 2) (precision <= 18, so DeltaSharp narrows the unscaled value to a
        // long) carrying an unscaled magnitude of 10^30 — far more than the declared precision admits.
        // The (long) cast would silently truncate it to 5076944270305263616 (a wrong, non-zero value),
        // so the boundary must reject it instead.
        var type = new ArrowDecimal128Type(10, 2);
        var unscaledBytes = new byte[16];
        Int128 unscaled = Int128.Parse("1000000000000000000000000000000"); // 10^30
        BinaryPrimitives.WriteInt128LittleEndian(unscaledBytes, unscaled);
        var data = new ArrayData(
            type, length: 1, nullCount: 0, offset: 0, new[] { ArrowBuffer.Empty, new ArrowBuffer(unscaledBytes) });
        var column = new Decimal128Array(data);
        using RecordBatch source = ArrowConverterTestSupport.RecordBatchOf(("d", column, true));

        Assert.Throws<UnsupportedTypeException>(() => ArrowBatchConverter.FromArrow(source));
    }
}
