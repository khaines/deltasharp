using DeltaSharp.Core.Tests.LazyEager;
using DeltaSharp.Core.Tests.Plans;
using Xunit;

namespace DeltaSharp.Core.Tests.Typed;

/// <summary>
/// STORY-04.2.4 (#163) AC4 and the lazy invariant — an unsupported typed lambda node raises the
/// deterministic <see cref="UnsupportedTypedExpressionException"/> at plan-construction time, and no
/// typed transformation (<see cref="DataFrame.As{T}"/>, <see cref="Dataset{T}.Where(System.Linq.Expressions.Expression{System.Func{Person, bool}})"/>,
/// <see cref="Dataset{T}.Select"/>, <see cref="Dataset{T}.ToDF"/>) ever reads the source (ADR-0001).
/// </summary>
public sealed class DatasetTypedLoweringDiagnosticsTests
{
    private sealed class Person
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public int Age { get; set; }
    }

    private static Dataset<Person> PeopleDataset() =>
        new DataFrame(PlanFixtures.Relation("people")).As<Person>();

    // ----- AC4: unsupported lambda nodes -> deterministic diagnostic -----

    [Fact]
    public void Where_MethodCall_ThrowsDeterministicDiagnostic()
    {
        Dataset<Person> ds = PeopleDataset();

        var ex = Assert.Throws<UnsupportedTypedExpressionException>(
            () => ds.Where(p => p.Name!.StartsWith("a")));

        Assert.Contains("Unsupported typed expression node", ex.Message);
    }

    [Fact]
    public void Where_NestedMemberAccess_ThrowsDeterministicDiagnostic()
    {
        Dataset<Person> ds = PeopleDataset();

        var ex = Assert.Throws<UnsupportedTypedExpressionException>(
            () => ds.Where(p => p.Name!.Length > 3));

        Assert.Contains("member access", ex.Message);
    }

    [Fact]
    public void Select_UnsupportedNode_ThrowsDeterministicDiagnostic()
    {
        Dataset<Person> ds = PeopleDataset();

        Assert.Throws<UnsupportedTypedExpressionException>(
            () => ds.Select(p => p.Name!.ToUpperInvariant()));
    }

    [Fact]
    public void Where_NullPredicate_Throws()
    {
        Dataset<Person> ds = PeopleDataset();

        Assert.Throws<ArgumentNullException>(() => ds.Where((System.Linq.Expressions.Expression<Func<Person, bool>>)null!));
    }

    // ----- Lazy invariant: the whole typed chain touches no scan -----

    [Fact]
    public void TypedChain_OverThrowOnReadSource_NeverReads()
    {
        var source = new ThrowOnReadSource("people");
        var df = new DataFrame(source.Describe());

        DataFrame result = df
            .As<Person>()
            .Where(p => p.Age >= 21)
            .Filter(p => p.Id > 0)
            .Select(p => p.Name, p => p.Age);

        Assert.NotNull(result.Plan);
        Assert.Equal(0, source.ReadCount);
    }
}
