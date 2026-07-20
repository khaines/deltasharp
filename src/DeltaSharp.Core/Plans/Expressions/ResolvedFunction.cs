using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// A <b>resolved</b> function call (Catalyst parity: a bound scalar <c>Expression</c> or an
/// <c>AggregateFunction</c>) — the analyzer's replacement for an <see cref="UnresolvedFunction"/>.
/// It carries the canonical Spark function name, its arguments (with any implicit
/// <see cref="Cast"/>s the analyzer inserted for type coercion), a concrete ADR-0008 result
/// <see cref="Type"/>, a recorded <see cref="Nullable"/> flag, and its <see cref="FunctionKind"/>
/// (scalar vs aggregate). It is always resolved (its children are the resolved, coerced arguments).
/// </summary>
/// <remarks>
/// This node is produced only by the analyzer's function-binding pass (STORY-04.5.2 / #171); it is
/// never built by the public API, which emits an <see cref="UnresolvedFunction"/>. Binding assigns
/// the result type and kind, so downstream stages (optimizer, physical planning) read a typed,
/// classified call rather than re-deriving it from the name.
/// </remarks>
internal sealed class ResolvedFunction : Expression
{
    private readonly DataType _type;
    private readonly bool _nullable;

    /// <summary>Creates a resolved function call.</summary>
    /// <param name="name">The canonical (lower-case) Spark function name.</param>
    /// <param name="kind">Whether the function is scalar or aggregate.</param>
    /// <param name="type">The resolved ADR-0008 result type.</param>
    /// <param name="nullable">Whether the result is nullable.</param>
    /// <param name="arguments">The (resolved, coerced) argument expressions, in order.</param>
    /// <param name="isDistinct">Whether the call carried the <c>DISTINCT</c> qualifier.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> or <paramref name="arguments"/>
    /// is null.</exception>
    public ResolvedFunction(
        string name,
        FunctionKind kind,
        DataType type,
        bool nullable,
        IEnumerable<Expression> arguments,
        bool isDistinct = false)
        : base(PlanCollections.ToImmutable(arguments, nameof(arguments)))
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(type);
        Name = name;
        Kind = kind;
        _type = type;
        _nullable = nullable;
        IsDistinct = isDistinct;
    }

    /// <summary>The canonical (lower-case) Spark function name.</summary>
    public string Name { get; }

    /// <summary>Whether this function is scalar or aggregate.</summary>
    public FunctionKind Kind { get; }

    /// <summary>The argument expressions, in order.</summary>
    public IReadOnlyList<Expression> Arguments => Children;

    /// <summary>Whether the call carried the <c>DISTINCT</c> qualifier.</summary>
    public bool IsDistinct { get; }

    /// <inheritdoc/>
    public override DataType Type => _type;

    /// <inheritdoc/>
    // #614: function-wrapped overflow nullability is intentionally NOT widened under Legacy — the
    // stored precise nullability is kept so a function is not conservatively over-reported nullable
    // under Ansi (e.g. isnull(...) must stay NOT-NULL). This leaves abs(v+v)-style wrapping as a
    // documented residual under Legacy, tracked in #627.
    public override bool Nullable => _nullable;

    /// <inheritdoc/>
    public override string NodeName => "ResolvedFunction";

    /// <inheritdoc/>
    public override string SimpleString
    {
        get
        {
            string args = string.Join(", ", Arguments.Select(a => a.SimpleString));
            string distinct = IsDistinct ? "distinct " : string.Empty;
            return $"{Name}({distinct}{args})";
        }
    }

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, Arguments.Count, NodeName);
        return new ResolvedFunction(Name, Kind, _type, _nullable, newChildren, IsDistinct);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other)
    {
        var function = (ResolvedFunction)other;
        return string.Equals(Name, function.Name, StringComparison.Ordinal)
            && Kind == function.Kind
            && IsDistinct == function.IsDistinct
            && _nullable == function._nullable
            && _type.Equals(function._type);
    }

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = PlanHash.Combine(PlanHash.Seed, PlanHash.OfString(Name));
        hash = PlanHash.Combine(hash, (int)Kind);
        hash = PlanHash.Combine(hash, IsDistinct ? 1 : 0);
        hash = PlanHash.Combine(hash, _nullable ? 1 : 0);
        return PlanHash.Combine(hash, _type.GetHashCode());
    }
}
