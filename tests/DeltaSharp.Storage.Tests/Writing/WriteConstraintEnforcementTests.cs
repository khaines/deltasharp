using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Tests.Delta;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Writing;

/// <summary>
/// Tests for #596: per-row constraint enforcement lives <b>inside</b> the Delta write primitive
/// (<see cref="DeltaWriteTarget.AppendAsync"/> / <see cref="DeltaWriteTarget.OverwriteAsync"/>), collected
/// from the SAME snapshot the commit bases on and delegated to the caller-supplied
/// <see cref="IWriteConstraintEnforcer"/>. This closes the read-constraints-vs-commit TOCTOU (one snapshot),
/// forbids a storage-direct bypass (a constrained write with no enforcer is refused fail-closed rather than
/// committed unvalidated), and runs post-reconcile. The actual predicate evaluation is covered end-to-end by
/// the executor's <c>DeltaConstraintEnforcementTests</c>; here the enforcer is a fake so the tests assert the
/// PRIMITIVE's wiring (which constraints it collects, from which snapshot, and its fail-closed contract).
/// </summary>
[Collection(ColumnMappingTestCollection.Name)]
public sealed class WriteConstraintEnforcementTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "delta-write-enforce-" + Guid.NewGuid().ToString("N"));

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

    private static readonly StructType IdSchema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
    });

    // A schema whose `amount` field declares a column invariant (delta.invariants metadata).
    private static StructType SchemaWithInvariant(string invariantJson) => new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField(
            "amount", IntegerType.Instance, nullable: true,
            FieldMetadata.FromEntries(new[]
            {
                new KeyValuePair<string, string>("delta.invariants", invariantJson),
            })),
    });

    private DeltaWriteTarget Target() => DeltaWriteTarget.ForLocalPath(_root);

    private static ColumnBatch IdBatch(params int[] ids)
    {
        MutableColumnVector id = ColumnVectors.Create(IntegerType.Instance, ids.Length);
        foreach (int value in ids)
        {
            id.AppendValue(value);
        }

        return new ManagedColumnBatch(IdSchema, new ColumnVector[] { id }, ids.Length);
    }

    private static ColumnBatch InvariantBatch(StructType schema, params (int Id, int? Amount)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(IntegerType.Instance, rows.Length);
        MutableColumnVector amount = ColumnVectors.Create(IntegerType.Instance, rows.Length);
        foreach ((int i, int? a) in rows)
        {
            id.AppendValue(i);
            if (a is null)
            {
                amount.AppendNull();
            }
            else
            {
                amount.AppendValue(a.Value);
            }
        }

        return new ManagedColumnBatch(schema, new ColumnVector[] { id, amount }, rows.Length);
    }

    // Creates an (empty) table at v0 whose metaData declares the given schema + configuration (e.g. a
    // delta.constraints.* CHECK), so a subsequent facade write goes through the existing-table path.
    private async Task SeedTableAsync(StructType schema, params (string Key, string Value)[] configuration)
    {
        using var backend = new LocalFileSystemBackend(_root);
        await DeltaTestHarness.WriteCommitAsync(
            backend,
            0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchemaAndConfig(schema, configuration));
    }

    private async Task<long?> LatestVersionAsync()
    {
        using var backend = new LocalFileSystemBackend(_root);
        return await new DeltaLog(backend).GetLatestCommitVersionAsync(CancellationToken.None);
    }

    // A fake enforcer that records exactly what the primitive handed it (and optionally rejects), so the
    // tests can assert the primitive collected the right constraint set from the commit's snapshot.
    private sealed class RecordingEnforcer : IWriteConstraintEnforcer
    {
        private readonly bool _reject;

        public RecordingEnforcer(bool reject = false) => _reject = reject;

        public int Calls { get; private set; }

        public StructType? Schema { get; private set; }

        public IReadOnlyList<DeltaTableConstraint>? Constraints { get; private set; }

        public IReadOnlyList<ColumnBatch>? Batches { get; private set; }

        public void Enforce(
            StructType schema,
            IReadOnlyList<DeltaTableConstraint> constraints,
            IReadOnlyList<ColumnBatch> batches)
        {
            Calls++;
            Schema = schema;
            Constraints = constraints;
            Batches = batches;
            if (_reject)
            {
                throw DeltaConstraintViolationException.ForRow(constraints[0], batchIndex: 0, rowIndex: 0);
            }
        }
    }

    [Fact]
    public async Task Append_ConstrainedTable_NoEnforcer_RefusedFailClosed_NoCommit()
    {
        await SeedTableAsync(IdSchema, ("delta.constraints.positive_id", "id > 0"));

        using DeltaWriteTarget target = Target();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => target.AppendAsync(IdSchema, Array.Empty<string>(), new[] { IdBatch(5) }));

        Assert.Contains("constraint enforcer", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, await LatestVersionAsync()); // no v1 — refused before any commit
    }

    [Fact]
    public async Task Overwrite_ConstrainedTable_NoEnforcer_RefusedFailClosed_NoCommit()
    {
        await SeedTableAsync(IdSchema, ("delta.constraints.positive_id", "id > 0"));

        using DeltaWriteTarget target = Target();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => target.OverwriteAsync(
                IdSchema, Array.Empty<string>(), new[] { IdBatch(5) }, DeltaPartitionOverwriteMode.Static));

        Assert.Equal(0L, await LatestVersionAsync());
    }

    [Fact]
    public async Task FreshCreate_WriteSchemaInvariant_NoEnforcer_RefusedFailClosed_NoTable()
    {
        // No prior table: a create whose write schema declares a column invariant must still be enforced, so
        // a storage-direct create with no enforcer is refused before v0 is written.
        StructType schema = SchemaWithInvariant("{\"expression\":{\"expression\":\"amount > 0\"}}");

        using DeltaWriteTarget target = Target();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => target.AppendAsync(schema, Array.Empty<string>(), new[] { InvariantBatch(schema, (1, 10)) }));

        Assert.Null(await LatestVersionAsync()); // no table was created
    }

    [Fact]
    public async Task Append_ConstrainedTable_WithEnforcer_InvokedWithSnapshotConstraints_ThenCommits()
    {
        // Proves the primitive collects the constraint set from the commit's OWN base snapshot and hands it to
        // the enforcer (one snapshot for enforcement + commit — no TOCTOU), then commits the satisfying write.
        await SeedTableAsync(IdSchema, ("delta.constraints.positive_id", "id > 0"));
        var enforcer = new RecordingEnforcer();
        ColumnBatch batch = IdBatch(5);

        using DeltaWriteTarget target = Target();
        DeltaWriteResult result = await target.AppendAsync(
            IdSchema, Array.Empty<string>(), new[] { batch }, enforcer: enforcer);

        Assert.Equal(1, enforcer.Calls);
        Assert.Same(IdSchema, enforcer.Schema);
        DeltaTableConstraint seen = Assert.Single(enforcer.Constraints!);
        Assert.Equal(DeltaConstraintKind.Check, seen.Kind);
        Assert.Equal("positive_id", seen.Name);
        Assert.Equal("id > 0", seen.Expression);
        Assert.Same(batch, Assert.Single(enforcer.Batches!)); // the enforcer validated the exact write batch
        Assert.Equal(1L, result.Version); // committed on top of the enforced snapshot
    }

    [Fact]
    public async Task Append_ConstrainedTable_EnforcerRejects_NoCommit()
    {
        await SeedTableAsync(IdSchema, ("delta.constraints.positive_id", "id > 0"));
        var enforcer = new RecordingEnforcer(reject: true);

        using DeltaWriteTarget target = Target();
        await Assert.ThrowsAsync<DeltaConstraintViolationException>(
            () => target.AppendAsync(IdSchema, Array.Empty<string>(), new[] { IdBatch(-1) }, enforcer: enforcer));

        Assert.Equal(1, enforcer.Calls);
        Assert.Equal(0L, await LatestVersionAsync()); // rejected before any Parquet was staged / committed
    }

    [Fact]
    public async Task Overwrite_ConstrainedTable_WithEnforcer_InvokedWithSnapshotConstraints()
    {
        // The general (non-schema-replacing) overwrite keeps the table's constraints, so the enforcer sees the
        // existing CHECK collected from the SAME snapshot the overwrite commits against.
        await SeedTableAsync(IdSchema, ("delta.constraints.positive_id", "id > 0"));
        var enforcer = new RecordingEnforcer();

        using DeltaWriteTarget target = Target();
        DeltaWriteResult result = await target.OverwriteAsync(
            IdSchema, Array.Empty<string>(), new[] { IdBatch(7) },
            DeltaPartitionOverwriteMode.Static, enforcer: enforcer);

        Assert.Equal(1, enforcer.Calls);
        DeltaTableConstraint seen = Assert.Single(enforcer.Constraints!);
        Assert.Equal(DeltaConstraintKind.Check, seen.Kind);
        Assert.Equal(1L, result.Version);
    }

    [Fact]
    public async Task Overwrite_OverwriteSchema_DropsExistingConstraints_EnforcesOnlyNewSchemaInvariants()
    {
        // overwriteSchema replaces the table schema wholesale, so the EXISTING CHECK is dropped with it — the
        // enforcer must see ONLY the incoming schema's own invariant, never the old CHECK (includeExisting=false).
        await SeedTableAsync(IdSchema, ("delta.constraints.positive_id", "id > 0"));
        StructType newSchema = SchemaWithInvariant("{\"expression\":{\"expression\":\"amount > 0\"}}");
        var enforcer = new RecordingEnforcer();

        using DeltaWriteTarget target = Target();
        await target.OverwriteAsync(
            newSchema, Array.Empty<string>(), new[] { InvariantBatch(newSchema, (1, 10)) },
            DeltaPartitionOverwriteMode.Static, overwriteSchema: true, enforcer: enforcer);

        Assert.Equal(1, enforcer.Calls);
        DeltaTableConstraint seen = Assert.Single(enforcer.Constraints!);
        Assert.Equal(DeltaConstraintKind.Invariant, seen.Kind); // the new schema's invariant only
        Assert.Equal("amount", seen.Name);
        Assert.DoesNotContain(enforcer.Constraints!, c => c.Kind == DeltaConstraintKind.Check);
    }

    [Fact]
    public async Task Append_UnconstrainedTable_NoEnforcer_Succeeds()
    {
        // An unconstrained write needs no enforcer — the fail-closed refusal applies only when the table (or
        // the write schema) actually declares a constraint.
        await SeedTableAsync(IdSchema);

        using DeltaWriteTarget target = Target();
        DeltaWriteResult result = await target.AppendAsync(IdSchema, Array.Empty<string>(), new[] { IdBatch(-9) });

        Assert.Equal(1L, result.Version); // -9 would violate `id > 0`, but no constraint is declared
    }

    [Fact]
    public async Task EmptyAppend_ConstrainedTable_NoEnforcer_IsNoOp()
    {
        // An empty write carries no rows to validate, so it is a benign no-op that needs no enforcer even on a
        // constrained table (Spark parity) — it neither throws nor commits a new version.
        await SeedTableAsync(IdSchema, ("delta.constraints.positive_id", "id > 0"));

        using DeltaWriteTarget target = Target();
        DeltaWriteResult result = await target.AppendAsync(
            IdSchema, Array.Empty<string>(), Array.Empty<ColumnBatch>());

        Assert.Equal(0L, result.Version);
        Assert.Equal(0L, await LatestVersionAsync());
    }
}
