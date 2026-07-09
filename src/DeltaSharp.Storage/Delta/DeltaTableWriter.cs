using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using DeltaSharp.Storage.Backends;

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
/// <item><b>Append</b> (<see cref="AppendAsync(Snapshot, IReadOnlyList{StagedDataFile}, CancellationToken)"/>):
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

    /// <summary>Loads the latest snapshot and appends <paramref name="files"/> to it (AC1 convenience).</summary>
    public async Task<DeltaCommitResult> AppendAsync(
        IReadOnlyList<StagedDataFile> files, CancellationToken cancellationToken = default)
    {
        Snapshot readSnapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
        return await AppendAsync(readSnapshot, files, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends <paramref name="files"/> as new <c>add</c> actions against <paramref name="readSnapshot"/>
    /// (STORY-05.3.3 AC1). Emits <b>only</b> adds — no removes — so every prior active file remains active,
    /// and commits under <see cref="DeltaReadScope.BlindAppend"/>, the read-nothing scope that conflicts
    /// only with a concurrent metadata/protocol change and rebases past concurrent appends.
    /// </summary>
    public Task<DeltaCommitResult> AppendAsync(
        Snapshot readSnapshot,
        IReadOnlyList<StagedDataFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException("An append must stage at least one data file.", nameof(files));
        }

        ValidatePartitionCoverage(files, readSnapshot.Metadata.PartitionColumns);

        var actions = new List<DeltaAction>(files.Count);
        AppendAddActions(actions, files);
        return _committer.CommitAsync(readSnapshot, actions, DeltaReadScope.BlindAppend, cancellationToken);
    }

    /// <summary>Loads the latest snapshot and overwrites it with <paramref name="files"/> (convenience).</summary>
    public async Task<DeltaCommitResult> OverwriteAsync(
        IReadOnlyList<StagedDataFile> files,
        PartitionOverwriteMode mode = PartitionOverwriteMode.Static,
        CancellationToken cancellationToken = default)
    {
        Snapshot readSnapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);
        return await OverwriteAsync(readSnapshot, files, mode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Overwrites <paramref name="readSnapshot"/> with <paramref name="files"/> (STORY-05.3.3 AC2/AC3). In
    /// <see cref="PartitionOverwriteMode.Static"/> (full overwrite) it removes <b>every</b> prior active
    /// file in the same atomic version as the new adds, under <see cref="DeltaReadScope.WholeTable"/>. In
    /// <see cref="PartitionOverwriteMode.Dynamic"/> it removes only the prior active files whose partition
    /// values match a partition the new write touches, under a
    /// <see cref="DeltaReadScope.ReadFiles(IEnumerable{string})"/> scope over exactly those files so a
    /// concurrent remove/re-add of one of them is rejected while an append to an untouched partition
    /// rebases (a concurrent new-file append into a touched partition is not rejected — tracked in #488).
    /// An unpartitioned table has a single partition, so a dynamic overwrite is routed to the full-overwrite
    /// (<see cref="DeltaReadScope.WholeTable"/>) path.
    /// </summary>
    public Task<DeltaCommitResult> OverwriteAsync(
        Snapshot readSnapshot,
        IReadOnlyList<StagedDataFile> files,
        PartitionOverwriteMode mode = PartitionOverwriteMode.Static,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            // An empty overwrite (truncate) is a distinct operation; STORY-05.3.3 overwrites with new data.
            throw new ArgumentException("An overwrite must stage at least one data file.", nameof(files));
        }

        ValidatePartitionCoverage(files, readSnapshot.Metadata.PartitionColumns);

        return mode switch
        {
            PartitionOverwriteMode.Static => FullOverwriteAsync(readSnapshot, files, cancellationToken),
            PartitionOverwriteMode.Dynamic => DynamicPartitionOverwriteAsync(readSnapshot, files, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown partition overwrite mode."),
        };
    }

    // AC2: remove EVERY prior active file + add the new files in one atomic version, scoped WholeTable so
    // any concurrent add/remove aborts the overwrite (it depends on the entire active set).
    private Task<DeltaCommitResult> FullOverwriteAsync(
        Snapshot readSnapshot, IReadOnlyList<StagedDataFile> files, CancellationToken cancellationToken)
    {
        long deletionTimestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var actions = new List<DeltaAction>(readSnapshot.ActiveFiles.Length + files.Count);
        foreach (AddFileAction prior in readSnapshot.ActiveFiles)
        {
            actions.Add(ToRemove(prior, deletionTimestamp));
        }

        AppendAddActions(actions, files);
        return _committer.CommitAsync(readSnapshot, actions, DeltaReadScope.WholeTable, cancellationToken);
    }

    // AC3: remove only the prior active files in the touched partitions + add the new files, scoped to
    // exactly those prior files (ReadFiles) so a concurrent remove/re-add of a touched-partition file is
    // rejected while an append to an untouched partition rebases.
    //
    // Removal selection is an EXACT partition-key match against the snapshot's active files — deliberately
    // NOT Snapshot.PruneFiles. PruneFiles is a *sound over-approximation* built for scans: it keeps any file
    // it cannot prove non-matching (e.g. one missing a partition-column key). Over-selecting is harmless for
    // a read (later filtered) but for a destructive overwrite it would tombstone a file in an UNTOUCHED
    // partition — silent, unrecoverable data loss (council #486 R1: red-team Critical / Security F1).
    // Matching each active file's canonical PartitionKey against the touched set is exact in both
    // directions: no over-select (data loss) and no under-select (stale duplicate data).
    private Task<DeltaCommitResult> DynamicPartitionOverwriteAsync(
        Snapshot readSnapshot, IReadOnlyList<StagedDataFile> files, CancellationToken cancellationToken)
    {
        ImmutableArray<string> partitionColumns = readSnapshot.Metadata.PartitionColumns;

        // An unpartitioned table is a single partition, so a dynamic overwrite replaces the whole table —
        // identical to a static overwrite. Route it to the full-overwrite path so it also gets the stronger
        // WholeTable isolation (a concurrent append aborts), not merely the same action set.
        if (partitionColumns.IsDefaultOrEmpty)
        {
            return FullOverwriteAsync(readSnapshot, files, cancellationToken);
        }

        // The distinct partition keys the staged files touch. The SAME injective key matches prior files
        // below, so removal selection is an exact set-membership test.
        var touchedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (StagedDataFile file in files)
        {
            touchedKeys.Add(PartitionKey(file.PartitionValues, partitionColumns));
        }

        // A prior active file is removed IFF its partition key exactly equals a touched partition key.
        var priorInTouched = new List<AddFileAction>();
        foreach (AddFileAction prior in readSnapshot.ActiveFiles)
        {
            if (touchedKeys.Contains(PartitionKey(prior.PartitionValues, partitionColumns)))
            {
                priorInTouched.Add(prior);
            }
        }

        long deletionTimestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var actions = new List<DeltaAction>(priorInTouched.Count + files.Count);
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
    // over-select it for removal (council #486 R1). For an unpartitioned table this is a no-op.
    private static void ValidatePartitionCoverage(
        IReadOnlyList<StagedDataFile> files, ImmutableArray<string> partitionColumns)
    {
        if (partitionColumns.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (StagedDataFile file in files)
        {
            foreach (string column in partitionColumns)
            {
                if (!file.PartitionValues.ContainsKey(column))
                {
                    throw new ArgumentException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Staged file '{file.Path}' is missing partition column '{column}'; a partitioned " +
                            $"write must specify a value (possibly null) for every partition column."),
                        nameof(files));
                }
            }
        }
    }

    // A stable, injective key for a partition's values over the table's partition columns, so two files in
    // the same partition map to the same key regardless of dictionary identity. NUL separators keep values
    // that concatenate ambiguously (e.g. "a","b" vs "ab") distinct, and a null value is a distinct sentinel.
    private static string PartitionKey(
        ImmutableSortedDictionary<string, string?> partitionValues, ImmutableArray<string> partitionColumns)
    {
        if (partitionColumns.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (string column in partitionColumns.OrderBy(c => c, StringComparer.Ordinal))
        {
            partitionValues.TryGetValue(column, out string? value);
            sb.Append(column.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(column).Append('\u0000');
            sb.Append(value is null ? "\u0001null" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value);
            sb.Append('\u0000');
        }

        return sb.ToString();
    }
}
