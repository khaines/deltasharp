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
            // Pin the parse depth bound explicitly (JsonDocument's default is 64) so deeply nested
            // metadata objects fail closed at the untrusted read boundary rather than relying on an
            // implicit default that a future runtime could change.
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
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
        foreach (KeyValuePair<string, MetadataValue> entry in metadata)
        {
            // FieldMetadata enumerates in sorted key order => deterministic output.
            writer.WritePropertyName(entry.Key);
            WriteMetadataValue(writer, entry.Value);
        }

        writer.WriteEndObject();
    }

    // Writes a typed metadata value (string/long/double/bool/null/array/nested-object). Numbers
    // are emitted so an integer stays an unquoted integer and a double keeps a fractional/exponent
    // form (so it re-reads as a Double, not a Long) — the Delta-log interop contract (#330).
    // Shared byte-for-byte with DeltaSharp.Storage's DeltaSchemaJson.WriteMetadataValue.
    internal static void WriteMetadataValue(Utf8JsonWriter writer, MetadataValue value)
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

    // Writes a double with a round-trippable ("R") representation, forcing a fractional part when
    // the value is integral so it never collapses to a bare integer literal (which would re-read as
    // a Long). NaN/Infinity are not representable in JSON, so they fall through to WriteNumberValue,
    // which throws the standard ArgumentException.
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
            "timestamp_ntz" => TimestampNtzType.Instance,
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

        return ReadMetadataObject(metadata);
    }

    // Parses a metadata JSON object into typed FieldMetadata. Recurses through nested objects and
    // arrays; a JSON number is discriminated Long-vs-Double the same way Spark/Jackson does.
    private static FieldMetadata ReadMetadataObject(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            throw new SchemaValidationException("Field 'metadata' must be a JSON object.");
        }

        var entries = new List<KeyValuePair<string, MetadataValue>>();
        foreach (JsonProperty property in metadata.EnumerateObject())
        {
            entries.Add(new KeyValuePair<string, MetadataValue>(
                property.Name, ReadMetadataValue(property.Value)));
        }

        return FieldMetadata.FromValues(entries);
    }

    private static MetadataValue ReadMetadataValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return MetadataValue.String(element.GetString()!);

            case JsonValueKind.Number:
                // An integral number that fits in Int64 is a Long (e.g. delta.columnMapping.id);
                // anything else (fractional, exponent, or out-of-range) is a Double. A non-finite
                // parse result (e.g. an overflowing 1e400 literal → ±Infinity) is not representable
                // in JSON, so fail closed here at the untrusted read boundary rather than accepting
                // a value that would throw an untyped ArgumentException on re-serialize.
                if (element.TryGetInt64(out long longValue))
                {
                    return MetadataValue.Long(longValue);
                }

                double doubleValue = element.GetDouble();
                if (!double.IsFinite(doubleValue))
                {
                    // Bound the echoed literal: a poisoned schema could carry a multi-KB numeric
                    // literal, and while its charset is injection-safe (JSON number tokens are
                    // [0-9+-.eE] only), echoing it in full is needless. A short prefix suffices.
                    throw new SchemaValidationException(
                        $"Metadata number '{Truncate(element.GetRawText(), 32)}' is not finite "
                        + "and cannot be represented as JSON.");
                }

                return MetadataValue.Double(doubleValue);

            case JsonValueKind.True:
            case JsonValueKind.False:
                return MetadataValue.Boolean(element.GetBoolean());

            case JsonValueKind.Null:
                return MetadataValue.Null;

            case JsonValueKind.Array:
                var elements = new List<MetadataValue>();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    elements.Add(ReadMetadataValue(item));
                }

                return MetadataValue.Array(elements);

            case JsonValueKind.Object:
                return MetadataValue.Nested(ReadMetadataObject(element));

            default:
                throw new SchemaValidationException(
                    $"Unsupported metadata value token '{element.ValueKind}'.");
        }
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

    /// <summary>Returns <paramref name="text"/> capped to <paramref name="max"/> characters, adding
    /// an ellipsis when truncated, so a diagnostic never echoes an unbounded attacker-supplied token.</summary>
    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");
}
