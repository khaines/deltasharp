# Operator spill paths for stateful operators (v1)

> **Status:** living document. Created with
> [STORY-03.6.2](../../planning/epics/EPIC-03-vectorized-execution-backend.md#story-0362-implement-spill-paths-for-stateful-operators)
> (#156). Documents how the four stateful interpreted operators — hash **aggregate**, **sort**, hash
> **join**, and local **exchange** — turn a refused memory reservation into a **spill** (serialize partial
> state to a spill store, release the reserved memory, and continue) instead of failing closed, so large
> inputs complete within a fixed budget. Builds directly on
> [STORY-03.6.1](../../planning/epics/EPIC-03-vectorized-execution-backend.md#story-0361-add-operator-memory-reservations-and-release-discipline)
> (#155): the reservation-refusal point that #155 documented as the fail-closed seam is **exactly** where
> spill hooks in here, and the `OperatorMetrics.SpilledBytes` field that #155 wired as a dormant `0` now
> carries real bytes. Grounded in [ADR-0001](../../adr/0001-execution-strategy.md) (the vectorized
> interpreter is the default and the parity oracle; NativeAOT-clean, no dynamic codegen on the data path)
> and [ADR-0002](../../adr/0002-columnar-batch-format.md) (`ColumnBatch`/`ColumnVector`). It reuses the
> EPIC-02 row serialization primitives ([`RowFormat/`](../../../src/DeltaSharp.Engine/RowFormat/)) for the
> on-spill encoding rather than inventing a second format. Update this doc whenever a spill trigger, the
> store abstraction, a per-operator merge algorithm, the I/O-failure contract, the exactly-once release
> model, or the `SpilledBytes` wiring changes.

## 1. The spill trigger — the #155 reservation-refusal seam

Every stateful operator reserves the byte cost of its **data-scaled** state before it materializes it,
through the unified [`IExecutionMemory`](../../../src/DeltaSharp.Engine/Execution/IExecutionMemory.cs)
budget (STORY-03.6.1 §2). Its concrete implementation
[`BoundedExecutionMemory`](../../../src/DeltaSharp.Engine/Execution/BoundedExecutionMemory.cs) is a strict
ledger: `TryReserve(bytes)` is all-or-nothing and rolls back its own add on overflow, and `Release(bytes)`
**throws** when asked to release more than is reserved — the over-release guard that proves exactly-once
release.

In #155 a refused `TryReserve` had nothing to fall back on and raised
[`ExecutionMemoryException`](../../../src/DeltaSharp.Engine/Execution/ExecutionMemoryException.cs)
(fail-closed). In this story the **same refusal point** is the spill trigger:

| Operator | Reservation site | On refusal (v2, this story) |
| --- | --- | --- |
| Aggregate | `ReserveOrSpill` ([`InterpretedAggregateStream.cs:420`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)) | spill in-memory groups to partitions, release, retry |
| Sort | buffer loop `TryReserve` ([`InterpretedSortStream.cs:280`](../../../src/DeltaSharp.Engine/Execution/InterpretedSortStream.cs)) | sort & write the current run, release, retry |
| Join | `TryReserveBuild` ([`InterpretedJoinStream.cs:698`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)) | switch to grace-hash, partition build + probe to disk |
| Exchange | per-batch `TryReserve` ([`InterpretedExchangeLocalStream.cs:184`](../../../src/DeltaSharp.Engine/Execution/InterpretedExchangeLocalStream.cs)) | spill that batch's partitions to disk |

The refusal is still detected by the ledger; the difference is the operator now **frees the reserved bytes
and continues** rather than throwing. `ExecutionMemoryException` is retained only as the terminal floor: a
single indivisible unit (one group, one row, one recovered partition, one output chunk) that cannot fit an
otherwise-empty budget still fails closed, because there is nothing left to spill (e.g.
[`InterpretedAggregateStream.cs:448-451`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs),
[`InterpretedJoinStream.cs:985-994`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)).

## 2. The spill store abstraction

[`ISpillStore`](../../../src/DeltaSharp.Engine/Execution/Spill/ISpillStore.cs) is the **where-the-bytes-go**
half of the seam (the memory model decides **when**). It is deliberately schema-blind — it moves opaque
byte records only — so one implementation serves all four operators and stays AOT-clean:

- `ISpillStore.CreateSegment(label)` hands out an append-only
  [`ISpillSegment`](../../../src/DeltaSharp.Engine/Execution/Spill/ISpillStore.cs); an operator opens one
  segment per spill partition or sorted run.
- `ISpillSegment.Write(record)` appends one already-framed record and exposes `BytesWritten` (the
  spill-bytes accounting unit). `OpenRead()` returns an `ISpillSegmentReader` that yields records in write
  order via `TryRead(out record)`.
- Every method documents `SpillIOException` as its failure mode, and `IDisposable` on the segment deletes
  the backing medium (for temp files, the file) on the normal, cancellation, **and** failure paths.

Implementations:

- [`MemorySpillStore`](../../../src/DeltaSharp.Engine/Execution/Spill/MemorySpillStore.cs) — segments are
  in-process `byte[]` lists. Lock-free, allocation-bounded, and deterministic, so it is the in-memory store
  tests and local in-process runs inject. It is **not** the production default: it frees the operator's
  reserved-budget bookkeeping but re-holds the spilled bytes on the GC heap (off-ledger), so it does not
  lower process memory — under real pressure that is worse than disk (Security F1).
- [`TempFileSpillStore`](../../../src/DeltaSharp.Engine/Execution/Spill/TempFileSpillStore.cs) — segments
  are length-prefixed records in a per-store temp directory under the OS temp root; it is the
  **production default** ([`ExecutionContext.cs:39`](../../../src/DeltaSharp.Engine/Execution/ExecutionContext.cs)),
  because it genuinely moves bytes out of process memory (#156 B1a). The temp directory is created **lazily**
  on the first segment (a context that never spills allocates no disk) with `UnixFileMode` **0700**, and each
  segment file is created **0600** via `FileStreamOptions.UnixCreateMode`, so spilled tenant rows are never
  group/world-readable on a shared pod (Security F3); `UnixFileMode` is a no-op on Windows. `Dispose` deletes
  the directory. Names use `Environment.ProcessId` + an `Interlocked` counter (no `Guid.NewGuid`, which the
  BannedApiAnalyzer forbids). The store is `IDisposable`; whoever builds the `ExecutionContext` (the executor)
  owns its lifetime and disposes it after the run.
- `FaultSpillStore` ([test](../../../tests/DeltaSharp.Engine.Tests/Execution/FaultSpillStore.cs)) — wraps a
  real store and throws `SpillIOException` deterministically on the Nth write (`FailOnWriteAfter`) or first
  read (`FailOnRead`), making the AC5 failure path reproducible.

The store is injected through
[`ExecutionContext.SpillStore`](../../../src/DeltaSharp.Engine/Execution/ExecutionContext.cs) (`init`-only,
defaulting to `TempFileSpillStore`); each operator captures it at `Open`.

### 2.1 Serialization reuse (RowFormat)

The on-spill encoding **reuses** the EPIC-02 row primitives rather than reinventing them:

- Operators that spill whole rows (sort runs, exchange partitions, join build/probe rows) frame each row
  with [`RowSpillCodec`](../../../src/DeltaSharp.Engine/Execution/Spill/RowSpillCodec.cs), which wraps the
  existing `BinaryRowEncoder` ([`RowFormat/`](../../../src/DeltaSharp.Engine/RowFormat/)) to encode a
  `ColumnVector[]` row to a `BinaryRow` frame and decode it back into mutable vectors.
- The aggregate spills **partial accumulator state**, not rows. Each aggregator implements
  `WriteState`/`MergeState`/`Reset` ([`Aggregators.cs`](../../../src/DeltaSharp.Engine/Execution/Aggregators.cs))
  and the partial records are framed with
  [`SpillStateWriter`/`SpillStateReader`](../../../src/DeltaSharp.Engine/Execution/Spill/SpillStateWriter.cs)
  (length-prefixed primitives), with the group **key** carried as its byte-sortable encoding.

Because the spilled bytes are produced by the same encoders the rest of the engine round-trips, decimal
scale/precision, widening, null lanes, and byte-sortable key order survive serialize→merge by construction.

## 3. Per-operator spill + merge algorithms

### 3.1 Aggregate — hash-partitioned partial-state spill (AC1)

The hash aggregate keeps a `Dictionary<RowKey, slot>` of grouped accumulators. On a refused reservation
(`ReserveOrSpill`, [`:420`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)) it calls
`SpillInMemoryGroups` ([`:458`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)):
each in-memory group is written to partition `FNV-1a(encodedKey) mod PartitionCount`
([`:477`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs), `PartitionCount = 16`,
[`:39`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)) as
`[encodedKey][keyFrame][per-aggregator WriteState]`, the dictionary is cleared, and the memory released;
the build then retries. A lone group that cannot fit an empty budget fails closed.

At emit, `LoadNextPartition` ([`:546`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs))
reads one partition at a time and `MergePartition`
([`:599`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)) folds each spilled partial
into a **fresh default accumulator** via `MergeState`; merging a partial into an identity slot reproduces
the spilled value, and because the **same key always hashes to the same partition**, every partial for a
group is co-located, so the merged fold equals the no-spill fold. Result-identity is therefore exact for
COUNT, SUM(integer/decimal), MIN, MAX, and AVG over integer inputs; the null-key group is a normal group
(its key frame round-trips), and ANSI/Legacy overflow is decided inside `MergeState` so it fires in both
spill and no-spill runs (§4.1). Spilled bytes are reported once per spill
([`:482`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)).

### 3.2 Sort — sorted runs + k-way merge (AC2)

When the sort buffer cannot reserve another row, the current buffer is sorted by `(keyBytes, globalSeq)` and
written as one **run** (`SpillCurrentRun`,
[`:319`](../../../src/DeltaSharp.Engine/Execution/InterpretedSortStream.cs)), the buffer's reservation is
released, and buffering resumes — so peak memory stays near one run. `(keyBytes, globalSeq)` is the **same
total order** the no-spill comparator uses: the #140 byte-sortable key first, ties broken by the global
input ordinal (stability). At emit, `FinishExternalSort`
([`:231`](../../../src/DeltaSharp.Engine/Execution/InterpretedSortStream.cs)) sorts and writes the final tail
as a run, then opens a cursor per run and a min-heap keyed by each run head's `(keyBytes, globalSeq)`. The
merge repeatedly pops the global minimum, so the merged stream is **byte-identical** to no-spill. Spilled
bytes are reported per run ([`:360`](../../../src/DeltaSharp.Engine/Execution/InterpretedSortStream.cs)).
NaN, `-0.0`, multi-key, and nulls-first/last all survive because they are properties of the byte-sortable
key, which is what the run records carry.

### 3.3 Join — grace-hash build partitioning + partitioned probe (AC3)

When the build hash table cannot reserve another row (`TryReserveBuild`,
[`:698`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)) the join switches to
**grace-hash** mode (`SwitchToGraceBuild`,
[`:716`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)): every already-buffered build
row is partitioned to disk by `FNV-1a(encodedKey) mod PartitionCount` (`PartitionBuildRange`,
[`:752`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)), the in-memory build state is
released, and the rest of the build batch streams straight to its partition. Build records carry
`[hasKey:bool][key bytes if hasKey][rowFrame]`; **null-key** build rows go to a dedicated null segment and
are never indexed. The probe is then drained and partitioned by the **same hash** (`PartitionProbe`,
[`:787`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)) with null-key probe rows to
their own segment.

Emit walks partitions one at a time (`AdvanceGracePartition`,
[`:849`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)): for each real partition it
rebuilds the in-memory hash table from that partition's build segment (`LoadBuildPartition`,
[`:907`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)) and streams the co-located probe
segment through the **unchanged** per-row emit machinery (`EmitLeftRow`/`Append*`). Two pseudo-partitions
follow: a probe-null partition (empty build → LEFT/FULL/ANTI surface unmatched probe rows) and a build-null
partition (empty probe → RIGHT/FULL surface unmatched build rows). Because a probe row and its matching
build rows hash identically, they always co-locate, so **cardinality (exact multiplicity)** is preserved
even when the key's partition is spilled, and the **null-key-never-matches** policy holds (null rows live in
segments that are never indexed). All six join types reuse the same machinery, so the output **multiset** is
identical to no-spill; only row **order** differs (partition order), which the tests compare as a multiset.
Spilled bytes are reported for build and probe
([`:312`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs),
[`:841`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)).

### 3.4 Exchange-local — per-partition buffer spill/recover (AC4)

The local exchange routes rows into `PartitionCount` buckets by FNV-1a hash and emits one batch per
partition. The in-memory path emits zero-copy selection views. When a batch's assignment vectors cannot be
reserved (`:184`), `SpillPartition`
([`InterpretedExchangeLocalStream.cs:218`](../../../src/DeltaSharp.Engine/Execution/InterpretedExchangeLocalStream.cs))
writes that batch's rows to one segment per partition (row frames via `RowSpillCodec`) and drops the batch.
On emit, `RecoverPartition`
([`:130`](../../../src/DeltaSharp.Engine/Execution/InterpretedExchangeLocalStream.cs)) reads a partition's
segment back into a materialized batch. The partition function is unchanged, so per-partition **row counts
and contents** match no-spill exactly. Spilled bytes are reported per spilled batch
([`:266`](../../../src/DeltaSharp.Engine/Execution/InterpretedExchangeLocalStream.cs)).

## 4. The result-identity guarantee (spill == no-spill)

The core correctness property is that spilling changes **only** where bytes live, never the result.

| Operator | Identity scope | Why |
| --- | --- | --- |
| Sort | **byte-identical** (ordered) | runs and merge use the same `(keyBytes, globalSeq)` total order as the no-spill comparator (§3.2). |
| Join | **identical multiset** (all 6 types) | matching keys co-locate by the same hash; per-row emit machinery is reused unchanged; only emission order differs (§3.3). |
| Exchange | **identical per-partition multiset** | same partition function; recover is a faithful row round-trip (§3.4). |
| Aggregate | **exact** for COUNT, SUM(int), SUM(decimal), MIN, MAX, AVG(int < 2^53) | partial-state fold is associative for these and every partial co-locates (§3.1, §4.1). |

**Honest scope (§4.2).** Aggregate SUM/AVG over `double` is **not** bit-identical under repartitioning
because IEEE-754 addition is non-associative: regrouping the summands across spilled partitions can change
the last ULP. This is a floating-point reality, not a spill defect; the identity tests therefore exercise
integer/decimal SUM and integer AVG (exact) and avoid asserting bit-identity on double sums. `AVG` over an
**integral** input accumulates its running sum in a `double` and is bit-exact only while the partial sums
stay `< 2^53` (the `double` integer-exact range); beyond that the same non-associativity applies. SUM
overflow cases use order-independent inputs (`long.MaxValue + long.MaxValue`, `9e37 + 9e37`) so ANSI throws
in both runs and Legacy nulls the group in both, regardless of fold order.

### 4.1 Overflow / decimal survival

`SumLong` accumulates in an **unchecked `Int128`** and `SumDecimal` in an **unchecked `Int128` unscaled
mantissa** ([`Aggregators.cs`](../../../src/DeltaSharp.Engine/Execution/Aggregators.cs)); the fit-to-`long`
/ `ToType` precision check is applied **once, to the FINAL value, at emit** (#156 B2). Because `Int128`
addition is modular and associative, the merged total equals the true sum whenever that sum fits `Int128`
— and any value that fits the result type necessarily does — so a spilled per-partition partial and the
single-pass no-spill fold reach the same final value and apply the same one check. This makes integer and
decimal SUM **exact under spill by construction**, and it closes the transient-overflow divergence the
Architect flagged (a cross-round transient that the true sum recovers from is never detected on either
path). `WriteState`/`MergeState` carry the raw `Int128` partial (decimal also carries the uniform input
scale) with **no intermediate overflow check**, so a `decimal(28,2)` sum survives serialize→merge unchanged
and final overflow follows `AnsiMode`: ANSI throws `ArithmeticOverflowException`
([`Types`](../../../src/DeltaSharp.Engine/Types/)), Legacy yields NULL.

## 5. The I/O-failure contract (AC5)

A spill-medium failure must **fail cleanly** — never corrupt or partially succeed. The contract has three
parts, all proven by the AC5 tests:

1. **Deterministic typed error.** A failed `Write`/`OpenRead`/`TryRead` raises
   [`SpillIOException`](../../../src/DeltaSharp.Engine/Execution/Spill/SpillIOException.cs) (an
   `InvalidOperationException`, distinct from the recoverable `ExecutionMemoryException`), carrying the
   `Operation`, `Detail`, and inner I/O fault.
2. **No partial output.** All four operators perform their spill writes inside the **build/partition phase
   that runs before any output row is emitted** (the aggregate spills during build; sort writes runs during
   build; join partitions during `EnsureBuilt`; exchange spills the batch before its first partition is
   emitted). So a write failure propagates out before a single output batch reaches the consumer — the test
   asserts the emitted row count is `0`.
3. **Release-all (exactly-once).** The exception unwinds to the stream's `Dispose`, which releases every
   outstanding reservation and disposes every segment (deleting temp files). Release runs under the
   `_disposed` guard with field-zeroing, and `BoundedExecutionMemory.Release` **throws on over-release**, so
   the AC5 tests' post-dispose assertion `ReservedBytes == 0` is positive proof of exactly-once release with
   no leak. A read failure during the sort merge phase is covered identically.

## 6. Exactly-once release on the spill and failure paths

Spill adds two release events beyond #155's normal/cancel/exception set, and both honor exactly-once:

- **On a successful spill**, the operator releases exactly the bytes it had reserved for the now-spilled
  state and zeroes its reservation field before continuing (e.g. aggregate `ReleaseInMemoryGroups`, join
  `SwitchToGraceBuild` [`:740-744`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs), sort
  buffer release in `SpillCurrentRun`). During emit, a recovered partition/run reserves its own working set
  and releases it before advancing to the next (e.g. join `AdvanceGracePartition`
  [`:857-861`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)).
- **On a spill failure**, `Dispose` releases whatever remains reserved exactly once.

The over-release ledger turns any double-release or release-without-reserve into a thrown
`ArgumentOutOfRangeException`, so the green release-to-zero assertions are evidence, not hope.

## 7. The `SpilledBytes` metric wiring

#155 added `OperatorMetrics.SpilledBytes` and `AddSpilledBytes` with zero callers (a dormant `0`). This
story makes it real: each operator sums the payload bytes it writes and calls `AddSpilledBytes` once per
spill event — aggregate
([`:482`](../../../src/DeltaSharp.Engine/Execution/InterpretedAggregateStream.cs)), sort per run
([`:360`](../../../src/DeltaSharp.Engine/Execution/InterpretedSortStream.cs)), join for build and probe
([`:312`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs),
[`:841`](../../../src/DeltaSharp.Engine/Execution/InterpretedJoinStream.cs)), and exchange per spilled batch
([`:266`](../../../src/DeltaSharp.Engine/Execution/InterpretedExchangeLocalStream.cs)). A non-zero
`Metrics.Snapshot().SpilledBytes` is the spill-happened signal the tests assert for non-vacuity.

## 7.1 The bounded blast-radius — memory ceiling → spill cap → fail-closed (#156 B1)

#155 enforced a fail-closed **memory** ceiling: a query that could not fit its reservation budget stopped.
#156 turns that refusal into a spill, which on its own would trade a bounded in-memory blast-radius for an
**unbounded disk** one — a tenant could fill a shared spill volume and `ENOSPC` its co-tenants (Security F2),
and the off-ledger `MemorySpillStore` default would re-hold the spilled bytes on the GC heap, untracked, to a
pod OOM (Security F1). #156 B1 restores a real, **bounded** ceiling end-to-end:

1. **Memory budget** ([`IExecutionMemory.BudgetBytes`](../../../src/DeltaSharp.Engine/Execution/IExecutionMemory.cs)) —
   the in-process reservation ceiling. On refusal the operator spills instead of failing.
2. **Spill to disk** — the default store is the disk-backed `TempFileSpillStore` (B1a), which actually
   relieves process memory; its temp dir/files are owner-only **0700/0600** (B1c, Security F3).
3. **Cumulative spill cap** ([`IExecutionMemory.MaxSpillBytes`](../../../src/DeltaSharp.Engine/Execution/IExecutionMemory.cs)) —
   a per-query ceiling on **total** bytes written to spill across every operator. Each operator records its
   spill against the run budget via `IExecutionMemory.RecordSpill(bytes)` at the same point it sums
   `AddSpilledBytes`. When the cumulative total would exceed the cap, `RecordSpill` throws the typed,
   deterministic [`SpillBudgetExceededException`](../../../src/DeltaSharp.Engine/Execution/Spill/SpillBudgetExceededException.cs);
   the operator releases **all** reservations exactly once (through the same `Dispose`/over-release-ledger
   path as a spill I/O failure) and emits **no** partial output.

So the blast-radius is bounded on both axes: memory by `BudgetBytes`, disk by `MaxSpillBytes`. The disk gap
is **not** unbounded — it is exactly the configured spill cap, and crossing it fails closed with the same
discipline as #359/#363. `BoundedExecutionMemory(budgetBytes, maxSpillBytes)` configures both; the legacy
single-argument constructor leaves the spill cap effectively unbounded (`long.MaxValue`) for callers that do
not set one.

## 8. AC coverage

| Acceptance criterion | Implementation | Test (in [`OperatorSpillTests`](../../../tests/DeltaSharp.Engine.Tests/Execution/OperatorSpillTests.cs)) |
| --- | --- | --- |
| AC1 — aggregate spill → merge == no-spill | §3.1 | `Aggregate_Spill_MatchesNoSpill_CountSumMinMaxAvg`, `…_NullKeyGroupPreserved`, `…_SumDecimal`, `Aggregate_Spill_PreservesOverflowSemantics(Ansi/Legacy)` |
| AC2 — sort runs k-way merged in global order | §3.2 | `Sort_Spill_MatchesNoSpill_SingleKey`, `…_MultiKey_NullsFirstLast` (×4), `…_DoubleNaNNegativeZero` |
| AC3 — join cardinality + null-key across all 6 types | §3.3 | `Join_Spill_MatchesNoSpill_AllTypes` (×6), `Join_Spill_PreservesMultiplicityAcrossPartitionBoundary` |
| AC4 — exchange per-partition counts/contents | §3.4 | `Exchange_Spill_MatchesNoSpill_PerPartitionCountsAndContents` |
| AC5 — I/O failure: release-all + deterministic error + no partial output | §5 | `Aggregate/Sort/Join/Exchange_SpillWriteFailure_ReleasesAll_DeterministicError_NoPartialOutput`, `Sort_SpillReadFailure_ReleasesAll_DeterministicError` |
| Temp-file hygiene (normal + failure + cancel) | §2, §5 | `TempFileSpillStore_NormalCompletion_DeletesAllTempFiles`, `TempFileSpillStore_WriteFailure_DeletesAllTempFiles`, `TempFileSpillStore_Cancellation_DeletesAllTempFiles` |
| B1a — executor default store relieves memory | §2, §7.1 | `ExecutorDefault_SpillStore_IsTempFileStore_NotMemory` |
| B1b — cumulative spill cap fails closed deterministically | §7.1 | `SpillBytesCap_Exceeded_FailsClosed_ReleasesAll_NoPartialOutput`, `SpillBytesCap_Generous_AllowsSpillToComplete` |
| B1c — owner-only temp dir/files (Unix) | §2 | `TempFileSpillStore_OnUnix_CreatesOwnerOnlyDirAndFiles` |
| B2 — transient-overflow SUM: spill == no-spill on in-range true sum | §4.1 | `Aggregate_Spill_TransientOverflow_FinalInRange_SpillEqualsNoSpill(Ansi/Legacy)` |

The identity tests run the **same input** under an ample budget (never spills) and a tight budget (forces
spill, asserted via `SpilledBytes > 0`) and require identical output — the spill-vs-no-spill oracle.
Non-vacuity is verified by mutation: dropping a spilled run from the sort k-way merge
(`FinishExternalSort`) flips every sort identity test to failing, and removing the join's
release-on-failure leaks a reservation that the AC5 `ReservedBytes == 0` assertion catches; both were run
and reverted.

## 9. Deferred

- **Recursive re-partitioning.** A single spilled partition whose recovered build still exceeds the budget
  fails closed (`InterpretedJoinStream.cs:985-994`, `InterpretedAggregateStream.cs` merge reservation)
  rather than re-partitioning recursively; the message says so. v1 assumes one hash level suffices for the
  configured `PartitionCount = 16`.
- **The output-chunk floor.** A spilling operator still needs enough budget to hold **one** output batch
  (up to `OutputBatchRows` rows) plus the heaviest recovered partition/run; the output chunk has no
  spillable representation in v1 (sort/join raise `ExecutionMemoryException` with that message). This is the
  irreducible emit floor, identical in spirit to #365's fixed-scaffolding note.
- **The cumulative spill cap is per-query, not per-volume.** `MaxSpillBytes` bounds one query's total
  spill; it does not coordinate across concurrent queries sharing a spill volume. Global volume admission
  (and remote spill below) is left to the scheduler/shuffle epic. Within a query the blast-radius is bounded
  (§7.1): memory by `BudgetBytes`, disk by `MaxSpillBytes`, both fail-closed.
- **Distributed / remote spill.** All spill here is node-local (`MemorySpillStore`/`TempFileSpillStore`).
  Cross-node and shuffle-service spill belong to the shuffle epic.

---

*This document matches the code as cited (line numbers against the worktree at the STORY-03.6.2 change
set). Update both together.*
