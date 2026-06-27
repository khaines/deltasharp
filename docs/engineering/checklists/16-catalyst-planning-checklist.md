# 16 — Catalyst Planning Checklist

> **Scope:** SQL/DataFrame lowering, unresolved/analyzed/optimized/physical/adaptive plans, analyzer rules, optimizer rules, physical strategies, CBO/AQE hooks, and `EXPLAIN` output.
> **Priority:** HIGH.
> **Owners:** query-execution-engine-engineer, query-optimizer-scheduler-engineer, sql-language-frontend-engineer. **Grounded in:** ADR-0006, ADR-0007, ADR-0008, ADR-0012, [15](15-spark-api-parity-checklist.md), [02](02-engine-implementation-checklist.md).

## How to use
Use this checklist whenever a change affects plan construction, resolution, optimization, physical planning, adaptive planning, or plan diagnostics. Planning correctness is a protected review domain: semantic-changing rewrites can be Critical.

## Checklist
### Plan-phase correctness
- [ ] SQL and DataFrame/Dataset inputs first produce unresolved logical plans without execution or optimization.
- [ ] Analyzer output is a resolved, typed, catalog-bound logical plan with stable attribute IDs and no unresolved relations, attributes, functions, stars, or aliases.
- [ ] Optimizer output is semantically equivalent to the analyzed plan under Spark/ANSI rules, including nulls, ordering guarantees, and outer-join behavior.
- [ ] Physical plans are executable strategies for the optimized logical plan and state partitioning, ordering, distribution, resource, and backend requirements.
- [ ] Adaptive plans are explicit versions or replacements at valid stage boundaries, not hidden mutation of already-running plans.
- [ ] Plans are immutable across phases; annotations, statistics, and costs are explicit metadata or new plan instances.
- [ ] Plan IDs and expression IDs are deterministic enough for golden tests, plan caching, metrics, and `EXPLAIN` correlation.
- [ ] Cross-link [02](02-engine-implementation-checklist.md) for immutable tree and serialization contracts.

### Analyzer resolution
- [ ] Relation, namespace, table, view, and function resolution goes through catalog/function-registry interfaces, not ad hoc parser or storage lookups.
- [ ] Attribute resolution defines lookup order, case-sensitivity, qualifiers, aliases, CTE shadowing, star expansion, nested fields, and ambiguity handling.
- [ ] Type checking and coercion use the shared Spark/ANSI type system from ADR-0008.
- [ ] Function binding records overload, return type, nullability, determinism, aggregate/window classification, and unsupported-feature status.
- [ ] Analyzer rules are ordered, repeatable, and terminate deterministically; fixed-point batches have explicit convergence limits.
- [ ] Analyzer errors include source spans where available, candidate names, relevant catalog object, expected/actual types, and ANSI/Spark context.
- [ ] Catalog snapshot/table version is captured so analysis is reproducible for execution, caching, and time travel.
- [ ] SQL frontend lowering follows ADR-0007 and hands off only after parse/lower/resolution invariants are met.

### Optimizer rule discipline
- [ ] Each optimizer rule documents preconditions, rewrite shape, semantic-equivalence argument, and affected plan nodes.
- [ ] Rules have positive, negative, and idempotency tests; semantic tests compare results before/after when executable.
- [ ] Predicate pushdown preserves null semantics, outer-join semantics, deterministic-expression constraints, and connector/storage capabilities.
- [ ] Projection pushdown and column pruning preserve required attributes for joins, filters, aggregates, windows, ordering, writes, metadata columns, and diagnostics.
- [ ] Constant folding, simplification, null propagation, and boolean rewrites respect ANSI errors, non-deterministic functions, overflow, and three-valued logic.
- [ ] Join reorder and join simplification preserve join type, null-preserving sides, correlation, hints, ordering/distribution constraints, and user-visible semantics.
- [ ] Partition pruning and Delta/Parquet data skipping are proven to reduce files/partitions/row groups read and never skip possible matches.
- [ ] Limit, aggregate, sort, window, and subquery rewrites include edge-case tests for empty inputs, nulls, duplicate columns, and nested fields.

### Rule-based core vs cost-based choices
- [ ] Rule-based planning establishes valid, semantically equivalent alternatives before CBO chooses among them.
- [ ] CBO inputs identify row counts, NDV, null counts, min/max, histograms, file sizes, partition stats, runtime shuffle stats, provenance, and confidence.
- [ ] Missing, stale, sampled, or low-confidence statistics trigger robust defaults and observable warnings rather than catastrophic plan flips.
- [ ] Cost-sensitive choices such as join order, broadcast threshold, exchange reuse, partition count, and join strategy are owned with `query-optimizer-scheduler-engineer` per ADR-0006.
- [ ] AQE occurs only at valid stage boundaries and uses live shuffle statistics without changing query results.
- [ ] Adaptive replacements preserve ordering/distribution contracts or explicitly re-establish them with exchanges/sorts.
- [ ] Hints and configuration have documented precedence over rule-based, CBO, and AQE decisions without bypassing semantic safety or tenant policy.
- [ ] CBO/AQE decisions are visible in `EXPLAIN` with estimated vs observed stats where available.

### Physical strategy selection
- [ ] Physical planning chooses strategies from declared logical requirements, data size, partitioning, ordering, statistics, memory budgets, and backend capabilities.
- [ ] Exchange insertion is deliberate at shuffle boundaries and states required partitioning/distribution and stage-splitting consequences.
- [ ] Join planning considers broadcast hash, shuffle hash, sort-merge, semi/anti, outer, null-aware, skew, spill, and tenant memory limits.
- [ ] Aggregation planning distinguishes partial/final aggregation, grouping key representation, null grouping, ordering, spill, and shuffle requirements.
- [ ] Scan planning respects projection, predicate, partition pruning, data skipping, file statistics, table version, and storage capability contracts.
- [ ] Sort, limit, window, and repartition/coalesce strategies state ordering and partitioning guarantees explicitly.
- [ ] Physical plans can be serialized to the ADR-0012 protobuf task boundary without process-local state.
- [ ] Backend-specific choices still produce identical results across interpreter and optional codegen tiers.

### EXPLAIN, diagnostics, and tests
- [ ] `EXPLAIN` can show unresolved, analyzed, optimized, physical, and adaptive plans where meaningful.
- [ ] `EXPLAIN` includes pushed predicates, pruned columns, partition pruning, data skipping, exchanges, broadcasts, join strategies, codegen regions, costs, and adaptive replacements.
- [ ] Plan output is stable enough for golden tests while still readable by Spark users.
- [ ] Rule traces can identify which rule changed a plan, why it applied, and what semantic guard allowed it.
- [ ] Planning tests include SQL/DataFrame paired cases and cross-link [15](15-spark-api-parity-checklist.md) for Spark semantic parity.
- [ ] Fault and edge tests cover ambiguous names, unsupported functions, stale stats, missing stats, skew, nulls, ANSI errors, and catalog failures.
- [ ] Metrics record planning time, analyzer time, optimizer iterations, rule applications, CBO estimates, AQE replacements, and physical-strategy choices.
- [ ] Tenant-safe diagnostics avoid leaking unauthorized schema, paths, statistics, values, or catalog names.

## Anti-patterns (red flags)
- Optimizer rules with no semantic-equivalence argument or tests for negative cases.
- Mutating plan nodes in place across analyzer, optimizer, physical, or adaptive phases.
- Predicate pushdown below an outer join, aggregate, window, or non-deterministic expression without a proven null/semantic guard.
- CBO rules that treat stale or missing statistics as exact facts.
- Adaptive replanning that changes result ordering guarantees, null semantics, or side effects.
- SQL and DataFrame plans diverging for equivalent operations.
- Physical plans that cannot be serialized to executors without closures or local object references.
- `EXPLAIN` output that hides exchanges, broadcasts, pushed predicates, adaptive changes, or unsupported-feature reasons.

## References
- [02 — Engine Implementation Checklist](02-engine-implementation-checklist.md)
- [15 — Spark API Parity Checklist](15-spark-api-parity-checklist.md)
- [20 — Developer Experience & API Checklist](20-developer-experience-api-checklist.md)
- [ADR-0006: Scheduler, Adaptive Query Execution, and cost-based optimization](../../adr/0006-scheduler-aqe-cbo.md)
- [ADR-0007: SQL frontend — parser and dialect](../../adr/0007-sql-frontend.md)
- [ADR-0008: Type system and internal row/value representation](../../adr/0008-type-system-row-format.md)
- [ADR-0012: Plan serialization](../../adr/0012-plan-serialization.md)
- [Query & Execution Engine Engineer Agent](../../persona/agents/query-execution-engine-engineer-agent.md)
- [Query Optimizer & Scheduler Engineer Agent](../../persona/agents/query-optimizer-scheduler-engineer-agent.md)
- [SQL Language & Frontend Engineer Agent](../../persona/agents/sql-language-frontend-engineer-agent.md)
