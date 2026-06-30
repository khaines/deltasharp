namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// One output column of a projection: either a zero-copy <see cref="Ordinal"/> reference to an input
/// column, or a computed <see cref="Evaluator"/> that materializes the column (STORY-03.4.1). The
/// interpreted project stream uses these to emit a selection-free batch whose columns are all
/// contiguous and logical-row aligned.
/// </summary>
internal readonly struct ProjectionPlan
{
    private ProjectionPlan(int ordinal, ExpressionEvaluator? evaluator)
    {
        Ordinal = ordinal;
        Evaluator = evaluator;
    }

    /// <summary>The referenced input-column ordinal when <see cref="Evaluator"/> is null; otherwise <c>-1</c>.</summary>
    public int Ordinal { get; }

    /// <summary>The evaluator that computes this column, or <see langword="null"/> for a column reference.</summary>
    public ExpressionEvaluator? Evaluator { get; }

    /// <summary>A zero-copy reference to input column <paramref name="ordinal"/>.</summary>
    public static ProjectionPlan Column(int ordinal) => new(ordinal, null);

    /// <summary>A computed column produced by <paramref name="evaluator"/>.</summary>
    public static ProjectionPlan Computed(ExpressionEvaluator evaluator) => new(-1, evaluator);
}
