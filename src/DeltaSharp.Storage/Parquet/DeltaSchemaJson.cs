using System.Buffers;
using System.Text;
using System.Text.Json;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Serializes a <see cref="StructType"/> schema to the Spark/Delta-compatible schema JSON that the
/// Parquet footer <c>key_value_metadata</c> carries (design §2.9.2 "footer metadata"). Field
/// metadata is serialized via the shared <c>SchemaJson.WriteMetadataValue</c> in
/// <c>DeltaSharp.Abstractions</c> (visible here through the <c>InternalsVisibleTo</c> grant), so
/// numeric column-mapping ids and identity numbers/booleans stay byte-for-byte identical to the
/// engine's serializer. Only <see cref="WriteType"/> differs — this layer stringifies complex
/// types (see the tracked deferral there).
/// </summary>
/// <remarks>Uses the reflection-free <see cref="Utf8JsonWriter"/> so the layer stays trim/AOT-clean
/// (ADR-0014).</remarks>
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
        ArgumentNullException.ThrowIfNull(schema);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteStruct(writer, schema);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteStruct(Utf8JsonWriter writer, StructType structType)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "struct");
        writer.WritePropertyName("fields");
        writer.WriteStartArray();
        foreach (StructField field in structType)
        {
            writer.WriteStartObject();
            writer.WriteString("name", field.Name);
            writer.WritePropertyName("type");
            WriteType(writer, field.DataType);
            writer.WriteBoolean("nullable", field.Nullable);
            writer.WritePropertyName("metadata");
            WriteMetadata(writer, field.Metadata);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // Emits each field-metadata entry in FieldMetadata's key-sorted order with a typed value, so a
    // schema carrying field metadata serializes byte-for-byte like the engine — including numeric
    // column-mapping ids and identity booleans, which MUST stay unquoted JSON numbers/booleans for
    // Delta interop (#330). The per-value writer is the shared SchemaJson.WriteMetadataValue in
    // DeltaSharp.Abstractions (no local copy) so the two serializers cannot drift.
    private static void WriteMetadata(Utf8JsonWriter writer, FieldMetadata metadata)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<string, MetadataValue> entry in metadata)
        {
            // FieldMetadata enumerates in sorted key order => deterministic output.
            writer.WritePropertyName(entry.Key);
            SchemaJson.WriteMetadataValue(writer, entry.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteType(Utf8JsonWriter writer, DataType type)
    {
        if (type is StructType nested)
        {
            WriteStruct(writer, nested);
            return;
        }

        // TRACKED DEFERRAL (#518): unlike the engine's SchemaJson.WriteType, this layer stringifies
        // ALL non-struct types (including Array/Map) to their TypeName rather than emitting the
        // nested elementType/keyType/valueType object shape. That footer complex-type gap is
        // pre-existing and out of scope for #330; #191 will need the full shape. Until then this
        // is intentionally NOT shared with SchemaJson.WriteType.
        writer.WriteStringValue(type.TypeName);
    }
}
