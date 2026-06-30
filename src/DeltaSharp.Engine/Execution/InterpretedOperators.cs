using System.Diagnostics;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Shared, AOT-clean dispatch from a v1 <see cref="PhysicalOperator"/> to its pull-based
/// <see cref="IBatchStream"/> kernel, plus the monotonic timing helper the operator streams use.
/// Both the <see cref="InterpretedVectorizedBackend"/> and the (delegating)
/// <see cref="CompiledBackend"/> route operator execution through here, so operator results are
/// identical across backends by construction — the ADR-0001 parity oracle is trivially satisfied for
/// operators because the compiled tier only fuses <i>expressions</i>, never operators (STORY-03.2.1).
/// </summary>
/// <remarks>
/// The dispatch is recursive and lazy: opening a parent opens its child first (building the pipeline
/// structure) but moves no rows; the first <see cref="IBatchStream.TryGetNext"/> drives work. A
/// <see cref="ColumnReference"/> predicate/projection keeps its zero-copy fast path; richer
/// expressions (arithmetic, comparison, boolean, cast, null-check) are bound to an
/// <see cref="ExpressionEvaluator"/> at Open (STORY-03.4.1) — still no row work until the first pull.
/// Shapes the interpreted evaluator does not cover raise <see cref="UnsupportedOperatorException"/>
/// rather than degrade to a row-at-a-time fallback. No member here carries
/// <c>[RequiresDynamicCode]</c>, keeping the interpreter NativeAOT-clean.
/// </remarks>
internal static class InterpretedOperators
{
    private const long NanosPerSecond = 1_000_000_000L;

    /// <summary>The operator kinds the interpreted streams can evaluate in v1 (FEAT-03.2 first slice).</summary>
    public static bool Supports(OperatorKind kind) =>
        kind is OperatorKind.Scan or OperatorKind.Filter or OperatorKind.Project
            or OperatorKind.Aggregate or OperatorKind.Sort or OperatorKind.Join or OperatorKind.ExchangeLocal;

    /// <summary>
    /// Opens <paramref name="op"/> as a pull-based stream, recursively opening children. Building the
    /// stream performs no row work (lazy); <paramref name="backendName"/> attributes any
    /// <see cref="UnsupportedOperatorException"/> to the requesting backend.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A predicate/projection ordinal or type is invalid for the input schema.</exception>
    /// <exception cref="UnsupportedOperatorException">The operator shape is not executable in v1 (no fallback).</exception>
    public static IBatchStream Open(string backendName, PhysicalOperator op, ExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(context);

        return op switch
        {
            InMemoryScanOperator scan => new InterpretedScanStream(scan, context),
            FilterOperator filter => OpenFilter(backendName, filter, context),
            ProjectOperator project => OpenProject(backendName, project, context),
            AggregateOperator aggregate => OpenAggregate(backendName, aggregate, context),
            SortOperator sort => OpenSort(backendName, sort, context),
            JoinOperator join => OpenJoin(backendName, join, context),
            ExchangeLocalOperator exchange => OpenExchangeLocal(backendName, exchange, context),
            ScanOperator => throw new UnsupportedOperatorException(
                op.Kind,
                backendName,
                "scan source is not materialized; bind data through InMemoryScanOperator "
                + "(real Parquet/connector scans are provided by the storage and connector layers)"),
            _ => throw new UnsupportedOperatorException(
                op.Kind, backendName, "operator kernels arrive in later FEAT-03.2 stories"),
        };
    }

    /// <summary>Converts a <see cref="Stopwatch.GetTimestamp"/> start mark to elapsed nanoseconds (monotonic, never wall-clock).</summary>
    public static long ElapsedNanos(long startTimestamp)
    {
        long ticks = Stopwatch.GetTimestamp() - startTimestamp;
        if (ticks <= 0)
        {
            return 0;
        }

        // Split whole seconds from the remainder so neither factor overflows Int64 even for long
        // deltas (ticks * 1e9 would overflow well within a typical query); the remainder is < Frequency.
        long frequency = Stopwatch.Frequency;
        long seconds = ticks / frequency;
        long remainder = ticks % frequency;
        return (seconds * NanosPerSecond) + (remainder * NanosPerSecond / frequency);
    }

    private static IBatchStream OpenFilter(string backendName, FilterOperator filter, ExecutionContext context)
    {
        StructType input = filter.InputSchema(0);

        // A boolean column-reference predicate keeps the zero-copy selection fast path (STORY-03.2.1).
        if (filter.Predicate is ColumnReference column)
        {
            int ordinal = column.Ordinal;

            // Validate the predicate column against the real input schema before opening the child, so a
            // malformed plan fails fast at Open (not mid-stream) and no child stream leaks.
            if ((uint)ordinal >= (uint)input.Count)
            {
                throw new ArgumentException(
                    $"Filter predicate references column {ordinal}, out of range for the input schema ({input.Count} field(s)).",
                    nameof(filter));
            }

            if (input[ordinal].DataType is not BooleanType)
            {
                throw new ArgumentException(
                    $"Filter predicate references column '{input[ordinal].Name}' of type '{input[ordinal].DataType.SimpleString}'; "
                    + "a boolean column is required.", nameof(filter));
            }

            IBatchStream columnChild = Open(backendName, filter.Children[0], context);
            return new InterpretedFilterStream(filter, ordinal, columnChild, context);
        }

        // General predicate (already boolean-typed by the FilterOperator ctor): bind the interpreted
        // evaluator before opening the child so a build-time UnsupportedOperatorException leaks no child.
        ExpressionEvaluator predicate = ExpressionEvaluators.Build(filter.Predicate, input, backendName, OperatorKind.Filter);
        IBatchStream child = Open(backendName, filter.Children[0], context);
        return new InterpretedFilterStream(filter, predicate, child, context);
    }

    private static IBatchStream OpenProject(string backendName, ProjectOperator project, ExecutionContext context)
    {
        StructType input = project.InputSchema(0);
        StructType output = project.OutputSchema;

        // All-column-reference projections stay a zero-copy reorder/rename (STORY-03.2.1); any computed
        // projection switches the whole batch to the materializing path.
        bool allColumnReferences = true;
        for (int i = 0; i < project.Projections.Count; i++)
        {
            if (project.Projections[i] is not ColumnReference)
            {
                allColumnReferences = false;
                break;
            }
        }

        if (allColumnReferences)
        {
            var ordinals = new int[project.Projections.Count];
            for (int i = 0; i < ordinals.Length; i++)
            {
                var column = (ColumnReference)project.Projections[i];
                ValidateColumnProjection(column, i, input, output);
                ordinals[i] = column.Ordinal;
            }

            IBatchStream columnChild = Open(backendName, project.Children[0], context);
            return new InterpretedProjectStream(project, ordinals, columnChild, context);
        }

        var plans = new ProjectionPlan[project.Projections.Count];
        for (int i = 0; i < plans.Length; i++)
        {
            PhysicalExpression expression = project.Projections[i];
            if (expression is ColumnReference column)
            {
                ValidateColumnProjection(column, i, input, output);
                plans[i] = ProjectionPlan.Column(column.Ordinal);
            }
            else
            {
                plans[i] = ProjectionPlan.Computed(ExpressionEvaluators.Build(expression, input, backendName, OperatorKind.Project));
            }
        }

        IBatchStream child = Open(backendName, project.Children[0], context);
        return new InterpretedProjectStream(project, plans, child, context);
    }

    private static void ValidateColumnProjection(ColumnReference column, int index, StructType input, StructType output)
    {
        if ((uint)column.Ordinal >= (uint)input.Count)
        {
            throw new ArgumentException(
                $"Projection {index} references column {column.Ordinal}, out of range for the input schema ({input.Count} field(s)).",
                nameof(column));
        }

        // The output column is the referenced input column verbatim (zero-copy reorder/rename), so
        // their value types must agree; the output field name may differ (an alias).
        if (!input[column.Ordinal].DataType.Equals(output[index].DataType))
        {
            throw new ArgumentException(
                $"Projection {index} reads column '{input[column.Ordinal].Name}' of type '{input[column.Ordinal].DataType.SimpleString}' "
                + $"but output field '{output[index].Name}' is '{output[index].DataType.SimpleString}'.", nameof(column));
        }
    }

    private static IBatchStream OpenAggregate(string backendName, AggregateOperator aggregate, ExecutionContext context)
    {
        StructType input = aggregate.InputSchema(0);
        StructType output = aggregate.OutputSchema;
        int keyCount = aggregate.GroupingKeys.Count;

        // Validate + build the grouping/aggregate machinery before opening the child, so a build-time
        // UnsupportedOperatorException (e.g. a non-byte-sortable key, or AVG(decimal)) leaks no child.
        RowKeyProjection? keyProjection = null;
        if (keyCount > 0)
        {
            for (int k = 0; k < keyCount; k++)
            {
                DataType keyType = aggregate.GroupingKeys[k].Type;
                if (!keyType.Equals(output[k].DataType))
                {
                    throw new ArgumentException(
                        $"Grouping key {k} type '{keyType.SimpleString}' disagrees with output field "
                        + $"'{output[k].Name}' of type '{output[k].DataType.SimpleString}'.", nameof(aggregate));
                }
            }

            keyProjection = new RowKeyProjection(aggregate.GroupingKeys, input, backendName, OperatorKind.Aggregate);
        }

        var aggregators = new Aggregator[aggregate.Aggregates.Count];
        var aggInputs = new ExpressionEvaluator?[aggregate.Aggregates.Count];
        for (int i = 0; i < aggregate.Aggregates.Count; i++)
        {
            if (aggregate.Aggregates[i] is not AggregateExpression term)
            {
                throw new ArgumentException(
                    $"Aggregate {i} must be an AggregateExpression but was '{aggregate.Aggregates[i].GetType().Name}'.",
                    nameof(aggregate));
            }

            if (!term.Type.Equals(output[keyCount + i].DataType))
            {
                throw new ArgumentException(
                    $"Aggregate {i} result type '{term.Type.SimpleString}' disagrees with output field "
                    + $"'{output[keyCount + i].Name}' of type '{output[keyCount + i].DataType.SimpleString}'.", nameof(aggregate));
            }

            aggregators[i] = Aggregator.Create(term, backendName, OperatorKind.Aggregate, context.Memory);
            aggInputs[i] = term.Input is null
                ? null
                : ExpressionEvaluators.Build(term.Input, input, backendName, OperatorKind.Aggregate);
        }

        IBatchStream child = Open(backendName, aggregate.Children[0], context);
        return new InterpretedAggregateStream(aggregate, keyProjection, aggInputs, aggregators, child, context);
    }

    private static IBatchStream OpenSort(string backendName, SortOperator sort, ExecutionContext context)
    {
        StructType input = sort.InputSchema(0);
        var keyExpressions = new PhysicalExpression[sort.SortOrders.Count];
        var orderings = new SortKeyOrdering[sort.SortOrders.Count];
        for (int i = 0; i < sort.SortOrders.Count; i++)
        {
            SortOrder order = sort.SortOrders[i];
            keyExpressions[i] = order.Expression;
            orderings[i] = new SortKeyOrdering(
                order.Direction == SortDirection.Descending ? SortKeyDirection.Descending : SortKeyDirection.Ascending,
                order.NullOrdering == NullOrdering.NullsLast ? NullSortOrder.NullsLast : NullSortOrder.NullsFirst);
        }

        // Build the keyed projection (with the requested per-key orderings) before opening the child.
        var projection = new RowKeyProjection(keyExpressions, input, backendName, OperatorKind.Sort, orderings);
        IBatchStream child = Open(backendName, sort.Children[0], context);
        return new InterpretedSortStream(sort, projection, child, context);
    }

    private static IBatchStream OpenJoin(string backendName, JoinOperator join, ExecutionContext context)
    {
        StructType left = join.InputSchema(0);
        StructType right = join.InputSchema(1);
        StructType output = join.OutputSchema;

        // The output schema must be left++right (inner/outer) or left-only (semi/anti); validate the
        // shape and per-field types so a malformed plan fails fast at Open, not mid-stream.
        if (join.JoinType is JoinType.LeftSemi or JoinType.LeftAnti)
        {
            if (output.Count != left.Count)
            {
                throw new ArgumentException(
                    $"{join.JoinType} output must have {left.Count} left field(s) but has {output.Count}.", nameof(join));
            }

            RequireJoinTypes(output, 0, left, "left", join);
        }
        else
        {
            if (output.Count != left.Count + right.Count)
            {
                throw new ArgumentException(
                    $"{join.JoinType} output must have {left.Count + right.Count} (left++right) field(s) but has {output.Count}.",
                    nameof(join));
            }

            RequireJoinTypes(output, 0, left, "left", join);
            RequireJoinTypes(output, left.Count, right, "right", join);
        }

        var leftKeys = new RowKeyProjection(join.LeftKeys, left, backendName, OperatorKind.Join);
        var rightKeys = new RowKeyProjection(join.RightKeys, right, backendName, OperatorKind.Join);

        IBatchStream probe = Open(backendName, join.Children[0], context);
        IBatchStream build;
        try
        {
            build = Open(backendName, join.Children[1], context);
        }
        catch
        {
            // The probe (left) child is already open; dispose it so a failed build-side open leaks nothing.
            probe.Dispose();
            throw;
        }

        return new InterpretedJoinStream(join, leftKeys, rightKeys, probe, build, context);
    }

    private static void RequireJoinTypes(StructType output, int outputStart, StructType side, string sideName, JoinOperator join)
    {
        for (int i = 0; i < side.Count; i++)
        {
            if (!output[outputStart + i].DataType.Equals(side[i].DataType))
            {
                throw new ArgumentException(
                    $"Join output field {outputStart + i} of type '{output[outputStart + i].DataType.SimpleString}' "
                    + $"disagrees with {sideName} input field {i} of type '{side[i].DataType.SimpleString}'.", nameof(join));
            }
        }
    }

    private static IBatchStream OpenExchangeLocal(string backendName, ExchangeLocalOperator exchange, ExecutionContext context)
    {
        StructType input = exchange.InputSchema(0);

        // Build the partition-key projection (empty keys = round-robin, no projection) before the child.
        RowKeyProjection? keys = exchange.PartitionKeys.Count > 0
            ? new RowKeyProjection(exchange.PartitionKeys, input, backendName, OperatorKind.ExchangeLocal)
            : null;

        IBatchStream child = Open(backendName, exchange.Children[0], context);
        return new InterpretedExchangeLocalStream(exchange, keys, child, context);
    }
}
