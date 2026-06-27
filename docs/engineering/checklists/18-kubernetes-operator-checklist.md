# 18 — Kubernetes Operator Checklist

> **Scope:** KubeOps operator code, CRDs, reconcilers, admission webhooks, finalizers, status subresources, RBAC, generated manifests, driver/executor/shuffle lifecycle, scaling, rollout, rollback, and operator tests.
> **Priority:** HIGH.
> **Owners:** kubernetes-operator-controller-engineer, cloud-native-site-reliability-engineer, cloud-native-security-sme. **Grounded in:** `.github/copilot-instructions.md`, `review-pr/rating-rubric.md`, ADR-0003, ADR-0004, ADR-0009.

## How to use
Use this checklist for every change to operator code or generated Kubernetes API surfaces. Reconciler behavior that can corrupt status, delete running jobs, leak tenant resources, or orphan pods is a red flag; pair with 05 security, 14 tenant isolation, and 10 runtime lifecycle.

## Checklist
### CRD API design and versioning
- [ ] `DeltaSharpApplication` and `DeltaSharpSession` schemas are explicit public contracts with stable spec/status fields, defaults, enums, nullable behavior, and pruning rules.
- [ ] CRD changes preserve compatibility or include conversion strategy, deprecation plan, migration notes, and tests for old and new versions.
- [ ] Required fields, resource bounds, image references, service accounts, storage references, tenant identifiers, and shuffle settings are validated by OpenAPI schema and webhooks.
- [ ] Status fields use conditions with type, status, reason, message, severity, observed generation, and last transition time where appropriate.
- [ ] `observedGeneration` discipline prevents stale status from appearing current after spec changes.
- [ ] Examples include realistic service accounts, images, resources, executor counts, storage references, network assumptions, and tenant boundaries.

### Level-triggered reconciliation
- [ ] Reconcile loops are level-triggered and converge actual cluster state to desired state after missed events, duplicate events, restarts, API conflicts, or stale caches.
- [ ] Reconciles are idempotent: repeated execution does not multiply pods, services, config maps, events, secrets references, finalizers, or cleanup work.
- [ ] Reconciler code does not block on long-running jobs, pod readiness waits, log streaming, data-plane RPC, sleeps, or hidden in-memory state.
- [ ] Child resources are selected by immutable labels, owner references, UID/generation annotations, and collision-resistant names.
- [ ] Status updates are conflict-aware, retried safely, and do not clobber conditions written by another reconciliation path.
- [ ] Errors are classified as terminal spec failures, retryable infrastructure failures, policy denials, and transient API conflicts with appropriate requeue/backoff behavior.

### Finalizers, cleanup, and lifecycle safety
- [ ] Finalizers are added before external or child resources are created and removed only after cleanup is complete or safely delegated.
- [ ] Deletion handles driver pods, executor pods, services, config maps, PVC references, status preservation, events, and shuffle dependencies in a resumable order.
- [ ] Interactive sessions and batch applications preserve terminal status and user-visible failure/completion state before teardown.
- [ ] The operator never deletes live workloads, another tenant's resources, or non-owned resources because of selector drift, namespace changes, or owner-reference mistakes.
- [ ] Garbage collection is used deliberately but not as the only mechanism when ordered drain, final status, or external cleanup is required.
- [ ] Namespace deletion, stuck finalizers, operator restarts during finalization, and API conflicts are covered by tests or failure-mode analysis.

### Admission and validation webhooks
- [ ] Webhooks are deterministic, side-effect-free, bounded, version-compatible, and explicit about failure policy.
- [ ] Validation rejects impossible or unsafe specs before reconcile: invalid service account, forbidden namespace, excessive resources, untrusted image, unsafe storage reference, and unsupported option combinations.
- [ ] Mutation/defaulting is minimal, documented, and does not silently change tenant, security, resource, or storage semantics.
- [ ] Webhook TLS certificates, rotation, service wiring, and deployment ordering are documented and tested.
- [ ] Webhook errors are actionable, tenant-safe, and avoid exposing secret names or unavailable resources from other namespaces.
- [ ] Webhook latency and failure modes are observable so admission issues do not look like stuck jobs.

### RBAC, security, and tenant isolation
- [ ] Operator RBAC is least-privilege for CRDs, status subresources, pods, services, config maps, daemonsets, events, leases, and secret references.
- [ ] The operator reads or references secrets only when required and never logs secret data, object-store credentials, or projected tokens.
- [ ] Namespace scope, cluster scope, leader-election leases, service accounts, role bindings, and webhook permissions are intentionally chosen.
- [ ] Tenant namespace, service account, network policy, quota, and image policy constraints are enforced or surfaced before child resources are created.
- [ ] Driver/executor/shuffle resources inherit tenant-safe labels, annotations, service accounts, network policy selectors, and resource budgets.
- [ ] Cross-check 05 and 14 before granting broad RBAC, secret access, storage references, or cluster-wide watch privileges.

### Driver, executor, and shuffle lifecycle
- [ ] Driver and executor pod creation, scaling, readiness, restart policy, image pull, node placement, PVC binding, and failure state are represented in status.
- [ ] Executor scaling is auditable: desired, actual, pending, failed, last scale reason, constraints, and backoff are visible.
- [ ] Shuffle workers are managed as a DaemonSet per ADR-0004, with status showing availability and compatibility without making the operator a data plane.
- [ ] The operator wires gRPC control and Arrow Flight data-plane ports/services per ADR-0003 without owning task dispatch or streaming data.
- [ ] Pod disruption, node drain, spot interruption, readiness removal, and graceful termination requirements align with 10 runtime behavior.
- [ ] Dynamic allocation or future cluster/pool CRDs are introduced behind compatible APIs and do not break fixed-executor assumptions.

### Rollout, rollback, and testing
- [ ] Operator upgrades use leader election, canaries or phased rollout, CRD compatibility checks, webhook readiness gates, and rollback criteria.
- [ ] Generated manifests, Helm charts, and Terraform modules are reviewable, pinned, and consistent with 13 infrastructure-as-code controls.
- [ ] Tests cover reconcile replay, fake-client or API-server behavior, status conflicts, admission validation, finalizers, deletion, namespace isolation, and version conversion.
- [ ] Metrics, logs, traces, and events include CRD namespace/name, UID, generation, reconcile attempt, child resource, and reason without leaking credentials or tenant data.
- [ ] Runbooks explain stuck reconciles, webhook failures, image pull failures, quota failures, driver/executor churn, and stuck finalizers.
- [ ] Safe rollback preserves CRD objects, status, running jobs, and child-resource ownership; rollback never requires deleting live workloads as the default recovery path.

## Anti-patterns (red flags)
- Reconciler logic can delete running jobs, another tenant's pods, non-owned resources, or live shuffle workers because of selector or owner-reference mistakes.
- Reconcile depends on edge events, sleeps, local memory, or a previous loop completing instead of actual API-server state.
- CRD schema changes break existing clients or stored objects without conversion and migration plan.
- Finalizers can wedge forever, skip cleanup, or remove evidence before terminal status is written.
- Admission webhooks mutate security, tenant, resource, or storage semantics silently.
- Operator RBAC grants broad secret, pod, namespace, or cluster-admin privileges without proof of necessity.

## References
- [05 — Security Checklist](05-security-checklist.md)
- [14 — Tenant Isolation Checklist](14-tenant-isolation-checklist.md)
- [10 — Runtime Environment Checklist](10-runtime-environment-checklist.md)
- [13 — Infrastructure as Code Checklist](13-infrastructure-as-code-checklist.md)
- ADR-0003: Data-plane transport
- ADR-0004: Shuffle architecture
- ADR-0009: Kubernetes Operator and CRD design
- `docs/persona/agents/kubernetes-operator-controller-engineer-agent.md`
- `docs/persona/agents/cloud-native-site-reliability-engineer-agent.md`
