using System;
using System.Collections.Generic;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// STORY-04.1.2 (#158) negative-path coverage for <see cref="LocalRelationBatches"/> — the row→batch
/// encoder behind <c>CreateDataFrame</c>. Each malformed input must fail with a deterministic,
/// field-named <see cref="UnsupportedPlanException"/> (never a raw framework throw), and the exact
/// message contract is pinned here. It also pins the two documented <b>deviations</b> whose behavior is
/// intentional (Security + Architect C7): a null in a non-nullable field is silently encoded as SQL
/// NULL (Spark's nullability is advisory), and CLR types must match exactly (no silent widening).
/// </summary>
public sealed class LocalRelationBatchesTests
{
    private static StructType Schema(params StructField[] fields) => new(fields);

    [Fact]
    public void NullRow_Throws_UnsupportedPlanException_NamingTheContract()
    {
        StructType schema = Schema(new StructField("id", IntegerType.Instance, nullable: false));

        UnsupportedPlanException ex = Assert.Throws<UnsupportedPlanException>(
            () => LocalRelationBatches.Build(schema, new Row?[] { null }!));

        Assert.Equal(
            "A LocalRelation row is null; every row supplied to CreateDataFrame must be a Row.",
            ex.Message);
    }

    [Fact]
    public void RowArityMismatch_Throws_NamingCounts()
    {
        StructType schema = Schema(
            new StructField("a", IntegerType.Instance, nullable: false),
            new StructField("b", IntegerType.Instance, nullable: false));
        // A row built against a 1-field schema, supplied where the authoritative schema declares two.
        StructType rowSchema = Schema(new StructField("a", IntegerType.Instance, nullable: false));
        var rows = new[] { new Row(rowSchema, 1) };

        UnsupportedPlanException ex = Assert.Throws<UnsupportedPlanException>(
            () => LocalRelationBatches.Build(schema, rows));

        Assert.Equal(
            "A LocalRelation row has 1 value(s) but the schema declares 2 column(s); every row must "
            + "match the schema arity.",
            ex.Message);
    }

    [Fact]
    public void DeclaredTypeVsRowValueClrMismatch_Throws_NamingFieldAndTypes()
    {
        StructType schema = Schema(new StructField("l", LongType.Instance, nullable: false));
        var rows = new[] { new Row(schema, 1) }; // int supplied for a long lane (no silent widening)

        UnsupportedPlanException ex = Assert.Throws<UnsupportedPlanException>(
            () => LocalRelationBatches.Build(schema, rows));

        Assert.Equal(
            "Column 'l' is 'bigint', which expects a Int64 value, but a row supplied a Int32.",
            ex.Message);
    }

    [Fact]
    public void DecimalScaleBeyondSystemDecimal_Throws_NamingScaleLimit()
    {
        StructType schema = Schema(new StructField("d", new DecimalType(30, 29), nullable: false));
        var rows = new[] { new Row(schema, 1m) };

        UnsupportedPlanException ex = Assert.Throws<UnsupportedPlanException>(
            () => LocalRelationBatches.Build(schema, rows));

        Assert.Equal(
            "Column 'd' is 'decimal(30,29)': scale 29 exceeds the System.Decimal maximum of 28, so a "
            + "decimal value cannot be encoded.",
            ex.Message);
    }

    [Fact]
    public void DecimalValueExceedingPrecision_Throws_NamingPrecision()
    {
        StructType schema = Schema(new StructField("d", new DecimalType(5, 2), nullable: false));
        var rows = new[] { new Row(schema, 10000m) }; // 10000.00 needs 7 digits, precision is 5

        UnsupportedPlanException ex = Assert.Throws<UnsupportedPlanException>(
            () => LocalRelationBatches.Build(schema, rows));

        Assert.Equal(
            "Decimal value 10000 for column 'd' does not fit in precision 5 (type 'decimal(5,2)').",
            ex.Message);
    }

    [Fact]
    public void DecimalValueExceedingScalePrecision_Throws_NamingLossOfPrecision()
    {
        StructType schema = Schema(new StructField("d", new DecimalType(10, 2), nullable: false));
        var rows = new[] { new Row(schema, 1.234m) }; // three fractional digits, scale is 2

        UnsupportedPlanException ex = Assert.Throws<UnsupportedPlanException>(
            () => LocalRelationBatches.Build(schema, rows));

        Assert.Equal(
            "Decimal value 1.234 for column 'd' cannot be represented at scale 2 without loss of precision.",
            ex.Message);
    }

    [Fact]
    public void NullInNonNullableField_IsSilentlyEncodedAsNull_DocumentedDeviation()
    {
        // DEVIATION (Spark nullability is advisory): a null cell in a non-nullable field does NOT throw;
        // it is encoded as SQL NULL. Pinned so a future nullability-enforcement change is a conscious one.
        StructType schema = Schema(new StructField("id", IntegerType.Instance, nullable: false));
        var rows = new[] { new Row(schema, new object?[] { null }) };

        IReadOnlyList<Row> materialized = RowMaterializer.Materialize(
            new BatchResult(schema, LocalRelationBatches.Build(schema, rows)), maxRows: null, maxBytes: null, default);

        Row row = Assert.Single(materialized);
        Assert.True(row.IsNullAt(0));
    }

    [Fact]
    public void TimestampNtz_EncodesWallClockAsIs_AndRoundTripsUnshifted()
    {
        // #558: the createDataFrame encode path (EncodeTimestampNtz) stores a timestamp_ntz DateTime as its
        // wall-clock value with NO Local->UTC shift, and it round-trips (via RowMaterializer.ReadTimestampNtz)
        // as the SAME wall-clock, tagged Unspecified. This is host-independent (timestamp_ntz never touches a
        // time zone); the forced-TZ contrast against TimestampType lives in the serialized regression test.
        StructType schema = Schema(new StructField("n", TimestampNtzType.Instance, nullable: false));
        var wall = new DateTime(2024, 6, 15, 13, 30, 45, DateTimeKind.Unspecified);
        var rows = new[] { new Row(schema, wall) };

        IReadOnlyList<Row> materialized = RowMaterializer.Materialize(
            new BatchResult(schema, LocalRelationBatches.Build(schema, rows)), maxRows: null, maxBytes: null, default);

        Row row = Assert.Single(materialized);
        DateTime got = row.GetAs<DateTime>(0);
        Assert.Equal(wall, got);
        Assert.Equal(DateTimeKind.Unspecified, got.Kind);
    }

    [Fact]
    public void StructColumn_RoundTrips_NestedRow_NullStruct_AndNullField()
    {
        // #608: a struct column materializes (row→StructColumnVector) and round-trips back to a nested Row.
        StructType inner = Schema(
            new StructField("a", IntegerType.Instance, nullable: false),
            new StructField("b", StringType.Instance, nullable: true));
        StructType schema = Schema(new StructField("s", inner, nullable: true));
        var rows = new[]
        {
            new Row(schema, new object?[] { new Row(inner, 1, "x") }),
            new Row(schema, new object?[] { null }),                        // null struct
            new Row(schema, new object?[] { new Row(inner, new object?[] { 2, null }) }), // null field
        };

        IReadOnlyList<Row> got = RoundTrip(schema, rows);

        Assert.Equal(3, got.Count);
        Row s0 = Assert.IsType<Row>(got[0][0]);
        Assert.Equal(1, s0.GetAs<int>("a"));
        Assert.Equal("x", s0.GetAs<string>("b"));
        Assert.True(got[1].IsNullAt(0));                                    // null struct, not a struct of nulls
        Row s2 = Assert.IsType<Row>(got[2][0]);
        Assert.Equal(2, s2.GetAs<int>("a"));
        Assert.True(s2.IsNullAt(1));                                        // null field inside a non-null struct
    }

    [Fact]
    public void ArrayColumn_RoundTrips_NonNull_Empty_Null_AndNullElements()
    {
        // #608: an array column round-trips; a NULL array is distinct from an EMPTY array (Spark semantics).
        StructType schema = Schema(
            new StructField("xs", new ArrayType(IntegerType.Instance, containsNull: true), nullable: true));
        var rows = new[]
        {
            new Row(schema, new object?[] { new object?[] { 1, 2, 3 } }),
            new Row(schema, new object?[] { Array.Empty<object?>() }),       // empty (non-null)
            new Row(schema, new object?[] { null }),                          // null array
            new Row(schema, new object?[] { new object?[] { 4, null, 6 } }),  // null element
        };

        IReadOnlyList<Row> got = RoundTrip(schema, rows);

        Assert.Equal(new object?[] { 1, 2, 3 }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(got[0][0]));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object?>>(got[1][0]));
        Assert.True(got[2].IsNullAt(0));                                     // null array distinct from empty
        Assert.Equal(new object?[] { 4, null, 6 }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(got[3][0]));
    }

    [Fact]
    public void MapColumn_RoundTrips_NonNull_Empty_Null_AndNullValues()
    {
        // #608: a map column round-trips; a NULL map is distinct from an EMPTY map, and a null VALUE is kept.
        StructType schema = Schema(
            new StructField("m", new MapType(StringType.Instance, IntegerType.Instance, valueContainsNull: true), nullable: true));
        var rows = new[]
        {
            new Row(schema, new object?[] { new Dictionary<object, object?> { ["a"] = 1, ["b"] = 2 } }),
            new Row(schema, new object?[] { new Dictionary<object, object?>() }),  // empty (non-null)
            new Row(schema, new object?[] { null }),                               // null map
            new Row(schema, new object?[] { new Dictionary<object, object?> { ["x"] = null } }),
        };

        IReadOnlyList<Row> got = RoundTrip(schema, rows);

        var d0 = Assert.IsAssignableFrom<IReadOnlyDictionary<object, object?>>(got[0][0]);
        Assert.Equal(2, d0.Count);
        Assert.Equal(1, d0["a"]);
        Assert.Equal(2, d0["b"]);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyDictionary<object, object?>>(got[1][0]));
        Assert.True(got[2].IsNullAt(0));                                     // null map distinct from empty
        var d3 = Assert.IsAssignableFrom<IReadOnlyDictionary<object, object?>>(got[3][0]);
        Assert.Null(d3["x"]);
    }

    [Fact]
    public void ArrayOfStruct_RoundTrips_RecursingNesting()
    {
        // #608: nesting composes — an array<struct> materializes and round-trips each element struct.
        StructType elem = Schema(new StructField("f", IntegerType.Instance, nullable: false));
        StructType schema = Schema(new StructField("xs", new ArrayType(elem), nullable: true));
        var rows = new[]
        {
            new Row(schema, new object?[] { new object?[] { new Row(elem, 1), new Row(elem, 2) } }),
        };

        IReadOnlyList<Row> got = RoundTrip(schema, rows);

        var a = Assert.IsAssignableFrom<IReadOnlyList<object?>>(got[0][0]);
        Assert.Equal(2, a.Count);
        Assert.Equal(1, Assert.IsType<Row>(a[0]).GetAs<int>("f"));
        Assert.Equal(2, Assert.IsType<Row>(a[1]).GetAs<int>("f"));
    }

    [Fact]
    public void StructColumn_NonRowValue_Throws_NamingTheExpectedType()
    {
        StructType inner = Schema(new StructField("a", IntegerType.Instance, nullable: false));
        StructType schema = Schema(new StructField("s", inner, nullable: true));
        var rows = new[] { new Row(schema, new object?[] { "not a row" }) };

        UnsupportedPlanException ex = Assert.Throws<UnsupportedPlanException>(
            () => LocalRelationBatches.Build(schema, rows));
        Assert.Contains("Column 's'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("expects a DeltaSharp.Row", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StructColumn_NestedRowArityMismatch_Throws()
    {
        StructType inner = Schema(
            new StructField("a", IntegerType.Instance, nullable: false),
            new StructField("b", IntegerType.Instance, nullable: false));
        StructType wrong = Schema(new StructField("a", IntegerType.Instance, nullable: false));
        StructType schema = Schema(new StructField("s", inner, nullable: true));
        var rows = new[] { new Row(schema, new object?[] { new Row(wrong, 1) }) };

        UnsupportedPlanException ex = Assert.Throws<UnsupportedPlanException>(
            () => LocalRelationBatches.Build(schema, rows));
        Assert.Contains("expects a Row with 2 field(s)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayColumn_StringValue_Throws_NotTreatedAsCharSequence()
    {
        // A string is a scalar StringType value, never an element sequence; accepting it here would silently
        // mis-encode a mistyped value, so it is rejected fail-closed.
        StructType schema = Schema(new StructField("xs", new ArrayType(IntegerType.Instance), nullable: true));
        var rows = new[] { new Row(schema, new object?[] { "abc" }) };

        UnsupportedPlanException ex = Assert.Throws<UnsupportedPlanException>(
            () => LocalRelationBatches.Build(schema, rows));
        Assert.Contains("non-string System.Collections.IEnumerable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapColumn_NonDictionaryValue_Throws_NamingTheExpectedType()
    {
        StructType schema = Schema(
            new StructField("m", new MapType(StringType.Instance, IntegerType.Instance), nullable: true));
        var rows = new[] { new Row(schema, new object?[] { new object?[] { 1 } }) };

        UnsupportedPlanException ex = Assert.Throws<UnsupportedPlanException>(
            () => LocalRelationBatches.Build(schema, rows));
        Assert.Contains("expects a System.Collections.IDictionary", ex.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<Row> RoundTrip(StructType schema, params Row[] rows) =>
        RowMaterializer.Materialize(
            new BatchResult(schema, LocalRelationBatches.Build(schema, rows)), maxRows: null, maxBytes: null, default);
}
