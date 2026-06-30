# Aggregate and comparison kernels (v1)

> **Status:** living document. Created with
> [STORY-03.3.1](../../planning/epics/EPIC-03-vectorized-execution-backend.md#story-0331-implement-aggregate-and-comparison-kernels)
> (#149). Scalar and SIMD kernels for the v1 aggregates (`SUM`, `MIN`, `MAX`, `COUNT`, `AVG`) and the six SQL
> comparison predicates over a [`ColumnVector`](../../../src/DeltaSharp.Engine/Columnar/ColumnVector.cs), so the
> aggregate/filter operators (#148) reuse one verified hot-loop primitive. Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (the vectorized interpreter is the default **and** the parity
> oracle; NativeAOT-clean, no dynamic codegen), [ADR-0002](../../adr/0002-columnar-batch-format.md)
> (`ColumnBatch`/`ColumnVector` with Arrow-compatible validity), the [null-validity model](null-validity-model.md)
> (#143), the [branchless null helpers](branchless-null-helpers.md) (#144, **reused** here for validity and
> counts), and the [type system](type-system.md) / [ADR-0008](../../adr/0008-type-system-row-format.md) ANSI and
> null contracts. Update this doc whenever an algorithm, the SIMD strategy, the consumption contract, or the
> parity methodology changes.

The kernels live in `src/DeltaSharp.Engine/Columnar/`, are engine-internal (no `DeltaSharp.Core` surface), and
are exercised through the friend-assembly test-access policy:

| File | Responsibility |
| --- | --- |
| [`AggregateKernels`](../../../src/DeltaSharp.Engine/Columnar/AggregateKernels.cs) | `SUM`/`MIN`/`MAX`/`COUNT`/`AVG` entry points, group-aware bulk update, and the tier-forced bulk SIMD reductions. |
| [`ComparisonKernels`](../../../src/DeltaSharp.Engine/Columnar/ComparisonKernels.cs) | `=`,`<>`,`<`,`<=`,`>`,`>=` over two vectors or a vector and a literal, producing a result + validity bitmap. |
| [`KernelScalars`](../../../src/DeltaSharp.Engine/Columnar/KernelScalars.cs) | The per-row scalar readers/comparators the **reference** path uses (the ADR-0001 oracle). |
| [`KernelTier`](../../../src/DeltaSharp.Engine/Columnar/KernelTier.cs) | The typed-value SIMD/scalar tier selector + dispatch gate (the typed analogue of #144's `NullMaskTier`). |
| [`KernelBenchmark`](../../../src/DeltaSharp.Engine/Columnar/KernelBenchmark.cs) | The lock-free, allocation-free timing harness recording batch size / null density / selectivity. |

## Design shape: two layers, scalar reference is the oracle

Every kernel family is split into two layers, mirroring #144:

1. **High-level entry points** take a `ColumnVector` (operator-facing, no `tier` parameter, always
   `KernelTier.Auto`). They classify the input shape and dispatch: a **contiguous, no-null** integer/temporal
   vector takes the SIMD fast path; **every other shape** (nulls, a selection, floating/decimal, mixed/widening
   types) takes the **scalar reference**.
2. **Low-level bulk reductions/compares** take a typed `ReadOnlySpan<T>` and a `KernelTier`. These are the SIMD
   primitives, and the `tier` parameter is what the parity tests force so each tier is reachable on any host.

Per [ADR-0001](../../adr/0001-execution-strategy.md) the **scalar reference is the correctness ground truth**.
It is computed through the logical-row [`ColumnVector.GetValue<T>`](../../../src/DeltaSharp.Engine/Columnar/ColumnVector.cs)
/ `IsNull` API, so it is correct over contiguous vectors, byte- or bit-offset slices, and zero-copy
[`SelectedColumnVector`](../../../src/DeltaSharp.Engine/Columnar/SelectedColumnVector.cs) selections alike. The
SIMD paths are an **additive** optimization that must produce an **identical** result (the aggregate reductions
and the comparison bitmaps are exact integer/bitwise ops, so the documented floating-point tolerance is
**zero** — see [Determinism](#determinism-why-some-paths-are-deliberately-scalar)).

`KernelScalars` deliberately lives in `Columnar` rather than reusing `Execution.Expressions.ScalarReader`: the
kernels are a **lower** layer that operators and the interpreter build *on*, so a `Columnar → Execution`
reference would invert the dependency. The comparison semantics here (Spark NaN/−0 order; decimal cross-scale;
date↔timestamp promotion) are byte-for-byte the interpreter's
([`ComparisonEvaluator`](../../../src/DeltaSharp.Engine/Execution/Expressions/ComparisonEvaluator.cs)), so both
tiers agree row-for-row.

## Aggregate kernels

### Spark null semantics (cited)

`SUM`/`MIN`/`MAX`/`AVG`/`COUNT(x)` **skip nulls**. An **empty or all-null** input yields SQL `NULL` (modeled as a
`null` nullable return) — except `COUNT(x)`, which yields `0`. `COUNT(*)` counts logical rows regardless of
validity. Result types follow Spark: `sum(int*)→bigint` (accumulate into `long`), `sum(float/double)→double`,
`sum(decimal(p,s))→decimal(min(38,p+10), s)` (the operator passes that target type), `avg(numeric)→double`.

### COUNT — reuses the #144 popcount

- `CountAll(vector)` = `vector.Length` (O(1)).
- `CountNonNull(vector)` = `vector.Length - vector.NullCount` (the vector's O(1) cached exact count).
- `CountNonNull(Validity)` = **`BitmapOps.PopCount`** (#144's hardware popcount) over a present, byte-aligned
  validity bitmap, the scalar `Validity.CountValid()` for a bit-unaligned offset, and `Validity.Length` for the
  absent (all-valid) bitmap without touching memory. This is the overload the operator calls when it holds a
  packed validity buffer.

### SUM — integral, with the no-null SIMD widening fast path

`SumInt64(vector, mode)` accumulates into `long`. The **fast path** is a contiguous `IntegerType` vector with no
nulls (`vector.Type is IntegerType && !vector.HasNulls && vector is not SelectedColumnVector`): it calls the bulk
`SumInt32`, which **widens** `Vector256<int>`/`Vector128<int>` lanes to `long` (`Vector256.Widen`) and sums into
a `long` accumulator. Integer addition is **associative and commutative**, so the lane-striped reduction is
**bit-identical** to the scalar tail and to every other tier, and widening to `long` means a single batch
(≤ a few thousand `int`s) cannot overflow the accumulator.

Every other shape — `long` (needs per-element overflow checking), `byte`/`short`, any nulls, or a selection —
uses the **scalar reference**: a left-to-right accumulate that skips null lanes and checks each add with the
**branchless** signed-overflow predicate

```
sum = unchecked(acc + x);
overflow = ((acc ^ sum) & (x ^ sum)) < 0;   // KernelScalars.AddOverflows
```

The two addends share a sign that differs from the result's sign exactly on overflow; this detects it without a
`checked` trap on the per-element path. (The null-skip is an honest data-dependent branch in the scalar
reference; only the no-null SIMD reduction is branch-free. The "branchless where practical" goal applies to the
word/lane formulas, not to null skipping over typed values.)

### SUM — ANSI overflow contract (cited, AC4)

On overflow the result follows [`AnsiMode`](../../../src/DeltaSharp.Engine/Types/AnsiMode.cs):
**`Ansi` throws [`ArithmeticOverflowException`](../../../src/DeltaSharp.Engine/Types/Exceptions.cs)**, **`Legacy`
yields `NULL`**. "Neither mode silently truncates or wraps" — DeltaSharp never wraps; it nulls, matching Spark's
non-ANSI path (`AnsiMode` doc / ADR-0008). Integral `MIN`/`MAX`/`COUNT` and floating `SUM` cannot overflow.

### SUM — floating (deterministic scalar reference) and decimal (exact)

`SumDouble(vector)` accumulates `double` **left-to-right, scalar only**. This is deliberate: a lane-striped SIMD
float reduction reassociates the adds, and IEEE addition is **not** associative, so the result would depend on
the hardware vector width and diverge across machines. The sequential scalar sum is therefore **the** reference
for *all* backends (tolerance 0); ±∞/NaN propagate per IEEE, matching Spark.

`SumDecimal(vector, resultType, mode)` accumulates an exact [`DecimalValue`](../../../src/DeltaSharp.Engine/Types/DecimalArithmetic.cs)
(`long` or `Int128` mantissa), then fits the running sum into the operator-supplied `resultType` via
`DecimalValue.ToType(resultType, mode)` (ANSI throws / Legacy nulls on out-of-range). Decimal has no SIMD path
(128-bit mantissa).

### MIN / MAX

- **Integral/temporal** (`MinInt64`/`MaxInt64` over a `ColumnVector`): a contiguous, no-null `IntegerType`/
  `DateType` vector reduces via the bulk `MinInt32`/`MaxInt32` (`Vector256.Min`/`Max`), and a `LongType`/
  `TimestampType` vector via `MinInt64`/`MaxInt64` (signed 64-bit `Vector*.Min`/`Max`). `Min`/`Max` are
  **order-independent**, so the SIMD reduction is identical across tiers. `byte`/`short`, nulls, or a selection
  use the scalar reference.
- **Floating** (`MinDouble`/`MaxDouble`): **scalar only**, under Spark's total order via
  `KernelScalars.CompareDouble` — **NaN is greatest** and **−0.0 == +0.0**. So `MAX` returns NaN if any value is
  NaN, `MIN` ignores NaN unless **every** value is NaN, and a ±0 tie keeps the first-seen value. Hardware
  `min`/`max` instructions implement IEEE minNum/maxNum (NaN-ignoring, −0/+0 unordered), which **disagree** with
  Spark's order, so they are intentionally not used.
- **Decimal** (`MinDecimal`/`MaxDecimal`): scalar, exact cross-scale via `KernelScalars.CompareDecimal`.

### AVG

`AverageDouble(vector)` is the Spark `avg(numeric)→double` mean: the `double` sum of non-null values divided by
their count, or `null` when there are none. Decimal input is averaged in double precision (lossy); for an
**exact** decimal mean the operator finalizes `SumDecimal` over `CountNonNull` itself.

### Group-aware bulk update — the #148 consumption contract

The hash-aggregate operator (#148) needs a **per-batch bulk update** keyed by a group-id vector, not a
whole-vector reduction. `AggregateKernels` exposes that as a scatter:

```csharp
void GroupSumInt64(ColumnVector values, ReadOnlySpan<int> groupIds,
                   Span<long> sums, Span<long> counts, Span<bool> overflowed, AnsiMode mode);
void GroupCountNonNull(ColumnVector values, ReadOnlySpan<int> groupIds, Span<long> counts);
```

**Contract** (what #148 owns vs. what the kernel does):

- The `sums`/`counts`/`overflowed` accumulator spans are **caller-owned and zero-initialized**, indexed by group
  id, and **persist across batches** — a partial/final merge is simply repeated bulk-update calls over successive
  batches. The kernel never allocates them.
- For each logical row `i`, the non-null `values[i]` is added into `sums[groupIds[i]]` and `counts[groupIds[i]]`
  is incremented. Null rows are skipped (so `counts` is the per-group non-null count).
- **Overflow:** under `Ansi`, a group overflow throws immediately; under `Legacy`, the kernel sets
  `overflowed[g]` and stops accumulating into that **poisoned** group only (other groups are unaffected).
- **Finalize (operator's job):** group `g`'s `SUM` is SQL `NULL` when `counts[g] == 0` (no non-null rows) **or**
  `overflowed[g]` is set; otherwise `sums[g]`.
- The update is a **scatter by group id** (data-dependent write index), so it is intentionally **scalar** — there
  is no SIMD scatter here — but it still avoids row materialization, boxing, and per-row virtual dispatch. A bad
  group id (`>= groupCount`) fails fast with `ArgumentException`.
- **Extension:** `MIN`/`MAX` extend by seeding a per-group accumulator with the type identity plus a parallel
  `seen` mask; decimal sum extends with a `DecimalValue[]` accumulator. These are documented but **deferred** to
  the operator story (#148) to keep this story's surface to the v1 `SUM`/`COUNT` the operator needs first.

## Comparison kernels

### Output shape and the propagate-on-any-null contract (cited)

`Compare(op, left, right, resultValues, resultValidity)` writes two Arrow LSB-first bitmaps and returns the null
count:

- `resultValues` — bit set ⇒ the predicate is `true`.
- `resultValidity` — bit set ⇒ the row is non-null.

A comparison whose **either** operand is `NULL` is `NULL` (propagate-on-any-null, #143): `out_valid =
left_valid & right_valid`. Both bitmaps are **canonical** — padding bits at index `≥ length` are `0` — and the
invariant **`value ⊆ valid`** holds (a null row has *both* bits clear), so the value bitmap is byte-comparable
with the scalar reference and feeds straight back into `BitmapOps`/`NullMasks`.

### Reusing the #144 seam for validity

The `out_valid = left_valid & right_valid` AND-combine is delegated to **`NullMasks.PropagateBinary`** (#144)
whenever **both** operands surface a packed validity bitmap via
[`ColumnVector.TryGetValidity`](../../../src/DeltaSharp.Engine/Columnar/ColumnVector.cs); the vector-vs-literal
overloads use **`NullMasks.PropagateUnary`** (the literal is never null). Today the base `TryGetValidity` returns
a bitmap only on the **no-null fast path** (`Validity.AllValid`); a managed/Arrow vector that *has* nulls does
**not** yet expose a packed buffer (an explicit deferral in [null-validity-model.md](null-validity-model.md)), so
those inputs take a **per-row reference fallback** that ANDs `!left.IsNull(i) && !right.IsNull(i)`. The seam is
wired so that the moment a concrete vector overrides `TryGetValidity` to expose its packed bitmap, the vectorized
AND-combine engages **with no kernel change**. The no-null SIMD path materializes all-valid via
**`BitmapOps.FillValid`** (#144).

### Comparison kinds (mirrors the interpreter exactly)

The pair of operand types is classified into one of four kinds, identical to the interpreter's
`ComparisonEvalKind` resolution:

| Kind | When | Per-row sign |
| --- | --- | --- |
| `Int64` | both integral / same temporal / boolean (no decimal/float) | `ReadInt64(l).CompareTo(ReadInt64(r))` (boolean as 0/1, so `false < true`) |
| `Double` | either operand float/double | `CompareDouble` (Spark total order: NaN greatest, −0 == +0) |
| `Decimal` | either operand decimal (and neither float) | `CompareDecimal` (exact cross-scale; rescale to the wider scale in `Int128`) |
| `TemporalPromote` | one `date`, one `timestamp` | promote the date to micros (`days × 86_400_000_000`) then compare as `long` |

`RequireComparable` rejects unsupported operand types (string/binary/struct) with a clear `NotSupportedException`
— string/binary stay on the interpreter path (AC2 scope is **primitive/decimal/date/timestamp**).

### SIMD fast path (vector vs vector)

Taken only when `nullCount == 0`, the kind is `Int64`, both vectors are **contiguous** (`is not
SelectedColumnVector`), and **same-typed**: `int`/`date` → bulk `CompareInt32`, `long`/`timestamp` → bulk
`CompareInt64`. The bulk kernels pack **8 rows per output byte**:

- A per-lane mask is built from only **`Equals` / `LessThan` / `GreaterThan`** plus complement, because integer
  order is **total**: `<=` is `~GreaterThan`, `>=` is `~LessThan`, `<>` is `~Equals` (no dependence on the
  optional `LessThanOrEqual`/`GreaterThanOrEqual` vector helpers). Complementing all lanes is correct because
  every SIMD block is a **full** lane group — partial tail rows never enter the vector path.
- `Vector*.ExtractMostSignificantBits(mask)` turns the lane mask directly into result bits: a `Vector256<int>`
  (8 lanes) fills a whole byte; a `Vector256<long>` (4 lanes) fills a nibble (two `Vector256<long>` compares per
  byte); `Vector128` fills 4 (`int`) / 2 (`long`) bits per op and the byte is assembled by shifting.
- The trailing `n & 7` rows are a **scalar** block that sets only the low `n & 7` bits, so the final byte's
  padding stays `0` (canonical) and no full-byte complement ever touches a padding lane.

Because every op reduces to lane masks + bit extraction (exact), the bitmap is **identical across Vector256,
Vector128, and scalar tiers** — this is what the forced-tier parity tests assert.

### Vector vs scalar literal (predicate pushdown)

`Compare(op, column, long literal, …)` compares an integral/temporal column against a literal in the column's
units (days for `date`, micros for `timestamp`): SIMD when the column is contiguous and no-null (`int`/`date`
when the literal fits `int`, else `long`/`timestamp`), broadcasting `Vector*.Create(literal)`; scalar reference
otherwise (e.g. an `int` column vs an out-of-`int`-range literal reads the column as `long`). `Compare(op,
column, double literal, …)` is **scalar only** under `CompareDouble` (floating comparison is not vectorized — see
[Determinism](#determinism-why-some-paths-are-deliberately-scalar)). Both use `PropagateUnary` validity (the
literal is never null).

## Selection-aware contract

`ColumnVector.GetValues<T>()` throws on a `SelectedColumnVector` (the documented non-contiguous type), so every
SIMD fast path is **guarded by `vector is not SelectedColumnVector`** and a selection falls to the scalar
reference. The scalar reference reads through logical-row `GetValue<T>`/`IsNull`, which the selection remaps to
physical rows — so a selected vector produces exactly the result of the same comparison/aggregate over the
selected logical rows, in selection order. Tests cover this with zero-copy `Select(...)` views (e.g. summing the
odd-indexed selection, comparing two selected views).

## SIMD width and AOT-safe fallback (AC3)

The inner loops use the modern [`System.Runtime.Intrinsics`](https://learn.microsoft.com/dotnet/api/system.runtime.intrinsics)
cross-platform vectors guarded by `IsHardwareAccelerated`, with a scalar tail. There is **no
`[RequiresDynamicCode]` / IL emit** anywhere — only static SIMD the JIT and the NativeAOT ILCompiler lower
directly.

| Tier | Width | Guard | x64 | arm64 (Apple Silicon dev/CI box) |
| --- | --- | --- | --- | --- |
| `Vector256<T>` | 32 B | `Vector256.IsHardwareAccelerated` | AVX2 | — (constant-folded away under `Auto`) |
| `Vector128<T>` | 16 B | `Vector128.IsHardwareAccelerated` | SSE2 | AdvSimd / NEON |
| scalar | per element | — | tail | tail |

**Why this is AOT-clean.** `VectorNNN.IsHardwareAccelerated` are `[Intrinsic]` properties the JIT/ILCompiler
resolve to **compile-time constants per target**, so an unsupported tier is **dead-code-eliminated** rather than
throwing `PlatformNotSupportedException` — the exact "intrinsics guarded by `IsSupported` with a scalar
fallback" requirement (ADR-0001 / [ADR-0014](../../adr/0014-target-framework-aot.md)). On arm64 the `Vector256`
tier vanishes and `Vector128` (NEON) runs; on a SIMD-less target both vanish and the scalar tail runs. The
`dotnet publish … -p:PublishAot=true -warnaserror` gate over `DeltaSharp.Executor` proves the whole path is
IL-clean.

**The tier-forcing test seam.** The CI host is **arm64** where `Vector256.IsHardwareAccelerated == false`, so the
`Vector256` body is constant-folded away under `Auto` and a mutation there could never fail a test. Every bulk
kernel therefore threads an optional [`KernelTier`](../../../src/DeltaSharp.Engine/Columnar/KernelTier.cs)
(`Auto` in production; `Scalar`/`Vector128`/`Vector256` for tests), resolved once per call by `KernelTierGate`.
Because the kernels are written against the **portable** `Vector256<T>` API (whose software fallback runs on any
architecture), a *forced* `Vector256` loop executes and is parity-checked **even on arm64**. Under `Auto` the
inner-loop codegen and AOT-safety are unchanged. As with #144, the cost of the seam is that a host-unsupported
tier becomes a never-taken branch evaluated once at loop entry rather than being fully DCE'd; the per-element hot
loop and the AOT gate are unaffected. Unlike #144 there is no `Word` (ulong) tier — a typed reduction widens or
compares whole `int`/`long` lanes, not packed bits.

> **Note on the AC3 wording.** The AC lists "x64 Vector256/Vector512, Arm AdvSimd, scalar-only". The portable
> `Vector256` is the widest tier implemented; **AVX-512 / `Vector512` is a documented follow-up** — it can be
> added behind the same `IsHardwareAccelerated` guard without any contract change. Arm AdvSimd is covered by the
> `Vector128`/NEON tier; scalar-only is the always-present tail.

### Determinism: why some paths are deliberately scalar

| SIMD path | Why it is bit-exact across tiers |
| --- | --- |
| `SUM(int)` via widening | integer `+` is associative/commutative ⇒ lane-striped == sequential |
| `MIN`/`MAX(int/long)` | `min`/`max` are order-independent ⇒ lane-striped == sequential |
| comparison → bitmap | per-lane mask + bit extraction is exact |

| Scalar-only path | Why SIMD would change the result |
| --- | --- |
| `SUM(float/double)` | IEEE `+` is **not** associative ⇒ a width-dependent lane reduction diverges across machines; the sequential sum is the single cross-backend reference |
| `MIN`/`MAX(float/double)` | hardware min/max = IEEE minNum/maxNum, which **disagree** with Spark's "NaN greatest, −0==+0" total order |
| anything decimal | 128-bit mantissa, no SIMD lane type |
| `long` `SUM` | per-element overflow detection has no cheap SIMD form; the scalar checked add is the reference |

Because the chosen SIMD paths are exact, the AC's "match exactly or within documented floating-point tolerance"
holds with **tolerance 0** — there is no floating SIMD reduction to tolerate.

## Parity with the scalar oracle (AC1, AC2)

The tests use **independent in-test oracles** written in plain C# from the source arrays — deliberately **not**
reusing `KernelScalars` — so a kernel that drifts (SIMD *or* its own scalar reference) fails a parity assertion
rather than agreeing with a co-mutated helper. (`AggregateKernelsTests`, `ComparisonKernelsTests`,
`KernelTierTests`, `KernelBenchmarkTests`, with builders/oracles in `KernelTestSupport`.)

- **Aggregates** assert the kernel equals an independent accumulate/track oracle across **null densities**
  `{0.0, 0.1, 0.5, 0.9, 1.0}` and the **length sweep** `{0,1,7,8,9,63,64,65,127,128,255,256,257,1000,1024,4096}`
  (incl. **257** the sub-byte tail and **1000** the vector-width tail), plus empty/all-null → `null`, selections,
  Spark NaN/−0 min/max, decimal cross-scale, and the `long`/group ANSI-overflow contract (throws) and Legacy
  (nulls / poisons the group).
- **Comparisons** assert **byte-identical value + validity bitmaps**, an **equal null count**, and **canonical
  padding** against a per-row oracle for all six ops, every kind (int/long/short/bool/date/timestamp/float/
  double/decimal-cross-scale/date↔timestamp), across null densities, including NaN/±0/±∞ doubles.
- **Forced-tier theories** run every bulk reduction and compare with `Scalar`/`Vector128`/`Vector256` forced and
  assert all tiers equal the oracle — making the wide `Vector256` body mutation-killable **on arm64**.
- **`KernelTierTests`** pin AC3: `Auto` tracks `VectorNNN.IsHardwareAccelerated`, forced `Vector256` is reachable
  via the software fallback, forced `Vector128`/`Scalar` suppress the wider loops, and `Auto` equals the
  hardware-appropriate forced tier for a real reduction.

**Non-vacuity (mutation evidence).** Mutating the `Vector256` `LessThan` mask to `GreaterThan` fails 13 of 16
`CompareInt32Bulk_ForcedTierParity` lengths (the short lengths that never fill a SIMD byte survive, as expected)
— proving the 256-bit path is genuinely exercised on this arm64 host. Mutating `AddOverflows` to always-`false`
fails every `SUM`/group overflow test. Every AC is covered by a test that a kernel mutation breaks.

## Zero-allocation methodology

The hot path allocates **nothing**: every entry point writes into **caller-owned** spans or returns a value type
(`long?`/`double?`/`DecimalValue?`/`int` — `Nullable<T>` and `DecimalValue` are structs, no boxing); the SIMD
accumulators stay in registers; there is no LINQ, reflection, virtual dispatch on the per-row path, or row
materialization. This is pinned by tests that warm up, then call the kernel 1000× inside a
`GC.GetAllocatedBytesForCurrentThread()` window and assert ≤ 64 B slack (mirroring the #143/#144 gate). The
benchmark's timed loop is held to the same bar over 200k iterations.

## Benchmark methodology

[`KernelBenchmark`](../../../src/DeltaSharp.Engine/Columnar/KernelBenchmark.cs) is a **lightweight timed
harness**, deliberately not BenchmarkDotNet: adding it would bring a package dependency and a
`packages.lock.json` to the otherwise **lock-free** `DeltaSharp.Engine` and pull in dynamic-codegen machinery
that fights the NativeAOT posture (ADR-0001/ADR-0014). Each measurement builds its input vectors with a seeded
**xorshift64** generator (never `System.Random`, banned in production), warms up, then times a tight kernel loop
with `Stopwatch.GetTimestamp()`/`GetElapsedTime` so the measured region is allocation-free, accumulating a
`Checksum` so the loop cannot be dead-code-eliminated. Every `Result` is **self-describing** — it records the
**batch size** and **null density** (the #149 AC), plus the realized comparison **selectivity** (fraction of
non-null rows whose predicate is true, from `BitmapOps.PopCount` of the outputs), iterations, elapsed, ns/row,
and the active SIMD width. `RunSuite` sweeps the ADR-0001 vectorized batch band `{1024, 4096, 8192}` × null
densities `{0.0, 0.1, 0.5, 0.9}`, one labeled cell per combination for a `performance-benchmarking-engineer`
regression gate.

## Non-goals / deferred scope

- **Bitmap → selection-vector left-pack** (compacting a comparison result bitmap into a `SelectionVector` so
  downstream operators process only matching rows) is **STORY-03.3.2 (#150/#153)**, built **on** these
  comparison bitmaps. This is the headline deferral the comparison kernels feed.
- **Masked-with-nulls SIMD** (a value-SIMD path for *null-bearing* vectors) is deferred: it requires a packed
  validity bitmap surfaced from null-bearing managed/Arrow vectors, itself a documented
  [null-validity-model.md](null-validity-model.md) deferral. Today null-bearing inputs take the scalar reference;
  the validity AND-combine seam (`NullMasks.PropagateBinary`) is already wired for when that lands.
- **Floating SIMD `SUM`/`MIN`/`MAX`** and **decimal SIMD** are intentionally scalar (determinism / Spark NaN
  order / 128-bit mantissa) — see [Determinism](#determinism-why-some-paths-are-deliberately-scalar).
- **SIMD floating vector-vs-scalar** comparison would need a NaN/−0-correct vector predicate; left as a
  follow-up while the scalar `CompareDouble` reference is the oracle.
- **AVX-512 / `Vector512`** wider tier (AC3 wording) can be added behind the same `IsHardwareAccelerated` guard
  without a contract change.
- **Group-aware `MIN`/`MAX`/decimal/avg** bulk updates extend `GroupSumInt64` (identity-seeded accumulators +
  `seen` mask / `DecimalValue[]`); the contract is documented above, the v1 surface ships `SUM`/`COUNT` for #148.
- **String/binary comparison** stays on the interpreter path (byte-lexicographic) — outside AC2's
  primitive/decimal/date/timestamp scope.

## References

- [ADR-0001: Execution strategy](../../adr/0001-execution-strategy.md) ·
  [ADR-0002: In-memory columnar batch format](../../adr/0002-columnar-batch-format.md) ·
  [ADR-0008: Type system & row format](../../adr/0008-type-system-row-format.md) ·
  [ADR-0014: Target framework & AOT](../../adr/0014-target-framework-aot.md)
- [branchless-null-helpers.md](branchless-null-helpers.md) (#144, the reused validity/popcount seam) ·
  [null-validity-model.md](null-validity-model.md) (#143) · [columnar-contracts.md](columnar-contracts.md) ·
  [type-system.md](type-system.md) · [interpreted-expression-evaluator.md](interpreted-expression-evaluator.md)
  (the comparison-semantics twin) · [native-aot.md](native-aot.md) · [testing-conventions.md](testing-conventions.md)
- [EPIC-03: Vectorized execution backend](../../planning/epics/EPIC-03-vectorized-execution-backend.md)
- .NET — `System.Runtime.Intrinsics` `Vector128`/`Vector256` (`IsHardwareAccelerated`, `LoadUnsafe`, `Widen`,
  `Min`/`Max`, `Equals`/`LessThan`/`GreaterThan`, `ExtractMostSignificantBits`); Apache Arrow validity bitmaps
  (LSB-first); Apache Spark aggregate null semantics, `SQLOrderingUtil` float ordering, and ANSI overflow.
