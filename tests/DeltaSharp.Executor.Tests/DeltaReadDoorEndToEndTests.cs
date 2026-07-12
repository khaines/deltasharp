using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DeltaSharp.Analysis;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// #499 end-to-end tests for the Delta read door: they drive the PUBLIC Core API
/// (<see cref="SparkSession.Read"/> → <c>Format("delta")</c> → <c>Load(path)</c>, with
/// <c>versionAsOf</c>/<c>timestampAsOf</c> and the <c>@v&lt;n&gt;</c>/<c>@yyyyMMddHHmmssSSS</c> path syntax)
/// against a real on-disk Delta table written through the PUBLIC write door
/// (<see cref="DataFrame.Write"/> → <c>Format("delta")</c> → <c>Save(path)</c>). Each test writes a table
/// (append v0, append v1, overwrite), then reads it back through the executor's Delta scan-source + the
/// public Storage read facade and asserts the exact row multiset, schema, partition values, and the pinned
/// resolved version (via the internal <see cref="DataFrame.ResolveDeltaReadVersion"/> surface) — the true
/// write→read round-trip, plus the fail-closed analysis diagnostics.
/// </summary>
[Collection(SessionExecutionTestCollection.Name)]
public sealed class DeltaReadDoorEndToEndTests : IDisposable
{
    private static readonly StructType PeopleSchema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField("name", StringType.Instance, nullable: true),
    });

    // A partitioned table: region is the (last) partition column.
    private static readonly StructType RegionSchema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField("name", StringType.Instance, nullable: true),
        new StructField("region", StringType.Instance, nullable: true),
    });

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "delta-readdoor-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string Table(string name) => Path.Combine(_root, name);

    private static SparkSession NewSession()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
        return SparkSession.Builder().AppName("delta-read-door-e2e").GetOrCreate();
    }

    private static IReadOnlyList<Row> People(params (int Id, string? Name)[] rows) =>
        rows.Select(r => new Row(PeopleSchema, r.Id, r.Name)).ToList();

    private static IReadOnlyList<Row> Regional(params (int Id, string? Name, string? Region)[] rows) =>
        rows.Select(r => new Row(RegionSchema, r.Id, r.Name, r.Region)).ToList();

    private void WritePeople(string table, string mode, params (int Id, string? Name)[] rows)
    {
        using SparkSession spark = NewSession();
        spark.CreateDataFrame(People(rows), PeopleSchema).Write.Format("delta").Mode(mode).Save(table);
    }

    private static List<(int Id, string? Name)> CollectPeople(DataFrame df) =>
        df.Collect()
            .Select(r => (r.GetAs<int>("id"), r.IsNullAt(r.Schema.IndexOf("name")) ? null : r.GetAs<string>("name")))
            .OrderBy(r => r.Item1)
            .ToList();

    // ---------------------------------------------------------------- base (latest) read

    [Fact]
    public void ReadLatest_Unpartitioned_RoundTripsRowsAndSchema()
    {
        string table = Table("latest");
        WritePeople(table, "append", (1, "alice"), (2, "bob"), (3, null));

        using SparkSession spark = NewSession();
        DataFrame df = spark.Read.Format("delta").Load(table);

        Assert.Equal(0L, df.ResolveDeltaReadVersion());
        IReadOnlyList<Row> collected = df.Collect();
        // The materialized schema matches the written table schema (partition-free here).
        Assert.Equal(
            new[] { "id", "name" }, collected[0].Schema.Fields.Select(f => f.Name).ToArray());
        Assert.Equal(
            new (int, string?)[] { (1, "alice"), (2, "bob"), (3, (string?)null) },
            collected
                .Select(r => (r.GetAs<int>("id"), r.IsNullAt(r.Schema.IndexOf("name")) ? null : r.GetAs<string>("name")))
                .OrderBy(r => r.Item1)
                .ToList());
    }

    // ---------------------------------------------------------------- versionAsOf (option + path syntax)

    [Fact]
    public void VersionAsOfOption_ReadsHistoricalSnapshot()
    {
        string table = Table("versioned");
        WritePeople(table, "append", (1, "alice"));           // v0
        WritePeople(table, "append", (2, "bob"));             // v1

        using SparkSession spark = NewSession();

        DataFrame v0 = spark.Read.Format("delta").Option("versionAsOf", "0").Load(table);
        Assert.Equal(0L, v0.ResolveDeltaReadVersion());
        Assert.Equal(new (int, string?)[] { (1, "alice") }, CollectPeople(v0));

        DataFrame latest = spark.Read.Format("delta").Load(table);
        Assert.Equal(1L, latest.ResolveDeltaReadVersion());
        Assert.Equal(new (int, string?)[] { (1, "alice"), (2, "bob") }, CollectPeople(latest));
    }

    [Fact]
    public void VersionAsOfPathSuffix_ReadsHistoricalSnapshot()
    {
        string table = Table("versioned-path");
        WritePeople(table, "append", (1, "alice"));           // v0
        WritePeople(table, "append", (2, "bob"));             // v1

        using SparkSession spark = NewSession();
        DataFrame v0 = spark.Read.Format("delta").Load(table + "@v0");

        Assert.Equal(0L, v0.ResolveDeltaReadVersion());
        Assert.Equal(new (int, string?)[] { (1, "alice") }, CollectPeople(v0));
    }

    // ---------------------------------------------------------------- overwrite then read

    [Fact]
    public void ReadLatest_AfterOverwrite_ReadsReplacedData()
    {
        string table = Table("overwritten");
        WritePeople(table, "append", (1, "alice"), (2, "bob"));   // v0
        WritePeople(table, "overwrite", (9, "zoe"));              // v1 replaces all

        using SparkSession spark = NewSession();
        DataFrame latest = spark.Read.Format("delta").Load(table);
        Assert.Equal(1L, latest.ResolveDeltaReadVersion());
        Assert.Equal(new (int, string?)[] { (9, "zoe") }, CollectPeople(latest));

        // ...and versionAsOf=0 still sees the pre-overwrite data.
        DataFrame v0 = spark.Read.Format("delta").Option("versionAsOf", "0").Load(table);
        Assert.Equal(new (int, string?)[] { (1, "alice"), (2, "bob") }, CollectPeople(v0));
    }

    // ---------------------------------------------------------------- partitioned round-trip

    [Fact]
    public void ReadLatest_Partitioned_ReDerivesPartitionColumn()
    {
        string table = Table("partitioned");
        using (SparkSession spark = NewSession())
        {
            spark.CreateDataFrame(Regional((1, "alice", "US"), (2, "bob", "EU")), RegionSchema)
                .Write.Format("delta").Mode("append").PartitionBy("region").Save(table);
        }

        using SparkSession read = NewSession();
        DataFrame df = read.Read.Format("delta").Load(table);

        Assert.Equal(0L, df.ResolveDeltaReadVersion());
        List<(int, string?, string?)> rows = df.Collect()
            .Select(r => (
                r.GetAs<int>("id"),
                r.IsNullAt(r.Schema.IndexOf("name")) ? null : r.GetAs<string>("name"),
                r.IsNullAt(r.Schema.IndexOf("region")) ? null : r.GetAs<string>("region")))
            .OrderBy(r => r.Item1)
            .ToList();

        Assert.Equal(
            new (int, string?, string?)[] { (1, "alice", "US"), (2, "bob", "EU") },
            rows);
    }

    // ---------------------------------------------------------------- timestampAsOf (deterministic mtimes)

    [Fact]
    public void TimestampAsOfOption_ReadsHistoricalSnapshot()
    {
        string table = Table("timestamped");
        WritePeople(table, "append", (1, "alice"));           // v0
        WritePeople(table, "append", (2, "bob"));             // v1

        // Pin deterministic commit timestamps so resolution is not at the mercy of mtime granularity.
        SetCommitTimestamp(table, 0, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        SetCommitTimestamp(table, 1, new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero));

        using SparkSession spark = NewSession();
        DataFrame between = spark.Read
            .Format("delta").Option("timestampAsOf", "2020-06-01 00:00:00").Load(table);

        Assert.Equal(0L, between.ResolveDeltaReadVersion());
        Assert.Equal(new (int, string?)[] { (1, "alice") }, CollectPeople(between));
    }

    // ---------------------------------------------------------------- fail-closed analysis diagnostics

    [Fact]
    public void BothVersionAndTimestamp_ThrowsAnalysisException()
    {
        string table = Table("conflict");
        WritePeople(table, "append", (1, "alice"));

        using SparkSession spark = NewSession();
        DataFrame df = spark.Read
            .Format("delta")
            .Option("versionAsOf", "0")
            .Option("timestampAsOf", "2020-01-01")
            .Load(table);

        AnalysisException ex = Assert.Throws<AnalysisException>(() => df.Collect());
        Assert.Equal(AnalysisErrorKind.InvalidTimeTravelSpec, ex.Kind);
    }

    [Fact]
    public void ReadNonExistentTable_ThrowsAnalysisException()
    {
        using SparkSession spark = NewSession();
        DataFrame df = spark.Read.Format("delta").Load(Table("does-not-exist"));

        AnalysisException ex = Assert.Throws<AnalysisException>(() => df.Collect());
        Assert.Equal(AnalysisErrorKind.FileSourceResolutionFailed, ex.Kind);
    }

    [Fact]
    public void VersionAsOfOutOfRange_ThrowsAnalysisException()
    {
        string table = Table("range");
        WritePeople(table, "append", (1, "alice"));

        using SparkSession spark = NewSession();
        DataFrame df = spark.Read.Format("delta").Option("versionAsOf", "42").Load(table);

        AnalysisException ex = Assert.Throws<AnalysisException>(() => df.Collect());
        Assert.Equal(AnalysisErrorKind.FileSourceResolutionFailed, ex.Kind);
    }

    // ---------------------------------------------------------------- transformations over a delta read

    [Fact]
    public void ReadLatest_ThenFilterAndSelect_ExecutesOverScannedRows()
    {
        string table = Table("transform");
        WritePeople(table, "append", (1, "alice"), (2, "bob"), (3, "carol"));

        using SparkSession spark = NewSession();
        DataFrame df = spark.Read.Format("delta").Load(table)
            .Filter(Functions.Col("id").Gt(Functions.Lit(1)))
            .Select("name");

        List<string?> names = df.Collect()
            .Select(r => r.IsNullAt(0) ? null : r.GetAs<string>("name"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "bob", "carol" }, names);
    }

    // ---------------------------------------------------------------- lazy scan: Explain does NO data I/O

    [Fact]
    public void Explain_DeltaRead_PlansPhysically_WithoutReadingDataFiles()
    {
        string table = Table("explain-no-io");
        WritePeople(table, "append", (1, "alice"), (2, "bob"));

        // Delete every data (.parquet) file but KEEP the _delta_log. If physical PLANNING read data files
        // (the lazy/eager violation), Explain's physical section could not open them and would render the
        // "<cannot plan physically: …>" diagnostic instead of a real Scan node. The lazy ScanPlan thunk
        // defers all data-plane I/O to execution, so planning renders the physical Scan without touching a
        // single Parquet byte (analysis reads only the still-present log metadata).
        DeleteDataFiles(table);

        using SparkSession spark = NewSession();
        DataFrame df = spark.Read.Format("delta").Load(table);

        string explain = df.ToExplainString(ExplainMode.Extended);

        Assert.Contains("Scan", explain);
        Assert.DoesNotContain("cannot plan physically", explain);

        // And the read genuinely IS deferred to execution: collecting now DOES touch the (deleted) files
        // and fails — proving Explain above did no data I/O.
        Assert.ThrowsAny<Exception>(() => df.Collect());
    }

    // ---------------------------------------------------------------- ResolveDeltaReadVersion single-leaf

    [Fact]
    public void ResolveDeltaReadVersion_MultipleDeltaLeaves_Throws()
    {
        string left = Table("multi-left");
        string right = Table("multi-right");
        WritePeople(left, "append", (1, "alice"));
        WritePeople(right, "append", (2, "bob"));

        using SparkSession spark = NewSession();
        DataFrame combined = spark.Read.Format("delta").Load(left)
            .Union(spark.Read.Format("delta").Load(right));

        // A union of two Delta reads has TWO pinned-version leaves and therefore no single resolved
        // version; the invariant is enforced (not "first match wins").
        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => combined.ResolveDeltaReadVersion());
        Assert.Contains("more than one Delta read leaf", ex.Message);
    }

    // ---------------------------------------------------------------- null partition round-trip (write→read)

    [Fact]
    public void NullPartitionValue_RoundTripsThroughWriteAndReadDoors()
    {
        string table = Table("null-partition");
        using (SparkSession write = NewSession())
        {
            write.CreateDataFrame(Regional((1, "alice", "US"), (2, "bob", null)), RegionSchema)
                .Write.Format("delta").Mode("append").PartitionBy("region").Save(table);
        }

        using SparkSession read = NewSession();
        List<(int, string?, string?)> rows = read.Read.Format("delta").Load(table).Collect()
            .Select(r => (
                r.GetAs<int>("id"),
                r.IsNullAt(r.Schema.IndexOf("name")) ? null : r.GetAs<string>("name"),
                r.IsNullAt(r.Schema.IndexOf("region")) ? null : r.GetAs<string>("region")))
            .OrderBy(r => r.Item1)
            .ToList();

        Assert.Equal(
            new (int, string?, string?)[] { (1, "alice", "US"), (2, "bob", (string?)null) },
            rows);
    }

    // ---------------------------------------------------------------- helpers

    private static void DeleteDataFiles(string table)
    {
        foreach (string file in Directory.EnumerateFiles(table, "*.parquet", SearchOption.AllDirectories))
        {
            // Data files live outside _delta_log; checkpoint .parquet files (if any) live inside it and are
            // metadata, not data — never touched by a base read, so leaving them is fine either way.
            if (!file.Contains(Path.Combine("_delta_log"), StringComparison.Ordinal))
            {
                File.Delete(file);
            }
        }
    }

    private static void SetCommitTimestamp(string table, long version, DateTimeOffset timestamp)
    {
        string commit = Path.Combine(
            table, "_delta_log", version.ToString("D20", CultureInfo.InvariantCulture) + ".json");
        File.SetLastWriteTimeUtc(commit, timestamp.UtcDateTime);
    }
}
