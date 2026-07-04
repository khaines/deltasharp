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
| Redaction | `SecretRedaction.RedactPath` used in plan/diagnostic rendering | Same primitive reused at every log/metric/span path site |

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
`ILogger` scope key. They are defined once in `DeltaSharpTelemetry` and mirrored here.

| Constant | Key | Names (codebase concept) | Cardinality |
| --- | --- | --- | --- |
| `RootName` | `DeltaSharp` | Prefix for every `Meter`/`ActivitySource` name and `ILogger` category | n/a |
| `ComponentKey` | `deltasharp.component` | Emitting subsystem (`engine`, `executor`, `catalog`) | bounded set |
| `OperationKey` | `deltasharp.operation` | Logical operation (`collect`, `plan`, `commit`) | bounded set |
| `OutcomeKey` | `deltasharp.outcome` | Terminal outcome (`success`, `cancelled`, `timeout`, `conflict`, `failure`) | bounded set |
| `JobIdKey` | `deltasharp.job.id` | Correlates all signals for one job/application | opaque, bounded per run |
| `StageKey` | `deltasharp.stage` | Pipeline stage (`QueryExecutionStage`, lowercased) | bounded enum |
| `TaskIdKey` | `deltasharp.task.id` | Task within a stage (distributed executor) | opaque, bounded per run |
| `ExecutorIdKey` | `deltasharp.executor.id` | Executor ordinal/slot, **not** a pod UID | bounded |
| `TableKey` | `deltasharp.table` | Catalog-qualified table name, **not** a storage path | bounded per catalog |
| `TableVersionKey` | `deltasharp.table.version` | Delta commit version (small integer) | bounded |
| `CorrelationIdKey` | `deltasharp.correlation.id` | Explicit action correlation id where no `Activity` context flows | opaque, bounded |

The `where applicable` qualifier from STORY-00.4.1 is deliberate: `task.id`, `executor.id`, `table`, and
`table.version` are populated only by the components that own those concepts (the distributed executor
and the Delta layer), and are omitted rather than faked in M1's single-node, tableless paths.

**Cardinality rule.** Every key names a **bounded** dimension. Values that are unbounded or
credential-bearing — raw storage paths, SQL text, plan text, row/cell values, user or tenant identity,
pod UIDs, object keys, shuffle block IDs — are **never** used as a telemetry key or as a label/attribute
**value**. High-cardinality detail belongs in logs, trace events, or exemplars, not in metric labels
(checklist [09b](../checklists/09b-metrics-checklist.md)). Per-tenant attribution, when it arrives, uses
approved pseudonymous identifiers per [14](../checklists/14-tenant-isolation-checklist.md).

## Logging conventions

### Framework and structure

- Components log through `Microsoft.Extensions.Logging` and a typed `ILogger<T>` injected via the
  constructor. No `Console.Write`, ad hoc files, or custom global/static loggers. The single existing
  `Console.WriteLine` (`src/DeltaSharp.Executor/Program.cs`) is the **process bootstrap banner** printed
  before any logging pipeline is configured; it is not a component log and is the only sanctioned console
  write.
- Log messages are **structured templates with named fields**, never interpolated strings or
  concatenation. Prefer `LoggerMessage`-source-generated methods for allocation-free logging on hot or
  frequently executed paths.
- Attach the shared vocabulary fields (`deltasharp.job.id`, `deltasharp.stage`, `deltasharp.outcome`, …)
  through `ILogger.BeginScope` with the `DeltaSharpTelemetry` keys, so the same key strings appear in
  logs, metrics, and spans. Message-template tokens (`{TableVersion}`) are for the human-readable line;
  the machine-correlatable dimensions come from the scope.
- Logging must never materialize data, serialize a full plan, or enumerate batches, and must not run
  inside a lazy transformation — that would imply execution before an action (checklist
  [09a](../checklists/09a-logging-checklist.md)).

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
are not built when the level is disabled. Expected domain outcomes — cancellation, timeout, and Delta
conflicts, which `LocalQueryExecutor` already distinguishes — are logged as such, not as unhandled
runtime errors.

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

### Redaction: never log secrets or credential-bearing paths

Logs, metrics, and spans **never** include secrets, credentials, bearer tokens, connection strings, SAS
tokens, object-store access keys, encryption keys, or raw authorization headers (checklists
[09a](../checklists/09a-logging-checklist.md), [05](../checklists/05-security-checklist.md)). Storage
paths are the highest-frequency leak vector because cloud URIs routinely carry a SAS `?sig=`, a presigned
signature, or `userinfo` credentials.

- Route **every** data-source path through the existing centralized primitive
  `SecretRedaction.RedactPath` (`src/DeltaSharp.Core/Plans/Logical/SecretRedaction.cs`) before it enters
  a log line, an exception message, or any diagnostic. Do not re-implement redaction. This is the same
  primitive already applied when plan trees, `Explain` output, and analysis diagnostics render a path
  (`src/DeltaSharp.Core/Analysis/AnalysisException.cs`,
  `src/DeltaSharp.Executor/Physical/SinkRegistry.cs`,
  `src/DeltaSharp.Core/Plans/Logical/SinkDescriptor.cs`).
- Prefer the **logical** `deltasharp.table` identity over a path entirely; a raw path is neither a
  low-cardinality dimension nor safe, so it is never a metric label or span attribute regardless of
  redaction.
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
  is explicit in the instrument metadata using standard forms — seconds (`s`), bytes (`By`), rows,
  tasks, executors, operations — and the description states the component, event, unit, and whether lower
  or higher is better.
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

### Concrete metric families and ownership

These are the families STORY-00.4.2 requires (job success, latency, throughput, storage I/O), plus the
runtime and Delta/shuffle families the checklists call for. The M1 driver already computes the first four
families' values in `ExecutionMetrics`; the first instruments simply record that snapshot at the action
boundary.

| Family | Type | Example instrument | Unit | Source today | Owner (impl) |
| --- | --- | --- | --- | --- | --- |
| Action outcome | Counter | `deltasharp.action.count` (`deltasharp.outcome` tag) | operations | driver outcome path | dotnet-distributed-execution-engineer |
| Action latency | Histogram | `deltasharp.action.duration` | s | `ExecutionMetrics.TotalDuration` | dotnet-distributed-execution-engineer |
| Planning latency | Histogram | `deltasharp.action.planning.duration` | s | `ExecutionMetrics.PlanningDuration` | query-execution-engine-engineer |
| Throughput | Counter | `deltasharp.action.rows`, `deltasharp.action.batches` | rows, batches | `ExecutionMetrics.OutputRows`/`OutputBatches` | query-execution-engine-engineer |
| Scan volume / memory | Counter / Histogram | `deltasharp.scan.bytes`, `deltasharp.exec.memory.peak` | By | `ExecutionMetrics.BytesScanned`/`PeakMemoryBytes`/`SpilledBytes` | dotnet-runtime-performance-engineer |
| Storage I/O | Counter / Histogram | `deltasharp.storage.io.bytes` (`direction`, `backend` tags), `deltasharp.storage.io.duration` | By, s | future storage layer | delta-storage-format-engineer |
| Delta commit | Counter / Histogram | `deltasharp.delta.commit.count` (`deltasharp.outcome` tag), `deltasharp.delta.commit.duration` | commits, s | future Delta log | delta-storage-format-engineer |
| Shuffle | Counter / Histogram | `deltasharp.shuffle.bytes`, `deltasharp.shuffle.fetch.duration`, `deltasharp.shuffle.reresolve.count` | By, s, operations | future shuffle ([ADR-0004](../../adr/0004-shuffle-architecture.md)) | dotnet-distributed-execution-engineer |
| Runtime / GC | EventCounters | allocation rate, GC pause, thread-pool queue (`dotnet-counters`) | varies | .NET runtime | dotnet-runtime-performance-engineer |

**Ownership split (checklist [09b](../checklists/09b-metrics-checklist.md)).** Implementation owners
create the instruments and define their domain meaning; the
cloud-native-site-reliability-engineer owns SLOs, dashboards, and alert thresholds built on them; the
performance-benchmarking-engineer owns histogram boundaries and instrumentation overhead. Outcomes
(success, failure, cancellation, timeout, conflict) are separated by the `deltasharp.outcome` tag rather
than collapsed into one ambiguous counter, and the same outcome is not double-counted across driver,
executor, and operator unless the instrument name identifies the viewpoint.

### Labels and cardinality limits

Labels use **bounded** sets only — `deltasharp.component`, `deltasharp.operation`, `deltasharp.outcome`,
`deltasharp.stage`, storage `backend`, error class, protocol. Never label a metric with raw query ID,
tenant name, user ID, table path, SQL text, exception message, pod UID, object key, or shuffle block ID.
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
raw SQL, full object paths, or raw exception payloads. Baggage is reserved for small, non-sensitive
routing/correlation hints and is not a general metadata bag. Span status matches the domain result
(success, cancellation, timeout, conflict, transient failure, permanent failure), and an exception is
recorded once at the owning boundary with a sanitized type and error class.

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
  `Meter` and instruments at startup is likewise cheap.
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
  exact value, asserts the attribute-key set is exactly the documented set, and enforces the structural
  invariants (dotted, lowercase, `deltasharp.`-prefixed, unique, no path/query characters), so this
  document and the constants cannot silently diverge.

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
| 00.4.1 | Stable log field names (job, stage, task, executor, table version, correlation) | [Shared telemetry vocabulary](#shared-telemetry-vocabulary); `DeltaSharpTelemetry` |
| 00.4.1 | Correlation propagated across a boundary, or non-propagation documented | [Correlation and context propagation](#correlation-and-context-propagation) (M1 non-propagation rule) |
| 00.4.1 | Redaction rules prohibit secrets/sensitive tokens in logs | [Redaction](#redaction-never-log-secrets-or-credential-bearing-paths); `SecretRedaction.RedactPath` |
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
