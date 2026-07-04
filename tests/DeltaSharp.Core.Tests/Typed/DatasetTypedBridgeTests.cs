using DeltaSharp.Core.Tests.Plans;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Typed;

/// <summary>
/// STORY-04.2.4 (#163) AC1/AC2 — the typed transformation bridge shares the DataFrame plan model
/// rather than forking it. A typed <c>Where</c>/<c>Select</c> lambda lowers to the <b>same</b>
/// immutable <see cref="Filter"/>/<see cref="Project"/> nodes as the equivalent untyped
/// <see cref="DataFrame"/> call (AC1), and <see cref="Dataset{T}.ToDF"/>/<see cref="DataFrame.As{T}"/>
/// preserve plan identity without materialization (AC2). Structural plan equality is asserted, so a
/// divergence in the lowered nodes fails the test.
/// </summary>
public sealed class DatasetTypedBridgeTests
{
    private sealed class Person
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public int Age { get; set; }
    }

    private static DataFrame People() => new(PlanFixtures.Relation("people"));

    // ----- AC1: typed filter lowers to the same Filter node as the untyped predicate -----

    [Fact]
    public void TypedWhere_LowersToSameFilterPlanAsUntyped()
    {
        DataFrame df = People();

        DataFrame untyped = df.Filter(Functions.Col("Age").Geq(Functions.Lit(21)));
        Dataset<Person> typed = df.As<Person>().Where(p => p.Age >= 21);

        Assert.IsType<Filter>(typed.Plan);
        Assert.Equal(untyped.Plan, typed.Plan);
    }

    [Fact]
    public void TypedWhere_WithCapturedVariable_LowersToSameFilterPlan()
    {
        DataFrame df = People();
        int threshold = 21;

        DataFrame untyped = df.Filter(Functions.Col("Age").Geq(Functions.Lit(21)));
        Dataset<Person> typed = df.As<Person>().Where(p => p.Age >= threshold);

        Assert.Equal(untyped.Plan, typed.Plan);
    }

    [Fact]
    public void TypedWhere_BooleanCombinators_LowerToSameFilterPlan()
    {
        DataFrame df = People();

        DataFrame untyped = df.Filter(
            Functions.Col("Age").Gt(Functions.Lit(18)).And(Functions.Col("Age").Lt(Functions.Lit(65))));
        Dataset<Person> typed = df.As<Person>().Where(p => p.Age > 18 && p.Age < 65);

        Assert.Equal(untyped.Plan, typed.Plan);
    }

    // ----- AC1: typed projection lowers to the same Project node as the untyped select -----

    [Fact]
    public void TypedSelect_LowersToSameProjectPlanAsUntyped()
    {
        DataFrame df = People();

        DataFrame untyped = df.Select(Functions.Col("Name"), Functions.Col("Age"));
        DataFrame typed = df.As<Person>().Select(p => p.Name, p => p.Age);

        Assert.IsType<Project>(typed.Plan);
        Assert.Equal(untyped.Plan, typed.Plan);
    }

    // ----- AC1: typed Filter(Column) shares the untyped node while staying typed -----

    [Fact]
    public void TypedFilterColumn_ProducesSameNodeAndStaysTyped()
    {
        DataFrame df = People();

        DataFrame untyped = df.Filter(Functions.Col("Age").Geq(Functions.Lit(21)));
        Dataset<Person> typed = df.As<Person>().Filter(Functions.Col("Age").Geq(Functions.Lit(21)));

        Assert.Equal(untyped.Plan, typed.Plan);
    }

    // ----- AC2: As<T>() / ToDF() preserve plan identity without materialization -----

    [Fact]
    public void As_And_ToDF_PreservePlanIdentity()
    {
        DataFrame df = People();

        Dataset<Person> ds = df.As<Person>();
        DataFrame back = ds.ToDF();

        Assert.Same(df.Plan, ds.Plan);
        Assert.Same(df.Plan, back.Plan);
    }

    [Fact]
    public void TypedTransformation_SharesUntouchedPlanSubtreeByReference()
    {
        DataFrame df = People();

        Dataset<Person> typed = df.As<Person>().Where(p => p.Age >= 21);

        var filter = Assert.IsType<Filter>(typed.Plan);
        Assert.Same(df.Plan, filter.Child); // structural sharing (#167)
    }

    [Fact]
    public void Schema_IsDerivedFromT_WithoutMaterialization()
    {
        DataFrame df = People();

        Dataset<Person> ds = df.As<Person>();

        Assert.Equal(new[] { "Id", "Name", "Age" }, ds.Schema.Fields.Select(f => f.Name).ToArray());
        Assert.False(ds.Schema["Id"].Nullable);
        Assert.True(ds.Schema["Name"].Nullable);
    }
}
