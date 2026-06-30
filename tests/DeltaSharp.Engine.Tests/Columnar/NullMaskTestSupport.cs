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

    /// <summary>
    /// Encodes lanes into the bit-packed representation but <b>deliberately violates the canonical
    /// <c>value ⊆ valid</c> invariant</c>: every NULL lane (validity bit <c>0</c>) is given a garbage value
    /// bit of <c>1</c> (so <c>v=0, b=1</c>), and every trailing PADDING lane (index <c>&gt;= length</c>) of
    /// both bitmaps is set to <c>1</c>. Valid lanes keep their real value. A kernel that masks value bits by
    /// validity (and canonicalizes its output padding) must still produce a canonical, oracle-identical
    /// output value bitmap from this non-canonical input; a kernel that drops the <c>&amp; validity</c> mask
    /// (or the tail mask) leaks these garbage bits and fails the value-bitmap parity check (#144, council
    /// finding #2).
    /// </summary>
    public static (byte[] Values, byte[] Validity) EncodePackedGarbageNulls(bool?[] lanes)
    {
        int byteCount = Math.Max(1, Bitmap.ByteCount(lanes.Length));
        var values = new byte[byteCount];
        var validity = new byte[byteCount];
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
            else
            {
                // Null lane: validity bit stays cleared, but seed a garbage value bit (v=0, b=1).
                Bitmap.Set(values, i, true);
            }
        }

        // Dirty the trailing padding lanes of both bitmaps so output canonicalization is exercised too.
        for (int i = lanes.Length; i < byteCount * 8; i++)
        {
            Bitmap.Set(values, i, true);
            Bitmap.Set(validity, i, true);
        }

        return (values, validity);
    }

    /// <summary>
    /// Packs a scalar <c>bool[]</c> value span gated by a validity bitmap into a canonical packed value
    /// bitmap (value bit set iff the lane is valid <i>and</i> <c>true</c>), with padding lanes left <c>0</c>.
    /// This is the byte-for-byte expected output value bitmap for the value-bitmap parity assertions.
    /// </summary>
    public static byte[] PackValues(ReadOnlySpan<bool> values, ReadOnlySpan<byte> validity, int length)
    {
        var packed = new byte[Math.Max(1, Bitmap.ByteCount(length))];
        for (int i = 0; i < length; i++)
        {
            if (Bitmap.Get(validity, i) && values[i])
            {
                Bitmap.Set(packed, i, true);
            }
        }

        return packed;
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
