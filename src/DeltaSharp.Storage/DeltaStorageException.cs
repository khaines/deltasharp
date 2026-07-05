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

    /// <summary>A required object (file/path) does not exist.</summary>
    NotFound,

    /// <summary>An object that must be created atomically already exists (a lost commit race that is
    /// unambiguously not this caller's win, or a data file already present at the destination).</summary>
    AlreadyExists,

    /// <summary>A user- or log-supplied path escapes the configured table root / tenant prefix and is
    /// rejected fail-closed (design §5.5 C-SCOPE, checklist 14). The message names the offending path.</summary>
    PathNotConfined,

    /// <summary>A storage operation failed in a way that cannot be safely retried because its outcome is
    /// ambiguous (design §2.11.3 "ambiguous commit PUT"). The caller must re-resolve, not blindly retry.</summary>
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

    /// <summary>Creates an <see cref="StorageErrorKind.UnsupportedFeature"/> error naming the feature.</summary>
    public static DeltaStorageException UnsupportedFeature(string feature) =>
        new(StorageErrorKind.UnsupportedFeature, feature);

    /// <summary>Creates a <see cref="StorageErrorKind.CorruptData"/> error naming the defect.</summary>
    public static DeltaStorageException CorruptData(string defect, Exception? innerException = null) =>
        new(StorageErrorKind.CorruptData, defect, innerException);

    /// <summary>Creates a <see cref="StorageErrorKind.PathNotConfined"/> error naming the rejected path.</summary>
    public static DeltaStorageException PathNotConfined(string message) =>
        new(StorageErrorKind.PathNotConfined, message);

    /// <summary>Creates a <see cref="StorageErrorKind.NotFound"/> error.</summary>
    public static DeltaStorageException NotFound(string message, Exception? innerException = null) =>
        new(StorageErrorKind.NotFound, message, innerException);

    /// <summary>Creates an <see cref="StorageErrorKind.AlreadyExists"/> error.</summary>
    public static DeltaStorageException AlreadyExists(string message) =>
        new(StorageErrorKind.AlreadyExists, message);
}
