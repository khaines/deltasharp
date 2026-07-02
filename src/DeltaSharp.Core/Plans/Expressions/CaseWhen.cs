namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// A conditional expression (Catalyst <c>CaseWhen</c>) — <c>CASE WHEN c1 THEN v1 [WHEN c2 THEN v2 …]
/// [ELSE e] END</c>. It is the IR node behind the public <c>Functions.When(...)</c> /
/// <c>Column.When(...)</c> / <c>Column.Otherwise(...)</c> builders. Each branch pairs a boolean
/// condition with a value; an optional trailing else value is used when no branch matches (its
/// absence means an unmatched row is SQL <c>NULL</c>, matching Spark).
/// </summary>
/// <remarks>
/// <para>
/// Children are the <b>flattened</b> branch expressions in evaluation order —
/// <c>[c0, v0, c1, v1, …, cN, vN, (else?)]</c> — so the base tree machinery (structural sharing,
/// equality, hashing, transforms) applies unchanged. The split between branches and the optional
/// else is derived from the child count's parity (an odd count carries an else), so there is no
/// extra own-state to compare or hash: two <see cref="CaseWhen"/>s are equal exactly when their
/// children are (mirroring <see cref="And"/>/<see cref="Or"/>).
/// </para>
/// <para>
/// Building a node does no work and no type coercion: the result <see cref="Type"/> is
/// <see langword="null"/> (the common type across the branch values needs coercion, an analyzer
/// concern — STORY-04.5.2 / #171) and <see cref="Nullable"/> is <b>derived</b> from the possible
/// result values — nullable if any branch value or the else value is nullable, or if there is no
/// else (implicit-NULL). It is resolved exactly when every child is (the default), so a
/// <see cref="CaseWhen"/> over unresolved conditions/values stays unresolved until analysis.
/// </para>
/// </remarks>
internal sealed class CaseWhen : Expression
{
    /// <summary>Creates a single-branch <c>CASE WHEN <paramref name="condition"/> THEN
    /// <paramref name="value"/> END</c> (the seed the <c>when(...)</c> builder returns).</summary>
    /// <param name="condition">The (boolean) branch condition.</param>
    /// <param name="value">The branch result value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> or
    /// <paramref name="value"/> is null.</exception>
    public CaseWhen(Expression condition, Expression value)
        : this(Seed(condition, value))
    {
    }

    private CaseWhen(IEnumerable<Expression> children)
        : base(PlanCollections.ToImmutable(children, nameof(children)))
    {
        if (Children.Count < 2)
        {
            throw new ArgumentException(
                "A CASE expression requires at least one WHEN branch (a condition and a value).",
                nameof(children));
        }
    }

    private static Expression[] Seed(Expression condition, Expression value)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(value);
        return new[] { condition, value };
    }

    /// <summary>Whether this <c>CASE</c> carries a trailing <c>ELSE</c> value (derived from the odd
    /// child count).</summary>
    public bool HasElse => (Children.Count & 1) == 1;

    /// <summary>The number of <c>WHEN … THEN …</c> branches.</summary>
    public int BranchCount => Children.Count / 2;

    /// <summary>The <c>WHEN … THEN …</c> branches, in evaluation order.</summary>
    public IReadOnlyList<(Expression Condition, Expression Value)> Branches
    {
        get
        {
            var branches = new (Expression Condition, Expression Value)[BranchCount];
            for (int i = 0; i < branches.Length; i++)
            {
                branches[i] = (Children[(2 * i) + 0], Children[(2 * i) + 1]);
            }

            return branches;
        }
    }

    /// <summary>The trailing <c>ELSE</c> value, or <see langword="null"/> when absent.</summary>
    public Expression? ElseValue => HasElse ? Children[^1] : null;

    /// <summary>
    /// Derives nullability from the possible result values (mirroring how <see cref="And"/>/
    /// <see cref="Or"/>/<see cref="BinaryArithmetic"/> derive theirs from their operands): the
    /// result is nullable if any branch value is nullable, the else value is nullable, or there is
    /// <b>no</b> else at all (an unmatched row then yields an implicit SQL <c>NULL</c>). A
    /// <c>CASE</c> with an else whose branch values and else are all non-nullable is therefore
    /// non-nullable. Conditions do not affect result nullability.
    /// </summary>
    public override bool Nullable
    {
        get
        {
            if (!HasElse || ElseValue!.Nullable)
            {
                return true;
            }

            foreach ((Expression _, Expression value) in Branches)
            {
                if (value.Nullable)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <inheritdoc/>
    public override string NodeName => "CaseWhen";

    /// <inheritdoc/>
    public override string SimpleString
    {
        get
        {
            string branches = string.Join(
                " ",
                Branches.Select(b => $"WHEN {b.Condition.SimpleString} THEN {b.Value.SimpleString}"));
            string elsePart = HasElse ? $" ELSE {ElseValue!.SimpleString}" : string.Empty;
            return $"CASE {branches}{elsePart} END";
        }
    }

    /// <summary>Returns a new <see cref="CaseWhen"/> with an additional <c>WHEN … THEN …</c> branch
    /// appended. Rejected once an <c>ELSE</c> is present, matching Spark's rule that a chained
    /// <c>when(...)</c> cannot follow <c>otherwise(...)</c>.</summary>
    /// <param name="condition">The (boolean) branch condition.</param>
    /// <param name="value">The branch result value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> or
    /// <paramref name="value"/> is null.</exception>
    /// <exception cref="InvalidOperationException">An <c>ELSE</c> value is already set.</exception>
    public CaseWhen AddBranch(Expression condition, Expression value)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(value);
        if (HasElse)
        {
            throw new InvalidOperationException(
                "Cannot add a WHEN branch after ELSE; when() can only follow another when(), not "
                + "otherwise().");
        }

        var next = new Expression[Children.Count + 2];
        for (int i = 0; i < Children.Count; i++)
        {
            next[i] = Children[i];
        }

        next[^2] = condition;
        next[^1] = value;
        return new CaseWhen(next);
    }

    /// <summary>Returns a new <see cref="CaseWhen"/> with the trailing <c>ELSE</c>
    /// <paramref name="value"/> set. Rejected once an <c>ELSE</c> is already present (Spark forbids a
    /// second <c>otherwise(...)</c>).</summary>
    /// <param name="value">The else result value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="InvalidOperationException">An <c>ELSE</c> value is already set.</exception>
    public CaseWhen WithElse(Expression value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (HasElse)
        {
            throw new InvalidOperationException(
                "Cannot set ELSE twice; otherwise() can only be applied once to a CASE expression.");
        }

        var next = new Expression[Children.Count + 1];
        for (int i = 0; i < Children.Count; i++)
        {
            next[i] = Children[i];
        }

        next[^1] = value;
        return new CaseWhen(next);
    }

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, Children.Count, NodeName);
        for (int i = 0; i < newChildren.Count; i++)
        {
            if (!ReferenceEquals(newChildren[i], Children[i]))
            {
                return new CaseWhen(newChildren);
            }
        }

        return this;
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) => true;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Seed;
}
