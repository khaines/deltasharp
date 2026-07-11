using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Executor;

/// <summary>
/// An <see cref="IScanSource"/> that resolves a scanned relation against an ordered list of sources, using
/// the first that recognizes it — the read-side mirror of <see cref="CompositeSinkFactory"/>. It composes
/// the Delta read adapter (<see cref="DeltaScanSource"/>) with the in-memory scan source
/// (<see cref="InMemoryScanSource"/>): a resolved Delta file scan is read from a real table, and any other
/// relation (an in-memory catalog fixture) resolves against the registry, so wiring the read door never
/// restructures the existing in-memory data-in path.
/// </summary>
internal sealed class CompositeScanSource : IScanSource
{
    private readonly IReadOnlyList<IScanSource> _sources;

    /// <summary>Creates a composite over <paramref name="sources"/>, tried in order.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> (or an element) is null.</exception>
    public CompositeScanSource(params IScanSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (IScanSource source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
        }

        _sources = sources;
    }

    /// <inheritdoc/>
    public bool TryGetBatches(
        ResolvedRelation relation,
        [NotNullWhen(true)] out Func<CancellationToken, IReadOnlyList<ColumnBatch>>? batchFactory)
    {
        ArgumentNullException.ThrowIfNull(relation);
        foreach (IScanSource source in _sources)
        {
            if (source.TryGetBatches(relation, out batchFactory))
            {
                return true;
            }
        }

        batchFactory = null;
        return false;
    }
}
