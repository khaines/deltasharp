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

    /// <summary>The stable cross-instance table identity (backend kind + canonical real table root) the
    /// checkpoint decode negative cache keys on (#647/#699/#716). Two <see cref="LocalFileSystemBackend"/>
    /// instances rooted at the SAME table therefore share negative-cache entries — so a persistently
    /// non-terminating checkpoint decoded through a fresh backend per load is NOT re-decoded every time — while
    /// two different table roots never collide. Uses <see cref="TableRootId"/> (the canonicalized real root,
    /// the same identity <see cref="Reading.ChangeFeedReader"/> binds its resume tokens to).
    /// <para><b>Case-sensitivity residual (noted, not canonicalized).</b> The identity is the case-PRESERVING
    /// real root. On a case-insensitive host (default macOS/Windows) two different-case paths to the SAME table
    /// (<c>/Tbl</c> vs <c>/tbl</c>) therefore yield DISTINCT identities, so a fresh backend opened via a
    /// different-case path would MISS the negative-cache entry the other seeded — costing at most an extra
    /// (safe) JSON replay / re-decode, never a wrong read or a cross-table suppression. Case is deliberately
    /// NOT folded here: a case-insensitive OS can still host a case-SENSITIVE volume (a case-mounted APFS
    /// volume, a Linux bind mount), where folding case would WRONGLY collide two genuinely distinct tables — a
    /// worse failure than the benign extra replay. The residual is accepted.</para></summary>
    public string TableIdentity =>
        string.Create(CultureInfo.InvariantCulture, $"{Kind}:{TableRootId}");

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
    // #704, SETTLED. The lookbehind class of branches 1 and 4 does not include a quote or string-start, so a
    // RELATIVE key (the shape an object-store backend produces) is not matched by those branches. That case
    // is now covered instead by the RIGHTWARD-evidence branches 5 and 6 (MECHANISM 7 below): a relative
    // `key=value` at string start or after a quote redacts whenever a following `/` (branch 5) or a `\`
    // anywhere (branch 6) proves a path continues. Pinned by Redact_RootlessRelativePath_RedactsTheFirstSegmentToo.
    //
    // This RETIRES the absolute-path precondition as a load-bearing dependency: the recognizer no longer
    // needs "a framework message embeds the absolute path so root-stripping leaves the remainder behind a
    // separator" to fire on an object-store key. The only relative shape that still declines is a BARE
    // `key=value` carrying no path evidence anywhere (`k=v`, `k=v.parquet`) -- residual R4 -- which is
    // deliberately indistinguishable from an operator diagnostic (`errno=13`, `retries=5`) and must decline
    // to keep those verbatim. Pinned by Redact_BareKeyValueWithNoPathEvidence_IsLeftAlone.
    //
    // #704(b), THE QUERY-STRING `?` DECISION, RECORDED. The key classes deliberately do NOT exclude `?`, so a
    // query-string `key=value` (e.g. `/objects?prefix=data`, `/obj?X-Amz-Signature=...`) is treated as a Hive
    // segment and its value redacted. This is chosen, not accidental: a presigned-URL credential
    // (`X-Amz-Signature`, `X-Amz-Credential`, `X-Amz-Security-Token`, `sig`) arrives as a query parameter and
    // MUST be redacted, and redacting a benign parameter (`prefix=`, `versionId=`, `uploadId=`) is a
    // diagnosability-only cost in the same SAFE direction as the ACCEPTED OVER-REDACTION below -- never a
    // disclosure. Excluding `?` would trade a credential leak for that context, which is the wrong way round.
    // Pinned by Redact_QueryStringKeyValue_IsRedacted_TheChosenSafeDirection.
    //
    // Branches are ordered most-anchored first.
    //
    // THE INVARIANT: NO BRANCH MAY EMIT A MARKER OVER A PARTIALLY-CONSUMED VALUE. Every outcome must be
    // exactly one of
    //
    //     FULL     -- the value is consumed to a true segment boundary and "<value>" replaces all of it, or
    //     DECLINE  -- nothing matches, the segment is echoed as-is and sanitized like any other text.
    //
    // PARTIAL is not a third option: it is worse than DECLINE because the marker claims removal while tail
    // content remains beside it. The same rule resolves `\` fail-closed: when delimiter meaning depends on
    // platform, treat backslash as content rather than let it end a value early.
    //
    // Beyond the invariant, the branch structure encodes a LADDER: the stronger the right delimiter, the
    // more permissive the key may be, because the delimiter is the evidence that a path is in play at all.
    //
    //   1. PATH SEPARATOR follows -- strongest evidence. Key `[^/\=]` and value `[^/]`: quotes AND
    //                                whitespace allowed in both.
    //   2. CLOSING QUOTE follows,  -- one relaxation: quotes in the key, so prose after a quoted path can
    //      key has quotes, no space    survive instead of being consumed by branch 4.
    //   3. CLOSING QUOTE follows,  -- the mirror relaxation: whitespace in the key, no quotes.
    //      key has space, no quotes
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
    // DescribePath takes the FIRST separator in a segment, so this regex must do the same; lazy keys plus
    // excluding '=' keep Redact and DescribePath aligned on WHICH SUBSTRING IS THE KEY, not just on which
    // separator spellings exist.
    //
    // Excluding '=' from every key class stops greedy over-capture across "/a=1=2=3/".

    // THE RESIDUALS are pinned by Redact_MonotonicityMatrix. R1 and R2 are DECLINE clauses; R3 is
    // closed for balanced quoting; R4 is defined beside branch 5; R5 is the only PARTIAL residual and
    // stays separate; R6 is closed.
    //
    //   R1  {WHITESPACE-BEARING key} x {NO right delimiter}. Not closable. Safe in-tree because no genuine
    //       door on this backend surfaces a runtime path at all -- the confinement and not-found guards
    //       pre-empt with their own DescribePath render, and the syscall wrappers synthesize a path-free
    //       errno detail. Pinned by Redact_NoGenuineDoorOnThisBackendSurfacesARuntimePath. Tracked by #704/#708.
    //   R2  {key bearing BOTH a quote and whitespace} x {any right delimiter that is NOT a real path
    //       separator}. The errno=13 cell. Irreducible: `o'brien y=v'` and `file": errno=13"` are the SAME
    //       STRING SHAPE, so no recognizer can redact one and preserve the other -- re-admitting quotes to
    //       branch 3's key closes R2 and reopens errno=13 on both quote spellings, which is a trade, not a
    //       free win. Confined in practice to a message naming a partition DIRECTORY, since a data file
    //       appends "/part-<guid>.parquet" and so supplies the separator branch 1 needs. This is a
    //       monotonicity regression against 9220b66 and is tracked by #714. As of #708 the WRITE-PATH half is
    //       retired FOR NEW WRITES: DeltaWriteEncoding.HivePartitionSegment now percent-encodes the partition
    //       column NAME as well as the value, so a DeltaSharp-authored add.path key WRITTEN AFTER #708 can no
    //       longer carry a quote or whitespace and cannot produce this shape. R2 remains reachable via a
    //       FOREIGN add.path (a key alphabet DeltaSharp does not control) AND via a LEGACY DeltaSharp-authored
    //       add.path written BEFORE #708 (raw, unencoded keys) -- the encoding retires the residual for NEW
    //       writes only, not retroactively -- which is why Redact_MonotonicityMatrix still asserts
    //       declinedR2 > 0: the recognizer must handle both foreign and legacy input forever.
    //
    // R3 -- {quote INSIDE the value} -- is closed for the balanced quoting a framework message emits; an
    // unclosed opening quote belongs to R1 instead.
    //
    // R5 is a PARTIAL residual, tracked by #723. A legitimately unclosed or truncated quote can still yield
    // `stat failed on '/tbl/name=<value>' Club Holdings`; those byte-identical inputs are irreducible at this
    // layer. Pinned by Redact_UnclosedOpeningQuote_RedactsTheQuotedRegionAndNothingElse for the parse, and
    // by Redact_InteriorQuoteInAValue_IsAnIrreduciblePartialResidual for the disclosure.
    //
    // ACCEPTED OVER-REDACTION, the safe direction, pinned in Redact_DelimiterAdjacentProse_IsAKnownAccepted-
    // OverRedaction: prose that places a key=value run before a later path separator now loses the value,
    // e.g. "stat /var/lib/x then set retries=5 for /tmp/y". Diagnosability only, never disclosure.
    //
    // The separator alphabet is DiagnosticText.HiveSeparatorPattern, the same definition DescribePath's
    // HiveSeparatorIndex scans for, concatenated in as a constant so the two recognizers cannot drift.
    /// <summary>
    /// Value run and right anchor shared by the two QUOTE-DELIMITED branches of the recognizer.
    /// </summary>
    /// <remarks>
    /// <para>The value run is fully permissive: quotes are valid value content, so excluding them would stop
    /// at an interior quote and produce a PARTIAL match.</para>
    /// <para>The right anchor is strong: the matching quote must be followed by a separator, whitespace, end
    /// of message, or terminal punctuation. A quote ends the value only when it has closing-delimiter
    /// evidence too.</para>
    /// <para>Defined once because the two quote-delimited branches must share the same rule.</para>
    /// </remarks>
    private const string ClosingQuoteValue = @"[^/]*(?=\k<oq>(?:[/\\\s]|$|[.,:;)\]]))";

    /// <summary>
    /// Left precondition for the two QUOTE-DELIMITED branches: the path must actually be QUOTED.
    /// </summary>
    /// <remarks>
    /// <para>A quote is only a delimiter if something opened it. Without this, any interior quote followed
    /// by a boundary would satisfy <c>ClosingQuoteValue</c> and truncate a partition value mid-string.</para>
    /// <para>The lookbehind captures the opening quote, keeps the segment free of intervening quotes, and
    /// requires the closing quote to match the same character. When the path is unquoted these branches
    /// decline and branch 4 consumes the value in full.</para>
    /// </remarks>
    // Separator roles are deliberate. `\` is admitted in every VALUE class because on POSIX it can be
    // content. The left-anchor question is narrower: a backslash may open a segment only while no Hive
    // separator has yet appeared in the current forward-slash segment. After that, everything further along
    // the run is value content and a `\` would invent a synthetic sub-key inside it.
    //
    // MECHANISM 7 (from PathDisclosureHygieneTests.Redact_RootlessRelativePath_RedactsTheFirstSegmentToo). add.path is RELATIVE by the Delta protocol, so a message may begin with its first
    // `key=value` segment. Branch 5 (RootlessPathValue) therefore takes its path evidence from the RIGHT: a
    // following `/...` proves a path continues without weakening the anchored branches above it.
    // PathRegionStart lets that branch open at the start of input, after a quote, or after whitespace.
    //
    // It deliberately does NOT match a bare key=value with no separator evidence. That is residual R4: a
    // key=value carrying no separator evidence ANYWHERE. `k=v` and `k=v.parquet` still decline. `k=v\` does not.
    private const string RootlessPathValue = @"[^/]*(?=/[^/]*(?:[/'""]|$))";

    /// <summary>
    /// Right anchor for a rootless path whose only separator evidence is a BACKSLASH.
    /// </summary>
    /// <remarks>
    /// <para>The value runs to the end of the message. On POSIX a backslash is ordinary filename content,
    /// so stopping at <c>\</c> would emit a marker over a partially consumed value.</para>
    /// <para>The evidence required is a backslash ANYWHERE, including a trailing one. Branch 6 is the
    /// rootless backslash-only sibling to branch 5 and closes the former trailing-backslash R6 case without
    /// reintroducing partial matches.</para>
    /// <para>The asymmetry with branch 5 is deliberate: <c>/</c> is a real component boundary, so branch 5
    /// may let the tail survive; <c>\</c> is not, so branch 6 must consume it.</para>
    /// </remarks>
    private const string RootlessBackslashPathValue = @"(?=[^/]*\\)[^/]*$";

    private const string PathRegionStart = @"(?<![^/\\""'\s])";

    // Guard-site mutation profile for NoSeparatorEarlierInSegment/QuotedPathPrefix, measured per-site
    // at the alternation that exists today. Every site is now subsumed while branch 6
    // (RootlessBackslashPathValue) is present; the per-site pin is in
    // PathDisclosureHygieneTests.SegmentGuard_PerSiteProfile_IsMeasuredAtThisHeadRatherThanDescribed
    // (which reads the pattern by reflection and deletes branch 6 at
    // runtime to restore individual teeth). Historical baseline:
    // MEASURED-AT 83b88dc — dropping `^` killed 6 tests, dropping `/` killed 5; at the next commit both
    // killed 0 as branch 6 became the sub-key blocker. The per-site test now owns these numbers.

    // The left anchor resolves the backslash guess in the same fail-closed direction as the value classes.
    // NoSeparatorEarlierInSegment scans the current forward-slash segment (including `^` for Windows-shaped
    // paths with no `/`) and declines once a Hive separator has already appeared there. After a separator,
    // a `\` is inside value content and must not start a synthetic sub-key. QuotedPathPrefix reuses the
    // same rule for quote-delimited openings.
    private const string NoSeparatorEarlierInSegment =
        @"(?<!(?:^|/)[^/]*" + DiagnosticText.HiveSeparatorPattern + @"[^/]*)";

    private const string QuotedPathPrefix = @"(?<=(?<oq>['""])[^'""]*?[/\\])" + NoSeparatorEarlierInSegment;

    // EVERY SITE THE SEPARATOR ALPHABET IS SPELLED, AND WHAT EACH ONE DOES WITH A BACKSLASH. Two rounds
    // running, this rule was applied where a defect was measured rather than at every position the
    // property has to hold, and each time the next reviewer found the position that was missed. So the
    // sites are enumerated, and a site that legitimately differs says why:
    //
    //   1. value classes, all six branches       [^/]*            admits \ -- a value may contain one
    //   2. branch 1 right anchor                 (?=/)            excludes \ -- it must not END a value
    //   3. ClosingQuoteValue right anchor        \k<oq>(?:[/\\\s]|$|[.,:;)\]]) admits \ AFTER the closing
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
    // INTERNAL for tests: `.Message`-level assertions are masked beyond DetailMaxLength by Sanitize, so
    // the recognizer must be pinned below that cap. Keeping this visible to tests makes the pre-cap
    // behavior observable without changing the production surface.
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
