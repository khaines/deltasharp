using System;
using System.Collections.Generic;
using System.Linq;
using DeltaSharp.Analysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Storage;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Tests for #619: the write enforcer's REFERENCE-BASED dependent-column check on a schema REPLACEMENT. When a
/// prior schema is supplied (an <c>overwriteSchema</c> replacement), <see cref="DeltaLocalSink.EnforceCore"/>
/// resolves each surviving CHECK against the prior schema (where it was valid), collects the top-level columns
/// it references, and — for any whose type CHANGED in the new schema — refuses the write with Delta's
/// <c>DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE</c>. This closes the gap where a type-COMPATIBLE retype
/// (<c>int → bigint</c> under <c>id &gt; 0</c>) still type-resolves and would silently enforce on the new type,
/// diverging from Delta (which blocks an <c>ALTER CHANGE COLUMN</c> a constraint depends on regardless of
/// type-compatibility). The check runs BEFORE Phase-1 resolution, so even a type-INCOMPATIBLE retype surfaces
/// as the parity error rather than a raw analyzer <c>DataTypeMismatch</c>. Without a prior schema (the append
/// path, or a direct call omitting it) the pre-#619 resolution-only behavior is preserved.
/// </summary>
public sealed class DeltaSinkRetypeDependencyTests
{
    private static readonly IReadOnlyList<ColumnBatch> NoBatches = Array.Empty<ColumnBatch>();

    private static IReadOnlyList<DeltaTableConstraint> Checks(params (string Name, string Expr)[] checks) =>
        checks.Select(c => new DeltaTableConstraint(DeltaConstraintKind.Check, c.Name, c.Expr)).ToArray();

    private static StructType Schema(params StructField[] fields) => new(fields);

    private static StructField Field(string name, DataType type) => new(name, type, nullable: true);

    private static ColumnBatch SingleIntBatch(StructType schema, int value)
    {
        MutableColumnVector col = ColumnVectors.Create(IntegerType.Instance, 1);
        col.AppendValue(value);
        return new ManagedColumnBatch(schema, new ColumnVector[] { col }, 1);
    }

    [Fact]
    public void EnforceCore_CompatibleRetype_ReferencedColumn_ReclassifiedAsDependentColumnChange()
    {
        // #619 (the core gap): `id int -> bigint` under CHECK `id > 0` STILL type-resolves, so resolution-only
        // detection would silently enforce on bigint. With the prior schema, the reference-based pre-pass
        // detects the retyped dependency and reports the Delta parity error naming `id`.
        var prior = Schema(Field("id", IntegerType.Instance));
        var next = Schema(Field("id", LongType.Instance));

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => DeltaLocalSink.EnforceCore(
                next, Checks(("pos_id", "id > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null, priorSchema: prior));
        Assert.Equal("id", ex.ColumnName);
        Assert.Equal("pos_id", Assert.Single(ex.Constraints).Name);
    }

    [Fact]
    public void EnforceCore_IncompatibleRetype_WithPriorSchema_StaysDataTypeMismatch_DeltaFaithful()
    {
        // `id int -> string` under `id > 0` is an INCOMPATIBLE retype: the predicate no longer type-resolves.
        // Delta runs canChangeDataType FIRST and surfaces a TYPE error (not the dependent-column error) for a
        // disallowed change, so the #619 pre-pass deliberately does NOT reclassify it — it falls through to
        // Phase-1's raw DataTypeMismatch (still fail-closed). Only a COMPATIBLE retype is a dependency block.
        var prior = Schema(Field("id", IntegerType.Instance));
        var next = Schema(Field("id", StringType.Instance));

        var ex = Assert.Throws<AnalysisException>(
            () => DeltaLocalSink.EnforceCore(
                next, Checks(("pos_id", "id > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null, priorSchema: prior));
        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
    }

    [Fact]
    public void EnforceCore_NestedSiblingRetype_NotReclassified_LeafPrecise()
    {
        // #619 leaf-precise (Delta parity: prefix/path-based dependency): retyping an UNREFERENCED sibling field
        // `s.g` (int->bigint) while a CHECK reads only `s.f` (unchanged) is NOT a dependency break — Delta's
        // path-precise check keys on `s.f`, not the whole struct `s`. No reclassification (zero rows → no throw).
        var priorInner = new StructType(new[]
        {
            new StructField("f", IntegerType.Instance, nullable: true),
            new StructField("g", IntegerType.Instance, nullable: true),
        });
        var nextInner = new StructType(new[]
        {
            new StructField("f", IntegerType.Instance, nullable: true),
            new StructField("g", LongType.Instance, nullable: true), // sibling g retyped; f unchanged
        });
        var prior = Schema(new StructField("s", priorInner, nullable: true));
        var next = Schema(new StructField("s", nextInner, nullable: true));

        DeltaLocalSink.EnforceCore(
            next, Checks(("sf_pos", "s.f > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null, priorSchema: prior);
    }

    [Fact]
    public void EnforceCore_NullabilityOnlyChange_NotReclassified()
    {
        // A nullability-only change to a referenced column (same DataType, nullable flag flipped) is NOT a retype:
        // Delta gates the dependency block on the field's dataType inequality only. The pre-pass compares the
        // referenced leaf DataType (nullability is not part of it), so it is not flagged — consistent for both a
        // top-level and a nested reference.
        var priorNonNull = Schema(new StructField("id", IntegerType.Instance, nullable: false));
        var nextNullable = Schema(new StructField("id", IntegerType.Instance, nullable: true));

        DeltaLocalSink.EnforceCore(
            nextNullable, Checks(("pos_id", "id > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null,
            priorSchema: priorNonNull);
    }

    [Fact]
    public void EnforceCore_QualifiedTopLevelDrop_AttributedToBoundColumnNotQualifier()
    {
        // #618/#619 attribution: a surviving CHECK with a QUALIFIED reference `t.y` (phantom table qualifier `t`)
        // whose column `y` is DROPPED is attributed to the bound column `y` — not the qualifier `t`. The pre-pass
        // resolves `t.y` against the prior schema (binding `y`), so the dropped path names `y`, avoiding Phase-1's
        // flattened-string `parts[0]` mis-attribution.
        var prior = Schema(Field("y", IntegerType.Instance));
        var next = Schema(Field("z", IntegerType.Instance)); // y dropped/renamed away

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => DeltaLocalSink.EnforceCore(
                next, Checks(("pos_y", "t.y > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null, priorSchema: prior));
        Assert.Equal("y", ex.ColumnName); // the bound column, not the phantom qualifier `t`
    }

    [Fact]
    public void EnforceCore_OneCheckReferencingTwoRetypedColumns_ReportsFirstDeterministically()
    {
        // A single CHECK referencing TWO compatibly-retyped columns reports the FIRST (by reference order),
        // with that one throw carrying the dependent constraint — a deterministic single-column report.
        var prior = Schema(Field("a", IntegerType.Instance), Field("b", IntegerType.Instance));
        var next = Schema(Field("a", LongType.Instance), Field("b", LongType.Instance));

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => DeltaLocalSink.EnforceCore(
                next, Checks(("ab", "a > 0 AND b > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null, priorSchema: prior));
        Assert.Equal("a", ex.ColumnName); // first referenced retyped column
        Assert.Equal("ab", Assert.Single(ex.Constraints).Name);
    }

    [Fact]
    public void EnforceCore_IncompatibleRetype_WithoutPriorSchema_StaysDataTypeMismatch()
    {
        // GUARD (contract preserved): WITHOUT a prior schema (no retype signal), the same `id int -> string`
        // retype under `id > 0` still surfaces as the comparison's raw DataTypeMismatch — the pre-#619 behavior
        // EnforceCore keeps when it lacks the schema context to attribute the failure to a dependency.
        var next = Schema(Field("id", StringType.Instance));

        var ex = Assert.Throws<AnalysisException>(
            () => DeltaLocalSink.EnforceCore(
                next, Checks(("pos_id", "id > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
    }

    [Fact]
    public void EnforceCore_UnchangedReferencedColumn_NotReclassified_EnforcesPerRow()
    {
        // No false positive: a referenced column with the SAME type in prior and new schema is not a retyped
        // dependency. The pre-pass passes it through and the CHECK enforces per-row — a violating row is rejected
        // as an ordinary constraint violation, NOT a dependent-column change.
        var prior = Schema(Field("id", IntegerType.Instance));
        var next = Schema(Field("id", IntegerType.Instance));

        var ex = Assert.Throws<DeltaConstraintViolationException>(
            () => DeltaLocalSink.EnforceCore(
                next, Checks(("pos_id", "id > 0")), new[] { SingleIntBatch(next, -1) },
                AnsiMode.Ansi, memoryBudgetBytes: null, priorSchema: prior));
        Assert.Equal("pos_id", ex.Constraint.Name);
    }

    [Fact]
    public void EnforceCore_UnreferencedColumnRetype_NotReclassified()
    {
        // Only columns a CHECK actually REFERENCES matter: retyping an UNREFERENCED column (`other` int->string)
        // while the CHECK `id > 0` reads only the unchanged `id` is not a dependency break — no reclassification,
        // and (zero rows) no violation either.
        var prior = Schema(Field("id", IntegerType.Instance), Field("other", IntegerType.Instance));
        var next = Schema(Field("id", IntegerType.Instance), Field("other", StringType.Instance));

        DeltaLocalSink.EnforceCore(
            next, Checks(("pos_id", "id > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null, priorSchema: prior);
    }

    [Fact]
    public void EnforceCore_NestedStructFieldRetype_ReclassifiedUnderTopLevelColumn()
    {
        // #619 for a nested reference: retyping struct `s`'s field `f` (int -> bigint) changes the TOP-LEVEL
        // column `s`'s type, so a CHECK `s.f > 0` is reported against `s` (Delta names the top-level column).
        var priorInner = new StructType(new[] { new StructField("f", IntegerType.Instance, nullable: true) });
        var nextInner = new StructType(new[] { new StructField("f", LongType.Instance, nullable: true) });
        var prior = Schema(new StructField("s", priorInner, nullable: true));
        var next = Schema(new StructField("s", nextInner, nullable: true));

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => DeltaLocalSink.EnforceCore(
                next, Checks(("sf_pos", "s.f > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null, priorSchema: prior));
        Assert.Equal("s", ex.ColumnName);
    }

    [Fact]
    public void EnforceCore_MultipleChecksOnRetypedColumn_Aggregated()
    {
        // Multiple CHECKs referencing the retyped column aggregate under the one column, in supplied order.
        var prior = Schema(Field("amount", IntegerType.Instance));
        var next = Schema(Field("amount", LongType.Instance));

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => DeltaLocalSink.EnforceCore(
                next, Checks(("amt_pos", "amount > 0"), ("amt_cap", "amount < 100")),
                NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null, priorSchema: prior));
        Assert.Equal("amount", ex.ColumnName);
        Assert.Equal(new[] { "amt_pos", "amt_cap" }, ex.Constraints.Select(c => c.Name).ToArray());
    }
}
