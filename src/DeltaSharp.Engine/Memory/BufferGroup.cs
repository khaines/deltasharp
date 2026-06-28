namespace DeltaSharp.Engine.Memory;

/// <summary>
/// An exception-safe owner of several <see cref="OwnedBuffer"/>s allocated together while building one columnar
/// batch or row buffer (ADR-0013). Multi-buffer construction can fail partway through — a later allocation throws
/// <see cref="OutOfMemoryException"/>, or a validation step rejects the input — and without a single owner the
/// buffers already allocated would leak. A <see cref="BufferGroup"/> tracks every buffer it hands out and, on
/// <see cref="Dispose"/>, releases them all (even if one release throws), so a <c>using</c> reclaims everything on
/// any failure path.
/// </summary>
/// <remarks>
/// <para>
/// The intended pattern is <c>using</c> + <see cref="Detach"/>: allocate through the group while constructing, then
/// <see cref="Detach"/> the buffers into the finished object on the success path. If anything throws before
/// <see cref="Detach"/>, the <c>using</c> disposes the group and reclaims every buffer; after a successful
/// <see cref="Detach"/> the group owns nothing, so its <see cref="Dispose"/> releases nothing and the new owner
/// holds the live buffers:
/// </para>
/// <code>
/// using var group = new BufferGroup(allocator);
/// OwnedBuffer values = group.Allocate(valueBytes);
/// OwnedBuffer validity = group.Allocate(validityBytes);
/// // ... more construction that may throw ...
/// return new NativeColumnVector(group.Detach()); // success: ownership transferred out
/// </code>
/// <para>A group is single-owner and not thread-safe; build on one thread.</para>
/// </remarks>
public sealed class BufferGroup : IDisposable
{
    private readonly NativeMemoryAllocator _allocator;
    private readonly List<OwnedBuffer> _buffers = [];
    private bool _disposed;
    private bool _detached;

    /// <summary>Creates a group that allocates through <paramref name="allocator"/> and owns what it hands out.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is null.</exception>
    public BufferGroup(NativeMemoryAllocator allocator)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        _allocator = allocator;
    }

    /// <summary>
    /// Allocates a buffer through the underlying allocator and tracks it for disposal. If tracking the buffer
    /// itself fails, the just-allocated buffer is disposed before the exception propagates, so nothing leaks.
    /// </summary>
    /// <param name="byteCount">The number of usable bytes to allocate.</param>
    /// <returns>A buffer owned by this group until <see cref="Detach"/> or <see cref="Dispose"/>.</returns>
    /// <exception cref="ObjectDisposedException">The group has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is negative.</exception>
    /// <exception cref="OutOfMemoryException">An off-heap allocation failed.</exception>
    public OwnedBuffer Allocate(int byteCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        OwnedBuffer buffer = _allocator.Allocate(byteCount);
        try
        {
            _buffers.Add(buffer);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        return buffer;
    }

    /// <summary>
    /// Transfers ownership of the tracked buffers to the caller and clears the group. After detaching, the group
    /// owns nothing, so disposing it (for example at the end of a <c>using</c>) releases nothing — the caller is
    /// now responsible for disposing the returned buffers.
    /// </summary>
    /// <returns>The buffers, in allocation order; an empty array if none are tracked.</returns>
    /// <exception cref="ObjectDisposedException">The group has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Ownership has already been transferred by a prior <see cref="Detach"/>.</exception>
    public OwnedBuffer[] Detach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_detached)
        {
            throw new InvalidOperationException("BufferGroup ownership has already been transferred by a prior Detach().");
        }

        _detached = true;
        OwnedBuffer[] detached = [.. _buffers];
        _buffers.Clear();
        return detached;
    }

    /// <summary>
    /// Disposes every tracked buffer in reverse allocation order. A failure disposing one buffer does not strand
    /// the rest: all are disposed and the failures are aggregated into a single <see cref="AggregateException"/>.
    /// Disposing an already-disposed group, or one emptied by <see cref="Detach"/>, is a no-op.
    /// </summary>
    /// <exception cref="AggregateException">One or more buffers threw while being disposed.</exception>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<Exception>? failures = null;
        for (int i = _buffers.Count - 1; i >= 0; i--)
        {
            try
            {
                _buffers[i].Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        _buffers.Clear();

        if (failures is not null)
        {
            throw new AggregateException("One or more buffers failed to dispose.", failures);
        }
    }
}
