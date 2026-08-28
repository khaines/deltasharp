using DeltaSharp.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// Zero-copy recursive re-type of a nested <see cref="ColumnVector"/> onto a congruent logical
/// <see cref="DataType"/> (#676 single-level, extended to depth&gt;1 by #866 866a — the column-mapping
/// name-mode read-exit inverse relabel). It substitutes the logical field NAMES (and per-field metadata) at
/// every level while sharing every child buffer / validity mask / window; only the logical TYPE changes,
/// never a scalar leaf type or the structural kind. A genuine kind/type mismatch throws (a backstop) — the
/// column-mapping projection validates congruence with a typed storage error before this is reached.
/// </summary>
internal static class NestedRelabel
{
    /// <summary>Relabels <paramref name="column"/> to <paramref name="logicalType"/>, recursing into nested
    /// interiors. A column already carrying the logical type (none mode / no rename) is returned unchanged.</summary>
    public static ColumnVector To(ColumnVector column, DataType logicalType)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(logicalType);
        if (column.Type.Equals(logicalType))
        {
            return column;
        }

        return column switch
        {
            StructColumnVector s when logicalType is StructType st => s.RelabelTo(st),
            ListColumnVector l when logicalType is ArrayType at => l.RelabelTo(at),
            MapColumnVector m when logicalType is MapType mt => m.RelabelTo(mt),
            _ => throw new ArgumentException(
                $"Cannot relabel a '{column.Type.SimpleString}' column to '{logicalType.SimpleString}'; the types "
                + "are not structurally congruent.", nameof(logicalType)),
        };
    }
}
