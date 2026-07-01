using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Computes output columns from a list of <see cref="Projections"/> over its single input. The
/// projection count must equal the output schema field count, and each projection's type must
/// match the corresponding output field, so the typed output contract is verified at construction.
/// </summary>
public sealed class ProjectOperator : PhysicalOperator
{
    private readonly PhysicalOperator[] _children;
    private readonly PhysicalExpression[] _projections;

    /// <summary>Creates a projection producing <paramref name="outputSchema"/> from expressions.</summary>
    /// <param name="input">The input operator.</param>
    /// <param name="outputSchema">The output schema; one field per projection.</param>
    /// <param name="projections">One expression per output column, in field order.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Projection count or types do not match the schema.</exception>
    public ProjectOperator(PhysicalOperator input, StructType outputSchema, IReadOnlyList<PhysicalExpression> projections)
        : base(outputSchema)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(projections);
        if (projections.Count != outputSchema.Count)
        {
            throw new ArgumentException($"Projection count {projections.Count} != output field count {outputSchema.Count}.", nameof(projections));
        }

        for (int i = 0; i < projections.Count; i++)
        {
            PhysicalExpression e = projections[i] ?? throw new ArgumentException($"Projection {i} is null.", nameof(projections));
            if (!e.Type.Equals(outputSchema[i].DataType))
            {
                throw new ArgumentException(
                    $"Projection {i} type '{e.Type.SimpleString}' != field '{outputSchema[i].Name}' type '{outputSchema[i].DataType.SimpleString}'.",
                    nameof(projections));
            }
        }

        _children = [input];
        _projections = [.. projections];
    }

    /// <inheritdoc />
    public override OperatorKind Kind => OperatorKind.Project;

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalOperator> Children => _children;

    /// <summary>One expression per output column, in output-field order.</summary>
    public IReadOnlyList<PhysicalExpression> Projections => _projections;
}
