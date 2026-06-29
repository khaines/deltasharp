using System.Buffers.Binary;
using Apache.Arrow;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Columnar.Arrow;

/// <summary>
/// Wraps a single Apache Arrow array as a DeltaSharp <see cref="ColumnVector"/> for
/// <see cref="ArrowBatchConverter.FromArrow"/> (STORY-02.2.2, #136). It is the per-column half of the
/// "Arrow at the edges" import: the broad set of v1 types is wrapped <b>zero-copy</b> through
/// <see cref="ArrowColumnVector.Wrap"/> (offset and validity preserved), nested arrays become an
/// opaque <see cref="ArrowNestedColumnVector"/>, and the two types whose physical layout genuinely
/// differs from DeltaSharp's — bit-packed <c>boolean</c> (Arrow packs 8 rows/byte, DeltaSharp uses a
/// 1-byte bool) and <c>decimal128</c> (Arrow is always 16 bytes, DeltaSharp compacts ≤18-digit
/// decimals to 8) — are materialized into a managed vector. Materialization copies the logical rows
/// in order with validity intact but, being a fresh buffer, resets the physical
/// <see cref="ColumnVector.Offset"/> to <c>0</c> (documented in the capability matrix).
/// </summary>
internal static class ArrowColumnReader
{
    /// <summary>Wraps <paramref name="array"/> as a DeltaSharp <see cref="ColumnVector"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> is null.</exception>
    /// <exception cref="UnsupportedTypeException">The Arrow type has no v1 DeltaSharp mapping.</exception>
    internal static ColumnVector WrapColumn(IArrowArray array)
    {
        ArgumentNullException.ThrowIfNull(array);

        return array switch
        {
            // Layout mismatch: materialize (offset resets, logical order/validity preserved).
            BooleanArray b => MaterializeBoolean(b),
            Decimal128Array d => MaterializeDecimal(d),

            // Nested: opaque zero-copy pass-through (offset + validity preserved).
            StructArray or ListArray or MapArray =>
                new ArrowNestedColumnVector(ArrowSchemaMapper.ToDeltaType(array.Data.DataType), array),

            // Everything else routes through the zero-copy edge factory, which wraps the supported
            // primitive/variable arrays and throws a precise UnsupportedTypeException for the rest
            // (unsigned/half-float, non-microsecond timestamp, null, …).
            _ => ArrowColumnVector.Wrap(array),
        };
    }

    private static ColumnVector MaterializeBoolean(BooleanArray array)
    {
        MutableColumnVector vector = ColumnVectors.Create(BooleanType.Instance, array.Length);
        for (int i = 0; i < array.Length; i++)
        {
            // GetValue is the null-aware accessor (GetBoolean is obsolete); it is offset-adjusted,
            // so a sliced source materializes in logical row order.
            bool? value = array.GetValue(i);
            if (value.HasValue)
            {
                vector.AppendValue(value.Value);
            }
            else
            {
                vector.AppendNull();
            }
        }

        return vector;
    }

    private static ColumnVector MaterializeDecimal(Decimal128Array array)
    {
        var type = new DecimalType(array.Precision, array.Scale);
        MutableColumnVector vector = ColumnVectors.Create(type, array.Length);
        for (int i = 0; i < array.Length; i++)
        {
            if (array.IsNull(i))
            {
                vector.AppendNull();
                continue;
            }

            // Arrow decimal128 is the unscaled integer as 16 little-endian two's-complement bytes —
            // the same unscaled value DeltaSharp stores, compacted to long when the precision allows.
            Int128 unscaled = BinaryPrimitives.ReadInt128LittleEndian(array.GetBytes(i));
            if (type.IsCompact)
            {
                vector.AppendValue((long)unscaled);
            }
            else
            {
                vector.AppendValue(unscaled);
            }
        }

        return vector;
    }
}
