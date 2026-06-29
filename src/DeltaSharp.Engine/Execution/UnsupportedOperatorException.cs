namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Thrown when an <see cref="IExecutionBackend"/> is asked to evaluate an operator shape it does
/// not support (STORY-03.1.1 AC3). The contract is deliberately fail-fast: a backend that cannot
/// vectorize a node raises this <b>instead of</b> silently switching to a row-at-a-time
/// interpreter. Row-at-a-time fallback is banned because it would mask plan-regression and parity
/// gaps and quietly forfeit columnar performance and cost bounds — the planner must instead pick a
/// supported shape or surface the gap to the user.
/// </summary>
public sealed class UnsupportedOperatorException : NotSupportedException
{
    /// <summary>The unsupported operator kind, when known.</summary>
    public OperatorKind? Kind { get; }

    /// <summary>The backend that declined the operator, for diagnostics.</summary>
    public string? BackendName { get; }

    /// <summary>Creates the exception with a precise, backend-attributed message.</summary>
    /// <param name="kind">The operator kind that is not supported.</param>
    /// <param name="backendName">The reporting backend's <see cref="IExecutionBackend.Name"/>.</param>
    /// <param name="detail">Optional extra detail (e.g. the unsupported sub-shape).</param>
    public UnsupportedOperatorException(OperatorKind kind, string backendName, string? detail = null)
        : base(BuildMessage(kind, backendName, detail))
    {
        Kind = kind;
        BackendName = backendName;
    }

    /// <summary>Creates the exception from a free-form message (no row-at-a-time fallback occurs).</summary>
    /// <param name="message">A precise description of the unsupported shape.</param>
    public UnsupportedOperatorException(string message)
        : base(message)
    {
    }

    private static string BuildMessage(OperatorKind kind, string backendName, string? detail)
    {
        string suffix = string.IsNullOrEmpty(detail) ? string.Empty : $" ({detail})";
        return $"Backend '{backendName}' does not support operator '{kind}'{suffix}. " +
            "No row-at-a-time fallback is provided; the planner must choose a supported physical shape.";
    }
}
