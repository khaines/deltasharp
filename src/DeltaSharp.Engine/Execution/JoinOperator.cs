using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Combines a left and right input on equality of <see cref="LeftKeys"/>/<see cref="RightKeys"/>,
/// shaped by <see cref="JoinType"/>. The contract fixes the join shape only; the physical algorithm
/// (broadcast/shuffle hash, sort-merge) is a backend choice. Key lists must be equal length and
/// pairwise type-compatible. Row multiplicity and null-key behaviour match Spark for v1.
/// </summary>
public sealed class JoinOperator : PhysicalOperator
{
    private readonly PhysicalOperator[] _children;
    private readonly PhysicalExpression[] _leftKeys;
    private readonly PhysicalExpression[] _rightKeys;

    /// <summary>Creates a join with the given output schema.</summary>
    /// <param name="left">The left (probe/stream) input.</param>
    /// <param name="right">The right (build) input.</param>
    /// <param name="outputSchema">The joined output schema.</param>
    /// <param name="joinType">The logical join shape.</param>
    /// <param name="leftKeys">Left-side equi-join keys.</param>
    /// <param name="rightKeys">Right-side equi-join keys, pairwise matched to <paramref name="leftKeys"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Key counts differ or pairwise types mismatch.</exception>
    public JoinOperator(
        PhysicalOperator left,
        PhysicalOperator right,
        StructType outputSchema,
        JoinType joinType,
        IReadOnlyList<PhysicalExpression> leftKeys,
        IReadOnlyList<PhysicalExpression> rightKeys)
        : base(outputSchema)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(leftKeys);
        ArgumentNullException.ThrowIfNull(rightKeys);
        if (leftKeys.Count != rightKeys.Count)
        {
            throw new ArgumentException($"Join key counts differ: left {leftKeys.Count}, right {rightKeys.Count}.", nameof(rightKeys));
        }

        for (int i = 0; i < leftKeys.Count; i++)
        {
            if (!leftKeys[i].Type.Equals(rightKeys[i].Type))
            {
                throw new ArgumentException(
                    $"Join key {i} types differ: left '{leftKeys[i].Type.SimpleString}', right '{rightKeys[i].Type.SimpleString}'.",
                    nameof(rightKeys));
            }
        }

        for (int i = 0; i < leftKeys.Count; i++)
        {
            if (leftKeys[i] is ColumnReference lc && lc.Ordinal >= left.OutputSchema.Count)
            {
                throw new ArgumentException($"Left key {i} ordinal {lc.Ordinal} out of range for left input ({left.OutputSchema.Count} fields).", nameof(leftKeys));
            }

            if (rightKeys[i] is ColumnReference rc && rc.Ordinal >= right.OutputSchema.Count)
            {
                throw new ArgumentException($"Right key {i} ordinal {rc.Ordinal} out of range for right input ({right.OutputSchema.Count} fields).", nameof(rightKeys));
            }
        }

        _children = [left, right];
        JoinType = joinType;
        _leftKeys = [.. leftKeys];
        _rightKeys = [.. rightKeys];
    }

    /// <inheritdoc />
    public override OperatorKind Kind => OperatorKind.Join;

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalOperator> Children => _children;

    /// <summary>The logical join shape.</summary>
    public JoinType JoinType { get; }

    /// <summary>Left-side equi-join keys.</summary>
    public IReadOnlyList<PhysicalExpression> LeftKeys => _leftKeys;

    /// <summary>Right-side equi-join keys, pairwise matched to <see cref="LeftKeys"/>.</summary>
    public IReadOnlyList<PhysicalExpression> RightKeys => _rightKeys;
}
