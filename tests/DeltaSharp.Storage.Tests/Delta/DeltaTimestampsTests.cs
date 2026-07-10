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
}
