# Plan-construction audit hooks for lazy/eager verification (M1)

> **Status:** living document. Created with
> [STORY-04.4.3](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md#story-0443-plan-construction-audit-hooks-for-lazyeager-verification)
> (FEAT-04.4, issue #169). Depends on STORY-04.4.1 (#167, immutable logical IR) and STORY-04.4.2
> (#168, expression IR). Grounded in [ADR-0001](../../adr/0001-execution-strategy.md) (pluggable
> execution backend) and the lazy/eager invariant that runs through all of EPIC-04. Extends
> [sparksession-lifecycle.md](sparksession-lifecycle.md) and [logical-plan-nodes.md](logical-plan-nodes.md).
> Update it whenever the audit seam shape, the observation points, or the wiring contract for
> transformations (#160) and actions (#173) changes.

## Why this exists

DeltaSharp's single most important invariant is:

> **Transformations are lazy; actions are eager.** Building a plan does **no** work. Only an action
> triggers the engine.

Every transformation (`select`, `filter`, `groupBy`, `join`, `withColumn`, …) must only extend an
immutable logical plan; every action (`collect`, `count`, `show`, `write`, …) is the *only* thing
that opens files, reads rows, or invokes the execution backend. A regression in which a
transformation accidentally executes would be silent, expensive, and semantically wrong.

This story adds the **audit substrate** that makes the boundary *observable*, and the **regression
tests** that fail if a transformation ever crosses it. It deliberately does **not** implement
transformations (`DataFrame.Select`/`Filter`, #160) or actions (`Collect`/`count`, #173); it builds
the seam those stories wire into, and the tests that will guard them.

## The audit seam

All types live in `src/DeltaSharp.Abstractions/Diagnostics/ExecutionAudit.cs`, namespace
`DeltaSharp.Diagnostics`. The whole seam is **`internal`** — it is an engine implementation detail,
not public API, so it adds nothing to the `DeltaSharp.Abstractions` PublicAPI baseline (RS0016 stays
clean; `PublicAPI.Unshipped.txt` is untouched in **both** `DeltaSharp.Abstractions` and
`DeltaSharp.Core`).

### Why the seam lives in `DeltaSharp.Abstractions`

The seam is a **shared internal contract straddling both siblings**, `DeltaSharp.Core` **and**
`DeltaSharp.Engine`, so — exactly like the internal `StableHash` under
[ADR-0016](../../adr/0016-shared-logical-type-model-abstractions.md) — it belongs in `DeltaSharp.Abstractions`, not in
`DeltaSharp.Core`:

- The **wiring points** below are **Engine-side**: a real source reader's eager pull loop (#158,
  EPIC-02/03) calls `ExecutionAudit.FileOpened`/`RowsRead`, and the #174 backend bridge calls
  `StageEntered(Backend)`. But `DeltaSharp.Engine` references **only** `DeltaSharp.Abstractions` and
  **never** `DeltaSharp.Core` (guarded by `Core_ReferencesNoEngineAssembly_*` /
  `CoreAssemblyDoesNotReferenceTheEngineAssembly`). A seam `internal` to `DeltaSharp.Core` would be
  **unreachable** from the very Engine callers the design names.
- Placing it in `DeltaSharp.Abstractions` (which both Core and Engine reference, preserving
  Core ⟂ Engine sibling independence) makes it reachable by everyone who must call it, while keeping
  every type `internal`. Cross-assembly access is granted through `InternalsVisibleTo` in
  `DeltaSharp.Abstractions.csproj`:
  - **`DeltaSharp.Engine`** — the pre-existing StableHash grant; the readers (#158) and #174 backend
    bridge reach the seam through it (no new grant needed).
  - **`DeltaSharp.Core`** — the future action driver (#173) and Core product code begin audit scopes
    and drive `StageEntered` during eager execution.
  - **`DeltaSharp.Core.Tests`** — the STORY-04.4.3 regression tests stay in `Core.Tests` (they also
    bind Core plan IR, `DeltaSharp.Plans.*`) and observe the internal seam through this grant.

  This mirrors ADR-0016's resolution for `StableHash`: a shared Core+Engine internal contract lives in
  `DeltaSharp.Abstractions` and stays internal, granted to the specific friend assemblies that need it.

| Type | Kind | Role |
| --- | --- | --- |
| `IExecutionAudit` | `internal interface` | The sink real readers/backends notify at eager-only observation points. |
| `ExecutionAudit` | `internal static class` | The ambient accessor: a current-sink slot plus zero-alloc forwarders. |
| `ExecutionAudit.AuditScope` | `internal readonly struct : IDisposable` | Installs a sink for a scope and restores the previous one on dispose (LIFO `using` contract). |
| `ExecutionStage` | `internal enum` | The ordered pipeline milestones: `Analyzer`, `Planner`, `Backend`. |

### Observation points

`IExecutionAudit` exposes exactly the three things that **only ever happen during eager execution**:

```csharp
internal interface IExecutionAudit
{
    void OnFileOpened(string source);          // a source reader opened a file/relation
    void OnRowsRead(long count);               // a source reader produced rows
    void OnStageEntered(ExecutionStage stage); // the pipeline entered analyzer/planner/backend
}
```

`OnFileOpened` and `OnRowsRead` are the **source-level counters** (AC1). `OnStageEntered` records the
ordered **pipeline path** — including "backend invoked" as `ExecutionStage.Backend` (AC2 for the
transformation case, AC3 for the action case). Keeping "backend invoked" as a stage rather than a
fourth method avoids redundancy: one seam records both the source I/O counters and the
analyzer → planner → backend path.

### Ambient accessor and thread-safety

`ExecutionAudit` holds the current sink in a **`static readonly AsyncLocal<IExecutionAudit?>`**:

```csharp
internal static void FileOpened(string source)      => _current.Value?.OnFileOpened(source);
internal static void RowsRead(long count)            => _current.Value?.OnRowsRead(count);
internal static void StageEntered(ExecutionStage stage) => _current.Value?.OnStageEntered(stage);
internal static AuditScope BeginScope(IExecutionAudit sink); // sets _current, returns restoring scope
```

Design choices:

- **`AsyncLocal` (not a global counter).** The current sink flows with the asynchronous control flow,
  so it is isolated **per executing action** and **per test** and survives `await` boundaries without
  a lock. Parallel xUnit tests cannot see each other's sinks. When #173's executor runs an action, it
  will `BeginScope` a listener scoped to that one action's execution.
- **Zero-overhead default.** When no sink is installed (the production default until an action
  installs one), each forwarder is a single `AsyncLocal` read and a null check — no allocation, no
  work. The forwarders are safe no-ops with no sink (proven by `Forwarders_WithNoSinkInstalled_AreNoOps`).
- **Thread-safe counting is the sink's job.** A single action may fan out across threads; the
  recording sink counts with `Interlocked` and guards its stage list with a lock, so it is safe to
  share across those threads.
- **Determinism.** Counters are plain `long`s; nothing here reads a clock, a GUID, or randomness
  (BannedApi-clean).
- **Nesting.** `AuditScope.Dispose` restores the *previous* sink, so scopes nest correctly (proven by
  `BeginScope_RestoresPreviousSinkOnDispose`). The scope models a **LIFO stack**: it must be
  `using`-disposed in the same asynchronous control flow that opened it, and nested scopes must be
  disposed in reverse order. Disposing out of order restores the wrong sink; the `using` pattern the
  tests and #173's executor follow guarantees LIFO by construction. This contract is documented on
  `BeginScope`/`AuditScope` (mirroring how #167's memoization documents its single-thread assumption).

## Test doubles

In `tests/DeltaSharp.Core.Tests/LazyEager/`:

| Double | Role |
| --- | --- |
| `RecordingAudit : IExecutionAudit` | Counts files/rows with `Interlocked`, captures the ordered `ExecutionStage` path under a lock, and exposes `ObservedNoExecution` (all counters zero + empty path). |
| `FakeSource` | `Describe()` builds an `UnresolvedRelation` (pure construction, **no** audit); `Read()` simulates the eager scan and notifies `FileOpened`/`RowsRead`. This is the seam a real reader (#158) plugs into. |
| `FakeExecutionBackend` | `Execute(plan, source)` drives `Analyzer → Planner → Backend → source.Read()` through the seam — a real backend is entered/invoked first and then drives the physical scan (the source read is part of backend execution), so the `Backend` stage is entered before `source.Read()`. This is the substrate the #173 action + #174 backend bridge will drive; building a plan never reaches it. The observed `StagePath` is `[Analyzer, Planner, Backend]` either way (a read adds no stage). |

## Wiring points future stories MUST call

The seam is the *contract*; the following future work fulfils it:

- **Source readers (#158, EPIC-02/03).** A real reader's eager pull loop calls
  `ExecutionAudit.FileOpened(...)` when it opens a file/relation and `ExecutionAudit.RowsRead(n)` per
  batch. A reader's *descriptor construction* (the `UnresolvedRelation` a transformation builds) must
  call **nothing** — mirroring `FakeSource.Describe()` vs `FakeSource.Read()`.
- **Transformations (#160).** `DataFrame.Select`/`Filter`/… must only build plan nodes (`Project`,
  `Filter`, …) exactly as the AC1/AC2 tests do today. They must never touch `ExecutionAudit`. The
  existing zero-counter tests become their standing guard automatically.
- **Actions + backend bridge (#173/#174).** An action (`Collect`/`count`/…) installs a sink scope for
  its execution and drives `ExecutionAudit.StageEntered(Analyzer)` → `Planner` → `Backend`, then the
  backend's physical scan issues the reader's file/row notifications (the scan is part of backend
  execution, so `Backend` is entered before the reads). The AC3 contract test asserts exactly this
  observed `StagePath` (`[Analyzer, Planner, Backend]`) today against the substrate; when #173 lands,
  the `FakeExecutionBackend.Execute(...)` call in
  `Action_ThroughFakeBackend_ObservesExpectedPathAndSourceReads` is swapped for the real action and
  the assertion holds unchanged.

## Acceptance-criteria → test mapping

| AC | Test(s) | Realized now vs substrate |
| --- | --- | --- |
| **AC1** fake source records opens/reads; transformations-only → counters zero | `TransformationsOnly_LeaveFileAndRowCountersAtZero`, `TransformationsOnly_OverExistingPlanFixture_ObservesNoExecution` | **Fully realized now.** Exercises today's construction path (`UnresolvedRelation`/`Project`/`Filter` + `DataFrame` wrap) and stands as the guard for #160's transformations. |
| **AC2** fake backend; transformations-only → no backend method invoked | `TransformationsOnly_DoNotInvokeAnyBackendStage` | **Fully realized now.** Asserts an empty stage path (no `ExecutionStage.Backend`) while only plans are built. |
| **AC3** action → exactly the expected analyzer/planner/backend path observable | `AuditSeam_DirectlyDriven_RecordsAnalyzerPlannerBackendPath` (pure substrate contract), `Action_ThroughFakeBackend_ObservesExpectedPathAndSourceReads` | **Substrate + contract now.** The action (#173) does not exist yet; the seam is proven to record the exact `Analyzer → Planner → Backend` path, and the contract test is the drop-in the real action upgrades. |
| **AC4** a transformation accidentally executing → at least one regression test fails | `RegressionGuard_FailsWhenATransformationAccidentallyExecutes` (self-contained non-vacuity), plus the AC1/AC2 zero-counter assertions | **Fully realized now.** The regression guard proves the zero-counter assertions are non-vacuous: a deliberately eager transformation is caught. |

### Non-vacuity

The AC1/AC2 zero-counter assertions are only meaningful if they can fail. Two things establish that:

1. **In-suite guard.** `RegressionGuard_FailsWhenATransformationAccidentallyExecutes` runs a
   deliberately buggy transformation that touches the seam and asserts the guard fires
   (`ObservedNoExecution == false`).
2. **Mutation proof (performed during development, reverted).** Temporarily making
   `FakeSource.Describe()` call `ExecutionAudit.FileOpened(...)` — i.e. injecting a real lazy/eager
   regression into the construction path — turned **exactly 3** tests red (empirically re-run on this
   suite):
   - `TransformationsOnly_LeaveFileAndRowCountersAtZero` — its `FilesOpened == 0` / `ObservedNoExecution`
     assertions fire.
   - `TransformationsOnly_DoNotInvokeAnyBackendStage` — its `ObservedNoExecution` assertion fires.
   - `Action_ThroughFakeBackend_ObservesExpectedPathAndSourceReads` — its **pre-execution**
     `Assert.True(recording.ObservedNoExecution)` (asserting nothing is observed *before* the action
     runs) fires, because `Describe()` now leaks a file-open at construction time.

   `TransformationsOnly_OverExistingPlanFixture_ObservesNoExecution` stays **green**: it builds from
   `PlanFixtures.SamplePlan()` and never touches `FakeSource`, so the mutation cannot reach it. The
   mutation was reverted; the suite is green.

## What is proven today vs guarded for the future

- **Proven today:** constructing the logical IR and wrapping it in a `DataFrame` performs no file
  opens, no row reads, and enters no pipeline stage. Structurally, `DeltaSharp.Core` references no
  engine assembly at all (`Core_ReferencesNoEngineAssembly_SoNoQueryWorkIsPossible` in
  `SparkSessionTests`), so construction *cannot* execute; this seam adds the fine-grained,
  future-proof behavioural guard on top.
- **Guarded for the future:** the seam is the single wiring point #158/#160/#173/#174 must use. The
  AC3 contract fixes the expected action path now so the real action lands against a pinned
  observable contract, and the AC1/AC2 guards automatically cover #160's transformations without new
  tests.
- **Known completeness gap (tracked by [#393](https://github.com/khaines/deltasharp/issues/393)).** The
  guard tests the **seam**, not real I/O: a future real reader (#158) that opens a file **without**
  calling `ExecutionAudit.FileOpened(...)` would evade the guard (a false green). When #158 lands, route
  every engine file-open through one audited reader abstraction, or add a BannedApi/Roslyn rule
  forbidding raw `File.Open`/stream opens outside it, and upgrade the AC1 contract test to drive a real
  reader end-to-end. A `TODO(#393)` marks the seam in `ExecutionAudit.cs`.
