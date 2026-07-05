namespace DeltaSharp.Storage.Backends;

/// <summary>
/// The engine-internal storage adapter contract (design §2.13.1, STORY-05.1.3 / #182). It is
/// deliberately <b>not</b> a public plug-in SPI: backends (S3, ADLS Gen2, GCS, PVC/POSIX) are
/// selected internally by URI scheme + options. Every method is asynchronous and carries a
/// <see cref="CancellationToken"/>.
/// </summary>
/// <remarks>
/// The load-bearing member is <see cref="PutIfAbsentAsync"/> — the <b>atomic single-winner</b>
/// conditional-create that makes a Delta <c>_delta_log/&lt;N&gt;.json</c> commit atomic
/// (design §2.11.1). Commit atomicity depends on this single-object put-if-absent, never on an
/// atomic directory rename (design §2.13.2). <see cref="ListAsync"/> is for discovery only and is
/// <b>never</b> the source of active-file truth (the committed log is).
/// </remarks>
internal interface IStorageBackend
{
    /// <summary>Opens a read stream over <c>[offset, offset + length)</c> of <paramref name="path"/> —
    /// the range GET used for Parquet footers and selective row-group reads (design §2.9.1).</summary>
    /// <exception cref="DeltaStorageException">The path escapes the root
    /// (<see cref="StorageErrorKind.PathNotConfined"/>) or does not exist
    /// (<see cref="StorageErrorKind.NotFound"/>).</exception>
    ValueTask<Stream> ReadRangeAsync(string path, long offset, long length, CancellationToken cancellationToken);

    /// <summary>Opens a read stream over the whole object at <paramref name="path"/>.</summary>
    /// <exception cref="DeltaStorageException">The path escapes the root or does not exist.</exception>
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>Opens a staged write stream for <paramref name="path"/>. The bytes are written to a
    /// temporary object and are <b>published atomically only when the caller signals success</b> — the
    /// returned stream implements <see cref="ICompletableWriteStream"/>, and the caller must invoke
    /// <see cref="ICompletableWriteStream.CompleteAsync"/> after a successful write. Disposing the
    /// stream <b>without</b> completing (a faulted/abandoned write) discards the staged bytes and never
    /// publishes a partial/torn destination (design §2.13.2 "write to temp + fsync + rename").</summary>
    /// <exception cref="DeltaStorageException">The path escapes the root.</exception>
    ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken);

    /// <summary>Atomically creates <paramref name="path"/> with <paramref name="content"/> iff it does
    /// not already exist — the single-winner commit primitive (design §2.11.1/§2.13.1). Returns
    /// <see langword="true"/> iff <b>this</b> caller created the object; a caller that lost the race
    /// gets <see langword="false"/>, never an exception.</summary>
    /// <exception cref="DeltaStorageException">The path escapes the root, or the outcome is ambiguous
    /// (<see cref="StorageErrorKind.RetryUnsafeAmbiguous"/>).</exception>
    ValueTask<bool> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>Lists the objects whose path starts with <paramref name="prefix"/>. For discovery only —
    /// never active-file truth (design §2.13.1).</summary>
    /// <exception cref="DeltaStorageException">The prefix escapes the root.</exception>
    IAsyncEnumerable<StorageObjectInfo> ListAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Deletes <paramref name="path"/>. Idempotent: a missing object is a no-op, not an error
    /// (design §2.13.1 VACUUM/cleanup semantics).</summary>
    /// <exception cref="DeltaStorageException">The path escapes the root.</exception>
    ValueTask DeleteAsync(string path, CancellationToken cancellationToken);

    /// <summary>Returns metadata for <paramref name="path"/>, or <see langword="null"/> if it does not
    /// exist (existence/size/mtime probe — design §2.13.1).</summary>
    /// <exception cref="DeltaStorageException">The path escapes the root.</exception>
    ValueTask<StorageObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken);
}
