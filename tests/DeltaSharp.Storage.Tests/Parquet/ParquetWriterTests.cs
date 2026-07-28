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

        // #679: this is the ARTIFACT assertion — it pins the bytes actually stamped into the written
        // Parquet footer, rather than what the serializer helper would have produced.
        //
        // Every other guard in this area — the reflection single-source guards, the DeltaSchemaJson
        // goldens — answers "who is allowed to serialize?". That is a question about PROVENANCE, and
        // an author can always evade it by picking a different tool: a Storage-local StringBuilder
        // serializer wired into ParquetFileWriter's call site produces a genuine footer/log
        // divergence while every provenance guard stays green, because they key on Utf8JsonWriter and
        // a StringBuilder is not one. Writing JSON does not require Utf8JsonWriter.
        //
        // These two assertions instead ask "are the bytes right?" — a question about OUTCOME, which a
        // rogue serializer cannot dodge BY CHOICE OF TOOL, because the bytes read back here are the
        // ones that ship inside the Parquet footer.
        //
        // SCOPE — read this before relying on the sentence above. Outcome coverage is bounded by the
        // CORPUS, and THIS test's corpus is one schema: three atomic fields with EMPTY metadata. A
        // rogue that is byte-exact for that shape and diverges only elsewhere would pass here. The
        // reachable part of that gap is closed by the two sibling tests below, which extend the
        // artifact layer over field metadata (including the #330 unquoted-integer column-mapping id
        // contract) and over every scalar type the writer accepts. What remains uncovered at this
        // layer is only the part that is INHERENTLY unreachable: nested types cannot be written at
        // all today, because ParquetTypeMapping.CreateField rejects array/map/struct with
        // UnsupportedFeature (design §2.9), so no artifact test can pin them until that lands. They
        // are pinned at the helper layer meanwhile. Tracked in #713.
        //
        // The three Assert.Contains calls below are substring checks: blind to field ORDER, to
        // property order within a field, and to anything additional, so they could never have caught
        // the call-site swap this assertion exists for.
        //
        // The two assertions are deliberately not redundant:
        //   * equality with SchemaJson.ToJson catches the footer being repointed at some OTHER
        //     serializer while the shared one is untouched (the call-site swap);
        //   * the golden catches drift INSIDE the shared serializer reaching the footer, which the
        //     equality check cannot see because both sides would move together.
        // Together they pin footer bytes == shared serializer == fixed wire shape, for this corpus.
        const string footerGolden =
            "{\"type\":\"struct\",\"fields\":[" +
            "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{}}," +
            "{\"name\":\"amount\",\"type\":\"decimal(10,2)\",\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"label\",\"type\":\"string\",\"nullable\":true,\"metadata\":{}}]}";
        Assert.Equal(footerGolden, schemaJson);
        Assert.Equal(SchemaJson.ToJson(Schema), schemaJson);

        Assert.Contains("\"type\":\"struct\"", schemaJson);
        Assert.Contains("\"name\":\"id\"", schemaJson);
        Assert.Contains("\"name\":\"amount\"", schemaJson);
        Assert.True(reader.CustomMetadata.ContainsKey(DeltaSchemaJson.WriterMetadataKey));
    }

    /// <summary>
    /// Reads the Delta schema string back out of a footer written for <paramref name="schema"/>.
    /// </summary>
    /// <remarks>
    /// Writes ZERO batches on purpose. <c>ParquetFileWriter.WriteAsync</c> builds its custom-metadata
    /// dictionary — including the <c>DeltaSchemaJson.ToJson(schema)</c> call this suite exists to pin —
    /// <b>before</b> the row-group loop and unconditionally, and the loop is pre-test so zero rows
    /// produce zero row groups. So the footer schema bytes here come from the identical call site the
    /// data-bearing test above exercises, while the schema corpus is freed from the cost of
    /// materialising a column vector per type. That is what makes broad type coverage affordable at
    /// the ARTIFACT layer rather than only at the helper layer.
    /// </remarks>
    private static async Task<string> WriteAndReadFooterSchemaAsync(StructType schema)
    {
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(
            stream, schema, Array.Empty<ColumnBatch>(), CancellationToken.None);
        stream.Position = 0;

        await using ParquetReader reader =
            await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        return reader.CustomMetadata[DeltaSchemaJson.SchemaMetadataKey];
    }

    [Fact]
    public async Task WrittenFooter_PinsFieldMetadata_IncludingUnquotedColumnMappingIds()
    {
        // #679: ARTIFACT-layer coverage of FIELD METADATA. The sibling test above pins a schema whose
        // fields all carry EMPTY metadata, so a rogue serializer at ParquetFileWriter's call site that
        // reproduces flat-atomic bytes exactly but mishandles metadata — dropping it, or emitting
        // delta.columnMapping.id as a QUOTED string — ships a live footer/log divergence and passes
        // there. Field metadata does reach the footer (it takes no part in Parquet type mapping), and
        // it is the column-mapping payload of #191/#676, so it is pinned here rather than deferred.
        //
        // The unquoted 7 and 12 below are load-bearing: the Delta protocol requires column-mapping ids
        // to be JSON integers, not strings (#330). Quoting them is a real interop break that a
        // structural or round-trip check would not notice.
        var schema = new StructType(new[]
        {
            new StructField("first", DataTypes.LongType, nullable: false, FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(7)),
                new KeyValuePair<string, MetadataValue>(
                    "delta.columnMapping.physicalName", MetadataValue.String("col-7")),
            })),
            new StructField("second", DataTypes.StringType, nullable: true, FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(12)),
                new KeyValuePair<string, MetadataValue>(
                    "delta.columnMapping.physicalName", MetadataValue.String("col-12")),
                // Exercises the remaining metadata value kinds that survive the writer, plus the
                // escaping policy (" is emitted as \u0022, not \"), and key ORDERING (ordinal-sorted,
                // so "absent" leads even though it was supplied last).
                new KeyValuePair<string, MetadataValue>("comment", MetadataValue.String("a \"quoted\" note")),
                new KeyValuePair<string, MetadataValue>("flag", MetadataValue.Boolean(true)),
                new KeyValuePair<string, MetadataValue>("ratio", MetadataValue.Double(0.5)),
                new KeyValuePair<string, MetadataValue>("absent", MetadataValue.Null),
            })),
            // A field with NO metadata alongside fields that have it, so "always emit {}" and "omit
            // when empty" stay distinguishable at this layer.
            new StructField("third", DataTypes.IntegerType, nullable: true),
        });

        string schemaJson = await WriteAndReadFooterSchemaAsync(schema);

        const string footerGolden =
            "{\"type\":\"struct\",\"fields\":[" +
            "{\"name\":\"first\",\"type\":\"long\",\"nullable\":false,\"metadata\":{" +
            "\"delta.columnMapping.id\":7,\"delta.columnMapping.physicalName\":\"col-7\"}}," +
            "{\"name\":\"second\",\"type\":\"string\",\"nullable\":true,\"metadata\":{" +
            "\"absent\":null,\"comment\":\"a \\u0022quoted\\u0022 note\"," +
            "\"delta.columnMapping.id\":12,\"delta.columnMapping.physicalName\":\"col-12\"," +
            "\"flag\":true,\"ratio\":0.5}}," +
            "{\"name\":\"third\",\"type\":\"integer\",\"nullable\":true,\"metadata\":{}}]}";

        // Same dual oracle as the sibling test: the golden catches drift inside the shared serializer
        // reaching the footer; the equality catches the footer being repointed at a different
        // serializer while the shared one is untouched. Neither subsumes the other.
        Assert.Equal(footerGolden, schemaJson);
        Assert.Equal(SchemaJson.ToJson(schema), schemaJson);
    }

    [Fact]
    public async Task WrittenFooter_PinsEveryScalarTypeTheWriterAccepts()
    {
        // #679: ARTIFACT-layer coverage of TYPE BREADTH. The data-bearing test above reaches the
        // footer with three type names (long, decimal, string), so a rogue that spells any other type
        // name differently in the footer than the log does — "int" for "integer", "timestampNtz" for
        // "timestamp_ntz" — diverges undetected at this layer.
        //
        // This is every atomic type ParquetTypeMapping.CreateField accepts. Nested types are
        // deliberately absent: CreateField throws UnsupportedFeature for array/map/struct (design
        // §2.9), so they cannot be written at all and cannot be pinned here until that lands (#713).
        // If nested write support arrives, extend this corpus rather than adding a new layer.
        var schema = new StructType(new[]
        {
            new StructField("c_bool", DataTypes.BooleanType, nullable: false),
            new StructField("c_byte", DataTypes.ByteType, nullable: true),
            new StructField("c_short", DataTypes.ShortType, nullable: false),
            new StructField("c_int", DataTypes.IntegerType, nullable: true),
            new StructField("c_long", DataTypes.LongType, nullable: false),
            new StructField("c_float", DataTypes.FloatType, nullable: true),
            new StructField("c_double", DataTypes.DoubleType, nullable: false),
            new StructField("c_string", DataTypes.StringType, nullable: true),
            new StructField("c_binary", DataTypes.BinaryType, nullable: false),
            new StructField("c_date", DataTypes.DateType, nullable: true),
            new StructField("c_ts", DataTypes.TimestampType, nullable: false),
            new StructField("c_ts_ntz", DataTypes.TimestampNtzType, nullable: true),
            // Precision 28 is ParquetTypeMapping.MaxSupportedDecimalPrecision, so the parameterised
            // decimal(p,s) rendering is pinned at its supported boundary rather than a mid-range value.
            new StructField("c_dec", DataTypes.CreateDecimalType(28, 7), nullable: true),
        });

        string schemaJson = await WriteAndReadFooterSchemaAsync(schema);

        const string footerGolden =
            "{\"type\":\"struct\",\"fields\":[" +
            "{\"name\":\"c_bool\",\"type\":\"boolean\",\"nullable\":false,\"metadata\":{}}," +
            "{\"name\":\"c_byte\",\"type\":\"byte\",\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"c_short\",\"type\":\"short\",\"nullable\":false,\"metadata\":{}}," +
            "{\"name\":\"c_int\",\"type\":\"integer\",\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"c_long\",\"type\":\"long\",\"nullable\":false,\"metadata\":{}}," +
            "{\"name\":\"c_float\",\"type\":\"float\",\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"c_double\",\"type\":\"double\",\"nullable\":false,\"metadata\":{}}," +
            "{\"name\":\"c_string\",\"type\":\"string\",\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"c_binary\",\"type\":\"binary\",\"nullable\":false,\"metadata\":{}}," +
            "{\"name\":\"c_date\",\"type\":\"date\",\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"c_ts\",\"type\":\"timestamp\",\"nullable\":false,\"metadata\":{}}," +
            "{\"name\":\"c_ts_ntz\",\"type\":\"timestamp_ntz\",\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"c_dec\",\"type\":\"decimal(28,7)\",\"nullable\":true,\"metadata\":{}}]}";

        Assert.Equal(footerGolden, schemaJson);
        Assert.Equal(SchemaJson.ToJson(schema), schemaJson);
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
