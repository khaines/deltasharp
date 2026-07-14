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
    public void Reconcile_NewColumn_RequiresAddNewColumns_WidenAliasIsInsufficient()
    {
        StructType table = Schema(Field("id", DataTypes.LongType, nullable: false));
        StructType write = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("extra", DataTypes.StringType, nullable: true));

        // Strict enforcement (None) does not permit new columns; only AddNewColumns/MergeSchema does.
        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.None));

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

    [Theory]
    [InlineData(0)] // None
    [InlineData(1)] // AddNewColumns
    public void Reconcile_WouldBeWidening_IsRejectedAsTypeWideningUnsupported_InAnyMode(int mode)
    {
        // FIX 1 (fail-close): int→long is a lossless widening, but widening the logical schema without the
        // typeWidening table feature makes existing Parquet files unreadable even by DeltaSharp. No mode
        // enables it; it is rejected DISTINCTLY (naming the deferred feature) — never silently applied.
        StructType table = Schema(Field("value", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.LongType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, (SchemaEvolutionMode)mode));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
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
    [MemberData(nameof(WouldBeWidenings))]
    public void Reconcile_IntegralAndFloatWidening_IsRejectedAsTypeWideningUnsupported(DataType from, DataType to)
    {
        // FIX 1: each of these is a lossless widening, but all are fail-closed (no typeWidening feature yet).
        StructType table = Schema(Field("value", from, nullable: true));
        StructType write = Schema(Field("value", to, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("value", ex.Path);
    }

    public static TheoryData<DataType, DataType> WouldBeWidenings() => new()
    {
        { DataTypes.ByteType, DataTypes.ShortType },
        { DataTypes.ByteType, DataTypes.IntegerType },
        { DataTypes.ShortType, DataTypes.IntegerType },
        { DataTypes.ShortType, DataTypes.LongType },
        { DataTypes.IntegerType, DataTypes.LongType },
        { DataTypes.ByteType, DataTypes.LongType },
        { DataTypes.FloatType, DataTypes.DoubleType },
    };

    [Fact]
    public void Reconcile_DateToTimestampWidening_IsRejectedAsTypeWideningUnsupported()
    {
        // FIX 1: date→timestamp is a would-be widening but is fail-closed here (Delta only sanctions
        // date→timestamp_ntz, which needs a not-yet-existing NTZ type — #495).
        StructType table = Schema(Field("ts", DataTypes.DateType, nullable: true));
        StructType write = Schema(Field("ts", DataTypes.TimestampType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("ts", ex.Path);
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
    public void Reconcile_WideningRequiredColumn_IsStillRejected()
    {
        // FIX 1: a would-be widening on a required column is rejected just the same (nullability is irrelevant
        // — the type change itself is fail-closed).
        StructType table = Schema(Field("value", DataTypes.IntegerType, nullable: false));
        StructType write = Schema(Field("value", DataTypes.LongType, nullable: false));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
    }

    [Fact]
    public void Reconcile_PreservesFieldMetadataOnAdditiveEvolution()
    {
        // An unchanged, metadata-bearing column keeps its field metadata when the schema evolves additively.
        FieldMetadata metadata = FieldMetadata.FromEntries(new[]
        {
            new KeyValuePair<string, string>("comment", "the amount"),
        });
        StructType table = Schema(new StructField("value", DataTypes.IntegerType, nullable: true, metadata));
        StructType write = Schema(
            Field("value", DataTypes.IntegerType, nullable: true),
            Field("note", DataTypes.StringType, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns);

        Assert.NotNull(merged);
        Assert.True(merged![0].Metadata.TryGetString("comment", out string? comment));
        Assert.Equal("the amount", comment);
    }

    // ---- AC2/AC3: decimal precision/scale compatibility ------------------------------------------------

    [Theory]
    [InlineData(10, 2, 12, 2)] // precision grows, scale equal → integer range grows
    [InlineData(10, 2, 12, 4)] // both precision and scale grow, integer range unchanged
    [InlineData(10, 2, 13, 3)] // both grow
    public void Reconcile_DecimalWidening_IsRejectedAsTypeWideningUnsupported(int fromP, int fromS, int toP, int toS)
    {
        // FIX 1: growing a decimal is a would-be widening, also fail-closed (#495).
        StructType table = Schema(Field("amount", DataTypes.CreateDecimalType(fromP, fromS), nullable: true));
        StructType write = Schema(Field("amount", DataTypes.CreateDecimalType(toP, toS), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("amount", ex.Path);
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
    public void Reconcile_NestedStructWidening_IsRejectedWithNestedPath()
    {
        // FIX 1: a would-be widening inside a nested struct is fail-closed too, with the dotted nested path.
        StructType tableInner = Schema(Field("zip", DataTypes.IntegerType, nullable: true));
        StructType writeInner = Schema(Field("zip", DataTypes.LongType, nullable: true));
        StructType table = Schema(Field("address", tableInner, nullable: true));
        StructType write = Schema(Field("address", writeInner, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("address.zip", ex.Path);
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

    [Fact]
    public void Reconcile_NewFieldInArrayElementStruct_WithEvolution_IsAdded()
    {
        // AC3: additive evolution recurses through an array element's struct, so a merged (changed) element
        // type flows back out as a new ArrayType (preserving the table's containsNull).
        StructType tableElement = Schema(Field("a", DataTypes.IntegerType, nullable: true));
        StructType writeElement = Schema(
            Field("a", DataTypes.IntegerType, nullable: true),
            Field("b", DataTypes.StringType, nullable: true));
        StructType table = Schema(Field("items", DataTypes.CreateArrayType(tableElement, containsNull: false), nullable: true));
        StructType write = Schema(Field("items", DataTypes.CreateArrayType(writeElement, containsNull: false), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns);

        Assert.NotNull(merged);
        ArrayType mergedArray = Assert.IsType<ArrayType>(merged![0].DataType);
        Assert.False(mergedArray.ContainsNull);
        StructType mergedElement = Assert.IsType<StructType>(mergedArray.ElementType);
        Assert.Equal(2, mergedElement.Count);
        Assert.Equal("b", mergedElement[1].Name);
    }

    [Fact]
    public void Reconcile_NewFieldInMapValueStruct_WithEvolution_IsAdded()
    {
        // AC3: additive evolution recurses through a map value's struct, so a merged (changed) value type
        // flows back out as a new MapType (preserving the key type and valueContainsNull).
        StructType tableValue = Schema(Field("a", DataTypes.IntegerType, nullable: true));
        StructType writeValue = Schema(
            Field("a", DataTypes.IntegerType, nullable: true),
            Field("b", DataTypes.StringType, nullable: true));
        StructType table = Schema(Field("lookup", DataTypes.CreateMapType(DataTypes.StringType, tableValue, valueContainsNull: false), nullable: true));
        StructType write = Schema(Field("lookup", DataTypes.CreateMapType(DataTypes.StringType, writeValue, valueContainsNull: false), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns);

        Assert.NotNull(merged);
        MapType mergedMap = Assert.IsType<MapType>(merged![0].DataType);
        Assert.False(mergedMap.ValueContainsNull);
        Assert.IsType<StringType>(mergedMap.KeyType);
        StructType mergedValue = Assert.IsType<StructType>(mergedMap.ValueType);
        Assert.Equal(2, mergedValue.Count);
        Assert.Equal("b", mergedValue[1].Name);
    }

    // ---- AC3: array / map element compatibility -------------------------------------------------------

    [Fact]
    public void Reconcile_ArrayElementWidening_IsRejectedWithElementPath()
    {
        // FIX 1: a would-be widening of an array element is fail-closed, with the `.element` path segment.
        StructType table = Schema(Field("tags", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true));
        StructType write = Schema(Field("tags", DataTypes.CreateArrayType(DataTypes.LongType), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("tags.element", ex.Path);
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
    public void Reconcile_MapValueWidening_IsRejectedWithValuePath()
    {
        // FIX 1: a would-be widening of a map value is fail-closed, with the `.value` path segment.
        StructType table = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType), nullable: true));
        StructType write = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("lookup.value", ex.Path);
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
    public void Reconcile_CaseFoldCollisionFromNewColumn_IsRejected()
    {
        // FIX 2: matching is case-sensitive, so table `id` + write column `ID` would evolve to a merged schema
        // {id, ID}. That collides case-insensitively — invalid at the Delta/Spark storage/protocol level — so
        // the merge is rejected rather than minted.
        StructType table = Schema(Field("id", DataTypes.LongType, nullable: true));
        StructType write = Schema(
            Field("id", DataTypes.LongType, nullable: true),
            Field("ID", DataTypes.StringType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns));

        Assert.Equal(DeltaSchemaMismatchKind.CaseInsensitiveDuplicateColumn, ex.Kind);
        Assert.Equal("ID", ex.Path);
    }

    [Fact]
    public void Reconcile_CaseFoldCollisionInNestedStruct_IsRejectedWithNestedPath()
    {
        // FIX 2: the case-fold uniqueness guard applies recursively — a new nested field that collides with an
        // existing nested field ignoring case is rejected with the dotted path.
        StructType tableInner = Schema(Field("zip", DataTypes.IntegerType, nullable: true));
        StructType writeInner = Schema(
            Field("zip", DataTypes.IntegerType, nullable: true),
            Field("ZIP", DataTypes.StringType, nullable: true));
        StructType table = Schema(Field("address", tableInner, nullable: true));
        StructType write = Schema(Field("address", writeInner, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns));

        Assert.Equal(DeltaSchemaMismatchKind.CaseInsensitiveDuplicateColumn, ex.Kind);
        Assert.Equal("address.ZIP", ex.Path);
    }

    // ---- FIX 3: partition-column awareness ------------------------------------------------------------

    [Fact]
    public void Reconcile_PartitionColumnTypeChange_IsRejectedDistinctly()
    {
        // FIX 3 + red-team #536 (case 2): int→long IS a Delta-sanctioned widening and, on a partition column,
        // is rewrite-FREE (partition values are strings) — so even with type widening NOT enabled it is
        // DEFERRED (#537), classified distinctly as TypeWideningUnsupported with an honest message. It must
        // NEVER carry the factually-wrong "requires a full table rewrite" reason just because the feature is
        // disabled (a feature-enablement gap, not a layout rewrite).
        StructType table = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("region", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("region", DataTypes.LongType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, new[] { "region" }));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("region", ex.Path);
        Assert.Contains("#537", ex.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("requires a full table rewrite", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_PartitionColumnUnchanged_WithAdditiveEvolution_IsAccepted()
    {
        // FIX 3: an additive evolution that leaves the partition column's type unchanged is fine.
        StructType table = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("region", DataTypes.StringType, nullable: true));
        StructType write = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("region", DataTypes.StringType, nullable: true),
            Field("note", DataTypes.StringType, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.AddNewColumns, new[] { "region" });

        Assert.NotNull(merged);
        Assert.Equal(3, merged!.Count);
        Assert.Equal("note", merged[2].Name);
    }

    // ---- Rejected lossy promotions (deliberately stricter than generic coercion) ----------------------

    [Theory]
    [MemberData(nameof(RejectedPromotions))]
    public void Reconcile_LossyFloatingPromotion_IsRejectedAsIncompatible(DataType from, DataType to)
    {
        // These are NOT Delta-sanctioned widenings (int→float and long→float lose precision; long→double is
        // lossy), so they are the generic IncompatibleType — distinct from the cross-family SANCTIONED
        // widenings (int→double, int/long→decimal) which are TypeWideningUnsupported/deferred #535 below.
        StructType table = Schema(Field("value", from, nullable: true));
        StructType write = Schema(Field("value", to, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    public static TheoryData<DataType, DataType> RejectedPromotions() => new()
    {
        { DataTypes.LongType, DataTypes.DoubleType },
        { DataTypes.LongType, DataTypes.FloatType },
        { DataTypes.IntegerType, DataTypes.FloatType },
    };

    // ---- FIX 3 (#535): cross-family SANCTIONED widenings are deferred, distinct from IncompatibleType ----

    [Theory]
    [MemberData(nameof(CrossFamilyDeferredWidenings))]
    public void Reconcile_CrossFamilyWidening_IsDeferred_AsTypeWideningUnsupported(DataType from, DataType to, bool enabled)
    {
        // int→double / long→decimal etc. ARE Delta-sanctioned but this build DEFERS them (#535). They are
        // surfaced DISTINCTLY as TypeWideningUnsupported (naming #535), NOT the generic IncompatibleType a
        // string→int gets — even when the feature is enabled (cross-family deferral is enablement-independent).
        StructType table = Schema(Field("value", from, nullable: true));
        StructType write = Schema(Field("value", to, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: enabled));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Contains("#535", ex.Message, System.StringComparison.Ordinal);
    }

    public static TheoryData<DataType, DataType, bool> CrossFamilyDeferredWidenings() => new()
    {
        { DataTypes.IntegerType, DataTypes.DoubleType, true },
        { DataTypes.IntegerType, DataTypes.DoubleType, false },
        { DataTypes.ByteType, DataTypes.DoubleType, true },
        { DataTypes.ShortType, DataTypes.DoubleType, true },
        { DataTypes.LongType, DataTypes.CreateDecimalType(20, 0), true },
        { DataTypes.IntegerType, DataTypes.CreateDecimalType(12, 2), true },
        { DataTypes.ByteType, DataTypes.CreateDecimalType(5, 0), false },
    };

    [Fact]
    public void Reconcile_UnrelatedChange_IsIncompatible_DistinctFromCrossFamilyDeferred()
    {
        // string→int is NOT a Delta-sanctioned change at all → the generic IncompatibleType, distinct from
        // the cross-family SANCTIONED-but-deferred widenings above.
        StructType table = Schema(Field("value", DataTypes.StringType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.IntegerType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

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
        // A deterministic additive evolution (new nullable column) yields an identical merged schema each call.
        StructType table = Schema(Field("value", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(
            Field("value", DataTypes.IntegerType, nullable: true),
            Field("extra", DataTypes.StringType, nullable: true));

        StructType? first = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema);
        StructType? second = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first, second);
    }

    // ---- #495: type widening APPLIED when the feature is enabled --------------------------------------

    [Theory]
    [MemberData(nameof(WouldBeWidenings))]
    public void Reconcile_Widening_WhenEnabled_AppliesAndRecordsTypeChange(DataType from, DataType to)
    {
        // With typeWidening enabled, each sanctioned integral/float widening is APPLIED: the merged schema
        // carries the WIDE type and a delta.typeChanges entry {fromType,toType} (Delta PROTOCOL.md
        // "Type Change Metadata").
        StructType table = Schema(Field("value", from, nullable: true));
        StructType write = Schema(Field("value", to, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["value"];
        Assert.Equal(to, field.DataType);
        AssertSingleTypeChange(field.Metadata, from.TypeName, to.TypeName);
    }

    [Theory]
    [InlineData(10, 2, 12, 2)] // integer range grows, scale equal
    [InlineData(10, 2, 12, 4)] // both grow, integer range unchanged
    [InlineData(10, 2, 13, 3)] // both grow
    public void Reconcile_DecimalGrowOnlyWidening_WhenEnabled_Applies(int fromP, int fromS, int toP, int toS)
    {
        DecimalType from = DataTypes.CreateDecimalType(fromP, fromS);
        DecimalType to = DataTypes.CreateDecimalType(toP, toS);
        StructType table = Schema(Field("amount", from, nullable: true));
        StructType write = Schema(Field("amount", to, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["amount"];
        Assert.Equal(to, field.DataType);
        AssertSingleTypeChange(field.Metadata, from.TypeName, to.TypeName);
    }

    [Theory]
    [InlineData(10, 2, 10, 1)] // scale shrinks
    [InlineData(10, 2, 11, 4)] // integer range shrinks
    [InlineData(10, 2, 9, 2)]  // precision shrinks
    public void Reconcile_DecimalNarrowing_WhenEnabled_IsStillRejected(int fromP, int fromS, int toP, int toS)
    {
        StructType table = Schema(Field("amount", DataTypes.CreateDecimalType(fromP, fromS), nullable: true));
        StructType write = Schema(Field("amount", DataTypes.CreateDecimalType(toP, toS), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Reconcile_DateToTimestamp_WhenEnabled_IsStillRejectedAsTypeWideningUnsupported()
    {
        // date→timestamp is DEFERRED even with the feature enabled: Delta only sanctions date→timestamp_ntz,
        // and no NTZ type exists in this build, so it stays fail-closed.
        StructType table = Schema(Field("ts", DataTypes.DateType, nullable: true));
        StructType write = Schema(Field("ts", DataTypes.TimestampType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
    }

    [Fact]
    public void Reconcile_LossyChange_WhenEnabled_IsRejectedAsIncompatible()
    {
        // long→int (narrowing) is not a widening at all: still IncompatibleType even with the feature enabled.
        StructType table = Schema(Field("value", DataTypes.LongType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.IntegerType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Reconcile_LongToDouble_WhenEnabled_IsRejectedAsIncompatible()
    {
        // long→double is LOSSY (a 64-bit integer exceeds double's 53-bit mantissa) and NOT Delta-sanctioned —
        // it is neither an applied widening nor a cross-family deferral, so it stays IncompatibleType even
        // when the feature is enabled (fail-closed).
        StructType table = Schema(Field("value", DataTypes.LongType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.DoubleType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Reconcile_UnrelatedChange_WhenEnabled_IsRejectedAsIncompatible()
    {
        // int→string is not a sanctioned widening: IncompatibleType even with the feature enabled.
        StructType table = Schema(Field("value", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.StringType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Reconcile_Widening_WithoutEnablement_IsRejectedAsTypeWideningUnsupported()
    {
        // The same int→long that applies when enabled stays fail-closed when the feature is NOT enabled.
        StructType table = Schema(Field("value", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.LongType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: false));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
    }

    [Fact]
    public void Reconcile_PartitionColumnWidening_WhenEnabled_IsDeferred_NotRewriteClaim()
    {
        // FIX 2 (#537): a partition-column WIDENING is Delta-sanctioned WITHOUT a rewrite (partition values
        // are strings), so it is NOT the factually-wrong "requires a full table rewrite" case. This build
        // DEFERS it (#537): still rejected, but classified DISTINCTLY as TypeWideningUnsupported with an
        // honest message naming #537 — never PartitionColumnEvolutionUnsupported.
        StructType table = Schema(Field("part", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(Field("part", DataTypes.LongType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: new[] { "part" }, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("part", ex.Path);
        Assert.Contains("#537", ex.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("requires a full table rewrite", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_PartitionColumnNonWideningChange_IsRejectedAsPartitionColumnEvolution()
    {
        // A NON-widening partition-column type change (int→string) keeps the existing classification.
        StructType table = Schema(Field("part", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(Field("part", DataTypes.StringType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: new[] { "part" }, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.PartitionColumnEvolutionUnsupported, ex.Kind);
    }

    [Fact]
    public void Reconcile_PartitionColumnCrossFamilyWidening_IsDeferred_NotRewriteClaim()
    {
        // Red-team #536 (case 1): a CROSS-FAMILY partition-column widening (int→double) is Delta-sanctioned
        // and rewrite-FREE (partition values are strings), just deferred in this build. IsSanctionedWidening
        // is false for it (it is tracked by the cross-family classifier, #535), so it must still route to the
        // honest partition-widening deferral (#537, TypeWideningUnsupported) — never to the factually-wrong
        // "requires a full table rewrite" PartitionColumnEvolution case — even with type widening enabled.
        StructType table = Schema(Field("part", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(Field("part", DataTypes.DoubleType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: new[] { "part" }, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("part", ex.Path);
        Assert.Contains("#537", ex.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("requires a full table rewrite", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_PartitionColumnDateToTimestampWidening_IsDeferred_NotRewriteClaim()
    {
        // Pins the THIRD arm of TypeWidening.IsAnySanctionedWidening on the partition guard: date→timestamp
        // (the #533 deferral). Delta sanctions date→timestamp_ntz and, on a partition column, it is rewrite-
        // free (partition values are strings), so it must route to the honest #537 deferral
        // (TypeWideningUnsupported) — never PartitionColumnEvolution's "requires a full table rewrite". Without
        // this test the date arm of the union predicate is unpinned at the partition guard (existing
        // date→timestamp tests take the scalar, non-partition path).
        StructType table = Schema(Field("part", DataTypes.DateType, nullable: true));
        StructType write = Schema(Field("part", DataTypes.TimestampType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: new[] { "part" }, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("part", ex.Path);
        Assert.Contains("#537", ex.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("requires a full table rewrite", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_Widening_PreservesPriorTypeChangeHistory_OldestFirst()
    {
        // A field already widened once (short→int, recorded in delta.typeChanges) is widened again (int→long):
        // the new change appends to the history, oldest first (Delta requires the full history).
        FieldMetadata priorHistory = FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(
                "delta.typeChanges",
                MetadataValue.Array(new[]
                {
                    MetadataValue.Nested(FieldMetadata.FromEntries(new[]
                    {
                        new KeyValuePair<string, string>("fromType", "short"),
                        new KeyValuePair<string, string>("toType", "integer"),
                    })),
                })),
        });
        StructType table = Schema(new StructField("value", DataTypes.IntegerType, nullable: true, priorHistory));
        StructType write = Schema(Field("value", DataTypes.LongType, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["value"];
        Assert.Equal(DataTypes.LongType, field.DataType);
        Assert.True(field.Metadata.TryGetValue("delta.typeChanges", out MetadataValue? changes));
        Assert.True(changes!.TryGetArray(out IReadOnlyList<MetadataValue>? entries));
        Assert.Equal(2, entries!.Count);
        AssertTypeChangeEntry(entries[0], "short", "integer");
        AssertTypeChangeEntry(entries[1], "integer", "long");
    }

    [Fact]
    public void Reconcile_WideningInsideArrayElement_IsNotApplied_EvenWhenEnabled()
    {
        // Applied widening is confined to a StructField's own scalar type; an array element widening is not
        // applied (it would need a fieldPath in delta.typeChanges and the reader can't promote nested types).
        var tableArray = new ArrayType(DataTypes.IntegerType, containsNull: true);
        var writeArray = new ArrayType(DataTypes.LongType, containsNull: true);
        StructType table = Schema(Field("nums", tableArray, nullable: true));
        StructType write = Schema(Field("nums", writeArray, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
    }

    private static void AssertSingleTypeChange(FieldMetadata metadata, string fromType, string toType)
    {
        Assert.True(metadata.TryGetValue("delta.typeChanges", out MetadataValue? changes));
        Assert.True(changes!.TryGetArray(out IReadOnlyList<MetadataValue>? entries));
        MetadataValue only = Assert.Single(entries!);
        AssertTypeChangeEntry(only, fromType, toType);
    }

    private static void AssertTypeChangeEntry(MetadataValue entry, string fromType, string toType)
    {
        Assert.True(entry.TryGetNested(out FieldMetadata? nested));
        Assert.True(nested!.TryGetString("fromType", out string? actualFrom));
        Assert.True(nested.TryGetString("toType", out string? actualTo));
        Assert.Equal(fromType, actualFrom);
        Assert.Equal(toType, actualTo);
    }
}
