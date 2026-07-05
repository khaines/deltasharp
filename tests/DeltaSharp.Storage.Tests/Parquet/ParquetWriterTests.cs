using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Cross-engine standards-compliance checks: a DeltaSharp-written Parquet file is read back with
/// Parquet.Net <b>directly</b> (not our reader) and asserted to carry equivalent data (AC1), and the
/// footer must expose per-column statistics (AC3) plus the Delta/Spark schema metadata.
/// </summary>
public sealed class ParquetWriterTests
{
    private static readonly StructType Schema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("amount", DataTypes.CreateDecimalType(10, 2), nullable: true),
        new StructField("label", DataTypes.StringType, nullable: true),
    });

    private static readonly long[] Ids = { 10L, 20L, 30L, 40L };
    private static readonly long?[] AmountsUnscaled = { 12345L, null, -678L, 0L };
    private static readonly string?[] Labels = { "alpha", string.Empty, null, "üni" };

    private static ColumnBatch BuildKnownBatch()
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, Ids.Length);
        MutableColumnVector amount = ColumnVectors.Create(DataTypes.CreateDecimalType(10, 2), Ids.Length);
        MutableColumnVector label = ColumnVectors.Create(DataTypes.StringType, Ids.Length);
        for (int i = 0; i < Ids.Length; i++)
        {
            id.AppendValue(Ids[i]);

            if (AmountsUnscaled[i] is long unscaled)
            {
                amount.AppendValue(unscaled);
            }
            else
            {
                amount.AppendNull();
            }

            if (Labels[i] is string text)
            {
                label.AppendBytes(System.Text.Encoding.UTF8.GetBytes(text));
            }
            else
            {
                label.AppendNull();
            }
        }

        return new ManagedColumnBatch(Schema, new ColumnVector[] { id, amount, label }, Ids.Length);
    }

    [Fact]
    public async Task WrittenFile_IsReadableByParquetNetDirectly()
    {
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(stream, Schema, new[] { BuildKnownBatch() }, CancellationToken.None);
        stream.Position = 0;

        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        Assert.Equal(1, reader.RowGroupCount);

        DataField[] fields = reader.Schema.DataFields;
        DataField idField = Array.Find(fields, f => f.Name == "id")!;
        DataField amountField = Array.Find(fields, f => f.Name == "amount")!;
        DataField labelField = Array.Find(fields, f => f.Name == "label")!;

        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        Assert.Equal(Ids.Length, rowGroup.RowCount);

        var idBuffer = new long[rowGroup.RowCount];
        await rowGroup.ReadAsync<long>(idField, idBuffer.AsMemory(), null, CancellationToken.None);
        Assert.Equal(Ids, idBuffer);

        var amountBuffer = new decimal?[rowGroup.RowCount];
        await rowGroup.ReadAsync<decimal>(amountField, amountBuffer.AsMemory(), null, CancellationToken.None);
        Assert.Equal(new decimal?[] { 123.45m, null, -6.78m, 0.00m }, amountBuffer);

        var labelBuffer = new string?[rowGroup.RowCount];
        await rowGroup.ReadAsync(labelField, labelBuffer.AsMemory(), null, CancellationToken.None);
        Assert.Equal(Labels, labelBuffer);
    }

    [Fact]
    public async Task WrittenFile_ExposesColumnStatistics()
    {
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(stream, Schema, new[] { BuildKnownBatch() }, CancellationToken.None);
        stream.Position = 0;

        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        DataField idField = Array.Find(reader.Schema.DataFields, f => f.Name == "id")!;
        DataField amountField = Array.Find(reader.Schema.DataFields, f => f.Name == "amount")!;

        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);

        DataColumnStatistics? idStats = rowGroup.GetStatistics(idField);
        Assert.NotNull(idStats);
        Assert.NotNull(idStats.MinValue);
        Assert.NotNull(idStats.MaxValue);
        Assert.Equal(10L, idStats.MinValue);
        Assert.Equal(40L, idStats.MaxValue);
        Assert.Equal(0L, idStats.NullCount);

        DataColumnStatistics? amountStats = rowGroup.GetStatistics(amountField);
        Assert.NotNull(amountStats);
        Assert.Equal(1L, amountStats.NullCount);
    }

    [Fact]
    public async Task WrittenFile_CarriesDeltaSchemaMetadata()
    {
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(stream, Schema, new[] { BuildKnownBatch() }, CancellationToken.None);
        stream.Position = 0;

        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);

        Assert.True(reader.CustomMetadata.ContainsKey(DeltaSchemaJson.SchemaMetadataKey));
        string schemaJson = reader.CustomMetadata[DeltaSchemaJson.SchemaMetadataKey];
        Assert.Contains("\"type\":\"struct\"", schemaJson);
        Assert.Contains("\"name\":\"id\"", schemaJson);
        Assert.Contains("\"name\":\"amount\"", schemaJson);
        Assert.True(reader.CustomMetadata.ContainsKey(DeltaSchemaJson.WriterMetadataKey));
    }

    [Fact]
    public async Task WriteAsync_CancelledToken_ThrowsOnMultiRowGroupStringWrite()
    {
        // CF-8: the writer honors cancellation at row-group granularity for ALL schemas (previously only
        // the reader did). A cancelled multi-row-group string write surfaces OperationCanceledException
        // rather than running to completion. ParquetWriter.CreateAsync does NOT observe the token, so the
        // writer's own per-row-group check is the first observation point (non-vacuous: removing it lets
        // the write complete).
        var schema = new StructType(new[] { new StructField("s", DataTypes.StringType, nullable: false) });
        const int rows = 5000;
        MutableColumnVector s = ColumnVectors.Create(DataTypes.StringType, rows);
        for (int i = 0; i < rows; i++)
        {
            s.AppendBytes(System.Text.Encoding.UTF8.GetBytes(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"row-{i}")));
        }

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { s }, rows);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var stream = new MemoryStream();

        // rowGroupRowLimit small so the write spans multiple row groups absent cancellation.
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await new ParquetFileWriter(rowGroupRowLimit: 1024)
                .WriteAsync(stream, schema, new[] { batch }, cts.Token));
    }

    [Fact]
    public async Task WriteAsync_CancelledToken_ThrowsOnMultiRowGroupNumericWrite()
    {
        // RF-6: a NUMERIC column has no per-row cancellation check (unlike string/binary), so the writer's
        // row-group-loop check is the ONLY observation point for a numeric schema. A cancelled
        // multi-row-group long write must still surface OperationCanceledException. Non-vacuous: deleting
        // the loop-level ThrowIfCancellationRequested lets this numeric write run to completion (the string
        // CF-8 test would not catch that regression because its per-row check masks it).
        var schema = new StructType(new[] { new StructField("n", DataTypes.LongType, nullable: false) });
        const int rows = 5000;
        MutableColumnVector n = ColumnVectors.Create(DataTypes.LongType, rows);
        for (int i = 0; i < rows; i++)
        {
            n.AppendValue((long)i);
        }

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { n }, rows);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await new ParquetFileWriter(rowGroupRowLimit: 1024)
                .WriteAsync(stream, schema, new[] { batch }, cts.Token));
    }
}
