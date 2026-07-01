using DeltaSharp.Types;
using ArrowTypes = Apache.Arrow.Types;

namespace DeltaSharp.Engine.Columnar.Arrow;

/// <summary>
/// Maps an Apache Arrow logical type tree to the DeltaSharp <see cref="DataType"/> contract for the
/// Arrow boundary round-trip (STORY-02.2.2, #136). It is the schema-on-read seam: every supported
/// Arrow type resolves to exactly one v1 <see cref="DataType"/>, and every gap raises a precise
/// <see cref="UnsupportedTypeException"/> that names the exact Arrow type — never silent coercion or
/// data loss. The mapping is recursive, so nested <c>struct</c>/<c>list</c>/<c>map</c> children are
/// validated all the way down before a batch is admitted.
/// </summary>
internal static class ArrowSchemaMapper
{
    /// <summary>
    /// Resolves the v1 DeltaSharp <see cref="DataType"/> for an Arrow <paramref name="arrowType"/>.
    /// </summary>
    /// <exception cref="UnsupportedTypeException">
    /// The Arrow type has no v1 DeltaSharp mapping (unsigned/half-float primitives, a non-microsecond
    /// timestamp, <c>date64</c>/<c>time</c>/<c>decimal256</c>, a <c>decimal128</c> whose precision/scale
    /// is outside DeltaSharp's range, the null type, large/view layouts, or a nested type whose children
    /// are themselves unsupported).
    /// </exception>
    internal static DataType ToDeltaType(ArrowTypes.IArrowType arrowType)
    {
        ArgumentNullException.ThrowIfNull(arrowType);

        return arrowType switch
        {
            ArrowTypes.Int8Type => ByteType.Instance,
            ArrowTypes.Int16Type => ShortType.Instance,
            ArrowTypes.Int32Type => IntegerType.Instance,
            ArrowTypes.Int64Type => LongType.Instance,
            ArrowTypes.FloatType => FloatType.Instance,
            ArrowTypes.DoubleType => DoubleType.Instance,
            ArrowTypes.BooleanType => BooleanType.Instance,
            ArrowTypes.Date32Type => DateType.Instance,
            ArrowTypes.TimestampType ts => MapTimestamp(ts),
            ArrowTypes.Decimal128Type d => MapDecimal(d),
            ArrowTypes.StringType => StringType.Instance,
            ArrowTypes.BinaryType => BinaryType.Instance,
            ArrowTypes.StructType st => MapStruct(st),
            ArrowTypes.ListType lt => new ArrayType(ToDeltaType(lt.ValueDataType), lt.ValueField.IsNullable),
            ArrowTypes.MapType mt => MapMap(mt),
            _ => throw Unsupported(arrowType),
        };
    }

    private static DataType MapTimestamp(ArrowTypes.TimestampType timestamp)
    {
        // DeltaSharp v1 stores timestamps as microseconds since the epoch; any other Arrow unit would
        // silently rescale, so it is an explicit gap (mirrors ArrowColumnVector.Wrap). The timezone
        // string is not modeled by the v1 TimestampType (UTC-normalized instant) and is dropped.
        if (timestamp.Unit != ArrowTypes.TimeUnit.Microsecond)
        {
            throw new UnsupportedTypeException(
                $"Arrow timestamp unit '{timestamp.Unit}' has no v1 columnar mapping; "
                + "only microsecond timestamps are supported.");
        }

        return TimestampType.Instance;
    }

    private static DecimalType MapDecimal(ArrowTypes.Decimal128Type decimal128)
    {
        int precision = decimal128.Precision;
        int scale = decimal128.Scale;

        // Apache.Arrow admits decimal128 precisions above DeltaSharp's Spark-parity cap of 38 (e.g.
        // Decimal128Type(40, 2)) and other out-of-range precision/scale combinations. Constructing
        // DecimalType for those would surface its SchemaValidationException, breaking this mapper's
        // "every gap -> UnsupportedTypeException" promise, so fail closed here naming the exact Arrow
        // type (council F-DEC1).
        if (precision < DecimalType.MinPrecision || precision > DecimalType.MaxPrecision
            || scale < 0 || scale > precision)
        {
            throw new UnsupportedTypeException(
                $"Arrow decimal128(precision: {precision}, scale: {scale}) has no v1 DeltaSharp columnar "
                + $"mapping; precision must be in [{DecimalType.MinPrecision}, {DecimalType.MaxPrecision}] "
                + "and scale in [0, precision].");
        }

        return new DecimalType(precision, scale);
    }

    private static StructType MapStruct(ArrowTypes.StructType arrowStruct)
    {
        var fields = new List<StructField>(arrowStruct.Fields.Count);
        foreach (Apache.Arrow.Field field in arrowStruct.Fields)
        {
            fields.Add(new StructField(field.Name, ToDeltaType(field.DataType), field.IsNullable));
        }

        return new StructType(fields);
    }

    private static MapType MapMap(ArrowTypes.MapType arrowMap)
    {
        DataType keyType = ToDeltaType(arrowMap.KeyField.DataType);
        DataType valueType = ToDeltaType(arrowMap.ValueField.DataType);
        return new MapType(keyType, valueType, arrowMap.ValueField.IsNullable);
    }

    private static UnsupportedTypeException Unsupported(ArrowTypes.IArrowType arrowType) =>
        new($"Arrow type '{arrowType.Name}' ({arrowType.TypeId}) has no v1 DeltaSharp columnar mapping "
            + "(unsigned/half-float, date64/time, non-microsecond timestamp, decimal256, null, and "
            + "large/view layouts are not supported); convert it to a supported type before importing.");
}
