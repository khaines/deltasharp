namespace DeltaSharp;

/// <summary>
/// Thrown when a <see cref="DataFrame"/> action (<see cref="DataFrame.Collect()"/>,
/// <see cref="DataFrame.Count()"/>, <see cref="DataFrame.Show(int, bool)"/>) fails during eager
/// execution — for example when no execution backend is registered, or when the engine backend
/// (STORY-04.6.2 / #174) reports a runtime failure. It is the single public error type for the
/// execution stage, complementing the analyzer's resolution failures which surface earlier.
/// </summary>
/// <remarks>
/// <para>
/// A resolution error (an unknown column or table) is raised during analysis, before execution, so it
/// never reaches this stage. This exception therefore signals a problem <b>after</b> a plan resolved:
/// a missing backend, or a backend-reported execution fault.
/// </para>
/// <para>
/// STORY-04.6.4 (#176) adds stage attribution and diagnostics: <see cref="Stage"/> names the pipeline
/// stage that failed and the <see cref="System.Exception.InnerException"/> preserves the root cause
/// (criterion 2), while <see cref="Metrics"/> carries whatever planning/execution counters accumulated
/// before the fault (criterion 4). An unsupported plan shape or an unrepresentable materialization
/// value still surfaces as the executor's <c>UnsupportedPlanException</c> (which is itself
/// stage-attributed) rather than this type; see
/// <c>docs/engineering/design/execution-boundaries.md</c> §3.
/// </para>
/// </remarks>
public sealed class QueryExecutionException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public QueryExecutionException()
    {
    }

    /// <summary>Initializes a new instance with a precise <paramref name="message"/>.</summary>
    public QueryExecutionException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    public QueryExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a stage-attributed instance carrying the root cause and, when available, the
    /// counters gathered before the failure (STORY-04.6.4 / #176).
    /// </summary>
    /// <param name="stage">The pipeline stage that failed.</param>
    /// <param name="message">A diagnostic naming the failed stage and reason.</param>
    /// <param name="innerException">The root cause, preserved for diagnosis.</param>
    /// <param name="metrics">The planning/execution counters gathered before the failure, if any.</param>
    public QueryExecutionException(
        QueryExecutionStage stage,
        string message,
        Exception? innerException = null,
        ExecutionMetrics? metrics = null)
        : base(message, innerException)
    {
        Stage = stage;
        Metrics = metrics;
    }

    /// <summary>The pipeline stage that failed, or <see langword="null"/> when unattributed.</summary>
    public QueryExecutionStage? Stage { get; }

    /// <summary>The planning/execution counters gathered before the failure, or <see langword="null"/>.</summary>
    public ExecutionMetrics? Metrics { get; }
}
