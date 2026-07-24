using System.Collections.Immutable;
using System.Globalization;
using DeltaSharp.Storage.Parquet;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Reads a Delta <b>classic checkpoint</b> Parquet part (design §2.10.3) into the typed
/// <see cref="DeltaAction"/> model. A checkpoint stores one surviving action per row, with the action
/// struct as columns (<c>add</c>/<c>remove</c>/<c>metaData</c>/<c>protocol</c>/<c>txn</c>), so this reader
/// is a <b>metadata-reconstruction</b> path — it decodes deeply-nested action structs, maps, and lists
/// directly with Parquet.Net's low-level column API (not the reflection class serializer, and not the
/// FEAT-05.1 flat <see cref="Parquet.ParquetFileReader"/> which fails on nested projection) so it stays
/// trim/AOT-clean (design §2.7 B-F1, ADR-0014).
///
/// <para><b>Nested decode.</b> Each leaf column is read via <see cref="ParquetRowGroupReader"/> raw column
/// data (packed values + definition/repetition levels). Scalars under an optional action struct are
/// row-aligned; maps (<c>partitionValues</c>/<c>tags</c>/<c>configuration</c>/<c>format.options</c>) and
/// lists (<c>partitionColumns</c>/<c>readerFeatures</c>/<c>writerFeatures</c>) are reconstructed from the
/// Dremel levels: an entry exists where the required key / list element is defined
/// (<c>def == MaxDefinitionLevel</c>), and a map value is null where its optional value leaf is
/// under-defined.</para>
///
/// <para><b>Fail closed.</b> Any structural defect — a truncated/malformed Parquet stream, a row that is
/// not exactly one action, an <c>add</c> missing its required <c>path</c>/<c>size</c>, a <c>metaData</c>
/// missing <c>schemaString</c>/<c>format</c>, a value column whose physical type is not the expected
/// one, or a row group whose declared decode footprint exceeds the ceiling — throws
/// <see cref="DeltaProtocolException"/>. The checkpoint is <b>non-authoritative</b> (design §2.10.3): the
/// caller (<see cref="DeltaLog"/>) treats any such failure as a corrupt checkpoint and falls back to JSON
/// replay from version 0, never inventing state.</para>
///
/// <para><b>Forward compatible.</b> Unknown checkpoint columns are ignored and absent optional columns
/// default to null/empty, mirroring <see cref="DeltaLogActionReader"/>'s tolerance — a v1-baseline reader
/// still reconstructs a baseline table, while any feature that would require understanding an unknown
/// column is rejected up front by protocol negotiation (§2.10.5).</para>
/// </summary>
internal static class DeltaCheckpointReader
{
    /// <summary>The maximum number of checkpoint bytes buffered in memory for a single part (design §5.4
    /// C-DECODE). Because a checkpoint is untrusted input and this reader decodes columns eagerly, an
    /// oversized <b>compressed</b> part fails closed (→ JSON-replay fallback) rather than driving an
    /// unbounded read. A streaming/seek-based checkpoint decode that lifts this cap is a tracked follow-up
    /// (mirrors the flat reader's eager-decode stance).</summary>
    internal const long MaxCheckpointPartBytes = 512L * 1024 * 1024;

    /// <summary>The maximum declared row count this reader will decode from a single checkpoint row group
    /// (design §5.4 C-DECODE) — a coarse first-line sanity bound; the authoritative memory guard is
    /// <see cref="MaxCheckpointRowGroupDecodedBytes"/>, which bounds the reader's <b>actual</b> eager
    /// allocation (values + per-slot definition/repetition levels + payload), because a small
    /// <i>compressed</i> RLE/null chunk can still declare a huge row count whose level arrays dominate.</summary>
    internal const int MaxCheckpointRowGroupRows = 16 * 1024 * 1024;

    /// <summary>The absolute per-row-group eager-decode memory ceiling (1&#160;GiB): the sum, over the
    /// columns this reader decodes, of each column's declared value count times its per-slot footprint
    /// (packed value width + the two 4-byte Dremel level ints) plus its decompressed payload, must not
    /// exceed this (design §5.4 C-DECODE). This bounds the reader's transient allocation directly, so a
    /// crafted checkpoint (few compressed bytes, enormous row/value count) fails closed rather than driving
    /// an OOM on the driver; a legitimately large checkpoint spreads across multiple row groups.</summary>
    internal const long MaxCheckpointRowGroupDecodedBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Reads one classic checkpoint Parquet part from <paramref name="stream"/> into its surviving actions,
    /// in row order. The stream is buffered (bounded by <see cref="MaxCheckpointPartBytes"/>) so Parquet's
    /// footer-seek works over any backend stream.
    /// </summary>
    /// <exception cref="DeltaProtocolException">The part is malformed/truncated, exceeds a decode ceiling,
    /// or carries an action row that violates the required Delta action shape (fail closed).</exception>
    public static async Task<IReadOnlyList<DeltaAction>> ReadAsync(
        Stream stream, CancellationToken cancellationToken,
        long maxPartBytes = MaxCheckpointPartBytes, long maxDecodedBytes = MaxCheckpointRowGroupDecodedBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);

        MemoryStream buffer = await BufferAsync(stream, maxPartBytes, cancellationToken).ConfigureAwait(false);
        await using (buffer.ConfigureAwait(false))
        {
            ParquetReader reader = await OpenAsync(buffer, cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                try
                {
                    var schema = CheckpointSchema.Resolve(reader.Schema);
                    var actions = new List<DeltaAction>();
                    for (int group = 0; group < reader.RowGroupCount; group++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(group);
                        await ReadRowGroupAsync(rowGroup, schema, actions, group, maxDecodedBytes, cancellationToken).ConfigureAwait(false);
                    }

                    return actions;
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or DeltaProtocolException))
                {
                    // Any lower-level decode failure (a page-level defect a byte-flip introduced past the
                    // footer) is a corrupt checkpoint: fail closed so the caller falls back to JSON replay.
                    // Fixed message (no ex.Message interpolation): an attacker-controlled checkpoint footer
                    // field name must never echo into the surfaced error text (info-leak parity with the
                    // ParquetFileReader fail-closed boundaries, #651). The cause is preserved as the inner
                    // exception for logs/diagnostics.
                    throw DeltaProtocolException.Malformed(
                        "The Delta checkpoint Parquet is malformed.", ex);
                }
            }
        }
    }

    private static async Task<MemoryStream> BufferAsync(Stream stream, long maxPartBytes, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxPartBytes)
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                throw DeltaProtocolException.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A Delta checkpoint part exceeds the {maxPartBytes}-byte decode ceiling."));
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }

    private static async Task<ParquetReader> OpenAsync(Stream input, CancellationToken cancellationToken)
    {
        try
        {
            return await ParquetReader.CreateAsync(input, null, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DeltaProtocolException))
        {
            // Fixed message (no ex.Message interpolation) so a crafted footer's bytes cannot echo into the
            // error text (info-leak parity with ParquetFileReader, #651); ex kept as the inner exception.
            throw DeltaProtocolException.Malformed(
                "The Delta checkpoint Parquet stream is malformed or truncated.", ex);
        }
    }

    private static async Task ReadRowGroupAsync(
        ParquetRowGroupReader rowGroup,
        CheckpointSchema schema,
        List<DeltaAction> actions,
        int group,
        long maxDecodedBytes,
        CancellationToken cancellationToken)
    {
        long declaredRows = rowGroup.RowCount;
        if (declaredRows < 0 || declaredRows > MaxCheckpointRowGroupRows)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Checkpoint row group {group} declares {declaredRows} rows, outside the supported bound "
                + $"[0, {MaxCheckpointRowGroupRows}]."));
        }

        int rowCount = (int)declaredRows;
        if (rowCount == 0)
        {
            return;
        }

        EnsureDecodeCeiling(rowGroup, schema.LeafFields(), group, maxDecodedBytes);

        var columns = await CheckpointColumns.ReadAsync(rowGroup, schema, rowCount, cancellationToken)
            .ConfigureAwait(false);

        for (int r = 0; r < rowCount; r++)
        {
            DeltaAction? action = columns.BuildAction(r, group);
            if (action is not null)
            {
                actions.Add(action);
            }
        }
    }

    /// <summary>Fails closed when the columns this reader will decode for <paramref name="group"/> would
    /// eagerly allocate more than <paramref name="maxDecodedBytes"/>, or when any column declares a
    /// decompression ratio beyond <see cref="ParquetFileReader.MaxDecompressionRatio"/> — so an untrusted
    /// checkpoint cannot drive an OOM/CPU DoS on the driver (design §5.4 C-DECODE). The bound is on the
    /// reader's <b>actual</b> per-slot footprint (packed value width + the two Dremel level ints) plus the
    /// declared decompressed payload, computed from each column chunk's declared metadata before any decode.
    /// Overflow-safe (saturating).</summary>
    /// <exception cref="DeltaProtocolException">A ceiling is exceeded or a declared size is negative.</exception>
    internal static void EnsureDecodeCeiling(
        ParquetRowGroupReader rowGroup, IReadOnlyList<DataField> leafFields, int group,
        long maxDecodedBytes = MaxCheckpointRowGroupDecodedBytes)
    {
        long totalBytes = 0;
        foreach (DataField field in leafFields)
        {
            if (!rowGroup.ColumnExists(field))
            {
                continue;
            }

            global::Parquet.Meta.ColumnMetaData? meta = rowGroup.GetMetadata(field)?.MetaData;
            if (meta is null)
            {
                continue;
            }

            long numValues = meta.NumValues;
            long compressed = meta.TotalCompressedSize;
            long uncompressed = meta.TotalUncompressedSize;
            totalBytes = SaturatingAdd(
                totalBytes, ColumnFootprintBytes(field.ClrType, numValues, compressed, uncompressed, group));
        }

        if (totalBytes > maxDecodedBytes)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Checkpoint row group {group} would eagerly allocate {totalBytes} bytes across its columns, "
                + $"exceeding the {maxDecodedBytes}-byte decode ceiling."));
        }
    }

    /// <summary>The reader's eager allocation footprint for one column chunk (design §5.4 C-DECODE):
    /// <paramref name="numValues"/> packed slots × (value width + the two 4-byte Dremel level ints) plus the
    /// declared decompressed payload, overflow-saturated. Fails closed on a negative declared size or a
    /// decompression ratio beyond <see cref="ParquetFileReader.MaxDecompressionRatio"/> (a decompression
    /// bomb). The fail-closed messages carry no file-derived token — only the bounded declared scalars
    /// (value/byte counts + the group index), which are attacker-declared int64 footer metadata, not a
    /// byte/text (injection) channel (#653). Pure/arithmetic so the ceiling is unit-testable without a real
    /// Parquet stream.</summary>
    /// <exception cref="DeltaProtocolException">A declared size is negative or the ratio ceiling is exceeded.</exception>
    internal static long ColumnFootprintBytes(
        Type clrType, long numValues, long compressedBytes, long uncompressedBytes, int group)
    {
        if (numValues < 0 || compressedBytes < 0 || uncompressedBytes < 0)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"A checkpoint column (group {group}) declares negative metadata "
                + $"(values {numValues}, compressed {compressedBytes}, decompressed {uncompressedBytes})."));
        }

        // Decompression-ratio ceiling — a chunk claiming far more decompressed than compressed bytes is a
        // decompression bomb. The product is widened to Int128 so a large declared compressed size cannot
        // overflow the 64-bit multiply into a spurious verdict (wrapping past a bomb check).
        if (uncompressedBytes > (Int128)Math.Max(compressedBytes, 1) * ParquetFileReader.MaxDecompressionRatio)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"A checkpoint column (group {group}) declares {uncompressedBytes} decompressed bytes "
                + $"for {compressedBytes} compressed, exceeding the "
                + $"{ParquetFileReader.MaxDecompressionRatio}:1 ratio ceiling."));
        }

        long perSlot = ElementWidth(clrType) + (2 * sizeof(int));
        return SaturatingAdd(SaturatingMul(numValues, perSlot), uncompressedBytes);
    }

    private static int ElementWidth(Type clrType)
    {
        if (clrType == typeof(long))
        {
            return sizeof(long);
        }

        if (clrType == typeof(int))
        {
            return sizeof(int);
        }

        if (clrType == typeof(bool))
        {
            return sizeof(bool);
        }

        // string columns surface as ReadOnlyMemory<char> slots (pointer + two ints); use a 16-byte proxy.
        return 16;
    }

    private static long SaturatingMul(long a, long b)
    {
        if (a == 0 || b == 0)
        {
            return 0;
        }

        long product = unchecked(a * b);
        return (a == product / b && (a ^ b) >= 0) ? product : long.MaxValue;
    }

    private static long SaturatingAdd(long a, long b) => b > long.MaxValue - a ? long.MaxValue : a + b;
}
