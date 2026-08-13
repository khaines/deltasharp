using DeltaSharp.Storage.Delta;

namespace DeltaSharp.Storage;

/// <summary>
/// The public failure the Delta <b>read</b> facade (<see cref="DeltaReadSource"/>, #499) raises when a
/// table cannot be opened or a snapshot cannot be resolved: the path is not a Delta table, the requested
/// version is out of range or below the retained log (a retention gap), the requested timestamp is after
/// the latest commit or before the earliest retained commit, or the log is malformed. It translates the
/// storage layer's internal <c>DeltaProtocolException</c> into a type callers across the layer boundary
/// (the Executor's file-relation resolver) can catch and re-surface as an analysis diagnostic, so a bad
/// read never reaches an execution backend.
/// </summary>
/// <remarks>
/// This facade composes its <see cref="Exception.Message"/> from an already-sanitized inner storage
/// message (<c>new DeltaReadException(inner.Message, inner)</c>); it introduces no new attacker-influenceable
/// token of its own. Note the whole-message pass-through shape is itself one of the source-scan guard's known
/// blind spots (the guard walks interpolation holes, not a pass-through argument), so this pass-through's
/// safety rests on the inner message already being sanitized, not on the guard. Producers that DO build a
/// message here via an interpolation hole are cross-checked by the source-scan guard
/// <c>StorageExceptionProducerInventoryGuardTests</c> (backed by
/// <c>storage-exception-producer-inventory.tsv</c>), which fails CI when such a token is neither
/// hygiene-wrapped nor classified in the site-keyed inventory (#749).
/// </remarks>
public sealed class DeltaReadException : Exception
{
    /// <summary>Creates a read failure with a caller-facing <paramref name="message"/>.</summary>
    /// <param name="message">The failure reason (already free of storage-internal detail).</param>
    /// <param name="innerException">The originating storage exception, if any.</param>
    public DeltaReadException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Renders this exception WITHOUT its <see cref="Exception.InnerException"/> chain (#664, RF-8b parity).
    /// This facade re-wraps an internal storage exception (whose own inner may descend to a raw Parquet.Net /
    /// JSON message over crafted bytes) as <c>new DeltaReadException(inner.Message, inner)</c>; the sanitized
    /// <see cref="Exception.Message"/> is safe to surface, but the default <c>ToString()</c> /
    /// <c>ILogger.LogError(ex, …)</c> would render the whole chain down to that raw inner. This override omits
    /// the chain; the inner remains reachable via <see cref="Exception.InnerException"/> for server-side
    /// diagnostics.
    /// </summary>
    public override string ToString() => DiagnosticText.DescribeWithoutInner(this);
}
