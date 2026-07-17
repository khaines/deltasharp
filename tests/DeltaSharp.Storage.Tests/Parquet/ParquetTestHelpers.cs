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
