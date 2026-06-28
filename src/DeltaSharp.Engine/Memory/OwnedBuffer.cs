namespace DeltaSharp.Engine.Memory;

/// <summary>
/// A single-owner, deterministically released byte buffer handed out by a
/// <see cref="NativeMemoryAllocator"/> (ADR-0013). It abstracts over an off-heap
/// <see cref="AlignedNativeBuffer"/> (the aligned default for columnar batches and binary rows) and a
/// GC-heap <see cref="GcHeapBuffer"/> (small short-lived scratch) so callers — vector builders,
/// row encoders, kernels — bind to one ownership contract regardless of where the bytes live.
/// </summary>
/// <remarks>
/// <para>
/// Ownership is <b>exactly once</b>: the backing memory is released the first time the buffer is
/// disposed (or, for native buffers, finalized as a safety net), and every later
/// <see cref="Dispose()"/> is a no-op. Disposal is guarded by an <see cref="System.Threading.Interlocked"/>
/// flag so the release and the allocator's live-counter decrement each happen once even under a race
/// between an explicit dispose and the finalizer.
/// </para>
/// <para>
/// A buffer is <b>not thread-safe for concurrent use</b>: one owner reads/writes it on one thread and
/// disposes it once. Only the owning allocator's diagnostic counters are safe for concurrent observation
/// (they are updated through <see cref="System.Threading.Interlocked"/>). For exception-safe ownership of
/// several buffers at once, use a <see cref="BufferGroup"/>.
/// </para>
/// </remarks>
public abstract class OwnedBuffer : IDisposable
{
    private readonly NativeMemoryAllocator _owner;

    // 0 = live, 1 = released. Interlocked-guarded so release + accounting run exactly once.
    private int _disposed;

    /// <summary>Initializes the base buffer, recording the owning allocator and usable byte count.</summary>
    /// <param name="owner">The allocator that produced this buffer and tracks its live byte/count totals.</param>
    /// <param name="length">The requested usable length, in bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    private protected OwnedBuffer(NativeMemoryAllocator owner, int length)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _owner = owner;
        Length = length;
    }

    /// <summary>The usable length of this buffer in bytes — the byte count originally requested.</summary>
    public int Length { get; }

    /// <summary>
    /// Whether the bytes live in off-heap native memory (<see langword="true"/>, an
    /// <see cref="AlignedNativeBuffer"/>) or on the GC heap via a pooled array (<see langword="false"/>, a
    /// <see cref="GcHeapBuffer"/> scratch rental). Native and scratch bytes are accounted in separate
    /// allocator counters.
    /// </summary>
    public abstract bool IsNative { get; }

    /// <summary>Whether this buffer has already been released (disposed or finalized).</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// A writable view over the buffer's <see cref="Length"/> usable bytes. For an
    /// <see cref="AlignedNativeBuffer"/> the span starts at the 64-byte-aligned base address.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The buffer has already been disposed.</exception>
    public Span<byte> AsSpan()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return AsSpanCore();
    }

    /// <summary>Releases the backing memory exactly once and decrements the owner's live counters.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the backing memory exactly once. Called with <paramref name="disposing"/> = <see langword="true"/>
    /// from <see cref="Dispose()"/> and with <see langword="false"/> from a finalizer safety net; either way the
    /// owning allocator's live byte/count totals are decremented exactly once. A finalizer-driven release
    /// (<paramref name="disposing"/> = <see langword="false"/>) additionally bumps the allocator's
    /// <see cref="NativeMemoryAllocator.FinalizedWithoutDispose"/> diagnostic, because reaching the finalizer
    /// means a deterministic <see cref="Dispose()"/> was missed.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>; <see langword="false"/> from a finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Release();
        _owner.OnReleased(this);
        if (!disposing)
        {
            _owner.OnFinalizedWithoutDispose();
        }
    }

    /// <summary>
    /// Releases the concrete backing memory (free native memory, or return the pooled array). Invoked at most
    /// once, under the disposal guard. Must be safe to run from a finalizer for native implementations.
    /// </summary>
    protected abstract void Release();

    /// <summary>Returns the usable byte span without re-checking disposal (the public <see cref="AsSpan"/> guards it).</summary>
    protected abstract Span<byte> AsSpanCore();
}
