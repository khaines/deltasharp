# Memory model: aligned off-heap buffer ownership (v1)

> **Status:** living document. Created with
> [STORY-02.3.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-02-columnar-memory-type-system.md#story-0231-implement-aligned-off-heap-allocation-ownership).
> Grounded in [ADR-0013](../../adr/0013-memory-model.md) (off-heap `NativeMemory`, 64-byte
> aligned, for in-memory batches) and [ADR-0002](../../adr/0002-columnar-batch-format.md) (the
> `ColumnBatch`/`ColumnVector` buffers this memory backs). Update it whenever the buffer-ownership
> contract or its accounting changes.

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
contrast, is always freed and flagged. A debug-only scratch-leak tracker can be added when the
unified manager (STORY-02.3.2 / #138) lands.

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

## v1 scope and deferrals

- **In scope:** the `OwnedBuffer` ownership contract; the aligned off-heap default
  (`AlignedNativeBuffer`); the small-scratch GC-heap path (`GcHeapBuffer`); the size-routing,
  separately-accounted `NativeMemoryAllocator`; exactly-once disposal with a finalizer safety net and
  leak counters; and the `BufferGroup` exception-safety pattern.
- **Deferred — off-heap `ColumnVector` lifetime:** `OwnedBuffer` is single-owner/exactly-once, but
  ADR-0002 `ColumnVector.Slice`/`WithSelection` are **zero-copy views that share buffers**. A future
  off-heap vector therefore needs an explicit lifetime model (ref-count, root-keepalive on each slice,
  or batch-arena dispose) — the disposal analogue of #133's seal-on-slice — and SIMD over-read on
  shared tails is already handled by the 64-byte capacity padding. This is the key #138 design risk.
- **Deferred to [STORY-02.3.2](https://github.com/khaines/deltasharp/issues/138) (#138):** the
  **unified execution/storage memory manager** — execution vs. storage pools, **per-task budgets**,
  and **spill-to-disk / object-store under pressure** (Spark's `UnifiedMemoryManager`; ties to
  [ADR-0004](../../adr/0004-shuffle-architecture.md)). This story deliberately provides only the
  deterministic, aligned **ownership** primitive those pools and spill triggers will be built on; the
  allocator is intentionally per-instance (no global pool) so a future manager can own budget and
  pressure policy without reworking ownership.
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
