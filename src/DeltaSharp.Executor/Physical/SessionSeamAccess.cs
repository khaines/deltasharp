namespace DeltaSharp.Executor;

/// <summary>
/// Test-only accessors that cross the Core↔Executor internals seam so
/// <c>DeltaSharp.Executor.Tests</c> (which sees Executor internals but not Core's) can assert on a
/// <see cref="SparkSession"/>'s internal <c>QueryExecutor</c>.
/// </summary>
internal static class SessionSeamAccess
{
    /// <summary>The runtime type name of the executor a session resolved (proves registration).</summary>
    /// <param name="session">The session to inspect.</param>
    /// <returns>The executor's type name (for example <c>LocalQueryExecutor</c>).</returns>
    public static string ExecutorTypeName(SparkSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.QueryExecutor.GetType().Name;
    }
}
