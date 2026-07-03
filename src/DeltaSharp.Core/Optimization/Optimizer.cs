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
/// <see cref="Optimize"/> is deterministic (fixed batch/rule order), idempotent (every batch runs to
/// a fixpoint), and terminating (each fixpoint batch stops at a structural fixpoint or after
/// <see cref="DefaultMaxIterations"/>).
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
    public LogicalPlan Optimize(LogicalPlan analyzedPlan)
    {
        ArgumentNullException.ThrowIfNull(analyzedPlan);

        LogicalPlan current = analyzedPlan;
        foreach (RuleBatch batch in _batches)
        {
            current = RunBatch(batch, current);
        }

        return current;
    }

    /// <summary>The M1 optimization pipeline: constant folding, then operator optimization.</summary>
    private static RuleBatch[] DefaultBatches(int maxIterations) =>
    [
        new RuleBatch(
            "ConstantFolding",
            RuleStrategy.FixedPoint,
            maxIterations,
            new ConstantFolding()),
        new RuleBatch(
            "Operator Optimization",
            RuleStrategy.FixedPoint,
            maxIterations,
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
                break;
            }
        }

        return current;
    }
}
