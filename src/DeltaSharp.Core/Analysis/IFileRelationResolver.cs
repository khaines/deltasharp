using DeltaSharp.Types;

namespace DeltaSharp.Analysis;

/// <summary>
/// The Core-owned dependency-inversion seam through which the <see cref="Analyzer"/> resolves a
/// path-based file-format scan (an <see cref="DeltaSharp.Plans.Logical.UnresolvedFileRelation"/>) to its
/// concrete <b>schema</b> and, for a Delta source, the <b>resolved snapshot version</b> — the read-door
/// counterpart of the write-door's <see cref="DeltaSharp.Execution.IQueryExecutor"/> seam (#499).
/// </summary>
/// <remarks>
/// <para>
/// Core builds and analyzes logical plans but cannot open a Delta table: the storage layer lives in
/// <c>DeltaSharp.Storage</c> (which Core may not reference — ADR-0014). Core therefore defines this
/// <b>internal</b> contract and the Executor lane (#499) implements it in <c>DeltaSharp.Executor</c>
/// (driving the public <c>DeltaReadSource</c> storage facade), reachable because Core grants
/// <c>InternalsVisibleTo("DeltaSharp.Executor")</c>. It is <see langword="internal"/> because it returns
/// the internal <see cref="StructType"/>-bearing resolution and is consumed only by the analyzer; it can
/// never be public.
/// </para>
/// <para>
/// Resolution is an <b>eager metadata read</b>: it is invoked only from the analyze pass (an action's
/// crossing into execution — the lazy <c>load()</c> that built the unresolved node did <b>no</b> I/O), it
/// reads the Delta log to bind the schema, and it <b>pins the resolved version</b> so the executor's scan
/// reads that exact snapshot (no analysis→execution TOCTOU even under a concurrent write). A resolution
/// failure (not a Delta table, an out-of-range/retention-gap version, a timestamp after the latest commit)
/// surfaces as an <see cref="AnalysisException"/>, so it can never reach an execution backend and EXPLAIN
/// degrades to a diagnostic line (AC4 parity).
/// </para>
/// </remarks>
internal interface IFileRelationResolver
{
    /// <summary>
    /// Tries to resolve <paramref name="request"/> to its schema and (for Delta) resolved version.
    /// Returns <see langword="false"/> for a format this resolver does not handle (for example
    /// <c>parquet</c>, whose reader is deferred — the analyzer then raises the deferred-source
    /// diagnostic), so an unhandled format never masquerades as a resolution failure.
    /// </summary>
    /// <param name="request">The file-format scan to resolve.</param>
    /// <param name="resolution">The bound schema + resolved read metadata when handled.</param>
    /// <returns><see langword="true"/> if this resolver handled <paramref name="request"/>'s format.</returns>
    /// <exception cref="AnalysisException">The format is handled but resolution failed (no table,
    /// out-of-range/retention-gap version, timestamp out of range, or a malformed log).</exception>
    bool TryResolve(FileRelationResolutionRequest request, out FileRelationResolution resolution);
}

/// <summary>
/// A normalized file-format scan to resolve: the <see cref="Format"/>, the (time-travel-suffix-stripped)
/// <see cref="Path"/>, an optional pinned <see cref="VersionAsOf"/> XOR <see cref="TimestampAsOf"/>
/// (mutually exclusive — the analyzer has already rejected specifying both), and any user
/// <see cref="UserSchema"/>. Time-travel option/path parsing and the both-specified error live in Core
/// (Spark semantics); the resolver only maps a valid spec onto a snapshot.
/// </summary>
/// <param name="Format">The data-source format (for example <c>delta</c>).</param>
/// <param name="Path">The table path, with any <c>@v…</c>/<c>@…</c> time-travel suffix already stripped.</param>
/// <param name="VersionAsOf">A pinned exact version, or <see langword="null"/>.</param>
/// <param name="TimestampAsOf">A pinned timestamp (UTC), or <see langword="null"/>.</param>
/// <param name="UserSchema">A user-specified read schema, or <see langword="null"/>.</param>
internal readonly record struct FileRelationResolutionRequest(
    string Format,
    string Path,
    long? VersionAsOf,
    DateTimeOffset? TimestampAsOf,
    StructType? UserSchema);

/// <summary>
/// The bound result of resolving a file-format scan: the table <see cref="Schema"/> the analyzer derives
/// output attributes from, and the <see cref="ResolvedVersion"/> the executor's scan reads (the exact
/// version for <c>versionAsOf</c>, the resolved version for <c>timestampAsOf</c>, or the latest committed
/// version for a base read).
/// </summary>
/// <param name="Schema">The resolved table schema.</param>
/// <param name="ResolvedVersion">The pinned snapshot version the scan reads.</param>
internal sealed record FileRelationResolution(StructType Schema, long ResolvedVersion);
