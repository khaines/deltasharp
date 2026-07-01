namespace DeltaSharp.Types;

/// <summary>
/// Selects the strictness lens for type coercion, decimal/timestamp arithmetic, and casts —
/// the engine analog of Spark's <c>spark.sql.ansi.enabled</c> session flag (STORY-02.5.2).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Ansi"/> is the documented default (ADR-0007/ADR-0008 make ANSI the semantic
/// lens): operations that exceed a target precision/scale or fall outside a type's range
/// <b>report overflow</b> instead of truncating. <see cref="Legacy"/> mirrors Spark's
/// non-ANSI behavior, where the same operations <b>return null</b> rather than throwing.
/// </para>
/// <para>
/// Neither mode silently truncates or wraps a decimal: the only difference is whether
/// out-of-range maps to an <see cref="ArithmeticOverflowException"/> (<see cref="Ansi"/>) or
/// to a SQL <c>NULL</c> (<see cref="Legacy"/>). This is a value, not ambient state, so it is
/// threaded explicitly into every coercion-sensitive operation and stays deterministic.
/// </para>
/// </remarks>
public enum AnsiMode
{
    /// <summary>
    /// ANSI semantics (default): overflow and out-of-range casts raise
    /// <see cref="ArithmeticOverflowException"/> instead of producing a silently wrong value.
    /// </summary>
    Ansi = 0,

    /// <summary>
    /// Spark legacy (non-ANSI) semantics: overflow and out-of-range casts yield SQL
    /// <c>NULL</c>. DeltaSharp never wraps; it nulls, matching Spark's non-ANSI path.
    /// </summary>
    Legacy = 1,
}
