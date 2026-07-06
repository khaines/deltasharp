using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

public sealed class DeltaCheckpointReaderTests
{
    [Fact]
    public async Task Reads_AllActionKinds_WithNestedMapsAndLists()
    {
        byte[] parquet = await new CheckpointFixture()
            .Protocol(minReaderVersion: 1, minWriterVersion: 2)
            .Metadata(
                id: "table-1",
                schemaString: EmptySchema,
                partitionColumns: ["year", "month"],
                configuration: [("delta.appendOnly", "true")],
                name: "t")
            .Add(
                "part-a.parquet",
                size: 100,
                partitionValues: [("year", "2026"), ("month", null)],
                stats: """{"numRecords":10,"minValues":{"id":1},"maxValues":{"id":9},"nullCount":{"id":0}}""",
                tags: [("ENGINE", "deltasharp")])
            .Remove("part-old.parquet", deletionTimestamp: 123, size: 50)
            .Txn("app-1", version: 7, lastUpdated: 999)
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        ProtocolAction protocol = Assert.Single(actions.OfType<ProtocolAction>());
        Assert.Equal(1, protocol.MinReaderVersion);
        Assert.Equal(2, protocol.MinWriterVersion);
        Assert.Empty(protocol.ReaderFeatures);

        MetadataAction metadata = Assert.Single(actions.OfType<MetadataAction>());
        Assert.Equal("table-1", metadata.Id);
        Assert.Equal("t", metadata.Name);
        Assert.Equal(["year", "month"], metadata.PartitionColumns.ToArray());
        Assert.Equal("true", metadata.Configuration["delta.appendOnly"]);
        Assert.Equal("parquet", metadata.Format.Provider);

        AddFileAction add = Assert.Single(actions.OfType<AddFileAction>());
        Assert.Equal("part-a.parquet", add.Path);
        Assert.Equal(100, add.Size);
        Assert.Equal("2026", add.PartitionValues["year"]);
        Assert.Null(add.PartitionValues["month"]); // explicit null partition value round-trips
        Assert.Equal("deltasharp", add.Tags["ENGINE"]);
        Assert.NotNull(add.Stats);
        Assert.Equal(10, add.Stats!.NumRecords);
        Assert.Equal("1", add.Stats.MinValues["id"].Raw);

        RemoveFileAction remove = Assert.Single(actions.OfType<RemoveFileAction>());
        Assert.Equal("part-old.parquet", remove.Path);
        Assert.Equal(123, remove.DeletionTimestamp);
        Assert.Equal(50, remove.Size);

        TxnAction txn = Assert.Single(actions.OfType<TxnAction>());
        Assert.Equal("app-1", txn.AppId);
        Assert.Equal(7, txn.Version);
        Assert.Equal(999, txn.LastUpdated);
    }

    [Fact]
    public async Task Reads_EmptyPartitionValues_AsEmptyMap()
    {
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("f.parquet", size: 1) // no partition values → empty map, not null
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        AddFileAction add = Assert.Single(actions.OfType<AddFileAction>());
        Assert.Empty(add.PartitionValues);
        Assert.Empty(add.Tags);
        Assert.Null(add.Stats);
    }

    [Fact]
    public async Task Reads_ProtocolReaderFeatures_List()
    {
        byte[] parquet = await new CheckpointFixture()
            .Protocol(3, 7, readerFeatures: ["deletionVectors", "columnMapping"])
            .Metadata("t", EmptySchema)
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        ProtocolAction protocol = Assert.Single(actions.OfType<ProtocolAction>());
        Assert.Equal(["columnMapping", "deletionVectors"], protocol.ReaderFeatures.Sort(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task MultiPart_Checkpoint_ConcatenatesActions()
    {
        var fixture = new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("a.parquet", size: 1)
            .Add("b.parquet", size: 2)
            .Add("c.parquet", size: 3);

        byte[][] parts = await fixture.ToPartsAsync(parts: 2);

        var all = new List<DeltaAction>();
        foreach (byte[] part in parts)
        {
            all.AddRange(await DeltaCheckpointReader.ReadAsync(new MemoryStream(part), default));
        }

        Assert.Equal(3, all.OfType<AddFileAction>().Count());
        Assert.Single(all.OfType<ProtocolAction>());
        Assert.Single(all.OfType<MetadataAction>());
    }

    [Fact]
    public async Task Reads_LargeStableMap_WithoutCorruption()
    {
        // Exercise multi-entry maps across a real row group to shake out Dremel level bugs.
        var pv = Enumerable.Range(0, 50).Select(i => ($"k{i:D3}", (string?)$"v{i}")).ToArray();
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("f.parquet", size: 1, partitionValues: pv)
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        AddFileAction add = Assert.Single(actions.OfType<AddFileAction>());
        Assert.Equal(50, add.PartitionValues.Count);
        Assert.Equal("v7", add.PartitionValues["k007"]);
        Assert.Equal("v49", add.PartitionValues["k049"]);
    }

    [Fact]
    public async Task Corrupt_Parquet_FailsClosed()
    {
        byte[] garbage = "this is not a parquet file, just bytes"u8.ToArray();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(garbage), default));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
    }

    private const string EmptySchema = """{"type":"struct","fields":[]}""";
}
