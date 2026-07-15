using System.Collections.Generic;
using System.Linq;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
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
        List<ColumnBatch> read = await ParquetTestHelpers.ReadAllAsync(bytes, readSchema, allowTypeWideningPromotion: true);
        ColumnVector vector = read.Single().Column(0);
        Assert.Equal(wide, vector.Type);
        return vector;
    }

    [Fact]
    public async Task Byte_To_Short()
    {
        // Includes NEGATIVE bytes: a masked/unsigned sign-extension regression (e.g. treating sbyte as byte)
        // would read -1 as 255 and -128 as 128, so these samples pin the signed upcast.
        ColumnVector v = await PromoteAsync(DataTypes.ByteType, DataTypes.ShortType,
            c => { c.AppendValue((byte)1); c.AppendValue((byte)127); c.AppendValue(unchecked((byte)(sbyte)-1)); c.AppendValue(unchecked((byte)(sbyte)-128)); }, 4);
        Assert.Equal(new short[] { 1, 127, -1, -128 }, v.GetValues<short>().ToArray());
    }

    [Fact]
    public async Task Byte_To_Int()
    {
        ColumnVector v = await PromoteAsync(DataTypes.ByteType, DataTypes.IntegerType,
            c => { c.AppendValue((byte)1); c.AppendValue((byte)127); c.AppendValue(unchecked((byte)(sbyte)-1)); c.AppendValue(unchecked((byte)(sbyte)-128)); }, 4);
        Assert.Equal(new[] { 1, 127, -1, -128 }, v.GetValues<int>().ToArray());
    }

    [Fact]
    public async Task Byte_To_Long()
    {
        ColumnVector v = await PromoteAsync(DataTypes.ByteType, DataTypes.LongType,
            c => { c.AppendValue((byte)1); c.AppendValue((byte)127); c.AppendValue(unchecked((byte)(sbyte)-1)); c.AppendValue(unchecked((byte)(sbyte)-128)); }, 4);
        Assert.Equal(new long[] { 1L, 127L, -1L, -128L }, v.GetValues<long>().ToArray());
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
    public async Task NarrowFile_WideSchema_WithoutPromotionGate_FailsClosed_NotSilentlyPromoted()
    {
        // FIX 1 (fail-close): an OLD Int32 file read under a WIDE `long` schema. The physical→requested type
        // is a sanctioned widening, but the read-side promotion gate is CLOSED (allowTypeWideningPromotion:
        // false) — as it is for a table whose protocol does NOT declare the `typeWidening` feature (a
        // tampered/malformed external log). The read must FAIL CLOSED as SchemaMismatch, never silently
        // "repair" the narrow file into the wide type.
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.IntegerType, nullable: true) });
        ManagedColumnBatch batch = OneColumn(DataTypes.IntegerType, c => { c.AppendValue(1); c.AppendValue(2); }, 2);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.LongType, nullable: true) });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(bytes, readSchema, keepRowGroup: null, allowTypeWideningPromotion: false));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Fact]
    public async Task NarrowFile_WideSchema_WithPromotionGate_Promotes()
    {
        // The SAME narrow file + wide schema, but with the promotion gate OPEN (as for a table whose protocol
        // declares `typeWidening`) → the reader promotes int→long. This pins the enabled/disabled asymmetry.
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.IntegerType, nullable: true) });
        ManagedColumnBatch batch = OneColumn(DataTypes.IntegerType, c => { c.AppendValue(1); c.AppendValue(2); }, 2);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.LongType, nullable: true) });
        List<ColumnBatch> read = await ParquetTestHelpers.ReadAllAsync(bytes, readSchema, keepRowGroup: null, allowTypeWideningPromotion: true);
        Assert.Equal(new long[] { 1L, 2L }, read.Single().Column(0).GetValues<long>().ToArray());
    }

    [Fact]
    public async Task Byte_To_Double_CrossFamily()
    {
        // #535: cross-family byte→double. Includes NEGATIVE bytes to pin the signed upcast (not unsigned).
        ColumnVector v = await PromoteAsync(DataTypes.ByteType, DataTypes.DoubleType,
            c => { c.AppendValue((byte)1); c.AppendValue((byte)127); c.AppendValue(unchecked((byte)(sbyte)-128)); }, 3);
        Assert.Equal(new[] { 1d, 127d, -128d }, v.GetValues<double>().ToArray());
    }

    [Fact]
    public async Task Short_To_Double_CrossFamily()
    {
        ColumnVector v = await PromoteAsync(DataTypes.ShortType, DataTypes.DoubleType,
            c => { c.AppendValue((short)-5); c.AppendValue((short)30000); }, 2);
        Assert.Equal(new[] { -5d, 30000d }, v.GetValues<double>().ToArray());
    }

    [Fact]
    public async Task Int_To_Double_CrossFamily()
    {
        // #535: int→double. 2147483647 (int.MaxValue) is representable exactly in a double (< 2^53).
        ColumnVector v = await PromoteAsync(DataTypes.IntegerType, DataTypes.DoubleType,
            c => { c.AppendValue(1); c.AppendValue(-2_000_000_000); c.AppendValue(int.MaxValue); }, 3);
        Assert.Equal(new[] { 1d, -2_000_000_000d, 2147483647d }, v.GetValues<double>().ToArray());
    }

    [Fact]
    public async Task Int_To_Double_CrossFamily_WithNulls_PromotesAndPreservesNulls()
    {
        ColumnVector v = await PromoteAsync(DataTypes.IntegerType, DataTypes.DoubleType,
            c => { c.AppendValue(7); c.AppendNull(); c.AppendValue(9); }, 3);
        Assert.False(v.IsNull(0));
        Assert.True(v.IsNull(1));
        Assert.False(v.IsNull(2));
        Assert.Equal(7d, v.GetValue<double>(0));
        Assert.Equal(9d, v.GetValue<double>(2));
    }

    [Fact]
    public async Task Int_To_Decimal_CrossFamily()
    {
        // #535: int→decimal(12,2). The narrow physical int is read then widened into the decimal lane; the
        // integer value V becomes V.00 (scaled by 10^scale). decimal(12,2) has p−s = 10 ≥ 10 int digits.
        var wide = DataTypes.CreateDecimalType(12, 2);
        ColumnVector v = await PromoteAsync(DataTypes.IntegerType, wide,
            c => { c.AppendValue(5); c.AppendValue(-3); c.AppendValue(int.MaxValue); }, 3);
        Assert.Equal(5.00m, ParquetTypeMapping.ReadDecimal(v, wide, 0));
        Assert.Equal(-3.00m, ParquetTypeMapping.ReadDecimal(v, wide, 1));
        Assert.Equal(2147483647.00m, ParquetTypeMapping.ReadDecimal(v, wide, 2));
    }

    [Fact]
    public async Task Long_To_Decimal_CrossFamily()
    {
        // #535: long→decimal(20,0). long is INT64 → the threshold is p−s ≥ 20; long.MaxValue/MinValue promote
        // losslessly.
        var wide = DataTypes.CreateDecimalType(20, 0);
        ColumnVector v = await PromoteAsync(DataTypes.LongType, wide,
            c => { c.AppendValue(long.MaxValue); c.AppendValue(long.MinValue); c.AppendValue(0L); }, 3);
        Assert.Equal(9223372036854775807m, ParquetTypeMapping.ReadDecimal(v, wide, 0));
        Assert.Equal(-9223372036854775808m, ParquetTypeMapping.ReadDecimal(v, wide, 1));
        Assert.Equal(0m, ParquetTypeMapping.ReadDecimal(v, wide, 2));
    }

    [Fact]
    public async Task Byte_To_Decimal_CrossFamily_WithNulls()
    {
        // byte is stored as INT32, so the Delta threshold is decimal(10,0)+ (p−s ≥ 10), not the byte value
        // range; nulls preserved through the promotion.
        var wide = DataTypes.CreateDecimalType(10, 0);
        ColumnVector v = await PromoteAsync(DataTypes.ByteType, wide,
            c => { c.AppendValue((byte)7); c.AppendNull(); c.AppendValue(unchecked((byte)(sbyte)-1)); }, 3);
        Assert.Equal(7m, ParquetTypeMapping.ReadDecimal(v, wide, 0));
        Assert.True(v.IsNull(1));
        Assert.Equal(-1m, ParquetTypeMapping.ReadDecimal(v, wide, 2));
    }

    [Fact]
    public async Task Short_To_Decimal_CrossFamily()
    {
        // short (INT32 physical) → decimal(10,0) — covers the short→decimal read lane incl. boundary values.
        var wide = DataTypes.CreateDecimalType(10, 0);
        ColumnVector v = await PromoteAsync(DataTypes.ShortType, wide,
            c => { c.AppendValue((short)32767); c.AppendValue((short)-32768); c.AppendValue((short)0); }, 3);
        Assert.Equal(32767m, ParquetTypeMapping.ReadDecimal(v, wide, 0));
        Assert.Equal(-32768m, ParquetTypeMapping.ReadDecimal(v, wide, 1));
        Assert.Equal(0m, ParquetTypeMapping.ReadDecimal(v, wide, 2));
    }

    [Fact]
    public async Task CrossFamily_Int_To_Double_WithoutPromotionGate_FailsClosed()
    {
        // A narrow Int32 file read under a WIDE `double` schema with the promotion gate CLOSED (no
        // `typeWidening` feature) must FAIL CLOSED as SchemaMismatch — never silently promoted.
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.IntegerType, nullable: true) });
        ManagedColumnBatch batch = OneColumn(DataTypes.IntegerType, c => { c.AppendValue(1); c.AppendValue(2); }, 2);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.DoubleType, nullable: true) });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(bytes, readSchema, keepRowGroup: null, allowTypeWideningPromotion: false));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Fact]
    public async Task Date_To_TimestampNtz()
    {
        // date (epoch-day INT32) → timestamp_ntz (epoch-micros INT64 at MIDNIGHT of the date, no session
        // offset). #533. A real DATE file is written and read back under the widened timestamp_ntz schema; the
        // promoted values are asserted to the exact midnight-micros, so a wrong scale/offset fails.
        const long microsPerDay = 86_400L * 1_000_000L;
        ColumnVector v = await PromoteAsync(DataTypes.DateType, DataTypes.TimestampNtzType,
            c => { c.AppendValue(0); c.AppendValue(1); c.AppendValue(-1); c.AppendValue(18_000); }, 4);
        Assert.Equal(
            new[] { 0L, microsPerDay, -microsPerDay, 18_000L * microsPerDay },
            v.GetValues<long>().ToArray());
    }

    [Fact]
    public async Task Date_To_TimestampNtz_WithNulls_PromotesAndPreservesNulls()
    {
        const long microsPerDay = 86_400L * 1_000_000L;
        ColumnVector v = await PromoteAsync(DataTypes.DateType, DataTypes.TimestampNtzType,
            c => { c.AppendValue(2); c.AppendNull(); c.AppendValue(-3); }, 3);
        Assert.Equal(2L * microsPerDay, v.GetValue<long>(0));
        Assert.True(v.IsNull(1));
        Assert.Equal(-3L * microsPerDay, v.GetValue<long>(2));
    }

    [Fact]
    public async Task Date_To_TimestampNtz_WithoutPromotionGate_FailsClosed()
    {
        // A DATE file read under a WIDE `timestamp_ntz` schema with the promotion gate CLOSED (no
        // `typeWidening` feature) must FAIL CLOSED as SchemaMismatch — never silently promoted.
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.DateType, nullable: true) });
        ManagedColumnBatch batch = OneColumn(DataTypes.DateType, c => { c.AppendValue(0); c.AppendValue(1); }, 2);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.TimestampNtzType, nullable: true) });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(bytes, readSchema, keepRowGroup: null, allowTypeWideningPromotion: false));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Fact]
    public async Task Date_To_TimestampLtz_IsNotPromotable_FailsClosedEvenWithGate()
    {
        // date→timestamp with a timezone (LTZ) is NOT a sanctioned widening (only date→timestamp_ntz is), so
        // even with the promotion gate OPEN it must fail closed — the reader never silently reinterprets a
        // DATE file as an LTZ timestamp.
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.DateType, nullable: true) });
        ManagedColumnBatch batch = OneColumn(DataTypes.DateType, c => { c.AppendValue(0); c.AppendValue(1); }, 2);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.TimestampType, nullable: true) });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(bytes, readSchema, keepRowGroup: null, allowTypeWideningPromotion: true));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
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
            stream, readSchema, keepRowGroup: null, nullFillMissingColumns: true, allowTypeWideningPromotion: true, System.Threading.CancellationToken.None))
        {
            batches.Add(b);
        }

        ColumnBatch result = batches.Single();
        Assert.Equal(new long[] { 11L, 22L }, result.Column(0).GetValues<long>().ToArray());
        Assert.True(result.Column(1).IsNull(0));
        Assert.True(result.Column(1).IsNull(1));
    }
}
