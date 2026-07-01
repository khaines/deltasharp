using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Evaluates an <see cref="ArithmeticExpression"/> lane-by-lane (STORY-03.4.1). Null propagation is
/// <b>propagate-on-any-null</b>: a null operand yields SQL <c>NULL</c> (#143). The accumulation type
/// (integral / single / double / decimal) is resolved once on the node; this kernel only reads,
/// computes, and applies the ANSI/Legacy overflow and zero-division contract — DeltaSharp never wraps:
/// <see cref="AnsiMode.Ansi"/> throws, <see cref="AnsiMode.Legacy"/> nulls (EPIC-02).
/// </summary>
internal sealed class ArithmeticEvaluator : ExpressionEvaluator
{
    private readonly ExpressionEvaluator _left;
    private readonly ExpressionEvaluator _right;
    private readonly ArithmeticOperator _op;
    private readonly ArithmeticEvalKind _kind;
    private readonly AnsiMode _mode;
    private readonly long _intMin;
    private readonly long _intMax;

    public ArithmeticEvaluator(ArithmeticExpression node, ExpressionEvaluator left, ExpressionEvaluator right)
        : base(node.Type, node.Nullable)
    {
        _left = left;
        _right = right;
        _op = node.Operator;
        _kind = node.EvalKind;
        _mode = node.Mode;
        (_intMin, _intMax) = node.Type switch
        {
            ByteType => (sbyte.MinValue, (long)sbyte.MaxValue),
            ShortType => (short.MinValue, (long)short.MaxValue),
            IntegerType => (int.MinValue, (long)int.MaxValue),
            LongType => (long.MinValue, long.MaxValue),
            _ => (0L, 0L),
        };
    }

    /// <inheritdoc />
    public override ColumnVector Evaluate(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(memory);
        ColumnVector left = _left.Evaluate(batch, memory, cancellationToken);
        ColumnVector right = _right.Evaluate(batch, memory, cancellationToken);
        int rows = batch.LogicalRowCount;

        memory.ReserveVector(Type, rows);
        MutableColumnVector result = ColumnVectors.Create(Type, rows);
        bool leftNulls = left.HasNulls;
        bool rightNulls = right.HasNulls;

        for (int i = 0; i < rows; i++)
        {
            CancellationPolicy.Poll(cancellationToken, i);
            if ((leftNulls && left.IsNull(i)) || (rightNulls && right.IsNull(i)))
            {
                result.AppendNull();
                continue;
            }

            AppendComputed(result, left, right, i);
        }

        return result;
    }

    private void AppendComputed(MutableColumnVector result, ColumnVector left, ColumnVector right, int i)
    {
        switch (_kind)
        {
            case ArithmeticEvalKind.Integral:
                if (ComputeIntegral(ScalarReader.ReadInt64(left, i), ScalarReader.ReadInt64(right, i), out long integral))
                {
                    VectorMaterializer.AppendIntegral(result, integral);
                }
                else
                {
                    result.AppendNull();
                }

                break;

            case ArithmeticEvalKind.Single:
                if (ComputeSingle(ScalarReader.ReadSingle(left, i), ScalarReader.ReadSingle(right, i), out float single))
                {
                    result.AppendValue(single);
                }
                else
                {
                    result.AppendNull();
                }

                break;

            case ArithmeticEvalKind.Double:
                if (ComputeDouble(ScalarReader.ReadDouble(left, i), ScalarReader.ReadDouble(right, i), out double dbl))
                {
                    result.AppendValue(dbl);
                }
                else
                {
                    result.AppendNull();
                }

                break;

            default:
                if (ComputeDecimal(ScalarReader.ReadDecimal(left, i), ScalarReader.ReadDecimal(right, i), out DecimalValue dec))
                {
                    VectorMaterializer.AppendDecimal(result, dec.Unscaled);
                }
                else
                {
                    result.AppendNull();
                }

                break;
        }
    }

    private bool ComputeIntegral(long a, long b, out long result)
    {
        result = 0;
        switch (_op)
        {
            case ArithmeticOperator.Add:
                try
                {
                    result = checked(a + b);
                }
                catch (OverflowException)
                {
                    return Overflow();
                }

                break;
            case ArithmeticOperator.Subtract:
                try
                {
                    result = checked(a - b);
                }
                catch (OverflowException)
                {
                    return Overflow();
                }

                break;
            case ArithmeticOperator.Multiply:
                try
                {
                    result = checked(a * b);
                }
                catch (OverflowException)
                {
                    return Overflow();
                }

                break;
            case ArithmeticOperator.Remainder:
                if (b == 0)
                {
                    return DivideByZero();
                }

                // Guard b == -1 so long.MinValue % -1 (which traps OverflowException) yields the
                // mathematically correct 0.
                result = b == -1 ? 0 : a % b;
                break;
            default:
                throw new InvalidOperationException($"Integral arithmetic does not handle '{_op}'.");
        }

        return result < _intMin || result > _intMax ? Overflow() : true;
    }

    private bool ComputeSingle(float a, float b, out float result)
    {
        result = 0f;
        switch (_op)
        {
            case ArithmeticOperator.Add:
                result = a + b;
                break;
            case ArithmeticOperator.Subtract:
                result = a - b;
                break;
            case ArithmeticOperator.Multiply:
                result = a * b;
                break;
            case ArithmeticOperator.Remainder:
                if (b == 0f)
                {
                    return DivideByZero();
                }

                result = a % b;
                break;
            default:
                throw new InvalidOperationException($"Single arithmetic does not handle '{_op}'.");
        }

        return true;
    }

    private bool ComputeDouble(double a, double b, out double result)
    {
        result = 0d;
        switch (_op)
        {
            case ArithmeticOperator.Add:
                result = a + b;
                break;
            case ArithmeticOperator.Subtract:
                result = a - b;
                break;
            case ArithmeticOperator.Multiply:
                result = a * b;
                break;
            case ArithmeticOperator.Divide:
                if (b == 0d)
                {
                    return DivideByZero();
                }

                result = a / b;
                break;
            case ArithmeticOperator.Remainder:
                if (b == 0d)
                {
                    return DivideByZero();
                }

                result = a % b;
                break;
            default:
                throw new InvalidOperationException($"Double arithmetic does not handle '{_op}'.");
        }

        return true;
    }

    private bool ComputeDecimal(DecimalValue a, DecimalValue b, out DecimalValue result)
    {
        result = default;
        DecimalValue exact;
        try
        {
            exact = _op switch
            {
                ArithmeticOperator.Add => DecimalValue.Add(a, b),
                ArithmeticOperator.Subtract => DecimalValue.Subtract(a, b),
                ArithmeticOperator.Multiply => DecimalValue.Multiply(a, b),
                _ => throw new InvalidOperationException("Decimal divide/remainder is rejected when the evaluator is built."),
            };
        }
        catch (ArithmeticOverflowException) when (_mode == AnsiMode.Legacy)
        {
            return false;
        }

        // Fit the exact result into the node's declared result type (HALF_UP rounding to its scale).
        // Under Ansi an out-of-range result throws; under Legacy it nulls.
        DecimalValue? fitted = exact.ToType((DecimalType)Type, _mode);
        if (fitted is null)
        {
            return false;
        }

        result = fitted.Value;
        return true;
    }

    private bool Overflow() =>
        _mode == AnsiMode.Ansi
            ? throw new ArithmeticOverflowException($"Arithmetic '{_op}' overflowed '{Type.SimpleString}'.")
            : false;

    private bool DivideByZero() =>
        _mode == AnsiMode.Ansi
            ? throw new DivideByZeroException($"Division by zero in arithmetic '{_op}'.")
            : false;
}
