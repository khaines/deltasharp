using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Selects passing rows of its single input using a boolean <see cref="Predicate"/>. Output schema
/// equals the input schema; passing rows are exposed via a selection vector so unneeded columns are
/// never copied (the columnar filter contract). Backends report kept rows in
/// <see cref="OperatorMetrics.SelectedRows"/>.
/// </summary>
public sealed class FilterOperator : PhysicalOperator
{
    private readonly PhysicalOperator[] _children;

    /// <summary>Creates a filter over <paramref name="input"/> with the given boolean predicate.</summary>
    /// <param name="input">The input operator.</param>
    /// <param name="predicate">A boolean predicate evaluated per row.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="predicate"/> is not boolean-typed.</exception>
    public FilterOperator(PhysicalOperator input, PhysicalExpression predicate)
        : base((input ?? throw new ArgumentNullException(nameof(input))).OutputSchema)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (predicate.Type is not BooleanType)
        {
            throw new ArgumentException($"Filter predicate must be boolean, was '{predicate.Type.SimpleString}'.", nameof(predicate));
        }

        _children = [input];
        Predicate = predicate;
    }

    /// <inheritdoc />
    public override OperatorKind Kind => OperatorKind.Filter;

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalOperator> Children => _children;

    /// <summary>The boolean predicate; only rows where it is true are kept.</summary>
    public PhysicalExpression Predicate { get; }
}
