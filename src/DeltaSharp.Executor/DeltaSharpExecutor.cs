namespace DeltaSharp.Executor;

/// <summary>
/// The public, idempotent bootstrap for DeltaSharp's execution backend. Calling
/// <see cref="Enable"/> registers the Executor's query-execution backend on
/// <see cref="SparkSession"/> so DataFrame <b>actions</b> (<c>Collect</c>/<c>Count</c>/<c>Show</c>)
/// run — including for a program that only ever touches <c>DeltaSharp.Core</c> public types (exactly
/// what <see cref="SparkSession.CreateDataFrame(System.Collections.Generic.IEnumerable{Row}, DeltaSharp.Types.StructType)"/>
/// enables).
/// </summary>
/// <remarks>
/// <para>
/// The backend is also registered automatically by a <c>[ModuleInitializer]</c> — but that only fires
/// the first time <b>an Executor type is used</b>. A Core-only program (create an in-memory DataFrame,
/// then <c>Collect</c>/<c>Count</c>) never touches an Executor type, so the module initializer never
/// runs and the action would otherwise fail with "No execution backend is registered." Call
/// <see cref="Enable"/> once at startup to guarantee execution is wired up regardless.
/// </para>
/// <para>
/// <b>Not a governed public-API baseline change.</b> <c>DeltaSharp.Executor</c> is a non-packable,
/// engine-internal assembly (its <c>public</c> types are engine-internal and carry no
/// <c>PublicAPI.*.txt</c> baseline), so this entry point is documented here rather than tracked in a
/// shipped public-API surface. It exists purely so an application (or a test) can force registration
/// deterministically.
/// </para>
/// </remarks>
public static class DeltaSharpExecutor
{
    /// <summary>Registers the Executor's query-execution backend on <see cref="SparkSession"/>, making
    /// DataFrame actions runnable. Idempotent and safe to call multiple times (and from any thread); it
    /// installs the same factory the module initializer does.</summary>
    public static void Enable() => ExecutorRegistration.Register();
}
