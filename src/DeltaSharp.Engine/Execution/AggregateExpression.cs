using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The SQL aggregate functions an <see cref="AggregateOperator"/> may compute (STORY-03.2.2).
/// Names match Spark so the row-level null and overflow semantics carry over verbatim.
/// </summary>
public enum AggregateFunction
{
    /// <summary>Counts rows. <c>COUNT(*)</c> counts every row; <c>COUNT(x)</c> counts non-null <c>x</c>.</summary>
    Count,

    /// <summary>Sums the non-null inputs; an empty/all-null group is SQL <c>NULL</c>.</summary>
    Sum,

    /// <summary>The minimum non-null input; an empty/all-null group is SQL <c>NULL</c>.</summary>
    Min,

    /// <summary>The maximum non-null input; an empty/all-null group is SQL <c>NULL</c>.</summary>
    Max,

    /// <summary>The mean of the non-null inputs; an empty/all-null group is SQL <c>NULL</c>.</summary>
    Average,
}

/// <summary>
/// A resolved aggregate term (<c>COUNT(*)</c>, <c>SUM(x)</c>, <c>AVG(x)</c>, <c>MIN(x)</c>,
/// <c>MAX(x)</c>) an <see cref="AggregateOperator"/> carries in its aggregate list. It is a
/// <see cref="PhysicalExpression"/> so the operator can hold it alongside its grouping keys, but it
/// is <b>not</b> a scalar node: the interpreted aggregate stream evaluates it directly from
/// accumulator state, never through <c>ExpressionEvaluators.Build</c> (its single scalar
/// <see cref="Input"/> sub-expression <i>is</i> evaluated through that path). Building the node does
/// no row work; it only resolves the Spark result type and nullability.
/// </summary>
/// <remarks>
/// <para><b>Result types (Spark parity).</b></para>
/// <list type="bullet">
/// <item><description><c>COUNT</c> → <see cref="LongType"/>, never null.</description></item>
/// <item><description><c>SUM</c>: integral → <see cref="LongType"/>; float/double →
/// <see cref="DoubleType"/>; <c>decimal(p,s)</c> → <c>decimal(min(38, p+10), s)</c>.</description></item>
/// <item><description><c>AVG</c>: integral/float/double → <see cref="DoubleType"/>;
/// <c>decimal(p,s)</c> → <c>decimal(min(38, p+4), s+4)</c> (execution deferred — see
/// relational-operators.md).</description></item>
/// <item><description><c>MIN</c>/<c>MAX</c> → the input type.</description></item>
/// </list>
/// <para>
/// Every aggregate except <c>COUNT</c> is nullable: an empty group, or one whose every input row is
/// null, yields SQL <c>NULL</c>. <see cref="Mode"/> selects ANSI (overflow throws) vs Legacy
/// (overflow nulls) for <c>SUM</c>/<c>AVG</c>; <c>COUNT</c>/<c>MIN</c>/<c>MAX</c> never overflow.
/// </para>
/// </remarks>
public sealed class AggregateExpression : PhysicalExpression
{
    private readonly PhysicalExpression[] _children;

    /// <summary>Creates an aggregate term.</summary>
    /// <param name="function">The aggregate function.</param>
    /// <param name="input">
    /// The single scalar argument, or <see langword="null"/> for <c>COUNT(*)</c>. Required for every
    /// function except <c>COUNT</c>.
    /// </param>
    /// <param name="mode">ANSI strictness for <c>SUM</c>/<c>AVG</c> overflow (default <see cref="AnsiMode.Ansi"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null for a function that requires it.</exception>
    /// <exception cref="ArgumentException">The input type is invalid for <paramref name="function"/>.</exception>
    public AggregateExpression(AggregateFunction function, PhysicalExpression? input, AnsiMode mode = AnsiMode.Ansi)
        : base(Resolve(function, input), function != AggregateFunction.Count)
    {
        Function = function;
        Mode = mode;
        _children = input is null ? [] : [input];
    }

    /// <summary>The aggregate function.</summary>
    public AggregateFunction Function { get; }

    /// <summary>The ANSI strictness lens applied to <c>SUM</c>/<c>AVG</c> overflow.</summary>
    public AnsiMode Mode { get; }

    /// <summary>The scalar argument, or <see langword="null"/> for <c>COUNT(*)</c>.</summary>
    public PhysicalExpression? Input => _children.Length == 0 ? null : _children[0];

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalExpression> Children => _children;

    /// <summary>
    /// Resolves the Spark result type for <paramref name="function"/> over
    /// <paramref name="input"/>, validating that the argument is present and orderable/numeric as
    /// the function requires. Decimal <c>SUM</c>/<c>AVG</c> widening follows Spark's
    /// <c>Sum</c>/<c>Average</c> aggregate result-type rules.
    /// </summary>
    private static DataType Resolve(AggregateFunction function, PhysicalExpression? input)
    {
        if (function == AggregateFunction.Count)
        {
            // COUNT(*) (input null) and COUNT(x) both produce a non-null bigint.
            return LongType.Instance;
        }

        ArgumentNullException.ThrowIfNull(input);
        DataType t = input.Type;

        switch (function)
        {
            case AggregateFunction.Min or AggregateFunction.Max:
                if (!IsOrderable(t))
                {
                    throw new ArgumentException(
                        $"{function} requires an orderable atomic input but got '{t.SimpleString}'.", nameof(input));
                }

                return t;

            case AggregateFunction.Sum:
                if (TypeCoercion.IsIntegral(t))
                {
                    return LongType.Instance;
                }

                if (t is FloatType or DoubleType)
                {
                    return DoubleType.Instance;
                }

                if (t is DecimalType d)
                {
                    // Spark: SUM(decimal(p,s)) -> decimal(min(38, p+10), s).
                    return new DecimalType(Math.Min(DecimalType.MaxPrecision, d.Precision + 10), d.Scale);
                }

                throw new ArgumentException(
                    $"SUM requires a numeric input but got '{t.SimpleString}'.", nameof(input));

            case AggregateFunction.Average:
                if (TypeCoercion.IsIntegral(t) || t is FloatType or DoubleType)
                {
                    return DoubleType.Instance;
                }

                if (t is DecimalType da)
                {
                    // Spark: AVG(decimal(p,s)) -> decimal(min(38, p+4), s+4). Execution of the decimal
                    // case is deferred (it needs the deferred decimal-divide rounding); the type is
                    // still resolved so the plan is well-typed and the stream fails fast at Open.
                    return new DecimalType(
                        Math.Min(DecimalType.MaxPrecision, da.Precision + 4),
                        Math.Min(DecimalType.MaxPrecision, da.Scale + 4));
                }

                throw new ArgumentException(
                    $"AVG requires a numeric input but got '{t.SimpleString}'.", nameof(input));

            default:
                throw new ArgumentOutOfRangeException(nameof(function), function, "Unknown aggregate function.");
        }
    }

    /// <summary>
    /// Whether <paramref name="type"/> has a total order the aggregate can compare on — the same
    /// atomic set the byte-sortable key encoder accepts (numeric, boolean, temporal, string,
    /// binary), excluding nested and void types.
    /// </summary>
    private static bool IsOrderable(DataType type) =>
        type is BooleanType or ByteType or ShortType or IntegerType or LongType
            or FloatType or DoubleType or DecimalType or DateType or TimestampType
            or StringType or BinaryType;
}
