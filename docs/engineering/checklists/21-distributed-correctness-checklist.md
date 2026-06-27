# 21 — Distributed Correctness Checklist

> **Scope:** Lazy/eager semantics, physical execution, stages, shuffle, retries, cancellation, distributed fault recovery, and correctness-under-failure evidence.
> **Priority:** CRITICAL.
> **Owners:** reliability-test-chaos-engineer, dotnet-distributed-execution-engineer. **Grounded in:** ADR-0001, ADR-0004, ADR-0006, `.github/copilot-instructions.md`.

## How to use
Use this checklist for changes that can affect when work executes, what rows are produced, how shuffle data is found, or how failures are retried. Treat wrong results, eager transformations, lost shuffle data, and Delta isolation violations as Critical findings.

## Checklist
### Lazy/eager invariant
- [ ] Transformations such as select, filter, join, groupBy, repartition, coalesce, and withColumn only build immutable logical plans.
- [ ] Actions such as collect, count, show, write, and foreach trigger execution explicitly and are the only entry points that schedule work.
- [ ] Connector metadata discovery during planning does not scan user data or commit external writes.
- [ ] Tests assert that transformation chains perform no task scheduling, no data-plane reads, and no Delta commits.
- [ ] Public API deviations from Spark lazy/eager semantics are documented and reviewed as compatibility risks.

### Stage boundaries and shuffle semantics
- [ ] Physical plans insert stage boundaries at shuffle exchanges, wide dependencies, repartitions, joins, aggregations, sorts, and other required exchanges.
- [ ] Partitioning requirements are explicit for joins, aggregations, windows, ordering, limit, coalesce, and repartition.
- [ ] Repartition and coalesce preserve row multiplicity and documented ordering/partition-count semantics.
- [ ] Hash/range partitioning uses stable expressions, null handling, collation, and seed behavior across driver and executors.
- [ ] Join implementations preserve outer/semi/anti/null-aware semantics and never assume partitioning or ordering not proven by the plan.
- [ ] Aggregations handle partial/final merge ordering, floating-point tolerance, null groups, empty inputs, and duplicate task attempts.
- [ ] Sort and limit operators state whether ordering is global, partition-local, stable, or undefined.
- [ ] AQE changes from ADR-0006 re-check partitioning, ordering, and required distributions after runtime replanning.

### Execution backend parity
- [ ] The vectorized interpreter from ADR-0001 is the correctness reference for expressions and operators.
- [ ] Optional compiled/codegen paths run only when dynamic code is supported and must produce identical rows, nulls, errors, and metadata.
- [ ] Differential tests compare interpreter and compiled backends over generated schemas, expressions, batches, nulls, NaNs, decimals, timestamps, and nested data.
- [ ] SIMD, unsafe, and specialized fast paths have scalar/reference fallbacks and parity oracles.
- [ ] Backend selection cannot change public semantics, exception shape, row counts, partition IDs, or commit behavior.

### Delta and storage isolation
- [ ] Delta reads pin a snapshot version for the duration of the action, even while concurrent writers commit later versions.
- [ ] Concurrent Delta writers rely on optimistic commit, conflict detection, and idempotent retry from 17; execution retries never weaken ACID.
- [ ] Task retries for writes produce deterministic commit messages or abort records and cannot duplicate committed rows.
- [ ] Time-travel reads, schema evolution, deletion vectors, CDF, clustering, and row tracking preserve visibility rules during distributed execution.
- [ ] Object-store and PVC storage failures are surfaced through storage contracts rather than guessed from executor-local state.

### Shuffle loss and migration
- [ ] Shuffle locations are resolved dynamically through the registry for every fetch attempt; reducers never pin a stale worker location.
- [ ] Fetch failure retries by re-resolving current holders and respects bounded retry, cancellation, and backoff policy.
- [ ] Registry state records shuffleId, mapId, partitionId, holder generation, replicas, health, drain state, and expiry.
- [ ] Drain-migration preserves block identity, checksum, length, generation, and reference counts before old holders are removed.
- [ ] Configurable eager replication is validated for sudden pod, worker, node, and spot loss.
- [ ] Object-store fallback, when enabled, is treated as a durability tier with explicit consistency and cost behavior.
- [ ] Shuffle block cleanup cannot delete data needed by active reduce attempts or retried stages.
- [ ] Tests simulate worker loss, registry failover, stale holders, partial migration, duplicate replicas, checksum mismatch, and re-resolve races.

### Executor, pod, and driver recovery
- [ ] Executor pod loss marks in-flight task attempts failed without marking their outputs committed unless commit acknowledgment is durable.
- [ ] Retried tasks are idempotent for shuffle writes, result reporting, connector sink writes, and Delta commit messages.
- [ ] Cancellation propagates from action to driver, scheduler, executor, storage readers, shuffle fetches, and connector writers.
- [ ] Driver restart behavior is specified for submitted jobs, pinned snapshots, streaming offsets, in-flight commits, and shuffle registry state.
- [ ] Kubernetes SIGTERM, preStop, readiness removal, shutdown timeout, and task drain are testable state transitions.
- [ ] Speculative execution cannot double-publish shuffle blocks, sink writes, Delta commits, or accumulator-like metrics.
- [ ] Heartbeat timeouts distinguish slow, lost, draining, canceled, and decommissioned executors.

### Correctness oracles
- [ ] Every distributed feature has a mechanical oracle: Spark/SQL differential results, model-based state machine, replay equivalence, or invariant checker.
- [ ] Randomized plan tests capture seed, schema, data, partitioning, backend, storage trace, and expected output.
- [ ] Fuzzing covers logical plans, physical plans, expressions, partition specs, shuffle schedules, connector options, Delta actions, and Parquet metadata.
- [ ] Deterministic simulation can inject scheduler, clock, retry, storage, network, executor, shuffle, and commit failures.
- [ ] Jepsen-style histories check Delta version monotonicity, snapshot isolation, read-your-writes, idempotent publication, and illegal anomalies.
- [ ] Wrong-result failures become permanent regression tests with minimized inputs and reproduction commands.
- [ ] Integration tests in 04b cover cross-process, storage-backend, Kubernetes, and chaos scenarios that unit tests cannot prove.

### Observability for correctness
- [ ] Logs, metrics, and traces correlate job, stage, task, attempt, executor, partition, shuffle block, snapshot version, and commit version.
- [ ] Correctness-relevant state transitions are auditable: task scheduled, task canceled, block registered, block migrated, commit attempted, commit acknowledged.
- [ ] Retry exhaustion includes the violated invariant or unavailable dependency, not only a generic timeout.
- [ ] Metrics distinguish duplicate attempts from duplicate committed outputs.
- [ ] Observability does not leak credentials, row data, or tenant-isolated metadata.

## Anti-patterns (red flags)
- A transformation that reads data, schedules tasks, or commits output.
- A shuffle reader that caches a worker endpoint and never re-resolves after failure.
- Retrying tasks or writes without idempotency, attempt IDs, or duplicate-output protection.
- Assuming partitioning, ordering, or uniqueness because a prior operator usually creates it.
- Treating chaos tests as passed because no process crashed while row correctness was not checked.
- Letting compiled/codegen execution diverge from the interpreter on nulls, NaNs, decimals, errors, or ordering contracts.
- Weakening Delta conflict detection or snapshot isolation to recover from executor failure.
- Deleting shuffle or Delta data while active attempts, retained snapshots, or retries may still need it.

## References
- [ADR-0001: Execution strategy](../../adr/0001-execution-strategy.md).
- [ADR-0004: Shuffle architecture](../../adr/0004-shuffle-architecture.md).
- [ADR-0006: Scheduler, AQE, and CBO](../../adr/0006-scheduler-aqe-cbo.md).
- [17 — Delta Storage Format Checklist](17-delta-storage-format-checklist.md); [19 — Data Source Connectors Checklist](19-data-source-connectors-checklist.md).
- [04b — Integration Testing Checklist](04b-integration-testing-checklist.md).
- Spark scheduler/shuffle semantics, Delta isolation semantics, Jepsen-style consistency testing, and deterministic simulation practices.
