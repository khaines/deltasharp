# 14 — Tenant Isolation Checklist

> **Scope:** Multi-tenant boundaries across planning, catalogs, storage, credentials, drivers, executors, shuffle, caches, logs, metrics, Kubernetes namespaces, service accounts, network policies, quotas, and admission controls.
> **Priority:** CRITICAL.
> **Owners:** cloud-native-security-sme, kubernetes-operator-controller-engineer, dotnet-distributed-execution-engineer, compute-storage-finops-engineer. **Grounded in:** `.github/copilot-instructions.md`, `review-pr/rating-rubric.md`, ADR-0003, ADR-0004, ADR-0009.

## How to use
Apply this checklist whenever a change can mix users, tenants, jobs, sessions, catalogs, storage paths, executors, caches, shuffle blocks, or Kubernetes resources. Any cross-tenant data access, credential reuse, cache leak, shuffle leak, or observability leak is Critical; coordinate with 05 security and 18 operator controls.

## Checklist
### Tenant model and identity
- [ ] The change states the tenant identifier source, trust level, lifetime, and propagation path from API/client through driver, executor, storage, catalog, shuffle, and observability.
- [ ] Tenant identity is explicit in job/session specs, execution context, plan analysis, catalog resolution, storage authorization, executor registration, and audit events.
- [ ] Tenant identity cannot be supplied solely by user-controlled labels, annotations, paths, SQL comments, or untrusted plan text.
- [ ] Driver, executor, shuffle-worker, and storage credentials are tenant- or job-scoped; shared credentials require an explicit brokered authorization layer.
- [ ] Tenant context is immutable for the lifetime of a job/session unless a documented administrative transition reauthorizes all resources.
- [ ] Tests cover negative cases where a job attempts to reuse another tenant's ID, namespace, service account, catalog, prefix, or shuffle handle.

### Plan, catalog, and metadata isolation
- [ ] Analyzer and optimizer resolution never lets one tenant access another tenant's catalog namespace, temporary view, function registry, table metadata, or Delta log.
- [ ] File listing, partition discovery, schema inference, and table existence checks are authorized before returning names, paths, counts, or errors.
- [ ] Logical plans, physical plans, `EXPLAIN`, query history, lineage, and cached plan fragments do not expose other tenants' identifiers, paths, schemas, or row statistics.
- [ ] Temporary views, sessions, functions, UDF registries, broadcast variables, and session configuration are scoped by tenant and session.
- [ ] Error messages for missing or unauthorized resources avoid oracle behavior that reveals another tenant's tables, prefixes, or namespaces.
- [ ] Catalog, table, and namespace caches are keyed by tenant identity and invalidated without cross-tenant bleed-through.

### Executor, credential, and runtime isolation
- [ ] Executors run under tenant/job-appropriate service accounts, environment, mounted secrets, projected tokens, and resource limits.
- [ ] Executor credential material cannot be reused by another tenant after pod reuse, image layer reuse, task retry, speculative execution, or failed cleanup.
- [ ] Task descriptors, serialized closures, UDF dependencies, and connector settings include only the credentials and metadata needed for that tenant/job.
- [ ] Broadcast variables, memory caches, disk caches, spill files, and checkpoint paths are tenant/job-scoped and cleaned or cryptographically separated before reuse.
- [ ] Driver and executor logs, metrics, traces, dumps, and profiles include correlation IDs but not cross-tenant data or credentials.
- [ ] Runtime shared services such as shuffle workers or registries enforce per-tenant authorization at every API boundary, not only at registration.

### Storage and shuffle isolation
- [ ] Object-store buckets, containers, prefixes, access points, keys, and encryption keys align with tenant boundaries and cannot be escaped by path normalization tricks.
- [ ] PVCs, storage classes, volume mounts, node-local disks, and hostPath/socket paths cannot expose another tenant's spill, cache, shuffle, or Delta files.
- [ ] Shuffle blocks are named, stored, replicated, migrated, listed, and fetched with tenant/job/stage/shuffle ownership checks.
- [ ] ADR-0004 dynamic shuffle location resolution never returns holders for another tenant's blocks, even during retry, drain, replica promotion, or registry failover.
- [ ] Object-store fallback for shuffle preserves tenant prefix, credential, encryption, retention, and cleanup isolation.
- [ ] Delta table data files and `_delta_log` metadata enforce the same tenant boundary; metadata-only reads are not exempt.

### Kubernetes and network controls
- [ ] Namespaces, service accounts, RBAC, role bindings, network policies, resource quotas, limit ranges, priority classes, and pod security settings are tenant-aware.
- [ ] The operator cannot create driver/executor pods in unauthorized namespaces or bind them to another tenant's service account.
- [ ] Network policy permits only required driver ↔ executor, executor ↔ shuffle worker, storage, catalog, and metrics paths; default cross-tenant pod communication is denied.
- [ ] Admission webhooks validate tenant, namespace, service-account, image, storage, executor count, and resource-budget constraints before reconcile.
- [ ] Owner references, labels, selectors, and generated names cannot collide across tenants or cause one tenant's controller actions to affect another tenant's pods.
- [ ] Deletion, finalization, namespace teardown, and garbage collection preserve isolation and cannot delete or orphan another tenant's live workloads.

### Resource budgets and noisy-neighbor control
- [ ] Per-tenant CPU, memory, executor count, driver count, shuffle storage, object-store request, and PVC capacity budgets are enforced or rejected before admission.
- [ ] Fair-scheduler pools, queue limits, priority, backpressure, and throttling cannot starve or preempt unrelated tenants without documented policy.
- [ ] Shuffle replication, drain-migration, object-store fallback, compaction, and retries are charged to the correct tenant/job and bounded.
- [ ] Runaway jobs, query bombs, skew, retry storms, and cache overuse are detected, attributed, and limited without corrupting results or Delta commits.
- [ ] Quota errors are surfaced as tenant/action-specific policy failures rather than generic infrastructure failures that hide noisy-neighbor impact.
- [ ] Cost and usage metrics support showback/chargeback without exposing other tenants' query text, table names, or object paths.

### Verification and observability
- [ ] Integration tests or policy tests prove negative isolation across catalogs, file listing, object-store prefixes, executor credentials, shuffle fetch, cache reuse, and logs.
- [ ] Metrics and traces include tenant-safe correlation and cardinality controls; raw tenant data is not used as metric labels.
- [ ] Audit logs capture denied cross-tenant attempts, successful tenant-scoped access, credential issuance, and operator admission decisions.
- [ ] Break-glass and administrative actions are time-limited, logged, reviewed, and excluded from normal tenant pathways.
- [ ] Isolation assumptions are documented for shared cluster, shared shuffle worker, shared object store, shared PVC class, and shared catalog deployments.
- [ ] Exceptions name the tenant boundary being relaxed, blast radius, owner, expiration, and compensating controls.

## Anti-patterns (red flags)
- Any path that lets tenant A read, write, list, infer, cache, fetch, delete, or observe tenant B's data, metadata, credentials, jobs, logs, metrics, or shuffle blocks.
- Tenant identity derived from untrusted strings such as raw paths, SQL text, labels, annotations, or client-supplied headers without authentication.
- Shared executors, shuffle workers, caches, credentials, or catalogs that rely on convention rather than enforced tenant keys and authorization.
- Object-store prefix concatenation without canonicalization and policy checks.
- Operator RBAC, finalizers, owner references, or selectors that can delete, scale, or orphan another tenant's workloads.
- Metric labels, traces, status, or `EXPLAIN` output that leaks another tenant's schema, path, credential, query, or resource usage.

## References
- [05 — Security Checklist](05-security-checklist.md)
- [18 — Kubernetes Operator Checklist](18-kubernetes-operator-checklist.md)
- [07 — Privacy Checklist](07-privacy-checklist.md)
- [13 — Infrastructure as Code Checklist](13-infrastructure-as-code-checklist.md)
- ADR-0003: Data-plane transport
- ADR-0004: Shuffle architecture
- ADR-0009: Kubernetes Operator and CRD design
- `docs/persona/agents/cloud-native-security-sme-agent.md`
- `docs/persona/agents/compute-storage-finops-engineer-agent.md`
