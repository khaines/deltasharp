using Apache.Arrow;
using DeltaSharp.Engine.Types;
using ArrowTimestampType = Apache.Arrow.Types.TimestampType;

namespace DeltaSharp.Engine.Columnar.Arrow;

/// <summary>
/// An immutable <see cref="ColumnVector"/> backed by an Apache Arrow array — the "Arrow at the
/// edges" bridge from ADR-0002 (STORY-02.2.1). It exists so DeltaSharp can wrap Parquet/Flight
/// arrays zero-copy and reach a working columnar engine fast, while hot operators later swap in an
/// off-heap vector with no operator changes. Only the <see cref="Wrap"/> boundary factory names an
/// <c>Apache.Arrow</c> type; the inherited <see cref="ColumnVector"/> read surface that operators
/// and kernels bind to names none (STORY-02.1.1 AC4).
/// </summary>
/// <remarks>
/// Arrow arrays are immutable, so this vector is read-only: it is a <see cref="ColumnVector"/>, not
/// a <see cref="MutableColumnVector"/>. An operator that needs an output vector builds a
/// DeltaSharp-owned one through <see cref="ColumnVectors.Create"/> and writes into that — the Arrow
/// buffers are never mutated. Value and validity reads honor the Arrow <see cref="ColumnVector.Offset"/>
/// and the LSB-first validity bit order (the same order <see cref="Bitmap"/> uses), so slices and
/// non-zero offsets resolve correctly without copying.
/// </remarks>
public abstract class ArrowColumnVector : ColumnVector
{
    private protected ArrowColumnVector(DataType type)
        : base(type)
    {
    }

    /// <summary>
    /// Wraps an Apache Arrow array as an immutable <see cref="ColumnVector"/> with no value or
    /// validity copy. This is the boundary edge: callers cross from <c>Apache.Arrow</c> into the
    /// engine's columnar contract here, and everything downstream binds to <see cref="ColumnVector"/>.
    /// </summary>
    /// <param name="array">The Arrow array to wrap (a non-zero offset, e.g. a slice, is honored).</param>
    /// <returns>A read-only <see cref="ColumnVector"/> over the array's buffers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> is null.</exception>
    /// <exception cref="UnsupportedTypeException">
    /// The Arrow type has no v1 columnar mapping (bit-packed boolean, decimal, unsigned/half-float
    /// primitives, a non-microsecond timestamp, null, or nested array/map/struct). The message names
    /// the exact gap rather than silently coercing or dropping data.
    /// </exception>
    public static ColumnVector Wrap(IArrowArray array)
    {
        ArgumentNullException.ThrowIfNull(array);

        // Most specific first: Date32Array/TimestampArray erase to PrimitiveArray<int>/<long>, and
        // StringArray derives from BinaryArray, so they must be matched before their generic kin.
        return array switch
        {
            Int8Array a => new ArrowFixedWidthColumnVector<sbyte>(ByteType.Instance, typeof(byte), a),
            Int16Array a => new ArrowFixedWidthColumnVector<short>(ShortType.Instance, typeof(short), a),
            Date32Array a => new ArrowFixedWidthColumnVector<int>(DateType.Instance, typeof(int), a),
            Int32Array a => new ArrowFixedWidthColumnVector<int>(IntegerType.Instance, typeof(int), a),
            TimestampArray a => WrapTimestamp(a),
            Int64Array a => new ArrowFixedWidthColumnVector<long>(LongType.Instance, typeof(long), a),
            FloatArray a => new ArrowFixedWidthColumnVector<float>(FloatType.Instance, typeof(float), a),
            DoubleArray a => new ArrowFixedWidthColumnVector<double>(DoubleType.Instance, typeof(double), a),
            StringArray a => new ArrowVariableWidthColumnVector(StringType.Instance, a),
            BinaryArray a => new ArrowVariableWidthColumnVector(BinaryType.Instance, a),
            _ => throw Unsupported(array),
        };
    }

    private static ColumnVector WrapTimestamp(TimestampArray array)
    {
        // DeltaSharp v1 stores timestamps as microseconds since the epoch (TimestampType). Any other
        // Arrow unit (s/ms/ns) would silently rescale, so it is an explicit unsupported gap.
        var arrowType = (ArrowTimestampType)array.Data.DataType;
        if (arrowType.Unit != Apache.Arrow.Types.TimeUnit.Microsecond)
        {
            throw new UnsupportedTypeException(
                $"Arrow timestamp unit '{arrowType.Unit}' has no v1 columnar mapping; "
                + "only microsecond timestamps are supported.");
        }

        return new ArrowFixedWidthColumnVector<long>(TimestampType.Instance, typeof(long), array);
    }

    private static UnsupportedTypeException Unsupported(IArrowArray array)
    {
        string typeId = array.Data.DataType.TypeId.ToString();
        return new UnsupportedTypeException(
            $"Arrow array of type '{typeId}' has no v1 DeltaSharp columnar mapping (bit-packed boolean, "
            + "decimal, unsigned/half-float, non-microsecond timestamp, null, and nested array/map/struct "
            + "are not yet supported); convert it to a supported primitive before wrapping.");
    }
}
