# EPIC-08: Distributed Runtime & Transport

- **Roadmap milestone:** M3 ([Milestone 3 — Distributed execution](../../../ROADMAP.md#milestone-3--distributed-execution-v0x))
- **Primary persona(s):** `dotnet-distributed-execution-engineer` (+ collaborators `query-execution-engine-engineer`, `dotnet-vectorized-columnar-compute-engineer`, `cloud-native-site-reliability-engineer`)
- **Related ADRs:** ADR-0003, ADR-0012
- **Depends on:** EPIC-03, EPIC-04
- **Status:** draft
- **Size:** XL

## Objective

Deliver the native .NET distributed runtime that turns planned work into driver/executor execution on Kubernetes. This epic establishes Generic Host process lifecycles, gRPC control traffic, Arrow Flight data exchange behind `IDataExchange`, protobuf plan/task serialization, executor scheduling, and graceful shutdown so DeltaSharp can run real multi-executor jobs while preserving lazy/eager engine semantics.

## Scope

**In scope**
- Driver and executor host processes using `IHostedService`/`BackgroundService`, Generic Host lifecycle, container-aware configuration, and cgroup-aware runtime defaults.
- gRPC control-plane services for registration, task assignment, heartbeats, task status, cancellation, lifecycle, and health checks over tuned Kestrel HTTP/2.
- `IDataExchange` abstraction and Arrow Flight data-plane implementation for result fetch and broadcast columnar transfers, with a documented raw `System.IO.Pipelines` replacement seam.
- Protobuf physical-plan and task wire format for driver-to-executor dispatch, including versioning and round-trip compatibility tests.
- Executor task dispatch using bounded `System.Threading.Channels`, backpressure, cancellation propagation, result reporting, and scheduler visibility.
- Kubernetes probes, SIGTERM drain, readiness removal, shutdown budgets, and graceful executor termination without losing in-flight tasks.

**Out of scope** (and where it lives instead)
- Logical planning, physical operator semantics, exchange insertion, and stage construction → EPIC-03, EPIC-04, EPIC-11 / personas `query-execution-engine-engineer`, `query-optimizer-scheduler-engineer`.
- Native remote shuffle worker, registry, replication, and drain-migration implementation → EPIC-09 / personas `dotnet-distributed-execution-engineer`, `delta-storage-format-engineer`.
- Kubernetes Operator CRDs and reconciliation loops → EPIC-10 / persona `kubernetes-operator-controller-engineer`.
- Production SLOs, alert routing, and incident-response runbooks → EPIC-13 / persona `cloud-native-site-reliability-engineer`.
- Security policy ownership for certificates, identity, and authorization → EPIC-00 / persona `cloud-native-security-sme`.

## Exit criteria

- [ ] A representative multi-executor job runs distributed: the driver schedules tasks over gRPC, executors execute EPIC-03 physical fragments, and columnar data moves over Arrow Flight.
- [ ] Protobuf physical plans and task envelopes serialize and deserialize round-trip across driver and executor test fixtures with explicit protocol versions.
- [ ] gRPC channel reuse, HTTP/2 multiplexing, stream limits, and Kestrel tuning are verified by integration tests or benchmarks that reject per-call channel creation.
- [ ] Executor task queues apply bounded backpressure, propagate cancellation/deadlines, and report completed, failed, canceled, and lost task states distinctly.
- [ ] Executors that receive SIGTERM stop readiness, drain or cancel work by policy, flush final task reports, and exit within `terminationGracePeriodSeconds` without losing in-flight task accounting.
- [ ] Control-plane and data-plane transports enforce documented mTLS/tenant-isolation hooks and emit correlated logs, metrics, and traces for job, stage, task, executor, and RPC identifiers.

## Features

### FEAT-08.1: Driver and executor host processes

- **Objective:** Establish reliable .NET process hosts for the driver and executor pods with explicit startup, registration, readiness, runtime configuration, and shutdown order. The hosts must respect Kubernetes resource limits and avoid hidden background work.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators none.
- **Depends on:** EPIC-03, EPIC-04

#### Stories

##### STORY-08.1.1: Build Generic Host process skeletons

- **As a** distributed runtime engineer **I want** driver and executor `IHostedService`/`BackgroundService` skeletons **so that** lifecycle state is explicit and testable in Kubernetes pods.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators none.
- **Size:** M. **Depends on:** EPIC-03, EPIC-04
- **Acceptance criteria:**
  - [ ] Given a driver pod starts, When the Generic Host initializes, Then services transition through configured, starting, ready, draining, and stopped states with observable state changes.
  - [ ] Given an executor pod starts, When registration dependencies are unavailable, Then startup remains bounded and readiness stays false with an actionable health reason.
  - [ ] Given host services are stopped, When `StopAsync` runs, Then no constructors, `async void`, or fire-and-forget loops hide required shutdown work.
  - [ ] Given unit and integration tests inspect host ordering, When services are added, Then start/stop dependencies are deterministic and documented.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `10`, `21` satisfied; docs updated if public API changes.

##### STORY-08.1.2: Add container-aware runtime configuration

- **As a** distributed runtime engineer **I want** executor runtime defaults derived from cgroup CPU and memory limits **so that** slots, queues, buffer pools, and GC assumptions match pod budgets.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators none.
- **Size:** M. **Depends on:** STORY-08.1.1
- **Acceptance criteria:**
  - [ ] Given CPU and memory limits are present, When an executor starts, Then default executor slots, queue capacities, buffer budgets, and shutdown budgets are derived from those limits.
  - [ ] Given limits are missing or malformed, When configuration loads, Then safe defaults are selected and a warning identifies the missing runtime fact.
  - [ ] Given configuration overrides are supplied, When validation runs, Then invalid negative, unbounded, or over-budget values fail before readiness.
  - [ ] Given diagnostics are collected, When an executor reports runtime state, Then cgroup inputs and effective runtime settings are visible without exposing secrets.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `10`, `21` satisfied; docs updated if public API changes.

### FEAT-08.2: gRPC control plane

- **Objective:** Provide the driver/executor control RPCs required for distributed task assignment, heartbeats, status, lifecycle, cancellation, and health. The control plane must use `Grpc.AspNetCore`, Kestrel HTTP/2, channel reuse, deadlines, and meaningful status codes.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators none.
- **Depends on:** FEAT-08.1

#### Stories

##### STORY-08.2.1: Define and host control-plane gRPC services

- **As a** distributed runtime engineer **I want** versioned control-plane services **so that** drivers and executors can register, receive tasks, heartbeat, report status, and receive lifecycle commands.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators none.
- **Size:** L. **Depends on:** FEAT-08.1
- **Acceptance criteria:**
  - [ ] Given an executor connects to a driver, When registration succeeds, Then the driver records executor identity, capabilities, protocol version, and current slot capacity.
  - [ ] Given task assignment, heartbeat, status, cancellation, and lifecycle RPCs, When clients invoke them, Then each RPC has documented deadlines, idempotency, status codes, and retry behavior.
  - [ ] Given version skew within the supported range, When driver and executor negotiate, Then unknown additive fields are ignored and incompatible versions fail with a clear protocol error.
  - [ ] Given RPC failures, When logs and metrics are inspected, Then `Unavailable`, `DeadlineExceeded`, `Cancelled`, `ResourceExhausted`, and domain failures are distinguishable.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `05`, `10`, `14`, `21` satisfied; docs updated if public API changes.

##### STORY-08.2.2: Tune Kestrel HTTP/2 and verify channel reuse

- **As a** distributed runtime engineer **I want** tested HTTP/2 and gRPC channel behavior **so that** control traffic multiplexes efficiently without connection explosions or head-of-line blocking.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators none.
- **Size:** M. **Depends on:** STORY-08.2.1
- **Acceptance criteria:**
  - [ ] Given many concurrent task RPCs and heartbeats, When integration tests run, Then calls reuse configured channels and connections rather than creating per-call channels.
  - [ ] Given configured stream limits, keep-alives, request-size limits, and flow-control windows, When load tests run, Then settings are enforced and documented with workload assumptions.
  - [ ] Given a saturated or slow peer, When deadlines expire, Then calls fail boundedly and do not block unrelated HTTP/2 streams indefinitely.
  - [ ] Given transport metrics are scraped, When a reviewer checks the run, Then active streams, connection counts, failures, and latency distributions are observable.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `08`, `10`, `21`, `22` satisfied; docs updated if public API changes.

### FEAT-08.3: `IDataExchange` and Arrow Flight data plane

- **Objective:** Implement bulk columnar transfer behind an `IDataExchange` abstraction using Arrow Flight first, while preserving the option to replace hot paths with raw `System.IO.Pipelines` later. The first data-plane scope covers result fetch and broadcast streams, with shape compatibility for future shuffle fetches.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Depends on:** FEAT-08.1, FEAT-08.2, EPIC-03

#### Stories

##### STORY-08.3.1: Define `IDataExchange` contracts for columnar streams

- **As a** runtime implementer **I want** an `IDataExchange` interface for columnar batch streams **so that** callers are isolated from Arrow Flight or future raw transport implementations.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** M. **Depends on:** EPIC-03, FEAT-08.2
- **Acceptance criteria:**
  - [ ] Given result fetch and broadcast callers, When they request a stream, Then the contract exposes batch schema, cancellation, deadlines, backpressure, and transfer metadata without binding to Arrow Flight types.
  - [ ] Given large transfers, When the consumer slows down, Then producer behavior is bounded by flow control rather than unbounded buffering.
  - [ ] Given a failed or canceled transfer, When status is reported, Then callers can distinguish cancellation, deadline, peer failure, schema mismatch, and authorization failure.
  - [ ] Given a future raw `System.IO.Pipelines` implementation, When API review runs, Then no public caller contract requires Flight-specific framing.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `05`, `08`, `14`, `21` satisfied; docs updated if public API changes.

##### STORY-08.3.2: Implement Arrow Flight result and broadcast streams

- **As a** distributed runtime engineer **I want** Arrow Flight streams for results and broadcasts **so that** executors and drivers exchange columnar data using ADR-0003's initial data plane.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** L. **Depends on:** STORY-08.3.1
- **Acceptance criteria:**
  - [ ] Given an executor produces `RecordBatch` or `ColumnBatch` data, When the driver fetches results, Then schema and batch contents arrive unchanged across the Flight stream.
  - [ ] Given broadcast data is published, When multiple executors fetch it concurrently, Then each executor receives the same versioned payload with bounded memory use.
  - [ ] Given a transfer is canceled mid-stream, When cancellation propagates, Then the producer stops work and the consumer observes a stable canceled status.
  - [ ] Given performance smoke tests run, When throughput and allocation metrics are captured, Then baseline values are recorded for future raw transport comparisons.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `08`, `21`, `22` satisfied; docs updated if public API changes.

### FEAT-08.4: Protobuf plan and task serialization

- **Objective:** Define the versioned protobuf wire format that carries physical plan fragments and task envelopes from driver to executor. The format must be owned jointly with query execution for plan shape and with distributed runtime for wire compatibility.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `query-execution-engine-engineer`.
- **Depends on:** EPIC-03, EPIC-04, FEAT-08.2

#### Stories

##### STORY-08.4.1: Define versioned physical-plan protobuf schema

- **As a** distributed runtime engineer **I want** a versioned protobuf schema for physical plan fragments **so that** drivers can dispatch tasks to executors over the gRPC control plane.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** EPIC-03, EPIC-04, FEAT-08.2
- **Acceptance criteria:**
  - [ ] Given supported physical operators from EPIC-03/EPIC-04, When schema review runs, Then each dispatchable fragment has a protobuf representation or an explicit unsupported error path.
  - [ ] Given schema evolution, When new fields are added, Then field numbers, defaults, unknown-field handling, and compatibility rules are documented.
  - [ ] Given query-engine plan invariants, When a fragment is serialized, Then lazy/eager semantics are preserved and serialization performs no execution or data reads.
  - [ ] Given malformed or incompatible payloads, When an executor decodes them, Then decoding fails before task execution with a stable protocol error.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `16`, `21` satisfied; docs updated if public API changes.

##### STORY-08.4.2: Add serialization round-trip and compatibility tests

- **As a** query execution collaborator **I want** deterministic plan/task serialization tests **so that** driver and executor releases can detect incompatible wire changes.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** STORY-08.4.1
- **Acceptance criteria:**
  - [ ] Given representative physical plans, When serialized and deserialized, Then the resulting task fragment is semantically equivalent to the source plan.
  - [ ] Given golden binary fixtures for supported protocol versions, When tests run, Then current code reads compatible fixtures and rejects incompatible fixtures with expected errors.
  - [ ] Given randomized valid plan fragments within bounded operator sets, When round-trip tests run, Then stable IDs, partition metadata, expressions, and task resources survive unchanged.
  - [ ] Given CI runs, When generated protobuf artifacts are stale, Then the build fails with an actionable regeneration instruction.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `16`, `21` satisfied; docs updated if public API changes.

### FEAT-08.5: Executor task dispatch and scheduling

- **Objective:** Build executor-side dispatch around bounded `System.Threading.Channels` with clear backpressure, cancellation, slot accounting, and result reporting. Executors must distinguish queue saturation from task failure and expose enough state for driver scheduling decisions.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `query-execution-engine-engineer`.
- **Depends on:** FEAT-08.2, FEAT-08.4

#### Stories

##### STORY-08.5.1: Implement bounded task queues and slot accounting

- **As a** distributed runtime engineer **I want** bounded executor task queues with slot accounting **so that** drivers cannot overload executors beyond configured runtime budgets.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** FEAT-08.2, FEAT-08.4
- **Acceptance criteria:**
  - [ ] Given an executor has finite slots and queue capacity, When the driver submits excess work, Then the executor applies documented backpressure or returns `ResourceExhausted` without accepting hidden work.
  - [ ] Given queued, running, completed, failed, and canceled tasks, When state is reported, Then counts and task attempt identities are consistent with driver observations.
  - [ ] Given task priorities or stage boundaries are configured, When scheduling occurs, Then ordering follows documented rules and avoids starvation under steady input.
  - [ ] Given the executor is saturated, When probes and heartbeats run, Then liveness remains stable while readiness or capacity reports indicate degraded acceptance.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `10`, `21` satisfied; docs updated if public API changes.

##### STORY-08.5.2: Propagate cancellation, deadlines, and result reporting

- **As a** query execution engineer **I want** cancellation and task results propagated end-to-end **so that** retries, user cancellation, and executor shutdown do not corrupt driver state.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-08.5.1
- **Acceptance criteria:**
  - [ ] Given a driver cancels a task attempt, When the cancellation reaches the executor, Then queued work is removed or running work observes the token and reports `Cancelled` distinctly from failure.
  - [ ] Given a task deadline expires, When execution observes the deadline, Then the executor stops work at a bounded checkpoint and reports `DeadlineExceeded` with task attempt metadata.
  - [ ] Given task execution succeeds or fails, When final reporting occurs, Then results, error details, counters, and data-plane descriptors are sent exactly once per attempt.
  - [ ] Given result reporting fails transiently, When retry policy runs, Then duplicate completion records are idempotently handled by the driver.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `16`, `21` satisfied; docs updated if public API changes.

### FEAT-08.6: Health, probes, and graceful Kubernetes shutdown

- **Objective:** Make driver and executor pods operationally safe through liveness, readiness, startup probes, SIGTERM handling, drain state, and shutdown budgets. Shutdown must stop new work before draining in-flight work and must preserve final task accounting.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `cloud-native-site-reliability-engineer`.
- **Depends on:** FEAT-08.1, FEAT-08.5

#### Stories

##### STORY-08.6.1: Implement health checks and Kubernetes probe semantics

- **As a** site reliability collaborator **I want** separate startup, readiness, and liveness checks **so that** Kubernetes can route and restart driver/executor pods safely.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `cloud-native-site-reliability-engineer`.
- **Size:** M. **Depends on:** FEAT-08.1, FEAT-08.2
- **Acceptance criteria:**
  - [ ] Given cold startup, When dependencies initialize, Then startup remains false until required services and protocol listeners are ready.
  - [ ] Given recoverable saturation, When task queues are full, Then liveness stays true while readiness or capacity reports indicate no new work should be routed.
  - [ ] Given an unrecoverable host failure, When health checks run, Then liveness fails with a reason that supports restart diagnosis.
  - [ ] Given health endpoints are called, When unauthenticated Kubernetes probes access them, Then only non-sensitive health state is returned.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `05`, `10`, `14`, `21` satisfied; docs updated if public API changes.

##### STORY-08.6.2: Implement SIGTERM drain and shutdown budget validation

- **As a** distributed runtime engineer **I want** SIGTERM drain behavior with validated shutdown budgets **so that** executors exit gracefully without losing in-flight task accounting.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `cloud-native-site-reliability-engineer`.
- **Size:** L. **Depends on:** FEAT-08.5, STORY-08.6.1
- **Acceptance criteria:**
  - [ ] Given an executor receives SIGTERM, When drain begins, Then readiness fails before new tasks are rejected or drained according to policy.
  - [ ] Given in-flight tasks can finish within budget, When shutdown proceeds, Then results are flushed and the executor exits within `terminationGracePeriodSeconds`.
  - [ ] Given in-flight tasks exceed budget, When cancellation policy triggers, Then tasks are canceled with final reports and no attempt remains in an ambiguous state.
  - [ ] Given configured `preStop`, readiness propagation, host shutdown timeout, and task drain budgets, When validation runs, Then invalid combinations fail deployment or startup with actionable messages.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `10`, `21` satisfied; docs updated if public API changes.

## Open questions

- Should the first executor scheduling policy include priority queues, or should priorities remain metadata until EPIC-11 owns scheduler policy?
- What minimum driver/executor protocol-version skew must v1 support during rolling upgrades?
- Which data-plane benchmark thresholds are sufficient to decide whether Arrow Flight remains acceptable or a raw `System.IO.Pipelines` implementation is required?
