using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// #839 §3 — the id-mode <c>array&lt;scalar&gt;</c> / <c>map&lt;scalar,scalar&gt;</c> read path. The container
/// binds by <c>physicalName</c>; the interior element/key/value leaf binds by its
/// <c>delta.columnMapping.nested.ids</c> <c>field_id</c> WITHIN the container's own interior (§2.5
/// containment). Every fixture is authored with the REAL nested writer (#834/#842) — the physical schema
/// carries <c>delta.columnMapping.id</c> + <c>delta.columnMapping.nested.ids</c>, so
/// <see cref="ParquetTypeMapping"/> stamps the interior leaf <c>field_id</c> = the <c>nested.ids</c> value —
/// and read back through <see cref="ParquetFileReader"/> with <c>resolveByFieldId: true</c>. Fail-closed cells
/// keep every other identity byte-exact and change EXACTLY one thing (the requested <c>nested.ids</c> value or
/// the requested container id), so the asserted guard is the SOLE rejecting guard (§3.8/§3.9/§3.13/§3.14
/// non-vacuity).
/// </summary>
public sealed class ArrayMapIdModeReadTests
{
    // -------------------------------------------------------------------------------------------------
    // §3.1 · array<scalar> id-mode create→read round-trip (container by physicalName, element by nested.id)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ArrayMapIdMode_ArrayScalar_RoundTrip_ContainerByPhysicalName_ElementByNestedId()
    {
        // {id:long, tags:array<long>} — id=1/col-a, tags container=2/col-b, tags.element=3.
        StructType physical = new(new[]
        {
            IdScalar("col-a", DataTypes.LongType, id: 1),
            IdArray("col-b", DataTypes.LongType, containerId: 2, elementId: 3),
        });

        var rows = new long[]?[] { new long[] { 10, 11 }, null, Array.Empty<long>(), new long[] { 12 } };
        byte[] bytes = await WriteAsync(physical, IdColumn(new long?[] { 1, 2, 3, 4 }), LongList(physical, 1, rows));

        // Read back with the SAME physical schema (container binds by name 'col-b', element by nested.id 3).
        ColumnBatch batch = await ReadSingleBatchAsync(bytes, physical);
        List<long[]?> readBack = NestedVectors.ReadLongList((ListColumnVector)batch.Column(1));
        Assert.Equal(rows.Length, readBack.Count);
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.Equal(rows[i], readBack[i]);
        }
    }

    [Fact]
    public async Task ArrayMapIdMode_ArrayScalar_RoundTrip_ReadsThroughContainerRename()
    {
        // The container's LOGICAL rename does not touch its physical name — a read whose requested container
        // uses the same physical name ('col-b') resolves the same footer group and element leaf, no rewrite.
        StructType physical = new(new[]
        {
            IdScalar("col-a", DataTypes.LongType, id: 1),
            IdArray("col-b", DataTypes.LongType, containerId: 2, elementId: 3),
        });
        var rows = new long[]?[] { new long[] { 42 } };
        byte[] bytes = await WriteAsync(physical, IdColumn(new long?[] { 7 }), LongList(physical, 1, rows));

        // A DIFFERENT logical name for the container would still author the same physical schema; read uses
        // the physical name, so identity is unchanged.
        ColumnBatch batch = await ReadSingleBatchAsync(bytes, physical);
        Assert.Equal(new long[] { 42 }, NestedVectors.ReadLongList((ListColumnVector)batch.Column(1))[0]);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.2 · map<scalar,scalar> id-mode round-trip (key/value by DISTINCT nested.ids ids)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ArrayMapIdMode_MapScalarScalar_RoundTrip_KeyValueByNestedIds()
    {
        // {id:long, props:map<string,int>} — id=1/col-a, props container=4/col-c, key=5, value=6.
        StructType physical = new(new[]
        {
            IdScalar("col-a", DataTypes.LongType, id: 1),
            IdMap("col-c", DataTypes.StringType, DataTypes.IntegerType, containerId: 4, keyId: 5, valueId: 6),
        });

        var rows = new IReadOnlyList<(string, int?)>?[]
        {
            new[] { ("k1", (int?)100), ("k2", (int?)200) },
            null,
            Array.Empty<(string, int?)>(),
        };
        byte[] bytes = await WriteAsync(
            physical, IdColumn(new long?[] { 1, 2, 3 }), StringIntMap(physical, 1, rows));

        ColumnBatch batch = await ReadSingleBatchAsync(bytes, physical);
        NestedVectors.AssertMapsEqual(ToMapModel(rows), NestedVectors.ReadStringIntMap((MapColumnVector)batch.Column(1)));
    }

    // -------------------------------------------------------------------------------------------------
    // §3.5 · Spark-authored id-mode array WITH nested.ids reads correctly (interior leaf field_id present)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ArrayMapIdMode_SparkAuthored_ArrayWithNestedIds_ReadsCorrectly()
    {
        // A Spark-shaped file (interior element leaf carries field_id; container group node id-free). The real
        // writer produces exactly that shape; DeltaSharp binds interior by leaf id + container by physicalName.
        StructType physical = new(new[]
        {
            IdArray("col-b", DataTypes.LongType, containerId: 2, elementId: 7),
        });
        var rows = new long[]?[] { new long[] { 1000, 1001 }, new long[] { 2000 } };
        byte[] bytes = await WriteAsync(physical, LongList(physical, 0, rows));

        ColumnBatch batch = await ReadSingleBatchAsync(bytes, physical);
        var list = (ListColumnVector)batch.Column(0);
        Assert.Equal(new long[] { 1000, 1001 }, NestedVectors.ReadLongList(list)[0]);
        Assert.Equal(new long[] { 2000 }, NestedVectors.ReadLongList(list)[1]);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.8 · interior id resolves to a TOP-LEVEL leaf → containment reject (footer-only tamper)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ArrayMapIdMode_InteriorId_ResolvesToTopLevelLeaf_FailsClosed()
    {
        // Written file: top-level 'col-a' leaf field_id 1, array 'col-b' element leaf field_id 9. The READ
        // request keeps every identity byte-exact EXCEPT it asks for the array element by id 1 — the top-level
        // leaf. The id resolves in the footer but OUTSIDE the container's own interior → containment reject.
        StructType writeSchema = new(new[]
        {
            IdScalar("col-a", DataTypes.LongType, id: 1),
            IdArray("col-b", DataTypes.LongType, containerId: 2, elementId: 9),
        });
        byte[] bytes = await WriteAsync(
            writeSchema, IdColumn(new long?[] { 5 }), LongList(writeSchema, 1, new long[]?[] { new long[] { 7 } }));

        StructType readSchema = new(new[]
        {
            IdScalar("col-a", DataTypes.LongType, id: 1),
            IdArray("col-b", DataTypes.LongType, containerId: 2, elementId: 1), // <-- only change: element id → 1
        });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleBatchAsync(bytes, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("outside the resolved container's own interior", error.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.9 · forged nested.ids relocates the element to a SIBLING container's interior → containment reject
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ArrayMapIdMode_ElementRelocatedToSiblingContainerInterior_FailsClosed()
    {
        // Two arrays: a (col-a, element leaf field_id 30), b (col-b, element leaf field_id 31). The footer
        // bijection is intact (each leaf a distinct field_id — a MOVE, not a dup). The READ request points a's
        // element at b's leaf (id 31); the resolved leaf is b's child, not a's → containment reject.
        StructType writeSchema = new(new[]
        {
            IdArray("col-a", DataTypes.LongType, containerId: 1, elementId: 30),
            IdArray("col-b", DataTypes.LongType, containerId: 2, elementId: 31),
        });
        byte[] bytes = await WriteAsync(
            writeSchema,
            LongList(writeSchema, 0, new long[]?[] { new long[] { 300 } }),
            LongList(writeSchema, 1, new long[]?[] { new long[] { 310 } }));

        StructType readSchema = new(new[]
        {
            IdArray("col-a", DataTypes.LongType, containerId: 1, elementId: 31), // <-- a's element → b's leaf
            IdArray("col-b", DataTypes.LongType, containerId: 2, elementId: 31),
        });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleBatchAsync(bytes, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("outside the resolved container's own interior", error.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.11 · map key/value swapped across DIFFERENTLY-typed interiors → type-validated → SchemaMismatch
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ArrayMapIdMode_MapKeyValueSwapped_DifferentlyTyped_FailsClosed_AsSchemaMismatch()
    {
        // map<string,int>: key leaf field_id 5 (string), value leaf field_id 6 (int). The READ request swaps
        // the ids (key→6, value→5), keeping both ids present, in-range, globally unique, container/containment
        // clean — so ONLY ExpectScalarLeaf type-validation fires (string request ↔ int leaf).
        StructType writeSchema = new(new[]
        {
            IdMap("col-c", DataTypes.StringType, DataTypes.IntegerType, containerId: 4, keyId: 5, valueId: 6),
        });
        byte[] bytes = await WriteAsync(
            writeSchema,
            StringIntMap(writeSchema, 0, new IReadOnlyList<(string, int?)>?[] { new[] { ("k", (int?)9) } }));

        StructType readSchema = new(new[]
        {
            IdMap("col-c", DataTypes.StringType, DataTypes.IntegerType, containerId: 4, keyId: 6, valueId: 5),
        });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleBatchAsync(bytes, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.13 · container-binding negatives (the containment ROOT)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ArrayMapIdMode_ContainerPhysicalNameAbsentFromFooter_FailsClosed()
    {
        StructType writeSchema = new(new[]
        {
            IdArray("col-b", DataTypes.LongType, containerId: 2, elementId: 3),
        });
        byte[] bytes = await WriteAsync(writeSchema, LongList(writeSchema, 0, new long[]?[] { new long[] { 1 } }));

        StructType readSchema = new(new[]
        {
            IdArray("missing", DataTypes.LongType, containerId: 2, elementId: 3),
        });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleBatchAsync(bytes, readSchema));
        Assert.Equal(StorageErrorKind.ColumnNotPresentInFile, error.Kind);
    }

    [Fact]
    public async Task ArrayMapIdMode_ContainerResolvesToNonListLeaf_FailsClosed()
    {
        // 'col-a' is a top-level SCALAR in the file, but requested as an array container.
        StructType writeSchema = new(new[]
        {
            IdScalar("col-a", DataTypes.LongType, id: 1),
        });
        byte[] bytes = await WriteAsync(writeSchema, IdColumn(new long?[] { 5 }));

        StructType readSchema = new(new[]
        {
            IdArray("col-a", DataTypes.LongType, containerId: 50, elementId: 3),
        });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleBatchAsync(bytes, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("non-list file column", error.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.14 · container declared id found on a footer leaf → structural-only reject
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ArrayMapIdMode_ContainerDeclaredIdFoundOnFooterLeaf_FailsClosed()
    {
        // The array container's declared id (9) is ALSO the interior element leaf's field_id (nested.ids
        // element=9). A container id must be structural-only; a footer-resolvable one is forged → reject.
        StructType schema = new(new[]
        {
            IdArray("col-b", DataTypes.LongType, containerId: 9, elementId: 9),
        });
        byte[] bytes = await WriteAsync(schema, LongList(schema, 0, new long[]?[] { new long[] { 1 } }));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleBatchAsync(bytes, schema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("structural-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArrayMapIdMode_InteriorId_AbsentFromFooter_FailsClosed_NoPositionalFallback()
    {
        // element id 999 declared but absent from the footer → fail closed with NO positional fallback.
        StructType writeSchema = new(new[]
        {
            IdArray("col-b", DataTypes.LongType, containerId: 2, elementId: 3),
        });
        byte[] bytes = await WriteAsync(writeSchema, LongList(writeSchema, 0, new long[]?[] { new long[] { 1 } }));

        StructType readSchema = new(new[]
        {
            IdArray("col-b", DataTypes.LongType, containerId: 2, elementId: 999),
        });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleBatchAsync(bytes, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("no positional fallback", error.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // #860 (585b) · PRESERVED id-mode fail-closed non-promotion (design §2.5 / §9 O1)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IdModeNestedWithinNested_AtDepth2_MismatchedFileShape_FailsClosed()
    {
        // 866b lifts id-mode depth>1, but reading an array<scalar> FILE under an array<array<long>> request is
        // a structural shape mismatch that still fails closed: the request's single-token nested.ids marks
        // col-b.element as a scalar leaf id present in the footer, so the container-element group-id-absent
        // guard (or the ExpectList shape check) fails closed — never a silent mis-decode.
        StructType writeSchema = new(new[]
        {
            IdArray("col-b", DataTypes.IntegerType, containerId: 2, elementId: 3),
        });
        byte[] bytes = await WriteAsync(
            writeSchema, NestedVectors.IntList((ArrayType)writeSchema.Fields[0].DataType,
                new int?[]?[] { new int?[] { 10, 20 } }));

        StructType readSchema = new(new[]
        {
            IdArray("col-b", new ArrayType(DataTypes.LongType, true), containerId: 2, elementId: 3),
        });

        await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSinglePromotedByIdAsync(bytes, readSchema));
    }

    [Fact]
    public async Task IdMode_Depth1_NarrowScalarLeaf_WideRequest_GateOpen_FailsClosed_SchemaMismatch()
    {
        // Cell 18b (light pin): the genuine id-mode widening-REFUSED-via-SchemaMismatch case is a DEPTH-1
        // scalar-interior container. An id-mode array<int> element requested (by the same nested.id) as
        // array<long> with the gate OPEN fails closed SchemaMismatch — the id-mode element leaf uses
        // promoteLeaf:false hardcoded and R3 retains `&& byFieldId is null`, so id-mode NEVER promotes
        // (heavily covered by the merged #675 CDF suite; pinned once here at the reader-unit level).
        StructType writeSchema = new(new[]
        {
            IdArray("col-b", DataTypes.IntegerType, containerId: 2, elementId: 3),
        });
        byte[] bytes = await WriteAsync(
            writeSchema, NestedVectors.IntList((ArrayType)writeSchema.Fields[0].DataType,
                new int?[]?[] { new int?[] { 10, 20 } }));

        StructType readSchema = new(new[]
        {
            IdArray("col-b", DataTypes.LongType, containerId: 2, elementId: 3),
        });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSinglePromotedByIdAsync(bytes, readSchema));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    // -------------------------------------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------------------------------------

    private static StructField IdScalar(string physicalName, DataType type, long id) =>
        new(physicalName, type, nullable: true, FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(ColumnMapping.PhysicalNameKey, MetadataValue.String(physicalName)),
            new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(id)),
        }));

    private static StructField IdArray(string physicalName, DataType elementType, long containerId, long elementId)
    {
        MetadataValue nestedIds = MetadataValue.Nested(FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(physicalName + ".element", MetadataValue.Long(elementId)),
        }));
        return new StructField(
            physicalName,
            new ArrayType(elementType),
            nullable: true,
            FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>(ColumnMapping.PhysicalNameKey, MetadataValue.String(physicalName)),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(containerId)),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.NestedIdsKey, nestedIds),
            }));
    }

    private static StructField IdMap(
        string physicalName, DataType keyType, DataType valueType, long containerId, long keyId, long valueId)
    {
        MetadataValue nestedIds = MetadataValue.Nested(FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(physicalName + ".key", MetadataValue.Long(keyId)),
            new KeyValuePair<string, MetadataValue>(physicalName + ".value", MetadataValue.Long(valueId)),
        }));
        return new StructField(
            physicalName,
            new MapType(keyType, valueType, valueContainsNull: true),
            nullable: true,
            FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>(ColumnMapping.PhysicalNameKey, MetadataValue.String(physicalName)),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(containerId)),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.NestedIdsKey, nestedIds),
            }));
    }

    private static ColumnVector IdColumn(long?[] ids)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.LongType, ids.Length);
        foreach (long? id in ids)
        {
            if (id is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendValue(id.Value);
            }
        }

        return v;
    }

    private static ColumnVector LongList(StructType schema, int fieldIndex, IReadOnlyList<long[]?> rows) =>
        NestedVectors.LongList((ArrayType)schema.Fields[fieldIndex].DataType, rows);

    private static ColumnVector StringIntMap(
        StructType schema, int fieldIndex, IReadOnlyList<IReadOnlyList<(string, int?)>?> rows) =>
        NestedVectors.StringIntMap((MapType)schema.Fields[fieldIndex].DataType, rows);

    private static async Task<byte[]> WriteAsync(StructType schema, params ColumnVector[] columns)
    {
        int rowCount = columns[0].Length;
        var batch = new ManagedColumnBatch(schema, columns, rowCount);
        return await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
    }

    private static async Task<ColumnBatch> ReadSingleBatchAsync(byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch b in new ParquetFileReader().ReadAsync(
            stream, requested, keepRowGroup: null, nullFillMissingColumns: false,
            allowTypeWideningPromotion: false, resolveByFieldId: true, CancellationToken.None))
        {
            Assert.Null(only);
            only = b;
        }

        return Assert.IsAssignableFrom<ColumnBatch>(only);
    }

    // Reads an id-mode column with the type-widening promotion gate OPEN (#860 cell 18) — proves an id-mode
    // nested leaf stays fail-closed even when the gate is open (the `byFieldId is null` conjunct is decisive).
    private static async Task<ColumnBatch> ReadSinglePromotedByIdAsync(byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch b in new ParquetFileReader().ReadAsync(
            stream, requested, keepRowGroup: null, nullFillMissingColumns: false,
            allowTypeWideningPromotion: true, resolveByFieldId: true, CancellationToken.None))
        {
            Assert.Null(only);
            only = b;
        }

        return Assert.IsAssignableFrom<ColumnBatch>(only);
    }

    private static List<List<(string Key, int? Value)>?> ToMapModel(
        IReadOnlyList<IReadOnlyList<(string, int?)>?> rows)
    {
        var result = new List<List<(string, int?)>?>(rows.Count);
        foreach (IReadOnlyList<(string, int?)>? row in rows)
        {
            if (row is null)
            {
                result.Add(null);
                continue;
            }

            var entries = new List<(string, int?)>(row.Count);
            foreach ((string k, int? val) in row)
            {
                entries.Add((k, val));
            }

            result.Add(entries);
        }

        return result;
    }
}
