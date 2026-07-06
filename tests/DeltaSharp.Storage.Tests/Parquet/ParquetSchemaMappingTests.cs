using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Parquet;
using Parquet.Schema;
using Xunit;
using Xunit.Abstractions;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Cross-engine schema correctness: (1) a DeltaSharp-written file's <b>physical/logical</b> Parquet
/// annotations (INT8/INT16 signedness, DATE, micros+isAdjustedToUTC TIMESTAMP, DECIMAL precision/scale)
/// are asserted via a Parquet.Net readback — so a wrong physical/annotation choice fails even though
/// our own self-readback would pass; and (2) a structurally valid file whose column type or nullability
/// disagrees with the requested engine type fails with a distinct <see cref="StorageErrorKind.SchemaMismatch"/>
/// (M2), not a generic "malformed" error.
/// </summary>
public sealed class ParquetSchemaMappingTests
{
    private readonly SeededRandom _random;

    public ParquetSchemaMappingTests(ITestOutputHelper output)
    {
        _random = SeededRandom.Create(output);
    }

    private static readonly StructType AllTypes = new(new[]
    {
        new StructField("bool", DataTypes.BooleanType, nullable: false),
        new StructField("byte", DataTypes.ByteType, nullable: false),
        new StructField("short", DataTypes.ShortType, nullable: false),
        new StructField("int", DataTypes.IntegerType, nullable: false),
        new StructField("long", DataTypes.LongType, nullable: false),
        new StructField("float", DataTypes.FloatType, nullable: false),
        new StructField("double", DataTypes.DoubleType, nullable: false),
        new StructField("string", DataTypes.StringType, nullable: false),
        new StructField("binary", DataTypes.BinaryType, nullable: false),
        new StructField("date", DataTypes.DateType, nullable: false),
        new StructField("ts", DataTypes.TimestampType, nullable: false),
        new StructField("dec_compact", DataTypes.CreateDecimalType(10, 2), nullable: false),
        new StructField("dec_wide", DataTypes.CreateDecimalType(24, 4), nullable: false),
    });

    [Fact]
    public async Task WrittenSchema_MatchesSparkPhysicalAndLogicalTypes()
    {
        ColumnBatch batch = TestData.RandomBatch(AllTypes, rowCount: 4, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(AllTypes, new[] { batch });

        using var stream = new MemoryStream(file, writable: false);
        ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        await using (reader.ConfigureAwait(false))
        {
            DataField Field(string name) => Array.Find(reader.Schema.DataFields, f => f.Name == name)!;

            Assert.Equal(typeof(bool), Field("bool").ClrType);
            Assert.Equal(typeof(sbyte), Field("byte").ClrType);   // signed INT8 (Spark tinyint).
            Assert.Equal(typeof(short), Field("short").ClrType);  // signed INT16 (Spark smallint).
            Assert.Equal(typeof(int), Field("int").ClrType);
            Assert.Equal(typeof(long), Field("long").ClrType);
            Assert.Equal(typeof(float), Field("float").ClrType);
            Assert.Equal(typeof(double), Field("double").ClrType);
            Assert.Equal(typeof(string), Field("string").ClrType);
            Assert.Equal(typeof(byte[]), Field("binary").ClrType);

            var date = Assert.IsType<DateTimeDataField>(Field("date"));
            Assert.Equal(DateTimeFormat.Date, date.DateTimeFormat);

            var ts = Assert.IsType<DateTimeDataField>(Field("ts"));
            Assert.Equal(DateTimeFormat.DateAndTimeMicros, ts.DateTimeFormat);
            Assert.Equal(DateTimeTimeUnit.Micros, ts.Unit);
            Assert.True(ts.IsAdjustedToUTC);

            var compact = Assert.IsType<DecimalDataField>(Field("dec_compact"));
            Assert.Equal(10, compact.Precision);
            Assert.Equal(2, compact.Scale);

            var wide = Assert.IsType<DecimalDataField>(Field("dec_wide"));
            Assert.Equal(24, wide.Precision);
            Assert.Equal(4, wide.Scale);
        }
    }

    [Fact]
    public async Task ReadWithWrongPhysicalType_ThrowsSchemaMismatch()
    {
        // File has an INT32 column; requesting it as LONG is a physical-type disagreement.
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.IntegerType, nullable: false) });
        ColumnBatch batch = TestData.RandomBatch(writeSchema, rowCount: 4, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.LongType, nullable: false) });
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(file, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    [Fact]
    public async Task ReadTimestampAsDate_ThrowsSchemaMismatch()
    {
        // File has a DATE column; requesting it as TIMESTAMP shares ClrType but disagrees on annotation.
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.DateType, nullable: false) });
        ColumnBatch batch = TestData.RandomBatch(writeSchema, rowCount: 4, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.TimestampType, nullable: false) });
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(file, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    [Fact]
    public async Task ReadWithWrongDecimalScale_ThrowsSchemaMismatch()
    {
        var writeSchema = new StructType(new[]
        {
            new StructField("v", DataTypes.CreateDecimalType(10, 2), nullable: false),
        });
        ColumnBatch batch = TestData.RandomBatch(writeSchema, rowCount: 4, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[]
        {
            new StructField("v", DataTypes.CreateDecimalType(10, 4), nullable: false),
        });
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(file, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    [Fact]
    public async Task ReadNullableColumnAsRequired_ThrowsSchemaMismatch()
    {
        // A nullable INT32 column read as a required lane could inject a null: reject deterministically.
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.IntegerType, nullable: true) });
        ColumnBatch batch = TestData.RandomBatch(writeSchema, rowCount: 4, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.IntegerType, nullable: false) });
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(file, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }
}
