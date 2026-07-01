using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

public class MutableColumnVectorTests
{
    [Fact]
    public void WrittenValuesAndNullBits_AreObservableThroughReads()
    {
        MutableColumnVector v = ColumnVectors.Create(LongType.Instance, capacity: 2);
        v.AppendValue(100L);
        v.AppendValue(200L);
        v.AppendNull();

        // Overwrite a value and toggle null bits, then read back (AC3).
        v.SetValue(1, 250L);
        v.SetNull(0);
        v.SetValue<long>(2, 300L); // was null -> now a value

        Assert.True(v.IsNull(0));
        Assert.Equal(250L, v.GetValue<long>(1));
        Assert.Equal(300L, v.GetValue<long>(2));
        Assert.False(v.IsNull(2));
        Assert.Equal(1, v.NullCount);
    }

    [Fact]
    public void AppendGrowsBeyondInitialCapacity()
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, capacity: 2);
        for (int i = 0; i < 100; i++)
        {
            v.AppendValue(i);
        }

        Assert.Equal(100, v.Length);
        Assert.Equal(99, v.GetValue<int>(99));
    }

    [Fact]
    public void Clear_ResetsToZeroRows()
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, capacity: 4);
        v.AppendValue(1);
        v.AppendNull();
        v.Clear();

        Assert.Equal(0, v.Length);
        Assert.False(v.HasNulls);
        Assert.Equal(0, v.NullCount);

        v.AppendValue(7);
        Assert.Equal(7, v.GetValue<int>(0));
        Assert.False(v.IsNull(0));
    }

    [Fact]
    public void Slice_IsReadOnly_AndRejectsMutation()
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, capacity: 4);
        v.AppendValue(1);
        v.AppendValue(2);

        ColumnVector slice = v.Slice(0, 2);
        var mutableSlice = Assert.IsAssignableFrom<MutableColumnVector>(slice);
        Assert.Throws<InvalidOperationException>(() => mutableSlice.AppendValue(3));
        Assert.Throws<InvalidOperationException>(() => mutableSlice.SetNull(0));
    }

    [Fact]
    public void SetValue_RejectsWrongElementType()
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, capacity: 1);
        v.AppendValue(1);
        Assert.Throws<InvalidOperationException>(() => v.SetValue(0, 5L));
    }
}
