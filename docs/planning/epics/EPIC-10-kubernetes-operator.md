# EPIC-10: Kubernetes Operator & CRDs

- **Roadmap milestone:** M3 ([Milestone 3 — Distributed execution](../../../ROADMAP.md#milestone-3--distributed-execution-v0x))
- **Primary persona(s):** `kubernetes-operator-controller-engineer` (+ collaborators `cloud-native-security-sme`, `dotnet-distributed-execution-engineer`)
- **Related ADRs:** ADR-0009, ADR-0003, ADR-0004
- **Depends on:** EPIC-08
- **Status:** draft
- **Size:** L

## Objective

Deliver the Kubernetes-native control plane for DeltaSharp by implementing KubeOps-based custom resources, reconcilers, admission policy, status, and lifecycle management for batch applications and interactive sessions. This epic turns the EPIC-08 distributed runtime into declarative Kubernetes workloads while preserving the ADR-0009 boundary: the operator manages desired state and lifecycle, and the runtime owns driver, executor, and shuffle data-plane behavior.

## Scope

**In scope**
- KubeOps CRDs for `DeltaSharpApplication` and `DeltaSharpSession`, including versioned schemas, validation, defaults, status subresources, and examples.
- Operator host, leader election, least-privilege RBAC, health, metrics, Kubernetes events, and deployment manifests.
- Idempotent, level-triggered reconcilers for application driver/executor pods, long-running sessions, status updates, retry/backoff, completion, and safe cleanup.
- Shuffle-worker DaemonSet management aligned with ADR-0004, including node-local worker rollout visibility and drain coordination hooks.
- Admission webhooks, finalizers, status conditions, observed-generation discipline, and envtest/kind integration coverage.

**Out of scope** (and where it lives instead)
- Driver, executor, gRPC, Arrow Flight, `IDataExchange`, and shuffle-worker process internals → EPIC-08 / EPIC-09 / persona `dotnet-distributed-execution-engineer`.
- Query planning, task scheduling, stage semantics, and adaptive execution policy → EPIC-11 / persona `query-optimizer-scheduler-engineer`.
- Delta transaction log and Parquet storage semantics → EPIC-05 / persona `delta-storage-format-engineer`.
- Production SLOs, incident runbooks, alert policy, and rollout operations beyond operator health signals → EPIC-13 / persona `cloud-native-site-reliability-engineer`.
- Cluster/pool CRDs beyond `DeltaSharpApplication` and `DeltaSharpSession` → future epic / persona `cloud-native-distributed-systems-architect`.

## Exit criteria

- [ ] Applying a valid `DeltaSharpApplication` custom resource to kind creates one driver workload and the requested executor workloads, exposes required services/config, and reports current status conditions with `observedGeneration`.
- [ ] Repeated reconciles, operator restarts, duplicate watch events, and leader changes are level-triggered and idempotent: no duplicate child resources, no orphaned owned resources, and no deletion of live workloads that are still desired by the custom resource.
- [ ] Applying an invalid application or session spec is rejected by admission validation with deterministic, actionable errors before any driver, executor, or shuffle workload is created.
- [ ] Deleting an application or session runs resumable finalization, preserves terminal status/events, and removes only DeltaSharp-owned child resources after safe teardown conditions are met.
- [ ] Operator RBAC is least-privilege for CRDs, status subresources, events, pods, services, config maps, daemonsets, leases, and webhook resources, with security review evidence.
- [ ] CRDs, webhooks, reconcilers, finalizers, and manifests are covered by unit tests plus envtest or kind integration tests in CI-equivalent commands.

## Features

### FEAT-10.1: CRD schemas for applications and sessions

- **Objective:** Define stable, versioned public APIs for batch `DeltaSharpApplication` and interactive `DeltaSharpSession` resources. Schemas must encode supported runtime configuration, validation, defaults, status contracts, and compatibility rules without leaking data-plane implementation details.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-distributed-systems-architect`, `cloud-native-security-sme`.
- **Depends on:** EPIC-08.

#### Stories

##### STORY-10.1.1: Define `DeltaSharpApplication` spec and status schema

- **As a** platform user **I want** a `DeltaSharpApplication` CRD for one-shot batch jobs **so that** DeltaSharp applications can be submitted declaratively on Kubernetes.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-distributed-systems-architect`, `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** EPIC-08.
- **Acceptance criteria:**
  - [ ] Given generated CRD manifests When `kubectl explain deltasharpapplication.spec` is run Then driver image, executor image, main entry point, arguments, executor count, resources, service account, shuffle settings, and storage references are discoverable.
  - [ ] Given a valid application spec When it is applied Then Kubernetes accepts it with structural schema pruning and defaulting enabled.
  - [ ] Given the runtime updates status When status is read Then conditions include at least `DriverCreated`, `DriverReady`, `ExecutorsReady`, `Running`, `Completed`, and `Failed` with reason, message, and `observedGeneration` fields.
  - [ ] Given an existing v1alpha1 application resource When optional fields are added later Then the schema remains additive and compatible without renaming existing fields.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `13`, `03a` satisfied; CRD examples and schema docs updated if public API changes.

##### STORY-10.1.2: Define `DeltaSharpSession` spec and status schema

- **As a** notebook or interactive client **I want** a `DeltaSharpSession` CRD for long-running sessions **so that** interactive DeltaSharp work has a durable Kubernetes lifecycle and discoverable endpoint status.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-distributed-systems-architect`, `cloud-native-security-sme`.
- **Size:** M. **Depends on:** STORY-10.1.1.
- **Acceptance criteria:**
  - [ ] Given generated CRD manifests When `kubectl explain deltasharpsession.spec` is run Then session image, endpoint exposure, idle timeout, executor bounds, resources, service account, and storage references are discoverable.
  - [ ] Given a valid session spec When it is applied Then Kubernetes accepts it and status can report interactive endpoint readiness without mutating spec.
  - [ ] Given a session status update When status is read Then conditions include at least `DriverCreated`, `EndpointReady`, `ExecutorsReady`, `Idle`, `Draining`, and `Failed` with `observedGeneration`.
  - [ ] Given tenant-sensitive fields When manifests are reviewed Then secrets are referenced by name/key and no inline secret values are represented in spec, status, labels, or annotations.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `05`, `13`, `03a` satisfied; CRD examples and schema docs updated if public API changes.

##### STORY-10.1.3: Add CRD versioning, defaults, and validation rules

- **Implement** CRD versioning and schema validation that reject unsupported or unsafe specs while preserving an additive compatibility path for future versions.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-security-sme`.
- **Size:** S. **Depends on:** STORY-10.1.1 / STORY-10.1.2.
- **Acceptance criteria:**
  - [ ] Given generated CRDs When reviewed Then each served version, storage version, conversion strategy assumption, nullable field, enum, and default is explicit.
  - [ ] Given invalid resource quantities, negative executor counts, unsupported shuffle settings, or missing images When the resource is applied Then schema validation rejects it.
  - [ ] Given omitted optional fields When the resource is applied Then documented defaults are visible in the persisted object or applied by admission consistently.
  - [ ] Given generated manifests When `kubectl apply --dry-run=server` runs against kind Then both CRDs pass server-side validation.
- **Definition of done:** builds/tests/format pass; checklists `18`, `05`, `13`, `04b`, `03a` satisfied; compatibility notes documented.

### FEAT-10.2: Operator scaffold, deployment, and control-plane security

- **Objective:** Establish the KubeOps operator host and deployment foundation with leader election, health, metrics, webhook hosting, and least-privilege Kubernetes permissions. The operator must be observable and safe to run with multiple replicas.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-security-sme`.
- **Depends on:** FEAT-10.1.

#### Stories

##### STORY-10.2.1: Scaffold KubeOps host and controller registration

- **Implement** the .NET KubeOps host that registers CRDs, reconcilers, admission webhooks, metrics, health checks, and graceful shutdown behavior.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** FEAT-10.1.
- **Acceptance criteria:**
  - [ ] Given the operator process starts When configuration is valid Then KubeOps registers application and session controllers plus webhook endpoints.
  - [ ] Given SIGTERM is sent to the operator When shutdown begins Then readiness fails before the host exits and no reconcile starts after shutdown cancellation is requested.
  - [ ] Given operator metrics are scraped When reconciles run Then reconcile counts, failures, durations, and workqueue depth are exposed with resource kind labels.
  - [ ] Given health probes are called When dependencies are healthy Then startup, liveness, and readiness endpoints return success independently.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `09b`, `03a` satisfied; operator startup docs updated.

##### STORY-10.2.2: Add leader election and safe multi-replica behavior

- **As an** operator maintainer **I want** leader election **so that** multiple operator replicas can be deployed without concurrent reconcile conflicts.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-security-sme`.
- **Size:** S. **Depends on:** STORY-10.2.1.
- **Acceptance criteria:**
  - [ ] Given two operator replicas are deployed When both start Then exactly one replica holds the leader lease for active reconciliation.
  - [ ] Given the leader replica exits When the lease expires Then a standby replica becomes leader without duplicating child resources.
  - [ ] Given lease permissions are reviewed When RBAC is inspected Then lease verbs are limited to the namespace and resource names needed for leader election.
  - [ ] Given leader failover occurs during status update conflicts When reconciliation resumes Then status converges using retryable conflict handling.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `05`, `04b`, `03a` satisfied; kind failover test evidence included.

##### STORY-10.2.3: Create least-privilege RBAC and deployment manifests

- **As a** cluster administrator **I want** minimal RBAC and deployable manifests **so that** the operator can be installed without broad cluster privileges.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-security-sme`.
- **Size:** M. **Depends on:** STORY-10.2.1.
- **Acceptance criteria:**
  - [ ] Given generated RBAC When reviewed Then verbs are limited by resource and subresource for CRDs, status, events, pods, services, config maps, daemonsets, leases, and webhook service access.
  - [ ] Given a forbidden resource such as secrets read-all or nodes update When RBAC is inspected Then the operator service account does not receive that permission.
  - [ ] Given manifests are applied to kind When the operator starts Then it can reconcile supported resources without using cluster-admin.
  - [ ] Given Helm or Kustomize output is rendered When compared to checked-in manifests Then labels, service accounts, probes, resources, webhook service, and RBAC are deterministic.
- **Definition of done:** builds/tests/format pass; checklists `18`, `05`, `10`, `13`, `04b`, `03a` satisfied; install manifests documented.

### FEAT-10.3: Application reconciler for batch workloads

- **Objective:** Reconcile `DeltaSharpApplication` desired state into driver and executor Kubernetes resources, status conditions, retry/backoff, completion, and cleanup. Reconciliation must be idempotent, level-triggered, and must never delete live desired workloads.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Depends on:** FEAT-10.1 / FEAT-10.2.

#### Stories

##### STORY-10.3.1: Provision driver resources for applications

- **As a** batch application submitter **I want** the operator to create the driver resources for my application **so that** the DeltaSharp runtime can start execution from declared desired state.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** FEAT-10.2.
- **Acceptance criteria:**
  - [ ] Given a valid `DeltaSharpApplication` When reconciled Then exactly one desired driver pod or controller-owned driver workload is created with stable labels, owner references, resources, probes, config, and service account.
  - [ ] Given reconcile runs repeatedly When the driver already exists Then the operator patches drifted desired fields without creating duplicate drivers.
  - [ ] Given the driver workload is running When status is updated Then `DriverCreated` and `DriverReady` conditions reflect pod phase and readiness with current `observedGeneration`.
  - [ ] Given the application spec still desires the driver When a child list includes a live driver Then cleanup logic does not delete it.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `21`, `04b`, `03a` satisfied; driver resource examples updated.

##### STORY-10.3.2: Provision and scale executor resources for applications

- **As a** batch application submitter **I want** the operator to create and scale executor resources **so that** requested parallelism is reflected in Kubernetes state.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** STORY-10.3.1.
- **Acceptance criteria:**
  - [ ] Given an application with executor count N When reconciled Then N executor pods or an equivalent controller-owned executor set are desired with stable selectors and owner references.
  - [ ] Given executor count changes from N to M When reconciled Then the child resources converge to M without deleting the driver or unrelated live workloads.
  - [ ] Given executor pods are pending, ready, failed, or terminating When status is updated Then desired, pending, ready, failed, and terminating counts are visible.
  - [ ] Given repeated reconciles or operator restart When executor resources already exist Then no duplicate executors with colliding names are created.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `21`, `04b`, `03a` satisfied; executor lifecycle tests included.

##### STORY-10.3.3: Implement application retry, backoff, completion, and cleanup

- **As a** platform operator **I want** application retries and completion handling to be explicit **so that** failed and completed jobs converge safely without resource leaks.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** STORY-10.3.1 / STORY-10.3.2.
- **Acceptance criteria:**
  - [ ] Given a driver exits successfully When reconciled Then application status reaches `Completed` and cleanup follows the configured retention policy.
  - [ ] Given a retryable driver or executor failure When reconciled Then backoff state is recorded in status and retry attempts do not create duplicate live workloads.
  - [ ] Given retry budget is exhausted When reconciled Then application status reaches `Failed` with reason and message explaining the terminal cause.
  - [ ] Given cleanup runs after terminal state When desired retained resources are configured Then the operator preserves them and deletes only owned resources selected for cleanup.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `21`, `04b`, `03a` satisfied; retry and terminal-state tests included.

### FEAT-10.4: Session reconciler for interactive workloads

- **Objective:** Reconcile `DeltaSharpSession` resources into long-running interactive driver/executor lifecycles with endpoint readiness, idle handling, and safe teardown. Sessions must behave as durable control-plane objects rather than transient client connections.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-distributed-systems-architect`, `dotnet-distributed-execution-engineer`.
- **Depends on:** FEAT-10.1 / FEAT-10.2.

#### Stories

##### STORY-10.4.1: Provision session driver, service, and endpoint status

- **As an** interactive client **I want** a session endpoint published in status **so that** notebooks and CLIs can discover when the session is ready.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** FEAT-10.2.
- **Acceptance criteria:**
  - [ ] Given a valid `DeltaSharpSession` When reconciled Then exactly one session driver workload and service are created with stable names, labels, owner references, and probes.
  - [ ] Given the service has ready endpoints When status is updated Then `EndpointReady=True` includes endpoint reference data safe for clients to consume.
  - [ ] Given service endpoints are absent When status is updated Then `EndpointReady=False` reports an actionable reason without marking stale generations ready.
  - [ ] Given repeated reconciles When the driver and service already exist Then no duplicate session endpoint resources are created.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `04b`, `03a` satisfied; session endpoint docs updated.

##### STORY-10.4.2: Manage session executors and idle behavior

- **As a** notebook user **I want** session executors to remain available while active and scale down when idle **so that** interactive work balances responsiveness and cost.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-distributed-systems-architect`, `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** STORY-10.4.1.
- **Acceptance criteria:**
  - [ ] Given a session with minimum and maximum executor bounds When reconciled Then executor desired state remains within those bounds.
  - [ ] Given the session reports activity through supported status or runtime signals When reconciled Then the operator preserves active executors and does not apply idle cleanup.
  - [ ] Given idle timeout has elapsed and no activity is observed When reconciled Then status records `Idle=True` and executor scale-down follows the configured policy.
  - [ ] Given repeated idle reconciles When executors are already scaled to the idle target Then no extra deletion or duplicate status events occur.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `21`, `04b`, `03a` satisfied; idle lifecycle tests included.

##### STORY-10.4.3: Safely terminate interactive sessions

- **As a** platform operator **I want** session deletion to drain and finalize safely **so that** live interactive workloads are not terminated without recorded teardown state.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-security-sme`, `dotnet-distributed-execution-engineer`.
- **Size:** S. **Depends on:** STORY-10.4.1 / STORY-10.4.2.
- **Acceptance criteria:**
  - [ ] Given a session has a deletion timestamp When reconciled Then finalization records `Draining=True` before deleting owned child resources.
  - [ ] Given teardown is interrupted by operator restart When reconciliation resumes Then finalization continues from observed Kubernetes state without requiring in-memory state.
  - [ ] Given a live desired child workload remains during non-deletion reconcile When cleanup code runs Then it is not deleted.
  - [ ] Given finalization completes When owned child resources are gone or explicitly retained Then the finalizer is removed and a terminal event is emitted.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `05`, `04b`, `03a` satisfied; finalization tests included.

### FEAT-10.5: Shuffle DaemonSet management

- **Objective:** Manage node-local DeltaSharp shuffle-worker DaemonSets as Kubernetes resources while preserving ADR-0004 data-plane ownership boundaries. The operator deploys, observes, and coordinates drain intent; the .NET shuffle service owns block movement, replication, registry, and transfer protocols.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Depends on:** FEAT-10.2 / EPIC-09.

#### Stories

##### STORY-10.5.1: Reconcile shuffle-worker DaemonSet manifests

- **As a** platform operator **I want** shuffle workers deployed on eligible nodes **so that** applications and sessions can use node-local remote shuffle services.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** FEAT-10.2 / EPIC-09.
- **Acceptance criteria:**
  - [ ] Given shuffle is enabled When the operator reconciles cluster-scoped shuffle configuration Then one desired DaemonSet is present with node-local storage mounts, ports, probes, resources, tolerations, and stable labels.
  - [ ] Given the DaemonSet already exists When reconcile repeats Then desired fields converge without replacing healthy pods unnecessarily.
  - [ ] Given shuffle is disabled by supported configuration When reconciled Then the operator does not delete live application or session workloads.
  - [ ] Given generated manifests are reviewed Then the DaemonSet runs the .NET shuffle-worker image and contains no JVM shuffle service dependency.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `13`, `21`, `04b`, `03a` satisfied; DaemonSet example updated.

##### STORY-10.5.2: Report shuffle-worker availability to applications and sessions

- **As a** DeltaSharp user **I want** status to show shuffle-worker availability **so that** blocked applications or sessions are explainable before tasks run.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Size:** S. **Depends on:** STORY-10.5.1.
- **Acceptance criteria:**
  - [ ] Given the shuffle DaemonSet has desired and available counts When an application or session reconciles Then status condition `ShuffleWorkersAvailable` reflects availability.
  - [ ] Given no eligible node has a ready shuffle worker When status is updated Then the condition reports `False` with reason and message instead of blocking the reconcile loop.
  - [ ] Given DaemonSet availability changes When reconcile runs again Then status converges without requiring a new application or session spec update.
  - [ ] Given shuffle is not required for a workload When status is read Then the absence of shuffle workers is not reported as a failure.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `21`, `04b`, `03a` satisfied; status-condition tests included.

##### STORY-10.5.3: Coordinate node drain intent with shuffle workers

- **As a** platform operator **I want** drain intent surfaced to shuffle workers **so that** graceful node shutdown can trigger ADR-0004 drain-migration without the operator owning data movement.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `dotnet-distributed-execution-engineer`, `cloud-native-distributed-systems-architect`.
- **Size:** S. **Depends on:** STORY-10.5.1 / EPIC-09.
- **Acceptance criteria:**
  - [ ] Given a shuffle-worker pod is terminating When its manifest is inspected Then preStop, termination grace period, and readiness behavior provide a bounded drain window for the runtime.
  - [ ] Given a node or worker enters drain state When status/events are emitted Then the operator records drain intent without moving shuffle blocks itself.
  - [ ] Given drain coordination fails or times out When reconcile observes the state Then a warning condition or event is recorded with actionable reason.
  - [ ] Given application or session workloads remain desired during shuffle drain When reconcile runs Then the operator does not delete their live driver or executor workloads.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `21`, `04b`, `03a` satisfied; drain coordination tests or kind scenario documented.

### FEAT-10.6: Webhooks, finalizers, status conditions, and events

- **Objective:** Provide admission-time guardrails and lifecycle correctness primitives for all operator-managed resources. Webhooks must be deterministic and side-effect-free; finalizers and status updates must be resumable, conflict-tolerant, and user-facing.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-security-sme`.
- **Depends on:** FEAT-10.1 / FEAT-10.2.

#### Stories

##### STORY-10.6.1: Implement admission validation webhooks

- **As a** cluster administrator **I want** malformed or unsafe specs rejected at admission **so that** invalid DeltaSharp workloads never enter stuck reconcile states.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-security-sme`.
- **Size:** M. **Depends on:** FEAT-10.1 / FEAT-10.2.
- **Acceptance criteria:**
  - [ ] Given malformed image, resource, executor, service account, shuffle, or endpoint settings When a resource is created or updated Then the webhook rejects it with field-specific errors.
  - [ ] Given a valid existing resource When only status changes Then admission validation does not reject the status update because of immutable spec checks.
  - [ ] Given webhook latency is measured in kind When validation runs Then it completes within the documented timeout budget and performs no data-plane calls.
  - [ ] Given webhook TLS and failure policy are reviewed When manifests are inspected Then certificate assumptions, service reference, timeout, and failure policy are explicit.
- **Definition of done:** builds/tests/format pass; checklists `18`, `05`, `10`, `13`, `04b`, `03a` satisfied; admission error examples documented.

##### STORY-10.6.2: Implement finalizers for applications, sessions, and shuffle resources

- **As a** DeltaSharp maintainer **I want** resumable finalizers **so that** teardown is safe across retries, restarts, and partial Kubernetes API failures.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-security-sme`, `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** FEAT-10.3 / FEAT-10.4 / FEAT-10.5.
- **Acceptance criteria:**
  - [ ] Given a newly observed application or session When reconciled Then the operator adds its finalizer before creating child resources that need ordered cleanup.
  - [ ] Given a delete request occurs mid-reconcile When finalization resumes Then cleanup is driven by observed child resources and not hidden in-memory state.
  - [ ] Given child resources are not owned by the deleting custom resource When finalization runs Then they are not deleted.
  - [ ] Given all teardown obligations are complete When reconciled Then the finalizer is removed exactly once and terminal status/events remain available until Kubernetes garbage collection completes.
- **Definition of done:** builds/tests/format pass; checklists `18`, `05`, `10`, `04b`, `03a` satisfied; finalizer replay tests included.

##### STORY-10.6.3: Standardize status conditions and Kubernetes events

- **As a** user or operator **I want** stable status conditions and events **so that** application, session, and shuffle lifecycle can be diagnosed without reading operator logs.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `cloud-native-distributed-systems-architect`, `cloud-native-security-sme`.
- **Size:** S. **Depends on:** FEAT-10.3 / FEAT-10.4 / FEAT-10.5.
- **Acceptance criteria:**
  - [ ] Given any status condition update When persisted Then it includes type, status, reason, message, last transition time, and current `observedGeneration`.
  - [ ] Given stale status from an older generation exists When spec changes Then readiness and terminal conditions are not treated as current until reconciled for the new generation.
  - [ ] Given important lifecycle transitions occur When events are listed Then driver creation, executor scaling, shuffle availability, validation rejection, retry, drain, completion, and failure are represented with stable reasons.
  - [ ] Given status update conflicts occur When reconcile retries Then conditions are merged without clobbering unrelated current conditions.
- **Definition of done:** builds/tests/format pass; checklists `18`, `10`, `09a`, `04b`, `03a` satisfied; status contract docs updated.

## Open questions

- Should the first implementation model driver and executor children as raw pods, Jobs, or Deployment/ReplicaSet-backed resources for best reconciliation and cleanup semantics?
- Which session endpoint exposure modes are supported in v1: ClusterIP only, port-forward-friendly services, ingress hooks, or all of them behind explicit policy?
- Where should cluster-wide shuffle-worker configuration live until a future cluster/pool CRD exists: operator Helm values, ConfigMap, namespace-scoped singleton resource, or application/session spec defaults?
- What is the initial CRD version-conversion strategy once `v1alpha1` evolves: no conversion until `v1beta1`, KubeOps conversion webhooks, or strict additive-only schema changes?
