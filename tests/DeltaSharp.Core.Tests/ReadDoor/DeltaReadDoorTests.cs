using System;
using System.Collections.Generic;
using DeltaSharp.Analysis;
using DeltaSharp.Core.Tests.LazyEager;
using DeltaSharp.Diagnostics;
using DeltaSharp.Plans;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.ReadDoor;

/// <summary>
/// STORY-05.4.1 follow-up (#499) — the Delta read door on the Core surface:
/// <c>spark.read.format("delta").load(path)</c> plus the <c>versionAsOf</c>/<c>timestampAsOf</c> options and
/// the <c>@v&lt;n&gt;</c>/<c>@yyyyMMddHHmmssSSS</c> path syntax. Covers the lazy <see cref="DataFrameReader.Load"/>
/// finalizer (builds an unresolved <c>delta</c> scan, opens no log), delta option validation, the analyzer
/// un-defer (a <c>delta</c> scan resolves through the read-door <c>IFileRelationResolver</c> seam instead of
/// throwing), the time-travel option/path parsing threaded to the resolver, and the both-specified /
/// invalid-value / no-backend / declined diagnostics. End-to-end reads against a real Delta table live in
/// <c>DeltaSharp.Executor.Tests</c> (Core cannot open storage).
/// </summary>
[Collection(SparkSessionTestCollection.Name)]
public sealed class DeltaReadDoorTests
{
    private static readonly StructType PeopleSchema = new(new[]
    {
        new StructField("id", LongType.Instance, nullable: false),
        new StructField("name", StringType.Instance, nullable: true),
    });

    public DeltaReadDoorTests()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
    }

    private static SparkSession NewSession() =>
        SparkSession.Builder().AppName("delta-read-door").GetOrCreate();

    // ---------------- lazy Load finalizer ----------------

    [Fact]
    public void Load_Delta_BuildsUnresolvedDeltaScan_WithoutOpeningLog()
    {
        using SparkSession spark = NewSession();
        var recording = new RecordingAudit();

        DataFrame df;
        using (ExecutionAudit.BeginScope(recording))
        {
            df = spark.Read.Format("delta").Load("/tables/people");
        }

        UnresolvedFileRelation file = Assert.IsType<UnresolvedFileRelation>(df.Plan);
        Assert.False(file.Resolved);
        Assert.Equal("delta", file.Format);
        Assert.Equal("/tables/people", file.Path);
        Assert.True(recording.ObservedNoExecution);
    }

    [Fact]
    public void Load_WithoutFormat_DefaultsToParquet()
    {
        using SparkSession spark = NewSession();

        UnresolvedFileRelation file = Assert.IsType<UnresolvedFileRelation>(
            spark.Read.Load("/tables/x").Plan);

        Assert.Equal("parquet", file.Format);
    }

    [Fact]
    public void Format_IsFluent()
    {
        using SparkSession spark = NewSession();
        DataFrameReader reader = spark.Read;

        Assert.Same(reader, reader.Format("delta"));
    }

    [Theory]
    [InlineData("versionAsOf", "3")]
    [InlineData("timestampAsOf", "2020-01-01 00:00:00")]
    public void Load_Delta_RecognizedTimeTravelOption_IsRecorded(string key, string value)
    {
        using SparkSession spark = NewSession();

        UnresolvedFileRelation file = Assert.IsType<UnresolvedFileRelation>(
            spark.Read.Format("delta").Option(key, value).Load("/t").Plan);

        Assert.Equal(value, file.Options[key]);
    }

    [Fact]
    public void Load_Delta_OptionKeysAreCaseInsensitive_CanonicalizedToSparkSpelling()
    {
        using SparkSession spark = NewSession();

        UnresolvedFileRelation file = Assert.IsType<UnresolvedFileRelation>(
            spark.Read.Format("delta").Option("VERSIONASOF", "7").Load("/t").Plan);

        Assert.Equal("7", file.Options["versionAsOf"]);
    }

    [Fact]
    public void Load_Delta_UnsupportedOption_ThrowsNamingOptionAndFormat()
    {
        using SparkSession spark = NewSession();

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => spark.Read.Format("delta").Option("mergeSchema", "true").Load("/t"));

        Assert.Contains("mergeSchema", ex.Message);
        Assert.Contains("delta", ex.Message);
        Assert.Contains("versionAsOf", ex.Message);
    }

    [Fact]
    public void Load_OnStoppedSession_Throws()
    {
        SparkSession spark = NewSession();
        DataFrameReader reader = spark.Read;
        spark.Stop();

        Assert.Throws<SessionStoppedException>(() => reader.Format("delta").Load("/t"));
    }

    // ---------------- analyzer un-defer: delta resolves through the seam ----------------

    [Fact]
    public void Analyzer_ResolvesDeltaScan_MintsOutput_AndPinsVersionInReservedOptions()
    {
        using SparkSession spark = NewSession();
        var resolver = new StubResolver(PeopleSchema, resolvedVersion: 4);
        LogicalPlan plan = spark.Read.Format("delta").Load("/tables/people").Plan;

        LogicalPlan resolved = new Analyzer(new LocalCatalog(), resolver).Resolve(plan);

        ResolvedRelation relation = Assert.IsType<ResolvedRelation>(resolved);
        Assert.True(relation.Resolved);
        Assert.Equal(PeopleSchema, relation.Schema);
        Assert.True(DeltaReadRelation.TryGet(relation.Options, out string path, out long version));
        Assert.Equal("/tables/people", path);
        Assert.Equal(4, version);

        // A base read pins the latest version and passes neither dimension to the resolver.
        Assert.Equal("/tables/people", resolver.LastRequest!.Value.Path);
        Assert.Null(resolver.LastRequest.Value.VersionAsOf);
        Assert.Null(resolver.LastRequest.Value.TimestampAsOf);
    }

    [Fact]
    public void Analyzer_DeltaScan_VersionAsOfOption_ThreadedToResolver()
    {
        using SparkSession spark = NewSession();
        var resolver = new StubResolver(PeopleSchema, resolvedVersion: 2);
        LogicalPlan plan = spark.Read.Format("delta").Option("versionAsOf", "2").Load("/t").Plan;

        _ = new Analyzer(new LocalCatalog(), resolver).Resolve(plan);

        Assert.Equal(2L, resolver.LastRequest!.Value.VersionAsOf);
        Assert.Null(resolver.LastRequest.Value.TimestampAsOf);
    }

    [Fact]
    public void Analyzer_DeltaScan_PathVersionSuffix_StrippedAndThreadedToResolver()
    {
        using SparkSession spark = NewSession();
        var resolver = new StubResolver(PeopleSchema, resolvedVersion: 5);
        LogicalPlan plan = spark.Read.Format("delta").Load("/tables/people@v5").Plan;

        _ = new Analyzer(new LocalCatalog(), resolver).Resolve(plan);

        Assert.Equal("/tables/people", resolver.LastRequest!.Value.Path);
        Assert.Equal(5L, resolver.LastRequest.Value.VersionAsOf);
    }

    [Fact]
    public void Analyzer_DeltaScan_TimestampAsOfOption_ThreadedToResolverAsUtc()
    {
        using SparkSession spark = NewSession();
        var resolver = new StubResolver(PeopleSchema, resolvedVersion: 1);
        LogicalPlan plan = spark.Read
            .Format("delta").Option("timestampAsOf", "2021-06-15 12:30:00").Load("/t").Plan;

        _ = new Analyzer(new LocalCatalog(), resolver).Resolve(plan);

        DateTimeOffset? ts = resolver.LastRequest!.Value.TimestampAsOf;
        Assert.NotNull(ts);
        Assert.Equal(new DateTimeOffset(2021, 6, 15, 12, 30, 0, TimeSpan.Zero), ts!.Value);
        Assert.Null(resolver.LastRequest.Value.VersionAsOf);
    }

    [Fact]
    public void Analyzer_DeltaScan_PathTimestampSuffix_ThreadedToResolverAsUtc()
    {
        using SparkSession spark = NewSession();
        var resolver = new StubResolver(PeopleSchema, resolvedVersion: 1);
        LogicalPlan plan = spark.Read.Format("delta").Load("/t@20210615123000000").Plan;

        _ = new Analyzer(new LocalCatalog(), resolver).Resolve(plan);

        DateTimeOffset? ts = resolver.LastRequest!.Value.TimestampAsOf;
        Assert.NotNull(ts);
        Assert.Equal(new DateTimeOffset(2021, 6, 15, 12, 30, 0, TimeSpan.Zero), ts!.Value);
        Assert.Equal("/t", resolver.LastRequest.Value.Path);
    }

    // ---------------- both-specified / invalid time-travel (fail closed) ----------------

    [Fact]
    public void Analyzer_DeltaScan_VersionAndTimestampBothSpecified_Throws()
    {
        using SparkSession spark = NewSession();
        var resolver = new StubResolver(PeopleSchema, resolvedVersion: 0);
        LogicalPlan plan = spark.Read
            .Format("delta")
            .Option("versionAsOf", "1")
            .Option("timestampAsOf", "2020-01-01")
            .Load("/t").Plan;

        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => new Analyzer(new LocalCatalog(), resolver).Resolve(plan));

        Assert.Equal(AnalysisErrorKind.InvalidTimeTravelSpec, ex.Kind);
        Assert.Null(resolver.LastRequest); // rejected before any resolution I/O
    }

    [Fact]
    public void Analyzer_DeltaScan_VersionOptionAndPathVersionSuffix_Throws()
    {
        using SparkSession spark = NewSession();
        var resolver = new StubResolver(PeopleSchema, resolvedVersion: 0);
        LogicalPlan plan = spark.Read.Format("delta").Option("versionAsOf", "1").Load("/t@v2").Plan;

        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => new Analyzer(new LocalCatalog(), resolver).Resolve(plan));

        Assert.Equal(AnalysisErrorKind.InvalidTimeTravelSpec, ex.Kind);
    }

    [Fact]
    public void Analyzer_DeltaScan_NegativeVersionOption_Throws()
    {
        using SparkSession spark = NewSession();
        var resolver = new StubResolver(PeopleSchema, resolvedVersion: 0);
        LogicalPlan plan = spark.Read.Format("delta").Option("versionAsOf", "-1").Load("/t").Plan;

        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => new Analyzer(new LocalCatalog(), resolver).Resolve(plan));

        Assert.Equal(AnalysisErrorKind.InvalidTimeTravelSpec, ex.Kind);
    }

    [Fact]
    public void Analyzer_DeltaScan_UnparseableTimestampOption_Throws()
    {
        using SparkSession spark = NewSession();
        var resolver = new StubResolver(PeopleSchema, resolvedVersion: 0);
        LogicalPlan plan = spark.Read.Format("delta").Option("timestampAsOf", "not-a-date").Load("/t").Plan;

        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => new Analyzer(new LocalCatalog(), resolver).Resolve(plan));

        Assert.Equal(AnalysisErrorKind.InvalidTimeTravelSpec, ex.Kind);
    }

    // ---------------- backend wiring diagnostics ----------------

    [Fact]
    public void Analyzer_DeltaScan_NoResolverRegistered_ThrowsNamingExecutorBootstrap()
    {
        using SparkSession spark = NewSession();
        LogicalPlan plan = spark.Read.Format("delta").Load("/t").Plan;

        // No resolver (Core-only analyzer): a delta read cannot open storage.
        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => new Analyzer(new LocalCatalog()).Resolve(plan));

        Assert.Equal(AnalysisErrorKind.FileSourceResolutionFailed, ex.Kind);
        Assert.Contains("DeltaSharp.Executor", ex.Message);
    }

    [Fact]
    public void Analyzer_DeltaScan_ResolverDeclinesFormat_ThrowsUnsupportedDataSource()
    {
        using SparkSession spark = NewSession();
        var resolver = new StubResolver(PeopleSchema, resolvedVersion: 0, handle: false);
        LogicalPlan plan = spark.Read.Format("delta").Load("/t").Plan;

        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => new Analyzer(new LocalCatalog(), resolver).Resolve(plan));

        Assert.Equal(AnalysisErrorKind.UnsupportedDataSource, ex.Kind);
    }

    [Fact]
    public void Analyzer_DeltaScan_ResolverThrows_PropagatesAnalysisException()
    {
        using SparkSession spark = NewSession();
        var resolver = new ThrowingResolver();
        LogicalPlan plan = spark.Read.Format("delta").Load("/missing").Plan;

        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => new Analyzer(new LocalCatalog(), resolver).Resolve(plan));

        Assert.Equal(AnalysisErrorKind.FileSourceResolutionFailed, ex.Kind);
    }

    [Fact]
    public void ResolvedDeltaScan_SimpleString_RedactsPath_NeverLeaksTablePath()
    {
        using SparkSession spark = NewSession();
        var resolver = new StubResolver(PeopleSchema, resolvedVersion: 0);
        LogicalPlan plan = spark.Read.Format("delta").Load(
            "wasbs://c@acct.blob.core.windows.net/t?sig=SUPERSECRETSAS").Plan;

        LogicalPlan resolved = new Analyzer(new LocalCatalog(), resolver).Resolve(plan);
        string rendered = resolved.SimpleString;

        Assert.DoesNotContain("SUPERSECRETSAS", rendered);
        Assert.Contains("<redacted>", rendered);
    }

    // ---------------- stubs ----------------

    private sealed class StubResolver : IFileRelationResolver
    {
        private readonly StructType _schema;
        private readonly long _resolvedVersion;
        private readonly bool _handle;

        public StubResolver(StructType schema, long resolvedVersion, bool handle = true)
        {
            _schema = schema;
            _resolvedVersion = resolvedVersion;
            _handle = handle;
        }

        public FileRelationResolutionRequest? LastRequest { get; private set; }

        public bool TryResolve(FileRelationResolutionRequest request, out FileRelationResolution resolution)
        {
            LastRequest = request;
            if (!_handle)
            {
                resolution = null!;
                return false;
            }

            resolution = new FileRelationResolution(_schema, _resolvedVersion);
            return true;
        }
    }

    private sealed class ThrowingResolver : IFileRelationResolver
    {
        public bool TryResolve(FileRelationResolutionRequest request, out FileRelationResolution resolution) =>
            throw AnalysisException.FileSourceResolutionFailed(
                request.Format, request.Path, "the storage read facade failed to open the table");
    }
}
