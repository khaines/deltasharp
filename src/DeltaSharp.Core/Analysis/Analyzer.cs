using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Analysis;

/// <summary>
/// The first analyzer pass (Catalyst-style): it rewrites an <b>unresolved</b>
/// <see cref="LogicalPlan"/> into a <b>resolved</b> one by binding names against a
/// <see cref="ICatalog"/> and the plan's own output attributes. It applies two resolution rules
/// bottom-up over the immutable IR — never mutating a node, always returning new trees that share
/// unchanged subtrees — and does <b>no</b> physical planning, optimization, or execution.
/// </summary>
/// <remarks>
/// <para>
/// <b>ResolveRelations</b> replaces each <see cref="UnresolvedRelation"/> with a
/// <see cref="ResolvedRelation"/> carrying the catalog schema and its derived output attributes,
/// each assigned a stable <see cref="ExprId"/> (AC1). <b>ResolveReferences</b> then expands
/// <see cref="UnresolvedStar"/>s and binds each <see cref="UnresolvedAttribute"/> to the matching
/// input <see cref="AttributeReference"/> — reusing the scan's already-assigned id so a column and
/// every reference to it share one identity (AC2).
/// </para>
/// <para>
/// Any catalog miss or name-resolution failure raises a single Spark-compatible
/// <see cref="AnalysisException"/> from within the analyze pass. There is no backend to call and
/// none is called: resolution failure never triggers physical planning or execution (AC4).
/// </para>
/// <para>
/// Ids come from an <see cref="ExprIdGenerator"/> seeded fresh per <see cref="Resolve(LogicalPlan)"/> call, so
/// resolution is deterministic run-to-run (no <c>Guid</c>/<c>System.Random</c>).
/// </para>
/// </remarks>
internal sealed class Analyzer
{
    private readonly ICatalog _catalog;
    private readonly IFileRelationResolver? _fileRelationResolver;

    /// <summary>Creates an analyzer bound to <paramref name="catalog"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is null.</exception>
    public Analyzer(ICatalog catalog)
        : this(catalog, fileRelationResolver: null)
    {
    }

    /// <summary>Creates an analyzer bound to <paramref name="catalog"/> and an optional
    /// <paramref name="fileRelationResolver"/> — the read-door seam (#499) through which a <c>delta</c>
    /// path scan is bound to its schema + pinned snapshot version. When it is <see langword="null"/> (a
    /// Core-only process with no storage backend registered), a <c>delta</c> read fails closed with a clear
    /// diagnostic instead of resolving.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is null.</exception>
    public Analyzer(ICatalog catalog, IFileRelationResolver? fileRelationResolver)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _fileRelationResolver = fileRelationResolver;
    }

    /// <summary>
    /// Resolves <paramref name="plan"/>, returning an equivalent fully-resolved plan.
    /// </summary>
    /// <param name="plan">The unresolved (or partially resolved) input plan.</param>
    /// <returns>A new resolved plan tree; unchanged subtrees are shared by reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <exception cref="AnalysisException">A relation or column reference did not resolve.</exception>
    public LogicalPlan Resolve(LogicalPlan plan) => ResolveCore(plan, out _);

    /// <summary>
    /// Resolves <paramref name="plan"/> and additionally reports the analyzed plan's ordered root
    /// <paramref name="output"/> columns (name, type, nullability) the result carries. This lets an
    /// action derive its result columns — for example <see cref="DataFrame.Show(int, bool)"/> rendering
    /// column headers — from the <b>single</b> analyze pass, so no second
    /// <see cref="ExecutionStage.Analyzer"/> audit stage is emitted. The list is <b>duplicate-name
    /// tolerant</b> (unlike a <see cref="StructType"/>, which rejects duplicate field names), so a plan
    /// whose output carries duplicate column names — e.g. a self-join or <c>Select(Col("x"),
    /// Col("x"))</c> — still yields headers, matching Spark's <c>show()</c> (see
    /// <see href="https://github.com/khaines/deltasharp/issues/419">#419</see> for the deeper
    /// <c>StructType</c>/<c>Row</c> materialization policy).
    /// </summary>
    /// <param name="plan">The unresolved (or partially resolved) input plan.</param>
    /// <param name="output">On return, the analyzed plan's ordered root output columns.</param>
    /// <returns>A new resolved plan tree; unchanged subtrees are shared by reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <exception cref="AnalysisException">A relation or column reference did not resolve.</exception>
    public LogicalPlan Resolve(
        LogicalPlan plan, out IReadOnlyList<(string Name, DataType Type, bool Nullable)> output)
    {
        LogicalPlan resolved = ResolveCore(plan, out IReadOnlyList<AttributeReference> rootOutput);
        output = ToOutputColumns(rootOutput);
        return resolved;
    }

    /// <summary>The shared resolution pass. It also yields the root's output attributes so a caller
    /// that needs the result columns derives them without a second pass; <see cref="Resolve(LogicalPlan)"/>
    /// discards them.</summary>
    private LogicalPlan ResolveCore(LogicalPlan plan, out IReadOnlyList<AttributeReference> rootOutput)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Emit the #169 audit seam's Analyzer milestone. Analysis is an eager, action-driven stage:
        // a lazy transformation (Select/Filter/WithColumn/…) must never reach here. A recording sink
        // installed for a transformation-only chain therefore observes an empty stage path, so an
        // accidental eager-analyze regression reddens (see DataFrameLazyTransformationTests).
        ExecutionAudit.StageEntered(ExecutionStage.Analyzer);

        var idGenerator = new ExprIdGenerator();
        LogicalPlan withRelations = ResolveRelations(plan, idGenerator);
        var outputByPlan = new Dictionary<LogicalPlan, IReadOnlyList<AttributeReference>>(
            ReferenceEqualityComparer.Instance);
        LogicalPlan resolved = ResolveReferences(withRelations, idGenerator, outputByPlan);
        CheckAnalysis(resolved, outputByPlan);
        rootOutput = outputByPlan[resolved];
        return resolved;
    }

    /// <summary>Projects a resolved plan's output attributes into the ordered
    /// <c>(name, type, nullable)</c> columns the materialized result carries. Unlike a
    /// <see cref="StructType"/> this list <b>tolerates duplicate names</b> (e.g. a self-join or
    /// <c>Select(Col("x"), Col("x"))</c>), so <see cref="DataFrame.Show(int, bool)"/> can render a
    /// duplicate-name header the way Spark's <c>show()</c> does. The dup-rejecting
    /// <see cref="StructType"/>/<see cref="Row"/> materialization policy is tracked by
    /// <see href="https://github.com/khaines/deltasharp/issues/419">#419</see>.</summary>
    private static IReadOnlyList<(string Name, DataType Type, bool Nullable)> ToOutputColumns(
        IReadOnlyList<AttributeReference> output)
    {
        var columns = new (string Name, DataType Type, bool Nullable)[output.Count];
        for (int i = 0; i < output.Count; i++)
        {
            AttributeReference attribute = output[i];
            columns[i] = (attribute.Name, attribute.Type!, attribute.Nullable);
        }

        return columns;
    }

    /// <summary>ResolveRelations: bind every <see cref="UnresolvedRelation"/> via the catalog, mint the
    /// output of every in-memory <see cref="LocalRelation"/> (#158), resolve a <c>delta</c> file-format
    /// scan (<see cref="UnresolvedFileRelation"/>) through the read-door resolver (#499), and reject any
    /// other file-format scan whose reader is EPIC-05.</summary>
    private LogicalPlan ResolveRelations(LogicalPlan plan, ExprIdGenerator idGenerator) =>
        plan.TransformUp(node => node switch
        {
            UnresolvedRelation relation => BindRelation(relation, idGenerator),
            LocalRelation { Resolved: false } local => BindLocalRelation(local, idGenerator),
            UnresolvedFileRelation file => ResolveFileRelation(file, idGenerator),
            WriteToSource write => ValidateWriteSink(write),
            _ => node,
        });

    /// <summary>Resolves a path-based file-format scan (#499). Only <c>delta</c> reads are wired: the
    /// resolver reads the Delta log to bind the schema and pin the snapshot version (honoring the parsed
    /// <c>versionAsOf</c>/<c>timestampAsOf</c> time-travel spec), yielding a <see cref="ResolvedRelation"/>
    /// the Executor's Delta scan-source consumes. A <c>parquet</c> (or any other) scan stays deferred to
    /// EPIC-05; a <c>delta</c> read in a process with no storage backend registered (no Executor) fails
    /// closed with a clear diagnostic. Time-travel parsing (and the both-specified error) runs first, so a
    /// bad spec is rejected before any I/O. A user-supplied read <c>schema</c>
    /// (<see cref="FileRelationResolutionRequest.UserSchema"/>) is deliberately <b>ignored</b> for <c>delta</c>
    /// (Delta parity — the schema is authoritative from the snapshot, exactly as Spark ignores a read-time
    /// schema on a Delta table); the resolved schema always comes from the pinned snapshot.</summary>
    private LogicalPlan ResolveFileRelation(UnresolvedFileRelation file, ExprIdGenerator idGenerator)
    {
        if (!string.Equals(file.Format, DeltaReadRelation.DeltaFormat, StringComparison.OrdinalIgnoreCase))
        {
            throw AnalysisException.UnsupportedDataSource(file.Format, file.Path);
        }

        DeltaTimeTravelSpec spec = DeltaTimeTravel.Parse(file.Path, file.Options);

        if (_fileRelationResolver is null)
        {
            throw AnalysisException.FileSourceResolutionFailed(
                file.Format,
                spec.Path,
                "no storage backend is registered. Reference the DeltaSharp.Executor assembly and call "
                + "DeltaSharp.Executor.DeltaSharpExecutor.Enable() once at startup.");
        }

        var request = new FileRelationResolutionRequest(
            file.Format, spec.Path, spec.Version, spec.Timestamp, file.UserSchema);
        if (!_fileRelationResolver.TryResolve(request, out FileRelationResolution resolution))
        {
            // The resolver declined this format (it handles only delta today) — defer like any other
            // file-format reader whose provider is not wired.
            throw AnalysisException.UnsupportedDataSource(file.Format, spec.Path);
        }

        return BindFileRelation(spec.Path, resolution, idGenerator);
    }

    /// <summary>Mints a resolved Delta file scan: derives output attributes from the resolved schema (with
    /// stable per-pass ids, exactly like a catalog relation), renders the identifier as the <b>redacted</b>
    /// path (so a plan string cannot leak a credential-bearing path — #431), and carries the real path +
    /// pinned version in the reserved <see cref="DeltaReadRelation"/> options the scan-source reads.</summary>
    private static ResolvedRelation BindFileRelation(
        string path, FileRelationResolution resolution, ExprIdGenerator idGenerator)
    {
        StructType schema = resolution.Schema;
        var output = new AttributeReference[schema.Count];
        for (int i = 0; i < schema.Count; i++)
        {
            StructField field = schema[i];
            output[i] = new AttributeReference(
                field.Name, field.DataType, field.Nullable, idGenerator.Next());
        }

        IReadOnlyList<string> identifier = new[] { SecretRedaction.RedactPath(path) };
        IReadOnlyDictionary<string, string> options =
            DeltaReadRelation.BuildOptions(path, resolution.ResolvedVersion);
        return new ResolvedRelation(identifier, schema, output, options);
    }

    /// <summary>Rejects a <see cref="WriteToSource"/> whose sink <b>format</b> has no M1 write mapping —
    /// an EPIC-05-deferred writer (Delta/Parquet — AC4) or an unsupported format (AC3) — with a
    /// deterministic <see cref="AnalysisException"/>, mirroring how <see cref="UnresolvedFileRelation"/>
    /// defers a read. This fires during analysis, before any physical planning or output commit, so a
    /// bad format produces the diagnostic before partial output. A supported local sink passes through
    /// unchanged (its child is already resolved bottom-up; a local sink has no reader/writer to bind).</summary>
    private static WriteToSource ValidateWriteSink(WriteToSource write)
    {
        SinkDescriptor sink = write.Sink;
        switch (WriteFormats.Classify(sink.Format))
        {
            case WriteFormatKind.Local:
            case WriteFormatKind.StorageBacked:
                // A storage-backed format (e.g. delta, #487) passes analysis just like the in-memory local
                // sink: its child is already resolved bottom-up, and physical planning resolves the concrete
                // writer through the Executor's Storage↔Executor sink adapter. Partition-column existence is
                // validated fail-closed at execution by the Storage write facade (analysis-time partitionBy
                // validation is deferred to #444).
                return write;

            case WriteFormatKind.DeferredToEpic05:
                throw AnalysisException.UnsupportedDataSink(
                    sink.Format, sink.Path, WriteFormats.LocalFormatNames);

            default:
                throw AnalysisException.UnsupportedWriteFormat(
                    sink.Format, sink.Path, WriteFormats.LocalFormatNames, WriteFormats.DeferredFormatNames);
        }
    }

    /// <summary>Mints the output attributes of an unresolved <see cref="LocalRelation"/> from the
    /// shared per-pass id generator (identically to <see cref="BindRelation"/> for a catalog table), so
    /// its relation attributes are numbered in the same 0..k-1 range the physical-planning bridge
    /// reconstructs from.</summary>
    private static LocalRelation BindLocalRelation(LocalRelation relation, ExprIdGenerator idGenerator)
    {
        StructType schema = relation.Schema;
        var output = new AttributeReference[schema.Count];
        for (int i = 0; i < schema.Count; i++)
        {
            StructField field = schema[i];
            output[i] = new AttributeReference(
                field.Name, field.DataType, field.Nullable, idGenerator.Next());
        }

        return relation.WithResolvedOutput(output);
    }

    private ResolvedRelation BindRelation(UnresolvedRelation relation, ExprIdGenerator idGenerator)
    {
        if (!_catalog.TryGetRelation(relation.Identifier, out CatalogTable? table))
        {
            throw AnalysisException.TableOrViewNotFound(relation.Identifier);
        }

        StructType schema = table.Schema;
        var output = new AttributeReference[schema.Count];
        for (int i = 0; i < schema.Count; i++)
        {
            StructField field = schema[i];
            output[i] = new AttributeReference(
                field.Name, field.DataType, field.Nullable, idGenerator.Next());
        }

        return new ResolvedRelation(relation.Identifier, schema, output, relation.Options);
    }

    /// <summary>
    /// ResolveReferences: expand stars and bind attributes bottom-up. Each node is resolved against
    /// the (already-resolved) output of its children, whose attribute lists are memoized as they are
    /// produced so a parent reads its children's output in O(1).
    /// </summary>
    private LogicalPlan ResolveReferences(
        LogicalPlan plan,
        ExprIdGenerator idGenerator,
        Dictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan)
    {
        return plan.TransformUp(node =>
        {
            IReadOnlyList<AttributeReference> input = CollectInput(node, outputByPlan);

            LogicalPlan resolved = node is Project project
                ? ExpandStars(project, outputByPlan)
                : node;

            resolved = resolved.MapExpressions(
                expression => expression.TransformUp(
                    inner => inner is UnresolvedAttribute attribute
                        ? ResolveAttribute(attribute, input)
                        : inner));

            // With names bound, bind functions and apply type coercion bottom-up (STORY-04.5.2 /
            // #171): each UnresolvedFunction becomes a typed ResolvedFunction and operator/CaseWhen
            // operands are coerced to Spark-compatible common types (implicit casts inserted). This
            // runs before DeriveOutput so an aliased expression exposes a concrete output type.
            resolved = resolved.MapExpressions(
                expression => expression.TransformUp(ExpressionCoercion.Coerce));

            outputByPlan[resolved] = DeriveOutput(resolved, outputByPlan, idGenerator);
            return resolved;
        });
    }

    /// <summary>
    /// CheckAnalysis: the analyzer's post-condition. After the rule pass, the result must be
    /// <b>fully resolved</b> and <b>well-typed</b>. This walk verifies that and throws a loud
    /// <see cref="AnalysisException"/> naming the offending node/expression if:
    /// <list type="bullet">
    /// <item>an unresolved marker survived — an <see cref="UnresolvedAttribute"/>, an
    /// <see cref="UnresolvedStar"/> (for example a star outside a <see cref="Project"/> that was
    /// never expanded), an <see cref="UnresolvedFunction"/> (a function the registry could not bind),
    /// or an operator otherwise still unresolved (e.g. an undesugared using/natural
    /// <see cref="Join"/>);</item>
    /// <item>a <see cref="Union"/>'s inputs differ in <b>column count</b> (arity) — a Spark-parity
    /// structural check (deep column-type compatibility/coercion is deferred to STORY-04.5.2 / #171);</item>
    /// <item>a resolved expression carries no result type (the coercion pass left it null-typed —
    /// a symmetric guard for <see cref="BinaryArithmetic"/>/<see cref="CaseWhen"/>, #171);</item>
    /// <item>a <see cref="Filter"/>/<see cref="Join"/> condition does not resolve to
    /// <see cref="BooleanType"/> (#160);</item>
    /// <item>an aggregate function appears outside a valid aggregate context (#166).</item>
    /// </list>
    /// Operand type coercion (arithmetic/comparison/boolean/CaseWhen operands, #165/#166) and
    /// function argument coercion are applied — and their mismatches rejected — during the
    /// bind-and-coerce sub-pass (<see cref="ExpressionCoercion"/>) that runs earlier within analysis,
    /// before physical planning; this walk is the final invariant gate over its result.
    /// </summary>
    private static void CheckAnalysis(
        LogicalPlan plan,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan)
    {
        foreach (LogicalPlan child in plan.Children)
        {
            CheckAnalysis(child, outputByPlan);
        }

        foreach (Expression expression in plan.Expressions)
        {
            CheckExpression(expression, plan.NodeName);
        }

        // Children and directly-held expressions are clean; any remaining unresolution is the
        // node's own state (e.g. an undesugared using/natural join).
        if (!plan.Resolved)
        {
            // A using-column/natural Join is permanently unresolved until the desugar-to-equi-
            // condition rule lands (#405). Surface a targeted, actionable diagnostic instead of the
            // generic UnresolvedOperator("Join") so callers know the feature is deferred, not broken.
            if (plan is Join { UsingColumns: { Count: > 0 } } or Join { IsNatural: true })
            {
                throw AnalysisException.UsingOrNaturalJoinNotImplemented(((Join)plan).IsNatural);
            }

            throw AnalysisException.UnresolvedOperator(plan.NodeName);
        }

        // Structural set-operation compatibility: every Union input must expose the same number of
        // columns (arity). Deep column-type compatibility/coercion is deferred to STORY-04.5.2
        // (#171); this is the arity half only (AC3).
        if (plan is Union union)
        {
            CheckUnionArity(union, outputByPlan);
        }

        // The plan is fully resolved: enforce the type-validation post-conditions (#171 completeness).
        foreach (Expression expression in plan.Expressions)
        {
            CheckResultTypes(expression, plan.NodeName);
        }

        CheckConditionIsBoolean(plan);
        CheckAggregateContext(plan);
    }

    /// <summary>Verifies every input of a resolved <see cref="Union"/> exposes the same column
    /// count, raising a Spark-parity diagnostic naming the mismatched arities otherwise.</summary>
    private static void CheckUnionArity(
        Union union,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan)
    {
        int firstColumnCount = ChildOutput(union.Inputs[0], outputByPlan).Count;
        for (int i = 1; i < union.Inputs.Count; i++)
        {
            int columnCount = ChildOutput(union.Inputs[i], outputByPlan).Count;
            if (columnCount != firstColumnCount)
            {
                throw AnalysisException.NumberOfColumnsMismatch(
                    union.NodeName, firstColumnCount, i, columnCount);
            }
        }
    }

    /// <summary>Walks an expression subtree, throwing if any unresolved marker survives.</summary>
    private static void CheckExpression(Expression expression, string ownerNodeName)
    {
        switch (expression)
        {
            case UnresolvedAttribute attribute:
                throw AnalysisException.UnresolvedExpression(attribute.Name, ownerNodeName);

            case UnresolvedStar star:
                throw AnalysisException.UnresolvedExpression(star.SimpleString, ownerNodeName);

            case UnresolvedFunction function:
                throw AnalysisException.UnresolvedExpression(function.Name, ownerNodeName);
        }

        foreach (Expression child in expression.Children)
        {
            CheckExpression(child, ownerNodeName);
        }
    }

    /// <summary>
    /// The null-typed-resolved guard: every resolved value expression must carry a concrete result
    /// type. A resolved node with a <see langword="null"/> <see cref="Expression.Type"/> means the
    /// coercion pass could not type it (e.g. a coercion gap) — a loud failure here prevents an
    /// untyped node leaking into physical planning (#171).
    /// </summary>
    private static void CheckResultTypes(Expression expression, string ownerNodeName)
    {
        foreach (Expression child in expression.Children)
        {
            CheckResultTypes(child, ownerNodeName);
        }

        // Sort orders and stars are structural carriers, not value expressions with a result type.
        if (expression is SortOrder or UnresolvedStar)
        {
            return;
        }

        if (expression.Resolved && expression.Type is null)
        {
            throw AnalysisException.UntypedResolvedExpression(
                CoercionHelpers.PrettyReference(expression), ownerNodeName);
        }
    }

    /// <summary>Enforces that a <see cref="Filter"/> predicate and an explicit <see cref="Join"/>
    /// condition resolve to <see cref="BooleanType"/> (#160). A non-boolean predicate — for example
    /// a bare arithmetic or string column — is a data-type mismatch the API cannot catch.</summary>
    private static void CheckConditionIsBoolean(LogicalPlan plan)
    {
        switch (plan)
        {
            case Filter filter:
                RequireBooleanCondition(filter.Condition, filter.NodeName);
                break;

            case Join { Condition: { } condition } join:
                RequireBooleanCondition(condition, join.NodeName);
                break;
        }
    }

    private static void RequireBooleanCondition(Expression condition, string ownerNodeName)
    {
        if (condition.Type is not BooleanType)
        {
            string actual = condition.Type?.SimpleString ?? "unknown";
            throw AnalysisException.DataTypeMismatch(
                CoercionHelpers.PrettyReference(condition),
                $"the condition of a '{ownerNodeName}' must be boolean but is '{actual}'.");
        }
    }

    /// <summary>Enforces that aggregate functions appear only in a valid aggregate context (#166):
    /// the aggregate expressions of an <see cref="Aggregate"/>. An aggregate used in any other
    /// operator — a <see cref="Project"/>, a <see cref="Filter"/>, an <see cref="Aggregate"/>'s
    /// grouping keys — is rejected before physical planning.</summary>
    private static void CheckAggregateContext(LogicalPlan plan)
    {
        if (plan is Aggregate aggregate)
        {
            // Grouping keys must be plain expressions; aggregates belong in the aggregate list only.
            foreach (Expression grouping in aggregate.GroupingExpressions)
            {
                if (FindAggregate(grouping) is { } misplaced)
                {
                    throw AnalysisException.MisplacedAggregate(
                        misplaced.Name, "Aggregate grouping expressions");
                }
            }

            // A plain aggregate (sum(x), count(1)) is legal here, but an aggregate whose ARGUMENT
            // subtree contains another aggregate (sum(sum(x)), sum(count(x))) is not — Spark rejects
            // nesting one aggregate inside another. Validate each aggregate expression's subtree.
            foreach (Expression expression in aggregate.AggregateExpressions)
            {
                CheckNoNestedAggregate(expression);
            }

            return;
        }

        foreach (Expression expression in plan.Expressions)
        {
            if (FindAggregate(expression) is { } misplaced)
            {
                throw AnalysisException.MisplacedAggregate(misplaced.Name, plan.NodeName);
            }
        }
    }

    /// <summary>Walks an aggregate expression subtree and rejects a nested aggregate — an aggregate
    /// <see cref="ResolvedFunction"/> whose own argument subtree contains another aggregate (e.g.
    /// <c>sum(sum(x))</c>, <c>sum(count(x))</c>). A plain aggregate (<c>sum(x)</c>, <c>count(1)</c>)
    /// or an aggregate combined with scalars (<c>sum(x)+1</c>) is left untouched; only an aggregate
    /// argument that itself contains an aggregate is a nesting error (Spark parity, #166).</summary>
    private static void CheckNoNestedAggregate(Expression expression)
    {
        if (expression is ResolvedFunction { Kind: FunctionKind.Aggregate } aggregate)
        {
            foreach (Expression argument in aggregate.Arguments)
            {
                if (FindAggregate(argument) is { } nested)
                {
                    throw AnalysisException.NestedAggregate(aggregate.Name, nested.Name);
                }
            }
        }

        foreach (Expression child in expression.Children)
        {
            CheckNoNestedAggregate(child);
        }
    }

    /// <summary>Returns the first aggregate <see cref="ResolvedFunction"/> in
    /// <paramref name="expression"/>, or <see langword="null"/> if there is none.</summary>
    private static ResolvedFunction? FindAggregate(Expression expression)
    {
        if (expression is ResolvedFunction { Kind: FunctionKind.Aggregate } aggregate)
        {
            return aggregate;
        }

        foreach (Expression child in expression.Children)
        {
            if (FindAggregate(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The concatenated output attributes of <paramref name="node"/>'s resolved children
    /// (empty for a leaf), forming the name-resolution scope for this node's expressions.</summary>
    private static IReadOnlyList<AttributeReference> CollectInput(
        LogicalPlan node, IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan)
    {
        if (node.Children.Count == 0)
        {
            return Array.Empty<AttributeReference>();
        }

        if (node.Children.Count == 1)
        {
            return ChildOutput(node.Children[0], outputByPlan);
        }

        var combined = new List<AttributeReference>();
        foreach (LogicalPlan child in node.Children)
        {
            combined.AddRange(ChildOutput(child, outputByPlan));
        }

        return combined;
    }

    /// <summary>Replaces every <see cref="UnresolvedStar"/> in a projection with the child's output
    /// attributes, preserving the position of the star within the projection list.</summary>
    private static LogicalPlan ExpandStars(
        Project project,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan)
    {
        bool hasStar = false;
        foreach (Expression element in project.ProjectList)
        {
            if (element is UnresolvedStar)
            {
                hasStar = true;
                break;
            }
        }

        if (!hasStar)
        {
            return project;
        }

        IReadOnlyList<AttributeReference> childOutput =
            ChildOutput(project.Child, outputByPlan);
        var expanded = new List<Expression>(project.ProjectList.Count);
        foreach (Expression element in project.ProjectList)
        {
            if (element is UnresolvedStar)
            {
                // M1: qualifiers are not tracked, so both `*` and `t.*` expand to the full child
                // output. Qualifier-scoped expansion is deferred behind the metastore/catalog seam.
                expanded.AddRange(childOutput);
            }
            else
            {
                expanded.Add(element);
            }
        }

        return new Project(expanded, project.Child);
    }

    private static Expression ResolveAttribute(
        UnresolvedAttribute attribute, IReadOnlyList<AttributeReference> input)
    {
        IReadOnlyList<string> parts = attribute.NameParts;

        // NESTED FIELD ACCESS: bind a base column, then extract a struct field per remaining part
        // (`s.a.b` -> GetStructField(GetStructField(s, …a), …b), #580). Scan for the FIRST part that
        // resolves to a column and treat every remaining part as a struct-field access:
        //   - `s.f`       -> base `s` at index 0, extract `f`.
        //   - `t.s.f`     -> `t` is an (unmodelled) relation qualifier, base `s` at index 1, extract `f`.
        //   - `people.id` -> `people` is a qualifier, base `id` at index 1 (no remaining parts).
        // Nested access takes precedence over the M1 trailing-part fallback, so `s.f` reaches struct
        // `s`'s field `f` even when a top-level column `f` also exists, and `t.s.f` reaches `s.f`
        // instead of silently binding a top-level `f` (the pre-#580 code bound only the trailing part).
        if (parts.Count > 1)
        {
            for (int baseIndex = 0; baseIndex < parts.Count; baseIndex++)
            {
                AttributeReference? column = MatchColumn(parts[baseIndex], input, attribute);
                if (column is null)
                {
                    continue;
                }

                Expression current = column;
                for (int i = baseIndex + 1; i < parts.Count; i++)
                {
                    current = ExtractStructField(current, parts[i], attribute);
                }

                return current;
            }
        }

        // No part resolves to a column: bind a single-part reference here (the common `col` case, where
        // the scan above is skipped), or fail. A multipart reference with no column part falls through
        // to UnresolvedColumn (a column part, including the `db.table.col` degenerate, already bound and
        // returned inside the scan loop above).
        return MatchColumn(parts[^1], input, attribute)
            ?? throw AnalysisException.UnresolvedColumn(attribute.Name, input);
    }

    // Case-insensitively matches a single column name against the input attributes (Spark parity): returns
    // null when absent, the sole match when unique, and throws on an ambiguous (multiple-match) name.
    private static AttributeReference? MatchColumn(
        string columnName, IReadOnlyList<AttributeReference> input, UnresolvedAttribute origin)
    {
        AttributeReference? found = null;
        List<AttributeReference>? ambiguous = null;
        foreach (AttributeReference candidate in input)
        {
            if (!string.Equals(candidate.Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is null)
            {
                found = candidate;
            }
            else
            {
                ambiguous ??= new List<AttributeReference> { found };
                ambiguous.Add(candidate);
            }
        }

        if (ambiguous is not null)
        {
            throw AnalysisException.AmbiguousReference(origin.Name, ambiguous);
        }

        return found;
    }

    // Builds a GetStructField extracting <paramref name="fieldName"/> from a struct-typed
    // <paramref name="child"/> (case-insensitive field match, Spark parity). A base that is not a
    // struct, or a struct with no such field, is a STRUCTURAL absence (the field was dropped/renamed
    // or the base retyped away from a struct) → UnresolvedStructField, so a survivor-constraint
    // reclassifier can attribute it to the top-level column (#600). An ambiguous field match remains
    // a DataTypeMismatch (the reference resolves to a struct, but the path is under-specified).
    private static Expression ExtractStructField(
        Expression child, string fieldName, UnresolvedAttribute origin)
    {
        if (child.Type is not StructType structType)
        {
            throw AnalysisException.UnresolvedStructField(
                origin.Name,
                $"cannot extract field '{fieldName}' from '{child.SimpleString}' of type "
                + $"'{child.Type?.SimpleString ?? "unknown"}' — a nested field reference requires a struct");
        }

        int ordinal = -1;
        string? actualName = null;
        for (int i = 0; i < structType.Count; i++)
        {
            if (!string.Equals(structType[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ordinal >= 0)
            {
                throw AnalysisException.DataTypeMismatch(
                    origin.Name,
                    $"field '{fieldName}' is ambiguous in struct type '{structType.SimpleString}'");
            }

            ordinal = i;
            actualName = structType[i].Name;
        }

        if (ordinal < 0)
        {
            throw AnalysisException.UnresolvedStructField(
                origin.Name,
                $"no such struct field '{fieldName}' in '{structType.SimpleString}'");
        }

        return new GetStructField(child, ordinal, actualName!);
    }

    /// <summary>Derives the output attribute list of a resolved node from its shape and its
    /// children's memoized outputs.</summary>
    private static IReadOnlyList<AttributeReference> DeriveOutput(
        LogicalPlan node,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan,
        ExprIdGenerator idGenerator)
    {
        switch (node)
        {
            case ResolvedRelation relation:
                return relation.Output;

            case LocalRelation { Output: { } output }:
                return output;

            case Project project:
                return ProjectionOutput(project.ProjectList, idGenerator);

            case Aggregate aggregate:
                return ProjectionOutput(aggregate.AggregateExpressions, idGenerator);

            case Join join:
                return JoinOutput(join, node, outputByPlan);

            // Shape-preserving unary operators expose their single child's output unchanged.
            case Filter:
            case Sort:
            case Limit:
            case Distinct:
            case WriteToSource:
                return ChildOutput(node.Children[0], outputByPlan);

            case Union:
                // TODO(#392): set-op output semantics (fresh ids + nullability widening). Union
                // currently reuses its first input's attributes; Spark mints fresh output
                // attributes and widens nullability across inputs.
                return ChildOutput(node.Children[0], outputByPlan);

            default:
                // A node whose output shape is not explicitly modelled (e.g. a future set-op such as
                // Intersect/Except or a Generate) must not silently pass a child's output through —
                // that would derive a wrong output and corrupt downstream name resolution.
                throw new NotSupportedException(
                    $"Output derivation is not defined for operator '{node.NodeName}'. Add an "
                    + "explicit DeriveOutput case before introducing this operator to the analyzer.");
        }
    }

    /// <summary>The output of a resolved <see cref="Join"/>: left ⧺ right for value-adding joins,
    /// but <b>left-only</b> for <see cref="JoinType.LeftSemi"/>/<see cref="JoinType.LeftAnti"/>,
    /// which filter the left side and never widen it with right-side columns (Spark parity).</summary>
    private static IReadOnlyList<AttributeReference> JoinOutput(
        Join join,
        LogicalPlan node,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan) =>
        join.JoinType is JoinType.LeftSemi or JoinType.LeftAnti
            ? ChildOutput(node.Children[0], outputByPlan)
            : CollectInput(node, outputByPlan);

    private static IReadOnlyList<AttributeReference> ProjectionOutput(
        IReadOnlyList<Expression> projectList, ExprIdGenerator idGenerator)
    {
        var output = new AttributeReference[projectList.Count];
        for (int i = 0; i < projectList.Count; i++)
        {
            output[i] = ToAttribute(projectList[i], idGenerator);
        }

        return output;
    }

    /// <summary>Maps a resolved projection element to the attribute it exposes: an attribute is
    /// itself; an alias becomes a fresh attribute named for the alias.</summary>
    private static AttributeReference ToAttribute(Expression element, ExprIdGenerator idGenerator)
    {
        switch (element)
        {
            case AttributeReference attribute:
                return attribute;

            case Alias alias:
                // Type coercion (STORY-04.5.2 / #171) now runs before output derivation, so a
                // resolved alias exposes a concrete type; a residual null here is a coercion gap the
                // untyped-resolved guard reports symmetrically with CheckAnalysis.
                DataType type = alias.Type
                    ?? throw AnalysisException.UntypedResolvedExpression(
                        CoercionHelpers.PrettyReference(alias.Child), "Project");
                return new AttributeReference(alias.Name, type, alias.Nullable, idGenerator.Next());

            case ResolvedFunction function:
                // A bare aggregate/scalar function in output position is auto-named exactly like
                // Spark: `groupBy("dept").agg(sum("salary"))` exposes a `sum(salary)` column (the
                // function's pretty SQL string, using unqualified child names WITHOUT the `#id`
                // suffix). Function binding + coercion (STORY-04.5.2 / #171) ran earlier in
                // ResolveReferences, so the call is already typed here; a residual null type is a
                // coercion gap reported symmetrically with the Alias case.
                DataType functionType = function.Type
                    ?? throw AnalysisException.UntypedResolvedExpression(
                        CoercionHelpers.PrettyReference(function), "Aggregate");
                return new AttributeReference(
                    SparkAutoName(function), functionType, function.Nullable, idGenerator.Next());

            case GetStructField field:
                // A bare nested reference in output position is auto-named after the extracted field,
                // exactly like Spark: `select(col("s.f"))` exposes a column named `f` (#580). The field
                // type is resolved at name resolution; a residual null type is a resolution gap reported
                // symmetrically with the Alias/function cases.
                DataType fieldType = field.Type
                    ?? throw AnalysisException.UntypedResolvedExpression(
                        CoercionHelpers.PrettyReference(field), "Project");
                return new AttributeReference(field.FieldName, fieldType, field.Nullable, idGenerator.Next());

            case UnresolvedFunction function:
                // Defensive invariant: function binding (ExpressionCoercion → FunctionRegistry.Bind)
                // runs over every expression in ResolveReferences BEFORE output derivation, and it is
                // total — it either produces a typed ResolvedFunction (handled above) or throws
                // (UnknownFunction for an unregistered name, InvalidFunctionArgument for a bad call).
                // An UnresolvedFunction therefore cannot normally reach here; if one does, the bind
                // pass was skipped for this subtree. Report it as an undefined/unbound function rather
                // than the obsolete "deferred to #171" self-reference (this story IS #171).
                throw AnalysisException.UnknownFunction(
                    function.Name,
                    function.Arguments.Select(a => a.Type ?? NullType.Instance).ToArray());

            default:
                throw AnalysisException.UnsupportedProjection(
                    $"Projection element '{CoercionHelpers.PrettyReference(element)}' is not a named output element "
                    + "(expected an attribute or an alias).");
        }
    }

    /// <summary>The Spark auto-name for a bare function call in output position: the pretty SQL
    /// string <c>name(arg, …)</c> built from unqualified argument names (no <c>#id</c> ExprId
    /// suffix), e.g. <c>sum(salary)</c>, <c>count(1)</c>, <c>avg(x)</c>, <c>count(DISTINCT v)</c>.
    /// It delegates to the shared <see cref="CoercionHelpers.PrettyReference"/> renderer, which
    /// unwraps implicit coercion <see cref="Cast"/>s and uppercases <c>DISTINCT</c>, mirroring Spark's
    /// <c>usePrettyExpression</c>. The same renderer names the offending reference in a
    /// <see cref="AnalysisException.DataTypeMismatch"/> diagnostic, so auto-names and diagnostics never
    /// diverge and neither leaks an ExprId.</summary>
    private static string SparkAutoName(ResolvedFunction function) =>
        CoercionHelpers.PrettyReference(function);

    private static IReadOnlyList<AttributeReference> ChildOutput(
        LogicalPlan child,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan) =>
        outputByPlan.TryGetValue(child, out IReadOnlyList<AttributeReference>? output)
            ? output
            : throw new InvalidOperationException(
                $"Output for child node '{child.NodeName}' was not resolved before its parent; "
                + "the bottom-up resolution order is violated.");
}
