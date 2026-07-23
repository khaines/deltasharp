using System.Collections.Immutable;

namespace DeltaSharp.Storage;

/// <summary>
/// The non-forgeable proof that a <see cref="DeltaChangeFeedInfo"/> was produced by
/// <see cref="DeltaReadSource.LoadChangeFeedAsync"/> — i.e. that its <c>[start, end]</c> range passed the full
/// resolve-time validation (bounds, availability, and CRUCIALLY the §2.7 conservative "CDF active for EVERY
/// version in the range" gate). It is <see langword="internal"/> with no public constructor, so a consumer
/// CANNOT fabricate one: an info built via the public <see cref="DeltaChangeFeedInfo"/> constructor (or
/// <c>default</c>) carries a <see langword="null"/> resolution and is rejected fail-closed by
/// <see cref="DeltaReadSource.ReadChangeBatchesAsync"/> before any I/O, so a manually-constructed info can
/// never bypass validation and surface change rows from an unvalidated (e.g. CDF-disabled) range.
///
/// <para>It also carries the pinned effective-<c>&lt;N&gt;.json</c>-mtime map (item 4 / query-exec L2): the
/// timeline snapshot captured at resolve time, from which <c>_commit_timestamp</c> is stamped at read time, so
/// an intervening log-cleanup — which can advance the earliest reconstructable floor between resolve and read
/// — cannot shift a near-floor version's stamped timestamp (§2.8).</para>
/// </summary>
internal sealed class ChangeFeedResolution
{
    internal ChangeFeedResolution(ImmutableSortedDictionary<long, long> commitMillisByVersion) =>
        CommitMillisByVersion = commitMillisByVersion;

    /// <summary>
    /// The effective <c>&lt;N&gt;.json</c> mtime (epoch millis) for every version in the resolved
    /// <c>[start, end]</c> range, PINNED at resolve time. <c>_commit_timestamp</c> is stamped from THIS map at
    /// read time (never re-derived), so the timestamp lane is pinned exactly like the versions and rows.
    /// </summary>
    internal ImmutableSortedDictionary<long, long> CommitMillisByVersion { get; }
}
