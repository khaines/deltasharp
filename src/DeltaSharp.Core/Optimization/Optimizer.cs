using DeltaSharp.Optimization.Rules;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Optimization;

/// <summary>
/// The rule-based logical optimizer (Catalyst's <c>Optimizer</c>/<c>RuleExecutor</c>): it turns an
/// analyzed <see cref="LogicalPlan"/> into an equivalent, cheaper one by running an ordered list of
/// <see cref="RuleBatch"/>es, each to a bounded fixpoint. It is pure — no I/O, no catalog, no
/// execution — and preserves the plan's output schema and result multiset
/// (see <c>docs/engineering/design/logical-optimizer.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// The optimizer sits after the analyzer and before physical planning. It is not yet wired into an
/// action driver (that bridge is #173/#174); this type is the standalone, testable optimization
/// stage. It is <see langword="internal"/> and references only the Core IR plus the shared
/// <c>DeltaSharp.Types</c> model, so it adds no public API surface and no reference to the engine.
/// </para>
/// <para>
/// <see cref="Optimize"/> is deterministic (fixed batch/rule order), idempotent (the single
/// operator-optimization batch runs to a <b>global</b> fixpoint, so a second <c>Optimize</c> re-runs
/// it, finds the plan already at that fixpoint, and stops), and terminating (each fixpoint batch
/// stops at a structural fixpoint or after <see cref="DefaultMaxIterations"/>).
/// </para>
/// </remarks>
internal sealed class Optimizer
{
    /// <summary>The default fixpoint iteration cap. M1 plans converge in a couple of sweeps; the
    /// generous cap is a pure safety valve that guarantees termination.</summary>
    public const int DefaultMaxIterations = 100;

    private readonly IReadOnlyList<RuleBatch> _batches;

    /// <summary>Creates an optimizer with the default M1 batch pipeline.</summary>
    public Optimizer()
        : this(DefaultBatches(DefaultMaxIterations))
    {
    }

    /// <summary>Creates an optimizer over an explicit batch list (used by framework tests).</summary>
    /// <param name="batches">The batches, applied in order.</param>
    /// <exception cref="ArgumentException"><paramref name="batches"/> is null or empty.</exception>
    internal Optimizer(IReadOnlyList<RuleBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if (batches.Count == 0)
        {
            throw new ArgumentException("An optimizer requires at least one batch.", nameof(batches));
        }

        _batches = batches;
    }

    /// <summary>
    /// Optimizes an analyzed <paramref name="analyzedPlan"/>, returning an equivalent, cheaper plan.
    /// </summary>
    /// <param name="analyzedPlan">A resolved (analyzed) logical plan.</param>
    /// <returns>The optimized logical plan (the same instance when no rule fired).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="analyzedPlan"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="analyzedPlan"/> is not resolved.</exception>
    public LogicalPlan Optimize(LogicalPlan analyzedPlan)
    {
        ArgumentNullException.ThrowIfNull(analyzedPlan);
        if (!analyzedPlan.Resolved)
        {
            throw new InvalidOperationException("Optimize requires an analyzed (resolved) plan.");
        }

        LogicalPlan current = analyzedPlan;
        foreach (RuleBatch batch in _batches)
        {
            current = RunBatch(batch, current);
        }

        return current;
    }

    /// <summary>
    /// The M1 optimization pipeline: a single "Operator Optimization" fixpoint batch. Constant
    /// folding is co-located <b>inside</b> this batch (not a separate earlier batch) so the whole
    /// pipeline reaches a <b>global</b> fixpoint: after <see cref="CombineFilters"/> synthesizes an
    /// <c>And(c1, c2)</c> of two boolean literals, the next sweep's <see cref="ConstantFolding"/>
    /// folds it, and the sweep after that is a no-op — which is what makes <c>Optimize</c> idempotent
    /// (design doc §5).
    /// </summary>
    private static RuleBatch[] DefaultBatches(int maxIterations) =>
    [
        new RuleBatch(
            "Operator Optimization",
            RuleStrategy.FixedPoint,
            maxIterations,
            new ConstantFolding(),
            new CombineFilters(),
            new PushPredicateThroughProject(),
            new ColumnPruning()),
    ];

    private static LogicalPlan RunBatch(RuleBatch batch, LogicalPlan plan)
    {
        LogicalPlan current = plan;
        int iteration = 0;
        while (true)
        {
            LogicalPlan start = current;
            foreach (Rule rule in batch.Rules)
            {
                current = rule.Apply(current)
                    ?? throw new InvalidOperationException(
                        $"Optimization rule '{rule.Name}' returned null.");
            }

            iteration++;
            if (batch.Strategy == RuleStrategy.Once)
            {
                break;
            }

            // Structural-equality fixpoint (Catalyst's fastEquals): a no-op sweep means convergence.
            if (current.Equals(start))
            {
                break;
            }

            if (iteration >= batch.MaxIterations)
            {
                // A fixpoint batch that exits via the safety valve (rather than a no-op sweep) has
                // NOT converged: it may still be mid-rewrite. That is a rule/ordering bug, so surface
                // it loudly in DEBUG/test builds; in Release, defensively return the best-effort plan
                // (a partially-optimized plan is still semantically equivalent).
#if DEBUG
                throw new InvalidOperationException(
                    $"Optimization batch '{batch.Name}' did not converge within "
                    + $"{batch.MaxIterations} iterations.");
#else
                break;
#endif
            }
        }

        return current;
    }
}
