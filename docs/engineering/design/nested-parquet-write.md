# Nested Parquet write (§2.9) — single-level `struct` / `array` / `map` of scalars

> **Status:** Draft (v4 — 6.1.0-viable; council rounds 2-3 findings folded in; all seats 4/5→PASS pending)
> **Issue:** [#828](https://github.com/khaines/deltasharp/issues/828) — feat(storage): nested Parquet write (§2.9)
> **Author:** design (spike-informed; 6.1.0 + RFL-round-2 revision)
> **Reviewers:** delta-storage-format-engineer, dotnet-vectorized-columnar-compute-engineer, dotnet-framework-runtime-engineer, reliability-test-chaos-engineer, cloud-native-security-sme
> **Last Updated:** 2026-08-19
> **Related:** #571/#584 (nested **read**), #713 (footer artifact tests), #676 (nested column mapping), #829 (`BuildFieldIdMap` path-keying — a **prerequisite of #676's** nested id-mode read), #585 (nested-within-nested), #730 (nullability→repetition), #683/#686 (`SimpleString` bounded diagnosis), #497 (write-door schema validation), #662 (CDF EE-08 leaf validation), #813 (required-nested-leaf null reject), #570/#546 (nested vector selection), #442/#443 (streaming sink)

---

## 1 · Overview

DeltaSharp **decodes** nested Parquet (`array<T>`/`map<K,V>`/`struct<…>`) via `EnsureReadSupported` +
`NestedParquetColumnReader` (#571/#584) but **cannot write** it (`CreateField` throws for nested; the writer is
scalar-only). This blocks **#713** (footer fixtures) and **#676** (nested column mapping — no write→read
round-trip). This design adds the **write** half, mirroring the delivered read surface: **single-level nesting
of scalars** — `struct<s₁…sₙ>`, `array<scalar>`, `map<scalar,scalar>`. Nested-within-nested stays rejected
fail-closed → **#585** (a hard, tested boundary, §2.6).

### 1.1 Viability — settled under Parquet.Net 6.1.0

The prior draft was non-viable against 6.0.3 (typed `WriteAsync` inferred only two definition levels/leaf; the
def-level writer was `internal`). **6.1.0 (on `main`, #837) exposes PUBLIC**

```csharp
Task WriteAllPartsAsync<T>(DataField field, ReadOnlyMemory<T> values,
    ReadOnlyMemory<int>? definitionValues, ReadOnlyMemory<int>? repetitionLevels, CancellationToken ct)
    where T : struct   // (Parquet.Net constrains T to a non-nullable value type)
```

It writes present values + **explicit** definition and repetition levels — the full Dremel encoding. Council
round-2 verified (independent 6.1.0 spikes) that the §2.3 level tables round-trip **byte-exact** across every
branch (list/struct/map, present/null-list/empty-list/null-element/null-struct/null-field, required-element,
required-value, decimal/DATE/binary leaves, mixed scalar+nested — `num_rows` correct). So the shredder
**computes** the levels from the internal vector and writes them explicitly; there is no inference and no
fallback.

**The `T : struct` constraint (NEW-1).** `string`/`byte[]` are inadmissible to `WriteAllPartsAsync<T>`, and the
non-generic string/binary overloads infer def levels (the 6.0.3 failure). String/binary leaves are therefore
written via **`WriteAllPartsAsync<ReadOnlyMemory<char>>` / `<ReadOnlyMemory<byte>>`** — verified to round-trip
exactly, the mirror of the reader's own `ReadLeafAsync<ReadOnlyMemory<char>>` path. §2.3b is the normative
value/physical-`T` table. **Containers** carry no settable `field_id` and `Field.IsNullable` has no public
setter in 6.1.0 (both verified) — shaping §2.4a and §2.5.

The correctness contract is a **round-trip oracle against the real #571 reader**, an **explicit level-stream
differential** (§3.1) AND — because `WriteAllPartsAsync` is a raw, unvalidated primitive (§2.3c) — a **production
pre-write level-invariant guard** plus a **post-write footer row-count reconciliation** (§2.4b). Oracle+
differential are test-time; the guards are the production control.

---

## 2 · Logical architecture

Pure storage-format change; no public **behavioral** API and no engine/plan behavior; the internal
`ColumnBatch`/`ColumnVector` semantics are unchanged — the only surface addition is two read-only vector
accessors (`RawElementSpan`/`RawEntrySpan`, §2.3/§9-Decision-6), on `IsPackable=false` Engine surface with no
external SemVer obligation.

### 2.1 Where nested write sits, and the `CreateField` widening ripple (NEW-2)
`CreateField(StructField, honorReferenceNullability)` returns the nested **`Field`** for in-scope shapes; its
return type widens `DataField`→`Field`. **Ripple (all three callers, not just the writer):**
- `ParquetFileWriter.WriteAsync` holds `Field[]` and fans a nested column out to N leaf writes.
- `ParquetFileReader.ValidateFileField` (~line 1390) both assigns to a `DataField` **and** documents that its
  raw `requestedField.DataType.SimpleString` echo is safe *because CreateField rejects nested outright*.
  Preserve a `DataField`-returning **scalar** entry point (`CreateScalarField`) for the read path so that raw-
  echo bound is retained; the nested `CreateField` is used only by the write door. `EnsureScalarReadable`
  already rejects nested before `CreateField` — unaffected.

### 2.2 The internal nested representation (read side) — what the shredder consumes
`StructColumnVector` (children each `Length==parent.Length` + struct validity), `ListColumnVector` (offsets
`Length+1` over `Elements` + validity; null-list≠empty-list), `MapColumnVector` (offsets over `Keys`+`Values`;
keys non-null over the **referenced** range only). The shredder is the inverse of `NestedParquetColumnReader`.

### 2.3 The shredder — `ColumnVector` → (values, def, rep) — NORMATIVE level tables
`internal static class NestedColumnShredder` fills **caller-owned** scratch buffers (no pooled arrays escape a
tuple — BL-10) with, per leaf: present values + explicit def+rep. **Accessor rule (NEW-6/BL-11/BL-12) and
slicing (A1 — the round-3 correction):** resolve the child **once** via `Elements`/`Keys`/`Values` (never the
per-row `ElementsAt`/`KeysAt`/`ValuesAt`, which allocate a `ColumnVector` per row and defeat §4). These
accessors — and `StructColumnVector.Child(ordinal)` — are **already row/element-aligned** to the vector's
`_offset`/`_length` window (`Elements`/`Keys`/`Values` slice `[offsets[_offset], offsets[_offset+_length])`;
`Child` slices `child.Slice(_offset,_length)`). **The shredder therefore does NOT re-rebase them.** What *is*
absolute is the raw `offsets` array; a per-row physical span is `offsets[base+i+1] − offsets[base+i]` where
`base = _offset`, i.e. offset arithmetic subtracts the **element base** `offsets[_offset]` while the child
accessors are used as-is. Walk rows by `IsNull(i)` + the **raw span** (a null row may legitimately carry a
non-zero span — `CopyValidatedOffsets` enforces only monotonicity — so `ElementLength(i)`, which returns 0 for
a null row, and `ElementsAt(i)`, which returns an empty view, **cannot** recover it). **Element counts derive
from the raw offsets** (`offsets[_offset+_length] − offsets[_offset]`), **never** from `Elements.Length`: for a
*mutable* vector `Elements` returns the whole child (which can carry an uncommitted tail before `EndList()`),
so `Elements.Length` over-counts and breaks the packed-values invariant (B-5). No public member exposes a null
row's raw span and `_offsets` is `private` on the Engine-layer vectors (`InternalsVisibleTo` reaches only
`*.Tests`, not `DeltaSharp.Storage`), so this design **adds a public** `(int Start, int Length)
RawElementSpan(int index)` (and `RawEntrySpan` for maps) on `List`/`MapColumnVector`: the **index** is
row-rebased by `_offset`, and **`Start`** is **`Elements`-relative** (element-base-rebased,
`offsets[_offset+index] − offsets[_offset]`) so it indexes the pre-sliced child view directly (B-2). This is
solution-internal surface only — `DeltaSharp.Engine` is `IsPackable=false` and packable `DeltaSharp.Core` does
not reference Engine — so it carries **no external SemVer obligation** (softening §2's "no public API" to "no
public *behavioral* API; two additive read-only vector accessors" — §9 Decision 6). Max depth ≤ 2, so the
tables are small and **normative** (reader thresholds cited):

**`struct<leaf>`** (struct OPTIONAL; struct `maxDef`=1):

| row | leaf def (nullable leaf, maxDef 2) | leaf def (REQUIRED leaf, maxDef 1) |
|---|---|---|
| null struct | 0 | 0 |
| present, null field | 1 | *impossible → rejected §2.4a* |
| present value | 2 | 1 |

**Cross-field agreement (G2):** for an optional struct, **every** child leaf must emit `def < structMaxDef(1)`
at a null-struct row, even across children of different `maxDef` (a REQUIRED child at 0, a nullable child at 0)
— `BuildStructNullMask` fails closed (`CorruptData`) on disagreement. The shredder derives the marker from the
struct validity and stamps it on all children at that row. Struct leaves pass **rep = `null`** (not empty) and
def non-null when `maxDef>0` (G3, the reader's documented API asymmetry).

**`array<element>`** (list OPTIONAL; `containerMaxDef=2`, `emptyContainerDef=1`; leaf maxDef 3 nullable / 2
required):

| row | def | rep(first elem) |
|---|---|---|
| null list | 0 | 0 |
| empty list `[]` | 1 | 0 |
| null element | 2 | 0 or 1 |
| present value | 3 | 0 or 1 |

rep = 0 for a row's first element, 1 for each subsequent; REQUIRED element drops one level (present 2, empty 1,
null-list 0); null-element then impossible (rejected §2.4a).

**`map<key,value>`** (NORMATIVE table, per `ValidateParallelDefinition` R6+R7 and `ValidateParallelRepetition`
— G1). key `maxDef` = value `maxDef` − (value nullable ? 1 : 0); with `valueContainsNull:false` both keys and
values sit at 2:

| row | key def | value def | rep(first entry) |
|---|---|---|---|
| null map | 0 | 0 | 0 |
| empty map `{}` | 1 | 1 | 0 |
| present entry, value null | 2 | 2 | 0 or 1 |
| present entry, value present | 2 | 3 | 0 or 1 |

Key and value **def streams must be equal for the null/empty/absent slots** (R7) and the **rep streams
identical** (`ValidateParallelRepetition`); keys are `REQUIRED` and never carry a null **value** — the
null/empty-map key slot is a structural-absence level (`def < keyMaxDef`), never a null key. Multi-entry rows
exercise the rep>0 continuation.

### 2.3b Value / physical-`T` mapping (NORMATIVE — NEW-1, Quality N1)
Mirrors the reader's `ReadScalarLeafAsync` dispatch; the shredder emits present values as physical `T`:

| DeltaSharp `DataType` | physical `T` for `WriteAllPartsAsync<T>` |
|---|---|
| Boolean | `bool` |
| Byte | `sbyte` (append via `unchecked((byte)…)` on read — the writer emits the same `sbyte` the reader expects; test 0/127/128/255, H1-b) |
| Short/Integer/Long | `short`/`int`/`long` |
| Float/Double | `float`/`double` |
| String | `ReadOnlyMemory<char>` |
| Binary | `ReadOnlyMemory<byte>` |
| Date | `DateTime` (epoch-day) |
| Timestamp / TimestampNtz | `DateTime` (epoch-micros) |
| Decimal | `decimal` |

**Packed-values invariant (NEW-1):** `values.Length == count(def == leafMaxDef)` exactly — values are
front-filled, defined-only (an off-by-one shifts every downstream value). **String/binary are NOT zero-copy
(Balanced N-2):** the managed vectors store `String` as **UTF-8** and expose `ReadOnlySpan<byte>` (`GetBytes`),
so the shredder **transcodes UTF-8→UTF-16** into a per-leaf, per-row-group pooled `char[]` scratch
(`Encoding.UTF8.GetChars`) and hands `ReadOnlyMemory<char>` **views** into it (binary similarly copies into a
pooled `byte[]`). The scratch is **sized exactly up front and never grown mid-leaf** (an `Array.Resize`/re-rent
partway through would strand already-handed-out `ReadOnlyMemory<char>` views in the abandoned array → silent
garbage; the cheap upper bound is Σ present-values' UTF-8 byte length, since UTF-16 char count ≤ UTF-8 byte
count — no decode pre-pass, B-3). It is rented under `try/finally` and returned only **after** the awaited
`WriteAllPartsAsync` (which the `await`+`finally` already guarantees — so pooling is safe; N-3), with
`clearArray:true` **mandatory** for the reference-bearing `ArrayPool<ReadOnlyMemory<char>>` element array (GC
retention of foreign buffers), and no `ReadOnlyMemory` is ever handed over a buffer returned in a prior
iteration. §4's cost model reflects the transcode; note this lane is **strictly cheaper** than today's scalar
path (`ParquetFileWriter` does one `Encoding.UTF8.GetString` `string` alloc per value) — the nested lane is
zero string allocations. **AOT (N-6):** the shredder dispatches leaf writes via an explicit `switch (DataType)`
calling closed `WriteAllPartsAsync<T>` instantiations — **never** `MakeGenericMethod` — so every instantiation
is statically rooted for the NativeAOT gate. (Scalar-path string/binary residual: `ParquetFileWriter`'s scalar
lane stays on the ct-less non-generic `WriteAsync` with a per-value `GetString`; migrating it onto this generic
`ReadOnlyMemory<char>` lane — buying the CancellationToken + the allocation win — is a tracked follow-up, N-5.)

### 2.3c Pre-write level-invariant guard (production control — Security N4, Architect A2/A4)
`WriteAllPartsAsync` is a raw primitive: malformed level streams are **silently persisted** (too-few values →
fabricated zeros; `def>maxDef` → cross-row bleed; a missing rep-0 → the footer's `NumRows` silently wrong when
*every* leaf agrees on the wrong count). The guard is **level-only** — right levels with wrong/reordered
*values* are the oracle's job (§3.1), not this control. Before **every** `WriteAllPartsAsync`, assert (O(levels),
span-based, LINQ-free, **unconditional** — not `Debug.Assert`) — violation ⇒ `CorruptData`, fail closed
**before** `CreateAsync` where feasible (§2.9 N9). Bind `maxDef`/`maxRep` to **`field.MaxDefinitionLevel` /
`field.MaxRepetitionLevel`** read off the exact `DataField` passed to `WriteAllPartsAsync` (public getters in
6.1.0) — never the shredder's own computed value, so the guard checks against the **schema's** levels the
decoder uses, not its own belief (N4-c):
- **Per-leaf (repeated lane, `rep != null`):** `def.Length == rep.Length`; `def[i] ∈ [0,maxDef]`,
  `rep[i] ∈ [0,maxRep]`; `count(def == maxDef) == values.Length`; `count(rep == 0) == segment row count`;
  `levels.Length == 0 || rep[0] == 0`.
- **Per-leaf (non-repeated lane, `rep == null` — struct/flat leaves):** `def.Length == segment row count`
  (N4-a/A2 — otherwise a struct leaf has *no* slot-count invariant and a short `def` is a read-time
  `IndexOutOfRange`); bounds + `count(def == maxDef) == values.Length` as above.
- **Joint legality (N4-d/A2):** `rep[i] > 0 ⇒ def[i] > emptyContainerDef` — a continuation slot cannot claim
  container absence.
- **Run legality (N4-e — the empty/null-container continuation class):** track the row-opening slot (`rep == 0`).
  If a row's opening slot carries `def < containerMaxDef` (a **null or empty container**), the next slot **must**
  have `rep == 0` — equivalently `rep[i] > 0 ⇒ def[opening slot of row i] ≥ containerMaxDef`. This mirrors the
  reader's F2 `rowComplete` guard (`BuildRepeatedStructure`) pre-write. It is the **only** detector for the
  phantom-element class (`def=[1,3,3]/rep=[0,1,0]` grows an element in an empty-list row; `def=[0,3,3]` in a
  null-list row): both pass every other clause AND §2.4b (footer `NumRows` is level-derived and stays correct),
  yet Spark/parquet-mr decode a fabricated element and lose the null-vs-empty distinction. The pointwise joint
  clause above does not catch it (the continuation's own `def` is legally high); this run-level clause does.
- **Cross-leaf, pre-write (N4-b — the decisive gap):** for a map, the key/value **rep streams must be
  identical** (`ValidateParallelRepetition`) and their **def streams equal for every null/empty/absent slot**
  (R6 presence + R7 equality below `mapMaxDef`); for a struct, **cross-child null parity** — at every row, all
  child leaves must **agree** on whether the struct is null (`def < structMaxDef` on all, or none). A per-leaf
  guard passes a mis-paired map (keys `[0,1,0]` / values `[0,0,1]`) OR a struct where child A emits
  `def < structMaxDef` at a row where sibling B emits `def == maxDef` — both silently persist, DeltaSharp reads
  an availability error, Spark reads **wrong rows** — so these cross-leaf asserts are mandatory, not "correct by
  construction." (`segment row count` is the writer's batch-derived segment sum, the same reference §2.4b uses —
  never sourced from the shredder's own output, so the `count(rep==0)` check is non-circular, P3.)
- **A4:** map any raw Parquet.Net writer fault escaping `WriteAllPartsAsync` (e.g. the library's own
  cross-leaf row-count `InvalidOperationException`) to the typed `CorruptData` contract (§2.8's wrapping scope
  extends to the write call, not only `ColumnVector` interactions).
- **Provenance (N4-c/A2):** the guard reads `field.MaxDefinitionLevel`/`MaxRepetitionLevel` off the
  **schema-attached** instance (they read 0 until the field is attached to a `ParquetSchema`; the ctor and
  `GetDataFields()` propagate them), and asserts `maxDef ≥ 1` for every nested leaf (always true in-scope) so a
  detached-field mis-derivation cannot silently degrade the bounds.

### 2.4 `CreateField` + recursive `ToDataSchema` + its consumers (Security F1/N1/N2/N3, Architect A3)
`CreateField` builds the nested `Field` recursively (reusing scalar leaf builders → #730/decimal/temporal
inherited), rejecting a nested child fail-closed (→#585) and a **zero-field struct** fail-closed
(`UnsupportedFeature`; Parquet.Net throws a raw `ArgumentException` otherwise — NEW-5).

**`ToDataSchema` must become recursive** (over `ParquetSchema.Fields`, the inverse of `CreateField`) — today it
flattens `DataFields` by leaf name, so a nested write fails `#497`'s `ValidateStagedWriteSchema` and two
array/map columns' duplicate `element`/`key`/`value` leaves make DeltaSharp declare its **own file
`CorruptData`**. `ToDataSchema` has **two production consumers** (`DeltaSharp.Storage` is `IsPackable=false` — no
external-caller surface): `ReadDataSchemaAsync` (the write door, reading DeltaSharp's **own** just-written
bytes) and `ReadDataLeafColumnsAsync` → `ChangeFeedReader` CDF-EE-08 (#662) — the **only foreign,
attacker-controlled** one:
1. `ReadDataSchemaAsync` (write door) — the #497 use; uses the **recursive** `ToDataSchema`.
2. **`ReadDataLeafColumnsAsync` → CDF-EE-08 (#662)** — an id-mode cdc validator that
   interpolates `fileType.SimpleString` **raw** into a user-visible message, safe today *only because
   `ToDataSchema` yields atomic leaf types*. Recursion would echo unbounded foreign nested field names
   (#683/#686). **Decision (A3/N1/Q5):** `ReadDataLeafColumnsAsync` uses a **leaf-flattening `ToDataLeafSchema`**
   helper (its existing behavior — one `ParquetLeafColumn` per leaf), so recursion is confined to the #497
   write door and CDF-EE-08's accept/reject acceptance set is **unchanged**; additionally **route `fileType`
   through `DiagnosticText.DescribeType`** (bounded) so even a defensive path cannot echo a foreign nested name.
   §3.3 pins both an **EE-08 acceptance-set invariance AC** (a foreign nested cdc footer's accept *and*
   mismatch-reject verdicts are identical before/after this PR) and the name-hygiene AC.
- **Depth bound (N2):** the recursive `ToDataSchema` runs on the write door's own bytes, but keep it robust —
  use an **iterative walk with a hard depth cap** (mirror `DeltaWriteSchemaEligibility.MaxDepth = 64`); a footer
  deeper than the cap fails closed `UnsupportedFeature` (an uncatchable `StackOverflowException` is a pod abort
  `MapFooterSchemaFailClosed` cannot catch). Public recursion surface in 6.1.0: `ParquetSchema.Fields`,
  `StructField.Fields`, `ListField.Item`, `MapField.Key`/`Value` (not `Field.Children`). The 64-*type*-level cap
  admits types `SchemaJson` (≈21 struct levels) would refuse, but `StagedDataFile.DataSchema` is only ever
  compared (`DataColumnsMatch`), never serialized — no live path; stated so a future consumer doesn't open one.
- **Comparator (N3):** `#497`'s `DataColumnsMatch` deliberately ignores top-level nullability + metadata, but
  `StructType.Equals`/`ArrayType.Equals` compare `Nullable`/`Metadata`/`ContainsNull` — so nested columns would
  false-reject. Specify a **recursive, nullability- and metadata-insensitive structural comparator** for nested
  types (extend the scalar leniency inward; never relax the door). ACs: nested child metadata present; child
  declared non-nullable but footer OPTIONAL; `ValueContainsNull` mismatch. **This leniency is only safe because
  §2.4a's required-lane value guard enforces the real null invariant pre-write** — a load-bearing compensating
  control an editor must not remove. **Forward (#676):** column-mapping `id`/`physicalName` live in field
  metadata, so nested child physical naming must be **re-derived** under #676, not inherited from this
  metadata-insensitive comparator.

### 2.4a Container nullability + required-lane + zero-field (BL-2/F5/NEW-5/NEW-8)
- `Field.IsNullable` has no public setter → containers are always OPTIONAL on the wire; a `nullable:false`
  **nested container** is **rejected fail-closed** at `CreateField` (refuses the #730 divergence). The `#730`
  `WriteColumnAsync` assert is reframed **per-leaf** (leaf repetition vs mapped `ContainsNull`/`ValueContainsNull`
  /struct-child `Nullable`), and the stale default-arm comment (`ToParquetField`→`CreateField`) fixed (BL-8).
- **Required-lane value guard (F5):** before writing, reject a null in any `REQUIRED` nested leaf
  (`nullable:false` struct child; map **key** over the **referenced** range only — scoping to the full child
  range over-rejects legitimately sliced vectors, NEW-8/BL-13) → `CorruptData`, pre-write.
- **Zero-field struct** rejected (NEW-5).

### 2.4b Post-write footer row-count reconciliation — the common `WriteAsync` core (Security N4/S1/P1/P2/P4)
Reconciliation is bound **inside `ParquetFileWriter.WriteAsync`'s core** — the single choke point where
`totalRows = Σ batch.LogicalRowCount` already exists — **not** "before returning `WriteResult`" (which would
miss `ChangeDataWriter`, whose `WriteAsync` returns `Task`, not `WriteResult`). Reconcile the footer's `NumRows`
(via the existing hardened, checked `GetRowCountAsync`) against that batch-derived `totalRows` before the file
is returned/committed; a mismatch ⇒ `CorruptData`. This binds **all three** production sites —
`DeltaWriteTarget.StageAsync`, **`DeltaOptimize.WriteCompactedFileAsync`** (worst blast radius: `add` + `remove`
tombstones in one transaction, and its only current row-count control is a **`Debug.Assert`** — absent in
Release, so §2.4b *supersedes* it, P2), and **`ChangeDataWriter`**. The reference is `LogicalRowCount`-derived
by contract (not `statistics.NumRecords ?? 0L`, which silently degrades to 0 when stats are off — a **missing
reference fails closed**, never compares against 0). **Streaming sink (#442/#443, P4):** all three sites buffer
to a `MemoryStream` today (seekable footer re-read is cheap); on a non-seekable/streaming sink the reconciliation
**fails closed or re-reads the published object — never silently skips**. Belt-and-suspenders with §2.3c
(per-leaf/cross-leaf level defects before bytes) and independent of it (footer `NumRows` is level-derived, so a
**dropped/duplicated segment** — where every leaf's levels are locally valid against its segment but
`footer NumRows ≠ Σ LogicalRowCount` — is caught **only** here, not by §2.3c, Q-7).

### 2.5 field_id stamping — and why this PR does NOT stamp nested leaf field_ids (Security N5)
Scalar (non-nested) columns stamp `field_id` via the extracted `StampFieldId` helper (guard once — BL-3).
**Nested leaves are NOT stamped in this PR.** Rationale: (a) id-mode nested read is deferred to **#676**
(§2.7), so no reader consumes nested leaf ids here; (b) two `struct<…>` columns with same-named leaves (`zip`)
emit two footer leaves named `zip`, and the current `BuildFieldIdMap` keys by leaf-local name (silent
overwrite) → `field_id` cross-column misattribution — **#829's path-keyed `BuildFieldIdMap` is a stated hard
prerequisite of #676's nested id-mode read.** The array/map `element`/`key`/`value` non-stamping stays a
normative prohibition (F9). #713 fixtures are authored in **none-mode** (no field_id needed).

### 2.6 Fail-closed boundary → #585
Depth>1 (nested as struct child / array element / map key/value), a `nullable:false` container (§2.4a), and a
zero-field struct are rejected at `CreateField` **before any bytes**, structurally + tested (§3.4).

### 2.7 Mode scope — **NONE-mode only** (Architect NEW-3 / Security N6)
`ColumnMapping.EnsureLeaf` rejects a nested top-level column in **both** `name` **and** `id` mode (called from
`ValidateColumnMappingSchema` at load+commit, `AssignFreshMapping`, `ToPhysicalSchema`, …). So at the Delta-table
level this PR delivers **none-mode only**; both mapped modes gate on **#676**. §3.4 asserts `EnsureLeaf` stays
armed for **both** modes (not just the `ParquetFileReader` id-mode reject). An implementer must not relax the
shared `EnsureLeaf` to satisfy a mapped-mode AC.

### 2.8 Selection-vector & diagnostic hygiene (F3/F4/F10)
Selection vector: a nested column on a batch with a `SelectionVector` fails closed (`UnsupportedFeature`; #570/
#546) in the pre-pass before `CreateAsync` — never a partial/misaligned write. Diagnostics: the shredder wraps
every nested-`ColumnVector` interaction so no raw `NotSupportedException`/`DataType.SimpleString` escapes —
convert to `UnsupportedFeature` with `DiagnosticText.Sanitize(columnName)` + bounded `TypeName` KIND; a typed
`default:` arm fails an `ArrowNestedColumnVector` closed (F10). §3.4 pins no nested field name in any emitted
diagnostic.

### 2.9 Component boundaries, ownership, residuals
`NestedColumnShredder` is a pure unit (BL-5) filling **caller-owned** buffers (no pooled-array escape — BL-10);
value-type + level buffers use `ArrayPool` rented under `try/finally` with `clearArray:true` on return and
exact `AsMemory(0,count)` slices (BL-4; string/binary value buffers are owned per-write, §2.3b); level-slot/
value counts accumulate with `checked` arithmetic (N10) and a per-row-group total-slot bound (Quality N4) fails
closed on pathological fan-out. **Assumption re-audit (N7):** the reader's structural guards (`BuildStructNullMask`,
`ValidateParallelDefinition`) previously reasoned "the released write door cannot author crafted levels" — now
false; re-word those comments (DeltaSharp's shredder can author arbitrary levels; the reader guards are the
backstop for DeltaSharp-authored files too). **Pre-pass timing (N9):** the required-lane + level guards run as a
per-file pre-pass before `CreateAsync` where possible; the residual (streaming sink #442/#443 removes the
buffer-to-memory protection F7) is stated. AOT (R3): no `ParquetSerializer`/Linq-Expressions; NativeAOT gate in
CI. Pin the spike (`files/spike-nested-parquet-write.md`) or cite the level-differential test as the in-repo
authority (N8).

---

## 3 · Functional test scenarios & correctness oracles

### 3.1 Round-trip oracle + level-stream differential + model-encoder oracle (the contract)
Per shape × state, `WriteAsync` a `ColumnBatch` from nested `ColumnVector`s, then: (a) read back through the
**real** `NestedParquetColumnReader` and assert equality via a **total, structural comparator** (values,
offsets, **validity bit**, null-vs-empty) whose kill-rate is pinned by a mutation set — validity flip,
null↔empty swap, dropped element, reordered map entries, nulled struct field, **plus (H3-add): offsets shifted
by one preserving the total; map value-lane rotated one with keys fixed; struct value shifted one across a
null-struct row**; (b) assert the **raw def/rep streams** equal the normative §2.3 tables (transcribed literals;
`RawColumnData.RepetitionLevels` **throws** for non-repeated — expect absence, not empty); (c) pin **one golden
file per shape by SHA-256** (H2), and **defer** cross-reader (pyarrow/Spark) validation to **#713** with the
residual named. **Matrix (Quality H1 — complete):**
- struct: present / null-struct / null-field / mixed; **mixed required+optional children** with null-struct
  rows (G2); required-leaf (writable); required-struct is **unwritable → §3.4 reject + a read-only unreachability
  note** (NEW-4/H1-a).
- array<string> & array<long>: `[a,b]`, null-list, empty-list, `[c,null]`, multi-element; required-element
  (writable); required-container **unwritable → reject** (NEW-4).
- map<string,long?>: present/null-map/empty-map/value-null/multi-entry; keys non-null; parallel key/value def+
  rep agreement (G1); **sliced map with a null key in the unreferenced tail must WRITE** (NEW-8).
- **Leaf-type parity (H1-b):** a `[Theory]` over **every** `DataType` the nested `CreateField` accepts —
  bool/byte/short/int/long/float/double/DATE/TIMESTAMP(_NTZ)/DECIMAL/string/binary — asserting nested-write ⇔
  nested-read acceptance parity (and TIME stays rejected both sides, #837). `byte` values 0/127/128/255.
- **Value-boundary lane (H1-c):** pre-1970 / `DateTime.Min|Max` DATE+TIMESTAMP; DECIMAL precision 28 / scale==
  precision; empty-string vs null-string, empty-binary vs null-binary; double NaN/±0/±Inf.
- **Structural (H1-d):** same logical rows as 1 batch vs N batches with varied `CollectSegments` splits vs
  `RowGroupRowLimit` forcing ≥2 row groups → decoded-equal, rep restarts at 0 at each row-group boundary
  (NEW-7); a row group whose **first** row is a null/empty list.
- **Sliced vector (H1-e):** `offset > 0` list/map — offset rebasing (A1: accessors pre-sliced; only raw offsets
  rebased by the element base); **and** an **unsliced** vector (`_offset == 0`) with a **non-zero element base**
  (`offsets[0] > 0`) + a dangling tail — legal per `CopyValidatedOffsets`, the cheapest A1 regression net.
- **Zero-row / all-null column (Quality Q-3):** a zero-row nested batch; a column where **every** row is
  null-list/null-struct/null-map (`values.Length == 0`, incl. the string/binary empty owned buffer).
- **Multi-nested-column attribution (Quality Q-4):** one file with `array<long> a`+`b`, `map<string,long> m1`+
  `m2`, `struct<x:{zip}>`+`struct<y:{zip}>`, and a **scalar** column sharing a leaf name — assert per-column
  **value attribution**, `ReadDataSchemaAsync` returns each shape distinctly, `ValidateStagedWriteSchema`
  accepts (the recursive-`ToDataSchema` regression cell for the duplicate-leaf-name hazard).
- **Level-guard fault injection (§2.3c — Quality Q-1 / Security):** `NestedLevelGuardTests`, one negative cell
  per §2.3c clause each asserting `CorruptData` **before `CreateAsync`** — `def.Length != rep.Length`;
  `def[i] > maxDef` **and** a **negative** `def[i]`/`rep[i]` (the `∈ [0,·]` lower bound); values **over-** and
  **under-**supply (the over-supply case is caught by **no** other oracle — levels are correct and the file
  round-trips — so the guard is its only detector; a shredder sizing from `Elements.Length` on a mutable vector
  produces exactly this, B-5); `count(rep==0) != row count`; `rep[0] != 0`; the `rep==null` struct lane
  `def.Length != row count`; the **joint** `rep>0 ∧ def[i]≤emptyContainerDef`; the **run-legality** (N4-e)
  continuation cells `def=[1,3,3]/rep=[0,1,0]` (empty-list) and `def=[0,3,3]/rep=[0,1,0]` (null-list), each
  pinned as **not** detected by §2.4b; and the **cross-leaf** cases — map key/value rep re-partition at constant
  rep-0 count, map R7 def-inequality, and the **struct cross-child null-parity disagreement** (child A
  `def < structMaxDef` at a row where sibling B emits `def == maxDef` — a distinct defect from the per-leaf
  short-def case, and the assert can otherwise ship no-op'd, Q-1).
- **`WriteAsync`-core reconciliation (§2.4b/S1 — Q-7/Q-8):** the reconciliation test injects a **§2.3c-invisible**
  fault — a **dropped/duplicated `CollectSegments` segment** where every leaf's levels stay locally valid
  against its segment but `footer NumRows ≠ Σ batch.LogicalRowCount` — asserting `CorruptData` **before commit**
  on **all three** write paths (`StageAsync`, `DeltaOptimize`, `ChangeDataWriter` — the last returns `Task`, so
  the hook lives in the `WriteAsync` core), plus a **missing-reference** cell asserting fail-closed (never
  compare-against-0).
- **Selection-vector fail-closed** (F4); pathological fan-out bound (Quality N4).
- **Seeded generative lane** with a **naive model-encoder** reference (row-by-row Dremel emitter in the test
  project) as an independent differential oracle, and **shrinking** to a minimized failing case before pinning
  (Quality N2).
- **Sentinel discipline (Quality Q-2):** structural cells use **non-default** present values (under-supply
  back-fills `default(T)`, so a `0`/`false`/empty/`±0` present value can't be distinguished from a fabricated
  one by round-trip alone); H1-c's deliberately-default values are covered by the level differential + §2.3c
  guard, not round-trip equality.
- **Golden-hash determinism (Quality Q-6):** the SHA-256 goldens are stable only under the pinned Parquet.Net
  version + compression/encoding + `created_by`; regenerating a golden is a **review-gated** event on a
  deliberate version bump (not a Dependabot rubber-stamp — cf. the 6.0.3→6.1.0 read-path break).

### 3.2 Mode & id-mode deferral
**None-mode** round-trip is delivered here (§2.7). Id-mode + name-mode nested are `[Fact(Skip="#676")]`. A
footer-level assertion that (none-mode) files carry the expected leaf structure feeds #713; nested leaf field_id
stamping is **not** exercised (§2.5).

### 3.3 Write-door & footer shape (#497 / #713)
`ReadDataSchemaAsync` on each emitted file returns the **declared logical shape** (recursive `ToDataSchema`);
`ValidateStagedWriteSchema` accepts a nested write (the recursive nullability/metadata-insensitive comparator,
§2.4); the post-write footer `NumRows == WriteResult.RowCount` reconciliation holds on **every** write path
(§2.4b). Footer structure per shape; map key `REQUIRED`; per-leaf `#730` repetition. A **foreign nested cdc
footer** asserts (i) no footer-derived name reaches the CDF-EE-08 message (N1, via `DescribeType`) **and**
(ii) the EE-08 accept/reject **acceptance set is invariant** before/after this PR (the `ToDataLeafSchema`
leaf-flattening decision, §2.4); a **crafted deep footer** fails closed (N2).

### 3.4 Fail-closed rejects
Nested-within-nested, `nullable:false` container, zero-field struct, required-lane null (§2.4a) — all before any
bytes; nested still rejected upstream in **both** mapped modes (`EnsureLeaf`) and by the id-mode reader reject
(§2.7); no diagnostic echoes a nested field name (F3).

### 3.5 Determinism
Byte-identical repeat write (incl. a large-then-small nested batch in one process — the anti-stale-bytes oracle,
Quality N3); normative `GetDataFields()` leaf order; no wall-clock/random/guid.

### 3.6 AC mapping — (rows: none-mode write of struct/array/map of scalars; level differential; all Dremel
states; #497 recursive-comparator round-trip; footer NumRows reconciliation; nested-within-nested + nullable-
container + zero-field + required-null rejects; both mapped modes gate on #676 with guards armed; #713 footer
artifacts; selection-vector fail-closed + sanitized diagnostics; leaf-type/value-boundary/segment/slice
coverage; determinism.)

## 4 · Performance
O(total leaf values) + a per-byte UTF-8→UTF-16 **transcode** term on the string/binary lane (still strictly
cheaper than the scalar path's per-value `Encoding.UTF8.GetString` allocation — the nested lane is zero string
allocations). Single pass over offsets+validity with pooled/cleared/exact-sliced (value+level) buffers, child
resolved once (no per-row accessor). Existing row-group cursor; BenchmarkDotNet on `array<long>`,
`struct<scalars>` **and `array<string>`/`map<string,long>`** (the lanes with the new transcode cost); gate = no
scalar-path regression — noting §2.4b now adds one bounded `GetRowCountAsync` footer scan to **every** write
(scalar output stays byte-identical, §8, but not cost-identical, B-6).

## 5 · Security
No new external input on the **write** path (trusted in-tree batches). Dominant hazard is silent corruption —
retired by §3.1's oracle+differential **and** the §2.3c pre-write + §2.4b post-write production guards (level
streams are an unvalidated wire contract). The **only** foreign/attacker-controlled read surface is the
CDF-EE-08 door, which uses the **leaf-flattening `ToDataLeafSchema`** (recursion is confined to the #497 write
door, over DeltaSharp's own just-written bytes) with `DescribeType`-bounded rendering + depth cap (§2.4).
Diagnostics sanitized (§2.8). **PII at rest:** nested is omitted from `add.stats` (`OmittedNestedType`), but the
Parquet footer's per-column-chunk stats carry **untruncated** nested leaf min/max (`add.stats` truncates
strings to 32; footer stats do not) — same object as the data; route classification to
`privacy-compliance-grc-lead`. The new `RawElementSpan`/`RawEntrySpan` accessors expose the physically-retained
bytes of *logically-null* rows — a shredder-internal structural affordance; §2.3c's
`count(def==maxDef)==values.Length` clause is what keeps those bytes off disk (P5).

## 6 · Threat model
Correctness/silent-corruption, not injection; the sole foreign-footer surface (CDF-EE-08) is
**leaf-flattened** (`ToDataLeafSchema`, not recursive `ToDataSchema`) + depth-capped + `DescribeType`-bounded
(§2.4). Managed nested vectors validate offsets in-ctor; the Arrow-imported `ArrowNestedColumnVector` is the one
non-in-tree producer, failed closed by the typed `default:` arm (§2.8). Reader-side guards
(`BuildStructNullMask`/`ValidateParallelDefinition`) are now the backstop for **DeltaSharp-authored** files too
(N7).

## 7 · Observability
Reuse writer diagnostics; nested rejects emit `UnsupportedFeature` + bounded `TypeName` KIND + #585/#676 pointer.

## 8 · Rollout & risk
Additive: scalar write byte-identical (asserted); inert until a caller writes nested. Risk concentrates in the
shredder's level computation — retired by §2.3c/§2.4b guards + §3.1 differential before merge. NativeAOT gate.
No migration.

## 9 · Decisions
1. Def-level expressibility — resolved (6.1.0 `WriteAllPartsAsync`; §2.3/§2.3b normative + differential-tested).
2. string/binary lane — `ReadOnlyMemory<char>`/`<byte>` (§2.3b).
3. array/map-leaf and (this-PR) nested-struct-leaf field_id — not stamped; #829 is a prerequisite of #676 (§2.5).
4. Mode — none-mode only; #676 owns mapped modes (§2.7).
5. CDF-EE-08 door uses leaf-flattening `ToDataLeafSchema` (recursion confined to the #497 write door); the
   shared `MapFooterSchemaFailClosed` boundary is **parameterized by the shaping function** (recursive vs
   leaf-flattening) so the two footer-schema readers can never diverge on their fail-closed wrapping (Architect
   round-4 merge-time condition; update that code comment).
6. Vector accessors — `RawElementSpan`/`RawEntrySpan` are added as **public** read-only members on
   `List`/`MapColumnVector` (Engine is `IsPackable=false`; no external SemVer obligation) rather than `internal`
   (Engine grants IVT only to its own `.Tests`, so Storage cannot see Engine internals).
7. PR split — PR1 `CreateField`+recursive `ToDataSchema`+`Field[]` ripple + §2.4a/§2.6 rejects + §2.4b
   reconciliation (mechanical, no nested write yet, forces the nullability + foreign-footer decisions open);
   PR2 shredder + §2.3c guards + oracle. #713 fixtures follow.

## 10 · References
`ParquetTypeMapping.cs` (`CreateField`, `ToDataSchema`, `EnsureReadSupported`), `ParquetFileWriter.cs`,
`ParquetFileReader.cs` (`ValidateFileField`, `ReadDataLeafColumnsAsync`, `BuildFieldIdMap` #829, nested-under-id
reject), `NestedParquetColumnReader.cs`, `Engine/Columnar/{Struct,List,Map}ColumnVector.cs`, `ColumnMapping.cs`
(`EnsureLeaf`), `ChangeFeedReader` (CDF-EE-08 #662), `StatisticsPolicy`, `DeltaWriteSchemaEligibility` (MaxDepth).
Spike: `files/spike-nested-parquet-write.md` (6.1.0). Issues: #571/#584, #713, #676, #829, #585, #730, #497,
#662, #813, #570/#546, #442/#443, #683/#686.
