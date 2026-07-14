namespace DeltaSharp.Storage;

/// <summary>
/// The public, fail-closed failure the Delta <b>read</b> facade (<see cref="DeltaReadSource"/>, #499)
/// raises when a data file is missing a <b>required (non-nullable)</b> column the current snapshot schema
/// demands — a genuine schema incompatibility that read-side null-fill <b>cannot</b> satisfy (a required
/// lane can never carry <c>null</c>). Additive schema evolution (#190) only ever adds <b>nullable</b>
/// columns, which the read path <b>null-fills</b> for older files that predate them (#497); this error is
/// therefore reserved for the residual, genuinely-incompatible case (e.g. a corrupt/foreign file, or a file
/// missing a column that is required under the current schema). Rather than surface the misleading raw
/// storage error ("column … not present in the Parquet file schema", which reads like corruption), the read
/// facade fails <b>closed</b> with this clear, actionable error — the mirror of OPTIMIZE's
/// <c>OptimizeSchemaEvolutionException</c>.
/// </summary>
public sealed class DeltaReadSchemaEvolutionException : Exception
{
    /// <summary>Creates the fail-closed error for the narrow input <paramref name="filePath"/>.</summary>
    /// <param name="filePath">The active data file that is missing a required column the current schema demands.</param>
    /// <param name="innerException">The originating storage exception (the absent-column read failure).</param>
    public DeltaReadSchemaEvolutionException(string filePath, Exception innerException)
        : base(
            $"Cannot read Delta data file '{filePath}' because it is missing a required (non-nullable) column "
            + "the current table schema demands, which read-side null-fill cannot satisfy (only later-added "
            + "nullable columns are null-filled, #497). The read fails closed rather than fabricating a value "
            + "for a required column.",
            innerException) => FilePath = filePath;

    /// <summary>The data file whose physical schema is narrower than the current snapshot schema.</summary>
    public string FilePath { get; }
}
