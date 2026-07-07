using System.Runtime.InteropServices;

namespace DeltaSharp.Storage.Backends;

/// <summary>
/// POSIX directory <c>fsync</c> so that a newly created or renamed entry's <b>name</b> is durable in
/// its parent directory, not just the file's data (design §2.13.2 durability). Writing and
/// <c>fsync</c>ing a file only guarantees the file <i>contents</i> survive a crash; the directory
/// entry that makes the file discoverable is a separate metadata write that must itself be
/// <c>fsync</c>ed. On Windows this is a no-op — NTFS journals directory metadata so a completed
/// <c>MoveFileEx</c>/create is already crash-durable. The result is <b>reported</b> (not swallowed) so a
/// commit path can treat a directory-durability failure as an ambiguous, retry-unsafe outcome.
/// </summary>
internal static class DirectoryFsync
{
    /// <summary>Fault-injection seam (tests only): when non-null, its return code <b>replaces</b> the
    /// native directory <c>fsync</c> result so a test can force a durability failure and prove the commit
    /// path surfaces <see cref="StorageErrorKind.RetryUnsafeAmbiguous"/> without inducing a real one. A
    /// non-zero code simulates a failed <c>fsync</c>; zero falls through to the genuine syscall. Null and
    /// inert in production; consulted at the exact <c>fsync</c> point.</summary>
    internal static volatile Func<string, int>? FsyncHook;

    /// <summary>Durably flushes <paramref name="directoryPath"/>'s directory entry table. Returns
    /// <see langword="true"/> when the entry is durable (and on Windows, where NTFS journals directory
    /// metadata so a completed create/rename is already crash-durable), and <see langword="false"/> when
    /// the directory could not be opened or <c>fsync</c>ed — the signal the commit path maps to an
    /// ambiguous, retry-unsafe outcome so a crash-in-window can never silently lose a name the caller was
    /// told was committed (design §2.13.2).</summary>
    public static bool Sync(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        ArgumentException.ThrowIfNullOrEmpty(directoryPath);

        Func<string, int>? hook = FsyncHook;
        if (hook is not null && hook(directoryPath) != 0)
        {
            return false;
        }

        int fd = PosixInterop.Open(directoryPath, PosixInterop.O_RDONLY);
        if (fd < 0)
        {
            return false;
        }

        try
        {
            return PosixInterop.Fsync(fd) == 0;
        }
        finally
        {
            _ = PosixInterop.Close(fd);
        }
    }
}

/// <summary>
/// AOT-friendly <c>[LibraryImport]</c> bindings to the libc primitives the local backend needs for a
/// <b>truly atomic single-winner publish</b> and for directory durability (design §2.13.2). These use
/// the source-generated marshaller (no <c>DllImport</c> reflection), so they survive trimming and
/// NativeAOT (ADR-0014).
/// </summary>
/// <remarks>
/// The load-bearing primitive is <see cref="Link"/>: <c>link(temp, dest)</c> fails atomically with
/// <see cref="EEXIST"/> when <c>dest</c> already exists, giving the same single-winner guarantee as
/// <c>open(O_CREAT|O_EXCL)</c> while publishing an already-<c>fsync</c>ed temp file. This is used in
/// place of <see cref="File.Move(string, string, bool)"/> on POSIX because .NET's
/// <c>File.Move(overwrite: false)</c> is <b>not</b> atomic on all POSIX platforms (on macOS it maps to
/// <c>rename()</c>, which silently replaces the destination, so concurrent callers can all "win").
/// </remarks>
internal static partial class PosixInterop
{
    /// <summary>The <c>O_RDONLY</c> open flag (0 on Linux and macOS).</summary>
    internal const int O_RDONLY = 0;

    /// <summary>The <c>O_WRONLY</c> open flag (1 on Linux and macOS).</summary>
    internal const int O_WRONLY = 1;

    /// <summary>The <c>EEXIST</c> errno (17 on Linux and macOS): the link/create target already exists.</summary>
    internal const int EEXIST = 17;

    /// <summary>The <c>ENOENT</c> errno (2 on Linux and macOS): a path component does not exist.</summary>
    internal const int ENOENT = 2;

    /// <summary>The <c>ENOTDIR</c> errno (20 on Linux and macOS): a path component is not a directory.</summary>
    internal const int ENOTDIR = 20;

    private static readonly bool IsMac = OperatingSystem.IsMacOS();

    /// <summary>The <c>O_DIRECTORY</c> open flag — fail unless the target is a directory. Linux
    /// <c>0200000</c> (65536), macOS <c>0x100000</c> (1048576).</summary>
    internal static readonly int O_DIRECTORY = IsMac ? 0x100000 : 0x10000;

    /// <summary>The <c>O_NOFOLLOW</c> open flag — fail (<see cref="ELOOP"/>) if the final component is a
    /// symbolic link, the load-bearing anti-symlink control. Linux <c>0400000</c> (131072), macOS
    /// <c>0x100</c> (256).</summary>
    internal static readonly int O_NOFOLLOW = IsMac ? 0x100 : 0x20000;

    /// <summary>The <c>O_CLOEXEC</c> open flag — close the descriptor across <c>exec</c>. Linux
    /// <c>02000000</c> (524288), macOS <c>0x1000000</c> (16777216).</summary>
    internal static readonly int O_CLOEXEC = IsMac ? 0x1000000 : 0x80000;

    /// <summary>The <c>O_CREAT</c> open flag. Linux <c>0100</c> (64), macOS <c>0x200</c> (512).</summary>
    internal static readonly int O_CREAT = IsMac ? 0x200 : 0x40;

    /// <summary>The <c>O_EXCL</c> open flag — with <see cref="O_CREAT"/>, fail
    /// (<see cref="EEXIST"/>) if the target already exists. Linux <c>0200</c> (128), macOS
    /// <c>0x800</c> (2048).</summary>
    internal static readonly int O_EXCL = IsMac ? 0x800 : 0x80;

    /// <summary>The <c>ELOOP</c> errno — an <see cref="O_NOFOLLOW"/> open hit a symbolic link (or too
    /// many links). Linux <c>40</c>, macOS <c>62</c>. This is the signal that a path component was a
    /// symlink (a confinement-escape attempt).</summary>
    internal static readonly int ELOOP = IsMac ? 62 : 40;

    /// <summary>The <c>AT_REMOVEDIR</c> flag for <see cref="UnlinkAt"/> (unused for files but reserved).
    /// Linux <c>0x200</c> (512), macOS <c>0x80</c> (128).</summary>
    internal static readonly int AT_REMOVEDIR = IsMac ? 0x80 : 0x200;

    /// <summary>Byte offset of <c>st_size</c> (an <see cref="long"/>) within <c>struct stat</c>. macOS
    /// <c>96</c>; Linux (glibc x86-64/arm64) <c>48</c>. Verified empirically; cross-checked at runtime
    /// against <see cref="System.IO.RandomAccess.GetLength(Microsoft.Win32.SafeHandles.SafeFileHandle)"/>
    /// so a wrong layout fails closed rather than returning garbage.</summary>
    internal static readonly int StatSizeOffset = IsMac ? 96 : 48;

    /// <summary>Byte offset of <c>st_mtim(e)spec.tv_sec</c> (an <see cref="long"/>) within
    /// <c>struct stat</c>. macOS <c>48</c>; Linux (glibc x86-64/arm64) <c>88</c>.</summary>
    internal static readonly int StatMtimeSecOffset = IsMac ? 48 : 88;

    /// <summary>Size of the <c>struct stat</c> receive buffer — over-allocated (160) to cover macOS
    /// (144) and both Linux ABIs (x86-64 144, arm64 128) so <c>fstat</c> never writes out of bounds.</summary>
    internal const int StatBufferSize = 160;

    /// <summary>Byte offset of <c>d_name</c> within <c>struct dirent</c>. macOS <c>21</c>; Linux <c>19</c>.
    /// <c>d_name</c> is a NUL-terminated name; only it is read (never the platform-variant numeric
    /// fields).</summary>
    internal static readonly int DirentNameOffset = IsMac ? 21 : 19;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    internal static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Link(string existingPath, string newPath);

    /// <summary><c>openat(2)</c> — open <paramref name="path"/> resolved <b>relative to the directory
    /// descriptor</b> <paramref name="dirfd"/> (never a re-resolvable absolute string). Combined with
    /// <see cref="O_NOFOLLOW"/> per component this is the race-free confinement primitive.</summary>
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int OpenAt(int dirfd, string path, int flags, uint mode);

    /// <summary><c>fstat(2)</c> into a caller-provided <see cref="StatBufferSize"/>-byte buffer. Only
    /// <c>st_size</c>/<c>st_mtime</c> are read, at platform offsets, with a size cross-check.</summary>
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    internal static partial int FStat(int fd, Span<byte> statBuffer);

    /// <summary><c>fchmod(2)</c> — set an open descriptor's permission bits. Used to fix a freshly-created
    /// temp to 0600 <b>independently of</b> <see cref="OpenAt"/>'s <c>mode</c> argument, which is a
    /// <b>variadic</b> parameter that a fixed P/Invoke signature cannot pass reliably on Apple-silicon
    /// (arm64 macOS passes variadic args on the stack, so the register-placed mode is read as garbage).
    /// <c>fchmod</c> is non-variadic, so its mode marshals correctly on every platform.</summary>
    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    internal static partial int FChmod(int fd, uint mode);

    /// <summary><c>unlinkat(2)</c> — remove <paramref name="path"/> relative to <paramref name="dirfd"/>.</summary>
    [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int UnlinkAt(int dirfd, string path, int flags);

    /// <summary><c>linkat(2)</c> — atomically hard-link <paramref name="oldPath"/> (relative to
    /// <paramref name="oldDirfd"/>) to <paramref name="newPath"/> (relative to
    /// <paramref name="newDirfd"/>); fails with <see cref="EEXIST"/> if the target exists (the
    /// single-winner publish primitive, now anchored to a confined parent descriptor).</summary>
    [LibraryImport("libc", EntryPoint = "linkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int LinkAt(int oldDirfd, string oldPath, int newDirfd, string newPath, int flags);

    /// <summary><c>fdopendir(3)</c> — turn a directory descriptor into a <c>DIR*</c> stream for
    /// <see cref="ReadDir"/>. The stream owns the descriptor and closes it on <see cref="CloseDir"/>.</summary>
    [LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    internal static partial nint FdOpenDir(int fd);

    /// <summary><c>readdir(3)</c> — next entry (a <c>struct dirent*</c>) from a <c>DIR*</c>, or
    /// <see cref="nint.Zero"/> at end of stream. Only <c>d_name</c> is read.</summary>
    [LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
    internal static partial nint ReadDir(nint dir);

    /// <summary><c>closedir(3)</c> — close a <c>DIR*</c> and its underlying descriptor.</summary>
    [LibraryImport("libc", EntryPoint = "closedir", SetLastError = true)]
    internal static partial int CloseDir(nint dir);
}
