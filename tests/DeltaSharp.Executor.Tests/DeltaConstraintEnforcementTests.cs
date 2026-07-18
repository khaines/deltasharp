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

    // Seeds MULTIPLE CHECK constraints in a single v1 metadata commit (AddCheckConstraint hardcodes v1 from v0,
    // so calling it twice would clobber the first constraint; this injects all pairs at once).
    private static void AddCheckConstraints(string table, params (string Name, string Expression)[] constraints)
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

        foreach ((string name, string expression) in constraints)
        {
            configuration[$"delta.constraints.{name}"] = expression;
        }

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

    // A schema whose `amount` field declares a column invariant `amount > 0` (delta.invariants metadata).
    private static readonly StructType InvariantSchema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField(
            "amount", IntegerType.Instance, nullable: true,
            FieldMetadata.FromEntries(new[] { new KeyValuePair<string, string>(
                "delta.invariants", "{\"expression\":{\"expression\":\"amount > 0\"}}") })),
    });

    private static IReadOnlyList<Row> InvariantRows(params int?[] amounts) =>
        amounts.Select((a, i) => new Row(InvariantSchema, i + 1, a)).ToList();

    [Fact]
    public void ColumnInvariant_OnCreate_ViolatingRow_RejectedFailClosed()
    {
        // The write schema declares a column invariant; a fresh create must validate its OWN rows (no prior
        // snapshot). A violating row (and a null, which is not-true) is rejected before any commit.
        string table = Table("invariant-create");
        using SparkSession spark = NewSession();

        Assert.Throws<DeltaConstraintViolationException>(
            () => spark.CreateDataFrame(InvariantRows(10, -5), InvariantSchema)
                .Write.Format("delta").Mode("append").Save(table));
        Assert.False(File.Exists(CommitFile(table, 0)));
    }

    [Fact]
    public void ColumnInvariant_NullValue_RejectedAsNotTrue()
    {
        string table = Table("invariant-null");
        using SparkSession spark = NewSession();

        Assert.Throws<DeltaConstraintViolationException>(
            () => spark.CreateDataFrame(InvariantRows(10, null), InvariantSchema)
                .Write.Format("delta").Mode("append").Save(table));
    }

    [Fact]
    public void ColumnInvariant_SatisfyingRows_Committed()
    {
        string table = Table("invariant-ok");
        using SparkSession spark = NewSession();

        spark.CreateDataFrame(InvariantRows(10, 20), InvariantSchema)
            .Write.Format("delta").Mode("append").Save(table);

        Assert.True(File.Exists(CommitFile(table, 0)));
    }

    [Fact]
    public void OverwriteSchema_SurvivingCheck_ViolatingRow_RejectedFailClosed()
    {
        // #596 Delta parity: overwriteSchema replaces the schema but KEEPS the table's named CHECK constraints,
        // so they are still enforced against the replacement rows — a row violating a surviving CHECK is
        // rejected fail-closed, never committed as unvalidated data into a table that still declares the CHECK.
        string table = Table("os-surviving-check");
        Append(table, Amounts(10, 20)); // v0: {id, amount}
        AddCheckConstraint(table, "positive_id", "id > 0"); // v1: CHECK id > 0 (survives overwriteSchema)

        using SparkSession spark = NewSession();
        // Replace the schema (drop `amount`, keep `id`) with a row that violates the surviving CHECK (-1 !> 0).
        var idOnly = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        DataFrame df = spark.CreateDataFrame(new[] { new Row(idOnly, -1) }, idOnly);

        Assert.Throws<DeltaConstraintViolationException>(
            () => df.Write.Format("delta").Mode("overwrite").Option("overwriteSchema", "true").Save(table));
        Assert.False(File.Exists(CommitFile(table, 2))); // rejected before any commit
    }

    [Fact]
    public void OverwriteSchema_SurvivingCheck_SatisfyingRows_Committed()
    {
        // The dual of the above: an overwriteSchema whose rows satisfy the surviving CHECK commits normally.
        string table = Table("os-surviving-ok");
        Append(table, Amounts(10, 20)); // v0
        AddCheckConstraint(table, "positive_id", "id > 0"); // v1

        using SparkSession spark = NewSession();
        var idOnly = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        spark.CreateDataFrame(new[] { new Row(idOnly, 7) }, idOnly)
            .Write.Format("delta").Mode("overwrite").Option("overwriteSchema", "true").Save(table);

        Assert.True(File.Exists(CommitFile(table, 2))); // the satisfying overwriteSchema committed
    }

    [Fact]
    public void OverwriteSchema_DroppingConstrainedColumn_RejectedFailClosed_NoBrick()
    {
        // #596/#598: an overwriteSchema that DROPS a column a surviving CHECK references must not leave a
        // dangling CHECK (which would then brick every future write). The surviving CHECK cannot resolve against
        // the new schema, so the write is refused fail-closed — and (#598) with a clear Delta-parity
        // DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE error naming the column + constraint, not a raw resolution
        // failure.
        string table = Table("os-drop-constrained");
        Append(table, Amounts(10, 20)); // v0: {id, amount}
        AddCheckConstraint(table, "positive_id", "id > 0"); // v1: CHECK references `id`

        using SparkSession spark = NewSession();
        var amountOnly = new StructType(new[] { new StructField("amount", IntegerType.Instance, nullable: true) });
        DataFrame df = spark.CreateDataFrame(new[] { new Row(amountOnly, 5) }, amountOnly); // drops `id`

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => df.Write.Format("delta").Mode("overwrite").Option("overwriteSchema", "true").Save(table));
        Assert.Equal("id", ex.ColumnName);
        DeltaTableConstraint dependent = Assert.Single(ex.Constraints);
        Assert.Equal("positive_id", dependent.Name);
        Assert.Equal(DeltaConstraintKind.Check, dependent.Kind);
        Assert.Contains("positive_id", ex.Message);
        Assert.Contains("id", ex.Message);
        Assert.Contains(DeltaConstraintDependentColumnException.ErrorClass, ex.Message);
        Assert.False(File.Exists(CommitFile(table, 2))); // no dangling-CHECK commit — table not bricked
    }

    [Fact]
    public void OverwriteSchema_DroppingConstrainedColumn_ParityErrorNamesTheActualColumnAndConstraint()
    {
        // #598: the DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE parity error names the ACTUAL dropped column and
        // dependent constraint (not a hardcoded pair) and echoes the surviving predicate: a CHECK `cap` on
        // `amount` blocks dropping `amount`.
        string table = Table("os-drop-amount");
        Append(table, Amounts(10, 20)); // v0: {id, amount}
        AddCheckConstraint(table, "cap", "amount < 100"); // CHECK references `amount`

        using SparkSession spark = NewSession();
        var idOnly = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        DataFrame df = spark.CreateDataFrame(new[] { new Row(idOnly, 7) }, idOnly); // drops `amount`

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => df.Write.Format("delta").Mode("overwrite").Option("overwriteSchema", "true").Save(table));
        Assert.Equal("amount", ex.ColumnName);
        Assert.Equal("cap", Assert.Single(ex.Constraints).Name);
        Assert.Contains("amount < 100", ex.Message); // the surviving predicate is echoed for actionability
        Assert.False(File.Exists(CommitFile(table, 2))); // no dangling-CHECK commit — table not bricked
    }

    [Fact]
    public void OverwriteSchema_DroppingColumnWithMultipleDependentChecks_ListsAllDeterministically()
    {
        // #598 (council: Architect/Quality): when MULTIPLE surviving CHECK constraints reference the dropped
        // column, the parity error aggregates ALL of them (mirroring Delta's foundViolatingConstraintsForColumn-
        // Change), in the deterministic name-sorted order CollectForWrite guarantees.
        string table = Table("os-drop-multi");
        Append(table, Amounts(10, 20)); // v0: {id, amount}
        AddCheckConstraints(table, ("amount_positive", "amount > 0"), ("amount_cap", "amount < 100")); // both ref `amount`

        using SparkSession spark = NewSession();
        var idOnly = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        DataFrame df = spark.CreateDataFrame(new[] { new Row(idOnly, 7) }, idOnly); // drops `amount`

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => df.Write.Format("delta").Mode("overwrite").Option("overwriteSchema", "true").Save(table));
        Assert.Equal("amount", ex.ColumnName);
        // Ordinal name sort: "amount_cap" < "amount_positive" ('c' < 'p'), regardless of injection order.
        Assert.Equal(new[] { "amount_cap", "amount_positive" }, ex.Constraints.Select(c => c.Name).ToArray());
        Assert.Contains("amount_cap -> amount < 100", ex.Message);
        Assert.Contains("amount_positive -> amount > 0", ex.Message);
        Assert.Contains("these surviving CHECK constraints still depend", ex.Message); // plural wording
        Assert.False(File.Exists(CommitFile(table, 2))); // not bricked
    }

    [Fact]
    public void OverwriteSchema_ChangingConstrainedColumnType_DoesNotReclassifyError()
    {
        // #598 (council: Quality false-positive guard): the reclassification is scoped to UnresolvedColumn (a
        // DROPPED column). RETYPING a constrained column so a surviving CHECK no longer resolves yields a
        // DataTypeMismatch, which must NOT be reclassified as DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE — the
        // write still fails closed, just not with the dependent-column parity error.
        string table = Table("os-retype-constrained");
        Append(table, Amounts(10, 20)); // v0: {id int, amount int}
        AddCheckConstraint(table, "positive_id", "id > 0"); // CHECK references `id`

        using SparkSession spark = NewSession();
        var idRetyped = new StructType(new[]
        {
            new StructField("id", StringType.Instance, nullable: false), // id int -> string (retype, not drop)
            new StructField("amount", IntegerType.Instance, nullable: true),
        });
        DataFrame df = spark.CreateDataFrame(new[] { new Row(idRetyped, "x", 5) }, idRetyped);

        Exception ex = Assert.ThrowsAny<Exception>(
            () => df.Write.Format("delta").Mode("overwrite").Option("overwriteSchema", "true").Save(table));
        Assert.IsNotType<DeltaConstraintDependentColumnException>(ex); // scoped: a retype is not a dropped-column change
        Assert.False(File.Exists(CommitFile(table, 2))); // still fail-closed — no brick
    }

    [Fact]
    public void MergeSchema_AppendAddingConstrainedColumn_EnforcesNewColumnInvariant()
    {
        // A mergeSchema append that ADDS a new column declaring a column invariant must validate the added
        // column's own rows: a violating value for the new column is rejected fail-closed.
        string table = Table("merge-invariant");
        Append(table, Amounts(10, 20)); // v0: {id, amount} (unconstrained)

        using SparkSession spark = NewSession();
        // Evolve by ADDING `extra` (nullable) carrying an invariant `extra > 0`; the row's extra is -5.
        var evolved = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance, nullable: false),
            new StructField("amount", IntegerType.Instance, nullable: true),
            new StructField(
                "extra", IntegerType.Instance, nullable: true,
                FieldMetadata.FromEntries(new[]
                {
                    new KeyValuePair<string, string>("delta.invariants", "{\"expression\":{\"expression\":\"extra > 0\"}}"),
                })),
        });
        DataFrame df = spark.CreateDataFrame(new[] { new Row(evolved, 1, 20, -5) }, evolved);

        Assert.Throws<DeltaConstraintViolationException>(
            () => df.Write.Format("delta").Mode("append").Option("mergeSchema", "true").Save(table));
        Assert.False(File.Exists(CommitFile(table, 1)));
    }
}
