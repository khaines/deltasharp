using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using DeltaSharp.Storage;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// End-to-end tests for per-row Delta constraint enforcement at the write door (#581): a CHECK constraint
/// (or column invariant) on the target table is resolved and evaluated over each write batch, and the write
/// is rejected fail-closed — before any Parquet file is staged or log commit is attempted — when a row's
/// predicate does not evaluate to <c>true</c> (i.e. <c>false</c> OR <c>null</c>, matching Delta's
/// <c>CheckDeltaInvariant.assertRule</c>). The constraint is seeded by injecting
/// <c>delta.constraints.&lt;name&gt;</c> into the table's <c>metaData.configuration</c> via a follow-up
/// metadata commit, then writing through the public <c>DataFrame.Write</c> door.
/// </summary>
[Collection(SessionExecutionTestCollection.Name)]
public sealed class DeltaConstraintEnforcementTests : IDisposable
{
    private static readonly StructType AmountSchema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField("amount", IntegerType.Instance, nullable: true),
    });

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "delta-constraint-" + Guid.NewGuid().ToString("N"));

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
        return SparkSession.Builder().AppName("delta-constraint-e2e").GetOrCreate();
    }

    private static IReadOnlyList<Row> Amounts(params int?[] amounts) =>
        amounts.Select((a, i) => new Row(AmountSchema, i + 1, a)).ToList();

    private static string CommitFile(string table, long version) =>
        Path.Combine(table, "_delta_log", $"{version:D20}.json");

    // Seed a CHECK constraint by injecting delta.constraints.<name> into the table's metaData.configuration
    // via a metadata-only follow-up commit (v1) that reuses v0's exact schemaString/protocol.
    private static void AddCheckConstraint(string table, string name, string expression)
    {
        string logDir = Path.Combine(table, "_delta_log");
        string metaLine = File.ReadAllLines(CommitFile(table, 0))
            .First(line => line.Contains("\"metaData\"", StringComparison.Ordinal));
        JsonNode root = JsonNode.Parse(metaLine)!;
        JsonObject metadata = root["metaData"]!.AsObject();
        if (metadata["configuration"] is not JsonObject configuration)
        {
            configuration = new JsonObject();
            metadata["configuration"] = configuration;
        }

        configuration[$"delta.constraints.{name}"] = expression;
        File.WriteAllText(Path.Combine(logDir, $"{1:D20}.json"), root.ToJsonString() + "\n");
    }

    private static void Append(string table, IReadOnlyList<Row> rows)
    {
        using SparkSession spark = NewSession();
        spark.CreateDataFrame(rows, AmountSchema).Write.Format("delta").Mode("append").Save(table);
    }

    private static void SeedConstrainedTable(string table, string expression)
    {
        Append(table, Amounts(10, 20)); // v0: satisfies amount > 0
        AddCheckConstraint(table, "positive_amount", expression); // v1: adds the constraint
    }

    [Fact]
    public void CheckConstraint_ViolatingRow_RejectedFailClosed_NoCommit()
    {
        string table = Table("violating");
        SeedConstrainedTable(table, "amount > 0");

        DeltaConstraintViolationException ex = Assert.Throws<DeltaConstraintViolationException>(
            () => Append(table, Amounts(5, -5, 7)));

        Assert.Equal(DeltaConstraintKind.Check, ex.Constraint.Kind);
        Assert.Equal("positive_amount", ex.Constraint.Name);
        Assert.False(File.Exists(CommitFile(table, 2))); // rejected before any commit
    }

    [Fact]
    public void CheckConstraint_NullValue_RejectedAsNotTrue()
    {
        // amount > 0 evaluates to NULL for a null amount; Delta rejects a row that is not TRUE (null OR false).
        string table = Table("null-amount");
        SeedConstrainedTable(table, "amount > 0");

        Assert.Throws<DeltaConstraintViolationException>(() => Append(table, Amounts(3, null)));
        Assert.False(File.Exists(CommitFile(table, 2)));
    }

    [Fact]
    public void CheckConstraint_SatisfyingRows_Committed()
    {
        string table = Table("satisfying");
        SeedConstrainedTable(table, "amount > 0");

        Append(table, Amounts(1, 100, 42));

        Assert.True(File.Exists(CommitFile(table, 2))); // the satisfying append committed
    }

    [Fact]
    public void CompoundCheckConstraint_EnforcesEveryConjunct()
    {
        string table = Table("compound");
        SeedConstrainedTable(table, "amount >= 0 AND amount < 100");

        Assert.Throws<DeltaConstraintViolationException>(() => Append(table, Amounts(50, 150))); // 150 !< 100
        Append(table, Amounts(0, 99)); // both in range
        Assert.True(File.Exists(CommitFile(table, 2)));
    }

    [Fact]
    public void NoConstraint_Table_AppendsFreely()
    {
        string table = Table("unconstrained");
        Append(table, Amounts(10)); // v0, no constraint

        Append(table, Amounts(-5, -99)); // would violate a constraint, but none is declared

        Assert.True(File.Exists(CommitFile(table, 1)));
    }

    [Fact]
    public void CheckConstraint_Overwrite_AlsoEnforced()
    {
        string table = Table("overwrite");
        SeedConstrainedTable(table, "amount > 0");

        using SparkSession spark = NewSession();
        DataFrame violating = spark.CreateDataFrame(Amounts(-1), AmountSchema);

        Assert.Throws<DeltaConstraintViolationException>(
            () => violating.Write.Format("delta").Mode("overwrite").Save(table));
        Assert.False(File.Exists(CommitFile(table, 2)));
    }
}
