# Query & Execution Engine Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

The Query & Execution Engine Engineer owns the center of DeltaSharp: the path from Spark-compatible SQL and DataFrame/Dataset APIs to distributed execution over native Delta tables. The role translates user intent into unresolved logical plans, resolves names and types against catalogs, optimizes the plan with Catalyst-style rules and cost choices, selects physical operators, and runs jobs as stages and tasks across executor pods. It must hold two promises at once: Spark users should recognize the semantics, and the .NET-native implementation should exploit Delta, Parquet, Kubernetes, and modern runtime primitives without exposing those details as accidental API behavior.[^1][^2]

DeltaSharp's most important invariant is that transformations are lazy and actions are eager. A call to `select`, `filter`, `groupBy`, `join`, `withColumn`, or `repartition` extends an immutable plan. A call to `collect`, `count`, `show`, `foreach`, or `write` triggers analysis, optimization, physical planning, and execution. If the API layer performs storage reads or schedules executor work while building a transformation, the architecture has been violated. This role is the guardian of that separation.[^1]

The field draws on a deep lineage of database and data-processing research: Volcano-style extensible execution, Cascades-style rule transformations, vectorized columnar processing, cost-based join ordering, adaptive query execution, and distributed shuffle systems.[^3][^4] DeltaSharp applies those ideas to native Delta tables: Parquet column pruning, Delta partition pruning, file-level statistics, data skipping, snapshot isolation, time travel, schema evolution, and execution over object stores or PersistentVolumes. The engineer must design for correctness under distributed failure and for cost safety under multi-tenant workloads.

In a greenfield project, early choices compound. Plan node shape, analyzer contracts, expression typing, exchange semantics, shuffle metadata, and cache-key scoping become long-lived interfaces. Poor choices create user-visible incompatibilities, missed pushdown, unnecessary shuffles, unsafe tenant sharing, and brittle execution kernels. Great choices create a system where new operators, file capabilities, adaptive planning, and code-generated execution can be added without rewriting the whole engine.

## Evidence base

- Apache Spark SQL, DataFrame, Dataset, Catalyst optimizer, Tungsten execution, and adaptive query execution references.[^1][^5]
- Delta Lake protocol and transaction log documentation, including snapshot isolation, schema evolution, time travel, and file statistics.[^2]
- Apache Parquet format documentation for columnar storage, row groups, column chunks, predicate pushdown, and statistics.[^6]
- Volcano and Cascades optimizer papers for extensible logical and physical planning.[^3]
- Research and production practice around vectorized execution, columnar batches, and whole-stage code generation.[^4][^5]
- Kubernetes execution-model references for driver/executor pods, retryable workloads, persistent volumes, and controller-managed lifecycle.[^7]
- Cloud object store design constraints: high-latency listing, eventual metadata concerns, request-cost awareness, multipart reads, and data locality absence.[^8]
- .NET runtime references for expression trees, dynamic methods, memory pooling, `Span<T>`, async I/O, and diagnostics through EventPipe counters.[^9]
- TPC-DS-style analytical query suites and Spark SQL compatibility tests as representative workloads.[^10]
- Literature and systems practice for distributed shuffle, skew handling, speculative execution, and fault-tolerant task retry.[^11]

## Explanation

### Why this role exists

DeltaSharp deliberately separates user-facing API design, storage-format internals, connector integration, and distributed platform topology. The query and execution engine is its own seat because it is where all those domains meet without being reducible to any one of them. SQL and DataFrame/Dataset calls need Spark-compatible semantics. Delta snapshots and Parquet statistics need to influence plans. The Kubernetes runtime needs jobs, stages, tasks, and shuffle boundaries. The .NET runtime needs memory-safe, efficient columnar execution. Multi-tenant operators need predictable limits.

A storage expert can expose snapshots and file statistics, but that does not decide whether a join should broadcast, shuffle, or sort-merge. An API expert can mirror Spark method names, but that does not decide how aliases resolve after nested projections. A platform architect can define driver and executor pods, but that does not decide where exchanges enter a plan. The Query & Execution Engine Engineer owns those decisions.

### Boundary with sibling personas

The `delta-storage-format-engineer` owns Delta log protocol, Parquet layout, write durability, compaction, clustering, checkpoints, and schema evolution mechanics. The query engineer consumes the snapshot, schema, file list, and statistics they provide.

The `data-platform-connectors-engineer` owns source and sink integration, connector capability discovery, streaming inputs, external catalog adapters, and pushdown APIs for non-Delta sources. The query engineer consumes those capabilities during planning.

The `developer-experience-api-engineer` owns public method shape, samples, migration paths, and Spark API ergonomics. The query engineer owns the semantics those methods lower into and the plans those methods build.

The `cloud-native-distributed-systems-architect` owns high-level topology, CRDs, service contracts, and operator responsibilities. The query engineer owns how a query becomes jobs, stages, tasks, exchanges, and executor-side operators within that topology.

The `cloud-native-site-reliability-engineer`, `performance-benchmarking-engineer`, `compute-storage-finops-engineer`, `cloud-native-security-sme`, and `reliability-test-chaos-engineer` depend on query-engine metrics and limits. The query engineer designs the enforcement and measurement points they need.

### Council consensus and criticality

The engine is a correctness boundary, a cost boundary, and a reliability boundary. A wrong analyzer can bind a column to the wrong relation. A bad optimizer can remove a filter or change null semantics. A poor physical planner can turn a broadcast join into a cluster-wide shuffle. Missing data skipping can multiply object-store reads. A cache key without tenant scope can leak results. A cancellation path that stops at the driver can strand executor tasks. Because queries are user-triggered, these mistakes surface immediately under load.

## Required knowledge domains

### 1. Spark-compatible query semantics: SQL, DataFrame, and Dataset

DeltaSharp should mirror Spark's public concepts: `SparkSession`, `DataFrame`, `Dataset<T>`, `Column`, SQL text, functions, temporary views, catalogs, readers, and writers. The query engineer must understand how Spark treats unresolved attributes, aliases, nested fields, stars, joins, grouping, windows, nulls, ordering, ANSI behavior, type coercion, case sensitivity, and function overload resolution.[^1]

The API's method names are not enough; compatibility lives in the lowering rules. `df.Select("a")`, `df.Select(Col("a"))`, `spark.Sql("select a from t")`, and typed Dataset projections may reach the planner through different API paths but must converge on equivalent logical meaning. Deviations from Spark semantics should be rare, explicit, and documented.

### 2. Parsing and unresolved logical plans

SQL requires parsing into an abstract syntax tree and then lowering to an unresolved logical plan. DataFrame/Dataset operations can build unresolved plan nodes directly. Unresolved plans intentionally contain names, stars, aliases, functions, relation references, and expressions that have not yet been bound. This is a feature: it lets the analyzer apply catalog and session rules consistently.

Important unresolved node families include relation references, projections, filters, aggregates, joins, sorts, windows, limits, subqueries, write commands, view expansion, and set operations. Expression nodes need source positions and stable debug output so errors can point to the right SQL token or API-created expression.

For Dataset-style typed operations, the engineer must define how expression trees or generated accessors map to logical expressions without prematurely executing user code. Typed encoders, if present, must be plan-building contracts first and serialization mechanisms second.

### 3. Analyzer design: catalog, names, types, and functions

The analyzer converts unresolved plans into analyzed logical plans. It resolves table identifiers against session catalogs, temporary views, Delta paths, and external connectors. It binds attributes to relation outputs. It expands stars. It resolves functions and operators. It applies type coercion. It validates aggregate grouping, window frames, join conditions, write schemas, and subquery scoping.

Name resolution is subtle. Ambiguous columns after joins should produce precise errors. Case-sensitivity should follow session configuration. Nested fields should preserve struct semantics. Aliases should shadow only where Spark would shadow. View expansion must not accidentally capture unrelated session state.

Delta-specific analyzer work includes reading snapshot schemas, enforcing time-travel references, handling schema evolution, checking generated or partition columns when they exist, and validating writes against table constraints surfaced by the storage layer.

### 4. Optimizer architecture: rules, batches, and equivalence

A Catalyst-style optimizer applies rules in ordered batches. Some rules are always safe: constant folding, boolean simplification, null propagation, redundant projection elimination, predicate combination, filter pushdown through projections, and limit simplification. Other rules require care: join reordering, aggregate pushdown, subquery rewrites, and adaptive exchange changes.

Every optimizer rule should specify:

- the pattern it matches;
- the rewrite it performs;
- the semantic preconditions;
- the statistics or capabilities it requires;
- the cases where it must not fire;
- tests that prove equivalence.

The optimizer should maintain a rule trace for `EXPLAIN` and debugging. Users and engineers need to see not just the final plan but why filters moved, why an exchange appeared, why a join strategy changed, and why a predicate could not push down.

### 5. Delta and Parquet-aware optimization

DeltaSharp's optimizer must exploit table metadata without owning it. Partition pruning removes directories or logical partitions before file reads. Data skipping uses file-level min/max/null counts and other statistics from Delta metadata. Parquet predicate pushdown skips row groups. Projection pushdown reads only required columns. Column pruning removes unused attributes from the plan before physical planning.

Time travel changes the input snapshot and therefore the file set, statistics, schema, and constraints. Schema evolution means plans must bind against the selected snapshot, not the latest table shape unless the query explicitly uses it. Pushdown must respect type conversions and null semantics so skipped data cannot change results.

Object stores and PVCs differ operationally. Object stores make listing and small reads expensive; PVCs may have locality and different failure modes. The optimizer should use abstract storage capabilities: listing cost, range-read support, statistics availability, and consistency guarantees. It should not bake one backend into plan semantics.

### 6. Physical planning and strategy selection

Physical planning maps optimized logical operators to executable operators. For scans, the planner chooses row-group pruning, columnar reads, partitioning, and batch shape. For filters and projections, it chooses vectorized kernels and codegen regions. For aggregations, it chooses local partial aggregation plus global merge where legal. For joins, it chooses among broadcast hash join, shuffle hash join, sort-merge join, nested-loop fallbacks, and possibly adaptive re-planning.

Join selection depends on estimated size, partitioning, ordering, join type, equality predicates, null semantics, broadcast thresholds, memory budgets, and skew. A small dimension relation can be broadcast to executors. Large equi-joins may use shuffle hash or sort-merge. Non-equi joins need special handling and strict limits.

Exchange insertion is a major cost decision. It defines stage boundaries, shuffle materialization, and retry scope. Physical plans should make exchanges explicit and report their partitioning contracts.

### 7. Distributed execution: jobs, stages, tasks, and shuffle

An action creates a job. The physical plan is split into stages at shuffle boundaries. Each stage contains tasks over partitions. The driver schedules tasks onto executor pods, tracks attempts, handles retries, propagates cancellation, and merges task metrics. Executors run operators, read Delta/Parquet data, produce shuffle output, spill when needed, and return results or materialized artifacts.

The engineer must design shuffle metadata, partition IDs, map-output tracking, fetch protocols, spill files, retry behavior, and cleanup. Shuffle failures are common distributed-system events, not exceptional mysteries. Lost executors, slow pods, partial writes, corrupt blocks, and driver restarts need clear semantics.

Speculative execution and skew handling should be designed as controlled mechanisms. Duplicated task attempts must not double-commit writes or double-count metrics. Skewed partitions may need salting, split-and-reduce, adaptive coalescing, or explicit user-visible diagnostics.

### 8. Vectorized execution and code generation

DeltaSharp should favor columnar batches and vectorized operators. Filters evaluate over arrays or spans of values, projections materialize column vectors, and aggregates update compact state structures. Whole-stage code generation can fuse compatible scan/filter/project/aggregate fragments to reduce virtual dispatch and intermediate allocations.

In .NET, implementation options include expression trees, dynamic methods, source-generated kernels, `Span<T>` and `Memory<T>` processing, SIMD-friendly loops, memory pools, and async range reads. These are implementation choices behind stable physical-operator contracts. The engine should always retain an interpreted or generic fallback for debugging, unsupported expressions, and safer rollout.

Codegen requires guardrails: deterministic generated code, cache invalidation by expression shape and type, safe handling of nulls, bounded compilation overhead, clear diagnostics, and parity tests between interpreted and generated paths.

### 9. Adaptive execution and statistics

Cost-based planning is only as good as its statistics. Delta file metadata, Parquet row-group statistics, connector-provided estimates, catalog table statistics, and runtime metrics can all inform decisions. The engine should separate stable logical rewrites from cost-sensitive choices.

Adaptive execution may change join strategies, coalesce shuffle partitions, split skewed partitions, or reuse exchanges after observing runtime sizes. It must preserve semantics and remain explainable. Adaptive changes should appear in final plan output and metrics so operators can understand why a query behaved differently from its initial plan.

Statistics can be stale or incomplete. The planner needs conservative fallbacks, configurable thresholds, and regression tests for cases where estimates are wrong.

### 10. Caching strategies

Caching should target layers where correctness and invalidation are tractable:

- **Parsed and analyzed plan cache** keyed by SQL text, session settings, catalog versions, and function registry versions.
- **Optimized plan cache** only when referenced table snapshots, capabilities, and settings are stable.
- **Delta snapshot and file-list cache** keyed by table identity and version.
- **Statistics cache** for partition/file/row-group metadata.
- **Broadcast relation cache** scoped by query, tenant, snapshot, and memory budget.
- **Shuffle cache** for retry and stage reuse where safe.
- **Persisted DataFrame cache** with explicit storage level, lineage, tenant scope, and invalidation behavior.

Tenant identity and authorization context must be part of cache keys whenever cached content can reveal data, metadata, timing, or existence. Whole-result caching is rarely a substitute for plan and data-fragment caching because actions, sessions, and snapshots carry semantics.

### 11. Multi-tenant read-time isolation

DeltaSharp's execution engine must assume shared infrastructure: executors, object-store bandwidth, shuffle storage, caches, and driver scheduling may serve more than one tenant or workload class. The query engine owns read-time enforcement primitives:

- maximum concurrent actions per tenant;
- maximum scan bytes and files per query;
- maximum shuffle bytes and spill bytes;
- memory budgets for broadcasts, joins, aggregations, and sorts;
- task and stage timeouts;
- fair scheduling across tenants and jobs;
- cancellation that reaches executor tasks and storage reads;
- safe cache scoping and eviction.

The limit model must live inside execution, not just at API ingress. Optimizer rewrites, adaptive splits, retries, and internal subqueries should not bypass quotas. When a query is killed, all participating tasks should release memory, close reads, and clean up shuffle artifacts predictably.

### 12. Observability and diagnostics

The engine should make query behavior inspectable. `EXPLAIN` should show parsed/analyzed/optimized/physical plans, pushed predicates, pruned columns, skipped files, chosen join strategies, exchanges, codegen regions, estimated sizes, and adaptive changes. Runtime metrics should include planning time, rule time, scan bytes, files read, row groups skipped, rows output, shuffle bytes, spill bytes, broadcast size, task duration, queue time, executor failures, retries, cache hits, and tenant identifiers where appropriate.

These metrics feed SRE alerts, Performance regression gates, FinOps cost models, Security reviews, and user support. They are not optional debug logs.

## Expected behaviors

- Writes precise query semantics specs before implementing new SQL or DataFrame/Dataset behavior.
- Produces logical and physical plan examples for every major operator, including unresolved and analyzed forms.
- Profiles real plans and realistic Delta snapshots before optimizing hot paths.
- Tests optimizer rules with golden plan files and result-correctness tests.
- Treats lost pushdown, extra exchanges, disabled column pruning, and plan-size explosions as regressions.
- Reviews storage read contracts for pushdown and statistics implications before accepting changes.
- Embeds tenant limits in planner and executor paths, not only in front-door validation.
- Requires `EXPLAIN` and operator metrics for new physical strategies.
- Keeps interpreted and generated execution paths behaviorally identical through differential tests.
- Designs cancellation, retry, and cleanup as part of each distributed operator.
- Maintains representative-query and query-bomb catalogues for benchmarks and chaos tests.
- Reads database and distributed-systems literature continuously and translates it into practical engine design.

## Traits and attributes

- **Semantic precision**: can explain why `NULL` handling, alias resolution, window frames, and join types affect both results and rewrites.
- **Plan-tree fluency**: can look at a query and sketch unresolved, analyzed, optimized, and physical plans without running code.
- **Cost intuition**: sees when a small API call hides a large scan, shuffle, broadcast, spill, or object-store request pattern.
- **Adversarial mindset toward queries**: assumes users can accidentally create full-table scans, cross joins, skewed shuffles, or unbounded collects.
- **Distributed-systems discipline**: treats pod loss, retries, stragglers, partial shuffle output, and cancellation as ordinary execution cases.
- **Columnar performance sense**: understands batches, vectors, cache locality, null bitmaps, dictionary encoding, and row-group skipping.
- **Runtime pragmatism**: uses expression trees, IL/codegen, memory pools, async I/O, and `Span<T>` when they improve the engine, without leaking them into public semantics.
- **Cross-functional communication**: translates between user-visible Spark behavior, storage capabilities, execution metrics, and platform constraints.
- **Healthy skepticism of cost models**: uses statistics, but verifies plans against runtime metrics and conservative fallbacks.

## Anti-patterns

- **Eager transformations**: `filter`, `select`, or `join` reads data before an action.
- **Mutable plan trees**: analyzer or optimizer rules mutate nodes in place, making rule order and debugging unsafe.
- **Pushdown theater**: plan output claims a filter pushed into storage while the physical scan still reads every file and row group.
- **Unscoped caches**: table, broadcast, shuffle, or result caches omit tenant, authorization, session setting, or snapshot identity.
- **Statistics worship**: cost-based planning assumes estimates are exact and chooses memory-risky joins without fallback.
- **Invisible exchanges**: physical plans insert shuffles without making stage boundaries and partitioning visible.
- **Row-at-a-time default**: the engine materializes row objects through the hot path when columnar batches would preserve semantics and reduce overhead.
- **Driver bottlenecking**: executor results or metadata are collected at the driver without bounded streaming, aggregation, or backpressure.
- **Cancellation as best effort only**: killed queries stop the client response but leave executor tasks, reads, or shuffle files alive.
- **Write semantics hidden in reads**: query execution bypasses Delta commit protocols or lets speculative attempts double-commit output.
- **Compatibility by name only**: methods mirror Spark names while nulls, types, joins, or window semantics differ silently.

## What This Means for DeltaSharp

**Native Delta tables plus Spark-compatible planning on .NET — design-phase decisions:**

1. **Plan pipeline first**: Define immutable logical and physical plan nodes before building operator internals. SQL and DataFrame/Dataset APIs should both lower into the same unresolved logical-plan vocabulary.

2. **Analyzer as a product**: Catalog resolution, function binding, type coercion, alias scoping, nested-field access, time-travel snapshot binding, and write-schema validation need explicit rules and user-grade error messages.

3. **Delta-aware optimization**: Treat partition pruning, column pruning, predicate pushdown, and data skipping as core requirements for the first scan operator, not future enhancements. The scan plan should expose which files, partitions, row groups, and columns were eliminated.

4. **Physical strategies as contracts**: Broadcast hash join, shuffle hash join, sort-merge join, local/global aggregation, vectorized scan, and exchange operators need documented input distribution, ordering, memory, spill, and retry contracts.

5. **Kubernetes execution semantics**: Model actions as jobs, jobs as stages, and stages as tasks scheduled on executor pods. Shuffle boundaries split stages. Task retry, pod loss, speculative execution, and cancellation must be designed before the first large query runs.

6. **Runtime-neutral first, .NET-efficient second**: Keep plan semantics independent of implementation details, then implement hot paths with columnar batches, memory pools, async I/O, expression trees, IL/codegen, and `Span<T>` where justified.

7. **Tenant limits in the engine**: Enforce scan-byte, memory, shuffle, concurrency, and timeout limits during planning and execution. Include tenant and authorization context in caches and metrics.

8. **Explainability from day one**: `EXPLAIN` should be part of the developer loop immediately. It is how API, storage, performance, SRE, and users agree on what the engine is actually doing.

9. **Representative workloads**: Maintain a query catalogue with TPC-DS-style analytics, Spark migration examples, nested schemas, schema evolution, time-travel reads, skewed joins, wide projections, and query-bomb patterns.

10. **No hidden single-node assumptions**: Any operator that collects to the driver or requires a single executor must declare limits, fallback behavior, and user-visible errors.

## Confidence Assessment

| Area | Maturity | Notes |
|---|---|---|
| Spark SQL and DataFrame semantics | **High** | Spark provides a large public compatibility target and extensive behavioral examples, though perfect parity requires careful edge-case testing. |
| Catalyst-style rule optimization | **High** | Rule batches, immutable plans, and explainable rewrites are mature patterns with strong precedent. |
| Delta/Parquet pushdown and data skipping | **High** | Partition pruning, column pruning, row-group statistics, and file-level statistics are well understood; correctness depends on strict null/type handling. |
| Distributed stage/task execution | **High** | Spark and similar systems validate the model; DeltaSharp must adapt it to Kubernetes-native executor pods and .NET runtime constraints. |
| Whole-stage codegen in .NET | **Medium** | The concept is proven elsewhere; DeltaSharp must choose safe expression-tree, IL, or generated-code mechanisms with fallback paths. |
| Vectorized .NET execution | **Medium-High** | `Span<T>`, SIMD-friendly loops, and memory pooling are mature, but columnar engine kernels require disciplined implementation and benchmarking. |
| Adaptive execution | **Medium** | Useful for skew and bad estimates, but should follow a stable baseline planner and strong metrics. |
| Multi-tenant execution isolation | **Medium** | Quotas and fair scheduling are known patterns; combining them with shared executor pods, shuffle storage, and object-store budgets needs careful design. |
| Spark parity for typed Dataset APIs | **Medium-Low** | .NET type systems and expression trees differ from JVM encoders, so parity goals need explicit scope and compatibility tests. |
| Object-store plus PVC abstraction | **Medium** | Both storage classes are understood, but planning must avoid assumptions that only hold for one backend. |

## Footnotes

[^1]: Apache Spark Project, Spark SQL, DataFrames and Datasets Guide; Spark SQL programming guide and API references.
[^2]: Delta Lake Project, Delta Transaction Log Protocol and Delta Lake documentation for ACID transactions, time travel, schema evolution, and metadata statistics.
[^3]: Graefe, G., "Volcano — An Extensible and Parallel Query Evaluation System," IEEE TKDE, 1994; Graefe, G., "The Cascades Framework for Query Optimization," IEEE Data Engineering Bulletin, 1995.
[^4]: Boncz, P., Zukowski, M., and Nes, N., "MonetDB/X100: Hyper-Pipelining Query Execution," CIDR, 2005; related vectorized execution literature.
[^5]: Armbrust, M. et al., "Spark SQL: Relational Data Processing in Spark," SIGMOD, 2015; Apache Spark adaptive query execution and Tungsten documentation.
[^6]: Apache Parquet Project, Parquet format documentation for row groups, column chunks, encodings, and statistics.
[^7]: Kubernetes Project documentation for controllers, pods, jobs, persistent volumes, and workload lifecycle.
[^8]: AWS S3, Azure Data Lake Storage, and cloud object-store design guidance for request patterns, range reads, listing, and consistency behavior.
[^9]: Microsoft .NET documentation for expression trees, `System.Reflection.Emit`, `Span<T>`, memory pooling, async I/O, diagnostics, and EventPipe.
[^10]: Transaction Processing Performance Council, TPC-DS benchmark specification; Spark SQL benchmark practices.
[^11]: Zaharia, M. et al., "Resilient Distributed Datasets: A Fault-Tolerant Abstraction for In-Memory Cluster Computing," NSDI, 2012; distributed shuffle and speculative execution practice in large-scale data systems.
