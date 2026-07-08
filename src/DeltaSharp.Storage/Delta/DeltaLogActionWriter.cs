using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Serializes the typed <see cref="DeltaAction"/> model back to a Delta <c>_delta_log/&lt;N&gt;.json</c>
/// commit file — newline-delimited JSON, one action per line (design §2.10.2) — the exact inverse of
/// <see cref="DeltaLogActionReader"/>. Reflection-free (<see cref="Utf8JsonWriter"/>, never
/// <c>JsonSerializer</c>) so it stays trim/AOT-clean (ADR-0014), matching the reader's field names and
/// shapes so <c>Parse(Serialize(actions))</c> reproduces the model (round-trip oracle).
///
/// <para>Maps/arrays are emitted in the reader's <see cref="StringComparer.Ordinal"/> sort order (the
/// model already stores them sorted), so serialization is deterministic and re-serialization is
/// byte-stable — a precondition of the commit engine embedding a stable nonce (design §2.11.4).</para>
/// </summary>
internal static class DeltaLogActionWriter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
    };

    private static ReadOnlySpan<byte> Newline => "\n"u8;

    /// <summary>
    /// Serializes <paramref name="actions"/> to the UTF-8 bytes of one commit file, in the given order,
    /// each action on its own line terminated by <c>\n</c> (a trailing newline is normal and the reader
    /// tolerates it). Produces the canonical single-key envelopes <c>{"add":{…}}</c>, <c>{"protocol":{…}}</c>,
    /// and so on.
    /// </summary>
    /// <exception cref="ArgumentException">An action is of an unknown <see cref="DeltaAction"/> subtype.</exception>
    public static byte[] SerializeCommit(IReadOnlyList<DeltaAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, WriterOptions);
        foreach (DeltaAction action in actions)
        {
            // One writer, reset per line: each object stays a valid single JSON root, and newlines are
            // written to the shared buffer between/after them (avoids a per-line writer allocation).
            writer.Reset();
            WriteAction(writer, action);
            writer.Flush();
            buffer.Write(Newline);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteAction(Utf8JsonWriter writer, DeltaAction action)
    {
        switch (action)
        {
            case ProtocolAction protocol:
                WriteProtocol(writer, protocol);
                break;
            case MetadataAction metadata:
                WriteMetadata(writer, metadata);
                break;
            case AddFileAction add:
                WriteAdd(writer, add);
                break;
            case RemoveFileAction remove:
                WriteRemove(writer, remove);
                break;
            case TxnAction txn:
                WriteTxn(writer, txn);
                break;
            case CommitInfoAction commitInfo:
                WriteCommitInfo(writer, commitInfo);
                break;
            default:
                throw new ArgumentException(
                    $"Unknown Delta action type '{action.GetType().Name}' cannot be serialized.",
                    nameof(action));
        }
    }

    private static void WriteProtocol(Utf8JsonWriter writer, ProtocolAction protocol)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("protocol");
        writer.WriteNumber("minReaderVersion", protocol.MinReaderVersion);
        writer.WriteNumber("minWriterVersion", protocol.MinWriterVersion);
        // Reader/writer feature lists appear only for table-features protocols (reader v3 / writer v7);
        // an empty list is written by omission so it round-trips to the reader's empty default.
        WriteStringArrayIfAny(writer, "readerFeatures", protocol.ReaderFeatures);
        WriteStringArrayIfAny(writer, "writerFeatures", protocol.WriterFeatures);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteMetadata(Utf8JsonWriter writer, MetadataAction metadata)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("metaData");
        writer.WriteString("id", metadata.Id);
        WriteOptionalString(writer, "name", metadata.Name);
        WriteOptionalString(writer, "description", metadata.Description);

        writer.WriteStartObject("format");
        writer.WriteString("provider", metadata.Format.Provider);
        writer.WriteStartObject("options");
        WriteStringMapEntries(writer, metadata.Format.Options);
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteString("schemaString", metadata.SchemaString);

        writer.WriteStartArray("partitionColumns");
        foreach (string column in metadata.PartitionColumns)
        {
            writer.WriteStringValue(column);
        }

        writer.WriteEndArray();

        writer.WriteStartObject("configuration");
        WriteStringMapEntries(writer, metadata.Configuration);
        writer.WriteEndObject();

        if (metadata.CreatedTime is { } createdTime)
        {
            writer.WriteNumber("createdTime", createdTime);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteAdd(Utf8JsonWriter writer, AddFileAction add)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("add");
        writer.WriteString("path", add.Path);

        writer.WriteStartObject("partitionValues");
        WriteNullableStringMapEntries(writer, add.PartitionValues);
        writer.WriteEndObject();

        writer.WriteNumber("size", add.Size);
        writer.WriteNumber("modificationTime", add.ModificationTime);
        writer.WriteBoolean("dataChange", add.DataChange);

        if (add.Stats is { } stats)
        {
            // Delta encodes per-file statistics as a JSON-encoded *string* within the add action.
            writer.WriteString("stats", SerializeStats(stats));
        }

        WriteStringMapIfAny(writer, "tags", add.Tags);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteRemove(Utf8JsonWriter writer, RemoveFileAction remove)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("remove");
        writer.WriteString("path", remove.Path);
        if (remove.DeletionTimestamp is { } deletionTimestamp)
        {
            writer.WriteNumber("deletionTimestamp", deletionTimestamp);
        }

        writer.WriteBoolean("dataChange", remove.DataChange);
        writer.WriteBoolean("extendedFileMetadata", remove.ExtendedFileMetadata);

        if (!remove.PartitionValues.IsEmpty)
        {
            writer.WriteStartObject("partitionValues");
            WriteNullableStringMapEntries(writer, remove.PartitionValues);
            writer.WriteEndObject();
        }

        if (remove.Size is { } size)
        {
            writer.WriteNumber("size", size);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteTxn(Utf8JsonWriter writer, TxnAction txn)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("txn");
        writer.WriteString("appId", txn.AppId);
        writer.WriteNumber("version", txn.Version);
        if (txn.LastUpdated is { } lastUpdated)
        {
            writer.WriteNumber("lastUpdated", lastUpdated);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteCommitInfo(Utf8JsonWriter writer, CommitInfoAction commitInfo)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("commitInfo");
        // The model stores commitInfo as string→string provenance; emit every entry as a JSON string.
        foreach (KeyValuePair<string, string> entry in commitInfo.Entries)
        {
            writer.WriteString(entry.Key, entry.Value);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Serializes <see cref="FileStatistics"/> to the <c>add.stats</c> JSON string understood by
    /// <see cref="DeltaLogActionReader.ParseStatsString"/> (numRecords / minValues / maxValues /
    /// nullCount / tightBounds). Empty sub-objects are omitted so they round-trip to the reader's empty
    /// defaults; <see cref="FileStatistics.Empty"/> serializes to <c>{}</c>.
    /// </summary>
    internal static string SerializeStats(FileStatistics stats)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            if (stats.NumRecords is { } numRecords)
            {
                writer.WriteNumber("numRecords", numRecords);
            }

            WriteStatValuesIfAny(writer, "minValues", stats.MinValues);
            WriteStatValuesIfAny(writer, "maxValues", stats.MaxValues);

            if (!stats.NullCount.IsEmpty)
            {
                writer.WriteStartObject("nullCount");
                foreach (KeyValuePair<string, long> entry in stats.NullCount)
                {
                    writer.WriteNumber(entry.Key, entry.Value);
                }

                writer.WriteEndObject();
            }

            if (stats.TightBounds is { } tightBounds)
            {
                writer.WriteBoolean("tightBounds", tightBounds);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteStatValuesIfAny(
        Utf8JsonWriter writer,
        string property,
        ImmutableSortedDictionary<string, DeltaStatValue> values)
    {
        if (values.IsEmpty)
        {
            return;
        }

        writer.WriteStartObject(property);
        foreach (KeyValuePair<string, DeltaStatValue> entry in values)
        {
            DeltaStatValue value = entry.Value;
            if (value.Kind == DeltaStatKind.String)
            {
                writer.WriteString(entry.Key, value.Raw);
            }
            else
            {
                // Long / Double / Boolean are already invariant-culture JSON scalar text. A non-finite
                // double (NaN/±Infinity) has no JSON representation and WriteRawValue would reject it — not
                // reachable today (stats only originate from the reader, which cannot parse those), and the
                // write-time-statistics story (#197) will skip/clamp non-finite bounds at their source.
                writer.WritePropertyName(entry.Key);
                writer.WriteRawValue(value.Raw, skipInputValidation: false);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteStringArrayIfAny(Utf8JsonWriter writer, string property, ImmutableArray<string> values)
    {
        if (values.IsDefaultOrEmpty)
        {
            return;
        }

        writer.WriteStartArray(property);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteStringMapIfAny(
        Utf8JsonWriter writer,
        string property,
        ImmutableSortedDictionary<string, string> map)
    {
        if (map.IsEmpty)
        {
            return;
        }

        writer.WriteStartObject(property);
        WriteStringMapEntries(writer, map);
        writer.WriteEndObject();
    }

    private static void WriteStringMapEntries(Utf8JsonWriter writer, ImmutableSortedDictionary<string, string> map)
    {
        foreach (KeyValuePair<string, string> entry in map)
        {
            writer.WriteString(entry.Key, entry.Value);
        }
    }

    private static void WriteNullableStringMapEntries(
        Utf8JsonWriter writer,
        ImmutableSortedDictionary<string, string?> map)
    {
        foreach (KeyValuePair<string, string?> entry in map)
        {
            if (entry.Value is null)
            {
                writer.WriteNull(entry.Key);
            }
            else
            {
                writer.WriteString(entry.Key, entry.Value);
            }
        }
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string property, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(property, value);
        }
    }
}
