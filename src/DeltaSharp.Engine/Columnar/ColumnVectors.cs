using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// Factory for the managed reference <see cref="MutableColumnVector"/> implementations, mapping a
/// logical <see cref="DataType"/> to the appropriate storage. Operators bind to the abstract
/// contracts; this factory is one (non-Arrow) way to obtain a concrete output vector.
/// </summary>
public static class ColumnVectors
{
    /// <summary>
    /// Creates an empty, mutable vector for <paramref name="type"/> with the given initial
    /// <paramref name="capacity"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    /// <exception cref="UnsupportedTypeException">
    /// The type has no managed vector representation (nested types and <see cref="NullType"/>).
    /// </exception>
    public static MutableColumnVector Create(DataType type, int capacity)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type switch
        {
            BooleanType => new ManagedFixedWidthColumnVector<bool>(type, capacity),
            ByteType => new ManagedFixedWidthColumnVector<byte>(type, capacity),
            ShortType => new ManagedFixedWidthColumnVector<short>(type, capacity),
            IntegerType or DateType => new ManagedFixedWidthColumnVector<int>(type, capacity),
            LongType or TimestampType => new ManagedFixedWidthColumnVector<long>(type, capacity),
            FloatType => new ManagedFixedWidthColumnVector<float>(type, capacity),
            DoubleType => new ManagedFixedWidthColumnVector<double>(type, capacity),
            DecimalType { IsCompact: true } => new ManagedFixedWidthColumnVector<long>(type, capacity),
            DecimalType => new ManagedFixedWidthColumnVector<Int128>(type, capacity),
            StringType or BinaryType => new ManagedVariableWidthColumnVector(type, capacity),
            _ => throw new UnsupportedTypeException(
                $"No managed column vector is defined for type '{type.SimpleString}'."),
        };
    }
}
