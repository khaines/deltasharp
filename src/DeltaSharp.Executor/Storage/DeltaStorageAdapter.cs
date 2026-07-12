namespace DeltaSharp.Executor;

/// <summary>
/// The Storage↔Executor integration seam (#487, #499): the single composition point where storage-backed
/// providers are registered into the Executor's write/read plumbing. It exposes the Delta <b>write</b> sink
/// (<see cref="DeltaSinkFactory"/>) alongside the in-memory sink (<see cref="InMemorySinkRegistry.Default"/>)
/// via <see cref="DefaultSinkFactory"/>, and — as a SIBLING seam — the base Delta <b>read</b> provider
/// (<see cref="DefaultScanSource"/>) plus the read-door resolver (<see cref="FileRelationResolver"/>). The
/// read provider is a scan-source property, NOT an entry in the <see cref="CompositeSinkFactory"/>: reads
/// flow through the <see cref="IScanSource"/> data-in seam, writes through the <see cref="ILocalSinkFactory"/>
/// data-out seam. Keeping them as distinct properties on this adapter means neither path restructures the
/// other.
/// </summary>
internal static class DeltaStorageAdapter
{
    /// <summary>The process-wide sink factory the auto-registered executor uses: the in-memory sink plus the
    /// Delta write sink, tried in order (each recognizes exactly its own format).</summary>
    public static ILocalSinkFactory DefaultSinkFactory { get; } =
        new CompositeSinkFactory(InMemorySinkRegistry.Default, DeltaSinkFactory.Instance);

    /// <summary>The process-wide scan source the auto-registered executor reads through: the Delta read
    /// adapter (<see cref="DeltaScanSource"/>) composed with the in-memory scan source
    /// (<see cref="InMemoryScanSource.Default"/>), tried in order — the SIBLING data-in seam of
    /// <see cref="DefaultSinkFactory"/>. A resolved Delta file scan reads a real table; any other relation
    /// resolves against the in-memory registry.</summary>
    public static IScanSource DefaultScanSource { get; } =
        new CompositeScanSource(DeltaScanSource.Instance, InMemoryScanSource.Default);

    /// <summary>The process-wide read-door resolver the analyzer binds a <c>delta</c> path scan through
    /// (#499): the <see cref="DeltaFileRelationResolver"/> over the public storage read facade.</summary>
    public static Analysis.IFileRelationResolver FileRelationResolver { get; } =
        DeltaFileRelationResolver.Instance;
}
