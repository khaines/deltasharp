using System;
using DeltaSharp.Analysis;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Analysis;

// #580: the analyzer resolves a multipart reference `s.f` to a GetStructField over the struct column `s`
// (nested field access), taking precedence over the pre-#580 trailing-part binding that silently bound a
// top-level `f`. A non-struct leading part or an absent field fails closed; a non-column leading part falls
// back to the M1 qualifier (trailing-part) binding.
public sealed class GetStructFieldResolutionTests
{
    private static readonly StructType InnerStruct =
        new(new[] { new StructField("f", IntegerType.Instance, nullable: true) });

    private static readonly StructType SchemaWithStructAndTopLevelF = new(new[]
    {
        new StructField("s", InnerStruct, nullable: true),
        new StructField("f", IntegerType.Instance, nullable: true),
        new StructField("id", LongType.Instance, nullable: false),
    });

    private static Expression ResolveCondition(StructType schema, Expression condition)
    {
        var catalog = new LocalCatalog();
        catalog.Register("t", schema);
        var plan = new Filter(condition, new UnresolvedRelation(new[] { "t" }));
        var resolved = (Filter)new Analyzer(catalog).Resolve(plan);
        return resolved.Condition;
    }

    private static Expression Ref(params string[] parts) => new UnresolvedAttribute(parts);

    [Fact]
    public void MultipartRef_ResolvesToGetStructField_OverStructColumn_NotTopLevelColumn()
    {
        Expression cond = ResolveCondition(
            SchemaWithStructAndTopLevelF,
            new BinaryComparison(Ref("s", "f"), Literal.OfInt(0), ComparisonOperator.GreaterThan));

        var comparison = Assert.IsType<BinaryComparison>(cond);
        var field = Assert.IsType<GetStructField>(comparison.Left);
        Assert.Equal("f", field.FieldName);
        Assert.Equal(0, field.Ordinal);
        Assert.Equal(IntegerType.Instance, field.Type);
        // The child is the STRUCT column `s`, NOT the top-level column `f` (the pre-#580 silent mis-bind).
        var structColumn = Assert.IsType<AttributeReference>(field.Child);
        Assert.Equal("s", structColumn.Name);
    }

    [Fact]
    public void MultipartRef_FieldMatchIsCaseInsensitive()
    {
        Expression cond = ResolveCondition(
            SchemaWithStructAndTopLevelF,
            new BinaryComparison(Ref("S", "F"), Literal.OfInt(0), ComparisonOperator.GreaterThan));

        var field = Assert.IsType<GetStructField>(Assert.IsType<BinaryComparison>(cond).Left);
        Assert.Equal("f", field.FieldName); // resolved to the actual (declared-case) field name
        Assert.Equal(IntegerType.Instance, field.Type);
    }

    [Fact]
    public void MultipartRef_NonStructLeadingColumn_FailsClosed()
    {
        // #600: extracting a field from a NON-struct base is a structural absence — UnresolvedStructField, not a
        // generic DataTypeMismatch — so a survivor-CHECK reclassifier can attribute it to the top-level column.
        var ex = Assert.Throws<AnalysisException>(() => ResolveCondition(
            SchemaWithStructAndTopLevelF,
            new BinaryComparison(Ref("id", "f"), Literal.OfInt(0), ComparisonOperator.GreaterThan)));
        Assert.Equal(AnalysisErrorKind.UnresolvedStructField, ex.Kind);
        Assert.Equal("id.f", ex.Reference); // full nested path, so callers can normalise to top-level `id`
    }

    [Fact]
    public void MultipartRef_UnknownStructField_FailsClosed()
    {
        // #600: a struct with no such field is likewise UnresolvedStructField, carrying the full `s.nope` path.
        var ex = Assert.Throws<AnalysisException>(() => ResolveCondition(
            SchemaWithStructAndTopLevelF,
            new BinaryComparison(Ref("s", "nope"), Literal.OfInt(0), ComparisonOperator.GreaterThan)));
        Assert.Equal(AnalysisErrorKind.UnresolvedStructField, ex.Kind);
        Assert.Equal("s.nope", ex.Reference);
    }

    [Fact]
    public void MultipartRef_AmbiguousStructField_StaysDataTypeMismatch()
    {
        // #600 GUARD (taxonomy boundary): a struct with two case-insensitively-equal fields (`f`, `F`) makes
        // `s.f` AMBIGUOUS — the path DOES resolve to a struct and the field DOES exist (twice), so this is a
        // genuine under-specified reference, NOT a structural absence. It must stay DataTypeMismatch (never
        // UnresolvedStructField), so the survivor-CHECK reclassifier does NOT treat it as a dropped dependency.
        // Locks in the asymmetry vs the non-struct-base / no-such-field cases the #600 split newly reclassifies.
        var ambiguousStruct = new StructType(new[]
        {
            new StructField("f", IntegerType.Instance, nullable: true),
            new StructField("F", IntegerType.Instance, nullable: true),
        });
        var schema = new StructType(new[] { new StructField("s", ambiguousStruct, nullable: true) });

        var ex = Assert.Throws<AnalysisException>(() => ResolveCondition(
            schema,
            new BinaryComparison(Ref("s", "f"), Literal.OfInt(0), ComparisonOperator.GreaterThan)));
        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
    }

    [Fact]
    public void MultipartRef_QualifiedNestedDrop_RootColumnIsBoundBaseNotQualifier()
    {
        // #618: for a qualified nested ref `t.s.f` (phantom qualifier `t` skipped by the base scan, real base
        // struct `s` lacking field `f`), the UnresolvedStructField carries RootColumn = the BOUND BASE column
        // `s` — not the qualifier `t` a first-dot split of `t.s.f` would wrongly pick — so a dependent-column
        // reclassifier attributes the failure to `s`. Reference stays the full path for the message.
        var innerNoF = new StructType(new[] { new StructField("g", IntegerType.Instance, nullable: true) });
        var schema = new StructType(new[] { new StructField("s", innerNoF, nullable: true) });

        var ex = Assert.Throws<AnalysisException>(() => ResolveCondition(
            schema,
            new BinaryComparison(Ref("t", "s", "f"), Literal.OfInt(0), ComparisonOperator.GreaterThan)));
        Assert.Equal(AnalysisErrorKind.UnresolvedStructField, ex.Kind);
        Assert.Equal("t.s.f", ex.Reference);
        Assert.Equal("s", ex.RootColumn); // bound base, not the phantom qualifier `t`
    }

    [Fact]
    public void UnresolvedColumn_RootColumnIsNamePartsZero_QuotedDotStaysWhole()
    {
        // #618: an unresolved plain column reference carries RootColumn = NameParts[0]. A single-part
        // quoted-dot name `a.b` roots at `a.b` (NOT split to `a`); a qualified `x.y` roots at `x`. This lets a
        // reclassifier attribute a dropped quoted-dot column correctly instead of splitting the flattened name.
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });

        var quotedDot = Assert.Throws<AnalysisException>(() => ResolveCondition(
            schema, new BinaryComparison(Ref("a.b"), Literal.OfInt(0), ComparisonOperator.GreaterThan)));
        Assert.Equal(AnalysisErrorKind.UnresolvedColumn, quotedDot.Kind);
        Assert.Equal("a.b", quotedDot.RootColumn);

        var qualified = Assert.Throws<AnalysisException>(() => ResolveCondition(
            schema, new BinaryComparison(Ref("x", "y"), Literal.OfInt(0), ComparisonOperator.GreaterThan)));
        Assert.Equal(AnalysisErrorKind.UnresolvedColumn, qualified.Kind);
        Assert.Equal("x", qualified.RootColumn);
    }

    [Fact]
    public void MultipartRef_NonColumnLeadingPart_FallsBackToTrailingPartBinding()
    {
        // `qualifier.id` where `qualifier` is not a column → M1 qualifier fallback binds the trailing `id`.
        Expression cond = ResolveCondition(
            SchemaWithStructAndTopLevelF,
            new BinaryComparison(Ref("qualifier", "id"), Literal.OfLong(0), ComparisonOperator.GreaterThan));

        var attr = Assert.IsType<AttributeReference>(Assert.IsType<BinaryComparison>(cond).Left);
        Assert.Equal("id", attr.Name);
    }

    [Fact]
    public void QualifiedNestedRef_ResolvesFromFirstColumnPart_NotTrailingColumn()
    {
        // `t.s.f`: `t` is an (unmodelled) relation qualifier, so resolution scans to the first part that
        // IS a column (`s`) and extracts `f` from it — it must NOT silently bind the top-level `f`.
        Expression cond = ResolveCondition(
            SchemaWithStructAndTopLevelF,
            new BinaryComparison(Ref("t", "s", "f"), Literal.OfInt(0), ComparisonOperator.GreaterThan));

        var field = Assert.IsType<GetStructField>(Assert.IsType<BinaryComparison>(cond).Left);
        Assert.Equal("f", field.FieldName);
        Assert.Equal(0, field.Ordinal);
        var structColumn = Assert.IsType<AttributeReference>(field.Child);
        Assert.Equal("s", structColumn.Name); // the struct column, not the top-level `f`
    }

    [Fact]
    public void MultipartRef_NoLeadingColumnPart_BindsTrailingColumn()
    {
        // `db.people.id`: neither `db` nor `people` is a column, so the M1 catalog-qualifier degenerate
        // still binds the trailing column `id`.
        Expression cond = ResolveCondition(
            SchemaWithStructAndTopLevelF,
            new BinaryComparison(Ref("db", "people", "id"), Literal.OfLong(0), ComparisonOperator.GreaterThan));

        var attr = Assert.IsType<AttributeReference>(Assert.IsType<BinaryComparison>(cond).Left);
        Assert.Equal("id", attr.Name);
    }

    [Fact]
    public void BareNestedRef_InProjection_ResolvesWithoutThrowing()
    {
        // Pre-#580 a bare nested ref in a projection threw (no ToAttribute case). It must now resolve to
        // a GetStructField; output auto-naming (`s.f` -> column `f`, Spark parity) is asserted end-to-end
        // in the executor PhysicalPlanShapeTests (where the derived OutputSchema is exposed).
        var catalog = new LocalCatalog();
        catalog.Register("t", SchemaWithStructAndTopLevelF);
        var plan = new Project(new Expression[] { Ref("s", "f") }, new UnresolvedRelation(new[] { "t" }));

        var resolved = (Project)new Analyzer(catalog).Resolve(plan);

        var field = Assert.IsType<GetStructField>(resolved.ProjectList[0]);
        Assert.Equal("f", field.FieldName);
    }
}
