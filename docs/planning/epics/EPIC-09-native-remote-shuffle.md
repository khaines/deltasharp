# EPIC-09: Native Remote Shuffle Service

- **Roadmap milestone:** M3 ([Milestone 3 — Distributed execution](../../../ROADMAP.md#milestone-3--distributed-execution-v0x))
- **Primary persona(s):** `dotnet-distributed-execution-engineer`, `delta-storage-format-engineer` (+ collaborators `kubernetes-operator-controller-engineer`, `cloud-native-site-reliability-engineer`, `compute-storage-finops-engineer`)
- **Related ADRs:** ADR-0004, ADR-0003
- **Depends on:** EPIC-02, EPIC-08
- **Status:** draft
- **Size:** XL

## Objective

Deliver DeltaSharp's .NET-native remote shuffle service for Kubernetes, matching ADR-0004's pull-first architecture with node-local workers, Arrow IPC blocks, a dynamic location registry, drain-migration, and configurable eager replication. This epic makes all-to-all exchange resilient to executor churn, graceful node drain, and sudden pod/node loss without adopting a JVM shuffle dependency.

## Scope

**In scope**
- `ShuffleManager` abstraction for map-side writes, reduce-side fetches, metadata registration, dynamic location resolution, and retry/re-resolve behavior.
- Arrow IPC shuffle block format, checksums, partition metadata, and map-side merge layout to reduce file/request count.
- Node-local shuffle worker process deployed as a DaemonSet and discovered by executors through Kubernetes host discovery (`status.hostIP`, fixed `hostPort`, or hostPath socket).
- Shuffle location registry equivalent to Spark `MapOutputTracker`, including holder generations, replica health, drain state, and current-location queries.
- Pull-based reduce fetch over Arrow Flight with bounded retries that re-resolve locations after every fetch failure; shuffle locations are never pinned.
- Graceful drain-migration and configurable eager replication to protect shuffle blocks during scale-down, node drain, sudden pod loss, and spot interruption.

**Out of scope** (and where it lives instead)
- General driver/executor hosting, gRPC control plane, Arrow Flight data-plane foundation, and SIGTERM drain mechanics → EPIC-08 / persona `dotnet-distributed-execution-engineer`.
- Kubernetes Operator CRDs, scheduling policy, and reconciliation ownership beyond DaemonSet-facing requirements → EPIC-10 / persona `kubernetes-operator-controller-engineer`.
- Object-store fallback durability tier implementation and policy automation → later EPIC / personas `dotnet-distributed-execution-engineer`, `compute-storage-finops-engineer`.
- Push-based shuffle merge, adaptive skew coalescing, and runtime repartitioning policy → EPIC-11 / persona `query-optimizer-scheduler-engineer`.
- Delta table ACID log, Parquet table files, and user table maintenance → EPIC-05 / persona `delta-storage-format-engineer`.

## Exit criteria

- [ ] A representative all-to-all shuffle completes correctly across multiple executors and node-local shuffle workers using pull-based reduce fetch.
- [ ] Reducers resolve shuffle block holders dynamically through the registry, never pin locations, and re-resolve plus retry after every fetch failure.
- [ ] Killing an executor pod after map output is written does not lose committed shuffle output when replication policy requires surviving replicas.
- [ ] Graceful node drain migrates local shuffle blocks to peers within the configured shutdown budget and updates the registry before the worker exits.
- [ ] Configurable eager replication writes each shuffle block to the requested number of worker nodes and reports under-replication distinctly from successful durability.
- [ ] Map-side merge reduces shuffle file/request count versus one-file-per-map-partition baselines and records performance evidence for regression gates.

## Features

### FEAT-09.1: `ShuffleManager` abstraction and Arrow IPC block format

- **Objective:** Define the runtime-facing shuffle abstraction and the storage-facing block format for pull-first remote shuffle. The contract must expose writes, commits, fetch descriptors, dynamic resolution hooks, retries, checksums, and format versions without leaking worker internals.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `delta-storage-format-engineer`.
- **Depends on:** EPIC-02, EPIC-08

#### Stories

##### STORY-09.1.1: Define `ShuffleManager` contracts and state model

- **As a** distributed runtime engineer **I want** a `ShuffleManager` abstraction **so that** query execution can materialize and fetch shuffle data without depending on worker implementation details.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `delta-storage-format-engineer`.
- **Size:** L. **Depends on:** EPIC-02, EPIC-08
- **Acceptance criteria:**
  - [ ] Given a map task writes shuffle output, When it calls `ShuffleManager`, Then shuffle ID, map ID, attempt ID, partition ID, block generation, and commit state are captured explicitly.
  - [ ] Given a reducer needs input, When it requests fetch descriptors, Then descriptors require registry resolution at fetch time and do not embed permanent worker locations.
  - [ ] Given task retry or speculative attempts, When multiple attempts write the same map output, Then commit rules pick one visible attempt and prevent duplicate reduce input.
  - [ ] Given a fetch failure, When retry policy runs, Then the next attempt re-resolves current holders from the registry rather than retrying a pinned endpoint.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `16`, `21` satisfied; docs updated if public API changes.

##### STORY-09.1.2: Specify Arrow IPC shuffle block format

- **As a** storage format collaborator **I want** a versioned Arrow IPC block format **so that** shuffle blocks are compact, checksummed, and readable across rolling upgrades.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** STORY-09.1.1, EPIC-02
- **Acceptance criteria:**
  - [ ] Given columnar batches from EPIC-02, When encoded as shuffle blocks, Then schema, partition ID, row count, byte length, compression metadata, checksum, and format version are recorded.
  - [ ] Given a corrupted, truncated, or wrong-version block, When a worker or reducer reads it, Then validation fails before rows are returned.
  - [ ] Given format evolution, When new metadata is added, Then older compatible readers ignore additive fields and incompatible changes fail clearly.
  - [ ] Given benchmark fixtures, When block encoding and decoding run, Then allocation, throughput, and compressed-size baselines are recorded.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `08`, `17`, `21`, `22` satisfied; docs updated if public API changes.

### FEAT-09.2: Node-local shuffle workers

- **Objective:** Run .NET shuffle workers on each Kubernetes node as a DaemonSet-backed fast local tier and make executors discover their local worker safely. Executors write to the local worker first, while worker readiness, disk ownership, and host discovery remain operator-friendly.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `kubernetes-operator-controller-engineer`.
- **Depends on:** EPIC-08, FEAT-09.1

#### Stories

##### STORY-09.2.1: Implement shuffle worker host and local disk ownership

- **As a** distributed runtime engineer **I want** a node-local shuffle worker host **so that** shuffle blocks survive executor-pod churn on the same node.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `kubernetes-operator-controller-engineer`.
- **Size:** L. **Depends on:** FEAT-09.1, EPIC-08
- **Acceptance criteria:**
  - [ ] Given a shuffle worker starts, When it initializes local storage, Then it verifies ownership, capacity, permissions, and format version before readiness becomes true.
  - [ ] Given multiple executors on the same node, When they write blocks, Then worker APIs isolate job, application, tenant, shuffle, and attempt namespaces.
  - [ ] Given disk pressure, When capacity thresholds are crossed, Then the worker stops accepting new blocks and reports degraded readiness without corrupting existing blocks.
  - [ ] Given worker restart on the same node, When local metadata is replayed, Then committed blocks and registry reconciliation candidates are recovered deterministically.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `10`, `18`, `21` satisfied; docs updated if public API changes.

##### STORY-09.2.2: Add DaemonSet host discovery contract

- **As a** Kubernetes operator collaborator **I want** executor-to-local-worker discovery rules **so that** every executor writes to the worker on its own node without hard-coded pod identities.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `kubernetes-operator-controller-engineer`.
- **Size:** M. **Depends on:** STORY-09.2.1
- **Acceptance criteria:**
  - [ ] Given an executor pod has downward-API `status.hostIP`, When it starts, Then it can discover the local worker endpoint via the documented host discovery mode.
  - [ ] Given fixed `hostPort` or hostPath socket modes are configured, When validation runs, Then incompatible or missing configuration fails before task acceptance.
  - [ ] Given a local worker is unavailable, When an executor starts or writes, Then the executor reports a precise degraded state and does not silently write to a remote non-local worker.
  - [ ] Given operator manifests are generated later, When EPIC-10 consumes this contract, Then required DaemonSet, volume, port, probe, and security context fields are specified.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `10`, `18`, `21` satisfied; docs updated if public API changes.

### FEAT-09.3: Shuffle location registry and dynamic resolution

- **Objective:** Build the authoritative registry that maps shuffle blocks to current worker holders and enforces the key invariant that reducers dynamically resolve locations and re-resolve on failure. Registry responses are current facts with generations and leases, never permanent pinned locations.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators none.
- **Depends on:** FEAT-09.1, FEAT-09.2

#### Stories

##### STORY-09.3.1: Implement registry metadata, generations, and holder health

- **As a** distributed runtime engineer **I want** a registry for current shuffle block holders **so that** reducers can find valid replicas as blocks move, drain, or fail.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators none.
- **Size:** L. **Depends on:** FEAT-09.2
- **Acceptance criteria:**
  - [ ] Given a worker commits a block, When it registers the block, Then the registry records shuffle ID, map ID, partition ID, generation, holder worker, replica state, and checksum metadata.
  - [ ] Given a worker is draining, unhealthy, or lost, When holder health changes, Then registry responses reflect current eligible holders and mark stale generations unavailable.
  - [ ] Given concurrent registration updates, When generations conflict, Then stale updates are rejected or superseded deterministically.
  - [ ] Given registry state is queried, When audit logs and metrics are inspected, Then holder count, under-replication, stale entries, and generation changes are observable.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `09a`, `09b`, `21` satisfied; docs updated if public API changes.

##### STORY-09.3.2: Enforce re-resolve fetch retry semantics

- **As a** reducer runtime **I want** fetch failures to re-resolve block holders **so that** moved or failed shuffle data remains reachable without pinned stale locations.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators none.
- **Size:** M. **Depends on:** STORY-09.3.1
- **Acceptance criteria:**
  - [ ] Given a reducer fetch descriptor, When fetch starts, Then the worker endpoint is obtained from the registry immediately before the attempt.
  - [ ] Given a fetch returns unavailable, checksum mismatch, deadline exceeded, or connection failure, When retry policy continues, Then the next attempt queries the registry again before choosing a holder.
  - [ ] Given all holders are unavailable, When retry budget is exhausted, Then the reducer reports a deterministic shuffle-fetch failure with registry version and attempted holders.
  - [ ] Given tests move a block between workers, When a reducer retries, Then it fetches the moved block through the updated registry entry without relying on the original endpoint.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `08`, `21`, `22` satisfied; docs updated if public API changes.

### FEAT-09.4: Map-side write/merge and reduce-side fetch

- **Objective:** Implement the core shuffle data path: map tasks write partitioned Arrow IPC blocks to the local worker, workers merge small partition fragments, and reducers fetch current block replicas through Arrow Flight. The design must reduce small files and request counts while preserving partition correctness.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `delta-storage-format-engineer`.
- **Depends on:** FEAT-09.1, FEAT-09.2, FEAT-09.3

#### Stories

##### STORY-09.4.1: Implement map-side write commit and partition merge

- **As a** distributed runtime engineer **I want** map-side shuffle writes merged by partition **so that** reducers avoid excessive files and fetch requests.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `delta-storage-format-engineer`.
- **Size:** L. **Depends on:** FEAT-09.1, FEAT-09.2
- **Acceptance criteria:**
  - [ ] Given a map task emits rows for multiple reduce partitions, When write completes, Then each partition's committed blocks are durable in the local worker and registered only after validation.
  - [ ] Given many small fragments for the same shuffle partition, When merge runs, Then fragments are coalesced into fewer Arrow IPC blocks without changing row membership or partition order requirements.
  - [ ] Given a map task fails before commit, When worker cleanup runs, Then uncommitted fragments are not visible to reducers or the registry.
  - [ ] Given benchmark fixtures, When merge is enabled, Then file/request count is lower than the one-fragment-per-map-partition baseline and correctness fixtures still pass.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `08`, `17`, `21`, `22` satisfied; docs updated if public API changes.

##### STORY-09.4.2: Implement reduce-side Arrow Flight fetch

- **As a** reduce task **I want** Arrow Flight shuffle fetches from dynamically resolved holders **so that** all map outputs for my partition arrive correctly and efficiently.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `delta-storage-format-engineer`.
- **Size:** L. **Depends on:** STORY-09.3.2, STORY-09.4.1, EPIC-08
- **Acceptance criteria:**
  - [ ] Given a reduce partition with outputs from multiple maps, When fetch runs, Then all committed map outputs are streamed exactly once into the reduce input.
  - [ ] Given a holder changes during fetch, When the current transfer fails, Then retry re-resolves through the registry and resumes according to block boundaries without duplicating rows.
  - [ ] Given a block checksum or format validation fails, When the reducer detects it, Then the failed holder is reported and another current replica is tried if available.
  - [ ] Given fetch metrics are collected, When a shuffle completes, Then bytes, blocks, retries, re-resolutions, throughput, and failed holders are recorded by shuffle and stage.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `08`, `17`, `21`, `22` satisfied; docs updated if public API changes.

### FEAT-09.5: Drain-migration on graceful scale-down

- **Objective:** Move shuffle blocks off a draining worker before node removal and update the registry so reducers keep resolving current holders. Drain-migration must fit Kubernetes termination budgets and report partial migration explicitly.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `cloud-native-site-reliability-engineer`.
- **Depends on:** FEAT-09.2, FEAT-09.3, FEAT-09.4

#### Stories

##### STORY-09.5.1: Implement worker drain state and migration planner

- **As a** shuffle worker **I want** a drain state and migration plan **so that** graceful node scale-down moves blocks before local storage disappears.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `cloud-native-site-reliability-engineer`.
- **Size:** L. **Depends on:** FEAT-09.3, FEAT-09.4
- **Acceptance criteria:**
  - [ ] Given a worker receives SIGTERM or drain command, When drain begins, Then readiness fails, new writes stop, and existing blocks are inventoried with sizes and replication state.
  - [ ] Given peer workers are available, When the migration planner runs, Then it selects target peers that preserve configured replica counts and avoids choosing the draining worker.
  - [ ] Given migration budget is insufficient, When planning completes, Then prioritized blocks, expected remaining risk, and required timeout are reported before exit.
  - [ ] Given drain state is active, When registry queries run, Then draining holders are deprioritized or excluded according to fetch policy.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `10`, `18`, `21` satisfied; docs updated if public API changes.

##### STORY-09.5.2: Migrate blocks and update registry atomically enough for fetchers

- **As a** distributed runtime engineer **I want** migrated blocks registered before old holders disappear **so that** reducers can re-resolve moved data during graceful scale-down.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `cloud-native-site-reliability-engineer`.
- **Size:** L. **Depends on:** STORY-09.5.1
- **Acceptance criteria:**
  - [ ] Given a block is copied to a peer, When checksum validation succeeds, Then the registry adds the peer holder before the draining holder is removed.
  - [ ] Given a reducer is fetching during migration, When an old holder fails, Then retry re-resolves and fetches from the newly registered holder.
  - [ ] Given migration fails for some blocks, When the worker exits or times out, Then under-replicated and unmigrated blocks are reported distinctly for driver recovery policy.
  - [ ] Given integration tests drain a node, When reducers continue, Then no fetch path uses pinned locations and completed shuffle output remains correct.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `10`, `18`, `21` satisfied; docs updated if public API changes.

### FEAT-09.6: Configurable eager replication

- **Objective:** Protect shuffle output from sudden crash, spot loss, and non-graceful node failure by eagerly replicating blocks to N worker nodes at write time. Replication factor, placement, cost visibility, and under-replication semantics must be configurable; object-store fallback remains a later toggle, not part of this epic's implementation.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `compute-storage-finops-engineer`.
- **Depends on:** FEAT-09.2, FEAT-09.3, FEAT-09.4

#### Stories

##### STORY-09.6.1: Implement configurable replication policy and placement

- **As a** distributed runtime engineer **I want** configurable eager replication **so that** committed shuffle blocks survive sudden worker or node loss when policy requires it.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `compute-storage-finops-engineer`.
- **Size:** L. **Depends on:** FEAT-09.3, FEAT-09.4
- **Acceptance criteria:**
  - [ ] Given replication factor N is configured, When a map block commits, Then the worker attempts to place validated replicas on N distinct eligible workers when available.
  - [ ] Given fewer eligible workers exist than N, When commit policy completes, Then the registry records under-replication and the driver receives a policy-specific warning or failure.
  - [ ] Given placement choices are made, When metrics are inspected, Then replica fan-out, bytes written, storage used, and per-shuffle replication cost are visible.
  - [ ] Given object-store fallback configuration is present, When this epic runs, Then unsupported fallback remains a flagged later toggle and is not silently used.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `08`, `21`, `22` satisfied; docs updated if public API changes.

##### STORY-09.6.2: Validate executor and worker loss with replicated blocks

- **As a** reliability-minded runtime engineer **I want** loss tests for replicated shuffle blocks **so that** sudden pod or node failure does not lose committed shuffle output.
- **Implementer persona(s):** Primary `dotnet-distributed-execution-engineer`; Collaborators `compute-storage-finops-engineer`.
- **Size:** L. **Depends on:** STORY-09.6.1
- **Acceptance criteria:**
  - [ ] Given a map executor pod is killed after its local worker commits and replicates blocks, When reducers fetch, Then they resolve surviving replicas and complete correctly.
  - [ ] Given a worker process disappears suddenly, When registry health marks it unavailable, Then reducers re-resolve to other replicas and retry without pinned endpoints.
  - [ ] Given one replica is corrupted or missing, When checksum validation fails, Then the failed replica is avoided and another current holder is tried before the shuffle fails.
  - [ ] Given replication factor changes between jobs, When tests run, Then durability behavior and cost metrics match the configured policy for each job.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `08`, `10`, `21`, `22` satisfied; docs updated if public API changes.

## Open questions

- What registry persistence or quorum model is required for v1 if the driver restarts during an active shuffle?
- What default replication factor balances spot/node-loss resilience against network and storage cost for small development clusters?
- Which worker placement constraints should be mandatory in EPIC-10 to avoid replicas landing on failure-correlated nodes?
