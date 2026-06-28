# Columnar vector & batch contracts (v1)

> **Status:** living document. Created with
> [STORY-02.1.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-02-columnar-memory-type-system.md#story-0211-define-vector-and-batch-contracts).
> Grounded in [ADR-0002](../../adr/0002-columnar-batch-format.md) (mutable, selection-vector-aware
> `ColumnBatch`/`ColumnVector`; operators must not bind to `Apache.Arrow`),
> [ADR-0013](../../adr/0013-memory-model.md) (off-heap memory, relevant later), and the
> [type system](type-system.md) (the logical `DataType` seam). Update it whenever the vector/batch
> contracts change.

DeltaSharp's vectorized operators and kernels bind to a small, stable **columnar contract** —
`ColumnVector` and `ColumnBatch` — rather than to any storage backend. Per ADR-0002, the same
contract is satisfied by an Arrow-backed implementation (STORY-02.2.1, for cheap Parquet/Flight
interop) and, later, a custom off-heap implementation for hot operators (STORY-02.3.1), so kernels
never have to change and **no operator-facing member names an `Apache.Arrow` type**.

These contracts live in the unshipped `DeltaSharp.Engine` assembly under
`src/DeltaSharp.Engine/Columnar/`; `public` is an engine-internal seam, not a shipped surface
(see [testing-conventions.md](testing-conventions.md)).

## The contract surface

| Type | Role |
| --- | --- |
| `ColumnVector` | Read contract for one column: type, length, offset, validity, typed value access, slicing. |
| `MutableColumnVector` | Write contract: append/set values and null bits; reads observe the writes. |
| `ColumnBatch` | A schema (`StructType`) + one equal-length vector per field; slicing; selection awareness. |
| `SelectionVector` | An ordered set of selected physical row indices (late materialization); composable. |
| `SelectedColumnVector` | A zero-copy view that re-maps a vector's logical rows to a selection's physical rows. |

### Logical indexing and offset

All indices are **logical**: row `0` is the first row of a vector's view, regardless of any
physical `Offset` into shared buffers. A `Slice(offset, length)` returns a view over the **same**
buffers — no value or validity bytes are copied — with a consistent `Length`, `Offset`, and
validity at each corresponding row (AC2). Because a slice (or a selection view) shares the owner's
buffers, **slicing or selecting seals the owner**: a mutable vector is built fully, then
sliced/selected/read, and any attempt to mutate it afterward throws (rather than silently corrupting
outstanding views via a stale null count or an append-triggered buffer reallocation). Typed bulk access (`GetValues<T>()`) returns the
offset-adjusted span for the slice's own `[0, Length)`, so kernels never do offset arithmetic.

### Typed access without per-row boxing (AC1)

The hot-path accessor is `ReadOnlySpan<T> GetValues<T>()`, fetched once per vector and iterated
with no boxing or per-row virtual dispatch (a regression test asserts the read loop allocates ~0
bytes). `T` is the natural CLR storage type of the logical type (`int` for `integer`/`date`, `long`
for `long`/`timestamp`/compact `decimal`, `Int128` for full `decimal`, …); a mismatched `T` throws.
Variable-width values (`string` UTF-8, `binary`) use `ReadOnlySpan<byte> GetBytes(int)`.

### Nullability / validity

Validity is exposed representation-agnostically: `IsNull(int)`, `HasNulls`, `NullCount`. The
reference implementation stores an **Arrow-compatible LSB-first validity bitmap** (set bit =
valid), so null positions and slicing offsets already match the Arrow ordering the Arrow-backed
vector (STORY-02.2.1) will use. Branchless/SIMD null helpers are a later concern (FEAT-02.6).

### Mutable output (AC3)

An operator materializes output through `MutableColumnVector` — `AppendValue<T>`, `AppendBytes`,
`AppendNull`, `SetValue<T>`, `SetNull`, `Clear` — and the written values and null bits are
immediately observable through the inherited read members, **with no immutable Arrow API on the
hot path**. Slices are read-only views and reject mutation.

### Selection-vector awareness and zero-copy selected views

A `ColumnVector` exposes `Select(SelectionVector)` and a `ColumnBatch` exposes `WithSelection`. Both
return a **zero-copy selected view**: the logical rows become the selection's physical rows in
selection order, so `Length`/`LogicalRowCount` equal the selected cardinality while every value and
validity buffer is shared, never copied (STORY-02.1.2 AC1). A selecting allocation is just the view
object plus the index array — independent of the parent's value-buffer size, which a regression test
pins. `ColumnBatch.SelectedColumn(i)` returns column `i` re-based to those selected rows (or the
column itself when no selection is present), so kernels enumerate `[0, LogicalRowCount)` with no
selection bookkeeping.

A selected row `i` resolves to physical index `selection[i]`: `GetValue<T>(i)`, `GetBytes(i)`, and
`IsNull(i)` gather from the parent at that index, so **validity matches the parent's bitmap at each
selected physical index** and `NullCount`/`HasNulls` reflect only the selected rows (AC4). Selection
is **non-contiguous**, so `GetValues<T>()` (the contiguous fast path) is unavailable on a view —
kernels enumerate per-row or materialize first. Empty / all-selected / partial selections enumerate
in deterministic selection order and never read out of range (AC3).

**Composition:** `Select` over an already-selected view (and `WithSelection` over a selected batch)
fuses via `SelectionVector.Compose` — the outer indices address the current logical rows and resolve
through to physical rows, so nesting never copies and never deepens: `v.Select(a).Select(b)` equals
`v.Select(a.Compose(b))` (AC2). **Seal interaction:** like `Slice`, taking a selected view seals a
mutable owner (the same `seal-on-slice` safety from STORY-02.1.1), so the shared buffers can't be
mutated underneath the view; build a vector fully, then select/slice it.

## Reference implementation (the AC4 proof)

`ManagedFixedWidthColumnVector<T>`, `ManagedVariableWidthColumnVector`, and `ManagedColumnBatch`
are a managed, GC-heap implementation of the contracts, obtained via the `ColumnVectors.Create`
factory. They are the **correctness reference** and a concrete **non-Arrow** implementation — they
prove AC4 (the contracts are fully implementable with no `Apache.Arrow` dependency; the engine
assembly references no Arrow package). They are intentionally simple; the performant Arrow-backed
and off-heap vectors are separate stories.

## v1 scope and deferrals

- **In scope:** the `ColumnVector`/`MutableColumnVector`/`ColumnBatch`/`SelectionVector` contracts,
  typed no-boxing access for the v1 primitives, the validity model, slicing, selection awareness,
  zero-copy selection **views** + composition (STORY-02.1.2, #134), and a managed reference
  implementation.
- **Deferred:** Arrow-backed vectors + boundary conversion (FEAT-02.2, #135/#136); off-heap
  `NativeMemory` vectors + spill (FEAT-02.3, ADR-0013); branchless null/validity helpers
  (FEAT-02.6, #143/#144); first-class **nested** (`array`/`map`/`struct`) child vectors — the
  contracts classify them as `Nested` and the managed factory does not yet build them.

## References

- [ADR-0002: In-memory columnar batch format](../../adr/0002-columnar-batch-format.md)
- [ADR-0013: Memory model for in-memory batches](../../adr/0013-memory-model.md)
- [Type system & schema model](type-system.md) · [Testing conventions](testing-conventions.md)
- [EPIC-02: Columnar Memory & Type System](../../planning/epics/EPIC-02-columnar-memory-type-system.md)
- Apache Spark `ColumnVector`/`ColumnarBatch`; Apache Arrow validity bitmaps; Velox/DuckDB vectors.
