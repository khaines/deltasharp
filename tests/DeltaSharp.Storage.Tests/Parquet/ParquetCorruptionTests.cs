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

    [Fact]
    public void DecodeCeiling_RejectsImplausibleRowCount()
    {
        // A footer claiming a billion rows for a 100-byte stream is a decompression bomb, not a file.
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.EnsureDecodeCeiling(rowCount: 1_000_000_000, streamLength: 100, group: 0));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public void DecodeCeiling_RejectsNegativeRowCount()
    {
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetFileReader.EnsureDecodeCeiling(rowCount: -1, streamLength: 100, group: 0));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public void DecodeCeiling_AllowsPlausibleRowCountAndUnknownLength()
    {
        // Well within the ceiling.
        ParquetFileReader.EnsureDecodeCeiling(rowCount: 50, streamLength: 100, group: 0);
        // Unknown length (non-seekable stream) disables the ratio check.
        ParquetFileReader.EnsureDecodeCeiling(rowCount: long.MaxValue, streamLength: -1, group: 0);
    }
}
