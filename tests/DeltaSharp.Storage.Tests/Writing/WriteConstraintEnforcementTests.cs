using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
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

    private async Task<Snapshot> SnapshotAsync()
    {
        using var backend = new LocalFileSystemBackend(_root);
        return await new DeltaLog(backend).LoadSnapshotAsync(version: null);
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
    public async Task Overwrite_OverwriteSchema_KeepsAndEnforcesSurvivingChecks_DropsOldSchemaInvariants()
    {
        // Delta parity (#596 fix): overwriteSchema replaces the schema but the committed metaData KEEPS the
        // table's named CHECK constraints, so they MUST still be enforced against the new rows — the enforcer
        // sees the surviving CHECK. Only the OLD schema's field invariants are dropped (replaced by the new
        // schema's), so the enforcer additionally sees the NEW schema's invariant, never an old one.
        await SeedTableAsync(IdSchema, ("delta.constraints.positive_id", "id > 0"));
        StructType newSchema = SchemaWithInvariant("{\"expression\":{\"expression\":\"amount > 0\"}}");
        var enforcer = new RecordingEnforcer();

        using DeltaWriteTarget target = Target();
        await target.OverwriteAsync(
            newSchema, Array.Empty<string>(), new[] { InvariantBatch(newSchema, (1, 10)) },
            DeltaPartitionOverwriteMode.Static, overwriteSchema: true, enforcer: enforcer);

        Assert.Equal(1, enforcer.Calls);
        Assert.Contains(enforcer.Constraints!, c => c.Kind == DeltaConstraintKind.Check && c.Name == "positive_id");
        Assert.Contains(enforcer.Constraints!, c => c.Kind == DeltaConstraintKind.Invariant && c.Name == "amount");
        Assert.Equal(2, enforcer.Constraints!.Count); // exactly: surviving CHECK + new-schema invariant

        // The committed metaData STILL declares the CHECK active (it survived the replacement, so enforcing it
        // above is what keeps the table honest — no fail-open, no dangling constraint).
        Snapshot after = await SnapshotAsync();
        Assert.True(after.Metadata.Configuration.ContainsKey("delta.constraints.positive_id"));
    }

    [Fact]
    public async Task Overwrite_OverwriteSchema_DropsOldFieldInvariant_WhenNewSchemaOmitsIt()
    {
        // A field invariant is schema metadata: overwriteSchema replacing the field (without the invariant)
        // drops it — the enforcer is never even called (no surviving CHECK, and the new schema declares none).
        StructType oldSchema = SchemaWithInvariant("{\"expression\":{\"expression\":\"amount > 0\"}}");
        await SeedTableAsync(oldSchema);
        StructType newSchema = new(new[]
        {
            new StructField("id", IntegerType.Instance, nullable: false),
            new StructField("amount", IntegerType.Instance, nullable: true), // no invariant now
        });
        var enforcer = new RecordingEnforcer();

        using DeltaWriteTarget target = Target();
        DeltaWriteResult result = await target.OverwriteAsync(
            newSchema, Array.Empty<string>(), new[] { InvariantBatch(newSchema, (1, -9)) }, // -9 would violate the OLD invariant
            DeltaPartitionOverwriteMode.Static, overwriteSchema: true, enforcer: enforcer);

        Assert.Equal(0, enforcer.Calls); // old invariant dropped, no new constraint → enforcement skipped
        Assert.Equal(1L, result.Version); // the -9 row commits (the dropped invariant no longer applies)
    }

    [Fact]
    public async Task Overwrite_Dynamic_ConstrainedTable_WithEnforcer_InvokedWithSnapshotConstraints()
    {
        // A dynamic partition overwrite keeps the schema + constraints; the enforcer must see the surviving
        // CHECK collected from the same base snapshot the dynamic overwrite commits against.
        await SeedTableAsync(IdSchema, ("delta.constraints.positive_id", "id > 0"));
        var enforcer = new RecordingEnforcer();

        using DeltaWriteTarget target = Target();
        await target.OverwriteAsync(
            IdSchema, Array.Empty<string>(), new[] { IdBatch(7) },
            DeltaPartitionOverwriteMode.Dynamic, enforcer: enforcer);

        Assert.Equal(1, enforcer.Calls);
        DeltaTableConstraint seen = Assert.Single(enforcer.Constraints!);
        Assert.Equal(DeltaConstraintKind.Check, seen.Kind);
        Assert.Equal("positive_id", seen.Name);
    }

    [Fact]
    public async Task Overwrite_DynamicWithOverwriteSchema_RejectedWithArgumentException_BeforeEnforcement()
    {
        // dynamic + overwriteSchema is an illegal combination; it must fail fast with ArgumentException — the
        // argument check is hoisted ABOVE snapshot load + enforcement, so a constrained table with NO enforcer
        // still surfaces the ArgumentException (not a fail-closed InvalidOperationException) for the real misuse.
        await SeedTableAsync(IdSchema, ("delta.constraints.positive_id", "id > 0"));

        using DeltaWriteTarget target = Target();
        await Assert.ThrowsAsync<ArgumentException>(
            () => target.OverwriteAsync(
                IdSchema, Array.Empty<string>(), new[] { IdBatch(5) },
                DeltaPartitionOverwriteMode.Dynamic, overwriteSchema: true));

        Assert.Equal(0L, await LatestVersionAsync()); // nothing committed
    }

    [Fact]
    public async Task EmptyOverwrite_ConstrainedTable_NoEnforcer_DoesNotThrow()
    {
        // An empty overwrite carries no rows to validate, so it needs no enforcer even on a constrained table
        // (a Static empty overwrite truncates; either way enforcement is skipped for 0 rows, no fail-closed).
        await SeedTableAsync(IdSchema, ("delta.constraints.positive_id", "id > 0"));

        using DeltaWriteTarget target = Target();
        DeltaWriteResult result = await target.OverwriteAsync(
            IdSchema, Array.Empty<string>(), Array.Empty<ColumnBatch>(), DeltaPartitionOverwriteMode.Static);

        Assert.True(result.Version >= 0); // did not throw for a missing enforcer
    }

    [Fact]
    public async Task Append_MergeSchema_NewColumnInvariant_SeenByEnforcer()
    {
        // A mergeSchema append that ADDS a constrained column must validate the new column's own rows: the
        // enforcer sees the invariant the incoming (evolved) write schema declares on the added column.
        await SeedTableAsync(IdSchema); // v0: {id}, unconstrained
        StructType evolved = SchemaWithInvariant("{\"expression\":{\"expression\":\"amount > 0\"}}"); // {id, amount(inv)}
        var enforcer = new RecordingEnforcer();

        using DeltaWriteTarget target = Target();
        await target.AppendAsync(
            evolved, Array.Empty<string>(), new[] { InvariantBatch(evolved, (1, 10)) },
            mergeSchema: true, enforcer: enforcer);

        Assert.Equal(1, enforcer.Calls);
        DeltaTableConstraint seen = Assert.Single(enforcer.Constraints!);
        Assert.Equal(DeltaConstraintKind.Invariant, seen.Kind);
        Assert.Equal("amount", seen.Name);
    }

    [Fact]
    public async Task Append_FreshDoor_ConcurrentlyCreatedTable_FailsClosed_NotBlindAppend()
    {
        // #596 facade wiring: the fresh-append door commits through the snapshot-RESPECTING core with an
        // explicit null base. This pins that wiring end-to-end: if a table already exists but the door's
        // existence probe reports "no table" (modelled by a backend that suppresses ListAsync — exactly the
        // stale-probe a concurrent create produces between the door's probe and its commit), the fresh write
        // must CONFLICT fail-closed, NOT silently blind-append UNENFORCED to that table (which is how a
        // concurrently-added constrained table's constraints would otherwise be bypassed). A regression that
        // reverts the door to the re-deriving CreateOrAppendAsync(writeSchema,…) overload turns this green→red.
        await SeedTableAsync(IdSchema); // a real v0 table exists on disk at _root

        using var real = new LocalFileSystemBackend(_root);
        using DeltaWriteTarget door = DeltaWriteTarget.ForBackend(
            new ListSuppressingBackend(real), TimeProvider.System, () => "part.parquet");

        // The door's GetLatestCommitVersionAsync lists _delta_log → suppressed → null → it takes the fresh
        // CREATE branch; the commit's PutIfAbsent(0.json) then hits the REAL, already-present v0 → conflict.
        await Assert.ThrowsAnyAsync<DeltaConcurrentModificationException>(
            () => door.AppendAsync(IdSchema, Array.Empty<string>(), new[] { IdBatch(5) }));

        Assert.Equal(0L, await LatestVersionAsync()); // no blind-appended v1 — the table is untouched
    }

    // A backend decorator that behaves exactly like its inner backend EXCEPT the FIRST listing reports EMPTY —
    // modelling the stale existence-probe a concurrent create produces: the door's own probe sees "no table",
    // but a later re-probe (as the re-deriving CreateOrAppendAsync overload would issue) sees the real table.
    // So this fault distinguishes the fixed door (one probe → null → fail-closed CREATE) from a regression to
    // the re-deriving overload (second probe → real table → silent blind append). The commit path never lists.
    private sealed class ListSuppressingBackend : IStorageBackend, IDisposable
    {
        private readonly IStorageBackend _inner;
        private int _listCalls;

        public ListSuppressingBackend(IStorageBackend inner) => _inner = inner;

        public StorageBackendKind Kind => _inner.Kind;

        public ValueTask<System.IO.Stream> ReadRangeAsync(string path, long offset, long length, CancellationToken cancellationToken) =>
            _inner.ReadRangeAsync(path, offset, length, cancellationToken);

        public ValueTask<System.IO.Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
            _inner.OpenReadAsync(path, cancellationToken);

        public ValueTask<System.IO.Stream> OpenWriteAsync(string path, CancellationToken cancellationToken) =>
            _inner.OpenWriteAsync(path, cancellationToken);

        public ValueTask<bool> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            _inner.PutIfAbsentAsync(path, content, cancellationToken);

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(path, cancellationToken);

        public ValueTask<StorageObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken) =>
            _inner.HeadAsync(path, cancellationToken);

        public async IAsyncEnumerable<StorageObjectInfo> ListAsync(
            string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _listCalls) == 1)
            {
                yield break; // first probe (the door's): report empty — the stale existence probe
            }

            await foreach (StorageObjectInfo info in _inner.ListAsync(prefix, cancellationToken).WithCancellation(cancellationToken))
            {
                yield return info;
            }
        }

        public void Dispose() => (_inner as IDisposable)?.Dispose();
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
