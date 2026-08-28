using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet.Schema;
using Xunit;
using static DeltaSharp.Storage.Tests.Parquet.NestedValueModel;
using ColumnSegment = DeltaSharp.Storage.Parquet.NestedColumnShredder.ColumnSegment;
using PqDataField = Parquet.Schema.DataField;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// #873 §3.3 fail-closed matrix for the nested-within-nested WRITE door (cells 12–20, 27) and the per-level
/// structural guard's phantom-continuation cells (28, 29). Every schema-shape reject fires at
/// <see cref="ParquetTypeMapping.CreateField"/> (before any byte); every value/level reject fires in the N9
/// pre-pass (<see cref="NestedColumnShredder.ValidateColumnAsync"/>), also before any byte.
/// </summary>
public sealed class NestedWithinNestedRejectTests
{
    private static readonly StructType StructA = DataTypes.CreateStructType(new[]
    {
        new StructField("a", DataTypes.IntegerType, nullable: true),
    });

    // 12 — array<struct<x:void>>: an unsupported leaf (void) at depth.
    [Fact]
    public void Write_NestedLeaf_VoidType_FailsClosed() =>
        AssertCreateFieldRejects(
            DataTypes.CreateArrayType(DataTypes.CreateStructType(new[]
            {
                new StructField("x", DataTypes.NullType, nullable: true),
            })));

    // 13 — map<string, struct<d:decimal(29,2)>>: decimal precision > 28 at depth.
    [Fact]
    public void Write_NestedLeaf_DecimalPrecision29_FailsClosed() =>
        AssertCreateFieldRejects(
            DataTypes.CreateMapType(
                DataTypes.StringType,
                DataTypes.CreateStructType(new[]
                {
                    new StructField("d", DataTypes.CreateDecimalType(29, 2), nullable: true),
                })));

    // 14 — array<struct<>>: a zero-field struct at depth 2.
    [Fact]
    public void Write_ZeroFieldStruct_AtDepth2_FailsClosed() =>
        AssertCreateFieldRejects(
            DataTypes.CreateArrayType(DataTypes.CreateStructType(Array.Empty<StructField>())));

    // 15 — a non-nullable NESTED CONTAINER at depth (#730 parity): a non-null struct array element, and a
    // non-null map-value struct.
    [Fact]
    public void Write_NonNullableNestedContainer_AtDepth_FailsClosed()
    {
        AssertCreateFieldRejects(DataTypes.CreateArrayType(StructA, containsNull: false));
        AssertCreateFieldRejects(
            DataTypes.CreateMapType(DataTypes.StringType, StructA, valueContainsNull: false));
    }

    // 16 — array<struct<a:int NOT NULL>> with a null a: the required-lane guard fires at depth.
    [Fact]
    public async Task Write_RequiredNestedLeaf_HoldsNull_AtDepth_FailsClosed()
    {
        StructType inner = DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: false),
        });
        ArrayType type = DataTypes.CreateArrayType(inner);
        var rows = new object?[] { Arr(Struct((object?)null)) };

        DeltaStorageException error = await AssertWriteRejectsAsync(type, rows);
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    // 17 — a REQUIRED (non-null) map VALUE that holds null fails closed at depth (CorruptData); and the
    // structural null-map-KEY invariant is enforced at the #570 vector layer (a MapColumnVector cannot even be
    // constructed with a null key), which is a stronger guarantee than a shredder-level reject.
    [Fact]
    public async Task Write_NullMapKey_AtDepth_FailsClosed()
    {
        // A required scalar map value that holds null, nested inside an array: the required-lane guard fires
        // on the value leaf at depth.
        ArrayType requiredValue = DataTypes.CreateArrayType(
            DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType, valueContainsNull: false));
        var nullValueRows = new object?[] { Arr(Map(("k", (object?)null))) };
        Assert.Equal(
            StorageErrorKind.CorruptData, (await AssertWriteRejectsAsync(requiredValue, nullValueRows)).Kind);

        // A null map KEY cannot even be represented: the MapColumnVector invariant refuses it at construction,
        // so a null-key map never reaches the write door at all (#570).
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, 1);
        keys.AppendNull();
        MutableColumnVector values = ColumnVectors.Create(DataTypes.LongType, 1);
        values.AppendValue(1L);
        Assert.ThrowsAny<ArgumentException>(() => new MapColumnVector(
            DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType),
            keys, values, new[] { 0, 1 }, new[] { false }));
    }

    // 18 — id-mode array<struct<...>> (nested-within-nested) is re-pointed to #866, not #585.
    [Fact]
    public void Write_IdModeNestedWithinNested_FailsClosed_RepointedTo866()
    {
        var metadata = FieldMetadata.FromValues(new Dictionary<string, MetadataValue>
        {
            ["delta.columnMapping.id"] = MetadataValue.Long(7),
        });
        var field = new StructField("c", DataTypes.CreateArrayType(StructA), nullable: true, metadata);

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(field, honorReferenceNullability: true));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("#866", error.Message, StringComparison.Ordinal);
    }

    // 19 — an array chain past depth 64 fails closed BEFORE any byte (the schema-construction depth guard).
    [Fact]
    public void Write_SchemaDeeperThanMaxNestedWriteDepth_FailsClosed()
    {
        DataType type = DataTypes.IntegerType;
        for (int i = 0; i < 70; i++)
        {
            type = DataTypes.CreateArrayType(type);
        }

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(
                new StructField("c", type, nullable: true), honorReferenceNullability: true));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("64", error.Message, StringComparison.Ordinal);
    }

    // 20 — a foreign / mismatched nested vector at a nested level fails closed on the bounded KIND (no raw
    // library text). Declared array<struct<a:int>> but the element vector is a plain int array.
    [Fact]
    public async Task Write_ForeignNestedVector_FailsClosed()
    {
        ArrayType declared = DataTypes.CreateArrayType(StructA);
        Field field = ParquetTypeMapping.CreateField(
            new StructField("c", declared, nullable: true), honorReferenceNullability: true);

        // A ListColumnVector whose ELEMENTS are int, not the declared struct — a mismatched nested vector.
        ListColumnVector mismatched = NestedVectors.IntList(
            DataTypes.CreateArrayType(DataTypes.IntegerType), new int?[]?[] { new int?[] { 1, 2 } });
        var segments = new[] { new ColumnSegment(mismatched, 0, 1) };

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => NestedColumnShredder.ValidateColumnAsync(
                field, new StructField("c", declared, nullable: true), segments, 1,
                256L * 1024 * 1024, CancellationToken.None));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("struct", error.Message, StringComparison.Ordinal);
    }

    // 27 — a map whose KEY is a container fails closed at EVERY depth (nested map key is not physically
    // writable — the file would be permanently unreadable). Scalar-key maps are unaffected.
    [Fact]
    public void Write_NestedMapKey_FailsClosed_AtEveryDepth()
    {
        // map<array<int>, string>
        AssertCreateFieldRejects(
            DataTypes.CreateMapType(DataTypes.CreateArrayType(DataTypes.IntegerType), DataTypes.StringType));
        // map<struct<a:int>, string>
        AssertCreateFieldRejects(DataTypes.CreateMapType(StructA, DataTypes.StringType));
        // a nested-key map buried at depth 2: array<map<array<int>,string>>
        AssertCreateFieldRejects(
            DataTypes.CreateArrayType(
                DataTypes.CreateMapType(DataTypes.CreateArrayType(DataTypes.IntegerType), DataTypes.StringType)));

        // Companion success: a SCALAR-key map with a nested value is unaffected.
        Field ok = ParquetTypeMapping.CreateField(
            new StructField(
                "c", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.CreateArrayType(DataTypes.IntegerType)),
                nullable: true),
            honorReferenceNullability: true);
        Assert.IsType<global::Parquet.Schema.MapField>(ok);
    }

    // 27 (read proof) — the synthesized-footer companion: a hand-authored file with an OPTIONAL nested map key
    // is rejected by the shipped 585a reader (EnsureRequiredMapKey → SchemaMismatch), which is the read-side
    // proof that MOTIVATES the write-door reject. If Parquet.Net refuses to construct a nested-key map at all,
    // the write reject is subsumed (the file could never be authored); either way the nested key is
    // unwritable/unreadable.
    [Fact]
    public async Task ReadPromote_NestedMapKey_Unreadable_FailsClosed()
    {
        byte[]? bytes;
        try
        {
            bytes = await AuthorNestedKeyMapFileAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // Parquet.Net itself refuses the nested-key map construction — the file is not even authorable, so
            // the write-door reject is a fortiori correct. The read proof is subsumed.
            return;
        }

        var requested = DataTypes.CreateStructType(new[]
        {
            new StructField(
                "m",
                DataTypes.CreateMapType(DataTypes.CreateArrayType(DataTypes.IntegerType), DataTypes.LongType),
                nullable: true),
        });

        using var stream = new MemoryStream(bytes, writable: false);
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch _ in new ParquetFileReader().ReadAsync(
                stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
                CancellationToken.None))
            {
            }
        });
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    // 28 — the per-level guard catches an INNERMOST phantom continuation (a rep-2 slot continuing an inner list
    // that opened empty, and a rep-2 slot at def < presentDef_2) → CorruptData.
    [Fact]
    public void Write_LevelGuard_InnermostPhantomContinuation_FailsClosed()
    {
        (PqDataField leaf, RepeatedLevel[] chain) = ArrayOfArrayLeaf();

        // (0,3)(2,3): a rep-2 continuation after an EMPTY inner list (def 3 == emptyDef_2).
        AssertGuardRejects(leaf, chain, def: new[] { 3, 3 }, rep: new[] { 0, 2 }, valueCount: 0, rowCount: 1);

        // (0,5)(2,4)(2,3): a rep-2 slot at def 3 < presentDef_2 (4) after a present inner element.
        AssertGuardRejects(
            leaf, chain, def: new[] { 5, 4, 3 }, rep: new[] { 0, 2, 2 }, valueCount: 1, rowCount: 1);
    }

    // 29 — the per-level guard catches a SHALLOWER phantom continuation (a rep-1 slot continuing an outer list
    // that opened NULL / EMPTY) → CorruptData. This is the cell the round-2 rep==maxRep gate false-accepted.
    [Fact]
    public void Write_LevelGuard_ShallowerPhantomContinuation_FailsClosed()
    {
        (PqDataField leaf, RepeatedLevel[] chain) = ArrayOfArrayLeaf();

        // (0,0)(1,2): a rep-1 continuation into an outer list that opened NULL (def 0).
        AssertGuardRejects(leaf, chain, def: new[] { 0, 2 }, rep: new[] { 0, 1 }, valueCount: 0, rowCount: 1);

        // (0,1)(1,2): a rep-1 continuation into an outer list that opened EMPTY (def 1).
        AssertGuardRejects(leaf, chain, def: new[] { 1, 2 }, rep: new[] { 0, 1 }, valueCount: 0, rowCount: 1);
    }

    // ----- helpers -----

    private static void AssertCreateFieldRejects(DataType type)
    {
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(
                new StructField("c", type, nullable: true), honorReferenceNullability: true));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
    }

    private static async Task<DeltaStorageException> AssertWriteRejectsAsync(
        DataType nestedType, IReadOnlyList<object?> rows)
    {
        var schema = DataTypes.CreateStructType(new[] { new StructField("c", nestedType, nullable: true) });
        ColumnVector column = Build(nestedType, rows);
        var batch = new ManagedColumnBatch(schema, new[] { column }, rows.Count);
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }));
        return error;
    }

    private static (PqDataField Leaf, RepeatedLevel[] Chain) ArrayOfArrayLeaf()
    {
        var inner = new ListField("element", new global::Parquet.Schema.DataField<int?>("element"));
        var outer = new ListField("c", inner);
        _ = new ParquetSchema(outer);
        var innerList = (ListField)outer.Item;
        var leaf = (PqDataField)innerList.Item;
        var chain = new[]
        {
            new RepeatedLevel(outer.MaxRepetitionLevel, outer.MaxDefinitionLevel, 0),
            new RepeatedLevel(innerList.MaxRepetitionLevel, innerList.MaxDefinitionLevel, outer.MaxDefinitionLevel),
        };
        return (leaf, chain);
    }

    private static void AssertGuardRejects(
        PqDataField leaf, RepeatedLevel[] chain, int[] def, int[] rep, int valueCount, int rowCount)
    {
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => NestedLevelGuard.Validate(
                leaf, chain, def, rep, hasRepetitions: true, valueCount, rowCount, "c"));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    // Hand-authors a Parquet file with a MAP whose KEY is a nested LIST (a container), using Parquet.Net's
    // low-level writer. Parquet.Net emits every group node OPTIONAL, so the key node carries
    // Key.MaxDef > Map.MaxDef — the shape the 585a reader's EnsureRequiredMapKey rejects. May throw at
    // construction (Parquet.Net could refuse a group key), which the caller treats as "subsumed".
    private static async Task<byte[]> AuthorNestedKeyMapFileAsync()
    {
        var keyList = new ListField("key", new global::Parquet.Schema.DataField<int>("element"));
        var map = new MapField("m", keyList, new global::Parquet.Schema.DataField<long>("value"));
        var schema = new ParquetSchema(map);
        using var stream = new MemoryStream();
        await using (global::Parquet.ParquetWriter writer =
            await global::Parquet.ParquetWriter.CreateAsync(schema, stream))
        {
            using global::Parquet.ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            // Zero rows is enough — the reader rejects on the SCHEMA (EnsureRequiredMapKey) before reading data.
        }

        return stream.ToArray();
    }
}
