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
    public void CollectForWrite_ArrayElementStructFieldInvariant_IsIgnored_DeltaParity()
    {
        // #606 (Delta parity): an invariant reached THROUGH an array (an array-element struct field) is IGNORED
        // — Delta's Invariants.getFromSchema uses filterRecursively(checkComplexTypes=false), which never
        // descends into an array element, so the invariant is never collected/enforced (no throw, no constraint).
        var element = new StructType(new[] { InvariantField("f", IntegerType.Instance, "f > 0") });
        var schema = new StructType(new[] { new StructField("arr", new ArrayType(element), nullable: true) });

        Assert.Empty(DeltaTableConstraints.CollectForWrite(snapshot: null, schema));
    }

    [Fact]
    public void CollectForWrite_MapValueStructFieldInvariant_IsIgnored_DeltaParity()
    {
        // #606 (Delta parity): an invariant reached THROUGH a map value (or key) is IGNORED — checkComplexTypes
        // = false does not descend into a map key/value.
        var valueStruct = new StructType(new[] { InvariantField("f", IntegerType.Instance, "f > 0") });
        var schema = new StructType(new[]
        {
            new StructField("m", new MapType(IntegerType.Instance, valueStruct), nullable: true),
        });

        Assert.Empty(DeltaTableConstraints.CollectForWrite(snapshot: null, schema));
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
    public void CollectForWrite_StructPathCollected_ButArrayPathIgnored_InSameSchema()
    {
        // #606: within one schema, an all-struct-path invariant (s.f) is collected while a sibling array-element
        // invariant (arr.element.g) is ignored — Delta descends structs only.
        var inner = new StructType(new[] { InvariantField("f", IntegerType.Instance, "s.f > 0") });
        var element = new StructType(new[] { InvariantField("g", IntegerType.Instance, "g > 0") });
        var schema = new StructType(new[]
        {
            new StructField("s", inner, nullable: true),
            new StructField("arr", new ArrayType(element), nullable: true),
        });

        DeltaTableConstraint only = Assert.Single(DeltaTableConstraints.CollectForWrite(snapshot: null, schema));
        Assert.Equal("s.f", only.Name);
    }

    [Fact]
    public void CollectForWrite_ArrayInArrayStructFieldInvariant_IsIgnored_DeltaParity()
    {
        // #606 (Delta parity): an invariant inside array<array<struct{f}>> is IGNORED across every collection
        // hop — checkComplexTypes=false stops at the first collection and never descends.
        var element = new StructType(new[] { InvariantField("f", IntegerType.Instance, "f > 0") });
        var schema = new StructType(new[]
        {
            new StructField("arr", new ArrayType(new ArrayType(element)), nullable: true),
        });

        Assert.Empty(DeltaTableConstraints.CollectForWrite(snapshot: null, schema));
    }

    [Fact]
    public void CollectForWrite_TopLevelArrayFieldOwnInvariant_IsCollected()
    {
        // #606 boundary: an invariant declared DIRECTLY on a top-level array (or map) field is on an
        // all-struct path from the root, so it is still collected (Delta collects it too — filterRecursively
        // matches the field before the checkComplexTypes gate governs DESCENT into its elements).
        var schema = new StructType(new[] { InvariantField("arr", new ArrayType(IntegerType.Instance), "size(arr) > 0") });

        DeltaTableConstraint only = Assert.Single(DeltaTableConstraints.CollectForWrite(snapshot: null, schema));
        Assert.Equal("arr", only.Name);
        Assert.Equal("size(arr) > 0", only.Expression);
    }

    [Fact]
    public void CollectForWrite_MapKeyStructFieldInvariant_IsIgnored_DeltaParity()
    {
        // #606 (Delta parity): an invariant reached through a map KEY (a key-struct field) is ignored, the same
        // as a map value — checkComplexTypes=false does not descend into either.
        var keyStruct = new StructType(new[] { InvariantField("f", IntegerType.Instance, "f > 0") });
        var schema = new StructType(new[]
        {
            new StructField("m", new MapType(keyStruct, IntegerType.Instance), nullable: true),
        });

        Assert.Empty(DeltaTableConstraints.CollectForWrite(snapshot: null, schema));
    }

    [Fact]
    public void CollectForWrite_StructInStructUnderArray_IsIgnored_DeltaParity()
    {
        // #606: the ENTIRE subtree under a collection is skipped — a struct nested inside a struct inside an
        // array (arr.element.s.f) is ignored (descent stops at the array, never re-entering struct recursion).
        var innerStruct = new StructType(new[] { InvariantField("f", IntegerType.Instance, "f > 0") });
        var element = new StructType(new[] { new StructField("s", innerStruct, nullable: true) });
        var schema = new StructType(new[] { new StructField("arr", new ArrayType(element), nullable: true) });

        Assert.Empty(DeltaTableConstraints.CollectForWrite(snapshot: null, schema));
    }

    private Snapshot SnapshotWithCheck(string value)
    {
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        return WriteAndLoadAsync(schema, ("delta.constraints.ck", value)).GetAwaiter().GetResult();
    }
}
