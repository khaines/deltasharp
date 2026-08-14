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
}
