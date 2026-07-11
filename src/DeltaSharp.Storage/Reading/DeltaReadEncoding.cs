using System.Globalization;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Reading;

/// <summary>
/// The read-side inverse of <see cref="DeltaSharp.Storage.Writing.DeltaWriteEncoding"/>: it re-derives a
/// partition column's typed lane values from the canonical partition-value <b>string</b> recorded in an
/// <c>add.partitionValues</c> entry (#499). Delta stores partition columns only on the add action (and the
/// Hive directory path), never inside the Parquet data file, so the read door const-fills each partition
/// column back into the output batch from this string — the exact inverse of
/// <see cref="DeltaSharp.Storage.Writing.DeltaWriteEncoding.FormatPartitionValue"/>, honoring the ADR-0008
/// lane storage (Date is an epoch-day int, Timestamp an epoch-microsecond long, compact/wide decimals the
/// unscaled integer). A <see langword="null"/> partition value — or the Hive default-partition sentinel
/// string (<see cref="DeltaSharp.Storage.Writing.DeltaWriteEncoding.HiveDefaultPartition"/>, which a
/// non-canonical/foreign writer may record literally) — fills the whole column with nulls.</summary>
internal static class DeltaReadEncoding
{
    private static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    /// <summary>Builds a constant <see cref="ColumnVector"/> of <paramref name="rowCount"/> rows all equal
    /// to the partition value <paramref name="value"/> (a <see langword="null"/> string fills nulls),
    /// parsed from its canonical Delta partition-value string into <paramref name="type"/>'s lane.</summary>
    /// <param name="type">The partition column's data type.</param>
    /// <param name="value">The canonical partition-value string, or <see langword="null"/> for a null value.</param>
    /// <param name="rowCount">The number of rows the constant column spans (the file's row count).</param>
    /// <returns>A constant column vector.</returns>
    /// <exception cref="DeltaStorageException">The type is unsupported as a partition column, or the string
    /// cannot be parsed into it.</exception>
    public static ColumnVector BuildConstantColumn(DataType type, string? value, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);

        MutableColumnVector vector = ColumnVectors.Create(type, Math.Max(rowCount, 1));

        // A JSON null OR the Hive default-partition sentinel string (`__HIVE_DEFAULT_PARTITION__`, the
        // shared const from DeltaWriteEncoding) both denote a NULL partition value. #507's write door stores
        // a real JSON null in `add.partitionValues`, so this is not a #507 round-trip break; but a
        // non-canonical / foreign writer (or a future directory-derived read) can record the sentinel
        // literally, and treating it as NULL — rather than trying to parse it into a typed lane — is the
        // unambiguous cross-engine read behavior (Spark/Delta parity), so a typed (int/long/date) partition
        // never crashes on the sentinel.
        if (value is null
            || string.Equals(value, DeltaWriteEncoding.HiveDefaultPartition, StringComparison.Ordinal))
        {
            for (int r = 0; r < rowCount; r++)
            {
                vector.AppendNull();
            }

            return vector;
        }

        FillTyped(vector, type, value, rowCount);
        return vector;
    }

    private static void FillTyped(MutableColumnVector vector, DataType type, string value, int rowCount)
    {
        switch (type)
        {
            case BooleanType:
                Fill(vector, ParseBool(value), rowCount);
                break;
            case ByteType:
                // Written as the SIGNED value (see DeltaWriteEncoding); parse sbyte, store the byte lane.
                Fill(vector, unchecked((byte)ParseSByte(value)), rowCount);
                break;
            case ShortType:
                Fill(vector, ParseInteger<short>(value, short.MinValue, short.MaxValue, v => (short)v), rowCount);
                break;
            case IntegerType:
                Fill(vector, ParseInteger<int>(value, int.MinValue, int.MaxValue, v => (int)v), rowCount);
                break;
            case LongType:
                Fill(vector, ParseLong(value), rowCount);
                break;
            case FloatType:
                Fill(vector, ParseFloat(value), rowCount);
                break;
            case DoubleType:
                Fill(vector, ParseDouble(value), rowCount);
                break;
            case DateType:
                Fill(vector, ParseDate(value), rowCount);
                break;
            case TimestampType:
                Fill(vector, ParseTimestamp(value), rowCount);
                break;
            case DecimalType decimalType:
                FillDecimal(vector, decimalType, value, rowCount);
                break;
            case StringType:
                FillBytes(vector, Encoding.UTF8.GetBytes(value), rowCount);
                break;
            default:
                throw new DeltaStorageException(
                    StorageErrorKind.UnsupportedFeature,
                    $"Type '{type.SimpleString}' is not supported as a Delta partition column.");
        }
    }

    private static void Fill<T>(MutableColumnVector vector, T value, int rowCount)
        where T : unmanaged
    {
        for (int r = 0; r < rowCount; r++)
        {
            vector.AppendValue(value);
        }
    }

    private static void FillBytes(MutableColumnVector vector, byte[] value, int rowCount)
    {
        for (int r = 0; r < rowCount; r++)
        {
            vector.AppendBytes(value);
        }
    }

    private static void FillDecimal(MutableColumnVector vector, DecimalType type, string value, int rowCount)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
        {
            throw ParseFailure(type, value);
        }

        decimal scaled = parsed;
        for (int i = 0; i < type.Scale; i++)
        {
            scaled *= 10m;
        }

        Int128 unscaled = (Int128)scaled;
        if (type.IsCompact)
        {
            Fill(vector, (long)unscaled, rowCount);
        }
        else
        {
            Fill(vector, unscaled, rowCount);
        }
    }

    private static sbyte ParseSByte(string value) =>
        sbyte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte result)
            ? result
            : throw ParseFailure(ByteType.Instance, value);

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
            ? result
            : throw ParseFailure(LongType.Instance, value);

    private static T ParseInteger<T>(string value, long min, long max, Func<long, T> narrow)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            && parsed >= min && parsed <= max)
        {
            return narrow(parsed);
        }

        throw new DeltaStorageException(
            StorageErrorKind.CorruptData,
            $"Partition value '{value}' is out of range for an integer partition column.");
    }

    private static bool ParseBool(string value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw ParseFailure(BooleanType.Instance, value),
    };

    private static float ParseFloat(string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
            ? result
            : throw ParseFailure(FloatType.Instance, value);

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            ? result
            : throw ParseFailure(DoubleType.Instance, value);

    private static int ParseDate(string value)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            return date.DayNumber - UnixEpochDate.DayNumber;
        }

        throw ParseFailure(DateType.Instance, value);
    }

    private static long ParseTimestamp(string value)
    {
        if (DateTime.TryParseExact(
                value,
                new[] { "yyyy-MM-dd HH:mm:ss.ffffff", "yyyy-MM-dd HH:mm:ss" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime parsed))
        {
            long ticks = parsed.Ticks - DateTime.UnixEpoch.Ticks;
            return ticks / TimeSpan.TicksPerMicrosecond;
        }

        throw ParseFailure(TimestampType.Instance, value);
    }

    private static DeltaStorageException ParseFailure(DataType type, string value) =>
        new(
            StorageErrorKind.CorruptData,
            $"Partition value '{value}' is not a valid '{type.SimpleString}'.");
}
