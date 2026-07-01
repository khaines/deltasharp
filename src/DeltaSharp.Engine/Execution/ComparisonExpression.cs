using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>The binary comparison operators the interpreted evaluator supports (Spark <c>= &lt;&gt; &lt; &lt;= &gt; &gt;=</c>).</summary>
public enum ComparisonOperator
{
    /// <summary>Equality (<c>=</c>).</summary>
    Equal,

    /// <summary>Inequality (<c>&lt;&gt;</c> / <c>!=</c>).</summary>
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

/// <summary>How a resolved <see cref="ComparisonExpression"/> compares its operands lane-by-lane.</summary>
internal enum ComparisonEvalKind
{
    /// <summary>Both operands widened to 64-bit signed integers.</summary>
    Int64,

    /// <summary>Both operands widened to IEEE double, using Spark NaN/−0 ordering.</summary>
    Double,

    /// <summary>Exact fixed-point comparison via <see cref="DecimalValue"/>.</summary>
    Decimal,

    /// <summary>Boolean comparison (<c>false &lt; true</c>).</summary>
    Boolean,

    /// <summary>UTF-8 byte-lexicographic comparison (Spark binary collation).</summary>
    String,

    /// <summary>Unsigned byte-lexicographic comparison.</summary>
    Binary,

    /// <summary>Epoch-day comparison.</summary>
    Date,

    /// <summary>Epoch-microsecond comparison.</summary>
    Timestamp,

    /// <summary>A date and a timestamp, with the date promoted to its UTC-midnight instant.</summary>
    DateTimestamp,
}

/// <summary>
/// A binary comparison (<c>left op right</c>) returning a nullable <see cref="BooleanType"/>
/// (STORY-03.4.1). Comparison is <b>propagate-on-any-null</b>: a null operand yields SQL <c>NULL</c>
/// (#143). Numeric operands are widened to a common type (EPIC-02 <see cref="TypeCoercion"/>);
/// floating comparisons use Spark's NaN semantics (NaN equals NaN and sorts greatest, −0 equals +0);
/// strings/binary compare byte-lexicographically; a date compared with a timestamp is promoted to
/// its UTC-midnight instant (EPIC-02 <see cref="TemporalValues"/>).
/// </summary>
public sealed class ComparisonExpression : PhysicalExpression
{
    private readonly PhysicalExpression[] _children;

    /// <summary>Creates <paramref name="left"/> <paramref name="op"/> <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="op">The comparison operator.</param>
    /// <exception cref="ArgumentNullException"><paramref name="left"/> or <paramref name="right"/> is null.</exception>
    /// <exception cref="ArgumentException">The operands are not comparable.</exception>
    public ComparisonExpression(PhysicalExpression left, PhysicalExpression right, ComparisonOperator op)
        : base(BooleanType.Instance, Nullability(left, right))
    {
        EvalKind = Resolve(left.Type, right.Type, op);
        _children = [left, right];
        Operator = op;
    }

    /// <summary>The comparison operator.</summary>
    public ComparisonOperator Operator { get; }

    /// <summary>The left operand.</summary>
    public PhysicalExpression Left => _children[0];

    /// <summary>The right operand.</summary>
    public PhysicalExpression Right => _children[1];

    /// <summary>The comparison kind the evaluator uses (resolved once at construction).</summary>
    internal ComparisonEvalKind EvalKind { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalExpression> Children => _children;

    private static bool Nullability(PhysicalExpression left, PhysicalExpression right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Nullable || right.Nullable;
    }

    private static ComparisonEvalKind Resolve(DataType l, DataType r, ComparisonOperator op)
    {
        if (TypeCoercion.IsNumeric(l) && TypeCoercion.IsNumeric(r))
        {
            DataType common = TypeCoercion.FindWiderTypeForTwo(l, r)
                ?? throw new ArgumentException(
                    $"Comparison '{op}' has no common numeric type for '{l.SimpleString}' and '{r.SimpleString}'.");
            return common switch
            {
                FloatType or DoubleType => ComparisonEvalKind.Double,
                DecimalType => ComparisonEvalKind.Decimal,
                _ => ComparisonEvalKind.Int64,
            };
        }

        return (l, r) switch
        {
            (BooleanType, BooleanType) => ComparisonEvalKind.Boolean,
            (StringType, StringType) => ComparisonEvalKind.String,
            (BinaryType, BinaryType) => ComparisonEvalKind.Binary,
            (DateType, DateType) => ComparisonEvalKind.Date,
            (TimestampType, TimestampType) => ComparisonEvalKind.Timestamp,
            (DateType, TimestampType) or (TimestampType, DateType) => ComparisonEvalKind.DateTimestamp,
            _ => throw new ArgumentException(
                $"Comparison '{op}' is not defined for operands '{l.SimpleString}' and '{r.SimpleString}'."),
        };
    }
}
