using System.Collections.Immutable;
using System.Globalization;
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
/// missing <c>schemaString</c>/<c>format</c>, or a value column whose physical type is not the expected
/// one — throws <see cref="DeltaProtocolException"/>. The checkpoint is <b>non-authoritative</b> (design
/// §2.10.3): the caller (<see cref="DeltaLog"/>) treats any such failure as a corrupt checkpoint and falls
/// back to JSON replay from version 0, never inventing state.</para>
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
    /// oversized part fails closed (→ JSON-replay fallback) rather than driving an unbounded allocation. A
    /// streaming/seek-based checkpoint decode that lifts this cap is a tracked follow-up (mirrors the flat
    /// reader's eager-decode stance).</summary>
    internal const long MaxCheckpointPartBytes = 512L * 1024 * 1024;

    /// <summary>The maximum declared row count this reader will decode from a single checkpoint row group
    /// (design §5.4 C-DECODE). A crafted footer inflating the row count fails closed before the eager
    /// per-column allocation.</summary>
    internal const int MaxCheckpointRowGroupRows = 64 * 1024 * 1024;

    /// <summary>
    /// Reads one classic checkpoint Parquet part from <paramref name="stream"/> into its surviving actions,
    /// in row order. The stream is buffered (bounded by <see cref="MaxCheckpointPartBytes"/>) so Parquet's
    /// footer-seek works over any backend stream.
    /// </summary>
    /// <exception cref="DeltaProtocolException">The part is malformed/truncated, exceeds a decode ceiling,
    /// or carries an action row that violates the required Delta action shape (fail closed).</exception>
    public static async Task<IReadOnlyList<DeltaAction>> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        MemoryStream buffer = await BufferAsync(stream, cancellationToken).ConfigureAwait(false);
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
                        await ReadRowGroupAsync(rowGroup, schema, actions, group, cancellationToken).ConfigureAwait(false);
                    }

                    return actions;
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or DeltaProtocolException))
                {
                    // Any lower-level decode failure (a page-level defect a byte-flip introduced past the
                    // footer) is a corrupt checkpoint: fail closed so the caller falls back to JSON replay.
                    throw DeltaProtocolException.Malformed(
                        $"The Delta checkpoint Parquet is malformed: {ex.Message}", ex);
                }
            }
        }
    }

    private static async Task<MemoryStream> BufferAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxCheckpointPartBytes)
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                throw DeltaProtocolException.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A Delta checkpoint part exceeds the {MaxCheckpointPartBytes}-byte decode ceiling."));
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
            throw DeltaProtocolException.Malformed(
                $"The Delta checkpoint Parquet stream is malformed or truncated: {ex.Message}", ex);
        }
    }

    private static async Task ReadRowGroupAsync(
        ParquetRowGroupReader rowGroup,
        CheckpointSchema schema,
        List<DeltaAction> actions,
        int group,
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
}
