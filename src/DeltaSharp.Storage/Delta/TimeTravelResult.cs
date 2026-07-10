namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The outcome of a <b>timestamp</b> time-travel load (design §2.12.1; STORY-05.4.1 AC2). Unlike a
/// version load — where the caller already knows the target version — a <c>timestampAsOf</c> request must
/// <b>resolve</b> the timestamp to a concrete version and report which one was selected, so the caller can
/// record/log the effective version (Spark-parity: this maps to <c>DataFrameReader.option("timestampAsOf", …)</c>,
/// whose resolved version is surfaced to the query planner). <see cref="ResolvedVersion"/> always equals
/// <see cref="Delta.Snapshot.Version"/> of <see cref="Snapshot"/>.
/// </summary>
/// <param name="Snapshot">The reconstructed, immutable snapshot at the resolved version.</param>
/// <param name="ResolvedVersion">The Delta version the requested timestamp resolved to (the latest commit
/// whose effective commit timestamp is at or before the requested instant).</param>
internal sealed record TimeTravelResult(Snapshot Snapshot, long ResolvedVersion);
