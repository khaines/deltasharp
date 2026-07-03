using DeltaSharp;
using DeltaSharp.Types;

// Minimal getting-started sample. It confirms that a .NET 8 (current LTS) application can
// reference the public DeltaSharp.Core surface and call into it, and it demonstrates the
// STORY-04.1.2 (#158) read door: building lazy DataFrame plans from local sources.
//
// `System` is available via ImplicitUsings (Directory.Build.props), so `Console` needs no
// explicit using.
Console.WriteLine($"{DeltaSharpInfo.Product} {DeltaSharpInfo.Version}");

using SparkSession spark = SparkSession.Builder()
    .AppName("getting-started")
    .GetOrCreate();

// 1) Create a DataFrame from an in-memory sequence with an explicit schema. This is a
//    transformation: it builds a scan (LocalRelation) plan and materializes NO rows until an
//    action runs (DeltaSharp's central lazy/eager invariant, ADR-0001).
var peopleSchema = new StructType(new[]
{
    new StructField("id", LongType.Instance, nullable: false),
    new StructField("name", StringType.Instance, nullable: true),
    new StructField("age", IntegerType.Instance, nullable: true),
});
var people = spark.CreateDataFrame(
    new[]
    {
        new Row(peopleSchema, 1L, "alice", 30),
        new Row(peopleSchema, 2L, "bob", 25),
    },
    peopleSchema);

// Transformations chain lazily over the scan plan — still no rows read.
DataFrame adults = people.Filter(Functions.Col("age").Geq(18)).Select("id", "name");
Console.WriteLine("Built a lazy in-memory DataFrame plan (no rows materialized).");

// 2) Open the read door for a local Parquet file. This builds an UNRESOLVED Parquet scan and
//    opens NO file; the Parquet reader itself ships in EPIC-05 (Delta/Parquet storage), so an
//    action over this frame reports a deterministic diagnostic naming EPIC-05.
DataFrame parquet = spark.Read
    .Option("mergeSchema", true)
    .Schema(peopleSchema)
    .Parquet("data/people.parquet");
Console.WriteLine("Built a lazy Parquet scan plan (no file opened).");

// NOTE: This sample references DeltaSharp.Core only, which cannot execute a query (the execution
// engine lives in DeltaSharp.Executor). Calling an action such as adults.Collect() here would
// throw QueryExecutionException; end-to-end execution of the in-memory source is exercised by the
// Executor test project. See docs/engineering/design/read-door.md.
_ = adults;
_ = parquet;
