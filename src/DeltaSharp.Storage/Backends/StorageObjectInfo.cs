namespace DeltaSharp.Storage.Backends;

/// <summary>
/// Metadata for a single stored object returned by <see cref="IStorageBackend.HeadAsync"/> and
/// <see cref="IStorageBackend.ListAsync"/> (design §2.13.1). <see cref="ETag"/> is optional: not
/// every backend surfaces one (POSIX/PVC does not natively).
/// </summary>
/// <param name="Path">The object's backend path (root-relative for the local backend).</param>
/// <param name="Length">The object's length in bytes.</param>
/// <param name="LastModifiedUtc">The last-modification instant, in UTC.</param>
/// <param name="ETag">An opaque entity tag when the backend provides one; otherwise <see langword="null"/>.</param>
internal sealed record StorageObjectInfo(string Path, long Length, DateTime LastModifiedUtc, string? ETag);
