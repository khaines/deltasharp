using DeltaSharp.Types;
using CoreJoinType = DeltaSharp.Plans.Logical.JoinType;
using EngineAggregateExpression = DeltaSharp.Engine.Execution.AggregateExpression;
using EngineAggregateFunction = DeltaSharp.Engine.Execution.AggregateFunction;
using EngineColumnReference = DeltaSharp.Engine.Execution.ColumnReference;
using EngineJoinType = DeltaSharp.Engine.Execution.JoinType;
using EnginePhysicalExpression = DeltaSharp.Engine.Execution.PhysicalExpression;
using EngineSortOrder = DeltaSharp.Engine.Execution.SortOrder;
using ExprAlias = DeltaSharp.Plans.Expressions.Alias;
using ExprAnd = DeltaSharp.Plans.Expressions.And;
using ExprAttributeReference = DeltaSharp.Plans.Expressions.AttributeReference;
using ExprBinaryComparison = DeltaSharp.Plans.Expressions.BinaryComparison;
using ExprComparisonOperator = DeltaSharp.Plans.Expressions.ComparisonOperator;
using ExprExpression = DeltaSharp.Plans.Expressions.Expression;
using ExprResolvedFunction = DeltaSharp.Plans.Expressions.ResolvedFunction;
using ExprSortOrder = DeltaSharp.Plans.Expressions.SortOrder;
using LogicalAggregate = DeltaSharp.Plans.Logical.Aggregate;
using LogicalDistinct = DeltaSharp.Plans.Logical.Distinct;
using LogicalFilter = DeltaSharp.Plans.Logical.Filter;
using LogicalJoin = DeltaSharp.Plans.Logical.Join;
using LogicalLimit = DeltaSharp.Plans.Logical.Limit;
using LogicalLocalRelation = DeltaSharp.Plans.Logical.LocalRelation;
using LogicalPlanNode = DeltaSharp.Plans.Logical.LogicalPlan;
using LogicalProject = DeltaSharp.Plans.Logical.Project;
using LogicalResolvedRelation = DeltaSharp.Plans.Logical.ResolvedRelation;
using LogicalSort = DeltaSharp.Plans.Logical.Sort;
using LogicalUnion = DeltaSharp.Plans.Logical.Union;
using LogicalWriteToSource = DeltaSharp.Plans.Logical.WriteToSource;

namespace DeltaSharp.Executor;

/// <summary>
/// The STORY-04.6.2 physical planner: it lowers an analyzed (and optionally optimized) Core
/// <see cref="LogicalPlanNode"/> into a <see cref="PhysicalPlan"/> tree whose nodes map onto EPIC-03
/// executable operators. Each supported logical node maps to exactly one strategy (M1 has no
/// cost-based choice); any node — or expression — with no M1 mapping raises the deterministic
/// <see cref="UnsupportedPlanException"/>.
/// </summary>
internal sealed class PhysicalPlanner
{
    private readonly IScanSource _scanSource;
    private readonly ILocalSinkFactory? _sinkFactory;
    private readonly AnsiMode _mode;

    /// <summary>Creates a planner that resolves scans against <paramref name="scanSource"/>.</summary>
    /// <param name="scanSource">The data-in seam mapping a relation to its in-memory batches.</param>
    /// <param name="sinkFactory">The data-out seam mapping a write intent to a local sink, or
    /// <see langword="null"/> when the planner is read-only (a write plan then fails deterministically).</param>
    /// <param name="mode">The ANSI lens baked into arithmetic/cast expressions (M1 default: ANSI).</param>
    public PhysicalPlanner(IScanSource scanSource, ILocalSinkFactory? sinkFactory = null, AnsiMode mode = AnsiMode.Ansi)
    {
        _scanSource = scanSource ?? throw new ArgumentNullException(nameof(scanSource));
        _sinkFactory = sinkFactory;
        _mode = mode;
    }

    /// <summary>Plans an analyzed logical plan into a physical operator tree.</summary>
    /// <param name="analyzedPlan">The analyzed (optionally optimized) logical plan root.</param>
    /// <returns>The mapped physical plan tree.</returns>
    /// <exception cref="UnsupportedPlanException">A node or expression has no M1 mapping.</exception>
    public PhysicalPlan Plan(LogicalPlanNode analyzedPlan)
    {
        ArgumentNullException.ThrowIfNull(analyzedPlan);
        LogicalOutput outputs = LogicalOutput.Derive(analyzedPlan);
        return PlanNode(analyzedPlan, outputs);
    }

    private PhysicalPlan PlanNode(LogicalPlanNode node, LogicalOutput outputs)
    {
        switch (node)
        {
            case LogicalResolvedRelation relation:
                return PlanScan(relation);

            case LogicalLocalRelation local:
                return PlanLocalRelation(local);

            case LogicalFilter filter:
                return PlanFilter(filter, outputs);

            case LogicalProject project:
                return PlanProject(project, outputs);

            case LogicalAggregate aggregate:
                return PlanAggregate(aggregate, outputs);

            case LogicalJoin join:
                return PlanJoin(join, outputs);

            case LogicalSort sort:
                return PlanSort(sort, outputs);

            case LogicalLimit limit:
                return new LimitPlan(PlanNode(limit.Child, outputs), limit.Count);

            case LogicalDistinct distinct:
                return PlanDistinct(distinct, outputs);

            case LogicalUnion union:
                return PlanUnion(union, outputs);

            case LogicalWriteToSource write:
                return PlanWrite(write, outputs);

            default:
                throw UnsupportedPlanException.ForNode(
                    node.NodeName, "no M1 physical-planning strategy maps this operator to an EPIC-03 operator");
        }
    }

    private PhysicalPlan PlanScan(LogicalResolvedRelation relation)
    {
        if (!_scanSource.TryGetBatches(relation, out var batchFactory))
        {
            throw new UnsupportedPlanException(
                QueryExecutionStage.Scan,
                $"No scan source is registered for relation '{string.Join('.', relation.Identifier)}'. "
                + "The M1 data-in door is the in-memory relation fixture; the public read-door is STORY-04.1.2 (#158).");
        }

        // The relation schema is authoritative for the leaf's batches; the derived attributes (carrying
        // the ExprIds downstream expression resolution binds against) are memoized in `outputs` by
        // LogicalOutput.Derive during the initial traversal, so no per-scan derivation is needed here.
        // The scan-source yields a DEFERRED batch factory the ScanPlan runs on first Execute (under the
        // run's token/budget), so planning — and #179 Explain — performs NO data-plane I/O.
        return new ScanPlan(relation.Schema, batchFactory);
    }

    private static PhysicalPlan PlanLocalRelation(LogicalLocalRelation relation)
    {
        // Unlike a catalog relation (resolved through the IScanSource seam), a LocalRelation carries its
        // rows inline. Defer the row→batch encoding into a thunk the ScanPlan runs on first Execute — so
        // Plan() (which #179 Explain also runs) performs NO enumeration or I/O, honoring the no-work
        // planning invariant (#158 AC1). The memoized LocalRelation.Data is enumerated exactly once, at
        // the first action's execution. The relation schema is authoritative.
        StructType schema = relation.Schema;
        IEnumerable<Row> data = relation.Data;
        return new ScanPlan(schema, token => LocalRelationBatches.Build(schema, data, token));
    }

    private PhysicalPlan PlanFilter(LogicalFilter filter, LogicalOutput outputs)
    {
        PhysicalPlan child = PlanNode(filter.Child, outputs);
        PhysicalExpressionTranslator translator = TranslatorFor(filter.Child, outputs);
        return new FilterPlan(child, translator.Translate(filter.Condition));
    }

    private PhysicalPlan PlanProject(LogicalProject project, LogicalOutput outputs)
    {
        PhysicalPlan child = PlanNode(project.Child, outputs);
        PhysicalExpressionTranslator translator = TranslatorFor(project.Child, outputs);
        var projections = new EnginePhysicalExpression[project.ProjectList.Count];
        for (int i = 0; i < project.ProjectList.Count; i++)
        {
            projections[i] = translator.Translate(project.ProjectList[i]);
        }

        return new ProjectPlan(child, SchemaOf(outputs.OutputOf(project)), projections);
    }

    private PhysicalPlan PlanAggregate(LogicalAggregate aggregate, LogicalOutput outputs)
    {
        PhysicalPlan child = PlanNode(aggregate.Child, outputs);
        PhysicalExpressionTranslator translator = TranslatorFor(aggregate.Child, outputs);

        var groupingKeys = new EnginePhysicalExpression[aggregate.GroupingExpressions.Count];
        for (int i = 0; i < aggregate.GroupingExpressions.Count; i++)
        {
            groupingKeys[i] = translator.Translate(aggregate.GroupingExpressions[i]);
        }

        int groupingCount = aggregate.GroupingExpressions.Count;
        var aggregates = new EngineAggregateExpression[aggregate.AggregateExpressions.Count - groupingCount];
        if (aggregates.Length == 0)
        {
            throw UnsupportedPlanException.ForNode(
                "Aggregate", "an aggregation with no aggregate functions is not an EPIC-03 aggregate (use Distinct for grouping-only)");
        }

        for (int i = groupingCount; i < aggregate.AggregateExpressions.Count; i++)
        {
            ExprExpression element = aggregate.AggregateExpressions[i];
            ExprResolvedFunction function = Unwrap(element) as ExprResolvedFunction
                ?? throw UnsupportedPlanException.ForExpression(
                    element.NodeName, "aggregate output element is not a resolved aggregate function");
            aggregates[i - groupingCount] = translator.TranslateAggregate(function);
        }

        return new AggregatePlan(child, SchemaOf(outputs.OutputOf(aggregate)), groupingKeys, aggregates);
    }

    private PhysicalPlan PlanJoin(LogicalJoin join, LogicalOutput outputs)
    {
        if (join.JoinType == CoreJoinType.Cross)
        {
            throw UnsupportedPlanException.ForNode("Join", "CROSS joins have no EPIC-03 equi-join mapping (deferred)");
        }

        PhysicalPlan left = PlanNode(join.Left, outputs);
        PhysicalPlan right = PlanNode(join.Right, outputs);
        IReadOnlyList<ExprAttributeReference> leftAttrs = outputs.OutputOf(join.Left);
        IReadOnlyList<ExprAttributeReference> rightAttrs = outputs.OutputOf(join.Right);

        if (join.Condition is null)
        {
            throw UnsupportedPlanException.ForNode(
                "Join", "a join without an equi-join condition (cartesian product) has no EPIC-03 mapping");
        }

        var leftKeys = new List<EnginePhysicalExpression>();
        var rightKeys = new List<EnginePhysicalExpression>();
        var leftIds = ExprIdSet(leftAttrs);
        var rightIds = ExprIdSet(rightAttrs);
        PhysicalExpressionTranslator leftTranslator = PhysicalExpressionTranslator.For(leftAttrs, _mode);
        PhysicalExpressionTranslator rightTranslator = PhysicalExpressionTranslator.For(rightAttrs, _mode);

        foreach (ExprExpression conjunct in Conjuncts(join.Condition))
        {
            if (conjunct is not ExprBinaryComparison { Operator: ExprComparisonOperator.Equal } equality)
            {
                throw UnsupportedPlanException.ForNode(
                    "Join", $"non-equi join predicate '{conjunct.SimpleString}' has no EPIC-03 equi-join mapping (theta joins deferred)");
            }

            bool leftFromLeft = ReferencesOnly(equality.Left, leftIds);
            bool rightFromRight = ReferencesOnly(equality.Right, rightIds);
            bool leftFromRight = ReferencesOnly(equality.Left, rightIds);
            bool rightFromLeft = ReferencesOnly(equality.Right, leftIds);

            if (leftFromLeft && rightFromRight)
            {
                leftKeys.Add(leftTranslator.Translate(equality.Left));
                rightKeys.Add(rightTranslator.Translate(equality.Right));
            }
            else if (leftFromRight && rightFromLeft)
            {
                leftKeys.Add(leftTranslator.Translate(equality.Right));
                rightKeys.Add(rightTranslator.Translate(equality.Left));
            }
            else
            {
                throw UnsupportedPlanException.ForNode(
                    "Join",
                    $"equi-join key '{equality.SimpleString}' does not split cleanly across the two inputs "
                    + "(each side must reference exactly one input)");
            }
        }

        if (leftKeys.Count == 0)
        {
            throw UnsupportedPlanException.ForNode("Join", "no equi-join keys were extracted from the condition");
        }

        return new JoinPlan(left, right, SchemaOf(outputs.OutputOf(join)), MapJoinType(join.JoinType), leftKeys, rightKeys);
    }

    private PhysicalPlan PlanSort(LogicalSort sort, LogicalOutput outputs)
    {
        PhysicalPlan child = PlanNode(sort.Child, outputs);
        PhysicalExpressionTranslator translator = TranslatorFor(sort.Child, outputs);
        var orders = new EngineSortOrder[sort.Order.Count];
        for (int i = 0; i < sort.Order.Count; i++)
        {
            orders[i] = sort.Order[i] is ExprSortOrder sortOrder
                ? translator.TranslateSortOrder(sortOrder)
                : new EngineSortOrder(translator.Translate(sort.Order[i]));
        }

        return new SortPlan(child, orders, sort.Global);
    }

    private PhysicalPlan PlanDistinct(LogicalDistinct distinct, LogicalOutput outputs)
    {
        PhysicalPlan child = PlanNode(distinct.Child, outputs);
        StructType childSchema = child.OutputSchema;

        // DISTINCT lowers to GROUP BY (all columns) with a COUNT(*) probe, then a projection that
        // drops the probe — fully EPIC-03-backed (Spark's DISTINCT null-equality matches GROUP BY).
        var groupingKeys = new EnginePhysicalExpression[childSchema.Count];
        var projections = new EnginePhysicalExpression[childSchema.Count];
        var aggregateFields = new StructField[childSchema.Count + 1];
        for (int i = 0; i < childSchema.Count; i++)
        {
            groupingKeys[i] = new EngineColumnReference(i, childSchema[i].DataType, childSchema[i].Nullable);
            projections[i] = new EngineColumnReference(i, childSchema[i].DataType, childSchema[i].Nullable);
            aggregateFields[i] = childSchema[i];
        }

        // The COUNT(*) probe column is dropped by the final ProjectPlan, so its name only has to be
        // unique w.r.t. the child schema; a hardcoded "count" collides when the child already carries a
        // column named "count" (the common df.GroupBy(...).Count().Distinct() idiom), which would make
        // the intermediate StructType throw SchemaValidationException. Derive a collision-proof internal
        // name so distinct() dedups correctly (Spark parity) instead of throwing.
        aggregateFields[childSchema.Count] = new StructField(
            UniqueProbeName(childSchema), LongType.Instance, nullable: false);
        var aggregates = new[] { new EngineAggregateExpression(EngineAggregateFunction.Count, input: null, _mode) };

        var aggregatePlan = new AggregatePlan(child, new StructType(aggregateFields), groupingKeys, aggregates);
        return new ProjectPlan(aggregatePlan, childSchema, projections);
    }

    private static string UniqueProbeName(StructType childSchema)
    {
        var names = new HashSet<string>(childSchema.Count, StringComparer.Ordinal);
        for (int i = 0; i < childSchema.Count; i++)
        {
            names.Add(childSchema[i].Name);
        }

        const string sentinel = "__distinct_count";
        if (!names.Contains(sentinel))
        {
            return sentinel;
        }

        for (int suffix = 0; ; suffix++)
        {
            string candidate = $"{sentinel}_{suffix}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private PhysicalPlan PlanUnion(LogicalUnion union, LogicalOutput outputs)
    {
        var children = new PhysicalPlan[union.Inputs.Count];
        for (int i = 0; i < union.Inputs.Count; i++)
        {
            children[i] = PlanNode(union.Inputs[i], outputs);
        }

        return new UnionPlan(SchemaOf(outputs.OutputOf(union)), children);
    }

    private PhysicalPlan PlanWrite(LogicalWriteToSource write, LogicalOutput outputs)
    {
        PhysicalPlan child = PlanNode(write.Child, outputs);

        // The analyzer has already validated the sink format is a supported LOCAL sink (a deferred or
        // unsupported format threw during analysis, before planning), so a factory miss here means the
        // sink seam was not wired — a deterministic Plan-stage diagnostic, never a silent no-op.
        if (_sinkFactory is null || !_sinkFactory.TryCreate(write.Sink, child.OutputSchema, _mode, out ILocalSink? sink))
        {
            throw new UnsupportedPlanException(
                QueryExecutionStage.Plan,
                $"No local sink is registered for write format '{write.Sink.Format}'. The M1 write door "
                + "executes the in-memory sink; file-format writers (Delta/Parquet) are delivered by EPIC-05.");
        }

        return new WriteToSinkPlan(child, sink, write.Sink);
    }

    private PhysicalExpressionTranslator TranslatorFor(LogicalPlanNode child, LogicalOutput outputs) =>
        PhysicalExpressionTranslator.For(outputs.OutputOf(child), _mode);

    private static EngineJoinType MapJoinType(CoreJoinType joinType) => joinType switch
    {
        CoreJoinType.Inner => EngineJoinType.Inner,
        CoreJoinType.LeftOuter => EngineJoinType.LeftOuter,
        CoreJoinType.RightOuter => EngineJoinType.RightOuter,
        CoreJoinType.FullOuter => EngineJoinType.FullOuter,
        CoreJoinType.LeftSemi => EngineJoinType.LeftSemi,
        CoreJoinType.LeftAnti => EngineJoinType.LeftAnti,
        _ => throw UnsupportedPlanException.ForNode("Join", $"join type '{joinType}' has no EPIC-03 mapping"),
    };

    private static StructType SchemaOf(IReadOnlyList<ExprAttributeReference> attributes)
    {
        // A StructType rejects duplicate field names with a raw SchemaValidationException; detect the
        // collision first so a plan whose output carries a repeated name (e.g. Select(col, col), or an
        // equi-join whose sides share a column name) fails with the contractual deterministic
        // UnsupportedPlanException naming the offender instead. Full duplicate-name support (Spark
        // permits duplicate output names) is deferred to #419.
        var seen = new HashSet<string>(attributes.Count, StringComparer.Ordinal);
        var fields = new StructField[attributes.Count];
        for (int i = 0; i < attributes.Count; i++)
        {
            if (!seen.Add(attributes[i].Name))
            {
                throw new UnsupportedPlanException(
                    $"Physical planning does not support a plan with a duplicate output column name "
                    + $"'{attributes[i].Name}'; rename/alias the column (this commonly arises from "
                    + "Select(col, col) or an equi-join whose sides share a column name) "
                    + "— full duplicate-name support is tracked in #419.");
            }

            fields[i] = new StructField(attributes[i].Name, attributes[i].Type, attributes[i].Nullable);
        }

        return new StructType(fields);
    }

    private static ExprExpression Unwrap(ExprExpression element) =>
        element is ExprAlias alias ? alias.Child : element;

    private static IEnumerable<ExprExpression> Conjuncts(ExprExpression condition)
    {
        if (condition is ExprAnd and)
        {
            foreach (ExprExpression conjunct in Conjuncts(and.Left))
            {
                yield return conjunct;
            }

            foreach (ExprExpression conjunct in Conjuncts(and.Right))
            {
                yield return conjunct;
            }
        }
        else
        {
            yield return condition;
        }
    }

    private static HashSet<long> ExprIdSet(IReadOnlyList<ExprAttributeReference> attributes)
    {
        var ids = new HashSet<long>(attributes.Count);
        foreach (ExprAttributeReference attribute in attributes)
        {
            ids.Add(attribute.ExprId.Value);
        }

        return ids;
    }

    private static bool ReferencesOnly(ExprExpression expression, HashSet<long> ids)
    {
        bool sawReference = false;
        bool ok = true;
        CollectReferences(expression, ids, ref sawReference, ref ok);
        return ok && sawReference;
    }

    private static void CollectReferences(
        ExprExpression expression, HashSet<long> ids, ref bool sawReference, ref bool ok)
    {
        if (expression is ExprAttributeReference attribute)
        {
            sawReference = true;
            if (!ids.Contains(attribute.ExprId.Value))
            {
                ok = false;
            }

            return;
        }

        foreach (ExprExpression child in expression.Children)
        {
            CollectReferences(child, ids, ref sawReference, ref ok);
        }
    }
}
