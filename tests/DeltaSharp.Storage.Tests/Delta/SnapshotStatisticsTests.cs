using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Tests for the optimizer statistics view (STORY-05.6.3 AC4): <see cref="Snapshot.GetStatistics"/>
/// returns a read-only <see cref="TableStatisticsReport"/> carrying a freshness/version token (the
/// snapshot version), table aggregates, and per-file/column availability with explicit
/// <see cref="StatisticColumnState"/> absence reasons — so an optimizer can tell an omitted statistic
/// (a missing hint) apart from a data condition, and never treats a hint as a correctness input.
/// </summary>
public sealed class SnapshotStatisticsTests
{
    private static readonly ImmutableSortedDictionary<string, string?> NoPartitions =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly ImmutableSortedDictionary<string, string> NoTags =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    // A schema spanning every stat-eligibility class: supported scalars (id/name/amount), an unsupported
    // scalar (blob=binary), an over-precision decimal (bigdec), and nested types (tags/props/nested).
    private static readonly StructType RichSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
        new StructField("amount", DataTypes.CreateDecimalType(10, 2), nullable: true),
        new StructField("blob", DataTypes.BinaryType, nullable: true),
        new StructField("bigdec", DataTypes.CreateDecimalType(38, 2), nullable: true),
        new StructField("tags", DataTypes.CreateArrayType(DataTypes.StringType), nullable: true),
        new StructField("props", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType), nullable: true),
        new StructField("nested", new StructType(new[] { new StructField("x", DataTypes.LongType) }), nullable: true),
    });

    private static AddFileAction File(string path, long size, FileStatistics? stats) =>
        new(path, NoPartitions, size, ModificationTime: 0, DataChange: true, stats, NoTags);

    private static FileStatistics Stats(
        long? numRecords,
        (string Column, DeltaStatValue Value)[]? mins = null,
        (string Column, DeltaStatValue Value)[]? maxs = null,
        (string Column, long Count)[]? nulls = null,
        bool? tightBounds = true)
    {
        var minB = ImmutableSortedDictionary.CreateBuilder<string, DeltaStatValue>(StringComparer.Ordinal);
        var maxB = ImmutableSortedDictionary.CreateBuilder<string, DeltaStatValue>(StringComparer.Ordinal);
        var nullB = ImmutableSortedDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
        foreach ((string column, DeltaStatValue value) in mins ?? Array.Empty<(string, DeltaStatValue)>())
        {
            minB[column] = value;
        }

        foreach ((string column, DeltaStatValue value) in maxs ?? Array.Empty<(string, DeltaStatValue)>())
        {
            maxB[column] = value;
        }

        foreach ((string column, long count) in nulls ?? Array.Empty<(string, long)>())
        {
            nullB[column] = count;
        }

        return new FileStatistics(numRecords, minB.ToImmutable(), maxB.ToImmutable(), nullB.ToImmutable(), tightBounds);
    }

    private static TableStatisticsReport Report(long version, StructType schema, params AddFileAction[] files) =>
        SnapshotStatisticsReporter.Build(version, schema, files.ToImmutableArray(), StatisticsPolicy.Default);

    private static FileStatisticsReport OnlyFile(TableStatisticsReport report) => Assert.Single(report.Files);

    // ---------------------------------------------------------------- freshness token + aggregates

    [Fact]
    public void GetStatistics_CarriesSnapshotVersionAsFreshnessToken_AndAggregates()
    {
        TableStatisticsReport report = Report(
            version: 5,
            RichSchema,
            File("a.parquet", 100, Stats(3, nulls: new[] { ("id", 0L) })),
            File("b.parquet", 200, Stats(7, nulls: new[] { ("id", 0L) })));

        Assert.Equal(5L, report.Version);
        Assert.Equal(5L, report.FreshnessToken);
        Assert.Equal(2, report.FileCount);
        Assert.Equal(300L, report.TotalSizeInBytes);
        Assert.True(report.HasCompleteRecordCounts);
        Assert.Equal(10L, report.RecordCount);
    }

    [Fact]
    public void GetStatistics_WithAnyMissingRecordCount_ReturnsNullAggregate()
    {
        TableStatisticsReport report = Report(
            version: 9,
            RichSchema,
            File("a.parquet", 100, Stats(3, nulls: new[] { ("id", 0L) })),
            File("b.parquet", 200, stats: null)); // no stats → no numRecords

        Assert.False(report.HasCompleteRecordCounts);
        Assert.Null(report.RecordCount);
        Assert.Equal(300L, report.TotalSizeInBytes); // size still aggregates (it is always on the add)
    }

    // ---------------------------------------------------------------- per-column absence reasons

    [Fact]
    public void GetStatistics_TightScalarStats_AreAvailable_NestedAndUnsupportedAreOmitted()
    {
        FileStatistics stats = Stats(
            numRecords: 3,
            mins: new[]
            {
                ("id", DeltaStatValue.OfLong(1)),
                ("name", DeltaStatValue.OfString("a")),
                ("amount", DeltaStatValue.OfString("1.00")),
            },
            maxs: new[]
            {
                ("id", DeltaStatValue.OfLong(9)),
                ("name", DeltaStatValue.OfString("z")),
                ("amount", DeltaStatValue.OfString("9.00")),
            },
            nulls: new[] { ("id", 0L), ("name", 0L), ("amount", 0L) });
        FileStatisticsReport file = OnlyFile(Report(1, RichSchema, File("f.parquet", 1, stats)));

        Assert.Equal(StatisticColumnState.Available, file.StateOf("id"));
        Assert.Equal(StatisticColumnState.Available, file.StateOf("name"));
        Assert.Equal(StatisticColumnState.Available, file.StateOf("amount"));

        Assert.Equal(StatisticColumnState.OmittedUnsupportedType, file.StateOf("blob"));
        Assert.Equal(StatisticColumnState.OmittedUnsupportedType, file.StateOf("bigdec"));
        Assert.Equal(StatisticColumnState.OmittedNestedType, file.StateOf("tags"));
        Assert.Equal(StatisticColumnState.OmittedNestedType, file.StateOf("props"));
        Assert.Equal(StatisticColumnState.OmittedNestedType, file.StateOf("nested"));

        Assert.True(file.HasStatistics);
        Assert.Equal(3L, file.RecordCount);
    }

    [Fact]
    public void GetStatistics_NoStats_YieldsAbsentNoStatistics_ButTypeOmissionsStand()
    {
        FileStatisticsReport file = OnlyFile(Report(1, RichSchema, File("f.parquet", 1, stats: null)));

        Assert.False(file.HasStatistics);
        Assert.Null(file.RecordCount);
        Assert.Equal(StatisticColumnState.AbsentNoStatistics, file.StateOf("id"));
        Assert.Equal(StatisticColumnState.AbsentNoStatistics, file.StateOf("name"));
        Assert.Equal(StatisticColumnState.AbsentNoStatistics, file.StateOf("amount"));

        // Type-based omissions are independent of any file's statistics.
        Assert.Equal(StatisticColumnState.OmittedUnsupportedType, file.StateOf("blob"));
        Assert.Equal(StatisticColumnState.OmittedNestedType, file.StateOf("nested"));
    }

    [Fact]
    public void GetStatistics_AllNullColumn_YieldsAbsentAllNull()
    {
        FileStatistics stats = Stats(
            numRecords: 4,
            mins: new[] { ("name", DeltaStatValue.OfString("a")) },
            maxs: new[] { ("name", DeltaStatValue.OfString("z")) },
            nulls: new[] { ("id", 4L), ("name", 0L) }); // id entirely null; name has bounds
        FileStatisticsReport file = OnlyFile(Report(1, RichSchema, File("f.parquet", 1, stats)));

        Assert.Equal(StatisticColumnState.AbsentAllNull, file.StateOf("id"));
        Assert.Equal(StatisticColumnState.Available, file.StateOf("name"));
    }

    [Fact]
    public void GetStatistics_NonTightBounds_YieldsAbsentNonTightBounds()
    {
        FileStatistics stats = Stats(
            numRecords: 5,
            mins: new[] { ("id", DeltaStatValue.OfLong(1)) },
            maxs: new[] { ("id", DeltaStatValue.OfLong(9)) },
            nulls: new[] { ("id", 0L) },
            tightBounds: false);
        FileStatisticsReport file = OnlyFile(Report(1, RichSchema, File("f.parquet", 1, stats)));

        Assert.Equal(StatisticColumnState.AbsentNonTightBounds, file.StateOf("id"));
    }

    [Fact]
    public void GetStatistics_IndexedButNoBounds_YieldsAbsentNotIndexed()
    {
        // name has a nullCount but no min/max and is not all-null → the column simply was not indexed.
        FileStatistics stats = Stats(numRecords: 5, nulls: new[] { ("name", 1L) });
        FileStatisticsReport file = OnlyFile(Report(1, RichSchema, File("f.parquet", 1, stats)));

        Assert.Equal(StatisticColumnState.AbsentNotIndexed, file.StateOf("name"));
    }

    [Fact]
    public void FileReport_StateOf_DefaultsToAbsentForUnknownColumn()
    {
        FileStatisticsReport file = OnlyFile(Report(1, RichSchema, File("f.parquet", 1, Stats(1))));

        Assert.Equal(StatisticColumnState.AbsentNoStatistics, file.StateOf("no_such_column"));
    }

    [Fact]
    public void GetStatistics_EmptyTable_ReportsZeroFiles_AndCompleteZeroRecordCount()
    {
        TableStatisticsReport report = Report(0, RichSchema);

        Assert.Equal(0L, report.Version);
        Assert.Equal(0, report.FileCount);
        Assert.Equal(0L, report.TotalSizeInBytes);
        Assert.True(report.HasCompleteRecordCounts); // vacuously complete
        Assert.Equal(0L, report.RecordCount);
        Assert.Empty(report.Files);
    }
}
