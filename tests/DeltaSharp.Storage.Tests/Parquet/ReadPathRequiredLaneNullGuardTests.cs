using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// #807: the read path's SCHEMA-level nullability guard (fileField.IsNullable && !expected.IsNullable) is
/// structurally inert for string/binary — <c>expected</c> is built with honorReferenceNullability:false, so
/// always-nullable by construction — deliberately, so a foreign / pre-#730 file that stores a log-REQUIRED
/// string/binary column as physically OPTIONAL still reads. That tolerance must not silently null-fill a
/// requested NON-nullable lane with an actual null. These tests pin the VALUE-level required-lane guard
/// (ParquetFileReader.RejectNullInRequiredLane): a physically-OPTIONAL string/binary column read into a
/// requested non-nullable field fails closed on the FIRST materialized null, while an all-non-null OPTIONAL
/// column still reads cleanly.
/// </summary>
public sealed class ReadPathRequiredLaneNullGuardTests
{
    // A physically-OPTIONAL Parquet column is produced by declaring the WRITE field nullable:true (the writer
    // emits string/binary as OPTIONAL for a declared-nullable column, #730). The READ field is what the caller
    // requests; nullable:false is the required lane the guard protects.
    private static async Task<byte[]> WriteOptionalAsync(DataType type, Action<MutableColumnVector> fill, int rows)
    {
        MutableColumnVector column = ColumnVectors.Create(type, capacity: rows);
        fill(column);
        var schema = new StructType(new[] { new StructField("v", type, nullable: true) });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { column }, rows);
        return await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
    }

    // A physically-OPTIONAL column split across MULTIPLE row groups (rowGroupRowLimit), so a null in a LATER
    // group proves the guard fires per row group, not only in the first.
    private static async Task<byte[]> WriteOptionalMultiGroupAsync(
        DataType type, Action<MutableColumnVector> fill, int rows, int rowGroupRowLimit)
    {
        MutableColumnVector column = ColumnVectors.Create(type, capacity: rows);
        fill(column);
        var schema = new StructType(new[] { new StructField("v", type, nullable: true) });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { column }, rows);
        return await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }, rowGroupRowLimit);
    }

    // A physically-REQUIRED column: declaring the WRITE field nullable:false makes the writer emit the
    // string/binary column REQUIRED (#730) — the actual steady-state output for a non-nullable Delta column.
    private static async Task<byte[]> WriteRequiredAsync(DataType type, Action<MutableColumnVector> fill, int rows)
    {
        MutableColumnVector column = ColumnVectors.Create(type, capacity: rows);
        fill(column);
        var schema = new StructType(new[] { new StructField("v", type, nullable: false) });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { column }, rows);
        return await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
    }

    private static Task<List<ColumnBatch>> ReadAsAsync(byte[] bytes, DataType type, bool nullable) =>
        ParquetTestHelpers.ReadAllAsync(
            bytes, new StructType(new[] { new StructField("v", type, nullable) }));

    private static void AppendString(MutableColumnVector v, string? s)
    {
        if (s is null)
        {
            v.AppendNull();
        }
        else
        {
            v.AppendBytes(Encoding.UTF8.GetBytes(s));
        }
    }

    private static void AppendBinary(MutableColumnVector v, byte[]? b)
    {
        if (b is null)
        {
            v.AppendNull();
        }
        else
        {
            v.AppendBytes(b);
        }
    }

    [Fact]
    public async Task RequiredStringColumn_PhysicallyOptional_WithNull_FailsClosed()
    {
        byte[] bytes = await WriteOptionalAsync(
            DataTypes.StringType, v => { AppendString(v, "east"); AppendString(v, null); AppendString(v, "west"); }, 3);

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadAsAsync(bytes, DataTypes.StringType, nullable: false));

        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#807", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiredBinaryColumn_PhysicallyOptional_WithNull_FailsClosed()
    {
        byte[] bytes = await WriteOptionalAsync(
            DataTypes.BinaryType,
            v => { AppendBinary(v, new byte[] { 1, 2 }); AppendBinary(v, null); AppendBinary(v, new byte[] { 3 }); }, 3);

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadAsAsync(bytes, DataTypes.BinaryType, nullable: false));

        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
        Assert.Contains("#807", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiredStringColumn_PhysicallyOptional_NoNulls_StillReads()
    {
        // The foreign / pre-#730 tolerance: an OPTIONAL string column that holds NO nulls must still read into
        // a requested non-nullable lane — the guard rejects only on a real materialized null, not on the
        // physical OPTIONAL repetition alone.
        byte[] bytes = await WriteOptionalAsync(
            DataTypes.StringType, v => { AppendString(v, "east"); AppendString(v, "west"); }, 2);

        List<ColumnBatch> read = await ReadAsAsync(bytes, DataTypes.StringType, nullable: false);
        ColumnVector column = read.Single().Column(0);
        Assert.Equal(2, column.Length);
        Assert.False(column.IsNull(0));
        Assert.False(column.IsNull(1));
    }

    [Fact]
    public async Task NullableStringColumn_PhysicallyOptional_WithNull_ReadsNullThrough()
    {
        // The requested lane is nullable, so the null is legal and preserved — the guard only bites a required
        // (non-nullable) lane.
        byte[] bytes = await WriteOptionalAsync(
            DataTypes.StringType, v => { AppendString(v, "east"); AppendString(v, null); }, 2);

        List<ColumnBatch> read = await ReadAsAsync(bytes, DataTypes.StringType, nullable: true);
        ColumnVector column = read.Single().Column(0);
        Assert.Equal(2, column.Length);
        Assert.False(column.IsNull(0));
        Assert.True(column.IsNull(1));
    }

    [Fact]
    public async Task RequiredStringColumn_PhysicallyOptional_NullInLaterRowGroup_FailsClosed()
    {
        // The guard runs per row group (ReadStringAsync is invoked per group): the null sits in the SECOND
        // group, proving it is not a first-group-only check.
        byte[] bytes = await WriteOptionalMultiGroupAsync(
            DataTypes.StringType,
            v => { AppendString(v, "a"); AppendString(v, "b"); AppendString(v, "c"); AppendString(v, null); },
            rows: 4, rowGroupRowLimit: 2);

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadAsAsync(bytes, DataTypes.StringType, nullable: false));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
        Assert.Contains("#807", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiredStringColumn_PhysicallyRequired_ReadsCleanly()
    {
        // The #730 steady state: a nullable:false Delta column is written as a physically REQUIRED Parquet
        // column. Reading it into a required lane must NOT trip the value-level guard (there are no nulls, and
        // the physical repetition already matches the request).
        byte[] bytes = await WriteRequiredAsync(
            DataTypes.StringType, v => { AppendString(v, "east"); AppendString(v, "west"); }, 2);

        List<ColumnBatch> read = await ReadAsAsync(bytes, DataTypes.StringType, nullable: false);
        ColumnVector column = read.Single().Column(0);
        Assert.Equal(2, column.Length);
        Assert.False(column.IsNull(0));
        Assert.False(column.IsNull(1));
    }

    [Fact]
    public async Task RequiredValueColumn_PhysicallyOptional_FailsAtSchemaLevel_NotValueGuard()
    {
        // Control for the string/binary-vs-value asymmetry the #807 guard exists to complete: for a VALUE type,
        // `expected.IsNullable` tracks the declared flag, so a physically-OPTIONAL value column read as
        // non-nullable is rejected by the SCHEMA-level guard (ValidateFileField) BEFORE any value is decoded —
        // note the message carries NO "#807" marker (that is the value-level guard's), proving which guard fired.
        // No null is even needed: the schema guard bites on the OPTIONAL-vs-required repetition alone.
        byte[] bytes = await WriteOptionalAsync(
            DataTypes.LongType, v => { v.AppendValue(1L); v.AppendValue(2L); }, 2);

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadAsAsync(bytes, DataTypes.LongType, nullable: false));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
        Assert.DoesNotContain("#807", ex.Message, StringComparison.Ordinal);
    }
}
