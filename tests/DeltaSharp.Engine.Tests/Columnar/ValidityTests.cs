using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-02.6.1 (#143) validity bitmap contracts: the absent bitmap is all-valid with no
/// synthetic allocation (AC1), a present bitmap is read Arrow LSB-first plus the logical offset
/// (AC2), and null-count utilities are correct and deterministic across all-null / no-null / empty /
/// sliced shapes (AC4). The three-valued-logic propagation (AC3) lives in
/// <see cref="NullPropagationTests"/>.
/// </summary>
public class ValidityTests
{
    // ----------------------------------------------------------------------------------------
    // AC1 — no validity bitmap => all-valid, without allocating a synthetic bitmap.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void AllValid_HasNoBitmap_AndReportsEveryRowValid()
    {
        Validity v = Validity.AllValid(5);

        Assert.False(v.HasBitmap); // no buffer materialized
        Assert.True(v.Bits.IsEmpty);
        Assert.Equal(5, v.Length);
        Assert.Equal(0, v.CountNulls());
        Assert.Equal(5, v.CountValid());
        for (int i = 0; i < 5; i++)
        {
            Assert.True(v.IsValid(i));
            Assert.False(v.IsNull(i));
        }
    }

    [Fact]
    public void EmptyBitmapSpan_IsEquivalentToAllValid()
    {
        // Passing an empty buffer explicitly is the same "no validity buffer => all valid" contract.
        var v = new Validity(ReadOnlySpan<byte>.Empty, offset: 0, length: 3);

        Assert.False(v.HasBitmap);
        Assert.Equal(0, v.CountNulls());
        Assert.True(v.IsValid(2));
    }

    [Fact]
    public void AllValid_QueryPath_DoesNotAllocate()
    {
        // AC1: querying null status on a no-bitmap vector allocates nothing — no synthetic all-ones
        // buffer is built. Validity is a ref struct, so the whole probe stays on the stack.
        int Probe()
        {
            Validity v = Validity.AllValid(4096);
            int nulls = 0;
            for (int i = 0; i < v.Length; i++)
            {
                if (v.IsNull(i))
                {
                    nulls++;
                }
            }

            return nulls + v.CountNulls();
        }

        Probe(); // warm up the JIT for this exact path

        long before = GC.GetAllocatedBytesForCurrentThread();
        int result = Probe();
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, result);
        Assert.True(after - before <= 64, $"all-valid query allocated {after - before} bytes (expected ~0)");
    }

    [Fact]
    public void ColumnVector_TryGetValidity_NoNulls_ReturnsAllValid_WithoutSyntheticBitmap()
    {
        // AC1 at the vector contract level: a vector with no nulls surfaces validity as the absent
        // (all-valid) bitmap and the query does not allocate a synthetic buffer.
        MutableColumnVector vector = ColumnVectors.Create(IntegerType.Instance, 1024);
        for (int i = 0; i < 1024; i++)
        {
            vector.AppendValue(i);
        }

        int Probe()
        {
            return vector.TryGetValidity(out Validity v) && !v.HasBitmap ? v.CountNulls() : -1;
        }

        Probe(); // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        int result = Probe();
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, result); // TryGetValidity true, no bitmap, zero nulls
        Assert.True(after - before <= 64, $"no-null TryGetValidity allocated {after - before} bytes (expected ~0)");
    }

    [Fact]
    public void ColumnVector_TryGetValidity_WithNulls_FallsBackToPerRow()
    {
        // The base contract only guarantees the no-null fast path; a null-bearing vector that does
        // not surface a packed buffer returns false so callers use per-row IsNull. (A concrete
        // vector may override TryGetValidity to expose its buffer.)
        MutableColumnVector vector = ColumnVectors.Create(IntegerType.Instance, 4);
        vector.AppendValue(1);
        vector.AppendNull();
        vector.AppendValue(3);

        Assert.False(vector.TryGetValidity(out Validity validity));
        Assert.Equal(0, validity.Length); // default Validity; caller must honor the false result
        Assert.True(vector.IsNull(1));     // per-row fallback still authoritative
    }

    [Fact]
    public void Bitmap_CountNulls_EmptyBitmap_IsZero()
    {
        // The low-level utility agrees with the contract: no buffer => no nulls, no out-of-range read.
        Assert.Equal(0, Bitmap.CountNulls(ReadOnlySpan<byte>.Empty, offset: 0, length: 0));
        Assert.Equal(0, Bitmap.CountNulls(ReadOnlySpan<byte>.Empty, offset: 0, length: 100));
    }

    // ----------------------------------------------------------------------------------------
    // AC2 — present bitmap is read Arrow LSB-first plus the logical offset.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void PresentBitmap_UsesArrowLsbFirstBitOrdering()
    {
        // Mark logical rows 0 and 9 valid; everything else null.
        var bitmap = new byte[2];
        Bitmap.Set(bitmap, 0, true);
        Bitmap.Set(bitmap, 9, true);

        // LSB-first packing: bit 0 -> byte 0 / 0x01; bit 9 -> byte 1 / 0x02 (matches Arrow + BitmapTests).
        Assert.Equal(0x01, bitmap[0]);
        Assert.Equal(0x02, bitmap[1]);

        var v = new Validity(bitmap, offset: 0, length: 16);
        Assert.True(v.IsValid(0));
        Assert.True(v.IsValid(9));
        Assert.True(v.IsNull(1));
        Assert.True(v.IsNull(8));
    }

    [Fact]
    public void LogicalOffset_ShiftsTheResolvedBit()
    {
        var bitmap = new byte[2];
        Bitmap.Set(bitmap, 9, true); // only logical bit 9 is valid

        // With offset 8, logical row i resolves physical bit (8 + i): row 1 -> bit 9 (valid).
        var shifted = new Validity(bitmap, offset: 8, length: 8);
        Assert.True(shifted.IsValid(1));  // bit 9
        Assert.True(shifted.IsNull(0));   // bit 8

        // The contract is exactly "Arrow LSB-first at (offset + index)".
        for (int i = 0; i < shifted.Length; i++)
        {
            Assert.Equal(Bitmap.Get(bitmap, 8 + i), shifted.IsValid(i));
        }
    }

    [Fact]
    public void Slice_AccumulatesLogicalOffset_OverSharedBuffer()
    {
        var bitmap = new byte[2];
        Bitmap.Set(bitmap, 9, true);
        Bitmap.Set(bitmap, 12, true);

        var v = new Validity(bitmap, offset: 0, length: 16);
        Validity tail = v.Slice(8, 8); // logical rows 8..15 -> physical bits 8..15

        Assert.Equal(8, tail.Length);
        Assert.Equal(8, tail.Offset);
        Assert.True(tail.IsValid(1));  // bit 9
        Assert.True(tail.IsValid(4));  // bit 12
        Assert.True(tail.IsNull(0));   // bit 8

        // Slicing a slice keeps accumulating the offset without copying bits.
        Validity inner = tail.Slice(1, 4); // physical bits 9..12
        Assert.Equal(9, inner.Offset);
        Assert.True(inner.IsValid(0));  // bit 9
        Assert.True(inner.IsValid(3));  // bit 12
    }

    // ----------------------------------------------------------------------------------------
    // AC4 — null counts correct + deterministic across all-null / no-null / empty / sliced.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void CountNulls_AllNull()
    {
        var bitmap = new byte[Bitmap.ByteCount(10)]; // all bits cleared => all null
        var v = new Validity(bitmap, offset: 0, length: 10);

        Assert.Equal(10, v.CountNulls());
        Assert.Equal(0, v.CountValid());
        Assert.True(v.HasBitmap);
    }

    [Fact]
    public void CountNulls_NoNull()
    {
        int n = 13;
        var bitmap = new byte[Bitmap.ByteCount(n)];
        for (int i = 0; i < n; i++)
        {
            Bitmap.Set(bitmap, i, true);
        }

        var v = new Validity(bitmap, offset: 0, length: n);
        Assert.Equal(0, v.CountNulls());
        Assert.Equal(n, v.CountValid());
    }

    [Fact]
    public void CountNulls_Empty()
    {
        Assert.Equal(0, Validity.AllValid(0).CountNulls());
        Assert.Equal(0, new Validity(ReadOnlySpan<byte>.Empty, 0, 0).CountNulls());
    }

    [Fact]
    public void CountNulls_Sliced_CountsOnlyTheWindow()
    {
        // rows 0,2,4 valid; 1,3,5,6,7,... null (mirrors BitmapTests fixture).
        var bitmap = new byte[2];
        Bitmap.Set(bitmap, 0, true);
        Bitmap.Set(bitmap, 2, true);
        Bitmap.Set(bitmap, 4, true);

        var v = new Validity(bitmap, offset: 0, length: 8);
        Assert.Equal(5, v.CountNulls()); // rows 1,3,5,6,7

        Validity window = v.Slice(2, 3); // rows 2,3,4 -> valid,null,valid
        Assert.Equal(1, window.CountNulls());
        Assert.Equal(2, window.CountValid());

        Validity headValid = v.Slice(0, 1); // row 0 only
        Assert.Equal(0, headValid.CountNulls());
    }

    [Fact]
    public void CountNulls_IsDeterministic_RepeatedCallsAgree()
    {
        var bitmap = new byte[2];
        Bitmap.Set(bitmap, 3, true);
        Bitmap.Set(bitmap, 11, true);
        var v = new Validity(bitmap, offset: 0, length: 16);

        int first = v.CountNulls();
        int second = v.CountNulls();
        int third = v.Slice(0, 16).CountNulls();

        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(14, first); // 16 rows, 2 valid
    }

    [Fact]
    public void CountNulls_RandomizedParity_MatchesRawBitOracle()
    {
        // AC4: across random buffers, offsets, and window lengths the utility matches an independent
        // raw-bit oracle (no shared code path), proving correctness for arbitrary slices.
        var rng = new Random(0xC0FFEE);
        for (int trial = 0; trial < 500; trial++)
        {
            int n = rng.Next(0, 257);
            var bitmap = new byte[Bitmap.ByteCount(n)];
            rng.NextBytes(bitmap);

            int offset = n == 0 ? 0 : rng.Next(0, n + 1);
            int length = n == 0 ? 0 : rng.Next(0, n - offset + 1);

            int expected = 0;
            for (int i = offset; i < offset + length; i++)
            {
                bool valid = (bitmap[i >> 3] & (1 << (i & 7))) != 0;
                if (!valid)
                {
                    expected++;
                }
            }

            var v = new Validity(bitmap, offset, length);
            Assert.Equal(expected, v.CountNulls());
            Assert.Equal(length - expected, v.CountValid());
        }
    }

    // ----------------------------------------------------------------------------------------
    // Guard rails.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Constructor_RejectsBufferTooSmallForWindow()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            var b = new byte[1]; // 8 bits
            _ = new Validity(b, offset: 0, length: 9); // needs 9 bits
        });
    }

    [Fact]
    public void Constructor_RejectsNegativeOffsetOrLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new Validity(ReadOnlySpan<byte>.Empty, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new Validity(ReadOnlySpan<byte>.Empty, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = Validity.AllValid(-1));
    }

    [Fact]
    public void IsValid_RejectsOutOfRangeIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Validity.AllValid(5).IsValid(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Validity.AllValid(5).IsNull(-1));
    }

    [Fact]
    public void Slice_RejectsRangeOutsideLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Validity.AllValid(4).Slice(2, 3));
    }

    [Fact]
    public void Slice_OffsetLengthOverflow_ThrowsNotWraps()
    {
        // int `offset + length` would wrap negative and silently accept the slice (Security F1).
        Assert.Throws<ArgumentOutOfRangeException>(() => Validity.AllValid(100).Slice(int.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Validity.AllValid(int.MaxValue).Slice(1, int.MaxValue));
    }
}
