using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// A leaf operator that produces batches from a source relation. It has no operator children; its
/// data comes from a scan source bound by the storage/connector layer. The contract is the output
/// schema and an opaque <see cref="SourceId"/>; predicate/projection pushdown into the source is a
/// later concern, surfaced through the source binding, not this node.
/// </summary>
public sealed class ScanOperator : PhysicalOperator
{
    /// <summary>Creates a scan over a source with the given output schema.</summary>
    /// <param name="outputSchema">The schema the source yields.</param>
    /// <param name="sourceId">A stable identifier for the bound source (for diagnostics/EXPLAIN).</param>
    /// <exception cref="ArgumentNullException"><paramref name="outputSchema"/> is null.</exception>
    public ScanOperator(StructType outputSchema, string sourceId = "")
        : base(outputSchema)
    {
        SourceId = sourceId ?? string.Empty;
    }

    /// <inheritdoc />
    public override OperatorKind Kind => OperatorKind.Scan;

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalOperator> Children => Array.Empty<PhysicalOperator>();

    /// <summary>A stable identifier of the bound scan source.</summary>
    public string SourceId { get; }
}
