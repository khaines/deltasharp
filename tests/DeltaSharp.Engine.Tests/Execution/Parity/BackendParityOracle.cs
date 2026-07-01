using System.Globalization;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Types;
using Xunit;
using Xunit.Sdk;

namespace DeltaSharp.Engine.Tests.Execution.Parity;

/// <summary>
/// The reproducibility/identity context attached to every parity assertion so a failure is replayable
/// (STORY-03.5.2 AC4): the plan shape under test, how the backend was selected, and the fixed seed (if
/// any). The oracle derives the schema and the expression tree from the inputs it is given, so a
/// diagnostic always carries <i>plan shape, backend selection, seed, schema, expression tree, and the
/// first mismatching row</i>.
/// </summary>
internal sealed record ParityContext(string PlanShape, string BackendSelection, ulong? Seed)
{
    /// <summary>A context for a golden, fixed (non-randomized) plan.</summary>
    public static ParityContext Golden(string planShape) =>
        new(planShape, "interpreted-vectorized (ForceInterpreted) vs compiled (CompiledBackend.BuildExpressionEvaluator)", Seed: null);

    /// <summary>A context for a seeded randomized case.</summary>
    public static ParityContext Randomized(string planShape, ulong seed) =>
        new(planShape, "interpreted-vectorized (oracle) vs compiled (CompiledBackend.BuildExpressionEvaluator)", seed);
}

/// <summary>
/// The differential oracle for STORY-03.5.2: it evaluates one <see cref="PhysicalExpression"/> on the
/// <b>interpreted</b> backend (the ADR-0001 ground truth) and the optional <b>compiled</b> tier over the
/// <i>same</i> batch and asserts the produced vectors are <b>value- and validity-identical</b>
/// (bit-exact for float/double). It enforces non-vacuity (a fusable tree must really be served by a
/// <see cref="CompiledExpressionEvaluator"/>, never a silent interpreter fallback) and scopes ANSI
/// exception parity exactly as the engine guarantees it (single-error batches: identical type+message;
/// multi-error batches: both raise <i>some</i> ANSI arithmetic error — see compiled-expression-fusion.md).
/// Every mismatch raises a <see cref="XunitException"/> carrying the full replay diagnostics.
/// </summary>
internal static class BackendParityOracle
{
    public const string InterpretedBackend = "interpreted-vectorized";

    private static ExpressionEvaluator BuildInterpreted(PhysicalExpression expr, StructType schema, OperatorKind kind)
        => ExpressionEvaluators.Build(expr, schema, InterpretedBackend, kind);

    private static ExpressionEvaluator BuildCompiled(PhysicalExpression expr, StructType schema, OperatorKind kind)
        => new CompiledBackend().BuildExpressionEvaluator(expr, schema, kind);

    private static ColumnVector Run(ExpressionEvaluator evaluator, ColumnBatch batch)
    {
        var ledger = new BatchEvaluationMemory(BoundedExecutionMemory.Unbounded);
        try
        {
            return evaluator.Evaluate(batch, ledger, CancellationToken.None);
        }
        finally
        {
            ledger.Release();
        }
    }

    /// <summary>
    /// Asserts the compiled tier is value/validity-identical to the interpreter over <paramref name="batch"/>
    /// for an expression that is <b>not expected to throw</b> (the randomized suite runs in Legacy ANSI mode,
    /// so overflow/zero-divide become SQL NULL — pure value comparison, no exception flakiness). A fusable
    /// tree is additionally proven to be served by a real <see cref="CompiledExpressionEvaluator"/>.
    /// </summary>
    public static void AssertValueParity(
        PhysicalExpression expr,
        StructType schema,
        ColumnBatch batch,
        OperatorKind kind,
        ParityContext context,
        bool fusableExpected = true)
    {
        ExpressionEvaluator interpreted = BuildInterpreted(expr, schema, kind);
        ExpressionEvaluator compiled = BuildCompiled(expr, schema, kind);
        AssertNonVacuity(compiled, fusableExpected, expr, schema, context);

        ColumnVector expected;
        ColumnVector actual;
        try
        {
            expected = Run(interpreted, batch);
        }
        catch (Exception ex)
        {
            throw Mismatch(context, schema, expr, batch, $"interpreter threw {ex.GetType().Name}: {ex.Message}", row: -1);
        }

        try
        {
            actual = Run(compiled, batch);
        }
        catch (Exception ex)
        {
            throw Mismatch(context, schema, expr, batch, $"compiled tier threw {ex.GetType().Name}: {ex.Message}", row: -1);
        }

        AssertVectorsIdentical(expected, actual, context, schema, expr, batch);
    }

    /// <summary>
    /// Asserts that an expression which throws <b>exactly one error kind</b> across the batch raises the
    /// <i>identical</i> exception (type and message) on both tiers — the engine's single-error ANSI parity
    /// guarantee (compiled-expression-fusion.md).
    /// </summary>
    public static void AssertSingleErrorParity<TException>(
        PhysicalExpression expr,
        StructType schema,
        ColumnBatch batch,
        OperatorKind kind,
        ParityContext context)
        where TException : Exception
    {
        ExpressionEvaluator interpreted = BuildInterpreted(expr, schema, kind);
        ExpressionEvaluator compiled = BuildCompiled(expr, schema, kind);
        AssertNonVacuity(compiled, fusableExpected: true, expr, schema, context);

        TException fromInterpreted = Assert.Throws<TException>(() => Run(interpreted, batch));
        TException fromCompiled = Assert.Throws<TException>(() => Run(compiled, batch));
        if (!string.Equals(fromInterpreted.Message, fromCompiled.Message, StringComparison.Ordinal))
        {
            throw Mismatch(
                context, schema, expr, batch,
                $"single-error ANSI messages differ: interpreter='{fromInterpreted.Message}' compiled='{fromCompiled.Message}'",
                row: -1);
        }
    }

    /// <summary>
    /// Asserts that a <b>multi-error-kind</b> ANSI batch raises <i>some</i> ANSI arithmetic error on both
    /// tiers, <b>without</b> requiring identical type/message: the interpreter is subtree/child-major while
    /// the compiled kernel is row-major, so they may legitimately reach different faults first. This is the
    /// engine's intended, documented scope — asserting type-equality here would assert an invariant the
    /// engine does not hold (compiled-expression-fusion.md).
    /// </summary>
    public static void AssertBothRaiseAnsiArithmeticError(
        PhysicalExpression expr,
        StructType schema,
        ColumnBatch batch,
        OperatorKind kind,
        ParityContext context)
    {
        ExpressionEvaluator interpreted = BuildInterpreted(expr, schema, kind);
        ExpressionEvaluator compiled = BuildCompiled(expr, schema, kind);
        AssertNonVacuity(compiled, fusableExpected: true, expr, schema, context);

        Exception fromInterpreted = Assert.ThrowsAny<Exception>(() => Run(interpreted, batch));
        Exception fromCompiled = Assert.ThrowsAny<Exception>(() => Run(compiled, batch));
        if (!IsAnsiArithmeticError(fromInterpreted))
        {
            throw Mismatch(context, schema, expr, batch, $"interpreter raised non-ANSI exception {fromInterpreted.GetType().Name}", row: -1);
        }

        if (!IsAnsiArithmeticError(fromCompiled))
        {
            throw Mismatch(context, schema, expr, batch, $"compiled tier raised non-ANSI exception {fromCompiled.GetType().Name}", row: -1);
        }
    }

    /// <summary>
    /// Asserts both tiers reject a shape at <b>build time</b> with the identical exception type and message
    /// (e.g. an out-of-range column reference, decimal divide, or an unsupported cast). Codegen is optional:
    /// what the interpreter cannot do, the compiled tier must refuse identically (ADR-0001).
    /// </summary>
    public static void AssertIdenticalBuildRejection<TException>(
        PhysicalExpression expr,
        StructType schema,
        OperatorKind kind,
        ParityContext context)
        where TException : Exception
    {
        TException fromInterpreted = Assert.Throws<TException>(() => BuildInterpreted(expr, schema, kind));
        TException fromCompiled = Assert.Throws<TException>(() => BuildCompiled(expr, schema, kind));
        if (!string.Equals(fromInterpreted.Message, fromCompiled.Message, StringComparison.Ordinal))
        {
            throw new XunitException(
                $"Build-rejection messages differ for {context.PlanShape}:\n"
                + $"  interpreter: {fromInterpreted.Message}\n  compiled   : {fromCompiled.Message}");
        }
    }

    private static bool IsAnsiArithmeticError(Exception e) =>
        e is ArithmeticOverflowException or DivideByZeroException;

    private static void AssertNonVacuity(
        ExpressionEvaluator compiled, bool fusableExpected, PhysicalExpression expr, StructType schema, ParityContext context)
    {
        bool isCompiled = compiled is CompiledExpressionEvaluator;
        if (fusableExpected && !isCompiled)
        {
            throw new XunitException(
                "Non-vacuity violation: a fusable expression was NOT served by the compiled tier (silent "
                + "interpreter fallback), so this parity case proves nothing about codegen.\n"
                + $"  plan shape      : {context.PlanShape}\n"
                + $"  schema          : {schema.SimpleString}\n"
                + $"  expression tree : {Describe(expr)}");
        }

        if (!fusableExpected && isCompiled)
        {
            throw new XunitException(
                $"Expected an interpreter fallback for a non-fusable expression but got a compiled evaluator.\n"
                + $"  expression tree : {Describe(expr)}");
        }
    }

    // ----- value + validity lane comparison (bit-exact for IEEE carriers) -----

    private static void AssertVectorsIdentical(
        ColumnVector expected, ColumnVector actual, ParityContext context, StructType schema, PhysicalExpression expr, ColumnBatch batch)
    {
        if (!expected.Type.Equals(actual.Type))
        {
            throw Mismatch(context, schema, expr, batch, $"result types differ: interpreter '{expected.Type.SimpleString}' vs compiled '{actual.Type.SimpleString}'", row: -1);
        }

        if (expected.Length != actual.Length)
        {
            throw Mismatch(context, schema, expr, batch, $"result lengths differ: interpreter {expected.Length} vs compiled {actual.Length}", row: -1);
        }

        for (int i = 0; i < expected.Length; i++)
        {
            bool expectedNull = expected.IsNull(i);
            bool actualNull = actual.IsNull(i);
            if (expectedNull != actualNull)
            {
                throw Mismatch(
                    context, schema, expr, batch,
                    $"validity differs at row {i}: interpreter={(expectedNull ? "NULL" : "valid")} compiled={(actualNull ? "NULL" : "valid")}",
                    row: i,
                    interpretedValue: FormatValue(expected, i),
                    compiledValue: FormatValue(actual, i));
            }

            if (expectedNull)
            {
                continue;
            }

            if (!ValuesEqual(expected, actual, i))
            {
                throw Mismatch(
                    context, schema, expr, batch,
                    $"value differs at row {i}",
                    row: i,
                    interpretedValue: FormatValue(expected, i),
                    compiledValue: FormatValue(actual, i));
            }
        }
    }

    private static bool ValuesEqual(ColumnVector a, ColumnVector b, int i) => a.Type switch
    {
        BooleanType => a.GetValue<bool>(i) == b.GetValue<bool>(i),
        ByteType => a.GetValue<byte>(i) == b.GetValue<byte>(i),
        ShortType => a.GetValue<short>(i) == b.GetValue<short>(i),
        IntegerType or DateType => a.GetValue<int>(i) == b.GetValue<int>(i),
        LongType or TimestampType => a.GetValue<long>(i) == b.GetValue<long>(i),
        // Bit-exact so -0.0 and NaN payloads are held identical, not merely numerically equal.
        FloatType => BitConverter.SingleToInt32Bits(a.GetValue<float>(i)) == BitConverter.SingleToInt32Bits(b.GetValue<float>(i)),
        DoubleType => BitConverter.DoubleToInt64Bits(a.GetValue<double>(i)) == BitConverter.DoubleToInt64Bits(b.GetValue<double>(i)),
        DecimalType d => d.IsCompact
            ? a.GetValue<long>(i) == b.GetValue<long>(i)
            : a.GetValue<Int128>(i) == b.GetValue<Int128>(i),
        StringType or BinaryType => a.GetBytes(i).SequenceEqual(b.GetBytes(i)),
        _ => throw new XunitException($"unhandled carrier type '{a.Type.SimpleString}'"),
    };

    /// <summary>A stable, bit-exact textual rendering of one cell (public for the plan-output comparator).</summary>
    public static string FormatCell(ColumnVector v, int i) => FormatValue(v, i);

    private static string FormatValue(ColumnVector v, int i)
    {
        if (i < 0 || i >= v.Length)
        {
            return "<out-of-range>";
        }

        if (v.IsNull(i))
        {
            return "NULL";
        }

        return v.Type switch
        {
            BooleanType => v.GetValue<bool>(i) ? "true" : "false",
            ByteType => ((sbyte)v.GetValue<byte>(i)).ToString(CultureInfo.InvariantCulture),
            ShortType => v.GetValue<short>(i).ToString(CultureInfo.InvariantCulture),
            IntegerType or DateType => v.GetValue<int>(i).ToString(CultureInfo.InvariantCulture),
            LongType or TimestampType => v.GetValue<long>(i).ToString(CultureInfo.InvariantCulture),
            FloatType => $"{v.GetValue<float>(i):R} (bits 0x{BitConverter.SingleToInt32Bits(v.GetValue<float>(i)):X8})",
            DoubleType => $"{v.GetValue<double>(i):R} (bits 0x{BitConverter.DoubleToInt64Bits(v.GetValue<double>(i)):X16})",
            DecimalType d => d.IsCompact
                ? $"unscaled {v.GetValue<long>(i)}"
                : $"unscaled {v.GetValue<Int128>(i)}",
            StringType => $"\"{Encoding.UTF8.GetString(v.GetBytes(i))}\"",
            BinaryType => Convert.ToHexString(v.GetBytes(i)),
            _ => "<unprintable>",
        };
    }

    // ----- replay diagnostics (AC4) -----

    private static XunitException Mismatch(
        ParityContext context,
        StructType schema,
        PhysicalExpression expr,
        ColumnBatch batch,
        string summary,
        int row,
        string? interpretedValue = null,
        string? compiledValue = null)
    {
        var sb = new StringBuilder();
        sb.Append("Backend parity mismatch (interpreter is the ADR-0001 oracle; compiled tier must match).\n");
        sb.Append("  summary         : ").Append(summary).Append('\n');
        sb.Append("  plan shape      : ").Append(context.PlanShape).Append('\n');
        sb.Append("  backend select  : ").Append(context.BackendSelection).Append('\n');
        sb.Append("  seed            : ").Append(context.Seed is { } s ? "0x" + s.ToString("X16", CultureInfo.InvariantCulture) : "n/a (fixed golden plan)").Append('\n');
        sb.Append("  schema          : ").Append(schema.SimpleString).Append('\n');
        sb.Append("  expression tree : ").Append(Describe(expr)).Append('\n');
        sb.Append("  rows            : ").Append(batch.LogicalRowCount).Append('\n');
        if (row >= 0)
        {
            sb.Append("  first mismatch  : row ").Append(row).Append('\n');
            sb.Append("      input row   : ").Append(DescribeRow(batch, row)).Append('\n');
            sb.Append("      interpreted : ").Append(interpretedValue ?? "<n/a>").Append('\n');
            sb.Append("      compiled    : ").Append(compiledValue ?? "<n/a>").Append('\n');
        }

        return new XunitException(sb.ToString());
    }

    private static string DescribeRow(ColumnBatch batch, int row)
    {
        var sb = new StringBuilder("{ ");
        for (int c = 0; c < batch.Schema.Count; c++)
        {
            if (c > 0)
            {
                sb.Append(", ");
            }

            ColumnVector col = batch.SelectedColumn(c);
            sb.Append(batch.Schema[c].Name).Append('=').Append(FormatValue(col, row));
        }

        return sb.Append(" }").ToString();
    }

    // ----- structural describers (expression tree + plan shape) -----

    /// <summary>An S-expression-style rendering of <paramref name="expr"/> for diagnostics (AC4).</summary>
    public static string Describe(PhysicalExpression expr) => expr switch
    {
        ColumnReference c => $"col[{c.Ordinal}]:{c.Type.SimpleString}",
        Literal l => l.IsNull ? $"null:{l.Type.SimpleString}" : $"lit({FormatLiteral(l)}):{l.Type.SimpleString}",
        ArithmeticExpression a => $"({Op(a.Operator)} {Describe(a.Left)} {Describe(a.Right)} [{a.Mode}]):{a.Type.SimpleString}",
        ComparisonExpression cmp => $"({Op(cmp.Operator)} {Describe(cmp.Left)} {Describe(cmp.Right)}):bool",
        LogicalExpression lg => lg.Operator == LogicalOperator.Not
            ? $"(not {Describe(lg.Left)}):bool"
            : $"({lg.Operator.ToString().ToLowerInvariant()} {Describe(lg.Left)} {Describe(lg.Right)}):bool",
        CastExpression cast => $"(cast {Describe(cast.Child)} -> {cast.TargetType.SimpleString} [{cast.Mode}])",
        IsNullExpression isn => $"({(isn.Negated ? "isnotnull" : "isnull")} {Describe(isn.Child)}):bool",
        AggregateExpression agg => $"({agg.Function}({(agg.Input is null ? "*" : Describe(agg.Input))}) [{agg.Mode}]):{agg.Type.SimpleString}",
        _ => $"<{expr.GetType().Name}>:{expr.Type.SimpleString}",
    };

    /// <summary>A one-line structural rendering of a physical plan for diagnostics (AC4).</summary>
    public static string DescribePlan(PhysicalOperator op)
    {
        var sb = new StringBuilder();
        DescribePlan(op, sb);
        return sb.ToString();
    }

    private static void DescribePlan(PhysicalOperator op, StringBuilder sb)
    {
        sb.Append(op.Kind);
        switch (op)
        {
            case FilterOperator f:
                sb.Append("[pred=").Append(Describe(f.Predicate)).Append(']');
                break;
            case ProjectOperator p:
                sb.Append("[proj=").Append(string.Join(", ", p.Projections.Select(Describe))).Append(']');
                break;
            case AggregateOperator a:
                sb.Append("[keys=").Append(string.Join(", ", a.GroupingKeys.Select(Describe)))
                  .Append("; aggs=").Append(string.Join(", ", a.Aggregates.Select(Describe))).Append(']');
                break;
            case SortOperator s:
                sb.Append("[orders=").Append(string.Join(", ", s.SortOrders.Select(o => $"{Describe(o.Expression)} {o.Direction} {o.NullOrdering}"))).Append(']');
                break;
            case JoinOperator j:
                sb.Append('[').Append(j.JoinType).Append("; on=")
                  .Append(string.Join(", ", j.LeftKeys.Zip(j.RightKeys, (l, r) => $"{Describe(l)}=={Describe(r)}"))).Append(']');
                break;
            case ExchangeLocalOperator e:
                sb.Append("[parts=").Append(e.PartitionCount).Append("; keys=")
                  .Append(string.Join(", ", e.PartitionKeys.Select(Describe))).Append(']');
                break;
            case InMemoryScanOperator scan:
                sb.Append("[src=").Append(scan.SourceId).Append("; batches=").Append(scan.Batches.Count).Append(']');
                break;
        }

        if (op.Children.Count > 0)
        {
            sb.Append('(');
            for (int i = 0; i < op.Children.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                DescribePlan(op.Children[i], sb);
            }

            sb.Append(')');
        }
    }

    private static string Op(ArithmeticOperator op) => op switch
    {
        ArithmeticOperator.Add => "+",
        ArithmeticOperator.Subtract => "-",
        ArithmeticOperator.Multiply => "*",
        ArithmeticOperator.Divide => "/",
        ArithmeticOperator.Remainder => "%",
        _ => op.ToString(),
    };

    private static string Op(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal => "==",
        ComparisonOperator.NotEqual => "!=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        _ => op.ToString(),
    };

    private static string FormatLiteral(Literal l) => l.Value switch
    {
        null => "null",
        double dv => dv.ToString("R", CultureInfo.InvariantCulture),
        float fv => fv.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString() ?? "?",
    };
}
