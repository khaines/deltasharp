using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Parquet.Schema;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Maps each ADR-0008 <b>atomic</b> <see cref="DataType"/> to a Parquet.Net <see cref="DataField"/>
/// with Spark-compatible physical semantics, and converts values between the engine's
/// <see cref="ColumnVector"/> physical layout and the CLR values Parquet.Net reads/writes
/// (design §2.9.1 "Spark-compatible physical semantics"). It mirrors
/// <c>LocalRelationBatches</c>/<c>RowMaterializer</c> for the temporal/decimal conversions, so a
/// value written and read through here is byte-for-byte the engine's physical representation.
/// </summary>
/// <remarks>
/// Supported (all round-trip): boolean, byte (Spark signed <c>tinyint</c>), short, integer, long,
/// float, double, string (UTF-8), binary, date (INT32 epoch-day), timestamp (INT64 epoch-micros,
/// <c>isAdjustedToUTC</c>), and decimal with precision ≤ 28. Deferred with a deterministic
/// <see cref="StorageErrorKind.UnsupportedFeature"/>: nested types (array/map/struct), the void
/// (null) type, and decimal with precision &gt; 28 (beyond the <see cref="decimal"/> range).
/// </remarks>
internal static class ParquetTypeMapping
{
    /// <summary>The largest decimal precision whose unscaled value fits in <see cref="decimal"/>'s
    /// 96-bit magnitude, so it round-trips through Parquet.Net's <see cref="decimal"/>-typed field.</summary>
    internal const int MaxSupportedDecimalPrecision = 28;

    private static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    // System.Decimal supports at most 28 fractional digits (mirrors RowMaterializer.MaxDecimalScale).
    private const int MaxDecimalScale = 28;
    private static readonly UInt128 MaxDecimalMagnitude = UInt128.MaxValue >> 32;

    /// <summary>
    /// Builds the Parquet <see cref="DataField"/> for <paramref name="field"/>, choosing the nullable
    /// Parquet field when <see cref="StructField.Nullable"/> is set.
    /// </summary>
    /// <exception cref="DeltaStorageException">
    /// The field's type has no supported Parquet mapping
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>): a nested type, the void type, or a decimal
    /// with precision &gt; 28.
    /// </exception>
    public static DataField CreateField(StructField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        bool nullable = field.Nullable;
        return field.DataType switch
        {
            BooleanType => Value<bool>(field.Name, nullable),
            ByteType => Value<sbyte>(field.Name, nullable),
            ShortType => Value<short>(field.Name, nullable),
            IntegerType => Value<int>(field.Name, nullable),
            LongType => Value<long>(field.Name, nullable),
            FloatType => Value<float>(field.Name, nullable),
            DoubleType => Value<double>(field.Name, nullable),
            StringType => new DataField<string>(field.Name),
            BinaryType => new DataField<byte[]>(field.Name),
            DateType => new DateTimeDataField(field.Name, DateTimeFormat.Date, isNullable: nullable),
            TimestampType => new DateTimeDataField(
                field.Name, DateTimeFormat.DateAndTimeMicros, isAdjustedToUTC: true, isNullable: nullable),
            DecimalType decimalType => CreateDecimalField(field.Name, decimalType, nullable),
            ArrayType or MapType or StructType => throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for column '{field.Name}': nested types (phased, design §2.9) — "
                + $"'{field.DataType.SimpleString}'."),
            _ => throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for column '{field.Name}' of type '{field.DataType.SimpleString}' "
                + "is not supported."),
        };
    }

    private static DataField Value<T>(string name, bool nullable)
        where T : unmanaged =>
        nullable ? new DataField<T?>(name) : new DataField<T>(name);

    private static DataField CreateDecimalField(string name, DecimalType type, bool nullable)
    {
        if (type.Precision > MaxSupportedDecimalPrecision)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for column '{name}': decimal precision {type.Precision} exceeds the "
                + $"System.Decimal limit of {MaxSupportedDecimalPrecision} (phased, design §2.9).");
        }

        return new DecimalDataField(name, type.Precision, type.Scale, isNullable: nullable);
    }

    // ----- Temporal conversions (mirror LocalRelationBatches / RowMaterializer) -----

    /// <summary>Converts a DeltaSharp epoch-day (days since 1970-01-01) to the UTC-midnight
    /// <see cref="DateTime"/> Parquet.Net writes for a DATE column.</summary>
    public static DateTime EpochDayToDateTime(int epochDay) =>
        DateTime.UnixEpoch.AddDays(epochDay);

    /// <summary>Converts a Parquet DATE <see cref="DateTime"/> back to the DeltaSharp epoch-day.</summary>
    public static int DateTimeToEpochDay(DateTime value) =>
        DateOnly.FromDateTime(value).DayNumber - UnixEpochDate.DayNumber;

    /// <summary>Converts a DeltaSharp epoch-microsecond instant to the UTC <see cref="DateTime"/>
    /// Parquet.Net writes for a micros TIMESTAMP column.</summary>
    /// <exception cref="DeltaStorageException">The value is outside the representable
    /// <see cref="DateTime"/> range (<see cref="StorageErrorKind.CorruptData"/>) — mapped
    /// deterministically so no raw <see cref="OverflowException"/> escapes the codec contract.</exception>
    public static DateTime EpochMicrosToDateTime(long micros)
    {
        try
        {
            long ticks = checked(DateTime.UnixEpoch.Ticks + (micros * TimeSpan.TicksPerMicrosecond));
            return new DateTime(ticks, DateTimeKind.Utc);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            throw DeltaStorageException.CorruptData(
                $"timestamp epoch-microsecond value {micros} is outside the representable DateTime range.", ex);
        }
    }

    /// <summary>Converts a Parquet micros TIMESTAMP <see cref="DateTime"/> back to the DeltaSharp
    /// epoch-microsecond instant. A local instant is normalized to UTC (mirrors
    /// <c>LocalRelationBatches.EncodeTimestamp</c>).</summary>
    public static long DateTimeToEpochMicros(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return (utc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;
    }

    // ----- Decimal conversions -----

    /// <summary>Reads the unscaled decimal at <paramref name="index"/> from <paramref name="column"/>
    /// and reconstructs the <see cref="decimal"/> at the declared scale (mirrors
    /// <c>RowMaterializer.ReadDecimal</c>).</summary>
    public static decimal ReadDecimal(ColumnVector column, DecimalType type, int index)
    {
        if (type.Scale > MaxDecimalScale)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"decimal scale {type.Scale} exceeds the System.Decimal maximum of {MaxDecimalScale}.");
        }

        Int128 unscaled = type.IsCompact ? column.GetValue<long>(index) : column.GetValue<Int128>(index);
        bool isNegative = unscaled < 0;
        UInt128 magnitude = isNegative ? (UInt128)(-unscaled) : (UInt128)unscaled;
        if (magnitude > MaxDecimalMagnitude)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"a '{type.SimpleString}' value's unscaled magnitude exceeds the 96-bit System.Decimal range.");
        }

        int lo = unchecked((int)(uint)magnitude);
        int mid = unchecked((int)(uint)(magnitude >> 32));
        int hi = unchecked((int)(uint)(magnitude >> 64));
        return new decimal(lo, mid, hi, isNegative, (byte)type.Scale);
    }

    /// <summary>Encodes a <see cref="decimal"/> read from Parquet as the unscaled integer lane value and
    /// appends it to <paramref name="vector"/> (mirrors <c>LocalRelationBatches.AppendDecimal</c>).</summary>
    public static void AppendDecimal(MutableColumnVector vector, DecimalType type, decimal value)
    {
        if (type.Scale > MaxDecimalScale)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"decimal scale {type.Scale} exceeds the System.Decimal maximum of {MaxDecimalScale}.");
        }

        decimal scaled = value * Pow10Decimal(type.Scale);
        if (scaled != decimal.Truncate(scaled))
        {
            throw DeltaStorageException.CorruptData(
                $"decimal value cannot be represented at scale {type.Scale} without loss of precision.");
        }

        Int128 unscaled = Int128.CreateChecked(scaled);

        // Over-precision guard (§2.9.1 mandates it on the read path too): a value whose unscaled
        // magnitude reaches 10^precision does not fit the declared decimal(P,S) and is corrupt. Mirrors
        // LocalRelationBatches.AppendDecimal.
        Int128 magnitude = unscaled < 0 ? -unscaled : unscaled;
        if (magnitude >= Pow10Int128(type.Precision))
        {
            throw DeltaStorageException.CorruptData(
                $"decimal value does not fit in precision {type.Precision} (type '{type.SimpleString}').");
        }

        if (type.IsCompact)
        {
            vector.AppendValue((long)unscaled);
        }
        else
        {
            vector.AppendValue(unscaled);
        }
    }

    /// <summary>Converts a Parquet-space <see cref="decimal"/> to the engine's unscaled
    /// <see cref="Int128"/> lane value (value × 10^scale), applying the same scale/precision guards as
    /// <see cref="AppendDecimal"/>. Used to lane-normalize row-group statistics for pruning.</summary>
    /// <exception cref="DeltaStorageException">The value cannot be represented at the declared
    /// scale/precision (<see cref="StorageErrorKind.CorruptData"/>) or the scale is unsupported
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>).</exception>
    public static Int128 DecimalToUnscaled(DecimalType type, decimal value)
    {
        if (type.Scale > MaxDecimalScale)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"decimal scale {type.Scale} exceeds the System.Decimal maximum of {MaxDecimalScale}.");
        }

        decimal scaled = value * Pow10Decimal(type.Scale);
        if (scaled != decimal.Truncate(scaled))
        {
            throw DeltaStorageException.CorruptData(
                $"decimal value cannot be represented at scale {type.Scale} without loss of precision.");
        }

        Int128 unscaled = Int128.CreateChecked(scaled);
        Int128 magnitude = unscaled < 0 ? -unscaled : unscaled;
        if (magnitude >= Pow10Int128(type.Precision))
        {
            throw DeltaStorageException.CorruptData(
                $"decimal value does not fit in precision {type.Precision} (type '{type.SimpleString}').");
        }

        return unscaled;
    }

    private static decimal Pow10Decimal(int exponent)
    {
        decimal result = 1m;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }

    private static Int128 Pow10Int128(int exponent)
    {
        Int128 result = Int128.One;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }
}
