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

    /// <summary>The <c>EEXIST</c> errno (17 on Linux and macOS): the link target already exists.</summary>
    internal const int EEXIST = 17;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    internal static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Link(string existingPath, string newPath);
}
