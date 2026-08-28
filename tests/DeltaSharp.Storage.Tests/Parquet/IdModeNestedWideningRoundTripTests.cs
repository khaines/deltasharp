using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// #870 write→read round-trip for the column-mapping <c>id</c>-mode + <c>typeWidening</c> nested-widening
/// gap. The schema-evolution enforcer (<see cref="DeltaSchemaEnforcer"/>) must never APPLY a Delta-sanctioned
/// nested-collection / struct-child widening that the id-mode NESTED reader cannot promote
/// (<c>promoteLeaf: false</c>, #839/#546 §9 O1), because the pre-widening narrow data files would then be
/// UNREADABLE (fail-closed <c>SchemaMismatch</c>). These tests exercise the enforcer's WRITE decision AND the
/// real <see cref="ParquetFileReader"/> together, so the <c>enforcer ⊆ reader</c> invariant is proven
/// end-to-end (Step-0 scope: nested leaves are unreadable under id mode; a top-level scalar is readable).
/// </summary>
public sealed class IdModeNestedWideningRoundTripTests
{
    // A1 — id mode: the array-element widen is REFUSED on WRITE (fail-closed), and — the justification — the
    // pre-widening narrow file IS unreadable-by-id had the widen been applied. The write guard is exactly what
    // averts that unreadable table.
    [Fact]
    public async Task IdMode_ArrayElementWiden_FailsClosedOnWrite_AndOldNarrowFileWouldBeUnreadableByFieldId()
    {
        StructType table = new(new[] { new StructField("tags", new ArrayType(DataTypes.IntegerType, true), true) });
        StructType write = new(new[] { new StructField("tags", new ArrayType(DataTypes.LongType, true), true) });

        // WRITE guard: the enforcer refuses to apply the nested widen under id mode.
        DeltaSchemaMismatchException writeError = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null,
                typeWideningEnabled: true, columnMappingMode: ColumnMappingMode.Id));
        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, writeError.Kind);
        Assert.Equal("tags.element", writeError.Path);
        Assert.Contains("'id'-mode", writeError.Message, StringComparison.Ordinal);

        // Justification: an id-mode narrow array<int> file, read as array<long> by field_id with the promotion
        // gate OPEN, fails closed SchemaMismatch — the exact unreadable table the write guard prevents.
        StructType physicalNarrow = new(new[] { IdArray("tags", DataTypes.IntegerType, containerId: 2, elementId: 3) });
        byte[] narrow = await WriteAsync(
            physicalNarrow,
            NestedVectors.IntList((ArrayType)physicalNarrow.Fields[0].DataType, new int?[]?[] { new int?[] { 10, 20 } }));

        StructType wideByIdRequest = new(new[] { IdArray("tags", DataTypes.LongType, containerId: 2, elementId: 3) });
        DeltaStorageException readError = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadWideByIdAsync(narrow, wideByIdRequest));
        Assert.Equal(StorageErrorKind.SchemaMismatch, readError.Kind);
    }

    // A2 — name-mode CONTROL: the enforcer STILL APPLIES the array-element widen, and the pre-widening narrow
    // file ROUND-TRIPS READABLY (the name-mode reader promotes a nested leaf by physical name). Proves the
    // #870 guard did not regress the name-mode safety claim (#546/#860).
    [Fact]
    public async Task NameMode_ArrayElementWiden_IsApplied_AndOldNarrowFileRoundTripsReadably()
    {
        StructType table = new(new[] { new StructField("tags", new ArrayType(DataTypes.IntegerType, true), true) });
        StructType write = new(new[] { new StructField("tags", new ArrayType(DataTypes.LongType, true), true) });

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null,
            typeWideningEnabled: true, columnMappingMode: ColumnMappingMode.Name);
        Assert.NotNull(merged);
        Assert.Equal(DataTypes.LongType, Assert.IsType<ArrayType>(merged!["tags"].DataType).ElementType);

        // Real read: a narrow array<int> file read under the widened array<long> schema by NAME (name-mode,
        // resolveByFieldId:false) with the gate OPEN promotes the element values into the long lane.
        byte[] narrow = await WriteAsync(
            table, NestedVectors.IntList((ArrayType)table.Fields[0].DataType, new int?[]?[] { new int?[] { 10, 20 } }));
        List<ColumnBatch> batches = await ParquetTestHelpers.ReadAllAsync(
            narrow, write, keepRowGroup: null, allowTypeWideningPromotion: true);

        List<long[]?> readBack = NestedVectors.ReadLongList((ListColumnVector)batches[0].Column(0));
        Assert.Equal(new long[] { 10, 20 }, readBack[0]);
    }

    // A3 — top-level scalar id-mode CONTROL (Step-0 probe 1): the enforcer APPLIES the top-level int→long
    // widen under id mode, and the pre-widening narrow file ROUND-TRIPS READABLY by field_id (the FLAT reader
    // promotes a top-level scalar on the promotion gate alone). The guard must NOT block this readable widen.
    [Fact]
    public async Task IdMode_TopLevelScalarWiden_IsApplied_AndOldNarrowFileRoundTripsReadablyByFieldId()
    {
        StructType table = new(new[] { new StructField("value", DataTypes.IntegerType, nullable: true) });
        StructType write = new(new[] { new StructField("value", DataTypes.LongType, nullable: true) });

        StructType? merged = DeltaSchemaEnforcer.Reconcile(
            table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null,
            typeWideningEnabled: true, columnMappingMode: ColumnMappingMode.Id);
        Assert.NotNull(merged);
        Assert.Equal(DataTypes.LongType, merged!["value"].DataType);

        // Real read: a narrow id-mode int column (field_id 1) read as long by field_id with the gate OPEN
        // promotes — the top-level scalar id-mode widen is genuinely readable.
        StructType physicalNarrow = new(new[] { IdScalar("value", DataTypes.IntegerType, id: 1) });
        byte[] narrow = await WriteAsync(physicalNarrow, IntColumn(new int?[] { 10, 20, 30 }));

        StructType wideByIdRequest = new(new[] { IdScalar("value", DataTypes.LongType, id: 1) });
        ColumnBatch batch = await ReadWideByIdAsync(narrow, wideByIdRequest);
        Assert.Equal(new long[] { 10, 20, 30 }, batch.Column(0).GetValues<long>().ToArray());
    }

    // ------------------------------------------------------------------------------------------------------
    // Fixtures (mirror ArrayMapIdModeReadTests / ParquetFieldIdResolutionTests id-mode authoring)
    // ------------------------------------------------------------------------------------------------------

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
            physicalName, new ArrayType(elementType), nullable: true, FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>(ColumnMapping.PhysicalNameKey, MetadataValue.String(physicalName)),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(containerId)),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.NestedIdsKey, nestedIds),
            }));
    }

    private static ColumnVector IntColumn(int?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.IntegerType, values.Length);
        foreach (int? value in values)
        {
            if (value is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendValue(value.Value);
            }
        }

        return v;
    }

    private static async Task<byte[]> WriteAsync(StructType schema, params ColumnVector[] columns)
    {
        var batch = new ManagedColumnBatch(schema, columns, columns[0].Length);
        return await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
    }

    private static async Task<ColumnBatch> ReadWideByIdAsync(byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch b in new ParquetFileReader().ReadAsync(
            stream, requested, keepRowGroup: null, nullFillMissingColumns: false,
            allowTypeWideningPromotion: true, resolveByFieldId: true, CancellationToken.None))
        {
            only = b;
        }

        return Assert.IsAssignableFrom<ColumnBatch>(only);
    }
}
