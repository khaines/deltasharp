using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Plans;

/// <summary>
/// A minimal M1 placeholder that pairs a value with its immutable <see cref="LogicalPlan"/>.
/// The full Spark-parity <c>DataFrame</c> surface (transformations, actions, schema) arrives in
/// FEAT-04.1/FEAT-04.2; this type exists only to anchor the logical-plan foundation and to
/// demonstrate that deriving a new value never mutates an existing one's plan (AC2).
/// </summary>
internal sealed class DataFrame
{
    /// <summary>Wraps an immutable logical plan.</summary>
    public DataFrame(LogicalPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    /// <summary>The immutable, unresolved logical plan backing this value.</summary>
    public LogicalPlan Plan { get; }
}
