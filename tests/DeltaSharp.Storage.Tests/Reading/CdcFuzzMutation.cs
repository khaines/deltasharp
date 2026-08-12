using DeltaSharp.TestSupport;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// The single, shared fuzz mutation strategy for the CDF <c>cdc</c>-file read-door suites. Extracted so the
/// live fuzz (<see cref="ChangeFeedCdcFuzzTests"/>) and the pinned #647/#716 regression
/// (<see cref="ChangeFeedCdcBoundedDecodeTests"/>) mutate bytes IDENTICALLY — a divergent copy-paste would let
/// the pinned repro silently drift off the strategy the fuzz actually explores.
/// </summary>
internal static class CdcFuzzMutation
{
    /// <summary>Applies one mutation to <paramref name="original"/> using <paramref name="random"/>: a random
    /// overwrite, a truncation, a handful of bit-flips, or trailing garbage. The four arms and their draw
    /// order MUST stay byte-for-byte stable so a seeded replay is reproducible.</summary>
    internal static byte[] Mutate(byte[] original, Random random)
    {
        switch (random.Next(4))
        {
            case 0: // random overwrite (arbitrary length, including empty)
                byte[] noise = new byte[random.Next(0, original.Length + 8)];
                random.NextBytes(noise);
                return noise;

            case 1: // truncate to a random shorter length (including 0)
                return original[..random.Next(0, original.Length)];

            case 2: // flip a handful of random bits
                byte[] flipped = (byte[])original.Clone();
                int flips = random.Next(1, 8);
                for (int f = 0; f < flips; f++)
                {
                    flipped[random.Next(flipped.Length)] ^= (byte)(1 << random.Next(8));
                }

                return flipped;

            default: // append trailing garbage (corrupts the Parquet footer-length interpretation)
                byte[] appended = new byte[original.Length + random.Next(1, 32)];
                original.CopyTo(appended, 0);
                for (int k = original.Length; k < appended.Length; k++)
                {
                    appended[k] = (byte)random.Next(256);
                }

                return appended;
        }
    }

    /// <summary>Replays the mutation stream for <paramref name="scope"/>/<paramref name="baseSeed"/> up to and
    /// including <paramref name="iteration"/>, returning that iteration's mutated bytes — the deterministic
    /// pin path both the fuzz and the regression share.</summary>
    internal static byte[] ReplayToIteration(byte[] original, int baseSeed, string scope, int iteration)
    {
        var random = new Random(TestSeed.Combine(baseSeed, scope));
        byte[] mutated = original;
        for (int i = 0; i <= iteration; i++)
        {
            mutated = Mutate(original, random);
        }

        return mutated;
    }
}
