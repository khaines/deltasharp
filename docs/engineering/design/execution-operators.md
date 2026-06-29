# Backend & operator execution contracts (v1)

> **Status:** living document. Created with
> [STORY-03.1.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-03-vectorized-execution-backend.md#story-0311-define-backend-and-operator-execution-contracts);
> extended with
> [STORY-03.2.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-03-vectorized-execution-backend.md#story-0321-implement-scan-filter-and-project-operators)
> (the first executable scan/filter/project operators).
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
and forfeit columnar performance/cost bounds. As of STORY-03.2.1 the backend supports `Scan`,
`Filter`, and `Project`; the remaining kinds (`Aggregate`, `Sort`, `Join`, `ExchangeLocal`) stay
fail-fast until their kernels land in later FEAT-03.2 stories, behind this unchanged shape.

## Executable scan / filter / project (STORY-03.2.1)

The first operator kernels land here, behind the unchanged STORY-03.1.1 shapes. They live in
`src/DeltaSharp.Engine/Execution/` as pull-based `IBatchStream` implementations, dispatched by the
internal, AOT-clean `InterpretedOperators`:

| Type | Role |
| --- | --- |
| `InMemoryScanOperator` | A `Scan` leaf binding an already-materialized `IReadOnlyList<ColumnBatch>` (the v1 executable source). |
| `InterpretedOperators` | Shared, recursive `Open(backendName, op, ctx)` dispatch + the monotonic `ElapsedNanos` helper. |
| `InterpretedScanStream` / `InterpretedFilterStream` / `InterpretedProjectStream` | The three operator streams. |
| `ExecutionMemoryException` | Typed budget-exceeded signal raised when a reservation is refused and there is nothing to spill (v1). |

**Both backends route operators through `InterpretedOperators`.** The interpreted backend and the
compiled tier delegate `Supports`/`Open` to the same dispatch, so operator results are identical
across backends *by construction* — the ADR-0001 parity oracle is trivially satisfied for operators
because the compiled tier's value is fusing scalar *expressions*, never reimplementing operators.

**Scan** binds data through `InMemoryScanOperator` because the bare `ScanOperator` carries only an
opaque `SourceId` (real Parquet/Delta and connector scans — pushdown, pruning, data skipping,
byte-accurate accounting — are owned by the storage and connector layers and bind behind this same
`Scan` shape later). `Open` on a bare, unbound `ScanOperator` is therefore `UnsupportedOperatorException`,
pointing at `InMemoryScanOperator`. The stream yields the source batches **verbatim** — preserving
schema, row count, column order, null metadata, and any pre-existing selection — and records
`InputRows`, `OutputRows`/batches, and an estimated `BytesScanned` (per-column fixed-width × rows, or
the sum of non-null variable-width lengths; a v1 proxy until the Parquet reader supplies the exact
figure).

**Filter** evaluates a boolean `ColumnReference` predicate into a `SelectionVector` of the passing
**logical** rows and exposes them via `ColumnBatch.WithSelection` — **no value column is ever copied**
(ADR-0002 late materialization), only "which rows survive" is materialized. Spark `WHERE` null
semantics: a row passes iff the predicate is `true` and not null. The no-selection path reads the
boolean value span directly; a selection-present path gathers through the selected view and composes
the two selections. A fully-filtered batch is **dropped** (the stream pulls the next input rather than
emit an empty batch). The transient passing-index buffer is rented from `ArrayPool<int>.Shared` (no
hot-path allocation); the retained selection vector is reserved against `ctx.Memory` before it is
built. Metrics: `InputRows`, `SelectedRows`, `OutputRows`.

**Project** produces the output batch by selecting/reordering the referenced input columns — a
**zero-copy** reorder/rename: output column *i* *is* the input column the *i*-th `ColumnReference`
names (shared, never copied), an output field may rename it (an alias), and duplicate references are
allowed. Any input selection is preserved, so the projected batch exposes the same logical rows.

**Cross-cutting behavior:**

- **Lazy / pull-based.** Building an operator (and `Open`) moves no rows; the first `TryGetNext`
  drives work, and an operator times only its own work (timing starts *after* the child pull, so
  `ElapsedNanos` never double-counts children). Each operator observes `ctx.CancellationToken` at the
  top of `TryGetNext` (a batch boundary) and throws `OperationCanceledException`.
- **Bounded memory.** Filter/project reserve before allocating and **release the previous batch's
  reservation at the top of the next `TryGetNext`**, capping outstanding reservations at one in-flight
  batch; `Dispose` releases the last reservation and the child. A refused reservation throws
  `ExecutionMemoryException` (v1 operators have no spillable state — turning this into a spill is the
  EPIC-02 memory manager's job). Reserved high-water marks roll into `PeakMemoryBytes`.
- **Interpreted, AOT-clean.** No new type carries `[RequiresDynamicCode]`; the kernels are reachable
  from the interpreted backend so NativeAOT keeps them and elides only `CompiledBackend`.

v1 only resolves `ColumnReference` predicates/projections; a richer expression (cast, arithmetic,
literal) raises `UnsupportedOperatorException` rather than degrade, until the interpreted expression
evaluator (STORY-03.4.1). Boolean columns are managed 1-byte vectors (Arrow bit-packed booleans have
no v1 columnar mapping), which is why the filter's value-span fast path is sound.

## AOT-clean interpreter (AC4)

No contract or interpreter type carries `[RequiresDynamicCode]`; the Engine builds with the
trim/AOT analyzers on, so a dynamic-code dependency would break the build. Only `CompiledBackend`
is annotated, reached solely behind `ExecutionBackends.IsCompiledBackendAvailable`, and elided from
NativeAOT — the interpreter needs no codegen to be correct.

## What is deferred

- Remaining operator **kernels** (aggregate, sort, join, exchange) and the SIMD library
  (FEAT-03.2/03.3); the general expression/function model and interpreted expression evaluator
  (STORY-03.4.1) that lifts the `ColumnReference`-only restriction on filter/project.
- Real **Parquet/Delta and connector scan sources** (predicate/partition pushdown, column pruning,
  data skipping, byte-accurate scan accounting) behind the `Scan` shape — owned by the storage and
  connector layers; `InMemoryScanOperator` is the v1 stand-in.
- **Spill** on refused reservations (today `ExecutionMemoryException`) and the differential parity
  oracle over the remaining operators and AQE/distributed exchange (later EPIC-03/11).
