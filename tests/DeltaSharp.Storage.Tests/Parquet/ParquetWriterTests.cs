using System.Text.RegularExpressions;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Tests.Parquet;
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
    public async Task WriteAsync_SchemaMismatchMessage_QuotesCommaBearingColumnName_NotAliasedAsTwoColumns()
    {
        // #751 storage-level pin for the DescribeSchema -> SanitizeAndJoinCounted quoting. The writer's
        // schema-mismatch guard renders BOTH schemas via DescribeSchema; a foreign column literally named
        // `a, b` must render RFC-4180 QUOTED so it cannot masquerade as two columns. Dropping the
        // SanitizeAndJoinCounted quoting (DiagnosticText.cs:~293) turns this red.
        var writerSchema = new StructType(new[]
        {
            new StructField("x", DataTypes.LongType, nullable: false),
        });
        var batchSchema = new StructType(new[]
        {
            new StructField("a, b", DataTypes.LongType, nullable: false),
        });
        MutableColumnVector col = ColumnVectors.Create(DataTypes.LongType, 1);
        col.AppendValue(1L);
        var batch = new ManagedColumnBatch(batchSchema, new ColumnVector[] { col }, 1);

        using var stream = new MemoryStream();
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => new ParquetFileWriter().WriteAsync(
                stream, writerSchema, new[] { batch }, CancellationToken.None));

        // The comma-bearing name renders quoted...
        Assert.Contains("struct with 1 column(s): [\"a, b\"]", ex.Message);
        // ...and NEVER as a bare two-column split.
        Assert.DoesNotContain("struct with 1 column(s): [a, b]", ex.Message);
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

    /// <summary>
    /// Pins the footer metadata keys to their WIRE LITERALS, transcribed independently in
    /// <see cref="FooterWireKeys"/> rather than read from the production constants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every footer assertion in this file used to look the schema up through
    /// <c>DeltaSchemaJson.SchemaMetadataKey</c> — the same symbol the writer stamps with — so the
    /// lookup succeeded whatever that symbol contained. A one-character deletion from it leaves the
    /// serializer, its input and the emitted bytes ALL CORRECT and moves only the wire identifier;
    /// it was 0 kills solution-wide. External readers would then find no schema in the footer at
    /// all, which is a worse outcome than a divergent one and is not what any of the open deferrals
    /// describe: every one of those scopes the schemaString VALUE.
    /// </para>
    /// <para>
    /// This is the tautology #679 exists to delete, moved from the value to the key, and it says
    /// something about shared sources generally that the rest of this file should be read with: a
    /// source shared between the prober and the probed is safe when it sits OUTSIDE both and unsafe
    /// when it sits BETWEEN them. Direction is what matters, not distance.
    /// </para>
    /// </remarks>
    [Fact]
    public void FooterMetadataKeys_AreTheWireLiterals()
    {
        Assert.Equal("org.apache.spark.sql.parquet.row.metadata", FooterWireKeys.Schema);

        if (!string.Equals(FooterWireKeys.Schema, DeltaSchemaJson.SchemaMetadataKey, StringComparison.Ordinal))
        {
            Assert.Fail(
                "The footer schema key no longer matches Spark's wire literal, so every external "
                + "reader would find NO schema in the footer -- the schemaString itself can be "
                + "perfectly correct and this still breaks every consumer."
                + $"{Environment.NewLine}  wire literal: {FooterWireKeys.Schema}"
                + $"{Environment.NewLine}  writer stamps: {DeltaSchemaJson.SchemaMetadataKey}");
        }

        Assert.Equal(FooterWireKeys.Writer, DeltaSchemaJson.WriterMetadataKey);
    }

    [Fact]
    public async Task WrittenFile_CarriesDeltaSchemaMetadata()
    {
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(stream, Schema, new[] { BuildKnownBatch() }, CancellationToken.None);
        stream.Position = 0;

        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);

        Assert.True(reader.CustomMetadata.ContainsKey(FooterWireKeys.Schema));
        string schemaJson = reader.CustomMetadata[FooterWireKeys.Schema];

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
        // writer ever accepts a type this corpus does not pin.
        //
        // NESTED types (#713): single-level array/map/struct write support landed in #841, so the
        // reachable nested shapes are now pinned at THIS artifact layer too —
        // WrittenFooter_PinsSingleLevelNestedTypes pins the array containsNull arm (both polarities),
        // the map valueContainsNull arm (both polarities), a nested struct field, the scalar
        // elementType/keyType/valueType recursion arms, and column-mapping metadata on a nested
        // container and leaf; WrittenFooter_PinsNestedTypes_FromRealNestedRows pins the same for a
        // DATA-BEARING nested write through the shredder. What remains helper-only is only the part
        // still INHERENTLY unreachable: the recursive OBJECT arms — a nested container/struct nested
        // INSIDE another (array<struct>, map<string,array>, struct<x:struct>) — which the writer still
        // refuses ("a nested type within a nested type ... deferred, #585" — a dangling ref to the closed
        // #585, tracked by #873), so no artifact test can pin them until nested-within-nested WRITE support
        // lands (#873). They stay pinned at the helper layer
        // (DeltaSchemaJsonComplexTypeTests) meanwhile, and the completeness guard's
        // AssertWriterRefusesAsync probes pin that refusal boundary on the write side.
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
        Assert.True(reader.CustomMetadata.ContainsKey(FooterWireKeys.Writer));
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
    private static async Task<string> WriteAndReadFooterSchemaAsync(
        StructType schema, ColumnBatch? rows = null)
    {
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(
            stream,
            schema,
            rows is null ? Array.Empty<ColumnBatch>() : new[] { rows },
            CancellationToken.None);
        stream.Position = 0;

        await using ParquetReader reader =
            await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        string declared = reader.CustomMetadata[FooterWireKeys.Schema];

        // THE OUTPUT-SIDE CHECK. Every other guard in this file ranges over the INPUT MODEL, while
        // the property they all protect is about the OUTPUT -- so anything that can differ between
        // what the writer was given and what it actually wrote is outside them by construction. A
        // writer that stamps a correct schemaString onto a file carrying FEWER PHYSICAL COLUMNS
        // produces a file whose own embedded metadata contradicts its own column list, and Spark,
        // delta-rs and Trino would each resolve the declared columns against the physical ones.
        //
        // This sits in the shared oracle rather than in one test, so it applies to every footer
        // this file writes: the corpora, the constructive sweep at every cardinality, and all 1200
        // generated schemas.
        // The comparison runs against the RE-PARSED DECLARED STRING, so both sides are read back
        // out of the artifact and the assertion is self-contained: it says the file does not
        // contradict itself, without borrowing anything from the caller. The input is then checked
        // against the declaration as well, which makes the three-way equality independent of
        // CALLER DISCIPLINE -- the callers do each pin the declared string to the shared
        // serializer's output, but a future caller that forgot to would silently unpin the input
        // side, and a guard that holds only while every call site remembers something is a guard
        // with a maintenance requirement rather than an invariant. MEASURED, not argued: with the
        // sweep's own parity assertion neutered and the declared string truncated to 64 of 257
        // columns, this form is RED while the input-only form is GREEN 1659 -- the old form's kill
        // had been coming entirely from the caller.
        //
        // Domain note: this ranges over column NAMES and NULLABILITY. Physical type, decimal
        // precision and timestamp annotation are not expressible here and are pinned by the
        // read-path guards instead -- measured, not assumed: ByteType->short is 12 RED,
        // decimal(p)->p+1 is 16 RED, and flipping isAdjustedToUTC for timestamp_ntz is 2 RED.
        // Nullability WAS in that unexpressible set until #730; it is now checked directly below.
        // The narrowness is a division of labour, not a gap. The positional gap is the real one,
        // and it is the LOG-side sibling call site, guarded in DeltaFooterLogSchemaParityTests
        // rather than here.
        // TOP-LEVEL fields, not DataFields: since #841 a nested column is ONE declared column that
        // physically fans out to N leaf DataFields, so the leaf projection would compare a struct's
        // children against the declared column list and false-fail. Schema.Fields is the faithful
        // counterpart of the declared top-level column list, and for a flat schema the two projections
        // are identical — so this generalises the guard to nested footers without loosening it.
        string[] physical = reader.Schema.Fields.Select(x => x.Name).ToArray();
        string[] redeclared = ((StructType)SchemaJson.FromJson(declared)).Select(x => x.Name).ToArray();
        string[] logical = schema.Select(x => x.Name).ToArray();
        if (!physical.SequenceEqual(redeclared, StringComparer.Ordinal))
        {
            Assert.Fail(
                $"The written file DECLARES {redeclared.Length} columns in its schemaString but "
                + $"physically carries {physical.Length}."
                + $"{Environment.NewLine}  declared: {Truncate(string.Join(", ", redeclared))}"
                + $"{Environment.NewLine}  physical: {Truncate(string.Join(", ", physical))}");
        }

        if (!redeclared.SequenceEqual(logical, StringComparer.Ordinal))
        {
            Assert.Fail(
                $"The written file DECLARES {redeclared.Length} columns but was HANDED "
                + $"{logical.Length}."
                + $"{Environment.NewLine}  handed:   {Truncate(string.Join(", ", logical))}"
                + $"{Environment.NewLine}  declared: {Truncate(string.Join(", ", redeclared))}");
        }

        // #730: NULLABILITY, not only names. The physical/declared comparison above is NAME-ONLY,
        // so a writer that emits a declared-NON-nullable column as physically OPTIONAL (or the
        // reverse) produces a footer whose physical repetition contradicts its own schemaString,
        // with nothing here to see it. That was not hypothetical: ParquetTypeMapping.CreateField
        // mapped StringType/BinaryType through the reference-typed DataField<T>(name) ctor, which
        // defaults IsNullable=true and IGNORED field.Nullable, so every "nullable":false string or
        // binary column shipped physically OPTIONAL while the log declared it required. Both sides
        // are derived from the artifact -- the physical Parquet field's repetition and the re-parsed
        // schemaString's Nullable -- and must agree per column; the handed schema is then checked
        // against the declaration too, so the property does not rest on caller discipline.
        bool[] physicalNullable = reader.Schema.Fields.Select(x => x.IsNullable).ToArray();
        bool[] redeclaredNullable =
            ((StructType)SchemaJson.FromJson(declared)).Select(x => x.Nullable).ToArray();
        bool[] logicalNullable = schema.Select(x => x.Nullable).ToArray();
        if (!physicalNullable.SequenceEqual(redeclaredNullable))
        {
            Assert.Fail(
                "The written file DECLARES a column nullability its physical Parquet repetition "
                + "does not carry -- the footer contradicts its own schemaString (#730)."
                + $"{Environment.NewLine}  declared nullable: {NullabilityReport(redeclared, redeclaredNullable)}"
                + $"{Environment.NewLine}  physical nullable: {NullabilityReport(physical, physicalNullable)}");
        }

        if (!redeclaredNullable.SequenceEqual(logicalNullable))
        {
            Assert.Fail(
                "The written file DECLARES a column nullability different from the one it was "
                + "HANDED (#730)."
                + $"{Environment.NewLine}  handed nullable:   {NullabilityReport(logical, logicalNullable)}"
                + $"{Environment.NewLine}  declared nullable: {NullabilityReport(redeclared, redeclaredNullable)}");
        }

        // AND IT MUST BE READABLE. A footer that only the writer can understand is a footer no
        // reader can open, so the artifact is round-tripped through the shared reader rather than
        // merely compared as text. This is what caught the depth probe measuring a cheaper shape
        // than the sweep emits.
        Assert.Equal(declared, SchemaJson.ToJson(SchemaJson.FromJson(declared)));

        return declared;
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
        // METADATA KEY and METADATA STRING VALUE are the other two arbitrary-string positions in
        // the wire format, and until now only field names were pinned for encoding. A rogue that
        // escaped names correctly but emitted keys and values raw was byte-exact for this entire
        // corpus and shipped 1648-green. Both positions are writable — verified against the writer,
        // not assumed — so the same probe string goes through both.
        new StructField("fourth", DataTypes.StringType, nullable: true, FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(EveryEscapeForm, MetadataValue.String(EveryEscapeForm)),
        })),
        // NUMERIC TOKEN FORMATTING. Every other pinned number here is a small positive, so
        // WriteDouble's integral-value branch (which appends ".0" when the round-tripped text
        // carries no fraction or exponent) was never exercised at this layer, and no Long outside
        // Int32 range was pinned anywhere. A transducer that rewrote only number tokens was
        // byte-exact for every other corpus and shipped 7159-green, producing both a TYPE CHANGE
        // visible on re-read (1.0 read back as Long, not Double) and a column-mapping id silently
        // corrupted by Int32 overflow (3000000000 -> -1294967296).
        new StructField("fifth", DataTypes.LongType, nullable: true, FieldMetadata.FromValues(new[]
        {
            // Integral-valued double: the ".0" branch. Without this the footer may spell it "1"
            // and re-read as a different metadata type than the log.
            new KeyValuePair<string, MetadataValue>("whole", MetadataValue.Double(1.0)),
            new KeyValuePair<string, MetadataValue>("negwhole", MetadataValue.Double(-42.0)),
            // Past Int32 range, and the 64-bit boundaries themselves.
            new KeyValuePair<string, MetadataValue>("bigid", MetadataValue.Long(3000000000L)),
            new KeyValuePair<string, MetadataValue>("maxlong", MetadataValue.Long(long.MaxValue)),
            new KeyValuePair<string, MetadataValue>("minlong", MetadataValue.Long(long.MinValue)),
            // Exponent and high-precision forms, so "R" formatting is pinned alongside ".0".
            new KeyValuePair<string, MetadataValue>("tiny", MetadataValue.Double(1e-300)),
        })),
        // ENCODING AT DEPTH. The escape forms above sit at the TOP LEVEL of a field's metadata,
        // and the guard that checks them unioned its findings across depths -- so a form present
        // in a top-level key satisfied "metadata key" while every nested key, nested string value
        // and array element string in this corpus stayed bare ASCII. A rogue that escaped names
        // and top-level keys/values correctly and emitted raw at depth >= 1 was byte-exact for
        // every pinned corpus here and shipped 1645-green, producing structurally invalid JSON
        // (an unescaped quote inside a nested value). The escaping contract does not vary with
        // depth, so the corpus must not either.
        new StructField("sixth", DataTypes.StringType, nullable: true, FieldMetadata.FromValues(new[]
        {
            // Array element string at depth 1.
            new KeyValuePair<string, MetadataValue>("darr", MetadataValue.Array(new[]
            {
                MetadataValue.String(EveryEscapeForm),
            })),
            // Nested metadata KEY and nested metadata STRING VALUE, both at depth 1.
            new KeyValuePair<string, MetadataValue>("dobj", MetadataValue.Nested(
                FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>(
                        EveryEscapeForm, MetadataValue.String(EveryEscapeForm)),
                }))),
        })),
    });

    /// <summary>
    /// One string exercising every escape form the serializer emits, used in EVERY arbitrary-string
    /// position and at EVERY depth the guard requires. Deliberately a single shared constant: the
    /// encoding contract varies with neither position nor nesting depth, so neither should the
    /// corpus that pins it. Two rogues in a row exploited exactly that -- one correct in names and
    /// wrong in metadata, one correct at depth 0 and wrong at depth 1.
    /// </summary>
    internal const string EveryEscapeForm = "e\\\t\n\r\b\f\u00E9\"z\U0001F389\0n";

    /// <summary>Footer bytes for the metadata corpus; shared with the encoding guard.</summary>
    private const string MetadataFooterGolden =
            "{\"type\":\"struct\",\"fields\":[" +
            "{\"name\":\"first\",\"type\":\"long\",\"nullable\":false,\"metadata\":{" +
            "\"delta.columnMapping.id\":7,\"delta.columnMapping.physicalName\":\"col-7\"}}," +
            "{\"name\":\"second\",\"type\":\"string\",\"nullable\":true,\"metadata\":{" +
            "\"absent\":null,\"arr\":[1,\"two\",false,null]," +
            "\"comment\":\"a \\u0022quoted\\u0022 note\"," +
            "\"delta.columnMapping.id\":12,\"delta.columnMapping.physicalName\":\"col-12\"," +
            "\"flag\":true,\"obj\":{\"deep\":{\"leaf\":\"x\"},\"inner\":9},\"ratio\":0.5}}," +
            "{\"name\":\"third\",\"type\":\"integer\",\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"fourth\",\"type\":\"string\",\"nullable\":true,\"metadata\":{\"e\\\\\\t\\n\\r\\b\\f\\u00E9\\u0022z\\uD83C\\uDF89\\u0000n\":\"e\\\\\\t\\n\\r\\b\\f\\u00E9\\u0022z\\uD83C\\uDF89\\u0000n\"}}," +
        "{\"name\":\"fifth\",\"type\":\"long\",\"nullable\":true,\"metadata\":{\"bigid\":3000000000,\"maxlong\":9223372036854775807,\"minlong\":-9223372036854775808,\"negwhole\":-42.0,\"tiny\":1E-300,\"whole\":1.0}}," +
            "{\"name\":\"sixth\",\"type\":\"string\",\"nullable\":true,\"metadata\":{\"darr\":[\"e\\\\\\t\\n\\r\\b\\f\\u00E9\\u0022z\\uD83C\\uDF89\\u0000n\"],\"dobj\":{\"e\\\\\\t\\n\\r\\b\\f\\u00E9\\u0022z\\uD83C\\uDF89\\u0000n\":\"e\\\\\\t\\n\\r\\b\\f\\u00E9\\u0022z\\uD83C\\uDF89\\u0000n\"}}}]}";

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

        // Golden hoisted to MetadataFooterGolden so the encoding completeness guard reads it.

        // Same dual oracle as the sibling test, same execution order: the golden is asserted first
        // and shadows the equality for both a call-site swap and shared-serializer drift; the
        // equality is the only assertion that can fail while the footer still matches the golden,
        // i.e. when SchemaJson drifts and the footer does not follow. Neither subsumes the other.
        Assert.Equal(MetadataFooterGolden, schemaJson);
        Assert.Equal(SchemaJson.ToJson(MetadataCorpusSchema), schemaJson);
    }

    /// <summary>
    /// The scalar artifact corpus, shared with the LOG-side parity guard via
    /// <see cref="ScalarCorpus"/> so the two sides cannot disagree about what "the corpus" is.
    /// </summary>
    private static readonly StructType ScalarCorpusSchema = ScalarCorpus.Schema;

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
        // Nested types are deliberately absent from the SCALAR corpus: their wire shape is an
        // {"type":"array"|"map"|"struct", ...} OBJECT, not a scalar type-name string, so it is pinned
        // by the nested artifact tests below (WrittenFooter_PinsSingleLevelNestedTypes and its
        // data-bearing sibling) rather than here. Single-level nested write landed in #841; the
        // recursive OBJECT arms (a nested container/struct INSIDE another) are still refused by the
        // writer (the message says "deferred, #585", a dangling ref to the closed #585; tracked by #873),
        // so those remain helper-only for now — see the nested tests below.
        string schemaJson = await WriteAndReadFooterSchemaAsync(ScalarCorpusSchema);

        Assert.Equal(ScalarFooterGolden, schemaJson);
        Assert.Equal(SchemaJson.ToJson(ScalarCorpusSchema), schemaJson);
    }

    /// <summary>
    /// The nested artifact corpus — the SINGLE-LEVEL nested shapes the writer accepts since #841:
    /// <c>array&lt;scalar&gt;</c> (both <c>containsNull</c> polarities), <c>map(scalar → scalar)</c>
    /// (both <c>valueContainsNull</c> polarities) and a nested <c>struct&lt;scalars&gt;</c> field
    /// (bare, and one carrying #191/#676 column-mapping metadata on both the container and a nested
    /// leaf). Every nested container is <c>nullable:true</c> because the writer refuses a
    /// non-nullable nested container (#730 — Parquet.Net emits every nested container as OPTIONAL);
    /// the <c>containsNull</c>/<c>valueContainsNull</c> polarities live on the ELEMENT/VALUE, which is
    /// independent of the container's own nullability.
    /// </summary>
    private static readonly StructType NestedCorpusSchema = new(new[]
    {
        new StructField("tags", DataTypes.CreateArrayType(DataTypes.StringType, containsNull: true), nullable: true),
        new StructField("ids", DataTypes.CreateArrayType(DataTypes.LongType, containsNull: false), nullable: true),
        new StructField(
            "props",
            DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType, valueContainsNull: true),
            nullable: true),
        new StructField(
            "counts",
            DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: false),
            nullable: true),
        new StructField("point", DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: true),
            new StructField("b", DataTypes.StringType, nullable: false),
        }), nullable: true),
        new StructField(
            "mapped",
            DataTypes.CreateStructType(new[]
            {
                new StructField("leaf", DataTypes.LongType, nullable: false, FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(7)),
                    new KeyValuePair<string, MetadataValue>(
                        "delta.columnMapping.physicalName", MetadataValue.String("col-7")),
                })),
            }),
            nullable: true,
            FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(3)),
                new KeyValuePair<string, MetadataValue>(
                    "delta.columnMapping.physicalName", MetadataValue.String("col-3")),
            })),
    });

    /// <summary>Footer bytes for <see cref="NestedCorpusSchema"/>, read back out of a real file.</summary>
    private const string NestedFooterGolden =
        "{\"type\":\"struct\",\"fields\":[" +
        "{\"name\":\"tags\",\"type\":{\"type\":\"array\",\"elementType\":\"string\",\"containsNull\":true}," +
        "\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"ids\",\"type\":{\"type\":\"array\",\"elementType\":\"long\",\"containsNull\":false}," +
        "\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"props\",\"type\":{\"type\":\"map\",\"keyType\":\"string\",\"valueType\":\"long\"," +
        "\"valueContainsNull\":true},\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"counts\",\"type\":{\"type\":\"map\",\"keyType\":\"string\",\"valueType\":\"integer\"," +
        "\"valueContainsNull\":false},\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"point\",\"type\":{\"type\":\"struct\",\"fields\":[" +
        "{\"name\":\"a\",\"type\":\"integer\",\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"b\",\"type\":\"string\",\"nullable\":false,\"metadata\":{}}]}," +
        "\"nullable\":true,\"metadata\":{}}," +
        "{\"name\":\"mapped\",\"type\":{\"type\":\"struct\",\"fields\":[" +
        "{\"name\":\"leaf\",\"type\":\"long\",\"nullable\":false,\"metadata\":{" +
        "\"delta.columnMapping.id\":7,\"delta.columnMapping.physicalName\":\"col-7\"}}]}," +
        "\"nullable\":true,\"metadata\":{" +
        "\"delta.columnMapping.id\":3,\"delta.columnMapping.physicalName\":\"col-3\"}}]}";

    [Fact]
    public async Task WrittenFooter_PinsSingleLevelNestedTypes()
    {
        // #713: ARTIFACT-layer coverage of NESTED TYPES. Before #841 the writer refused array/map/struct
        // outright (UnsupportedFeature, design §2.9), so these shapes were pinned ONLY at the helper
        // layer (DeltaSchemaJsonComplexTypeTests over DeltaSchemaJson/SchemaJson goldens). That layer is
        // provenance-blind by construction: it asserts what the SERIALIZER would produce, never what
        // ParquetFileWriter DID stamp into a footer, so a call-site swap for a nested-typed table was
        // undetectable. Single-level nested write landed in #841, so the reachable half is now pinned
        // here at the artifact layer — the bytes are read back out of a real file, the same way the
        // scalar corpus above is.
        //
        // The corpus pins the shapes the issue names as the residual:
        //   * the array containsNull arm, BOTH polarities ("tags" true, "ids" false);
        //   * the map valueContainsNull arm, BOTH polarities ("props" true, "counts" false);
        //   * a nested struct field ("point"), including a non-nullable child ("b") so the child
        //     nullability inside the object shape is pinned, not just the container's;
        //   * the scalar elementType / keyType / valueType recursion arms (string, long, integer) that
        //     the object shape carries at its base;
        //   * field metadata on BOTH a nested container ("mapped") and a nested leaf ("leaf") — the
        //     #191/#676 column-mapping payload stamped into a nested tree, with the #330 unquoted
        //     integer id contract, now pinned at the artifact layer rather than only the helper layer.
        //
        // NOT pinned here, because the writer still REFUSES it (message: "deferred, #585", a dangling ref
        // to the closed #585; tracked by #873): the recursive OBJECT
        // arms — a nested container or struct nested INSIDE another (array<struct>, map<string,array>,
        // struct<x:struct>). CreateNestedLeaf throws "a nested type within a nested type ... (deferred,
        // #585)" for those, so no artifact test can pin them until that write support lands. They
        // remain helper-only (DeltaSchemaJsonComplexTypeTests) meanwhile; see the completeness guard's
        // AssertWriterRefusesAsync probes, which pin that refusal boundary on the write side.
        string schemaJson = await WriteAndReadFooterSchemaAsync(NestedCorpusSchema);

        // Same dual oracle as the scalar/metadata siblings, same execution order: the golden fires
        // first for any footer-byte divergence (a call-site swap AND shared-serializer drift reaching
        // the footer both trip it, shadowing the equality); the equality is the only assertion that can
        // fail while the footer still matches the golden, i.e. when SchemaJson drifts and the footer
        // does not follow. Neither subsumes the other.
        Assert.Equal(NestedFooterGolden, schemaJson);
        Assert.Equal(SchemaJson.ToJson(NestedCorpusSchema), schemaJson);
    }

    [Fact]
    public async Task WrittenFooter_PinsNestedTypes_FromRealNestedRows()
    {
        // #713: the DATA-BEARING nested footer test. The corpus above writes ZERO batches, which
        // reaches the real call site but never materialises a nested column vector, so it pins the
        // footer the writer produces from the SCHEMA. This test drives real nested rows through the
        // NestedColumnShredder — an array<int>, a map(string→int) and a struct<int,string>, each with
        // present/null/empty rows — so the footer is the one the writer stamps while actually shredding
        // nested data. That is the issue's core point: the helper layer cannot tell the two apart, and
        // only a data-bearing write pins the footer against the true write path.
        var arrType = DataTypes.CreateArrayType(DataTypes.IntegerType);
        var mapType = DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType);
        var structType = DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: true),
            new StructField("b", DataTypes.StringType, nullable: true),
        });
        var schema = new StructType(new[]
        {
            new StructField("a", arrType, nullable: true),
            new StructField("m", mapType, nullable: true),
            new StructField("s", structType, nullable: true),
        });

        var aVec = NestedVectors.IntList(
            arrType, new int?[]?[] { new int?[] { 1, 2 }, null, Array.Empty<int?>() });
        var mVec = NestedVectors.StringIntMap(
            mapType,
            new IReadOnlyList<(string Key, int? Value)>?[]
            {
                new[] { ("k1", (int?)1) },
                null,
                Array.Empty<(string, int?)>(),
            });
        var sVec = NestedVectors.IntStringStruct(
            structType, new (int? A, string? B)?[] { (1, "one"), null, (3, null) });
        var batch = new ManagedColumnBatch(
            schema, new ColumnVector[] { aVec, mVec, sVec }, rowCount: 3);

        const string dataFooterGolden =
            "{\"type\":\"struct\",\"fields\":[" +
            "{\"name\":\"a\",\"type\":{\"type\":\"array\",\"elementType\":\"integer\",\"containsNull\":true}," +
            "\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"m\",\"type\":{\"type\":\"map\",\"keyType\":\"string\",\"valueType\":\"integer\"," +
            "\"valueContainsNull\":true},\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"s\",\"type\":{\"type\":\"struct\",\"fields\":[" +
            "{\"name\":\"a\",\"type\":\"integer\",\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"b\",\"type\":\"string\",\"nullable\":true,\"metadata\":{}}]}," +
            "\"nullable\":true,\"metadata\":{}}]}";

        string dataBearing = await WriteAndReadFooterSchemaAsync(schema, batch);

        // Dual oracle on the data-bearing footer.
        Assert.Equal(dataFooterGolden, dataBearing);
        Assert.Equal(SchemaJson.ToJson(schema), dataBearing);

        // PROVENANCE: the footer must be byte-identical whether or not nested vectors were
        // materialised. The zero-batch path builds the custom-metadata dict before the row-group loop;
        // this asserts the shredding path did not perturb it. If they ever diverge, one of the two
        // call sites is stamping a different schema string for the same schema.
        string zeroBatch = await WriteAndReadFooterSchemaAsync(schema);
        Assert.Equal(zeroBatch, dataBearing);
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
        "{\"name\":\"ctrl\\u0001x\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}," +
            "{\"name\":\"nul\\u0000end\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}]}";

    /// <summary>The field-name artifact corpus; shared with the encoding completeness guard.</summary>
    private static readonly StructType NameCorpusSchema = new(new[]
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
        // U+0000. The writer accepts it in all three string positions, and a footer
        // serializer that round-tripped through a NUL-TERMINATED UTF-8 buffer truncated the
        // name there -- declaring a different column set than the log, fully green.
        new StructField("nul\0end", DataTypes.LongType, nullable: true),
    });

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

        string schemaJson = await WriteAndReadFooterSchemaAsync(NameCorpusSchema);

        // Golden hoisted to NameEncodingGolden so the escape-form completeness guard reads THIS
        // string rather than a copy of it.

        Assert.Equal(NameEncodingGolden, schemaJson);
        Assert.Equal(SchemaJson.ToJson(NameCorpusSchema), schemaJson);

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
    public void EncodingCorpus_CoversEveryEscapeFormInEveryStringPosition()
    {
        // #679: COMPLETENESS guard for STRING ENCODING, across every position a caller-supplied
        // string can occupy in the wire format.
        //
        // Two independent things go wrong here, and an earlier version of this guard got both:
        //
        //   1. The required SET was hand-listed. The encoder emits SEVEN distinct escape forms and
        //      the first corpus covered four, omitting \b, \f and \r with nothing saying so.
        //   2. The set of POSITIONS was hand-listed -- implicitly, at one. This guard checked field
        //      names only, while a schema JSON has THREE arbitrary-string positions: field name,
        //      metadata key, and metadata string value. A rogue that escaped names correctly but
        //      emitted metadata keys and values raw was byte-exact for every pinned corpus here
        //      and shipped 1648-green.
        //
        // Both are now derived. The required set is probed out of the serializer; each position is
        // checked against that same set, so widening the encoder or adding a corpus row cannot
        // leave one position silently behind the others.
        //
        //   3. The set of DEPTHS was hand-listed -- also implicitly, at one. The walk below
        //      unioned its findings across nesting levels, so a form appearing in a TOP-LEVEL
        //      metadata key satisfied "metadata key" outright while every nested key, nested
        //      string value and array element string in the corpus was bare ASCII. A rogue that
        //      escaped correctly at depth 0 and emitted raw at depth >= 1 was byte-exact for
        //      every pinned corpus and shipped 1645-green.
        //
        // Depth is now part of the cell key, and the required depth range is stated ONCE as an
        // explicit bound (RequiredMetadataDepth) rather than falling out of whatever the corpus
        // happens to contain. That bound is the one hand-choice left in this guard, and it is
        // deliberately in a single named place so a reviewer can see and challenge it.
        SortedSet<string> required = ProbeEmittedEscapeForms();

        // Non-vacuity: a probe loop that silently stopped matching would make everything below pass
        // trivially, so require the encoder to have produced a plausible number of forms.
        Assert.True(required.Count >= 5, $"Escape-form probe produced only {required.Count} forms.");

        (string Position, IEnumerable<string> Strings)[] positions =
        [
            ("field name", NameCorpusSchema.Select(field => field.Name)),
        ];

        var cells = new Dictionary<(string Position, int Depth), SortedSet<string>>();

        static SortedSet<string> Cell(
            Dictionary<(string, int), SortedSet<string>> into, string position, int depth)
        {
            if (!into.TryGetValue((position, depth), out SortedSet<string>? set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                into[(position, depth)] = set;
            }

            return set;
        }

        foreach ((string position, IEnumerable<string> strings) in positions)
        {
            foreach (string candidate in strings)
            {
                Cell(cells, position, 0).UnionWith(EscapeFormsIn(candidate));
            }
        }

        foreach ((string position, int depth, string value) in MetadataStrings(MetadataCorpusSchema))
        {
            Cell(cells, position, depth).UnionWith(EscapeFormsIn(value));
        }

        // The required cells come from the GRAMMAR of the wire format -- the set of arbitrary-string
        // positions a schema JSON admits -- crossed with the depth bound, not from what the corpus
        // happens to contain. A cell the corpus never reaches is therefore a failure, not a silent
        // absence, which is precisely what the union-across-depths version could not express.
        (string Position, int Depth)[] requiredCells =
            RequiredStringCells(RequiredMetadataDepth).ToArray();

        foreach ((string position, int depth) in requiredCells)
        {
            SortedSet<string> covered = cells.TryGetValue((position, depth), out SortedSet<string>? found)
                ? found
                : new SortedSet<string>(StringComparer.Ordinal);

            string[] missing = required.Where(form => !covered.Contains(form)).ToArray();
            Assert.True(
                missing.Length == 0,
                $"The serializer emits these escape forms but no artifact corpus row exercises them "
                + $"in the {position} position at depth {depth}, so a rogue mishandling them there "
                + $"would ship undetected: {string.Join(" ", missing)}");
        }

        // Non-vacuity for the goldens themselves: the escaped bytes must actually appear in the
        // pinned footers, not merely in the in-memory corpus.
        Assert.Contains("\\u00E9", NameEncodingGolden, StringComparison.Ordinal);
        Assert.Contains("\\u00E9", MetadataFooterGolden, StringComparison.Ordinal);
    }

    /// <summary>
    /// Probes the serializer to discover which escape forms it actually emits, rather than
    /// restating its policy. If the encoder changes, this set changes with it.
    /// </summary>
    private static SortedSet<string> ProbeEmittedEscapeForms() => EscapeProbe.Forms;

    /// <summary>
    /// Returns the escape forms the serializer emits for <paramref name="value"/>, obtained by
    /// running it through <c>SchemaJson</c> itself -- the serializer is its own oracle here.
    /// </summary>
    private static IEnumerable<string> EscapeFormsIn(string value)
    {
        string json = SchemaJson.ToJson(
            new StructType([new StructField("x" + value + "y", DataTypes.LongType)]));
        int start = json.IndexOf("\"name\":\"x", StringComparison.Ordinal) + 9;
        int end = json.IndexOf("y\",\"type\"", StringComparison.Ordinal);
        string encoded = json[start..end];

        var forms = new List<string>();
        for (int i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] != '\\')
            {
                continue;
            }

            if (encoded[i + 1] != 'u')
            {
                forms.Add(encoded.Substring(i, 2));
                i++;
                continue;
            }

            // \uXXXX. BMP and ASTRAL must stay DISTINGUISHABLE. An earlier version collapsed both
            // to a single "\u" family token, which made a corpus containing only \u00E9 satisfy the
            // requirement for astral -- and an astral codepoint is emitted as a surrogate PAIR, two
            // \u escapes for one character, which is a materially different encoding path. A rogue
            // emitting astral characters raw was byte-exact for every corpus row that used only BMP.
            int code = int.Parse(
                encoded.AsSpan(i + 2, 4),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
            if (char.IsHighSurrogate((char)code))
            {
                forms.Add("\\u(astral pair)");
                i += 11;
            }
            else
            {
                forms.Add("\\u(bmp)");
                i += 5;
            }
        }

        return forms;
    }

    /// <summary>
    /// The metadata nesting depth to which the encoding guard requires every escape form to be
    /// pinned in every position. This is an explicit BOUND, not a derivation: the input grammar
    /// admits unbounded nesting, so some finite cut is unavoidable and it belongs in one named
    /// place rather than being an emergent property of the corpus. Depth 0 is a field's own
    /// metadata; depth 1 is inside a nested object or array value. Raising it requires
    /// corresponding corpus rows, and the guard will say exactly which cells are missing.
    /// </summary>
    private const int RequiredMetadataDepth = 1;

    /// <summary>
    /// Recursively collects every arbitrary string in the metadata of <paramref name="schema"/>,
    /// tagged with the wire-format POSITION it occupies and its nesting DEPTH.
    /// <para>
    /// An earlier version returned bare strings and let the caller union them, which made a form
    /// present at depth 0 satisfy the check for all depths. Depth is part of the identity of a
    /// position, so it is carried here rather than discarded.
    /// </para>
    /// </summary>
    private static IEnumerable<(string Position, int Depth, string Value)> MetadataStrings(
        StructType schema)
    {
        var found = new List<(string Position, int Depth, string Value)>();
        foreach (StructField field in schema)
        {
            Walk(field.Metadata, found, depth: 0);
        }

        return found;

        static void Walk(
            FieldMetadata metadata, List<(string, int, string)> into, int depth)
        {
            foreach (KeyValuePair<string, MetadataValue> entry in metadata)
            {
                into.Add(("metadata key", depth, entry.Key));
                WalkValue(entry.Value, into, depth, "metadata string value");
            }
        }

        static void WalkValue(
            MetadataValue value, List<(string, int, string)> into, int depth, string position)
        {
            switch (value.Kind)
            {
                case MetadataValueKind.String:
                    into.Add((position, depth, value.AsString()));
                    break;
                case MetadataValueKind.Array:
                    foreach (MetadataValue item in value.AsArray())
                    {
                        WalkValue(item, into, depth + 1, "array element string");
                    }

                    break;
                case MetadataValueKind.Nested:
                    Walk(value.AsNested(), into, depth + 1);
                    break;
                default:
                    break;
            }
        }
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

        // The candidate set is DERIVED by asking the writer across the whole DataType space --
        // including the full decimal precision sweep -- not by reflecting over AtomicType
        // subclasses. That earlier derivation reached 17 types; asking the writer reaches 95,
        // because DecimalType is a parameterised family whose parameters are protocol-visible.
        // Eleven-plus accepted types sat outside the old derivation's reach while it looked
        // thorough: the same leaf-of-the-derivation defect, one level further out.
        IReadOnlyList<DataType> accepted = await WriterAcceptedTypesAsync();

        // Non-vacuity: an empty or collapsed support would make every check below pass trivially.
        Assert.True(
            accepted.Count >= 20,
            $"Writer-accepted type support collapsed to {accepted.Count}.");

        // What the GOLDEN corpus must pin, and what it deliberately does not.
        //
        // Pinning all 95 accepted types as literal bytes is neither possible nor useful -- the
        // decimal family is unbounded in principle and its members differ only in two integers.
        // The division of labour is explicit:
        //   * every accepted NON-parameterised type must be pinned as bytes here, because each one
        //     is a distinct arm of the serializer's type switch;
        //   * the parameterised family is pinned at its BOUNDARIES (the widest precision the writer
        //     accepts, and the maximum scale), because that is where its encoding can break;
        //   * breadth across the interior of the family is covered by the GENERATED surface, whose
        //     support is this same derived list.
        // A type that is neither pinned nor generated would be invisible; none is.
        // NESTED types are excluded from the SCALAR corpus's completeness obligation: since #841 the
        // writer accepts single-level array/map/struct, but their footer wire shape is an
        // {"type":"array"|"map"|"struct", ...} OBJECT rather than a scalar type-name string, so their
        // FOOTER bytes are pinned by WrittenFooter_PinsSingleLevelNestedTypes /
        // WrittenFooter_PinsNestedTypes_FromRealNestedRows (artifact layer, both polarities of
        // containsNull/valueContainsNull and a nested struct), and their Dremel level encoding by
        // NestedParquetWriteTests (round-trip + literal level streams), instead of by ScalarFooterGolden.
        // The refusal-boundary assertions below pin that acceptance so this exclusion cannot quietly
        // hide a regression in either direction.
        string[] unparameterised = accepted
            .Where(t => !t.TypeName.StartsWith("decimal", StringComparison.Ordinal))
            .Where(t => t is not (ArrayType or MapType or StructType))
            .Select(t => t.TypeName)
            .ToArray();
        string[] missing = unparameterised
            .Where(typeName => !pinnedTypeNames.Contains(typeName, StringComparer.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            missing.Length == 0,
            "ParquetFileWriter accepts these types but WrittenFooter_PinsEveryScalarTypeTheWriterAccepts "
            + "does not pin them, so a footer/log divergence in them would ship undetected: "
            + string.Join(", ", missing));

        // The widest accepted decimal precision must be pinned as bytes: it is the boundary, and a
        // boundary that only the generator visits is a boundary nothing pins the wire shape of.
        int widestPrecision = accepted
            .OfType<DecimalType>()
            .Max(d => d.Precision);
        Assert.Contains(
            pinnedTypeNames,
            t => t.StartsWith($"decimal({widestPrecision},", StringComparison.Ordinal));

        // The acceptance boundary, asserted rather than described. #841 MOVED it: the three SINGLE-LEVEL
        // nested shapes are now written, so they must be accepted here; the null type and every
        // out-of-scope nested shape must still be refused outright.
        string[] acceptedNames = accepted.Select(t => t.TypeName).ToArray();
        Assert.Contains("array", acceptedNames, StringComparer.Ordinal);
        Assert.Contains("map", acceptedNames, StringComparer.Ordinal);
        Assert.Contains("struct", acceptedNames, StringComparer.Ordinal);
        Assert.DoesNotContain("void", acceptedNames, StringComparer.Ordinal);

        // The residual refusals (#585/§2.4a/§2.6): nested-within-nested, a zero-field struct, and a
        // non-nullable nested container. Asserted through the same real-write probe the acceptance set is
        // derived from, so the boundary is measured on both sides rather than described on one.
        await AssertWriterRefusesAsync(
            new StructField(
                "probe", DataTypes.CreateArrayType(DataTypes.CreateArrayType(DataTypes.LongType)),
                nullable: true));
        await AssertWriterRefusesAsync(
            new StructField("probe", DataTypes.CreateStructType(Array.Empty<StructField>()), nullable: true));
        await AssertWriterRefusesAsync(
            new StructField("probe", DataTypes.CreateArrayType(DataTypes.LongType), nullable: false));
    }

    private static async Task AssertWriterRefusesAsync(StructField field)
    {
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => WriteAndReadFooterSchemaAsync(new StructType([field])));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
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

    /// <summary>
    /// The arbitrary-string cells of the wire format: every (position, depth) pair the grammar
    /// admits up to <paramref name="depthBound"/>. Shared by the corpus guard and the generator
    /// audit so the two cannot drift apart -- the generator audit checked MARGINALS while the
    /// corpus guard checked cells, which is how a depth-only rogue survived one and not the other.
    /// <para>
    /// Sharing a source is not automatically safe -- a shared loop bound is exactly how U+0000 hid
    /// from the audit that was supposed to find it, because the prober and the thing it probed
    /// agreed while both were short. The difference here is the direction of the sharing: this is
    /// one DERIVATION consumed by two independent CHECKS, not a check validating itself against
    /// its own input. If this method is wrong both guards fail together and loudly; if it were a
    /// bound shared with a prober, both would pass together and silently.
    /// </para>
    /// </summary>
    private static IEnumerable<(string Position, int Depth)> RequiredStringCells(int depthBound)
    {
        yield return ("field name", 0);
        for (int depth = 0; depth <= depthBound; depth++)
        {
            yield return ("metadata key", depth);
            yield return ("metadata string value", depth);

            // An array element is by construction inside its array, so it cannot occur at depth 0.
            if (depth >= 1)
            {
                yield return ("array element string", depth);
            }
        }
    }

    /// <summary>
    /// String lengths the systematic sweep pins.
    /// <para>
    /// UNLIKE every other domain in this file, these values are <b>chosen, not derived</b>, and
    /// that is stated rather than disguised. There is no external ground truth for magnitude: the
    /// writer accepts any length and the serializer's behaviour does not change at any documented
    /// boundary, so nothing can be probed for it. They bracket the buffer sizes real serializers
    /// actually use -- <c>stackalloc</c> thresholds and pooled-buffer sizes -- because that is
    /// where truncation lives. A reviewer should treat this array as the weakest derivation here.
    /// </para>
    /// <para>
    /// VERDICT (#726, closed-as-documented; CORRECTED in round 2 after a red-team pass). String
    /// magnitude has NO external ground truth to derive from, so this domain cannot be made derived
    /// the way type support (writer acceptance), <c>MetadataValueKind</c> (<c>Enum.GetValues</c>),
    /// escape forms (encoder output), codepoints (the UCD) and numeric boundaries (reflection) are.
    /// The adjudication is therefore NOT to force an underivable derivation but to make the chosen
    /// domain (a) explicit -- this comment -- and (b) load-bearing.
    /// </para>
    /// <para>
    /// WHAT (b) ACTUALLY MEANS, because the earlier wording of this verdict OVER-CLAIMED and the
    /// over-claim is worth recording. It said <c>SchemaDegreesOfFreedom_AreEachVaried</c> requires
    /// the sweep's longest string to exceed the largest plausible fixed buffer, "so a 256-char
    /// truncating serializer is killed". That is FALSE as stated for a metadata-VALUE truncation:
    /// <c>SchemaDegreesOfFreedom_AreEachVaried</c> inspects the generated INPUT fixtures and asserts
    /// they REACH <see cref="MinimumLongestString"/>. It serializes nothing and re-parses nothing,
    /// so no truncating serializer can fail it. Input-fixture variety is a CORPUS property, not an
    /// output property, and only an output property can catch an output defect.
    /// </para>
    /// <para>
    /// The real catch is a serialized-then-RE-PARSED value-EQUALITY assertion over a corpus that
    /// carries a value longer than the buffer:
    /// <c>DeltaFooterLogSchemaParityTests</c>'s <c>*_PreserveEveryMetadataEntryTheCallerDeclared</c>
    /// family, whose <c>HostileSchema</c> declares a metadata string value of 4097 characters (flat,
    /// inside an array, and inside a nested object) and whose oracle parses the committed artifact
    /// back and asserts <c>Assert.Equal(entry.Value, value)</c> against what the caller declared. A
    /// serializer truncating a metadata value at 256, 1024 or 4096 goes RED there (measured). Note
    /// what does NOT catch it, and why the mistake was easy to make: footer/log BYTE parity is blind
    /// to it, because a truncation in the shared serializer is SYMMETRIC -- both artifacts lose the
    /// same tail and still match each other, and so does any assertion comparing an artifact to a
    /// fresh <c>SchemaJson.ToJson</c> of the same schema.
    /// </para>
    /// <para>
    /// So this array's role is the honest, smaller one: it ensures the FIXTURES exercise long
    /// strings in every position and depth of the wire grammar (which is what makes hostile-content
    /// coverage real, and what the joint-cell requirement below is built on), and it is BACKED by
    /// the round-trip value-fidelity pin above for the output property. Completeness of the bracket
    /// set remains a judgement about plausible implementations, which is disclosed, not hidden.
    /// </para>
    /// </summary>
    private static readonly int[] MagnitudeLengths = [1, 255, 256, 257, 1023, 1024, 4097];

    /// <summary>
    /// The codepoints the systematic sweep places in every position at every depth.
    /// <para>
    /// GROUND TRUTH IS EXTERNAL TO THIS TEST. Earlier versions kept one representative per escape
    /// FORM, which is a lossy projection: <c>SchemaJson</c> escapes every non-ASCII character
    /// identically, so U+2028 LINE SEPARATOR and U+00E9 share a form and only one survived --
    /// while a rogue using a laxer encoder emits U+2028 raw and breaks JSON parsers that treat it
    /// as a line terminator. Keying on the serializer's own output cannot distinguish them,
    /// because the serializer treats them the same.
    /// </para>
    /// <para>
    /// So the set is derived from the <b>Unicode character database</b> instead: complete over the
    /// first 256 codepoints, then sampled per <see cref="System.Globalization.UnicodeCategory"/>
    /// across the BMP and the astral planes. U+2028 and U+2029 appear here because they are the
    /// sole members of <c>LineSeparator</c> and <c>ParagraphSeparator</c> -- derived, not
    /// remembered. Adding a category to Unicode adds cells here without anyone editing this file.
    /// </para>
    /// <para>Lone surrogates are excluded: they are #710, a known-open defect these tests are not about.</para>
    /// </summary>
    private static IReadOnlyList<string> HazardCodepoints => _hazardCodepoints ??= BuildHazardCodepoints();

    private static IReadOnlyList<string>? _hazardCodepoints;

    private const int HazardSamplesPerCategory = 2;

    private static IReadOnlyList<string> BuildHazardCodepoints()
    {
        var result = new List<string>();
        var perCategory = new Dictionary<System.Globalization.UnicodeCategory, int>();

        // Complete over the first 256 codepoints: NUL, the C0 controls, the ASCII specials, C1 and
        // Latin-1. No sampling, no bound to be wrong.
        for (int c = 0; c < 0x100; c++)
        {
            result.Add(char.ConvertFromUtf32(c));
        }

        // Then every Unicode category, sampled. The enumeration source is the UCD, not this file.
        for (int c = 0x100; c <= 0x10FFFF; c++)
        {
            if (c is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            System.Globalization.UnicodeCategory category =
                System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            perCategory.TryGetValue(category, out int seen);
            if (seen >= HazardSamplesPerCategory)
            {
                continue;
            }

            perCategory[category] = seen + 1;
            result.Add(char.ConvertFromUtf32(c));
        }

        return result;
    }

    /// <summary>
    /// The footer schemaString must not depend on THE DATA — same schema, rows and no rows, byte
    /// for byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="WriteAndReadFooterSchemaAsync"/> writes ZERO batches, which is a large speed win
    /// (no row groups) and was chosen for that reason. But it also silently PINS a parameter of the
    /// thing under test, and a pinned parameter is a coordinate no rogue has to respect: a
    /// divergence conditioned on rows being present is invisible to every test that goes through
    /// that oracle. Measured — a footer serializer handed a metadata-stripped schema only when
    /// <c>totalRows &gt; 0</c> was 0 kills across the whole footer surface.
    /// </para>
    /// <para>
    /// So the row count is stated here as a DIFFERENTIAL property rather than left as a provenance
    /// argument about today's call site. Every artifact schema is written both ways and the two
    /// footers must be identical to each other and to the shared serializer. All-null rows are used
    /// deliberately: they are constructible for every type the writer accepts, so this covers the
    /// whole corpus rather than the subset someone was willing to hand-build values for.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FooterSchemaString_IsIndependentOfWhetherRowsWereWritten()
    {
        var covered = new HashSet<(string, int, string)>();
        foreach ((string what, StructType schema) in ConstructiveArtifacts(covered))
        {
            string expected = SchemaJson.ToJson(schema);
            string empty = await WriteAndReadFooterSchemaAsync(schema);
            string populated = await WriteAndReadFooterSchemaAsync(schema, SingleRowBatch(schema));

            if (!string.Equals(empty, populated, StringComparison.Ordinal))
            {
                Assert.Fail(
                    $"The footer schemaString changed when rows were written, at {what}."
                    + $"{Environment.NewLine}  no rows ({empty.Length} chars): {Truncate(empty)}"
                    + $"{Environment.NewLine}  rows    ({populated.Length} chars): {Truncate(populated)}");
            }

            Assert.Equal(expected, populated);
        }
    }

    /// <summary>
    /// A single row: null wherever the schema permits one, a value wherever it does not.
    /// </summary>
    /// <remarks>
    /// Nulls are used wherever they are legal so that BOTH definition-level paths are exercised
    /// rather than only the dense one — the writer rejects a null in a non-nullable column
    /// (<c>"Non-nullable column 'a' holds a null at row 0"</c>), which is why this is not simply an
    /// all-null row. The value side dispatches over the type surface rather than a hand-picked
    /// subset, and the default arm FAILS CLOSED, so a newly accepted writer type cannot quietly
    /// drop out of this test's coverage.
    /// </remarks>
    private static ColumnBatch SingleRowBatch(StructType schema)
    {
        var vectors = new ColumnVector[schema.Count];
        for (int i = 0; i < schema.Count; i++)
        {
            StructField field = schema[i];
            MutableColumnVector vector = ColumnVectors.Create(field.DataType, 1);
            if (field.Nullable)
            {
                vector.AppendNull();
            }
            else
            {
                AppendOneValue(vector, field.DataType);
            }

            vectors[i] = vector;
        }

        return new ManagedColumnBatch(schema, vectors, 1);
    }

    private static void AppendOneValue(MutableColumnVector vector, DataType type) =>
        ScalarCorpus.AppendOne(vector, type);

    private static string Truncate(string text) =>
        text.Length <= 400 ? text : text[..200] + $" …[{text.Length} chars]… " + text[^200..];

    /// <summary>Renders each column with its nullability, so a per-column divergence is legible.</summary>
    private static string NullabilityReport(string[] names, bool[] nullable) =>
        Truncate(string.Join(
            ", ",
            names.Zip(nullable, (n, b) => $"{n}:{(b ? "null" : "req")}")));

    /// <summary>
    /// Collection sizes the constructive sweep emits.
    /// <para>
    /// PARTIALLY DERIVED, and the split matters. WHICH collections must be varied is derived --
    /// <see cref="SchemaDegreesOfFreedom_AreEachVaried"/> finds every collection-valued member of
    /// the schema types by reflection, so a new collection cannot be added to the model without
    /// this file failing until it is exercised. WHAT COUNTS to use is not derivable: neither the
    /// writer nor the reader has a cardinality limit to probe, exactly as with
    /// <see cref="MagnitudeLengths"/>. These bracket the inline/pooled buffer capacities real
    /// serializers use. Zero is included because an empty collection is a distinct emission path
    /// (no separators at all), not merely a small one.
    /// </para>
    /// </summary>
    private static readonly int[] CardinalityCounts = [0, 1, 15, 16, 17, 63, 64, 65, 257];

    /// <summary>
    /// The largest collection <see cref="SchemaDegreesOfFreedom_AreEachVaried"/> demands to see.
    /// <para>
    /// This is a chosen number, like <see cref="CardinalityCounts"/> -- but it is DELIBERATELY NOT
    /// derived from that array. Writing it as <c>CardinalityCounts.Max()</c> was the first attempt
    /// and it was vacuous: narrowing the sweep's counts narrowed the requirement along with them,
    /// so the guard agreed with whatever it was auditing. That is the same shared-source defect
    /// this file fixed at the codepoint axis and again at the depth axis, made a third time in the
    /// guard written to prevent it -- which is the strongest evidence available that the shape is
    /// easy to reintroduce and worth a named rule.
    /// </para>
    /// <para>
    /// Two chosen numbers from independent places disagree when either moves; one number cannot.
    /// </para>
    /// </summary>
    private const int MinimumLargestCollection = 257;

    /// <summary>
    /// The longest string <see cref="SchemaDegreesOfFreedom_AreEachVaried"/> demands to see.
    /// Chosen, and deliberately NOT <c>MagnitudeLengths.Max()</c>, for the reason given on
    /// <see cref="MinimumLargestCollection"/>: a requirement derived from the thing it audits
    /// agrees with it however that thing is narrowed.
    /// <para>
    /// This bounds the FIXTURES only. The matching OUTPUT-side pin -- a metadata value of the same
    /// magnitude, serialized, re-parsed, and compared for equality with what the caller declared,
    /// which is what a truncating serializer actually fails -- lives in
    /// <c>DeltaFooterLogSchemaParityTests.LongMetadataValueLength</c>, chosen independently there so
    /// the two numbers disagree loudly if either moves.
    /// </para>
    /// </summary>
    private const int MinimumLongestString = 4097;

    /// <summary>
    /// The schemas the cardinality sweep writes: every count above, in every collection-valued
    /// position the model has, with <c>Nullable</c> varied across them.
    /// </summary>
    /// <summary>
    /// Content for slot <paramref name="i"/> of a swept collection, rotating through the boundary
    /// classes the degrees-of-freedom guard requires so that every class occurs INSIDE a
    /// collection of every swept size.
    /// </summary>
    /// <remarks>
    /// The rotation is by index rather than by a hand-placed special case, so the classes land in
    /// every collection regardless of its size, and the long one appears once per collection
    /// rather than in every slot -- a 257-entry collection of 4097-character keys is a megabyte of
    /// metadata for no extra coverage.
    /// </remarks>
    private static string ContentAt(int i, string plain, bool allowEmpty) =>
        // Exactly ONE empty slot per collection, not one in seven: an empty key repeated is a
        // single dictionary entry, so a rotating empty silently SHRANK the collections it was
        // meant to decorate and pulled the cardinality axis back below its own boundary.
        i == 2 && allowEmpty
            ? string.Empty
            : (i % 7) switch
            {
                1 => "caf\u00E9\u2028\uD83D\uDE00" + plain,
                3 => plain + new string('m', MinimumLongestString),
                _ => plain,
            };

    private static IEnumerable<(string What, StructType Schema)> CardinalitySweepSchemas()
    {
        foreach (int n in CardinalityCounts)
        {
            // Field count. Zero is skipped because the WRITER rejects a fieldless schema -- a
            // probed fact, asserted by CardinalitySweep_SkipsOnlyWhatTheWriterRejects, not an
            // assumption. If that ever changes, the assertion fails and this regains a count.
            if (n > 0)
            {
                var fields = new List<StructField>();
                for (int i = 0; i < n; i++)
                {
                    // CARDINALITY x ENCODING. The counts used to carry nothing but plain ASCII, so
                    // "large collections are covered" and "hostile content is covered" were both
                    // true while the cell where a LARGE collection carries HOSTILE content was
                    // covered by neither. Content classes are mixed in at every count.
                    fields.Add(new StructField(
                        ContentAt(i, $"f{i}", allowEmpty: false), DataTypes.LongType,
                        nullable: i % 2 == 0));
                }

                yield return ($"{n} fields", new StructType(fields));
            }

            // Metadata entry count -- the position that evicts protocol keys when it overflows.
            var entries = new List<KeyValuePair<string, MetadataValue>>();
            for (int i = 0; i < n; i++)
            {
                entries.Add(new KeyValuePair<string, MetadataValue>(
                    ContentAt(i, $"a.tenant.tag{i:D3}", allowEmpty: true),
                    MetadataValue.String(ContentAt(i + 1, $"v{i}", allowEmpty: true))));
            }

            entries.Add(new KeyValuePair<string, MetadataValue>(
                "delta.columnMapping.id", MetadataValue.Long(7)));
            entries.Add(new KeyValuePair<string, MetadataValue>(
                "delta.columnMapping.physicalName", MetadataValue.String("col-7")));
            entries.Add(new KeyValuePair<string, MetadataValue>(
                "delta.identity.start", MetadataValue.Long(4)));

            yield return ($"{n} metadata entries", new StructType(
            [
                new StructField(
                    "m", DataTypes.LongType, nullable: n % 2 == 0,
                    FieldMetadata.FromValues(entries.ToArray())),
            ]));

            // Array element count, and a nested metadata object of the same size one level down.
            var items = new List<MetadataValue>();
            for (int i = 0; i < n; i++)
            {
                items.Add(MetadataValue.String(ContentAt(i, $"e{i}", allowEmpty: true)));
            }

            yield return ($"{n} array elements", new StructType(
            [
                new StructField(
                    "a", DataTypes.StringType, nullable: n % 2 != 0,
                    FieldMetadata.FromValues(
                    [
                        new KeyValuePair<string, MetadataValue>("arr", MetadataValue.Array(items.ToArray())),
                        new KeyValuePair<string, MetadataValue>(
                            "obj", MetadataValue.Nested(FieldMetadata.FromValues(entries.ToArray()))),
                    ])),
            ]));
        }
    }

    /// <summary>
    /// The cardinality sweep skips exactly one cell -- zero fields -- and only because the writer
    /// refuses it. This asserts that refusal, so the skip is a measured property of the writer
    /// rather than a convenience.
    /// </summary>
    [Fact]
    public async Task CardinalitySweep_SkipsOnlyWhatTheWriterRejects()
    {
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await WriteAndReadFooterSchemaAsync(new StructType([])));
    }

    /// <summary>
    /// META-GUARD: every degree of freedom the schema model HAS must be varied by something this
    /// file writes -- and the list of degrees of freedom is read off the TYPES, not written here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sixteen review rounds found sixteen divergence axes, one at a time, and each was fixed by
    /// deriving the values WITHIN that axis from an external source. That works, and it never
    /// terminates, because the set of AXES was still discovered by inspection: two consecutive
    /// rounds found structural axes (nesting depth, then collection cardinality) that no amount of
    /// character-level derivation could have reached.
    /// </para>
    /// <para>
    /// The input to the serializer is a closed object model, so the axes are not actually open:
    /// they are the members of <c>StructType</c>, <c>StructField</c>, <c>FieldMetadata</c> and
    /// <c>MetadataValue</c>. Reflection enumerates them. This test asserts that each one is
    /// actually VARIED by the corpus, with the requirement derived from the member's TYPE: a
    /// <c>bool</c> must show both values, an enum every value, a count several values including a
    /// large one, a string several values -- and, where a collection and its contents interact,
    /// the JOINT cell rather than the two marginals.
    /// </para>
    /// <para>
    /// This paragraph used to name <c>DataType</c> as well. It was not in the list the code walks,
    /// which made it a completeness claim in prose that no test runs -- the same pattern as the
    /// table deleted from this PR earlier, found independently by two reviewers. The consequence
    /// is real and is tracked as issue #729: <c>DecimalType.Precision</c> and <c>Scale</c> are
    /// required by nothing here.
    /// </para>
    /// <para>
    /// One category is outside this scheme entirely and cannot be reached by extending it: an axis
    /// must be a member of the input model, so AMBIENT PROCESS STATE is invisible to this guard by
    /// construction. Culture is the instance that mattered -- a culture-sensitive number format
    /// makes the footer structurally invalid JSON -- and it is pinned separately by
    /// <see cref="SharedSerializer_IsIndependentOfAmbientCulture"/> rather than by anything derived
    /// from the model.
    /// </para>
    /// <para>
    /// So adding a property to the model -- or, as happened here, leaving <c>Nullable</c> pinned to
    /// <c>true</c> and every collection under nine elements -- fails this test rather than waiting
    /// for a reviewer to notice. That is the axis-enumeration problem given the same treatment
    /// <c>UnicodeCategory</c> gave the codepoint problem: rooted in a definition outside the test.
    /// </para>
    /// <para>
    /// SCOPE, stated because a verdict elsewhere in this file once overstated it (#726, round 2).
    /// This guard is a claim about the CORPUS, not about the OUTPUT: it inspects the schemas this
    /// file writes and asserts each axis takes enough distinct values. Nothing here serializes a
    /// fixture and re-reads it, so no defect of the serializer -- a truncation, a dropped entry, a
    /// mangled escape -- can fail this test. Its job is to keep the fixtures wide; the artifact
    /// guards in this file and the end-to-end value-fidelity guards in
    /// <c>DeltaFooterLogSchemaParityTests</c> are what turn a wide fixture into a caught defect.
    /// </para>
    /// </remarks>
    [Fact]
    public void SchemaDegreesOfFreedom_AreEachVaried()
    {
        var observed = new Dictionary<string, HashSet<object>>(StringComparer.Ordinal);
        var joint = new JointCells();
        foreach (StructType schema in ArtifactSchemas())
        {
            ObserveType(schema, observed, joint);
        }

        var unvaried = new List<string>();
        foreach ((Type owner, System.Reflection.MemberInfo member, bool elements) in SchemaDegreesOfFreedom())
        {
            string key = elements ? $"{owner.Name}.{member.Name}[]" : $"{owner.Name}.{member.Name}";
            HashSet<object> values = observed.TryGetValue(key, out HashSet<object>? v)
                ? v
                : new HashSet<object>();

            Type declared = elements
                ? ElementType(MemberValueType(member))
                : MemberValueType(member);

            // A collection member's axis is its SIZE, and sizes are compared on the same footing
            // as any other count -- so "how many fields", "how many metadata entries" and "how
            // many array elements" are one derived requirement rather than three hand-written ones.
            Type t = !elements && IsCollectionAxis(declared)
                ? typeof(int)
                : Nullable.GetUnderlyingType(declared) ?? declared;
            if (t == typeof(bool))
            {
                if (values.Count < 2)
                {
                    unvaried.Add($"{key} (bool) only ever {Describe(values)}");
                }
            }
            else if (t.IsEnum)
            {
                object[] missing = Enum.GetValues(t).Cast<object>().Where(x => !values.Contains(x)).ToArray();
                if (missing.Length > 0)
                {
                    unvaried.Add($"{key} (enum) never {string.Join("/", missing)}");
                }
            }
            else if (t == typeof(int))
            {
                int max = values.Count == 0 ? -1 : values.Cast<int>().Max();
                if (values.Count < 3 || max < MinimumLargestCollection)
                {
                    unvaried.Add(
                        $"{key} (count) took {values.Count} distinct values, max {max}, "
                        + $"below the required minimum of {MinimumLargestCollection}");
                }
            }
            else if (t == typeof(string))
            {
                // SYMMETRY WITH THE COUNT ARM. This used to require only "two distinct values",
                // while the count arm required a specific boundary -- so a string axis could
                // satisfy the guard while missing the boundary that matters, and the guard would
                // report coverage. A requirement weaker than the thing it certifies is the exact
                // shape this file exists to remove, and it had reached the layer meant to end it.
                //
                // Strings get the same treatment as counts: the empty string is the zero of the
                // length axis, exactly as 0 is for cardinality, and there must be a long one.
                var lengths = values.Cast<string>().Select(x => x.Length).ToArray();
                if (values.Count < 2)
                {
                    unvaried.Add($"{key} (string) only ever {Describe(values)}");
                }
                else if (!lengths.Contains(0) && EmptyStringIsConstructible(owner, member.Name))
                {
                    unvaried.Add(
                        $"{key} (string) is never EMPTY -- the zero of the length axis, and the "
                        + "value a \"skip blanks\" normalisation silently drops");
                }
                else if (lengths.Max() < MinimumLongestString)
                {
                    unvaried.Add(
                        $"{key} (string) reaches only {lengths.Max()} characters, below the "
                        + $"required {MinimumLongestString}");
                }
                else
                {
                    // THE JOINT. Everything above is a MARGINAL: it asks whether the axis ever
                    // took a value, never whether it took it while the collection around it was
                    // large. Both marginals can be satisfied -- large collections of ASCII, and
                    // non-ASCII in small ones -- while the cell where a LARGE collection carries
                    // non-ASCII content is required by neither. A bulk ASCII fast path that
                    // engages only above a size threshold lives exactly there, and it is an
                    // optimisation someone would plausibly write.
                    //
                    // The pairs are not hand-listed. Each class below is one the marginal arm
                    // ALREADY requires, so the joint is the marginal lifted, and the two cannot
                    // drift apart: adding a class to the marginal adds it here.
                    foreach (string cls in RequiredContentClasses(owner, member.Name))
                    {
                        if (!joint.Has(key, cls, MinimumLargestCollection))
                        {
                            unvaried.Add(
                                $"{key} (string) is never {cls} inside a collection of at least "
                                + $"{MinimumLargestCollection} -- the marginals are covered, the "
                                + "joint cell is not");
                        }
                    }
                }
            }
        }

        Assert.True(
            unvaried.Count == 0,
            "These degrees of freedom of the schema model are never varied by anything this file "
            + "writes to a footer, so a serializer that mishandles them diverges undetected:"
            + Environment.NewLine + string.Join(Environment.NewLine, unvaried.Select(u => "  - " + u)));

        static string Describe(HashSet<object> values) =>
            values.Count == 0 ? "unobserved" : string.Join("/", values.Select(v => v?.ToString() ?? "null"));
    }

    /// <summary>
    /// The degrees of freedom, read off the schema types. Reference-typed members are excluded
    /// BY THEIR TYPE rather than by name: they are other model objects or views over them, and are
    /// covered by their own owner's entry.
    /// </summary>
    private static IEnumerable<(Type Owner, System.Reflection.MemberInfo Member, bool Elements)> SchemaDegreesOfFreedom()
    {
        Type[] owners =
        [
            typeof(StructType), typeof(StructField), typeof(FieldMetadata), typeof(MetadataValue),
        ];

        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly;

        foreach (Type owner in owners)
        {
            foreach (System.Reflection.PropertyInfo property in owner.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                // A scalar member is an axis only if it is CALLER-SUPPLIED, decided by matching a
                // constructor parameter. That replaces a hand-written exclusion of
                // StructType.TypeName with a rule: TypeName and SimpleString are RENDERINGS of the
                // type, not inputs to it, so demanding an empty SimpleString would be demanding
                // something no caller can ask for. Collections are exempt from the rule, because a
                // collection member always exposes real contained state however it is surfaced --
                // FieldMetadata.Keys is not a constructor parameter but its size is still an axis.
                if (IsCollectionAxis(property.PropertyType))
                {
                    yield return (owner, property, false);
                    if (IsScalarAxis(ElementType(property.PropertyType)))
                    {
                        // A collection of scalars has TWO degrees of freedom: how many it holds and
                        // what they contain. FieldMetadata.Keys is the case that matters -- metadata
                        // keys are strings that reach the serializer, but they are elements rather
                        // than members, so a member-only walk asked for their COUNT to be varied and
                        // never for their CONTENT. That is how the empty key stayed unreachable
                        // while the guard reported the model fully covered.
                        yield return (owner, property, true);
                    }
                }
                else if (IsScalarAxis(property.PropertyType)
                    && IsCallerSupplied(owner, property.PropertyType))
                {
                    yield return (owner, property, false);
                }
            }

            // Parameterless METHODS returning a collection are degrees of freedom too. This is not
            // pedantry: MetadataValue exposes its array only through AsArray(), so a
            // property-only walk never asks for array-element cardinality to be varied -- the
            // sweep happened to vary it, but nothing REQUIRED it, which is the difference between
            // coverage and accident that this file has already been caught by once.
            foreach (System.Reflection.MethodInfo method in owner.GetMethods(flags))
            {
                if (method.IsSpecialName
                    || method.GetParameters().Length != 0
                    || method.Name == nameof(IEnumerable<int>.GetEnumerator)
                    || method.GetBaseDefinition().DeclaringType == typeof(object))
                {
                    continue;
                }

                if (IsCollectionAxis(method.ReturnType))
                {
                    yield return (owner, method, false);
                    if (IsScalarAxis(ElementType(method.ReturnType)))
                    {
                        yield return (owner, method, true);
                    }
                }
                else if (IsScalarAxis(method.ReturnType) && IsCallerSupplied(owner, method.ReturnType))
                {
                    // MetadataValue.AsString() is the string that a metadata value CONTAINS. Like
                    // AsArray() before it, it is exposed only as a method, so the property walk
                    // never saw it -- and it is the position an empty-string-dropping normalisation
                    // attacks. Object's own overrides are excluded by asking where the method was
                    // first declared, not by naming them.
                    yield return (owner, method, false);
                }
            }
        }
    }

    private static Type MemberValueType(System.Reflection.MemberInfo member) =>
        member is System.Reflection.PropertyInfo p
            ? p.PropertyType
            : ((System.Reflection.MethodInfo)member).ReturnType;

    /// <summary>
    /// Whether the empty string is a CONSTRUCTIBLE value for this member, decided by trying it.
    /// </summary>
    /// <remarks>
    /// <c>StructField</c> rejects an empty name outright, so demanding an empty field name would be
    /// demanding a value the model forbids -- a fact of the type, discovered by asking it, not a
    /// judgement to hand-list. Metadata keys, string values and array elements all accept empty,
    /// so they keep the requirement. If the probe cannot decide, it FAILS CLOSED and the
    /// requirement stands: an undecidable case should surface, not disappear.
    /// </remarks>
    private static bool EmptyStringIsConstructible(Type owner, string memberName)
    {
        foreach (System.Reflection.ConstructorInfo ctor in owner.GetConstructors(
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance))
        {
            System.Reflection.ParameterInfo[] parameters = ctor.GetParameters();
            int index = Array.FindIndex(
                parameters,
                x => x.ParameterType == typeof(string)
                    && string.Equals(x.Name, memberName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                continue;
            }

            var arguments = new object?[parameters.Length];
            bool buildable = true;
            for (int i = 0; i < parameters.Length && buildable; i++)
            {
                if (i == index)
                {
                    arguments[i] = string.Empty;
                }
                else if (!TryDefaultArgument(parameters[i].ParameterType, out arguments[i]))
                {
                    buildable = false;
                }
            }

            if (!buildable)
            {
                continue;
            }

            try
            {
                ctor.Invoke(arguments);
                return true;
            }
            catch (System.Reflection.TargetInvocationException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDefaultArgument(Type type, out object? value)
    {
        value = type switch
        {
            _ when type == typeof(string) => "x",
            _ when type == typeof(bool) => true,
            _ when type == typeof(DataType) => DataTypes.LongType,
            _ when type == typeof(FieldMetadata) => FieldMetadata.Empty,
            _ when type.IsValueType => Activator.CreateInstance(type),
            _ => null,
        };

        return value is not null;
    }

    /// <summary>
    /// <summary>
    /// Whether a caller can SUPPLY a value of this type to this owner, decided by looking for a
    /// parameter of that type on any constructor or any static factory returning the owner.
    /// </summary>
    /// <remarks>
    /// Matching on TYPE rather than on parameter NAME is what makes this work for factory-built
    /// types: MetadataValue.AsString() reads back what MetadataValue.String(string) was given, and
    /// no name matches across that pair. StructType stays excluded on the same rule for the same
    /// reason as before -- it is constructed from fields alone, so no caller can hand it a
    /// SimpleString or a TypeName, and demanding an empty one would demand the unaskable.
    /// </remarks>
    private static bool IsCallerSupplied(Type owner, Type valueType)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static;

        foreach (System.Reflection.MethodBase candidate in
            owner.GetConstructors(flags).Cast<System.Reflection.MethodBase>()
                .Concat(owner.GetMethods(flags).Where(x => x.IsStatic && x.ReturnType == owner)))
        {
            if (candidate.GetParameters().Any(x => x.ParameterType == valueType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The element type of a collection axis, or the collection type itself when it is not generic.
    /// </summary>
    private static Type ElementType(Type collection)
    {
        Type? enumerable = collection.IsGenericType
            && collection.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? collection
                : collection.GetInterfaces().FirstOrDefault(
                    x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumerable?.GetGenericArguments()[0] ?? collection;
    }

    /// <summary>
    /// The scalar kinds this guard knows how to state a boundary for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This set and the <c>owners</c> array in <see cref="SchemaDegreesOfFreedom"/> are the two
    /// HAND-LISTS remaining inside a guard whose purpose is to remove hand-lists, and they are
    /// named here rather than left to be discovered. Neither has a live gap today: every scalar
    /// member of the schema model is an enum, a bool, an int or a string, and the model is the four
    /// types listed.
    /// </para>
    /// <para>
    /// What happens when a new scalar kind is added is the part worth stating plainly: a member of
    /// an unlisted kind is silently NOT an axis, so it would be unrequired without any test saying
    /// so -- the same failure shape this file has been caught by repeatedly. A long or a decimal
    /// added to the model is the realistic case. The mitigation available today is that such a
    /// member still has to be serialized, so the corpus-completeness and byte-parity layers would
    /// still see it; what would be lost is the REQUIREMENT that it be varied at a boundary.
    /// </para>
    /// <para>
    /// The same reservation applies to <c>owners</c>, and it has a NAMED consequence rather than a
    /// hypothetical one: the list omits <c>DataType</c>, so the walk never descends through
    /// <c>StructField.DataType</c> and <c>DecimalType.Precision</c>/<c>Scale</c> are required by
    /// nothing. They are exercised by the writable-type corpus, which is byte-parity rather than a
    /// requirement -- accidental coverage, which this file has been caught trusting before.
    /// Tracked as issue #729, and filed rather than fixed here because the obvious repair states
    /// an IMPOSSIBLE requirement: the int arm below is written for collection sizes and demands a
    /// maximum of 257, while <c>DecimalType</c> rejects any precision above 38. The repair needs
    /// the arm split, with scalar ints rooted in the model's own declared bounds.
    /// </para>
    /// </remarks>
    private static bool IsScalarAxis(Type t)
    {
        Type u = Nullable.GetUnderlyingType(t) ?? t;
        return u.IsEnum || u == typeof(bool) || u == typeof(int) || u == typeof(string);
    }

    private static bool IsCollectionAxis(Type t) =>
        t != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t);

    private static void Observe(object target, Dictionary<string, HashSet<object>> into)
    {
        foreach ((Type owner, System.Reflection.MemberInfo member, bool elements) in SchemaDegreesOfFreedom())
        {
            if (!owner.IsInstanceOfType(target))
            {
                continue;
            }

            object? value;
            try
            {
                value = member is System.Reflection.PropertyInfo p
                    ? p.GetValue(target)
                    : ((System.Reflection.MethodInfo)member).Invoke(target, null);
            }
            catch (System.Reflection.TargetInvocationException)
            {
                // A kind-specific accessor on the wrong kind (AsArray on a Long). Not an
                // observation, and not an error -- the axis is observed from the values that do
                // have it.
                continue;
            }

            if (value is null)
            {
                continue;
            }

            string key = elements ? $"{owner.Name}.{member.Name}[]" : $"{owner.Name}.{member.Name}";
            if (!into.TryGetValue(key, out HashSet<object>? set))
            {
                set = new HashSet<object>();
                into[key] = set;
            }

            if (elements)
            {
                // The CONTENT axis of a scalar collection: every element is an observation.
                foreach (object item in ((System.Collections.IEnumerable)value).Cast<object>())
                {
                    set.Add(item);
                }

                continue;
            }

            // For a collection the observation is its SIZE, which is what makes cardinality a
            // derived axis rather than a hand-listed one.
            set.Add(value is System.Collections.IEnumerable sequence and not string
                ? sequence.Cast<object>().Count()
                : value);
        }
    }

    private static void ObserveType(
        StructType schema, Dictionary<string, HashSet<object>> into, JointCells joint)
    {
        Observe(schema, into);
        int width = schema.Count;
        foreach (StructField field in schema)
        {
            Observe(field, into);

            // The JOINT cell: this name, and how many names sit beside it. A marginal record of
            // "some name was non-ASCII" and "some schema was 257 wide" leaves the cell where a
            // WIDE schema carries a non-ASCII name required by neither.
            joint.Add($"{nameof(StructField)}.{nameof(StructField.Name)}", width, field.Name);
            ObserveMetadata(field.Metadata, into, joint);
        }
    }

    private static void ObserveMetadata(
        FieldMetadata metadata, Dictionary<string, HashSet<object>> into, JointCells joint)
    {
        Observe(metadata, into);
        int size = metadata.Count();
        foreach (KeyValuePair<string, MetadataValue> entry in metadata)
        {
            joint.Add($"{nameof(FieldMetadata)}.{nameof(FieldMetadata.Keys)}[]", size, entry.Key);
            ObserveValue(entry.Value, into, joint, size);
        }
    }

    private static void ObserveValue(
        MetadataValue value, Dictionary<string, HashSet<object>> into, JointCells joint, int size)
    {
        Observe(value, into);
        switch (value.Kind)
        {
            case MetadataValueKind.String:
                joint.Add($"{nameof(MetadataValue)}.{nameof(MetadataValue.AsString)}", size, value.AsString());
                break;
            case MetadataValueKind.Array:
                IReadOnlyList<MetadataValue> items = value.AsArray();
                foreach (MetadataValue item in items)
                {
                    ObserveValue(item, into, joint, items.Count);
                }

                break;
            case MetadataValueKind.Nested:
                ObserveMetadata(value.AsNested(), into, joint);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Joint (collection size x content class) cells for the string axes.
    /// </summary>
    /// <remarks>
    /// The requirement here is not a new hand-list of interacting pairs. It is the marginal
    /// requirement LIFTED: whatever a content axis is separately required to hold, a
    /// boundary-sized collection must be observed holding it too. That is derived from the
    /// requirements already stated, so it cannot drift away from them, and it closes the cell a
    /// bulk fast path lives in -- "identity until the collection is large, then drop the
    /// non-ASCII entries" is invisible to a guard that checks size and content separately.
    /// </remarks>
    private sealed class JointCells
    {
        private readonly Dictionary<string, HashSet<(int Size, string Class)>> _cells =
            new(StringComparer.Ordinal);

        public void Add(string axis, int size, string text)
        {
            if (!_cells.TryGetValue(axis, out HashSet<(int, string)>? set))
            {
                set = new HashSet<(int, string)>();
                _cells[axis] = set;
            }

            foreach (string cls in ContentClasses(text))
            {
                set.Add((size, cls));
            }
        }

        public bool Has(string axis, string cls, int minimumSize) =>
            _cells.TryGetValue(axis, out HashSet<(int Size, string Class)>? set)
            && set.Any(x => x.Size >= minimumSize && string.Equals(x.Class, cls, StringComparison.Ordinal));

        /// <summary>
        /// The boundary classes a string belongs to -- the same boundaries the marginal string arm
        /// requires, so the joint requirement is stated in the marginal's own vocabulary.
        /// </summary>
        public static IEnumerable<string> ContentClasses(string text)
        {
            if (text.Length == 0)
            {
                yield return EmptyClass;
            }

            if (text.Length >= MinimumLongestString)
            {
                yield return LongClass;
            }

            if (text.Any(c => c > '\u007F'))
            {
                yield return NonAsciiClass;
            }
        }
    }

    /// <summary>
    /// The content classes this axis must show INSIDE a boundary-sized collection: exactly those
    /// the marginal arm requires of it, minus any the model forbids.
    /// </summary>
    private static IEnumerable<string> RequiredContentClasses(Type owner, string memberName)
    {
        if (EmptyStringIsConstructible(owner, memberName))
        {
            yield return EmptyClass;
        }

        yield return LongClass;
        yield return NonAsciiClass;
    }

    private const string EmptyClass = "EMPTY";
    private const string LongClass = "LONG";
    private const string NonAsciiClass = "NON-ASCII";

    /// <summary>
    /// The single enumeration of every schema the constructive layer writes to a Parquet footer.
    /// </summary>
    /// <remarks>
    /// The sweep WRITES these and the degrees-of-freedom guard OBSERVES these, from one source.
    /// Before, the guard walked a parallel reconstruction that merely resembled the sweep, and
    /// nothing checked the resemblance -- so the guard could have reported an axis covered on the
    /// strength of a schema no footer ever saw. That is the unsafe direction of an unchecked
    /// structural claim, and this file has already been caught believing one. Sharing the source
    /// makes "observed implies written" true by construction rather than by assertion.
    /// </remarks>
    private static IEnumerable<(string What, StructType Schema)> ConstructiveArtifacts(
        HashSet<(string, int, string)> covered)
    {
        IReadOnlyList<string> codepoints = HazardCodepoints;

        // NOT A COVERAGE BOUND. This only decides how many codepoints share one write, and it was
        // read as a coverage bound once -- for a while it was the ONLY thing setting the artifact
        // layer's schema width, so a writer with a 64-column buffer passed everything. Schema
        // width is now an explicit axis (CardinalityCounts, up to 257 fields) and is REQUIRED by
        // SchemaDegreesOfFreedom_AreEachVaried, so changing this number cannot narrow coverage.
        const int chunkSize = 48;
        for (int start = 0; start < codepoints.Count; start += chunkSize)
        {
            string[] chunk = codepoints.Skip(start).Take(chunkSize).ToArray();
            yield return (
                $"the systematic sweep, chunk starting at codepoint index {start}",
                BuildSweepSchema(chunk, start, covered));
        }

        // Magnitude, same construction: one long string per position per length.
        foreach (int length in MagnitudeLengths)
        {
            yield return (
                $"string length {length} -- a fixed-buffer truncation",
                BuildSweepSchema([new string('m', length)], -length, covered));
        }

        // CARDINALITY. Every axis before this one varies what a SINGLE ITEM contains; none varied
        // how many items there are. A serializer with a fixed inline buffer for metadata entries
        // needs no hostile character and no long string to diverge -- and because keys are emitted
        // in ordinal order, enough tenant-supplied keys evict the protocol's own delta.* keys from
        // the footer while the log keeps them.
        foreach ((string what, StructType schema) in CardinalitySweepSchemas())
        {
            yield return ($"{what} -- a fixed-capacity collection buffer", schema);
        }

        // NUMERIC BOUNDARIES, enumerated rather than sampled. Every reflected double and long is
        // written at every depth. -0.0 is here because it is derived from the sign bit rather than
        // from a value comparison; no equality-based enumeration can produce it, since -0.0 == 0.0.
        yield return ("a reflected numeric boundary value", BuildNumericSweepSchema());
    }

    /// <summary>
    /// The schemas this guard observes: the constructive artifacts, taken from the very
    /// enumeration the sweep writes, plus the three fixed corpora that their own tests write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This deliberately does NOT claim to be every schema the file writes. It used to, and the
    /// claim was false -- a prose totality claim of the same family as the completeness table
    /// already deleted from this PR, and a test that never runs. What it claims now is the
    /// direction that matters for soundness: everything observed here IS written to a real footer,
    /// so the guard cannot report an axis covered on the strength of an artifact that does not
    /// exist. The converse is safe to leave open, because observing FEWER schemas can only make
    /// the guard demand more, never less.
    /// </para>
    /// <para>
    /// The 1200 generated schemas are excluded on purpose. A coverage requirement satisfied by a
    /// random draw is that requirement plus a sampling assumption, which is the reasoning that
    /// made the finite axes constructive in the first place; the guard must not come to depend on
    /// a seed.
    /// </para>
    /// </remarks>
    private static IEnumerable<StructType> ArtifactSchemas()
    {
        yield return MetadataCorpusSchema;
        yield return NameCorpusSchema;
        yield return ScalarCorpusSchema;
        var covered = new HashSet<(string, int, string)>();
        foreach ((string _, StructType schema) in ConstructiveArtifacts(covered))
        {
            yield return schema;
        }
    }


    /// <summary>
    /// A schema whose serialization depends on ambient culture if anything in the writer is
    /// culture-sensitive: fractional and negative doubles, exponent forms, negative longs, and
    /// keys whose ORDINAL order differs from every linguistic collation.
    /// </summary>
    /// <remarks>
    /// Built PER CALL, not once into a static. A static would be constructed under whichever
    /// culture happened to be current at type-initialisation, and any culture sensitivity in
    /// CONSTRUCTION -- metadata keys are held in a sorted dictionary, so the comparer decides the
    /// footer's key order -- would then be frozen before the first culture is ever set. Measured:
    /// with a static, a rogue swapping the key comparer to the current culture scored 0 kills; the
    /// hazard was invisible because the schema had already been sorted.
    /// </remarks>
    private static StructType BuildCultureHazardSchema() => new(
    [
        new StructField(
            "ratio", DataTypes.DoubleType, nullable: true,
            FieldMetadata.FromValues(
            [
                // Number formatting: the decimal separator, the negative sign, the exponent form
                // and the digit shapes are all culture-dependent, and a footer carrying a comma
                // separator is not merely wrong, it is INVALID JSON -- the log's schemaString with
                // it is unreadable by every Delta reader, ours and everyone else's.
                new KeyValuePair<string, MetadataValue>("half", MetadataValue.Double(0.5)),
                new KeyValuePair<string, MetadataValue>("negHalf", MetadataValue.Double(-0.5)),
                new KeyValuePair<string, MetadataValue>("tiny", MetadataValue.Double(1E-300)),
                new KeyValuePair<string, MetadataValue>("huge", MetadataValue.Double(double.MaxValue)),
                new KeyValuePair<string, MetadataValue>("least", MetadataValue.Double(double.MinValue)),
                new KeyValuePair<string, MetadataValue>("eps", MetadataValue.Double(double.Epsilon)),
                new KeyValuePair<string, MetadataValue>("negZero", MetadataValue.Double(-0.0)),
                new KeyValuePair<string, MetadataValue>("negLong", MetadataValue.Long(long.MinValue)),

                // Collation: these four sort one way ORDINALLY and another way under essentially
                // every linguistic comparer, so a culture-sensitive sort reorders the footer.
                new KeyValuePair<string, MetadataValue>("_leading", MetadataValue.String("a")),
                new KeyValuePair<string, MetadataValue>("Zebra", MetadataValue.String("b")),
                new KeyValuePair<string, MetadataValue>("apple", MetadataValue.String("c")),

                // Casing: Turkish maps i/I outside the ASCII pair, so any ToUpper/ToLower on a key
                // shows up here and nowhere else.
                new KeyValuePair<string, MetadataValue>("id", MetadataValue.String("I")),
                new KeyValuePair<string, MetadataValue>("ID", MetadataValue.String("i")),
            ])),
    ]);

    /// <summary>
    /// One representative culture per distinct FORMATTING BEHAVIOUR, derived from the runtime's
    /// own culture table rather than chosen.
    /// </summary>
    /// <remarks>
    /// Naming three cultures would be a hand-list, and a hand-list is what this file keeps being
    /// caught by. The signature below is the behaviour the serializer could actually depend on --
    /// number formatting, collation, casing -- so the set covers every DISTINCT behaviour the
    /// runtime knows about, and grows by itself when the runtime's table does.
    /// </remarks>
    private static IReadOnlyList<System.Globalization.CultureInfo> FormattingBehaviourCultures()
    {
        var representatives =
            new Dictionary<string, System.Globalization.CultureInfo>(StringComparer.Ordinal);
        foreach (System.Globalization.CultureInfo culture in
            System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.AllCultures))
        {
            representatives.TryAdd(FormattingSignature(culture), culture);
        }

        return representatives.Values.ToArray();
    }

    private static string FormattingSignature(System.Globalization.CultureInfo culture)
    {
        System.Globalization.NumberFormatInfo n = culture.NumberFormat;
        return string.Join(
            '\u0001',
            n.NumberDecimalSeparator,
            n.NumberGroupSeparator,
            n.NegativeSign,
            n.PositiveSign,
            string.Concat(n.NativeDigits),
            n.DigitSubstitution.ToString(),
            Math.Sign(string.Compare("a", "B", culture, System.Globalization.CompareOptions.None))
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            "i".ToUpper(culture),
            "I".ToLower(culture));
    }

    /// <summary>
    /// The serializer's output must not depend on the AMBIENT CULTURE of the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A category that every other guard in this file is blind to BY CONSTRUCTION. The
    /// degrees-of-freedom guard defines an axis as a member of the schema model, and ambient
    /// process state is not a member of the schema model, so nothing derived from it can see this.
    /// Eighteen rogues varied the input or replaced the serializer; this one varies the
    /// ENVIRONMENT the serializer runs in, and the shipped code passes only because the machine
    /// running it happens to use a dot.
    /// </para>
    /// <para>
    /// The consequence is not a wrong string, it is an INVALID one. A comma decimal separator
    /// makes both the footer and the log's schemaString structurally invalid JSON, so every table
    /// written on such a host is unreadable by every Delta reader, including ours -- silently, and
    /// for tables already written. That is why this is pinned rather than deferred.
    /// </para>
    /// </remarks>
    [Fact]
    public void SharedSerializer_IsIndependentOfAmbientCulture()
    {
        System.Globalization.CultureInfo original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.InvariantCulture;
            string baseline = SchemaJson.ToJson(BuildCultureHazardSchema());

            foreach (System.Globalization.CultureInfo culture in FormattingBehaviourCultures())
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
                string actual = SchemaJson.ToJson(BuildCultureHazardSchema());
                if (!string.Equals(baseline, actual, StringComparison.Ordinal))
                {
                    Assert.Fail(
                        $"The shared serializer's output depends on the ambient culture "
                        + $"'{culture.Name}'."
                        + $"{Environment.NewLine}  invariant: {Truncate(baseline)}"
                        + $"{Environment.NewLine}  {culture.Name,-12}: {Truncate(actual)}");
                }

                // Byte-equality alone would still pass if BOTH sides were broken the same way, so
                // the output is also required to remain READABLE.
                SchemaJson.FromJson(actual);
            }
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// The ARTIFACT under a hostile culture: a real footer, read back, compared to the log.
    /// </summary>
    /// <remarks>
    /// The test above pins the shared serializer in isolation. This one pins the whole write path,
    /// because culture reaches further than one method -- anything between the schema and the
    /// bytes on disk could format a number, and only a real footer proves it did not.
    /// </remarks>
    [Fact]
    public async Task WrittenFooter_IsIndependentOfAmbientCulture()
    {
        System.Globalization.CultureInfo original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // Pin to InvariantCulture before building the schema and computing the expected JSON,
            // so the anchor is genuinely culture-neutral rather than dependent on the ambient
            // process culture. This makes the oracle sensitive to a mutation that shifts both
            // sides identically inside the loop — the expected value is anchored here, outside
            // any rogue culture, and under a known-good culture.
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            StructType schema = BuildCultureHazardSchema();
            string expected = SchemaJson.ToJson(schema);

            foreach (System.Globalization.CultureInfo culture in FormattingBehaviourCultures())
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
                string footer = await WriteAndReadFooterSchemaAsync(schema);
                if (!string.Equals(expected, footer, StringComparison.Ordinal))
                {
                    Assert.Fail(
                        $"The written footer depends on the ambient culture '{culture.Name}'."
                        + $"{Environment.NewLine}  expected (log): {Truncate(expected)}"
                        + $"{Environment.NewLine}  actual (footer): {Truncate(footer)}");
                }

                SchemaJson.FromJson(footer);
            }
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Meta-guard: the derived culture set must actually CONTAIN the hazards it exists to probe.
    /// </summary>
    /// <remarks>
    /// Under globalization-invariant mode the runtime's culture table collapses to a single
    /// entry, and the culture test above would pass while probing nothing -- a guard that silently
    /// becomes a no-op is the failure this file has met more than once. This names the condition
    /// instead, and it is rooted in the culture data rather than in the list under test.
    /// </remarks>
    [Fact]
    public void FormattingBehaviourCultures_ContainTheHazardsTheyProbe()
    {
        IReadOnlyList<System.Globalization.CultureInfo> cultures = FormattingBehaviourCultures();

        Assert.True(
            cultures.Any(x => x.NumberFormat.NumberDecimalSeparator != "."),
            "No culture uses a non-dot decimal separator, so the culture guard probes nothing. "
            + "This is what globalization-invariant mode looks like from inside the test.");
        Assert.True(
            cultures.Any(x => x.NumberFormat.NegativeSign != "-"),
            "No culture uses a non-ASCII negative sign.");
        Assert.True(
            cultures.Any(x => string.Compare("a", "B", x, System.Globalization.CompareOptions.None) < 0),
            "No culture collates 'a' before 'B', so a culture-sensitive sort would be invisible.");

        // BuildCultureHazardSchema carries 'id'/'ID' keys specifically to probe dotted/dotless-I
        // casing, which is the hazard a culture-sensitive ToUpper/ToLower on a metadata key would
        // trip. That probe was passing INCIDENTALLY: the three requirements above are all satisfied
        // without any Turkic culture present, so the casing coverage rested on the derivation
        // happening to pick one rather than on anything requiring it. Same shape as the astral
        // case -- a probe that exists but is required by nothing.
        Assert.True(
            cultures.Any(x => !string.Equals("i".ToUpper(x), "I", StringComparison.Ordinal)),
            "No culture maps 'i' to something other than 'I', so the dotted/dotless-I keys in "
            + "BuildCultureHazardSchema probe nothing and a culture-sensitive casing of metadata "
            + "keys would be invisible.");
    }

    /// <summary>
    /// Meta-guard: the hazard set must represent EVERY Unicode category that has members.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the first version of the sweep reproduced, in new code, the exact defect
    /// it was written to fix. <see cref="HazardCodepoints"/> feeds both the sweep and the
    /// generator's hazard stratum, so stripping its sampling loop narrowed the requirement and the
    /// satisfier TOGETHER: a rogue emitting U+2028 raw then survived all three layers, and the
    /// domain audit stayed green because its character-class assertions are all satisfiable from
    /// the first 256 codepoints. That is a shared source between a requirement and the thing that
    /// meets it -- measured, not theorised (verify25 M3).
    /// </para>
    /// <para>
    /// The requirement here is therefore rooted in a DIFFERENT source: the
    /// <see cref="System.Globalization.UnicodeCategory"/> enumeration, not the loop that samples
    /// it. If the sampling loop is narrowed, this fails; if a future runtime adds a category, this
    /// fails until the set covers it. The two can only agree by both being right.
    /// </para>
    /// </remarks>
    [Fact]
    public void HazardCodepoints_RepresentEveryUnicodeCategory()
    {
        var represented = new HashSet<System.Globalization.UnicodeCategory>();
        foreach (string text in HazardCodepoints)
        {
            represented.Add(System.Globalization.CharUnicodeInfo.GetUnicodeCategory(
                char.ConvertToUtf32(text, 0)));
        }

        // Surrogate is deliberately absent: lone surrogates are #710, a known-open defect this
        // file is not about, and a surrogate codepoint cannot be expressed as a string here anyway.
        var expected = Enum.GetValues<System.Globalization.UnicodeCategory>()
            .Where(c => c != System.Globalization.UnicodeCategory.Surrogate)
            .ToArray();

        string[] missing = expected
            .Where(c => !represented.Contains(c))
            .Select(c => c.ToString())
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "The hazard codepoint set does not represent every Unicode category, so characters in "
            + "the missing ones are unreachable by both the sweep and the generator: "
            + string.Join(", ", missing));
    }

    /// <summary>
    /// SYSTEMATIC artifact sweep: every probed codepoint, in every string position, at every depth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the constructive half of the artifact layer, and it exists because of a measured
    /// distinction. Four independent reviews found the same defect in four projections -- a shared
    /// loop bound, a marginal instead of a joint cell, a form-keyed alphabet, and a missing
    /// magnitude axis -- and in each case the meta-guard could not see the gap BECAUSE IT USED THE
    /// SAME PROJECTION as the thing it was auditing.
    /// </para>
    /// <para>
    /// The response is not another audit. An audit that checks whether a random draw happened to
    /// cover an enumeration is strictly worse than executing the enumeration: it is the same
    /// enumeration plus a sampling assumption. So for the axes that are finite and enumerable --
    /// codepoint, position, depth, magnitude -- this test ENUMERATES AND EMITS rather than sampling
    /// and auditing. Coverage is true by construction, not by assertion.
    /// </para>
    /// <para>
    /// Random generation is retained in the sibling test for what this cannot do: COMPOSITIONS.
    /// The two are complementary -- this one is complete on single axes and blind to interactions;
    /// the generator reaches interactions and is complete on nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SystematicSweep_PinsEveryProbedCodepointInEveryPositionAndDepth()
    {
        IReadOnlyList<string> codepoints = HazardCodepoints;
        Assert.True(
            codepoints.Count >= 0x100,
            $"The hazard codepoint probe collapsed to {codepoints.Count} entries.");

        var covered = new HashSet<(string, int, string)>();

        // ONE source, shared with the degrees-of-freedom guard: everything that guard observes is
        // written HERE, so its coverage report cannot outrun the artifacts.
        foreach ((string what, StructType schema) in ConstructiveArtifacts(covered))
        {
            string expected = SchemaJson.ToJson(schema);
            string footer = await WriteAndReadFooterSchemaAsync(schema);
            if (!string.Equals(expected, footer, StringComparison.Ordinal))
            {
                Assert.Fail(
                    $"Footer diverged from the shared serializer at {what}."
                    + $"{Environment.NewLine}  expected (log, {expected.Length} chars): "
                    + $"{Truncate(expected)}"
                    + $"{Environment.NewLine}  actual (footer, {footer.Length} chars): "
                    + $"{Truncate(footer)}");
            }
        }

        (string Position, int Depth)[] cells = RequiredStringCells(SweepDepthBound).ToArray();
        foreach (string text in codepoints)
        {
            foreach ((string position, int depth) in cells)
            {
                Assert.Contains((position, depth, text), covered);
            }
        }
    }

    /// <summary>
    /// Builds a schema placing each of <paramref name="texts"/> in EVERY arbitrary-string position
    /// at every depth the grammar admits, recording the cells it filled.
    /// </summary>
    private static StructType BuildSweepSchema(
        IReadOnlyList<string> texts, int salt, HashSet<(string, int, string)> covered)
    {
        var fields = new List<StructField>(texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            string t = texts[i];
            string tag = $"{salt}_{i}";
            covered.Add(("field name", 0, t));
            for (int depth = 0; depth <= SweepDepthBound; depth++)
            {
                covered.Add(("metadata key", depth, t));
                covered.Add(("metadata string value", depth, t));
                if (depth >= 1)
                {
                    covered.Add(("array element string", depth, t));
                }
            }

            fields.Add(new StructField(
                "f" + tag + t,
                DataTypes.LongType,
                nullable: true,
                FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>("k" + t, MetadataValue.String("v" + t)),

                    // The EMPTY STRING, in every position that accepts one. It is the zero of the
                    // length axis and was unreachable by construction: every swept string carried
                    // a literal prefix, MagnitudeLengths started at 1 while CardinalityCounts
                    // started at 0, and the generator drew at least one character. A "skip blanks"
                    // normalisation therefore dropped entries with no test noticing.
                    new KeyValuePair<string, MetadataValue>(string.Empty, MetadataValue.String("kept-under-empty-key")),
                    new KeyValuePair<string, MetadataValue>("blank", MetadataValue.String(string.Empty)),
                    new KeyValuePair<string, MetadataValue>("blanks", MetadataValue.Array(
                        [MetadataValue.String(string.Empty), MetadataValue.String("after")])),
                    new KeyValuePair<string, MetadataValue>("blanknest", MetadataValue.Nested(
                        FieldMetadata.FromValues(
                        [
                            new KeyValuePair<string, MetadataValue>(string.Empty, MetadataValue.String(string.Empty)),
                        ]))),
                    new KeyValuePair<string, MetadataValue>("obj", NestMetadata(t, 1)),
                    new KeyValuePair<string, MetadataValue>("arr", MetadataValue.Array(new[]
                    {
                        MetadataValue.String("a" + t),
                    })),
                })));
        }

        return new StructType(fields);
    }

    /// <summary>
    /// A schema carrying every reflected numeric boundary, at every depth, as a metadata value.
    /// </summary>
    private static StructType BuildNumericSweepSchema()
    {
        var entries = new List<KeyValuePair<string, MetadataValue>>();
        for (int depth = 0; depth <= SweepDepthBound; depth++)
        {
            var level = new List<MetadataValue>();
            foreach (double d in DoubleBoundaries)
            {
                level.Add(MetadataValue.Double(d));
            }

            foreach (long l in LongBoundaries)
            {
                level.Add(MetadataValue.Long(l));
            }

            entries.Add(new KeyValuePair<string, MetadataValue>(
                "d" + depth, MetadataValue.Array(level.ToArray())));
        }

        MetadataValue nested = MetadataValue.Nested(FieldMetadata.FromValues(entries.ToArray()));
        return new StructType(
        [
            new StructField(
                "numeric",
                DataTypes.LongType,
                nullable: true,
                FieldMetadata.FromValues(
                    entries.Append(new KeyValuePair<string, MetadataValue>("obj", nested)).ToArray())),
        ]);
    }

    /// <summary>
    /// The deepest nesting the systematic sweep constructs: the deepest the READER accepts,
    /// MEASURED by asking it, not copied from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be <c>= GeneratedMetadataDepthBound</c>, which made one literal cap the sweep,
    /// the generator AND the requirement that audits them both. That is the shared-source defect
    /// this file already fixed at the codepoint axis, surviving one level up at the depth axis: a
    /// cell-set parameterised by the bound it is supposed to check can never name a missing depth.
    /// A writer with a fixed 3-slot depth stack, emitting an empty container instead of recursing,
    /// passed all three layers.
    /// </para>
    /// <para>
    /// Metadata nesting is unbounded in the grammar, so there is no natural literal. The bound that
    /// matters in production is the one past which the footer stops ROUND-TRIPPING, and that is a
    /// property of <c>SchemaJson.FromJson</c> -- so it is probed from the reader's actual
    /// behaviour. If the reader's limit changes, this follows without anyone editing a test.
    /// </para>
    /// </remarks>
    private static int SweepDepthBound => ProbedMaxMetadataDepth;

    /// <summary>
    /// The deepest metadata nesting that survives a <c>ToJson</c> -&gt; <c>FromJson</c> -&gt;
    /// <c>ToJson</c> round trip, discovered by binary-free linear probe rather than declared.
    /// </summary>
    private static int ProbedMaxMetadataDepth => _probedMaxDepth ??= ProbeMaxMetadataDepth();

    private static int? _probedMaxDepth;

    /// <summary>
    /// A ceiling on the PROBE, not on the format. It is not allowed to be the binding constraint --
    /// <see cref="SweepDepthBound_IsTheReadersOwnLimit"/> fails if the probe ever reaches it, which
    /// is what keeps this from becoming another hand-chosen bound hiding behind a derivation.
    /// </summary>
    private const int DepthProbeCeiling = 128;

    private static int ProbeMaxMetadataDepth()
    {
        int deepest = 0;
        for (int depth = 1; depth <= DepthProbeCeiling; depth++)
        {
            try
            {
                // #711: the write side now enforces the SAME depth bound as the read side, so ToJson itself
                // fails closed once the schema would serialize past what FromJson can re-read. That is the
                // symmetric limit this probe measures, so the FIRST ToJson is inside the try too — a
                // write-side rejection means "this depth does not round-trip" exactly like a read-side one.
                string json = SchemaJson.ToJson(BuildDepthProbeSchema(depth));
                if (!string.Equals(SchemaJson.ToJson(SchemaJson.FromJson(json)), json, StringComparison.Ordinal))
                {
                    break;
                }
            }
            catch (Exception)
            {
                break;
            }

            deepest = depth;
        }

        return deepest;
    }

    /// <remarks>
    /// The leaf is an ARRAY, not a bare string, because that is what the sweep actually emits at
    /// its deepest level -- <see cref="NestMetadata"/> puts an array beside the recursion at every
    /// depth, and an array element costs one more JSON level than a string does. Probing the
    /// cheaper shape and applying the answer to the more expensive one is a bound measured in one
    /// place and used in another, which is the defect this probe exists to remove; it was found by
    /// the output-side check, which parsed a swept footer and hit the reader's ceiling.
    /// </remarks>
    private static StructType BuildDepthProbeSchema(int depth)
    {
        MetadataValue value = MetadataValue.Array([MetadataValue.String("leaf")]);
        for (int i = 0; i < depth; i++)
        {
            value = MetadataValue.Nested(FieldMetadata.FromValues(
                [new KeyValuePair<string, MetadataValue>("o", value)]));
        }

        return new StructType(
        [
            new StructField(
                "probe",
                DataTypes.LongType,
                nullable: true,
                FieldMetadata.FromValues([new KeyValuePair<string, MetadataValue>("m", value)])),
        ]);
    }

    /// <summary>
    /// Meta-guard: the sweep's depth bound is the reader's own limit, and the probe that found it
    /// was not itself the limit.
    /// </summary>
    [Fact]
    public void SweepDepthBound_IsTheReadersOwnLimit()
    {
        int probed = ProbedMaxMetadataDepth;

        // The sweep must USE the probed limit. Trivially true as written, and deliberately so:
        // it is the regression guard against someone re-pinning SweepDepthBound to a literal,
        // which is exactly how this axis was defective before -- and which every other assertion
        // here would happily pass, because they check the PROBE rather than what consumes it.
        Assert.Equal(probed, SweepDepthBound);

        Assert.True(
            probed > GeneratedMetadataDepthBound,
            $"The probed reader depth limit ({probed}) is not deeper than the generator's bound "
            + $"({GeneratedMetadataDepthBound}), so the constructive sweep would add no depth "
            + "coverage over the random draw and the depth axis would be sampled only.");

        Assert.True(
            probed < DepthProbeCeiling,
            $"The depth probe reached its own ceiling ({DepthProbeCeiling}) without finding the "
            + "reader's limit, so the bound the sweep uses is this test's literal rather than a "
            + "property of the reader. Raise the ceiling.");

        // Non-vacuity of the probe itself: one deeper than the measured limit must NOT round-trip,
        // or the loop is stopping for a reason other than the reader's depth handling.
        // #711: with the write side now bounded too, "does not round-trip" includes ToJson itself failing
        // closed at probed+1 — so the serialize is inside the try and a write-side rejection counts as
        // not-round-tripping exactly like a read-side one.
        bool roundTripped;
        try
        {
            string tooDeep = SchemaJson.ToJson(BuildDepthProbeSchema(probed + 1));
            roundTripped = string.Equals(
                SchemaJson.ToJson(SchemaJson.FromJson(tooDeep)), tooDeep, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            roundTripped = false;
        }

        Assert.False(
            roundTripped,
            $"Metadata nested {probed + 1} deep round-trips, so the probe stopped early and the "
            + "sweep is shallower than the reader actually supports.");
    }

    /// <summary>
    /// Nests <paramref name="text"/> into a key, a string value and an array element at every depth
    /// from <paramref name="depth"/> down to <see cref="SweepDepthBound"/>.
    /// </summary>
    private static MetadataValue NestMetadata(string text, int depth)
    {
        var entries = new List<KeyValuePair<string, MetadataValue>>
        {
            new("k" + depth + text, MetadataValue.String("v" + depth + text)),
            new("arr", MetadataValue.Array([MetadataValue.String("a" + depth + text)])),
        };

        if (depth < SweepDepthBound)
        {
            entries.Add(new KeyValuePair<string, MetadataValue>(
                "obj", NestMetadata(text, depth + 1)));
        }

        return MetadataValue.Nested(FieldMetadata.FromValues(entries.ToArray()));
    }

    /// <summary>
    /// Numeric boundary values, REFLECTED from the numeric types rather than listed.
    /// <para>
    /// <c>MinValue</c>, <c>MaxValue</c> and <c>Epsilon</c> come from the type system, and the Int32
    /// edges come from <c>typeof(int)</c> -- which is the actual narrowing hazard, so the hazard
    /// defines its own boundary. NEGATIVE ZERO is added from its bit pattern because it cannot be
    /// discovered by any value-based enumeration: <c>-0.0 == 0.0</c> is true, so it is invisible to
    /// equality yet renders as a different JSON token.
    /// </para>
    /// </summary>
    private static IReadOnlyList<double> DoubleBoundaries => _doubleBoundaries ??= BuildBoundaries<double>(
        [BitConverter.Int64BitsToDouble(long.MinValue), 0.0, 1.0, -1.0, 1e-300]);

    private static IReadOnlyList<double>? _doubleBoundaries;

    private static IReadOnlyList<long> LongBoundaries => _longBoundaries ??= BuildBoundaries<long>(
        [0L, 1L, -1L, int.MaxValue, (long)int.MaxValue + 1, int.MinValue, (long)int.MinValue - 1]);

    private static IReadOnlyList<long>? _longBoundaries;

    private static IReadOnlyList<T> BuildBoundaries<T>(IReadOnlyList<T> extras)
        where T : struct
    {
        var values = new List<T>();
        foreach (System.Reflection.FieldInfo field in typeof(T).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.FieldType == typeof(T) && field.GetValue(null) is T value
                && (value is not double d || double.IsFinite(d)))
            {
                values.Add(value);
            }
        }

        values.AddRange(extras);
        return values;
    }

    /// <summary>
    /// GENERATED artifact surface: the primary call-site divergence assertion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every pinned corpus in this file enumerates one axis — scalar type, metadata value kind,
    /// string-encoding position, numeric token shape — and nine review rounds established the
    /// pattern that closing the axis you were shown leaves the next one hand-listed. This test
    /// replaces axis enumeration for the divergence property: the oracle is
    /// <c>footer == SchemaJson.ToJson(schema)</c>, which is axis-free, so a rogue at the writer's
    /// call site is caught by any generated schema that reaches the shape it mishandles, whether or
    /// not anyone thought of that shape.
    /// </para>
    /// <para>
    /// SCOPE — stated precisely, because the temptation to overclaim here is exactly the failure
    /// mode this PR keeps rediscovering. Generation is sound and strictly subsumes axis
    /// enumeration for call-site divergence, but <b>enumeration cannot be eliminated, only
    /// relocated to one auditable place</b>. It relocates here, into the per-position value
    /// domains below. A generator drawing doubles from <c>[0,1)</c> would miss an integral-double
    /// rogue with probability ~1 — exactly as a hand corpus containing only <c>0.5</c> does. The
    /// win is that N scattered hand-lists became one surface that a reviewer can read in a single
    /// sitting; the domains are still chosen, and they are now the thing to review.
    /// </para>
    /// <para>
    /// WHY THIS IS DIFFERENT FROM THE PREVIOUS NINE ROUNDS, rather than a tenth patch. The space of
    /// AXES along which a rogue may diverge is <i>open</i> — nobody enumerated it, which is why ten
    /// rounds found ten. The space this relocates enumeration into is the <i>closed grammar of the
    /// input type</i>: concrete types by writer acceptance, kinds by <c>Enum.GetValues</c>, string
    /// alphabet probed from the serializer, nesting depth by an explicit bound, numeric domains
    /// over the full 64-bit ranges. A closed grammar is something a meta-guard can check, and
    /// <c>GeneratedValueDomains_AreNotSilentlyNarrowed</c> checks it. That is the difference: the
    /// remaining hand-choices are finite, named, and asserted rather than scattered and implicit.
    /// </para>
    /// <para>
    /// This is also why <c>GeneratedValueDomains_AreNotSilentlyNarrowed</c> exists. Every earlier
    /// guard in this file derives its <i>set</i> — types from writer acceptance, kinds from the
    /// enum, escape forms probed from the serializer — but all of those range over strings and
    /// kinds. Nothing constrained the <i>value</i> inside a numeric kind, which is how an
    /// integral-double rogue survived. A domain narrowed by a well-meaning edit must fail loudly
    /// rather than quietly reduce coverage, so the domains are asserted, not just documented.
    /// </para>
    /// <para>
    /// The goldens remain necessary and are not superseded. This assertion compares the footer
    /// against the shared serializer, so it cannot see drift <i>inside</i> that serializer, where
    /// both sides move together. Only the pinned bytes can.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GeneratedSchemas_FooterAlwaysMatchesTheSharedSerializer()
    {
        IReadOnlyList<DataType> support = await WriterAcceptedTypesAsync();

        // The support is DERIVED by asking the writer across the whole DataType space, including
        // the full decimal parameter sweep — not by reflecting over AtomicType subclasses, which
        // leaves every parameterised type outside its reach while appearing thorough.
        Assert.True(
            support.Count >= 20,
            $"Writer-accepted type support collapsed to {support.Count}; the generator would be "
            + "exercising a fraction of the type space while still passing.");

        var rng = new DeterministicRng(GeneratedSeed);
        for (int i = 0; i < GeneratedCaseCount; i++)
        {
            StructType schema = GenerateSchema(rng, support);
            string expected = SchemaJson.ToJson(schema);
            string footer = await WriteAndReadFooterSchemaAsync(schema);
            if (!string.Equals(expected, footer, StringComparison.Ordinal))
            {
                // Record the failing case in full. The seed and index reproduce it exactly, and
                // the two payloads are printed because a CI log is often the only artifact anyone
                // gets to look at.
                Assert.Fail(
                    $"Footer diverged from the shared serializer at seed {GeneratedSeed}, case {i}."
                    + $"{Environment.NewLine}  expected (log): {expected}"
                    + $"{Environment.NewLine}  actual (footer): {footer}");
            }
        }
    }

    /// <summary>
    /// Fixed seed: a failure must be reproducible from the test name alone, so this never draws on
    /// ambient randomness. Changing it is a deliberate act that re-samples the whole surface.
    /// </summary>
    private const int GeneratedSeed = 20260728;

    /// <summary>
    /// Case budget. Each case is a real Parquet write and footer read; the zero-batch path keeps
    /// that to roughly half a millisecond, so this costs ~0.1s against a Storage suite that runs
    /// in about three minutes. Raising it is cheap and raising it a lot is not, which is why the
    /// number is here rather than inline.
    /// </summary>
    private const int GeneratedCaseCount = 1200;

    /// <summary>
    /// Audits the generator's VALUE DOMAINS, which are where enumeration now lives.
    /// </summary>
    /// <remarks>
    /// The sibling test's oracle is axis-free, so it can only catch a rogue on a shape its
    /// generator actually produces. That makes the domains load-bearing in exactly the way a hand
    /// corpus used to be: a generator drawing doubles from <c>[0,1)</c> misses an integral-double
    /// rogue with probability ~1. Narrowing a domain — by an innocent-looking edit to a
    /// <c>switch</c> arm — would silently reduce coverage while every test stayed green, which is
    /// the failure this file has been correcting for ten rounds. So the domains are asserted
    /// against the same generator, on the same seed, rather than described in prose.
    /// </remarks>
    [Fact]
    public async Task GeneratedValueDomains_AreNotSilentlyNarrowed()
    {
        IReadOnlyList<DataType> support = await WriterAcceptedTypesAsync();
        var rng = new DeterministicRng(GeneratedSeed);

        var kinds = new HashSet<MetadataValueKind>();
        var formCells = new Dictionary<(string Position, int Depth), SortedSet<string>>();
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        bool integralDouble = false;
        bool exponentDouble = false;
        bool longBeyondInt32 = false;
        bool longAtBoundary = false;
        bool astralCodepoint = false;
        bool controlCharacter = false;
        bool quoteOrBackslash = false;
        bool nonAsciiBmp = false;
        var stringPositions = new SortedSet<string>(StringComparer.Ordinal);
        int maxStringLength = 0;
        int maxDepth = -1;
        bool loneSurrogate = false;

        for (int i = 0; i < GeneratedCaseCount; i++)
        {
            StructType schema = GenerateSchema(rng, support);
            foreach (StructField field in schema)
            {
                typeNames.Add(field.DataType.TypeName);
                InspectString(field.Name, "field name", 0);
                InspectMetadata(field.Metadata, 0);
            }
        }

        // Numeric domains: the axis that no earlier guard constrained, because every other guard
        // ranges over strings and kinds rather than the value inside a kind.
        Assert.True(integralDouble, "No integral-valued double generated: WriteDouble's \".0\" branch is unreachable.");
        Assert.True(exponentDouble, "No exponent-form double generated.");
        Assert.True(longBeyondInt32, "No long outside Int32 range generated: an Int32-narrowing rogue would survive.");
        Assert.True(longAtBoundary, "Neither 64-bit boundary generated.");

        // String domains: the character classes the encoder treats differently.
        Assert.True(astralCodepoint, "No astral codepoint generated: surrogate-pair encoding is unexercised.");
        Assert.True(controlCharacter, "No control character generated.");
        Assert.True(quoteOrBackslash, "Neither quote nor backslash generated.");
        Assert.True(nonAsciiBmp, "No non-ASCII BMP character generated.");

        // Kind and type domains, derived from the same sources the generator draws from.
        Assert.Equal(Enum.GetValues<MetadataValueKind>().OrderBy(k => k), kinds.OrderBy(k => k));
        Assert.True(
            typeNames.Count >= 20,
            $"Only {typeNames.Count} distinct types generated across {GeneratedCaseCount} schemas.");

        // ESCAPE FORMS per (POSITION x DEPTH) CELL -- not unioned across them.
        //
        // The previous version collected every emitted form into ONE set and checked that set
        // against the serializer's. That is a MARGINAL: a form produced anywhere satisfied it
        // everywhere, so a generator that emitted backslashes only in field names and never at
        // depth reported full coverage. It is exactly the collapse fixed in the corpus guard by
        // making depth part of the cell key -- and the fix was applied there and not here, in the
        // same commit. The cell key is now joint in both places.
        SortedSet<string> requiredForms = ProbeEmittedEscapeForms();
        foreach ((string position, int depth) in RequiredStringCells(GeneratedMetadataDepthBound))
        {
            SortedSet<string> covered = formCells.TryGetValue((position, depth), out SortedSet<string>? c)
                ? c
                : new SortedSet<string>(StringComparer.Ordinal);
            string[] missing = requiredForms.Where(form => !covered.Contains(form)).ToArray();
            Assert.True(
                missing.Length == 0,
                $"The generator never produced these escape forms in the {position} position at "
                + $"depth {depth}, so a rogue mishandling them THERE would not be sampled: "
                + $"{string.Join(" ", missing)}");
        }

        // MAGNITUDE. Length was not a dimension of this audit at all: the generator drew 1-6
        // elements, so no string ever exceeded ~12 characters, and a footer serializer that
        // truncated at a fixed buffer size (256 chars) collapsed two distinct columns onto one
        // footer name -- an identity break with no hostile characters anywhere in it.
        Assert.True(
            maxStringLength >= MagnitudeLengths.Max(),
            $"The generator's longest string was {maxStringLength} characters, below the longest "
            + $"pinned magnitude ({MagnitudeLengths.Max()}): a fixed-buffer truncation would not "
            + "be sampled.");

        // POSITIONS and DEPTH: the grammar's string slots, and that recursion actually reaches the
        // stated bound rather than terminating early for some accident of the seed.
        // Positions come from the SAME derivation the cell loop uses, rather than being repeated as
        // a literal. The literal that used to be here still named the pre-depth-key positions
        // ("nested metadata key"), which conflated position with depth -- the very collapse the
        // cell key was introduced to remove.
        Assert.Equal(
            RequiredStringCells(GeneratedMetadataDepthBound)
                .Select(c => c.Position)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray(),
            stringPositions.ToArray());
        Assert.True(
            maxDepth >= GeneratedMetadataDepthBound,
            $"Generated metadata reached depth {maxDepth}, below the stated bound of "
            + $"{GeneratedMetadataDepthBound}: the recursive arms are under-sampled.");

        // The #710 exclusion, asserted rather than assumed. If the alphabet ever admits a lone
        // surrogate the sibling test starts failing for a known-open defect it is not about, and
        // this says so directly instead of leaving a confusing byte diff.
        Assert.False(
            loneSurrogate,
            "The generator produced a lone surrogate. Those are replaced by U+FFFD en route to "
            + "UTF-8 (#710) and are deliberately outside this generator's support.");

        void InspectMetadata(FieldMetadata metadata, int depth)
        {
            maxDepth = Math.Max(maxDepth, depth);
            foreach (KeyValuePair<string, MetadataValue> entry in metadata)
            {
                InspectString(entry.Key, "metadata key", depth);
                InspectValue(entry.Value, depth, "metadata string value");
            }
        }

        void InspectValue(MetadataValue value, int depth, string position)
        {
            kinds.Add(value.Kind);
            switch (value.Kind)
            {
                case MetadataValueKind.Double:
                    double d = value.AsDouble();
                    string text = d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    integralDouble |= double.IsFinite(d) && text.IndexOfAny(['.', 'e', 'E']) < 0;
                    exponentDouble |= text.IndexOfAny(['e', 'E']) >= 0;
                    break;
                case MetadataValueKind.Long:
                    long l = value.AsLong();
                    longBeyondInt32 |= l > int.MaxValue || l < int.MinValue;
                    longAtBoundary |= l == long.MaxValue || l == long.MinValue;
                    break;
                case MetadataValueKind.String:
                    InspectString(value.AsString(), position, depth);
                    break;
                case MetadataValueKind.Array:
                    foreach (MetadataValue item in value.AsArray())
                    {
                        InspectValue(item, depth + 1, "array element string");
                    }

                    break;
                case MetadataValueKind.Nested:
                    InspectMetadata(value.AsNested(), depth + 1);
                    break;
                default:
                    break;
            }
        }

        void InspectString(string text, string position, int depth)
        {
            stringPositions.Add(position);
            maxStringLength = Math.Max(maxStringLength, text.Length);
            if (!formCells.TryGetValue((position, depth), out SortedSet<string>? cell))
            {
                cell = new SortedSet<string>(StringComparer.Ordinal);
                formCells[(position, depth)] = cell;
            }

            cell.UnionWith(EscapeFormsIn(text));
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                astralCodepoint |= char.IsHighSurrogate(c);
                controlCharacter |= c < 0x20;
                quoteOrBackslash |= c is '"' or '\\';
                nonAsciiBmp |= c >= 0x80 && !char.IsSurrogate(c);
                loneSurrogate |= char.IsHighSurrogate(c)
                    ? i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])
                    : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(text[i - 1]));
            }
        }
    }

    /// <summary>
    /// Asks the WRITER which types it accepts, by attempting a real write of each candidate. The
    /// property we care about is acceptance, not the shape of the code that decides it, so this
    /// never reads <c>ParquetTypeMapping</c>'s switch. The decimal family is swept across its full
    /// precision range rather than sampled, because its parameters are protocol-visible.
    /// </summary>
    private static async Task<IReadOnlyList<DataType>> WriterAcceptedTypesAsync()
    {
        var candidates = new List<DataType>();

        // Every non-abstract DataType the engine declares that exposes a parameterless singleton.
        foreach (System.Reflection.PropertyInfo property in typeof(DataTypes).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (typeof(DataType).IsAssignableFrom(property.PropertyType)
                && property.GetValue(null) is DataType singleton)
            {
                candidates.Add(singleton);
            }
        }

        // Full decimal parameter sweep. Scale extremes at every precision: the accepted region is
        // bounded by precision, and both bounds are protocol-visible in schemaString.
        for (int precision = 1; precision <= 38; precision++)
        {
            foreach (int scale in new[] { 0, precision / 2, precision })
            {
                candidates.Add(DataTypes.CreateDecimalType(precision, scale));
            }
        }

        candidates.Add(DataTypes.CreateArrayType(DataTypes.StringType));
        candidates.Add(DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType));
        candidates.Add(DataTypes.CreateStructType([new StructField("x", DataTypes.LongType)]));

        var accepted = new List<DataType>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (DataType candidate in candidates)
        {
            if (!seen.Add(candidate.TypeName))
            {
                continue;
            }

            try
            {
                await WriteAndReadFooterSchemaAsync(
                    new StructType([new StructField("probe", candidate, nullable: true)]));
                accepted.Add(candidate);
            }
            catch (DeltaStorageException)
            {
                // Refused by the writer, so no artifact assertion for it is possible at all.
            }
        }

        return accepted;
    }

    private static StructType GenerateSchema(DeterministicRng rng, IReadOnlyList<DataType> support)
    {
        int fieldCount = 1 + rng.Next(4);
        var fields = new List<StructField>(fieldCount);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < fieldCount; i++)
        {
            string name = GenerateString(rng);
            if (!usedNames.Add(name))
            {
                name += i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                usedNames.Add(name);
            }

            // A nested container is always emitted OPTIONAL by Parquet.Net, so the writer refuses a
            // declared-REQUIRED one outright (#730/§2.4a) — that boundary is pinned directly by
            // ScalarArtifactCorpus_CoversEveryTypeTheWriterAccepts. Here the generator draws from the
            // WRITABLE surface, so nested fields are always nullable while scalars keep both lanes.
            DataType type = support[rng.Next(support.Count)];
            bool nullable = rng.Next(2) == 0 || type is ArrayType or MapType or StructType;
            fields.Add(new StructField(name, type, nullable, GenerateMetadata(rng, depth: 0)));
        }

        return new StructType(fields);
    }

    private static FieldMetadata GenerateMetadata(DeterministicRng rng, int depth)
    {
        int count = rng.Next(depth == 0 ? 4 : 3);
        var entries = new List<KeyValuePair<string, MetadataValue>>(count);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string key = GenerateString(rng, allowEmpty: true);
            if (!usedKeys.Add(key))
            {
                continue;
            }

            entries.Add(new KeyValuePair<string, MetadataValue>(key, GenerateValue(rng, depth)));
        }

        return FieldMetadata.FromValues(entries.ToArray());
    }

    /// <summary>
    /// The nesting depth the generator recurses to. The input grammar admits unbounded nesting, so
    /// this is an explicit BOUND rather than a derivation — stated once, next to the other
    /// generator domains, so it is reviewable rather than buried in a comparison.
    /// </summary>
    private const int GeneratedMetadataDepthBound = 2;

    private static MetadataValue GenerateValue(DeterministicRng rng, int depth)
    {
        // Every MetadataValueKind is reachable; Array and Nested recurse to a bounded depth so
        // compositions no hand-written corpus expresses are generated.
        MetadataValueKind[] kinds = Enum.GetValues<MetadataValueKind>();
        MetadataValueKind kind = kinds[rng.Next(kinds.Length)];
        if (depth >= GeneratedMetadataDepthBound
            && (kind == MetadataValueKind.Array || kind == MetadataValueKind.Nested))
        {
            kind = MetadataValueKind.Long;
        }

        switch (kind)
        {
            case MetadataValueKind.Long:
                return MetadataValue.Long(GenerateLong(rng));
            case MetadataValueKind.Double:
                return MetadataValue.Double(GenerateDouble(rng));
            case MetadataValueKind.Boolean:
                return MetadataValue.Boolean(rng.Next(2) == 0);
            case MetadataValueKind.Null:
                return MetadataValue.Null;
            case MetadataValueKind.Array:
                int items = rng.Next(4);
                var array = new MetadataValue[items];
                for (int i = 0; i < items; i++)
                {
                    array[i] = GenerateValue(rng, depth + 1);
                }

                return MetadataValue.Array(array);
            case MetadataValueKind.Nested:
                return MetadataValue.Nested(GenerateMetadata(rng, depth + 1));
            default:
                return MetadataValue.String(GenerateString(rng, allowEmpty: true));
        }
    }

    /// <summary>
    /// Longs across the FULL 64-bit range, with the boundaries and the Int32 edges weighted in:
    /// a transducer that narrowed ids to Int32 corrupted 3000000000 to -1294967296 silently.
    /// </summary>
    private static long GenerateLong(DeterministicRng rng)
    {
        IReadOnlyList<long> boundaries = LongBoundaries;
        return rng.Next(2) == 0
            ? boundaries[rng.Next(boundaries.Count)]
            : unchecked((long)rng.NextUInt64());
    }

    /// <summary>
    /// Doubles including INTEGRAL values, which are the ones that exercise WriteDouble's ".0"
    /// branch and whose omission let a number-token rogue change a metadata value's type on
    /// re-read (Double 1.0 spelled "1" reads back as Long).
    /// </summary>
    private static double GenerateDouble(DeterministicRng rng)
    {
        // Half the draws come from the REFLECTED boundary set (so -0.0, Epsilon, MinValue and the
        // integral ".0" cases are all reachable without being hand-listed here), half from the full
        // 64-bit bit space. The arms used to be a hand-written switch, which is the same
        // hand-listed-leaf defect one level in from the domains it was supposed to widen.
        IReadOnlyList<double> boundaries = DoubleBoundaries;
        if (rng.Next(2) == 0)
        {
            return boundaries[rng.Next(boundaries.Count)];
        }

        // Finite only: NaN and the infinities are not JSON numbers at all, so they are a
        // SHARED-serializer question rather than a footer/log divergence one, and the equality
        // oracle here is blind to them by construction. Tracked separately.
        return BitConverter.Int64BitsToDouble(unchecked((long)rng.NextUInt64())) is double d
            && double.IsFinite(d) ? d : 0.5;
    }

    /// <summary>
    /// THE single codepoint sweep. Both the required escape-form set and the generator's alphabet
    /// read from this one probe.
    /// <para>
    /// Two duplicated walks used to exist, and both opened with <c>for (int c = 1; ...)</c> — so
    /// <b>U+0000 was excluded from the requirement AND from the generator that is checked against
    /// it</b>. Because the exclusion was shared, the meta-guard comparing them could not see it:
    /// they agreed perfectly while both being short. The writer accepts NUL in field names,
    /// metadata keys and metadata string values, and a footer serializer that truncated at NUL
    /// (UTF-8 marshalling through a NUL-terminated buffer) declared a different column set than the
    /// log and shipped fully green. That is the subtlest form of this file's recurring defect: not
    /// a leaf that stopped deriving, but <i>a bound shared by the prober and the thing it probes</i>.
    /// </para>
    /// <para>
    /// So there is now ONE walk, it starts at U+0000, and it covers the WHOLE Basic Multilingual
    /// Plane rather than stopping at an ASCII boundary — 63,488 codepoints in ~75 ms, measured, so
    /// completeness costs nothing worth trading. Astral is sampled rather than swept because the
    /// encoder's behaviour above the BMP is uniform (every astral scalar becomes a surrogate pair);
    /// that claim is not assumed, it is checked by
    /// <c>AlphabetCompletenessBound_IsJustifiedByTheEncoder</c>.
    /// </para>
    /// <para>
    /// LONE SURROGATES ARE EXCLUDED DELIBERATELY. An unpaired surrogate is replaced by U+FFFD on
    /// the way to UTF-8 — a real defect, but a KNOWN and separately tracked one (#710); generating
    /// it here would make these tests fail for a reason they are not about.
    /// </para>
    /// </summary>
    /// <summary>
    /// One text per escape form the encoder emits, PROBED rather than listed. The generator draws
    /// from this stratum as well as from the full alphabet: the alphabet is complete over the
    /// first 256 codepoints, which dilutes the seven escape forms to roughly 1/250 per character,
    /// so uniform sampling stopped reaching them in the rarer (position, depth) cells. Widening
    /// the alphabet narrowed form coverage -- the two are in tension, and stratifying resolves it
    /// without either set being hand-written.
    /// </summary>
    private static IReadOnlyList<string> EscapeFormRepresentatives =>
        _escapeFormReps ??= EscapeProbe.Alphabet
            .Where(t => EscapeFormsIn(t).Any(f => f != "literal"))
            .ToArray();

    private static IReadOnlyList<string>? _escapeFormReps;

    private static (SortedSet<string> Forms, IReadOnlyList<string> Alphabet) EscapeProbe =>
        _escapeProbe ??= BuildEscapeProbe();

    private static (SortedSet<string> Forms, IReadOnlyList<string> Alphabet)? _escapeProbe;

    /// <summary>
    /// The codepoint below which the generator's alphabet is COMPLETE — every escaped character
    /// under it is in the alphabet, not merely one representative per form. Above it the alphabet
    /// keeps one representative per form. This is a stated bound, but unlike the bound it replaced
    /// it is <b>checked</b>: <c>AlphabetCompletenessBound_IsJustifiedByTheEncoder</c> fails if the
    /// encoder ever emits a form above this bound that it does not also emit below it.
    /// </summary>
    private const int AlphabetCompletenessBound = 0x100;

    /// <summary>Astral samples. The encoder is uniform above the BMP; see the bound guard.</summary>
    private static readonly int[] AstralProbes = [0x10000, 0x1F389, 0x1F600, 0x10FFFF];

    private static (SortedSet<string> Forms, IReadOnlyList<string> Alphabet) BuildEscapeProbe()
    {
        var forms = new SortedSet<string>(StringComparer.Ordinal);
        var alphabet = new List<string>();
        var byForm = new SortedDictionary<string, string>(StringComparer.Ordinal);

        // Complete below the bound: EVERY escaped codepoint enters the alphabet, so NUL, the C0
        // controls, the quote and the backslash are all individually generable rather than being
        // represented by whichever one happened to be found first.
        for (int c = 0; c < AlphabetCompletenessBound; c++)
        {
            string text = char.ConvertFromUtf32(c);
            string[] found = EscapeFormsIn(text).ToArray();
            if (found.Length == 0)
            {
                continue;
            }

            forms.UnionWith(found);
            alphabet.Add(text);
        }

        // The rest of the BMP, swept in full for FORMS, sampled for alphabet representatives.
        for (int c = AlphabetCompletenessBound; c <= 0xFFFF; c++)
        {
            if (c is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            string text = char.ConvertFromUtf32(c);
            foreach (string form in EscapeFormsIn(text))
            {
                forms.Add(form);
                byForm.TryAdd(form, text);
            }
        }

        foreach (int c in AstralProbes)
        {
            string text = char.ConvertFromUtf32(c);
            foreach (string form in EscapeFormsIn(text))
            {
                forms.Add(form);
                byForm.TryAdd(form, text);
            }

            alphabet.Add(text);
        }

        alphabet.AddRange(byForm.Values.Where(v => !alphabet.Contains(v, StringComparer.Ordinal)));
        return (forms, alphabet);
    }

    /// <summary>
    /// Justifies <see cref="AlphabetCompletenessBound"/> instead of asserting it in prose.
    /// </summary>
    /// <remarks>
    /// The generator's alphabet is complete only below the bound; above it, it keeps one
    /// representative per form. That is sound exactly as long as the encoder emits no form up there
    /// that it does not also emit down here. This test sweeps the entire remaining BMP and the
    /// astral samples and checks precisely that, so the bound cannot quietly become wrong — which
    /// is the failure mode of the bound it replaced.
    /// </remarks>
    [Fact]
    public void AlphabetCompletenessBound_IsJustifiedByTheEncoder()
    {
        var below = new SortedSet<string>(StringComparer.Ordinal);
        for (int c = 0; c < AlphabetCompletenessBound; c++)
        {
            below.UnionWith(EscapeFormsIn(char.ConvertFromUtf32(c)));
        }

        var above = new SortedSet<string>(StringComparer.Ordinal);
        for (int c = AlphabetCompletenessBound; c <= 0xFFFF; c++)
        {
            if (c is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            above.UnionWith(EscapeFormsIn(char.ConvertFromUtf32(c)));
        }

        string[] onlyAbove = above.Where(form => !below.Contains(form)).ToArray();
        Assert.True(
            onlyAbove.Length == 0,
            $"The encoder emits {string.Join(" ", onlyAbove)} only ABOVE U+{AlphabetCompletenessBound:X4}, "
            + "so one-representative-per-form is no longer sufficient up there and the generator's "
            + "alphabet is incomplete for those forms. Raise AlphabetCompletenessBound.");

        // Non-vacuity: the sweeps must actually have found the encoder's forms.
        Assert.Contains("\\u(bmp)", below);
        Assert.Contains("\\n", below);
        Assert.True(above.Count > 0, "The above-bound sweep produced no escape forms at all.");
    }

    /// <summary>
    /// Strings drawn from the DERIVED escape alphabet plus the character classes the encoder
    /// treats structurally differently — plain ASCII, non-ASCII BMP, and astral codepoints, which
    /// the encoder emits as a surrogate PAIR rather than a single escape.
    /// </summary>
    /// <param name="allowEmpty">
    /// Whether the empty string is a legal draw HERE. It is legal for a metadata key or a metadata
    /// string value and illegal for a field name, because StructField rejects an empty name -- so
    /// the generator is narrowed by the model rather than by a blanket minimum length of one, which
    /// was the third of the four mechanisms that made the empty string unreachable.
    /// </param>
    private static string GenerateString(DeterministicRng rng, bool allowEmpty = false)
    {
        IReadOnlyList<string> alphabet = EscapeProbe.Alphabet;
        IReadOnlyList<string> forms = EscapeFormRepresentatives;

        // MAGNITUDE is a domain, not an accident of the loop bound. One draw in eight is long
        // enough to cross a fixed-buffer truncation; the rest stay short so compositions stay
        // cheap and the case budget still buys interaction coverage rather than bulk.
        if (allowEmpty && rng.Next(16) == 0)
        {
            return string.Empty;
        }

        int length = rng.Next(8) == 0
            ? MagnitudeLengths[rng.Next(MagnitudeLengths.Length)]
            : 1 + rng.Next(6);
        var chars = new System.Text.StringBuilder(length);
        IReadOnlyList<string> hazards = HazardCodepoints;
        for (int i = 0; i < length; i++)
        {
            // Four strata, three of them DERIVED. The arms used to carry hand-chosen sub-ranges
            // (0x80 + 0x300, 0x10000 + 0x1000, 0xE000 + 0x1000) which is the same defect as a
            // hand-chosen loop bound, one level in: the generator could only ever reach the
            // characters those literals happened to span, and U+2028 was outside all of them.
            switch (rng.Next(4))
            {
                case 0:
                    // Plain ASCII, so most generated strings stay legible in a failure message.
                    chars.Append((char)('a' + rng.Next(26)));
                    break;
                case 1:
                    // The escape-form stratum, probed from the encoder.
                    chars.Append(forms[rng.Next(forms.Count)]);
                    break;
                case 2:
                    // The alphabet the encoding audit requires coverage of.
                    chars.Append(alphabet[rng.Next(alphabet.Count)]);
                    break;
                default:
                    // The UCD-derived hazard set: every Unicode category, plus everything below
                    // U+0100 exhaustively. U+2028 and U+2029 are reachable from here because the
                    // character database says they are their own categories, not because anyone
                    // remembered them.
                    chars.Append(hazards[rng.Next(hazards.Count)]);
                    break;
            }
        }

        return chars.ToString();
    }

    /// <summary>
    /// Seeded xorshift64*. Deterministic by construction so a failure is reproducible from the
    /// seed alone, and independent of any ambient randomness source.
    /// </summary>
    private sealed class DeterministicRng(ulong seed)
    {
        private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

        public ulong NextUInt64()
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return unchecked(_state * 0x2545F4914F6CDD1DUL);
        }

        public int Next(int exclusiveBound) => (int)(NextUInt64() % (ulong)exclusiveBound);
    }
}
