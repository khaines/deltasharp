using Apache.Arrow;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Columnar.Arrow;
using DeltaSharp.Types;
using Xunit;
using ArrowTypes = Apache.Arrow.Types;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// timestamp_ntz Arrow-interop coverage (#558): a timezone-less wall-clock column exports to an Arrow
/// micros timestamp with a NULL zone and imports back as <see cref="TimestampNtzType"/> with the stored
/// wall-clock long preserved exactly — never collapsing to the UTC-instant <see cref="TimestampType"/>
/// (which exports a "UTC" zone). The two families share the epoch-microsecond long lane and are
/// distinguished purely by the Arrow zone marker.
/// </summary>
public class ArrowTimestampNtzRoundTripTests
{
    [Fact]
    public void RoundTrip_TimestampNtz_PreservesWallClockLong_AndStaysTimezoneLess()
    {
        const int rows = 3;
        var schema = new StructType(new[] { new StructField("n", TimestampNtzType.Instance) });
        MutableColumnVector n = ColumnVectors.Create(TimestampNtzType.Instance, rows);
        n.AppendValue(86_400_000_500L); // 1970-01-02 00:00:00.000500 wall-clock
        n.AppendNull();
        n.AppendValue(-500L);           // just before the epoch wall-clock
        var original = new ManagedColumnBatch(schema, new ColumnVector[] { n }, rows);

        using RecordBatch arrow = ArrowBatchConverter.ToArrow(original);

        // Export: a micros timestamp with a NULL/empty zone (the timezone-less marker), NOT "UTC".
        var arrowType = Assert.IsType<ArrowTypes.TimestampType>(arrow.Schema.GetFieldByName("n").DataType);
        Assert.Equal(ArrowTypes.TimeUnit.Microsecond, arrowType.Unit);
        Assert.True(string.IsNullOrEmpty(arrowType.Timezone));

        using ArrowColumnBatch back = ArrowBatchConverter.FromArrow(arrow);

        // Import: still timestamp_ntz (NOT timestamp), wall-clock long preserved bit-for-bit.
        Assert.Equal(TimestampNtzType.Instance, back.Schema[0].DataType);
        ColumnVector col = back.Column(0);
        Assert.Equal(86_400_000_500L, col.GetValue<long>(0));
        Assert.True(col.IsNull(1));
        Assert.Equal(-500L, col.GetValue<long>(2));
    }

    [Fact]
    public void Export_TimestampVsTimestampNtz_UseUtcVersusNullZone()
    {
        // Same epoch-micros lane, different zone marker: timestamp -> "UTC" (a UTC instant),
        // timestamp_ntz -> null (a timezone-less wall-clock).
        var schema = new StructType(new[]
        {
            new StructField("ts", TimestampType.Instance),
            new StructField("tsn", TimestampNtzType.Instance),
        });
        MutableColumnVector ts = ColumnVectors.Create(TimestampType.Instance, 1);
        ts.AppendValue(123_000_000L);
        MutableColumnVector tsn = ColumnVectors.Create(TimestampNtzType.Instance, 1);
        tsn.AppendValue(123_000_000L);
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { ts, tsn }, 1);

        using RecordBatch arrow = ArrowBatchConverter.ToArrow(batch);

        var tsType = Assert.IsType<ArrowTypes.TimestampType>(arrow.Schema.GetFieldByName("ts").DataType);
        var tsnType = Assert.IsType<ArrowTypes.TimestampType>(arrow.Schema.GetFieldByName("tsn").DataType);
        Assert.Equal("UTC", tsType.Timezone);
        Assert.True(string.IsNullOrEmpty(tsnType.Timezone));
    }

    [Fact]
    public void Import_ArrowMicrosTimestamp_WithEmptyZone_MapsToTimestampNtz()
    {
        // An Arrow micros timestamp whose zone is the empty string (not null) is still timezone-less and
        // must map to timestamp_ntz, mirroring the null-zone case.
        var arrowType = new ArrowTypes.TimestampType(ArrowTypes.TimeUnit.Microsecond, string.Empty);
        Assert.Equal(TimestampNtzType.Instance, ArrowSchemaMapper.ToDeltaType(arrowType));

        var utcType = new ArrowTypes.TimestampType(ArrowTypes.TimeUnit.Microsecond, "UTC");
        Assert.Equal(TimestampType.Instance, ArrowSchemaMapper.ToDeltaType(utcType));
    }
}
