namespace DeltaSharp.Storage.Backends;

/// <summary>
/// A staged write stream that publishes its destination <b>only when the caller signals success</b>
/// via <see cref="CompleteAsync"/> (design §2.13.2 "write to temp + fsync + rename"). The write
/// stream returned by <see cref="IStorageBackend.OpenWriteAsync"/> implements this contract:
/// <list type="bullet">
/// <item>the destination is <b>not</b> visible until <see cref="CompleteAsync"/> returns;</item>
/// <item>disposing the stream <b>without</b> calling <see cref="CompleteAsync"/> (a faulted or
/// abandoned write) discards the staged bytes and never publishes a partial/torn destination.</item>
/// </list>
/// This makes publication an explicit, opt-in act, so a half-written or exception-interrupted
/// producer can never leave a readable-but-incomplete object at the destination path.
/// </summary>
internal interface ICompletableWriteStream
{
    /// <summary>Durably flushes the staged bytes and atomically publishes them to the destination.
    /// Idempotent: a second call after a successful publish is a no-op.</summary>
    /// <exception cref="DeltaStorageException">The destination already exists
    /// (<see cref="StorageErrorKind.AlreadyExists"/>) or the publish failed
    /// (<see cref="StorageErrorKind.Transient"/>/<see cref="StorageErrorKind.RetryUnsafeAmbiguous"/>).</exception>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
}
