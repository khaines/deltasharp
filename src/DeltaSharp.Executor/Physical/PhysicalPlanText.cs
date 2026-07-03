using System.Globalization;
using System.Text;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// Renders EPIC-03 physical <see cref="PhysicalExpression"/>s and small metadata lists as short,
/// Spark-like text for <c>DataFrame.Explain</c>'s physical section (STORY-04.7.3). It is purely
/// descriptive: it walks the already-built expression trees and <b>never</b> throws — a rendering
/// helper for a debugging aid must not crash on an unfamiliar node (AC4), so any unrecognised
/// expression falls back to <c>TypeName(children…)</c>.
/// </summary>
/// <remarks>
/// A physical <see cref="ColumnReference"/> is a bare input <b>ordinal</b>; to give the reader the same
/// <c>name#ordinal</c> shape the logical renderer uses (logical is <c>name#exprId</c>), a node passes
/// the input schema the expression's ordinals address (its child's <c>OutputSchema</c>) so the ordinal
/// resolves back to a column name. When no schema is available (or an ordinal is out of range) the
/// renderer degrades to the bare <c>#ordinal</c> rather than throwing.
/// </remarks>
internal static class PhysicalPlanText
{
    /// <summary>Renders a schema's field names as <c>[a, b, c]</c>.</summary>
    public static string Columns(StructType schema)
    {
        var builder = new StringBuilder("[");
        for (int i = 0; i < schema.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(schema[i].Name);
        }

        return builder.Append(']').ToString();
    }

    /// <summary>Renders a list of expressions as <c>[e1, e2]</c>, resolving ordinals against
    /// <paramref name="inputSchema"/> (the schema the expressions' <see cref="ColumnReference"/>s address).</summary>
    public static string ExprList(IReadOnlyList<PhysicalExpression> expressions, StructType? inputSchema = null)
    {
        var builder = new StringBuilder("[");
        for (int i = 0; i < expressions.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Expr(expressions[i], inputSchema));
        }

        return builder.Append(']').ToString();
    }

    /// <summary>
    /// Renders a projection list as <c>[expr AS name, col#ord]</c>: a computed projection is suffixed
    /// with its output field name (Spark's <c>AS</c> alias), while a bare column reference renders as
    /// just its resolved <c>name#ordinal</c> (Spark does not alias a pass-through column to itself).
    /// </summary>
    public static string ProjectionList(
        IReadOnlyList<PhysicalExpression> projections, StructType inputSchema, StructType outputSchema)
    {
        var builder = new StringBuilder("[");
        for (int i = 0; i < projections.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Expr(projections[i], inputSchema));
            if (projections[i] is not ColumnReference && i < outputSchema.Count)
            {
                builder.Append(" AS ").Append(outputSchema[i].Name);
            }
        }

        return builder.Append(']').ToString();
    }

    /// <summary>Renders a list of aggregate terms as <c>[sum(#2), count]</c>.</summary>
    public static string AggregateList(IReadOnlyList<AggregateExpression> aggregates, StructType? inputSchema = null)
    {
        var builder = new StringBuilder("[");
        for (int i = 0; i < aggregates.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Expr(aggregates[i], inputSchema));
        }

        return builder.Append(']').ToString();
    }

    /// <summary>Renders a list of sort keys as <c>[#2 DESC, #0 ASC]</c>.</summary>
    public static string SortList(IReadOnlyList<SortOrder> orders, StructType? inputSchema = null)
    {
        var builder = new StringBuilder("[");
        for (int i = 0; i < orders.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            SortOrder order = orders[i];
            builder.Append(Expr(order.Expression, inputSchema))
                .Append(order.Direction == SortDirection.Descending ? " DESC" : " ASC");
        }

        return builder.Append(']').ToString();
    }

    /// <summary>
    /// Renders a physical expression as short Spark-like text, resolving <see cref="ColumnReference"/>
    /// ordinals against <paramref name="inputSchema"/> when supplied. Never throws.
    /// </summary>
    public static string Expr(PhysicalExpression expression, StructType? inputSchema = null) => expression switch
    {
        ColumnReference column => RenderColumn(column, inputSchema),
        Literal literal => RenderLiteral(literal),
        ComparisonExpression comparison =>
            $"({Expr(comparison.Children[0], inputSchema)} {ComparisonSymbol(comparison.Operator)} {Expr(comparison.Children[1], inputSchema)})",
        ArithmeticExpression arithmetic =>
            $"({Expr(arithmetic.Children[0], inputSchema)} {ArithmeticSymbol(arithmetic.Operator)} {Expr(arithmetic.Children[1], inputSchema)})",
        LogicalExpression logical => RenderLogical(logical, inputSchema),
        IsNullExpression isNull => $"{(isNull.Negated ? "isnotnull" : "isnull")}({Expr(isNull.Child, inputSchema)})",
        CastExpression cast => $"cast({Expr(cast.Child, inputSchema)} as {cast.TargetType.SimpleString})",
        AggregateExpression aggregate => RenderAggregate(aggregate, inputSchema),
        _ => RenderFallback(expression, inputSchema),
    };

    /// <summary>Renders a column reference as <c>name#ordinal</c> when the ordinal resolves against
    /// <paramref name="inputSchema"/>; otherwise as the bare <c>#ordinal</c>.</summary>
    private static string RenderColumn(ColumnReference column, StructType? inputSchema) =>
        inputSchema is not null && column.Ordinal < inputSchema.Count
            ? $"{inputSchema[column.Ordinal].Name}#{column.Ordinal}"
            : $"#{column.Ordinal}";

    private static string RenderLiteral(Literal literal)
    {
        if (literal.IsNull || literal.Value is null)
        {
            return "null";
        }

        return literal.Value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : literal.Value.ToString() ?? "null";
    }

    private static string RenderLogical(LogicalExpression logical, StructType? inputSchema)
    {
        if (logical.Operator == LogicalOperator.Not)
        {
            return $"NOT({Expr(logical.Children[0], inputSchema)})";
        }

        string symbol = logical.Operator == LogicalOperator.And ? "AND" : "OR";
        return $"({Expr(logical.Children[0], inputSchema)} {symbol} {Expr(logical.Children[1], inputSchema)})";
    }

    private static string RenderAggregate(AggregateExpression aggregate, StructType? inputSchema)
    {
        string name = aggregate.Function switch
        {
            AggregateFunction.Count => "count",
            AggregateFunction.Sum => "sum",
            AggregateFunction.Min => "min",
            AggregateFunction.Max => "max",
            AggregateFunction.Average => "avg",
            _ => aggregate.Function.ToString().ToLowerInvariant(),
        };

        return aggregate.Input is { } input ? $"{name}({Expr(input, inputSchema)})" : $"{name}(*)";
    }

    private static string RenderFallback(PhysicalExpression expression, StructType? inputSchema)
    {
        var builder = new StringBuilder(expression.GetType().Name);
        IReadOnlyList<PhysicalExpression> children = expression.Children;
        if (children.Count == 0)
        {
            return builder.ToString();
        }

        builder.Append('(');
        for (int i = 0; i < children.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Expr(children[i], inputSchema));
        }

        return builder.Append(')').ToString();
    }

    private static string ComparisonSymbol(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal => "=",
        ComparisonOperator.NotEqual => "<>",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        _ => op.ToString(),
    };

    private static string ArithmeticSymbol(ArithmeticOperator op) => op switch
    {
        ArithmeticOperator.Add => "+",
        ArithmeticOperator.Subtract => "-",
        ArithmeticOperator.Multiply => "*",
        ArithmeticOperator.Divide => "/",
        ArithmeticOperator.Remainder => "%",
        _ => op.ToString(),
    };
}
