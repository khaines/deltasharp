using DeltaSharp.Plans.Logical;

namespace DeltaSharp;

/// <summary>
/// A distributed collection of data organized into named columns, equivalent to Apache Spark's
/// <c>DataFrame</c> (an untyped <c>Dataset&lt;Row&gt;</c>).
/// </summary>
/// <remarks>
/// <b>M1 placeholder.</b> STORY-04.1.1 (#157) introduces this type as the return shape of the
/// <see cref="SparkSession"/> doors (<see cref="SparkSession.Sql(string)"/> and the reader), and it
/// is backed here by the immutable logical <see cref="Plan"/> it wraps — the structural-sharing
/// foundation from STORY-04.4.1 (#167). Its transformation and action surface is delivered by later
/// FEAT-04.1/FEAT-04.2 stories (#158/#159 and following); it is intentionally inert here. Instances
/// are created by the engine from a logical plan, not by user code, so the constructor is non-public.
/// </remarks>
public sealed class DataFrame
{
    /// <summary>Wraps an immutable, unresolved logical plan.</summary>
    internal DataFrame(LogicalPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    /// <summary>The immutable, unresolved logical plan backing this DataFrame.</summary>
    internal LogicalPlan Plan { get; }
}
