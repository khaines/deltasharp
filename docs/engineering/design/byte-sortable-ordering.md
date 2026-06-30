# Byte-sortable row ordering and serialization (v1)

> **Status:** living document. Created with
> [STORY-02.4.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-02-columnar-memory-type-system.md).
> Builds directly on the [binary row format](binary-row-format.md) (STORY-02.4.1) and is grounded in
> [ADR-0008](../../adr/0008-type-system-row-format.md) (the `UnsafeRow`-analog binary row is
> *byte-sortable* and *shuffle/spill serializable*), [ADR-0013](../../adr/0013-memory-model.md)
> (off-heap `OwnedBuffer`s), and the [type system](type-system.md) ordering semantics. Update it
> whenever the key encoding, frame format, or validation rules change.

DeltaSharp's sort, join, spill, and shuffle stages all need to order and move *rows*. STORY-02.4.1
gave us the compact, 8-byte-aligned [binary row](binary-row-format.md). This story makes that row
representation **byte-sortable** and **standalone-serializable** so a single row form serves every
one of those stages:

- **Byte-sortable sort keys** — an order-preserving encoding of a row's key columns such that a raw
  `memcmp` (`ReadOnlySpan<byte>.SequenceCompareTo`) of two encodings reproduces the configured Spark
  ordering. Sort and merge can then compare keys without re-interpreting types, and shuffle can
  range-partition on the same bytes.
- **Spill/shuffle frames** — a self-describing byte frame (schema-version metadata + length +
  payload) that round-trips a row through disk or the network and is validated on the way back in,
  with a bounded, typed error and no out-of-bounds reads.

These are two distinct mechanisms with two distinct byte layouts. The sort key is a *comparison*
artifact (never decoded back to values); the frame is a *transport* artifact (decoded back to exact
values). This document specifies both. Everything lives in the unshipped `DeltaSharp.Engine`
assembly under `src/DeltaSharp.Engine/RowFormat/`; `public` here is an engine-internal seam, never a
shipped NuGet surface.

## Where the ordering knobs live

A sort key field is ordered by a [`SortKeyOrdering`](../../../src/DeltaSharp.Engine/RowFormat/SortKeyOrdering.cs):
a `SortKeyDirection` (`Ascending`/`Descending`) and a `NullSortOrder` (`NullsFirst`/`NullsLast`),
mirroring Spark's `ASC`/`DESC` and `NULLS FIRST`/`NULLS LAST`. The Spark defaults (`ASC ⇒ NULLS
FIRST`, `DESC ⇒ NULLS LAST`) are available via `SortKeyOrdering.ForDirection`, and either can be
overridden.

These ordering primitives are owned by the **row-format layer**, not the execution layer. The binary
row is the foundation; the query-execution `SortOrder`/`SortDirection`/`NullOrdering` (in
`Execution/`) map *onto* `SortKeyOrdering` at the operator boundary. Keeping the primitive here keeps
the row format self-contained and avoids a layering inversion — `Execution` already documents that
sort is "implemented over EPIC-02 binary sort keys".

## Byte-sortable sort key encoding (AC1)

### Per-field structure

The key for a row is the concatenation, in key-priority order, of one segment per key field:

```
field segment := null-marker (1 byte) [ value-bytes (present only) ]
```

`memcmp` walks the concatenation left to right, so the first field that differs decides the order —
exactly lexicographic-by-field-priority comparison. Two properties make this sound:

1. **The null marker disambiguates nullness before any value bytes are compared.** A null segment is
   just its marker; a present segment is its marker followed by value bytes. Because the marker
   differs whenever nullness differs, a null is never compared byte-for-byte against a present
   value's bytes.
2. **Each present field's value bytes are self-delimiting.** Fixed-width encodings are constant
   length; variable-width encodings are terminated (below). So when two present values are equal,
   their value bytes are byte-identical and the comparison advances to the next field in lockstep;
   when they differ, the difference resolves inside the field.

### Null marker and placement

The null marker encodes placement independently of direction (Spark allows `DESC NULLS FIRST`):

| Case | `NullsFirst` marker | `NullsLast` marker |
| --- | --- | --- |
| value is `NULL` | `0x00` | `0x02` |
| value is present | `0x01` | `0x01` |

A present value always carries marker `0x01`. With `NULLS FIRST`, `0x00 < 0x01` sorts nulls before
every non-null; with `NULLS LAST`, `0x01 < 0x02` sorts them after. The marker is **never**
complemented for descending, so null placement is absolute.

### Direction

The encoder always produces the **ascending** value bytes, then complements them (bitwise NOT of
every value byte) for a descending field. Complement is an order-reversing bijection on bytes, so it
reverses `memcmp` order — provided no encoding is a strict prefix of another (otherwise the shorter
"prefix is less" rule would *not* reverse). Fixed-width encodings are all the same length, and the
variable-width terminator guarantees the no-prefix property (below), so the complement is an exact
reversal in every case.

### Per-type encoding

Every encoding is written **big-endian** so that `memcmp` (most-significant byte first) matches
numeric magnitude. (The binary row stores values little-endian for cheap native loads; the sort key
is the one place we deliberately use big-endian.)

| Type | Ascending value bytes | Rationale |
| --- | --- | --- |
| `boolean` | 1 byte, `0x00`/`0x01` | `false < true` (Spark) |
| `byte`,`short`,`int`,`long` | flip the sign bit, big-endian | maps two's-complement onto unsigned order: `MinValue → 0x00…`, `0 → 0x80…`, `MaxValue → 0xFF…` |
| `date` | as `int` (epoch days) | integer order = chronological order |
| `timestamp` | as `long` (epoch micros) | integer order = chronological order |
| `decimal` | unscaled value as `Int128`, sign-bit flipped, big-endian (16 bytes) | one `DecimalType` per key column ⇒ unscaled values are directly comparable |
| `float`,`double` | IEEE-754 *total-order* transform, big-endian | see below |
| `string` | UTF-8 bytes, escaped + terminated | UTF-8 byte order = code-point order = Spark `UTF8String` order |
| `binary` | raw bytes, escaped + terminated | unsigned lexicographic |

#### Signed integers — worked example

`int 0` encodes (with present marker) to `01 80 00 00 00`; `int -1` to `01 7F FF FF FF`. Flipping the
sign bit (`x ^ 0x80000000`) makes `-1` (`0x7FFFFFFF`) sort just below `0` (`0x80000000`), and
`int.MinValue` (`0x00000000`) the smallest — the signed order under an unsigned `memcmp`.

#### Floating point — NaN and −0.0

`float`/`double` use the standard total-ordering transform with two canonicalizations that match
Spark (SPARK-26021):

1. **−0.0 is canonicalized to +0.0** before encoding, so the two zeros encode to identical bytes and
   compare equal.
2. **Every NaN bit pattern (quiet, signaling, sign-bit set, any payload) is canonicalized to one
   quiet-NaN pattern**, so all NaNs compare equal.

The transform on the 64-bit pattern `u`:

```
if value is NaN:  u = 0x7FF8000000000000      // canonical quiet NaN (positive)
elif value == 0:  u = 0                        // +0, also catches −0
mask = (u >> 63 arithmetic) | 0x8000000000000000
u ^= mask                                       // negative ⇒ flip all bits, positive ⇒ flip sign bit
```

After the transform, the most-negative double encodes smallest and the canonical NaN encodes
**largest — greater than +∞** — so NaN sorts greatest, exactly as Spark's `nanSafeCompare` orders it.
`float` uses the 32-bit analog (`0x7FC00000` canonical NaN, shift 31).

#### Strings and binary — escape + terminator

Variable-length values must stay correctly ordered for **multi-column keys** (where another field
follows) and under **descending complement**. We use the classic order-preserving, prefix-free
encoding:

- escape every `0x00` byte as `0x00 0xFF`, then
- append a `0x00 0x00` terminator.

The terminator is the minimum two-byte sequence and, because of the escape, cannot occur inside the
body — so no encoded value is a strict prefix of another. That gives two guarantees at once:

- **Ascending order is exact lexicographic order.** The per-byte codes (`00 00` terminator `<`
  `00 FF` for an embedded NUL `<` `01` `< … <` `FF`) form a prefix code whose order matches
  end-of-string `<` `0x00` `< … <` `0xFF`, so `memcmp` of the codes equals lexicographic order of the
  raw bytes (verified by tests over `""`, `"\u0000"`, `"\u0000\u0000"`, `"a"`, `"a\u0000"`, `"ab"`,
  `"b"`).
- **Descending complement is an exact reversal.** With no strict-prefix relationship, the first
  differing byte always exists within both encodings, and complement flips that comparison.

### Spark parity and deviations

The ordering matches Spark for all supported types: signed-integer order, `false < true`, NaN
greatest, `−0.0 == +0.0`, decimal-by-value, chronological temporal order, and UTF-8/binary
lexicographic order, each under the field's `ASC`/`DESC` and `NULLS FIRST`/`NULLS LAST`.

**Deviation / v1 scope:** only atomic and `decimal` types are byte-sortable keys
(`boolean, byte, short, int, long, float, double, date, timestamp, decimal, string, binary`).
Ordering by `array`/`map`/`struct` (which Spark supports lexicographically/structurally) and by the
`void` type is **deferred**; the encoder rejects them at construction with a typed
`RowFormatException`. This covers the v1 sort/join/shuffle key needs; nested-key ordering is a
follow-up.

### Allocation and the hot path

Encoding is `Span<byte>`-based and allocation-free:
`SortKeyEncoder.GetMaxEncodedLength(row)` sizes a caller-owned (stack or pooled) buffer cheaply
(UTF-8 byte *counts*, no encoding), and `Encode(row, Span<byte>)` writes straight into it, returning
the exact length. String UTF-8 conversion uses a stack scratch buffer (≤ 256 bytes) or a pooled
rental — no GC allocation on the encode path. Comparison is then a single
`SequenceCompareTo` (`memcmp`). A convenience `Encode(row) → byte[]` overload exists for tests and
cold paths.

## Scalar comparator oracle (AC2)

[`RowOrderingComparer`](../../../src/DeltaSharp.Engine/RowFormat/RowOrderingComparer.cs) is the
**correctness reference**: an `IComparer<RowData>` that compares rows field-by-field with ordinary
typed comparisons and the same Spark rules — nulls placed per `NullSortOrder` (independent of
direction), the value comparison negated for descending, `−0.0 == +0.0`, `NaN == NaN` and greatest,
and strings/binary compared as unsigned UTF-8/byte sequences (UTF-8, not .NET UTF-16 ordinal, so it
agrees with the encoder and Spark on supplementary characters).

**Parity is the contract:** for any two rows, the sign of `memcmp(encode(a), encode(b))` must equal
the sign of `comparer.Compare(a, b)`. The tests prove this two ways over rows packed with decimals,
timestamps, NaN (multiple bit patterns), −0.0, negatives, extremes, and nulls in every position,
across four direction/null-placement configurations:

- **pairwise** — every ordered pair compares to the same sign, and
- **whole-list** — a stable sort under each method yields the identical row order.

The comparator stays the oracle; if the byte encoding and the comparator ever disagree, the encoding
is wrong by definition.

## Spill/shuffle frame serialization (AC3)

A row is moved across disk or the network as a self-describing **frame**: a fixed 16-byte header
followed by the row's binary-row payload.

```
┌────────┬─────────┬──────────┬───────────────┬──────────────┬───────────────────────┐
│ offset │ 0       │ 4        │ 6             │ 8            │ 12            │ 16     │
│ field  │ magic   │ version  │ reserved (0)  │ schema FP    │ payload len   │ payload│
│ size   │ 4 B     │ 2 B (LE) │ 2 B           │ 4 B (LE)     │ 4 B (LE)      │ N B    │
└────────┴─────────┴──────────┴───────────────┴──────────────┴───────────────────────┘
```

- **magic** `"DSR1"` is a **fixed brand tag** that identifies a DeltaSharp row frame. The trailing
  `1` is part of the brand, **not** a version counter: the magic is *never* bumped on a format change.
  Format evolution happens **only** through the `version` field (below). If a future format instead
  changed the magic, a v1 reader would misdiagnose a newer frame as foreign bytes (`invalid magic`)
  rather than reporting the more useful `unsupported version`, and back-compat tooling could no longer
  recognize the frame family at all.
- **version** is the frame format version and the **sole** compatible-evolution mechanism: a reader
  that knows version *v* rejects *v+1* with a typed `unsupported frame format version` error. New
  fields are added by spending `reserved` (below) and bumping `version`, leaving the magic constant.
- **schema fingerprint** is a 32-bit, deterministic, process-independent hash of the schema
  (`StructType.GetHashCode()`, FNV-1a based) — it is **not** the schema itself. The frame does **not**
  carry the schema; the reader must supply the *expected* schema out-of-band (planner-controlled in
  v1, where both ends of a shuffle/spill agree on the plan). The fingerprint is only a cheap
  **sanity / schema-version check** — a guard against accidentally reading a frame with the wrong
  schema — and is explicitly **not** tamper-evidence: a 32-bit non-cryptographic hash is trivial to
  forge or collide. The real integrity defense is the structural validation in
  [AC4](#bounded-validation-ac4), which range-checks every byte regardless of the fingerprint.
- **payload length** bounds the row block that follows; the header is 16 bytes (8-aligned) and the
  payload is already 8-aligned, so the frame stays aligned.
- **reserved** is two zero bytes today; readers **ignore** it, so a later format can spend it for a
  small additive field (with a `version` bump) and stay forward-compatible with this layout.

### Frame scope: one row per frame

A frame wraps **exactly one row**. There is deliberately **no** block envelope here — no row count,
no total-length prefix, and no checksum spanning multiple rows. `ReadFrame` returns the bytes it
consumed so a caller can walk back-to-back frames out of a spill/shuffle segment, but the framing
*of* that segment — a block header (row count / total byte length), batching, and any integrity /
bit-rot detection (e.g. a CRC) — belongs to the **shuffle block format** ([#245](https://github.com/khaines/deltasharp/issues/245)),
not to the per-row frame. Keeping the per-row frame minimal avoids paying envelope/checksum overhead
per row and keeps the two concerns (one row's bytes vs. a block of many rows) cleanly separated.

`WriteFrame(row, Span<byte>)` writes with no allocation; `ReadFrame(source, schema, out consumed)`
validates and decodes, returning the exact bytes consumed so frames can be read back-to-back from a
spill segment. A round-trip reproduces identical values, nulls, and the schema-version metadata
(`RowFrameHeader`). Actual file/stream I/O belongs to the execution/memory layers; this type owns
only the byte frame.

## Bounded validation (AC4)

Spill and shuffle bytes are **untrusted** — they come from disk or another process and may be
truncated or corrupt. `ReadFrame` therefore validates before it trusts anything, and the guarantee
is: **a malformed or truncated frame fails with a bounded, typed
[`RowValidationException`](../../../src/DeltaSharp.Engine/RowFormat/RowValidationException.cs) and
never reads outside the buffer.**

The header is checked field by field (length ≥ 16, magic, supported version, non-negative payload
length, payload length within the buffer, fingerprint matches the expected schema). The payload is
then handed to
[`BinaryRowValidator`](../../../src/DeltaSharp.Engine/RowFormat/BinaryRowValidator.cs), which walks
the block structure with explicit in-bounds tests **before any read**:

- the struct/array/map header must fit the block;
- every variable reference `(offset, length)` is checked `offset ≥ 0 && length ≥ 0 && offset + length
  ≤ block.Length` (in 64-bit arithmetic, so it cannot overflow) before the payload is sliced;
- a `decimal` reference must be exactly 16 bytes;
- an array/map **element count comes from the bytes but is bounded against the block length** before
  any size is computed (each element needs an 8-byte slot), so the bitset/slot math neither overflows
  nor loops past the buffer;
- a **map** is validated as more than two independent arrays. After bounds-checking the key and value
  arrays, the validator enforces the two cross-array invariants the decoder and `MapData` require:
  the **key count must equal the value count** (the decoder pairs `keys[i]` with `values[i]` in
  lockstep, so a mismatch would otherwise surface as an `IndexOutOfRangeException` at decode time), and
  **every map key must be non-null** (a set bit in the key array's null bitset would otherwise reach
  `MapData`'s constructor as an `ArgumentException`). Both are reported as the same typed
  `RowValidationException` — with integers and type names only, never raw payload bytes — so a crafted
  or corrupt map frame cannot escape the contract by raising a different exception type from inside the
  decoder.

Two structural facts keep this safe and terminating:

- **Recursion is schema-driven, not byte-driven.** Nesting follows the *trusted* schema's type tree,
  so an adversarial buffer cannot force unbounded recursion — depth is bounded by the schema.
- **Work is linear in the buffer.** Element counts are capped at `block.Length / 8`, so a giant count
  is rejected rather than acted upon.

Only after validation succeeds is the (now-proven-in-bounds) payload decoded by the existing
`RowDecoder`, so decoding cannot read past the payload. `RowValidationException` derives from
`RowFormatException`, so existing `catch (RowFormatException)` sites still observe it while new code
can catch the untrusted-input case specifically.

The tests assert this exhaustively: every header/structure corruption throws `RowValidationException`
(including a map whose key/value counts disagree — which must surface as a typed validation error,
not an `IndexOutOfRangeException` — and a map with a null-key bit set, which must not reach
`MapData`'s `ArgumentException`); **truncating a valid frame at every length** `0 … N−1` throws (and
only the full frame decodes); and a **multi-schema fuzz** only ever produces a clean decode or a
`RowValidationException` — never an out-of-bounds, overflow, `ArgumentException`, or other exception.
The fuzz runs three batches: random bytes against a flat `(int, string)` schema and against a nested
`map`/`array`/`struct` schema (half of each seeded with a valid magic/version/fingerprint so the deep
structural checks are reached), plus byte-level corruptions of a *valid* nested frame (which stay
close enough to valid to drive the map pairing/null-key, array-count, and recursion checks that random
noise reaches only by accident). All fuzz/test data uses a small deterministic LCG rather than
`System.Random`, so failures are reproducible.

## Runtime / performance notes

- **Determinism.** All sort-key and frame multi-byte fields use `BinaryPrimitives` with an explicit
  endianness, so encodings are identical across platforms — a hard requirement for shuffle and for
  reproducible spill segments. The schema fingerprint uses the type system's process-stable FNV-1a
  hash, not the randomized CLR string hash.
- **No banned APIs.** No clocks, RNG, reflection, or `Expression.Compile` — the encode/compare/serialize
  paths are pure span arithmetic, AOT/trim-clean under the engine's analyzers.
- **Comparison cost.** Once encoded, key comparison is a single `memcmp`; the encoding front-loads
  all the per-type branching so the inner sort/merge loop is branch-light.

## v1 scope and deferrals

- **In scope:** order-preserving byte encoding for all atomic + `decimal` key types under
  `ASC`/`DESC` × `NULLS FIRST`/`NULLS LAST`; the scalar comparator oracle and its parity contract;
  the self-describing **per-row** spill/shuffle frame with schema-version metadata; bounded, typed,
  no-OOB validation of malformed/truncated frames (including the map key/value-pairing and
  non-null-key invariants).
- **Deferred:** byte-sortable ordering by nested `array`/`map`/`struct` keys; a sort *prefix* (a
  packed leading `long` à la Spark's `PrefixComparator` for a fast first-pass compare) on top of the
  full key; collation-aware string ordering (v1 is binary/UTF-8 order, matching Spark's default); the
  **block envelope** that wraps many back-to-back frames (row count / total length / optional CRC) and
  any **integrity / bit-rot detection** — both belong to the shuffle block format
  ([#245](https://github.com/khaines/deltasharp/issues/245)), not the per-row frame.

## References

- [Binary row format](binary-row-format.md) (STORY-02.4.1) · [Type system](type-system.md) ·
  [Memory model](memory-model.md)
- [ADR-0008: Type system and internal row/value representation](../../adr/0008-type-system-row-format.md) ·
  [ADR-0013: Memory model](../../adr/0013-memory-model.md)
- Apache Spark `UnsafeRow`, `RowBasedKeyValueBatch`, `PrefixComparators`, and `SortOrder`
  (`NULLS FIRST`/`LAST`, nan-safe ordering, SPARK-26021 `−0.0`/`NaN` normalization).
