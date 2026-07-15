using System.Numerics;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.Execution.Spill;
using DeltaSharp.Types;

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
    /// Serializes <paramref name="group"/>'s <b>partial</b> accumulator state to <paramref name="writer"/>
    /// when the hash table spills (STORY-03.6.2 AC1). The bytes are the minimum needed to resume the fold:
    /// running sums/counts plus the has-value/nulled flags, or the running MIN/MAX best — never the emitted
    /// result, so AVG (sum + count) and overflow-tracking SUM merge exactly.
    /// </summary>
    internal abstract void WriteState(int group, SpillStateWriter writer);

    /// <summary>
    /// Folds a spilled partial state (read from <paramref name="reader"/> in the same field order
    /// <see cref="WriteState"/> wrote it) into <paramref name="group"/> of the recovery table. The fold is
    /// associative/commutative for the exact aggregates (COUNT/SUM-integer/SUM-decimal/MIN/MAX), so the
    /// merged value equals the no-spill value; ANSI overflow re-raises and Legacy overflow nulls the group.
    /// </summary>
    internal abstract void MergeState(int group, ref SpillStateReader reader);

    /// <summary>
    /// Clears all per-group state (and releases any variable-width reservation) so the aggregator can be
    /// reused for the next spill partition's recovery table.
    /// </summary>
    internal abstract void Reset();

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

    internal override void WriteState(int group, SpillStateWriter writer) => writer.WriteLong(_counts[group]);

    internal override void MergeState(int group, ref SpillStateReader reader) => _counts[group] += reader.ReadLong();

    internal override void Reset() => _counts = [];
}

/// <summary>
/// <c>SUM</c> over an integral input, accumulated in an unchecked <see cref="Int128"/> (result
/// <see cref="LongType"/>). Summing <see cref="long"/> lanes into an <see cref="Int128"/> cannot
/// realistically overflow, so there is no intermediate overflow state: the fold is exact and the
/// fit-to-<see cref="long"/> check happens once, at <see cref="Emit"/>, against the <b>final true sum</b>.
/// Overflow of that final value follows <see cref="AnsiMode"/>: ANSI throws, Legacy nulls the group. A
/// group with no non-null input is SQL <c>NULL</c>.
/// </summary>
/// <remarks>
/// Deferring the overflow check to the final value (#156 B2) is what makes <c>SUM</c> exact under spill:
/// because the running accumulator never poisons a group on a transient intermediate overflow, a
/// re-partitioned spill fold and the single-pass no-spill fold accumulate the same modular
/// <see cref="Int128"/> total and apply the same one fit check — spill == no-spill by construction.
/// </remarks>
internal sealed class SumLongAggregator : Aggregator
{
    private readonly AnsiMode _mode;
    private Int128[] _sums = [];
    private bool[] _hasValue = [];

    internal SumLongAggregator(AnsiMode mode) => _mode = mode;

    internal override long BytesPerGroup => 16 + 1;

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

        _hasValue[group] = true;

        // Unchecked Int128 add: a long summed into Int128 cannot overflow short of > 2^64 rows, so there is
        // no intermediate overflow to detect here. The true-value fit check is deferred to Emit so a
        // transient overflow that the true sum recovers from never poisons the group (spill == no-spill).
        _sums[group] += ScalarReader.ReadInt64(input, row);
    }

    internal override void Emit(int group, MutableColumnVector destination)
    {
        if (!_hasValue[group])
        {
            destination.AppendNull();
            return;
        }

        Int128 sum = _sums[group];
        if (sum < long.MinValue || sum > long.MaxValue)
        {
            // The FINAL true sum does not fit bigint: ANSI throws, Legacy yields NULL (EPIC-02 never-wrap).
            if (_mode == AnsiMode.Ansi)
            {
                throw new ArithmeticOverflowException("SUM overflowed bigint.");
            }

            destination.AppendNull();
            return;
        }

        VectorMaterializer.AppendIntegral(destination, (long)sum);
    }

    internal override void WriteState(int group, SpillStateWriter writer)
    {
        writer.WriteInt128(_sums[group]);
        writer.WriteBool(_hasValue[group]);
    }

    internal override void MergeState(int group, ref SpillStateReader reader)
    {
        // Merge the partition's partial Int128 sum unchecked — no intermediate overflow check — so the merged
        // total is the exact modular running sum and Emit applies the single final fit check.
        _sums[group] += reader.ReadInt128();
        if (reader.ReadBool())
        {
            _hasValue[group] = true;
        }
    }

    internal override void Reset()
    {
        _sums = [];
        _hasValue = [];
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

    internal override void WriteState(int group, SpillStateWriter writer)
    {
        writer.WriteDouble(_sums[group]);
        writer.WriteBool(_hasValue[group]);
    }

    internal override void MergeState(int group, ref SpillStateReader reader)
    {
        _sums[group] += reader.ReadDouble();
        if (reader.ReadBool())
        {
            _hasValue[group] = true;
        }
    }

    internal override void Reset()
    {
        _sums = [];
        _hasValue = [];
    }
}

/// <summary>
/// <c>SUM</c> over a decimal input, accumulated as an arbitrary-precision
/// <see cref="System.Numerics.BigInteger"/> unscaled mantissa at the input's (uniform) scale and fitted
/// to the Spark result type <c>decimal(min(38, p+10), s)</c> at emit. Precision overflow of the
/// <b>final</b> value follows <see cref="AnsiMode"/>: ANSI throws, Legacy nulls the group.
/// </summary>
/// <remarks>
/// <para>Unlike <see cref="SumLongAggregator"/> (whose <see cref="long"/> lanes need ~2^64 rows to overflow
/// an <see cref="Int128"/> accumulator), a single decimal mantissa is ALREADY a full-width
/// <see cref="Int128"/> (<see cref="DecimalValue.Unscaled"/>, up to ±~1e38). As few as three near-max
/// <c>decimal(38,0)</c> values overflow an <see cref="Int128"/> accumulator, so an <em>unchecked</em>
/// <see cref="Int128"/> add would wrap (mod 2^128) to a small-magnitude value that then passes
/// <see cref="DecimalValue.ToType(DecimalType, AnsiMode)"/> — a true overflow silently returned as a wrong
/// value (#156 N1).</para>
/// <para>The accumulator is therefore widened to <see cref="System.Numerics.BigInteger"/>: it is
/// arbitrary-precision so it holds the EXACT true running sum and NEVER wraps. <see cref="BigInteger"/>
/// addition is associative/commutative, so the merged mantissa is a pure function of the input multiset —
/// independent of partition/accumulation order — which is what keeps spill == no-spill EXACT even when a
/// transient partial exceeds <see cref="Int128"/> range but the final true sum is back in decimal range
/// (#156 B2). A CHECKED <see cref="Int128"/> add would instead throw on that transient in the single-pass
/// no-spill arm while a re-partitioned spill arm restarts from zero and never sees it — the exact A1
/// divergence — so a non-wrapping wide accumulator is required, not a checked narrow one.</para>
/// <para>The single overflow gate is at <see cref="Emit"/>, applied to the final value: any valid
/// <c>decimal(≤38)</c> result fits <see cref="Int128"/>, so a sum outside
/// <c>[Int128.MinValue, Int128.MaxValue]</c> is definitively an overflow (ANSI throws / Legacy nulls);
/// otherwise the in-range mantissa is handed to <see cref="DecimalValue.ToType(DecimalType, AnsiMode)"/>,
/// which performs the precision/ANSI/Legacy check exactly as the rest of EPIC-02.</para>
/// </remarks>
internal sealed class SumDecimalAggregator : Aggregator
{
    private readonly DecimalType _resultType;
    private readonly AnsiMode _mode;
    private BigInteger[] _sums = [];
    private bool[] _hasValue = [];

    // The uniform scale of the input decimal column, captured on the first observed value/merge. -1 until
    // then; a group with no value never reads it (Emit short-circuits on !_hasValue).
    private int _scale = -1;

    internal SumDecimalAggregator(DecimalType resultType, AnsiMode mode)
    {
        _resultType = resultType;
        _mode = mode;
    }

    // BigInteger state is variable-width; a decimal(≤38) mantissa needs ≤16 bytes of magnitude and the
    // exact running sum stays a few words wide. Use a fixed estimate covering the BigInteger handle plus a
    // small magnitude buffer and the scale + has-value bytes.
    internal override long BytesPerGroup => 40 + 4 + 1;

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

        DecimalValue value = ScalarReader.ReadDecimal(input, row);
        _scale = value.Scale; // uniform across the column; idempotent
        _hasValue[group] = true;

        // Arbitrary-precision add: the BigInteger accumulator holds the EXACT true running sum and never
        // wraps, so a transient mantissa beyond Int128 range that the final sum recovers from never poisons
        // the group and the result is order-invariant. The single fit/precision gate is deferred to Emit.
        _sums[group] += (BigInteger)value.Unscaled;
    }

    internal override void Emit(int group, MutableColumnVector destination)
    {
        if (!_hasValue[group])
        {
            destination.AppendNull();
            return;
        }

        // The single overflow gate, applied to the FINAL exact sum. Any valid decimal(≤38) result fits
        // Int128, so a sum outside the Int128 range is DEFINITELY an overflow: ANSI throws, Legacy → NULL.
        BigInteger sum = _sums[group];
        if (sum < (BigInteger)Int128.MinValue || sum > (BigInteger)Int128.MaxValue)
        {
            if (_mode == AnsiMode.Ansi)
            {
                throw new ArithmeticOverflowException(
                    $"Decimal value out of range for '{_resultType.SimpleString}'.");
            }

            destination.AppendNull();
            return;
        }

        // In Int128 range: ToType still applies the result type's precision/scale check (ANSI/Legacy).
        DecimalValue? fitted = new DecimalValue((Int128)sum, _scale).ToType(_resultType, _mode);
        if (fitted is null)
        {
            destination.AppendNull();
        }
        else
        {
            VectorMaterializer.AppendDecimal(destination, fitted.Value.Unscaled);
        }
    }

    internal override void WriteState(int group, SpillStateWriter writer)
    {
        // Serialize the FULL-WIDTH partial sum (no intermediate fit check) so a spilled partial is the
        // partition's exact running sum and Emit applies the single final gate.
        writer.WriteBytes(_sums[group].ToByteArray());
        writer.WriteInt(_scale);
        writer.WriteBool(_hasValue[group]);
    }

    internal override void MergeState(int group, ref SpillStateReader reader)
    {
        var unscaled = new BigInteger(reader.ReadBytes());
        int scale = reader.ReadInt();
        bool has = reader.ReadBool();
        if (has)
        {
            _scale = scale;
            _hasValue[group] = true;
        }

        // Merge the partition's partial mantissa with no intermediate check — BigInteger never wraps, so the
        // merged total is the exact running sum (order-invariant) and Emit applies the single final gate.
        _sums[group] += unscaled;
    }

    internal override void Reset()
    {
        _sums = [];
        _hasValue = [];
        _scale = -1;
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

    internal override void WriteState(int group, SpillStateWriter writer)
    {
        writer.WriteDouble(_sums[group]);
        writer.WriteLong(_counts[group]);
    }

    internal override void MergeState(int group, ref SpillStateReader reader)
    {
        _sums[group] += reader.ReadDouble();
        _counts[group] += reader.ReadLong();
    }

    internal override void Reset()
    {
        _sums = [];
        _counts = [];
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

    internal override void WriteState(int group, SpillStateWriter writer)
    {
        object? best = _best[group];
        if (best is null)
        {
            writer.WriteBool(false);
            return;
        }

        writer.WriteBool(true);
        WriteStorage(writer, best);
    }

    internal override void MergeState(int group, ref SpillStateReader reader)
    {
        if (!reader.ReadBool())
        {
            return;
        }

        object candidate = ReadStorage(ref reader);
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

    internal override void Reset()
    {
        Release();
        _best = [];
    }

    // Writes the running best in its storage CLR shape (the inverse of ReadStorage), keyed by _type.
    private void WriteStorage(SpillStateWriter writer, object value)
    {
        switch (_type)
        {
            case BooleanType: writer.WriteBool((bool)value); break;
            case ByteType: writer.WriteByte((byte)value); break;
            case ShortType: writer.WriteShort((short)value); break;
            case IntegerType or DateType: writer.WriteInt((int)value); break;
            case LongType or TimestampType or TimestampNtzType: writer.WriteLong((long)value); break;
            case FloatType: writer.WriteSingle((float)value); break;
            case DoubleType: writer.WriteDouble((double)value); break;
            case DecimalType { IsCompact: true }: writer.WriteLong((long)value); break;
            case DecimalType: writer.WriteInt128((Int128)value); break;
            case StringType or BinaryType: writer.WriteBytes((byte[])value); break;
            default: throw new UnsupportedTypeException($"MIN/MAX cannot spill type '{_type.SimpleString}'.");
        }
    }

    private object ReadStorage(ref SpillStateReader reader) => _type switch
    {
        BooleanType => reader.ReadBool(),
        ByteType => reader.ReadByte(),
        ShortType => reader.ReadShort(),
        IntegerType or DateType => reader.ReadInt(),
        LongType or TimestampType or TimestampNtzType => reader.ReadLong(),
        FloatType => reader.ReadSingle(),
        DoubleType => reader.ReadDouble(),
        DecimalType { IsCompact: true } => reader.ReadLong(),
        DecimalType => reader.ReadInt128(),
        StringType or BinaryType => reader.ReadBytes().ToArray(),
        _ => throw new UnsupportedTypeException($"MIN/MAX cannot recover type '{_type.SimpleString}'."),
    };

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
