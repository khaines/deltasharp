using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// Materializes an execution <see cref="BatchResult"/> into Core <see cref="Row"/>s, converting each
/// EPIC-03 <see cref="ColumnVector"/> lane to the natural CLR value for its logical
/// <see cref="DataType"/> (ADR-0002 column format → Spark-compatible <see cref="Row"/> values),
/// null-aware. It reads through <see cref="ColumnBatch.SelectedColumn"/> so any selection vector a
/// filter/limit left on a batch is honored.
/// </summary>
internal static class RowMaterializer
{
    /// <summary>Materializes every logical row of every batch into a <see cref="Row"/>.</summary>
    /// <param name="result">The executed schema + batches.</param>
    /// <returns>All rows, in batch-then-row order.</returns>
    public static IReadOnlyList<Row> Materialize(BatchResult result)
    {
        StructType schema = result.Schema;
        var rows = new List<Row>();
        foreach (ColumnBatch batch in result.Batches)
        {
            int rowCount = batch.LogicalRowCount;
            int columnCount = schema.Count;
            var columns = new ColumnVector[columnCount];
            for (int c = 0; c < columnCount; c++)
            {
                columns[c] = batch.SelectedColumn(c);
            }

            for (int r = 0; r < rowCount; r++)
            {
                var values = new object?[columnCount];
                for (int c = 0; c < columnCount; c++)
                {
                    values[c] = columns[c].IsNull(r) ? null : ReadValue(columns[c], schema[c].DataType, r);
                }

                rows.Add(new Row(schema, values));
            }
        }

        return rows;
    }

    /// <summary>Sums the logical row counts across the result's batches without materializing values.</summary>
    /// <param name="result">The executed schema + batches.</param>
    /// <returns>The total logical row count.</returns>
    public static long CountRows(BatchResult result)
    {
        long count = 0;
        foreach (ColumnBatch batch in result.Batches)
        {
            count += batch.LogicalRowCount;
        }

        return count;
    }

    private static object ReadValue(ColumnVector column, DataType type, int index) => type switch
    {
        BooleanType => column.GetValue<bool>(index),

        // Spark ByteType is a signed tinyint; the Engine stores it as an unsigned byte lane.
        ByteType => unchecked((sbyte)column.GetValue<byte>(index)),
        ShortType => column.GetValue<short>(index),
        IntegerType or DateType => column.GetValue<int>(index),
        LongType or TimestampType => column.GetValue<long>(index),
        FloatType => column.GetValue<float>(index),
        DoubleType => column.GetValue<double>(index),
        DecimalType decimalType => ReadDecimal(column, decimalType, index),
        StringType => Encoding.UTF8.GetString(column.GetBytes(index)),
        BinaryType => column.GetBytes(index).ToArray(),
        _ => throw new UnsupportedPlanException(
            $"Row materialization has no CLR mapping for type '{type.SimpleString}'."),
    };

    private static decimal ReadDecimal(ColumnVector column, DecimalType type, int index)
    {
        Int128 unscaled = type.IsCompact ? column.GetValue<long>(index) : column.GetValue<Int128>(index);
        return (decimal)unscaled / Pow10(type.Scale);
    }

    private static decimal Pow10(int scale)
    {
        decimal result = 1m;
        for (int i = 0; i < scale; i++)
        {
            result *= 10m;
        }

        return result;
    }
}
