using System.Collections.Generic;
using DeltaSharp.Analysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Tests for #595: per-row enforcement of a nested STRUCT-field column invariant (an all-struct path such as
/// <c>s.f</c> / <c>s.a.b</c>). The invariant predicate references the field's fully-qualified path and resolves
/// via <c>GetStructField</c> (#580), evaluating through the nested <c>StructFieldEvaluator</c> (#589); a row is
/// rejected fail-closed when it does not evaluate to <c>true</c> (Delta's <c>CheckDeltaInvariant.assertRule</c>).
/// These exercise the enforcer directly (<see cref="DeltaLocalSink.EnforceCore"/>); end-to-end enforcement
/// through the public write door awaits nested-column writes (#571), which reject a nested column at
/// row-encode before enforcement runs.
/// </summary>
public sealed class DeltaNestedInvariantEnforcementTests
{
    private static readonly StructType Inner =
        new(new[] { new StructField("f", IntegerType.Instance, nullable: false) });

    private static readonly StructType Schema =
        new(new[] { new StructField("s", Inner, nullable: true) });

    // Builds a batch with a single struct column `s{f:int}`; a null value in `values` marks a NULL struct row.
    private static ColumnBatch StructBatch(params int?[] fValues)
    {
        MutableColumnVector fCol = ColumnVectors.Create(IntegerType.Instance, fValues.Length);
        var structNulls = new bool[fValues.Length];
        for (int i = 0; i < fValues.Length; i++)
        {
            if (fValues[i] is { } v)
            {
                fCol.AppendValue(v);
            }
            else
            {
                fCol.AppendValue(0); // child slot is irrelevant at a null struct row
                structNulls[i] = true;
            }
        }

        var sCol = new StructColumnVector(Inner, new ColumnVector[] { fCol }, structNulls);
        return new ManagedColumnBatch(Schema, new ColumnVector[] { sCol }, fValues.Length);
    }

    private static IReadOnlyList<DeltaTableConstraint> Invariant(string name, string expression) =>
        new[] { new DeltaTableConstraint(DeltaConstraintKind.Invariant, name, expression) };

    [Fact]
    public void EnforceCore_NestedStructFieldInvariant_RejectsViolatingRow()
    {
        // `s.f > 0` over rows [5, -1]: row 1 violates -> rejected fail-closed, message names the qualified path.
        var ex = Assert.Throws<DeltaConstraintViolationException>(
            () => DeltaLocalSink.EnforceCore(
                Schema, Invariant("s.f", "s.f > 0"), new[] { StructBatch(5, -1) }, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal("s.f", ex.Constraint.Name);
        Assert.Equal(DeltaConstraintKind.Invariant, ex.Constraint.Kind);
        Assert.Contains("s.f", ex.Message);
    }

    [Fact]
    public void EnforceCore_NestedStructFieldInvariant_PassesSatisfyingRows()
    {
        // `s.f > 0` over rows [5, 10]: all satisfy -> no throw.
        DeltaLocalSink.EnforceCore(
            Schema, Invariant("s.f", "s.f > 0"), new[] { StructBatch(5, 10) }, AnsiMode.Ansi, memoryBudgetBytes: null);
    }

    [Fact]
    public void EnforceCore_DeeplyNestedStructFieldInvariant_RejectsViolatingRow()
    {
        // `s.a.b > 0` over a two-level struct; row 0 = 3 (ok), row 1 = -2 (violates).
        var b = new StructType(new[] { new StructField("b", IntegerType.Instance, nullable: false) });
        var a = new StructType(new[] { new StructField("a", b, nullable: false) });
        var schema = new StructType(new[] { new StructField("s", a, nullable: false) });

        MutableColumnVector bCol = ColumnVectors.Create(IntegerType.Instance, 2);
        bCol.AppendValue(3);
        bCol.AppendValue(-2);
        var aCol = new StructColumnVector(b, new ColumnVector[] { bCol });
        var sCol = new StructColumnVector(a, new ColumnVector[] { aCol });
        ColumnBatch batch = new ManagedColumnBatch(schema, new ColumnVector[] { sCol }, 2);

        var ex = Assert.Throws<DeltaConstraintViolationException>(
            () => DeltaLocalSink.EnforceCore(
                schema, Invariant("s.a.b", "s.a.b > 0"), new[] { batch }, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal("s.a.b", ex.Constraint.Name);
    }

    [Fact]
    public void EnforceCore_NullStructRow_RejectedByAssertRule()
    {
        // A NULL struct row makes `s.f` null, so `s.f > 0` is null -> NOT TRUE -> rejected, matching Delta's
        // CheckDeltaInvariant.assertRule (a null result rejects). Pinned so the behavior is a conscious choice.
        var ex = Assert.Throws<DeltaConstraintViolationException>(
            () => DeltaLocalSink.EnforceCore(
                Schema, Invariant("s.f", "s.f > 0"), new[] { StructBatch(5, null) }, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal("s.f", ex.Constraint.Name);
    }

    [Fact]
    public void EnforceCore_UnqualifiedLeafExpression_FailsClosed()
    {
        // #595 (council: Quality): an invariant whose expression uses a bare LEAF name (`f`) instead of the
        // qualified path (`s.f`) does not resolve against the write schema (there is no top-level `f`), so it
        // fails CLOSED with an AnalysisException — never silently passes a misconfigured/legacy leaf expression.
        var ex = Assert.Throws<AnalysisException>(
            () => DeltaLocalSink.EnforceCore(
                Schema, Invariant("s.f", "f > 0"), new[] { StructBatch(5) }, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Contains("resolve column", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnforceCore_TopLevelAndNestedInvariants_NestedIsEnforcedAlongside()
    {
        // #595 (council: Quality): a top-level invariant and a nested one enforced in the same pass do not
        // cross-pollinate — with both `id > 0` and `s.f > 0` active and id satisfied, a violating `s.f` is still
        // caught (proving the nested invariant is genuinely enforced beside the top-level one).
        var inner = new StructType(new[] { new StructField("f", IntegerType.Instance, nullable: false) });
        var schema = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance, nullable: false),
            new StructField("s", inner, nullable: false),
        });
        MutableColumnVector idCol = ColumnVectors.Create(IntegerType.Instance, 2);
        idCol.AppendValue(10);
        idCol.AppendValue(10);
        MutableColumnVector fCol = ColumnVectors.Create(IntegerType.Instance, 2);
        fCol.AppendValue(3);
        fCol.AppendValue(-1); // row 1 violates s.f > 0 (id is fine on both rows)
        var sCol = new StructColumnVector(inner, new ColumnVector[] { fCol });
        ColumnBatch batch = new ManagedColumnBatch(schema, new ColumnVector[] { idCol, sCol }, 2);

        var constraints = new[]
        {
            new DeltaTableConstraint(DeltaConstraintKind.Invariant, "id", "id > 0"),
            new DeltaTableConstraint(DeltaConstraintKind.Invariant, "s.f", "s.f > 0"),
        };
        var ex = Assert.Throws<DeltaConstraintViolationException>(
            () => DeltaLocalSink.EnforceCore(schema, constraints, new[] { batch }, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal("s.f", ex.Constraint.Name);
    }

    [Fact]
    public void EnforceCore_CrossFieldNestedInvariant_Enforced()
    {
        // #595 (council: Quality): an invariant referencing TWO nested fields (`s.a > s.b`) resolves + enforces —
        // row 1 (a=1, b=4) violates and is rejected.
        var inner = new StructType(new[]
        {
            new StructField("a", IntegerType.Instance, nullable: false),
            new StructField("b", IntegerType.Instance, nullable: false),
        });
        var schema = new StructType(new[] { new StructField("s", inner, nullable: false) });
        MutableColumnVector aCol = ColumnVectors.Create(IntegerType.Instance, 2);
        aCol.AppendValue(5);
        aCol.AppendValue(1);
        MutableColumnVector bCol = ColumnVectors.Create(IntegerType.Instance, 2);
        bCol.AppendValue(3);
        bCol.AppendValue(4);
        var sCol = new StructColumnVector(inner, new ColumnVector[] { aCol, bCol });
        ColumnBatch batch = new ManagedColumnBatch(schema, new ColumnVector[] { sCol }, 2);

        var ex = Assert.Throws<DeltaConstraintViolationException>(
            () => DeltaLocalSink.EnforceCore(
                schema, Invariant("s", "s.a > s.b"), new[] { batch }, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal("s", ex.Constraint.Name);
    }
}
