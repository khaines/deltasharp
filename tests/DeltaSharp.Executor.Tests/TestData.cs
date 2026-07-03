using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Small helpers for building typed <see cref="ColumnBatch"/>es and schemas the end-to-end tests feed
/// through the in-memory relation fixture. Nulls are expressed as CLR <c>null</c> in the value arrays.
/// </summary>
internal static class TestData
{
    public static StructType Schema(params StructField[] fields) => new(fields);

    public static StructField Field(string name, DataType type, bool nullable = true) => new(name, type, nullable);

    public static ColumnVector Ints(params int?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, Math.Max(values.Length, 1));
        foreach (int? value in values)
        {
            if (value is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendValue(value.Value);
            }
        }

        return v;
    }

    public static ColumnVector Longs(params long?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(LongType.Instance, Math.Max(values.Length, 1));
        foreach (long? value in values)
        {
            if (value is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendValue(value.Value);
            }
        }

        return v;
    }

    public static ColumnVector Doubles(params double?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DoubleType.Instance, Math.Max(values.Length, 1));
        foreach (double? value in values)
        {
            if (value is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendValue(value.Value);
            }
        }

        return v;
    }

    public static ColumnVector Strings(params string?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(StringType.Instance, Math.Max(values.Length, 1));
        foreach (string? value in values)
        {
            if (value is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendBytes(System.Text.Encoding.UTF8.GetBytes(value));
            }
        }

        return v;
    }

    public static ColumnVector Bools(params bool?[] values) =>
        Fixed(BooleanType.Instance, values, static (v, x) => v.AppendValue(x));

    // Spark ByteType is a signed tinyint stored on an unsigned byte lane.
    public static ColumnVector Bytes(params sbyte?[] values) =>
        Fixed(ByteType.Instance, values, static (v, x) => v.AppendValue(unchecked((byte)x)));

    public static ColumnVector Shorts(params short?[] values) =>
        Fixed(ShortType.Instance, values, static (v, x) => v.AppendValue(x));

    public static ColumnVector Floats(params float?[] values) =>
        Fixed(FloatType.Instance, values, static (v, x) => v.AppendValue(x));

    // A DateType lane stores the epoch-day (days since 1970-01-01) as an int.
    public static ColumnVector Dates(params int?[] epochDays) =>
        Fixed(DateType.Instance, epochDays, static (v, x) => v.AppendValue(x));

    // A TimestampType lane stores the epoch-microsecond instant as a long.
    public static ColumnVector Timestamps(params long?[] epochMicros) =>
        Fixed(TimestampType.Instance, epochMicros, static (v, x) => v.AppendValue(x));

    // A compact decimal(p<=18, s) lane stores the unscaled value as a long.
    public static ColumnVector DecimalsCompact(DecimalType type, params long?[] unscaled) =>
        Fixed(type, unscaled, static (v, x) => v.AppendValue(x));

    // A wide decimal(p>18, s) lane stores the unscaled value as an Int128.
    public static ColumnVector DecimalsWide(DecimalType type, params Int128?[] unscaled) =>
        Fixed(type, unscaled, static (v, x) => v.AppendValue(x));

    public static ColumnVector Binaries(params byte[]?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(BinaryType.Instance, Math.Max(values.Length, 1));
        foreach (byte[]? value in values)
        {
            if (value is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendBytes(value);
            }
        }

        return v;
    }

    private static ColumnVector Fixed<T>(DataType type, T?[] values, Action<MutableColumnVector, T> append)
        where T : struct
    {
        MutableColumnVector v = ColumnVectors.Create(type, Math.Max(values.Length, 1));
        foreach (T? value in values)
        {
            if (value is null)
            {
                v.AppendNull();
            }
            else
            {
                append(v, value.Value);
            }
        }

        return v;
    }

    public static ColumnBatch Batch(StructType schema, params ColumnVector[] columns)
    {
        int rowCount = columns.Length > 0 ? columns[0].Length : 0;
        return new ManagedColumnBatch(schema, columns, rowCount);
    }
}
