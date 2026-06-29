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

---

# Arrow boundary conversion (STORY-02.2.2, #136)

> **Status:** living document. Implements [STORY-02.2.2](../../planning/epics/EPIC-02-columnar-memory-type-system.md)
> under [ADR-0002](../../adr/0002-columnar-batch-format.md). Builds on the per-vector `Wrap` (#135)
> above; same "Arrow at the edges" rule.

`#135` wrapped a single Arrow array. `#136` lifts that to the **batch** boundary that a Parquet or
Arrow Flight reader/writer actually crosses, and closes the round-trip so a batch can leave for Arrow
and come back unchanged. The whole boundary is two static methods on `ArrowBatchConverter`:

```csharp
ArrowColumnBatch batch = ArrowBatchConverter.FromArrow(recordBatch, ownership); // RecordBatch -> ColumnBatch
RecordBatch       rb    = ArrowBatchConverter.ToArrow(batch);                    // ColumnBatch -> RecordBatch
```

`ArrowColumnBatch` is a `ColumnBatch` (operator-facing) that also `IDisposable` — no member names an
`Apache.Arrow` type, so `ColumnContractTests.ContractSurface_NamesNoApacheArrowType` still holds and
the boundary stays at `Wrap`/the converter. Everything under `Columnar/Arrow/` (`ArrowBatchConverter`,
`ArrowColumnBatch`, `ArrowColumnReader`, `ArrowColumnWriter`, `ArrowSchemaMapper`,
`ArrowNestedColumnVector`, `ArrowImportOwnership`) is the only code that sees Arrow.

## Capability matrix (AC1)

Import is column-wise. Three strategies, chosen by whether DeltaSharp's physical layout matches
Arrow's:

| Arrow array | import strategy | DeltaSharp type | offset on import |
| --- | --- | --- | --- |
| Int8/16/32/64, Float, Double, Date32, Timestamp(µs), String, Binary | **zero-copy** via `ArrowColumnVector.Wrap` | tinyint…/float/double/date/timestamp/string/binary | **preserved** |
| Struct, List, Map | **opaque pass-through** (`ArrowNestedColumnVector`, zero-copy) | struct/array/map (recursive) | **preserved** |
| Boolean | **materialized** (Arrow packs 8 rows/byte; DeltaSharp uses a 1-byte bool) | boolean | resets to `0` |
| Decimal128 | **materialized** (Arrow is always 16 bytes; DeltaSharp compacts ≤18-digit to 8) | decimal(p,s) | resets to `0` |
| unsigned / half-float / Date64 / Time / Decimal256 / non-µs Timestamp / Null / large&view | **throw** `UnsupportedTypeException` (names the gap) | — | — |

Export (`ToArrow`) reads through the logical `ColumnVector` contract, so a managed, Arrow-backed,
sliced, or selected batch all export the same way: flat columns are rebuilt into **fresh managed
buffers** (the returned `RecordBatch` owns its memory and is independent of any imported source);
nested columns are passed back through as a retained Arrow reference. The Arrow field type is read off
the built array, so decimal precision/scale and the microsecond timestamp unit are carried exactly.
Booleans are re-packed LSB-first and decimals re-widened to 16 little-endian two's-complement bytes.

The schema maps 1:1 and recursively (`ArrowSchemaMapper`): every Arrow field resolves to exactly one
v1 `DataType` (validating nested children all the way down) or throws — never a silent coercion. Field
names and nullability round-trip, so `original.Schema.Equals(roundTripped.Schema)`.

## Validity and null counts (AC2)

Validity needs no translation: Arrow and DeltaSharp's `Bitmap` are both **LSB-first, set = valid**.
Zero-copy and nested columns expose the source validity buffer directly (offset-adjusted); materialized
columns copy nullness per logical row. A null-free column exports an **empty** validity buffer
(`ArrowBuffer.Empty`, Arrow's "all valid"); a column with nulls exports a packed bitmap and the exact
`nullCount`. All-null, no-null, and mixed-null arrays preserve both the per-row null mask and the null
count across `FromArrow` → `ToArrow` → `FromArrow`.

## Slices and offset (AC3)

A sliced Arrow array (non-zero `Offset`) imports with its **logical row order intact** in every case.
For zero-copy and nested columns the physical `Offset` is **preserved** (the wrapper points at the
parent's shared buffers at the slice offset). For the two materialized types (boolean, decimal) the
rows are copied in logical order into a fresh buffer, so `Offset` legitimately **resets to `0`** while
values and validity are unchanged — the documented layout-mismatch caveat, not data loss.

## Ownership and disposal (AC4)

Import is zero-copy in both modes, so the source `RecordBatch`'s buffers stay live behind the imported
columns. `ArrowImportOwnership` makes "who frees them" explicit:

| mode | disposing the `ArrowColumnBatch` | the caller |
| --- | --- | --- |
| `Borrowed` (default) | releases **nothing** (only closes the view) | still owns the source; must keep it alive while the batch/any slice/selection is used, then dispose it |
| `Transfer` | disposes the source **exactly once** (idempotent on repeated/concurrent `Dispose`) | must not use or dispose the source afterward |

Exactly-once is enforced with an `Interlocked.Exchange` flag: the first `Dispose` wins and performs the
documented release; later calls are no-ops. After disposal the **batch-level** accessors (`Column`,
`Slice`, `WithSelection`) throw `ObjectDisposedException` as lifecycle **hygiene** — this is *not* a
memory-safety guarantee for views already handed out (see **Lifetime contract / use-after-free** below).
Tests prove the disposal accounting at both levels — a counting `RecordBatch` (dispose funnel entered
once) and a counting `MemoryAllocator` (every imported buffer freed once, none leaked) — for both modes.

## Lifetime contract / use-after-free

Import is **zero-copy**, so a flat `ColumnVector` — and every `ReadOnlySpan<T>`, `Slice`, or selection
derived from it — reads straight through to the source `RecordBatch`'s Arrow buffers. Those buffers are
freed when the owning lifetime ends:

| mode | what frees the source buffers |
| --- | --- |
| `Transfer` | disposing the `ArrowColumnBatch` (the **sharp** mode — the batch owns the source) |
| `Borrowed` | the caller disposing the source (disposing the batch frees nothing) |

A view vended from the batch **must not outlive that lifetime**. Reading a `ColumnVector`/span/slice
after the batch (`Transfer`) or the source (`Borrowed`) is disposed reads freed memory and is
**undefined behavior** — a use-after-free that may throw a native `NullReferenceException`, silently
return recycled bytes (a future cross-tenant disclosure primitive once buffers are pooled/native), or
crash.

The batch-level `ObjectDisposedException` on `Column`/`Slice`/`WithSelection` is **disposal hygiene on
those accessors only**: it does **not** invalidate a column already vended, and is therefore **not** a
memory-safety boundary. The honest contract is *"do not let a vended view outlive its batch (`Transfer`)
or source (`Borrowed`)"*; to keep values longer, **materialize** (copy) them out first. The XML docs on
`FromArrow`, `ArrowColumnBatch.Column`/`Slice`/`WithSelection`, and `ArrowImportOwnership.Transfer` carry
the same warning. A contract test pins the **safe** half — a `Borrowed` vended `ColumnVector` reads
correctly while the source is alive, and the batch-level accessors throw `ObjectDisposedException` after
dispose — and documents (but does **not** execute) the use-after-free half so the rule is recorded
without invoking undefined behavior.

## Residual caveats

- **Offset reset** for materialized `boolean`/`decimal128` columns (above) — logical order/validity
  preserved, physical offset is `0`.
- **Nested columns are opaque**: `ArrowNestedColumnVector` exposes length/offset/validity but its
  scalar accessors (`GetValues`/`GetBytes`) throw — the v1 contract has no child-vector accessor.
  Nested kernels are deferred (FEAT-02.3); v1 only needs to **carry** nested columns through
  projection/shuffle/Flight and round-trip them, which the pass-through does.
- **Selection over a nested column can't be exported** (`ToArrow` throws `NotSupportedException`):
  there is no logical re-indexing of an opaque Arrow array in v1 — materialize the selection first.
  Flat columns apply any selection on export (rows emitted in selection order).
- **Timestamp**: only microsecond precision maps; the timezone is not modeled by the v1
  `TimestampType` (a UTC-normalized instant), so export normalizes the Arrow zone to `"UTC"` and the
  source zone string is dropped.
- **`NullArray`** is unsupported on purpose (the reader can't materialize an all-null typeless column
  consistently); convert to a typed all-null column before importing.
- **Field and schema metadata are dropped in both directions**: v1 maps only name / type / nullability,
  so Arrow field metadata and schema-level metadata — Delta column-mapping IDs, Spark field comments — do
  **not** survive `FromArrow`/`ToArrow`. (A test pins this deliberate drop so it can't silently change;
  `StructField.Equals` includes metadata, so round-tripping it would otherwise be required for schema
  identity.)
