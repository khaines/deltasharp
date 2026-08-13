using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DeltaSharp.Diagnostics;

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
    /// The single JSON container-nesting bound shared by BOTH directions, so the write side can never
    /// persist a schema the read side would then refuse (#711). <see cref="FromJson(string)"/> parses with
    /// <c>JsonDocumentOptions.MaxDepth = MaxDepth</c> (rejecting any JSON nested deeper) and
    /// <see cref="ToJson(DataType)"/> refuses to open a container once <see cref="Utf8JsonWriter.CurrentDepth"/>
    /// reaches this bound. <see cref="Utf8JsonWriter.CurrentDepth"/> counts open objects/arrays exactly as
    /// <see cref="JsonDocument"/> measures depth, so the two BOUNDS meet at the same container level and the
    /// load-bearing invariant holds: <b>anything DeltaSharp WRITES it can read back</b> — every string
    /// <see cref="ToJson(DataType)"/> agrees to emit is at container depth &lt;= <see cref="MaxDepth"/>, which
    /// <see cref="FromJson(string)"/> always accepts.
    /// </summary>
    /// <remarks>
    /// <para><b>The converse does NOT hold, deliberately.</b> The read side is more tolerant than the write
    /// side by exactly one container in one documented case: a FOREIGN schema whose deepest struct field
    /// OMITS <c>"metadata"</c> (or writes <c>"metadata":null</c>). <see cref="FromJson(string)"/> tolerates
    /// the omission (Spark/delta-rs both emit it, but the field is optional in practice), while
    /// <see cref="ToJson(DataType)"/> always materializes a <c>"metadata":{}</c> object — one MORE container
    /// than the input carried. So a legal foreign schema sitting exactly at read depth <see cref="MaxDepth"/>
    /// reads fine and can be queried, but cannot be RE-SERIALIZED: a schema evolution / overwrite of that
    /// table fails closed at <see cref="ToJson(DataType)"/>. That is the safe direction (a refusal, never a
    /// silently unreadable commit) and is pinned as a documented limitation, not a claim of symmetry.</para>
    /// <para>The unit is JSON CONTAINERS (open objects/arrays), not schema levels: an array/map level costs 1
    /// container, a struct level costs 3 (the struct object, its <c>fields</c> array, the field object), so
    /// <see cref="MaxDepth"/> = 64 admits ~21 nested struct levels. This is a serialization-shape bound and is
    /// unrelated to <c>DeltaSharp.Executor.Physical.NestedTypeDepth.MaxDepth</c> (the query-execution
    /// nested-value recursion bound), which counts TYPE levels — the two numbers measure different things and
    /// are not in conflict.</para>
    /// </remarks>
    private const int MaxDepth = 64;

    /// <summary>
    /// Serializes a <see cref="DataType"/> tree to the Spark-compatible schema JSON (the same
    /// format Delta stores in its log), round-trippable with <see cref="FromJson(string)"/>
    /// (STORY-02.5.1 AC3).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    /// <exception cref="SchemaValidationException">The type tree nests deeper than <see cref="MaxDepth"/>
    /// (would serialize to JSON <see cref="FromJson(string)"/> could never re-read, #711), or a field name,
    /// metadata key, or metadata string value contains invalid UTF-16 (an unpaired surrogate) that
    /// <see cref="Utf8JsonWriter"/> would lossily transcode to U+FFFD, #710.</exception>
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
    /// <exception cref="SchemaValidationException">The JSON is malformed, nests deeper than
    /// <see cref="MaxDepth"/> JSON containers, contains invalid UTF-16 (an unpaired surrogate, raw or
    /// <c>\uD800</c>-escaped), or describes an invalid/unknown type. This is the ONLY exception type this
    /// method raises for a hostile document: it is the untrusted read boundary, and every caller
    /// (<c>Snapshot</c>, <c>DeltaReadSource</c>, <c>DeltaLog</c>, <c>ChangeFeedReader</c>,
    /// <c>DeltaCommitter</c>) catches exactly this to fail closed.</exception>
    public static DataType FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            // Pin the parse depth bound explicitly (JsonDocument's default is 64) so deeply nested
            // metadata objects fail closed at the untrusted read boundary rather than relying on an
            // implicit default that a future runtime could change. ToJson enforces the SAME MaxDepth on
            // the write side (#711), so anything DeltaSharp agrees to persist it can also read back.
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
            return ReadType(document.RootElement);
        }
        catch (JsonException ex)
        {
            // #683-class message hygiene, at the LARGEST raw-echo producer on this untrusted read path:
            // System.Text.Json embeds a fragment of the OFFENDING DOCUMENT in its message (and the document
            // here is an attacker-authored schemaString — unbounded, and free to carry CR/LF for log-line
            // forgery). Bounded + control-char-neutralized exactly like the other read-path echoes; the cap
            // is generous enough to keep the useful part of the STJ text (the reason plus its LineNumber /
            // BytePositionInLine). The untruncated original stays available on the InnerException for a
            // raw-inner diagnostics sink.
            throw new SchemaValidationException(
                $"Invalid schema JSON: {DiagnosticText.Sanitize(ex.Message, JsonExceptionMessageMaxLength)}", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // #710 (read-side mirror): System.Text.Json signals INVALID UTF-16 with untyped exceptions rather
            // than JsonException — JsonDocument.Parse throws ArgumentException for a RAW unpaired surrogate
            // ("Cannot transcode invalid UTF-16 string to UTF-8 JSON text") and JsonElement.GetString() /
            // JsonProperty.Name throw InvalidOperationException for an ESCAPED one ("\uD800", "Cannot read
            // incomplete UTF-16 JSON text as string with missing low surrogate"). Both would otherwise escape
            // every caller's `catch (SchemaValidationException)` and break the documented fail-closed contract
            // at the UNTRUSTED read boundary. Re-typed here so a hostile schemaString is exactly as
            // classifiable as a malformed one. The message is deliberately CONTENT-FREE: the offending token
            // is attacker-authored (and, being invalid UTF-16, is not safely renderable at all).
            //
            // The wording is deliberately DE-SPECIFIED: this net is broad by design (fail-closed), so it can
            // also catch an ArgumentException raised by a type/field CONSTRUCTOR reached during the read
            // (e.g. a struct field whose "name" is empty) — attributing every such fault to invalid UTF-16
            // would be a confidently wrong diagnostic. The common cause is named as the common cause, not as
            // a verdict. Concrete cases worth a precise message get an explicit guard upstream (see
            // ReadStruct's empty-name check) rather than a narrower catch, which would let a genuine
            // escaped-surrogate fault escape the fail-closed contract.
            throw new SchemaValidationException(
                "Invalid schema JSON: the document could not be decoded as text (most commonly invalid UTF-16 "
                + "— an unpaired surrogate, raw or escaped). The offending content is not echoed.", ex);
        }
    }

    /// <summary>The cap applied to a <see cref="JsonException"/> message before it is echoed. System.Text.Json
    /// reports the reason plus <c>LineNumber</c>/<c>BytePositionInLine</c> well within this, while the
    /// document fragment it may quote is attacker-authored and must never be echoed unbounded.</summary>
    private const int JsonExceptionMessageMaxLength = 256;

    private static void WriteType(Utf8JsonWriter writer, DataType type)
    {
        switch (type)
        {
            case ArrayType array:
                StartObject(writer);
                writer.WriteString("type", "array");
                writer.WritePropertyName("elementType");
                WriteType(writer, array.ElementType);
                writer.WriteBoolean("containsNull", array.ContainsNull);
                writer.WriteEndObject();
                break;

            case MapType map:
                StartObject(writer);
                writer.WriteString("type", "map");
                writer.WritePropertyName("keyType");
                WriteType(writer, map.KeyType);
                writer.WritePropertyName("valueType");
                WriteType(writer, map.ValueType);
                writer.WriteBoolean("valueContainsNull", map.ValueContainsNull);
                writer.WriteEndObject();
                break;

            case StructType structType:
                StartObject(writer);
                writer.WriteString("type", "struct");
                writer.WritePropertyName("fields");
                StartArray(writer);
                foreach (StructField field in structType)
                {
                    StartObject(writer);
                    writer.WriteString("name", ValidateUtf16(field.Name, "field name"));
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
                //
                // #702: NullType serializes to the type name "void", which is NOT a Delta-protocol primitive.
                // That string is intentionally still EMITTED here (and FromJson still ACCEPTS "void"/"null")
                // so DeltaSharp stays tolerant of a schemaString another engine wrote — delta-rs 1.6.2 reads
                // "void" as Arrow Null. Rejecting it in this SERIALIZER would be the wrong door: ToJson also
                // re-serializes a FOREIGN snapshot's schema (checkpoint/footer/evolution paths), so a reject
                // here would make a table another engine created unreadable rather than merely un-creatable.
                //
                // The door that keeps "void" out of a schemaString DeltaSharp COMMITS is the declared
                // WRITE-SCHEMA eligibility check in the Delta writer
                // (DeltaSharp.Storage.Delta.DeltaWriteSchemaEligibility.EnsureCommittable), which walks the
                // whole type tree — top-level field, array element, map key AND value, nested struct field —
                // and runs on every path that builds a metaData action (create, create-mapped, schema
                // evolution, overwriteSchema replacement), INDEPENDENT of how many data files are staged.
                // That independence is the point: a ZERO-FILE create (an empty write to a fresh path)
                // reaches none of the per-file guards — not ParquetTypeMapping.CreateField, not
                // ValidateStagedWriteSchema — so before that check a `void` column committed at version 0 and
                // produced a table DeltaSharp itself could not read.
                if (type is AtomicType or DecimalType)
                {
                    writer.WriteStringValue(type.TypeName);
                    break;
                }

                throw new SchemaValidationException($"Cannot serialize unsupported type '{type.SimpleString}'.");
        }
    }

    // Opens a JSON object only if doing so keeps the writer's container nesting within the shared MaxDepth
    // bound FromJson enforces on read (#711) — so a schema this method serializes is one FromJson can always
    // re-read. See EnsureDepth for why the check is fail-closed rather than clamped.
    private static void StartObject(Utf8JsonWriter writer)
    {
        EnsureDepth(writer);
        writer.WriteStartObject();
    }

    // Opens a JSON array under the same MaxDepth guard as StartObject (#711).
    private static void StartArray(Utf8JsonWriter writer)
    {
        EnsureDepth(writer);
        writer.WriteStartArray();
    }

    // Fails closed BEFORE opening another JSON container when the writer already holds MaxDepth open
    // containers: one more would emit JSON at depth MaxDepth+1, which FromJson (JsonDocumentOptions.MaxDepth
    // = MaxDepth) rejects — a schema that would commit to metaData.schemaString and then be permanently
    // unreadable (#711). Utf8JsonWriter.CurrentDepth counts open objects/arrays exactly as JsonDocument
    // measures depth, so the two bounds meet at the same level. The message names the limit AND its unit
    // (JSON containers, not schema levels — see the MaxDepth remarks) and carries no caller-supplied content.
    private static void EnsureDepth(Utf8JsonWriter writer)
    {
        if (writer.CurrentDepth >= MaxDepth)
        {
            throw new SchemaValidationException(
                $"Schema nesting exceeds the maximum supported depth of {MaxDepth} (JSON container nesting; "
                + "each struct level consumes 3 containers); a deeper schema would serialize to a "
                + "schemaString that could never be read back.");
        }
    }

    // Rejects a field name / metadata key / metadata string value containing invalid UTF-16 (an unpaired
    // surrogate) at the WRITE door (#710). Utf8JsonWriter silently transcodes such input to U+FFFD, which is
    // lossy and non-round-tripping: two distinct legal names (e.g. "x\uD800" and "x\uDC00") both collapse to
    // "x\uFFFD", so the commit succeeds but every subsequent read fails duplicate-name detection — the table
    // is bricked. Failing closed here keeps the invariant "anything we agree to persist, we can read back".
    // The offending value is invalid UTF-16 and untrusted, so the message echoes only the sanitized ROLE
    // (never the raw content) and the offending UTF-16 code-unit offset.
    private static string ValidateUtf16(string value, string role)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    throw InvalidUtf16(role, i);
                }

                i++; // A valid surrogate pair — skip the trailing low surrogate.
            }
            else if (char.IsLowSurrogate(c))
            {
                throw InvalidUtf16(role, i);
            }
        }

        return value;
    }

    private static SchemaValidationException InvalidUtf16(string role, int offset) =>
        new($"A {role} contains invalid UTF-16 (an unpaired surrogate at code-unit offset "
            + $"{offset.ToString(CultureInfo.InvariantCulture)}) and cannot be persisted to a Delta "
            + "schemaString without lossy transcoding.");

    private static void WriteMetadata(Utf8JsonWriter writer, FieldMetadata metadata)
    {
        StartObject(writer);
        foreach (KeyValuePair<string, MetadataValue> entry in metadata)
        {
            // FieldMetadata enumerates in sorted key order => deterministic output.
            writer.WritePropertyName(ValidateUtf16(entry.Key, "metadata key"));
            WriteMetadataValue(writer, entry.Value);
        }

        writer.WriteEndObject();
    }

    // Writes a typed metadata value (string/long/double/bool/null/array/nested-object). Numbers
    // are emitted so an integer stays an unquoted integer and a double keeps a fractional/exponent
    // form (so it re-reads as a Double, not a Long) — the Delta-log interop contract (#330).
    // Narrowed back to private by #679: DeltaSharp.Storage's footer serializer used to call this
    // directly to keep its duplicated WriteMetadata in lockstep. That copy is gone — Storage now
    // delegates the whole footer schema to ToJson — so no cross-assembly seam is needed here.
    private static void WriteMetadataValue(Utf8JsonWriter writer, MetadataValue value)
    {
        switch (value.Kind)
        {
            case MetadataValueKind.Null:
                writer.WriteNullValue();
                break;
            case MetadataValueKind.String:
                writer.WriteStringValue(ValidateUtf16(value.AsString(), "metadata string value"));
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
                StartArray(writer);
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
                    // #683-class message hygiene: `kind` is a raw token from an UNTRUSTED schemaString —
                    // unbounded and control-char-bearing. Bound + neutralize it exactly as
                    // ParquetTypeMapping does for the identical column-name echo.
                    _ => throw new SchemaValidationException(
                        $"Unknown complex type kind '{DiagnosticText.Sanitize(kind)}'."),
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
            // Same hygiene obligation as the complex-kind arm: an unknown type NAME is attacker-authored
            // content from a foreign schemaString, so it is bounded + control-char-neutralized before echo.
            _ => throw new SchemaValidationException(
                $"Unknown type name '{DiagnosticText.Sanitize(name)}'."),
        };
    }

    private static DecimalType ParseDecimal(string name)
    {
        int open = name.IndexOf('(', StringComparison.Ordinal);
        int close = name.IndexOf(')', StringComparison.Ordinal);
        if (open < 0 || close <= open || close != name.Length - 1)
        {
            // The closing paren must be the final character — reject trailing garbage such as
            // "decimal(10,2) junk". The name is untrusted (and here, by definition, malformed), so it is
            // bounded + neutralized before echo.
            throw new SchemaValidationException($"Malformed decimal type '{DiagnosticText.Sanitize(name)}'.");
        }

        string inner = name[(open + 1)..close];
        string[] parts = inner.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int precision)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int scale))
        {
            throw new SchemaValidationException($"Malformed decimal type '{DiagnosticText.Sanitize(name)}'.");
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
            if (name.Length == 0)
            {
                // A precise diagnostic for a concrete foreign-schema shape. Without it, StructField's
                // ArgumentException.ThrowIfNullOrEmpty(name) surfaces through FromJson's broad fail-closed
                // net and gets described as a decoding fault, which is correct in classification but wrong in
                // attribution. Content-free by construction (the offending value is the empty string).
                throw new SchemaValidationException(
                    "Invalid schema JSON: a struct field 'name' must be non-empty.");
            }

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
