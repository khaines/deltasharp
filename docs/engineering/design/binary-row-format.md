# Binary row format (v1)

> **Status:** living document. Created with
> [STORY-02.4.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-02-columnar-memory-type-system.md#story-0241-define-and-implement-binary-row-layout).
> Grounded in [ADR-0008](../../adr/0008-type-system-row-format.md) (the `UnsafeRow`-analog binary
> row), [ADR-0013](../../adr/0013-memory-model.md) (off-heap `NativeMemory`/`OwnedBuffer`), and
> [ADR-0002](../../adr/0002-columnar-batch-format.md) (the row⇄columnar hybrid). It binds the
> [type system](type-system.md) physical-layout seam and the [memory model](memory-model.md)
> ownership contract. Update it whenever the row layout, slot encoding, or nested subset changes.

The DeltaSharp **binary row** is the compact, off-heap counterpart to the columnar `ColumnBatch`
(ADR-0008's *row + columnar hybrid*). It is the `UnsafeRow` analog Spark uses for shuffle keys,
sort/join keys, and materialized result rows: a single 8-byte-aligned byte block carrying one row,
backed by an `OwnedBuffer` so it serializes to shuffle/spill with **zero per-field allocation**.
It lives in the unshipped `DeltaSharp.Engine` assembly under `src/DeltaSharp.Engine/RowFormat/`;
`public` here is an engine-internal seam, never a shipped NuGet surface.

This story delivers v1: the layout geometry, encode/decode for every fixed- and variable-width
type, the nested `array`/`map`/`struct` subset, and explicit off-heap ownership. Byte-sortable
ordering and standalone spill serialization are STORY-02.4.2.

## Block layout

A row — and every nested struct, array, and map — is a self-contained **block**:

```
┌───────────────┬───────────────────────────┬──────────────────────────┐
│ null bitset   │ fixed region              │ variable region          │
│ ceil(N/64)·8B │ N · 8-byte slots          │ 8-byte-padded payloads   │
└───────────────┴───────────────────────────┴──────────────────────────┘
```

- **Null bitset** — one bit per field, word-aligned to `ceil(N/64)·8` bytes; a **set** bit means
  the field is **null** (the inverse of the columnar validity convention, which the row layer does
  not share). Word-rounding makes the fixed region start 8-byte aligned.
- **Fixed region** — exactly one **8-byte slot** per field (`N·8`, already 8-aligned). A ≤8-byte
  fixed-width value sits inline; everything else stores a packed reference into the variable region.
- **Variable region** — each variable payload is appended and padded up to an 8-byte boundary.

Because the bitset is a multiple of 8, the fixed region is `N·8`, and each variable payload is
8-padded, the **total block size is always 8-byte aligned** (AC1) — for fixed-width-only,
variable-width, and large-decimal schemas alike.

## Slot encoding

Each field's 8-byte slot holds either an inline value or a packed reference
`(offset << 32) | length` where `offset` is **relative to the start of the enclosing block**.

| Type | Slot | Notes |
| --- | --- | --- |
| `boolean` | inline, 1 byte | `0`/`1` |
| `byte` (signed) | inline, 1 byte | |
| `short` | inline, 2 bytes | little-endian |
| `int`, `date` | inline, 4 bytes | date = epoch days |
| `long`, `timestamp` | inline, 8 bytes | timestamp = epoch micros |
| `float`, `double` | inline, 4/8 bytes | IEEE-754 LE |
| `decimal` (`p ≤ 18`) | inline, 8-byte unscaled long | compact |
| `decimal` (`p > 18`) | ref → 16-byte unscaled `Int128` | variable region |
| `string` | ref → UTF-8 bytes | |
| `binary` | ref → bytes | |
| `array`/`map`/`struct` | ref → nested block | recursion |
| `void` | — | no physical layout; `RowLayout` rejects it |

All multi-byte fields are **little-endian** (`BinaryPrimitives`) for cross-platform determinism. A
null field's slot is left zero. Offsets/lengths are 32-bit, capping a single row at the buffer's
`int.MaxValue` ceiling (memory-model.md), which matches the batch-granularity intent.

## Nested subset (AC3)

- **struct** is a nested block, byte-for-byte the row layout — order and per-field nulls preserved.
- **array** is `[8B count][null bitset][count 8-byte slots][variable region]`; slots use the same
  encoding so element order and nested nulls survive.
- **map** is `[8B keys-block size][keys array][values array]` — two equal-length arrays, so
  `key[i]→value[i]` pairing and order are exact. Keys are non-null; values follow `valueContainsNull`.

Empty and null collections round-trip: a null nested field sets its bit; an empty collection encodes
a count-0 block. The `RowData`/`ArrayData`/`MapData` value model is what tests assert structural
equality on.

## Off-heap ownership (AC4)

`BinaryRowEncoder.Encode` builds the block, then copies it into an `OwnedBuffer` from a
`NativeMemoryAllocator` (off-heap above the scratch threshold). `BinaryRow` owns that buffer and is
disposed exactly once; the underlying `OwnedBuffer` already releases at most once, so **double-free
is a safe no-op** and the allocator's live counters never go negative. `TransferBuffer()` hands the
buffer to a shuffle/spill owner and nulls the row, making the row's later `Dispose()` a no-op — so
**disposal responsibility is always exactly one party's**, and the double-free tests pass on both
the row and the transferred buffer.

## v1 scope and deferrals

- **In scope:** the 8-aligned layout; encode/decode for all v1 scalar/decimal types; the nested
  array/map/struct subset; off-heap buffers with explicit, double-free-safe disposal and transfer.
- **Deferred to STORY-02.4.2:** byte-sortable ordering, schema-version metadata, standalone spill
  serialization, and bounded validation of malformed/truncated bytes.

## References

- [ADR-0008: Type system and internal row/value representation](../../adr/0008-type-system-row-format.md) ·
  [ADR-0013: Memory model](../../adr/0013-memory-model.md) ·
  [ADR-0002: Columnar batch format](../../adr/0002-columnar-batch-format.md)
- [Type system](type-system.md) · [Memory model](memory-model.md) · [Testing conventions](testing-conventions.md)
- Apache Spark `UnsafeRow`/`UnsafeArrayData`/`UnsafeMapData`.
