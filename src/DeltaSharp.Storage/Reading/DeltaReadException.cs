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
public sealed class DeltaReadException : Exception
{
    /// <summary>Creates a read failure with a caller-facing <paramref name="message"/>.</summary>
    /// <param name="message">The failure reason (already free of storage-internal detail).</param>
    /// <param name="innerException">The originating storage exception, if any.</param>
    public DeltaReadException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
