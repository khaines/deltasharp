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

All types live in `src/DeltaSharp.Core/Diagnostics/ExecutionAudit.cs`, namespace
`DeltaSharp.Diagnostics`. The whole seam is **`internal`** — it is an engine implementation detail,
not public API, so it adds nothing to the `DeltaSharp.Core` PublicAPI baseline (RS0016 stays clean;
`PublicAPI.Unshipped.txt` is untouched). `DeltaSharp.Core.Tests` observes it through the standard
`InternalsVisibleTo` grant (`Directory.Build.props`).

| Type | Kind | Role |
| --- | --- | --- |
| `IExecutionAudit` | `internal interface` | The sink real readers/backends notify at eager-only observation points. |
| `ExecutionAudit` | `internal static class` | The ambient accessor: a current-sink slot plus zero-alloc forwarders. |
| `ExecutionAudit.AuditScope` | `internal readonly struct : IDisposable` | Installs a sink for a scope and restores the previous one on dispose. |
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
internal static void FileOpened(string source)   => _current.Value?.OnFileOpened(source);
internal static void RowsRead(long count)         => _current.Value?.OnRowsRead(count);
internal static void StageEntered(ExecutionStage) => _current.Value?.OnStageEntered(stage);
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
  `BeginScope_RestoresPreviousSinkOnDispose`).

## Test doubles

In `tests/DeltaSharp.Core.Tests/LazyEager/`:

| Double | Role |
| --- | --- |
| `RecordingAudit : IExecutionAudit` | Counts files/rows with `Interlocked`, captures the ordered `ExecutionStage` path under a lock, and exposes `ObservedNoExecution` (all counters zero + empty path). |
| `FakeSource` | `Describe()` builds an `UnresolvedRelation` (pure construction, **no** audit); `Read()` simulates the eager scan and notifies `FileOpened`/`RowsRead`. This is the seam a real reader (#158) plugs into. |
| `FakeExecutionBackend` | `Execute(plan, source)` drives `Analyzer → Planner → source.Read() → Backend` through the seam — the substrate the #173 action + #174 backend bridge will drive. Building a plan never reaches it. |

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
  its execution and drives `ExecutionAudit.StageEntered(Analyzer)` → `Planner` → the reader's
  file/row notifications → `Backend`. The AC3 contract test asserts exactly this ordered path today
  against the substrate; when #173 lands, the `FakeExecutionBackend.Execute(...)` call in
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
   regression into the construction path — turned **3** lazy tests red
   (`TransformationsOnly_LeaveFileAndRowCountersAtZero`,
   `TransformationsOnly_DoNotInvokeAnyBackendStage`,
   `TransformationsOnly_OverExistingPlanFixture_ObservesNoExecution`). The mutation was reverted; the
   suite is green.

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
