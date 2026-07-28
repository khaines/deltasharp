using System.Text.RegularExpressions;
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
        // reachable part of that gap is closed by the sibling tests below, which extend the
        // artifact layer over field metadata (including the #330 unquoted-integer column-mapping id
        // contract), over every scalar type the writer accepts with the decimal parameter boundaries,
        // and over field names requiring JSON escaping — plus a completeness guard that fires if the
        // writer ever accepts a type this corpus does not pin. What remains uncovered at this
        // layer is only the part that is INHERENTLY unreachable: nested types cannot be written at
        // all today, because ParquetTypeMapping.CreateField rejects array/map/struct with
        // UnsupportedFeature (design §2.9), so no artifact test can pin them until that lands. They
        // are pinned at the helper layer meanwhile. Tracked in #713.
        //
        // The three Assert.Contains calls below are substring checks: blind to field ORDER, to
        // property order within a field, and to anything additional, so they could never have caught
        // the call-site swap this assertion exists for.
        //
        // The two assertions are deliberately not redundant, but note the ORDER they execute in,
        // because it decides which one you will actually see fail:
        //   * the golden is asserted FIRST and fires for any divergence of the footer bytes from the
        //     pinned wire shape — a call-site swap AND shared-serializer drift reaching the footer
        //     both trip it. For those two failure modes it SHADOWS the equality below, which is
        //     never reached. (Measured: a rogue at the call site reports Expected = the golden.)
        //   * the equality is therefore not "the call-site swap check" — it is the only assertion
        //     that can fail while the footer bytes still MATCH the golden, i.e. when SchemaJson has
        //     drifted away from the pinned shape but the footer has not followed it. That is the log
        //     side moving while the footer stays put, which the golden cannot see by construction.
        // So: the golden pins footer bytes == fixed wire shape; the equality pins footer bytes ==
        // shared serializer. Neither subsumes the other, and both hold only for this corpus.
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

    /// <summary>The metadata artifact corpus; shared with the value-kind completeness guard.</summary>
    private static readonly StructType MetadataCorpusSchema = new(new[]
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
            // EVERY MetadataValueKind, not "the remaining ones that survive the writer" -- an
            // earlier version of this comment claimed the latter while omitting Array and Nested,
            // both of which reach the footer intact. MetadataValueKindCorpus_CoversEveryKind
            // below now derives that claim from the enum instead of asserting it in prose.
            //
            // Also exercises the escaping policy (" is emitted as \u0022, not \"), and key
            // ORDERING (ordinal-sorted, so "absent" leads even though it was supplied last).
            new KeyValuePair<string, MetadataValue>("comment", MetadataValue.String("a \"quoted\" note")),
            new KeyValuePair<string, MetadataValue>("flag", MetadataValue.Boolean(true)),
            new KeyValuePair<string, MetadataValue>("ratio", MetadataValue.Double(0.5)),
            new KeyValuePair<string, MetadataValue>("absent", MetadataValue.Null),
            // Array and Nested carry the recursive arms of the metadata serializer; "deep" nests
            // a second level so a rogue that flattens only the top level is still caught.
            new KeyValuePair<string, MetadataValue>("arr", MetadataValue.Array(new[]
            {
                MetadataValue.Long(1), MetadataValue.String("two"),
                MetadataValue.Boolean(false), MetadataValue.Null,
            })),
            new KeyValuePair<string, MetadataValue>("obj", MetadataValue.Nested(
                FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>("inner", MetadataValue.Long(9)),
                    new KeyValuePair<string, MetadataValue>("deep", MetadataValue.Nested(
                        FieldMetadata.FromValues(new[]
                        {
                            new KeyValuePair<string, MetadataValue>("leaf", MetadataValue.String("x")),
                        }))),
                }))),
        })),
        // A field with NO metadata alongside fields that have it, so "always emit {}" and "omit
        // when empty" stay distinguishable at this layer.
        new StructField("third", DataTypes.IntegerType, nullable: true),
    });

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

        string schemaJson = await WriteAndReadFooterSchemaAsync(MetadataCorpusSchema);

        const string footerGolden =
            "{\"type\":\"struct\",\"fields\":[" +
            "{\"name\":\"first\",\"type\":\"long\",\"nullable\":false,\"metadata\":{" +
            "\"delta.columnMapping.id\":7,\"delta.columnMapping.physicalName\":\"col-7\"}}," +
            "{\"name\":\"second\",\"type\":\"string\",\"nullable\":true,\"metadata\":{" +
            "\"absent\":null,\"arr\":[1,\"two\",false,null]," +
            "\"comment\":\"a \\u0022quoted\\u0022 note\"," +
            "\"delta.columnMapping.id\":12,\"delta.columnMapping.physicalName\":\"col-12\"," +
            "\"flag\":true,\"obj\":{\"deep\":{\"leaf\":\"x\"},\"inner\":9},\"ratio\":0.5}}," +
            "{\"name\":\"third\",\"type\":\"integer\",\"nullable\":true,\"metadata\":{}}]}";

        // Same dual oracle as the sibling test, same execution order: the golden is asserted first
        // and shadows the equality for both a call-site swap and shared-serializer drift; the
        // equality is the only assertion that can fail while the footer still matches the golden,
        // i.e. when SchemaJson drifts and the footer does not follow. Neither subsumes the other.
        Assert.Equal(footerGolden, schemaJson);
        Assert.Equal(SchemaJson.ToJson(MetadataCorpusSchema), schemaJson);
    }

    /// <summary>
    /// The scalar artifact corpus: every atomic type <c>ParquetTypeMapping.CreateField</c> accepts,
    /// plus the decimal parameter family at its boundaries. Shared by the pinning test and the
    /// completeness guard, so the two cannot disagree about what "the corpus" is.
    /// </summary>
    private static readonly StructType ScalarCorpusSchema = new(new[]
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
        // decimal(p,s) is a parameterised FAMILY, and p/s are protocol-visible in schemaString, so
        // pinning one member pins almost nothing. These are its boundaries: minimum precision,
        // a mid-range value, zero scale, maximum supported precision (28 ==
        // ParquetTypeMapping.MaxSupportedDecimalPrecision), and scale == precision.
        new StructField("c_dec_min", DataTypes.CreateDecimalType(1, 0), nullable: true),
        new StructField("c_dec_mid", DataTypes.CreateDecimalType(9, 2), nullable: false),
        new StructField("c_dec_int", DataTypes.CreateDecimalType(10, 0), nullable: true),
        new StructField("c_dec_max", DataTypes.CreateDecimalType(28, 7), nullable: false),
        new StructField("c_dec_scale", DataTypes.CreateDecimalType(28, 28), nullable: true),
    });

    /// <summary>Footer bytes for <see cref="ScalarCorpusSchema"/>, read back out of a real file.</summary>
    private const string ScalarFooterGolden =
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
        "{\"name\":\"c_dec_min\",\"type\":\"decimal(1,0)\",\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"c_dec_mid\",\"type\":\"decimal(9,2)\",\"nullable\":false,\"metadata\":{}}," +
        "{\"name\":\"c_dec_int\",\"type\":\"decimal(10,0)\",\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"c_dec_max\",\"type\":\"decimal(28,7)\",\"nullable\":false,\"metadata\":{}}," +
        "{\"name\":\"c_dec_scale\",\"type\":\"decimal(28,28)\",\"nullable\":true,\"metadata\":{}}]}";

    [Fact]
    public async Task WrittenFooter_PinsEveryScalarTypeTheWriterAccepts()
    {
        // #679: ARTIFACT-layer coverage of TYPE BREADTH. The data-bearing test above reaches the
        // footer with three type names (long, decimal, string), so a rogue that spells any other type
        // name differently in the footer than the log does — "int" for "integer", or collapsing
        // "timestamp_ntz" to "timestamp" — diverges undetected at that corpus. The NTZ collapse in
        // particular is real cross-engine corruption, not a cosmetic difference: an NTZ column
        // silently becomes UTC for every external reader.
        //
        // Nested types are deliberately absent: CreateField throws UnsupportedFeature for
        // array/map/struct (design §2.9), so they cannot be written at all and cannot be pinned here
        // until that lands (#713). If nested write support arrives, extend this corpus.
        string schemaJson = await WriteAndReadFooterSchemaAsync(ScalarCorpusSchema);

        Assert.Equal(ScalarFooterGolden, schemaJson);
        Assert.Equal(SchemaJson.ToJson(ScalarCorpusSchema), schemaJson);
    }

    /// <summary>Footer bytes for the escaped-name corpus; shared with the escape-form guard.</summary>
    private const string NameEncodingGolden =
        "{\"type\":\"struct\",\"fields\":[" +
        "{\"name\":\"caf\\u00E9\",\"type\":\"long\",\"nullable\":false,\"metadata\":{}}," +
        "{\"name\":\"\\u4E2D\\u6587\",\"type\":\"string\",\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"q\\u0022z\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"a\\\\b\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"tab\\there\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"nl\\nhere\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"cr\\rhere\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"bs\\bhere\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"ff\\fhere\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"emoji\\uD83C\\uDF89\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"ctrl\\u0001x\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}]}";

    [Fact]
    public async Task WrittenFooter_PinsFieldNamesRequiringJsonEscaping()
    {
        // #679: ARTIFACT-layer coverage of NAME ENCODING — the dimension every other corpus here
        // misses, because every other field name is a plain ASCII identifier.
        //
        // A rogue that interpolates names into JSON raw is byte-exact for ASCII identifiers and so
        // passes every other assertion in this file, yet produces a footer that disagrees with the
        // log for any name needing an escape — and for a quote-bearing name produces STRUCTURALLY
        // INVALID JSON ("name":"q"z"), which is a protocol break, not a cosmetic one.
        //
        // The escaping is deliberately NOT uniform, and that is the point of pinning it rather than
        // asserting a round-trip: the strict encoder emits " as \u0022 with UPPERCASE hex, but
        // backslash as \\, tab as \t and newline as \n, and astral characters as a surrogate PAIR.
        // No structural or round-trip check can see any of that. All of these names are accepted by
        // the writer today — verified, not assumed.
        var schema = new StructType(new[]
        {
            new StructField("café", DataTypes.LongType, nullable: false),
            new StructField("中文", DataTypes.StringType, nullable: true),
            new StructField("q\"z", DataTypes.LongType, nullable: true),
            new StructField("a\\b", DataTypes.LongType, nullable: true),
            new StructField("tab\there", DataTypes.LongType, nullable: true),
            new StructField("nl\nhere", DataTypes.LongType, nullable: true),
            new StructField("cr\rhere", DataTypes.LongType, nullable: true),
            new StructField("bs\bhere", DataTypes.LongType, nullable: true),
            new StructField("ff\fhere", DataTypes.LongType, nullable: true),
            new StructField("emoji\U0001F389", DataTypes.LongType, nullable: true),
            new StructField("ctrl\u0001x", DataTypes.LongType, nullable: true),
        });

        string schemaJson = await WriteAndReadFooterSchemaAsync(schema);

        // Golden hoisted to NameEncodingGolden so the escape-form completeness guard reads THIS
        // string rather than a copy of it.

        Assert.Equal(NameEncodingGolden, schemaJson);
        Assert.Equal(SchemaJson.ToJson(schema), schemaJson);

        // The footer must remain parseable JSON: a raw-interpolating rogue breaks this outright for
        // the quote-bearing name, and this fails with a clearer message than a byte diff would.
        Assert.Equal(schemaJson, SchemaJson.ToJson(SchemaJson.FromJson(schemaJson)));
    }

    [Fact]
    public async Task MetadataValueKindCorpus_CoversEveryKind()
    {
        // #679: COMPLETENESS guard for metadata VALUE KINDS -- the dimension a prose comment in the
        // test above once claimed ("the remaining metadata value kinds that survive the writer")
        // while silently omitting Array and Nested, both of which reach the footer intact. Prose
        // cannot be executed; this can.
        //
        // MetadataValueKind is a closed enum, so the required set is ENUMERATED from the type system
        // rather than listed. Adding a kind without extending the corpus fails here, naming it.
        string metadataJson = await WriteAndReadFooterSchemaAsync(MetadataCorpusSchema);

        var covered = new List<MetadataValueKind>();
        foreach (StructField field in MetadataCorpusSchema)
        {
            CollectKinds(field.Metadata, covered);
        }

        MetadataValueKind[] missing = Enum.GetValues<MetadataValueKind>()
            .Where(kind => !covered.Contains(kind))
            .ToArray();
        Assert.True(
            missing.Length == 0,
            "These MetadataValueKinds reach the footer but no artifact corpus row exercises them, so a "
            + "rogue serializer mishandling them would ship undetected: "
            + string.Join(", ", missing));

        // Non-vacuity: the kinds must actually be visible in the pinned bytes, not merely present in
        // the in-memory schema. Array and Nested are the two that a flattening rogue erases.
        Assert.Contains("\"arr\":[1,\"two\",false,null]", metadataJson, StringComparison.Ordinal);
        Assert.Contains("\"obj\":{\"deep\":{\"leaf\":\"x\"},\"inner\":9}", metadataJson, StringComparison.Ordinal);
    }

    private static void CollectKinds(FieldMetadata metadata, List<MetadataValueKind> into)
    {
        foreach (KeyValuePair<string, MetadataValue> entry in metadata)
        {
            CollectKinds(entry.Value, into);
        }
    }

    private static void CollectKinds(MetadataValue value, List<MetadataValueKind> into)
    {
        into.Add(value.Kind);
        switch (value.Kind)
        {
            case MetadataValueKind.Array:
                foreach (MetadataValue item in value.AsArray())
                {
                    CollectKinds(item, into);
                }

                break;
            case MetadataValueKind.Nested:
                CollectKinds(value.AsNested(), into);
                break;
            default:
                break;
        }
    }

    [Fact]
    public void NameEncodingCorpus_CoversEveryEscapeFormTheSerializerEmits()
    {
        // #679: COMPLETENESS guard for field-name ENCODING. The corpus of escaped names is a list,
        // and the set it must cover is not obvious by inspection -- the encoder emits SEVEN distinct
        // escape forms, and the first version of that corpus covered four (it omitted \b, \f and \r
        // without anything saying so).
        //
        // So the required set is DERIVED: probe the serializer across the control range plus a
        // sample of non-ASCII, observe which escape forms it actually produces, and require the
        // pinned name golden to exercise each one. Nothing here restates the encoder's policy; if
        // the encoder changes, this set changes with it.
        var emitted = new SortedSet<string>(StringComparer.Ordinal);
        var codepoints = new List<int>();
        for (int c = 1; c < 0x80; c++)
        {
            codepoints.Add(c);
        }

        codepoints.AddRange(new[] { 0x00A0, 0x00E9, 0x2028, 0x2029, 0x4E2D, 0xFFFD });

        foreach (int codepoint in codepoints)
        {
            string probeName = "x" + char.ConvertFromUtf32(codepoint) + "y";
            string json = SchemaJson.ToJson(
                new StructType(new[] { new StructField(probeName, DataTypes.LongType) }));

            int start = json.IndexOf("\"name\":\"x", StringComparison.Ordinal) + 9;
            int end = json.IndexOf("y\",\"type\"", StringComparison.Ordinal);
            string encoded = json[start..end];
            if (encoded.Length == 1 && encoded[0] == codepoint)
            {
                continue;
            }

            // Collapse \uXXXX to its family; the specific codepoints are pinned by the golden.
            emitted.Add(encoded.StartsWith("\\u", StringComparison.Ordinal) ? "\\u" : encoded);
        }

        // Non-vacuity: a probe loop that silently stopped matching would make the check below pass
        // trivially, so require the encoder to have produced a plausible number of forms.
        Assert.True(emitted.Count >= 5, $"Escape-form probe produced only {emitted.Count} forms.");

        string[] unpinned = emitted
            .Where(form => !NameEncodingGolden.Contains(form, StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            unpinned.Length == 0,
            "The serializer emits these escape forms but WrittenFooter_PinsFieldNamesRequiringJsonEscaping "
            + "does not pin any name using them, so a rogue mishandling them would ship undetected: "
            + string.Join(" ", unpinned));
    }

    [Fact]
    public async Task ScalarArtifactCorpus_CoversEveryTypeTheWriterAccepts()
    {
        // #679: COMPLETENESS guard for the corpus above. That corpus is a list, and a list silently
        // falls behind: this suite already shipped an artifact corpus of three types while the writer
        // accepted seventeen, with nothing failing to say so.
        //
        // The guard therefore derives BOTH of its inputs instead of restating them:
        //   * what the writer accepts — by attempting a real write of each candidate, not by reading
        //     the switch in ParquetTypeMapping (the property we care about is acceptance, not the
        //     shape of the code that decides it);
        //   * what the corpus pins — by parsing the type names straight out of ScalarFooterGolden,
        //     so removing a field from the corpus removes it from this set too and the guard fires.
        //     A hand-maintained mirror of the golden could be edited in step with it, which would
        //     let the corpus shrink silently — the exact failure this guard exists to prevent.
        //
        // This guards COVERAGE, not bytes — the golden is what pins the wire shape.
        string[] pinnedTypeNames = Regex
            .Matches(ScalarFooterGolden, "\"type\":\"(?<t>[^\"]+)\"")
            .Select(m => m.Groups["t"].Value)
            .Where(t => !string.Equals(t, "struct", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Non-vacuity: if the regex ever stops matching, every "missing" check below would pass
        // trivially. Tie it to the corpus so a broken parse cannot look like success.
        Assert.Equal(ScalarCorpusSchema.Count, pinnedTypeNames.Length);

        var candidates = new List<DataType>
        {
            DataTypes.BooleanType, DataTypes.ByteType, DataTypes.ShortType, DataTypes.IntegerType,
            DataTypes.LongType, DataTypes.FloatType, DataTypes.DoubleType, DataTypes.StringType,
            DataTypes.BinaryType, DataTypes.DateType, DataTypes.TimestampType, DataTypes.TimestampNtzType,
            DataTypes.NullType,
            DataTypes.CreateDecimalType(1, 0), DataTypes.CreateDecimalType(9, 2),
            DataTypes.CreateDecimalType(10, 0), DataTypes.CreateDecimalType(28, 7),
            DataTypes.CreateDecimalType(28, 28),
            DataTypes.CreateArrayType(DataTypes.StringType),
            DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType),
            DataTypes.CreateStructType(new[] { new StructField("x", DataTypes.LongType) }),
        };

        // Cross-check the candidate list against reflection over every CONCRETE DataType the engine
        // declares — not just AtomicType subclasses. DecimalType, ArrayType, MapType and StructType
        // all derive straight from DataType, so an AtomicType-scoped check would leave the entire
        // parameterised decimal family outside its reach while appearing thorough.
        string[] declaredTypes = typeof(DataType).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(DataType).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        string[] coveredTypes = candidates
            .Select(t => t.GetType().Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(declaredTypes, coveredTypes);

        var accepted = new List<string>();
        var rejected = new List<string>();
        foreach (DataType candidate in candidates)
        {
            var probe = new StructType(new[] { new StructField("probe", candidate, nullable: true) });
            try
            {
                await WriteAndReadFooterSchemaAsync(probe);
                accepted.Add(candidate.TypeName);
            }
            catch (DeltaStorageException)
            {
                // The writer refuses this type, so no artifact assertion for it is possible.
                rejected.Add(candidate.TypeName);
            }
        }

        // Nested types and the void type are refused; everything else must be in the pinned corpus.
        // (Type names here are the bare DataType.TypeName: "array"/"map"/"struct", and the null type
        // spells itself "void" — both verified against the writer rather than assumed.)
        Assert.Equal(
            new[] { "array", "map", "struct", "void" },
            rejected.OrderBy(n => n, StringComparer.Ordinal).ToArray());

        string[] missing = accepted
            .Where(typeName => !pinnedTypeNames.Contains(typeName, StringComparer.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            missing.Length == 0,
            "ParquetFileWriter accepts these types but WrittenFooter_PinsEveryScalarTypeTheWriterAccepts "
            + "does not pin them, so a footer/log divergence in them would ship undetected: "
            + string.Join(", ", missing));
    }

    [Fact]
    public async Task DecimalPrecisionBoundary_IsWhereTheWriterStopsAccepting()
    {
        // The decimal family is unbounded in principle, so the corpus pins its BOUNDARIES rather than
        // its members. This asserts the boundary is where the corpus assumes it is: if a future change
        // raises MaxSupportedDecimalPrecision, precision 29 starts writing, silently landing an
        // unpinned type name in footers. That would pass the completeness guard above, because a type
        // nobody probes is a type nobody notices.
        var accepted = new StructType(new[]
        {
            new StructField("d", DataTypes.CreateDecimalType(ParquetTypeMappingMaxPrecision, 0), nullable: true),
        });
        Assert.Contains($"decimal({ParquetTypeMappingMaxPrecision},0)", await WriteAndReadFooterSchemaAsync(accepted));

        var beyond = new StructType(new[]
        {
            new StructField("d", DataTypes.CreateDecimalType(ParquetTypeMappingMaxPrecision + 1, 0), nullable: true),
        });
        await Assert.ThrowsAsync<DeltaStorageException>(() => WriteAndReadFooterSchemaAsync(beyond));
    }

    /// <summary>Mirrors <c>ParquetTypeMapping.MaxSupportedDecimalPrecision</c>, pinned by the test above.</summary>
    private const int ParquetTypeMappingMaxPrecision = 28;

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
