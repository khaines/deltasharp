namespace DeltaSharp.Executor;

/// <summary>
/// The Storage↔Executor integration seam (#487): the single composition point where storage-backed
/// providers are registered into the Executor's write/read plumbing. Today it exposes the Delta
/// <b>write</b> sink (<see cref="DeltaSinkFactory"/>) alongside the in-memory sink
/// (<see cref="InMemorySinkRegistry.Default"/>) via <see cref="DefaultSinkFactory"/>. The base Delta
/// <b>read</b> provider (#499) is a <i>separate</i> seam: it will be a SIBLING scan-source property here
/// (a <c>DefaultScanSource</c>/<c>CompositeScanSource</c> composing the Delta <see cref="IScanSource"/>),
/// NOT an entry in the <see cref="CompositeSinkFactory"/> — reads flow through the <see cref="IScanSource"/>
/// data-in seam, writes through the <see cref="ILocalSinkFactory"/> data-out seam. Keeping them as distinct
/// properties on this adapter means neither path restructures the other.
/// </summary>
internal static class DeltaStorageAdapter
{
    /// <summary>The process-wide sink factory the auto-registered executor uses: the in-memory sink plus the
    /// Delta write sink, tried in order (each recognizes exactly its own format).</summary>
    public static ILocalSinkFactory DefaultSinkFactory { get; } =
        new CompositeSinkFactory(InMemorySinkRegistry.Default, DeltaSinkFactory.Instance);
}
