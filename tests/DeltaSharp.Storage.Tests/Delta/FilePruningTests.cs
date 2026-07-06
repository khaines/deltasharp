using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

public sealed class FilePruningTests
{
    private static readonly ImmutableSortedDictionary<string, string?> NoPartitions =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly ImmutableSortedDictionary<string, string> NoTags =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private static AddFileAction File(
        string path,
        (string Column, string? Value)[]? partitions = null,
        FileStatistics? stats = null)
    {
        ImmutableSortedDictionary<string, string?> pv = NoPartitions;
        if (partitions is not null)
        {
            var builder = ImmutableSortedDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
            foreach ((string column, string? value) in partitions)
            {
                builder[column] = value;
            }

            pv = builder.ToImmutable();
        }

        return new AddFileAction(path, pv, Size: 1, ModificationTime: 0, DataChange: true, stats, NoTags);
    }

    private static FileStatistics IntStats(
        long numRecords, (string Column, long Min, long Max, long Nulls)[] columns, bool? tightBounds = null)
    {
        var min = ImmutableSortedDictionary.CreateBuilder<string, DeltaStatValue>(StringComparer.Ordinal);
        var max = ImmutableSortedDictionary.CreateBuilder<string, DeltaStatValue>(StringComparer.Ordinal);
        var nulls = ImmutableSortedDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
        foreach ((string column, long minValue, long maxValue, long nullCount) in columns)
        {
            min[column] = DeltaStatValue.OfLong(minValue);
            max[column] = DeltaStatValue.OfLong(maxValue);
            nulls[column] = nullCount;
        }

        return new FileStatistics(numRecords, min.ToImmutable(), max.ToImmutable(), nulls.ToImmutable(), tightBounds);
    }

    private static FilePruningResult Prune(ImmutableArray<AddFileAction> files, FilePruningRequest request) =>
        FilePruner.Prune(files, request);

    [Fact]
    public void PartitionFilter_PrunesNonMatchingPartitions()
    {
        ImmutableArray<AddFileAction> files =
        [
            File("y2025.parquet", partitions: [("year", "2025")]),
            File("y2026.parquet", partitions: [("year", "2026")]),
        ];

        FilePruningResult result = Prune(files, FilePruningRequest.ForPartitions(new PartitionEqualityFilter("year", "2026")));

        Assert.Equal(["y2026.parquet"], result.Candidates.Select(a => a.Path));
        Assert.Equal(1, result.PrunedByPartition);
    }

    [Fact]
    public void PartitionFilter_IsNull_MatchesNullValue()
    {
        ImmutableArray<AddFileAction> files =
        [
            File("null.parquet", partitions: [("region", null)]),
            File("us.parquet", partitions: [("region", "us")]),
        ];

        FilePruningResult result = Prune(files, FilePruningRequest.ForPartitions(new PartitionEqualityFilter("region", null)));

        Assert.Equal(["null.parquet"], result.Candidates.Select(a => a.Path));
    }

    [Fact]
    public void PartitionFilter_UnknownColumn_DoesNotPrune()
    {
        ImmutableArray<AddFileAction> files = [File("f.parquet", partitions: [("year", "2025")])];

        FilePruningResult result = Prune(files, FilePruningRequest.ForPartitions(new PartitionEqualityFilter("month", "01")));

        Assert.Single(result.Candidates); // cannot evaluate an unknown partition column → keep (sound)
        Assert.Equal(0, result.PrunedByPartition);
    }

    [Fact]
    public void DataFilter_Equality_SkipsDisjointRanges()
    {
        ImmutableArray<AddFileAction> files =
        [
            File("low.parquet", stats: IntStats(10, [("id", 1, 10, 0)])),
            File("high.parquet", stats: IntStats(10, [("id", 20, 30, 0)])),
        ];

        FilePruningResult result = Prune(files,
            FilePruningRequest.ForData(new ColumnRangeFilter("id", DeltaPredicateOp.Equal, DeltaStatValue.OfLong(5))));

        Assert.Equal(["low.parquet"], result.Candidates.Select(a => a.Path));
        Assert.Equal(1, result.PrunedByStatistics);
    }

    [Fact]
    public void DataFilter_GreaterThan_SkipsRangesBelow()
    {
        ImmutableArray<AddFileAction> files =
        [
            File("low.parquet", stats: IntStats(10, [("id", 1, 10, 0)])),
            File("high.parquet", stats: IntStats(10, [("id", 20, 30, 0)])),
        ];

        FilePruningResult result = Prune(files,
            FilePruningRequest.ForData(new ColumnRangeFilter("id", DeltaPredicateOp.GreaterThan, DeltaStatValue.OfLong(15))));

        Assert.Equal(["high.parquet"], result.Candidates.Select(a => a.Path));
    }

    [Fact]
    public void DataFilter_AllNullColumn_IsPruned()
    {
        ImmutableArray<AddFileAction> files = [File("f.parquet", stats: IntStats(5, [("id", 0, 0, 5)]))];

        FilePruningResult result = Prune(files,
            FilePruningRequest.ForData(new ColumnRangeFilter("id", DeltaPredicateOp.Equal, DeltaStatValue.OfLong(0))));

        Assert.Empty(result.Candidates); // every value is null → cannot satisfy id = 0
        Assert.Equal(1, result.PrunedByStatistics);
    }

    [Fact]
    public void DataFilter_NonTightBounds_AreNotUsed()
    {
        ImmutableArray<AddFileAction> files =
            [File("f.parquet", stats: IntStats(10, [("id", 1, 10, 0)], tightBounds: false))];

        FilePruningResult result = Prune(files,
            FilePruningRequest.ForData(new ColumnRangeFilter("id", DeltaPredicateOp.Equal, DeltaStatValue.OfLong(999))));

        Assert.Single(result.Candidates); // bounds are not tight → cannot prune (sound)
    }

    [Fact]
    public void DataFilter_MissingStats_DoesNotPrune()
    {
        ImmutableArray<AddFileAction> files = [File("f.parquet", stats: null)];

        FilePruningResult result = Prune(files,
            FilePruningRequest.ForData(new ColumnRangeFilter("id", DeltaPredicateOp.Equal, DeltaStatValue.OfLong(5))));

        Assert.Single(result.Candidates);
    }

    [Fact]
    public void DataFilter_StringStats_AreNotUsedForRangeSkipping()
    {
        var min = ImmutableSortedDictionary.CreateBuilder<string, DeltaStatValue>(StringComparer.Ordinal);
        var max = ImmutableSortedDictionary.CreateBuilder<string, DeltaStatValue>(StringComparer.Ordinal);
        min["name"] = DeltaStatValue.OfString("a");
        max["name"] = DeltaStatValue.OfString("m");
        var stats = new FileStatistics(
            10, min.ToImmutable(), max.ToImmutable(),
            ImmutableSortedDictionary<string, long>.Empty.WithComparers(StringComparer.Ordinal), TightBounds: true);
        ImmutableArray<AddFileAction> files = [File("f.parquet", stats: stats)];

        // "name = z" is outside [a, m] ordinally, but Delta may truncate string stats, so we must NOT prune.
        FilePruningResult result = Prune(files,
            FilePruningRequest.ForData(new ColumnRangeFilter("name", DeltaPredicateOp.Equal, DeltaStatValue.OfString("z"))));

        Assert.Single(result.Candidates);
    }

    [Fact]
    public void Pruning_IsSound_UnderRandomizedData()
    {
        var random = new Random(20260706);
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            // Build a file with a known multiset of nullable integer rows and matching statistics.
            int rowCount = random.Next(1, 8);
            var rows = new long?[rowCount];
            for (int r = 0; r < rowCount; r++)
            {
                rows[r] = random.Next(0, 4) == 0 ? null : random.Next(-20, 20);
            }

            long[] present = rows.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            long nulls = rowCount - present.Length;
            FileStatistics? stats = present.Length == 0
                ? new FileStatistics(rowCount,
                    ImmutableSortedDictionary<string, DeltaStatValue>.Empty.WithComparers(StringComparer.Ordinal),
                    ImmutableSortedDictionary<string, DeltaStatValue>.Empty.WithComparers(StringComparer.Ordinal),
                    Singleton("v", nulls), TightBounds: true)
                : new FileStatistics(rowCount,
                    Singleton("v", DeltaStatValue.OfLong(present.Min())),
                    Singleton("v", DeltaStatValue.OfLong(present.Max())),
                    Singleton("v", nulls), TightBounds: true);

            AddFileAction file = File("f.parquet", stats: stats);

            DeltaPredicateOp op = (DeltaPredicateOp)random.Next(0, 5);
            long value = random.Next(-22, 22);
            FilePruningResult result = Prune([file],
                FilePruningRequest.ForData(new ColumnRangeFilter("v", op, DeltaStatValue.OfLong(value))));

            bool anyRowMatches = present.Any(v => Satisfies(op, v, value));
            bool pruned = result.Candidates.IsEmpty;

            // Soundness: a pruned file must genuinely contain no matching row.
            if (pruned)
            {
                Assert.False(anyRowMatches,
                    $"Unsound prune: op={op} value={value} rows=[{string.Join(",", present)}] nulls={nulls}");
            }
        }
    }

    private static bool Satisfies(DeltaPredicateOp op, long rowValue, long predicateValue) => op switch
    {
        DeltaPredicateOp.Equal => rowValue == predicateValue,
        DeltaPredicateOp.LessThan => rowValue < predicateValue,
        DeltaPredicateOp.LessThanOrEqual => rowValue <= predicateValue,
        DeltaPredicateOp.GreaterThan => rowValue > predicateValue,
        DeltaPredicateOp.GreaterThanOrEqual => rowValue >= predicateValue,
        _ => false,
    };

    private static ImmutableSortedDictionary<string, T> Singleton<T>(string key, T value) =>
        ImmutableSortedDictionary<string, T>.Empty.WithComparers(StringComparer.Ordinal).Add(key, value);
}
