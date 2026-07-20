using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// How a <see cref="ResolvedFunction"/>'s result nullability relates to its value arguments, so
/// <see cref="ResolvedFunction.NullableUnder(AnsiMode)"/> can recompute it mode-awarely (#627). Under
/// <see cref="AnsiMode.Ansi"/> every classification is byte-identical to the stored
/// <see cref="ResolvedFunction.Nullable"/>; only under <see cref="AnsiMode.Legacy"/> can an
/// overflow-capable/lossy argument widen a <see cref="PropagatesAny"/>/<see cref="PropagatesAll"/>
/// result (DeltaSharp nulls on overflow/invalid-cast in Legacy).
/// </summary>
internal enum FunctionNullability
{
    /// <summary>Result is null if ANY value argument is null (the common SQL default, e.g.
    /// <c>upper</c>/<c>length</c>/<c>concat</c>).</summary>
    PropagatesAny,

    /// <summary>Result is null only if ALL value arguments are null (e.g. <c>coalesce</c>).</summary>
    PropagatesAll,

    /// <summary>Nullability is independent of argument nullability — a constant, a nullary function,
    /// or a bespoke rule not expressible as any/all propagation (e.g. <c>count</c>, aggregates,
    /// <c>to_date</c>). The stored precise value is kept in both modes.</summary>
    Fixed,
}

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
    /// <param name="nullPropagation">How the result's nullability relates to its arguments (#627). It
    /// defaults to <see cref="FunctionNullability.PropagatesAny"/> — the common SQL rule — so callers
    /// that do not classify a function get the safe, mode-aware default. Under
    /// <see cref="AnsiMode.Ansi"/> every classification reproduces <paramref name="nullable"/> exactly.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> or <paramref name="arguments"/>
    /// is null.</exception>
    public ResolvedFunction(
        string name,
        FunctionKind kind,
        DataType type,
        bool nullable,
        IEnumerable<Expression> arguments,
        bool isDistinct = false,
        FunctionNullability nullPropagation = FunctionNullability.PropagatesAny)
        : base(PlanCollections.ToImmutable(arguments, nameof(arguments)))
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(type);
        Name = name;
        Kind = kind;
        _type = type;
        _nullable = nullable;
        IsDistinct = isDistinct;
        NullPropagation = nullPropagation;
    }

    /// <summary>The canonical (lower-case) Spark function name.</summary>
    public string Name { get; }

    /// <summary>Whether this function is scalar or aggregate.</summary>
    public FunctionKind Kind { get; }

    /// <summary>The argument expressions, in order.</summary>
    public IReadOnlyList<Expression> Arguments => Children;

    /// <summary>Whether the call carried the <c>DISTINCT</c> qualifier.</summary>
    public bool IsDistinct { get; }

    /// <summary>How this function's result nullability relates to its value arguments (#627), used by
    /// <see cref="NullableUnder(AnsiMode)"/> to recompute nullability mode-awarely.</summary>
    public FunctionNullability NullPropagation { get; }

    /// <inheritdoc/>
    public override DataType Type => _type;

    /// <inheritdoc/>
    // The stored precise nullability, mode-independent. This is the ANSI-truth value; the Legacy
    // widening (an overflow-capable / lossy argument manufacturing SQL NULL) is applied only by
    // NullableUnder below, per this function's NullPropagation classification (#627).
    public override bool Nullable => _nullable;

    /// <inheritdoc/>
    // #627: recompute nullability mode-awarely from the classified null-propagation. Under Ansi every
    // branch is byte-identical to the stored _nullable (the #614 invariant); under Legacy a
    // PropagatesAny/All function widens when an overflow-capable/lossy argument does. A Fixed function
    // (constant, nullary, aggregate, or bespoke rule) keeps its exact stored value in both modes.
    public override bool NullableUnder(AnsiMode mode) => NullPropagation switch
    {
        FunctionNullability.PropagatesAny => Children.Any(c => c.NullableUnder(mode)) || (_nullable && Children.Count == 0),
        FunctionNullability.PropagatesAll => Children.Count > 0 && Children.All(c => c.NullableUnder(mode)),
        _ => Nullable,
    };

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
        return new ResolvedFunction(Name, Kind, _type, _nullable, newChildren, IsDistinct, NullPropagation);
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
