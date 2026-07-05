using System.Buffers;
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
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

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
