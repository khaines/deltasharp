using DeltaSharp.Core.Tests.LazyEager;
using DeltaSharp.Core.Tests.Plans;
using DeltaSharp.Diagnostics;
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

    private sealed class Unmappable
    {
        public int Id { get; set; }

        public Guid Key { get; set; }
    }

    private static Dataset<Person> PeopleDataset() =>
        new DataFrame(PlanFixtures.Relation("people")).As<Person>();

    // ----- AC4: unmappable schema property surfaces the distinct schema diagnostic (not wrapped) -----

    [Fact]
    public void As_WithUnmappableProperty_ThrowsSchemaExceptionDirectly()
    {
        var df = new DataFrame(PlanFixtures.Relation("bad"));

        // The per-T schema cache defers derivation lazily, so a bad T throws the deterministic schema
        // exception DIRECTLY — never wrapped in a TypeInitializationException — and it is distinct from
        // the lambda-lowering diagnostic (catch-site precision).
        var ex = Assert.Throws<UnsupportedTypedSchemaException>(() => df.As<Unmappable>());
        Assert.IsAssignableFrom<UnsupportedTypedException>(ex);

        // Cached: a second construction re-throws the same deterministic type.
        Assert.Throws<UnsupportedTypedSchemaException>(() => df.As<Unmappable>());
    }

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

    // ----- Lazy invariant (AC2): the whole typed chain touches no scan, but an action would -----

    [Fact]
    public void TypedChain_BuildsPlanWithoutReading_WhileAnActionWouldRead()
    {
        // Non-vacuity: the FakeSource's Read() (the eager scan) is genuinely reachable — the action
        // path below drives it and observes reads — yet building the entire typed chain
        // (As<T>().Where(...).Filter(...).Select(...).ToDF()) triggers ZERO reads. If any typed
        // transformation performed eager work it would touch the ExecutionAudit seam and fail the
        // zero-observation assertion. The subsequent backend.Execute proves the assertion is not
        // vacuous: the same source, when an action runs, does read.
        var recording = new RecordingAudit();
        var source = new FakeSource("people", rowCount: 3);
        var backend = new FakeExecutionBackend();

        DataFrame projected;
        DataFrame roundTripped;
        using (ExecutionAudit.BeginScope(recording))
        {
            Dataset<Person> ds = new DataFrame(source.Describe())
                .As<Person>()
                .Where(p => p.Age >= 21)
                .Filter(p => p.Id > 0);

            roundTripped = ds.ToDF();                       // exercises ToDF
            projected = ds.Select(p => p.Name, p => p.Age); // exercises Select

            // Building the plan read nothing: no file opened, no row read, no stage entered.
            Assert.True(recording.ObservedNoExecution);
            Assert.Equal(0, recording.FilesOpened);
            Assert.Equal(0, recording.RowsRead);

            // Now run an ACTION over the built plan; the eager scan is genuinely wired in and reads.
            long rows = backend.Execute(projected.Plan, source);
            Assert.Equal(3, rows);
        }

        Assert.NotNull(roundTripped.Plan);
        Assert.Equal(1, recording.FilesOpened); // the action — not any transformation — read
        Assert.Equal(3, recording.RowsRead);
    }
}
