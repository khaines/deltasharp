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
/// <see cref="DeltaProtocolException"/>. A checkpoint that is a <b>valid</b> Parquet file DeltaSharp simply
/// cannot read — today only Parquet Modular Encryption, in either footer mode — instead throws
/// <see cref="DeltaStorageException"/> with <see cref="StorageErrorKind.UnsupportedFeature"/> (#681), so the
/// diagnosis is actionable rather than a misleading "malformed" (diagnosability parity with the data-file
/// door, #655). The checkpoint is <b>non-authoritative</b> (design §2.10.3) in BOTH cases: the caller
/// (<see cref="DeltaLog"/>) treats either failure as an unusable checkpoint and falls back to JSON replay
/// from version 0, never inventing state.</para>
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
    /// <exception cref="DeltaStorageException">The part is a valid Parquet file written with a feature
    /// DeltaSharp cannot read — Parquet Modular Encryption, either footer mode
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>, #681) — OR it could not be decoded within the
    /// bounded wall-clock decode ceiling (<see cref="StorageErrorKind.DecodeBudgetExceeded"/>). Both are
    /// distinct from <see cref="DeltaProtocolException"/> so an unreadable-but-VALID or a resource-exhausting
    /// (but not provably corrupt) checkpoint is not reported as a corrupt one; the checkpoint stays
    /// non-authoritative either way (<see cref="DeltaLog"/> falls back to JSON replay).</exception>
    public static async Task<IReadOnlyList<DeltaAction>> ReadAsync(
        Stream stream, CancellationToken cancellationToken,
        long maxPartBytes = MaxCheckpointPartBytes, long maxDecodedBytes = MaxCheckpointRowGroupDecodedBytes,
        TimeSpan? decodeBudget = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Fail fast on a misconfigured budget (positive and within the accepted ceiling) BEFORE any I/O, with
        // an explicit paramName — never as a raw ArgumentOutOfRangeException surfacing mid-decode from
        // Task.Delay. A null budget means "derive a size-aware budget below".
        if (decodeBudget is { } configured)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configured.Ticks, nameof(decodeBudget));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(configured, BoundedDecode.MaxBudget, nameof(decodeBudget));
        }

        // Buffer to an IMMUTABLE byte[] (not a MemoryStream the caller then disposes). Isolation contract: the
        // bounded decode below opens its OWN MemoryStream over these bytes INSIDE the work delegate, so a
        // stranded (detached-past-deadline) decode never touches a caller-owned/pooled stream — it can never
        // observe a disposed buffer nor a rewound position, eliminating the wrong-result race on timeout. The
        // bytes are read-only and shared; only a stranded decode's own MemoryStream pins them (≤ maxPartBytes,
        // the accepted residual).
        byte[] bytes = await BufferAsync(stream, maxPartBytes, cancellationToken).ConfigureAwait(false);

        // Bounded-time decode (#716/#699/#647). A single corrupted byte (the terminal footer STOP flipped,
        // #699; the byte at index 5595 in the #716 minimized repro; a corrupt data-page header, #647) can
        // drive Parquet.Net 6.0.3 into effectively UNBOUNDED work — inside ParquetReader.CreateAsync at
        // open, or inside the synchronous column/page decode — that IGNORES the CancellationToken. A hang is
        // not an exception, so the fail-closed catch inside DecodeBufferedAsync cannot intercept it. Race the
        // WHOLE open+decode against a wall-clock deadline via the shared BoundedDecode policy so a corrupt
        // checkpoint fails closed deterministically (→ the non-authoritative checkpoint is discarded and
        // DeltaLog falls back to JSON replay) rather than stalling the table read indefinitely. On expiry a
        // DecodeBudgetExceeded DeltaStorageException (NOT Malformed: a wall-clock stall is a resource fault,
        // not proven corruption — #649/#655/#681 classification contract) routes DeltaLog to JSON replay under
        // the DecodeTimeout reason; a valid-but-unsupported UnsupportedFeature and cooperative cancellation
        // both finish inside the budget and propagate unwrapped as the work's own outcome.
        return await BoundedDecode.RunAsync(
            decodeToken => DecodeBufferedAsync(bytes, maxDecodedBytes, decodeToken),
            decodeBudget ?? SizeAwareBudget(bytes.Length),
            static _ => DeltaStorageException.DecodeBudgetExceeded(
                "The Delta checkpoint Parquet could not be decoded within the bounded-decode time budget."),
            cancellationToken,
            onAbandonedResult: null,
            timeProvider).ConfigureAwait(false);
    }

    /// <summary>Derives the checkpoint decode budget from the buffered (compressed) part size so a HEALTHY
    /// large part the byte ceilings admit (up to <see cref="MaxCheckpointPartBytes"/> = 512&#160;MiB) is never
    /// discarded merely for taking longer than a flat default, while a crafted part that stalls indefinitely
    /// still trips. Stated FLOOR decode throughput = 8&#160;MiB/s: a legitimate part decodes at least this fast
    /// (real Parquet decode is &gt;&gt; 100&#160;MiB/s), so 512&#160;MiB ⇒ 64&#160;s ≥ the worst case the byte
    /// ceilings permit. The budget is floored at <see cref="BoundedDecode.DefaultBudget"/> (small parts still
    /// get the full default) and capped at <see cref="BoundedDecode.MaxBudget"/>.</summary>
    private static TimeSpan SizeAwareBudget(long compressedBytes)
    {
        const double FloorBytesPerSecond = 8.0 * 1024 * 1024;
        TimeSpan derived = TimeSpan.FromSeconds(compressedBytes / FloorBytesPerSecond);
        if (derived < BoundedDecode.DefaultBudget)
        {
            derived = BoundedDecode.DefaultBudget;
        }

        return derived > BoundedDecode.MaxBudget ? BoundedDecode.MaxBudget : derived;
    }

    // The open + full row-group decode of one buffered checkpoint part (bounded in time by the caller's
    // BoundedDecode.RunAsync). Kept as its own method so the bounded-decode policy wraps a single unit of work
    // whose CancellationToken is the linked (deadline + caller) token. Opens an ISOLATED MemoryStream over the
    // caller-supplied immutable bytes so a stranded decode never shares a caller-owned stream (see ReadAsync).
    private static async Task<IReadOnlyList<DeltaAction>> DecodeBufferedAsync(
        byte[] bytes, long maxDecodedBytes, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream(bytes, writable: false);
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
                catch (Exception ex) when (ex is not (OperationCanceledException or DeltaProtocolException or DeltaStorageException))
                {
                    // Any lower-level decode failure (a page-level defect a byte-flip introduced past the
                    // footer) is a corrupt checkpoint: fail closed so the caller falls back to JSON replay.
                    // Fixed message (no ex.Message interpolation): an attacker-controlled checkpoint footer
                    // field name must never echo into the surfaced error text (info-leak parity with the
                    // ParquetFileReader fail-closed boundaries, #651). The cause is preserved as the inner
                    // exception for logs/diagnostics. A DeltaStorageException (UnsupportedFeature) already
                    // carries its own classification and propagates unwrapped.
                    throw DeltaProtocolException.Malformed(
                        "The Delta checkpoint Parquet is malformed.", ex);
                }
            }
        }
    }

    // Buffers the whole part into an IMMUTABLE byte[] (bounded by maxPartBytes). Returning a byte[] rather than
    // a MemoryStream is deliberate: the bounded decode opens its OWN read-only MemoryStream over these bytes so
    // a stranded decode never shares a caller-disposed/pooled stream (isolation contract; see ReadAsync).
    private static async Task<byte[]> BufferAsync(Stream stream, long maxPartBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxPartBytes)
            {
                throw DeltaProtocolException.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A Delta checkpoint part exceeds the {maxPartBytes}-byte decode ceiling."));
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Opens the checkpoint Parquet, classifying a Parquet Modular Encryption checkpoint as
    /// <see cref="StorageErrorKind.UnsupportedFeature"/> and every other open failure as fail-closed
    /// <see cref="DeltaProtocolErrorKind.MalformedAction"/> (#681).</summary>
    private static async Task<ParquetReader> OpenAsync(Stream input, CancellationToken cancellationToken)
    {
        ParquetReader? reader = null;
        try
        {
            reader = await ParquetReader.CreateAsync(input, null, false, cancellationToken).ConfigureAwait(false);

            // Parquet.Net parses the footer LAZILY: CreateAsync reads the Thrift FileMetaData, but the
            // high-level ParquetSchema is built ON FIRST ACCESS. Force that materialization HERE — the same
            // order the data-file door uses (ParquetFileReader.OpenAsync) — so a corrupt footer is proven
            // corrupt BEFORE the encryption check below runs. The order matters for PRECISION: a byte-flipped
            // footer can still PARSE into a bogus non-null encryption_algorithm (4 of the 400 single-bit flips
            // in DeltaFuzzTests.CheckpointReader_OnlyFailsClosed_OnByteFlippedCheckpoint do exactly that),
            // and every one of those flips also breaks the schema — so materializing the schema first keeps a
            // genuinely corrupt checkpoint classified MALFORMED instead of mislabeled "encrypted". A real
            // encrypted checkpoint is unaffected: its plaintext footer and schema materialize cleanly, so it
            // reaches the check below.
            //
            // LOAD-BEARING: this line is the SOLE control for those 4 flips, not a redundant second one.
            // Deleting it turns the fuzz test RED. All 4 corrupt into a footer with an EMPTY
            // encryption_algorithm union AND zero inspectable column chunks, which is precisely the shape the
            // classifier's third arm treats as bare presence (its per-column backstop cannot speak), so the
            // classifier calls them "encrypted" and only the schema probe rejects them first. Note the
            // coupling this creates: the third arm is what re-armed this line, so changing either one changes
            // the other's bite — they are NOT independent.
            //
            // The claim is asserted, not merely asserted-in-prose: DeltaCheckpointReaderTests
            // .SchemaFirstOrdering_IsSoleControl_ForAtLeastOneCorruptFlip pins both halves (that the
            // classifier alone WOULD misclassify at least one flip, and that the door still reports
            // MALFORMED for every such flip) so this comment cannot drift out of date again.
            _ = reader.Schema;

            // Parquet Modular Encryption, SUCCESS-path arm (#681, diagnosability parity with #655/#680 on the
            // data-file door). A plaintext-footer encrypted checkpoint keeps the ordinary PAR1 magic and its
            // footer parses cleanly, so it OPENS here and would otherwise fail later — as "malformed" — when
            // its encrypted pages fail to decode. It is not malformed: it is a valid checkpoint written with a
            // feature DeltaSharp cannot read. Detection is the shared classifier's presence-only footer check
            // (no field content, path, or key id is read or echoed — #653 hygiene).
            if (ParquetEncryption.IsPlaintextFooterEncrypted(reader.Metadata))
            {
                await DisposeQuietlyAsync(reader).ConfigureAwait(false);
                throw DeltaStorageException.UnsupportedFeature(
                    ParquetEncryption.PlaintextFooterEncryptionMessage);
            }

            return reader;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DeltaProtocolException)
            && ex is not DeltaStorageException { Kind: StorageErrorKind.UnsupportedFeature })
        {
            // Parquet Modular Encryption, FAILURE-path arm (#681). Encrypted-FOOTER mode (PARE magic) is
            // rejected by Parquet.Net at open, and a REAL plaintext-footer encryptor omits the encrypted
            // columns' plaintext ColumnMetaData, which makes the library throw during row-group-reader init —
            // both land here, indistinguishable (by message) from genuine corruption. The shared classifier
            // reads the input's own magic/footer to separate them; it MUST run before the dispose below, which
            // releases the input stream. Anything not positively identified keeps the fail-closed malformed
            // default — encryption is asserted, never guessed.
            //
            // The filter excludes ONLY the exception this method itself re-raises (the SUCCESS-path
            // UnsupportedFeature above, thrown inside this same try) — not DeltaStorageException as a whole.
            // Excluding the whole type would be safe only while ReadAsync fully buffers each part into a
            // MemoryStream before calling us; if checkpoint parts are ever streamed straight from the backend,
            // a transient storage fault raised in here would escape UNMAPPED past DeltaLog's UnsupportedFeature
            // -only fallback, turning a retryable blip into a failed table read instead of a JSON replay
            // (#698 review FIX 5, same class as the DeltaLog swallow narrowing).
            string? unsupportedEncryption = ParquetEncryption.ClassifyUnreadableInput(input);
            if (reader is not null)
            {
                await DisposeQuietlyAsync(reader).ConfigureAwait(false);
            }

            if (unsupportedEncryption is not null)
            {
                // The classifier's fixed message names only the feature — no path, footer field, or value
                // (#653), and no ex.Message echo.
                throw DeltaStorageException.UnsupportedFeature(unsupportedEncryption);
            }

            // Fixed message (no ex.Message interpolation) so a crafted footer's bytes cannot echo into the
            // error text (info-leak parity with ParquetFileReader, #651); ex kept as the inner exception.
            throw DeltaProtocolException.Malformed(
                "The Delta checkpoint Parquet stream is malformed or truncated.", ex);
        }
    }

    /// <summary>Disposes a reader that is being abandoned on a fail-closed path, swallowing a dispose-time
    /// fault so it cannot escape UNMAPPED and replace the classification the caller is about to throw (the
    /// open boundary stays exception-total). Deliberately BROADER than the data-file door's equivalent, which
    /// swallows only <c>IsUndecodableParquetInput</c> faults: here every non-cancellation dispose fault is
    /// swallowed, because on this path a classification is already pending and any cleanup fault that replaced
    /// it would be strictly less informative. Swallowing more is the safe direction for an exception-total
    /// boundary; cancellation still propagates (#698 review). Typed as <see cref="IAsyncDisposable"/> (the
    /// reader implements it) so the swallow-but-propagate-cancellation contract is unit-pinnable (#773).</summary>
    internal static async ValueTask DisposeQuietlyAsync(IAsyncDisposable reader)
    {
        try
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposeFault) when (disposeFault is not OperationCanceledException)
        {
            // Cleanup fault on a reader being torn down: ignored so the pending classification stays the
            // single, meaningful outcome.
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
