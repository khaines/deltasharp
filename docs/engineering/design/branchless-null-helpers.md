# Branchless null helper primitives (v1)

> **Status:** living document. Created with
> [STORY-02.6.2](../../planning/epics/EPIC-02-columnar-memory-type-system.md#story-0262-add-branchless-null-helper-primitives)
> (#144). The vectorized/SIMD tier over the scalar null-propagation contracts of
> [STORY-02.6.1](../../planning/epics/EPIC-02-columnar-memory-type-system.md#story-0261-specify-validity-bitmap-and-null-propagation-contracts)
> (#143). Grounded in [ADR-0001](../../adr/0001-execution-strategy.md) (vectorized interpreter is the
> default and the parity oracle; NativeAOT-clean, no dynamic codegen), [ADR-0002](../../adr/0002-columnar-batch-format.md)
> (`ColumnBatch`/`ColumnVector` with Arrow-compatible validity), and the
> [null-validity model](null-validity-model.md) (#143). Update this doc whenever the branchless scheme,
> SIMD strategy, or parity methodology changes.

The [null-validity model](null-validity-model.md) defines the single contract every backend agrees on:
an **Arrow LSB-first validity bitmap** (set bit = valid) and two **null-propagation families** specified
by a scalar `bool?` reference. That reference is correct but per-row and branch-heavy. This story adds the
**branchless, SIMD-accelerated fast path** under that contract: kernels that combine validity and compute
null counts a whole machine word at a time, with **no data-dependent branches in the inner loop**.

Per [ADR-0001](../../adr/0001-execution-strategy.md) the interpreted scalar path remains the **correctness
ground truth**; these vectorized helpers are an additive fast path that must produce **byte-identical**
results. The implementation lives in `src/DeltaSharp.Engine/Columnar/`:
[`BitmapOps`](../../../src/DeltaSharp.Engine/Columnar/BitmapOps.cs) (low-level word primitives),
[`NullMasks`](../../../src/DeltaSharp.Engine/Columnar/NullMasks.cs) (the propagate/Kleene kernels),
[`NullMaskTier`](../../../src/DeltaSharp.Engine/Columnar/NullMaskTier.cs) (the SIMD/scalar tier selector and
its dispatch gate), and
[`NullHelperBenchmark`](../../../src/DeltaSharp.Engine/Columnar/NullHelperBenchmark.cs) (the timing
harness). All are engine-internal (no `DeltaSharp.Core` surface) and exercised through the
friend-assembly test-access policy.

## The branchless mask scheme

A validity bitmap is just a stream of bits. Every operation here is therefore a **bitwise word formula**
applied uniformly to `byte` / `ulong` / `Vector128` / `Vector256` words — there is no per-lane branch,
only mask arithmetic.

### Propagate-on-any-null is a bytewise AND

For arithmetic, comparison, and most scalar functions the output is null wherever **any** input is null,
independent of values (null-validity-model.md). With validity packed LSB-first this is exactly:

```
out_valid = left_valid & right_valid          // binary  (NullMasks.PropagateBinary)
out_valid = in_valid                           // unary   (NullMasks.PropagateUnary)
null_count = length - popcount(out_valid)
```

The absent-bitmap (all-valid) inputs are handled without materializing a synthetic all-ones buffer for
the common case: both operands absent ⇒ the output is filled valid and the null count is `0`; exactly one
absent ⇒ the result equals the present operand's validity. These mirror `NullPropagation`'s
`NeedsValidityBitmap` gating so the no-null path stays cheap.

### Kleene is a bit-packed value+validity formula

`NullPropagation` models a boolean column as `ReadOnlySpan<bool>` values plus a `Validity`. The SIMD-native
form instead packs **two** Arrow LSB-first bitmaps: a **value** bitmap `b` and a **validity** bitmap `v`
(the value bit is meaningful only where the validity bit is set). With

```
tX = vX & bX      // "valid TRUE"  lanes of operand X
fX = vX & ~bX     // "valid FALSE" lanes of operand X
```

the SQL/Spark three-valued truth tables collapse to pure word formulas:

| Op | output value `bO` | output validity `vO` |
| --- | --- | --- |
| `AND` | `tL & tR` | `fL \| fR \| (tL & tR)` |
| `OR`  | `tL \| tR` | `(tL \| tR) \| (fL & fR)` |
| `NOT` | `vX & ~bX` | `vX` |

`AND` is valid (non-null) exactly when some operand is a valid `FALSE` (which rescues the null — `FALSE AND
NULL = FALSE`) or both are valid `TRUE`; `OR` dually. Each `& ~` maps to a single `ANDNOT`/`BIC`
instruction (`Vector128.AndNot`/`Vector256.AndNot`). The four-input → two-output transform is applied
**fused** in one pass: no scratch buffers, no intermediate allocations.

A useful invariant falls out of the tables: whenever an output **value** bit is `1`, its **validity** bit is
also `1` (because `bO ⊆ vO` in every row). So a **null output lane always carries value bit `0`** — exactly
the deterministic `false` placeholder the scalar kernels write into null lanes. That is what makes the value
lanes byte-comparable with the scalar reference.

### Canonical padding (the memcmp guarantee)

The final partial byte of a bitmap holds padding lanes at index `>= length`. Every write here **masks those
padding bits to `0`** (`BitmapOps.TailMask`), matching the canonicalization the scalar bulk kernels already
guarantee (null-validity-model.md, "Output canonicalization"). This makes the parity check an unambiguous
`memcmp` of `ByteCount(length)` bytes, and lets `popcount` ignore padding without masking the inputs.

## SIMD width and AOT-safe fallback

The inner loops use the modern [`System.Runtime.Intrinsics`](https://learn.microsoft.com/dotnet/api/system.runtime.intrinsics)
cross-platform vectors guarded by their `IsHardwareAccelerated` properties, with a scalar `ulong`/`byte`
tail. There is **no `[RequiresDynamicCode]` / IL emit** anywhere — only static SIMD that the JIT and the
NativeAOT ILCompiler lower directly.

| Tier | Width | Guard | x64 | arm64 (this dev box / Apple Silicon) |
| --- | --- | --- | --- | --- |
| `Vector256<byte>` | 32 B | `Vector256.IsHardwareAccelerated` | AVX2 | — (constant-folded away) |
| `Vector128<byte>` | 16 B | `Vector128.IsHardwareAccelerated` | SSE2 | AdvSimd / NEON |
| `ulong` | 8 B | — | always | always |
| `byte` | 1 B | — | tail | tail |
| popcount | per `ulong` | `BitOperations.PopCount` | `POPCNT` | `CNT` |

**Why this is AOT-clean.** `VectorNNN.IsHardwareAccelerated` are `[Intrinsic]` properties that the JIT and
ILCompiler resolve to **compile-time constants per target** — an unsupported tier is **dead-code-eliminated**
rather than throwing `PlatformNotSupportedException`, so no runtime probing or codegen occurs. This is the
exact "intrinsics guarded by `IsSupported` with a scalar fallback" requirement (ADR-0001/ADR-0014):
on arm64 the `Vector256` tier vanishes and `Vector128` (NEON) runs; on a hypothetical SIMD-less target both
vanish and the `ulong`/`byte` fallback runs. `BitOperations.PopCount` is itself the AOT-safe hardware
popcount (it lowers to `POPCNT`/`CNT` with a software fallback). The `dotnet publish … -p:PublishAot=true
-warnaserror` gate over `DeltaSharp.Executor` proves the whole path is IL-clean.

**CI architecture and the tier-forcing test seam.** The development/CI host is **arm64 (Apple Silicon)**.
Its shell runs under Rosetta, so `uname` reports `x86_64`, but the .NET runtime is native arm64
(`RuntimeInformation.ProcessArchitecture == Arm64`): there `Vector256.IsHardwareAccelerated == false` and only
`Vector128` (NEON) is hardware-accelerated. **The `Vector256`/AVX2 tier is reachable under `Auto` only on an
x64 host.** That makes the `Vector256` body *vacuously* covered on this box — a mutation in a constant-folded-away
branch can never fail a test. To close that hole every kernel threads an optional
[`NullMaskTier`](../../../src/DeltaSharp.Engine/Columnar/NullMaskTier.cs) (`Auto` in production;
`Scalar`/`Word`/`Vector128`/`Vector256` for tests), resolved once per call by `NullMaskTierGate`. Because the
kernels are written against the **portable** `Vector256<T>` API (whose software fallback runs on any
architecture), a *forced* `Vector256` loop executes and is parity-checked **even on arm64**. Under `Auto`
the inner-loop codegen and AOT-safety are unchanged — the `IsHardwareAccelerated` sub-expression still folds
to its per-target constant and the AOT publish stays IL-clean. One honest caveat: because `tier` is now a
runtime parameter, the gate reduces to a `tier == Vector256` comparison rather than to a literal `false`, so a
tier unsupported on the host (e.g. `Vector256` on arm64) becomes a **never-taken guarded branch** evaluated
once at loop entry rather than being fully dead-code-eliminated. The cost is one predictable entry branch plus
a marginally larger method body; correctness, parity, the per-element hot loop, and the NativeAOT gate are
unaffected. (A future `Auto`-only entry split from the test-only forcing seam could restore full per-target
DCE — tracked as a perf follow-up.)

**Why there is no virtual dispatch.** The two Kleene connectives share one fused loop parameterized by a
`readonly struct` operator implementing a `static abstract` interface (`IKleeneBinaryOperator`). The JIT/AOT
**monomorphizes** the generic per operator struct and inlines the bitwise formula, so there is no boxing, no
delegate, and no virtual call in the hot loop — the same pattern the BCL's `TensorPrimitives` uses.

**Selectable / fallback (the interpreter stays the reference).** When a `Validity` slice is **bit-unaligned**
(`Offset % 8 != 0`), a bytewise AND would misalign the operands, so `NullMasks` **defers to the scalar
`NullPropagation` reference** for that call — correctness is never traded for speed, and the interpreted path
is always reachable and is the oracle (ADR-0001). Byte-aligned slices (the common fresh-buffer and
block-aligned cases) take the SIMD path. `BitmapOps.IsHardwareAccelerated` exposes which tier is live for
diagnostics and benchmark metadata.

**Kleene entry points are offset-0 / byte-aligned only.** Unlike the propagate kernels (which accept a
`Validity` carrying a bit `Offset`), the bit-packed `KleeneAnd`/`KleeneOr`/`KleeneNot` entry points take
their value and validity **spans starting at bit 0** — there is no offset parameter. A consumer that holds a
**bit-unaligned boolean slice** (e.g. a filtered/sliced boolean column whose logical row 0 is not on a byte
boundary, as the #150/#153 filter and projection operators can produce) **must materialize it into a fresh
offset-0 bitmap first** (a one-pass copy that re-aligns the bits) before calling these kernels. This keeps the
fused word formulas a pure `byte`/`ulong`/vector stream with no per-lane shift, and matches how the operator
layer is expected to stage boolean intermediates.

## Parity with the scalar oracle (AC2)

The scalar `bool?` methods in `NullPropagation` (#143) are the **parity oracle**. The randomized tests assert
the vectorized result equals it **exactly** along four axes simultaneously:

1. **Validity bits** — a byte-for-byte `SequenceEqual` of the output validity bitmap over `ByteCount(length)`
   bytes (made unambiguous by canonical padding).
2. **Value bits** — a byte-for-byte `SequenceEqual` of the output **value** bitmap against the oracle-derived
   canonical value bitmap (and against the packed scalar `bool[]` output). Asserting the value *bitmap*, not
   only the decoded lanes, is what catches a regression that emits **garbage bits in null/padding lanes**:
   dedicated cases seed `v=0, b=1` value bits into null input lanes (and dirty the trailing padding),
   deliberately breaking the canonical `value ⊆ valid` invariant, so the kernels' `& validity` masking and
   tail-canonicalization are *non-vacuously* exercised — dropping either mask (e.g. AND `value = bL & bR`
   instead of `(vL&bL)&(vR&bR)`) leaks the garbage and fails this check.
3. **Null count** — the returned count equals both the scalar bulk kernel's count and an independent
   lane-by-lane count.
4. **Value lanes** — every output lane decoded from the bit-packed value+validity equals the single-lane
   `NullPropagation.Kleene*` oracle *and* the scalar bulk kernel's `bool[]` output.

Coverage (`BitmapOpsTests`, `NullMasksKleeneTests`, `NullMasksPropagateTests`):

- **Null densities** `{0.0, 0.1, 0.5, 0.9, 1.0}` — no-null, sparse, half, dense, all-null.
- **Tail / boundary lengths** `{0,1,7,8,9,…,127,128,255,256,257,1000,1024,4096}` — including **257**, the true
  sub-byte tail (`257 % 8 == 1`), and **1000**, a *vector-width* tail (byte-aligned at `1000 % 8 == 0`, but its
  125 bytes leave a sub-vector remainder below the 16/32-byte stride), every byte boundary, and the 16/32-byte
  vector boundaries so the SIMD body, the `ulong` step, and the `byte` tail are all exercised in one batch.
- **Forced tiers** — each kernel is additionally run with every tier *forced* (`Scalar`/`Word`/`Vector128`/
  `Vector256`) over lengths ≥ 256 bits, so the wide `Vector256` body runs and is mutation-killable **on the
  arm64 CI host** where `Auto` constant-folds it away (see "CI architecture and the tier-forcing test seam").
- **Offsets/slices** — byte-aligned (`0, 8, 16, 24`, same and differing bytes) taking the SIMD path, and
  bit-unaligned (`3, 5, 11`) taking the scalar fallback; both proven byte-identical to the reference.
- **Edge shapes** — empty (`length == 0`), all-null, all-valid, single lane, and the absent-bitmap fast
  paths.

Because the SIMD and scalar code paths produce identical bits by construction (exact integer/bitwise ops),
the same tests also satisfy the epic's "hardware popcount vs scalar fallback return identical counts"
criterion: `BitmapOps.PopCount` is validated against an independent per-bit oracle, and the result is
width-invariant.

## Zero-allocation methodology (AC3)

The hot path allocates **nothing**:

- All kernels write into **caller-owned** `Span<byte>` outputs; none allocate, return collections, use LINQ,
  box, or reflect.
- `Validity` is a `readonly ref struct` (stack-only), and the fused operators pass `out` value tuples that
  stay in registers.
- The off-bitmap fast paths reuse `Span.Fill`/`CopyTo` rather than allocating synthetic buffers.
- The `BitmapOps` word kernels (`And`/`FillValid`/`CopyValidity`/`PopCount`) read and write via unchecked
  `Unsafe`, so each **self-guards** its size precondition with a single allocation-free length comparison
  (`spanLength >= ByteCount(length)`, the same `RequireInput` discipline the Kleene kernels use): an
  undersized span fails fast with a clear `ArgumentException` instead of corrupting memory, and the check is
  off the per-element path so it does not perturb the zero-alloc/perf budget.

This is pinned by tests that warm up, then call the kernel thousands of times inside a
`GC.GetAllocatedBytesForCurrentThread()` window and assert ~0 bytes (≤ 64 B slack), mirroring the #143
no-alloc gate. The benchmark's timed region is held to the same bar over 100k iterations.

## Benchmark methodology (AC4)

`NullHelperBenchmark` is a **lightweight timed harness**, deliberately not BenchmarkDotNet: adding it would
bring a package dependency and a `packages.lock.json` to the otherwise **lock-free** `DeltaSharp.Engine` and
pull in dynamic-codegen machinery that fights the NativeAOT posture (ADR-0001/ADR-0014). Instead each
measurement:

- pre-allocates and **deterministically** fills its bitmaps with a seeded **xorshift64** generator (never
  `System.Random`, which is banned in production for determinism/security), so the measured region is
  allocation-free and reproducible;
- times a tight kernel loop with `Stopwatch.GetTimestamp()` / `GetElapsedTime` (allocation-free, monotonic);
- accumulates a `Checksum` of returned null counts so the loop cannot be dead-code-eliminated.

Every `Result` is **self-describing**: it records the **batch size** and **null density** it was taken at,
plus iterations, elapsed time, derived ns/row, and the active SIMD width. `RunSuite` sweeps the
ADR-0001 vectorized batch band `{1024, 4096, 8192}` × null densities `{0.0, 0.1, 0.5, 0.9}`, yielding one
labeled cell per combination for a `performance-benchmarking-engineer` regression gate to consume.

## Non-goals / follow-ups

- **Selection-vector-combined validity** (gathering validity through a `SelectionVector` so only selected
  physical rows contribute to the output bitmap — epic AC2) is the next layer built **on** these primitives;
  it is deferred to keep this story focused on the dense validity-bitmap fast path. The bit-packed value+
  validity form here is the seam late-materialization and predicate-to-bitmap kernels will feed.
- **Dictionary peeling / memoization** and **predicate-to-bitmap comparison kernels** (which *produce* the
  bit-packed booleans these Kleene kernels consume) are separate FEAT-02.6 / operator stories.
- **Bit-unaligned SIMD** (shifting across byte boundaries to vectorize arbitrary-offset slices) is left as a
  future optimization; today such slices use the correct scalar reference.
- **An AVX-512 `VPOPCNTDQ` / wider tier** is unnecessary while `BitOperations.PopCount` already provides the
  hardware count; it can be added behind the same `IsHardwareAccelerated` guard without changing the contract.

## References

- [ADR-0001: Execution strategy](../../adr/0001-execution-strategy.md) ·
  [ADR-0002: In-memory columnar batch format](../../adr/0002-columnar-batch-format.md)
- [null-validity-model.md](null-validity-model.md) (#143) · [columnar-contracts.md](columnar-contracts.md) ·
  [native-aot.md](native-aot.md) · [testing-conventions.md](testing-conventions.md)
- [EPIC-02: Columnar Memory & Type System](../../planning/epics/EPIC-02-columnar-memory-type-system.md)
- .NET — `System.Runtime.Intrinsics` `Vector128`/`Vector256` (`IsHardwareAccelerated`, `LoadUnsafe`,
  `AndNot`); `System.Numerics.BitOperations.PopCount`; `static abstract` interface members and generic
  monomorphization (`dotnet/runtime` `TensorPrimitives`). Apache Arrow validity bitmaps (LSB-first, optional
  null buffer); Apache Spark Kleene 3VL.
