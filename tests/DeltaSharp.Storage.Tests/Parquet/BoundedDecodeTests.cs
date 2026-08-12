using System.Diagnostics;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Unit + integration coverage for the shared <see cref="BoundedDecode"/> wall-clock decode policy and its
/// wiring into the data-file door (design §5.4 C-DECODE, "fail deterministically … fail closed … never
/// hangs"; #647/#699/#716). Parquet.Net 6.0.3 can be driven by a single corrupted byte into unbounded,
/// cancellation-ignoring work; the policy converts that non-termination into a deterministic, typed
/// fail-closed exception within a bounded time.
/// </summary>
public sealed class BoundedDecodeTests
{
    // A generous test watchdog: the policy releases the caller at the (much smaller) budget, so this only trips
    // on a genuine regression (the policy failing to release the caller), converting it to a test failure.
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

    private static readonly StructType DataSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    [Fact]
    public async Task RunAsync_ReturnsResult_WhenWorkFinishesFirst()
    {
        int result = await BoundedDecode.RunAsync(
            _ => Task.FromResult(42),
            TimeSpan.FromSeconds(5),
            static _ => new InvalidOperationException("must not time out"),
            CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunAsync_PropagatesWorkException_Unwrapped_WhenWorkFinishesFirst()
    {
        // A typed fail-closed exception the work itself throws must propagate unwrapped — never remapped to the
        // timeout exception. This is the property that keeps UnsupportedFeature / CorruptData contracts intact.
        DeltaStorageException thrown = await Assert.ThrowsAsync<DeltaStorageException>(() =>
            BoundedDecode.RunAsync<int>(
                _ => throw DeltaStorageException.UnsupportedFeature("valid but unsupported"),
                TimeSpan.FromSeconds(5),
                static _ => DeltaStorageException.CorruptData("must not be surfaced"),
                CancellationToken.None));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, thrown.Kind);
        Assert.Equal("valid but unsupported", thrown.Message);
    }

    [Fact]
    public async Task RunAsync_ThrowsOnTimeout_WhenWorkIgnoresTokenAndNeverTerminatesInBudget()
    {
        // The work IGNORES its token and blocks past the budget (a detached decode the runtime cannot abort).
        // The caller must be released at the budget with the caller-supplied fail-closed exception, well under
        // the watchdog.
        var stopwatch = Stopwatch.StartNew();
        var thrown = await RunWatchdoggedAsync(() =>
            BoundedDecode.RunAsync<int>(
                _ =>
                {
                    Thread.Sleep(TimeSpan.FromSeconds(3)); // ignores the token entirely
                    return Task.FromResult(1);
                },
                TimeSpan.FromMilliseconds(200),
                static _ => DeltaStorageException.CorruptData("bounded-decode budget exceeded"),
                CancellationToken.None));
        stopwatch.Stop();

        DeltaStorageException ex = Assert.IsType<DeltaStorageException>(thrown);
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"expected release near the budget, took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_SurfacesCallerCancellation_NotTimeout()
    {
        // Caller cancellation is control flow: it must surface OperationCanceledException, NEVER be masked as
        // the timeout exception — even though the work ignores the token and the budget is long.
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var thrown = await RunWatchdoggedAsync(() =>
            BoundedDecode.RunAsync<int>(
                _ =>
                {
                    Thread.Sleep(TimeSpan.FromSeconds(3));
                    return Task.FromResult(1);
                },
                TimeSpan.FromSeconds(30),
                static _ => new InvalidOperationException("timeout must not win over caller cancellation"),
                cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
    }

    [Fact]
    public void ParquetDecodeLimits_RejectsNonPositiveDecodeBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParquetDecodeLimits(decodeTimeBudget: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParquetDecodeLimits(decodeTimeBudget: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void ParquetDecodeLimits_DefaultDecodeBudget_IsTheSharedDefault()
    {
        Assert.Equal(BoundedDecode.DefaultBudget, ParquetDecodeLimits.Default.DecodeTimeBudget);
    }

    [Fact]
    public async Task DataFileDoor_FailsClosedWithinBudget_OnLastFooterByteFlip()
    {
        // #699 on the DATA-FILE door. A single bit flip in the last footer byte (the terminal Thrift STOP of
        // FileMetaData, index len-9 before the 4-byte footer_length + PAR1 magic) drives ParquetReader
        // .CreateAsync into unbounded, token-ignoring work. The bounded-time open must fail closed with a typed
        // CorruptData within the budget rather than hanging the read.
        byte[] file = await BuildDataFileAsync();
        byte[] mutated = (byte[])file.Clone();
        mutated[^9] ^= 1;

        var reader = new ParquetFileReader(new ParquetDecodeLimits(decodeTimeBudget: TimeSpan.FromMilliseconds(300)));
        var stopwatch = Stopwatch.StartNew();
        var thrown = await RunWatchdoggedAsync(() => ReadAllAsync(reader, mutated));
        stopwatch.Stop();

        DeltaStorageException ex = Assert.IsType<DeltaStorageException>(thrown);
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"expected a fast fail-closed, took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task DataFileDoor_ReadsCleanly_UnderTheBoundedDecodePolicy()
    {
        // A well-formed file must still decode correctly with the policy in place (the budget wraps the open
        // and every row-group decode without changing the result on valid input).
        byte[] file = await BuildDataFileAsync();
        var reader = new ParquetFileReader(new ParquetDecodeLimits(decodeTimeBudget: TimeSpan.FromSeconds(5)));
        using var stream = new MemoryStream(file, writable: false);
        long total = 0;
        await foreach (ColumnBatch batch in reader.ReadAsync(
            stream, DataSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
        {
            total += batch.LogicalRowCount;
        }

        Assert.Equal(500, total);
    }

    private static async Task ReadAllAsync(ParquetFileReader reader, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await foreach (ColumnBatch batch in reader.ReadAsync(
            stream, DataSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
        {
            _ = batch.LogicalRowCount;
        }
    }

    // Runs the operation on the thread pool under the watchdog so a genuine non-termination fails the test
    // rather than stalling CI, and returns the exception it threw (null if it completed).
    private static async Task<Exception?> RunWatchdoggedAsync(Func<Task> operation)
    {
        Task<Exception?> run = Task.Run(async () =>
        {
            try
            {
                await operation();
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        if (await Task.WhenAny(run, Task.Delay(Watchdog)) != run)
        {
            Assert.Fail(
                $"The bounded operation did not terminate within {Watchdog.TotalSeconds:0}s — the bounded-decode "
                + "policy failed to release the caller (regression of #647/#699/#716).");
        }

        return await run;
    }

    private static async Task<byte[]> BuildDataFileAsync()
    {
        const int rows = 500;
        MutableColumnVector idVector = ColumnVectors.Create(DataTypes.LongType, rows);
        MutableColumnVector nameVector = ColumnVectors.Create(DataTypes.StringType, rows);
        for (int i = 0; i < rows; i++)
        {
            idVector.AppendValue((long)i);
            nameVector.AppendBytes(System.Text.Encoding.UTF8.GetBytes("row-" + (i % 37)));
        }

        var batch = new ManagedColumnBatch(DataSchema, new ColumnVector[] { idVector, nameVector }, rows);
        var writer = new ParquetFileWriter();
        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, DataSchema, new[] { batch }, CancellationToken.None);
        return stream.ToArray();
    }
}
