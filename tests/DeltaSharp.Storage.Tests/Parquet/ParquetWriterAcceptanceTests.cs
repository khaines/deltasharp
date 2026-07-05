using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using Xunit;
using Xunit.Abstractions;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Writer acceptance criteria (STORY-05.1.2 / #181): row groups are packed to the configured row
/// limit (verified against Parquet.Net's own <see cref="ParquetReader.RowGroupCount"/>), multiple input
/// batches concatenate in order across row-group boundaries, a zero-row write produces <b>zero</b> row
/// groups (L2), and per-column min/max/null statistics are present (or explicitly unavailable for the
/// types Parquet.Net does not summarize) for every supported type.
/// </summary>
public sealed class ParquetWriterAcceptanceTests
{
    private readonly SeededRandom _random;

    public ParquetWriterAcceptanceTests(ITestOutputHelper output)
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
        new StructField("string", DataTypes.StringType, nullable: true),
        new StructField("binary", DataTypes.BinaryType, nullable: true),
        new StructField("date", DataTypes.DateType, nullable: false),
        new StructField("ts", DataTypes.TimestampType, nullable: false),
        new StructField("dec_compact", DataTypes.CreateDecimalType(10, 2), nullable: true),
        new StructField("dec_wide", DataTypes.CreateDecimalType(24, 4), nullable: true),
    });

    [Theory]
    [InlineData(500, 128, 4)]
    [InlineData(256, 128, 2)]
    [InlineData(300, 100, 3)]
    [InlineData(1, 128, 1)]
    public async Task RowGroupCount_MatchesConfiguredRowLimit(int rows, int limit, int expectedGroups)
    {
        var schema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch batch = TestData.RandomBatch(schema, rows, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }, rowGroupRowLimit: limit);

        using var stream = new MemoryStream(file, writable: false);
        ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        await using (reader.ConfigureAwait(false))
        {
            Assert.Equal(expectedGroups, reader.RowGroupCount);
        }
    }

    [Fact]
    public async Task MultipleInputBatches_ConcatenateInOrderAcrossRowGroups()
    {
        var schema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch b0 = BuildLongBatch(schema, new long[] { 1, 2, 3 });
        ColumnBatch b1 = BuildLongBatch(schema, new long[] { 4, 5 });
        ColumnBatch b2 = BuildLongBatch(schema, new long[] { 6, 7, 8, 9 });

        // A row-group limit that does NOT align to any batch boundary forces cross-batch packing.
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { b0, b1, b2 }, rowGroupRowLimit: 2);
        List<ColumnBatch> result = await ParquetTestHelpers.ReadAllAsync(file, schema);

        var flat = new List<long>();
        foreach (ColumnBatch batch in result)
        {
            ColumnVector column = batch.SelectedColumn(0);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                flat.Add(column.GetValue<long>(r));
            }
        }

        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, flat);
    }

    [Fact]
    public async Task ZeroRowWrite_ProducesZeroRowGroups()
    {
        ColumnBatch empty = TestData.RandomBatch(AllTypes, rowCount: 0, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(AllTypes, new[] { empty });

        using var stream = new MemoryStream(file, writable: false);
        ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        await using (reader.ConfigureAwait(false))
        {
            Assert.Equal(0, reader.RowGroupCount);
        }

        // Our reader yields no batches for a zero-row file.
        List<ColumnBatch> result = await ParquetTestHelpers.ReadAllAsync(file, AllTypes);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ZeroRowWrite_AcrossMultipleEmptyBatches_ProducesZeroRowGroups()
    {
        var schema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch e0 = BuildLongBatch(schema, Array.Empty<long>());
        ColumnBatch e1 = BuildLongBatch(schema, Array.Empty<long>());
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { e0, e1 }, rowGroupRowLimit: 4);

        using var stream = new MemoryStream(file, writable: false);
        ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        await using (reader.ConfigureAwait(false))
        {
            Assert.Equal(0, reader.RowGroupCount);
        }
    }

    [Fact]
    public async Task PerColumnStatistics_ArePresentOrExplicitlyUnavailable_ForEverySupportedType()
    {
        ColumnBatch batch = TestData.RandomBatch(AllTypes, rowCount: 32, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(AllTypes, new[] { batch });

        using var stream = new MemoryStream(file, writable: false);
        ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        await using (reader.ConfigureAwait(false))
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            foreach (StructField field in AllTypes)
            {
                DataField dataField = Array.Find(reader.Schema.DataFields, f => f.Name == field.Name)!;
                DataColumnStatistics? stats = rowGroup.GetStatistics(dataField);
                Assert.NotNull(stats);

                // NullCount is always summarized.
                Assert.NotNull(stats.NullCount);

                // Boolean and binary are the two supported types Parquet.Net does not min/max-summarize;
                // every other type MUST carry both bounds.
                bool minMaxExpected = field.DataType is not (BooleanType or BinaryType);
                if (minMaxExpected)
                {
                    Assert.NotNull(stats.MinValue);
                    Assert.NotNull(stats.MaxValue);
                }
            }
        }
    }

    private static ColumnBatch BuildLongBatch(StructType schema, long[] values)
    {
        MutableColumnVector vector = ColumnVectors.Create(DataTypes.LongType, Math.Max(values.Length, 1));
        foreach (long value in values)
        {
            vector.AppendValue(value);
        }

        return new ManagedColumnBatch(schema, new ColumnVector[] { vector }, values.Length);
    }
}
