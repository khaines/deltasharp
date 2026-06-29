namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The closed set of v1 physical operators an <see cref="IExecutionBackend"/> evaluates
/// (EPIC-03 / STORY-03.1.1). Each value pairs with a <see cref="PhysicalOperator"/> subtype and a
/// typed input/output <see cref="DeltaSharp.Engine.Columnar.ColumnBatch"/> contract; a backend
/// that cannot evaluate a given kind raises an <see cref="UnsupportedOperatorException"/> rather
/// than degrading to a row-at-a-time fallback.
/// </summary>
public enum OperatorKind
{
    /// <summary>A leaf that produces batches from a source relation (no operator children).</summary>
    Scan,

    /// <summary>Selects passing rows via a boolean predicate; output schema equals input schema.</summary>
    Filter,

    /// <summary>Computes output columns from expressions; output schema is the projection list.</summary>
    Project,

    /// <summary>Groups rows and computes aggregates; output schema is group keys plus aggregates.</summary>
    Aggregate,

    /// <summary>Reorders rows by a sort specification; output schema equals input schema.</summary>
    Sort,

    /// <summary>Combines two inputs on join keys; output schema depends on the join type.</summary>
    Join,

    /// <summary>Repartitions a single input across local partitions; output schema equals input schema.</summary>
    ExchangeLocal,
}
