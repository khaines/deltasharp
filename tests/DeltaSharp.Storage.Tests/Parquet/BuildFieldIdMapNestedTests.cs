using DeltaSharp.Storage;
using DeltaSharp.Storage.Parquet;
using Parquet;
using Parquet.Schema;
using Xunit;

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
        var schema = new ParquetSchema(
            new ListField("tags", new DataField<string>("element") { FieldId = 21 }),
            new MapField(
                "attributes",
                new DataField<string>("key", false) { FieldId = 22 },
                new DataField<long>("value") { FieldId = 23 }));
        byte[] bytes = await WriteListAndMapAsync(schema);

        IReadOnlyDictionary<int, string> paths = await ReadFieldIdPathsAsync(bytes);

        Assert.Equal("tags/list/element", paths[21]);
        Assert.Equal("attributes/key_value/key", paths[22]);
        Assert.Equal("attributes/key_value/value", paths[23]);
    }

    private static async Task<byte[]> WriteStructCitiesAsync(int homeCityFieldId, int workCityFieldId)
    {
        var schema = new ParquetSchema(
            new StructField("home", new DataField<string>("city") { FieldId = homeCityFieldId }),
            new StructField("work", new DataField<string>("city") { FieldId = workCityFieldId }));

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

    private static async Task<IReadOnlyDictionary<int, string>> ReadFieldIdPathsAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        IReadOnlyDictionary<int, DataField> map = ParquetFileReader.BuildFieldIdMap(
            reader.Schema, reader.Metadata!.Schema);
        return map.ToDictionary(entry => entry.Key, entry => entry.Value.Path.ToString());
    }
}
