namespace DeltaSharp.Engine.Memory;

/// <summary>
/// A spillable memory consumer — a sort buffer, hash aggregate, or join build side — that the
/// <see cref="UnifiedMemoryManager"/> can ask to release reserved bytes by serializing its in-memory state to a spill
/// target supplied by later runtime layers (local disk / object store; ADR-0004). This is the spill trigger of
/// ADR-0013: when a reservation would push a pool over budget, the manager invokes the owning task's spillable
/// reservations <b>before</b> it rejects the allocation, so memory pressure causes a spill rather than an
/// <see cref="OutOfMemoryException"/>.
/// </summary>
/// <remarks>
/// <para>
/// The manager is the single owner of reservation accounting: after a successful spill it decrements the consumer's
/// <see cref="MemoryReservation"/> (and the pool / task totals) by the returned byte count. A <see cref="Spill"/>
/// implementation therefore frees its <em>physical</em> buffers (off-heap memory, pooled arrays) and returns how many
/// reserved bytes that freed — it MUST NOT itself release those bytes through the manager (no
/// <see cref="MemoryReservation.Dispose"/> for the spilled portion), or the bytes would be double-released.
/// </para>
/// <para>
/// <b>Threading:</b> <see cref="Spill"/> is invoked while the manager holds its coordinating lock, so a v1
/// implementation must be fast and MUST NOT re-enter the manager (no reserve / release from inside
/// <see cref="Spill"/>); it should only free buffers and return the count. Moving blocking spill I/O off the lock is a
/// documented deferral (see <c>docs/engineering/design/memory-model.md</c>).
/// </para>
/// </remarks>
public interface ISpillable
{
    /// <summary>
    /// Releases up to <paramref name="bytesRequested"/> bytes of this consumer's reservation by spilling, and returns
    /// the number of reserved bytes actually freed (<c>0</c> if nothing can be spilled right now). The return value
    /// must not exceed the consumer's current reservation; the manager defensively clamps it if it does.
    /// </summary>
    /// <param name="bytesRequested">The number of bytes the manager would like freed (non-negative).</param>
    /// <returns>The number of reserved bytes actually freed (<c>0</c> ≤ result ≤ the consumer's reserved bytes).</returns>
    long Spill(long bytesRequested);
}

/// <summary>
/// A spill callback: releases up to <paramref name="bytesRequested"/> bytes and returns how many were actually freed.
/// The same contract as <see cref="ISpillable.Spill(long)"/>, for callers that prefer a delegate over an interface.
/// </summary>
/// <param name="bytesRequested">The number of bytes the manager would like freed (non-negative).</param>
/// <returns>The number of reserved bytes actually freed.</returns>
public delegate long SpillCallback(long bytesRequested);

/// <summary>Adapts a <see cref="SpillCallback"/> delegate to the <see cref="ISpillable"/> interface the manager consumes.</summary>
public sealed class DelegateSpillable : ISpillable
{
    private readonly SpillCallback _spill;

    /// <summary>Wraps <paramref name="spill"/> so a lambda can be registered as a spillable reservation.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="spill"/> is null.</exception>
    public DelegateSpillable(SpillCallback spill)
    {
        ArgumentNullException.ThrowIfNull(spill);
        _spill = spill;
    }

    /// <inheritdoc/>
    public long Spill(long bytesRequested) => _spill(bytesRequested);
}
