# 09c — Distributed Tracing Checklist

> **Scope:** OpenTelemetry traces for client actions, driver scheduling, executor tasks, shuffle, Delta commits, storage I/O, gRPC control traffic, Arrow Flight data transfer, and Kubernetes Operator reconciliation.
> **Priority:** STANDARD.
> **Owners:** cloud-native-site-reliability-engineer, dotnet-distributed-execution-engineer, dotnet-runtime-performance-engineer. **Grounded in:** `.github/copilot-instructions.md`, ADR-0003, 09a, 09b, 14, SRE and distributed execution persona docs.

## How to use
Use this checklist when adding or changing spans, context propagation, sampling, trace attributes, or trace-to-log/metric correlation. Escalate as High when missing propagation makes distributed failures across driver, executor, shuffle, storage, or operator paths impossible to diagnose.

## Checklist
### OpenTelemetry and Activity model
- [ ] Product code uses OpenTelemetry .NET primitives through `ActivitySource` and `Activity` rather than custom trace IDs alone.
- [ ] Each component has a stable `ActivitySource` name and version aligned with package or component identity.
- [ ] Spans follow OpenTelemetry semantic conventions where applicable for HTTP, gRPC, messaging, database-like storage, and Kubernetes operations.
- [ ] Internal span names are stable, low-cardinality operation names such as `DeltaSharp.Query.Execute`, `DeltaSharp.Stage.Run`, or `DeltaSharp.Shuffle.Fetch`.
- [ ] Span creation is guarded so disabled tracing adds minimal overhead in executor and operator hot paths.
- [ ] Trace instrumentation is compatible with NativeAOT and does not rely on dynamic code generation.

### Context propagation
- [ ] Trace context propagates across client to driver, driver to executor, executor to shuffle worker, and storage calls where protocols permit it.
- [ ] gRPC control-plane calls carry W3C trace context through metadata and preserve deadlines and cancellation context separately.
- [ ] Arrow Flight data-plane calls carry trace context without embedding sensitive payload data in headers or descriptors.
- [ ] Kubernetes Operator reconciliation links resource events, status updates, driver pod creation, and executor lifecycle actions to a reconcile trace where possible.
- [ ] Async continuations, channels, background services, and task queues preserve the relevant `ActivityContext` explicitly when ambient context would be lost.
- [ ] Retries, failover, and shuffle re-resolution create child spans or span events that retain the original operation context.

### Span granularity and hierarchy
- [ ] Query/action traces show a clear hierarchy from action to logical or physical execution, stages, tasks, shuffle fetches, storage I/O, and Delta commit where applicable.
- [ ] Stage spans identify stage type, partitioning boundary, attempt, and terminal outcome without recording unbounded plan text.
- [ ] Task spans identify executor, attempt, scheduling delay, execution duration, cancellation, and failure class.
- [ ] Delta commit spans capture transaction version, conflict detection, retry outcome, checkpoint interaction, and storage backend.
- [ ] Shuffle fetch spans capture bytes, block group or bounded identifier, source role, re-resolution, retry, and failure class.
- [ ] Operator reconcile spans capture resource kind, generation, reconcile result, duration, and driver or executor lifecycle decision.
- [ ] Tight vectorized loops, per-row operations, per-value kernels, and per-shuffle-block micro-events are not individual spans unless sampled diagnostic mode is explicitly enabled.

### Attributes, events, and baggage
- [ ] Attributes use bounded names and values consistent with 09a log fields and 09b metric labels.
- [ ] Span attributes never include PII, secrets, credentials, bearer tokens, object-store keys, raw SQL containing tenant data, full object paths, or raw exception payloads.
- [ ] Tenant attribution uses approved pseudonymous or bucketed identifiers and follows 14.
- [ ] Baggage is reserved for small, non-sensitive routing or correlation hints and is not used as a general metadata bag.
- [ ] Span events capture meaningful state transitions such as retry scheduled, executor lost, shuffle location re-resolved, Delta conflict detected, or drain started.
- [ ] Exceptions are recorded once at the owning boundary with sanitized type, error class, and status.

### Sampling and overhead
- [ ] Sampling policy is documented for development, production, hot paths, errors, and incident override modes.
- [ ] Error, timeout, cancellation, Delta conflict, executor loss, and shuffle re-resolution traces are retained or upsampled according to SRE policy.
- [ ] High-volume successful task or shuffle traces are sampled or aggregated so tracing cannot dominate CPU, allocation, network, or storage cost.
- [ ] Sampling decisions preserve enough parent context to connect retained child spans to logs, metrics, and runbooks.
- [ ] Trace exporters are non-blocking and cannot block query execution, commits, executor shutdown, or operator reconciliation.

### Logs, metrics, and trace correlation
- [ ] Logs include trace ID and span ID when an `Activity` is active, matching 09a.
- [ ] Metrics include exemplars or safe correlation hooks where supported, matching 09b cardinality limits.
- [ ] Runbooks describe how to pivot from an alerting metric to a trace and from a trace to relevant logs.
- [ ] Trace IDs are surfaced in user-facing or operator-facing error reports only when doing so does not leak tenant or deployment internals.
- [ ] Trace status and span outcome match the domain result: success, cancellation, timeout, conflict, transient failure, or permanent failure.

### Validation and compatibility
- [ ] Tests or integration smoke checks prove context propagation over gRPC, Arrow Flight, background task dispatch, and operator reconcile paths.
- [ ] Trace schemas are reviewed before changing span names, attribute names, or hierarchy expected by dashboards or runbooks.
- [ ] Instrumentation works when tracing is disabled, partially sampled, exporter-failing, or running under constrained executor pods.
- [ ] Trace changes are reviewed with 09a and 09b so observability remains coherent.

## Anti-patterns (red flags)
- Creating a span per row, per vector element, per hot-loop iteration, or per tiny allocation-sensitive operation.
- Putting raw SQL, plan text, object-store paths, credentials, tenant names, or user identity in attributes or baggage.
- Assuming ambient `Activity.Current` survives channels, queues, callbacks, or background hosted services without verification.
- Sampling away all failed or slow traces while keeping only successful traces.
- Instrumenting gRPC but not Arrow Flight, shuffle, Delta commit, storage, or operator reconcile boundaries.
- Treating baggage as a distributed global variable or tenant metadata carrier.
- Producing traces that cannot be joined to logs or metrics during an incident.

## References
- [09a — Logging Checklist](09a-logging-checklist.md)
- [09b — Metrics Checklist](09b-metrics-checklist.md)
- [14 — Tenant Isolation Checklist](14-tenant-isolation-checklist.md)
- ADR-0003: Data-plane transport
- `.github/copilot-instructions.md`
- `.github/skills/review-pr/rating-rubric.md`
- `docs/persona/agents/cloud-native-site-reliability-engineer-agent.md`
- `docs/persona/agents/dotnet-distributed-execution-engineer-agent.md`
- `docs/persona/agents/dotnet-runtime-performance-engineer-agent.md`
- OpenTelemetry .NET tracing, W3C Trace Context, and semantic conventions
- gRPC and Arrow Flight context propagation guidance
