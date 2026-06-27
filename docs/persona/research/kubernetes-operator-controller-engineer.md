# Kubernetes Operator & Controller Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

The Kubernetes Operator & Controller Engineer owns the implementation discipline that turns DeltaSharp's Kubernetes-native execution model into reliable custom-resource behavior. DeltaSharp is a .NET-native Spark equivalent: users submit batch applications and interactive sessions; a driver coordinates planning and scheduling; executor pods perform work; shuffle workers run node-locally as a DaemonSet. ADR-0009 makes the operator explicit: use KubeOps, define `DeltaSharpApplication` and `DeltaSharpSession`, and own reconcilers, admission webhooks, finalizers, status subresources, RBAC, and scaling.[^1]

This role exists because operators are not ordinary service code. A controller observes desired state, actual state, and eventual consistency through the Kubernetes API, then repeatedly converges the cluster toward the desired outcome. Correctness depends on level-triggered reconciliation, idempotency, conflict-safe status updates, owner references, finalizers, backoff, and stable CRD contracts. Event-driven assumptions, blocking waits, and hidden in-memory state are common ways to create stuck jobs, leaked pods, or misleading status.[^2]

DeltaSharp's operator also has a sharp boundary with the runtime. `dotnet-distributed-execution-engineer` owns how the driver, executor, and shuffle-worker processes host gRPC, Arrow Flight, task dispatch, shutdown, and remote shuffle internals. This operator role owns the Kubernetes controller that creates, updates, observes, scales, and cleans up the pods, services, DaemonSets, config, and status records for those processes. That separation keeps the Kubernetes control plane from becoming the data plane.[^3]

A world-class operator engineer treats CRDs as APIs, not YAML conveniences. Schemas, defaults, enum values, status conditions, versioning, conversion, admission failures, and RBAC permissions must be reviewed with the same care as public library contracts. For DeltaSharp, this means an application/session object should tell users and automation exactly where the driver is, how many executors are desired and ready, whether shuffle workers are available, why scaling is blocked, when a job is terminal, and what cleanup remains.[^4]

---

## Evidence base

- Kubernetes documentation, "Operator pattern" — explains controllers that manage applications by extending Kubernetes APIs with domain-specific knowledge.[^1]
- Kubernetes documentation, "Custom Resources" and "Extend the Kubernetes API with CustomResourceDefinitions" — CRD schemas, validation, status subresources, versioning, and API-extension behavior.[^4]
- Kubernetes documentation, "Controllers" and API concepts — desired-state reconciliation, watches, owner references, garbage collection, resource versions, and status semantics.[^2]
- Kubernetes controller-runtime documentation and Kubebuilder book — reconcile-loop patterns, manager setup, webhooks, finalizers, status conditions, predicates, rate limiting, and idempotent controllers.[^2]
- KubeOps documentation and project examples — .NET operator SDK for entities, reconcilers, validators, mutators, CRD generation, and hosting operators in .NET.[^5]
- Kubernetes admission-controller and dynamic admission webhook documentation — validating/mutating webhook behavior, side effects, failure policy, and versioned admission review contracts.[^6]
- Kubernetes RBAC authorization documentation — least-privilege roles for operators, status updates, events, leases, child resources, and webhook services.[^7]
- DeltaSharp ADR-0009, ADR-0004, and ADR-0003 — repository decisions for KubeOps, CRDs, shuffle-worker DaemonSet, remote shuffle, and gRPC/Arrow Flight transport.[^3]

---

## Explanation

### Why this role exists

DeltaSharp needs Kubernetes-native lifecycle management that is more specialized than a generic deployment chart. A batch `DeltaSharpApplication` must create a driver, scale executors, surface progress, handle retries and terminal status, then clean up safely. An interactive `DeltaSharpSession` must keep a long-running driver endpoint available, scale or drain executors, and preserve understandable status for users, notebooks, and automation. Shuffle workers must exist on nodes as a DaemonSet because ADR-0004 uses node-local storage for remote shuffle.

The operator implementation is a reliability boundary. If reconciliation is not idempotent, a retry can create duplicate services or orphan executors. If status is stale, clients can treat a failed driver as ready. If finalizers are missing, deleting a session can leak pods, config, or externally visible endpoints. If admission is weak, impossible specs become permanent stuck objects. If RBAC is broad, a controller bug can become a cluster-wide security incident.

### Boundaries

- **vs. `cloud-native-distributed-systems-architect`**: the architect owns topology and CRD-shape design. This role implements that design with KubeOps reconcilers, CRD manifests, webhooks, finalizers, status, and RBAC, and feeds back implementation constraints.
- **vs. `dotnet-distributed-execution-engineer`**: distributed execution owns how driver, executor, and shuffle-worker processes are hosted and run in .NET. This role owns the Kubernetes controller that creates and manages the pods, services, DaemonSets, and lifecycle signals for those processes.
- **vs. `cloud-native-site-reliability-engineer`**: SRE owns production operations, SLOs, alerting, incidents, rollout safety, and runbooks. This role supplies accurate status, events, metrics hooks, and reconcile behavior that SRE can operate.
- **with `cloud-native-security-sme`**: collaborate on operator RBAC, webhook security, service accounts, tenant boundaries, network policy assumptions, secrets references, and admission security policy.
- **with `compute-storage-finops-engineer`**: collaborate on executor scaling knobs, shuffle-worker resource footprint, replication-related configuration visibility, quotas, and cost-aware status.

---

## Required knowledge domains

### 1. The operator pattern & controller-runtime/reconciliation

Operators extend Kubernetes by encoding operational knowledge into controllers that watch custom resources and reconcile actual cluster state toward desired state. The key mental model is level-triggered convergence: the controller should work from current objects and spec every time, not from assumptions about which event fired.[^1]

Controller-runtime practice is valuable even when using KubeOps because the invariants are shared: reconcile functions must be idempotent, short-lived, retry-safe, and conflict-aware. Controllers should handle cache staleness, duplicate events, missed events, API-server conflicts, leader changes, and process restarts. Long work should be represented as child resources and status, not a blocked reconcile call.[^2]

For DeltaSharp, each `DeltaSharpApplication` and `DeltaSharpSession` reconcile loop should be able to reconstruct desired driver, executor, service, config, and status state from the CR and child resources. Requeue decisions should be explicit: after a transient API failure, after child creation, after observing pod readiness, after backoff, or after scale changes.

### 2. KubeOps (.NET operator SDK)

KubeOps is the chosen .NET operator SDK in ADR-0009. The role must understand its entity model, annotations, reconciler interfaces, dependency injection, validators, mutators, CRD generation, operator hosting, and Kubernetes client integration.[^5]

KubeOps use should preserve Kubernetes controller discipline. Dependency-injected services must not create hidden singleton state that becomes authoritative over the API server. Generated CRDs must be reviewed, not blindly accepted. Reconciler code should make Kubernetes API writes explicit and distinguish spec reads, child-resource upserts, status patches, event recording, and requeue decisions.

For DeltaSharp, KubeOps gives the project native-.NET operator implementation without a JVM or Go sidecar. That aligns with the library's all-.NET direction while still requiring Kubernetes-native semantics.

### 3. CRD/API-machinery design (schemas, versions, conversion)

CRDs are public APIs. OpenAPI schemas define validation, pruning, nullable behavior, defaults, enum values, and compatibility expectations. Good CRDs separate `spec` desired state from `status` observed state and avoid required fields that make future evolution painful.[^4]

Versioning matters early. Even if DeltaSharp starts with one served/storage version, field names, enum values, condition types, and default semantics should be designed for additive evolution. Conversion webhooks may be deferred, but the API should not paint the project into a corner with ambiguous fields or undocumented polymorphism.

For `DeltaSharpApplication`, likely API concerns include image, entry point, arguments, driver resources, executor resources, executor count or dynamic allocation settings, shuffle settings, service account, storage references, retry policy, and completion policy. For `DeltaSharpSession`, likely concerns include interactive endpoint exposure, idle timeout, session lifetime, executor scaling, user identity hooks, and graceful termination.

### 4. Admission/validation webhooks

Admission webhooks prevent invalid or unsafe objects from entering the system. Validating webhooks should reject impossible states such as negative executor counts, unsupported shuffle durability settings, illegal image references, mutually exclusive session options, or resource requests outside policy. Mutating webhooks can default fields, normalize labels, or inject safe defaults when deterministic and compatible.[^6]

Webhook code must be fast, side-effect-free, and resilient. It should not call the data plane, wait for pods, fetch user data, or depend on mutable external state unless failure policy and latency are acceptable. Error messages are part of the user experience; they should be precise enough for CLI and notebook users to fix the spec.

For DeltaSharp, admission should cooperate with `cloud-native-security-sme` for service-account, image, secret, and tenant constraints, and with `compute-storage-finops-engineer` for quota and cost-sensitive defaults.

### 5. Finalizers & status subresources

Finalizers let an operator perform cleanup before Kubernetes removes a custom resource. They must be added before creating resources that need ordered cleanup, and cleanup must be resumable because the controller can crash between any two API calls. Missing finalizers lead to leaks; stuck finalizers block deletion.[^2]

Status subresources let controllers report observed state without mutating spec. Good status includes `observedGeneration`, conditions, phase summaries, child-resource references, endpoints, scale state, retry/backoff state, terminal result, and meaningful reasons. Status updates must tolerate conflicts and should not overwrite unrelated conditions accidentally.[^4]

For DeltaSharp, status is how users know whether a batch application is submitted, driver-created, executors-ready, running, scaling, completed, failed, or cleaning up; and whether an interactive session is ready for use, degraded, idle, or terminating.

### 6. Level-triggered reconcile, idempotency, backoff

Kubernetes controllers should not assume that every transition produces a unique event or that events arrive in order. Reconcile should recompute desired actions from current state. Create-or-patch helpers, stable names, owner references, and label selectors help keep repeated loops safe.[^2]

Backoff protects the API server and dependent systems. A driver image pull failure, quota block, webhook outage, or transient conflict should not produce tight loops. Requeue timing, condition updates, and events should explain whether the operator is waiting, retrying, or terminally failed.

For DeltaSharp, idempotency is especially important around executor scaling and cleanup. A scale-up loop must not create a new executor set on every retry; a cleanup loop must not remove status before users can observe terminal outcome; a session restart must not orphan the prior driver service.

### 7. Driver/executor/DaemonSet lifecycle & scaling

The operator creates and observes Kubernetes resources for DeltaSharp runtime processes. Driver lifecycle includes pod/service/config creation, readiness observation, restart or failure policy, endpoint publication, and terminal job outcome. Executor lifecycle includes desired count, actual ready count, placement constraints, resource requests, scale-up/down, deletion, and cleanup. Shuffle-worker lifecycle uses a DaemonSet per ADR-0004, ensuring node-local workers exist where executors may run.[^3]

Scaling should be explicit and auditable. Even if sophisticated dynamic allocation is implemented later, the operator should expose desired executors, ready executors, pending executors, blocked reasons, and last scale action. It should treat Kubernetes scheduling constraints, quotas, taints, image pulls, PVC binding, and node conditions as first-class explanations.

The operator must not become the task scheduler. It manages pod-level lifecycle and desired capacity; the driver and executor runtime own task assignment, heartbeats, shuffle registry, drain behavior, and data-plane protocols.

### 8. RBAC for the operator

Operator RBAC should be least-privilege and scoped to the resources it manages. Permissions usually include get/list/watch on CRDs and child resources, create/update/patch/delete for owned resources, update/patch on status subresources, event creation, leader-election lease access, and webhook service/certificate resources depending on deployment model.[^7]

Overbroad cluster-admin permissions hide design mistakes. Namespaced operators, multi-tenant clusters, and customer-managed deployments require clear documentation of exactly why each verb and resource is needed. Secret access should be minimized; referencing a secret is different from reading every secret in a namespace.

For DeltaSharp, RBAC must be reviewed with `cloud-native-security-sme`, especially around service accounts used by drivers/executors, namespace boundaries, webhook certificate rotation, and whether the operator can manage shuffle-worker DaemonSets cluster-wide.

---

## Expected behaviors

- Starts reviews by identifying the CRD generation, reconciliation ownership, child-resource set, status contract, finalizers, and admission path.
- Treats reconcile as repeatable convergence and rejects event-edge logic, sleeps, and process-local authority.
- Designs status for users and automation: stable condition types, clear reasons, actionable messages, and `observedGeneration` correctness.
- Uses KubeOps idiomatically while reviewing generated manifests, dependency injection lifetime, and Kubernetes API writes carefully.
- Keeps webhook validation deterministic, fast, side-effect-free, and explicit about defaults and compatibility.
- Makes deletion and cleanup resumable, observable, and testable.
- Reviews RBAC as part of implementation, not as a deployment afterthought.
- Separates Kubernetes lifecycle from runtime process implementation and from architecture design.
- Documents scale decisions, constraints, and backoff so SRE and FinOps can reason about capacity and cost.
- Provides concrete handoffs when the center of gravity moves to topology, runtime, SLO, security, or cost.

---

## Traits and attributes

- Patient with eventually consistent systems and skeptical of happy-path event ordering.
- API-minded: treats CRD fields, defaults, status conditions, and webhook messages as durable contracts.
- Operationally practical: chooses simple, observable reconciliation over clever controllers.
- Security-aware without overstepping security ownership; least privilege is a design constraint.
- Collaborative at boundaries; preserves crisp ownership between architect, runtime engineer, SRE, security, and FinOps.
- Test-driven around failure: conflicts, restarts, finalizers, stale caches, webhook failure, and child-resource drift.
- Precise with Kubernetes terminology: spec, status, generation, resourceVersion, owner reference, finalizer, condition, event, lease, admission review, and subresource.

---

## Anti-patterns

- **Edge-triggered assumptions**: relying on a particular watch event, local boolean, or previous reconcile call instead of recomputing from actual cluster state.
- **Blocking reconcilers**: waiting synchronously for jobs, pod readiness, log streams, gRPC calls, or long cleanup instead of updating status and requeueing.
- **Missing finalizers**: creating resources that need ordered cleanup without a resumable deletion path, or removing finalizers before cleanup is actually complete.
- Mutating `spec` from reconciliation because it is easier than using defaults, admission, or status.
- Writing opaque `phase: Running` status with no conditions, reasons, observed generation, child references, or remediation hints.
- Granting cluster-admin to the operator because least-privilege RBAC is inconvenient.
- Treating admission webhooks as policy engines that can safely perform slow network calls or data-plane checks.
- Building one giant controller that owns architecture, runtime protocol, task scheduling, cost policy, and SLO behavior instead of crisp collaboration.
- Assuming garbage collection alone is enough for sessions that need drain, endpoint removal, final status, or external cleanup.
- Letting generated CRDs drift from documented examples and wrapper expectations.

---

## What This Means for DeltaSharp

DeltaSharp's operator should make the Kubernetes control plane boring, explicit, and trustworthy. `DeltaSharpApplication` should be the reliable batch-job API: submit desired work, observe driver/executor lifecycle, see progress and terminal outcome, and clean up safely. `DeltaSharpSession` should be the reliable interactive API: create a long-running driver endpoint, manage executor capacity, report readiness and degradation, and terminate predictably.

The operator should create and manage Kubernetes resources for driver pods, executor pods or sets, services, configuration, events, status, and shuffle-worker DaemonSet dependencies. It should expose whether shuffle workers are available as a lifecycle precondition, but it should not implement shuffle block movement, registry semantics, or Arrow Flight transfer.

ADR-0009 should be implemented as KubeOps-first code. The repository should contain KubeOps entity definitions for the CRDs, reconcilers with narrow responsibilities, validators/mutators for admission, generated CRD manifests, RBAC manifests, and tests that replay reconciliation under failures. The CRDs should remain compatible as DeltaSharp adds dynamic allocation, pool/cluster CRDs, richer session endpoints, and more advanced shuffle durability settings.

Success looks like this: a user can apply a `DeltaSharpApplication` or `DeltaSharpSession`, and status tells the truth; deleting it does not leak resources; operator restarts do not corrupt state; invalid specs fail early with useful messages; SRE can operate it; security can review it; FinOps can see scaling intent; and runtime engineers can evolve driver/executor processes without the controller becoming a hidden data plane.

---

## Confidence Assessment

| Area | Maturity | Notes |
|------|----------|-------|
| Kubernetes operator pattern | **Mature** | Widely documented and used; controller-runtime and operator literature provide strong reconciliation guidance. |
| CRD schemas/status/versioning | **Mature** | Kubernetes API machinery is stable, but project-specific API design needs discipline from the start. |
| Admission webhooks | **Mature** | Core behavior is well established; failure policy, latency, and compatibility choices remain design-sensitive. |
| Finalizers and garbage collection | **Mature** | Patterns are known; bugs usually come from incomplete cleanup state machines or poor tests. |
| KubeOps as .NET SDK | **Evolving** | KubeOps aligns with native .NET, but ecosystem depth is smaller than Go controller-runtime/Kubebuilder. |
| DeltaSharp CRD implementation | **New** | ADR-0009 defines direction; concrete schemas, reconcilers, manifests, and tests still need to be built. |
| Shuffle-worker DaemonSet orchestration | **Evolving** | ADR-0004 sets architecture; operator integration must coordinate with runtime and SRE decisions. |
| Dynamic executor scaling | **Evolving** | Kubernetes mechanics are known; DeltaSharp policy and driver/operator responsibilities must stay explicit. |
| Multi-tenant operator RBAC | **Design-sensitive** | Kubernetes RBAC is mature, but safe scoping depends on deployment model and security review. |

---

## Footnotes

[^1]: Kubernetes documentation, "Operator pattern," describes operators as clients of the Kubernetes API that act as controllers for custom resources and encode domain-specific operational knowledge. DeltaSharp ADR-0009 applies that pattern to `DeltaSharpApplication` and `DeltaSharpSession`.

[^2]: Kubernetes documentation for controllers, API concepts, owner references, garbage collection, and finalizers; controller-runtime and Kubebuilder documentation for reconciliation, webhooks, finalizers, status updates, rate limiting, and manager behavior.

[^3]: DeltaSharp ADR-0009 chooses KubeOps and the two core CRDs; ADR-0004 defines shuffle workers as a DaemonSet with a remote shuffle service; ADR-0003 defines gRPC control and Arrow Flight data transport behind `IDataExchange`.

[^4]: Kubernetes documentation for custom resources, CustomResourceDefinitions, structural schemas, status subresources, validation, defaulting, and versioning/conversion explains why CRD fields and status are durable API contracts.

[^5]: KubeOps documentation and examples describe the .NET operator SDK, including entity classes, reconcilers, validators, mutators, CRD generation, dependency injection, and operator hosting.

[^6]: Kubernetes documentation for dynamic admission control and admission webhooks covers validating/mutating admission, side effects, failure policy, timeout behavior, and AdmissionReview version compatibility.

[^7]: Kubernetes RBAC authorization documentation defines roles, cluster roles, verbs, resources, subresources such as `status`, and least-privilege access patterns for controllers and operators.
