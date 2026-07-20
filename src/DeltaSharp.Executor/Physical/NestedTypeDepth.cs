using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// Fail-closes an adversarially deep nested <see cref="DataType"/> at the CreateDataFrame encode door
/// (<see cref="LocalRelationBatches"/>) and the materialize decode door (<see cref="RowMaterializer"/>).
/// Both recurse per nesting level — and <c>ColumnVectors.Create</c> recurses to build the nested vectors
/// even for a ZERO-row relation — so an unbounded type-tree depth would overflow the stack with an
/// <b>uncatchable</b> <see cref="System.StackOverflowException"/> (a driver-process crash, not a planned
/// error). This mirrors the existing <c>TreeNode.MaxDepth</c> plan-tree guard and
/// <c>ConstraintExpressionFrontend.MaxConstraintExpressionDepth</c>: a shallow realistic schema is
/// accepted, a pathological one is rejected with a deterministic <see cref="UnsupportedPlanException"/>.
/// </summary>
internal static class NestedTypeDepth
{
    /// <summary>The maximum nested type-tree depth CreateDataFrame encode/decode accepts. Mirrors
    /// <c>Core.Plans.TreeNode.MaxDepth</c> (1000); every realistic Delta/Spark schema nests far shallower,
    /// so this only rejects a hostile deeply-nested schema before it can overflow the recursive encode/decode
    /// (or the recursive <c>ColumnVectors.Create</c>) on a small worker-thread stack.</summary>
    public const int MaxDepth = 1000;

    /// <summary>Rejects <paramref name="type"/> (a column's declared type) if its nested depth exceeds
    /// <see cref="MaxDepth"/>. Walked <b>iteratively</b> (an explicit stack) so validating a deep type cannot
    /// itself overflow the stack.</summary>
    /// <exception cref="UnsupportedPlanException"><paramref name="type"/> nests deeper than <see cref="MaxDepth"/>.</exception>
    public static void Ensure(DataType type, QueryExecutionStage stage, string path)
    {
        var pending = new Stack<(DataType Type, int Depth)>();
        pending.Push((type, 1));
        while (pending.Count > 0)
        {
            (DataType current, int depth) = pending.Pop();
            if (depth > MaxDepth)
            {
                throw new UnsupportedPlanException(
                    stage,
                    $"Column '{path}' nests deeper than the supported limit of {MaxDepth} levels; a more deeply "
                    + "nested type is refused fail-closed to avoid an uncatchable StackOverflow.");
            }

            switch (current)
            {
                case StructType structType:
                    foreach (StructField field in structType)
                    {
                        pending.Push((field.DataType, depth + 1));
                    }

                    break;
                case ArrayType arrayType:
                    pending.Push((arrayType.ElementType, depth + 1));
                    break;
                case MapType mapType:
                    pending.Push((mapType.KeyType, depth + 1));
                    pending.Push((mapType.ValueType, depth + 1));
                    break;
            }
        }
    }
}
