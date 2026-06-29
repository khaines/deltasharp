using DeltaSharp.Engine.Memory;

namespace DeltaSharp.Engine.Tests.Memory;

/// <summary>
/// A test <see cref="ISpillable"/> that records how often and how much it was asked to spill, and frees up to a
/// configurable amount of "held" bytes. <see cref="Held"/> = <see cref="long.MaxValue"/> models a fully spillable
/// consumer (it frees whatever the manager asks, up to the reservation); a smaller value models a partially spillable
/// one. <see cref="Enabled"/> = <see langword="false"/> models a consumer that currently cannot spill (returns 0).
/// </summary>
internal sealed class CountingSpillable : ISpillable
{
    private long _held;

    public CountingSpillable(long held = long.MaxValue) => _held = held;

    /// <summary>When false, <see cref="Spill"/> frees nothing (still counting the call) — models an unspillable moment.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many times <see cref="Spill"/> was invoked.</summary>
    public int SpillCalls { get; private set; }

    /// <summary>Total bytes this consumer has freed across all spills.</summary>
    public long TotalSpilled { get; private set; }

    /// <summary>The <c>bytesRequested</c> of the most recent <see cref="Spill"/> call.</summary>
    public long LastRequested { get; private set; }

    /// <inheritdoc/>
    public long Spill(long bytesRequested)
    {
        SpillCalls++;
        LastRequested = bytesRequested;
        if (!Enabled)
        {
            return 0;
        }

        long freed = Math.Min(bytesRequested, _held);
        if (freed < 0)
        {
            freed = 0;
        }

        _held -= freed;
        TotalSpilled += freed;
        return freed;
    }
}
