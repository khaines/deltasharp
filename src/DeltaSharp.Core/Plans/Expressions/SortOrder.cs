using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// An ordering term (Catalyst <c>SortOrder</c>) — a child expression plus a
/// <see cref="SortDirection"/> and a <see cref="NullOrdering"/>. It is the element an
/// <c>orderBy</c>/<c>sort</c> plan node carries. It forwards the child's type/nullability hints and
/// is resolved exactly when its child is.
/// </summary>
internal sealed class SortOrder : Expression
{
    /// <summary>Creates an ordering term over <paramref name="child"/>.</summary>
    /// <param name="child">The expression to order by.</param>
    /// <param name="direction">Ascending or descending.</param>
    /// <param name="nullOrdering">Where SQL <c>NULL</c>s sort.</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    public SortOrder(Expression child, SortDirection direction, NullOrdering nullOrdering)
        : base(Unary(child))
    {
        Direction = direction;
        NullOrdering = nullOrdering;
    }

    /// <summary>The ordering direction.</summary>
    public SortDirection Direction { get; }

    /// <summary>Where SQL <c>NULL</c>s sort.</summary>
    public NullOrdering NullOrdering { get; }

    /// <summary>The ordered expression.</summary>
    public Expression Child => Children[0];

    /// <inheritdoc/>
    public override DataType? Type => Child.Type;

    /// <inheritdoc/>
    public override bool Nullable => Child.Nullable;

    /// <inheritdoc/>
    // #614: a SortOrder is never in output position (it wraps an ORDER BY child), so this is symmetry
    // only — it propagates the child's mode-aware nullability like every other structural node.
    public override bool NullableUnder(AnsiMode mode) => Child.NullableUnder(mode);

    /// <inheritdoc/>
    public override string NodeName => "SortOrder";

    /// <inheritdoc/>
    public override string SimpleString
    {
        get
        {
            string direction = Direction == SortDirection.Ascending ? "ASC" : "DESC";
            string nulls = NullOrdering == NullOrdering.NullsFirst ? "NULLS FIRST" : "NULLS LAST";
            return $"{Child.SimpleString} {direction} {nulls}";
        }
    }

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 1, NodeName);
        return ReferenceEquals(newChildren[0], Child)
            ? this
            : new SortOrder(newChildren[0], Direction, NullOrdering);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other)
    {
        var order = (SortOrder)other;
        return Direction == order.Direction && NullOrdering == order.NullOrdering;
    }

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = PlanHash.Combine(PlanHash.Seed, (int)Direction);
        return PlanHash.Combine(hash, (int)NullOrdering);
    }
}
