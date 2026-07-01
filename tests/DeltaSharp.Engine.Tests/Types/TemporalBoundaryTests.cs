using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

/// <summary>
/// STORY-02.5.2 AC3/AC5: date/timestamp boundary instants, UTC-microsecond cast/compare, and
/// null propagation. Documents the v1 tz/precision assumptions (UTC, microseconds, no session tz).
/// </summary>
public class TemporalBoundaryTests
{
    [Fact]
    public void MinMaxConstants_AreSelfConsistent()
    {
        Assert.Equal(TemporalValues.MinEpochMicros, TemporalValues.MinEpochDay * TemporalValues.MicrosPerDay);
        Assert.True(TemporalValues.MaxEpochMicros < (TemporalValues.MaxEpochDay + 1) * TemporalValues.MicrosPerDay);
        Assert.True(TemporalValues.IsDateInRange(TemporalValues.MinEpochDay));
        Assert.True(TemporalValues.IsDateInRange(TemporalValues.MaxEpochDay));
        Assert.True(TemporalValues.IsTimestampInRange(TemporalValues.MaxEpochMicros));
    }

    [Fact]
    public void DateToTimestamp_IsUtcMidnight()
    {
        Assert.Equal(0L, TemporalValues.DateToTimestamp(0, AnsiMode.Ansi)); // 1970-01-01
        Assert.Equal(TemporalValues.MicrosPerDay, TemporalValues.DateToTimestamp(1, AnsiMode.Ansi));
        Assert.Equal(-TemporalValues.MicrosPerDay, TemporalValues.DateToTimestamp(-1, AnsiMode.Ansi));
    }

    [Fact]
    public void TimestampToDate_FloorsTowardNegativeInfinity()
    {
        Assert.Equal(0, TemporalValues.TimestampToDate(0, AnsiMode.Ansi));
        Assert.Equal(0, TemporalValues.TimestampToDate(TemporalValues.MicrosPerDay - 1, AnsiMode.Ansi));
        Assert.Equal(-1, TemporalValues.TimestampToDate(-1, AnsiMode.Ansi)); // one micro before epoch → prior day
    }

    [Fact]
    public void Boundary_DateOutOfRange_AnsiThrows_LegacyNull()
    {
        Assert.Throws<ArithmeticOverflowException>(() => TemporalValues.DateToTimestamp(TemporalValues.MaxEpochDay + 1, AnsiMode.Ansi));
        Assert.Null(TemporalValues.DateToTimestamp(TemporalValues.MaxEpochDay + 1, AnsiMode.Legacy));
    }

    [Fact]
    public void Boundary_TimestampOutOfRange_AnsiThrows_LegacyNull_NeverWrapsEpochDay()
    {
        Assert.Throws<ArithmeticOverflowException>(() => TemporalValues.TimestampToDate(TemporalValues.MaxEpochMicros + 1, AnsiMode.Ansi));
        Assert.Throws<ArithmeticOverflowException>(() => TemporalValues.TimestampToDate(TemporalValues.MinEpochMicros - 1, AnsiMode.Ansi));
        Assert.Null(TemporalValues.TimestampToDate(long.MaxValue, AnsiMode.Legacy));
        Assert.Null(TemporalValues.TimestampToDate(TemporalValues.MaxEpochMicros + 1, AnsiMode.Legacy));
    }

    [Fact]
    public void CompareDateToTimestamp_AtBoundaryInstant()
    {
        Assert.Equal(0, TemporalValues.CompareDateToTimestamp(0, 0, AnsiMode.Ansi)); // midnight equals
        Assert.Equal(-1, TemporalValues.CompareDateToTimestamp(0, 1, AnsiMode.Ansi)); // one micro later
        Assert.Equal(1, TemporalValues.CompareDateToTimestamp(1, 0, AnsiMode.Ansi));
    }

    [Fact]
    public void NullPropagation_OnAnyNullInput()
    {
        Assert.Null(TemporalValues.DateToTimestamp(null, AnsiMode.Ansi));
        Assert.Null(TemporalValues.TimestampToDate(null, AnsiMode.Ansi));
        Assert.Null(TemporalValues.Compare(0, null));
        Assert.Null(TemporalValues.CompareDateToTimestamp(null, 0, AnsiMode.Ansi));
        Assert.True(TemporalValues.IsDateInRange(null)); // null is in-range (stays null)
    }
}
