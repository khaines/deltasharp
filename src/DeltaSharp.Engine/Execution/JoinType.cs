namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The v1 join shapes a <see cref="JoinOperator"/> may declare, named to match Spark so row
/// multiplicity and null-key semantics carry over (checklist 21, EPIC-03 AC). Choosing the
/// physical algorithm (broadcast hash, shuffle hash, sort-merge) is a planner/backend concern;
/// this enum fixes only the logical join shape the contract carries.
/// </summary>
public enum JoinType
{
    /// <summary>Emit matched pairs only.</summary>
    Inner,

    /// <summary>Emit all left rows, null-padded on the right when unmatched.</summary>
    LeftOuter,

    /// <summary>Emit all right rows, null-padded on the left when unmatched.</summary>
    RightOuter,

    /// <summary>Emit all rows from both sides, null-padded where unmatched.</summary>
    FullOuter,

    /// <summary>Emit left rows that have at least one right match (left columns only).</summary>
    LeftSemi,

    /// <summary>Emit left rows that have no right match (left columns only).</summary>
    LeftAnti,
}
