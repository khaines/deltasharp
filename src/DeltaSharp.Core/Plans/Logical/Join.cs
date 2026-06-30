using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// A relational join of two child plans, recording the <see cref="JoinType"/> and exactly one of
/// three mutually exclusive join criteria without reading either side: an explicit
/// <see cref="Condition"/>, a set of <see cref="UsingColumns"/> (<c>df.join(other, Seq("id"))</c>),
/// or a <see cref="IsNatural"/> natural join. Spark parity: <c>Join</c>.
/// </summary>
/// <remarks>
/// <see cref="UsingColumns"/> and <see cref="IsNatural"/> capture the pre-resolution shape of a
/// using/natural join. The analyzer desugars them — once both sides resolve — into a resolved
/// equi-<see cref="Condition"/> over the shared columns; construction here performs no such
/// resolution. The three criteria are mutually exclusive: at most one of
/// (<see cref="Condition"/>, <see cref="UsingColumns"/>, <see cref="IsNatural"/>) may be set.
/// </remarks>
internal sealed class Join : LogicalPlan
{
    private readonly IReadOnlyList<Expression> _expressions;

    /// <summary>Creates a join.</summary>
    /// <param name="left">The left input plan.</param>
    /// <param name="right">The right input plan.</param>
    /// <param name="joinType">The join kind.</param>
    /// <param name="condition">An explicit join condition, or <see langword="null"/>.</param>
    /// <param name="usingColumns">The shared column names for a using-join, or <see langword="null"/>.</param>
    /// <param name="isNatural">Whether this is a natural join.</param>
    public Join(
        LogicalPlan left,
        LogicalPlan right,
        JoinType joinType,
        Expression? condition = null,
        IEnumerable<string>? usingColumns = null,
        bool isNatural = false)
        : base(PlanCollections.AsReadOnly(
            left ?? throw new ArgumentNullException(nameof(left)),
            right ?? throw new ArgumentNullException(nameof(right))))
    {
        IReadOnlyList<string>? copiedUsing = usingColumns is null
            ? null
            : PlanCollections.ToImmutable(usingColumns, nameof(usingColumns));
        if (copiedUsing is { Count: 0 })
        {
            throw new ArgumentException(
                "A using-join requires at least one column.", nameof(usingColumns));
        }

        if (condition is not null && (copiedUsing is not null || isNatural))
        {
            throw new ArgumentException(
                "A join may not specify both a condition and using/natural columns.",
                nameof(condition));
        }

        if (copiedUsing is not null && isNatural)
        {
            throw new ArgumentException(
                "A natural join derives its columns and cannot also specify using columns.",
                nameof(usingColumns));
        }

        Left = left;
        Right = right;
        JoinType = joinType;
        Condition = condition;
        UsingColumns = copiedUsing;
        IsNatural = isNatural;
        _expressions = condition is null
            ? PlanCollections.Empty<Expression>()
            : PlanCollections.AsReadOnly(condition);
    }

    /// <summary>The left input plan.</summary>
    public LogicalPlan Left { get; }

    /// <summary>The right input plan.</summary>
    public LogicalPlan Right { get; }

    /// <summary>The join kind.</summary>
    public JoinType JoinType { get; }

    /// <summary>The optional explicit join condition.</summary>
    public Expression? Condition { get; }

    /// <summary>The shared column names of a using-join, or <see langword="null"/> when not a
    /// using-join.</summary>
    public IReadOnlyList<string>? UsingColumns { get; }

    /// <summary>Whether this is a natural join (the analyzer derives the shared columns).</summary>
    public bool IsNatural { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => _expressions;

    /// <inheritdoc/>
    public override string NodeName => "Join";

    /// <inheritdoc/>
    public override string SimpleString
    {
        get
        {
            string criteria = Condition is not null
                ? $", ({Condition.SimpleString})"
                : UsingColumns is not null
                    ? $", using [{string.Join(", ", UsingColumns)}]"
                    : IsNatural
                        ? ", natural"
                        : string.Empty;
            return $"{UnresolvedPrefix}Join {JoinType}{criteria}";
        }
    }

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren)
    {
        (LogicalPlan left, LogicalPlan right) = PlanNodes.TwoChildren(newChildren, NodeName);
        return new Join(left, right, JoinType, Condition, UsingColumns, IsNatural);
    }

    /// <inheritdoc/>
    public override LogicalPlan WithNewExpressions(IReadOnlyList<Expression> newExpressions)
    {
        if (Condition is null)
        {
            PlanNodes.RequireNoExpressions(newExpressions, NodeName);
            return this;
        }

        Expression newCondition = PlanNodes.RequireExpressions(newExpressions, 1, NodeName)[0];
        return new Join(Left, Right, JoinType, newCondition);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) =>
        other is Join join
        && JoinType == join.JoinType
        && IsNatural == join.IsNatural
        && ConditionEquals(Condition, join.Condition)
        && UsingColumnsEqual(UsingColumns, join.UsingColumns);

    private static bool ConditionEquals(Expression? a, Expression? b) =>
        (a, b) switch
        {
            (null, null) => true,
            (not null, not null) => a.Equals(b),
            _ => false,
        };

    private static bool UsingColumnsEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a, b) switch
        {
            (null, null) => true,
            (not null, not null) => PlanCollections.StringSequenceEquals(a, b),
            _ => false,
        };

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = PlanHash.Combine(PlanHash.Seed, (int)JoinType);
        hash = PlanHash.Combine(hash, IsNatural ? 1 : 0);
        hash = PlanHash.Combine(hash, Condition?.GetHashCode() ?? 0);
        if (UsingColumns is not null)
        {
            hash = PlanHash.CombineStrings(hash, UsingColumns);
        }

        return hash;
    }
}
