using System.Buffers;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Memory;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;
using ArrowListType = Apache.Arrow.Types.ListType;
using ArrowStructType = Apache.Arrow.Types.StructType;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// Shared fixtures for the Arrow boundary round-trip tests (STORY-02.2.2, #136): logical
/// column equality, Arrow nested-array and <see cref="RecordBatch"/> builders, and the
/// instrumented Arrow doubles that make ownership/disposal observable (AC4).
/// </summary>
internal static class ArrowConverterTestSupport
{
    /// <summary>
    /// Asserts two columns are logically equal: same type, length, null count, and per-row nullness
    /// and value (compared through the storage-agnostic contract, so a managed and an Arrow-backed
    /// vector compare equal when they carry the same logical data).
    /// </summary>
    internal static void AssertColumnsEqual(ColumnVector expected, ColumnVector actual)
    {
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected.NullCount, actual.NullCount);

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected.IsNull(i), actual.IsNull(i));
            if (expected.IsNull(i))
            {
                continue;
            }

            switch (expected.Type)
            {
                case BooleanType:
                    Assert.Equal(expected.GetValue<bool>(i), actual.GetValue<bool>(i));
                    break;
                case ByteType:
                    Assert.Equal(expected.GetValue<byte>(i), actual.GetValue<byte>(i));
                    break;
                case ShortType:
                    Assert.Equal(expected.GetValue<short>(i), actual.GetValue<short>(i));
                    break;
                case IntegerType:
                case DateType:
                    Assert.Equal(expected.GetValue<int>(i), actual.GetValue<int>(i));
                    break;
                case LongType:
                case TimestampType:
                    Assert.Equal(expected.GetValue<long>(i), actual.GetValue<long>(i));
                    break;
                case FloatType:
                    Assert.Equal(expected.GetValue<float>(i), actual.GetValue<float>(i));
                    break;
                case DoubleType:
                    Assert.Equal(expected.GetValue<double>(i), actual.GetValue<double>(i));
                    break;
                case DecimalType { IsCompact: true }:
                    Assert.Equal(expected.GetValue<long>(i), actual.GetValue<long>(i));
                    break;
                case DecimalType:
                    Assert.Equal(expected.GetValue<Int128>(i), actual.GetValue<Int128>(i));
                    break;
                case StringType:
                case BinaryType:
                    Assert.True(expected.GetBytes(i).SequenceEqual(actual.GetBytes(i)));
                    break;
                default:
                    Assert.Fail($"No equality check defined for column type '{expected.Type.SimpleString}'.");
                    break;
            }
        }
    }

    /// <summary>Builds a single-batch <see cref="RecordBatch"/> from named, typed columns.</summary>
    internal static RecordBatch RecordBatchOf(params (string Name, IArrowArray Array, bool Nullable)[] columns)
    {
        var fields = new Field[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            fields[i] = new Field(columns[i].Name, columns[i].Array.Data.DataType, columns[i].Nullable);
        }

        int length = columns.Length == 0 ? 0 : columns[0].Array.Length;
        return new RecordBatch(
            new Schema(fields, metadata: null), System.Array.ConvertAll(columns, c => c.Array), length);
    }

    /// <summary>
    /// Builds an Arrow <c>struct&lt;v: int32&gt;</c> array with the given child values and optional
    /// struct-level validity (LSB-first, set = valid).
    /// </summary>
    internal static StructArray BuildStructArray(int[] childValues, bool[]? structValid = null)
    {
        var childBuilder = new Int32Array.Builder();
        foreach (int value in childValues)
        {
            childBuilder.Append(value);
        }

        Int32Array child = childBuilder.Build();
        var childField = new Field("v", Apache.Arrow.Types.Int32Type.Default, nullable: true);
        var structType = new ArrowStructType(new[] { childField });
        ArrowBuffer validity = PackValidity(structValid, childValues.Length, out int nullCount);
        return new StructArray(structType, childValues.Length, new IArrowArray[] { child }, validity, nullCount, offset: 0);
    }

    /// <summary>Builds an Arrow <c>list&lt;item: int32&gt;</c> array from one inner int array per row.</summary>
    internal static ListArray BuildListArray(int[][] lists)
    {
        var valuesBuilder = new Int32Array.Builder();
        var offsets = new int[lists.Length + 1];
        int running = 0;
        for (int i = 0; i < lists.Length; i++)
        {
            offsets[i] = running;
            foreach (int value in lists[i])
            {
                valuesBuilder.Append(value);
            }

            running += lists[i].Length;
        }

        offsets[lists.Length] = running;
        Int32Array values = valuesBuilder.Build();
        var valueField = new Field("item", Apache.Arrow.Types.Int32Type.Default, nullable: true);
        var listType = new ArrowListType(valueField);
        var offsetsBuffer = new ArrowBuffer(MemoryMarshal.AsBytes<int>(offsets).ToArray());
        return new ListArray(listType, lists.Length, offsetsBuffer, values, ArrowBuffer.Empty, nullCount: 0, offset: 0);
    }

    private static ArrowBuffer PackValidity(bool[]? valid, int length, out int nullCount)
    {
        nullCount = 0;
        if (valid is null)
        {
            return ArrowBuffer.Empty;
        }

        var bytes = new byte[(length + 7) / 8];
        for (int i = 0; i < length; i++)
        {
            if (valid[i])
            {
                bytes[i >> 3] |= (byte)(1 << (i & 7));
            }
            else
            {
                nullCount++;
            }
        }

        return nullCount == 0 ? ArrowBuffer.Empty : new ArrowBuffer(bytes);
    }
}

/// <summary>
/// A <see cref="RecordBatch"/> that counts every call to its disposal funnel
/// (<see cref="Dispose(bool)"/>, which is what releases the buffers) so a test can prove the
/// converter disposes the source <b>exactly once</b> per ownership mode (AC4). It intentionally
/// carries no idempotency guard, so a double release would be observed as a count of two.
/// </summary>
internal sealed class DisposeCountingRecordBatch : RecordBatch
{
    public DisposeCountingRecordBatch(Schema schema, IReadOnlyList<IArrowArray> data, int length)
        : base(schema, data, length)
    {
    }

    /// <summary>How many times the buffer-release funnel has been entered.</summary>
    public int DisposeCalls { get; private set; }

    protected override void Dispose(bool disposing)
    {
        DisposeCalls++;
        base.Dispose(disposing);
    }
}

/// <summary>
/// A <see cref="MemoryAllocator"/> that tracks outstanding buffers so a test can prove imported
/// buffers are freed exactly once (no leak, no double free) per ownership mode (AC4).
/// </summary>
internal sealed class CountingMemoryAllocator : MemoryAllocator
{
    private int _outstanding;

    public CountingMemoryAllocator()
        : base(alignment: 64)
    {
    }

    /// <summary>Total buffers allocated through this allocator.</summary>
    public int AllocationCount { get; private set; }

    /// <summary>Total buffers freed (each owner counts at most once).</summary>
    public int FreeCount { get; private set; }

    /// <summary>Buffers allocated but not yet freed.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    protected override IMemoryOwner<byte> AllocateInternal(int length, out int bytesAllocated)
    {
        AllocationCount++;
        Interlocked.Increment(ref _outstanding);
        bytesAllocated = length;
        return new CountingOwner(this, length);
    }

    private void OnFree()
    {
        FreeCount++;
        Interlocked.Decrement(ref _outstanding);
    }

    private sealed class CountingOwner : IMemoryOwner<byte>
    {
        private readonly CountingMemoryAllocator _allocator;
        private readonly byte[] _buffer;
        private bool _freed;

        public CountingOwner(CountingMemoryAllocator allocator, int length)
        {
            _allocator = allocator;
            _buffer = new byte[length];
        }

        public Memory<byte> Memory => _buffer;

        public void Dispose()
        {
            if (_freed)
            {
                return;
            }

            _freed = true;
            _allocator.OnFree();
        }
    }
}
