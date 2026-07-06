using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeltaSharp.Storage.Backends;

/// <summary>
/// The PVC/POSIX (local file system) <see cref="IStorageBackend"/> (design §2.13.2 "PVC (POSIX)"
/// column, STORY-05.1.3 / #182). Every operation is <b>confined to a configured table-root
/// directory</b>: each user- or log-supplied path is canonicalized and any path that escapes the
/// root — an absolute path outside it, a <c>..</c> traversal, or a <b>symlink whose real target
/// leaves the root</b> — is rejected fail-closed with
/// <see cref="StorageErrorKind.PathNotConfined"/> (design §5.5 C-SCOPE / LOG-E, checklist 14).
/// </summary>
/// <remarks>
/// <para>The commit primitive <see cref="PutIfAbsentAsync"/> stages content to a private temp file,
/// <c>fsync</c>s it, then publishes it as the <b>atomic single-winner</b>: on POSIX via
/// <c>link()</c> (which fails atomically with <c>EEXIST</c> when the destination exists), on Windows
/// via <see cref="File.Move(string, string, bool)"/> with <c>overwrite: false</c>. Under a concurrent
/// race exactly one caller wins; the losers get <see langword="false"/>, never an exception, and a
/// failed/cancelled attempt can only ever leave an orphan temp file — never a partial destination
/// (design §2.11.1, §2.13.2).</para>
/// <para>Staged writes (<see cref="OpenWriteAsync"/>) write to a temporary file and publish it
/// atomically <b>only when the caller signals success</b> via
/// <see cref="ICompletableWriteStream.CompleteAsync"/>; disposing without completing discards the
/// staged bytes and never publishes a torn destination (design §2.13.2).</para>
/// <para><b>Residual TOCTOU:</b> symlink confinement resolves each path's real target at the moment
/// it is checked. An adversary who can swap an in-root directory for an out-of-root symlink
/// <i>between</i> the check and the subsequent <c>open</c> could still race the filesystem; a fully
/// race-free guarantee requires <c>openat</c>/<c>O_NOFOLLOW</c> primitives that are a tracked
/// follow-up. The lexical + real-target check closes the common log-supplied-path escape.</para>
/// </remarks>
internal sealed class LocalFileSystemBackend : IStorageBackend
{
    private readonly string _root;
    private readonly string _rootWithSeparator;
    private readonly string _realRoot;
    private readonly string _realRootWithSeparator;

    // The long-lived table-root directory descriptor for the race-free (openat + O_NOFOLLOW) confinement
    // walk on POSIX (issue #474). Null on Windows, which retains the canonicalize-then-open confinement.
    // The root is trusted (established + canonicalized once at construction), so opening it by absolute
    // path is not a TOCTOU surface; every subsequent path is resolved relative to this descriptor.
    private readonly SafeFileHandle? _rootHandle;

    // PERF: Redact is handed to every StagedWriteStream; binding the instance method once avoids
    // allocating a fresh method-group delegate on each OpenWriteAsync call.
    private readonly Func<string, string> _redactDelegate;

    // Local temp-file names must be unique so two concurrent staged writes never collide. A monotonic
    // per-process counter (not a banned nondeterministic id source) gives in-process uniqueness; the pod
    // hostname (Environment.MachineName) gives CROSS-process/cross-pod uniqueness on a shared RWX PVC,
    // where Environment.ProcessId is only namespace-local and can repeat across pods. A residual name
    // collision is resolved by retrying with a fresh ordinal (never by deleting a foreign temp), so the
    // ordinal alone is NOT relied on for cross-process uniqueness. The temp name is ephemeral and never
    // persisted in the Delta log.
    private static long _tempCounter;

    // The filename-safe pod/host token mixed into every staging temp name for cross-pod uniqueness.
    private static readonly string TempHostToken = SanitizeHostToken(Environment.MachineName);

    // Bounded retry budget when a staging temp name is already taken by a foreign in-flight temp.
    private const int MaxTempAttempts = 64;

    // Ordering-observation seam (tests only): fired with a commit-step label at each durability step so a
    // test can prove the write -> file-fsync -> atomic-publish -> dir-fsync order. Null/inert in
    // production; fired at the exact step boundary.
    internal static volatile Action<string>? CommitStepProbe;

    // RF-5 file-data durability seam (tests only): substitutes the staged bytes' flush-to-disk so a test
    // can observe that the staging file is fsync'd BEFORE the atomic publish. Null in production, where
    // FlushToDisk performs the real fsync; a test sets it to record the flushed stream. Because the flush
    // and its observation are the SAME call, dropping the flush also drops the observation (the flush
    // test reddens) -- the durability step is no longer independent of its ordering label.
    internal static volatile Action<FileStream>? FlushToDiskProbe;

    // R4F-1 publish-fault seam (tests only): when set, its return code REPLACES the native link() result
    // in TryAtomicPublish so a test can drive the non-EEXIST ambiguous-publish path (e.g. simulate EIO)
    // and assert the surfaced error leaks no absolute path -- without inducing a real link failure. A
    // zero return falls through to the genuine syscall; EEXIST simulates a lost race; any other non-zero
    // simulates an ambiguous failure. Null and inert in production; consulted at the exact link() point.
    internal static volatile Func<int>? PublishFaultErrnoHook;

    // RF-1 perf-observation seam (tests only): invoked with a directory path the FIRST time that
    // directory prefix is canonicalized during a ListAsync scan, so a test can prove the shared ancestor
    // chain is resolved once per directory, not once per listed entry. Null/inert in production.
    internal static volatile Action<string>? ListDirectoryResolveProbe;

    // Unified I/O fault seam (tests only): when non-null, its return for a given op tag REPLACES the real
    // syscall at that op so a test can inject a root-bearing failure and prove a sanitizer non-vacuously.
    // A null return (or a null hook) leaves the real syscall to run; null and inert in production. It is
    // consulted at the exact op boundary of the read/write/flush/list/publish paths, so the injected
    // exception flows into that site's EXISTING sanitizing catch.
    internal static volatile Func<string, Exception?>? IoFaultHook;

    /// <summary>Creates a backend confined to <paramref name="tableRoot"/>. The directory is created if
    /// it does not exist so the backend is usable immediately.</summary>
    /// <exception cref="ArgumentException"><paramref name="tableRoot"/> is null or empty.</exception>
    public LocalFileSystemBackend(string tableRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableRoot);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tableRoot));
        _rootWithSeparator = _root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_root);

        // The real root may differ from the lexical root when an ancestor is itself a symlink (for
        // example macOS's /var -> /private/var). Confinement compares real target against real root so
        // that ambient ancestor symlinks cancel out and only an *escape* is rejected.
        // NOTE: this is the ONE CanonicalizeExisting call NOT wrapped in a fail-closed catch. It is
        // intentional: it runs at construction time on the operator's OWN supplied tableRoot (not a
        // lower-trust request/log path), so failing fast -- and surfacing that self-supplied root -- on a
        // mis-permissioned or cyclic root is acceptable and is not a cross-trust path disclosure.
        _realRoot = CanonicalizeExisting(_root);
        _realRootWithSeparator = _realRoot + Path.DirectorySeparatorChar;

        // On POSIX, pin the real root as a directory descriptor for race-free confinement (issue #474).
        if (!OperatingSystem.IsWindows())
        {
            _rootHandle = ConfinedFileSystem.OpenRoot(_realRoot);
        }

        // PERF: bind Redact once for reuse by every staged write (see _redactDelegate).
        _redactDelegate = Redact;
    }

    /// <inheritdoc/>
    public async ValueTask<Stream> ReadRangeAsync(
        string path, long offset, long length, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        FileStream source;
        try
        {
            Func<string, Exception?>? faultHook = IoFaultHook;
            if (faultHook?.Invoke("read-open") is { } fault)
            {
                throw fault;
            }

            source = OpenConfinedRead(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw SurfaceFailure("Reading", path, ex);
        }

        await using (source.ConfigureAwait(false))
        {
            long fileLength;
            try
            {
                Func<string, Exception?>? faultHook = IoFaultHook;
                if (faultHook?.Invoke("read-len") is { } fault)
                {
                    throw fault;
                }

                fileLength = source.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // RF-8g: reading the open handle's length can throw a path-bearing framework exception;
                // surface it redacted rather than letting the raw absolute path escape.
                throw SurfaceFailure("Reading", path, ex);
            }

            if (offset > fileLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset), offset, $"Offset exceeds the object length {fileLength}.");
            }

            long toReadLong = Math.Min(length, fileLength - offset);
            if (toReadLong > int.MaxValue)
            {
                // A single in-memory range read is bounded by Array's length; a caller asking for a
                // multi-gigabyte slice must page it (design §2.9.1 range GET).
                throw new ArgumentOutOfRangeException(
                    nameof(length), length, $"Range length {toReadLong} exceeds the {int.MaxValue}-byte buffer limit.");
            }

            int toRead = (int)toReadLong;
            var buffer = new byte[toRead];
            try
            {
                Func<string, Exception?>? faultHook = IoFaultHook;
                if (faultHook?.Invoke("read-io") is { } fault)
                {
                    throw fault;
                }

                source.Seek(offset, SeekOrigin.Begin);
                await source.ReadExactlyAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // RF-8b: a read-time I/O error must not leak the absolute mount/warehouse path.
                throw SurfaceFailure("Reading", path, ex);
            }

            return new MemoryStream(buffer, writable: false);
        }
    }

    /// <inheritdoc/>
    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Func<string, Exception?>? faultHook = IoFaultHook;
            if (faultHook?.Invoke("read-open") is { } fault)
            {
                throw fault;
            }

            Stream stream = OpenConfinedRead(path);
            return ValueTask.FromResult(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // RF-8b: an open-for-read failure must not leak the absolute mount/warehouse path.
            throw SurfaceFailure("Opening a read for", path, ex);
        }
    }

    /// <inheritdoc/>
    public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string full = Resolve(path);
        string directory = Path.GetDirectoryName(full) ?? _root;

        FileStream inner;
        string temp;
        try
        {
            // RF-8b: the directory create is inside the sanitizing catch too -- a missing-parent inside a
            // read-only ancestor throws a path-bearing framework exception that would otherwise escape raw.
            Directory.CreateDirectory(directory);
            inner = CreateFreshTemp(full, ".tmp", out temp);
        }
        catch (Exception ex)
        {
            throw SurfaceFailure("Opening a staged write for", path, ex);
        }

        Stream stream = new StagedWriteStream(inner, temp, full, directory, path, _redactDelegate);
        return ValueTask.FromResult(stream);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> PutIfAbsentAsync(
        string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string full = Resolve(path);
        string directory = Path.GetDirectoryName(full) ?? _root;

        // Stage: create a PRIVATE temp with O_EXCL (CreateNew) semantics so it can never alias or clobber
        // another writer's in-flight temp, write the full content, and fsync it BEFORE publishing. A
        // write/cancel/fsync failure can therefore only leave an orphan temp THIS call created — never a
        // partial or zero-length destination, and never a foreign temp deletion (design §2.13.2).
        FileStream stagingStream;
        string temp;
        try
        {
            // RF-8b: the directory create is inside the sanitizing catch (see OpenWriteAsync).
            Directory.CreateDirectory(directory);
            stagingStream = CreateFreshTemp(full, ".put.tmp", out temp);
        }
        catch (Exception ex)
        {
            throw SurfaceFailure("Staging conditional-create of", path, ex);
        }

        try
        {
            await using (stagingStream.ConfigureAwait(false))
            {
                await stagingStream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                FlushToDisk(stagingStream);
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            throw SurfaceFailure("Staging conditional-create of", path, ex);
        }

        // Publish: the atomic single-winner. A lost race deletes the temp and returns false — never an
        // exception (design §2.11.1). A genuinely ambiguous outcome is surfaced, not silently retried.
        bool won;
        try
        {
            won = TryAtomicPublish(temp, full);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            // RF-8b: redact + path-free synthetic inner (Windows File.Move failures carry an absolute
            // path; POSIX TryAtomicPublish is already file-name-only).
            string detail = string.Create(
                CultureInfo.InvariantCulture, $"{ex.GetType().Name}: {Redact(ex.Message)}");
            throw DeltaStorageException.RetryUnsafeAmbiguous(
                string.Create(CultureInfo.InvariantCulture, $"Conditional-create of '{path}' failed ambiguously: {detail}"),
                new IOException(detail));
        }

        if (!won)
        {
            TryDelete(temp);
            return false;
        }

        CommitStepProbe?.Invoke("publish");

        // The name is now published; make the directory entry durable and drop the temp alias (on POSIX
        // link() leaves both names pointing at the same inode). A directory-fsync failure means the name
        // may not survive a crash even though link() succeeded — the outcome is ambiguous and the caller
        // must re-resolve rather than trust a commit we cannot make durable (CF-3).
        CommitStepProbe?.Invoke("dir-fsync");

        // The destination is published (link succeeded), so drop the temp alias whether or not the
        // directory entry can be made durable -- otherwise the ambiguous-durability throw below would
        // orphan it (RF-2). Deleting the temp alias is safe: it leaves the published destination inode
        // intact (on POSIX link() left both names pointing at the same inode).
        bool durable = DirectoryFsync.Sync(directory);
        TryDelete(temp);
        if (!durable)
        {
            throw DeltaStorageException.RetryUnsafeAmbiguous(
                $"Conditional-create of '{path}' linked its destination but the directory entry could not "
                + "be made durable; the outcome is ambiguous and must be re-resolved.");
        }

        return true;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StorageObjectInfo> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        // The prefix is a LITERAL string prefix over object paths (design §2.13.1). A caller trailing
        // separator ("a/") signals directory-scoping intent and is preserved so the scan can be scoped
        // to that subtree instead of walking the whole root.
        bool trailingSeparator = prefix.Length > 0
            && (prefix[^1] == '/' || prefix[^1] == Path.DirectorySeparatorChar);
        string fullPrefix = prefix.Length == 0 ? _root : Resolve(prefix, allowRoot: true);

        string enumerationRoot;
        string literalMatchPrefix;
        if (string.Equals(fullPrefix, _root, StringComparison.Ordinal))
        {
            enumerationRoot = _root;
            literalMatchPrefix = _rootWithSeparator;
        }
        else if (trailingSeparator || Directory.Exists(fullPrefix))
        {
            // Scope the scan to the named directory; everything beneath it matches the prefix.
            enumerationRoot = fullPrefix;
            literalMatchPrefix = fullPrefix;
        }
        else
        {
            // A partial leaf prefix (e.g. "a/1" matching "a/1.bin"): scan the parent and string-match.
            enumerationRoot = Path.GetDirectoryName(fullPrefix) ?? _root;
            literalMatchPrefix = fullPrefix;
        }

        string[] files;
        try
        {
            files = await Task.Run(
                () =>
                {
                    Func<string, Exception?>? faultHook = IoFaultHook;
                    if (faultHook?.Invoke("list-enumerate") is { } fault)
                    {
                        throw fault;
                    }

                    // S2: IgnoreInaccessible SKIPS an unreadable subtree instead of throwing a raw,
                    // path-bearing UnauthorizedAccessException that would leak the mount/warehouse layout.
                    return Directory.Exists(enumerationRoot)
                        ? Directory.GetFiles(
                            enumerationRoot, "*",
                            new EnumerationOptions
                            {
                                RecurseSubdirectories = true,
                                IgnoreInaccessible = true,
                                // A fresh EnumerationOptions defaults AttributesToSkip = Hidden|System and
                                // MatchType = Simple; the replaced SearchOption.AllDirectories overload used
                                // None/Win32. Restore both so listing keeps base semantics -- otherwise
                                // dot/hidden entries are silently dropped AND a non-zero AttributesToSkip
                                // forces a per-entry stat (a ~4x LIST syscall storm on Unix, the very
                                // per-entry-syscall class the RF-1 memoization defends against).
                                AttributesToSkip = FileAttributes.None,
                                MatchType = MatchType.Win32,
                            })
                        : Array.Empty<string>();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // S2: an enumeration failure surfaces a redacted, classified error, never a raw path leak.
            throw SurfaceFailure("Listing", prefix, ex);
        }

        Array.Sort(files, StringComparer.Ordinal);

        // RF-1: the ancestor chain is shared by every file in a directory, so each directory prefix's
        // real (symlink-resolved) path is canonicalized ONCE and memoized -- never re-walked leaf->root
        // per entry, which was an O(depth) syscall storm on a networked PVC (measured ~67 syscalls/entry).
        var resolvedDirectories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.StartsWith(literalMatchPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // Skip reparse-point LEAVES (symlinks/junctions): a listing surfaces real objects, not link
            // entries (design §5.5 C-SCOPE). FileInfo.Attributes reflects the link itself (lstat -- it
            // does not follow the target), so a symlinked leaf is detected without reading a target's
            // metadata.
            bool isReparseLeaf;
            try
            {
                Func<string, Exception?>? faultHook = IoFaultHook;
                if (faultHook?.Invoke("list-leaf-attr") is { } fault)
                {
                    throw fault;
                }

                isReparseLeaf = new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // RF-8g (red-team MISS): reading FileInfo.Attributes stats the leaf, and is the FIRST
                // per-entry syscall -- outside the list-canon/list-meta guards below. It can throw a raw,
                // path-bearing UnauthorizedAccessException (an EACCES race on the entry or its parent) or an
                // IOException (incl. FileNotFound/DirectoryNotFound when the entry vanished between
                // enumeration and this read). An unwrapped throw would escape the async iterator and leak
                // the absolute path. Skip the entry fail-closed -- consistent with the list-canon/list-meta
                // skips -- so it neither leaks the absolute path nor aborts the whole listing.
                continue;
            }

            if (isReparseLeaf)
            {
                continue;
            }

            // RF-1: confine FIRST, then read metadata from the confinement-confirmed real path. Resolving
            // the real path BEFORE reading Length/mtime closes a metadata TOCTOU -- otherwise a directory
            // symlink swapped in between a metadata read and the confinement check could leak an
            // out-of-root Length/mtime/ETag (cross-tenant metadata disclosure). A file under a symlinked
            // ANCESTOR directory has a non-reparse leaf, yet Directory.GetFiles with recursion enabled
            // follows directory symlinks and would surface it, so the ancestor chain (not just the leaf)
            // is resolved and confined (design §5.5 "no cross-tenant listing").
            string directory = Path.GetDirectoryName(file) ?? _root;
            if (!resolvedDirectories.TryGetValue(directory, out string? realDirectory))
            {
                ListDirectoryResolveProbe?.Invoke(directory);
                try
                {
                    Func<string, Exception?>? faultHook = IoFaultHook;
                    if (faultHook?.Invoke("list-canon") is { } fault)
                    {
                        throw fault;
                    }

                    realDirectory = CanonicalizeExisting(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // S1/RF-8f: canonicalizing this entry's ancestor chain can throw a raw, path-bearing
                    // framework exception -- an IOException on a symlink cycle (ELOOP) or an
                    // UnauthorizedAccessException when ResolveLinkTarget crosses an EACCES component. Skip
                    // the entry fail-closed so it neither leaks the absolute path nor aborts the listing.
                    continue;
                }

                resolvedDirectories[directory] = realDirectory;
            }

            string realFile = Path.Combine(realDirectory, Path.GetFileName(file));
            if (!realFile.StartsWith(_realRootWithSeparator, StringComparison.Ordinal))
            {
                continue;
            }

            StorageObjectInfo entry;
            try
            {
                Func<string, Exception?>? faultHook = IoFaultHook;
                if (faultHook?.Invoke("list-meta") is { } fault)
                {
                    throw fault;
                }

                var info = new FileInfo(realFile);
                entry = new StorageObjectInfo(
                    ToRelativeReal(realFile), info.Length, info.LastWriteTimeUtc, MakeETag(info));
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
            {
                // S3: the object vanished between enumeration and this metadata read (a delete race), so
                // its FileInfo throws FileNotFoundException; skip it rather than leak a raw path.
                continue;
            }

            yield return entry;
        }
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return DeleteConfinedUnix(path);
        }

        string full = Resolve(path);
        try
        {
            Func<string, Exception?>? faultHook = IoFaultHook;
            if (faultHook?.Invoke("delete") is { } fault)
            {
                throw fault;
            }

            File.Delete(full);
        }
        catch (DirectoryNotFoundException)
        {
            // Idempotent: a missing object (or its missing parent) is a no-op, not an error.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // RF-8b: a delete failure (permission, or the path is a directory) must not leak the absolute
            // mount/warehouse path.
            throw SurfaceFailure("Deleting", path, ex);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<StorageObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return HeadConfinedUnix(path);
        }

        string full = Resolve(path);
        if (!File.Exists(full))
        {
            return ValueTask.FromResult<StorageObjectInfo?>(null);
        }

        try
        {
            Func<string, Exception?>? faultHook = IoFaultHook;
            if (faultHook?.Invoke("head-meta") is { } injected)
            {
                throw injected;
            }

            var info = new FileInfo(full);
            return ValueTask.FromResult<StorageObjectInfo?>(
                new StorageObjectInfo(ToRelative(full), info.Length, info.LastWriteTimeUtc, MakeETag(info)));
        }
        catch (FileNotFoundException)
        {
            // S3: the object vanished between the File.Exists check and this metadata read (a delete
            // race). Head is nullable and a vanished object is correctly reported as "not found".
            return ValueTask.FromResult<StorageObjectInfo?>(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Any other metadata-read failure must not leak the absolute mount/warehouse path.
            throw SurfaceFailure("Reading metadata for", path, ex);
        }
    }

    // Atomically publishes the fsync'd temp file to the destination as the single-winner. Returns true
    // iff THIS call created the destination; false iff the destination already existed. On POSIX this
    // uses link() (EEXIST is the atomic single-winner signal); .NET File.Move(overwrite:false) is NOT
    // atomic on all POSIX platforms (macOS maps it to rename(), which silently overwrites, so
    // concurrent callers can all appear to win), so it is used only on Windows where MoveFileEx without
    // MOVEFILE_REPLACE_EXISTING fails atomically when the destination exists.
    internal static bool TryAtomicPublish(string tempPath, string destinationPath)
    {
        Func<string, Exception?>? faultHook = IoFaultHook;
        if (faultHook?.Invoke("publish") is { } injected)
        {
            // Test-only: inject a publish-time failure BEFORE the real link/Move so the injected exception
            // flows into the caller's publish catch and exercises its redaction non-vacuously. Inert in
            // production (IoFaultHook is null).
            throw injected;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.Move(tempPath, destinationPath, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                return false;
            }
        }

        int errno;
        Func<int>? fault = PublishFaultErrnoHook;
        if (fault is not null)
        {
            // Test-only: simulate the link() outcome without touching the filesystem.
            errno = fault();
            if (errno == 0)
            {
                return true;
            }
        }
        else
        {
            int rc = PosixInterop.Link(tempPath, destinationPath);
            if (rc == 0)
            {
                return true;
            }

            errno = Marshal.GetLastPInvokeError();
        }

        if (errno == PosixInterop.EEXIST)
        {
            return false;
        }

        // Surface only the file NAMES (never the absolute directory paths) so that when a caller wraps
        // this into an ambiguous-outcome error and logs its message, the internal mount/warehouse layout
        // is not disclosed (RF-7 message hygiene); the errno is the actionable diagnostic.
        throw new IOException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"link('{Path.GetFileName(tempPath)}' -> '{Path.GetFileName(destinationPath)}') failed with errno {errno}."));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of an unpublished temp file; an orphan is reclaimed by VACUUM.
        }
        catch (UnauthorizedAccessException)
        {
            // Likewise best-effort.
        }
    }

    // RF-5: the single file-data durability flush for the staged bytes, routed through one helper so the
    // flush and its observation are the SAME action. In production FlushToDiskProbe is null and this
    // performs the real fsync; a test substitutes the probe to record the flushed stream. The "file-fsync"
    // ordering step is emitted HERE (not at the call site) so removing the flush also removes its
    // observation -- both the durability-order test and the flush test redden if the flush is dropped.
    private static void FlushToDisk(FileStream stream)
    {
        Action<FileStream>? probe = FlushToDiskProbe;
        if (probe is not null)
        {
            probe(stream);
        }
        else
        {
            stream.Flush(flushToDisk: true);
        }

        CommitStepProbe?.Invoke("file-fsync");
    }

    // Builds a staging temp name mixing the pod/host token (cross-pod uniqueness), the process id
    // (in-namespace uniqueness) and a monotonic ordinal (in-process uniqueness). Deterministic given its
    // inputs — no Guid/Random.
    internal static string BuildTempName(string destinationFull, long ordinal, string suffix) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{destinationFull}.{TempHostToken}.{Environment.ProcessId}.{ordinal}{suffix}");

    // Reduces a hostname to a bounded, filename-safe token: non-[A-Za-z0-9-] characters become '-' and
    // the result is capped. Deterministic and environment-sourced (Environment.MachineName is the K8s
    // pod hostname), so it never introduces a banned nondeterministic id.
    private static string SanitizeHostToken(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return "host";
        }

        const int maxLength = 64;
        int length = Math.Min(host.Length, maxLength);
        return string.Create(length, host, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                char c = source[i];
                bool safe = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9') || c == '-';
                span[i] = safe ? c : '-';
            }
        });
    }

    // Opens a private staging temp with O_EXCL (FileMode.CreateNew) semantics, drawing a fresh monotonic
    // ordinal each attempt. Used by every write path so a temp can NEVER clobber or alias a foreign
    // in-flight temp (critical on a shared RWX PVC where two pods share a PID namespace).
    private static FileStream CreateFreshTemp(string destinationFull, string suffix, out string tempPath) =>
        CreateFreshTempFrom(
            destinationFull, suffix, static () => Interlocked.Increment(ref _tempCounter), out tempPath);

    // Core of CreateFreshTemp with an injectable ordinal source (for deterministic collision tests): on a
    // CreateNew collision with a foreign temp of the same name, retries with the NEXT ordinal — it never
    // deletes the colliding file, because that file belongs to another in-flight writer.
    internal static FileStream CreateFreshTempFrom(
        string destinationFull, string suffix, Func<long> nextOrdinal, out string tempPath)
    {
        for (int attempt = 0; attempt < MaxTempAttempts; attempt++)
        {
            string candidate = BuildTempName(destinationFull, nextOrdinal(), suffix);
            try
            {
                var stream = new FileStream(
                    candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
                tempPath = candidate;
                return stream;
            }
            catch (IOException) when (attempt < MaxTempAttempts - 1 && File.Exists(candidate))
            {
                // A foreign temp already owns this name: retry with a fresh ordinal, never deleting it.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A genuine staging-create failure (permissions, no space, missing parent, or a final-
                // attempt collision). Surface only the file NAME and the failure TYPE -- never the absolute
                // directory path (nor the path-bearing framework exception as an inner) -- so a caller that
                // logs this cannot learn the internal mount/warehouse layout (RF-8 message hygiene).
                throw new IOException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Could not create staging temp '{Path.GetFileName(candidate)}' ({ex.GetType().Name})."));
            }
        }

        throw new IOException(string.Create(
            CultureInfo.InvariantCulture,
            $"Could not create a unique staging temp for '{Path.GetFileName(destinationFull)}' after {MaxTempAttempts} attempts."));
    }

    // Strips the confined table root (both its lexical and real forms) from a message so a surfaced error
    // never discloses the internal mount/warehouse layout (RF-7/RF-8 message hygiene). A relative object
    // path or a bare file name is retained -- only the absolute root prefix is redacted.
    private string Redact(string message)
    {
        string redacted = message.Replace(_root, "<table-root>", StringComparison.Ordinal);
        if (!string.Equals(_realRoot, _root, StringComparison.Ordinal))
        {
            redacted = redacted.Replace(_realRoot, "<table-root>", StringComparison.Ordinal);
        }

        return redacted;
    }

    // Wraps a filesystem operation failure into a Transient DeltaStorageException that discloses ONLY the
    // caller-relative object path and the failure type -- never the absolute mount/warehouse layout, in
    // the message OR the inner-exception chain. Exception.ToString() surfaces an inner exception's own
    // message, so the raw path-bearing framework exception must NOT be chained; a synthetic, path-free
    // inner carrying the redacted detail is attached instead so diagnostics survive without disclosure
    // (RF-8b: never let a raw filesystem exception become the inner of a surfaced storage error).
    internal DeltaStorageException SurfaceFailure(string operation, string path, Exception ex)
    {
        string detail = string.Create(
            CultureInfo.InvariantCulture, $"{ex.GetType().Name}: {Redact(ex.Message)}");
        return DeltaStorageException.Transient(
            string.Create(CultureInfo.InvariantCulture, $"{operation} '{path}' failed: {detail}"),
            new IOException(detail));
    }

    // Resolves a path to its root-relative form using the cheap lexical gate only (reject absolute-
    // outside-root and ".." traversal). On POSIX the symlink/real-target gate that Resolve performs is
    // REPLACED by the race-free openat + O_NOFOLLOW walk (strictly stronger — it closes the check-to-use
    // window), so only the lexical portion is needed here to derive the components to walk.
    private string ResolveRelative(string path, bool allowRoot = false)
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

        return isRoot ? string.Empty : full[_rootWithSeparator.Length..];
    }

    // Maps a confinement-walk failure to the deterministic storage error the backend contract promises.
    private static DeltaStorageException MapWalkError(ConfinedFileSystem.WalkError error, string path) => error switch
    {
        ConfinedFileSystem.WalkError.NotFound => DeltaStorageException.NotFound($"Object '{path}' does not exist."),
        ConfinedFileSystem.WalkError.NotConfined => DeltaStorageException.PathNotConfined(
            $"Path '{path}' resolves through a symlink to a location outside the confined table root and is rejected."),
        _ => DeltaStorageException.Transient($"Resolving '{path}' failed."),
    };

    // Race-free confined open of a leaf (POSIX): walks each component with openat + O_NOFOLLOW from the
    // root descriptor and returns the leaf descriptor, mapping ELOOP/ENOTDIR (a symlink swap) to
    // PathNotConfined and ENOENT to NotFound. Caller must be on the non-Windows path.
    private SafeFileHandle OpenConfinedLeaf(string path, int leafFlags, uint mode = 0)
    {
        // Defense-in-depth: the lexical + canonicalize pre-check rejects obvious escapes early and keeps
        // the §5.5 LOG-E path-sanitization on the hot path; the openat + O_NOFOLLOW walk below is the
        // load-bearing RACE-FREE enforcement that also catches a component swapped in after this check.
        _ = Resolve(path);
        string rel = ResolveRelative(path);
        string[] components = ConfinedFileSystem.SplitConfinedComponents(rel);
        SafeFileHandle? handle = ConfinedFileSystem.TryOpenLeaf(
            _rootHandle!, components, leafFlags, mode, out ConfinedFileSystem.WalkError error);
        return handle ?? throw MapWalkError(error, path);
    }

    // Opens a confined read stream over an existing object. On POSIX this is race-free (openat +
    // O_NOFOLLOW component walk from the root descriptor), so a symlink swapped in after any check still
    // cannot redirect the open outside the root (issue #474). On Windows the existing canonicalize-then-
    // open confinement is retained. Throws NotFound for a missing object and PathNotConfined for an escape.
    private FileStream OpenConfinedRead(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            string full = Resolve(path);
            if (!File.Exists(full))
            {
                throw DeltaStorageException.NotFound($"Object '{path}' does not exist.");
            }

            return new FileStream(
                full, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        }

        SafeFileHandle handle = OpenConfinedLeaf(path, PosixInterop.O_RDONLY);
        return new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
    }

    // POSIX race-free HeadAsync: reach the leaf by an openat + O_NOFOLLOW walk, then read size (via the
    // descriptor, not a re-resolvable path) and mtime (fstat, size-cross-checked). A missing object is
    // reported as null; a symlink-swap escape as PathNotConfined.
    private ValueTask<StorageObjectInfo?> HeadConfinedUnix(string path)
    {
        try
        {
            Func<string, Exception?>? faultHook = IoFaultHook;
            if (faultHook?.Invoke("head-meta") is { } injected)
            {
                throw injected;
            }

            // Defense-in-depth pre-check (see OpenConfinedLeaf); openat below is the race-free enforcement.
            _ = Resolve(path);
            string rel = ResolveRelative(path);
            string[] components = ConfinedFileSystem.SplitConfinedComponents(rel);
            SafeFileHandle? handle = ConfinedFileSystem.TryOpenLeaf(
                _rootHandle!, components, PosixInterop.O_RDONLY, 0, out ConfinedFileSystem.WalkError error);
            if (handle is null)
            {
                return error == ConfinedFileSystem.WalkError.NotFound
                    ? ValueTask.FromResult<StorageObjectInfo?>(null)
                    : throw MapWalkError(error, path);
            }

            using (handle)
            {
                long length = RandomAccess.GetLength(handle);
                DateTime lastWriteUtc = ConfinedFileSystem.GetLastModifiedUtc(handle, length);
                return ValueTask.FromResult<StorageObjectInfo?>(
                    new StorageObjectInfo(rel, length, lastWriteUtc, MakeETagFromParts(length, lastWriteUtc)));
            }
        }
        catch (FileNotFoundException)
        {
            // Parity with the Windows path: a vanished object (e.g. an injected head-meta fault modelling a
            // delete race) is reported as "not found" (null), not surfaced.
            return ValueTask.FromResult<StorageObjectInfo?>(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw SurfaceFailure("Reading metadata for", path, ex);
        }
    }

    // POSIX race-free DeleteAsync: reach the leaf's confined PARENT descriptor by an openat + O_NOFOLLOW
    // walk, then unlinkat the leaf name relative to it. Idempotent: a missing leaf or missing parent is a
    // no-op; a symlink-swap escape is PathNotConfined.
    private ValueTask DeleteConfinedUnix(string path)
    {
        try
        {
            Func<string, Exception?>? faultHook = IoFaultHook;
            if (faultHook?.Invoke("delete") is { } fault)
            {
                throw fault;
            }

            // Defense-in-depth pre-check (see OpenConfinedLeaf); openat below is the race-free enforcement.
            _ = Resolve(path);
            string rel = ResolveRelative(path);
            string[] components = ConfinedFileSystem.SplitConfinedComponents(rel);
            SafeFileHandle? parent = ConfinedFileSystem.TryOpenParent(
                _rootHandle!, components, out string leafName, out ConfinedFileSystem.WalkError error);
            if (parent is null)
            {
                // A missing parent means the object is already gone — idempotent no-op.
                return error == ConfinedFileSystem.WalkError.NotFound
                    ? ValueTask.CompletedTask
                    : throw MapWalkError(error, path);
            }

            using (parent)
            {
                int parentFd = (int)parent.DangerousGetHandle();

                // Reject a symlink leaf, uniform with read/head (O_NOFOLLOW): operating on a symlink is an
                // escape attempt. unlinkat itself never follows a symlink, so this probe only enforces that
                // policy — a swapped-in symlink would at worst have its in-root entry removed, never an
                // out-of-root target, so confinement holds regardless of the probe→unlink window.
                int probe = PosixInterop.OpenAt(
                    parentFd, leafName, PosixInterop.O_RDONLY | PosixInterop.O_NOFOLLOW | PosixInterop.O_CLOEXEC, 0);
                if (probe < 0)
                {
                    int probeErrno = Marshal.GetLastPInvokeError();
                    if (probeErrno == PosixInterop.ENOENT)
                    {
                        return ValueTask.CompletedTask; // idempotent
                    }

                    if (probeErrno == PosixInterop.ELOOP || probeErrno == PosixInterop.ENOTDIR)
                    {
                        throw MapWalkError(ConfinedFileSystem.WalkError.NotConfined, path);
                    }

                    throw SurfaceFailure("Deleting", path, new IOException($"openat failed (errno {probeErrno})."));
                }

                _ = PosixInterop.Close(probe);

                if (PosixInterop.UnlinkAt(parentFd, leafName, 0) != 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == PosixInterop.ENOENT)
                    {
                        return ValueTask.CompletedTask; // idempotent
                    }

                    throw SurfaceFailure("Deleting", path, new IOException($"unlinkat failed (errno {errno})."));
                }
            }

            return ValueTask.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw SurfaceFailure("Deleting", path, ex);
        }
    }

    // A synthetic entity tag over size + mtime for descriptor-based Head/List (POSIX has no native ETag).
    private static string MakeETagFromParts(long length, DateTime lastWriteUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{length:x}-{lastWriteUtc.Ticks:x}");

    // Canonicalizes an incoming path and confines it to the table root, rejecting fail-closed anything
    // that escapes (absolute-outside-root, ".." traversal, or a symlink whose real target leaves the
    // root). This is the LOG-E control (§5.5): it runs for EVERY path, whether user- or log-supplied.
    private string Resolve(string path, bool allowRoot = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string combined = Path.IsPathFullyQualified(path) ? path : Path.Combine(_root, path);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));

        // Cheap lexical gate first.
        bool isRoot = string.Equals(full, _root, StringComparison.Ordinal);
        bool isUnderRoot = full.StartsWith(_rootWithSeparator, StringComparison.Ordinal);
        if (!isUnderRoot && !(allowRoot && isRoot))
        {
            throw DeltaStorageException.PathNotConfined(
                $"Path '{path}' escapes the confined table root and is rejected.");
        }

        // Real-target gate: follow symlinks on the existing portion and re-check containment, so a
        // lexically-clean path cannot tunnel out of the root through a planted symlink.
        string realFull;
        try
        {
            Func<string, Exception?>? faultHook = IoFaultHook;
            if (faultHook?.Invoke("resolve-canon") is { } fault)
            {
                throw fault;
            }

            realFull = CanonicalizeExisting(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // S1/RF-8f: canonicalizing the real target can throw a raw, path-bearing, UNCLASSIFIED
            // framework exception -- an IOException on a symlink cycle (ELOOP), or an
            // UnauthorizedAccessException when ResolveLinkTarget crosses an EACCES component. Either would
            // escape before any sanitizer. Fail closed uniformly: reject as unconfined with a
            // RELATIVE-path-only message so the absolute root never leaks and a catch(DeltaStorageException)
            // caller still traps it.
            throw DeltaStorageException.PathNotConfined(
                $"Path '{path}' could not be resolved (possible symlink cycle or inaccessible ancestor) and is rejected.");
        }

        bool realIsRoot = string.Equals(realFull, _realRoot, StringComparison.Ordinal);
        bool realIsUnderRoot = realFull.StartsWith(_realRootWithSeparator, StringComparison.Ordinal);
        if (!realIsUnderRoot && !(allowRoot && realIsRoot))
        {
            throw DeltaStorageException.PathNotConfined(
                $"Path '{path}' resolves through a symlink to a location outside the confined table root and is rejected.");
        }

        return full;
    }

    // Resolves the real (symlink-free) path for the existing portion of a path, leaving any not-yet-
    // existing trailing segments appended verbatim. Emulates realpath(3) for the existing prefix by
    // following link targets component-by-component; bounded to avoid symlink cycles.
    private static string CanonicalizeExisting(string path)
    {
        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var trailing = new Stack<string>();
        while (!Path.Exists(current))
        {
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Length == 0 || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            trailing.Push(Path.GetFileName(current));
            current = parent;
        }

        string real = CanonicalizeExistingNode(current, depth: 0);
        while (trailing.Count > 0)
        {
            real = Path.Combine(real, trailing.Pop());
        }

        return Path.TrimEndingDirectorySeparator(real);
    }

    private static string CanonicalizeExistingNode(string existingPath, int depth)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(existingPath));
        for (int guard = 0; guard < 64; guard++)
        {
            if (!Path.Exists(p))
            {
                return p;
            }

            FileSystemInfo info = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                // p itself is not a link; canonicalize its parent chain so an ancestor symlink is
                // still followed (recursion terminates: the parent is strictly shorter).
                string? parent = Path.GetDirectoryName(p);
                if (parent is null || parent.Length == 0 || string.Equals(parent, p, StringComparison.Ordinal)
                    || depth >= 256)
                {
                    return p;
                }

                string canonicalParent = CanonicalizeExistingNode(parent, depth + 1);
                return Path.Combine(canonicalParent, Path.GetFileName(p));
            }

            p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
        }

        return p;
    }

    private string ToRelative(string full) =>
        Path.GetRelativePath(_root, full).Replace(Path.DirectorySeparatorChar, '/');

    // Relativizes a REAL (symlink-resolved) path against the real root, so a listed object key is correct
    // even when an ambient ancestor symlink makes the lexical root differ from the real root (RF-1: the
    // ListAsync metadata + key are read from the confinement-confirmed real path, which lives under
    // _realRoot, not the lexical _root).
    private string ToRelativeReal(string realFull) =>
        Path.GetRelativePath(_realRoot, realFull).Replace(Path.DirectorySeparatorChar, '/');

    // A cheap, non-cryptographic entity tag over the size + mtime — enough for idempotent-retry probes;
    // POSIX has no native ETag (design §2.13.2).
    private static string MakeETag(FileInfo info) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}");

    /// <summary>
    /// A write stream that stages into a temporary file and, <b>only when the caller invokes
    /// <see cref="ICompletableWriteStream.CompleteAsync"/></b>, <c>fsync</c>s it and publishes it
    /// atomically to the destination (design §2.13.2). Disposing WITHOUT completing (a faulted or
    /// abandoned write) deletes the temp and never publishes, so a torn/partial destination is
    /// impossible. A destination that already exists makes the publish fail with
    /// <see cref="StorageErrorKind.AlreadyExists"/> rather than overwriting.
    /// </summary>
    private sealed class StagedWriteStream : Stream, ICompletableWriteStream
    {
        private readonly FileStream _inner;
        private readonly string _tempPath;
        private readonly string _destinationPath;
        private readonly string _destinationDirectory;
        private readonly string _displayPath;
        private readonly Func<string, string> _redact;
        private bool _completed;
        private bool _disposed;

        public StagedWriteStream(
            FileStream inner, string tempPath, string destinationPath, string destinationDirectory,
            string displayPath, Func<string, string> redact)
        {
            _tempPath = tempPath;
            _destinationPath = destinationPath;
            _destinationDirectory = destinationDirectory;

            // RF-7: the caller-supplied RELATIVE path is used in ambiguous-failure messages (mirroring
            // PutIfAbsent) so a surfaced error never leaks the internal absolute mount/warehouse layout.
            _displayPath = displayPath;
            _redact = redact;
            _inner = inner;
        }

        // RF-8b: a staged-stream write/flush failure (mid-write ENOSPC/EDQUOT/EIO) surfaces ONLY the
        // relative object path + failure type, never the absolute path in the message OR the inner chain.
        private DeltaStorageException Sanitize(string operation, Exception ex)
        {
            string detail = string.Create(
                CultureInfo.InvariantCulture, $"{ex.GetType().Name}: {_redact(ex.Message)}");
            return DeltaStorageException.Transient(
                string.Create(CultureInfo.InvariantCulture, $"{operation} staged write to '{_displayPath}' failed: {detail}"),
                new IOException(detail));
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        // CanSeek is false, so Length/Position are not part of this forward-only write stream's contract
        // (like Read/Seek/SetLength above they throw NotSupportedException). Length in particular must NOT
        // delegate to _inner.Length: FileStream.Length does an fstat that, on a degraded mount, throws a
        // path-bearing IOException carrying the temp's absolute path under _root -- the same fstat leak the
        // read path guards via the "read-len" seam (RF-8g, Security R11).
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            try
            {
                Func<string, Exception?>? faultHook = LocalFileSystemBackend.IoFaultHook;
                if (faultHook?.Invoke("flush") is { } fault)
                {
                    throw fault;
                }

                _inner.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Sanitize("Flushing", ex);
            }
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            try
            {
                Func<string, Exception?>? faultHook = LocalFileSystemBackend.IoFaultHook;
                if (faultHook?.Invoke("flush") is { } fault)
                {
                    throw fault;
                }

                await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Sanitize("Flushing", ex);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        // A staged write is forward-only (Read/Seek throw NotSupported); SetLength likewise -- both keeps
        // the stream contract consistent (SetLength requires CanSeek) and avoids delegating to an unguarded
        // _inner.SetLength whose failure would carry the temp's absolute path (RF-8g, Security R10 Info).
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            try
            {
                Func<string, Exception?>? faultHook = LocalFileSystemBackend.IoFaultHook;
                if (faultHook?.Invoke("write") is { } fault)
                {
                    throw fault;
                }

                _inner.Write(buffer, offset, count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Sanitize("Writing", ex);
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try
            {
                Func<string, Exception?>? faultHook = LocalFileSystemBackend.IoFaultHook;
                if (faultHook?.Invoke("write") is { } fault)
                {
                    throw fault;
                }

                _inner.Write(buffer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Sanitize("Writing", ex);
            }
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                Func<string, Exception?>? faultHook = LocalFileSystemBackend.IoFaultHook;
                if (faultHook?.Invoke("write") is { } fault)
                {
                    throw fault;
                }

                await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Sanitize("Writing", ex);
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                Func<string, Exception?>? faultHook = LocalFileSystemBackend.IoFaultHook;
                if (faultHook?.Invoke("write") is { } fault)
                {
                    throw fault;
                }

                await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Sanitize("Writing", ex);
            }
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Durably flush the staged bytes, then publish atomically. Publication happens ONLY here,
            // so a producer that faults before completing never lands a readable destination. The flush
            // is routed through FlushToDisk (RF-5) so the durability step is observable. A flush/dispose
            // failure (mid-completion ENOSPC/quota) is sanitized (RF-8b) so it never leaks the absolute
            // path; Publish() throws its own already-sanitized DeltaStorageException outside the catch.
            try
            {
                await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
                FlushToDisk(_inner);
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Sanitize("Completing", ex);
            }

            Publish();
            _completed = true;
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await QuietDisposeInnerAsync().ConfigureAwait(false);
            if (!_completed)
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
                QuietDisposeInner();
                if (!_completed)
                {
                    CleanupTemp();
                }
            }

            base.Dispose(disposing);
        }

        // A staged write abandoned WITHOUT CompleteAsync discards its bytes (CleanupTemp drops the temp),
        // so a dispose-time flush fault (ENOSPC/EDQUOT/EIO on the buffered bytes) is irrelevant AND must
        // not throw out of Dispose: on Unix a FileStream flush failure carries the temp's absolute path
        // (SafeFileHandle.Path, which is under _root), so an unguarded rethrow would both leak the root and
        // MASK the in-flight exception that triggered the abandon. Swallow it best-effort (RF-8g, Security
        // R10) -- like TryDelete -- after consulting the fault seam so the swallow is non-vacuously testable.
        // When _completed the inner was already disposed inside CompleteAsync (guarded), so this second
        // dispose is a no-op and cannot throw.
        private async ValueTask QuietDisposeInnerAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);

                Func<string, Exception?>? faultHook = IoFaultHook;
                if (faultHook?.Invoke("dispose") is { } fault)
                {
                    throw fault;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: the abandoned temp is discarded regardless; never throw the path out of
                // Dispose nor mask the in-flight exception that triggered the abandon.
            }
        }

        private void QuietDisposeInner()
        {
            try
            {
                _inner.Dispose();

                Func<string, Exception?>? faultHook = IoFaultHook;
                if (faultHook?.Invoke("dispose") is { } fault)
                {
                    throw fault;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: see QuietDisposeInnerAsync.
            }
        }

        private void Publish()
        {
            bool won;
            try
            {
                won = TryAtomicPublish(_tempPath, _destinationPath);
            }
            catch (Exception ex) when (ex is not DeltaStorageException)
            {
                // RF-2: the atomic publish failed, so the temp is unpublished -- drop it before surfacing
                // the ambiguous outcome so it is not orphaned. RF-8b: redact the message and attach a
                // path-free synthetic inner (Windows File.Move failures carry an absolute path; POSIX
                // TryAtomicPublish is already file-name-only).
                CleanupTemp();
                string detail = string.Create(
                    CultureInfo.InvariantCulture, $"{ex.GetType().Name}: {_redact(ex.Message)}");
                throw DeltaStorageException.RetryUnsafeAmbiguous(
                    string.Create(CultureInfo.InvariantCulture, $"Publishing staged write to '{_displayPath}' failed ambiguously: {detail}"),
                    new IOException(detail));
            }

            if (!won)
            {
                CleanupTemp();
                throw DeltaStorageException.AlreadyExists(
                    $"Cannot publish staged write: destination '{_displayPath}' already exists.");
            }

            CommitStepProbe?.Invoke("publish");

            // The single-winner rename/link landed; make its directory entry durable, then drop the
            // temp alias (on POSIX link() left both names pointing at the same inode). A directory-fsync
            // failure means the name may not survive a crash even though the publish succeeded — the
            // outcome is ambiguous and the caller must re-resolve rather than trust it (CF-3). The temp
            // alias is dropped whether or not the entry is durable, so the ambiguous-durability throw
            // never orphans it (RF-2): the published destination inode stays intact.
            CommitStepProbe?.Invoke("dir-fsync");
            bool durable = DirectoryFsync.Sync(_destinationDirectory);
            CleanupTemp();
            if (!durable)
            {
                throw DeltaStorageException.RetryUnsafeAmbiguous(
                    $"Staged write to '{_displayPath}' published but the directory entry could not be "
                    + "made durable; the outcome is ambiguous and must be re-resolved.");
            }
        }

        private void CleanupTemp() => TryDelete(_tempPath);
    }
}
