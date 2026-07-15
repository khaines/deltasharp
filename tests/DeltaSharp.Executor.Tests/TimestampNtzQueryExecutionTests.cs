using System;
using System.Collections.Generic;
using System.Linq;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Functions;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// End-to-end (public API → analyzer → physical translation → execution) coverage for timestamp_ntz
/// query authoring (#558). Core-only tests can prove analysis but cannot execute, so the materialized
/// proofs live here: a <c>date == timestamp_ntz</c> filter returns the analyzer-coerced correct rows
/// (the date operand is cast to timestamp_ntz, so the comparison runs ntz-vs-ntz and never hits the
/// fail-closed mixed-pair kernel guard), and a <c>timestamp_ntz</c> literal survives translation to
/// materialize its wall-clock value in both projection and predicate position.
/// </summary>
[Collection(SessionExecutionTestCollection.Name)]
public sealed class TimestampNtzQueryExecutionTests
{
    private static readonly StructType Schema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField("d", DateType.Instance, nullable: false),
        new StructField("n", TimestampNtzType.Instance, nullable: false),
    });

    private static long EpochMicros(DateTime value) =>
        (value.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;

    private static SparkSession NewSession()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
        return SparkSession.Builder().AppName("timestamp-ntz-e2e").GetOrCreate();
    }

    // id=1/3 have an ntz wall-clock exactly at the date's midnight (date == ntz); id=2 does not.
    private static IReadOnlyList<Row> SampleRows() => new[]
    {
        new Row(Schema, 1, new DateOnly(2024, 6, 15), new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Unspecified)),
        new Row(Schema, 2, new DateOnly(2024, 6, 15), new DateTime(2024, 6, 15, 13, 30, 45, DateTimeKind.Unspecified)),
        new Row(Schema, 3, new DateOnly(2024, 6, 16), new DateTime(2024, 6, 16, 0, 0, 0, DateTimeKind.Unspecified)),
    };

    [Fact]
    public void Filter_DateEqualsTimestampNtz_CoercesAndReturnsMatchingRows()
    {
        // date == timestamp_ntz: the analyzer casts the date operand to timestamp_ntz (midnight wall-clock)
        // and the comparison runs ntz-vs-ntz. Only rows whose ntz value sits exactly at the date's midnight
        // survive — proving the coercion is wired end-to-end AND produces the correct result.
        using SparkSession spark = NewSession();

        IReadOnlyList<Row> result = spark.CreateDataFrame(SampleRows(), Schema)
            .Filter(Col("d").EqualTo(Col("n")))
            .Select(Col("id"))
            .Collect();

        Assert.Equal(new[] { 1, 3 }, result.Select(r => r.GetAs<int>(0)).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Filter_TimestampNtzGreaterThanDate_CoercesReverseOperandOrder()
    {
        // Reverse operand order also coerces (the date operand, now on the right, is cast to ntz). id=2's
        // ntz (13:30:45) is strictly after its date's midnight; ids 1 and 3 sit exactly at midnight (not >).
        using SparkSession spark = NewSession();

        IReadOnlyList<Row> result = spark.CreateDataFrame(SampleRows(), Schema)
            .Filter(Col("n").Gt(Col("d")))
            .Select(Col("id"))
            .Collect();

        Assert.Equal(new[] { 2 }, result.Select(r => r.GetAs<int>(0)).ToArray());
    }

    [Fact]
    public void Select_TimestampNtzLiteral_MaterializesWallClockUnshifted()
    {
        // A timestamp_ntz literal (no public Functions.Lit overload yields ntz, so it is built directly)
        // flows through analysis → physical translation → execution and materializes its wall-clock value,
        // tagged Unspecified (timezone-less), never shifted.
        var wall = new DateTime(2024, 6, 15, 13, 30, 45, DateTimeKind.Unspecified);
        var literalColumn = new Column(Literal.OfTimestampNtz(EpochMicros(wall))).As("lit");
        using SparkSession spark = NewSession();

        IReadOnlyList<Row> result = spark.CreateDataFrame(SampleRows(), Schema)
            .Select(Col("id"), literalColumn)
            .Collect();

        Assert.Equal(3, result.Count);
        foreach (Row row in result)
        {
            DateTime got = row.GetAs<DateTime>("lit");
            Assert.Equal(wall, got);
            Assert.Equal(DateTimeKind.Unspecified, got.Kind);
        }
    }

    [Fact]
    public void Filter_OnTimestampNtzLiteral_ExecutesPredicateAgainstColumn()
    {
        // The literal also drives a predicate end-to-end: n == <wall-clock of id=2> selects exactly id=2,
        // proving the ntz literal survives translation into an executable comparison (not just projection).
        var target = new DateTime(2024, 6, 15, 13, 30, 45, DateTimeKind.Unspecified);
        var literalColumn = new Column(Literal.OfTimestampNtz(EpochMicros(target)));
        using SparkSession spark = NewSession();

        IReadOnlyList<Row> result = spark.CreateDataFrame(SampleRows(), Schema)
            .Filter(Col("n").EqualTo(literalColumn))
            .Select(Col("id"))
            .Collect();

        Assert.Equal(new[] { 2 }, result.Select(r => r.GetAs<int>(0)).ToArray());
    }
}
