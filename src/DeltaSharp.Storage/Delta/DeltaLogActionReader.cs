using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Parses a Delta <c>_delta_log/&lt;N&gt;.json</c> commit file — newline-delimited JSON, one action per
/// line (design §2.10.2) — into the typed <see cref="DeltaAction"/> model. Reflection-free
/// (<see cref="JsonDocument"/>/<see cref="Utf8JsonReader"/>) so it stays trim/AOT-clean (ADR-0014).
///
/// <para>Fails closed: any malformed JSON line, wrong-typed field, or missing required field throws a
/// <see cref="DeltaProtocolException"/> naming the offending version/action (never silently skipped).
/// An unrecognized <b>top-level action key</b> is ignored for forward compatibility — Delta's documented
/// rule — but an unsupported <b>protocol feature</b> is rejected later during negotiation (§2.10.5).</para>
/// </summary>
internal static class DeltaLogActionReader
{
    private static readonly ImmutableSortedDictionary<string, string> EmptyStringMap =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly ImmutableSortedDictionary<string, string?> EmptyNullableStringMap =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// Parses one commit file's UTF-8 bytes into its actions, in file order. <paramref name="version"/>
    /// is the commit version (for error context). Blank lines are skipped (a trailing newline is normal).
    /// </summary>
    /// <exception cref="DeltaProtocolException">A line is not a single-key JSON action object, is
    /// malformed, or an action is missing a required field / has a wrong-typed field.</exception>
    public static IReadOnlyList<DeltaAction> ParseCommit(ReadOnlyMemory<byte> content, long version)
    {
        var actions = new List<DeltaAction>();
        ReadOnlySpan<byte> span = content.Span;
        int line = 0;

        while (!span.IsEmpty)
        {
            int newline = span.IndexOf((byte)'\n');
            ReadOnlySpan<byte> lineSpan = newline < 0 ? span : span[..newline];
            span = newline < 0 ? default : span[(newline + 1)..];
            line++;

            // Trim ASCII whitespace (incl. a CR from CRLF); skip blank lines.
            lineSpan = TrimAsciiWhitespace(lineSpan);
            if (lineSpan.IsEmpty)
            {
                continue;
            }

            DeltaAction? action = ParseLine(lineSpan, version, line);
            if (action is not null)
            {
                actions.Add(action);
            }
        }

        return actions;
    }

    private static DeltaAction? ParseLine(ReadOnlySpan<byte> lineSpan, long version, int line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(lineSpan.ToArray());
        }
        catch (JsonException ex)
        {
            throw DeltaProtocolException.Malformed(
                string.Create(CultureInfo.InvariantCulture, $"Delta log version {version} line {line} is not valid JSON."),
                ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw DeltaProtocolException.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Delta log version {version} line {line} must be a JSON object with a single action key, but was '{root.ValueKind}'."));
            }

            // Each action line is a single-key object: {"add": {...}} / {"protocol": {...}} / ...
            string? actionKey = null;
            JsonElement body = default;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (actionKey is not null)
                {
                    throw DeltaProtocolException.Malformed(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Delta log version {version} line {line} must contain exactly one action key, but found multiple."));
                }

                actionKey = property.Name;
                body = property.Value;
            }

            if (actionKey is null)
            {
                // An empty object {} carries no action; tolerate it.
                return null;
            }

            return actionKey switch
            {
                "protocol" => ParseProtocol(body, version, line),
                "metaData" => ParseMetadata(body, version, line),
                "add" => ParseAdd(body, version, line),
                "remove" => ParseRemove(body, version, line),
                "txn" => ParseTxn(body, version, line),
                "commitInfo" => ParseCommitInfo(body),
                // Forward compatibility: an unknown action key (e.g. a future/phased action) is ignored.
                // A table that *requires* understanding it advertises a reader feature, which protocol
                // negotiation (§2.10.5) rejects up front — so ignoring here can never read past an
                // unsupported feature.
                _ => null,
            };
        }
    }

    private static ProtocolAction ParseProtocol(JsonElement body, long version, int line)
    {
        RequireObject(body, "protocol", version, line);
        int minReader = GetRequiredInt32(body, "minReaderVersion", "protocol", version, line);
        int minWriter = GetRequiredInt32(body, "minWriterVersion", "protocol", version, line);
        return new ProtocolAction(
            minReader,
            minWriter,
            GetStringArray(body, "readerFeatures", "protocol", version, line),
            GetStringArray(body, "writerFeatures", "protocol", version, line));
    }

    private static MetadataAction ParseMetadata(JsonElement body, long version, int line)
    {
        RequireObject(body, "metaData", version, line);
        TableFormat format = ParseFormat(body, version, line);
        return new MetadataAction(
            GetRequiredString(body, "id", "metaData", version, line),
            GetOptionalString(body, "name"),
            GetOptionalString(body, "description"),
            format,
            GetRequiredString(body, "schemaString", "metaData", version, line),
            GetStringArray(body, "partitionColumns", "metaData", version, line),
            GetStringMap(body, "configuration", "metaData", version, line),
            GetOptionalInt64(body, "createdTime", "metaData", version, line));
    }

    private static TableFormat ParseFormat(JsonElement metadata, long version, int line)
    {
        if (!metadata.TryGetProperty("format", out JsonElement format) || format.ValueKind == JsonValueKind.Null)
        {
            // A metaData without a format is malformed — the reader must know the data-file provider.
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Delta log version {version} line {line}: metaData is missing the required 'format'."));
        }

        RequireObject(format, "metaData.format", version, line);
        return new TableFormat(
            GetRequiredString(format, "provider", "metaData.format", version, line),
            GetStringMap(format, "options", "metaData.format", version, line));
    }

    private static AddFileAction ParseAdd(JsonElement body, long version, int line)
    {
        RequireObject(body, "add", version, line);
        return new AddFileAction(
            GetRequiredString(body, "path", "add", version, line),
            GetNullableStringMap(body, "partitionValues", "add", version, line),
            GetRequiredInt64(body, "size", "add", version, line),
            GetOptionalInt64(body, "modificationTime", "add", version, line) ?? 0L,
            GetOptionalBool(body, "dataChange", "add", version, line) ?? true,
            ParseStats(body, version, line),
            GetStringMap(body, "tags", "add", version, line));
    }

    private static RemoveFileAction ParseRemove(JsonElement body, long version, int line)
    {
        RequireObject(body, "remove", version, line);
        return new RemoveFileAction(
            GetRequiredString(body, "path", "remove", version, line),
            GetOptionalInt64(body, "deletionTimestamp", "remove", version, line),
            GetOptionalBool(body, "dataChange", "remove", version, line) ?? true,
            GetOptionalBool(body, "extendedFileMetadata", "remove", version, line) ?? false,
            GetNullableStringMap(body, "partitionValues", "remove", version, line),
            GetOptionalInt64(body, "size", "remove", version, line));
    }

    private static TxnAction ParseTxn(JsonElement body, long version, int line)
    {
        RequireObject(body, "txn", version, line);
        return new TxnAction(
            GetRequiredString(body, "appId", "txn", version, line),
            GetRequiredInt64(body, "version", "txn", version, line),
            GetOptionalInt64(body, "lastUpdated", "txn", version, line));
    }

    private static CommitInfoAction ParseCommitInfo(JsonElement body)
    {
        // commitInfo is best-effort provenance: preserve scalar entries as strings, ignore nested/complex
        // values. Never fails the read — it is not load-bearing for replay (§2.10.1).
        if (body.ValueKind != JsonValueKind.Object)
        {
            return new CommitInfoAction(EmptyStringMap);
        }

        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in body.EnumerateObject())
        {
            string? scalar = ScalarToString(property.Value);
            if (scalar is not null)
            {
                builder[property.Name] = scalar;
            }
        }

        return new CommitInfoAction(builder.ToImmutable());
    }

    private static FileStatistics? ParseStats(JsonElement add, long version, int line)
    {
        if (!add.TryGetProperty("stats", out JsonElement stats) || stats.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (stats.ValueKind != JsonValueKind.String)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Delta log version {version} line {line}: add.stats must be a JSON string, but was '{stats.ValueKind}'."));
        }

        string? statsJson = stats.GetString();
        if (string.IsNullOrWhiteSpace(statsJson))
        {
            return null;
        }

        JsonDocument statsDocument;
        try
        {
            statsDocument = JsonDocument.Parse(statsJson);
        }
        catch (JsonException ex)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Delta log version {version} line {line}: add.stats is not valid JSON."),
                ex);
        }

        using (statsDocument)
        {
            JsonElement root = statsDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return FileStatistics.Empty;
            }

            long? numRecords = null;
            if (root.TryGetProperty("numRecords", out JsonElement nr) && nr.ValueKind == JsonValueKind.Number
                && nr.TryGetInt64(out long nrValue))
            {
                numRecords = nrValue;
            }

            bool? tightBounds = null;
            if (root.TryGetProperty("tightBounds", out JsonElement tb)
                && (tb.ValueKind == JsonValueKind.True || tb.ValueKind == JsonValueKind.False))
            {
                tightBounds = tb.GetBoolean();
            }

            return new FileStatistics(
                numRecords,
                ReadStatValues(root, "minValues"),
                ReadStatValues(root, "maxValues"),
                ReadNullCounts(root),
                tightBounds);
        }
    }

    private static ImmutableSortedDictionary<string, DeltaStatValue> ReadStatValues(JsonElement statsRoot, string property)
    {
        if (!statsRoot.TryGetProperty(property, out JsonElement values) || values.ValueKind != JsonValueKind.Object)
        {
            return FileStatistics.Empty.MinValues;
        }

        var builder = ImmutableSortedDictionary.CreateBuilder<string, DeltaStatValue>(StringComparer.Ordinal);
        foreach (JsonProperty column in values.EnumerateObject())
        {
            // v1 scope: only top-level scalar bounds. Nested-struct bounds (a JSON object) are advisory
            // and skipped — pruning simply forgoes them, never a correctness risk (§2.10.5).
            DeltaStatValue? value = column.Value.ValueKind switch
            {
                JsonValueKind.String => DeltaStatValue.OfString(column.Value.GetString()!),
                JsonValueKind.Number => column.Value.TryGetInt64(out long l)
                    ? DeltaStatValue.OfLong(l)
                    : DeltaStatValue.OfDouble(column.Value.GetDouble()),
                JsonValueKind.True => DeltaStatValue.OfBoolean(true),
                JsonValueKind.False => DeltaStatValue.OfBoolean(false),
                _ => null,
            };

            if (value is not null)
            {
                builder[column.Name] = value;
            }
        }

        return builder.ToImmutable();
    }

    private static ImmutableSortedDictionary<string, long> ReadNullCounts(JsonElement statsRoot)
    {
        if (!statsRoot.TryGetProperty("nullCount", out JsonElement counts) || counts.ValueKind != JsonValueKind.Object)
        {
            return FileStatistics.Empty.NullCount;
        }

        var builder = ImmutableSortedDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
        foreach (JsonProperty column in counts.EnumerateObject())
        {
            if (column.Value.ValueKind == JsonValueKind.Number && column.Value.TryGetInt64(out long count))
            {
                builder[column.Name] = count;
            }
        }

        return builder.ToImmutable();
    }

    // ---- field-extraction helpers (fail-closed on missing required / wrong type) ----

    private static void RequireObject(JsonElement element, string action, long version, int line)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Delta log version {version} line {line}: '{action}' must be a JSON object, but was '{element.ValueKind}'."));
        }
    }

    private static string GetRequiredString(JsonElement obj, string prop, string action, long version, int line)
    {
        if (!obj.TryGetProperty(prop, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Delta log version {version} line {line}: '{action}' is missing required string '{prop}'."));
        }

        return value.GetString()!;
    }

    private static string? GetOptionalString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetRequiredInt32(JsonElement obj, string prop, string action, long version, int line)
    {
        if (!obj.TryGetProperty(prop, out JsonElement value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int result))
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Delta log version {version} line {line}: '{action}' is missing required integer '{prop}'."));
        }

        return result;
    }

    private static long GetRequiredInt64(JsonElement obj, string prop, string action, long version, int line)
    {
        if (!obj.TryGetProperty(prop, out JsonElement value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long result))
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Delta log version {version} line {line}: '{action}' is missing required integer '{prop}'."));
        }

        return result;
    }

    private static long? GetOptionalInt64(JsonElement obj, string prop, string action, long version, int line)
    {
        if (!obj.TryGetProperty(prop, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result))
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Delta log version {version} line {line}: '{action}.{prop}' must be an integer, but was '{value.ValueKind}'."));
        }

        return result;
    }

    private static bool? GetOptionalBool(JsonElement obj, string prop, string action, long version, int line)
    {
        if (!obj.TryGetProperty(prop, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Delta log version {version} line {line}: '{action}.{prop}' must be a boolean, but was '{value.ValueKind}'."));
        }

        return value.GetBoolean();
    }

    private static ImmutableArray<string> GetStringArray(JsonElement obj, string prop, string action, long version, int line)
    {
        if (!obj.TryGetProperty(prop, out JsonElement array) || array.ValueKind == JsonValueKind.Null)
        {
            return ImmutableArray<string>.Empty;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Delta log version {version} line {line}: '{action}.{prop}' must be an array, but was '{array.ValueKind}'."));
        }

        ImmutableArray<string>.Builder builder = ImmutableArray.CreateBuilder<string>(array.GetArrayLength());
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw DeltaProtocolException.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Delta log version {version} line {line}: '{action}.{prop}' must be an array of strings."));
            }

            builder.Add(item.GetString()!);
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableSortedDictionary<string, string> GetStringMap(JsonElement obj, string prop, string action, long version, int line)
    {
        if (!obj.TryGetProperty(prop, out JsonElement map) || map.ValueKind == JsonValueKind.Null)
        {
            return EmptyStringMap;
        }

        if (map.ValueKind != JsonValueKind.Object)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Delta log version {version} line {line}: '{action}.{prop}' must be an object, but was '{map.ValueKind}'."));
        }

        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in map.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                throw DeltaProtocolException.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Delta log version {version} line {line}: '{action}.{prop}' must map strings to strings."));
            }

            builder[entry.Name] = entry.Value.GetString()!;
        }

        return builder.ToImmutable();
    }

    private static ImmutableSortedDictionary<string, string?> GetNullableStringMap(JsonElement obj, string prop, string action, long version, int line)
    {
        if (!obj.TryGetProperty(prop, out JsonElement map) || map.ValueKind == JsonValueKind.Null)
        {
            return EmptyNullableStringMap;
        }

        if (map.ValueKind != JsonValueKind.Object)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Delta log version {version} line {line}: '{action}.{prop}' must be an object, but was '{map.ValueKind}'."));
        }

        var builder = ImmutableSortedDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        foreach (JsonProperty entry in map.EnumerateObject())
        {
            // Partition values are strings OR JSON null (a null partition value — distinct from absent).
            string? value = entry.Value.ValueKind switch
            {
                JsonValueKind.String => entry.Value.GetString(),
                JsonValueKind.Null => null,
                _ => throw DeltaProtocolException.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Delta log version {version} line {line}: '{action}.{prop}' values must be strings or null.")),
            };
            builder[entry.Name] = value;
        }

        return builder.ToImmutable();
    }

    private static string? ScalarToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        int start = 0;
        int end = span.Length;
        while (start < end && IsAsciiWhitespace(span[start]))
        {
            start++;
        }

        while (end > start && IsAsciiWhitespace(span[end - 1]))
        {
            end--;
        }

        return span[start..end];
    }

    private static bool IsAsciiWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
