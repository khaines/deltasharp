using System.Text;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using Xunit;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Behavioral tests for <see cref="LocalFileSystemBackend"/>: the single-winner conditional-create
/// primitive under concurrency, log-resolved-path confinement (§5.5), idempotent delete, range-read
/// correctness, and the staged write→fsync→rename durability sequence.
/// </summary>
public sealed class LocalFileSystemBackendTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public LocalFileSystemBackendTests()
    {
        long ordinal = System.Threading.Interlocked.Increment(ref _counter);
        _root = Path.Combine(
            AppContext.BaseDirectory,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"storage-test-{Environment.ProcessId}-{ordinal}"));
        _backend = new LocalFileSystemBackend(_root);
    }

    private static long _counter;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leaked temp directory must not fail the suite.
        }
    }

    [Fact]
    public async Task PutIfAbsent_ExactlyOneWinnerUnderConcurrency()
    {
        const int racers = 16;
        const string key = "commits/00000000000000000000.json";
        using var barrier = new Barrier(racers);
        var results = new bool[racers];
        var contents = new byte[racers][];

        var tasks = new Task[racers];
        for (int i = 0; i < racers; i++)
        {
            int index = i;
            contents[index] = Encoding.UTF8.GetBytes($"writer-{index}");
            tasks[index] = Task.Run(async () =>
            {
                // Ensure every racer reaches the CreateNew call together.
                barrier.SignalAndWait();
                results[index] = await _backend.PutIfAbsentAsync(key, contents[index], CancellationToken.None);
            });
        }

        await Task.WhenAll(tasks);

        int winners = results.Count(won => won);
        Assert.Equal(1, winners);

        int winnerIndex = Array.FindIndex(results, won => won);
        await using Stream stored = await _backend.OpenReadAsync(key, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer);
        Assert.Equal(contents[winnerIndex], buffer.ToArray());
    }

    [Fact]
    public async Task PutIfAbsent_SecondCallReturnsFalse()
    {
        const string key = "once.bin";
        Assert.True(await _backend.PutIfAbsentAsync(key, new byte[] { 1, 2, 3 }, CancellationToken.None));
        Assert.False(await _backend.PutIfAbsentAsync(key, new byte[] { 9, 9, 9 }, CancellationToken.None));

        await using Stream stored = await _backend.OpenReadAsync(key, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer.ToArray());
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("nested/../../escape.bin")]
    public async Task PathEscape_TraversalRejected(string path)
    {
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            async () => await _backend.PutIfAbsentAsync(path, new byte[] { 0 }, CancellationToken.None));
        Assert.Equal(StorageErrorKind.PathNotConfined, error.Kind);
    }

    [Fact]
    public async Task PathEscape_AbsoluteOutsideRootRejected()
    {
        string outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside-root.bin");
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            async () => await _backend.PutIfAbsentAsync(outside, new byte[] { 0 }, CancellationToken.None));
        Assert.Equal(StorageErrorKind.PathNotConfined, error.Kind);
    }

    [Fact]
    public async Task Delete_IsIdempotent()
    {
        const string key = "data/file.bin";

        // Deleting a non-existent object is a no-op.
        await _backend.DeleteAsync(key, CancellationToken.None);

        await _backend.PutIfAbsentAsync(key, new byte[] { 7 }, CancellationToken.None);
        Assert.NotNull(await _backend.HeadAsync(key, CancellationToken.None));

        await _backend.DeleteAsync(key, CancellationToken.None);
        Assert.Null(await _backend.HeadAsync(key, CancellationToken.None));

        // Deleting again is still a no-op.
        await _backend.DeleteAsync(key, CancellationToken.None);
    }

    [Fact]
    public async Task ReadRange_ReturnsExactSlice()
    {
        const string key = "range.bin";
        var payload = new byte[64];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        await _backend.PutIfAbsentAsync(key, payload, CancellationToken.None);

        await using Stream slice = await _backend.ReadRangeAsync(key, offset: 10, length: 8, CancellationToken.None);
        using var buffer = new MemoryStream();
        await slice.CopyToAsync(buffer);
        Assert.Equal(payload.AsSpan(10, 8).ToArray(), buffer.ToArray());
    }

    [Fact]
    public async Task ReadRange_ClampsLengthToEndOfObject()
    {
        const string key = "clamp.bin";
        byte[] payload = Encoding.UTF8.GetBytes("hello world");
        await _backend.PutIfAbsentAsync(key, payload, CancellationToken.None);

        await using Stream slice = await _backend.ReadRangeAsync(key, offset: 6, length: 1000, CancellationToken.None);
        using var buffer = new MemoryStream();
        await slice.CopyToAsync(buffer);
        Assert.Equal(Encoding.UTF8.GetBytes("world"), buffer.ToArray());
    }

    [Fact]
    public async Task StagedWrite_PublishesAtomicallyOnClose()
    {
        const string key = "part-00000.parquet";
        byte[] payload = Encoding.UTF8.GetBytes("durable-content");

        Stream write = await _backend.OpenWriteAsync(key, CancellationToken.None);
        await using (write.ConfigureAwait(false))
        {
            await write.WriteAsync(payload, CancellationToken.None);
        }

        StorageObjectInfo? head = await _backend.HeadAsync(key, CancellationToken.None);
        Assert.NotNull(head);
        Assert.Equal(payload.Length, head.Length);

        await using Stream stored = await _backend.OpenReadAsync(key, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer);
        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task ReadRange_MissingObjectThrowsNotFound()
    {
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            async () => await _backend.ReadRangeAsync("absent.bin", 0, 1, CancellationToken.None));
        Assert.Equal(StorageErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task List_EnumeratesWrittenObjects()
    {
        await _backend.PutIfAbsentAsync("a/1.bin", new byte[] { 1 }, CancellationToken.None);
        await _backend.PutIfAbsentAsync("a/2.bin", new byte[] { 2 }, CancellationToken.None);
        await _backend.PutIfAbsentAsync("b/3.bin", new byte[] { 3 }, CancellationToken.None);

        var paths = new List<string>();
        await foreach (StorageObjectInfo info in _backend.ListAsync("a/", CancellationToken.None))
        {
            paths.Add(info.Path);
        }

        Assert.Contains("a/1.bin", paths);
        Assert.Contains("a/2.bin", paths);
        Assert.DoesNotContain("b/3.bin", paths);
    }
}
