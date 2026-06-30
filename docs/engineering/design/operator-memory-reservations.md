# Operator memory reservations and release discipline (v1)

> **Status:** living document. Created with
> [STORY-03.6.1](../../planning/epics/EPIC-03-vectorized-execution-backend.md#story-0361-add-operator-memory-reservations-and-release-discipline)
> (#155). Documents how every interpreted operator reserves execution memory through the unified
> [`IExecutionMemory`](../../../src/DeltaSharp.Engine/Execution/IExecutionMemory.cs) budget **before** it
> allocates output vectors or scratch state, releases every reservation **exactly once** on the normal,
> cancellation, and exception paths, and reports the reservation surface through
> [`OperatorMetrics`](../../../src/DeltaSharp.Engine/Execution/OperatorMetrics.cs). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (the vectorized interpreter is the default and the
> parity oracle; NativeAOT-clean, no dynamic codegen on the data path) and
> [ADR-0002](../../adr/0002-columnar-batch-format.md) (`ColumnBatch`/`ColumnVector`). This story also
> absorbs the three accounting follow-ups deferred from the #148 relational-operator council (issue #155
> comments): (a) collection-overhead accounting, (b) within-batch cancellation granularity, and (c)
> output-side variable-width accounting. **Spill itself is out of scope** — it is
> [STORY-03.6.2](../../planning/epics/EPIC-03-vectorized-execution-backend.md#story-0362-implement-spill-paths-for-stateful-operators)
> (#156); v1 fails closed instead of spilling and wires `SpilledBytes` as a dormant `0`. Update this doc
> whenever a reservation site, the release model, an overhead constant, the cancellation interval, or the
> metrics surface changes.

## 1. The reservation seam

[`IExecutionMemory`](../../../src/DeltaSharp.Engine/Execution/IExecutionMemory.cs) is the byte-counting
budget an operator runs against. Its concrete v1 implementation,
[`BoundedExecutionMemory`](../../../src/DeltaSharp.Engine/Execution/BoundedExecutionMemory.cs), enforces a
fixed ceiling and — critically for this story — is a **strict ledger**:

- `TryReserve(bytes)` adds atomically and rolls its own add back when the new total exceeds the budget
  (or 64-bit-overflows), returning `false` without mutating the visible total
  ([`BoundedExecutionMemory.cs:36-48`](../../../src/DeltaSharp.Engine/Execution/BoundedExecutionMemory.cs)).
  A refused reservation therefore leaves **nothing** half-reserved.
- `Release(bytes)` **throws `ArgumentOutOfRangeException` when asked to release more than is currently
  reserved** ([`BoundedExecutionMemory.cs:51-61`](../../../src/DeltaSharp.Engine/Execution/BoundedExecutionMemory.cs)).
  This over-release guard is the proof surface for AC2 (§3): any double release or release-without-reserve
  throws, so a green test that drains the budget back to `0` is positive evidence of *exactly-once* release.

When a reservation is refused and the operator has nothing to spill (v1 always), it fails fast with
[`ExecutionMemoryException`](../../../src/DeltaSharp.Engine/Execution/ExecutionMemoryException.cs), carrying
the requested/available/budget figures for attribution.

> **Note (doc==code precision).** The v1 reservation contract is **fail-closed**: a refused reservation
> raises `ExecutionMemoryException` rather than spilling (spill is deferred to
> [STORY-03.6.2](../../planning/epics/EPIC-03-vectorized-execution-backend.md#story-0362-implement-spill-paths-for-stateful-operators)
> / #156). The pre-existing `BoundedExecutionMemory` class-summary phrasing "operators spill rather than
> fail" is EPIC-02 wording that predates this fail-closed model; correcting that stale comment is tracked
> separately and is intentionally out of this story's diff.

## 2. Reserve-before-allocate, per operator (AC1)

Every operator computes the byte cost of the state it is about to materialize, calls its private `Reserve*`
helper (which routes through `IExecutionMemory.TryReserve`), and only then mutates its buffers. Because
`TryReserve` is all-or-nothing, a refusal throws with the build left consistent — no **data-scaled** state
(a buffered row's columns, a hash-table group/entry, a per-key build index, a `bool[]` match flag, an
`_order` permutation slot, a copied output row, or a variable-width payload) is ever accumulated against
bytes the budget did not grant. This is the path that grows with tenant input, so it is the blast-radius
control STORY-03.2.2 (#148) / #359 hardened and this story completes.

> **Scope of the guarantee.** The reserve-before-allocate bound above covers the **data-scaled** state
> (everything that grows with the number/size of tenant rows). It does **not** yet cover the **fixed
> per-batch vector scaffolding** — each operator allocates its output/build `ColumnVector` backing arrays
> at the `OutputBatchRows = 1024` chunk capacity (`ColumnVectors.CreateForSchema(...)` /
> `ColumnVectors.Create(..., OutputBatchRows)`), so a wide-schema operator allocates
> `schema-width × ~8–12 KB` of scaffolding before any reservation. This is **bounded and predictable**
> (schema-width × a fixed 1024-row cap, known at plan time — not the unbounded per-payload class #359
> demonstrated) and is **GC-released between pulls** (one in-flight chunk per operator), but it is a
> deliberate exception to the bound: reserving it correctly is an output-batch-sizing / admission decision
> (right-size vs. eagerly fail-close a small-budget query) tracked in
> [#365](https://github.com/khaines/deltasharp/issues/365), alongside the #156 spill / output-sizing work.

| Operator | Reserve-before-allocate site | What is reserved |
| --- | --- | --- |
| [`InterpretedFilterStream`](../../../src/DeltaSharp.Engine/Execution/InterpretedFilter.cs) | `Reserve((long)passing * sizeof(int))` at [`:96`](../../../src/DeltaSharp.Engine/Execution/InterpretedFilter.cs) — **before** `new SelectionVector(...)` at `:97` | the retained selection vector (one `int` per surviving row) |
| [`InterpretedProjectStream`](../../../src/DeltaSharp.Engine/Execution/InterpretedProject.cs) | `Reserve(... * sizeof(long))` at [`:103`](../../../src/DeltaSharp.Engine/Execution/InterpretedProject.cs); per-expression evaluator scratch reserved internally by `Evaluate` at [`:139`](../../../src/DeltaSharp.Engine/Execution/InterpretedProject.cs); the gather var-width materialization reserved in `ReserveGather` at [`:174`](../../../src/DeltaSharp.Engine/Execution/InterpretedProject.cs)/[`:186`](../../../src/DeltaSharp.Engine/Execution/InterpretedProject.cs) | the projected-index map; the per-expression evaluator scratch; the gathered materialization of a passthrough `ColumnReference` under an input selection (fixed-width footprint at `:174`, plus its offsets + true value bytes at `:186`) |
| [`InterpretedSortStream`](../../../src/DeltaSharp.Engine/Execution/InterpretedSortStream.cs) | `ReserveBuffer(...)` at [`:184`](../../../src/DeltaSharp.Engine/Execution/InterpretedSortStream.cs) per buffered row; `ReserveChunk(...)` at [`:87`](../../../src/DeltaSharp.Engine/Execution/InterpretedSortStream.cs) per emitted chunk | the buffered rows + key bytes + permutation slot; the emitted-chunk index map |
| [`InterpretedAggregateStream`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs) | global group at [`:203`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs); each new group at [`:273`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs); output copy at [`:314`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs) | accumulator state + output estimate + key bytes + hash-entry overhead; MIN/MAX output copy |
| [`InterpretedJoinStream`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs) | `ReserveBuild(...)` at [`:243`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs) per build row; `ReserveOutput(...)` at [`:461`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)/`:480`/`:496`/`:512` per emitted row | buffered build columns + key + var-width + collection overhead; the output chunk |
| [`InterpretedExchangeLocalStream`](../../../src/DeltaSharp.Engine/Execution/InterpretedExchangeLocalStream.cs) | `Reserve(...)` at [`:140`](../../../src/DeltaSharp.Engine/Execution/InterpretedExchangeLocalStream.cs) | the per-row assignment + per-partition `counts`/`cursors`/bucket index arrays |

The expression evaluators reserve their result/scratch through the project/aggregate paths above; they do
not own a budget directly.

## 3. Exactly-once release across normal / cancel / exception (AC2)

The release model is uniform: **the operator that reserved bytes is the one that releases them**, and it
releases on whichever path it exits. The over-release-throwing ledger (§1) means an *over*-release is
self-detecting; a `_disposed` / `_reservedBytes == 0` guard makes every release idempotent so a *double*
release cannot occur.

- **Streaming operators** account for **at most one in-flight reservation**. **Filter** and **project**
  reserve one emitted batch per `TryGetNext`: each pull first calls `ReleaseReservation()` to free the
  previous emission, then reserves the current one; `Dispose` releases the last in-flight reservation. See
  `InterpretedFilter.cs` `ReleaseReservation`
  ([`:247-252`](../../../src/DeltaSharp.Engine/Execution/InterpretedFilter.cs)) and its `_reservedBytes = 0`
  guard, mirrored in project ([`:211-214`](../../../src/DeltaSharp.Engine/Execution/InterpretedProject.cs)).
  **Exchange** is also a streaming operator with at most one in-flight reservation, but its release cadence
  differs: it holds a single input batch's assignment-array reservation across the **N partition-view
  emissions** it produces from that batch and releases it inside `AdvanceInput`
  ([`:109`](../../../src/DeltaSharp.Engine/Execution/InterpretedExchangeLocalStream.cs)) only when it
  advances to the next input batch (and in `Dispose` for the final one), via the same `_reservedBytes = 0`
  guard ([`:219-223`](../../../src/DeltaSharp.Engine/Execution/InterpretedExchangeLocalStream.cs)) — see §4
  and §8.
- **Pipeline breakers** (aggregate, sort, join) hold their reservation for the operator's whole lifetime —
  the materialized result rows are live until the consumer finishes draining — so the bulk release happens
  in `Dispose`: aggregate at
  [`InterpretedAggregateStream.cs:136-156`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)
  (aggregator MIN/MAX best released first, then `_reservedBytes`, guarded by `if (_reservedBytes > 0)` and the
  `_disposed` flag), sort at
  [`:106-124`](../../../src/DeltaSharp.Engine/Execution/InterpretedSortStream.cs), and join at
  [`:148-184`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs) (the join additionally
  releases its one in-flight output chunk via `ReleaseOutputReservation` at the top of each `TryGetNext`,
  [`:122`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)).

Why this is exactly-once on each path:

- **Normal completion** — the consumer drains then disposes; `Dispose` releases the held reservation once,
  and the `_disposed` guard makes a second `Dispose` a no-op (a second `Release` of the same bytes would
  trip the ledger — proved by `Aggregate_NormalCompletion_ReleasesExactlyOnce_AndDisposeIsIdempotent`).
- **Cancellation** — within-batch polling (§5) or a batch-boundary check throws `OperationCanceledException`
  mid-build with the partial reservation still held; the enclosing `using`/`Dispose` releases exactly that
  partial amount (proved by `Aggregate_CancelledMidBuild_ReleasesAllReservations`).
- **Exception** — a refused reservation throws `ExecutionMemoryException` from inside a `Reserve*` helper.
  Because `TryReserve` already rolled its own failed add back (§1), `_reservedBytes` reflects only the
  *successful* reserves; `Dispose` releases exactly that, once (proved by
  `Join_ReservationRefused_ReleasesAllReservations_ExactlyOnce`).

The interpreter is a single-threaded pull pipeline, so these reserve/release sequences need no additional
locking; `BoundedExecutionMemory` itself is interlocked for the rare cross-operator shared budget.

## 4. Collection-overhead accounting — deferral (a)

The flat row estimates from #148 did not charge the managed *collection* structures the stateful operators
allocate (`Dictionary` buckets/entries, per-key `List<int>`, the `_matched bool[]`, and array-doubling
transients), so the real peak ran ~1.5–3× over the reserved figure. v1 now charges documented per-event
constants, defined in
[`RowSizeEstimate`](../../../src/DeltaSharp.Engine/Execution/RowSizeEstimate.cs):

| Constant | Value | Charged where |
| --- | --- | --- |
| `HashTableEntryBytes` | `64` | once per newly discovered aggregate group ([`InterpretedAggregateStream.cs:273`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)) and per distinct join build key ([`InterpretedJoinStream.cs:238`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)) |
| `ListHeaderBytes` | `48` | once when a join build key is first seen (new `List<int>` index) ([`InterpretedJoinStream.cs:238`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)) |
| `ListAppendBytes` | `sizeof(int) * 2` | per ordinal appended to an existing build-index list ([`InterpretedJoinStream.cs:237`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)) |
| `MatchFlagBytes` | `1` | per build row, for the `_matched bool[]` slot ([`InterpretedJoinStream.cs:230`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)) |
| `PermutationEntryBytes` | `sizeof(int) * 2` | per buffered sort row, for the `_order int[]` slot ([`InterpretedSortStream.cs:186`](../../../src/DeltaSharp.Engine/Execution/InterpretedSortStream.cs)) |

The constants embed array-doubling headroom (e.g. `*2` on the int slots) so the reserved figure **bounds**
the real peak in bytes rather than tracking only row count. The join decides this overhead **before** the
reserve so the reservation still precedes every mutation: it looks the bucket up once
([`:236`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)) and reuses that lookup for
both the cost decision and the later insert
([`:264-266`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)). The exchange charges its
`O(partitionCount)` `counts` + `cursors` arrays in the same `Reserve` as the per-row assignment
([`:140`](../../../src/DeltaSharp.Engine/Execution/InterpretedExchangeLocalStream.cs)). The global (no-key)
aggregate uses no hash table and is charged state + output only
([`:199-203`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)).

## 5. Within-batch cancellation granularity — deferral (b)

Operators already observe cancellation at batch boundaries (`ThrowIfCancellationRequested` at the top of
each `TryGetNext` and before each consumed input batch). The remaining window was a single **large** input
batch the producer did not chunk: its per-row build/buffer/assign/evaluate loop would otherwise run to
completion uncancellably. [`CancellationPolicy`](../../../src/DeltaSharp.Engine/Execution/CancellationPolicy.cs)
closes it:

- `RowPollInterval = 1024` rows
  ([`CancellationPolicy.cs:18`](../../../src/DeltaSharp.Engine/Execution/CancellationPolicy.cs)) — a power of
  two so `Poll`'s predicate is a single mask-and-branch, negligible against the per-row work it guards.
- `Poll(token, row)` calls `token.ThrowIfCancellationRequested()` only when `(row & (RowPollInterval - 1)) == 0`
  ([`CancellationPolicy.cs:27-33`](../../../src/DeltaSharp.Engine/Execution/CancellationPolicy.cs)). Row `0`
  is a poll point, so an already-cancelled token is seen immediately; the worst-case uncancellable run is
  bounded to `RowPollInterval` rows regardless of upstream batch size.

`Poll` is called in every per-row loop that can be fed an arbitrarily large batch: aggregate accumulate
([`:246`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)), join build
([`:223`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)) and join probe-emit
([`:344`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)), sort buffer
([`:176`](../../../src/DeltaSharp.Engine/Execution/InterpretedSortStream.cs)), exchange assign/route
([`:155`,`:169`,`:185`](../../../src/DeltaSharp.Engine/Execution/InterpretedExchangeLocalStream.cs)), filter
select ([`:160`,`:171`,`:186`,`:212`](../../../src/DeltaSharp.Engine/Execution/InterpretedFilter.cs)), and the
five scalar expression evaluators
([`ArithmeticEvaluator.cs:57`](../../../src/DeltaSharp.Engine/Execution/Expressions/ArithmeticEvaluator.cs),
[`CastEvaluator.cs:88`](../../../src/DeltaSharp.Engine/Execution/Expressions/CastEvaluator.cs),
[`ComparisonEvaluator.cs:45`](../../../src/DeltaSharp.Engine/Execution/Expressions/ComparisonEvaluator.cs),
[`LogicalEvaluator.cs:42`,`:54`](../../../src/DeltaSharp.Engine/Execution/Expressions/LogicalEvaluator.cs),
[`NullCheckEvaluator.cs:37`](../../../src/DeltaSharp.Engine/Execution/Expressions/NullCheckEvaluator.cs)).

## 6. Output-side variable-width accounting — deferral (c)

The input-side fix from #148 charged the *true* byte length of buffered string/binary values; the **output**
paths still reserved only the flat estimate, so a wide payload copied into an emitted chunk could exceed the
budget in bytes (a probe materialized ~51 MB under a 1 MB budget — bounded in row count, but not in bytes).
v1 adds a single-value overload
[`RowSizeEstimate.VariableWidthBytes(ColumnVector column, int row)`](../../../src/DeltaSharp.Engine/Execution/RowSizeEstimate.cs)
(returns the true `GetBytes(row).Length` for non-null string/binary, `0` otherwise) and charges it on the
output copies:

- **Join output chunk** — every `AppendJoinedRow`/`AppendLeftWithNullRight`/`AppendNullLeftWithBuild`/
  `AppendLeftOnly` charges the var-width of the left and/or build values it copies, on top of `_outputRowBytes`
  ([`InterpretedJoinStream.cs:461-462`,`:480`,`:496`,`:512`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)).
- **Aggregate MIN/MAX output** — `BuildResult` charges the var-width of each emitted MIN/MAX value as it is
  copied into the output column
  ([`InterpretedAggregateStream.cs:302-315`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)),
  symmetric with the input-side running-best retention the
  [`MinMaxAggregator`](../../../src/DeltaSharp.Engine/Execution/Aggregators.cs) reserves.

This keeps the "output stays bounded" claim true **in bytes**, not just in the `OutputBatchRows = 1024` cap.

## 7. Metrics surface (AC3)

[`OperatorMetrics`](../../../src/DeltaSharp.Engine/Execution/OperatorMetrics.cs) and its immutable
`OperatorMetricsSnapshot` expose the reservation plane:

| Field | Meaning | Maintained by |
| --- | --- | --- |
| `PeakMemoryBytes` ([`:38`](../../../src/DeltaSharp.Engine/Execution/OperatorMetrics.cs)) | high-water mark of reserved bytes; never decays | `ObservePeakMemory` / `ObserveReservation` |
| `CurrentReservedBytes` ([`:45`](../../../src/DeltaSharp.Engine/Execution/OperatorMetrics.cs)) | bytes currently held reserved; `0` once disposed | `ObserveReservation` (up) / `ObserveRelease` (down) |
| `AllocationCount` ([`:51`](../../../src/DeltaSharp.Engine/Execution/OperatorMetrics.cs)) | number of **operator-level** reserve events — one per call through an operator's `Reserve*` helper; **excludes** the aggregator's internal MIN/MAX running-best retentions (see below) | `ObserveReservation` |
| `SpilledBytes` ([`:35`](../../../src/DeltaSharp.Engine/Execution/OperatorMetrics.cs)) | **placeholder `0`** — wired but dormant; STORY-03.6.2 (#156) populates it | n/a in v1 |

`ObserveReservation(currentTotal)` bumps the count, sets current, and rolls the peak in one call
([`:94-99`](../../../src/DeltaSharp.Engine/Execution/OperatorMetrics.cs)); each `Reserve*` helper invokes it
after a successful `TryReserve`. `ObserveRelease(currentTotal)` lowers current on each release and refreshes
it after the out-of-band MIN/MAX retention growth, leaving the peak high-water mark untouched
([`:108`](../../../src/DeltaSharp.Engine/Execution/OperatorMetrics.cs)). For a blocking operator,
`CurrentReservedBytes == PeakMemoryBytes` while the result is held and falls to `0` on `Dispose`.

> **MIN/MAX retention and `AllocationCount`.** The [`MinMaxAggregator`](../../../src/DeltaSharp.Engine/Execution/Aggregators.cs)
> charges a retained string/binary running-best value's true byte length **directly** against the budget
> ([`Aggregators.cs:441-457`](../../../src/DeltaSharp.Engine/Execution/Aggregators.cs)), without routing
> through `ObserveReservation`; the aggregate stream folds that growth into `PeakMemoryBytes` /
> `CurrentReservedBytes` once per input batch via `ObservePeakMemory` + `ObserveRelease`
> ([`InterpretedAggregateStream.cs:221-223`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)).
> Consequently `AllocationCount` counts only operator-level `Reserve*` events and **does not** include those
> internal retentions — `PeakMemoryBytes` and `CurrentReservedBytes` stay byte-accurate, but a MIN/MAX
> retention does not increment the reserve-event count. This is intentional in v1: the count measures
> data-plane reserve calls (new groups, buffered rows, output chunks), not every byte-level adjustment.

## 8. Ownership-transfer contract (AC4)

Release ownership is held by the **producing** operator; a consumer never releases bytes it did not reserve:

- A streaming producer (filter/project/exchange) reserves its emitted batch's backing storage and releases
  it on the *next* pull or on `Dispose`. The downstream consumer reads the batch but performs no release —
  so there is no double-release and no leak. The ledger (§1) would throw on any double release; the final
  drain to `0` proves no leak (proved by `Filter_OwnershipTransfer_ReleasesPreviousBatch_NoDoubleReleaseNoLeak`).
- A blocking producer (aggregate/sort/join) owns its whole materialized result until `Dispose`; consumers
  hold slices but never release. The join's one in-flight output chunk is released by the producer at the top
  of each `TryGetNext`, never by the consumer.

Transient evaluator scratch is owned by the evaluating operator via `BatchEvaluationMemory`, reserved during
the batch and released in a `finally` (e.g. aggregate `scratch.Release()` at
[`:256`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)), so it never escapes the
batch that allocated it.

## 9. AC-coverage table

| Acceptance criterion | Implementation | Test (in [`OperatorMemoryReservationTests`](../../../tests/DeltaSharp.Engine.Tests/Execution/OperatorMemoryReservationTests.cs)) |
| --- | --- | --- |
| AC1 — reserve before use | §2 (all six operators reserve before allocating **data-scaled** state; fixed `OutputBatchRows` scaffolding tracked in [#365](https://github.com/khaines/deltasharp/issues/365)) | `Filter_OverBudget_FailsClosed_AndLeavesNoReservation` |
| AC2 — exactly-once release, normal | §3 + `_disposed`/`_reservedBytes` guards | `Aggregate_NormalCompletion_ReleasesExactlyOnce_AndDisposeIsIdempotent`, `Sort_NormalCompletion_ReleasesExactlyOnce`, `ExchangeLocal_NormalCompletion_ReleasesExactlyOnce` |
| AC2 — exactly-once release, cancellation | §3 + §5 | `Aggregate_CancelledMidBuild_ReleasesAllReservations` |
| AC2 — exactly-once release, exception | §3 + `TryReserve` rollback | `Join_ReservationRefused_ReleasesAllReservations_ExactlyOnce` |
| AC3 — peak/current/spill/alloc metrics | §7 | `Aggregate_Metrics_ReportPeakCurrentSpillAndAllocationCount` |
| AC4 — ownership transfer | §8 | `Filter_OwnershipTransfer_ReleasesPreviousBatch_NoDoubleReleaseNoLeak` |
| Deferral (a) — collection overhead | §4 | `Aggregate_HighCardinality_TightBudget_FailsClosed_OnCollectionOverhead`, `Join_HighCardinalityBuild_TightBudget_FailsClosed_OnCollectionOverhead` |
| Deferral (b) — within-batch cancellation | §5 | `CancellationPolicy_Poll_ThrowsOnlyAtIntervalBoundaries`, `Aggregate_CancelMidLargeBatch_ObservedWithinPollInterval` |
| Deferral (c) — output var-width | §6 | `Join_LargeStringOutput_TightBudget_FailsClosed_OnOutputVarWidth`, `Aggregate_MinMaxLargeString_TightBudget_FailsClosed_OnOutputVarWidth` |

The fail-closed tests use **knife-edge** budgets that sit strictly between the with-accounting and
without-accounting reserved totals (the doc-vs-code byte arithmetic is decoded in each test's comment), so
deleting the audited reservation flips the test from throwing to passing — verified by removing the aggregate
`HashTableEntryBytes` charge (the collection-overhead test then passes vacuously) and by widening
`RowPollInterval` past the batch size (the within-batch-cancellation test then runs to completion).

## 10. Deferred to STORY-03.6.2 (#156)

- **Spill.** v1 fails closed with `ExecutionMemoryException` when a stateful operator cannot reserve;
  STORY-03.6.2 turns the refusal into a spill for aggregate/sort/join/exchange and populates
  `OperatorMetrics.SpilledBytes` (wired as `0` here). No spill machinery — buffers, codecs, or merge — is in
  this story.

---

*This document matches the code as cited (line numbers against the worktree at the STORY-03.6.1 change set).
Update both together.*
