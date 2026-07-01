using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeltaSharp.Types;

/// <summary>
/// Serializes a <see cref="DataType"/> tree to and from the Spark-compatible schema JSON (the
/// same representation Delta stores in its transaction log), so test fixtures and external
/// schema strings round-trip the type tree, nullability, and string metadata
/// (STORY-02.5.1 AC3).
/// </summary>
/// <remarks>
/// Uses the reflection-free <see cref="Utf8JsonWriter"/>/<see cref="JsonDocument"/> APIs so the
/// engine stays trim- and AOT-clean (ADR-0014). Atomic and decimal types serialize as a JSON
/// string (their <see cref="DataType.TypeName"/>); array/map/struct serialize as a JSON object.
/// </remarks>
internal static class SchemaJson
{
    /// <summary>
    /// Serializes a <see cref="DataType"/> tree to the Spark-compatible schema JSON (the same
    /// format Delta stores in its log), round-trippable with <see cref="FromJson(string)"/>
    /// (STORY-02.5.1 AC3).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    public static string ToJson(DataType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteType(writer, type);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Parses a type tree from Spark-compatible schema JSON produced by <see cref="ToJson(DataType)"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="SchemaValidationException">The JSON is malformed or describes an invalid/unknown type.</exception>
    public static DataType FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return ReadType(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new SchemaValidationException($"Invalid schema JSON: {ex.Message}", ex);
        }
    }

    private static void WriteType(Utf8JsonWriter writer, DataType type)
    {
        switch (type)
        {
            case ArrayType array:
                writer.WriteStartObject();
                writer.WriteString("type", "array");
                writer.WritePropertyName("elementType");
                WriteType(writer, array.ElementType);
                writer.WriteBoolean("containsNull", array.ContainsNull);
                writer.WriteEndObject();
                break;

            case MapType map:
                writer.WriteStartObject();
                writer.WriteString("type", "map");
                writer.WritePropertyName("keyType");
                WriteType(writer, map.KeyType);
                writer.WritePropertyName("valueType");
                WriteType(writer, map.ValueType);
                writer.WriteBoolean("valueContainsNull", map.ValueContainsNull);
                writer.WriteEndObject();
                break;

            case StructType structType:
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
                break;

            default:
                // Atomic and decimal types serialize as their type-name string. The default
                // arm fails fast so a future DataType added without a matching case cannot be
                // silently mis-serialized.
                if (type is AtomicType or DecimalType)
                {
                    writer.WriteStringValue(type.TypeName);
                    break;
                }

                throw new SchemaValidationException($"Cannot serialize unsupported type '{type.SimpleString}'.");
        }
    }

    private static void WriteMetadata(Utf8JsonWriter writer, FieldMetadata metadata)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<string, string> entry in metadata)
        {
            // FieldMetadata enumerates in sorted key order => deterministic output.
            writer.WriteString(entry.Key, entry.Value);
        }

        writer.WriteEndObject();
    }

    private static DataType ReadType(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return ParseNamedType(element.GetString()!);

            case JsonValueKind.Object:
                if (!element.TryGetProperty("type", out JsonElement typeProp)
                    || typeProp.ValueKind != JsonValueKind.String)
                {
                    throw new SchemaValidationException(
                        "Invalid type JSON: object is missing a string 'type' property.");
                }

                string kind = typeProp.GetString()!;
                return kind switch
                {
                    "array" => ReadArray(element),
                    "map" => ReadMap(element),
                    "struct" => ReadStruct(element),
                    _ => throw new SchemaValidationException($"Unknown complex type kind '{kind}'."),
                };

            default:
                throw new SchemaValidationException(
                    $"Invalid type JSON: unexpected token '{element.ValueKind}'.");
        }
    }

    private static DataType ParseNamedType(string name)
    {
        if (name.StartsWith("decimal(", StringComparison.Ordinal))
        {
            return ParseDecimal(name);
        }

        return name switch
        {
            "boolean" => BooleanType.Instance,
            "byte" => ByteType.Instance,
            "short" => ShortType.Instance,
            "integer" => IntegerType.Instance,
            "long" => LongType.Instance,
            "float" => FloatType.Instance,
            "double" => DoubleType.Instance,
            "string" => StringType.Instance,
            "binary" => BinaryType.Instance,
            "date" => DateType.Instance,
            "timestamp" => TimestampType.Instance,
            "void" or "null" => NullType.Instance,
            _ => throw new SchemaValidationException($"Unknown type name '{name}'."),
        };
    }

    private static DecimalType ParseDecimal(string name)
    {
        int open = name.IndexOf('(', StringComparison.Ordinal);
        int close = name.IndexOf(')', StringComparison.Ordinal);
        if (open < 0 || close <= open || close != name.Length - 1)
        {
            // The closing paren must be the final character — reject trailing garbage such as
            // "decimal(10,2) junk".
            throw new SchemaValidationException($"Malformed decimal type '{name}'.");
        }

        string inner = name[(open + 1)..close];
        string[] parts = inner.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int precision)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int scale))
        {
            throw new SchemaValidationException($"Malformed decimal type '{name}'.");
        }

        // Constructor re-validates precision/scale and throws SchemaValidationException on bad ranges.
        return new DecimalType(precision, scale);
    }

    private static ArrayType ReadArray(JsonElement element)
    {
        DataType elementType = ReadType(GetRequired(element, "elementType"));
        bool containsNull = GetRequiredBoolean(element, "containsNull");
        return new ArrayType(elementType, containsNull);
    }

    private static MapType ReadMap(JsonElement element)
    {
        DataType keyType = ReadType(GetRequired(element, "keyType"));
        DataType valueType = ReadType(GetRequired(element, "valueType"));
        bool valueContainsNull = GetRequiredBoolean(element, "valueContainsNull");
        return new MapType(keyType, valueType, valueContainsNull);
    }

    private static StructType ReadStruct(JsonElement element)
    {
        JsonElement fieldsElement = GetRequired(element, "fields");
        if (fieldsElement.ValueKind != JsonValueKind.Array)
        {
            throw new SchemaValidationException("Struct 'fields' must be a JSON array.");
        }

        var fields = new List<StructField>();
        foreach (JsonElement fieldElement in fieldsElement.EnumerateArray())
        {
            if (fieldElement.ValueKind != JsonValueKind.Object)
            {
                throw new SchemaValidationException(
                    $"Each struct field must be a JSON object, but found '{fieldElement.ValueKind}'.");
            }

            string name = GetRequiredString(fieldElement, "name");
            DataType type = ReadType(GetRequired(fieldElement, "type"));
            bool nullable = GetRequiredBoolean(fieldElement, "nullable");
            FieldMetadata metadata = ReadMetadata(fieldElement);
            fields.Add(new StructField(name, type, nullable, metadata));
        }

        // Constructor re-validates for duplicate field names.
        return new StructType(fields);
    }

    private static FieldMetadata ReadMetadata(JsonElement fieldElement)
    {
        if (!fieldElement.TryGetProperty("metadata", out JsonElement metadata)
            || metadata.ValueKind == JsonValueKind.Null)
        {
            return FieldMetadata.Empty;
        }

        if (metadata.ValueKind != JsonValueKind.Object)
        {
            throw new SchemaValidationException("Field 'metadata' must be a JSON object.");
        }

        var entries = new List<KeyValuePair<string, string>>();
        foreach (JsonProperty property in metadata.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new SchemaValidationException(
                    $"Unsupported metadata value for key '{property.Name}': "
                    + "v1 supports string metadata values only.");
            }

            entries.Add(new KeyValuePair<string, string>(property.Name, property.Value.GetString()!));
        }

        return FieldMetadata.FromEntries(entries);
    }

    private static JsonElement GetRequired(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new SchemaValidationException($"Invalid type JSON: missing '{propertyName}' property.");
        }

        return value;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        JsonElement value = GetRequired(element, propertyName);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new SchemaValidationException($"Invalid type JSON: '{propertyName}' must be a string.");
        }

        return value.GetString()!;
    }

    private static bool GetRequiredBoolean(JsonElement element, string propertyName)
    {
        JsonElement value = GetRequired(element, propertyName);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new SchemaValidationException($"Invalid type JSON: '{propertyName}' must be a boolean.");
        }

        return value.GetBoolean();
    }
}
