using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// Per-field column-mapping <b>id</b>-mode resolution in <see cref="ParquetFileReader"/> (#658). Under
/// <c>resolveByFieldId: true</c> each requested column is resolved INDEPENDENTLY: by its
/// <c>delta.columnMapping.id</c> against the footer field_ids when it declares one, or by physical NAME when it
/// carries none (an engine-synthesized column like CDF's <c>_change_type</c>). The by-name fallback matches
/// ONLY an un-mapped (no-field_id) file column, so a requested column that merely LACKS its id can never grab a
/// genuinely column-mapped file column — preserving the fail-closed posture this capability was gated on.
/// </summary>
public sealed class ParquetFieldIdResolutionTests
{
    private const string ChangeType = "_change_type";

    [Fact]
    public async Task MixedProjection_DataByFieldId_AndNoIdColumnByName_ResolvesInOnePass()
    {
        // The single call #658 unblocks for the CDF explicit read: project id-mapped DATA columns ALONGSIDE the
        // engine-synthesized _change_type (never column-mapped, no field_id) in ONE ReadAsync. The physical
        // data column NAMES (z1/z2) deliberately DIVERGE from the requested logical names (id/name) — and are in
        // the OPPOSITE order — so ONLY field-id resolution can locate them; a by-name read would miss. The
        // _change_type physical name matches (it is read by name).
        var physical = new StructType(new[]
        {
            PhysFieldWithId("z2", DataTypes.StringType, nullable: true, id: 2),    // logical "name" (field_id 2)
            PhysFieldWithId("z1", DataTypes.LongType, nullable: false, id: 1),     // logical "id"   (field_id 1)
            new StructField(ChangeType, DataTypes.StringType, nullable: false),    // engine col, NO field_id
        });
        byte[] bytes = await WriteAsync(physical, batch =>
        {
            AppendString(batch[0], "alice");
            AppendLong(batch[1], 100);
            AppendString(batch[2], "insert");

            AppendString(batch[0], "bob");
            AppendLong(batch[1], 200);
            AppendString(batch[2], "update_postimage");
        });

        var requested = new StructType(new[]
        {
            IdField("id", DataTypes.LongType, nullable: false, id: 1),
            IdField("name", DataTypes.StringType, nullable: true, id: 2),
            new StructField(ChangeType, DataTypes.StringType, nullable: false),   // no id -> by name
        });

        ColumnBatch batchOut = await ReadSingleAsync(bytes, requested, nullFill: false);

        Assert.Equal(2, batchOut.LogicalRowCount);
        Assert.Equal(new long[] { 100, 200 }, ReadLongs(batchOut.SelectedColumn(0), 2));           // id  <- z1
        Assert.Equal(new[] { "alice", "bob" }, ReadStrings(batchOut.SelectedColumn(1), 2));         // name<- z2
        Assert.Equal(new[] { "insert", "update_postimage" }, ReadStrings(batchOut.SelectedColumn(2), 2)); // _change_type <- by name
    }

    [Fact]
    public async Task NoIdColumn_NameMatchingAnIdBearingFileColumn_FailsClosed_NotSilentlyNameMatched()
    {
        // Fail-closed guarantee (#658): a requested column that SHOULD carry an id but lacks one must NOT be
        // silently accepted where that would mask a foreign/corrupt file. Here the file column `foo` IS
        // column-mapped (field_id 1); a requested `foo` with NO delta.columnMapping.id must not grab it by
        // physical name — the read fails closed rather than reading an id-bearing column as if un-mapped.
        var physical = new StructType(new[]
        {
            PhysFieldWithId("foo", DataTypes.LongType, nullable: false, id: 1),
        });
        byte[] bytes = await WriteAsync(physical, batch => AppendLong(batch[0], 42));

        var requested = new StructType(new[]
        {
            new StructField("foo", DataTypes.LongType, nullable: false),   // NO id, non-nullable
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, requested, nullFill: true));   // null-fill on, but foo is non-nullable
        Assert.Equal(StorageErrorKind.ColumnNotPresentInFile, ex.Kind);
    }

    [Fact]
    public async Task DeclaredIdAbsentFromFooter_FailsClosed_EvenWhenPhysicalNameMatches()
    {
        // Fail-closed guarantee (#658): a column that DECLARES an id whose value is absent from the footer fails
        // closed — it is NEVER silently name-matched. The file column is physically named `foo`; the request
        // declares `foo` with id 99 (absent). Even though a name match exists, id resolution is strict.
        var physical = new StructType(new[]
        {
            PhysFieldWithId("foo", DataTypes.LongType, nullable: false, id: 1),
        });
        byte[] bytes = await WriteAsync(physical, batch => AppendLong(batch[0], 42));

        var requested = new StructType(new[]
        {
            IdField("foo", DataTypes.LongType, nullable: false, id: 99),   // declares id 99 (absent from footer)
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, requested, nullFill: false));
        Assert.Equal(StorageErrorKind.ColumnNotPresentInFile, ex.Kind);
    }

    [Fact]
    public async Task NoIdColumn_AbsentByName_NullFillsWhenNullable()
    {
        // A no-id requested column genuinely absent from the file (no matching un-mapped physical name) null-fills
        // when nullable + null-fill is enabled (#497), alongside an id-resolved data column — all in one pass.
        var physical = new StructType(new[]
        {
            PhysFieldWithId("z1", DataTypes.LongType, nullable: false, id: 1),
        });
        byte[] bytes = await WriteAsync(physical, batch => AppendLong(batch[0], 7));

        var requested = new StructType(new[]
        {
            IdField("id", DataTypes.LongType, nullable: false, id: 1),
            new StructField(ChangeType, DataTypes.StringType, nullable: true),   // no id, absent -> null-fill
        });

        ColumnBatch batchOut = await ReadSingleAsync(bytes, requested, nullFill: true);
        Assert.Equal(1, batchOut.LogicalRowCount);
        Assert.Equal(7L, batchOut.SelectedColumn(0).GetValue<long>(0));
        Assert.True(batchOut.SelectedColumn(1).IsNull(0));
    }

    [Fact]
    public async Task NoIdColumn_NameFallbackOntoTypeMismatchedFileColumn_SchemaMismatches()
    {
        // The by-name fallback is still type-checked: a no-id requested column whose un-mapped file column has a
        // disagreeing physical type is a SchemaMismatch (never silently coerced), same as the name-mode path.
        var physical = new StructType(new[]
        {
            new StructField(ChangeType, DataTypes.LongType, nullable: false),   // file _change_type is LONG, no id
        });
        byte[] bytes = await WriteAsync(physical, batch => AppendLong(batch[0], 1));

        var requested = new StructType(new[]
        {
            new StructField(ChangeType, DataTypes.StringType, nullable: false),   // requested STRING
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, requested, nullFill: false));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    // --- helpers ---

    private static StructField PhysFieldWithId(string name, DataType type, bool nullable, long id) =>
        new(name, type, nullable, FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(id)),
        }));

    private static StructField IdField(string name, DataType type, bool nullable, long id) =>
        PhysFieldWithId(name, type, nullable, id);

    private static async Task<byte[]> WriteAsync(StructType physical, Action<MutableColumnVector[]> fill)
    {
        var columns = new MutableColumnVector[physical.Count];
        for (int i = 0; i < physical.Count; i++)
        {
            columns[i] = ColumnVectors.Create(physical[i].DataType, 4);
        }

        fill(columns);
        int rows = columns.Length == 0 ? 0 : columns[0].Length;
        var batch = new ManagedColumnBatch(physical, columns.Cast<ColumnVector>().ToArray(), rows);

        using var buffer = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(buffer, physical, new[] { batch }, CancellationToken.None);
        return buffer.ToArray();
    }

    private static async Task<ColumnBatch> ReadSingleAsync(byte[] bytes, StructType requested, bool nullFill)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch b in new ParquetFileReader().ReadAsync(
            stream, requested, keepRowGroup: null, nullFillMissingColumns: nullFill,
            allowTypeWideningPromotion: false, resolveByFieldId: true, CancellationToken.None))
        {
            Assert.Null(only);   // the test fixtures fit in a single row group
            only = b;
        }

        return Assert.IsAssignableFrom<ColumnBatch>(only);
    }

    private static void AppendLong(MutableColumnVector v, long value) => v.AppendValue(value);

    private static void AppendString(MutableColumnVector v, string value) =>
        v.AppendBytes(Encoding.UTF8.GetBytes(value));

    private static long[] ReadLongs(ColumnVector v, int count)
    {
        var result = new long[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = v.GetValue<long>(i);
        }

        return result;
    }

    private static string?[] ReadStrings(ColumnVector v, int count)
    {
        var result = new string?[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = v.IsNull(i) ? null : Encoding.UTF8.GetString(v.GetBytes(i));
        }

        return result;
    }
}
