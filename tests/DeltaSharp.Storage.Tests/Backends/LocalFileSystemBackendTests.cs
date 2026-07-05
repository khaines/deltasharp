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
    public async Task PutIfAbsent_FlushesStagingFileToDiskBeforePublish()
    {
        // RF-5: the file-data durability flush is routed through FlushToDisk, whose observation IS the
        // flush. This test proves the staging bytes are flushed-to-disk strictly BEFORE the atomic
        // publish. Non-vacuous: deleting the FlushToDisk call removes both the real fsync and this
        // observation, so "file-flush" is never recorded and the ordering assertion reddens.
        var events = new List<string>();
        FileStream? flushedStream = null;
        LocalFileSystemBackend.FlushToDiskProbe = stream =>
        {
            lock (events)
            {
                events.Add("file-flush");
            }

            flushedStream = stream;
            stream.Flush(flushToDisk: true); // preserve production-faithful durability during the test
        };
        LocalFileSystemBackend.CommitStepProbe = step =>
        {
            lock (events)
            {
                events.Add(step);
            }
        };
        try
        {
            Assert.True(await _backend.PutIfAbsentAsync("flush.bin", new byte[] { 1, 2, 3 }, CancellationToken.None));
        }
        finally
        {
            LocalFileSystemBackend.FlushToDiskProbe = null;
            LocalFileSystemBackend.CommitStepProbe = null;
        }

        Assert.NotNull(flushedStream);
        int flushIndex = events.IndexOf("file-flush");
        int publishIndex = events.IndexOf("publish");
        Assert.True(flushIndex >= 0, "the staging file must be flushed-to-disk (FlushToDisk was not invoked)");
        Assert.True(publishIndex > flushIndex, "the staging file must be flushed-to-disk BEFORE the atomic publish");
    }

    [Fact]
    public async Task DirectoryFsyncFailure_OnStagedWrite_SurfacesRetryUnsafeAmbiguousAndLeavesNoOrphanTemp()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows journals directory metadata; DirectoryFsync is a no-op and the hook is not consulted.
            return;
        }

        // RF-2/F2b: force the staged-write commit path's directory fsync to fail. The publish (link)
        // already landed, so the outcome is RetryUnsafeAmbiguous — but the now-redundant temp alias must
        // still be cleaned up on the throw path (no orphan .tmp), and the destination stays published.
        const string key = "staged/part-00000.parquet";
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("staged-durable");

        DirectoryFsync.FsyncHook = static _ => 1;
        try
        {
            Stream write = await _backend.OpenWriteAsync(key, CancellationToken.None);
            await using (write.ConfigureAwait(false))
            {
                await write.WriteAsync(payload, CancellationToken.None);
                DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                    async () => await ((ICompletableWriteStream)write).CompleteAsync(CancellationToken.None));
                Assert.Equal(StorageErrorKind.RetryUnsafeAmbiguous, error.Kind);
            }
        }
        finally
        {
            DirectoryFsync.FsyncHook = null;
        }

        // The destination was published (link succeeded) even though its durability is unconfirmed.
        await using Stream stored = await _backend.OpenReadAsync(key, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer);
        Assert.Equal(payload, buffer.ToArray());

        // No orphan temp alias remains under the root.
        string[] leftovers = Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.DoesNotContain(leftovers, f => f.Contains(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectoryFsyncFailure_OnPutIfAbsent_LeavesNoOrphanTemp()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // RF-2/F2b: the PutIfAbsent dir-fsync-failure throw path must also drop its published temp alias.
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

        string[] leftovers = Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.DoesNotContain(leftovers, f => f.Contains(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_ReturnsMetadataFromConfinedRealFile()
    {
        // RF-1: a surfaced entry's metadata (Length/mtime/ETag) is read from the confinement-confirmed
        // real path, AFTER confinement — never from a pre-resolution path. Here the happy-path Length and
        // mtime must exactly match the on-disk object.
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("metadata-source");
        await _backend.PutIfAbsentAsync("meta/obj.bin", payload, CancellationToken.None);

        StorageObjectInfo? listed = null;
        await foreach (StorageObjectInfo info in _backend.ListAsync("meta/", CancellationToken.None))
        {
            listed = info;
        }

        Assert.NotNull(listed);
        Assert.Equal("meta/obj.bin", listed.Path);

        string realPath = new FileInfo(Path.Combine(_root, "meta", "obj.bin")).FullName;
        var actual = new FileInfo(realPath);
        Assert.Equal(actual.Length, listed.Length);
        Assert.Equal(payload.Length, listed.Length);
        Assert.Equal(actual.LastWriteTimeUtc, listed.LastModifiedUtc);
    }

    [Fact]
    public async Task List_ResolvesEachDirectoryPrefixOncePerDirectoryNotPerEntry()
    {
        // RF-1 perf: every file in a directory shares its ancestor chain, so the confinement
        // canonicalization must run ONCE per directory, not once per entry (a per-entry re-walk was an
        // O(depth) syscall storm on a networked PVC). Non-vacuous: reverting to a per-file re-resolution
        // fires the probe once per file, so the single-directory assertion reddens.
        await _backend.PutIfAbsentAsync("bulk/1.bin", new byte[] { 1 }, CancellationToken.None);
        await _backend.PutIfAbsentAsync("bulk/2.bin", new byte[] { 2 }, CancellationToken.None);
        await _backend.PutIfAbsentAsync("bulk/3.bin", new byte[] { 3 }, CancellationToken.None);

        var resolvedDirectories = new List<string>();
        LocalFileSystemBackend.ListDirectoryResolveProbe = dir =>
        {
            lock (resolvedDirectories)
            {
                resolvedDirectories.Add(dir);
            }
        };
        int listed = 0;
        try
        {
            await foreach (StorageObjectInfo _ in _backend.ListAsync("bulk/", CancellationToken.None))
            {
                listed++;
            }
        }
        finally
        {
            LocalFileSystemBackend.ListDirectoryResolveProbe = null;
        }

        Assert.Equal(3, listed); // all three files are surfaced
        Assert.Single(resolvedDirectories); // the shared directory prefix is canonicalized exactly once
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
    public async Task OpenWrite_FlushesDataFileToDiskBeforePublish()
    {
        // R4F-2 (Quality): the STAGED-WRITE data path (OpenWriteAsync -> CompleteAsync) must flush the
        // data file to disk BEFORE it is published, exactly like the conditional-create path. Non-vacuous:
        // the flush is routed through FlushToDisk whose observation IS the flush, so deleting
        // FlushToDisk(_inner) in CompleteAsync removes this observation and the ordering assertion reddens.
        var events = new List<string>();
        LocalFileSystemBackend.FlushToDiskProbe = stream =>
        {
            lock (events)
            {
                events.Add("file-flush");
            }

            stream.Flush(flushToDisk: true); // production-faithful durability during the test
        };
        LocalFileSystemBackend.CommitStepProbe = step =>
        {
            lock (events)
            {
                events.Add(step);
            }
        };
        try
        {
            Stream stream = await _backend.OpenWriteAsync("data/part-0.parquet", CancellationToken.None);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(new byte[] { 1, 2, 3, 4 });
                await ((ICompletableWriteStream)stream).CompleteAsync(CancellationToken.None);
            }
        }
        finally
        {
            LocalFileSystemBackend.FlushToDiskProbe = null;
            LocalFileSystemBackend.CommitStepProbe = null;
        }

        int flushIndex = events.IndexOf("file-flush");
        int publishIndex = events.IndexOf("publish");
        Assert.True(flushIndex >= 0, "the staged data file must be flushed-to-disk (FlushToDisk was not invoked)");
        Assert.True(publishIndex > flushIndex, "the staged data file must be flushed-to-disk BEFORE the atomic publish");
    }

    [Fact]
    public async Task PublishFault_AmbiguousError_DoesNotLeakAbsoluteStoragePath()
    {
        // R4F-1 (Security): a non-EEXIST link() failure surfaces RetryUnsafeAmbiguous; its message must
        // disclose only the caller-relative object path + errno, never the absolute mount/warehouse
        // layout. Non-vacuous: reverting TryAtomicPublish's IOException to interpolate the absolute
        // temp/dest paths makes both the surfaced message and its inner exception contain the confined
        // root, reddening the assertions.
        LocalFileSystemBackend.PublishFaultErrnoHook = () => 5; // simulate EIO (a non-EEXIST link failure)
        DeltaStorageException error;
        try
        {
            error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.PutIfAbsentAsync(
                    "logs/00000000000000000005.json", new byte[] { 1 }, CancellationToken.None).AsTask());
        }
        finally
        {
            LocalFileSystemBackend.PublishFaultErrnoHook = null;
        }

        Assert.Equal(StorageErrorKind.RetryUnsafeAmbiguous, error.Kind);
        Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, error.InnerException?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_YieldsRealPathForInRootDirectorySymlink()
    {
        // R4F-3 (Quality): makes the RF-1 metadata-from-real-path guarantee non-vacuous. An IN-ROOT
        // directory symlink (dirlink -> realdir, both under the root) is the only case where the lexical
        // and the confined-real path DIVERGE, so listing through it must surface the object under its REAL
        // path ("realdir/...") not the symlink path ("dirlink/..."). Non-vacuous: reverting
        // ToRelativeReal(realFile) to ToRelative(file) yields the "dirlink/" path and reddens this.
        await _backend.PutIfAbsentAsync("realdir/obj.bin", new byte[] { 1, 2 }, CancellationToken.None);

        string dirLink = Path.Combine(_root, "dirlink");
        try
        {
            Directory.CreateSymbolicLink(dirLink, Path.Combine(_root, "realdir"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Symlink creation is unprivileged on this platform/run — the guarantee cannot be exercised.
            return;
        }

        var paths = new List<string>();
        await foreach (StorageObjectInfo info in _backend.ListAsync("dirlink/", CancellationToken.None))
        {
            paths.Add(info.Path);
        }

        Assert.Contains("realdir/obj.bin", paths); // surfaced under the confined REAL path
        Assert.DoesNotContain(paths, p => p.Contains("dirlink", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StagingCreateFailure_DoesNotLeakAbsoluteStoragePath()
    {
        // RF-8 (Security): a staging-create failure (here a read-only target directory: the RO-PVC / quota
        // class) must surface an error that discloses only the caller-relative object path, never the
        // absolute mount/warehouse layout — in BOTH the message and the inner exception chain. Non-vacuous:
        // reverting CreateFreshTempFrom's clean-wrap or the Redact() on the staging catch reintroduces the
        // absolute path into Message/ToString and reddens the assertions.
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file-mode gating is unavailable; the RO-directory trigger cannot be set here.
        }

        string dir = Path.Combine(_root, "sekret-warehouse");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute); // no write -> CreateNew fails
        // The RO-directory trigger is ineffective under a uid that bypasses DAC mode bits (e.g. root in a
        // CI container), so probe it: if a write into the "read-only" dir unexpectedly succeeds, skip
        // rather than hard-fail the ThrowsAsync assertion.
        string probe = Path.Combine(dir, ".ro-probe");
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            File.SetUnixFileMode(
                dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return; // mode not enforced for this uid (root) — trigger unavailable
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Good: the directory is genuinely read-only for this uid; the trigger will fire.
        }

        try
        {
            DeltaStorageException putError = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.PutIfAbsentAsync(
                    "sekret-warehouse/obj.json", new byte[] { 1 }, CancellationToken.None).AsTask());
            Assert.Equal(StorageErrorKind.Transient, putError.Kind);
            Assert.DoesNotContain(_root, putError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, putError.ToString(), StringComparison.Ordinal); // incl. inner chain

            DeltaStorageException openError = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.OpenWriteAsync("sekret-warehouse/obj.parquet", CancellationToken.None).AsTask());
            Assert.DoesNotContain(_root, openError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, openError.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(
                dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task StagingSetupFailure_ThroughFileWhereDirectoryExpected_DoesNotLeakAbsolutePath()
    {
        // RF-8b: a staging-SETUP failure whose framework exception carries the absolute path — here a path
        // component ("collide") is a FILE, so Directory.CreateDirectory of the parent throws a root-bearing
        // IOException — must surface ONLY the relative object path, in BOTH .Message and .ToString() (inner
        // chain). Deterministic + cross-platform + root-safe (NOT permission-based). This exercises the
        // CreateDirectory-inside-the-catch move AND makes Redact() non-vacuous (ex.Message here really does
        // contain the root, unlike the create-temp path which is already clean at source).
        await _backend.PutIfAbsentAsync("collide", new byte[] { 1 }, CancellationToken.None); // now a FILE

        DeltaStorageException putError = await Assert.ThrowsAsync<DeltaStorageException>(
            () => _backend.PutIfAbsentAsync("collide/obj.json", new byte[] { 2 }, CancellationToken.None).AsTask());
        Assert.Equal(StorageErrorKind.Transient, putError.Kind);
        Assert.DoesNotContain(_root, putError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, putError.ToString(), StringComparison.Ordinal);

        DeltaStorageException openError = await Assert.ThrowsAsync<DeltaStorageException>(
            () => _backend.OpenWriteAsync("collide/obj.parquet", CancellationToken.None).AsTask());
        Assert.DoesNotContain(_root, openError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, openError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StagingWriteFailure_DoesNotLeakAbsolutePathInInnerChain()
    {
        // RF-8b (Security): a write/flush-time failure (the ENOSPC/EDQUOT/EIO quota class) whose framework
        // exception carries the absolute temp path must surface ONLY the relative path — in .Message AND
        // .ToString() (the inner chain, which Exception.ToString() includes). Simulated via the FlushToDisk
        // seam throwing a root-bearing IOException. Covers BOTH PutIfAbsentAsync (write block) and the
        // StagedWriteStream CompleteAsync path. Non-vacuous: reverting the StagingFailure/Sanitize helpers
        // to chain the raw exception reintroduces the absolute path into .ToString().
        LocalFileSystemBackend.FlushToDiskProbe = stream =>
            throw new IOException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"No space left on device : '{stream.Name}'"));
        try
        {
            DeltaStorageException putError = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.PutIfAbsentAsync("logs/x.json", new byte[] { 1 }, CancellationToken.None).AsTask());
            Assert.DoesNotContain(_root, putError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, putError.ToString(), StringComparison.Ordinal);

            Stream stream = await _backend.OpenWriteAsync("logs/y.parquet", CancellationToken.None);
            DeltaStorageException openError = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
            {
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(new byte[] { 1, 2 });
                    await ((ICompletableWriteStream)stream).CompleteAsync(CancellationToken.None);
                }
            });
            Assert.DoesNotContain(_root, openError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, openError.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.FlushToDiskProbe = null;
        }
    }

    [Fact]
    public void SurfaceFailure_StripsAbsoluteRootFromMessageAndInnerChain()
    {
        // RF-8b: the SurfaceFailure mechanism (used by every read/write/delete surfaced error) discloses
        // only the caller-relative path + failure type, never the absolute root — in .Message AND the inner
        // chain. Non-vacuous: reverting Redact reddens .Message; chaining the raw exception reddens .ToString().
        var raw = new IOException(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"No space left on device : '{Path.Combine(_root, "logs", "x.tmp")}'"));
        DeltaStorageException surfaced = _backend.SurfaceFailure("Testing", "logs/x", raw);

        Assert.Equal(StorageErrorKind.Transient, surfaced.Kind);
        Assert.Contains("logs/x", surfaced.Message, StringComparison.Ordinal); // relative path retained
        Assert.DoesNotContain(_root, surfaced.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, surfaced.ToString(), StringComparison.Ordinal); // inner chain too
        Assert.NotSame(raw, surfaced.InnerException); // the raw path-bearing exception is NOT chained
    }

    [Fact]
    public async Task DeleteFailure_OnDirectoryPath_DoesNotLeakAbsolutePath()
    {
        // RF-8b: File.Delete on a path that is a DIRECTORY throws a root-bearing framework exception; the
        // surfaced error must disclose only the relative path (message + inner). Deterministic +
        // cross-platform + root-safe (deleting a directory via File.Delete fails regardless of uid), so it
        // is the non-vacuous end-to-end oracle for the delete-path SurfaceFailure wrapping.
        Directory.CreateDirectory(Path.Combine(_root, "adir"));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => _backend.DeleteAsync("adir", CancellationToken.None).AsTask());
        Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadIoFault_ViaIoFaultHook_DoesNotLeakAbsolutePath()
    {
        // Quality: the ReadRangeAsync ReadExactlyAsync ("read-io") sanitizer is made non-vacuous. A
        // root-bearing exception injected at "read-io" must surface a DeltaStorageException root-free.
        await _backend.PutIfAbsentAsync("logs/ri.json", new byte[] { 1, 2, 3 }, CancellationToken.None);

        LocalFileSystemBackend.IoFaultHook = tag => tag == "read-io"
            ? new IOException($"io failure '{Path.Combine(_root, "logs", "ri.json")}'")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.ReadRangeAsync("logs/ri.json", 0, 3, CancellationToken.None).AsTask());
            Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    public async Task StagedStreamWriteAndFlushOverloads_FaultViaIoFaultHook_DoNotLeakAbsolutePath()
    {
        // Quality: EACH StagedWriteStream write/flush override -- sync Write(byte[]), sync Write(span),
        // legacy WriteAsync(byte[]), and sync Flush() -- routes a faulting IO through Sanitize; a root-
        // bearing exception injected via IoFaultHook must surface a DeltaStorageException root-free.
        LocalFileSystemBackend.IoFaultHook = tag => tag is "write" or "flush"
            ? new IOException($"io failure '{Path.Combine(_root, "part.tmp")}'")
            : null;
        try
        {
            await AssertOverloadSanitizes("wa.bin", s => s.Write(new byte[] { 1 }, 0, 1));
            await AssertOverloadSanitizes("wb.bin", s => s.Write(new byte[] { 1 }.AsSpan()));
            await AssertOverloadSanitizes("wc.bin", s => s.Flush());
            await AssertOverloadSanitizesAsync("wd.bin", s => s.WriteAsync(new byte[] { 1 }, 0, 1));
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }

        async Task AssertOverloadSanitizes(string key, Action<Stream> op)
        {
            Stream s = await _backend.OpenWriteAsync(key, CancellationToken.None);
            await using (s.ConfigureAwait(false))
            {
                var error = Assert.Throws<DeltaStorageException>(() => op(s));
                Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(_root, error.ToString(), StringComparison.Ordinal);
            }
        }

        async Task AssertOverloadSanitizesAsync(string key, Func<Stream, Task> op)
        {
            Stream s = await _backend.OpenWriteAsync(key, CancellationToken.None);
            await using (s.ConfigureAwait(false))
            {
                DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(() => op(s));
                Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(_root, error.ToString(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task ListCanonFault_ViaIoFaultHook_SkipsEntryWithoutLeaking()
    {
        // Quality/S1: an ELOOP resolving an entry's ancestor directory ("list-canon") skips the entry
        // fail-closed (never leaks, never aborts the listing). Non-vacuous: removing the catch/continue
        // lets the injected exception escape the iterator.
        await _backend.PutIfAbsentAsync("lc/only.bin", new byte[] { 1 }, CancellationToken.None);

        LocalFileSystemBackend.IoFaultHook = tag => tag == "list-canon"
            ? new IOException($"Too many levels of symbolic links in '{Path.Combine(_root, "lc")}'")
            : null;
        var paths = new List<string>();
        try
        {
            await foreach (StorageObjectInfo info in _backend.ListAsync("lc/", CancellationToken.None))
            {
                paths.Add(info.Path);
            }
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }

        Assert.Empty(paths); // the sole entry's ancestor canonicalization faulted → skipped, not surfaced
    }

    [Fact]
    public async Task ListMetaFault_UnauthorizedAccess_SkipsEntryWithoutLeaking()
    {
        // Balanced: a mid-listing PERMISSION race (UnauthorizedAccessException, which is NOT an
        // IOException) on the metadata read must be skipped fail-closed, not leak the absolute path.
        // Non-vacuous: dropping "or UnauthorizedAccessException" from the list-meta filter lets it escape.
        await _backend.PutIfAbsentAsync("lu/only.bin", new byte[] { 1 }, CancellationToken.None);

        LocalFileSystemBackend.IoFaultHook = tag => tag == "list-meta"
            ? new UnauthorizedAccessException($"denied '{Path.Combine(_root, "lu", "only.bin")}'")
            : null;
        var paths = new List<string>();
        try
        {
            await foreach (StorageObjectInfo info in _backend.ListAsync("lu/", CancellationToken.None))
            {
                paths.Add(info.Path);
            }
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }

        Assert.Empty(paths);
    }

    [Fact]
    public async Task List_IncludesDotAndHiddenEntries()
    {
        // Architect/Performance regression guard: EnumerationOptions must NOT inherit the default
        // AttributesToSkip = Hidden|System (which silently drops dot/hidden entries AND forces a per-entry
        // stat). A dot-prefixed object must be surfaced by a listing, matching the base SearchOption
        // behavior. Non-vacuous: reverting to the default AttributesToSkip drops ".hidden.crc".
        await _backend.PutIfAbsentAsync("d/.hidden.crc", new byte[] { 1 }, CancellationToken.None);
        await _backend.PutIfAbsentAsync("d/visible.json", new byte[] { 2 }, CancellationToken.None);

        var paths = new List<string>();
        await foreach (StorageObjectInfo info in _backend.ListAsync("d/", CancellationToken.None))
        {
            paths.Add(info.Path);
        }

        Assert.Contains("d/.hidden.crc", paths); // dot/hidden entry not dropped
        Assert.Contains("d/visible.json", paths);
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

    [Fact]
    public async Task SymlinkCycle_IsRejectedFailClosed_WithoutLeakingRoot()
    {
        // S1 (Security): an IN-ROOT symlink cycle (cycleA -> cycleB -> cycleA) makes the confinement
        // primitive's real-target canonicalization (ResolveLinkTarget final=true) throw a raw,
        // path-bearing, UNCLASSIFIED IOException that escapes BEFORE any sanitizer -- leaking the absolute
        // root AND evading a catch(DeltaStorageException) caller. EVERY op must instead fail closed with a
        // CLASSIFIED PathNotConfined whose .Message and .ToString() (inner chain) exclude the root.
        // Non-vacuous: removing the Resolve ELOOP catch reddens this with a raw, unclassified IOException.
        string a = Path.Combine(_root, "cycleA");
        string b = Path.Combine(_root, "cycleB");
        try
        {
            File.CreateSymbolicLink(a, b); // cycleA -> cycleB
            File.CreateSymbolicLink(b, a); // cycleB -> cycleA (an in-root ELOOP cycle)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Symlink creation is unprivileged on this platform/run — the guard cannot be exercised here.
            return;
        }

        try
        {
            await AssertCycleRejectedAsync(() => _backend.ReadRangeAsync("cycleA", 0, 1, CancellationToken.None).AsTask());
            await AssertCycleRejectedAsync(() => _backend.OpenReadAsync("cycleA", CancellationToken.None).AsTask());
            await AssertCycleRejectedAsync(() => _backend.DeleteAsync("cycleA", CancellationToken.None).AsTask());
            await AssertCycleRejectedAsync(() => _backend.HeadAsync("cycleA", CancellationToken.None).AsTask());
            await AssertCycleRejectedAsync(() => _backend.PutIfAbsentAsync("cycleA", new byte[] { 1 }, CancellationToken.None).AsTask());
            await AssertCycleRejectedAsync(() => _backend.OpenWriteAsync("cycleA", CancellationToken.None).AsTask());
        }
        finally
        {
            try { File.Delete(a); } catch (IOException) { }
            try { File.Delete(b); } catch (IOException) { }
        }
    }

    private async Task AssertCycleRejectedAsync(Func<Task> action)
    {
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(action);
        Assert.Equal(StorageErrorKind.PathNotConfined, error.Kind);
        Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, error.ToString(), StringComparison.Ordinal); // incl. inner chain
    }

    [Fact]
    public async Task ReadOpenFault_ViaIoFaultHook_DoesNotLeakAbsolutePath()
    {
        // Q1 (Quality): the read-open sanitizer (OpenReadAsync / ReadRangeAsync open) is made non-vacuous
        // via IoFaultHook. A root-bearing UnauthorizedAccessException injected at "read-open" must surface
        // a DeltaStorageException whose .Message and .ToString() (inner chain) exclude the absolute root.
        // Non-vacuous: reverting SurfaceFailure's Redact/synthetic-inner reddens these assertions.
        await _backend.PutIfAbsentAsync("logs/r.json", new byte[] { 1 }, CancellationToken.None); // File.Exists passes

        LocalFileSystemBackend.IoFaultHook = tag => tag == "read-open"
            ? new UnauthorizedAccessException($"denied '{Path.Combine(_root, "x")}'")
            : null;
        try
        {
            DeltaStorageException openError = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.OpenReadAsync("logs/r.json", CancellationToken.None).AsTask());
            Assert.DoesNotContain(_root, openError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, openError.ToString(), StringComparison.Ordinal);

            DeltaStorageException rangeError = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.ReadRangeAsync("logs/r.json", 0, 1, CancellationToken.None).AsTask());
            Assert.DoesNotContain(_root, rangeError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, rangeError.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    public async Task StagedWriteOverrideFault_ViaIoFaultHook_DoesNotLeakAbsolutePath()
    {
        // Q2 (Quality): the StagedWriteStream.WriteAsync sanitizer is made non-vacuous via IoFaultHook. A
        // root-bearing IOException injected at "write" during an OpenWriteAsync stream write must surface a
        // DeltaStorageException whose .Message and .ToString() (inner chain) exclude the absolute root.
        // Non-vacuous: reverting Sanitize's synthetic-inner to chain the raw exception reddens .ToString().
        LocalFileSystemBackend.IoFaultHook = tag => tag == "write"
            ? new IOException($"No space left on device : '{Path.Combine(_root, "logs", "w.tmp")}'")
            : null;
        try
        {
            Stream stream = await _backend.OpenWriteAsync("logs/w.parquet", CancellationToken.None);
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
            {
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(new byte[] { 1, 2, 3 });
                }
            });
            Assert.Equal(StorageErrorKind.Transient, error.Kind);
            Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    public async Task StagedWriteFlushFault_ViaIoFaultHook_DoesNotLeakAbsolutePath()
    {
        // Quality: the StagedWriteStream.Flush/FlushAsync sanitizer is made non-vacuous via IoFaultHook. A
        // root-bearing IOException injected at "flush" during an explicit FlushAsync must surface a
        // DeltaStorageException whose .Message and .ToString() exclude the absolute root.
        LocalFileSystemBackend.IoFaultHook = tag => tag == "flush"
            ? new IOException($"No space left on device : '{Path.Combine(_root, "logs", "f.tmp")}'")
            : null;
        try
        {
            Stream stream = await _backend.OpenWriteAsync("logs/f.parquet", CancellationToken.None);
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
            {
                await using (stream.ConfigureAwait(false))
                {
                    await stream.FlushAsync(CancellationToken.None);
                }
            });
            Assert.Equal(StorageErrorKind.Transient, error.Kind);
            Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    public async Task PublishFault_ViaIoFaultHook_DoesNotLeakAbsolutePath()
    {
        // publish (Security/Quality): a root-bearing IOException injected at "publish" (BEFORE the real
        // link/Move) flows into the PutIfAbsent publish catch, which must redact + attach a path-free
        // synthetic inner. Both .Message and .ToString() (inner chain) must exclude the absolute root.
        // Non-vacuous: reverting the publish-catch Redact/synthetic-inner reddens these assertions.
        LocalFileSystemBackend.IoFaultHook = tag => tag == "publish"
            ? new IOException($"I/O error : '{Path.Combine(_root, "logs", "p.tmp")}'")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.PutIfAbsentAsync("logs/p.json", new byte[] { 1 }, CancellationToken.None).AsTask());
            Assert.Equal(StorageErrorKind.RetryUnsafeAmbiguous, error.Kind);
            Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    public async Task ListEnumerateFault_ViaIoFaultHook_SurfacesCleanDeltaStorageException()
    {
        // S2 (Security): a root-bearing exception injected at "list-enumerate" (the recursive GetFiles)
        // must surface a CLASSIFIED, redacted DeltaStorageException — never a raw path leak. Non-vacuous:
        // removing the list-enumerate sanitizing catch lets the raw exception escape the iterator and
        // reddens the Kind + no-leak assertions.
        await _backend.PutIfAbsentAsync("d/obj.bin", new byte[] { 1 }, CancellationToken.None);

        LocalFileSystemBackend.IoFaultHook = tag => tag == "list-enumerate"
            ? new UnauthorizedAccessException($"denied '{Path.Combine(_root, "d")}'")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
            {
                await foreach (StorageObjectInfo _ in _backend.ListAsync("d/", CancellationToken.None))
                {
                }
            });
            Assert.Equal(StorageErrorKind.Transient, error.Kind);
            Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    public async Task ListMetaFault_ViaIoFaultHook_SkipsEntryWithoutLeaking()
    {
        // S3 (Security): an object that vanishes between enumeration and its metadata read (a delete race)
        // makes FileInfo throw FileNotFoundException; the entry is SKIPPED, never surfaced with a raw path.
        // Injected at "list-meta" via IoFaultHook. Non-vacuous: removing the list-meta hook consult yields
        // the entry (Assert.Empty reddens); removing the try/catch lets the exception escape the iterator.
        await _backend.PutIfAbsentAsync("m/only.bin", new byte[] { 1 }, CancellationToken.None);

        LocalFileSystemBackend.IoFaultHook = tag => tag == "list-meta"
            ? new FileNotFoundException($"vanished '{Path.Combine(_root, "m", "only.bin")}'")
            : null;
        var paths = new List<string>();
        try
        {
            await foreach (StorageObjectInfo info in _backend.ListAsync("m/", CancellationToken.None))
            {
                paths.Add(info.Path);
            }
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }

        Assert.Empty(paths); // the sole entry's metadata read faulted, so it is skipped, not surfaced
    }

    [Fact]
    public async Task HeadMetaFault_ViaIoFaultHook_DoesNotLeakAbsolutePath()
    {
        // S3 (Security): a HeadAsync metadata-read failure OTHER than a vanished object (FileNotFound ->
        // null) must surface a DeltaStorageException whose .Message and .ToString() exclude the absolute
        // root. Injected at "head-meta" via IoFaultHook. Non-vacuous: reverting SurfaceFailure's
        // Redact/synthetic-inner reddens; a plain FileNotFoundException instead returns null (control).
        await _backend.PutIfAbsentAsync("h/obj.bin", new byte[] { 1 }, CancellationToken.None); // File.Exists passes

        LocalFileSystemBackend.IoFaultHook = tag => tag == "head-meta"
            ? new UnauthorizedAccessException($"denied '{Path.Combine(_root, "h", "obj.bin")}'")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.HeadAsync("h/obj.bin", CancellationToken.None).AsTask());
            Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }

        // Control: a vanished object (FileNotFoundException) is reported as "not found" (null), not surfaced.
        LocalFileSystemBackend.IoFaultHook = tag => tag == "head-meta"
            ? new FileNotFoundException($"vanished '{Path.Combine(_root, "h", "obj.bin")}'")
            : null;
        try
        {
            Assert.Null(await _backend.HeadAsync("h/obj.bin", CancellationToken.None));
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    public async Task List_SkipsUnreadableSubtree_WithoutRawLeak()
    {
        // S2 (Security): an unreadable subdirectory under the root must be SKIPPED by the listing
        // (IgnoreInaccessible), never surface a raw path-bearing UnauthorizedAccessException. Non-vacuous:
        // reverting GetFiles to SearchOption.AllDirectories makes the unreadable subtree throw, which the
        // list-enumerate catch then surfaces as a DeltaStorageException instead of a clean listing.
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file-mode gating is unavailable; the unreadable-subdir trigger cannot be set here.
        }

        await _backend.PutIfAbsentAsync("ok.bin", new byte[] { 1 }, CancellationToken.None);
        await _backend.PutIfAbsentAsync("locked/secret.bin", new byte[] { 2 }, CancellationToken.None);
        string locked = Path.Combine(_root, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None); // no r/x -> recursion into it is inaccessible

        // The mode gate is ineffective under a uid that bypasses DAC (e.g. root in CI); probe and skip.
        try
        {
            _ = Directory.GetFiles(locked);
            File.SetUnixFileMode(
                locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return; // mode not enforced for this uid (root) — trigger unavailable
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Good: the subdir is genuinely inaccessible for this uid; the skip will be exercised.
        }

        try
        {
            var paths = new List<string>();
            await foreach (StorageObjectInfo info in _backend.ListAsync(string.Empty, CancellationToken.None))
            {
                paths.Add(info.Path);
            }

            Assert.Contains("ok.bin", paths); // the readable entry is still surfaced
            Assert.DoesNotContain(paths, p => p.Contains("secret", StringComparison.Ordinal)); // subtree skipped
        }
        finally
        {
            File.SetUnixFileMode(
                locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
