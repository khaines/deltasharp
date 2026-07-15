using DeltaSharp.Types;

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
            LongType or TimestampType or TimestampNtzType => new ManagedFixedWidthColumnVector<long>(type, capacity),
            FloatType => new ManagedFixedWidthColumnVector<float>(type, capacity),
            DoubleType => new ManagedFixedWidthColumnVector<double>(type, capacity),
            DecimalType { IsCompact: true } => new ManagedFixedWidthColumnVector<long>(type, capacity),
            DecimalType => new ManagedFixedWidthColumnVector<Int128>(type, capacity),
            StringType or BinaryType => new ManagedVariableWidthColumnVector(type, capacity),
            _ => throw new UnsupportedTypeException(
                $"No managed column vector is defined for type '{type.SimpleString}'."),
        };
    }

    /// <summary>
    /// Creates one empty, mutable vector per field of <paramref name="schema"/>, each with the
    /// given <paramref name="capacity"/> — a convenience for building a <see cref="ColumnBatch"/>
    /// without restating each column's type.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <exception cref="UnsupportedTypeException">A field has no managed vector representation.</exception>
    public static MutableColumnVector[] CreateForSchema(StructType schema, int capacity)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var vectors = new MutableColumnVector[schema.Count];
        for (int i = 0; i < schema.Count; i++)
        {
            vectors[i] = Create(schema[i].DataType, capacity);
        }

        return vectors;
    }
}
