using System.Globalization;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// The binary arithmetic operators a <see cref="BinaryArithmetic"/> node carries (Spark
/// <c>+ - * / %</c>). The logical tree deliberately does <b>not</b> reference the execution layer
/// (EPIC-04 layer separation): these mirror the eventual physical operators for parity only.
/// </summary>
internal enum ArithmeticOperator
{
    /// <summary>Addition (<c>+</c>).</summary>
    Add,

    /// <summary>Subtraction (<c>-</c>).</summary>
    Subtract,

    /// <summary>Multiplication (<c>*</c>).</summary>
    Multiply,

    /// <summary>Division (<c>/</c>).</summary>
    Divide,

    /// <summary>Modulo (<c>%</c>).</summary>
    Remainder,
}

/// <summary>
/// The binary comparison operators a <see cref="BinaryComparison"/> node carries (Spark
/// <c>= &lt;&gt; &lt; &lt;= &gt; &gt;=</c>).
/// </summary>
internal enum ComparisonOperator
{
    /// <summary>Equality (<c>=</c>).</summary>
    Equal,

    /// <summary>Inequality (<c>&lt;&gt;</c>).</summary>
    NotEqual,

    /// <summary>Less-than (<c>&lt;</c>).</summary>
    LessThan,

    /// <summary>Less-than-or-equal (<c>&lt;=</c>).</summary>
    LessThanOrEqual,

    /// <summary>Greater-than (<c>&gt;</c>).</summary>
    GreaterThan,

    /// <summary>Greater-than-or-equal (<c>&gt;=</c>).</summary>
    GreaterThanOrEqual,
}

/// <summary>The ordering direction of a <see cref="SortOrder"/> (Spark <c>ASC</c>/<c>DESC</c>).</summary>
internal enum SortDirection
{
    /// <summary>Ascending (<c>ASC</c>).</summary>
    Ascending,

    /// <summary>Descending (<c>DESC</c>).</summary>
    Descending,
}

/// <summary>Where SQL <c>NULL</c>s sort within a <see cref="SortOrder"/> (Spark <c>NULLS FIRST/LAST</c>).</summary>
internal enum NullOrdering
{
    /// <summary>Nulls sort before non-nulls (<c>NULLS FIRST</c>).</summary>
    NullsFirst,

    /// <summary>Nulls sort after non-nulls (<c>NULLS LAST</c>).</summary>
    NullsLast,
}

/// <summary>
/// A stable identity for a resolved <see cref="AttributeReference"/> (Catalyst <c>ExprId</c>).
/// The analyzer (FEAT-04.5) assigns ids; this story constructs them explicitly and never
/// allocates them, so no <c>Guid.NewGuid</c>/reflection is used (BannedApiAnalyzers-clean).
/// </summary>
/// <param name="Value">The monotonic identity value.</param>
internal readonly record struct ExprId(long Value)
{
    /// <inheritdoc/>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
