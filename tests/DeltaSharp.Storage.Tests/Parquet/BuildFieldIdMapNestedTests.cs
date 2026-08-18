using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet;
using Parquet.Schema;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

public sealed class BuildFieldIdMapNestedTests
{
    [Fact]
    public async Task StructSiblingsWithSameLeafName_MapByPhysicalPath()
    {
        byte[] bytes = await WriteStructCitiesAsync(homeCityFieldId: 5, workCityFieldId: 6);

        IReadOnlyDictionary<int, string> paths = await ReadFieldIdPathsAsync(bytes);

        Assert.Equal("home/city", paths[5]);
        Assert.Equal("work/city", paths[6]);
    }

    [Fact]
    public async Task ScalarRequestForNestedDuplicateLeaf_FailsClosedBeforeWrongRead()
    {
        byte[] bytes = await WriteStructCitiesAsync(homeCityFieldId: 5, workCityFieldId: 6);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, OneField(IdField("logical_city", DataTypes.StringType, nullable: true, id: 5))));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("resolves to a nested physical leaf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScalarRequestForDeepNestedStructLeaf_FailsClosed()
    {
        byte[] bytes = await WriteDeepStructAsync(fieldId: 31);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, OneField(IdField("logical_c", DataTypes.StringType, nullable: true, id: 31))));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("resolves to a nested physical leaf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScalarRequestForListElement_FailsClosed()
    {
        byte[] bytes = await WriteListAndMapAsync(ListAndMapSchema());

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, OneField(IdField("logical_tag", DataTypes.StringType, nullable: true, id: 21))));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("resolves to a nested physical leaf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScalarRequestForMapKey_FailsClosed()
    {
        byte[] bytes = await WriteListAndMapAsync(ListAndMapSchema());

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, OneField(IdField("logical_key", DataTypes.StringType, nullable: false, id: 22))));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("resolves to a nested physical leaf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScalarRequestForMapValue_FailsClosed()
    {
        byte[] bytes = await WriteListAndMapAsync(ListAndMapSchema());

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, OneField(IdField("logical_value", DataTypes.LongType, nullable: true, id: 23))));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("resolves to a nested physical leaf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoIdTopLevelNameMatchingNestedIdBearingLeaf_StillResolvesByName()
    {
        var schema = new ParquetSchema(
            new ListField("tags", new DataField<string>("element") { FieldId = 21 }),
            new DataField<string>("element"));
        byte[] bytes = await WriteListAndTopLevelElementAsync(schema);

        ColumnBatch batch = await ReadSingleAsync(
            bytes, new StructType(new[] { new StructField("element", DataTypes.StringType, nullable: false) }));

        Assert.Equal(new[] { "top-a", "top-b" }, ReadStrings(batch.SelectedColumn(0), 2));
    }

    [Fact]
    public async Task ReadDataLeafColumns_MapsOnlyTopLevelLeafNames()
    {
        var schema = new ParquetSchema(
            new DataField<string>("city") { FieldId = 1 },
            new global::Parquet.Schema.StructField("home", new DataField<string>("zip") { FieldId = 5 }));
        byte[] bytes = await WriteTopLevelAndNestedCityAsync(schema);

        using var stream = new MemoryStream(bytes, writable: false);
        IReadOnlyList<ParquetFileReader.ParquetLeafColumn> columns =
            await new ParquetFileReader().ReadDataLeafColumnsAsync(stream, CancellationToken.None);

        ParquetFileReader.ParquetLeafColumn city = Assert.Single(columns, column => column.Name == "city");
        Assert.Equal(1, city.FieldId);
        ParquetFileReader.ParquetLeafColumn zip = Assert.Single(columns, column => column.Name == "zip");
        Assert.Null(zip.FieldId);
    }

    [Fact]
    public async Task DuplicateFooterFieldId_FailsClosed()
    {
        byte[] bytes = await WriteStructCitiesAsync(homeCityFieldId: 7, workCityFieldId: 7);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadFieldIdPathsAsync(bytes));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("duplicate field_id 7", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DistinctFieldIdsResolvingToSameLeaf_FailClosed()
    {
        var schema = new ParquetSchema(new DataField<string>("leaf"));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 2 },
            new global::Parquet.Meta.SchemaElement { Name = "leaf", FieldId = 10 },
            new global::Parquet.Meta.SchemaElement { Name = "leaf", FieldId = 11 },
        };

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.BuildFieldIdMap(schema, footer));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("maps field_ids 10 and 11 to the same physical leaf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateFooterPathWithSameFieldId_FailsClosed()
    {
        var schema = new ParquetSchema(new DataField<string>("leaf"));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 2 },
            new global::Parquet.Meta.SchemaElement { Name = "leaf", FieldId = 10 },
            new global::Parquet.Meta.SchemaElement { Name = "leaf", FieldId = 10 },
        };

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.BuildFieldIdMap(schema, footer));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("duplicate footer leaf path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateDecodedLeafPath_FailsClosed()
    {
        var schema = new ParquetSchema(new DataField<string>("dup"), new DataField<string>("dup"));

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.BuildFieldIdMap(schema, footer: null));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("decodes multiple leaf columns", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdBearingFooterLeafWithoutDecodedDataField_FailsClosed()
    {
        var schema = new ParquetSchema(new DataField<string>("present"));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "missing", FieldId = 12 },
        };

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.BuildFieldIdMap(schema, footer));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("no decoded DataField at the same physical path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyIntermediateFooterName_MirrorsFieldPathAndFailsClosedOnAlias()
    {
        var schema = new ParquetSchema(new DataField<string>("c"));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 2 },
            new global::Parquet.Meta.SchemaElement { Name = "c", FieldId = 1 },
            new global::Parquet.Meta.SchemaElement { Name = string.Empty, NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "c", FieldId = 2 },
        };

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.BuildFieldIdMap(schema, footer));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("maps field_ids 1 and 2 to the same physical leaf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepFooterPath_FailsClosedBeforePathAmplification()
    {
        var footer = new List<global::Parquet.Meta.SchemaElement>
        {
            new() { Name = "root", NumChildren = 1 },
        };
        for (int i = 0; i < 101; i++)
        {
            footer.Add(new global::Parquet.Meta.SchemaElement { Name = "g" + i, NumChildren = 1 });
        }

        footer.Add(new global::Parquet.Meta.SchemaElement { Name = "leaf", FieldId = 1 });

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.BuildFieldIdMap(new ParquetSchema(new DataField<string>("leaf")), footer));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("nesting depth exceeds", error.Message, StringComparison.Ordinal);
    }

    // S-1 (#829): a footer that UNDER-declares NumChildren makes Parquet.Net drop the trailing element, but
    // the footer walk would otherwise re-parent that orphan to the root at a length-1 path — where its
    // field_id could bind to a real top-level decoded leaf (silent cross-column mis-attribution). The walk
    // must reject the orphan fail-closed. The decoded schema here matches what Parquet.Net actually decodes
    // (top-level `t`, and `g/x`); the footer carries the extra orphaned `t` with field_id 77.
    [Fact]
    public void OrphanFooterElementAfterRootChildrenExhausted_FailsClosed()
    {
        var schema = new ParquetSchema(
            new DataField<string>("t"),
            new global::Parquet.Schema.StructField("g", new DataField<string>("x") { FieldId = 9 }));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 2 },
            new global::Parquet.Meta.SchemaElement { Name = "t" },
            new global::Parquet.Meta.SchemaElement { Name = "g", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "x", FieldId = 9 },
            new global::Parquet.Meta.SchemaElement { Name = "t", FieldId = 77 },
        };

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.BuildFieldIdMap(schema, footer));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("after the root's children are exhausted", error.Message, StringComparison.Ordinal);
    }

    // S-1 (#829): a footer that OVER-declares NumChildren (a truncated schema tree) must fail closed at the
    // exact-consumption check rather than silently under-yielding leaves.
    [Fact]
    public void TruncatedFooterOverDeclaredNumChildren_FailsClosed()
    {
        var schema = new ParquetSchema(new DataField<string>("leaf"));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 3 },
            new global::Parquet.Meta.SchemaElement { Name = "leaf", FieldId = 1 },
        };

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.BuildFieldIdMap(schema, footer));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("truncated", error.Message, StringComparison.Ordinal);
    }

    // S-1 (#829): the live completeness cross-check — a footer with MORE leaves than the decoded schema (the
    // footer↔decoder disagree, e.g. a leaf Parquet.Net could not decode) fails closed one-to-one.
    [Fact]
    public void FooterLeafCountExceedsDecodedLeaves_FailsClosed()
    {
        var schema = new ParquetSchema(new DataField<string>("a"));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 2 },
            new global::Parquet.Meta.SchemaElement { Name = "a", FieldId = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "b" },
        };

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.BuildFieldIdMap(schema, footer));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("do not correspond one-to-one", error.Message, StringComparison.Ordinal);
    }

    // S-1 (#829): a well-formed footer at EXACTLY the depth cap (100 physical path components) is accepted —
    // guards the depth-cap boundary against an off-by-one that would tighten it to 99.
    [Fact]
    public void FooterPathAtDepthLimit_IsAccepted()
    {
        var footer = new List<global::Parquet.Meta.SchemaElement>
        {
            new() { Name = "root", NumChildren = 1 },
        };
        for (int i = 0; i < 99; i++)
        {
            footer.Add(new global::Parquet.Meta.SchemaElement { Name = "g" + i, NumChildren = 1 });
        }

        footer.Add(new global::Parquet.Meta.SchemaElement { Name = "leaf", FieldId = 1 });

        // Decoded schema: a single leaf whose physical path is g0/g1/.../g98/leaf (100 components).
        global::Parquet.Schema.Field leaf = new DataField<string>("leaf") { FieldId = 1 };
        for (int i = 98; i >= 0; i--)
        {
            leaf = new global::Parquet.Schema.StructField("g" + i, leaf);
        }

        var schema = new ParquetSchema(leaf);

        IReadOnlyDictionary<int, DataField> map = ParquetFileReader.BuildFieldIdMap(schema, footer);

        Assert.True(map.ContainsKey(1));
    }

    [Fact]
    public async Task FlatSchema_FieldIdMappingPreservesExistingNameParity()
    {
        var schema = new ParquetSchema(
            new DataField<long>("id") { FieldId = 1 },
            new DataField<string>("name") { FieldId = 2 });
        byte[] bytes = await WriteFlatAsync(schema);

        IReadOnlyDictionary<int, string> paths = await ReadFieldIdPathsAsync(bytes);

        Assert.Equal("id", paths[1]);
        Assert.Equal("name", paths[2]);
    }

    [Fact]
    public async Task ListElementAndMapKeyValue_MapByPhysicalWrapperPaths()
    {
        var schema = ListAndMapSchema();
        byte[] bytes = await WriteListAndMapAsync(schema);

        IReadOnlyDictionary<int, string> paths = await ReadFieldIdPathsAsync(bytes);

        Assert.Equal("tags/list/element", paths[21]);
        Assert.Equal("attributes/key_value/key", paths[22]);
        Assert.Equal("attributes/key_value/value", paths[23]);
    }

    [Fact]
    public void FieldIdsOnListWrapperGroups_AreIgnored()
    {
        var schema = new ParquetSchema(new ListField("tags", new DataField<string>("element")));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "tags", NumChildren = 1, FieldId = 90 },
            new global::Parquet.Meta.SchemaElement { Name = "list", NumChildren = 1, FieldId = 91 },
            new global::Parquet.Meta.SchemaElement { Name = "element", FieldId = 21 },
        };

        IReadOnlyDictionary<int, DataField> map = ParquetFileReader.BuildFieldIdMap(schema, footer);

        KeyValuePair<int, DataField> only = Assert.Single(map);
        Assert.Equal(21, only.Key);
        Assert.Equal("tags/list/element", only.Value.Path.ToString());
    }

    [Fact]
    public void LegacyTwoLevelBagArrayListFooter_MapsByPreservedWrapperPath()
    {
        var schema = new ParquetSchema(
            new global::Parquet.Schema.StructField(
                "tags",
                new global::Parquet.Schema.StructField("bag", new DataField<string>("array"))));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "tags", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "bag", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "array", FieldId = 41 },
        };

        IReadOnlyDictionary<int, string> paths = MapFieldIdsToPaths(schema, footer);

        Assert.Equal("tags/bag/array", paths[41]);
    }

    [Fact]
    public void LegacyMapKeyValueFooter_MapsByPreservedWrapperPath()
    {
        var schema = new ParquetSchema(
            new global::Parquet.Schema.StructField(
                "m",
                new global::Parquet.Schema.StructField(
                    "MAP_KEY_VALUE",
                    new DataField<string>("key"),
                    new DataField<long?>("value"))));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "m", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "MAP_KEY_VALUE", NumChildren = 2 },
            new global::Parquet.Meta.SchemaElement { Name = "key", FieldId = 42 },
            new global::Parquet.Meta.SchemaElement { Name = "value", FieldId = 43 },
        };

        IReadOnlyDictionary<int, string> paths = MapFieldIdsToPaths(schema, footer);

        Assert.Equal("m/MAP_KEY_VALUE/key", paths[42]);
        Assert.Equal("m/MAP_KEY_VALUE/value", paths[43]);
    }

    [Fact]
    public void SlashNamedLeafAndNestedPath_DoNotAlias()
    {
        var schema = new ParquetSchema(new DataField<string>("a/b"));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 2 },
            new global::Parquet.Meta.SchemaElement { Name = "a/b", FieldId = 61 },
            new global::Parquet.Meta.SchemaElement { Name = "a", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "b", FieldId = 62 },
        };

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.BuildFieldIdMap(schema, footer));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("no decoded DataField at the same physical path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StructInList_PathIncludesListAndElementWrappers()
    {
        var schema = new ParquetSchema(
            new ListField(
                "people",
                new global::Parquet.Schema.StructField("element", new DataField<string>("name"))));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "people", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "list", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "element", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "name", FieldId = 71 },
        };

        IReadOnlyDictionary<int, string> paths = MapFieldIdsToPaths(schema, footer);

        Assert.Equal("people/list/element/name", paths[71]);
    }

    [Fact]
    public void ListOfList_PathIncludesBothListElementWrapperPairs()
    {
        var schema = new ParquetSchema(
            new ListField("matrix", new ListField("element", new DataField<string>("element"))));
        var footer = new[]
        {
            new global::Parquet.Meta.SchemaElement { Name = "root", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "matrix", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "list", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "element", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "list", NumChildren = 1 },
            new global::Parquet.Meta.SchemaElement { Name = "element", FieldId = 72 },
        };

        IReadOnlyDictionary<int, string> paths = MapFieldIdsToPaths(schema, footer);

        Assert.Equal("matrix/list/element/list/element", paths[72]);
    }

    private static async Task<byte[]> WriteStructCitiesAsync(int homeCityFieldId, int workCityFieldId)
    {
        var schema = new ParquetSchema(
            new global::Parquet.Schema.StructField("home", new DataField<string>("city") { FieldId = homeCityFieldId }),
            new global::Parquet.Schema.StructField("work", new DataField<string>("city") { FieldId = workCityFieldId }));

        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            DataField[] leaves = schema.GetDataFields();
            await rowGroup.WriteAsync(leaves[0], new[] { "London", "Paris" }, null);
            await rowGroup.WriteAsync(leaves[1], new[] { "Dublin", "Madrid" }, null);
        }

        return stream.ToArray();
    }

    private static async Task<byte[]> WriteDeepStructAsync(int fieldId)
    {
        var schema = new ParquetSchema(
            new global::Parquet.Schema.StructField(
                "a",
                new global::Parquet.Schema.StructField("b", new DataField<string>("c") { FieldId = fieldId })));

        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            DataField[] leaves = schema.GetDataFields();
            await rowGroup.WriteAsync(leaves[0], new[] { "one", "two" }, null);
        }

        return stream.ToArray();
    }

    private static async Task<byte[]> WriteFlatAsync(ParquetSchema schema)
    {
        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            DataField[] leaves = schema.GetDataFields();
            await rowGroup.WriteAsync<long>(
                leaves[0], new ReadOnlyMemory<long?>(new long?[] { 1, 2 }), null, null, CancellationToken.None);
            await rowGroup.WriteAsync(leaves[1], new[] { "alice", "bob" }, null);
        }

        return stream.ToArray();
    }

    private static async Task<byte[]> WriteListAndMapAsync(ParquetSchema schema)
    {
        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            DataField[] leaves = schema.GetDataFields();
            await rowGroup.WriteAsync(leaves[0], new[] { "red", "blue", "green" }, new[] { 0, 1, 0 });
            await rowGroup.WriteAsync(leaves[1], new[] { "one", "two" }, new[] { 0, 0 });
            await rowGroup.WriteAsync<long>(
                leaves[2],
                new ReadOnlyMemory<long?>(new long?[] { 1, 2 }),
                new[] { 0, 0 },
                null,
                CancellationToken.None);
        }

        return stream.ToArray();
    }

    private static async Task<byte[]> WriteListAndTopLevelElementAsync(ParquetSchema schema)
    {
        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            DataField[] leaves = schema.GetDataFields();
            await rowGroup.WriteAsync(leaves[0], new[] { "nested-a", "nested-b", "nested-c" }, new[] { 0, 1, 0 });
            await rowGroup.WriteAsync(leaves[1], new[] { "top-a", "top-b" }, null);
        }

        return stream.ToArray();
    }

    private static async Task<byte[]> WriteTopLevelAndNestedCityAsync(ParquetSchema schema)
    {
        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            DataField[] leaves = schema.GetDataFields();
            await rowGroup.WriteAsync(leaves[0], new[] { "top-a", "top-b" }, null);
            await rowGroup.WriteAsync(leaves[1], new[] { "nested-a", "nested-b" }, null);
        }

        return stream.ToArray();
    }

    private static async Task<IReadOnlyDictionary<int, string>> ReadFieldIdPathsAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        IReadOnlyDictionary<int, DataField> map = ParquetFileReader.BuildFieldIdMap(
            reader.Schema, reader.Metadata!.Schema);
        return map.ToDictionary(entry => entry.Key, entry => entry.Value.Path.ToString());
    }

    private static IReadOnlyDictionary<int, string> MapFieldIdsToPaths(
        ParquetSchema schema,
        IReadOnlyList<global::Parquet.Meta.SchemaElement> footer)
    {
        IReadOnlyDictionary<int, DataField> map = ParquetFileReader.BuildFieldIdMap(schema, footer);
        return map.ToDictionary(entry => entry.Key, entry => entry.Value.Path.ToString());
    }

    private static ParquetSchema ListAndMapSchema() =>
        new(
            new ListField("tags", new DataField<string>("element") { FieldId = 21 }),
            new MapField(
                "attributes",
                new DataField<string>("key", false) { FieldId = 22 },
                new DataField<long?>("value") { FieldId = 23 }));

    private static StructField IdField(string name, DataType type, bool nullable, long id) =>
        new(name, type, nullable, FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(id)),
        }));

    private static StructType OneField(StructField field) => new(new[] { field });

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

    private static string?[] ReadStrings(ColumnVector v, int count)
    {
        var result = new string?[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = v.IsNull(i) ? null : Encoding.UTF8.GetString(v.GetBytes(i));
        }

        return result;
    }
}
