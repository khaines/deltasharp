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
    public void EnforceCore_IncompatibleRetype_ReferencedColumn_ReclassifiedNotRawDataTypeMismatch()
    {
        // `id int -> string` under `id > 0`: without a prior schema this throws a raw comparison DataTypeMismatch
        // (see the guard below). WITH a prior schema the pre-pass runs FIRST and reports the dependent-column
        // parity error naming `id`, never reaching the comparison typecheck.
        var prior = Schema(Field("id", IntegerType.Instance));
        var next = Schema(Field("id", StringType.Instance));

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => DeltaLocalSink.EnforceCore(
                next, Checks(("pos_id", "id > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null, priorSchema: prior));
        Assert.Equal("id", ex.ColumnName);
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
