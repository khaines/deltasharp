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

    /// <summary>Renders a list of expressions as <c>[e1, e2]</c>.</summary>
    public static string ExprList(IReadOnlyList<PhysicalExpression> expressions)
    {
        var builder = new StringBuilder("[");
        for (int i = 0; i < expressions.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Expr(expressions[i]));
        }

        return builder.Append(']').ToString();
    }

    /// <summary>Renders a list of aggregate terms as <c>[sum(#2), count]</c>.</summary>
    public static string AggregateList(IReadOnlyList<AggregateExpression> aggregates)
    {
        var builder = new StringBuilder("[");
        for (int i = 0; i < aggregates.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Expr(aggregates[i]));
        }

        return builder.Append(']').ToString();
    }

    /// <summary>Renders a list of sort keys as <c>[#2 DESC, #0 ASC]</c>.</summary>
    public static string SortList(IReadOnlyList<SortOrder> orders)
    {
        var builder = new StringBuilder("[");
        for (int i = 0; i < orders.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            SortOrder order = orders[i];
            builder.Append(Expr(order.Expression))
                .Append(order.Direction == SortDirection.Descending ? " DESC" : " ASC");
        }

        return builder.Append(']').ToString();
    }

    /// <summary>Renders a physical expression as short Spark-like text. Never throws.</summary>
    public static string Expr(PhysicalExpression expression) => expression switch
    {
        ColumnReference column => $"#{column.Ordinal}",
        Literal literal => RenderLiteral(literal),
        ComparisonExpression comparison =>
            $"({Expr(comparison.Children[0])} {ComparisonSymbol(comparison.Operator)} {Expr(comparison.Children[1])})",
        ArithmeticExpression arithmetic =>
            $"({Expr(arithmetic.Children[0])} {ArithmeticSymbol(arithmetic.Operator)} {Expr(arithmetic.Children[1])})",
        LogicalExpression logical => RenderLogical(logical),
        IsNullExpression isNull => $"{(isNull.Negated ? "isnotnull" : "isnull")}({Expr(isNull.Child)})",
        CastExpression cast => $"cast({Expr(cast.Child)} as {cast.TargetType.SimpleString})",
        AggregateExpression aggregate => RenderAggregate(aggregate),
        _ => RenderFallback(expression),
    };

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

    private static string RenderLogical(LogicalExpression logical)
    {
        if (logical.Operator == LogicalOperator.Not)
        {
            return $"NOT({Expr(logical.Children[0])})";
        }

        string symbol = logical.Operator == LogicalOperator.And ? "AND" : "OR";
        return $"({Expr(logical.Children[0])} {symbol} {Expr(logical.Children[1])})";
    }

    private static string RenderAggregate(AggregateExpression aggregate)
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

        return aggregate.Input is { } input ? $"{name}({Expr(input)})" : $"{name}(*)";
    }

    private static string RenderFallback(PhysicalExpression expression)
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

            builder.Append(Expr(children[i]));
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
