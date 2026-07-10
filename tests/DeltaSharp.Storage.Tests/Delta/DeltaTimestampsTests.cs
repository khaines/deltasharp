using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Unit coverage for <see cref="DeltaTimestamps.ToEpochMillis(System.DateTime)"/> (FIX 2). The helper is the single home for
/// converting a <c>_delta_log</c> object's <see cref="System.DateTime"/> modification time to Delta epoch
/// milliseconds for both timestamp time travel (<see cref="DeltaLog"/>) and VACUUM orphan-age comparisons.
/// It must honor the <c>StorageObjectInfo.LastModifiedUtc</c> UTC contract without silently reinterpreting a
/// non-UTC <see cref="System.DateTime.Kind"/>: <c>Local</c> is <b>converted</b> (never reinterpreted), while
/// <c>Utc</c> and <c>Unspecified</c> are treated as already-UTC.
/// </summary>
public sealed class DeltaTimestampsTests
{
    [Fact]
    public void ToEpochMillis_UtcKind_UsesInstantAsIs()
    {
        var utc = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        long millis = DeltaTimestamps.ToEpochMillis(utc);

        Assert.Equal(new DateTimeOffset(utc).ToUnixTimeMilliseconds(), millis);
    }

    [Fact]
    public void ToEpochMillis_UnspecifiedKind_TreatedAsUtc()
    {
        var unspecified = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);

        long millis = DeltaTimestamps.ToEpochMillis(unspecified);

        // Contract: Unspecified is already-UTC, so it must equal the same wall-clock read as UTC.
        long asUtc = new DateTimeOffset(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        Assert.Equal(asUtc, millis);
    }

    [Fact]
    public void ToEpochMillis_LocalKind_ConvertsToUtc_NotReinterpreted()
    {
        // A non-conforming backend returning a Local-kind mtime must be CONVERTED to UTC (its true instant),
        // not reinterpreted (which would shift the epoch by the timezone offset). This test injects a fixed
        // non-zero-offset zone so the convert-vs-reinterpret discrimination is REAL on every host — including
        // a UTC-offset CI host, where the ambient offset is zero and the two paths would otherwise coincide.
        var pst = TimeZoneInfo.CreateCustomTimeZone(
            "deltasharp-test-pst", TimeSpan.FromHours(-7), "Test PST", "Test PST");
        var local = new DateTime(2024, 6, 1, 8, 30, 0, DateTimeKind.Local);

        long converted = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), pst))
            .ToUnixTimeMilliseconds();
        long reinterpret = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        // Sanity: with a -7 zone the two genuinely differ, so the assertion below has real discriminating power.
        Assert.NotEqual(reinterpret, converted);

        // TEETH: the helper must CONVERT using the injected zone. A reinterpret mutant that ignores the zone
        // yields `reinterpret` and FAILS this on any host, including a UTC CI host.
        Assert.Equal(converted, DeltaTimestamps.ToEpochMillis(local, pst));

        // The production (ambient) overload still converts via TimeZoneInfo.Local — the true instant of a
        // Local DateTime — so the ambient path remains covered.
        Assert.Equal(new DateTimeOffset(local).ToUnixTimeMilliseconds(), DeltaTimestamps.ToEpochMillis(local));
    }

    [Fact]
    public void ToEpochMillis_LocalKind_DstInvalidGapTime_ResolvesGracefully_WithoutThrowing()
    {
        // Round-3 converted the Local arm to TimeZoneInfo.ConvertTimeToUtc, which THROWS ArgumentException for a
        // DST spring-forward "invalid" wall-clock time (the ~1h gap that never existed). The prior
        // DateTime.ToUniversalTime() resolved such times gracefully using the pre-transition (standard) offset.
        // This regression test injects a custom DST-observing zone with an explicit spring-forward rule so a known
        // local time is invalid on EVERY host (including a UTC CI host with no ambient DST), and asserts the helper
        // no longer throws and returns the pre-transition-offset epoch that ToUniversalTime() produced.
        var springForward = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 10);
        var fallBack = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 3);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), springForward, fallBack);
        var dstZone = TimeZoneInfo.CreateCustomTimeZone(
            "deltasharp-test-dst", TimeSpan.FromHours(-8), "Test DST", "Test Standard", "Test Daylight", new[] { rule });

        // 02:30 on the spring-forward day never existed in this zone (clocks jump 02:00 -> 03:00).
        var gap = new DateTime(2024, 3, 10, 2, 30, 0, DateTimeKind.Local);
        Assert.True(dstZone.IsInvalidTime(DateTime.SpecifyKind(gap, DateTimeKind.Unspecified)));

        // Old ToUniversalTime() resolved the gap with the standard (pre-transition) -08:00 offset => 10:30 UTC.
        long expected = new DateTimeOffset(new DateTime(2024, 3, 10, 10, 30, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        // Must NOT throw and must equal the graceful pre-transition-offset epoch.
        long actual = DeltaTimestamps.ToEpochMillis(gap, dstZone);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ToEpochMillis_LocalKind_DstAmbiguousFallBackTime_ResolvesToStandardTime()
    {
        // Quality r4 (Low): cover the fall-back AMBIGUOUS hour on the valid path. When clocks fall back
        // (daylight 02:00 -> standard 01:00), local times in [01:00, 02:00) occur twice. DateTime.ToUniversalTime()
        // (and TimeZoneInfo.GetUtcOffset) resolve an ambiguous time to STANDARD time. This test injects a custom
        // DST-observing zone so the ambiguity is deterministic on every host, and asserts the helper picks the
        // standard-time (ToUniversalTime-consistent) instant.
        var springForward = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 10);
        var fallBack = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 3);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), springForward, fallBack);
        var dstZone = TimeZoneInfo.CreateCustomTimeZone(
            "deltasharp-test-dst-amb", TimeSpan.FromHours(-8), "Test DST", "Test Standard", "Test Daylight", new[] { rule });

        // 01:30 on the fall-back day occurs twice (daylight 02:00 -> standard 01:00).
        var ambiguous = new DateTime(2024, 11, 3, 1, 30, 0, DateTimeKind.Local);
        Assert.True(dstZone.IsAmbiguousTime(DateTime.SpecifyKind(ambiguous, DateTimeKind.Unspecified)));

        // ToUniversalTime()/GetUtcOffset resolve ambiguity to the standard (-08:00) offset => 09:30 UTC.
        long expected = new DateTimeOffset(new DateTime(2024, 11, 3, 9, 30, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        Assert.Equal(expected, DeltaTimestamps.ToEpochMillis(ambiguous, dstZone));
    }

    [Fact]
    public void ToEpochMillis_LocalKind_HistoricalBaseOffsetShift_UsesHistoricalOffset_NotCurrentBase()
    {
        // Round-4 resolved DST-invalid gap times with TimeZoneInfo.BaseUtcOffset, which is the zone's CURRENT
        // base offset. Zones that historically SHIFTED their base offset (e.g. Pacific/Apia's end-2011
        // International Date Line skip: -11:00 -> +13:00) therefore resolved a PRE-shift invalid-gap time with
        // the WRONG offset — up to a 24h discrepancy vs the original DateTime.ToUniversalTime(), which consults
        // the zone's FULL historical adjustment rules via GetUtcOffset. This regression test builds a custom zone
        // whose current base is +13:00 but whose older era (2000..2009) had an effective base of +02:00 (a -11h
        // baseUtcOffsetDelta, mirroring Apia's dateline shift), with a spring-forward gap in that older era. It
        // proves the helper resolves the pre-shift gap with the HISTORICAL (+02:00) offset — matching
        // ToUniversalTime() — NOT the current +13:00 base that BaseUtcOffset would (wrongly) apply.
        var oldEraSpringForward = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 9, 26);
        var oldEraFallBack = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 4, 4);
        var oldRule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2000, 1, 1), new DateTime(2009, 12, 31),
            TimeSpan.FromHours(1),        // daylightDelta
            oldEraSpringForward, oldEraFallBack,
            TimeSpan.FromHours(-11));     // baseUtcOffsetDelta => effective base +13-11 = +02:00 in this era
        var shiftZone = TimeZoneInfo.CreateCustomTimeZone(
            "deltasharp-test-hist-shift", TimeSpan.FromHours(13), "Hist Shift", "Hist Standard", "Hist Daylight",
            new[] { oldRule });

        // 02:30 on the old-era spring-forward day never existed (clocks jump 02:00 -> 03:00).
        var oldGap = new DateTime(2005, 9, 26, 2, 30, 0, DateTimeKind.Local);
        var oldGapUnspec = DateTime.SpecifyKind(oldGap, DateTimeKind.Unspecified);
        Assert.True(shiftZone.IsInvalidTime(oldGapUnspec));
        // Sanity: the historical (+02:00) and current-base (+13:00) offsets genuinely differ (by 11h here).
        Assert.NotEqual(shiftZone.BaseUtcOffset, shiftZone.GetUtcOffset(oldGapUnspec));

        // Historical (+02:00) offset => 00:30 UTC. This is what ToUniversalTime()/GetUtcOffset produce.
        long expected = new DateTimeOffset(new DateTime(2005, 9, 26, 0, 30, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        // The round-4 BaseUtcOffset (+13:00) bug would instead yield the prior day 13:30 UTC — assert we DON'T.
        long buggyBaseOffset = new DateTimeOffset(
            DateTime.SpecifyKind(oldGapUnspec - shiftZone.BaseUtcOffset, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        long actual = DeltaTimestamps.ToEpochMillis(oldGap, shiftZone);
        Assert.Equal(expected, actual);
        Assert.NotEqual(buggyBaseOffset, actual);
    }
}
