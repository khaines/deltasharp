using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
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

    private static readonly ImmutableSortedDictionary<string, DeltaStatValue> EmptyStatValues =
        ImmutableSortedDictionary<string, DeltaStatValue>.Empty.WithComparers(StringComparer.Ordinal);

    // A single-column FileStatistics where EITHER numeric bound may be omitted — exactly what
    // ParquetStatisticsCollector emits when a column's true max is unencodable (a NaN/+Infinity value ->
    // finite min only) or its true min is unencodable (a -Infinity value -> finite max only). Tight by
    // default so the surviving bound is usable for range pruning.
    private static FileStatistics PartialDoubleStats(
        string column, long numRecords, double? min, double? max, long nulls = 0, bool? tightBounds = true)
    {
        ImmutableSortedDictionary<string, DeltaStatValue> minValues = min is double lo
            ? Singleton(column, DeltaStatValue.OfDouble(lo))
            : EmptyStatValues;
        ImmutableSortedDictionary<string, DeltaStatValue> maxValues = max is double hi
            ? Singleton(column, DeltaStatValue.OfDouble(hi))
            : EmptyStatValues;
        return new FileStatistics(numRecords, minValues, maxValues, Singleton(column, nulls), tightBounds);
    }

    private static void AssertSkipped(FileStatistics stats, DeltaPredicateOp op, double value)
    {
        FilePruningResult result = Prune(
            [File("f.parquet", stats: stats)],
            FilePruningRequest.ForData(new ColumnRangeFilter("v", op, DeltaStatValue.OfDouble(value))));
        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.PrunedByStatistics);
    }

    private static void AssertKept(FileStatistics stats, DeltaPredicateOp op, double value)
    {
        FilePruningResult result = Prune(
            [File("f.parquet", stats: stats)],
            FilePruningRequest.ForData(new ColumnRangeFilter("v", op, DeltaStatValue.OfDouble(value))));
        Assert.Single(result.Candidates);
        Assert.Empty(result.Skipped);
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
    public void PartitionFilter_IsNull_OnIntegerColumn_KeepsEmptyStringAndSentinelFiles_Issue916()
    {
        // #916: on a typed column, a committed "" reads as null (PROTOCOL.md), and so does the Hive sentinel.
        // The pruner must apply the read's null rules, or IS NULL would prune files a full scan returns as null.
        ImmutableArray<AddFileAction> files =
        [
            File("empty.parquet", partitions: [("n", "")]),
            File("five.parquet", partitions: [("n", "5")]),
            File("null.parquet", partitions: [("n", null)]),
            File("sentinel.parquet", partitions: [("n", "__HIVE_DEFAULT_PARTITION__")]),
        ];
        var types = new Dictionary<string, DataType>(StringComparer.Ordinal) { ["n"] = IntegerType.Instance };

        FilePruningResult result = FilePruner.Prune(
            files, FilePruningRequest.ForPartitions(new PartitionEqualityFilter("n", null)), types);

        Assert.Equal(["empty.parquet", "null.parquet", "sentinel.parquet"], result.Candidates.Select(a => a.Path));
        Assert.Equal(1, result.PrunedByPartition);
    }

    [Fact]
    public void PartitionFilter_Equality_OnIntegerColumn_PrunesEmptyStringFile_Issue916()
    {
        // The other direction: a "" file reads as null, so it cannot match n = 5 and is pruned (soundly).
        ImmutableArray<AddFileAction> files =
        [
            File("empty.parquet", partitions: [("n", "")]),
            File("five.parquet", partitions: [("n", "5")]),
        ];
        var types = new Dictionary<string, DataType>(StringComparer.Ordinal) { ["n"] = IntegerType.Instance };

        FilePruningResult result = FilePruner.Prune(
            files, FilePruningRequest.ForPartitions(new PartitionEqualityFilter("n", "5")), types);

        Assert.Equal(["five.parquet"], result.Candidates.Select(a => a.Path));
    }

    [Fact]
    public void PartitionFilter_IsNull_OnStringColumn_PrunesEmptyStringFile_D5()
    {
        // Design D5: on a STRING column "" is a real value, not null, so IS NULL proves the "" file cannot match.
        // The sentinel is still null on a string column (the read never materializes the literal).
        ImmutableArray<AddFileAction> files =
        [
            File("empty.parquet", partitions: [("s", "")]),
            File("null.parquet", partitions: [("s", null)]),
            File("sentinel.parquet", partitions: [("s", "__HIVE_DEFAULT_PARTITION__")]),
        ];
        var types = new Dictionary<string, DataType>(StringComparer.Ordinal) { ["s"] = StringType.Instance };

        FilePruningResult result = FilePruner.Prune(
            files, FilePruningRequest.ForPartitions(new PartitionEqualityFilter("s", null)), types);

        Assert.Equal(["null.parquet", "sentinel.parquet"], result.Candidates.Select(a => a.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("__HIVE_DEFAULT_PARTITION__")]
    public void PartitionFilter_NullDenotingValue_OnIntegerColumn_FiltersAsIsNull_Issue916(string filterValue)
    {
        // The filter's own value goes through the read's null rules: on an integer column, a filter value of ""
        // or the sentinel denotes null, so it keeps every file that reads as null and prunes "5".
        ImmutableArray<AddFileAction> files =
        [
            File("empty.parquet", partitions: [("n", "")]),
            File("five.parquet", partitions: [("n", "5")]),
            File("null.parquet", partitions: [("n", null)]),
        ];
        var types = new Dictionary<string, DataType>(StringComparer.Ordinal) { ["n"] = IntegerType.Instance };

        FilePruningResult result = FilePruner.Prune(
            files, FilePruningRequest.ForPartitions(new PartitionEqualityFilter("n", filterValue)), types);

        Assert.Equal(["empty.parquet", "null.parquet"], result.Candidates.Select(a => a.Path));
    }

    [Fact]
    public void PartitionFilter_IsNull_WithUnknownType_KeepsEmptyStringFile_Issue916()
    {
        // With no type for the column, "" may be a typed null, so IS NULL never proves it non-matching; a
        // non-empty value still prunes. The sentinel is null for every type.
        ImmutableArray<AddFileAction> files =
        [
            File("empty.parquet", partitions: [("c", "")]),
            File("x.parquet", partitions: [("c", "x")]),
            File("sentinel.parquet", partitions: [("c", "__HIVE_DEFAULT_PARTITION__")]),
        ];

        FilePruningResult isNull =
            Prune(files, FilePruningRequest.ForPartitions(new PartitionEqualityFilter("c", null)));
        Assert.Equal(["empty.parquet", "sentinel.parquet"], isNull.Candidates.Select(a => a.Path));

        // ...and an "" filter keeps both the "" file and the null-denoting sentinel file, but prunes "x".
        FilePruningResult isEmpty =
            Prune(files, FilePruningRequest.ForPartitions(new PartitionEqualityFilter("c", "")));
        Assert.Equal(["empty.parquet", "sentinel.parquet"], isEmpty.Candidates.Select(a => a.Path));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("id")]
    [InlineData("none")]
    public void SnapshotPruneFiles_TypesPartitionFiltersByTheAddKey_InEveryMappingMode_Issue916(string mode)
    {
        // End to end through Snapshot.PruneFiles. The type map is keyed by the name add.partitionValues uses: the
        // PHYSICAL name in name and id mode, the logical name in none mode. The string column pins that the map is
        // actually used: without its type, "" against IS NULL can never be proven a mismatch, so f2 would be kept.
        const string schema =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-a\"}},"
            + "{\"name\":\"n\",\"type\":\"integer\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"col-b\"}},"
            + "{\"name\":\"s\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":3,\"delta.columnMapping.physicalName\":\"col-c\"}}]}";
        ImmutableSortedDictionary<string, string> configuration = mode == "none"
            ? ImmutableSortedDictionary<string, string>.Empty
            : ImmutableSortedDictionary<string, string>.Empty
                .Add("delta.columnMapping.mode", mode)
                .Add("delta.columnMapping.maxColumnId", "3");
        (string intKey, string stringKey) = mode == "none" ? ("n", "s") : ("col-b", "col-c");
        var metadata = new MetadataAction(
            Id: "issue-916",
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
            SchemaString: schema,
            PartitionColumns: ["n", "s"],
            Configuration: configuration,
            CreatedTime: null);
        var snapshot = new Snapshot(
            version: 0,
            new ProtocolAction(2, 5, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty),
            metadata,
            [
                File("f1.parquet", partitions: [(intKey, ""), (stringKey, "x")]),
                File("f2.parquet", partitions: [(intKey, "5"), (stringKey, "")]),
            ],
            ImmutableArray<RemoveFileAction>.Empty,
            ImmutableSortedDictionary<string, long>.Empty,
            SnapshotLoadMetrics.Empty);

        // Integer column: f1's "" reads as null, so it matches IS NULL; f2's "5" does not.
        FilePruningResult intIsNull =
            snapshot.PruneFiles(FilePruningRequest.ForPartitions(new PartitionEqualityFilter(intKey, null)));
        Assert.Equal(["f1.parquet"], intIsNull.Candidates.Select(a => a.Path));

        // String column (D5): f2's "" is a real value, so IS NULL prunes both files.
        FilePruningResult stringIsNull =
            snapshot.PruneFiles(FilePruningRequest.ForPartitions(new PartitionEqualityFilter(stringKey, null)));
        Assert.Empty(stringIsNull.Candidates);
    }

    [Fact]
    public void SnapshotPruneFiles_DuplicatePartitionColumnEntry_KeepsTheType()
    {
        // metaData.partitionColumns ["s", "s"] is rejected only at commit, so a foreign table can load with it.
        // Both entries name the same field, so this is not an ambiguous physical name: the string type is kept
        // and IS NULL prunes the "" file (D5), as it does with a single entry.
        var metadata = new MetadataAction(
            Id: "duplicate-entry",
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
            SchemaString: "{\"type\":\"struct\",\"fields\":["
                + "{\"name\":\"s\",\"type\":\"string\",\"nullable\":true,\"metadata\":{}}]}",
            PartitionColumns: ["s", "s"],
            Configuration: ImmutableSortedDictionary<string, string>.Empty,
            CreatedTime: null);
        var snapshot = new Snapshot(
            version: 0,
            new ProtocolAction(1, 2, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty),
            metadata,
            [File("empty.parquet", partitions: [("s", "")]), File("null.parquet", partitions: [("s", null)])],
            ImmutableArray<RemoveFileAction>.Empty,
            ImmutableSortedDictionary<string, long>.Empty,
            SnapshotLoadMetrics.Empty);

        FilePruningResult result =
            snapshot.PruneFiles(FilePruningRequest.ForPartitions(new PartitionEqualityFilter("s", null)));

        Assert.Equal(["null.parquet"], result.Candidates.Select(a => a.Path));
    }

    [Theory]
    [InlineData("n", "s")]   // a last-wins map would type "p" as string
    [InlineData("s", "n")]   // a first-wins map would type "p" as string
    public void SnapshotPruneFiles_SharedPhysicalName_DropsTheType_KeepsEmptyStringFile(string first, string second)
    {
        // A hand-built snapshot (the load gate rejects two mapped columns with one physical name) where an integer
        // and a string partition column share the physical name "p". The type is ambiguous, so it is dropped and
        // IS NULL keeps the "" file, which the integer column's read returns as null. Both orders are tested, so
        // neither first-wins nor last-wins passes.
        const string schema =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"n\",\"type\":\"integer\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"p\"}},"
            + "{\"name\":\"s\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"p\"}}]}";
        var metadata = new MetadataAction(
            Id: "shared-physical",
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
            SchemaString: schema,
            PartitionColumns: [first, second],
            Configuration: ImmutableSortedDictionary<string, string>.Empty
                .Add("delta.columnMapping.mode", "name")
                .Add("delta.columnMapping.maxColumnId", "2"),
            CreatedTime: null);
        var snapshot = new Snapshot(
            version: 0,
            new ProtocolAction(2, 5, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty),
            metadata,
            [File("empty.parquet", partitions: [("p", "")])],
            ImmutableArray<RemoveFileAction>.Empty,
            ImmutableSortedDictionary<string, long>.Empty,
            SnapshotLoadMetrics.Empty);

        FilePruningResult result =
            snapshot.PruneFiles(FilePruningRequest.ForPartitions(new PartitionEqualityFilter("p", null)));

        Assert.Equal(["empty.parquet"], result.Candidates.Select(a => a.Path));
    }

    [Fact]
    public void PartitionFilter_UnknownType_EmptyStringAgainstNonEmptyValue_Prunes()
    {
        // The unknown-type leniency covers only "" against null. A "" file can never equal "x" under any type,
        // so the filter still prunes it.
        ImmutableArray<AddFileAction> files = [File("empty.parquet", partitions: [("c", "")])];

        FilePruningResult result =
            Prune(files, FilePruningRequest.ForPartitions(new PartitionEqualityFilter("c", "x")));

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void SnapshotPruneFiles_SharedPhysicalName_KeepsOtherColumnsTyped()
    {
        // Dropping the ambiguous type of "p" must not drop the type of an unrelated column: "q" is a string
        // column, so its "" is a real value (D5) and IS NULL prunes the file.
        const string schema =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"n\",\"type\":\"integer\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"p\"}},"
            + "{\"name\":\"s\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"p\"}},"
            + "{\"name\":\"t\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":3,\"delta.columnMapping.physicalName\":\"q\"}}]}";
        var metadata = new MetadataAction(
            Id: "shared-physical-other",
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
            SchemaString: schema,
            PartitionColumns: ["n", "s", "t"],
            Configuration: ImmutableSortedDictionary<string, string>.Empty
                .Add("delta.columnMapping.mode", "name")
                .Add("delta.columnMapping.maxColumnId", "3"),
            CreatedTime: null);
        var snapshot = new Snapshot(
            version: 0,
            new ProtocolAction(2, 5, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty),
            metadata,
            [File("f.parquet", partitions: [("p", "1"), ("q", "")])],
            ImmutableArray<RemoveFileAction>.Empty,
            ImmutableSortedDictionary<string, long>.Empty,
            SnapshotLoadMetrics.Empty);

        FilePruningResult result =
            snapshot.PruneFiles(FilePruningRequest.ForPartitions(new PartitionEqualityFilter("q", null)));

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void SnapshotPruneFiles_NoPartitionFilter_DoesNotResolveTheSchema()
    {
        // The partition types are resolved only for a request with a partition filter, so a data-only request on
        // a snapshot whose schema cannot be parsed still returns every file instead of throwing.
        var metadata = new MetadataAction(
            Id: "bad-schema",
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
            SchemaString: "not json",
            PartitionColumns: ["n"],
            Configuration: ImmutableSortedDictionary<string, string>.Empty,
            CreatedTime: null);
        var snapshot = new Snapshot(
            version: 0,
            new ProtocolAction(1, 2, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty),
            metadata,
            [File("f.parquet", partitions: [("n", "")])],
            ImmutableArray<RemoveFileAction>.Empty,
            ImmutableSortedDictionary<string, long>.Empty,
            SnapshotLoadMetrics.Empty);

        FilePruningResult result = snapshot.PruneFiles(FilePruningRequest.Empty);

        Assert.Single(result.Candidates);
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

    // ---------------------------------------------------------------- per-bound (one-sided) pruning

    [Fact]
    public void DataFilter_MinPresentMaxAbsent_RecoversLowerBoundSkip_KeepsUpperAndEqual()
    {
        // A NaN-bearing numeric column [5.0, NaN]: the collector keeps the finite min (5.0) but OMITS the
        // max (NaN is Spark's GREATEST value, so the true max is NaN and unencodable). Per-bound pruning
        // must RECOVER the min-only skip for lower-bound predicates (this whole test FAILS on the previous
        // require-both-bounds code, which kept the file) while staying sound on the upper/equality side.
        FileStatistics stats = PartialDoubleStats("v", numRecords: 2, min: 5.0, max: null);

        // Lower-bound predicates use MIN only → recovered skips.
        AssertSkipped(stats, DeltaPredicateOp.LessThan, 4.0);          // min 5.0 >= 4.0 → no value < 4.0
        AssertSkipped(stats, DeltaPredicateOp.LessThan, 5.0);          // min 5.0 >= 5.0 → no value < 5.0
        AssertSkipped(stats, DeltaPredicateOp.LessThanOrEqual, 4.0);   // min 5.0 > 4.0 → no value <= 4.0
        AssertSkipped(stats, DeltaPredicateOp.Equal, 4.0);             // 4.0 < min → cannot equal

        // Lower-bound predicates the min does NOT prove disjoint → KEEP.
        AssertKept(stats, DeltaPredicateOp.LessThan, 6.0);             // 5.0 < 6.0 matches
        AssertKept(stats, DeltaPredicateOp.LessThanOrEqual, 5.0);      // 5.0 <= 5.0 matches

        // Upper-bound / equality predicates need MAX (absent) → KEEP (the NaN row may match `>`/`>=`/`=`).
        AssertKept(stats, DeltaPredicateOp.GreaterThan, 6.0);
        AssertKept(stats, DeltaPredicateOp.GreaterThan, 4.0);
        AssertKept(stats, DeltaPredicateOp.GreaterThanOrEqual, 6.0);
        AssertKept(stats, DeltaPredicateOp.Equal, 6.0);
        AssertKept(stats, DeltaPredicateOp.Equal, 5.0);
    }

    [Fact]
    public void DataFilter_MaxPresentMinAbsent_RecoversUpperBoundSkip_KeepsLowerAndEqual()
    {
        // The symmetric case — a -Infinity-bearing column [-Infinity, 5.0]: the collector keeps the finite
        // max (5.0) but OMITS the min (-Infinity has no JSON-number encoding). Per-bound pruning recovers
        // the max-only skip for upper-bound predicates while staying sound on the lower/equality side.
        FileStatistics stats = PartialDoubleStats("v", numRecords: 2, min: null, max: 5.0);

        // Upper-bound predicates use MAX only → recovered skips.
        AssertSkipped(stats, DeltaPredicateOp.GreaterThan, 6.0);          // max 5.0 <= 6.0 → no value > 6.0
        AssertSkipped(stats, DeltaPredicateOp.GreaterThan, 5.0);          // max 5.0 <= 5.0 → no value > 5.0
        AssertSkipped(stats, DeltaPredicateOp.GreaterThanOrEqual, 6.0);   // max 5.0 < 6.0 → no value >= 6.0
        AssertSkipped(stats, DeltaPredicateOp.Equal, 6.0);               // 6.0 > max → cannot equal

        // Upper-bound predicates the max does NOT prove disjoint → KEEP.
        AssertKept(stats, DeltaPredicateOp.GreaterThan, 4.0);            // 5.0 > 4.0 matches
        AssertKept(stats, DeltaPredicateOp.GreaterThanOrEqual, 5.0);     // 5.0 >= 5.0 matches

        // Lower-bound / equality-below predicates need MIN (absent) → KEEP (a -Infinity row may match).
        AssertKept(stats, DeltaPredicateOp.LessThan, 4.0);
        AssertKept(stats, DeltaPredicateOp.LessThanOrEqual, -100.0);
        AssertKept(stats, DeltaPredicateOp.Equal, 4.0);
        AssertKept(stats, DeltaPredicateOp.Equal, 5.0);
    }

    [Fact]
    public void NaNColumn_KeptForGreaterThanFinite_ButSkippedForLessThanMin()
    {
        // The exact round-2 soundness property (PRESERVED) plus the round-3 recovery, on one column: a
        // NaN-bearing column [5.0, NaN] (finite min 5.0 present, max omitted) MUST be KEPT for `> finite`
        // (NaN is Spark's greatest value, so the NaN row satisfies `> 6.0`) AND is now SKIPPED for
        // `< min` via the surviving finite min — a valid optimization the require-both code forfeited.
        FileStatistics stats = PartialDoubleStats("v", numRecords: 2, min: 5.0, max: null);

        AssertKept(stats, DeltaPredicateOp.GreaterThan, 6.0);          // NaN > 6.0 is TRUE under Spark order
        AssertKept(stats, DeltaPredicateOp.GreaterThanOrEqual, 6.0);   // NaN >= 6.0 is TRUE
        AssertKept(stats, DeltaPredicateOp.Equal, 5.0);                // 5.0 = 5.0 matches

        AssertSkipped(stats, DeltaPredicateOp.LessThan, 4.0);          // no value < 4.0 (min 5.0, NaN not <)
    }

    [Fact]
    public void DataFilter_Equality_PerBound_SkipsOnEitherBoundElseKeeps()
    {
        // `= X` can be proven disjoint by EITHER bound alone: X below min (min side) or X above max (max
        // side). An in-range or unprovable value is kept.
        FileStatistics minOnly = PartialDoubleStats("v", numRecords: 2, min: 5.0, max: null);
        AssertSkipped(minOnly, DeltaPredicateOp.Equal, 4.0);   // 4.0 < min 5.0 → skip on the MIN side
        AssertKept(minOnly, DeltaPredicateOp.Equal, 6.0);      // 6.0 >= min, max absent → unprovable → keep

        FileStatistics maxOnly = PartialDoubleStats("v", numRecords: 2, min: null, max: 5.0);
        AssertSkipped(maxOnly, DeltaPredicateOp.Equal, 6.0);   // 6.0 > max 5.0 → skip on the MAX side
        AssertKept(maxOnly, DeltaPredicateOp.Equal, 4.0);      // 4.0 <= max, min absent → unprovable → keep

        FileStatistics both = PartialDoubleStats("v", numRecords: 3, min: 10.0, max: 20.0);
        AssertKept(both, DeltaPredicateOp.Equal, 15.0);        // 10 <= 15 <= 20 → in range → keep
        AssertSkipped(both, DeltaPredicateOp.Equal, 5.0);      // below min → skip (min side)
        AssertSkipped(both, DeltaPredicateOp.Equal, 25.0);     // above max → skip (max side)
    }

    [Fact]
    public void Pruning_IsSound_WithPartialBounds_UnderRandomizedData()
    {
        // Extends the soundness fuzzer to PARTIAL-bound columns — ParquetStatisticsCollector omits the max
        // for a NaN/+Infinity column (finite min only) and the min for a -Infinity column (finite max
        // only). Every pruned file must still genuinely contain no row matching the predicate under Spark's
        // float TOTAL ORDER (NaN is the GREATEST value). Seeded → deterministic; no wall-clock/unseeded RNG.
        var random = new Random(20260709);
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            int rowCount = random.Next(1, 8);
            var rows = new double?[rowCount];
            for (int r = 0; r < rowCount; r++)
            {
                rows[r] = random.Next(0, 6) switch
                {
                    0 => null,                     // SQL null
                    1 => double.NaN,               // omits max (NaN is greatest)
                    2 => double.PositiveInfinity,  // omits max (+Infinity unencodable)
                    3 => double.NegativeInfinity,  // omits min (-Infinity unencodable)
                    _ => random.Next(-20, 20),     // finite
                };
            }

            double[] present = rows.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            long nulls = rowCount - present.Length;

            // Replicate ParquetStatisticsCollector.CollectDouble exactly: a NaN never enters min/max but
            // omits the max; min/max are computed over the non-NaN values; a non-finite (±Infinity) bound is
            // omitted. tightBounds stays true (only string truncation clears it).
            bool sawNaN = present.Any(double.IsNaN);
            double[] nonNaN = present.Where(v => !double.IsNaN(v)).ToArray();
            bool any = nonNaN.Length > 0;
            double lo = any ? nonNaN.Min() : 0.0;
            double hi = any ? nonNaN.Max() : 0.0;
            double? min = any && double.IsFinite(lo) ? lo : null;
            double? max = any && !sawNaN && double.IsFinite(hi) ? hi : null;

            FileStatistics stats = PartialDoubleStats("v", rowCount, min, max, nulls);

            var op = (DeltaPredicateOp)random.Next(0, 5);
            double value = random.Next(-22, 22);
            FilePruningResult result = Prune(
                [File("f.parquet", stats: stats)],
                FilePruningRequest.ForData(new ColumnRangeFilter("v", op, DeltaStatValue.OfDouble(value))));

            if (result.Candidates.IsEmpty)
            {
                Assert.False(present.Any(v => SatisfiesSparkDouble(op, v, value)),
                    $"Unsound partial-bound prune: op={op} value={value} rows=[{string.Join(",", present)}] nulls={nulls}");
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
    public void StatsPresent_ButPredicateColumnMinMaxOmitted_AreAlwaysKept_ForEveryOp()
    {
        // AC3 fail-open: the file HAS statistics (a record count and another indexed column), but the
        // predicate column's min/max are omitted — e.g. it lies beyond the 32-column indexing horizon or
        // is an omitted type. With no bound to prove non-matching, the file must be kept (scanned) for
        // every predicate; a missing min/max must NEVER make the file skip-eligible.
        var min = ImmutableSortedDictionary.CreateBuilder<string, DeltaStatValue>(StringComparer.Ordinal);
        var max = ImmutableSortedDictionary.CreateBuilder<string, DeltaStatValue>(StringComparer.Ordinal);
        var nulls = ImmutableSortedDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
        min["indexed"] = DeltaStatValue.OfLong(0);
        max["indexed"] = DeltaStatValue.OfLong(100);
        nulls["indexed"] = 0;
        var stats = new FileStatistics(
            10, min.ToImmutable(), max.ToImmutable(), nulls.ToImmutable(), TightBounds: true);
        ImmutableArray<AddFileAction> files = [File("f.parquet", stats: stats)];

        foreach (DeltaPredicateOp op in AllOps)
        {
            foreach (long value in new long[] { -1000, 0, 50, 1000 })
            {
                FilePruningResult result = Prune(
                    files, FilePruningRequest.ForData(new ColumnRangeFilter("id", op, DeltaStatValue.OfLong(value))));

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

    // The soundness oracle for double columns under Spark's float TOTAL ORDER, where NaN is the GREATEST
    // value (engine KernelScalars.CompareDouble). C#'s IEEE operators return false for every NaN
    // comparison, so NaN's ordering is applied explicitly; ±Infinity order matches IEEE. The predicate
    // literal is always finite in the generator.
    private static bool SatisfiesSparkDouble(DeltaPredicateOp op, double rowValue, double predicateValue)
    {
        if (double.IsNaN(rowValue))
        {
            return op is DeltaPredicateOp.GreaterThan or DeltaPredicateOp.GreaterThanOrEqual;
        }

        return op switch
        {
            DeltaPredicateOp.Equal => rowValue == predicateValue,
            DeltaPredicateOp.LessThan => rowValue < predicateValue,
            DeltaPredicateOp.LessThanOrEqual => rowValue <= predicateValue,
            DeltaPredicateOp.GreaterThan => rowValue > predicateValue,
            DeltaPredicateOp.GreaterThanOrEqual => rowValue >= predicateValue,
            _ => false,
        };
    }

    private static ImmutableSortedDictionary<string, T> Singleton<T>(string key, T value) =>
        ImmutableSortedDictionary<string, T>.Empty.WithComparers(StringComparer.Ordinal).Add(key, value);
}
