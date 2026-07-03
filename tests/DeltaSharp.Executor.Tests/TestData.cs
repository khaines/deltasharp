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

    public static ColumnBatch Batch(StructType schema, params ColumnVector[] columns)
    {
        int rowCount = columns.Length > 0 ? columns[0].Length : 0;
        return new ManagedColumnBatch(schema, columns, rowCount);
    }
}
