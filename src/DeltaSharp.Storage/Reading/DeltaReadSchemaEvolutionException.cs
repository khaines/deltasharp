namespace DeltaSharp.Storage;

/// <summary>
/// The public, fail-closed failure the Delta <b>read</b> facade (<see cref="DeltaReadSource"/>, #499)
/// raises when a data file was written under an <b>older, narrower table schema</b> (additive schema
/// evolution, #190) than the current snapshot's schema, so reading the current-schema columns requires
/// <b>read-side null-fill</b> of the later-added column(s) — which is not yet implemented (issue
/// <b>#497</b>, "Evolved-column read null-fill"). Rather than surface the misleading raw storage error
/// ("column … not present in the Parquet file schema", which reads like corruption), the read facade fails
/// <b>closed</b> with this clear, actionable error — the exact mirror of OPTIMIZE's
/// <c>OptimizeSchemaEvolutionException</c> (#195/#497). #499 is deliberately scoped to base and time-travel
/// reads of non-evolved / current-schema files; evolved-column read null-fill lands with #497.
/// </summary>
public sealed class DeltaReadSchemaEvolutionException : Exception
{
    /// <summary>Creates the fail-closed error for the narrow input <paramref name="filePath"/>.</summary>
    /// <param name="filePath">The active data file whose physical schema is narrower than the snapshot schema.</param>
    /// <param name="innerException">The originating storage exception (the absent-column read failure).</param>
    public DeltaReadSchemaEvolutionException(string filePath, Exception innerException)
        : base(
            $"Cannot read Delta data file '{filePath}' because it was written under an older table schema "
            + "and read-side null-fill for the later-added column(s) is not yet implemented (issue #497). "
            + "The read fails closed rather than fabricating values for an evolved file. #499 supports base "
            + "and time-travel reads of non-evolved / current-schema files; re-try once #497 (evolved-column "
            + "read null-fill) lands.",
            innerException) => FilePath = filePath;

    /// <summary>The data file whose physical schema is narrower than the current snapshot schema.</summary>
    public string FilePath { get; }
}
