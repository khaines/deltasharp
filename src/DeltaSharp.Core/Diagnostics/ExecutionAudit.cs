using System.Threading;

namespace DeltaSharp.Diagnostics;

/// <summary>
/// A milestone on the eager execution pipeline, recorded through <see cref="IExecutionAudit"/> so a
/// test (or, later, a diagnostic listener) can observe the ordered analyzer → planner → backend path
/// that an <b>action</b> drives. Constructing a logical plan or a transformation never enters a stage.
/// </summary>
/// <remarks>
/// The values are ordered to match the pipeline: the analyzer resolves the plan, the planner turns
/// the optimized logical plan into a physical plan, and the backend executes it. STORY-04.4.3 (#169)
/// establishes this substrate; the real analyzer/planner/backend bridge (#173/#174) records each
/// stage as it is entered.
/// </remarks>
internal enum ExecutionStage
{
    /// <summary>The analyzer began resolving an unresolved logical plan against the catalog.</summary>
    Analyzer,

    /// <summary>The planner began translating the optimized logical plan into a physical plan.</summary>
    Planner,

    /// <summary>The execution backend was invoked to run the physical plan.</summary>
    Backend,
}

/// <summary>
/// The internal instrumentation sink that real source readers (EPIC-02/03 and the future
/// <c>SparkSession.Read</c> door, #158) and the execution backend bridge (#173/#174) notify at
/// observation points that <b>only ever occur during eager execution</b> — a file being opened, rows
/// being read, or a pipeline stage being entered.
/// </summary>
/// <remarks>
/// <para>
/// This is the wiring point that proves DeltaSharp's central invariant — <b>transformations are lazy,
/// actions are eager</b>. Building a logical plan or applying a transformation must never notify a
/// sink; only an action's execution may. Tests install a recording sink (see
/// <see cref="ExecutionAudit.BeginScope(IExecutionAudit)"/>) and assert the counters stay zero while
/// only transformations run, and that the expected stage path appears when an action executes.
/// </para>
/// <para>
/// The seam is deliberately <see langword="internal"/>: it is an implementation detail of the engine,
/// not a public API, so it adds nothing to the PublicAPI baseline.
/// </para>
/// </remarks>
internal interface IExecutionAudit
{
    /// <summary>Notifies the sink that a source reader opened a physical file or relation.</summary>
    /// <param name="source">A short, stable identifier of the opened source (for diagnostics).</param>
    void OnFileOpened(string source);

    /// <summary>Notifies the sink that a source reader produced rows.</summary>
    /// <param name="count">The number of rows read (may be a per-batch increment).</param>
    void OnRowsRead(long count);

    /// <summary>Notifies the sink that the eager pipeline entered <paramref name="stage"/>.</summary>
    /// <param name="stage">The pipeline milestone that was entered.</param>
    void OnStageEntered(ExecutionStage stage);
}

/// <summary>
/// The ambient accessor for the current <see cref="IExecutionAudit"/> sink. Observation points call
/// the static forwarders (<see cref="FileOpened(string)"/>, <see cref="RowsRead(long)"/>,
/// <see cref="StageEntered(ExecutionStage)"/>); each forwards to the sink installed for the current
/// asynchronous control flow, or does nothing when none is installed (the zero-overhead production
/// default until an action installs one).
/// </summary>
/// <remarks>
/// <para>
/// The current sink is held in an <see cref="AsyncLocal{T}"/> so it is isolated per test / per
/// executing action and flows across <c>await</c> boundaries without a lock. A recording sink counts
/// with <see cref="Interlocked"/> and so is safe to share across the threads of a single execution.
/// The forwarders allocate nothing and read a single <see cref="AsyncLocal{T}"/> slot; when no sink
/// is installed they compile down to a null check.
/// </para>
/// <para>
/// This type performs <b>no</b> query work itself. It is the observation substrate that lets the
/// lazy/eager regression tests distinguish plan construction from execution.
/// </para>
/// </remarks>
internal static class ExecutionAudit
{
    private static readonly AsyncLocal<IExecutionAudit?> _current = new();

    /// <summary>The sink installed for the current asynchronous control flow, or <see langword="null"/>.</summary>
    internal static IExecutionAudit? Current => _current.Value;

    /// <summary>
    /// Installs <paramref name="sink"/> as the current sink until the returned scope is disposed,
    /// restoring the previously installed sink (supporting nesting). Intended for tests and, later,
    /// for the executor to scope a diagnostic listener to a single action's execution.
    /// </summary>
    /// <param name="sink">The sink to make current.</param>
    /// <returns>A scope that restores the previous sink on disposal.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    internal static AuditScope BeginScope(IExecutionAudit sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        IExecutionAudit? previous = _current.Value;
        _current.Value = sink;
        return new AuditScope(previous);
    }

    /// <summary>Forwards a file-opened observation to the current sink, if any.</summary>
    /// <param name="source">A short, stable identifier of the opened source.</param>
    internal static void FileOpened(string source) => _current.Value?.OnFileOpened(source);

    /// <summary>Forwards a rows-read observation to the current sink, if any.</summary>
    /// <param name="count">The number of rows read.</param>
    internal static void RowsRead(long count) => _current.Value?.OnRowsRead(count);

    /// <summary>Forwards a stage-entered observation to the current sink, if any.</summary>
    /// <param name="stage">The pipeline milestone that was entered.</param>
    internal static void StageEntered(ExecutionStage stage) => _current.Value?.OnStageEntered(stage);

    /// <summary>
    /// Restores the previously installed <see cref="IExecutionAudit"/> sink when disposed. Returned by
    /// <see cref="BeginScope(IExecutionAudit)"/>; use it with a <c>using</c> statement.
    /// </summary>
    internal readonly struct AuditScope : IDisposable
    {
        private readonly IExecutionAudit? _previous;

        internal AuditScope(IExecutionAudit? previous) => _previous = previous;

        /// <summary>Restores the sink that was current before the scope was opened.</summary>
        public void Dispose() => _current.Value = _previous;
    }
}
