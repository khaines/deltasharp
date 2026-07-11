using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Storage;

namespace DeltaSharp.Executor;

/// <summary>
/// The Storage↔Executor <b>read</b> adapter (#499): an <see cref="IScanSource"/> that recognizes a resolved
/// Delta file scan (a <see cref="ResolvedRelation"/> carrying the reserved <see cref="DeltaReadRelation"/>
/// options the analyzer emitted) and drives the storage layer's public <see cref="DeltaReadSource"/> facade
/// to read the pinned snapshot's active files into partition-filled <see cref="ColumnBatch"/>es. The read
/// is <b>deferred</b> into the factory the <see cref="ScanPlan"/> invokes at execution (never at plan time),
/// so <see cref="PhysicalPlanner.Plan"/>/<c>Explain</c> do no data-plane I/O and the read runs under the
/// run's cancellation token and memory/byte budget. It is the read-side mirror of <see cref="DeltaSinkFactory"/>/<see cref="DeltaLocalSink"/> and flows through the
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
    public bool TryGetBatches(
        ResolvedRelation relation,
        [NotNullWhen(true)] out Func<CancellationToken, IReadOnlyList<ColumnBatch>>? batchFactory)
    {
        ArgumentNullException.ThrowIfNull(relation);
        if (!DeltaReadRelation.TryGet(relation.Options, out string path, out long version))
        {
            batchFactory = null;
            return false;
        }

        // LAZY SCAN (#499 F1): defer the data-plane read into the factory the ScanPlan invokes at
        // Execute — NOT at plan time. This keeps the lazy/eager invariant (Plan/Explain do NO file I/O),
        // and, because the factory runs under the runtime, the read is cancellable via the run's
        // CancellationToken (threaded into ReadBatchesAsync below, resolving the dropped-token deferral for
        // the scan path) and its bytes are counted against the run's accounting — post-materialization, not
        // bounded pre-materialization (see the #442 deferral below). The facade opened here is per-scan and
        // short-lived. The closure is a plain delegate (no reflection/codegen), so it stays NativeAOT-safe.
        //
        // TRACKED DEFERRAL (#508 sync-over-async; #442 unbounded materialization): the async facade is
        // still driven via .GetAwaiter().GetResult() (the IScanSource seam is synchronous), and the whole
        // snapshot is materialized to an in-memory batch list with no streaming/spill bound. A streaming
        // columnar scan contract that reads incrementally is #442/#508.
        batchFactory = token =>
        {
            using DeltaReadSource source = DeltaReadSource.ForLocalPath(path);
            return source.ReadBatchesAsync(version, token).GetAwaiter().GetResult();
        };
        return true;
    }
}
