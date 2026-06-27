# 09a — Logging Checklist

> **Scope:** Driver, executor, shuffle, storage, Delta commit, Kubernetes Operator, CLI, and support paths that emit diagnostic logs.
> **Priority:** STANDARD.
> **Owners:** cloud-native-site-reliability-engineer, dotnet-distributed-execution-engineer, dotnet-runtime-performance-engineer. **Grounded in:** `.github/copilot-instructions.md`, `review-pr/rating-rubric.md`, 05, 07, 09b, 09c, 14, SRE and .NET runtime persona docs.

## How to use
Use this checklist when adding or changing logs, exceptions, diagnostics, or operational runbooks. Escalate as Critical when logs expose secrets, credentials, tenant data, or object-store keys; escalate as High when missing logs block diagnosis of driver, executor, operator, shuffle, or Delta commit failures.

## Checklist
### Logging framework and structure
- [ ] Code uses `Microsoft.Extensions.Logging` and typed `ILogger<T>` instead of console writes, ad hoc files, or custom global loggers.
- [ ] Log messages are structured templates with named fields, not interpolated strings or string concatenation.
- [ ] Field names are stable across driver, executor, shuffle worker, storage, and operator logs.
- [ ] Important events include component, operation, job, stage, task, attempt, executor, table, and storage-backend identifiers where applicable.
- [ ] Log event names or message templates are specific enough for alert triage and support search.
- [ ] Log schema changes are reviewed with 09b and 09c so dashboards and trace correlation do not break silently.

### Levels and operational meaning
- [ ] `Trace` is reserved for opt-in deep diagnostics and is safe to enable briefly in non-hot paths.
- [ ] `Debug` explains developer diagnostics without becoming required for normal production operations.
- [ ] `Information` records lifecycle milestones such as driver start, executor registration, job accepted, stage started, Delta commit completed, and operator reconciliation outcome.
- [ ] `Warning` identifies degraded but recoverable behavior such as retry, backpressure, executor churn, shuffle re-resolution, or storage throttling.
- [ ] `Error` records failed operations that require user, operator, or automated remediation.
- [ ] `Critical` is reserved for process, tenant-isolation, data-integrity, security, or unrecoverable runtime failures.
- [ ] Level changes are justified by user impact and runbook behavior, not by local verbosity preference.

### Security, privacy, and tenant isolation
- [ ] Logs never include PII, secrets, credentials, bearer tokens, connection strings, SAS tokens, object-store access keys, encryption keys, or raw authorization headers.
- [ ] Tenant identifiers are pseudonymous or approved stable IDs; logs do not leak user identity or tenant-private metadata beyond the need defined by 07 and 14.
- [ ] File paths, table names, catalog names, SQL text, plan text, and exception details are scrubbed when they may reveal regulated data or tenant boundaries.
- [ ] Redaction is centralized and tested for storage clients, gRPC metadata, Arrow Flight metadata, configuration dumps, and Kubernetes event mirroring.
- [ ] Security-sensitive failures include enough context for responders without printing the rejected secret, policy document, or credential payload.
- [ ] Logging changes that touch authentication, authorization, storage credentials, or tenant routing are reviewed with 05, 07, and 14.

### Correlation and distributed context
- [ ] Logs carry correlation IDs across client action, driver scheduling, executor task execution, shuffle service, storage calls, and operator reconciliation.
- [ ] Logs include trace ID and span ID when an OpenTelemetry `Activity` is active, matching 09c.
- [ ] gRPC control-plane, Arrow Flight data-plane, and operator reconcile logs preserve the same job or application correlation where possible.
- [ ] Retry logs include attempt number, deadline or remaining budget, retry reason, and whether the retry re-resolved shuffle or storage location.
- [ ] Delta commit logs include table identifier, target version, conflict outcome, and commit duration without exposing object-store credentials.
- [ ] Cancellation, timeout, transient failure, conflict, and programmer error are distinguishable in log fields and message text.

### Volume, cardinality, and performance
- [ ] Hot paths avoid logging in tight inner loops, per row, per value, or per batch unless gated by explicit sampling or disabled trace diagnostics.
- [ ] High-frequency logs are sampled, rate-limited, or aggregated with clear visibility into dropped or sampled counts.
- [ ] Structured fields avoid unbounded cardinality such as raw SQL text, arbitrary query IDs in metric-like positions, full paths, user input, tenant names, or exception messages as dimensions.
- [ ] `LoggerMessage` source generation is used for allocation-free logging in hot or frequently executed .NET paths.
- [ ] Logging does not allocate large strings, serialize full plans, enumerate batches, or materialize data during lazy transformations.
- [ ] Log-level guards protect expensive diagnostic payload construction.
- [ ] Logging behavior remains safe under executor memory pressure, cancellation, shutdown, and operator reconcile storms.

### Exception and boundary handling
- [ ] Exceptions are logged once at the boundary that owns remediation, then propagated or translated without duplicate stack spam.
- [ ] Internal helper methods either add structured context or let callers log; they do not both log and throw by default.
- [ ] Expected domain outcomes such as cancellation, Delta conflicts, or validation failures are not logged as unhandled runtime errors.
- [ ] Boundary logs include actionable next step, runbook link, or domain classification when the failure is operator-facing.
- [ ] Fatal process logs flush through the host logging pipeline before shutdown when feasible.

### Validation and operations
- [ ] Unit or integration tests verify redaction and important structured fields for new diagnostic paths.
- [ ] Operationally important log events are referenced by runbooks or troubleshooting docs when they are part of diagnosis.
- [ ] Log retention, sampling, and export expectations are documented for production deployments.
- [ ] Changes are reviewed against 09b metrics and 09c tracing so responders can pivot between logs, metrics, and traces.

## Anti-patterns (red flags)
- Logging full SQL, plans, rows, object-store URIs with credentials, Kubernetes secrets, or exception data that may contain tenant content.
- Using `$"..."`, `+`, or `.ToString()` to build structured log messages.
- Logging every task heartbeat, shuffle block, row, or batch on an unsampled hot path.
- Logging the same exception at every layer until the root cause is buried.
- Treating logs as the only observability signal instead of pairing them with 09b metrics and 09c traces.
- Using tenant, user, query, path, or raw exception text as unbounded searchable dimensions.
- Emitting logs from lazy transformations that imply execution happened before an action.

## References
- [05 — Security Checklist](05-security-checklist.md)
- [07 — Privacy Checklist](07-privacy-checklist.md)
- [09b — Metrics Checklist](09b-metrics-checklist.md)
- [09c — Distributed Tracing Checklist](09c-distributed-tracing-checklist.md)
- [14 — Tenant Isolation Checklist](14-tenant-isolation-checklist.md)
- `.github/copilot-instructions.md`
- `.github/skills/review-pr/rating-rubric.md`
- `docs/persona/agents/cloud-native-site-reliability-engineer-agent.md`
- `docs/persona/agents/dotnet-distributed-execution-engineer-agent.md`
- `docs/persona/agents/dotnet-runtime-performance-engineer-agent.md`
- Microsoft.Extensions.Logging and `LoggerMessage` source-generation guidance
- OpenTelemetry .NET logs and correlation guidance
