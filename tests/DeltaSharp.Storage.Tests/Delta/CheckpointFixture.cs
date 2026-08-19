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
            protocol["readerFeatures"] = ToStringList(readerFeatures);
        }

        if (writerFeatures is not null)
        {
            protocol["writerFeatures"] = ToStringList(writerFeatures);
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
                ["options"] = ToNullableMap(null),
            },
            ["schemaString"] = schemaString,
            ["partitionColumns"] = ToStringList(partitionColumns ?? []),
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
                    ["partitionValues"] = new Dictionary<ReadOnlyMemory<char>, ReadOnlyMemory<char>?>
                    {
                        [Memory("k1")] = Memory("v1"),
                    },
                },
            },
            new Dictionary<string, object?>
            {
                ["add"] = new Dictionary<string, object?>
                {
                    ["partitionValues"] = new Dictionary<ReadOnlyMemory<char>, ReadOnlyMemory<char>?>
                    {
                        [Memory("k2")] = Memory("v2"),
                    },
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

    /// <summary>Authors a minimal valid checkpoint add action with a null <c>partitionValues</c> map value
    /// through the low-level writer. Parquet.Net 6.1's high-level untyped serializer requires
    /// <see cref="ReadOnlyMemory{T}"/> string map values and materializes null nullable-memory values as empty
    /// strings, so this keeps the checkpoint reader's real null-value coverage independent of that serializer
    /// conversion seam.</summary>
    internal static async Task<byte[]> AddWithNullPartitionValueAsync()
    {
        var schema = new ParquetSchema(new StructField("add",
            new DataField<string?>("path"),
            new MapField("partitionValues",
                new DataField<string>("key", nullable: false),
                new DataField<string?>("value")),
            new DataField<long?>("size")));
        DataField[] leaves = schema.GetDataFields(); // [path, key, value, size]

        var path = new ReadOnlyMemory<char>?[] { Memory("part-null.parquet") };
        var keys = new ReadOnlyMemory<char>?[] { Memory("year"), Memory("month") };
        var values = new ReadOnlyMemory<char>?[] { Memory("2026"), null };
        var size = new long?[] { 100L };

        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            await rowGroup.WriteAsync<ReadOnlyMemory<char>>(
                leaves[0], new ReadOnlyMemory<ReadOnlyMemory<char>?>(path), null, null, CancellationToken.None);
            await rowGroup.WriteAsync<ReadOnlyMemory<char>>(
                leaves[1], new ReadOnlyMemory<ReadOnlyMemory<char>?>(keys), new[] { 0, 1 }, null, CancellationToken.None);
            await rowGroup.WriteAsync<ReadOnlyMemory<char>>(
                leaves[2], new ReadOnlyMemory<ReadOnlyMemory<char>?>(values), new[] { 0, 1 }, null, CancellationToken.None);
            await rowGroup.WriteAsync<long>(
                leaves[3], new ReadOnlyMemory<long?>(size), null, null, CancellationToken.None);
        }

        return stream.ToArray();
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
                    ["partitionValues"] = new Dictionary<ReadOnlyMemory<char>, int?> { [Memory("k1")] = 7 },
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

    /// <summary>Authors a MINIMAL classic checkpoint whose ONLY column is the <c>add.partitionValues</c> MAP,
    /// written through the <b>low-level</b> <see cref="ParquetRowGroupWriter"/>'s <c>WriteAsync&lt;T&gt;</c> so
    /// the map's KEY and VALUE level streams are <b>DESYNCED</b> — something the high-level untyped serializer
    /// can never emit (a map's key and value share one repeated <c>key_value</c> group, so it always authors
    /// identical key/value slot counts). This is the R6 refutation of the R5 "the struct-only low-level writer
    /// can't author string map leaves" annotation: <c>ReadOnlyMemory&lt;char&gt;</c> satisfies the writer's
    /// <c>T : struct</c> constraint, so it authors a genuine STRING (BYTE_ARRAY/UTF8) leaf that the reader
    /// decodes cleanly as <c>ReadOnlyMemory&lt;char&gt;</c> — the physical-type guard does NOT trip first. The
    /// KEY leaf is written with repetition levels <c>0,1,0</c> (3 slots) and the VALUE leaf with <c>0,0</c>
    /// (2 slots); both carry two repetition-0 markers, so the row group's row count stays a consistent 2 (no
    /// numRows forge) and the map reconstruction reaches the key/value <b>slot-count</b> check — not a
    /// row-count check — where <c>keys.Definition.Length</c> (3) ≠ <c>values.Definition.Length</c> (2) trips
    /// <c>CheckpointColumns.ForEachMapEntry</c>'s slot-mismatch branch. The key leaf is renamed to
    /// <paramref name="keyLeafName"/> after write; its resolved key <c>DataField.Path</c> is FILE-derived
    /// (<c>CheckpointSchema.Map</c> returns Parquet.Net's logical <c>.Key</c> verbatim), so a reverted scrub
    /// would echo <c>add/partitionValues/key_value/&lt;sentinel&gt;</c> into that message (#653).</summary>
    internal static async Task<byte[]> MalformedAddPartitionValuesMapSlotMismatchAsync(string keyLeafName)
    {
        var schema = new ParquetSchema(new StructField("add",
            new MapField("partitionValues",
                new DataField<string>("key", nullable: false),
                new DataField<string?>("value"))));
        DataField[] leaves = schema.GetDataFields();   // [key, value]

        // Every key/value is present (non-null) so all inferred definition levels sit at max — the divergence
        // is purely in the NUMBER of slots (3 key vs 2 value), authored via the desynced repetition streams.
        var keys = new ReadOnlyMemory<char>?[] { "k1".AsMemory(), "k2".AsMemory(), "k3".AsMemory() };
        var values = new ReadOnlyMemory<char>?[] { "v1".AsMemory(), "v2".AsMemory() };

        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
            {
                using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
                // ReadOnlyMemory<char> : struct → the low-level writer authors a real STRING leaf. Definition
                // levels are inferred from the (all-non-null) value arrays; repetition levels are supplied
                // directly — 3 for the key (0,1,0), 2 for the value (0,0) — so keys.Definition.Length (3) !=
                // values.Definition.Length (2). Both streams still carry two repetition-0 rows (numRows == 2).
                await rowGroup.WriteAsync<ReadOnlyMemory<char>>(
                    leaves[0], new ReadOnlyMemory<ReadOnlyMemory<char>?>(keys), new[] { 0, 1, 0 }, null, CancellationToken.None);
                await rowGroup.WriteAsync<ReadOnlyMemory<char>>(
                    leaves[1], new ReadOnlyMemory<ReadOnlyMemory<char>?>(values), new[] { 0, 0 }, null, CancellationToken.None);
            }

            bytes = stream.ToArray();
        }

        return await ParquetTestHelpers.ForgeLeafColumnNameAsync(bytes, "key", keyLeafName);
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
                    ["partitionColumns"] = new List<ReadOnlyMemory<char>?> { Memory("c1") },
                },
            },
            new Dictionary<string, object?>
            {
                ["metaData"] = new Dictionary<string, object?>
                {
                    ["partitionColumns"] = new List<ReadOnlyMemory<char>?> { Memory("c2") },
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

    // Parquet.Net 6.1's untyped serializer requires ReadOnlyMemory<char> string map keys/values, but
    // ReadOnlyMemory<char> is a STRUCT whose default equality compares the (object, offset, length) triple —
    // NOT the character content. Keying the builder dictionary by it would therefore lose the content-based
    // last-write-wins dedup these fixtures relied on before #832 (two entries spelling the same key would
    // both survive, silently changing the authored map's cardinality). So accumulate under `string` +
    // StringComparer.Ordinal — exactly the pre-#832 semantics — and project to ReadOnlyMemory<char> only at
    // the final serializer boundary, once per surviving entry.
    //
    // Beyond guarding today's fixtures, this is forward protection: the moment a fixture feeds a duplicate
    // key, a ReadOnlyMemory-keyed builder would double-count it. `Fixture_DuplicateMapKeys_CollapseByOrdinalContent`
    // in DeltaCheckpointReaderTests feeds exactly that and asserts the AUTHORED map cardinality straight off
    // the footer, so the ordinal accumulation cannot silently be dropped.
    private static Dictionary<ReadOnlyMemory<char>, ReadOnlyMemory<char>?> ToNullableMap((string Key, string? Value)[]? entries)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach ((string key, string? value) in entries ?? [])
        {
            map[key] = value;
        }

        return ToSerializerMap(map);
    }

    private static Dictionary<ReadOnlyMemory<char>, ReadOnlyMemory<char>?> ToMap((string Key, string Value)[]? entries)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach ((string key, string value) in entries ?? [])
        {
            map[key] = value;
        }

        return ToSerializerMap(map);
    }

    /// <summary>Projects an ordinal-keyed string map onto the <see cref="ReadOnlyMemory{T}"/> shape Parquet.Net
    /// 6.1's untyped serializer demands. Dedup already happened on the <see cref="string"/> keys, so every key
    /// here is distinct by content and the reference-equality semantics of the memory keys cannot collide.</summary>
    private static Dictionary<ReadOnlyMemory<char>, ReadOnlyMemory<char>?> ToSerializerMap(Dictionary<string, string?> entries)
    {
        var map = new Dictionary<ReadOnlyMemory<char>, ReadOnlyMemory<char>?>();
        foreach ((string key, string? value) in entries)
        {
            map[Memory(key)] = value is null ? null : Memory(value);
        }

        return map;
    }

    private static List<ReadOnlyMemory<char>?> ToStringList(IEnumerable<string> values) =>
        values.Select(value => (ReadOnlyMemory<char>?)Memory(value)).ToList();

    private static ReadOnlyMemory<char> Memory(string value) => value.AsMemory();

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
