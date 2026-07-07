using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeltaSharp.Storage.Backends;

/// <summary>
/// Race-free (TOCTOU-proof) path confinement for the POSIX local backend (design §2.13.1; issue #474).
/// Every root-relative path is resolved by <b>walking one component at a time</b> with
/// <c>openat(2)</c> + <see cref="PosixInterop.O_NOFOLLOW"/> starting from a long-lived <b>root directory
/// descriptor</b>, so the kernel — not a re-resolvable string path — enforces confinement. A component
/// that is a symlink fails the open with <see cref="PosixInterop.ELOOP"/>, closing the check-to-use
/// window that a lexical/canonicalize-then-open check leaves open (an adversary swapping an in-root
/// directory for an out-of-root symlink between check and use). Callers operate on the returned
/// descriptor (read/stat/list) or on a component name relative to a confined <b>parent</b> descriptor
/// (create/link/unlink), never on a path string that could be re-resolved.
/// </summary>
/// <remarks>
/// Unix-only: <c>openat</c>/<c>O_NOFOLLOW</c> have no Windows equivalent, and Windows keeps its existing
/// canonicalize-then-open confinement (NTFS reparse-point handling differs). Callers gate on
/// <see cref="OperatingSystem.IsWindows"/>.
/// </remarks>
internal static class ConfinedFileSystem
{
    /// <summary>The result of a confinement walk that failed, distinguishing a genuine "not found" from a
    /// confinement-escape attempt so the backend can map each to the right
    /// <see cref="StorageErrorKind"/>.</summary>
    internal enum WalkError
    {
        /// <summary>The walk succeeded.</summary>
        None = 0,

        /// <summary>A component did not exist (<see cref="PosixInterop.ENOENT"/>) — map to
        /// <see cref="StorageErrorKind.NotFound"/>.</summary>
        NotFound,

        /// <summary>A component was a symlink (<see cref="PosixInterop.ELOOP"/>) or a non-directory used as
        /// one (<see cref="PosixInterop.ENOTDIR"/>) — a confinement-escape attempt; map to
        /// <see cref="StorageErrorKind.PathNotConfined"/>.</summary>
        NotConfined,

        /// <summary>Any other native failure — map to <see cref="StorageErrorKind.Transient"/>.</summary>
        Io,
    }

    /// <summary>Opens the trusted table-root directory as a long-lived descriptor
    /// (<c>O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC</c>). The root itself is established and canonicalized once at
    /// backend construction, so opening it by absolute path is not a TOCTOU surface; every subsequent
    /// resolution is relative to this descriptor.</summary>
    internal static SafeFileHandle OpenRoot(string rootRealPath)
    {
        int flags = PosixInterop.O_RDONLY | PosixInterop.O_DIRECTORY | PosixInterop.O_NOFOLLOW | PosixInterop.O_CLOEXEC;
        int fd = PosixInterop.Open(rootRealPath, flags);
        if (fd < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new IOException($"Cannot open table root directory (errno {err}).");
        }

        return new SafeFileHandle((nint)fd, ownsHandle: true);
    }

    /// <summary>Splits a normalized root-relative path into its components, rejecting any traversal
    /// (<c>.</c>, <c>..</c>), empty, or NUL-bearing component so the walk can never step outside the tree
    /// even before the kernel checks. Forward slash is the only separator (POSIX).</summary>
    /// <exception cref="DeltaStorageException"><see cref="StorageErrorKind.PathNotConfined"/> for a
    /// traversal or malformed component.</exception>
    internal static string[] SplitConfinedComponents(string relativePath)
    {
        string[] parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (part is "." or ".." || part.Length == 0 || part.Contains('\0', StringComparison.Ordinal))
            {
                throw DeltaStorageException.PathNotConfined(relativePath);
            }
        }

        return parts;
    }

    /// <summary>Walks every component of <paramref name="components"/> from <paramref name="rootHandle"/>
    /// with <c>openat</c>+<see cref="PosixInterop.O_NOFOLLOW"/>, opening the leaf with
    /// <paramref name="leafFlags"/> (also NOFOLLOW/CLOEXEC). Intermediate components are opened
    /// <c>O_DIRECTORY|O_NOFOLLOW</c> — a symlinked or non-directory component fails the walk. Returns the
    /// leaf descriptor, or <see langword="null"/> with <paramref name="error"/> set.</summary>
    internal static SafeFileHandle? TryOpenLeaf(
        SafeFileHandle rootHandle, string[] components, int leafFlags, uint mode, out WalkError error)
    {
        if (components.Length == 0)
        {
            // The prefix IS the root directory itself.
            error = WalkError.None;
            return DuplicateAsDirectory(rootHandle, out error);
        }

        int dirfd = -1;
        bool ownsDir = false;
        try
        {
            dirfd = (int)rootHandle.DangerousGetHandle();
            for (int i = 0; i < components.Length - 1; i++)
            {
                int walkFlags = PosixInterop.O_RDONLY | PosixInterop.O_DIRECTORY | PosixInterop.O_NOFOLLOW | PosixInterop.O_CLOEXEC;
                int next = PosixInterop.OpenAt(dirfd, components[i], walkFlags, 0);
                if (next < 0)
                {
                    error = ClassifyOpenError(Marshal.GetLastPInvokeError());
                    return null;
                }

                if (ownsDir)
                {
                    _ = PosixInterop.Close(dirfd);
                }

                dirfd = next;
                ownsDir = true;
            }

            int leaf = PosixInterop.OpenAt(dirfd, components[^1], leafFlags | PosixInterop.O_NOFOLLOW | PosixInterop.O_CLOEXEC, mode);
            if (leaf < 0)
            {
                error = ClassifyOpenError(Marshal.GetLastPInvokeError());
                return null;
            }

            error = WalkError.None;
            return new SafeFileHandle((nint)leaf, ownsHandle: true);
        }
        finally
        {
            if (ownsDir && dirfd >= 0)
            {
                _ = PosixInterop.Close(dirfd);
            }
        }
    }

    /// <summary>Walks to the confined <b>parent</b> directory of the last component and returns its
    /// descriptor plus the leaf name, for operations that act on a name within a directory
    /// (<c>linkat</c>/<c>unlinkat</c>/<c>openat O_CREAT|O_EXCL</c>). The returned handle owns the parent
    /// descriptor; the caller disposes it.</summary>
    internal static SafeFileHandle? TryOpenParent(
        SafeFileHandle rootHandle, string[] components, out string leafName, out WalkError error)
    {
        leafName = components.Length == 0 ? string.Empty : components[^1];
        if (components.Length == 0)
        {
            error = WalkError.NotConfined;
            return null;
        }

        if (components.Length == 1)
        {
            error = WalkError.None;
            return DuplicateAsDirectory(rootHandle, out error);
        }

        int dirfd = (int)rootHandle.DangerousGetHandle();
        bool ownsDir = false;
        for (int i = 0; i < components.Length - 1; i++)
        {
            int walkFlags = PosixInterop.O_RDONLY | PosixInterop.O_DIRECTORY | PosixInterop.O_NOFOLLOW | PosixInterop.O_CLOEXEC;
            int next = PosixInterop.OpenAt(dirfd, components[i], walkFlags, 0);
            if (next < 0)
            {
                if (ownsDir)
                {
                    _ = PosixInterop.Close(dirfd);
                }

                error = ClassifyOpenError(Marshal.GetLastPInvokeError());
                return null;
            }

            if (ownsDir)
            {
                _ = PosixInterop.Close(dirfd);
            }

            dirfd = next;
            ownsDir = true;
        }

        error = WalkError.None;
        return new SafeFileHandle((nint)dirfd, ownsHandle: true);
    }

    /// <summary>Reads the last-modified time of an open descriptor via <c>fstat</c>, cross-checking the
    /// decoded <c>st_size</c> against <see cref="RandomAccess.GetLength"/> so a wrong <c>struct stat</c>
    /// layout on an unexpected platform/arch fails closed (throws) instead of returning a garbage time.</summary>
    internal static DateTime GetLastModifiedUtc(SafeFileHandle handle, long expectedLength)
    {
        Span<byte> buffer = stackalloc byte[PosixInterop.StatBufferSize];
        buffer.Clear();
        int fd = (int)handle.DangerousGetHandle();
        if (PosixInterop.FStat(fd, buffer) != 0)
        {
            throw DeltaStorageException.Transient($"fstat failed (errno {Marshal.GetLastPInvokeError()}).");
        }

        long statSize = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(PosixInterop.StatSizeOffset, 8));
        if (statSize != expectedLength)
        {
            throw new InvalidOperationException(
                $"fstat st_size ({statSize}) disagrees with the descriptor length ({expectedLength}); "
                + "the struct stat layout is unexpected on this platform — refusing to report metadata.");
        }

        long mtimeSeconds = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(PosixInterop.StatMtimeSecOffset, 8));
        long mtimeNanoseconds = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(PosixInterop.StatMtimeNsecOffset, 8));
        return DateTimeOffset.FromUnixTimeSeconds(mtimeSeconds).UtcDateTime.AddTicks(mtimeNanoseconds / 100);
    }

    /// <summary>Enumerates the entry <b>names</b> of a confined directory descriptor via
    /// <c>fdopendir</c>/<c>readdir</c> (reading only <c>d_name</c>), skipping <c>.</c> and <c>..</c>. The
    /// <c>DIR*</c> takes ownership of the descriptor, so the caller must transfer (not dispose) the
    /// handle it walked; this method closes it via <c>closedir</c>.</summary>
    internal static List<string> ReadDirectoryNames(int dirFd)
    {
        var names = new List<string>();
        nint dir = PosixInterop.FdOpenDir(dirFd);
        if (dir == nint.Zero)
        {
            _ = PosixInterop.Close(dirFd);
            throw DeltaStorageException.Transient($"fdopendir failed (errno {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            nint entry;
            while ((entry = PosixInterop.ReadDir(dir)) != nint.Zero)
            {
                string name = Marshal.PtrToStringUTF8(entry + PosixInterop.DirentNameOffset) ?? string.Empty;
                if (name.Length == 0 || name is "." or "..")
                {
                    continue;
                }

                names.Add(name);
            }
        }
        finally
        {
            _ = PosixInterop.CloseDir(dir);
        }

        return names;
    }

    private static WalkError ClassifyOpenError(int errno)
    {
        if (errno == PosixInterop.ELOOP || errno == PosixInterop.ENOTDIR)
        {
            return WalkError.NotConfined;
        }

        if (errno == PosixInterop.ENOENT)
        {
            return WalkError.NotFound;
        }

        return WalkError.Io;
    }

    private static SafeFileHandle? DuplicateAsDirectory(SafeFileHandle rootHandle, out WalkError error)
    {
        // Re-open the root directory relative to itself ("." with O_NOFOLLOW is safe — "." is never a
        // symlink) so callers get an independently-owned descriptor they can dispose without closing the
        // shared long-lived root handle.
        int rootFd = (int)rootHandle.DangerousGetHandle();
        int flags = PosixInterop.O_RDONLY | PosixInterop.O_DIRECTORY | PosixInterop.O_CLOEXEC;
        int fd = PosixInterop.OpenAt(rootFd, ".", flags, 0);
        if (fd < 0)
        {
            error = ClassifyOpenError(Marshal.GetLastPInvokeError());
            return null;
        }

        error = WalkError.None;
        return new SafeFileHandle((nint)fd, ownsHandle: true);
    }
}
