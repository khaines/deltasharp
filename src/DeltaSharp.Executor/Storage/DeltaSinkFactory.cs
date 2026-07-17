using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Analysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Storage;
using DeltaSharp.Types;
using CoreExpr = DeltaSharp.Plans.Expressions.Expression;
using EnginePhysicalExpression = DeltaSharp.Engine.Execution.PhysicalExpression;
using ExprAttributeReference = DeltaSharp.Plans.Expressions.AttributeReference;

namespace DeltaSharp.Executor;

/// <summary>
/// The Storage↔Executor <b>write</b> adapter (#487): an <see cref="ILocalSinkFactory"/> that resolves the
/// <c>delta</c> write format to a <see cref="DeltaLocalSink"/> driving the storage layer's public
/// <see cref="DeltaWriteTarget"/> facade (Parquet data files + <c>_delta_log</c> commit). It is the mirror
/// of <see cref="InMemorySinkRegistry"/> for a real, durable table. The base <c>delta</c> <b>read</b>
/// provider is #499's responsibility and is a SEPARATE seam — the <see cref="IScanSource"/> data-in path,
/// wired as a sibling scan-source property on <see cref="DeltaStorageAdapter"/>, NOT another entry in the
/// write-side <see cref="CompositeSinkFactory"/> — so wiring the read path never restructures the write
/// path.
/// </summary>
internal sealed class DeltaSinkFactory : ILocalSinkFactory
{
    public static DeltaSinkFactory Instance { get; } = new();

    /// <inheritdoc/>
    public bool TryCreate(SinkDescriptor descriptor, StructType schema, [NotNullWhen(true)] out ILocalSink? sink)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(schema);

        if (!string.Equals(descriptor.Format, WriteFormats.Delta, StringComparison.OrdinalIgnoreCase))
        {
            sink = null;
            return false;
        }

        sink = new DeltaLocalSink(descriptor, schema);
        return true;
    }
}

/// <summary>
/// An <see cref="ILocalSink"/> that commits the write's rows to a real Delta table through the storage
/// layer's public <see cref="DeltaWriteTarget"/> facade. It converts the materialized rows to a columnar
/// batch (the inverse of the read-door encode, reusing <see cref="LocalRelationBatches"/>), then drives the
/// facade's Append/Overwrite lifecycle per the descriptor's <see cref="SaveMode"/>: Append and Overwrite
/// write (creating the table on first write); Ignore skips an existing target; ErrorIfExists throws on one.
/// The facade stages the Parquet files <b>before</b> the log commit, so a mode conflict or a concurrent-write
/// abort leaves the staged files as VACUUM-reclaimable orphans — never a partial commit.
/// </summary>
internal sealed class DeltaLocalSink : ILocalSink
{
    // Spark's DataFrameWriter option / SQL conf selecting how an overwrite replaces data. Both spellings are
    // accepted (case-insensitively) so `.option("partitionOverwriteMode", "dynamic")` and the fully-qualified
    // `spark.sql.sources.partitionOverwriteMode` both work.
    private const string PartitionOverwriteModeOption = "partitionOverwriteMode";
    private const string PartitionOverwriteModeConf = "spark.sql.sources.partitionOverwriteMode";

    // Spark's DataFrameWriter option enabling a destructive schema replacement on overwrite (#496): a full
    // (Static) overwrite with overwriteSchema=true replaces the table schema wholesale.
    private const string OverwriteSchemaOption = "overwriteSchema";

    // Spark's DataFrameWriter option enabling ADDITIVE schema evolution on append (#556): mergeSchema=true
    // lets an append add a new nullable column (or apply a sanctioned type widening the table enables)
    // instead of being rejected by strict enforcement. Distinct from overwriteSchema (a destructive full
    // replacement) — mergeSchema only ever ADDS.
    private const string MergeSchemaOption = "mergeSchema";

    private readonly SinkDescriptor _descriptor;
    private readonly StructType _schema;

    public DeltaLocalSink(SinkDescriptor descriptor, StructType schema)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    /// <inheritdoc/>
    public long Commit(StructType schema, IReadOnlyList<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rows);

        // TRACKED DEFERRAL (#508): ILocalSink.Commit is synchronous, so the async DeltaWriteTarget facade is
        // driven here (and in RunAppend/TableExists) via .GetAwaiter().GetResult() — a sync-over-async bridge
        // that also drops the run's CancellationToken (none is threaded through Commit). An async sink
        // contract that flows the token into staging + the log commit is #508.
        string path = ResolvePath();

        // TRACKED DEFERRAL (#442 unbounded materialization; columnar sink-contract #443): the write is
        // already fully materialized to rows here, then re-materialized rows→ColumnBatch, and again into
        // per-partition batches + a per-file MemoryStream inside the facade — a triple materialization with
        // no spill bound. A columnar/streaming sink contract that avoids the rows→batch round-trip is #443.
        IReadOnlyList<ColumnBatch> batches = LocalRelationBatches.Build(schema, rows);
        IReadOnlyList<string> partitionColumns = _descriptor.PartitionColumns;
        bool mergeSchema = ResolveMergeSchema();

        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(path);
        switch (_descriptor.Mode)
        {
            case SaveMode.Append:
                return RunAppend(target, schema, partitionColumns, batches, mergeSchema);

            case SaveMode.Overwrite:
                // `mergeSchema` governs ADDITIVE evolution on the append family only. A schema change on an
                // overwrite is expressed via `overwriteSchema` (a destructive wholesale replacement), so a
                // `mergeSchema`+overwrite combination is rejected fail-loud rather than silently ignored — it
                // would otherwise mislead a caller into thinking their columns were merged (#556 council:
                // Balanced/Quality R1). Additive-merge-on-overwrite (Spark's mergeSchema-on-overwrite) is a
                // separate, unimplemented concern.
                if (mergeSchema)
                {
                    throw new InvalidOperationException(
                        "The 'mergeSchema' option is not supported with an overwrite. Use 'overwriteSchema' "
                        + "to replace the table schema on a full overwrite, or append with 'mergeSchema' to "
                        + "add columns additively.");
                }

                EnforceConstraints(target, schema, batches, includeExisting: !ResolveOverwriteSchema());
                return target
                    .OverwriteAsync(
                        schema, partitionColumns, batches,
                        ResolvePartitionOverwriteMode(), ResolveOverwriteSchema())
                    .GetAwaiter().GetResult().RowsWritten;

            case SaveMode.Ignore:
                // Re-check existence at the commit boundary (a race that created the table since the
                // pre-commit probe still skips cleanly — Spark parity).
                if (TableExists(target))
                {
                    return 0;
                }

                return RunAppend(target, schema, partitionColumns, batches, mergeSchema);

            case SaveMode.ErrorIfExists:
                if (TableExists(target))
                {
                    throw ErrorIfExistsConflict(path);
                }

                return RunAppend(target, schema, partitionColumns, batches, mergeSchema);

            default:
                throw new InvalidOperationException($"Unknown save mode '{_descriptor.Mode}'.");
        }
    }

    /// <inheritdoc/>
    public bool ShouldSkipOrThrow()
    {
        if (_descriptor.Mode is not (SaveMode.Ignore or SaveMode.ErrorIfExists))
        {
            return false;
        }

        string path = ResolvePath();
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(path);
        if (!TableExists(target))
        {
            return false;
        }

        if (_descriptor.Mode == SaveMode.Ignore)
        {
            return true;
        }

        throw ErrorIfExistsConflict(path);
    }

    private static long RunAppend(
        DeltaWriteTarget target, StructType schema, IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches, bool mergeSchema)
    {
        EnforceConstraints(target, schema, batches, includeExisting: true);
        return target.AppendAsync(schema, partitionColumns, batches, mergeSchema).GetAwaiter().GetResult().RowsWritten;
    }

    // The backend name attributed in any evaluator diagnostic raised while enforcing a constraint predicate.
    private const string ConstraintBackendName = "delta-constraint-enforcement";

    /// <summary>
    /// Enforces the write's active per-row constraints (column invariants + CHECK constraints, #581) over the
    /// write batches BEFORE any Parquet file is staged. The constraint set is the union of the target table's
    /// active constraints and any invariant declared on the incoming write schema (so a fresh create, or a
    /// mergeSchema append that adds a constrained column, validates its own rows). Each is resolved against the
    /// write schema (reusing the query path's parse/resolve/coerce via <see cref="ConstraintExpressionFrontend"/>,
    /// so nested references and non-boolean predicates are handled identically to <c>WHERE</c>), translated to a
    /// physical predicate, and evaluated over every batch. A row is rejected fail-closed when the predicate does
    /// not evaluate to <c>true</c> (i.e. <c>false</c> OR <c>null</c>), matching Delta's
    /// <c>CheckDeltaInvariant.assertRule</c>. <paramref name="includeExisting"/> is false for an
    /// overwrite that replaces the schema (<c>overwriteSchema=true</c>): the existing table's constraints are
    /// dropped with the schema, so only the incoming schema's invariants apply.
    /// </summary>
    /// <remarks>
    /// TOCTOU: the constraint set is read from the table's latest committed version, read independently of the
    /// commit path — so under concurrency it and the commit's base snapshot can differ. Threading a single
    /// snapshot through enforcement and the commit is tracked in #596. The constraint set is trusted table
    /// metadata; the untrusted input (the batch rows) is what is validated here.
    /// </remarks>
    private static void EnforceConstraints(
        DeltaWriteTarget target, StructType schema, IReadOnlyList<ColumnBatch> batches, bool includeExisting)
    {
        IReadOnlyList<DeltaTableConstraint> constraints =
            target.LoadActiveConstraintsAsync(schema, includeExisting).GetAwaiter().GetResult();
        if (constraints.Count == 0)
        {
            return;
        }

        foreach (DeltaTableConstraint constraint in constraints)
        {
            (CoreExpr predicate, IReadOnlyList<ExprAttributeReference> input) =
                ConstraintExpressionFrontend.ParseResolveWithInput(constraint.Expression, schema);
            EnginePhysicalExpression physical =
                PhysicalExpressionTranslator.For(input, AnsiMode.Ansi).Translate(predicate);
            BatchPredicateEvaluator evaluator = BatchPredicateEvaluator.Build(physical, schema, ConstraintBackendName);

            for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                ColumnVector result = evaluator.Evaluate(batches[batchIndex], BoundedExecutionMemory.Unbounded);
                for (int row = 0; row < result.Length; row++)
                {
                    if (result.IsNull(row) || !result.GetValue<bool>(row))
                    {
                        throw DeltaConstraintViolationException.ForRow(constraint, batchIndex, row);
                    }
                }
            }
        }
    }

    private static bool TableExists(DeltaWriteTarget target) =>
        target.TableExistsAsync().GetAwaiter().GetResult();

    private DeltaPartitionOverwriteMode ResolvePartitionOverwriteMode()
    {
        string? value = null;
        if (_descriptor.Options.TryGetValue(PartitionOverwriteModeOption, out string? optionValue))
        {
            value = optionValue;
        }
        else if (_descriptor.Options.TryGetValue(PartitionOverwriteModeConf, out string? confValue))
        {
            value = confValue;
        }

        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "static" => DeltaPartitionOverwriteMode.Static,
            "dynamic" => DeltaPartitionOverwriteMode.Dynamic,
            _ => throw new InvalidOperationException(
                $"Unsupported '{PartitionOverwriteModeOption}' value '{value}'. DeltaSharp recognizes "
                + "'static' and 'dynamic'."),
        };
    }

    // Resolves the connector `mergeSchema` write option (#556). Absent/empty/false ⇒ strict enforcement (the
    // default); true ⇒ an append may ADD a new nullable column (or apply a sanctioned widening the table
    // enables), evolving the schema. Mirrors ResolveOverwriteSchema.
    private bool ResolveMergeSchema()
    {
        if (!_descriptor.Options.TryGetValue(MergeSchemaOption, out string? value))
        {
            return false;
        }

        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "false" => false,
            "true" => true,
            _ => throw new InvalidOperationException(
                $"Unsupported '{MergeSchemaOption}' value '{value}'. DeltaSharp recognizes 'true' and 'false'."),
        };
    }

    // Resolves the connector `overwriteSchema` write option (#496). Absent/empty/false ⇒ additive enforcement
    // (the default); true ⇒ a full (Static) overwrite replaces the table schema wholesale. Combined with a
    // dynamic partition overwrite it is rejected downstream by the write-door (fail-closed).
    private bool ResolveOverwriteSchema()
    {
        if (!_descriptor.Options.TryGetValue(OverwriteSchemaOption, out string? value))
        {
            return false;
        }

        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "false" => false,
            "true" => true,
            _ => throw new InvalidOperationException(
                $"Unsupported '{OverwriteSchemaOption}' value '{value}'. DeltaSharp recognizes 'true' and 'false'."),
        };
    }

    // The delta sink writes to a real storage path; a path is mandatory. Resolution (descriptor path or a
    // case-insensitive `path` option) is shared with InMemorySinkRegistry via SinkDescriptorPaths so the two
    // sinks never drift; only the "no path" outcome differs (the delta sink cannot default a target).
    private string ResolvePath() =>
        SinkDescriptorPaths.ResolvePath(_descriptor)
        ?? throw new InvalidOperationException(
            "A delta write requires an output path (df.write.format(\"delta\").save(path)).");

    private static InvalidOperationException ErrorIfExistsConflict(string path) =>
        new($"Cannot write to '{SecretRedaction.RedactPath(path)}': it already exists and the save mode is "
            + "ErrorIfExists. Use Overwrite, Append, or Ignore to write to an existing table.");
}
