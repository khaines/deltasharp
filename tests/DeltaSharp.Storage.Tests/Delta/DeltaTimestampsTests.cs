using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Unit coverage for <see cref="DeltaTimestamps.ToEpochMillis"/> (FIX 2). The helper is the single home for
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
        // not reinterpreted (which would shift the epoch by the machine's timezone offset). On a UTC-offset
        // machine both agree; off UTC, only the conversion matches the true instant — which is the point.
        var local = new DateTime(2024, 6, 1, 8, 30, 0, DateTimeKind.Local);

        long millis = DeltaTimestamps.ToEpochMillis(local);

        // The true instant of a Local DateTime uses the local UTC offset (what a correct conversion yields).
        Assert.Equal(new DateTimeOffset(local).ToUnixTimeMilliseconds(), millis);
        // Equivalently, converting explicitly to UTC first must produce the identical epoch.
        Assert.Equal(
            new DateTimeOffset(local.ToUniversalTime()).ToUnixTimeMilliseconds(),
            millis);
    }
}
