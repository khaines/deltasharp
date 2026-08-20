using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Tests.Delta.Simulation;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// The §2.4b post-write footer row-count reconciliation, driven through a REAL
/// <see cref="ParquetFileWriter.WriteAsync"/> and through the production write paths that bind to it.
/// </summary>
/// <remarks>
/// <para>Calling <c>ReconcileFooterRowCountAsync</c> directly with a deliberately wrong reference proves the
/// comparison works, but says nothing about whether the check is still WIRED into the write. Unwiring it from
/// <c>WriteAsync</c>'s core left the whole suite green — so every cell below drives the real writer.</para>
/// <para>The fault is injected at the row-group SEGMENTATION seam, which produces exactly the defect class
/// §2.3c is blind to: every leaf in every row group is locally level-valid (the levels are computed from the
/// perturbed segments, so they agree with themselves), yet the file's footer <c>NumRows</c> no longer equals
/// the sum of the input batches' <c>LogicalRowCount</c>. Only the post-write reconciliation can see it.</para>
/// </remarks>
public sealed class NestedParquetReconciliationWiringTests
{
    private static readonly ArrayType IntArrayType = DataTypes.CreateArrayType(DataTypes.IntegerType);

    // Truncates the last segment of every row group by one row — the row-LOSING defect.
    private static int DropOneRow(List<ParquetFileWriter.Segment> segments, int size)
    {
        ParquetFileWriter.Segment last = segments[^1];
        if (last.Length <= 1)
        {
            return size;
        }

        segments[^1] = new ParquetFileWriter.Segment(last.Batch, last.Start, last.Length - 1);
        return size - 1;
    }

    // Re-emits the last segment — the row-DUPLICATING defect.
    private static int DuplicateOneSegment(List<ParquetFileWriter.Segment> segments, int size)
    {
        ParquetFileWriter.Segment last = segments[^1];
        segments.Add(last);
        return size + last.Length;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WriteAsync_FailsClosedWhenTheFooterRowCountDivergesFromTheBatches(bool drop)
    {
        (StructType schema, ManagedColumnBatch batch) = NestedBatch();
        var writer = new ParquetFileWriter
        {
            RowGroupSegmentFault = drop ? DropOneRow : DuplicateOneSegment,
        };

        using var output = new MemoryStream();
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => writer.WriteAsync(output, schema, new[] { batch }, CancellationToken.None));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("in its footer but", error.Message, StringComparison.Ordinal);
        Assert.Contains("structurally inconsistent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_ReconcilesEveryRowGroupNotJustTheLast()
    {
        // The fault fires per row group, so with a 2-row cap over 5 rows the divergence accumulates across
        // three groups. This pins that the reference is the WHOLE-FILE batch total, not a per-group check.
        (StructType schema, ManagedColumnBatch batch) = NestedBatch();
        var writer = new ParquetFileWriter(rowGroupRowLimit: 2)
        {
            RowGroupSegmentFault = DropOneRow,
        };

        using var output = new MemoryStream();
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => writer.WriteAsync(output, schema, new[] { batch }, CancellationToken.None));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("in its footer but 5 row(s) were written", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_WithNoFault_Reconciles()
    {
        // Non-vacuity: the identical write with the seam left at its production value (null) must SUCCEED,
        // or every cell above could be passing because the reconciliation rejects everything.
        (StructType schema, ManagedColumnBatch batch) = NestedBatch();

        using var output = new MemoryStream();
        await new ParquetFileWriter(rowGroupRowLimit: 2)
            .WriteAsync(output, schema, new[] { batch }, CancellationToken.None);

        Assert.True(output.Length > 0);
    }

    [Fact]
    public async Task WriteAsync_ReconcilesAScalarOnlyFileToo()
    {
        // §2.4b is bound inside WriteAsync's CORE, not on the nested lane, so a purely scalar write is
        // reconciled as well — the property the three production paths below rely on.
        var schema = DataTypes.CreateStructType(new[]
        {
            new StructField("v", DataTypes.IntegerType, nullable: true),
        });
        MutableColumnVector column = ColumnVectors.Create(DataTypes.IntegerType, 4);
        for (int i = 0; i < 4; i++)
        {
            column.AppendValue(i);
        }

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { column }, 4);
        var writer = new ParquetFileWriter { RowGroupSegmentFault = DropOneRow };

        using var output = new MemoryStream();
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => writer.WriteAsync(output, schema, new[] { batch }, CancellationToken.None));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("in its footer but 4 row(s) were written", error.Message, StringComparison.Ordinal);
    }

    // ----- per-path binding -----

    [Fact]
    public async Task ChangeDataWriter_IsBoundToTheReconciliation_AndPublishesNothingWhenItFires()
    {
        // The cdc path is the reason the hook lives in WriteAsync's CORE rather than in
        // WriteWithStatisticsAsync: ChangeDataWriter calls the void-returning WriteAsync and never sees a
        // row count it could check itself. It buffers to memory and publishes through the staged-write door
        // only after the write returns, so a reconciliation failure must leave the backend untouched.
        var backend = new InMemoryStorageBackend();
        var writer = new ChangeDataWriter(
            backend, new ParquetFileWriter { RowGroupSegmentFault = DropOneRow });

        var schema = DataTypes.CreateStructType(new[]
        {
            new StructField("v", DataTypes.IntegerType, nullable: true),
        });
        MutableColumnVector column = ColumnVectors.Create(DataTypes.IntegerType, 3);
        for (int i = 0; i < 3; i++)
        {
            column.AppendValue(i);
        }

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { column }, 3);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => writer.WriteAsync(
                schema, new[] { batch }, ChangeDataWriter.InsertChange, "cafe", CancellationToken.None));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.False(backend.HasObject(ChangeDataWriter.ChangeDataDirectory + "/cdc-cafe.parquet"));
    }

    [Fact]
    public async Task ChangeDataWriter_PublishesWhenTheReconciliationPasses()
    {
        // Non-vacuity for the binding cell above.
        var backend = new InMemoryStorageBackend();
        var writer = new ChangeDataWriter(backend);

        var schema = DataTypes.CreateStructType(new[]
        {
            new StructField("v", DataTypes.IntegerType, nullable: true),
        });
        MutableColumnVector column = ColumnVectors.Create(DataTypes.IntegerType, 3);
        for (int i = 0; i < 3; i++)
        {
            column.AppendValue(i);
        }

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { column }, 3);

        ChangeDataWriter.ChangeDataFile file = await writer.WriteAsync(
            schema, new[] { batch }, ChangeDataWriter.InsertChange, "beef", CancellationToken.None);

        Assert.Equal(3, file.RowCount);
        Assert.True(backend.HasObject(file.Path));
    }

    private static (StructType Schema, ManagedColumnBatch Batch) NestedBatch()
    {
        var schema = DataTypes.CreateStructType(new[] { new StructField("a", IntArrayType, nullable: true) });
        var rows = new int?[]?[]
        {
            new int?[] { 1, 2 }, null, Array.Empty<int?>(), new int?[] { 3, 4, 5 }, new int?[] { 6 },
        };
        return (
            schema,
            new ManagedColumnBatch(
                schema, new ColumnVector[] { NestedVectors.IntList(IntArrayType, rows) }, rows.Length));
    }
}
