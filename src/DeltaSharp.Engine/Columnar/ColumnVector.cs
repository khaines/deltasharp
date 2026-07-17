using DeltaSharp.Types;

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
    /// Exposes this view's null state as a zero-copy <see cref="Validity"/> so a kernel can combine
    /// validity in bulk instead of per-row <see cref="IsNull"/> dispatch (STORY-02.6.1). Returns
    /// <see langword="true"/> when validity can be described as a packed Arrow LSB-first bitmap
    /// (or as the absent, all-valid bitmap).
    /// </summary>
    /// <remarks>
    /// The base contract only guarantees the <b>no-null fast path</b>: a vector with no nulls yields
    /// <see cref="Validity.AllValid(int)"/> — an empty bitmap, treated as all-valid <b>without
    /// allocating a synthetic all-ones buffer</b> (AC1). A vector that has nulls but does not surface
    /// a packed buffer through this base returns <see langword="false"/>, and the caller falls back to
    /// per-row <see cref="IsNull"/>; a concrete vector that owns a packed validity buffer may override
    /// to return it (enabling the bulk validity path for null-bearing inputs too).
    /// </remarks>
    /// <param name="validity">The validity view, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if <paramref name="validity"/> is populated; otherwise <see langword="false"/>.</returns>
    public virtual bool TryGetValidity(out Validity validity)
    {
        if (!HasNulls)
        {
            validity = Validity.AllValid(Length);
            return true;
        }

        validity = default;
        return false;
    }

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
        where T : unmanaged
    {
        ReadOnlySpan<T> values = GetValues<T>();
        if ((uint)index >= (uint)values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {values.Length}).");
        }

        return values[index];
    }

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

    /// <summary>
    /// A zero-copy selected view whose logical rows are <paramref name="selection"/>'s physical
    /// rows, in selection order: <c>Length</c> equals the selected cardinality and no value or
    /// validity buffers are copied. Selecting over an existing selected view composes the two
    /// selections (STORY-02.1.2 AC1, AC2). Like <see cref="Slice"/>, creating a view seals a
    /// mutable owner so the shared buffers can't be mutated underneath the view. The view is
    /// enumerated per-row through <see cref="GetValue{T}"/>/<see cref="GetBytes"/>/<see cref="IsNull"/>;
    /// the contiguous <see cref="GetValues{T}"/> span is unavailable on a selection (kernels gather).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="selection"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A selected index is outside <c>[0, Length)</c>.</exception>
    public virtual ColumnVector Select(SelectionVector selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ReadOnlySpan<int> indices = selection.Indices;
        for (int i = 0; i < indices.Length; i++)
        {
            if ((uint)indices[i] >= (uint)Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(selection), indices[i], $"Selected index must be in [0, {Length}).");
            }
        }

        SealForView();
        return new SelectedColumnVector(this, selection);
    }

    /// <summary>
    /// Seals an owner when a zero-copy view (slice or selection) is taken over its buffers.
    /// The base does nothing; a mutable reference vector overrides it to reject later mutation.
    /// </summary>
    protected virtual void SealForView()
    {
    }

    /// <summary>Assembly-internal seal entry point so a nested vector can propagate the seal to its shared
    /// child vectors when it is sliced/viewed (#575); on a flat vector this just marks it sealed.</summary>
    internal void Seal() => SealForView();
}
