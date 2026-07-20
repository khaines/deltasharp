using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// An explicit type conversion (Catalyst <c>Cast</c>) — <c>CAST(child AS targetType)</c>. The
/// target is an ADR-0008 <see cref="DataType"/> (AC3) and is the node's known result type even
/// before analysis. The supported-conversion matrix and ANSI overflow behavior are an
/// analyzer/execution concern (EPIC-02/EPIC-03); building the node does no work. The nullability
/// hint forwards the child's as a pre-analysis hint; the analyzer (FEAT-04.5) widens it for
/// null-introducing (lossy/non-ANSI) casts.
/// </summary>
internal sealed class Cast : Expression
{
    /// <summary>Creates <c>CAST(<paramref name="child"/> AS <paramref name="targetType"/>)</c>.</summary>
    /// <param name="child">The operand to convert.</param>
    /// <param name="targetType">The destination ADR-0008 type.</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> or
    /// <paramref name="targetType"/> is null.</exception>
    public Cast(Expression child, DataType targetType)
        : base(Unary(child))
    {
        ArgumentNullException.ThrowIfNull(targetType);
        TargetType = targetType;
    }

    /// <summary>The destination type (identical to <see cref="Type"/>).</summary>
    public DataType TargetType { get; }

    /// <summary>The operand being converted.</summary>
    public Expression Child => Children[0];

    /// <inheritdoc/>
    public override DataType Type => TargetType;

    /// <inheritdoc/>
    // #614: Legacy widening for null-introducing (lossy/non-ANSI identity-changing) casts is handled
    // by NullableUnder; the mode-independent hint below forwards the child's.
    public override bool Nullable => Child.Nullable;

    /// <inheritdoc/>
    public override bool NullableUnder(AnsiMode mode) =>
        Child.NullableUnder(mode) || (mode == AnsiMode.Legacy && !TargetType.Equals(Child.Type));

    /// <inheritdoc/>
    public override string NodeName => "Cast";

    /// <inheritdoc/>
    public override string SimpleString => $"cast({Child.SimpleString} as {TargetType.SimpleString})";

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 1, NodeName);
        return ReferenceEquals(newChildren[0], Child) ? this : new Cast(newChildren[0], TargetType);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) => TargetType.Equals(((Cast)other).TargetType);

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Combine(PlanHash.Seed, TargetType.GetHashCode());
}
