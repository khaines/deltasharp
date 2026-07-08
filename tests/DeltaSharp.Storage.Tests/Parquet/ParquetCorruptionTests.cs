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
            .ReadAsync(stream, schema, keepRowGroup: null, CancellationToken.None)
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
}
