using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// An immutable node in a physical plan and the unit an <see cref="IExecutionBackend"/> evaluates
/// (STORY-03.1.1). It is a pure descriptor — schema, ordered children, and bound expressions —
/// carrying no execution state, so analyzer/optimizer immutability (copilot-instructions) holds.
/// The backend turns a node into an <see cref="IBatchStream"/>; each node carries an
/// <see cref="OperatorMetrics"/> surface the backend updates while running.
/// </summary>
public abstract class PhysicalOperator
{
    private readonly StructType _outputSchema;

    /// <summary>Initializes the base with an output schema.</summary>
    /// <param name="outputSchema">The typed schema every output batch conforms to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="outputSchema"/> is null.</exception>
    protected PhysicalOperator(StructType outputSchema)
    {
        ArgumentNullException.ThrowIfNull(outputSchema);
        _outputSchema = outputSchema;
    }

    /// <summary>The operator kind, used for dispatch and unsupported-shape diagnostics.</summary>
    public abstract OperatorKind Kind { get; }

    /// <summary>The schema of batches this operator produces (the output side of the typed contract).</summary>
    public StructType OutputSchema => _outputSchema;

    /// <summary>The ordered child operators that feed this node (empty for a scan leaf).</summary>
    public abstract IReadOnlyList<PhysicalOperator> Children { get; }

    /// <summary>The per-instance metrics surface a backend fills while this node runs.</summary>
    public OperatorMetrics Metrics { get; } = new();

    /// <summary>The input schema of child <paramref name="ordinal"/> (the input side of the typed contract).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is outside <c>[0, Children.Count)</c>.</exception>
    public StructType InputSchema(int ordinal)
    {
        if ((uint)ordinal >= (uint)Children.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, $"Operator '{Kind}' has {Children.Count} input(s).");
        }

        return Children[ordinal].OutputSchema;
    }
}
