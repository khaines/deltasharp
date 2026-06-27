# 15 — Spark API Parity Checklist

> **Scope:** Public `SparkSession`, `DataFrame`, `Dataset<T>`, `Column`, SQL, functions, reader/writer, configuration, and migration-facing behavior.
> **Priority:** HIGH.
> **Owners:** developer-experience-api-engineer, sql-language-frontend-engineer, query-execution-engine-engineer. **Grounded in:** `.github/copilot-instructions.md`, ADR-0007, ADR-0008, [16](16-catalyst-planning-checklist.md), [20](20-developer-experience-api-checklist.md).

## How to use
Use this checklist for any user-visible API or semantic change. Spark compatibility is a protected domain in the review rubric: broken Spark semantics or lazy/eager behavior can be Critical.

## Checklist
### Public API shape
- [ ] Public methods use Spark-recognizable names for core operations: `select`, `filter`, `where`, `groupBy`, `agg`, `join`, `withColumn`, `drop`, `orderBy`, `sort`, `limit`, `collect`, `count`, `show`, and `write`.
- [ ] Overloads match Spark argument shapes where practical, including column names, `Column` expressions, expression strings where supported, join conditions, save modes, and options.
- [ ] Return types preserve Spark chaining expectations: transformations return plan-building `DataFrame`/`Dataset<T>` surfaces and actions return materialized values or action results.
- [ ] API names that intentionally deviate for .NET safety or clarity are documented in a parity matrix with rationale and migration guidance.
- [ ] Common PySpark and Scala Spark snippets have obvious C# translations without inventing a new mental model.
- [ ] Case sensitivity, column qualification, aliases, nested-field access, and duplicate-column behavior are specified against Spark behavior.
- [ ] Configuration knobs use Spark-compatible naming or documented aliases when user migration value is high.
- [ ] Public APIs never expose internal driver/executor, protobuf, `ColumnBatch`, or shuffle details unless the surface is explicitly diagnostic.

### Lazy/eager semantics
- [ ] Transformations build unresolved logical plans only and do not read files, query table data, schedule tasks, write output, or materialize rows.
- [ ] Actions are the only API calls that trigger analysis-to-execution, including `collect`, `count`, `show`, writes, saves, inserts, and explain variants that require planning.
- [ ] Samples and XML docs label whether each core method is lazy or eager.
- [ ] Exceptions from transformations are limited to local argument validation and plan-construction errors; catalog/data errors occur at analysis/action time as Spark users expect.
- [ ] Cached/persisted DataFrame behavior distinguishes marking a plan for caching from materializing cache contents.
- [ ] `EXPLAIN` behavior is documented: which plan phases it computes and whether it executes data.
- [ ] Async action variants preserve eager semantics while providing cancellation and non-blocking I/O.
- [ ] Lazy/eager violations are treated as Critical review findings.

### Nulls, types, and ANSI behavior
- [ ] SQL and DataFrame expressions implement SQL three-valued logic for `NULL`, comparisons, boolean operators, joins, filters, aggregates, and `CASE`.
- [ ] Null-safe equality, null ordering, aggregate null handling, and outer-join null preservation match Spark/ANSI expectations.
- [ ] Type coercion rules are centralized and shared by SQL, DataFrame, Dataset, and function APIs.
- [ ] ANSI mode covers casts, arithmetic overflow, division by zero, invalid dates/timestamps, decimal precision/scale, and assignment policy per ADR-0008.
- [ ] Nullable reference annotations in C# reflect API contracts without changing Spark null semantics.
- [ ] Complex types (`array`, `map`, `struct`) preserve Spark-compatible field access, nullability, equality, ordering where supported, and serialization behavior.
- [ ] Error messages identify the expression, input types, ANSI/Spark rule, and corrective action for type or nullability failures.
- [ ] Differential tests or goldens cover null/type edge cases against Spark where lawful and practical.

### Expression and function semantics
- [ ] Built-in functions use Spark names, aliases, arity, overloads, determinism metadata, null propagation, and error behavior unless deviations are recorded.
- [ ] Column operators and expression builders lower to the same expression IR as SQL functions.
- [ ] Aggregates, windows, predicates, casts, literals, date/time functions, string functions, collection functions, and math functions have parity status and tests.
- [ ] Function registry behavior is shared across SQL and DataFrame APIs and supports catalog/temp functions where applicable.
- [ ] Unsupported functions fail with precise unsupported-feature errors rather than silently returning approximate semantics.
- [ ] Expression IDs, aliases, star expansion, generated column names, and metadata columns are compatible with Spark expectations where exposed.
- [ ] Function documentation states supported Spark version target, ANSI caveats, and DeltaSharp deviations.
- [ ] Optimizer rewrites preserve expression semantics and are checked with [16](16-catalyst-planning-checklist.md).

### SQL and DataFrame convergence
- [ ] Equivalent SQL and DataFrame/Dataset expressions produce equivalent unresolved or analyzed logical plans after frontend lowering.
- [ ] Parser output, API expression builders, and function registry entries share the same type coercion and resolution rules.
- [ ] SQL CTEs, aliases, subqueries, joins, aggregates, windows, limits, and ordering map to shared plan nodes where supported.
- [ ] DataFrame reader/writer table references and SQL table references resolve through the same catalog contracts.
- [ ] `EXPLAIN` can show comparable logical, analyzed, optimized, physical, and adaptive plans for SQL and DataFrame entry points.
- [ ] Tests include paired SQL/DataFrame cases for core relational operations and edge cases.
- [ ] Any temporary divergence between SQL and DataFrame support is listed in the parity matrix with owner and planned convergence.
- [ ] SQL frontend work follows ADR-0007 and the `sql-language-frontend-engineer` handoff boundary.

### Parity matrix, migration, and explainability
- [ ] A parity matrix lists Spark methods/functions/syntax, supported overloads, support level, semantic caveats, tests, owner, and target milestone.
- [ ] Deliberate .NET deviations are documented with Spark equivalent, DeltaSharp shape, migration impact, and why the deviation improves safety or ergonomics.
- [ ] Source-compatibility guidance exists for common PySpark and Scala Spark migration paths, including lower-case Spark method names in C# where preserved.
- [ ] `EXPLAIN` output supports parity review by showing logical/analyzed/optimized/physical/adaptive phases and key choices.
- [ ] Error classes and diagnostics are stable enough for migration docs, tests, IDEs, and support triage.
- [ ] Unsupported features include actionable alternatives or links to tracking issues where appropriate.
- [ ] Public examples demonstrate Spark-like chaining, SQL use, Delta reads/writes, and Kubernetes execution without hiding semantic caveats.
- [ ] Cross-link [20](20-developer-experience-api-checklist.md) for API ergonomics and [16](16-catalyst-planning-checklist.md) for planning correctness.

## Anti-patterns (red flags)
- Renaming core Spark methods to idiomatic .NET names without providing Spark-compatible aliases or documented migration rationale.
- Transformations that perform I/O, execute plans, or throw data-dependent errors before an action.
- SQL and DataFrame APIs implementing separate type coercion, function, or null semantics.
- Approximate function behavior shipped under a Spark-compatible name without caveats or tests.
- Missing parity matrix entries for new public methods, functions, SQL syntax, or deliberate deviations.
- Analyzer/optimizer rewrites that change null, outer-join, aggregation, or ANSI overflow semantics.
- Samples that look source-compatible but omit unsupported behavior or hidden execution.
- Breaking public API changes without deprecation, migration path, or [20](20-developer-experience-api-checklist.md) review.

## References
- [DeltaSharp Copilot Instructions](../../../.github/copilot-instructions.md)
- [ADR-0007: SQL frontend — parser and dialect](../../adr/0007-sql-frontend.md)
- [ADR-0008: Type system and internal row/value representation](../../adr/0008-type-system-row-format.md)
- [16 — Catalyst Planning Checklist](16-catalyst-planning-checklist.md)
- [20 — Developer Experience & API Checklist](20-developer-experience-api-checklist.md)
- [Developer Experience & API Engineer Agent](../../persona/agents/developer-experience-api-engineer-agent.md)
- [SQL Language & Frontend Engineer Agent](../../persona/agents/sql-language-frontend-engineer-agent.md)
- [Query & Execution Engine Engineer Agent](../../persona/agents/query-execution-engine-engineer-agent.md)
- [Review PR rating rubric](../../../.github/skills/review-pr/rating-rubric.md)
