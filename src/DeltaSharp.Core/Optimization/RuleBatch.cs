namespace DeltaSharp.Optimization;

/// <summary>
/// How a <see cref="RuleBatch"/> applies its rules — Catalyst's <c>Once</c> vs <c>FixedPoint</c>
/// strategy.
/// </summary>
internal enum RuleStrategy
{
    /// <summary>Apply the batch's rules exactly once, in order (a single sweep).</summary>
    Once,

    /// <summary>
    /// Repeat the full sweep until the plan stops changing (structural fixpoint) or the batch's
    /// <see cref="RuleBatch.MaxIterations"/> safety valve is reached.
    /// </summary>
    FixedPoint,
}

/// <summary>
/// An ordered group of <see cref="Rule"/>s applied together under a single <see cref="RuleStrategy"/>
/// (Catalyst's <c>Batch</c>). Grouping related rewrites lets one rule's output feed another within a
/// fixpoint (for example predicate pushdown exposing further column pruning).
/// </summary>
internal sealed class RuleBatch
{
    /// <summary>Creates a batch.</summary>
    /// <param name="name">A stable, human-readable batch name (for diagnostics/ordering).</param>
    /// <param name="strategy">Whether to sweep once or iterate to a fixpoint.</param>
    /// <param name="maxIterations">The fixpoint safety valve (ignored for <see cref="RuleStrategy.Once"/>).</param>
    /// <param name="rules">The rules, applied in the given order.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null/empty, <paramref name="rules"/>
    /// is empty, or <paramref name="maxIterations"/> is not positive for a fixpoint batch.</exception>
    public RuleBatch(string name, RuleStrategy strategy, int maxIterations, params Rule[] rules)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Length == 0)
        {
            throw new ArgumentException("A rule batch must contain at least one rule.", nameof(rules));
        }

        if (strategy == RuleStrategy.FixedPoint && maxIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxIterations), maxIterations, "A fixpoint batch requires a positive iteration cap.");
        }

        for (int i = 0; i < rules.Length; i++)
        {
            if (rules[i] is null)
            {
                throw new ArgumentException("A rule batch cannot contain a null rule.", nameof(rules));
            }
        }

        Name = name;
        Strategy = strategy;
        MaxIterations = maxIterations;
        Rules = Array.AsReadOnly((Rule[])rules.Clone());
    }

    /// <summary>The batch name.</summary>
    public string Name { get; }

    /// <summary>The application strategy.</summary>
    public RuleStrategy Strategy { get; }

    /// <summary>The fixpoint iteration cap (a bounded-termination guarantee).</summary>
    public int MaxIterations { get; }

    /// <summary>The rules, in application order.</summary>
    public IReadOnlyList<Rule> Rules { get; }
}
