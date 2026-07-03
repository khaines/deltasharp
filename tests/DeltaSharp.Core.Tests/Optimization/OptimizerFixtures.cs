using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Core.Tests.Optimization;

/// <summary>
/// Shared builders for optimizer tests: a resolved <c>people(id, name, age)</c> relation and its
/// attributes, plus small expression helpers. Attributes carry fixed <see cref="ExprId"/>s so tests
/// can reference the same columns the relation exposes.
/// </summary>
internal static class OptimizerFixtures
{
    public static AttributeReference Id => new("id", LongType.Instance, nullable: false, new ExprId(0));

    public static AttributeReference Name => new("name", StringType.Instance, nullable: true, new ExprId(1));

    public static AttributeReference Age => new("age", IntegerType.Instance, nullable: true, new ExprId(2));

    /// <summary>A resolved <c>people(id, name, age)</c> scan whose output matches <see cref="Id"/>/
    /// <see cref="Name"/>/<see cref="Age"/>.</summary>
    public static ResolvedRelation People()
    {
        var schema = new StructType(new[]
        {
            new StructField("id", LongType.Instance, nullable: false),
            new StructField("name", StringType.Instance, nullable: true),
            new StructField("age", IntegerType.Instance, nullable: true),
        });

        return new ResolvedRelation(new[] { "people" }, schema, new[] { Id, Name, Age });
    }

    /// <summary><c>age &gt; value</c>.</summary>
    public static BinaryComparison AgeGreaterThan(int value) =>
        new(Age, Literal.OfInt(value), ComparisonOperator.GreaterThan);

    /// <summary><c>1 + 2</c> as an integer arithmetic tree.</summary>
    public static BinaryArithmetic OnePlusTwo() =>
        new(Literal.OfInt(1), Literal.OfInt(2), ArithmeticOperator.Add);
}
