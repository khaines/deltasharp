using System.Collections.Immutable;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// End-to-end write-time statistics tests (STORY-05.6.3 AC1): a Parquet file written through
/// <see cref="ParquetFileWriter.WriteWithStatisticsAsync"/> yields a <see cref="FileStatistics"/> whose
/// record count and per-scalar-column <c>min</c>/<c>max</c>/<c>nullCount</c> are recorded on the committed
/// <c>add</c> action alongside its byte size and partition values, and the <c>add.stats</c> survives a
/// serialize→parse round-trip through the log codec. Mirrors <see cref="DeltaTableWriterTests"/>.
/// </summary>
public sealed class WriteTimeStatisticsTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public WriteTimeStatisticsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "write-stats-tests-" + Guid.NewGuid().ToString("N"));
        _backend = new LocalFileSystemBackend(_root);
    }

    public void Dispose()
    {
        _backend.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static readonly StructType Schema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("score", DataTypes.DoubleType, nullable: true),
        new StructField("label", DataTypes.StringType, nullable: true),
    });

    private static readonly ImmutableSortedDictionary<string, string?> NoPartition =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static ColumnBatch SampleBatch()
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 4);
        MutableColumnVector score = ColumnVectors.Create(DataTypes.DoubleType, 4);
        MutableColumnVector label = ColumnVectors.Create(DataTypes.StringType, 4);

        var ids = new long[] { 10, 20, 30, 40 };
        var scores = new double?[] { 1.5, null, -3.5, 2.25 };
        var labels = new string?[] { "b", "a", null, "c" };
        for (int i = 0; i < ids.Length; i++)
        {
            id.AppendValue(ids[i]);
            if (scores[i] is double s)
            {
                score.AppendValue(s);
            }
            else
            {
                score.AppendNull();
            }

            if (labels[i] is string text)
            {
                label.AppendBytes(System.Text.Encoding.UTF8.GetBytes(text));
            }
            else
            {
                label.AppendNull();
            }
        }

        return new ManagedColumnBatch(Schema, new ColumnVector[] { id, score, label }, ids.Length);
    }

    private static ImmutableSortedDictionary<string, string?> Partition(params (string Key, string? Value)[] values)
    {
        ImmutableSortedDictionary<string, string?>.Builder builder =
            ImmutableSortedDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        foreach ((string key, string? value) in values)
        {
            builder[key] = value;
        }

        return builder.ToImmutable();
    }

    [Fact]
    public async Task WriteWithStatisticsAsync_ReturnsRecordCountSizeAndBounds()
    {
        using var stream = new MemoryStream();
        ParquetFileWriter.WriteResult result = await new ParquetFileWriter().WriteWithStatisticsAsync(
            stream, Schema, new[] { SampleBatch() }, StatisticsPolicy.Default, CancellationToken.None);

        Assert.Equal(4L, result.RowCount);
        Assert.True(result.ByteSize > 0, "byte size must be measured from the written stream");
        Assert.Equal(stream.Length, result.ByteSize);

        FileStatistics stats = result.Statistics;
        Assert.Equal(4L, stats.NumRecords);
        Assert.Equal(DeltaStatValue.OfLong(10L), stats.MinValues["id"]);
        Assert.Equal(DeltaStatValue.OfLong(40L), stats.MaxValues["id"]);
        Assert.Equal(0L, stats.NullCount["id"]);
        Assert.Equal(DeltaStatValue.OfDouble(-3.5d), stats.MinValues["score"]);
        Assert.Equal(DeltaStatValue.OfDouble(2.25d), stats.MaxValues["score"]);
        Assert.Equal(1L, stats.NullCount["score"]);
        Assert.Equal(DeltaStatValue.OfString("a"), stats.MinValues["label"]);
        Assert.Equal(DeltaStatValue.OfString("c"), stats.MaxValues["label"]);
        Assert.Equal(1L, stats.NullCount["label"]);
        Assert.True(stats.TightBounds!.Value);
    }

    [Fact]
    public async Task CommittedAdd_CarriesWriteTimeStatistics_PartitionValues_AndSize()
    {
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.Metadata(partitionColumns: new[] { "region" }));

        using var stream = new MemoryStream();
        ParquetFileWriter.WriteResult result = await new ParquetFileWriter().WriteWithStatisticsAsync(
            stream, Schema, new[] { SampleBatch() }, StatisticsPolicy.Default, CancellationToken.None);

        ImmutableSortedDictionary<string, string?> partition = Partition(("region", "eu"));
        var staged = new StagedDataFile(
            "region=eu/part-0.parquet", partition, result.ByteSize, 1L, result.Statistics);

        Snapshot writeSnapshot = await new DeltaLog(_backend).LoadSnapshotAsync();
        DeltaCommitResult commit = await new DeltaTableWriter(_backend)
            .AppendAsync(writeSnapshot, writeSnapshot.Schema, new[] { staged });
        Assert.Equal(1L, commit.Version);

        Snapshot reloaded = await new DeltaLog(_backend).LoadSnapshotAsync();
        AddFileAction add = Assert.Single(reloaded.ActiveFiles);

        // AC1: min, max, null count, record count, partition values and byte size are all recorded.
        Assert.Equal(result.ByteSize, add.Size);
        Assert.Equal("eu", add.PartitionValues["region"]);
        Assert.NotNull(add.Stats);
        Assert.Equal(4L, add.Stats!.NumRecords);
        Assert.Equal(DeltaStatValue.OfLong(10L), add.Stats.MinValues["id"]);
        Assert.Equal(DeltaStatValue.OfLong(40L), add.Stats.MaxValues["id"]);
        Assert.Equal(1L, add.Stats.NullCount["score"]);
        Assert.Equal(DeltaStatValue.OfString("a"), add.Stats.MinValues["label"]);
    }

    [Fact]
    public async Task Snapshot_GetStatistics_ExposesVersionTokenAndAggregatesAfterWrite()
    {
        // AC4: after a write commits, the optimizer stats API reports the snapshot version as its
        // freshness token together with the table aggregates derived from the committed add.stats.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(minReader: 1, minWriter: 2), DeltaTestHarness.Metadata());

        using var stream = new MemoryStream();
        ParquetFileWriter.WriteResult result = await new ParquetFileWriter().WriteWithStatisticsAsync(
            stream, Schema, new[] { SampleBatch() }, StatisticsPolicy.Default, CancellationToken.None);
        var staged = new StagedDataFile("part-0.parquet", NoPartition, result.ByteSize, 1L, result.Statistics);

        Snapshot writeSnapshot = await new DeltaLog(_backend).LoadSnapshotAsync();
        DeltaCommitResult commit = await new DeltaTableWriter(_backend)
            .AppendAsync(writeSnapshot, writeSnapshot.Schema, new[] { staged });
        Snapshot reloaded = await new DeltaLog(_backend).LoadSnapshotAsync();

        TableStatisticsReport report = reloaded.GetStatistics();

        Assert.Equal(commit.Version, report.Version);
        Assert.Equal(commit.Version, report.FreshnessToken);
        Assert.Equal(1, report.FileCount);
        Assert.Equal(result.ByteSize, report.TotalSizeInBytes);
        Assert.True(report.HasCompleteRecordCounts);
        Assert.Equal(4L, report.RecordCount);
    }

    [Fact]
    public void FileStatistics_RoundTripThroughLogCodec()
    {
        // The write-time statistics serialize and parse back identically through the add.stats codec.
        FileStatistics stats = ParquetStatisticsCollector.Collect(
            Schema, new[] { SampleBatch() }, StatisticsPolicy.Default);

        string json = DeltaLogActionWriter.SerializeStats(stats);
        FileStatistics? parsed = DeltaLogActionReader.ParseStatsString(json);

        Assert.NotNull(parsed);
        Assert.Equal(stats.NumRecords, parsed!.NumRecords);
        Assert.Equal(stats.TightBounds, parsed.TightBounds);
        Assert.Equal(stats.MinValues["id"], parsed.MinValues["id"]);
        Assert.Equal(stats.MaxValues["id"], parsed.MaxValues["id"]);
        Assert.Equal(stats.MinValues["score"], parsed.MinValues["score"]);
        Assert.Equal(stats.MinValues["label"], parsed.MinValues["label"]);
        Assert.Equal(stats.NullCount["score"], parsed.NullCount["score"]);
    }
}
