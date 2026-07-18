using System;
using System.Collections.Generic;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Storage;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Tests for #597: the Delta sink's per-row constraint enforcement (1) translates predicates under the run's
/// <see cref="AnsiMode"/> (so overflow/cast behavior matches the query path), (2) bounds per-row evaluation by
/// the run's memory budget instead of evaluating unbounded, and (3) refuses fail-closed a write whose table
/// declares more than <c>MaxActiveConstraints</c> active constraints. The mode/budget-explicit
/// <see cref="DeltaLocalSink.EnforceCore"/> is exercised directly; a factory test proves the run's mode is
/// actually threaded through sink construction into enforcement.
/// </summary>
public sealed class DeltaLocalSinkEnforceCoreTests
{
    private static readonly StructType Schema =
        new(new[] { new StructField("v", IntegerType.Instance, nullable: false) });

    private static IReadOnlyList<ColumnBatch> Batch(params int[] values)
    {
        var rows = new List<Row>();
        foreach (int v in values)
        {
            rows.Add(new Row(Schema, v));
        }

        return LocalRelationBatches.Build(Schema, rows);
    }

    private static IReadOnlyList<DeltaTableConstraint> Checks(params string[] expressions)
    {
        var list = new List<DeltaTableConstraint>(expressions.Length);
        for (int i = 0; i < expressions.Length; i++)
        {
            list.Add(new DeltaTableConstraint(DeltaConstraintKind.Check, $"c{i}", expressions[i]));
        }

        return list;
    }

    // ---------- ANSI mode threading ----------

    [Fact]
    public void EnforceCore_AnsiMode_IntegerOverflowInPredicate_ReportsOverflow()
    {
        // Under ANSI, an integer overflow inside a constraint predicate REPORTS the overflow (parity with the
        // query path) rather than silently wrapping. `v + v` at int.MaxValue overflows int32.
        Assert.Throws<ArithmeticOverflowException>(
            () => DeltaLocalSink.EnforceCore(
                Schema, Checks("v + v > 0"), Batch(int.MaxValue), AnsiMode.Ansi, memoryBudgetBytes: null));
    }

    [Fact]
    public void EnforceCore_LegacyMode_IntegerOverflowInPredicate_WrapsAndRejectsRow()
    {
        // Under LEGACY, the SAME overflow wraps (to a negative value), so `v + v > 0` is false → the row is
        // rejected as an ordinary constraint violation, NOT an overflow error. The behavior difference proves
        // the run's ANSI mode is threaded into constraint translation (#597).
        Assert.Throws<DeltaConstraintViolationException>(
            () => DeltaLocalSink.EnforceCore(
                Schema, Checks("v + v > 0"), Batch(int.MaxValue), AnsiMode.Legacy, memoryBudgetBytes: null));
    }

    [Theory]
    [InlineData("delta")]
    public void DeltaSinkFactory_ThreadsRunAnsiMode_IntoEnforcement(string format)
    {
        // Mutation guard for the wiring: the factory must thread the run's ANSI mode through sink construction
        // into the enforcer. A sink built with AnsiMode.Legacy wraps the overflow (rejects the row); one built
        // with AnsiMode.Ansi reports it. A ctor that ignored the mode (hardcoded Ansi) would fail the Legacy case.
        var descriptor = new SinkDescriptor(format, SaveMode.Append, path: "/tmp/deltasharp-597-anisi-thread");

        Assert.True(DeltaSinkFactory.Instance.TryCreate(descriptor, Schema, AnsiMode.Legacy, out ILocalSink? legacySink));
        Assert.Throws<DeltaConstraintViolationException>(
            () => ((IWriteConstraintEnforcer)legacySink!).Enforce(Schema, Checks("v + v > 0"), Batch(int.MaxValue)));

        Assert.True(DeltaSinkFactory.Instance.TryCreate(descriptor, Schema, AnsiMode.Ansi, out ILocalSink? ansiSink));
        Assert.Throws<ArithmeticOverflowException>(
            () => ((IWriteConstraintEnforcer)ansiSink!).Enforce(Schema, Checks("v + v > 0"), Batch(int.MaxValue)));
    }

    // ---------- memory budget threading ----------

    [Fact]
    public void EnforceCore_TinyMemoryBudget_BoundsEvaluation_FailsDeterministically()
    {
        // A real (tiny) budget bounds per-row evaluation: the interpreted evaluator cannot reserve within 1
        // byte, so it fails with a deterministic ExecutionMemoryException instead of evaluating unbounded (#597).
        Assert.Throws<ExecutionMemoryException>(
            () => DeltaLocalSink.EnforceCore(
                Schema, Checks("v > 0"), Batch(1, 2, 3), AnsiMode.Ansi, memoryBudgetBytes: 1));
    }

    [Fact]
    public void EnforceCore_NullBudget_EvaluatesUnbounded_PreservesPre597Behavior()
    {
        // No configured budget ⇒ unbounded evaluation (the pre-#597 behavior): a satisfied constraint passes.
        DeltaLocalSink.EnforceCore(Schema, Checks("v > 0"), Batch(1, 2, 3), AnsiMode.Ansi, memoryBudgetBytes: null);
    }

    [Fact]
    public void EnforceCore_GenerousBudget_Passes()
    {
        // A budget comfortably above the per-row reservation does not spuriously fail a satisfied write.
        DeltaLocalSink.EnforceCore(Schema, Checks("v > 0"), Batch(1, 2, 3), AnsiMode.Ansi, memoryBudgetBytes: 1L << 20);
    }

    // ---------- active-constraint-count cap ----------

    [Fact]
    public void EnforceCore_ConstraintCountAboveCap_RejectedFailClosed_BeforeEvaluation()
    {
        var many = new List<DeltaTableConstraint>();
        for (int i = 0; i < 1025; i++) // MaxActiveConstraints (1024) + 1
        {
            many.Add(new DeltaTableConstraint(DeltaConstraintKind.Check, $"c{i}", "v > 0"));
        }

        var ex = Assert.Throws<InvalidOperationException>(
            () => DeltaLocalSink.EnforceCore(Schema, many, Batch(1), AnsiMode.Ansi, memoryBudgetBytes: null));
        Assert.Contains("1025", ex.Message);
        Assert.Contains("1024", ex.Message);
    }

    [Fact]
    public void EnforceCore_ConstraintCountAtCap_Enforced()
    {
        // Exactly at the cap is allowed (the cap rejects strictly ABOVE MaxActiveConstraints); the satisfied
        // constraints all pass.
        var atCap = new List<DeltaTableConstraint>();
        for (int i = 0; i < 1024; i++)
        {
            atCap.Add(new DeltaTableConstraint(DeltaConstraintKind.Check, $"c{i}", "v > 0"));
        }

        DeltaLocalSink.EnforceCore(Schema, atCap, Batch(1), AnsiMode.Ansi, memoryBudgetBytes: null);
    }
}
