namespace DeltaSharp.Engine.Types;

/// <summary>
/// Spark-parity convenience factory for the type system, mirroring Spark's
/// <c>org.apache.spark.sql.types.DataTypes</c>. The singleton atomic types are also reachable
/// directly through each type's <c>Instance</c> property; the parameterized types additionally
/// have public constructors.
/// </summary>
public static class DataTypes
{
    /// <summary>The <see cref="Types.BooleanType"/> singleton.</summary>
    public static BooleanType BooleanType => global::DeltaSharp.Engine.Types.BooleanType.Instance;

    /// <summary>The <see cref="Types.ByteType"/> singleton.</summary>
    public static ByteType ByteType => global::DeltaSharp.Engine.Types.ByteType.Instance;

    /// <summary>The <see cref="Types.ShortType"/> singleton.</summary>
    public static ShortType ShortType => global::DeltaSharp.Engine.Types.ShortType.Instance;

    /// <summary>The <see cref="Types.IntegerType"/> singleton.</summary>
    public static IntegerType IntegerType => global::DeltaSharp.Engine.Types.IntegerType.Instance;

    /// <summary>The <see cref="Types.LongType"/> singleton.</summary>
    public static LongType LongType => global::DeltaSharp.Engine.Types.LongType.Instance;

    /// <summary>The <see cref="Types.FloatType"/> singleton.</summary>
    public static FloatType FloatType => global::DeltaSharp.Engine.Types.FloatType.Instance;

    /// <summary>The <see cref="Types.DoubleType"/> singleton.</summary>
    public static DoubleType DoubleType => global::DeltaSharp.Engine.Types.DoubleType.Instance;

    /// <summary>The <see cref="Types.StringType"/> singleton.</summary>
    public static StringType StringType => global::DeltaSharp.Engine.Types.StringType.Instance;

    /// <summary>The <see cref="Types.BinaryType"/> singleton.</summary>
    public static BinaryType BinaryType => global::DeltaSharp.Engine.Types.BinaryType.Instance;

    /// <summary>The <see cref="Types.DateType"/> singleton.</summary>
    public static DateType DateType => global::DeltaSharp.Engine.Types.DateType.Instance;

    /// <summary>The <see cref="Types.TimestampType"/> singleton.</summary>
    public static TimestampType TimestampType => global::DeltaSharp.Engine.Types.TimestampType.Instance;

    /// <summary>The <see cref="Types.NullType"/> singleton (the <c>void</c> type).</summary>
    public static NullType NullType => global::DeltaSharp.Engine.Types.NullType.Instance;

    /// <summary>Creates a <see cref="Types.DecimalType"/>.</summary>
    public static DecimalType CreateDecimalType(int precision, int scale) => new(precision, scale);

    /// <summary>Creates an <see cref="Types.ArrayType"/>.</summary>
    public static ArrayType CreateArrayType(DataType elementType, bool containsNull = true) =>
        new(elementType, containsNull);

    /// <summary>Creates a <see cref="Types.MapType"/>.</summary>
    public static MapType CreateMapType(DataType keyType, DataType valueType, bool valueContainsNull = true) =>
        new(keyType, valueType, valueContainsNull);

    /// <summary>Creates a <see cref="Types.StructField"/>.</summary>
    public static StructField CreateStructField(
        string name, DataType dataType, bool nullable = true, FieldMetadata? metadata = null) =>
        new(name, dataType, nullable, metadata);

    /// <summary>Creates a <see cref="Types.StructType"/> from the given fields.</summary>
    public static StructType CreateStructType(IEnumerable<StructField> fields) => new(fields);
}
