# 01 — Architecture Checklist

> **Scope:** Repository-wide architecture, project structure, service boundaries, engine topology, foundational interfaces, ADRs, and cross-layer designs.
> **Priority:** STANDARD.
> **Owners:** cloud-native-distributed-systems-architect, query-execution-engine-engineer. **Grounded in:** `.github/copilot-instructions.md`, `docs/engineering/design/engine-architecture.md`, ADR-0001, ADR-0002, ADR-0006, ADR-0007, ADR-0008, ADR-0012.

## How to use
Use this checklist when reviewing architecture docs, foundational interfaces, new subsystems, and changes that alter DeltaSharp's layer boundaries. Treat ADRs as the source of truth; if a new foundational decision is needed, require a new ADR before accepting the design.

## Checklist
### Layered engine invariants
- [ ] Public APIs (`SparkSession`, `DataFrame`, `Dataset<T>`, `Column`, functions, SQL) only build plans and never perform scans, scheduling, writes, or execution during transformations.
- [ ] Transformations are lazy and actions are eager; every new API or operator identifies which side of the boundary it is on.
- [ ] The design preserves the required pipeline: API/SQL -> unresolved logical plan -> analyzer/optimizer -> physical plan -> execution.
- [ ] Each layer exposes only the minimum contracts needed by the next layer; API code does not reference executor, shuffle, storage transport, or Kubernetes internals.
- [ ] Logical, analyzed, optimized, physical, and adaptive plan concepts are named distinctly so reviewers can detect boundary leaks.
- [ ] SQL and DataFrame/Dataset routes converge on shared plan and expression nodes rather than forking into separate engines.
- [ ] Write paths are actions and pass through planning/execution before coordinating with Delta commit ownership.
- [ ] `EXPLAIN` can describe the layer boundary affected by the design, not only final execution details.

### Architecture pattern: swappable implementations
- [ ] New subsystems follow the project pattern of abstractions with swappable implementations, as summarized in `engine-architecture.md`.
- [ ] Execution backend decisions preserve interpreter <-> codegen substitutability from ADR-0001.
- [ ] Columnar compute binds to `ColumnBatch`/`ColumnVector`, allowing Arrow-backed vectors now and custom vectors later per ADR-0002.
- [ ] Transport choices remain behind interfaces so Arrow Flight can be swapped with raw Pipelines or other data-plane implementations when justified.
- [ ] Shuffle/storage abstractions allow node-local workers first and object-store fallback later without changing query semantics.
- [ ] Interface boundaries state semantic obligations, failure modes, metrics, cancellation, and tenant scoping, not just method signatures.
- [ ] Implementations can be tested against shared contract suites and parity oracles.
- [ ] The default implementation is pragmatic and durable; optional fast paths never become correctness dependencies.

### Control plane vs data plane
- [ ] Kubernetes Operator, CRDs, admission, reconciliation, driver/executor lifecycle, and status conditions remain control-plane responsibilities.
- [ ] Driver RPC, executor task execution, shuffle transfer, object-store/PVC access, and Delta data movement remain data-plane responsibilities.
- [ ] The Operator is not placed in per-task hot paths or used as a data-transfer proxy.
- [ ] Control-plane state machines are idempotent, observable, and safe under retries, restarts, and partial cleanup.
- [ ] Data-plane protocols define backpressure, cancellation, retry, idempotency, and duplicate-attempt behavior.
- [ ] Service contracts make it clear which component owns driver recovery, executor loss, shuffle loss, and Delta commit fencing.
- [ ] Plan serialization over the control plane uses the ADR-0012 protobuf-defined physical plan boundary.
- [ ] Control-plane and data-plane metrics can be correlated without leaking tenant data.

### Multi-tenant isolation by design
- [ ] Tenant identity flows through catalog resolution, plan caches, scheduler pools, execution metrics, storage credentials, shuffle locations, and logs.
- [ ] Shared caches, statistics, file indexes, broadcast relations, and shuffle metadata are scoped or keyed to prevent cross-tenant leakage.
- [ ] Resource controls cover driver memory, executor memory, task concurrency, scan bytes, shuffle bytes, object-store requests, and queue fairness.
- [ ] Fair scheduling and resource pools align with ADR-0006 and cannot be bypassed by API, SQL, or connector entry points.
- [ ] Storage abstractions keep S3, ADLS, GCS, and PVC credentials separate per tenant and per execution context.
- [ ] Error messages, diagnostics, `EXPLAIN`, logs, metrics, and traces avoid exposing another tenant's object paths, schemas, values, or credentials.
- [ ] Architecture reviews include noisy-neighbor, quota exhaustion, cancellation, and preemption scenarios.
- [ ] Tenant isolation violations are treated as Critical findings under the review rubric.

### Reliability, security, observability, and cost trade-offs
- [ ] Designs state expected failure modes: driver loss, executor loss, duplicate task attempts, storage throttling, shuffle fetch failure, network partition, and commit race.
- [ ] Reliability mechanisms define retry bounds, idempotency, replay, rollback, drain, and recovery ownership.
- [ ] Security boundaries include service accounts, IAM/storage credentials, network policy, image provenance, catalog authorization, and secret handling.
- [ ] Observability includes logs, metrics, traces, plan events, task events, and status conditions with stable names and tenant-safe dimensions.
- [ ] Cost impacts are explicit for executor time, scan bytes, shuffle bytes, spill, object-store operations, PVC usage, replication, and recompute.
- [ ] Trade-offs are documented where performance optimizations increase reliability, security, operability, or cost risk.
- [ ] Critical domains from the review rubric are considered: Spark semantics, Catalyst correctness, Delta ACID, shuffle correctness, tenant isolation, and .NET runtime safety.
- [ ] Architecture changes include validation evidence or a test plan that can prove the intended invariants.

### ADR governance and ownership
- [ ] Existing ADRs are cited for decisions already made and are not re-litigated in implementation docs.
- [ ] Any new foundational decision, reversal, or cross-cutting trade-off is captured in a new ADR before implementation hardens around it.
- [ ] Ownership is assigned to the correct persona seat and handoff boundaries are explicit.
- [ ] Open decisions are tracked separately from accepted decisions and do not masquerade as settled architecture.
- [ ] Architecture diagrams and decision ledgers are updated when accepted ADRs change the system shape.
- [ ] Cross-link related checklists when applicable, especially [02](02-engine-implementation-checklist.md) for engine component design and [14](14-tenant-isolation-checklist.md) for isolation.

## Anti-patterns (red flags)
- API methods that read data, schedule tasks, or call storage during transformations.
- A subsystem tied directly to one implementation where the ADR requires a swappable abstraction.
- Operator or CRD reconciliation logic embedded in executor hot paths.
- Shared caches, stats, logs, or metrics without tenant scoping.
- Architecture diagrams that omit failure, security, observability, or cost consequences.
- Foundational decisions buried in PR comments instead of ADRs.
- SQL, DataFrame, and Dataset paths that implement separate semantics for the same operation.
- Designs that optimize happy-path throughput while ignoring shuffle, commit, retry, or cancellation correctness.

## References
- [DeltaSharp Copilot Instructions](../../../.github/copilot-instructions.md)
- [Engine Architecture Overview](../design/engine-architecture.md)
- [ADR-0001: Execution strategy](../../adr/0001-execution-strategy.md)
- [ADR-0002: In-memory columnar batch format](../../adr/0002-columnar-batch-format.md)
- [ADR-0006: Scheduler, Adaptive Query Execution, and cost-based optimization](../../adr/0006-scheduler-aqe-cbo.md)
- [ADR-0007: SQL frontend — parser and dialect](../../adr/0007-sql-frontend.md)
- [ADR-0008: Type system and internal row/value representation](../../adr/0008-type-system-row-format.md)
- [ADR-0012: Plan serialization](../../adr/0012-plan-serialization.md)
- [Review PR rating rubric](../../../.github/skills/review-pr/rating-rubric.md)
- [02 — Engine Implementation Checklist](02-engine-implementation-checklist.md)
