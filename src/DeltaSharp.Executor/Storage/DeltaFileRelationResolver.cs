using DeltaSharp.Analysis;
using DeltaSharp.Storage;

namespace DeltaSharp.Executor;

/// <summary>
/// The Storage↔Executor <b>read-resolution</b> adapter (#499): the Core-owned
/// <see cref="IFileRelationResolver"/> the analyzer binds a <c>delta</c> path scan through, implemented by
/// driving the public <see cref="DeltaReadSource"/> facade to read the Delta log — binding the schema and
/// <b>pinning the snapshot version</b> (the exact version for <c>versionAsOf</c>, the resolved version for
/// <c>timestampAsOf</c>, or the latest committed version for a base read). It is the read-side mirror of the
/// write door's analyzer pass-through: the write door validates the sink format in the analyzer and resolves
/// the concrete writer at execution; the read door resolves the schema + version in the analyzer (a read
/// needs the schema to bind columns) and reads the pinned version at execution through
/// <see cref="DeltaScanSource"/>. A storage-side failure (not a Delta table, out-of-range/retention-gap
/// version, timestamp out of range) is translated to an <see cref="AnalysisException"/> so it surfaces as an
/// analysis diagnostic and never reaches an execution backend.
/// </summary>
internal sealed class DeltaFileRelationResolver : IFileRelationResolver
{
    /// <summary>The shared stateless instance.</summary>
    public static DeltaFileRelationResolver Instance { get; } = new();

    /// <inheritdoc/>
    public bool TryResolve(FileRelationResolutionRequest request, out FileRelationResolution resolution)
    {
        if (!string.Equals(request.Format, DeltaReadRelationFormat, StringComparison.OrdinalIgnoreCase))
        {
            // Not a format this resolver handles (only delta today); the analyzer defers the rest to EPIC-05.
            resolution = null!;
            return false;
        }

        try
        {
            // TRACKED DEFERRAL (#508 sync-over-async): the analyzer's resolve seam is synchronous, so the
            // async facade is driven via .GetAwaiter().GetResult(). This is a bounded metadata read (list the
            // log, reconstruct one snapshot's header) — no data-plane I/O — so the sync bridge is cheap.
            using DeltaReadSource source = DeltaReadSource.ForLocalPath(request.Path);
            DeltaSnapshotInfo info = source
                .LoadSnapshotAsync(request.VersionAsOf, request.TimestampAsOf)
                .GetAwaiter().GetResult();
            resolution = new FileRelationResolution(info.Schema, info.Version);
            return true;
        }
        catch (DeltaReadException ex)
        {
            throw AnalysisException.FileSourceResolutionFailed(request.Format, request.Path, ex.Message);
        }
    }

    // The one read format this resolver handles. Kept local to the Executor adapter (the Core marker lives
    // in DeltaReadRelation) so the two never disagree on the literal.
    private const string DeltaReadRelationFormat = "delta";
}
