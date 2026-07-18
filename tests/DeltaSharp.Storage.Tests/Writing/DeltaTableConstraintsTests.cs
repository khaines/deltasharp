using System.Threading.Tasks;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Tests.Delta;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Writing;

/// <summary>
/// Unit tests for <see cref="DeltaTableConstraints.Collect"/> (#581): the write seam reads a snapshot's
/// active per-row constraints — named CHECK constraints from <c>metaData.configuration</c>
/// (<c>delta.constraints.*</c>) and column invariants from a field's <c>delta.invariants</c> persisted
/// expression — before the Executor resolves and enforces them.
/// </summary>
public sealed class DeltaTableConstraintsTests : System.IDisposable
{
    private readonly string _root = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "delta-constraints-collect-" + System.Guid.NewGuid().ToString("N"));
    private readonly LocalFileSystemBackend _backend;

    public DeltaTableConstraintsTests() => _backend = new LocalFileSystemBackend(_root);

    public void Dispose()
    {
        _backend.Dispose();
        try
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (System.IO.DirectoryNotFoundException)
        {
        }
    }

    private static StructType SchemaWithInvariant(string invariantJson) => new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField(
            "amount", IntegerType.Instance, nullable: true,
            FieldMetadata.FromEntries(new[] { new System.Collections.Generic.KeyValuePair<string, string>("delta.invariants", invariantJson) })),
    });

    private async Task<Snapshot> WriteAndLoadAsync(StructType schema, params (string Key, string Value)[] configuration)
    {
        await DeltaTestHarness.WriteCommitAsync(
            _backend,
            0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchemaAndConfig(schema, configuration));
        return await new DeltaLog(_backend).LoadSnapshotAsync();
    }

    [Fact]
    public async Task Collect_CheckConstraintFromConfiguration_IsReturned()
    {
        StructType schema = new(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        Snapshot snapshot = await WriteAndLoadAsync(schema, ("delta.constraints.positive_id", "id > 0"));

        DeltaTableConstraint constraint = Assert.Single(DeltaTableConstraints.Collect(snapshot));
        Assert.Equal(DeltaConstraintKind.Check, constraint.Kind);
        Assert.Equal("positive_id", constraint.Name);
        Assert.Equal("id > 0", constraint.Expression);
    }

    [Fact]
    public async Task Collect_ColumnInvariantFromFieldMetadata_IsParsedAndReturned()
    {
        Snapshot snapshot = await WriteAndLoadAsync(
            SchemaWithInvariant("{\"expression\":{\"expression\":\"amount > 0\"}}"));

        DeltaTableConstraint constraint = Assert.Single(DeltaTableConstraints.Collect(snapshot));
        Assert.Equal(DeltaConstraintKind.Invariant, constraint.Kind);
        Assert.Equal("amount", constraint.Name);
        Assert.Equal("amount > 0", constraint.Expression);
    }

    [Fact]
    public async Task Collect_BothCheckAndInvariant_AreReturned()
    {
        Snapshot snapshot = await WriteAndLoadAsync(
            SchemaWithInvariant("{\"expression\":{\"expression\":\"amount > 0\"}}"),
            ("delta.constraints.id_bound", "id < 1000"));

        System.Collections.Generic.IReadOnlyList<DeltaTableConstraint> constraints =
            DeltaTableConstraints.Collect(snapshot);
        Assert.Equal(2, constraints.Count);
        Assert.Contains(constraints, c => c.Kind == DeltaConstraintKind.Check && c.Expression == "id < 1000");
        Assert.Contains(constraints, c => c.Kind == DeltaConstraintKind.Invariant && c.Expression == "amount > 0");
    }

    [Fact]
    public async Task Collect_NoConstraints_ReturnsEmpty()
    {
        StructType schema = new(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        Snapshot snapshot = await WriteAndLoadAsync(schema);

        Assert.Empty(DeltaTableConstraints.Collect(snapshot));
    }

    [Fact]
    public async Task Collect_MalformedInvariant_FailsClosed()
    {
        Snapshot snapshot = await WriteAndLoadAsync(SchemaWithInvariant("not-a-persisted-expression"));

        Assert.Throws<DeltaProtocolException>(() => DeltaTableConstraints.Collect(snapshot));
    }

    [Fact]
    public void CollectForWrite_WriteSchemaInvariant_NoSnapshot_IsReturned()
    {
        // A fresh create (no snapshot) still enforces an invariant declared on the incoming write schema.
        DeltaTableConstraint constraint = Assert.Single(DeltaTableConstraints.CollectForWrite(
            snapshot: null, SchemaWithInvariant("{\"expression\":{\"expression\":\"amount > 0\"}}")));

        Assert.Equal(DeltaConstraintKind.Invariant, constraint.Kind);
        Assert.Equal("amount > 0", constraint.Expression);
    }

    [Fact]
    public void CollectForWrite_EmptyCheckConstraint_FailsClosed()
    {
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        // A present delta.constraints.* key with an empty predicate must fail closed (not be silently skipped).
        Assert.Throws<DeltaProtocolException>(() => DeltaTableConstraints.CollectForWrite(
            SnapshotWithCheck(""), schema));
    }

    [Fact]
    public async Task Collect_NestedFieldInvariant_FailsClosed()
    {
        // An invariant attached to a NESTED (struct) field is rejected fail-closed (#595) rather than silently
        // unenforced — a nested-field invariant per-row evaluator is not wired yet.
        var inner = new StructType(new[]
        {
            new StructField(
                "f", IntegerType.Instance, nullable: true,
                FieldMetadata.FromEntries(new[] { new System.Collections.Generic.KeyValuePair<string, string>(
                    "delta.invariants", "{\"expression\":{\"expression\":\"f > 0\"}}") })),
        });
        var schema = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance, nullable: false),
            new StructField("s", inner, nullable: true),
        });
        Snapshot snapshot = await WriteAndLoadAsync(schema);

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() => DeltaTableConstraints.Collect(snapshot));
        Assert.Contains("#595", ex.Message, System.StringComparison.Ordinal);
    }

    private Snapshot SnapshotWithCheck(string value)
    {
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        return WriteAndLoadAsync(schema, ("delta.constraints.ck", value)).GetAwaiter().GetResult();
    }
}
