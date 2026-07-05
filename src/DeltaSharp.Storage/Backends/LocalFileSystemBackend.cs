using System.Runtime.CompilerServices;

namespace DeltaSharp.Storage.Backends;

/// <summary>
/// The PVC/POSIX (local file system) <see cref="IStorageBackend"/> (design §2.13.2 "PVC (POSIX)"
/// column, STORY-05.1.3 / #182). Every operation is <b>confined to a configured table-root
/// directory</b>: each user- or log-supplied path is canonicalized and any path that escapes the
/// root — an absolute path outside it, or a <c>..</c> traversal — is rejected fail-closed with
/// <see cref="StorageErrorKind.PathNotConfined"/> (design §5.5 C-SCOPE / LOG-E, checklist 14).
/// </summary>
/// <remarks>
/// <para>The commit primitive <see cref="PutIfAbsentAsync"/> uses <see cref="FileMode.CreateNew"/>
/// (POSIX <c>open(O_CREAT|O_EXCL)</c>), so under a concurrent race exactly one caller creates the
/// object and wins; the losers get <see langword="false"/>, never an exception (design §2.11.1,
/// §2.13.2).</para>
/// <para>Staged writes (<see cref="OpenWriteAsync"/>) write to a temporary file, <c>fsync</c> it
/// (<see cref="FileStream.Flush(bool)"/>), then atomically <see cref="File.Move(string, string, bool)"/>
/// it into place — never a partial/torn destination (design §2.13.2).</para>
/// </remarks>
internal sealed class LocalFileSystemBackend : IStorageBackend
{
    private readonly string _root;
    private readonly string _rootWithSeparator;

    // Local temp-file names must be unique to avoid two concurrent staged writes colliding. A monotonic
    // per-process counter (not a banned nondeterministic id source) plus the process id gives that
    // uniqueness deterministically; the temp name is ephemeral and never persisted in the Delta log.
    private static long _tempCounter;

    /// <summary>Creates a backend confined to <paramref name="tableRoot"/>. The directory is created if
    /// it does not exist so the backend is usable immediately.</summary>
    /// <exception cref="ArgumentException"><paramref name="tableRoot"/> is null or empty.</exception>
    public LocalFileSystemBackend(string tableRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableRoot);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tableRoot));
        _rootWithSeparator = _root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc/>
    public async ValueTask<Stream> ReadRangeAsync(
        string path, long offset, long length, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        string full = Resolve(path);
        if (!File.Exists(full))
        {
            throw DeltaStorageException.NotFound($"Object '{path}' does not exist.");
        }

        var source = new FileStream(
            full, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        await using (source.ConfigureAwait(false))
        {
            long fileLength = source.Length;
            if (offset > fileLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset), offset, $"Offset exceeds the object length {fileLength}.");
            }

            int toRead = (int)Math.Min(length, fileLength - offset);
            source.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[toRead];
            await source.ReadExactlyAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            return new MemoryStream(buffer, writable: false);
        }
    }

    /// <inheritdoc/>
    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string full = Resolve(path);
        if (!File.Exists(full))
        {
            throw DeltaStorageException.NotFound($"Object '{path}' does not exist.");
        }

        Stream stream = new FileStream(
            full, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        return ValueTask.FromResult(stream);
    }

    /// <inheritdoc/>
    public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string full = Resolve(path);
        string? directory = Path.GetDirectoryName(full);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        long ordinal = Interlocked.Increment(ref _tempCounter);
        string temp = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{full}.{Environment.ProcessId}.{ordinal}.tmp");
        Stream stream = new StagedWriteStream(temp, full);
        return ValueTask.FromResult(stream);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> PutIfAbsentAsync(
        string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        string full = Resolve(path);
        string? directory = Path.GetDirectoryName(full);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        FileStream stream;
        try
        {
            // O_CREAT|O_EXCL: exactly one concurrent caller creates the object. A caller that finds the
            // object already present (construction throws and the file exists) lost the race — return
            // false, do not throw (design §2.11.1 single-winner).
            stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(full))
        {
            return false;
        }
        catch (IOException ex)
        {
            // The create neither clearly won nor clearly lost — surface it precisely rather than
            // blindly retrying (design §2.13.3 ambiguous PUT).
            throw new DeltaStorageException(
                StorageErrorKind.RetryUnsafeAmbiguous,
                $"Conditional-create of '{path}' failed ambiguously: {ex.Message}", ex);
        }

        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            // Durably persist the commit object before it is treated as landed (design §2.13.2 fsync).
            stream.Flush(flushToDisk: true);
        }

        return true;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StorageObjectInfo> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string fullPrefix = prefix.Length == 0 ? _root : Resolve(prefix, allowRoot: true);

        string[] files = await Task.Run(
            () => Directory.Exists(_root)
                ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                : Array.Empty<string>(),
            cancellationToken).ConfigureAwait(false);

        Array.Sort(files, StringComparer.Ordinal);
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.StartsWith(fullPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var info = new FileInfo(file);
            yield return new StorageObjectInfo(
                ToRelative(file), info.Length, info.LastWriteTimeUtc, MakeETag(info));
        }
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string full = Resolve(path);
        try
        {
            File.Delete(full);
        }
        catch (DirectoryNotFoundException)
        {
            // Idempotent: a missing object (or its missing parent) is a no-op, not an error.
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<StorageObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string full = Resolve(path);
        if (!File.Exists(full))
        {
            return ValueTask.FromResult<StorageObjectInfo?>(null);
        }

        var info = new FileInfo(full);
        return ValueTask.FromResult<StorageObjectInfo?>(
            new StorageObjectInfo(ToRelative(full), info.Length, info.LastWriteTimeUtc, MakeETag(info)));
    }

    // Canonicalizes an incoming path and confines it to the table root, rejecting fail-closed anything
    // that escapes (absolute-outside-root or ".." traversal). This is the LOG-E control (§5.5): it runs
    // for EVERY path, whether user- or log-supplied.
    private string Resolve(string path, bool allowRoot = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string combined = Path.IsPathFullyQualified(path) ? path : Path.Combine(_root, path);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));

        bool isRoot = string.Equals(full, _root, StringComparison.Ordinal);
        bool isUnderRoot = full.StartsWith(_rootWithSeparator, StringComparison.Ordinal);
        if (!isUnderRoot && !(allowRoot && isRoot))
        {
            throw DeltaStorageException.PathNotConfined(
                $"Path '{path}' escapes the confined table root and is rejected.");
        }

        return full;
    }

    private string ToRelative(string full) =>
        Path.GetRelativePath(_root, full).Replace(Path.DirectorySeparatorChar, '/');

    // A cheap, non-cryptographic entity tag over the size + mtime — enough for idempotent-retry probes;
    // POSIX has no native ETag (design §2.13.2).
    private static string MakeETag(FileInfo info) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}");

    /// <summary>
    /// A write stream that stages into a temporary file and, on close, <c>fsync</c>s and atomically
    /// renames it onto the destination (design §2.13.2). A destination that already exists makes the
    /// publish fail with <see cref="StorageErrorKind.AlreadyExists"/> rather than overwriting.
    /// </summary>
    private sealed class StagedWriteStream : Stream
    {
        private readonly FileStream _inner;
        private readonly string _tempPath;
        private readonly string _destinationPath;
        private bool _published;
        private bool _disposed;

        public StagedWriteStream(string tempPath, string destinationPath)
        {
            _tempPath = tempPath;
            _destinationPath = destinationPath;
            _inner = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                // Durably flush the staged bytes, then publish atomically. File.Move with overwrite:false
                // is the POSIX atomic rename that either fully lands the file or does not.
                _inner.Flush(flushToDisk: true);
                await _inner.DisposeAsync().ConfigureAwait(false);
                Publish();
            }
            finally
            {
                CleanupTemp();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                base.Dispose(disposing);
                return;
            }

            _disposed = true;
            if (disposing)
            {
                try
                {
                    _inner.Flush(flushToDisk: true);
                    _inner.Dispose();
                    Publish();
                }
                finally
                {
                    CleanupTemp();
                }
            }

            base.Dispose(disposing);
        }

        private void Publish()
        {
            try
            {
                File.Move(_tempPath, _destinationPath, overwrite: false);
                _published = true;
            }
            catch (IOException ex) when (File.Exists(_destinationPath))
            {
                throw DeltaStorageException.AlreadyExists(
                    $"Cannot publish staged write: destination '{_destinationPath}' already exists. ({ex.Message})");
            }
        }

        private void CleanupTemp()
        {
            if (_published)
            {
                return;
            }

            try
            {
                File.Delete(_tempPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup of an unpublished temp file; an orphan is reclaimed by VACUUM.
            }
        }
    }
}
