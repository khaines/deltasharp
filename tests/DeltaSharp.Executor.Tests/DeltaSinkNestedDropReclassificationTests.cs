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
/// Tests for #600: a surviving CHECK that reads a NESTED field (`s.f`) whose STRUCTURE the write schema no
/// longer provides — the field was dropped/renamed, or the base column was retyped away from a struct — now
/// surfaces Delta's <c>DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE</c> parity error naming the top-level column,
/// instead of the raw analyzer failure. Before #600 the analyzer threw <see cref="AnalysisErrorKind.DataTypeMismatch"/>
/// for a missing/typeless struct field, which the #598/#599 reclassifier (scoped to
/// <see cref="AnalysisErrorKind.UnresolvedColumn"/>) skipped — leaking a bare "cannot resolve" error. #600 splits
/// the STRUCTURAL absence of a struct field into <see cref="AnalysisErrorKind.UnresolvedStructField"/> so the
/// reclassifier catches it and normalises `s.f` → top-level `s`. A genuine top-level operand RETYPE (e.g.
/// <c>id int → string</c> under <c>id &gt; 0</c>) still throws <see cref="AnalysisErrorKind.DataTypeMismatch"/> from
/// the COMPARISON — not from nested-field extraction — and stays fail-closed but NOT reclassified. The enforcer
/// (<see cref="DeltaLocalSink.EnforceCore"/>) is exercised directly: the reclassification is a Phase-1 resolution
/// decision (row-count-independent), and a nested struct column cannot be persisted to establish a v0 baseline
/// (nested Parquet write stays scalar-only), so this is the same layer #595's nested tests use.
/// </summary>
public sealed class DeltaSinkNestedDropReclassificationTests
{
    // Resolution throws in Phase 1 (before any row is evaluated), so the reclassification cases need no batch.
    private static readonly IReadOnlyList<ColumnBatch> NoBatches = Array.Empty<ColumnBatch>();

    private static IReadOnlyList<DeltaTableConstraint> Checks(params (string Name, string Expr)[] checks) =>
        checks.Select(c => new DeltaTableConstraint(DeltaConstraintKind.Check, c.Name, c.Expr)).ToArray();

    [Fact]
    public void EnforceCore_NestedFieldDropped_StructSurvives_ReclassifiedAsDependentColumnChange()
    {
        // The struct column `s` survives, but its field `f` was renamed away (schema now has `s{g:int}`), so a
        // surviving CHECK `s.f > 0` no longer resolves — "no such struct field 'f'". #600: this is a STRUCTURAL
        // absence (UnresolvedStructField), reclassified to the dependent-column parity error naming top-level `s`.
        var survivingStruct = new StructType(new[] { new StructField("g", IntegerType.Instance, nullable: false) });
        var schema = new StructType(new[] { new StructField("s", survivingStruct, nullable: true) });

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => DeltaLocalSink.EnforceCore(
                schema, Checks(("chk_sf", "s.f > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal("s", ex.ColumnName); // normalised from the nested reference `s.f`
        Assert.Equal("chk_sf", Assert.Single(ex.Constraints).Name);
        Assert.Contains("s.f > 0", ex.Message); // the surviving predicate is echoed for actionability
    }

    [Fact]
    public void EnforceCore_StructBaseRetypedToScalar_ReclassifiedAsDependentColumnChange()
    {
        // The base column `s` was retyped from a struct to a scalar (int), so `s.f` cannot extract a field from a
        // non-struct — "a nested field reference requires a struct". #600: also a STRUCTURAL absence
        // (UnresolvedStructField), reclassified to the dependent-column parity error naming top-level `s`.
        var schema = new StructType(new[] { new StructField("s", IntegerType.Instance, nullable: false) });

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => DeltaLocalSink.EnforceCore(
                schema, Checks(("chk_sf", "s.f > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal("s", ex.ColumnName);
        Assert.Equal("chk_sf", Assert.Single(ex.Constraints).Name);
    }

    [Fact]
    public void EnforceCore_MultipleChecksOnDroppedNestedField_AggregatedUnderTopLevelColumn()
    {
        // Multiple surviving CHECKs reading the dropped nested field `s.f` aggregate under the ONE top-level
        // column `s` (mirroring Delta's foundViolatingConstraintsForColumnChange). EnforceCore's Phase-1
        // aggregation APPENDS in iteration order, so the reported list preserves the INPUT order it is handed —
        // proven here by supplying them in non-alphabetical order (`sf_pos` before `sf_cap`) and getting that
        // SAME order back (not re-sorted). Upstream, CollectForWrite supplies the set in deterministic
        // Kind-then-name order; this test pins the aggregation's order-preservation, decoupled from that source.
        var survivingStruct = new StructType(new[] { new StructField("g", IntegerType.Instance, nullable: false) });
        var schema = new StructType(new[] { new StructField("s", survivingStruct, nullable: true) });

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => DeltaLocalSink.EnforceCore(
                schema,
                Checks(("sf_pos", "s.f > 0"), ("sf_cap", "s.f < 100")),
                NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal("s", ex.ColumnName);
        Assert.Equal(new[] { "sf_pos", "sf_cap" }, ex.Constraints.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void EnforceCore_NestedBaseColumnCompletelyDropped_ReclassifiedAsDependentColumnChange()
    {
        // The base struct column `s` was dropped ENTIRELY (the write schema has neither `s` nor a top-level
        // `f`), so the nested reference `s.f` binds NO base column and the analyzer's trailing-part fallback
        // throws UnresolvedColumn (naming the full path `s.f`) — the pre-#600 kind the reclassifier already
        // caught. Pinned here so BOTH the fully-dropped-base path (UnresolvedColumn) and the surviving-struct
        // path (#600's UnresolvedStructField) are proven to normalise `s.f` → top-level `s` for the parity error.
        var schema = new StructType(new[] { new StructField("other", IntegerType.Instance, nullable: false) });

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => DeltaLocalSink.EnforceCore(
                schema, Checks(("chk_sf", "s.f > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal("s", ex.ColumnName);
        Assert.Equal("chk_sf", Assert.Single(ex.Constraints).Name);
    }

    [Fact]
    public void EnforceCore_QualifiedNestedFieldDrop_AttributedToBoundBaseNotQualifier()
    {
        // #618: a surviving CHECK with a QUALIFIED nested ref `t.s.f` (phantom table qualifier `t`, real base
        // struct `s` whose field `f` was dropped) is attributed to the bound base column `s` — NOT the
        // qualifier `t` that a first-dot split of `t.s.f` would wrongly report. The analyzer's structured
        // RootColumn (the bound base) feeds the per-column aggregation instead of the flattened-string split.
        var survivingStruct = new StructType(new[] { new StructField("g", IntegerType.Instance, nullable: false) });
        var schema = new StructType(new[] { new StructField("s", survivingStruct, nullable: true) });

        var ex = Assert.Throws<DeltaConstraintDependentColumnException>(
            () => DeltaLocalSink.EnforceCore(
                schema, Checks(("chk_tsf", "t.s.f > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal("s", ex.ColumnName); // bound base, not the phantom qualifier `t`
        Assert.Equal("chk_tsf", Assert.Single(ex.Constraints).Name);
    }

    [Fact]
    public void EnforceCore_TopLevelRetypeUnderComparison_NotReclassified_StaysDataTypeMismatch()
    {
        // GUARD (false-positive scope): retyping a TOP-LEVEL column so a surviving CHECK's COMPARISON no longer
        // typechecks (`id > 0` with `id` now a string) throws DataTypeMismatch from the comparison operator — NOT
        // from nested-field extraction — so #600's UnresolvedStructField split must NOT catch it. The write still
        // fails closed, just not with the dependent-column parity error (a genuine type error, not a dropped dep).
        var schema = new StructType(new[] { new StructField("id", StringType.Instance, nullable: false) });

        var ex = Assert.Throws<AnalysisException>(
            () => DeltaLocalSink.EnforceCore(
                schema, Checks(("pos_id", "id > 0")), NoBatches, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
        Assert.IsNotType<DeltaConstraintDependentColumnException>(ex);
    }

    [Fact]
    public void EnforceCore_PresentNestedField_ResolvesAndEnforces_NotReclassified()
    {
        // POSITIVE CONTROL: when the nested field is PRESENT, the CHECK resolves through GetStructField and is
        // genuinely enforced per-row — a violating row is rejected as an ordinary constraint violation, NOT
        // reclassified. Proves #600 did not turn a resolvable nested CHECK into a spurious dependent-column error.
        var inner = new StructType(new[] { new StructField("f", IntegerType.Instance, nullable: false) });
        var schema = new StructType(new[] { new StructField("s", inner, nullable: false) });

        MutableColumnVector fCol = ColumnVectors.Create(IntegerType.Instance, 2);
        fCol.AppendValue(5);
        fCol.AppendValue(-1); // row 1 violates s.f > 0
        var sCol = new StructColumnVector(inner, new ColumnVector[] { fCol });
        ColumnBatch batch = new ManagedColumnBatch(schema, new ColumnVector[] { sCol }, 2);

        var ex = Assert.Throws<DeltaConstraintViolationException>(
            () => DeltaLocalSink.EnforceCore(
                schema, Checks(("chk_sf", "s.f > 0")), new[] { batch }, AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Equal("chk_sf", ex.Constraint.Name);
        Assert.Equal(DeltaConstraintKind.Check, ex.Constraint.Kind);
    }
}
