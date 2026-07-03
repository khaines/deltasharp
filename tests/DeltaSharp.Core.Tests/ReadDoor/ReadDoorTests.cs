using System;
using System.Collections;
using System.Collections.Generic;
using DeltaSharp.Analysis;
using DeltaSharp.Core.Tests.LazyEager;
using DeltaSharp.Diagnostics;
using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.ReadDoor;

/// <summary>
/// STORY-04.1.2 (#158) — the read door and in-memory DataFrame creation. Covers the four acceptance
/// criteria on the Core surface: lazy <see cref="SparkSession.CreateDataFrame(IEnumerable{Row}, StructType)"/>
/// building an unmaterialized scan plan (AC1), <see cref="SparkSession.Read"/>.<c>Parquet</c> building an
/// unresolved Parquet scan without opening files (AC2), Spark-parity unsupported-option diagnostics
/// (AC3), and both sources rendering as a scan node in the plan tree (AC4). End-to-end materialization
/// of the in-memory source lives in <c>DeltaSharp.Executor.Tests</c> (Core cannot execute). See
/// <c>docs/engineering/design/read-door.md</c>.
/// </summary>
[Collection(SparkSessionTestCollection.Name)]
public sealed class ReadDoorTests
{
    private static readonly StructType PeopleSchema = new(new[]
    {
        new StructField("id", LongType.Instance, nullable: false),
        new StructField("name", StringType.Instance, nullable: true),
        new StructField("age", IntegerType.Instance, nullable: true),
    });

    public ReadDoorTests()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
    }

    private static SparkSession NewSession() =>
        SparkSession.Builder().AppName("read-door").GetOrCreate();

    private static IEnumerable<Row> SampleRows() => new[]
    {
        new Row(PeopleSchema, 1L, "alice", 30),
        new Row(PeopleSchema, 2L, "bob", null),
    };

    // ---------------- AC1: in-memory CreateDataFrame is lazy ----------------

    [Fact]
    public void CreateDataFrame_BuildsUnresolvedLocalRelation_ScanNode()
    {
        using SparkSession spark = NewSession();

        DataFrame df = spark.CreateDataFrame(SampleRows(), PeopleSchema);

        LocalRelation local = Assert.IsType<LocalRelation>(df.Plan);
        Assert.False(local.Resolved);
        Assert.Equal(PeopleSchema, local.Schema);
        Assert.Null(local.Output);
    }

    [Fact]
    public void CreateDataFrame_DoesNotEnumerate_TheRowSequence()
    {
        using SparkSession spark = NewSession();
        var rows = new ThrowingRowSequence();

        DataFrame df = spark.CreateDataFrame(rows, PeopleSchema);

        // Neither creation nor any transformation may enumerate the sequence (materialization is an
        // action-only step). The booby-trapped enumerator throws if ever iterated.
        Assert.False(rows.Enumerated);
        _ = df.Select("id").Filter(Functions.Col("id")).WithColumn("flag", Functions.Lit(true));
        Assert.False(rows.Enumerated);
    }

    [Fact]
    public void CreateDataFrame_AndTransformations_TouchNoAuditSeam()
    {
        using SparkSession spark = NewSession();
        var recording = new RecordingAudit();

        using (ExecutionAudit.BeginScope(recording))
        {
            DataFrame df = spark.CreateDataFrame(SampleRows(), PeopleSchema);
            _ = df.Select("id").Filter(Functions.Col("id"));
        }

        Assert.True(recording.ObservedNoExecution);
        Assert.Empty(recording.StagePath);
    }

    [Fact]
    public void Analyzer_ResolvesLocalRelation_MintsZeroBasedOutputWithSchemaTypes()
    {
        using SparkSession spark = NewSession();
        LogicalPlan plan = spark.CreateDataFrame(SampleRows(), PeopleSchema).Plan;

        LogicalPlan resolved = new Analyzer(new LocalCatalog()).Resolve(plan);

        LocalRelation local = Assert.IsType<LocalRelation>(resolved);
        Assert.True(local.Resolved);
        Assert.NotNull(local.Output);
        Assert.Collection(
            local.Output!,
            a => AssertAttribute(a, "id", LongType.Instance, nullable: false, exprId: 0),
            a => AssertAttribute(a, "name", StringType.Instance, nullable: true, exprId: 1),
            a => AssertAttribute(a, "age", IntegerType.Instance, nullable: true, exprId: 2));
    }

    [Fact]
    public void LocalRelation_RendersAsScanNode_InPlanTree()
    {
        using SparkSession spark = NewSession();

        string tree = spark.CreateDataFrame(SampleRows(), PeopleSchema).Plan.TreeString();

        Assert.Contains("LocalRelation", tree);
    }

    // ---------------- AC2: Read.Parquet builds an unresolved scan, opens no file ----------------

    [Fact]
    public void Read_Parquet_BuildsUnresolvedFileRelation_WithoutOpeningFiles()
    {
        using SparkSession spark = NewSession();
        var recording = new RecordingAudit();

        DataFrame df;
        using (ExecutionAudit.BeginScope(recording))
        {
            df = spark.Read.Option("mergeSchema", true).Parquet("/data/people.parquet");
        }

        UnresolvedFileRelation file = Assert.IsType<UnresolvedFileRelation>(df.Plan);
        Assert.False(file.Resolved);
        Assert.Equal("parquet", file.Format);
        Assert.Equal("/data/people.parquet", file.Path);
        Assert.Equal("true", file.Options["mergeSchema"]);
        Assert.True(recording.ObservedNoExecution);
    }

    [Fact]
    public void Reader_SchemaAndOptions_AreFluentAndRecorded()
    {
        using SparkSession spark = NewSession();
        DataFrameReader reader = spark.Read;

        Assert.Same(reader, reader.Schema(PeopleSchema));
        Assert.Same(reader, reader.Option("recursiveFileLookup", true));

        UnresolvedFileRelation file = Assert.IsType<UnresolvedFileRelation>(reader.Parquet("/p").Plan);
        Assert.Equal(PeopleSchema, file.UserSchema);
        Assert.Equal("true", file.Options["recursiveFileLookup"]);
    }

    [Fact]
    public void Reader_OptionOverloads_StoreInvariantStrings()
    {
        using SparkSession spark = NewSession();

        UnresolvedFileRelation file = Assert.IsType<UnresolvedFileRelation>(
            spark.Read
                .Option("mergeSchema", true)
                .Option("recursiveFileLookup", 42L)
                .Option("pathGlobFilter", 1.5d)
                .Option("modifiedBefore", "2020-01-01")
                .Parquet("/p")
                .Plan);

        Assert.Equal("true", file.Options["mergeSchema"]);
        Assert.Equal("42", file.Options["recursiveFileLookup"]);
        Assert.Equal("1.5", file.Options["pathGlobFilter"]);
        Assert.Equal("2020-01-01", file.Options["modifiedBefore"]);
    }

    [Fact]
    public void ParquetScan_RendersAsScanNode_InPlanTree()
    {
        using SparkSession spark = NewSession();

        string tree = spark.Read.Option("mergeSchema", true).Parquet("/data/x.parquet").Plan.TreeString();

        Assert.Contains("UnresolvedRelation parquet", tree);
        Assert.Contains("/data/x.parquet", tree);
    }

    // ---------------- security: rendering must not leak secrets ----------------

    [Fact]
    public void ParquetScan_SimpleString_RendersOptionKeysOnly_NeverValues()
    {
        using SparkSession spark = NewSession();

        LogicalPlan plan = spark.Read.Option("pathGlobFilter", "S3CR3T-OPTION-VALUE").Parquet("/p").Plan;
        string rendered = plan.SimpleString;

        Assert.Contains("options=[pathGlobFilter]", rendered);
        Assert.DoesNotContain("S3CR3T-OPTION-VALUE", rendered);
    }

    [Fact]
    public void ParquetScan_SimpleString_RedactsCredentialBearingPathQuery()
    {
        using SparkSession spark = NewSession();

        LogicalPlan plan = spark.Read.Parquet(
            "wasbs://c@acct.blob.core.windows.net/f.parquet?sig=SUPERSECRETSAS&sp=r").Plan;
        string rendered = plan.SimpleString;

        Assert.DoesNotContain("SUPERSECRETSAS", rendered);
        Assert.Contains("<redacted>", rendered);
    }

    [Fact]
    public void AnalyzingParquetScan_DiagnosticRedactsSecretInPath()
    {
        using SparkSession spark = NewSession();
        LogicalPlan plan = spark.Read.Parquet(
            "s3://bucket/f.parquet?X-Amz-Signature=DEADBEEFSECRET").Plan;

        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => new Analyzer(new LocalCatalog()).Resolve(plan));

        Assert.DoesNotContain("DEADBEEFSECRET", ex.Message);
        Assert.Contains("<redacted>", ex.Message);
    }

    [Fact]
    public void AnalyzingParquetScan_ThrowsUnsupportedDataSource_NamingEpic05()
    {
        using SparkSession spark = NewSession();
        LogicalPlan plan = spark.Read.Parquet("/data/x.parquet").Plan;

        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => new Analyzer(new LocalCatalog()).Resolve(plan));

        Assert.Equal(AnalysisErrorKind.UnsupportedDataSource, ex.Kind);
        Assert.Contains("EPIC-05", ex.Message);
        Assert.Contains("parquet", ex.Message);
        Assert.Contains("/data/x.parquet", ex.Message);
        Assert.Contains("CreateDataFrame", ex.Message);
    }

    // ---------------- AC3: unsupported reader options ----------------

    [Fact]
    public void Parquet_WithUnsupportedOption_ThrowsNamingOptionAndAlternative()
    {
        using SparkSession spark = NewSession();

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => spark.Read.Option("badOption", "x").Parquet("/p"));

        Assert.Contains("badOption", ex.Message);
        Assert.Contains("Schema", ex.Message);
        Assert.Null(ex.ParamName); // no private-var leak: single-arg ArgumentException (DX council nit)
    }

    [Theory]
    [InlineData("mergeSchema")]
    [InlineData("recursiveFileLookup")]
    [InlineData("pathGlobFilter")]
    [InlineData("modifiedBefore")]
    [InlineData("modifiedAfter")]
    [InlineData("datetimeRebaseMode")]
    [InlineData("int96RebaseMode")]
    public void Parquet_WithRecognizedOption_IsAccepted(string key)
    {
        using SparkSession spark = NewSession();

        UnresolvedFileRelation file = Assert.IsType<UnresolvedFileRelation>(
            spark.Read.Option(key, "v").Parquet("/p").Plan);

        Assert.Equal("v", file.Options[key]);
    }

    [Fact]
    public void Parquet_OptionKeys_AreCaseInsensitive()
    {
        using SparkSession spark = NewSession();

        UnresolvedFileRelation file = Assert.IsType<UnresolvedFileRelation>(
            spark.Read.Option("MERGESCHEMA", "true").Parquet("/p").Plan);

        Assert.Equal("true", file.Options["mergeSchema"]);
    }

    // ---------------- lifecycle + argument guards ----------------

    [Fact]
    public void Read_OnStoppedSession_Throws()
    {
        SparkSession spark = NewSession();
        spark.Stop();

        Assert.Throws<SessionStoppedException>(() => spark.Read);
    }

    [Fact]
    public void CreateDataFrame_OnStoppedSession_Throws()
    {
        SparkSession spark = NewSession();
        spark.Stop();

        Assert.Throws<SessionStoppedException>(() => spark.CreateDataFrame(SampleRows(), PeopleSchema));
    }

    [Fact]
    public void Parquet_OnStoppedSession_Throws()
    {
        SparkSession spark = NewSession();
        DataFrameReader reader = spark.Read;
        spark.Stop();

        Assert.Throws<SessionStoppedException>(() => reader.Parquet("/p"));
    }

    [Fact]
    public void CreateDataFrame_Untyped_ThrowsPointingToSchemaOverload()
    {
        using SparkSession spark = NewSession();

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => spark.CreateDataFrame(new[] { 1, 2, 3 }));

        Assert.Contains("schema", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateDataFrame_NullData_Throws()
    {
        using SparkSession spark = NewSession();

        Assert.Throws<ArgumentNullException>(
            () => spark.CreateDataFrame((IEnumerable<Row>)null!, PeopleSchema));
    }

    [Fact]
    public void CreateDataFrame_NullSchema_Throws()
    {
        using SparkSession spark = NewSession();

        Assert.Throws<ArgumentNullException>(() => spark.CreateDataFrame(SampleRows(), null!));
    }

    private static void AssertAttribute(
        AttributeReference attribute, string name, DataType type, bool nullable, long exprId)
    {
        Assert.Equal(name, attribute.Name);
        Assert.Equal(type, attribute.Type);
        Assert.Equal(nullable, attribute.Nullable);
        Assert.Equal(exprId, attribute.ExprId.Value);
    }

    /// <summary>An <see cref="IEnumerable{Row}"/> whose enumerator throws — proving CreateDataFrame and
    /// downstream transformations never iterate the row sequence (AC1 laziness).</summary>
    private sealed class ThrowingRowSequence : IEnumerable<Row>
    {
        public bool Enumerated { get; private set; }

        public IEnumerator<Row> GetEnumerator()
        {
            Enumerated = true;
            throw new InvalidOperationException(
                "CreateDataFrame must not enumerate its rows before an action (lazy invariant).");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
