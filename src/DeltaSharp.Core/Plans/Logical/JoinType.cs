using System.Buffers;

namespace DeltaSharp.Plans.Logical;

/// <summary>The relational join kinds the M1 logical plan can record. Spark parity.</summary>
internal enum JoinType
{
    /// <summary>Inner join.</summary>
    Inner,

    /// <summary>Cross (Cartesian) join.</summary>
    Cross,

    /// <summary>Left outer join.</summary>
    LeftOuter,

    /// <summary>Right outer join.</summary>
    RightOuter,

    /// <summary>Full outer join.</summary>
    FullOuter,

    /// <summary>Left semi join.</summary>
    LeftSemi,

    /// <summary>Left anti join.</summary>
    LeftAnti,
}

/// <summary>
/// Maps Spark's join-type <b>string aliases</b> (the <c>joinType</c> argument of
/// <c>Dataset.join</c>) onto the internal <see cref="JoinType"/> enum, so the enum stays off the
/// public surface while <see cref="DataFrame.Join(DataFrame, Column, string)"/> accepts the same
/// strings Spark users already know. The input is normalized (lower-cased, with underscores
/// <b>and</b> spaces dropped) before matching, so <c>"left_outer"</c>, <c>"leftouter"</c>, and
/// <c>"LEFT OUTER"</c> are all the same kind. This is a <b>superset</b> of Spark's
/// <c>JoinType.apply(String)</c> aliases (Spark strips <c>'_'</c> only; DeltaSharp also tolerates
/// spaces for a friendlier UX).
/// </summary>
internal static class JoinTypes
{
    /// <summary>The Spark join-type aliases accepted by <see cref="FromSparkString"/>, listed in the
    /// diagnostic when an unknown string is supplied (AC3).</summary>
    public const string SupportedAliases =
        "inner, cross, outer, full, fullouter, full_outer, left, leftouter, left_outer, "
        + "right, rightouter, right_outer, semi, leftsemi, left_semi, anti, leftanti, left_anti";

    /// <summary>Resolves a Spark join-type string to a <see cref="JoinType"/>.</summary>
    /// <param name="joinType">The Spark join-type alias (e.g. <c>"inner"</c>, <c>"left"</c>).</param>
    /// <returns>The matching <see cref="JoinType"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="joinType"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="joinType"/> is not a supported alias; the
    /// message names the valid aliases.</exception>
    public static JoinType FromSparkString(string joinType)
    {
        ArgumentNullException.ThrowIfNull(joinType);
        string normalized = Normalize(joinType);
        return normalized switch
        {
            "inner" => JoinType.Inner,
            "cross" => JoinType.Cross,
            "outer" or "full" or "fullouter" => JoinType.FullOuter,
            "left" or "leftouter" => JoinType.LeftOuter,
            "right" or "rightouter" => JoinType.RightOuter,
            "semi" or "leftsemi" => JoinType.LeftSemi,
            "anti" or "leftanti" => JoinType.LeftAnti,
            _ => throw new ArgumentException(
                $"Unsupported join type '{joinType}'. Supported join types are: {SupportedAliases}.",
                nameof(joinType)),
        };
    }

    // A join-type alias is a short literal; only the longest supported alias needs the fast path.
    // Anything longer is a caller error (it will fail the switch anyway) — cap the on-stack buffer so
    // a pathologically long attacker-controlled string cannot overflow the stack (consistent with the
    // TreeNode.MaxDepth adversarial-bound posture).
    private const int MaxStackAllocChars = 256;

    private static string Normalize(string joinType)
    {
        char[]? rented = joinType.Length > MaxStackAllocChars
            ? ArrayPool<char>.Shared.Rent(joinType.Length)
            : null;
        Span<char> buffer = rented is null ? stackalloc char[joinType.Length] : rented;
        try
        {
            int length = 0;
            foreach (char c in joinType)
            {
                if (c is '_' or ' ')
                {
                    continue;
                }

                buffer[length++] = char.ToLowerInvariant(c);
            }

            return new string(buffer[..length]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }
}
