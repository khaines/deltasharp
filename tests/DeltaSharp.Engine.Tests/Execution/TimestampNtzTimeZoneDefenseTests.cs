using System;
using Apache.Arrow;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Columnar.Arrow;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;
using ArrowTypes = Apache.Arrow.Types;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// Defense-in-depth timezone guards for the timestamp_ntz lane paths that are structurally zone-free
/// today but had no tz-forced coverage (#558): the Arrow ntz round-trip and the
/// <c>timestamp → timestamp_ntz</c> cast. Both operate purely on the epoch-microsecond long, so a
/// Local→UTC shift wrongly introduced anywhere is ONLY observable under a non-UTC host — a
/// host-independent assertion is a false oracle on a UTC CI runner. Each test forces
/// <c>America/Los_Angeles</c> for its duration, asserts the wall-clock long is preserved UNSHIFTED, and
/// proves the forced zone genuinely shifts (so the contrast is a real oracle). Serialized via the
/// process-wide environment-sensitive collection because it mutates <see cref="TimeZoneInfo"/> state.
/// </summary>
[Collection(EnvironmentSensitiveTestCollection.Name)]
public sealed class TimestampNtzTimeZoneDefenseTests
{
    private static long EpochMicros(DateTime value) =>
        (value.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;

    /// <summary>Runs <paramref name="body"/> under a forced non-UTC host zone, skipping (not failing) when
    /// the platform does not honor the TZ override — the guard is only meaningful under a shifting zone.</summary>
    private static void UnderForcedNonUtcZone(Action<long, long> body)
    {
        string? original = Environment.GetEnvironmentVariable("TZ");
        try
        {
            Environment.SetEnvironmentVariable("TZ", "America/Los_Angeles");
            TimeZoneInfo.ClearCachedData();
            if (TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.Zero)
            {
                // Platform ignored TZ (e.g. Windows / missing tz database). The Local->UTC shift this guards
                // is only observable under a non-UTC zone; skip rather than assert a false pass. The Linux CI
                // runner honors TZ, so the guard is effective there.
                return;
            }

            // The same Local wall-clock, stored as-is vs. shifted to UTC. A timezone-less lane must keep the
            // RAW value; a bug applying value.ToUniversalTime() would store the shifted value instead.
            var localWall = new DateTime(2021, 1, 1, 12, 30, 15, DateTimeKind.Local);
            long rawMicros = EpochMicros(localWall);
            long shiftedMicros = EpochMicros(localWall.ToUniversalTime());
            Assert.NotEqual(rawMicros, shiftedMicros); // the forced zone genuinely shifts -> a real oracle

            body(rawMicros, shiftedMicros);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", original);
            TimeZoneInfo.ClearCachedData();
        }
    }

    [Fact]
    public void ArrowRoundTrip_TimestampNtz_UnderForcedNonUtcZone_PreservesWallClockLongUnshifted()
    {
        UnderForcedNonUtcZone((rawMicros, shiftedMicros) =>
        {
            var schema = new StructType(new[] { new StructField("n", TimestampNtzType.Instance) });
            MutableColumnVector n = ColumnVectors.Create(TimestampNtzType.Instance, 3);
            n.AppendValue(rawMicros);
            n.AppendNull();
            n.AppendValue(-500L);
            var original = new ManagedColumnBatch(schema, new ColumnVector[] { n }, 3);

            using RecordBatch arrow = ArrowBatchConverter.ToArrow(original);

            // Export stays timezone-less (null/empty zone), NOT "UTC" — a non-UTC host must not tag it.
            var arrowType = Assert.IsType<ArrowTypes.TimestampType>(arrow.Schema.GetFieldByName("n").DataType);
            Assert.True(string.IsNullOrEmpty(arrowType.Timezone));

            using ArrowColumnBatch back = ArrowBatchConverter.FromArrow(arrow);
            Assert.Equal(TimestampNtzType.Instance, back.Schema[0].DataType);
            ColumnVector col = back.Column(0);

            // The wall-clock long round-trips UNSHIFTED under the non-UTC host (never == shiftedMicros).
            Assert.Equal(rawMicros, col.GetValue<long>(0));
            Assert.NotEqual(shiftedMicros, col.GetValue<long>(0));
            Assert.True(col.IsNull(1));
            Assert.Equal(-500L, col.GetValue<long>(2));
        });
    }

    [Fact]
    public void Cast_TimestampToTimestampNtz_UnderForcedNonUtcZone_IsValueInvariant()
    {
        UnderForcedNonUtcZone((rawMicros, shiftedMicros) =>
        {
            var schema = new StructType(new[] { new StructField("t", TimestampType.Instance, nullable: true) });
            MutableColumnVector t = ColumnVectors.Create(TimestampType.Instance, 4);
            t.AppendValue(rawMicros);
            t.AppendValue(0L);
            t.AppendValue(-1_000_000L);
            t.AppendNull();
            var batch = new ManagedColumnBatch(schema, new ColumnVector[] { t }, 4);

            var cast = new CastExpression(
                new ColumnReference(0, TimestampType.Instance, true), TimestampNtzType.Instance, AnsiMode.Legacy);
            ExpressionEvaluator evaluator = ExpressionEvaluators.Build(
                cast, schema, "interpreted-vectorized", OperatorKind.Project);
            var ledger = new BatchEvaluationMemory(BoundedExecutionMemory.Unbounded);
            try
            {
                ColumnVector result = evaluator.Evaluate(batch, ledger, CancellationToken.None);

                // timestamp -> timestamp_ntz reinterprets the epoch-micros lane with NO shift, even under a
                // non-UTC host (TemporalValues.SessionZoneIsUtc): the stored long is identical, never shifted.
                Assert.Equal(TimestampNtzType.Instance, result.Type);
                Assert.Equal(rawMicros, result.GetValue<long>(0));
                Assert.NotEqual(shiftedMicros, result.GetValue<long>(0));
                Assert.Equal(0L, result.GetValue<long>(1));
                Assert.Equal(-1_000_000L, result.GetValue<long>(2));
                Assert.True(result.IsNull(3));
            }
            finally
            {
                ledger.Release();
            }
        });
    }
}
