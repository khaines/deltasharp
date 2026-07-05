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
    public async Task StagedWrite_PublishesAtomicallyOnComplete()
    {
        const string key = "part-00000.parquet";
        byte[] payload = Encoding.UTF8.GetBytes("durable-content");

        Stream write = await _backend.OpenWriteAsync(key, CancellationToken.None);
        await using (write.ConfigureAwait(false))
        {
            await write.WriteAsync(payload, CancellationToken.None);

            // Publish-on-complete: the destination is NOT visible until CompleteAsync is invoked.
            Assert.Null(await _backend.HeadAsync(key, CancellationToken.None));

            await ((ICompletableWriteStream)write).CompleteAsync(CancellationToken.None);
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
    public async Task StagedWrite_AbandonedWithoutComplete_LeavesNoDestinationOrTemp()
    {
        const string key = "abandoned/part.parquet";

        Stream write = await _backend.OpenWriteAsync(key, CancellationToken.None);
        await using (write.ConfigureAwait(false))
        {
            await write.WriteAsync(Encoding.UTF8.GetBytes("half-written"), CancellationToken.None);
            // Dispose WITHOUT completing (faulted/abandoned) — must publish nothing.
        }

        Assert.Null(await _backend.HeadAsync(key, CancellationToken.None));

        // No orphan temp files remain under the root.
        string[] leftovers = Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.DoesNotContain(leftovers, f => f.Contains(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StagedWrite_PublishOntoExistingDestination_ThrowsAlreadyExists()
    {
        const string key = "existing.parquet";
        Assert.True(await _backend.PutIfAbsentAsync(key, new byte[] { 1, 2, 3 }, CancellationToken.None));

        Stream write = await _backend.OpenWriteAsync(key, CancellationToken.None);
        await using (write.ConfigureAwait(false))
        {
            await write.WriteAsync(new byte[] { 9, 9, 9 }, CancellationToken.None);
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                async () => await ((ICompletableWriteStream)write).CompleteAsync(CancellationToken.None));
            Assert.Equal(StorageErrorKind.AlreadyExists, error.Kind);
        }

        // The original content is untouched (no overwrite).
        await using Stream stored = await _backend.OpenReadAsync(key, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer.ToArray());
    }

    [Fact]
    public async Task PutIfAbsent_CanceledBeforePublish_LeavesNoDestination()
    {
        const string key = "commits/00000000000000000005.json";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await _backend.PutIfAbsentAsync(key, new byte[] { 1, 2, 3, 4 }, cts.Token));

        // The commit slot must NOT be poisoned by a canceled winner — no dest, no orphan temp.
        Assert.Null(await _backend.HeadAsync(key, CancellationToken.None));
        string[] leftovers = Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.DoesNotContain(leftovers, f => f.Contains(".tmp", StringComparison.Ordinal));

        // A later, uncanceled writer can still claim the slot.
        Assert.True(await _backend.PutIfAbsentAsync(key, new byte[] { 5 }, CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("nested/../../escape.bin")]
    public async Task PathConfinement_EnforcedOnEveryBackendMethod(string escape)
    {
        await AssertNotConfinedAsync(() => _backend.PutIfAbsentAsync(escape, new byte[] { 0 }, CancellationToken.None).AsTask());
        await AssertNotConfinedAsync(() => _backend.OpenReadAsync(escape, CancellationToken.None).AsTask());
        await AssertNotConfinedAsync(() => _backend.OpenWriteAsync(escape, CancellationToken.None).AsTask());
        await AssertNotConfinedAsync(() => _backend.ReadRangeAsync(escape, 0, 1, CancellationToken.None).AsTask());
        await AssertNotConfinedAsync(() => _backend.DeleteAsync(escape, CancellationToken.None).AsTask());
        await AssertNotConfinedAsync(() => _backend.HeadAsync(escape, CancellationToken.None).AsTask());
        await AssertNotConfinedAsync(async () =>
        {
            await foreach (StorageObjectInfo _ in _backend.ListAsync(escape, CancellationToken.None))
            {
            }
        });
    }

    private static async Task AssertNotConfinedAsync(Func<Task> action)
    {
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(action);
        Assert.Equal(StorageErrorKind.PathNotConfined, error.Kind);
    }

    [Fact]
    public async Task Symlink_EscapingRoot_IsRejected()
    {
        // A symlink INSIDE the root whose real target escapes the root must be rejected (M4), closing
        // the lexical-confinement gap where "safe/link.bin" canonicalizes outside the table root.
        string outsideDir = Path.Combine(Path.GetDirectoryName(_root)!, $"{Path.GetFileName(_root)}-outside");
        Directory.CreateDirectory(outsideDir);
        string outsideFile = Path.Combine(outsideDir, "secret.bin");
        await File.WriteAllBytesAsync(outsideFile, new byte[] { 42 });

        Directory.CreateDirectory(_root);
        string linkPath = Path.Combine(_root, "escape-link.bin");
        try
        {
            File.CreateSymbolicLink(linkPath, outsideFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Symlink creation is unprivileged on this platform/run — the guard cannot be exercised here.
            Directory.Delete(outsideDir, recursive: true);
            return;
        }

        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                async () => await _backend.OpenReadAsync("escape-link.bin", CancellationToken.None));
            Assert.Equal(StorageErrorKind.PathNotConfined, error.Kind);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
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

    [Fact]
    public async Task List_DoesNotLeakEntriesUnderSymlinkedAncestorDirectory()
    {
        // CF-2: a symlinked DIRECTORY inside the root pointing outside it. Directory.GetFiles(...,
        // AllDirectories) follows it and yields the out-of-root file with a NON-reparse leaf, so only the
        // real-target ancestor check catches it. The listing must not leak the out-of-root metadata
        // (design §5.5 "no cross-tenant listing").
        string outsideDir = Path.Combine(Path.GetDirectoryName(_root)!, $"{Path.GetFileName(_root)}-outside");
        Directory.CreateDirectory(outsideDir);
        await File.WriteAllBytesAsync(Path.Combine(outsideDir, "secret.bin"), new byte[] { 7, 7, 7 });

        await _backend.PutIfAbsentAsync("inside.bin", new byte[] { 1 }, CancellationToken.None);

        string dirLink = Path.Combine(_root, "dirlink");
        try
        {
            Directory.CreateSymbolicLink(dirLink, outsideDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Symlink creation is unprivileged on this platform/run — the guard cannot be exercised here.
            Directory.Delete(outsideDir, recursive: true);
            return;
        }

        try
        {
            var paths = new List<string>();
            await foreach (StorageObjectInfo info in _backend.ListAsync(string.Empty, CancellationToken.None))
            {
                paths.Add(info.Path);
            }

            Assert.Contains("inside.bin", paths);
            Assert.DoesNotContain(paths, p => p.Contains("secret", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(dirLink, recursive: false);
            }
            catch (IOException)
            {
                // Best-effort teardown of the symlink.
            }

            try
            {
                Directory.Delete(outsideDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort teardown.
            }
        }
    }

    [Fact]
    public async Task DirectoryFsyncFailure_OnPutIfAbsent_SurfacesRetryUnsafeAmbiguous()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows journals directory metadata; DirectoryFsync is a no-op and the hook is not consulted.
            return;
        }

        // CF-3: force the commit-path directory fsync to fail. A publish we cannot make durable is an
        // ambiguous outcome the caller must re-resolve — never a silently-successful commit.
        DirectoryFsync.FsyncHook = static _ => 1;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                async () => await _backend.PutIfAbsentAsync("commit.bin", new byte[] { 1 }, CancellationToken.None));
            Assert.Equal(StorageErrorKind.RetryUnsafeAmbiguous, error.Kind);
        }
        finally
        {
            DirectoryFsync.FsyncHook = null;
        }
    }

    [Fact]
    public async Task CommitDurabilityOrder_IsFileFsyncThenPublishThenDirFsync()
    {
        // CF-3: prove the durability ordering — the staged bytes are fsync'd, THEN atomically published,
        // THEN the directory entry is fsync'd. A reordering (e.g. publish before the data fsync) would
        // let a crash expose a name whose bytes are not yet durable.
        var steps = new List<string>();
        LocalFileSystemBackend.CommitStepProbe = step =>
        {
            lock (steps)
            {
                steps.Add(step);
            }
        };
        try
        {
            bool won = await _backend.PutIfAbsentAsync("ordered.bin", new byte[] { 1, 2, 3 }, CancellationToken.None);
            Assert.True(won);
        }
        finally
        {
            LocalFileSystemBackend.CommitStepProbe = null;
        }

        Assert.Equal(new[] { "file-fsync", "publish", "dir-fsync" }, steps);
    }

    [Fact]
    public void CreateFreshTemp_RetriesOnCollision_WithoutDeletingForeignTemp()
    {
        // CF-4: a foreign in-flight temp already owns the name the first ordinal would pick. The staging
        // temp must retry with a FRESH ordinal and NEVER delete the foreign temp (which would be a mutual
        // commit DoS between two writer pods sharing a PID namespace on an RWX PVC).
        Directory.CreateDirectory(_root);
        string dest = Path.Combine(_root, "obj.bin");
        string foreign = LocalFileSystemBackend.BuildTempName(dest, 5, ".tmp");
        File.WriteAllBytes(foreign, new byte[] { 9 });

        var ordinals = new Queue<long>(new long[] { 5, 6 });
        using (FileStream created = LocalFileSystemBackend.CreateFreshTempFrom(
            dest, ".tmp", () => ordinals.Dequeue(), out string tempPath))
        {
            Assert.Equal(LocalFileSystemBackend.BuildTempName(dest, 6, ".tmp"), tempPath);
            Assert.NotEqual(foreign, tempPath);
        }

        Assert.True(File.Exists(foreign));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(foreign));
    }

    [Fact]
    public void BuildTempName_IncludesSanitizedMachineNameForCrossPodUniqueness()
    {
        // CF-4: the temp name embeds the (sanitized) pod hostname so two pods with identical PIDs on a
        // shared PVC cannot generate identical temp names.
        string dest = Path.Combine(_root, "obj.bin");
        string name = LocalFileSystemBackend.BuildTempName(dest, 1, ".tmp");

        Assert.Contains($".{SanitizeHostForTest(Environment.MachineName)}.", name);
        Assert.StartsWith(dest + ".", name);
        Assert.EndsWith(".tmp", name);
    }

    private static string SanitizeHostForTest(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return "host";
        }

        int length = Math.Min(host.Length, 64);
        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            char c = host[i];
            bool safe = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9') || c == '-';
            chars[i] = safe ? c : '-';
        }

        return new string(chars);
    }
}
