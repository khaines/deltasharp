using System.Collections.Generic;
using System.Linq;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// Exhaustive read-side promotion matrix for the Delta <c>typeWidening</c> feature (#495). An OLD data file
/// physically stores the NARROW type; the current table schema is the WIDE type. Per Delta PROTOCOL.md
/// "Reader Requirements for Type Widening" the reader must read the file's physical (narrow) values and
/// convert them into the current wide type. Each case writes a narrow file then reads it back under the
/// widened schema and asserts the promoted values, for every sanctioned physical width.
/// </summary>
public sealed class ParquetTypeWideningPromotionTests
{
    private static ManagedColumnBatch OneColumn(DataType type, System.Action<MutableColumnVector> fill, int rowCount)
    {
        MutableColumnVector column = ColumnVectors.Create(type, capacity: rowCount);
        fill(column);
        var schema = new StructType(new[] { new StructField("v", type, nullable: true) });
        return new ManagedColumnBatch(schema, new ColumnVector[] { column }, rowCount);
    }

    private static async Task<ColumnVector> PromoteAsync(DataType narrow, DataType wide, System.Action<MutableColumnVector> fill, int rowCount)
    {
        var writeSchema = new StructType(new[] { new StructField("v", narrow, nullable: true) });
        ManagedColumnBatch batch = OneColumn(narrow, fill, rowCount);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", wide, nullable: true) });
        List<ColumnBatch> read = await ParquetTestHelpers.ReadAllAsync(bytes, readSchema);
        ColumnVector vector = read.Single().Column(0);
        Assert.Equal(wide, vector.Type);
        return vector;
    }

    [Fact]
    public async Task Byte_To_Short()
    {
        ColumnVector v = await PromoteAsync(DataTypes.ByteType, DataTypes.ShortType,
            c => { c.AppendValue((byte)1); c.AppendValue((byte)127); }, 2);
        Assert.Equal(new short[] { 1, 127 }, v.GetValues<short>().ToArray());
    }

    [Fact]
    public async Task Byte_To_Int()
    {
        ColumnVector v = await PromoteAsync(DataTypes.ByteType, DataTypes.IntegerType,
            c => { c.AppendValue((byte)1); c.AppendValue((byte)127); }, 2);
        Assert.Equal(new[] { 1, 127 }, v.GetValues<int>().ToArray());
    }

    [Fact]
    public async Task Byte_To_Long()
    {
        ColumnVector v = await PromoteAsync(DataTypes.ByteType, DataTypes.LongType,
            c => { c.AppendValue((byte)1); c.AppendValue((byte)127); }, 2);
        Assert.Equal(new long[] { 1L, 127L }, v.GetValues<long>().ToArray());
    }

    [Fact]
    public async Task Short_To_Int()
    {
        ColumnVector v = await PromoteAsync(DataTypes.ShortType, DataTypes.IntegerType,
            c => { c.AppendValue((short)-5); c.AppendValue((short)30000); }, 2);
        Assert.Equal(new[] { -5, 30000 }, v.GetValues<int>().ToArray());
    }

    [Fact]
    public async Task Short_To_Long()
    {
        ColumnVector v = await PromoteAsync(DataTypes.ShortType, DataTypes.LongType,
            c => { c.AppendValue((short)-5); c.AppendValue((short)30000); }, 2);
        Assert.Equal(new long[] { -5L, 30000L }, v.GetValues<long>().ToArray());
    }

    [Fact]
    public async Task Int_To_Long()
    {
        ColumnVector v = await PromoteAsync(DataTypes.IntegerType, DataTypes.LongType,
            c => { c.AppendValue(1); c.AppendValue(-2_000_000_000); }, 2);
        Assert.Equal(new long[] { 1L, -2_000_000_000L }, v.GetValues<long>().ToArray());
    }

    [Fact]
    public async Task Float_To_Double()
    {
        ColumnVector v = await PromoteAsync(DataTypes.FloatType, DataTypes.DoubleType,
            c => { c.AppendValue(1.5f); c.AppendValue(-0.25f); }, 2);
        Assert.Equal(new[] { 1.5d, -0.25d }, v.GetValues<double>().ToArray());
    }

    [Fact]
    public async Task Decimal_GrowOnly_Rescales()
    {
        var narrow = new DecimalType(6, 2);
        var wide = new DecimalType(10, 4);
        ColumnVector v = await PromoteAsync(narrow, wide,
            c =>
            {
                ParquetTypeMapping.AppendDecimal(c, narrow, 12.34m);
                ParquetTypeMapping.AppendDecimal(c, narrow, -0.05m);
            }, 2);
        Assert.Equal(12.3400m, ParquetTypeMapping.ReadDecimal(v, wide, 0));
        Assert.Equal(-0.0500m, ParquetTypeMapping.ReadDecimal(v, wide, 1));
    }

    [Fact]
    public async Task Int_To_Long_WithNulls_PromotesAndPreservesNulls()
    {
        ColumnVector v = await PromoteAsync(DataTypes.IntegerType, DataTypes.LongType,
            c => { c.AppendValue(7); c.AppendNull(); c.AppendValue(9); }, 3);
        Assert.False(v.IsNull(0));
        Assert.True(v.IsNull(1));
        Assert.False(v.IsNull(2));
        Assert.Equal(7L, v.GetValue<long>(0));
        Assert.Equal(9L, v.GetValue<long>(2));
    }

    [Fact]
    public async Task Int_To_Long_ComposesWithNullFillMissingColumn()
    {
        // OLD file: single Int32 column "v". CURRENT schema: widened `long v` PLUS a brand-new column "added"
        // that the old file never had. Read must both promote v (int->long) AND null-fill "added" (#497).
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.IntegerType, nullable: true) });
        ManagedColumnBatch batch = OneColumn(DataTypes.IntegerType,
            c => { c.AppendValue(11); c.AppendValue(22); }, 2);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[]
        {
            new StructField("v", DataTypes.LongType, nullable: true),
            new StructField("added", DataTypes.StringType, nullable: true),
        });

        using var stream = new System.IO.MemoryStream(bytes, writable: false);
        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch b in new ParquetFileReader().ReadAsync(
            stream, readSchema, keepRowGroup: null, nullFillMissingColumns: true, System.Threading.CancellationToken.None))
        {
            batches.Add(b);
        }

        ColumnBatch result = batches.Single();
        Assert.Equal(new long[] { 11L, 22L }, result.Column(0).GetValues<long>().ToArray());
        Assert.True(result.Column(1).IsNull(0));
        Assert.True(result.Column(1).IsNull(1));
    }
}
