namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// An unresolved function call — a name plus argument expressions that the analyzer
/// (FEAT-04.5) later binds to a typed scalar or aggregate function. Spark parity:
/// <c>UnresolvedFunction</c>.
/// </summary>
/// <remarks>It is never resolved at construction (it renders with a leading apostrophe),
/// satisfying the unresolved-before-analysis invariant (AC4). Its children are its
/// <see cref="Arguments"/>.</remarks>
internal sealed class UnresolvedFunction : Expression
{
    /// <summary>Creates an unresolved function call.</summary>
    /// <param name="name">The function name (non-empty).</param>
    /// <param name="arguments">The argument expressions, in order.</param>
    /// <param name="isDistinct">Whether the call carries the <c>DISTINCT</c> qualifier.</param>
    public UnresolvedFunction(string name, IEnumerable<Expression> arguments, bool isDistinct = false)
        : base(PlanCollections.ToImmutable(arguments, nameof(arguments)))
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        IsDistinct = isDistinct;
    }

    /// <summary>The function name.</summary>
    public string Name { get; }

    /// <summary>The argument expressions, in order.</summary>
    public IReadOnlyList<Expression> Arguments => Children;

    /// <summary>Whether the call carries the <c>DISTINCT</c> qualifier.</summary>
    public bool IsDistinct { get; }

    /// <inheritdoc/>
    public override string NodeName => "UnresolvedFunction";

    /// <inheritdoc/>
    public override bool Resolved => false;

    /// <inheritdoc/>
    public override string SimpleString
    {
        get
        {
            string args = string.Join(", ", Arguments.Select(a => a.SimpleString));
            string distinct = IsDistinct ? "distinct " : string.Empty;
            return $"'{Name}({distinct}{args})";
        }
    }

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        ArgumentNullException.ThrowIfNull(newChildren);
        if (newChildren.Count != Arguments.Count)
        {
            throw new ArgumentException(
                $"UnresolvedFunction '{Name}' expects {Arguments.Count} argument(s) but got "
                + $"{newChildren.Count}.",
                nameof(newChildren));
        }

        return new UnresolvedFunction(Name, newChildren, IsDistinct);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) =>
        other is UnresolvedFunction function
        && string.Equals(Name, function.Name, StringComparison.Ordinal)
        && IsDistinct == function.IsDistinct;

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = PlanHash.Combine(PlanHash.Seed, PlanHash.OfString(Name));
        return PlanHash.Combine(hash, IsDistinct ? 1 : 0);
    }
}
