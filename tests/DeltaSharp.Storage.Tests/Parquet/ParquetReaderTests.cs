using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;
using Xunit.Abstractions;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Reader-specific behavior: column projection reads only the requested columns, row-group pruning
/// skips groups whose statistics prove no match, and malformed/unsupported inputs surface a
/// deterministic <see cref="DeltaStorageException"/> (never partial rows).
/// </summary>
public sealed class ParquetReaderTests
{
    private readonly SeededRandom _random;

    public ParquetReaderTests(ITestOutputHelper output)
    {
        _random = SeededRandom.Create(output);
    }

    private static readonly StructType FullSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
        new StructField("score", DataTypes.DoubleType, nullable: true),
    });

    [Fact]
    public async Task Projection_ReadsOnlyRequestedColumns()
    {
        ColumnBatch source = TestData.RandomBatch(FullSchema, rowCount: 64, _random);
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(stream, FullSchema, new[] { source }, CancellationToken.None);
        stream.Position = 0;

        var projection = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(stream, projection, keepRowGroup: null, CancellationToken.None))
        {
            batches.Add(batch);
        }

        ColumnBatch result = Assert.Single(batches);
        Assert.Equal(projection, result.Schema);
        Assert.Equal(1, result.ColumnCount);
        Assert.Equal(source.LogicalRowCount, result.LogicalRowCount);

        // The projected column must still carry the original values in order.
        ColumnVector expectedId = source.SelectedColumn(0);
        ColumnVector actualId = result.SelectedColumn(0);
        for (int r = 0; r < result.LogicalRowCount; r++)
        {
            Assert.Equal(expectedId.GetValue<long>(r), actualId.GetValue<long>(r));
        }
    }

    [Fact]
    public async Task RowGroupPruning_SkipsNonMatchingGroups()
    {
        // Two row groups: ids [1..3] then [100..102]; a predicate that keeps only groups whose max id
        // is >= 100 must drop the first group and return exactly the second group's rows.
        var schema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 100, 101, 102 });

        using var stream = new MemoryStream();
        await new ParquetFileWriter(rowGroupRowLimit: 3).WriteAsync(stream, schema, new[] { batch }, CancellationToken.None);
        stream.Position = 0;

        var kept = new List<long>();
        await foreach (ColumnBatch result in new ParquetFileReader().ReadAsync(
            stream,
            schema,
            keepRowGroup: stats => stats.Max("id") is long max && max >= 100,
            CancellationToken.None))
        {
            ColumnVector column = result.SelectedColumn(0);
            for (int r = 0; r < result.LogicalRowCount; r++)
            {
                kept.Add(column.GetValue<long>(r));
            }
        }

        Assert.Equal(new long[] { 100, 101, 102 }, kept);
    }

    [Fact]
    public async Task TruncatedStream_ThrowsDeterministicCorruptData()
    {
        var schema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 4, 5 });
        using var full = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(full, schema, new[] { batch }, CancellationToken.None);

        // Keep only the first half of the file so the footer/magic is gone.
        byte[] truncated = full.ToArray().AsSpan(0, (int)(full.Length / 2)).ToArray();
        using var stream = new MemoryStream(truncated);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch _ in new ParquetFileReader().ReadAsync(stream, schema, keepRowGroup: null, CancellationToken.None))
            {
            }
        });
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task GarbageStream_ThrowsDeterministicCorruptData()
    {
        var schema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        var garbage = new byte[256];
        _random.NextBytes(garbage);
        using var stream = new MemoryStream(garbage);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch _ in new ParquetFileReader().ReadAsync(stream, schema, keepRowGroup: null, CancellationToken.None))
            {
            }
        });
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task NestedRequestedType_ThrowsUnsupportedFeature()
    {
        // Write a simple valid file, then request a nested (array) column, which the layer defers.
        var writeSchema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch batch = BuildLongBatch(writeSchema, new long[] { 1, 2, 3 });
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(stream, writeSchema, new[] { batch }, CancellationToken.None);
        stream.Position = 0;

        var nested = new StructType(new[]
        {
            new StructField("id", DataTypes.CreateArrayType(DataTypes.LongType), nullable: true),
        });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch _ in new ParquetFileReader().ReadAsync(stream, nested, keepRowGroup: null, CancellationToken.None))
            {
            }
        });
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
    }

    private static ColumnBatch BuildLongBatch(StructType schema, long[] values)
    {
        MutableColumnVector vector = ColumnVectors.Create(DataTypes.LongType, values.Length);
        foreach (long value in values)
        {
            vector.AppendValue(value);
        }

        return new ManagedColumnBatch(schema, new ColumnVector[] { vector }, values.Length);
    }
}
