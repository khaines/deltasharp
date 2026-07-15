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
/// build supports timestamp_ntz as a <b>read-side</b> type (the Delta schema is authoritative for the logical
/// type; the INT64 epoch-micros are read into the ntz lane) and the <c>date → timestamp_ntz</c> read-promotion
/// (covered in <c>ParquetTypeWideningPromotionTests</c>). <b>Native timestamp_ntz writes are fail-closed</b>:
/// Parquet.Net 6.0.3 cannot persist the <c>TIMESTAMP(isAdjustedToUTC=false)</c> annotation (it emits a legacy
/// UTC <c>TIMESTAMP_MICROS</c> ConvertedType), so a written column would be a protocol-non-conformant LTZ file.
/// These tests pin the schema-JSON round-trip, the fail-closed write, the schema-authoritative read, and the
/// partition-value round-trip.
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
    public async Task NativeTimestampNtzWrite_IsFailClosed()
    {
        // Writing a native timestamp_ntz column is fail-closed (Parquet.Net cannot persist isAdjustedToUTC=false).
        var schema = new StructType(new[] { new StructField("v", DataTypes.TimestampNtzType, nullable: true) });
        ManagedColumnBatch batch = OneColumn(DataTypes.TimestampNtzType, c => { c.AppendValue(1_000_000L); }, 1);

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        Assert.Contains("timestamp_ntz", ex.Message, System.StringComparison.Ordinal);
        Assert.Contains("isAdjustedToUTC=false", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task MicrosFile_ReadAsTimestampNtz_IsSchemaAuthoritative()
    {
        // The Delta schema is authoritative for timestamp vs timestamp_ntz. A physical INT64-micros file (here
        // written via TimestampType, since native ntz write is fail-closed) requested under a timestamp_ntz
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
}
