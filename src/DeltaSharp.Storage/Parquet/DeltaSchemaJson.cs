using DeltaSharp.Types;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Carries the Parquet footer <c>key_value_metadata</c> keys (design §2.9.2 "footer metadata") and
/// serializes a <see cref="StructType"/> schema to the Spark/Delta-compatible schema JSON stored
/// under <see cref="SchemaMetadataKey"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type holds NO serializer of its own: <see cref="ToJson"/> delegates to the engine's
/// canonical <c>SchemaJson.ToJson</c> in <c>DeltaSharp.Abstractions</c> (visible here through the
/// existing <c>InternalsVisibleTo</c> grant), which is the same writer <c>DeltaTableWriter</c> uses
/// for the Delta log <c>metaData.schemaString</c>. Storage previously maintained a byte-for-byte
/// copy of <c>WriteType</c>/<c>WriteStruct</c>/<c>WriteMetadata</c>, guarded against drift only by
/// the footer↔log byte-parity tests (#518); #679 removes that copy so the footer schema string and
/// the log schema string are produced by a single source of truth and CANNOT drift structurally —
/// including for complex types (array/map/nested struct) and typed field metadata such as the
/// numeric column-mapping ids stamped into nested trees (#191/#676, #330).
/// </para>
/// <para>The shared serializer uses the reflection-free <c>Utf8JsonWriter</c> so this layer stays
/// trim/AOT-clean (ADR-0014).</para>
/// </remarks>
internal static class DeltaSchemaJson
{
    /// <summary>The footer metadata key under which the schema JSON is written (Spark parity).</summary>
    public const string SchemaMetadataKey = "org.apache.spark.sql.parquet.row.metadata";

    /// <summary>The footer metadata key carrying the writer identity.</summary>
    public const string WriterMetadataKey = "deltasharp.writer";

    /// <summary>Serializes <paramref name="schema"/> to Spark/Delta schema JSON.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    public static string ToJson(StructType schema)
    {
        // Null-check locally so the thrown paramName stays "schema" for this call site rather than
        // the shared serializer's parameter name; the delegation below is otherwise unconditional.
        ArgumentNullException.ThrowIfNull(schema);
        return SchemaJson.ToJson(schema);
    }
}
