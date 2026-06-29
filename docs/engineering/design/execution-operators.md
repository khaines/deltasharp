# Backend & operator execution contracts (v1)

> **Status:** living document. Created with
> [STORY-03.1.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-03-vectorized-execution-backend.md#story-0311-define-backend-and-operator-execution-contracts).
> Grounded in [ADR-0001](../../adr/0001-execution-strategy.md) (pluggable vectorized
> interpreter + optional JIT codegen tier) and [ADR-0014](../../adr/0014-target-framework-aot.md)
> (NativeAOT executor). Extends [execution-backend.md](execution-backend.md) from a single scalar
> seam to the v1 physical-operator contract. Update it whenever an operator, the metrics surface,
> or the unsupported-operator policy changes.

STORY-03.1.1 fixes the **shapes** a physical plan executes through, before any operator kernels
exist (those land in FEAT-03.2). Everything here is interfaces, immutable descriptors, and one
exception type — no row-loops, no kernels. The interpreter is the AOT-clean correctness ground
truth; the compiled tier only fuses hot expressions and must match it (ADR-0001). These contracts
live in the unshipped `DeltaSharp.Engine` assembly under `src/DeltaSharp.Engine/Execution/`;
`public` here is an engine-internal seam, not shipped surface.

## The contract surface

| Type | Role | AC |
| --- | --- | --- |
| `PhysicalOperator` | Immutable plan node: `Kind`, `OutputSchema`, `Children`, `Metrics`, `InputSchema(i)`. | AC1/AC2 |
| `ScanOperator`, `FilterOperator`, `ProjectOperator`, `AggregateOperator`, `SortOperator`, `JoinOperator`, `ExchangeLocalOperator` | The seven v1 operators, each validating its typed input/output. | AC2 |
| `PhysicalExpression` / `ColumnReference` | Resolved scalar contract operators carry (predicate, projection, key); type/nullability already decided. | AC1 |
| `SortOrder` (+ `SortDirection`, `NullOrdering`), `JoinType` | Spark-shaped key/join descriptors. | AC2 |
| `IBatchStream` | Pull-based, schema-stable `ColumnBatch` output of a running operator. | AC2 |
| `ExecutionContext` | Run-scoped controls: cancellation + `IExecutionMemory`. | AC1 |
| `IExecutionMemory` / `BoundedExecutionMemory` | Bounded reservation seam in front of the EPIC-02 memory manager. | AC1 |
| `OperatorMetrics` / `OperatorMetricsSnapshot` | Per-operator counters: input/output/selected rows, batches, scan/shuffle/spill bytes, peak memory, elapsed. | AC2 |
| `UnsupportedOperatorException` | Fail-fast, backend-attributed error; **no** row-at-a-time fallback. | AC3 |
| `IExecutionBackend.Supports` / `.Open` | Operator support test + `PhysicalOperator` → `IBatchStream`. | AC1–AC3 |

## How the contract maps to the inputs (AC1)

`backend.Open(op, ctx)` receives every required input: **schemas** and **expressions** on the
immutable `op` (its `OutputSchema`/`InputSchema(i)` and bound `PhysicalExpression`s), **batches** as
the `IBatchStream` it returns and pulls from children, **cancellation** via
`ctx.CancellationToken` observed at batch boundaries, and **memory context** via `ctx.Memory` —
operators reserve before allocating and spill on refusal so one query cannot exhaust shared
executor memory.

## Typed input/output + metrics per operator (AC2)

Each node fixes its schema contract at construction: filter/sort/exchange preserve the input
schema; project verifies one type-matched expression per output field; aggregate verifies
`keys + aggregates == output fields`; join verifies equal-length, pairwise-typed keys. `IBatchStream`
emits only `OutputSchema`-conforming batches. Every node owns an `OperatorMetrics` the backend fills
(filter→`SelectedRows`, scan→`BytesScanned`, exchange→`ShuffleBytes`), read as an immutable
`Snapshot()` by SRE/perf/FinOps. The surface reads no wall clock (banned), so it stays deterministic.

## Unsupported shapes fail fast (AC3)

`Supports(kind)` lets the planner pick a supported shape; `Open` on an unsupported shape throws
`UnsupportedOperatorException` (a `NotSupportedException`) naming the kind and backend. **No
row-at-a-time fallback exists** — silent degradation would mask plan regressions, hide parity gaps,
and forfeit columnar performance/cost bounds. In v1 every kind is unsupported by design; kernels
arrive in FEAT-03.2 behind this unchanged shape.

## AOT-clean interpreter (AC4)

No contract or interpreter type carries `[RequiresDynamicCode]`; the Engine builds with the
trim/AOT analyzers on, so a dynamic-code dependency would break the build. Only `CompiledBackend`
is annotated, reached solely behind `ExecutionBackends.IsCompiledBackendAvailable`, and elided from
NativeAOT — the interpreter needs no codegen to be correct.

## What is deferred

- Operator **kernels** and the SIMD library (FEAT-03.2/03.3); the general expression/function model.
- Differential parity oracle over operators, spill, and AQE/distributed exchange (later EPIC-03/11).
