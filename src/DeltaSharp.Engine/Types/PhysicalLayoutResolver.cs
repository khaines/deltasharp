namespace DeltaSharp.Engine.Types;

/// <summary>
/// Resolves the <see cref="PhysicalLayout"/> a vector or binary-row builder should use for a
/// given logical <see cref="DataType"/> (STORY-02.5.1 AC4).
/// </summary>
/// <remarks>
/// <para>
/// This is the physical/execution seam that used to live on <see cref="DataType"/> itself. It
/// was lifted out so the logical type descriptors carry no physical-layout knowledge and can be
/// extracted into the shared type-model assembly untouched (ADR-0016). The width each type maps
/// to is byte-for-byte identical to the descriptors' former overrides.
/// </para>
/// <para>
/// The switch is exhaustive over the closed <see cref="DataType"/> hierarchy.
/// <see cref="NullType"/> is the sole type that legitimately has no physical representation and
/// resolves to no layout. Any other unrecognized type — for example a future
/// <see cref="DataType"/> leaf added without a resolver case — fails loudly with an
/// <see cref="UnsupportedTypeException"/> rather than silently reporting no layout, so the
/// resolver must be extended whenever a new <see cref="DataType"/> is introduced.
/// </para>
/// </remarks>
internal static class PhysicalLayoutResolver
{
    /// <summary>
    /// Resolves the physical layout for <paramref name="type"/>, throwing when the type has no
    /// supported physical representation. Prefer <see cref="TryResolve"/> to avoid exceptions on
    /// hot paths.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    /// <exception cref="UnsupportedTypeException">The type has no physical representation.</exception>
    public static PhysicalLayout Resolve(DataType type)
    {
        if (TryResolve(type, out PhysicalLayout layout))
        {
            return layout;
        }

        throw new UnsupportedTypeException(
            $"Type '{type.SimpleString}' has no supported physical layout.");
    }

    /// <summary>
    /// Resolves the physical layout a vector or binary-row builder should use for
    /// <paramref name="type"/>.
    /// </summary>
    /// <param name="type">The logical type to resolve.</param>
    /// <param name="layout">The resolved layout when the type is physically representable.</param>
    /// <returns>
    /// <see langword="true"/> and a supported <paramref name="layout"/> for representable types;
    /// <see langword="false"/> (with <paramref name="layout"/> defaulted to
    /// <see cref="PhysicalLayoutKind.None"/>) for <see cref="NullType"/>, the only type with no
    /// physical representation.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    /// <exception cref="UnsupportedTypeException">
    /// <paramref name="type"/> is an unrecognized <see cref="DataType"/> (for example a new leaf
    /// added without a resolver case); the resolver must be extended to handle it.
    /// </exception>
    public static bool TryResolve(DataType type, out PhysicalLayout layout)
    {
        ArgumentNullException.ThrowIfNull(type);
        switch (type)
        {
            case BooleanType:
            case ByteType:
                layout = PhysicalLayout.FixedWidth(1);
                return true;
            case ShortType:
                layout = PhysicalLayout.FixedWidth(2);
                return true;
            case IntegerType:
            case FloatType:
            case DateType:
                layout = PhysicalLayout.FixedWidth(4);
                return true;
            case LongType:
            case DoubleType:
            case TimestampType:
                layout = PhysicalLayout.FixedWidth(8);
                return true;
            case StringType:
            case BinaryType:
                layout = PhysicalLayout.Variable;
                return true;
            case DecimalType d:
                layout = PhysicalLayout.FixedWidth(d.IsCompact ? 8 : 16);
                return true;
            case ArrayType:
            case MapType:
            case StructType:
                layout = PhysicalLayout.Nested;
                return true;
            case NullType:
                layout = default;
                return false;
            default:
                throw new UnsupportedTypeException(
                    $"No physical layout mapping is defined for type '{type.SimpleString}'. " +
                    "PhysicalLayoutResolver must be extended when a new DataType is added.");
        }
    }
}
