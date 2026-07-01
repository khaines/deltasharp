using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// A leaf <see cref="PhysicalOperator"/> (<see cref="OperatorKind.Scan"/>) whose source is a fixed,
/// already-materialized list of <see cref="ColumnBatch"/>es held in memory — the <b>v1 executable
/// scan</b> (STORY-03.2.1). It exists because the abstract <see cref="ScanOperator"/> binds a source
/// only by opaque id; this node carries the actual batches so the interpreted backend can stream
/// them without a storage round-trip. Each batch's schema must equal <see cref="PhysicalOperator.OutputSchema"/>,
/// so the typed scan contract is verified at construction.
/// </summary>
/// <remarks>
/// This is deliberately the simplest faithful scan source: real Parquet/Delta and connector scans
/// (predicate/partition pushdown, column pruning, data skipping, byte-accurate scan accounting) are
/// owned by the storage-format and connector layers and arrive as separate scan-source bindings
/// behind this same <see cref="OperatorKind.Scan"/> shape. The backend yields the batches verbatim
/// — preserving row count, column ordering, null metadata, and any pre-existing selection — so a
/// caller can assert the emitted output against the exact batches it supplied.
/// </remarks>
public sealed class InMemoryScanOperator : PhysicalOperator
{
    private readonly ColumnBatch[] _batches;

    /// <summary>Creates an in-memory scan over <paramref name="batches"/> producing <paramref name="outputSchema"/>.</summary>
    /// <param name="outputSchema">The schema every emitted batch must conform to.</param>
    /// <param name="batches">The source batches, streamed in order (may be empty; batches may be empty).</param>
    /// <param name="sourceId">A stable identifier for diagnostics/EXPLAIN (defaults to <c>memory</c>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="outputSchema"/>, <paramref name="batches"/>, or any batch is null.</exception>
    /// <exception cref="ArgumentException">A batch's schema does not equal <paramref name="outputSchema"/>.</exception>
    public InMemoryScanOperator(StructType outputSchema, IReadOnlyList<ColumnBatch> batches, string sourceId = "memory")
        : base(outputSchema)
    {
        ArgumentNullException.ThrowIfNull(batches);

        // OutputSchema (the base property) is non-null here: the base ctor already rejected a null
        // schema, so use it rather than the parameter to keep the nullable analysis clean.
        StructType schema = OutputSchema;
        var copy = new ColumnBatch[batches.Count];
        for (int i = 0; i < batches.Count; i++)
        {
            ColumnBatch batch = batches[i]
                ?? throw new ArgumentNullException(nameof(batches), $"Scan source batch {i} is null.");
            if (!batch.Schema.Equals(schema))
            {
                throw new ArgumentException(
                    $"Scan source batch {i} has schema '{batch.Schema.SimpleString}' but the scan output schema is "
                    + $"'{schema.SimpleString}'.", nameof(batches));
            }

            copy[i] = batch;
        }

        _batches = copy;
        SourceId = string.IsNullOrEmpty(sourceId) ? "memory" : sourceId;
    }

    /// <inheritdoc />
    public override OperatorKind Kind => OperatorKind.Scan;

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalOperator> Children => Array.Empty<PhysicalOperator>();

    /// <summary>The in-memory source batches, streamed in order (each conforms to <see cref="PhysicalOperator.OutputSchema"/>).</summary>
    public IReadOnlyList<ColumnBatch> Batches => _batches;

    /// <summary>A stable identifier for the bound source (diagnostics/EXPLAIN).</summary>
    public string SourceId { get; }
}
