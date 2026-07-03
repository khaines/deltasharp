using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Analysis;

/// <summary>
/// A deterministic, monotonic source of <see cref="ExprId"/> identities for a single analyze pass.
/// </summary>
/// <remarks>
/// <para>
/// Catalyst allocates <c>ExprId</c>s from a global <c>AtomicLong</c>; DeltaSharp instead seeds a
/// fresh counter at the start of every <see cref="Analyzer.Resolve(DeltaSharp.Plans.Logical.LogicalPlan)"/> call so the ids a plan
/// receives depend only on the plan's structure and the deterministic order the analyzer walks it —
/// never on wall-clock time, a GUID, or process-global state. Identical input plans therefore
/// resolve to byte-identical output across runs and machines, which reproducible planning, plan
/// caching, and golden-file tests require.
/// </para>
/// <para>
/// The counter is intentionally <b>not</b> thread-safe: a single <see cref="Analyzer.Resolve(DeltaSharp.Plans.Logical.LogicalPlan)"/> call
/// resolves one plan on one thread, matching #167/#168's single-threaded-resolution assumption. No
/// <c>Guid.NewGuid</c> or <c>System.Random</c> is used, keeping the analyzer
/// BannedApiAnalyzers-clean.
/// </para>
/// </remarks>
internal sealed class ExprIdGenerator
{
    private long _next;

    /// <summary>Allocates the next monotonically-increasing identity, starting from <c>0</c>.</summary>
    public ExprId Next() => new(_next++);
}
