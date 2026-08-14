using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

public sealed class DeltaCheckpointReaderTests
{
    [Fact]
    public async Task ReadAsync_TransientFaultWhileReadingPart_SurfacesUnmapped_NotMalformed()
    {
        // KIND guard (PR #698 review, FIX 5). ReadAsync buffers the part via BufferAsync over the supplied
        // stream BEFORE OpenAsync's reclassification catch runs, so a retryable Transient raised while reading
        // must surface UNMAPPED — never remapped to a corrupt-checkpoint Malformed (which DeltaLog would then
        // swallow into JSON replay). This is the reader-side complement to
        // TransientFaultDuringCheckpointRead_Propagates_NotSilentlyReplayed. RED-on-revert: a bare
        // `catch (Exception)` over BufferAsync, or mapping every DeltaStorageException to Malformed, reddens.
        await using var faulty = new FaultyReadCheckpointBackend.TransientOnReadStream();

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeltaCheckpointReader.ReadAsync(faulty, default));

        Assert.Equal(StorageErrorKind.Transient, error.Kind);
    }

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
    public async Task Corrupt_Parquet_FailsClosed_WithFixedMessage_NoEchoOfUnderlyingBytes()
    {
        // #651: the checkpoint reader's fail-closed message must be a FIXED string that does NOT interpolate
        // the underlying ex.Message — a crafted checkpoint footer's structural bytes (e.g. a field name) could
        // otherwise echo into the surfaced error text (info-leak parity with the ParquetFileReader boundaries).
        // Exact-equality proves no interpolation; the cause is still preserved as the inner exception for logs.
        byte[] garbage = "this is not a parquet file, just bytes"u8.ToArray();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(garbage), default));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Equal("The Delta checkpoint Parquet stream is malformed or truncated.", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task PostFooterDecodeCorruption_FailsClosed_WithFixedMessage_NoEchoOfUnderlyingBytes()
    {
        // #651: the OTHER changed catch — the ReadAsync top-level decode catch (a VALID footer whose
        // post-footer page bytes are corrupt) — must likewise surface a FIXED message, not an ex.Message echo
        // of the corrupt page. A byte flipped in the first data page (offset 8, well before the trailing footer)
        // leaves the footer parseable (OpenAsync succeeds) but fails the row-group page decode → the top-level
        // catch. Exact-equality proves no interpolation; the cause is preserved as the inner exception.
        byte[] valid = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("a.parquet", size: 1)
            .ToParquetAsync();
        valid[8] ^= 0xFF;

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(valid), default));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Equal("The Delta checkpoint Parquet is malformed.", ex.Message);
        Assert.NotNull(ex.InnerException);
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
            typeof(string), numValues: 100_000_000, compressedBytes: 4_096, uncompressedBytes: 4_096, 0);
        Assert.True(footprint > DeltaCheckpointReader.MaxCheckpointRowGroupDecodedBytes,
            $"footprint {footprint} should exceed the {DeltaCheckpointReader.MaxCheckpointRowGroupDecodedBytes}-byte ceiling");
    }

    [Fact]
    public void DecodeCeiling_RejectsDecompressionBomb()
    {
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() =>
            DeltaCheckpointReader.ColumnFootprintBytes(
                typeof(long), numValues: 8, compressedBytes: 1_000, uncompressedBytes: 1_000 * 5_000, 0));
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
            typeof(long), numValues: 1, compressedBytes: 9_223_372_036_854_776L, uncompressedBytes: 1_000, 0);
        Assert.True(footprint > 0 && footprint < DeltaCheckpointReader.MaxCheckpointRowGroupDecodedBytes);
    }

    [Fact]
    public void DecodeCeiling_RejectsNegativeMetadata()
    {
        Assert.Throws<DeltaProtocolException>(() =>
            DeltaCheckpointReader.ColumnFootprintBytes(typeof(long), -1, 10, 10, 0));
    }

    [Fact]
    public void DecodeCeiling_AllowsNormalColumn()
    {
        // A realistic column (100k rows, ~2 MB) is well under the ceiling and does not throw.
        long footprint = DeltaCheckpointReader.ColumnFootprintBytes(
            typeof(long), numValues: 100_000, compressedBytes: 500_000, uncompressedBytes: 2_000_000, 0);
        Assert.True(footprint > 0 && footprint < DeltaCheckpointReader.MaxCheckpointRowGroupDecodedBytes);
    }

    [Fact]
    public void DecodeCeiling_NegativeMetadata_MessageDoesNotEchoColumnPath()
    {
        // #653 info-leak parity: the malformed-checkpoint-column message must NOT interpolate the (schema-
        // derived, attacker-influenceable) column path. Exact-equality proves no '{path}' interpolation; the
        // numeric structural context (group + declared sizes) is retained for diagnosability — these are
        // bounded declared scalars (attacker-declared int64 footer metadata, not a text/injection channel).
        // (The path arg was removed from ColumnFootprintBytes so the surfaced message structurally cannot
        // carry it.)
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() =>
            DeltaCheckpointReader.ColumnFootprintBytes(typeof(long), numValues: -1, compressedBytes: 10, uncompressedBytes: 10, group: 7));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Equal(
            "A checkpoint column (group 7) declares negative metadata (values -1, compressed 10, decompressed 10).",
            ex.Message);
        Assert.DoesNotContain("'", ex.Message, StringComparison.Ordinal);   // no quoted column path
    }

    [Fact]
    public void DecodeCeiling_DecompressionBomb_MessageDoesNotEchoColumnPath()
    {
        // #653: the decompression-bomb rejection likewise carries only bounded declared scalars (the numeric
        // ratio context — attacker-declared int64 footer metadata, not a text/injection channel), never the
        // column path. The declared-bytes / ratio numbers are the diagnostic point of the message.
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() =>
            DeltaCheckpointReader.ColumnFootprintBytes(typeof(long), numValues: 8, compressedBytes: 1_000, uncompressedBytes: 1_000 * 5_000, group: 4));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.DoesNotContain("'", ex.Message, StringComparison.Ordinal);   // no quoted column path
        Assert.Contains("group 4", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ratio ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CumulativePerPartCeiling_Seam_CrossingPart_FailsClosed_AsDecodeCeilingExceeded_NotMalformed()
    {
        // Round-10 #4 — the cumulative per-PART decode ceiling, exercised through the new maxPartDecodedBytes
        // seam. A VALID checkpoint whose per-ROW-GROUP decode is within maxDecodedBytes but whose cumulative
        // across-row-group decode crosses a (tiny, injected) per-part ceiling must fail closed as a DISTINCT
        // resource/decode-ceiling fault — DeltaStorageException(DecodeCeilingExceeded) — NOT as
        // DeltaProtocolException corruption (a resource ceiling mislabelled as Malformed wrongly rejected legit
        // foreign Spark parts as corrupt). RED if the throw at the cumulative crossing reverts to
        // DeltaProtocolException.Malformed, or if the maxPartDecodedBytes seam is dropped.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2).Metadata("t", EmptySchema).Add("f.parquet", size: 1)
            .ToParquetAsync();

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default, maxPartDecodedBytes: 10));

        Assert.Equal(StorageErrorKind.DecodeCeilingExceeded, ex.Kind);
        Assert.Contains("cumulative", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CumulativePerPartCeiling_Seam_LegitPartUnderCeiling_DecodesCleanly()
    {
        // The seam's inverse: a valid checkpoint decoded under the PRODUCTION cumulative ceiling (the default
        // maxPartDecodedBytes) decodes cleanly — the ceiling only bites a genuine crossing, never a normal part.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2).Metadata("t", EmptySchema).Add("f.parquet", size: 1)
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions =
            await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        Assert.Contains(actions, a => a is AddFileAction);
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
    public async Task MalformedReconstruction_MessageDoesNotEchoColumnPath()
    {
        // #653 defense-in-depth: the checkpoint column-reconstruction fail-closed messages (slot-count /
        // repetition-level / physical-type faults in CheckpointColumns) must NOT interpolate the Parquet leaf
        // path. Those paths are a bounded Delta-protocol vocabulary — not a current attacker-byte leak — but
        // they are scrubbed uniformly with the ParquetFileReader decode sites for a consistent
        // no-file-derived-token posture (forward-safe if per-column-stats resolution ever descends these paths
        // into user column names). Trigger one such site: forge the row group to declare one MORE row than it
        // actually holds, so a scalar column's decoded slot count (or a map/list reconstruction's row count) no
        // longer matches rowCount → the reconstruction fails closed. Assert the surfaced message carries no
        // quoted path (no ' character).
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2).Metadata("t", EmptySchema).Add("f.parquet", size: 1)
            .ToParquetAsync();
        long actualRows = await ParquetTestHelpers.RowGroupNumRowsAsync(parquet, rowGroup: 0);
        byte[] forged = await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(
            parquet, rowGroup: 0, forgedNumRows: actualRows + 1);

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(forged), default));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.DoesNotContain("'", ex.Message, StringComparison.Ordinal);   // no quoted (file-derived) leaf path
        // Pins the scrubbed CheckpointColumns SlotMismatch site (not the generic outer catch): the message
        // names the column CLASS and the structural slot/row counts, never the leaf path.
        Assert.Contains("A checkpoint scalar column produced", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedMapReconstruction_MessageDoesNotEchoMapKeyLeafPath()
    {
        // #653 no-echo (MAP reconstruction path). The sibling MalformedReconstruction_... test covers the
        // SCALAR site; a global numRows forge on a full checkpoint never reaches the map/list sites because the
        // FIRST scalar column (add/path) trips first. This covers the MAP site: a MINIMAL checkpoint whose ONLY
        // column is the add.partitionValues MAP (no preceding scalar) so the map is the first reconstructed
        // column. Its key leaf is renamed to an attacker SENTINEL — and unlike the scalar add/path (a fixed
        // protocol name), a map key leaf name is genuinely FILE-DERIVED: CheckpointSchema.Map returns
        // Parquet.Net's logical .Key verbatim from the footer, so a foreign checkpoint controls it. The row
        // group over-declares its row count, so CheckpointColumns.ForEachMapEntry → EnsureRowCount fails closed
        // — and its message must NOT echo the file-derived key path (add/partitionValues/key_value/<sentinel>).
        const string keySentinel = "att4cker_ckpt_map_key_s3ntinel";
        byte[] forged = await CheckpointFixture.MalformedAddPartitionValuesMapAsync(keySentinel);

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(forged), default));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.DoesNotContain(keySentinel, ex.Message, StringComparison.Ordinal);   // no file-derived leaf path
        Assert.DoesNotContain("'", ex.Message, StringComparison.Ordinal);           // no quoted path at all
        // Pins the scrubbed EnsureRowCount site reached via ForEachMapEntry (not the scalar SlotMismatch site):
        // the message names the column CLASS and the reconstructed/declared row counts, never the leaf path.
        Assert.Contains("A checkpoint column reconstructed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedListReconstruction_MessageDoesNotEchoListElementLeafPath()
    {
        // #653 no-echo (LIST reconstruction path). Sibling of the map test above: a MINIMAL checkpoint whose
        // ONLY column is the metaData.partitionColumns LIST (no preceding scalar), so the list is the first
        // reconstructed column. Its element leaf is named an attacker SENTINEL — file-derived, since
        // CheckpointSchema.ListElement returns Parquet.Net's logical .Item verbatim from the footer. The row
        // group over-declares its row count, so CheckpointColumns.ForEachListElement → EnsureRowCount fails
        // closed — and its message must NOT echo the element path (metaData/partitionColumns/list/<sentinel>).
        const string elementSentinel = "att4cker_ckpt_list_elem_s3ntinel";
        byte[] forged = await CheckpointFixture.MalformedMetaPartitionColumnsListAsync(elementSentinel);

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(forged), default));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.DoesNotContain(elementSentinel, ex.Message, StringComparison.Ordinal);   // no file-derived path
        Assert.DoesNotContain("'", ex.Message, StringComparison.Ordinal);               // no quoted path at all
        // Pins the scrubbed EnsureRowCount site reached via ForEachListElement (not the scalar SlotMismatch).
        Assert.Contains("A checkpoint column reconstructed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedScalarReconstruction_RepeatedScalar_MessageDoesNotEchoColumnPath()
    {
        // #653 no-echo (SCALAR repetition site, CheckpointColumns.FillScalar col.MaxRepetition != 0). A
        // checkpoint whose add.size leaf is a legacy 1-level REPEATED long (MaxRepetitionLevel=1) still resolves
        // as an ordinary scalar (CheckpointSchema.Scalar matches only the expected NAME, not the repetition), so
        // the scalar reader reaches FillScalar, which fails closed on the unexpected repetition. Unlike a
        // map/list leaf (whose name is attacker-forgeable), a SCALAR leaf name is a bounded Delta-protocol
        // vocabulary (add/size), so no sentinel can ride it — the load-bearing no-echo signal here is the
        // absence of any ' (quoting): a reverted scrub re-emits 'add/size' QUOTED (mirrors the sibling scalar
        // SlotMismatch test, which pins the same class of leak on the other scalar branch).
        byte[] parquet = await CheckpointFixture.MalformedRepeatedAddSizeScalarAsync();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.DoesNotContain("'", ex.Message, StringComparison.Ordinal);   // no quoted (would-be 'add/size') path
        // Pins the scrubbed FillScalar repetition site: names the column CLASS, never the leaf path.
        Assert.Contains("scalar column is unexpectedly repeated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedMapReconstruction_RowOutOfRange_MessageDoesNotEchoMapKeyLeafPath()
    {
        // #653 no-echo (MAP reconstruction, EnsureRowInRange). Sibling of MalformedMapReconstruction_… above
        // which OVER-declares the row count (→ EnsureRowCount); this UNDER-declares it (actual-1) so a
        // repetition-0 slot advances the reconstructed row PAST the shrunken bound and ForEachMapEntry →
        // EnsureRowInRange fails closed. The map key leaf carries an attacker SENTINEL — file-derived, since
        // CheckpointSchema.Map returns Parquet.Net's logical .Key verbatim — so a reverted scrub would echo
        // add/partitionValues/key_value/<sentinel>. NOTE: this branch's fixed message legitimately contains an
        // apostrophe ("column's"), so unlike the scalar/physical-type tests the no-echo signal is the SENTINEL's
        // absence, not the absence of ' — DoesNotContain(sentinel) is the load-bearing assertion.
        const string keySentinel = "att4cker_ckpt_map_range_s3ntinel";
        byte[] forged =
            await CheckpointFixture.MalformedAddPartitionValuesMapAsync(keySentinel, overDeclareRows: false);

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(forged), default));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.DoesNotContain(keySentinel, ex.Message, StringComparison.Ordinal);   // no file-derived leaf path
        // Pins the scrubbed EnsureRowInRange site (distinct from the EnsureRowCount site the sibling pins).
        Assert.Contains("repetition levels address row", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedMapPhysicalType_MessageDoesNotEchoMapValueLeafPath()
    {
        // #653 no-echo (physical-type site, CheckpointColumns.ReadRawAsync InvalidCastException catch — the R4
        // finding). A checkpoint add.partitionValues MAP whose VALUE leaf is physically INT32 (renamed to an
        // attacker SENTINEL) is read AS A STRING by the map reconstruction, so the wrong-typed RawColumnData
        // cast throws InvalidCastException and the reader fails closed. The value leaf is file-derived
        // (CheckpointSchema.Map returns Parquet.Net's logical .Value verbatim), so a reverted scrub would echo
        // add/partitionValues/key_value/<sentinel>. The KEY stays a well-formed string so the VALUE read (not
        // the key read) is the site that trips.
        const string valueSentinel = "att4cker_ckpt_map_type_s3ntinel";
        byte[] forged = await CheckpointFixture.MalformedAddPartitionValuesMapValueTypeAsync(valueSentinel);

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(forged), default));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.DoesNotContain(valueSentinel, ex.Message, StringComparison.Ordinal);   // no file-derived leaf path
        Assert.DoesNotContain("'", ex.Message, StringComparison.Ordinal);             // no quoted path at all
        // Pins the scrubbed ReadRawAsync physical-type site (not the outer generic "malformed" catch).
        Assert.Contains("unexpected physical type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedMapReconstruction_SlotMismatch_MessageDoesNotEchoMapKeyLeafPath()
    {
        // #653 no-echo (MAP reconstruction, ForEachMapEntry key/value SLOT-COUNT check) — the R6 finding that
        // corrects an R5 "UNREACHABLE" annotation. R5 claimed this branch could not be hit through the public
        // read door because the only tool that can desync a map's key/value level streams — the low-level
        // ParquetRowGroupWriter.WriteAsync<T> — is T:struct-constrained and so (it claimed) cannot author the
        // STRING leaves a checkpoint map requires, meaning a slot-divergent map would necessarily be non-string
        // and trip the physical-type guard FIRST. That is FALSE: ReadOnlyMemory<char> IS a struct, so the
        // low-level writer authors a genuine STRING map whose KEY stream has 3 slots (rep 0,1,0) and VALUE
        // stream has 2 (rep 0,0). Both decode cleanly as ReadOnlyMemory<char> (no physical-type trip) and,
        // because both still carry two repetition-0 rows, the row count stays a consistent 2 — so
        // CheckpointColumns.ForEachMapEntry reaches the slot-count check (keys.Definition.Length 3 !=
        // values.Definition.Length 2) rather than a row-count check. The map key leaf carries an attacker
        // SENTINEL — file-derived, since CheckpointSchema.Map returns Parquet.Net's logical .Key verbatim — so a
        // reverted scrub would echo add/partitionValues/key_value/<sentinel>. NOTE: this branch's fixed message
        // quotes nothing, so (unlike the scalar/physical-type siblings) the no-echo signal is the SENTINEL's
        // absence — DoesNotContain(sentinel) is the load-bearing assertion; Contains(...) pins THIS branch.
        const string keySentinel = "att4cker_ckpt_map_slots_s3ntinel";
        byte[] forged = await CheckpointFixture.MalformedAddPartitionValuesMapSlotMismatchAsync(keySentinel);

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(forged), default));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.DoesNotContain(keySentinel, ex.Message, StringComparison.Ordinal);   // no file-derived leaf path
        // Pins the scrubbed slot-count branch of ForEachMapEntry (distinct from the EnsureRowInRange/
        // EnsureRowCount and physical-type sites the other map siblings pin): names the column CLASS and the
        // bounded slot counts, never the leaf path.
        Assert.Contains("mismatched key/value slot counts", ex.Message, StringComparison.Ordinal);
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
    public async Task MalformedDeletionVector_UnrecognizedStorageType_MessageDoesNotEchoAttackerValue()
    {
        // #653 re-check of the CHECKPOINT-path DvMalformed site (CheckpointColumns' DvMalformed helper): it
        // wraps the SHARED DeletionVectorDescriptor.Create validator's `detail` and adds only action/row/group
        // — it has NO DataField in scope, so it is structurally incapable of echoing a leaf path, and the one
        // attacker-controlled value that reaches it (the storageType STRING) is already scrubbed by Create into
        // a fixed domain message. This drives an oversized sentinel storageType through the checkpoint decode
        // door (the sibling BadStorageType_ test above uses a 1-char "x" that could not reveal an echo) and
        // pins that the surfaced message never carries the attacker value — parity with the JSON-path
        // Parse_UnrecognizedStorageType_… regression, since both share the one validator. (Belt-and-suspenders:
        // there is no field.Path to revert here, so this is a permanent no-echo pin, not a mutation oracle.)
        const string sentinel = "att4cker_ckpt_dv_st0rageType_s3ntinel";
        byte[] parquet = await new CheckpointFixture()
            .Protocol(3, 7, readerFeatures: ["deletionVectors"], writerFeatures: ["deletionVectors"])
            .Metadata("t", EmptySchema)
            .Add("dv.parquet", size: 1,
                deletionVector: new CheckpointFixture.DvColumns(sentinel, "0123456789abcdefghij", Offset: 4, SizeInBytes: 40, Cardinality: 3))
            .ToParquetAsync();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.DoesNotContain(sentinel, ex.Message, StringComparison.Ordinal);   // attacker storageType never surfaced
        Assert.Contains("malformed deletionVector", ex.Message, StringComparison.Ordinal);   // pins the checkpoint DvMalformed wrapper
        Assert.Contains("storageType is not one of", ex.Message, StringComparison.Ordinal);  // fixed bounded domain
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

    // ---- #681: Parquet Modular Encryption on the CHECKPOINT door (diagnosability parity with #655/#680) --
    //
    // The data-file door (ParquetFileReader.OpenAsync) classifies both encryption modes as an actionable
    // UnsupportedFeature. The checkpoint door opens its Parquet through a SEPARATE door
    // (DeltaCheckpointReader.OpenAsync -> ParquetReader.CreateAsync), which used to report an encrypted
    // checkpoint as MalformedAction — fail-closed, but a misleading diagnosis: the file is not malformed, it
    // is a valid checkpoint written with a feature DeltaSharp cannot read. Both doors now share ONE classifier
    // (ParquetEncryption), so these tests pin BOTH of its arms on the checkpoint door:
    //   * SUCCESS path — a plaintext-footer file whose encrypted columns keep their plaintext ColumnMetaData
    //     opens cleanly, and is caught from the PARSED footer (encryption_algorithm / ColumnCryptoMetaData);
    //   * FAILURE path — an encrypted-footer ('PARE') file, and the shape a REAL plaintext-footer encryptor
    //     writes (encrypted column's plaintext ColumnMetaData omitted), both make Parquet.Net throw at open,
    //     and are caught by reading the input's own magic / plaintext footer.
    // Fixtures reuse the #655/#680 footer-splicing helpers, applied to a real checkpoint Parquet.

    [Fact]
    public async Task PlaintextFooterEncryptedCheckpoint_IsUnsupportedFeature_NotMalformed()
    {
        // SUCCESS-path arm: file-level encryption_algorithm spliced into a real checkpoint's plaintext footer
        // (ordinary 'PAR1' magic, footer parses cleanly, so CreateAsync succeeds).
        // RED-on-revert: dropping the checkpoint door's IsPlaintextFooterEncrypted check lets the checkpoint
        // open and decode its (plaintext) pages, so no exception is thrown at all.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("a.parquet", size: 1)
            .ToParquetAsync();
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterEncryptedFileAsync(parquet);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(encrypted), default));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
        // The whole point of #681: the diagnosis is the feature, never the fail-closed "malformed" default.
        Assert.DoesNotContain("malformed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownEncryptionAlgorithmUnionMemberCheckpoint_IsUnsupportedFeature_NotMalformed()
    {
        // #773 — the foreign-writer residual, closed on the CHECKPOINT door. The checkpoint's footer carries an
        // EncryptionAlgorithm whose union member is an UNKNOWN id (3); Parquet.Net SkipFields it and opens the
        // checkpoint with both known members null, so the PARSED classifier (which also has inspectable columns
        // here and no crypto_metadata) returns false — the residual. The raw-footer disambiguation must classify
        // it UnsupportedFeature (non-empty field-8), not let the checkpoint decode its (plaintext-in-fixture)
        // pages. RED-on-revert: reverting the door to IsPlaintextFooterEncrypted(metadata) — the parsed-only
        // overload — reopens the residual; the classifier returns false and ReadAsync decodes the checkpoint and
        // returns its actions with NO exception, so this Assert.ThrowsAsync fails ("No exception was thrown").
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("a.parquet", size: 1)
            .ToParquetAsync();
        byte[] forged = await ParquetTestHelpers.UnknownEncryptionAlgorithmUnionMemberFileAsync(parquet);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(forged), default));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("malformed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaintextFooterEncryptedCheckpoint_OnlySubsetOfColumnsEncrypted_IsUnsupportedFeature()
    {
        // SUCCESS-path arm, per-column marker only: a plaintext-footer checkpoint may encrypt just SOME
        // columns — file-level encryption_algorithm UNSET, one column chunk carrying ColumnCryptoMetaData.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("a.parquet", size: 1)
            .ToParquetAsync();
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterColumnCryptoFileAsync(
            parquet, rowGroup: 0, columnIndex: 0);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(encrypted), default));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaintextFooterEncryptedCheckpoint_EncryptedColumnMetaDataOmitted_IsUnsupportedFeature()
    {
        // FAILURE-path arm: the shape a genuine encryptor writes — the encrypted column's plaintext
        // ColumnMetaData is absent (stored encrypted), which makes Parquet.Net 6.0.3 throw inside CreateAsync
        // before any parsed-metadata check can run. Only the footer probe can classify it.
        // RED-on-revert: dropping the checkpoint door's ClassifyUnreadableInput call sends this straight back
        // to DeltaProtocolException/MalformedAction.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("a.parquet", size: 1)
            .ToParquetAsync();
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterEncryptedRealisticFileAsync(
            parquet, rowGroup: 0, columnIndex: 0);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(encrypted), default));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("malformed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EncryptedFooterCheckpoint_IsUnsupportedFeature_NotMalformed()
    {
        // FAILURE-path arm, encrypted-FOOTER mode (#649): the checkpoint is bracketed by the 'PARE' magic,
        // which Parquet.Net rejects with a message byte-for-byte identical to the one it emits for arbitrary
        // garbage — so the classification comes from the file's own magic, at both ends.
        byte[] encrypted = ParquetTestHelpers.EncryptedFooterMagicFile();

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(encrypted), default));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SchemaFirstOrdering_IsSoleControl_ForAtLeastOneCorruptFlip()
    {
        // EXECUTABLE PIN for the `_ = reader.Schema;` ordering claim in DeltaCheckpointReader.OpenAsync
        // (#698 Balanced review). That claim used to live only in prose, and it went stale twice — this test
        // asserts it instead, so it cannot drift again.
        //
        // It pins BOTH halves of the invariant, over the same seeded corpus the fuzz test uses:
        //   (a) there is AT LEAST ONE bit-flip whose corrupt footer the classifier ALONE would call
        //       "encrypted" — so the schema probe is load-bearing, not defense-in-depth; and
        //   (b) for every such flip the door still reports MALFORMED, never UnsupportedFeature — so the
        //       ordering actually does its job.
        // Assert.NotEmpty on (a) is the anti-vacuity guard: if a future classifier change ever covers the
        // whole corpus on its own, this goes RED and whoever made that change must re-derive the comment
        // rather than leave it stale. Deleting `_ = reader.Schema;` turns (b) RED.
        //
        // The set is derived by RUNNING the classifier, so on its own it would encode the implementation's
        // own predicate and confirm it rather than test it (#698 gate finding): a future precision
        // regression would silently redefine the set instead of failing. Its IDENTITY is therefore pinned
        // too, against the value four review seats and three gate runs measured independently.
        //
        // These indices are NOT a tunable bound that gets re-fitted to make a test pass. They are a
        // property of a FIXED seed over a FIXED corpus, so there are exactly two ways they can move — the
        // corpus changed, or the classifier's precision changed — and both must be noticed, not absorbed.
        // A pin like this is re-VERIFIED, never re-TUNED; if it goes red, derive why before touching it.
        // The byte/bit offsets are pinned alongside the indices so a corpus change is self-diagnosing:
        // offsets shifting while indices hold means the fixture moved, not the classifier.
        byte[] valid = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", """{"type":"struct","fields":[]}""")
            .Add("a.parquet", size: 1)
            .ToParquetAsync();

        var random = new Random(5);
        var classifierAloneWouldMisclassify = new List<(int Iteration, int ByteOffset, int Bit)>();
        for (int i = 0; i < 400; i++)
        {
            byte[] mutated = (byte[])valid.Clone();
            int byteOffset = random.Next(mutated.Length);
            int bit = random.Next(8);
            mutated[byteOffset] ^= (byte)(1 << bit);

            global::Parquet.Meta.FileMetaData? metadata = null;
            try
            {
                using var probe = new MemoryStream(mutated, writable: false);
                global::Parquet.ParquetReader reader =
                    await global::Parquet.ParquetReader.CreateAsync(probe, null, false, default);
                await using (reader.ConfigureAwait(false))
                {
                    metadata = reader.Metadata;
                }
            }
            catch
            {
                continue; // Cannot even open: the failure-path probe owns this one, not the ordering.
            }

            if (!ParquetEncryption.IsPlaintextFooterEncrypted(metadata))
            {
                continue;
            }

            classifierAloneWouldMisclassify.Add((i, byteOffset, bit));

            // (b) The door must still fail closed as MALFORMED — the schema probe rejects it first.
            await Assert.ThrowsAsync<DeltaProtocolException>(
                () => DeltaCheckpointReader.ReadAsync(new MemoryStream(mutated), default));
        }

        // (a) Non-vacuity: the schema probe is the SOLE control for these flips.
        Assert.NotEmpty(classifierAloneWouldMisclassify);

        // (c) Identity: the set is exactly this, not merely non-empty. Independently measured by the
        // Architect, Security, Balanced and Quality seats and by three red-team gate runs.
        //
        // Measured direction of travel, so a future reader knows what a failure here means. Reverting the
        // union rule to bare presence — the MAXIMAL classifier, which calls every non-null algorithm
        // encrypted — leaves this set byte-identical, so on this corpus the set cannot GROW from a
        // classifier change: these 4 are every flip that produces a non-null encryption_algorithm at all.
        // It can only SHRINK (a precision change; (a) catches a shrink to empty and (c) catches a shrink
        // to a proper subset) or MOVE (the corpus changed). Note the seed is not free either: seed 6 over
        // this corpus hits the unbounded-CreateAsync hang tracked in #699, so re-seeding to make this pass
        // is not an option that exists.
        Assert.Equal(
            new[] { (45, 2982, 5), (289, 2428, 7), (306, 2430, 5), (353, 2546, 7) },
            classifierAloneWouldMisclassify);
    }

    [Fact]
    public async Task EncryptionAlgorithmWithNoRowGroupsCheckpoint_FailsClosed_AsUnsupportedFeature()
    {
        // #773 (was #698 gate finding) — a footer carrying a non-null encryption_algorithm whose known union
        // members are both null (the shape an unknown algorithm id takes) AND no row groups. Under the former
        // three-arm classifier this was the case the per-column CryptoMetadata backstop could not vouch for;
        // now bare-presence arm-1 covers it directly — ANY parsed encryption_algorithm fails closed.
        // RED-on-revert: narrowing arm-1 back to a non-empty-union requirement lets this checkpoint be read as
        // ordinary plaintext, so no exception is thrown at all.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("a.parquet", size: 1)
            .ToParquetAsync();
        byte[] forged = await ParquetTestHelpers.EmptyEncryptionAlgorithmUnionNoRowGroupsFileAsync(parquet);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(forged), default));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaintextFooterEncryptedCheckpoint_AesGcmCtrV1_IsUnsupportedFeature()
    {
        // #698 review FIX 7 — pins the SECOND union disjunct. parquet.thrift defines EncryptionAlgorithm as
        // exactly {AES_GCM_V1, AES_GCM_CTR_V1}; the other fixtures all use the former, so without this one a
        // future edit could drop the AESGCMCTRV1 arm of the classifier and no test would notice.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("a.parquet", size: 1)
            .ToParquetAsync();
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterEncryptedCtrFileAsync(parquet);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(encrypted), default));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("malformed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyEncryptionAlgorithmUnionCheckpoint_FailsClosed_AsUnsupportedFeature()
    {
        // #773 (supersedes #698 review FIX 4) — the SUCCESS-path arm on the checkpoint door. The footer carries
        // a NON-NULL encryption_algorithm whose parsed union has NEITHER member set. That shape is AMBIGUOUS:
        // a corrupt footer parses into it AND Parquet.Net produces it for an UNKNOWN union member it dropped
        // via SkipField. Since the parsed layer cannot distinguish them and a raw re-parse cannot securely
        // agree with Parquet.Net's tolerant parser (four differentials found), the classifier now BARE-PRESENCE
        // fails closed on ANY parsed encryption_algorithm. Everything else in the footer is intact, so the file
        // opens AND its schema materializes — schema-first does NOT reject it — leaving this bare-presence check
        // as the control: UnsupportedFeature, not a plaintext read. RED-on-revert: narrowing arm-1 back to a
        // non-empty-union requirement reopens the #773 fail-open and this Assert.ThrowsAsync fails.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("a.parquet", size: 1)
            .ToParquetAsync();
        byte[] forged = await ParquetTestHelpers.EmptyEncryptionAlgorithmUnionFileAsync(parquet);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(forged), default));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EncryptedCheckpoint_Message_IsFixed_AndEchoesNoCheckpointContent()
    {
        // #653 hygiene on the NEW classification: the surfaced text must be a FIXED, presence-only diagnosis.
        // The sentinel action path is genuinely attacker-influenced AND genuinely present in the footer this
        // door parsed (Parquet writes column statistics — min/max of the path column — into the footer), so
        // its absence from the message is a real no-echo proof, not a vacuous one. Exact-equality additionally
        // proves nothing is interpolated, and the null inner proves no raw library fault rides along.
        const string sentinel = "s3://tenant-42/secret-prefix/leak-me.parquet";
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add(sentinel, size: 1)
            .ToParquetAsync();
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterEncryptedFileAsync(parquet);

        // Pin the no-echo proof to the exact bytes this door PARSED — the trailing footer of the file it was
        // handed — not merely to the file as a whole (where the sentinel also sits in the data pages). If
        // Parquet ever stopped writing the path column's statistics into the footer, a whole-file assertion
        // would still pass while the proof quietly evaporated; this slice cannot go vacuous without failing.
        int footerLength = BitConverter.ToInt32(encrypted, encrypted.Length - 8);
        int footerStart = encrypted.Length - 8 - footerLength;
        Assert.Contains(
            sentinel,
            System.Text.Encoding.UTF8.GetString(encrypted, footerStart, footerLength),
            StringComparison.Ordinal);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(encrypted), default));

        Assert.Equal(
            "Parquet Modular Encryption is not supported: the file uses plaintext-footer encryption "
            + "(the footer carries Parquet Modular Encryption metadata). DeltaSharp cannot read encrypted "
            + "Parquet files.",
            error.Message);
        Assert.DoesNotContain(sentinel, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-42", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("encryption_algorithm", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AESGCM", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task NormalCheckpoint_WithoutEncryptionMetadata_ReadsNormally_NoFalsePositive()
    {
        // Precision guard: the SAME checkpoint the encrypted fixtures are spliced from must still read — only
        // the spliced crypto field flips the classification, so the classifier cannot be firing on "any
        // checkpoint" or on a merely-successful open.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("a.parquet", size: 1)
            .ToParquetAsync();

        IReadOnlyList<DeltaAction> actions = await DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default);

        Assert.Single(actions.OfType<AddFileAction>(), a => a.Path == "a.parquet");
    }

    [Fact]
    public async Task CorruptCheckpoint_StaysMalformed_NotReclassifiedAsEncryption()
    {
        // Precision guard on the other side: genuine corruption must NOT be relabeled "encrypted". A
        // bit-flipped trailing footer LENGTH is the adversarial case for the footer probe (it reads that
        // length), and a plain garbage stream is the baseline — both stay MalformedAction.
        byte[] parquet = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema)
            .Add("a.parquet", size: 1)
            .ToParquetAsync();
        parquet[^5] ^= 0xFF; // last byte of the 4-byte footer length, just before the trailing 'PAR1'

        DeltaProtocolException flipped = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream(parquet), default));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, flipped.Kind);

        DeltaProtocolException garbage = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => DeltaCheckpointReader.ReadAsync(new MemoryStream("not a parquet file"u8.ToArray()), default));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, garbage.Kind);
    }

    private const string EmptySchema = """{"type":"struct","fields":[]}""";

    // ---- #773 (Quality residual): DisposeQuietlyAsync fail-closed dispose contract -------------------------
    //
    // The fail-closed open path abandons its reader through DisposeQuietlyAsync so a dispose-time fault cannot
    // escape UNMAPPED and replace the classification the caller is about to throw. These pins fix that
    // contract directly (it is otherwise only reachable when a real ParquetReader.DisposeAsync throws, which
    // no fixture can force): every NON-cancellation dispose fault is swallowed, cancellation still propagates,
    // and the normal reader is disposed exactly once.

    private sealed class ThrowingAsyncDisposable(Exception fault) : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            throw fault;
        }
    }

    private sealed class CountingAsyncDisposable : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task DisposeQuietlyAsync_SwallowsNonCancellationDisposeFault()
    {
        // A dispose-time IOException on the abandoned reader must be swallowed so the pending classification
        // remains the single, meaningful outcome. RED-on-revert: narrowing the catch (or removing it) lets the
        // fault escape and this call throws.
        var reader = new ThrowingAsyncDisposable(new IOException("disk gone during teardown"));

        await DeltaCheckpointReader.DisposeQuietlyAsync(reader);

        Assert.Equal(1, reader.DisposeCount);
    }

    [Fact]
    public async Task DisposeQuietlyAsync_PropagatesCancellation()
    {
        // Cancellation is the ONE dispose fault that must NOT be swallowed — it is not a cleanup defect and the
        // caller's cooperative-cancellation contract depends on it surfacing. RED-on-revert: broadening the
        // catch to all exceptions swallows this and the assertion flips.
        var reader = new ThrowingAsyncDisposable(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await DeltaCheckpointReader.DisposeQuietlyAsync(reader));

        Assert.Equal(1, reader.DisposeCount);
    }

    [Fact]
    public async Task DisposeQuietlyAsync_DisposesNormalReaderExactlyOnce()
    {
        // Positive control: a well-behaved reader is disposed exactly once and no fault is invented.
        var reader = new CountingAsyncDisposable();

        await DeltaCheckpointReader.DisposeQuietlyAsync(reader);

        Assert.Equal(1, reader.DisposeCount);
    }

    [Fact]
    public void DeriveSizeAwareBudget_ClampsToFloorForSmallParts_AndIsMonotonicNonDecreasing()
    {
        // High #6 + Round-8 #4 + Round-10 #11 — direct proof the per-part budget derives from a DECODED-bytes
        // estimate (compressed × bounded expansion) clamped to the per-PART decoded ceiling (8 GiB — Round-10 #4),
        // then divided by the floor throughput, floored at 30 s and CAPPED at 128 s (Round-10 #11 — the budget-
        // time cap is DECOUPLED from the larger cumulative RESOURCE ceiling), NOT the arithmetically-inert
        // compressed÷decoded the Round-5 code used (which always collapsed to the floor), and NOT the per-ROW-
        // GROUP 1 GiB ceiling the Round-5 code clamped the basis to (which capped the budget at 32 s < the real
        // decode of a foreign 200–500 MiB Spark part → deterministic timeout → strike → 24h suppression →
        // unreadable table). This test pins FOUR properties of the pure derivation:
        //   (1) FLOOR CLAMP — a tiny part still gets at least the default budget (never zero/negative).
        //   (2) MONOTONE NON-DECREASING — a larger part never gets a SMALLER budget than a smaller one.
        //   (3) CEILING CLAMP — an enormous / overflowing compressed length saturates its DECODED estimate to
        //       the enforced per-PART decoded-bytes ceiling (8 GiB) and never overflows to a negative TimeSpan.
        //   (4) 128 s BUDGET CAP (Round-10 #11) — even an 8 GiB decoded basis (which would derive 256 s at the
        //       32 MiB/s floor) is capped at 128 s, decoupled from the cumulative resource ceiling.
        var sizes = new long[]
        {
            0L,
            4L * 1024, // 4 KiB
            1L * 1024 * 1024, // 1 MiB
            16L * 1024 * 1024, // 16 MiB
            64L * 1024 * 1024, // 64 MiB
            256L * 1024 * 1024, // 256 MiB
            DeltaCheckpointReader.MaxCheckpointPartBytes, // 512 MiB — the enforced per-part buffer ceiling
            long.MaxValue, // overflow guard
        };

        TimeSpan previous = TimeSpan.MinValue;
        foreach (long size in sizes)
        {
            TimeSpan budget = DeltaCheckpointReader.DeriveSizeAwareBudget(size);

            // (1) never below the floor and never a non-positive budget.
            Assert.True(budget >= BoundedDecode.DefaultBudget, $"size={size}: budget {budget} < floor {BoundedDecode.DefaultBudget}");
            Assert.True(budget > TimeSpan.Zero, $"size={size}: budget {budget} must be positive");

            // (2) monotone non-decreasing in the part's compressed length.
            Assert.True(budget >= previous, $"size={size}: budget {budget} decreased below previous {previous}");
            previous = budget;

            // (3)+(4) never above the 128 s cap (which is itself well below the defensive MaxBudget).
            Assert.True(budget <= TimeSpan.FromSeconds(128), $"size={size}: budget {budget} > the 128 s cap");
            Assert.True(budget <= BoundedDecode.MaxBudget, $"size={size}: budget {budget} > ceiling {BoundedDecode.MaxBudget}");
        }

        // A small part collapses to the FLOOR exactly (the decoded estimate is well under the throughput floor).
        Assert.Equal(BoundedDecode.DefaultBudget, DeltaCheckpointReader.DeriveSizeAwareBudget(4L * 1024));

        // STRICT-SCALING (Round-8 test charge) — a 256 MiB part gets a STRICTLY LARGER budget than a 4 MiB part:
        // 256 MiB × 8 = 2 GiB decoded / 32 MiB/s = 64s, vs 4 MiB → floor (30s). This is the property that would
        // silently regress if the basis reverted to the compressed length or the collapse-to-floor arithmetic.
        Assert.True(
            DeltaCheckpointReader.DeriveSizeAwareBudget(256L * 1024 * 1024) > DeltaCheckpointReader.DeriveSizeAwareBudget(4L * 1024 * 1024),
            "a 256 MiB part must derive a strictly larger budget than a 4 MiB part (decoded-bytes basis scales with size).");
        Assert.Equal(TimeSpan.FromSeconds(64), DeltaCheckpointReader.DeriveSizeAwareBudget(256L * 1024 * 1024));

        // (4) THE 128 s CAP is the operative upper bound (Round-10 #11): a compressed length whose ×8 decoded
        // estimate saturates to the 8 GiB per-PART ceiling would derive 8 GiB / 32 MiB/s = 256 s, but is CAPPED
        // at 128 s — decoupled from the (larger) cumulative resource ceiling. RED if the cap is removed (the
        // budget would jump to 256 s) or reverted to the MaxBudget clamp.
        Assert.Equal(TimeSpan.FromSeconds(128), DeltaCheckpointReader.DeriveSizeAwareBudget(long.MaxValue));
        Assert.True(DeltaCheckpointReader.DeriveSizeAwareBudget(long.MaxValue) < BoundedDecode.MaxBudget,
            "the 128 s cap holds the budget well below the defensive MaxBudget clamp.");
    }

    [Fact]
    public async Task MultiPart_I7_EachPartDecodesUnderItsOwnBudget_Part2NotStarvedByPart1()
    {
        // I7 behavioral (Round-8 test charge) — a multi-part checkpoint decodes EACH part under its OWN
        // size-aware budget, NEVER a shrinking aggregate remainder shared across parts (which would hand a later
        // part a starved budget and seed a healthy-but-slow part into the negative cache as "known-bad" →
        // permanent JSON replay → an unreadable table). Proven two ways:
        //   (1) BEHAVIORAL — a 2-part checkpoint where part 1 is substantially larger than part 2 decodes BOTH
        //       parts in order and reconstructs every action, so part 2 (processed SECOND) is not dropped/starved
        //       by part 1's decode.
        //   (2) STATELESS BUDGET — the per-part budget derivation is a PURE function of THAT part's bytes: part 2's
        //       budget is identical whether or not part 1 was derived first, and it equals its standalone budget.
        //       A shared-remainder design would make part 2's budget a function of part 1's consumption — RED here.
        var fixture = new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", EmptySchema);
        for (int i = 0; i < 64; i++)
        {
            fixture.Add($"big-{i:D3}.parquet", size: 1000 + i);
        }

        fixture.Add("small.parquet", size: 7);

        byte[][] parts = await fixture.ToPartsAsync(parts: 2);
        Assert.Equal(2, parts.Length);

        // (1) BOTH parts decode under their own budget (decodeBudget:null → DeriveSizeAwareBudget per part). Record
        // the ACTUAL budget each part's RunAsync receives via the onPartBudget seam — not a test re-derivation — so
        // this is the real value threaded into the bounded decode, closing the "pure-static tautology" gap.
        var all = new List<DeltaAction>();
        var observedBudgets = new List<TimeSpan>();
        foreach (byte[] part in parts)
        {
            all.AddRange(await DeltaCheckpointReader.ReadAsync(
                new MemoryStream(part), default, decodeBudget: null,
                onPartBudget: observedBudgets.Add));
        }

        Assert.Equal(65, all.OfType<AddFileAction>().Count()); // every action across BOTH parts survived
        Assert.Single(all.OfType<ProtocolAction>());
        Assert.Single(all.OfType<MetadataAction>());

        // Each RECORDED budget equals the size-aware budget of THAT part's own bytes — never a residual of the
        // prior part. RED under a shared-remainder design (part 2's recorded budget would depend on part 1).
        Assert.Equal(2, observedBudgets.Count);
        Assert.Equal(DeltaCheckpointReader.DeriveSizeAwareBudget(parts[0].Length), observedBudgets[0]);
        Assert.Equal(DeltaCheckpointReader.DeriveSizeAwareBudget(parts[1].Length), observedBudgets[1]);

        // (2) The per-part budget is stateless: part 2's budget does not depend on part 1 having been derived
        // first (no shrinking remainder). Larger part 1 ⇒ larger-or-equal budget than the smaller part 2.
        TimeSpan part1Budget = DeltaCheckpointReader.DeriveSizeAwareBudget(parts[0].Length);
        TimeSpan part2First = DeltaCheckpointReader.DeriveSizeAwareBudget(parts[1].Length);
        _ = DeltaCheckpointReader.DeriveSizeAwareBudget(parts[0].Length); // "consume" part 1 again
        TimeSpan part2AfterPart1 = DeltaCheckpointReader.DeriveSizeAwareBudget(parts[1].Length);

        Assert.Equal(part2First, part2AfterPart1); // part 2's budget is invariant — NOT a residual of part 1
        Assert.True(part2AfterPart1 > TimeSpan.Zero, "part 2 must always get a positive, viable budget of its own.");
        Assert.True(part1Budget >= part2AfterPart1, "the larger part 1 must derive a budget ≥ the smaller part 2's.");
    }

    [Fact]
    public void Consts_CheckpointDecodedCeilings_MatchTheirDerivation()
    {
        // Specialist Medium (Round-13): pin the checkpoint decoded-bytes ceilings against silent literal drift.
        // The MaxCheckpointPartDecodedBytes doc claims it is DERIVED as
        // max(MaxCheckpointPartBytes × CheckpointDecodedExpansionFactor,
        //     CheckpointCumulativeRowGroupFloorMultiple × MaxCheckpointRowGroupDecodedBytes)
        // but the const is a hard literal — this test makes that derivation load-bearing so the literal cannot
        // drift from the two contributing ceilings (the floor term currently wins: 8 × 1 GiB = 8 GiB).
        long derivedPartDecoded = Math.Max(
            DeltaCheckpointReader.MaxCheckpointPartBytes
                * (long)DeltaCheckpointReader.CheckpointDecodedExpansionFactorForTest,
            DeltaCheckpointReader.CheckpointCumulativeRowGroupFloorMultiple
                * DeltaCheckpointReader.MaxCheckpointRowGroupDecodedBytes);
        Assert.Equal(DeltaCheckpointReader.MaxCheckpointPartDecodedBytes, derivedPartDecoded);

        // The checkpoint DOOR footprint (BoundedDecode.CheckpointMaxFootprintBytes) is the isolated buffered part
        // copy (≤ MaxCheckpointPartBytes) PLUS the cumulative per-part decoded arrays (≤ MaxCheckpointPartDecoded
        // Bytes). Pin that too so the two doors' literal footprint cannot drift from the reader ceilings it
        // mirrors (the BoundedDecode literal is held locally to avoid cross-type static-init order dependence).
        Assert.Equal(
            BoundedDecode.CheckpointMaxFootprintBytes,
            DeltaCheckpointReader.MaxCheckpointPartBytes + DeltaCheckpointReader.MaxCheckpointPartDecodedBytes);
    }
}
