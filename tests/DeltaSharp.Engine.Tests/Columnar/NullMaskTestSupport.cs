using DeltaSharp.Engine.Columnar;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Shared encoders/oracles for the STORY-02.6.2 (#144) branchless null-helper parity tests. Bridges the
/// scalar <see cref="NullPropagation"/> representation (<c>bool[]</c> values + validity bitmap) and the
/// vectorized <see cref="NullMasks"/> representation (bit-packed value bitmap + validity bitmap) so the
/// two can be compared lane-for-lane and byte-for-byte against the same random three-valued-logic input.
/// </summary>
internal static class NullMaskTestSupport
{
    /// <summary>Generates <paramref name="length"/> random three-valued-logic lanes (null = UNKNOWN).</summary>
    public static bool?[] RandomLanes(Random rng, int length)
    {
        var lanes = new bool?[length];
        for (int i = 0; i < length; i++)
        {
            lanes[i] = rng.Next(3) switch
            {
                0 => null,
                1 => false,
                _ => true,
            };
        }

        return lanes;
    }

    /// <summary>
    /// Generates <paramref name="length"/> random lanes at an approximate <paramref name="nullDensity"/>
    /// of nulls, so parity is exercised across the no-null, sparse, half, and dense regimes.
    /// </summary>
    public static bool?[] RandomLanes(Random rng, int length, double nullDensity)
    {
        var lanes = new bool?[length];
        for (int i = 0; i < length; i++)
        {
            lanes[i] = rng.NextDouble() < nullDensity ? null : rng.Next(2) == 1;
        }

        return lanes;
    }

    /// <summary>Encodes lanes into the scalar representation: a <c>bool[]</c> of values and a validity bitmap.</summary>
    public static (bool[] Values, byte[] Validity) EncodeScalar(bool?[] lanes)
    {
        var values = new bool[lanes.Length];
        var validity = new byte[Math.Max(1, Bitmap.ByteCount(lanes.Length))];
        for (int i = 0; i < lanes.Length; i++)
        {
            if (lanes[i] is bool b)
            {
                values[i] = b;
                Bitmap.Set(validity, i, true);
            }
        }

        return (values, validity);
    }

    /// <summary>Encodes lanes into the bit-packed representation: a value bitmap and a validity bitmap.</summary>
    public static (byte[] Values, byte[] Validity) EncodePacked(bool?[] lanes)
    {
        var values = new byte[Math.Max(1, Bitmap.ByteCount(lanes.Length))];
        var validity = new byte[Math.Max(1, Bitmap.ByteCount(lanes.Length))];
        for (int i = 0; i < lanes.Length; i++)
        {
            if (lanes[i] is bool b)
            {
                Bitmap.Set(validity, i, true);
                if (b)
                {
                    Bitmap.Set(values, i, true);
                }
            }
        }

        return (values, validity);
    }

    /// <summary>Decodes lane <paramref name="index"/> from a packed value bitmap gated by a validity bitmap.</summary>
    public static bool? DecodePacked(ReadOnlySpan<byte> values, ReadOnlySpan<byte> validity, int index)
        => Bitmap.Get(validity, index) ? Bitmap.Get(values, index) : null;

    /// <summary>Decodes lane <paramref name="index"/> from a scalar <c>bool[]</c> gated by a validity bitmap.</summary>
    public static bool? DecodeScalar(ReadOnlySpan<bool> values, ReadOnlySpan<byte> validity, int index)
        => Bitmap.Get(validity, index) ? values[index] : null;

    /// <summary>Builds a validity bitmap with each row valid/null per the lanes (used for propagate parity).</summary>
    public static byte[] ValidityBitmap(bool?[] lanes)
    {
        var validity = new byte[Math.Max(1, Bitmap.ByteCount(lanes.Length))];
        for (int i = 0; i < lanes.Length; i++)
        {
            if (lanes[i] is not null)
            {
                Bitmap.Set(validity, i, true);
            }
        }

        return validity;
    }
}
