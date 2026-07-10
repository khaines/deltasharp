namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Shared conversion of a <c>_delta_log</c> object's modification time to Delta epoch milliseconds — the unit
/// timestamp time travel (design §2.12.1) and VACUUM orphan-age comparisons operate in.
///
/// <para><b>Backend contract.</b> <see cref="Backends.StorageObjectInfo.LastModifiedUtc"/> must carry the
/// modification instant in <b>UTC</b> (a <see cref="DateTimeKind.Utc"/> value); an <see cref="DateTimeKind.Unspecified"/>
/// value is treated as already-UTC per that contract. A <see cref="DateTimeKind.Local"/> value (a
/// non-conforming backend) is <b>converted</b> to UTC via <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/>
/// — never silently reinterpreted — so a backend whose listing returns a local-kind time cannot shift timestamp
/// resolution by the machine's timezone offset. DST-invalid local times (the spring-forward gap) are resolved
/// gracefully with the pre-transition (standard) offset — matching the prior <c>DateTime.ToUniversalTime()</c>
/// behavior byte-for-byte — so they never throw.</para>
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
    /// <see cref="TimeZoneInfo.Local"/>. A DST-invalid local instant (the spring-forward gap, where
    /// <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> would throw) is resolved with the
    /// pre-transition (standard) offset — matching the prior <c>DateTime.ToUniversalTime()</c> result exactly —
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
    /// converting (never reinterpreting) so the machine's timezone offset cannot shift resolution. A DST-invalid
    /// spring-forward instant — for which <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> throws —
    /// is instead resolved with the zone's standard (pre-transition) offset. This reproduces the previous
    /// <c>DateTime.ToUniversalTime()</c> instant byte-for-byte (verified across a full-year, multi-zone sweep) while
    /// still converting valid times through the injected zone so a reinterpret regression remains detectable.</summary>
    private static DateTime LocalToUtc(DateTime lastModified, TimeZoneInfo localZone)
    {
        // SpecifyKind to Unspecified first: ConvertTimeToUtc throws for a Local-kind DateTime paired with a
        // non-Local zone (and IsInvalidTime/IsAmbiguousTime expect a non-UTC/non-Local kind for the target zone).
        DateTime unspecified = DateTime.SpecifyKind(lastModified, DateTimeKind.Unspecified);

        // Spring-forward gap: this wall-clock time never existed, so ConvertTimeToUtc would throw. The prior
        // ToUniversalTime() resolved it gracefully using the pre-transition (standard) offset; reproduce that.
        if (localZone.IsInvalidTime(unspecified))
        {
            return DateTime.SpecifyKind(unspecified - localZone.BaseUtcOffset, DateTimeKind.Utc);
        }

        // Valid (incl. fall-back ambiguous) times: convert through the injected zone. ConvertTimeToUtc resolves
        // ambiguous times to standard time — identical to the prior ToUniversalTime() — so no special-casing.
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, localZone);
    }
}
