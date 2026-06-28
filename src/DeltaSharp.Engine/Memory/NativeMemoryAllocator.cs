namespace DeltaSharp.Engine.Memory;

/// <summary>
/// The single source of <see cref="OwnedBuffer"/>s for the engine's columnar batches and binary rows (ADR-0013).
/// It routes each request by size — small scratch onto a pooled GC-heap array, everything else onto
/// 64-byte-aligned off-heap memory — and tracks <b>live</b> bytes and counts for the two pools separately through
/// <see cref="Interlocked"/>, so accounting is correct under concurrent allocation and disposal.
/// </summary>
/// <remarks>
/// <para>
/// Off-heap is the default because it is GC-invisible, deterministically reclaimed, SIMD-aligned, and avoids
/// Large-Object-Heap / gen-2 pauses on large buffers; the GC-heap path is reserved for small, short-lived scratch
/// (<see cref="ScratchThreshold"/> bytes or fewer) where a pooled array is cheaper. A large request is
/// <b>never</b> satisfied from the scratch path.
/// </para>
/// <para>
/// Because every successful <see cref="Allocate"/> increments a live counter and every release decrements it, the
/// four <c>Live*</c> counters return to zero once all handed-out buffers are disposed — the leak signal the tests
/// assert. <see cref="FinalizedWithoutDispose"/> counts native buffers reclaimed by the finalizer safety net
/// instead of a deterministic dispose. The unified execution/storage pools and spill-to-disk triggers that ADR-0013
/// layers on top of this ownership model are deferred to STORY-02.3.2 (#138).
/// </para>
/// </remarks>
public sealed class NativeMemoryAllocator
{
    /// <summary>
    /// The default <see cref="ScratchThreshold"/>: 4&#160;KiB. Requests this size or smaller take the pooled
    /// GC-heap scratch path; larger requests take aligned off-heap memory. Well under the ~85&#160;KB
    /// Large-Object-Heap threshold so scratch never churns the LOH.
    /// </summary>
    public const int DefaultScratchThresholdBytes = 4096;

    private long _liveNativeBytes;
    private long _liveNativeCount;
    private long _liveScratchBytes;
    private long _liveScratchCount;
    private long _finalizedWithoutDispose;

    /// <summary>Creates an allocator using the <see cref="DefaultScratchThresholdBytes"/> scratch threshold.</summary>
    public NativeMemoryAllocator()
        : this(DefaultScratchThresholdBytes)
    {
    }

    /// <summary>Creates an allocator routing requests of <paramref name="scratchThreshold"/> bytes or fewer to pooled GC-heap scratch.</summary>
    /// <param name="scratchThreshold">The inclusive upper bound, in bytes, for the GC-heap scratch path.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scratchThreshold"/> is negative.</exception>
    public NativeMemoryAllocator(int scratchThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scratchThreshold);
        ScratchThreshold = scratchThreshold;
    }

    /// <summary>The inclusive byte threshold at or below which a request is served from pooled GC-heap scratch.</summary>
    public int ScratchThreshold { get; }

    /// <summary>Live off-heap (native) bytes currently held by undisposed buffers from this allocator.</summary>
    public long LiveNativeBytes => Interlocked.Read(ref _liveNativeBytes);

    /// <summary>Count of live off-heap (native) buffers currently undisposed from this allocator.</summary>
    public long LiveNativeCount => Interlocked.Read(ref _liveNativeCount);

    /// <summary>Live GC-heap scratch bytes currently held by undisposed buffers from this allocator.</summary>
    public long LiveScratchBytes => Interlocked.Read(ref _liveScratchBytes);

    /// <summary>Count of live GC-heap scratch buffers currently undisposed from this allocator.</summary>
    public long LiveScratchCount => Interlocked.Read(ref _liveScratchCount);

    /// <summary>
    /// Count of native buffers reclaimed by the finalizer safety net because a deterministic
    /// <see cref="OwnedBuffer.Dispose()"/> was missed. Stays zero when every buffer is disposed; a positive value
    /// flags a leak to fix.
    /// </summary>
    public long FinalizedWithoutDispose => Interlocked.Read(ref _finalizedWithoutDispose);

    /// <summary>
    /// Allocates an <see cref="OwnedBuffer"/> of <paramref name="byteCount"/> usable bytes. Requests at or below
    /// <see cref="ScratchThreshold"/> are served from pooled GC-heap scratch (<see cref="GcHeapBuffer"/>); larger
    /// requests are served from 64-byte-aligned off-heap memory (<see cref="AlignedNativeBuffer"/>). The matching
    /// live counter is incremented only after the allocation succeeds.
    /// </summary>
    /// <param name="byteCount">The number of usable bytes to allocate.</param>
    /// <returns>A buffer the caller owns and must dispose exactly once.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is negative.</exception>
    /// <exception cref="OutOfMemoryException">An off-heap allocation failed (no counter is moved).</exception>
    public OwnedBuffer Allocate(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        if (byteCount <= ScratchThreshold)
        {
            var scratch = new GcHeapBuffer(this, byteCount);
            Interlocked.Add(ref _liveScratchBytes, byteCount);
            Interlocked.Increment(ref _liveScratchCount);
            return scratch;
        }

        // Allocate first; only on success do we publish a counter, so a failed AlignedAlloc cannot leak accounting.
        AlignedNativeBuffer native = AlignedNativeBuffer.Allocate(this, byteCount);
        Interlocked.Add(ref _liveNativeBytes, byteCount);
        Interlocked.Increment(ref _liveNativeCount);
        return native;
    }

    /// <summary>Decrements the matching live counters when <paramref name="buffer"/> is released exactly once.</summary>
    internal void OnReleased(OwnedBuffer buffer)
    {
        if (buffer.IsNative)
        {
            Interlocked.Add(ref _liveNativeBytes, -buffer.Length);
            Interlocked.Decrement(ref _liveNativeCount);
        }
        else
        {
            Interlocked.Add(ref _liveScratchBytes, -buffer.Length);
            Interlocked.Decrement(ref _liveScratchCount);
        }
    }

    /// <summary>Records that a native buffer was reclaimed by its finalizer because a deterministic dispose was missed.</summary>
    internal void OnFinalizedWithoutDispose() => Interlocked.Increment(ref _finalizedWithoutDispose);
}
