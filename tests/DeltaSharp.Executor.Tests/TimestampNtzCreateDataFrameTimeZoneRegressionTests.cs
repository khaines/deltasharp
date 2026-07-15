using System;
using System.Collections.Generic;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Timezone-regression guard for the createDataFrame timestamp_ntz encode path (#558), isolated in a
/// non-parallel collection because it mutates process-wide <see cref="TimeZoneInfo"/> state. A
/// <c>Local &#8594; UTC</c> shift wrongly applied to timestamp_ntz is only observable under a NON-UTC
/// local zone, so a host-independent assertion is a false oracle on a UTC CI runner. This forces
/// <c>America/Los_Angeles</c> for its duration and asserts the wall-clock is stored UNSHIFTED, while a
/// sibling TimestampType column in the same row IS shifted (proving the guard genuinely bites).
/// </summary>
[Collection("ExecutorProcessTimeZoneMutation")]
public sealed class TimestampNtzCreateDataFrameTimeZoneRegressionTests
{
    private static long EpochMicros(DateTime value) =>
        (value.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;

    [Fact]
    public void CreateDataFrameEncode_UnderForcedNonUtcTimeZone_StoresNtzWallClockUnshifted_ButShiftsTimestamp()
    {
        string? original = Environment.GetEnvironmentVariable("TZ");
        try
        {
            Environment.SetEnvironmentVariable("TZ", "America/Los_Angeles");
            TimeZoneInfo.ClearCachedData();
            if (TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.Zero)
            {
                // Platform did not honor the TZ override (e.g. Windows / missing tz database). The regression
                // this guards is only observable under a non-UTC local zone; skip rather than assert a false
                // pass. The Linux CI runner honors TZ, so the guard is effective there.
                return;
            }

            // The SAME Local wall-clock is authored into both a timestamp (LTZ) and a timestamp_ntz column.
            var localWall = new DateTime(2021, 1, 1, 12, 30, 15, DateTimeKind.Local);
            long rawMicros = EpochMicros(localWall);                        // timestamp_ntz: stored as-is
            long shiftedMicros = EpochMicros(localWall.ToUniversalTime());  // timestamp (LTZ): Local -> UTC

            // The forced zone genuinely shifts, so the contrast below is a real oracle (not a UTC no-op).
            Assert.NotEqual(rawMicros, shiftedMicros);

            StructType schema = new(new[]
            {
                new StructField("ts", TimestampType.Instance, nullable: false),
                new StructField("tsn", TimestampNtzType.Instance, nullable: false),
            });
            var rows = new[] { new Row(schema, localWall, localWall) };

            IReadOnlyList<Row> materialized = RowMaterializer.Materialize(
                new BatchResult(schema, LocalRelationBatches.Build(schema, rows)),
                maxRows: null, maxBytes: null, default);

            Row row = Assert.Single(materialized);
            DateTime ts = row.GetAs<DateTime>("ts");
            DateTime tsn = row.GetAs<DateTime>("tsn");

            // timestamp_ntz: UNSHIFTED wall-clock (a Local->UTC bug would store shiftedMicros and fail here).
            Assert.Equal(rawMicros, EpochMicros(tsn));
            Assert.Equal(DateTimeKind.Unspecified, tsn.Kind);

            // timestamp (LTZ): the Local->UTC shift is still applied.
            Assert.Equal(shiftedMicros, EpochMicros(ts));
            Assert.Equal(DateTimeKind.Utc, ts.Kind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", original);
            TimeZoneInfo.ClearCachedData();
        }
    }
}

/// <summary>Serializes tests that mutate process-wide <see cref="TimeZoneInfo"/> state.</summary>
[CollectionDefinition("ExecutorProcessTimeZoneMutation", DisableParallelization = true)]
public sealed class ExecutorProcessTimeZoneMutationCollection
{
}
