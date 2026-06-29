using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Groups its single input by <see cref="GroupingKeys"/> (empty for a global aggregate) and emits
/// one row per group with <see cref="Aggregates"/> appended after the keys. Output schema is keys
/// followed by aggregates, so its field count equals key count plus aggregate count. Partial/final
/// merge, null groups, empty input, and overflow behaviour follow the scalar oracle (checklist 21).
/// </summary>
public sealed class AggregateOperator : PhysicalOperator
{
    private readonly PhysicalOperator[] _children;
    private readonly PhysicalExpression[] _groupingKeys;
    private readonly PhysicalExpression[] _aggregates;

    /// <summary>Creates an aggregate producing keys then aggregates.</summary>
    /// <param name="input">The input operator.</param>
    /// <param name="outputSchema">Keys-then-aggregates schema; field count = keys + aggregates.</param>
    /// <param name="groupingKeys">Grouping key expressions (empty = global aggregate).</param>
    /// <param name="aggregates">Aggregate expressions; at least one.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">No aggregates, or field counts do not add up.</exception>
    public AggregateOperator(
        PhysicalOperator input,
        StructType outputSchema,
        IReadOnlyList<PhysicalExpression> groupingKeys,
        IReadOnlyList<PhysicalExpression> aggregates)
        : base(outputSchema)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(groupingKeys);
        ArgumentNullException.ThrowIfNull(aggregates);
        if (aggregates.Count == 0)
        {
            throw new ArgumentException("Aggregate requires at least one aggregate expression.", nameof(aggregates));
        }

        if (groupingKeys.Count + aggregates.Count != outputSchema.Count)
        {
            throw new ArgumentException(
                $"Output fields {outputSchema.Count} != keys {groupingKeys.Count} + aggregates {aggregates.Count}.",
                nameof(outputSchema));
        }

        int inputFields = input.OutputSchema.Count;
        foreach (PhysicalExpression key in groupingKeys)
        {
            if (key is ColumnReference c && c.Ordinal >= inputFields)
            {
                throw new ArgumentException(
                    $"Grouping key ordinal {c.Ordinal} is out of range for input schema ({inputFields} fields).", nameof(groupingKeys));
            }
        }

        _children = [input];
        _groupingKeys = [.. groupingKeys];
        _aggregates = [.. aggregates];
    }

    /// <inheritdoc />
    public override OperatorKind Kind => OperatorKind.Aggregate;

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalOperator> Children => _children;

    /// <summary>Grouping key expressions; empty means a single global group.</summary>
    public IReadOnlyList<PhysicalExpression> GroupingKeys => _groupingKeys;

    /// <summary>Aggregate expressions emitted after the keys.</summary>
    public IReadOnlyList<PhysicalExpression> Aggregates => _aggregates;
}
