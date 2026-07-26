using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Efficacy tests that prove the read-path <b>access</b> guarantees, not just round-trip parity:
/// projection reads only the requested chunks (poisoned non-projected bytes are never touched),
/// row-group pruning truly skips a group (poisoned pruned bytes are never decoded), a mid-stream
/// corrupt row group surfaces a deterministic error <i>after</i> a complete earlier batch and never a
/// torn one (H3), and the decode ceiling fails closed on an implausible row count (H4).
/// </summary>
public sealed class ParquetCorruptionTests
{
    private static readonly StructField KeepField = new("keep", DataTypes.LongType, nullable: false);
    private static readonly StructField PoisonField = new("poison", DataTypes.LongType, nullable: false);

    private static ColumnBatch BuildLongBatch(StructType schema, params long[][] columns)
    {
        int rows = columns[0].Length;
        var vectors = new ColumnVector[columns.Length];
        for (int c = 0; c < columns.Length; c++)
        {
            MutableColumnVector v = ColumnVectors.Create(DataTypes.LongType, rows);
            foreach (long value in columns[c])
            {
                v.AppendValue(value);
            }

            vectors[c] = v;
        }

        return new ManagedColumnBatch(schema, vectors, rows);
    }

    [Fact]
    public async Task Projection_NeverReadsPoisonedNonProjectedColumn()
    {
        var schema = new StructType(new[] { KeepField, PoisonField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 4 }, new long[] { 10, 20, 30, 40 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });

        // Poison ONLY the non-projected "poison" column chunk (column index 1) in the single row group.
        byte[] poisoned = await ParquetTestHelpers.PoisonColumnChunkAsync(file, rowGroup: 0, columnIndex: 1);

        // Projecting just "keep" must succeed — the poisoned chunk is never read.
        var projection = new StructType(new[] { KeepField });
        List<ColumnBatch> result = await ParquetTestHelpers.ReadAllAsync(poisoned, projection);

        ColumnBatch only = Assert.Single(result);
        ColumnVector keep = only.SelectedColumn(0);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, Enumerable.Range(0, 4).Select(i => keep.GetValue<long>(i)));

        // Control: reading the poisoned column (full schema) DOES surface a deterministic corruption
        // error, proving the poison is real and that projection genuinely avoided it.
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(poisoned, schema));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task RowGroupPruning_NeverDecodesPoisonedPrunedGroup()
    {
        var schema = new StructType(new[] { KeepField });
        // Two row groups: [1,2,3] then [100,101,102].
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 100, 101, 102 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }, rowGroupRowLimit: 3);

        // Poison group 0's only column chunk — a predicate that prunes group 0 must still succeed.
        byte[] poisoned = await ParquetTestHelpers.PoisonColumnChunkAsync(file, rowGroup: 0, columnIndex: 0);

        List<ColumnBatch> result = await ParquetTestHelpers.ReadAllAsync(
            poisoned, schema, keepRowGroup: stats => stats.Max("keep") is long max && max >= 100);

        ColumnBatch only = Assert.Single(result);
        ColumnVector keep = only.SelectedColumn(0);
        Assert.Equal(new long[] { 100, 101, 102 }, Enumerable.Range(0, 3).Select(i => keep.GetValue<long>(i)));
    }

    [Fact]
    public async Task RowGroupPruning_OnCorruptColumnStatistics_FailsClosedNotRawBcl()
    {
        // Council R1 Security HIGH: the row-group-statistics decode is an UNTRUSTED-byte boundary. A valid
        // Parquet file whose footer column-statistics blob is corrupt (a too-short MaxValue) OPENS cleanly, but
        // Parquet.Net's eager typed min/max decode throws a raw ArgumentException while reading it. On the
        // predicate-pushdown pruning path that decode is reached by RowGroupStatistics.GetStatistics — and that
        // prologue used to run OUTSIDE ReadRowGroupAsync's fail-closed try, so a NON-NULL keepRowGroup read
        // LEAKED the raw BCL exception (Security probe: CONSTRUCT-ONLY rawEscapes=1). It must now map to a
        // deterministic CorruptData (storage-delta-architecture.md §5.4 C-DECODE / ADR-0013; PDX-T lying stats).
        // Regression check: reverting the prologue wrap makes the NON-NULL-predicate assertion below go RED.
        var schema = new StructType(new[] { KeepField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 4, 5 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] corruptStats =
            await ParquetTestHelpers.ForgeShortColumnStatisticsAsync(file, rowGroup: 0, columnIndex: 0);

        // A NON-NULL pruning predicate forces the RowGroupStatistics construction that decodes the corrupt blob;
        // the raw ArgumentException must surface as a deterministic CorruptData, never escape raw to the caller.
        DeltaStorageException pruning = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(
                corruptStats, schema, keepRowGroup: stats => stats.Max("keep") is long max && max >= 0));
        Assert.Equal(StorageErrorKind.CorruptData, pruning.Kind);

        // Control (the CDF door path, keepRowGroup:null): SKIPS RowGroupStatistics — but Parquet.Net ALSO reads
        // the same column statistics WITHIN the normal column read (ReadColumnStatistics under ReadAsync<T>), so
        // this file fails closed here TOO. Crucially that data-read decode was ALWAYS inside the fail-closed try,
        // so the door NEVER leaked raw (Security probe: NO-PREDICATE rawEscapes=0) — it maps to CorruptData, not
        // an ArgumentException. So the ONLY pre-fix gap was the pruning-path construction (isolated by the
        // regression check). Both untrusted-stats-decode paths must now yield the SAME deterministic contract.
        DeltaStorageException door = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(corruptStats, schema));
        Assert.Equal(StorageErrorKind.CorruptData, door.Kind);
    }

    [Fact]
    public async Task GetRowCount_OnOverflowingRowGroupCounts_FailsClosedNotRawOverflow()
    {
        // Red-team HIGH (a FOURTH untrusted-byte decode site): GetRowCountAsync sums attacker-controlled footer
        // NumRows via checked(total + rows). A crafted file whose per-row-group counts sum past long.MaxValue
        // raises a raw OverflowException that USED TO escape — this metadata-only entry point had NO fail-closed
        // try, unlike ReadRowGroupAsync. It must now map to a deterministic CorruptData (storage-delta-
        // architecture.md §5.4 C-DECODE / ADR-0013). Regression check: removing the wrap makes THIS test throw a
        // raw OverflowException instead of DeltaStorageException.
        var schema = new StructType(new[] { KeepField });
        // Two row groups (6 rows, limit 3). GetRowCountAsync reads ONLY the footer NumRows, so the physical data
        // pages are irrelevant — forge EACH group's declared NumRows to long.MaxValue so their checked sum
        // (long.MaxValue + long.MaxValue) overflows on the second iteration.
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 100, 101, 102 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }, rowGroupRowLimit: 3);
        byte[] forged =
            await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(file, rowGroup: 0, forgedNumRows: long.MaxValue);
        forged = await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(forged, rowGroup: 1, forgedNumRows: long.MaxValue);

        using var stream = new MemoryStream(forged, writable: false);
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileReader().GetRowCountAsync(stream, CancellationToken.None));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task ReadDataSchema_OnEmptyFooterFieldName_FailsClosedNotRawArgument()
    {
        // Red-team HIGH (a FIFTH untrusted-byte decode site): ReadDataSchemaAsync maps footer field descriptors
        // into DeltaSharp StructFields via ParquetTypeMapping.ToDataSchema. OpenAsync force-materializes
        // reader.Schema inside its footer-PARSE boundary, but the SUBSEQUENT mapping was unsealed — ToDataSchema
        // eagerly builds a StructField for EVERY footer field, so a crafted footer with an empty field name USED
        // TO raise a raw System.ArgumentException ("The value cannot be an empty string") that escaped to the
        // caller. It must now map to a deterministic CorruptData (crafted schema; storage-delta-architecture.md
        // §5.4 C-DECODE / ADR-0013). Regression check: removing the wrap makes THIS test throw a raw
        // ArgumentException instead of DeltaStorageException.
        //
        // NOTE the decorrelated sibling ReadAsync path is NOT vulnerable to this vector: it uses footer field
        // names as dictionary KEYS (an empty name is a valid key) and reports resolution failures as a TYPED
        // DeltaStorageException (ColumnNotPresentInFile), never a raw exception — only ToDataSchema eagerly
        // constructs a name-validating StructField from every footer field, which is why only this entry leaked.
        var schema = new StructType(new[] { KeepField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] forged = await ParquetTestHelpers.ForgeFieldNameAsync(file, "keep", "");

        using var stream = new MemoryStream(forged, writable: false);
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileReader().ReadDataSchemaAsync(stream, CancellationToken.None));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task MidStreamCorruption_YieldsCompleteEarlierBatchThenDeterministicError()
    {
        var schema = new StructType(new[] { KeepField });
        // Two row groups: [0,1,2] then [3,4,5].
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 0, 1, 2, 3, 4, 5 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }, rowGroupRowLimit: 3);

        // Corrupt row group 1's data page; row group 0 must remain fully readable.
        byte[] poisoned = await ParquetTestHelpers.PoisonColumnChunkAsync(file, rowGroup: 1, columnIndex: 0);

        using var stream = new MemoryStream(poisoned, writable: false);
        IAsyncEnumerator<ColumnBatch> enumerator = new ParquetFileReader()
            .ReadAsync(stream, schema, keepRowGroup: null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None)
            .GetAsyncEnumerator();

        try
        {
            // Row group 0 is returned COMPLETE (never torn).
            Assert.True(await enumerator.MoveNextAsync());
            ColumnVector group0 = enumerator.Current.SelectedColumn(0);
            Assert.Equal(3, enumerator.Current.LogicalRowCount);
            Assert.Equal(new long[] { 0, 1, 2 }, Enumerable.Range(0, 3).Select(i => group0.GetValue<long>(i)));

            // Advancing into the corrupt row group throws a deterministic CorruptData — no partial batch.
            DeltaStorageException error =
                await Assert.ThrowsAsync<DeltaStorageException>(async () => await enumerator.MoveNextAsync());
            Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    // ---- CF-1: decode ceiling (design §5.4 C-DECODE) --------------------------------------------

    private static IReadOnlyList<ParquetFileReader.ColumnChunkFootprint> Footprints(
        params ParquetFileReader.ColumnChunkFootprint[] chunks) => chunks;

    [Fact]
    public void DecodeCeiling_RejectsImplausibleDecompressionRatio()
    {
        // A chunk claiming >1000x more decompressed than compressed bytes is a decompression bomb.
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.EnsureDecodeCeiling(
                rowCount: 8,
                Footprints(new ParquetFileReader.ColumnChunkFootprint(
                    CompressedBytes: 100, UncompressedBytes: (100 * 1000) + 1, ElementBytes: 8)),
                group: 0));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public void DecodeCeiling_RejectsAbsoluteDecompressedSize()
    {
        // Within the ratio ceiling, but the absolute decompressed size (5 GiB) blows the memory bound.
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.EnsureDecodeCeiling(
                rowCount: 8,
                Footprints(new ParquetFileReader.ColumnChunkFootprint(
                    CompressedBytes: 5_000_000, UncompressedBytes: 5_000_000_000L, ElementBytes: 8)),
                group: 0));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public void DecodeCeiling_RejectsImplausibleRowCount()
    {
        // A billion rows for a physically tiny chunk would eagerly materialize past the memory bound.
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.EnsureDecodeCeiling(
                rowCount: 1_000_000_000,
                Footprints(new ParquetFileReader.ColumnChunkFootprint(
                    CompressedBytes: 100, UncompressedBytes: 200, ElementBytes: 8)),
                group: 0));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public void DecodeCeiling_RejectsNegativeRowCount()
    {
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.EnsureDecodeCeiling(
                rowCount: -1,
                Array.Empty<ParquetFileReader.ColumnChunkFootprint>(),
                group: 0));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public void DecodeCeiling_RejectsNegativeChunkSize()
    {
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.EnsureDecodeCeiling(
                rowCount: 8,
                Footprints(new ParquetFileReader.ColumnChunkFootprint(
                    CompressedBytes: -1, UncompressedBytes: 10, ElementBytes: 8)),
                group: 0));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public void DecodeCeiling_AllowsLegitimatelyCompressibleRowGroup()
    {
        // Real measured footprints for a 131072-row constant-bool chunk plus an all-null long chunk
        // (SNAPPY): a high logical-rows-to-byte density the old rows/byte proxy false-rejected, but sound
        // under the ratio + absolute-size + row-plausibility controls. Must NOT throw.
        ParquetFileReader.EnsureDecodeCeiling(
            rowCount: 131072,
            Footprints(
                new ParquetFileReader.ColumnChunkFootprint(
                    CompressedBytes: 799, UncompressedBytes: 16410, ElementBytes: 1),
                new ParquetFileReader.ColumnChunkFootprint(
                    CompressedBytes: 75, UncompressedBytes: 73, ElementBytes: 8)),
            group: 0);
    }

    [Fact]
    public void DecodeCeiling_RowCountBound_AccountsForNullableElementWidth()
    {
        // RF-4a: a nullable long column reads into new long?[] (16B/element), not long[] (8B). A row count
        // that fits under the 4 GiB eager-decode cap at the unwrapped 8B width but EXCEEDS it at the true
        // 16B nullable width must be rejected — otherwise the real transient is ~2x the cap. Non-vacuous:
        // reverting AllocatedElementByteWidth to the unwrapped size collapses nullableWidth to plainWidth
        // and the first assertion (and the rejection) reddens.
        int nullableWidth = ParquetFileReader.AllocatedElementByteWidth(DataTypes.LongType, nullable: true);
        int plainWidth = ParquetFileReader.AllocatedElementByteWidth(DataTypes.LongType, nullable: false);
        Assert.True(nullableWidth > plainWidth, "long? must allocate wider than long");

        // A row count in the gap: over the nullable-width bound, still under the unwrapped-width bound.
        long rowCount = (ParquetFileReader.MaxRowGroupDecodedBytes / nullableWidth) + 1;
        Assert.True(rowCount <= ParquetFileReader.MaxRowGroupDecodedBytes / plainWidth);

        // The bound with the TRUE nullable width rejects it.
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.EnsureDecodeCeiling(
                rowCount,
                Footprints(new ParquetFileReader.ColumnChunkFootprint(
                    CompressedBytes: 100, UncompressedBytes: 200, ElementBytes: nullableWidth)),
                group: 0));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);

        // Positive control: at the UNWRAPPED width the SAME row count is NOT rejected — proving the
        // nullable accounting is precisely what catches it (the test is not vacuous by rejecting all).
        ParquetFileReader.EnsureDecodeCeiling(
            rowCount,
            Footprints(new ParquetFileReader.ColumnChunkFootprint(
                CompressedBytes: 100, UncompressedBytes: 200, ElementBytes: plainWidth)),
            group: 0);
    }

    [Fact]
    public void DecodeCeiling_RejectsFootprintZeroChunk_MetadataStrippedFooter()
    {
        // §5.4 footprint-0 guard: a stripped footer declaring ZERO decompressed bytes while the chunk has
        // real compressed pages (which Parquet.Net would still decode by offset) is rejected — the declared
        // ceiling cannot bound it. Non-vacuous: the guard is the only control that fires (ratio/absolute/
        // row-count all pass a zero-uncompressed chunk).
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.EnsureDecodeCeiling(
                rowCount: 1024,
                Footprints(new ParquetFileReader.ColumnChunkFootprint(
                    CompressedBytes: 32, UncompressedBytes: 0, ElementBytes: 8)),
                group: 0));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("zero decompressed bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeCeiling_RejectsFootprintZeroChunk_FullyAbsentMetadata()
    {
        // An absent/missing-metadata chunk (both sizes zero) for a non-empty row group is likewise rejected
        // fail-closed rather than passed through as a "harmless" zero footprint.
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.EnsureDecodeCeiling(
                rowCount: 8,
                Footprints(new ParquetFileReader.ColumnChunkFootprint(
                    CompressedBytes: 0, UncompressedBytes: 0, ElementBytes: 8)),
                group: 0));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public void DecodeCeiling_AllowsFootprintZero_WhenRowGroupIsEmpty()
    {
        // A genuinely empty row group (zero rows) has nothing to decode, so a zero footprint is fine — the
        // guard is scoped to rowCount > 0 and must NOT reject an empty group.
        ParquetFileReader.EnsureDecodeCeiling(
            rowCount: 0,
            Footprints(new ParquetFileReader.ColumnChunkFootprint(
                CompressedBytes: 0, UncompressedBytes: 0, ElementBytes: 8)),
            group: 0);
    }

    [Fact]
    public void DecodeCeiling_RejectsFootprintZeroChunk_AmongNonZeroProjectedColumns()
    {
        // The guard is inside the per-chunk loop, so a zero-footprint chunk mixed among legitimate
        // non-zero columns still rejects (the whole row group fails closed, not just an isolated chunk).
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.EnsureDecodeCeiling(
                rowCount: 1024,
                Footprints(
                    new ParquetFileReader.ColumnChunkFootprint(
                        CompressedBytes: 500, UncompressedBytes: 4000, ElementBytes: 8),
                    new ParquetFileReader.ColumnChunkFootprint(
                        CompressedBytes: 32, UncompressedBytes: 0, ElementBytes: 8),
                    new ParquetFileReader.ColumnChunkFootprint(
                        CompressedBytes: 500, UncompressedBytes: 4000, ElementBytes: 8)),
                group: 0));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("zero decompressed bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsParquetDefect_MapsEagerAllocationFailuresToCorruptData()
    {
        // RF-4b/ADR-0013: an OutOfMemoryException or OverflowException from the eager decode allocation is
        // classified as a decode defect (→ CorruptData), never escaping raw. Non-vacuous: removing either
        // type from IsParquetDefect flips its assertion to false.
        Assert.True(ParquetFileReader.IsParquetDefect(new OutOfMemoryException()));
        Assert.True(ParquetFileReader.IsParquetDefect(new OverflowException()));

        // A genuine logic bug in our own decode path still surfaces as itself (not masked as corruption).
        Assert.False(ParquetFileReader.IsParquetDefect(new InvalidOperationException()));
        Assert.False(ParquetFileReader.IsParquetDefect(new ArgumentException()));
    }

    [Fact]
    public void IsUndecodableParquetInput_FailsClosedOnUnboundedLibraryFaults()
    {
        // storage-delta-architecture.md §5.4 (C-DECODE) / #193 increment 4: at the THREE decode boundaries
        // where Parquet.Net consumes UNTRUSTED bytes (OpenAsync footer parse; ReadRowGroupAsync's row-group
        // prologue incl. statistics/pruning; ReadRowGroupAsync page/level decode) the library empirically raises
        // an UNBOUNDED set of raw BCL types on a malformed file (the CDF cdc-file fuzz drove all of the below),
        // so the boundary must fail closed on every fault EXCEPT cooperative cancellation and DeltaSharp's own
        // typed storage exception. This predicate is the fail-closed SUPERSET of IsParquetDefect.

        // (1) The broad BCL family IsParquetDefect deliberately EXCLUDES — the fuzz proved the library decoder
        // raises these on corrupt data, so the boundary MUST still fail closed on them.
        Assert.True(ParquetFileReader.IsUndecodableParquetInput(new IndexOutOfRangeException()));
        Assert.True(ParquetFileReader.IsUndecodableParquetInput(new ArgumentException()));
        Assert.True(ParquetFileReader.IsUndecodableParquetInput(new ArgumentOutOfRangeException()));
        Assert.True(ParquetFileReader.IsUndecodableParquetInput(new InvalidOperationException()));
        Assert.True(ParquetFileReader.IsUndecodableParquetInput(new NotSupportedException()));
        Assert.True(ParquetFileReader.IsUndecodableParquetInput(new FormatException()));
        Assert.True(ParquetFileReader.IsUndecodableParquetInput(new NullReferenceException()));

        // (2) Superset property: every type IsParquetDefect matches is also undecodable input here.
        Assert.True(ParquetFileReader.IsUndecodableParquetInput(new OutOfMemoryException()));
        Assert.True(ParquetFileReader.IsUndecodableParquetInput(new OverflowException()));
        Assert.True(ParquetFileReader.IsUndecodableParquetInput(new IOException()));
        Assert.True(ParquetFileReader.IsUndecodableParquetInput(new EndOfStreamException()));

        // (3) Cooperative cancellation is control flow — it must PROPAGATE, never be masked as corruption.
        Assert.False(ParquetFileReader.IsUndecodableParquetInput(new OperationCanceledException()));
        Assert.False(ParquetFileReader.IsUndecodableParquetInput(new TaskCanceledException()));

        // (4) DeltaSharp's OWN typed fail-closed signal must propagate UNWRAPPED — an unsupported but VALID
        // feature stays UnsupportedFeature, and an inner CorruptData is not re-masked.
        Assert.False(ParquetFileReader.IsUndecodableParquetInput(DeltaStorageException.UnsupportedFeature("x")));
        Assert.False(ParquetFileReader.IsUndecodableParquetInput(DeltaStorageException.CorruptData("x")));
    }

    [Fact]
    public async Task LegitimateCompressibleFile_RoundTripsThroughReadAsync()
    {
        // A constant bool column and an all-null long column filling a full default row group (131072
        // rows): legitimately compressible data that the pre-fix rows/byte decode ceiling false-rejected.
        // Written through the DEFAULT writer and read back through ReadAsync — every value must survive.
        const int rows = 131072;
        var schema = new StructType(new[]
        {
            new StructField("flag", DataTypes.BooleanType, nullable: false),
            new StructField("value", DataTypes.LongType, nullable: true),
        });

        MutableColumnVector flag = ColumnVectors.Create(DataTypes.BooleanType, rows);
        MutableColumnVector value = ColumnVectors.Create(DataTypes.LongType, rows);
        for (int i = 0; i < rows; i++)
        {
            flag.AppendValue(true);
            value.AppendNull();
        }

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { flag, value }, rows);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });

        List<ColumnBatch> read = await ParquetTestHelpers.ReadAllAsync(file, schema);
        int seen = 0;
        foreach (ColumnBatch group in read)
        {
            ColumnVector flags = group.SelectedColumn(0);
            ColumnVector values = group.SelectedColumn(1);
            for (int r = 0; r < group.LogicalRowCount; r++)
            {
                Assert.False(flags.IsNull(r));
                Assert.True(flags.GetValue<bool>(r));
                Assert.True(values.IsNull(r));
                seen++;
            }
        }

        Assert.Equal(rows, seen);
    }

    [Fact]
    public async Task DecodeBomb_ViaReadAsync_IsRejected()
    {
        // A physically tiny file whose footer is forged to declare a 100 GB decompressed column chunk:
        // ReadAsync must reject it as CorruptData at the decode ceiling — never attempt the allocation.
        var schema = new StructType(new[] { new StructField("value", DataTypes.LongType, nullable: false) });
        MutableColumnVector value = ColumnVectors.Create(DataTypes.LongType, 8);
        for (long i = 0; i < 8; i++)
        {
            value.AppendValue(i);
        }

        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(
            schema, new[] { new ManagedColumnBatch(schema, new ColumnVector[] { value }, 8) });
        byte[] forged = await ParquetTestHelpers.ForgeColumnUncompressedSizeAsync(
            file, rowGroup: 0, columnIndex: 0, inflatedUncompressedSize: 100_000_000_000L);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(forged, schema));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    // ---- #649: classify library-rejected VALID features (Parquet Modular Encryption) as UnsupportedFeature,
    // ---- not CorruptData — WITHOUT mislabeling genuine corruption (the precision boundary) ---------------
    //
    // The Parquet fail-closed boundaries map every non-cancellation, non-typed library fault to CorruptData:
    // correct for genuine corruption, but it MIS-LABELS a VALID Parquet file the LIBRARY (not DeltaSharp)
    // refuses for using an unimplemented feature. The clearest such case is Parquet Modular Encryption: an
    // encrypted-footer file is bracketed by the 'PARE' magic (vs plaintext 'PAR1'), which Parquet.Net 6.0.3
    // rejects at open as "not a parquet file, head: 50415245, tail: 50415245" — a message shape byte-for-byte
    // IDENTICAL to the one it emits for arbitrary garbage ("head: 74686973…"). So the reader peeks the file's
    // own leading MAGIC BYTES (never ex.Message) to reclassify only a 'PARE' head as an actionable
    // UnsupportedFeature. The precision boundary is load-bearing and pinned below: genuine corruption (garbage,
    // a 'PAR1'-magic garbage footer, a bit-flipped page, or a NotSupportedException from an invalid codec) must
    // STILL map to CorruptData.

    [Fact]
    public async Task EncryptedFooter_ThroughReadAsync_IsUnsupportedFeatureNotCorruptData()
    {
        // A valid-but-unsupported encrypted-footer file ('PARE' magic). ReadAsync must classify it as an
        // actionable UnsupportedFeature ("it's encrypted"), NOT a misleading CorruptData ("it's broken").
        // RED-on-revert: removing the OpenAsync 'PARE'-magic peek makes the library IOException fall to the
        // superset default and this assertion flips to CorruptData.
        byte[] encrypted = ParquetTestHelpers.EncryptedFooterMagicFile();
        var schema = new StructType(new[] { KeepField });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(encrypted, schema));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase); // "Encryption"/"encrypted"
        // The diagnosis is the feature, never the fail-closed "malformed/corrupt" default.
        Assert.DoesNotContain("malformed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EncryptedFooter_ThroughReadDataSchemaAsync_IsUnsupportedFeature()
    {
        // The same 'PARE' file through the footer-only schema door. ReadDataSchemaAsync also funnels through
        // OpenAsync, so the single magic-peek classifier covers it uniformly: UnsupportedFeature, not
        // CorruptData.
        byte[] encrypted = ParquetTestHelpers.EncryptedFooterMagicFile();
        using var stream = new MemoryStream(encrypted, writable: false);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileReader().ReadDataSchemaAsync(stream, CancellationToken.None));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EncryptedFooter_ThroughGetRowCountAsync_IsUnsupportedFeature()
    {
        // And through the row-count door (which bounds a deletion vector's positions) — a third public entry
        // that goes through OpenAsync — proving the ONE OpenAsync classifier covers every entry point.
        byte[] encrypted = ParquetTestHelpers.EncryptedFooterMagicFile();
        using var stream = new MemoryStream(encrypted, writable: false);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileReader().GetRowCountAsync(stream, CancellationToken.None));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
    }

    // ---- #655: plaintext-footer Parquet Modular Encryption (mode b — ordinary 'PAR1' magic) --------------
    //
    // Encrypted-FOOTER mode (above) is caught by the 'PARE' magic peek in the OpenAsync FAILURE path.
    // Plaintext-FOOTER mode is the format's SECOND encryption mode: the file keeps the ordinary 'PAR1' magic
    // and its footer parses cleanly, carrying encryption_algorithm (and/or per-column ColumnCryptoMetaData)
    // while the column chunks are encrypted. It therefore opens SUCCESSFULLY, so it is classified on the
    // OpenAsync SUCCESS path via IsPlaintextFooterEncrypted(reader.Metadata). Fixtures are synthesized by
    // splicing the crypto Thrift fields into a normal file's footer (Parquet.Net 6.0.3 parses them but cannot
    // decrypt) — no library upgrade required (#655).

    [Fact]
    public async Task PlaintextFooterEncryption_ThroughReadAsync_IsUnsupportedFeatureNotCorruptData()
    {
        // A plaintext-footer encrypted file (encryption_algorithm present, 'PAR1' magic). ReadAsync must
        // classify it as an actionable UnsupportedFeature, NOT a misleading CorruptData or a garbage read.
        // RED-on-revert: removing the OpenAsync IsPlaintextFooterEncrypted check lets the reader open the file
        // and read its (plaintext) pages, so this Assert.ThrowsAsync would fail (no exception thrown).
        var schema = new StructType(new[] { KeepField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 4, 5 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterEncryptedFileAsync(file);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(encrypted, schema));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
        // The diagnosis is the feature, never the fail-closed "malformed/corrupt" default.
        Assert.DoesNotContain("malformed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaintextFooterEncryption_ThroughReadDataSchemaAsync_IsUnsupportedFeature()
    {
        // The footer-only schema door funnels through the SAME OpenAsync classifier — UnsupportedFeature, not
        // CorruptData — proving one success-path check covers every entry point (mirrors the 'PARE' coverage).
        var schema = new StructType(new[] { KeepField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 7, 8, 9 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterEncryptedFileAsync(file);
        using var stream = new MemoryStream(encrypted, writable: false);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileReader().ReadDataSchemaAsync(stream, CancellationToken.None));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaintextFooterEncryption_ThroughGetRowCountAsync_IsUnsupportedFeature()
    {
        // And through the row-count door — the third OpenAsync entry point.
        var schema = new StructType(new[] { KeepField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 4 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterEncryptedFileAsync(file);
        using var stream = new MemoryStream(encrypted, writable: false);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileReader().GetRowCountAsync(stream, CancellationToken.None));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
    }

    [Fact]
    public async Task PlaintextFooterColumnCrypto_OnlySubsetOfColumnsEncrypted_IsUnsupportedFeature()
    {
        // Edge case: a plaintext-footer file may encrypt only SOME columns — the file-level
        // encryption_algorithm is UNSET, but a single column chunk carries ColumnCryptoMetaData. The
        // per-column arm of IsPlaintextFooterEncrypted must still classify it UnsupportedFeature.
        var schema = new StructType(new[] { KeepField, PoisonField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3 }, new long[] { 4, 5, 6 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterColumnCryptoFileAsync(file, rowGroup: 0, columnIndex: 1);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(encrypted, schema));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NormalFile_WithoutEncryptionMetadata_ReadsNormally_NoFalsePositive()
    {
        // Precision guard: a normal 'PAR1' file with NO crypto footer metadata must NOT false-positive — it
        // reads its data normally. This proves IsPlaintextFooterEncrypted keys on the presence of the crypto
        // fields, not on merely opening successfully. (The same normal file is the fixture base for the
        // encrypted cases above, so this pins that only the spliced crypto field flips the classification.)
        var schema = new StructType(new[] { KeepField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 10, 11, 12, 13 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });

        List<ColumnBatch> result = await ParquetTestHelpers.ReadAllAsync(file, schema);

        ColumnBatch only = Assert.Single(result);
        ColumnVector keep = only.SelectedColumn(0);
        Assert.Equal(new long[] { 10, 11, 12, 13 }, Enumerable.Range(0, 4).Select(i => keep.GetValue<long>(i)));
    }

    // ---- #655: the REALISTIC plaintext-footer shape (encrypted column OMITS plaintext ColumnMetaData) -----
    //
    // A real encrypting writer stores an encrypted column's ColumnMetaData ENCRYPTED, so the plaintext
    // `meta_data` (Thrift field 3) is absent. Parquet.Net 6.0.3 dereferences it unguarded during
    // CreateAsync's row-group-reader init, throwing an NRE BEFORE the success-path reader.Metadata check runs.
    // These files therefore reach the OpenAsync FAILURE path and are classified by the reflection-free footer
    // probe (which reads the file-level encryption_algorithm the format spec sets on every mode-b file). Prior
    // to the failure-path probe they misclassified as CorruptData — so each of these is RED-on-revert against
    // the probe.

    [Fact]
    public async Task PlaintextFooterEncryption_EncryptedColumnMetaDataOmitted_ThroughReadAsync_IsUnsupportedFeature()
    {
        // The shape a genuine encryptor produces: file-level encryption_algorithm + per-column crypto_metadata,
        // and the encrypted column's plaintext meta_data OMITTED (→ Parquet.Net NREs in CreateAsync). The
        // failure-path footer probe must reclassify it UnsupportedFeature, not CorruptData.
        var schema = new StructType(new[] { KeepField, PoisonField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3 }, new long[] { 4, 5, 6 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterEncryptedRealisticFileAsync(file, rowGroup: 0, columnIndex: 1);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(encrypted, schema));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("malformed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaintextFooterEncryption_EncryptedColumnMetaDataOmitted_ThroughReadDataSchemaAsync_IsUnsupportedFeature()
    {
        var schema = new StructType(new[] { KeepField, PoisonField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3 }, new long[] { 4, 5, 6 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterEncryptedRealisticFileAsync(file, rowGroup: 0, columnIndex: 1);
        using var stream = new MemoryStream(encrypted, writable: false);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileReader().ReadDataSchemaAsync(stream, CancellationToken.None));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("ncrypt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaintextFooterEncryption_EncryptedColumnMetaDataOmitted_ThroughGetRowCountAsync_IsUnsupportedFeature()
    {
        var schema = new StructType(new[] { KeepField, PoisonField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3 }, new long[] { 4, 5, 6 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterEncryptedRealisticFileAsync(file, rowGroup: 0, columnIndex: 1);
        using var stream = new MemoryStream(encrypted, writable: false);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileReader().GetRowCountAsync(stream, CancellationToken.None));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
    }

    [Fact]
    public async Task Par1FileWithBitFlippedFooterLength_StaysCorruptData_ProbePrecisionGuard()
    {
        // PRECISION GUARD for the footer probe: a genuinely corrupt PAR1 file whose trailing 4-byte footer
        // length is bit-flipped to a bogus value opens-fails in Parquet.Net, and the probe — reading that
        // bogus length — must NOT over-trigger. It stays CorruptData (the probe is fail-closed on a footer
        // length it cannot use).
        var schema = new StructType(new[] { KeepField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] corrupt = (byte[])file.Clone();
        // Corrupt the 4-byte little-endian footer length that sits just before the trailing 'PAR1' magic.
        corrupt[^8] ^= 0xFF;
        corrupt[^7] ^= 0xFF;

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(corrupt, schema));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task MalformedFooterWithField8StructHeaderButTruncatedBody_StaysCorruptData_ProbePrecisionGuard()
    {
        // Red-team R2 precision guard: a malformed PAR1 file whose 1-byte "footer" is exactly a Thrift field-8
        // STRUCT header (0x8C = field delta 8 | type STRUCT=12) with NO struct body/STOP must NOT be mislabeled
        // encryption. The footer probe requires the WHOLE footer to parse cleanly to its STOP (validating the
        // field-8 struct body), so a truncated field-8 struct fails the walk and stays the fail-closed
        // CorruptData default. RED-on-revert against an early-return-on-field-8-header probe.
        byte[] malformed =
        [
            0x50, 0x41, 0x52, 0x31, // 'PAR1' head
            0x8C,                   // footer content: field 8 (delta 8), type STRUCT — but no body/STOP
            0x01, 0x00, 0x00, 0x00, // footer_length = 1 (little-endian) => the footer is just the 0x8C byte
            0x50, 0x41, 0x52, 0x31, // 'PAR1' tail magic
        ];
        var schema = new StructType(new[] { KeepField });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(malformed, schema));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task WellFormedFooterButNotAFileMetaData_WithEmptyField8_StaysCorruptData_ProbePrecisionGuard()
    {
        // Red-team R3 precision guard: a footer that is SYNTACTICALLY valid Thrift but is NOT a plausible
        // FileMetaData — just an EMPTY field-8 struct then the top-level STOP ([0x8C, 0x00, 0x00]) — must stay
        // CorruptData. It lacks the FileMetaData required fields (version/schema/num_rows/row_groups) and its
        // encryption_algorithm union is empty (no member), so the probe rejects it. RED-on-revert against a
        // probe that trusts a bare field-8-struct header without required-field / non-empty-union validation.
        byte[] malformed =
        [
            0x50, 0x41, 0x52, 0x31, // 'PAR1' head
            0x8C, 0x00, 0x00,       // footer: field 8 STRUCT, empty body (0x00 = struct STOP), 0x00 = top STOP
            0x03, 0x00, 0x00, 0x00, // footer_length = 3 (little-endian)
            0x50, 0x41, 0x52, 0x31, // 'PAR1' tail magic
        ];
        var schema = new StructType(new[] { KeepField });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(malformed, schema));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task GarbageInput_StaysCorruptData_PrecisionGuard()
    {
        // PRECISION GUARD: non-Parquet garbage trips the SAME "not a parquet file, head: …" library
        // IOException as an encrypted file, but its leading magic is NOT 'PARE', so it must stay CorruptData
        // through both the data door and the schema door.
        byte[] garbage = "this is not a parquet file"u8.ToArray();
        var schema = new StructType(new[] { KeepField });

        DeltaStorageException read = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(garbage, schema));
        Assert.Equal(StorageErrorKind.CorruptData, read.Kind);

        using var stream = new MemoryStream(garbage, writable: false);
        DeltaStorageException schemaRead = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileReader().ReadDataSchemaAsync(stream, CancellationToken.None));
        Assert.Equal(StorageErrorKind.CorruptData, schemaRead.Kind);
    }

    [Fact]
    public async Task Par1MagicGarbageFooter_StaysCorruptData_PrecisionGuard()
    {
        // PRECISION GUARD (the tightest one): a corrupt file that fails at the SAME boundary as the encrypted
        // file (OpenAsync), differing ONLY in its magic — a 'PAR1' head with a garbage footer body →
        // ThriftProtocolException. The classifier keys on 'PARE', so this stays CorruptData: it proves the
        // OpenAsync peek does not over-trigger on an arbitrary non-'PARE' open failure.
        byte[] corrupt = ParquetTestHelpers.Par1MagicGarbageFooterFile();
        var schema = new StructType(new[] { KeepField });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(corrupt, schema));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task BitFlippedValidParquetPage_StaysCorruptData_PrecisionGuard()
    {
        // PRECISION GUARD: a valid file whose interior column-chunk bytes are bit-flipped (footer + 'PAR1'
        // magic intact) opens cleanly then fails during page decode — genuine corruption that must stay
        // CorruptData (the leading magic is 'PAR1', never 'PARE').
        var schema = new StructType(new[] { KeepField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 4, 5 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] poisoned = await ParquetTestHelpers.PoisonColumnChunkAsync(file, rowGroup: 0, columnIndex: 0);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(poisoned, schema));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task BitFlippedRowGroupPage_CorruptDataMessage_DoesNotEchoUnderlyingBytes()
    {
        // #653 info-leak parity: the ReadRowGroupAsync IsParquetDefect catch used to interpolate the raw
        // decode exception's Message ("… row group {group}: {ex.Message}") into the surfaced CorruptData
        // string — a crafted page can carry bytes into ex.Message. It must surface a FIXED message (the cause
        // is preserved as the inner exception), mirroring the #651 checkpoint-reader fix. Poisoning the whole
        // column chunk corrupts the page-header Thrift → an IsParquetDefect fault; exact-equality proves no
        // ex.Message interpolation.
        var schema = new StructType(new[] { KeepField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 4, 5 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] poisoned = await ParquetTestHelpers.PoisonColumnChunkAsync(file, rowGroup: 0, columnIndex: 0);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(poisoned, schema));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Equal("Failed to decode Parquet row group 0.", error.Message);
        Assert.NotNull(error.InnerException);   // the raw decode cause is preserved, just not surfaced in text
    }

    [Fact]
    public async Task OutOfRangeDate_CorruptDataMessage_DoesNotEchoFileColumnName()
    {
        // #653 info-leak parity — a decode site the PR MISSED: ReadValueAsync<T>'s DATE/TIMESTAMP-range catch
        // used to interpolate fileField.Name, the FILE's physical column name. Under column-mapping id mode
        // that name is resolved by field_id from the untrusted footer verbatim (attacker-authored for a
        // foreign/untrusted table), and it surfaces UNWRAPPED through the public read facade. It must surface a
        // FIXED message naming NEITHER the column NOR the value (the cause is preserved as the inner exception),
        // exactly like the sibling row-group decode fault above.
        //
        // Forge the fault: DeltaSharp's writer is bounded by DateTime and cannot emit an out-of-range date, so
        // write a plain INT32 "birthdate" column holding int.MaxValue (2,147,483,647 days since epoch — far
        // beyond DateTime.MaxValue), then annotate that column's footer schema element as a logical DATE.
        // Reading it as a DateType column drives Parquet.Net's INT32-DATE → DateTime decode
        // (epoch.AddDays(int.MaxValue)) → ArgumentOutOfRangeException → the reader's date/time-range catch.
        var writeSchema = new StructType(new[] { new StructField("birthdate", DataTypes.IntegerType, nullable: true) });
        MutableColumnVector days = ColumnVectors.Create(DataTypes.IntegerType, 1);
        days.AppendValue(int.MaxValue);
        var batch = new ManagedColumnBatch(writeSchema, new ColumnVector[] { days }, 1);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });
        byte[] forged = await ParquetTestHelpers.ForgeColumnConvertedTypeToDateAsync(file, rowGroup: 0, columnIndex: 0);

        var readSchema = new StructType(new[] { new StructField("birthdate", DataTypes.DateType, nullable: true) });
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(forged, readSchema));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        // Exact-equality proves the message carries no file-derived token (neither the column name nor a value).
        Assert.Equal(
            "A Parquet column holds a physical value outside the representable date/time range.", error.Message);
        Assert.DoesNotContain("birthdate", error.Message, StringComparison.Ordinal);   // no file-derived column name
        Assert.NotNull(error.InnerException);   // the raw decode cause is preserved, just not surfaced in text
    }

    [Fact]
    public async Task UnsupportedFooterColumnType_FailsClosedWithoutEchoingFileColumnName()
    {
        // Message hygiene (#653): ParquetTypeMapping.ToDataType dropped the FILE footer field.Name from its
        // UnsupportedFeature message. A footer column's physical CLR type AND its NAME are file-derived — for a
        // foreign/untrusted table the name is attacker-authored (resolved by field-id from the untrusted
        // footer) — and ReadDataSchemaAsync surfaces this typed UnsupportedFeature UNWRAPPED through the public
        // read facade (both its catch predicates exclude DeltaStorageException, so it is never re-masked as
        // CorruptData). So the mapping-failure message must name the unsupported physical TYPE yet NEVER the
        // column name. Author a Parquet whose one column is a physical TimeSpan (no DeltaSharp type mapping)
        // named a sentinel — a type DeltaSharp's own writer cannot emit — then map its footer schema.
        const string sentinel = "att4cker_footer_col_s3ntinel";
        byte[] file = await ParquetTestHelpers.WriteUnmappedTimeSpanColumnAsync(sentinel);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileReader().ReadDataSchemaAsync(new MemoryStream(file), CancellationToken.None));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.DoesNotContain(sentinel, error.Message, StringComparison.Ordinal);   // no file-derived column name
        // Pins the scrubbed ToDataType site: it names the unsupported physical CLR TYPE, never the column name.
        Assert.Contains("physical CLR type 'TimeSpan'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgedInvalidCompressionCodec_NotSupportedException_StaysCorruptData()
    {
        // PRECISION GUARD for the NotSupportedException family (#649). Forging an OUT-OF-RANGE compression
        // codec (9, not a real CompressionCodec) yields a file that OPENS cleanly (valid footer + 'PAR1'
        // magic) yet raises a raw System.NotSupportedException ("Compression method 9 is not supported.")
        // during page decode. An empirical fuzz proved random bit-flips raise this SAME exception on genuinely
        // corrupt pages (invalid codec / page-type / logical-type codes), so it is NOT separable from
        // corruption — reclassifying it to UnsupportedFeature would MISLABEL corruption. It must stay
        // CorruptData. This also documents why the "library NotSupported on a VALID file" case is not
        // separately triggerable with Parquet.Net 6.0.3: the library neither reads nor writes such an encoding,
        // so no valid-but-unsupported-encoding fixture is constructible — every NotSupportedException reachable
        // in the page decode is corruption.
        var schema = new StructType(new[] { KeepField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 4, 5 });
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        byte[] forged = await ParquetTestHelpers.ForgeColumnCompressionCodecAsync(
            file, rowGroup: 0, columnIndex: 0, forgedCodec: 9);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(forged, schema));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task PareHeadWithNonPareTail_StaysCorruptData_PrecisionGuard()
    {
        // PRECISION GUARD (#649, council R1): a complete encrypted-footer file is bracketed by 'PARE' at BOTH
        // ends. A file with a 'PARE' HEAD but a non-'PARE' tail ('GARB') is genuinely corrupt (an
        // incomplete/mangled encrypted file), NOT a valid-but-unsupported one — so it must stay CorruptData,
        // not be mislabeled UnsupportedFeature. This pins the head-AND-tail requirement through the read door.
        byte[] corrupt = ParquetTestHelpers.PareHeadOnlyFile();
        var schema = new StructType(new[] { KeepField });

        DeltaStorageException read = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(corrupt, schema));
        Assert.Equal(StorageErrorKind.CorruptData, read.Kind);

        using var stream = new MemoryStream(corrupt, writable: false);
        DeltaStorageException schemaRead = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileReader().ReadDataSchemaAsync(stream, CancellationToken.None));
        Assert.Equal(StorageErrorKind.CorruptData, schemaRead.Kind);
    }

    [Fact]
    public async Task PareHeadTruncated_StaysCorruptData_PrecisionGuard()
    {
        // PRECISION GUARD (#649, council R1): a 'PARE'-prefixed input truncated to just the leading magic is
        // too short to be bracketed by a trailing 'PARE' — genuinely corrupt, must stay CorruptData.
        byte[] truncated = ParquetTestHelpers.PareHeadTruncatedFile();
        var schema = new StructType(new[] { KeepField });

        DeltaStorageException read = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(truncated, schema));
        Assert.Equal(StorageErrorKind.CorruptData, read.Kind);
    }

    [Fact]
    public void IsParquetEncryptedFooterMagic_RequiresPareAtBothEnds_AndIsTransparent()
    {
        // Unit-level proof the discriminator keys on the actual MAGIC BYTES at BOTH ends (robust), not on
        // ex.Message: only a file bracketed by 'PARE' head AND tail is detected; a 'PARE' head with a
        // non-'PARE' tail, a 'PARE'-only truncated file, a 'PAR1' head (even with a garbage footer), arbitrary
        // garbage, and a too-short input are NOT — so each keeps the CorruptData default. This pins the
        // precision boundary (head-and-tail) at the classifier itself.
        using (var pare = new MemoryStream(ParquetTestHelpers.EncryptedFooterMagicFile()))
        {
            Assert.True(ParquetFileReader.IsParquetEncryptedFooterMagic(pare));
        }

        using (var pareHeadOnly = new MemoryStream(ParquetTestHelpers.PareHeadOnlyFile()))
        {
            Assert.False(ParquetFileReader.IsParquetEncryptedFooterMagic(pareHeadOnly)); // non-'PARE' tail
        }

        using (var pareTruncated = new MemoryStream(ParquetTestHelpers.PareHeadTruncatedFile()))
        {
            Assert.False(ParquetFileReader.IsParquetEncryptedFooterMagic(pareTruncated)); // < 8 bytes
        }

        using (var par1 = new MemoryStream(ParquetTestHelpers.Par1MagicGarbageFooterFile()))
        {
            Assert.False(ParquetFileReader.IsParquetEncryptedFooterMagic(par1));
        }

        using (var garbage = new MemoryStream("this is not a parquet file"u8.ToArray()))
        {
            Assert.False(ParquetFileReader.IsParquetEncryptedFooterMagic(garbage));
        }

        using (var tooShort = new MemoryStream(new byte[] { 0x50, 0x41 })) // "PA" — fewer than 4 magic bytes
        {
            Assert.False(ParquetFileReader.IsParquetEncryptedFooterMagic(tooShort));
        }

        // The peek is TRANSPARENT: it seeks to read the head and tail, then restores the caller's position, so
        // a fully-bracketed 'PARE' file is still found when the stream is positioned mid-file and the caller's
        // position survives.
        using (var pareMidPosition = new MemoryStream(ParquetTestHelpers.EncryptedFooterMagicFile()))
        {
            pareMidPosition.Position = 3;
            Assert.True(ParquetFileReader.IsParquetEncryptedFooterMagic(pareMidPosition));
            Assert.Equal(3, pareMidPosition.Position);
        }
    }
}
