using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// Resolves a scanned <see cref="ResolvedRelation"/> to a <b>deferred</b> batch factory the physical
/// planner builds a leaf scan over. It is the M1 data-in seam: the public read-door
/// (<c>createDataFrame</c>/file scans) is STORY-04.1.2 (#158) and registers batches (or a real reader)
/// through this same shape.
/// </summary>
/// <remarks>
/// A source yields a <see cref="Func{T, TResult}">Func&lt;CancellationToken, IReadOnlyList&lt;ColumnBatch&gt;&gt;</see>
/// rather than materialized batches so the read stays <b>lazy</b>: the factory is invoked only at
/// <see cref="ScanPlan.Execute"/> (under the run's cancellation token and memory/byte budget), so
/// <see cref="PhysicalPlanner.Plan"/> — and thus #179 <c>Explain</c> — performs NO data-plane I/O. An
/// already-materialized source wraps its batches in a trivial thunk; a real reader (the Delta scan-source)
/// defers the file reads into the thunk. The factory shape stays reflection-free (NativeAOT-safe).
/// </remarks>
internal interface IScanSource
{
    /// <summary>Tries to resolve <paramref name="relation"/> to a deferred factory producing its source batches.</summary>
    /// <param name="relation">The scanned relation.</param>
    /// <param name="batchFactory">The deferred batch factory when found; invoked at execution with the run's
    /// cancellation token.</param>
    /// <returns><see langword="true"/> if the relation is registered; otherwise <see langword="false"/>.</returns>
    bool TryGetBatches(
        ResolvedRelation relation,
        [NotNullWhen(true)] out Func<CancellationToken, IReadOnlyList<ColumnBatch>>? batchFactory);
}

/// <summary>
/// An <see cref="IScanSource"/> backed by an in-process registry keyed by a relation's identifier.
/// Tests (and, until #158, any data-in path) register a relation's schema and batches, then run an
/// analyzed plan whose scan resolves against this source. Registration validates that every batch
/// conforms to the registered schema so a fixture cannot smuggle a mismatched batch into execution.
/// </summary>
internal sealed class InMemoryScanSource : IScanSource
{
    // Concurrent because the process-global Default is shared across threads (parallel test hosts, and
    // any future concurrent data-in path); registration and resolution must stay thread-safe.
    private readonly ConcurrentDictionary<string, IReadOnlyList<ColumnBatch>> _byIdentifier = new(StringComparer.Ordinal);

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
    public bool TryGetBatches(
        ResolvedRelation relation,
        [NotNullWhen(true)] out Func<CancellationToken, IReadOnlyList<ColumnBatch>>? batchFactory)
    {
        ArgumentNullException.ThrowIfNull(relation);
        if (_byIdentifier.TryGetValue(Key(relation.Identifier), out IReadOnlyList<ColumnBatch>? batches))
        {
            // The batches are already materialized; wrap them in a trivial thunk so the leaf scan honors
            // the deferred-factory contract uniformly (nothing to defer, no I/O).
            batchFactory = _ => batches;
            return true;
        }

        batchFactory = null;
        return false;
    }

    private static string Key(IReadOnlyList<string> identifier) => string.Join('.', identifier);
}
