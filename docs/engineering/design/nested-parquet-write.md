# Nested Parquet write (§2.9) — single-level `struct` / `array` / `map` of scalars

> **Status:** Draft (revised for Parquet.Net 6.1.0 — feature now viable)
> **Issue:** [#828](https://github.com/khaines/deltasharp/issues/828) — feat(storage): nested Parquet write (§2.9)
> **Author:** design (spike-informed; revised after the 6.1.0 upgrade)
> **Reviewers:** delta-storage-format-engineer, dotnet-vectorized-columnar-compute-engineer, dotnet-framework-runtime-engineer, reliability-test-chaos-engineer, cloud-native-security-sme
> **Last Updated:** 2026-08-19
> **Related:** #571/#584 (nested **read** — the round-trip counterpart), #713 (footer artifact tests), #676 (nested column mapping), #829 (`BuildFieldIdMap` path-keying), #585 (nested-**within**-nested — deferred), #730 (nullability→repetition), #683/#686 (`SimpleString` bounded nested diagnosis), #497 (write-door schema validation), #570/#546 (nested vector selection)

---

## 1 · Overview

DeltaSharp can **decode** nested Parquet (`array<T>`, `map<K,V>`, `struct<…>`) via
`ParquetTypeMapping.EnsureReadSupported` + `NestedParquetColumnReader` (#571/#584), but **cannot write** it:
`ParquetTypeMapping.CreateField` throws `UnsupportedFeature` for `Array`/`Map`/`StructType` (~line 119) and
`ParquetFileWriter` writes scalar leaves only. This blocks **#713** (footer-artifact fixtures) and **#676**
(nested column mapping — no write→read round-trip).

This design adds the **write** half, mirroring the delivered **read** surface: **single-level nesting of
scalars** — `struct<s₁…sₙ>` (scalar `StructField`s), `array<scalar>`, `map<scalar,scalar>`.
**Nested-within-nested** (`array<struct>`, `struct<array>`, `map<_,struct>`, `array<array>`, …) stays rejected
**fail-closed** and defers to **#585** — a hard, tested boundary (§2.6), not a TODO.

### 1.1 Viability — settled under Parquet.Net 6.1.0 (this is the material change from the prior draft)

The **prior draft was found non-viable against 6.0.3**: its public typed `WriteAsync` inferred definition
levels from a two-state (present/null) array and could emit only `maxDef`/`maxDef−1`, so null-list, empty-list,
null-element, and null-struct all collapsed and silently decoded wrong; the four-state def-level writer was
`internal`. **6.1.0 (pinned on `main`, #837) makes `ParquetRowGroupWriter.WriteAllPartsAsync<T>` PUBLIC:**

```csharp
ValueTask WriteAllPartsAsync<T>(DataField field, ReadOnlyMemory<T> values,
    ReadOnlyMemory<int>? definitionValues, ReadOnlyMemory<int>? repetitionLevels, CancellationToken ct)
```

It writes the leaf's **present values** plus **explicit definition and repetition levels** — the full Dremel
encoding. A spike (`files/spike-nested-parquet-write.md`, 6.1.0) proved exact round-trips of the previously
inexpressible cases (`ReadRawAsync` reads back the identical levels):

- `array<long>` rows `[1,2]`, **null-list**, **empty-list**, `[3, null-element]` → def `[3,3,0,1,3,2]`,
  rep `[0,1,0,0,0,1]` — **round-tripped byte-exact**.
- `struct<zip>` rows `{10}`, **null-struct**, `{null}` → def `[2,0,1]` — **round-tripped byte-exact**.

So the writer **computes** the Dremel levels from the internal vector (§2.3) and writes them explicitly — it no
longer depends on library inference, and the "phantom fallback" of the prior draft is gone. **Container nodes
still carry no settable `field_id`** and `Field.IsNullable` has **no public setter** in 6.1.0 (verified) — both
shape the design (§2.4, §2.5). The correctness contract is a **round-trip oracle against the real #571
reader**, backstopped by an **explicit level-stream differential** (§3) — level math is now *specified*, per
the prior council's H1, not left to inference.

---

## 2 · Logical architecture

Pure storage-format change under `DeltaSharp.Storage/Parquet`; no public API, no engine/plan behavior; the
internal `ColumnBatch`/`ColumnVector` contract is unchanged (the nested vectors already exist for read).

### 2.1 Where nested write sits
- `ParquetTypeMapping.CreateField(StructField, honorReferenceNullability)` returns **one** scalar `DataField`
  today and throws on nested. It must return the nested **`Field`** (`StructField`/`ListField`/`MapField`) for
  the in-scope shapes. Its return type widens `DataField` → `Field` (base); `ParquetFileWriter` holds `Field[]`
  and resolves each field's leaves in `schema.GetDataFields()` order.
- `ParquetFileWriter.WriteAsync` builds the fields and calls a per-column writer. A nested column fans out to
  **N leaf writes**, each `WriteAllPartsAsync<T>(leaf, values, def, rep, ct)`.

### 2.2 The internal nested representation already exists (read side)
`StructColumnVector` (children, each `Length==parent.Length`, + struct validity), `ListColumnVector`
(int offsets length `Length+1` over an `Elements` child + validity; null-list vs empty-list distinguished by
`IsNull`), `MapColumnVector` (offsets over `Keys`+`Values`; keys non-null). The writer **consumes** these; its
job is the inverse of `NestedParquetColumnReader` — shred offsets+validity into **def+rep** level streams.

### 2.3 The shredder — `ColumnVector` → (values, def, rep), then `WriteAllPartsAsync` (NORMATIVE level table)

For each top-level column the shredder walks the single-level vector and, per **leaf**, produces the present
values plus **explicit** def+rep arrays. Max depth ≤ 2, so the level tables are small and **specified here**
(reader thresholds cited from `NestedParquetColumnReader`):

**`struct<leaf>`** (struct OPTIONAL, leaf per its nullability) — leaf `maxDef` = (struct optional:1) + (leaf
optional:1); rep always 0:

| row state | def | reader decode |
|---|---|---|
| null struct | 0 | `BuildStructNullMask`: `def < structMaxDef(1)` ⇒ null struct |
| present struct, null field | 1 | present struct; leaf null |
| present value | 2 (`maxDef`) | present value |

A **REQUIRED** leaf (`nullable:false`) has `maxDef` = 1: null-struct ⇒ 0, present ⇒ 1; a null field is
**impossible** and rejected pre-write (§2.4a).

**`array<element>`** (list OPTIONAL, element per its nullability) — leaf `maxDef` = (list optional:1) + (repeated
list:1) + (element optional:1) = 3 when element nullable; `containerMaxDef=2`, `emptyContainerDef=1` in
`BuildRepeatedStructure`:

| row state | def | rep(first) | reader decode |
|---|---|---|---|
| null list | 0 | 0 | `def < emptyContainerDef(1)` ⇒ null list |
| empty list `[]` | 1 | 0 | `def == 1` ⇒ empty list |
| null element | 2 | 0/1 | present list, null element |
| present value | 3 | 0/1 | present value |

rep = 0 for the first element of a row, 1 for each subsequent element. A **REQUIRED** element drops one level
(present ⇒ 2, empty ⇒ 1, null-list ⇒ 0); null-element impossible (rejected §2.4a).

**`map<key,value>`** — identical to `array` over the `key_value` repeated group, emitting **paired** key/value
leaves. Keys are `REQUIRED` (`maxDef` one lower than the value); values follow `ValueContainsNull`. The
key-leaf and value-leaf level slots for null/empty maps are emitted **in parallel** so
`ValidateParallelDefinition` sees them agree on entry presence — the prior draft's contradiction ("keys never
null" vs "a null/empty map still needs a key-leaf slot") is resolved: the key leaf's slot for a null/empty map
is authored at `def < mapMaxDef` (a structural absence marker), never as a null key **value**.

The shredder is a pure function of offsets+validity — **no** `Guid`/`DateTime.UtcNow`/`Random` (determinism
ban); it rents/reuses per-leaf `def`/`rep`/value buffers via `ArrayPool<T>` with `clearArray:true` and always
writes exact-length slices (`AsMemory(0,count)`) so no stale/cross-tenant tail bytes reach disk (prior BL-4).

### 2.4 `CreateField` — emitting the nested `Field`, and the write-door schema round-trip (Security F1)
`CreateField` gains recursive construction (reusing the scalar leaf builders, so `#730` reference-nullability
and decimal/temporal handling are inherited at the leaves): `StructType`→`StructField`, `ArrayType`→`ListField`,
`MapType`→`MapField(keyLeaf REQUIRED, valueLeaf)`; each rejects a **nested child** fail-closed (→ #585).

**`ParquetTypeMapping.ToDataSchema` MUST become recursive.** It reconstructs the physical schema a staged file
was written with — used by `#497`'s `ValidateStagedWriteSchema`. Today it iterates `ParquetSchema.DataFields`
(**leaves**) keyed by leaf-local name, so a nested column reconstructs as its flattened leaves → count/name
mismatch → **every nested write fails at commit**, and two array/map columns produce duplicate `element`/`key`/
`value` leaves → `StructType` duplicate-name throw → DeltaSharp declares **its own valid file `CorruptData`**.
Fix: iterate `ParquetSchema.Fields` and rebuild `StructType`/`ArrayType`/`MapType` from `StructField`/
`ListField`/`MapField` — the exact inverse of the widened `CreateField`. §3.3 pins a `ReadDataSchemaAsync`→
declared-shape assertion and a `#497` write-door round-trip AC. **Do not** exempt nested columns from
`ValidateStagedWriteSchema` — that would fail-open the only gate comparing real bytes to the declaration.

### 2.4a Container nullability (BL-2) and the required-lane value guard (F5)
`Field.IsNullable` has **no public setter** in 6.1.0, so `StructField`/`ListField`/`MapField` are always
`OPTIONAL` on the wire. Consequences and rules:
- A Delta `"nullable": false` **nested container** cannot be emitted `REQUIRED`. **Reject it fail-closed** at
  `CreateField` (`UnsupportedFeature`, naming this limitation) rather than silently writing an `OPTIONAL`
  container and re-introducing the `#730` footer↔log divergence. (Scalar leaves inside are still emitted with
  their own repetition via the leaf builder; the container-level `nullable:false` is the unsupported case.)
- The existing `#730` single-source assert in `WriteColumnAsync` (`field.IsNullable == schemaField.Nullable`)
  is **per-leaf** in the nested path — assert each leaf's repetition against the mapped
  `ArrayType.ContainsNull` / `MapType.ValueContainsNull` / struct-child `Nullable`, never against the top-level
  column's nullability. State the stale default-arm comment fix (`ToParquetField`→`CreateField`).
- **Required-lane value guard:** before `WriteAllPartsAsync`, validate that no `REQUIRED` nested leaf carries a
  null (a `nullable:false` struct child; a map **key** over the *full* child range, since `MapColumnVector`
  only enforces keys over the referenced range) → `CorruptData`, **before any bytes are written**.

### 2.5 field_id stamping (id mode)
Stamp each **scalar leaf** (incl. struct scalar children) with `delta.columnMapping.id` via the existing
range-guarded helper (extract `StampFieldId(DataField,StructField)` so the guard lives once, not duplicated
across the three shape handlers — prior BL-3). **Array `element` / map `key`,`value` carry no Delta id → no
footer field_id (a normative prohibition, not an open question — prior F9): stamping them would create
guaranteed `field_id` collisions across two array/map columns).** **Containers** cannot be stamped (leaf-only,
6.1.0-confirmed) → id-mode **read** correlates containers by **physicalName** (stable across logical rename in
both mapped modes) and scalar leaves by **field_id**.

### 2.6 Fail-closed boundary — nested-within-nested → #585
Depth > 1 (a nested type as a struct child / array element / map key/value) is rejected at `CreateField`
(`UnsupportedFeature`, names #585), **before any bytes are written**, enforced structurally + tested (§3.4).

### 2.7 id-mode read correlation is gated on #676 (not #829) — prior H2/F2
The **writer** stamps leaf field_ids and is independent of #829. But the **id-mode read** of a nested file is
rejected upstream today by `ParquetFileReader` (nested-under-id-mode reject) **and** by `ColumnMapping.EnsureLeaf`
at load/commit — both are **#676** scope. So **this PR ships name-mode / none-mode only**; the id-mode nested
round-trip AC is `[Fact(Skip="#676")]`, and §3.4 asserts those upstream guards are **still armed** after this
PR. The prior draft's claim that the id-mode oracle needs only #829 was wrong; #829 (`BuildFieldIdMap`
path-keying) is a sibling correctness fix, not this feature's gate.

### 2.8 Selection-vector & diagnostic hygiene (prior F3/F4)
- **Selection vector (F4):** `ParquetFileWriter` consumes `batch.SelectedColumn(c)`; `Select` throws for nested
  vectors (#570, deferred to #546). A nested column in a batch carrying a `SelectionVector` must **fail closed**
  with a typed `UnsupportedFeature` (nested write requires a materialized batch) — never a partial/misaligned
  write (which would be cross-row corruption). §3.1 pins this.
- **Diagnostics (F3):** the shredder wraps every nested-`ColumnVector` interaction so no
  `ColumnVector`-originated exception (`ListColumnVector.Select`, `ArrowNestedColumnVector.GetValues`, …) escapes
  the storage layer un-normalized — convert to `UnsupportedFeature` with `DiagnosticText.Sanitize(columnName)` +
  the bounded `TypeName` KIND (never raw `DataType.SimpleString`, the #683/#686 hazard). An explicit typed
  `default:` arm on the shredder's vector dispatch fails an Arrow-imported nested vector closed. §3.4 asserts no
  emitted nested-write diagnostic contains a nested field name.

### 2.9 Component boundaries & residual risks
- Shredder is a new `internal static class NestedColumnShredder` producing `(values,def,rep)` per leaf, **unit-
  testable without a Parquet stream** (prior BL-5); `ParquetFileWriter` retains dispatch only. Element-granular
  loops honor the existing cancellation stride.
- **AOT (R3):** `WriteAllPartsAsync` is a typed method on the same Parquet.Net surface already AOT-gated for
  scalar write — **no** `ParquetSerializer`/Linq-Expressions (the AOT-hostile path the prior draft's only viable
  option required). The NativeAOT gate runs in CI when the write path changes.
- **PII at rest (Security F8):** `add.stats` correctly omits nested (`StatisticsPolicy` → `OmittedNestedType`),
  but Parquet's per-column-chunk footer statistics carry nested leaf min/max; state this asymmetry (§5) so a
  data-classification reviewer isn't misled.

---

## 3 · Functional test scenarios & correctness oracles

### 3.1 Round-trip oracle + level-stream differential (the contract)
For each shape × null/empty/present combination, `WriteAsync` a `ColumnBatch` built from the nested
`ColumnVector`s, then (a) read the bytes back through the **real** `NestedParquetColumnReader` and assert the
decoded vector **equals** the input via a **total, structural comparator** (values, offsets, **validity bit**,
null-vs-empty — the comparator is specified and its kill-rate pinned by a mutation test that flips one bit at a
time: validity flip, null-list↔empty-list swap, dropped element, reordered map entries, nulled struct field —
prior H3); and (b) assert the **raw def/rep level streams** equal the normative §2.3 table (transcribed
literals, `FooterWireKeys` doctrine). Matrix (prior Quality H1 — complete):
- `struct<city:string, zip:long?>`: present / null-struct / null-field / mixed; **required struct**
  (`structMaxDef==0` branch) and **required leaf**.
- `array<string>` and `array<long>`: `[a,b]`, null-list, empty-list, `[c,null]`; **required container**
  (different `emptyContainerDef` branch); **multi-element rows** (rep>0 continuation).
- `map<string,long?>`: present / null-map / empty-map / value-null; **multi-entry map** (rep>0); keys always
  non-null; parallel key/value slot agreement.
- Nested leaf types beyond string/long: bool/int/double/DATE/TIMESTAMP(_NTZ)/DECIMAL/binary (annotations a
  foreign reader keys on).
- A **seeded generative** lane (random row counts incl. 0, per-row `{null,empty,N}`, null elements, leaf types,
  row-group boundaries), printing+pinning failing seeds, matching the scalar path's `DeterministicRng` bar.
- **Selection-vector fail-closed** (F4): a nested column in a batch with a `SelectionVector` throws
  `UnsupportedFeature`.

### 3.2 Name-mode & (deferred) id-mode
Name/none-mode round-trip is delivered here. Id-mode nested round-trip is `[Fact(Skip="#676")]`; a footer-level
assertion that leaf field_ids land in `reader.Metadata.Schema` (for #713) **is** delivered.

### 3.3 Write-door & footer shape (feeds #713, Security F1)
Assert `ReadDataSchemaAsync` on each emitted file returns the **declared logical shape** (recursive
`ToDataSchema`), and `ValidateStagedWriteSchema` accepts a nested write (the `#497` round-trip AC). Assert the
Thrift footer structure per shape (`addr(nc=2)`→city,zip; `tags`→`list`→`element`; `m`→`key_value(REPEATED)`→
`key(REQUIRED)`,`value`), map key `REQUIRED`, `#730` reference-nullability honored per leaf.

### 3.4 Fail-closed rejects
`array<struct>`, `struct<array>`, `struct<struct>`, `map<_,struct>`, `array<array>` throw before any bytes
(→#585); a `nullable:false` **nested container** rejected (§2.4a); a `REQUIRED`-leaf null rejected pre-write
(§2.4a); id-mode nested still rejected upstream (§2.7); no diagnostic echoes a nested field name (F3).

### 3.5 Determinism
Two writes of the same batch are byte-identical (extend the scalar assertion); leaf iteration order is
normative (`GetDataFields()` order); no wall-clock/random/guid.

### 3.6 AC mapping
| AC | Scenario |
|----|----------|
| write struct/array/map of scalars, name mode | §3.1 |
| write→read parity vs #571 reader + level differential | §3.1 |
| null vs empty list/map, null field/value (all Dremel states) | §3.1, §2.3 table |
| #497 write-door round-trip (recursive ToDataSchema) | §3.3 |
| nested-within-nested + nullable-container + required-null rejected | §3.4, §2.4a |
| id-mode deferred to #676; upstream guards armed | §3.2, §3.4 |
| footer artifacts for #713 | §3.3 |
| selection-vector fail-closed; diagnostics sanitized | §3.1, §3.4 |
| determinism | §3.5 |

## 4 · Performance
O(total leaf values); single pass over offsets+validity with pooled, cleared, exact-sliced buffers; existing
row-group cursor unchanged. BenchmarkDotNet micro-bench on `array<long>`/`struct<scalars>`; gate = no
regression on the scalar path.

## 5 · Security
No new external input on the write path (trusted in-tree `ColumnBatch`es). Dominant hazard is silent
corruption — retired by §3.1's oracle **and** the level differential. Diagnostics sanitized (§2.8). **PII note:**
nested columns are omitted from `add.stats` but their leaf min/max appear in the Parquet footer's column-chunk
statistics — the value inherits the data's classification at rest (§2.9).

## 6 · Threat model
Correctness/silent-corruption surface, not injection. Managed nested vectors validate offsets in-ctor; an
Arrow-imported (`ArrowNestedColumnVector`) column is the one non-in-tree producer and is failed closed by the
typed `default:` arm (§2.8). Foreign-file read hazards are the reader's/#829's domain.

## 7 · Observability
Reuse the writer's diagnostics; nested rejects emit `UnsupportedFeature` with the bounded nested `TypeName`
KIND (#683/#686) + the #585/#676 pointer.

## 8 · Rollout & risk
Additive: scalar write byte-identical (asserted); inert until a caller writes nested. Risk now concentrates in
the shredder's level computation — retired by §3.1's level differential before merge. NativeAOT gate in CI. No
migration.

## 9 · Decisions (prior open questions resolved)
1. **Def-level expressibility:** resolved — 6.1.0 `WriteAllPartsAsync` writes explicit levels; the §2.3 table is
   normative and differential-tested. No inference, no fallback.
2. **array/map-leaf field_id:** normative **prohibition** (§2.5), tested.
3. **PR split:** PR1 = `CreateField`+`ToDataSchema` widening + `Field[]` ripple + §2.6/§2.4a fail-closed rejects
   (mechanical, no nested write yet, forces the nullability decision into the open); PR2 = shredder + oracle.
   `#713` fixtures follow as a thin consumer.

## 10 · References
- `ParquetTypeMapping.cs` (`CreateField`, `ToDataSchema`, `EnsureReadSupported`), `ParquetFileWriter.cs`
  (`WriteAsync`, `WriteColumnAsync`), `NestedParquetColumnReader.cs`, `Engine/Columnar/{Struct,List,Map}ColumnVector.cs`,
  `ParquetFileReader.cs` (`BuildFieldIdMap` #829; nested-under-id-mode reject #676), `ColumnMapping.cs`
  (`EnsureLeaf`), `StatisticsPolicy`.
- Spike: `files/spike-nested-parquet-write.md` (6.1.0 `WriteAllPartsAsync` level round-trips).
- Issues: #571/#584, #713, #676, #829, #585, #730, #497, #570/#546, #683/#686.
