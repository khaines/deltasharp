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
using ExprGetStructField = DeltaSharp.Plans.Expressions.GetStructField;

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
    public bool TryCreate(SinkDescriptor descriptor, StructType schema, AnsiMode ansiMode, [NotNullWhen(true)] out ILocalSink? sink)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(schema);

        if (!string.Equals(descriptor.Format, WriteFormats.Delta, StringComparison.OrdinalIgnoreCase))
        {
            sink = null;
            return false;
        }

        sink = new DeltaLocalSink(descriptor, schema, ansiMode);
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
internal sealed class DeltaLocalSink : ILocalSink, IWriteConstraintEnforcer
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

    // #597: an upper bound on how many active per-row constraints a single write will enforce. A real Delta
    // table declares a handful of CHECK constraints / invariants; this cap (generously above any realistic
    // table) fail-closes a write whose table metadata declares a pathological number of constraints rather
    // than doing unbounded per-row resolve+evaluate work. Predicate DEPTH is separately bounded by the
    // constraint frontend (MaxConstraintExpressionDepth), so count + depth together bound the enforcement work.
    private const int MaxActiveConstraints = 1024;

    private readonly SinkDescriptor _descriptor;
    private readonly StructType _schema;
    private readonly AnsiMode _ansiMode;

    // #597: the run's operator memory budget, captured from the Commit call (execute-time) and used to bound
    // per-row constraint evaluation. Null (unbounded) until Commit sets it.
    private long? _memoryBudgetBytes;

    public DeltaLocalSink(SinkDescriptor descriptor, StructType schema, AnsiMode ansiMode)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _ansiMode = ansiMode;
    }

    /// <inheritdoc/>
    public long Commit(StructType schema, IReadOnlyList<Row> rows, long? memoryBudgetBytes = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rows);

        // #597: capture the run's memory budget so Enforce (invoked from inside the write primitive, below)
        // bounds its per-row constraint evaluation by the same budget as the rest of the run.
        _memoryBudgetBytes = memoryBudgetBytes;

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

                // #596: enforcement now runs INSIDE the write primitive (against the commit's own snapshot,
                // post-reconcile), so hand it the enforcer instead of pre-validating here.
                return target
                    .OverwriteAsync(
                        schema, partitionColumns, batches,
                        ResolvePartitionOverwriteMode(), ResolveOverwriteSchema(), enforcer: this)
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

    private long RunAppend(
        DeltaWriteTarget target, StructType schema, IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches, bool mergeSchema)
    {
        // #596: enforcement runs INSIDE AppendAsync (sharing the commit's snapshot); pass the enforcer.
        return target.AppendAsync(schema, partitionColumns, batches, mergeSchema, enforcer: this)
            .GetAwaiter().GetResult().RowsWritten;
    }

    // The backend name attributed in any evaluator diagnostic raised while enforcing a constraint predicate.
    private const string ConstraintBackendName = "delta-constraint-enforcement";

    /// <summary>
    /// The storage layer's <see cref="IWriteConstraintEnforcer"/> hook (#581/#596): evaluates each active
    /// per-row constraint (column invariant / CHECK) the write primitive collected over the write batches.
    /// The primitive calls this from inside <see cref="DeltaWriteTarget.AppendAsync"/> /
    /// <see cref="DeltaWriteTarget.OverwriteAsync"/> — against the SAME snapshot the commit bases on and after
    /// the physical write shape is resolved, BEFORE any Parquet file is staged — so enforcement and the commit
    /// can never diverge (no TOCTOU) and the write door cannot be bypassed. Each predicate is resolved against
    /// the write <paramref name="schema"/> (reusing the query path's parse/resolve/coerce via
    /// <see cref="ConstraintExpressionFrontend"/>, so nested references and non-boolean predicates behave
    /// exactly as in <c>WHERE</c>), translated to a physical predicate, and evaluated over every batch. A row
    /// is rejected fail-closed when the predicate does not evaluate to <c>true</c> (i.e. <c>false</c> OR
    /// <c>null</c>), matching Delta's <c>CheckDeltaInvariant.assertRule</c>.
    /// </summary>
    /// <remarks>
    /// The constraint set is trusted table metadata; the untrusted input (the batch rows) is what is validated
    /// here. The run's <see cref="AnsiMode"/> (so overflow/cast behavior inside a constraint predicate matches
    /// the query path) and its operator memory budget (so per-row evaluation is bounded, not unbounded) are
    /// threaded in from the sink's construction and <see cref="Commit"/> respectively; the number of active
    /// constraints is bounded by <see cref="MaxActiveConstraints"/> and predicate depth by the constraint
    /// frontend, so a pathological table cannot drive unbounded enforcement work (#597).
    /// </remarks>
    public void Enforce(
        StructType schema,
        IReadOnlyList<DeltaTableConstraint> constraints,
        IReadOnlyList<ColumnBatch> batches,
        StructType? priorSchema = null) =>
        EnforceCore(schema, constraints, batches, _ansiMode, _memoryBudgetBytes, priorSchema);

    /// <summary>
    /// The ANSI-mode- and memory-budget-explicit core of <see cref="Enforce"/> (#597), so the mode and budget
    /// the enforcement uses are unit-testable directly (the instance <see cref="Enforce"/> delegates here with
    /// the run's threaded values). See <see cref="Enforce"/> for the enforcement contract.
    /// </summary>
    internal static void EnforceCore(
        StructType schema,
        IReadOnlyList<DeltaTableConstraint> constraints,
        IReadOnlyList<ColumnBatch> batches,
        AnsiMode ansiMode,
        long? memoryBudgetBytes,
        StructType? priorSchema = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentNullException.ThrowIfNull(batches);

        // #597: bound the number of active per-row constraints a single write resolves + evaluates. A real
        // table declares a handful; a pathological count is refused fail-closed rather than driving unbounded
        // per-row work (predicate DEPTH is separately bounded by the constraint frontend).
        if (constraints.Count > MaxActiveConstraints)
        {
            throw new InvalidOperationException(
                $"This write targets a table with {constraints.Count} active per-row constraints, exceeding the "
                + $"maximum {MaxActiveConstraints} DeltaSharp enforces in one write; the write is refused "
                + "fail-closed rather than performing unbounded per-row constraint evaluation.");
        }

        // #619 — reference-based dependent-column check (only when the schema is being REPLACED, i.e. priorSchema
        // is supplied). Resolution-failure detection (Phase 1) catches a DROPPED/RENAMED column a CHECK reads,
        // but a type-COMPATIBLE RETYPE (int→bigint under `id > 0`, or a struct-field type change under `s.f > 0`)
        // resolves cleanly and would silently enforce on the NEW type — where Delta blocks with
        // DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE. This pass compares the type at each referenced PATH between
        // the prior and new schema (path-precise, mirroring Delta's reference-based block) and reports a broken
        // dependency BEFORE Phase 1. An INCOMPATIBLE retype (e.g. int→string) is left to Phase-1's DataTypeMismatch
        // — matching Delta, which surfaces a TYPE error (canChangeDataType) before ever reaching the dependent
        // check for a disallowed change.
        if (priorSchema is not null)
        {
            DetectRetypedDependencies(priorSchema, schema, constraints);
        }

        // Phase 1 — resolve every constraint against the write schema. A surviving named CHECK that no longer
        // resolves because the write schema DROPPED a top-level column it references (an overwriteSchema /
        // future ALTER) is aggregated per dropped column and reported as Delta's parity error
        // DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE (#598) BEFORE any row is evaluated — mirroring
        // foundViolatingConstraintsForColumnChange, which lists ALL dependent constraints for the column so the
        // caller can DROP them in one pass. Resolving up front (not interleaved with eval) keeps the reported
        // error deterministic for a set that mixes an unresolvable CHECK with a violating row.
        var resolved =
            new List<(DeltaTableConstraint Constraint, CoreExpr Predicate, IReadOnlyList<ExprAttributeReference> Input)>(
                constraints.Count);
        Dictionary<string, List<DeltaTableConstraint>>? dependentsByColumn = null;
        List<string>? droppedColumnOrder = null;

        foreach (DeltaTableConstraint constraint in constraints)
        {
            CoreExpr predicate;
            IReadOnlyList<ExprAttributeReference> input;
            try
            {
                (predicate, input) = ConstraintExpressionFrontend.ParseResolveWithInput(constraint.Expression, schema);
            }
            catch (AnalysisException ex)
                when (constraint.Kind == DeltaConstraintKind.Check
                    && (ex.Kind == AnalysisErrorKind.UnresolvedColumn
                        || ex.Kind == AnalysisErrorKind.UnresolvedStructField)
                    && ex.Reference is not null)
            {
                // A SURVIVING CHECK references a column the write schema no longer resolves — an
                // overwriteSchema (or future ALTER) dropped/renamed the TOP-LEVEL column
                // (UnresolvedColumn), or a NESTED field the CHECK reads was dropped/renamed or its base
                // struct retyped away from a struct (UnresolvedStructField, #600). Collect it (aggregated
                // per column, normalizing a nested reference `s.f` to its top-level column `s`) and surface
                // the Delta parity error after the pass, instead of the raw analyzer failure. Only named
                // CHECK constraints are reported: a column invariant is attached to its own field and cannot
                // be DROP CONSTRAINT'd, and a new write-schema invariant that fails to resolve is a
                // different, non-reclassified error. An INCOMPATIBLE top-level retype of a referenced column
                // (e.g. int->string under `id > 0`) surfaces here as the comparison's DataTypeMismatch — which
                // is NOT one of the reclassified kinds, so it stays fail-closed but un-reclassified (a genuine
                // type error, matching Delta's canChangeDataType-first ordering). When a prior schema IS available
                // (an overwriteSchema replacement) the #619 pre-pass already handled drops/renames and COMPATIBLE
                // retypes before this pass ran; so with a prior schema this catch only sees an incompatible-retype
                // resolution failure, and WITHOUT one (a direct EnforceCore call) it is the primary drop/rename
                // reclassification path.
                //
                // #618: prefer the analyzer's structured RootColumn (the bound base struct column for a nested
                // access, or NameParts[0] for a plain/quoted-dot column) over splitting the flattened Reference
                // string — the latter mis-attributes a qualified `t.s.f` (→ `t`, not the base `s`) or a
                // quoted-dot column `a.b` (→ `a`, not `a.b`). TopLevelColumn(Reference) is a defensive fallback
                // (every current throw site sets RootColumn; the fallback guards a future site that does not).
                string column = ex.RootColumn ?? TopLevelColumn(ex.Reference);
                dependentsByColumn ??= new Dictionary<string, List<DeltaTableConstraint>>(StringComparer.OrdinalIgnoreCase);
                droppedColumnOrder ??= new List<string>();
                if (!dependentsByColumn.TryGetValue(column, out List<DeltaTableConstraint>? dependents))
                {
                    dependents = new List<DeltaTableConstraint>();
                    dependentsByColumn.Add(column, dependents);
                    droppedColumnOrder.Add(column);
                }

                dependents.Add(constraint);
                continue;
            }

            resolved.Add((constraint, predicate, input));
        }

        if (droppedColumnOrder is { Count: > 0 })
        {
            // Report the first dropped column deterministically (constraints arrive in CollectForWrite's stable
            // Kind-then-name order), listing every CHECK that depends on it.
            string column = droppedColumnOrder[0];
            throw DeltaConstraintDependentColumnException.ForColumnChange(column, dependentsByColumn![column]);
        }

        // Phase 2 — evaluate every resolved predicate over every batch; reject fail-closed on the first row that
        // is NOT TRUE (false OR null), matching Delta's CheckDeltaInvariant.assertRule. The predicate is
        // translated under the run's ANSI mode (#597), and each batch evaluation is bounded by the run's memory
        // budget (#597) — unbounded only when the run itself is.
        foreach ((DeltaTableConstraint constraint, CoreExpr predicate, IReadOnlyList<ExprAttributeReference> input) in resolved)
        {
            EnginePhysicalExpression physical =
                PhysicalExpressionTranslator.For(input, ansiMode).Translate(predicate);
            BatchPredicateEvaluator evaluator = BatchPredicateEvaluator.Build(physical, schema, ConstraintBackendName);

            for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                ColumnVector result = evaluator.Evaluate(batches[batchIndex], CreateEvaluationMemory(memoryBudgetBytes));
                for (int row = 0; row < result.Length; row++)
                {
                    if (RowRejected(result, row))
                    {
                        throw DeltaConstraintViolationException.ForRow(constraint, batchIndex, row);
                    }
                }
            }
        }
    }

    // #597: the memory a single constraint-batch evaluation is bounded by — the run's operator budget when
    // configured, else unbounded (the pre-#597 behavior). A fresh instance per evaluation bounds each batch's
    // peak scratch by the budget (matching the per-Evaluate semantics of the prior Unbounded singleton).
    private static IExecutionMemory CreateEvaluationMemory(long? memoryBudgetBytes) =>
        memoryBudgetBytes is { } budget
            ? new BoundedExecutionMemory(budget)
            : BoundedExecutionMemory.Unbounded;

    // #619: reference-based dependent-column detection for a schema REPLACEMENT (overwriteSchema), matching
    // Delta's reference-based dependency block (SchemaUtils.findDependentConstraints / containsDependentExpression,
    // which is PATH-PRECISE and prefix-based). For each surviving named CHECK, resolve it against the PRIOR
    // schema (where it was valid) and, per referenced column PATH (`id`, or a nested `s.f`), compare the type at
    // that exact path against the new schema:
    //   • the path is GONE in the new schema (a field/base dropped or renamed)      → dependency broken (report);
    //   • the leaf type CHANGED and the predicate still type-resolves (a COMPATIBLE  → dependency broken (report):
    //     retype like int→bigint, which resolution-failure detection alone misses,     Delta blocks this;
    //     silently enforcing on the new type);
    //   • the leaf type CHANGED but the predicate NO LONGER type-resolves (an          → NOT reported here: Delta
    //     INCOMPATIBLE retype like int→string)                                          runs canChangeDataType
    //     first and surfaces a TYPE error, not the dependent-column error — left to Phase-1's DataTypeMismatch;
    //   • the leaf type is UNCHANGED (a nullability-only / metadata-only / sibling-     → NOT a dependency break:
    //     field / field-reorder change that does not alter the REFERENCED leaf type)     Delta gates on the field's
    //     dataType only.
    // The report names the TOP-LEVEL column (the overwriteSchema trigger replaces whole top-level columns). Only
    // named CHECK constraints participate: a column invariant is attached to its own field and travels with it.
    private static void DetectRetypedDependencies(
        StructType priorSchema, StructType newSchema, IReadOnlyList<DeltaTableConstraint> constraints)
    {
        Dictionary<string, List<DeltaTableConstraint>>? brokenByColumn = null;
        List<string>? brokenOrder = null;

        foreach (DeltaTableConstraint constraint in constraints)
        {
            if (constraint.Kind != DeltaConstraintKind.Check)
            {
                continue;
            }

            CoreExpr priorPredicate;
            try
            {
                (priorPredicate, _) = ConstraintExpressionFrontend.ParseResolveWithInput(constraint.Expression, priorSchema);
            }
            catch (AnalysisException)
            {
                // The CHECK does not cleanly resolve against the PRIOR schema either — not a dependency signal we
                // can attribute. Leave it to Phase-1 resolution against the new schema.
                continue;
            }

            // Does the predicate still type-resolve against the NEW schema? Distinguishes a COMPATIBLE retype
            // (still resolves → dependency block) from an INCOMPATIBLE one (Delta reports a TYPE error instead).
            bool resolvesAgainstNew;
            try
            {
                ConstraintExpressionFrontend.ParseResolveWithInput(constraint.Expression, newSchema);
                resolvesAgainstNew = true;
            }
            catch (AnalysisException)
            {
                resolvesAgainstNew = false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string RootColumn, string PathKey, IReadOnlyList<string> Path, DataType LeafType) reference
                in CollectReferencedLeaves(priorPredicate))
            {
                if (!seen.Add(reference.PathKey))
                {
                    continue; // the same path referenced multiple times in one predicate is counted once
                }

                DataType? newLeafType = ResolveLeafType(newSchema, reference.RootColumn, reference.Path);
                bool dependencyBroken;
                if (newLeafType is null)
                {
                    // The referenced field/base is gone: a genuine DROP/RENAME, or a base RETYPED to a non-struct
                    // so a nested path can no longer navigate (e.g. `s` struct→scalar under `s.f`). Both are
                    // reported as the dependency-change parity error — consistent with #600's non-struct-base
                    // classification. (Delta's ALTER path would surface a canChangeDataType TYPE error for the
                    // base-retype sub-case specifically; DeltaSharp's whole-column-replacement model reports the
                    // dependency uniformly. Both fail-closed and reject a Delta-illegal struct→scalar change.)
                    dependencyBroken = true;
                }
                else if (!newLeafType.Equals(reference.LeafType))
                {
                    // A retype of the referenced leaf. Report only the COMPATIBLE one (the predicate still
                    // type-resolves against the new schema); an INCOMPATIBLE retype is left to Phase-1's
                    // DataTypeMismatch — modelling Delta's canChangeDataType-first ordering. resolvesAgainstNew is
                    // a pragmatic proxy for canChangeDataType's allowed-widening rules (exact parity would need an
                    // explicit widening table); it is conservative and fail-closed for the current grammar.
                    dependencyBroken = resolvesAgainstNew;
                }
                else
                {
                    dependencyBroken = false; // unchanged leaf type — nullability/metadata/sibling/reorder is invisible
                }

                if (!dependencyBroken)
                {
                    continue;
                }

                brokenByColumn ??= new Dictionary<string, List<DeltaTableConstraint>>(StringComparer.OrdinalIgnoreCase);
                brokenOrder ??= new List<string>();
                if (!brokenByColumn.TryGetValue(reference.RootColumn, out List<DeltaTableConstraint>? deps))
                {
                    deps = new List<DeltaTableConstraint>();
                    brokenByColumn.Add(reference.RootColumn, deps);
                    brokenOrder.Add(reference.RootColumn);
                }

                if (!deps.Contains(constraint))
                {
                    deps.Add(constraint);
                }
            }
        }

        if (brokenOrder is { Count: > 0 })
        {
            // Report the first affected column deterministically (constraints arrive in CollectForWrite's stable
            // Kind-then-name order), listing every CHECK that depends on it.
            string column = brokenOrder[0];
            throw DeltaConstraintDependentColumnException.ForColumnChange(column, brokenByColumn![column]);
        }
    }

    // Collects the referenced column PATHS of a resolved predicate — a plain column `id` (empty field path) or a
    // nested access `s.f`/`s.a.b` (root `s` + field path `[f]`/`[a,b]`) — each with the type AT THE REFERENCED
    // LEAF (not the whole top-level column's type). A GetStructField's own child chain is NOT re-yielded as a
    // separate top-level reference, so `s.f` contributes exactly one path (`s`+`[f]`), keyed on the leaf `f`'s
    // type, never on the whole struct `s`.
    private static IEnumerable<(string RootColumn, string PathKey, IReadOnlyList<string> Path, DataType LeafType)>
        CollectReferencedLeaves(CoreExpr expr)
    {
        switch (expr)
        {
            case ExprAttributeReference attribute:
                yield return (attribute.Name, attribute.Name, Array.Empty<string>(), attribute.Type);
                yield break;

            case ExprGetStructField field:
                {
                    var fields = new List<string>();
                    CoreExpr current = field;
                    while (current is ExprGetStructField gsf)
                    {
                        fields.Add(gsf.FieldName);
                        current = gsf.Child;
                    }

                    fields.Reverse();
                    if (current is ExprAttributeReference root && field.Type is { } leafType)
                    {
                        string pathKey = root.Name + "\u0000" + string.Join("\u0000", fields);
                        yield return (root.Name, pathKey, fields, leafType);
                    }

                    // NOTE: in the current constraint grammar a GetStructField chain ALWAYS bottoms out at a bound
                    // AttributeReference with a non-null Type (there is no struct-constructor function under nested
                    // access), so the guard above always yields. If a future grammar extension allowed a
                    // GetStructField over a cast/function base, this would silently drop the reference — revisit
                    // here (and collect any AttributeReferences under such a base) so a dependency is never missed.
                    yield break; // a resolved field access is a leaf reference — do not descend into its base again
                }

            default:
                foreach (CoreExpr child in expr.Children)
                {
                    foreach (var leaf in CollectReferencedLeaves(child))
                    {
                        yield return leaf;
                    }
                }

                break;
        }
    }

    // Navigates the field PATH under top-level column <paramref name="rootColumn"/> in <paramref name="schema"/>
    // (case-insensitively, Spark parity) and returns the leaf DataType, or null when any part is absent or a
    // non-struct is traversed (the referenced path no longer exists — a drop/rename/base-retype).
    private static DataType? ResolveLeafType(StructType schema, string rootColumn, IReadOnlyList<string> path)
    {
        StructField? field = FindFieldIgnoreCase(schema, rootColumn);
        if (field is null)
        {
            return null;
        }

        DataType current = field.DataType;
        foreach (string part in path)
        {
            if (current is not StructType structType)
            {
                return null; // a nested part is requested but the base is no longer a struct
            }

            StructField? child = FindFieldIgnoreCase(structType, part);
            if (child is null)
            {
                return null; // the nested field was dropped/renamed
            }

            current = child.DataType;
        }

        return current;
    }

    // Case-insensitive field lookup (Spark parity; StructType's own index is ordinal), returning null when the
    // new schema has no field of that name (a dropped/renamed column).
    private static StructField? FindFieldIgnoreCase(StructType schema, string name)
    {
        foreach (StructField field in schema)
        {
            if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return field;
            }
        }

        return null;
    }

    // The top-level column of a (possibly multipart) analyzer attribute reference: `amount` -> `amount`,
    // `s.f` -> `s` (a dropped struct column referenced via nested access). Delta names the top-level column
    // being altered in DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE.
    private static string TopLevelColumn(string reference)
    {
        int dot = reference.IndexOf('.');
        return dot < 0 ? reference : reference[..dot];
    }

    /// <summary>
    /// Whether the constraint-predicate result at logical <paramref name="row"/> REJECTS the row: a row is
    /// rejected when the predicate is <b>not TRUE</b> — i.e. <c>null</c> OR <c>false</c> — matching Delta's
    /// <c>CheckDeltaInvariant.assertRule</c>. The <see cref="ColumnVector.IsNull(int)"/> guard is
    /// contract-mandated and load-bearing: a <see cref="ColumnVector"/> leaves the value lane UNSPECIFIED at a
    /// null slot, so a null result must be rejected regardless of whatever the value lane happens to hold —
    /// never derive the pass/reject decision from <see cref="ColumnVector.GetValue{T}(int)"/> at a null row.
    /// </summary>
    internal static bool RowRejected(ColumnVector result, int row) =>
        result.IsNull(row) || !result.GetValue<bool>(row);

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
