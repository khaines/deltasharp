using System.Reflection;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Shared helpers for the Parquet codec tests: serialize batches to bytes, and <b>surgically poison</b>
/// an individual row group's column chunk on disk (using Parquet.Net's own footer offsets) so a test
/// can prove that a projected/pruned read never touched the poisoned bytes, and that a corrupt row
/// group surfaces a deterministic error without a torn batch.
/// </summary>
internal static class ParquetTestHelpers
{
    /// <summary>Writes <paramref name="batches"/> to a standalone Parquet byte buffer.</summary>
    public static async Task<byte[]> WriteToBytesAsync(
        StructType schema, IReadOnlyList<ColumnBatch> batches, int? rowGroupRowLimit = null)
    {
        var writer = rowGroupRowLimit is int limit
            ? new ParquetFileWriter(limit)
            : new ParquetFileWriter();
        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, schema, batches, CancellationToken.None);
        return stream.ToArray();
    }

    /// <summary>Writes a single-column Parquet file whose one column is a physical unsigned <see cref="byte"/>
    /// named <paramref name="columnName"/>. Unsigned byte has NO DeltaSharp type mapping, so reading this file's
    /// footer (<c>ParquetFileReader.ReadDataSchemaAsync</c> → <c>ParquetTypeMapping.ToDataType</c>) fails
    /// closed with <c>UnsupportedFeature</c> — used to prove that fail-closed message never echoes the
    /// file-derived column name (#653). Authored with Parquet.Net's low-level writer because DeltaSharp's
    /// writer, by construction, cannot emit an unmapped physical type.</summary>
    public static async Task<byte[]> WriteUnmappedByteColumnAsync(string columnName)
    {
        var field = new global::Parquet.Schema.DataField<byte>(columnName);
        var schema = new global::Parquet.Schema.ParquetSchema(field);
        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            await rowGroup.WriteAsync<byte>(
                field, new ReadOnlyMemory<byte>(new byte[] { 1 }), null, null, CancellationToken.None);
        }

        return stream.ToArray();
    }

    /// <summary>Writes a single-column Parquet file whose one column is a real Parquet <b>TIME</b> logical type
    /// (<c>TimeDataField</c>) of <paramref name="precision"/>, named <paramref name="columnName"/>. DeltaSharp
    /// has NO time-of-day type, so this file must FAIL CLOSED at footer mapping. The trap this guards: under
    /// Parquet.Net ≥6.1 a TIME field's <c>ClrType</c> is a RAW <see cref="int"/> (millis) or <see cref="long"/>
    /// (micros/nanos), so without an explicit <c>TimeDataField</c> arm the raw-CLR fallback would silently
    /// misread a TIME column as IntegerType/LongType sub-day units (#832). Authored with Parquet.Net's
    /// low-level writer because DeltaSharp's writer, by construction, cannot emit a TIME column.</summary>
    public static async Task<byte[]> WriteTimeColumnAsync(
        string columnName, global::Parquet.Schema.TimeUnitPrecision precision)
    {
        var field = new global::Parquet.Schema.TimeDataField(columnName, precision);
        var schema = new global::Parquet.Schema.ParquetSchema(field);
        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            if (field.ClrType == typeof(int))
            {
                await rowGroup.WriteAsync<int>(
                    field, new ReadOnlyMemory<int>(new[] { 1 }), null, null, CancellationToken.None);
            }
            else
            {
                await rowGroup.WriteAsync<long>(
                    field, new ReadOnlyMemory<long>(new[] { 1L }), null, null, CancellationToken.None);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Writes a Parquet file whose single top-level column is a nested container (per
    /// <paramref name="position"/>) holding a Parquet <b>TIME</b> leaf of <paramref name="precision"/>.
    /// DeltaSharp has no time-of-day type, so a nested read that asks for the container with the ALIASING
    /// integral leaf (<c>int</c> for millis, <c>bigint</c> for micros/nanos) must FAIL CLOSED rather than
    /// decode the raw sub-day units (#832). Authored with Parquet.Net's low-level writer for the same reason
    /// <see cref="WriteTimeColumnAsync"/> is: DeltaSharp's own writer cannot emit a TIME column.
    /// <para><paramref name="precision"/> chooses which arm of the nested leaf guard is exercised: millis is
    /// physically an INT32 and flows through the <c>IntegerType</c> arm, micros and nanos are INT64 and flow
    /// through <c>LongType</c>. Covering only one of them lets the other arm's guard be deleted with the whole
    /// suite still green, so callers must sweep all three.</para>
    /// <para>With <paramref name="annotateAsTime"/> false the leaf is written as a PLAIN integral field of the
    /// matching width, under the same leaf NAME — the carrier
    /// <see cref="ForgeConvertedTypeOnlyTimeAsync"/> (which matches SchemaElements by name) re-annotates into
    /// the LEGACY ConvertedType-only nested TIME that Parquet.Net's writer cannot itself emit.</para></summary>
    public static async Task<byte[]> WriteNestedTimeColumnAsync(
        NestedTimePosition position,
        global::Parquet.Schema.TimeUnitPrecision precision,
        bool annotateAsTime = true)
    {
        string leafName = NestedTimeLeafName(position);
        bool millis = precision == global::Parquet.Schema.TimeUnitPrecision.Millis;
        global::Parquet.Schema.DataField time = annotateAsTime
            ? new global::Parquet.Schema.TimeDataField(leafName, precision)
            : millis
                ? new global::Parquet.Schema.DataField<int>(leafName)
                : new global::Parquet.Schema.DataField<long>(leafName);

        global::Parquet.Schema.Field container = position switch
        {
            NestedTimePosition.StructField => new global::Parquet.Schema.StructField("s", time),
            NestedTimePosition.ArrayElement => new global::Parquet.Schema.ListField("arr", time),
            _ => new global::Parquet.Schema.MapField(
                "m", new global::Parquet.Schema.DataField<string>("key", nullable: false), time),
        };

        var schema = new global::Parquet.Schema.ParquetSchema(container);
        global::Parquet.Schema.DataField[] leaves = schema.GetDataFields();

        // A repeated leaf needs an explicit repetition stream; a struct leaf has max repetition 0 and takes
        // none. One row, one present value, is enough — the guard rejects on the SCHEMA, before any decode.
        int[]? reps = position == NestedTimePosition.StructField ? null : new[] { 0 };

        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            if (position == NestedTimePosition.MapValue)
            {
                await rowGroup.WriteAsync<ReadOnlyMemory<char>>(
                    leaves[0],
                    new ReadOnlyMemory<ReadOnlyMemory<char>?>(new ReadOnlyMemory<char>?[] { "k".AsMemory() }),
                    reps,
                    null,
                    CancellationToken.None);
            }

            global::Parquet.Schema.DataField timeLeaf = leaves[^1];
            if (millis)
            {
                await rowGroup.WriteAsync<int>(
                    timeLeaf, new ReadOnlyMemory<int?>(new int?[] { 5 }), reps, null, CancellationToken.None);
            }
            else
            {
                await rowGroup.WriteAsync<long>(
                    timeLeaf, new ReadOnlyMemory<long?>(new long?[] { 5L }), reps, null, CancellationToken.None);
            }
        }

        return stream.ToArray();
    }

    /// <summary>The leaf name <see cref="WriteNestedTimeColumnAsync"/> gives the TIME leaf in each nested
    /// position — the handle the footer forge matches on, and the one a test asserts against.</summary>
    public static string NestedTimeLeafName(NestedTimePosition position) => position switch
    {
        NestedTimePosition.StructField => "t",
        NestedTimePosition.ArrayElement => "element",
        _ => "value",
    };

    /// <summary>Rewrites the footer of <paramref name="bytes"/> so its <paramref name="columnName"/> leaf is
    /// annotated as a <b>LEGACY, <c>ConvertedType</c>-ONLY</b> Parquet TIME column: <c>converted_type</c> is
    /// set to <paramref name="convertedType"/> and <c>logicalType</c> is CLEARED. This is the shape
    /// parquet-mr ≤1.10, Hive, Impala and older Spark emit (they all predate the <c>logicalType</c> union),
    /// and it is trivially forgeable on a foreign file.
    /// <para>It CANNOT be produced by Parquet.Net's writer — the writer rebuilds every <c>SchemaElement</c>
    /// from the <c>DataField</c>'s CLR/annotation shape and always stamps a <c>logicalType</c> for TIME (and
    /// discards any <c>SchemaElement</c> mutation made on a constructed field, whose <c>SchemaElement</c> is
    /// null before a footer round-trip). So author it the same way the other forged-footer fixtures here do:
    /// reopen, mutate the parsed <c>FileMetaData</c>, and re-serialize with Parquet.Net's own Thrift writer.
    /// The result reopens cleanly, and Parquet.Net 6.1 materializes it as a PLAIN <c>DataField</c> with a raw
    /// <c>int</c>/<c>long</c> ClrType — i.e. <c>field is TimeDataField</c> is FALSE — which is exactly why
    /// the fail-closed guard cannot key on <c>TimeDataField</c> alone.</para></summary>
    public static async Task<byte[]> ForgeConvertedTypeOnlyTimeAsync(
        byte[] bytes, string columnName, global::Parquet.Meta.ConvertedType convertedType)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                foreach (global::Parquet.Meta.SchemaElement element in metadata.Schema)
                {
                    if (string.Equals(element.Name, columnName, StringComparison.Ordinal))
                    {
                        element.ConvertedType = convertedType;
                        element.LogicalType = null;
                    }
                }

                newFooter = SerializeFooter(metadata);
            }
        }

        return SpliceFooter(bytes, newFooter);
    }

    /// <summary>Replaces the footer of <paramref name="bytes"/> with <paramref name="newFooter"/>, rewriting
    /// the trailing footer length and PAR1 magic so the result reopens as a valid Parquet file.</summary>
    private static byte[] SpliceFooter(byte[] bytes, byte[] newFooter)
    {
        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>Writes a single-column Parquet file whose one column is a plain physical <see cref="int"/>
    /// (<paramref name="millis"/>) or <see cref="long"/> named <paramref name="columnName"/> — the carrier
    /// this file's <see cref="ForgeConvertedTypeOnlyTimeAsync"/> re-annotates into a legacy TIME column.
    /// Written unannotated so the forge is the ONLY thing that makes it a TIME.</summary>
    public static async Task<byte[]> WriteRawIntegralColumnAsync(string columnName, bool millis)
    {
        global::Parquet.Schema.DataField field = millis
            ? new global::Parquet.Schema.DataField<int>(columnName)
            : new global::Parquet.Schema.DataField<long>(columnName);
        var schema = new global::Parquet.Schema.ParquetSchema(field);
        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            if (millis)
            {
                await rowGroup.WriteAsync<int>(
                    field, new ReadOnlyMemory<int>(new[] { 1 }), null, null, CancellationToken.None);
            }
            else
            {
                await rowGroup.WriteAsync<long>(
                    field, new ReadOnlyMemory<long>(new[] { 1L }), null, null, CancellationToken.None);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Writes a single-column Parquet file holding a well-formed
    /// <c>DECIMAL(<paramref name="precision"/>, <paramref name="scale"/>)</c> column named
    /// <paramref name="columnName"/>. At the default <c>DECIMAL(10, 2)</c> it is the carrier
    /// <see cref="ForgeDecimalPrecisionAsync"/> re-annotates into an out-of-range decimal; the explicit
    /// precision overload authors an IN-range decimal AT the cap, pinning that the fail-closed range check
    /// rejects only what is genuinely unrepresentable.</summary>
    public static async Task<byte[]> WriteDecimalColumnAsync(
        string columnName,
        int precision = 10,
        int scale = 2)
    {
        var field = new global::Parquet.Schema.DecimalDataField(columnName, precision, scale);
        var schema = new global::Parquet.Schema.ParquetSchema(field);
        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            await rowGroup.WriteAsync<decimal>(
                field, new ReadOnlyMemory<decimal>(new[] { 1.23m }), null, null, CancellationToken.None);
        }

        return stream.ToArray();
    }

    /// <summary>Rewrites the footer of <paramref name="bytes"/> so its <paramref name="columnName"/> DECIMAL
    /// leaf declares <paramref name="precision"/> — used to author a precision ABOVE DeltaSharp's Spark-parity
    /// cap of 38. A footer may legally declare more (Arrow's <c>decimal256</c> emits up to 76, and a hostile
    /// footer can declare anything), and Parquet.Net happily materializes a <c>DecimalDataField</c> for it, so
    /// DeltaSharp's mapping must reject it rather than let <c>DecimalType</c>'s range validation throw.
    /// <para>Both the legacy <c>SchemaElement.Precision</c> and the <c>logicalType.DECIMAL</c> precision are
    /// rewritten so the two annotations agree. Forged post-write for the same reason
    /// <see cref="ForgeConvertedTypeOnlyTimeAsync"/> is: Parquet.Net's writer rebuilds every SchemaElement
    /// from the DataField.</para>
    /// <para>The sibling out-of-range shapes are NOT authorable and need no fixture: Parquet.Net itself
    /// rejects <c>scale &gt; precision</c> and <c>precision &lt; 1</c>, at both footer parse and
    /// <c>DecimalDataField</c> construction. The mapping still guards them, as defense in depth against a
    /// future Parquet.Net that relaxes those checks.</para></summary>
    public static async Task<byte[]> ForgeDecimalPrecisionAsync(byte[] bytes, string columnName, int precision)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                foreach (global::Parquet.Meta.SchemaElement element in metadata.Schema)
                {
                    if (string.Equals(element.Name, columnName, StringComparison.Ordinal))
                    {
                        element.Precision = precision;
                        if (element.LogicalType?.DECIMAL is not null)
                        {
                            element.LogicalType.DECIMAL.Precision = precision;
                        }
                    }
                }

                newFooter = SerializeFooter(metadata);
            }
        }

        return SpliceFooter(bytes, newFooter);
    }

    /// <summary>Authors an int→int map Parquet file at the LOW level, writing the key and value leaves with
    /// caller-supplied repetition levels — the only way to forge a map whose value repetition stream diverges
    /// from the key's (same total entry count, different per-row distribution), which the typed
    /// <c>ParquetSerializer</c> can never emit (it shares the one <c>key_value</c> group, so key/value reps are
    /// always identical). Definition levels are DERIVED from the nullable value arrays (present vs null), so
    /// this helper authors only maps whose every row has ≥1 present entry (no empty/null-map rows — those are
    /// covered by the serializer-based tests). Used to prove the reader rejects a cross-row value mis-pairing
    /// (F1) yet still accepts a well-formed matching stream.</summary>
    public static async Task<byte[]> WriteIntMapWithRepLevelsAsync(
        int?[] ids, int?[] keys, int[] keyRep, int?[] values, int[] valueRep)
    {
        var mapField = new global::Parquet.Schema.MapField(
            "M",
            new global::Parquet.Schema.DataField<int>("key"),
            new global::Parquet.Schema.DataField<int?>("value"));
        var schema = new global::Parquet.Schema.ParquetSchema(
            new global::Parquet.Schema.DataField<int>("Id"), mapField);
        global::Parquet.Schema.DataField[] leaves = schema.GetDataFields();

        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            await rowGroup.WriteAsync<int>(leaves[0], new ReadOnlyMemory<int?>(ids), null, null, CancellationToken.None);
            await rowGroup.WriteAsync<int>(leaves[1], new ReadOnlyMemory<int?>(keys), keyRep, null, CancellationToken.None);
            await rowGroup.WriteAsync<int>(leaves[2], new ReadOnlyMemory<int?>(values), valueRep, null, CancellationToken.None);
        }

        return stream.ToArray();
    }

    /// <summary>Authors a struct whose scalar field is a 1-level LEGACY REPEATED primitive — a
    /// <c>DataField</c> with <c>isArray=true</c>, which round-trips as a leaf <c>DataField</c> with
    /// <c>MaxRepetitionLevel=1</c> directly under a struct. The typed <c>ParquetSerializer</c> never emits
    /// this (it models a nested collection as a 3-level <c>ListField</c>, caught earlier as "file column is
    /// itself nested"), so this low-level writer is the only way to author the R8 struct-field maxRep
    /// masquerade: requesting this column as <c>struct&lt;A: scalar&gt;</c> navigates the reader to a scalar
    /// struct field whose file leaf is repeated (its N element occurrences would pose as N struct rows if the
    /// repetition stream were ignored). <paramref name="fieldRep"/> supplies the repeated field's repetition
    /// levels (definition levels derive from the nullable value array).</summary>
    public static async Task<byte[]> WriteStructWithRepeatedFieldAsync(int?[] ids, int?[] fieldValues, int[] fieldRep)
    {
        var repeatedField = new global::Parquet.Schema.DataField("A", typeof(int), isArray: true);
        var structField = new global::Parquet.Schema.StructField("S", repeatedField);
        var schema = new global::Parquet.Schema.ParquetSchema(
            new global::Parquet.Schema.DataField<int>("Id"), structField);
        global::Parquet.Schema.DataField[] leaves = schema.GetDataFields();

        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            await rowGroup.WriteAsync<int>(leaves[0], new ReadOnlyMemory<int?>(ids), null, null, CancellationToken.None);
            await rowGroup.WriteAsync<int>(
                leaves[1], new ReadOnlyMemory<int?>(fieldValues), fieldRep, null, CancellationToken.None);
        }

        return stream.ToArray();
    }

    /// <summary>Reads all row-group batches through <see cref="ParquetFileReader"/>.</summary>
    public static async Task<List<ColumnBatch>> ReadAllAsync(
        byte[] bytes,
        StructType readSchema,
        ParquetFileReader.RowGroupPredicate? keepRowGroup = null,
        bool allowTypeWideningPromotion = false)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
            stream, readSchema, keepRowGroup, nullFillMissingColumns: false, allowTypeWideningPromotion, CancellationToken.None))
        {
            batches.Add(batch);
        }

        return batches;
    }

    /// <summary>XOR-poisons every byte of the compressed column chunk for
    /// (<paramref name="rowGroup"/>, <paramref name="columnIndex"/>) in place, corrupting only that one
    /// chunk while leaving the footer, other columns, and other row groups intact.</summary>
    public static async Task<byte[]> PoisonColumnChunkAsync(byte[] bytes, int rowGroup, int columnIndex)
    {
        (long start, long length) = await ChunkRegionAsync(bytes, rowGroup, columnIndex);
        var poisoned = (byte[])bytes.Clone();
        for (long i = start; i < start + length; i++)
        {
            poisoned[i] ^= 0xFF;
        }

        return poisoned;
    }

    private static async Task<(long Start, long Length)> ChunkRegionAsync(byte[] bytes, int rowGroup, int columnIndex)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        await using (reader.ConfigureAwait(false))
        {
            global::Parquet.Meta.ColumnMetaData meta = reader.Metadata!.RowGroups[rowGroup].Columns[columnIndex].MetaData!;
            long start = meta.DictionaryPageOffset ?? meta.DataPageOffset;
            return (start, meta.TotalCompressedSize);
        }
    }

    /// <summary>Rewrites the footer of <paramref name="bytes"/> so that
    /// (<paramref name="rowGroup"/>, <paramref name="columnIndex"/>)'s column chunk declares an inflated
    /// <c>TotalUncompressedSize</c> — a forged decompression-bomb file whose physical bytes are unchanged
    /// but whose metadata claims an implausible decode target. The result reopens cleanly through
    /// Parquet.Net, so a <see cref="ParquetFileReader"/> read must reject it via its decode ceiling rather
    /// than attempting the (impossible) allocation. Re-serializes the parsed <c>FileMetaData</c> with
    /// Parquet.Net's own Thrift writer (reached by reflection) and splices it back as a valid footer.</summary>
    public static async Task<byte[]> ForgeColumnUncompressedSizeAsync(
        byte[] bytes, int rowGroup, int columnIndex, long inflatedUncompressedSize)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                metadata.RowGroups[rowGroup].Columns[columnIndex].MetaData!.TotalUncompressedSize =
                    inflatedUncompressedSize;
                newFooter = SerializeFooter(metadata);
            }
        }

        // Splice: original bytes up to the old footer, then the forged footer, its little-endian length,
        // and the trailing "PAR1" magic — the layout Parquet.Net expects at the tail of the file.
        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>Rewrites the footer so that <paramref name="rowGroup"/>'s <c>NumRows</c> declares
    /// <paramref name="forgedNumRows"/> instead of its true row count — an attacker-controlled footer field
    /// (the physical data pages are untouched). The file reopens cleanly through Parquet.Net, so a
    /// <see cref="ParquetFileReader"/> read must reject the implausible row count via its eager-decode ceiling
    /// (A1) BEFORE any rowCount-scaled allocation, rather than materializing a giant offsets/nulls buffer.
    /// Mirrors <see cref="ForgeColumnUncompressedSizeAsync"/>, mutating only the row group's NumRows.</summary>
    public static async Task<byte[]> ForgeRowGroupNumRowsAsync(byte[] bytes, int rowGroup, long forgedNumRows)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                metadata.RowGroups[rowGroup].NumRows = forgedNumRows;
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>Reads the declared <c>NumRows</c> of row group <paramref name="rowGroup"/> from the footer —
    /// so a test can forge a value RELATIVE to the file's real row count (e.g. <c>actual + 1</c>) without
    /// hard-coding a fixture-dependent number.</summary>
    public static async Task<long> RowGroupNumRowsAsync(byte[] bytes, int rowGroup)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        await using (reader.ConfigureAwait(false))
        {
            return reader.Metadata!.RowGroups[rowGroup].NumRows;
        }
    }

    /// <summary>Rewrites the footer so that (<paramref name="rowGroup"/>, <paramref name="columnIndex"/>)'s
    /// column-chunk <c>Statistics</c> carry a deliberately TOO-SHORT <c>MaxValue</c> blob (fewer bytes than the
    /// column's fixed-width physical type needs — e.g. 3 bytes for an INT64 that needs 8). The footer still
    /// parses (the file OPENS cleanly) and the physical data pages are untouched — but Parquet.Net's eager typed
    /// min/max decode throws a raw <see cref="ArgumentException"/> while reading the blob. That decode is reached
    /// BOTH by <c>RowGroupStatistics.GetStatistics</c> on the predicate-pushdown pruning path AND by
    /// <c>ReadColumnStatistics</c> inside a normal column read, so both must fail closed — but the pruning-path
    /// construction used to run OUTSIDE the reader's fail-closed try, so only IT leaked the raw BCL exception. A
    /// <see cref="ParquetFileReader"/> read must map it to a deterministic CorruptData (PDX-T crafted/lying
    /// stats; storage-delta-architecture.md §5.4 C-DECODE). Mirrors <see cref="ForgeColumnUncompressedSizeAsync"/>,
    /// mutating only the column's statistics blob.</summary>
    public static async Task<byte[]> ForgeShortColumnStatisticsAsync(byte[] bytes, int rowGroup, int columnIndex)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                global::Parquet.Meta.Statistics statistics =
                    metadata.RowGroups[rowGroup].Columns[columnIndex].MetaData!.Statistics
                    ?? throw new InvalidOperationException("column chunk carries no Statistics to corrupt");
                statistics.MaxValue = new byte[] { 0x01, 0x02, 0x03 };
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>Forges the footer schema element named <paramref name="targetFieldName"/> to
    /// <paramref name="forgedName"/> (e.g. an empty string) and re-serializes the footer. Used to prove the
    /// schema-mapping decode boundary fails closed: <c>ParquetTypeMapping.ToDataSchema</c> eagerly builds a
    /// DeltaSharp <c>StructField</c> from EVERY footer field, so an empty field name makes the StructField
    /// constructor raise a raw <see cref="ArgumentException"/> — a <see cref="ParquetFileReader"/> schema read
    /// must map it to a deterministic CorruptData (crafted schema; storage-delta-architecture.md §5.4 C-DECODE).
    /// The file reopens cleanly through Parquet.Net (an empty name is a valid thrift string). Mirrors
    /// <see cref="ForgeShortColumnStatisticsAsync"/>, mutating only the named schema element's name.</summary>
    public static async Task<byte[]> ForgeFieldNameAsync(byte[] bytes, string targetFieldName, string forgedName)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                global::Parquet.Meta.SchemaElement element =
                    metadata.Schema.FirstOrDefault(e => e.Name == targetFieldName)
                    ?? throw new InvalidOperationException($"no footer schema element named '{targetFieldName}'");
                element.Name = forgedName;
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>Rewrites the footer so the schema element for (<paramref name="rowGroup"/>,
    /// <paramref name="columnIndex"/>) — an ordinary physical INT32 column — is annotated as a logical DATE
    /// (BOTH the legacy <c>ConvertedType.DATE</c> and the modern <c>LogicalType.DATE</c>, since Parquet.Net
    /// 6.1.0 keys on either). The physical pages are untouched, so a raw INT32 value the writer emitted (e.g.
    /// <c>int.MaxValue</c> days) now decodes through Parquet.Net's INT32-DATE → <see cref="DateTime"/> path
    /// (<c>epoch.AddDays</c>), whose <see cref="ArgumentOutOfRangeException"/> for an out-of-representable-range
    /// day drives <c>ReadValueAsync</c>'s date/time-range fail-closed catch (#653: the surfaced CorruptData
    /// message must NOT echo the file-derived physical column name). The file OPENS cleanly (a valid DATE
    /// annotation). Mirrors <see cref="ForgeFieldNameAsync"/>, mutating only the named schema element's
    /// logical-type annotation.</summary>
    public static async Task<byte[]> ForgeColumnConvertedTypeToDateAsync(byte[] bytes, int rowGroup, int columnIndex)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                global::Parquet.Meta.ColumnChunk chunk = metadata.RowGroups[rowGroup].Columns[columnIndex];
                string leafName = chunk.MetaData!.PathInSchema[^1];
                global::Parquet.Meta.SchemaElement element =
                    metadata.Schema.FirstOrDefault(e => e.Name == leafName)
                    ?? throw new InvalidOperationException($"no footer schema element named '{leafName}'");
                element.ConvertedType = global::Parquet.Meta.ConvertedType.DATE;
                element.LogicalType = new global::Parquet.Meta.LogicalType { DATE = new global::Parquet.Meta.DateType() };
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>Constructs a minimal Parquet Modular Encryption (encrypted-footer mode) input: the
    /// <c>PARE</c> magic (0x50 0x41 0x52 0x45) at BOTH the head and tail (per the Parquet format Encryption
    /// spec), bracketing an opaque encrypted-footer body. Parquet.Net 6.1.0 rejects the <c>PARE</c> head at
    /// open with <c>IOException "not a parquet file, head: 50415245, tail: 50415245"</c> — the same path a
    /// real pyarrow-emitted encrypted table trips (the library can neither read nor WRITE encrypted files, so
    /// this hand-crafted shape is the only way to author the fixture). Enough to drive the reader's encryption
    /// classifier (#649): a <see cref="ParquetFileReader"/> read must map it to
    /// <see cref="StorageErrorKind.UnsupportedFeature"/>, not <see cref="StorageErrorKind.CorruptData"/>.</summary>
    public static byte[] EncryptedFooterMagicFile()
    {
        using var stream = new MemoryStream();
        stream.Write("PARE"u8);
        stream.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 }); // opaque encrypted-footer body
        stream.Write(BitConverter.GetBytes(8)); // little-endian footer length
        stream.Write("PARE"u8);
        return stream.ToArray();
    }

    /// <summary>The corruption-precision SIBLING of <see cref="EncryptedFooterMagicFile"/>: a genuinely
    /// CORRUPT file that ALSO fails at open (like the encrypted file), differing ONLY in its magic — it
    /// carries the ordinary plaintext <c>PAR1</c> magic at both ends but a garbage footer body, so
    /// Parquet.Net rejects it with a <c>ThriftProtocolException</c>. This isolates the encryption classifier's
    /// precision (#649): a <c>PAR1</c> head is NOT encryption, so a <see cref="ParquetFileReader"/> read must
    /// keep classifying this as <see cref="StorageErrorKind.CorruptData"/> — only a <c>PARE</c> head becomes
    /// <see cref="StorageErrorKind.UnsupportedFeature"/>.</summary>
    public static byte[] Par1MagicGarbageFooterFile()
    {
        using var stream = new MemoryStream();
        stream.Write("PAR1"u8);
        stream.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 }); // garbage (non-Thrift) footer body
        stream.Write(BitConverter.GetBytes(8));
        stream.Write("PAR1"u8);
        return stream.ToArray();
    }

    /// <summary>A corrupt/truncated file carrying ONLY the leading <c>PARE</c> magic (no trailing magic) — the
    /// precision SIBLING that proves the encryption classifier requires <c>PARE</c> at BOTH ends (#649,
    /// council R1). A complete encrypted-footer file is bracketed by <c>PARE</c>; this half-bracketed shape is
    /// genuinely corrupt, so a <see cref="ParquetFileReader"/> read must keep it
    /// <see cref="StorageErrorKind.CorruptData"/>, never <see cref="StorageErrorKind.UnsupportedFeature"/>.</summary>
    public static byte[] PareHeadOnlyFile()
    {
        using var stream = new MemoryStream();
        stream.Write("PARE"u8);
        stream.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 }); // opaque body
        stream.Write(BitConverter.GetBytes(8));
        stream.Write("GARB"u8); // NON-'PARE' tail — an incomplete/corrupt encrypted file, not a complete one
        return stream.ToArray();
    }

    /// <summary>A minimal <c>PARE</c>-prefixed input that is TRUNCATED to just the leading magic (4 bytes) —
    /// too short to be bracketed by a trailing <c>PARE</c>. Genuinely corrupt: the classifier must keep it
    /// <see cref="StorageErrorKind.CorruptData"/> (#649 precision, council R1).</summary>
    public static byte[] PareHeadTruncatedFile() => "PARE"u8.ToArray();

    /// <summary>Constructs a <b>plaintext-footer</b> Parquet Modular Encryption fixture (#655) from a normal
    /// Parquet <paramref name="bytes"/> file: reopens it, sets <c>FileMetaData.EncryptionAlgorithm</c> (Thrift
    /// field 8 — per the format spec "set only in encrypted files with plaintext footer") to an empty
    /// AES-GCM-V1 algorithm, re-serializes the footer with Parquet.Net's own Thrift writer, and splices it
    /// back. The file keeps the ordinary <c>PAR1</c> magic and its footer parses cleanly, so
    /// <see cref="ParquetReader.CreateAsync(System.IO.Stream, ParquetOptions?, bool, CancellationToken)"/>
    /// opens it and populates <c>reader.Metadata.EncryptionAlgorithm</c> — exactly the mode-b shape the reader
    /// must classify as <see cref="StorageErrorKind.UnsupportedFeature"/>. (The column pages are left as
    /// ordinary plaintext, which is irrelevant: classification is footer-presence-based and occurs at open
    /// time, before any column is materialized.)</summary>
    public static async Task<byte[]> PlaintextFooterEncryptedFileAsync(byte[] bytes)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                metadata.EncryptionAlgorithm = new global::Parquet.Meta.EncryptionAlgorithm
                {
                    AESGCMV1 = new global::Parquet.Meta.AesGcmV1(),
                };
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>The SURVIVOR-#4 sibling of <see cref="PlaintextFooterEncryptedFileAsync"/>: sets the file-level
    /// <c>EncryptionAlgorithm</c> (so the failure-path footer probe classifies it encrypted) AND nulls the
    /// <c>Type</c> of the first leaf <c>SchemaElement</c>. A null leaf type re-serializes into a footer that
    /// <see cref="ParquetReader.CreateAsync(System.IO.Stream, ParquetOptions?, bool, CancellationToken)"/>
    /// still OPENS (the reader is constructed and <c>reader.Metadata</c> is populated), but whose high-level
    /// <c>reader.Schema</c> materialization THROWS (Parquet.Net 6.1.0: "cannot decode schema for field ...").
    /// This is the ONE shape that reaches <c>DeltaCheckpointReader.OpenAsync</c>'s failure-path catch with a
    /// <b>non-null</b> reader — so it is the only fixture that exercises the "classify BEFORE dispose" ordering
    /// (#717 survivor 4). Both checkpoint failure-path encryption fixtures make <c>CreateAsync</c> itself throw
    /// (reader stays null, no dispose runs), which is exactly why that ordering survived mutation. Because the
    /// footer carries a non-empty <c>encryption_algorithm</c> union with all four required FileMetaData fields
    /// intact, the raw footer probe classifies it <see cref="StorageErrorKind.UnsupportedFeature"/> — but only
    /// if it runs while the input stream is still open.</summary>
    public static async Task<byte[]> PlaintextFooterEncryptedButSchemaThrowsFileAsync(byte[] bytes)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                metadata.EncryptionAlgorithm = new global::Parquet.Meta.EncryptionAlgorithm
                {
                    AESGCMV1 = new global::Parquet.Meta.AesGcmV1(),
                };
                global::Parquet.Meta.SchemaElement leaf =
                    metadata.Schema.FirstOrDefault(e => e.Type is not null)
                    ?? throw new InvalidOperationException("no leaf schema element with a physical type to null");
                leaf.Type = null; // Opens fine; reader.Schema then throws on the un-typed leaf.
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>The per-column SIBLING of <see cref="PlaintextFooterEncryptedFileAsync"/>: leaves the
    /// file-level <c>EncryptionAlgorithm</c> UNSET and instead marks the single column chunk
    /// (<paramref name="rowGroup"/>, <paramref name="columnIndex"/>) with <c>ColumnCryptoMetaData</c> — the
    /// "only a SUBSET of columns encrypted" shape a plaintext-footer file may take. The reader must still
    /// classify it as <see cref="StorageErrorKind.UnsupportedFeature"/>.</summary>
    public static async Task<byte[]> PlaintextFooterColumnCryptoFileAsync(byte[] bytes, int rowGroup, int columnIndex)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                metadata.RowGroups[rowGroup].Columns[columnIndex].CryptoMetadata =
                    new global::Parquet.Meta.ColumnCryptoMetaData
                    {
                        ENCRYPTIONWITHFOOTERKEY = new global::Parquet.Meta.EncryptionWithFooterKey(),
                    };
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>Constructs the <b>realistic</b> plaintext-footer encryption shape (#655): a real encrypting
    /// writer sets the file-level <c>EncryptionAlgorithm</c>, marks the encrypted column
    /// (<paramref name="rowGroup"/>, <paramref name="columnIndex"/>) with <c>ColumnCryptoMetaData</c>, and
    /// <b>OMITS</b> that column's plaintext <c>ColumnMetaData</c> (it is stored encrypted, not in the plaintext
    /// footer). That omission is what makes Parquet.Net 6.1.0 throw during <c>CreateAsync</c>'s row-group-reader
    /// init — so this fixture exercises the <b>failure-path</b> footer probe, not the success-path
    /// <c>reader.Metadata</c> check. Unlike <see cref="PlaintextFooterEncryptedFileAsync"/> (which keeps
    /// <c>ColumnMetaData</c> and so opens cleanly), this is the shape a genuine encryptor produces.</summary>
    public static async Task<byte[]> PlaintextFooterEncryptedRealisticFileAsync(byte[] bytes, int rowGroup, int columnIndex)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                metadata.EncryptionAlgorithm = new global::Parquet.Meta.EncryptionAlgorithm
                {
                    AESGCMV1 = new global::Parquet.Meta.AesGcmV1(),
                };
                metadata.RowGroups[rowGroup].Columns[columnIndex].CryptoMetadata =
                    new global::Parquet.Meta.ColumnCryptoMetaData
                    {
                        ENCRYPTIONWITHFOOTERKEY = new global::Parquet.Meta.EncryptionWithFooterKey(),
                    };
                metadata.RowGroups[rowGroup].Columns[columnIndex].MetaData = null;
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>The <c>AES_GCM_CTR_V1</c> sibling of <see cref="PlaintextFooterEncryptedFileAsync"/> (which
    /// uses <c>AES_GCM_V1</c>). <c>parquet.thrift</c> defines the <c>EncryptionAlgorithm</c> union with exactly
    /// these two members, and since #698 review FIX 4 the classifier accepts a union only if one of them is
    /// set — so this fixture pins the SECOND disjunct, which the <c>AES_GCM_V1</c> fixtures alone leave
    /// unguarded against a future edit dropping it (#698 review FIX 7).</summary>
    public static async Task<byte[]> PlaintextFooterEncryptedCtrFileAsync(byte[] bytes)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                metadata.EncryptionAlgorithm = new global::Parquet.Meta.EncryptionAlgorithm
                {
                    AESGCMCTRV1 = new global::Parquet.Meta.AesGcmCtrV1(),
                };
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>The ZERO-COLUMN-CHUNK variant of <see cref="EmptyEncryptionAlgorithmUnionFileAsync"/>: sets a
    /// non-null <c>EncryptionAlgorithm</c> whose known union members are BOTH null — the shape an unknown
    /// future algorithm id takes, since Parquet.Net 6.1.0 silently drops a union member it cannot
    /// deserialize — and ALSO clears <c>RowGroups</c>, so the footer carries no column chunk at all. That
    /// combination empties the per-column <c>CryptoMetadata</c> backstop: with no columns there is nothing to
    /// mark, so "the spec mandates crypto_metadata on every encrypted column" becomes vacuously true and
    /// cannot vouch for the file. Both doors must therefore fall back to bare presence and still classify it
    /// <see cref="StorageErrorKind.UnsupportedFeature"/> rather than reading it as ordinary plaintext
    /// (#698 gate finding).</summary>
    public static async Task<byte[]> EmptyEncryptionAlgorithmUnionNoRowGroupsFileAsync(byte[] bytes)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                metadata.EncryptionAlgorithm = new global::Parquet.Meta.EncryptionAlgorithm();
                metadata.RowGroups = new List<global::Parquet.Meta.RowGroup>();
                metadata.NumRows = 0;
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>The EMPTY-UNION sibling of <see cref="PlaintextFooterEncryptedFileAsync"/>: sets the
    /// file-level <c>EncryptionAlgorithm</c> to a union with NEITHER member set. This is not a shape any real
    /// encryptor produces — per <c>parquet.thrift</c> the union always carries exactly one of
    /// <c>AES_GCM_V1</c>/<c>AES_GCM_CTR_V1</c> — but it IS the shape a corrupt footer parses into (every
    /// single-bit-flip false positive observed in the checkpoint fuzz corpus was an empty union). Everything
    /// else in the footer is left intact, so the file opens cleanly AND its schema materializes: the
    /// schema-first ordering therefore does NOT reject it, which is what makes this fixture a guard on the
    /// union-non-empty rule ALONE (#698 review FIX 4). Both doors must classify it as corruption, not
    /// <see cref="StorageErrorKind.UnsupportedFeature"/>.</summary>
    public static async Task<byte[]> EmptyEncryptionAlgorithmUnionFileAsync(byte[] bytes)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                metadata.EncryptionAlgorithm = new global::Parquet.Meta.EncryptionAlgorithm();
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>The UNKNOWN-MEMBER sibling of <see cref="EmptyEncryptionAlgorithmUnionFileAsync"/>: forges a
    /// file-level <c>EncryptionAlgorithm</c> whose union member is a <b>third, unknown</b> id (3) that this
    /// Parquet.Net version cannot deserialize. Built by serializing a real <c>AES_GCM_V1</c> member (union
    /// field id 1, Thrift-compact header <c>0x1C</c> = delta 1, struct type) and diff-patching that member
    /// header to id 3 (<c>0x3C</c>) — the byte-level "unknown union member" the #698 council byte-patched
    /// empirically. Parquet.Net's reader <c>SkipField</c>s the unrecognized id, so it OPENS the file cleanly
    /// with a non-null <c>EncryptionAlgorithm</c> whose <c>AESGCMV1</c>/<c>AESGCMCTRV1</c> are BOTH null — the
    /// exact residual shape from #773. This is distinct from the empty-union corruption shape at the RAW
    /// footer level (field-8 is NON-EMPTY here, EMPTY there), which is how the classifier separates them.
    /// Columns are left intact and carry no <c>crypto_metadata</c>, so the parsed classifier's per-column and
    /// bare-presence arms do not fire.</summary>
    public static async Task<byte[]> UnknownEncryptionAlgorithmUnionMemberFileAsync(byte[] bytes)
    {
        byte[] aesFooter = await SerializeFooterWithAlgorithmAsync(bytes, aesMember: true);
        byte[] emptyFooter = await SerializeFooterWithAlgorithmAsync(bytes, aesMember: false);

        // The only difference between the two footers is the AES_GCM_V1 member inside field-8; the first
        // divergence is that member's Thrift-compact header (0x1C). Patch its id nibble 1 -> 3 (0x3C).
        int i = 0;
        int min = Math.Min(aesFooter.Length, emptyFooter.Length);
        while (i < min && aesFooter[i] == emptyFooter[i])
        {
            i++;
        }

        if (i >= aesFooter.Length || aesFooter[i] != 0x1C)
        {
            throw new InvalidOperationException(
                $"expected AES_GCM_V1 member header 0x1C at footer offset {i}, got "
                + (i < aesFooter.Length ? $"0x{aesFooter[i]:X2}" : "<end>"));
        }

        // Belt-and-braces: the divergence on the EMPTY side must be the union's STOP byte (0x00). Pinning both
        // sides proves offset i is genuinely the start of the field-8 union body, ruling out a coincidental
        // 0x1C at an unrelated earlier divergence if the Thrift layout ever shifts.
        if (i >= emptyFooter.Length || emptyFooter[i] != 0x00)
        {
            throw new InvalidOperationException(
                $"expected empty-union STOP 0x00 at footer offset {i}, got "
                + (i < emptyFooter.Length ? $"0x{emptyFooter[i]:X2}" : "<end>"));
        }

        aesFooter[i] = 0x3C; // union member id 1 -> 3 (unknown), type nibble (struct) unchanged

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(aesFooter, 0, aesFooter.Length);
        forged.Write(BitConverter.GetBytes(aesFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    private static async Task<byte[]> SerializeFooterWithAlgorithmAsync(byte[] bytes, bool aesMember)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        await using (reader.ConfigureAwait(false))
        {
            global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
            metadata.EncryptionAlgorithm = aesMember
                ? new global::Parquet.Meta.EncryptionAlgorithm { AESGCMV1 = new global::Parquet.Meta.AesGcmV1() }
                : new global::Parquet.Meta.EncryptionAlgorithm();
            return SerializeFooter(metadata);
        }
    }

    /// <summary>Returns a valid Parquet file identical to <paramref name="bytes"/> except its footer metadata
    /// carries an extra <c>key_value_metadata</c> entry of roughly <paramref name="padBytes"/> bytes, inflating
    /// the total footer_length past a threshold. Used to build the #773 red-team's oversized-footer bypass
    /// fixture: <c>key_value_metadata</c> is FileMetaData field 5 (before field-8 encryption_algorithm), so a
    /// subsequent <see cref="UnknownEncryptionAlgorithmUnionMemberFileAsync"/> patch still locates the field-8
    /// member header as the first divergence — the padding is byte-identical in both serializations.</summary>
    public static async Task<byte[]> PadFooterMetadataAsync(byte[] bytes, int padBytes)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                metadata.KeyValueMetadata ??= new List<global::Parquet.Meta.KeyValue>();
                metadata.KeyValueMetadata.Add(new global::Parquet.Meta.KeyValue
                {
                    Key = "deltasharp.test.padding",
                    Value = new string('A', padBytes),
                });
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>#773 R4 red-team/security fixture: a TYPE-NIBBLE / multi-field-8 desync. The footer carries TWO
    /// field-8 headers — first an EMPTY struct (<c>0x00</c> body), then a BOOLEAN-typed field-8 (explicit id 8,
    /// header <c>0x01 0x10</c>) followed by an unknown member and STOPs (<c>0x3C 0x00 0x00 0x00</c>).
    /// Parquet.Net's <c>FileMetaData.Read</c> dispatches field 8 by id ALONE (ignoring the type nibble) and
    /// always consumes it as a struct via <c>EncryptionAlgorithm.Read</c>, so it re-enters a nested struct and
    /// parses the trailing bytes as a real unknown member — opening the file with a NON-NULL algorithm. A
    /// strict raw walk that gated field-8 on <c>type == struct</c> skipped the boolean-typed second field-8 and
    /// locked in the first (empty) struct — the last-wins desync that read this as plaintext. Splices the
    /// AES-member footer up to the field-8 field header.</summary>
    public static async Task<byte[]> UnknownEncryptionAlgorithmUnionMember_TypeConfusedMultiField8_FileAsync(byte[] bytes)
    {
        byte[] aesFooter = await SerializeFooterWithAlgorithmAsync(bytes, aesMember: true);
        byte[] emptyFooter = await SerializeFooterWithAlgorithmAsync(bytes, aesMember: false);

        int i = 0;
        int min = Math.Min(aesFooter.Length, emptyFooter.Length);
        while (i < min && aesFooter[i] == emptyFooter[i])
        {
            i++;
        }

        if (i >= aesFooter.Length || aesFooter[i] != 0x1C)
        {
            throw new InvalidOperationException(
                $"expected AES_GCM_V1 member header 0x1C at footer offset {i}, got "
                + (i < aesFooter.Length ? $"0x{aesFooter[i]:X2}" : "<end>"));
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var footerBuilder = new MemoryStream();
        footerBuilder.Write(bytes, 0, footerStart);
        // Fields 1..7 up to (but not including) the field-8 FIELD header byte.
        footerBuilder.Write(aesFooter, 0, i - 1);
        // field-8 #1: the original struct header (aesFooter[i-1]) with an EMPTY body (immediate STOP).
        footerBuilder.WriteByte(aesFooter[i - 1]);
        footerBuilder.WriteByte(0x00);
        // field-8 #2: boolean-typed (0x01), explicit id zigzag(8)=0x10, then unknown member 0x3C + STOPs.
        footerBuilder.WriteByte(0x01);
        footerBuilder.WriteByte(0x10);
        footerBuilder.WriteByte(0x3C);
        footerBuilder.WriteByte(0x00); // STOP for the (re-entered) EncryptionAlgorithm struct
        footerBuilder.WriteByte(0x00); // STOP for the top-level FileMetaData struct
        footerBuilder.WriteByte(0x00); // trailing STOP margin

        byte[] withFooter = footerBuilder.ToArray();
        int newFooterLen = withFooter.Length - footerStart;
        using var final = new MemoryStream();
        final.Write(withFooter, 0, withFooter.Length);
        final.Write(BitConverter.GetBytes(newFooterLen), 0, 4);
        final.Write("PAR1"u8);
        return final.ToArray();
    }

    /// <summary>#773 red-team field-id-truncation fixture. Builds a file whose <c>encryption_algorithm</c>
    /// (field 8) header uses an EXPLICIT Thrift-compact field id of <c>65544</c> (<c>0x10008</c>) carrying an
    /// unknown union member. Parquet.Net decodes the explicit id via <c>ReadI16</c> =
    /// <c>(short)ZigzagToInt(...)</c>, which TRUNCATES <c>65544</c> to <c>8</c>, so it parses the field AS
    /// <c>encryption_algorithm</c> (opening the file with a non-null algorithm, both known members null). A
    /// strict raw walk that read the id at full width would see <c>65544 != 8</c> and miss field-8 entirely —
    /// the differential that let this be read as plaintext. Splices the AES-member footer up to the field-8
    /// field header, replaces that header with an explicit-id-65544 struct header + an unknown (id-3) member,
    /// then the required STOP bytes.</summary>
    public static async Task<byte[]> UnknownEncryptionAlgorithmUnionMember_ExplicitFieldId65544_FileAsync(byte[] bytes)
    {
        byte[] aesFooter = await SerializeFooterWithAlgorithmAsync(bytes, aesMember: true);
        byte[] emptyFooter = await SerializeFooterWithAlgorithmAsync(bytes, aesMember: false);

        int i = 0;
        int min = Math.Min(aesFooter.Length, emptyFooter.Length);
        while (i < min && aesFooter[i] == emptyFooter[i])
        {
            i++;
        }

        if (i >= aesFooter.Length || aesFooter[i] != 0x1C)
        {
            throw new InvalidOperationException(
                $"expected AES_GCM_V1 member header 0x1C at footer offset {i}, got "
                + (i < aesFooter.Length ? $"0x{aesFooter[i]:X2}" : "<end>"));
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var footerBuilder = new MemoryStream();
        footerBuilder.Write(bytes, 0, footerStart);
        // Everything up to (but not including) the field-8 FIELD header byte (the byte just before the member).
        footerBuilder.Write(aesFooter, 0, i - 1);
        // field-8 header with explicit field id: type=struct (0x0C), modifier nibble 0 => explicit id follows.
        footerBuilder.WriteByte(0x0C);
        // zigzag varint for field id 65544: zigzag(65544) = 131088 => varint 0x90 0x80 0x08.
        footerBuilder.WriteByte(0x90);
        footerBuilder.WriteByte(0x80);
        footerBuilder.WriteByte(0x08);
        // Unknown union member: field id 3, struct, empty body => header 0x3C, then STOP.
        footerBuilder.WriteByte(0x3C);
        footerBuilder.WriteByte(0x00);
        // STOP for the encryption_algorithm struct, then STOP for the top-level FileMetaData struct.
        footerBuilder.WriteByte(0x00);
        footerBuilder.WriteByte(0x00);

        byte[] withFooter = footerBuilder.ToArray();
        int newFooterLen = withFooter.Length - footerStart;
        using var final = new MemoryStream();
        final.Write(withFooter, 0, withFooter.Length);
        final.Write(BitConverter.GetBytes(newFooterLen), 0, 4);
        final.Write("PAR1"u8);
        return final.ToArray();
    }

    /// <summary>#773 security fixture: an unknown-member file with one required FileMetaData field mistyped —
    /// <c>num_rows</c> (field 3) flipped from its Thrift i64 field header (<c>0x16</c>) to i32 (<c>0x15</c>).
    /// Parquet.Net tolerantly opens it (row counts come from row-group metadata), so the parsed layer still
    /// confirms the algorithm is present; a raw walk that gated on the exact required-field types would then
    /// (pre-fix) read the encrypted file as plaintext. Locates the header by the deterministic <c>0x16</c>
    /// (delta 1, i64) followed by the zigzag varint of the known row count.</summary>
    public static async Task<byte[]> UnknownEncryptionAlgorithmUnionMember_MistypedNumRows_FileAsync(
        byte[] bytes, long rowCount)
    {
        byte[] forged = await UnknownEncryptionAlgorithmUnionMemberFileAsync(bytes);
        int originalFooterLength = BitConverter.ToInt32(forged, forged.Length - 8);
        int footerStart = forged.Length - 8 - originalFooterLength;

        byte zig = (byte)((rowCount << 1) ^ (rowCount >> 63)); // small non-negative counts fit one byte
        int at = -1;
        for (int p = footerStart; p < forged.Length - 8 - 1; p++)
        {
            if (forged[p] == 0x16 && forged[p + 1] == zig)
            {
                at = p;
                break;
            }
        }

        if (at < 0)
        {
            throw new InvalidOperationException(
                $"could not locate num_rows header 0x16 followed by zigzag(0x{zig:X2}) in the footer");
        }

        forged[at] = 0x15; // i64 (0x6) -> i32 (0x5): mistype the required num_rows field
        return forged;
    }

    /// value (e.g. <c>9</c>, which is not a real <c>CompressionCodec</c>) that leaves the footer parseable and
    /// the physical pages untouched, so the file OPENS cleanly (valid <c>PAR1</c> magic), yet Parquet.Net's
    /// page decode raises a raw <see cref="NotSupportedException"/> ("Compression method 9 is not supported.")
    /// when it reaches the chunk. That is CORRUPTION (an invalid codec code), not a valid-but-unsupported
    /// feature — and it is a deterministic member of the same NotSupportedException family a random bit-flip
    /// produces — so a <see cref="ParquetFileReader"/> read must keep mapping it to
    /// <see cref="StorageErrorKind.CorruptData"/> (#649 precision guard: the fix must NOT broaden
    /// NotSupported → UnsupportedFeature). Mirrors <see cref="ForgeColumnUncompressedSizeAsync"/>, mutating
    /// only the column's codec.</summary>
    public static async Task<byte[]> ForgeColumnCompressionCodecAsync(
        byte[] bytes, int rowGroup, int columnIndex, int forgedCodec)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                metadata.RowGroups[rowGroup].Columns[columnIndex].MetaData!.Codec =
                    (global::Parquet.Meta.CompressionCodec)forgedCodec;
                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    /// <summary>Renames a footer LEAF column CONSISTENTLY — both its schema element name AND every column
    /// chunk's <c>PathInSchema</c> tail — so the file stays self-consistent and the reader can still LOCATE
    /// and DECODE the column (unlike <see cref="ForgeFieldNameAsync"/>, which renames only the schema element,
    /// desyncing it from the chunk path and breaking column lookup). Used to author a checkpoint whose map
    /// key / list element LEAF carries an attacker sentinel name that surfaces in the resolved
    /// <c>DataField.Path</c> — proving the checkpoint reconstruction fail-closed messages never echo that
    /// file-derived leaf path (#653). Mirrors <see cref="ForgeFieldNameAsync"/>.</summary>
    /// <summary>Rewrites the footer so the column chunk whose <c>PathInSchema</c> ends with
    /// <paramref name="leafName"/> declares <paramref name="numValues"/> values, leaving the pages untouched.
    /// Drives <c>NestedParquetColumnReader.LeafNumValues</c>' fail-closed guards (negative count, eager-decode
    /// ceiling, Int32 overflow) on a file whose leaf NAME is attacker-chosen — the #653 channel those
    /// messages echo.</summary>
    public static async Task<byte[]> ForgeLeafNumValuesAsync(byte[] bytes, string leafName, long numValues)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                bool found = false;
                foreach (global::Parquet.Meta.RowGroup rowGroup in metadata.RowGroups)
                {
                    foreach (global::Parquet.Meta.ColumnChunk chunk in rowGroup.Columns)
                    {
                        global::Parquet.Meta.ColumnMetaData meta = chunk.MetaData!;
                        if (meta.PathInSchema.Contains(leafName, StringComparer.Ordinal))
                        {
                            meta.NumValues = numValues;
                            found = true;
                        }
                    }
                }

                if (!found)
                {
                    throw new InvalidOperationException($"no column chunk whose path contains '{leafName}'");
                }

                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    public static async Task<byte[]> ForgeLeafColumnNameAsync(byte[] bytes, string targetLeafName, string forgedName)
    {
        byte[] newFooter;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
            await using (reader.ConfigureAwait(false))
            {
                global::Parquet.Meta.FileMetaData metadata = reader.Metadata!;
                global::Parquet.Meta.SchemaElement element =
                    metadata.Schema.FirstOrDefault(e => e.Name == targetLeafName)
                    ?? throw new InvalidOperationException($"no footer schema element named '{targetLeafName}'");
                element.Name = forgedName;
                foreach (global::Parquet.Meta.RowGroup rowGroup in metadata.RowGroups)
                {
                    foreach (global::Parquet.Meta.ColumnChunk chunk in rowGroup.Columns)
                    {
                        List<string> path = chunk.MetaData!.PathInSchema;
                        for (int i = 0; i < path.Count; i++)
                        {
                            if (string.Equals(path[i], targetLeafName, StringComparison.Ordinal))
                            {
                                path[i] = forgedName;
                            }
                        }
                    }
                }

                newFooter = SerializeFooter(metadata);
            }
        }

        int originalFooterLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - originalFooterLength;
        using var forged = new MemoryStream();
        forged.Write(bytes, 0, footerStart);
        forged.Write(newFooter, 0, newFooter.Length);
        forged.Write(BitConverter.GetBytes(newFooter.Length), 0, 4);
        forged.Write("PAR1"u8);
        return forged.ToArray();
    }

    private static byte[] SerializeFooter(global::Parquet.Meta.FileMetaData metadata)
    {
        Assembly parquet = typeof(ParquetReader).Assembly;
        Type writerType = parquet.GetType("Parquet.Meta.Proto.ThriftCompactProtocolWriter", throwOnError: true)!;
        using var footerStream = new MemoryStream();
        object protocolWriter = Activator.CreateInstance(
            writerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { footerStream },
            culture: null)!;
        MethodInfo write = typeof(global::Parquet.Meta.FileMetaData).GetMethod(
            "Write", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        write.Invoke(metadata, new[] { protocolWriter });
        return footerStream.ToArray();
    }
}

/// <summary>The nested position a forged Parquet <b>TIME</b> leaf occupies, for
/// <see cref="ParquetTestHelpers.WriteNestedTimeColumnAsync"/>. Each one flows through a DIFFERENT arm of the
/// nested reader's leaf validation, so all three must be covered independently. Public (unlike the internal
/// helper class that consumes it) only because an xUnit <c>[Theory]</c> parameter cannot be less accessible
/// than the public test method that takes it.</summary>
public enum NestedTimePosition
{
    /// <summary>A TIME field inside a struct.</summary>
    StructField,

    /// <summary>A TIME element inside a list.</summary>
    ArrayElement,

    /// <summary>A TIME value inside a string-keyed map.</summary>
    MapValue,
}
