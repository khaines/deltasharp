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
    /// <summary>The maximum number of nested type levels the relabel recursion descends before failing closed,
    /// matching the storage read/write caps (64). A defense-in-depth StackOverflow guard on the public vector
    /// <c>RelabelTo</c> API; in the production read path the projection's congruence check (also 64-capped)
    /// fires first.</summary>
    internal const int MaxRelabelDepth = 64;

    /// <summary>Relabels <paramref name="column"/> to <paramref name="logicalType"/>, recursing into nested
    /// interiors. A column already carrying the logical type (none mode / no rename) is returned unchanged.</summary>
    public static ColumnVector To(ColumnVector column, DataType logicalType) => To(column, logicalType, 0);

    // Depth-bounded recursive relabel. The depth is checked BEFORE any descent, so an over-deep congruent
    // vector fails closed with a typed exception rather than a StackOverflowException.
    internal static ColumnVector To(ColumnVector column, DataType logicalType, int depth)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(logicalType);
        if (depth > MaxRelabelDepth)
        {
            throw new NotSupportedException(
                $"Cannot relabel a column nested deeper than the supported limit of {MaxRelabelDepth} type levels.");
        }

        if (column.Type.Equals(logicalType))
        {
            return column;
        }

        return column switch
        {
            StructColumnVector s when logicalType is StructType st => s.RelabelTo(st, depth),
            ListColumnVector l when logicalType is ArrayType at => l.RelabelTo(at, depth),
            MapColumnVector m when logicalType is MapType mt => m.RelabelTo(mt, depth),
            _ => throw new ArgumentException(
                $"Cannot relabel a '{column.Type.SimpleString}' column to '{logicalType.SimpleString}'; the types "
                + "are not structurally congruent.", nameof(logicalType)),
        };
    }
}
