using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Storage;

namespace DeltaSharp.Executor;

/// <summary>
/// The Storage↔Executor <b>read</b> adapter (#499): an <see cref="IScanSource"/> that recognizes a resolved
/// Delta file scan (a <see cref="ResolvedRelation"/> carrying the reserved <see cref="DeltaReadRelation"/>
/// options the analyzer emitted) and drives the storage layer's public <see cref="DeltaReadSource"/> facade
/// to read the pinned snapshot's active files into partition-filled <see cref="ColumnBatch"/>es. It is the
/// read-side mirror of <see cref="DeltaSinkFactory"/>/<see cref="DeltaLocalSink"/> and flows through the
/// data-in <see cref="IScanSource"/> seam (NOT the write-side <see cref="CompositeSinkFactory"/>), wired as
/// a SIBLING scan-source on <see cref="DeltaStorageAdapter"/>, so the read path never restructures the
/// write path. It cleanly declines any non-Delta relation (an in-memory catalog scan), which then resolves
/// through <see cref="InMemoryScanSource"/>.
/// </summary>
internal sealed class DeltaScanSource : IScanSource
{
    /// <summary>The shared stateless instance (the facade it opens is per-scan and short-lived).</summary>
    public static DeltaScanSource Instance { get; } = new();

    /// <inheritdoc/>
    public bool TryGetBatches(ResolvedRelation relation, [NotNullWhen(true)] out IReadOnlyList<ColumnBatch>? batches)
    {
        ArgumentNullException.ThrowIfNull(relation);
        if (!DeltaReadRelation.TryGet(relation.Options, out string path, out long version))
        {
            batches = null;
            return false;
        }

        // TRACKED DEFERRAL (#508 sync-over-async; #442 unbounded materialization): IScanSource.TryGetBatches
        // is synchronous, so the async DeltaReadSource facade is driven via .GetAwaiter().GetResult() (a
        // sync-over-async bridge that also drops the run's CancellationToken — none is threaded through this
        // seam). The whole snapshot is materialized to an in-memory batch list here with no streaming/spill
        // bound. A streaming columnar scan contract that flows the token and reads incrementally is #442/#508.
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(path);
        batches = source.ReadBatchesAsync(version).GetAwaiter().GetResult();
        return true;
    }
}
