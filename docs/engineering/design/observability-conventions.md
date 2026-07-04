# Observability conventions: logging, metrics, and tracing

> **Status:** living document. Created with
> [STORY-00.4.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-00-engineering-foundations.md#story-0041-logging-and-correlation-identifier-conventions)
> (#110) and
> [STORY-00.4.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-00-engineering-foundations.md#story-0042-metrics-and-activitysource-conventions)
> (#111) under
> [FEAT-00.4](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-00-engineering-foundations.md#feat-004-observability-scaffolding-conventions)
> (#18). Grounded in
> [`.github/copilot-instructions.md`](../../../.github/copilot-instructions.md),
> [ADR-0001](../../adr/0001-execution-strategy.md) (pluggable backends / AOT gating),
> [ADR-0003](../../adr/0003-data-plane-transport.md) (gRPC control plane + Arrow Flight data plane),
> [ADR-0004](../../adr/0004-shuffle-architecture.md) (shuffle), and
> [ADR-0014](../../adr/0014-target-framework-aot.md) (target frameworks / NativeAOT), and satisfies
> checklists [05](../checklists/05-security-checklist.md),
> [09a](../checklists/09a-logging-checklist.md), [09b](../checklists/09b-metrics-checklist.md),
> [09c](../checklists/09c-distributed-tracing-checklist.md),
> [10](../checklists/10-runtime-environment-checklist.md),
> [11](../checklists/11-documentation-support-checklist.md), and
> [14](../checklists/14-tenant-isolation-checklist.md). Read with
> [execution-boundaries.md](execution-boundaries.md) (the `ExecutionMetrics`/stage seam these conventions
> attach to) and [engine-architecture.md](engine-architecture.md). Update it whenever a telemetry name,
> attribute key, redaction rule, propagation rule, or the `DeltaSharpTelemetry` registry changes.

This document defines the OpenTelemetry .NET conventions DeltaSharp components follow when they emit
**logs**, **metrics**, and **traces**, plus the **correlation** and **redaction** rules that tie those
signals together safely. It is deliberately written **before** most driver, executor, and Kubernetes
Operator code exists so that observability is consistent and low-cardinality by design rather than
retrofitted. The conventions add nothing to *when* execution happens — **transformations stay lazy,
actions stay eager** ([ADR-0001](../../adr/0001-execution-strategy.md)); instrumentation is attached to
the eager execution path, never to plan construction.

The canonical telemetry **names** (the shared root prefix and the low-cardinality attribute keys) live in
one compiled, tested place, `DeltaSharpTelemetry`
(`src/DeltaSharp.Abstractions/Diagnostics/DeltaSharpTelemetry.cs`), so this prose and future
instrumentation cannot drift apart. That type is the source of truth for the literal strings; this
document is the source of truth for how they are used.

## Scope and status

These are **conventions and scaffolding**, not a running telemetry pipeline. No component ships a `Meter`
or `ActivitySource` yet; there is no `Microsoft.Extensions.Logging` dependency in the tree today. The
rules below apply the moment the first instrumented component lands, and reviewers apply them to any PR
that adds logging, metrics, or tracing.

### What exists today, and what is deferred

| Concern | Today (M1, in-process single node) | Deferred to later milestones |
| --- | --- | --- |
| Execution | One synchronous `DataFrame` action on the local driver (`LocalQueryExecutor`) | Driver + executor pods, gRPC control plane, Arrow Flight data plane ([ADR-0003](../../adr/0003-data-plane-transport.md)) |
| Stage/metrics seam | `ExecutionMetrics` snapshot per action; `QueryExecutionStage` attribution; `ExecutionAudit` lazy/eager audit | Stage/task spans and instruments fed from the same values |
| Correlation | Ambient `Activity` (if tracing enabled) within one process; no cross-process boundary yet | W3C Trace Context over gRPC + Arrow Flight; shuffle re-resolution spans ([ADR-0004](../../adr/0004-shuffle-architecture.md)) |
| Redaction | `SecretRedaction.RedactPath` (path/credential-scoped) in plan/diagnostic rendering; SQL/rows/option values kept safe by never rendering them | Same path primitive reused at every log/metric/span path site |

This document changes **no public API**. `DeltaSharpTelemetry` is `internal`, so the
`DeltaSharp.Abstractions` and `DeltaSharp.Core` PublicAPI baselines are unchanged (RS0016/RS0017 stay
clean).

### The execution model these conventions attach to

The concrete concepts the field names below map to already exist in code:

- **Action boundary.** A `DataFrame` action (`Collect`/`Count`/`Show`/`Write`) crosses the internal
  `IQueryExecutor` seam into `LocalQueryExecutor`
  (`src/DeltaSharp.Executor/Physical/LocalQueryExecutor.cs`). This is the request/action boundary a
  correlation identifier and a root span attach to.
- **Stages.** Two distinct stage vocabularies exist and must not be conflated:
  - `DeltaSharp.QueryExecutionStage` (public, `src/DeltaSharp.Core/Execution/QueryExecutionStage.cs`):
    `Analyze`, `Plan`, `Scan`, `Backend`, `Materialize` — used to attribute a failure and, by extension,
    the `deltasharp.stage` telemetry dimension.
  - `DeltaSharp.Diagnostics.ExecutionStage` (internal, the #169 lazy/eager audit seam in
    `src/DeltaSharp.Abstractions/Diagnostics/ExecutionAudit.cs`): `Analyzer`, `Planner`, `Backend` — the
    substrate that proves actions are eager and transformations are lazy. The analyzer enters it at
    `src/DeltaSharp.Core/Analysis/Analyzer.cs`.
- **Per-action metrics.** `ExecutionMetrics` (`src/DeltaSharp.Core/Execution/ExecutionMetrics.cs`) is an
  immutable snapshot the driver publishes for every action (success or failure) through a per-call
  `ExecutionMetricsSink`. Its durations come from a monotonic timer
  (`Stopwatch.GetTimestamp()`/`GetElapsedTime()` in `LocalQueryExecutor`), never the wall clock. These
  are the exact values the first metric instruments record.

## Shared telemetry vocabulary

Logs, metric labels, and span attributes for the same operational event **use the same field names**
(checklists [09a](../checklists/09a-logging-checklist.md),
[09b](../checklists/09b-metrics-checklist.md), [09c](../checklists/09c-distributed-tracing-checklist.md)
all require this so responders can pivot between signals). The names are dotted, lowercase, and prefixed
with `deltasharp.` so one string is simultaneously a valid metric tag key, an `Activity` tag key, and an
`ILogger` scope key. They are defined once in `DeltaSharpTelemetry` and mirrored here. This table mirrors
the code constants byte-for-byte; `DeltaSharpTelemetry`
(`src/DeltaSharp.Abstractions/Diagnostics/DeltaSharpTelemetry.cs`) is the **source of truth** and its
mutation tests enforce the code side, so any edit here must land in the same PR as the constant it renames.

The vocabulary splits into two **disjoint** groups that must not be conflated, because putting a
correlation key on a metric is a cardinality bomb:

**Metric-label-safe keys** — closed, bounded-at-any-instant sets. These are the only keys permitted as
**metric labels** (they are equally valid on logs and spans):

| Constant | Key | Names (codebase concept) | Cardinality |
| --- | --- | --- | --- |
| `ComponentKey` | `deltasharp.component` | Emitting subsystem (`engine`, `executor`, `catalog`) | bounded set |
| `OperationKey` | `deltasharp.operation` | Logical operation (`collect`, `plan`, `commit`) | bounded set |
| `OutcomeKey` | `deltasharp.outcome` | Terminal outcome (`success`, `cancelled`, `timeout`, `conflict`, `failure`) | bounded set (5) |
| `StageKey` | `deltasharp.stage` | Pipeline stage (`QueryExecutionStage`, lowercased) | bounded enum |

**Correlation / exemplar-only keys** — valid on **logs, span attributes, and metric exemplars**, but
**never** a metric label, because each is unbounded over a run's or table's lifetime and would multiply a
metric's time series:

| Constant | Key | Names (codebase concept) | Cardinality |
| --- | --- | --- | --- |
| `JobIdKey` | `deltasharp.job.id` | Correlates all signals for one job/application | opaque, unbounded across runs |
| `TaskIdKey` | `deltasharp.task.id` | Task within a stage (distributed executor) | opaque, unbounded across runs |
| `ExecutorIdKey` | `deltasharp.executor.id` | Executor ordinal/slot, **not** a pod UID | bounded per run, unbounded across runs |
| `AttemptKey` | `deltasharp.attempt` | Task/stage attempt number (retry / shuffle re-resolution, ADR-0004) | bounded per run, grows per retry |
| `PartitionKey` | `deltasharp.partition` | Partition (split) index a task processes | workload-dependent, unbounded across jobs |
| `TableKey` | `deltasharp.table` | Catalog-qualified table name, **not** a storage path | bounded per catalog; may embed a tenant id — scrub/omit when regulated |
| `TableVersionKey` | `deltasharp.table.version` | Delta commit version | small at any instant, **unbounded over time** — never a metric label |
| `CorrelationIdKey` | `deltasharp.correlation.id` | Explicit action correlation id where no `Activity` context flows | opaque, unbounded |

`RootName` (`DeltaSharp`) is the shared prefix for every `Meter`/`ActivitySource` name and `ILogger`
category; it is not an attribute key.

**Instance identity lives in the OpenTelemetry Resource, not in keys or labels.** Pod, node, and process
identity are cardinality-unsafe as attributes but essential for an SRE mapping a failing executor to a
pod/node. They belong on the **Resource** attached once per process (standard OpenTelemetry
semantic-convention keys, adopted verbatim rather than reinvented under `deltasharp.`, so they are
documented here but **not** minted in `DeltaSharpTelemetry`):

| Resource attribute | Meaning |
| --- | --- |
| `service.name` | Logical service (`DeltaSharp.<Component>`) |
| `service.instance.id` | Stable instance identity for this process |
| `k8s.pod.name` | Kubernetes pod hosting the driver/executor |
| `k8s.node.name` | Kubernetes node the pod is scheduled on |
| `host.name` | Host/node name for non-K8s runs |

`deltasharp.executor.id` stays a bounded ordinal (never a pod UID); it must be **resolvable to a pod** by
correlating the ordinal against (a) the process Resource and (b) an `Information`-level executor-
registration log line emitted at startup that records `deltasharp.executor.id` alongside `k8s.pod.name`/
`k8s.node.name`. That indirection keeps per-executor metrics low-cardinality while preserving the
executor → pod → node path an incident responder needs.

The `where applicable` qualifier from STORY-00.4.1 is deliberate: `task.id`, `executor.id`, `attempt`,
`partition`, `table`, and `table.version` are populated only by the components that own those concepts (the
distributed executor and the Delta layer), and are omitted rather than faked in M1's single-node, tableless
paths.

**Cardinality rule.** Every **metric-label** key names a **bounded** set (the four metric-label-safe keys
above, plus storage `backend`, error class, and protocol). Values that are unbounded or credential-bearing
— raw storage paths, SQL text, plan text, row/cell values, user or tenant identity, pod UIDs, object keys,
shuffle block IDs — are **never** a metric label or attribute **value**, and secrets/PII/SQL literals/row
values are **never** placed anywhere at all (see redaction below). Bounded correlation detail (job/task/
executor/attempt/partition ids) belongs in **logs, span attributes, and metric exemplars** — never metric
labels; genuinely high-cardinality but non-sensitive structural detail (a *summarized* plan shape, a
bounded error class) belongs in logs or span events. Per-tenant attribution, when it arrives, uses approved
pseudonymous identifiers per [14](../checklists/14-tenant-isolation-checklist.md).

## Logging conventions

### Framework and structure

- Components log through `Microsoft.Extensions.Logging` and a typed `ILogger<T>` injected via the
  constructor. No `Console.Write`, ad hoc files, or custom global/static loggers. The two bootstrap
  `Console.WriteLine` calls (`src/DeltaSharp.Executor/Program.cs:10-11`) are the **process bootstrap
  banner** printed before any logging pipeline is configured; they are not component logs and are the only
  sanctioned console writes in framework code (`samples/` may write freely to the console for illustration
  and are out of scope for this rule).
- Log messages are **structured templates with named fields**, never interpolated strings or
  concatenation. Prefer `LoggerMessage`-source-generated methods for allocation-free logging on hot or
  frequently executed paths.
- Attach the shared vocabulary fields (`deltasharp.job.id`, `deltasharp.stage`, `deltasharp.outcome`, …)
  through `ILogger.BeginScope` with the `DeltaSharpTelemetry` keys, so the same key strings appear in
  logs, metrics, and spans. Message-template tokens (`{TableVersion}`) are for the human-readable line;
  the machine-correlatable dimensions come from the scope.
- Logging must never materialize data, serialize a full plan, or enumerate batches, and must not run
  inside a lazy transformation — that would imply execution before an action (checklist
  [09a](../checklists/09a-logging-checklist.md)). This rule is why **raw SQL text and row/cell values never
  reach a log, exception, or span** (see redaction below): they are user data, and rendering them would
  both leak content and, for row values, materialize the very data the lazy/eager boundary forbids touching
  outside an action.

### Logger categories, event names, and EventIds

A maintainer adding a new component gets its logger category **for free**: `ILogger<T>` uses
`typeof(T).FullName` as the category, and because every DeltaSharp type lives under the `DeltaSharp`
namespace, categories are automatically `DeltaSharp.<Area>.<Type>` (for example
`DeltaSharp.Executor.LocalQueryExecutor`). Operators filter by category prefix — `DeltaSharp.Executor`,
`DeltaSharp.Engine`, and so on — so **no bespoke category registry is required**; the namespace *is* the
registry.

Named, searchable events use a stable `EventId` (an `Id` **and** a `Name`) declared on a
`LoggerMessage`-generated method. `EventId.Name` is a stable PascalCase identifier used for alert triage
and support search (for example `ActionCompleted`, `DeltaCommitConflict`). To keep IDs unique across
components, allocate from this reserved-range registry (extend it when a component is added):

| Range | Area |
| --- | --- |
| 1000–1999 | Session / public API (`DeltaSharp.Core`) |
| 2000–2999 | Analyzer, optimizer, catalog |
| 3000–3999 | Physical planning and local execution (`DeltaSharp.Executor`) |
| 4000–4999 | Storage and Delta transaction log |
| 5000–5999 | Shuffle service |
| 6000–6999 | Kubernetes Operator and reconciliation |
| 7000–7999 | Executor / driver host lifecycle |

### Levels and operational meaning

Follow the level semantics in [09a](../checklists/09a-logging-checklist.md): `Information` for lifecycle
milestones (action accepted, Delta commit completed, executor registered), `Warning` for degraded but
recoverable behavior (retry, backpressure, shuffle re-resolution, storage throttling), `Error` for failed
operations needing remediation, and `Critical` reserved for process, tenant-isolation, data-integrity, or
security failures. `Trace`/`Debug` are opt-in diagnostics guarded by level checks so expensive payloads
are not built when the level is disabled. Expected domain outcomes are logged as such, not as unhandled
runtime errors: cancellation and timeout, which `LocalQueryExecutor` distinguishes (mapping a timeout to
`TimeoutException` and honoring caller cancellation, `src/DeltaSharp.Executor/Physical/LocalQueryExecutor.cs`),
plus the `SaveMode.ErrorIfExists` save-mode conflict the sink raises as an `InvalidOperationException`
(`SinkRegistry.ErrorIfExistsConflict`, `src/DeltaSharp.Executor/Physical/SinkRegistry.cs`). Delta
`_delta_log` commit (OCC) conflicts are **not** distinguished yet — they arrive as a distinct
`deltasharp.outcome=conflict` when the Delta transaction log lands.

### Correlation and context propagation

STORY-00.4.1 requires that correlation identifiers are **either propagated across a request/action
boundary or the non-propagation rule is documented**. Both are stated here because M1 has no cross-process
boundary yet.

- **M1 rule (in-process, single node).** A DeltaSharp action runs synchronously in one process through
  `LocalQueryExecutor`; there is no network hop, no driver→executor dispatch, and no shuffle. Correlation
  is therefore **ambient, not propagated over the wire**: when tracing is enabled, `Activity.Current`
  supplies the W3C `trace.id`/`span.id` that logs include; when tracing is disabled, a component MAY
  attach a `deltasharp.correlation.id` (and, for a multi-statement session, `deltasharp.job.id`) at the
  action boundary for log correlation. DeltaSharp does **not** invent a bespoke wire propagation protocol
  in M1, and correlation is not threaded through the internal `IQueryExecutor` seam beyond the ambient
  `Activity` — that is the explicit non-propagation decision, valid only while execution is single-node.
- **Future rule (driver + executors).** When distributed execution lands
  ([ADR-0003](../../adr/0003-data-plane-transport.md)), correlation propagates as **W3C Trace Context**:
  over gRPC control-plane metadata and Arrow Flight data-plane calls, with deadlines/cancellation carried
  separately, per [09c](../checklists/09c-distributed-tracing-checklist.md). Async continuations,
  channels, and background services must capture `ActivityContext` explicitly because ambient
  `Activity.Current` does not survive those hops.
- **Identifier generation.** Correlation and span identifiers come from `Activity` (W3C id generation) or
  an injected id source. They are **never** minted with `Guid.NewGuid()` or `System.Random`, which are
  banned for determinism (`BannedSymbols.txt`); the wall clock (`DateTime.UtcNow`) is likewise banned, so
  any time component of an id uses an injected `TimeProvider`.

### Redaction: never log secrets, credential-bearing paths, SQL literals, or row values

Logs, metrics, and spans **never** include secrets, credentials, bearer tokens, connection strings, SAS
tokens, object-store access keys, encryption keys, or raw authorization headers (checklists
[09a](../checklists/09a-logging-checklist.md), [05](../checklists/05-security-checklist.md)). They also
**never** include raw SQL text, plan text, or row/cell values, which are user data and a leak vector in
their own right (checklists [05](../checklists/05-security-checklist.md) — "without echoing complete SQL
text … or sensitive row values"; [09a](../checklists/09a-logging-checklist.md) — SQL/plan text "scrubbed",
"Logging full SQL, plans, rows" called out as an anti-pattern). Storage paths are the highest-frequency
leak vector because cloud URIs routinely carry a SAS `?sig=`, a presigned signature, or `userinfo`
credentials.

DeltaSharp has exactly **one** structured redaction primitive, and its scope is deliberately narrow —
**path/credential-scoped only**. Everything else (SQL, rows, option values) is kept safe by **never
rendering it**, not by a redactor:

- **Paths → `SecretRedaction.RedactPath`.** Route **every** data-source path through the existing
  centralized primitive `SecretRedaction.RedactPath`
  (`src/DeltaSharp.Core/Plans/Logical/SecretRedaction.cs`) before it enters a log line, an exception
  message, or any diagnostic. It is a best-effort textual mask that redacts `userinfo` passwords and the
  values of credential-bearing **query-string** parameters (`?sig=`/`&token=`/`&X-Amz-Signature=`, …). Do
  not re-implement it. It is the same primitive already applied when plan trees, `Explain` output, and
  analysis diagnostics render a path (`src/DeltaSharp.Core/Analysis/AnalysisException.cs`,
  `src/DeltaSharp.Executor/Physical/SinkRegistry.cs`,
  `src/DeltaSharp.Core/Plans/Logical/SinkDescriptor.cs`).
- **SQL and row/cell values → never rendered.** There is **no** SQL or row redactor, and one is not the
  goal: SQL must be reduced to a **structural summary** (operator shape, bounded stage/outcome, statement
  kind) or a **parameterized** form with literals stripped **before** anything is logged — a raw literal or
  a materialized cell value must never reach a log, exception, or span in the first place. This is the
  redaction-by-omission counterpart to the "logging never materializes data" rule above.
- **Connection strings and option values → never rendered.** `RedactPath` masks only `?`/`&`
  query-string parameters; it does **not** parse a `;`-delimited connection string, so it would leave an
  `AccountKey=…;` in an ADO/ADLS-style connection string exposed. Connection-string safety therefore does
  **not** rest on `RedactPath`: it rests on **never rendering option values at all**. `SinkDescriptor`
  omits options entirely from its rendered form (`SinkDescriptor.SimpleString`), and the read-side
  `UnresolvedFileRelation` renders option **keys only** (values are never stringified), which is the
  property that keeps connection strings and other option secrets out of diagnostics.
- **Table/catalog names may still carry tenant data.** Prefer the **logical** `deltasharp.table` identity
  over a path entirely; a raw path is neither a low-cardinality dimension nor safe, so it is never a metric
  label or span attribute regardless of redaction. A catalog-qualified `deltasharp.table` (or catalog
  name) may itself embed a tenant id or otherwise reveal a tenant boundary or regulated dataset; when it
  does, it must be **scrubbed or omitted** (checklists [09a](../checklists/09a-logging-checklist.md) line —
  "table names, catalog names … scrubbed when they may reveal regulated data or tenant boundaries",
  [14](../checklists/14-tenant-isolation-checklist.md)).
- Redaction is **centralized and tested**: `SecretRedaction` has unit coverage, and any new diagnostic
  path adds a redaction test (checklist [09a](../checklists/09a-logging-checklist.md)). `SecretRedaction`
  is `internal` to `DeltaSharp.Core` today; a log site in another assembly reuses it through the existing
  internals-visibility grants or promotes it via a reviewed PublicAPI change — it is never duplicated.
- Security-, privacy-, and tenant-sensitive logging changes are reviewed against
  [05](../checklists/05-security-checklist.md), [07](../checklists/07-privacy-checklist.md), and
  [14](../checklists/14-tenant-isolation-checklist.md).

## Metrics conventions

### Meter name and versioning policy

- A component creates **one** `System.Diagnostics.Metrics.Meter` (OpenTelemetry-compatible) and reuses it
  for the life of the component — never one per action, stage, task, or tenant (checklist
  [09b](../checklists/09b-metrics-checklist.md)).
- The **Meter name** is the component identity under the shared root: `DeltaSharp.<Component>` (for
  example `DeltaSharp.Engine`, `DeltaSharp.Executor`, `DeltaSharp.Delta`), built from
  `DeltaSharpTelemetry.RootName`. The name is the subscription handle operators use to enable a Meter, so
  it is stable and low-cardinality.
- The **Meter version** is supplied at construction and tracks the owning assembly's version (the value
  surfaced by `DeltaSharpInfo.Version`, `src/DeltaSharp.Core/DeltaSharpInfo.cs`). Bumping the version is a
  compatible signal that instrument definitions may have evolved; **renaming an instrument or changing its
  unit or its tag set is a breaking change** that requires migration notes for any dashboard or alert that
  consumes it.

### Instrument naming and types

- Instrument (metric) names are dotted, lowercase, and prefixed: `deltasharp.<area>.<name>`. The **unit**
  is explicit in the instrument metadata using **valid UCUM**: seconds (`s`) and bytes (`By`) are UCUM
  units and are used verbatim, while dimensionless counts use the UCUM **annotation** form
  `{operation}`, `{row}`, `{batch}`, `{commit}`, `{task}`, `{executor}` (never a bare English plural like
  `rows`, which is not UCUM). The description states the component, event, unit, and whether lower or
  higher is better.
- Choose the instrument type by semantics:
  - **Counter** — monotonic counts (completed actions, bytes read, retries, failed commits).
  - **Histogram** — distributions (action/stage latency, batch size, shuffle block size); support p50/p95/
    p99, not just averages. Histogram boundaries are chosen for DeltaSharp workloads and owned by the
    performance-benchmarking-engineer (checklist [22](../checklists/22-benchmark-regression-gates-checklist.md)).
  - **UpDownCounter / ObservableGauge** — current levels (active executors, queue depth, in-flight RPCs,
    reserved memory). Observable callbacks read authoritative state only; they never block, allocate
    heavily, or perform I/O.
- Instruments never allocate per row, per value, or per tight-loop iteration, and metric export failure
  never blocks execution, commits, shuffle fetch, or shutdown (checklists
  [09b](../checklists/09b-metrics-checklist.md), [10](../checklists/10-runtime-environment-checklist.md)).
- On hot paths, guard non-trivial tag construction with `instrument.Enabled`. `Counter.Add`/
  `Histogram.Record` are free when no `MeterListener` subscribes, but the **tag arguments** are evaluated
  at the call site regardless — an enum `ToString()`, a string concatenation, a `RedactPath` call, or a
  `TagList` build still runs. Wrap that work in `if (instrument.Enabled) { … }` so a disabled meter costs
  only a boolean check (see [Safe no-ops](#safe-no-ops-when-telemetry-is-disabled)).

### Concrete metric families and ownership

These are the families STORY-00.4.2 requires (job success, latency, throughput, storage I/O), plus the
runtime and Delta/shuffle families the checklists call for. The M1 driver already computes the first four
families' values in `ExecutionMetrics`; the first instruments simply record that snapshot at the action
boundary.

| Family | Type | Example instrument | Unit | Source today | Owner (impl) |
| --- | --- | --- | --- | --- | --- |
| Action outcome | Counter | `deltasharp.action.count` (`deltasharp.outcome` tag) | `{operation}` | driver outcome path | dotnet-distributed-execution-engineer |
| Action latency | Histogram | `deltasharp.action.duration` (`deltasharp.outcome`, `deltasharp.stage` tags) | `s` | `ExecutionMetrics.TotalDuration` | dotnet-distributed-execution-engineer |
| Planning latency | Histogram | `deltasharp.action.planning.duration` (`deltasharp.outcome` tag) | `s` | `ExecutionMetrics.PlanningDuration` | query-execution-engine-engineer |
| Throughput | Counter | `deltasharp.action.rows`, `deltasharp.action.batches` (`deltasharp.outcome` tag) | `{row}`, `{batch}` | `ExecutionMetrics.OutputRows`/`OutputBatches` | query-execution-engine-engineer |
| Scan volume / memory | Counter / Histogram | `deltasharp.scan.bytes`, `deltasharp.exec.memory.peak` | `By` | `ExecutionMetrics.BytesScanned`/`PeakMemoryBytes`/`SpilledBytes` | dotnet-runtime-performance-engineer |
| Storage I/O | Counter / Histogram | `deltasharp.storage.io.bytes` (`direction`, `backend` tags), `deltasharp.storage.io.duration` | `By`, `s` | future storage layer | delta-storage-format-engineer |
| Delta commit | Counter / Histogram | `deltasharp.delta.commit.count` (`deltasharp.outcome` tag), `deltasharp.delta.commit.duration` | `{commit}`, `s` | future Delta log | delta-storage-format-engineer |
| Shuffle | Counter / Histogram | `deltasharp.shuffle.bytes`, `deltasharp.shuffle.fetch.duration`, `deltasharp.shuffle.reresolve.count` | `By`, `s`, `{operation}` | future shuffle ([ADR-0004](../../adr/0004-shuffle-architecture.md)) | dotnet-distributed-execution-engineer |
| Saturation / USE | UpDownCounter / ObservableGauge | `deltasharp.executor.active`, `deltasharp.exec.queue.depth`, `deltasharp.rpc.inflight`, `deltasharp.exec.memory.reserved` | `{executor}`, `{task}`, `{operation}`, `By` | future driver/executor + shuffle | cloud-native-site-reliability-engineer |
| Runtime / GC | EventCounters | allocation rate, GC pause, thread-pool queue (`dotnet-counters`) | varies | .NET runtime | dotnet-runtime-performance-engineer |

**Success-only SLIs need `deltasharp.outcome` on latency/throughput.** The action/planning `*.duration`
histograms and the throughput counters carry the bounded `deltasharp.outcome` label (5 values), and the
action-duration histogram may also carry `deltasharp.stage`, so a dashboard can compute
*success-only* latency percentiles. Without it a timeout would inflate the latency histogram and a fast
failure would deflate it, making the SLI meaningless. Attempt/partition detail attaches as **exemplars or
span links**, never as histogram labels. The **Saturation / USE** row supplies concrete USE-method
gauges/up-down counters (active executors, queue depth, in-flight RPCs, reserved memory) to complement the
RED-method counters/histograms above; observable-gauge callbacks read authoritative state only and never
block, allocate heavily, or perform I/O.

**Ownership split (checklist [09b](../checklists/09b-metrics-checklist.md)).** Implementation owners
create the instruments and define their domain meaning; the
cloud-native-site-reliability-engineer owns SLOs, dashboards, and alert thresholds built on them; the
performance-benchmarking-engineer owns histogram boundaries and instrumentation overhead. Outcomes
(success, failure, cancellation, timeout, conflict) are separated by the `deltasharp.outcome` tag rather
than collapsed into one ambiguous counter, and the same outcome is not double-counted across driver,
executor, and operator unless the instrument name identifies the viewpoint.

### Labels and cardinality limits

Labels use the **metric-label-safe** set only — the four bounded `DeltaSharpTelemetry` keys
(`deltasharp.component`, `deltasharp.operation`, `deltasharp.outcome`, `deltasharp.stage`) plus storage
`backend`, error class, and protocol. The **correlation/exemplar-only** keys (`deltasharp.job.id`,
`deltasharp.task.id`, `deltasharp.executor.id`, `deltasharp.attempt`, `deltasharp.partition`,
`deltasharp.table`, `deltasharp.table.version`, `deltasharp.correlation.id`) are **never** metric labels —
they grow unbounded over a run's or table's lifetime — and attach instead to logs, span attributes, and
metric **exemplars**. Never label a metric with raw query ID, tenant name, user ID, table path, SQL text,
row values, exception message, pod UID, object key, shuffle block ID, Delta version, or a per-run id.
Adding a label is reviewed for time-series cost and tenant-isolation risk before merge.

## Tracing conventions

### ActivitySource names and versioning

- A component uses OpenTelemetry .NET primitives — `System.Diagnostics.ActivitySource` and `Activity` —
  not bespoke trace IDs.
- Each component has a stable `ActivitySource` **name** aligned with its identity, `DeltaSharp.<Component>`
  (built from `DeltaSharpTelemetry.RootName`), and a **version** aligned with the owning assembly, exactly
  like the Meter name and version. This is the handle an `ActivityListener` or OpenTelemetry
  `TracerProvider` subscribes to.

### Span names and hierarchy

Internal span names are stable, low-cardinality operation names in PascalCase, matching the examples in
[09c](../checklists/09c-distributed-tracing-checklist.md): `DeltaSharp.Query.Execute`,
`DeltaSharp.Stage.Run`, `DeltaSharp.Shuffle.Fetch`. A span name never embeds a variable (a table name,
path, or id) — that goes in a bounded attribute. The query/action trace shows a hierarchy from the action
root down through the pipeline stages that already exist in code
(`Analyze` → `Plan` → `Scan` → `Backend` → `Materialize`, from
`QueryExecutionStage`), and later through stages, tasks, shuffle fetches, storage I/O, and Delta commit.
Tight vectorized loops, per-row kernels, and per-shuffle-block micro-events are **not** individual spans
unless a sampled diagnostic mode is explicitly enabled.

### Span attributes and cardinality limits

Span attributes use the **same** `DeltaSharpTelemetry` keys as logs and metric labels, and obey the same
cardinality limits: bounded names and values, and **never** PII, secrets, credentials, object-store keys,
raw SQL, row/cell values, full object paths, or raw exception payloads. Because spans (unlike metrics)
tolerate per-run cardinality, they are the right home for the **correlation/exemplar-only** keys —
`deltasharp.job.id`, `deltasharp.task.id`, `deltasharp.executor.id`, `deltasharp.attempt`,
`deltasharp.partition`, `deltasharp.table`, `deltasharp.table.version`, `deltasharp.correlation.id` — which
are prohibited as metric labels. A stage span carries `deltasharp.stage` + `deltasharp.attempt` +
`deltasharp.outcome`, and a task span carries `deltasharp.executor.id` + `deltasharp.partition` +
`deltasharp.attempt` (matching [09c](../checklists/09c-distributed-tracing-checklist.md), which requires
stage/task spans to identify partitioning boundary, attempt, and terminal outcome). Baggage is reserved
for small, non-sensitive routing/correlation hints and is not a general metadata bag. Span status matches
the domain result (success, cancellation, timeout, conflict, transient failure, permanent failure), and an
exception is recorded once at the owning boundary with a sanitized type and error class.

### Context propagation

Trace-context propagation follows the correlation rule above: ambient within M1's single process, and
W3C Trace Context over gRPC and Arrow Flight once distributed execution exists
([ADR-0003](../../adr/0003-data-plane-transport.md)). Retries, failover, and shuffle re-resolution create
child spans or span events that retain the original operation context rather than starting disconnected
traces.

## Safe no-ops when telemetry is disabled

STORY-00.4.2 requires that with telemetry export disabled, instrumentation calls are **safe no-ops that
do not require a collector**. This is an inherent property of the .NET primitives, reinforced by a
guarding convention:

- **Metrics.** A `Counter.Add`/`Histogram.Record` call with no subscribed `MeterListener` (and therefore
  no OpenTelemetry exporter) performs no aggregation and no export — it is effectively free. Creating the
  `Meter` and instruments at startup is likewise cheap. **The call is free; the tag arguments are not** —
  building a `TagList`, calling `enum.ToString()`, or invoking `RedactPath` still runs at the call site
  even with no listener, so on hot paths gate that work behind `if (instrument.Enabled)` (the
  `Enabled` property is `true` only while a listener subscribes). Trivial, already-materialized tags need
  no guard.
- **Tracing.** `ActivitySource.StartActivity` returns `null` when no `ActivityListener` samples the
  source. Instrumentation guards on that (`using Activity? a = source.StartActivity(...);` then
  `a?.SetTag(...)`), so disabled tracing costs a null check, and any expensive attribute construction is
  wrapped in `if (a is not null)`.
- **In-repo precedent.** This is exactly how the existing `ExecutionAudit` seam behaves: its forwarders
  are `_current.Value?.OnStageEntered(...)`
  (`src/DeltaSharp.Abstractions/Diagnostics/ExecutionAudit.cs`), a no-op when no sink is installed — the
  zero-overhead production default. Telemetry follows the same shape.
- **No dependency, no collector.** The instrumentation types live in `System.Diagnostics.DiagnosticSource`,
  which is part of the shared framework and its reference assemblies on **both** `net8.0` and `net10.0`,
  so a component needs **no NuGet dependency and no running collector** to build or run locally; signals
  simply go nowhere until an operator opts in.

## AOT and determinism constraints

Instrumentation runs inside the NativeAOT executor image ([ADR-0014](../../adr/0014-target-framework-aot.md)),
so it must respect the same runtime discipline as the rest of the engine (checklist
[10](../checklists/10-runtime-environment-checklist.md)):

- `Meter`, `ActivitySource`, `Activity`, and the instrument types are AOT-safe and require no dynamic
  code. Telemetry must not reintroduce reflection-based enrichment on hot paths or any
  `Reflection.Emit`/`Expression.Compile` path — those stay banned and the optional codegen tier stays
  elided ([ADR-0001](../../adr/0001-execution-strategy.md), `BannedSymbols.txt`).
- Durations are measured with a **monotonic** timer (`Stopwatch`), as `ExecutionMetrics` already does,
  and recorded to histograms in seconds; the wall clock is never read.
- Identifiers use `Activity`/an injected id source, never the banned `Guid.NewGuid()` or `System.Random`.
- Instruments and sources are created **once** per component (static or DI singleton), never per row,
  batch, task, or tenant.

## Scaffolding: the `DeltaSharpTelemetry` name registry

The only code this feature adds is `DeltaSharpTelemetry`
(`src/DeltaSharp.Abstractions/Diagnostics/DeltaSharpTelemetry.cs`), an `internal static` class that holds
the shared root prefix and the canonical attribute keys as `const string`s. It sits beside the existing
`ExecutionAudit`/`ExecutionStage` diagnostics in `DeltaSharp.Abstractions` — the shared Core+Engine
diagnostics layer, visible to `DeltaSharp.Core`, `DeltaSharp.Engine`, and `DeltaSharp.Core.Tests` — so
both siblings can bind identical names.

- **Names only.** It creates no `Meter` or `ActivitySource` instance; a component owns its own instances
  and names them with `RootName`. This keeps the registry AOT-trivial (string constants) and dependency-
  free.
- **Internal, not public.** It seeds a convention, not a shipped contract, so it adds nothing to the
  PublicAPI baseline. A host that must publish these names to configure an OpenTelemetry provider is
  granted internals visibility, or the vocabulary is promoted to public through a reviewed PublicAPI
  change when a real consumer needs it.
- **Tested.** `tests/DeltaSharp.Core.Tests/Diagnostics/DeltaSharpTelemetryTests.cs` pins every constant's
  exact value (including `deltasharp.attempt` and `deltasharp.partition`), asserts the attribute-key set is
  exactly the documented set, and enforces the structural invariants (dotted, lowercase, `deltasharp.`-
  prefixed, unique, no path/query characters), so this document and the constants cannot silently diverge.
  The [vocabulary tables](#shared-telemetry-vocabulary) above mirror these constants byte-for-byte; the
  standard OpenTelemetry Resource attributes (`service.*`, `k8s.*`, `host.name`) and instrument names are
  documented-only and intentionally **not** minted here (Resource keys are not `deltasharp.`-prefixed, and
  instrument names are owned by each component), keeping the registry a pure attribute-key vocabulary.

## Ownership and review

The cloud-native-site-reliability-engineer owns these conventions and the SLOs/dashboards built on them;
the dotnet-framework-runtime-engineer and dotnet-runtime-performance-engineer own the .NET
instrumentation shape and overhead; the technical-writer owns this document's clarity. A PR that adds or
changes logging, metrics, or tracing is reviewed against the relevant checklist —
[09a](../checklists/09a-logging-checklist.md), [09b](../checklists/09b-metrics-checklist.md),
[09c](../checklists/09c-distributed-tracing-checklist.md) — plus
[05](../checklists/05-security-checklist.md)/[07](../checklists/07-privacy-checklist.md)/[14](../checklists/14-tenant-isolation-checklist.md)
whenever it touches credentials, tenant routing, or storage paths, and
[10](../checklists/10-runtime-environment-checklist.md) for runtime/AOT impact.

## Acceptance-criteria traceability

| Story | Acceptance criterion | Where satisfied |
| --- | --- | --- |
| 00.4.1 | Stable log field names (job, stage, task, executor, attempt, partition, table version, correlation) | [Shared telemetry vocabulary](#shared-telemetry-vocabulary); `DeltaSharpTelemetry` |
| 00.4.1 | Correlation propagated across a boundary, or non-propagation documented | [Correlation and context propagation](#correlation-and-context-propagation) (M1 non-propagation rule) |
| 00.4.1 | Redaction rules prohibit secrets/sensitive tokens in logs | [Redaction](#redaction-never-log-secrets-credential-bearing-paths-sql-literals-or-row-values); `SecretRedaction.RedactPath` |
| 00.4.1 | Logger categories + event-naming guidance for new components | [Logger categories, event names, and EventIds](#logger-categories-event-names-and-eventids) |
| 00.4.2 | Meter name, versioning policy, instrument-naming pattern | [Meter name and versioning policy](#meter-name-and-versioning-policy), [Instrument naming and types](#instrument-naming-and-types) |
| 00.4.2 | `ActivitySource` names + span attributes within cardinality limits | [Tracing conventions](#tracing-conventions) |
| 00.4.2 | Concrete metric families (success, latency, throughput, storage I/O) + ownership | [Concrete metric families and ownership](#concrete-metric-families-and-ownership) |
| 00.4.2 | Instrumentation is a safe no-op when export is disabled | [Safe no-ops when telemetry is disabled](#safe-no-ops-when-telemetry-is-disabled) |

## References

- [ADR-0001: Execution strategy](../../adr/0001-execution-strategy.md)
- [ADR-0003: Data-plane transport](../../adr/0003-data-plane-transport.md)
- [ADR-0004: Shuffle architecture](../../adr/0004-shuffle-architecture.md)
- [ADR-0014: Target framework and AOT posture](../../adr/0014-target-framework-aot.md)
- [05 — Security Checklist](../checklists/05-security-checklist.md)
- [09a — Logging Checklist](../checklists/09a-logging-checklist.md)
- [09b — Metrics Checklist](../checklists/09b-metrics-checklist.md)
- [09c — Distributed Tracing Checklist](../checklists/09c-distributed-tracing-checklist.md)
- [10 — Runtime Environment Checklist](../checklists/10-runtime-environment-checklist.md)
- [11 — Documentation Support Checklist](../checklists/11-documentation-support-checklist.md)
- [14 — Tenant Isolation Checklist](../checklists/14-tenant-isolation-checklist.md)
- [execution-boundaries.md](execution-boundaries.md) — `ExecutionMetrics`, stage attribution, the driver
- `src/DeltaSharp.Abstractions/Diagnostics/DeltaSharpTelemetry.cs` — canonical telemetry names
- `src/DeltaSharp.Core/Plans/Logical/SecretRedaction.cs` — centralized path redaction
- OpenTelemetry .NET (logs, metrics, tracing), W3C Trace Context, and semantic-convention guidance
