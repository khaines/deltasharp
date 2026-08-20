using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet;
using Parquet.Schema;
using Xunit;
using PqStructField = Parquet.Schema.StructField;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// The #676 §3.9–3.13 id-mode nested-struct CONTAINMENT oracle — the design's "CRITICAL closure" (§2.5). Under
/// column-mapping id mode a <c>struct&lt;scalars&gt;</c> read binds the container by physical name and each
/// child by <c>field_id</c> <b>within that container's own leaves</b>, then type/level-validates the
/// id-selected leaf. These tests forge a Parquet footer whose leaf <c>field_id</c> stamps point at the wrong
/// leaf (a top-level leaf, a sibling container's leaf, a same-profile leaf, a differently-typed leaf) or whose
/// container binding is malformed, and prove each fails closed with a typed <see cref="DeltaStorageException"/>
/// rather than mis-attributing a column. Same-name sibling values are drawn from DISJOINT domains (home-/work-)
/// so a positional mis-bind cannot pass on equal values.
/// </summary>
public sealed class NestedColumnMappingIdContainmentTests
{
    // ----- §3.10 / §3.11 — child id resolves to a leaf under a DIFFERENT container -----

    [Fact]
    public async Task IdMode_NestedChildId_ResolvesToLeafUnderDifferentContainer_FailsClosed()
    {
        // home/v field_id 5, work/v field_id 6 (disjoint witness domains). Request struct 'home' whose child
        // 'v' declares id 6 — work's leaf. It resolves inside the file but OUTSIDE home's own children.
        byte[] bytes = await WriteTwoStringStructsAsync(homeFieldId: 5, workFieldId: 6);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, One(Container("home", containerId: null, IdField("v", DataTypes.StringType, id: 6)))));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("outside the resolved container's own children", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdMode_ChildIdStampedOnLeafOfEqualRepDefProfile_FailsClosed()
    {
        // home/v and work/v are BOTH string, both under an optional struct → identical max rep/def profile, so
        // the structural-level guard alone CANNOT tell them apart; only containment can. Request home.v with
        // work's id 6 → fails closed on containment, not on levels.
        byte[] bytes = await WriteTwoStringStructsAsync(homeFieldId: 5, workFieldId: 6);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, One(Container("home", containerId: null, IdField("v", DataTypes.StringType, id: 6)))));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("outside the resolved container's own children", error.Message, StringComparison.Ordinal);
    }

    // ----- §3.9 — child id resolves to a TOP-LEVEL leaf -----

    [Fact]
    public async Task IdMode_NestedChildId_ResolvesToTopLevelLeaf_FailsClosed()
    {
        // Top-level scalar 'flat' (field_id 1) + struct 'home' (child leaf field_id 5). Request struct 'home'
        // whose child 'v' declares id 1 — a top-level leaf, outside home's subtree.
        byte[] bytes = await WriteFlatAndStructAsync(flatFieldId: 1, homeFieldId: 5);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, One(Container("home", containerId: null, IdField("v", DataTypes.StringType, id: 1)))));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("outside the resolved container's own children", error.Message, StringComparison.Ordinal);
    }

    // ----- §3.12 — request type disagrees with the ID-SELECTED leaf (type-validated, not the name-matched leaf) -----

    [Fact]
    public async Task IdMode_ChildIdSelectedLeafTypeDisagreesWithRequest_FailsClosed_AsSchemaMismatch()
    {
        // The id-SELECTED leaf (not a name-matched one) is passed through ExpectScalarLeaf, so a request whose
        // type disagrees with the leaf the field_id resolves to fails closed as a typed SchemaMismatch (never a
        // raw mid-decode cast fault). home/v is a string leaf (id 5); request child 'v' as LONG with id 5 → the
        // id-selected string leaf is type-validated against the requested long and rejected. (The finer
        // temporal date-vs-timestamp annotation distinction is validated by ExpectScalarLeaf's underlying
        // ValidateLeafPhysicalType; the mechanism that the ID-selected leaf is the one validated is pinned here.)
        byte[] bytes = await WriteTwoStringStructsAsync(homeFieldId: 5, workFieldId: 6);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, One(Container("home", containerId: null, IdField("v", DataTypes.LongType, id: 5)))));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("does not match the requested", error.Message, StringComparison.Ordinal);
    }

    // ----- §3.13 — container-binding negatives (the containment ROOT) -----

    [Fact]
    public async Task IdMode_ContainerPhysicalNameGroupAbsentFromFooter_FailsClosed()
    {
        byte[] bytes = await WriteTwoStringStructsAsync(homeFieldId: 5, workFieldId: 6);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, One(Container("missing", containerId: null, IdField("v", DataTypes.StringType, id: 5)))));

        Assert.Equal(StorageErrorKind.ColumnNotPresentInFile, error.Kind);
    }

    [Fact]
    public async Task IdMode_ContainerPhysicalNameResolvesToNonGroupLeaf_FailsClosed()
    {
        // 'flat' is a top-level SCALAR in the file, but requested as a struct container.
        byte[] bytes = await WriteFlatAndStructAsync(flatFieldId: 1, homeFieldId: 5);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, One(Container("flat", containerId: null, IdField("v", DataTypes.StringType, id: 1)))));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("non-struct file column", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdMode_ContainerDeclaredIdPresentOnSomeFooterLeaf_FailsClosed()
    {
        // The struct container's DECLARED id must be structural-only; here it is stamped on home/v (id 5) in
        // the footer, which is forged and fails closed.
        byte[] bytes = await WriteTwoStringStructsAsync(homeFieldId: 5, workFieldId: 6);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, One(Container("home", containerId: 5, IdField("v", DataTypes.StringType, id: 5)))));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("structural-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdMode_ContainerGroupPresentButChildIdAbsentFromSubtree_FailsClosed_NoNameFallback()
    {
        // Child 'v' declares id 999, absent from the footer — must fail closed with NO fall back to matching by
        // name (which would otherwise silently bind home/v).
        byte[] bytes = await WriteTwoStringStructsAsync(homeFieldId: 5, workFieldId: 6);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, One(Container("home", containerId: null, IdField("v", DataTypes.StringType, id: 999)))));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("no name fallback", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdMode_StructChild_PhysicalNameAbsentFromFooterButFieldIdPresentAndContained_Succeeds()
    {
        // Identity is the id-within-container lookup, NOT the name: a child whose declared physical name is
        // ABSENT from the footer still resolves by its (contained) field_id and reads the right value.
        byte[] bytes = await WriteTwoStringStructsAsync(homeFieldId: 5, workFieldId: 6);

        ColumnBatch batch = await ReadSingleAsync(
            bytes, One(Container("home", containerId: null, IdField("not_in_footer", DataTypes.StringType, id: 5))));

        var home = Assert.IsType<StructColumnVector>(batch.Column(0));
        Assert.Equal("home-1000", Utf8(home.Child(0), 0)); // home/v disjoint domain
        Assert.Equal("home-1001", Utf8(home.Child(0), 1));
    }

    // ----- §3.7 — same-typed siblings, footer reversed, binds by field_id (witness-disjoint, positive) -----

    [Fact]
    public async Task IdMode_StructSameTypedSiblings_FooterReversed_BindsByFieldId_WitnessDisjoint()
    {
        // Footer emits children in order [b(id 6), a(id 5)] with disjoint domains a→'A-*', b→'B-*'.
        // Requesting struct s<a(id 5), b(id 6)> must bind each by id (not position), so a reads 'A-*' and b
        // reads 'B-*' despite the reversed footer order.
        byte[] bytes = await WriteReversedSameTypedStructAsync(aFieldId: 5, bFieldId: 6);

        ColumnBatch batch = await ReadSingleAsync(
            bytes,
            One(Container(
                "s",
                containerId: null,
                IdField("a", DataTypes.StringType, id: 5),
                IdField("b", DataTypes.StringType, id: 6))));

        var s = Assert.IsType<StructColumnVector>(batch.Column(0));
        Assert.Equal("A-1000", Utf8(s.Child(0), 0));
        Assert.Equal("B-2000", Utf8(s.Child(1), 0));
    }

    // -------------------------------------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------------------------------------

    private static StructType One(StructField field) => new(new[] { field });

    private static string Utf8(ColumnVector vector, int index) => Encoding.UTF8.GetString(vector.GetBytes(index));

    private static StructField IdField(string name, DataType type, long id) =>
        new(name, type, nullable: true, FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(id)),
        }));

    private static StructField Container(string name, long? containerId, params StructField[] children)
    {
        FieldMetadata meta = containerId is long id
            ? FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(id)),
            })
            : FieldMetadata.Empty;
        return new StructField(name, new StructType(children), nullable: true, meta);
    }

    private static async Task<byte[]> WriteTwoStringStructsAsync(int homeFieldId, int workFieldId)
    {
        var schema = new ParquetSchema(
            new PqStructField("home", new DataField<string>("v") { FieldId = homeFieldId }),
            new PqStructField("work", new DataField<string>("v") { FieldId = workFieldId }));

        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            DataField[] leaves = schema.GetDataFields();
            await rowGroup.WriteAsync(leaves[0], new[] { "home-1000", "home-1001" }, null); // home/v disjoint
            await rowGroup.WriteAsync(leaves[1], new[] { "work-2000", "work-2001" }, null); // work/v disjoint
        }

        return stream.ToArray();
    }

    private static async Task<byte[]> WriteFlatAndStructAsync(int flatFieldId, int homeFieldId)
    {
        var schema = new ParquetSchema(
            new DataField<string>("flat") { FieldId = flatFieldId },
            new PqStructField("home", new DataField<string>("v") { FieldId = homeFieldId }));

        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            DataField[] leaves = schema.GetDataFields();
            await rowGroup.WriteAsync(leaves[0], new[] { "flat-a", "flat-b" }, null);
            await rowGroup.WriteAsync(leaves[1], new[] { "home-1000", "home-1001" }, null);
        }

        return stream.ToArray();
    }

    private static async Task<byte[]> WriteReversedSameTypedStructAsync(int aFieldId, int bFieldId)
    {
        // Footer declares b BEFORE a; ids still anchor identity.
        var schema = new ParquetSchema(
            new PqStructField(
                "s",
                new DataField<string>("b") { FieldId = bFieldId },
                new DataField<string>("a") { FieldId = aFieldId }));

        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            DataField[] leaves = schema.GetDataFields();
            await rowGroup.WriteAsync(leaves[0], new[] { "B-2000" }, null); // b
            await rowGroup.WriteAsync(leaves[1], new[] { "A-1000" }, null); // a
        }

        return stream.ToArray();
    }

    private static async Task<ColumnBatch> ReadSingleAsync(byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch b in new ParquetFileReader().ReadAsync(
            stream, requested, keepRowGroup: null, nullFillMissingColumns: false,
            allowTypeWideningPromotion: false, resolveByFieldId: true, CancellationToken.None))
        {
            Assert.Null(only);
            only = b;
        }

        return Assert.IsAssignableFrom<ColumnBatch>(only);
    }
}
