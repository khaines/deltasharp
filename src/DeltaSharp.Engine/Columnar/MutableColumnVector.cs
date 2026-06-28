namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// The write contract for an output column an operator materializes (ADR-0002). Values and null
/// bits are written through this surface and become observable through the inherited
/// <see cref="ColumnVector"/> read members — with no immutable Arrow API required on the hot path
/// (STORY-02.1.1 AC3). Implementations grow <see cref="ColumnVector.Length"/> as rows are
/// appended.
/// </summary>
public abstract class MutableColumnVector : ColumnVector
{
    /// <inheritdoc/>
    protected MutableColumnVector(Types.DataType type)
        : base(type)
    {
    }

    /// <summary>Appends a fixed-width value, growing the vector by one row.</summary>
    /// <exception cref="InvalidOperationException">The element type is not <typeparamref name="T"/>.</exception>
    public abstract void AppendValue<T>(T value)
        where T : unmanaged;

    /// <summary>Appends a variable-width value (UTF-8 for string, raw bytes for binary).</summary>
    /// <exception cref="InvalidOperationException">The vector is not a variable-width vector.</exception>
    public abstract void AppendBytes(ReadOnlySpan<byte> value);

    /// <summary>Appends a null row.</summary>
    public abstract void AppendNull();

    /// <summary>Overwrites the fixed-width value at logical <paramref name="index"/> and clears its null bit.</summary>
    /// <exception cref="InvalidOperationException">The element type is not <typeparamref name="T"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
    public abstract void SetValue<T>(int index, T value)
        where T : unmanaged;

    /// <summary>Marks the logical row at <paramref name="index"/> null.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
    public abstract void SetNull(int index);

    /// <summary>Resets the vector to zero rows, retaining capacity for reuse.</summary>
    public abstract void Clear();
}
