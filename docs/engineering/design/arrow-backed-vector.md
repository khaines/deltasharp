# Arrow-backed `ColumnVector` (STORY-02.2.1, #135)

> **Status:** living document. Implements [STORY-02.2.1](../../planning/epics/EPIC-02-columnar-memory-type-system.md#story-0221-implement-arrow-backed-columnvector)
> under [ADR-0002](../../adr/0002-columnar-batch-format.md) ("Arrow-first; Arrow at the edges").
> See [columnar-contracts.md](columnar-contracts.md) for the contract surface this satisfies.

DeltaSharp's vectorized operators bind to `ColumnVector` (never `Apache.Arrow`). `ArrowColumnVector`
is the first non-managed implementation of that contract: it wraps an Apache Arrow array so Parquet,
Arrow Flight, and the C Data Interface bridge into the engine zero-copy, getting a working columnar
engine fast while a later off-heap vector swaps in for hot operators with **no operator changes**.

## Boundary, not surface (AC4 of #133)

`Apache.Arrow` is referenced **only** under `src/DeltaSharp.Engine/Columnar/Arrow/`. The lone
Arrow-named member is the edge factory:

```csharp
ColumnVector v = ArrowColumnVector.Wrap(arrowArray); // IArrowArray in, ColumnVector out
```

`ColumnContractTests.ContractSurface_NamesNoApacheArrowType` pins that the operator-facing contracts
(`ColumnVector`/`MutableColumnVector`/`ColumnBatch`/`SelectionVector`/…) name no Arrow type. The
engine assembly *does* now reference Arrow — that is exactly the "Arrow at the edges" move — but the
hot path stays Arrow-free.

## Offset and validity (AC1, AC2)

`PrimitiveArray<S>.Values`, `IsNull(i)`, and `NullCount` are already adjusted for the array's
physical `Offset`, and Arrow validity is **LSB-first, set = valid** — the same order DeltaSharp's
`Bitmap` uses. So a sliced array (non-zero offset) wraps with no offset arithmetic and no bitmap
copy; the wrapper reports `Offset = array.Offset`, `GetValues<T>()` is the offset-adjusted `[0,Length)`
span, and `IsNull(i)` matches Arrow for every tested offset (including across byte boundaries).

## Immutability (AC3)

Arrow arrays are immutable, so the wrapper is a `ColumnVector`, not a `MutableColumnVector` — there is
no append/set path that could touch Arrow buffers. An operator that needs output builds a
DeltaSharp-owned vector via `ColumnVectors.Create(...)` and writes into that; the source Arrow buffers
are never mutated. `Slice` re-wraps Arrow's zero-copy slice; `Select` uses the base gather view.

## Type mapping and unsupported gaps (AC4)

| Arrow array | DeltaSharp type | storage |
| --- | --- | --- |
| Int8 / Int16 / Int32 / Int64 | tinyint/smallint/int/bigint | byte (from sbyte) / short / int / long |
| Float / Double | float / double | float / double |
| Date32 | date | int (days) |
| Timestamp(µs) | timestamp | long (micros) |
| String / Binary | string / binary | UTF-8 / raw bytes |

`Wrap` throws a precise `UnsupportedTypeException` (no silent data loss) for v1 gaps: bit-packed
`BooleanArray` (≠ DeltaSharp's 1-byte bool), `Decimal128` (compact decimal is 8-byte), unsigned and
half-float primitives, non-microsecond timestamps, `NullArray`, and nested array/map/struct. These
move to STORY-02.2.2 (#136, round-trip) and custom off-heap (FEAT-02.3).
