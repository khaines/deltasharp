using System.Globalization;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Writing;

/// <summary>
/// The narrow set of columnar encoding helpers the Delta write facade (<see cref="DeltaWriteTarget"/>,
/// #487) needs: copying a single logical value from a source <see cref="ColumnVector"/> onto a
/// <see cref="MutableColumnVector"/> lane (used to split a batch into per-partition sub-batches without
/// materializing to <c>Row</c>s), and formatting a partition-column value into its canonical Delta
/// partition-value string (the string stored in <c>add.partitionValues</c> and Hive-encoded into the file
/// directory path). Both are the exact inverse of the read/materialize side and honor the ADR-0008 lane
/// storage (Date is an epoch-day int, Timestamp an epoch-microsecond long, compact/wide decimals the
/// unscaled integer).
/// </summary>
internal static class DeltaWriteEncoding
{
    private static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    /// <summary>Sentinel directory-name value for a null partition value (Hive/Delta convention). The
    /// <c>add.partitionValues</c> map stores a real JSON null; only the physical directory path uses this.</summary>
    public const string HiveDefaultPartition = "__HIVE_DEFAULT_PARTITION__";

    /// <summary>Appends the logical value at <paramref name="row"/> of <paramref name="source"/> onto
    /// <paramref name="destination"/>, preserving nulls and the ADR-0008 lane encoding. The two vectors
    /// must share the same <see cref="ColumnVector.Type"/>.</summary>
    public static void AppendValue(MutableColumnVector destination, ColumnVector source, int row)
    {
        if (source.IsNull(row))
        {
            destination.AppendNull();
            return;
        }

        switch (source.Type)
        {
            case BooleanType:
                destination.AppendValue(source.GetValue<bool>(row));
                break;
            case ByteType:
                destination.AppendValue(source.GetValue<byte>(row));
                break;
            case ShortType:
                destination.AppendValue(source.GetValue<short>(row));
                break;
            case IntegerType:
                destination.AppendValue(source.GetValue<int>(row));
                break;
            case LongType:
                destination.AppendValue(source.GetValue<long>(row));
                break;
            case FloatType:
                destination.AppendValue(source.GetValue<float>(row));
                break;
            case DoubleType:
                destination.AppendValue(source.GetValue<double>(row));
                break;
            case DateType:
                destination.AppendValue(source.GetValue<int>(row));
                break;
            case TimestampType or TimestampNtzType:
                destination.AppendValue(source.GetValue<long>(row));
                break;
            case DecimalType decimalType:
                if (decimalType.IsCompact)
                {
                    destination.AppendValue(source.GetValue<long>(row));
                }
                else
                {
                    destination.AppendValue(source.GetValue<Int128>(row));
                }

                break;
            case StringType:
            case BinaryType:
                destination.AppendBytes(source.GetBytes(row));
                break;
            default:
                throw new DeltaStorageException(
                    StorageErrorKind.UnsupportedFeature,
                    $"The Delta write facade has no columnar encoding for type '{source.Type.SimpleString}'.");
        }
    }

    /// <summary>Formats the value at <paramref name="row"/> of a partition column <paramref name="source"/>
    /// into its canonical Delta partition-value string, or <see langword="null"/> for a null value.</summary>
    public static string? FormatPartitionValue(ColumnVector source, int row)
    {
        if (source.IsNull(row))
        {
            return null;
        }

        return source.Type switch
        {
            BooleanType => source.GetValue<bool>(row) ? "true" : "false",
            ByteType => ((sbyte)source.GetValue<byte>(row)).ToString(CultureInfo.InvariantCulture),
            ShortType => source.GetValue<short>(row).ToString(CultureInfo.InvariantCulture),
            IntegerType => source.GetValue<int>(row).ToString(CultureInfo.InvariantCulture),
            LongType => source.GetValue<long>(row).ToString(CultureInfo.InvariantCulture),
            FloatType => source.GetValue<float>(row).ToString("R", CultureInfo.InvariantCulture),
            DoubleType => source.GetValue<double>(row).ToString("R", CultureInfo.InvariantCulture),
            StringType => Encoding.UTF8.GetString(source.GetBytes(row)),
            DateType => UnixEpochDate.AddDays(source.GetValue<int>(row)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimestampType or TimestampNtzType => FormatTimestamp(source.GetValue<long>(row)),
            DecimalType decimalType => FormatDecimal(source, row, decimalType),
            _ => throw new DeltaStorageException(
                StorageErrorKind.UnsupportedFeature,
                $"Type '{source.Type.SimpleString}' is not supported as a Delta partition column."),
        };
    }

    private static string FormatTimestamp(long epochMicros)
    {
        DateTime utc = DateTime.UnixEpoch.AddTicks(epochMicros * TimeSpan.TicksPerMicrosecond);
        return utc.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    // System.Decimal spans an unscaled magnitude of at most decimal.MaxValue (~7.9e28, 29 digits); an
    // Int128 unscaled value from a precision-38 Delta decimal can exceed that. Precompute the inclusive
    // bounds once so FormatDecimal can range-check instead of letting the `(decimal)` cast throw a raw
    // OverflowException (which would escape the storage layer's deterministic exception contract).
    private static readonly Int128 MaxDecimalUnscaled = (Int128)decimal.MaxValue;
    private static readonly Int128 MinDecimalUnscaled = (Int128)decimal.MinValue;

    private static string FormatDecimal(ColumnVector source, int row, DecimalType type)
    {
        Int128 unscaled = type.IsCompact ? source.GetValue<long>(row) : source.GetValue<Int128>(row);
        if (unscaled < MinDecimalUnscaled || unscaled > MaxDecimalUnscaled)
        {
            throw new DeltaStorageException(
                StorageErrorKind.UnsupportedFeature,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Decimal partition value (precision {type.Precision}, scale {type.Scale}) has an " +
                    $"unscaled magnitude that exceeds System.Decimal's range and cannot be formatted as a " +
                    $"Delta partition value."));
        }

        decimal value = (decimal)unscaled;
        for (int i = 0; i < type.Scale; i++)
        {
            value /= 10m;
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }
}
