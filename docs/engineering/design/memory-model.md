# Memory model: aligned off-heap buffer ownership (v1)

> **Status:** living document. Created with
> [STORY-02.3.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-02-columnar-memory-type-system.md#story-0231-implement-aligned-off-heap-allocation-ownership).
> Grounded in [ADR-0013](../../adr/0013-memory-model.md) (off-heap `NativeMemory`, 64-byte
> aligned, for in-memory batches) and [ADR-0002](../../adr/0002-columnar-batch-format.md) (the
> `ColumnBatch`/`ColumnVector` buffers this memory backs). Extended with
> [STORY-02.3.2](https://github.com/khaines/deltasharp/issues/138) (#138), which adds the **unified
> execution/storage memory manager** (one budget, two pools, per-task reservations, and spill
> triggers; spill targets per [ADR-0004](../../adr/0004-shuffle-architecture.md)). Update it whenever
> the buffer-ownership contract, the reservation/budget model, or their accounting changes.

DeltaSharp's columnar batches and binary row buffers need a memory strategy that is
**deterministically reclaimed**, **SIMD-aligned**, and **free of Large-Object-Heap (LOH) / gen-2
GC pauses** on large buffers. Per [ADR-0013](../../adr/0013-memory-model.md), the default is
**off-heap** (`NativeMemory`, 64-byte aligned — the same alignment Arrow C# uses), with **GC-heap
`ArrayPool<byte>`** reserved for small, short-lived scratch.

This story delivers the **ownership model**: who allocates a buffer, how it is aligned, how it is
released **exactly once**, and how live memory is accounted so leaks are visible. The pieces live in
the unshipped `DeltaSharp.Engine` assembly under `src/DeltaSharp.Engine/Memory/`; `public` there is
an engine-internal seam, not a shipped NuGet surface (see
[testing-conventions.md](testing-conventions.md)).

## The ownership surface

| Type | Role |
| --- | --- |
| `OwnedBuffer` | Abstract `IDisposable` byte buffer: `Length`, `IsNative`, `AsSpan()`, exactly-once disposal. |
| `AlignedNativeBuffer` | Off-heap buffer from `NativeMemory.AlignedAlloc(byteCount, 64)`; the aligned default. |
| `GcHeapBuffer` | Pooled `ArrayPool<byte>.Shared` rental for small scratch; `IsNative == false`. |
| `NativeMemoryAllocator` | Routes requests by size and tracks live native/scratch bytes and counts. |
| `BufferGroup` | Exception-safe owner of several buffers built together; `Detach()` transfers ownership out. |

A caller (a vector builder, a row encoder, a kernel) asks a `NativeMemoryAllocator` for an
`OwnedBuffer`, writes to `AsSpan()`, and disposes it once. It binds to the one `OwnedBuffer` contract
regardless of whether the bytes are off-heap or on a pooled array.

## Aligned off-heap as the default (AC1)

`AlignedNativeBuffer` wraps `NativeMemory.AlignedAlloc`. Every base address is **64-byte aligned**
(`AlignedNativeBuffer.Alignment`), and the physical allocation is **rounded up to a 64-byte multiple**
(`Capacity`) so a vectorized kernel can over-read the final partial vector (AVX-512 / `Vector512`)
past `Length` without faulting — Arrow-parity. `AsSpan()` still exposes **exactly** the requested
`Length` bytes — `new Span<byte>((void*)pointer, Length)` — and the alignment guarantee is a
root/offset-0 property: a sliced view starting at `base + offset` uses ordinary unaligned loads.
Off-heap bytes are invisible to the GC: large buffers never land on the LOH, never trigger a gen-2
compaction, and are reclaimed the instant they are disposed rather than at the next collection. This
requires `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` (scoped to `DeltaSharp.Engine.csproj`, **not**
`Directory.Build.props`); `NativeMemory` and unsafe pointers are AOT/trim-clean (the engine keeps
`EnableTrimAnalyzer`/`EnableAotAnalyzer` on) and are not on `BannedSymbols.txt`. A single `OwnedBuffer`
is capped at `int.MaxValue` bytes — an intentional batch-granularity constraint (a buffer holds one
column's vector for a 1k–8k-row batch, not an entire table).

If the aligned allocation fails, `AlignedNativeBuffer.Allocate` throws `OutOfMemoryException`
**before** any wrapper object is constructed or any counter is moved, so a failed allocation cannot
leak accounting.

## Zeroed by default; uninitialized is opt-in

`Allocate` returns **zeroed** memory (the secure default, like `new byte[]`): native buffers clear
their whole capacity, scratch clears its usable window. `AllocateUninitialized` skips zeroing for the
hot path that fully overwrites before reading (like `GC.AllocateUninitializedArray` / `ArrayPool`);
its bytes are arbitrary prior contents, so **the caller MUST write every byte before reading**. This
makes the data-bearing path safe by default (buffers flow to shuffle/spill and, on a shared executor,
across tenants) while preserving a zero-cost path for kernels that own initialization. `AsSpan()`
returns a raw view: it does **not** keep the buffer alive and **must not outlive it** — a span retained
past `Dispose()` dangles (use `GC.KeepAlive(buffer)` if the buffer is not otherwise rooted).

## Deterministic, exactly-once disposal (AC2)

`OwnedBuffer` implements the standard dispose pattern: public `Dispose()` calls `Dispose(true)` then
`GC.SuppressFinalize(this)`; `protected virtual void Dispose(bool disposing)` releases the backing
memory and decrements the owner's live counters. Release runs **exactly once**, guarded by an
`Interlocked.Exchange` flag — so a double `Dispose()` is a safe no-op, the counters never go
negative, and there is no race between an explicit dispose and the finalizer. After disposal,
`AsSpan()` throws `ObjectDisposedException`.

Because every successful allocation increments a live counter and every release decrements it, **once
all buffers are disposed the live counters return to zero** — the leak signal the unit tests assert.

### Finalizer as a safety net only

`AlignedNativeBuffer` carries a finalizer **purely as a safety net**: if a deterministic `Dispose()`
is missed, the native memory is still freed (so the process does not leak off-heap memory) and the
allocator's `FinalizedWithoutDispose` diagnostic counter is bumped, surfacing the missed dispose in
tests and telemetry. A finalizer run is distinguished by `disposing == false`; reaching it means a
leak. Correct callers always dispose (directly or via a `BufferGroup`), which suppresses
finalization. A raw finalizer (not `SafeHandle`/`CriticalFinalizerObject`) is the deliberate v1
choice: the resource is a plain `NativeMemory` pointer with no handle-recycling surface, and
`SafeHandle`'s ref-count protection is moot because `AsSpan()` hands out a span that escapes any
`DangerousAddRef` scope — the single-owner rooting contract is the real mechanism (revisit if buffers
become concurrently shared or a pointer is held across a racing P/Invoke). `GcHeapBuffer` needs
**no** finalizer: its backing store is a managed array the GC reclaims on its own, and returning an
array to a pool from a finalizer is undesirable anyway. It returns the array with `clearArray: true`,
so pooled scratch never carries one caller's bytes into the next renter (defense-in-depth for row/PII
data; scratch is small, so the zeroing cost is negligible).

**Scratch leak asymmetry (intentional).** Because `GcHeapBuffer` has no finalizer, a *leaked*
(never-disposed) scratch buffer is reclaimed by the GC but its `LiveScratch*` counters are **never
decremented** and `FinalizedWithoutDispose` is **not** bumped — the leak is invisible to the
diagnostics. This is an accepted trade-off: a missed scratch dispose costs only pool churn, not a
process memory leak, so it does not warrant a finalizer on the cheap path. A *native* leak, by
contrast, is always freed and flagged. A debug-only scratch-leak tracker remains a possible future
diagnostic; the #138 unified manager does not add one, since it accounts logical reservations rather
than scratch-buffer disposal.

### Observing the real release

The `Live*` gauges balance to zero on dispose, but balancing alone does not prove the backing
memory was actually freed (a release that silently stopped freeing would still let the base
`Dispose` decrement the gauge). So each concrete release also increments a cumulative
**operation** counter from inside the release itself — `NativeFreeOperations` at the
`NativeMemory.AlignedFree` boundary and `ScratchReturnOperations` at the `ArrayPool.Return`
boundary. Tests assert these equal the number of allocations, which catches a no-op or
double-release that the gauges cannot.

## Execution vs. scratch split (AC4)

`NativeMemoryAllocator.Allocate(int byteCount)` routes by size:

- `byteCount <= ScratchThreshold` → a `GcHeapBuffer` (pooled `ArrayPool<byte>` rental), accounted in
  the **scratch** counters. The default threshold is 4&#160;KiB (`DefaultScratchThresholdBytes`),
  well under the ~85&#160;KB LOH threshold, and is configurable via the constructor.
- otherwise → an `AlignedNativeBuffer`, accounted in the **native** counters.

A **large request is never satisfied from the scratch path**, and a scratch request never allocates
native memory. The two pools are accounted **separately** through `Interlocked`-updated counters so
memory pressure and leaks can be attributed to the right pool:

| Counter | Meaning |
| --- | --- |
| `LiveNativeBytes` / `LiveNativeCount` | Off-heap bytes / buffers currently undisposed. |
| `LiveScratchBytes` / `LiveScratchCount` | Pooled GC-heap scratch bytes / buffers currently undisposed (no finalizer backstop — see the scratch leak asymmetry above). |
| `FinalizedWithoutDispose` | Native buffers reclaimed by the finalizer safety net (a leak signal). |
| `NativeFreeOperations` | Cumulative native frees actually performed at the `AlignedFree` boundary (observes the real release). |
| `ScratchReturnOperations` | Cumulative scratch arrays actually returned to the pool. |

Counters are accounted by the **requested** byte count (a pooled array may be larger than requested,
but the logical usable size is what is tracked), so allocate and release are symmetric and balance to
zero. Each counter is updated atomically and the live counters return to zero once every buffer is
released, but the gauges are **not** a jointly-consistent instantaneous snapshot — a concurrent reader
can observe `LiveNativeBytes` and `LiveNativeCount` mid-flight between two `Interlocked` ops, so build
leak assertions on quiescent state, not on a coherent multi-counter read during live concurrency.

## Exception-safe multi-buffer construction: `BufferGroup` (AC3)

Building one columnar vector often needs **several** buffers at once (values + validity bitmap;
offsets + data for variable-width). If a later allocation throws `OutOfMemoryException`, or a
validation step rejects the input partway through, the buffers already allocated would leak.
`BufferGroup` is the exception-safe owner: its `Allocate` delegates to the allocator and **tracks**
each buffer (disposing it immediately if even adding it to the tracking list fails), and its
`Dispose()` releases **all** tracked buffers in reverse allocation order, **aggregating** any disposal
failures into a single `AggregateException` so one failure cannot strand the rest.

The intended idiom is **`using` + `Detach()`**:

```csharp
using var group = new BufferGroup(allocator);
OwnedBuffer values   = group.Allocate(valueBytes);
OwnedBuffer validity = group.Allocate(validityBytes);
// ... more construction that may throw ...
return new NativeColumnVector(group.Detach()); // success: ownership transferred out
```

If anything throws before `Detach()`, the `using` disposes the group and reclaims every buffer (live
counters return to zero). On the success path `Detach()` transfers the buffers out and **empties** the
group, so the trailing `using` disposal releases nothing — the new owner now holds the live buffers
and is responsible for disposing them. This is the off-heap analogue of a scope guard /
"commit-or-rollback" for native memory. Note the handoff is exactly-transfer: detach **into** the
consumer and let it take ownership only after all throwing work, e.g.
`var bufs = group.Detach(); try { return new NativeColumnVector(bufs); } catch { foreach (var b in bufs) b.Dispose(); throw; }` — otherwise a throwing consumer constructor after a bare `Detach()` orphans the buffers (reclaimed late by the native finalizer; scratch leaks silently per the asymmetry above).

## Unified execution/storage memory manager (STORY-02.3.2)

The ownership layer above hands out and reclaims individual buffers; it does not **bound** how much an
executor holds at once. [STORY-02.3.2](https://github.com/khaines/deltasharp/issues/138) (#138) adds the
**unified memory manager** ADR-0013 calls for — Spark's `UnifiedMemoryManager` analog — a **reservation
ledger** governing one executor-wide budget so a single unbounded query cannot exhaust shared memory. It
lives beside the allocator in `src/DeltaSharp.Engine/Memory/` and is a **logical byte ledger**: it
accounts reservations and triggers spills; it does **not** itself allocate. Callers reserve here, then
allocate the physical bytes from a `NativeMemoryAllocator`, and release both in reverse. It is the
machinery the operator-facing `IExecutionMemory` seam (`BoundedExecutionMemory`) is designed to front;
connecting the two lives in `Execution/`, outside this story's `Memory/`-only scope.

| Type | Role |
| --- | --- |
| `UnifiedMemoryManager` | Owns the total budget, both pools, the soft boundary, and the borrow/spill orchestration. One per executor. |
| `MemoryPool` / `MemoryPoolKind` | Accounting for one region — `Execution` (non-evictable scratch) or `Storage` (evictable cache) — with its shifting `PoolSizeBytes`/`UsedBytes`/`FreeBytes`. |
| `TaskMemoryManager` | The per-task reservation handle (Spark's `TaskMemoryManager`): tracks the task's used bytes and its reservations; disposing it releases them all. |
| `MemoryReservation` | One reservation — the budget analogue of `OwnedBuffer`: exactly-once release, shrink-on-spill. |
| `ISpillable` / `SpillCallback` / `DelegateSpillable` | The spill trigger: a consumer the manager asks to free reserved bytes under pressure. |
| `MemoryBudgetExceededException` | The deterministic budget-exceeded error (task id, requested bytes, full pool state). |

### Two pools, one budget, a soft borrowing boundary (AC1)

The budget is split into an **execution** region (non-evictable scratch for sorts, joins, aggregation)
and a **storage** region (evictable cached/broadcast batches), sized at construction by
`StorageRegionBytes` (default half — Spark's `spark.memory.storageFraction`). The split is a **soft,
shifting boundary**: a pool grows by **borrowing the other pool's free capacity** — execution may take
all of storage's free space (the storage region only caps execution to the extent storage is *using*
memory), and storage may take execution's free space (execution is never evicted on storage's behalf).
Borrowing is a boundary shift (`IncrementPoolSize`/`DecrementPoolSize`), so the two pool sizes always
sum to the budget. Each pool reports `PoolSizeBytes`/`UsedBytes`/`FreeBytes`, and each
`TaskMemoryManager` reports its own `ExecutionUsedBytes`/`StorageUsedBytes`, so available bytes, used
bytes, and **task ownership** are all accurately attributable (AC1).

### Per-task reservations and release discipline

A task reserves through a `TaskMemoryManager` (`manager.RegisterTask(taskId)`) **before** it allocates,
and holds each grant as a `MemoryReservation` (`IDisposable`). Release is the budget analogue of the
ownership layer's exactly-once disposal: `MemoryReservation.Dispose()` returns the reservation's
remaining bytes to its pool and task exactly once (Interlocked-latched, so a double dispose or a
disposer race is a safe no-op and totals never go negative), and disposing the `TaskMemoryManager`
releases every reservation the task still holds — so a finished or failed task cannot leak budget.

### Spill triggers: spill-or-fail, never OOM (AC2, AC3, AC5)

When a reservation would push a pool over budget *after* borrowing, the manager invokes the requesting
task's **spillable reservations** before it rejects (AC2). An `ISpillable` consumer (a sort buffer, hash
aggregate, join build side) serializes in-memory state to a spill target supplied by later runtime
layers — local disk / object store, [ADR-0004](../../adr/0004-shuffle-architecture.md) — and returns
the bytes it freed; the manager then **releases exactly that many bytes** from the consumer's
reservation and the pool/task totals (AC5), shrinking the reservation so a later `Dispose()` releases
only the remainder. If spilling frees enough, the reservation is granted; if not, it **fails
deterministically** — `TryReserve` returns `null` and `Reserve` throws `MemoryBudgetExceededException`
carrying the task id, requested bytes, and full pool state (AC3). Either way the failure is a bounded,
reproducible signal — **not** an `OutOfMemoryException`: the manager bounds memory by refusing the
reservation, never by exhausting the process.

The manager only ever spills the **requesting task's own** spillable reservations, so one task spilling
never decrements or leaks a concurrent task's accounting (AC4). Spill callbacks run while the manager
holds its coordinating lock, so a v1 `Spill` must be fast and must not re-enter the manager — it frees
buffers and returns a count; the manager is the sole accountant (the callback must not also release, or
bytes would be double-counted).

### Deterministic accounting and thread-safety (AC4)

Per-field byte counters are updated through `Interlocked`, so the public gauges are torn-free for a
lock-free observer (metrics, leak assertions), matching the allocator's counters — but they are **not** a
jointly-consistent multi-field snapshot during live concurrency, so assert on quiescent state. The
compound reserve/borrow/spill *decision* is serialized by one coordinating lock per manager, because it
must atomically read-and-shift two pools and orchestrate spills — a multi-variable invariant
`Interlocked` alone cannot hold. Once every reservation and task is released, each pool's `UsedBytes`
returns to zero **and** the soft boundary resets to the configured regions, so the manager returns to its
exact initial baseline — the deterministic-accounting signal the unit tests assert (borrowing across
pools, reserve/release balancing to zero, concurrent reserve/release, and double-release safety).

## v1 scope and deferrals

- **In scope:** the `OwnedBuffer` ownership contract; the aligned off-heap default
  (`AlignedNativeBuffer`); the small-scratch GC-heap path (`GcHeapBuffer`); the size-routing,
  separately-accounted `NativeMemoryAllocator`; exactly-once disposal with a finalizer safety net and
  leak counters; and the `BufferGroup` exception-safety pattern.
- **Deferred — off-heap `ColumnVector` lifetime:** `OwnedBuffer` is single-owner/exactly-once, but
  ADR-0002 `ColumnVector.Slice`/`WithSelection` are **zero-copy views that share buffers**. A future
  off-heap vector therefore needs an explicit lifetime model (ref-count, root-keepalive on each slice,
  or batch-arena dispose) — the disposal analogue of #133's seal-on-slice — and SIMD over-read on
  shared tails is already handled by the 64-byte capacity padding. This stays the key design risk for a
  future off-heap vector; the #138 manager sidesteps it by accounting bytes logically rather than owning
  shared buffers.
- **Delivered by [STORY-02.3.2](https://github.com/khaines/deltasharp/issues/138) (#138):** the
  **unified execution/storage memory manager** documented above — two pools over one shared budget with
  free-space borrowing, per-task reservations with exactly-once release discipline, and spill triggers
  that spill-or-fail (never OOM). It builds on this ownership primitive without reworking it: the manager
  is a logical reservation ledger, and the per-instance allocator (no global pool) still owns the
  physical bytes — reserve in the manager, allocate from the allocator, release both in reverse.
- **Deferred — the manager's own follow-ups:**
  - **Physical wiring:** the manager is not yet connected to `NativeMemoryAllocator` (reserve → allocate)
    or to the operator-facing `IExecutionMemory` / `BoundedExecutionMemory` seam — both live in
    `Execution/`, outside this story's `Memory/`-only scope.
  - **Used-storage eviction:** v1 borrows only the *free* capacity of the other pool; reclaiming
    storage's *used* (cached) blocks down to `StorageRegionBytes` by eviction is later (ties to caching).
  - **Cross-task spill and fair sharing:** v1 spills only the requesting task's own reservations and
    never blocks; Spark's 1/2N..1/N per-task fair share with blocking and cross-task spill is later.
  - **Spill I/O off the lock:** v1 invokes spill callbacks under the coordinating lock (fast,
    non-reentrant); moving blocking disk / object-store spill I/O outside the lock is later.
- **Also later:** wiring `AlignedNativeBuffer` under the off-heap `ColumnVector` implementation (the
  Arrow-backed vector, FEAT-02.2, remains the early baseline), pod-memory accounting/limits for
  executors, and a custom off-heap allocator beyond `NativeMemory` if profiling justifies it.

## References

- [ADR-0013: Memory model for in-memory batches](../../adr/0013-memory-model.md)
- [ADR-0002: In-memory columnar batch format](../../adr/0002-columnar-batch-format.md) ·
  [ADR-0004: Shuffle architecture](../../adr/0004-shuffle-architecture.md)
- [Columnar vector & batch contracts](columnar-contracts.md) ·
  [Testing conventions](testing-conventions.md)
- [EPIC-02: Columnar Memory & Type System](../../planning/epics/EPIC-02-columnar-memory-type-system.md)
- [08 — Performance Checklist](../checklists/08-performance-checklist.md) ·
  [03a — .NET Coding Standards Checklist](../checklists/03a-dotnet-coding-standards.md)
- .NET `System.Runtime.InteropServices.NativeMemory` (aligned alloc/free); `System.Buffers.ArrayPool<T>`;
  Apache Arrow C# 64-byte-aligned allocator; Spark `UnifiedMemoryManager`.
