using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// The read contract a vectorized operator or kernel binds to for one column of values
/// (ADR-0002). It is deliberately independent of any storage backend: an Arrow-backed
/// implementation (STORY-02.2.1) and a future off-heap implementation (STORY-02.3.1) both
/// satisfy this contract, and <b>no operator-facing member names a <c>Apache.Arrow</c> type</b>
/// (STORY-02.1.1 AC4).
/// </summary>
/// <remarks>
/// <para>
/// All indices are <b>logical</b>: row <c>0</c> is the first row of this vector's view,
/// regardless of any physical <see cref="Offset"/> into shared buffers created by
/// <see cref="Slice"/>. Typed bulk access (<see cref="GetValues{T}"/>) is the hot-path
/// accessor — a kernel fetches the span once and iterates it without per-row boxing or virtual
/// dispatch.
/// </para>
/// <para>Instances are not thread-safe for concurrent writes; reads are safe once a producer
/// has finished writing (see <see cref="MutableColumnVector"/>).</para>
/// </remarks>
public abstract class ColumnVector
{
    /// <summary>Initializes the base with the vector's logical <paramref name="type"/>.</summary>
    protected ColumnVector(DataType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
    }

    /// <summary>The logical element type of this vector (the shared contract from ADR-0008).</summary>
    public DataType Type { get; }

    /// <summary>The number of logical rows in this vector's view.</summary>
    public abstract int Length { get; }

    /// <summary>
    /// The physical offset of logical row <c>0</c> within the underlying buffers. <c>0</c> for a
    /// freshly built vector; positive for a <see cref="Slice"/>. Logical access already accounts
    /// for it; it is exposed for consumers that index shared physical buffers directly.
    /// </summary>
    public abstract int Offset { get; }

    /// <summary>Whether any logical row in this view is null.</summary>
    public abstract bool HasNulls { get; }

    /// <summary>The number of null logical rows in this view.</summary>
    public abstract int NullCount { get; }

    /// <summary>Whether the logical row at <paramref name="index"/> is null.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
    public abstract bool IsNull(int index);

    /// <summary>
    /// The contiguous, offset-adjusted span of <c>Length</c> fixed-width values for this vector,
    /// typed as <typeparamref name="T"/> — the no-boxing hot-path accessor for the v1 primitives
    /// (bool, byte, short, int, long, float, double, date as <see cref="int"/>, timestamp and
    /// compact decimal as <see cref="long"/>). Null rows still occupy a slot; pair this with
    /// <see cref="IsNull"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The vector is not a fixed-width vector whose element type is <typeparamref name="T"/>.
    /// </exception>
    public abstract ReadOnlySpan<T> GetValues<T>()
        where T : unmanaged;

    /// <summary>The fixed-width value at logical <paramref name="index"/>, without boxing.</summary>
    /// <exception cref="InvalidOperationException">The element type is not <typeparamref name="T"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
    public virtual T GetValue<T>(int index)
        where T : unmanaged => GetValues<T>()[index];

    /// <summary>
    /// The raw bytes of the variable-width value at logical <paramref name="index"/> — UTF-8 for
    /// <see cref="StringType"/>, the value itself for <see cref="BinaryType"/>. Empty for a null
    /// row; use <see cref="IsNull"/> to distinguish null from empty.
    /// </summary>
    /// <exception cref="InvalidOperationException">The vector is not a variable-width vector.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
    public abstract ReadOnlySpan<byte> GetBytes(int index);

    /// <summary>
    /// A logical sub-range view over the same underlying buffers — no value or validity bytes are
    /// copied. The result reports <c>Length == length</c>, a consistent <see cref="Offset"/>, and
    /// validity that matches the parent at each corresponding logical row (STORY-02.1.1 AC2).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside <c>[0, Length]</c>.</exception>
    public abstract ColumnVector Slice(int offset, int length);
}
