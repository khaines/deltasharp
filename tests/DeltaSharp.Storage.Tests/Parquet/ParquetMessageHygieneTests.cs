using System.Globalization;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet.Serialization;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// Message-hygiene regressions for the Parquet read/write surfaces (#683/#686, council round-1 completion).
/// Every column name reaching these messages originates in <c>metaData.schemaString</c>, which on a
/// foreign/hostile table read is fully attacker-authored, so each echo must be neutralized (no control
/// characters that could break a structured-log line) and length-bounded (no unbounded log flood).
/// <para>These tests deliberately drive ONE guard each, so reverting a single <c>Sanitize</c> call turns
/// exactly one test red — the grouped-revert style used in round 1 masked a vacuous guard.</para>
/// </summary>
public sealed class ParquetMessageHygieneTests
{
    // Same corpus as StorageMessageHygieneTests: CR, LF, NUL, tab, a C1 control (NEL), the Unicode LINE and
    // PARAGRAPH separators, and a lone high surrogate — every class DiagnosticText.Sanitize neutralizes.
    private const string FullInjectionCorpus = "a\r\nb\0c\td\u0085e\u2028f\u2029g\uD800h";

    // The subset that survives being written into a real Parquet footer. Parquet.Net encodes column names as
    // UTF-8, which rewrites a LONE SURROGATE to U+FFFD — so a requested name containing \uD800 would no longer
    // match the file column, diverting the read to ColumnNotPresentInFile and silently testing a DIFFERENT
    // guard than the one under test. (That mis-targeting is exactly what this corpus split prevents; the lone
    // surrogate is still exercised on the direct-call tests below, which never round-trip through a file.)
    private const string FileNameCorpus = "a\r\nb\0c\td\u0085e\u2028f\u2029g";

    private static void AssertFullyNeutralized(string message, int expectedNewlines = 0)
    {
        Assert.Equal(expectedNewlines, message.Count(c => c == '\n'));
        // U+001B (ANSI ESC) is included: a raw escape sequence reaching a terminal-backed log viewer is a
        // real injection, and omitting it here is what let a theory case pass vacuously under mutation.
        foreach (char c in new[] { '\r', '\0', '\t', '\u001b', '\u0085', '\u2028', '\u2029', '\uD800' })
        {
            Assert.DoesNotContain(c, message);
        }

        Assert.DoesNotContain(FullInjectionCorpus, message, StringComparison.Ordinal);
    }

    private sealed class ListRow
    {
        public int Id { get; set; }

        public List<int?>? Arr { get; set; }
    }

    private static async Task<byte[]> WriteAsync<T>(IReadOnlyList<T> rows)
        where T : class, new()
    {
        using var stream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, stream, cancellationToken: CancellationToken.None);
        return stream.ToArray();
    }

    private static async Task ReadAsync(byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await foreach (ColumnBatch _ in new ParquetFileReader().ReadAsync(
            stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
        }
    }

    // ---- #683 item 1: ParquetFileReader.ValidateFileField and its siblings ----

    [Theory]
    [InlineData(FileNameCorpus)]
    [InlineData("a\r\nDROP TABLE")]
    [InlineData("esc\u001b[31mred")]
    public async Task ValidateFileField_PhysicalTypeMismatch_SanitizesRequestedColumnName(string poison)
    {
        // The file column is an int named `poison`; the request asks for the SAME name as a string, so
        // resolution succeeds by name and ValidateFileField reports the ClrType mismatch — echoing the
        // attacker-authored requested column name. Protected ONLY by the entry-point sanitize at the top of
        // ValidateFileField.
        byte[] bytes = await RewriteWithColumnNameAsync(poison);
        var requested = new StructType(new[] { new StructField(poison, DataTypes.StringType, nullable: true) });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadAsync(bytes, requested));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);

        // Pin the SPECIFIC guard: this phrase exists only in ValidateFileField's physical-type mismatch.
        // Asserting on the Kind alone would also be satisfied by ColumnNotPresentInFile (a different,
        // already-covered guard), which would make this test silently mis-targeted.
        Assert.Contains("file physical type", error.Message, StringComparison.Ordinal);
        AssertFullyNeutralized(error.Message);

        // Assert against THIS case's own payload, not just the shared corpus constant — otherwise a theory
        // case whose poison is outside the corpus asserts nothing about itself.
        Assert.DoesNotContain(poison, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateFileField_OversizedRequestedColumnName_IsLengthBoundedInMessage()
    {
        // A 100,000-char column name previously rendered a ~100,108-char message. The cap must bound it.
        var huge = new string('x', 100_000);

        byte[] bytes = await RewriteWithColumnNameAsync(huge);
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadAsync(
                bytes,
                new StructType(new[] { new StructField(huge, DataTypes.StringType, nullable: true) })));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("file physical type", error.Message, StringComparison.Ordinal);
        Assert.True(
            error.Message.Length < 400,
            string.Create(CultureInfo.InvariantCulture, $"message was {error.Message.Length} chars: unbounded"));
        Assert.DoesNotContain(new string('x', 200), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateFileField_NullabilityMismatch_SanitizesRequestedColumnName()
    {
        // A distinct throw site inside ValidateFileField (nullable file column into a non-nullable request),
        // so the entry-point sanitize is pinned at more than one exit.
        byte[] bytes = await RewriteWithColumnNameAsync(FileNameCorpus, nullable: true);
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadAsync(
                bytes,
                new StructType(new[]
                {
                    new StructField(FileNameCorpus, DataTypes.IntegerType, nullable: false),
                })));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("the file column is nullable", error.Message, StringComparison.Ordinal);
        AssertFullyNeutralized(error.Message);
    }

    // Writes a single-column Parquet file whose ONE column is literally named `name`, so a request carrying
    // the same (poisoned) name resolves by name and reaches the validation throw sites.
    private static async Task<byte[]> RewriteWithColumnNameAsync(string name, bool nullable = false)
    {
        global::Parquet.Schema.DataField field = nullable
            ? new global::Parquet.Schema.DataField<int?>(name)
            : new global::Parquet.Schema.DataField<int>(name);
        var schema = new global::Parquet.Schema.ParquetSchema(field);
        using var stream = new MemoryStream();
        await using (global::Parquet.ParquetWriter writer =
            await global::Parquet.ParquetWriter.CreateAsync(schema, stream))
        {
            using global::Parquet.ParquetRowGroupWriter group = writer.CreateRowGroup();
            await group.WriteAsync<int>(
                schema.DataFields[0], new ReadOnlyMemory<int?>(new int?[] { 7 }), null, null,
                CancellationToken.None);
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task ValidateFileField_BinaryReadAsString_NamesDistinctActionablePhysicalTypes()
    {
        // #832 diagnosability pin: Parquet.Net 6.1 reports BOTH a UTF-8 and a BYTE_ARRAY column as
        // `ReadOnlyMemory`1`, so rendering Type.Name made this message self-contradictory —
        // "file physical type 'ReadOnlyMemory`1' does not match the requested engine type 'string'
        // (expected 'ReadOnlyMemory`1')" — i.e. it claimed a mismatch between a type and ITSELF, telling the
        // operator nothing about what the file actually holds. DescribePhysicalClrType must render the two
        // kinds as DISTINCT, actionable Parquet tokens. Drive the real read path with a genuine binary column
        // requested as a string, and pin both rendered tokens verbatim.
        var fileSchema = new StructType(new[] { new StructField("c", DataTypes.BinaryType, nullable: true) });
        MutableColumnVector values = ColumnVectors.Create(DataTypes.BinaryType, 1);
        values.AppendBytes(new byte[] { 0x01, 0x02, 0x03 });
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(
            fileSchema, new[] { new ManagedColumnBatch(fileSchema, new ColumnVector[] { values }, 1) });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadAsync(
                bytes,
                new StructType(new[] { new StructField("c", DataTypes.StringType, nullable: true) })));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("file physical type 'binary (BYTE_ARRAY)'", error.Message, StringComparison.Ordinal);
        Assert.Contains("(expected 'string (BYTE_ARRAY/UTF8)')", error.Message, StringComparison.Ordinal);

        // The opaque CLR rendering must be gone entirely — its presence is the regression itself.
        Assert.DoesNotContain("ReadOnlyMemory", error.Message, StringComparison.Ordinal);
        AssertFullyNeutralized(error.Message);
    }

    [Theory]
    [InlineData(global::Parquet.Schema.TimeUnitPrecision.Millis, "TIME_MILLIS", "int")]
    [InlineData(global::Parquet.Schema.TimeUnitPrecision.Micros, "TIME_MICROS", "bigint")]
    [InlineData(global::Parquet.Schema.TimeUnitPrecision.Nanos, "TIME_NANOS", "bigint")]
    public async Task ValidateFileField_TimeColumn_NamesTheAnnotation_NotASelfContradictoryClrName(
        global::Parquet.Schema.TimeUnitPrecision precision, string annotation, string requestedTypeName)
    {
        // #832 diagnosability pin, TIME edition. A TIME column's ClrType is a bare Int32/Int64, so describing
        // the rejection by CLR type produced the self-contradictory "file physical type 'Int64' … cannot be
        // read as 'bigint'" — the operator sees the file type and the requested type as the SAME thing and has
        // no way to learn the column is a time-of-day. The message must name the TIME ANNOTATION instead, so
        // the fix ("this column has no DeltaSharp equivalent; cast it upstream") is legible from the message
        // alone. Pinned here so the rendering cannot silently degrade back to a CLR name.
        byte[] file = await ParquetTestHelpers.WriteTimeColumnAsync("t", precision);
        DataType requested = requestedTypeName == "int" ? DataTypes.IntegerType : DataTypes.LongType;

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadAsync(file, new StructType(new[] { new StructField("t", requested, nullable: true) })));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains($"Parquet TIME column ({annotation})", error.Message, StringComparison.Ordinal);
        Assert.Contains($"cannot be read as '{requestedTypeName}'", error.Message, StringComparison.Ordinal);

        // The bare CLR names are exactly the self-contradictory rendering this replaced.
        Assert.DoesNotContain("Int32", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Int64", error.Message, StringComparison.Ordinal);
        AssertFullyNeutralized(error.Message);
    }

    // ---- #683 item 7: NestedParquetColumnReader.ReadAsync entry-point sanitize ----

    [Fact]
    public async Task NestedReadAsync_PoisonedColumnLabel_IsSanitizedInListReassemblyGuard()
    {
        // Round-1 gap: the ReadAsync entry-point sanitize was MUTATION-VACUOUS — reverting it alone left the
        // whole suite green, because every existing nested test asserted only on clean labels. ~12 echoes in
        // ReadListAsync/ReadStructAsync/ReadMapAsync are reachable ONLY through ReadAsync and protected ONLY
        // by that one line.
        //
        // ParquetFileReader passes `requested[c].Name` RAW into ReadAsync (verified at the call site), so
        // ReadAsync's own sanitize is the sole guard. Drive it directly and trip the element-slot
        // disagreement guard, whose message interpolates the label.
        byte[] bytes = await WriteAsync(new[]
        {
            new ListRow { Id = 1, Arr = new List<int?> { 1, 2 } },
            new ListRow { Id = 2, Arr = new List<int?> { 3 } },
        });

        using var stream = new MemoryStream(bytes, writable: false);
        await using global::Parquet.ParquetReader reader =
            await global::Parquet.ParquetReader.CreateAsync(stream);
        using global::Parquet.ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        global::Parquet.Schema.Field arrField =
            reader.Schema.Fields.Single(f => f.Name == "Arr");

        // An inflated rowCount makes the file's element-slot count too small to describe the group, tripping
        // the fail-closed guard inside ReadListAsync.
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
            await NestedParquetColumnReader.ReadAsync(
                rowGroup,
                arrField,
                DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true),
                rowCount: 10_000,
                columnName: FullInjectionCorpus,
                new NestedParquetColumnReader.NestedDecodeBudget(50_000_000),
                CancellationToken.None));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("element slot", error.Message, StringComparison.Ordinal);
        AssertFullyNeutralized(error.Message);
    }

    [Fact]
    public async Task NestedReadAsync_OversizedColumnLabel_IsLengthBoundedInGuardMessage()
    {
        byte[] bytes = await WriteAsync(new[] { new ListRow { Id = 1, Arr = new List<int?> { 1 } } });

        using var stream = new MemoryStream(bytes, writable: false);
        await using global::Parquet.ParquetReader reader =
            await global::Parquet.ParquetReader.CreateAsync(stream);
        using global::Parquet.ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        global::Parquet.Schema.Field arrField = reader.Schema.Fields.Single(f => f.Name == "Arr");

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
            await NestedParquetColumnReader.ReadAsync(
                rowGroup,
                arrField,
                DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true),
                rowCount: 10_000,
                columnName: new string('y', 100_000),
                new NestedParquetColumnReader.NestedDecodeBudget(50_000_000),
                CancellationToken.None));

        Assert.True(
            error.Message.Length < 400,
            string.Create(CultureInfo.InvariantCulture, $"message was {error.Message.Length} chars: unbounded"));
    }

    // ---- #686 item 2: DataType.SimpleString is NOT a bounded type name ----

    [Fact]
    public void EnsureScalarReadable_NestedWithinNested_EchoesBoundedKindNotRecursiveSimpleString()
    {
        // StructType.SimpleString appends each field's Name VERBATIM and recurses, so echoing it is
        // simultaneously a raw-name echo AND an unbounded aggregate. A 5,000-field struct previously rendered
        // a ~124,000-char message carrying every field name raw. The message must instead carry the bounded
        // KIND ("struct") — the sanitized column label already identifies WHICH column is at fault.
        var fields = new StructField[5_000];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = new StructField(
                string.Create(CultureInfo.InvariantCulture, $"f{i}{FullInjectionCorpus}"),
                DataTypes.IntegerType,
                nullable: true);
        }

        ArrayType nestedWithinNested = DataTypes.CreateArrayType(new StructType(fields), containsNull: true);

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.EnsureReadSupported(
                new StructField("c", nestedWithinNested, nullable: true)));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("'struct'", error.Message, StringComparison.Ordinal);
        Assert.True(
            error.Message.Length < 400,
            string.Create(CultureInfo.InvariantCulture, $"message was {error.Message.Length} chars: unbounded"));
        AssertFullyNeutralized(error.Message);
    }

    [Fact]
    public void CreateField_NestedColumn_EchoesBoundedKindNotRecursiveSimpleString()
    {
        // The write-door mapping guard: same SimpleString hazard, reached through ParquetTypeMapping.CreateField.
        var fields = new StructField[5_000];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = new StructField(
                string.Create(CultureInfo.InvariantCulture, $"f{i}{FullInjectionCorpus}"),
                DataTypes.IntegerType,
                nullable: true);
        }

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(
                new StructField(FullInjectionCorpus, new StructType(fields), nullable: true), honorReferenceNullability: false));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("'struct'", error.Message, StringComparison.Ordinal);
        Assert.True(
            error.Message.Length < 400,
            string.Create(CultureInfo.InvariantCulture, $"message was {error.Message.Length} chars: unbounded"));
        AssertFullyNeutralized(error.Message);
    }
}
