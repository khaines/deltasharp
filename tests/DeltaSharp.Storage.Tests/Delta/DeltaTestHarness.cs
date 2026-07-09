using System.Globalization;
using System.Text;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Shared helpers for Delta log tests: JSON commit-action builders, writers for JSON commits / classic
/// (single- and multi-part) checkpoints / the <c>_last_checkpoint</c> hint over a real
/// <see cref="LocalFileSystemBackend"/>, and a canonical <see cref="Describe"/> of a snapshot's queryable
/// state. <see cref="Describe"/> exists because <see cref="AddFileAction"/>/<see cref="MetadataAction"/>
/// hold <c>ImmutableSortedDictionary</c>/<c>ImmutableArray</c> fields whose record equality is
/// <b>reference</b>-based; a canonical string compares <i>content</i>, which is what the
/// checkpoint-vs-JSON parity oracle needs (INV I7 / HP-04 compares active files + metadata + protocol).
/// </summary>
internal static class DeltaTestHarness
{
    public const string EmptySchema = """{\"type\":\"struct\",\"fields\":[]}""";

    // ---- JSON commit-action builders (NDJSON line per action) ----

    public static string Protocol(int minReader = 1, int minWriter = 2) =>
        """{"protocol":{"minReaderVersion":__R__,"minWriterVersion":__W__}}"""
            .Replace("__R__", Int(minReader), StringComparison.Ordinal)
            .Replace("__W__", Int(minWriter), StringComparison.Ordinal);

    public static string ProtocolWithReaderFeature(string feature, int minReader = 3, int minWriter = 7) =>
        """{"protocol":{"minReaderVersion":__R__,"minWriterVersion":__W__,"readerFeatures":["__F__"],"writerFeatures":["__F__"]}}"""
            .Replace("__R__", Int(minReader), StringComparison.Ordinal)
            .Replace("__W__", Int(minWriter), StringComparison.Ordinal)
            .Replace("__F__", feature, StringComparison.Ordinal);

    public static string Metadata(string id = "t", string[]? partitionColumns = null) =>
        """{"metaData":{"id":"__ID__","format":{"provider":"parquet","options":{}},"schemaString":"__S__","partitionColumns":[__P__],"configuration":{}}}"""
            .Replace("__ID__", id, StringComparison.Ordinal)
            .Replace("__S__", EmptySchema, StringComparison.Ordinal)
            .Replace("__P__", partitionColumns is null ? "" : string.Join(",", partitionColumns.Select(p => $"\"{p}\"")), StringComparison.Ordinal);

    /// <summary>A <c>metaData</c> line carrying an arbitrary table <c>configuration</c> map (e.g.
    /// <c>delta.deletedFileRetentionDuration</c>), used to test VACUUM honoring the table-configured
    /// retention property.</summary>
    public static string MetadataWithConfig(params (string Key, string Value)[] configuration)
    {
        string config = "{" + string.Join(
            ",", configuration.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\"")) + "}";
        return """{"metaData":{"id":"t","format":{"provider":"parquet","options":{}},"schemaString":"__S__","partitionColumns":[],"configuration":__C__}}"""
            .Replace("__S__", EmptySchema, StringComparison.Ordinal)
            .Replace("__C__", config, StringComparison.Ordinal);
    }

    public static string Add(string path, string? stats = null, (string Key, string Value)[]? partitionValues = null)
    {
        string pv = partitionValues is null
            ? "{}"
            : "{" + string.Join(",", partitionValues.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\"")) + "}";
        string statsField = stats is null ? "" : ",\"stats\":" + System.Text.Json.JsonSerializer.Serialize(stats);
        return """{"add":{"path":"__PATH__","partitionValues":__PV__,"size":1,"modificationTime":1,"dataChange":true__STATS__}}"""
            .Replace("__PATH__", path, StringComparison.Ordinal)
            .Replace("__PV__", pv, StringComparison.Ordinal)
            .Replace("__STATS__", statsField, StringComparison.Ordinal);
    }

    public static string Remove(string path) =>
        """{"remove":{"path":"__P__","deletionTimestamp":1,"dataChange":true}}"""
            .Replace("__P__", path, StringComparison.Ordinal);

    public static string Txn(string appId, long version) =>
        """{"txn":{"appId":"__APP__","version":__V__}}"""
            .Replace("__APP__", appId, StringComparison.Ordinal)
            .Replace("__V__", version.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    // ---- writers over a backend ----

    public static async Task WriteCommitAsync(IStorageBackend backend, long version, params string[] lines)
    {
        string name = LogPath(version.ToString(CultureInfo.InvariantCulture).PadLeft(20, '0') + ".json");
        byte[] content = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        await backend.PutIfAbsentAsync(name, content, CancellationToken.None);
    }

    public static async Task WriteCheckpointAsync(IStorageBackend backend, long version, CheckpointFixture fixture)
    {
        byte[] parquet = await fixture.ToParquetAsync();
        string name = LogPath(Pad20(version) + ".checkpoint.parquet");
        await backend.PutIfAbsentAsync(name, parquet, CancellationToken.None);
    }

    public static async Task WriteMultipartCheckpointAsync(
        IStorageBackend backend, long version, CheckpointFixture fixture, int parts)
    {
        byte[][] partBytes = await fixture.ToPartsAsync(parts);
        for (int p = 1; p <= parts; p++)
        {
            string name = LogPath($"{Pad20(version)}.checkpoint.{Pad10(p)}.{Pad10(parts)}.parquet");
            await backend.PutIfAbsentAsync(name, partBytes[p - 1], CancellationToken.None);
        }
    }

    /// <summary>Writes only the specified <paramref name="partsToWrite"/> of an <paramref name="parts"/>-part
    /// checkpoint, modelling an incomplete multi-part checkpoint (a missing part).</summary>
    public static async Task WritePartialMultipartCheckpointAsync(
        IStorageBackend backend, long version, CheckpointFixture fixture, int parts, params int[] partsToWrite)
    {
        byte[][] partBytes = await fixture.ToPartsAsync(parts);
        foreach (int p in partsToWrite)
        {
            string name = LogPath($"{Pad20(version)}.checkpoint.{Pad10(p)}.{Pad10(parts)}.parquet");
            await backend.PutIfAbsentAsync(name, partBytes[p - 1], CancellationToken.None);
        }
    }

    public static async Task WriteRawCheckpointAsync(IStorageBackend backend, long version, byte[] content)
    {
        string name = LogPath(Pad20(version) + ".checkpoint.parquet");
        await backend.PutIfAbsentAsync(name, content, CancellationToken.None);
    }

    /// <summary>Writes one part of a multi-part checkpoint with raw <paramref name="content"/> (used to
    /// place a valid part alongside a deliberately-corrupt later part).</summary>
    public static async Task WriteRawMultipartPartAsync(
        IStorageBackend backend, long version, int part, int parts, byte[] content)
    {
        string name = LogPath($"{Pad20(version)}.checkpoint.{Pad10(part)}.{Pad10(parts)}.parquet");
        await backend.PutIfAbsentAsync(name, content, CancellationToken.None);
    }

    public static async Task WriteLastCheckpointAsync(IStorageBackend backend, long version, int? parts = null)
    {
        string json = parts is null
            ? string.Create(CultureInfo.InvariantCulture, $$"""{"version":{{version}},"size":1}""")
            : string.Create(CultureInfo.InvariantCulture, $$"""{"version":{{version}},"size":1,"parts":{{parts}}}""");
        await backend.PutIfAbsentAsync("_delta_log/_last_checkpoint", Encoding.UTF8.GetBytes(json), CancellationToken.None);
    }

    // ---- canonical snapshot description (content, not reference, equality) ----

    public static string Describe(Snapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.Append("version=").Append(snapshot.Version).Append('\n');
        sb.Append("protocol=").Append(snapshot.Protocol.MinReaderVersion).Append('/')
            .Append(snapshot.Protocol.MinWriterVersion)
            .Append(" rf=[").Append(string.Join(",", snapshot.Protocol.ReaderFeatures.Sort(StringComparer.Ordinal)))
            .Append("] wf=[").Append(string.Join(",", snapshot.Protocol.WriterFeatures.Sort(StringComparer.Ordinal)))
            .Append("]\n");
        sb.Append("metadata id=").Append(snapshot.Metadata.Id)
            .Append(" name=").Append(snapshot.Metadata.Name ?? "∅")
            .Append(" provider=").Append(snapshot.Metadata.Format.Provider)
            .Append(" schema=").Append(snapshot.Metadata.SchemaString)
            .Append(" part=[").Append(string.Join(",", snapshot.Metadata.PartitionColumns))
            .Append("] config=").Append(DescribeMap(snapshot.Metadata.Configuration))
            .Append('\n');
        sb.Append("txns=");
        foreach ((string appId, long v) in snapshot.Transactions)
        {
            sb.Append(appId).Append(':').Append(v).Append(';');
        }

        sb.Append('\n');
        foreach (AddFileAction add in snapshot.ActiveFiles.OrderBy(a => a.Path, StringComparer.Ordinal))
        {
            sb.Append("add path=").Append(add.Path)
                .Append(" size=").Append(add.Size)
                .Append(" mtime=").Append(add.ModificationTime)
                .Append(" dc=").Append(add.DataChange)
                .Append(" pv=").Append(DescribeNullableMap(add.PartitionValues))
                .Append(" tags=").Append(DescribeMap(add.Tags))
                .Append(" stats=").Append(DescribeStats(add.Stats))
                .Append('\n');
        }

        return sb.ToString();
    }

    private static string DescribeMap(System.Collections.Immutable.ImmutableSortedDictionary<string, string> map) =>
        "{" + string.Join(",", map.Select(kv => kv.Key + "=" + kv.Value)) + "}";

    private static string DescribeNullableMap(System.Collections.Immutable.ImmutableSortedDictionary<string, string?> map) =>
        "{" + string.Join(",", map.Select(kv => kv.Key + "=" + (kv.Value ?? "∅"))) + "}";

    private static string DescribeStats(FileStatistics? stats)
    {
        if (stats is null)
        {
            return "∅";
        }

        string min = string.Join(",", stats.MinValues.Select(kv => kv.Key + ":" + kv.Value.Raw));
        string max = string.Join(",", stats.MaxValues.Select(kv => kv.Key + ":" + kv.Value.Raw));
        string nulls = string.Join(",", stats.NullCount.Select(kv => kv.Key + ":" + kv.Value));
        return $"n={stats.NumRecords} min={{{min}}} max={{{max}}} nc={{{nulls}}} tb={stats.TightBounds}";
    }

    private static string LogPath(string fileName) => "_delta_log/" + fileName;

    private static string Pad20(long version) => version.ToString(CultureInfo.InvariantCulture).PadLeft(20, '0');

    private static string Pad10(int value) => value.ToString(CultureInfo.InvariantCulture).PadLeft(10, '0');
}
