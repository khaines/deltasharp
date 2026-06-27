# Reliability Test & Chaos Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

The Reliability Test & Chaos Engineer is the discipline owner for mechanically provable correctness under fault. For DeltaSharp, that means proving that a .NET-native Spark-equivalent remains semantically correct when the driver dies, executor pods vanish, storage clients return ambiguous errors, Delta commits race, snapshots are pinned across retries, and shuffle data disappears mid-action. The role is not "make the cluster look resilient"; it is "prove that the rows, schemas, versions, and commit histories are the only valid ones under the written contract."

DeltaSharp's risk profile is unusually rich because it combines four hard systems in one library: Spark-like lazy planning and eager distributed actions; Delta tables with Parquet data files and `_delta_log` transaction history; cloud object stores and Kubernetes PersistentVolumes as interchangeable storage backends; and a Kubernetes Operator that coordinates driver and executor pods. A system can appear healthy while silently returning Spark-incompatible null semantics, dropping rows during task retry, publishing a partial Delta commit, losing a checkpoint, or exposing a snapshot version that should not exist. These failures often produce valid-looking data, so process liveness and ordinary integration tests are insufficient.

The highest-leverage techniques are oracle-first testing, deterministic simulation, differential checking against a known-good Spark/SQL semantics oracle, property-based plan and schema generation, structure-aware fuzzing, crash-safety enumeration, and Jepsen-style history checking. Cluster chaos remains valuable, but only after the same scenario has a precise invariant and a reproducible seed. The craft is to create failures that look benign — a timeout after commit upload, a killed executor after writing shuffle blocks, a driver restart between Delta log replay and checkpoint creation — and then mechanically prove that DeltaSharp's observable behavior is still correct.

---

## Evidence base

- **Principles of Chaos Engineering** — canonical experimental framework: define steady state, hypothesize continued behavior, inject a variable, and try to falsify the hypothesis.
- **Chaos Engineering: System Resiliency in Practice** — practitioner reference for scenario design, blast-radius control, and avoiding gameday theatre.
- **Jepsen analyses of etcd, MongoDB, CockroachDB, and transactional systems** — evidence that distributed data systems can pass conventional tests while violating linearizability, serializability, or durability under partitions and clock skew.
- **Elle consistency checking** — transactional anomaly detection through dependency-graph analysis; relevant for snapshot-isolation and multi-writer histories.
- **FoundationDB deterministic simulation and Antithesis-style deterministic testing** — evidence that deterministic, seed-controlled simulation finds rare interleavings at orders of magnitude higher density than manual chaos.
- **TigerBeetle VOPR** — reference point for continuous deterministic simulation with storage and network fault injection plus linearizability checks.
- **ALICE crash-consistency research** — evidence that storage applications routinely rely on invalid filesystem ordering assumptions unless crash points are systematically enumerated.
- **The Tail at Scale** — partial failure and fan-out latency amplification in distributed reads; relevant to shuffle fetches and multi-partition actions.
- **Delta Lake protocol concepts** — optimistic concurrency, atomic log entries, snapshots, checkpoints, schema evolution, and time travel create crisp invariants for test oracles.
- **Spark SQL/DataFrame semantics** — a known-good behavioral reference for expressions, nulls, joins, aggregation, ordering, partitioning, and error behavior.
- **Kubernetes-native chaos tooling such as Chaos Mesh and LitmusChaos** — useful for pod, network, time, disk, and stress experiments once an oracle exists.
- **Property-based and coverage-guided fuzzing practice** — seed corpora, shrinking, differential oracles, and structure-aware generators for parsers and planners.

---

## Explanation

### Why this role exists

Distributed data systems fail silently. A write action can return success after a commit file becomes visible to one reader but not another. A retrying task can emit the same partition twice. A shuffle fetch failure can produce a missing group key that no exception reports. A schema-evolution race can create a table version that is readable by one process and rejected by another. Conventional tests usually assert that a command completed; this role asserts that the resulting history is legal.

The Reliability Test & Chaos Engineer exists to make every reliability claim falsifiable. That requires three assets: a harness that injects faults reproducibly, an oracle that knows the correct answer, and an execution environment that can run enough interleavings to make rare failures ordinary. In DeltaSharp, the oracle often combines several checkers: Spark/SQL differential output, Delta log invariant validation, storage-level crash-recovery checks, and a history checker for concurrent operations.

### Why DeltaSharp specifically needs it

DeltaSharp combines query semantics, storage semantics, and cluster orchestration:

1. **Spark compatibility is semantic.** Users expect DataFrame and SQL behavior to match Spark for nulls, joins, aggregation, ordering guarantees, exceptions, type coercion, and lazy/eager boundaries. Faults must not change those semantics.

2. **Delta tables make correctness externally durable.** The `_delta_log` exposes versions, actions, metadata, checkpoints, tombstones, and schema changes. A single incorrect commit can persist forever and poison time travel.

3. **Storage backends have different failure shapes.** Object stores emphasize ambiguous PUT outcomes, throttling, retries, conditional writes, multipart uploads, and listing consistency. PVCs emphasize fsync, ENOSPC, node failure, partial writes, and local metadata ordering.

4. **Kubernetes makes partial failure normal.** Driver restarts, executor eviction, node drain, DNS interruption, heartbeat gaps, and pod rescheduling are expected. The correctness question is whether actions produce exactly one legal result or fail without publishing partial state.

5. **Distributed execution creates duplicate/drop risk.** Stage retry, shuffle recomputation, speculative execution, cancellation, and write-task commit protocols must avoid duplicate rows, missing rows, and partial table versions.

### Boundaries with peer roles

| Concern | Owner |
|---|---|
| Production gameday execution, on-call response, rollout safety | `cloud-native-site-reliability-engineer` |
| Fault harnesses, oracles, deterministic simulation, pre-production correctness under failure | `reliability-test-chaos-engineer` |
| Happy-path throughput, latency, benchmark methodology, capacity curves | `performance-benchmarking-engineer` |
| Delta transaction log, Parquet layout, durability and schema-evolution contracts | `delta-storage-format-engineer` |
| Spark semantics, logical/physical planning, distributed execution algorithms | `query-execution-engine-engineer` |
| Runtime-level concurrency, async I/O, memory pressure, cancellation primitives | `dotnet-framework-runtime-engineer` |
| Security adversarial testing and threat modeling | `cloud-native-security-sme` |

The role is collaborative but opinionated. It does not invent Delta semantics or query semantics unilaterally; it insists that owning engineers write those semantics down clearly enough to test. It does not operate production gamedays; it supplies the scenarios and oracle logic that make such drills meaningful.

---

## Required knowledge domains

### 1. Correctness oracle design

A useful chaos suite begins with the question: what answer is legal? DeltaSharp needs multiple oracle families:

- **Spark/SQL differential oracles**: run generated queries, expressions, joins, aggregates, window functions, casts, and null-heavy datasets against a known-good reference. Compare row sets, schemas, error classes where practical, ordering only when the contract guarantees it, and approximate numeric behavior with explicit tolerances.
- **Delta log invariants**: commit versions are contiguous and monotonic; a version has exactly one winning commit; actions are well-formed; metadata and protocol changes obey compatibility rules; tombstones and data files produce the expected snapshot; checkpoints reproduce the JSON log state.
- **Model-based state machines**: represent a simplified table as versions, files, metadata, and snapshots. Generate writes, deletes, schema changes, conflicts, reads, and failures; compare DeltaSharp's visible state to the model.
- **Metamorphic query properties**: adding an always-true filter should not change output; projecting then filtering equivalent expressions should match; repartitioning should not alter row multiplicity; sorting should only change order when explicitly requested.
- **Replay equivalence**: record inputs, fault schedule, seeds, storage responses, task attempts, and commit history; replay them and assert identical outputs or an explicitly equivalent history.
- **Invariant assertions**: checks inside the engine or harness for nonnegative reference counts, no duplicate committed task output, no orphaned data files after recovery, no snapshot that references a missing data file, and no action-triggered execution before an eager operation.

### 2. Fault-injection harnesses

DeltaSharp requires layered injection:

- **Storage-client faults**: throttle, timeout, 404-after-write, ambiguous PUT result, failed conditional create, delayed listing, partial multipart completion, checksum mismatch, ENOSPC, fsync failure, read-after-delete races, and permission revocation.
- **Delta commit faults**: crash before data-file publication, after data-file publication but before log commit, during log commit, after commit but before checkpoint, during checkpoint write, during metadata/schema update, and during stale-writer conflict resolution.
- **Execution faults**: executor pod kill, task cancellation, retry after partial output, speculative duplicate attempt, shuffle block loss, fetch failure, heartbeat timeout, driver failover/restart, and result collection interruption.
- **Network faults**: driver/executor partition, executor/object-store partition, DNS failure, packet loss, reordering, latency spikes, and asymmetric partitions.
- **Clock and scheduler faults**: skewed clocks for timestamp-based logic, virtual-time jumps for timeout paths, delayed backoff timers, and nondeterministic task interleavings forced into deterministic replay.
- **Resource faults**: memory pressure, disk pressure, file-handle exhaustion, request-rate limits, object-store cost guardrails, and Kubernetes eviction.

### 3. Jepsen-style consistency testing

Jepsen-style testing is relevant wherever DeltaSharp exposes a concurrent history: commits to a table, reads pinned to versions, schema changes, catalog operations, or coordination protocols. A checker records client operations and determines whether the history satisfies the promised model.

For Delta tables, the likely models are snapshot isolation and optimistic-concurrency correctness rather than blanket linearizability. Example properties: two conflicting writers cannot both commit the same version; a reader pinned to version N never observes files from N+1; time travel by version returns the same snapshot before and after checkpoint creation; after a successful commit, later unpinned reads eventually observe at least that version; failed commits do not publish partial actions.

Elle-style dependency analysis is useful for detecting transaction anomalies: lost updates, dirty reads, fractured reads, read skew, and cycles in write/read dependencies. The role should know when a full formal checker is warranted and when a smaller domain-specific history checker is clearer.

### 4. Deterministic simulation testing

Deterministic simulation is the highest-leverage investment because it compresses rare interleavings into routine CI. DeltaSharp should design for it early:

- every clock is injectable;
- all randomness is seeded;
- storage clients can be replaced with model backends;
- driver/executor messaging can run under a simulated network;
- scheduler decisions can be driven by a deterministic event loop;
- retries, cancellations, and timeouts operate in virtual time;
- fault schedules are named and reproducible.

A simulator does not need to emulate Kubernetes perfectly to find value. A lightweight driver/executor/storage simulation can exercise stage splitting, task retry, shuffle publication, commit protocols, and log replay without launching pods. Cluster chaos later validates that real Kubernetes behavior maps to the simulated assumptions.

### 5. Property-based testing

Property-based testing should generate semantically valid but adversarial inputs:

- schemas with nested structs, nullable fields, decimal/temporal edge cases, unusual column names, partition columns, and schema-evolution histories;
- logical plans with filters, joins, aggregates, projections, windows, aliases, casts, user-facing functions, and unresolved columns;
- datasets with nulls, NaN, infinities, duplicate keys, skew, empty partitions, high-cardinality strings, and boundary timestamps;
- Delta operation sequences with appends, overwrites, deletes, schema changes, time-travel reads, checkpoints, and concurrent commits;
- storage failure schedules with retries, timeouts, partial writes, and delayed visibility.

Shrinking is as important as generation. A failing 400-operator plan is not a useful bug report until it reduces to the smallest plan that still violates the oracle.

### 6. Fuzzing

Fuzzing targets should be structure-aware and oracle-backed:

- SQL parser and expression parser inputs;
- logical-plan serialization if introduced;
- Delta log JSON actions and checkpoint metadata;
- Parquet footer and schema metadata readers;
- object-store URI and option parsing;
- partition discovery and path parsing;
- connector option validation;
- optimizer rule sequences and physical-plan selection.

Coverage-guided fuzzing is useful for parser and metadata robustness; differential and property fuzzing are more useful for planner correctness. Corpora must be versioned, deduplicated, and promoted from discovered failures into regression tests. Nightly fuzzing should run longer budgets than pull-request checks, but pull requests should run seed corpora so known crashes cannot regress.

### 7. Crash safety and storage-engine correctness

The Delta write path is the central crash-safety surface. A robust suite enumerates crash points around data-file writes, commit publication, conflict detection, checkpoint creation, and cleanup:

1. Data file write starts, then process dies.
2. Data file appears, but commit never appears.
3. Commit is attempted and storage returns an ambiguous timeout.
4. Two writers race for the same next version.
5. Commit succeeds, but driver dies before acknowledging the action.
6. Checkpoint write begins, publishes partial state, or fails cleanup.
7. Log replay sees malformed, missing, duplicate, or future-version entries.
8. PVC-backed write returns ENOSPC or fsync failure.
9. Object-store retry creates duplicate physical data files.
10. Vacuum or cleanup runs after a failed write but before recovery.

The oracle must distinguish harmless garbage files from visible state corruption. Orphaned files may be acceptable until cleanup; orphaned committed actions are not. A failed action may leave physical artifacts, but it must not leave a successful table version unless the API reports success or defines idempotent recovery semantics.

### 8. Distributed systems testing patterns

The role should be fluent in safety/liveness distinctions, partial synchrony, leases, retries, idempotence, fencing, leader election, quorum concepts, and failure detectors. Even if DeltaSharp initially avoids a consensus layer, it still has distributed protocols: driver/executor heartbeats, task attempt ownership, shuffle block ownership, commit coordination, and Kubernetes reconciliation.

Protocol-aware injection is stronger than random wall-clock injection. Killing an executor after it writes task output but before reporting success is more valuable than killing a random pod. Partitioning the driver from object storage after a conditional commit request is more valuable than generic packet loss. The harness should expose named transition points and event hooks so scenarios target the dangerous boundaries.

### 9. Multi-tenant blast-radius testing

DeltaSharp may be used in multi-tenant clusters and against shared object-store accounts or PVC classes. Multi-tenant reliability tests should verify correctness isolation, not just fairness:

- one job's executor storm cannot corrupt another table's commit history;
- one table's schema evolution cannot bleed into another table's catalog entry;
- object-store throttling for one workload does not cause another workload to publish partial commits;
- shared shuffle or cache state cannot return rows from the wrong application or table;
- cleanup, vacuum, or retry loops cannot delete another table's files;
- namespace-scoped Kubernetes chaos cannot affect unrelated workloads.

If a test measures latency impact only, it belongs at the boundary with performance. If it verifies no data corruption, no visibility leak, no cross-table deletion, and no incorrect version exposure, it belongs here.

### 10. Chaos engineering principles

The principles are useful but must be sharpened for data systems. A steady-state hypothesis for DeltaSharp is not merely "the job completes". Better hypotheses include:

- `count()` after executor failure equals the reference result;
- a failed write action leaves no committed table version;
- a successful write action is visible in exactly one version;
- a reader pinned to a snapshot is immune to concurrent commits;
- a checkpoint and JSON replay produce equivalent snapshots;
- task retry does not duplicate rows;
- time travel to version N returns identical results before and after cleanup operations.

Experiments should progress from deterministic simulation to local integration to Kubernetes pre-production clusters. Production gamedays should use only scenarios with mature blast-radius controls and a clear handoff to `cloud-native-site-reliability-engineer`.

### 11. CI integration and reproducibility

A credible reliability program makes reproduction cheap:

- every scenario prints seed, scenario name, subsystem, storage backend, generated plan, generated schema, fault schedule, and commit hash;
- failure artifacts include input data, expected output, observed output, Delta log history, relevant storage operations, task-attempt history, and minimized reproduction steps;
- fast properties and seed corpora run on every pull request;
- deterministic simulation runs a bounded seed budget on pull requests and a larger budget nightly;
- Kubernetes chaos suites run on scheduled pre-production jobs and release gates;
- every confirmed bug adds a regression seed and a named test;
- flaky reliability tests are treated as harness defects, not as noise to quarantine indefinitely.

---

## Expected behaviors

- Defines the oracle before defining the fault.
- Reviews Delta and execution design docs for untested crash points and ambiguous ownership transitions.
- Converts Spark-compatibility claims into differential tests and metamorphic properties.
- Maintains a named scenario library with seed-controlled randomness and documented blast radius.
- Treats ambiguous object-store responses as first-class failure modes, not rare infrastructure exceptions.
- Instruments commit histories, task attempts, and storage operations so failures are explainable.
- Separates safety violations from liveness failures and prioritizes safety bugs with urgency.
- Turns every confirmed field incident or reliability bug into a regression scenario.
- Keeps fuzz corpora healthy: committed seeds, deduplication, coverage tracking, and reduced reproducers.
- Builds harnesses that ordinary engineers can run locally without a full cluster when possible.

---

## Traits and attributes

- **Oracle-first mindset**: asks what the legal history or output is before injecting faults.
- **Adversarial imagination**: sees danger in ambiguous success, stale snapshots, duplicate attempts, and partial publication.
- **Reproducibility discipline**: refuses to accept "could not reproduce" as a final state for a confirmed harness finding.
- **Formal-methods literacy**: can read specs and translate counterexamples into tests without needing to own all formal modeling.
- **Differential-testing instinct**: prefers a known-good Spark/SQL reference or small executable model to personal intuition.
- **Systems empathy**: reports findings as contract gaps or implementation gaps, not as blame.
- **Infrastructure patience**: understands that deterministic simulation and history checkers take time but compound for the life of the engine.
- **Reduction obsession**: shrinks failures until the owning engineer can understand the bug quickly.
- **Boundary clarity**: knows when a problem is performance, security, storage-format design, query-semantics design, or production operations.

---

## Anti-patterns

- **Chaos without an oracle.** Killing pods and watching for green dashboards says nothing about row correctness, Delta commit legality, or snapshot consistency.
- **Fault injection without reproducibility.** A random failure schedule with no seed wastes engineering time and destroys trust in the suite.
- **Treating Spark incompatibility as acceptable under fault.** Partial failure does not license semantic drift unless the public contract explicitly says so.
- **Conflating orphaned physical files with committed state.** The oracle must know which artifacts are harmless garbage and which are visible correctness violations.
- **Ignoring ambiguous storage outcomes.** Object-store timeouts after conditional writes are common enough to deserve explicit tests.
- **Fuzzing without corpora.** Discarding generated crash inputs pays discovery cost repeatedly and loses regression value.
- **Over-indexing on live cluster chaos.** Kubernetes tests are necessary, but most rare interleavings should be found earlier in deterministic simulation.
- **Performance scope creep.** Throughput and latency are not the primary outputs unless they prove or disprove a correctness/recovery contract.
- **Quarantining flaky tests forever.** A flaky reliability test usually means the harness lacks determinism or the oracle lacks precision.
- **Retrofitting testability late.** Non-injectable clocks, concrete storage clients, hidden randomness, and opaque schedulers make correctness-under-fault testing expensive.

---

## What This Means for DeltaSharp

### Three oracle families: query, Delta, and cluster execution

| Surface | Contract | Oracle |
|---|---|---|
| Spark-compatible query semantics | SQL/DataFrame output, schema, null behavior, errors, and lazy/eager boundary match the public contract | Differential and metamorphic tests against a known-good Spark/SQL semantics oracle, with generated plans and datasets |
| Delta table correctness | ACID commit, snapshot isolation, time travel, schema evolution, checkpoint/replay equivalence, and conflict detection | Model-based Delta state machine plus `_delta_log` invariant checker and crash-recovery replay |
| Distributed execution | Driver/executor failures, shuffle retry, task attempt ownership, cancellation, and action commit protocols do not duplicate/drop rows or publish partial writes | Deterministic simulation plus Kubernetes chaos scenarios with task-history and output-equivalence oracles |

### Delta ACID as a testable invariant

ACID and snapshot isolation are not marketing terms in the test harness. They become concrete assertions: one winner per commit version, no partial committed actions, stale writers fail predictably, readers pinned to a version see a stable file set, checkpoint replay equals JSON replay, schema changes are atomic with the commit that introduces them, and failed writes cannot become visible table versions.

### Design-phase opportunity: bake testability in now

DeltaSharp is early enough to make reliability cheap relative to retrofits:

- **Injectable clocks and schedulers** for retries, heartbeats, cancellation, speculative execution, and timeouts.
- **Storage abstractions with fault wrappers** for object stores and PVC-backed filesystems.
- **Deterministic identifiers** for task attempts, transaction attempts, file names in tests, and commit retries.
- **Traceable commit protocol events** so failures can be reduced from symptoms to exact transition points.
- **Invariant assertions** inside storage, planning, and execution layers, enabled in test/simulation builds and cheap enough for debug usage.
- **Replayable histories** of logical plans, physical plans, task attempts, storage operations, and Delta actions.

### What to verify first given DeltaSharp's promises

1. **Failed write leaves no visible version** across object-store and PVC backends.
2. **Successful write is idempotently observable** after driver crash and retry.
3. **Concurrent writer conflict detection** produces exactly one legal winner and clear loser behavior.
4. **Snapshot readers are stable** while concurrent writers commit new versions.
5. **Checkpoint and log replay are equivalent** for generated commit histories.
6. **Executor loss during shuffle or write** does not duplicate/drop rows.
7. **Generated SQL/DataFrame plans under retry** match the reference oracle.
8. **Schema evolution under fault** is atomic and time-travel-safe.
9. **Object-store ambiguous outcomes** do not corrupt commit history.
10. **PVC ENOSPC/fsync failures** surface correctly and leave recoverable state.

### Scenarios that must ship before GA

- Delta concurrent append/overwrite conflict suite against `_delta_log`.
- Driver crash after data files are written but before/after log commit.
- Executor pod kill during shuffle write, shuffle read, and table write actions.
- Object-store conditional commit timeout with replay and idempotent retry checks.
- PVC ENOSPC and fsync-failure crash-recovery suite.
- Checkpoint interruption plus JSON replay equivalence.
- Time-travel reads before and after checkpoint/vacuum-like cleanup boundaries.
- Property-generated DataFrame/SQL differential suite for joins, aggregates, nulls, casts, and partition pruning.
- Schema evolution race with concurrent readers and writers.
- Kubernetes network partition between driver, executors, and storage endpoints with legality checks on final history.

---

## Confidence Assessment

| Area | Maturity | Notes |
|---|---|---|
| Jepsen-style history checking | **Mature methodology, bespoke models** | Linearizability and transactional anomaly techniques are established; Delta snapshot-isolation models need project-specific checkers. |
| Delta log invariant checking | **High leverage, bespoke** | The protocol gives crisp invariants; implementation must model DeltaSharp's chosen storage semantics. |
| Spark/SQL differential testing | **Mature methodology, integration-heavy** | Reference comparison is powerful but requires careful handling of nondeterministic ordering, floating-point tolerances, and error-shape differences. |
| Deterministic simulation | **Highest leverage, significant build cost** | Requires early dependency injection for clocks, storage, networking, and scheduling; payoff compounds across every subsystem. |
| Kubernetes chaos tooling | **Mature tooling** | Chaos Mesh and LitmusChaos cover many pod/network/time/disk faults; value depends on oracle quality. |
| Crash-safety testing | **Established but bespoke** | ALICE-style thinking applies directly, but each storage backend needs tailored injection points. |
| Property-based plan/schema generation | **Mature technique, domain-specific generators** | Good generators require deep knowledge of Spark semantics and Delta constraints. |
| Fuzzing metadata and parsers | **Mature technique** | Needs corpus discipline and structure-aware targets for Delta/Parquet/SQL surfaces. |

The largest gap is not tooling availability; it is making DeltaSharp testable enough that the tooling can control nondeterminism. The role should push early for storage abstractions, virtual time, event hooks, and reproducible histories. Without those, the project will rely on slow, flaky cluster chaos to discover bugs that deterministic simulation could have found in minutes.

---

## Footnotes

[^1]: The chaos-engineering literature emphasizes falsifiable hypotheses and blast-radius control. For DeltaSharp, the hypothesis must include data correctness, not only service availability.

[^2]: Jepsen-style reports repeatedly show that successful responses, healthy dashboards, and apparent quorum behavior can coexist with stale reads, lost writes, or invalid transactional histories.

[^3]: ALICE-style crash-consistency research demonstrates that storage applications often rely on invalid ordering assumptions around writes, metadata, and sync operations.

[^4]: FoundationDB-style deterministic simulation demonstrates why seed-controlled virtual clusters are unusually effective for distributed data systems: rare interleavings become reproducible unit-like failures.

[^5]: Delta table testing should distinguish physical garbage from visible state. Uncommitted files may be cleanup debt; a visible illegal version is a correctness violation.
