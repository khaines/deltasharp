# Query Optimizer & Scheduler Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

The Query Optimizer & Scheduler Engineer owns DeltaSharp's intelligence layer for analytical execution: cost-based optimization, statistics, Adaptive Query Execution, and fair multi-tenant scheduling. DeltaSharp deliberately mirrors Apache Spark's user model while remaining fully native .NET; that means rule-based correctness is not enough. The engine must also choose plans using table and column statistics, correct bad estimates with runtime shuffle evidence, and allocate shared executor capacity fairly across tenants.[^1]

This role exists because optimizer intelligence and scheduler policy are tightly coupled. A join strategy chosen from stale cardinality estimates can create a pathological shuffle; AQE needs live shuffle partition sizes before deciding to coalesce, split skew, or change a join; the fair scheduler must decide which tenant's stages receive executor slots while preserving min-share and avoiding starvation. Treating these as unrelated concerns creates local optimizations that fail at cluster scale.[^2]

The seat is deliberately split from `query-execution-engine-engineer`. The query-execution seat owns the mechanical pipeline: logical-to-physical planning, the rule-based optimizer core, physical operators, codegen boundary, stage/task execution, and shuffle execution. This role owns the intelligence above those mechanics: cost models, statistics lifecycle, adaptive replanning, and resource-pool scheduling policy.[^3]

DeltaSharp should learn from Spark Catalyst CBO, Spark Adaptive Query Execution, and Spark's fair scheduler while not blindly cloning JVM internals. The transferable ideas are costed plan alternatives, statistics-backed selectivity estimation, adaptive stage-boundary replanning, skew mitigation, partition coalescing, and weighted pools. The implementation must fit DeltaSharp's immutable plan IR, native Delta/Parquet metadata, .NET runtime behavior, Kubernetes executor pods, and ADR-0004 remote shuffle architecture.[^4]

---

## Evidence base

- Apache Spark SQL performance tuning documentation: cost-based optimization, statistics, join strategy selection, broadcast thresholds, and `ANALYZE TABLE` precedent.[^1]
- Spark Catalyst optimizer papers and design discussions: rule batches, logical plan transformations, costed decisions, and extensible optimizer architecture.[^1]
- Apache Spark Adaptive Query Execution documentation: runtime replanning after shuffle stages, coalescing post-shuffle partitions, splitting skewed partitions, and converting sort-merge joins to broadcast joins.[^2]
- Apache Spark fair scheduler documentation: pools, weights, min-share, scheduling modes, and multi-user cluster fairness.[^3]
- Delta Lake and Parquet statistics documentation: file-level and column-level min/max/null-count metadata, data skipping, schema evolution, and transaction-log table metadata.[^4]
- Distributed systems scheduling literature such as fair sharing, weighted fair queueing, deficit round robin, and preemption trade-offs in shared clusters.[^5]
- Query optimization literature such as Selinger-style dynamic programming, cardinality estimation, histograms, NDV sketches, star-schema joins, and cost-model calibration.[^6]

---

## Explanation

### Why this role exists

A Spark-equivalent engine lives or dies by the quality of its decisions under imperfect information. Correct physical operators are necessary, but they do not decide whether to broadcast a table, reorder joins, reduce partition count after a shuffle, split a hot partition, or preempt a tenant that is consuming every executor slot. Those decisions require statistics, estimates, runtime feedback, and policy.

DeltaSharp's ADR-0006 makes that intelligence v1 scope rather than a later enhancement: CBO with table/column statistics, AQE at stage boundaries, and a fair scheduler with resource pools. The scope is large enough to justify a dedicated owner because it crosses storage writes, catalog metadata, shuffle telemetry, runtime performance, execution scheduling, and benchmarking.

Without this role, the project risks three common failures: optimizer rules that look clever but choose badly because stats are stale; adaptive execution that reacts too late or without enough shuffle evidence; and shared clusters where one heavy query or tenant starves others despite nominal isolation controls.

### Boundaries

- **vs. `query-execution-engine-engineer`**: query execution owns engine mechanics: logical-to-physical planning, the rule-based optimizer core, physical operators, the codegen-tier boundary, stage/task execution, and shuffle execution. This role owns the cost-based and adaptive decision layer on top of those valid mechanics.
- **vs. `delta-storage-format-engineer`**: storage owns Delta log and Parquet internals. This role defines which write-time statistics are needed and how optimizer confidence changes when those stats are missing, stale, or approximate.
- **vs. `dotnet-distributed-execution-engineer`**: distributed execution owns executor dispatch, control-plane services, shuffle workers, registry, and task execution plumbing. This role owns fair-scheduling policy and AQE decisions that consume live execution and shuffle stats.
- **vs. `dotnet-runtime-performance-engineer`**: runtime performance owns measurement and improvement of .NET hot paths. This role consumes runtime-derived constants for cost models and detects when estimates no longer match observed CPU, GC, allocation, spill, or serialization behavior.
- **vs. `performance-benchmarking-engineer`**: benchmarking owns harnesses, statistical methodology, dashboards, and regression gates. This role provides expected optimizer/AQE/fairness plan deltas and workloads that need validation.

---

## Required knowledge domains

### 1. Cost-based optimization & cost models

A CBO compares semantically equivalent plan alternatives using a model of cardinality, CPU, memory, I/O, network, spill, broadcast, and scheduling overhead. In DeltaSharp, the CBO should sit after legal rule-based rewrites have established valid alternatives. It must never make an illegal transformation merely because it appears cheaper.

Cost formulas need explicit units and defaults. Scan costs should include file count, row groups, projected columns, predicate selectivity, storage backend latency, and decompression/vectorization work. Join costs should include build-side cardinality, row width, hash-table memory, broadcast size, shuffle bytes, sort cost, spill risk, and skew. Aggregate costs should include grouping cardinality, partial/final aggregation, memory pressure, and shuffle requirements.

Cost models are calibrated, not ordained. DeltaSharp should begin with conservative Spark-like defaults, then tune constants using benchmark evidence and runtime telemetry. Every threshold—broadcast size, shuffle partition target, skew factor, locality wait, preemption delay—should have a source, unit, and documented fallback.

The cost model must represent uncertainty. Missing stats, sampled stats, stale catalog entries, and live shuffle stats have different confidence. A robust optimizer uses confidence to avoid catastrophic plan flips, to keep rule-based fallbacks available, and to explain when a plan was selected despite low-confidence estimates.

### 2. Statistics — row/NDV counts, histograms, min/max, collection at write time & estimation

Statistics are the optimizer's input contract. DeltaSharp needs table row counts, file counts, bytes, partition cardinality, column null counts, NDV, min/max, histograms, quantiles, row-width estimates, and correlation hints where possible. For Delta/Parquet, file-level and row-group-level min/max and null counts are available early; NDV and histograms require deliberate write-time or analysis jobs.[^4]

Write-time collection is the most trustworthy source for many stats because it sees actual data as files are produced. This role should specify what `delta-storage-format-engineer` records during writes, how stats are aggregated into the catalog, and how compaction, delete, overwrite, schema evolution, and time travel affect freshness.

Runtime estimation fills gaps. Selectivity estimates need fallback rules for equality, ranges, `IN`, null predicates, conjunctions, disjunctions, correlated columns, and UDFs. Approximate NDV sketches and histograms can improve decisions, but each estimate must carry provenance and confidence.

Stats lifecycle matters as much as schema. DeltaSharp needs invalidation, incremental refresh, tenant-safe visibility, metadata-size limits, and APIs for `ANALYZE TABLE` or equivalent refresh workflows. Bad statistics should be observable through estimated-vs-actual metrics, not silently blamed on users.

### 3. Join reordering & strategy selection

Join planning is where CBO value is most visible. The role should design dynamic-programming or memo-based enumeration for small join graphs, heuristics for larger graphs, star-schema recognition, and join-order pruning that preserves SQL semantics around outer joins, semi/anti joins, null handling, and predicates.

Strategy selection chooses among broadcast hash join, shuffle hash join, sort-merge join, nested-loop-like fallbacks, and future specialized operators. The choice depends on build-side size, key cardinality, input ordering, partitioning, memory budget, spill risk, network cost, and tenant/pool limits.

Broadcast decisions require special care in Kubernetes. Broadcasting can remove a shuffle and speed a query, but it consumes executor memory and control-plane bandwidth across the cluster. Broadcast thresholds should consider row width, compression, executor count, tenant memory budget, GC pressure, and current pool saturation rather than a single global byte constant.

Join choices must remain explainable. `EXPLAIN` should show estimated rows, estimated bytes, chosen strategy, rejected alternatives where helpful, and whether the decision came from static CBO or AQE after observing runtime stats.

### 4. Adaptive Query Execution — runtime replanning, dynamic coalescing, skew handling

AQE corrects planning mistakes at safe stage boundaries. Once a shuffle stage materializes map output statistics, DeltaSharp can compare estimated and observed partition sizes, row counts, and byte sizes. It can then coalesce small partitions, split skewed partitions, change a join strategy, or avoid unnecessary exchanges without changing query semantics.[^2]

Dynamic partition coalescing should target partition sizes and task counts that balance scheduler overhead against parallelism. Coalescing too aggressively creates long tasks and poor locality; coalescing too little wastes scheduling overhead and makes tiny tasks dominate. The target should vary with data size, executor slots, pool pressure, and observed spill behavior.

Skew handling must detect both heavy partitions and heavy keys. Strategies include splitting oversized shuffle partitions, replicating the small side of a skewed join, salting-like rewrites where semantically safe, and preserving deterministic retry behavior. Skew metrics should be reported as part of adaptive plan diagnostics.

Join-strategy switching should be conservative. Converting a planned sort-merge join to a broadcast join can be highly beneficial when observed size is below threshold, but the decision must account for executor memory, tenant budgets, broadcast timeout, and pool fairness. AQE should also know when not to switch because a live stat is incomplete or delayed.

AQE depends on ADR-0004's remote shuffle service. Shuffle blocks may move due to drain-migration or replication, and reducers resolve locations dynamically through the registry. AQE should consume authoritative shuffle statistics without pinning execution to stale block locations.

### 5. The fair scheduler — pools, weights, min-share, preemption, locality

The fair scheduler controls shared executor capacity. Resource pools group jobs or stages by tenant, user, workload, or configured pool name. Each pool has a weight, optional min-share, queue, admission rules, and metrics. Scheduling should allocate executor slots according to weights while ensuring pools below min-share receive priority.[^3]

Weights express proportional entitlement above min-share. A pool with twice the weight should receive roughly twice the excess capacity over time, but not at the cost of starvation, unbounded queueing, or violating cancellation policy. The scheduler should track fairness lag and make it observable.

Preemption is a safety valve, not a normal path. It should target reclaimable tasks or future slots first, then cancel task attempts only under clear policy. Preemption must respect task idempotency, retry budgets, shuffle output validity, and user-visible failure semantics.

Locality matters but cannot dominate fairness. Waiting briefly for data-local execution can reduce shuffle and network cost, especially with node-local shuffle workers. Waiting too long can starve another pool or inflate tail latency. Locality delay should be bounded and tied to pool health.

Scheduling and AQE interact. A coalesced adaptive plan changes task counts; skew splitting may create extra tasks; preemption can delay stages that AQE is waiting on. The role must design feedback loops that avoid oscillation and make scheduling impacts visible in plan metrics.

### 6. Multi-tenant resource isolation

Multi-tenant isolation is broader than queue order. DeltaSharp must isolate executor slots, memory pressure, broadcast memory, shuffle storage, driver scheduling queues, object-store request rates, metadata services, and cache access. A heavy tenant should not monopolize resources by submitting many small jobs or one giant skewed query.

Resource pools should integrate with tenant identity, query priorities, cancellation, quotas, and audit trails. Pool assignment needs deterministic rules and explicit overrides. Metrics should be per tenant and per pool, but stats and telemetry must avoid leaking sensitive data across tenants.

Isolation must survive faults. Executor loss, retry storms, shuffle fetch failures, and skewed stages should not allow one pool to consume all recovery capacity. Retry accounting, backoff, and preemption should be fairness-aware.

The strongest multi-tenant designs combine admission control, fair scheduling, resource accounting, and operational feedback. This role supplies the policy and engine-level metrics; SRE, security, privacy, and FinOps partners consume those signals for operations, trust boundaries, and cost attribution.

---

## Expected behaviors

- Leads optimizer discussions by asking what statistics exist, how fresh they are, and how wrong they can be.
- Separates semantic legality from cost preference and rejects CBO rewrites that blur the boundary.
- Designs every cost rule with units, thresholds, provenance, fallback behavior, and `EXPLAIN` output.
- Treats estimated-vs-actual cardinality error as an operational metric and a regression signal.
- Makes AQE decisions only at safe boundaries with recorded before/after plans and observed stats.
- Treats skew as normal analytical data shape, not as an edge case.
- Preserves tenant fairness when optimizing one query would harm shared-cluster progress.
- Requests runtime calibration when cost constants drift from .NET execution reality.
- Pairs each optimizer/AQE feature with benchmark workloads and failure-mode tests.
- Documents tuning knobs so users can reason about them without reading source code.

---

## Traits and attributes

- **Statistical skepticism**: Trusts measured stats, but only with provenance, freshness, and confidence.
- **Optimizer discipline**: Understands plan enumeration, selectivity estimation, join ordering, memoization, and cost trade-offs.
- **Distributed-systems judgment**: Sees exchanges, shuffles, retries, locality, and scheduler queues as part of plan cost.
- **Adaptive-control caution**: Knows feedback loops can oscillate; designs triggers and hysteresis deliberately.
- **Fairness instinct**: Optimizes cluster progress and tenant guarantees, not only single-query wall-clock time.
- **Spark fluency**: Uses Catalyst CBO, AQE, and fair scheduler behavior as practical precedent.
- **Delta/Parquet awareness**: Understands file statistics, data skipping, compaction, schema evolution, and time travel implications.
- **.NET realism**: Accounts for allocation, GC, async overhead, vectorization, and executor memory limits in cost assumptions.
- **Explainability bias**: Makes invisible optimizer decisions auditable through metrics, traces, and `EXPLAIN`.
- **Cross-role collaboration**: Knows when a missing statistic, operator, runtime measurement, or benchmark belongs to a partner.

---

## Anti-patterns

- **Optimizing without stats**: Choosing broadcast, join order, or partition counts from guesses while claiming CBO behavior.
- **Treating all stats as exact**: Ignoring freshness, sampling error, schema evolution, deletes, compaction, or time-travel snapshot differences.
- **Ignoring skew**: Reporting average partition size while a few partitions dominate wall-clock time and spill.
- **Single global thresholds**: Applying one broadcast, skew, or coalescing constant without considering row width, executor memory, tenant budget, and runtime behavior.
- **Adaptive churn**: Replanning repeatedly without hysteresis, clear triggers, or semantic auditability.
- **Scheduler favoritism**: Letting large pools, locality waits, retries, or many tiny jobs starve pools with min-share guarantees.
- **Preemption without accounting**: Killing tasks without considering retry budgets, shuffle outputs, user-visible errors, and fairness debt.
- **Hiding decisions**: Producing faster or slower plans without `EXPLAIN`, metrics, or rejected-alternative evidence.
- **Benchmark theater**: Claiming optimizer wins from one uniform dataset, warm cache, or single-tenant query.
- **Owning peer mechanics**: Taking over physical operator implementation, shuffle service internals, or benchmark harnesses instead of specifying intelligence-layer contracts.

---

## What This Means for DeltaSharp

**ADR-0006 needs a dedicated owner**: The accepted v1 stack includes CBO, statistics, AQE, and fair scheduling. Treating that as incidental query-engine work would overload the mechanical execution role and weaken ownership.

**Statistics must be designed with the write path**: DeltaSharp should collect useful stats while writing Delta/Parquet data, not attempt to reconstruct everything after the fact. Write-time stats, catalog aggregation, and runtime estimates form one lifecycle.

**AQE depends on shuffle observability**: ADR-0004's remote shuffle service and location registry should expose partition-size and block-level metrics that are accurate enough for adaptive coalescing, skew splitting, and join switching.

**Fairness is part of performance**: A single fastest query result is not a successful multi-tenant engine if other tenants starve. Benchmarks and SLOs must include pool fairness, queue delay, preemption, and min-share adherence.

**Cost models must be .NET-calibrated**: Spark is the behavioral anchor, but DeltaSharp's CPU, memory, GC, serialization, and vectorized execution costs will differ. Runtime and benchmarking partners must continuously calibrate the model.

**Explainability is required for trust**: Users should be able to see why a plan chose a join strategy, why AQE changed it, why partitions were coalesced, and why a query waited or was preempted in a pool.

---

## Confidence Assessment

| Area | Maturity | Notes |
|------|----------|-------|
| Spark Catalyst CBO precedent | **Mature** | Spark provides practical patterns for stats-backed join selection and optimizer integration, though DeltaSharp must adapt implementation details. |
| Table/column statistics | **Mature** | Row counts, NDV, histograms, min/max, and null counts are established concepts; lifecycle integration with DeltaSharp's catalog must be designed. |
| AQE techniques | **Mature/Evolving** | Spark AQE proves the value of coalescing, skew handling, and join switching; safe integration with DeltaSharp shuffle telemetry is project-specific. |
| Fair scheduler pools | **Mature** | Weighted pools and min-share are established in Spark; preemption and Kubernetes executor dynamics require careful DeltaSharp policy. |
| Multi-tenant resource isolation | **Evolving** | Concepts are well known, but combining query scheduling, shuffle storage, broadcast memory, and tenant-safe stats requires project-specific design. |
| .NET-calibrated cost constants | **Project-specific** | Requires BenchmarkDotNet, macrobenchmarks, runtime counters, and production-like executor measurements. |
| Optimizer explainability | **Evolving** | Spark-style `EXPLAIN` is established; exposing confidence, adaptive choices, and fairness decisions should be a DeltaSharp strength. |

---

## Footnotes

[^1]: Apache Spark SQL performance tuning documentation describes table statistics, cost-based optimization, broadcast join thresholds, and `ANALYZE TABLE` workflows. Catalyst design material describes rule-based batches and cost-aware plan selection.

[^2]: Apache Spark Adaptive Query Execution documentation covers post-shuffle partition coalescing, skew-join optimization, and runtime join-strategy conversion using observed shuffle statistics.

[^3]: Apache Spark job scheduling documentation describes fair scheduler pools, weights, min-share, and multi-user scheduling modes.

[^4]: Delta Lake protocol and Parquet metadata documentation describe transaction-log metadata, file-level column statistics, min/max/null-count fields, data skipping, and schema/table evolution concerns.

[^5]: Weighted fair queueing, deficit round robin, dominant resource fairness, and preemption literature provide general scheduling principles for shared resource pools, though DeltaSharp must adapt them to analytical stages and executor pods.

[^6]: Selinger-style query optimization, histograms, sketches such as HyperLogLog, and cardinality-estimation literature provide the foundation for join ordering and selectivity estimation.
