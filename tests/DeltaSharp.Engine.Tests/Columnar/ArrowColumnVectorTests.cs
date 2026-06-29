using Apache.Arrow;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Columnar.Arrow;
using DeltaSharp.Engine.Types;
using Xunit;
using ArrowTimeUnit = Apache.Arrow.Types.TimeUnit;
using Decimal128Type = Apache.Arrow.Types.Decimal128Type;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Tests for the Arrow-backed <see cref="ColumnVector"/> (STORY-02.2.1, #135): non-zero Arrow
/// offsets, Arrow LSB-first validity ordering, immutability (mutable output goes to DeltaSharp
/// builders, never the Arrow array), and precise unsupported-type errors for v1 gaps.
/// </summary>
public class ArrowColumnVectorTests
{
    [Fact]
    public void Wrap_Int32_ExposesContractWithoutNamingArrow()
    {
        Int32Array arrow = new Int32Array.Builder().Append(10).AppendNull().Append(30).Build();
        ColumnVector v = ArrowColumnVector.Wrap(arrow);

        Assert.Equal(IntegerType.Instance, v.Type);
        Assert.Equal(3, v.Length);
        Assert.Equal(0, v.Offset);
        Assert.True(v.HasNulls);
        Assert.Equal(1, v.NullCount);
        Assert.False(v.IsNull(0));
        Assert.True(v.IsNull(1));
        Assert.Equal(10, v.GetValue<int>(0));
        Assert.Equal(30, v.GetValues<int>()[2]);
    }

    // AC1: a non-zero Arrow offset (a slice) must adjust both value and validity access.
    [Fact]
    public void Wrap_SlicedArray_ValueAndValidityHonorTheArrowOffset()
    {
        Int32Array full = new Int32Array.Builder().Append(10).Append(20).AppendNull().Append(40).Append(50).Build();
        Int32Array sliced = (Int32Array)full.Slice(2, 3); // [null, 40, 50], Offset == 2

        ColumnVector v = ArrowColumnVector.Wrap(sliced);

        Assert.Equal(3, v.Length);
        Assert.Equal(2, v.Offset);
        Assert.True(v.IsNull(0));
        Assert.Equal(40, v.GetValue<int>(1));
        Assert.Equal(50, v.GetValue<int>(2));
        ReadOnlySpan<int> values = v.GetValues<int>();
        Assert.Equal(3, values.Length);
        Assert.Equal(50, values[2]);
        Assert.Equal(1, v.NullCount);
    }

    // AC2: null positions must match Arrow bit ordering for every tested offset (cross byte boundary).
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Wrap_Validity_MatchesArrowBitOrdering_AtEveryOffset(int offset)
    {
        var builder = new Int32Array.Builder();
        for (int i = 0; i < 20; i++)
        {
            if (i % 3 == 0)
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(i);
            }
        }

        Int32Array full = builder.Build();
        const int length = 8;
        Int32Array sliced = (Int32Array)full.Slice(offset, length);
        ColumnVector v = ArrowColumnVector.Wrap(sliced);

        int expectedNulls = 0;
        for (int i = 0; i < length; i++)
        {
            // The wrapper's logical row i must agree with Arrow's own offset-aware null bit.
            Assert.Equal(sliced.IsNull(i), v.IsNull(i));
            if (sliced.IsNull(i))
            {
                expectedNulls++;
            }
        }

        Assert.Equal(expectedNulls, v.NullCount);
    }

    [Fact]
    public void Wrap_Int8_PresentsByteWithoutCopy()
    {
        Int8Array arrow = new Int8Array.Builder().Append((sbyte)-5).Append((sbyte)5).Build();
        ColumnVector v = ArrowColumnVector.Wrap(arrow);

        Assert.Equal(ByteType.Instance, v.Type);
        Assert.Equal((byte)251, v.GetValue<byte>(0)); // -5 reinterpreted as unsigned byte
        Assert.Equal((byte)5, v.GetValues<byte>()[1]);
    }

    [Fact]
    public void Wrap_LongDoubleDate_MapToContractTypes()
    {
        Assert.Equal(LongType.Instance, ArrowColumnVector.Wrap(new Int64Array.Builder().Append(7L).Build()).Type);
        Assert.Equal(DoubleType.Instance, ArrowColumnVector.Wrap(new DoubleArray.Builder().Append(1.5).Build()).Type);
        Assert.Equal(FloatType.Instance, ArrowColumnVector.Wrap(new FloatArray.Builder().Append(1.5f).Build()).Type);
        Assert.Equal(ShortType.Instance, ArrowColumnVector.Wrap(new Int16Array.Builder().Append((short)3).Build()).Type);
        Assert.Equal(DateType.Instance, ArrowColumnVector.Wrap(new Date32Array.Builder().Append(new DateTime(2024, 1, 1)).Build()).Type);
    }

    [Fact]
    public void Wrap_MicrosecondTimestamp_MapsToTimestampType()
    {
        var arrow = new TimestampArray.Builder(ArrowTimeUnit.Microsecond).Append(DateTimeOffset.UnixEpoch).Build();
        Assert.Equal(TimestampType.Instance, ArrowColumnVector.Wrap(arrow).Type);
    }

    [Fact]
    public void Wrap_String_StoresUtf8AndHonorsNullAndOffset()
    {
        StringArray full = new StringArray.Builder().Append("a").AppendNull().Append("ccc").Build();
        StringArray sliced = (StringArray)full.Slice(1, 2); // [null, "ccc"]
        ColumnVector v = ArrowColumnVector.Wrap(sliced);

        Assert.Equal(StringType.Instance, v.Type);
        Assert.True(v.IsNull(0));
        Assert.True(v.GetBytes(0).IsEmpty);
        Assert.Equal("ccc"u8.ToArray(), v.GetBytes(1).ToArray());
        Assert.Throws<InvalidOperationException>(() => v.GetValues<int>().Length);
    }

    [Fact]
    public void Wrap_Binary_StoresRawBytes()
    {
        byte[] payload = { 0x00, 0xFF, 0x10 };
        BinaryArray arrow = new BinaryArray.Builder().Append(payload.AsSpan()).Build();
        ColumnVector v = ArrowColumnVector.Wrap(arrow);

        Assert.Equal(BinaryType.Instance, v.Type);
        Assert.True(v.GetBytes(0).SequenceEqual(payload));
    }

    [Fact]
    public void Slice_OnWrappedVector_StaysZeroCopyAndOffsetAware()
    {
        Int32Array arrow = new Int32Array.Builder().Append(10).Append(20).Append(30).Append(40).Build();
        ColumnVector sliced = ArrowColumnVector.Wrap(arrow).Slice(1, 2);

        Assert.Equal(2, sliced.Length);
        Assert.Equal(20, sliced.GetValue<int>(0));
        Assert.Equal(30, sliced.GetValue<int>(1));
    }

    // AC3: an Arrow-backed vector is immutable input; a mutable output goes to DeltaSharp builders.
    [Fact]
    public void Wrapped_IsImmutable_MutableOutputIsDeltaSharpOwned()
    {
        Int32Array arrow = new Int32Array.Builder().Append(10).Append(20).Build();
        ColumnVector input = ArrowColumnVector.Wrap(arrow);

        Assert.IsNotAssignableFrom<MutableColumnVector>(input);

        // The only way to mutate is into a DeltaSharp-owned vector; Arrow buffers stay untouched.
        MutableColumnVector output = ColumnVectors.Create(IntegerType.Instance, input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            output.AppendValue(input.GetValue<int>(i) * 2);
        }

        output.SetValue(0, 999);
        Assert.Equal(999, output.GetValue<int>(0));
        Assert.Equal(10, input.GetValue<int>(0)); // unchanged
        Assert.Equal(10, arrow.Values[0]);
    }

    [Fact]
    public void GetValues_WrongElementType_Throws()
    {
        ColumnVector v = ArrowColumnVector.Wrap(new Int32Array.Builder().Append(1).Build());
        Assert.Throws<InvalidOperationException>(() => v.GetValues<long>().Length);
        Assert.Throws<InvalidOperationException>(() => v.GetBytes(0));
    }

    // AC4: unsupported Arrow features for v1 produce a precise error, not silent data loss.
    [Fact]
    public void Wrap_UnsupportedArrowTypes_ThrowUnsupportedType()
    {
        Assert.Throws<UnsupportedTypeException>(() =>
            ArrowColumnVector.Wrap(new BooleanArray.Builder().Append(true).Build()));
        Assert.Throws<UnsupportedTypeException>(() =>
            ArrowColumnVector.Wrap(new UInt32Array.Builder().Append(1u).Build()));
        Assert.Throws<UnsupportedTypeException>(() =>
            ArrowColumnVector.Wrap(new Decimal128Array.Builder(new Decimal128Type(10, 2)).Append(1.23m).Build()));
        Assert.Throws<UnsupportedTypeException>(() => ArrowColumnVector.Wrap(new NullArray(3)));
    }

    [Fact]
    public void Wrap_NonMicrosecondTimestamp_ThrowsWithUnitInMessage()
    {
        var arrow = new TimestampArray.Builder(ArrowTimeUnit.Nanosecond).Append(DateTimeOffset.UnixEpoch).Build();
        UnsupportedTypeException ex = Assert.Throws<UnsupportedTypeException>(() => ArrowColumnVector.Wrap(arrow));
        Assert.Contains("Nanosecond", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrap_Null_Throws() => Assert.Throws<ArgumentNullException>(() => ArrowColumnVector.Wrap(null!));
}
