# 09b — Metrics Checklist

> **Scope:** Runtime, engine, storage, Delta, shuffle, Kubernetes Operator, and .NET process metrics used for SLOs, dashboards, alerts, capacity planning, and support.
> **Priority:** STANDARD.
> **Owners:** cloud-native-site-reliability-engineer, dotnet-runtime-performance-engineer, dotnet-distributed-execution-engineer. **Grounded in:** `.github/copilot-instructions.md`, `review-pr/rating-rubric.md`, 09a, 09c, 14, SRE and .NET runtime persona docs.

## How to use
Use this checklist when adding instruments, dashboards, alerts, counters, or runtime diagnostics. Escalate as High when missing or misleading metrics block production diagnosis, SLO ownership, or benchmark-regression interpretation.

## Checklist
### Instrumentation model
- [ ] Metrics use `System.Diagnostics.Metrics.Meter` for OpenTelemetry-compatible instruments in product code.
- [ ] EventCounters or runtime counters are exposed or documented where .NET tooling such as `dotnet-counters` is the operational path.
- [ ] Instruments are created once per component or library and reused rather than created per request, query, stage, task, or tenant.
- [ ] Metric names use the documented DeltaSharp prefix and stable dot-separated names.
- [ ] Units are explicit in the instrument name or metadata and use standard forms such as seconds, milliseconds, bytes, rows, tasks, executors, or operations.
- [ ] Metric descriptions identify the component, event, unit, and whether lower or higher is better.

### Engine SLIs and user-visible outcomes
- [ ] Query and action latency is measured as distributions with p50, p95, and p99 support, not only averages.
- [ ] Stage and task duration metrics include scheduling delay, execution time, retry count, and terminal outcome where applicable.
- [ ] Job success, failure, cancellation, timeout, and retry outcomes are counted separately.
- [ ] Shuffle bytes written, bytes read, fetch latency, re-resolution count, replica count, and fetch failure reasons are instrumented.
- [ ] Executor desired, running, ready, draining, failed, and lost counts are visible for driver and operator views.
- [ ] Delta commit latency, conflict count, retry count, committed version count, and failure classification are measured.
- [ ] Storage I/O request count, latency, throttling, timeout, bytes read, bytes written, and backend type are measured without exposing credentials.
- [ ] Runtime signals include allocation rate, GC pause time, GC heap size, thread-pool queue length, exception rate, and relevant EventPipe or EventCounter names.

### Instrument choice and semantics
- [ ] Counters are monotonic counts for events such as completed tasks, failed commits, bytes read, and retries.
- [ ] Histograms measure latency, duration, size, rows per batch, shuffle block size, and allocation-relevant distributions.
- [ ] Up-down counters or observable gauges represent current executor count, queue depth, active tasks, in-flight RPCs, and memory usage.
- [ ] Gauges are sampled from authoritative state and do not perform blocking I/O or expensive enumeration.
- [ ] Metrics distinguish user cancellation from internal timeout, transient infrastructure failure, correctness conflict, and programmer error.
- [ ] The same outcome is not double-counted by driver, executor, and operator unless the metric name clearly identifies the viewpoint.

### Naming, labels, and cardinality
- [ ] Attribute names are consistent across logs and traces from 09a and 09c.
- [ ] Labels use bounded sets for component, operation, backend, result, error class, stage type, and protocol.
- [ ] Metrics do not use unbounded labels such as raw query ID, tenant name, user ID, table path, SQL text, exception message, pod UID, object key, or shuffle block ID.
- [ ] Per-tenant attribution uses approved pseudonymous or bucketed identifiers and does not leak identity, matching 14.
- [ ] High-cardinality analysis uses logs, traces, exemplars, or sampling rather than metric labels.
- [ ] Label additions are reviewed for time-series cost, SLO usefulness, and tenant isolation risk before merge.

### SLOs, dashboards, and alerts
- [ ] Each page-worthy alert maps to a symptom, owner, severity, threshold, evaluation window, and runbook.
- [ ] Dashboard panels start from SLIs such as action latency, job success, Delta commit success, freshness, executor availability, and shuffle health.
- [ ] Burn-rate or multi-window alerting is used for SLOs where appropriate instead of single noisy thresholds.
- [ ] SRE owns dashboard and alert thresholds; implementation owners supply instrumentation and domain meaning.
- [ ] Dashboards can pivot from metric spikes to correlated logs and traces using job, stage, executor, component, or trace context where safe.
- [ ] Alerts avoid paging on purely diagnostic or ticket-only counters unless they predict user impact.

### Performance and overhead
- [ ] Instrumentation overhead is measured or reasoned about for hot paths, especially operators, shuffle fetch, and executor task loops.
- [ ] Metrics do not allocate per row, per value, or per tight-loop iteration.
- [ ] Histogram boundaries are chosen for DeltaSharp workloads rather than copied blindly from generic defaults.
- [ ] Metric export failure does not block query execution, Delta commits, shuffle fetch, executor shutdown, or operator reconciliation.
- [ ] Sampling or aggregation is used for very high-frequency measurements while preserving SLO-level accuracy.

### Validation and supportability
- [ ] Tests or smoke checks verify instrument names, units, attributes, and major state transitions for new metrics.
- [ ] Runbooks reference the metrics responders need to diagnose stuck drivers, executor churn, shuffle failures, degraded storage, and commit conflicts.
- [ ] Metric changes that affect public dashboards or alerts include migration notes or compatibility handling.
- [ ] Metrics are cross-checked with 09a logs and 09c spans for the same operational events.

## Anti-patterns (red flags)
- Per-query, per-user, per-tenant, per-table-path, per-pod-UID, or per-shuffle-block labels that create unbounded time series.
- Averages without histograms for latency-sensitive query, stage, task, shuffle, storage, or commit paths.
- Counters that mix success, failure, cancellation, and timeout into one ambiguous number.
- Dashboards full of pod health but missing job success, action latency, Delta commit durability, or shuffle health.
- Alert thresholds with no owner, runbook, severity, or user-impact definition.
- Blocking, allocating, or performing network I/O while collecting observable gauges.
- Treating metric names and labels as private implementation details after dashboards depend on them.

## References
- [09a — Logging Checklist](09a-logging-checklist.md)
- [09c — Distributed Tracing Checklist](09c-distributed-tracing-checklist.md)
- [14 — Tenant Isolation Checklist](14-tenant-isolation-checklist.md)
- [22 — Benchmark Regression Gates Checklist](22-benchmark-regression-gates-checklist.md)
- `.github/copilot-instructions.md`
- `.github/skills/review-pr/rating-rubric.md`
- `docs/persona/agents/cloud-native-site-reliability-engineer-agent.md`
- `docs/persona/agents/dotnet-runtime-performance-engineer-agent.md`
- `docs/persona/agents/dotnet-distributed-execution-engineer-agent.md`
- System.Diagnostics.Metrics and OpenTelemetry .NET metrics guidance
- .NET EventCounters, EventPipe, `dotnet-counters`, and runtime diagnostic guidance
