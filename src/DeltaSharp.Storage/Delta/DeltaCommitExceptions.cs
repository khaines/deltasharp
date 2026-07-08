namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The classified reason a Delta commit lost the optimistic-concurrency race in a way that could not be
/// safely rebased (design §2.11.2 conflict matrix). Callers branch on
/// <see cref="DeltaConcurrentModificationException.Kind"/> rather than the concrete type when a coarse
/// classification suffices.
/// </summary>
internal enum DeltaConflictKind
{
    /// <summary>A concurrent winner <c>add</c> landed in this writer's read/overwrite scope
    /// (<see cref="ConcurrentAppendException"/>).</summary>
    ConcurrentAppend,

    /// <summary>A concurrent winner <c>remove</c> deleted a file this writer read
    /// (<see cref="ConcurrentDeleteReadException"/>).</summary>
    ConcurrentDeleteRead,

    /// <summary>A concurrent winner changed the table <c>metaData</c>
    /// (<see cref="MetadataChangedException"/>).</summary>
    MetadataChanged,

    /// <summary>A concurrent winner changed the table <c>protocol</c>
    /// (<see cref="ProtocolChangedException"/>).</summary>
    ProtocolChanged,

    /// <summary>A concurrent winner committed a <c>txn</c> for the same <c>appId</c>
    /// (<see cref="ConcurrentTransactionException"/>).</summary>
    ConcurrentTransaction,
}

/// <summary>
/// The base type for a Delta optimistic-concurrency conflict: this writer's read scope logically overlaps a
/// commit that landed since its read snapshot, so its commit was <b>aborted</b> rather than rebased (design
/// §2.11.2). The concrete subtypes mirror Delta's exact exception names so behavior ports 1:1 from Spark
/// Delta. Distinct from <see cref="DeltaCommitUnknownStateException"/>, which is an <i>unresolved</i>
/// outcome, and from a rebase-and-retry, which is not an exception at all.
/// </summary>
internal abstract class DeltaConcurrentModificationException : Exception
{
    private protected DeltaConcurrentModificationException(DeltaConflictKind kind, string message)
        : base(message) => Kind = kind;

    /// <summary>The coarse classification of the conflict.</summary>
    public DeltaConflictKind Kind { get; }
}

/// <summary>A concurrent commit added files into this writer's read/overwrite scope (Delta's
/// <c>ConcurrentAppendException</c>). The writer must re-read and re-evaluate before retrying.</summary>
internal sealed class ConcurrentAppendException : DeltaConcurrentModificationException
{
    public ConcurrentAppendException(string message)
        : base(DeltaConflictKind.ConcurrentAppend, message)
    {
    }
}

/// <summary>A concurrent commit removed a file this writer read (Delta's
/// <c>ConcurrentDeleteReadException</c>): the writer's decision was based on data that no longer exists.</summary>
internal sealed class ConcurrentDeleteReadException : DeltaConcurrentModificationException
{
    public ConcurrentDeleteReadException(string message)
        : base(DeltaConflictKind.ConcurrentDeleteRead, message)
    {
    }
}

/// <summary>A concurrent commit changed the table <c>metaData</c> (Delta's <c>MetadataChangedException</c>).
/// Any concurrent metadata change aborts this writer — the winner-driven rule of the conflict matrix.</summary>
internal sealed class MetadataChangedException : DeltaConcurrentModificationException
{
    public MetadataChangedException(string message)
        : base(DeltaConflictKind.MetadataChanged, message)
    {
    }
}

/// <summary>A concurrent commit changed the table <c>protocol</c> (Delta's <c>ProtocolChangedException</c>).
/// Any concurrent protocol change aborts this writer — the winner-driven rule of the conflict matrix.</summary>
internal sealed class ProtocolChangedException : DeltaConcurrentModificationException
{
    public ProtocolChangedException(string message)
        : base(DeltaConflictKind.ProtocolChanged, message)
    {
    }
}

/// <summary>A concurrent commit recorded a <c>txn</c> for the same <c>appId</c> as this writer (Delta's
/// <c>ConcurrentTransactionException</c>) — two streams sharing an application id raced.</summary>
internal sealed class ConcurrentTransactionException : DeltaConcurrentModificationException
{
    public ConcurrentTransactionException(string message)
        : base(DeltaConflictKind.ConcurrentTransaction, message)
    {
    }
}

/// <summary>
/// The commit outcome could <b>not</b> be resolved to committed-or-not (design §2.11.3 "ambiguous success"
/// that recovery could not classify, §2.11.6). Raised only after the writer re-read <c>&lt;N&gt;.json</c>
/// and still could not prove whether its own commit landed — never a silent success or a blind retry that
/// could double-commit. This is a <b>non-retryable, precise unknown-state</b> error (STORY-05.3.1 AC4): the
/// caller must reconcile out of band, not guess.
/// </summary>
internal sealed class DeltaCommitUnknownStateException : Exception
{
    public DeltaCommitUnknownStateException(long version, string message, Exception? innerException = null)
        : base(message, innerException) => Version = version;

    /// <summary>The commit version whose durability could not be determined.</summary>
    public long Version { get; }
}

/// <summary>
/// The commit could not be published within the writer's rebase-retry budget under sustained concurrency
/// (design §2.11.3 "definite conflict" repeated past <see cref="MaxAttempts"/>). Unlike
/// <see cref="DeltaCommitUnknownStateException"/> this is a <b>known, retryable</b> outcome: the commit
/// provably did <b>not</b> land (every exhausted attempt ended in a lost race or a safe rebase, never a
/// durable put), so the caller may safely retry from a fresh snapshot. It is surfaced rather than looping
/// forever so pathological contention is visible instead of manifesting as a hang.
/// </summary>
internal sealed class DeltaCommitContentionException : Exception
{
    public DeltaCommitContentionException(long version, int maxAttempts, string message)
        : base(message)
    {
        Version = version;
        MaxAttempts = maxAttempts;
    }

    /// <summary>The version the writer was attempting to publish when it exhausted its retry budget.</summary>
    public long Version { get; }

    /// <summary>The rebase-retry budget that was exhausted.</summary>
    public int MaxAttempts { get; }
}
