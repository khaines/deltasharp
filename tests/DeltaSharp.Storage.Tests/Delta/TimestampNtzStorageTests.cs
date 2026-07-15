using System.Collections.Generic;
using System.Linq;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Storage-layer correctness for the <see cref="TimestampNtzType"/> (timestamp without timezone, #533). This
/// build supports timestamp_ntz as a <b>native read+write</b> type: writes emit a conformant modern
/// <c>TIMESTAMP(isAdjustedToUTC=false, MICROS)</c> LogicalType (via Parquet.Net's <c>DateTimeFormat.Timestamp</c>);
/// the read is schema-authoritative (the Delta schema selects the lane) and Kind-agnostic (the stored INT64
/// epoch-micros are read raw, with no host-timezone shift); and <c>date → timestamp_ntz</c> is a sanctioned
/// widening — read-promoted (covered in <c>ParquetTypeWideningPromotionTests</c>) and applied on append.
/// These tests pin the schema-JSON round-trip, the conformant-annotation native write + value round-trip, the
/// schema-authoritative read, the Kind-agnostic (timezone-independent) decode, and the partition-value
/// round-trip.
/// </summary>
public sealed class TimestampNtzStorageTests
{
    private static ManagedColumnBatch OneColumn(DataType type, System.Action<MutableColumnVector> fill, int rowCount)
    {
        MutableColumnVector column = ColumnVectors.Create(type, capacity: rowCount);
        fill(column);
        var schema = new StructType(new[] { new StructField("v", type, nullable: true) });
        return new ManagedColumnBatch(schema, new ColumnVector[] { column }, rowCount);
    }

    [Fact]
    public void SchemaJson_TimestampNtz_RoundTrips()
    {
        // Parse side: the Delta type-name "timestamp_ntz" resolves to the NTZ singleton.
        Assert.Same(TimestampNtzType.Instance, SchemaJson.FromJson("\"timestamp_ntz\""));

        // Serialize side: the NTZ type writes back its exact type-name (distinct from "timestamp").
        Assert.Equal("\"timestamp_ntz\"", SchemaJson.ToJson(TimestampNtzType.Instance));
        Assert.NotEqual(SchemaJson.ToJson(TimestampType.Instance), SchemaJson.ToJson(TimestampNtzType.Instance));
    }

    [Fact]
    public async Task NativeTimestampNtz_WritesConformantAnnotation_AndRoundTrips()
    {
        // A native timestamp_ntz column writes a conformant modern LogicalType.TIMESTAMP{isAdjustedToUTC=false,
        // MICROS} (via DateTimeFormat.Timestamp), NOT the legacy UTC TIMESTAMP_MICROS ConvertedType — and its
        // INT64 micros (including a null) round-trip exactly.
        long?[] micros = { 0L, null, 1_609_459_200_123_456L, -5_000_000L };
        var schema = new StructType(new[] { new StructField("v", DataTypes.TimestampNtzType, nullable: true) });
        ManagedColumnBatch batch = OneColumn(DataTypes.TimestampNtzType, c =>
        {
            foreach (long? m in micros)
            {
                if (m is long l)
                {
                    c.AppendValue(l);
                }
                else
                {
                    c.AppendNull();
                }
            }
        }, micros.Length);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });

        using (var stream = new System.IO.MemoryStream(file, writable: false))
        {
            global::Parquet.ParquetReader reader = await global::Parquet.ParquetReader.CreateAsync(
                stream, null, false, System.Threading.CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                var f = Assert.IsType<global::Parquet.Schema.DateTimeDataField>(
                    System.Array.Find(reader.Schema.DataFields, x => x.Name == "v"));
                Assert.Equal(global::Parquet.Schema.DateTimeFormat.Timestamp, f.DateTimeFormat);
                Assert.Equal(global::Parquet.Schema.DateTimeTimeUnit.Micros, f.Unit);
                Assert.False(f.IsAdjustedToUTC, "timestamp_ntz must write isAdjustedToUTC=false");
            }
        }

        List<ColumnBatch> read = await ParquetTestHelpers.ReadAllAsync(file, schema);
        ColumnVector v = read.Single().Column(0);
        Assert.Equal(DataTypes.TimestampNtzType, v.Type);
        Assert.Equal(0L, v.GetValue<long>(0));
        Assert.True(v.IsNull(1), "the null cell must round-trip as null");
        Assert.Equal(1_609_459_200_123_456L, v.GetValue<long>(2));
        Assert.Equal(-5_000_000L, v.GetValue<long>(3));
    }

    [Fact]
    public async Task NativeTimestampNtzFile_ReadAsTimestampLtz_IsSchemaAuthoritativePassthrough()
    {
        // The read remains schema-authoritative: even though the annotation is now faithful, a native
        // timestamp_ntz file read under a TimestampType (LTZ) schema still returns the same stored micros —
        // the requested type selects the lane and the value is a pure long passthrough (no timezone shift),
        // matching the interop-robust design.
        var ntzSchema = new StructType(new[] { new StructField("v", DataTypes.TimestampNtzType, nullable: true) });
        ManagedColumnBatch batch = OneColumn(DataTypes.TimestampNtzType, c => { c.AppendValue(5L); c.AppendValue(6L); }, 2);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(ntzSchema, new[] { batch });

        var ltzSchema = new StructType(new[] { new StructField("v", DataTypes.TimestampType, nullable: true) });
        List<ColumnBatch> read = await ParquetTestHelpers.ReadAllAsync(file, ltzSchema);
        Assert.Equal(new[] { 5L, 6L }, read.Single().Column(0).GetValues<long>().ToArray());
    }

    [Fact]
    public async Task MicrosFile_ReadAsTimestampNtz_IsSchemaAuthoritative()
    {
        // The Delta schema is authoritative for timestamp vs timestamp_ntz. A physical INT64-micros file (here
        // written via TimestampType to exercise a same-physical micros file whose footer annotation differs
        // from the requested ntz type) requested under a timestamp_ntz
        // schema is read into the ntz lane and the stored micros round-trip exactly — the read path selects the
        // lane from the REQUESTED type, not the (unreliable) Parquet isAdjustedToUTC annotation. Read with the
        // promotion gate OPEN (allowTypeWideningPromotion: true), which is how production DeltaReadSource always
        // reads a typeWidening-enabled table: a same-physical micros→ntz pair must take the IDENTITY read, not
        // mis-route into promotion (#533 gate-asymmetry regression).
        long[] micros = { 0L, 1_609_459_200_000_000L, 1_609_459_200_123_456L, -5_000_000L };
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.TimestampType, nullable: true) });
        ManagedColumnBatch batch = OneColumn(DataTypes.TimestampType, c =>
        {
            foreach (long m in micros)
            {
                c.AppendValue(m);
            }
        }, micros.Length);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.TimestampNtzType, nullable: true) });
        List<ColumnBatch> read = await ParquetTestHelpers.ReadAllAsync(
            file, readSchema, keepRowGroup: null, allowTypeWideningPromotion: true);

        ColumnVector v = read.Single().Column(0);
        Assert.Equal(DataTypes.TimestampNtzType, v.Type);
        Assert.Equal(micros, v.GetValues<long>().ToArray());
    }

    [Fact]
    public async Task MixedDateAndMicrosFiles_ReadAsTimestampNtz_WithGateOpen()
    {
        // A table that widened date→timestamp_ntz has BOTH old DATE files (promoted on read) AND native
        // micros files (identity read). With the promotion gate open (production state), both must read
        // correctly into the ntz lane: the DATE file promotes to midnight-of-date micros, the micros file
        // reads its stored micros unchanged. Pins that the gate-asymmetry fix keeps them distinct (#533).
        const long microsPerDay = 86_400L * 1_000_000L;
        var readSchema = new StructType(new[] { new StructField("v", DataTypes.TimestampNtzType, nullable: true) });

        // Old DATE file (INT32 epoch-day) → promoted.
        var dateSchema = new StructType(new[] { new StructField("v", DataTypes.DateType, nullable: true) });
        byte[] dateFile = await ParquetTestHelpers.WriteToBytesAsync(
            dateSchema, new[] { OneColumn(DataTypes.DateType, c => { c.AppendValue(0); c.AppendValue(3); }, 2) });
        ColumnVector promoted = (await ParquetTestHelpers.ReadAllAsync(
            dateFile, readSchema, keepRowGroup: null, allowTypeWideningPromotion: true)).Single().Column(0);
        Assert.Equal(DataTypes.TimestampNtzType, promoted.Type);
        Assert.Equal(new[] { 0L, 3L * microsPerDay }, promoted.GetValues<long>().ToArray());

        // Native micros file (INT64) → identity read (NOT mis-routed into promotion).
        var microsSchema = new StructType(new[] { new StructField("v", DataTypes.TimestampType, nullable: true) });
        byte[] microsFile = await ParquetTestHelpers.WriteToBytesAsync(
            microsSchema, new[] { OneColumn(DataTypes.TimestampType, c => { c.AppendValue(42L); c.AppendValue(-7L); }, 2) });
        ColumnVector identity = (await ParquetTestHelpers.ReadAllAsync(
            microsFile, readSchema, keepRowGroup: null, allowTypeWideningPromotion: true)).Single().Column(0);
        Assert.Equal(DataTypes.TimestampNtzType, identity.Type);
        Assert.Equal(new[] { 42L, -7L }, identity.GetValues<long>().ToArray());
    }

    [Fact]
    public async Task DateFile_ReadAsTimestampNtz_WithoutTypeWidening_FailsClosed()
    {
        // A DATE file requested as timestamp_ntz is a WIDENING (date→timestamp_ntz), gated by the typeWidening
        // feature. Without the promotion gate it fails closed (never silently promoted). The promoted case is
        // covered by ParquetTypeWideningPromotionTests.Date_To_TimestampNtz.
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.DateType, nullable: true) });
        ManagedColumnBatch batch = OneColumn(DataTypes.DateType, c => { c.AppendValue(0); c.AppendValue(1); }, 2);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.TimestampNtzType, nullable: true) });
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(file, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Theory]
    [InlineData(1_609_459_200_000_000L)] // 2021-01-01 00:00:00 (midnight, whole seconds)
    [InlineData(1_609_459_215_123_456L)] // 2021-01-01 00:00:15.123456 (sub-second micros)
    [InlineData(0L)]                      // epoch
    public void PartitionValue_TimestampNtz_EncodeDecode_RoundTrips_NoTimezoneShift(long micros)
    {
        // Partition values are strings (not stored in the data file), so timestamp_ntz partition encoding is
        // NOT subject to the Parquet.Net write limitation. The wall-clock is preserved (no timezone shift).
        MutableColumnVector source = ColumnVectors.Create(DataTypes.TimestampNtzType, 1);
        source.AppendValue(micros);

        string? formatted = DeltaWriteEncoding.FormatPartitionValue(source, row: 0);
        Assert.NotNull(formatted);

        ColumnVector decoded = DeltaReadEncoding.BuildConstantColumn(DataTypes.TimestampNtzType, formatted, rowCount: 1);
        Assert.Equal(DataTypes.TimestampNtzType, decoded.Type);
        Assert.Equal(micros, decoded.GetValue<long>(0));
    }

    [Fact]
    public void TypeCoercion_DateAndTimestampNtz_CommonTypeIsTimestampNtz()
    {
        Assert.Equal(DataTypes.TimestampNtzType, TypeCoercion.FindTightestCommonType(DataTypes.DateType, DataTypes.TimestampNtzType));
        Assert.Equal(DataTypes.TimestampNtzType, TypeCoercion.FindTightestCommonType(DataTypes.TimestampNtzType, DataTypes.DateType));
    }

    [Fact]
    public void DateTimeToEpochMicros_ReadsRawTicks_KindAgnostic_NoTimezoneShift()
    {
        // Regression (#533/#557): the Parquet timestamp reader converts a decoded DateTime → epoch-micros by
        // reading its RAW ticks, regardless of DateTimeKind. Parquet.Net labels a timestamp_ntz decode
        // DateTimeKind.Local and a timestamp decode DateTimeKind.Utc, but that Kind is a SEMANTIC label, not a
        // conversion instruction — a ToUniversalTime() here would offset a timestamp_ntz value by the host time
        // zone (the +8h shift the prototype's naive format swap first produced on a PST host).
        long ticks = new System.DateTime(2021, 1, 1, 12, 30, 15).Ticks;
        long expected = (ticks - System.DateTime.UnixEpoch.Ticks) / System.TimeSpan.TicksPerMicrosecond;

        Assert.Equal(expected, ParquetTypeMapping.DateTimeToEpochMicros(new System.DateTime(ticks, System.DateTimeKind.Utc)));
        Assert.Equal(expected, ParquetTypeMapping.DateTimeToEpochMicros(new System.DateTime(ticks, System.DateTimeKind.Local)));
        Assert.Equal(expected, ParquetTypeMapping.DateTimeToEpochMicros(new System.DateTime(ticks, System.DateTimeKind.Unspecified)));
    }

    [Fact]
    public void TryToDataType_LegacyDateAndTimeMicrosField_MapsToTimestampType()
    {
        // Backward-compat: a legacy DeltaSharp file (written with the old DateTimeFormat.DateAndTimeMicros /
        // ConvertedType.TIMESTAMP_MICROS, which Parquet.Net reads back as isAdjustedToUTC=true) must map to
        // TimestampType (LTZ), NOT timestamp_ntz — so existing tables keep reading as timestamp.
        var legacy = new global::Parquet.Schema.DateTimeDataField(
            "v", global::Parquet.Schema.DateTimeFormat.DateAndTimeMicros, isAdjustedToUTC: true, isNullable: false);
        Assert.True(ParquetTypeMapping.TryToDataType(legacy, out DataType? mapped));
        Assert.Equal(DataTypes.TimestampType, mapped);

        // And a modern isAdjustedToUTC=false field maps to timestamp_ntz.
        var ntz = new global::Parquet.Schema.DateTimeDataField(
            "v", global::Parquet.Schema.DateTimeFormat.Timestamp, isAdjustedToUTC: false,
            unit: global::Parquet.Schema.DateTimeTimeUnit.Micros, isNullable: false);
        Assert.True(ParquetTypeMapping.TryToDataType(ntz, out DataType? mappedNtz));
        Assert.Equal(DataTypes.TimestampNtzType, mappedNtz);
    }
}

/// <summary>
/// Timezone-regression guard for the Kind-agnostic Parquet timestamp reader (#533/#557), isolated in a
/// non-parallel collection because it mutates process-wide <see cref="System.TimeZoneInfo"/> state.
/// </summary>
[Collection("ProcessTimeZoneMutation")]
public sealed class TimestampNtzTimeZoneRegressionTests
{
    [Fact]
    public void DateTimeToEpochMicros_UnderForcedNonUtcTimeZone_ReadsRawTicks_CatchesToUniversalTimeRegression()
    {
        // Parquet.Net hands a timestamp_ntz decode back as DateTimeKind.Local; the reader must read its RAW
        // ticks and NOT apply value.ToUniversalTime(). A ToUniversalTime() regression is only observable under
        // a NON-UTC local zone, so this test FORCES America/Los_Angeles for its duration (TZ env var +
        // TimeZoneInfo.ClearCachedData(), honored on the Linux CI runner) — making the guard effective even on
        // a UTC host (where the earlier "host-independent" assertion silently passed the bug). Runs in a
        // non-parallel collection so the transient process-wide zone change cannot perturb sibling tests.
        string? original = System.Environment.GetEnvironmentVariable("TZ");
        try
        {
            System.Environment.SetEnvironmentVariable("TZ", "America/Los_Angeles");
            System.TimeZoneInfo.ClearCachedData();
            if (System.TimeZoneInfo.Local.BaseUtcOffset == System.TimeSpan.Zero)
            {
                // Platform did not honor the TZ override (e.g. Windows / missing tz database). The regression
                // this guards is only observable under a non-UTC local zone; skip rather than assert a false
                // pass. The Linux CI runner honors TZ, so the guard is effective there.
                return;
            }

            var local = new System.DateTime(2021, 1, 1, 12, 30, 15, System.DateTimeKind.Local);
            long rawTicksMicros = (local.Ticks - System.DateTime.UnixEpoch.Ticks) / System.TimeSpan.TicksPerMicrosecond;
            long wouldBeShifted =
                (local.ToUniversalTime().Ticks - System.DateTime.UnixEpoch.Ticks) / System.TimeSpan.TicksPerMicrosecond;

            // The forced zone genuinely shifts, so the guard is meaningful, AND the reader returns raw ticks.
            Assert.NotEqual(rawTicksMicros, wouldBeShifted);
            Assert.Equal(rawTicksMicros, ParquetTypeMapping.DateTimeToEpochMicros(local));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("TZ", original);
            System.TimeZoneInfo.ClearCachedData();
        }
    }
}

/// <summary>Serializes tests that mutate process-wide <see cref="System.TimeZoneInfo"/> state.</summary>
[CollectionDefinition("ProcessTimeZoneMutation", DisableParallelization = true)]
public sealed class ProcessTimeZoneMutationCollection
{
}
