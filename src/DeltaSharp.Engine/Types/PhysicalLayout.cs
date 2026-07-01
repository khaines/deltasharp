namespace DeltaSharp.Engine.Types;

/// <summary>
/// How a <see cref="DataType"/> is physically materialized by the columnar
/// (STORY-02.1.1) and binary-row (STORY-02.4.1) layers (ADR-0002, ADR-0008).
/// </summary>
public enum PhysicalLayoutKind
{
    /// <summary>
    /// No physical representation — the value of a <see cref="PhysicalLayout"/> that was never
    /// resolved (the <c>out</c> value when <see cref="PhysicalLayoutResolver.TryResolve"/> returns
    /// <see langword="false"/>, e.g. for <see cref="NullType"/>). It is the default so that an
    /// unresolved layout is never mistaken for a real fixed-width one.
    /// </summary>
    None = 0,

    /// <summary>A fixed number of bytes per value — see <see cref="PhysicalLayout.FixedWidthBytes"/>.</summary>
    FixedWidth,

    /// <summary>
    /// Variable-length bytes per value: an offsets buffer plus a shared byte buffer
    /// (for example <see cref="StringType"/> and <see cref="BinaryType"/>).
    /// </summary>
    Variable,

    /// <summary>
    /// A composite of child values (<see cref="ArrayType"/>, <see cref="MapType"/>,
    /// <see cref="StructType"/>); a consumer recurses on the type's children.
    /// </summary>
    Nested,
}

/// <summary>
/// The physical representation a <see cref="DataType"/> advertises to buffer/builder code.
/// This is the seam the columnar and binary-row layers consume (STORY-02.5.1 AC4): a type
/// either resolves to a supported layout, or <see cref="PhysicalLayoutResolver.TryResolve"/>
/// reports that it has none (for example <see cref="NullType"/>).
/// </summary>
public readonly struct PhysicalLayout : IEquatable<PhysicalLayout>
{
    private PhysicalLayout(PhysicalLayoutKind kind, int fixedWidthBytes)
    {
        Kind = kind;
        FixedWidthBytes = fixedWidthBytes;
    }

    /// <summary>The physical layout family.</summary>
    public PhysicalLayoutKind Kind { get; }

    /// <summary>
    /// The per-value byte width when <see cref="Kind"/> is
    /// <see cref="PhysicalLayoutKind.FixedWidth"/>; otherwise <c>0</c>.
    /// </summary>
    public int FixedWidthBytes { get; }

    /// <summary>Whether this layout is fixed width.</summary>
    public bool IsFixedWidth => Kind == PhysicalLayoutKind.FixedWidth;

    /// <summary>A fixed-width layout of <paramref name="byteWidth"/> bytes per value.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteWidth"/> is not positive.</exception>
    public static PhysicalLayout FixedWidth(int byteWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteWidth);
        return new PhysicalLayout(PhysicalLayoutKind.FixedWidth, byteWidth);
    }

    /// <summary>The variable-length layout (offsets + shared byte buffer).</summary>
    public static PhysicalLayout Variable { get; } = new(PhysicalLayoutKind.Variable, 0);

    /// <summary>The nested/composite layout (child values).</summary>
    public static PhysicalLayout Nested { get; } = new(PhysicalLayoutKind.Nested, 0);

    /// <inheritdoc/>
    public bool Equals(PhysicalLayout other) =>
        Kind == other.Kind && FixedWidthBytes == other.FixedWidthBytes;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PhysicalLayout other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StableHash.Combine((int)Kind, FixedWidthBytes);

    /// <inheritdoc/>
    public override string ToString() =>
        IsFixedWidth ? $"FixedWidth({FixedWidthBytes})" : Kind.ToString();

    /// <summary>Value equality.</summary>
    public static bool operator ==(PhysicalLayout left, PhysicalLayout right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(PhysicalLayout left, PhysicalLayout right) => !left.Equals(right);
}
