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
