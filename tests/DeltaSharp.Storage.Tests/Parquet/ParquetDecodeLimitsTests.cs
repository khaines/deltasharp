using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;
using Xunit.Abstractions;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// The eager-decode ceiling and decompression ratio are operator-configurable via
/// <see cref="ParquetDecodeLimits"/> (#473): an operator can lower the ceiling below a constrained
/// executor budget or raise it for a trusted large-row-group workload, while the safe 4&#160;GiB / 1000:1
/// defaults hold when unset. These tests pin the configurability, the fail-fast validation, and that a
/// custom limit is threaded end-to-end through <see cref="ParquetFileReader"/>.
/// </summary>
public sealed class ParquetDecodeLimitsTests
{
    private readonly SeededRandom _random;

    public ParquetDecodeLimitsTests(ITestOutputHelper output) => _random = SeededRandom.Create(output);

    private static IReadOnlyList<ParquetFileReader.ColumnChunkFootprint> Footprint(
        long compressed, long uncompressed, int elementBytes) =>
        [new ParquetFileReader.ColumnChunkFootprint(compressed, uncompressed, elementBytes)];

    [Fact]
    public void Default_UsesSafeConstants()
    {
        Assert.Equal(4L * 1024 * 1024 * 1024, ParquetDecodeLimits.Default.MaxRowGroupDecodedBytes);
        Assert.Equal(1000, ParquetDecodeLimits.Default.MaxDecompressionRatio);
        Assert.Equal(ParquetFileReader.MaxRowGroupDecodedBytes, ParquetDecodeLimits.Default.MaxRowGroupDecodedBytes);
        Assert.Equal(ParquetFileReader.MaxDecompressionRatio, ParquetDecodeLimits.Default.MaxDecompressionRatio);
    }

    [Fact]
    public void CustomLowerCeiling_RejectsWhatDefaultAccepts()
    {
        IReadOnlyList<ParquetFileReader.ColumnChunkFootprint> footprint = Footprint(2000, 2000, 8);

        // Default (4 GiB) accepts a 2000-byte row group; a lowered 1000-byte ceiling rejects it.
        ParquetFileReader.EnsureDecodeCeiling(rowCount: 1, footprint, group: 0, ParquetDecodeLimits.Default);
        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(() =>
            ParquetFileReader.EnsureDecodeCeiling(rowCount: 1, footprint, group: 0, new ParquetDecodeLimits(maxRowGroupDecodedBytes: 1000)));
        Assert.Contains("1000-byte eager-decode ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomHigherCeiling_AcceptsWhatDefaultRejects()
    {
        // A 5 GiB row group exceeds the 4 GiB default but is accepted under an 8 GiB ceiling (trusted workload).
        IReadOnlyList<ParquetFileReader.ColumnChunkFootprint> footprint = Footprint(5L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, 8);

        Assert.Throws<DeltaStorageException>(() =>
            ParquetFileReader.EnsureDecodeCeiling(rowCount: 1, footprint, group: 0, ParquetDecodeLimits.Default));
        ParquetFileReader.EnsureDecodeCeiling(rowCount: 1, footprint, group: 0, new ParquetDecodeLimits(maxRowGroupDecodedBytes: 8L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void CustomLowerRatio_RejectsWhatDefaultAccepts()
    {
        // 10 compressed → 5000 decompressed = 500:1: under the 1000:1 default, over a lowered 100:1 ceiling.
        IReadOnlyList<ParquetFileReader.ColumnChunkFootprint> footprint = Footprint(10, 5000, 8);

        ParquetFileReader.EnsureDecodeCeiling(rowCount: 1, footprint, group: 0, ParquetDecodeLimits.Default);
        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(() =>
            ParquetFileReader.EnsureDecodeCeiling(rowCount: 1, footprint, group: 0, new ParquetDecodeLimits(maxDecompressionRatio: 100)));
        Assert.Contains("100:1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomHigherRatio_AcceptsWhatDefaultRejects()
    {
        // 10 compressed → 20000 decompressed = 2000:1: over the 1000:1 default, under a raised 5000:1 ceiling
        // (trusted workload). The absolute-size and row-count branches stay slack, so only the ratio branch decides.
        IReadOnlyList<ParquetFileReader.ColumnChunkFootprint> footprint = Footprint(10, 20000, 8);

        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(() =>
            ParquetFileReader.EnsureDecodeCeiling(rowCount: 1, footprint, group: 0, ParquetDecodeLimits.Default));
        Assert.Contains("1000:1", ex.Message, StringComparison.Ordinal);
        ParquetFileReader.EnsureDecodeCeiling(rowCount: 1, footprint, group: 0, new ParquetDecodeLimits(maxDecompressionRatio: 5000));
    }

    [Fact]
    public void RatioGuard_LargeCompressedSize_DoesNotOverflow()
    {
        // Overflow-safety of the decompression-ratio check: a chunk with a huge declared COMPRESSED size and
        // a tiny decompressed payload is NOT a ratio bomb (ratio << 1) and must be accepted. The check widens
        // the compressed×ratio product to Int128 so it never wraps a 64-bit multiply into a spurious verdict.
        // Pre-fix, `long compressedFloor * MaxDecompressionRatio` overflowed (here to a negative product),
        // flipping the comparison and wrongly rejecting this legitimate chunk — the same wrap can, for other
        // crafted sizes, land on a positive product and wrongly ACCEPT (a false negative in a decode-bomb guard).
        IReadOnlyList<ParquetFileReader.ColumnChunkFootprint> footprint = Footprint(9_223_372_036_854_776L, 1000, 8);

        // Must not throw: well under every ceiling and not a ratio bomb.
        ParquetFileReader.EnsureDecodeCeiling(rowCount: 1, footprint, group: 0, ParquetDecodeLimits.Default);
    }

    [Fact]
    public void RatioGuard_RealBomb_StillRejected()
    {
        // The overflow fix widens the product; it must not relax detection. A genuine ratio bomb (1000 bytes
        // compressed → 2 GiB decompressed ≈ 2.1M:1) is still rejected by the ratio ceiling.
        IReadOnlyList<ParquetFileReader.ColumnChunkFootprint> footprint = Footprint(1000, 2L * 1024 * 1024 * 1024, 8);

        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(() =>
            ParquetFileReader.EnsureDecodeCeiling(rowCount: 1, footprint, group: 0, ParquetDecodeLimits.Default));
        Assert.Contains("decompression-ratio ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureDecodeCeiling_NullLimits_UsesDefault()
    {
        // The default-parameter path keeps existing call sites (and the default ceiling) working.
        ParquetFileReader.EnsureDecodeCeiling(rowCount: 1, Footprint(2000, 2000, 8), group: 0);
        Assert.Throws<DeltaStorageException>(() =>
            ParquetFileReader.EnsureDecodeCeiling(rowCount: 1, Footprint(5L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, 8), group: 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveCeiling(long ceiling)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParquetDecodeLimits(maxRowGroupDecodedBytes: ceiling));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_RejectsRatioBelowOne(long ratio)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParquetDecodeLimits(maxDecompressionRatio: ratio));
    }

    [Fact]
    public async Task Reader_ThreadsCustomCeiling_EndToEnd()
    {
        var schema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch batch = TestData.RandomBatch(schema, rowCount: 64, _random);
        using var write = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(write, schema, new[] { batch }, CancellationToken.None);
        byte[] parquet = write.ToArray();

        // A 1-byte ceiling rejects any non-empty row group → proves the reader enforces its configured limit.
        var tiny = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 1));
        await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch _ in tiny.ReadAsync(new MemoryStream(parquet), schema, keepRowGroup: null, CancellationToken.None))
            {
            }
        });

        // The same file reads cleanly under the default limits.
        var read = new List<ColumnBatch>();
        await foreach (ColumnBatch b in new ParquetFileReader().ReadAsync(new MemoryStream(parquet), schema, keepRowGroup: null, CancellationToken.None))
        {
            read.Add(b);
        }

        Assert.Equal(64, read.Single().LogicalRowCount);
    }
}
