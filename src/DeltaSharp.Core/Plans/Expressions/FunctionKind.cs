namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// Whether a bound function is a <b>scalar</b> function (one output row per input row) or an
/// <b>aggregate</b> function (one output row per group). Spark parity: the distinction between an
/// <c>Expression</c> and an <c>AggregateFunction</c>. The analyzer (STORY-04.5.2 / #171) classifies
/// every <see cref="UnresolvedFunction"/> into one of these by its canonical name, and enforces that
/// aggregate functions appear only in a valid aggregate context.
/// </summary>
internal enum FunctionKind
{
    /// <summary>A scalar function — evaluated per row (e.g. <c>upper</c>, <c>coalesce</c>).</summary>
    Scalar,

    /// <summary>An aggregate function — evaluated per group (e.g. <c>sum</c>, <c>count</c>).</summary>
    Aggregate,
}
