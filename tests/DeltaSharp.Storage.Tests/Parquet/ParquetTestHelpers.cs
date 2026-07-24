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

    /// <summary>Constructs a minimal Parquet Modular Encryption (encrypted-footer mode) input: the
    /// <c>PARE</c> magic (0x50 0x41 0x52 0x45) at BOTH the head and tail (per the Parquet format Encryption
    /// spec), bracketing an opaque encrypted-footer body. Parquet.Net 6.0.3 rejects the <c>PARE</c> head at
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

    /// <summary>Rewrites the footer so that (<paramref name="rowGroup"/>, <paramref name="columnIndex"/>)'s
    /// column chunk declares <paramref name="forgedCodec"/> as its compression <c>Codec</c> — an OUT-OF-RANGE
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
