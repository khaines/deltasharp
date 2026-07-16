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
/// unpartitioned table), byte <see cref="Size"/>, <see cref="ModificationTime"/>, optional per-file
/// <see cref="Stats"/>, and — when the producing write-door supplies it (#497) — the
/// <see cref="DataSchema"/>: the file's <b>actual physical data schema read back from its Parquet
/// footer</b>. When present it is cross-checked against the declared write schema by
/// <see cref="DeltaTableWriter"/> so schema enforcement gates the <i>real written bytes</i>, not merely the
/// caller's declaration; a <see langword="null"/> <see cref="DataSchema"/> (a caller that does not supply
/// it) skips that cross-check.
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
///
/// <para><b>Physical write-schema validation (#497).</b> <see cref="DataSchema"/> is the file's real
/// footer-derived data schema (physical-named under column mapping). The commit-time cross-check compares
/// it by name + logical type only, because Parquet.Net models string/binary as nullable and a footer does
/// not carry Spark field metadata — neither is footer-faithful.</para>
/// </summary>
internal sealed record StagedDataFile(
    string Path,
    ImmutableSortedDictionary<string, string?> PartitionValues,
    long Size,
    long ModificationTime,
    FileStatistics? Stats = null,
    StructType? DataSchema = null);

/// <summary>
/// The resolved PHYSICAL write shape for an append/overwrite, computed <b>once</b> so the write door
/// (<c>DeltaWriteTarget</c>) can stage the Parquet data files under the exact physical column names the
/// commit will record — closing the door↔committer double-mint gap (#556). Under column-mapping name mode a
/// schema-evolving write (an additive column, an applied widening, or a wholesale <c>overwriteSchema</c>
/// replacement) MINTS a fresh <c>physicalName</c>+<c>id</c> for each new column; that minting must happen
/// exactly once, because the physical names chosen at staging time MUST equal the ones recorded in the
/// committed <c>metaData</c> (a second, independent mint would stage bytes under one <c>col-&lt;uuid&gt;</c>
/// while the log records another — silently unreadable data). The door obtains this plan from
/// <see cref="DeltaTableWriter.PlanAppend"/> / <see cref="DeltaTableWriter.PlanOverwriteReplaceSchema"/>,
/// stages under <see cref="PhysicalWriteSchema"/> / <see cref="PhysicalPartitionColumns"/>, then commits it
/// through <see cref="DeltaTableWriter.CommitAppendAsync"/> /
/// <see cref="DeltaTableWriter.CommitOverwriteReplaceSchemaAsync"/> — which re-uses the SAME
/// <see cref="SchemaChange"/> mapping rather than re-resolving it.
/// </summary>
/// <param name="PhysicalWriteSchema">The physical (name-mapped) schema the staged Parquet files must carry,
/// in write order; equals the logical write schema for a <c>none</c>-mode table.</param>
/// <param name="PhysicalPartitionColumns">The physical partition-column names keying the staged files'
/// <c>partitionValues</c>; equal to the logical names for a <c>none</c>-mode table.</param>
/// <param name="SchemaChange">The schema-change <c>metaData</c> to commit (an additive/widening evolution, or
/// a wholesale <c>overwriteSchema</c> replacement carrying the reconciled mapping + bumped
/// <c>maxColumnId</c>), or <see langword="null"/> when the write conforms to the current schema.</param>
internal readonly record struct DeltaWritePlan(
    StructType PhysicalWriteSchema,
    ImmutableArray<string> PhysicalPartitionColumns,
    MetadataAction? SchemaChange);

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
    private readonly IColumnPhysicalNameSource _nameSource;

    /// <summary>Creates a writer over <paramref name="backend"/> (constructs its own log reader + committer).</summary>
    public DeltaTableWriter(IStorageBackend backend)
        : this(new DeltaLog(backend), new DeltaCommitter(backend), TimeProvider.System)
    {
    }

    /// <summary>Creates a writer over an explicit reader + committer (tests inject a committer with a
    /// race probe / bounded retries, and a deterministic clock for tombstone timestamps). An explicit
    /// <paramref name="nameSource"/> supplies deterministic <c>col-&lt;uuid&gt;</c> physical names when a
    /// name-mode append/overwrite mints a new column (#541); production defaults to the crypto RNG source.</summary>
    internal DeltaTableWriter(
        DeltaLog log,
        DeltaCommitter committer,
        TimeProvider? timeProvider = null,
        IColumnPhysicalNameSource? nameSource = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(committer);
        _log = log;
        _committer = committer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nameSource = nameSource ?? RandomPhysicalNameSource.Instance;
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
    /// evolution). An existing column's type is changed only by an <b>applied Delta-sanctioned type widening</b>
    /// (#495) — and only when the table enables it (the <c>typeWidening</c> table feature is present AND
    /// <c>delta.enableTypeWidening=true</c>); otherwise a would-be widening stays fail-closed
    /// (<see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/>). Commits
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

        // #525/#541/#556: resolve the physical write shape (minting a name-mode additive/widening column
        // ONCE) and commit it. The plan/commit split is what lets the public write door
        // (DeltaWriteTarget) stage under the SAME minted physical names the commit records — this
        // convenience overload keeps the pre-#556 one-shot behavior.
        DeltaWritePlan plan = PlanAppend(readSnapshot, writeSchema, evolutionMode);
        return CommitAppendAsync(readSnapshot, plan, files, cancellationToken);
    }

    /// <summary>
    /// Resolves — but does NOT commit — the PHYSICAL append shape (<see cref="DeltaWritePlan"/>) for
    /// <paramref name="writeSchema"/> against <paramref name="readSnapshot"/>, minting a name-mode additive
    /// column / applied widening EXACTLY ONCE (#556). The write door calls this before staging so the Parquet
    /// files land under the physical names the matching <see cref="CommitAppendAsync"/> will record; a second
    /// independent mint (door vs. committer) is what this seam eliminates.
    /// </summary>
    /// <exception cref="DeltaSchemaMismatchException">The write is incompatible with the table schema, or
    /// requires a schema change <paramref name="evolutionMode"/> does not permit.</exception>
    internal DeltaWritePlan PlanAppend(
        Snapshot readSnapshot, StructType writeSchema, SchemaEvolutionMode evolutionMode)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(writeSchema);

        // #525/#541: an append to a column-mapped table stages Parquet under the table's PHYSICAL column
        // names (name mode). A same-logical-schema write reuses the existing mapping (#525); an additive
        // column / applied widening MINTS a fresh physicalName+id and bumps maxColumnId (#541). `none` is
        // logical==physical; `id` write stays fail-closed (#523) via EnsureWriteSupported below.
        ColumnMappingMode mode = ColumnMapping.ResolveMode(readSnapshot.Metadata.Configuration);
        ColumnMapping.EnsureWriteSupported(mode);
        (StructType physicalWriteSchema, ImmutableArray<string> physicalPartitionColumns,
            MetadataAction? schemaEvolution) = ResolveWrite(readSnapshot, writeSchema, evolutionMode, mode);
        return new DeltaWritePlan(physicalWriteSchema, physicalPartitionColumns, schemaEvolution);
    }

    /// <summary>
    /// Commits a previously-resolved append <paramref name="plan"/> (from <see cref="PlanAppend"/>) plus its
    /// staged <paramref name="files"/> against <paramref name="readSnapshot"/> — the second half of the
    /// #556 plan/commit split. Gates the staged bytes against the plan's physical schema/partitions
    /// (fail-closed BEFORE any action is built), then publishes the optional schema-evolution
    /// <c>metaData</c> atomically with the new adds under <see cref="DeltaReadScope.BlindAppend"/>.
    /// </summary>
    internal Task<DeltaCommitResult> CommitAppendAsync(
        Snapshot readSnapshot,
        DeltaWritePlan plan,
        IReadOnlyList<StagedDataFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException("An append must stage at least one data file.", nameof(files));
        }

        ValidateStagedWriteSchema(plan.PhysicalWriteSchema, plan.PhysicalPartitionColumns, files);
        ValidatePartitionCoverage(files, plan.PhysicalPartitionColumns);

        var actions = new List<DeltaAction>((plan.SchemaChange is null ? 0 : 1) + files.Count);
        if (plan.SchemaChange is not null)
        {
            actions.Add(plan.SchemaChange);
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

        // #525/#541: mirror AppendAsync — stage under PHYSICAL names for a name-mode table, minting a fresh
        // physicalName+id (and bumping maxColumnId) for an additive column / applied widening; `none` is
        // logical==physical; `id` stays fail-closed (#523). Physical partition columns key both the staged
        // files' partitionValues and the dynamic-overwrite removal selection.
        ColumnMappingMode mode = ColumnMapping.ResolveMode(readSnapshot.Metadata.Configuration);
        ColumnMapping.EnsureWriteSupported(mode);
        (StructType physicalWriteSchema, ImmutableArray<string> physicalPartitionColumns,
            MetadataAction? schemaEvolution) = ResolveWrite(readSnapshot, writeSchema, evolutionMode, mode);

        ValidateStagedWriteSchema(physicalWriteSchema, physicalPartitionColumns, files);
        ValidatePartitionCoverage(files, physicalPartitionColumns);

        return partitionMode switch
        {
            PartitionOverwriteMode.Static =>
                FullOverwriteAsync(readSnapshot, files, schemaEvolution, cancellationToken),
            PartitionOverwriteMode.Dynamic =>
                DynamicPartitionOverwriteAsync(
                    readSnapshot, files, physicalPartitionColumns, schemaEvolution, cancellationToken),
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
            // Append of nothing to an existing table: no new version, report the current one. (No staging, so
            // there is no logical→physical mapping concern for a name-mode table; AppendAsync handles the
            // physical staging for a non-empty append.)
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
        bool overwriteSchema = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(files);

        // #496: overwriteSchema (destructive wholesale replacement) is legal ONLY for a Static/full overwrite,
        // because a full overwrite rewrites every file. Under Dynamic partition overwrite, files in the
        // UNTOUCHED partitions survive and still conform to the OLD schema, so replacing the schema wholesale
        // would leave them unreadable under the new schema — silent corruption. Reject the combination fail-
        // closed (Spark raises an AnalysisException for overwriteSchema with dynamic partitionOverwriteMode).
        if (overwriteSchema && partitionMode == PartitionOverwriteMode.Dynamic)
        {
            throw new ArgumentException(
                "overwriteSchema is only supported for a full (Static) overwrite: a dynamic partition "
                + "overwrite preserves files in untouched partitions that still conform to the old schema, so "
                + "a wholesale schema replacement would leave them unreadable. Use a Static (full) overwrite to "
                + "replace the schema.",
                nameof(overwriteSchema));
        }

        long? latest = await _log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            // A fresh table's create sets the schema outright, so overwriteSchema is moot (there is nothing to
            // replace); the write's declared schema/partitioning becomes version 0.
            return await CreateTableAsync(writeSchema, partitionColumns, files, cancellationToken).ConfigureAwait(false);
        }

        Snapshot readSnapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);

        // #542: overwriteSchema (wholesale schema replacement, #496) is now supported for a NAME-mode
        // column-mapped table — OverwriteReplaceSchemaAsync reconciles the columnMapping config with the
        // replaced schema (surviving columns keep their id+physicalName, new columns mint fresh identity,
        // maxColumnId bumps monotonically). `id` mode is rejected fail-closed by the centralized id-write gate
        // (DeltaCommitter.CommitAsync) and by EnsureWriteSupported at the append/overwrite entry, so it never
        // reaches here; `none` is unaffected (logical==physical).
        if (overwriteSchema)
        {
            return await OverwriteReplaceSchemaAsync(
                readSnapshot, writeSchema, partitionColumns, files, cancellationToken).ConfigureAwait(false);
        }

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

    // Creates a fresh (non-column-mapped) table: basic protocol (reader 1 / writer 2), empty configuration.
    // Delegates the version-0 atomic commit to CreateTableCoreAsync.
    private Task<DeltaCommitResult> CreateTableAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<StagedDataFile> files,
        CancellationToken cancellationToken)
    {
        // #497: a non-mapped create is logical==physical, so gate the version-0 metaData schema on the real
        // staged bytes too (defense-in-depth: the recorded schema must match the files it describes).
        ValidateStagedWriteSchema(writeSchema, partitionColumns, files);

        var protocol = new ProtocolAction(
            ProtocolSupport.BasicReaderVersion,
            ProtocolSupport.MaxBasicWriterVersion,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty);
        return CreateTableCoreAsync(
            writeSchema, partitionColumns, partitionColumns, EmptyStringMap, protocol, files, cancellationToken);
    }

    /// <summary>
    /// Creates a fresh Delta table with an explicit <paramref name="configuration"/> and
    /// <paramref name="protocol"/> — the enablement path for column mapping (STORY-05.4.3 / #191). The
    /// <paramref name="writeSchema"/> is the mapped LOGICAL schema (each field carrying its
    /// <c>delta.columnMapping.id</c> / <c>delta.columnMapping.physicalName</c>);
    /// <paramref name="logicalPartitionColumns"/> are the LOGICAL partition-column names recorded in
    /// <c>metaData.partitionColumns</c> (HIGH#1 / Spark golden <c>dv-with-columnmapping</c>); and
    /// <paramref name="physicalPartitionColumns"/> are the PHYSICAL partition-column names the staged
    /// <paramref name="files"/> key their <c>partitionValues</c> by (Delta protocol writer requirement), used
    /// to validate partition coverage. Enablement is scoped to a fresh table so every data file is written
    /// under physical names from version 0 — read-through is guaranteed without rewriting existing data.
    /// </summary>
    internal Task<DeltaCommitResult> CreateMappedTableAsync(
        StructType writeSchema,
        IReadOnlyList<string> logicalPartitionColumns,
        IReadOnlyList<string> physicalPartitionColumns,
        ImmutableSortedDictionary<string, string> configuration,
        ProtocolAction protocol,
        IReadOnlyList<StagedDataFile> files,
        CancellationToken cancellationToken,
        bool validatePhysicalWriteSchema = false)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(logicalPartitionColumns);
        ArgumentNullException.ThrowIfNull(physicalPartitionColumns);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(files);

        // #497: gate the version-0 metaData schema on the real staged bytes when the caller is
        // logical==physical (the deletion-vector create path passes true; the actual column-mapping name-mode
        // create passes false because its footer schema is physical-named while writeSchema is logical —
        // physical≠logical staged-schema validation is deferred to #525).
        if (validatePhysicalWriteSchema)
        {
            ValidateStagedWriteSchema(writeSchema, physicalPartitionColumns, files);
        }

        return CreateTableCoreAsync(
            writeSchema, logicalPartitionColumns, physicalPartitionColumns, configuration, protocol, files, cancellationToken);
    }

    // Commits version 0 = protocol + metaData (schema + partition columns + configuration) + the new adds
    // against a synthetic empty snapshot (version -1), so a fresh path becomes a well-formed Delta table in a
    // single atomic commit. A create is append-only (no removes) so it commits under BlindAppend; a
    // concurrent create that loses the version-0 race does NOT rebase — its commit carries protocol +
    // metaData, so the committer's pre-write gates observe the winner's version-0 protocol/metadata and ABORT
    // it via ProtocolChangedException/MetadataChangedException (a second table-creation is not a blind
    // append). metaData.partitionColumns records the LOGICAL (metadataPartitionColumns) names, while coverage
    // is validated against the staged files' actual partition-value keys (validationPartitionColumns) — the
    // two differ under column mapping name mode (logical vs. physical); they are identical otherwise.
    private Task<DeltaCommitResult> CreateTableCoreAsync(
        StructType writeSchema,
        IReadOnlyList<string> metadataPartitionColumns,
        IReadOnlyList<string> validationPartitionColumns,
        ImmutableSortedDictionary<string, string> configuration,
        ProtocolAction protocol,
        IReadOnlyList<StagedDataFile> files,
        CancellationToken cancellationToken)
    {
        ImmutableArray<string> metadataPartitionArray = metadataPartitionColumns.ToImmutableArray();
        ValidatePartitionCoverage(files, validationPartitionColumns.ToImmutableArray());

        long createdTime = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var metadata = new MetadataAction(
            Id: new Guid(RandomNumberGenerator.GetBytes(16)).ToString(),
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", EmptyStringMap),
            SchemaString: SchemaJson.ToJson(writeSchema),
            PartitionColumns: metadataPartitionArray,
            Configuration: configuration,
            CreatedTime: createdTime);

        var actions = new List<DeltaAction>(2 + files.Count) { protocol, metadata };
        AppendAddActions(actions, files);
        return _committer.CommitAsync(EmptySnapshot(), actions, DeltaReadScope.BlindAppend, cancellationToken);
    }

    /// <summary>
    /// Renames a column in a name-mode column-mapping table (STORY-05.4.3 AC1) — a <b>metadata-only</b>
    /// operation. The field's <c>delta.columnMapping.physicalName</c> and <c>id</c> are unchanged (so no data
    /// file is rewritten and existing rows read through under the new logical name); only the logical/display
    /// name changes. Commits a lone <c>metaData</c> action (all other metadata fields copied) under
    /// <see cref="DeltaReadScope.WholeTable"/>, so any concurrent commit aborts the rename (a schema change
    /// needs a fresh snapshot).
    /// </summary>
    /// <exception cref="InvalidOperationException">The table does not use column mapping <c>name</c> mode, the
    /// source column is absent, or the target name collides with an existing column.</exception>
    internal async Task<DeltaCommitResult> RenameColumnAsync(
        string fromName, string toName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fromName);
        ArgumentException.ThrowIfNullOrEmpty(toName);
        Snapshot snapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
        RequireNameMode(snapshot);

        StructType schema = snapshot.Schema;
        if (!schema.TryGetField(fromName, out StructField target))
        {
            throw new InvalidOperationException(
                $"Cannot rename column '{fromName}': no such column in the table schema.");
        }

        if (!string.Equals(fromName, toName, StringComparison.Ordinal) && schema.IndexOf(toName) >= 0)
        {
            throw new InvalidOperationException(
                $"Cannot rename column '{fromName}' to '{toName}': a column named '{toName}' already exists.");
        }

        var fields = new List<StructField>(schema.Count);
        foreach (StructField field in schema)
        {
            // ReferenceEquals invariant: TryGetField/schema enumeration return the SAME StructField instance
            // for the matched column, so identity comparison uniquely selects the target (no name re-match).
            fields.Add(
                ReferenceEquals(field, target)
                    ? new StructField(toName, field.DataType, field.Nullable, field.Metadata)
                    : field);
        }

        // MEDIUM#4: metaData.partitionColumns holds LOGICAL names (HIGH#1), so renaming a PARTITION column
        // must update its logical entry there too. physicalName/id are unchanged, and add.partitionValues
        // stay keyed by physical name, so existing data files still resolve — this is metadata-only.
        ImmutableArray<string>? updatedPartitions = null;
        if (snapshot.Metadata.PartitionColumns.Contains(fromName, StringComparer.Ordinal))
        {
            updatedPartitions = snapshot.Metadata.PartitionColumns
                .Select(p => string.Equals(p, fromName, StringComparison.Ordinal) ? toName : p)
                .ToImmutableArray();
        }

        return await CommitSchemaChangeAsync(snapshot, new StructType(fields), updatedPartitions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Drops a column from a name-mode column-mapping table (STORY-05.4.3 AC2) — a <b>logical-only</b>
    /// operation. The field is removed from the LOGICAL schema; the physical column remains unreferenced in
    /// existing data files (no rewrite), and <c>delta.columnMapping.maxColumnId</c> is unchanged (a dropped
    /// id is never reused). Old snapshots (time travel) still expose the dropped column and its data per their
    /// version. Commits a lone <c>metaData</c> action under <see cref="DeltaReadScope.WholeTable"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The table does not use column mapping <c>name</c> mode, the
    /// column is absent, or dropping it would be a partition column (out of scope here).</exception>
    internal async Task<DeltaCommitResult> DropColumnAsync(
        string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Snapshot snapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
        RequireNameMode(snapshot);

        StructType schema = snapshot.Schema;
        if (!schema.TryGetField(name, out StructField target))
        {
            throw new InvalidOperationException(
                $"Cannot drop column '{name}': no such column in the table schema.");
        }

        // MEDIUM#5: the partition-column guard checks the LOGICAL name against metaData.partitionColumns
        // (which holds LOGICAL names under name mode — HIGH#1), not the physical name.
        if (snapshot.Metadata.PartitionColumns.Contains(name, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot drop partition column '{name}'; dropping a partition column is out of scope.");
        }

        var fields = new List<StructField>(schema.Count - 1);
        foreach (StructField field in schema)
        {
            // ReferenceEquals invariant: TryGetField returns the SAME StructField instance as the matched
            // column, so identity comparison uniquely excludes exactly the target (no name re-match).
            if (!ReferenceEquals(field, target))
            {
                fields.Add(field);
            }
        }

        return await CommitSchemaChangeAsync(snapshot, new StructType(fields), partitionColumns: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Enables type widening on an EXISTING table (#534): commits a <b>metadata-only</b> <c>protocol</c> +
    /// <c>metaData</c> upgrade that adds the <c>typeWidening</c> table feature (reader → v3 / writer → v7,
    /// preserving every existing feature) and sets <c>delta.enableTypeWidening=true</c>, so a subsequent
    /// <see cref="AppendAsync(Snapshot, StructType, IReadOnlyList{StagedDataFile}, SchemaEvolutionMode, CancellationToken)"/>
    /// with a wider-typed column may apply a Delta-sanctioned widening (before this, such a write fails closed
    /// as <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/>). No data file is written or removed —
    /// every pre-widening file stays active and is promoted at read time. Commits a lone <c>protocol</c> + a
    /// lone <c>metaData</c> action under <see cref="DeltaReadScope.WholeTable"/>, so any concurrent commit
    /// aborts (a protocol/metadata change must rebase on a fresh snapshot). Idempotent: if the table already
    /// supports AND enables type widening, no version is written and the current version is reported.
    /// </summary>
    internal async Task<DeltaCommitResult> EnableTypeWideningAsync(CancellationToken cancellationToken = default)
    {
        Snapshot snapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);

        // Idempotent: the table already declares the feature AND sets the property → nothing to do.
        if (TypeWideningFeature.IsWriteEnabled(snapshot))
        {
            return new DeltaCommitResult(snapshot.Version, Attempts: 0, Skipped: true);
        }

        // Interop safety: refuse fail-closed rather than silently deactivate a legacy (writer < 7) invariant /
        // CHECK constraint this build cannot yet carry as an explicit table feature through the table-features
        // upgrade (#568, overlaps #190). An active delta.appendOnly=true no longer blocks the upgrade — it is
        // enumerated + enforced (#549).
        TypeWideningFeature.EnsureUpgradeable(snapshot.Protocol, snapshot.Schema, snapshot.Metadata.Configuration);

        // The upgraded protocol (adds typeWidening, enumerates an active appendOnly, preserving existing
        // features + raising the version floors) and the metaData carrying delta.enableTypeWidening=true. Both
        // are needed: the protocol makes the feature SUPPORTED, the property makes a widening APPLIED (Delta
        // "Writer Requirements for Type Widening"). The committer re-validates any installed protocol
        // (EnsureWritable/EnsureReadable), so it never publishes a protocol this build could not itself read
        // or write back.
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            snapshot.Protocol, snapshot.Metadata.Configuration);
        ImmutableSortedDictionary<string, string> configuration =
            snapshot.Metadata.Configuration.SetItem(TypeWideningFeature.EnablePropertyKey, "true");
        MetadataAction metadata = snapshot.Metadata with { Configuration = configuration };

        return await _committer.CommitAsync(
            snapshot, new DeltaAction[] { upgraded, metadata }, DeltaReadScope.WholeTable, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void RequireNameMode(Snapshot snapshot)
    {
        ColumnMappingMode mode = ColumnMapping.ResolveMode(snapshot.Metadata.Configuration);
        if (mode != ColumnMappingMode.Name)
        {
            throw new InvalidOperationException(
                "Column rename/drop as a metadata-only operation requires column mapping 'name' mode; "
                + $"the table's mode is '{mode}'.");
        }
    }

    // #525/#541: resolves the PHYSICAL staging shape + the optional schema-evolution metaData for an
    // append/overwrite, handling column mapping. It first reconciles the incoming write schema against the
    // table schema (an additive column / applied widening yields a merged schema; a compatible write yields
    // null). Then, by mode:
    //   * `none` (logical==physical): stage under the write schema; the evolution metaData (if any) carries
    //     the merged LOGICAL schema — behavior byte-for-byte unchanged from the pre-#541 path.
    //   * `name`, no evolution (#525): stage under the table's EXISTING physical mapping (never re-mint).
    //   * `name`, evolution (#541): MINT a fresh physicalName+id for each new column (an applied widening
    //     keeps its identity), bump maxColumnId, stage the write columns (existing + new) under the EVOLVED
    //     mapping, and re-emit the mapped metaData (mapped schema + bumped maxColumnId config).
    // `id` mode is rejected fail-closed by EnsureWriteSupported (#523) at the commit-path entry and never reaches here.
    private (StructType PhysicalWriteSchema, ImmutableArray<string> PhysicalPartitionColumns,
        MetadataAction? SchemaEvolution)
        ResolveWrite(
            Snapshot readSnapshot, StructType writeSchema, SchemaEvolutionMode evolutionMode, ColumnMappingMode mode)
    {
        ImmutableArray<string> logicalPartitions = readSnapshot.Metadata.PartitionColumns.IsDefault
            ? ImmutableArray<string>.Empty
            : readSnapshot.Metadata.PartitionColumns;

        StructType? mergedSchema = MergeSchema(readSnapshot, writeSchema, evolutionMode);

        if (mode != ColumnMappingMode.Name)
        {
            // none (and any non-name) mode: logical==physical, so the caller's write schema and the table's
            // logical partition columns ARE the physical staging shape; the evolution (if any) carries the
            // merged logical schema — byte-for-byte unchanged behavior.
            return (writeSchema, logicalPartitions, LogicalEvolution(readSnapshot.Metadata, mergedSchema));
        }

        if (mergedSchema is null)
        {
            // name mode, compatible (same-logical-schema) write (#525): reuse the existing physical mapping,
            // never re-mint — maxColumnId and every existing column's identity are unchanged.
            StructType physical = ColumnMapping.MapWriteSchemaToPhysical(writeSchema, readSnapshot.Schema, mode);
            ImmutableArray<string> physicalParts =
                ColumnMapping.PhysicalPartitionColumns(readSnapshot.Schema, logicalPartitions, mode)
                    .ToImmutableArray();
            return (physical, physicalParts, null);
        }

        // name mode, schema evolution (#541): mint a fresh physicalName+id for each NEW column (an applied
        // widening keeps its identity), bump maxColumnId, and re-emit the mapped metaData. Stage the write
        // columns (existing + new) under the EVOLVED mapping so a new column lands under its minted physical
        // name; partition columns stay physical-keyed.
        (StructType mappedEvolvedSchema, ImmutableSortedDictionary<string, string> evolvedConfiguration) =
            ColumnMapping.EvolveNameModeMapping(
                mergedSchema, readSnapshot.Schema, readSnapshot.Metadata.Configuration, _nameSource);

        StructType evolvedPhysical =
            ColumnMapping.MapWriteSchemaToPhysical(writeSchema, mappedEvolvedSchema, mode);
        ImmutableArray<string> evolvedPhysicalParts =
            ColumnMapping.PhysicalPartitionColumns(mappedEvolvedSchema, logicalPartitions, mode)
                .ToImmutableArray();
        MetadataAction evolution = readSnapshot.Metadata with
        {
            SchemaString = SchemaJson.ToJson(mappedEvolvedSchema),
            Configuration = evolvedConfiguration,
        };
        return (evolvedPhysical, evolvedPhysicalParts, evolution);
    }

    // Merges the incoming write schema against the table's current schema (an additive nullable column, or an
    // applied Delta type widening), returning the merged LOGICAL schema, or null when the write is compatible
    // and needs no schema change. Type widening is applied only when the table enabled it (`typeWidening`
    // feature + `delta.enableTypeWidening`); otherwise a would-be widening stays fail-closed. Threads the
    // partition columns so a partition-column type change is rejected with a clear reason. Throws
    // DeltaSchemaMismatchException, fail-closed, if the write is incompatible or needs an evolution the mode
    // forbids.
    private static StructType? MergeSchema(
        Snapshot readSnapshot, StructType writeSchema, SchemaEvolutionMode evolutionMode)
    {
        IReadOnlyCollection<string>? partitionColumns = readSnapshot.Metadata.PartitionColumns.IsDefaultOrEmpty
            ? null
            : readSnapshot.Metadata.PartitionColumns;
        bool typeWideningEnabled = TypeWideningFeature.IsWriteEnabled(readSnapshot);
        return DeltaSchemaEnforcer.Reconcile(
            readSnapshot.Schema, writeSchema, evolutionMode, partitionColumns, typeWideningEnabled);
    }

    private Task<DeltaCommitResult> CommitSchemaChangeAsync(
        Snapshot snapshot, StructType newSchema, ImmutableArray<string>? partitionColumns, CancellationToken cancellationToken)
    {
        MetadataAction metadata = snapshot.Metadata with { SchemaString = SchemaJson.ToJson(newSchema) };
        if (partitionColumns is { } updated)
        {
            metadata = metadata with { PartitionColumns = updated };
        }

        return _committer.CommitAsync(
            snapshot, new DeltaAction[] { metadata }, DeltaReadScope.WholeTable, cancellationToken);
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
    // the caller commits in the SAME version as the data actions. Used by the empty-static-overwrite
    // (truncate) path, which passes SchemaEvolutionMode.None (a would-be evolution is rejected fail-closed, so
    // the returned metaData is always null there — the call is a compatibility gate). Throws
    // DeltaSchemaMismatchException, fail-closed, if the write is incompatible.
    private static MetadataAction? ReconcileSchema(
        Snapshot readSnapshot, StructType writeSchema, SchemaEvolutionMode evolutionMode)
    {
        return LogicalEvolution(readSnapshot.Metadata, MergeSchema(readSnapshot, writeSchema, evolutionMode));
    }

    // The metaData action that commits a logical-schema evolution in NONE mode (and the truncate path): the
    // merged logical schema, all other metadata fields preserved; null when there is no evolution. Name-mode
    // evolution instead re-emits a MAPPED schema + bumped maxColumnId config (ResolveWrite's #541 branch).
    private static MetadataAction? LogicalEvolution(MetadataAction metadata, StructType? mergedSchema) =>
        mergedSchema is null ? null : metadata with { SchemaString = SchemaJson.ToJson(mergedSchema) };

    // AC2: remove EVERY prior active file + add the new files in one atomic version, scoped WholeTable so
    // any concurrent add/remove aborts the overwrite (it depends on the entire active set). The optional
    // metaData change (an additive schema evolution, STORY-05.4.2; or a wholesale overwriteSchema
    // replacement, #496) rides in the SAME action list so metadata + removes + adds publish as one version.
    private Task<DeltaCommitResult> FullOverwriteAsync(
        Snapshot readSnapshot,
        IReadOnlyList<StagedDataFile> files,
        MetadataAction? metaDataToCommit,
        CancellationToken cancellationToken)
    {
        long deletionTimestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var actions = new List<DeltaAction>(
            (metaDataToCommit is null ? 0 : 1) + readSnapshot.ActiveFiles.Length + files.Count);
        if (metaDataToCommit is not null)
        {
            actions.Add(metaDataToCommit);
        }

        foreach (AddFileAction prior in readSnapshot.ActiveFiles)
        {
            actions.Add(ToRemove(prior, deletionTimestamp));
        }

        AppendAddActions(actions, files);
        return _committer.CommitAsync(readSnapshot, actions, DeltaReadScope.WholeTable, cancellationToken);
    }

    // #496/#542: a Static/full overwrite with overwriteSchema=true REPLACES the table schema (and partition
    // columns) wholesale — drop, narrow, reorder, add, or change a column's type — because every prior file
    // is removed in the same version, so no surviving data must conform to the old schema. This deliberately
    // BYPASSES the additive DeltaSchemaEnforcer.Reconcile (which forbids drop/narrow/type-change): the
    // destructive replacement is legal precisely because the overwrite rewrites all data. For a `none`-mode
    // table (logical==physical) the new metaData simply carries the write's declared schema (#496). For a
    // NAME-mode table (#542) the columnMapping config is RECONCILED with the replaced schema: a surviving
    // column (matched by logical name) keeps its id+physicalName, a new column mints a fresh physicalName+id,
    // maxColumnId bumps monotonically (a dropped column's id is retired, never reused), and the staged files
    // are gated against the NEW PHYSICAL schema. All other metadata fields (id, format, createdTime) are
    // preserved by `with`, so the table identity is stable. `id` mode is rejected fail-closed by the
    // centralized id-write gate (DeltaCommitter.CommitAsync) and by EnsureWriteSupported at the commit-path
    // entry, so it never reaches here. Committed under WholeTable scope (like every full
    // overwrite) so any concurrent add/remove/metaData aborts it.
    private Task<DeltaCommitResult> OverwriteReplaceSchemaAsync(
        Snapshot readSnapshot,
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<StagedDataFile> files,
        CancellationToken cancellationToken)
    {
        // One-shot convenience (the direct committer callers): resolve the replacement plan (minting a
        // name-mode new column ONCE) then commit the caller-staged files against it. The public write door
        // (#556) instead calls PlanOverwriteReplaceSchema, stages under the plan's physical names, and
        // commits with CommitOverwriteReplaceSchemaAsync — so door and committer never mint independently.
        DeltaWritePlan plan = PlanOverwriteReplaceSchema(readSnapshot, writeSchema, partitionColumns);
        return CommitOverwriteReplaceSchemaAsync(readSnapshot, plan, files, cancellationToken);
    }

    /// <summary>
    /// Resolves — but does NOT commit — the wholesale <c>overwriteSchema</c> replacement plan
    /// (<see cref="DeltaWritePlan"/>) for <paramref name="writeSchema"/> against
    /// <paramref name="readSnapshot"/> (#542/#556). Under name mode it reconciles the columnMapping onto the
    /// replaced schema — surviving columns keep their id+physicalName, a new column MINTS a fresh
    /// physicalName+id, <c>maxColumnId</c> bumps monotonically — so the write door can stage under the SAME
    /// physical names the commit records. The plan's <see cref="DeltaWritePlan.SchemaChange"/> is the
    /// replacement <c>metaData</c> (always non-null).
    /// </summary>
    internal DeltaWritePlan PlanOverwriteReplaceSchema(
        Snapshot readSnapshot, StructType writeSchema, IReadOnlyList<string> partitionColumns)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);

        ImmutableArray<string> partitionArray = partitionColumns.ToImmutableArray();
        ColumnMappingMode mode = ColumnMapping.ResolveMode(readSnapshot.Metadata.Configuration);
        ColumnMapping.EnsureWriteSupported(mode); // id-mode WRITES are refused fail-closed here (#523): id mode is readable at load but not writable

        StructType stagingSchema;
        ImmutableArray<string> stagingPartitions;
        MetadataAction replacement;
        if (mode == ColumnMappingMode.Name)
        {
            // #542: reconcile the columnMapping onto the wholesale-replaced schema (reuse-by-name / mint-new /
            // bump-maxColumnId), stage under the NEW physical names, and record LOGICAL partition columns.
            (StructType mappedNewSchema, ImmutableSortedDictionary<string, string> newConfiguration) =
                ColumnMapping.EvolveNameModeMapping(
                    writeSchema, readSnapshot.Schema, readSnapshot.Metadata.Configuration, _nameSource);
            stagingSchema = ColumnMapping.MapWriteSchemaToPhysical(writeSchema, mappedNewSchema, mode);
            stagingPartitions =
                ColumnMapping.PhysicalPartitionColumns(mappedNewSchema, partitionColumns, mode).ToImmutableArray();
            replacement = readSnapshot.Metadata with
            {
                SchemaString = SchemaJson.ToJson(mappedNewSchema),
                PartitionColumns = partitionArray,
                Configuration = newConfiguration,
            };
        }
        else
        {
            // none mode: logical==physical — the write's declared schema/partitions ARE the physical shape.
            stagingSchema = writeSchema;
            stagingPartitions = partitionArray;
            replacement = readSnapshot.Metadata with
            {
                SchemaString = SchemaJson.ToJson(writeSchema),
                PartitionColumns = partitionArray,
            };
        }

        return new DeltaWritePlan(stagingSchema, stagingPartitions, replacement);
    }

    /// <summary>
    /// Commits a previously-resolved <c>overwriteSchema</c> <paramref name="plan"/> (from
    /// <see cref="PlanOverwriteReplaceSchema"/>) plus its staged <paramref name="files"/> — the second half
    /// of the #556 plan/commit split. Gates the staged bytes against the plan's NEW physical schema/partitions
    /// (fail-closed BEFORE any action is built), then removes EVERY prior active file, adds the new files, and
    /// publishes the replacement <c>metaData</c> as one atomic version.
    /// </summary>
    internal Task<DeltaCommitResult> CommitOverwriteReplaceSchemaAsync(
        Snapshot readSnapshot,
        DeltaWritePlan plan,
        IReadOnlyList<StagedDataFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(files);

        // #542/#556: the replacement metaData is always resolved by PlanOverwriteReplaceSchema.
        MetadataAction replacement = plan.SchemaChange
            ?? throw new InvalidOperationException(
                "An overwriteSchema plan must carry a replacement metaData.");

        // Gate the real staged bytes against the NEW PHYSICAL schema (#497) and validate coverage against the
        // NEW physical partition columns — both BEFORE any action is built, so a rejected replacement leaves
        // the table unchanged (fail-closed).
        ValidateStagedWriteSchema(plan.PhysicalWriteSchema, plan.PhysicalPartitionColumns, files);
        ValidatePartitionCoverage(files, plan.PhysicalPartitionColumns);

        // Idempotent no-op: an empty overwriteSchema against an already-empty table whose (mapped) schema +
        // partition columns are unchanged would build a 0-remove/0-add/0-metadata action list (the metaData
        // equals the current one), which DeltaCommitter rejects as an empty commit — short-circuit to Skipped,
        // mirroring the plain empty-static-overwrite guard. In name mode EvolveNameModeMapping reproduces the
        // current mapping verbatim for an unchanged schema, so the SchemaString comparison still holds.
        //
        // Compare the LOGICAL partition columns on BOTH sides: `replacement.PartitionColumns` carries the
        // caller's logical partition names (PlanOverwriteReplaceSchema sets it from the write's declared
        // partitionColumns), and `metaData.partitionColumns` is logical too. Using the plan's PHYSICAL
        // partition names here would compare physical-vs-logical and never fire for a partitioned name-mode
        // table (physical `col-<uuid>` != logical name), spuriously committing a redundant metaData-only
        // version (#556 council: Architect/DeltaStorage R1).
        if (files.Count == 0
            && readSnapshot.ActiveFiles.IsEmpty
            && replacement.SchemaString == readSnapshot.Metadata.SchemaString
            && replacement.PartitionColumns.SequenceEqual(readSnapshot.Metadata.PartitionColumns))
        {
            return Task.FromResult(new DeltaCommitResult(readSnapshot.Version, Attempts: 0, Skipped: true));
        }

        // Remove EVERY prior active file + add the new files + the replacement metaData, one atomic version.
        return FullOverwriteAsync(readSnapshot, files, replacement, cancellationToken);
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
        ImmutableArray<string> partitionColumns,
        MetadataAction? schemaEvolution,
        CancellationToken cancellationToken)
    {
        // #525: partitionColumns are the PHYSICAL partition-column names (name mode) — the form BOTH the
        // staged files' partitionValues AND the prior active files' partitionValues are keyed by, so the
        // touched-key match below is exact in physical-name space. For a `none` table these ARE the logical
        // metaData partition columns (logical==physical), so behavior is unchanged.

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

    // #497: cross-check that every staged file's ACTUAL physical data schema — read back from the written
    // Parquet FOOTER by the write-door (DeltaWriteTarget.StageAsync via ParquetFileReader.ReadDataSchemaAsync)
    // and recorded on StagedDataFile.DataSchema — matches the DATA columns of the DECLARED writeSchema
    // (writeSchema minus its partition columns, in schema order: exactly the shape ColumnBatchPartitioner
    // writes and a Delta Parquet data file physically stores). Because DataSchema is derived from the real
    // bytes, this genuinely gates the written columns/types rather than re-validating the declaration against
    // itself: a staged file whose footer diverges from the committed declaration (a foreign producer, or a
    // staging-vs-commit schema mismatch) is rejected fail-closed BEFORE any action is built or committed
    // (mirroring ValidatePartitionCoverage). Comparison is by NAME + logical TYPE only — Parquet.Net models
    // string/binary as always-nullable and a footer does not carry Spark field metadata, so nullability and
    // metadata are NOT footer-faithful and must not be compared (comparing them would false-reject a valid
    // required-string write). A file whose DataSchema is null (a caller that does not supply it) is skipped —
    // the cross-check binds only when the producing write-door supplies the true written schema. The
    // `writeSchema`/`partitionColumns` passed here are the PHYSICAL forms for a name-mode table (#525/#541:
    // ResolveWrite maps the logical write columns to their physicalName — reusing the existing mapping, or the
    // freshly-minted one for an additive/widening evolution — before this call) and the logical forms for a
    // `none`-mode / non-mapped create (logical==physical). Either way the comparison is like-for-like against the file's PHYSICAL footer schema — a logical-named file staged into a physical-
    // name table (the corruption case) mismatches and is rejected fail-closed.
    private static void ValidateStagedWriteSchema(
        StructType writeSchema, IEnumerable<string> partitionColumns, IReadOnlyList<StagedDataFile> files)
    {
        var partitionSet = new HashSet<string>(partitionColumns, StringComparer.Ordinal);
        var dataFields = new List<StructField>(writeSchema.Count);
        for (int i = 0; i < writeSchema.Count; i++)
        {
            if (!partitionSet.Contains(writeSchema[i].Name))
            {
                dataFields.Add(writeSchema[i]);
            }
        }

        var expected = new StructType(dataFields);
        foreach (StagedDataFile file in files)
        {
            if (file.DataSchema is { } actual && !DataColumnsMatch(expected, actual))
            {
                throw DeltaSchemaMismatchException.PhysicalWriteSchemaMismatch(
                    file.Path, expected.SimpleString, actual.SimpleString);
            }
        }
    }

    // Structural equality of two data schemas by NAME (ordinal) + logical TYPE, in order — deliberately
    // ignoring nullability and field metadata (see ValidateStagedWriteSchema: neither is footer-faithful, so
    // comparing them against a footer-derived schema would false-reject a valid write).
    private static bool DataColumnsMatch(StructType expected, StructType actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(expected[i].Name, actual[i].Name, StringComparison.Ordinal)
                || !expected[i].DataType.Equals(actual[i].DataType))
            {
                return false;
            }
        }

        return true;
    }
}
