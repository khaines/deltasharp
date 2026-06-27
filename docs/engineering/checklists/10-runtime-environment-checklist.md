# 10 — Runtime Environment Checklist

> **Scope:** .NET runtime configuration, NativeAOT executor images, driver/executor/shuffle containers, Kubernetes probes, shutdown, resources, CI workflow permissions, and deployment-time runtime settings.
> **Priority:** STANDARD.
> **Owners:** dotnet-distributed-execution-engineer, cloud-native-site-reliability-engineer, cloud-native-security-sme. **Grounded in:** `.github/copilot-instructions.md`, `review-pr/rating-rubric.md`, ADR-0003, ADR-0004, ADR-0014.

## How to use
Use this checklist for container images, runtime hosts, executor or driver process changes, Kubernetes pod specs, CI workflows, and runtime configuration. Cross-check 18 for operator-managed lifecycle, 13 for manifests/IaC, 05 for hardening, and 14 for tenant boundaries.

## Checklist
### .NET target, AOT, and runtime configuration
- [ ] Engine and executor projects target `net10.0`; public-facing libraries multi-target `net8.0;net10.0` and remain trim/AOT-annotation-clean per ADR-0014.
- [ ] NativeAOT executor image builds without dynamic-code dependencies; optional JIT/codegen paths are guarded and elidable.
- [ ] `runtimeconfig.json`, feature switches, globalization, invariant mode, EventPipe, diagnostics, and reflection settings are intentional and documented.
- [ ] `DOTNET_` environment variables are set only when needed, version-controlled, and tested under container limits.
- [ ] Runtime configuration does not encode secrets, tenant data, storage credentials, or environment-specific mutable policy.
- [ ] Driver, executor, and shuffle-worker hosts expose version, protocol, and feature compatibility without requiring privileged diagnostics.

### Cgroup-aware CPU and memory behavior
- [ ] GC heap hard limit, memory load, buffer pools, Arrow buffers, spill thresholds, and cache sizes respect Kubernetes memory limits.
- [ ] Thread-pool minimums, executor slots, task queues, gRPC stream limits, and data-plane concurrency respect CPU requests/limits and cgroup quotas.
- [ ] OOM risk is mitigated by bounded queues, backpressure, memory accounting, spill policy, and tenant/job budgets.
- [ ] Metrics expose GC pauses, allocation rate, LOH/POH pressure, thread-pool starvation, queue depth, and memory-limit saturation.
- [ ] Defaults are validated for small, medium, and large executor pods rather than assuming bare-metal resources.
- [ ] ResourceExhausted and cancellation paths fail jobs safely without corrupting Delta commits or shuffle registry state.

### Container image hardening
- [ ] Runtime images are minimal, pinned by digest, vulnerability-scanned, SBOM-producing, and compatible with release signing/provenance controls in 05.
- [ ] Containers run as non-root with dropped Linux capabilities, read-only root filesystem, explicit writable mounts, and no package manager or build tools in runtime layers.
- [ ] File ownership, UID/GID, `fsGroup`, supplemental groups, and volume permissions are explicit for spill, shuffle, cache, logs, and projected secrets.
- [ ] Images do not include test data, sample credentials, source-control metadata, private feeds, or unused debugging tools.
- [ ] Native libraries, ICU/globalization assets, TLS roots, and Arrow/Flight dependencies are present only as required and tracked.
- [ ] Startup command, args, environment variables, and mounted config are deterministic and reviewable.

### Kubernetes probes and lifecycle
- [ ] Startup probes cover cold initialization, config load, certificate availability, protocol binding, and required dependency readiness without hiding permanent failures.
- [ ] Readiness probes fail before shutdown begins and when the process cannot accept new work, register executors, serve tasks, or satisfy data-plane requests.
- [ ] Liveness probes detect deadlocks or unrecoverable hangs but do not flap during recoverable saturation, backpressure, long GC, or storage degradation.
- [ ] Probes are cheap, bounded, unauthenticated only when safe, and do not expose secrets, row data, plans, or privileged status.
- [ ] Probe ports and endpoints are distinct from sensitive gRPC/Arrow Flight paths or protected by policy when they share a server.
- [ ] Probe behavior is represented in operator status and runbooks so failures are actionable.

### Graceful shutdown and drain
- [ ] SIGTERM handling removes readiness, stops accepting new work, propagates cancellation, drains or checkpoints tasks, flushes reports, and shuts down hosted services in order.
- [ ] `terminationGracePeriodSeconds` exceeds preStop delay, readiness propagation, host `ShutdownTimeout`, task drain, shuffle drain-migration, and telemetry flush budgets.
- [ ] Executor and shuffle-worker drain paths integrate with ADR-0004 migration/replication and do not pin stale shuffle locations.
- [ ] Driver shutdown preserves job/session terminal status, outstanding task state, and Delta commit safety.
- [ ] Forced termination, node drain, spot interruption, and pod eviction paths are tested or covered by failure-injection scenarios.
- [ ] Shutdown logs and metrics are concise, correlated, and free of credentials or personal data.

### Resource requests, limits, and scheduling
- [ ] CPU, memory, ephemeral-storage, PVC, and object-store usage assumptions are translated into Kubernetes requests, limits, quotas, and limit ranges.
- [ ] Driver, executor, and shuffle-worker pod specs include placement, affinity/anti-affinity, tolerations, topology, priority, and disruption expectations when relevant.
- [ ] Ephemeral-storage limits cover spill, logs, shuffle temp files, caches, Arrow IPC blocks, diagnostics, and crash dumps.
- [ ] Autoscaling or fixed executor counts are bounded by tenant budgets and do not bypass 14 isolation controls.
- [ ] Resource defaults have safe behavior under constrained clusters and produce actionable admission/status errors when unschedulable.
- [ ] FinOps-visible resource metrics can attribute runtime consumption by job, stage, executor, table, and tenant without privacy leaks.

### CI workflows and release runtime safety
- [ ] CI builds, tests, formats, containerizes, signs, scans, and publishes artifacts with least-privilege workflow permissions.
- [ ] Pull-request workflows cannot access production secrets, signing keys, cloud credentials, or privileged package feeds.
- [ ] NativeAOT, trim analysis, container build, SBOM generation, and vulnerability scanning run as gates for executor images.
- [ ] Workflow dependencies and actions are pinned; generated artifacts are reproducible and tied to commit SHA.
- [ ] Runtime compatibility tests exercise gRPC control plane, Arrow Flight data plane, shutdown, probes, and cgroup behavior.
- [ ] CI logs redact credentials and avoid dumping full plans, object-store URLs, tenant data, or personal data.

## Anti-patterns (red flags)
- Executor images target the wrong framework, require dynamic code under NativeAOT, or fail trim/AOT analysis.
- Containers run as root, writable-root by default, broad capabilities, or include build tools and package managers in runtime images.
- Unbounded queues, caches, Arrow buffers, or thread pools ignore cgroup memory and CPU limits.
- Liveness probes kill healthy-but-saturated jobs; readiness remains true during drain or after task acceptance has stopped.
- `terminationGracePeriodSeconds` is shorter than drain, shutdown, and shuffle migration budgets.
- CI workflows expose secrets to pull requests or publish unsigned/unscanned runtime artifacts.

## References
- [18 — Kubernetes Operator Checklist](18-kubernetes-operator-checklist.md)
- [13 — Infrastructure as Code Checklist](13-infrastructure-as-code-checklist.md)
- [05 — Security Checklist](05-security-checklist.md)
- [14 — Tenant Isolation Checklist](14-tenant-isolation-checklist.md)
- ADR-0003: Data-plane transport
- ADR-0004: Shuffle architecture
- ADR-0014: Target framework and AOT posture
- `docs/persona/agents/dotnet-distributed-execution-engineer-agent.md`
- `docs/persona/agents/cloud-native-site-reliability-engineer-agent.md`
