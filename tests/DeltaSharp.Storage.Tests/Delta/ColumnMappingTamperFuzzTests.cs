using System.Globalization;
using System.Text.Json.Nodes;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Fail-closed tamper fuzz for the column-mapping read door on a LEGALLY-created (name-mode AND id-mode) table
/// whose committed <c>_delta_log/&lt;n&gt;.json</c> <c>metaData.schemaString</c> is rewritten to inject a
/// specific column-mapping defect. Unlike the raw-write column-mapping safety tests
/// (<c>ColumnMappingTests</c>), these start from a REAL DeltaSharp-written mapped table (real physical
/// <c>col-&lt;uuid&gt;</c> names, a real Parquet data file with stamped field ids) and corrupt ONLY the
/// committed schema metadata — proving the read door re-validates the on-disk metadata at load/read and never
/// trusts a poisoned <c>metaData</c> it (or a foreign engine) may have committed.
/// </summary>
/// <remarks>
/// <para>Each defect asserts (1) the specific fail-closed exception TYPE the read facade surfaces, and (2)
/// <b>#653 message hygiene</b> — the surfaced message never echoes the on-disk file path (the tamper file or
/// the table root). For the defects whose diagnostic is bounded (a duplicate/missing <c>id</c>, an
/// out-of-range field id), it additionally asserts the message never leaks a physical <c>col-&lt;uuid&gt;</c>
/// token. The duplicate-<b>physical-name</b> defect DELIBERATELY echoes the offending physical name (that IS
/// the schema-consistency diagnostic, not a row/path leak), so it asserts only the no-path invariant.</para>
/// <para>Exception shapes are taken from the production validators:
/// <see cref="ColumnMapping.ValidateColumnMappingSchema"/> throws <c>DeltaProtocolException</c> (surfaced as
/// <see cref="DeltaReadException"/> by <see cref="DeltaReadSource"/>) for duplicate/missing mapping metadata at
/// snapshot LOAD; an id-mode field id outside the Parquet <c>int32</c> field-id domain passes load but fails
/// closed as <c>DeltaStorageException(SchemaMismatch)</c> → <see cref="DeltaReadException"/> at batch READ.</para>
/// </remarks>
[Collection(ColumnMappingTestCollection.Name)]
public sealed class ColumnMappingTamperFuzzTests : IDisposable
{
    private const string IdKey = "delta.columnMapping.id";
    private const string PhysicalNameKey = "delta.columnMapping.physicalName";
    private const string MaxColumnIdKey = "delta.columnMapping.maxColumnId";
    private const long OverflowFieldId = 4294967297L; // 2^32 + 1 — outside the Parquet int32 field-id domain

    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task NameMode_DuplicatePhysicalName_FailsClosedAtLoad_NoPathLeak()
    {
        string root = await CreateMappedTableAsync(ColumnMappingMode.Name);

        // Relabel the SECOND column's physical name to collide with the FIRST — two logical columns would
        // resolve to one Parquet column (silent mis-read) if not rejected.
        TamperSchema(root, schema =>
        {
            JsonArray fields = schema["fields"]!.AsArray();
            fields[1]!["metadata"]![PhysicalNameKey] = fields[0]!["metadata"]![PhysicalNameKey]!.GetValue<string>();
        });

        DeltaReadException ex = await AssertLoadFailsClosedAsync(root);
        Assert.Contains("assigned to more than one column", ex.Message, StringComparison.Ordinal);
        AssertNoPathLeak(ex, root);
        // NOTE: this defect's message intentionally echoes the offending physical name (the schema-consistency
        // diagnostic), so the no-col-uuid hygiene assertion does NOT apply here — only the no-path invariant.
    }

    [Fact]
    public async Task NameMode_DuplicateColumnMappingId_FailsClosedAtLoad_NoLeak()
    {
        string root = await CreateMappedTableAsync(ColumnMappingMode.Name);

        TamperSchema(root, schema =>
        {
            JsonArray fields = schema["fields"]!.AsArray();
            fields[1]!["metadata"]![IdKey] = fields[0]!["metadata"]![IdKey]!.GetValue<long>();
        });

        DeltaReadException ex = await AssertLoadFailsClosedAsync(root);
        Assert.Contains("is assigned to more than one column", ex.Message, StringComparison.Ordinal);
        AssertNoLeak(ex, root);
    }

    [Fact]
    public async Task NameMode_MissingColumnMappingId_FailsClosedAtLoad_NoLeak()
    {
        string root = await CreateMappedTableAsync(ColumnMappingMode.Name);

        TamperSchema(root, schema =>
        {
            JsonObject metadata = schema["fields"]!.AsArray()[1]!["metadata"]!.AsObject();
            metadata.Remove(IdKey);
        });

        DeltaReadException ex = await AssertLoadFailsClosedAsync(root);
        Assert.Contains("has no '" + IdKey + "'", ex.Message, StringComparison.Ordinal);
        AssertNoLeak(ex, root);
    }

    [Fact]
    public async Task IdMode_FieldIdOutOfInt32Range_FailsClosedAtRead_NoLeak()
    {
        string root = await CreateMappedTableAsync(ColumnMappingMode.Id);

        // Relabel the second column's id to a value outside the Parquet int32 field-id domain, and bump
        // maxColumnId so the load-time bound check passes — the read then resolves by field_id and must fail
        // closed at ParquetTypeMapping.CreateField (not silently truncate the id onto another column).
        TamperSchema(root, schema =>
        {
            schema["fields"]!.AsArray()[1]!["metadata"]![IdKey] = OverflowFieldId;
        });
        TamperMaxColumnId(root, OverflowFieldId);

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(root);
        // Load succeeds (the id-range upper bound is a deliberate read-layer concern) — the failure is at read.
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(() => source.ReadBatchesAsync(info.Version));
        Assert.Contains("could not be read", ex.Message, StringComparison.Ordinal);
        AssertNoLeak(ex, root);
    }

    // ------------------------------------------------------------------ table + tamper mechanics

    // Writes a REAL 2-column (id long non-null, score long nullable) mapped table with a data file through the
    // production create door — so the committed metaData carries genuine physical col-<uuid> names / field ids
    // and the Parquet footer stamps them. Returns the table root.
    private async Task<string> CreateMappedTableAsync(ColumnMappingMode mode)
    {
        string root = NewRoot();
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("score", DataTypes.LongType, nullable: true),
        });

        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 2);
        MutableColumnVector score = ColumnVectors.Create(DataTypes.LongType, 2);
        id.AppendValue(1L);
        id.AppendValue(2L);
        score.AppendValue(10L);
        score.AppendValue(20L);
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { id, score }, 2);

        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(root);
        var nameSource = new SeededPhysicalNameSource("cdf-tamper");
        _ = mode == ColumnMappingMode.Id
            ? await target.CreateIdMappedTableAsync(schema, Array.Empty<string>(), new[] { batch }, nameSource)
            : await target.CreateNameMappedTableAsync(schema, Array.Empty<string>(), new[] { batch }, nameSource);
        return root;
    }

    // Rewrites the v0 commit's metaData.schemaString in place: decodes the JSON-encoded schema, applies
    // `mutate`, and re-encodes it, preserving every other action/field in the commit file.
    private static void TamperSchema(string root, Action<JsonObject> mutate)
    {
        string path = CommitPath(root);
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            JsonNode node = JsonNode.Parse(lines[i])!;
            if (node["metaData"] is JsonObject metadata && metadata["schemaString"] is JsonValue schemaValue)
            {
                var schema = JsonNode.Parse(schemaValue.GetValue<string>())!.AsObject();
                mutate(schema);
                metadata["schemaString"] = schema.ToJsonString();
                lines[i] = node.ToJsonString();
            }
        }

        File.WriteAllLines(path, lines);
    }

    // Rewrites the v0 commit's metaData.configuration maxColumnId (so a defect that raises a field id above the
    // tracked max still passes the load-time id<=maxColumnId bound and reaches the read-time field-id guard).
    private static void TamperMaxColumnId(string root, long value)
    {
        string path = CommitPath(root);
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            JsonNode node = JsonNode.Parse(lines[i])!;
            if (node["metaData"] is JsonObject metadata && metadata["configuration"] is JsonObject configuration)
            {
                configuration[MaxColumnIdKey] = value.ToString(CultureInfo.InvariantCulture);
                lines[i] = node.ToJsonString();
            }
        }

        File.WriteAllLines(path, lines);
    }

    private static string CommitPath(string root) =>
        Path.Combine(root, "_delta_log", "00000000000000000000.json");

    private static async Task<DeltaReadException> AssertLoadFailsClosedAsync(string root)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(root);
        return await Assert.ThrowsAsync<DeltaReadException>(() => source.LoadSnapshotAsync(null, null));
    }

    // #653: the surfaced message must never echo an on-disk path (the tamper file or the table root).
    private static void AssertNoPathLeak(Exception ex, string root)
    {
        Assert.DoesNotContain(root, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(CommitPath(root), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("_delta_log", ex.Message, StringComparison.Ordinal);
    }

    // #653: the fully-bounded diagnostics additionally never leak a physical col-<uuid> token.
    private static void AssertNoLeak(Exception ex, string root)
    {
        AssertNoPathLeak(ex, root);
        Assert.DoesNotContain("col-", ex.Message, StringComparison.Ordinal);
    }

    private string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "ds-cdf-tamper-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }
}
