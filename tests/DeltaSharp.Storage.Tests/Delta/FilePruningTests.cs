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

    private static readonly DeltaPredicateOp[] AllOps =
    {
        DeltaPredicateOp.Equal,
        DeltaPredicateOp.LessThan,
        DeltaPredicateOp.LessThanOrEqual,
        DeltaPredicateOp.GreaterThan,
        DeltaPredicateOp.GreaterThanOrEqual,
    };

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
    public void EmptyRequest_KeepsAllFiles()
    {
        ImmutableArray<AddFileAction> files =
        [
            File("a.parquet", partitions: [("year", "2025")], stats: IntStats(5, [("id", 1, 10, 0)])),
            File("b.parquet", partitions: [("year", "2026")]),
        ];

        FilePruningResult result = Prune(files, FilePruningRequest.Empty);

        Assert.Equal(2, result.Candidates.Length);
        Assert.Equal(0, result.PrunedByPartition);
        Assert.Equal(0, result.PrunedByStatistics);
        Assert.Equal(2, result.TotalFiles);
    }

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

    // ---------------------------------------------------------------- AC3: skipping is an optimization

    [Fact]
    public void Skipped_ForPartitionMismatch_CarriesReason_AndIsNotACandidate()
    {
        // AC3: a skip is an audit entry with a reason; the file is excluded from — never hidden inside —
        // the candidate set.
        ImmutableArray<AddFileAction> files =
        [
            File("y2025.parquet", partitions: [("year", "2025")]),
            File("y2026.parquet", partitions: [("year", "2026")]),
        ];

        FilePruningResult result =
            Prune(files, FilePruningRequest.ForPartitions(new PartitionEqualityFilter("year", "2026")));

        SkippedFile skipped = Assert.Single(result.Skipped);
        Assert.Equal("y2025.parquet", skipped.File.Path);
        Assert.Equal(FileSkipReason.PartitionMismatch, skipped.Reason);
        Assert.DoesNotContain(result.Candidates, c => c.Path == "y2025.parquet");
    }

    [Fact]
    public void Skipped_ForStatisticsDisjoint_CarriesReason()
    {
        ImmutableArray<AddFileAction> files =
        [
            File("low.parquet", stats: IntStats(10, [("id", 1, 10, 0)])),
            File("high.parquet", stats: IntStats(10, [("id", 20, 30, 0)])),
        ];

        FilePruningResult result = Prune(files,
            FilePruningRequest.ForData(new ColumnRangeFilter("id", DeltaPredicateOp.Equal, DeltaStatValue.OfLong(5))));

        SkippedFile skipped = Assert.Single(result.Skipped);
        Assert.Equal("high.parquet", skipped.File.Path);
        Assert.Equal(FileSkipReason.StatisticsDisjoint, skipped.Reason);
    }

    [Fact]
    public void AbsentStats_AreAlwaysKept_NeverSkipped_ForEveryOp()
    {
        // AC3 fail-open: with no statistics there is nothing to prove non-matching, so the file must be a
        // candidate (scanned) for every predicate — skipping never becomes a correctness dependency.
        ImmutableArray<AddFileAction> files = [File("f.parquet", stats: null)];

        foreach (DeltaPredicateOp op in AllOps)
        {
            foreach (long value in new long[] { -1000, 0, 1000 })
            {
                FilePruningResult result =
                    Prune(files, FilePruningRequest.ForData(new ColumnRangeFilter("id", op, DeltaStatValue.OfLong(value))));

                Assert.Single(result.Candidates);
                Assert.Empty(result.Skipped);
            }
        }
    }

    [Fact]
    public void NonTightBounds_AreAlwaysKept_NeverSkipped_ForEveryOp()
    {
        // AC3 fail-open: tightBounds == false marks min/max as inexact, so they cannot prove non-matching
        // even for a value far outside [1, 10]; the file is always kept.
        ImmutableArray<AddFileAction> files =
            [File("f.parquet", stats: IntStats(10, [("id", 1, 10, 0)], tightBounds: false))];

        foreach (DeltaPredicateOp op in AllOps)
        {
            foreach (long value in new long[] { -1000, 5, 1000 })
            {
                FilePruningResult result =
                    Prune(files, FilePruningRequest.ForData(new ColumnRangeFilter("id", op, DeltaStatValue.OfLong(value))));

                Assert.Single(result.Candidates);
                Assert.Empty(result.Skipped);
            }
        }
    }

    [Fact]
    public void CandidatesAndSkipped_ExactlyPartitionTheInput()
    {
        // AC3: every file is accounted for as either a scan candidate or a reported skip — nothing is
        // silently dropped, and the two sets are disjoint.
        ImmutableArray<AddFileAction> files =
        [
            File("keep-stats.parquet", stats: IntStats(10, [("id", 1, 10, 0)])),
            File("skip-stats.parquet", stats: IntStats(10, [("id", 20, 30, 0)])),
            File("keep-nostats.parquet", stats: null),
            File("skip-partition.parquet", partitions: [("year", "1999")], stats: IntStats(10, [("id", 1, 10, 0)])),
        ];

        FilePruningResult result = Prune(files, new FilePruningRequest(
            [new PartitionEqualityFilter("year", "2025")],
            [new ColumnRangeFilter("id", DeltaPredicateOp.Equal, DeltaStatValue.OfLong(5))]));

        Assert.Equal(files.Length, result.TotalFiles);
        Assert.Equal(files.Length, result.Candidates.Length + result.Skipped.Length);

        var candidatePaths = result.Candidates.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var skippedPaths = result.Skipped.Select(s => s.File.Path).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(candidatePaths.Intersect(skippedPaths, StringComparer.Ordinal));
    }

    [Fact]
    public void SkipReport_IsSound_UnderRandomizedData()
    {
        // AC3: every reported skip must be provably justified — a StatisticsDisjoint skip genuinely has no
        // matching row, and Candidates ∪ Skipped exactly partitions the inputs (nothing dropped).
        var random = new Random(20260930);
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            int fileCount = random.Next(1, 5);
            var files = ImmutableArray.CreateBuilder<AddFileAction>(fileCount);
            var presentByPath = new Dictionary<string, long[]>(StringComparer.Ordinal);
            for (int f = 0; f < fileCount; f++)
            {
                string path = $"f{f}.parquet";
                int rowCount = random.Next(1, 6);
                var rows = new long?[rowCount];
                for (int r = 0; r < rowCount; r++)
                {
                    rows[r] = random.Next(0, 4) == 0 ? null : random.Next(-15, 15);
                }

                long[] present = rows.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                presentByPath[path] = present;
                long nulls = rowCount - present.Length;
                FileStatistics stats = present.Length == 0
                    ? new FileStatistics(rowCount,
                        ImmutableSortedDictionary<string, DeltaStatValue>.Empty.WithComparers(StringComparer.Ordinal),
                        ImmutableSortedDictionary<string, DeltaStatValue>.Empty.WithComparers(StringComparer.Ordinal),
                        Singleton("v", nulls), TightBounds: true)
                    : new FileStatistics(rowCount,
                        Singleton("v", DeltaStatValue.OfLong(present.Min())),
                        Singleton("v", DeltaStatValue.OfLong(present.Max())),
                        Singleton("v", nulls), TightBounds: true);
                files.Add(File(path, stats: stats));
            }

            DeltaPredicateOp op = (DeltaPredicateOp)random.Next(0, 5);
            long value = random.Next(-17, 17);
            FilePruningResult result = Prune(files.ToImmutable(),
                FilePruningRequest.ForData(new ColumnRangeFilter("v", op, DeltaStatValue.OfLong(value))));

            // Nothing is dropped: the candidate and skip sets exactly partition the inputs.
            Assert.Equal(fileCount, result.Candidates.Length + result.Skipped.Length);

            foreach (SkippedFile skipped in result.Skipped)
            {
                Assert.Equal(FileSkipReason.StatisticsDisjoint, skipped.Reason);
                long[] present = presentByPath[skipped.File.Path];
                Assert.False(present.Any(v => Satisfies(op, v, value)),
                    $"Unsound skip: op={op} value={value} rows=[{string.Join(",", present)}]");
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
