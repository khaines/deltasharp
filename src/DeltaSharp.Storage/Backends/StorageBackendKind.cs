namespace DeltaSharp.Storage.Backends;

/// <summary>
/// The bounded, low-cardinality identity of an <see cref="IStorageBackend"/> — the object-store family a
/// signal concerns (design §2.13.2). It backs the <c>deltasharp.backend</c> telemetry label/attribute
/// (docs/engineering/design/storage-delta-architecture.md §7.3/§7.4): a <b>closed</b> set that is safe as a
/// metric label because it never grows per run or per table. It is deliberately <b>not</b> a plug-in SPI —
/// backends are selected internally by URI scheme + options — so this enum enumerates only the four
/// first-party adapters the design commits to.
/// </summary>
internal enum StorageBackendKind
{
    /// <summary>Amazon S3 (and S3-compatible) object storage.</summary>
    S3,

    /// <summary>Azure Data Lake Storage Gen2 / Azure Blob Storage.</summary>
    Adls,

    /// <summary>Google Cloud Storage.</summary>
    Gcs,

    /// <summary>A Kubernetes PersistentVolume / local POSIX file system.</summary>
    Pvc,
}

/// <summary>Maps a <see cref="StorageBackendKind"/> to its stable, low-cardinality telemetry label — the
/// exact <c>deltasharp.backend</c> value the design's dashboards and the OpenTelemetry export pipeline key
/// on (§7.3). Kept beside the enum so the label strings live in one compiled place and cannot drift.</summary>
internal static class StorageBackendKindExtensions
{
    /// <summary>The bounded <c>deltasharp.backend</c> label value for <paramref name="kind"/>.</summary>
    internal static string ToLabel(this StorageBackendKind kind) => kind switch
    {
        StorageBackendKind.S3 => "s3",
        StorageBackendKind.Adls => "adls",
        StorageBackendKind.Gcs => "gcs",
        StorageBackendKind.Pvc => "pvc",
        _ => "unknown",
    };
}
