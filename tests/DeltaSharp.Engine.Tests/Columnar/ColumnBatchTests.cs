using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

public class ColumnBatchTests
{
    private static (StructType Schema, MutableColumnVector Ids, MutableColumnVector Flags) BuildColumns(int rows)
    {
        var schema = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance),
            new StructField("flag", BooleanType.Instance),
        });
        MutableColumnVector ids = ColumnVectors.Create(IntegerType.Instance, rows);
        MutableColumnVector flags = ColumnVectors.Create(BooleanType.Instance, rows);
        for (int i = 0; i < rows; i++)
        {
            ids.AppendValue(i);
            flags.AppendValue(i % 2 == 0);
        }

        return (schema, ids, flags);
    }

    [Fact]
    public void Batch_ExposesSchemaColumnsByOrdinalAndName()
    {
        (StructType schema, MutableColumnVector ids, MutableColumnVector flags) = BuildColumns(3);
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { ids, flags }, rowCount: 3);

        Assert.Equal(schema, batch.Schema);
        Assert.Equal(3, batch.RowCount);
        Assert.Equal(2, batch.ColumnCount);
        Assert.Same(ids, batch.Column(0));
        Assert.Same(flags, batch.Column("flag"));
        Assert.Equal(3, batch.LogicalRowCount); // no selection
        Assert.Null(batch.Selection);
    }

    [Fact]
    public void Column_RejectsUnknownNameAndOrdinal()
    {
        (StructType schema, MutableColumnVector ids, MutableColumnVector flags) = BuildColumns(1);
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { ids, flags }, rowCount: 1);

        Assert.Throws<KeyNotFoundException>(() => batch.Column("missing"));
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Column(5));
    }

    [Fact]
    public void Construction_RejectsTypeMismatchWithSchema()
    {
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance) });
        MutableColumnVector wrong = ColumnVectors.Create(LongType.Instance, 1);
        wrong.AppendValue(1L);

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            new ManagedColumnBatch(schema, new ColumnVector[] { wrong }, rowCount: 1));
        Assert.Contains("type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Construction_RejectsColumnCountAndLengthMismatch()
    {
        var schema = new StructType(new[]
        {
            new StructField("a", IntegerType.Instance),
            new StructField("b", IntegerType.Instance),
        });
        MutableColumnVector only = ColumnVectors.Create(IntegerType.Instance, 2);
        only.AppendValue(1);
        only.AppendValue(2);

        Assert.Throws<ArgumentException>(() =>
            new ManagedColumnBatch(schema, new ColumnVector[] { only }, rowCount: 2)); // count mismatch

        var single = new StructType(new[] { new StructField("a", IntegerType.Instance) });
        Assert.Throws<ArgumentException>(() =>
            new ManagedColumnBatch(single, new ColumnVector[] { only }, rowCount: 5)); // length mismatch
    }
}
