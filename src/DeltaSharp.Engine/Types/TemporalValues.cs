namespace DeltaSharp.Engine.Types;

/// <summary>
/// v1 date/timestamp boundary, cast, and comparison helpers (STORY-02.5.2 AC3). v1 fixes two
/// assumptions, documented in <c>docs/engineering/design/type-system.md</c>: a
/// <see cref="TimestampType"/> is a <b>UTC-normalized</b> instant in <b>microseconds</b> since
/// the Unix epoch, and a <see cref="DateType"/> is a UTC calendar day count since the epoch.
/// There is no session time zone in v1, so casts and comparisons are exact integer arithmetic.
/// </summary>
public static class TemporalValues
{
    /// <summary>Microseconds in one UTC day (24 × 60 × 60 × 1_000_000).</summary>
    public const long MicrosPerDay = 86_400_000_000L;

    /// <summary>Microseconds in one second.</summary>
    public const long MicrosPerSecond = 1_000_000L;

    /// <summary>Epoch day of <c>0001-01-01</c> (the smallest supported date).</summary>
    public const int MinEpochDay = -719_162;

    /// <summary>Epoch day of <c>9999-12-31</c> (the largest supported date).</summary>
    public const int MaxEpochDay = 2_932_896;

    /// <summary>Microseconds at <c>0001-01-01T00:00:00Z</c>, the smallest supported timestamp.</summary>
    public const long MinEpochMicros = -62_135_596_800_000_000L;

    /// <summary>Microseconds at <c>9999-12-31T23:59:59.999999Z</c>, the largest supported timestamp.</summary>
    public const long MaxEpochMicros = 253_402_300_799_999_999L;

    /// <summary>Whether <paramref name="day"/> is a supported epoch day; null stays null (AC5).</summary>
    public static bool IsDateInRange(int? day) => day is null || (day >= MinEpochDay && day <= MaxEpochDay);

    /// <summary>Whether <paramref name="micros"/> is a supported epoch instant; null stays null (AC5).</summary>
    public static bool IsTimestampInRange(long? micros) =>
        micros is null || (micros >= MinEpochMicros && micros <= MaxEpochMicros);

    /// <summary>
    /// Casts an epoch day to the UTC-midnight instant of that day (Spark <c>date→timestamp</c>).
    /// A null day yields null (AC5); an out-of-range day overflows under <see cref="AnsiMode.Ansi"/>.
    /// </summary>
    public static long? DateToTimestamp(int? day, AnsiMode mode)
    {
        if (day is null)
        {
            return null;
        }

        if (!IsDateInRange(day))
        {
            return mode == AnsiMode.Ansi
                ? throw new ArithmeticOverflowException($"Date {day} out of range for timestamp cast.")
                : null;
        }

        return day.Value * MicrosPerDay;
    }

    /// <summary>
    /// Casts a timestamp to its UTC calendar day, flooring toward negative infinity so instants
    /// before the epoch map to the correct day (Spark <c>timestamp→date</c>). Null stays null
    /// (AC5); an out-of-range instant overflows under <see cref="AnsiMode.Ansi"/> and yields null
    /// under <see cref="AnsiMode.Legacy"/> — never a silently wrapped epoch day.
    /// </summary>
    public static int? TimestampToDate(long? micros, AnsiMode mode)
    {
        if (micros is null)
        {
            return null;
        }

        if (!IsTimestampInRange(micros))
        {
            return mode == AnsiMode.Ansi
                ? throw new ArithmeticOverflowException($"Timestamp {micros} out of range for date cast.")
                : null;
        }

        return (int)FloorDiv(micros.Value, MicrosPerDay);
    }

    /// <summary>
    /// Three-valued comparison of two timestamps after promoting a date to its UTC midnight.
    /// Either operand null yields null (AC5); otherwise -1/0/+1 on the microsecond instant.
    /// </summary>
    public static int? Compare(long? leftMicros, long? rightMicros) =>
        leftMicros is null || rightMicros is null ? null : leftMicros.Value.CompareTo(rightMicros.Value);

    /// <summary>Three-valued comparison of a date and a timestamp at boundary instants; null on null input.</summary>
    public static int? CompareDateToTimestamp(int? day, long? micros, AnsiMode mode) =>
        Compare(DateToTimestamp(day, mode), micros);

    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        return (a % b != 0 && (a ^ b) < 0) ? q - 1 : q;
    }
}
