# Scalar-vs-SIMD kernel parity suite (STORY-03.5.1)

> **Status:** living document. Created with
> [STORY-03.5.1](../../planning/epics/EPIC-03-vectorized-execution-backend.md) (#153 — add a
> scalar-vs-SIMD kernel parity suite). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md): the vectorized **scalar reference is the correctness
> ground truth** and a SIMD fast path is "an additive optimization, never a correctness dependency" that
> must match it. Builds directly on the three columnar kernel families that ship a forced-tier seam — the
> [aggregate/comparison kernels](aggregate-comparison-kernels.md) (#149), the
> [selection kernels](selection-kernels.md) (#150), and the
> [branchless null helpers](branchless-null-helpers.md) (#144) — and is the cross-family analogue of the
> [interpreter-vs-compiled backend parity suite](backend-parity-suite.md) (#154). Update it whenever a
> kernel family gains/loses a SIMD tier, the generator grammar changes, the float policy changes, or the
> AC4 diagnostic format changes.
>
> **Owner:** reliability-test-chaos-engineer. **Suite:**
> `tests/DeltaSharp.Engine.Tests/Columnar/Parity/`.

## 1. Why this suite exists (the ADR-0001 obligation)

The columnar engine ships hand-written SIMD fast paths (32-byte `Vector256`, 16-byte `Vector128`, 8-byte
`ulong`) that run *whenever the hardware accelerates them*. ADR-0001 makes the **scalar tier** the
correctness oracle and every wider tier "an additive optimization that must produce the identical result."
A hardware fast path that changes a query result is a correctness defect, not a performance regression.

This suite is the mechanically-checkable oracle for that obligation across **all** kernel families at once:

> **Invariant.** For every generated input, and for every kernel family, the result computed at the forced
> `Scalar` tier is **bit-identical** to the result computed at each SIMD tier (`Vector128`, `Vector256`,
> the `ulong` word tier where present) and to `Auto`. Tolerance is **0 ULP** — see §6.

### How it complements the per-kernel forced-tier tests (no duplication)

Each kernel PR already ships *curated* forced-tier parity tests with hand-picked boundary lengths
(`AggregateKernelsTests`, `ComparisonKernelsTests`, `SelectionKernelsTests`, `NullMasksKleeneTests`,
`BitmapOpsTests`, `KernelTierTests`). This suite does **not** re-enumerate those; its value-add is a
**single deterministic generator** that synthesizes *one* batch varying type / null density / selection
density / offset / batch size and then drives **every family together** off that one seed, with the AC4
rich-diagnostics contract (seed / schema / kernel / tier / minimal repro). It is the cross-family,
generator-driven analogue of what #154 did for interpreter-vs-compiled expression evaluation.

## 2. Methodology: a differential oracle with two independent sides

For each generated case and each tier-seamed op, the harness
(`KernelParityHarness`) makes two distinct comparisons:

1. **scalar reference vs an independent in-test oracle.** The reference is the kernel run at the forced
   `Scalar`/`NullMaskTier.Scalar` tier — the per-element tail loop only, no vector body. The oracle is
   re-implemented from scratch in the test (plain loops / 3VL truth tables) — deliberately **not** reusing
   `KernelScalars` or `NullPropagation` — so a co-mutation of a production helper cannot make the assertion
   vacuously pass. This catches a bug shared by *all* tiers (a scalar-reference or whole-kernel drift).
2. **every SIMD tier vs the scalar reference.** This is the AC1 contract proper: a hardware-fast-path-only
   divergence. The reference here is the kernel's own scalar tail, so this is a pure tier-vs-tier identity
   check independent of the oracle.

Both sides must hold for every generated case.

## 3. The forced-tier seam and why every tier is reachable on the arm64 CI host

The dev/CI box is **arm64/NEON**, where `Vector256.IsHardwareAccelerated == false`. Under the production
`KernelTier.Auto` dispatch the 32-byte body is therefore constant-folded away and would be **vacuously
green** — its mutations untested. The kernels solve this with a forced-tier seam reused verbatim here:

| Seam | File | Tiers it can force |
| --- | --- | --- |
| `KernelTier` / `KernelTierGate` | [`KernelTier.cs`](../../../src/DeltaSharp.Engine/Columnar/KernelTier.cs) | `Scalar`, `Vector128`, `Vector256`, `Auto` |
| `NullMaskTier` / `NullMaskTierGate` | [`NullMaskTier.cs`](../../../src/DeltaSharp.Engine/Columnar/NullMaskTier.cs) | `Scalar`, `Word` (`ulong`), `Vector128`, `Vector256`, `Auto` |

The kernels are written against the **portable `System.Runtime.Intrinsics` vector API**, whose software
fallback runs on every architecture. `KernelTierGate.UseVector256(tier)` returns `true` for a forced
`Vector256` regardless of `IsHardwareAccelerated`, so the 32-byte body executes as ordinary reachable code
via the portable fallback — making it parity-checked and mutation-killable even on arm64. Under `Auto` the
`IsHardwareAccelerated` sub-expression still folds to the per-target constant, so production codegen and
NativeAOT dead-code elimination are unchanged (the forcing values are test-only). A forced vector tier runs
exactly one vector loop and lets the narrower tail loops drain the remainder, so the output is identical to
`Auto` and to the scalar reference regardless of the tier chosen.

**AC3 (unsupported-hardware / scalar-only configuration).** Because the harness *always* runs the forced
`Scalar` tier for every family on every seed, scalar-fallback coverage validates every kernel family on any
host — exactly the "unsupported hardware in CI simulation OR scalar-only configuration" path. The forced
`Vector128`/`Vector256`/`Word` tiers additionally guarantee each SIMD tier is reachable even where the host
does not accelerate it.

## 4. Coverage matrix: families × tiers × dimensions

| Family | Op(s) under tier seam | Element type(s) | SIMD tiers compared to Scalar | Output shape checked |
| --- | --- | --- | --- | --- |
| Aggregate | `SumInt32`, `MinInt32`, `MaxInt32` | `int` (incl. `date` layout) | `Vector128`, `Vector256`, `Auto` | scalar reduction (`long`) |
| Aggregate | `MinInt64`, `MaxInt64` | `long` (incl. `timestamp` layout) | `Vector128`, `Vector256`, `Auto` | scalar reduction (`long`) |
| Comparison | `CompareInt32` (vector·vector, vector·scalar) | `int` | `Vector128`, `Vector256`, `Auto` | packed result bitmap |
| Comparison | `CompareInt64` (vector·vector, vector·scalar) | `long` | `Vector128`, `Vector256`, `Auto` | packed result bitmap |
| Selection | `ToSelection` (bitmap→indices, varied **offset**) | bitmap | `Vector128`, `Vector256`, `Auto` | selection index list |
| Selection | `Compose` (selection ∘ predicate) | bitmap + selection | `Vector128`, `Vector256`, `Auto` | selection index list |
| Null mask | `BitmapOps.And` | validity bitmap | `Word`, `Vector128`, `Vector256`, `Auto` | bitmap + null count |
| Null mask | `KleeneAnd`, `KleeneOr`, `KleeneNot` | bit-packed 3VL boolean | `Word`, `Vector128`, `Vector256`, `Auto` | value + validity bitmaps + null count |
| Null mask | `PropagateBinary` (validity AND path) | `Validity` (offset 0) | `Word`, `Vector128`, `Vector256`, `Auto` | bitmap + null count |

The six comparison predicates (`=`, `<>`, `<`, `<=`, `>`, `>=`) are each run for both the vector·vector and
vector·scalar overloads at every tier, so a single seed produces 24 comparison parity checks.

### The five generated dimensions (AC1)

The generator (`KernelParityGenerator`) draws all five AC1 dimensions from one seed:

- **type** — covered by running int32, int64, 3VL-boolean, and packed-validity families on the *same* case.
- **batch size** `n` — half the cases pick a boundary length (`0,1,…,9,15,16,17,…,63,64,65,…,4096`, i.e.
  sub-byte tails, exact byte/vector-width multiples ±1) and half pick a uniform random length in `[0,4096]`,
  so a forced tier's vector body, its narrower tails, and the scalar remainder are all exercised.
- **null density** — fraction of `NULL`/`UNKNOWN` lanes; drives the validity bitmaps and the 3VL lanes.
- **selection density** — fraction of set predicate/selection bits; drives `ToSelection`/`Compose`/`And`.
- **offset** — a bit offset in `[0,24)` into the predicate window, including **non-byte-aligned** values,
  which is the defining stressor of `ToSelection`'s aligned-lead-in + whole-byte-skip path.

## 5. The deterministic generator (seeded, reproducible)

Reproducibility is reduced to a **seed**. `KernelParityRng` is SplitMix64 (a fixed 64-bit integer
recurrence), **not** `System.Random` (whose sequence is not contractually stable across .NET versions). The
same seed therefore yields byte-identical draws on every runtime and CI run; `Generator_IsDeterministic_ForFixedSeed`
asserts `Generate(seed)` twice produces equal arrays. The seed corpus is fixed (a golden-ratio-stepped
function of the index `i ∈ [1,256]`), pinned in `KernelParityTests.Seeds()`, so the suite is a deterministic
cross-family sweep and every case is replayable from its seed alone.

## 6. Float NaN / ±0 / ∞ tolerance and policy

**Tolerance is exactly bit-identical (0 ULP) across the entire columnar kernel surface, because there is no
floating-point SIMD reassociation anywhere in it.** The only floating aggregates and comparisons are
deliberately **scalar-only**:

- `AggregateKernels.SumDouble`, `MinDouble`, `MaxDouble`, `AverageDouble`, and the decimal aggregates take
  **no `KernelTier` parameter** — they have a single per-element loop and no vector body, so there is no
  SIMD tier to diverge. Float `SUM` is scalar-only specifically so its left-to-right accumulation is
  deterministic (a SIMD lane-stripe would reassociate and change the rounding).
- `ComparisonKernels.Compare` takes the SIMD fast path only for contiguous, no-null, same-typed `int32`
  (`int`/`date`) or `int64` (`long`/`timestamp`) operands; **float, double, and decimal always use the
  scalar reference** (`KernelScalars.CompareDouble`/`CompareDecimal`).

So float parity is **trivial by construction** — no hardware fast path exists for it to diverge — and no
epsilon is needed or permitted. What still must be validated is that the scalar float path implements
**Spark's total order**, which `KernelParityFloatPolicyTests` checks against an independent oracle over a
corpus saturated with the special values:

- `NaN` equals `NaN` and sorts **greatest** (`SQLOrderingUtil`); `MAX` over any-`NaN` input is `NaN`, `MIN`
  ignores `NaN` unless every value is `NaN`.
- `-0.0 == +0.0` (IEEE equality; Spark `NormalizeFloatingNumbers`).
- `±∞` order normally and propagate through `SUM` per IEEE.
- comparison results are exact booleans (bit-exact, no tolerance).

## 7. Ops with no SIMD tier (parity-trivial by construction)

The suite must not claim coverage where no fast path exists. These ops are scalar-only / single-path and so
are parity-trivial; they are documented here rather than asserted across tiers:

| Op | File | Why no SIMD tier |
| --- | --- | --- |
| `SumDouble` / `MinDouble` / `MaxDouble` / `AverageDouble` | `AggregateKernels.cs` | float reductions are deterministic scalar-only (no reassociation) — see §6 |
| `SumDecimal` / `MinDecimal` / `MaxDecimal` | `AggregateKernels.cs` | exact `Int128` arithmetic, no vector form |
| `ComparisonKernels.Compare` (float/double/decimal/temporal-promote) | `ComparisonKernels.cs` | total-order/cross-scale semantics use the scalar reference |
| `PropagateUnary` | `NullMasks.cs` | a validity copy/fill; ignores its `tier` argument (`_ = tier;`) |
| `PopCount` / `CountNulls` | `BitmapOps.cs` | already word-parallel via `BitOperations.PopCount`; no tier knob |
| `GroupSumInt64` / `GroupCountNonNull` | `AggregateKernels.cs` | a scatter by group id (no SIMD) |

`PropagateBinary` over a **bit-unaligned** `Validity` offset also deterministically defers to the scalar
reference for every tier (so it is parity-trivial); the suite exercises the offset-0, byte-aligned path that
takes the `BitmapOps.And` SIMD hot loop.

## 8. AC4 diagnostics format

On any mismatch the harness throws an `XunitException` whose message carries the full replay contract:

```
Scalar-vs-SIMD kernel parity mismatch — the forced-Scalar path is the ADR-0001 oracle; every SIMD tier must be bit-identical.
  summary        : <kernel> diverged at tier <tier>
  seed           : 0x<16-hex>                      # replay key
  family         : <Aggregate|Comparison|Selection|NullMask>
  kernel         : <e.g. AggregateKernels.SumInt32>
  operands       : <e.g. int[1000], or "op=LessThan, int[37] vs int[37]">
  schema / dims  : batchSize=<n> nullDensity=<d> selectionDensity=<d> offset=<o>
  hardware path  : tier=<tier> vs forced-Scalar reference (host: Vector128.IsHardwareAccelerated=…, Vector256.IsHardwareAccelerated=…; a forced vector tier runs the portable software fallback so it is reachable on any host)
  first diverge  : <index/row/lane/bit and the two divergent values>
  minimal repro  : <kernel>(<minimized input prefix>, tier=<tier>) => <simd>, expected (Scalar) <scalar>
  replay         : KernelParityGenerator.Generate(0x<16-hex>) then re-run this family.
```

The **minimal repro** is genuinely minimized: `MinimalPrefix` finds the smallest input prefix length for
which the two tiers still disagree (a deterministic upward scan), then prints that prefix — so the printed
inputs are the smallest case that reproduces the divergence, not the whole batch.
`Diagnostics_CarryEveryAc4Field` asserts the rendered message contains every required field.

## 9. Non-vacuity and the injected-divergence proof

Two layers prove the suite is not vacuous:

1. **In-suite (always runs):** the independent oracle (§2) and the `Diagnostics_CarryEveryAc4Field`
   self-test (the failure path renders and throws with all AC4 fields).
2. **Out-of-tree mutation proof (STORY-03.5.1 §4):** in a scratch clone (`git archive HEAD | tar -x`,
   *outside* the worktree, full rebuild), a deliberate SIMD-tier divergence is injected into a kernel (e.g.
   perturbing a `Vector256` reduction in `AggregateKernels`, a `SelectionKernels` emit, or a `NullMasks`
   Kleene SIMD path) and the suite is confirmed to **fail with the full AC4 diagnostics** (seed / schema /
   kernel / tier / first divergence / minimal repro). The scratch tree is then discarded. This is the
   mutation test that proves a real hardware-fast-path regression cannot pass silently.

## 10. AC coverage

| Acceptance criterion | Where satisfied |
| --- | --- |
| **AC1** — generated batches varying type/null/selection/offset/batch size; scalar == SIMD for every case | `KernelParityGenerator` (§4–5) + `KernelParityTests.AllFamilies_ScalarEqualsEverySimdTier` (256 seeds × all families) |
| **AC2** — float NaN/∞/−0/normals use documented tolerance + NaN policy | `KernelParityFloatPolicyTests` + §6 (0-ULP bit-exact; Spark total order; float SUM/MIN/MAX scalar-only) |
| **AC3** — unsupported hardware / scalar-only validates every family; every tier reachable | forced `Scalar` always run per family + `ForcedTierSeam_ReachesEveryTier_OnAnyHost` + §3 |
| **AC4** — failure prints seed / schema / kernel+args / hardware path / minimal repro | `KernelParityHarness.BuildDiagnostic` + `Diagnostics_CarryEveryAc4Field` + §8–9 |
| Determinism | SplitMix64 `KernelParityRng` + `Generator_IsDeterministic_ForFixedSeed` (§5) |
