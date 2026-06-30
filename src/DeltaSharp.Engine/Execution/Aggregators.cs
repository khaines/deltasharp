using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// A per-group accumulator for one <see cref="AggregateExpression"/>. The interpreted aggregate
/// stream owns one instance per aggregate term and indexes its state arrays by the group ordinal
/// (discovery order). The contract is allocation-light on the hot path: <see cref="Accumulate"/>
/// folds one logical lane into a group's running state, <see cref="EnsureCapacity"/> grows the state
/// arrays as new groups appear, and <see cref="Emit"/> writes the finished value (or SQL <c>NULL</c>)
/// once the child is fully drained.
/// </summary>
internal abstract class Aggregator
{
    /// <summary>A coarse per-group state size used by the stream's memory reservation.</summary>
    internal abstract long BytesPerGroup { get; }

    /// <summary>Ensures the state arrays can index <paramref name="groupCount"/> groups.</summary>
    internal abstract void EnsureCapacity(int groupCount);

    /// <summary>
    /// Folds logical row <paramref name="row"/> of <paramref name="input"/> into
    /// <paramref name="group"/>. <paramref name="input"/> is <see langword="null"/> only for
    /// <c>COUNT(*)</c>; every other aggregate receives its evaluated argument vector.
    /// </summary>
    internal abstract void Accumulate(int group, ColumnVector? input, int row);

    /// <summary>Appends <paramref name="group"/>'s finished value (or a null) to <paramref name="destination"/>.</summary>
    internal abstract void Emit(int group, MutableColumnVector destination);

    /// <summary>
    /// Variable-width bytes this aggregator has reserved beyond its flat <see cref="BytesPerGroup"/>
    /// estimate — non-zero only for <see cref="MinMaxAggregator"/> over string/binary, which retains a
    /// boxed best value whose true length is charged to the budget at retention time.
    /// </summary>
    internal virtual long ReservedBytes => 0;

    /// <summary>Releases any variable-width reservation this aggregator holds (see <see cref="ReservedBytes"/>).</summary>
    internal virtual void Release()
    {
    }

    /// <summary>
    /// Builds the accumulator matching <paramref name="aggregate"/>. Throws
    /// <see cref="UnsupportedOperatorException"/> for the one deferred shape, <c>AVG(decimal)</c>.
    /// </summary>
    internal static Aggregator Create(
        AggregateExpression aggregate, string backendName, OperatorKind kind, IExecutionMemory memory)
    {
        switch (aggregate.Function)
        {
            case AggregateFunction.Count:
                return new CountAggregator();

            case AggregateFunction.Sum:
                DataType sumType = aggregate.Input!.Type;
                if (TypeCoercion.IsIntegral(sumType))
                {
                    return new SumLongAggregator(aggregate.Mode);
                }

                if (sumType is FloatType or DoubleType)
                {
                    return new SumDoubleAggregator();
                }

                return new SumDecimalAggregator((DecimalType)aggregate.Type, aggregate.Mode);

            case AggregateFunction.Average:
                DataType avgType = aggregate.Input!.Type;
                if (TypeCoercion.IsIntegral(avgType) || avgType is FloatType or DoubleType)
                {
                    return new AvgDoubleAggregator();
                }

                throw new UnsupportedOperatorException(
                    kind,
                    backendName,
                    "AVG(decimal) needs the deferred decimal-divide rounding (see type-system.md); "
                    + "cast the input to double, or compute SUM/COUNT and divide downstream");

            case AggregateFunction.Min or AggregateFunction.Max:
                return new MinMaxAggregator(
                    aggregate.Function == AggregateFunction.Min, aggregate.Input!.Type, memory);

            default:
                throw new UnsupportedOperatorException(
                    kind, backendName, $"aggregate function '{aggregate.Function}' is not implemented");
        }
    }

    /// <summary>Grows <paramref name="array"/> to at least <paramref name="length"/> (amortized doubling).</summary>
    private protected static void Grow<T>(ref T[] array, int length)
    {
        if (array.Length >= length)
        {
            return;
        }

        int capacity = array.Length == 0 ? 16 : array.Length * 2;
        if (capacity < length)
        {
            capacity = length;
        }

        Array.Resize(ref array, capacity);
    }
}

/// <summary>
/// <c>COUNT</c>. <c>COUNT(*)</c> (null <paramref name="input"/>) counts every row; <c>COUNT(x)</c>
/// counts non-null lanes. The result is a non-null <see cref="LongType"/> — an empty group counts 0.
/// </summary>
internal sealed class CountAggregator : Aggregator
{
    private long[] _counts = [];

    internal override long BytesPerGroup => sizeof(long);

    internal override void EnsureCapacity(int groupCount) => Grow(ref _counts, groupCount);

    internal override void Accumulate(int group, ColumnVector? input, int row)
    {
        if (input is null || !input.IsNull(row))
        {
            _counts[group]++;
        }
    }

    internal override void Emit(int group, MutableColumnVector destination) =>
        VectorMaterializer.AppendIntegral(destination, _counts[group]);
}

/// <summary>
/// <c>SUM</c> over an integral input, accumulated in a checked <see cref="long"/> (result
/// <see cref="LongType"/>). Overflow follows <see cref="AnsiMode"/>: ANSI throws, Legacy nulls the
/// group. A group with no non-null input is SQL <c>NULL</c>.
/// </summary>
internal sealed class SumLongAggregator : Aggregator
{
    private readonly AnsiMode _mode;
    private long[] _sums = [];
    private bool[] _hasValue = [];
    private bool[] _nulled = [];

    internal SumLongAggregator(AnsiMode mode) => _mode = mode;

    internal override long BytesPerGroup => sizeof(long) + 2;

    internal override void EnsureCapacity(int groupCount)
    {
        Grow(ref _sums, groupCount);
        Grow(ref _hasValue, groupCount);
        Grow(ref _nulled, groupCount);
    }

    internal override void Accumulate(int group, ColumnVector? input, int row)
    {
        if (input!.IsNull(row))
        {
            return;
        }

        _hasValue[group] = true;
        if (_nulled[group])
        {
            return;
        }

        long value = ScalarReader.ReadInt64(input, row);
        try
        {
            _sums[group] = checked(_sums[group] + value);
        }
        catch (OverflowException ex)
        {
            if (_mode == AnsiMode.Ansi)
            {
                throw new ArithmeticOverflowException("SUM overflowed bigint.", ex);
            }

            _nulled[group] = true;
        }
    }

    internal override void Emit(int group, MutableColumnVector destination)
    {
        if (!_hasValue[group] || _nulled[group])
        {
            destination.AppendNull();
        }
        else
        {
            VectorMaterializer.AppendIntegral(destination, _sums[group]);
        }
    }
}

/// <summary>
/// <c>SUM</c> over a float/double input, accumulated in IEEE <see cref="double"/> (result
/// <see cref="DoubleType"/>). Floating sums never overflow to an exception (matching Spark); a group
/// with no non-null input is SQL <c>NULL</c>.
/// </summary>
internal sealed class SumDoubleAggregator : Aggregator
{
    private double[] _sums = [];
    private bool[] _hasValue = [];

    internal override long BytesPerGroup => sizeof(double) + 1;

    internal override void EnsureCapacity(int groupCount)
    {
        Grow(ref _sums, groupCount);
        Grow(ref _hasValue, groupCount);
    }

    internal override void Accumulate(int group, ColumnVector? input, int row)
    {
        if (input!.IsNull(row))
        {
            return;
        }

        _sums[group] += ScalarReader.ReadDouble(input, row);
        _hasValue[group] = true;
    }

    internal override void Emit(int group, MutableColumnVector destination)
    {
        if (_hasValue[group])
        {
            destination.AppendValue(_sums[group]);
        }
        else
        {
            destination.AppendNull();
        }
    }
}

/// <summary>
/// <c>SUM</c> over a decimal input, accumulated exactly via <see cref="DecimalValue"/> and fitted to
/// the Spark result type <c>decimal(min(38, p+10), s)</c> at emit. Mantissa or precision overflow
/// follows <see cref="AnsiMode"/>: ANSI throws, Legacy nulls the group.
/// </summary>
internal sealed class SumDecimalAggregator : Aggregator
{
    private readonly DecimalType _resultType;
    private readonly AnsiMode _mode;
    private DecimalValue[] _sums = [];
    private bool[] _hasValue = [];
    private bool[] _nulled = [];

    internal SumDecimalAggregator(DecimalType resultType, AnsiMode mode)
    {
        _resultType = resultType;
        _mode = mode;
    }

    internal override long BytesPerGroup => 16 + 2;

    internal override void EnsureCapacity(int groupCount)
    {
        Grow(ref _sums, groupCount);
        Grow(ref _hasValue, groupCount);
        Grow(ref _nulled, groupCount);
    }

    internal override void Accumulate(int group, ColumnVector? input, int row)
    {
        if (input!.IsNull(row))
        {
            return;
        }

        _hasValue[group] = true;
        if (_nulled[group])
        {
            return;
        }

        DecimalValue value = ScalarReader.ReadDecimal(input, row);
        try
        {
            // A default DecimalValue is 0 at scale 0, the additive identity for any scale.
            _sums[group] = DecimalValue.Add(_sums[group], value);
        }
        catch (ArithmeticOverflowException)
        {
            if (_mode == AnsiMode.Ansi)
            {
                throw;
            }

            _nulled[group] = true;
        }
    }

    internal override void Emit(int group, MutableColumnVector destination)
    {
        if (!_hasValue[group] || _nulled[group])
        {
            destination.AppendNull();
            return;
        }

        DecimalValue? fitted = _sums[group].ToType(_resultType, _mode);
        if (fitted is null)
        {
            destination.AppendNull();
        }
        else
        {
            VectorMaterializer.AppendDecimal(destination, fitted.Value.Unscaled);
        }
    }
}

/// <summary>
/// <c>AVG</c> over a non-decimal numeric input: a <see cref="double"/> running sum divided by the
/// non-null count (result <see cref="DoubleType"/>), matching Spark's double accumulation buffer for
/// non-decimal averages. A group with no non-null input is SQL <c>NULL</c>.
/// </summary>
internal sealed class AvgDoubleAggregator : Aggregator
{
    private double[] _sums = [];
    private long[] _counts = [];

    internal override long BytesPerGroup => sizeof(double) + sizeof(long);

    internal override void EnsureCapacity(int groupCount)
    {
        Grow(ref _sums, groupCount);
        Grow(ref _counts, groupCount);
    }

    internal override void Accumulate(int group, ColumnVector? input, int row)
    {
        if (input!.IsNull(row))
        {
            return;
        }

        _sums[group] += ScalarReader.ReadDouble(input, row);
        _counts[group]++;
    }

    internal override void Emit(int group, MutableColumnVector destination)
    {
        if (_counts[group] == 0)
        {
            destination.AppendNull();
        }
        else
        {
            destination.AppendValue(_sums[group] / _counts[group]);
        }
    }
}

/// <summary>
/// <c>MIN</c>/<c>MAX</c> over any orderable atomic input. The running best is kept boxed in storage
/// shape (<see cref="ScalarValues"/>); a group that never sees a non-null input stays unset and emits
/// SQL <c>NULL</c>. Ordering is Spark's total order (signed integral, <c>NaN</c>-largest float,
/// unsigned-byte strings), so <c>MAX(double)</c> returns <c>NaN</c> when present and <c>MIN</c> ignores
/// it unless every value is <c>NaN</c>.
/// </summary>
internal sealed class MinMaxAggregator : Aggregator
{
    private readonly bool _isMin;
    private readonly DataType _type;
    private readonly bool _isVariableWidth;
    private readonly IExecutionMemory _memory;
    private object?[] _best = [];
    private long _reservedBytes;

    internal MinMaxAggregator(bool isMin, DataType type, IExecutionMemory memory)
    {
        _isMin = isMin;
        _type = type;
        _isVariableWidth = type is StringType or BinaryType;
        _memory = memory;
    }

    internal override long BytesPerGroup => 32;

    internal override long ReservedBytes => _reservedBytes;

    internal override void EnsureCapacity(int groupCount) => Grow(ref _best, groupCount);

    internal override void Accumulate(int group, ColumnVector? input, int row)
    {
        if (input!.IsNull(row))
        {
            return;
        }

        object candidate = ScalarValues.ReadStorage(input, row);
        object? current = _best[group];
        if (current is null)
        {
            Retain(group, candidate);
            return;
        }

        int comparison = ScalarValues.Compare(_type, candidate, current);
        if (_isMin ? comparison < 0 : comparison > 0)
        {
            Retain(group, candidate);
        }
    }

    internal override void Emit(int group, MutableColumnVector destination)
    {
        object? best = _best[group];
        if (best is null)
        {
            destination.AppendNull();
        }
        else
        {
            ScalarValues.AppendStorage(destination, best);
        }
    }

    internal override void Release()
    {
        if (_reservedBytes > 0)
        {
            _memory.Release(_reservedBytes);
            _reservedBytes = 0;
        }
    }

    // Stores the new running best, charging the budget for a retained string/binary value's TRUE byte
    // length (the flat 32-byte BytesPerGroup estimate cannot cover a wide payload). The reservation
    // grows monotonically over a group's lifetime — the displaced best is conservatively not refunded —
    // and is released wholesale by the owning stream via Release().
    private void Retain(int group, object candidate)
    {
        if (_isVariableWidth && candidate is byte[] { Length: > 0 } bytes)
        {
            if (!_memory.TryReserve(bytes.Length))
            {
                throw new ExecutionMemoryException(
                    bytes.Length, _memory.AvailableBytes, _memory.BudgetBytes,
                    "the MIN/MAX running best has no spillable representation in v1 (spill is STORY-03.5.x); "
                    + "raise the query/tenant memory budget");
            }

            _reservedBytes += bytes.Length;
        }

        _best[group] = candidate;
    }
}
