namespace DeltaSharp;

/// <summary>
/// The stage of local action execution a <see cref="QueryExecutionException"/> is attributed to
/// (STORY-04.6.4 / #176, criterion 2). It lets a caller — and diagnostics — tell <i>where</i> an
/// action failed without parsing messages: analyzer resolution, physical planning, scan resolution,
/// backend execution, or result materialization.
/// </summary>
/// <remarks>
/// This is a public, failure-oriented enum distinct from the internal
/// <c>DeltaSharp.Diagnostics.ExecutionStage</c> audit-milestone enum (which the #169 lazy/eager audit
/// substrate uses). See <c>docs/engineering/design/execution-boundaries.md</c> §3.
/// </remarks>
public enum QueryExecutionStage
{
    /// <summary>The analyzer was resolving the logical plan against the catalog (before the execution seam).</summary>
    Analyze,

    /// <summary>The physical planner was mapping the analyzed plan onto executable operators.</summary>
    Plan,

    /// <summary>A scan source was being resolved/opened for a relation.</summary>
    Scan,

    /// <summary>The execution backend was running the physical plan.</summary>
    Backend,

    /// <summary>The engine's column batches were being materialized into <see cref="Row"/>s.</summary>
    Materialize,
}
