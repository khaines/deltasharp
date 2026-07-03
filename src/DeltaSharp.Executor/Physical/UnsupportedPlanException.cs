namespace DeltaSharp.Executor;

/// <summary>
/// Thrown by the physical planner when an analyzed logical plan node (or an expression it carries)
/// has no M1 mapping onto an EPIC-03 executable operator. It is the <b>deterministic
/// unsupported-plan diagnostic</b> required by STORY-04.6.2: physical planning either maps a node to
/// a real operator or fails loudly here, naming the offending node — it never silently produces a
/// wrong plan or degrades to an approximation.
/// </summary>
public sealed class UnsupportedPlanException : Exception
{
    /// <summary>Creates the exception with a message naming the unsupported node/expression.</summary>
    /// <param name="message">A diagnostic identifying what could not be planned and why.</param>
    public UnsupportedPlanException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    /// <param name="message">A diagnostic identifying what could not be planned and why.</param>
    /// <param name="innerException">The underlying cause.</param>
    public UnsupportedPlanException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Builds a diagnostic for an unsupported logical-plan operator.</summary>
    /// <param name="nodeName">The logical node name (for example <c>Intersect</c>).</param>
    /// <param name="reason">Why it cannot be planned in M1.</param>
    /// <returns>A ready-to-throw exception.</returns>
    public static UnsupportedPlanException ForNode(string nodeName, string reason) =>
        new($"Physical planning does not support logical operator '{nodeName}': {reason}.");

    /// <summary>Builds a diagnostic for an unsupported expression.</summary>
    /// <param name="expressionName">The expression node name.</param>
    /// <param name="reason">Why it cannot be translated in M1.</param>
    /// <returns>A ready-to-throw exception.</returns>
    public static UnsupportedPlanException ForExpression(string expressionName, string reason) =>
        new($"Physical planning does not support expression '{expressionName}': {reason}.");
}
