using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Engine.Execution.Expressions;
using Xunit;

namespace DeltaSharp.Engine.Tests.Execution.Parity;

/// <summary>
/// The randomized half of the interpreter-vs-compiled parity suite (STORY-03.5.2 AC2/AC3/AC4/AC5):
/// a seeded generator (<see cref="BackendParityGenerator"/>) synthesizes a schema + expression tree +
/// batch over the v1 fixed-width types, and the oracle (<see cref="BackendParityOracle"/>) evaluates
/// the same batch on the interpreter (the ADR-0001 ground truth) and the compiled tier and asserts the
/// produced vectors are <b>value- and validity-identical</b> (bit-exact for float/double).
/// <para>
/// Seeds are <b>fixed</b> (declared in <see cref="Seeds"/>), so repeated runs are byte-identical (AC5);
/// each seed is emitted in any mismatch diagnostic so a regression is replayable from its seed (AC4).
/// The compiled cases are gated with <see cref="DynamicCodeTheoryAttribute"/>, so on a dynamic-code host
/// they run (and the compiled evaluator is proven non-vacuous) while on a NativeAOT / dynamic-code-disabled
/// host they are reported <b>Skipped</b> with a documented reason; the interpreter-only theory below always
/// runs, keeping interpreter coverage green either way (AC3).
/// </para>
/// </summary>
public sealed class BackendParityRandomizedExpressionTests
{
    /// <summary>
    /// The fixed seed corpus. Each seed is a SplitMix64-mixed function of its index, chosen once and
    /// pinned here so the suite is deterministic and reproducible across runs and runtimes (AC5).
    /// </summary>
    public static TheoryData<ulong> Seeds()
    {
        var data = new TheoryData<ulong>();
        for (int i = 1; i <= 250; i++)
        {
            // A fixed, documented spread (golden-ratio step + offset); NOT a clock/Guid/default Random.
            ulong seed = unchecked(((ulong)i * 0x9E3779B97F4A7C15UL) + 0xD1B54A32D192ED03UL);
            data.Add(seed);
        }

        return data;
    }

    // ===== AC2/AC4/AC5: compiled tier == interpreter, value + validity identical, over seeded cases =====

    [DynamicCodeTheory]
    [MemberData(nameof(Seeds))]
    public void Randomized_CompiledEqualsInterpreter_ValueAndValidity(ulong seed)
    {
        GeneratedCase c = BackendParityGenerator.Generate(seed);
        BackendParityOracle.AssertValueParity(
            c.Expression,
            c.Schema,
            c.Batch,
            OperatorKind.Project,
            ParityContext.Randomized(c.RootForm, seed),
            fusableExpected: true);
    }

    // ===== AC3: interpreter coverage stays green even when the compiled tier is skipped/elided =====

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Randomized_InterpreterAlwaysProducesWellFormedVector(ulong seed)
    {
        // This case is NOT gated on dynamic code: it is the interpreter ground truth and must run on
        // every host. It proves the generated tree is well-typed and the interpreter evaluates it into a
        // vector of the right type and the batch's logical length, with no exceptions (Legacy ANSI mode).
        GeneratedCase c = BackendParityGenerator.Generate(seed);
        ExpressionEvaluator interpreted =
            ExpressionEvaluators.Build(c.Expression, c.Schema, BackendParityOracle.InterpretedBackend, OperatorKind.Project);

        var ledger = new BatchEvaluationMemory(BoundedExecutionMemory.Unbounded);
        try
        {
            ColumnVector result = interpreted.Evaluate(c.Batch, ledger, CancellationToken.None);
            Assert.Equal(c.Expression.Type, result.Type);
            Assert.Equal(c.Batch.LogicalRowCount, result.Length);
        }
        finally
        {
            ledger.Release();
        }
    }

    // ===== AC5: determinism — the generator AND the differential are reproducible from a fixed seed =====

    [Fact]
    public void Generator_IsDeterministic_SameSeedSameTreeSchemaAndBatch()
    {
        const ulong seed = 0xC0FFEE_1234_5678UL;
        GeneratedCase a = BackendParityGenerator.Generate(seed);
        GeneratedCase b = BackendParityGenerator.Generate(seed);

        Assert.Equal(a.Rows, b.Rows);
        Assert.Equal(a.RootForm, b.RootForm);
        Assert.Equal(BackendParityOracle.Describe(a.Expression), BackendParityOracle.Describe(b.Expression));
        Assert.Same(a.Schema, b.Schema); // the schema is the shared fixed instance

        // The batches are independently materialized but byte-identical, cell for cell.
        Assert.Equal(a.Batch.LogicalRowCount, b.Batch.LogicalRowCount);
        for (int col = 0; col < a.Schema.Count; col++)
        {
            ColumnVector ca = a.Batch.SelectedColumn(col);
            ColumnVector cb = b.Batch.SelectedColumn(col);
            for (int row = 0; row < ca.Length; row++)
            {
                Assert.Equal(BackendParityOracle.FormatCell(ca, row), BackendParityOracle.FormatCell(cb, row));
            }
        }
    }

    [DynamicCodeFact]
    public void Differential_IsDeterministic_RepeatedRunsAgreeForFixedSeed()
    {
        // Re-running the full differential for the same seed must behave identically every time. We run a
        // handful of seeds three times each; any nondeterminism (e.g. a tier that depends on tiered-JIT
        // state) would surface as an intermittent mismatch here.
        foreach (ulong seed in new ulong[] { 1UL, 2UL, 7UL, 42UL, 0xBADC0FFEEUL })
        {
            for (int pass = 0; pass < 3; pass++)
            {
                GeneratedCase c = BackendParityGenerator.Generate(seed);
                BackendParityOracle.AssertValueParity(
                    c.Expression, c.Schema, c.Batch, OperatorKind.Project, ParityContext.Randomized(c.RootForm, seed));
            }
        }
    }

    // ===== Non-vacuity guard: a generated tree is genuinely served by a compiled delegate =====

    [DynamicCodeTheory]
    [MemberData(nameof(Seeds))]
    public void Randomized_CompiledEvaluatorIsNonVacuous(ulong seed)
    {
        // Every tree the generator emits satisfies CanFuse, so the compiled backend MUST return a real
        // CompiledExpressionEvaluator (not a silent interpreter fallback). If this ever fails, the whole
        // randomized parity theory above would be vacuously green — this is the tripwire that prevents that.
        GeneratedCase c = BackendParityGenerator.Generate(seed);
        ExpressionEvaluator compiled = new CompiledBackend().BuildExpressionEvaluator(c.Expression, c.Schema, OperatorKind.Project);
        Assert.IsType<CompiledExpressionEvaluator>(compiled);
    }
}
