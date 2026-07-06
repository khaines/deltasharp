using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Row-group statistics are exposed in the <b>engine lane space</b> (M3) — <c>int</c> epoch-day for
/// DATE, <c>long</c> epoch-micros for TIMESTAMP, unscaled <c>Int128</c> for DECIMAL — so a lane-space
/// pruning predicate compares like-for-like and can prune correctly, while a <c>NaN</c>-poisoned
/// float/double bound is surfaced as "cannot prune" (null) so no matching row is ever wrongly skipped
/// (M6). TIMESTAMP bounds are ±1&#160;ms-widened because Parquet stats are millisecond-truncated.
/// </summary>
public sealed class ParquetStatisticsPruningTests
{
    private static ColumnBatch BuildLane(StructType schema, DataType type, Array laneValues)
    {
        MutableColumnVector vector = ColumnVectors.Create(type, laneValues.Length);
        foreach (object value in laneValues)
        {
            switch (type)
            {
                case DateType:
                    vector.AppendValue((int)value);
                    break;
                case TimestampType:
                case LongType:
                    vector.AppendValue((long)value);
                    break;
                case DecimalType { IsCompact: true }:
                    vector.AppendValue((long)value);
                    break;
                case DoubleType:
                    vector.AppendValue((double)value);
                    break;
                default:
                    throw new NotSupportedException(type.SimpleString);
            }
        }

        return new ManagedColumnBatch(schema, new ColumnVector[] { vector }, laneValues.Length);
    }

    private static List<long> ReadLongs(IReadOnlyList<ColumnBatch> batches, Func<ColumnVector, int, long> read)
    {
        var values = new List<long>();
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector column = batch.SelectedColumn(0);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                values.Add(read(column, r));
            }
        }

        return values;
    }

    [Fact]
    public async Task DatePredicate_PrunesInLaneEpochDaySpace()
    {
        var schema = new StructType(new[] { new StructField("d", DataTypes.DateType, nullable: false) });
        ColumnBatch batch = BuildLane(schema, DataTypes.DateType, new[] { 0, 5, 1000, 1005 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }, rowGroupRowLimit: 2);

        List<ColumnBatch> result = await ParquetTestHelpers.ReadAllAsync(
            file, schema, keepRowGroup: stats => stats.Max("d") is int max && max >= 500);

        List<long> kept = ReadLongs(result, static (c, r) => c.GetValue<int>(r));
        Assert.Equal(new long[] { 1000, 1005 }, kept);
    }

    [Fact]
    public async Task TimestampPredicate_PrunesInLaneMicrosSpaceWithWidening()
    {
        var schema = new StructType(new[] { new StructField("ts", DataTypes.TimestampType, nullable: false) });
        ColumnBatch batch = BuildLane(
            schema, DataTypes.TimestampType, new[] { 0L, 1000L, 10_000_000L, 10_001_000L });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }, rowGroupRowLimit: 2);

        List<ColumnBatch> result = await ParquetTestHelpers.ReadAllAsync(
            file, schema, keepRowGroup: stats => stats.Max("ts") is long max && max >= 5_000_000);

        List<long> kept = ReadLongs(result, static (c, r) => c.GetValue<long>(r));
        Assert.Equal(new long[] { 10_000_000L, 10_001_000L }, kept);
    }

    [Fact]
    public async Task DecimalPredicate_PrunesInUnscaledInt128LaneSpace()
    {
        DataType dec = DataTypes.CreateDecimalType(10, 2);
        var schema = new StructType(new[] { new StructField("v", dec, nullable: false) });
        ColumnBatch batch = BuildLane(schema, dec, new[] { 100L, 200L, 900_000L, 900_100L });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }, rowGroupRowLimit: 2);

        List<ColumnBatch> result = await ParquetTestHelpers.ReadAllAsync(
            file, schema, keepRowGroup: stats => stats.Max("v") is Int128 max && max >= (Int128)500_000);

        List<long> kept = ReadLongs(result, static (c, r) => c.GetValue<long>(r));
        Assert.Equal(new long[] { 900_000L, 900_100L }, kept);
    }

    [Fact]
    public async Task LaneStatistics_AreTypedAndBoundTheGroup()
    {
        var schema = new StructType(new[] { new StructField("ts", DataTypes.TimestampType, nullable: false) });
        ColumnBatch batch = BuildLane(schema, DataTypes.TimestampType, new[] { 100L, 1_234L });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });

        object? min = null;
        object? max = null;
        object? rawMin = null;
        object? rawMax = null;
        _ = await ParquetTestHelpers.ReadAllAsync(file, schema, keepRowGroup: stats =>
        {
            min = stats.Min("ts");
            max = stats.Max("ts");
            rawMin = stats.RawMin("ts");
            rawMax = stats.RawMax("ts");
            return true;
        });

        // Lane bounds are longs (epoch-micros) that truly bound the group's [100, 1234] micros — the
        // ±1 ms widening keeps them conservative, never tighter than the real values.
        long laneMin = Assert.IsType<long>(min);
        long laneMax = Assert.IsType<long>(max);
        Assert.True(laneMin <= 100, $"lane min {laneMin} must be <= 100");
        Assert.True(laneMax >= 1_234, $"lane max {laneMax} must be >= 1234");

        // Raw (Parquet-space) values remain available and are the millisecond-truncated statistics.
        Assert.NotNull(rawMin);
        Assert.NotNull(rawMax);
    }

    [Fact]
    public async Task NaNPoisonedStatistics_ReturnNullSoPruningIsSafe()
    {
        var schema = new StructType(new[] { new StructField("d", DataTypes.DoubleType, nullable: false) });
        // A NaN in the group poisons Parquet's min/max to NaN.
        ColumnBatch batch = BuildLane(schema, DataTypes.DoubleType, new[] { double.NaN, 5.0, -3.0 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });

        object? min = "unset";
        object? max = "unset";
        List<ColumnBatch> result = await ParquetTestHelpers.ReadAllAsync(file, schema, keepRowGroup: stats =>
        {
            min = stats.Min("d");
            max = stats.Max("d");

            // A safe predicate: only prune when a real (non-null) max proves no match. A NaN-poisoned
            // (null) bound must NEVER prune.
            return max is not double m || m >= 5.0;
        });

        // We must never hand a NaN bound to the predicate.
        Assert.Null(min);
        Assert.Null(max);

        // The group (which really contains 5.0) is kept, not wrongly skipped.
        ColumnBatch only = Assert.Single(result);
        Assert.Equal(3, only.LogicalRowCount);
    }
}
