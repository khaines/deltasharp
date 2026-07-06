using System.Collections.Immutable;
using Parquet;
using Parquet.Schema;
using Parquet.Serialization;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Test-only writer that emits a <b>standard-layout</b> Delta classic checkpoint Parquet (design §2.10.3)
/// from a fluent list of surviving actions. It deliberately uses Parquet.Net's high-level
/// <see cref="ParquetSerializer.SerializeUntypedAsync"/> against an explicit schema — a code path fully
/// independent of the production low-level <c>DeltaCheckpointReader</c> — so a round-trip / parity test
/// exercises a real decode rather than a self-consistent tautology. The schema mirrors what Spark and
/// delta-rs write (nullable top-level action structs; 3-level MAP <c>key_value</c> and LIST <c>list</c>).
/// </summary>
internal sealed class CheckpointFixture
{
    private readonly List<IDictionary<string, object?>> _rows = [];

    public CheckpointFixture Protocol(
        int minReaderVersion, int minWriterVersion, string[]? readerFeatures = null, string[]? writerFeatures = null)
    {
        var protocol = new Dictionary<string, object?>
        {
            ["minReaderVersion"] = minReaderVersion,
            ["minWriterVersion"] = minWriterVersion,
        };
        if (readerFeatures is not null)
        {
            protocol["readerFeatures"] = readerFeatures.ToList();
        }

        if (writerFeatures is not null)
        {
            protocol["writerFeatures"] = writerFeatures.ToList();
        }

        return Row("protocol", protocol);
    }

    public CheckpointFixture Metadata(
        string id,
        string schemaString,
        string[]? partitionColumns = null,
        (string Key, string Value)[]? configuration = null,
        string provider = "parquet",
        string? name = null,
        long? createdTime = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["format"] = new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["options"] = new Dictionary<string, string?>(),
            },
            ["schemaString"] = schemaString,
            ["partitionColumns"] = (partitionColumns ?? []).ToList(),
            ["configuration"] = ToMap(configuration),
        };
        if (name is not null)
        {
            metadata["name"] = name;
        }

        if (createdTime is not null)
        {
            metadata["createdTime"] = createdTime.Value;
        }

        return Row("metaData", metadata);
    }

    public CheckpointFixture Add(
        string path,
        long size,
        (string Key, string? Value)[]? partitionValues = null,
        string? stats = null,
        long? modificationTime = 0,
        bool? dataChange = true,
        (string Key, string Value)[]? tags = null)
    {
        var add = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["partitionValues"] = ToNullableMap(partitionValues),
            ["size"] = size,
            ["tags"] = ToMap(tags),
        };

        // A null modificationTime/dataChange omits the column entirely, modelling a foreign checkpoint that
        // leaves the optional field absent — so the reader's default (0 / true) is exercised.
        if (modificationTime is not null)
        {
            add["modificationTime"] = modificationTime.Value;
        }

        if (dataChange is not null)
        {
            add["dataChange"] = dataChange.Value;
        }

        if (stats is not null)
        {
            add["stats"] = stats;
        }

        return Row("add", add);
    }

    public CheckpointFixture Remove(
        string path,
        long? deletionTimestamp = null,
        bool dataChange = false,
        bool extendedFileMetadata = false,
        (string Key, string? Value)[]? partitionValues = null,
        long? size = null)
    {
        var remove = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["dataChange"] = dataChange,
            ["extendedFileMetadata"] = extendedFileMetadata,
        };
        if (deletionTimestamp is not null)
        {
            remove["deletionTimestamp"] = deletionTimestamp.Value;
        }

        if (partitionValues is not null)
        {
            remove["partitionValues"] = ToNullableMap(partitionValues);
        }

        if (size is not null)
        {
            remove["size"] = size.Value;
        }

        return Row("remove", remove);
    }

    public CheckpointFixture Txn(string appId, long version, long? lastUpdated = null)
    {
        var txn = new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["version"] = version,
        };
        if (lastUpdated is not null)
        {
            txn["lastUpdated"] = lastUpdated.Value;
        }

        return Row("txn", txn);
    }

    /// <summary>Serializes all accumulated rows to a single checkpoint Parquet part.</summary>
    public async Task<byte[]> ToParquetAsync()
    {
        using var stream = new MemoryStream();
        await ParquetSerializer.SerializeUntypedAsync(_rows, BuildSchema(), stream);
        return stream.ToArray();
    }

    /// <summary>Splits the accumulated rows across <paramref name="parts"/> checkpoint Parquet files
    /// (round-robin) to model a multi-part classic checkpoint.</summary>
    public async Task<byte[][]> ToPartsAsync(int parts)
    {
        ParquetSchema schema = BuildSchema();
        var buckets = new List<IDictionary<string, object?>>[parts];
        for (int p = 0; p < parts; p++)
        {
            buckets[p] = [];
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            buckets[i % parts].Add(_rows[i]);
        }

        var result = new byte[parts][];
        for (int p = 0; p < parts; p++)
        {
            using var stream = new MemoryStream();
            await ParquetSerializer.SerializeUntypedAsync(buckets[p], schema, stream);
            result[p] = stream.ToArray();
        }

        return result;
    }

    private CheckpointFixture Row(string action, Dictionary<string, object?> body)
    {
        _rows.Add(new Dictionary<string, object?> { [action] = body });
        return this;
    }

    private static Dictionary<string, string?> ToNullableMap((string Key, string? Value)[]? entries)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach ((string key, string? value) in entries ?? [])
        {
            map[key] = value;
        }

        return map;
    }

    private static Dictionary<string, string?> ToMap((string Key, string Value)[]? entries)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach ((string key, string value) in entries ?? [])
        {
            map[key] = value;
        }

        return map;
    }

    /// <summary>The standard Delta classic-checkpoint Parquet schema (all v1-baseline action columns).</summary>
    public static ParquetSchema BuildSchema()
    {
        var txn = new StructField("txn",
            NullableString("appId"),
            new DataField<long?>("version"),
            new DataField<long?>("lastUpdated"));

        var add = new StructField("add",
            NullableString("path"),
            StringMap("partitionValues"),
            new DataField<long?>("size"),
            new DataField<long?>("modificationTime"),
            new DataField<bool?>("dataChange"),
            NullableString("stats"),
            StringMap("tags"));

        var remove = new StructField("remove",
            NullableString("path"),
            new DataField<long?>("deletionTimestamp"),
            new DataField<bool?>("dataChange"),
            new DataField<bool?>("extendedFileMetadata"),
            StringMap("partitionValues"),
            new DataField<long?>("size"));

        var format = new StructField("format",
            NullableString("provider"),
            StringMap("options"));

        var metaData = new StructField("metaData",
            NullableString("id"),
            NullableString("name"),
            NullableString("description"),
            format,
            NullableString("schemaString"),
            new ListField("partitionColumns", NullableString("element")),
            StringMap("configuration"),
            new DataField<long?>("createdTime"));

        var protocol = new StructField("protocol",
            new DataField<int?>("minReaderVersion"),
            new DataField<int?>("minWriterVersion"),
            new ListField("readerFeatures", NullableString("element")),
            new ListField("writerFeatures", NullableString("element")));

        return new ParquetSchema(txn, add, remove, metaData, protocol);
    }

    private static DataField NullableString(string name) => new DataField<string?>(name);

    private static MapField StringMap(string name) =>
        new(name, new DataField<string>("key", nullable: false), new DataField<string?>("value"));
}
