using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Teeth for #190/FIX-1 (CRITICAL): proves <b>why</b> logical type widening (e.g. <c>int → long</c>) must be
/// fail-closed until the Delta <c>typeWidening</c> table feature exists (#495). The Parquet reader binds a
/// requested column to the file's <b>exact physical CLR type</b>
/// (<see cref="Parquet.ParquetFileReader"/> / <c>ValidateFileField</c>): an <c>Int32</c> file cannot be read
/// back under a widened <c>long</c> schema — it throws <see cref="StorageErrorKind.SchemaMismatch"/>. So if
/// the enforcer had allowed the table schema to widen from <c>int</c> to <c>long</c>, every already-written
/// Parquet file would become UNREADABLE even by DeltaSharp itself (silent, whole-column data corruption).
///
/// <para>The companion enforcement teeth (widening rejected before any commit) live in
/// <see cref="DeltaSchemaEvolutionWriterTests.Append_WidenType_IsRejectedBeforeAnyCommit"/> and the enforcer
/// unit tests; this test pins the underlying read-back invariant that justifies fail-closing.</para>
/// </summary>
public sealed class DeltaSchemaWideningReadBackTests
{
    [Fact]
    public async Task WidenedReadBack_OfInt32FileUnderLongSchema_IsUnreadable_JustifyingFailClose()
    {
        // Write a plain Int32 column — the physical layout an int-typed table produces today.
        var writtenSchema = new StructType(new[] { new StructField("value", DataTypes.IntegerType, nullable: false) });
        MutableColumnVector column = ColumnVectors.Create(DataTypes.IntegerType, capacity: 3);
        column.AppendValue(1);
        column.AppendValue(2);
        column.AppendValue(3);
        var batch = new ManagedColumnBatch(writtenSchema, new ColumnVector[] { column }, rowCount: 3);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(writtenSchema, new[] { batch });

        // Reading the SAME file under a widened `long` schema (what int→long evolution would install as the
        // table schema) is rejected on physical type — the file is now unreadable, i.e. corrupted-at-read.
        var widenedSchema = new StructType(new[] { new StructField("value", DataTypes.LongType, nullable: false) });
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(bytes, widenedSchema));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);

        // Control: reading it back under its own (un-widened) schema still succeeds — proving the failure is
        // caused specifically by the widening, not by a malformed file.
        System.Collections.Generic.List<ColumnBatch> ok = await ParquetTestHelpers.ReadAllAsync(bytes, writtenSchema);
        Assert.Equal(3, ok.Single().RowCount);
    }
}
