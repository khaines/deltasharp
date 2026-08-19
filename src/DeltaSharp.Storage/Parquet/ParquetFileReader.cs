using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Diagnostics;
using DeltaSharp.Types;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Reads a Parquet <see cref="Stream"/> into <see cref="ColumnBatch"/>es — one batch per row group
/// (design §2.9.1, STORY-05.1.1 / #180). It supports <b>projection</b> (only the requested columns'
/// chunks are read) and <b>row-group pruning</b> (a caller predicate over each group's column
/// statistics may skip a group as a <i>hint</i>; the residual predicate stays the engine's job, so a
/// kept row group is always fully correct).
/// </summary>
/// <remarks>
/// <para><b>Streaming corruption contract (H3, design §3.2 EE-01/EE-02).</b> The reader is
/// <i>batch-atomic</i>, not file-atomic: every yielded <see cref="ColumnBatch"/> is <b>always</b>
/// complete — a torn/partial batch is never produced. Concretely:
/// <list type="bullet">
/// <item>Structural/footer/metadata corruption fails <b>before any batch is yielded</b>: the footer
/// (schema + row-group metadata) is read at open, so a malformed/truncated file throws a deterministic
/// <see cref="DeltaStorageException"/> up front.</item>
/// <item>A page-level defect inside row group <c>K</c> surfaces as a deterministic error <b>at batch
/// <c>K</c></b>: batches for groups <c>0..K-1</c> were already returned complete, then enumerating the
/// defective group throws — it never yields a partial batch <c>K</c>.</item>
/// </list>
/// The reader deliberately keeps streaming (it does not buffer every row group to make the whole file
/// atomic): the honest, design-consistent guarantee is that a batch is never torn, not that the whole
/// file is validated before the first batch.</para>
/// <para><b>Eager-decode memory ceiling (H4/CF-1, design §5.4 C-DECODE).</b> Before the eager
/// per-column allocation, each projected row group's declared metadata is validated via
/// <see cref="EnsureDecodeCeiling"/>: a per-chunk decompression-ratio ceiling, an absolute
/// decompressed-size ceiling, and a row-count bound on the bytes the declared row count would eagerly
/// materialize. This is fundamentally a bound on the <b>transient memory this reader allocates</b>
/// because it decodes each row group eagerly and whole, not purely a "decompression bomb" guard: a
/// crafted footer that inflates the decompressed size or row count fails closed rather than driving an
/// out-of-memory allocation, and a <i>legitimate</i> row group whose projected columns would
/// materialize past the cap is likewise rejected because the eager decode cannot fit it. Legitimately
/// compressible data (constant/RLE/all-null columns that encode millions of rows in a few hundred
/// bytes) passes, since the bound is on the decoded footprint rather than a physical rows/byte proxy.
/// Chunked/streaming page-at-a-time decode that would lift the cap is a tracked follow-up.</para>
/// </remarks>
internal sealed class ParquetFileReader
{
    /// <summary>The default maximum plausible ratio of a column chunk's declared decompressed bytes to its
    /// declared compressed bytes (design §5.4 C-DECODE). Real Parquet encodings — even Snappy over
    /// constant/RLE/all-null data — stay well under this (empirically ≈ 20:1 at the column-chunk level),
    /// so a declared ratio beyond it is a crafted footer, not a real file, and is rejected as a
    /// decompression bomb. Configurable per reader via <see cref="ParquetDecodeLimits"/>; this constant is
    /// the default (and the reference used by the checkpoint reader's own guard).</summary>
    internal const long MaxDecompressionRatio = ParquetDecodeLimits.DefaultMaxDecompressionRatio;

    /// <summary>The default absolute per-row-group <b>eager-decode</b> memory ceiling (4&#160;GiB), applied
    /// to both the declared decompressed bytes AND the bytes a row group's declared row count would eagerly
    /// materialize (design §5.4 C-DECODE). Because this reader decodes each row group whole, it bounds the
    /// transient decode/allocation memory: a crafted footer (inflating the decompressed size or row count)
    /// fails closed rather than driving an out-of-memory allocation, and a legitimate row group whose
    /// projected columns exceed the cap is likewise rejected until chunked/streaming decode lifts the limit
    /// (tracked follow-up). Configurable per reader via <see cref="ParquetDecodeLimits"/> — lower it below a
    /// constrained executor budget, or raise it for a trusted large-row-group workload.</summary>
    internal const long MaxRowGroupDecodedBytes = ParquetDecodeLimits.DefaultMaxRowGroupDecodedBytes;

    /// <summary>The documented ceiling (256&#160;MiB) on the retained footprint charged for an OPEN / footer
    /// metadata strand (Round-10 #1). An open/metadata strand retains only the PARSED footer (the Thrift
    /// <c>FileMetaData</c> + the reader's footer buffers), which the reader already bounds when it reads the
    /// footer — NOT the whole file. Pre-Round-10 <see cref="EstimateOpenRetainedBytes"/> charged the WHOLE file
    /// length (or the full 8&#160;GiB door footprint when non-seekable) for a strand that never materializes the
    /// file body, so a couple of large-file opens degenerated the byte gate into a de-facto count cap. Clamp the
    /// open charge to this ceiling (floored at <see cref="BoundedDecode.MinStrandChargeBytes"/>) and use THIS
    /// ceiling — not the door footprint — for the non-seekable fallback.</summary>
    internal const long MaxFooterMetadataBytes = 256L * 1024 * 1024;

    private readonly ParquetDecodeLimits _limits;
    private readonly TimeProvider _timeProvider;
    private readonly DeltaStorageTelemetry _telemetry;
    private readonly BoundedDecoder _dataFileDecoder;

    // Micros per calendar day (86_400 s × 1_000_000 µs), for the date→timestamp_ntz read-promotion
    // (midnight-of-date epoch-micros). #533.
    private const long MicrosPerDay = 86_400L * 1_000_000L;

    // The estimated retained bytes a STRAND of an OPEN / footer metadata decode would pin (a reader + its parsed
    // Thrift FileMetaData + footer buffers) — charged against the data-file door's stranded residual only if that
    // open/metadata decode detaches. Derived per-open from the input length (see EstimateOpenRetainedBytes, High
    // #7 — the fixed 16-MiB fiction under-counted an attacker-scaled footer); the row-group decode charges the
    // row group's REAL projected footprint (EstimateRowGroupRetainedBytes).

    // The retained footprint a stranded OPEN / metadata-scan strand pins: the already-parsed Thrift FileMetaData
    // (attacker-scaled, but bounded by the footer the reader reads) plus the reader's footer buffers. It does NOT
    // retain the file body. Clamp the charge to MaxFooterMetadataBytes (the reader already bounds footer reads) —
    // charging the whole file length (or the full door footprint when non-seekable), as the pre-Round-10 code
    // did, over-stated a footer-only strand and degenerated the byte gate into a de-facto count cap (Round-10
    // #1). Basis = min(length, ceiling) when seekable, else the ceiling itself (NOT the door footprint). Floored
    // at BoundedDecode.MinStrandChargeBytes so a cheap strand still consumes the byte residual (High #1b), then
    // clamped to the door footprint by RunAsync.
    private static long EstimateOpenRetainedBytes(Stream input)
    {
        long length = -1;
        try
        {
            if (input.CanSeek)
            {
                length = input.Length;
            }
        }
        catch
        {
            length = -1;
        }

        long basis = length > 0 ? Math.Min(length, MaxFooterMetadataBytes) : MaxFooterMetadataBytes;
        return Math.Max(basis, BoundedDecode.MinStrandChargeBytes);
    }

    /// <summary>Creates a reader whose eager-decode guard uses <paramref name="limits"/> (or the safe
    /// <see cref="ParquetDecodeLimits.Default"/> when unset).</summary>
    /// <param name="limits">The eager-decode + bounded-time guard limits (default
    /// <see cref="ParquetDecodeLimits.Default"/>).</param>
    /// <param name="timeProvider">The clock the wall-clock decode deadline is measured against (default
    /// <see cref="TimeProvider.System"/>), injected per the storage-layer convention so deadline tests can
    /// drive it deterministically.</param>
    /// <param name="telemetry">The storage telemetry surface a decode-budget timeout is reported on (default
    /// the process-wide no-op <see cref="DeltaStorageTelemetry.Shared"/>).</param>
    /// <param name="dataFileDecoder">The bounded-decode admission surface for the data-file door (default the
    /// process-wide <see cref="BoundedDecode.DataFileDecoder"/>). Injected — symmetric with the
    /// <paramref name="timeProvider"/>/<paramref name="telemetry"/> seams — so a test can drive ALL five
    /// capacity branches through the REAL read path with a <c>cap=1</c> decoder and a gated strand, without a
    /// test-only widening that hides production sizing.</param>
    public ParquetFileReader(
        ParquetDecodeLimits? limits = null,
        TimeProvider? timeProvider = null,
        DeltaStorageTelemetry? telemetry = null,
        BoundedDecoder? dataFileDecoder = null)
    {
        _limits = limits ?? ParquetDecodeLimits.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _telemetry = telemetry ?? DeltaStorageTelemetry.Shared;
        _dataFileDecoder = dataFileDecoder ?? BoundedDecode.DataFileDecoder;
    }

    // The data-file decode wall-clock budget is enforced PER DECODE OPERATION (one open, one row-group decode),
    // never as an aggregate spanning the streaming iterator's yield suspensions (I7). The Round-2 aggregate
    // DecodeDeadline was created at ReadAsync entry and threaded through every `yield return`, so ALL downstream
    // engine work between MoveNextAsync calls (shuffle write, spill, join build, sink backpressure) was charged
    // to the "decode" budget — a healthy query could fail mid-scan with a false DecodeBudgetExceeded. The budget
    // now bounds only the decode delegate, measured from its EXECUTION start (BoundedDecoder I3), so consumer
    // time is never charged. A crafted file with a non-terminating row group still fails closed on the FIRST
    // such group after one budget (the whole read then aborts); a healthy N-group read spends only each group's
    // own (millisecond) decode time.

    // Records the data-file decode-budget timeout on the storage meter (door=data_file, tagged with the read
    // STAGE so a non-terminating row-group page decode is distinguishable from a non-terminating open, #647)
    // and returns the typed DecodeBudgetExceeded fail-closed exception. Used as the BoundedDecode onTimeout
    // factory, so a timeout is observable exactly once per decode operation.
    private Exception DataFileDecodeTimeout(string message, DecodeStage stage)
    {
        _telemetry.RecordDecodeBudgetExceeded(DecodeDoor.DataFile, stage);
        return DeltaStorageException.DecodeBudgetExceeded(message);
    }

    // Maps the bounded-decode worker's fail-fast admission rejection (the data-file door is at its memory/strand
    // cap — too many untrusted decodes already detached past their deadline, #647/#699/#716) to the PUBLIC
    // retryable DecoderSaturated storage error, and records the door-dimensioned decode.capacity_exhausted
    // counter (I8). DISTINCT from a decode timeout: the decode NEVER STARTED, so it must never be labeled a
    // decode-timeout, never be negatively cached, and surfaces as a fail-closed saturation a caller may retry
    // after capacity frees (no auto-retry today — a bounded backoff-retry facade is tracked in #804).
    private DeltaStorageException DataFileDecoderSaturated(DecodeCapacityExhaustedException ex)
    {
        _telemetry.RecordDecodeCapacityExhausted(DecodeDoor.DataFile);
        return DeltaStorageException.DecoderSaturated(
            "The Parquet data-file decode was rejected because the bounded-decode worker is at capacity "
            + "(the door's memory budget / strand cap is exhausted by decodes running past their deadline). "
            + "Fail-closed; retry after capacity frees.", ex);
    }

    // Best-effort disposal of a ParquetReader whose (non-terminating) open completed AFTER the deadline — the
    // reader owns its input stream (leaveStreamOpen:false), so a late win must not leak it. Fire-and-forget on
    // the detached path (there is no caller to await); any dispose fault is swallowed.
    private static void DisposeAbandonedReader(ParquetReader? reader)
    {
        if (reader is null)
        {
            return;
        }

        _ = SafeDisposeAsync(reader);

        static async Task SafeDisposeAsync(ParquetReader r)
        {
            try
            {
                await r.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup on a detached, abandoned decode — nothing to observe it.
            }
        }
    }

    // A ref-counted lease over a caller-shared ParquetReader (I6 — data-file strand isolation). The row-group
    // decode (#647) and the row-count metadata scan run the untrusted ParquetReader on a dedicated background
    // thread that CANNOT be reclaimed if it never terminates (Parquet.Net ignores cancellation). If the caller
    // disposed the reader — and, via leaveStreamOpen:false, its input stream — while such a strand were still
    // reading it, the strand would touch a disposed reader/stream (a use-after-free-class fault). This lease
    // makes disposal happen ONLY when the LAST holder releases: the caller holds the base lease for the whole
    // read; each bounded decode Retain()s an extra lease before starting and Release()s it when the (possibly
    // stranded) decode settles (via BoundedDecode.onWorkSettled). A non-terminating strand therefore keeps the
    // reader alive for its whole lifetime — a bounded residual (≤ the door's strand cap, exactly as the
    // checkpoint door pins ≤ maxPartBytes of isolated bytes per strand) — and the reader is disposed only once
    // no strand holds it. This is the SAME ownership contract for both data-file strand doors.
    private sealed class SharedParquetReader
    {
        private readonly ParquetReader _reader;
        private int _leases; // starts at 1 (the caller's base lease); disposal fires when it reaches 0

        private SharedParquetReader(ParquetReader reader)
        {
            _reader = reader;
            _leases = 1;
        }

        internal static SharedParquetReader Wrap(ParquetReader reader) => new(reader);

        internal ParquetReader Reader => _reader;

        // Take an extra lease for a bounded decode about to start. The base lease is held for the whole read, so
        // the count is always ≥ 1 here and can never resurrect a disposed reader.
        internal void Retain() => Interlocked.Increment(ref _leases);

        // Release a bounded decode's lease (called from BoundedDecode.onWorkSettled when the — possibly
        // stranded — decode settles). Sync + fire-and-forget disposal because onWorkSettled is synchronous; only
        // the holder that drives the count to 0 disposes.
        internal void Release()
        {
            if (Interlocked.Decrement(ref _leases) == 0)
            {
                DisposeAbandonedReader(_reader);
            }
        }

        // Release the caller's base lease at the end of the read, awaiting disposal WHEN this is the last holder
        // (the common, no-strand case: deterministic stream close). If a strand still holds a lease the count
        // stays ≥ 1 and the reader is kept alive for the strand; the strand's later Release disposes it.
        internal async ValueTask ReleaseCallerAsync()
        {
            if (Interlocked.Decrement(ref _leases) == 0)
            {
                await _reader.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Best-effort disposal of a row-group ColumnBatch whose (non-terminating) decode completed AFTER the
    // deadline (a late win the caller has already stopped awaiting). Today the row-group decode yields a
    // ManagedColumnBatch backed purely by managed arrays (GC-reclaimed, nothing to release), but a batch type
    // MAY hold an unmanaged/pooled resource (e.g. an Arrow-backed batch is IDisposable), so dispose it if it is
    // disposable — a late result must never leak. Fire-and-forget on the detached path; any fault is swallowed.
    private static void DisposeAbandonedBatch(ColumnBatch? batch)
    {
        switch (batch)
        {
            case IAsyncDisposable asyncDisposable:
                _ = SafeDisposeAsync(asyncDisposable);
                break;
            case IDisposable disposable:
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Best-effort cleanup on a detached, abandoned decode — nothing to observe it.
                }

                break;
            default:
                break;
        }

        static async Task SafeDisposeAsync(IAsyncDisposable d)
        {
            try
            {
                await d.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup on a detached, abandoned decode — nothing to observe it.
            }
        }
    }

    /// <summary>A row-group pruning hint: return <see langword="false"/> to skip a row group whose
    /// <see cref="RowGroupStatistics"/> prove it cannot match. Pruning is a hint only — a kept group is
    /// still read in full and the residual predicate is the engine's responsibility (design §2.9.1).</summary>
    public delegate bool RowGroupPredicate(RowGroupStatistics statistics);

    /// <summary>Reads <paramref name="input"/>, projecting to <paramref name="requested"/> (a subset of
    /// the file schema by field name) and optionally skipping row groups via
    /// <paramref name="keepRowGroup"/>.
    ///
    /// <para><b>Batch-lifetime contract (load-bearing — do not weaken).</b> Every iteration of the returned
    /// sequence yields a <b>freshly allocated</b> <see cref="ColumnBatch"/> for one row group, backed by
    /// freshly-allocated column vectors; the reader NEVER recycles, pools, or overwrites a previously-yielded
    /// batch's backing buffers across a subsequent <c>MoveNext</c> (nor after the enumerator is disposed). A
    /// caller MAY therefore <b>retain</b> a yielded batch — or a zero-copy selection view over it — past the
    /// next <c>MoveNext</c>, and it stays valid and <b>unaliased</b> with respect to every other yielded
    /// batch. <see cref="DeltaSharp.Storage.Delta.DeltaDelete"/>'s Change Data Feed capture (§2.5/§4.3)
    /// depends on this: it accumulates one selection view per row group across the whole file and writes them
    /// all AFTER the scan completes, which a recycled/overwritten buffer would silently corrupt. A future
    /// buffer-pool optimization MUST preserve this contract (or hand retaining callers an explicit copy); a
    /// reader regression test pins it.</para></summary>
    /// <param name="input">The Parquet byte stream.</param>
    /// <param name="requested">The projection: the columns to read, by field name, in output order.</param>
    /// <param name="keepRowGroup">An optional row-group pruning hint (see <see cref="RowGroupPredicate"/>).</param>
    /// <param name="nullFillMissingColumns">When <see langword="true"/>, a <paramref name="requested"/> column
    /// that is <b>absent</b> from the file <i>and</i> <b>nullable</b> is materialized as an all-<c>null</c>
    /// column instead of failing — the additive schema-evolution (#190) read-side null-fill (#497): a file
    /// written under an older, narrower schema reads back through the current schema with the later-added
    /// columns null-filled. It never masks a genuine incompatibility: an absent <b>non-nullable</b> requested
    /// column still fails closed (a required column cannot be null-filled), and a <i>present</i> column whose
    /// physical type/nullability disagrees with the request is still rejected as a
    /// <see cref="StorageErrorKind.SchemaMismatch"/>. When <see langword="false"/> (the default for the
    /// general reader), an absent column of any nullability fails closed, preserving the reader's strict
    /// projection contract for callers that must not silently null-fill.</param>
    /// <param name="allowTypeWideningPromotion">When <see langword="true"/>, a <paramref name="requested"/>
    /// column whose physical (file) type is a NARROWER Delta-sanctioned widening of the requested type is
    /// PROMOTED into the requested wide type on read (Delta PROTOCOL.md "Reader Requirements for Type
    /// Widening", #495). When <see langword="false"/> (the strict default), a narrower physical type is NOT
    /// promotable — the exact physical/engine type mismatch fails closed as
    /// <see cref="StorageErrorKind.SchemaMismatch"/>. This gate is the READ-side counterpart to the write-side
    /// enablement check: only a caller that KNOWS the table declares the <c>typeWidening</c> feature in its
    /// snapshot protocol may pass <see langword="true"/>, so a tampered/malformed external log (a wide schema
    /// over narrow files with NO <c>typeWidening</c> feature) fails closed rather than being silently
    /// "repaired". Promotion trusts this caller-supplied gate: the scan layer knows the protocol, the
    /// stream-level reader does not.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DeltaStorageException">A requested column type is unsupported, or the file uses a
    /// valid-but-unsupported library feature such as Parquet Modular Encryption — <b>either</b> footer mode:
    /// an encrypted-footer (<c>PARE</c>-magic) file or a plaintext-footer file whose footer carries the crypto
    /// metadata (<see cref="ParquetEncryption"/>) — (<see cref="StorageErrorKind.UnsupportedFeature"/>); the resolved file column's
    /// physical type or nullability does not match the requested engine type
    /// (<see cref="StorageErrorKind.SchemaMismatch"/>); a requested column is absent from the file and not
    /// null-filled (per <paramref name="nullFillMissingColumns"/>)
    /// (<see cref="StorageErrorKind.ColumnNotPresentInFile"/>); or the file is malformed/truncated or a row
    /// group's declared size exceeds the decode ceiling (<see cref="StorageErrorKind.CorruptData"/>).</exception>
    public IAsyncEnumerable<ColumnBatch> ReadAsync(
        Stream input,
        StructType requested,
        RowGroupPredicate? keepRowGroup,
        bool nullFillMissingColumns,
        bool allowTypeWideningPromotion,
        CancellationToken cancellationToken)
        => ReadAsync(input, requested, keepRowGroup, nullFillMissingColumns, allowTypeWideningPromotion,
            resolveByFieldId: false, cancellationToken);

    /// <summary>
    /// As <see cref="ReadAsync(Stream, StructType, RowGroupPredicate?, bool, bool, CancellationToken)"/>, but
    /// when <paramref name="resolveByFieldId"/> is <see langword="true"/> each requested column is resolved
    /// <b>independently</b> (Delta column-mapping <b>id</b> mode, #523/#658): a column that carries a
    /// <c>delta.columnMapping.id</c> is matched by that id against the Parquet footer's
    /// <c>SchemaElement.field_id</c> (not by physical name — a logical rename that never rewrote the file still
    /// reads through), while a column that carries <b>no</b> id falls back to by-<b>name</b> resolution. The
    /// by-name fallback lets a single call project id-mapped data columns <b>alongside</b> an engine-synthesized
    /// column that is never column-mapped (e.g. CDF's <c>_change_type</c>). A column that declares an id whose
    /// value is absent from the footer fails <b>closed</b> (it is never silently name-matched). The file's
    /// field_ids come from the Thrift footer via <see cref="ParquetReader.Metadata"/> (the high-level
    /// <c>DataField.FieldId</c> is not populated on decode). The batch-lifetime (no-recycling) contract
    /// documented on the 6-argument overload applies unchanged — this is the overload
    /// <see cref="DeltaSharp.Storage.Delta.DeltaDelete"/>'s cdc capture enumerates.
    /// </summary>
    public async IAsyncEnumerable<ColumnBatch> ReadAsync(
        Stream input,
        StructType requested,
        RowGroupPredicate? keepRowGroup,
        bool nullFillMissingColumns,
        bool allowTypeWideningPromotion,
        bool resolveByFieldId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(requested);

        // Validate every requested column maps to a supported Parquet read shape BEFORE any decode, so an
        // unsupported projection fails deterministically without materializing a partial batch. Beyond the
        // scalar mappings, the reader decodes the three single-level nested shapes (#571: struct-of-scalar,
        // array-of-scalar, map-of-scalar); anything nested further fails closed here.
        for (int c = 0; c < requested.Count; c++)
        {
            ParquetTypeMapping.EnsureReadSupported(requested[c]);
        }

        var deadlineBudget = _limits.DecodeTimeBudget;
        ParquetReader opened = await OpenAsync(input, cancellationToken).ConfigureAwait(false);
        // I6 — hold a ref-counted lease over the reader for the whole read so a stranded row-group decode never
        // touches a reader/stream the caller has disposed. The base lease is released in the finally; each
        // bounded row-group decode takes its own extra lease released when that (possibly stranded) decode
        // settles. The reader is disposed only once no strand holds it.
        var shared = SharedParquetReader.Wrap(opened);
        try
        {
            ParquetReader reader = shared.Reader;
            // Structural validation happens here (footer read at open) — schema/type mismatches fail
            // before any batch is yielded (H3). An Absent slot marks a requested column not present in the
            // file that will be null-filled (nullFillMissingColumns; #497); a Nested slot carries the
            // resolved nested Field graph (#571).
            IReadOnlyDictionary<int, DataField>? byFieldId = resolveByFieldId ? BuildFieldIdMap(reader) : null;
            ResolvedColumn[] fileFields = ResolveFileFields(
                reader.Schema, requested, nullFillMissingColumns, allowTypeWideningPromotion, byFieldId);
            for (int group = 0; group < reader.RowGroupCount; group++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Each row group's bounded decode gets its OWN per-operation budget measured from that decode's
                // execution start (I7) — the budget never spans the `yield return` suspension below, so consumer
                // time between MoveNextAsync calls is never charged to the decode. A crafted file with a
                // non-terminating group fails closed on that group after one budget (aborting the whole read);
                // the strand residual is bounded by the data-file door's byte-aware admission cap.
                ColumnBatch? batch = await ReadRowGroupAsync(
                    shared, group, requested, fileFields, keepRowGroup, _limits, deadlineBudget, _dataFileDecoder,
                    _timeProvider, _telemetry, allowTypeWideningPromotion, cancellationToken)
                    .ConfigureAwait(false);
                if (batch is not null)
                {
                    yield return batch;
                }
            }
        }
        finally
        {
            await shared.ReleaseCallerAsync().ConfigureAwait(false);
        }
    }

    // Column-mapping id mode (#523): correlates the Parquet footer's field_ids with the file's leaf data
    // fields. The footer SchemaElements (reader.Metadata.Schema) carry field_id but not the decoded values;
    // reader.Schema.DataFields carry the values but always read FieldId == -1 (Parquet.Net's high-level decode
    // never copies the footer field_id onto DataField). Correlating the two by physical name (a DeltaSharp
    // file is flat) yields the field_id → DataField map an id-mode reader resolves against. A duplicate
    // field_id in a foreign footer is rejected fail-closed rather than silently taking the last writer.
    private static IReadOnlyDictionary<int, DataField> BuildFieldIdMap(ParquetReader reader)
    {
        var byName = new Dictionary<string, DataField>(StringComparer.Ordinal);
        foreach (DataField dataField in reader.Schema.DataFields)
        {
            byName[dataField.Name] = dataField;
        }

        var byFieldId = new Dictionary<int, DataField>();
        IReadOnlyList<global::Parquet.Meta.SchemaElement>? footer = reader.Metadata?.Schema;
        if (footer is not null)
        {
            foreach (global::Parquet.Meta.SchemaElement element in footer)
            {
                if (element.FieldId is int fieldId && byName.TryGetValue(element.Name, out DataField? dataField))
                {
                    if (!byFieldId.TryAdd(fieldId, dataField!))
                    {
                        throw DeltaStorageException.SchemaMismatch(
                            $"The Parquet file declares duplicate field_id {fieldId} — a column-mapping id-mode "
                            + "table must assign each column a unique field_id.");
                    }
                }
            }
        }

        return byFieldId;
    }

    /// <summary>
    /// Reads only the Parquet footer and returns the file's total PHYSICAL row count (summed across row
    /// groups) — decoding no data pages. This is the file's real row count, used to bound a deletion vector's
    /// decoded positions by the truth on disk (never an attacker-controlled descriptor/stats field), so a
    /// poisoned DV can neither reference a row beyond the file nor force an oversized allocation.
    /// </summary>
    /// <exception cref="DeltaStorageException">The Parquet footer is malformed/truncated, or a row group
    /// declares a negative row count (<see cref="StorageErrorKind.CorruptData"/>, fail closed); or the file
    /// uses Parquet Modular Encryption, in either footer mode — an encrypted-footer (<c>PARE</c>-magic) file
    /// or a plaintext-footer file carrying the crypto metadata (<see cref="ParquetEncryption"/>)
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>).</exception>
    public async Task<long> GetRowCountAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        ParquetReader opened = await OpenAsync(input, cancellationToken).ConfigureAwait(false);
        // I6 — the metadata scan below runs the caller-owned reader on a dedicated background thread that cannot
        // be reclaimed if it strands. Hold a ref-counted lease so a stranded scan never touches a reader/stream
        // the caller disposed; the reader is disposed only once no strand holds it.
        var shared = SharedParquetReader.Wrap(opened);
        try
        {
            ParquetReader reader = shared.Reader;
            // Fail-closed row-group-count boundary (storage-delta-architecture.md §5.4 C-DECODE / ADR-0013).
            // The summation reads attacker-controlled footer NumRows fields, so a crafted file whose per-group
            // row counts sum past long.MaxValue raises a raw OverflowException from checked(total + rows). This
            // metadata-only entry point must fail closed like ReadRowGroupAsync: map every library fault (from
            // the summation, OpenRowGroupReader, or RowCount on a crafted footer) to the deterministic
            // CorruptData contract. The typed negative-count CorruptData (a DeltaStorageException) and
            // cooperative cancellation (an OperationCanceledException) are both EXCLUDED by the predicates below,
            // so they still propagate UNWRAPPED (never double-mapped, cancellation still wins).
            //
            // Bounded-time (#647). The loop iterates the ATTACKER-CONTROLLED RowGroupCount, and each
            // OpenRowGroupReader/RowCount touches untrusted footer metadata that Parquet.Net 6.1.0 can be driven
            // into non-terminating, cancellation-ignoring work over. This DV-bounding door (used to bound a
            // deletion vector's decoded positions by the truth on disk) is therefore bounded under its OWN
            // per-operation wall-clock budget (stage=metadata) so a crafted footer fails closed with a DISTINCT
            // DecodeBudgetExceeded rather than hanging here. A stranded scan keeps the reader alive via an extra
            // lease on `shared` (I6), released via onWorkSettled when it settles; door saturation surfaces the
            // retryable DecoderSaturated (I8), never a decode-timeout.
            try
            {
                shared.Retain();
                return await _dataFileDecoder.RunAsync(
                    decodeToken =>
                    {
                        long total = 0;
                        for (int group = 0; group < reader.RowGroupCount; group++)
                        {
                            decodeToken.ThrowIfCancellationRequested();
                            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(group);
                            long rows = rowGroup.RowCount;
                            if (rows < 0)
                            {
                                throw DeltaStorageException.CorruptData(
                                    $"Row group {group} declares a negative row count ({rows}).");
                            }

                            total = checked(total + rows);
                        }

                        return Task.FromResult(total);
                    },
                    _limits.DecodeTimeBudget,
                    _ => DataFileDecodeTimeout(
                        "The Parquet footer row-group metadata could not be summed within the bounded-decode "
                        + "time budget (possible crafted footer driving a non-terminating metadata scan).",
                        DecodeStage.Metadata),
                    cancellationToken,
                    onAbandonedResult: null,
                    onWorkSettled: shared.Release,
                    timeProvider: _timeProvider,
                    estimatedRetainedBytes: EstimateOpenRetainedBytes(input)).ConfigureAwait(false);
            }
            catch (DecodeCapacityExhaustedException capacity)
            {
                // Rejected without starting: the lease is released via onWorkSettled (which fires on the
                // capacity-rejection path); surface the retryable saturation (never a decode-timeout).
                throw DataFileDecoderSaturated(capacity);
            }
            catch (Exception ex) when (IsParquetDefect(ex))
            {
                // Expected family: checked(total + rows) over attacker-controlled footer NumRows fields raises
                // OverflowException (which IsParquetDefect lists) when they sum past long.MaxValue. Fixed
                // message (no ex.Message) so no footer content echoes into the error text (info-leak posture).
                throw DeltaStorageException.CorruptData(
                    "Parquet footer declares an implausible total row count (row-group counts overflow).", ex);
            }
            catch (Exception ex) when (IsUndecodableParquetInput(ex))
            {
                // Superset fallback for any other raw library fault from OpenRowGroupReader / RowCount on a
                // crafted footer, so this metadata-only entry point can never leak a raw BCL exception.
                throw DeltaStorageException.CorruptData(
                    "Parquet footer row-group metadata is malformed.", ex);
            }
        }
        finally
        {
            await shared.ReleaseCallerAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads only the Parquet footer and reconstructs the file's <b>actual physical data schema</b> (field
    /// names + DeltaSharp types via <see cref="ParquetTypeMapping.ToDataSchema"/>), decoding no data pages.
    /// The write-door records this on each staged file so schema enforcement gates the <b>real written
    /// bytes</b>, not the caller's declared write schema (#497). Nullability is the footer's physical
    /// REPETITION (which since #730 matches the declared schema for files DeltaSharp writes, but need not on
    /// a foreign or pre-#730 file); callers compare the returned schema by name + type only.
    /// </summary>
    /// <exception cref="DeltaStorageException">The footer is malformed/truncated
    /// (<see cref="StorageErrorKind.CorruptData"/>), or a footer field has no supported DeltaSharp type
    /// mapping — or the file uses Parquet Modular Encryption, in either footer mode (an encrypted-footer
    /// <c>PARE</c>-magic file, or a plaintext-footer file carrying the crypto metadata; see
    /// <see cref="ParquetEncryption"/>) (<see cref="StorageErrorKind.UnsupportedFeature"/>).</exception>
    public async Task<StructType> ReadDataSchemaAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        ParquetReader reader = await OpenAsync(input, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            return MapFooterSchemaFailClosed(reader);
        }
    }

    // Fail-closed schema-mapping boundary (storage-delta-architecture.md §5.4 C-DECODE / ADR-0013), shared by
    // ReadDataSchemaAsync and ReadDataLeafColumnsAsync so the two footer-schema readers can NEVER diverge on it.
    // OpenAsync force-materialized reader.Schema inside its footer-PARSE boundary, but the SUBSEQUENT mapping of
    // untrusted footer field descriptors into DeltaSharp StructFields is a distinct decode step that was
    // unsealed: ToDataSchema eagerly builds a StructField for EVERY footer field, so a crafted footer with an
    // empty field name makes the StructField constructor raise a raw ArgumentException (PDX-T crafted schema).
    // Map every library/domain fault from the mapping to the deterministic CorruptData contract with a FIXED
    // message (no ex.Message, so no footer content echoes into the error text). ToDataSchema's legitimate typed
    // UnsupportedFeature (an unsupported-but-VALID Parquet type) is a DeltaStorageException, EXCLUDED by both
    // predicates below, so it still propagates UNWRAPPED (never re-masked as CorruptData).
    private static StructType MapFooterSchemaFailClosed(ParquetReader reader)
    {
        try
        {
            return ParquetTypeMapping.ToDataSchema(reader.Schema);
        }
        catch (Exception ex) when (IsParquetDefect(ex))
        {
            // Informative first catch: a low-level decode defect surfacing while mapping a field descriptor
            // (e.g. an OverflowException from a crafted decimal precision/scale).
            throw DeltaStorageException.CorruptData(
                "Parquet footer schema is malformed (undecodable field descriptor).", ex);
        }
        catch (Exception ex) when (IsUndecodableParquetInput(ex))
        {
            // Superset fallback: the empty-field-name ArgumentException from the StructField constructor and
            // any other raw fault from mapping an attacker-controlled footer field descriptor land here.
            throw DeltaStorageException.CorruptData(
                "Parquet footer declares an unmappable field descriptor (e.g. an empty field name).", ex);
        }
    }

    /// <summary>One decoded leaf column of a Parquet file: its name, DeltaSharp <see cref="DataType"/>, and
    /// (when the footer assigns one) its column-mapping <c>field_id</c> — <see langword="null"/> for a leaf the
    /// footer gives no <c>field_id</c> (e.g. the engine-synthesized <c>_change_type</c>). The unit
    /// <see cref="ReadDataLeafColumnsAsync"/> returns for CDF-EE-08 id-mode validation (#662).</summary>
    internal readonly record struct ParquetLeafColumn(string Name, DataType Type, int? FieldId);

    /// <summary>
    /// Reads only the Parquet footer and returns the file's leaf data columns — each carrying its name,
    /// DeltaSharp type (via <see cref="ParquetTypeMapping.ToDataSchema"/>), and (when the footer assigns one)
    /// its <c>field_id</c> — decoding no data pages, in file order. This is the <b>field-id-aware sibling</b> of
    /// <see cref="ReadDataSchemaAsync"/>: the CDF explicit (cdc) read validates a foreign column-mapping
    /// <b>id</b>-mode cdc file's leaf schema by <c>field_id</c> (the Delta id-mode authority) rather than by
    /// physical name, because a foreign id-mode file's physical Parquet column names may diverge from the
    /// metaData <c>physicalName</c>s (CDF-EE-08, #662). A leaf the footer assigns no <c>field_id</c> reports
    /// <see cref="ParquetLeafColumn.FieldId"/> == <see langword="null"/>.
    /// </summary>
    /// <exception cref="DeltaStorageException">The footer is malformed/truncated, or a footer field has no
    /// supported DeltaSharp type mapping (<see cref="StorageErrorKind.CorruptData"/>); the footer declares a
    /// duplicate <c>field_id</c> (<see cref="StorageErrorKind.SchemaMismatch"/>, via
    /// <see cref="BuildFieldIdMap"/>); or the file uses Parquet Modular Encryption
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>) — all fail closed.</exception>
    internal async Task<IReadOnlyList<ParquetLeafColumn>> ReadDataLeafColumnsAsync(
        Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        ParquetReader reader = await OpenAsync(input, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            StructType schema = MapFooterSchemaFailClosed(reader);

            // Correlate the footer field_ids with the leaves (BuildFieldIdMap fails closed on a duplicate
            // field_id — a typed DeltaStorageException that propagates unwrapped, mirroring the ReadAsync id
            // path). Invert to name → field_id so each leaf carries its own id; a leaf with no footer field_id
            // (e.g. `_change_type`) stays null.
            IReadOnlyDictionary<int, DataField> byFieldId = BuildFieldIdMap(reader);
            var fieldIdByName = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<int, DataField> entry in byFieldId)
            {
                fieldIdByName[entry.Value.Name] = entry.Key;
            }

            var columns = new List<ParquetLeafColumn>(schema.Count);
            foreach (StructField field in schema)
            {
                int? fieldId = fieldIdByName.TryGetValue(field.Name, out int id) ? id : null;
                columns.Add(new ParquetLeafColumn(field.Name, field.DataType, fieldId));
            }

            return columns;
        }
    }

    /// <summary>The declared compressed/decompressed footprint of one projected column chunk, plus the
    /// per-row width of the read buffer the reader will eagerly allocate for it (including any
    /// <see cref="Nullable{T}"/> overhead) — the inputs to <see cref="EnsureDecodeCeiling"/>.
    /// <paramref name="Absent"/> marks a column that is <b>not present</b> in the file and will be
    /// null-filled (#497): it declares no compressed/decompressed bytes (nothing is decoded) but still
    /// carries its element width so the null-fill allocation is bounded by the same row-count ceiling.
    /// </summary>
    internal readonly record struct ColumnChunkFootprint(
        long CompressedBytes, long UncompressedBytes, int ElementBytes, bool Absent = false,
        bool IsVariableWidth = false);

    /// <summary>Fails closed when a row group's declared metadata would exceed this reader's
    /// <b>eager-decode</b> memory ceiling (design §5.4 C-DECODE), so neither a crafted footer nor a
    /// genuinely oversized row group can drive an out-of-memory allocation. Over the projected column
    /// chunks it enforces: (0) a <b>footprint-0 guard</b> — a projected chunk declaring zero decompressed
    /// bytes for a non-empty row group is a stripped/absent footer whose real pages would still decode
    /// unbounded, so it is rejected; (i) a per-chunk decompression-<b>ratio</b> ceiling
    /// (<see cref="MaxDecompressionRatio"/>); (ii) an <b>absolute</b> decompressed-size ceiling
    /// (<see cref="MaxRowGroupDecodedBytes"/>); and (iii) a <b>row-count</b> bound — the bytes the declared
    /// <paramref name="rowCount"/> would eagerly materialize, at the column's <b>actual</b> allocated
    /// element width (including any <see cref="Nullable{T}"/> overhead), must not exceed the same absolute
    /// ceiling. A negative row count or a negative declared size is likewise rejected. These declared-size
    /// bounds are sound where a physical rows/byte proxy is not: legitimately compressible data
    /// (constant/RLE/all-null columns, which encode millions of rows in a few hundred bytes) passes, while
    /// an inflated decompressed size or row count is caught. A <b>page-header-inflated</b> size within a
    /// non-zero footer is <i>not</i> caught here — that needs page-level streaming decode (Parquet.Net's
    /// public API does not expose it), tracked in #472.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="projectedChunks"/> is null.</exception>
    /// <exception cref="DeltaStorageException">A declared value is negative, or a ceiling is exceeded
    /// (<see cref="StorageErrorKind.CorruptData"/>).</exception>
    internal static void EnsureDecodeCeiling(
        long rowCount, IReadOnlyList<ColumnChunkFootprint> projectedChunks, int group, ParquetDecodeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(projectedChunks);
        limits ??= ParquetDecodeLimits.Default;
        if (rowCount < 0)
        {
            throw DeltaStorageException.CorruptData(
                $"Row group {group} declares a negative row count ({rowCount}).");
        }

        long totalDecompressedBytes = 0;
        // (iv) Running TOTAL of the bytes the projected columns would EAGERLY MATERIALIZE simultaneously
        // (Round-8 #2). The decode allocates ColumnVector[requested.Count] and holds every projected column's
        // decoded vector at once, so the retained materialization is Σ(rows × elementWidth) ACROSS columns — not
        // the per-column bound (iii) alone. Bounding the SUM to the ceiling keeps the door's max footprint a
        // bounded over-approximation within a documented constant factor (Round-10 #5): the flat materialization
        // ceiling here + the decompressed ceiling (ii) + the independent nested-reconstruction budget together
        // stay within BoundedDecode.DataFileMaxFootprintBytes (3× the row-group ceiling) — NOT a strict Σ
        // ceiling, but a documented containment. This still improves on a per-column truncation that would
        // under-count the strand charge by the column count.
        long totalMaterializedBytes = 0;
        for (int c = 0; c < projectedChunks.Count; c++)
        {
            ColumnChunkFootprint chunk = projectedChunks[c];

            // A null-filled absent column (#497) is never decoded: it declares no bytes and skips the
            // decompression guards (0)/(i)/(ii) below, but still applies the (iii) row-count element-width
            // bound further down so its all-null materialization stays inside the same eager-decode ceiling.
            if (chunk.Absent)
            {
                if (chunk.ElementBytes > 0 && rowCount > limits.MaxRowGroupDecodedBytes / chunk.ElementBytes)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Row group {group} declares {rowCount} rows, whose null-fill of an absent column would "
                        + $"eagerly materialize more than the {limits.MaxRowGroupDecodedBytes}-byte eager-decode "
                        + $"ceiling for a {chunk.ElementBytes}-byte column.");
                }

                totalMaterializedBytes = SaturatingAdd(totalMaterializedBytes, SaturatingMul(rowCount, chunk.ElementBytes));
                continue;
            }

            if (chunk.CompressedBytes < 0 || chunk.UncompressedBytes < 0)
            {
                throw DeltaStorageException.CorruptData(
                    $"Row group {group} declares a negative column-chunk size (compressed "
                    + $"{chunk.CompressedBytes}, decompressed {chunk.UncompressedBytes}).");
            }

            // (0) Metadata-stripped-footer / footprint-0 guard (design §5.4 C-DECODE). A projected column
            // chunk that declares ZERO decompressed bytes while the row group has rows is malformed: the
            // Parquet spec defines TotalUncompressedSize as the size of all pages *including page headers*,
            // so any present column with rows has >= 1 page and therefore declares a strictly positive size
            // (this holds for required columns with no definition levels too). A zero is thus a
            // stripped/absent/missing-metadata footer. Its real data pages would still decode — Parquet.Net
            // reads pages by offset and decompresses each to its page-header size — so the declared-size
            // ceiling below cannot bound it. Fail closed rather than hand an unbounded chunk to the decoder.
            // (A per-PAGE declared-vs-produced control that also catches a page-header-inflated size WITHIN a
            // non-zero footer needs page-level streaming decode, which Parquet.Net's public API does not
            // expose — tracked in #472.)
            if (rowCount > 0 && chunk.UncompressedBytes == 0)
            {
                throw DeltaStorageException.CorruptData(
                    $"Row group {group} projects a column chunk that declares zero decompressed bytes for "
                    + $"{rowCount} rows (a metadata-stripped or absent footer); the reader cannot bound its "
                    + "decode and fails closed.");
            }

            // (i) Decompression-ratio ceiling: a chunk claiming far more decompressed than compressed
            // bytes is a decompression bomb. The floor of 1 avoids a divide-by/zero-compressed edge. The
            // product is widened to Int128 so a large declared compressed size cannot overflow the 64-bit
            // multiply into a spurious verdict (wrapping to a negative or small-positive threshold that would
            // either wrongly reject a legitimate chunk or wrongly accept a bomb).
            long compressedFloor = Math.Max(chunk.CompressedBytes, 1);
            if (chunk.UncompressedBytes > (Int128)compressedFloor * limits.MaxDecompressionRatio)
            {
                throw DeltaStorageException.CorruptData(
                    $"Row group {group} declares {chunk.UncompressedBytes} decompressed bytes for "
                    + $"{chunk.CompressedBytes} compressed, exceeding the {limits.MaxDecompressionRatio}:1 "
                    + "decompression-ratio ceiling (possible decompression bomb).");
            }

            // (iii) Row-count bound: reject BEFORE the eager per-column allocation if this row count would
            // materialize more than the absolute eager-decode ceiling. ElementBytes is the ACTUAL read
            // buffer width (Nullable<T>-aware, RF-4a). Dividing (rather than multiplying rowCount by the
            // width) keeps the check overflow-safe for an attacker-sized row count.
            if (chunk.ElementBytes > 0 && rowCount > limits.MaxRowGroupDecodedBytes / chunk.ElementBytes)
            {
                throw DeltaStorageException.CorruptData(
                    $"Row group {group} declares {rowCount} rows, which would eagerly materialize more "
                    + $"than the {limits.MaxRowGroupDecodedBytes}-byte eager-decode ceiling for a "
                    + $"{chunk.ElementBytes}-byte column.");
            }

            totalDecompressedBytes = SaturatingAdd(totalDecompressedBytes, chunk.UncompressedBytes);
            totalMaterializedBytes = SaturatingAdd(totalMaterializedBytes, SaturatingMul(rowCount, chunk.ElementBytes));
        }

        // (ii) Absolute decompressed-size ceiling over the row group's projected chunks.
        if (totalDecompressedBytes > limits.MaxRowGroupDecodedBytes)
        {
            throw DeltaStorageException.CorruptData(
                $"Row group {group} declares {totalDecompressedBytes} decompressed bytes across its "
                + $"projected columns, exceeding the {limits.MaxRowGroupDecodedBytes}-byte eager-decode ceiling.");
        }

        // (iv) Absolute TOTAL-materialization ceiling (Round-8 #2): the SUM of the bytes every projected column
        // would eagerly materialize simultaneously must not exceed the same ceiling, so the actual retained
        // footprint of a whole-row-group decode stays a bounded over-approximation within a documented constant
        // factor — the flat materialization ceiling here + the decompressed ceiling (ii) + the independent
        // nested-reconstruction budget together within BoundedDecode.DataFileMaxFootprintBytes (3× the ceiling,
        // Round-10 #5). Without this, N projected columns each within the per-column bound could retain up to N×
        // the ceiling while the strand charge clamped to one ceiling.
        if (totalMaterializedBytes > limits.MaxRowGroupDecodedBytes)
        {
            throw DeltaStorageException.CorruptData(
                $"Row group {group} declares {rowCount} rows whose projected columns would eagerly materialize "
                + $"{totalMaterializedBytes} bytes in total, exceeding the {limits.MaxRowGroupDecodedBytes}-byte "
                + "eager-decode ceiling.");
        }
    }

    private static long SaturatingAdd(long a, long b) => b > long.MaxValue - a ? long.MaxValue : a + b;

    // A documented over-approximation multiplier for VARIABLE-WIDTH (string/binary) column materialization
    // (Round-10 #5). AllocatedElementByteWidth charges only IntPtr.Size for a string/binary element — the per-row
    // managed REFERENCE — but the real materialization is the decoded UTF-16 string object (UTF-8 backing can
    // roughly double into UTF-16, plus per-object header/length overhead). The strand charge therefore adds the
    // chunk's decompressed backing bytes scaled by this factor for a string/binary chunk, so a stranded decode of
    // a wide string column is not UNDER-charged. Four is a conservative upper bound on UTF-8→UTF-16 expansion
    // plus object overhead for short strings; #802 tracks calibration. This is used ONLY for the strand CHARGE
    // (an over-approximation is safe), never for the accept/reject ceiling (which would falsely reject).
    internal const long VariableWidthMaterializationFactor = 4;

    // The row group's REAL projected retained footprint — the sum, over its projected column chunks, of each
    // chunk's declared decompressed bytes PLUS the bytes its declared row count would eagerly materialize at the
    // chunk's element width PLUS, for a variable-width (string/binary) chunk, an over-approximation of its UTF-16
    // materialization (decompressed backing × VariableWidthMaterializationFactor, Round-10 #5) — clamped to the
    // door's max footprint (a bounded over-approximation within a documented constant factor: the decompressed
    // ceiling + the flat materialization ceiling + the independent nested-reconstruction budget, Round-10 #5).
    // Used as the charge a STRAND of this decode books against the data-file door's stranded residual at detach
    // (the honest per-row-group footprint, hoisted out of the bounded decode so it is known BEFORE the decode
    // runs; it is NOT clamped to ONE ceiling, which would UNDER-count by the column count). It reads the
    // already-parsed footer metadata (the open was bounded), so it is a cheap in-memory read; on ANY metadata
    // fault it falls back to the door's max footprint (conservative — a strand's charge must never UNDER-count).
    // Exposed internally for a direct Σ-footprint / clamp / fault-fallback / overflow-saturation test. (The
    // eager-decode LIMITS are intentionally NOT a parameter: the strand charge is clamped to the DOOR footprint
    // — the true per-strand retention ceiling RunAsync re-clamps to — not to a single row-group's accept/reject
    // ceiling, which would UNDER-count the multi-column Σ. Round-10 #11.)
    internal static long EstimateRowGroupRetainedBytes(
        ParquetReader reader, int group, StructType requested, ResolvedColumn[] fileFields)
    {
        try
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(group);
            long rowCount = Math.Max(rowGroup.RowCount, 0);
            long total = 0;
            foreach (ColumnChunkFootprint chunk in ProjectedFootprints(rowGroup, requested, fileFields))
            {
                long decompressed = Math.Max(chunk.UncompressedBytes, 0);
                total = SaturatingAdd(total, decompressed);
                if (chunk.ElementBytes > 0)
                {
                    total = SaturatingAdd(total, SaturatingMul(rowCount, chunk.ElementBytes));
                }

                // Variable-width UTF-16 materialization over-approximation (Round-10 #5): a string/binary chunk
                // materializes decoded string objects far larger than the IntPtr-sized reference the flat width
                // charges, so add its decompressed backing scaled by the documented factor.
                if (chunk.IsVariableWidth)
                {
                    total = SaturatingAdd(total, SaturatingMul(decompressed, VariableWidthMaterializationFactor));
                }
            }

            // Charge the HONEST total (Σ decompressed + Σ flat-materialized + Σ variable-width over-approx),
            // clamped to the door's per-strand max footprint — NOT to ONE row-group ceiling (which truncated the
            // charge to a single column's worth). RunAsync re-clamps to the door footprint too; clamping here
            // keeps the estimate a plausible byte count.
            return Math.Clamp(total, 0, BoundedDecode.DataFileMaxFootprintBytes);
        }
        catch
        {
            // A crafted/undecodable footer can fault the estimate; the bounded decode itself fails closed on the
            // same fault, but the residual charge must not under-count — charge the door's max footprint.
            return BoundedDecode.DataFileMaxFootprintBytes;
        }
    }

    private static long SaturatingMul(long a, long b)
    {
        if (a <= 0 || b <= 0)
        {
            return 0;
        }

        return a > long.MaxValue / b ? long.MaxValue : a * b;
    }

    // The declared footprint the reader will decode/materialize for each projected column, pulled from
    // the row group's Parquet metadata (CF-1). A PRESENT chunk with missing/encrypted chunk metadata
    // contributes a zero decompressed footprint, which EnsureDecodeCeiling rejects fail-closed for a
    // non-empty row group (guard (0)): a present column with rows always declares a positive decompressed
    // size, so a zero is a stripped/absent footer whose real pages would still decode unbounded (§5.4). A
    // requested column ABSENT from the file (#497 null-fill) is marked Absent so it skips the decompression
    // guards but keeps its element width for the (iii) null-fill allocation bound. A NESTED column (#571)
    // contributes one footprint per leaf: each leaf's declared compressed/decompressed bytes feed the
    // decompression-ratio and absolute-size guards; the eager per-leaf allocation itself is bounded
    // separately (by the leaf's declared value count) inside NestedParquetColumnReader.
    private static List<ColumnChunkFootprint> ProjectedFootprints(
        ParquetRowGroupReader rowGroup, StructType requested, ResolvedColumn[] fileFields)
    {
        var footprints = new List<ColumnChunkFootprint>(requested.Count);
        for (int c = 0; c < requested.Count; c++)
        {
            ResolvedColumn resolved = fileFields[c];
            if (resolved.IsAbsent)
            {
                footprints.Add(
                    new ColumnChunkFootprint(
                        CompressedBytes: 0,
                        UncompressedBytes: 0,
                        AllocatedElementByteWidth(requested[c].DataType, nullable: true),
                        Absent: true));
                continue;
            }

            if (resolved.Nested is { } nestedField)
            {
                var leaves = new List<DataField>();
                NestedParquetColumnReader.CollectLeafFields(nestedField, leaves);

                // A1 (DoS): the container's OWN reconstructed structure — a list/map's per-row offsets plus
                // per-row null flags, or a struct's per-row null flags — is a rowCount-scaled allocation with
                // no backing column chunk, so the leaves' declared bytes do not cover it. Fold its per-row
                // width into the FIRST leaf's (iii) row-count bound so an implausible declared rowCount (e.g. a
                // forged footer NumRows) is rejected in EnsureDecodeCeiling BEFORE ReadRowGroupAsync allocates
                // the offsets/nulls arrays. The leaves' own value/level buffers are bounded separately (by
                // their declared value count) in NestedParquetColumnReader.LeafNumValues; ElementBytes 0 on the
                // remaining leaves keeps them off the per-row bound (a repeated leaf can hold more values than
                // there are rows, so that bound does not apply to them).
                int structuralWidth = NestedContainerStructuralWidth(requested[c].DataType);
                for (int leafIndex = 0; leafIndex < leaves.Count; leafIndex++)
                {
                    (long leafCompressed, long leafUncompressed) = ChunkBytes(rowGroup, leaves[leafIndex]);
                    footprints.Add(new ColumnChunkFootprint(
                        leafCompressed, leafUncompressed, ElementBytes: leafIndex == 0 ? structuralWidth : 0));
                }

                continue;
            }

            DataField fileField = resolved.Scalar!;
            (long compressed, long uncompressed) = ChunkBytes(rowGroup, fileField);
            footprints.Add(
                new ColumnChunkFootprint(
                    compressed,
                    uncompressed,
                    // For a type-widening promotion (#495) the file column is physically NARROWER than the
                    // requested (wide) type, yet the reader materializes the WIDE vector (ColumnVectors.Create
                    // over the requested type) — so the requested width is the dominant eager allocation and a
                    // safe (never under-counting) bound. The physical narrow read buffer is smaller and
                    // additive; the requested width upper-bounds the transient either way.
                    AllocatedElementByteWidth(requested[c].DataType, fileField.IsNullable),
                    IsVariableWidth: IsVariableWidthType(requested[c].DataType)));
        }

        return footprints;
    }

    // The per-row byte cost of a nested container's OWN reconstructed structure (independent of its leaf
    // values): a list/map allocates a per-row offset (int) plus a per-row null flag (bool); a struct allocates
    // only a per-row null flag. Feeds the (iii) row-count decode bound (A1) so a forged/implausible rowCount is
    // rejected before that rowCount-scaled structure is allocated.
    private static int NestedContainerStructuralWidth(DataType nested) => nested switch
    {
        ArrayType or MapType => sizeof(int) + sizeof(bool),
        _ => sizeof(bool),
    };

    // The declared (compressed, decompressed) bytes of a column chunk, or (0, 0) when the chunk is absent or
    // its metadata is missing/encrypted — a zero the (0) guard rejects fail-closed for a non-empty row group.
    private static (long Compressed, long Uncompressed) ChunkBytes(ParquetRowGroupReader rowGroup, DataField field)
    {
        if (rowGroup.ColumnExists(field))
        {
            global::Parquet.Meta.ColumnMetaData? meta = rowGroup.GetMetadata(field)?.MetaData;
            if (meta is not null)
            {
                return (meta.TotalCompressedSize, meta.TotalUncompressedSize);
            }
        }

        return (0, 0);
    }

    // The per-row byte width of the read buffer the reader eagerly allocates for a column — used only to
    // bound that allocation (CF-1 (iii)/RF-4a). It is the ACTUAL element size of the `new T[]`/`new T?[]`
    // read buffer, INCLUDING Nullable<T> overhead (long? is 16 bytes, decimal? is 24), so the (iii) bound
    // reflects the true transient rather than the unwrapped width. String/binary read into a per-row
    // managed reference (IntPtr.Size); their backing bytes are separately bounded by the decompressed
    // ceiling.
    internal static int AllocatedElementByteWidth(DataType type, bool nullable) => type switch
    {
        BooleanType => nullable ? Unsafe.SizeOf<bool?>() : Unsafe.SizeOf<bool>(),
        ByteType => nullable ? Unsafe.SizeOf<sbyte?>() : Unsafe.SizeOf<sbyte>(),
        ShortType => nullable ? Unsafe.SizeOf<short?>() : Unsafe.SizeOf<short>(),
        IntegerType => nullable ? Unsafe.SizeOf<int?>() : Unsafe.SizeOf<int>(),
        LongType => nullable ? Unsafe.SizeOf<long?>() : Unsafe.SizeOf<long>(),
        FloatType => nullable ? Unsafe.SizeOf<float?>() : Unsafe.SizeOf<float>(),
        DoubleType => nullable ? Unsafe.SizeOf<double?>() : Unsafe.SizeOf<double>(),
        DateType or TimestampType or TimestampNtzType => nullable ? Unsafe.SizeOf<DateTime?>() : Unsafe.SizeOf<DateTime>(),
        DecimalType => nullable ? Unsafe.SizeOf<decimal?>() : Unsafe.SizeOf<decimal>(),
        StringType or BinaryType => IntPtr.Size,
        _ => IntPtr.Size,
    };

    // Whether a column type materializes VARIABLE-WIDTH values (string/binary) whose decoded footprint is far
    // larger than the IntPtr-sized per-row reference AllocatedElementByteWidth charges (Round-10 #5). Used only
    // to add the VariableWidthMaterializationFactor over-approximation to a STRAND's charge — never to the
    // accept/reject ceiling.
    internal static bool IsVariableWidthType(DataType type) => type is StringType or BinaryType;

    private async Task<ParquetReader> OpenAsync(Stream input, CancellationToken cancellationToken)
    {
        ParquetReader? reader = null;
        try
        {
            // Bounded-time open (#699). Parquet.Net 6.1.0 can be driven by a single flipped terminal footer
            // byte into effectively UNBOUNDED work inside CreateAsync (and the forced lazy Schema
            // materialization) that IGNORES the CancellationToken. Race the open against its OWN per-operation
            // wall-clock budget via the data-file BoundedDecoder so a crafted footer fails closed with a
            // deterministic, DISTINCT DecodeBudgetExceeded (NOT CorruptData — a wall-clock timeout is a
            // resource fault, not proof the bytes are corrupt) rather than hanging the caller. The timeout is a
            // DeltaStorageException, which IsUndecodableParquetInput EXCLUDES, so it propagates UNWRAPPED past
            // the catch below (never re-masked, and — correctly — never reclassified as encryption).
            // Cooperative caller cancellation surfaces as OperationCanceledException, also excluded.
            //
            // Disposal on the two abandoned paths (both restored here): (a) the SCHEMA-FAULT path disposes the
            // half-built reader INSIDE the work delegate (the outer `reader` stays null when the work throws,
            // so the outer catch cannot dispose it); (b) the ABANDONED-SUCCESS path (a non-terminating open that
            // eventually wins AFTER the deadline) is disposed via the onAbandonedResult hook so the late reader
            // — which owns the input stream (leaveStreamOpen:false) — is never leaked.
            reader = await _dataFileDecoder.RunAsync(
                async innerToken =>
                {
                    ParquetReader r = await ParquetReader.CreateAsync(input, null, false, innerToken).ConfigureAwait(false);
                    try
                    {
                        // Parquet.Net parses the footer LAZILY: CreateAsync reads the thrift FileMetaData, but
                        // the high-level ParquetSchema is built ON FIRST ACCESS
                        // (ThriftFooter.CreateModelSchema), which raises raw BCL exceptions (the #193 CDF
                        // cdc-file fuzz drove InvalidOperationException, and a byte-flipped footer drives
                        // NullReferenceException from InitRowGroupReaders) on a malformed schema footer. Every
                        // caller (ReadDataSchemaAsync, ReadAsync) reads reader.Schema OUTSIDE this boundary, so
                        // force that materialization HERE — inside the fail-closed footer-parse boundary AND the
                        // bounded-time budget — so a corrupt footer can neither escape as a raw BCL exception at
                        // a later reader.Schema access nor hang there (storage-delta-architecture.md §5.4
                        // C-DECODE).
                        _ = r.Schema;
                    }
                    catch
                    {
                        // The forced Schema access failed after the reader was constructed: dispose it HERE
                        // (leaveStreamOpen:false also releases the input stream) before rethrowing, so the
                        // successful ParquetReader that owns the input stream is never dropped undisposed when
                        // the work faults inside BoundedDecode (the outer `reader` is still null on this path).
                        await r.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }

                    return r;
                },
                _limits.DecodeTimeBudget,
                _ => DataFileDecodeTimeout(
                    "The Parquet stream could not be decoded within the bounded-decode time budget "
                    + "(possible malformed footer driving a non-terminating decode).", DecodeStage.Open),
                cancellationToken,
                onAbandonedResult: DisposeAbandonedReader,
                timeProvider: _timeProvider,
                estimatedRetainedBytes: EstimateOpenRetainedBytes(input)).ConfigureAwait(false);

            // Plaintext-footer Parquet Modular Encryption — SUCCESS-path arm (#655). A plaintext-footer
            // encrypted file keeps the ordinary PAR1 magic; when its encrypted columns RETAIN their plaintext
            // ColumnMetaData (a spec-permitted shape) Parquet.Net opens it cleanly HERE, so we detect it from
            // the parsed footer metadata (file-level encryption_algorithm and/or per-column ColumnCryptoMetaData)
            // and fail closed with an actionable UnsupportedFeature rather than returning a reader whose column
            // reads would later fall through to CorruptData. The COMPLEMENTARY case — a real encrypting writer
            // that OMITS the plaintext ColumnMetaData on encrypted columns — makes Parquet.Net 6.1.0 throw
            // (NRE) during its row-group-reader init inside CreateAsync above, before this line; that shape is
            // caught on the FAILURE path in the catch below via the footer probe. Detection is presence-only —
            // the field's existence is the diagnosis; no footer content is read or echoed (#653 hygiene). The
            // rules themselves live in the shared ParquetEncryption classifier so the checkpoint door applies
            // exactly the same ones (#681).
            if (ParquetEncryption.IsPlaintextFooterEncrypted(reader.Metadata))
            {
                // Dispose the healthy reader (leaveStreamOpen:false also releases the input stream) before
                // failing closed; guard the dispose so a dispose-time fault cannot escape UNMAPPED and
                // replace the UnsupportedFeature contract — the boundary stays exception-total, mirroring the
                // encrypted-footer dispose in the catch below. This UnsupportedFeature is a DeltaStorageException,
                // which IsUndecodableParquetInput excludes, so it propagates past the catch (not re-caught).
                try
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeFault) when (IsUndecodableParquetInput(disposeFault))
                {
                    // Cleanup fault on the reader being torn down: ignore it so the UnsupportedFeature
                    // classification below remains the single, meaningful outcome.
                }

                throw DeltaStorageException.UnsupportedFeature(
                    ParquetEncryption.PlaintextFooterEncryptionMessage);
            }

            return reader;
        }
        catch (DecodeCapacityExhaustedException ex)
        {
            // The data-file door is at strand capacity: the OPEN was rejected fail-fast WITHOUT starting (I8).
            // Map it to the public retryable DecoderSaturated (never a decode-timeout, never CorruptData) so a
            // caller/engine can back off and retry. It is excluded by IsUndecodableParquetInput, so this
            // explicit catch is the only mapping site.
            throw DataFileDecoderSaturated(ex);
        }
        catch (Exception ex) when (IsUndecodableParquetInput(ex))
        {
            // Fail-closed footer/metadata-parse boundary (storage-delta-architecture.md §5.4 C-DECODE /
            // ADR-0013). This try wraps only the pure Parquet.Net footer parse (CreateAsync plus the forced
            // lazy Schema materialization above) — NO DeltaSharp decode code runs inside it, so there is no OWN
            // bug to mask here: every fault (other than the cancellation / typed-storage exceptions
            // IsUndecodableParquetInput excludes) is Parquet.Net rejecting a malformed/truncated footer. The
            // narrow IsParquetDefect whitelist is insufficient at THIS site (see IsUndecodableParquetInput), so
            // map every such fault to CorruptData — EXCEPT the one distinguishable unsupported-but-VALID family
            // classified below.
            //
            // Parquet Modular Encryption classification (#649). An encrypted-footer file is a perfectly VALID
            // Parquet file that the LIBRARY (not DeltaSharp) refuses: Parquet.Net 6.1.0 rejects its 'PARE' head
            // as "not a parquet file, head: 50415245, tail: 50415245" — a message shape BYTE-FOR-BYTE identical
            // to the one it emits for arbitrary non-Parquet garbage ("head: 74686973…" for "this is not a
            // parquet file"), so ex.Message cannot separate encryption from corruption. The file's own leading
            // stays CorruptData (see the ReadRowGroupAsync superset catch). Encrypted-FOOTER mode (PARE magic)
            // is classified via the leading-magic peek. Plaintext-footer encryption (mode b) keeps the ordinary
            // PAR1 magic; when its encrypted columns retain plaintext ColumnMetaData the footer parses cleanly
            // and it is classified on the SUCCESS path above, but a real encrypting writer OMITS that plaintext
            // metadata, so Parquet.Net 6.1.0 throws (NRE) during row-group-reader init and lands HERE. Probe the
            // plaintext footer directly for the FileMetaData encryption_algorithm field to reclassify that shape
            // as UnsupportedFeature too (#655). Both peeks (and their messages) live in the shared
            // ParquetEncryption classifier, which the checkpoint door applies identically (#681). They read the
            // still-open input (CreateAsync leaves the caller's stream open on failure) and MUST run BEFORE the
            // dispose below (leaveStreamOpen:false releases the input stream); anything not positively
            // identified stays the fail-closed CorruptData default. NOTE the sibling library
            // NotSupportedException family (raised from page decode in ReadRowGroupAsync) is NOT reclassified:
            // Parquet.Net raises it on genuinely CORRUPT pages too, so it is not safely separable from
            // corruption and stays CorruptData (see the ReadRowGroupAsync catch).
            string? unsupportedEncryption = ParquetEncryption.ClassifyUnreadableInput(input);

            // NOTE: on this catch path the outer `reader` is ALWAYS null — BoundedDecode.RunAsync assigns
            // `reader` only on a successful return, and a schema-materialization fault is disposed INSIDE the
            // work delegate (which releases the input stream via leaveStreamOpen:false) before the fault
            // propagates here. There is therefore no half-built reader to dispose at this site (the pre-PR
            // dispose branch is dead now that disposal moved into the work delegate); the ClassifyUnreadableInput
            // probe above still reads the caller's still-open input stream (CreateAsync leaves it open on
            // failure), which is correct because that stream is caller-owned and only the reader-owned wrapper
            // was disposed inside the work.

            if (unsupportedEncryption is not null)
            {
                // Actionable, cause-preserving classification (#649/#655): the file is not corrupt, it is a
                // valid Parquet Modular Encryption file DeltaSharp cannot read — either encrypted-footer mode
                // (the 'PARE' bracketing at both ends is the whole diagnosis) or plaintext-footer mode whose
                // encrypted columns omit their plaintext ColumnMetaData (so CreateAsync threw, but the footer
                // still carries encryption_algorithm). The classifier returns the matching fixed message; it
                // carries no ex.Message and no footer content (#653), and the original library fault is
                // deliberately not echoed here.
                throw DeltaStorageException.UnsupportedFeature(unsupportedEncryption);
            }

            // Fixed message (no ex.Message interpolation): an attacker-controlled footer field name must never
            // echo into the error text (info-leak, Security LOW). The cause is preserved as the inner exception
            // for logs/diagnostics.
            throw DeltaStorageException.CorruptData(
                "The Parquet stream is malformed or truncated.", ex);
        }
    }

    // One requested column resolved against the file: a present scalar leaf (Scalar), a present nested Field
    // graph (Nested; #571), or Absent (not in the file, to be null-filled per #497). Exactly one state holds.
    internal readonly struct ResolvedColumn
    {
        private ResolvedColumn(DataField? scalar, Field? nested, bool absent)
        {
            Scalar = scalar;
            Nested = nested;
            IsAbsent = absent;
        }

        internal DataField? Scalar { get; }

        internal Field? Nested { get; }

        internal bool IsAbsent { get; }

        internal static ResolvedColumn ForScalar(DataField field) => new(field, null, absent: false);

        internal static ResolvedColumn ForNested(Field field) => new(null, field, absent: false);

        internal static ResolvedColumn Missing() => new(null, null, absent: true);
    }

    // Resolves each requested column to the matching file field (validating physical type/nullability for a
    // scalar, or the container shape for a nested column; #571), or an Absent slot for a column not present in
    // the file when it may be null-filled (#497). An Absent slot is produced ONLY when nullFillMissingColumns
    // is set AND the requested column is nullable: an additively-added column (#190) is always nullable, and
    // older files written before it lack it, so it reads back as all-null. An absent NON-nullable column can
    // never be null-filled (a required lane cannot carry null), and an absent column with null-fill disabled
    // preserves the strict projection contract — both fail closed with the dedicated ColumnNotPresentInFile
    // kind the OPTIMIZE/read guards match on (#513). A PRESENT column with a disagreeing physical
    // type/nullability/shape is still rejected (ValidateFileField / NestedParquetColumnReader.ValidateShape)
    // as a distinct SchemaMismatch (never silently coerced or null-filled).
    //
    // The by-name map is built from the file schema's TOP-LEVEL fields (not the flattened leaves), so a nested
    // column resolves to its container Field. For a flat file the top-level fields ARE the leaf DataFields, so
    // the scalar path is unchanged. Nested columns are not (yet) supported under column-mapping id mode or
    // null-fill: both fail closed rather than risk a wrong read.
    internal static ResolvedColumn[] ResolveFileFields(
        ParquetSchema fileSchema, StructType requested, bool nullFillMissingColumns, bool allowTypeWideningPromotion,
        IReadOnlyDictionary<int, DataField>? byFieldId)
    {
        var byName = new Dictionary<string, Field>(StringComparer.Ordinal);
        foreach (Field field in fileSchema.Fields)
        {
            byName[field.Name] = field;
        }

        // Id mode only (#658): the set of file columns that carry a footer field_id. A requested column with NO
        // delta.columnMapping.id falls back to by-name resolution, but ONLY against a file column that is itself
        // un-mapped (no field_id) — an engine-synthesized column like CDF's `_change_type`. This keeps the
        // fallback from letting a requested column that merely LACKS its id silently grab a genuinely
        // column-mapped (id-bearing) file column by physical name, which would mask a foreign/corrupt file.
        HashSet<string>? idBearingFileNames = null;
        if (byFieldId is not null)
        {
            idBearingFileNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (DataField idBearing in byFieldId.Values)
            {
                idBearingFileNames.Add(idBearing.Name);
            }
        }

        var resolved = new ResolvedColumn[requested.Count];
        for (int c = 0; c < requested.Count; c++)
        {
            StructField requestedField = requested[c];
            string name = requestedField.Name;

            if (requestedField.DataType is ArrayType or MapType or StructType)
            {
                // Nested column (#571). Not supported under id mode (BuildFieldIdMap is flat/leaf-only) — fail
                // closed rather than resolve a nested container by an ambiguous leaf field_id.
                if (byFieldId is not null)
                {
                    throw DeltaStorageException.UnsupportedFeature(
                        $"Column '{DiagnosticText.Sanitize(name)}': reading a nested column under column-mapping id mode is not supported.");
                }

                if (!byName.TryGetValue(name, out Field? nestedField))
                {
                    // A nested column absent from the file is not null-filled in this increment (fail closed).
                    throw DeltaStorageException.ColumnNotPresentInFile(name);
                }

                NestedParquetColumnReader.ValidateShape(nestedField, requestedField.DataType, name);
                resolved[c] = ResolvedColumn.ForNested(nestedField);
                continue;
            }

            // Per-field resolution (#523, #658): under column-mapping id mode each requested column is resolved
            // INDEPENDENTLY — by its delta.columnMapping.id against the file's footer field_ids
            // (BuildFieldIdMap) when it DECLARES one (so a logical rename that never rewrites the Parquet still
            // reads through), or by physical NAME when it carries NO id. A requested column with no
            // column-mapping id is an engine-synthesized column (e.g. CDF's `_change_type`, written by literal
            // name and never column-mapped); the by-name fallback lets a MIXED projection —
            // [data-by-id, _change_type-by-name] — resolve in ONE ReadAsync. Fail-closed posture is preserved:
            // a column that DECLARES an id whose value is ABSENT from the footer fails closed here (present
            // stays false → null-fill or ColumnNotPresentInFile) — it is NEVER silently name-matched, so a
            // foreign/corrupt file that dropped that id can't be masked by a coincidental physical-name hit.
            DataField? field = null;
            bool present;
            if (byFieldId is not null && ColumnMapping.TryGetId(requestedField, out long id))
            {
                present = id is >= 0 and <= int.MaxValue
                    && byFieldId.TryGetValue((int)id, out field);
            }
            else if (byName.TryGetValue(name, out Field? candidate)
                && (idBearingFileNames is null || !idBearingFileNames.Contains(name)))
            {
                // Name mode (byFieldId is null), OR id mode for a column that carries no delta.columnMapping.id
                // AND whose physical-name match is an un-mapped file column (idBearingFileNames guard above —
                // so a no-id request can never grab a column-mapped file column by name). A scalar column
                // resolves to a leaf DataField. If the file column with this name is itself nested, the
                // requested scalar type genuinely disagrees with the file — a SchemaMismatch.
                field = candidate as DataField
                    ?? throw DeltaStorageException.SchemaMismatch(
                        $"Column '{DiagnosticText.Sanitize(name)}': the requested type '{requestedField.DataType.SimpleString}' is scalar "
                        + "but the file column is nested.");
                present = true;
            }
            else
            {
                present = false;
            }

            if (!present)
            {
                if (nullFillMissingColumns && requestedField.Nullable)
                {
                    // Absent + nullable + null-fill enabled: an Absent slot the read path materializes as an
                    // all-null column (evolved-column read null-fill, #497).
                    resolved[c] = ResolvedColumn.Missing();
                    continue;
                }

                throw DeltaStorageException.ColumnNotPresentInFile(name);
            }

            ValidateFileField(field!, requestedField, allowTypeWideningPromotion);
            resolved[c] = ResolvedColumn.ForScalar(field!);
        }

        return resolved;
    }

    // M2: cross-check the resolved file field's physical type, temporal annotation, decimal
    // precision/scale, and nullability against the requested engine type. A mismatch is a DISTINCT
    // SchemaMismatch error (not a generic "malformed" one), so a schema-evolution/type surprise is
    // never silently coerced or masked as corruption.
    //
    // Type-widening promotion (Delta PROTOCOL.md "Reader Requirements for Type Widening", #495): an OLDER
    // file physically stores a NARROWER type than the current (widened) requested type — e.g. an Int32 file
    // read under a widened `long` schema, or a decimal(6,2) file under a widened decimal(10,4) schema. When
    // the file's physical type is a Delta-SANCTIONED widening OF the requested type
    // (TypeWidening.IsSanctionedWidening) AND the caller opened this promotion gate
    // (allowTypeWideningPromotion — the scan layer proved the table's protocol declares the `typeWidening`
    // feature; the stream-level reader cannot see the protocol, so it TRUSTS this flag), accept it: the read
    // path (ReadColumnAsync/ReadPromotedColumnAsync) reads the narrow physical values and PROMOTES them into
    // the requested wide vector. When the gate is closed (allowTypeWideningPromotion == false) a narrower
    // physical type is NOT promotable — the exact-type mismatch below fires and the read fails closed
    // (SchemaMismatch), so a tampered/malformed log (wide schema, narrow files, no `typeWidening` feature) is
    // never silently "repaired". Any OTHER physical divergence (a narrowing on read, an unrelated type) is
    // still rejected fail-closed.
    private static void ValidateFileField(DataField fileField, StructField requestedField, bool allowTypeWideningPromotion)
    {
        // #683 message hygiene: `requestedField.Name` originates in `metaData.schemaString`, which on a
        // foreign/hostile table read is fully attacker-authored. It is a pure DIAGNOSTIC LABEL here (every
        // resolution decision above used the raw name; this method only reports), so sanitize it ONCE at the
        // entry point — the same idiom applied at the resolution sites ~60 lines above and in
        // NestedParquetColumnReader — rather than at each of the five throw sites below.
        string columnLabel = DiagnosticText.Sanitize(requestedField.Name);

        // `requestedField.DataType.SimpleString` IS echoed raw below, and that is deliberate. The class doc on
        // DiagnosticText warns that SimpleString is unbounded FOR A NESTED TYPE, because StructType/ArrayType/
        // MapType append every nested field name verbatim and recurse. This method only ever sees a SCALAR:
        // ParquetTypeMapping.CreateField (next line) rejects nested types outright, and a nested request is
        // routed to NestedParquetColumnReader.ValidateShape long before it reaches here. On a scalar,
        // SimpleString is a bounded literal ("bigint", "decimal(10,2)") carrying no caller text. Do NOT copy
        // this pattern into a context that can see a nested type.
        // honorReferenceNullability: false — `expected` is the ALWAYS-NULLABLE reference shape this guard is
        // deliberately written around (see the nullability check below and issue #807). Passing true here
        // would reject every foreign/pre-#730 file that stores a log-required string/binary as OPTIONAL.
        DataField expected = ParquetTypeMapping.CreateField(requestedField, honorReferenceNullability: false);

        // Read-side promotion gate: a narrower physical type that is a sanctioned widening of the request is
        // accepted (the values are widened on read) ONLY when the caller opened the promotion gate. This is
        // lossless and matches what the writer recorded in `delta.typeChanges`. Nullability is still checked
        // below against the physical column.
        bool mappable = ParquetTypeMapping.TryToDataType(fileField, out DataType? physicalType);
        bool promotable = allowTypeWideningPromotion
            && mappable
            && !physicalType!.Equals(requestedField.DataType)
            && TypeWidening.IsSanctionedWidening(physicalType, requestedField.DataType);

        // FAIL CLOSED on a footer column whose physical type has NO DeltaSharp mapping, BEFORE the CLR-shape
        // gate below. The shape gate compares raw CLR types, so an ANNOTATED column whose annotation carries
        // the real logical type — a Parquet TIME, whose ClrType is a bare int (millis) or long (micros/nanos)
        // — sails straight through it and is silently decoded as int/bigint sub-day units. That is a SILENT
        // data corruption on a foreign or forged file, and it is exactly what the schema door
        // (ToDataType/ToDataSchema) already rejects; routing the read door through the same mapping keeps the
        // two doors from disagreeing. A genuine int/long column still maps and proceeds unchanged.
        if (!mappable)
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Column '{columnLabel}': file physical type "
                + $"'{ParquetTypeMapping.DescribePhysical(fileField)}' has no supported DeltaSharp type "
                + $"mapping and cannot be read as '{requestedField.DataType.SimpleString}'.");
        }

        if (!promotable && !ParquetTypeMapping.PhysicalClrTypesMatch(fileField.ClrType, expected.ClrType))
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Column '{columnLabel}': file physical type "
                + $"'{ParquetTypeMapping.DescribePhysical(fileField)}' does not "
                + $"match the requested engine type '{requestedField.DataType.SimpleString}' "
                + $"(expected '{ParquetTypeMapping.DescribePhysicalClrType(expected.ClrType)}').");
        }

        // A nullable file column cannot be read into a column the writer would have emitted as
        // non-nullable without risking a null in a required lane; reject rather than coerce. We compare
        // against the EXPECTED field's nullability (not the requested engine flag) because `expected` is
        // built with honorReferenceNullability:false — BY CONSTRUCTION always-nullable for string/binary.
        // That is deliberate on the read path: a file this reader did not write (a foreign producer, or a
        // DeltaSharp file written before #730) may store a log-REQUIRED string/binary column as OPTIONAL,
        // and those files must stay readable. The consequence — this SCHEMA-level guard is inert for
        // string/binary — is dispositioned (issue #807) by the VALUE-level required-lane guard at
        // materialization (RejectNullInRequiredLane in ReadStringAsync/ReadBinaryAsync): an OPTIONAL
        // string/binary column with no nulls still reads, but the first actual null in a required lane fails
        // closed. This SCHEMA guard still bites for every value-typed column, whose physical repetition has
        // always tracked the declared flag.
        if (fileField.IsNullable && !expected.IsNullable)
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Column '{columnLabel}': the file column is nullable but the requested engine "
                + "type is non-nullable.");
        }

        // A promoted (narrower physical) column has already been validated as a sanctioned widening; its
        // physical decimal/temporal annotation legitimately differs from the requested wide type, so skip the
        // exact annotation cross-checks below (which assume physical == requested).
        if (promotable)
        {
            return;
        }

        switch (requestedField.DataType)
        {
            case DateType:
                if (fileField is not DateTimeDataField { DateTimeFormat: DateTimeFormat.Date })
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{columnLabel}': expected a DATE column but the file annotation "
                        + "is not DATE.");
                }

                break;

            case TimestampType:
            case TimestampNtzType:
                // Both timestamp (LTZ) and timestamp_ntz accept a non-DATE micros DateTimeDataField. The
                // isAdjustedToUTC annotation is deliberately NOT checked here: the read is SCHEMA-AUTHORITATIVE
                // — the Delta table schema (the REQUESTED type, this case) selects the lane, and the stored
                // INT64 micros are read into it. This keeps cross-engine reads robust: a native timestamp_ntz
                // file requested under an LTZ schema (or vice versa) is a pure raw-micros passthrough (no
                // timezone shift), which the schema-authoritative-passthrough test pins.
                if (fileField is not DateTimeDataField timestampField
                    || timestampField.DateTimeFormat == DateTimeFormat.Date)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{columnLabel}': expected a {requestedField.DataType.SimpleString} "
                        + "column but the file annotation is DATE or not a temporal type.");
                }

                break;

            case DecimalType decimalType:
                if (fileField is not DecimalDataField decimalField
                    || decimalField.Precision != decimalType.Precision
                    || decimalField.Scale != decimalType.Scale)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{columnLabel}': file decimal type does not match the requested "
                        + $"'{decimalType.SimpleString}' (precision/scale differ).");
                }

                break;

            default:
                break;
        }
    }

    private static async Task<ColumnBatch?> ReadRowGroupAsync(
        SharedParquetReader shared,
        int group,
        StructType requested,
        ResolvedColumn[] fileFields,
        RowGroupPredicate? keepRowGroup,
        ParquetDecodeLimits limits,
        TimeSpan decodeBudget,
        BoundedDecoder decoder,
        TimeProvider timeProvider,
        DeltaStorageTelemetry telemetry,
        bool allowTypeWideningPromotion,
        CancellationToken cancellationToken)
    {
        ParquetReader reader = shared.Reader;

        // Fail-closed row-group decode boundary (storage-delta-architecture.md §5.4 C-DECODE / ADR-0013). It
        // encloses EVERY step that consumes untrusted footer/stats/page bytes so none can escape a raw BCL
        // exception (PDX-T covers crafted/lying stats): OpenRowGroupReader (row-group metadata); the
        // RowGroupStatistics construction + keepRowGroup pruning invocation, whose eager GetStatistics decodes
        // attacker-controlled column-statistics blobs on the predicate-pushdown path the CDF door
        // (keepRowGroup:null) never reaches; EnsureDecodeCeiling / ProjectedFootprints (column-chunk sizes);
        // and the per-column page/level decode. Any raw fault maps to the deterministic CorruptData contract.
        //
        // Bounded-time decode (#647). A corrupt data-page header can drive Parquet.Net 6.1.0's SYNCHRONOUS
        // page/level decode into a non-terminating CPU loop that observes NO CancellationToken mid-page — a
        // hang is not an exception, so the catches below cannot intercept it. Race the whole decode against its
        // OWN per-operation wall-clock budget (measured from the decode's EXECUTION start, I3 — never spanning
        // the streaming iterator's `yield return` suspension, so consumer time between MoveNextAsync calls is
        // never charged, I7) via the data-file BoundedDecoder: on expiry it fails closed with a DISTINCT
        // DecodeBudgetExceeded (NOT CorruptData — a wall-clock timeout is a resource fault, not proof the page
        // is corrupt; a DeltaStorageException the catches EXCLUDE, so it propagates unwrapped), never hanging
        // the caller.
        //
        // Data-file door residual (honest, §5.4). The decode runs via Task.Run on the shared ThreadPool — NOT a
        // dedicated thread. A dedicated thread provided ZERO isolation here because DecodeGroupAsync awaits
        // async reads that resume on the pool anyway, where the non-terminating CPU loop then runs (the
        // dedicated thread would merely sit blocked in GetResult), at 68–74× the per-decode cost. An abandoned
        // non-terminating decode therefore holds ~1 pool thread + its retained bytes until process restart; the
        // ThreadPool injects replacement threads so it never queue-starves new work. The residual is charged
        // ONLY at DETACH (when this decode strands past its deadline) with the ROW GROUP'S REAL projected
        // footprint (hoisted below, clamped to MaxRowGroupDecodedBytes) — a healthy in-flight decode is never
        // charged nor throttled. The now-working negative cache stops self-renewal. When the door's stranded
        // residual is already full of PERMANENT strands the decode is rejected fail-fast with a
        // DecodeCapacityExhaustedException (NEVER started) mapped to the public retryable DecoderSaturated below
        // (I8), NOT a decode-timeout. The reader is kept alive for a stranded
        // decode via an extra lease on `shared` (I6) released when the decode settles.
        try
        {
            // I6 — take an extra reader lease BEFORE the decode starts; release it when the (possibly stranded)
            // decode settles via onWorkSettled (which fires on EVERY exit path, including the pre-start
            // capacity-rejection throw). A never-terminating strand holds the lease forever, keeping the
            // reader/stream alive so the strand never touches disposed state (residual ≤ the door's residual).
            shared.Retain();
            try
            {
                // Hoist the row group's REAL projected footprint (ProjectedFootprints + declared row count,
                // Σ decompressed + Σ materialized across ALL projected columns, clamped to the door's max
                // footprint — Round-8 #2) so a strand of THIS decode charges the actual bytes it would pin at
                // detach — never a fictional fixed representative nor a single-column truncation. Floored at
                // BoundedDecode.MinStrandChargeBytes (High #1b) so a CHEAP row group still consumes the byte
                // residual. Computed from the already-parsed footer metadata (the open was bounded), so it is a
                // cheap in-memory read; any metadata fault falls back to the door footprint (never under-counts).
                long strandFootprint = Math.Max(
                    EstimateRowGroupRetainedBytes(reader, group, requested, fileFields),
                    BoundedDecode.MinStrandChargeBytes);
                return await decoder.RunAsync(
                    DecodeGroupAsync,
                    decodeBudget,
                    _ =>
                    {
                        telemetry.RecordDecodeBudgetExceeded(DecodeDoor.DataFile, DecodeStage.RowGroup);
                        return DeltaStorageException.DecodeBudgetExceeded(
                            "A Parquet row group could not be decoded within the bounded-decode time budget "
                            + "(possible corrupt data-page header driving a non-terminating decode).");
                    },
                    cancellationToken,
                    onAbandonedResult: DisposeAbandonedBatch,
                    onWorkSettled: shared.Release,
                    timeProvider: timeProvider,
                    estimatedRetainedBytes: strandFootprint).ConfigureAwait(false);
            }
            catch (DecodeCapacityExhaustedException capacity)
            {
                // The data-file door is at capacity: the decode was rejected WITHOUT starting. The extra lease
                // is released via onWorkSettled (which fires on the capacity-rejection path), so no manual
                // release is needed here. Surface the retryable saturation (never a decode-timeout).
                telemetry.RecordDecodeCapacityExhausted(DecodeDoor.DataFile);
                throw DeltaStorageException.DecoderSaturated(
                    "The Parquet data-file row-group decode was rejected because the bounded-decode worker is at "
                    + "capacity (the door's memory budget / strand cap is exhausted by decodes running past their "
                    + "deadline). Fail-closed; retry after capacity frees.", capacity);
            }

            async Task<ColumnBatch?> DecodeGroupAsync(CancellationToken decodeToken)
            {
                using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(group);

                if (keepRowGroup is not null)
                {
                    // RowGroupStatistics's constructor eagerly calls rowGroup.GetStatistics(fileField), decoding
                    // the footer statistics blob for every projected column. A corrupt stats blob throws here
                    // (e.g. ArgumentException / InvalidDataException); the enclosing boundary maps it to
                    // CorruptData rather than let it escape raw to a predicate-pushdown caller (the CDF door
                    // passes keepRowGroup:null).
                    var statistics = new RowGroupStatistics(rowGroup, requested, fileFields);
                    if (!keepRowGroup(statistics))
                    {
                        // Pruned: return without reading any column chunk for this group.
                        return null;
                    }
                }

                // CF-1/H4/L3: reject an implausible or out-of-range row group BEFORE the eager allocation below,
                // so a crafted footer (inflated decompressed size or row count) surfaces as a deterministic
                // CorruptData error rather than an OOM or a raw OverflowException escaping the codec contract.
                long declaredRows = rowGroup.RowCount;
                EnsureDecodeCeiling(declaredRows, ProjectedFootprints(rowGroup, requested, fileFields), group, limits);
                int rowCount;
                try
                {
                    rowCount = checked((int)declaredRows);
                }
                catch (OverflowException ex)
                {
                    // Kept as a distinct, informative CorruptData (a DeltaStorageException the boundary excludes
                    // and lets propagate) so the "exceeds Int32.MaxValue" cause is not flattened into the generic
                    // map.
                    throw DeltaStorageException.CorruptData(
                        $"Row group {group} declares {declaredRows} rows, exceeding Int32.MaxValue.", ex);
                }

                var columns = new ColumnVector[requested.Count];
                // One eager-decode budget for THIS row group's nested reconstruction, shared across every nested
                // column (and every leaf + container structure within each), so their COMBINED peak — not each
                // column independently — stays under the ceiling. The flat EnsureDecodeCeiling above already
                // bounds the raw declared bytes cumulatively; this bounds reconstruction overhead that aggregate
                // misses.
                var nestedBudget = new NestedParquetColumnReader.NestedDecodeBudget(
                    limits.MaxRowGroupDecodedBytes);
                for (int c = 0; c < requested.Count; c++)
                {
                    ResolvedColumn resolved = fileFields[c];
                    if (resolved.Nested is { } nestedField)
                    {
                        // Nested column (#571): reconstruct an immutable nested vector from the raw Dremel
                        // levels. NestedParquetColumnReader owns its own allocation ceiling and null-correct
                        // reassembly.
                        columns[c] = await NestedParquetColumnReader.ReadAsync(
                            rowGroup, nestedField, requested[c].DataType, rowCount, requested[c].Name, nestedBudget, decodeToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    MutableColumnVector vector = ColumnVectors.Create(requested[c].DataType, Math.Max(rowCount, 1));
                    if (resolved.Scalar is { } fileField)
                    {
                        await ReadColumnAsync(rowGroup, fileField, requested[c], vector, rowCount, allowTypeWideningPromotion, decodeToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        // The requested column is absent from this (older, narrower) file: materialize it as an
                        // all-null column (evolved-column read null-fill, #497). Only nullable columns reach here
                        // (ResolveFileFields fails closed on an absent non-nullable column).
                        for (int r = 0; r < rowCount; r++)
                        {
                            vector.AppendNull();
                        }
                    }

                    columns[c] = vector;
                }

                return new ManagedColumnBatch(requested, columns, rowCount);
            }
        }
        catch (Exception ex) when (IsParquetDefect(ex))
        {
            throw DeltaStorageException.CorruptData(
                $"Failed to decode Parquet row group {group}.", ex);
        }
        catch (Exception ex) when (IsUndecodableParquetInput(ex))
        {
            // Fail-closed fallback (storage-delta-architecture.md §5.4 C-DECODE / ADR-0013) for the decode-fault
            // family the #193 CDF cdc-file fuzz proved Parquet.Net raises from WITHIN its own footer-stats and
            // page/level decode of a malformed file — a set IsParquetDefect deliberately EXCLUDES so a genuine
            // bug in our own decode surfaces as itself: DataColumnReader.ReadDataPageV1Async indexes outside its
            // decode buffers (IndexOutOfRangeException); RleBitpackedHybridEncoder.Decode rejects a bad level
            // run / bit-width / value-count (ArgumentException); rowGroup.GetStatistics rejects a crafted footer
            // stats blob (ArgumentException / InvalidDataException) on the predicate-pushdown pruning path; and
            // other malformed pages raise InvalidOperationException / NotSupportedException / FormatException.
            // This is an UNBOUNDED set of raw BCL types, so a whitelist cannot fail closed against it — but at
            // THIS boundary it need not: every index and length this method and its leaf readers own is bounded
            // by an allocation whose size is fixed by the REQUEST (columns / fileFields / requested by
            // requested.Count) or by the already-validated row count (each leaf buffer by rowCount, its
            // post-read loop over [0,rowCount)) — never by the file bytes — so on any VALID file none of that
            // interleaved code throws these types (the whole valid-file corpus proves it), and our OWN genuine
            // faults (an unsupported but VALID feature) are raised as a TYPED DeltaStorageException that
            // IsUndecodableParquetInput excludes and lets propagate. Every remaining exception is therefore the
            // library decoding corrupt bytes: map it to the deterministic CorruptData contract rather than leak
            // a raw BCL exception. (Cooperative cancellation is excluded, so it still propagates.)
            //
            // #649 note — NotSupportedException stays CorruptData here (precision boundary). It is tempting to
            // reclassify a library NotSupportedException as "unsupported-but-valid feature", but Parquet.Net
            // 6.1.0 raises the SAME NotSupportedException on genuinely CORRUPT pages — a random bit-flip that
            // lands on a compression-method, page-type, or logical-type code is decoded as an unknown code and
            // rejected with e.g. "Compression method 9 is not supported" / "can't read page type 8" (an
            // empirical fuzz over this reader's own written files reproduces it, and a forged out-of-range codec
            // triggers it deterministically). "The footer parsed / the file opened" does NOT imply the pages are
            // valid, so there is no runtime predicate that separates a valid-but-unimplemented encoding from a
            // corrupt page here — and a valid-but-unsupported-encoding fixture is not even constructible with
            // Parquet.Net 6.1.0 (it neither reads nor writes such encodings). Reclassifying would therefore
            // MISLABEL corruption as "unsupported", violating the fail-closed contract, so it stays CorruptData.
            // The one distinguishable unsupported-but-valid family (Parquet Modular Encryption) is caught earlier
            // by its 'PARE' magic in OpenAsync, before any page ever reaches this decode.
            throw DeltaStorageException.CorruptData(
                $"Failed to decode Parquet row group {group}: a column's data page or footer metadata is malformed.", ex);
        }
    }

    // M9: for fixed-width primitive columns the read path materializes into the ColumnVector's backing
    // array with no per-row object allocation (AC #180.3). String/binary columns still materialize one
    // managed object per row via Parquet.Net's decoder; a native UTF-8/byte decode that removes that
    // per-row allocation is a documented, tracked follow-up (not in FEAT-05.1 scope).
    private static async Task ReadColumnAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        StructField requestedField,
        MutableColumnVector vector,
        int rowCount,
        bool allowTypeWideningPromotion,
        CancellationToken cancellationToken)
    {
        // Type-widening promotion (#495): when the file column's physical type is a NARROWER sanctioned
        // widening of the requested type, read the file's physical values and widen them into the requested
        // (wide) vector. ValidateFileField has already proven this is a sanctioned widening AND that the
        // caller opened the promotion gate; here we re-check BOTH (defense-in-depth, and — critically — the
        // sanctioned-widening check disambiguates a same-physical pair whose logical types differ but is NOT a
        // widening: a native micros file read as `timestamp_ntz` has physical `timestamp` ≠ requested
        // `timestamp_ntz` yet must take the identity micros read, not promotion, since Parquet.Net cannot
        // distinguish the two on the wire — #533). A physical type equal to the request, or a differing pair
        // that is not a sanctioned widening, takes the normal path below.
        if (allowTypeWideningPromotion
            && ParquetTypeMapping.TryToDataType(fileField, out DataType? physicalType)
            && !physicalType.Equals(requestedField.DataType)
            && TypeWidening.IsSanctionedWidening(physicalType, requestedField.DataType))
        {
            await ReadPromotedColumnAsync(
                rowGroup, fileField, physicalType, requestedField, vector, rowCount, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        switch (requestedField.DataType)
        {
            case BooleanType:
                await ReadValueAsync<bool>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case ByteType:
                await ReadValueAsync<sbyte>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(unchecked((byte)value)), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ShortType:
                await ReadValueAsync<short>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case IntegerType:
                await ReadValueAsync<int>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case LongType:
                await ReadValueAsync<long>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case FloatType:
                await ReadValueAsync<float>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case DoubleType:
                await ReadValueAsync<double>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case DateType:
                await ReadValueAsync<DateTime>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(ParquetTypeMapping.DateTimeToEpochDay(value)),
                    cancellationToken).ConfigureAwait(false);
                break;
            case TimestampType or TimestampNtzType:
                await ReadValueAsync<DateTime>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(ParquetTypeMapping.DateTimeToEpochMicros(value)),
                    cancellationToken).ConfigureAwait(false);
                break;
            case DecimalType decimalType:
                // L1: thread decimalType through a non-capturing static reader instead of a closure so
                // no per-column-chunk delegate allocation occurs (mirrors the static primitive delegates).
                await ReadDecimalAsync(rowGroup, fileField, vector, rowCount, decimalType, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case StringType:
                await ReadStringAsync(rowGroup, fileField, requestedField, vector, rowCount, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case BinaryType:
                await ReadBinaryAsync(rowGroup, fileField, requestedField, vector, rowCount, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                // #705 predicate: ResolveFileFields diverts Array/Map/Struct to NestedParquetColumnReader before
                // this switch, so requestedField.DataType is scalar here. DescribeType bounds a future non-atomic
                // kind rather than recursing into raw nested field names.
                throw DeltaStorageException.UnsupportedFeature(
                    $"Parquet read for column '{DiagnosticText.Sanitize(requestedField.Name)}' of type "
                    + $"'{DiagnosticText.DescribeType(requestedField.DataType)}' is not supported.");
        }
    }

    // Type-widening read promotion (#495, #535 / Delta PROTOCOL.md "Reader Requirements for Type Widening"):
    // reads the file's NARROW physical values and widens each into the requested WIDE vector. The dispatch is
    // by (physical, requested) — ValidateFileField already proved the pair is a sanctioned widening, so every
    // arm here is a lossless promotion: an integral sign-extend (byte/short/int → wider integral), float→
    // double, a decimal rescale (grow-only decimal), or a cross-family integral→double / integral→decimal
    // (#535) computed in managed code (Parquet.Net stores the file value as the physical integral, so we read
    // the integral and convert — no Parquet.Net cross-type coercion is required). Every append targets the
    // requested vector's storage width, and each lambda is `static` (non-capturing) so no per-column-chunk
    // delegate is allocated.
    private static Task ReadPromotedColumnAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        DataType physicalType,
        StructField requestedField,
        MutableColumnVector vector,
        int rowCount,
        CancellationToken cancellationToken)
    {
        if (requestedField.DataType is DecimalType requestedDecimal)
        {
            // decimal(p,s) → decimal(p',s') grow-only (#495): read at the file's scale, rescale to the
            // requested type.
            if (physicalType is DecimalType)
            {
                return ReadDecimalAsync(rowGroup, fileField, vector, rowCount, requestedDecimal, cancellationToken);
            }

            // Cross-family integral → decimal (#535): read the narrow integral physical values and widen each
            // into the requested decimal lane. ValidateFileField proved the decimal holds the full integral
            // range (its integer-digit capacity p − s ≥ the source's digits), so AppendDecimal never truncates.
            return ReadIntegralAsDecimalAsync(
                rowGroup, fileField, physicalType, vector, rowCount, requestedDecimal, cancellationToken);
        }

        if (requestedField.DataType is DoubleType)
        {
            // float → double (#495).
            if (physicalType is FloatType)
            {
                return ReadValueAsync<float>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue((double)value), cancellationToken);
            }

            // Cross-family byte/short/int → double (#535). long → double is lossy and NOT sanctioned, so it
            // never reaches here; the physical is byte/short/int only.
            return physicalType switch
            {
                ByteType => ReadValueAsync<sbyte>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue((double)value), cancellationToken),
                ShortType => ReadValueAsync<short>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue((double)value), cancellationToken),
                _ => ReadValueAsync<int>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue((double)value), cancellationToken),
            };
        }

        if (requestedField.DataType is TimestampNtzType && physicalType is DateType)
        {
            // date → timestamp_ntz (#533): read the narrow epoch-day physical values and promote each to
            // epoch-micros at midnight of the date (days × MicrosPerDay). Timezone-less, so no session offset
            // is applied — the promoted instant is wall-clock midnight, matching Delta/Spark semantics. The
            // multiply is `checked`: for any epoch-day a Parquet DATE can materialize (bounded by DateTime's
            // range, ±~2.9e6) the product is ≤ 2.5e17 ≪ long.MaxValue, so it never throws in practice, but a
            // future path feeding a raw out-of-range epoch-day fails loud as OverflowException (→ CorruptData
            // via IsParquetDefect) rather than silently wrapping.
            return ReadValueAsync<DateTime>(rowGroup, fileField, vector, rowCount,
                static (v, value) => v.AppendValue(
                    checked((long)ParquetTypeMapping.DateTimeToEpochDay(value) * MicrosPerDay)), cancellationToken);
        }

        // Integral widening: byte(sbyte) → short → int → long. The requested vector's storage width is the
        // target of the upcast; the file's physical width is the read buffer's element type.
        switch (requestedField.DataType)
        {
            case ShortType:
                // Only byte → short reaches here (a sanctioned narrower integral).
                return ReadValueAsync<sbyte>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue((short)value), cancellationToken);

            case IntegerType:
                return physicalType is ByteType
                    ? ReadValueAsync<sbyte>(rowGroup, fileField, vector, rowCount,
                        static (v, value) => v.AppendValue((int)value), cancellationToken)
                    : ReadValueAsync<short>(rowGroup, fileField, vector, rowCount,
                        static (v, value) => v.AppendValue((int)value), cancellationToken);

            case LongType:
                return physicalType switch
                {
                    ByteType => ReadValueAsync<sbyte>(rowGroup, fileField, vector, rowCount,
                        static (v, value) => v.AppendValue((long)value), cancellationToken),
                    ShortType => ReadValueAsync<short>(rowGroup, fileField, vector, rowCount,
                        static (v, value) => v.AppendValue((long)value), cancellationToken),
                    _ => ReadValueAsync<int>(rowGroup, fileField, vector, rowCount,
                        static (v, value) => v.AppendValue((long)value), cancellationToken),
                };

            default:
                // Unreachable: ValidateFileField only admits the sanctioned widenings handled above.
                // #705 predicate: both operands are scalar (widening is scalar-only); DescribeType bounds a
                // future non-atomic kind rather than recursing into raw nested field names.
                throw DeltaStorageException.SchemaMismatch(
                    $"Column '{DiagnosticText.Sanitize(requestedField.Name)}': cannot promote physical type "
                    + $"'{DiagnosticText.DescribeType(physicalType)}' to requested '{DiagnosticText.DescribeType(requestedField.DataType)}'.");
        }
    }

    // Cross-family integral → decimal read promotion (#535): reads the file's NARROW integral physical values
    // (byte/short/int/long) and widens each into the requested decimal lane. The scale factors are hoisted
    // ONCE per column chunk (like ReadDecimalAsync). ValidateFileField already proved the requested decimal's
    // integer-digit capacity (precision − scale) holds the full source range, so AppendDecimal's over-precision
    // guard never trips for an in-range integral value.
    private static Task ReadIntegralAsDecimalAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        DataType physicalType,
        MutableColumnVector vector,
        int rowCount,
        DecimalType requestedDecimal,
        CancellationToken cancellationToken)
    {
        ParquetTypeMapping.DecimalScaleFactors factors = ParquetTypeMapping.DecimalScaleFactors.For(requestedDecimal);
        return physicalType switch
        {
            ByteType => ReadDecimalFromIntegralAsync<sbyte>(
                rowGroup, fileField, vector, rowCount, requestedDecimal, factors, static v => v, cancellationToken),
            ShortType => ReadDecimalFromIntegralAsync<short>(
                rowGroup, fileField, vector, rowCount, requestedDecimal, factors, static v => v, cancellationToken),
            IntegerType => ReadDecimalFromIntegralAsync<int>(
                rowGroup, fileField, vector, rowCount, requestedDecimal, factors, static v => v, cancellationToken),
            _ => ReadDecimalFromIntegralAsync<long>(
                rowGroup, fileField, vector, rowCount, requestedDecimal, factors, static v => v, cancellationToken),
        };
    }

    private static async Task ReadDecimalFromIntegralAsync<T>(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        MutableColumnVector vector,
        int rowCount,
        DecimalType requestedDecimal,
        ParquetTypeMapping.DecimalScaleFactors factors,
        Func<T, decimal> toDecimal,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        if (fileField.IsNullable)
        {
            var buffer = new T?[rowCount];
            await rowGroup.ReadAsync<T>(fileField, new Memory<T?>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                if (buffer[i] is { } value)
                {
                    ParquetTypeMapping.AppendDecimal(vector, requestedDecimal, toDecimal(value), factors);
                }
                else
                {
                    vector.AppendNull();
                }
            }
        }
        else
        {
            var buffer = new T[rowCount];
            await rowGroup.ReadAsync<T>(fileField, new Memory<T>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                ParquetTypeMapping.AppendDecimal(vector, requestedDecimal, toDecimal(buffer[i]), factors);
            }
        }
    }

    private static async Task ReadValueAsync<T>(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        MutableColumnVector vector,
        int rowCount,
        Action<MutableColumnVector, T> append,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        try
        {
            if (fileField.IsNullable)
            {
                var buffer = new T?[rowCount];
                await rowGroup.ReadAsync<T>(fileField, new Memory<T?>(buffer), null, cancellationToken)
                    .ConfigureAwait(false);
                for (int i = 0; i < rowCount; i++)
                {
                    if (buffer[i] is { } value)
                    {
                        append(vector, value);
                    }
                    else
                    {
                        vector.AppendNull();
                    }
                }
            }
            else
            {
                var buffer = new T[rowCount];
                await rowGroup.ReadAsync<T>(fileField, new Memory<T>(buffer), null, cancellationToken)
                    .ConfigureAwait(false);
                for (int i = 0; i < rowCount; i++)
                {
                    append(vector, buffer[i]);
                }
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // Parquet.Net raises ArgumentOutOfRangeException from its DATE/TIMESTAMP decode
            // (DateTime.AddDays/AddTicks) when a physical INT32/INT64 value is outside the representable
            // DateTime range — a corrupt/hostile file, not a bug in our own code (ReadValueAsync's only
            // ArgumentOutOfRangeException source is the Parquet.Net decode above; the append delegate is
            // range-safe arithmetic). IsParquetDefect deliberately excludes ArgumentException (to surface our
            // own bugs), so map this specific decode fault to the deterministic CorruptData contract
            // (ADR-0013) rather than leaking a raw BCL exception. The message names NEITHER the column NOR the
            // cell value: fileField.Name is the FILE's physical column name — under column-mapping id mode it
            // is resolved by field_id from the untrusted footer (BuildFieldIdMap) verbatim / unsanitized, so
            // it is attacker-authored for a foreign table. It is scrubbed like the sibling row-group decode
            // fault above (#176/#8/#653); the cause is preserved as the inner exception for server-side
            // diagnostics.
            throw DeltaStorageException.CorruptData(
                "A Parquet column holds a physical value outside the representable date/time range.", ex);
        }
    }

    private static async Task ReadDecimalAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        MutableColumnVector vector,
        int rowCount,
        DecimalType decimalType,
        CancellationToken cancellationToken)
    {
        // L1/CF-5: the scale factor (10^scale) and over-precision ceiling (10^precision) are invariant
        // across the whole column chunk, so compute them ONCE here instead of per value in AppendDecimal.
        ParquetTypeMapping.DecimalScaleFactors factors = ParquetTypeMapping.DecimalScaleFactors.For(decimalType);
        if (fileField.IsNullable)
        {
            var buffer = new decimal?[rowCount];
            await rowGroup.ReadAsync<decimal>(fileField, new Memory<decimal?>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                if (buffer[i] is { } value)
                {
                    ParquetTypeMapping.AppendDecimal(vector, decimalType, value, factors);
                }
                else
                {
                    vector.AppendNull();
                }
            }
        }
        else
        {
            var buffer = new decimal[rowCount];
            await rowGroup.ReadAsync<decimal>(fileField, new Memory<decimal>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                ParquetTypeMapping.AppendDecimal(vector, decimalType, buffer[i], factors);
            }
        }
    }

    private static async Task ReadStringAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        StructField requestedField,
        MutableColumnVector vector,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var buffer = new string?[rowCount];
        await rowGroup.ReadAsync(fileField, new Memory<string?>(buffer), null, cancellationToken)
            .ConfigureAwait(false);
        for (int i = 0; i < rowCount; i++)
        {
            if (buffer[i] is { } value)
            {
                vector.AppendBytes(Encoding.UTF8.GetBytes(value));
            }
            else
            {
                RejectNullInRequiredLane(requestedField);
                vector.AppendNull();
            }
        }
    }

    private static async Task ReadBinaryAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        StructField requestedField,
        MutableColumnVector vector,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[]?[rowCount];
        await rowGroup.ReadAsync(fileField, new Memory<byte[]?>(buffer), null, cancellationToken)
            .ConfigureAwait(false);
        for (int i = 0; i < rowCount; i++)
        {
            if (buffer[i] is { } value)
            {
                vector.AppendBytes(value);
            }
            else
            {
                RejectNullInRequiredLane(requestedField);
                vector.AppendNull();
            }
        }
    }

    // #807 value-level required-lane guard for TOP-LEVEL scalar string/binary. ValidateFileField's SCHEMA-level
    // nullability check (fileField.IsNullable && !expected.IsNullable) is structurally INERT for string/binary
    // because `expected` is built with honorReferenceNullability:false — always-nullable by construction —
    // deliberately, so a foreign / pre-#730 file that stores a log-required string/binary column as physically
    // OPTIONAL still reads (the physical repetition alone is not authoritative). That tolerance must NOT extend
    // to actual data: a physically-OPTIONAL string/binary column read into a requested non-nullable lane that
    // materializes a NULL would hand a null to a consumer that trusts StructField.Nullable == false. Value types
    // cannot reach here in that state — for them `expected.IsNullable` tracks the declared flag, so the schema
    // guard already rejected the physically-optional case. So this only bites the string/binary gap, and only on
    // a REAL null: an all-non-null OPTIONAL column still reads cleanly (preserving the foreign-file tolerance),
    // while the FIRST null in a required lane fails closed rather than silently null-filling it.
    //
    // SCOPE: this covers only TOP-LEVEL scalar string/binary lanes (the ReadStringAsync/ReadBinaryAsync callers).
    // NESTED string/binary (and value) leaves inside a struct/array/map go through NestedParquetColumnReader,
    // whose leaf-own nullability is now enforced by the Dremel-aware analogue
    // NestedParquetColumnReader.RejectNullInRequiredNestedLeaf (#813): a required nested leaf backed by a
    // physically-OPTIONAL column that materializes a LEAF-ATTRIBUTABLE null (every ancestor present, only the
    // leaf's own value null) fails closed there, while an ancestor-null (null struct / absent element / null
    // map entry) is accepted.
    //
    // TIMING: unlike the value-type SCHEMA guard (which rejects before any batch is decoded), this fires
    // mid-decode at the first offending value, which can be in a late row group — so earlier clean batches may
    // already have been yielded. Reads are not transactional, so partial emission before a fail-closed rejection
    // is acceptable; the invariant enforced is "no null is ever delivered INSIDE a required lane", not "reject
    // before first output".
    private static void RejectNullInRequiredLane(StructField requestedField)
    {
        if (requestedField.Nullable)
        {
            return;
        }

        throw DeltaStorageException.SchemaMismatch(
            $"Column '{DiagnosticText.Sanitize(requestedField.Name)}': a required (non-nullable) "
            + $"{DiagnosticText.DescribeType(requestedField.DataType)} column materialized a NULL from a "
            + "physically OPTIONAL Parquet column; the read-time required-lane guard (#807) rejects rather than "
            + "silently null-fill a non-nullable lane.");
    }

    // M2/RF-4b: the narrow set of exceptions the read path maps to a deterministic CorruptData — the
    // faults Parquet.Net actually raises for a malformed, truncated, or otherwise undecodable stream
    // (I/O/format faults plus Parquet.Net's own format exceptions), PLUS the two failure modes an eager
    // per-column allocation can raise if a row group slips past the decode ceiling: OutOfMemoryException
    // and OverflowException. ADR-0013 mandates the reader never let a raw OutOfMemoryException (nor an
    // unchecked-arithmetic OverflowException) escape the decode boundary, so both fail closed as
    // CorruptData rather than escaping raw. Other broad CLR exceptions (InvalidOperationException /
    // ArgumentException / IndexOutOfRangeException / NotSupportedException / FormatException) are deliberately
    // NOT in this GENERAL predicate, so a genuine bug in our own decode path surfaces as itself instead of
    // being masked as "corrupt data" — but this whitelist is INSUFFICIENT on its own to satisfy the
    // storage-delta-architecture.md §5.4 (C-DECODE) fail-closed contract, because Parquet.Net's footer-stats
    // and page/level decoders empirically raise an UNBOUNDED set of those broad BCL types from WITHIN their own
    // decode of a malformed file (the #193 CDF cdc-file fuzz drove IndexOutOfRangeException, ArgumentException,
    // InvalidOperationException, NotSupportedException, and a footer-parse NullReferenceException; a crafted
    // footer stats blob drives ArgumentException/InvalidDataException on the pruning path). A whitelist cannot
    // enumerate an unbounded set, so at the boundaries where Parquet.Net (or the DeltaSharp mapping that
    // consumes its decoded footer) touches UNTRUSTED bytes — OpenAsync (footer parse), GetRowCountAsync (the
    // checked row-group-count summation over footer NumRows fields), ReadDataSchemaAsync (mapping footer field
    // descriptors into DeltaSharp StructFields via ToDataSchema), and ReadRowGroupAsync (which spans BOTH the
    // row-group prologue: statistics/pruning + chunk footprints, AND the per-column page/level decode) — the
    // fail-closed SUPERSET IsUndecodableParquetInput maps every remaining library fault to CorruptData; see
    // those catches and IsUndecodableParquetInput for why every exception there is a library decode fault and
    // not our own bug. IsParquetDefect is retained as the informative first catch (and for the ADR-0013
    // eager-allocation OOM/Overflow mapping; the same Overflow entry also covers the GetRowCountAsync summation
    // past long.MaxValue). One further site-specific map: ArgumentOutOfRangeException in ReadValueAsync
    // (out-of-range DATE/TIMESTAMP).
    internal static bool IsParquetDefect(Exception ex) => ex is
        IOException or
        InvalidDataException or
        EndOfStreamException or
        OutOfMemoryException or
        OverflowException or
        global::Parquet.ParquetException or
        global::Parquet.Meta.Proto.ThriftProtocolException;

    // The fail-closed decode-boundary SUPERSET (storage-delta-architecture.md §5.4 C-DECODE / ADR-0013) used at
    // EVERY read entry point that consumes UNTRUSTED footer/row-group/page bytes: OpenAsync (footer parse);
    // GetRowCountAsync (the checked row-group-count summation over attacker-controlled footer NumRows fields);
    // ReadDataSchemaAsync (mapping footer field descriptors into DeltaSharp StructFields via ToDataSchema, whose
    // eager StructField construction rejects an empty footer field name with a raw ArgumentException);
    // ReadRowGroupAsync's row-group prologue (statistics/pruning + chunk footprints); and ReadRowGroupAsync's
    // page/level decode. Because Parquet.Net's decoder raises an unbounded set of raw BCL exception types on a
    // malformed file (and an eager stats decode on the predicate-pushdown pruning path the CDF door never
    // exercises), the boundary must map EVERYTHING to a deterministic CorruptData EXCEPT: (a)
    // OperationCanceledException — cooperative
    // cancellation is control flow and must propagate; and (b) DeltaStorageException — DeltaSharp's OWN typed
    // fail-closed signal (e.g. UnsupportedFeature for a genuinely unsupported but VALID feature, a
    // CorruptData already mapped at an inner site such as ReadValueAsync, or a DecodeBudgetExceeded minted by
    // the bounded-decode policy on a wall-clock timeout), which must propagate UNWRAPPED rather than be
    // re-masked; and (c) DecodeCapacityExhaustedException — the admission-control fail-fast the bounded-decode
    // worker raises when saturated by other stranded decodes, a transient RESOURCE signal that must never be
    // conflated with corrupt bytes. Every remaining exception at those boundaries originates in the library
    // decoding corrupt bytes — never in our own (request-shaped, bounded-by-construction) code, and never our
    // own unsupported-feature path (which is raised TYPED) — so it is safe to fail closed on all of them
    // without masking a genuine bug in our code. This is the fail-closed superset of the IsParquetDefect
    // whitelist above (it also covers every type IsParquetDefect matches).
    internal static bool IsUndecodableParquetInput(Exception ex) =>
        ex is not (OperationCanceledException or DeltaStorageException or DecodeCapacityExhaustedException);

    /// <summary>
    /// A read-only view of one row group's per-column statistics, exposed to a
    /// <see cref="RowGroupPredicate"/> for pruning.
    /// </summary>
    /// <remarks>
    /// <para><b>Min/Max are ENGINE-LANE-normalized (M3).</b> <see cref="Min"/>/<see cref="Max"/> return
    /// values in the same physical lane space the reader decodes columns into — <c>int</c> epoch-day for
    /// DATE, <c>long</c> epoch-microseconds for TIMESTAMP, unscaled <c>Int128</c> for DECIMAL — so a
    /// lane-space pruning predicate compares apples to apples and can never silently drop a matching row.
    /// The raw Parquet-space values remain available via <see cref="RawMin"/>/<see cref="RawMax"/>.</para>
    /// <para><b>Pruning is safe-by-construction (M6).</b> When a statistic is missing, cannot be
    /// lane-normalized without loss (a wide DECIMAL whose stat is a logical <c>BigDecimal</c>), or is a
    /// <c>NaN</c>-poisoned float/double bound, <see cref="Min"/>/<see cref="Max"/> return
    /// <see langword="null"/> — meaning "cannot prune", so the residual predicate is always evaluated and
    /// no row is ever wrongly skipped. TIMESTAMP bounds are additionally widened by ±1&#160;ms because
    /// Parquet's timestamp statistics are millisecond-truncated toward zero, keeping <see cref="Min"/> a
    /// true lower bound and <see cref="Max"/> a true upper bound.</para>
    /// </remarks>
    internal sealed class RowGroupStatistics
    {
        private readonly Dictionary<string, ColumnEntry> _byColumn;

        internal RowGroupStatistics(ParquetRowGroupReader rowGroup, StructType requested, ResolvedColumn[] fileFields)
        {
            RowCount = rowGroup.RowCount;
            _byColumn = new Dictionary<string, ColumnEntry>(StringComparer.Ordinal);
            for (int c = 0; c < requested.Count; c++)
            {
                // A column absent from the file (#497 null-fill) or NESTED (#571) carries no scalar statistics
                // here, so pruning on it always returns "cannot prune" — an all-null column has no useful
                // bounds, and nested min/max pruning is not modeled in this increment.
                DataColumnStatistics? statistics =
                    fileFields[c].Scalar is { } fileField && rowGroup.ColumnExists(fileField)
                        ? rowGroup.GetStatistics(fileField)
                        : null;
                _byColumn[requested[c].Name] = new ColumnEntry(requested[c].DataType, statistics);
            }
        }

        /// <summary>The number of rows in the row group.</summary>
        public long RowCount { get; }

        /// <summary>The raw Parquet.Net statistics for <paramref name="column"/>, or
        /// <see langword="null"/> if absent.</summary>
        public DataColumnStatistics? ForColumn(string column) =>
            _byColumn.TryGetValue(column, out ColumnEntry entry) ? entry.Statistics : null;

        /// <summary>The engine-lane-normalized minimum for <paramref name="column"/>, or
        /// <see langword="null"/> when pruning is not safe (see the type remarks).</summary>
        public object? Min(string column) => LaneBound(column, isMin: true);

        /// <summary>The engine-lane-normalized maximum for <paramref name="column"/>, or
        /// <see langword="null"/> when pruning is not safe (see the type remarks).</summary>
        public object? Max(string column) => LaneBound(column, isMin: false);

        /// <summary>The raw Parquet-space minimum for <paramref name="column"/> (before lane
        /// normalization), or <see langword="null"/> if absent.</summary>
        public object? RawMin(string column) => ForColumn(column)?.MinValue;

        /// <summary>The raw Parquet-space maximum for <paramref name="column"/> (before lane
        /// normalization), or <see langword="null"/> if absent.</summary>
        public object? RawMax(string column) => ForColumn(column)?.MaxValue;

        /// <summary>The null count recorded for <paramref name="column"/>, or <see langword="null"/>.</summary>
        public long? NullCount(string column) =>
            _byColumn.TryGetValue(column, out ColumnEntry entry) ? entry.Statistics?.NullCount : null;

        private object? LaneBound(string column, bool isMin)
        {
            if (!_byColumn.TryGetValue(column, out ColumnEntry entry) || entry.Statistics is null)
            {
                return null;
            }

            object? raw = isMin ? entry.Statistics.MinValue : entry.Statistics.MaxValue;
            if (raw is null)
            {
                return null;
            }

            return Normalize(entry.DataType, raw, isMin);
        }

        // Convert a Parquet-space statistic to the engine lane value, returning null ("cannot prune")
        // whenever normalization would be lossy/ambiguous so a row is never wrongly skipped (M3/M6).
        private static object? Normalize(DataType type, object raw, bool isMin)
        {
            switch (type)
            {
                case BooleanType when raw is bool b:
                    return b;
                case ByteType when raw is int i:
                    return (sbyte)i; // INT8 stat is the signed logical tinyint value.
                case ShortType when raw is int i:
                    return (short)i;
                case IntegerType when raw is int i:
                    return i;
                case LongType when raw is long l:
                    return l;
                case FloatType when raw is float f:
                    return float.IsNaN(f) ? null : f; // NaN-poisoned bound => cannot prune.
                case DoubleType when raw is double d:
                    return double.IsNaN(d) ? null : d;
                case DateType when raw is int i:
                    return i; // Epoch-day already equals the engine lane.
                case TimestampType when raw is long millis:
                    return WidenTimestampMillis(millis, isMin);
                case DecimalType decimalType:
                    return NormalizeDecimal(decimalType, raw);
                default:
                    // string/binary and any unexpected physical type: not prunable in v1.
                    return null;
            }
        }

        // Parquet timestamp statistics are millisecond-truncated toward zero, so the engine-lane
        // (microsecond) bound must be widened by ±1 ms to stay a true lower/upper bound regardless of
        // sign; overflow of the widening or the ms→µs scale => null ("cannot prune").
        private static object? WidenTimestampMillis(long millis, bool isMin)
        {
            try
            {
                long widenedMillis = checked(isMin ? millis - 1 : millis + 1);
                return checked(widenedMillis * 1000L);
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        private static object? NormalizeDecimal(DecimalType decimalType, object raw)
        {
            // A compact decimal (precision ≤ 18) stat is the Int64 unscaled value == the engine lane; a
            // wide decimal stat is a logical BigDecimal we cannot losslessly lane-normalize here, so we
            // return null (cannot prune) rather than risk an incorrect bound.
            return raw switch
            {
                long unscaled => (Int128)unscaled,
                int unscaled => (Int128)unscaled,
                _ => null,
            };
        }

        private readonly struct ColumnEntry
        {
            internal ColumnEntry(DataType dataType, DataColumnStatistics? statistics)
            {
                DataType = dataType;
                Statistics = statistics;
            }

            internal DataType DataType { get; }

            internal DataColumnStatistics? Statistics { get; }
        }
    }
}
