using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// #616 — REAL end-to-end regressions proving the ALTER DROP/RENAME dependent-CHECK guard on the storage
/// ALTER door (<see cref="DeltaTableWriter.DropColumnAsync"/> / <see cref="DeltaTableWriter.RenameColumnAsync"/>)
/// works against the <b>real</b> constraint enforcer — the production <c>DeltaLocalSink</c> obtained through
/// <see cref="DeltaSinkFactory"/> as an <see cref="IWriteConstraintEnforcer"/> — not a fake. The ALTER door
/// collects the table's active named CHECKs against the POST-ALTER schema
/// (<c>CollectForWrite(..., includeSnapshotInvariants: false)</c>) and runs the enforcer's resolve pass over
/// the constraint SET with NO batches; a surviving CHECK that no longer resolves is refused fail-closed with
/// <see cref="DeltaConstraintDependentColumnException"/> (re-labelled with the ALTER operation), before any
/// metaData action is committed.
/// <para>The wiring (ALTER door → enforcer) is unit-proven with a fake enforcer in
/// <c>DeltaSharp.Storage.Tests</c> (ColumnMappingTests); THESE tests prove the enforcer's REAL parse/resolve
/// pass actually classifies a dangling CHECK end-to-end and that the guard commits when nothing dangles.</para>
/// </summary>
/// <remarks>
/// These live in DeltaSharp.Executor.Tests because the only <see cref="IWriteConstraintEnforcer"/>
/// implementation (<c>DeltaLocalSink</c>) is internal to DeltaSharp.Executor, while the ALTER door
/// (<see cref="DeltaTableWriter"/>) is internal to DeltaSharp.Storage — the two halves can only meet in one
/// assembly, so DeltaSharp.Storage grants this test assembly internals visibility.
/// </remarks>
public sealed class AlterColumnDependentCheckEndToEndTests : IDisposable
{
    // {id, score, name} — the DROP/RENAME target is `score`, referenced (or not) by a named CHECK.
    private static readonly StructType FlatSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("score", DataTypes.LongType, nullable: true),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    private readonly string _root;

    public AlterColumnDependentCheckEndToEndTests() =>
        _root = Path.Combine(Path.GetTempPath(), "deltaalter616-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // ---------------------------------------------------------------- DROP

    [Fact]
    public async Task DropColumn_CheckReferencesDroppedColumn_RealEnforcerRefusesFailClosed_NoCommit()
    {
        // A named CHECK `score_positive = "score > 0"` references the column being dropped. The real enforcer's
        // Phase-1 resolve of `score > 0` against the post-drop schema {id, name} fails (UnresolvedColumn), so
        // the ALTER door refuses fail-closed and commits nothing.
        await CreateNameModeTableAsync(FlatSchema, new object?[] { 1L, 100L, "alice" });
        AddCheckConstraintAtV1("score_positive", "score > 0");
        long before = await CurrentVersionAsync(); // v1 (v0 create + v1 CHECK)

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);
        IWriteConstraintEnforcer enforcer = RealEnforcer(FlatSchema);

        DeltaConstraintDependentColumnException ex =
            await Assert.ThrowsAsync<DeltaConstraintDependentColumnException>(
                () => writer.DropColumnAsync("score", enforcer));

        Assert.Equal("score", ex.ColumnName);
        Assert.Contains("ALTER TABLE DROP COLUMN", ex.Message, StringComparison.Ordinal);
        // The enforcer's default label ("overwriteSchema replacement") was replaced with the ALTER operation.
        Assert.DoesNotContain("overwriteSchema replacement", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await CurrentVersionAsync()); // no v2 commit — snapshot version is unchanged
    }

    [Fact]
    public async Task DropColumn_CheckReferencesSurvivingColumn_RealEnforcerResolvesClean_Commits()
    {
        // The named CHECK references `id`, which SURVIVES the drop of `score`. The real enforcer resolves
        // `id > 0` cleanly against the post-drop schema {id, name}, so the guard runs but does not block —
        // the DROP commits.
        await CreateNameModeTableAsync(FlatSchema, new object?[] { 1L, 100L, "alice" });
        AddCheckConstraintAtV1("id_positive", "id > 0");

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);
        IWriteConstraintEnforcer enforcer = RealEnforcer(FlatSchema);

        DeltaCommitResult drop = await writer.DropColumnAsync("score", enforcer);

        Assert.False(drop.Skipped);
        Assert.Equal(2L, drop.Version); // v0 create, v1 CHECK, v2 drop
    }

    [Fact]
    public async Task DropColumn_DroppedFieldInvariantOnly_NoNamedCheck_RealEnforcerCommits()
    {
        // The dropped field `score` carries its OWN column invariant (delta.invariants) and there is NO named
        // CHECK. includeSnapshotInvariants:false must EXCLUDE the vanished field's invariant, so the constraint
        // set is empty and the DROP commits. (If it were `true`, the guard would collect `score > 0` from the
        // snapshot and the real enforcer would fail to resolve it against {id, name} — so this also kills that
        // mutant.)
        await CreateNameModeTableAsync(FlatSchema, new object?[] { 1L, 100L, "alice" });
        AddFieldInvariantAtV1("score", "{\"expression\":{\"expression\":\"score > 0\"}}");
        long before = await CurrentVersionAsync(); // v1 (create + invariant metadata commit; no named CHECK)

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);
        IWriteConstraintEnforcer enforcer = RealEnforcer(FlatSchema);

        DeltaCommitResult drop = await writer.DropColumnAsync("score", enforcer);

        Assert.False(drop.Skipped);
        Assert.Equal(before + 1, drop.Version); // committed — the dropped field's own invariant is excluded
    }

    // ---------------------------------------------------------------- RENAME

    [Fact]
    public async Task RenameColumn_CheckReferencesRenamedColumn_RealEnforcerRefusesFailClosed_NoCommit()
    {
        // A named CHECK `score_positive = "score > 0"` references the column being renamed to `points`. After
        // the rename the schema is {id, points, name}; the real enforcer's Phase-1 resolve of `score > 0` fails
        // (UnresolvedColumn `score`), so the RENAME is refused fail-closed and commits nothing.
        await CreateNameModeTableAsync(FlatSchema, new object?[] { 1L, 100L, "alice" });
        AddCheckConstraintAtV1("score_positive", "score > 0");
        long before = await CurrentVersionAsync(); // v1

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);
        IWriteConstraintEnforcer enforcer = RealEnforcer(FlatSchema);

        DeltaConstraintDependentColumnException ex =
            await Assert.ThrowsAsync<DeltaConstraintDependentColumnException>(
                () => writer.RenameColumnAsync("score", "points", enforcer));

        Assert.Equal("score", ex.ColumnName);
        Assert.Contains("ALTER TABLE RENAME COLUMN", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("overwriteSchema replacement", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await CurrentVersionAsync()); // no commit — snapshot version is unchanged
    }

    // ---------------------------------------------------------------- helpers

    // Builds the production Delta sink (DeltaLocalSink) via the real factory and exposes it as the storage
    // layer's IWriteConstraintEnforcer — the SAME enforcer the write door uses (mirrors
    // DeltaLocalSinkEnforceCoreTests). The construction schema is unused by the resolve-only Enforce pass the
    // ALTER door drives (it passes the post-ALTER schema explicitly), but is supplied for realism.
    private IWriteConstraintEnforcer RealEnforcer(StructType schema)
    {
        Assert.True(DeltaSinkFactory.Instance.TryCreate(
            new SinkDescriptor("delta", SaveMode.Overwrite, _root), schema, AnsiMode.Ansi, out ILocalSink? sink));
        return (IWriteConstraintEnforcer)sink!;
    }

    // Creates a fresh column-mapping name-mode table (v0) at _root from the logical schema + a single batch.
    private async Task CreateNameModeTableAsync(StructType schema, params object?[][] rows)
    {
        List<Row> materialized = rows.Select(values => new Row(schema, values)).ToList();
        IReadOnlyList<ColumnBatch> batches = LocalRelationBatches.Build(schema, materialized);
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        await target.CreateNameMappedTableAsync(
            schema, Array.Empty<string>(), batches, RandomPhysicalNameSource.Instance);
    }

    // Seeds a named CHECK by injecting delta.constraints.<name> into the freshly-created (v0) table's
    // metaData.configuration as a v1 metadata-only commit that reuses v0's exact schemaString/protocol
    // (mirrors AddCheckConstraint in DeltaConstraintEnforcementTests / AddCheckConstraintAtV1 in
    // ColumnMappingTests).
    private void AddCheckConstraintAtV1(string name, string expression)
    {
        string logDir = Path.Combine(_root, "_delta_log");
        string metaLine = File.ReadAllLines(Path.Combine(logDir, $"{0:D20}.json"))
            .First(line => line.Contains("\"metaData\"", StringComparison.Ordinal));
        JsonNode root = JsonNode.Parse(metaLine)!;
        JsonObject metadata = root["metaData"]!.AsObject();
        if (metadata["configuration"] is not JsonObject configuration)
        {
            configuration = new JsonObject();
            metadata["configuration"] = configuration;
        }

        configuration[$"delta.constraints.{name}"] = expression;
        File.WriteAllText(Path.Combine(logDir, $"{1:D20}.json"), root.ToJsonString() + "\n");
    }

    // Seeds a column invariant by attaching delta.invariants to <paramref name="fieldName"/>'s metadata inside
    // the v0 metaData.schemaString and re-committing it as a v1 metadata-only commit. (The CREATE door refuses
    // an unenforceable invariant without an enforcer, so the invariant is injected post-create — the same
    // metadata-edit shape AddCheckConstraintAtV1 uses, but on the schema rather than the configuration.)
    private void AddFieldInvariantAtV1(string fieldName, string invariantJson)
    {
        string logDir = Path.Combine(_root, "_delta_log");
        string metaLine = File.ReadAllLines(Path.Combine(logDir, $"{0:D20}.json"))
            .First(line => line.Contains("\"metaData\"", StringComparison.Ordinal));
        JsonNode root = JsonNode.Parse(metaLine)!;
        JsonObject metadata = root["metaData"]!.AsObject();
        JsonNode schema = JsonNode.Parse(metadata["schemaString"]!.GetValue<string>())!;
        JsonObject field = schema["fields"]!.AsArray()
            .Select(node => node!.AsObject())
            .First(node => node["name"]!.GetValue<string>() == fieldName);
        field["metadata"]!.AsObject()["delta.invariants"] = invariantJson;
        metadata["schemaString"] = schema.ToJsonString();
        File.WriteAllText(Path.Combine(logDir, $"{1:D20}.json"), root.ToJsonString() + "\n");
    }

    private async Task<long> CurrentVersionAsync()
    {
        using var backend = new LocalFileSystemBackend(_root);
        return (await new DeltaLog(backend).LoadSnapshotAsync(version: null)).Version;
    }
}
