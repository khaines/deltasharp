# Nested Parquet write (§2.9) — single-level `struct` / `array` / `map` of scalars

> **Status:** Draft (v3 — 6.1.0-viable; council round-2 findings folded in)
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

Pure storage-format change; no public API, no engine/plan behavior; the `ColumnBatch`/`ColumnVector` contract
is unchanged.

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
tuple — BL-10) with, per leaf: present values + explicit def+rep. Accessor rule (NEW-6/BL-11/BL-12): resolve
the child **once** via `Elements`/`Keys`/`Values` (never the per-row `ElementsAt`/`KeysAt`/`ValuesAt`, which
allocate a `ColumnVector` per row and defeat §4); walk rows via `IsNull(i)` + the **raw physical span**
(`offsets[i+1]−offsets[i]`), **not** `ElementLength(i)` — which returns 0 for a null list even when its physical
span is non-zero, misaligning the leaf cursor (BL-11). Since `_offsets` is private, the shredder reads the span
via the sanctioned public surface (`ElementLength` for present rows + a documented null-row raw-span rule); if
that surface is insufficient, add an `internal` span accessor on the vectors (state which). Sliced vectors
carry an absolute `_offset` — the shredder rebases by it (NEW-8/H1-e). Max depth ≤ 2, so the tables are small
and **normative** (reader thresholds cited):

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
front-filled, defined-only (an off-by-one shifts every downstream value). String/binary `ReadOnlyMemory<char>`/
`<byte>` buffers backing `values` **must outlive the `WriteAllPartsAsync` call** — so string/binary leaves use
a per-write owned buffer (not a cleared-and-returned pool slice) whose lifetime spans the call, reconciling
with §2.9's exact-slice/clear discipline (which applies to the value-type + level lanes).

### 2.3c Pre-write level-invariant guard (production control — Security N4)
`WriteAllPartsAsync` is a raw primitive: malformed level streams are **silently persisted** (too-few values →
fabricated zeros; `def>maxDef` → cross-row bleed; a missing rep-0 → the footer's `NumRows` silently wrong).
Before **every** `WriteAllPartsAsync`, assert (O(levels), cheap) — violation ⇒ `CorruptData`, fail closed
**before** `CreateAsync` where feasible (§2.9 N9):
`def.Length == rep.Length` (or rep `null` for non-repeated); `def[i] ∈ [0,maxDef]`, `rep[i] ∈ [0,maxRep]`;
`count(def == maxDef) == values.Length`; `count(rep == 0) == segment row count`; `rep[0] == 0`.

### 2.4 `CreateField` + recursive `ToDataSchema` + its consumers (Security F1/N1/N2/N3)
`CreateField` builds the nested `Field` recursively (reusing scalar leaf builders → #730/decimal/temporal
inherited), rejecting a nested child fail-closed (→#585) and a **zero-field struct** fail-closed
(`UnsupportedFeature`; Parquet.Net throws a raw `ArgumentException` otherwise — NEW-5).

**`ToDataSchema` must become recursive** (over `ParquetSchema.Fields`, the inverse of `CreateField`) — today it
flattens `DataFields` by leaf name, so a nested write fails `#497`'s `ValidateStagedWriteSchema` and two
array/map columns' duplicate `element`/`key`/`value` leaves make DeltaSharp declare its **own file
`CorruptData`**. But `ToDataSchema` has **three** consumers, two on **foreign, attacker-controlled** footers:
1. `ReadDataSchemaAsync` (write door) — the #497 use.
2. **`ReadDataLeafColumnsAsync` → `ChangeFeedReader` CDF-EE-08 (#662)** — an id-mode cdc validator that
   interpolates `fileType.SimpleString` **raw** into a user-visible message, safe today *only because
   `ToDataSchema` yields atomic leaf types*. Recursion would echo unbounded foreign nested field names
   (#683/#686). **Route `fileType` through `DiagnosticText.DescribeType`** (bounded), and either keep
   `ReadDataLeafColumnsAsync` on a **leaf-flattening** helper or specify container handling explicitly.
- **Depth bound (N2):** `ToDataSchema` runs on untrusted footers; unbounded recursion → uncatchable
  `StackOverflowException` (a pod abort `MapFooterSchemaFailClosed` cannot catch). Use an **iterative walk with a
  hard depth cap** (mirror `DeltaWriteSchemaEligibility.MaxDepth = 64`); a footer deeper than the cap fails
  closed `UnsupportedFeature`. State the depth>1 foreign-footer behavior normatively.
- **Comparator (N3):** `#497`'s `DataColumnsMatch` deliberately ignores top-level nullability + metadata, but
  `StructType.Equals`/`ArrayType.Equals` compare `Nullable`/`Metadata`/`ContainsNull` — so nested columns would
  false-reject. Specify a **recursive, nullability- and metadata-insensitive structural comparator** for nested
  types (extend the scalar leniency inward; never relax the door). ACs: nested child metadata present; child
  declared non-nullable but footer OPTIONAL; `ValueContainsNull` mismatch.

### 2.4a Container nullability + required-lane + zero-field (BL-2/F5/NEW-5/NEW-8)
- `Field.IsNullable` has no public setter → containers are always OPTIONAL on the wire; a `nullable:false`
  **nested container** is **rejected fail-closed** at `CreateField` (refuses the #730 divergence). The `#730`
  `WriteColumnAsync` assert is reframed **per-leaf** (leaf repetition vs mapped `ContainsNull`/`ValueContainsNull`
  /struct-child `Nullable`), and the stale default-arm comment (`ToParquetField`→`CreateField`) fixed (BL-8).
- **Required-lane value guard (F5):** before writing, reject a null in any `REQUIRED` nested leaf
  (`nullable:false` struct child; map **key** over the **referenced** range only — scoping to the full child
  range over-rejects legitimately sliced vectors, NEW-8/BL-13) → `CorruptData`, pre-write.
- **Zero-field struct** rejected (NEW-5).

### 2.4b Post-write footer row-count reconciliation (Security N4)
At the staging door (which already re-reads the footer for schema), compare the footer's `NumRows` against
`WriteResult.RowCount` (derived from the batch) **before `PutIfAbsentAsync`**; a mismatch ⇒ `CorruptData`, so a
level defect can never become a committed `add` whose `numRecords` contradicts the bytes (with knock-on DV/CDF
accounting). Belt-and-suspenders with §2.3c.

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
- **Sliced vector (H1-e):** `offset > 0` list/map — offset rebasing.
- **Selection-vector fail-closed** (F4); pathological fan-out bound (Quality N4).
- **Seeded generative lane** with a **naive model-encoder** reference (row-by-row Dremel emitter in the test
  project) as an independent differential oracle, and **shrinking** to a minimized failing case before pinning
  (Quality N2).

### 3.2 Mode & id-mode deferral
**None-mode** round-trip is delivered here (§2.7). Id-mode + name-mode nested are `[Fact(Skip="#676")]`. A
footer-level assertion that (none-mode) files carry the expected leaf structure feeds #713; nested leaf field_id
stamping is **not** exercised (§2.5).

### 3.3 Write-door & footer shape (#497 / #713)
`ReadDataSchemaAsync` on each emitted file returns the **declared logical shape** (recursive `ToDataSchema`);
`ValidateStagedWriteSchema` accepts a nested write (the recursive nullability/metadata-insensitive comparator,
§2.4); the post-write footer `NumRows == WriteResult.RowCount` reconciliation holds (§2.4b). Footer structure
per shape; map key `REQUIRED`; per-leaf `#730` repetition. A **foreign nested cdc footer** asserts no
footer-derived name reaches the CDF-EE-08 message (N1) and a **crafted deep footer** fails closed (N2).

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
O(total leaf values); single pass over offsets+validity with pooled/cleared/exact-sliced (value+level) buffers,
child resolved once (no per-row accessor). Existing row-group cursor; BenchmarkDotNet on `array<long>`/
`struct<scalars>`; gate = no scalar-path regression.

## 5 · Security
No new external input on the **write** path (trusted in-tree batches). Dominant hazard is silent corruption —
retired by §3.1's oracle+differential **and** the §2.3c pre-write + §2.4b post-write production guards (level
streams are an unvalidated wire contract). The recursive `ToDataSchema` now runs on **foreign** footers
(CDF-EE-08) — bounded rendering + depth cap (§2.4). Diagnostics sanitized (§2.8). **PII at rest:** nested is
omitted from `add.stats` (`OmittedNestedType`), but the Parquet footer's per-column-chunk stats carry
**untruncated** nested leaf min/max (`add.stats` truncates strings to 32; footer stats do not) — same object as
the data; route classification to `privacy-compliance-grc-lead`.

## 6 · Threat model
Correctness/silent-corruption + a foreign-footer read surface (via recursive `ToDataSchema`), not injection.
Managed nested vectors validate offsets in-ctor; the Arrow-imported `ArrowNestedColumnVector` is the one
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
5. PR split — PR1 `CreateField`+recursive `ToDataSchema`+`Field[]` ripple + §2.4a/§2.6 rejects + §2.4b
   reconciliation (mechanical, no nested write yet, forces the nullability + foreign-footer decisions open);
   PR2 shredder + §2.3c guards + oracle. #713 fixtures follow.

## 10 · References
`ParquetTypeMapping.cs` (`CreateField`, `ToDataSchema`, `EnsureReadSupported`), `ParquetFileWriter.cs`,
`ParquetFileReader.cs` (`ValidateFileField`, `ReadDataLeafColumnsAsync`, `BuildFieldIdMap` #829, nested-under-id
reject), `NestedParquetColumnReader.cs`, `Engine/Columnar/{Struct,List,Map}ColumnVector.cs`, `ColumnMapping.cs`
(`EnsureLeaf`), `ChangeFeedReader` (CDF-EE-08 #662), `StatisticsPolicy`, `DeltaWriteSchemaEligibility` (MaxDepth).
Spike: `files/spike-nested-parquet-write.md` (6.1.0). Issues: #571/#584, #713, #676, #829, #585, #730, #497,
#662, #813, #570/#546, #442/#443, #683/#686.
