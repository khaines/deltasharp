using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeltaSharp.Storage.Diagnostics;
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

    /// <summary>The CUMULATIVE per-PART decoded-bytes ceiling (Round-8 #3, re-derived Round-10 #4): the SUM,
    /// over ALL row groups of one checkpoint part, of the bytes this reader eagerly decodes/materializes (the
    /// growing <c>List&lt;DeltaAction&gt;</c> plus the current row group's arrays) must not exceed this. It is
    /// derived as
    /// <c>max(MaxCheckpointPartBytes × CheckpointDecodedExpansionFactor,
    /// CheckpointCumulativeRowGroupFloorMultiple × MaxCheckpointRowGroupDecodedBytes)</c>
    /// = <c>max(512&#160;MiB × 8, 8 × 1&#160;GiB)</c> = <b>8&#160;GiB</b> (the floor term wins). The floor on
    /// <c>k × MaxCheckpointRowGroupDecodedBytes</c> is the Round-10 #4 fix: the previous flat 4&#160;GiB ceiling
    /// wrongly rejected a LEGITIMATE foreign (Spark) checkpoint part (300–512&#160;MiB compressed that decodes to
    /// &gt;4&#160;GiB once Dremel per-slot levels are counted across several row groups) — and reported it as
    /// <see cref="DeltaProtocolException"/> corruption. The floor ensures a legit MULTI-row-group part (each
    /// group ≤ <see cref="MaxCheckpointRowGroupDecodedBytes"/>) is not rejected, and a breach is now classified
    /// as a distinct <see cref="StorageErrorKind.DecodeCeilingExceeded"/> resource fallback (→ JSON replay), NOT
    /// corruption. The per-ROW-GROUP ceiling (<see cref="MaxCheckpointRowGroupDecodedBytes"/>) bounds ONE group;
    /// this bounds the whole part so the strand's LIVE charge (<c>length + cumulativeDecoded</c>, Round-10 #1)
    /// stays bounded. Enforced across row groups in <c>DecodeBufferedAsync</c> (checked BEFORE each group's
    /// column decode, Round-10 #6, so the 8&#160;GiB holds at every instant). A legitimately larger checkpoint
    /// spreads across multiple PARTS.</summary>
    internal const long MaxCheckpointPartDecodedBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>The floor multiple <c>k</c> on <see cref="MaxCheckpointRowGroupDecodedBytes"/> used to derive
    /// <see cref="MaxCheckpointPartDecodedBytes"/> (Round-10 #4): the cumulative per-part ceiling is floored at
    /// <c>k</c> maximal row groups so a legit multi-row-group foreign part is not rejected by a ceiling derived
    /// only from the compressed-buffer × expansion product. Pinned by a relationship test so the const literal
    /// above cannot silently drift from the derivation.</summary>
    internal const int CheckpointCumulativeRowGroupFloorMultiple = 8;

    /// <summary>The default per-part wall-clock decode budget FLOOR (design §5.4 C-DECODE, #647/#699/#716):
    /// each checkpoint part is decoded under its OWN size-aware budget derived from THAT part's DECODED-bytes
    /// estimate (see <see cref="DeriveSizeAwareBudget"/>) — never a shrinking aggregate remainder shared across
    /// parts, which would hand a later part a starved budget and seed a healthy-but-slow part into the negative
    /// cache as "known-bad" (Critical #2b). The basis derives from the enforced per-PART DECODED-bytes ceiling
    /// (<see cref="MaxCheckpointPartDecodedBytes"/> — NOT the per-ROW-GROUP 1&#160;GiB ceiling, which capped a
    /// whole-part budget at 32&#160;s &lt; the real decode of a foreign 200–500&#160;MiB Spark part → deterministic
    /// timeout → strike → 24h suppression → unreadable table) divided by a conservative documented FLOOR decode
    /// throughput, so its UNITS MATCH the ceiling the decode actually enforces (#802 tracks benchmark-backed
    /// calibration). Stated floor decode throughput = 32&#160;MiB/s of DECODED output (real Parquet decode is
    /// &gt;&gt; that; DeltaSharp measures in the hundreds). Floored at <see cref="BoundedDecode.DefaultBudget"/>
    /// (30&#160;s) and capped at <see cref="MaxSizeAwareBudget"/> (128&#160;s, Round-10 #11) — the budget-time cap
    /// is decoupled from the (larger, Round-10 #4) cumulative RESOURCE ceiling so the budget stays ≤ 128&#160;s
    /// even though the byte ceiling is 8&#160;GiB; a real part decodes far faster than the pessimistic floor, so
    /// 128&#160;s is ample for a legit 8&#160;GiB-decoding foreign part.</summary>
    private const double FloorDecodedBytesPerSecond = 32.0 * 1024 * 1024;

    /// <summary>The upper cap on the size-aware per-part decode budget (128&#160;s, Round-10 #11): the derived
    /// budget is <c>min(compressed × CheckpointDecodedExpansionFactor, MaxCheckpointPartDecodedBytes) ÷
    /// FloorDecodedBytesPerSecond</c>, floored at 30&#160;s and capped HERE at 128&#160;s. The cap is decoupled
    /// from the cumulative RESOURCE ceiling (now 8&#160;GiB, Round-10 #4) so the budget cannot grow to 256&#160;s;
    /// 128&#160;s = 4&#160;GiB ÷ 32&#160;MiB/s, and because real decode throughput is &gt;&gt; the 32&#160;MiB/s
    /// floor, 128&#160;s comfortably covers a legit 8&#160;GiB-decoding part.</summary>
    private static readonly TimeSpan MaxSizeAwareBudget = TimeSpan.FromSeconds(128);

    /// <summary>The conservative bounded DECOMPRESSION expansion factor applied to a part's COMPRESSED buffered
    /// bytes to estimate its DECODED footprint for the size-aware budget (High #6). The Round-5 budget derived
    /// the budget from the part's COMPRESSED length divided by the DECODED throughput — a units mismatch that,
    /// because a typical compressed part is far below the throughput×floor product, ALWAYS collapsed to the
    /// 30&#160;s floor regardless of part size (arithmetically inert). Estimating decoded bytes as
    /// <c>compressed × factor</c> (clamped to the enforced per-PART decoded ceiling) makes the budget genuinely
    /// scale with the decode work a large healthy part demands, so it is not starved into the negative cache.
    /// Eight is a conservative upper bound on columnar (snappy/zstd) checkpoint expansion; #802 tracks
    /// calibration.</summary>
    private const double CheckpointDecodedExpansionFactor = 8.0;

    // Derives the per-part decode budget from the part's OWN estimated DECODED bytes (High #6): the part's
    // buffered COMPRESSED length is scaled by the bounded decompression expansion factor and clamped to the
    // enforced per-PART decoded-bytes ceiling (MaxCheckpointPartDecodedBytes — NOT the per-ROW-GROUP 1 GiB
    // ceiling, which capped a whole-part budget at 32 s < the real decode of a foreign 200–500 MiB Spark part),
    // then divided by the conservative floor decode throughput, floored at DefaultBudget (30 s) and capped at
    // MaxSizeAwareBudget (128 s, Round-10 #11 — the budget-time cap is decoupled from the larger cumulative
    // resource ceiling). Using a decoded-bytes basis (not the compressed length the Round-5 code used) makes the
    // budget actually scale with part size instead of collapsing to the floor for every part. I/O transfer is
    // EXCLUDED (this is called after BufferAsync completes and the decode clock starts at the bounded decode's
    // execution start), so a slow download never consumes the decode budget. Exposed internally for a direct
    // monotonicity/clamp table test (High #6). Callers within this class treat it as private.
    internal static TimeSpan DeriveSizeAwareBudget(long compressedBytes)
    {
        long estimatedDecoded = SaturatingScale(Math.Max(compressedBytes, 0L), CheckpointDecodedExpansionFactor);
        long basisBytes = Math.Min(estimatedDecoded, MaxCheckpointPartDecodedBytes);
        TimeSpan derived = TimeSpan.FromSeconds(basisBytes / FloorDecodedBytesPerSecond);
        if (derived < BoundedDecode.DefaultBudget)
        {
            derived = BoundedDecode.DefaultBudget;
        }

        return derived > MaxSizeAwareBudget ? MaxSizeAwareBudget : derived;
    }

    // Scales a non-negative byte count by a factor without overflowing to a negative long (saturates at
    // long.MaxValue); the result is always clamped to the decoded ceiling by the caller.
    private static long SaturatingScale(long value, double factor)
    {
        double scaled = value * factor;
        return scaled >= long.MaxValue ? long.MaxValue : (long)scaled;
    }

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
        TimeSpan? decodeBudget = null, TimeProvider? timeProvider = null, BoundedDecoder? decoder = null,
        long maxPartDecodedBytes = MaxCheckpointPartDecodedBytes, Action<TimeSpan>? onPartBudget = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Fail fast on a misconfigured budget (positive and within the accepted ceiling) BEFORE any I/O, with
        // an explicit paramName — never as a raw ArgumentOutOfRangeException surfacing mid-decode from
        // Task.Delay. A null budget means "derive a size-aware budget below from the buffered part bytes".
        if (decodeBudget is { } configured)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configured.Ticks, nameof(decodeBudget));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(configured, BoundedDecode.MaxBudget, nameof(decodeBudget));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPartDecodedBytes, nameof(maxPartDecodedBytes));

        BoundedDecoder checkpointDecoder = decoder ?? BoundedDecode.CheckpointDecoder;

        // Buffer to a bounded byte[] region (not a MemoryStream the caller then disposes). Isolation contract:
        // the bounded decode below opens its OWN read-only MemoryStream over these bytes INSIDE the work
        // delegate, so a stranded (detached-past-deadline) decode never touches a caller-owned/pooled stream —
        // it can never observe a disposed buffer nor a rewound position, eliminating the wrong-result race on
        // timeout. The bytes are read-only and shared; only a stranded decode's own MemoryStream pins them
        // (≤ maxPartBytes, the accepted residual). BufferAsync returns the backing array plus the valid length
        // (avoiding the ToArray doubling copy); the length-limited MemoryStream never exposes the slack tail.
        (byte[] bytes, int length) = await BufferAsync(stream, maxPartBytes, cancellationToken).ConfigureAwait(false);

        // Size-aware per-part budget (Critical #2b). The budget is derived from THIS part's buffered bytes —
        // never a shrinking aggregate remainder shared across parts — and the decode clock starts at the bounded
        // decode's EXECUTION start (after BufferAsync above completes), so I/O transfer is EXCLUDED. A healthy
        // multi-part checkpoint on slow storage therefore never hands a later part a starved budget, and any
        // timeout here is provably past an ADEQUATE budget (so DeltaLog may safely seed the negative cache).
        TimeSpan partBudget = decodeBudget ?? DeriveSizeAwareBudget(length);

        // TEST seam (Round-10 MultiPart I7): publish the ACTUAL per-part budget threaded into RunAsync below, so a
        // test can record the real value each part's decode receives (proving it is derived from THIS part's own
        // buffered bytes, never a shrinking aggregate remainder) rather than re-deriving it and asserting a
        // tautology. No-op in production (null).
        onPartBudget?.Invoke(partBudget);

        // LIVE per-part strand charge (Round-10 #1). A stranded checkpoint decode retains its buffered COMPRESSED
        // byte[] (`length`) PLUS the cumulative decoded arrays + growing List<DeltaAction> across the row groups
        // it has processed SO FAR. Pre-Round-10 the charge was a FLAT `length + MaxCheckpointPartDecodedBytes`
        // (the full 8 GiB cumulative ceiling) regardless of how much the strand had actually decoded — so the
        // byte gate degenerated into a de-facto count cap of ~1 (two crafted strands wedged the door). Instead,
        // publish the live retained total (`length + cumulativeDecoded`) through this shared counter — updated by
        // DecodeBufferedAsync per row group — and charge what the counter reads AT THE MOMENT OF DETACH, floored
        // at MinStrandChargeBytes (so a cheap strand still consumes the byte residual, Round-8 #1b) and clamped
        // to the door footprint by RunAsync (so it stays a TRUE ceiling). This restores the intended ~64-strand
        // headroom while keeping the bound honest.
        var retainedBytes = new StrongBox<long>(length);
        long RetainedChargeProbe() =>
            Math.Max(Volatile.Read(ref retainedBytes.Value), BoundedDecode.MinStrandChargeBytes);

        // Bounded-time decode (#716/#699/#647). A single corrupted byte (the terminal footer STOP flipped,
        // #699; the byte at index 5595 in the #716 minimized repro; a corrupt data-page header, #647) can
        // drive Parquet.Net 6.0.3 into effectively UNBOUNDED work — inside ParquetReader.CreateAsync at
        // open, or inside the synchronous column/page decode — that IGNORES the CancellationToken. A hang is
        // not an exception, so the fail-closed catch inside DecodeBufferedAsync cannot intercept it. Race the
        // WHOLE open+decode against the per-part wall-clock budget via the CHECKPOINT door so a corrupt
        // checkpoint fails closed deterministically (→ the non-authoritative checkpoint is discarded and
        // DeltaLog falls back to JSON replay) rather than stalling the table read indefinitely. The checkpoint
        // decode is SYNCHRONOUS over the pre-buffered byte[], so it genuinely runs on the door's dedicated
        // strand thread (isolated from data-file strands; I5) — unlike the data-file door, whose async decode
        // resumes on the pool. On expiry a DecodeBudgetExceeded DeltaStorageException (NOT Malformed: a
        // wall-clock stall is a resource fault, not proven corruption — #649/#655/#681 classification contract)
        // routes DeltaLog to JSON replay under the DecodeTimeout reason; when the door is at its memory/strand
        // cap the decode is rejected with a DecodeCapacityExhaustedException (never started) which DeltaLog
        // classifies DecoderSaturated (I8); a valid-but-unsupported UnsupportedFeature and cooperative
        // cancellation both finish inside the budget and propagate unwrapped as the work's own outcome. If this
        // decode STRANDS (detaches past its deadline), it charges its LIVE retained footprint via
        // RetainedChargeProbe (Round-10 #1), clamped by the door to CheckpointMaxFootprintBytes and floored at
        // MinStrandChargeBytes, against the checkpoint door's stranded residual — a healthy in-budget decode
        // charges NOTHING (§5.4).
        return await checkpointDecoder.RunAsync(
            decodeToken => DecodeBufferedAsync(bytes, length, maxDecodedBytes, maxPartDecodedBytes, retainedBytes, decodeToken),
            partBudget,
            static _ => DeltaStorageException.DecodeBudgetExceeded(
                "The Delta checkpoint Parquet could not be decoded within the bounded-decode time budget."),
            cancellationToken,
            onAbandonedResult: null,
            onWorkSettled: null,
            timeProvider: timeProvider,
            retainedBytesProbe: RetainedChargeProbe).ConfigureAwait(false);
    }

    // The open + full row-group decode of one buffered checkpoint part (bounded in time by the caller's
    // BoundedDecode.RunAsync). Kept as its own method so the bounded-decode policy wraps a single unit of work
    // whose CancellationToken is the linked (deadline + caller) token. Opens an ISOLATED read-only MemoryStream
    // over the caller-supplied bytes (length-limited) so a stranded decode never shares a caller-owned stream
    // (see ReadAsync).
    private static async Task<IReadOnlyList<DeltaAction>> DecodeBufferedAsync(
        byte[] bytes, int length, long maxDecodedBytes, long maxPartDecodedBytes,
        StrongBox<long> retainedBytes, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream(bytes, 0, length, writable: false);
        await using (buffer.ConfigureAwait(false))
        {
            ParquetReader reader = await OpenAsync(buffer, cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                try
                {
                    var schema = CheckpointSchema.Resolve(reader.Schema);
                    var actions = new List<DeltaAction>();
                    // Cumulative per-PART decoded-bytes total across ALL row groups (Round-8 #3). Each row group
                    // is individually bounded by maxDecodedBytes, but a part with many row groups would otherwise
                    // accumulate an unbounded List<DeltaAction> while the strand charge counted only the buffered
                    // COMPRESSED length. Fail closed (→ JSON replay) once the SUM crosses the per-part ceiling.
                    long cumulativeDecoded = 0;
                    for (int group = 0; group < reader.RowGroupCount; group++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(group);

                        // Round-10 #6: measure the group's declared decoded footprint (via EnsureDecodeCeiling)
                        // and enforce the CUMULATIVE per-part ceiling BEFORE this group's columns are decoded, so
                        // the ceiling holds at EVERY instant — the pre-Round-10 order checked only AFTER
                        // ReadRowGroupAsync had already materialized the group, letting the transient peak reach
                        // ceiling + one row group (~5.5 GiB) before the check fired.
                        (int rowCount, long groupDecoded) = MeasureRowGroup(rowGroup, schema, group, maxDecodedBytes);
                        cumulativeDecoded = SaturatingAdd(cumulativeDecoded, groupDecoded);

                        // Publish the LIVE retained total (length + cumulativeDecoded) so a strand's charge at
                        // detach reads the actual incremental bytes retained SO FAR (Round-10 #1), not a flat
                        // ceiling. Written on the dedicated strand thread; the RunAsync probe reads it via
                        // Volatile at the moment of detach.
                        Volatile.Write(ref retainedBytes.Value, SaturatingAdd(length, cumulativeDecoded));

                        if (cumulativeDecoded > maxPartDecodedBytes)
                        {
                            // A resource/decode-ceiling breach is NOT corruption (Round-10 #4): a legit foreign
                            // (Spark) multi-row-group part can decode past the ceiling. Classify it as a distinct
                            // DECODE-CEILING fallback reason (DeltaStorageException.DecodeCeilingExceeded) so
                            // DeltaLog routes to JSON replay WITHOUT mislabelling it Malformed and WITHOUT seeding
                            // the negative cache (it is not proven bad).
                            throw DeltaStorageException.DecodeCeilingExceeded(string.Create(
                                CultureInfo.InvariantCulture,
                                $"The Delta checkpoint part would eagerly decode {cumulativeDecoded} bytes across "
                                + $"its row groups, exceeding the {maxPartDecodedBytes}-byte cumulative "
                                + $"per-part decode ceiling."));
                        }

                        if (rowCount == 0)
                        {
                            continue;
                        }

                        await DecodeRowGroupColumnsAsync(
                            rowGroup, schema, actions, group, rowCount, cancellationToken).ConfigureAwait(false);
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

    // Buffers the whole part into a byte[] region (bounded by maxPartBytes) and returns the backing array plus
    // the valid prefix length. Returning (array, length) rather than a trimmed copy avoids the ToArray doubling
    // copy (a 512-MiB part would otherwise transiently hold two full buffers). The bounded decode opens its OWN
    // length-limited read-only MemoryStream over the returned array so a stranded decode never shares a
    // caller-disposed/pooled stream (isolation contract; see ReadAsync) and never sees the slack tail past
    // length. The pre-sized initial capacity keeps growth doublings bounded for large healthy parts.
    private static async Task<(byte[] Buffer, int Length)> BufferAsync(
        Stream stream, long maxPartBytes, CancellationToken cancellationToken)
    {
        // Pre-size to the declared length when the backend stream exposes one (capped at the ceiling so a
        // crafted Length can't force an oversized allocation); otherwise start small and let it grow.
        int initialCapacity = 81920;
        if (stream.CanSeek)
        {
            long length = stream.Length;
            if (length > 0 && length <= maxPartBytes)
            {
                initialCapacity = (int)Math.Min(length, int.MaxValue);
            }
        }

        using var buffer = new MemoryStream(initialCapacity);
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

        // A MemoryStream constructed with a capacity exposes its backing buffer via TryGetBuffer. When the
        // pre-size matched the part length exactly (a seekable stream), the backing array fits the valid bytes
        // with no slack, so we hand it back with NO extra copy. Otherwise the doubling growth can leave the
        // backing array up to ~2× the valid length — a stranded decode would then pin twice the part's bytes,
        // and the byte-aware admission cap (which reserves `length`) would UNDER-count the true residual. In
        // that case trim to an exactly-sized copy so the retained footprint matches the reserved bytes.
        if (buffer.TryGetBuffer(out ArraySegment<byte> segment)
            && segment.Offset == 0
            && segment.Array!.Length == segment.Count)
        {
            return (segment.Array, segment.Count);
        }

        byte[] copy = buffer.ToArray();
        return (copy, copy.Length);
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

    // Round-10 #6: measures a row group's declared decoded-bytes footprint WITHOUT decoding any column, so the
    // caller (DecodeBufferedAsync) can enforce the CUMULATIVE per-part ceiling BEFORE the eager column decode
    // runs — keeping the peak footprint ≤ the ceiling at every instant. Returns (rowCount, groupDecoded); a
    // zero-row group is (0, 0). Split out of the former ReadRowGroupAsync (which decoded inline).
    private static (int RowCount, long GroupDecoded) MeasureRowGroup(
        ParquetRowGroupReader rowGroup,
        CheckpointSchema schema,
        int group,
        long maxDecodedBytes)
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
            return (0, 0);
        }

        // Returns the row group's decoded-bytes footprint so DecodeBufferedAsync can enforce the CUMULATIVE
        // per-part ceiling across row groups (Round-8 #3, hoisted BEFORE the column decode in Round-10 #6).
        long groupDecoded = EnsureDecodeCeiling(rowGroup, schema.LeafFields(), group, maxDecodedBytes);
        return (rowCount, groupDecoded);
    }

    // Decodes one row group's columns and appends the reconstructed actions. Called by DecodeBufferedAsync ONLY
    // AFTER MeasureRowGroup's decoded footprint has been folded into the cumulative total and checked against
    // the per-part ceiling (Round-10 #6), so this eager decode never overshoots the ceiling.
    private static async Task DecodeRowGroupColumnsAsync(
        ParquetRowGroupReader rowGroup,
        CheckpointSchema schema,
        List<DeltaAction> actions,
        int group,
        int rowCount,
        CancellationToken cancellationToken)
    {
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
    /// <returns>The row group's total eager-decode footprint (bytes) — accumulated across row groups by the
    /// caller to enforce the CUMULATIVE per-part ceiling (Round-8 #3).</returns>
    internal static long EnsureDecodeCeiling(
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

        return totalBytes;
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
