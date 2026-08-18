using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Parquet;
using Parquet.Schema;
using Xunit;
using Xunit.Abstractions;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Cross-engine schema correctness: (1) a DeltaSharp-written file's <b>physical/logical</b> Parquet
/// annotations (INT8/INT16 signedness, DATE, micros+isAdjustedToUTC TIMESTAMP, DECIMAL precision/scale)
/// are asserted via a Parquet.Net readback — so a wrong physical/annotation choice fails even though
/// our own self-readback would pass; and (2) a structurally valid file whose column type or nullability
/// disagrees with the requested engine type fails with a distinct <see cref="StorageErrorKind.SchemaMismatch"/>
/// (M2), not a generic "malformed" error.
/// </summary>
public sealed class ParquetSchemaMappingTests
{
    private readonly SeededRandom _random;

    public ParquetSchemaMappingTests(ITestOutputHelper output)
    {
        _random = SeededRandom.Create(output);
    }

    private static readonly StructType AllTypes = new(new[]
    {
        new StructField("bool", DataTypes.BooleanType, nullable: false),
        new StructField("byte", DataTypes.ByteType, nullable: false),
        new StructField("short", DataTypes.ShortType, nullable: false),
        new StructField("int", DataTypes.IntegerType, nullable: false),
        new StructField("long", DataTypes.LongType, nullable: false),
        new StructField("float", DataTypes.FloatType, nullable: false),
        new StructField("double", DataTypes.DoubleType, nullable: false),
        new StructField("string", DataTypes.StringType, nullable: false),
        new StructField("binary", DataTypes.BinaryType, nullable: false),
        new StructField("date", DataTypes.DateType, nullable: false),
        new StructField("ts", DataTypes.TimestampType, nullable: false),
        new StructField("dec_compact", DataTypes.CreateDecimalType(10, 2), nullable: false),
        new StructField("dec_wide", DataTypes.CreateDecimalType(24, 4), nullable: false),
    });

    [Fact]
    public async Task WrittenSchema_MatchesSparkPhysicalAndLogicalTypes()
    {
        ColumnBatch batch = TestData.RandomBatch(AllTypes, rowCount: 4, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(AllTypes, new[] { batch });

        using var stream = new MemoryStream(file, writable: false);
        ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        await using (reader.ConfigureAwait(false))
        {
            DataField Field(string name) => Array.Find(reader.Schema.DataFields, f => f.Name == name)!;

            Assert.Equal(typeof(bool), Field("bool").ClrType);
            Assert.Equal(typeof(sbyte), Field("byte").ClrType);   // signed INT8 (Spark tinyint).
            Assert.Equal(typeof(short), Field("short").ClrType);  // signed INT16 (Spark smallint).
            Assert.Equal(typeof(int), Field("int").ClrType);
            Assert.Equal(typeof(long), Field("long").ClrType);
            Assert.Equal(typeof(float), Field("float").ClrType);
            Assert.Equal(typeof(double), Field("double").ClrType);
            Assert.Equal(typeof(ReadOnlyMemory<char>), Field("string").ClrType);
            Assert.Equal(typeof(ReadOnlyMemory<byte>), Field("binary").ClrType);

            var date = Assert.IsType<DateTimeDataField>(Field("date"));
            Assert.Equal(DateTimeFormat.Date, date.DateTimeFormat);

            var ts = Assert.IsType<DateTimeDataField>(Field("ts"));
            Assert.Equal(DateTimeFormat.Timestamp, ts.DateTimeFormat);
            Assert.Equal(DateTimeTimeUnit.Micros, ts.Unit);
            Assert.True(ts.IsAdjustedToUTC);

            var compact = Assert.IsType<DecimalDataField>(Field("dec_compact"));
            Assert.Equal(10, compact.Precision);
            Assert.Equal(2, compact.Scale);

            var wide = Assert.IsType<DecimalDataField>(Field("dec_wide"));
            Assert.Equal(24, wide.Precision);
            Assert.Equal(4, wide.Scale);
        }
    }

    [Fact]
    public void StringAndBinaryFooterClrForms_MapToDeltaTypes()
    {
        // Both physical shapes a Parquet.Net footer can report for a UTF-8 / BYTE_ARRAY column must reconstruct
        // the SAME DeltaSharp logical type. Under the pinned Parquet.Net (≥6.1) `new DataField<string>(...)` is
        // already normalized to ReadOnlyMemory<char> (and `DataField<byte[]>` to ReadOnlyMemory<byte>), so the
        // end-to-end ToDataType assertions below CANNOT reach the legacy string/byte[] legs on their own — they
        // would stay green even if those legs were deleted. Pin the predicates DIRECTLY as well, so both legs
        // (and the non-membership of every other shape) are covered independently of the library's
        // normalization. Positive legs first, then negative pins so a widened predicate cannot pass.
        Assert.True(ParquetTypeMapping.IsStringPhysicalClrType(typeof(string)));
        Assert.True(ParquetTypeMapping.IsStringPhysicalClrType(typeof(ReadOnlyMemory<char>)));
        Assert.True(ParquetTypeMapping.IsBinaryPhysicalClrType(typeof(byte[])));
        Assert.True(ParquetTypeMapping.IsBinaryPhysicalClrType(typeof(ReadOnlyMemory<byte>)));

        Assert.False(ParquetTypeMapping.IsStringPhysicalClrType(typeof(byte[])));
        Assert.False(ParquetTypeMapping.IsStringPhysicalClrType(typeof(ReadOnlyMemory<byte>)));
        Assert.False(ParquetTypeMapping.IsStringPhysicalClrType(typeof(int)));
        Assert.False(ParquetTypeMapping.IsBinaryPhysicalClrType(typeof(string)));
        Assert.False(ParquetTypeMapping.IsBinaryPhysicalClrType(typeof(ReadOnlyMemory<char>)));
        Assert.False(ParquetTypeMapping.IsBinaryPhysicalClrType(typeof(int)));

        // …and the end-to-end mapping, which is what a real footer actually flows through. Both constructions
        // normalize to the memory form under 6.1, so these pin the mapping — the direct predicate assertions
        // above are what keep the legacy legs themselves covered.
        Assert.Equal(DataTypes.StringType, ParquetTypeMapping.ToDataType(new DataField<string>("legacy_string")));
        Assert.Equal(DataTypes.StringType, ParquetTypeMapping.ToDataType(new DataField<ReadOnlyMemory<char>>("memory_string")));
        Assert.Equal(DataTypes.BinaryType, ParquetTypeMapping.ToDataType(new DataField<byte[]>("legacy_binary")));
        Assert.Equal(DataTypes.BinaryType, ParquetTypeMapping.ToDataType(new DataField<ReadOnlyMemory<byte>>("memory_binary")));
    }

    [Fact]
    public void PhysicalClrTypesMatch_EquatesStringAndBinaryCrossForms_AndRejectsOthers()
    {
        // PhysicalClrTypesMatch treats a file shape and a requested shape as the same physical type when the
        // CLR types are equal OR when both are string shapes OR both are binary shapes. Under the pinned
        // Parquet.Net (≥6.1) EVERY DataField — including `new DataField<string>(...)` and even
        // `new DataField(name, typeof(string))` — is normalized to the memory form, so a cross-form PAIR
        // cannot be built from real fields at all and a bare `file == requested` reduction would be
        // indistinguishable from the full predicate. Since #832 the predicate takes the CLR TYPES directly
        // (which also lets the nested-leaf guard reuse it), so the cross-form pairs are stated outright, in
        // BOTH directions.
        Assert.True(ParquetTypeMapping.PhysicalClrTypesMatch(typeof(string), typeof(ReadOnlyMemory<char>)));
        Assert.True(ParquetTypeMapping.PhysicalClrTypesMatch(typeof(ReadOnlyMemory<char>), typeof(string)));
        Assert.True(ParquetTypeMapping.PhysicalClrTypesMatch(typeof(byte[]), typeof(ReadOnlyMemory<byte>)));
        Assert.True(ParquetTypeMapping.PhysicalClrTypesMatch(typeof(ReadOnlyMemory<byte>), typeof(byte[])));

        // A string shape and a binary shape are NEVER interchangeable, in either direction or either form.
        Assert.False(ParquetTypeMapping.PhysicalClrTypesMatch(typeof(string), typeof(byte[])));
        Assert.False(ParquetTypeMapping.PhysicalClrTypesMatch(typeof(byte[]), typeof(string)));
        Assert.False(ParquetTypeMapping.PhysicalClrTypesMatch(typeof(ReadOnlyMemory<char>), typeof(ReadOnlyMemory<byte>)));
        Assert.False(ParquetTypeMapping.PhysicalClrTypesMatch(typeof(ReadOnlyMemory<byte>), typeof(ReadOnlyMemory<char>)));

        // Unrelated shapes still fall back to exact CLR-type equality.
        Assert.False(ParquetTypeMapping.PhysicalClrTypesMatch(typeof(int), typeof(ReadOnlyMemory<char>)));
        Assert.False(ParquetTypeMapping.PhysicalClrTypesMatch(typeof(int), typeof(long)));
        Assert.True(ParquetTypeMapping.PhysicalClrTypesMatch(typeof(int), typeof(int)));
    }

    [Fact]
    public void DescribePhysicalClrType_RendersActionableParquetVocabulary()
    {
        // #832: Type.Name alone renders BOTH string and binary columns as the opaque `ReadOnlyMemory`1` under
        // Parquet.Net 6.1, which made the mismatch message self-contradictory ("file physical type
        // 'ReadOnlyMemory`1' … expected 'ReadOnlyMemory`1'"). Both shapes of each kind must collapse onto ONE
        // distinct, actionable Parquet token, and the two kinds must never render the same token.
        Assert.Equal("string (BYTE_ARRAY/UTF8)", ParquetTypeMapping.DescribePhysicalClrType(typeof(string)));
        Assert.Equal("string (BYTE_ARRAY/UTF8)", ParquetTypeMapping.DescribePhysicalClrType(typeof(ReadOnlyMemory<char>)));
        Assert.Equal("binary (BYTE_ARRAY)", ParquetTypeMapping.DescribePhysicalClrType(typeof(byte[])));
        Assert.Equal("binary (BYTE_ARRAY)", ParquetTypeMapping.DescribePhysicalClrType(typeof(ReadOnlyMemory<byte>)));
        Assert.NotEqual(
            ParquetTypeMapping.DescribePhysicalClrType(typeof(ReadOnlyMemory<char>)),
            ParquetTypeMapping.DescribePhysicalClrType(typeof(ReadOnlyMemory<byte>)));

        // Every other physical type keeps its (already unambiguous, already bounded) CLR type name.
        Assert.Equal("Int32", ParquetTypeMapping.DescribePhysicalClrType(typeof(int)));
        Assert.Equal("Byte", ParquetTypeMapping.DescribePhysicalClrType(typeof(byte)));
        Assert.Equal("DateTime", ParquetTypeMapping.DescribePhysicalClrType(typeof(DateTime)));

        // The rendered vocabulary is fixed and short, so it can never carry file-derived text into a message
        // nor leak the generic-arity backtick that made the old rendering unreadable.
        Assert.DoesNotContain('`', ParquetTypeMapping.DescribePhysicalClrType(typeof(ReadOnlyMemory<char>)));
        Assert.DoesNotContain('`', ParquetTypeMapping.DescribePhysicalClrType(typeof(ReadOnlyMemory<byte>)));
    }

    [Theory]
    [InlineData(TimeUnitPrecision.Millis)]  // TIME_MILLIS — physical INT32.
    [InlineData(TimeUnitPrecision.Micros)]  // TIME_MICROS — physical INT64.
    [InlineData(TimeUnitPrecision.Nanos)]   // TIME_NANOS  — physical INT64.
    public async Task TimeColumn_FailsClosed_AtEveryPrecision(TimeUnitPrecision precision)
    {
        // #832 regression: Parquet.Net ≥6.1 surfaces a TIME column as a TimeDataField whose ClrType is a RAW
        // int (millis) or long (micros/nanos) — NOT the TimeSpan that 6.0.3 reported. DeltaSharp has no
        // time-of-day logical type, so without the explicit TimeDataField arm in TryToDataType the raw-CLR
        // fallback would silently reinterpret sub-day time units as IntegerType/LongType — a SILENT data
        // corruption, not an error. Drive the REAL footer path (author the file, read its schema back) so the
        // guard is proven where a foreign file actually enters, at every precision.
        byte[] file = await ParquetTestHelpers.WriteTimeColumnAsync("t", precision);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileReader().ReadDataSchemaAsync(new MemoryStream(file), CancellationToken.None));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);

        // Same rejection through the direct mapping entry points, on the exact footer field the file yields —
        // so the guard is pinned independently of the reader plumbing that surfaces it.
        using var stream = new MemoryStream(file, writable: false);
        ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        await using (reader.ConfigureAwait(false))
        {
            DataField footerField = Assert.Single(reader.Schema.DataFields);
            Assert.IsType<TimeDataField>(footerField);
            Assert.False(ParquetTypeMapping.TryToDataType(footerField, out _));
            Assert.Throws<DeltaStorageException>(() => ParquetTypeMapping.ToDataSchema(reader.Schema));
        }

        // …and on a directly constructed TimeDataField, which needs no file at all.
        Assert.False(ParquetTypeMapping.TryToDataType(new TimeDataField("t", precision), out _));
    }

    [Fact]
    public async Task ReadWithNarrowingPhysicalType_ThrowsSchemaMismatch()
    {
        // File has a LONG column; requesting it as INT is a NARROWING physical-type disagreement (not a
        // sanctioned widening), so it stays fail-closed. (The reverse — an INT32 file read as LONG — is a
        // sanctioned type-widening promotion and is covered by the promotion tests, #495.)
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.LongType, nullable: false) });
        ColumnBatch batch = TestData.RandomBatch(writeSchema, rowCount: 4, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.IntegerType, nullable: false) });
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(file, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    [Fact]
    public async Task ReadTimestampAsDate_ThrowsSchemaMismatch()
    {
        // File has a DATE column; requesting it as TIMESTAMP shares ClrType but disagrees on annotation.
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.DateType, nullable: false) });
        ColumnBatch batch = TestData.RandomBatch(writeSchema, rowCount: 4, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.TimestampType, nullable: false) });
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(file, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    [Fact]
    public async Task ReadWithWrongDecimalScale_ThrowsSchemaMismatch()
    {
        var writeSchema = new StructType(new[]
        {
            new StructField("v", DataTypes.CreateDecimalType(10, 2), nullable: false),
        });
        ColumnBatch batch = TestData.RandomBatch(writeSchema, rowCount: 4, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[]
        {
            new StructField("v", DataTypes.CreateDecimalType(10, 4), nullable: false),
        });
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(file, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    [Fact]
    public async Task ReadNullableColumnAsRequired_ThrowsSchemaMismatch()
    {
        // A nullable INT32 column read as a required lane could inject a null: reject deterministically.
        var writeSchema = new StructType(new[] { new StructField("v", DataTypes.IntegerType, nullable: true) });
        ColumnBatch batch = TestData.RandomBatch(writeSchema, rowCount: 4, _random);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(writeSchema, new[] { batch });

        var readSchema = new StructType(new[] { new StructField("v", DataTypes.IntegerType, nullable: false) });
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(file, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }
}
