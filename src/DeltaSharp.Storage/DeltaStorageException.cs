using DeltaSharp.Storage.Delta;

namespace DeltaSharp.Storage;

/// <summary>
/// The deterministic failure classes the storage layer raises. Every storage defect maps to
/// exactly one kind so callers (and tests) can branch on the cause without string-matching a
/// message (design §2.9.1 "fails deterministically", §2.13.3 failure handling).
/// </summary>
internal enum StorageErrorKind
{
    /// <summary>A physical type, Parquet logical type, or feature DeltaSharp does not yet support
    /// (for example nested Parquet columns). The message names the unsupported feature.</summary>
    UnsupportedFeature,

    /// <summary>A malformed, truncated, or otherwise corrupt Parquet file or footer. Never yields
    /// partial rows (design §2.9.1); the message names the defect.</summary>
    CorruptData,

    /// <summary>A structurally valid file whose column physical type or nullability does not match the
    /// requested engine type (design §2.9.1). Distinct from <see cref="CorruptData"/> so a schema/type
    /// disagreement is not conflated with byte-level corruption; the message names the mismatch.</summary>
    SchemaMismatch,

    /// <summary>A column the current data schema requests is <b>absent</b> from a Parquet file's schema and
    /// cannot be read-side null-filled (an absent REQUIRED column, or a strict projection). This is the
    /// additive schema-evolution (#190/#497) narrow-file signal: distinct from <see cref="CorruptData"/> so
    /// the read (<c>DeltaReadSource</c>) and OPTIMIZE (<c>DeltaOptimize</c>) guards can classify it on the
    /// <see cref="DeltaStorageException.Kind"/> rather than string-matching the message (#513). The message
    /// names the absent column.</summary>
    ColumnNotPresentInFile,

    /// <summary>A required object (file/path) does not exist.</summary>
    NotFound,

    /// <summary>An object that must be created atomically already exists (a lost commit race that is
    /// unambiguously not this caller's win, or a data file already present at the destination).</summary>
    AlreadyExists,

    /// <summary>A user- or log-supplied path escapes the configured table root / tenant prefix -- or its
    /// confinement <b>could not be proven</b> (e.g. an inaccessible or cyclic ancestor blocks real-path
    /// resolution) -- and is rejected fail-closed (design §5.5 C-SCOPE, checklist 14). The message names
    /// only the relative offending path, never the absolute root.</summary>
    PathNotConfined,

    /// <summary>A storage operation failed in a way that cannot be safely retried because its outcome is
    /// ambiguous (design §2.11.3 "ambiguous commit PUT"): the effect <b>may</b> have taken place but could
    /// not be confirmed durable. The caller must re-resolve <b>idempotently</b> — read back whether its own
    /// effect landed (by transaction id or content) — and must never blindly retry the same slot or advance
    /// as if it definitely failed, since either can double-commit.</summary>
    RetryUnsafeAmbiguous,

    /// <summary>A transient, retryable condition (throttling, a temporary I/O error). Design §2.13.3
    /// classifies these for bounded backoff + retry.</summary>
    Transient,
}

/// <summary>
/// The single deterministic exception the <c>DeltaSharp.Storage</c> layer throws. It carries a
/// <see cref="Kind"/> so a failure is classifiable without parsing its message, and a message that
/// <b>names</b> the unsupported feature or the concrete defect (design §2.9.1, §2.13.3).
/// </summary>
/// <remarks>
/// <b>Message hygiene obligation (#747):</b> every factory that accepts a fully-composed
/// <c>string message</c> or <c>string defect</c> passes it verbatim to
/// <see cref="Exception.Message"/>. Any caller-supplied token that is attacker-influenceable
/// (a column name, a type name from a foreign Parquet footer, a path segment from a foreign
/// <c>_delta_log</c>) MUST be routed through <see cref="DiagnosticText.Sanitize"/> — or a
/// stronger drop/minimization — BEFORE interpolation. The sweep test in
/// <c>StorageHygieneSweepTests</c> covers every known call site; new call sites carry the
/// same obligation.
/// </remarks>
internal sealed class DeltaStorageException : Exception
{
    /// <summary>Creates a storage exception of the given <paramref name="kind"/>.</summary>
    /// <param name="kind">The deterministic failure class.</param>
    /// <param name="message">A message naming the unsupported feature or the concrete defect.</param>
    /// <param name="innerException">The optional underlying cause.</param>
    public DeltaStorageException(StorageErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException) => Kind = kind;

    /// <summary>The deterministic failure class of this error.</summary>
    public StorageErrorKind Kind { get; }

    /// <summary>
    /// The RAW table-relative object path this failure concerns, when the failing operation names one.
    /// <para>The <see cref="System.Exception.Message"/> deliberately renders a path through
    /// <see cref="DiagnosticText.DescribePath"/>, which drops the Hive partition VALUES (column values, i.e.
    /// table data and potentially PII) and keeps only the sanitized file name and partition COLUMN NAMES.
    /// The table owner, however, is entitled to their own data and needs the exact key to act on it — so the
    /// unmodified path is retained here for a caller to read and route deliberately to a sink it trusts.
    /// This is the same split <c>DeltaSchemaMismatchException.Path</c> already uses (#682).</para>
    /// <para>Treat this as untrusted text: it comes from the <c>_delta_log</c>. Anything that writes it to a
    /// structured-log sink must sanitize it at that boundary.</para>
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Renders this exception without its <see cref="System.Exception.InnerException"/> chain.
    /// </summary>
    /// <remarks>
    /// The <see cref="System.Exception.Message"/> is authored by factory methods whose call sites are
    /// responsible for sanitizing attacker-influenceable tokens before interpolation (verified by
    /// <c>StorageHygieneSweepTests</c> for every known producer; see the type-level <c>&lt;remarks&gt;</c>
    /// for the full obligation — #745/#749). The raw
    /// underlying cause (e.g. a Parquet.Net exception over crafted bytes) is retained as the inner for
    /// server-side diagnostics; the default <c>ToString()</c> / <c>ILogger.LogError(ex, …)</c> would
    /// re-surface that raw inner, so this override omits it (#664, RF-8b parity). The inner remains
    /// reachable via <see cref="System.Exception.InnerException"/>.
    /// </remarks>
    public override string ToString() => DiagnosticText.DescribeWithoutInner(this, Kind.ToString());

    /// <summary>Creates an <see cref="StorageErrorKind.UnsupportedFeature"/> error naming the feature.
    /// <para><b>Message hygiene obligation (#747):</b> this factory accepts a fully-composed message; it cannot
    /// sanitize tokens on the caller's behalf. Any caller-supplied token that is attacker-influenceable
    /// (a column name, a type name derived from a foreign Parquet footer, a protocol feature key from a
    /// foreign <c>_delta_log</c>) MUST be routed through <see cref="DiagnosticText.Sanitize"/> — or a
    /// stronger drop/minimization — BEFORE interpolation. The sweep test in
    /// <c>StorageHygieneSweepTests</c> enforces this for every known call site; new call sites must
    /// satisfy the same property.</para></summary>
    public static DeltaStorageException UnsupportedFeature(string feature) =>
        new(StorageErrorKind.UnsupportedFeature, feature);

    /// <summary>Creates a <see cref="StorageErrorKind.CorruptData"/> error naming the defect.</summary>
    /// <remarks><b>Message hygiene obligation:</b> see the type-level
    /// <c>&lt;remarks&gt;</c> — the <paramref name="defect"/> string is accepted fully-composed;
    /// any attacker-influenceable token must be sanitized by the caller before interpolation.</remarks>
    public static DeltaStorageException CorruptData(string defect, Exception? innerException = null) =>
        new(StorageErrorKind.CorruptData, defect, innerException);

    /// <summary>Creates a <see cref="StorageErrorKind.SchemaMismatch"/> error naming the mismatch.
    /// <para><b>Message hygiene obligation (#747):</b> same contract as
    /// <see cref="UnsupportedFeature"/>: the message is accepted fully-composed; any attacker-influenceable
    /// token must be routed through <see cref="DiagnosticText.Sanitize"/> by the caller before
    /// interpolation. The sweep test in <c>StorageHygieneSweepTests</c> covers every known call site.</para></summary>
    public static DeltaStorageException SchemaMismatch(string message) =>
        new(StorageErrorKind.SchemaMismatch, message);

    /// <summary>Creates a <see cref="StorageErrorKind.ColumnNotPresentInFile"/> error: a requested column is
    /// absent from the Parquet file schema and cannot be read-side null-filled. Carries a dedicated kind so
    /// the read/OPTIMIZE schema-evolution guards classify it without string-matching the message (#513).</summary>
    public static DeltaStorageException ColumnNotPresentInFile(string columnName) =>
        new(StorageErrorKind.ColumnNotPresentInFile,
            // #683/#685: the requested column name is a schema identifier that, on a foreign/untrusted table,
            // is attacker-authored — route it through the shared sanitizer (control-char strip + length cap)
            // exactly like DeltaSchemaMismatchException already does, so the two cannot drift.
            $"Requested column '{DiagnosticText.Sanitize(columnName)}' is not present in the Parquet file schema.");

    /// <summary>Creates a <see cref="StorageErrorKind.Transient"/> error (a retryable I/O condition).</summary>
    // NOTE (#683/#685): a table-relative path that must appear in a Transient message is redacted at the CALL
    // SITE via DiagnosticText.DescribePath (drops Hive partition VALUES, keeps shape + partition column names)
    // — rendering moved out of this factory. The raw path is retained on the typed Path property for the
    // entitled owner; a reflecting log sink that destructures it is the caller's obligation, not this
    // factory's (see DiagnosticText's class doc). Local POSIX paths carry no secret today.
    // OPEN OBLIGATION (formerly #113): DescribePath does NOT parse URIs / redact URL credentials (SAS ?sig=,
    // presigned signatures, userinfo). When an object-store backend (S3/ADLS/GCS) lands, a credential-bearing
    // URI reaching this factory MUST be redacted by a dedicated URL renderer FIRST — see
    // storage-delta-architecture.md §5.3 "OUT OF SCOPE — credential-bearing URIs".
    public static DeltaStorageException Transient(string message, Exception? innerException = null, string? path = null) =>
        new(StorageErrorKind.Transient, message, innerException) { Path = path };

    /// <summary>Creates a <see cref="StorageErrorKind.RetryUnsafeAmbiguous"/> error: an operation whose
    /// outcome cannot be determined (the effect may have landed but is not confirmed durable), so the
    /// caller must re-resolve idempotently rather than blindly retry or assume failure.</summary>
    public static DeltaStorageException RetryUnsafeAmbiguous(string message, Exception? innerException = null, string? path = null) =>
        new(StorageErrorKind.RetryUnsafeAmbiguous, message, innerException) { Path = path };

    /// <summary>Creates a <see cref="StorageErrorKind.PathNotConfined"/> error naming the rejected path.</summary>
    public static DeltaStorageException PathNotConfined(string message, string? path = null) =>
        new(StorageErrorKind.PathNotConfined, message) { Path = path };

    /// <summary>Creates a <see cref="StorageErrorKind.NotFound"/> error.</summary>
    public static DeltaStorageException NotFound(string message, Exception? innerException = null, string? path = null) =>
        new(StorageErrorKind.NotFound, message, innerException) { Path = path };

    /// <summary>Creates an <see cref="StorageErrorKind.AlreadyExists"/> error.</summary>
    public static DeltaStorageException AlreadyExists(string message, string? path = null) =>
        new(StorageErrorKind.AlreadyExists, message) { Path = path };
}
