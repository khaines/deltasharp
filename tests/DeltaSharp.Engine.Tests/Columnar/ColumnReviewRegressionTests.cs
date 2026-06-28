using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Regression guards for findings raised during the STORY-02.1.1 (#133) review council.
/// </summary>
public class ColumnReviewRegressionTests
{
    [Fact]
    public void Slicing_SealsTheFixedWidthOwner_AgainstFurtherMutation()
    {
        // Red-team repro: slice-then-mutate-parent previously corrupted the slice silently; it now
        // throws a clear error instead (seal-on-slice).
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, 1);
        v.AppendValue(10);
        ColumnVector slice = v.Slice(0, 1);

        Assert.Throws<InvalidOperationException>(() => v.SetNull(0));
        Assert.Throws<InvalidOperationException>(() => v.AppendValue(200)); // would have forced a detaching resize
        Assert.Throws<InvalidOperationException>(() => v.SetValue(0, 9));
        Assert.Throws<InvalidOperationException>(() => v.AppendNull());
        Assert.Throws<InvalidOperationException>(() => v.Clear());

        // The slice remains valid and consistent.
        Assert.Equal(10, slice.GetValue<int>(0));
        Assert.False(slice.IsNull(0));
        Assert.Equal(0, slice.NullCount);
    }

    [Fact]
    public void Slicing_SealsTheVariableWidthOwner()
    {
        MutableColumnVector v = ColumnVectors.Create(StringType.Instance, 1);
        v.AppendBytes("a"u8);
        _ = v.Slice(0, 1);

        Assert.Throws<InvalidOperationException>(() => v.AppendBytes("b"u8));
        Assert.Throws<InvalidOperationException>(() => v.SetNull(0));
    }

    [Fact]
    public void GetBytes_ReturnsEmpty_ForARowSetToNull()
    {
        MutableColumnVector v = ColumnVectors.Create(StringType.Instance, 1);
        v.AppendBytes("hello"u8);
        v.SetNull(0);

        Assert.True(v.IsNull(0));
        Assert.True(v.GetBytes(0).IsEmpty); // honors the documented empty-for-null contract
    }

    [Fact]
    public void GetValue_OutOfRange_ThrowsArgumentOutOfRange_LikeSiblingAccessors()
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, 1);
        v.AppendValue(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => v.GetValue<int>(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => v.GetValue<int>(-1));
    }

    [Fact]
    public void CreateForSchema_BuildsOneVectorPerField()
    {
        var schema = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance),
            new StructField("name", StringType.Instance),
        });

        MutableColumnVector[] columns = ColumnVectors.CreateForSchema(schema, capacity: 4);

        Assert.Equal(2, columns.Length);
        Assert.Equal(IntegerType.Instance, columns[0].Type);
        Assert.Equal(StringType.Instance, columns[1].Type);

        columns[0].AppendValue(7);
        columns[1].AppendBytes("x"u8);
        var batch = new ManagedColumnBatch(schema, columns, rowCount: 1);
        Assert.Equal(7, batch.Column("id").GetValue<int>(0));
    }
}
