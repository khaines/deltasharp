namespace DeltaSharp;

/// <summary>
/// Selects which DeltaSharp execution backend a session should use when an action runs
/// (Apache Spark has no equivalent concept; this is a DeltaSharp addition that surfaces the
/// pluggable-backend choice from <see href="https://github.com/khaines/deltasharp/blob/main/docs/adr/0001-execution-strategy.md">ADR-0001</see>).
/// </summary>
/// <remarks>
/// <para>
/// The value is <b>recorded</b> on the <see cref="SparkSession"/> at creation and is inert until an
/// action consumes it — selecting a backend performs no work during session creation or
/// transformations, preserving DeltaSharp's lazy/eager invariant. Configure it through the
/// <c>spark.deltasharp.execution.backend</c> session config key (case-insensitive values
/// <c>auto</c>, <c>interpreted</c>, <c>compiled</c>) and read it back from
/// <see cref="SparkSession.ExecutionBackend"/>.
/// </para>
/// <para>
/// The physical-planning bridge maps this value to the engine's backend options at action time; see
/// <c>docs/engineering/design/sparksession-lifecycle.md</c>.
/// </para>
/// </remarks>
public enum ExecutionBackend
{
    /// <summary>
    /// Defer to the runtime: use the optional compiled tier when dynamic code is supported, otherwise
    /// the always-available interpreted vectorized backend. This is the default.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Force the interpreted vectorized backend even on a runtime that supports dynamic code. Useful
    /// for determinism, debugging, and differential parity testing (ADR-0001's force-interpreter
    /// override).
    /// </summary>
    Interpreted = 1,

    /// <summary>
    /// Prefer the optional compiled tier. Best-effort: when the runtime does not support dynamic code
    /// (for example under Native AOT) execution falls back to the interpreted backend.
    /// </summary>
    Compiled = 2,
}
