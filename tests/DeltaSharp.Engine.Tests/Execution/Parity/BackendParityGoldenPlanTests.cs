using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Types;
using Xunit;
using ExecutionContext = DeltaSharp.Engine.Execution.ExecutionContext;

namespace DeltaSharp.Engine.Tests.Execution.Parity;

/// <summary>
/// Golden physical-plan parity for all seven v1 operator kinds (STORY-03.5.2 AC1): scan, filter,
/// project, aggregate, sort, join, and exchange-local. Each golden plan is <b>expression-bearing</b>
/// — its predicate / projection / key / aggregate-input carries a non-trivial fusable expression — so
/// the path where interpreter and compiled tier can actually differ is exercised (a plan with no
/// expressions would be a vacuous parity case, since both backends delegate operator execution to the
/// shared <see cref="InterpretedOperators"/> dispatch).
/// <para>
/// Each case asserts two things. First, <b>operator-output parity</b>: running the whole plan with the
/// interpreted-forced backend and with the default (compiled-selected) backend yields byte-identical
/// output rows — the ADR-0001 ground-truth equality. Second, <b>expression-path parity</b>: the
/// operator's bearing expression is evaluated on both tiers over the operator's actual input batch and
/// proven value/validity-identical <i>and</i> served by a real
/// <see cref="DeltaSharp.Engine.Execution.Expressions.CompiledExpressionEvaluator"/> — this is what an
/// injected lowering divergence trips, and it is where the codegen tier's value-add lives once the
/// operator layer wires it. Cases are gated with <see cref="DynamicCodeFactAttribute"/> ("both backends
/// enabled"); on a dynamic-code-disabled host they are reported Skipped (AC3).
/// </para>
/// </summary>
public sealed class BackendParityGoldenPlanTests
{
    // ===================== AC1: the seven operator kinds =====================

    [DynamicCodeFact]
    public void Scan_BothBackends_StreamIdenticalBatches()
    {
        // A scan is the one expression-free operator: its parity is faithful passthrough of the bound
        // source batches. We still assert byte-identical output across both backend selections.
        StructType schema = ScanSchema();
        var scan = new InMemoryScanOperator(schema, [ScanBatch()]);
        AssertPlanOutputsIdentical(scan, ParityContext.Golden("Scan(in-memory)"));
    }

    [DynamicCodeFact]
    public void Filter_BothBackends_And_ExpressionPath_AreIdentical()
    {
        StructType schema = ScanSchema();
        ColumnBatch batch = ScanBatch();
        var scan = new InMemoryScanOperator(schema, [batch]);

        // predicate: (a + b) > c  (int arithmetic compared to double -> double comparison; Legacy so an
        // overflow row becomes NULL -> filtered out, never throws).
        PhysicalExpression predicate = new ComparisonExpression(
            new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add, AnsiMode.Legacy),
            Ref(2, DataTypes.DoubleType),
            ComparisonOperator.GreaterThan);
        var filter = new FilterOperator(scan, predicate);

        AssertPlanOutputsIdentical(filter, ParityContext.Golden("Filter((a+b) > c)"));
        BackendParityOracle.AssertValueParity(predicate, schema, batch, OperatorKind.Filter, ParityContext.Golden("Filter.predicate (a+b) > c"));
    }

    [DynamicCodeFact]
    public void Project_BothBackends_And_ExpressionPath_AreIdentical()
    {
        StructType schema = ScanSchema();
        ColumnBatch batch = ScanBatch();
        var scan = new InMemoryScanOperator(schema, [batch]);

        PhysicalExpression sum = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add, AnsiMode.Legacy);
        PhysicalExpression toLong = new CastExpression(Ref(0, DataTypes.IntegerType), DataTypes.LongType, AnsiMode.Legacy);
        PhysicalExpression gt = new ComparisonExpression(Ref(2, DataTypes.DoubleType), Literal.OfDouble(0.0), ComparisonOperator.GreaterThan);
        var projections = new[] { sum, toLong, gt };

        var output = new StructType(
        [
            new StructField("sum_ab", DataTypes.IntegerType, true),
            new StructField("a_long", DataTypes.LongType, true),
            new StructField("c_pos", DataTypes.BooleanType, true),
        ]);
        var project = new ProjectOperator(scan, output, projections);

        AssertPlanOutputsIdentical(project, ParityContext.Golden("Project(a+b, cast(a long), c>0)"));
        foreach (PhysicalExpression p in projections)
        {
            BackendParityOracle.AssertValueParity(p, schema, batch, OperatorKind.Project, ParityContext.Golden($"Project.element {BackendParityOracle.Describe(p)}"));
        }
    }

    [DynamicCodeFact]
    public void Aggregate_BothBackends_And_ExpressionPath_AreIdentical()
    {
        StructType schema = ScanSchema();
        ColumnBatch batch = ScanBatch();
        var scan = new InMemoryScanOperator(schema, [batch]);

        // group by (k % 4) -> int key; aggregates: SUM(a * 2), COUNT(*), MAX(d).
        PhysicalExpression key = new ArithmeticExpression(Ref(4, DataTypes.IntegerType), Literal.OfInt(4), ArithmeticOperator.Remainder, AnsiMode.Legacy);
        PhysicalExpression sumInput = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Literal.OfInt(2), ArithmeticOperator.Multiply, AnsiMode.Legacy);
        var aggSum = new AggregateExpression(AggregateFunction.Sum, sumInput, AnsiMode.Legacy);
        var aggCount = new AggregateExpression(AggregateFunction.Count, input: null);
        var aggMax = new AggregateExpression(AggregateFunction.Max, Ref(3, DataTypes.LongType));

        var output = new StructType(
        [
            new StructField("k4", DataTypes.IntegerType, true),
            new StructField("sum_a2", aggSum.Type, aggSum.Nullable),
            new StructField("cnt", DataTypes.LongType, false),
            new StructField("max_d", DataTypes.LongType, true),
        ]);
        var aggregate = new AggregateOperator(scan, output, [key], [aggSum, aggCount, aggMax]);

        AssertPlanOutputsIdentical(aggregate, ParityContext.Golden("Aggregate(group k%4; SUM(a*2), COUNT(*), MAX(d))"));
        BackendParityOracle.AssertValueParity(key, schema, batch, OperatorKind.Aggregate, ParityContext.Golden("Aggregate.groupKey k%4"));
        BackendParityOracle.AssertValueParity(sumInput, schema, batch, OperatorKind.Aggregate, ParityContext.Golden("Aggregate.SUM input a*2"));
    }

    [DynamicCodeFact]
    public void Sort_BothBackends_And_ExpressionPath_AreIdentical()
    {
        StructType schema = ScanSchema();
        ColumnBatch batch = ScanBatch();
        var scan = new InMemoryScanOperator(schema, [batch]);

        // primary key: (a + b) descending, nulls last; secondary key: c ascending, nulls first.
        PhysicalExpression primary = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add, AnsiMode.Legacy);
        var orders = new[]
        {
            new SortOrder(primary, SortDirection.Descending, NullOrdering.NullsLast),
            new SortOrder(Ref(2, DataTypes.DoubleType), SortDirection.Ascending, NullOrdering.NullsFirst),
        };
        var sort = new SortOperator(scan, orders);

        AssertPlanOutputsIdentical(sort, ParityContext.Golden("Sort((a+b) DESC NULLS LAST, c ASC)"));
        BackendParityOracle.AssertValueParity(primary, schema, batch, OperatorKind.Sort, ParityContext.Golden("Sort.key (a+b)"));
    }

    [DynamicCodeFact]
    public void Join_BothBackends_And_ExpressionPath_AreIdentical()
    {
        StructType left = new(
        [
            new StructField("la", DataTypes.IntegerType, true),
            new StructField("lb", DataTypes.IntegerType, true),
        ]);
        StructType right = new(
        [
            new StructField("ra", DataTypes.IntegerType, true),
            new StructField("rb", DataTypes.DoubleType, true),
        ]);
        ColumnBatch leftBatch = Batch(left, IntCol(1, 2, null, 3, 2, 7), IntCol(1, 0, 4, 3, 1, null));
        ColumnBatch rightBatch = Batch(right, IntCol(2, 3, 6, null, 2), DblCol(2.0, 3.5, null, 9.0, -1.0));
        var leftScan = new InMemoryScanOperator(left, [leftBatch]);
        var rightScan = new InMemoryScanOperator(right, [rightBatch]);

        // left key: (la + lb); right key: (ra + 0) — both computed/fusable, pairwise int.
        PhysicalExpression leftKey = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add, AnsiMode.Legacy);
        PhysicalExpression rightKey = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Literal.OfInt(0), ArithmeticOperator.Add, AnsiMode.Legacy);

        var output = new StructType(
        [
            new StructField("la", DataTypes.IntegerType, true),
            new StructField("lb", DataTypes.IntegerType, true),
            new StructField("ra", DataTypes.IntegerType, true),
            new StructField("rb", DataTypes.DoubleType, true),
        ]);
        var join = new JoinOperator(leftScan, rightScan, output, JoinType.Inner, [leftKey], [rightKey]);

        AssertPlanOutputsIdentical(join, ParityContext.Golden("Join(Inner on la+lb == ra+0)"));
        BackendParityOracle.AssertValueParity(leftKey, left, leftBatch, OperatorKind.Join, ParityContext.Golden("Join.leftKey la+lb"));
        BackendParityOracle.AssertValueParity(rightKey, right, rightBatch, OperatorKind.Join, ParityContext.Golden("Join.rightKey ra+0"));
    }

    [DynamicCodeFact]
    public void ExchangeLocal_BothBackends_And_ExpressionPath_AreIdentical()
    {
        StructType schema = ScanSchema();
        ColumnBatch batch = ScanBatch();
        var scan = new InMemoryScanOperator(schema, [batch]);

        // hash partition by (a * b) into 4 local partitions.
        PhysicalExpression partKey = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Multiply, AnsiMode.Legacy);
        var exchange = new ExchangeLocalOperator(scan, partitionCount: 4, [partKey]);

        AssertPlanOutputsIdentical(exchange, ParityContext.Golden("ExchangeLocal(4 parts; key a*b)"));
        BackendParityOracle.AssertValueParity(partKey, schema, batch, OperatorKind.ExchangeLocal, ParityContext.Golden("ExchangeLocal.key a*b"));
    }

    // ===================== AC1 (errors): exception parity, scoped per the engine guarantee =====================

    [DynamicCodeFact]
    public void Filter_AnsiOverflow_SingleErrorBatch_BothTiersThrowIdentical()
    {
        // Single-error batch: under ANSI, exactly one row's (a + b) overflows int, so the throwing row,
        // exception type, and message are byte-identical on both tiers (the single-error parity guarantee).
        StructType schema = new(
        [
            new StructField("a", DataTypes.IntegerType, false),
            new StructField("b", DataTypes.IntegerType, false),
        ]);
        ColumnBatch batch = Batch(schema, IntCol(1, 2, int.MaxValue, 4), IntCol(0, 0, 1, 0));
        PhysicalExpression predicate = new ComparisonExpression(
            new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add /* ANSI */),
            Literal.OfInt(0),
            ComparisonOperator.GreaterThan);

        BackendParityOracle.AssertSingleErrorParity<ArithmeticOverflowException>(
            predicate, schema, batch, OperatorKind.Filter, ParityContext.Golden("Filter ANSI single-error (a+b)>0"));
    }

    [DynamicCodeFact]
    public void Project_AnsiMultiErrorKindBatch_BothTiersRaiseAnsiError_TypeMayDiffer()
    {
        // Multi-error-kind batch: (a + b) + (c % d) under ANSI. Row 0 divides by zero (right subtree);
        // row 1 overflows int (left subtree). The interpreter is subtree/child-major and the compiled
        // kernel is row-major, so they may reach DIFFERENT faults first. The engine's documented scope is
        // only that BOTH raise an ANSI arithmetic error — we assert exactly that and NOT type-equality
        // (asserting an invariant the engine does not hold would be wrong). See compiled-expression-fusion.md.
        StructType schema = new(
        [
            new StructField("a", DataTypes.IntegerType, false),
            new StructField("b", DataTypes.IntegerType, false),
            new StructField("c", DataTypes.IntegerType, false),
            new StructField("d", DataTypes.IntegerType, false),
        ]);
        ColumnBatch batch = Batch(schema, IntCol(0, int.MaxValue), IntCol(0, 1), IntCol(7, 5), IntCol(0, 1));
        var expr = new ArithmeticExpression(
            new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add),
            new ArithmeticExpression(Ref(2, DataTypes.IntegerType), Ref(3, DataTypes.IntegerType), ArithmeticOperator.Remainder),
            ArithmeticOperator.Add);

        BackendParityOracle.AssertBothRaiseAnsiArithmeticError(
            expr, schema, batch, OperatorKind.Project, ParityContext.Golden("Project ANSI multi-error (a+b)+(c%d)"));
    }

    [DynamicCodeFact]
    public void BadColumnReference_BothTiersRejectIdenticallyAtBuild()
    {
        // Codegen is optional: an invalid plan must be rejected identically by both tiers, with the same
        // exception type and message (no silent compiled-tier divergence at build time).
        StructType schema = new([new StructField("a", DataTypes.IntegerType, false)]);
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(9, DataTypes.IntegerType), ArithmeticOperator.Add, AnsiMode.Legacy);
        BackendParityOracle.AssertIdenticalBuildRejection<ArgumentException>(
            expr, schema, OperatorKind.Project, ParityContext.Golden("Project bad column reference col[9]"));
    }

    // ===================== plan execution + byte-identical output comparison =====================

    private static void AssertPlanOutputsIdentical(PhysicalOperator plan, ParityContext context)
    {
        List<ColumnBatch> interpreted = RunPlan(plan, ExecutionBackends.Select(new ExecutionBackendOptions { ForceInterpreted = true }));
        List<ColumnBatch> compiled = RunPlan(plan, ExecutionBackends.Select());

        int expectedRows = interpreted.Sum(b => b.LogicalRowCount);
        int actualRows = compiled.Sum(b => b.LogicalRowCount);
        if (expectedRows != actualRows)
        {
            throw new Xunit.Sdk.XunitException(
                $"Plan output row count differs for {context.PlanShape}:\n"
                + $"  plan      : {BackendParityOracle.DescribePlan(plan)}\n"
                + $"  interp    : {expectedRows} rows\n  compiled  : {actualRows} rows");
        }

        StructType outputSchema = plan.OutputSchema;
        for (int col = 0; col < outputSchema.Count; col++)
        {
            List<string> e = FlattenColumn(interpreted, col);
            List<string> a = FlattenColumn(compiled, col);
            if (!e.SequenceEqual(a, StringComparer.Ordinal))
            {
                int row = FirstDifference(e, a);
                throw new Xunit.Sdk.XunitException(
                    $"Plan output mismatch (interpreter oracle vs compiled-selected backend).\n"
                    + $"  plan shape      : {context.PlanShape}\n"
                    + $"  plan            : {BackendParityOracle.DescribePlan(plan)}\n"
                    + $"  backend select  : {context.BackendSelection}\n"
                    + $"  output schema   : {outputSchema.SimpleString}\n"
                    + $"  column          : {col} ('{outputSchema[col].Name}':{outputSchema[col].DataType.SimpleString})\n"
                    + $"  first mismatch  : row {row}\n"
                    + $"      interpreted : {(row < e.Count ? e[row] : "<missing>")}\n"
                    + $"      compiled    : {(row < a.Count ? a[row] : "<missing>")}");
            }
        }
    }

    private static List<ColumnBatch> RunPlan(PhysicalOperator plan, IExecutionBackend backend)
    {
        var ctx = new ExecutionContext(BoundedExecutionMemory.Unbounded);
        var batches = new List<ColumnBatch>();
        using IBatchStream stream = backend.Open(plan, ctx);
        while (stream.TryGetNext(out ColumnBatch? batch))
        {
            batches.Add(batch);
        }

        return batches;
    }

    private static List<string> FlattenColumn(List<ColumnBatch> batches, int ordinal)
    {
        var values = new List<string>();
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector column = batch.SelectedColumn(ordinal);
            for (int i = 0; i < column.Length; i++)
            {
                values.Add(BackendParityOracle.FormatCell(column, i));
            }
        }

        return values;
    }

    private static int FirstDifference(List<string> a, List<string> b)
    {
        int n = Math.Min(a.Count, b.Count);
        for (int i = 0; i < n; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return n;
    }

    // ===================== fixtures + column builders =====================

    private static ColumnReference Ref(int ordinal, DataType type, bool nullable = false) => new(ordinal, type, nullable);

    private static StructType ScanSchema() => new(
    [
        new StructField("a", DataTypes.IntegerType, true),
        new StructField("b", DataTypes.IntegerType, true),
        new StructField("c", DataTypes.DoubleType, true),
        new StructField("d", DataTypes.LongType, true),
        new StructField("k", DataTypes.IntegerType, true),
    ]);

    private static ColumnBatch ScanBatch() => Batch(
        ScanSchema(),
        IntCol(5, 0, null, 7, -5, 13, int.MaxValue, 2, 9, -1),
        IntCol(2, 5, 3, null, -5, -1, 1, 8, 9, 0),
        DblCol(1.5, -2.0, null, 7.0, 0.0, double.NaN, -0.0, 3.25, 100.0, double.PositiveInfinity),
        LongCol(100, -3, null, 9, 7, 4, 2, long.MaxValue, 0, -42),
        IntCol(0, 1, 2, 3, 0, 1, 2, 3, null, 1));

    private static ColumnBatch Batch(StructType schema, params ColumnVector[] columns)
        => new ManagedColumnBatch(schema, columns, columns.Length > 0 ? columns[0].Length : 0);

    private static ColumnVector IntCol(params int?[] values) => BuildCol(DataTypes.IntegerType, values, static (v, x) => v.AppendValue(x));

    private static ColumnVector LongCol(params long?[] values) => BuildCol(DataTypes.LongType, values, static (v, x) => v.AppendValue(x));

    private static ColumnVector DblCol(params double?[] values) => BuildCol(DataTypes.DoubleType, values, static (v, x) => v.AppendValue(x));

    private static ColumnVector BuildCol<T>(DataType type, T?[] values, Action<MutableColumnVector, T> append)
        where T : struct
    {
        MutableColumnVector v = ColumnVectors.Create(type, Math.Max(values.Length, 1));
        foreach (T? x in values)
        {
            if (x.HasValue)
            {
                append(v, x.Value);
            }
            else
            {
                v.AppendNull();
            }
        }

        return v;
    }
}
