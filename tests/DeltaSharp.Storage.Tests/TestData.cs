using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Shared helpers for the storage tests: builds random <see cref="ColumnBatch"/>es for the supported
/// atomic types (mirroring the engine's physical layout) and compares batches cell-by-cell so the
/// round-trip parity oracle asserts values, validity, and order.
/// </summary>
internal static class TestData
{
    private static readonly string[] StringSamples =
    {
        string.Empty, "a", "hello", "héllo", "日本語", "emoji-😀", "with space", "line\nbreak", "quote\"'",
    };

    /// <summary>Builds a random <see cref="ColumnBatch"/> conforming to <paramref name="schema"/>.
    /// Nullable columns get roughly 25% nulls; required columns get none.</summary>
    public static ColumnBatch RandomBatch(StructType schema, int rowCount, SeededRandom random)
    {
        var columns = new ColumnVector[schema.Count];
        for (int c = 0; c < schema.Count; c++)
        {
            StructField field = schema[c];
            MutableColumnVector vector = ColumnVectors.Create(field.DataType, Math.Max(rowCount, 1));
            for (int r = 0; r < rowCount; r++)
            {
                if (field.Nullable && random.Next(4) == 0)
                {
                    vector.AppendNull();
                }
                else
                {
                    AppendRandom(vector, field.DataType, random);
                }
            }

            columns[c] = vector;
        }

        return new ManagedColumnBatch(schema, columns, rowCount);
    }

    private static void AppendRandom(MutableColumnVector vector, DataType type, SeededRandom random)
    {
        switch (type)
        {
            case BooleanType:
                vector.AppendValue(random.NextBool());
                break;
            case ByteType:
                vector.AppendValue((byte)random.Next(0, 256));
                break;
            case ShortType:
                vector.AppendValue((short)random.Next(short.MinValue, short.MaxValue + 1));
                break;
            case IntegerType:
                vector.AppendValue(random.Next(int.MinValue, int.MaxValue));
                break;
            case LongType:
                vector.AppendValue(((long)random.Next() << 21) ^ random.Next());
                break;
            case FloatType:
                vector.AppendValue((float)((random.NextDouble() * 2000.0) - 1000.0));
                break;
            case DoubleType:
                vector.AppendValue((random.NextDouble() * 2.0e9) - 1.0e9);
                break;
            case StringType:
                vector.AppendBytes(Encoding.UTF8.GetBytes(StringSamples[random.Next(StringSamples.Length)]));
                break;
            case BinaryType:
                int length = random.Next(0, 8);
                var bytes = new byte[length];
                random.NextBytes(bytes);
                vector.AppendBytes(bytes);
                break;
            case DateType:
                // A calendar date well within the DateOnly/epoch-day range.
                vector.AppendValue(random.Next(-100_000, 100_000));
                break;
            case TimestampType:
            case TimestampNtzType:
                // Epoch micros within a safe multi-century window around the epoch. timestamp_ntz shares the
                // same long lane as timestamp (only the wire isAdjustedToUTC annotation differs).
                vector.AppendValue(((long)random.Next(-500_000, 500_000) * TimeSpan.TicksPerMillisecond) + random.Next());
                break;
            case DecimalType decimalType:
                AppendRandomDecimal(vector, decimalType, random);
                break;
            default:
                throw new NotSupportedException($"No random generator for '{type.SimpleString}'.");
        }
    }

    private static void AppendRandomDecimal(MutableColumnVector vector, DecimalType type, SeededRandom random)
    {
        // A random unscaled magnitude strictly within 10^precision so the value is representable at its
        // declared precision/scale.
        Int128 bound = Pow10(type.Precision);
        Int128 magnitude = RandomInt128(random, bound);
        Int128 unscaled = random.NextBool() ? magnitude : -magnitude;
        if (type.IsCompact)
        {
            vector.AppendValue((long)unscaled);
        }
        else
        {
            vector.AppendValue(unscaled);
        }
    }

    private static Int128 RandomInt128(SeededRandom random, Int128 exclusiveBound)
    {
        // Build a non-negative Int128 in [0, exclusiveBound) from 64-bit random chunks.
        UInt128 raw = ((UInt128)(uint)random.Next() << 96)
            | ((UInt128)(uint)random.Next() << 64)
            | ((UInt128)(uint)random.Next() << 32)
            | (uint)random.Next();
        return (Int128)(raw % (UInt128)exclusiveBound);
    }

    private static Int128 Pow10(int exponent)
    {
        Int128 result = Int128.One;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }

    /// <summary>Asserts two batches carry identical schema, row count, and cell values (incl. validity).</summary>
    public static void AssertBatchesEqual(ColumnBatch expected, ColumnBatch actual)
    {
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.LogicalRowCount, actual.LogicalRowCount);
        StructType schema = expected.Schema;
        for (int c = 0; c < schema.Count; c++)
        {
            ColumnVector expectedColumn = expected.SelectedColumn(c);
            ColumnVector actualColumn = actual.SelectedColumn(c);
            for (int r = 0; r < expected.LogicalRowCount; r++)
            {
                AssertCellEqual(schema[c], expectedColumn, actualColumn, r);
            }
        }
    }

    private static void AssertCellEqual(StructField field, ColumnVector expected, ColumnVector actual, int row)
    {
        Assert.Equal(expected.IsNull(row), actual.IsNull(row));
        if (expected.IsNull(row))
        {
            return;
        }

        if (field.DataType is BinaryType)
        {
            Assert.True(
                expected.GetBytes(row).SequenceEqual(actual.GetBytes(row)),
                $"Binary cell mismatch in column '{field.Name}' at row {row}.");
            return;
        }

        Assert.Equal(ReadCell(expected, field.DataType, row), ReadCell(actual, field.DataType, row));
    }

    /// <summary>Reads a single non-null cell as a boxed CLR value for equality comparison.</summary>
    public static object ReadCell(ColumnVector column, DataType type, int row) => type switch
    {
        BooleanType => column.GetValue<bool>(row),
        ByteType => column.GetValue<byte>(row),
        ShortType => column.GetValue<short>(row),
        IntegerType => column.GetValue<int>(row),
        LongType => column.GetValue<long>(row),
        FloatType => column.GetValue<float>(row),
        DoubleType => column.GetValue<double>(row),
        DecimalType { IsCompact: true } => column.GetValue<long>(row),
        DecimalType => column.GetValue<Int128>(row),
        DateType => column.GetValue<int>(row),
        TimestampType or TimestampNtzType => column.GetValue<long>(row),
        StringType => Encoding.UTF8.GetString(column.GetBytes(row)),
        BinaryType => column.GetBytes(row).ToArray(),
        _ => throw new NotSupportedException($"No cell reader for '{type.SimpleString}'."),
    };
}
