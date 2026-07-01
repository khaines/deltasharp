using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Evaluates a <see cref="Literal"/> by broadcasting its constant (or SQL <c>NULL</c>) across the
/// batch's logical rows into a contiguous output vector (STORY-03.4.1). The value is decoded once
/// into its storage shape at construction, so evaluation is a tight append loop with no per-row
/// boxing.
/// </summary>
internal sealed class LiteralEvaluator : ExpressionEvaluator
{
    private readonly Literal _literal;
    private readonly byte[]? _bytes;

    public LiteralEvaluator(Literal literal)
        : base(literal.Type, literal.IsNull)
    {
        _literal = literal;

        // Decode the variable-width payload once so every row appends the same bytes.
        if (!literal.IsNull)
        {
            _bytes = literal.Type switch
            {
                StringType => Encoding.UTF8.GetBytes((string)literal.Value!),
                BinaryType => (byte[])literal.Value!,
                _ => null,
            };
        }
    }

    /// <inheritdoc />
    public override ColumnVector Evaluate(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(memory);
        int rows = batch.LogicalRowCount;

        if (_bytes is not null)
        {
            // Variable-width: validity + offsets (one int per row) + the repeated value bytes.
            long validity = (rows + 7L) / 8L;
            memory.Reserve(validity + ((long)rows * sizeof(int)) + ((long)rows * _bytes.Length));
        }
        else
        {
            memory.ReserveVector(Type, rows);
        }

        MutableColumnVector result = ColumnVectors.Create(Type, rows);
        if (_literal.IsNull)
        {
            for (int i = 0; i < rows; i++)
            {
                result.AppendNull();
            }

            return result;
        }

        AppendBroadcast(result, rows);
        return result;
    }

    private void AppendBroadcast(MutableColumnVector result, int rows)
    {
        switch (Type)
        {
            case BooleanType:
                AppendRepeated(result, (bool)_literal.Value!, rows);
                break;
            case ByteType:
                AppendRepeated(result, unchecked((byte)(sbyte)_literal.Value!), rows);
                break;
            case ShortType:
                AppendRepeated(result, (short)_literal.Value!, rows);
                break;
            case IntegerType or DateType:
                AppendRepeated(result, (int)_literal.Value!, rows);
                break;
            case LongType or TimestampType:
                AppendRepeated(result, (long)_literal.Value!, rows);
                break;
            case FloatType:
                AppendRepeated(result, (float)_literal.Value!, rows);
                break;
            case DoubleType:
                AppendRepeated(result, (double)_literal.Value!, rows);
                break;
            case DecimalType { IsCompact: true }:
                AppendRepeated(result, (long)(Int128)_literal.Value!, rows);
                break;
            case DecimalType:
                AppendRepeated(result, (Int128)_literal.Value!, rows);
                break;
            case StringType or BinaryType:
                for (int i = 0; i < rows; i++)
                {
                    result.AppendBytes(_bytes);
                }

                break;
            default:
                throw new UnsupportedTypeException($"No literal materialization is defined for type '{Type.SimpleString}'.");
        }
    }

    private static void AppendRepeated<T>(MutableColumnVector result, T value, int rows)
        where T : unmanaged
    {
        for (int i = 0; i < rows; i++)
        {
            result.AppendValue(value);
        }
    }
}
