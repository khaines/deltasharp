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
                modificationTime: 1717171717,
                dataChange: false,
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
        Assert.Equal(1717171717, add.ModificationTime); // decoded, not defaulted (guards the ?? 0L path)
        Assert.False(add.DataChange);                    // decoded, not defaulted (guards the ?? true path)
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
    public async Task Reads_OmittedOptionalAddFields_UseDeltaDefaults()
    {
        // A foreign checkpoint may omit optional add fields; the reader must apply Delta's defaults
        // (modificationTime → 0, dataChange → true), which guards the `?? 0L` / `?? true` fallbacks.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("f.parquet", size: 3, modificationTime: null, dataChange: null)
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        AddFileAction add = Assert.Single(actions.OfType<AddFileAction>());
        Assert.Equal(0, add.ModificationTime); // default (guards ?? 0L)
        Assert.True(add.DataChange);            // default (guards ?? true)
    }

    [Fact]
    public async Task Corrupt_Parquet_FailsClosed()
    {
        byte[] garbage = "this is not a parquet file, just bytes"u8.ToArray();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(garbage), default));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
    }

    [Fact]
    public async Task PartialMetadataRow_MissingId_FailsClosed()
    {
        // A metaData present (schemaString set) but missing its required primary key `id` is a corrupt
        // row: it must fail closed, never be silently dropped (which would reconstruct a wrong state).
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: null!, schemaString: EmptySchema)
            .ToParquetAsync();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Contains("id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialAddRow_MissingPath_FailsClosed()
    {
        // An add present (size set) but missing its required `path` must fail closed — a silent skip would
        // drop a committed data file from the reconstructed active-file set.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add(path: null!, size: 5)
            .ToParquetAsync();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Contains("path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeCeiling_RejectsAmplifiedRowCount()
    {
        // A tiny compressed chunk that declares an enormous value count would eagerly allocate huge
        // value/level arrays: its footprint must exceed the per-row-group decode ceiling (fail closed).
        long footprint = DeltaCheckpointReader.ColumnFootprintBytes(
            typeof(string), numValues: 100_000_000, compressedBytes: 4_096, uncompressedBytes: 4_096, "add/path", 0);
        Assert.True(footprint > DeltaCheckpointReader.MaxCheckpointRowGroupDecodedBytes,
            $"footprint {footprint} should exceed the {DeltaCheckpointReader.MaxCheckpointRowGroupDecodedBytes}-byte ceiling");
    }

    [Fact]
    public void DecodeCeiling_RejectsDecompressionBomb()
    {
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() =>
            DeltaCheckpointReader.ColumnFootprintBytes(
                typeof(long), numValues: 8, compressedBytes: 1_000, uncompressedBytes: 1_000 * 5_000, "add/size", 0));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Contains("ratio", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeCeiling_RejectsNegativeMetadata()
    {
        Assert.Throws<DeltaProtocolException>(() =>
            DeltaCheckpointReader.ColumnFootprintBytes(typeof(long), -1, 10, 10, "add/size", 0));
    }

    [Fact]
    public void DecodeCeiling_AllowsNormalColumn()
    {
        // A realistic column (100k rows, ~2 MB) is well under the ceiling and does not throw.
        long footprint = DeltaCheckpointReader.ColumnFootprintBytes(
            typeof(long), numValues: 100_000, compressedBytes: 500_000, uncompressedBytes: 2_000_000, "add/size", 0);
        Assert.True(footprint > 0 && footprint < DeltaCheckpointReader.MaxCheckpointRowGroupDecodedBytes);
    }

    [Fact]
    public async Task DecodeCeiling_RejectsRowGroup_ViaAbsoluteBound()
    {
        // A normal checkpoint decoded under a tiny injected per-row-group ceiling trips the ABSOLUTE
        // (summed-across-columns) throw path in EnsureDecodeCeiling — the integration guard, before decode.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2).Metadata("t", EmptySchema).Add("f.parquet", size: 1)
            .ToParquetAsync();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default, maxDecodedBytes: 10));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Contains("decode ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartByteCeiling_RejectsOversizedPart()
    {
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2).Metadata("t", EmptySchema).Add("f.parquet", size: 1)
            .ToParquetAsync();

        // With a tiny part ceiling the (valid) part is refused before decode — the fail-closed outer bound.
        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default, maxPartBytes: 16));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Contains("ceiling", ex.Message, StringComparison.Ordinal);
    }

    private const string EmptySchema = """{"type":"struct","fields":[]}""";
}
