using System.Globalization;
using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Functions;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Row-materialization tests (STORY-04.6.2 council): the type matrix (one nullable column per
/// <see cref="DataType"/>), decimal scale/overflow handling, Date/Timestamp CLR surfacing, selection
/// ordering, and all-null/empty edges. These pin the <see cref="RowMaterializer"/> contract that
/// <c>Collect()</c> returns exact <see cref="Row"/> values.
/// </summary>
public class MaterializationTests
{
    private static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    private static int EpochDay(DateOnly date) => date.DayNumber - UnixEpochDate.DayNumber;

    private static long EpochMicros(DateTime utc) =>
        (utc - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerMicrosecond;

    // Mirrors the invariant-culture rendering DataFrame.Show/Row.ToString apply via Row.Render
    // (internal to Core): an IFormattable value uses the invariant culture. A DateOnly/DateTime/decimal
    // therefore renders as a date/instant/scaled number rather than a raw epoch int/long.
    private static string RenderInvariant(object? value) =>
        value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "null";

    // ---- Type-materialization matrix (pins decimal scale + Date/Timestamp surfacing + nulls) ----

    [Fact]
    public void TypeMatrix_MaterializesEveryTypeAndNulls()
    {
        var fixture = new InMemoryRelationFixture();
        var decimalType = new DecimalType(5, 2);
        StructType schema = TestData.Schema(
            TestData.Field("b", BooleanType.Instance),
            TestData.Field("by", ByteType.Instance),
            TestData.Field("sh", ShortType.Instance),
            TestData.Field("i", IntegerType.Instance),
            TestData.Field("l", LongType.Instance),
            TestData.Field("f", FloatType.Instance),
            TestData.Field("d", DoubleType.Instance),
            TestData.Field("dec", decimalType),
            TestData.Field("s", StringType.Instance),
            TestData.Field("dt", DateType.Instance),
            TestData.Field("ts", TimestampType.Instance),
            TestData.Field("bin", BinaryType.Instance));

        var date = new DateOnly(2024, 1, 1);
        var timestamp = new DateTime(2024, 1, 1, 12, 30, 15, DateTimeKind.Utc);

        DataFrame df = fixture.Relation("matrix", schema, TestData.Batch(
            schema,
            TestData.Bools(true, null),
            TestData.Bytes(-7, null),
            TestData.Shorts(1234, null),
            TestData.Ints(42, null),
            TestData.Longs(9_000_000_000L, null),
            TestData.Floats(1.5f, null),
            TestData.Doubles(2.5, null),
            TestData.DecimalsCompact(decimalType, 12345, null), // 123.45
            TestData.Strings("héllo", null),
            TestData.Dates(EpochDay(date), null),
            TestData.Timestamps(EpochMicros(timestamp), null),
            TestData.Binaries(new byte[] { 1, 2, 3 }, null)));

        IReadOnlyList<Row> rows = fixture.Collect(df);
        Assert.Equal(2, rows.Count);

        Row row = rows[0];
        Assert.True(row.GetAs<bool>("b"));
        Assert.Equal((sbyte)-7, row.GetAs<sbyte>("by"));
        Assert.Equal((short)1234, row.GetAs<short>("sh"));
        Assert.Equal(42, row.GetAs<int>("i"));
        Assert.Equal(9_000_000_000L, row.GetAs<long>("l"));
        Assert.Equal(1.5f, row.GetAs<float>("f"));
        Assert.Equal(2.5, row.GetAs<double>("d"));
        Assert.Equal(123.45m, row.GetAs<decimal>("dec"));
        Assert.Equal("héllo", row.GetAs<string>("s"));
        Assert.Equal(date, row.GetAs<DateOnly>("dt"));
        Assert.Equal(timestamp, row.GetAs<DateTime>("ts"));
        Assert.Equal(new byte[] { 1, 2, 3 }, row.GetAs<byte[]>("bin"));

        Row nulls = rows[1];
        for (int c = 0; c < schema.Count; c++)
        {
            Assert.True(nulls.IsNullAt(c), $"column {schema[c].Name} should be null");
        }
    }

    // ---- MEDIUM 2: decimal scale preservation + overflow rejection ----

    [Fact]
    public void Decimal_5_2_RoundTripsWithScalePreserved()
    {
        var fixture = new InMemoryRelationFixture();
        var type = new DecimalType(5, 2);
        StructType schema = TestData.Schema(TestData.Field("amount", type, nullable: false));
        DataFrame df = fixture.Relation("dec52", schema, TestData.Batch(
            schema, TestData.DecimalsCompact(type, 10000))); // unscaled 10000 == 100.00

        Row row = Assert.Single(fixture.Collect(df));
        decimal value = row.GetAs<decimal>(0);

        Assert.Equal(100.00m, value);
        // Scale is preserved: rendered form keeps the two fractional digits (old code normalized to "100").
        Assert.Equal("100.00", value.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("100.00", RenderInvariant(value));
    }

    [Fact]
    public void Decimal_Wide_MaterializesCorrectly()
    {
        var fixture = new InMemoryRelationFixture();
        var type = new DecimalType(20, 4); // precision > 18 => Int128 lane
        StructType schema = TestData.Schema(TestData.Field("big", type, nullable: false));
        // unscaled 1234567890123456 with scale 4 == 123456789012.3456
        DataFrame df = fixture.Relation("decwide", schema, TestData.Batch(
            schema, TestData.DecimalsWide(type, (Int128)1234567890123456L)));

        Row row = Assert.Single(fixture.Collect(df));
        Assert.Equal(123456789012.3456m, row.GetAs<decimal>(0));
        Assert.Equal("123456789012.3456", RenderInvariant(row[0]));
    }

    [Fact]
    public void Decimal_ScaleAtLeast29_ThrowsDeterministicUnsupported()
    {
        var fixture = new InMemoryRelationFixture();
        var type = new DecimalType(30, 29); // scale 29 > System.Decimal max of 28
        StructType schema = TestData.Schema(TestData.Field("x", type, nullable: false));
        DataFrame df = fixture.Relation("decscale", schema, TestData.Batch(
            schema, TestData.DecimalsWide(type, (Int128)1)));

        var ex = Assert.Throws<UnsupportedPlanException>(() => fixture.Collect(df));
        Assert.Contains("scale", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decimal_MagnitudeWiderThan96Bits_ThrowsDeterministicUnsupported()
    {
        var fixture = new InMemoryRelationFixture();
        var type = new DecimalType(38, 0);
        StructType schema = TestData.Schema(TestData.Field("x", type, nullable: false));
        // 2^96 needs 97 bits of magnitude — representable at scale 0 as an integer but not as System.Decimal.
        DataFrame df = fixture.Relation("decmag", schema, TestData.Batch(
            schema, TestData.DecimalsWide(type, (Int128)1 << 96)));

        var ex = Assert.Throws<UnsupportedPlanException>(() => fixture.Collect(df));
        Assert.Contains("96-bit", ex.Message, StringComparison.Ordinal);
    }

    // ---- MEDIUM 3: Date/Timestamp surface as CLR temporal types that lit() round-trips ----

    [Fact]
    public void LitDate_RoundTripsAsDateOnly_AndRendersCalendarDate()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        var date = new DateOnly(2024, 1, 1);

        Row row = Assert.Single(fixture.Collect(people.Limit(1).Select(Lit(date).As("d"))));

        Assert.Equal(date, row.GetAs<DateOnly>(0));
        // Rendered as a calendar date, never the raw epoch-day int (old code surfaced 19723).
        Assert.Equal(19723, EpochDay(date));
        Assert.DoesNotContain("19723", RenderInvariant(row[0]));
        Assert.Contains("2024", RenderInvariant(row[0]));
    }

    [Fact]
    public void LitTimestamp_RoundTripsAsDateTime_AndRendersInstant()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        var timestamp = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Row row = Assert.Single(fixture.Collect(people.Limit(1).Select(Lit(timestamp).As("t"))));

        Assert.Equal(timestamp, row.GetAs<DateTime>(0));
        Assert.DoesNotContain(EpochMicros(timestamp).ToString(CultureInfo.InvariantCulture), RenderInvariant(row[0]));
        Assert.Contains("2024", RenderInvariant(row[0]));
    }

    // ---- MEDIUM 1: duplicate output names -> deterministic UnsupportedPlanException (not SchemaValidationException) ----

    [Fact]
    public void SelectSameColumnTwice_ThrowsDuplicateNameUnsupported()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        var ex = Assert.Throws<UnsupportedPlanException>(
            () => fixture.Collect(people.Select(Col("id"), Col("id"))));
        Assert.Contains("duplicate output column name 'id'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("#419", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EquiJoinWithSharedColumnName_ThrowsDuplicateNameUnsupported()
    {
        var fixture = new InMemoryRelationFixture();
        StructType leftSchema = TestData.Schema(
            TestData.Field("oid", IntegerType.Instance, nullable: false),
            TestData.Field("tag", StringType.Instance));
        StructType rightSchema = TestData.Schema(
            TestData.Field("cid", IntegerType.Instance, nullable: false),
            TestData.Field("tag", StringType.Instance));

        DataFrame left = fixture.Relation("dupl_left", leftSchema, TestData.Batch(
            leftSchema, TestData.Ints(1), TestData.Strings("a")));
        DataFrame right = fixture.Relation("dupl_right", rightSchema, TestData.Batch(
            rightSchema, TestData.Ints(1), TestData.Strings("b")));

        var ex = Assert.Throws<UnsupportedPlanException>(
            () => fixture.Plan(left.Join(right, Col("oid").EqualTo(Col("cid")))));
        Assert.Contains("duplicate output column name 'tag'", ex.Message, StringComparison.Ordinal);
    }

    // ---- Coverage: selection ordering, all-null, empty ----

    [Fact]
    public void FilterInducedSelection_MaterializesSelectedRowsInOrder()
    {
        var fixture = new InMemoryRelationFixture();
        StructType schema = TestData.Schema(TestData.Field("v", IntegerType.Instance, nullable: false));
        DataFrame df = fixture.Relation("sel", schema, TestData.Batch(
            schema, TestData.Ints(10, 20, 30, 40, 50)));

        // Keep the odd-tens (20, 40): exercises a selection vector left on the batch by the filter.
        IReadOnlyList<Row> rows = fixture.Collect(df.Filter(Col("v").EqualTo(20).Or(Col("v").EqualTo(40))));

        Assert.Equal(new[] { 20, 40 }, rows.Select(r => r.GetAs<int>(0)));
    }

    [Fact]
    public void AllNullColumn_MaterializesAllNulls()
    {
        var fixture = new InMemoryRelationFixture();
        StructType schema = TestData.Schema(TestData.Field("v", IntegerType.Instance));
        DataFrame df = fixture.Relation("allnull", schema, TestData.Batch(
            schema, TestData.Ints(null, null, null)));

        IReadOnlyList<Row> rows = fixture.Collect(df);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsNullAt(0)));
    }

    [Fact]
    public void EmptyResult_MaterializesNoRows()
    {
        var fixture = new InMemoryRelationFixture();
        StructType schema = TestData.Schema(TestData.Field("v", IntegerType.Instance, nullable: false));
        DataFrame df = fixture.Relation("empty", schema, TestData.Batch(
            schema, TestData.Ints(1, 2, 3)));

        IReadOnlyList<Row> rows = fixture.Collect(df.Filter(Col("v").Gt(100)));

        Assert.Empty(rows);
    }

    // ---- Coverage: unsupported EXPRESSION diagnostics (translator, not planner-join) ----

    [Fact]
    public void CaseWhenExpression_ThrowsDeterministicUnsupported()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        var ex = Assert.Throws<UnsupportedPlanException>(
            () => fixture.Plan(people.Select(When(Col("salary").Gt(150.0), 1).Otherwise(0).As("bucket"))));
        Assert.Contains("expression", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DistinctAggregate_ThrowsDeterministicUnsupported()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        var ex = Assert.Throws<UnsupportedPlanException>(
            () => fixture.Plan(people.GroupBy(Col("dept")).Agg(CountDistinct(Col("id")))));
        Assert.Contains("DISTINCT", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static StructType PeopleSchema => TestData.Schema(
        TestData.Field("id", IntegerType.Instance, nullable: false),
        TestData.Field("dept", StringType.Instance),
        TestData.Field("salary", DoubleType.Instance));

    private static (InMemoryRelationFixture Fixture, DataFrame People) NewPeople()
    {
        var fixture = new InMemoryRelationFixture();
        DataFrame people = fixture.Relation("people_mat", PeopleSchema, TestData.Batch(
            PeopleSchema,
            TestData.Ints(1, 2, 3, 4, 5),
            TestData.Strings("eng", "eng", "sales", "sales", "sales"),
            TestData.Doubles(100.0, 200.0, 300.0, 50.0, null)));
        return (fixture, people);
    }
}
