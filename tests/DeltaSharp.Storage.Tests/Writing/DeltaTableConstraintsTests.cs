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

    // Builds a StructField carrying a delta.invariants column invariant with the given persisted expression.
    private static StructField InvariantField(string name, DataType type, string sql) => new(
        name, type, nullable: true,
        FieldMetadata.FromEntries(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string>(
                "delta.invariants", "{\"expression\":{\"expression\":\"" + sql + "\"}}"),
        }));

    [Fact]
    public void CollectForWrite_NestedStructFieldInvariant_IsCollectedWithQualifiedPath()
    {
        // #595: an invariant on a struct field reached by an ALL-STRUCT path (`s.f`) is now COLLECTED (not
        // rejected) with its fully-qualified path as the name; the predicate references that path and enforces
        // via GetStructField (#580/#589). The collector keeps the raw expression — an enforceable invariant
        // references the qualified path, matching Spark's data-schema resolution.
        var inner = new StructType(new[] { InvariantField("f", IntegerType.Instance, "s.f > 0") });
        var schema = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance, nullable: false),
            new StructField("s", inner, nullable: true),
        });

        DeltaTableConstraint constraint = Assert.Single(DeltaTableConstraints.CollectForWrite(snapshot: null, schema));
        Assert.Equal(DeltaConstraintKind.Invariant, constraint.Kind);
        Assert.Equal("s.f", constraint.Name);
        Assert.Equal("s.f > 0", constraint.Expression);
    }

    [Fact]
    public void CollectForWrite_DeeplyNestedStructFieldInvariant_IsCollected()
    {
        // #595: an all-struct path of arbitrary depth (`s.a.b`) is collected with its full path.
        var b = new StructType(new[] { InvariantField("b", IntegerType.Instance, "s.a.b > 0") });
        var a = new StructType(new[] { new StructField("a", b, nullable: true) });
        var schema = new StructType(new[] { new StructField("s", a, nullable: true) });

        DeltaTableConstraint constraint = Assert.Single(DeltaTableConstraints.CollectForWrite(snapshot: null, schema));
        Assert.Equal("s.a.b", constraint.Name);
        Assert.Equal("s.a.b > 0", constraint.Expression);
    }

    [Fact]
    public void CollectForWrite_ArrayElementStructFieldInvariant_FailsClosed()
    {
        // #595/#606: an invariant reached THROUGH an array (an array-element struct field) needs per-element
        // enforcement not yet available, so it is refused fail-closed (never silently unenforced).
        var element = new StructType(new[] { InvariantField("f", IntegerType.Instance, "f > 0") });
        var schema = new StructType(new[] { new StructField("arr", new ArrayType(element), nullable: true) });

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => DeltaTableConstraints.CollectForWrite(snapshot: null, schema));
        Assert.Contains("#606", ex.Message, System.StringComparison.Ordinal);
        Assert.Contains("arr.element.f", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void CollectForWrite_MapValueStructFieldInvariant_FailsClosed()
    {
        // #595/#606: an invariant reached THROUGH a map value (a map-value struct field) is refused fail-closed.
        var valueStruct = new StructType(new[] { InvariantField("f", IntegerType.Instance, "f > 0") });
        var schema = new StructType(new[]
        {
            new StructField("m", new MapType(IntegerType.Instance, valueStruct), nullable: true),
        });

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => DeltaTableConstraints.CollectForWrite(snapshot: null, schema));
        Assert.Contains("#606", ex.Message, System.StringComparison.Ordinal);
        Assert.Contains("m.value.f", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void CollectForWrite_MixedTopLevelAndNestedInvariants_AreBothCollected()
    {
        // #595 (council: Quality): a top-level invariant AND a nested struct-field invariant on the same schema
        // are BOTH collected — the struct recursion neither short-circuits nor overwrites the top-level one.
        var inner = new StructType(new[] { InvariantField("f", IntegerType.Instance, "s.f > 0") });
        var schema = new StructType(new[]
        {
            InvariantField("id", IntegerType.Instance, "id > 0"), // top-level invariant
            new StructField("s", inner, nullable: true),          // nested invariant on s.f
        });

        System.Collections.Generic.IReadOnlyList<DeltaTableConstraint> constraints =
            DeltaTableConstraints.CollectForWrite(snapshot: null, schema);
        Assert.Equal(2, constraints.Count);
        Assert.Contains(constraints, c => c.Name == "id" && c.Expression == "id > 0");
        Assert.Contains(constraints, c => c.Name == "s.f" && c.Expression == "s.f > 0");
    }

    [Fact]
    public void CollectForWrite_ArrayInArrayStructFieldInvariant_FailsClosed()
    {
        // #595/#606 (council: Quality): the insideCollection flag must LATCH across multiple collection hops —
        // an invariant inside array<array<struct{f}>> is refused fail-closed with the full path.
        var element = new StructType(new[] { InvariantField("f", IntegerType.Instance, "f > 0") });
        var schema = new StructType(new[]
        {
            new StructField("arr", new ArrayType(new ArrayType(element)), nullable: true),
        });

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => DeltaTableConstraints.CollectForWrite(snapshot: null, schema));
        Assert.Contains("#606", ex.Message, System.StringComparison.Ordinal);
        Assert.Contains("arr.element.element.f", ex.Message, System.StringComparison.Ordinal);
    }

    private Snapshot SnapshotWithCheck(string value)
    {
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        return WriteAndLoadAsync(schema, ("delta.constraints.ck", value)).GetAwaiter().GetResult();
    }
}
