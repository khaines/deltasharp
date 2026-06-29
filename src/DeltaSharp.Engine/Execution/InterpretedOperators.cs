using System.Diagnostics;
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
/// structure) but moves no rows; the first <see cref="IBatchStream.TryGetNext"/> drives work. v1 only
/// resolves <see cref="ColumnReference"/> predicates/projections — richer expressions (casts,
/// arithmetic) raise <see cref="UnsupportedOperatorException"/> rather than degrade, until the
/// interpreted expression evaluator (STORY-03.4.1) lands. No member here carries
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
        int ordinal = RequireColumnReference(filter.Predicate, backendName, OperatorKind.Filter, "filter predicate");

        // Validate the predicate column against the real input schema before opening the child, so a
        // malformed plan fails fast at Open (not mid-stream) and no child stream leaks.
        StructType input = filter.InputSchema(0);
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

        IBatchStream child = Open(backendName, filter.Children[0], context);
        return new InterpretedFilterStream(filter, ordinal, child, context);
    }

    private static IBatchStream OpenProject(string backendName, ProjectOperator project, ExecutionContext context)
    {
        StructType input = project.InputSchema(0);
        StructType output = project.OutputSchema;

        var ordinals = new int[project.Projections.Count];
        for (int i = 0; i < ordinals.Length; i++)
        {
            int ordinal = RequireColumnReference(project.Projections[i], backendName, OperatorKind.Project, $"projection {i}");
            if ((uint)ordinal >= (uint)input.Count)
            {
                throw new ArgumentException(
                    $"Projection {i} references column {ordinal}, out of range for the input schema ({input.Count} field(s)).",
                    nameof(project));
            }

            // The output column is the referenced input column verbatim (zero-copy reorder/rename), so
            // their value types must agree; the output field name may differ (an alias).
            if (!input[ordinal].DataType.Equals(output[i].DataType))
            {
                throw new ArgumentException(
                    $"Projection {i} reads column '{input[ordinal].Name}' of type '{input[ordinal].DataType.SimpleString}' "
                    + $"but output field '{output[i].Name}' is '{output[i].DataType.SimpleString}'.", nameof(project));
            }

            ordinals[i] = ordinal;
        }

        IBatchStream child = Open(backendName, project.Children[0], context);
        return new InterpretedProjectStream(project, ordinals, child, context);
    }

    private static int RequireColumnReference(PhysicalExpression expression, string backendName, OperatorKind kind, string role)
    {
        if (expression is ColumnReference column)
        {
            return column.Ordinal;
        }

        // No silent row-at-a-time fallback: a non-column-reference expression needs the interpreted
        // expression evaluator (STORY-03.4.1), which is not part of this first operator slice.
        throw new UnsupportedOperatorException(
            kind,
            backendName,
            $"{role} must be a column reference in v1 ('{expression.GetType().Name}' is not yet executable); "
            + "general expression evaluation arrives in STORY-03.4.1");
    }
}
