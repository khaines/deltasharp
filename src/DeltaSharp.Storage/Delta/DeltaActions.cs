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
/// the extended trio — <c>partitionValues</c>/<c>size</c>/<c>tags</c> — for checkpoint fidelity and strict
/// cross-engine readers (design §2.10.1; Delta protocol "Remove File"). <see cref="Tags"/> mirrors
/// <see cref="AddFileAction.Tags"/> (default empty); DeltaSharp's own removes carry none today. An optional
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
    ImmutableSortedDictionary<string, string> Tags,
    DeletionVectorDescriptor? DeletionVector = null) : DeltaAction;

/// <summary>
/// <c>txn</c> — an application transaction marker (<c>appId</c> → last committed <c>version</c>) used
/// for idempotent writes (design §2.10.1, §2.11).
/// </summary>
internal sealed record TxnAction(string AppId, long Version, long? LastUpdated) : DeltaAction;

/// <summary>
/// <c>commitInfo</c> — optional, best-effort commit provenance. Not load-bearing for replay (§2.10.1): a
/// reader ignores it for correctness, so this is an interop/parity enrichment, not a rule. Alongside the
/// flat <see cref="Entries"/> (which carry the engine's idempotency <c>txnId</c> and any other raw
/// key/value provenance) it models the typed fields the Delta ecosystem (<c>DESCRIBE HISTORY</c>,
/// delta-standalone/-rs/-spark) reads: the commit <see cref="Timestamp"/> (epoch-ms), the
/// <see cref="Operation"/> string (e.g. <c>WRITE</c>, <c>DELETE</c>, <c>OPTIMIZE</c>, <c>CREATE TABLE</c>),
/// its <see cref="OperationParameters"/>, the per-operation <see cref="OperationMetrics"/>, and the
/// <see cref="EngineInfo"/>. All the typed fields are optional so an older or caller-minted
/// <c>commitInfo</c> carrying only <see cref="Entries"/> stays valid (backward compatible).
///
/// <para><b><see cref="OperationParameters"/> value contract.</b> Per the Delta spec each
/// <c>operationParameters</c> value is itself a <b>JSON-encoded string</b> — a scalar is a JSON string
/// literal (e.g. <c>mode</c> ⇒ <c>"Append"</c> WITH the quotes) and a list is a JSON array (e.g.
/// <c>partitionBy</c> ⇒ <c>["region"]</c>). This map therefore stores each value as the <b>already
/// JSON-encoded token</b> (string → raw-JSON-string); the writer emits it verbatim via
/// <c>Utf8JsonWriter.WriteRawValue</c> so a quoted scalar or a bracketed array serializes correctly, and the
/// reader round-trips it with <c>JsonElement.GetRawText</c>. Build values through
/// <see cref="DeltaCommitInfo"/> (never hand-concatenate) so encoding stays correct.</para>
///
/// <para><b><see cref="OperationMetrics"/> value contract.</b> <c>operationMetrics</c> is likewise a Delta
/// <c>Map&lt;String,String&gt;</c>, so — exactly like <see cref="OperationParameters"/> — each value is an
/// already JSON-encoded token. A metric is a numeric string, so its token is a quoted number-string (e.g.
/// <c>numAddedFiles</c> ⇒ <c>"3"</c>, WITH the quotes, NOT a bare JSON number). The writer emits it raw and
/// the reader round-trips it with <c>GetRawText</c>, identically to <see cref="OperationParameters"/>.</para>
/// </summary>
internal sealed record CommitInfoAction(
    ImmutableSortedDictionary<string, string> Entries,
    long? Timestamp = null,
    string? Operation = null,
    ImmutableSortedDictionary<string, string>? OperationParameters = null,
    ImmutableSortedDictionary<string, string>? OperationMetrics = null,
    string? EngineInfo = null) : DeltaAction;
