using DeltaSharp.Engine.Columnar;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Exercises the <c>internal</c> <see cref="Bitmap"/> helper directly through the friend-assembly
/// test-access policy (Directory.Build.props grants InternalsVisibleTo to this test assembly).
/// </summary>
public class BitmapTests
{
    [Fact]
    public void ByteCount_RoundsUpToWholeBytes()
    {
        Assert.Equal(0, Bitmap.ByteCount(0));
        Assert.Equal(1, Bitmap.ByteCount(1));
        Assert.Equal(1, Bitmap.ByteCount(8));
        Assert.Equal(2, Bitmap.ByteCount(9));
    }

    [Fact]
    public void SetAndGet_RoundTripBits_WithLsbFirstOrdering()
    {
        var bitmap = new byte[2];
        Bitmap.Set(bitmap, 0, true);
        Bitmap.Set(bitmap, 9, true);

        Assert.True(Bitmap.Get(bitmap, 0));
        Assert.True(Bitmap.Get(bitmap, 9));
        Assert.False(Bitmap.Get(bitmap, 1));

        // LSB-first: bit 0 -> byte 0 / 0x01; bit 9 -> byte 1 / 0x02.
        Assert.Equal(0x01, bitmap[0]);
        Assert.Equal(0x02, bitmap[1]);

        Bitmap.Set(bitmap, 0, false);
        Assert.False(Bitmap.Get(bitmap, 0));
    }

    [Fact]
    public void CountNulls_CountsClearedBitsInWindowOnly()
    {
        var bitmap = new byte[2];
        // Mark rows 0,2,4 valid; the rest (1,3,5,6,7,...) are null.
        Bitmap.Set(bitmap, 0, true);
        Bitmap.Set(bitmap, 2, true);
        Bitmap.Set(bitmap, 4, true);

        Assert.Equal(2, Bitmap.CountNulls(bitmap, offset: 0, length: 4)); // rows 1,3 null
        Assert.Equal(1, Bitmap.CountNulls(bitmap, offset: 2, length: 2)); // row 3 null, row 2 valid
        Assert.Equal(0, Bitmap.CountNulls(bitmap, offset: 0, length: 1)); // row 0 valid
    }
}
