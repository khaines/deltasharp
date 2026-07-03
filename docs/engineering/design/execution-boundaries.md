# Local execution boundaries: cancellation, errors, limits & metrics (v1)

> **Status:** living document. Created with
> [STORY-04.6.4](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md#story-0464-local-execution-error-cancellation-and-resource-boundaries)
> (#176) — the lane that *hardens* the local execution driver STORY-04.6.2 (#174, merged `ed1aa5f`)
> delivered. It discharges the **disposal half** of
> [#420](https://github.com/khaines/deltasharp/issues/420) (deterministic `ExecutionContext`/spill-store
> release) and reinforces its `IBatchStream` **batch-ownership invariant**; the durable pooled-reuse
> streaming seam + the parity oracle remain tracked by #420 (**OPEN**). It also discharges
> [#416](https://github.com/khaines/deltasharp/issues/416) (execution-seam `CancellationToken` +
> resource bounds). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (pluggable backends — interpreted vectorized is the
> default and correctness reference) and [ADR-0002](../../adr/0002-columnar-batch-format.md) (mutable
> `ColumnBatch`/`ColumnVector` ownership). Read together with
> [physical-planning.md](physical-planning.md) (the bridge this story hardens),
> [actions-and-row.md](actions-and-row.md) (the action pipeline), and
> [sparksession-lifecycle.md](sparksession-lifecycle.md) (config + lifecycle). Update it whenever the
> cancellation path, disposal contract, stage-attributed exception design, limit-enforcement points,
> metrics surface, config keys, or the Core↔Executor seam change.

This story adds nothing to *when* execution triggers: **transformations stay lazy, actions stay eager**
(ADR-0001; `DataFrame.cs:572-745`). It bounds *how* an action runs once triggered — it stops
cooperatively, releases local resources deterministically, attributes failures to a pipeline stage,
fails safely before unbounded materialization, and exposes planning/execution counters.

---

## 1. Where the boundaries sit in the pipeline

```
DataFrame.Collect/Count/Show          Core, public API — builds ExecutionOptions from a
   │  (optional CancellationToken)     CancellationToken + SparkSession config, then crosses the seam
   ▼
IQueryExecutor.Collect/Count(plan, ExecutionOptions)   Core internal seam (#416 discharged)
   │                                                    ─────────────►  DeltaSharp.Executor
   ▼
LocalQueryExecutor                     stage driver: times + attributes + bounds each stage,
   │                                   builds ExecutionMetrics, owns deterministic disposal
   ├─ PhysicalPlanner.Plan  ───────────────────────────────►  [Plan] / [Scan] stage
   ├─ PhysicalPlan.Execute(PhysicalRuntime)  ──────────────►  [Backend] stage
   │     (single shared ExecutionContext; token + memory budget threaded to every operator)
   ▼
RowMaterializer.Materialize  ──────────────────────────────►  [Materialize] stage
```

Analyzer resolution runs in Core **before** the seam (`DataFrame.AnalyzeForExecution`,
`DataFrame.cs:850-865`); it is the `[Analyze]` stage. Everything from `IQueryExecutor` down is the
Executor and owns the `[Plan]`/`[Scan]`/`[Backend]`/`[Materialize]` stages.

---

## 2. Criterion 1 — cancellation / timeout & deterministic resource release

### 2.1 Propagation path

`CancellationToken` and the timeout flow **top-down** through one immutable `ExecutionOptions`:

1. **Core action.** `DataFrame.Collect(CancellationToken)` / `Count(CancellationToken)` accept a user
   token. `ExecutionOptions.From(session, cancellationToken)` reads the session config (§2.4, §4) and
   captures the token.
2. **Seam.** `IQueryExecutor.Collect(LogicalPlan, ExecutionOptions)` /
   `Count(LogicalPlan, ExecutionOptions)` carry it across the Core↔Executor boundary (this is the
   `CancellationToken` #416 asked for).
3. **Driver → runtime.** `LocalQueryExecutor` builds an **effective token**: when a timeout is
   configured it creates a linked `CancellationTokenSource` (`CancellationTokenSource.CreateLinkedTokenSource(userToken)`
   + `CancelAfter(timeout)`); otherwise it uses the user token directly. The effective token is handed
   to `PhysicalRuntime`.
4. **Runtime → operators.** `PhysicalRuntime` builds **one** Engine `ExecutionContext`
   (`ExecutionContext.CancellationToken`) shared across the whole operator tree (matching
   `ExecutionContext.cs:9-11` — "immutable and shared across an operator tree so cancellation … are
   consistent end-to-end"). Every operator `Open`ed on the backend observes the same token.

### 2.2 Cooperative observation (already in the Engine, reinforced at the driver)

Engine operators already observe cancellation cooperatively: `InterpretedScanStream.TryGetNext` calls
`_cancellationToken.ThrowIfCancellationRequested()` at each batch boundary
(`InterpretedScan.cs:40`), and per-row interpreted loops poll every `1024` rows via
`CancellationPolicy.Poll` (`CancellationPolicy.cs:18-33`), bounding the worst-case uncancellable run.

This story adds an **upfront driver gate plus two driver-level poll points** so cancellation and an
already-elapsed timeout are honored even for plan shapes that never reach a polling operator — a bare
`ScanPlan` root (`ScanPlan.Execute`, `PhysicalPlan.cs:134-135`) returns its batches directly, and a
bare `LimitPlan` (`PhysicalPlan.cs:368-396`) or `UnionPlan` (`PhysicalPlan.cs:424-434`) root likewise
bypasses `PhysicalRuntime.Run`'s per-batch poll:

- **Upfront gate (this story).** `LocalQueryExecutor.Execute` calls
  `effectiveToken.IsCancellationRequested` → `throw MapCancellation(...)` at the very top of the
  execution stage, after building the effective token and **before** any plan is executed or any row is
  materialized. This makes a **pre-cancelled token or an already-elapsed timeout deterministic for
  *both* actions (`Collect` and `Count`) over *every* plan shape**, including `Count`-over-bare-root and
  an empty-result `Collect` that never opens an Engine operator. A cancelled user token surfaces as
  `OperationCanceledException`; an elapsed timeout (user token not cancelled) surfaces as
  `TimeoutException`.
- `PhysicalRuntime.Run` calls `token.ThrowIfCancellationRequested()` between drained batches.
- `RowMaterializer.Materialize` polls every 1024 rows (a power-of-two `CancellationPollMask == 1023`)
  while building the `Row` list, and `RowMaterializer.CountRows` polls per drained batch, bounding a
  single huge batch's uncancellable materialization/count exactly as `CancellationPolicy` bounds the
  interpreted loops.

### 2.3 Deterministic disposal & batch ownership (discharges #420)

`#420` flags that `PhysicalRuntime.Run` created a fresh `ExecutionContext` per operator (default
`TempFileSpillStore`, `IDisposable`) and **never disposed it** — benign under M1's unbounded memory
(the temp dir is created lazily and never spills, `ExecutionContext.cs:16,48-49`) but a real handle leak
once bounded memory + spilling land. This story closes it:

- **Engine seam.** `ExecutionContext` now implements `IDisposable`; `Dispose()` disposes its
  `SpillStore` when the store is `IDisposable` (`TempFileSpillStore` is; `MemorySpillStore` is not).
  A private `_disposed` guard makes `Dispose()` **genuinely idempotent** (a second call is a no-op that
  never re-disposes the store), rather than relying on the store's own idempotency. This is the durable
  Engine seam #420 said was missing ("`SpillStore` is internal; `ExecutionContext` isn't
  `IDisposable`").
- **Single owner.** `PhysicalRuntime` builds and owns **one** `ExecutionContext` for the whole run
  (not one per operator) and is itself `IDisposable`; `LocalQueryExecutor.Execute` drives it and
  disposes it in a `finally` (`runtime?.Dispose()`), so the context (and thus the spill store / any temp
  files) is disposed on **every** path — normal completion, cancellation, timeout, and failure alike.
- **Streams.** `PhysicalRuntime.Run` already disposes each `IBatchStream` in a `finally`
  (`PhysicalRuntime.cs:114-118`); that is retained. Because a stream is drained-then-disposed inside a
  single `Run`, an in-flight cancellation throwing out of `TryGetNext` still hits that `finally`, so no
  stream is stranded.
- **Batch-ownership contract (discharges #420 §2).** `Run` accumulates every emitted `ColumnBatch` into
  a `List<ColumnBatch>` that outlives both the producing `TryGetNext` call and the stream's `Dispose`.
  This is only sound if each batch is **independently owned** — valid after subsequent `TryGetNext`
  calls and after the stream is disposed. Every M1 operator emits fresh, independently-owned output
  (fresh columns, or a view over immutable GC-owned buffers), so the invariant holds today; this story
  makes the contract **explicit** in `PhysicalRuntime.Run`'s remarks (the batch-ownership half of #420,
  ADR-0002 `ColumnBatch`/`ColumnVector` ownership). A future pooled/off-heap operator that reuses or
  frees its output buffers on `Dispose` would violate it and MUST copy-out before adding to the list;
  that streaming/pooling seam remains tracked by #420.

Net effect: an `OperationCanceledException`/`TimeoutException` unwinding through `Run` →
`PhysicalPlan.Execute` → `LocalQueryExecutor.Execute` disposes the runtime's context and the open
stream deterministically before the exception leaves the driver.

### 2.4 Timeout surface (Spark/.NET parity)

Timeout is sourced from config `spark.deltasharp.execution.timeoutMs` (§4). A positive timeout arms a
linked CTS (`CreateLinkedTokenSource(userToken)`) with `CancelAfter(timeout)`. A **non-positive**
timeout is an already-elapsed deadline, so the driver cancels the linked CTS **synchronously**
(`Cancel()`) for a deterministic, race-free result rather than scheduling it; config never produces this
(a configured `0` maps to `null`/"disabled" and a **negative** value is rejected fail-fast with
`ArgumentException` in `ExecutionOptions.From` → `ReadPositiveLong`, `ExecutionOptions.cs:82-100`), so it
only arises via the direct options API and the `TimeSpan.Zero` timeout test. On expiry the linked CTS
cancels the effective token; the driver
distinguishes the two cases in its catch:

- user token cancelled → rethrow the `OperationCanceledException` (the .NET idiom; a user who cancels
  gets `OperationCanceledException`).
- timeout fired (user token *not* cancelled) → throw `TimeoutException` naming the configured timeout
  key: *"The DataFrame action exceeded its configured execution timeout
  ('spark.deltasharp.execution.timeoutMs') and was cancelled."*

**Out-of-range timeout (#7).** `CancellationTokenSource.CancelAfter(TimeSpan)` caps the delay at
`uint.MaxValue - 1` ms (~49.7 days) and throws a raw `ArgumentOutOfRangeException` above it — which
would otherwise escape *outside* the driver's try (unattributed, metrics-less).
`ExecutionOptions.From` therefore **clamps** a configured `timeoutMs` above
`ExecutionOptions.MaxTimeoutMilliseconds` (`= uint.MaxValue - 1`) down to that ceiling (a value so large
the practical effect is "effectively no timeout"), keeping the read-time discipline fail-fast for
negatives while never letting a huge-but-valid number escape as an unattributed framework throw. The
driver applies the same defensive clamp before `CancelAfter`.

`OperationCanceledException`/`TimeoutException` are **not** wrapped in `QueryExecutionException`: they
are control-flow signals, not stage faults, and .NET consumers expect to catch them by their framework
types. This deviates from raw Spark (which cancels a job via `SparkContext`), a documented .NET-idiom
adaptation (§7).

---

## 3. Criterion 2 — stage-attributed exceptions that preserve the root cause

### 3.1 The stage enum & the public exception

`QueryExecutionStage` (public, Core — `enum { Analyze, Plan, Scan, Backend, Materialize }`) names the
pipeline stage. It is deliberately **not** the pre-existing internal
`DeltaSharp.Diagnostics.ExecutionStage` (audit milestones, `ExecutionAudit.cs:16-26`), which is internal
and carries only `Analyzer/Planner/Backend`; a public, failure-oriented enum is a separate concern.

`QueryExecutionException` (already the single public execution error type, `QueryExecutionException.cs`)
gains two nullable properties — `QueryExecutionStage? Stage` and `ExecutionMetrics? Metrics` — plus
constructors that carry them. It stays the type `DataFrame.Collect/Count/Show` document
(`DataFrame.cs:587-588`).

### 3.2 What is wrapped vs. what propagates unwrapped

Two categories, both of which "identify the failed stage and preserve the root cause":

1. **Inherently stage-identifying typed diagnostics propagate unwrapped**:
   - `UnsupportedPlanException` (public, Executor — `UnsupportedPlanException.cs`) is the deterministic
     *unsupported-shape / unrepresentable-value* diagnostic. It already names the offending node and is
     asserted unwrapped by existing tests from `fixture.Collect`
     (`MaterializationTests.cs:136,150,194,…`; `UnsupportedPlanTests.cs`). Wrapping it would break those
     contracts, so it **must** propagate as-is. This story instead adds a `QueryExecutionStage Stage`
     property to it so it is machine-readably stage-attributed **without** changing its type or message.
     The three attributed sub-cases are: `Plan` for planner `ForNode`/`ForExpression`
     cases; `Scan` for a scan-source miss raised by `PlanScan` (`PhysicalPlanner.cs:103-117`) **and**
     for a #158 `LocalRelationBatches` deferred row→batch encode mismatch (a schema/type/encode failure
     is a scan/data-in fault, not a plan-shape fault, so `LocalRelationBatches` raises its diagnostics
     with `QueryExecutionStage.Scan` — see §3.3); and `Materialize` for the `RowMaterializer`
     unrepresentable-value cases.
   - The analyzer's `AnalysisException` (internal, Core) is the `[Analyze]`-stage diagnostic raised
     before the seam; per `QueryExecutionException.cs` remarks a resolution error "never reaches this
     stage." It is unchanged, and the driver never emits a `QueryExecutionStage.Analyze` wrapper — that
     enum value exists for consumers/parity with the pipeline, but analyzer failures self-identify via
     `AnalysisException` in Core before the seam.
2. **Unexpected / runtime faults are wrapped** by `LocalQueryExecutor` into
   `QueryExecutionException(stage, message, innerException, metrics)` with the failing stage and the
   original exception as `InnerException`. A scan-source miss surfaced by the planner as a
   `Scan`-attributed `UnsupportedPlanException` is left unwrapped (category 1); a genuine backend
   runtime fault (e.g. `ExecutionMemoryException` from a refused reservation, `PhysicalRuntime.cs`), or
   any unforeseen exception from `IExecutionBackend.Open`/drain, is wrapped as `Stage = Backend`; a
   materialization runtime fault that is *not* already an `UnsupportedPlanException` is wrapped as
   `Stage = Materialize`.

### 3.3 Stage boundaries in the driver

`LocalQueryExecutor` executes each stage inside its own `try`/`catch`:

| Stage | Work | Unwrapped diagnostic | Wrapped as |
| --- | --- | --- | --- |
| `Plan` | `PhysicalPlanner.Plan` (node/expression mapping) | `UnsupportedPlanException(Plan)` | `QueryExecutionException(Plan, cause)` for any other planner fault |
| `Scan` | scan-source resolution inside planning (`PlanScan`, `PhysicalPlanner.cs:103-117`) plus #158 `LocalRelationBatches` deferred encode (`ScanPlan.Execute`) | `UnsupportedPlanException(Scan)` (scan-source miss or deferred-encode mismatch) | — (a deterministic unwrapped diagnostic) |
| `Backend` | `PhysicalPlan.Execute` (operator `Open`/drain) | `UnsupportedPlanException` (ill-typed bridge build) | `QueryExecutionException(Backend, cause)` for `ExecutionMemoryException` & unforeseen faults |
| `Materialize` | `RowMaterializer.Materialize` (+ final batch-list read) | `UnsupportedPlanException(Materialize)` (unrepresentable value) | `QueryExecutionException(Materialize, cause)` for a `ResultLimitExceededException` (§4.1) **or any other materialization fault** (a trailing general `catch` mirrors the `Backend` stage, so an unforeseen materialization fault is wrapped/attributed/metrics-bearing, never escaping raw) |

An `OperationCanceledException` from a cancelled effective token (a user cancel or an expired timeout
CTS) is caught by the **`Backend` and `Materialize`** stage catches and passed through `MapCancellation`,
which **rethrows** the original OCE for a user cancel or **synthesizes** a `TimeoutException` (preserving
the OCE as its cause) for a timeout — it is never wrapped in a `QueryExecutionException`. (Planning polls
no token and is preceded by the upfront gate, so the `Plan` stage has no dedicated OCE catch.) A
`QueryExecutionException` is never **double-wrapped** because the stage `try`s are **sequential, not
nested**: the wrapper an earlier stage throws propagates straight out of the method through the outer
`finally` (which only publishes metrics and disposes the runtime — it has no `catch`) and never enters a
later stage's `try`. Each stage `try` catches only the faults of *its own* work — its specific
diagnostic types (`UnsupportedPlanException`, plus `ResultLimitExceededException` in `Materialize`) and a
trailing general `catch (Exception)` that wraps exactly once — so a fault reaches the caller a single
time. Scan resolution runs *inside*
`PhysicalPlanner.Plan` in M1 (the in-memory scan source resolves during planning,
`PhysicalPlanner.cs:103-117`), so a scan-source miss is raised there; it is attributed to the distinct
`Scan` stage value (rather than `Plan`) so the failed stage is reported precisely. Now that the #158
read-door is merged, `LocalRelationBatches`' deferred row→batch encode (run inside `ScanPlan.Execute`)
raises its `UnsupportedPlanException` with the same `Scan` value, aligning the deferred-encode
data-in failure with the scan-source-miss case (design §3.3 intent).

---

## 4. Criterion 3 — memory / row limits before unbounded materialization

Two independent, opt-in bounds; both default to **unbounded** so no existing behavior changes.

### 4.1 Driver result bounds (the #416 query-bomb guard — the crux)

`PhysicalRuntime.Run` drains **every** batch of an operator's output into a `List<ColumnBatch>` and
`RowMaterializer` builds a `Row` per logical row — an unbounded driver-side materialization (#416: a
"query-bomb / OOM risk"). This story caps it **during accumulation**, before the whole result is
materialized:

- `MaxResultRows` (config `spark.deltasharp.execution.maxResultRows`): the maximum logical rows the
  final result materialization may accumulate. Enforced in `RowMaterializer.Materialize` — checked
  **before** each batch's rows are appended (`rowsSoFar + rowCount > cap` ⇒ throw), so the `Row` list
  is never grown past the cap.
- `MaxResultBytes` (config `spark.deltasharp.execution.maxResultBytes`): the same, measured in
  estimated batch bytes (a coarse fixed-width `rows × width` + variable-width value-length estimate,
  mirroring `InterpretedScanStream.EstimateBatchBytes`, `InterpretedScan.cs:70-98`, plus a conservative
  per-value/per-row CLR object-overhead factor — see the caveat below). Enforced incrementally in
  `RowMaterializer.Materialize`, checked before each batch is materialized.

> **Byte-cap accuracy caveat (#9).** `MaxResultBytes` bounds the **coarse columnar estimate**, *not* the
> materialized driver heap. The result the caller holds is a `List<Row>` of **boxed** CLR values whose
> real footprint (object headers, boxing, references, `List` slack) was measured ~37× larger than the raw
> columnar estimate. `RowMaterializer` now applies a conservative per-value (`~32 B`) and per-row
> (`~48 B`) object-overhead factor so the estimate is less wildly optimistic, but it is still an
> approximation and MUST NOT be read as a hard driver-heap bound. A heap-accurate *default* result-size
> guardrail (Spark's `spark.driver.maxResultSize=1g` analog) is deferred until the estimate is
> heap-accurate — tracked by [#434](https://github.com/khaines/deltasharp/issues/434).

The bounds are enforced **only at the final result-materialization boundary** (`RowMaterializer`), not
per intermediate operator: an intermediate operator may legitimately emit many rows that a downstream
`filter`/`limit`/aggregate then reduces, so bounding intermediates would wrongly reject valid queries.
The driver-side result the caller holds is the actual `#416` OOM risk, and that is exactly what
`RowMaterializer` accumulates, so capping there is both sufficient and precise. On breach `RowMaterializer`
raises the internal `ResultLimitExceededException`, which the driver's `Materialize` catch re-surfaces as
`QueryExecutionException(Materialize, "The result exceeds the configured maximum of N row(s)
(materialization reached M row(s)); increase 'spark.deltasharp.execution.maxResultRows' or add a narrower
filter/limit.", cause)` (the byte breach message is the `…maximum of N byte(s) (estimated materialization
reached M byte(s))…` analog) **before** appending past the cap — a deterministic, bounded failure, never
an OOM. `Count` is **not** row/byte capped: it sums `LogicalRowCount` without holding rows
(`RowMaterializer.CountRows`), so it is not a materialization OOM risk.

> **Unbounded-by-default (RATIFIED for M1).** Both result bounds and the operator memory budget default
> to **unbounded** — no config, no cap. This is an **intentional M1 decision**, not an oversight: it
> preserves existing behavior and keeps the local driver a faithful "run it and get the answer" surface.
> The trade-off is an **OOMKill risk** for a query whose result does not fit the driver heap. Operators
> opt into safety with `spark.deltasharp.execution.maxResultRows` / `maxResultBytes` /
> `memoryBudgetBytes` (§6.2). A heap-accurate *default* guardrail (Spark's `maxResultSize=1g` analog) is
> intentionally deferred until the byte estimate is heap-accurate (see the #9 caveat above), tracked by
> [#434](https://github.com/khaines/deltasharp/issues/434).

### 4.2 Operator memory budget

`MemoryBudgetBytes` (config `spark.deltasharp.execution.memoryBudgetBytes`): when set,
`PhysicalRuntime` builds its shared `ExecutionContext` over `new BoundedExecutionMemory(budget)`
instead of `BoundedExecutionMemory.Unbounded` (`BoundedExecutionMemory.cs:36`). Reserving operators
(aggregate/sort/join buffers) that exceed the budget with nothing to spill fail fast with
`ExecutionMemoryException` (`ExecutionMemoryException.cs`), which the `[Backend]` catch wraps as
`QueryExecutionException(Backend, cause)`. This bounds *intermediate* operator memory; the §4.1 result
bounds bound *driver materialization*. Together they give "bounded, not OOM."

---

## 5. Criterion 4 — planning & execution metrics

### 5.1 The `ExecutionMetrics` surface (public, Core)

An immutable diagnostics object retrievable after an action:

| Member | Meaning | Source |
| --- | --- | --- |
| `PlanningDuration` | wall-to-wall of `PhysicalPlanner.Plan` | driver `Stopwatch.GetTimestamp`/`GetElapsedTime` (monotonic; `DateTime.UtcNow` is BannedApi) |
| `ExecutionDuration` | wall-to-wall of `PhysicalPlan.Execute` + materialization | driver monotonic clock |
| `TotalDuration` | `PlanningDuration + ExecutionDuration` | derived |
| `OutputRows` | rows the action produced | final `BatchResult` logical row count |
| `OutputBatches` | batches the action produced | final `BatchResult` batch count |
| `BytesScanned` | estimated data-plane bytes read | Engine `OperatorMetrics.BytesScanned`, counted **once per genuine source read** (see below) |
| `PeakMemoryBytes` | high-water reserved memory | max Engine `OperatorMetrics.PeakMemoryBytes` |
| `SpilledBytes` | bytes spilled to the spill store during the run | summed Engine `OperatorMetrics.SpilledBytes` (0 under M1's default unbounded memory) |

Timings use the monotonic `Stopwatch` clock the Engine already standardizes on
(`OperatorMetrics.cs:54` / `InterpretedOperators.ElapsedNanos`) — **never** the banned wall clock.
`OutputRows`/`OutputBatches` are computed at the driver from the final result so they are populated for
**every** plan shape, including bare-scan/limit/union roots that never open an Engine operator.
`BytesScanned` is counted **once per genuine source read**. Each materialization boundary re-wraps its
child in an `InMemoryScanOperator` (`PhysicalRuntime.ScanOf`) whose `InterpretedScanStream` reports
`AddBytesScanned` as it streams, so naively summing every wrapper would over-count in proportion to plan
depth (a `project(filter(scan))` would measure `2×` the true bytes). Instead, `ScanOf` marks a wrapper
as a genuine source read exactly when its child subtree *reads source directly* —
`PhysicalPlan.ReadsSourceDirectly` (`PhysicalPlan.cs:91-95`): a `ScanPlan`, or a `Limit`/`Union` bridge
whose inputs all read source directly — and `AccumulateMetrics` counts `BytesScanned` only from those
source-marked scans, excluding every other (intermediate re-scan) wrapper. This makes the metric the
true source volume regardless of plan depth (`project(filter(scan))` = one source read), keeps a
`Limit`/`Union` bridge sitting between a scan and the nearest operator counted rather than zeroed, and
counts a union of two sources as their **sum** (`union(scan, scan)` = `2×`). Because counting rides the
stream, a cancelled or `Limit`-truncated run only accrues the bytes it actually pulled. A bare
scan/limit/union root that opens no consuming operator reports `0` (there is no source-read wrapper), an
accepted best-effort limitation of this diagnostic proxy.
`PeakMemoryBytes`/`SpilledBytes` aggregate the per-operator `OperatorMetrics` (`OperatorMetrics.cs`)
that `PhysicalRuntime.Run` snapshots after draining each operator (`PeakMemoryBytes` as a `max`,
`SpilledBytes` as a sum).

### 5.2 Retrieval on success **and** failure

- **Success.** Public overloads `DataFrame.Collect(out ExecutionMetrics metrics, CancellationToken = default)`
  and `Count(out ExecutionMetrics metrics, CancellationToken = default)` return the metrics alongside
  the result.
- **Failure.** For faults the driver **wraps** (`QueryExecutionException`, §3.3 — `Plan`/`Backend`/
  `Materialize` including a result-limit breach), `QueryExecutionException.Metrics` carries whatever
  accumulated before the fault (planning duration is present once planning completed — including a
  Plan-stage fault, which computes its elapsed from the planning start so `PlanningDuration` is real, not
  0; execution counters are partial). Independently, the `out` overloads themselves surface metrics on
  **every** exit path: the driver publishes the metrics snapshot into the per-call sink in a `finally`
  (so it holds partial metrics on cancel/timeout/throw as well as success), and each `out` overload reads
  that sink in its **own** `finally` before the exception unwinds — so a caller using
  `Collect(out metrics, …)` sees meaningful metrics (planning duration, partial counters) even when the
  action throws `OperationCanceledException`/`TimeoutException`. When no metrics were published (e.g. a
  fault before the driver ran) the `out` value is `ExecutionMetrics.Empty`. The unwrapped
  control/diagnostic types — `UnsupportedPlanException`, `OperationCanceledException`, `TimeoutException`
  — still carry no metrics **slot on the exception** by design (they are typed, self-identifying signals,
  not the general execution-error surface); the sink is the retrieval path for them.

### 5.3 The #179 (EXPLAIN) seam

`ExecutionMetrics` is a clean, retrievable, dependency-free object. The internal seam is a **per-call
`ExecutionMetricsSink`** — a small mutable holder allocated fresh by each action (never on the shared,
process-wide `ExecutionOptions.Default`, which carries **no** mutable per-run state, so two concurrent
actions cannot race or corrupt each other's metrics and `Default` never silently accrues state).
`LocalQueryExecutor` fills the sink in a `finally` on both the success and failure paths; sibling lane
#179 (EXPLAIN) can consume `ExecutionMetrics` via the public `out` overloads or the internal sink to
display physical-execution metadata. This story exposes the seam but **does not depend on** #179. A
Spark-style `QueryExecution.Metrics` accessor may later supersede the `out` overloads — tracked by
[#435](https://github.com/khaines/deltasharp/issues/435).

---

## 6. Public API additions & config keys

### 6.1 Public Core API (updates `PublicAPI.Unshipped.txt`; RS0016/RS0017 under `-warnaserror`)

- `enum DeltaSharp.QueryExecutionStage { Analyze, Plan, Scan, Backend, Materialize }`. `Analyze` is
  carried for pipeline parity but is **never emitted by the driver** — analyzer faults self-identify
  pre-seam via `AnalysisException` in Core (§3.2).
- `sealed class DeltaSharp.ExecutionMetrics` (immutable; the §5.1 members incl. `SpilledBytes` + ctor +
  `ToString` + the static `ExecutionMetrics.Empty`).
- `DeltaSharp.QueryExecutionException.Stage.get -> DeltaSharp.QueryExecutionStage?`,
  `.Metrics.get -> DeltaSharp.ExecutionMetrics?`, and new carrying constructors.
- `DeltaSharp.DataFrame.Collect(System.Threading.CancellationToken) -> …`,
  `Count(System.Threading.CancellationToken) -> long`,
  `Collect(out DeltaSharp.ExecutionMetrics!, System.Threading.CancellationToken = default) -> …`,
  `Count(out DeltaSharp.ExecutionMetrics!, System.Threading.CancellationToken = default) -> long`.

`ExecutionOptions` is **internal** (referenced by the internal `IQueryExecutor`), so it adds nothing to
the public surface. `UnsupportedPlanException.Stage` lives in the **non-packable** Executor assembly
(no PublicAPI tracking there — Directory.Build.props gates PublicApiAnalyzers on packable libraries).

### 6.2 Config keys (`spark.deltasharp.execution.*`, Spark-style, read from `SparkSession.Conf`)

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `spark.deltasharp.execution.timeoutMs` | long ms | unset → no timeout | action timeout (§2.4) |
| `spark.deltasharp.execution.maxResultRows` | long | unset → unbounded | driver row cap (§4.1) |
| `spark.deltasharp.execution.maxResultBytes` | long | unset → unbounded | driver byte cap (§4.1) |
| `spark.deltasharp.execution.memoryBudgetBytes` | long | unset → unbounded | operator memory budget (§4.2) |

Absent → the feature is disabled (unbounded / no timeout). A present value that is unparseable as an
integer, or a **negative** value, throws `ArgumentException` at read time (fail-fast, matching the
existing execution-backend key discipline, `RuntimeConfig.cs:160`); a parsed value **`== 0` disables**
the bound/timeout (treated as absent). A `timeoutMs` above `CancellationTokenSource.CancelAfter`'s
`uint.MaxValue - 1` ms (~49.7-day) ceiling is **clamped** to that ceiling rather than throwing a raw
`ArgumentOutOfRangeException` (§2.4, #7). Keys are read through the live `Conf` so a runtime `Conf.Set`
is honored on the next action.

---

## 7. Lazy/eager & Spark-parity notes

- **Lazy/eager preserved.** No transformation gains a side effect; only `Collect/Count/Show` execute
  (`DataFrame.cs:572-745`). The new overloads are still actions.
- **Deviations (documented).** Spark has no `Dataset.collect(CancellationToken)` and cancels via
  `SparkContext.cancelJobGroup`; the `CancellationToken` overloads and `TimeoutException` surface are a
  .NET-idiom adaptation. The `spark.deltasharp.*` limit keys are DeltaSharp-specific (Spark's nearest
  analogue is `spark.driver.maxResultSize`).

---

## 8. Failure-mode test plan

`tests/DeltaSharp.Executor.Tests/ExecutionBoundariesTests.cs` (seam + runtime, driven through
`InMemoryRelationFixture.CollectWithMetrics`/`CountWithMetrics`), `tests/DeltaSharp.Engine.Tests/
Execution/ExecutionContextDisposalTests.cs` (the #420 disposal contract), and
`tests/DeltaSharp.Core.Tests/Actions/DataFrameBoundaryActionTests.cs` (public action overloads + config
threading):

1. **Cancellation mid-action releases resources (AC1, non-vacuous).** A pre-cancelled token → `Collect`
   throws `OperationCanceledException`, and a *subsequent* normal collect over the same fixture succeeds
   with full output (proving the shared `ExecutionContext`/spill store was released and no state leaked).
   An **in-flight** fault-injected scan double is cancelled mid-drain through a disposal-tracking runtime
   factory seam (`PhysicalRuntime.IsDisposed` + `LocalQueryExecutor.RuntimeFactory`) that asserts the
   action stopped promptly **and** the runtime/context was disposed — this test **fails if the driver's
   disposal `finally` is removed**. Plus: `Count` (and empty-result `Collect`) with a **pre-cancelled**
   token over a *bare* `CreateDataFrame(...)` scan (and `Limit`/`Union` roots) throws
   `OperationCanceledException` via the upfront driver gate (#1). The Engine-level
   `ExecutionContextDisposalTests` inject a disposal-recording `ISpillStore` and assert
   `ExecutionContext.Dispose` disposes it, is idempotent (`DisposeCalls == 1` via the `_disposed` guard,
   #12), and is a safe no-op for a non-`IDisposable` store and for a context that never spilled.
2. **Timeout fires.** An already-elapsed timeout (`TimeSpan.Zero`) → `TimeoutException` (not the internal
   `OperationCanceledException`); a `Count` with a `TimeSpan.Zero` timeout over a bare scan → `TimeoutException`
   via the upfront gate (#1); a cancelled user token racing an elapsed timeout → the user cancellation
   wins (`OperationCanceledException`).
3. **Per-stage attribution + root cause (AC2, non-vacuous).** Each stage's failure is forced and the
   exact `QueryExecutionStage` **and** the specific `InnerException` root cause (identity/type, not just
   non-null) are asserted: `Scan` (schema-only relation → `UnsupportedPlanException`, `Stage == Scan`;
   **and** a #158 `LocalRelationBatches` deferred row→batch encode mismatch → `UnsupportedPlanException`,
   `Stage == Scan`); `Plan` (`CrossJoin` → `UnsupportedPlanException`, `Stage == Plan`); `Backend` (a
   4-byte `memoryBudgetBytes` refusing a filter's selection-vector reservation → `QueryExecutionException`,
   `Stage == Backend`, `InnerException is ExecutionMemoryException`, `Metrics != null`); `Materialize` (an
   out-of-range timestamp value → `UnsupportedPlanException`, `Stage == Materialize`; **and** an
   unforeseen materialization fault via a fault-injected batch list → `QueryExecutionException`,
   `Stage == Materialize`, `InnerException` is the injected fault, exercising the new general Materialize
   catch, #2).
4. **Row/byte limit trips deterministically (AC3, non-vacuous).** `maxResultRows`/`maxResultBytes` below
   the result size → `QueryExecutionException(Materialize)` with `InnerException is
   ResultLimitExceededException` and `Metrics != null`, **before full materialization** (a poison/sentinel
   batch that throws if its values are ever read proves the cap trips *before* the offending batch is
   materialized); exactly-at-limit passes and one-over-limit trips; `Count` is not row-capped.
5. **Metrics on success and failure (AC4, non-vacuous).** Success: the capturing fixture helpers return
   metrics with `OutputRows`/`OutputBatches` **non-zero where expected**, `PlanningDuration` present,
   `TotalDuration >= 0`, and correct `BytesScanned` (a known N-byte source over a 2-deep plan reports
   `N`, not `2N`; a `Limit`/`Union` **bridge** between the scan and the operator still reports `N` rather
   than `0`; and a `union(scan, scan)` reports `2N` — #3); the Core `Collect(out metrics, …)`/`Count(out metrics, …)` overloads surface the
   executor-published metrics (or `ExecutionMetrics.Empty` when none). Failure: the thrown
   `QueryExecutionException.Metrics` **and** the `out` metrics surfaced on a Backend/row-limit failure and
   on cancel/timeout carry meaningful values (planning duration present, partial counters) — not merely
   non-null (#4/#5). A concurrency test runs two actions over the shared `Default`-config and asserts they
   do not corrupt each other's metrics (no shared-static mutation).
6. **Config threading (discharges #416).** Session `spark.deltasharp.execution.*` keys thread into the
   seam's `ExecutionOptions` (`maxResultRows`/`maxResultBytes`/`memoryBudgetBytes`/`timeoutMs`); an
   absent config yields unbounded options; a non-numeric bound fails fast before the executor is invoked.
7. **No-regression.** Existing `EndToEndExecutionTests`/`MaterializationTests`/`DataFrameActionTests`
   pass unchanged (bounds default to unbounded; `UnsupportedPlanException` still propagates unwrapped
   from `fixture.Collect`).
