# 04b — Integration Testing Checklist

> **Scope:** Cross-component tests for Delta/Parquet storage, object-store and PVC backends, driver/executor execution, shuffle, Kubernetes operator reconciliation, streaming micro-batches, and tenant isolation.
> **Priority:** HIGH.
> **Owners:** reliability-test-chaos-engineer, delta-storage-format-engineer, kubernetes-operator-engineer. **Grounded in:** build-test-config, review-pr rating rubric, `.github/copilot-instructions.md`, ADR-0001, ADR-0013.

## How to use
Run these tests when behavior crosses process, storage, filesystem, object-store, or Kubernetes boundaries. Keep them isolated and deterministic even when they use real emulators or test clusters; use the documented 900s integration timeout budget.

## Checklist
### Environment and command discipline
- [ ] Integration suites are runnable through `dotnet test` with documented filters or categories.
- [ ] Long-running suites use the `integration_test_timeout` budget of 900s instead of arbitrary sleeps or endless retries.
- [ ] Required emulators, PVCs, namespaces, ports, and credentials are provisioned by the test harness and not by a developer's machine state.
- [ ] Tests create unique table paths, object-store prefixes, namespaces, application names, and checkpoint locations per run.
- [ ] Cleanup removes objects, Delta tables, PVC data, namespaces, pods, CRDs, and local artifacts even after assertion failure.
- [ ] Parallel runs cannot collide through shared buckets, paths, ports, cluster namespaces, or static table names.

### Delta, Parquet, and storage integration
- [ ] End-to-end queries read and write real Parquet and Delta data through the same abstractions production uses.
- [ ] Delta commit tests cover optimistic concurrency, conflict detection, idempotent retry, checkpoints, snapshots, and transaction-log action ordering.
- [ ] Time-travel tests verify version-based and timestamp-based reads across multiple commits.
- [ ] Schema-evolution tests cover add, reorder, nullable, incompatible change, partition column, and metadata cases.
- [ ] Parquet tests validate physical schema, logical types, statistics, nullability, partition values, and Spark-compatible reads where applicable.
- [ ] Storage integration covers S3, ADLS, GCS, and PVC behavior through abstractions or supported local emulators.
- [ ] Object-store tests include list consistency assumptions, retryable failures, credential scoping, and path normalization.
- [ ] PVC tests include local disk spill/checkpoint behavior and cleanup after failed jobs.

### Query and distributed execution
- [ ] End-to-end DataFrame/SQL tests prove transformations stay lazy until an action triggers execution.
- [ ] Multi-executor tests verify shuffle correctness for joins, aggregations, repartition, coalesce, ordering-sensitive operators, and skewed partitions.
- [ ] Stage-boundary tests verify exchanges are inserted where required and omitted where unsafe or redundant.
- [ ] Retry tests cover executor loss, shuffle-location re-resolution, transient storage failures, and idempotent task reattempts.
- [ ] ADR-0001 parity integration tests compare interpreter and compiled backend results on representative queries when dynamic code is supported.
- [ ] Failure tests distinguish cancellation, timeout, task failure, storage failure, Delta conflict, and driver shutdown.
- [ ] Large-batch tests exercise off-heap memory, spill, disposal, and pod memory-limit behavior without relying only on unit mocks.

### Kubernetes operator and lifecycle
- [ ] Operator tests reconcile custom resources against envtest, kind, or an equivalent test cluster.
- [ ] Reconcile tests prove idempotency: repeated reconciliation does not duplicate drivers, executors, services, PVCs, or status events.
- [ ] Finalizer tests prove resources are cleaned up without deleting live unrelated workloads.
- [ ] Status tests verify phase transitions, conditions, error surfaces, progress, and terminal states.
- [ ] Rollout/rollback tests cover driver image changes, executor spec changes, failed pods, and restart policy behavior.
- [ ] RBAC and namespace tests verify the operator cannot cross tenant boundaries accidentally.
- [ ] Health/readiness tests prove driver and executor pods become observable before work is assigned.

### Streaming and exactly-once behavior
- [ ] Micro-batch tests cover checkpoint creation, restart from checkpoint, duplicate input, and idempotent sink commits.
- [ ] Exactly-once tests prove a failed micro-batch can retry without duplicate Delta commits or missing data.
- [ ] Watermark or trigger-time behavior uses controlled clocks and deterministic input sequences.
- [ ] Backpressure tests verify bounded buffering across source, planner, executor, shuffle, and sink boundaries.
- [ ] Streaming cleanup removes checkpoints, offsets, output tables, and temporary shuffle data.

### Tenant isolation and observability
- [ ] Integration tests prove storage credentials, catalogs, file listings, executors, pods, logs, metrics, and traces stay scoped to the tenant.
- [ ] Negative tests attempt cross-tenant table access, object-prefix traversal, namespace reuse, and credential reuse.
- [ ] Logs and metrics are asserted to include correlation identifiers but exclude secrets and row payloads.
- [ ] Distributed traces connect driver, executor, storage, shuffle, and operator spans for failed and successful jobs.
- [ ] Tests verify errors are actionable without exposing credentials or another tenant's paths.

## Anti-patterns (red flags)
- Integration tests require a contributor's personal cloud credentials or preexisting cluster resources.
- Tests share a fixed bucket prefix, namespace, table path, or checkpoint directory across runs.
- Longer sleeps are added to hide operator, storage, or shuffle races.
- Delta ACID, conflict, or exactly-once failures are treated as flaky infrastructure noise.
- Multi-executor tests validate job completion but not row-level correctness.
- Operator cleanup can delete resources outside the test namespace or tenant.
- Object-store behavior is mocked in a test labeled integration.

## References
- [04 — Testing Checklist](04-testing-checklist.md)
- [04a — Unit Testing Checklist](04a-unit-testing-checklist.md)
- [14 — Tenant Isolation Checklist](14-tenant-isolation-checklist.md)
- [17 — Delta Storage Format Checklist](17-delta-storage-format-checklist.md)
- [18 — Kubernetes Operator Checklist](18-kubernetes-operator-checklist.md)
- [19 — Data Source Connectors Checklist](19-data-source-connectors-checklist.md)
- [21 — Distributed Correctness Checklist](21-distributed-correctness-checklist.md)
- `.github/skills/implement-work-item/build-test-config.md`
- `.github/skills/review-pr/rating-rubric.md`
- ADR-0001: Execution strategy
- ADR-0013: Memory model for in-memory batches
