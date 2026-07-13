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
    private bool _withDeletionVector;
    private bool _dvLeavesRequired;

    /// <summary>A nested <c>deletionVector</c> struct's column values for a checkpoint <c>add</c>/
    /// <c>remove</c> (issue #527). Each field is independently nullable so a test can model a valid DV
    /// (all present) or a MALFORMED one (e.g. <see cref="StorageType"/> present but
    /// <see cref="SizeInBytes"/> omitted) that must fail closed on read.</summary>
    public readonly record struct DvColumns(
        string? StorageType,
        string? PathOrInlineDv,
        int? Offset,
        int? SizeInBytes,
        long? Cardinality)
    {
        /// <summary>A well-formed relative-path ('u') DV descriptor for round-trip/parity tests.</summary>
        public static DvColumns Uuid(
            string pathOrInlineDv, int offset, int sizeInBytes, long cardinality) =>
            new("u", pathOrInlineDv, offset, sizeInBytes, cardinality);

        internal Dictionary<string, object?> ToStruct()
        {
            var dv = new Dictionary<string, object?>();

            // Each present field is set; an omitted (null) field is left out entirely so the reader sees a
            // null leaf — the mechanism a malformed-DV test uses to drop a required sub-column.
            if (StorageType is not null)
            {
                dv["storageType"] = StorageType;
            }

            if (PathOrInlineDv is not null)
            {
                dv["pathOrInlineDv"] = PathOrInlineDv;
            }

            if (Offset is not null)
            {
                dv["offset"] = Offset.Value;
            }

            if (SizeInBytes is not null)
            {
                dv["sizeInBytes"] = SizeInBytes.Value;
            }

            if (Cardinality is not null)
            {
                dv["cardinality"] = Cardinality.Value;
            }

            return dv;
        }
    }

    /// <summary>Emits the DV struct's <c>storageType</c>/<c>pathOrInlineDv</c>/<c>sizeInBytes</c>/
    /// <c>cardinality</c> leaves as REQUIRED (non-nullable) <b>within the optional</b> <c>deletionVector</c>
    /// struct — the depth-2 definition-level shape real Spark writes (leaf MaxDefinitionLevel=2), versus the
    /// fixture's default all-optional leaves (MaxDefinitionLevel=3). <c>offset</c> stays optional (inline DVs
    /// carry none). The reader is parametric on per-field max-def, so a required-leaf round trip hardens the
    /// parity claim against the exact shape Spark emits (issue #527). Must be set before serialization; a
    /// malformed-DV fixture (which omits a required leaf) cannot use this variant.</summary>
    public CheckpointFixture WithRequiredDvLeaves()
    {
        _dvLeavesRequired = true;
        return this;
    }

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
        (string Key, string Value)[]? tags = null,
        DvColumns? deletionVector = null)
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

        if (deletionVector is not null)
        {
            _withDeletionVector = true;
            add["deletionVector"] = deletionVector.Value.ToStruct();
        }

        return Row("add", add);
    }

    public CheckpointFixture Remove(
        string path,
        long? deletionTimestamp = null,
        bool dataChange = false,
        bool extendedFileMetadata = false,
        (string Key, string? Value)[]? partitionValues = null,
        long? size = null,
        DvColumns? deletionVector = null)
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

        if (deletionVector is not null)
        {
            _withDeletionVector = true;
            remove["deletionVector"] = deletionVector.Value.ToStruct();
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

    /// <summary>Serializes all accumulated rows to a single checkpoint Parquet part whose row groups hold at
    /// most <paramref name="rowGroupSize"/> rows, forcing a MULTI-row-group part so a checkpoint-reader test
    /// can exercise the per-row-group Dremel decode across a row-group boundary (issue #527 DV alignment).</summary>
    public async Task<byte[]> ToParquetAsync(int rowGroupSize)
    {
        using var stream = new MemoryStream();
        await ParquetSerializer.SerializeUntypedAsync(
            _rows, BuildSchema(), stream, new ParquetOptions { RowGroupSize = rowGroupSize });
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

    /// <summary>The standard Delta classic-checkpoint Parquet schema (all v1-baseline action columns), plus
    /// the nested <c>deletionVector</c> struct on <c>add</c>/<c>remove</c> when a DV-bearing action was added
    /// (issue #527) — so existing DV-free checkpoints keep their exact baseline schema.</summary>
    public ParquetSchema BuildSchema()
    {
        var txn = new StructField("txn",
            NullableString("appId"),
            new DataField<long?>("version"),
            new DataField<long?>("lastUpdated"));

        var addFields = new List<Field>
        {
            NullableString("path"),
            StringMap("partitionValues"),
            new DataField<long?>("size"),
            new DataField<long?>("modificationTime"),
            new DataField<bool?>("dataChange"),
            NullableString("stats"),
            StringMap("tags"),
        };
        if (_withDeletionVector)
        {
            addFields.Add(DeletionVectorStruct());
        }

        var add = new StructField("add", addFields.ToArray());

        var removeFields = new List<Field>
        {
            NullableString("path"),
            new DataField<long?>("deletionTimestamp"),
            new DataField<bool?>("dataChange"),
            new DataField<bool?>("extendedFileMetadata"),
            StringMap("partitionValues"),
            new DataField<long?>("size"),
        };
        if (_withDeletionVector)
        {
            removeFields.Add(DeletionVectorStruct());
        }

        var remove = new StructField("remove", removeFields.ToArray());

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

    /// <summary>The nested <c>deletionVector</c> struct schema. By default all sub-fields are nullable so a
    /// test can omit a required one to model a malformed DV (fail-closed on read). When
    /// <see cref="WithRequiredDvLeaves"/> is set, <c>storageType</c>/<c>pathOrInlineDv</c>/<c>sizeInBytes</c>/
    /// <c>cardinality</c> are REQUIRED within the still-optional struct (Spark's depth-2 shape); <c>offset</c>
    /// stays optional either way.</summary>
    private StructField DeletionVectorStruct() =>
        _dvLeavesRequired
            ? new StructField("deletionVector",
                new DataField<string>("storageType", nullable: false),
                new DataField<string>("pathOrInlineDv", nullable: false),
                new DataField<int?>("offset"),
                new DataField<int>("sizeInBytes"),
                new DataField<long>("cardinality"))
            : new StructField("deletionVector",
                NullableString("storageType"),
                NullableString("pathOrInlineDv"),
                new DataField<int?>("offset"),
                new DataField<int?>("sizeInBytes"),
                new DataField<long?>("cardinality"));

    private static DataField NullableString(string name) => new DataField<string?>(name);

    private static MapField StringMap(string name) =>
        new(name, new DataField<string>("key", nullable: false), new DataField<string?>("value"));
}
