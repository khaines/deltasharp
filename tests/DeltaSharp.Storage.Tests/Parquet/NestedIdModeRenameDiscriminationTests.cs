using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// RFL-2 (BAL-1) DISCRIMINATING cells for id-mode interior binding: a RENAMED id-mode container (logical name
/// ≠ physicalName) must still bind its interior leaves BY the physical-prefixed <c>nested.ids</c> field_ids —
/// the reader indexes <c>nested.ids</c> by the stable PHYSICAL name (never the logical <c>field.Name</c>) and
/// FAILS CLOSED on a missing/forged interior id rather than silently falling back to POSITIONAL binding (which
/// would bypass id verification). Each write emits a PHYSICAL-named file; each read uses a RENAMED request
/// (logical <c>field.Name</c> ≠ physicalName metadata) so a positional / logical-name mis-bind produces a
/// WRONG value or a wrong exception and FAILS the test. These cells FAIL against the pre-RFL-2 code (which
/// stripped <c>nested.ids</c> by <c>field.Name</c> and located the container by logical name → the renamed
/// container was ColumnNotPresentInFile, or its interior read positionally).
/// </summary>
public sealed class NestedIdModeRenameDiscriminationTests
{
    // §BAL-1a — renamed array<int>: the element binds by its physical-prefixed nested.ids id after a rename.
    [Fact]
    public async Task IdMode_RenamedArrayContainer_BindsElementById_RoundTrip()
    {
        byte[] bytes = await WriteAsync(
            PhysicalArray("col_b", DataTypes.IntegerType, containerId: 2, elementId: 3),
            NestedVectors.IntList(new ArrayType(DataTypes.IntegerType), new int?[]?[] { new int?[] { 41, 42 } }));

        // RENAMED request: logical "labels" ≠ physicalName "col_b"; nested.ids keyed by "col_b.element".
        StructType read = One(RenamedArray("labels", "col_b", DataTypes.IntegerType, containerId: 2, elementId: 3));
        ColumnBatch batch = await ReadByIdAsync(bytes, read);

        List<int?[]?> back = NestedVectors.ReadIntList((ListColumnVector)batch.Column(0));
        Assert.Equal(new int?[] { 41, 42 }, back[0]);
    }

    // §BAL-1b — renamed array<int> + FORGED element field_id (absent from footer) → SchemaMismatch, not a
    // positional read.
    [Fact]
    public async Task IdMode_RenamedArrayContainer_ForgedElementId_FailsClosed()
    {
        byte[] bytes = await WriteAsync(
            PhysicalArray("col_b", DataTypes.IntegerType, containerId: 2, elementId: 3),
            NestedVectors.IntList(new ArrayType(DataTypes.IntegerType), new int?[]?[] { new int?[] { 41 } }));

        StructType read = One(RenamedArray("labels", "col_b", DataTypes.IntegerType, containerId: 2, elementId: 999));
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(() => ReadByIdAsync(bytes, read));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    // §BAL-1c (fix-1b direct) — a PHYSICAL-named array whose nested.ids is EMPTY (no element id): the element
    // MUST fail closed, NEVER read positionally (the pre-fix bypass). Container resolves by name, so this
    // isolates the missing-interior-id fail-closed from the rename fix.
    [Fact]
    public async Task IdMode_ArrayEmptyNestedIds_MissingElementId_FailsClosed_NotPositional()
    {
        byte[] bytes = await WriteAsync(
            PhysicalArray("col_b", DataTypes.IntegerType, containerId: 2, elementId: 3),
            NestedVectors.IntList(new ArrayType(DataTypes.IntegerType), new int?[]?[] { new int?[] { 41 } }));

        // Same physical name (container resolves) but an EMPTY nested.ids → no resolvable element id.
        var emptyNestedIds = MetadataValue.Nested(FieldMetadata.FromValues(
            Array.Empty<KeyValuePair<string, MetadataValue>>()));
        var field = new StructField(
            "col_b", new ArrayType(DataTypes.IntegerType), nullable: true, FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>(ColumnMapping.PhysicalNameKey, MetadataValue.String("col_b")),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(2)),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.NestedIdsKey, emptyNestedIds),
            }));

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(() => ReadByIdAsync(bytes, One(field)));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    // §BAL-1d — renamed map<string,int> with DISJOINT key/value domains: key binds by key-id, value by
    // value-id, after a rename (a positional/logical mis-bind would swap or drop them).
    [Fact]
    public async Task IdMode_RenamedMapContainer_BindsKeyValueById_RoundTrip()
    {
        byte[] bytes = await WriteAsync(
            PhysicalMap("col_m", containerId: 2, keyId: 3, valueId: 4),
            NestedVectors.StringIntMap(
                new MapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true),
                new IReadOnlyList<(string, int?)>[] { new (string, int?)[] { ("k1", 700) } }));

        StructType read = One(RenamedMap("dict", "col_m", containerId: 2, keyId: 3, valueId: 4));
        ColumnBatch batch = await ReadByIdAsync(bytes, read);

        var model = NestedVectors.ReadStringIntMap((MapColumnVector)batch.Column(0));
        Assert.Equal("k1", model[0]![0].Key);
        Assert.Equal(700, model[0]![0].Value);
    }

    // §BAL-1e — renamed map + FORGED value id → SchemaMismatch (not positional).
    [Fact]
    public async Task IdMode_RenamedMapContainer_ForgedValueId_FailsClosed()
    {
        byte[] bytes = await WriteAsync(
            PhysicalMap("col_m", containerId: 2, keyId: 3, valueId: 4),
            NestedVectors.StringIntMap(
                new MapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true),
                new IReadOnlyList<(string, int?)>[] { new (string, int?)[] { ("k1", 700) } }));

        StructType read = One(RenamedMap("dict", "col_m", containerId: 2, keyId: 3, valueId: 999));
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(() => ReadByIdAsync(bytes, read));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    // §BAL-1f — renamed array<array<int>>: the INNER element binds by its MULTI-TOKEN nested.ids id
    // (col_aa.element.element) threaded through Descend after a rename of the OUTER container.
    [Fact]
    public async Task IdMode_RenamedArrayArray_InnerElementBindsById_RoundTrip()
    {
        var physType = new ArrayType(new ArrayType(DataTypes.IntegerType));
        byte[] bytes = await WriteAsync(
            PhysicalArrayArray("col_aa", containerId: 2, elementGroupId: 3, innerElementId: 4),
            ArrayOfArrayInt(physType, new[] { new int[][] { new[] { 7, 8 }, new[] { 9 } } }));

        StructType read = One(RenamedArrayArray("nested", "col_aa", containerId: 2, elementGroupId: 3, innerElementId: 4));
        ColumnBatch batch = await ReadByIdAsync(bytes, read);

        var outer = (ListColumnVector)batch.Column(0);
        var inner0 = (ListColumnVector)outer.ElementsAt(0);
        Assert.Equal(7, inner0.ElementsAt(0).GetValue<int>(0));
        Assert.Equal(8, inner0.ElementsAt(0).GetValue<int>(1));
        Assert.Equal(9, inner0.ElementsAt(1).GetValue<int>(0));
    }

    // §BAL-1g (8p / re-seed) — struct<b: array<int>> with the struct CHILD `b` renamed: the container child is
    // located by its rename-stable physicalName AND the inner element re-seeds from `b`'s OWN physical-prefixed
    // nested.ids. Pre-fix, `b` would be located by logical name (absent → null-filled) — WRONG.
    [Fact]
    public async Task IdMode_StructChildArray_RenamedChild_ReSeedBindsById_RoundTrip()
    {
        byte[] bytes = await WriteAsync(
            PhysicalStructOfArray("col_s", "col_b", structId: 2, arrayId: 3, elementId: 4),
            StructOfArrayInt("col_b", new[] { new[] { 55, 56 } }));

        StructType read = One(RenamedStructOfArray("s", "col_s", "b_renamed", "col_b", structId: 2, arrayId: 3, elementId: 4));
        ColumnBatch batch = await ReadByIdAsync(bytes, read);

        var s = (StructColumnVector)batch.Column(0);
        var b = (ListColumnVector)s.Child(0);
        Assert.False(b.IsNull(0)); // NOT null-filled (pre-fix would null-fill the logical-name-missing child)
        Assert.Equal(55, b.ElementsAt(0).GetValue<int>(0));
        Assert.Equal(56, b.ElementsAt(0).GetValue<int>(1));
    }

    // §3.8e (reader-level) — a REQUIRED scalar leaf at depth>1 absent from the file FAILS CLOSED
    // (ColumnNotPresentInFile — a required lane cannot null-fill). Write array<struct<a>>, read
    // array<struct<a, b:long REQUIRED>>.
    [Fact]
    public async Task IdMode_RequiredScalarChild_AbsentAtDepth2_FailsClosed_ColumnNotPresent()
    {
        byte[] bytes = await WriteAsync(
            PhysicalArrayOfStruct("col_it", "col_a", containerId: 2, elementGroupId: 3, aId: 4),
            ArrayOfStructA(new[] { new long?[] { 100L } }));

        var elem = new StructType(new[]
        {
            new StructField("a", DataTypes.LongType, nullable: true, MapMeta("col_a", 4)),
            new StructField("b", DataTypes.LongType, nullable: false, MapMeta("col_b", 5)), // REQUIRED, absent
        });
        var read = One(new StructField("col_it", new ArrayType(elem), nullable: true,
            MapMeta("col_it", 2, ("col_it.element", 3))));

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(() => ReadByIdAsync(bytes, read));
        Assert.Equal(StorageErrorKind.ColumnNotPresentInFile, ex.Kind);
    }

    // §3.8j (reader-level) — a REQUIRED nested CONTAINER (struct) at depth>1, structurally absent, FAILS CLOSED.
    [Fact]
    public async Task IdMode_RequiredContainerChild_AbsentAtDepth2_FailsClosed_ColumnNotPresent()
    {
        byte[] bytes = await WriteAsync(
            PhysicalArrayOfStruct("col_it", "col_a", containerId: 2, elementGroupId: 3, aId: 4),
            ArrayOfStructA(new[] { new long?[] { 100L } }));

        var innerB = new StructType(new[] { new StructField("c", DataTypes.LongType, nullable: true, MapMeta("col_c", 6)) });
        var elem = new StructType(new[]
        {
            new StructField("a", DataTypes.LongType, nullable: true, MapMeta("col_a", 4)),
            new StructField("b", innerB, nullable: false, MapMeta("col_b", 5)), // REQUIRED container, absent
        });
        var read = One(new StructField("col_it", new ArrayType(elem), nullable: true,
            MapMeta("col_it", 2, ("col_it.element", 3))));

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(() => ReadByIdAsync(bytes, read));
        Assert.Equal(StorageErrorKind.ColumnNotPresentInFile, ex.Kind);
    }

    // §3.8n (reader-level) — a REQUIRED top-level container whose group node is entirely absent FAILS CLOSED.
    [Fact]
    public async Task IdMode_RequiredTopLevelContainer_StructurallyAbsent_FailsClosed_ColumnNotPresent()
    {
        byte[] bytes = await WriteAsync(
            PhysicalArray("col_b", DataTypes.IntegerType, containerId: 2, elementId: 3),
            NestedVectors.IntList(new ArrayType(DataTypes.IntegerType), new int?[]?[] { new int?[] { 41 } }));

        // Request a REQUIRED struct container 'addr' that does not exist in the file.
        var addr = new StructField("addr",
            new StructType(new[] { new StructField("city", DataTypes.LongType, nullable: true, MapMeta("col_city", 7)) }),
            nullable: false, MapMeta("col_addr", 6));
        var read = One(addr);

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(() => ReadByIdAsync(bytes, read));
        Assert.Equal(StorageErrorKind.ColumnNotPresentInFile, ex.Kind);
    }

    private static StructField PhysicalArrayOfStruct(string containerPhysical, string aPhysical, long containerId, long elementGroupId, long aId)
    {
        var elem = new StructType(new[] { new StructField(aPhysical, DataTypes.LongType, nullable: true, MapMeta(aPhysical, aId)) });
        return new StructField(containerPhysical, new ArrayType(elem), nullable: true,
            MapMeta(containerPhysical, containerId, (containerPhysical + ".element", elementGroupId)));
    }

    private static ListColumnVector ArrayOfStructA(IReadOnlyList<long?[]> rows)
    {
        var elemType = new StructType(new[] { new StructField("col_a", DataTypes.LongType, nullable: true) });
        MutableColumnVector a = ColumnVectors.Create(DataTypes.LongType, 16);
        var elemNulls = new List<bool>();
        var offsets = new int[rows.Count + 1];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            foreach (long? v in rows[i])
            {
                if (v is null)
                {
                    a.AppendNull();
                }
                else
                {
                    a.AppendValue(v.Value);
                }

                elemNulls.Add(false);
                cursor++;
            }
        }

        offsets[rows.Count] = cursor;
        var elements = new StructColumnVector(elemType, new ColumnVector[] { a }, elemNulls.ToArray());
        return new ListColumnVector(new ArrayType(elemType), elements, offsets, new bool[rows.Count]);
    }

    // ---- fixtures ----

    private static StructType One(StructField f) => new(new[] { f });

    private static FieldMetadata MapMeta(string physicalName, long id, params (string Key, long Id)[] nested)
    {
        var entries = new List<KeyValuePair<string, MetadataValue>>
        {
            new(ColumnMapping.PhysicalNameKey, MetadataValue.String(physicalName)),
            new(ColumnMapping.IdKey, MetadataValue.Long(id)),
        };
        if (nested.Length > 0)
        {
            var nestedIds = nested.Select(n =>
                new KeyValuePair<string, MetadataValue>(n.Key, MetadataValue.Long(n.Id))).ToArray();
            entries.Add(new KeyValuePair<string, MetadataValue>(
                ColumnMapping.NestedIdsKey, MetadataValue.Nested(FieldMetadata.FromValues(nestedIds))));
        }

        return FieldMetadata.FromValues(entries);
    }

    private static StructField PhysicalArray(string physicalName, DataType element, long containerId, long elementId) =>
        new(physicalName, new ArrayType(element), nullable: true,
            MapMeta(physicalName, containerId, (physicalName + ".element", elementId)));

    private static StructField RenamedArray(string logicalName, string physicalName, DataType element, long containerId, long elementId) =>
        new(logicalName, new ArrayType(element), nullable: true,
            MapMeta(physicalName, containerId, (physicalName + ".element", elementId)));

    private static StructField PhysicalMap(string physicalName, long containerId, long keyId, long valueId) =>
        new(physicalName, new MapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true), nullable: true,
            MapMeta(physicalName, containerId, (physicalName + ".key", keyId), (physicalName + ".value", valueId)));

    private static StructField RenamedMap(string logicalName, string physicalName, long containerId, long keyId, long valueId) =>
        new(logicalName, new MapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true), nullable: true,
            MapMeta(physicalName, containerId, (physicalName + ".key", keyId), (physicalName + ".value", valueId)));

    private static StructField PhysicalArrayArray(string physicalName, long containerId, long elementGroupId, long innerElementId) =>
        new(physicalName, new ArrayType(new ArrayType(DataTypes.IntegerType)), nullable: true,
            MapMeta(physicalName, containerId,
                (physicalName + ".element", elementGroupId),
                (physicalName + ".element.element", innerElementId)));

    private static StructField RenamedArrayArray(string logicalName, string physicalName, long containerId, long elementGroupId, long innerElementId) =>
        new(logicalName, new ArrayType(new ArrayType(DataTypes.IntegerType)), nullable: true,
            MapMeta(physicalName, containerId,
                (physicalName + ".element", elementGroupId),
                (physicalName + ".element.element", innerElementId)));

    private static StructField PhysicalStructOfArray(string structPhysical, string arrayPhysical, long structId, long arrayId, long elementId)
    {
        var b = new StructField(arrayPhysical, new ArrayType(DataTypes.IntegerType), nullable: true,
            MapMeta(arrayPhysical, arrayId, (arrayPhysical + ".element", elementId)));
        return new StructField(structPhysical, new StructType(new[] { b }), nullable: true, MapMeta(structPhysical, structId));
    }

    private static StructField RenamedStructOfArray(
        string structLogical, string structPhysical, string arrayLogical, string arrayPhysical, long structId, long arrayId, long elementId)
    {
        var b = new StructField(arrayLogical, new ArrayType(DataTypes.IntegerType), nullable: true,
            MapMeta(arrayPhysical, arrayId, (arrayPhysical + ".element", elementId)));
        return new StructField(structLogical, new StructType(new[] { b }), nullable: true, MapMeta(structPhysical, structId));
    }

    private static ListColumnVector ArrayOfArrayInt(ArrayType type, IReadOnlyList<int[][]> rows)
    {
        var innerType = (ArrayType)type.ElementType;
        MutableColumnVector leaf = ColumnVectors.Create(DataTypes.IntegerType, 16);
        var innerOffsets = new List<int> { 0 };
        var innerNulls = new List<bool>();
        var outerOffsets = new int[rows.Count + 1];
        int leafCursor = 0;
        int innerCursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            outerOffsets[i] = innerCursor;
            foreach (int[] innerRow in rows[i])
            {
                foreach (int v in innerRow)
                {
                    leaf.AppendValue(v);
                    leafCursor++;
                }

                innerOffsets.Add(leafCursor);
                innerNulls.Add(false);
                innerCursor++;
            }
        }

        outerOffsets[rows.Count] = innerCursor;
        var innerList = new ListColumnVector(innerType, leaf, innerOffsets.ToArray(), innerNulls.ToArray());
        return new ListColumnVector(type, innerList, outerOffsets, new bool[rows.Count]);
    }

    private static StructColumnVector StructOfArrayInt(string childName, IReadOnlyList<int[]> bRows)
    {
        var arrType = new ArrayType(DataTypes.IntegerType);
        MutableColumnVector leaf = ColumnVectors.Create(DataTypes.IntegerType, 16);
        var offsets = new int[bRows.Count + 1];
        int cursor = 0;
        for (int i = 0; i < bRows.Count; i++)
        {
            offsets[i] = cursor;
            foreach (int v in bRows[i])
            {
                leaf.AppendValue(v);
                cursor++;
            }
        }

        offsets[bRows.Count] = cursor;
        var bVec = new ListColumnVector(arrType, leaf, offsets, new bool[bRows.Count]);
        var structType = new StructType(new[] { new StructField(childName, arrType, nullable: true) });
        return new StructColumnVector(structType, new ColumnVector[] { bVec }, new bool[bRows.Count]);
    }

    private static async Task<byte[]> WriteAsync(StructField physicalField, ColumnVector column)
    {
        var schema = new StructType(new[] { physicalField });
        // Relabel the column to the physical field's exact DataType (carrying the id/nested.ids metadata on its
        // struct children) so ManagedColumnBatch's type-equality check holds — the writer then stamps field_ids.
        ColumnVector relabelled = (column, physicalField.DataType) switch
        {
            (StructColumnVector s, StructType st) => s.RelabelTo(st),
            (ListColumnVector l, ArrayType at) => l.RelabelTo(at),
            (MapColumnVector m, MapType mt) => m.RelabelTo(mt),
            _ => column,
        };
        var batch = new ManagedColumnBatch(schema, new[] { relabelled }, relabelled.Length);
        return await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
    }

    private static async Task<ColumnBatch> ReadByIdAsync(byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch b in new ParquetFileReader().ReadAsync(
            stream, requested, keepRowGroup: null, nullFillMissingColumns: false,
            allowTypeWideningPromotion: false, resolveByFieldId: true, CancellationToken.None))
        {
            only = b;
        }

        return Assert.IsAssignableFrom<ColumnBatch>(only);
    }
}
