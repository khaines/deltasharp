using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet.Schema;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// The nested write door's FAIL-CLOSED matrix (§2.4a/§2.6/§2.7/§2.8) and the §2.3c pre-write level-invariant
/// guard's negative cells.
/// </summary>
/// <remarks>
/// Every case here is a shape or an encoding the writer must refuse <b>before any byte is published</b>. The
/// level-guard cells are fault-injected directly into <see cref="NestedLevelGuard"/> rather than provoked
/// through the shredder: the guard exists precisely to catch a shredder defect, so a test that could only
/// reach it through a correct shredder would be vacuous.
/// </remarks>
public sealed class NestedParquetWriteRejectTests
{
    private static readonly ArrayType IntArrayType = DataTypes.CreateArrayType(DataTypes.IntegerType);

    // ----- schema-shape rejects (§2.6 → #585, §2.4a) -----

    public static TheoryData<string, DataType, bool> OutOfScopeShapes() => new()
    {
        // Nested within nested — the #585 boundary, in all three container positions.
        { "array-of-array", DataTypes.CreateArrayType(DataTypes.CreateArrayType(DataTypes.LongType)), true },
        { "array-of-struct", DataTypes.CreateArrayType(
            DataTypes.CreateStructType(new[] { new StructField("x", DataTypes.LongType) })), true },
        { "array-of-map", DataTypes.CreateArrayType(
            DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType)), true },
        { "map-value-nested", DataTypes.CreateMapType(
            DataTypes.StringType, DataTypes.CreateArrayType(DataTypes.LongType)), true },
        { "map-key-nested", DataTypes.CreateMapType(
            DataTypes.CreateStructType(new[] { new StructField("x", DataTypes.LongType) }),
            DataTypes.LongType), true },
        { "struct-of-struct", DataTypes.CreateStructType(new[]
        {
            new StructField("x", DataTypes.LongType),
            new StructField(
                "y", DataTypes.CreateStructType(new[] { new StructField("z", DataTypes.LongType) })),
        }), true },
        { "struct-of-array", DataTypes.CreateStructType(new[]
        {
            new StructField("x", DataTypes.CreateArrayType(DataTypes.LongType)),
        }), true },

        // A zero-field struct (§2.4a/NEW-5): Parquet.Net's own ctor raises a raw ArgumentException for it.
        { "zero-field-struct", DataTypes.CreateStructType(Array.Empty<StructField>()), true },

        // A declared-REQUIRED nested container (§2.4a/#730): Parquet.Net emits every nested container as
        // OPTIONAL, so writing one would make the footer contradict the committed schemaString.
        { "required-array", DataTypes.CreateArrayType(DataTypes.LongType), false },
        { "required-map", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType), false },
        { "required-struct", DataTypes.CreateStructType(new[] { new StructField("x", DataTypes.LongType) }), false },

        // An unsupported LEAF inside an in-scope container: rejected on the same door, not silently widened.
        { "array-of-void", DataTypes.CreateArrayType(DataTypes.NullType), true },
    };

    [Theory]
    [MemberData(nameof(OutOfScopeShapes))]
    public void CreateField_RejectsOutOfScopeNestedShapes_FailClosed(string label, DataType type, bool nullable)
    {
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(
                new StructField("c", type, nullable), honorReferenceNullability: true));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.NotEmpty(label);
    }

    [Fact]
    public void CreateField_NestedShapeMessages_NeverEchoANestedFieldName()
    {
        // §2.8 diagnostic hygiene: the message identifies the offending child by ORDINAL and the offending
        // type by KIND, so no foreign nested field name (and no recursive SimpleString) reaches a log.
        var type = DataTypes.CreateStructType(new[]
        {
            new StructField("harmless", DataTypes.LongType),
            new StructField(
                "SECRET_CHILD_NAME",
                DataTypes.CreateStructType(new[] { new StructField("DEEPER_SECRET", DataTypes.LongType) })),
        });

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(
                new StructField("outer", type, nullable: true), honorReferenceNullability: true));

        // Asserted on ToString(), not Message: A2 attaches the raw cause as an inner exception, and ToString()
        // is the render an operator actually sees. Hygiene must hold on that whole rendering.
        string rendered = error.ToString();
        Assert.DoesNotContain("SECRET_CHILD_NAME", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("DEEPER_SECRET", rendered, StringComparison.Ordinal);
        Assert.Contains("'struct'", error.Message, StringComparison.Ordinal);
        Assert.Contains("outer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateField_RejectsNestedArrayColumnCarryingAColumnMappingId_DeferredTo839()
    {
        // §2.5/#839: an ARRAY (or map) column carrying delta.columnMapping.id under id mode is rejected at the
        // write door — its interior (element/key/value) is not a StructField and carries no representable
        // field_id, so id-mode nested array/map column mapping is deferred to #839. (A struct<scalar> container
        // carrying ids DOES write — its scalar children stamp their own field_id; see the sibling positive
        // test below.)
        var metadata = FieldMetadata.FromValues(new Dictionary<string, MetadataValue>
        {
            ["delta.columnMapping.id"] = MetadataValue.Long(7),
        });
        var field = new StructField("c", IntArrayType, nullable: true, metadata);

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(field, honorReferenceNullability: true));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("#839", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateField_IdModeStructWithStampedChildren_StampsEachChildLeafFieldId()
    {
        // §2.5/#676 positive: a struct<scalars> container whose children each carry a delta.columnMapping.id
        // writes successfully under id mode — each scalar child leaf is stamped with ITS OWN field_id (= the
        // child StructField's id). The container GROUP node carries no field_id (Parquet.Net exposes no public
        // setter — the container binds by physical name). The container's own id is structural-only, never
        // stamped on the wire.
        var inner = DataTypes.CreateStructType(new[]
        {
            new StructField(
                "a", DataTypes.LongType, nullable: true,
                FieldMetadata.FromValues(new Dictionary<string, MetadataValue>
                {
                    ["delta.columnMapping.id"] = MetadataValue.Long(11),
                })),
            new StructField(
                "b", DataTypes.LongType, nullable: true,
                FieldMetadata.FromValues(new Dictionary<string, MetadataValue>
                {
                    ["delta.columnMapping.id"] = MetadataValue.Long(12),
                })),
        });
        var container = new StructField(
            "s", inner, nullable: true,
            FieldMetadata.FromValues(new Dictionary<string, MetadataValue>
            {
                ["delta.columnMapping.id"] = MetadataValue.Long(2),
            }));

        var parquetStruct = (global::Parquet.Schema.StructField)ParquetTypeMapping.CreateField(
            container, honorReferenceNullability: true);

        var stampedIds = parquetStruct.Fields.Cast<DataField>().Select(f => f.FieldId).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 11, 12 }, stampedIds); // every struct-child leaf carries its own field_id
    }

    [Fact]
    public void CreateField_IdModeStructWithUnstampedChild_FailsClosed_UnstampedLeafUnreadable()
    {
        // §2.5/#676 write-door assertion: under id mode EVERY mapped struct child leaf MUST carry a stampable
        // delta.columnMapping.id — an unstamped leaf would commit a permanently-unreadable file. A struct whose
        // container carries an id but whose child does NOT fails closed rather than emitting an unreadable leaf.
        var inner = DataTypes.CreateStructType(new[]
        {
            new StructField(
                "a", DataTypes.LongType, nullable: true,
                FieldMetadata.FromValues(new Dictionary<string, MetadataValue>
                {
                    ["delta.columnMapping.id"] = MetadataValue.Long(11),
                })),
            new StructField("b", DataTypes.LongType, nullable: true), // NO id — the unstamped leaf
        });
        var container = new StructField(
            "s", inner, nullable: true,
            FieldMetadata.FromValues(new Dictionary<string, MetadataValue>
            {
                ["delta.columnMapping.id"] = MetadataValue.Long(2),
            }));

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(container, honorReferenceNullability: true));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("unstamped leaf would be unreadable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateField_StampsNoFieldIdOnANestedLeaf()
    {
        // §2.5/F9: the synthesized leaf StructFields carry no column-mapping metadata, so a nested leaf is
        // structurally never field_id-stamped — the property #676 will build on.
        var inner = DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: true),
            new StructField("b", DataTypes.StringType, nullable: true),
        });

        var parquetStruct = (global::Parquet.Schema.StructField)ParquetTypeMapping.CreateField(
            new StructField("s", inner, nullable: true), honorReferenceNullability: true);

        foreach (Field child in parquetStruct.Fields)
        {
            Assert.Equal(-1, ((DataField)child).FieldId);
        }
    }

    // ----- runtime value rejects (§2.4a required lane) -----

    [Fact]
    public async Task RequiredArrayElement_HoldingANull_FailsClosedBeforeAnyByte()
    {
        var type = DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: false);
        var schema = DataTypes.CreateStructType(new[] { new StructField("a", type, nullable: true) });
        ListColumnVector vector = NestedVectors.IntList(type, new int?[]?[] { new int?[] { 1, null } });

        DeltaStorageException error = await AssertWriteFailsAsync(schema, vector);
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("non-nullable element", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiredMapValue_HoldingANull_FailsClosedBeforeAnyByte()
    {
        var type = DataTypes.CreateMapType(
            DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: false);
        var schema = DataTypes.CreateStructType(new[] { new StructField("m", type, nullable: true) });
        MapColumnVector vector = NestedVectors.StringIntMap(
            type, new IReadOnlyList<(string Key, int? Value)>?[] { new[] { ("a", (int?)null) } });

        DeltaStorageException error = await AssertWriteFailsAsync(schema, vector);
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("non-nullable value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonNullableStructField_HoldingANull_FailsClosedBeforeAnyByte()
    {
        var inner = DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: false),
            new StructField("b", DataTypes.StringType, nullable: true),
        });
        var schema = DataTypes.CreateStructType(new[] { new StructField("s", inner, nullable: true) });
        StructColumnVector vector = NestedVectors.IntStringStruct(
            inner, new (int? A, string? B)?[] { (1, "ok"), (null, "boom") });

        DeltaStorageException error = await AssertWriteFailsAsync(schema, vector);
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("declared non-nullable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullMapKey_IsUnrepresentableAtTheVectorLayer()
    {
        // §2.4a's key lane is REQUIRED on the wire. MapColumnVector already refuses to hold a null key, so
        // the shredder's null-key guard is unreachable defence-in-depth rather than the primary control —
        // pinned here so a future relaxation of the vector invariant surfaces as a failure in THIS file,
        // next to the guard it would activate.
        var type = DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType);
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, 2);
        MutableColumnVector values = ColumnVectors.Create(DataTypes.IntegerType, 2);
        keys.AppendBytes(System.Text.Encoding.UTF8.GetBytes("a"));
        values.AppendValue(1);
        keys.AppendNull();
        values.AppendValue(2);

        Assert.Throws<ArgumentException>(
            () => new MapColumnVector(type, keys, values, new[] { 0, 2 }));
    }

    // ----- selection-vector reject (§2.8) -----

    [Fact]
    public async Task SelectionVectorOverANestedColumn_FailsClosedBeforeTheWriter()
    {
        var schema = DataTypes.CreateStructType(new[] { new StructField("a", IntArrayType, nullable: true) });
        var rows = new int?[]?[] { new int?[] { 1 }, null, new int?[] { 2 } };
        var batch = new ManagedColumnBatch(
            schema, new ColumnVector[] { NestedVectors.IntList(IntArrayType, rows) }, rows.Length);
        ColumnBatch selected = batch.WithSelection(new SelectionVector(new[] { 0, 2 }));

        using var output = new MemoryStream();
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileWriter().WriteAsync(output, schema, new[] { selected }, CancellationToken.None));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("selection vector", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    // ----- §2.3c level-guard negative cells -----

    private static DataField RepeatedLeaf()
    {
        var list = new ListField("a", new DataField<int?>("element"));
        _ = new ParquetSchema(list);
        return (DataField)list.Item;
    }

    private static DataField StructLeaf()
    {
        var structField = new global::Parquet.Schema.StructField("s", new DataField<int?>("x"));
        _ = new ParquetSchema(structField);
        return (DataField)structField.Fields[0];
    }

    [Fact]
    public void LevelGuard_AcceptsTheNormativeEncoding()
    {
        // Non-vacuity: the exact stream NestedParquetLevelStreamTests reads off the wire must PASS, or every
        // negative below could be passing for the wrong reason.
        NestedLevelGuard.Validate(
            RepeatedLeaf(), new[] { 3, 3, 0, 1, 2, 3 }, new[] { 0, 1, 0, 0, 0, 0 }, hasRepetitions: true,
            valueCount: 3, rowCount: 5, "a");

        NestedLevelGuard.Validate(
            StructLeaf(), new[] { 2, 0, 1, 2 }, ReadOnlySpan<int>.Empty, hasRepetitions: false,
            valueCount: 2, rowCount: 4, "s");
    }

    [Fact]
    public void LevelGuard_RejectsADefinitionLevelAboveTheLeafMaximum() =>
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 4, 0 }, new[] { 0, 0 }, true, 0, 2, "a"),
            "outside [0, 3]");

    [Fact]
    public void LevelGuard_RejectsARepetitionLevelAboveTheLeafMaximum() =>
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 3, 3 }, new[] { 0, 2 }, true, 2, 1, "a"),
            "outside [0, 1]");

    [Fact]
    public void LevelGuard_RejectsAFirstSlotThatContinuesAnUnopenedRow() =>
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 3, 3 }, new[] { 1, 1 }, true, 2, 1, "a"),
            "never opened");

    [Fact]
    public void LevelGuard_RejectsAContinuationOfAnEmptyContainerSlot() =>
        // The design's run-legality cell: def=[1,3,3] rep=[0,1,0]. Slot 0 encodes an EMPTY list, which
        // occupies exactly one slot and can never be continued.
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 1, 3, 3 }, new[] { 0, 1, 0 }, true, 2, 2, "a"),
            "absent or empty");

    [Fact]
    public void LevelGuard_RejectsAContinuationOfANullContainerSlot() =>
        // The sibling cell: def=[0,3,3] rep=[0,1,0]. Slot 0 encodes a NULL list.
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 0, 3, 3 }, new[] { 0, 1, 0 }, true, 2, 2, "a"),
            "absent or empty");

    [Fact]
    public void LevelGuard_RejectsAContinuationSlotAtAnAbsentDefinitionLevel() =>
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 3, 1 }, new[] { 0, 1 }, true, 1, 1, "a"),
            "encodes an absent or empty container");

    [Fact]
    public void LevelGuard_RejectsAValueCountThatDisagreesWithTheLevels() =>
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 3, 3, 0 }, new[] { 0, 1, 0 }, true, 3, 2, "a"),
            "were packed");

    [Fact]
    public void LevelGuard_RejectsARowCountThatDisagreesWithTheRowOpenings() =>
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 3, 3 }, new[] { 0, 1 }, true, 2, 7, "a"),
            "open 1 row(s)");

    [Fact]
    public void LevelGuard_RejectsANonRepeatedLeafStreamOfTheWrongLength() =>
        AssertRejected(
            () => NestedLevelGuard.Validate(
                StructLeaf(), new[] { 2, 2 }, ReadOnlySpan<int>.Empty, false, 2, 3, "s"),
            "the row group covers 3 row(s)");

    [Fact]
    public void LevelGuard_RejectsARepeatedLeafWithNoRepetitionStream() =>
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 3 }, ReadOnlySpan<int>.Empty, false, 1, 1, "a"),
            "without a repetition stream");

    [Fact]
    public void LevelGuard_RejectsANonRepeatedLeafCarryingARepetitionStream() =>
        AssertRejected(
            () => NestedLevelGuard.Validate(
                StructLeaf(), new[] { 2 }, new[] { 0 }, true, 1, 1, "s"),
            "with a repetition stream");

    [Fact]
    public void LevelGuard_RejectsMismatchedStreamLengths() =>
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 3, 3 }, new[] { 0 }, true, 2, 1, "a"),
            "different lengths");

    [Fact]
    public void LevelGuard_RejectsANegativeDefinitionLevel() =>
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 3, -1 }, new[] { 0, 1 }, true, 1, 1, "a"),
            "outside [0, 3]");

    [Fact]
    public void LevelGuard_RejectsANegativeRepetitionLevel() =>
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 3, 3 }, new[] { 0, -1 }, true, 2, 1, "a"),
            "outside [0, 1]");

    [Fact]
    public void LevelGuard_RejectsAValueCountThatUNDERSuppliesTheLevels() =>
        // The under-fill direction (B1): fewer packed values than the levels claim would leave the tail of the
        // value buffer UNINITIALIZED — pooled memory from a previous tenant — reaching WriteAllPartsAsync.
        AssertRejected(
            () => NestedLevelGuard.Validate(
                RepeatedLeaf(), new[] { 3, 3, 0 }, new[] { 0, 1, 0 }, true, 1, 2, "a"),
            "were packed");

    [Fact]
    public void LevelGuard_RejectsALeafNotAttachedToASchema() =>
        // §2.3c N4-c level PROVENANCE (B8): a DETACHED DataField reports maxDef 0, which silently collapses
        // the container/empty levels and turns the run-legality clause into a no-op. The guard must refuse to
        // validate against bounds that are not the file's.
        AssertRejected(
            () => NestedLevelGuard.Validate(
                new DataField<int?>("element"), new[] { 0 }, new[] { 0 }, true, 0, 1, "a"),
            "not attached to a Parquet schema");

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void SlotBound_RejectsAPathologicalNestedFanOut(int bytesPerSlot)
    {
        // The per-row-group leaf slot ceiling for callers that shred a hand-built segment list without
        // planning first. Driving a real multi-hundred-million-element vector is not feasible in a unit test,
        // so the bound itself is exercised directly — the arm is dead otherwise. It is denominated per LANE
        // (Q2): every shape reaches it at the same transient BYTE footprint, so the map lane cannot rent four
        // times what the struct lane may.
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => NestedColumnShredder.CheckSlotBound(
                NestedColumnShredder.MaxLeafSlotsPerRowGroup(
                    ParquetFileWriter.DefaultNestedLevelBufferBudgetBytes, bytesPerSlot) + 1L,
                "a", bytesPerSlot, ParquetFileWriter.DefaultNestedLevelBufferBudgetBytes));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("Dremel level slot(s)", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void SlotBound_AcceptsTheBoundExactly(int bytesPerSlot) =>
        // Non-vacuity: the ceiling is inclusive, so a leaf sitting exactly on it is legal.
        Assert.Equal(
            NestedColumnShredder.MaxLeafSlotsPerRowGroup(
                ParquetFileWriter.DefaultNestedLevelBufferBudgetBytes, bytesPerSlot),
            NestedColumnShredder.CheckSlotBound(
                NestedColumnShredder.MaxLeafSlotsPerRowGroup(
                    ParquetFileWriter.DefaultNestedLevelBufferBudgetBytes, bytesPerSlot),
                "a", bytesPerSlot, ParquetFileWriter.DefaultNestedLevelBufferBudgetBytes));

    [Fact]
    public void SlotBound_IsTheSameByteFootprintForEveryLane() =>
        // The backstop stands in for NestedLevelBufferBudgetBytes, so each lane's ceiling times its own
        // per-slot cost must be that same budget — a type-blind ceiling would be 4x loose on the map lane.
        Assert.All(
            new[] { 4, 8, 16 },
            bytesPerSlot => Assert.Equal(
                ParquetFileWriter.DefaultNestedLevelBufferBudgetBytes,
                (long)NestedColumnShredder.MaxLeafSlotsPerRowGroup(
                    ParquetFileWriter.DefaultNestedLevelBufferBudgetBytes, bytesPerSlot) * bytesPerSlot));

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void SlotBound_TracksTheInstanceBudget_NotTheDefaultConst(int bytesPerSlot)
    {
        // #845 item 4: the backstop is derived from the caller's INSTANCE budget (threaded from
        // ParquetFileWriter.NestedLevelBufferBudgetBytes), not the DefaultNestedLevelBufferBudgetBytes const,
        // so the "backstop >= plan ceiling" relation holds for ANY override — including one that RAISES the
        // budget above the default. A backstop pinned to the const would be LOWER than a raised-budget plan
        // ceiling and reject a validly-planned row group. Doubling the budget must double the ceiling.
        long doubled = ParquetFileWriter.DefaultNestedLevelBufferBudgetBytes * 2;
        Assert.Equal(
            2L * NestedColumnShredder.MaxLeafSlotsPerRowGroup(
                ParquetFileWriter.DefaultNestedLevelBufferBudgetBytes, bytesPerSlot),
            NestedColumnShredder.MaxLeafSlotsPerRowGroup(doubled, bytesPerSlot));

        // And a leaf sitting exactly on the raised ceiling is accepted (the plan ceiling for the raised budget
        // never exceeds the backstop for the same budget).
        Assert.Equal(
            NestedColumnShredder.MaxLeafSlotsPerRowGroup(doubled, bytesPerSlot),
            NestedColumnShredder.CheckSlotBound(
                NestedColumnShredder.MaxLeafSlotsPerRowGroup(doubled, bytesPerSlot),
                "a", bytesPerSlot, doubled));
    }

    private static void AssertRejected(Action act, string expectedFragment)
    {
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(act);
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains(expectedFragment, error.Message, StringComparison.Ordinal);
    }

    private static async Task<DeltaStorageException> AssertWriteFailsAsync(StructType schema, ColumnVector column)
    {
        var batch = new ManagedColumnBatch(schema, new[] { column }, column.Length);
        using var output = new MemoryStream();
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new ParquetFileWriter().WriteAsync(output, schema, new[] { batch }, CancellationToken.None));

        // §2.9 N9 (B7): ParquetWriter.CreateAsync publishes the PAR1 magic the moment it is called, so a
        // "fails closed BEFORE any byte" claim is only meaningful if the stream is still EMPTY. Every reject
        // routed through this helper — schema-door AND runtime required-lane/level — is held to that bar here.
        Assert.Equal(0, output.Length);
        return error;
    }
}
