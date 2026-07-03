namespace DeltaSharp;

/// <summary>
/// Selects how <see cref="DataFrame.Explain(ExplainMode)"/> renders a query's plan, mirroring Apache
/// Spark's <c>org.apache.spark.sql.execution.ExplainMode</c> (the <c>explain(mode)</c> string values
/// <c>"simple"</c>, <c>"extended"</c>, <c>"codegen"</c>, <c>"cost"</c>, <c>"formatted"</c>).
/// </summary>
/// <remarks>
/// No mode triggers execution: logical/analyzed/optimized rendering happens entirely in
/// <c>DeltaSharp.Core</c>, and physical rendering plans the query (through the executor seam) but never
/// runs it (ADR-0001, lazy/eager invariant). See
/// <c>docs/engineering/design/explain.md</c>.
/// </remarks>
public enum ExplainMode
{
    /// <summary>Renders only the physical plan (Spark's default <c>explain()</c>).</summary>
    Simple = 0,

    /// <summary>Renders the parsed (unresolved), analyzed, optimized, and physical plans, each in its
    /// own section (Spark's <c>explain(true)</c>).</summary>
    Extended = 1,

    /// <summary>Renders the physical plan plus a diagnostic note: whole-stage codegen is not part of
    /// the M1 interpreted backend (ADR-0001).</summary>
    Codegen = 2,

    /// <summary>Renders the logical/physical plans plus a diagnostic note: cost statistics are not
    /// collected in M1.</summary>
    Cost = 3,

    /// <summary>Renders the physical plan (Spark's formatted per-node detail sections are deferred with
    /// execution metrics, #176).</summary>
    Formatted = 4,
}
