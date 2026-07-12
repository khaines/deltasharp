using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Serializes a <see cref="StructType"/> schema to the Spark/Delta-compatible schema JSON that the
/// Parquet footer <c>key_value_metadata</c> carries (design §2.9.2 "footer metadata"). It mirrors
/// the engine's <c>SchemaJson</c> format for the atomic/decimal types this layer supports; a
/// standalone copy lives here because <c>SchemaJson</c> is <c>internal</c> to
/// <c>DeltaSharp.Engine</c> (design §2.10.1 handoff note: relocate/grant is a later story).
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

    // Faithful copy of DeltaSharp.Abstractions' SchemaJson.WriteMetadata (M7): each field-metadata
    // entry is emitted in FieldMetadata's key-sorted order with a typed value, so a schema carrying
    // field metadata serializes byte-for-byte like the engine — including numeric column-mapping ids
    // and identity booleans, which MUST stay unquoted JSON numbers/booleans for Delta interop (#330).
    // D-6 relocation follow-up: once SchemaJson is shared out of Abstractions, delete this copy and
    // call the shared serializer directly.
    private static void WriteMetadata(Utf8JsonWriter writer, FieldMetadata metadata)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<string, MetadataValue> entry in metadata)
        {
            // FieldMetadata enumerates in sorted key order => deterministic output.
            writer.WritePropertyName(entry.Key);
            WriteMetadataValue(writer, entry.Value);
        }

        writer.WriteEndObject();
    }

    // Mirrors SchemaJson.WriteMetadataValue byte-for-byte (string/long/double/bool/null/array/nested).
    private static void WriteMetadataValue(Utf8JsonWriter writer, MetadataValue value)
    {
        switch (value.Kind)
        {
            case MetadataValueKind.Null:
                writer.WriteNullValue();
                break;
            case MetadataValueKind.String:
                writer.WriteStringValue(value.AsString());
                break;
            case MetadataValueKind.Long:
                writer.WriteNumberValue(value.AsLong());
                break;
            case MetadataValueKind.Double:
                WriteDouble(writer, value.AsDouble());
                break;
            case MetadataValueKind.Boolean:
                writer.WriteBooleanValue(value.AsBoolean());
                break;
            case MetadataValueKind.Array:
                writer.WriteStartArray();
                foreach (MetadataValue element in value.AsArray())
                {
                    WriteMetadataValue(writer, element);
                }

                writer.WriteEndArray();
                break;
            case MetadataValueKind.Nested:
                WriteMetadata(writer, value.AsNested());
                break;
            default:
                throw new SchemaValidationException($"Cannot serialize metadata value kind '{value.Kind}'.");
        }
    }

    // Mirrors SchemaJson.WriteDouble: round-trippable ("R") with a forced fractional part when
    // integral so a double never collapses to a bare integer literal.
    private static void WriteDouble(Utf8JsonWriter writer, double value)
    {
        if (!double.IsFinite(value))
        {
            writer.WriteNumberValue(value);
            return;
        }

        string text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.IndexOfAny(FractionOrExponent) < 0)
        {
            text += ".0";
        }

        writer.WriteRawValue(text);
    }

    private static readonly char[] FractionOrExponent = ['.', 'e', 'E'];

    private static void WriteType(Utf8JsonWriter writer, DataType type)
    {
        if (type is StructType nested)
        {
            WriteStruct(writer, nested);
            return;
        }

        // Atomic and decimal types serialize as their Spark type-name string.
        writer.WriteStringValue(type.TypeName);
    }
}
