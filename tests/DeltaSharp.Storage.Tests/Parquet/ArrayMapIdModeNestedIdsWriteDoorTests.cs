using System.Security.Cryptography;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet.Schema;
using Xunit;
using PqListField = Parquet.Schema.ListField;
using PqMapField = Parquet.Schema.MapField;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// #839 §3.20–§3.22 — the write door. The real nested writer (<see cref="ParquetTypeMapping.CreateField"/>)
/// stamps the interior element/key/value Parquet <c>field_id</c> = the container's
/// <c>delta.columnMapping.nested.ids</c> value under id mode; a container group node carries no
/// <c>field_id</c> (Parquet.Net 6.1.0 leaf-only). Name/none mode stamps NO interior <c>field_id</c> (byte
/// path unchanged from #676, pinned both at the schema door and with a real SHA-256 byte-invariance write). An
/// id-mode interior with no <c>nested.ids</c> to stamp fails closed at the door.
/// </summary>
public sealed class ArrayMapIdModeNestedIdsWriteDoorTests
{
    // §3.20 — array element leaf carries the nested.id field_id
    [Fact]
    public void ArrayMapIdMode_ArrayWrite_ElementLeafCarriesNestedIdFieldId()
    {
        var container = IdArray("col-b", DataTypes.LongType, containerId: 2, elementId: 7);
        var list = (PqListField)ParquetTypeMapping.CreateField(container, honorReferenceNullability: true);

        Assert.Equal(7, ((DataField)list.Item).FieldId);
    }

    // §3.20 — map key/value leaves carry their DISTINCT nested.id field_ids
    [Fact]
    public void ArrayMapIdMode_MapWrite_KeyValueLeavesCarryNestedIdFieldIds()
    {
        var container = IdMap("col-c", DataTypes.StringType, DataTypes.IntegerType, containerId: 4, keyId: 5, valueId: 6);
        var map = (PqMapField)ParquetTypeMapping.CreateField(container, honorReferenceNullability: true);

        Assert.Equal(5, ((DataField)map.Key).FieldId);
        Assert.Equal(6, ((DataField)map.Value).FieldId);
    }

    // §3.21 — an id-mode interior with no nested.ids to stamp fails closed at the write door
    [Fact]
    public void ArrayMapIdMode_ArrayWrite_UnstampedInteriorLeaf_FailsClosedAtWriteDoor()
    {
        // id-mode array (carries delta.columnMapping.id) but NO nested.ids → the door refuses rather than
        // emit an unstamped, unreadable interior leaf.
        var container = new StructField(
            "col-b",
            new ArrayType(DataTypes.LongType),
            nullable: true,
            FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(2)),
            }));

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(container, honorReferenceNullability: true));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("unstamped interior leaf would be unreadable", error.Message, StringComparison.Ordinal);
    }

    // §3.22 — name-mode array/map stamps NO interior field_id (byte path unchanged from #676)
    [Fact]
    public void ArrayMapIdMode_NameModeArrayMapWrite_NoInteriorFieldId()
    {
        // No delta.columnMapping.id → name/none mode. Interior leaves keep the default unset field_id (-1).
        var arrayContainer = new StructField("tags", new ArrayType(DataTypes.LongType), nullable: true);
        var list = (PqListField)ParquetTypeMapping.CreateField(arrayContainer, honorReferenceNullability: true);
        Assert.Equal(-1, ((DataField)list.Item).FieldId);

        var mapContainer = new StructField(
            "props", new MapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true), nullable: true);
        var map = (PqMapField)ParquetTypeMapping.CreateField(mapContainer, honorReferenceNullability: true);
        Assert.Equal(-1, ((DataField)map.Key).FieldId);
        Assert.Equal(-1, ((DataField)map.Value).FieldId);
    }

    // §3.22 (byte-invariance) — a name-mode array/map physical WRITE is byte-identical to the #676 baseline
    // (metadata-free) shape, measured with SHA-256 over the produced Parquet bytes. #839 introduced the id-mode
    // nested.ids code path; this pins that the NAME-mode write path is byte-unchanged from #676 — it emits NO
    // interior field_id AND leaks NO delta.columnMapping.nested.ids into the footer's embedded schema JSON. The
    // proof is a genuine pre/post comparison: (A) the physical schema produced by the #839-AWARE name-mode
    // mapping pipeline (AssignFreshMapping → ToPhysicalSchema, which strips all column-mapping metadata in name
    // mode) vs (B) a hand-built #676 baseline schema with the SAME physical field names/types and NO metadata.
    // The writer embeds DeltaSchemaJson(schema) in the footer's key_value_metadata, so if the name-mode path
    // ever leaked a nested.ids / field_id, (A)'s bytes would diverge from (B) and the SHA-256s would differ.
    [Fact]
    public async Task NameMode_ArrayMapWrite_NoNestedIds_NoInteriorFieldId_ByteUnchanged()
    {
        var logical = new StructType(new[]
        {
            new StructField("tags", new ArrayType(DataTypes.LongType), nullable: true),
            new StructField(
                "props", new MapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true), nullable: true),
        });

        // (A) The #839-aware name-mode pipeline: mint the mapping, then derive the physical schema. In name
        // mode ToPhysicalSchema strips every column-mapping key (id / physicalName / nested.ids), so the wire
        // schema is metadata-free — exactly the #676 shape.
        (StructType mapped, _) = ColumnMapping.AssignFreshMapping(
            logical, new SeededPhysicalNameSource("bytes-inv"), ColumnMappingMode.Name);
        StructType physicalPipeline = ColumnMapping.ToPhysicalSchema(mapped, ColumnMappingMode.Name);

        // (B) The #676 baseline: a plain schema carrying the SAME physical field names/types/nullability with
        // NO metadata whatsoever (constructed independently of the #839 code path).
        string arrayPhys = physicalPipeline.Fields[0].Name;
        string mapPhys = physicalPipeline.Fields[1].Name;
        var physicalBaseline = new StructType(new[]
        {
            new StructField(arrayPhys, new ArrayType(DataTypes.LongType), nullable: true),
            new StructField(
                mapPhys, new MapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true), nullable: true),
        });

        ColumnBatch Batch(StructType schema) => new ManagedColumnBatch(
            schema,
            new ColumnVector[]
            {
                NestedVectors.LongList(
                    (ArrayType)schema.Fields[0].DataType,
                    new long[]?[] { new long[] { 1, 2 }, null, new long[] { 3 } }),
                NestedVectors.StringIntMap(
                    (MapType)schema.Fields[1].DataType,
                    new IReadOnlyList<(string Key, int? Value)>?[]
                    {
                        new[] { ("a", (int?)1), ("b", (int?)2) },
                        null,
                        new[] { ("c", (int?)3) },
                    }),
            },
            3);

        byte[] pipelineBytes = await ParquetTestHelpers.WriteToBytesAsync(physicalPipeline, new[] { Batch(physicalPipeline) });
        byte[] baselineBytes = await ParquetTestHelpers.WriteToBytesAsync(physicalBaseline, new[] { Batch(physicalBaseline) });

        // Byte-invariance: the name-mode pipeline write is SHA-256-identical to the metadata-free #676 baseline.
        Assert.Equal(Sha256Hex(baselineBytes), Sha256Hex(pipelineBytes));

        // The footer carries NO interior field_id, and no id-mode nested.ids artifact leaked into the bytes.
        await AssertNoFieldIdInFooterAsync(pipelineBytes);
        Assert.DoesNotContain(ColumnMapping.NestedIdsKey, System.Text.Encoding.UTF8.GetString(pipelineBytes), StringComparison.Ordinal);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task AssertNoFieldIdInFooterAsync(byte[] parquetBytes)
    {
        using var stream = new MemoryStream(parquetBytes, writable: false);
        await using global::Parquet.ParquetReader reader = await global::Parquet.ParquetReader.CreateAsync(stream);
        foreach (global::Parquet.Meta.SchemaElement element in reader.Metadata!.Schema)
        {
            Assert.Null(element.FieldId);
        }
    }

    private static StructField IdArray(string physicalName, DataType elementType, long containerId, long elementId)
    {
        MetadataValue nestedIds = MetadataValue.Nested(FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(physicalName + ".element", MetadataValue.Long(elementId)),
        }));
        return new StructField(
            physicalName, new ArrayType(elementType), nullable: true,
            FieldMetadata.FromValues(new[]
            {
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
            physicalName, new MapType(keyType, valueType, valueContainsNull: true), nullable: true,
            FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(containerId)),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.NestedIdsKey, nestedIds),
            }));
    }
}
