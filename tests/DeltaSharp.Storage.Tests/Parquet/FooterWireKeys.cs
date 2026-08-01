namespace DeltaSharp.Storage.Tests;

/// <summary>
/// The Parquet footer <c>key_value_metadata</c> keys, <b>transcribed as literals</b> rather than
/// imported from <c>DeltaSchemaJson</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never replace these with the production constants.</b> The point of this type is that they
/// are NOT the production constants.
/// </para>
/// <para>
/// Reading a footer back through <c>DeltaSchemaJson.SchemaMetadataKey</c> — the same symbol the
/// writer stamps with — makes the lookup succeed no matter what that symbol contains. Deleting one
/// character from it leaves the serializer, its input and the emitted bytes all correct, moves only
/// the wire identifier, and ships green: Spark, delta-rs and Trino then find <b>no schema at all</b>
/// in the footer, which is worse than a divergent one. Measured, not theorised — a one-character
/// deletion at <c>DeltaSchemaJson.cs:28</c> was 0 kills solution-wide before this type existed.
/// </para>
/// <para>
/// That is the tautology this PR exists to delete, moved from the value to the KEY. The rule it
/// generalises to: a source shared between the prober and the probed is safe when it sits
/// <b>outside</b> both and unsafe when it sits <b>between</b> them. The production constant sits
/// between, so the test side keeps its own copy and
/// <c>ParquetWriterTests.FooterMetadataKeys_AreTheWireLiterals</c> asserts the two agree.
/// </para>
/// </remarks>
internal static class FooterWireKeys
{
    /// <summary>
    /// Spark's schema key, fixed by the Parquet/Spark interop contract rather than by us: it is
    /// what every external reader looks under, so it can never be changed to suit our code.
    /// </summary>
    internal const string Schema = "org.apache.spark.sql.parquet.row.metadata";

    /// <summary>Our own writer-identity key. Ours to choose, but not ours to change silently.</summary>
    internal const string Writer = "deltasharp.writer";
}
