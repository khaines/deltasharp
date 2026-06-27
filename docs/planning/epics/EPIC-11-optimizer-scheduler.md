# EPIC-11: Query Optimizer, Statistics, AQE & Scheduler

- **Roadmap milestone:** M4 ([Milestone 4 — Optimization](../../../ROADMAP.md#milestone-4--optimization-v0x))
- **Primary persona(s):** `query-optimizer-scheduler-engineer` (+ collaborators `delta-storage-format-engineer`, `query-execution-engine-engineer`, `dotnet-distributed-execution-engineer`, `cloud-native-distributed-systems-architect`)
- **Related ADRs:** ADR-0006, ADR-0004
- **Depends on:** EPIC-05, EPIC-07, EPIC-08
- **Status:** draft
- **Size:** L

## Objective

Deliver DeltaSharp's v1 intelligence layer for cost-based planning, statistics, adaptive execution, and fair scheduling. This epic turns Delta/Parquet, catalog, shuffle, runtime, and tenant signals into Spark-compatible plan choices and resource decisions that are measurable, explainable, and safe under missing or stale inputs.

## Scope

**In scope**
- Table, column, file, partition, and runtime statistics contracts for optimization, including row counts, NDV, null counts, min/max, histograms, file sizes, partition cardinality, and confidence/provenance metadata.
- Cost model and cardinality estimation for scans, filters, joins, aggregates, exchanges, broadcast, spill, and scheduler-aware costs.
- CBO rules that choose among legal plan alternatives established by the rule-based optimizer, including join ordering, join strategy, aggregate strategy, and explainable cost traces.
- Adaptive Query Execution at stage boundaries using live shuffle statistics for dynamic partition coalescing, skew-join mitigation, join-strategy switching, and adaptive `EXPLAIN` output.
- Fair scheduler and resource-pool policy for weighted pools, min-share, preemption, locality-aware placement, queueing, starvation prevention, and tenant isolation.

**Out of scope** (and where it lives instead)
- Rule-based optimizer core, logical/physical plan IR, operator implementation, exchange insertion mechanics, and stage/task execution mechanics → EPIC-04, EPIC-08 / persona `query-execution-engine-engineer`.
- Delta log, Parquet encoding, checkpoint format, compaction, clustering, and storage-format internals → EPIC-05 / persona `delta-storage-format-engineer`.
- SQL grammar and parsing for `ANALYZE`, optimizer hints, and `EXPLAIN` syntax → EPIC-07 / persona `sql-language-frontend-engineer`.
- Catalog/metastore persistence APIs, namespace authorization, and external metastore adapters → EPIC-06 / persona `catalog-metastore-engineer`.
- Shuffle worker, location registry, Arrow Flight data plane, executor dispatch plumbing, and remote shuffle durability → EPIC-08, EPIC-09 / persona `dotnet-distributed-execution-engineer`.
- Kubernetes CRDs and cluster control loops for exposing scheduler policy → EPIC-10 / persona `kubernetes-operator-controller-engineer`.

## Exit criteria

- [ ] `ANALYZE TABLE` and write-time collection populate table and column statistics, including row counts, NDV or approximation metadata, null counts, min/max, histograms where enabled, file sizes, table-version provenance, and confidence, persisted and retrieved through the catalog.
- [ ] On a stats-driven test set with at least three-way joins and skewed distributions, the CBO chooses the expected join order and broadcast/shuffle-hash/sort-merge strategies, and rule-only fallback remains correct when stats are absent or marked stale.
- [ ] AQE measurably coalesces undersized shuffle partitions and mitigates skew on a skewed dataset using live shuffle statistics from the distributed runtime/shuffle service, with byte-for-byte equivalent query results versus non-adaptive execution where ordering semantics permit.
- [ ] The fair scheduler enforces configured pool weights and min-share, prevents starvation, isolates tenants under contention, and records preemption/locality decisions in deterministic tests and contention benchmarks.
- [ ] `EXPLAIN` exposes estimated cardinalities, statistics provenance/confidence, cost inputs, selected optimizer rules, physical strategy choices, initial and adaptive plans, and scheduler-pool decisions in stable automated-testable output.

## Features

### FEAT-11.1: Statistics subsystem for optimizer inputs

- **Objective:** Define and implement the statistics lifecycle that feeds CBO and AQE from Delta/Parquet write-time metadata, catalog state, explicit `ANALYZE`, and runtime observations. Statistics must be versioned, provenance-aware, confidence-scored, tenant-safe, and safe to ignore when stale or incomplete.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `delta-storage-format-engineer`, `catalog-metastore-engineer`, `sql-language-frontend-engineer`.
- **Depends on:** EPIC-05, EPIC-06, EPIC-07.

#### Stories

##### STORY-11.1.1: Define statistics schema and provenance model

- Implement `OptimizerStatistics` contracts that represent table, column, file, partition, row-group, histogram, NDV, null-count, min/max, file-size, table-version, freshness, and confidence metadata.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `delta-storage-format-engineer`, `catalog-metastore-engineer`.
- **Size:** M. **Depends on:** EPIC-05, EPIC-06.
- **Acceptance criteria:**
  - [ ] Given a Delta table snapshot with active files When statistics are read Then every statistic records table version, source, collection time, and confidence.
  - [ ] Given a column type that cannot support ordered min/max When statistics are represented Then the contract marks that statistic unsupported without blocking other statistics.
  - [ ] Given exact write-time stats and sampled `ANALYZE` stats for the same column When the optimizer requests inputs Then the contract exposes both provenance and confidence rather than overwriting the distinction.
  - [ ] Given tenant-scoped catalog access When statistics are retrieved Then no cross-tenant table identifiers or metrics are exposed.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `16`, `17`, `14`, `04a` satisfied; docs updated if public API changes.

##### STORY-11.1.2: Collect write-time and Delta/Parquet statistics

- **As a** query optimizer **I want** write-time Delta/Parquet statistics surfaced through a stable contract **so that** fresh tables can be optimized before a full `ANALYZE` scan.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `delta-storage-format-engineer`.
- **Size:** M. **Depends on:** STORY-11.1.1, EPIC-05.
- **Acceptance criteria:**
  - [ ] Given a successful Delta write When files are committed Then row count, file size, partition values, min/max, and null counts available from storage metadata are surfaced to the optimizer statistics layer.
  - [ ] Given a Parquet file with missing or truncated footer statistics When the stats collector runs Then missing fields are marked partial and query execution remains correct.
  - [ ] Given a table version change through append or overwrite When statistics are refreshed Then stale version-bound statistics are invalidated or downgraded in confidence.
  - [ ] Given a storage statistics fixture When the catalog exposes it Then the optimizer can retrieve it without reading data files again.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `17`, `16`, `08`, `04a` satisfied; docs updated if public API changes.

##### STORY-11.1.3: Implement `ANALYZE` statistics refresh workflow

- **As a** SQL user **I want** `ANALYZE TABLE` to refresh table and column statistics **so that** CBO decisions are based on current data distributions.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `sql-language-frontend-engineer`, `catalog-metastore-engineer`, `delta-storage-format-engineer`.
- **Size:** L. **Depends on:** STORY-11.1.1, STORY-11.1.2, EPIC-07.
- **Acceptance criteria:**
  - [ ] Given `ANALYZE TABLE t COMPUTE STATISTICS` When the command completes Then table row count, total bytes, file count, and table-version provenance are persisted through the catalog.
  - [ ] Given `ANALYZE TABLE t COMPUTE STATISTICS FOR COLUMNS c1, c2` When the command completes Then NDV, null count, min/max, and histogram metadata are persisted where supported.
  - [ ] Given a concurrent table version change during analysis When results are committed Then the workflow either binds stats to the analyzed version or retries/fails with a precise stale-snapshot reason.
  - [ ] Given insufficient privileges to inspect a table When `ANALYZE` is requested Then no statistics are written and an authorization-aware error is returned by the catalog path.
  - [ ] Given an analyzed table When `EXPLAIN` is run Then the plan reports which table and column statistics came from explicit analysis.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `16`, `17`, `19`, `04a` satisfied; docs updated if public API changes.

### FEAT-11.2: Cost model and cardinality estimation

- **Objective:** Build a cost model that estimates cardinality, bytes, CPU, I/O, network, spill, broadcast, and scheduling costs with explicit units, confidence, and fallback behavior. The model must prefer robust plans when inputs are missing or stale and expose assumptions to `EXPLAIN`.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `query-execution-engine-engineer`, `dotnet-runtime-performance-engineer`, `performance-benchmarking-engineer`.
- **Depends on:** FEAT-11.1, EPIC-04, EPIC-08.

#### Stories

##### STORY-11.2.1: Estimate scan, filter, projection, and aggregate cardinalities

- Implement cardinality estimators for scan, filter, projection, and aggregate plan nodes that consume statistics provenance and confidence.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** FEAT-11.1.
- **Acceptance criteria:**
  - [ ] Given exact table row counts and column histograms When a filter predicate is estimated Then estimated output rows fall within documented tolerance for the fixture distribution.
  - [ ] Given missing NDV for a group-by key When aggregate cardinality is estimated Then the estimator uses a documented fallback and marks low confidence.
  - [ ] Given conjunctive and disjunctive predicates When cardinality is estimated Then selectivity composition is deterministic and visible in estimator diagnostics.
  - [ ] Given stale table-version statistics When a plan is estimated Then the estimate is downgraded and `EXPLAIN` identifies the stale input.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `16`, `08`, `22`, `04a` satisfied; docs updated if public API changes.

##### STORY-11.2.2: Estimate join cardinalities and data movement costs

- Implement join cardinality and cost estimators for equi, outer, semi, anti, cross, broadcast, shuffle-hash, and sort-merge alternatives.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `query-execution-engine-engineer`, `dotnet-distributed-execution-engineer`.
- **Size:** L. **Depends on:** STORY-11.2.1, EPIC-08.
- **Acceptance criteria:**
  - [ ] Given two relations with join-key NDV and null-count statistics When an equi-join is estimated Then output cardinality accounts for key overlap assumptions and null join semantics.
  - [ ] Given a broadcast candidate above the configured memory threshold When costs are compared Then broadcast is rejected or penalized and the reason appears in diagnostics.
  - [ ] Given a shuffle join alternative When cost is estimated Then network bytes, partition count, spill risk, and remote shuffle overhead are included with explicit units.
  - [ ] Given missing join-key stats When alternatives are costed Then the model keeps a semantically valid fallback and marks the decision low confidence.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `16`, `21`, `08`, `04a` satisfied; docs updated if public API changes.

##### STORY-11.2.3: Calibrate and expose cost-model constants

- **As a** performance engineer **I want** cost constants and thresholds to be calibrated and observable **so that** optimizer wins and regressions are attributable.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `dotnet-runtime-performance-engineer`, `performance-benchmarking-engineer`.
- **Size:** M. **Depends on:** STORY-11.2.1, STORY-11.2.2.
- **Acceptance criteria:**
  - [ ] Given benchmark calibration inputs When constants are loaded Then CPU, allocation, I/O, network, spill, broadcast, and scheduler-overhead constants include units and source metadata.
  - [ ] Given default constants When no calibration file is present Then the optimizer uses conservative documented values and emits no runtime failure.
  - [ ] Given a plan with cost-based choices When `EXPLAIN COST` is produced Then constants and major cost components used for each decision are visible.
  - [ ] Given a changed constant set When regression benchmarks run Then plan choices and runtime deltas are captured for comparison against gates.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `16`, `08`, `22`, `04a` satisfied; docs updated if public API changes.

### FEAT-11.3: Cost-based optimizer rules

- **Objective:** Add cost-based rules that select among semantically equivalent rule-based plan alternatives for join ordering, join strategy, aggregate strategy, exchange reuse, and plan hints. Rules must be deterministic, Spark-compatible, explainable, and safe under missing or stale statistics.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `query-execution-engine-engineer`, `sql-language-frontend-engineer`, `performance-benchmarking-engineer`.
- **Depends on:** FEAT-11.1, FEAT-11.2, EPIC-07.

#### Stories

##### STORY-11.3.1: Implement join reordering and star-schema recognition

- **As a** query planner **I want** stats-driven join reordering **so that** multiway joins avoid unnecessary large intermediates while preserving semantics.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** FEAT-11.2.
- **Acceptance criteria:**
  - [ ] Given a three-or-more-way inner equi-join with reliable stats When CBO is enabled Then the chosen join order minimizes estimated intermediate cost in the fixture.
  - [ ] Given a star-schema query When fact and dimension statistics identify the shape Then dimensions are ordered to reduce fact-side data movement where legal.
  - [ ] Given outer, semi, anti, or non-deterministic join predicates When reordering would change semantics Then the optimizer preserves the original legal order.
  - [ ] Given absent or low-confidence stats When join reordering is evaluated Then the rule uses documented fallbacks or declines to rewrite and records the reason.
  - [ ] Given `EXPLAIN` for a reordered query When output is inspected Then original order, selected order, costs, and rule rationale are visible.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `16`, `15`, `22`, `04a` satisfied; docs updated if public API changes.

##### STORY-11.3.2: Implement join-strategy selection

- Implement cost-based selection for broadcast hash join, shuffle hash join, and sort-merge join using relation size, partitioning, ordering, skew, memory, spill, and hint inputs.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `query-execution-engine-engineer`, `dotnet-distributed-execution-engineer`.
- **Size:** L. **Depends on:** STORY-11.2.2, STORY-11.3.1.
- **Acceptance criteria:**
  - [ ] Given a small build-side relation below the broadcast threshold When join planning runs Then broadcast hash join is selected and `EXPLAIN` names the threshold and size estimate.
  - [ ] Given both sides already partitioned by the join key When join planning runs Then shuffle cost is reduced or avoided according to the physical-planning contract.
  - [ ] Given large sorted inputs When sort-merge join has lower estimated spill and network cost Then it is selected over shuffle hash join.
  - [ ] Given a user hint that conflicts with memory or semantic safety When planning runs Then the hint is rejected or downgraded with a stable diagnostic.
  - [ ] Given missing stats When strategy selection runs Then a correct non-catastrophic default strategy is chosen.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `16`, `21`, `08`, `04a` satisfied; docs updated if public API changes.

##### STORY-11.3.3: Implement aggregate and exchange strategy selection

- Implement cost-based aggregate strategy selection and exchange-reuse choices for local/global aggregation, partial aggregation, shuffle width, and reuse of compatible exchanges.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `query-execution-engine-engineer`, `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** FEAT-11.2, EPIC-08.
- **Acceptance criteria:**
  - [ ] Given low-NDV group keys When aggregate planning runs Then local/partial aggregation is selected before shuffle where it reduces estimated bytes.
  - [ ] Given high-NDV group keys and memory pressure When aggregate planning runs Then the strategy accounts for spill risk and avoids unsafe in-memory assumptions.
  - [ ] Given two compatible downstream consumers When exchange reuse is legal Then the optimizer reuses the exchange and reports the saved cost.
  - [ ] Given exchange reuse would cross tenant, snapshot, or semantic boundaries When planning runs Then reuse is rejected and the reason is traceable.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `16`, `21`, `14`, `04a` satisfied; docs updated if public API changes.

### FEAT-11.4: Adaptive Query Execution

- **Objective:** Implement stage-boundary runtime re-optimization using live shuffle and task metrics while preserving query semantics. AQE must coalesce partitions, mitigate skew, switch join strategies, tolerate missing runtime stats, and expose initial and adaptive plans through `EXPLAIN`.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `dotnet-distributed-execution-engineer`, `query-execution-engine-engineer`, `performance-benchmarking-engineer`.
- **Depends on:** FEAT-11.2, FEAT-11.3, EPIC-08, EPIC-09.

#### Stories

##### STORY-11.4.1: Ingest live shuffle and task statistics for AQE

- Implement the AQE runtime-statistics contract that consumes partition sizes, row counts, spill bytes, fetch retries, locality, and skew metrics at stage boundaries.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `dotnet-distributed-execution-engineer`, `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** EPIC-08, EPIC-09.
- **Acceptance criteria:**
  - [ ] Given a completed shuffle map stage When AQE reads runtime statistics Then every reducer partition has size, row-count availability, provenance, and freshness status.
  - [ ] Given shuffle block migration or fetch retry When runtime stats are refreshed Then AQE re-resolves current locations and does not pin stale holders.
  - [ ] Given partial or delayed shuffle metrics When AQE evaluates a rewrite Then missing metrics are marked partial and unsafe rewrites are skipped.
  - [ ] Given tenant-scoped execution metrics When AQE diagnostics are emitted Then tenant identifiers and sensitive cross-tenant details are not leaked.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `21`, `16`, `14`, `04a` satisfied; docs updated if public API changes.

##### STORY-11.4.2: Coalesce shuffle partitions dynamically

- **As a** query runner **I want** AQE to coalesce undersized shuffle partitions **so that** task overhead falls without changing query results.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `dotnet-distributed-execution-engineer`, `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** STORY-11.4.1.
- **Acceptance criteria:**
  - [ ] Given many shuffle partitions below the target size When AQE runs at a stage boundary Then partitions are coalesced to meet configured byte and task-count targets.
  - [ ] Given downstream operators with ordering or distribution requirements When coalescing is considered Then AQE preserves required semantics or declines the rewrite.
  - [ ] Given AQE disabled by configuration When the same query runs Then the original non-adaptive partitioning is used.
  - [ ] Given benchmark fixtures with undersized partitions When coalescing is enabled Then task count and scheduler overhead decrease against the configured performance gate.
  - [ ] Given `EXPLAIN ADAPTIVE` When output is inspected Then the original partition count, final partition count, and reason are shown.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `16`, `21`, `22`, `04a` satisfied; docs updated if public API changes.

##### STORY-11.4.3: Mitigate skew and switch join strategies at runtime

- Implement AQE skew-join handling and runtime join-strategy switching based on observed partition sizes and build-side relation sizes.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `dotnet-distributed-execution-engineer`, `query-execution-engine-engineer`, `performance-benchmarking-engineer`.
- **Size:** L. **Depends on:** STORY-11.4.1, STORY-11.4.2, FEAT-11.3.
- **Acceptance criteria:**
  - [ ] Given a skewed shuffle dataset When AQE observes partitions above the skew threshold Then oversized partitions are split or otherwise mitigated according to the configured strategy.
  - [ ] Given a build-side relation observed below the adaptive broadcast threshold When a join boundary is reached Then AQE can switch from shuffle join to broadcast join if semantic and memory checks pass.
  - [ ] Given a runtime strategy switch would violate ordering, distribution, hint, or memory constraints When AQE evaluates it Then the switch is rejected with a stable diagnostic.
  - [ ] Given a skewed benchmark When AQE skew mitigation is enabled Then p95 task duration or stage completion time improves against the configured benchmark gate.
  - [ ] Given adaptive and non-adaptive executions of the same deterministic query When results are compared Then rows match byte-for-byte where ordering semantics allow.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `16`, `21`, `22`, `08`, `04a` satisfied; docs updated if public API changes.

### FEAT-11.5: Fair scheduler and resource pools

- **Objective:** Provide a tenant-aware scheduler policy layer with weighted resource pools, min-share, preemption, locality-aware placement, queueing, isolation, and observability. The scheduler must make bounded progress under contention without leaking tenant data or bypassing execution cancellation and retry semantics.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `cloud-native-distributed-systems-architect`, `dotnet-distributed-execution-engineer`, `cloud-native-site-reliability-engineer`.
- **Depends on:** EPIC-08, ADR-0006.

#### Stories

##### STORY-11.5.1: Define resource-pool policy and admission model

- Implement resource-pool configuration contracts for pool weights, min-share, max concurrency, queue limits, tenant binding, admission errors, and default policy.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `cloud-native-distributed-systems-architect`, `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** EPIC-08.
- **Acceptance criteria:**
  - [ ] Given a valid pool configuration When the scheduler loads it Then weights, min-share, concurrency, and queue limits are validated with units and defaults.
  - [ ] Given an invalid or unsafe pool configuration When it is loaded Then scheduling rejects it with precise diagnostics and keeps the previous valid policy.
  - [ ] Given a submitted job with tenant context When admission runs Then the job is assigned to the correct pool or rejected without falling into an unbounded default queue.
  - [ ] Given no explicit pool configuration When the engine starts Then a conservative default pool policy provides bounded concurrency and no tenant bypass.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `14`, `21`, `16`, `04a` satisfied; docs updated if public API changes.

##### STORY-11.5.2: Enforce weighted fairness, min-share, and starvation prevention

- **As a** platform operator **I want** fair scheduling under contention **so that** tenants receive configured shares and no tenant starves shared execution resources.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `cloud-native-distributed-systems-architect`, `dotnet-distributed-execution-engineer`, `cloud-native-site-reliability-engineer`.
- **Size:** L. **Depends on:** STORY-11.5.1.
- **Acceptance criteria:**
  - [ ] Given two busy pools with different weights When sufficient runnable tasks exist Then long-window executor allocation converges within documented tolerance of configured weights.
  - [ ] Given a pool below min-share while another pool is above fair share When new slots open Then the below-min-share pool is prioritized until its guarantee is restored.
  - [ ] Given a tenant with a long queue and low weight When the cluster remains busy Then starvation-prevention aging grants bounded progress without violating other pools' min-share.
  - [ ] Given task retries and speculative attempts When fairness accounting runs Then repeated attempts are charged to the originating pool and cannot bypass limits.
  - [ ] Given contention benchmarks When scheduler metrics are inspected Then pool share, queue time, min-share violations, and fairness lag are emitted.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `14`, `21`, `09b`, `22`, `04a` satisfied; docs updated if public API changes.

##### STORY-11.5.3: Implement preemption and locality-aware task placement

- Implement preemption and locality-aware placement so scheduler choices balance pool fairness, data locality, task retries, cancellation, and AQE/skew behavior.
- **Implementer persona(s):** Primary `query-optimizer-scheduler-engineer`; Collaborators `cloud-native-distributed-systems-architect`, `dotnet-distributed-execution-engineer`, `cloud-native-site-reliability-engineer`.
- **Size:** L. **Depends on:** STORY-11.5.1, STORY-11.5.2.
- **Acceptance criteria:**
  - [ ] Given a pool persistently below min-share and another pool holding preemptible work When preemption delay expires Then eligible tasks are cancelled or deprioritized according to policy and replacement capacity is assigned to the deprived pool.
  - [ ] Given data-local and non-local task placement options When locality wait would violate fairness or tail-latency budgets Then the scheduler chooses bounded non-local placement and records the reason.
  - [ ] Given executor loss or shuffle block migration When placement decisions are retried Then task locality and shuffle locations are re-evaluated rather than pinned to stale nodes.
  - [ ] Given a non-preemptible task or commit-critical region When preemption is evaluated Then the scheduler skips it and selects another eligible task or records no safe victim.
  - [ ] Given `EXPLAIN` or scheduler diagnostics for a contended job When output is inspected Then pool assignment, locality waits, preemption events, and tenant-safe reasons are visible.
- **Definition of done:** `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass; checklists `14`, `21`, `16`, `09b`, `04a` satisfied; docs updated if public API changes.

## Open questions

- Should v1 persist histograms for all supported comparable types by default, or gate histogram collection behind table properties to control Delta log and catalog metadata growth?
- What tolerance should benchmark gates use for CBO and AQE wins when cloud object-store latency and Kubernetes executor churn introduce run-to-run variance?
- Which scheduler-pool configuration surface is authoritative for v1: session/catalog metadata, cluster configuration, or Kubernetes CRDs once EPIC-10 is available?
