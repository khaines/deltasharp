namespace DeltaSharp.Engine.Columnar.Arrow;

/// <summary>
/// Declares who owns the Apache Arrow buffers behind a batch imported through
/// <see cref="ArrowBatchConverter.FromArrow(global::Apache.Arrow.RecordBatch, ArrowImportOwnership)"/>,
/// and therefore who is responsible for releasing them (STORY-02.2.2 #136, AC4). The boundary is
/// explicit so a producer never double-frees and a consumer never leaks: the import is zero-copy in
/// both modes, only the disposal contract differs.
/// </summary>
public enum ArrowImportOwnership
{
    /// <summary>
    /// The caller retains ownership of the source <c>RecordBatch</c> and must keep it alive for at
    /// least as long as the returned batch (and any slice/selection derived from it) is used, then
    /// dispose it. Disposing the returned <see cref="ArrowColumnBatch"/> releases nothing — it only
    /// closes the view. This is the default and the right choice when the source outlives the import
    /// (e.g. a reader that recycles batches).
    /// </summary>
    Borrowed = 0,

    /// <summary>
    /// Ownership of the source <c>RecordBatch</c> transfers to the returned
    /// <see cref="ArrowColumnBatch"/>: disposing it disposes the source exactly once (idempotent
    /// under repeated <c>Dispose</c>), and the caller must not use or dispose the source afterward.
    /// This is the right choice when the import is the sole consumer of a freshly produced batch.
    /// </summary>
    /// <remarks>
    /// This is the <b>sharp</b> mode: disposing the batch frees the source Arrow buffers, so any
    /// <see cref="ColumnVector"/>, <see cref="ReadOnlySpan{T}"/>, slice, or selection previously vended
    /// from the batch is <b>invalidated</b> by that dispose. Reading such a view afterward is a
    /// use-after-free with undefined results (it may throw, return recycled bytes, or crash); the
    /// batch's <see cref="ObjectDisposedException"/> guards are disposal hygiene on the batch accessors,
    /// not memory-safety protection for views already handed out. Keep no vended view past the dispose,
    /// or copy out the values first.
    /// </remarks>
    Transfer = 1,
}
