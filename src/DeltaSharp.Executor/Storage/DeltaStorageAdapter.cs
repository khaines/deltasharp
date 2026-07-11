namespace DeltaSharp.Executor;

/// <summary>
/// The Storage↔Executor integration seam (#487): the single composition point where storage-backed
/// providers are registered into the Executor's write/read plumbing. Today it registers the Delta
/// <b>write</b> sink (<see cref="DeltaSinkFactory"/>) alongside the in-memory sink
/// (<see cref="InMemorySinkRegistry.Default"/>). The base Delta <b>read</b> provider (#499) registers here
/// too, next to the write sink, so neither path restructures the other.
/// </summary>
internal static class DeltaStorageAdapter
{
    /// <summary>The process-wide sink factory the auto-registered executor uses: the in-memory sink plus the
    /// Delta write sink, tried in order (each recognizes exactly its own format).</summary>
    public static ILocalSinkFactory DefaultSinkFactory { get; } =
        new CompositeSinkFactory(InMemorySinkRegistry.Default, DeltaSinkFactory.Instance);
}
