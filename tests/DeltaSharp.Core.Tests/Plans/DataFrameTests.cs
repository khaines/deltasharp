using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// The unified <see cref="DataFrame"/> is a plan-backed value: it wraps a non-null immutable
/// <see cref="DataFrame.Plan"/> and does no work at construction (the lazy half of the EPIC-04
/// lazy/eager invariant).
/// </summary>
public sealed class DataFrameTests
{
    [Fact]
    public void Constructor_NullPlan_ThrowsArgumentNullException()
    {
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => new DataFrame(null!));
        Assert.Equal("plan", ex.ParamName);
    }

    [Fact]
    public void Constructor_StoresPlanByReferenceWithoutMutatingIt()
    {
        LogicalPlan plan = PlanFixtures.Relation("people");

        var frame = new DataFrame(plan);

        Assert.Same(plan, frame.Plan);
    }
}
