using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// A null predicate (<c>child IS NULL</c> or <c>child IS NOT NULL</c>) over a single operand
/// (STORY-03.4.1). Unlike every other expression, this one <b>never</b> returns SQL <c>NULL</c>: it
/// inspects the operand's validity bit and always produces a defined <see cref="BooleanType"/>, so a
/// null input maps to <see langword="true"/>/<see langword="false"/> rather than propagating.
/// </summary>
public sealed class IsNullExpression : PhysicalExpression
{
    private readonly PhysicalExpression[] _children;

    /// <summary>Creates <c>child IS NULL</c> (or its negation).</summary>
    /// <param name="child">The operand whose validity is tested.</param>
    /// <param name="negated"><see langword="true"/> for <c>IS NOT NULL</c>; otherwise <c>IS NULL</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    public IsNullExpression(PhysicalExpression child, bool negated)
        : base(BooleanType.Instance, false)
    {
        ArgumentNullException.ThrowIfNull(child);
        _children = [child];
        Negated = negated;
    }

    /// <summary><see langword="true"/> for <c>IS NOT NULL</c>; <see langword="false"/> for <c>IS NULL</c>.</summary>
    public bool Negated { get; }

    /// <summary>The operand whose validity is tested.</summary>
    public PhysicalExpression Child => _children[0];

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalExpression> Children => _children;
}
