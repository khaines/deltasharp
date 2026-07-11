using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Delta;

/// <summary>How an overwrite commit replaces prior data (Spark's
/// <c>spark.sql.sources.partitionOverwriteMode</c>): replace <b>every</b> active file
/// (<see cref="Static"/> — the default, a full-table overwrite) or replace only the active files in the
/// partitions the new write touches (<see cref="Dynamic"/> — dynamic partition overwrite).</summary>
internal enum PartitionOverwriteMode
{
    /// <summary>Full-table overwrite: every prior active file is removed in the same atomic version as the
    /// new adds (STORY-05.3.3 AC2).</summary>
    Static,

    /// <summary>Dynamic partition overwrite: only the prior active files whose partition values match a
    /// partition the new write touches are removed; files in untouched partitions stay active
    /// (STORY-05.3.3 AC3).</summary>
    Dynamic,
}

/// <summary>
/// A data file already written out to storage (its bytes exist), described by the facts a Delta
/// <c>add</c> action needs: the table-relative <see cref="Path"/>, its <see cref="PartitionValues"/>
/// (a value — possibly null — for <b>every</b> partition column of a partitioned table; empty for an
/// unpartitioned table), byte <see cref="Size"/>, <see cref="ModificationTime"/>, and optional per-file
/// <see cref="Stats"/>.
///
/// <para><b>Partition-coverage contract.</b> For a partitioned table each <see cref="PartitionValues"/>
/// must carry a key for every partition column (the value may be null for the null partition). A staged
/// file missing a partition column is rejected fail-closed by <see cref="DeltaTableWriter"/>: silently
/// coercing a missing key to the null partition would land data in the wrong partition and, for a file
/// already in the log, is the malformed state that makes a read-oriented pruner over-select it for
/// removal during a dynamic overwrite.</para>
///
/// <para><b>Write-time statistics boundary (#197).</b> Generating rich min/max/nullCount statistics from
/// the file's rows is STORY-05.3.4 / #197's responsibility. STORY-05.3.3 (#188) commits the adds with the
/// size/row-count the caller already has and leaves <see cref="Stats"/> as <see langword="null"/> (or a
/// minimal stat) when the extractor is not yet wired — pruning simply forfeits the opportunity, never
/// correctness (statistics are advisory, design §2.10.5).</para>
/// </summary>
internal sealed record StagedDataFile(
    string Path,
    ImmutableSortedDictionary<string, string?> PartitionValues,
    long Size,
    long ModificationTime,
    FileStatistics? Stats = null);

/// <summary>
/// Builds the correct <see cref="DeltaAction"/> set and <see cref="DeltaReadScope"/> for a Delta write
/// operation and publishes it atomically through <see cref="DeltaCommitter.CommitAsync"/> (STORY-05.3.3,
/// design §2.11). It is the storage-side AC-bearing core for the three ACID write shapes:
///
/// <list type="bullet">
/// <item><b>Append</b> (<see cref="AppendAsync(Snapshot, StructType, IReadOnlyList{StagedDataFile}, SchemaEvolutionMode, CancellationToken)"/>):
/// only <c>add</c> actions under <see cref="DeltaReadScope.BlindAppend"/>; prior active files stay active
/// and the write rebases past concurrent appends (AC1).</item>
/// <item><b>Full overwrite</b> (<see cref="OverwriteAsync"/> with
/// <see cref="PartitionOverwriteMode.Static"/>): <c>remove</c> of <b>every</b> prior active file plus the
/// new <c>add</c>s in one atomic version, under <see cref="DeltaReadScope.WholeTable"/> so any concurrent
/// add/remove aborts it (AC2).</item>
/// <item><b>Dynamic partition overwrite</b> (<see cref="OverwriteAsync"/> with
/// <see cref="PartitionOverwriteMode.Dynamic"/>): <c>remove</c> of only the prior active files in the
/// touched partitions plus the new <c>add</c>s, scoped with
/// <see cref="DeltaReadScope.ReadFiles(IEnumerable{string})"/> to those prior files so a concurrent
/// remove/re-add of one of them aborts it while an append to an untouched partition rebases. A concurrent
/// new-file append <i>into</i> a touched partition is not detected (it needs a partition-predicate read
/// scope) — tracked in #488. For an unpartitioned table a dynamic overwrite is a full-table overwrite and
/// is routed to the <see cref="PartitionOverwriteMode.Static"/> path (WholeTable) (AC3).</item>
/// </list>
///
/// <para><b>Layering.</b> This type lives in the storage layer and takes the <i>staged</i> data files as
/// input; it does not execute a plan or generate the data (that is the executor's job). The
/// <c>SaveMode</c> → operation mapping (Append→append; Overwrite→<see cref="OverwriteAsync"/> per
/// the partition-overwrite mode; Ignore/ErrorIfExists→their existing existence semantics) is applied by
/// the executor/write-door seam that calls this type — see the STORY-05.3.3 end-to-end wiring note.</para>
///
/// <para><b>Schema declaration is mandatory.</b> Every write entry point <b>requires</b> the incoming
/// <c>writeSchema</c>; schema enforcement (<see cref="DeltaSchemaEnforcer"/>) <b>always</b> runs before any
/// action is built. There is deliberately no "trust the table schema" overload: one would let a caller
/// commit schema-incompatible data with no enforcement at all (a silent bypass). The production write-door
/// (#487) supplies the true schema of the staged Parquet; validating that the staged files physically match
/// that declared schema is that write-door's / the read path's responsibility (#497).</para>
/// </summary>
internal sealed class DeltaTableWriter
{
    private static readonly ImmutableSortedDictionary<string, string> NoTags =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private readonly DeltaLog _log;
    private readonly DeltaCommitter _committer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a writer over <paramref name="backend"/> (constructs its own log reader + committer).</summary>
    public DeltaTableWriter(IStorageBackend backend)
        : this(new DeltaLog(backend), new DeltaCommitter(backend), TimeProvider.System)
    {
    }

    /// <summary>Creates a writer over an explicit reader + committer (tests inject a committer with a
    /// race probe / bounded retries, and a deterministic clock for tombstone timestamps).</summary>
    internal DeltaTableWriter(DeltaLog log, DeltaCommitter committer, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(committer);
        _log = log;
        _committer = committer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Loads the latest snapshot and appends <paramref name="files"/> to it, enforcing/evolving the incoming
    /// <paramref name="writeSchema"/> against the table schema (STORY-05.4.2 AC1/AC2 convenience). Schema
    /// declaration is mandatory (there is no no-schema overload — that would bypass enforcement); pass the
    /// table's own schema to express "this write conforms to the current schema".
    /// </summary>
    /// <exception cref="DeltaSchemaMismatchException">The write is incompatible with the table schema, or
    /// requires a schema change <paramref name="evolutionMode"/> does not permit.</exception>
    public async Task<DeltaCommitResult> AppendAsync(
        StructType writeSchema,
        IReadOnlyList<StagedDataFile> files,
        SchemaEvolutionMode evolutionMode = SchemaEvolutionMode.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        Snapshot readSnapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
        return await AppendAsync(readSnapshot, writeSchema, files, evolutionMode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends <paramref name="files"/> to <paramref name="readSnapshot"/> after enforcing/evolving the
    /// incoming <paramref name="writeSchema"/> against the table schema (STORY-05.4.2 AC1/AC2). Schema
    /// enforcement runs <b>before</b> any action is built or committed: a write with an incompatible type,
    /// a missing required (non-nullable) column, a nullability violation, or a change
    /// <paramref name="evolutionMode"/> does not permit throws <see cref="DeltaSchemaMismatchException"/>
    /// here, so the table is left completely unchanged (reject-before-commit). When
    /// <paramref name="evolutionMode"/> allows an additive change (a new nullable column) the merged schema
    /// is committed as a <c>metaData</c> action in the <b>same</b> version as the new adds (atomic
    /// evolution). No existing column's type is ever changed — type widening is fail-closed (#495). Commits
    /// under <see cref="DeltaReadScope.BlindAppend"/>; an evolution append additionally carries metadata, so
    /// any concurrent commit aborts it (a schema change needs a fresh snapshot — AC4).
    /// </summary>
    /// <exception cref="DeltaSchemaMismatchException">The write is incompatible with the table schema, or
    /// requires a schema change <paramref name="evolutionMode"/> does not permit.</exception>
    public Task<DeltaCommitResult> AppendAsync(
        Snapshot readSnapshot,
        StructType writeSchema,
        IReadOnlyList<StagedDataFile> files,
        SchemaEvolutionMode evolutionMode = SchemaEvolutionMode.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException("An append must stage at least one data file.", nameof(files));
        }

        MetadataAction? schemaEvolution = ReconcileSchema(readSnapshot, writeSchema, evolutionMode);

        ValidatePartitionCoverage(files, readSnapshot.Metadata.PartitionColumns);

        var actions = new List<DeltaAction>((schemaEvolution is null ? 0 : 1) + files.Count);
        if (schemaEvolution is not null)
        {
            actions.Add(schemaEvolution);
        }

        AppendAddActions(actions, files);
        return _committer.CommitAsync(readSnapshot, actions, DeltaReadScope.BlindAppend, cancellationToken);
    }

    /// <summary>
    /// Loads the latest snapshot and overwrites it with <paramref name="files"/>, enforcing/evolving the
    /// incoming <paramref name="writeSchema"/> against the table schema (convenience). Schema declaration is
    /// mandatory (there is no no-schema overload — that would bypass enforcement); pass the table's own
    /// schema to express "this write conforms to the current schema".
    /// </summary>
    /// <exception cref="DeltaSchemaMismatchException">The write is incompatible with the table schema, or
    /// requires a schema change <paramref name="evolutionMode"/> does not permit.</exception>
    public async Task<DeltaCommitResult> OverwriteAsync(
        StructType writeSchema,
        IReadOnlyList<StagedDataFile> files,
        PartitionOverwriteMode partitionMode = PartitionOverwriteMode.Static,
        SchemaEvolutionMode evolutionMode = SchemaEvolutionMode.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        Snapshot readSnapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
        return await OverwriteAsync(
            readSnapshot, writeSchema, files, partitionMode, evolutionMode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Overwrites <paramref name="readSnapshot"/> with <paramref name="files"/> after enforcing/evolving the
    /// incoming <paramref name="writeSchema"/> against the table schema (STORY-05.4.2 AC1/AC2). Schema
    /// enforcement runs <b>before</b> any action is built or committed, so a rejected write leaves the table
    /// unchanged; an allowed evolution commits the merged schema as a <c>metaData</c> action in the
    /// <b>same</b> version as the removes and new adds (atomic evolution). The partition-overwrite semantics
    /// and read scopes are exactly those of the base overload (<see cref="PartitionOverwriteMode.Static"/> ⇒
    /// full overwrite under <see cref="DeltaReadScope.WholeTable"/>; <see cref="PartitionOverwriteMode.Dynamic"/>
    /// ⇒ touched-partition replacement under a <see cref="DeltaReadScope.ReadFiles(IEnumerable{string})"/>
    /// scope, routed to the full-overwrite path for an unpartitioned table).
    /// </summary>
    /// <exception cref="DeltaSchemaMismatchException">The write is incompatible with the table schema, or
    /// requires a schema change <paramref name="evolutionMode"/> does not permit.</exception>
    public Task<DeltaCommitResult> OverwriteAsync(
        Snapshot readSnapshot,
        StructType writeSchema,
        IReadOnlyList<StagedDataFile> files,
        PartitionOverwriteMode partitionMode = PartitionOverwriteMode.Static,
        SchemaEvolutionMode evolutionMode = SchemaEvolutionMode.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            // An empty overwrite (truncate) is a distinct operation; STORY-05.3.3 overwrites with new data.
            throw new ArgumentException("An overwrite must stage at least one data file.", nameof(files));
        }

        MetadataAction? schemaEvolution = ReconcileSchema(readSnapshot, writeSchema, evolutionMode);

        ValidatePartitionCoverage(files, readSnapshot.Metadata.PartitionColumns);

        return partitionMode switch
        {
            PartitionOverwriteMode.Static =>
                FullOverwriteAsync(readSnapshot, files, schemaEvolution, cancellationToken),
            PartitionOverwriteMode.Dynamic =>
                DynamicPartitionOverwriteAsync(readSnapshot, files, schemaEvolution, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(partitionMode), partitionMode, "Unknown partition overwrite mode."),
        };
    }

    /// <summary>
    /// The write-door entry (#487): appends <paramref name="files"/> to the latest snapshot, or — when the
    /// table does not yet exist — <b>creates</b> it, committing the <c>protocol</c> + <c>metaData</c> (the
    /// declared <paramref name="writeSchema"/> and <paramref name="partitionColumns"/>) together with the new
    /// <c>add</c>s as version 0 in a single atomic commit (Spark's first-write-creates-the-table semantics).
    /// An empty write (no files) against a fresh path still creates the (empty) table; against an existing
    /// table it is a no-op (append adds nothing).
    /// </summary>
    /// <exception cref="DeltaSchemaMismatchException">An append to an existing table is incompatible with its
    /// schema (schema evolution is <see cref="SchemaEvolutionMode.None"/> here — evolution is #495/#496).</exception>
    public async Task<DeltaCommitResult> CreateOrAppendAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<StagedDataFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(files);

        long? latest = await _log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return await CreateTableAsync(writeSchema, partitionColumns, files, cancellationToken).ConfigureAwait(false);
        }

        Snapshot readSnapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
        if (files.Count == 0)
        {
            // Append of nothing to an existing table: no new version, report the current one.
            return new DeltaCommitResult(readSnapshot.Version, Attempts: 0, Skipped: true);
        }

        return await AppendAsync(readSnapshot, writeSchema, files, SchemaEvolutionMode.None, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The write-door overwrite entry (#487): overwrites the latest snapshot with <paramref name="files"/>
    /// per <paramref name="partitionMode"/>, or — when the table does not yet exist — <b>creates</b> it
    /// exactly as <see cref="CreateOrAppendAsync"/> does (there is nothing to overwrite).
    ///
    /// <para><b>Empty overwrite (Spark parity).</b> An overwrite REPLACES prior data, so an empty write is
    /// NOT a no-op the way an empty append is. The behavior is mode-aware, matching Spark's
    /// <c>df.write.mode("overwrite").save()</c> of an empty DataFrame:</para>
    /// <list type="bullet">
    /// <item><b>Static overwrite + empty + existing table</b> ⇒ <b>TRUNCATE</b>: every prior active file is
    /// removed in a new atomic version that adds 0 files, so a subsequent read is empty (a static overwrite
    /// is a full-table replacement — replacing with nothing leaves nothing).</item>
    /// <item><b>Dynamic overwrite + empty</b> ⇒ <b>no-op</b>: dynamic overwrite replaces only the partitions
    /// the new data touches, and empty data touches no partitions, so nothing is removed and the version is
    /// unchanged (<see cref="DeltaCommitResult.Skipped"/>).</item>
    /// <item><b>Overwrite + empty + fresh path</b> (either mode) ⇒ create the schema'd <b>empty table at
    /// v0</b> (<c>protocol</c> + <c>metaData</c>, 0 adds), exactly as Spark creates an empty table on a
    /// first empty write.</item>
    /// </list>
    /// </summary>
    public async Task<DeltaCommitResult> CreateOrOverwriteAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<StagedDataFile> files,
        PartitionOverwriteMode partitionMode = PartitionOverwriteMode.Static,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(files);

        long? latest = await _log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return await CreateTableAsync(writeSchema, partitionColumns, files, cancellationToken).ConfigureAwait(false);
        }

        Snapshot readSnapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
        if (files.Count == 0)
        {
            // Spark parity: an overwrite REPLACES prior data, so an empty overwrite is NOT a no-op the way an
            // empty append is — it is mode-aware.
            if (partitionMode == PartitionOverwriteMode.Dynamic)
            {
                // Dynamic overwrite replaces only the partitions the new data touches. Empty data touches NO
                // partitions, so nothing is removed: a genuine no-op (version unchanged). Report the current
                // version.
                return new DeltaCommitResult(readSnapshot.Version, Attempts: 0, Skipped: true);
            }

            // Static overwrite of an existing table with empty data ⇒ TRUNCATE: remove EVERY prior active
            // file in one atomic version that adds 0 files, so a subsequent read is empty. Reconcile the
            // declared schema first (fail-closed, no evolution) so the truncate cannot silently accept an
            // incompatible schema; a compatible same-schema truncate carries no metaData change.
            MetadataAction? truncateEvolution =
                ReconcileSchema(readSnapshot, writeSchema, SchemaEvolutionMode.None);

            // Idempotent no-op guard: if the active set is ALREADY empty AND the schema is unchanged
            // (no metaData/schema evolution), a "truncate" would build a 0-remove/0-add/0-metadata action
            // list — which DeltaCommitter rejects as an empty commit. Spark treats an empty overwrite of an
            // already-empty table as a benign no-op, so short-circuit to Skipped (version unchanged) rather
            // than let that internal invariant escape the deterministic storage contract. A non-empty active
            // set still truncates for real below.
            if (readSnapshot.ActiveFiles.IsEmpty && truncateEvolution is null)
            {
                return new DeltaCommitResult(readSnapshot.Version, Attempts: 0, Skipped: true);
            }

            return await FullOverwriteAsync(readSnapshot, files, truncateEvolution, cancellationToken)
                .ConfigureAwait(false);
        }

        return await OverwriteAsync(readSnapshot, writeSchema, files, partitionMode, SchemaEvolutionMode.None, cancellationToken)
            .ConfigureAwait(false);
    }

    // Commits version 0 = protocol + metaData (schema + partition columns) + the new adds against a synthetic
    // empty snapshot (version -1), so a fresh path becomes a well-formed Delta table in a single atomic
    // commit. A create is append-only (no removes) so it commits under BlindAppend; a concurrent create that
    // loses the version-0 race does NOT rebase — its commit carries protocol + metaData, so the committer's
    // pre-write gates observe the winner's version-0 protocol/metadata and ABORT it via
    // ProtocolChangedException/MetadataChangedException (a second table-creation is not a blind append).
    private Task<DeltaCommitResult> CreateTableAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<StagedDataFile> files,
        CancellationToken cancellationToken)
    {
        ImmutableArray<string> partitionArray = partitionColumns.ToImmutableArray();
        ValidatePartitionCoverage(files, partitionArray);

        long createdTime = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var metadata = new MetadataAction(
            Id: new Guid(RandomNumberGenerator.GetBytes(16)).ToString(),
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", EmptyStringMap),
            SchemaString: SchemaJson.ToJson(writeSchema),
            PartitionColumns: partitionArray,
            Configuration: EmptyStringMap,
            CreatedTime: createdTime);
        var protocol = new ProtocolAction(
            ProtocolSupport.BasicReaderVersion,
            ProtocolSupport.MaxBasicWriterVersion,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty);

        var actions = new List<DeltaAction>(2 + files.Count) { protocol, metadata };
        AppendAddActions(actions, files);
        return _committer.CommitAsync(EmptySnapshot(), actions, DeltaReadScope.BlindAppend, cancellationToken);
    }

    private static readonly ImmutableSortedDictionary<string, string> EmptyStringMap =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    // A synthetic snapshot for a table that does not yet exist: version -1 (so the committer targets version
    // 0), a supported basic protocol (so the pre-write protocol gate passes), an empty schema/metadata, and
    // no active files or transactions. Its metadata/schema are never load-bearing for a create commit (the
    // create builds the real protocol+metaData actions itself); it exists only to drive the committer's
    // version arithmetic and pre-write gates.
    private static Snapshot EmptySnapshot()
    {
        var protocol = new ProtocolAction(
            ProtocolSupport.BasicReaderVersion,
            ProtocolSupport.MaxBasicWriterVersion,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty);
        var metadata = new MetadataAction(
            Id: "00000000-0000-0000-0000-000000000000",
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", EmptyStringMap),
            SchemaString: SchemaJson.ToJson(StructType.Empty),
            PartitionColumns: ImmutableArray<string>.Empty,
            Configuration: EmptyStringMap,
            CreatedTime: null);
        return new Snapshot(
            version: -1L,
            protocol,
            metadata,
            ImmutableArray<AddFileAction>.Empty,
            ImmutableArray<RemoveFileAction>.Empty,
            ImmutableSortedDictionary<string, long>.Empty.WithComparers(StringComparer.Ordinal),
            SnapshotLoadMetrics.Empty);
    }

    // Runs schema enforcement/evolution against the table's current schema BEFORE any action is built
    // (STORY-05.4.2 AC1/AC2). Returns null when the write is compatible and needs no schema change, or the
    // metaData action carrying the merged schema (all other fields copied from the current metadata) that
    // the caller commits in the SAME version as the data actions. Threads the table's partition columns so a
    // partition-column type change is rejected with a clear reason. Throws DeltaSchemaMismatchException, fail-
    // closed, if the write is incompatible or needs an evolution the mode forbids.
    private static MetadataAction? ReconcileSchema(
        Snapshot readSnapshot, StructType writeSchema, SchemaEvolutionMode evolutionMode)
    {
        IReadOnlyCollection<string>? partitionColumns = readSnapshot.Metadata.PartitionColumns.IsDefaultOrEmpty
            ? null
            : readSnapshot.Metadata.PartitionColumns;
        StructType? mergedSchema = DeltaSchemaEnforcer.Reconcile(
            readSnapshot.Schema, writeSchema, evolutionMode, partitionColumns);
        return mergedSchema is null
            ? null
            : readSnapshot.Metadata with { SchemaString = SchemaJson.ToJson(mergedSchema) };
    }

    // AC2: remove EVERY prior active file + add the new files in one atomic version, scoped WholeTable so
    // any concurrent add/remove aborts the overwrite (it depends on the entire active set). A schema
    // evolution (STORY-05.4.2) rides in the SAME action list so metadata + removes + adds publish as one
    // version.
    private Task<DeltaCommitResult> FullOverwriteAsync(
        Snapshot readSnapshot,
        IReadOnlyList<StagedDataFile> files,
        MetadataAction? schemaEvolution,
        CancellationToken cancellationToken)
    {
        long deletionTimestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var actions = new List<DeltaAction>(
            (schemaEvolution is null ? 0 : 1) + readSnapshot.ActiveFiles.Length + files.Count);
        if (schemaEvolution is not null)
        {
            actions.Add(schemaEvolution);
        }

        foreach (AddFileAction prior in readSnapshot.ActiveFiles)
        {
            actions.Add(ToRemove(prior, deletionTimestamp));
        }

        AppendAddActions(actions, files);
        return _committer.CommitAsync(readSnapshot, actions, DeltaReadScope.WholeTable, cancellationToken);
    }

    // AC3: remove only the prior active files in the touched partitions + add the new files, scoped to
    // exactly those prior files (ReadFiles) so a concurrent remove/re-add of a touched-partition file is
    // rejected while an append to an untouched partition rebases. A schema evolution (STORY-05.4.2) rides in
    // the SAME action list so metadata + removes + adds publish as one version.
    //
    // Removal selection is an EXACT partition-key match against the snapshot's active files — deliberately
    // NOT Snapshot.PruneFiles. PruneFiles is a *sound over-approximation* built for scans: it keeps any file
    // it cannot prove non-matching (e.g. one missing a partition-column key). Over-selecting is harmless for
    // a read (later filtered) but for a destructive overwrite it would tombstone a file in an UNTOUCHED
    // partition — silent, unrecoverable data loss (council #486 R1: red-team Critical / Security F1).
    // Matching each active file's canonical PartitionKey against the touched set is exact in both
    // directions: no over-select (data loss) and no under-select (stale duplicate data).
    private Task<DeltaCommitResult> DynamicPartitionOverwriteAsync(
        Snapshot readSnapshot,
        IReadOnlyList<StagedDataFile> files,
        MetadataAction? schemaEvolution,
        CancellationToken cancellationToken)
    {
        ImmutableArray<string> partitionColumns = readSnapshot.Metadata.PartitionColumns;

        // An unpartitioned table is a single partition, so a dynamic overwrite replaces the whole table —
        // identical to a static overwrite. Route it to the full-overwrite path so it also gets the stronger
        // WholeTable isolation (a concurrent append aborts), not merely the same action set.
        if (partitionColumns.IsDefaultOrEmpty)
        {
            return FullOverwriteAsync(readSnapshot, files, schemaEvolution, cancellationToken);
        }

        // The distinct partition keys the staged files touch. The SAME injective key matches prior files
        // below, so removal selection is an exact set-membership test.
        var touchedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (StagedDataFile file in files)
        {
            touchedKeys.Add(PartitionKeyBuilder.Build(file.PartitionValues, partitionColumns));
        }

        // A prior active file is removed IFF its partition key exactly equals a touched partition key.
        // Null-partition semantics: an active file MISSING a partition-column key (a malformed/foreign-
        // written add — the writer's own ValidatePartitionCoverage forbids authoring one) coerces that
        // column to null via PartitionKey, so it belongs to the null partition. It is therefore left intact
        // by any overwrite of a non-null partition, and is removed only by an overwrite that explicitly
        // targets the null partition — the same "absent value ≡ null/default partition" semantics Hive/Delta
        // use, and a bounded, deterministic behavior (never the untouched-partition data loss of #486 R1).
        var priorInTouched = new List<AddFileAction>();
        foreach (AddFileAction prior in readSnapshot.ActiveFiles)
        {
            if (touchedKeys.Contains(PartitionKeyBuilder.Build(prior.PartitionValues, partitionColumns)))
            {
                priorInTouched.Add(prior);
            }
        }

        long deletionTimestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var actions = new List<DeltaAction>(
            (schemaEvolution is null ? 0 : 1) + priorInTouched.Count + files.Count);
        if (schemaEvolution is not null)
        {
            actions.Add(schemaEvolution);
        }

        foreach (AddFileAction prior in priorInTouched)
        {
            actions.Add(ToRemove(prior, deletionTimestamp));
        }

        AppendAddActions(actions, files);

        // ReadFiles over exactly the removed priors: a concurrent remove/re-add of one aborts us; an append
        // to an untouched partition rebases. A concurrent NEW-file append into a touched partition is NOT
        // caught (it needs a partition-predicate read scope) — tracked in #488.
        DeltaReadScope scope = priorInTouched.Count == 0
            ? DeltaReadScope.BlindAppend // no prior files in the touched partitions ⇒ effectively an append.
            : DeltaReadScope.ReadFiles(priorInTouched.Select(prior => prior.Path));

        return _committer.CommitAsync(readSnapshot, actions, scope, cancellationToken);
    }

    private static void AppendAddActions(List<DeltaAction> actions, IReadOnlyList<StagedDataFile> files)
    {
        foreach (StagedDataFile file in files)
        {
            actions.Add(new AddFileAction(
                file.Path,
                file.PartitionValues,
                file.Size,
                file.ModificationTime,
                DataChange: true,
                file.Stats,
                NoTags));
        }
    }

    // Tombstone a prior active file. ExtendedFileMetadata=true round-trips partitionValues/size so the
    // remove survives checkpoint reconstruction with full fidelity (design §2.10.1).
    private static RemoveFileAction ToRemove(AddFileAction add, long deletionTimestamp) =>
        new(
            add.Path,
            DeletionTimestamp: deletionTimestamp,
            DataChange: true,
            ExtendedFileMetadata: true,
            add.PartitionValues,
            add.Size);

    // Fail-closed precondition: a partitioned write must specify a value (possibly null) for EVERY partition
    // column of each staged file, so the add lands in a well-formed partition and the exact-key remove
    // selection is unambiguous. A missing key would otherwise be silently coerced to the null partition —
    // and, for a file already in the log, is the malformed state that would make a read-oriented pruner
    // over-select it for removal (council #486 R1). For an UNPARTITIONED table the mirror invariant holds: no
    // staged file may carry ANY partition value, else a stray partition key would land in the log as an add
    // the table's (empty) partition layout does not declare — a malformed action rejected fail-closed here.
    private static void ValidatePartitionCoverage(
        IReadOnlyList<StagedDataFile> files, ImmutableArray<string> partitionColumns)
    {
        if (partitionColumns.IsDefaultOrEmpty)
        {
            foreach (StagedDataFile file in files)
            {
                if (!file.PartitionValues.IsEmpty)
                {
                    throw new DeltaStorageException(
                        StorageErrorKind.SchemaMismatch,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Staged file '{file.Path}' carries partition value(s) " +
                            $"[{string.Join(", ", file.PartitionValues.Keys)}] but the table is unpartitioned; " +
                            $"an unpartitioned write must not specify any partition value."));
                }
            }

            return;
        }

        // Validate coverage with OUR OWN ordinal sets, INDEPENDENT of each dict's own key comparer. The
        // guard must never delegate the ordinal-equality decision to the caller's `PartitionValues`
        // comparer: a StagedDataFile built with StringComparer.OrdinalIgnoreCase would let a case-variant
        // key (e.g. "REGION" for a declared "region") satisfy ContainsKey("region") AND keep an exact Count,
        // silently authoring an `add` whose partitionValues keys do not ordinally match the table's declared
        // partitionColumns. Building explicit Ordinal HashSets closes that case-variant bypass — for both the
        // stray-key and the missing-column checks — regardless of the incoming dictionary's comparer
        // (council #487 round-4 red-team, defense-in-depth). partitionColumns is duplicate-free table metadata.
        var declared = new HashSet<string>(partitionColumns, StringComparer.Ordinal);
        foreach (StagedDataFile file in files)
        {
            // Reject stray keys: a staged file must carry NO partition value beyond the declared columns
            // (measured ordinally), else a malformed partitionValues entry (a key the table's partition
            // layout does not declare) would commit into the _delta_log. Name the offending key(s).
            List<string> stray = file.PartitionValues.Keys
                .Where(key => !declared.Contains(key)).ToList();
            if (stray.Count > 0)
            {
                throw new DeltaStorageException(
                    StorageErrorKind.SchemaMismatch,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Staged file '{file.Path}' carries partition value(s) [{string.Join(", ", stray)}] " +
                        $"not declared by the table's partition columns [{string.Join(", ", partitionColumns)}]; " +
                        $"a partitioned write must not specify any partition value beyond the declared columns."));
            }

            // Reject missing columns: every declared column must be ordinally present among the file's keys.
            var fileKeys = new HashSet<string>(file.PartitionValues.Keys, StringComparer.Ordinal);
            List<string> missing = partitionColumns
                .Where(column => !fileKeys.Contains(column)).ToList();
            if (missing.Count > 0)
            {
                throw new DeltaStorageException(
                    StorageErrorKind.SchemaMismatch,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Staged file '{file.Path}' is missing partition column(s) " +
                        $"[{string.Join(", ", missing)}]; a partitioned write must specify a value " +
                        $"(possibly null) for every partition column."));
            }
        }
    }
}
