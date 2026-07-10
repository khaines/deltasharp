namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Shared conversion of a <c>_delta_log</c> object's modification time to Delta epoch milliseconds — the unit
/// timestamp time travel (design §2.12.1) and VACUUM orphan-age comparisons operate in.
///
/// <para><b>Backend contract.</b> <see cref="Backends.StorageObjectInfo.LastModifiedUtc"/> must carry the
/// modification instant in <b>UTC</b> (a <see cref="DateTimeKind.Utc"/> value); an <see cref="DateTimeKind.Unspecified"/>
/// value is treated as already-UTC per that contract. A <see cref="DateTimeKind.Local"/> value (a
/// non-conforming backend) is <b>converted</b> to UTC by reproducing <see cref="DateTime.ToUniversalTime()"/>
/// via <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> — never silently reinterpreted — so a backend whose
/// listing returns a local-kind time cannot shift timestamp resolution by the machine's timezone offset.
/// <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> consults the zone's full historical adjustment rules, so
/// the conversion is historically accurate (even for zones that shifted their base offset) and, for a
/// DST-invalid spring-forward gap time, uses the pre-transition offset. The out-of-range result is clamped to
/// <c>[DateTime.MinValue, DateTime.MaxValue]</c> exactly as <c>DateTime.ToUniversalTime()</c> saturates at the
/// boundaries — so it never throws and matches the prior <c>DateTime.ToUniversalTime()</c> behavior byte-for-byte,
/// including at the <see cref="DateTime"/> range boundaries.</para>
/// </summary>
internal static class DeltaTimestamps
{
    /// <summary>Converts <paramref name="lastModified"/> to Delta epoch milliseconds, honoring the
    /// <see cref="Backends.StorageObjectInfo.LastModifiedUtc"/> UTC contract: <see cref="DateTimeKind.Utc"/> and
    /// <see cref="DateTimeKind.Unspecified"/> are used as UTC, while <see cref="DateTimeKind.Local"/> is
    /// converted with <see cref="TimeZoneInfo.Local"/> rather than reinterpreted.</summary>
    internal static long ToEpochMillis(DateTime lastModified) => ToEpochMillis(lastModified, TimeZoneInfo.Local);

    /// <summary>Converts <paramref name="lastModified"/> to Delta epoch milliseconds, honoring the
    /// <see cref="Backends.StorageObjectInfo.LastModifiedUtc"/> UTC contract: <see cref="DateTimeKind.Utc"/> and
    /// <see cref="DateTimeKind.Unspecified"/> are used as UTC, while <see cref="DateTimeKind.Local"/> is
    /// <b>converted</b> to UTC using <paramref name="localZone"/> rather than reinterpreted. The
    /// <paramref name="localZone"/> parameter is injectable so tests can exercise the conversion
    /// deterministically regardless of the host's ambient timezone; production callers pass
    /// <see cref="TimeZoneInfo.Local"/>. The conversion reproduces <c>DateTime.ToUniversalTime()</c> via
    /// <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> (which consults the zone's full historical adjustment
    /// rules), so it is historically accurate and, for a DST-invalid spring-forward gap instant, uses the
    /// pre-transition offset — matching the prior <c>DateTime.ToUniversalTime()</c> result exactly, including at
    /// the <see cref="DateTime"/> range boundaries (the result is clamped as <c>ToUniversalTime()</c> saturates) —
    /// so this method never throws.</summary>
    internal static long ToEpochMillis(DateTime lastModified, TimeZoneInfo localZone)
    {
        DateTime utc = lastModified.Kind switch
        {
            DateTimeKind.Local => LocalToUtc(lastModified, localZone),
            _ => DateTime.SpecifyKind(lastModified, DateTimeKind.Utc),
        };

        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

    /// <summary>Converts a <see cref="DateTimeKind.Local"/> instant to UTC via <paramref name="localZone"/>,
    /// reproducing <c>DateTime.ToUniversalTime()</c> exactly. <see cref="DateTime.ToUniversalTime()"/> internally
    /// computes <c>local - TimeZoneInfo.Local.GetUtcOffset(local)</c>, and
    /// <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> consults the zone's <b>full historical adjustment rules</b>
    /// (and, for a DST-invalid spring-forward gap time, returns the pre-transition offset). Computing the instant
    /// uniformly as <c>unspecified - GetUtcOffset(unspecified)</c> and <b>clamping the tick result to
    /// <c>[DateTime.MinValue, DateTime.MaxValue]</c></b> therefore (a) never throws — subtracting a west (negative)
    /// offset from <see cref="DateTime.MaxValue"/> or an east (positive) offset from <see cref="DateTime.MinValue"/>
    /// would otherwise overflow the <see cref="DateTime"/> range, but <c>ToUniversalTime()</c> saturates at those
    /// bounds and <see cref="Math.Clamp{T}(T,T,T)"/> to the same bounds reproduces that saturation byte-for-byte,
    /// (b) is historically accurate — correct even for zones that shifted their <i>base</i> offset (e.g.
    /// <c>Pacific/Apia</c>'s 2011 International Date Line skip, which <see cref="TimeZoneInfo.BaseUtcOffset"/> would
    /// resolve with the wrong, current base offset), (c) exactly reproduces <c>ToUniversalTime()</c> for the ambient
    /// zone (including at the range boundaries), and (d) still converts (never reinterprets) through the injected
    /// zone so a reinterpret regression remains detectable (a fixed -7 zone yields <c>GetUtcOffset = -7</c> for all
    /// interior times, so the result differs from a <c>SpecifyKind(dt, Utc)</c> reinterpret by 7h).</summary>
    private static DateTime LocalToUtc(DateTime lastModified, TimeZoneInfo localZone)
    {
        // SpecifyKind to Unspecified first: GetUtcOffset resolves a Local-kind DateTime against TimeZoneInfo.Local
        // rather than the injected zone, so normalize the Kind before consulting the target zone's rules.
        DateTime unspecified = DateTime.SpecifyKind(lastModified, DateTimeKind.Unspecified);
        long ticks = unspecified.Ticks - localZone.GetUtcOffset(unspecified).Ticks;
        // Clamp to the DateTime range, exactly as DateTime.ToUniversalTime() saturates at the boundaries, so
        // subtracting a west (negative) offset from DateTime.MaxValue or an east (positive) offset from
        // DateTime.MinValue can never throw ArgumentOutOfRangeException.
        ticks = Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
        return new DateTime(ticks, DateTimeKind.Utc);
    }
}
