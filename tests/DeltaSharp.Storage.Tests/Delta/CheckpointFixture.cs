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
        (string Key, string Value)[]? tags = null,
        DvColumns? deletionVector = null)
    {
        var remove = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["dataChange"] = dataChange,
            ["extendedFileMetadata"] = extendedFileMetadata,
            ["tags"] = ToMap(tags),
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

    /// <summary>Authors a MINIMAL classic checkpoint whose ONLY column is the <c>add.partitionValues</c> MAP —
    /// no scalar precedes it, so the checkpoint reader's FIRST (and only) reconstructed column is that map and
    /// a row-count forge trips the MAP reconstruction path (<c>CheckpointColumns.ForEachMapEntry</c>) rather
    /// than a scalar slot check. The map's KEY leaf is renamed to <paramref name="keyLeafName"/> by forging the
    /// footer AFTER serialization (Parquet.Net's untyped serializer hard-requires the literal <c>key</c>/
    /// <c>value</c> leaf names), so the resolved key <c>DataField.Path</c> carries that FILE-derived leaf name
    /// (<c>CheckpointSchema.Map</c> returns Parquet.Net's logical <c>.Key</c>/<c>.Value</c> verbatim). The row
    /// group's declared row count is then forged so the map reconstruction fails closed: <b>over</b>-declaring
    /// (one MORE than the two entries) trips the row-COUNT check (<c>EnsureRowCount</c>); <b>under</b>-declaring
    /// (one FEWER, <paramref name="overDeclareRows"/> = false) trips the row-IN-RANGE check
    /// (<c>EnsureRowInRange</c>, whose repetition-0 slot advances <c>row</c> past the shrunken bound). Either
    /// way the surfaced message must never echo the leaf path (#653).</summary>
    internal static async Task<byte[]> MalformedAddPartitionValuesMapAsync(
        string keyLeafName, bool overDeclareRows = true)
    {
        var schema = new ParquetSchema(new StructField("add",
            new MapField("partitionValues",
                new DataField<string>("key", nullable: false),
                new DataField<string?>("value"))));
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["add"] = new Dictionary<string, object?>
                {
                    ["partitionValues"] = new Dictionary<string, string?> { ["k1"] = "v1" },
                },
            },
            new Dictionary<string, object?>
            {
                ["add"] = new Dictionary<string, object?>
                {
                    ["partitionValues"] = new Dictionary<string, string?> { ["k2"] = "v2" },
                },
            },
        };

        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            await ParquetSerializer.SerializeUntypedAsync(rows, schema, stream);
            bytes = stream.ToArray();
        }

        bytes = await ParquetTestHelpers.ForgeLeafColumnNameAsync(bytes, "key", keyLeafName);
        long actual = await ParquetTestHelpers.RowGroupNumRowsAsync(bytes, 0);
        return await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(
            bytes, 0, overDeclareRows ? actual + 1 : actual - 1);
    }

    /// <summary>Authors a MINIMAL classic checkpoint whose ONLY column is the <c>add.partitionValues</c> MAP
    /// with a physically <b>INT32</b> value leaf (renamed to <paramref name="valueLeafName"/> after
    /// serialization). The reader reads every map value leaf as a string (<c>ReadOnlyMemory&lt;char&gt;</c>),
    /// so the INT32 physical column casts to the wrong <c>RawColumnData&lt;T&gt;</c> and
    /// <c>CheckpointColumns.ReadRawAsync</c> fails closed with the "unexpected physical type" message. The
    /// value leaf is FILE-derived (<c>CheckpointSchema.Map</c> returns Parquet.Net's logical <c>.Value</c>
    /// verbatim), so it carries the caller's sentinel — proving that fail-closed message never echoes the leaf
    /// path (#653, R4 physical-type finding). The KEY stays a well-formed string so the value read (not the
    /// key read) is the site that trips.</summary>
    internal static async Task<byte[]> MalformedAddPartitionValuesMapValueTypeAsync(string valueLeafName)
    {
        var schema = new ParquetSchema(new StructField("add",
            new MapField("partitionValues",
                new DataField<string>("key", nullable: false),
                new DataField<int?>("value"))));
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["add"] = new Dictionary<string, object?>
                {
                    ["partitionValues"] = new Dictionary<string, int?> { ["k1"] = 7 },
                },
            },
        };

        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            await ParquetSerializer.SerializeUntypedAsync(rows, schema, stream);
            bytes = stream.ToArray();
        }

        // Rename the INT32 value leaf to the sentinel (its resolved DataField.Path is what a reverted scrub
        // would echo). ForgeLeafColumnNameAsync rewrites BOTH the schema element and the chunk PathInSchema, so
        // the column still LOADS and DECODES (as INT32) — the physical-type mismatch is on TYPE, not lookup.
        return await ParquetTestHelpers.ForgeLeafColumnNameAsync(bytes, "value", valueLeafName);
    }

    /// <summary>Authors a MINIMAL classic checkpoint whose ONLY column is a legacy 1-level <b>REPEATED</b>
    /// primitive <c>add.size</c> (a <c>DataField</c> with <c>isArray=true</c>, <c>MaxRepetitionLevel=1</c>
    /// directly under the <c>add</c> struct). <c>CheckpointSchema.Scalar</c> resolves <c>add/size</c> as an
    /// ordinary scalar <c>DataField</c> (it matches only the expected name), so the scalar reader reaches
    /// <c>CheckpointColumns.FillScalar</c>, which fails closed on the unexpected repetition
    /// (<c>col.MaxRepetition != 0</c>). Authored with the LOW-LEVEL <see cref="ParquetWriter"/> because the
    /// untyped serializer models a repeated primitive as a 3-level list, never a 1-level repeated leaf. A
    /// scalar leaf name is a bounded Delta-protocol vocabulary (<c>add/size</c>), so a reverted scrub would
    /// re-echo it <i>quoted</i> — the no-echo assertion pins the absence of that quoting (#653).</summary>
    internal static async Task<byte[]> MalformedRepeatedAddSizeScalarAsync()
    {
        var repeated = new DataField("size", typeof(long), isArray: true);
        var schema = new ParquetSchema(new StructField("add", repeated));
        DataField[] leaves = schema.GetDataFields();

        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            // One row carrying two repeated long elements (rep 0 then rep 1) → MaxRepetitionLevel=1.
            await rowGroup.WriteAsync<long>(
                leaves[0], new ReadOnlyMemory<long?>(new long?[] { 1L, 2L }), new[] { 0, 1 }, null, CancellationToken.None);
        }

        return stream.ToArray();
    }

    /// <summary>Authors a MINIMAL classic checkpoint whose ONLY column is the <c>metaData.partitionColumns</c>
    /// LIST — no scalar precedes it, so the reader's FIRST (and only) reconstructed column is that list and a
    /// row-count forge trips the LIST reconstruction path (<c>CheckpointColumns.ForEachListElement</c>). Its
    /// ELEMENT leaf is named <paramref name="elementLeafName"/> DIRECTLY (Parquet.Net's untyped serializer
    /// accepts a custom list-element name), so the resolved element <c>DataField.Path</c> carries that
    /// FILE-derived leaf name (<c>CheckpointSchema.ListElement</c> returns Parquet.Net's logical <c>.Item</c>
    /// verbatim). The row group's declared row count is then forged to one MORE than the two elements it holds,
    /// so the list reconstruction fails closed at the row-count check — proving that message never echoes the
    /// leaf path (#653).</summary>
    internal static async Task<byte[]> MalformedMetaPartitionColumnsListAsync(string elementLeafName)
    {
        var schema = new ParquetSchema(new StructField("metaData",
            new ListField("partitionColumns", new DataField<string?>(elementLeafName))));
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["metaData"] = new Dictionary<string, object?>
                {
                    ["partitionColumns"] = new List<string?> { "c1" },
                },
            },
            new Dictionary<string, object?>
            {
                ["metaData"] = new Dictionary<string, object?>
                {
                    ["partitionColumns"] = new List<string?> { "c2" },
                },
            },
        };

        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            await ParquetSerializer.SerializeUntypedAsync(rows, schema, stream);
            bytes = stream.ToArray();
        }

        long actual = await ParquetTestHelpers.RowGroupNumRowsAsync(bytes, 0);
        return await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(bytes, 0, actual + 1);
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
            StringMap("tags"),
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
