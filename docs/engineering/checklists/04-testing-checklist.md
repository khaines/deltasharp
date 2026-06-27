# 04 — Testing Checklist

> **Scope:** Test strategy, coverage expectations, deterministic validation, and regression policy across unit, integration, parity, storage, distributed, and operator tests.
> **Priority:** HIGH.
> **Owners:** reliability-test-chaos-engineer, query-execution-engine-engineer, delta-storage-format-engineer. **Grounded in:** build-test-config, review-pr rating rubric, `.github/copilot-instructions.md`, ADR-0001, ADR-0013.

## How to use
Use this checklist to decide whether the right kinds of tests exist before applying 04a or 04b. Missing tests for new behavior are High findings, and missing tests for storage, engine, operator, or tenant-isolation behavior can cap or block approval under the rubric.

## Checklist
### Test pyramid and commands
- [ ] Fast unit tests cover pure plan, expression, analyzer, optimizer, API, and helper behavior before slower integration tests are added.
- [ ] Integration tests cover storage, Delta, Parquet, shuffle, executor, operator, and connector seams that cannot be validated in isolation.
- [ ] Full validation is reproducible from the root with `dotnet restore`, `dotnet build -c Release`, `dotnet test`, and `dotnet format --verify-no-changes`.
- [ ] Focused iteration uses `dotnet test --filter "FullyQualifiedName~X"` or `dotnet test --filter "Name=Y"`, but merge evidence comes from the relevant full suite.
- [ ] Coverage collection uses `dotnet test --collect:"XPlat Code Coverage"` when configured.
- [ ] Long-running integration suites use the documented 900s timeout budget rather than weakening assertions.

### Required correctness domains
- [ ] Spark public API parity is tested for names, overload behavior, null behavior, SQL/DataFrame semantics, and documented .NET deviations.
- [ ] Lazy/eager behavior is tested so transformations only build plans and actions trigger execution.
- [ ] Catalyst-style analyzer and optimizer behavior is tested for name resolution, type coercion, predicate pushdown, column pruning, and semantic preservation.
- [ ] Physical planning tests verify required exchanges, partitioning, join strategies, ordering assumptions, and stage boundaries.
- [ ] Delta tests cover ACID commits, optimistic concurrency, transaction log actions, snapshots, checkpoints, idempotency, conflicts, time travel, and schema evolution.
- [ ] Parquet tests cover schema mapping, nullability, statistics, partition values, metadata, and compatibility.
- [ ] Shuffle and stage tests cover joins, aggregations, repartition/coalesce, retry, dynamic location resolution, and wrong-result risks.
- [ ] Kubernetes tests cover reconcile idempotency, finalizers, status, rollout/rollback, driver/executor lifecycle, and safe cleanup.
- [ ] Tenant-isolation tests cover plan analysis, catalogs, file listings, credentials, executors, logs, metrics, and traces.

### Differential and regression strategy
- [ ] ADR-0001 differential tests assert interpreter and optional compiled backend produce identical results for the same logical inputs.
- [ ] The vectorized interpreter is treated as the correctness oracle; compiled-code tests never redefine semantics.
- [ ] Randomized or property-based tests include deterministic seeds that are printed on failure.
- [ ] Golden tests are reviewed as semantic contracts and include intentional update procedures.
- [ ] Every bug fix adds a regression test that fails before the fix and passes after it.
- [ ] When tests fail, implementation is fixed before expectations are weakened unless the design doc or issue proves the test is wrong.
- [ ] Performance-sensitive changes add or update benchmark evidence rather than relying on wall-clock unit test timing.

### Coverage and quality targets
- [ ] Logical plan, analyzer, and optimizer rule coverage is at least 90% for changed areas.
- [ ] Delta transaction log and Parquet I/O coverage is at least 90% for changed areas.
- [ ] Public API surface coverage is at least 85% for changed areas.
- [ ] Operator, executor coordination, connector, and storage-backend coverage is at least 80% for changed areas.
- [ ] Coverage gaps are justified by risk, not by convenience, and paired with integration or parity evidence where appropriate.
- [ ] Test names describe the behavior and condition, not only the method under test.
- [ ] Tests avoid hidden dependencies on local paths, ambient cloud credentials, wall-clock time, test order, or machine culture.

### Determinism and diagnostics
- [ ] Assertions compare semantic results, schemas, plans, diagnostics, or effects that are stable across supported runtimes.
- [ ] Time-dependent behavior uses fake clocks, controlled schedulers, or bounded polling with clear failure messages.
- [ ] Concurrency tests expose cancellation, timeout, retry, and disposal behavior without depending on sleeps for correctness.
- [ ] Test fixtures clean up files, tables, namespaces, pods, object-store keys, and native resources deterministically.
- [ ] Failure messages identify the layer, query, seed, table version, partitioning, or operator under test.
- [ ] Flaky tests are quarantined only with owner, issue, and removal criteria; flakes are not normalized.

## Anti-patterns (red flags)
- New engine, storage, or operator behavior has only compile-time validation.
- A transformation test accidentally performs I/O or starts execution.
- Expected results are changed to match a bug instead of fixing implementation.
- Tests depend on real time, random seeds that are not logged, local machine paths, or ambient credentials.
- Interpreter and compiled backend are tested separately but never compared on the same data.
- Coverage excludes Delta commits, tenant isolation, shuffle correctness, or lazy/eager semantics.
- Integration failures are hidden by longer sleeps, broad retries, or disabled assertions.

## References
- [04a — Unit Testing Checklist](04a-unit-testing-checklist.md)
- [04b — Integration Testing Checklist](04b-integration-testing-checklist.md)
- [15 — Spark API Parity Checklist](15-spark-api-parity-checklist.md)
- [16 — Catalyst Planning Checklist](16-catalyst-planning-checklist.md)
- [17 — Delta Storage Format Checklist](17-delta-storage-format-checklist.md)
- [18 — Kubernetes Operator Checklist](18-kubernetes-operator-checklist.md)
- [21 — Distributed Correctness Checklist](21-distributed-correctness-checklist.md)
- `.github/skills/implement-work-item/build-test-config.md`
- `.github/skills/review-pr/rating-rubric.md`
- ADR-0001: Execution strategy
