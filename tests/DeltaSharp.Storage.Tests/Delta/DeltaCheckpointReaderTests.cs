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
            .Remove("part-old.parquet", deletionTimestamp: 123, size: 50, tags: [("ZCUBE_ID", "z1")])
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
        Assert.Equal("z1", remove.Tags["ZCUBE_ID"]);

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
    public async Task Reads_RemoveTags_FromCheckpoint()
    {
        // A remove authored by an external engine carries tags (e.g. INSERTION_TIME/ZCUBE_ID); the reader's
        // remove.tags binding must decode them from the checkpoint's remove struct (issue #491).
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Remove(
                "part-old.parquet",
                deletionTimestamp: 123,
                extendedFileMetadata: true,
                size: 50,
                tags: [("INSERTION_TIME", "1700000000000"), ("ZCUBE_ID", "abc")])
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        RemoveFileAction remove = Assert.Single(actions.OfType<RemoveFileAction>());
        Assert.Equal("1700000000000", remove.Tags["INSERTION_TIME"]);
        Assert.Equal("abc", remove.Tags["ZCUBE_ID"]);
    }

    [Fact]
    public async Task Reads_EmptyRemoveTags_AsEmptyMap()
    {
        // A remove with no tags round-trips to an empty map (not null), like the add path.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Remove("part-old.parquet", deletionTimestamp: 123, size: 50) // no tags → empty map
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        RemoveFileAction remove = Assert.Single(actions.OfType<RemoveFileAction>());
        Assert.Empty(remove.Tags);
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
    public async Task PartialMetadataRow_OnlyEmptyMapsAndNulls_FailsClosed()
    {
        // Red-team R3: a metaData struct that is PRESENT but whose scalar fields are all null and whose
        // only sub-content is empty maps/lists (format.options={}, configuration={}, partitionColumns=[])
        // must still fail closed — the struct-present signal comes from the key column's definition level,
        // not from field content, so an empty-map-only partial action is not silently skipped.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: null!, schemaString: null!, provider: null!)
            .ToParquetAsync();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Contains("id", ex.Message, StringComparison.Ordinal);
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
    public void DecodeCeiling_LargeCompressedSize_DoesNotOverflow()
    {
        // Overflow-safety of the checkpoint decompression-ratio ceiling: a chunk with a huge declared
        // COMPRESSED size and a tiny decompressed payload is NOT a ratio bomb (ratio << 1) and must be
        // accepted. The compressed×ratio product is widened to Int128 so the 64-bit multiply cannot wrap into
        // a spurious verdict — pre-fix, `Math.Max(compressedBytes, 1) * MaxDecompressionRatio` overflowed
        // (here to a negative product), flipping the comparison and wrongly rejecting this legitimate column.
        long footprint = DeltaCheckpointReader.ColumnFootprintBytes(
            typeof(long), numValues: 1, compressedBytes: 9_223_372_036_854_776L, uncompressedBytes: 1_000, "add/size", 0);
        Assert.True(footprint > 0 && footprint < DeltaCheckpointReader.MaxCheckpointRowGroupDecodedBytes);
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

    [Fact]
    public async Task Reads_AddDeletionVector_RoundTrips_DescriptorExactly()
    {
        // Issue #527: a checkpoint whose add carries a nested deletionVector struct must reconstruct the
        // EXACT descriptor (storageType/pathOrInlineDv/offset/sizeInBytes/cardinality) — silently dropping
        // it would resurrect deleted rows.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(3, 7, readerFeatures: ["deletionVectors"], writerFeatures: ["deletionVectors"])
            .Metadata("t", EmptySchema)
            .Add("dv-file.parquet", size: 100,
                deletionVector: CheckpointFixture.DvColumns.Uuid("0123456789abcdefghij", offset: 4, sizeInBytes: 40, cardinality: 3))
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        AddFileAction add = Assert.Single(actions.OfType<AddFileAction>());
        Assert.NotNull(add.DeletionVector);
        Assert.Equal("u", add.DeletionVector!.StorageType);
        Assert.Equal("0123456789abcdefghij", add.DeletionVector.PathOrInlineDv);
        Assert.Equal(4, add.DeletionVector.Offset);
        Assert.Equal(40, add.DeletionVector.SizeInBytes);
        Assert.Equal(3, add.DeletionVector.Cardinality);
    }

    [Fact]
    public async Task Reads_InlineAddDeletionVector_WithoutOffset_RoundTrips()
    {
        // An inline ('i') DV carries no offset; the reader must round-trip a null Offset (not fabricate one).
        byte[] parquet = await new CheckpointFixture()
            .Protocol(3, 7, readerFeatures: ["deletionVectors"])
            .Metadata("t", EmptySchema)
            .Add("inline-dv.parquet", size: 10,
                deletionVector: new CheckpointFixture.DvColumns("i", "wxyz0123456789ABCDEF", Offset: null, SizeInBytes: 8, Cardinality: 2))
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        AddFileAction add = Assert.Single(actions.OfType<AddFileAction>());
        Assert.NotNull(add.DeletionVector);
        Assert.Equal("i", add.DeletionVector!.StorageType);
        Assert.Equal("wxyz0123456789ABCDEF", add.DeletionVector.PathOrInlineDv);
        Assert.Null(add.DeletionVector.Offset);
        Assert.Equal(8, add.DeletionVector.SizeInBytes);
        Assert.Equal(2, add.DeletionVector.Cardinality);
        // UniqueId of an inline (offset-less) DV is storageType+pathOrInlineDv — pin the derived identity too.
        Assert.Equal("iwxyz0123456789ABCDEF", add.DeletionVector.UniqueId);
    }

    [Fact]
    public async Task Reads_RemoveDeletionVector_RoundTrips()
    {
        // A tombstone's DV is part of the removed logical file's identity; it must round-trip too.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(3, 7, readerFeatures: ["deletionVectors"])
            .Metadata("t", EmptySchema)
            .Add("live.parquet", size: 1)
            .Remove("dead.parquet", deletionTimestamp: 9, size: 5,
                deletionVector: CheckpointFixture.DvColumns.Uuid("removedvremovedvremov", offset: 1, sizeInBytes: 20, cardinality: 7))
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        RemoveFileAction remove = Assert.Single(actions.OfType<RemoveFileAction>());
        Assert.NotNull(remove.DeletionVector);
        Assert.Equal("u", remove.DeletionVector!.StorageType);
        Assert.Equal("removedvremovedvremov", remove.DeletionVector.PathOrInlineDv);
        Assert.Equal(1, remove.DeletionVector.Offset);
        Assert.Equal(20, remove.DeletionVector.SizeInBytes);
        Assert.Equal(7, remove.DeletionVector.Cardinality);
        // The removed logical file's identity (path + DV uniqueId) must round-trip exactly.
        Assert.Equal("uremovedvremovedvremov@1", remove.DeletionVector.UniqueId);

        // The DV struct is present in the schema but the plain add carries no DV → null (no regression).
        AddFileAction add = Assert.Single(actions.OfType<AddFileAction>());
        Assert.Null(add.DeletionVector);
    }

    [Fact]
    public async Task Reads_AddWithoutDeletionVector_WhenSchemaHasDvColumn_NullDescriptor()
    {
        // With the deletionVector struct present in the checkpoint schema (because a sibling row carries a
        // DV), a DV-free add must still read back a NULL descriptor — no phantom DV, no regression.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(3, 7, readerFeatures: ["deletionVectors"])
            .Metadata("t", EmptySchema)
            .Add("plain.parquet", size: 1)
            .Add("dv.parquet", size: 2,
                deletionVector: CheckpointFixture.DvColumns.Uuid("aaaaaaaaaaaaaaaaaaaa", offset: 0, sizeInBytes: 4, cardinality: 1))
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        AddFileAction plain = Assert.Single(actions.OfType<AddFileAction>(), a => a.Path == "plain.parquet");
        Assert.Null(plain.DeletionVector);
        AddFileAction dv = Assert.Single(actions.OfType<AddFileAction>(), a => a.Path == "dv.parquet");
        Assert.NotNull(dv.DeletionVector);
    }

    [Fact]
    public async Task MalformedDeletionVector_MissingSizeInBytes_FailsClosed()
    {
        // A DV struct present (storageType set) but missing a required sub-column (sizeInBytes) is a corrupt
        // DV: it MUST fail closed (→ JSON replay), never yield a partial descriptor or drop the DV.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(3, 7, readerFeatures: ["deletionVectors"])
            .Metadata("t", EmptySchema)
            .Add("dv.parquet", size: 1,
                deletionVector: new CheckpointFixture.DvColumns("u", "0123456789abcdefghij", Offset: 4, SizeInBytes: null, Cardinality: 3))
            .ToParquetAsync();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Contains("deletionVector", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sizeInBytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedDeletionVector_StructPresentButStorageTypeNull_FailsClosed_NotSilentlyDropped()
    {
        // The subtle DV-drop hazard: a DV struct is PRESENT (its other sub-columns are set) but its required
        // storageType leaf is null. Presence MUST be detected from ANY DV leaf (not storageType alone), so
        // this fails closed (→ JSON replay) rather than being mistaken for "no DV" and SILENTLY DROPPED —
        // which would resurrect the rows the DV deletes (the cardinal DV safety violation). This mirrors the
        // JSON parser, where the presence of the deletionVector object (not its storageType) triggers
        // required-field validation.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(3, 7, readerFeatures: ["deletionVectors"])
            .Metadata("t", EmptySchema)
            .Add("dv.parquet", size: 1,
                deletionVector: new CheckpointFixture.DvColumns(
                    StorageType: null, PathOrInlineDv: "0123456789abcdefghij", Offset: 4, SizeInBytes: 34, Cardinality: 3))
            .ToParquetAsync();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Contains("storageType", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedDeletionVector_BadStorageType_FailsClosed()
    {
        byte[] parquet = await new CheckpointFixture()
            .Protocol(3, 7, readerFeatures: ["deletionVectors"])
            .Metadata("t", EmptySchema)
            .Add("dv.parquet", size: 1,
                deletionVector: new CheckpointFixture.DvColumns("x", "0123456789abcdefghij", Offset: 4, SizeInBytes: 40, Cardinality: 3))
            .ToParquetAsync();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Contains("storageType", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterleavedDeletionVectors_AcrossRowGroupBoundaries_LandOnCorrectAdd()
    {
        // DURABLE cross-row-group DV-alignment regression (issue #527): a checkpoint whose adds span MORE
        // than one Parquet row group, with DV / no-DV adds INTERLEAVED (every 3rd add carries a DV with a
        // DISTINCT path + cardinality). The per-row-group Dremel decode must land each DV on the EXACT add
        // that carried it (1:1) with NO off-by-one across the row-group boundary — the highest-value DV
        // correctness property, pinned permanently.
        const int addCount = 20;
        var fixture = new CheckpointFixture()
            .Protocol(3, 7, readerFeatures: ["deletionVectors"], writerFeatures: ["deletionVectors"])
            .Metadata("t", EmptySchema);
        for (int i = 0; i < addCount; i++)
        {
            CheckpointFixture.DvColumns? dv = i % 3 == 0
                ? CheckpointFixture.DvColumns.Uuid(DvPath(i), offset: i, sizeInBytes: 8 + i, cardinality: 100 + i)
                : null;
            fixture.Add(FileName(i), size: 1, modificationTime: i, deletionVector: dv);
        }

        // rowGroupSize small enough that the 22 rows (protocol + metadata + 20 adds) span several row groups,
        // so the DV column is decoded per-group and the alignment is exercised across every boundary.
        byte[] parquet = await fixture.ToParquetAsync(rowGroupSize: 4);

        // Pin the "spans >1 row group" precondition — otherwise the test could silently degrade to a single
        // group and stop covering the boundary.
        await using (var reader = await global::Parquet.ParquetReader.CreateAsync(new MemoryStream(parquet)))
        {
            Assert.True(reader.RowGroupCount > 1, "fixture must produce a multi-row-group checkpoint part.");
        }
        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);
        List<AddFileAction> adds = actions.OfType<AddFileAction>().ToList();
        Assert.Equal(addCount, adds.Count);

        for (int i = 0; i < addCount; i++)
        {
            AddFileAction add = Assert.Single(adds, a => a.Path == FileName(i));
            if (i % 3 == 0)
            {
                Assert.NotNull(add.DeletionVector);
                Assert.Equal("u", add.DeletionVector!.StorageType);
                Assert.Equal(DvPath(i), add.DeletionVector.PathOrInlineDv); // the DV's OWN distinct path
                Assert.Equal(i, add.DeletionVector.Offset);
                Assert.Equal(8 + i, add.DeletionVector.SizeInBytes);
                Assert.Equal(100 + i, add.DeletionVector.Cardinality); // distinct cardinality → no cross-add smear
            }
            else
            {
                Assert.Null(add.DeletionVector); // a no-DV add between DV adds must stay null (no bleed)
            }
        }

        static string FileName(int i) => "f" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ".parquet";
        // A 20-char Z85-safe relative pathOrInlineDv unique per add, so a misaligned DV would surface as a
        // path mismatch, not just a cardinality one.
        static string DvPath(int i) => "dv" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + "0123456789abcd";
    }

    [Fact]
    public async Task Reads_AddDeletionVector_WithRequiredLeaves_RoundTrips()
    {
        // Real Spark marks storageType/pathOrInlineDv/sizeInBytes/cardinality REQUIRED within the OPTIONAL
        // deletionVector struct (leaf MaxDefinitionLevel=2), whereas the fixture defaults to all-optional
        // leaves (MaxDefinitionLevel=3). The reader is parametric on per-field max-def, so the required-leaf
        // shape Spark actually writes must round-trip identically (issue #527 parity hardening).
        byte[] parquet = await new CheckpointFixture()
            .WithRequiredDvLeaves()
            .Protocol(3, 7, readerFeatures: ["deletionVectors"], writerFeatures: ["deletionVectors"])
            .Metadata("t", EmptySchema)
            .Add("plain.parquet", size: 1) // a DV-free add coexists (null struct under required leaves)
            .Add("dv-required.parquet", size: 100,
                deletionVector: CheckpointFixture.DvColumns.Uuid("0123456789abcdefghij", offset: 4, sizeInBytes: 40, cardinality: 3))
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        AddFileAction dv = Assert.Single(actions.OfType<AddFileAction>(), a => a.Path == "dv-required.parquet");
        Assert.NotNull(dv.DeletionVector);
        Assert.Equal("u", dv.DeletionVector!.StorageType);
        Assert.Equal("0123456789abcdefghij", dv.DeletionVector.PathOrInlineDv);
        Assert.Equal(4, dv.DeletionVector.Offset);
        Assert.Equal(40, dv.DeletionVector.SizeInBytes);
        Assert.Equal(3, dv.DeletionVector.Cardinality);
        Assert.Equal("u0123456789abcdefghij@4", dv.DeletionVector.UniqueId);

        // The DV-free sibling still reads back a null descriptor under the depth-2 (required-leaf) shape.
        AddFileAction plain = Assert.Single(actions.OfType<AddFileAction>(), a => a.Path == "plain.parquet");
        Assert.Null(plain.DeletionVector);
    }

    private const string EmptySchema = """{"type":"struct","fields":[]}""";
}
