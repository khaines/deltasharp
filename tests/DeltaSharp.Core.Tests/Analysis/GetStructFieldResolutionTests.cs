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
        Assert.Throws<AnalysisException>(() => ResolveCondition(
            SchemaWithStructAndTopLevelF,
            new BinaryComparison(Ref("id", "f"), Literal.OfInt(0), ComparisonOperator.GreaterThan)));
    }

    [Fact]
    public void MultipartRef_UnknownStructField_FailsClosed()
    {
        Assert.Throws<AnalysisException>(() => ResolveCondition(
            SchemaWithStructAndTopLevelF,
            new BinaryComparison(Ref("s", "nope"), Literal.OfInt(0), ComparisonOperator.GreaterThan)));
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
}
