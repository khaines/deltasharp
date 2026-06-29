# Null and validity model for columnar compute (v1)

> **Status:** living document. Created with
> [STORY-02.6.1](../../planning/epics/EPIC-02-columnar-memory-type-system.md#story-0261-specify-validity-bitmap-and-null-propagation-contracts)
> (#143). Grounded in [ADR-0002](../../adr/0002-columnar-batch-format.md) (mutable, selection-vector-aware
> `ColumnBatch`/`ColumnVector`; Arrow-compatible validity) and
> [ADR-0008](../../adr/0008-type-system-row-format.md) (SQL three-valued logic, ANSI null semantics).
> This is the validity companion to [columnar-contracts.md](columnar-contracts.md) — it specifies how
> kernels read null state and derive output validity. Branchless/SIMD null helpers are
> [STORY-02.6.2](../../planning/epics/EPIC-02-columnar-memory-type-system.md#story-0262-add-branchless-null-helper-primitives)
> (#144) and build on this contract. Update this doc whenever the validity model changes.

DeltaSharp's scalar, SIMD, Arrow-backed, and future off-heap vectors must agree on **where the nulls
are** and on **how an expression's nulls flow to its output**. This document defines that single
contract so every kernel — and every backend — produces identical null results (the parity oracle of
ADR-0001). The implementation lives in `src/DeltaSharp.Engine/Columnar/`:
[`Bitmap`](../../../src/DeltaSharp.Engine/Columnar/Bitmap.cs) (raw bit ops),
[`Validity`](../../../src/DeltaSharp.Engine/Columnar/Validity.cs) (the offset-aware view), and
[`NullPropagation`](../../../src/DeltaSharp.Engine/Columnar/NullPropagation.cs) (3VL rules), with
[`ColumnVector.TryGetValidity`](../../../src/DeltaSharp.Engine/Columnar/ColumnVector.cs) bridging a
vector to a `Validity`.

## The bitmap

Validity is an **Arrow-compatible, LSB-first** bitmap: a **set** bit means the row is **valid**
(non-null); a **cleared** bit means **null**. Bit `i` lives in byte `i / 8` at bit position `i % 8`,
so logical row `9` is `byte[1] & 0x02`. This is byte-for-byte the layout the Arrow-backed vector
already exposes (`Apache.Arrow` `Values`/`IsValid` are LSB-first), so no re-packing happens at the
boundary, and it is the layout `ManagedFixedWidthColumnVector`/`ManagedVariableWidthColumnVector`
already write.

### The bitmap is optional — absence means all-valid (AC1)

A column with no nulls carries **no validity buffer**. The contract treats an **absent (empty)**
bitmap as *all-valid*, and it does so **without ever allocating a synthetic all-ones buffer**:

- `Validity.AllValid(length)` and `new Validity(ReadOnlySpan<byte>.Empty, 0, length)` both build the
  no-buffer view. `HasBitmap` is `false`, `IsNull(i)` is `false` for every row, and `CountNulls()`
  short-circuits to `0` without touching memory.
- `Bitmap.CountNulls(empty, offset, length)` returns `0` (no out-of-range read on the empty span).
- `ColumnVector.TryGetValidity(out Validity v)` returns `Validity.AllValid(Length)` for any vector
  whose `HasNulls` is `false`. The whole probe is a `ref struct` on the stack — a regression test
  pins it to ~0 bytes allocated.

This keeps the common no-null path branch-light and allocation-free: a kernel asks
`NullPropagation.NeedsValidityBitmap(...)` first and only rents an output bitmap when an input
actually has one.

### Logical offset and slicing (AC2)

All indices are **logical**: row `0` is the first row of a view. A `Validity` carries an `Offset`
(in bits) into a shared buffer, so the resolved physical bit is exactly:

```
isValid(i) = bitmap.IsEmpty ? true : Bitmap.Get(bitmap, Offset + i)   // Arrow LSB-first at (Offset + i)
```

`Validity.Slice(offset, length)` returns a sub-range over the **same** buffer with
`Offset + offset` — no bits are copied, an absent buffer stays absent, and a present buffer keeps
resolving the parent's bits. This matches `ColumnVector.Slice` semantics, so a sliced vector and its
validity stay consistent.

### The `Validity` view

`Validity` is a `readonly ref struct` — a transient, stack-only, zero-copy view over a buffer
(mirroring `ReadOnlySpan<T>`). Kernels take it by value and never box it.

| Member | Meaning |
| --- | --- |
| `Length` | Logical row count of the view. |
| `Offset` | Logical bit offset of row `0` into `Bits`. |
| `Bits` | Raw LSB-first buffer, or **empty** when all-valid. SIMD/block kernels (STORY-02.6.2) consume this. |
| `HasBitmap` | Whether a buffer is present. `false` is the all-valid fast path. |
| `IsValid(i)` / `IsNull(i)` | Per-row null state (bounds-checked); absent buffer ⇒ valid. |
| `CountNulls()` / `CountValid()` | Deterministic null/valid counts over `[0, Length)` (AC4). |
| `Slice(o, l)` | Zero-copy sub-range; accumulates `Offset`. |
| `AllValid(n)` | The absent-buffer, all-valid view (no allocation). |

## Null propagation (AC3)

Two families cover v1 expressions. Both are specified by a **single-lane `bool?` reference** (where
`null` is SQL `UNKNOWN`) in `NullPropagation`; the bulk span kernels compute the *same* result
lane-by-lane and exist so a later SIMD/branchless path has a parity oracle.

### Propagate-on-any-null (arithmetic, comparison, most scalar functions)

The output is null wherever **any** input is null, independent of the input *values*:

- Unary: `out_valid[i] = in_valid[i]` — `NullPropagation.PropagateUnary`.
- Binary: `out_valid[i] = left_valid[i] AND right_valid[i]` — `NullPropagation.PropagateBinary`.

Both write the output validity into a caller-provided `Span<byte>` and return the null count. When
**no** input has a bitmap the result is all-valid, so `NeedsValidityBitmap(...)` returns `false` and
the caller skips the output bitmap entirely (the AC1 no-alloc path).

### Kleene three-valued logic (boolean `AND` / `OR` / `NOT`)

Boolean connectives are **value-aware**: a `FALSE` rescues `AND` and a `TRUE` rescues `OR` even when
the other operand is null. These are the SQL/Spark truth tables:

| `AND` | F | T | N |   | `OR` | F | T | N |   | `NOT` | |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **F** | F | F | F |   | **F** | F | T | N |   | **F** | T |
| **T** | F | T | N |   | **T** | T | T | T |   | **T** | F |
| **N** | F | N | N |   | **N** | N | T | N |   | **N** | N |

`NullPropagation.KleeneAnd/KleeneOr/KleeneNot` exist as both the scalar `bool?` reference and the
bulk `(values, validity) → (values, validity)` kernels (writing a deterministic `false` placeholder
into null output lanes). The defining contrast with propagate-on-any-null:

```
FALSE AND NULL  =>  Kleene: valid FALSE      |  propagate-on-any-null: NULL
TRUE  OR  NULL  =>  Kleene: valid TRUE       |  propagate-on-any-null: NULL
```

## Null counts and determinism (AC4)

`Validity.CountNulls()` / `CountValid()` and `Bitmap.CountNulls(bitmap, offset, length)` are pure
functions of the bits, offset, and length, so they are **deterministic** (repeated and re-sliced
calls agree). Behavior across the canonical shapes:

| Shape | Result |
| --- | --- |
| **all-null** (cleared buffer) | `CountNulls == Length` |
| **no-null** (set buffer, or absent buffer) | `CountNulls == 0` |
| **empty** (`Length == 0`, any buffer) | `CountNulls == 0` |
| **sliced** (non-zero offset) | counts only the `[Offset, Offset+Length)` window |

A randomized parity test compares `CountNulls` against an independent raw-bit oracle across hundreds
of random buffers, offsets, and window lengths.

## Worked example

```csharp
// a: [1, null, 3]   b: [10, 20, null]      a + b  (propagate-on-any-null)
var aBits = new byte[1]; Bitmap.Set(aBits, 0, true); Bitmap.Set(aBits, 2, true);   // valid, null, valid
var bBits = new byte[1]; Bitmap.Set(bBits, 0, true); Bitmap.Set(bBits, 1, true);   // valid, valid, null

var av = new Validity(aBits, 0, 3);
var bv = new Validity(bBits, 0, 3);

if (NullPropagation.NeedsValidityBitmap(av, bv))          // true: a has nulls
{
    var outBits = new byte[Bitmap.ByteCount(3)];
    int nulls = NullPropagation.PropagateBinary(av, bv, outBits);   // out_valid = [valid, null, null]
    // nulls == 2; rows 1 and 2 are null because either operand was null
}
```

## Non-goals / deferrals

- **Branchless/SIMD null helpers, popcount-accelerated counts, and combined validity over selection
  vectors** are [STORY-02.6.2](../../planning/epics/EPIC-02-columnar-memory-type-system.md#story-0262-add-branchless-null-helper-primitives)
  (#144). The v1 bulk kernels here are scalar **references** the SIMD tier must match; `Validity.Bits`
  + `Offset` is the seam those block kernels read.
- **Exposing a packed bitmap from null-bearing managed/Arrow vectors.** `ColumnVector.TryGetValidity`
  guarantees only the no-null fast path in the base; a concrete vector that owns a packed buffer may
  override it to surface that buffer (enabling the bulk path for null-bearing inputs) without changing
  this contract.
- **Nested (`array`/`map`/`struct`) child-vector validity** follows the columnar-contracts deferral.

## References

- [ADR-0002: In-memory columnar batch format](../../adr/0002-columnar-batch-format.md) ·
  [ADR-0008: Type system and row format](../../adr/0008-type-system-row-format.md)
- [columnar-contracts.md](columnar-contracts.md) · [type-system.md](type-system.md) ·
  [testing-conventions.md](testing-conventions.md)
- [EPIC-02: Columnar Memory & Type System](../../planning/epics/EPIC-02-columnar-memory-type-system.md)
- Apache Arrow validity bitmaps (LSB-first, optional null buffer); Apache Spark Kleene 3VL
  (`And`/`Or` null semantics).
