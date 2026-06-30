using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>The boolean connectives the interpreted evaluator supports under Kleene 3-valued logic.</summary>
public enum LogicalOperator
{
    /// <summary>Conjunction (<c>AND</c>): binary.</summary>
    And,

    /// <summary>Disjunction (<c>OR</c>): binary.</summary>
    Or,

    /// <summary>Negation (<c>NOT</c>): unary.</summary>
    Not,
}

/// <summary>
/// A boolean connective (<c>AND</c>/<c>OR</c>/<c>NOT</c>) evaluated under Kleene three-valued logic
/// (STORY-03.4.1), so SQL <c>NULL</c> means "unknown": <c>FALSE AND NULL = FALSE</c>,
/// <c>TRUE OR NULL = TRUE</c>, and <c>NOT NULL = NULL</c>. The 3VL truth tables are the EPIC-02 #143
/// reference in <see cref="DeltaSharp.Engine.Columnar.NullPropagation"/>; this node only validates that
/// operands are boolean and resolves nullability. The result is a nullable <see cref="BooleanType"/>.
/// </summary>
public sealed class LogicalExpression : PhysicalExpression
{
    private readonly PhysicalExpression[] _children;

    /// <summary>Creates a binary <c>AND</c>/<c>OR</c>.</summary>
    /// <param name="left">The left boolean operand.</param>
    /// <param name="right">The right boolean operand.</param>
    /// <param name="op">Either <see cref="LogicalOperator.And"/> or <see cref="LogicalOperator.Or"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="left"/> or <paramref name="right"/> is null.</exception>
    /// <exception cref="ArgumentException">An operand is not boolean, or <paramref name="op"/> is <see cref="LogicalOperator.Not"/>.</exception>
    public LogicalExpression(PhysicalExpression left, PhysicalExpression right, LogicalOperator op)
        : base(BooleanType.Instance, Nullability(left, right))
    {
        if (op == LogicalOperator.Not)
        {
            throw new ArgumentException("NOT is unary; use the single-operand constructor.", nameof(op));
        }

        RequireBoolean(left, nameof(left));
        RequireBoolean(right, nameof(right));
        _children = [left, right];
        Operator = op;
    }

    /// <summary>Creates a unary <c>NOT</c>.</summary>
    /// <param name="operand">The boolean operand to negate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operand"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="operand"/> is not boolean.</exception>
    public LogicalExpression(PhysicalExpression operand)
        : base(BooleanType.Instance, Nullability(operand))
    {
        RequireBoolean(operand, nameof(operand));
        _children = [operand];
        Operator = LogicalOperator.Not;
    }

    /// <summary>The boolean connective.</summary>
    public LogicalOperator Operator { get; }

    /// <summary>The left operand of a binary connective, or the sole operand of <c>NOT</c>.</summary>
    public PhysicalExpression Left => _children[0];

    /// <summary>The right operand of a binary connective.</summary>
    /// <exception cref="InvalidOperationException">This is a unary <c>NOT</c>.</exception>
    public PhysicalExpression Right => _children.Length == 2
        ? _children[1]
        : throw new InvalidOperationException("NOT has no right operand.");

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalExpression> Children => _children;

    private static bool Nullability(PhysicalExpression operand)
    {
        ArgumentNullException.ThrowIfNull(operand);
        return operand.Nullable;
    }

    private static bool Nullability(PhysicalExpression left, PhysicalExpression right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Nullable || right.Nullable;
    }

    private static void RequireBoolean(PhysicalExpression operand, string paramName)
    {
        if (operand.Type is not BooleanType)
        {
            throw new ArgumentException(
                $"Boolean connective requires a boolean operand but got '{operand.Type.SimpleString}'.", paramName);
        }
    }
}
