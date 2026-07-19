using System.Collections.Immutable;
using System.Security.Cryptography;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;

namespace DeltaSharp.Storage;

/// <summary>How an overwrite replaces prior data, mirroring Spark's
/// <c>spark.sql.sources.partitionOverwriteMode</c>. This is the PUBLIC write-facade counterpart of the
/// internal <c>PartitionOverwriteMode</c> (#487): <see cref="Static"/> replaces the whole table;
/// <see cref="Dynamic"/> replaces only the partitions the new write touches.</summary>
public enum DeltaPartitionOverwriteMode
{
    /// <summary>Full-table overwrite (the default): every prior active file is removed.</summary>
    Static,

    /// <summary>Dynamic partition overwrite: only prior files in touched partitions are removed.</summary>
    Dynamic,
}

/// <summary>The outcome of a committed Delta write: the log <see cref="Version"/> that became visible, the
/// number of Parquet data <see cref="FilesWritten"/>, and the total <see cref="RowsWritten"/>.</summary>
public readonly record struct DeltaWriteResult(long Version, int FilesWritten, long RowsWritten);

/// <summary>
/// The PUBLIC storage-side write facade the Executor's Delta sink drives (#487, STORY-05.3.3 follow-up).
/// It resolves the storage backend for a table path (local filesystem for now), stages a
/// <see cref="ColumnBatch"/> — partitioned by the declared partition columns — into Parquet data
/// file(s) under the table directory (reusing <c>ParquetFileWriter</c>, computing size/mtime/statistics),
/// and commits the write (Append or Overwrite) through the internal Delta commit engine
/// (<c>DeltaTableWriter</c>), creating the table on first write. It also exposes a cheap existence check so
/// a caller can honor Ignore/ErrorIfExists save modes before executing the query.
///
/// <para>No Core/Executor type crosses this seam: the facade takes only <see cref="StructType"/> and
/// <see cref="ColumnBatch"/> (Engine) plus partition-column names and a save-mode-neutral write shape. The
/// caller (the Executor's Delta sink) maps Spark's <c>SaveMode</c> onto <see cref="AppendAsync"/> /
/// <see cref="OverwriteAsync"/> / <see cref="TableExistsAsync"/>.</para>
///
/// <para><b>Failure atomicity.</b> Staged Parquet files are published before the log commit; if the commit
/// fails (a mode conflict, a concurrent-write abort) the staged files are never referenced by any
/// <c>add</c> action — they become orphans reclaimable by VACUUM, never a partial commit.</para>
/// </summary>
public sealed class DeltaWriteTarget : IDisposable
{
    private readonly IStorageBackend _backend;
    private readonly DeltaLog _log;
    private readonly DeltaTableWriter _writer;
    private readonly ParquetFileWriter _parquetWriter = new();
    private readonly ParquetFileReader _reader = new();
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _fileNameFactory;

    private DeltaWriteTarget(
        IStorageBackend backend,
        TimeProvider timeProvider,
        Func<string> fileNameFactory,
        IColumnPhysicalNameSource? nameSource = null)
    {
        _backend = backend;
        _log = new DeltaLog(backend);

        // Construct the committer with an optional injectable physical-name source (null ⇒ the production
        // crypto RNG). This is byte-for-byte equivalent to `new DeltaTableWriter(backend)` when nameSource is
        // null; a test injects a deterministic source so a name-mode evolution (#556) mints golden physical
        // names. TimeProvider.System matches the pre-#556 committer clock (the door's own _timeProvider drives
        // only staged-file mtime).
        _writer = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend), TimeProvider.System, nameSource);
        _timeProvider = timeProvider;
        _fileNameFactory = fileNameFactory;
    }

    /// <summary>Opens a write target over a local-filesystem table directory (created if absent).</summary>
    /// <param name="tablePath">The table root path.</param>
    /// <exception cref="ArgumentException"><paramref name="tablePath"/> is null or empty.</exception>
    public static DeltaWriteTarget ForLocalPath(string tablePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(tablePath);
        return new DeltaWriteTarget(new LocalFileSystemBackend(tablePath), TimeProvider.System, DefaultFileNameFactory);
    }

    // A deterministic test factory: injects a fixed clock (createdTime/mtime) and a reproducible data-file
    // name source so a golden column-mapping fixture is byte-for-byte stable.
    internal static DeltaWriteTarget ForLocalPath(
        string tablePath, TimeProvider timeProvider, Func<string> fileNameFactory)
    {
        ArgumentException.ThrowIfNullOrEmpty(tablePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(fileNameFactory);
        return new DeltaWriteTarget(new LocalFileSystemBackend(tablePath), timeProvider, fileNameFactory);
    }

    // A deterministic test factory that ALSO injects the committer's physical-name source, so a name-mode
    // schema-evolution / overwriteSchema-add through the door (#556) mints golden physical names. Sharing one
    // seeded source instance across the create call and this writer keeps the minted col-<uuid> sequence
    // monotonic (no collision between the create-time and evolution-time mints).
    internal static DeltaWriteTarget ForLocalPath(
        string tablePath, TimeProvider timeProvider, Func<string> fileNameFactory, IColumnPhysicalNameSource nameSource)
    {
        ArgumentException.ThrowIfNullOrEmpty(tablePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(fileNameFactory);
        ArgumentNullException.ThrowIfNull(nameSource);
        return new DeltaWriteTarget(new LocalFileSystemBackend(tablePath), timeProvider, fileNameFactory, nameSource);
    }

    // A test seam that injects a pre-built backend (e.g. a fault-injecting decorator over a real
    // LocalFileSystemBackend) so a facade behavior that depends on the backend's responses — notably the
    // fresh-vs-existing probe (GetLatestCommitVersionAsync) that decides the create-vs-append branch — can be
    // exercised deterministically. Production callers use ForLocalPath; DeltaWriteTarget's logic never depends
    // on the concrete backend type (only IStorageBackend + optional IDisposable).
    internal static DeltaWriteTarget ForBackend(
        IStorageBackend backend, TimeProvider timeProvider, Func<string> fileNameFactory)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(fileNameFactory);
        return new DeltaWriteTarget(backend, timeProvider, fileNameFactory);
    }

    // A collision-resistant data-file name from the sanctioned deterministic RNG (never the banned
    // Guid.NewGuid), so two concurrent writers never stage the same physical path (mirrors DeltaOptimize).
    private static string DefaultFileNameFactory() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    // Whether any batch carries at least one logical row — the write actually has data to stage. An
    // empty append (no batches, or only zero-row batches) is a benign no-op on an existing table.
    private static bool HasRows(IReadOnlyList<ColumnBatch> batches)
    {
        foreach (ColumnBatch batch in batches)
        {
            if (batch.LogicalRowCount > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a Delta table already exists at this path (any committed version). Used to honor
    /// Ignore/ErrorIfExists before the write query executes.</summary>
    public async Task<bool> TableExistsAsync(CancellationToken cancellationToken = default) =>
        await _log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Enforces the write's active per-row constraints (column <b>invariants</b> + named <b>CHECK</b>
    /// constraints, #581) over <paramref name="batches"/> BEFORE any Parquet file is staged. The constraint
    /// set is collected from <paramref name="constraintSnapshot"/> (the SAME snapshot the commit bases on, or
    /// <see langword="null"/> for a fresh create) unioned with any invariant declared on
    /// <paramref name="writeSchema"/> itself — so enforcement and the commit share one snapshot (no
    /// read-vs-commit TOCTOU, #596). Called from inside the write primitive so a non-sink caller of the public
    /// write door cannot bypass it.
    /// </summary>
    /// <param name="enforcer">The predicate evaluator (the executor's Delta sink); required when the write has
    /// any active constraint (else the write is refused fail-closed rather than committed unvalidated).</param>
    /// <param name="constraintSnapshot">The commit's base snapshot whose active constraints apply, or
    /// <see langword="null"/> for a fresh create (no prior table).</param>
    /// <param name="writeSchema">The write's logical schema; the batches conform to it and its fields' own
    /// <c>delta.invariants</c> are enforced too.</param>
    /// <param name="batches">The write batches whose rows are validated.</param>
    /// <param name="includeSnapshotInvariants">Whether the snapshot's own field <c>delta.invariants</c> apply.
    /// <see langword="true"/> for an append / same-schema overwrite (the table's columns and their invariants
    /// survive). <see langword="false"/> for an <c>overwriteSchema</c> replacement: the table's named CHECK
    /// constraints (<c>delta.constraints.*</c> config) survive the replacement and MUST still be enforced
    /// (Delta parity — the committed <c>metaData</c> keeps them), but the OLD schema's field invariants are
    /// replaced wholesale by the incoming <paramref name="writeSchema"/>'s, so only the latter apply.</param>
    /// <param name="resolveConstraintsWhenEmpty">Whether to run the enforcer's <b>resolvability</b> pass even
    /// when the write carries no rows. <see langword="false"/> (default) keeps an empty write a benign no-op.
    /// <see langword="true"/> is set on the <c>overwriteSchema</c>-replace path (#601): that path commits its
    /// new schema even at zero rows, so a surviving named CHECK that the replacement leaves referencing a
    /// dropped column must still be validated (a schema-metadata check over the constraint set, independent of
    /// row count) rather than skipped — otherwise the write would brick the table with a dangling CHECK.</param>
    /// <exception cref="DeltaProtocolException">A constraint is malformed, empty, or a nested-field invariant.</exception>
    /// <exception cref="InvalidOperationException">The write has active constraints but no
    /// <paramref name="enforcer"/> was provided — refused fail-closed.</exception>
    /// <exception cref="DeltaConstraintViolationException">A row does not satisfy a constraint.</exception>
    private static void EnforceWriteConstraints(
        IWriteConstraintEnforcer? enforcer,
        Snapshot? constraintSnapshot,
        StructType writeSchema,
        IReadOnlyList<ColumnBatch> batches,
        bool includeSnapshotInvariants = true,
        bool resolveConstraintsWhenEmpty = false,
        bool detectRetypedDependencies = false)
    {
        // An empty write normally carries no rows to validate, so there is nothing to enforce (and no unenforced
        // data to protect against a bypass) — skip uniformly, keeping an empty create/append/overwrite a benign
        // no-op (Spark parity; the append path also short-circuits empty). EXCEPTION (#601): an overwriteSchema
        // REPLACEMENT commits its new schema even at zero rows, so a surviving named CHECK that the replacement
        // leaves referencing a DROPPED column must STILL be validated for resolvability — the enforcer's Phase-1
        // pass runs over the constraint SET (not the rows), so it rejects a dangling CHECK independent of row
        // count. Without this a 0-row overwriteSchema that drops a constrained column would commit and brick the
        // table. resolveConstraintsWhenEmpty forces that resolve-only pass on the schema-replace path.
        if (!HasRows(batches) && !resolveConstraintsWhenEmpty)
        {
            return;
        }

        IReadOnlyList<DeltaTableConstraint> constraints =
            DeltaTableConstraints.CollectForWrite(constraintSnapshot, writeSchema, includeSnapshotInvariants);
        if (constraints.Count == 0)
        {
            return;
        }

        if (enforcer is null)
        {
            throw new InvalidOperationException(
                $"This write targets a table with {constraints.Count} active per-row constraint(s) "
                + "(column invariant / CHECK) but no constraint enforcer was supplied to the write primitive; "
                + "the write is refused fail-closed rather than committed without validating its rows. Route "
                + "the write through the executor's Delta sink (which supplies the enforcer).");
        }

        // #619: on a schema REPLACEMENT (overwriteSchema), also hand the enforcer the PRIOR schema so it can
        // run the reference-based dependent-column check — a surviving CHECK that references a column the
        // replacement RETYPES (a change that still type-resolves, which the resolvability pass alone misses) is
        // refused with the same Delta parity error, matching Delta's ALTER CHANGE COLUMN dependency block.
        StructType? priorSchema = detectRetypedDependencies ? constraintSnapshot?.Schema : null;
        enforcer.Enforce(writeSchema, constraints, batches, priorSchema);
    }

    // #596: the internal fresh-create fixture seams (name-mode / deletion-vector) construct a table with no
    // IWriteConstraintEnforcer, so — exactly like the public create door — they refuse fail-closed to CREATE a
    // table whose write schema declares a per-row constraint (a column invariant) rather than commit its rows
    // unvalidated. A no-op for the unconstrained schemas these seams are used with today; it makes the
    // no-bypass invariant STRUCTURAL (not "no test attaches an invariant") should a future path ever create a
    // constrained mapped/DV table through them.
    private static void RejectUnenforceableCreate(
        StructType writeSchema, IReadOnlyList<ColumnBatch> batches) =>
        EnforceWriteConstraints(enforcer: null, constraintSnapshot: null, writeSchema, batches);

    /// <summary>Appends <paramref name="batches"/> to the table (creating it on first write).</summary>
    /// <param name="writeSchema">The full write schema (partition + data columns).</param>
    /// <param name="partitionColumns">The partition column names, in order (a subset of the schema).</param>
    /// <param name="batches">The full-schema batches to write.</param>
    /// <param name="mergeSchema">Whether an incompatible-but-additive write may EVOLVE the schema (Spark's
    /// <c>mergeSchema</c> write option — add a new nullable column / apply a sanctioned type widening the
    /// table enables) or must strictly conform to it (<see langword="false"/>, the default). Under
    /// column-mapping name mode a new column is minted a fresh physical name ONCE and staged under it, so the
    /// door and the commit agree on the physical identity (#556).</param>
    /// <param name="enforcer">The per-row constraint enforcer (#581/#596). Enforcement runs inside this
    /// primitive against the SAME snapshot the commit bases on, after the physical write shape is resolved.
    /// It is required only when the table (or the write schema) declares an active constraint; an
    /// unconstrained write needs none. A constrained write with no enforcer is refused fail-closed.</param>
    /// <param name="cancellationToken">Cancels staging and the commit.</param>
    public async Task<DeltaWriteResult> AppendAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        bool mergeSchema = false,
        IWriteConstraintEnforcer? enforcer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);

        SchemaEvolutionMode evolutionMode = mergeSchema ? SchemaEvolutionMode.MergeSchema : SchemaEvolutionMode.None;

        if (await _log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            // Fresh path: the write CREATES a plain (none-mode) table — logical==physical, nothing to mint,
            // and evolutionMode is moot (the declared schema becomes version 0). A name-mode table is created
            // via CreateNameMappedTableAsync, not this facade path. There is no prior snapshot, so only the
            // write schema's own invariants apply.
            EnforceWriteConstraints(enforcer, constraintSnapshot: null, writeSchema, batches);
            (IReadOnlyList<StagedDataFile> createFiles, long createRows) =
                await StageAsync(writeSchema, partitionColumns, batches, cancellationToken).ConfigureAwait(false);

            // #596: commit through the snapshot-RESPECTING core with an explicit null base — so if a table was
            // created concurrently since the probe above, this create conflicts (fail-closed retry) instead of
            // silently downgrading to a blind, UNENFORCED append against that table's snapshot (which would
            // bypass any constraint it declares). Mirrors the fresh-overwrite path's CreateOrOverwriteAsync(null).
            DeltaCommitResult created = await _writer
                .CreateOrAppendAsync(readSnapshot: null, writeSchema, partitionColumns, createFiles, cancellationToken)
                .ConfigureAwait(false);
            return new DeltaWriteResult(created.Version, createFiles.Count, createRows);
        }

        // #525/#541/#556: resolve the physical staging shape against the loaded snapshot, minting a name-mode
        // additive/widening column's physicalName+id EXACTLY ONCE, then stage under those physical names and
        // commit the SAME plan — so the appended Parquet bytes and the committed metaData never disagree on a
        // column's physical identity (no independent door-vs-committer mint). For a `none`-mode table or a
        // compatible (same-schema) write this reduces to the pre-#556 staging behavior.
        Snapshot snapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);

        // An empty append to an existing table adds nothing — a benign no-op (Spark parity), mirroring
        // DeltaTableWriter.CreateOrAppendAsync (whose 0-file branch returns Skipped BEFORE any schema
        // enforcement). Short-circuit BEFORE planning so an empty append neither runs enforcement nor mints
        // (an empty write carries no rows to define a new column) — #556 council: Architect/Reliability R1.
        if (!HasRows(batches))
        {
            return new DeltaWriteResult(snapshot.Version, 0, 0L);
        }

        DeltaWritePlan plan = _writer.PlanAppend(snapshot, writeSchema, evolutionMode);

        // #596: enforce inside the primitive, against the SAME `snapshot` the commit bases on and AFTER the
        // write shape is planned (post-reconcile) — so the constraint set and the commit can never diverge
        // (no TOCTOU) and the shape validated is the shape committed. Runs before any Parquet is staged.
        EnforceWriteConstraints(enforcer, snapshot, writeSchema, batches);

        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(plan.PhysicalWriteSchema, plan.PhysicalPartitionColumns, batches, cancellationToken)
                .ConfigureAwait(false);

        DeltaCommitResult commit = await _writer
            .CommitAppendAsync(snapshot, plan, files, cancellationToken).ConfigureAwait(false);
        return new DeltaWriteResult(commit.Version, files.Count, rows);
    }

    /// <summary>Overwrites the table with <paramref name="batches"/> (creating it on first write), replacing
    /// either the whole table (<see cref="DeltaPartitionOverwriteMode.Static"/>) or only the touched
    /// partitions (<see cref="DeltaPartitionOverwriteMode.Dynamic"/>).
    /// <para>When <paramref name="overwriteSchema"/> is <see langword="true"/> (the connector's
    /// <c>overwriteSchema</c> option, #496) a full (Static) overwrite <b>replaces the table schema wholesale</b>
    /// — it may drop, narrow, reorder, add, or change the type of columns, and change the partition columns —
    /// because every prior file is rewritten. It is rejected for a Dynamic partition overwrite (files in
    /// untouched partitions would still carry the old schema). The staged files are gated against the new
    /// schema, so the committed metadata matches the real bytes.</para>
    /// <para>For a <b>column-mapped</b> (name OR id) table, this door supports an <c>overwriteSchema</c> that
    /// keeps the same columns, drops / reorders / retypes them, <b>or ADDS a new column</b> (#556/#572): the
    /// door reconciles the columnMapping (minting a new column's physical name+id ONCE), stages the Parquet
    /// files under the resulting physical names (stamping the field_id in id mode), and commits that same
    /// mapping — so the staged bytes and the committed <c>metaData</c> agree on every column's physical
    /// identity.</para></summary>
    /// <exception cref="ArgumentException"><paramref name="overwriteSchema"/> is combined with
    /// <see cref="DeltaPartitionOverwriteMode.Dynamic"/> (only a full/Static overwrite may replace the schema).</exception>
    public async Task<DeltaWriteResult> OverwriteAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        DeltaPartitionOverwriteMode partitionOverwriteMode,
        bool overwriteSchema = false,
        IWriteConstraintEnforcer? enforcer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);

        // #496: overwriteSchema (wholesale schema replacement) is legal ONLY for a Static/full overwrite — a
        // dynamic partition overwrite would leave untouched partitions conforming to the OLD schema. Reject
        // that combination up front (a pure argument check) BEFORE any snapshot load, enforcement, or staging,
        // so an illegal call fails fast with the correct ArgumentException rather than after wasted work (and
        // never surfaces a constraint error ahead of the real misuse error). CreateOrOverwriteAsync repeats
        // this guard for its direct (non-facade) callers.
        if (overwriteSchema && partitionOverwriteMode == DeltaPartitionOverwriteMode.Dynamic)
        {
            throw new ArgumentException(
                "overwriteSchema is only supported for a full (Static) overwrite: a dynamic partition "
                + "overwrite preserves files in untouched partitions that still conform to the old schema, so "
                + "a wholesale schema replacement would leave them unreadable. Use a Static (full) overwrite to "
                + "replace the schema.",
                nameof(partitionOverwriteMode));
        }

        // #556/#572: a wholesale overwriteSchema replacement on an EXISTING table (Static/full overwrite only)
        // routes through the plan/commit split so a name/id-mode ADD mints the new column's physical name+id
        // ONCE and stages under it (stamping the field_id in id mode). A fresh path and a `none`-mode
        // drop/retype/reorder keep the pre-#556 route below.
        if (overwriteSchema
            && partitionOverwriteMode == DeltaPartitionOverwriteMode.Static
            && await _log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            Snapshot snapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
            DeltaWritePlan plan = _writer.PlanOverwriteReplaceSchema(snapshot, writeSchema, partitionColumns);

            // #596: overwriteSchema replaces the schema but the committed metaData KEEPS the table's named
            // CHECK constraints (delta.constraints.* config survives — see PlanOverwriteReplaceSchema), so they
            // MUST still be enforced against the new rows (Delta parity — never commit unvalidated data into a
            // table that still declares the CHECK active). Only the OLD schema's field invariants are dropped
            // (replaced by writeSchema's), hence includeSnapshotInvariants: false. #601: resolveConstraintsWhenEmpty
            // makes the resolvability pass run even for a ZERO-ROW replacement, so a surviving CHECK that
            // references a column the replacement drops fails closed at resolution (no dangling-CHECK brick),
            // independent of row count. #619: detectRetypedDependencies hands the enforcer the PRIOR schema so a
            // surviving CHECK over a RETYPED column (a change that still type-resolves) is likewise refused with
            // the Delta parity error — closing the gap where a compatible retype would silently enforce on the
            // new type.
            EnforceWriteConstraints(
                enforcer, snapshot, writeSchema, batches, includeSnapshotInvariants: false,
                resolveConstraintsWhenEmpty: true, detectRetypedDependencies: true);

            (IReadOnlyList<StagedDataFile> replaceFiles, long replaceRows) =
                await StageAsync(plan.PhysicalWriteSchema, plan.PhysicalPartitionColumns, batches, cancellationToken)
                    .ConfigureAwait(false);
            DeltaCommitResult replaced = await _writer
                .CommitOverwriteReplaceSchemaAsync(snapshot, plan, replaceFiles, cancellationToken)
                .ConfigureAwait(false);
            return new DeltaWriteResult(replaced.Version, replaceFiles.Count, replaceRows);
        }

        // #596: load the base snapshot ONCE here (null on a fresh path) and thread it through enforcement,
        // physical staging resolution, AND the commit — so the constraints enforced and the snapshot committed
        // against can never diverge (no TOCTOU), and enforcement runs inside the primitive (no bypass).
        Snapshot? baseSnapshot =
            await _log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false) is null
                ? null
                : await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);

        // A general (non-schema-replacing) overwrite keeps the table's schema and thus ALL its active
        // constraints (CHECK + field invariants), so the base snapshot's constraints apply in full. (An
        // overwriteSchema write reaches this path only fresh — Static-existing goes through the branch above,
        // dynamic is rejected at the top — so baseSnapshot is null and only writeSchema's invariants apply;
        // includeSnapshotInvariants: !overwriteSchema keeps that crisp.)
        EnforceWriteConstraints(
            enforcer, baseSnapshot, writeSchema, batches, includeSnapshotInvariants: !overwriteSchema);

        // #525: stage under the table's PHYSICAL schema for an EXISTING name-mode table (see AppendAsync); a
        // fresh path or a `none`-mode table returns the logical schema unchanged. The mode-aware overwrite
        // (incl. the dynamic+overwriteSchema reject and fresh-create) is applied by CreateOrOverwriteAsync.
        (StructType stagingSchema, IReadOnlyList<string> stagingPartitions) =
            ResolvePhysicalStaging(baseSnapshot, writeSchema, partitionColumns);

        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(stagingSchema, stagingPartitions, batches, cancellationToken).ConfigureAwait(false);
        DeltaCommitResult commit = await _writer
            .CreateOrOverwriteAsync(
                baseSnapshot, writeSchema, partitionColumns, files, Map(partitionOverwriteMode), overwriteSchema,
                cancellationToken)
            .ConfigureAwait(false);
        return new DeltaWriteResult(commit.Version, files.Count, rows);
    }

    /// <summary>
    /// Creates a fresh Delta table with column mapping <c>name</c> mode enabled (STORY-05.4.3 / #191). Each
    /// top-level column is assigned a stable physical name (<c>col-&lt;uuid&gt;</c> from
    /// <paramref name="physicalNameSource"/>) and id; the Parquet data files are written under those
    /// <b>physical</b> names, <c>add.partitionValues</c> are keyed by physical name, the data-file path is
    /// physical, and the committed <c>metaData</c> carries the logical schema (with per-field id/physicalName),
    /// the <b>LOGICAL</b> <c>partitionColumns</c> (matching the Spark golden <c>dv-with-columnmapping</c>: name
    /// mode records partition IDENTITY logically while partition VALUE KEYS stay physical), the
    /// <c>delta.columnMapping.mode=name</c> / <c>maxColumnId</c> configuration, and the table-features
    /// protocol declaring the <c>columnMapping</c> feature. Enablement is scoped to a fresh table (there is
    /// nothing to read through), so every file is physical-named from version 0.
    /// </summary>
    /// <exception cref="InvalidOperationException">A table already exists at this path (enabling column
    /// mapping on an existing non-empty table is out of scope in this build).</exception>
    internal async Task<DeltaWriteResult> CreateNameMappedTableAsync(
        StructType logicalSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        IColumnPhysicalNameSource physicalNameSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logicalSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(physicalNameSource);

        if (await TableExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Enabling column mapping on an existing table is out of scope in this build; column mapping "
                + "'name' mode can only be enabled on a fresh table (first write).");
        }

        RejectUnenforceableCreate(logicalSchema, batches);

        (StructType mappedSchema, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(logicalSchema, physicalNameSource);

        // Route the staging into PHYSICAL name space: renaming the write schema + partition columns to their
        // physical names makes the existing partitioner/Parquet writer emit files with physical column names,
        // physical-keyed partition values, and a physical data-file path — no new staging path needed.
        StructType physicalSchema = ColumnMapping.ToPhysicalSchema(mappedSchema, ColumnMappingMode.Name);
        IReadOnlyList<string> physicalPartitions =
            ColumnMapping.PhysicalPartitionColumns(mappedSchema, partitionColumns, ColumnMappingMode.Name);

        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(physicalSchema, physicalPartitions, batches, cancellationToken).ConfigureAwait(false);

        // metaData.partitionColumns are the LOGICAL names (HIGH#1 / Spark golden); the staged files carry
        // PHYSICAL partition-value keys, so CreateMappedTableAsync validates coverage against the physical
        // set while committing the logical names into the metaData.
        DeltaCommitResult commit = await _writer
            .CreateMappedTableAsync(
                mappedSchema,
                partitionColumns,
                physicalPartitions,
                ColumnMapping.NameModeConfiguration(maxColumnId),
                ColumnMapping.NameModeProtocol(),
                files,
                cancellationToken)
            .ConfigureAwait(false);
        return new DeltaWriteResult(commit.Version, files.Count, rows);
    }

    /// <summary>
    /// Creates a fresh Delta table with column mapping <c>id</c> mode enabled (#572). Mirrors
    /// <see cref="CreateNameMappedTableAsync"/> exactly — each top-level column is assigned a stable physical
    /// name (<c>col-&lt;uuid&gt;</c> from <paramref name="physicalNameSource"/>) and id, the Parquet data
    /// files are written under those <b>physical</b> names, <c>add.partitionValues</c> are keyed by physical
    /// name, the data-file path is physical, and <c>metaData.partitionColumns</c> carry the <b>LOGICAL</b>
    /// names — with ONE behavioral difference: the staged physical schema PRESERVES each field's
    /// <c>delta.columnMapping.id</c>, so <see cref="Parquet.ParquetTypeMapping.CreateField"/> stamps the
    /// Parquet <c>field_id</c> into the footer (an id-mode reader resolves columns by that field_id, not by
    /// physical name). The committed <c>metaData</c> declares <c>delta.columnMapping.mode=id</c> /
    /// <c>maxColumnId</c> and the <c>protocol</c> declares the <c>columnMapping</c> reader+writer feature.
    /// Enablement is scoped to a fresh table (there is nothing to read through), so every file is
    /// physical-named with a stamped field_id from version 0.
    /// </summary>
    /// <exception cref="InvalidOperationException">A table already exists at this path (enabling column
    /// mapping on an existing non-empty table is out of scope in this build).</exception>
    internal async Task<DeltaWriteResult> CreateIdMappedTableAsync(
        StructType logicalSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        IColumnPhysicalNameSource physicalNameSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logicalSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(physicalNameSource);

        if (await TableExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Enabling column mapping on an existing table is out of scope in this build; column mapping "
                + "'id' mode can only be enabled on a fresh table (first write).");
        }

        RejectUnenforceableCreate(logicalSchema, batches);

        (StructType mappedSchema, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(logicalSchema, physicalNameSource);

        // Route the staging into PHYSICAL name space (as name mode) but PRESERVE the per-field id metadata on
        // the physical schema — ToPhysicalSchema(mappedSchema, Id) keeps delta.columnMapping.id so the Parquet
        // writer stamps the footer field_id. Partition-value keys stay physical.
        StructType physicalSchema = ColumnMapping.ToPhysicalSchema(mappedSchema, ColumnMappingMode.Id);
        IReadOnlyList<string> physicalPartitions =
            ColumnMapping.PhysicalPartitionColumns(mappedSchema, partitionColumns, ColumnMappingMode.Id);

        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(physicalSchema, physicalPartitions, batches, cancellationToken).ConfigureAwait(false);

        // metaData.partitionColumns are the LOGICAL names; the staged files carry PHYSICAL partition-value
        // keys, so CreateMappedTableAsync validates coverage against the physical set while committing the
        // logical names into the metaData.
        DeltaCommitResult commit = await _writer
            .CreateMappedTableAsync(
                mappedSchema,
                partitionColumns,
                physicalPartitions,
                ColumnMapping.IdModeConfiguration(maxColumnId),
                ColumnMapping.IdModeProtocol(),
                files,
                cancellationToken)
            .ConfigureAwait(false);
        return new DeltaWriteResult(commit.Version, files.Count, rows);
    }

    /// <summary>
    /// Creates a fresh Delta table that BOTH uses column mapping <c>name</c> mode AND enables deletion
    /// vectors (#529 test seam): each top-level column is assigned a stable physical name / id (like
    /// <see cref="CreateNameMappedTableAsync"/>) and the committed <c>protocol</c>/<c>configuration</c>
    /// additionally declare the <c>deletionVectors</c> feature (reader v3 / writer v7) and set
    /// <c>delta.enableDeletionVectors=true</c>, so a subsequent <see cref="DeltaDelete"/> passes the
    /// protocol gate and exercises the WRITE-path column-mapping relabel. Data files are written under the
    /// PHYSICAL names; <c>metaData.partitionColumns</c> carry the LOGICAL names and the staged partition
    /// values are physical-keyed — identical to the pure name-mode create.
    /// </summary>
    /// <exception cref="InvalidOperationException">A table already exists at this path.</exception>
    internal async Task<DeltaWriteResult> CreateNameMappedDeletionVectorTableAsync(
        StructType logicalSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        IColumnPhysicalNameSource physicalNameSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logicalSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(physicalNameSource);

        if (await TableExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Enabling column mapping + deletion vectors on an existing table is out of scope in this "
                + "build; both can only be enabled on a fresh table (first write).");
        }

        RejectUnenforceableCreate(logicalSchema, batches);

        (StructType mappedSchema, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(logicalSchema, physicalNameSource);

        StructType physicalSchema = ColumnMapping.ToPhysicalSchema(mappedSchema, ColumnMappingMode.Name);
        IReadOnlyList<string> physicalPartitions =
            ColumnMapping.PhysicalPartitionColumns(mappedSchema, partitionColumns, ColumnMappingMode.Name);

        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(physicalSchema, physicalPartitions, batches, cancellationToken).ConfigureAwait(false);

        // Merge the name-mode and deletion-vector enablement into ONE protocol (reader v3 / writer v7 with
        // BOTH features declared) and ONE configuration (columnMapping mode/maxColumnId + enableDeletionVectors).
        ImmutableSortedDictionary<string, string> configuration = ColumnMapping.NameModeConfiguration(maxColumnId)
            .Add(DeletionVectorsFeature.EnablePropertyKey, "true");
        var protocol = new ProtocolAction(
            DeletionVectorsFeature.ReaderVersion,
            DeletionVectorsFeature.WriterVersion,
            ImmutableArray.Create(ColumnMapping.Feature, DeletionVectorsFeature.Feature),
            ImmutableArray.Create(ColumnMapping.Feature, DeletionVectorsFeature.Feature));

        DeltaCommitResult commit = await _writer
            .CreateMappedTableAsync(
                mappedSchema,
                partitionColumns,
                physicalPartitions,
                configuration,
                protocol,
                files,
                cancellationToken)
            .ConfigureAwait(false);
        return new DeltaWriteResult(commit.Version, files.Count, rows);
    }

    /// <summary>
    /// Creates a fresh Delta table that BOTH uses column mapping <c>id</c> mode AND enables deletion vectors
    /// (#572 DELETE test seam): the id-mode sibling of <see cref="CreateNameMappedDeletionVectorTableAsync"/>.
    /// Data files are written under the PHYSICAL names WITH each field's <c>delta.columnMapping.id</c>
    /// preserved (so the Parquet footer field_id is stamped); the committed <c>protocol</c>/<c>configuration</c>
    /// declare BOTH the <c>columnMapping</c> (id mode) and <c>deletionVectors</c> features, so a subsequent
    /// <see cref="DeltaDelete"/> passes the protocol gate and exercises the id-mode WRITE-path resolution
    /// (columns resolved by field_id; the deletion vector stays POSITIONAL over the physical file).
    /// </summary>
    /// <exception cref="InvalidOperationException">A table already exists at this path.</exception>
    internal async Task<DeltaWriteResult> CreateIdMappedDeletionVectorTableAsync(
        StructType logicalSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        IColumnPhysicalNameSource physicalNameSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logicalSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(physicalNameSource);

        if (await TableExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Enabling column mapping + deletion vectors on an existing table is out of scope in this "
                + "build; both can only be enabled on a fresh table (first write).");
        }

        RejectUnenforceableCreate(logicalSchema, batches);

        (StructType mappedSchema, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(logicalSchema, physicalNameSource);

        StructType physicalSchema = ColumnMapping.ToPhysicalSchema(mappedSchema, ColumnMappingMode.Id);
        IReadOnlyList<string> physicalPartitions =
            ColumnMapping.PhysicalPartitionColumns(mappedSchema, partitionColumns, ColumnMappingMode.Id);

        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(physicalSchema, physicalPartitions, batches, cancellationToken).ConfigureAwait(false);

        // Merge the id-mode and deletion-vector enablement into ONE protocol (reader v3 / writer v7 with BOTH
        // features declared) and ONE configuration (columnMapping mode=id/maxColumnId + enableDeletionVectors).
        ImmutableSortedDictionary<string, string> configuration = ColumnMapping.IdModeConfiguration(maxColumnId)
            .Add(DeletionVectorsFeature.EnablePropertyKey, "true");
        var protocol = new ProtocolAction(
            DeletionVectorsFeature.ReaderVersion,
            DeletionVectorsFeature.WriterVersion,
            ImmutableArray.Create(ColumnMapping.Feature, DeletionVectorsFeature.Feature),
            ImmutableArray.Create(ColumnMapping.Feature, DeletionVectorsFeature.Feature));

        DeltaCommitResult commit = await _writer
            .CreateMappedTableAsync(
                mappedSchema,
                partitionColumns,
                physicalPartitions,
                configuration,
                protocol,
                files,
                cancellationToken)
            .ConfigureAwait(false);
        return new DeltaWriteResult(commit.Version, files.Count, rows);
    }

    /// <summary>
    /// Creates a fresh Delta table with <b>deletion vectors enabled</b> (STORY-05.5.1 / #192): the committed
    /// <c>protocol</c> declares the <c>deletionVectors</c> table feature (reader v3 / writer v7) and the
    /// <c>metaData.configuration</c> sets <c>delta.enableDeletionVectors=true</c>, so a subsequent
    /// <see cref="DeltaDelete"/> passes the protocol gate. Data files are written normally (no column
    /// mapping); this is the write-side enablement seam the AC2/AC3/AC4 tests build a table with.
    /// </summary>
    /// <exception cref="InvalidOperationException">A table already exists at this path (enabling deletion
    /// vectors on an existing table is out of scope in this build).</exception>
    internal async Task<DeltaWriteResult> CreateDeletionVectorTableAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);

        if (await TableExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Enabling deletion vectors on an existing table is out of scope in this build; deletion "
                + "vectors can only be enabled on a fresh table (first write).");
        }

        RejectUnenforceableCreate(writeSchema, batches);

        (IReadOnlyList<StagedDataFile> files, long rows) =
            await StageAsync(writeSchema, partitionColumns, batches, cancellationToken).ConfigureAwait(false);

        DeltaCommitResult commit = await _writer
            .CreateMappedTableAsync(
                writeSchema,
                partitionColumns,
                partitionColumns,
                DeletionVectorsFeature.EnabledConfiguration(),
                DeletionVectorsFeature.Protocol(),
                files,
                cancellationToken,
                // #497: the DV-create path is logical==physical (no column mapping), so gate the version-0
                // metaData schema on the real staged bytes' footer schema too.
                validatePhysicalWriteSchema: true)
            .ConfigureAwait(false);
        return new DeltaWriteResult(commit.Version, files.Count, rows);
    }

    // #525/#572/#596: for an EXISTING column-mapped (name OR id) table, resolve the PHYSICAL schema +
    // partition columns the staged Parquet must physically carry (so an append/overwrite writes col-<uuid>
    // names + physical-keyed partitionValues, IDENTICAL to the fresh-create path). The physical names are the
    // table's EXISTING per-field delta.columnMapping.physicalName — reused verbatim, NEVER re-minted. In id
    // mode the physical schema additionally carries each field's delta.columnMapping.id, so the Parquet writer
    // stamps the footer field_id (the id-mode reader resolves columns by that field_id). For a fresh path
    // (create door, `snapshot` null) or a `none`-mode table this is logical==physical, so the caller's write
    // schema / partition columns pass through unchanged (byte-for-byte identical staging to prior behavior).
    // This is a STAGING concern only — the commit call still passes the LOGICAL write schema to
    // DeltaTableWriter, which re-derives the physical form for its own commit-time validation. #596: takes the
    // caller's already-loaded base snapshot so overwrite loads the snapshot exactly ONCE (shared with
    // enforcement + the commit) instead of re-reading the log here.
    private static (StructType StagingSchema, IReadOnlyList<string> StagingPartitions)
        ResolvePhysicalStaging(
            Snapshot? snapshot, StructType writeSchema, IReadOnlyList<string> partitionColumns)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);

        if (snapshot is null)
        {
            // Fresh path: the write creates the table (logical==physical for a plain create; a name/id-mode
            // create goes through CreateNameMappedTableAsync / CreateIdMappedTableAsync, not this facade path).
            return (writeSchema, partitionColumns);
        }

        ColumnMappingMode mode = ColumnMapping.ResolveMode(snapshot.Metadata.Configuration);
        if (mode == ColumnMappingMode.None)
        {
            return (writeSchema, partitionColumns);
        }

        StructType physicalSchema = ColumnMapping.MapWriteSchemaToPhysical(writeSchema, snapshot.Schema, mode);
        IReadOnlyList<string> physicalPartitions =
            ColumnMapping.PhysicalPartitionColumns(snapshot.Schema, partitionColumns, mode);
        return (physicalSchema, physicalPartitions);
    }

    // Partition the batches, write one Parquet data file per non-empty partition (data columns only), and
    // return the staged-file descriptors plus the total row count.
    private async Task<(IReadOnlyList<StagedDataFile> Files, long Rows)> StageAsync(
        StructType writeSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);

        IReadOnlyList<ColumnBatchPartitioner.PartitionGroup> groups =
            ColumnBatchPartitioner.Partition(writeSchema, partitionColumns, batches, cancellationToken);

        // TRACKED DEFERRAL (#442 unbounded materialization; columnar sink-contract #443): this stages the
        // whole write in memory — rows→ColumnBatch (upstream), then per-partition ColumnBatches here, then a
        // per-file MemoryStream + ToArray() below — a triple materialization with no spill/streaming bound.
        // A streaming/columnar sink contract that writes each partition file incrementally is #442/#443.
        var files = new List<StagedDataFile>(groups.Count);
        long totalRows = 0;
        long modificationTime = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        foreach (ColumnBatchPartitioner.PartitionGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = DataFilePath(partitionColumns, group.PartitionValues, _fileNameFactory());

            using var buffer = new MemoryStream();
            ParquetFileWriter.WriteResult result = await _parquetWriter
                .WriteWithStatisticsAsync(buffer, group.DataSchema, group.Batches, StatisticsPolicy.Default, cancellationToken)
                .ConfigureAwait(false);
            byte[] bytes = buffer.ToArray();

            // #497: derive the ACTUAL physical data schema from the file we just wrote by reading its footer
            // back — NOT the declared group.DataSchema. Recording the real bytes' schema makes the commit-time
            // enforcement (DeltaTableWriter.ValidateStagedWriteSchema) gate the true written columns/types
            // rather than trusting the caller's declaration, closing the trusted-declaration gap flagged on
            // #492/#190. (The footer parse decodes no data pages.) In column-mapping name mode this is the
            // PHYSICAL-named schema — correct, and unvalidated because the mapped create path deliberately
            // does not call ValidateStagedWriteSchema (deferred to #525).
            StructType writtenSchema;
            using (var footer = new MemoryStream(bytes, writable: false))
            {
                writtenSchema = await _reader.ReadDataSchemaAsync(footer, cancellationToken).ConfigureAwait(false);
            }

            await _backend.PutIfAbsentAsync(relativePath, bytes, cancellationToken).ConfigureAwait(false);

            files.Add(new StagedDataFile(
                relativePath,
                group.PartitionValues,
                Size: bytes.LongLength,
                ModificationTime: modificationTime,
                Stats: result.Statistics,
                DataSchema: writtenSchema));
            totalRows += result.RowCount;
        }

        return (files, totalRows);
    }

    // The table-relative data-file path: Hive-style `col=value/...` partition directories (a null value uses
    // the __HIVE_DEFAULT_PARTITION__ sentinel directory; the log still records a real null), then a unique
    // `part-<guid>.parquet`. Partition truth lives in the committed add.partitionValues, so the physical
    // directory encoding never affects read correctness.
    private static string DataFilePath(
        IReadOnlyList<string> partitionColumns,
        System.Collections.Immutable.ImmutableSortedDictionary<string, string?> partitionValues,
        string fileNameToken)
    {
        string fileName = "part-" + fileNameToken + ".parquet";
        if (partitionColumns.Count == 0)
        {
            return fileName;
        }

        var segments = new List<string>(partitionColumns.Count + 1);
        foreach (string column in partitionColumns)
        {
            partitionValues.TryGetValue(column, out string? value);
            string encoded = value is null
                ? DeltaWriteEncoding.HiveDefaultPartition
                : Uri.EscapeDataString(value);
            segments.Add(column + "=" + encoded);
        }

        segments.Add(fileName);
        return string.Join('/', segments);
    }

    private static PartitionOverwriteMode Map(DeltaPartitionOverwriteMode mode) => mode switch
    {
        DeltaPartitionOverwriteMode.Static => PartitionOverwriteMode.Static,
        DeltaPartitionOverwriteMode.Dynamic => PartitionOverwriteMode.Dynamic,
        _ => throw new ArgumentOutOfRangeException(
            nameof(mode), mode, "Unknown partition overwrite mode."),
    };

    /// <inheritdoc/>
    public void Dispose() => (_backend as IDisposable)?.Dispose();
}
