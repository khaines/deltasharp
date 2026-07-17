using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

public class SlicingTests
{
    [Fact]
    public void FixedWidthSlice_HasConsistentLengthOffsetValueAndValidity()
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, capacity: 5);
        v.AppendValue(0);
        v.AppendNull();
        v.AppendValue(2);
        v.AppendValue(3);
        v.AppendNull();

        ColumnVector slice = v.Slice(1, 3); // logical rows 1..3 of the parent

        Assert.Equal(3, slice.Length);
        Assert.Equal(1, slice.Offset);
        Assert.True(slice.IsNull(0)); // parent row 1 was null
        Assert.False(slice.IsNull(1)); // parent row 2
        Assert.Equal(2, slice.GetValue<int>(1));
        Assert.Equal(3, slice.GetValue<int>(2));
        Assert.Equal(1, slice.NullCount);

        // The slice's typed span is offset-adjusted to its own logical [0, Length).
        ReadOnlySpan<int> span = slice.GetValues<int>();
        Assert.Equal(3, span.Length);
        Assert.Equal(2, span[1]);
    }

    [Fact]
    public void VariableWidthSlice_PreservesPerRowBytes()
    {
        MutableColumnVector v = ColumnVectors.Create(StringType.Instance, capacity: 3);
        v.AppendBytes(Encoding.UTF8.GetBytes("a"));
        v.AppendBytes(Encoding.UTF8.GetBytes("bb"));
        v.AppendBytes(Encoding.UTF8.GetBytes("ccc"));

        ColumnVector slice = v.Slice(1, 2);

        Assert.Equal(2, slice.Length);
        Assert.Equal("bb", Encoding.UTF8.GetString(slice.GetBytes(0)));
        Assert.Equal("ccc", Encoding.UTF8.GetString(slice.GetBytes(1)));
    }

    [Fact]
    public void Slice_RejectsOutOfRangeRanges()
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, capacity: 2);
        v.AppendValue(1);
        v.AppendValue(2);

        Assert.Throws<ArgumentOutOfRangeException>(() => v.Slice(1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => v.Slice(-1, 1));
    }

    [Fact]
    public void BatchSlice_SlicesEveryColumnConsistently()
    {
        var schema = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance),
            new StructField("name", StringType.Instance),
        });

        MutableColumnVector ids = ColumnVectors.Create(IntegerType.Instance, 4);
        MutableColumnVector names = ColumnVectors.Create(StringType.Instance, 4);
        for (int i = 0; i < 4; i++)
        {
            ids.AppendValue(i);
            names.AppendBytes(Encoding.UTF8.GetBytes($"n{i}"));
        }

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { ids, names }, rowCount: 4);
        ColumnBatch sliced = batch.Slice(1, 2);

        Assert.Equal(2, sliced.RowCount);
        Assert.Equal(2, sliced.ColumnCount);
        Assert.Equal(schema, sliced.Schema);
        foreach (int ordinal in new[] { 0, 1 })
        {
            Assert.Equal(2, sliced.Column(ordinal).Length);
            Assert.Equal(1, sliced.Column(ordinal).Offset);
        }

        Assert.Equal(1, sliced.Column(0).GetValue<int>(0));
        Assert.Equal("n2", Encoding.UTF8.GetString(sliced.Column(1).GetBytes(1)));
    }

    [Fact]
    public void FixedWidthSlice_OverflowingRange_IsRejected()
    {
        // #576: offset+length overflows int on a 0-length vector; the (long) guard must still reject it
        // (a plain int add would wrap negative and fail open, returning an invalid view).
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, capacity: 1); // 0 rows
        Assert.Throws<ArgumentOutOfRangeException>(() => v.Slice(int.MaxValue, 1));
    }

    [Fact]
    public void VariableWidthSlice_OverflowingRange_IsRejected()
    {
        // #576: same overflow guard on the variable-width lane.
        MutableColumnVector v = ColumnVectors.Create(StringType.Instance, capacity: 1); // 0 rows
        Assert.Throws<ArgumentOutOfRangeException>(() => v.Slice(int.MaxValue, 1));
    }
}
