# Nested Parquet write (§2.9) — single-level `struct` / `array` / `map` of scalars

> **Status:** Draft
> **Issue:** [#828](https://github.com/khaines/deltasharp/issues/828) — feat(storage): nested Parquet write (§2.9) — single-level struct/array/map of scalars
> **Author:** design (spike-informed)
> **Reviewers:** delta-storage-format-engineer, dotnet-vectorized-columnar-compute-engineer, dotnet-framework-runtime-engineer, reliability-test-chaos-engineer, cloud-native-security-sme
> **Last Updated:** 2026-08-15
> **Related:** #571/#584 (nested Parquet **read** — the round-trip counterpart), #713 (nested footer artifact tests — consumer), #676 (nested column mapping — consumer), #829 (`BuildFieldIdMap` path-keying — id-mode read correlation), #585 (nested-**within**-nested — the deferred follow-up), #730 (reference-type nullability → footer repetition), #683/#686 (`SimpleString` bounded nested diagnosis)

---

## 1 · Overview

DeltaSharp can **decode** nested Parquet — `array<T>`, `map<K,V>`, `struct<…>` — via
`ParquetTypeMapping.EnsureReadSupported` and `NestedParquetColumnReader` (#571/#584). It **cannot write**
nested Parquet at all: `ParquetTypeMapping.CreateField` throws `StorageErrorKind.UnsupportedFeature` for
`ArrayType`/`MapType`/`StructType` (`ParquetTypeMapping.cs:119`) and `ParquetFileWriter` only ever writes
scalar leaves. Every nested-storage feature is blocked behind this asymmetry:

- **#713** — nested Parquet **footer artifact** tests cannot produce their fixtures.
- **#676** — nested **column mapping** cannot be tested (no write→read round-trip).

This design adds the **write** half so the two halves round-trip. Scope is deliberately the mirror image of
the delivered **read** surface: **single-level nesting of scalars** —

- `struct<s₁, …, sₙ>` where each `sᵢ` is a scalar `StructField`,
- `array<scalar>`,
- `map<scalar, scalar>`.

**Nested-within-nested** writes (`array<struct<…>>`, `struct<array<…>>`, `map<…, struct<…>>`, `array<array<…>>`,
…) are **out of scope** and remain rejected **fail-closed**, deferred to **#585**. That boundary is a hard,
tested contract, not a TODO (§2.6).

The invariant this feature must not break: **the writer produces exactly what the #571 reader decodes.** The
correctness mechanism is a **round-trip oracle** (§3.1) against the real `NestedParquetColumnReader`, not
hand-audited Dremel level arithmetic.

### 1.1 Spike outcome (viability — settled)

A spike against Parquet.Net 6.0.3 (`files/spike-nested-parquet-write.md`) established:

- The high-level API writes and reopens all three shapes (`StructField`, `ListField(name, element)`,
  `MapField(name, key, value)`); the physical structure round-trips (`addr/city`; `tags/list/element`;
  `m/key_value/key`, `m/key_value/value`).
- Column writes are **per leaf** in `schema.GetDataFields()` order via the typed extensions the writer
  already uses: value leaves `rg.WriteAsync<T>(field, ReadOnlyMemory<T?>, repetitionLevels?, customMeta?, ct)`
  with **`T : struct`**; reference leaves (`string`/`byte[]`) the non-generic
  `rg.WriteAsync(field, IReadOnlyCollection<T>, repetitionLevels?)`. Repetition levels are **supplied
  explicitly**; **definition levels are inferred** by Parquet.Net from the (nullable) data array — there is
  **no** explicit definition-level parameter on the typed write API.
- **Nested value leaves must be written as nullable `T?[]`** — a non-null `ReadOnlyMemory<T>` into a nested
  (optional-path) leaf throws `"Definition levels are not ready yet"`.
- **Map keys are `REQUIRED`** — `MapField`'s constructor throws `"map's key cannot be nullable"`; the key leaf
  is written non-nullable.
- **field_id lands in the Thrift footer** (`reader.Metadata.Schema` → `Parquet.Meta.SchemaElement.FieldId`)
  and only on **leaf** `DataField`s. `StructField`/`ListField`/`MapField` **have no settable `FieldId`** —
  Parquet.Net's high-level API cannot stamp a group/container node's field_id. This shapes id-mode mapping
  (§2.7) and is why container correlation is by **physicalName**, leaf correlation by **field_id**.

---

## 2 · Logical architecture

Keep the layering: this is a pure **storage-format** change under `DeltaSharp.Storage/Parquet`. It adds no
public API and no engine/plan behavior; the internal `ColumnBatch`/`ColumnVector` contract is unchanged (the
nested vectors already exist for read — §2.2).

### 2.1 Where nested write sits

Two functions own the writer's schema→Parquet mapping:

- `ParquetTypeMapping.CreateField(StructField, honorReferenceNullability)` — today returns **one** scalar
  `DataField` per top-level column and throws on nested. It must instead return the nested **`Field`**
  (`StructField`/`ListField`/`MapField`) for the in-scope shapes.
- `ParquetFileWriter.WriteAsync` builds `DataField[] fields` (one per column) and calls
  `WriteColumnAsync(rowGroup, fields[c], schema[c], …)` per column. A nested column is **not** one
  `DataField`; it is a `Field` whose **leaves** (`Field.Path`-addressed `DataField`s) are each written
  separately with repetition levels. So the per-column write fans out to **N leaf writes**.

### 2.2 The internal nested representation already exists (read side)

`DeltaSharp.Engine.Columnar` already models nested columns — the write path **consumes** them, it does not
invent them:

- `StructColumnVector` — `FieldCount` children (`Child(i)`), each `Length == parent.Length`, plus the struct's
  own validity bitmap (null struct vs struct-of-null-fields).
- `ListColumnVector` — an `int` **offsets** buffer of length `Length + 1` over an `Elements` child, plus a
  validity bitmap; a **null list** (`IsNull`) is distinct from an **empty list** (`ElementLength == 0`,
  `IsNull == false`).
- `MapColumnVector` — offsets over `Keys` + `Values` children; **keys non-null** by construction; value
  nullability is advisory (`MapType.ValueContainsNull`).

These are exactly the structures the read side assembles, so **the writer's job is the inverse of
`NestedParquetColumnReader`**: shred offsets + validity into Parquet **repetition + definition** levels.

### 2.3 The core new machinery — `ColumnVector` → Parquet leaf shredding

For each top-level column the writer walks the (single-level) nested vector and, for **each leaf**, produces:

1. a **flattened, nullable** value array of the leaf's present values in document order, and
2. a **repetition-level** array of equal length.

Definition levels are then inferred by Parquet.Net from the nullable value array (§1.1). The single-level
shredding rules (max repetition/definition depth ≤ 2) are bounded and enumerable:

- **`struct<sᵢ>`** (non-repeated): each leaf `sᵢ` emits one value **per row** (repetition level `0`
  everywhere; leaf length `== rowCount`). Nulls encode two masked cases through the leaf's nullable array +
  the struct's optionality: a **null struct** masks all its fields; a **null field** inside a present struct.
  Both surface as a null at the leaf position; the reader reconstructs struct-null vs field-null from
  definition levels (Parquet.Net infers them from the schema's optional nesting + the null markers). This
  null/null ambiguity is precisely why the **round-trip oracle (§3.1) is the contract**, not the level math.
- **`array<scalar>`**: iterate rows; for row *r* with `k = offsets[r+1] − offsets[r]` elements, emit the *k*
  element values with repetition `0` for the first element of the row and `1` for each subsequent element;
  a **null** or **empty** list emits a single boundary marker (repetition `0`, a null value) so the row is
  represented — matching the reader's null-vs-empty decode. The exact boundary encoding is **pinned by the
  oracle**, and if Parquet.Net's typed inference cannot distinguish null-list from empty-list, §2.8 R1
  specifies the fallback.
- **`map<scalar,scalar>`**: identical to `array` over the `key_value` repeated group, emitting **paired**
  key/value leaves; keys are `REQUIRED` (never null), values follow `ValueContainsNull`.

The shredding is a pure function of the vector's offsets + validity — **no** `Guid`/`DateTime.UtcNow`/`Random`
(determinism ban), allocation-conscious (reuse per-leaf buffers across row groups where practical).

### 2.4 `CreateField` — emitting the nested `Field`

`CreateField` gains recursive construction for the in-scope shapes, reusing the existing scalar leaf builders
(so `#730` reference-nullability and decimal/temporal handling are inherited unchanged at the leaves):

- `StructType` → `new StructField(name, childLeaf₁, …, childLeafₙ)` where each child is built by the existing
  scalar path; **reject** (fail closed) any child that is itself nested (→ #585).
- `ArrayType(element)` → `new ListField(name, elementLeaf)`; reject nested `element`.
- `MapType(key, value)` → `new MapField(name, keyLeaf /* REQUIRED */, valueLeaf)`; reject nested `key`/`value`.
- The return type widens from `DataField` to `Field` (its base). `ParquetFileWriter` holds `Field[]` and
  resolves each field's leaves via `Field`/`DataField.Path` in `GetDataFields()` order.

`EnsureReadSupported` already accepts these shapes; the two are brought into agreement (writer no longer a
strict subset).

### 2.5 field_id stamping (column-mapping id mode)

In id mode, DeltaSharp stamps `delta.columnMapping.id` as the Parquet `field_id`. Per the spike, only **leaf**
`DataField`s can carry a footer field_id; group nodes cannot. Rules:

- Stamp each **scalar leaf** (including a struct's scalar children) with its column-mapping id, exactly as the
  scalar path does today (`ColumnMapping.TryGetId`).
- **Array `element` / map `key`,`value`** carry **no** Delta column-mapping id (Delta assigns ids only to
  `StructField`s — see #676 C1); they get **no** footer field_id, or a writer-synthesized one only if the
  reader requires it (decided with #676; default: none).
- **Container** nodes (`struct`/`array`/`map`) cannot be stamped → id-mode **read** correlation resolves
  containers by **physicalName** and scalar leaves by **field_id** (this is the #676/#829 contract; see §2.7).

### 2.6 Fail-closed boundary — nested-within-nested → #585

Any nesting depth > 1 (a nested type appearing as a struct child, an array element, or a map key/value) is
**rejected** at `CreateField` with `StorageErrorKind.UnsupportedFeature` and a diagnostic that names **#585**.
This is enforced structurally (the recursion rejects a non-scalar child) and covered by explicit
reject-tests (§3.4), so the scope boundary cannot silently regress into a partial/incorrect deep write.

### 2.7 id-mode read correlation (why #829 is a sibling, not a dependency of the writer)

The **writer** does not depend on #829. But the **round-trip oracle** for id mode reads back through the
reader, which correlates footer field_ids to leaves via `BuildFieldIdMap`. On nested schemas the *current*
`BuildFieldIdMap` mis-attributes by leaf-local name (#829). The id-mode round-trip tests therefore require
#829's path-keying fix to pass; name-mode round-trips do not. The write design is complete without #829; the
**id-mode test lane** consumes it. (Sequencing: #829 lands independently; this feature's id-mode oracle turns
green once both are in.)

### 2.8 Component boundaries & risks

- **R1 — null-list vs empty-list encoding.** Central risk. Mitigation: an oracle case matrix (§3.1) drives the
  encoding; if the typed API cannot express a distinction, drop to `ParquetRowGroupWriter`'s
  `ReadRawAsync`/def-level-aware sibling or the raw column API for that leaf. Fallback is local to the
  shredder; the `Field`/`CreateField` surface is unaffected.
- **R2 — struct-null vs field-null ambiguity.** Resolved by definition-level inference + oracle; the writer
  never needs to disambiguate, only to emit the correct null markers.
- **R3 — AOT.** The write path is on the NativeAOT Executor image; the new code stays on the same typed
  Parquet.Net surface already AOT-gated for scalar write (OQ-1). No reflection/dynamic codegen added; the
  NativeAOT gate runs in CI when the write path changes.
- **R4 — allocation.** Shredding allocates per-leaf value + repetition buffers; size them from offsets and
  reuse across row groups. No `O(total-rows)` index materialized (matches the existing writer's M5 cursor).

---

## 3 · Functional test scenarios & correctness oracles

### 3.1 Round-trip oracle (the contract)

For each shape and each **null/empty/present** combination, `WriteAsync` a `ColumnBatch` built from the nested
`ColumnVector`s, then read the bytes back through the **real** `NestedParquetColumnReader` and assert the
decoded vector equals the input (values, offsets, validity, null-vs-empty). Matrix:

- `struct<city:string, zip:long>`: present struct; **null struct**; struct with **null field** (`zip` null);
  mixed rows.
- `array<string>`: `["a","b"]`, **null list**, **empty list `[]`**, `["c", null-element]`, all in one column.
- `array<long>` (value-type element, exercises the nullable-`T?[]` leaf rule).
- `map<string,long>`: present map, null map, empty map, value-null entry; **keys always non-null**.

### 3.2 Name-mode & id-mode

- **Name mode**: round-trip without column mapping — physical names carry the correlation.
- **Id mode**: stamp field_ids, round-trip through the id-mode reader; asserts leaf field_ids land in the
  footer (`reader.Metadata.Schema`) and resolve to the correct leaves (requires #829; §2.7). Include a
  **same-typed sibling** case (`home:struct<city>`, `work:struct<city>`) to prove no leaf-name collision.

### 3.3 Footer shape (feeds #713)

Assert the emitted Thrift footer structure for each shape: `struct` → `addr(num_children=2)` → `city`,`zip`;
`array` → `tags`→`list`→`element`; `map` → `m`→`key_value(REPEATED)`→`key(REQUIRED)`,`value`. Map key
`REQUIRED`; nullable leaves `OPTIONAL`; `#730` reference-nullability honored (a `"nullable":false` string leaf
is `REQUIRED`).

### 3.4 Fail-closed rejects (scope boundary, #585)

`array<struct<…>>`, `struct<array<…>>`, `struct<struct<…>>`, `map<string, struct<…>>`, `array<array<…>>`
each throw `UnsupportedFeature` **before any bytes are written**, with a message naming #585. A `map` with a
**nullable key** type is rejected (or coerced REQUIRED) per Parquet's map contract.

### 3.5 Determinism / purity

Two writes of the same batch produce byte-identical files (already asserted for scalar write; extend to nested).
No wall-clock/random/guid in the shredder.

### 3.6 Acceptance-criteria mapping

| AC | Scenario |
|----|----------|
| write struct/array/map of scalars | §3.1 |
| write→read parity vs #571 reader (name + id mode) | §3.1, §3.2 |
| null vs empty list/map, null field/value | §3.1 |
| nested-within-nested rejected fail-closed (→#585) | §3.4 |
| footer artifacts for #713 | §3.3 |
| id-mode field_ids in footer, correct correlation | §3.2 (with #829) |
| determinism | §3.5 |

---

## 4 · Performance

Nested write is O(total leaf values); the shredder is a single pass over each vector's offsets + validity with
pre-sized, reused buffers. Row-group sizing keeps the existing running-cursor model (no per-row index).
Baseline vs the scalar path is measured with a BenchmarkDotNet micro-bench on `array<long>` /
`struct<scalars>` writes; the gate is “no regression on the scalar path” (nested is net-new).

## 5 · Security

No new external input on the **write** path (callers pass in-tree `ColumnBatch`es). The **read** side of the
oracle exercises `NestedParquetColumnReader`, whose fail-closed decode (bounded nesting, footer sanity) is
unchanged. Diagnostics run through `DiagnosticText.Sanitize` (no raw column names/paths leaked), matching the
existing writer.

## 6 · Threat model

Writer consumes trusted in-process batches — the surface is a **correctness** (silent-corruption) risk, not an
injection risk. The dominant hazard is a mis-shredded file that reads back wrong (data corruption); the
round-trip oracle against the independent reader is the mitigation. A malformed **foreign** nested file is a
read-path concern, covered by the existing reader's fail-closed decode and by #829 on the id-mode correlation.

## 7 · Observability

Reuse the writer's existing diagnostics; nested rejects emit `UnsupportedFeature` with the bounded nested
`SimpleString` KIND (`#683/#686`) plus the #585 pointer. No new counters required for M-scope.

## 8 · Rollout & risk

Additive: no existing file changes shape (scalar write is byte-identical; assert it). Feature is inert until a
caller writes a nested schema. Risk concentrates in R1/R2 (§2.8), retired by §3.1 before merge. NativeAOT gate
in CI. No migration.

## 9 · Open questions & decisions

1. **Q:** Does the typed `WriteAsync` inference express null-list vs empty-list, or is the raw fallback needed?
   **Decision:** pin empirically in §3.1 first; implement the fallback only if a matrix case fails. Either way
   the `CreateField` surface is unchanged.
2. **Q:** Do array/map leaves need a synthesized footer field_id in id mode? **Decision:** default **no**
   (Delta assigns none); revisit with #676 if its reader resolution requires it.
3. **Q:** One PR or split (CreateField + shredder, then tests/#713 fixtures)? **Decision:** one feature PR;
   #713 fixtures may follow as a thin consumer PR.

## 10 · References

- `src/DeltaSharp.Storage/Parquet/ParquetTypeMapping.cs` (`CreateField:70`, `EnsureReadSupported:165`, nested
  throw `:119`)
- `src/DeltaSharp.Storage/Parquet/ParquetFileWriter.cs` (`WriteAsync`, `WriteColumnAsync:227`)
- `src/DeltaSharp.Storage/Parquet/NestedParquetColumnReader.cs` (the decode this writer must mirror)
- `src/DeltaSharp.Engine/Columnar/{Struct,List,Map}ColumnVector.cs` (the nested representation shredded)
- `src/DeltaSharp.Storage/Parquet/ParquetFileReader.cs` (`BuildFieldIdMap:449` — id-mode correlation, #829)
- Spike: `files/spike-nested-parquet-write.md`
- Issues: #571/#584 (read), #713 (footer tests), #676 (column mapping), #829 (path-keying), #585 (nested-in-nested), #730 (nullability→repetition)
