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
            new BatchResult(schema, LocalRelationBatches.Build(schema, rows)));

        Row row = Assert.Single(materialized);
        Assert.True(row.IsNullAt(0));
    }
}
