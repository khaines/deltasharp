using System.Runtime.CompilerServices;

namespace DeltaSharp.Executor;

/// <summary>
/// Registers the Executor's <see cref="LocalQueryExecutor"/> as the <see cref="IQueryExecutor"/>
/// factory on <see cref="SparkSession"/>, so any session created in a process that references
/// DeltaSharp.Executor executes queries for real (against the process-wide
/// <see cref="InMemoryScanSource.Default"/> data-in seam; the public read-door is STORY-04.1.2 / #158).
/// </summary>
/// <remarks>
/// The <see cref="ModuleInitializerAttribute"/> runs the first time any Executor type is touched
/// (which includes the test host loading the assembly), so no explicit bootstrap call is required for
/// a program that already uses an Executor type. A program that reaches an action through <b>only</b>
/// <c>DeltaSharp.Core</c> public types (for example <c>CreateDataFrame(...).Collect()</c>) never
/// touches an Executor type, so it must call the public <see cref="DeltaSharpExecutor.Enable"/>
/// bootstrap (which delegates here). <see cref="Register"/> is idempotent.
/// </remarks>
internal static class ExecutorRegistration
{
    /// <summary>Registers the local executor factory on <see cref="SparkSession"/> (idempotent).</summary>
    [ModuleInitializer]
    public static void Register() =>
        SparkSession.RegisterQueryExecutorFactory(
            static session => new LocalQueryExecutor(InMemoryScanSource.Default, session));
}
