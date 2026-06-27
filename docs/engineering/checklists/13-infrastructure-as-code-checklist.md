# 13 — Infrastructure as Code Checklist

> **Scope:** Helm charts, Kubernetes manifests, Kustomize overlays, Terraform/OpenTofu modules, CI-generated deployment artifacts, environment configuration, RBAC/IAM, secrets references, quotas, and drift controls.
> **Priority:** STANDARD.
> **Owners:** cloud-native-site-reliability-engineer, cloud-native-security-sme, compute-storage-finops-engineer, kubernetes-operator-controller-engineer. **Grounded in:** `.github/copilot-instructions.md`, `review-pr/rating-rubric.md`, ADR-0003, ADR-0004, ADR-0009, ADR-0014.

## How to use
Apply this checklist to infrastructure definitions that install or configure DeltaSharp on Kubernetes or cloud infrastructure. Validate runtime settings with 10, operator resources with 18, security with 05, and tenant isolation with 14.

## Checklist
### Version pinning and reproducibility
- [ ] Helm chart dependencies, container images, CRDs, Terraform providers, modules, Kubernetes API versions, GitHub Actions, and policy bundles are pinned to versions or digests.
- [ ] Generated manifests are reproducible from source and do not hide unreviewed changes in committed rendered output.
- [ ] Chart values, overlays, modules, and examples identify supported Kubernetes versions, cloud providers, storage backends, and DeltaSharp image versions.
- [ ] Upgrade paths document CRD ordering, webhook readiness, operator rollout, driver/executor image compatibility, and rollback constraints.
- [ ] Provider and module lock files are committed when applicable and reviewed as dependency changes.
- [ ] Defaults are deterministic across local, CI, staging, and production environments.

### Secrets and sensitive configuration
- [ ] IaC never commits plaintext cloud keys, object-store credentials, SAS tokens, connection strings, webhook certificates, signing keys, or tenant secrets.
- [ ] Charts and modules accept secret references, workload identity bindings, external secret operators, or cloud-native identity configuration instead of raw secret values.
- [ ] Rendered manifests, plan output, CI logs, Terraform state, Helm release history, and diff tooling redact or avoid sensitive values.
- [ ] Secret rotation, revocation, namespace scoping, and least-privilege access are expressible without changing application code.
- [ ] Example values use placeholders that cannot be mistaken for production credentials.
- [ ] Storage credentials and workload identities align with 05 security and 14 tenant boundaries.

### Least privilege RBAC and IAM
- [ ] Kubernetes RBAC for the operator, drivers, executors, shuffle workers, metrics, and webhooks grants only required verbs on required resource types and namespaces.
- [ ] Cloud IAM policies for S3, ADLS, GCS, KMS, registries, and logging are scoped by tenant, environment, bucket/container, prefix, key, and operation.
- [ ] Service accounts, workload identity bindings, role bindings, and trust relationships are environment- and namespace-specific.
- [ ] Break-glass roles are separated from normal runtime roles, time-limited, audited, and excluded from examples.
- [ ] NetworkPolicy, Pod Security, admission policy, image policy, and mTLS assumptions are represented explicitly rather than relying on cluster defaults.
- [ ] Privileged, hostPath, hostNetwork, broad node access, and cluster-admin settings require documented justification and compensating controls.

### Kubernetes resources and runtime settings
- [ ] Driver, executor, shuffle-worker, and operator deployments include resource requests/limits, probes, security contexts, termination grace periods, and read-only filesystem settings aligned with 10.
- [ ] NativeAOT executor image settings from ADR-0014 are selectable without weakening security or runtime constraints.
- [ ] Services, ports, and network policies support gRPC control and Arrow Flight data-plane traffic from ADR-0003 with mTLS-compatible routing.
- [ ] Shuffle DaemonSet, node-local storage, PVCs, and object-store fallback knobs align with ADR-0004 and do not create cross-tenant storage paths.
- [ ] Names, labels, annotations, selectors, owner references, and namespaces are collision-resistant and operator-compatible.
- [ ] CRDs, webhooks, leader-election resources, metrics endpoints, and service monitors align with 18 operator expectations.

### Environment parity and configuration management
- [ ] Dev, test, staging, and production overlays differ only through documented values such as size, region, tenant count, storage class, and policy strictness.
- [ ] Feature flags, runtime environment variables, log levels, retention windows, and admission policies have explicit defaults and environment-specific overrides.
- [ ] Object-store regions, PVC storage classes, KMS keys, backup settings, and lifecycle policies are declared and reviewable.
- [ ] Configuration supports private clusters, air-gapped or restricted-egress clusters, and provider-specific workload identity where claimed.
- [ ] Tenant onboarding creates namespaces, quotas, service accounts, network policies, storage prefixes, and budget metadata consistently.
- [ ] Examples and quickstarts are safe-by-default and do not teach users to disable mTLS, tenant isolation, pod security, or admission validation.

### Idempotency, plans, diffs, and drift
- [ ] Terraform applies, Helm upgrades, and manifest reconciliation are idempotent and safe to rerun after partial failure.
- [ ] Plans and diffs are reviewable, stable, and small enough to detect RBAC/IAM, secret, image, quota, and CRD changes.
- [ ] Drift detection covers Kubernetes objects, cloud IAM, object-store policies, KMS keys, buckets/containers, DNS, registries, and Helm releases.
- [ ] Manual changes are either blocked by policy or detected with owner, severity, and remediation guidance.
- [ ] Destroy operations are guarded to prevent deleting live Delta tables, object-store buckets, PVC data, running jobs, audit evidence, or production namespaces by mistake.
- [ ] State backends are encrypted, access-controlled, versioned, locked, and scoped per environment or tenant where applicable.

### Cost, quota, and operations
- [ ] Resource quotas, limit ranges, executor count bounds, shuffle storage limits, PVC classes, and object-store lifecycle settings are configurable per tenant/environment.
- [ ] Cost-attribution labels and annotations are standardized without embedding raw tenant data or personal data.
- [ ] Logging, metrics, tracing, audit, and retention settings produce operational evidence without unbounded storage growth.
- [ ] Alerts or policy checks detect runaway namespace growth, object-store request spikes, PVC saturation, idle drivers, and mis-sized executor defaults.
- [ ] Backup, restore, disaster recovery, and region-failover definitions are tested and do not violate residency or retention promises.
- [ ] Decommissioning runbooks clean up resources in a safe order and preserve required evidence.

## Anti-patterns (red flags)
- Plaintext secrets, credentials in values files, sensitive Terraform outputs, or unredacted plan artifacts committed or printed in CI.
- Floating image tags, unpinned providers, mutable chart dependencies, or unreviewable generated manifest blobs.
- Cluster-admin, wildcard IAM, broad secret reads, hostPath, privileged pods, or disabled admission controls in default installs.
- Environment-specific snowflakes that make staging unable to reveal production RBAC, mTLS, quota, or storage failures.
- IaC destroy or rollback can delete live jobs, Delta tables, object-store data, PVCs, audit logs, or another tenant's namespace.
- Examples that normalize disabling 05 security, 14 tenant isolation, 18 operator validation, or 10 runtime hardening.

## References
- [10 — Runtime Environment Checklist](10-runtime-environment-checklist.md)
- [18 — Kubernetes Operator Checklist](18-kubernetes-operator-checklist.md)
- [05 — Security Checklist](05-security-checklist.md)
- [14 — Tenant Isolation Checklist](14-tenant-isolation-checklist.md)
- [07 — Privacy Checklist](07-privacy-checklist.md)
- ADR-0003: Data-plane transport
- ADR-0004: Shuffle architecture
- ADR-0009: Kubernetes Operator and CRD design
- ADR-0014: Target framework and AOT posture
- `docs/persona/agents/compute-storage-finops-engineer-agent.md`
