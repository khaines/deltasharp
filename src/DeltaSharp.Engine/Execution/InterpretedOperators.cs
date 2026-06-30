using System.Diagnostics;
using DeltaSharp.Engine.Execution.Expressions;
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
        kind is OperatorKind.Scan or OperatorKind.Filter or OperatorKind.Project;

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
}
