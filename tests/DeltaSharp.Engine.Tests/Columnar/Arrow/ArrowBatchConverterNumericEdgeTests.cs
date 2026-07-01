using Apache.Arrow;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Columnar.Arrow;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// Floating-point and empty-batch edges (council columnar F6). The all-types round-trip carries only
/// "ordinary" floats, so it can't catch a writer that canonicalizes <c>NaN</c> or flushes <c>-0.0</c>
/// to <c>+0.0</c> — value equality would miss both (xUnit treats <c>-0.0 == 0.0</c>, and a NaN-to-zero
/// regression escapes a <c>==</c> oracle). These assert <b>bit-exact</b> survival via
/// <see cref="System.BitConverter"/>, and that empty (zero-row) and zero-column batches survive the
/// boundary intact.
/// </summary>
public class ArrowBatchConverterNumericEdgeTests
{
    [Fact]
    public void RoundTrip_FloatNaNAndNegativeZero_PreservesExactBits()
    {
        float[] values =
        {
            float.NaN,
            -0.0f,
            0.0f,
            float.PositiveInfinity,
            float.NegativeInfinity,
            3.5f,
        };

        MutableColumnVector vector = ColumnVectors.Create(FloatType.Instance, values.Length + 1);
        foreach (float value in values)
        {
            vector.AppendValue(value);
        }

        vector.AppendNull();

        var batch = new ManagedColumnBatch(
            new StructType(new[] { new StructField("f", FloatType.Instance) }), new[] { vector }, values.Length + 1);

        using RecordBatch arrow = ArrowBatchConverter.ToArrow(batch);
        using ArrowColumnBatch back = ArrowBatchConverter.FromArrow(arrow);

        ColumnVector column = back.Column(0);
        Assert.Equal(values.Length + 1, column.Length);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.False(column.IsNull(i));
            Assert.Equal(
                BitConverter.SingleToInt32Bits(values[i]),
                BitConverter.SingleToInt32Bits(column.GetValue<float>(i)));
        }

        Assert.True(column.IsNull(values.Length));
    }

    [Fact]
    public void RoundTrip_DoubleNaNAndNegativeZero_PreservesExactBits()
    {
        double[] values =
        {
            double.NaN,
            -0.0d,
            0.0d,
            double.PositiveInfinity,
            double.NegativeInfinity,
            3.5d,
        };

        MutableColumnVector vector = ColumnVectors.Create(DoubleType.Instance, values.Length + 1);
        foreach (double value in values)
        {
            vector.AppendValue(value);
        }

        vector.AppendNull();

        var batch = new ManagedColumnBatch(
            new StructType(new[] { new StructField("d", DoubleType.Instance) }), new[] { vector }, values.Length + 1);

        using RecordBatch arrow = ArrowBatchConverter.ToArrow(batch);
        using ArrowColumnBatch back = ArrowBatchConverter.FromArrow(arrow);

        ColumnVector column = back.Column(0);
        Assert.Equal(values.Length + 1, column.Length);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.False(column.IsNull(i));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(values[i]),
                BitConverter.DoubleToInt64Bits(column.GetValue<double>(i)));
        }

        Assert.True(column.IsNull(values.Length));
    }

    [Fact]
    public void RoundTrip_ZeroRowBatch_PreservesSchemaAndEmptiness()
    {
        MutableColumnVector vector = ColumnVectors.Create(IntegerType.Instance, 0);
        var batch = new ManagedColumnBatch(
            new StructType(new[] { new StructField("i", IntegerType.Instance) }), new[] { vector }, 0);

        using RecordBatch arrow = ArrowBatchConverter.ToArrow(batch);
        Assert.Equal(1, arrow.ColumnCount);
        Assert.Equal(0, arrow.Length);

        using ArrowColumnBatch back = ArrowBatchConverter.FromArrow(arrow);
        Assert.Equal(1, back.ColumnCount);
        Assert.Equal(0, back.RowCount);
        Assert.IsType<IntegerType>(back.Schema[0].DataType);
        Assert.Equal(0, back.Column(0).Length);
    }

    [Fact]
    public void RoundTrip_ZeroColumnBatch_PreservesEmptiness()
    {
        var batch = new ManagedColumnBatch(StructType.Empty, System.Array.Empty<ColumnVector>(), 0);

        using RecordBatch arrow = ArrowBatchConverter.ToArrow(batch);
        Assert.Equal(0, arrow.ColumnCount);
        Assert.Equal(0, arrow.Length);

        using ArrowColumnBatch back = ArrowBatchConverter.FromArrow(arrow);
        Assert.Equal(0, back.ColumnCount);
        Assert.Equal(0, back.RowCount);
    }
}
