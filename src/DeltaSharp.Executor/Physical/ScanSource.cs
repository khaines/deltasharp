using DeltaSharp.Engine.Columnar;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// Resolves a scanned <see cref="ResolvedRelation"/> to the in-memory batches the physical planner
/// builds a leaf scan over. It is the M1 data-in seam: the public read-door
/// (<c>createDataFrame</c>/file scans) is STORY-04.1.2 (#158) and will register batches (or a real
/// reader) through this same shape.
/// </summary>
internal interface IScanSource
{
    /// <summary>Tries to resolve <paramref name="relation"/> to its source batches.</summary>
    /// <param name="relation">The scanned relation.</param>
    /// <param name="batches">The resolved batches when found.</param>
    /// <returns><see langword="true"/> if the relation is registered; otherwise <see langword="false"/>.</returns>
    bool TryGetBatches(ResolvedRelation relation, out IReadOnlyList<ColumnBatch> batches);
}

/// <summary>
/// An <see cref="IScanSource"/> backed by an in-process registry keyed by a relation's identifier.
/// Tests (and, until #158, any data-in path) register a relation's schema and batches, then run an
/// analyzed plan whose scan resolves against this source. Registration validates that every batch
/// conforms to the registered schema so a fixture cannot smuggle a mismatched batch into execution.
/// </summary>
internal sealed class InMemoryScanSource : IScanSource
{
    private readonly Dictionary<string, IReadOnlyList<ColumnBatch>> _byIdentifier = new(StringComparer.Ordinal);

    /// <summary>A process-wide default source the auto-registered <see cref="LocalQueryExecutor"/> reads from.</summary>
    public static InMemoryScanSource Default { get; } = new();

    /// <summary>Registers the batches that back a relation identifier.</summary>
    /// <param name="identifier">The relation identifier (for example <c>["people"]</c>).</param>
    /// <param name="schema">The relation schema every batch must conform to.</param>
    /// <param name="batches">The source batches, streamed in order.</param>
    /// <exception cref="ArgumentException">A batch's schema does not equal <paramref name="schema"/>.</exception>
    public void Register(IReadOnlyList<string> identifier, StructType schema, IReadOnlyList<ColumnBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(batches);

        foreach (ColumnBatch batch in batches)
        {
            if (!batch.Schema.Equals(schema))
            {
                throw new ArgumentException(
                    $"Batch schema '{batch.Schema.SimpleString}' does not equal registered schema '{schema.SimpleString}'.",
                    nameof(batches));
            }
        }

        _byIdentifier[Key(identifier)] = batches;
    }

    /// <inheritdoc />
    public bool TryGetBatches(ResolvedRelation relation, out IReadOnlyList<ColumnBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(relation);
        return _byIdentifier.TryGetValue(Key(relation.Identifier), out batches!);
    }

    private static string Key(IReadOnlyList<string> identifier) => string.Join('.', identifier);
}
