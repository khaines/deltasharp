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

    /// <summary>Reads all row-group batches through <see cref="ParquetFileReader"/>.</summary>
    public static async Task<List<ColumnBatch>> ReadAllAsync(
        byte[] bytes,
        StructType readSchema,
        ParquetFileReader.RowGroupPredicate? keepRowGroup = null)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
            stream, readSchema, keepRowGroup, nullFillMissingColumns: false, CancellationToken.None))
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
