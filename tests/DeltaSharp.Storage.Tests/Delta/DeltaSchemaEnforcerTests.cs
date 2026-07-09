using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Unit tests for <see cref="DeltaSchemaEnforcer.Reconcile"/> — the pure, deterministic schema
/// enforcement/evolution rule set (STORY-05.4.2). These exercise every acceptance criterion directly
/// against the rule engine (no log/commit machinery): AC1 rejects an incompatible write with a classified
/// <see cref="DeltaSchemaMismatchException"/>; AC2 merges an allowed additive change into a new schema; AC3
/// applies deterministic, total, case-sensitive rules to nested structs, arrays, maps, decimals, and
/// timestamps. End-to-end atomicity and the AC4 stale-schema conflict are covered by
/// <see cref="DeltaSchemaEvolutionWriterTests"/>.
/// </summary>
public sealed class DeltaSchemaEnforcerTests
{
    private static StructField Field(string name, DataType type, bool nullable) => new(name, type, nullable);

    private static StructType Schema(params StructField[] fields) => new(fields);

    // ---- AC1: reject-before-commit, classified by DeltaSchemaMismatchKind (mode = None) ----------------

    [Fact]
    public void Reconcile_IncompatibleType_IsRejected()
    {
        StructType table = Schema(Field("value", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.StringType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.None));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
        Assert.Equal("value", ex.Path);
    }

    [Fact]
    public void Reconcile_NarrowingType_IsRejectedAsIncompatible()
    {
        StructType table = Schema(Field("value", DataTypes.LongType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.IntegerType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
        Assert.Equal("value", ex.Path);
    }

    [Fact]
    public void Reconcile_MissingRequiredColumn_IsRejected()
    {
        StructType table = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("name", DataTypes.StringType, nullable: true));
        StructType write = Schema(Field("name", DataTypes.StringType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.MissingRequiredColumn, ex.Kind);
        Assert.Equal("id", ex.Path);
    }

    [Fact]
    public void Reconcile_MissingNullableColumn_IsAcceptedWithNoSchemaChange()
    {
        StructType table = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("name", DataTypes.StringType, nullable: true));
        StructType write = Schema(Field("id", DataTypes.LongType, nullable: false));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.None);

        Assert.Null(merged);
    }

    [Fact]
    public void Reconcile_NullabilityViolation_IsRejected()
    {
        StructType table = Schema(Field("id", DataTypes.LongType, nullable: false));
        StructType write = Schema(Field("id", DataTypes.LongType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.NullabilityViolation, ex.Kind);
        Assert.Equal("id", ex.Path);
    }

    [Fact]
    public void Reconcile_NewColumn_WithoutEvolution_IsRejected()
    {
        StructType table = Schema(Field("id", DataTypes.LongType, nullable: false));
        StructType write = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("extra", DataTypes.StringType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.None));

        Assert.Equal(DeltaSchemaMismatchKind.NewColumnNotAllowed, ex.Kind);
        Assert.Equal("extra", ex.Path);
    }

    [Fact]
    public void Reconcile_NewColumn_RequiresWidenTypesToBeInsufficient()
    {
        StructType table = Schema(Field("id", DataTypes.LongType, nullable: false));
        StructType write = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("extra", DataTypes.StringType, nullable: true));

        // WidenTypes alone does not permit new columns; only AddNewColumns does.
        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.WidenTypes));

        Assert.Equal(DeltaSchemaMismatchKind.NewColumnNotAllowed, ex.Kind);
    }

    [Fact]
    public void Reconcile_NewNonNullableColumn_WithEvolution_IsRejected()
    {
        StructType table = Schema(Field("id", DataTypes.LongType, nullable: false));
        StructType write = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("extra", DataTypes.StringType, nullable: false));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns));

        Assert.Equal(DeltaSchemaMismatchKind.NewColumnMustBeNullable, ex.Kind);
        Assert.Equal("extra", ex.Path);
    }

    [Fact]
    public void Reconcile_PermittedWidening_WithoutWidenTypes_IsRejectedDistinctly()
    {
        StructType table = Schema(Field("value", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.LongType, nullable: true));

        // AddNewColumns is enabled but WidenTypes is not: the widening is safe but not turned on, which is a
        // DISTINCT, actionable reason from IncompatibleType.
        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningNotEnabled, ex.Kind);
        Assert.Equal("value", ex.Path);
    }

    // ---- AC2: allowed additive evolution merges into a new schema --------------------------------------

    [Fact]
    public void Reconcile_AddNullableColumn_AppendsAfterExistingColumns()
    {
        StructType table = Schema(Field("id", DataTypes.LongType, nullable: false));
        StructType write = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("name", DataTypes.StringType, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns);

        Assert.NotNull(merged);
        Assert.Equal(2, merged!.Count);
        Assert.Equal("id", merged[0].Name);
        Assert.Equal("name", merged[1].Name);
        Assert.True(merged[1].Nullable);
    }

    [Theory]
    [MemberData(nameof(PermittedWidenings))]
    public void Reconcile_IntegralAndFloatWidening_IsAccepted(DataType from, DataType to)
    {
        StructType table = Schema(Field("value", from, nullable: true));
        StructType write = Schema(Field("value", to, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.WidenTypes);

        Assert.NotNull(merged);
        Assert.Equal(to, merged![0].DataType);
    }

    public static TheoryData<DataType, DataType> PermittedWidenings() => new()
    {
        { DataTypes.ByteType, DataTypes.ShortType },
        { DataTypes.ShortType, DataTypes.IntegerType },
        { DataTypes.IntegerType, DataTypes.LongType },
        { DataTypes.ByteType, DataTypes.LongType },
        { DataTypes.FloatType, DataTypes.DoubleType },
    };

    [Fact]
    public void Reconcile_DateToTimestampWidening_IsAccepted()
    {
        StructType table = Schema(Field("ts", DataTypes.DateType, nullable: true));
        StructType write = Schema(Field("ts", DataTypes.TimestampType, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.WidenTypes);

        Assert.NotNull(merged);
        Assert.IsType<TimestampType>(merged![0].DataType);
    }

    [Fact]
    public void Reconcile_TimestampToDateNarrowing_IsRejected()
    {
        StructType table = Schema(Field("ts", DataTypes.TimestampType, nullable: true));
        StructType write = Schema(Field("ts", DataTypes.DateType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Reconcile_WideningPreservesRequiredNullability()
    {
        StructType table = Schema(Field("value", DataTypes.IntegerType, nullable: false));
        StructType write = Schema(Field("value", DataTypes.LongType, nullable: false));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.WidenTypes);

        Assert.NotNull(merged);
        Assert.IsType<LongType>(merged![0].DataType);
        Assert.False(merged[0].Nullable);
    }

    [Fact]
    public void Reconcile_PreservesFieldMetadataOnWiden()
    {
        FieldMetadata metadata = FieldMetadata.FromEntries(new[]
        {
            new KeyValuePair<string, string>("comment", "the amount"),
        });
        StructType table = Schema(new StructField("value", DataTypes.IntegerType, nullable: true, metadata));
        StructType write = Schema(Field("value", DataTypes.LongType, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.WidenTypes);

        Assert.NotNull(merged);
        Assert.Equal("the amount", merged![0].Metadata["comment"]);
    }

    // ---- AC2/AC3: decimal precision/scale compatibility ------------------------------------------------

    [Theory]
    [InlineData(10, 2, 12, 2)] // precision grows, scale equal → integer range grows
    [InlineData(10, 2, 12, 4)] // both precision and scale grow, integer range unchanged
    [InlineData(10, 2, 13, 3)] // both grow
    public void Reconcile_DecimalWidening_IsAccepted(int fromP, int fromS, int toP, int toS)
    {
        StructType table = Schema(Field("amount", DataTypes.CreateDecimalType(fromP, fromS), nullable: true));
        StructType write = Schema(Field("amount", DataTypes.CreateDecimalType(toP, toS), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.WidenTypes);

        Assert.NotNull(merged);
        DecimalType widened = Assert.IsType<DecimalType>(merged![0].DataType);
        Assert.Equal(toP, widened.Precision);
        Assert.Equal(toS, widened.Scale);
    }

    [Theory]
    [InlineData(10, 2, 10, 1)] // scale shrinks → lossy
    [InlineData(10, 2, 11, 4)] // scale grows but integer range (p-s) shrinks from 8 to 7
    [InlineData(10, 2, 9, 2)]  // precision shrinks
    public void Reconcile_DecimalNarrowing_IsRejected(int fromP, int fromS, int toP, int toS)
    {
        StructType table = Schema(Field("amount", DataTypes.CreateDecimalType(fromP, fromS), nullable: true));
        StructType write = Schema(Field("amount", DataTypes.CreateDecimalType(toP, toS), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
        Assert.Equal("amount", ex.Path);
    }

    // ---- AC3: nested struct recursion -----------------------------------------------------------------

    [Fact]
    public void Reconcile_NestedStructWidening_RecursesAndMerges()
    {
        StructType tableInner = Schema(Field("zip", DataTypes.IntegerType, nullable: true));
        StructType writeInner = Schema(Field("zip", DataTypes.LongType, nullable: true));
        StructType table = Schema(Field("address", tableInner, nullable: true));
        StructType write = Schema(Field("address", writeInner, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.WidenTypes);

        Assert.NotNull(merged);
        StructType mergedInner = Assert.IsType<StructType>(merged![0].DataType);
        Assert.IsType<LongType>(mergedInner[0].DataType);
    }

    [Fact]
    public void Reconcile_NestedStructNarrowing_IsRejectedWithNestedPath()
    {
        StructType tableInner = Schema(Field("zip", DataTypes.LongType, nullable: true));
        StructType writeInner = Schema(Field("zip", DataTypes.IntegerType, nullable: true));
        StructType table = Schema(Field("address", tableInner, nullable: true));
        StructType write = Schema(Field("address", writeInner, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
        Assert.Equal("address.zip", ex.Path);
    }

    [Fact]
    public void Reconcile_NewNestedStructField_WithEvolution_IsAdded()
    {
        StructType tableInner = Schema(Field("zip", DataTypes.IntegerType, nullable: true));
        StructType writeInner = Schema(
            Field("zip", DataTypes.IntegerType, nullable: true),
            Field("city", DataTypes.StringType, nullable: true));
        StructType table = Schema(Field("address", tableInner, nullable: true));
        StructType write = Schema(Field("address", writeInner, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns);

        Assert.NotNull(merged);
        StructType mergedInner = Assert.IsType<StructType>(merged![0].DataType);
        Assert.Equal(2, mergedInner.Count);
        Assert.Equal("city", mergedInner[1].Name);
    }

    // ---- AC3: array / map element compatibility -------------------------------------------------------

    [Fact]
    public void Reconcile_ArrayElementWidening_IsAccepted()
    {
        StructType table = Schema(Field("tags", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true));
        StructType write = Schema(Field("tags", DataTypes.CreateArrayType(DataTypes.LongType), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.WidenTypes);

        Assert.NotNull(merged);
        ArrayType widened = Assert.IsType<ArrayType>(merged![0].DataType);
        Assert.IsType<LongType>(widened.ElementType);
    }

    [Fact]
    public void Reconcile_ArrayElementNarrowing_IsRejectedWithElementPath()
    {
        StructType table = Schema(Field("tags", DataTypes.CreateArrayType(DataTypes.LongType), nullable: true));
        StructType write = Schema(Field("tags", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
        Assert.Equal("tags.element", ex.Path);
    }

    [Fact]
    public void Reconcile_ArrayContainsNullRelaxation_IsRejected()
    {
        StructType table = Schema(
            Field("tags", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: false), nullable: true));
        StructType write = Schema(
            Field("tags", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.NullabilityViolation, ex.Kind);
        Assert.Equal("tags.element", ex.Path);
    }

    [Fact]
    public void Reconcile_MapValueWidening_IsAccepted()
    {
        StructType table = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType), nullable: true));
        StructType write = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.WidenTypes);

        Assert.NotNull(merged);
        MapType widened = Assert.IsType<MapType>(merged![0].DataType);
        Assert.IsType<LongType>(widened.ValueType);
    }

    [Fact]
    public void Reconcile_MapValueContainsNullRelaxation_IsRejectedWithValuePath()
    {
        StructType table = Schema(
            Field(
                "lookup",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: false),
                nullable: true));
        StructType write = Schema(
            Field(
                "lookup",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true),
                nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.NullabilityViolation, ex.Kind);
        Assert.Equal("lookup.value", ex.Path);
    }

    [Fact]
    public void Reconcile_MapKeyNarrowing_IsRejectedWithKeyPath()
    {
        StructType table = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.LongType, DataTypes.StringType), nullable: true));
        StructType write = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.IntegerType, DataTypes.StringType), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
        Assert.Equal("lookup.key", ex.Path);
    }

    // ---- AC3: case-sensitive column-name matching -----------------------------------------------------

    [Fact]
    public void Reconcile_CaseDifferingColumn_TreatedAsDistinctColumn()
    {
        // The table requires "id"; the write provides "Id". Case-sensitive matching means "Id" does NOT
        // satisfy "id", so the required column is missing (and "Id" is a new column).
        StructType table = Schema(Field("id", DataTypes.LongType, nullable: false));
        StructType write = Schema(Field("Id", DataTypes.LongType, nullable: false));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.None));

        Assert.Equal(DeltaSchemaMismatchKind.MissingRequiredColumn, ex.Kind);
        Assert.Equal("id", ex.Path);
    }

    [Fact]
    public void Reconcile_CaseDifferingColumn_AddedAsNewColumnUnderEvolution()
    {
        StructType table = Schema(Field("id", DataTypes.LongType, nullable: true));
        StructType write = Schema(
            Field("id", DataTypes.LongType, nullable: true),
            Field("ID", DataTypes.StringType, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns);

        Assert.NotNull(merged);
        Assert.Equal(2, merged!.Count);
        Assert.Equal("id", merged[0].Name);
        Assert.Equal("ID", merged[1].Name);
    }

    // ---- Rejected lossy promotions (deliberately stricter than generic coercion) ----------------------

    [Theory]
    [MemberData(nameof(RejectedPromotions))]
    public void Reconcile_IntegralToFloatingPromotion_IsRejected(DataType from, DataType to)
    {
        StructType table = Schema(Field("value", from, nullable: true));
        StructType write = Schema(Field("value", to, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    public static TheoryData<DataType, DataType> RejectedPromotions() => new()
    {
        { DataTypes.IntegerType, DataTypes.DoubleType },
        { DataTypes.LongType, DataTypes.DoubleType },
        { DataTypes.LongType, DataTypes.FloatType },
        { DataTypes.IntegerType, DataTypes.FloatType },
    };

    // ---- Determinism / totality ------------------------------------------------------------------------

    [Fact]
    public void Reconcile_IdenticalSchema_ReturnsNull()
    {
        StructType table = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("name", DataTypes.StringType, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, table, SchemaEvolutionMode.MergeSchema);

        Assert.Null(merged);
    }

    [Fact]
    public void Reconcile_ReorderedColumns_ReturnsNull()
    {
        // Matching is by name, so column order is not a schema change.
        StructType table = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("name", DataTypes.StringType, nullable: true));
        StructType write = Schema(
            Field("name", DataTypes.StringType, nullable: true),
            Field("id", DataTypes.LongType, nullable: false));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.None);

        Assert.Null(merged);
    }

    [Fact]
    public void Reconcile_IsDeterministic_AcrossRepeatedCalls()
    {
        StructType table = Schema(Field("value", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(
            Field("value", DataTypes.LongType, nullable: true),
            Field("extra", DataTypes.StringType, nullable: true));

        StructType? first = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema);
        StructType? second = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first, second);
    }
}
