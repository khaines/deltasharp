using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Evaluates a <see cref="CastExpression"/> lane-by-lane over the v1 conversion matrix
/// (STORY-03.4.1). A null input always yields a null output; otherwise the conversion follows the
/// EPIC-02 ANSI/Legacy contract — out-of-range, non-finite, or precision-overflowing conversions
/// throw under <see cref="AnsiMode.Ansi"/> and yield SQL <c>NULL</c> under <see cref="AnsiMode.Legacy"/>,
/// never a silently wrapped value. Float/double narrow to integrals by truncating toward zero.
/// </summary>
/// <remarks>
/// v1 matrix: identity; the numeric/boolean core
/// {boolean, byte, short, int, long, float, double, decimal} in all directions (boolean as 0/1, target
/// boolean as <c>!= 0</c>); and <c>date↔timestamp</c>. Deferred to later stories (and rejected at build
/// by <see cref="IsSupported"/>): any string/binary cast, numeric↔temporal beyond <c>date↔timestamp</c>,
/// and <c>float/double→decimal</c>.
/// </remarks>
internal sealed class CastEvaluator : ExpressionEvaluator
{
    private readonly ExpressionEvaluator _child;
    private readonly DataType _source;
    private readonly AnsiMode _mode;
    private readonly long _intMin;
    private readonly long _intMax;
    private readonly double _doubleUpperExclusive;

    public CastEvaluator(CastExpression node, ExpressionEvaluator child)
        : base(node.Type, node.Nullable)
    {
        _child = child;
        _source = node.Child.Type;
        _mode = node.Mode;
        (_intMin, _intMax, _doubleUpperExclusive) = node.TargetType switch
        {
            ByteType => (sbyte.MinValue, (long)sbyte.MaxValue, sbyte.MaxValue + 1.0),
            ShortType => (short.MinValue, (long)short.MaxValue, short.MaxValue + 1.0),
            IntegerType => (int.MinValue, (long)int.MaxValue, int.MaxValue + 1.0),

            // long.MaxValue (2^63 - 1) is not representable as a double — it rounds up to 2^63 — so an
            // inclusive `truncated > long.MaxValue` guard (which promotes long.MaxValue to (double)2^63)
            // fails to reject the boundary 2^63, and (long) then silently saturates it to long.MaxValue.
            // Reject against the exact double-exclusive upper bound 2^63 (9223372036854775808.0) so 2^63
            // and every larger double overflow. The lower bound long.MinValue (-2^63) IS exactly
            // representable, so it stays correct via the inclusive `truncated < _intMin` guard below.
            LongType => (long.MinValue, long.MaxValue, 9223372036854775808.0),
            _ => (0L, 0L, 0.0),
        };
    }

    /// <summary>Whether the interpreted v1 tier can convert <paramref name="source"/> to <paramref name="target"/>.</summary>
    public static bool IsSupported(DataType source, DataType target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.Equals(target))
        {
            return true;
        }

        return target switch
        {
            BooleanType => IsCore(source),
            ByteType or ShortType or IntegerType or LongType => IsCore(source),
            FloatType or DoubleType => IsCore(source),
            DecimalType => source is BooleanType or ByteType or ShortType or IntegerType or LongType or DecimalType,
            DateType => source is TimestampType,
            TimestampType => source is DateType,
            _ => false,
        };
    }

    /// <inheritdoc />
    public override ColumnVector Evaluate(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(memory);
        ColumnVector child = _child.Evaluate(batch, memory, cancellationToken);
        int rows = batch.LogicalRowCount;

        memory.ReserveVector(Type, rows);
        MutableColumnVector result = ColumnVectors.Create(Type, rows);
        bool hasNulls = child.HasNulls;

        for (int i = 0; i < rows; i++)
        {
            CancellationPolicy.Poll(cancellationToken, i);
            if (hasNulls && child.IsNull(i))
            {
                result.AppendNull();
                continue;
            }

            AppendCast(result, child, i);
        }

        return result;
    }

    private static bool IsCore(DataType type) =>
        type is BooleanType or ByteType or ShortType or IntegerType or LongType or FloatType or DoubleType or DecimalType;

    private void AppendCast(MutableColumnVector result, ColumnVector child, int i)
    {
        if (_source.Equals(Type))
        {
            VectorMaterializer.CopyValue(result, child, i);
            return;
        }

        switch (Type)
        {
            case BooleanType:
                result.AppendValue(CastToBoolean(child, i));
                break;

            case ByteType or ShortType or IntegerType or LongType:
                if (TryCastToIntegral(child, i, out long integral))
                {
                    VectorMaterializer.AppendIntegral(result, integral);
                }
                else
                {
                    result.AppendNull();
                }

                break;

            case FloatType:
                result.AppendValue((float)ReadAsDouble(child, i));
                break;

            case DoubleType:
                result.AppendValue(ReadAsDouble(child, i));
                break;

            case DecimalType target:
                if (TryCastToDecimal(child, i, target, out DecimalValue dec))
                {
                    VectorMaterializer.AppendDecimal(result, dec.Unscaled);
                }
                else
                {
                    result.AppendNull();
                }

                break;

            case DateType:
                AppendNullable(result, TemporalValues.TimestampToDate(ScalarReader.ReadInt64(child, i), _mode));
                break;

            case TimestampType:
                AppendNullable(result, TemporalValues.DateToTimestamp((int)ScalarReader.ReadInt64(child, i), _mode));
                break;

            default:
                throw new UnsupportedTypeException($"No cast is defined to type '{Type.SimpleString}'.");
        }
    }

    private bool CastToBoolean(ColumnVector child, int i) => _source switch
    {
        FloatType or DoubleType => ScalarReader.ReadDouble(child, i) != 0d,
        DecimalType => ScalarReader.ReadDecimal(child, i).Unscaled != Int128.Zero,
        _ => ScalarReader.ReadInt64(child, i) != 0L,
    };

    private double ReadAsDouble(ColumnVector child, int i) =>
        _source is BooleanType ? (ScalarReader.ReadBool(child, i) ? 1d : 0d) : ScalarReader.ReadDouble(child, i);

    private bool TryCastToIntegral(ColumnVector child, int i, out long result)
    {
        switch (_source)
        {
            case BooleanType:
                result = ScalarReader.ReadBool(child, i) ? 1L : 0L;
                return true;

            case FloatType or DoubleType:
                double d = ScalarReader.ReadDouble(child, i);
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    result = 0L;
                    return Fail();
                }

                double truncated = Math.Truncate(d);
                if (truncated < _intMin || truncated >= _doubleUpperExclusive)
                {
                    result = 0L;
                    return Fail();
                }

                result = (long)truncated;
                return true;

            case DecimalType:
                DecimalValue dv = ScalarReader.ReadDecimal(child, i);
                Int128 integerPart = dv.Unscaled / DecimalValue.Pow10(dv.Scale); // truncates toward zero
                if (integerPart < _intMin || integerPart > _intMax)
                {
                    result = 0L;
                    return Fail();
                }

                result = (long)integerPart;
                return true;

            default:
                result = ScalarReader.ReadInt64(child, i);
                return result < _intMin || result > _intMax ? Fail() : true;
        }
    }

    private bool TryCastToDecimal(ColumnVector child, int i, DecimalType target, out DecimalValue result)
    {
        DecimalValue source = _source switch
        {
            BooleanType => new DecimalValue(ScalarReader.ReadBool(child, i) ? Int128.One : Int128.Zero, 0),
            ByteType or ShortType or IntegerType or LongType => new DecimalValue(ScalarReader.ReadInt64(child, i), 0),
            DecimalType => ScalarReader.ReadDecimal(child, i),
            _ => throw new InvalidOperationException($"Cast from '{_source.SimpleString}' to decimal is rejected at build time."),
        };

        DecimalValue? fitted = source.ToType(target, _mode);
        if (fitted is null)
        {
            result = default;
            return false;
        }

        result = fitted.Value;
        return true;
    }

    private static void AppendNullable(MutableColumnVector result, int? value)
    {
        if (value is int v)
        {
            result.AppendValue(v);
        }
        else
        {
            result.AppendNull();
        }
    }

    private static void AppendNullable(MutableColumnVector result, long? value)
    {
        if (value is long v)
        {
            result.AppendValue(v);
        }
        else
        {
            result.AppendNull();
        }
    }

    private bool Fail() =>
        _mode == AnsiMode.Ansi
            ? throw new ArithmeticOverflowException($"Cast to '{Type.SimpleString}' is out of range.")
            : false;
}
