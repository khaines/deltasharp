using System.Collections.Immutable;
using DeltaSharp.Storage.Delta.DeletionVectors;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Folds an ordered Delta action stream into active table state (design §2.10.4 step 4). Callers apply
/// checkpoint actions first (the initial state at some version <c>C</c>), then the JSON commit actions of
/// versions <c>(C, V]</c> in ascending version order, each commit's actions in file order; later actions
/// win. The result is materialized into an immutable <see cref="Snapshot"/>.
///
/// <para>The active file set is derived <b>only</b> from committed <c>add</c>/<c>remove</c> actions —
/// never a directory listing (§2.10.4 anti-pattern #1).</para>
/// </summary>
internal sealed class SnapshotState
{
    private readonly Dictionary<string, AddFileAction> _activeAdds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoveFileAction> _tombstones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _transactions = new(StringComparer.Ordinal);

    // The active-file identity is (path, deletionVector.uniqueId) — NOT the path alone (Delta protocol
    // "Derived Fields": uniqueId "is used for snapshot reconstruction to differentiate the same file with
    // different DVs in successive versions"). A merge-on-read DELETE commits remove(path, oldDv) + add(path,
    // newDv); keying by the composite makes the fold ORDER-INDEPENDENT within the commit (the remove clears
    // the old logical file's key, the add installs the new one, whatever order they appear), so the file
    // stays active with exactly its new DV. For a table with no DVs the DV segment is empty and the key is
    // the path — byte-for-byte the prior behavior.
    private static string FileKey(string path, DeletionVectorDescriptor? deletionVector) =>
        deletionVector is null ? path : path + "\u0001" + deletionVector.UniqueId;

    /// <summary>The most recent <c>protocol</c> action seen, or null if none yet.</summary>
    public ProtocolAction? Protocol { get; private set; }

    /// <summary>The most recent <c>metaData</c> action seen, or null if none yet.</summary>
    public MetadataAction? Metadata { get; private set; }

    /// <summary>Applies one action, mutating the active state per the replay rules (§2.10.4).</summary>
    public void Apply(DeltaAction action)
    {
        switch (action)
        {
            case AddFileAction add:
                // An add makes the (path, dvId) logical file active; it also supersedes any prior tombstone
                // for that same logical file (a re-add of a previously-removed logical file).
                string addKey = FileKey(add.Path, add.DeletionVector);
                _activeAdds[addKey] = add;
                _tombstones.Remove(addKey);
                break;

            case RemoveFileAction remove:
                string removeKey = FileKey(remove.Path, remove.DeletionVector);
                _activeAdds.Remove(removeKey);
                _tombstones[removeKey] = remove;
                break;

            case MetadataAction metadata:
                Metadata = metadata;
                break;

            case ProtocolAction protocol:
                Protocol = protocol;
                break;

            case TxnAction txn:
                // Keep the highest committed version per appId (idempotency, §2.11).
                _transactions[txn.AppId] =
                    _transactions.TryGetValue(txn.AppId, out long existing) ? Math.Max(existing, txn.Version) : txn.Version;
                break;

            case CommitInfoAction:
                // Provenance only — not load-bearing for the active state.
                break;
        }
    }

    /// <summary>Applies every action in <paramref name="actions"/>, in order.</summary>
    public void ApplyAll(IEnumerable<DeltaAction> actions)
    {
        foreach (DeltaAction action in actions)
        {
            Apply(action);
        }
    }

    /// <summary>The active <c>add</c> files (order-independent; a stable ordering is imposed by
    /// <see cref="Snapshot"/>).</summary>
    public IReadOnlyCollection<AddFileAction> ActiveFiles => _activeAdds.Values;

    /// <summary>The surviving tombstones (removes not yet superseded by a re-add).</summary>
    public IReadOnlyCollection<RemoveFileAction> Tombstones => _tombstones.Values;

    /// <summary>The per-appId last-committed transaction versions.</summary>
    public IReadOnlyDictionary<string, long> Transactions => _transactions;

    /// <summary>The last committed version for <paramref name="appId"/>, or null if none.</summary>
    public long? TransactionVersion(string appId) =>
        _transactions.TryGetValue(appId, out long v) ? v : null;

    /// <summary>Materializes the folded state into an immutable <see cref="Snapshot"/> at
    /// <paramref name="version"/>. A snapshot with no <c>metaData</c>/<c>protocol</c> is an inconsistent
    /// log — the reader refuses to invent table state (design §2.10.4, checklist anti-pattern).</summary>
    public Snapshot ToSnapshot(long version, SnapshotLoadMetrics metrics)
    {
        if (Protocol is null)
        {
            throw DeltaProtocolException.Inconsistent(
                $"Delta table snapshot at version {version} has no protocol action; the log is incomplete and cannot be read.");
        }

        if (Metadata is null)
        {
            throw DeltaProtocolException.Inconsistent(
                $"Delta table snapshot at version {version} has no metaData action; the log is incomplete and cannot be read.");
        }

        // Deterministic, path-ordered active-file list so scans see a stable membership order.
        ImmutableArray<AddFileAction> active = _activeAdds.Values
            .OrderBy(a => a.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        ImmutableArray<RemoveFileAction> tombstones = _tombstones.Values
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        ImmutableSortedDictionary<string, long> txns = _transactions
            .ToImmutableSortedDictionary(StringComparer.Ordinal);

        return new Snapshot(version, Protocol, Metadata, active, tombstones, txns, metrics);
    }
}
