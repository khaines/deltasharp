using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DeltaSharp.Storage.Delta;
using Microsoft.Win32.SafeHandles;

namespace DeltaSharp.Storage.Backends;

/// <summary>
/// The PVC/POSIX (local file system) <see cref="IStorageBackend"/> (design §2.13.2 "PVC (POSIX)"
/// column, STORY-05.1.3 / #182). Every operation is <b>confined to a configured table-root
/// directory</b>: each user- or log-supplied path is rejected fail-closed with
/// <see cref="StorageErrorKind.PathNotConfined"/> if it escapes the root — an absolute path outside
/// it, a <c>..</c> traversal, or a <b>symlink whose target leaves the root</b> (design §5.5 C-SCOPE /
/// LOG-E, checklist 14). On POSIX the confinement is <b>race-free</b> (see remarks); on Windows it is
/// the lexical + real-target (canonicalize-then-open) check.
/// </summary>
/// <remarks>
/// <para>The commit primitive <see cref="PutIfAbsentAsync"/> stages content to a private temp file,
/// <c>fsync</c>s it, then publishes it as the <b>atomic single-winner</b>: on POSIX via
/// <c>linkat()</c> into the confined parent descriptor (which fails atomically with <c>EEXIST</c> when
/// the destination exists), on Windows via <see cref="File.Move(string, string, bool)"/> with
/// <c>overwrite: false</c>. Under a concurrent race exactly one caller wins; the losers get
/// <see langword="false"/>, never an exception, and a failed/cancelled attempt can only ever leave an
/// orphan temp file — never a partial destination (design §2.11.1, §2.13.2).</para>
/// <para>Staged writes (<see cref="OpenWriteAsync"/>) write to a temporary file and publish it
/// atomically <b>only when the caller signals success</b> via
/// <see cref="ICompletableWriteStream.CompleteAsync"/>; disposing without completing discards the
/// staged bytes and never publishes a torn destination (design §2.13.2).</para>
/// <para><b>Race-free confinement (POSIX, issue #474).</b> Every operation resolves its path by
/// walking one component at a time with <c>openat(2)</c> + <see cref="PosixInterop.O_NOFOLLOW"/> from a
/// long-lived table-root directory descriptor, and then operates on the returned <b>descriptor</b>
/// (read/stat/list) or on a component name relative to a confined <b>parent</b> descriptor
/// (create/link/unlink) — never on a re-resolvable path string. A component swapped for a symlink at
/// any point (including in the check-to-use window an adversary would exploit) fails the open with
/// <c>ELOOP</c>, so the previously-documented residual TOCTOU is <b>closed</b>: there is no
/// check-to-use window for read/list/publish. A cheap lexical + canonicalize pre-check still runs first
/// as defense-in-depth (fast classified reject + §5.5 sanitization), but the <c>openat</c> walk is the
/// load-bearing enforcement. Windows retains the canonicalize-then-open confinement (no
/// <c>openat</c>/<c>O_NOFOLLOW</c> equivalent; NTFS reparse-point semantics differ).</para>
/// </remarks>
internal sealed partial class LocalFileSystemBackend : IStorageBackend, IDisposable
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

    // Test-only seam (issue #474): fired AFTER the Resolve pre-check but BEFORE the race-free openat walk,
    // so a test can swap an in-root directory for an out-of-root symlink in the exact check-to-use window
    // and prove the openat + O_NOFOLLOW walk — not the (now-stale) pre-check — is what fails closed. Null
    // and inert in production.
    internal static volatile Action? ConfinementRaceProbe;

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

    /// <summary>Releases the long-lived table-root directory descriptor used for POSIX race-free
    /// confinement (null/no-op on Windows). The <see cref="SafeFileHandle"/> finalizer also reclaims it if
    /// the backend is not disposed, so a missed dispose leaks no descriptor.</summary>
    public void Dispose() => _rootHandle?.Dispose();

    /// <summary>
    /// The canonicalized (symlink-resolved) absolute table root — a STABLE, value-based identity for this
    /// table. Two backends constructed over the same table root (e.g. a resolve-then-read pair spanning two
    /// <see cref="DeltaSharp.Storage.Reading.DeltaReadSource"/> instances) share it, while different tables
    /// differ. The CDF read door binds a resolution proof to this identity so the proof cannot replay on a
    /// DIFFERENT table (which would bypass that table's enablement gate and stamp foreign timestamps).
    /// <para><b>Case-sensitivity assumption.</b> Consumers compare this id with <c>StringComparison.Ordinal</c>,
    /// matching the case-sensitive Linux production filesystem (where two case-differing spellings ARE distinct
    /// tables — an <c>OrdinalIgnoreCase</c> compare there would be a security regression, falsely ACCEPTING a
    /// cross-table proof). On a case-INsensitive dev filesystem (macOS APFS / Windows NTFS) two case-differing
    /// path spellings of the SAME table therefore yield different ids and a same-table resolve→read with
    /// mismatched-case paths is falsely REJECTED — a fail-closed inconvenience (it throws, never bypasses), not
    /// a correctness/security hole. Use consistent-case table paths on such hosts.</para>
    /// </summary>
    internal string TableRootId => _realRoot;

    /// <summary>The PVC/POSIX backend family — the <c>deltasharp.backend=pvc</c> telemetry identity.</summary>
    public StorageBackendKind Kind => StorageBackendKind.Pvc;

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
        if (!OperatingSystem.IsWindows())
        {
            return OpenWriteConfinedUnix(path);
        }

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

    // POSIX race-free staged write: stage a private temp in the CONFINED parent descriptor; the returned
    // stream publishes it with linkat into that same descriptor on CompleteAsync (issue #474).
    private ValueTask<Stream> OpenWriteConfinedUnix(string path)
    {
        string full = Resolve(path); // defense-in-depth pre-check
        string directory = Path.GetDirectoryName(full) ?? _root;

        SafeFileHandle? parent = null;
        try
        {
            string rel = ResolveRelative(path);
            ConfinementRaceProbe?.Invoke();
            string[] components = ConfinedFileSystem.SplitConfinedComponents(rel);
            parent = ConfinedFileSystem.OpenOrCreateParent(
                _rootHandle!, components, out string destName, out ConfinedFileSystem.WalkError werr);
            if (parent is null)
            {
                throw MapWalkError(werr, path);
            }

            (SafeFileHandle tempHandle, string tempName) = CreateConfinedTemp((int)parent.DangerousGetHandle(), destName);
            var inner = new FileStream(tempHandle, FileAccess.Write, bufferSize: 4096, isAsync: false);
            Stream stream = new StagedWriteStream(inner, parent, tempName, destName, directory, path, _redactDelegate);
            return ValueTask.FromResult(stream);
        }
        catch (DeltaStorageException)
        {
            parent?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            parent?.Dispose();
            throw SurfaceFailure("Opening a staged write for", path, ex);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> PutIfAbsentAsync(
        string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return await PutIfAbsentConfinedUnix(path, content, cancellationToken).ConfigureAwait(false);
        }

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
                string.Create(CultureInfo.InvariantCulture, $"Conditional-create of {DiagnosticText.DescribePath(path)} failed ambiguously: {detail}"),
                new IOException(detail),
                path: path);
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
                $"Conditional-create of {DiagnosticText.DescribePath(path)} linked its destination but the directory entry could not "
                + "be made durable; the outcome is ambiguous and must be re-resolved.",
                path: path);
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

            StorageObjectInfo? entry = ReadListEntryMetadata(realFile);
            if (entry is null)
            {
                continue;
            }

            yield return entry;
        }
    }

    // Reads a confirmed-confined list entry's metadata. On POSIX this is race-free: the metadata is read
    // from a descriptor reached by an openat + O_NOFOLLOW walk, so a symlink swapped in between the
    // confinement check above and this read cannot leak an out-of-root Length/mtime (it fails the walk and
    // the entry is skipped). On Windows the FileInfo stat is retained. Returns null to skip a vanished or
    // now-unconfined entry rather than aborting the listing.
    private StorageObjectInfo? ReadListEntryMetadata(string realFile)
    {
        try
        {
            Func<string, Exception?>? faultHook = IoFaultHook;
            if (faultHook?.Invoke("list-meta") is { } fault)
            {
                throw fault;
            }

            if (OperatingSystem.IsWindows())
            {
                var info = new FileInfo(realFile);
                return new StorageObjectInfo(
                    ToRelativeReal(realFile), info.Length, info.LastWriteTimeUtc, MakeETag(info));
            }

            string relKey = ToRelativeReal(realFile);
            string[] components = ConfinedFileSystem.SplitConfinedComponents(relKey);
            SafeFileHandle? handle = ConfinedFileSystem.TryOpenLeaf(
                _rootHandle!, components, PosixInterop.O_RDONLY, 0, out _);
            if (handle is null)
            {
                return null; // vanished or became a symlink between confinement and this read — skip
            }

            using (handle)
            {
                long length = RandomAccess.GetLength(handle);
                DateTime mtime = ConfinedFileSystem.GetLastModifiedUtc(handle, length);
                return new StorageObjectInfo(relKey, length, mtime, MakeETagFromParts(length, mtime));
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or DeltaStorageException)
        {
            // S3: the object vanished (delete race) or its metadata read failed; skip it rather than leak
            // a raw path or abort the listing.
            return null;
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
    // never discloses the internal mount/warehouse layout (RF-7/RF-8 message hygiene), and then strips the
    // VALUE out of any Hive `key=value` directory segment that survives.
    //
    // The second half exists because root-stripping alone is not enough under the Hive-path PII ruling. A
    // framework exception embeds the absolute path; redacting the root leaves the table-RELATIVE remainder,
    // which is Hive-encoded, so the partition value -- a column value, i.e. table data -- lands in `{detail}`
    // in the very same message from which DescribePath just dropped it:
    //
    //   Deleting 'part-x.parquet' (partitioned by: email, region) failed:
    //   IOException: Could not delete file '<table-root>/email=alice%40example.com/region=EU/part-x.parquet'
    //
    // and Uri.UnescapeDataString recovers the address. `{detail}` was correctly out of scope for #664, which
    // was about inner-exception/ToString() rendering; the Hive-path ruling is newer and converts it from a
    // log-injection question into a PII channel in Message itself. Only a value that follows a separator and
    // a `key=` is rewritten, so ordinary text such as "errno=13" is untouched.
    private string Redact(string message)
    {
        string redacted = message.Replace(_root, "<table-root>", StringComparison.Ordinal);
        if (!string.Equals(_realRoot, _root, StringComparison.Ordinal))
        {
            redacted = redacted.Replace(_realRoot, "<table-root>", StringComparison.Ordinal);
        }

        // BOUND THE REGEX INPUT, not the regex. QuotedPathPrefix scans BACKWARD for the quote that opened
        // the path, and on a message containing no quote at all that scan runs to position 0 from every
        // start position -- measured quadratic: 15.8 KB in 35 ms, 69.8 KB in 284 ms, 297 KB in 5.25 s. The
        // fix is NOT to bound the lookbehind: bounding a recognizer is how this file failed open twice, and
        // a path longer than the bound would simply opt out of redaction. Truncating the INPUT instead is
        // safe in the only direction that matters -- it removes strictly more text than it keeps -- and the
        // result is capped at DetailMaxLength three lines below regardless, so the limit is set well above
        // that to leave redaction room to fit more prose under the cap than the raw message would.
        //
        // TRUNCATE AT A SEGMENT BOUNDARY, NOT MID-VALUE. A blind cut is not merely lossy, it FABRICATES a
        // shape the producer never emitted: a quoted path whose closing quote has been cut off. That
        // matters because an opened-but-never-closed quoted path is the one input on which an interior
        // quote is indistinguishable from a closing quote -- ".../k=Aaaa' Bbb" reads, by the message's own
        // syntax, as a closed quoted region followed by prose, so the recognizer stops at the apostrophe
        // and "Bbb" (still partition value) survives next to a <value> marker. No amount of recognizer
        // work can resolve that, because the truncated string is byte-identical to a legitimately closed
        // one. So the cut backs off to the last path separator in the window: whatever partial segment the
        // cut would have exposed is removed outright rather than half-redacted. If the window holds no
        // separator at all there is no path-shaped run in it either -- every branch is separator-gated --
        // so the hard cut is safe.
        if (redacted.Length > RedactScanLimit)
        {
            redacted = redacted[..RedactScanLimit];
            int lastSegment = redacted.LastIndexOfAny(RedactPathSeparators);
            if (lastSegment >= 0)
            {
                redacted = redacted[..(lastSegment + 1)];
            }
        }

        redacted = HivePartitionValue().Replace(redacted, static m => m.Groups["key"].Value + "=<value>");

        // #683 sibling of the PII strip. Redact previously did root-stripping and value-stripping and never
        // called Sanitize, so a poisoned segment's raw CR/LF/U+2028 reached Message through `{detail}` -- the
        // channel this PR just moved IN scope for PII was still open for INJECTION. The {detail} carve-out
        // cited #664, but #664 was about inner-exception/ToString() RENDERING; defending one injection class
        // and not the other in the same string is incoherent. The cap is generous (a framework message is
        // prose, not an identifier) but present, so `{detail}` cannot be a flooding aggregate either.
        return DiagnosticText.Sanitize(redacted, DetailMaxLength);
    }

    // The cap for an echoed FRAMEWORK exception message. Far wider than an identifier cap because this is
    // prose that an operator reads (a .NET IO message plus a redacted path runs a few hundred characters),
    // but bounded, because the path inside it is attacker-influenceable.
    private const int DetailMaxLength = 512;

    // How much of a framework message the Hive recognizer is allowed to scan. The backward `QuotedPathPrefix`
    // lookbehind is quadratic in this window (a message with many apostrophes but no separator re-scans to 0
    // from every start position): at 8× the cap it was ~0.5 s of synchronous CPU per surfaced error — a ReDoS
    // on the async I/O failure path (apostrophes are legal path chars, so a foreign `add.path` supplies them,
    // and an error storm multiplies it into thread-pool starvation). Bounding the INPUT (not the recognizer —
    // bounding the recognizer is how this file failed open twice) at 2× the cap keeps the quadratic term at a
    // fixed ~1 KB (worst case ~2 ms) while still leaving redaction room to pull more prose under DetailMaxLength
    // than the raw text would fit. The cut still backs off to a segment boundary (below) so no value is halved.
    private const int RedactScanLimit = DetailMaxLength * 2;

    // The separator set the scan-limit back-off cuts to. Deliberately the same two characters every branch
    // of HivePartitionValue is gated on, so "the cut lands on a segment boundary" and "the cut cannot
    // expose half a value to the recognizer" are the same statement.
    private static readonly char[] RedactPathSeparators = ['/', '\\'];

    // A Hive partition directory inside a path-shaped run: a separator, a key with no separator or '=' in it,
    // '=', then the encoded value up to the next separator, quote, or whitespace.
    //
    // BOTH quantifiers are UNBOUNDED and the key may be EMPTY, and every one of those choices closes a
    // fail-OPEN hole rather than being a stylistic preference. The governing rule, learned the hard way over
    // three revisions of this line: a hygiene recognizer must fail CLOSED, so any input shape it declines to
    // match is a redaction the attacker opted out of by choosing that shape.
    //
    //   * Bounded key {1,128}: a 129-character key made the group unmatchable, and because the lookbehind
    //     requires the match to start immediately after a separator there is no alternative start position,
    //     so the WHOLE `key=value` survived. A 129-character Delta column name is legal and 129+1+26 bytes is
    //     well under NAME_MAX, so this was reachable on this backend.
    //   * Bounded value {0,512}: leaked the value's tail.
    //   * Non-empty key {1,}: "/=alice%40example.com/" and "/==alice%40example.com/" matched NOTHING and
    //     leaked in full. An empty or '='-leading Hive key is not a shape DeltaSharp writes, which is exactly
    //     why the recognizer must still cover it — the input is a FOREIGN add.path.
    //   * Key class excluding whitespace and quotes: a key containing a space, tab, CR, U+2028, NBSP, an
    //     apostrophe or a double quote made the group unable to span to the '=', and with only one legal
    //     start position per segment the recognizer then matched NOTHING. Unlike the three above this is
    //     not a foreign-table hypothetical: DeltaWriteTarget percent-encodes the partition VALUE but writes
    //     the column NAME raw, and nothing in this assembly validates a partition column name — so a table
    //     partitioned by a legal Delta column such as `my col` or `o'brien` emits the leaking shape on
    //     DeltaSharp's OWN happy path, with no adversary involved at all.
    //   * Literal '=' only: a segment whose separator arrived percent-encoded ("email%3Dvalue") is a Hive
    //     directory the recognizer did not recognize. Both spellings are now accepted.
    //
    // The bounds also bought nothing they claimed to: a single greedy negated character class with no nesting
    // and no alternation is linear either way, measured identical on 2,560,000 characters. Bound the
    // DISCLOSURE, never the recognizer.
    //
    // RegexOptions.NonBacktracking is not available here — it rejects lookaround, and the (?<=[/\\])
    // lookbehind is what keeps ordinary prose such as "errno=13" out of the match.
    //
    // An UNENCODED space in a partition VALUE is now fully covered whenever a right delimiter exists -- the
    // delimited branches admit whitespace in the value precisely because add.path is FOREIGN and a foreign
    // writer is under no obligation to percent-encode (DeltaSharp itself does). It under-redacts only in the
    // undelimited branch 4, which is residual R1 below: "name=Alice Taylor" at the very end of a message
    // renders as "name=<value> Taylor". That partial-match shape is exactly why branch order matters.
    //
    // Residual, tracked in #704 and deliberately NOT changed here so that issue stays self-contained: the
    // lookbehind class does not include a quote or string-start, so a RELATIVE key (the shape an object-store
    // backend will produce) is not matched. Unreachable in-tree today — Resolve/ResolveRelative both
    // Path.Combine + GetFullPath, so every framework message carries an absolute path and root-stripping
    // always leaves the remainder behind a separator.
    //
    // The remaining residuals are NOT restated here. They are enumerated as R1/R2 beside the branch table
    // below, next to the character classes that produce them, because a residual list kept at a distance
    // from the code it describes is how the previous one came to be factually wrong.
    // SIX branches, ordered MOST-ANCHORED FIRST. It was FOUR until the rootless branch closed mechanism 7,
    // and FIVE until the rootless-BACKSLASH branch closed sibling drift #5; if a comment anywhere still says
    // four or five, it predates one of those and is wrong. Counts of the code's own shape are the prose most
    // likely to rot, because they are true when written and nothing re-checks them -- and a stale STRUCTURAL
    // claim is worse than a stale behavioural one, since a reader uses it to decide where to look rather
    // than what to expect. Three such counts had rotted by the time this was written, and the separator
    // table below rotted three more times after it -- which is why that one is now executed rather than
    // read. This count is not, and a reader should treat it accordingly.
    //
    // THE INVARIANT, and it is the one this recognizer kept violating: NO BRANCH MAY EMIT A MARKER OVER A
    // PARTIALLY-CONSUMED VALUE. Every outcome must be exactly one of
    //
    //     FULL     -- the value is consumed to a true segment boundary and "<value>" replaces all of it, or
    //     DECLINE  -- nothing matches, the segment is echoed as-is and sanitized like any other text.
    //
    // PARTIAL is not a third option. It is strictly WORSE than DECLINE even though it discloses less,
    // because the marker tells the reader the value was handled while its tail sits next to the marker:
    // "email=<value> Taylor" or "email=<value>'taylor%40example.com". A decline is a documentable residual;
    // a partial is a false claim of removal. SEVEN distinct mechanisms produced partials here, and only the
    // first was found by reasoning about the design rather than by measuring it. Six are enumerated
    // immediately below; the seventh is stated where the branch that closes it is defined, because it is a
    // fact about a LEFT anchor rather than about a value class:
    //
    //   1. BRANCH ORDER. An unanchored branch tried first wins at the same start position and stops the
    //      value at whitespace, so the anchored branch that would have consumed the rest never runs.
    //   2. VALUE CLASS vs QUOTE DELIMITER. A value class excluding quotes stops at a quote INSIDE the value,
    //      and a bare (?=['"]) lookahead is then satisfied by that interior quote.
    //   3. UNANCHORED TERMINAL BRANCH. With no right anchor at all, a value class excluding whitespace
    //      simply stops mid-value and emits.
    //   4. RIGHT ANCHOR MET INSIDE THE VALUE. Strengthening the lookahead to demand a boundary AFTER the
    //      quote narrows the class but does not close it: any quote in the value that happens to be
    //      followed by a boundary still satisfies it. `/tbl/email=Boys' Club.taylor%40example.com` emits a
    //      marker over "Boys" and leaves the rest standing. Mechanism 2 with a shorter reach.
    //   5. A CUT THAT INVENTS AN UNPARSEABLE SHAPE. Not in the recognizer at all -- in Redact, which caps
    //      the input at RedactScanLimit. A blind cut removes only text, which is why it looked obviously
    //      safe, but it also FABRICATES a quoted path whose closing quote was cut off. That is the one
    //      input on which an interior quote is genuinely indistinguishable from a closing quote, because
    //      the truncated string is byte-identical to a legitimately closed one, so the recognizer stops at
    //      the apostrophe and the tail survives beside the marker. It cannot be fixed in the pattern; the
    //      cut has to land on a segment boundary, and it now does. The lesson generalises past this file:
    //      a defensive bound is part of the parser's input contract, and one that can synthesise a shape
    //      the parser cannot disambiguate is a defect no matter how strictly it only-removes-data.
    //
    //   6. A CHARACTER THAT IS A SEPARATOR ON ONE PLATFORM AND CONTENT ON ANOTHER. `\` was excluded from
    //      every value class and accepted by every right anchor, so the value run stopped at a backslash
    //      and that same backslash satisfied the lookahead. On Windows that reading is right; on POSIX a
    //      backslash is an ordinary filename character, so `dept=Legal\alice%40example.com` is ONE
    //      component and the marker was emitted over half of it. The recognizer cannot know which platform
    //      produced a foreign add.path, and the two errors are not symmetric: reading `\` as a separator
    //      LEAKS on POSIX, while reading it as content merely over-redacts a Windows directory name. So it
    //      is read as content -- fail closed -- and `\` is no longer a right delimiter anywhere.
    //
    //      THE GENERAL FORM, which is what makes this worth six lines: a delimiter that holds on one
    //      platform is not a delimiter, it is a guess, and a recognizer must not resolve a guess in the
    //      direction that discloses. This is the delimiter-strength ladder applied to a dimension the
    //      ladder did not originally range over -- strength across PLATFORMS, not merely across contexts.
    //
    // Mechanism 3 is closed by anchoring branch 4 at $ and letting its value run to the end of the segment.
    // Mechanism 5 is closed in Redact, by backing the cut off to the last path separator.
    // Mechanism 6 is closed by admitting `\` to every value class and removing it from branch 1's right
    // anchor. Measured on the 4500-cell lattice: 180 partials to 0, no FULL cell lost for a strict key, and
    // the over-redaction corpus SHRANK by one row -- `stat '/usr/lib' -> st_mode=0100644\` now survives
    // verbatim, because prose ending in a backslash is no longer eaten by a value run.
    // Mechanisms 2 and 4 are closed by QuotedPathPrefix, and the reason it works where two rounds of
    // boundary-set tuning did not is that it stops asking a LOCAL question. "Is this quote followed by
    // something boundary-shaped?" is unanswerable from the quote's neighbourhood, because an interior
    // quote and a closing quote have identical neighbourhoods. "Did anything OPEN this quote?" is
    // answerable, and it is the question that actually defines a delimiter. When the path is unquoted the
    // quote branches decline and branch 4 consumes the value in full, so closing this cost no coverage:
    // measured over a 768-cell product, 99 partials became 0 with zero FULL cells lost.
    //
    // A NOTE ON MECHANISM 1, which the previous version of this comment got WRONG in a way worth keeping.
    // It claimed anchoring branch 4 SUBSUMED the ordering fix, so that "no ordering of these four can
    // [FOUR AT THE TIME; there are five now -- the quotation is preserved as it was written, because the
    // point is that the claim was false when it was true-as-counted]
    // produce a partial". Mechanism 4 falsifies that: a right anchor satisfied INSIDE the value still lets
    // an earlier branch pre-empt the branch that would have consumed the whole run. The claim was derived
    // from a matrix that was green -- and green because its corpus could not express the cell. Anchoring
    // constrains where a branch may STOP; it says nothing about which branch gets to start. Ordering
    // remains load-bearing, and the two are independent.
    //
    // AND THE COROLLARY, because the obvious repair for mechanism 4 is to reorder rather than to gate.
    // Putting the $-anchored branch first DOES close the partials -- a reviewer measured exactly that and
    // was right about it. But it was right against a recognizer where the partials were still OPEN. Once
    // QuotedPathPrefix closes them, reordering buys nothing and costs prose. Re-measured on the 1620-cell
    // lattice, the one that HAS the follower axis:
    //
    //     shipped [1,2,3,4]   FULL 1098  PARTIAL 0  DECLINE 522   trailing prose 3/3
    //     B4 first [4,1,2,3]  FULL 1098  PARTIAL 0  DECLINE 522   trailing prose 1/3
    //     B4 second [1,4,2,3] FULL 1098  PARTIAL 0  DECLINE 522   trailing prose 1/3
    //
    // Identical census, strictly worse diagnosability: "Access to the path '...' is denied." loses
    // " is denied." and "...'. Retry later." loses "Retry later.", because the $-anchored value run eats
    // every character to end-of-message once it is allowed to start first. The ordering is pinned -- the
    // reorder turns tests RED, among them
    // Redact_ApostropheBearingPartitionValue_IsFullyStrippedAndKeepsTheProse -- so this is a decision the
    // suite defends rather than a comment claiming it.
    //
    // The general lesson, and it applies to every measurement recorded in this file: A MEASUREMENT IS
    // ONLY VALID AGAINST THE BASELINE IT WAS TAKEN ON. Both the earlier claim (ordering is subsumed) and
    // this one (reordering is free) were sound when measured and wrong when carried forward.
    //
    // Beyond the invariant, the branch structure encodes a LADDER: the STRONGER the right delimiter, the
    // MORE PERMISSIVE the key may be, because the delimiter is the evidence that a path is in play at all.
    // Earlier rounds instead anchored by delimiter CLASS and tuned key classes to compensate, which coupled
    // the two axes -- narrowing a key class to protect prose silently withdrew coverage from a legal column
    // name, and widening it to restore that ate prose. Four rounds of that produced six fail-opens.
    //
    //   1. PATH SEPARATOR follows -- strongest evidence. Key `[^/\=]` and value `[^/\]`: quotes AND
    //                                whitespace allowed in both. Covers every segment DeltaWriteTarget can
    //                                emit, including "o'brien y=v/".
    //   2. CLOSING QUOTE follows,  -- one relaxation: quotes in the key. Covers
    //      key has quotes, no space    "'<root>/o'brien=alice%40example.com' is denied."
    //                                  This branch is for DIAGNOSABILITY, not privacy, and the distinction
    //                                  is measured: dropping it leaves the matrix green, because branch 4
    //                                  then consumes the same values to end-of-message. What is lost is the
    //                                  PROSE AFTER the closing quote -- " is denied." gets eaten along with
    //                                  the value. It is pinned by the over-redaction theory, which is the
    //                                  honest place for it, rather than claimed as a leak guard.
    //   3. CLOSING QUOTE follows,  -- the mirror relaxation: whitespace in the key, no quotes. Covers
    //      key has space, no quotes    "'<root>/my col=alice%40example.com'."
    //   4. END OF MESSAGE follows  -- key without whitespace (quotes allowed), value runs to the end.
    //
    // Branches 2 and 3 are deliberately NOT merged. Their INTERSECTION -- a key holding BOTH a quote and
    // whitespace at a quote delimiter -- is the one cell that must stay declined, because that is the shape
    // of prose containing a quoted path: in `Error opening "/tmp/file": errno=13"` the key candidate is
    // `file": errno`, a quote AND a space. Splitting the relaxation buys quote-bearing AND space-bearing
    // column names without losing errno=13. Branch 4's key admits quotes but NOT whitespace for the same
    // reason: whitespace is what keeps it off "open /proc/self/fd failed: errno=13".
    //
    // EVERY key group is LAZY (`*?`), and that is a correctness requirement, not a performance tweak.
    //
    //   The shared DiagnosticText.HiveSeparatorPattern made this recognizer and DescribePath accept the same
    //   separator ALPHABET. It did NOT make them equivalent, because they used different SEARCH STRATEGIES:
    //   DescribePath scans left-to-right and stops at the FIRST separator, while a GREEDY regex key takes the
    //   LAST one it can reach. On "/k%3DSECRET=value/" the greedy form captured "k%3DSECRET" as the key and
    //   redacted only "value" -- so SECRET, a partition VALUE, was echoed, while DescribePath correctly
    //   reported the key as "k" and dropped the rest. Five reviewers verified the shared constant and all
    //   five confirmed it, because the property they checked -- "do both halves accept the same spellings?"
    //   -- genuinely holds. The question none of us asked is "do both halves pick the same separator when
    //   SEVERAL are present?" Lazy keys make the regex adopt first-separator semantics, which is
    //   DescribePath's rule, and Redact_AndDescribePath_ResolveTheSameKey asserts the two agree on WHICH
    //   SUBSTRING IS THE KEY -- not merely that neither leaks. A shared alphabet is not a shared decision
    //   procedure; only the equivalence assertion catches that.
    //
    // Excluding '=' from every key class stops greedy over-capture across "/a=1=2=3/".
    //
    // THE RESIDUALS. This list has been factually wrong in three consecutive reviews, so it is no longer
    // written as prose that a reader must trust. The clauses below are the LITERAL PREDICATE evaluated
    // by Redact_MonotonicityMatrix: a declined cell that does not satisfy one of them fails that test, and
    // so does a clause that no longer has any declined cell to explain. The list cannot drift from the code
    // in either direction, which is the only property that makes a residual list worth reading.
    //
    // THE FULL SET, because this block announces itself as "THE RESIDUALS" and for two rounds it did not
    // contain all of them. R1 and R2 are stated here. R3 is CLOSED and its scoping is stated below. R4 is
    // defined beside branch 5, ~280 lines down, because it is a fact about that branch's left anchor -- and
    // an auditor reading only this block would have judged an R4-classified decline undocumented, which is
    // the failure mode this block exists to prevent, arriving through layout instead of through wording.
    // R5 is stated below and is a PARTIAL residual, the only one; it is filed as #723 and deliberately not
    // folded into a decline clause. R6 was the bare trailing backslash; it is CLOSED, and it is kept in this
    // list with its disposition rather than deleted, because a residual that was filed and then fixed is the
    // only kind whose absence looks identical to a residual that was never noticed.
    // Distance from the code is why the old list rotted, so nothing is moved
    // here that belongs beside its branch -- but a set that names itself complete has to enumerate, and the
    // pointers are the enumeration.
    //
    // R4 HAS SINCE BEEN NARROWED, AND THE WAY IT WAS OVER-READ IS THE MOST TRANSFERABLE THING IN THIS FILE.
    // Its text says "no slash at all"; every classifier written against it said "no FORWARD slash"; and the
    // gap between those two sentences was a live disclosure that five reviewers, three corpora and a
    // purpose-built anti-over-claim assertion all filed under R4 as irreducible. When a residual is used to
    // EXCUSE a divergence, the predicate that selects it is load-bearing security code and has to be read
    // against the residual's own words, not written from the shape of the cells in front of you.
    //
    // NO COUNTS ARE RESTATED HERE, deliberately. Every previous version of this comment carried a census
    // ("exactly two cells are declined") that the recognizer contradicted -- once while it was separately
    // producing 82 PARTIAL matches across the same sweep, which is why two reviewers read the same
    // paragraph and disagreed about whether the partials were documented. They were not, and could not be:
    // a decline list cannot express a partial. The census lives in the test, as numbers a change must
    // update.
    //
    //   R1  {WHITESPACE-BEARING key} x {NO right delimiter}. "No right delimiter" now covers FOUR ways of
    //       having none: end of message, an unquoted path merely ENDING in a quote, a quoted path whose
    //       quote nothing ever closes, and -- added this round -- a path that continues with a BACKSLASH,
    //       since a backslash is a filename character on POSIX and so cannot be relied on to end anything.
    //       That last one is not a new residual; it is this clause acquiring one more spelling of "there
    //       was nothing here strong enough to stop at", which is why it is a widening of R1 (738 to 1017
    //       cells) rather than a new numbered residual. All four are one clause because they are one
    //       cause; the matrix
    //       classifies cells by the delimiter structure of the RENDERED string rather than by how the row
    //       was built, which is what showed these to be the same residual and not three. ("Rather than a
    //       NEW NUMBERED RESIDUAL" was written when the next free number was 4; R4 now exists and means
    //       something else, so the phrasing is by cause, not by index.)
    //       Not closable, and this is MEASURED rather than argued: admitting whitespace to branch 4's key
    //       -- the change that would close R1 -- turns tests RED, most of them prose rows in
    //       Redact_LeavesOperationalDiagnosticsVerbatim: "open /proc/self/fd failed: errno=13",
    //       "Error opening \"/tmp/file\": errno=13\"", "path /var/log/app: retries=5 attempts",
    //       "check /var/log then set retries=5" and both st_mode spellings. A residual's justification is
    //       the easiest thing in this file to write as an excuse, so it is pinned as a mutant: the cost of
    //       closing R1 is six operator diagnostics, and that number is reproducible rather than asserted. Safe in-tree
    //       because no genuine door on this backend surfaces a runtime path at all -- the confinement and
    //       not-found guards pre-empt with their own DescribePath render, and the syscall wrappers
    //       synthesize a path-free errno detail. Pinned by
    //       Redact_NoGenuineDoorOnThisBackendSurfacesARuntimePath. Tracked by #704/#708.
    //   R2  {key bearing BOTH a quote and whitespace} x {any right delimiter that is NOT a real path
    //       separator}. The errno=13 cell. Irreducible: `o'brien y=v'` and `file": errno=13"` are the SAME
    //       STRING SHAPE, so no recognizer can redact one and preserve the other -- re-admitting quotes to
    //       branch 3's key closes R2 and reopens errno=13 on both quote spellings, which is a trade, not a
    //       free win. Confined in practice to a message naming a partition DIRECTORY, since a data file
    //       appends "/part-<guid>.parquet" and so supplies the separator branch 1 needs. This is a
    //       monotonicity regression against 9220b66 and is tracked by #714.
    //
    // A DOCUMENTED OVER-REDACTION, recorded because "unstated" is how four fail-opens got into this file
    // and the same argument applies to losses in the safe direction. Branch 4 is anchored at $ and its
    // value class runs to the end of the message, so any prose sitting after an undelimited partition
    // segment is consumed with the value: "/tbl/name=Alice (errno 13)" renders as "name=<value>" and the
    // errno goes with it. This is over-redaction, not disclosure, and it is the direct price of closing
    // mechanism 3 -- an unanchored terminal branch stops mid-value and leaks, so the branch must reach the
    // end. It is bounded to the terminal segment: prose after a segment that HAS a right delimiter is kept,
    // which is what the trailing-prose rows in the B4-first table above measure. Not a residual, because
    // nothing survives that should have been removed; the entry exists so the next reader finds it
    // measured rather than discovering it.
    //
    // R3 -- {quote INSIDE the value} -- WAS a residual, was then declared CLOSED one round too early, and
    // is closed now. Admitting quotes to the value run closed it only for the follower class the corpus
    // happened to use (a LETTER after the interior quote); every other follower still partial-matched. The
    // matrix now ranges over the follower explicitly and asserts those cells FULL, so "R3 is closed" is a
    // measured fact rather than a claim.
    //
    // SCOPE THAT CLAIM TO ITS INPUT SET, because three independent censuses disagreed about it and all
    // three were right. Restricted to BALANCED quoting -- a framework message closes the quote it opens --
    // the class measures zero. Without that restriction it does not: unbalanced shapes still produce
    // quote-follower partials, and a census that admits them will report them. Neither result refutes the
    // other; they are answers about different input sets, and the difference is the restriction, not the
    // recognizer. So what is claimed here is the narrow thing: R3 is closed for the messages a framework
    // actually emits. The unbalanced shapes are a SEPARATE and narrower question, they are the "quoted
    // path whose quote nothing ever closes" clause of R1 rather than a live R3, and they have their own
    // matrix axis plus Redact_UnclosedOpeningQuote_RedactsTheQuotedRegionAndNothingElse. Stating the
    // unrestricted result as if it were the restricted one -- in either direction -- is precisely the
    // failure this comment block has had to retract three times. Note the failure mode this had: a residual declared closed was
    // checked in neither direction, while the two declared OPEN each had an anti-vacuity assertion. A list
    // that is only audited where it says "open" will keep drifting where it says "closed".
    //
    // R5 -- AND IT IS A PARTIAL RESIDUAL, NOT A DECLINE ONE. This entry previously read "NOT A RESIDUAL",
    // on the argument that a message opening a quote it never closes places the tail OUTSIDE the quoted
    // path, so redacting up to the first quote is the correct PARSE. That argument is still true and it is
    // still the right parse. It is also the wrong claim, because the question a residual list answers is
    // not "did we parse correctly" but "what can still be disclosed", and those come apart here:
    //
    //   stat failed on '/tbl/name=Boys' Club Holdings
    //     ->            stat failed on '/tbl/name=<value>' Club Holdings
    //
    // If the message was truncated, `Club Holdings` is prose and this is a full redaction. If the partition
    // value was legitimately `Boys' Club Holdings` -- an ordinary possessive, not an adversarial shape --
    // then a marker has been printed over a value of which half survives. THE TWO INPUTS ARE BYTE-IDENTICAL,
    // so no recognizer reading this string can tell them apart, and no closing rule exists: resolving the
    // ambiguity the other way would truncate every legitimately-quoted path at its first interior quote.
    // Irreducible, and accepted as such.
    //
    // What is NOT acceptable is the label. R1 and R2 are DECLINE clauses, and this file states that a
    // decline list cannot express a partial; filing this under R1 would assert no marker where the code
    // emits one. A reader auditing "are there partial matches?" would get the wrong answer from a list that
    // looks complete, which is a worse failure than an omission -- an omission invites a question and a
    // mislabel closes it. So it is stated separately, as a PARTIAL residual, and tracked by its own issue
    // rather than folded into a decline clause: #723.
    //
    // The census reports PARTIAL 0, and that figure is scoped to the reader's parse -- opening quote to the
    // first quote of that kind -- which is the parse the recognizer implements. Under the alternate reading
    // these cells are partial. Both statements are true of different input sets, the same scoping that
    // applies to R3, and neither refutes the other.
    //
    // Pinned by Redact_UnclosedOpeningQuote_RedactsTheQuotedRegionAndNothingElse for the parse, and by
    // Redact_InteriorQuoteInAValue_IsAnIrreduciblePartialResidual for the disclosure.
    //
    // ACCEPTED OVER-REDACTION, the safe direction, pinned in Redact_DelimiterAdjacentProse_IsAKnownAccepted-
    // OverRedaction: prose that places a key=value run before a later path separator now loses the value,
    // e.g. "stat /var/lib/x then set retries=5 for /tmp/y". Diagnosability only, never disclosure.
    //
    // The separator alphabet is DiagnosticText.HiveSeparatorPattern, the same definition DescribePath's
    // HiveSeparatorIndex scans for, concatenated in as a constant so the two recognizers cannot drift.
    // COST. The figures that used to sit here (~16 ms redacting / ~82 ms declining at 60,000 segments, with
    // a claimed linear scaling) are WITHDRAWN. They were measured before QuotedPathPrefix existed and they
    // no longer describe this recognizer, because a variable-length lookbehind changes the complexity class
    // rather than the constant. Re-measured on the form actually shipped here:
    //
    //   UNBOUNDED input, quote-free (the worst case: the backward scan finds no quote and runs to position
    //   0 from every start position) -- 49 KB in 0.65 s, 99 KB in 2.2 s, 209 KB in 3.5 s, 429 KB in 15.4 s,
    //   869 KB in 63.3 s. Doubling the input roughly quadruples the time, i.e. QUADRATIC, not linear. This
    //   was a ReDoS regression that the lookbehind introduced and that no earlier measurement here could
    //   have caught, since every earlier form scanned forward only.
    //
    //   BOUNDED at RedactScanLimit (what Redact actually feeds it) -- ~0.85 ms per call and CONSTANT, since
    //   the input can no longer grow: 4,096 chars x 100 iterations completes in 83 ms whatever the caller
    //   passed in. That constant is the only number worth trusting, and it is the reason the bound lives on
    //   the INPUT and not on the pattern (see Redact).
    //
    // Treat the bounded figure as an order of magnitude and not a budget; the unbounded row is recorded
    // only so the next person to relax RedactScanLimit knows what they are re-enabling. The "faster than
    // the form it replaces" claim once made here is withdrawn as unmeasurable -- any A/B compares a
    // source-generated matcher against an interpreted one.
    /// <summary>
    /// Value run and right anchor shared by the two QUOTE-DELIMITED branches of the recognizer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two independent decisions, and separating them is what took the partial-match count to zero without
    /// paying for it in diagnosability.
    /// </para>
    /// <para>
    /// THE VALUE RUN IS FULLY PERMISSIVE — it admits quotes. Four reviewers converged on this
    /// independently and they are right: excluding quotes from the value never protected prose (the KEY
    /// classes do that work), it only stopped the run at the first INTERIOR quote and let a bare
    /// <c>(?=['"])</c> be satisfied by it. Spark's <c>escapePathName</c> does not escape an apostrophe and
    /// <c>add.path</c> is foreign, so <c>name=O'Brien Household</c> is a real partition value, not a
    /// hypothetical.
    /// </para>
    /// <para>
    /// THE RIGHT ANCHOR IS STRONG — the quote must be followed by a separator, whitespace, end of message,
    /// or terminal punctuation. Relaxing the value run WITHOUT this, which is the literal form the reviewers
    /// prescribed, re-opens the class it was meant to close: greedy backtracking settles on the LAST quote
    /// in the segment, and when that quote is an interior one because the path was never quoted at all,
    /// <c>&lt;table-root&gt;/name=O'Brien Household</c> emits a marker over <c>O</c> and leaves
    /// <c>'Brien Household</c> standing.
    /// </para>
    /// <para>
    /// Both halves are pinned by mutation rather than by a census in this comment, which is the policy the
    /// residual block below explains: reverting the value run to a quote-excluding class, and reducing the
    /// anchor to a bare <c>(?=['"])</c>, each turn the suite RED — the first on
    /// Redact_ApostropheBearingPartitionValue_IsFullyStrippedAndKeepsTheProse and the matrix census, the
    /// second on the unquoted-terminal row of that same test. If either claim here stops being true, a test
    /// says so.
    /// </para>
    /// <para>
    /// Defined once because two branches use it and a divergence between them is the exact failure this
    /// recognizer has now had four times.
    /// </para>
    /// </remarks>
    private const string ClosingQuoteValue = @"[^/]*(?=\k<oq>(?:[/\\\s]|$|[.,:;)\]]))";

    /// <summary>
    /// Left precondition for the two QUOTE-DELIMITED branches: the path must actually be QUOTED.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A quote is only a delimiter if something opened it. Without this, ANY quote inside a value that
    /// happens to be followed by a boundary character satisfies <c>ClosingQuoteValue</c>, and an ordinary
    /// partition value truncates mid-string:
    /// <c>/tbl/email=Boys&#39; Club.taylor%40example.com</c> emits a marker over <c>Boys</c> and leaves
    /// the rest standing. That is a PARTIAL match, which this recognizer may not produce.
    /// </para>
    /// <para>
    /// Variable-length lookbehind plus a backreference is the whole mechanism: capture the opening quote,
    /// require no intervening quote between it and the current segment, and require the CLOSING quote to
    /// be the SAME character. When the path is unquoted the branches decline and the end-of-message branch
    /// consumes the value in full, which is why closing this cost no coverage.
    /// </para>
    /// </remarks>
    // EVERY SITE THE SEPARATOR ALPHABET IS SPELLED, AND WHAT EACH ONE DOES WITH A BACKSLASH. Two rounds
    // running, this rule was applied where a defect was measured rather than at every position the
    // property has to hold, and each time the next reviewer found the position that was missed. So the
    // sites are enumerated, and a site that legitimately differs says why:
    //
    //   1. value classes, all six branches       [^/]*            admits \ -- a value may contain one
    //   2. branch 1 right anchor                 (?=/)            excludes \ -- it must not END a value
    //   3. ClosingQuoteValue right anchor        \k<oq>(?:[/\\\\s]...) admits \ AFTER the closing
    //                                            quote, where it is past the value and cannot truncate it
    //   4. branches 1 and 4 left anchor          (?<=[/\\])       admits \, guarded (below)
    //   5. QuotedPathPrefix left anchor          [/\\]           admits \, guarded (below)
    //   6. key classes                           [^/\\=...]      exclude \ -- a key never spans one
    //   7. DiagnosticText.PathSeparators         { /, \\ }        splits on \, guarded by the
    //                                            hiveInBackslashRun latch AND by suppressing key harvest
    //                                            inside a latched run
    //   8. RootlessPathValue right anchor        (?=/[^/]*...)    excludes \ -- like site 2, a
    //                                            backslash must not stand in for the slash that proves
    //                                            this is a path
    //   9. PathRegionStart left anchor           (?<![^/\\""'\s])     admits \, guarded (below) -- the
    //                                            third left-anchor site, added with branch 5
    //  10. NoSeparatorEarlierInSegment           (?<!(?:^|/)[^/]*=[^/]*) spells / THREE TIMES and \ not at
    //                                            all: the guard's notion of "segment" is deliberately the
    //                                            FORWARD-SLASH segment, because a backslash-delimited run is
    //                                            exactly what it exists to look across
    //  11. RootlessBackslashPathValue            (?=[^/]*\\)[^/]*$      requires a \ ANYWHERE as
    //                                            evidence, then runs to end of input; the only site where a
    //                                            backslash is the evidence rather than a hazard
    //
    // THIS TABLE IS NOW EXECUTED, because it went stale three times in three rounds and a prose table
    // asserting completeness is a test that never runs. See
    // PathDisclosureHygieneTests.SeparatorBearingConstants_AllHaveARowInTheTotalityTable: it reflects over
    // this type's private string constants, selects the ones spelling a separator, and requires the name set
    // to equal the table's. It covers rows 3, 8, 9, 10 and 11 -- the EXTRACTED constants, which are the rows
    // that have historically gone missing. Rows 1, 2, 4, 5 and 6 are written inline in the [GeneratedRegex]
    // attribute and row 7 lives in the sibling, so those remain prose obligations; that limit is written
    // down rather than left implied, because a completeness check that quietly is not one is the defect
    // this whole table exists to prevent.
    //
    // ROW 10 WAS MISSING FROM THE DAY THE GUARD WAS WRITTEN, for a third consecutive round of this table
    // being incomplete, and it was found by grepping for the alphabet rather than by reading the table.
    // Row 11 is new in the same commit and was written INTO the table before the constant was declared,
    // which is the order this table only becomes reliable in once the check runs.
    //
    // SITE 8'S MISSING ROW WAS THE LEAKING CONSTRUCT. That is not a coincidence and it is the strongest
    // argument for keeping this table honest. Before the round that added these entries, site 8's anchor
    // read (?=/[^/\s]*...) -- it excluded WHITESPACE from the segment after the value, so a file name with
    // a space in it made the whole branch decline and a rootless partition value went out in full. Had the
    // site been enumerated, its disposition would have had to be written down as "excludes whitespace in a
    // segment the value does not live in", and that sentence does not survive being read. The documentation
    // gap and the disclosure were the same omission at two layers, found by two different reviewers, and
    // they are closed in the same commit.
    //
    // SITES 8 AND 9 WERE MISSING FOR TWO ROUNDS. Branch 5 introduced both, one edit updated entry 1 to say
    // "all five branches", and the two new constants were never given rows. A reviewer swept for stale
    // claims here and reported the table consistent, because it IS consistent: the header says seven and
    // seven rows follow. A COUNT-CHECK CANNOT DETECT A MISSING ROW -- the observable is identical whether
    // the table is complete or not, which makes a totality claim the one kind of prose that cannot be
    // audited by reading it. The only check that discriminates is the one that goes the other way: grep
    // the file for separator alphabets and confirm every occurrence has a row. TWO reviewers swept this
    // table in the same round and each found ONE of the two missing entries -- one the left anchor, one the
    // right. Neither sweep was wrong and neither was complete, which is the same partial-sweep pattern the
    // table exists to prevent, arriving in the audit of the table rather than in the code. That is the check to run
    // when a branch is added, and it is stated here because the table's own purpose -- preventing a rule
    // applied at some positions and not others -- was defeated at exactly the position it did not list.
    //
    // Site 7 is the other recognizer and it needed the guard twice: the latch stopped a value being echoed
    // as a FILE NAME, and a second round found the same value still being echoed as a COLUMN NAME by the
    // key harvester. Suppressing one echo of a token says nothing about the others.
    //
    // THE LEFT ANCHOR NEEDS THE SAME POSIX RULE AS THE RIGHT ONE, BUT NOT THE SAME REMEDY.
    // A backslash is an ordinary filename character on POSIX (see the block above), so it cannot be
    // trusted to END a value -- that is why every value class is now [^/]*. The mirror-image question is
    // whether it may START a key, and the naive uniform answer, "no, drop it from the left anchors too",
    // is WRONG. Measured: dropping it turns C:\\warehouse\\tbl\\email=alice@example.com\\part-1.parquet
    // from a full redaction into a DECLINE, because a Windows-shaped message contains no forward slash to
    // anchor on at all. That is not a conservative outcome; on a Windows or PVC host it surrenders every
    // partition value in the deployment. The rule is not "a backslash is never a separator" -- it is that a
    // backslash may never RESOLVE THE PLATFORM GUESS TOWARD DISCLOSURE, and which way that cuts depends on
    // the syntactic position, because the DIRECTION OF THE ERROR differs:
    //
    //   right anchor / value class : guessing "separator" ends the value early -> the tail SURVIVES -> PARTIAL.
    //   left anchor                : guessing "not a separator" only fails to start -> DECLINE, never PARTIAL.
    //   left anchor                : guessing "separator" invents a segment INSIDE a value, and the synthetic
    //                                sub-key is then echoed verbatim by ${key} -> PARTIAL.
    //
    // So the left anchor has a leaking guess too, just a different one, and it needs a narrower remedy than
    // exclusion. This is it: a backslash may open a segment only while no Hive separator has yet appeared in
    // the current forward-slash segment. Once one has, everything further along that run is value content and
    // no sub-key inside it is real. That is the identical rule DiagnosticText.DescribePath latches with
    // hiveInBackslashRun, arrived at from the other recognizer, and the two halves now agree on it by
    // construction rather than by coincidence.
    //
    // The guard costs nothing at a slash-opened position: neither [^/]* may cross the slash, so the pattern
    // can never end immediately after one. It therefore constrains exactly the backslash arm while being
    // written once, which is the point -- the previous defect was a rule applied at some positions and not
    // others. It over-declines on one documented shape: prose carrying an equals sign with no slash between
    // it and a Windows path ("retries=5 then check C:\\tbl\\name=..."). That is the safe direction and it
    // is asserted, not assumed, by Redact_ProseEqualsBeforeAWindowsPath_DeclinesRatherThanInventingAKey.
    // THE `^` ALTERNATIVE IS LOAD-BEARING, AND THE WAY I NEARLY LOST IT IS THE LESSON. The guard scans
    // back to the start of the current forward-slash segment; on a Windows-shaped message there is no
    // forward slash anywhere, so `^` is the ONLY scan origin and without it the guard stops guarding
    // exactly the shape the Windows cost argument above makes load-bearing. Deleting it reopens 138
    // census cells as PARTIAL -- C:\\warehouse\\my col=<value>\\<value>=<marker> with two sentinels
    // surviving under a marker -- and is pinned by Redact_WindowsRootWithTwoSeparators_...
    //
    // I reported this alternative as unpinned-but-harmless on the strength of a mutant that WIDENED the
    // guard's start set and changed no cell. That was true and it was worthless: widening a guard can only
    // make the recognizer more conservative, so it can never demonstrate the guard is load-bearing. A
    // mutation run in the direction that cannot leak is not evidence about the direction that can. Two
    // rules come out of it, and both generalize past this line:
    //
    //   - EQUIVALENCE IN THE SAFE DIRECTION IS NOT EVIDENCE OF PINNING. Mutate toward disclosure.
    //   - INTRODUCING A NEW CONSIDERATION INVALIDATES EVERY PRIOR EQUIVALENCE MEASUREMENT WHOSE CORPUS
    //     DOES NOT RANGE OVER IT. The commit that added the Windows cost argument is the commit that made
    //     a slash-free root relevant, and the equivalence was measured in that same commit on a corpus
    //     that predated the argument. A 0-RED result carried across a change of argument measures the old
    //     world. The corpus and the argument have to move together.
    // MECHANISM 7 AND THE BRANCH THAT CLOSES IT. add.path is RELATIVE by the Delta protocol, so a message
    // can carry a partition path with nothing before the first key. That first segment has no left
    // delimiter and could never match, while the SECOND segment still did -- printing a marker over an
    // unredacted head, the worst reading available, because the reader is told a value was removed and one
    // was, just not that one:
    //
    //   email=alice%40example.com/region=<value>/part-0.parquet
    //
    // The left anchor exists to prove we are in a PATH rather than in prose, since key=value is also what
    // an operator diagnostic looks like. A preceding separator is that proof, and a rootless segment has
    // none -- so the proof has to come from the right instead. My first attempt simply weakened branch 1's
    // left anchor, on the reasoning that branch 1 already requires a following slash. A following slash is
    // NOT sufficient, and ordinary prose says so:
    //
    //   compression ratio=1/2 rejected      ->  ratio=<value>/2 rejected
    //   mount option uid=1000/gid=1000 ...  ->  uid=<value>/gid=<value>
    //   quota=80/100 exceeded               ->  quota=<value>/100 exceeded
    //
    // Eight prose rows did not contain a single key=N/M, so the corpus that exists to protect operator
    // diagnostics could not see it. So the weak-left case is its own branch with a STRONGER right anchor
    // than branch 1 has, rather than a relaxation of branch 1 -- rooted behaviour is untouched, and the
    // ladder stays honest: the strength a branch needs at one end depends on what it already has at the
    // other.
    //
    // THE DISCRIMINATOR THAT ANCHOR FIRST USED WAS FALSE, and it is worth the space because it was false
    // about the domain rather than about the regex. It read: a PATH segment contains no whitespace and
    // ends at a separator, a quote or end of input, whereas prose resumes with a space. Parquet file names
    // contain spaces. `part 0.parquet` is an ordinary name, and one space ONE SEGMENT DOWNSTREAM of the
    // value made the whole branch decline:
    //
    //   k=SENTINEL/part-0.parquet   ->  k=<value>/part-0.parquet     redacted
    //   k=SENTINEL/part 0.parquet   ->  k=SENTINEL/part 0.parquet    ECHOED IN FULL
    //
    // That is not a residual, it is an ATTACKER-SELECTABLE OPT-OUT: add.path is foreign, so the writer of
    // the poisoned path chooses whether the recognizer runs by choosing a file name. This file's own rule
    // -- a shape the recognizer declines is a redaction the attacker opted out of by choosing it -- has no
    // exception for shapes that are expensive to close. So the downstream whitespace class is gone and the
    // right anchor is (?=/[^/]*(?:[/'"]|$)).
    //
    // AND THAT CLOSURE IS TOTAL, NOT NARROWED, WHICH IS A STRUCTURAL ARGUMENT AND NOT A CELL COUNT. Once a
    // `/` follows the value, [^/]* cannot cross a separator, so the run after that slash always terminates
    // at either a later `/` or `$` -- the alternation (?:[/'"]|$) is therefore SATISFIABLE BY CONSTRUCTION
    // for every input of this shape. There is no residue of the whitespace class left to find, and no
    // downstream character class remains that an attacker could steer. This is worth more than the cell
    // counts that accompany it: a census says no cell in this corpus opts out, whereas this says no cell
    // CAN. Corpora are how the defects here were found; arguments of this shape are how a class is closed.
    //
    // THE COST, measured and pinned rather than asserted to be small: four rootless key=N/M prose rows
    // move from the verbatim corpus to Redact_DelimiterAdjacentProse_IsAKnownAcceptedOverRedaction, with
    // their exact rendered text. `compression ratio=1/2 rejected` becomes `ratio=<value>/2 rejected` --
    // the key, the denominator and the surrounding prose survive and a numerator is lost. The trade is a
    // numerator in a rootless framework message against a partition value echoed in full, and it is only
    // a trade at all because both readings of `key=value/token token` are genuinely available; there is no
    // third option that keeps both, and I looked for one before taking the cost.
    //
    // THE GENERAL FORM, since this is the fourth time this recognizer and DescribePath have disagreed:
    // A CLASS THAT NARROWS A RECOGNIZER ON EVIDENCE THE ATTACKER SUPPLIES IS A SWITCH THE ATTACKER OWNS.
    // The four drifts were the fragment alphabet, the search strategy, the separator alphabet, and this --
    // the whitespace class of a segment the value does not even live in. Each was found by a different
    // reviewer's corpus, never by reasoning, which is why the two recognizers are now compared cell by
    // cell over the whole corpus rather than at the positions a defect was last measured.
    //
    // A FIFTH drift followed anyway, at the R4 boundary below, and the cell-by-cell comparison did not see
    // it -- because the comparison EXCUSES any divergence that lands in a filed residual, and the predicate
    // naming that residual was wider than the residual itself. Comparing two recognizers everywhere is not
    // enough if the disagreements are triaged by a predicate nobody checked against the text it models.
    //
    // Its key class also excludes whitespace. A rootless key is the weakest evidence in the file and does
    // not get the permissive key class as well. That exclusion is DEFENCE IN DEPTH AND IS NOT CLAIMED AS
    // PINNED: admitting whitespace to it turns no test red, and I could not build a row that discriminates,
    // because PathRegionStart opens after any whitespace, so a strict key simply starts at the last space
    // and the echoed ${key} comes out identical either way. Per the rule recorded at the segment guard, a
    // mutation in the over-redacting direction proves nothing about pinning, and "no test failed" is not a
    // licence to call it equivalent -- it is recorded as unpinned so the next reader knows which it is.
    //
    // What it deliberately does NOT do is match a bare key=value with no slash at all. That is residual R4:
    // indistinguishable from errno=13 by any means available in the string, and redacting it would destroy
    // the diagnostics this recognizer is built to preserve.
    //
    // R4 IS NARROWER THAN EVERY CLASSIFIER THAT MODELLED IT, AND THE GAP WAS A LIVE DISCLOSURE.
    // Read the sentence above literally: the justification is INDISTINGUISHABILITY, and the qualifying
    // phrase is "by any means AVAILABLE IN THE STRING". A backslash is such a means. `errno=13` has none;
    // DescribePath reaches a FULL redaction on `k=SENTINEL\part.parquet` using nothing else; and this
    // recognizer already accepted a backslash as evidence when the path was ROOTED, redacting
    // `C:\t\k=SENTINEL\part.parquet` via branch 4. Accepting the same separator rooted and rejecting it
    // rootless was an internal inconsistency, not a principled decline -- sibling drift #5, and a value
    // echoed in full on any relative add.path written by a Windows or PVC writer.
    //
    // HOW IT SURVIVED FIVE REVIEWS. This block says "no slash at all". Every classifier written against it
    // -- three independent corpora, five reviewers, and the outsideBoth == 0 assertion added one round
    // earlier for the express purpose of stopping over-wide claims -- said "no FORWARD slash". A predicate
    // WIDER than the residual it models does not merely fail to catch the cell: it files the cell under a
    // residual that has been argued irreducible, which converts a finding into an excuse and does it
    // silently. An omission invites a question; a MISLABEL closes it.
    //
    // Branch 6 closes it, and R4 keeps only what its own sentence describes: a key=value carrying no
    // separator evidence ANYWHERE. `k=v` and `k=v.parquet` still decline. `k=v\` does not.
    //
    // R6 IS CLOSED, AND IT WAS THE THIRD ATTACKER-SELECTABLE OPT-OUT. Branch 6 first read
    // `(?=[^/]*\\[^/])`, requiring the backslash to have CONTENT AFTER IT, which spared the prose row
    // `st_mode=0100644\`. That one character was a switch: a value ending AT its backslash declined and
    // echoed in full, so the author of a poisoned `add.path` opted out of redaction by ending the path at a
    // separator -- which is the natural spelling of a partition DIRECTORY. The sixth drift, introduced by
    // the commit that fixed the fifth, in the construct that fixed it.
    //
    // THE RULE THAT SETTLES IT IS ALREADY IN THIS FILE, one paragraph down, and it points the other way
    // from where it was applied. Branch 6 consumes its tail because on POSIX a `\` is an ordinary filename
    // character, so a value may legitimately END with one and cutting there would emit a marker over a
    // partly-consumed value. The same premise forbids a trailing `\` from being the thing that DENIES
    // evidence: it cannot simultaneously be an ordinary character too weak to bound a value and a signal
    // strong enough to prove the string is prose. That is why the terminal case was a bug rather than a
    // second, symmetric asymmetry -- one premise, applied consistently, decides both.
    //
    // ITS REACHABILITY WAS DISPUTED, AND THE DISPUTE IS RECORDED AS MOOT RATHER THAN RESOLVED. One reviewer
    // held that no door presents the bare shape, because Redact is only ever handed a path already made
    // ROOTED, so `k=QQSENTINELQQ\` renders `Could not delete file <table-root>/k=<value>` -- clean, the root
    // supplying the evidence the string lacks. Another held that a foreign `add.path` naming a partition
    // directory ends at its separator by nature. Both measurements reproduce; the fix was one character
    // wide, and this file has already been wrong twice about what an attacker can reach. Settling a
    // reachability argument is worth less than the round it costs whenever the fix is cheaper than the
    // argument, and betting a disclosure on the caller's habits is the coupling the residual was filed to
    // name in the first place. Both layers now hold independently -- the door roots the path AND the
    // recognizer redacts the bare string -- which is what keeps the dispute moot instead of load-bearing.
    //
    // A CANDIDATE FOURTH OPT-OUT, RECORDED AS A CANDIDATE AND DECLINED -- read this before hunting one.
    // The strongest shape anyone has found that still survives is the QUOTED ROOTLESS SINGLE SEGMENT:
    //
    //     Could not find file 'email=SENTINEL'.        survives in full
    //     Access to the path 'email=SENTINEL' is denied.  survives in full
    //     'email=SENTINEL/p.parquet'  '/tbl/email=SENTINEL'  'email=SENTINEL\'   all redact
    //
    // It is DECLINED, and it is genuinely R4 rather than an excuse: the message carries no separator in
    // either alphabet, so by the residual's own sentence nothing in it proves the token is a path. The
    // quotes are the only candidate evidence, and admitting a bare quote re-breaks the pinned prose row
    // `Error opening "/tmp/file": errno=13"` -- which is the row branch 3's whole right-anchor design
    // exists to preserve. Verified at this HEAD: that row is still verbatim.
    //
    // THE NARROWING THAT WOULD REACH IT, if it is ever needed, is an opening quote IMMEDIATELY ABUTTING
    // `key=` -- `'email=` -- which the errno row never is, because its quote sits several tokens to the
    // left. That is a real distinction and it is written down here for one reason: the next person hunting
    // opt-outs will re-derive this shape, and without the cost stated they will either file it as a
    // disclosure it is not, or implement quote-as-evidence and discover the errno row the hard way.
    // Naming what a fix would COST is the part of a decline that keeps it from being an excuse.
    //
    // AND THE DIRECTION OF EVERY DRIFT SO FAR IS ITSELF A FINDING. Across a separator-agnostic census of
    // 3,808 cells, every single divergence between this recognizer and DescribePath ran the same way:
    // describe=SUPPRESSED, redact=ECHOED. Six drifts, six times the pattern-matcher was the weaker of the
    // two, and never once the splitter. That is not luck -- DescribePath decides by SPLITTING on a
    // separator set, so a new shape is a new segment and the default is to suppress; Redact decides by
    // MATCHING, so a new shape is an unmatched one and the default is to pass through. A structure whose
    // failure mode is "emit the input" will drift toward disclosure every time, which is why the sibling
    // census exists at all, and why the review effort belongs HERE rather than split evenly between them.
    //
    // THE COST, MEASURED: exactly one prose row, `st_mode=0100644\`, which moves to the accepted
    // over-redaction corpus and loses a trailing separator off a mode bitmask. Flipping the character
    // breaks five tests; the other four are expectations encoding the old behaviour. Against a value going
    // out in full the trade is not close, and it is the same trade taken one round earlier when the
    // whitespace-reachability narrowing was rejected: pay in over-redaction rather than leave a switch.
    // The census moved only toward FULL -- 135 cells in the monotonicity matrix, 29 in the tail axis, with
    // R1, R2 and R4 all shrinking and nothing moving the other way.
    //
    // "Not preceded by anything that is not a separator, quote, or whitespace" also succeeds at position
    // zero, so one lookbehind covers rootless, post-quote and post-space openings alike.
    private const string RootlessPathValue = @"[^/]*(?=/[^/]*(?:[/'""]|$))";

    /// <summary>
    /// Right anchor for a rootless path whose only separator evidence is a BACKSLASH.
    /// </summary>
    /// <remarks>
    /// <para>The value runs to the end of the message, exactly as branch 4's does, rather than stopping at
    /// the backslash: on POSIX the backslash is an ordinary filename character, so everything after it is
    /// still the same component's VALUE, and stopping there would emit a marker over a partially consumed
    /// value -- partial-match mechanism 6, which every value class in this file exists to prevent.</para>
    /// <para>The evidence required is a backslash ANYWHERE, including a trailing one. Requiring content
    /// after it was the third attacker-selectable opt-out; see the R6 paragraph above for why the same
    /// premise that makes this branch consume its tail also forbids a trailing backslash from denying
    /// evidence.</para>
    /// <para>WHY THE TWO SEPARATORS ARE HANDLED DIFFERENTLY, which is the question every reader of a file
    /// with five recorded drifts will ask on sight. Branch 5 lets the tail SURVIVE
    /// (<c>k=&lt;value&gt;/a/b (errno 13)</c>); branch 6 consumes it. That asymmetry in the code is what
    /// makes the two cases SYMMETRIC UNDER THE INVARIANTS: a <c>/</c> is a component boundary on every
    /// platform, so the value provably ends there and what follows is not value content; a <c>\</c> is an
    /// ordinary POSIX byte, so stopping there would emit a marker over a PARTLY-CONSUMED value, which is an
    /// invariant 1 violation. Consuming to end of input is then the only option that satisfies invariant 3,
    /// FULL or DECLINE and never PARTIAL. Treating the two separators identically is what would be
    /// asymmetric -- it would apply one rule to a boundary and the same rule to a byte.</para>
    /// <para>MEASURED, NOT ONLY ARGUED. Substituting <c>[^/\\]*</c> for the value run -- i.e. stopping at the
    /// backslash -- turns 32,193 cells of the guard corpus that this recognizer renders CLEAN into
    /// mechanism-6 partials of exactly the predicted shape, <c>email=&lt;value&gt;\QQSENTINELQQ</c>: a marker
    /// standing beside the very run it claims to have replaced. So consuming to end of input trades
    /// disclosure for over-redaction deliberately, and the trade is forced by invariant 3 rather than
    /// incidental. Two reviewers reached the same conclusion independently, one analytically and one by
    /// measuring the counterfactual on its own corpus.</para>
    /// <para>The evidence class is deliberately NOT narrowed to "a backslash reachable without crossing
    /// whitespace", which would spare the prose row <c>retries=5 then check C:\\tbl\\name=...</c> from
    /// collapsing to <c>retries=&lt;value&gt;</c>. A space inside the value is ATTACKER-SUPPLIED, so that
    /// narrowing is a switch the attacker owns -- <c>k=a b\\c.parquet</c> would decline and echo -- which is
    /// the defect closed one round earlier in <see cref="RootlessPathValue"/>. The cost is paid instead as
    /// over-redaction, in the corpus that already records over-redaction of delimiter-adjacent prose.</para>
    /// </remarks>
    private const string RootlessBackslashPathValue = @"(?=[^/]*\\)[^/]*$";

    private const string PathRegionStart = @"(?<![^/\\""'\s])";

    // WHICH SITES OF THIS GUARD ARE LOAD-BEARING, MEASURED PER SITE RATHER THAN AS A WHOLE. Deleting the
    // guard from one branch at a time gives three different answers, and the differences are the useful
    // part. MEASURED-AT the commit that added branch 6, and not re-run since -- the counts below are facts
    // about that tree, not about the one you are reading:
    //
    //   branch 4 (terminal)     RED 10, 738 cells   load-bearing outright
    //   quoted prefix           RED  3, 198 cells   load-bearing outright
    //   branch 1 (slash-right)  GREEN               MASKED, not subsumed -- see below
    //   branch 5 (rootless)     GREEN               masked the same way, by branch 1
    //   branch 6 (rootless \)   GREEN               added later; see the RE-MEASUREMENT below
    //
    // ...and the row branch 6 never got when it was added, which is the omission this table exists to
    // prevent and committed anyway. It is GREEN, and for the same reason every other site is now green:
    // branch 6 itself masks them. The whole profile is measured in the test named below.
    //
    // THE TABLE ABOVE IS THE OLD MEASUREMENT AND THE WHOLE ROW SET IS NOW SUPERSEDED -- kept because the
    // way it went stale is the finding. The guard is spelled FIVE times in source (branches 1, 4, 5 and 6
    // directly, plus once inside QuotedPathPrefix) and SIX times in the compiled pattern, because
    // QuotedPathPrefix is used by branches 2 and 3. This table said FOUR, and branch 6 had no row at all,
    // for the same reason and in the same commit as every other stale structural count here.
    //
    // RE-MEASURED AFTER BRANCH 6, AND THE ANSWER CHANGED FOR EVERY SITE AT ONCE. MEASURED-AT 83b88dc and
    // at the commit that added branch 6; not re-run since. At 83b88dc, dropping the
    // guard's `^` origin killed 6 tests and dropping its `/` origin killed 5. At the commit that added
    // branch 6, both dropped to ZERO -- and deleting the guard from ALL SIX compiled sites is now
    // output-identical over 168,000 corpus cells. It is not inert: delete branch 6 as well and the guard
    // immediately regains its teeth in the DISCLOSURE direction, harvesting a sub-key from inside the value
    // and echoing it through ${key}. Masking, not equivalence.
    //
    // THE SIXTH CAUSE, RUNNING BACKWARDS, AND IT ARRIVED IN THE COMMIT THAT NAMED THE SIXTH CAUSE. Below,
    // TIME is recorded as "an equivalence certified before its masker existed". This is the mirror image:
    // A NEW BRANCH SILENTLY UN-PINNING AN EXISTING GUARD. It is the more dangerous orientation, because the
    // certification was correct when made AND the guard is still load-bearing -- only its PIN is gone, so
    // nothing fails and nothing tells you. Hence the rule, which is the operational form of the sixth
    // cause: ANY COMMIT THAT ADDS A BRANCH TO THE ALTERNATION MUST RE-RUN EVERY GUARD-SITE MUTATION,
    // because a new branch can absorb another guard's failures without touching it. This is the second time
    // an alternation change has invalidated a prior mutation result.
    //
    // AND THE RE-RUN IS NOW AUTOMATIC, because a rule that depends on someone remembering it is the same
    // artifact as a prose table asserting completeness. See
    // PathDisclosureHygieneTests.SegmentGuard_IsSubsumedByTheRootlessBranches_ButOnlyWhileTheyExist: it
    // takes the SHIPPED pattern text, deletes the guard from it at runtime, and asserts both halves --
    // output-identical while branch 6 exists, and materially different (with a sub-key echo) once branch 6
    // is removed. It also asserts the site count, so the number in this paragraph is checked by the build
    // rather than maintained by hand. The day the guard stops being subsumed, that test fails and says so,
    // which is what a comment reading "currently masked" could never do.
    //
    // Branch 1 measures green because the regex engine scans left to right and branch 5 reaches the REAL
    // key at an earlier start position, so branch 1 never gets offered the synthetic one. Delete branch 5
    // as well and branch 1's guard becomes load-bearing immediately.
    //
    // THAT SENTENCE IS STILL TRUE. THE EVIDENCE IT USED TO CITE IS NOT, AND THE ROW READ AS PLAUSIBLE
    // THROUGHOUT. It used to end "RED 5, and the partials are the BACKSLASH class" -- MEASURED-AT the
    // commit that wrote it. Deleting branch 5 now fails a large set of tests, and turning branch 1's guard
    // off on top of that fails exactly the same set -- increment zero, no backslash partials. The row's claim was re-derived at the level of the recognizer
    // instead, and it holds: branch 1's site goes from 0 to 1,152 differing cells the moment branch 5 is
    // removed, every one of them a sub-key echo.
    //
    // SATURATION, NOT MASKING -- CAUSE TWO, NOT CAUSE FOUR, and the distinction is not pedantry because the
    // two have opposite remedies. Masking is repaired by re-pinning against the masker; saturation is
    // repaired by measuring something finer-grained than a pass/fail count. A reviewer reading only the
    // suite delta reached the masking diagnosis and named branch 6 as the masker. Branch 6 is measurably
    // NOT the masker here: the DIFFERING CELLS are byte-identical with branch 6 present and absent, which
    // is asserted by set equality rather than by comparing two counts. The backslash partials the row cited
    // are gone for an unrelated reason -- branch 6 now redacts that class outright.
    //
    // AND THE GENERAL LESSON, WHICH IS WHY THIS ROW IS NOW A TEST: a row whose evidence is "N tests go red"
    // is only as good as the corpus behind N, and it rots the moment anything ELSE in the file starts
    // failing first. Rows stating a count of the code's own shape rot VISIBLY -- someone eventually counts.
    // Rows stating a RED delta rot INVISIBLY, and keep reading as plausible while doing it. Every row of
    // this table was of the second kind.
    //
    // THE SWEEP WAS BUILT FOR ONE TABLE AND NOT RUN ON THE OTHER, IN THE SAME COMMIT. The every-site sweep
    // exists forty lines up, for the separator alphabet, and was created by the commit that left this table
    // with a missing row and a stale reason. That is this recognizer's own recurring defect -- the rule
    // applied where a defect was measured rather than at every position the property has to hold -- applied
    // to the artifact built to prevent it. Both tables are now executable:
    // PathDisclosureHygieneTests.SegmentGuard_PerSiteProfile_IsMeasuredAtThisHeadRatherThanDescribed
    // measures every site under every branch set and pins the whole profile, so a row cannot go stale in
    // its reasoning while remaining plausible.
    //
    // AND THAT PROFILE IS THE RE-PIN THE UN-PINNING ASKED FOR, which is the part worth reading twice. The
    // two halves of this guard's origin set lost their individual killers when branch 6 landed.
    // MEASURED-AT 83b88dc and at the commit after it; not re-run since: dropping `^` killed 6 tests and
    // dropping `/` killed 5; at the next commit both killed zero. Neither
    // half is pinned by the whole-guard subsumption test either, because as SHIPPED the guard is inert, so
    // any edit to it is output-identical. The per-site profile restores both pins without changing the
    // recognizer at all, by measuring the guard where it still has an effect:
    //
    //   drop `^` from the origin set   quoted-prefix site, without branch 6: 480 cells -> 96    RED
    //   drop `/` from the origin set   quoted-prefix site, without branch 6: 480 cells -> 384   RED
    //   add `\` to the origin set      whole profile unchanged                                  GREEN
    //
    // The third is recorded because it is GREEN and should be: widening the set of positions an earlier
    // separator may sit at only makes the guard decline MORE, which is the safe direction, and equivalence
    // in the safe direction is not evidence of pinning. A mutation score is only meaningful when the mutant
    // moves toward DISCLOSURE. Both mutants that do are now killed on a measured number rather than on a
    // pass/fail count, which is also why the guard's text is read out of this class by reflection in the
    // test rather than transcribed there: an earlier draft transcribed it, and both mutants then failed on
    // "expected 6 sites, actual 0" -- a report that the copy had drifted, carrying no information about
    // behaviour at all.
    //
    // So the guard stays on ALL FIVE SOURCE SITES, and none of them is removed on the strength of the
    // subsumption above. "Correct only while some other branch is present" is not a property a recognizer
    // should rely on silently, and it is now not relied on silently: it is asserted, with its own
    // dependency stated in the assertion. A single-site mutation score is a statement about the CURRENT
    // branch set, not about the site -- which is exactly why the site count and the subsumption are both
    // executed rather than described.
    //
    // AND A SIXTH CAUSE OF A 0-RED SURVIVOR, which is the one none of the other five is: TIME. An
    // equivalence can be certified correctly and become false when a LATER commit adds the masker. A
    // reviewer paired branch 1's guard with branch 4 and certified a genuine equivalent; the real masker is
    // branch 5, which did not exist when the pairing was run. The classification was right when made. The
    // other five causes -- mis-scoped mutant, saturated corpus, safe-direction mutation, masking, and a
    // corpus that predates the argument -- are all properties of the ANALYSIS, and can be found by
    // re-reading it. This one cannot: nothing about the measurement is wrong, and no amount of re-reading
    // reveals it. Only re-running does.
    //
    // WHICH MAKES "RE-DERIVE EVERY EQUIVALENCE CLAIM WHEN A BRANCH IS ADDED" A STANDING OBLIGATION, of the
    // same shape as the separator-alphabet sweep above, and it is easier to skip because each individual
    // claim still reads correctly. The two claims in this file that predate branch 5 were re-derived
    // against it rather than assumed to survive:
    //
    //   * "dropping branch 2 leaves the matrix green, because branch 4 then consumes the same values"
    //     (~40 lines up). Still true, and still for that reason: deleting branch 2 costs exactly one
    //     over-redaction row and no matrix cell, and deleting branch 2 AND branch 5 together costs only
    //     branch 5's own rows on top. Branch 5 is not a second masker here -- the two mutants' failure sets
    //     are disjoint, which is what distinguishes independence from masking.
    //   * "re-bounding the key quantifier to {0,512} left the entire suite green" (below the regex).
    //     RE-DERIVED AND THE WORDING WAS STALE -- it said "on both branches" and there are five. Re-run
    //     across all five: red only in Recognizer_StripsTheValueAtAnyKeyLength_BelowTheDetailCap. The
    //     finding it records is intact and is now BETTER supported than when written: the test that exists
    //     because the property was invisible behind the cap is the test that catches it.
    //
    private const string NoSeparatorEarlierInSegment =
        @"(?<!(?:^|/)[^/]*" + DiagnosticText.HiveSeparatorPattern + @"[^/]*)";

    private const string QuotedPathPrefix = @"(?<=(?<oq>['""])[^'""]*?[/\\])" + NoSeparatorEarlierInSegment;

    [GeneratedRegex(
        @"(?:"
        + @"(?<=[/\\])" + NoSeparatorEarlierInSegment + @"(?<key>[^/\\=]*?)" + DiagnosticText.HiveSeparatorPattern + @"[^/]*(?=/)"
        + @"|" + QuotedPathPrefix + @"(?<key>[^/\\=\s]*?)" + DiagnosticText.HiveSeparatorPattern + ClosingQuoteValue
        + @"|" + QuotedPathPrefix + @"(?<key>[^/\\='""]*?)" + DiagnosticText.HiveSeparatorPattern + ClosingQuoteValue
        + @"|(?<=[/\\])" + NoSeparatorEarlierInSegment + @"(?<key>[^/\\=\s]*?)" + DiagnosticText.HiveSeparatorPattern + @"[^/]*$"
        + @"|" + PathRegionStart + NoSeparatorEarlierInSegment + @"(?<key>[^/\\=\s]*?)"
            + DiagnosticText.HiveSeparatorPattern + RootlessPathValue
        + @"|" + PathRegionStart + NoSeparatorEarlierInSegment + @"(?<key>[^/\\=\s]*?)"
            + DiagnosticText.HiveSeparatorPattern + RootlessBackslashPathValue
        + @")")]
    // INTERNAL, not private, and only for tests -- deliberately, with a reason that is itself a finding.
    // The `.Message`-level regressions pin this recognizer only up to DetailMaxLength (512): past that the
    // Sanitize cap truncates the message and MASKS whether the recognizer matched at all. Measured when the
    // recognizer had two branches: with the key quantifier re-bounded to {0,512} on both of them the ENTIRE
    // suite stayed green, because every observable assertion sat behind the cap. Re-derived across all five
    // branches after branch 5 was added -- red only in the recognizer-level test this finding
    // caused to be written, which is the outcome the finding predicts rather than a contradiction of it. That made the cap load-bearing for PII through an undocumented
    // coupling -- raising DetailMaxLength ("framework prose is getting truncated") would silently re-arm the
    // exact bypass that has already failed open twice in this file. Pinning the recognizer BELOW the cap is
    // the only place the property is observable without that confound.
    internal static partial Regex HivePartitionValue();

    // Wraps a filesystem operation failure into a Transient DeltaStorageException that discloses ONLY the
    // caller-relative object path and the failure type -- never the absolute mount/warehouse layout, in
    // the message OR the inner-exception chain. Exception.ToString() surfaces an inner exception's own
    // message, so the raw path-bearing framework exception must NOT be chained; a synthetic, path-free
    // inner carrying the redacted detail is attached instead so diagnostics survive without disclosure
    // (RF-8b: never let a raw filesystem exception become the inner of a surfaced storage error).
    // #683 + the Hive-path PII ruling: the object path is routed through DiagnosticText.DescribePath, NOT
    // Sanitize. On the READ path `path` is the log-supplied `add.path`, so a CONFINED-but-poisoned relative
    // path (e.g. "sub\r\nx.parquet", valid under-root on POSIX) that then hits a genuine IO fault would
    // otherwise echo raw control characters into this Transient message — a log-injection vector at a
    // structured-log sink. Sanitizing alone is NOT enough: DeltaSharp writes Hive-encoded paths, so the
    // directory segments carry partition VALUES = column values = potentially PII, and neither the
    // control-char strip nor the 128-char cap removes an email address. DescribePath keeps the sanitized file
    // name and the sanitized partition COLUMN NAMES and drops the values; the raw path is preserved on the
    // typed DeltaStorageException.Path for a caller that is entitled to it. `{detail}` (the framework inner
    // text) is the separately tracked #664 family and stays as-is.
    internal DeltaStorageException SurfaceFailure(string operation, string path, Exception ex)
    {
        string detail = string.Create(
            CultureInfo.InvariantCulture, $"{ex.GetType().Name}: {Redact(ex.Message)}");
        return DeltaStorageException.Transient(
            string.Create(CultureInfo.InvariantCulture, $"{operation} {DiagnosticText.DescribePath(path)} failed: {detail}"),
            new IOException(detail),
            path: path);
    }

    // Resolves a path to its root-relative form using the cheap lexical gate only (reject absolute-
    // outside-root and ".." traversal). On POSIX the symlink/real-target gate that Resolve performs is
    // REPLACED by the race-free openat + O_NOFOLLOW walk (strictly stronger — it closes the check-to-use
    // window), so only the lexical portion is needed here to derive the components to walk.
    private string ResolveRelative(string path, bool allowRoot = false)
    {
        string full = LexicallyConfine(path, allowRoot, out bool isRoot);
        return isRoot ? string.Empty : full[_rootWithSeparator.Length..];
    }

    // THE lexical confinement gate: normalize, then reject anything that is not under the root (optionally
    // allowing the root itself). Returns the normalized absolute path.
    //
    // Extracted because Resolve and ResolveRelative had each implemented it, character for character, with
    // the same predicate and the same message -- and every POSIX caller of ResolveRelative runs Resolve
    // first as a defense-in-depth pre-check, so ResolveRelative's copy of the throw was UNREACHABLE. A gate
    // that cannot fire is not defense in depth; it is a second thing to keep in sync that no test can hold
    // you to (mutating its message killed zero tests across the whole suite). Deleting it would have been
    // worse -- ResolveRelative would then slice `full[_rootWithSeparator.Length..]` off a path that is not
    // under the root -- so the duplication is removed instead of the guard, which makes the single remaining
    // copy load-bearing on EVERY door and therefore actually pinned. This is the same defect as the two Hive
    // recognizers: two pieces of code independently answering one question.
    private string LexicallyConfine(string path, bool allowRoot, out bool isRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string combined = Path.IsPathFullyQualified(path) ? path : Path.Combine(_root, path);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));

        isRoot = string.Equals(full, _root, StringComparison.Ordinal);
        if (!full.StartsWith(_rootWithSeparator, StringComparison.Ordinal) && !(allowRoot && isRoot))
        {
            throw DeltaStorageException.PathNotConfined(
                $"Path {DiagnosticText.DescribePath(path)} escapes the confined table root and is rejected.",
                path: path);
        }

        return full;
    }

    // Maps a confinement-walk failure to the deterministic storage error the backend contract promises.
    private static DeltaStorageException MapWalkError(ConfinedFileSystem.WalkError error, string path) => error switch
    {
        ConfinedFileSystem.WalkError.NotFound => DeltaStorageException.NotFound($"Object {DiagnosticText.DescribePath(path)} does not exist.", path: path),
        ConfinedFileSystem.WalkError.NotConfined => DeltaStorageException.PathNotConfined(
            $"Path {DiagnosticText.DescribePath(path)} resolves through a symlink to a location outside the confined table root and is rejected.",
            path: path),
        _ => DeltaStorageException.Transient($"Resolving {DiagnosticText.DescribePath(path)} failed.", path: path),
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
        ConfinementRaceProbe?.Invoke();
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
                throw DeltaStorageException.NotFound($"Object {DiagnosticText.DescribePath(path)} does not exist.", path: path);
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
            ConfinementRaceProbe?.Invoke();
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

    // POSIX race-free PutIfAbsentAsync: stage a private temp in the CONFINED parent descriptor and publish
    // it with linkat into that same descriptor, so neither the temp create nor the single-winner link can
    // be redirected outside the root by a swapped ancestor (issue #474). Preserves the atomic single-winner
    // (linkat EEXIST -> false), the ambiguous-durability contract (a linked destination whose directory
    // entry cannot be made durable -> RetryUnsafeAmbiguous), and the CommitStepProbe / PublishFaultErrnoHook
    // / FlushToDisk seams.
    private async ValueTask<bool> PutIfAbsentConfinedUnix(
        string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        string full = Resolve(path); // defense-in-depth pre-check
        string directory = Path.GetDirectoryName(full) ?? _root;
        string rel = ResolveRelative(path);
        ConfinementRaceProbe?.Invoke();
        string[] components = ConfinedFileSystem.SplitConfinedComponents(rel);
        SafeFileHandle? parent = ConfinedFileSystem.OpenOrCreateParent(
            _rootHandle!, components, out string destName, out ConfinedFileSystem.WalkError werr);
        if (parent is null)
        {
            throw MapWalkError(werr, path);
        }

        using (parent)
        {
            int parentFd = (int)parent.DangerousGetHandle();

            SafeFileHandle tempHandle;
            string tempName;
            try
            {
                (tempHandle, tempName) = CreateConfinedTemp(parentFd, destName);
            }
            catch (Exception ex)
            {
                throw SurfaceFailure("Staging conditional-create of", path, ex);
            }

            try
            {
                await using (var staging = new FileStream(tempHandle, FileAccess.Write, bufferSize: 4096, isAsync: false))
                {
                    await staging.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                    FlushToDisk(staging);
                }
            }
            catch (OperationCanceledException)
            {
                UnlinkTemp(parentFd, tempName);
                throw;
            }
            catch (Exception ex)
            {
                UnlinkTemp(parentFd, tempName);
                throw SurfaceFailure("Staging conditional-create of", path, ex);
            }

            bool won;
            try
            {
                won = TryAtomicPublishAt(parentFd, tempName, destName);
            }
            catch (Exception ex)
            {
                UnlinkTemp(parentFd, tempName);
                string detail = string.Create(CultureInfo.InvariantCulture, $"{ex.GetType().Name}: {Redact(ex.Message)}");
                throw DeltaStorageException.RetryUnsafeAmbiguous(
                    string.Create(CultureInfo.InvariantCulture, $"Conditional-create of {DiagnosticText.DescribePath(path)} failed ambiguously: {detail}"),
                    new IOException(detail),
                    path: path);
            }

            if (!won)
            {
                UnlinkTemp(parentFd, tempName);
                return false;
            }

            CommitStepProbe?.Invoke("publish");
            CommitStepProbe?.Invoke("dir-fsync");
            bool durable = FsyncDirFd(parentFd, directory);
            UnlinkTemp(parentFd, tempName);
            if (!durable)
            {
                throw DeltaStorageException.RetryUnsafeAmbiguous(
                    $"Conditional-create of {DiagnosticText.DescribePath(path)} linked its destination but the directory entry could not "
                    + "be made durable; the outcome is ambiguous and must be re-resolved.",
                    path: path);
            }

            return true;
        }
    }

    // Creates a private staging temp in the confined parent descriptor via O_CREAT|O_EXCL|O_NOFOLLOW (mode
    // 0600), retrying the ordinal on a name collision without ever deleting a foreign temp.
    private static (SafeFileHandle Handle, string Name) CreateConfinedTemp(int parentFd, string destName)
    {
        for (int attempt = 0; attempt < MaxTempAttempts; attempt++)
        {
            string candidate = BuildTempName(destName, Interlocked.Increment(ref _tempCounter), ".put.tmp");
            int flags = PosixInterop.O_CREAT | PosixInterop.O_EXCL | PosixInterop.O_WRONLY
                | PosixInterop.O_NOFOLLOW | PosixInterop.O_CLOEXEC;
            int fd = PosixInterop.OpenAt(parentFd, candidate, flags, 0x180 /* 0600 (unreliable when variadic) */);
            if (fd >= 0)
            {
                // OpenAt's mode is a variadic arg and is unreliable on arm64 macOS, so set 0600 explicitly
                // via the non-variadic fchmod before the temp is written or published.
                var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
                if (PosixInterop.FChmod(fd, 0x180) != 0)
                {
                    int chmodErrno = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    _ = PosixInterop.UnlinkAt(parentFd, candidate, 0);
                    throw new IOException(string.Create(
                        CultureInfo.InvariantCulture, $"Could not set staging temp '{candidate}' mode (errno {chmodErrno})."));
                }

                return (handle, candidate);
            }

            int errno = Marshal.GetLastPInvokeError();
            if (errno == PosixInterop.EEXIST && attempt < MaxTempAttempts - 1)
            {
                continue; // a foreign temp owns this name — retry with a fresh ordinal, never deleting it
            }

            throw new IOException(string.Create(
                CultureInfo.InvariantCulture, $"Could not create staging temp '{candidate}' (errno {errno})."));
        }

        throw new IOException(string.Create(
            CultureInfo.InvariantCulture,
            $"Could not create a unique staging temp for '{destName}' after {MaxTempAttempts} attempts."));
    }

    // linkat single-winner publish anchored to the confined parent descriptor (EEXIST -> lost race), firing
    // the same "publish" fault + PublishFaultErrnoHook seams as the string-path TryAtomicPublish.
    private static bool TryAtomicPublishAt(int parentFd, string tempName, string destName)
    {
        if (IoFaultHook?.Invoke("publish") is { } injected)
        {
            throw injected;
        }

        int errno;
        Func<int>? fault = PublishFaultErrnoHook;
        if (fault is not null)
        {
            errno = fault();
            if (errno == 0)
            {
                return true;
            }
        }
        else
        {
            if (PosixInterop.LinkAt(parentFd, tempName, parentFd, destName, 0) == 0)
            {
                return true;
            }

            errno = Marshal.GetLastPInvokeError();
        }

        if (errno == PosixInterop.EEXIST)
        {
            return false;
        }

        throw new IOException(string.Create(
            CultureInfo.InvariantCulture, $"linkat('{tempName}' -> '{destName}') failed with errno {errno}."));
    }

    // fsync the confined parent DESCRIPTOR for directory-entry durability, preserving the DirectoryFsync
    // FsyncHook seam (keyed by directory path for tests) without re-opening a re-resolvable path.
    private static bool FsyncDirFd(int dirFd, string directoryForHook)
    {
        Func<string, int>? hook = DirectoryFsync.FsyncHook;
        if (hook is not null && hook(directoryForHook) != 0)
        {
            return false;
        }

        return PosixInterop.Fsync(dirFd) == 0;
    }

    private static void UnlinkTemp(int parentFd, string tempName)
    {
        // Best-effort cleanup of the temp alias; an orphan is reclaimed by VACUUM. On POSIX the published
        // destination is a separate hard link to the same inode, so dropping the temp name never affects it.
        _ = PosixInterop.UnlinkAt(parentFd, tempName, 0);
    }

    // Canonicalizes an incoming path and confines it to the table root, rejecting fail-closed anything
    // that escapes (absolute-outside-root, ".." traversal, or a symlink whose real target leaves the
    // root). This is the LOG-E control (§5.5): it runs for EVERY path, whether user- or log-supplied.
    private string Resolve(string path, bool allowRoot = false)
    {
        // Cheap lexical gate first -- the SAME one ResolveRelative uses (see LexicallyConfine).
        string full = LexicallyConfine(path, allowRoot, out bool isRoot);

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
                $"Path {DiagnosticText.DescribePath(path)} could not be resolved (possible symlink cycle or inaccessible ancestor) and is rejected.",
                path: path);
        }

        bool realIsRoot = string.Equals(realFull, _realRoot, StringComparison.Ordinal);
        bool realIsUnderRoot = realFull.StartsWith(_realRootWithSeparator, StringComparison.Ordinal);
        if (!realIsUnderRoot && !(allowRoot && realIsRoot))
        {
            throw DeltaStorageException.PathNotConfined(
                $"Path {DiagnosticText.DescribePath(path)} resolves through a symlink to a location outside the confined table root and is rejected.",
                path: path);
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
        private readonly string _rawPath;
        private readonly Func<string, string> _redact;

        // POSIX race-free publish (issue #474): when set, publication uses linkat into this CONFINED parent
        // descriptor (not a re-resolvable string path). The stream owns the descriptor for its lifetime and
        // disposes it. Null on Windows, which keeps the string-path TryAtomicPublish.
        private readonly SafeFileHandle? _confinedParent;
        private readonly string _tempName;
        private readonly string _destName;

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
            // #683 + the Hive-path PII ruling: DESCRIBED once here so every message this stream raises is
            // injection-safe, length-bounded, and free of partition VALUES, without each call site having to
            // remember. The raw path is kept for DeltaStorageException.Path.
            _displayPath = DiagnosticText.DescribePath(displayPath);
            _rawPath = displayPath;
            _redact = redact;
            _inner = inner;
            _confinedParent = null;
            _tempName = string.Empty;
            _destName = string.Empty;
        }

        // POSIX race-free constructor: publish via linkat into the confined parent descriptor.
        public StagedWriteStream(
            FileStream inner, SafeFileHandle confinedParent, string tempName, string destName,
            string destinationDirectory, string displayPath, Func<string, string> redact)
        {
            _inner = inner;
            _confinedParent = confinedParent;
            _tempName = tempName;
            _destName = destName;
            _destinationDirectory = destinationDirectory;

            // #683 + the Hive-path PII ruling: DESCRIBED once (see the string-path constructor) so every
            // message this stream raises is injection-safe, length-bounded, and partition-value-free.
            _displayPath = DiagnosticText.DescribePath(displayPath);
            _rawPath = displayPath;
            _redact = redact;
            _tempPath = string.Empty;
            _destinationPath = string.Empty;
        }

        // RF-8b: a staged-stream write/flush failure (mid-write ENOSPC/EDQUOT/EIO) surfaces ONLY the
        // relative object path + failure type, never the absolute path in the message OR the inner chain.
        private DeltaStorageException Sanitize(string operation, Exception ex)
        {
            string detail = string.Create(
                CultureInfo.InvariantCulture, $"{ex.GetType().Name}: {_redact(ex.Message)}");
            return DeltaStorageException.Transient(
                string.Create(CultureInfo.InvariantCulture, $"{operation} staged write to {_displayPath} failed: {detail}"),
                new IOException(detail),
                path: _rawPath);
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

            _confinedParent?.Dispose();
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

                _confinedParent?.Dispose();
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
            if (_confinedParent is not null)
            {
                PublishConfined();
                return;
            }

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
                    string.Create(CultureInfo.InvariantCulture, $"Publishing staged write to {_displayPath} failed ambiguously: {detail}"),
                    new IOException(detail),
                    path: _rawPath);
            }

            if (!won)
            {
                CleanupTemp();
                throw DeltaStorageException.AlreadyExists(
                    $"Cannot publish staged write: destination {_displayPath} already exists.", path: _rawPath);
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
                    $"Staged write to {_displayPath} published but the directory entry could not be "
                    + "made durable; the outcome is ambiguous and must be re-resolved.",
                    path: _rawPath);
            }
        }

        private void CleanupTemp()
        {
            if (_confinedParent is not null)
            {
                UnlinkTemp((int)_confinedParent.DangerousGetHandle(), _tempName);
            }
            else
            {
                TryDelete(_tempPath);
            }
        }

        // POSIX race-free publish: linkat the staged temp into the confined parent descriptor (single-
        // winner, EEXIST -> AlreadyExists), then fsync the parent descriptor for durability and drop the
        // temp alias. Mirrors the string-path Publish() semantics (ambiguous-durability, probes, cleanup).
        private void PublishConfined()
        {
            int parentFd = (int)_confinedParent!.DangerousGetHandle();
            bool won;
            try
            {
                won = TryAtomicPublishAt(parentFd, _tempName, _destName);
            }
            catch (Exception ex) when (ex is not DeltaStorageException)
            {
                UnlinkTemp(parentFd, _tempName);
                string detail = string.Create(
                    CultureInfo.InvariantCulture, $"{ex.GetType().Name}: {_redact(ex.Message)}");
                throw DeltaStorageException.RetryUnsafeAmbiguous(
                    string.Create(CultureInfo.InvariantCulture, $"Publishing staged write to {_displayPath} failed ambiguously: {detail}"),
                    new IOException(detail),
                    path: _rawPath);
            }

            if (!won)
            {
                UnlinkTemp(parentFd, _tempName);
                throw DeltaStorageException.AlreadyExists(
                    $"Cannot publish staged write: destination {_displayPath} already exists.", path: _rawPath);
            }

            CommitStepProbe?.Invoke("publish");
            CommitStepProbe?.Invoke("dir-fsync");
            bool durable = FsyncDirFd(parentFd, _destinationDirectory);
            UnlinkTemp(parentFd, _tempName);
            if (!durable)
            {
                throw DeltaStorageException.RetryUnsafeAmbiguous(
                    $"Staged write to {_displayPath} published but the directory entry could not be "
                    + "made durable; the outcome is ambiguous and must be re-resolved.",
                    path: _rawPath);
            }
        }
    }
}
