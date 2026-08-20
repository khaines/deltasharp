using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// §2.4b post-write footer row-count reconciliation and the #497 write-door's nested structural match.
/// </summary>
/// <remarks>
/// Reconciliation is the only control that catches a shredder defect which produces a SELF-CONSISTENT but
/// row-losing encoding (e.g. a run of continuation slots that never opens a row): the level guard would pass
/// it and the reader would decode it happily, but the footer's <c>NumRows</c> would disagree with the batches
/// the caller handed in — and the Delta <c>add</c> action would then advertise a row count the file does not
/// contain. The reconciler is driven directly with a deliberately wrong reference here, because provoking it
/// through the shredder would require first introducing the very defect it exists to catch.
/// </remarks>
public sealed class NestedParquetWriteDoorTests
{
    private static readonly ArrayType IntArrayType = DataTypes.CreateArrayType(DataTypes.IntegerType);

    private static readonly StructType InnerType = DataTypes.CreateStructType(new[]
    {
        new StructField("a", DataTypes.IntegerType, nullable: true),
        new StructField("b", DataTypes.StringType, nullable: true),
    });

    // ----- §2.4b reconciliation -----

    [Fact]
    public async Task Reconciliation_AcceptsAFileWhoseFooterAgreesWithTheBatches()
    {
        (byte[] bytes, long rows) = await WriteNestedAsync();

        using var stream = new MemoryStream(bytes, writable: true);
        stream.Position = stream.Length;

        await ParquetFileWriter.ReconcileFooterRowCountAsync(stream, 0, rows, CancellationToken.None);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public async Task Reconciliation_FailsClosedWhenTheFooterDisagreesWithTheReference(long claimedRows)
    {
        (byte[] bytes, long rows) = await WriteNestedAsync();
        Assert.NotEqual(claimedRows, rows);

        using var stream = new MemoryStream(bytes, writable: true);
        stream.Position = stream.Length;

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetFileWriter.ReconcileFooterRowCountAsync(
                stream, 0, claimedRows, CancellationToken.None));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains($"{rows} row(s) in its footer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconciliation_ReadsOnlyTheWindowItWasGiven_AndRestoresThePosition()
    {
        // The three production write sites hand the reconciler an output stream that may already hold
        // unrelated bytes (a stage buffer reused across files), so the window offset — not position 0 — is
        // the file's origin, and the position must come back untouched for the byteSize measurement.
        (byte[] bytes, long rows) = await WriteNestedAsync();
        byte[] prefix = new byte[37];
        Random.Shared.NextBytes(prefix);

        using var stream = new MemoryStream();
        stream.Write(prefix);
        long start = stream.Position;
        stream.Write(bytes);
        long end = stream.Position;

        await ParquetFileWriter.ReconcileFooterRowCountAsync(stream, start, rows, CancellationToken.None);

        Assert.Equal(end, stream.Position);
        Assert.Equal(prefix, stream.ToArray()[..prefix.Length]);
    }

    [Fact]
    public async Task Reconciliation_FailsClosedOnANonSeekableOutput()
    {
        (byte[] bytes, long rows) = await WriteNestedAsync();
        await using var stream = new NonSeekableStream(bytes);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetFileWriter.ReconcileFooterRowCountAsync(stream, 0, rows, CancellationToken.None));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("not seekable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteWithStatistics_ReportsARecordCountTheFooterAgreesWith()
    {
        // The binding assertion for all three production sites (DeltaWriteTarget.StageAsync,
        // DeltaOptimize.WriteCompactedFileAsync, ChangeDataWriter): they all funnel through this entry
        // point, and the count they publish in add.numRecords is now footer-reconciled.
        var schema = DataTypes.CreateStructType(new[] { new StructField("a", IntArrayType, nullable: true) });
        var rows = new int?[]?[] { new int?[] { 1, 2 }, null, Array.Empty<int?>(), new int?[] { 3 } };
        var batch = new ManagedColumnBatch(
            schema, new ColumnVector[] { NestedVectors.IntList(IntArrayType, rows) }, rows.Length);

        using var output = new MemoryStream();
        ParquetFileWriter.WriteResult result = await new ParquetFileWriter().WriteWithStatisticsAsync(
            output, schema, new[] { batch }, StatisticsPolicy.Default, CancellationToken.None);

        Assert.Equal(rows.Length, result.RowCount);

        using var read = new MemoryStream(output.ToArray(), writable: false);
        Assert.Equal(rows.Length, await new ParquetFileReader().GetRowCountAsync(read, CancellationToken.None));
    }

    // ----- #497 write-door structural match -----

    [Fact]
    public async Task WriteDoor_AcceptsANestedFooterThatMatchesTheTableSchemaStructurally()
    {
        // The door compares the FOOTER's re-derived shape against the table schema. Parquet emits every
        // nested container as OPTIONAL and carries no Delta metadata, so the comparator must be nullability-
        // and metadata-INSENSITIVE inside the nested tree — otherwise a perfectly valid nested write would
        // be rejected at commit.
        var schema = DataTypes.CreateStructType(new[]
        {
            new StructField("s", InnerType, nullable: true),
            new StructField("a", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: false), nullable: true),
            new StructField(
                "m",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: false),
                nullable: true),
        });

        var batch = new ManagedColumnBatch(
            schema,
            new ColumnVector[]
            {
                NestedVectors.IntStringStruct(InnerType, new (int? A, string? B)?[] { (1, "x"), null }),
                NestedVectors.IntList(
                    (ArrayType)schema.Fields[1].DataType, new int?[]?[] { new int?[] { 1 }, null }),
                NestedVectors.StringIntMap(
                    (MapType)schema.Fields[2].DataType,
                    new IReadOnlyList<(string Key, int? Value)>?[] { new[] { ("k", (int?)5) }, null }),
            },
            2);

        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });

        using var stream = new MemoryStream(bytes, writable: false);
        StructType footer = await new ParquetFileReader().ReadDataSchemaAsync(stream, CancellationToken.None);

        Assert.Equal(3, footer.Fields.Count);
        Assert.Equal(new[] { "s", "a", "m" }, footer.Fields.Select(f => f.Name));
        Assert.IsType<StructType>(footer.Fields[0].DataType);
        Assert.IsType<ArrayType>(footer.Fields[1].DataType);
        Assert.IsType<MapType>(footer.Fields[2].DataType);

        // The recursive comparator's job: nullability/metadata differ, the SHAPE does not.
        Assert.True(DeltaTableWriter.DataColumnsMatch(schema, footer));
    }

    [Fact]
    public async Task WriteDoor_StillRejectsAGenuineNestedShapeDifference()
    {
        // The leniency extends inward, it does not open the door: a renamed struct child, a different leaf
        // type, and a different container kind must all still be refused.
        var schema = DataTypes.CreateStructType(new[] { new StructField("s", InnerType, nullable: true) });
        var batch = new ManagedColumnBatch(
            schema,
            new ColumnVector[]
            {
                NestedVectors.IntStringStruct(InnerType, new (int? A, string? B)?[] { (1, "x") }),
            },
            1);

        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        using var stream = new MemoryStream(bytes, writable: false);
        StructType footer = await new ParquetFileReader().ReadDataSchemaAsync(stream, CancellationToken.None);

        var renamedChild = DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: true),
            new StructField("RENAMED", DataTypes.StringType, nullable: true),
        });
        var widenedLeaf = DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.LongType, nullable: true),
            new StructField("b", DataTypes.StringType, nullable: true),
        });
        var extraChild = DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: true),
            new StructField("b", DataTypes.StringType, nullable: true),
            new StructField("c", DataTypes.IntegerType, nullable: true),
        });

        foreach (DataType mismatch in new DataType[]
        {
            renamedChild, widenedLeaf, extraChild, DataTypes.CreateArrayType(DataTypes.IntegerType),
        })
        {
            var candidate = DataTypes.CreateStructType(new[] { new StructField("s", mismatch, nullable: true) });
            Assert.False(DeltaTableWriter.DataColumnsMatch(candidate, footer));
        }
    }

    [Fact]
    public async Task FooterSchema_SurvivesADeepButInScopeTree_AndBoundsRecursion()
    {
        // ToDataSchema is recursive over the footer; it must decode the in-scope tree and refuse (rather
        // than overflow) anything past the shared MaxDepth bound. The in-scope side is asserted here; the
        // bound itself is a constant shared with DeltaWriteSchemaEligibility.
        var schema = DataTypes.CreateStructType(new[] { new StructField("s", InnerType, nullable: true) });
        var batch = new ManagedColumnBatch(
            schema,
            new ColumnVector[]
            {
                NestedVectors.IntStringStruct(InnerType, new (int? A, string? B)?[] { (7, "deep") }),
            },
            1);

        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        using var stream = new MemoryStream(bytes, writable: false);
        StructType footer = await new ParquetFileReader().ReadDataSchemaAsync(stream, CancellationToken.None);

        var inner = Assert.IsType<StructType>(footer.Fields[0].DataType);
        Assert.Equal(new[] { "a", "b" }, inner.Fields.Select(f => f.Name));
        Assert.Equal(DataTypes.IntegerType, inner.Fields[0].DataType);
        Assert.Equal(DataTypes.StringType, inner.Fields[1].DataType);
        Assert.True(ParquetTypeMapping.MaxFooterTypeDepth >= 64);
    }

    private static async Task<(byte[] Bytes, long Rows)> WriteNestedAsync()
    {
        var schema = DataTypes.CreateStructType(new[] { new StructField("a", IntArrayType, nullable: true) });
        var rows = new int?[]?[] { new int?[] { 1, 2 }, null, Array.Empty<int?>(), new int?[] { 3 }, null };
        var batch = new ManagedColumnBatch(
            schema, new ColumnVector[] { NestedVectors.IntList(IntArrayType, rows) }, rows.Length);

        return (await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }), rows.Length);
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
