using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Tests.Delta.Simulation;

/// <summary>
/// A deterministic in-memory <see cref="IStorageBackend"/> that models the Delta commit CAS primitive
/// (design §2.11.1/§2.13.1) for the cooperative simulation. Its load-bearing member is
/// <see cref="PutIfAbsentAsync"/> — an atomic <b>single-winner</b> conditional-create over an in-memory
/// object store: the first caller to reach the claim for a key wins, every later caller gets
/// <see langword="false"/> (never an exception). Because the simulation is single-threaded and the claim
/// executes synchronously right after the scheduler yield, the winner is exactly the writer the scheduler
/// resumed first — the race is deterministic, not thread-timing.
///
/// <para><b>Stepwise scheduler control.</b> Every operation first awaits <see cref="_yield"/> (the
/// scheduler's yield point), so the scheduler can interleave other writers between any two backend steps —
/// covering the put-if-absent, the ambiguous re-read (<c>Head</c>/<c>OpenRead</c>), and the winner scan
/// (<c>List</c>/<c>Head</c>) interleaving points named in the issue.</para>
///
/// <para><b>Injectable faults (design §3.4.1, keyed on op + path + call index → reproducible from a
/// seed).</b> A <see cref="IBackendFaultSchedule"/> may turn a put into a bounded <b>transient</b> failure
/// (the committer retries with backoff) or an <b>ambiguous</b> acknowledgment — either durable (the write
/// lands but the ack is "lost", modelling §2.11.6) or non-durable (nothing lands). All injected faults are
/// ones the correct committer recovers from, so the invariant catalogue still holds; the checker proves it.
/// <see cref="DisableSingleWinner"/> is the deliberate <b>fault-injection efficacy</b> switch: it breaks the
/// CAS (every put "wins" and overwrites) so the Jepsen checker must catch the resulting I2 violation.</para>
/// </summary>
internal sealed class InMemoryStorageBackend : IStorageBackend
{
    private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _putCalls = new(StringComparer.Ordinal);
    private readonly Func<string, Task>? _yield;
    private readonly IBackendFaultSchedule _faults;

    /// <summary>Creates a backend. <paramref name="yield"/> is the scheduler yield point awaited before
    /// every operation (null ⇒ no interleaving, e.g. for out-of-band seeding); <paramref name="faults"/>
    /// injects reproducible put faults (defaults to none).</summary>
    public InMemoryStorageBackend(Func<string, Task>? yield = null, IBackendFaultSchedule? faults = null)
    {
        _yield = yield;
        _faults = faults ?? NoFaults.Instance;
    }

    /// <summary>When set, the CAS single-winner guarantee is <b>disabled</b> — every put "wins" and
    /// overwrites — to prove the Jepsen checker detects the resulting I2 (single-winner) violation.</summary>
    public bool DisableSingleWinner { get; set; }

    /// <summary>Gates the injectable fault schedule. Out-of-band table seeding (v0, pre-commits) runs the
    /// committer-less <see cref="DeltaTestHarness.WriteCommitAsync"/> path with no retry, so faults must not
    /// fire there; the runner flips this on only for the interleaved concurrent phase.</summary>
    public bool FaultsActive { get; set; }

    public async ValueTask<bool> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        await YieldAsync(StorageOp.Put, path).ConfigureAwait(false);

        int callIndex = _putCalls.TryGetValue(path, out int prior) ? prior : 0;
        _putCalls[path] = callIndex + 1;

        switch (FaultsActive ? _faults.NextPutFault(path, callIndex) : FaultKind.None)
        {
            case FaultKind.Transient:
                throw new DeltaStorageException(StorageErrorKind.Transient, $"injected transient put-if-absent failure for '{path}'.");

            case FaultKind.AmbiguousDurable:
                // The write lands but the acknowledgment is lost (§2.11.6): the committer must re-resolve
                // idempotently by re-reading the slot and recognizing its own nonce — never double-commit.
                _store.TryAdd(path, content.ToArray());
                throw new DeltaStorageException(StorageErrorKind.RetryUnsafeAmbiguous, $"injected durable-but-unacknowledged put for '{path}'.");

            case FaultKind.AmbiguousNonDurable:
                throw new DeltaStorageException(StorageErrorKind.RetryUnsafeAmbiguous, $"injected non-durable ambiguous put for '{path}'.");

            case FaultKind.None:
            default:
                break;
        }

        if (DisableSingleWinner)
        {
            _store[path] = content.ToArray(); // BROKEN CAS: overwrite, always "win".
            return true;
        }

        if (_store.ContainsKey(path))
        {
            return false; // atomic single-winner: a later caller loses, gets false (not an exception).
        }

        _store[path] = content.ToArray();
        return true;
    }

    public async ValueTask<StorageObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken)
    {
        await YieldAsync(StorageOp.Head, path).ConfigureAwait(false);
        return _store.TryGetValue(path, out byte[]? bytes)
            ? new StorageObjectInfo(path, bytes.Length, DateTime.UnixEpoch, ETag: null)
            : null;
    }

    public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        await YieldAsync(StorageOp.Read, path).ConfigureAwait(false);
        if (!_store.TryGetValue(path, out byte[]? bytes))
        {
            throw DeltaStorageException.NotFound($"object '{path}' does not exist.");
        }

        return new MemoryStream(bytes, writable: false);
    }

    public async ValueTask<Stream> ReadRangeAsync(string path, long offset, long length, CancellationToken cancellationToken)
    {
        await YieldAsync(StorageOp.ReadRange, path).ConfigureAwait(false);
        if (!_store.TryGetValue(path, out byte[]? bytes))
        {
            throw DeltaStorageException.NotFound($"object '{path}' does not exist.");
        }

        if (offset < 0 || length < 0 || offset + length > bytes.Length)
        {
            throw DeltaStorageException.CorruptData($"range [{offset}, {offset + length}) is out of bounds for '{path}'.");
        }

        return new MemoryStream(bytes, (int)offset, (int)length, writable: false);
    }

    public async IAsyncEnumerable<StorageObjectInfo> ListAsync(
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await YieldAsync(StorageOp.List, prefix).ConfigureAwait(false);

        // Snapshot the matching keys before yielding so a concurrent (interleaved) put does not mutate the
        // enumeration; ordering is by ordinal path for a reproducible listing.
        var matches = new List<KeyValuePair<string, byte[]>>();
        foreach (KeyValuePair<string, byte[]> entry in _store)
        {
            if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                matches.Add(entry);
            }
        }

        matches.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        foreach (KeyValuePair<string, byte[]> entry in matches)
        {
            yield return new StorageObjectInfo(entry.Key, entry.Value.Length, DateTime.UnixEpoch, ETag: null);
        }
    }

    public async ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
    {
        await YieldAsync(StorageOp.Delete, path).ConfigureAwait(false);
        _store.Remove(path); // idempotent: a missing object is a no-op.
    }

    public async ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken)
    {
        await YieldAsync(StorageOp.Write, path).ConfigureAwait(false);
        return new StagedWriteStream(this, path);
    }

    private Task YieldAsync(StorageOp op, string path) =>
        _yield is { } yield
            ? yield(op.ToString() + ":" + path)
            : Task.CompletedTask;

    private void Publish(string path, byte[] content) => _store[path] = content;

    /// <summary>A staged write that publishes only on <see cref="CompleteAsync"/> (design §2.13.2). Present
    /// for interface completeness; the commit path uses <see cref="PutIfAbsentAsync"/>, not this.</summary>
    private sealed class StagedWriteStream : MemoryStream, ICompletableWriteStream
    {
        private readonly InMemoryStorageBackend _backend;
        private readonly string _path;
        private bool _completed;

        public StagedWriteStream(InMemoryStorageBackend backend, string path)
        {
            _backend = backend;
            _path = path;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (!_completed)
            {
                _backend.Publish(_path, ToArray());
                _completed = true;
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The backend operations, used only to name a deterministic scheduler yield point.</summary>
internal enum StorageOp
{
    Put,
    Head,
    Read,
    ReadRange,
    List,
    Delete,
    Write,
}

/// <summary>A reproducible put-if-absent fault decision (design §3.4.1). See
/// <see cref="InMemoryStorageBackend.PutIfAbsentAsync"/> for the injected behavior of each.</summary>
internal enum FaultKind
{
    None,
    Transient,
    AmbiguousDurable,
    AmbiguousNonDurable,
}

/// <summary>A deterministic schedule of put-if-absent faults, keyed on the target <c>path</c> and the
/// 0-based <c>callIndex</c> of the put to that path (design §3.4.1: replays byte-for-byte from a seed).</summary>
internal interface IBackendFaultSchedule
{
    FaultKind NextPutFault(string path, int callIndex);
}

/// <summary>The no-fault schedule (the default): every put proceeds normally.</summary>
internal sealed class NoFaults : IBackendFaultSchedule
{
    public static readonly NoFaults Instance = new();

    private NoFaults()
    {
    }

    public FaultKind NextPutFault(string path, int callIndex) => FaultKind.None;
}

/// <summary>A fault schedule that fires a single explicit fault at the first put to one target path (and,
/// for a transient burst, the following <c>transientRuns - 1</c> puts), then behaves normally — used to
/// exercise the ambiguous-ack and bounded-transient recovery paths deterministically.</summary>
internal sealed class TargetedFaultSchedule : IBackendFaultSchedule
{
    private readonly string _targetPath;
    private readonly FaultKind _kind;
    private readonly int _runs;

    public TargetedFaultSchedule(string targetPath, FaultKind kind, int runs = 1)
    {
        _targetPath = targetPath;
        _kind = kind;
        _runs = runs;
    }

    public FaultKind NextPutFault(string path, int callIndex) =>
        string.Equals(path, _targetPath, StringComparison.Ordinal) && callIndex < _runs ? _kind : FaultKind.None;
}

/// <summary>A seeded, low-rate fault schedule keyed on (seed, path, callIndex) that injects only
/// <b>recoverable</b> faults — a bounded transient failure or a durable-but-unacknowledged ambiguous put —
/// so a correct committer still upholds every invariant while the checker proves it (design §3.4.1). The
/// decision is a pure FNV-1a hash, so it replays identically for a given base seed.</summary>
internal sealed class SeededFaultSchedule : IBackendFaultSchedule
{
    private readonly int _seed;

    public SeededFaultSchedule(int seed) => _seed = seed;

    public FaultKind NextPutFault(string path, int callIndex)
    {
        uint h = Hash(_seed, path, callIndex);
        uint bucket = h % 100u;

        // ~8% durable-ambiguous, ~8% single transient — both fully recoverable by the correct committer.
        if (bucket < 8)
        {
            return FaultKind.AmbiguousDurable;
        }

        if (bucket < 16)
        {
            return FaultKind.Transient;
        }

        return FaultKind.None;
    }

    private static uint Hash(int seed, string path, int callIndex)
    {
        const uint fnvOffsetBasis = 2166136261;
        const uint fnvPrime = 16777619;

        uint hash = fnvOffsetBasis;
        hash = Mix(hash, unchecked((uint)seed), fnvPrime);
        hash = Mix(hash, unchecked((uint)callIndex), fnvPrime);
        foreach (char c in path)
        {
            hash ^= c;
            hash *= fnvPrime;
        }

        return hash;
    }

    private static uint Mix(uint hash, uint value, uint prime)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash ^= (value >> shift) & 0xFF;
            hash *= prime;
        }

        return hash;
    }
}
