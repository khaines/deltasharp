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
/// path unchanged from #676). An id-mode interior with no <c>nested.ids</c> to stamp fails closed at the door.
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
