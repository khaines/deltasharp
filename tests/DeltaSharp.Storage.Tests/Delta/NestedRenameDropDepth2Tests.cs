using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// The §3 oracle for #866 866c — metadata-only rename/drop of a <b>depth&gt;1 struct-chain</b> nested field
/// addressed by a <b>segment array</b> (never a dotted string). 866c lifts #840's single-hop <c>F4b</c>
/// descent ceiling (RD1): <c>RenameColumnAsync</c>/<c>DropColumnAsync</c> descend an arbitrary struct-of-struct
/// spine, rebuild the metadata, and produce a metadata-only commit (exactly one <c>metaData</c>, zero
/// <c>add</c>/<c>remove</c>, byte-identical data files, <c>maxColumnId</c> unchanged). RETAINED fail-closed
/// (unchanged by 866c): array/map INTERIOR rename/drop (<c>F4</c>, §9 — no logical-name hop, incl. the
/// <c>array&lt;struct&gt;</c> leaf), id-mode rename/drop (<c>RequireNameMode</c>, RD2), and an
/// ambiguous/non-existent path.
/// </summary>
/// <remarks>
/// Mirrors <see cref="NestedRenameDropTests"/> at depth&gt;1: the metadata-only round-trips author the nested
/// data files with the merged real nested writer (#834) paired with a hand-authored <c>_delta_log</c>, then
/// exercise the segment-array ALTER doors against that committed table. Every same-typed sibling draws its
/// values from a DISJOINT domain so a positional mis-bind cannot pass on equal values.
/// </remarks>
[Collection(ColumnMappingTestCollection.Name)]
public sealed class NestedRenameDropDepth2Tests : IDisposable
{
    private const string Seed = "nested-rename-drop-866c";

    private readonly string _root;

    public NestedRenameDropDepth2Tests() =>
        _root = Path.Combine(Path.GetTempPath(), "nestedrd2-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }

        GC.SuppressFinalize(this);
    }

    // { id:long, outer: struct<inner: struct<a:long, b:string>> } — a depth-2 struct chain. Disjoint domains so
    // a mis-bind is visible: id ∈ [1..], a ∈ [1000..], b = strings.
    private static StructType Depth2Schema() => new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("outer", new StructType(new[]
        {
            new StructField("inner", new StructType(new[]
            {
                new StructField("a", DataTypes.LongType, nullable: true),
                new StructField("b", DataTypes.StringType, nullable: true),
            }), nullable: true),
        }), nullable: true),
    });

    private static ManagedColumnBatch Depth2Batch(StructType schema)
    {
        var innerType = (StructType)((StructType)schema["outer"].DataType)["inner"].DataType;
        var outerType = (StructType)schema["outer"].DataType;
        ColumnVector a = Long(1000L, 1001L);
        ColumnVector b = Str("x", "y");
        var inner = new StructColumnVector(innerType, new[] { a, b }, new[] { false, false });
        var outer = new StructColumnVector(outerType, new[] { inner }, new[] { false, false });
        return new ManagedColumnBatch(schema, new ColumnVector[] { Long(1L, 2L), outer }, 2);
    }

    // ================================================================ §3.18 — centerpiece (conjunctive)

    [Fact]
    public async Task RenameDepth2StructChild_NameMode_IsMetadataOnly_NoRewrite()
    {
        StructType schema = Depth2Schema();
        await WriteNameMappedAsync(schema, Depth2Batch(schema));

        Snapshot before = await LoadSnapshotAsync();
        Dictionary<string, string> shaBefore = await Sha256OfActiveFilesAsync(before);
        StructField bBefore = DeepField(before.Schema, "outer", "inner", "b");
        string bPhysicalBefore = ColumnMapping.PhysicalName(bBefore, ColumnMappingMode.Name);
        Assert.True(ColumnMapping.TryGetId(bBefore, out long bIdBefore));
        string maxColumnIdBefore = before.Metadata.Configuration[ColumnMapping.MaxColumnIdKey];

        // Rename outer.inner.b -> outer.inner.c, addressed by a SEGMENT ARRAY (RD1 lifts #840 F4b).
        using var backend = new LocalFileSystemBackend(_root);
        DeltaCommitResult result = await new DeltaTableWriter(backend)
            .RenameColumnAsync(new[] { "outer", "inner", "b" }, "c");
        Assert.Equal(1L, result.Version);

        // (a) exactly one metaData action ∧ zero add/remove in the commit.
        Dictionary<string, int> actions = await CommitActionKindsAsync(1);
        Assert.Equal(1, actions.GetValueOrDefault("metaData"));
        Assert.Equal(0, actions.GetValueOrDefault("add"));
        Assert.Equal(0, actions.GetValueOrDefault("remove"));

        Snapshot after = await LoadSnapshotAsync();

        // (b) SHA-256 of every data-file byte identical pre/post.
        Assert.Equal(shaBefore, await Sha256OfActiveFilesAsync(after));

        // (c) each AddFile's (path, size, modificationTime, stats, partitionValues) identical pre/post.
        Assert.Equal(before.ActiveFiles.Length, after.ActiveFiles.Length);
        for (int i = 0; i < before.ActiveFiles.Length; i++)
        {
            AddFileAction x = before.ActiveFiles[i];
            AddFileAction y = after.ActiveFiles[i];
            Assert.Equal(x.Path, y.Path);
            Assert.Equal(x.Size, y.Size);
            Assert.Equal(x.ModificationTime, y.ModificationTime);
            Assert.Equal(x.Stats, y.Stats);
            Assert.Equal(x.PartitionValues, y.PartitionValues);
        }

        // (d) maxColumnId unchanged; the renamed deep child keeps id + physicalName verbatim, only Name changes.
        Assert.Equal(maxColumnIdBefore, after.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
        StructField cAfter = DeepField(after.Schema, "outer", "inner", "c");
        Assert.Equal(bPhysicalBefore, ColumnMapping.PhysicalName(cAfter, ColumnMappingMode.Name));
        Assert.True(ColumnMapping.TryGetId(cAfter, out long cId));
        Assert.Equal(bIdBefore, cId);
        var innerAfter = (StructType)((StructType)after.Schema["outer"].DataType)["inner"].DataType;
        Assert.False(innerAfter.TryGetField("b", out _));

        // (e) post-read returns the same values under the new logical name (resolves by preserved physicalName).
        ColumnBatch read = await ReadSingleBatchAsync();
        var readOuter = (StructColumnVector)read.Column(1);
        var readInner = (StructColumnVector)readOuter.Child("inner");
        Assert.Equal(1000L, readInner.Child("a").GetValue<long>(0));
        Assert.Equal(1001L, readInner.Child("a").GetValue<long>(1));
        Assert.Equal("x", Encoding.UTF8.GetString(readInner.Child("c").GetBytes(0)));
        Assert.Equal("y", Encoding.UTF8.GetString(readInner.Child("c").GetBytes(1)));
    }

    [Fact]
    public async Task DropDepth2StructChild_NameMode_IsMetadataOnly_DataRetained()
    {
        StructType schema = Depth2Schema();
        await WriteNameMappedAsync(schema, Depth2Batch(schema));

        Snapshot before = await LoadSnapshotAsync();
        Dictionary<string, string> shaBefore = await Sha256OfActiveFilesAsync(before);
        string maxColumnIdBefore = before.Metadata.Configuration[ColumnMapping.MaxColumnIdKey];

        using var backend = new LocalFileSystemBackend(_root);
        DeltaCommitResult result = await new DeltaTableWriter(backend)
            .DropColumnAsync(new[] { "outer", "inner", "b" });
        Assert.Equal(1L, result.Version);

        Dictionary<string, int> actions = await CommitActionKindsAsync(1);
        Assert.Equal(1, actions.GetValueOrDefault("metaData"));
        Assert.Equal(0, actions.GetValueOrDefault("add"));
        Assert.Equal(0, actions.GetValueOrDefault("remove"));

        Snapshot after = await LoadSnapshotAsync();

        // Logical removal only: data files byte-identical (retained), maxColumnId unchanged (id never reused).
        Assert.Equal(shaBefore, await Sha256OfActiveFilesAsync(after));
        Assert.Equal(maxColumnIdBefore, after.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
        for (int i = 0; i < before.ActiveFiles.Length; i++)
        {
            Assert.Equal(before.ActiveFiles[i].Path, after.ActiveFiles[i].Path);
            Assert.Equal(before.ActiveFiles[i].Size, after.ActiveFiles[i].Size);
        }

        // The deep child is gone from the logical schema; the surviving deep sibling `a` still reads through.
        var innerAfter = (StructType)((StructType)after.Schema["outer"].DataType)["inner"].DataType;
        Assert.False(innerAfter.TryGetField("b", out _));
        Assert.True(innerAfter.TryGetField("a", out _));

        ColumnBatch read = await ReadSingleBatchAsync();
        var readInner = (StructColumnVector)((StructColumnVector)read.Column(1)).Child("inner");
        Assert.Equal(1000L, readInner.Child("a").GetValue<long>(0));
        Assert.Equal(1001L, readInner.Child("a").GetValue<long>(1));
    }

    [Fact]
    public async Task RenameDepth2StructChild_OldFileReadsThroughByPhysicalName_ZeroRewrite()
    {
        // The #675 oracle's depth>1 extension (§3.26): after a metadata-only depth>1 rename, the OLD (v0) data
        // file stays active (no rewrite) and reads through under the NEW logical name via the preserved
        // physicalName. Renaming the whole intermediate `inner` -> `inner2` (a depth-1 struct-node rename that
        // itself descends a struct chain) must keep every deep leaf resolvable.
        StructType schema = Depth2Schema();
        await WriteNameMappedAsync(schema, Depth2Batch(schema));

        Snapshot before = await LoadSnapshotAsync();
        string dataFileBefore = before.ActiveFiles[0].Path;
        StructField innerBefore = DeepField(before.Schema, "outer", "inner");
        string innerPhysBefore = ColumnMapping.PhysicalName(innerBefore, ColumnMappingMode.Name);

        using var backend = new LocalFileSystemBackend(_root);
        await new DeltaTableWriter(backend).RenameColumnAsync(new[] { "outer", "inner" }, "inner2");

        Snapshot after = await LoadSnapshotAsync();
        Assert.Equal(dataFileBefore, after.ActiveFiles[0].Path); // no rewrite
        StructField inner2After = DeepField(after.Schema, "outer", "inner2");
        Assert.Equal(innerPhysBefore, ColumnMapping.PhysicalName(inner2After, ColumnMappingMode.Name));

        // The old file reads through under the NEW intermediate logical name; deep leaves keep their values.
        ColumnBatch read = await ReadSingleBatchAsync();
        var readInner = (StructColumnVector)((StructColumnVector)read.Column(1)).Child("inner2");
        Assert.Equal(1000L, readInner.Child("a").GetValue<long>(0));
        Assert.Equal("x", Encoding.UTF8.GetString(readInner.Child("b").GetBytes(0)));
        Assert.Equal("y", Encoding.UTF8.GetString(readInner.Child("b").GetBytes(1)));
    }

    // ================================================================ §3.19 — array/map interior fail-closed (F4)

    [Fact]
    public void Depth2_ArrayInterior_Rename_FailsClosed_Naming866_F4()
    {
        // struct<items: array<struct<a,b>>> — a path descending THROUGH the array element is unaddressable (C1:
        // an array element is not a StructField, no logical-name hop). Fail-closed naming #866 (RETAINED F4).
        var elem = new StructType(new[]
        {
            new StructField("a", DataTypes.LongType),
            new StructField("b", DataTypes.StringType),
        });
        var schema = new StructType(new[]
        {
            new StructField("outer", new StructType(new[]
            {
                new StructField("items", new ArrayType(elem)),
            })),
        });

        // Address the array-element struct leaf `a` (the documented §9 unaddressable case): the array node is an
        // intermediate → F4 fires before any name hop into the element.
        var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
            schema, new[] { "outer", "items", "a" }, DeltaTableWriter.SchemaChangeOp.Rename, "x"));
        Assert.Contains("array element / map key/value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("#866", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Depth2_MapInterior_Drop_FailsClosed_Naming866_F4()
    {
        var schema = new StructType(new[]
        {
            new StructField("outer", new StructType(new[]
            {
                new StructField("m", new MapType(DataTypes.StringType, DataTypes.LongType)),
            })),
        });

        var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
            schema, new[] { "outer", "m", "value" }, DeltaTableWriter.SchemaChangeOp.Drop, null));
        Assert.Contains("array element / map key/value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("#866", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Depth2_ArrayOfStructLeaf_FailsClosed_Unaddressable_F4()
    {
        // The documented §9 boundary: `a` in array<struct<a,b>> IS a StructField (renamable in principle) but
        // has no logical-name hop through the array element, so it is unaddressable by the segment-array
        // mechanism → fail-closed F4 (naming #866, NOT the closed #585).
        var elem = new StructType(new[]
        {
            new StructField("a", DataTypes.LongType),
            new StructField("b", DataTypes.StringType),
        });
        var schema = new StructType(new[] { new StructField("arr", new ArrayType(elem)) });

        var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
            schema, new[] { "arr", "a" }, DeltaTableWriter.SchemaChangeOp.Rename, "renamed"));
        Assert.Contains("array element / map key/value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("#866", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("#585", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================ §3.20 — id-mode fail-closed (RD2)

    [Fact]
    public async Task Depth2_IdMode_Rename_FailsClosed_RequireNameMode()
    {
        StructType schema = Depth2Schema();
        await WriteIdMappedAsync(schema, Depth2Batch(schema));
        using var backend = new LocalFileSystemBackend(_root);

        var rename = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).RenameColumnAsync(new[] { "outer", "inner", "b" }, "c"));
        Assert.Contains("name' mode", rename.Message, StringComparison.Ordinal);

        var drop = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).DropColumnAsync(new[] { "outer", "inner", "b" }));
        Assert.Contains("name' mode", drop.Message, StringComparison.Ordinal);
    }

    // ================================================================ §3.19 — ambiguous / non-existent path

    [Fact]
    public async Task Depth2_NonExistentDeepSegment_FailsClosed_F2()
    {
        StructType schema = Depth2Schema();
        await WriteNameMappedAsync(schema, Depth2Batch(schema));
        using var backend = new LocalFileSystemBackend(_root);

        // outer.inner.absent — the intermediate struct chain resolves but the deep target is missing.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).RenameColumnAsync(new[] { "outer", "inner", "absent" }, "x"));
        Assert.Contains("no such", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Depth2_ScalarIntermediateInChain_FailsClosed_F3()
    {
        // outer.inner.a is a SCALAR; descending one more (outer.inner.a.deeper) cannot address a child of a
        // scalar → F3, even though the earlier struct chain recursed fine.
        StructType schema = Depth2Schema();
        var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
            schema, new[] { "outer", "inner", "a", "deeper" }, DeltaTableWriter.SchemaChangeOp.Rename, "x"));
        Assert.Contains("cannot descend", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================ Depth bound (StackOverflow DoS guard)

    [Fact]
    public void PathDeeperThanSegmentCeiling_FailsClosed_BeforeDescent()
    {
        // A path longer than the shared MaxSegmentPathDepth ceiling (= 64) is rejected fail-closed BEFORE any
        // descent — parity with the assign/validate/read/write caps (#866 866c StackOverflow DoS guard).
        var schema = new StructType(new[] { new StructField("id", DataTypes.LongType) });
        var overDeep = Enumerable.Range(0, DeltaTableWriter.MaxSegmentPathDepth + 1)
            .Select(i => "s" + i.ToString(CultureInfo.InvariantCulture)).ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
            schema, overDeep, DeltaTableWriter.SchemaChangeOp.Drop, null));
        Assert.Contains("nests deeper than the supported limit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("64", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================ Harness helpers

    private static StructField DeepField(StructType schema, params string[] path)
    {
        StructType current = schema;
        for (int i = 0; i < path.Length - 1; i++)
        {
            Assert.True(current.TryGetField(path[i], out StructField intermediate), $"no field '{path[i]}'");
            current = Assert.IsType<StructType>(intermediate.DataType);
        }

        Assert.True(current.TryGetField(path[^1], out StructField leaf), $"no field '{path[^1]}'");
        return leaf;
    }

    private async Task<Snapshot> LoadSnapshotAsync()
    {
        using var backend = new LocalFileSystemBackend(_root);
        return await new DeltaLog(backend).LoadSnapshotAsync(version: null);
    }

    private async Task<ColumnBatch> ReadSingleBatchAsync()
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        var batches = new List<ColumnBatch>();
        foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
        {
            batches.Add(b);
        }

        return Assert.Single(batches);
    }

    private async Task<Dictionary<string, int>> CommitActionKindsAsync(long version)
    {
        string path = Path.Combine(_root, "_delta_log", $"{version:D20}.json");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (JsonNode.Parse(line) is JsonObject obj)
            {
                foreach (string key in obj.Select(kv => kv.Key))
                {
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
        }

        return counts;
    }

    private async Task<Dictionary<string, string>> Sha256OfActiveFilesAsync(Snapshot snapshot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (AddFileAction add in snapshot.ActiveFiles)
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(_root, add.Path));
            result[add.Path] = Convert.ToHexString(SHA256.HashData(bytes));
        }

        return result;
    }

    private Task WriteNameMappedAsync(StructType schema, ColumnBatch batch)
        => WriteRawNestedTableAsync(schema, batch, ColumnMappingMode.Name);

    private Task WriteIdMappedAsync(StructType schema, ColumnBatch batch)
        => WriteRawNestedTableAsync(schema, batch, ColumnMappingMode.Id);

    // Authors a single-commit depth>1 nested Delta table end-to-end: mint the mapping, write the batch to a REAL
    // physical Parquet file via the merged nested writer (recursive relabel to the physical shape), and
    // hand-author a protocol + metaData + add commit.
    private async Task WriteRawNestedTableAsync(StructType schema, ColumnBatch batch, ColumnMappingMode mode)
    {
        (StructType mapped, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(schema, new SeededPhysicalNameSource(Seed), mode);
        StructType physical = ColumnMapping.MapWriteSchemaToPhysical(schema, mapped, mode);
        byte[] parquetBytes = await ParquetTestHelpers.WriteToBytesAsync(physical, new[] { RelabelForWrite(batch, physical) });

        string schemaJson = DeltaSchemaJson.ToJson(mapped);
        string modeName = mode == ColumnMappingMode.Id ? "id" : "name";
        const string relativePath = "part-00000.parquet";

        using var backend = new LocalFileSystemBackend(_root);
        await backend.PutIfAbsentAsync(relativePath, parquetBytes, CancellationToken.None);

        string addLine =
            $"{{\"add\":{{\"path\":\"{relativePath}\",\"partitionValues\":{{}},"
            + $"\"size\":{parquetBytes.Length},\"modificationTime\":0,\"dataChange\":true}}}}";
        byte[] commit = Encoding.UTF8.GetBytes(
            ProtocolFeatureLine() + "\n"
            + MetadataLine(schemaJson, modeName, maxColumnId) + "\n"
            + addLine + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", commit, CancellationToken.None);
    }

    private static ColumnBatch RelabelForWrite(ColumnBatch batch, StructType physicalSchema)
    {
        var cols = new ColumnVector[physicalSchema.Count];
        for (int i = 0; i < physicalSchema.Count; i++)
        {
            cols[i] = RelabelColumn(batch.Column(i), physicalSchema[i].DataType);
        }

        return new ManagedColumnBatch(physicalSchema, cols, batch.RowCount);
    }

    private static ColumnVector RelabelColumn(ColumnVector column, DataType targetType) => (column, targetType) switch
    {
        (StructColumnVector s, StructType st) => s.RelabelTo(st),
        (ListColumnVector l, ArrayType at) => l.RelabelTo(at),
        (MapColumnVector m, MapType mt) => m.RelabelTo(mt),
        _ => column,
    };

    private static string ProtocolFeatureLine() =>
        """{"protocol":{"minReaderVersion":3,"minWriterVersion":7,"readerFeatures":["columnMapping"],"writerFeatures":["columnMapping"]}}""";

    private static string MetadataLine(string schemaJson, string mode, long maxColumnId)
    {
        string escapedSchema = System.Text.Json.JsonSerializer.Serialize(schemaJson);
        string config =
            "{\"delta.columnMapping.mode\":" + System.Text.Json.JsonSerializer.Serialize(mode)
            + ",\"delta.columnMapping.maxColumnId\":"
            + System.Text.Json.JsonSerializer.Serialize(maxColumnId.ToString(CultureInfo.InvariantCulture)) + "}";
        return "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
            + "\"schemaString\":" + escapedSchema + ",\"partitionColumns\":[]"
            + ",\"configuration\":" + config + "}}";
    }

    private static MutableColumnVector Long(params long?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.LongType, values.Length);
        foreach (long? value in values)
        {
            if (value is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendValue(value.Value);
            }
        }

        return v;
    }

    private static MutableColumnVector Str(params string?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, values.Length);
        foreach (string? value in values)
        {
            if (value is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendBytes(Encoding.UTF8.GetBytes(value));
            }
        }

        return v;
    }
}
