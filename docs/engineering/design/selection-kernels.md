# Selection kernels — bitmap→selection and selection-aware composition

> **Status:** living document. Created with
> [STORY-03.3.2](../../planning/epics/EPIC-03-vectorized-execution-backend.md#story-0332-implement-bitmap-to-selection-and-selection-aware-kernels)
> (#150). Reusable scalar+SIMD kernels that turn a packed predicate bitmap into a
> [`SelectionVector`](../../../src/DeltaSharp.Engine/Columnar/SelectionVector.cs) and compose an existing selection
> with a new predicate, so filters and joins push selection down **without materializing rows**. Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (the vectorized interpreter is the default **and** the parity
> oracle; NativeAOT-clean, no dynamic codegen) and [ADR-0002](../../adr/0002-columnar-batch-format.md)
> (`ColumnBatch`/`ColumnVector` is mutable and **selection-vector-aware**, with Arrow-compatible LSB-first
> validity). Reuses the [branchless null helpers](branchless-null-helpers.md) (#144) tier seam and the
> [aggregate/comparison kernels](aggregate-comparison-kernels.md) (#149) `KernelTier` gate. Update this doc whenever
> an algorithm, the SIMD strategy, the consumption contract, or the parity methodology changes.

The kernels live in `src/DeltaSharp.Engine/Columnar/`, are engine-internal (no `DeltaSharp.Core` surface), and are
exercised through the friend-assembly test-access policy.

| File | Responsibility |
| --- | --- |
| [`SelectionKernels`](../../../src/DeltaSharp.Engine/Columnar/SelectionKernels.cs) | `ToSelection` (bitmap→selection) and `Compose` (selection∘predicate), scalar reference + tier-forced SIMD zero-skip. |
| [`SelectionVector`](../../../src/DeltaSharp.Engine/Columnar/SelectionVector.cs) | The ordered physical-row index set the kernels produce/consume (ADR-0002 late materialization; pre-existing, unchanged). |
| [`KernelTier`](../../../src/DeltaSharp.Engine/Columnar/KernelTier.cs) | The SIMD/scalar tier selector + `KernelTierGate` dispatch (reused from #149). |
| [`Bitmap`](../../../src/DeltaSharp.Engine/Columnar/Bitmap.cs) | The scalar LSB-first bit reader (`Bitmap.Get`) used by the reference loops. |
| [`SelectionBenchmark`](../../../src/DeltaSharp.Engine/Columnar/SelectionBenchmark.cs) | The lock-free, allocation-free timing harness recording batch size / selectivity / throughput. |

## Where this sits

A vectorized filter evaluates a predicate over a `ColumnVector` into a **packed result bitmap** plus a **validity
bitmap** (the [comparison kernels](aggregate-comparison-kernels.md), #149). A row survives `WHERE` only when the
predicate is `TRUE` — i.e. valid **and** set — so the operator first folds validity in with
[`BitmapOps.And`](../../../src/DeltaSharp.Engine/Columnar/BitmapOps.cs) (#144; a null row clears its bit, matching
Spark's "drop rows that are not `TRUE`"), then hands the combined **pass bitmap** to `ToSelection`. The resulting
`SelectionVector` lets downstream operators read only surviving rows through `ColumnBatch.WithSelection` /
`ColumnVector.Select` (ADR-0002) with **no value buffer copied**. A second predicate on the already-filtered batch
is then folded in with `Compose` — the join/filter-pushdown composition primitive. These two kernels are the
reusable selection layer the story calls for; combining validity, and the operator wiring itself, stay with their
existing owners (#144, #155).

## AC1 — bitmap → selection

`ToSelection(ReadOnlySpan<byte> predicate, int offset, int length, Span<int> dest, KernelTier tier)` scans the
logical window `[offset, offset + length)` of an Arrow LSB-first bitmap (bit `j` lives in byte `j/8` at position
`j%8`; a **set** bit = the row passes) and writes the logical index `i ∈ [0, length)` of every set bit into `dest`,
returning the count written. Logical index `i` reads source bit `offset + i`.

### Ordered / unique / in-bounds — by construction

The scan visits each logical index `i` in strictly increasing order `0, 1, …, length-1` and emits `i` **iff** its
bit is set. Therefore:

- **Ordered** — indices are appended in increasing `i`, so `dest[0..count)` is strictly ascending.
- **Unique** — each `i` is visited exactly once, so no index can repeat.
- **In-bounds** — only `i < length` is ever emitted, so every index is in `[0, length)` (i.e. within the input row
  count).

These are *structural* guarantees, not run-time checks: the loop bound `i < length` and the single emission site are
the proof. The tests assert all three independently (`AssertOrderedUniqueInBounds`).

### Arbitrary offsets and tails

`offset` may be any non-negative value, **including non-byte-aligned**, and `length` any non-negative tail:

- A **scalar lead-in** walks `i` from `0` until `(offset + i) & 7 == 0`, i.e. until the source bit reaches a byte
  boundary (at most 7 bits). These are emitted per-bit via `Bitmap.Get`.
- The **whole-byte body** then processes `fullBytes = (length - i) >> 3` source bytes, each covering 8 logical
  positions that are all `< length`.
- A **scalar tail** drains the final `(length - i) & 7` positions per-bit.

Because the body covers only bytes wholly inside `[0, length)`, **padding lanes at index `≥ offset + length` are
never inspected** — a junk bit in the final partial source byte (or before `offset`) can never leak into the output.
The tests seed every non-window bit with random junk (`BuildBitmap`) to enforce this. `length == 0` returns `0`
without touching memory.

### Buffer contract

`dest` must hold at least `length` ints (the worst case, all-pass). `predicate` must cover at least
`ByteCount(offset + length)` bytes. Both are validated once, off the per-element path (`RequireSpan`, mirroring
`BitmapOps`), so an undersized span throws `ArgumentException` instead of reading/writing out of bounds under the
`Unsafe` loads. The cold-path `SelectionVector ToSelection(predicate, offset, length, tier)` overload allocates the
result for callers that want an object rather than filling a scratch span.

## AC2 — selection ∘ predicate composition

`Compose(ReadOnlySpan<int> selection, ReadOnlySpan<byte> predicate, Span<int> dest, KernelTier tier)` takes an
existing selection (`selection[p]` = the physical/original-row-space index at selection **position** `p`) and a
predicate bitmap whose **bit `p` answers "did selection position `p` pass?"**. It emits `selection[p]` for every set
`p`, in selection order, returning the count written.

### Semantics: predicate applies over the already-selected rows; indexes stay original-row-space

The predicate is indexed by **selection position** `p ∈ [0, selection.Count)`, *not* by physical row — so it is
exactly "a predicate evaluated over the already-selected rows" (the survivors of the first predicate, re-packed to
positions `0..count-1`, which is the shape a comparison kernel produces when run on the selected view). The emitted
values are `selection[p]`, which are the **original physical row indices**, so the composed selection is still in
original-row-space and can be applied directly to the underlying (uncompacted) `ColumnVector`.

Composing thus equals **applying both predicates in order**: a physical row survives iff it passed predicate 1
(so it appears in `selection`) **and** predicate 2 at its rank among the predicate-1 survivors. The test
`Compose_EqualsApplyingBothPredicatesInOrder` proves this against a fully independent two-pass oracle, and
`Compose_AppliesPredicateOverAlreadySelectedRows_InOriginalRowSpace` pins the original-row-space mapping.

Because `Compose` only **drops** positions (never reorders or duplicates), it **preserves the base selection's order
and uniqueness**: an ordered/unique input yields an ordered/unique output. The predicate is offset-0 by definition
(positions start at 0), so no lead-in is needed; `dest` must hold at least `selection.Count` ints and `predicate` at
least `ByteCount(selection.Count)` bytes. `SelectionVector Compose(SelectionVector, predicate, tier)` is the cold,
allocating convenience overload.

> This kernel `Compose` (selection × **predicate bitmap**) is distinct from the pre-existing
> `SelectionVector.Compose` (selection × **selection**, STORY-02.1.2). Both express layered selection; this one is
> the bitmap-driven filter/join pushdown primitive and leaves `SelectionVector` untouched.

## AC3 — scalar reference and SIMD tiers are bit-identical

### Scalar reference (the in-code oracle)

Under `KernelTier.Scalar` both kernels degrade to a **pure per-bit loop**: `ToSelection` runs only the final
`for (; i < length; i++)` over `[0, length)`, and `Compose` only its `for (; p < count; p++)` tail. This is the
simplest correct implementation and serves as the in-code reference (ADR-0001: the interpreter/scalar path is the
oracle). The parity tests additionally check against a **separate** per-bit oracle written in the test file
(`ScalarOracle` / `ComposeOracle`), never the kernel — so a co-mutation of the reference still fails.

### SIMD tiers: a zero-region skip, not a different result

The `Vector128`/`Vector256` tiers are an **additive accelerator**: they load a 16-/32-byte chunk of the bitmap and,
when `chunk == Vector{128,256}<byte>.Zero` (all bits clear), skip 128/256 logical indices with a single compare —
emitting nothing, exactly as the scalar path would for those zero bits. A non-zero chunk falls back to the
**identical per-byte bit-scan** (`BitOperations.TrailingZeroCount`, LSB→ascending, clearing the lowest set bit each
step). The emitted index sequence is therefore **independent of which zero regions a tier skips wholesale**, hence
**bit-identical across Scalar, Vector128, Vector256, and Auto**. A forced vector tier runs exactly one vector loop
and lets the per-byte remainder + scalar tail drain the rest, so the result is always identical to the scalar
reference regardless of tier (the same property #144/#149 rely on).

This design is what makes parity *provable* rather than coincidental: the fast path cannot compute a different
answer, only reach the same answer faster on sparse data.

### The forced-tier test seam (host-independent)

Tier dispatch reuses [`KernelTierGate`](../../../src/DeltaSharp.Engine/Columnar/KernelTier.cs) (#149). The vector
loops are written against the **portable `System.Runtime.Intrinsics` API**, whose software fallback runs on every
architecture. Under `KernelTier.Auto` each tier is gated by its `IsHardwareAccelerated` guard, which the JIT and the
NativeAOT ILCompiler fold to a compile-time constant per target — so an unsupported tier is **dead-code-eliminated**
(ADR-0014) and production codegen is unchanged. A **forced** `KernelTier.Vector256` turns the 256-bit body into
ordinary reachable code even on the **arm64 CI host where `Vector256.IsHardwareAccelerated == false`**, so the tests
execute and mutation-kill it host-independently (proven below). The gate is evaluated once per call at loop entry,
never per element, so the hot path is unaffected.

## AC4 — zero-alloc hot path + throughput

### Zero-alloc design

The `Span<int>` overloads allocate, box, use LINQ, or dispatch virtually **nowhere**: they fill a caller-owned
`dest` span, keep counters and refs in locals, and read the bitmap through `Unsafe`/`MemoryMarshal` refs. An operator
sizes **one** `int[length]` scratch per batch and reuses it across batches. (The `SelectionVector`-returning
overloads are the explicit cold path and do allocate the result array — they are not on the hot loop.)

### Benchmark + regression-gate methodology

[`SelectionBenchmark`](../../../src/DeltaSharp.Engine/Columnar/SelectionBenchmark.cs) is a lock-free,
allocation-free timing harness — deliberately **not** BenchmarkDotNet, which would add a package + lock file to the
lock-free engine and pull in dynamic-codegen that fights NativeAOT (ADR-0001/ADR-0014), matching the
[#144](../../../src/DeltaSharp.Engine/Columnar/NullHelperBenchmark.cs)/[#149](../../../src/DeltaSharp.Engine/Columnar/KernelBenchmark.cs)
harnesses. Each `Measure` builds its predicate (seeded xorshift, never `System.Random`) and reusable `int[]`
scratch up front, warms up, then times a tight loop with `Stopwatch.GetTimestamp`. Every self-describing `Result`
records the **batch size**, the **selectivity**, `NanosecondsPerRow` (the throughput metric a regression gate
compares), and the active SIMD width. `RunSuite` sweeps the dimensions below; a `Checksum` accumulates selected
counts so the loop can't be dead-code-eliminated.

### Benchmark dimensions

| Dimension | Values | Why |
| --- | --- | --- |
| Batch size | 1024, 4096, 8192 | ADR-0001's ~1k–8k vectorized band; amortization of fixed cost. |
| Selectivity | 0.0, 0.1, 0.5, 0.9, 1.0 | All-fail (max zero-skip), sparse, half, dense, all-pass (no skip) — the predicate density the SIMD skip is sensitive to. |
| Operation | `ToSelection`, `Compose` | Both selection kernels. |
| Hardware width | host SIMD width (recorded) | `Auto` on the CI arm64 host is `Vector128`; `VectorByteWidth` is recorded per run. |

The allocation guard (`Measure_TimedRegion_IsAllocationFree`) uses the **poll-to-steady-state** loop the rest of the
suite uses: it re-measures (up to 50 attempts, `Thread.Sleep(5)` between) until a tier-1 pass sees only the small
fixed setup allocation, tolerating the one-time background-JIT tier-up transient that a single 100k-iteration
measurement would otherwise flake on under parallel xUnit load (the exact flake fixed in
`NullHelperBenchmarkTests`).

## AC-coverage table

| AC | Requirement | Implementation | Tests |
| --- | --- | --- | --- |
| 1 | Bitmap→selection over arbitrary offsets/tails → ordered, unique, in-`[0,length)` indices | `SelectionKernels.ToSelection` (scalar lead-in + whole-byte body + scalar tail) | `ToSelection_MatchesPerBitOracle_AcrossOffsetsAndTiers`, `ToSelection_EdgePredicates_AllTiersIdentical`, `ToSelection_NonByteAlignedOffset_SkipsLeadInBits`, `ToSelection_ReturnsSelectionVector_OrderedUniqueInBounds` (`AssertOrderedUniqueInBounds`) |
| 2 | Selection + new predicate → selection of applying both in order, original-row-space | `SelectionKernels.Compose` (gather `selection[p]` for set `p`) | `Compose_MatchesIndependentOracle_AcrossTiers`, `Compose_AppliesPredicateOverAlreadySelectedRows_InOriginalRowSpace`, `Compose_EqualsApplyingBothPredicatesInOrder`, `Compose_EmptySelection_YieldsEmpty` |
| 3 | Scalar/SIMD parity over empty/all-pass/all-fail/sparse → identical counts + indexes | Tier zero-skip is bit-identical to the per-byte scan; forced-tier seam (`KernelTierGate`) | `*_AcrossTiers` theories over `ForcedTiers` × lengths `{7,8,9,15,16,17,31,32,33,63,64,65,127,128,255,256,257,…}`; `ToSelection_EdgePredicates_AllTiersIdentical` |
| 4 | Zero-alloc hot path + throughput across selectivity/batch size for regression gating | `Span<int>` overloads allocate nothing; `SelectionBenchmark` records ns/row | `Measure_TimedRegion_IsAllocationFree` (poll-to-steady-state), `Measure_RecordsBatchSizeAndSelectivityMetadata_ForEveryOperation`, `RunSuite_CoversTheBatchSizeAndSelectivityGrid`, `Measure_Checksum_ReflectsActualWork` |

## Non-vacuity (mutation evidence)

Forcing the `Vector256` tier makes its portable-software-fallback body reachable on this arm64 box. Two mutations
were applied and confirmed to **fail** a test, then reverted:

- **Tier mutation** — changing the `Vector256` emit base from `i + ((b+k) << 3)` to `<< 2` (wrong index scale) fails
  `ToSelection_MatchesPerBitOracle_AcrossOffsetsAndTiers` (10 failures). Proof the forced 256-bit body actually runs
  and is checked, not vacuously green under `Auto`.
- **Composition mutation** — emitting the position `p` instead of `selection[p]` in `Compose` fails
  `Compose_AppliesPredicateOverAlreadySelectedRows_InOriginalRowSpace` (and 21 others). Proof the original-row-space
  gather is verified.

## Deferred scope

- **Operator wiring** — the filter/join operators that *call* these kernels (combining validity, choosing when to
  compact vs. carry the selection) are EPIC-03 operator work (#155); this story owns the reusable primitives only.
- **Selection→bitmap (inverse)** and **selection compaction/dedup** are not needed by #150 and are deferred until an
  operator requires them.
- **A dedicated SIMD index-compress** (e.g. a shuffle-table `compress` that emits indices without the per-byte
  scan) is a possible future fast path for *dense* predicates; the current zero-skip already wins on the common
  sparse/medium case and stays trivially parity-provable. Tracked as a perf follow-up, not a correctness gap.
- **`SelectionVector` adopt-with-count** to let the convenience overloads avoid the second copy is a minor cold-path
  optimization deferred to avoid touching the `SelectionVector` surface.

## References

- [ADR-0001: Execution strategy](../../adr/0001-execution-strategy.md) ·
  [ADR-0002: In-memory columnar batch format](../../adr/0002-columnar-batch-format.md) ·
  [ADR-0014: Target framework & AOT](../../adr/0014-target-framework-aot.md)
- [aggregate-comparison-kernels.md](aggregate-comparison-kernels.md) (#149, the `KernelTier` seam reused here) ·
  [branchless-null-helpers.md](branchless-null-helpers.md) (#144, the validity `And`/popcount + `NullMaskTier`
  seam) · [null-validity-model.md](null-validity-model.md) (#143) ·
  [columnar-contracts.md](columnar-contracts.md) (the `SelectionVector` / late-materialization contract) ·
  [testing-conventions.md](testing-conventions.md)
- [EPIC-03: Vectorized execution backend](../../planning/epics/EPIC-03-vectorized-execution-backend.md)
- .NET — `System.Runtime.Intrinsics` `Vector128`/`Vector256` (`IsHardwareAccelerated`, `LoadUnsafe`, `==` all-equal),
  `System.Numerics.BitOperations.TrailingZeroCount`; Apache Arrow validity bitmaps (LSB-first); Apache Spark `WHERE`
  null semantics (a row survives only when the predicate is `TRUE`).

---

*This document matches the code exactly as of the STORY-03.3.2 (#150) implementation commit.*
