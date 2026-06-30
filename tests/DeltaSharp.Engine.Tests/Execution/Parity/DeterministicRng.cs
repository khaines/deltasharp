namespace DeltaSharp.Engine.Tests.Execution.Parity;

/// <summary>
/// A tiny, fully deterministic pseudo-random generator (SplitMix64) used to seed the parity suite's
/// schema / expression-tree / batch synthesis (STORY-03.5.2 AC5). It is deliberately <b>not</b>
/// <see cref="System.Random"/>: <see cref="System.Random"/>'s sequence is not contractually stable
/// across .NET versions, whereas SplitMix64 is a fixed, well-documented integer recurrence, so the
/// same <c>seed</c> yields byte-identical draws on every runtime and every CI run — the reproducibility
/// the suite reports in its mismatch diagnostics and replays a regression from.
/// </summary>
/// <remarks>
/// SplitMix64 (Steele, Lea &amp; Flood) advances a 64-bit state by the golden-ratio increment and
/// finalizes with two xor-shift-multiply mixes. It has no hidden global state and no allocation, so a
/// run is reduced entirely to its <see cref="Seed"/>.
/// </remarks>
internal sealed class DeterministicRng
{
    private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;
    private ulong _state;

    /// <summary>Creates a generator pinned to <paramref name="seed"/> (the value emitted in diagnostics).</summary>
    public DeterministicRng(ulong seed)
    {
        Seed = seed;
        _state = seed;
    }

    /// <summary>The fixed seed this generator was created from; emitted in every mismatch diagnostic.</summary>
    public ulong Seed { get; }

    /// <summary>The next 64-bit draw (SplitMix64).</summary>
    public ulong NextUInt64()
    {
        _state += GoldenGamma;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A draw in <c>[0, maxExclusive)</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExclusive"/> is not positive.</exception>
    public int Next(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        return (int)(NextUInt64() % (ulong)maxExclusive);
    }

    /// <summary>A draw in <c>[minInclusive, maxExclusive)</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is empty.</exception>
    public int Next(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "maxExclusive must exceed minInclusive.");
        }

        return minInclusive + Next(maxExclusive - minInclusive);
    }

    /// <summary>A fair coin.</summary>
    public bool NextBool() => (NextUInt64() & 1UL) != 0UL;

    /// <summary>A draw in <c>[0.0, 1.0)</c> with 53 bits of mantissa entropy.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>A signed long in <c>[minInclusive, maxInclusive]</c>.</summary>
    public long NextLong(long minInclusive, long maxInclusive)
    {
        ulong span = (ulong)(maxInclusive - minInclusive) + 1UL;
        return minInclusive + (long)(NextUInt64() % span);
    }

    /// <summary>Picks a uniformly random element of <paramref name="items"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="items"/> is empty.</exception>
    public T Pick<T>(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("Cannot pick from an empty list.", nameof(items));
        }

        return items[Next(items.Count)];
    }
}
