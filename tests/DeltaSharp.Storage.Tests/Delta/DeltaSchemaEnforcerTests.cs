using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
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

    // ---- #702: NullType ("void") reachability through the Delta WRITE door -----------------------------

    // VERDICT (#702, CORRECTED at Round-1 review): the pre-review claim — that a NullType column cannot reach
    // a committed metaData.schemaString because ParquetTypeMapping.CreateField rejects it first — was FALSE.
    // CreateField is a PER-FILE guard; a ZERO-FILE create (an empty write to a fresh path) stages no file at
    // all, so neither CreateField nor DeltaTableWriter.ValidateStagedWriteSchema (which iterates the staged
    // file list) ever runs, and the declared schema went straight into version 0 as "type":"void" — a table
    // DeltaSharp itself then refused to read. See
    // DeltaWriteSchemaEligibilityTests.ZeroFileCreate_WithVoidColumn_CommitsNothing for the end-to-end repro.
    //
    // The door is now closed at DeltaWriteSchemaEligibility.EnsureCommittable, invoked on every path that
    // builds a metaData action, INDEPENDENT of the staged-file count. DeltaSchemaEnforcer.Reconcile is still
    // not the guard (a CREATE at version 0 bypasses it entirely). Read-side tolerance is deliberately
    // unchanged (SchemaJson.FromJson still accepts "void"/"null"), so a schemaString another engine wrote —
    // delta-rs 1.6.2 maps "void" to Arrow Null — still round-trips.
    //
    // This test keeps the per-file guard honest; the write-schema door itself is pinned end-to-end by
    // DeltaWriteSchemaEligibilityTests.
    [Fact]
    public void NullTypeColumn_IsAlsoRejectedByThePerFileParquetGuard()
    {
        DeltaStorageException ex = Assert.ThrowsAny<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(
                new StructField("n", DataTypes.NullType, nullable: true), honorReferenceNullability: false));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        Assert.Contains("is not supported", ex.Message, StringComparison.Ordinal);
    }

    // ---- AC1: reject-before-commit, classified by DeltaSchemaMismatchKind (mode = None) ----------------

    [Fact]
    public void Reconcile_IncompatibleType_IsRejected()
    {
        StructType table = Schema(Field("value", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.StringType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.None, ColumnMappingMode.None));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
        Assert.Equal("value", ex.Path);
    }

    [Fact]
    public void Reconcile_NarrowingType_IsRejectedAsIncompatible()
    {
        StructType table = Schema(Field("value", DataTypes.LongType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.IntegerType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.None, ColumnMappingMode.None);

        Assert.Null(merged);
    }

    [Fact]
    public void Reconcile_NullabilityViolation_IsRejected()
    {
        StructType table = Schema(Field("id", DataTypes.LongType, nullable: false));
        StructType write = Schema(Field("id", DataTypes.LongType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.None, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.None, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, (SchemaEvolutionMode)mode, ColumnMappingMode.None));

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

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns, ColumnMappingMode.None);

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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
    public void Reconcile_DateToTimestampLtz_IsRejectedAsIncompatibleType()
    {
        // date→timestamp with a timezone (LTZ) is NOT a Delta-sanctioned widening at all — Delta only widens
        // date→timestamp_ntz (#533). So it is a plain incompatible type change, not a deferred widening.
        StructType table = Schema(Field("ts", DataTypes.DateType, nullable: true));
        StructType write = Schema(Field("ts", DataTypes.TimestampType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
        Assert.Equal("ts", ex.Path);
    }

    [Fact]
    public void Reconcile_DateToTimestampNtz_WhenDisabled_IsRejectedAsTypeWideningUnsupported()
    {
        // date→timestamp_ntz IS a sanctioned widening (#533), but with the feature disabled it is fail-closed
        // and surfaced distinctly as TypeWideningUnsupported (naming the enablement requirement), not the
        // generic IncompatibleType.
        StructType table = Schema(Field("ts", DataTypes.DateType, nullable: true));
        StructType write = Schema(Field("ts", DataTypes.TimestampNtzType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("ts", ex.Path);
    }

    [Fact]
    public void Reconcile_TimestampToDateNarrowing_IsRejected()
    {
        StructType table = Schema(Field("ts", DataTypes.TimestampType, nullable: true));
        StructType write = Schema(Field("ts", DataTypes.DateType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns, ColumnMappingMode.None);

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns, ColumnMappingMode.None);

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

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns, ColumnMappingMode.None);

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

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns, ColumnMappingMode.None);

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("tags.element", ex.Path);
    }

    [Fact]
    public void Reconcile_ArrayElementNarrowing_IsRejectedWithElementPath()
    {
        StructType table = Schema(Field("tags", DataTypes.CreateArrayType(DataTypes.LongType), nullable: true));
        StructType write = Schema(Field("tags", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.None, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns, ColumnMappingMode.None));

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
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.AddNewColumns, ColumnMappingMode.None));

        Assert.Equal(DeltaSchemaMismatchKind.CaseInsensitiveDuplicateColumn, ex.Kind);
        Assert.Equal("address.ZIP", ex.Path);
    }

    // ---- FIX 3: partition-column awareness ------------------------------------------------------------

    [Fact]
    public void Reconcile_PartitionColumnTypeChange_WithoutEnablement_IsRejectedAsTypeWideningUnsupported()
    {
        // int→long IS a Delta-sanctioned intra-family widening and, on a partition column, is rewrite-FREE
        // (partition values are strings). It is APPLIED when the feature is enabled (#537) but, with type
        // widening NOT enabled, stays fail-closed — classified (exactly like a non-partition column) as
        // TypeWideningUnsupported naming the enablement requirement. It must NEVER carry the factually-wrong
        // "requires a full table rewrite" reason (a feature-enablement gap, not a layout rewrite).
        StructType table = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("region", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("region", DataTypes.LongType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, new[] { "region" }));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("region", ex.Path);
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
            table, write, SchemaEvolutionMode.AddNewColumns, ColumnMappingMode.None, new[] { "region" });

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
        // widenings (int→double, int/long→decimal) which are applied when enabled / TypeWideningUnsupported
        // when disabled (#535).
        StructType table = Schema(Field("value", from, nullable: true));
        StructType write = Schema(Field("value", to, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    public static TheoryData<DataType, DataType> RejectedPromotions() => new()
    {
        { DataTypes.LongType, DataTypes.DoubleType },
        { DataTypes.LongType, DataTypes.FloatType },
        { DataTypes.IntegerType, DataTypes.FloatType },
    };

    // ---- #535: cross-family SANCTIONED widenings are READ-PROMOTABLE but NOT applied on write ----

    [Theory]
    [MemberData(nameof(CrossFamilyWidenings))]
    public void Reconcile_CrossFamilyWidening_WhenEnabled_IsRejectedAsTypeWideningUnsupported_ReadOnly(
        DataType from, DataType to)
    {
        // int→double, byte/short→double, and int/long→decimal (that fits the range) ARE Delta-sanctioned
        // widenings for READ + explicit ALTER TABLE (Delta `TypeWidening.isTypeChangeSupported`), but they are
        // NOT eligible for automatic SCHEMA EVOLUTION (`isTypeChangeSupportedForSchemaEvolution` excludes
        // int→double and integral→decimal). DeltaSharp applies widenings only on the append/reconcile path
        // (schema evolution), so — matching Spark — a cross-family widening is REJECTED fail-closed as
        // TypeWideningUnsupported EVEN WHEN the feature is enabled. #535 supports these ONLY on READ
        // (ParquetFileReader.ReadPromotedColumnAsync) for interop with a Spark/delta-rs table that recorded the
        // change via ALTER TABLE; DeltaSharp never auto-applies them on write.
        StructType table = Schema(Field("value", from, nullable: true));
        StructType write = Schema(Field("value", to, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
    }

    [Theory]
    [MemberData(nameof(CrossFamilyWidenings))]
    public void Reconcile_CrossFamilyWidening_WhenDisabled_IsRejectedAsTypeWideningUnsupported(DataType from, DataType to)
    {
        // With the feature DISABLED, a cross-family sanctioned widening is REJECTED fail-closed as
        // TypeWideningUnsupported (naming the enablement requirement) — NOT the generic IncompatibleType a
        // string→int gets — because it IS Delta-sanctioned, just not applied on write.
        StructType table = Schema(Field("value", from, nullable: true));
        StructType write = Schema(Field("value", to, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: false));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
    }

    public static TheoryData<DataType, DataType> CrossFamilyWidenings() => new()
    {
        { DataTypes.ByteType, DataTypes.DoubleType },
        { DataTypes.ShortType, DataTypes.DoubleType },
        { DataTypes.IntegerType, DataTypes.DoubleType },
        { DataTypes.ByteType, DataTypes.CreateDecimalType(10, 0) },
        { DataTypes.ShortType, DataTypes.CreateDecimalType(10, 0) },
        { DataTypes.IntegerType, DataTypes.CreateDecimalType(12, 2) },
        { DataTypes.LongType, DataTypes.CreateDecimalType(20, 0) },
    };

    [Theory]
    [MemberData(nameof(TooNarrowDecimalTargets))]
    public void Reconcile_IntegralToTooNarrowDecimal_IsRejectedAsIncompatible(DataType from, int toP, int toS)
    {
        // AC5: a decimal target that cannot hold the FULL integral range losslessly is NOT a sanctioned
        // widening (it would truncate). It is rejected fail-closed as the generic IncompatibleType — never
        // silently applied — even with the feature enabled.
        StructType table = Schema(Field("value", from, nullable: true));
        StructType write = Schema(Field("value", DataTypes.CreateDecimalType(toP, toS), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    public static TheoryData<DataType, int, int> TooNarrowDecimalTargets() => new()
    {
        // Delta keys the threshold to the Parquet PHYSICAL type: byte/short/int (INT32) need p−s ≥ 10, long
        // (INT64) needs p−s ≥ 20 — NOT the value-range digit count. A decimal below that threshold is not a
        // sanctioned widening (falls through to IncompatibleType), even one that would hold the value range.
        { DataTypes.ByteType, 9, 0 },     // INT32 source needs decimal(10,0)+; (9,0) below threshold
        { DataTypes.ShortType, 9, 0 },    // INT32 source needs decimal(10,0)+
        { DataTypes.IntegerType, 9, 0 },  // int (INT32) needs p−s ≥ 10; decimal(9,0) truncates
        { DataTypes.IntegerType, 11, 2 }, // p−s = 9 < 10
        { DataTypes.LongType, 19, 0 },    // long (INT64) needs p−s ≥ 20; (19,0) below threshold (lossless by value)
        { DataTypes.LongType, 18, 0 },    // p−s = 18 < 20
    };

    [Fact]
    public void Reconcile_DoubleToDecimal_WhenEnabled_IsRejectedAsIncompatible()
    {
        // AC4: double→decimal is NOT Delta-sanctioned (double is not an integral source). It stays
        // IncompatibleType even with the feature enabled (fail-closed).
        StructType table = Schema(Field("value", DataTypes.DoubleType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.CreateDecimalType(20, 4), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Reconcile_UnrelatedChange_IsIncompatible_DistinctFromCrossFamilyWidening()
    {
        // string→int is NOT a Delta-sanctioned change at all → the generic IncompatibleType, distinct from
        // the cross-family SANCTIONED widenings above (which apply, or fail-closed as TypeWideningUnsupported
        // when disabled).
        StructType table = Schema(Field("value", DataTypes.StringType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.IntegerType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    // ---- Determinism / totality ------------------------------------------------------------------------

    [Fact]
    public void Reconcile_IdenticalSchema_ReturnsNull()
    {
        StructType table = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("name", DataTypes.StringType, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, table, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None);

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

        StructType? merged = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.None, ColumnMappingMode.None);

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

        StructType? first = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None);
        StructType? second = DeltaSchemaEnforcer.Reconcile(table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None);

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
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

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
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

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
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Reconcile_DateToTimestampNtz_WhenEnabled_Applies()
    {
        // date→timestamp_ntz is schema-evolution-eligible (#533): with the feature enabled the merged schema
        // carries timestamp_ntz and a delta.typeChanges {date, timestamp_ntz} entry. Existing date files are
        // read-promoted to midnight-of-date micros; new rows write native timestamp_ntz.
        StructType table = Schema(Field("ts", DataTypes.DateType, nullable: true));
        StructType write = Schema(Field("ts", DataTypes.TimestampNtzType, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["ts"];
        Assert.Equal(DataTypes.TimestampNtzType, field.DataType);
        AssertSingleTypeChange(field.Metadata, DataTypes.DateType.TypeName, DataTypes.TimestampNtzType.TypeName);
    }

    [Fact]
    public void Reconcile_DateToTimestampLtz_WhenEnabled_IsRejectedAsIncompatibleType()
    {
        // date→timestamp with a timezone (LTZ) stays a plain incompatible change even with the feature
        // enabled — Delta never widens date→timestamp (only date→timestamp_ntz).
        StructType table = Schema(Field("ts", DataTypes.DateType, nullable: true));
        StructType write = Schema(Field("ts", DataTypes.TimestampType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Reconcile_LossyChange_WhenEnabled_IsRejectedAsIncompatible()
    {
        // long→int (narrowing) is not a widening at all: still IncompatibleType even with the feature enabled.
        StructType table = Schema(Field("value", DataTypes.LongType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.IntegerType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Reconcile_LongToDouble_WhenEnabled_IsRejectedAsIncompatible()
    {
        // long→double is LOSSY (a 64-bit integer exceeds double's 53-bit mantissa) and NOT Delta-sanctioned —
        // it is neither an applied widening nor a cross-family widening, so it stays IncompatibleType even
        // when the feature is enabled (fail-closed).
        StructType table = Schema(Field("value", DataTypes.LongType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.DoubleType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

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
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

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
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: false));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
    }

    [Fact]
    public void Reconcile_PartitionColumnWidening_WhenEnabled_IsApplied_WithTypeChanges()
    {
        // #537: an intra-family partition-column WIDENING (int→long) is Delta-sanctioned WITHOUT a rewrite
        // (partition values are strings), so it is now APPLIED when the feature is enabled — EXACTLY like a
        // non-partition column: the merged schema carries the WIDE type and a delta.typeChanges entry
        // {fromType,toType} on the partition field so the read door const-fills the partition string under the
        // widened type. metaData.partitionColumns (the logical name) is not the enforcer's output — the writer
        // keeps it unchanged; the schema here is what proves the field widened + recorded its type change.
        StructType table = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("part", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(
            Field("id", DataTypes.LongType, nullable: false),
            Field("part", DataTypes.LongType, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: new[] { "part" }, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["part"];
        Assert.Equal(DataTypes.LongType, field.DataType);
        AssertSingleTypeChange(field.Metadata, DataTypes.IntegerType.TypeName, DataTypes.LongType.TypeName);
    }

    [Fact]
    public void Reconcile_PartitionColumnNonWideningChange_IsRejectedAsPartitionColumnEvolution()
    {
        // A NON-widening partition-column type change (int→string) keeps the existing classification.
        StructType table = Schema(Field("part", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(Field("part", DataTypes.StringType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: new[] { "part" }, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.PartitionColumnEvolutionUnsupported, ex.Kind);
    }

    [Fact]
    public void Reconcile_PartitionColumnCrossFamilyWidening_IsDeferred_NotRewriteClaim()
    {
        // Red-team #536 (case 1): a CROSS-FAMILY partition-column widening (int→double) is Delta-sanctioned
        // and rewrite-FREE (partition values are strings). Even though #535 now APPLIES cross-family widening
        // to DATA columns, the partition guard applies only SAME-family widening (IsSameFamilyWidening is
        // false for cross-family); a partition cross-family widening stays DEFERRED (#537) because there is no
        // data-file value to promote — it would need the read door to parse the partition-value STRING into
        // the widened lane. So it must still route to the honest partition-widening deferral (#537,
        // TypeWideningUnsupported) — never the factually-wrong "requires a full table rewrite"
        // PartitionColumnEvolution case — even with type widening enabled.
        StructType table = Schema(Field("part", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(Field("part", DataTypes.DoubleType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: new[] { "part" }, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("part", ex.Path);
        Assert.Contains("#537", ex.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("requires a full table rewrite", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_PartitionColumnDateToTimestampNtzWidening_IsDeferred_NotRewriteClaim()
    {
        // Pins the temporal arm of TypeWidening.IsAnySanctionedWidening on the partition guard:
        // date→timestamp_ntz (the #533 widening). Delta sanctions it and, on a partition column, it is rewrite-
        // free (partition values are strings), so it must route to the honest #537 deferral
        // (TypeWideningUnsupported) — never PartitionColumnEvolution's "requires a full table rewrite". Without
        // this test the temporal arm is unpinned at the partition guard: the scalar, non-partition
        // date→timestamp_ntz path rejects via the general IsSanctionedWidening branch, a DIFFERENT reject
        // branch than the partition guard's PartitionColumnWideningDeferred.
        StructType table = Schema(Field("part", DataTypes.DateType, nullable: true));
        StructType write = Schema(Field("part", DataTypes.TimestampNtzType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: new[] { "part" }, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("part", ex.Path);
        Assert.Contains("#537", ex.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("requires a full table rewrite", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_PartitionColumnDateToTimestampLtz_IsPartitionColumnEvolution()
    {
        // date→timestamp with a timezone (LTZ) is NOT a sanctioned widening at all, so on a partition column it
        // is a genuine non-widening evolution — PartitionColumnEvolutionUnsupported, NOT the #537 rewrite-free
        // widening deferral (which is reserved for actually-sanctioned families).
        StructType table = Schema(Field("part", DataTypes.DateType, nullable: true));
        StructType write = Schema(Field("part", DataTypes.TimestampType, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: new[] { "part" }, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.PartitionColumnEvolutionUnsupported, ex.Kind);
        Assert.Equal("part", ex.Path);
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
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

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
    public void Reconcile_ArrayElementWidening_WhenEnabled_IsAppliedWithElementPath()
    {
        // #546: a sanctioned widening of a TOP-LEVEL array<scalar> element (the shape #571's reader decodes)
        // is APPLIED when the feature is enabled — the merged element type widens and a delta.typeChanges
        // entry carrying fieldPath="element" is recorded on the enclosing field so pre-widening nested files
        // are read-promoted.
        var tableArray = new ArrayType(DataTypes.IntegerType, containsNull: true);
        var writeArray = new ArrayType(DataTypes.LongType, containsNull: true);
        StructType table = Schema(Field("nums", tableArray, nullable: true));
        StructType write = Schema(Field("nums", writeArray, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["nums"];
        ArrayType mergedArray = Assert.IsType<ArrayType>(field.DataType);
        Assert.Equal(DataTypes.LongType, mergedArray.ElementType);
        Assert.True(mergedArray.ContainsNull);
        AssertSingleNestedTypeChange(field.Metadata, "integer", "long", "element");
    }

    [Fact]
    public void Reconcile_MapValueWidening_WhenEnabled_IsAppliedWithValuePath()
    {
        // #546: a sanctioned widening of a TOP-LEVEL map value is APPLIED with fieldPath="value".
        StructType table = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType), nullable: true));
        StructType write = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["lookup"];
        MapType mergedMap = Assert.IsType<MapType>(field.DataType);
        Assert.IsType<StringType>(mergedMap.KeyType);
        Assert.Equal(DataTypes.LongType, mergedMap.ValueType);
        AssertSingleNestedTypeChange(field.Metadata, "integer", "long", "value");
    }

    [Fact]
    public void Reconcile_MapKeyWidening_WhenEnabled_IsAppliedWithKeyPath()
    {
        // #546: a sanctioned widening of a TOP-LEVEL map key is APPLIED with fieldPath="key".
        StructType table = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.IntegerType, DataTypes.StringType), nullable: true));
        StructType write = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.LongType, DataTypes.StringType), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["lookup"];
        MapType mergedMap = Assert.IsType<MapType>(field.DataType);
        Assert.Equal(DataTypes.LongType, mergedMap.KeyType);
        Assert.IsType<StringType>(mergedMap.ValueType);
        AssertSingleNestedTypeChange(field.Metadata, "integer", "long", "key");
    }

    [Fact]
    public void Reconcile_MapKeyAndValueWidening_WhenEnabled_RecordsBothPaths()
    {
        // #546: widening BOTH the key and the value of a top-level map records two delta.typeChanges entries
        // on the enclosing field, each with its own fieldPath ("key" then "value", in merge order).
        StructType table = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.IntegerType, DataTypes.ShortType), nullable: true));
        StructType write = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.LongType, DataTypes.IntegerType), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["lookup"];
        MapType mergedMap = Assert.IsType<MapType>(field.DataType);
        Assert.Equal(DataTypes.LongType, mergedMap.KeyType);
        Assert.Equal(DataTypes.IntegerType, mergedMap.ValueType);
        Assert.True(field.Metadata.TryGetValue("delta.typeChanges", out MetadataValue? changes));
        Assert.True(changes!.TryGetArray(out IReadOnlyList<MetadataValue>? entries));
        Assert.Equal(2, entries!.Count);
        AssertNestedTypeChangeEntry(entries[0], "integer", "long", "key");
        AssertNestedTypeChangeEntry(entries[1], "short", "integer", "value");
    }

    [Fact]
    public void Reconcile_StructFieldWidening_WhenEnabled_IsAppliedOnInnerField_NoFieldPath()
    {
        // #546: a sanctioned widening of a TOP-LEVEL struct<scalar> field (readable by #571) is APPLIED and
        // recorded on the INNER StructField directly, with NO fieldPath (a struct field's change is carried
        // on the field, per PROTOCOL.md "Type Change Metadata").
        StructType tableInner = Schema(Field("zip", DataTypes.IntegerType, nullable: true));
        StructType writeInner = Schema(Field("zip", DataTypes.LongType, nullable: true));
        StructType table = Schema(Field("address", tableInner, nullable: true));
        StructType write = Schema(Field("address", writeInner, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField addressField = merged!["address"];
        StructType mergedInner = Assert.IsType<StructType>(addressField.DataType);
        StructField zip = mergedInner["zip"];
        Assert.Equal(DataTypes.LongType, zip.DataType);

        // The change is on the inner `zip` field (no fieldPath), and the enclosing `address` field carries none.
        AssertSingleTypeChange(zip.Metadata, "integer", "long");
        Assert.False(addressField.Metadata.TryGetValue("delta.typeChanges", out _));
    }

    // ---- #870: column-mapping id-mode gates the NESTED-leaf widening APPLY (enforcer ⊆ reader) ----------
    //
    // Empirically (read probes against a real written footer): under id mode the flat reader PROMOTES a
    // top-level scalar by field_id (ValidateFileField gates on the promotion flag alone, no field_id
    // conjunct) — so a top-level widen is READABLE and must NOT be blocked. But the NESTED reader binds an
    // array element / map key-value / struct child by field_id with promoteLeaf:false hardcoded
    // (#839/#546 §9 O1), so it fails closed SchemaMismatch on a pre-widening narrow file — an UNREADABLE
    // table. The enforcer therefore refuses to APPLY those nested widenings under id mode (fail-closed
    // TypeWideningUnsupported), so enforcer ⊆ reader holds. name/none mode is byte-for-byte unchanged.

    [Fact]
    public void Reconcile_ArrayElementWidening_IdMode_FailsClosed_AsTypeWideningUnsupported()
    {
        // array<int> → array<long> under id mode: the id-mode element reader (nested.ids, promoteLeaf:false)
        // cannot promote the pre-widening narrow file, so the enforcer refuses to apply — fail-closed.
        StructType table = Schema(Field("nums", new ArrayType(DataTypes.IntegerType, true), nullable: true));
        StructType write = Schema(Field("nums", new ArrayType(DataTypes.LongType, true), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null,
                typeWideningEnabled: true, columnMappingMode: ColumnMappingMode.Id));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("nums.element", ex.Path);
        Assert.Contains("'id'-mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_MapValueWidening_IdMode_FailsClosed_AsTypeWideningUnsupported()
    {
        StructType table = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType), nullable: true));
        StructType write = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null,
                typeWideningEnabled: true, columnMappingMode: ColumnMappingMode.Id));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("lookup.value", ex.Path);
        Assert.Contains("'id'-mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_MapKeyWidening_IdMode_FailsClosed_AsTypeWideningUnsupported()
    {
        StructType table = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.IntegerType, DataTypes.StringType), nullable: true));
        StructType write = Schema(
            Field("lookup", DataTypes.CreateMapType(DataTypes.LongType, DataTypes.StringType), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null,
                typeWideningEnabled: true, columnMappingMode: ColumnMappingMode.Id));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("lookup.key", ex.Path);
        Assert.Contains("'id'-mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_StructChildWidening_IdMode_FailsClosed_AsTypeWideningUnsupported()
    {
        // struct<zip:int> → struct<zip:long> under id mode: the id-mode struct-child reader
        // (ResolveStructFieldById, promoteLeaf:false) cannot promote the pre-widening narrow file — the
        // struct-child (depth 1) widen is refused fail-closed, mirroring the nested-collection interior.
        StructType tableInner = Schema(Field("zip", DataTypes.IntegerType, nullable: true));
        StructType writeInner = Schema(Field("zip", DataTypes.LongType, nullable: true));
        StructType table = Schema(Field("address", tableInner, nullable: true));
        StructType write = Schema(Field("address", writeInner, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null,
                typeWideningEnabled: true, columnMappingMode: ColumnMappingMode.Id));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("address.zip", ex.Path);
        Assert.Contains("'id'-mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_TopLevelScalarWidening_IdMode_IsStillApplied()
    {
        // CONTROL (Step-0 probe 1): a TOP-LEVEL scalar int→long under id mode is READ-PROMOTED by the flat
        // reader (by field_id, on the promotion gate alone), so the enforcer STILL APPLIES it — the id-mode
        // guard is scoped to nested leaves ONLY and must not over-block a genuinely-readable widen.
        StructType table = Schema(Field("value", DataTypes.IntegerType, nullable: true));
        StructType write = Schema(Field("value", DataTypes.LongType, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null,
            typeWideningEnabled: true, columnMappingMode: ColumnMappingMode.Id);

        Assert.NotNull(merged);
        StructField field = merged!["value"];
        Assert.Equal(DataTypes.LongType, field.DataType);
        AssertSingleTypeChange(field.Metadata, "integer", "long");
    }

    [Fact]
    public void Reconcile_ArrayElementWidening_NameMode_IsStillApplied()
    {
        // CONTROL: name mode is unaffected — the array element widen still applies and records
        // fieldPath="element" exactly as #546/#860 (the name-mode reader promotes a nested leaf by physical
        // name, so the round-trip stays readable).
        StructType table = Schema(Field("nums", new ArrayType(DataTypes.IntegerType, true), nullable: true));
        StructType write = Schema(Field("nums", new ArrayType(DataTypes.LongType, true), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null,
            typeWideningEnabled: true, columnMappingMode: ColumnMappingMode.Name);

        Assert.NotNull(merged);
        StructField field = merged!["nums"];
        Assert.Equal(DataTypes.LongType, Assert.IsType<ArrayType>(field.DataType).ElementType);
        AssertSingleNestedTypeChange(field.Metadata, "integer", "long", "element");
    }

    [Fact]
    public void Reconcile_ArrayElementWidening_NoneMode_IsStillApplied()
    {
        // CONTROL: none mode still applies the array element widen — proves the enforcer's name/none behavior
        // is byte-for-byte the pre-#870 path (the #870 id-mode guard is scoped to columnMappingMode == Id).
        // (#8: columnMappingMode is now a REQUIRED parameter with no silent default, so callers must state the
        // mode explicitly; None here is the name/none path.)
        StructType table = Schema(Field("nums", new ArrayType(DataTypes.IntegerType, true), nullable: true));
        StructType write = Schema(Field("nums", new ArrayType(DataTypes.LongType, true), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["nums"];
        Assert.Equal(DataTypes.LongType, Assert.IsType<ArrayType>(field.DataType).ElementType);
        AssertSingleNestedTypeChange(field.Metadata, "integer", "long", "element");
    }

    [Fact]
    public void Reconcile_ArrayElementWidening_IdMode_WhenFeatureDisabled_StaysGenericTypeWideningUnsupported()
    {
        // When the feature is DISABLED the id-mode path never reaches the new guard (the apply arm is not
        // entered): the array element widen is rejected by the pre-existing generic TypeWideningUnsupported,
        // unchanged — the #870 guard is purely about a widen the reader can't honor WHEN it would be applied.
        StructType table = Schema(Field("nums", new ArrayType(DataTypes.IntegerType, true), nullable: true));
        StructType write = Schema(Field("nums", new ArrayType(DataTypes.LongType, true), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null,
                typeWideningEnabled: false, columnMappingMode: ColumnMappingMode.Id));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        // The disabled-feature message names the enablement requirement (not the id-mode-specific one).
        Assert.DoesNotContain("'id'-mode", ex.Message, StringComparison.Ordinal);
    }

    // ---- #870 future-proofing: the id-mode guard at depth ≥ 2 MIXED (struct + collection) nesting ----------
    // The pinned #870 cells above exercise depth-1 collection leaves (array element / map key-value) and a
    // depth-1 struct child. These add DEEPER mixed shapes so a future change to the recursion cannot silently
    // reopen the id-mode hole at an interleaved struct/collection position.

    [Fact]
    public void Reconcile_StructNestedArrayElementWidening_IdMode_FailsClosed_AsTypeWideningUnsupported()
    {
        // struct<x: array<int>> → struct<x: array<long>> under id mode. The widened leaf is an ARRAY ELEMENT
        // nested one level below a struct field (depth 2), bound by the id-mode nested reader by field_id with
        // promoteLeaf:false — it cannot read-promote the pre-widening narrow file, so the enforcer must refuse
        // to APPLY the widen (fail-closed) rather than mint an unreadable table.
        StructType tableInner = Schema(Field("x", new ArrayType(DataTypes.IntegerType, true), nullable: true));
        StructType writeInner = Schema(Field("x", new ArrayType(DataTypes.LongType, true), nullable: true));
        StructType table = Schema(Field("s", tableInner, nullable: true));
        StructType write = Schema(Field("s", writeInner, nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.Id, partitionColumns: null,
                typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("s.x.element", ex.Path);
        Assert.Contains("'id'-mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_StructNestedArrayElementWidening_NameAndNoneModes_AreApplied()
    {
        // CONTROL for the mixed depth-2 shape: a collection-element widen is APPLIED at ANY depth in name/none
        // mode (585b lifted the depth cap for array/map elements), recording fieldPath="element" on the inner
        // 'x' field — the name/none reader promotes a nested leaf by physical name, so the round-trip is
        // readable. The #870 guard must not over-block these genuinely-readable widenings.
        StructType tableInner = Schema(Field("x", new ArrayType(DataTypes.IntegerType, true), nullable: true));
        StructType writeInner = Schema(Field("x", new ArrayType(DataTypes.LongType, true), nullable: true));
        StructType table = Schema(Field("s", tableInner, nullable: true));
        StructType write = Schema(Field("s", writeInner, nullable: true));

        foreach (ColumnMappingMode mode in new[] { ColumnMappingMode.Name, ColumnMappingMode.None })
        {
            StructType? merged = DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, mode, partitionColumns: null,
                typeWideningEnabled: true);

            Assert.NotNull(merged);
            StructType mergedInner = Assert.IsType<StructType>(merged!["s"].DataType);
            StructField xField = mergedInner["x"];
            Assert.Equal(DataTypes.LongType, Assert.IsType<ArrayType>(xField.DataType).ElementType);
            AssertSingleNestedTypeChange(xField.Metadata, "integer", "long", "element");
        }
    }

    [Fact]
    public void Reconcile_ArrayOfStructChildWidening_IdMode_FailsClosed_AsTypeWideningUnsupported()
    {
        // array<struct<a:int>> → array<struct<a:long>> under id mode. The widened leaf is a STRUCT CHILD nested
        // one level below an array element (depth 2); the id-mode struct-child reader (ResolveStructFieldById,
        // promoteLeaf:false) cannot promote the pre-widening narrow file, so the widen fails closed with the
        // id-mode-specific reason.
        StructType tableInner = Schema(Field("a", DataTypes.IntegerType, nullable: true));
        StructType writeInner = Schema(Field("a", DataTypes.LongType, nullable: true));
        StructType table = Schema(Field("rows", new ArrayType(tableInner, true), nullable: true));
        StructType write = Schema(Field("rows", new ArrayType(writeInner, true), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.Id, partitionColumns: null,
                typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("rows.element.a", ex.Path);
        Assert.Contains("'id'-mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_ArrayOfStructChildWidening_NameMode_FailsClosed_AtDepth2StructChildBoundary()
    {
        // BOUNDARY (documents the CURRENT limit, not the id-mode guard): a STRUCT-CHILD scalar widen at
        // depth ≥ 2 stays fail-closed in name/none mode too — 585b lifted the depth cap for COLLECTION elements
        // (array element / map key-value), but a struct child at depth ≥ 2 is still refused because #571's
        // reader does not promote it. So this shape is rejected as the GENERIC TypeWideningUnsupported (no
        // id-mode reason) in name mode — distinct from the id-mode reject above, which future-proofs that the
        // two paths keep producing distinct messages at deeper mixed nesting.
        StructType tableInner = Schema(Field("a", DataTypes.IntegerType, nullable: true));
        StructType writeInner = Schema(Field("a", DataTypes.LongType, nullable: true));
        StructType table = Schema(Field("rows", new ArrayType(tableInner, true), nullable: true));
        StructType write = Schema(Field("rows", new ArrayType(writeInner, true), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.Name, partitionColumns: null,
                typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("rows.element.a", ex.Path);
        Assert.DoesNotContain("'id'-mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_ArrayElementDecimalGrowOnlyWidening_WhenEnabled_IsAppliedWithElementPath()
    {
        // #546: a grow-only decimal widening of a top-level array element is applied with fieldPath="element".
        DecimalType from = DataTypes.CreateDecimalType(10, 2);
        DecimalType to = DataTypes.CreateDecimalType(12, 4);
        StructType table = Schema(Field("amounts", DataTypes.CreateArrayType(from), nullable: true));
        StructType write = Schema(Field("amounts", DataTypes.CreateArrayType(to), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["amounts"];
        ArrayType mergedArray = Assert.IsType<ArrayType>(field.DataType);
        Assert.Equal(to, mergedArray.ElementType);
        AssertSingleNestedTypeChange(field.Metadata, from.TypeName, to.TypeName, "element");
    }

    [Fact]
    public void Reconcile_ArrayElementWidening_PreservesPriorNestedTypeChangeHistory_OldestFirst()
    {
        // #546 + the full-history rule: an array element already widened once (short→int, recorded with
        // fieldPath="element") widens again (int→long): the new entry appends after the prior one, oldest
        // first, both carrying fieldPath="element".
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
                        new KeyValuePair<string, string>("fieldPath", "element"),
                    })),
                })),
        });
        StructType table = Schema(new StructField(
            "nums", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true, priorHistory));
        StructType write = Schema(Field("nums", DataTypes.CreateArrayType(DataTypes.LongType), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["nums"];
        Assert.True(field.Metadata.TryGetValue("delta.typeChanges", out MetadataValue? changes));
        Assert.True(changes!.TryGetArray(out IReadOnlyList<MetadataValue>? entries));
        Assert.Equal(2, entries!.Count);
        AssertNestedTypeChangeEntry(entries[0], "short", "integer", "element");
        AssertNestedTypeChangeEntry(entries[1], "integer", "long", "element");
    }

    [Fact]
    public void Reconcile_ArrayElementCrossFamilyWidening_WhenEnabled_IsDeferred_NotApplied()
    {
        // #546 parity with the scalar path: a CROSS-FAMILY widening (#535: int→double) is read-promotable but
        // NOT schema-evolution-eligible, so it is NOT auto-applied on append at a nested position either —
        // rejected fail-closed as TypeWideningUnsupported, exactly like the scalar column path.
        StructType table = Schema(Field("nums", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true));
        StructType write = Schema(Field("nums", DataTypes.CreateArrayType(DataTypes.DoubleType), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("nums.element", ex.Path);
    }

    [Fact]
    public void Reconcile_WideningInsideArrayOfStruct_IsNotApplied_EvenWhenEnabled()
    {
        // #546 boundary: a leaf nested WITHIN another nested type (array<struct<scalar>>) stays fail-closed
        // even when the feature is enabled — #571's reader cannot promote a struct inside an array, so
        // applying the widening would mint an unreadable table. Rejected as TypeWideningUnsupported, closing
        // the pre-#546 latent gap where this widening was silently accepted but unreadable. (Tracked as a
        // follow-up: widening inside nested-within-nested shapes.)
        StructType tableElement = Schema(Field("zip", DataTypes.IntegerType, nullable: true));
        StructType writeElement = Schema(Field("zip", DataTypes.LongType, nullable: true));
        StructType table = Schema(
            Field("items", DataTypes.CreateArrayType(tableElement), nullable: true));
        StructType write = Schema(
            Field("items", DataTypes.CreateArrayType(writeElement), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("items.element.zip", ex.Path);
    }

    [Fact]
    public void Reconcile_WideningInsideMapValueStruct_IsNotApplied_EvenWhenEnabled()
    {
        // #546 boundary: a leaf nested within a map value struct (map<*, struct<scalar>>) stays fail-closed.
        StructType tableValue = Schema(Field("zip", DataTypes.IntegerType, nullable: true));
        StructType writeValue = Schema(Field("zip", DataTypes.LongType, nullable: true));
        StructType table = Schema(Field(
            "lookup", DataTypes.CreateMapType(DataTypes.StringType, tableValue), nullable: true));
        StructType write = Schema(Field(
            "lookup", DataTypes.CreateMapType(DataTypes.StringType, writeValue), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("lookup.value.zip", ex.Path);
    }

    // ---- #860 (585b): depth>1 nested widening applied with the fieldPath CHAIN (design §2.5 / §3.2) -----
    // The widening machinery (allowlist, fieldPath emission, NestedTypeChange) is reused verbatim; 585b lifts
    // the MergeCollectionElement depth cap (E3) and threads the accumulated fieldPath chain (E1/E2).

    [Fact]
    public void Widen_ArrayOfArrayElement_IntToLong_AppendApplies_EmitsFieldPath_element_element()
    {
        // Cell 1 (AC2): array<array<int>> → array<array<long>> applies at depth 2, recording the chain
        // "element.element" on the enclosing field (outermost-first, tokens joined by '.').
        StructType table = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.IntegerType, true), true), nullable: true));
        StructType write = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.LongType, true), true), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["x"];
        ArrayType outer = Assert.IsType<ArrayType>(field.DataType);
        ArrayType inner = Assert.IsType<ArrayType>(outer.ElementType);
        Assert.Equal(DataTypes.LongType, inner.ElementType);
        AssertSingleNestedTypeChange(field.Metadata, "integer", "long", "element.element");
    }

    [Fact]
    public void Widen_MapValueArrayElement_IntToLong_EmitsFieldPath_value_element()
    {
        // Cell 2 (AC2): map<string, array<int>> → map<string, array<long>> → "value.element".
        StructType table = Schema(Field(
            "m", DataTypes.CreateMapType(DataTypes.StringType, new ArrayType(DataTypes.IntegerType, true)),
            nullable: true));
        StructType write = Schema(Field(
            "m", DataTypes.CreateMapType(DataTypes.StringType, new ArrayType(DataTypes.LongType, true)),
            nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["m"];
        MapType map = Assert.IsType<MapType>(field.DataType);
        Assert.Equal(DataTypes.LongType, Assert.IsType<ArrayType>(map.ValueType).ElementType);
        AssertSingleNestedTypeChange(field.Metadata, "integer", "long", "value.element");
    }

    [Fact]
    public void Widen_ArrayOfMapValue_IntToLong_EmitsFieldPath_element_value()
    {
        // Cell 3 (AC2): array<map<string,int>> → array<map<string,long>> → "element.value".
        StructType table = Schema(Field(
            "a", new ArrayType(DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType), true),
            nullable: true));
        StructType write = Schema(Field(
            "a", new ArrayType(DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType), true),
            nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["a"];
        MapType map = Assert.IsType<MapType>(Assert.IsType<ArrayType>(field.DataType).ElementType);
        Assert.Equal(DataTypes.LongType, map.ValueType);
        AssertSingleNestedTypeChange(field.Metadata, "integer", "long", "element.value");
    }

    [Fact]
    public void Widen_MapKeyArrayElement_IntToLong_EmitsFieldPath_key_element()
    {
        // Cell 4 (AC2) — the KEY-side chain. DeltaSharp forbids a map-TYPED key (MapType rejects it), so the
        // design's `map<map<…>>` → "key.key" is not constructible; the feasible key-side chain uses an
        // ARRAY-typed key: map<array<int>, string> → map<array<long>, string> → "key.element" (the "key" token
        // composes into a chain exactly like "value"/"element"). See the delivery report for this substitution.
        StructType table = Schema(Field(
            "m",
            DataTypes.CreateMapType(new ArrayType(DataTypes.IntegerType, true), DataTypes.StringType),
            nullable: true));
        StructType write = Schema(Field(
            "m",
            DataTypes.CreateMapType(new ArrayType(DataTypes.LongType, true), DataTypes.StringType),
            nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["m"];
        MapType map = Assert.IsType<MapType>(field.DataType);
        Assert.Equal(DataTypes.LongType, Assert.IsType<ArrayType>(map.KeyType).ElementType);
        AssertSingleNestedTypeChange(field.Metadata, "integer", "long", "key.element");
    }

    [Fact]
    public void Widen_ArrayOfMapKey_IntToLong_EmitsFieldPath_element_key()
    {
        // Cell 4b (AC2) — the map-KEY chain with "key" INNERMOST (the readable counterpart of cell 4's
        // "key.element"): array<map<int,string>> → array<map<long,string>> → "element.key". Map-key widening
        // is a sanctioned same-family widening (#546 Reconcile_MapKeyWidening applies it at depth 1 with
        // fieldPath="key"); 585b composes the "key" token into the chain at depth 2. int→long keys are injective.
        StructType table = Schema(Field(
            "a",
            new ArrayType(DataTypes.CreateMapType(DataTypes.IntegerType, DataTypes.StringType), true),
            nullable: true));
        StructType write = Schema(Field(
            "a",
            new ArrayType(DataTypes.CreateMapType(DataTypes.LongType, DataTypes.StringType), true),
            nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["a"];
        MapType map = Assert.IsType<MapType>(Assert.IsType<ArrayType>(field.DataType).ElementType);
        Assert.Equal(DataTypes.LongType, map.KeyType);
        AssertSingleNestedTypeChange(field.Metadata, "integer", "long", "element.key");
    }

    [Fact]
    public void Widen_ArrayOfArrayOfArrayElement_Depth3_EmitsFieldPath_element_element_element()
    {
        // Cell 5 (AC2/AC4): depth-3 array<array<array<int>>> → "element.element.element" — pins the
        // fieldPath ACCUMULATOR (one token per array/map descent), not a two-token special-case.
        StructType table = Schema(Field(
            "x",
            new ArrayType(new ArrayType(new ArrayType(DataTypes.IntegerType, true), true), true),
            nullable: true));
        StructType write = Schema(Field(
            "x",
            new ArrayType(new ArrayType(new ArrayType(DataTypes.LongType, true), true), true),
            nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["x"];
        AssertSingleNestedTypeChange(field.Metadata, "integer", "long", "element.element.element");
    }

    [Fact]
    public void Widen_MapValueArrayArray_Depth3_EmitsFieldPath_value_element_element()
    {
        // Cell 5b (AC2/AC4): a MIXED depth-3 chain — map<string, array<array<int→long>>> → "value.element.element"
        // — complements the pure "element.element.element", pinning the accumulator threads through map AND array.
        StructType table = Schema(Field(
            "m",
            DataTypes.CreateMapType(
                DataTypes.StringType, new ArrayType(new ArrayType(DataTypes.IntegerType, true), true)),
            nullable: true));
        StructType write = Schema(Field(
            "m",
            DataTypes.CreateMapType(
                DataTypes.StringType, new ArrayType(new ArrayType(DataTypes.LongType, true), true)),
            nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        AssertSingleNestedTypeChange(merged!["m"].Metadata, "integer", "long", "value.element.element");
    }

    [Fact]
    public void Widen_ArrayOfArrayElement_FloatToDouble_Depth2_AppliesWithChain()
    {
        // Cell 1-float (AC2/AC3): same-family float→double applies at depth 2 → "element.element".
        StructType table = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.FloatType, true), true), nullable: true));
        StructType write = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.DoubleType, true), true), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["x"];
        Assert.Equal(DataTypes.DoubleType, Assert.IsType<ArrayType>(Assert.IsType<ArrayType>(field.DataType).ElementType).ElementType);
        AssertSingleNestedTypeChange(field.Metadata, "float", "double", "element.element");
    }

    [Fact]
    public void Widen_ArrayOfArrayElement_DateToTimestampNtz_Depth2_AppliesWithChain()
    {
        // Cell 1-date (AC2/AC3): temporal date→timestamp_ntz (#533) is schema-evolution-eligible, applies at
        // depth 2 → "element.element".
        StructType table = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.DateType, true), true), nullable: true));
        StructType write = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.TimestampNtzType, true), true), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        AssertSingleNestedTypeChange(
            merged!["x"].Metadata, DataTypes.DateType.TypeName, DataTypes.TimestampNtzType.TypeName, "element.element");
    }

    [Fact]
    public void Widen_ArrayOfArrayElement_DecimalGrowOnlyFits_Depth2_AppliesWithChain()
    {
        // Cell 1-decimal (AC2/AC3): grow-only decimal(10,2) → decimal(12,2) (integer digits 8→10 grow, scale
        // equal) is a sanctioned same-family widening, applies at depth 2 → "element.element".
        DecimalType from = DataTypes.CreateDecimalType(10, 2);
        DecimalType to = DataTypes.CreateDecimalType(12, 2);
        StructType table = Schema(Field(
            "x", new ArrayType(new ArrayType(from, true), true), nullable: true));
        StructType write = Schema(Field(
            "x", new ArrayType(new ArrayType(to, true), true), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        AssertSingleNestedTypeChange(merged!["x"].Metadata, from.TypeName, to.TypeName, "element.element");
    }

    [Fact]
    public void Widen_ArrayOfArrayElement_DecimalGrowBeyondFit_Depth2_FailsClosed()
    {
        // Cell 15b (AC3) — the decimal grow-BEYOND-fit (fit-guard) branch at depth 2: decimal(10,2) →
        // decimal(10,4) grows the scale (2→4) but SHRINKS the integer-digit range p−s (8→6), so it is NOT
        // grow-only and NOT sanctioned at all → IncompatibleType (verified against IsSameFamilyWidening's
        // grow-only guard: (p'−s') ≥ (p−s) fails). Distinct from cross-family sanctioned-but-not-evolution.
        DecimalType from = DataTypes.CreateDecimalType(10, 2);
        DecimalType to = DataTypes.CreateDecimalType(10, 4);
        StructType table = Schema(Field(
            "x", new ArrayType(new ArrayType(from, true), true), nullable: true));
        StructType write = Schema(Field(
            "x", new ArrayType(new ArrayType(to, true), true), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Widen_ArrayOfMap_KeyAndValue_IntToLong_Depth2_RecordsBothChains()
    {
        // Cell 4c (AC2): a map inside an array with BOTH key AND value widened int→long — array<map<int,int>>
        // → array<map<long,long>> records TWO delta.typeChanges on the enclosing array field, with chains
        // "element.key" (merge-order first) then "element.value". (map<map<…>> "key.key" is infeasible.)
        StructType table = Schema(Field(
            "a", new ArrayType(DataTypes.CreateMapType(DataTypes.IntegerType, DataTypes.IntegerType), true),
            nullable: true));
        StructType write = Schema(Field(
            "a", new ArrayType(DataTypes.CreateMapType(DataTypes.LongType, DataTypes.LongType), true),
            nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField field = merged!["a"];
        MapType map = Assert.IsType<MapType>(Assert.IsType<ArrayType>(field.DataType).ElementType);
        Assert.Equal(DataTypes.LongType, map.KeyType);
        Assert.Equal(DataTypes.LongType, map.ValueType);
        Assert.True(field.Metadata.TryGetValue("delta.typeChanges", out MetadataValue? changes));
        Assert.True(changes!.TryGetArray(out IReadOnlyList<MetadataValue>? entries));
        Assert.Equal(2, entries!.Count);
        AssertNestedTypeChangeEntry(entries[0], "integer", "long", "element.key");
        AssertNestedTypeChangeEntry(entries[1], "integer", "long", "element.value");
    }

    [Fact]
    public void Widen_StructChildArrayElement_EmitsElementFieldPath_OnChildStructField()
    {
        // Cell 6 (AC2): struct<xs:array<int>> → struct<xs:array<long>> — the change is recorded on the INNER
        // `xs` StructField with fieldPath="element" (the struct boundary resets the chain to a fresh namespace),
        // and the enclosing `s` struct field carries NO typeChanges.
        StructType tableInner = Schema(Field("xs", new ArrayType(DataTypes.IntegerType, true), nullable: true));
        StructType writeInner = Schema(Field("xs", new ArrayType(DataTypes.LongType, true), nullable: true));
        StructType table = Schema(Field("s", tableInner, nullable: true));
        StructType write = Schema(Field("s", writeInner, nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        StructField sField = merged!["s"];
        StructType sInner = Assert.IsType<StructType>(sField.DataType);
        StructField xs = sInner["xs"];
        Assert.Equal(DataTypes.LongType, Assert.IsType<ArrayType>(xs.DataType).ElementType);
        AssertSingleNestedTypeChange(xs.Metadata, "integer", "long", "element");
        Assert.False(sField.Metadata.TryGetValue("delta.typeChanges", out _));
    }

    [Fact]
    public void Widen_ArrayOfStructField_StaysFailClosedOnAppend_D9_ReaderOverPermissive()
    {
        // Cell 7 (AC2) — DEVIATION from the §3.2 cell-7 PROSE, faithful to decision D9 + edits E1–E3. The
        // design's cell-7 text expects array<struct<a:int>> → array<struct<a:long>> to APPLY on the inner `a`
        // StructField, but E1–E3 leave MergeType's default scalar arm (`depth <= 1`) UNCHANGED per D9 (a struct
        // child re-enters MergeField and reaches the default arm at depth 2), so on APPEND this stays fail-closed
        // TypeWideningUnsupported — identical to the pre-existing #546 boundary
        // (Reconcile_WideningInsideArrayOfStruct_IsNotApplied_EvenWhenEnabled). The READER, by contrast, DOES
        // promote this shape (NestedParquetReadTests.Array_OfStruct_InnerFieldWidening_AtDepth2_..._Promotes),
        // the D9 "safe over-permissive read" (reader ⊇ enforcer). See the delivery report for the deviation.
        StructType tableInner = Schema(Field("a", DataTypes.IntegerType, nullable: true));
        StructType writeInner = Schema(Field("a", DataTypes.LongType, nullable: true));
        StructType table = Schema(Field("items", new ArrayType(tableInner, true), nullable: true));
        StructType write = Schema(Field("items", new ArrayType(writeInner, true), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("items.element.a", ex.Path);
    }

    [Fact]
    public void Widen_CrossFamily_IntToDouble_AtDepth2_AppendFailsClosed()
    {
        // Cell 14 (AC3): cross-family int→double is read-promotable but NOT schema-evolution-eligible at ANY
        // depth — array<array<int>> → array<array<double>> falls through to TypeWideningUnsupported.
        StructType table = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.IntegerType, true), true), nullable: true));
        StructType write = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.DoubleType, true), true), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal("x.element.element", ex.Path);
    }

    [Fact]
    public void Widen_DecimalNonGrowOnly_AtDepth2_FailsClosed()
    {
        // Cell 15 (AC3): the decimal-fit / grow-only guard inside the widening predicate is reused unchanged at
        // depth 2. A non-grow-only decimal change of a nested leaf (decimal(10,2) → decimal(9,2), precision
        // shrinks) is not a sanctioned widening → IncompatibleType (matching the depth-1 reused-predicate
        // behavior, Reconcile_DecimalNarrowing_WhenEnabled_IsStillRejected). The design's cell-15 label of
        // "TypeWideningUnsupported" was optimistic — a shrink is not sanctioned at all, so the reused predicate
        // classifies it IncompatibleType. See the delivery report.
        StructType table = Schema(Field(
            "m",
            DataTypes.CreateMapType(
                DataTypes.StringType, new ArrayType(DataTypes.CreateDecimalType(10, 2), true)),
            nullable: true));
        StructType write = Schema(Field(
            "m",
            DataTypes.CreateMapType(
                DataTypes.StringType, new ArrayType(DataTypes.CreateDecimalType(9, 2), true)),
            nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Widen_Narrowing_AtDepth2_FailsClosed_IncompatibleType()
    {
        // Cell 16a (AC3): a narrowing at depth 2 (array<array<long>> → array<array<int>>) is not a widening at
        // all → IncompatibleType.
        StructType table = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.LongType, true), true), nullable: true));
        StructType write = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.IntegerType, true), true), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Widen_CrossKind_AtDepth2_FailsClosed_IncompatibleType()
    {
        // Cell 16b (AC3): a cross-kind change at depth 2 (array<array<int>> → array<array<string>>) →
        // IncompatibleType.
        StructType table = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.IntegerType, true), true), nullable: true));
        StructType write = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.StringType, true), true), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
    }

    [Fact]
    public void Widen_AtDepth2_FeatureDisabled_FailsClosed()
    {
        // Cell 17 (AC3): with the typeWidening feature OFF, an otherwise-sanctioned depth-2 widening is not
        // auto-applied → TypeWideningUnsupported.
        StructType table = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.IntegerType, true), true), nullable: true));
        StructType write = Schema(Field(
            "x", new ArrayType(new ArrayType(DataTypes.LongType, true), true), nullable: true));

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: false));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
    }

    [Fact]
    public void Widen_Depth1_ByteIdentical_After585b_SingleToken()
    {
        // Cell 21 (AC4): the #546 depth-1 append behavior is unchanged — a top-level array<int> → array<long>
        // still records a SINGLE-token fieldPath "element" (Combine(null,"element") == "element"), and the
        // depth-1 gate still applies. Pins that 585b's gate lift + chain accumulation preserve depth-1 behavior.
        StructType table = Schema(Field("nums", new ArrayType(DataTypes.IntegerType, true), nullable: true));
        StructType write = Schema(Field("nums", new ArrayType(DataTypes.LongType, true), nullable: true));

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, ColumnMappingMode.None, partitionColumns: null, typeWideningEnabled: true);

        Assert.NotNull(merged);
        AssertSingleNestedTypeChange(merged!["nums"].Metadata, "integer", "long", "element");
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

    // Asserts the field carries EXACTLY one delta.typeChanges entry: {fromType, toType, fieldPath} — a nested
    // (array element / map key-value) widening recorded on the enclosing field with its Delta fieldPath (#546).
    private static void AssertSingleNestedTypeChange(
        FieldMetadata metadata, string fromType, string toType, string fieldPath)
    {
        Assert.True(metadata.TryGetValue("delta.typeChanges", out MetadataValue? changes));
        Assert.True(changes!.TryGetArray(out IReadOnlyList<MetadataValue>? entries));
        MetadataValue only = Assert.Single(entries!);
        AssertNestedTypeChangeEntry(only, fromType, toType, fieldPath);
    }

    private static void AssertNestedTypeChangeEntry(
        MetadataValue entry, string fromType, string toType, string fieldPath)
    {
        Assert.True(entry.TryGetNested(out FieldMetadata? nested));
        Assert.True(nested!.TryGetString("fromType", out string? actualFrom));
        Assert.True(nested.TryGetString("toType", out string? actualTo));
        Assert.True(nested.TryGetString("fieldPath", out string? actualPath));
        Assert.Equal(fromType, actualFrom);
        Assert.Equal(toType, actualTo);
        Assert.Equal(fieldPath, actualPath);
    }
}
