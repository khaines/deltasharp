using System.Collections.Immutable;
using DeltaSharp.Storage.Delta.DeletionVectors;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The base type for an explicitly-modeled Delta transaction-log action (design §2.10.1). Every
/// action a v1-baseline reader must understand — <c>protocol</c>, <c>metaData</c>, <c>add</c>,
/// <c>remove</c>, <c>txn</c>, <c>commitInfo</c> — is a sealed subtype so replay (§2.10.4) is a total,
/// exhaustive match. Unknown top-level action keys are ignored for forward compatibility (Delta's
/// documented rule), but an unsupported <b>protocol feature</b> fails closed (§2.10.5).
/// </summary>
internal abstract record DeltaAction;

/// <summary>
/// <c>protocol</c> — the minimum reader/writer versions and (reader v3 / writer v7 "table features")
/// the named reader/writer features required to access the table. Gates every read/write (§2.10.5).
/// </summary>
internal sealed record ProtocolAction(
    int MinReaderVersion,
    int MinWriterVersion,
    ImmutableArray<string> ReaderFeatures,
    ImmutableArray<string> WriterFeatures) : DeltaAction;

/// <summary>The <c>format</c> sub-object of <c>metaData</c> — the data file provider and its options.</summary>
internal sealed record TableFormat(string Provider, ImmutableSortedDictionary<string, string> Options);

/// <summary>
/// <c>metaData</c> — table identity, the Spark <see cref="SchemaString"/> (parsed lazily via the
/// relocated <c>SchemaJson</c>, design §9.1 D-6), partition columns, and table configuration
/// (design §2.10.1).
/// </summary>
internal sealed record MetadataAction(
    string Id,
    string? Name,
    string? Description,
    TableFormat Format,
    string SchemaString,
    ImmutableArray<string> PartitionColumns,
    ImmutableSortedDictionary<string, string> Configuration,
    long? CreatedTime) : DeltaAction;

/// <summary>
/// <c>add</c> — a data file that becomes active table state only via this committed action (never a
/// directory listing; §2.10.4 anti-pattern #1). Carries partition values, size/mtime, the
/// <c>dataChange</c> flag, optional parsed <see cref="Stats"/>, tags, and an optional
/// <see cref="DeletionVector"/> (STORY-05.5.1): when present, the rows it names are physically in the file
/// but logically deleted and MUST be excluded from scans. A DV rides on the <c>add</c> so it participates in
/// snapshot reconstruction and time travel by version (design §2.10.1; Delta protocol "Deletion Vectors").
/// </summary>
internal sealed record AddFileAction(
    string Path,
    ImmutableSortedDictionary<string, string?> PartitionValues,
    long Size,
    long ModificationTime,
    bool DataChange,
    FileStatistics? Stats,
    ImmutableSortedDictionary<string, string> Tags,
    DeletionVectorDescriptor? DeletionVector = null) : DeltaAction;

/// <summary>
/// <c>remove</c> — a tombstone deleting a previously-<c>add</c>ed <see cref="Path"/> from the active
/// set. Retained for time travel + VACUUM; when <see cref="ExtendedFileMetadata"/> is true it round-trips
/// <c>partitionValues</c>/<c>size</c> for checkpoint fidelity (design §2.10.1). An optional
/// <see cref="DeletionVector"/> records the DV the removed logical file carried, so the DV forms part of the
/// logical file's identity when a merge-on-read delete supersedes it with a new DV on the same path.
/// </summary>
internal sealed record RemoveFileAction(
    string Path,
    long? DeletionTimestamp,
    bool DataChange,
    bool ExtendedFileMetadata,
    ImmutableSortedDictionary<string, string?> PartitionValues,
    long? Size,
    DeletionVectorDescriptor? DeletionVector = null) : DeltaAction;

/// <summary>
/// <c>txn</c> — an application transaction marker (<c>appId</c> → last committed <c>version</c>) used
/// for idempotent writes (design §2.10.1, §2.11).
/// </summary>
internal sealed record TxnAction(string AppId, long Version, long? LastUpdated) : DeltaAction;

/// <summary>
/// <c>commitInfo</c> — optional, best-effort commit provenance. Not load-bearing for replay; the raw
/// key/value pairs are preserved for diagnostics/audit without imposing a rigid schema (design §2.10.1).
/// </summary>
internal sealed record CommitInfoAction(ImmutableSortedDictionary<string, string> Entries) : DeltaAction;
