using System.Collections.Immutable;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// An immutable, point-in-time view of a Delta table at a single <see cref="Version"/> (design §2.10.5).
/// It exposes exactly the state a query planner needs — schema, protocol, metadata, the active
/// <see cref="AddFileAction"/> set, tombstones, and aggregate statistics — all read-only. A read pinned
/// to a snapshot is served entirely from it and <b>cannot</b> observe a later commit (STORY-05.2.3 AC1;
/// design "ACID" isolation). This is the handoff contract to query execution.
/// </summary>
internal sealed class Snapshot
{
    private readonly SnapshotLoadMetrics _metrics;
    private StructType? _schema;

    internal Snapshot(
        long version,
        ProtocolAction protocol,
        MetadataAction metadata,
        ImmutableArray<AddFileAction> activeFiles,
        ImmutableArray<RemoveFileAction> tombstones,
        ImmutableSortedDictionary<string, long> transactions,
        SnapshotLoadMetrics metrics)
    {
        Version = version;
        Protocol = protocol;
        Metadata = metadata;
        ActiveFiles = activeFiles;
        Tombstones = tombstones;
        Transactions = transactions;
        _metrics = metrics;
    }

    /// <summary>The committed log version this snapshot reflects.</summary>
    public long Version { get; }

    /// <summary>The table's <c>protocol</c> action (reader/writer versions + features) at this version.</summary>
    public ProtocolAction Protocol { get; }

    /// <summary>The table's <c>metaData</c> action at this version — reported as-of snapshot creation even
    /// if a later commit changes it (STORY-05.2.3 AC3).</summary>
    public MetadataAction Metadata { get; }

    /// <summary>
    /// The table schema, parsed once from <see cref="MetadataAction.SchemaString"/> via the relocated
    /// <c>SchemaJson</c> (design §9.1 D-6). A <c>schemaString</c> that is not a struct is an inconsistent
    /// log — the reader fails closed rather than inventing a shape.
    /// </summary>
    public StructType Schema => _schema ??= ParseSchema();

    /// <summary>The active data files (path-ordered) — the file membership for scans at this version.</summary>
    public ImmutableArray<AddFileAction> ActiveFiles { get; }

    /// <summary>The surviving tombstones (removed files retained for time travel / VACUUM).</summary>
    public ImmutableArray<RemoveFileAction> Tombstones { get; }

    /// <summary>Per-appId last-committed transaction versions (idempotency).</summary>
    public ImmutableSortedDictionary<string, long> Transactions { get; }

    /// <summary>The number of active data files.</summary>
    public int ActiveFileCount => ActiveFiles.Length;

    /// <summary>The total size in bytes of the active data files (aggregate statistic).</summary>
    public long ActiveSizeInBytes
    {
        get
        {
            long total = 0;
            foreach (AddFileAction add in ActiveFiles)
            {
                total += add.Size;
            }

            return total;
        }
    }

    /// <summary>The load observability for how this snapshot was reconstructed (checkpoint + replay depth).</summary>
    public SnapshotLoadMetrics Metrics => _metrics with { ActiveFileCount = ActiveFileCount };

    /// <summary>
    /// Selects the active files that <b>might</b> satisfy <paramref name="request"/> (partition + data-skip
    /// filters), pruning only files the committed partition values / statistics <b>prove</b> cannot match
    /// (design §2.4; STORY-05.2.3 AC2). The result is a <b>sound over-approximation</b> — every truly
    /// matching file is a candidate — and the residual predicate remains the scan's responsibility. This is
    /// the file-pruning half of the snapshot's handoff contract to query execution (§2.10.5).
    /// </summary>
    public FilePruningResult PruneFiles(FilePruningRequest request) => FilePruner.Prune(ActiveFiles, request);

    /// <summary>
    /// Builds the read-only, side-effect-free <see cref="TableStatisticsReport"/> an optimizer requests
    /// for cost-based planning (STORY-05.6.3 AC4): the table aggregates, a freshness/version token (this
    /// snapshot's <see cref="Version"/>), and per-file/column statistic availability with explicit absence
    /// reasons. It reads only the already-parsed snapshot state under <paramref name="policy"/> (defaults
    /// to <see cref="StatisticsPolicy.Default"/>) and never re-reads data files.
    /// </summary>
    public TableStatisticsReport GetStatistics(StatisticsPolicy? policy = null) =>
        SnapshotStatisticsReporter.Build(Version, Schema, ActiveFiles, policy ?? StatisticsPolicy.Default);

    private StructType ParseSchema()
    {
        DataType parsed;
        try
        {
            parsed = SchemaJson.FromJson(Metadata.SchemaString);
        }
        catch (SchemaValidationException ex)
        {
            throw DeltaProtocolException.Inconsistent(
                $"Delta table snapshot at version {Version} has an unparseable metaData.schemaString.", ex);
        }

        if (parsed is not StructType structType)
        {
            throw DeltaProtocolException.Inconsistent(
                $"Delta table snapshot at version {Version} has a metaData.schemaString that is not a struct.");
        }

        return structType;
    }
}
