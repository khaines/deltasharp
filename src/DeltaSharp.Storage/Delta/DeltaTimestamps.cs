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
/// resolution by the machine's timezone offset.</para>
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
    /// <see cref="TimeZoneInfo.Local"/>.</summary>
    internal static long ToEpochMillis(DateTime lastModified, TimeZoneInfo localZone)
    {
        DateTime utc = lastModified.Kind switch
        {
            // Convert (never reinterpret) a Local-kind instant to UTC via the injected zone. SpecifyKind to
            // Unspecified first: ConvertTimeToUtc throws for a Local-kind DateTime paired with a non-Local zone.
            DateTimeKind.Local => TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(lastModified, DateTimeKind.Unspecified), localZone),
            _ => DateTime.SpecifyKind(lastModified, DateTimeKind.Utc),
        };

        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }
}
