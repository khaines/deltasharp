# Type system & schema model (v1)

> **Status:** living document. Created with
> [STORY-02.5.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-02-columnar-memory-type-system.md#story-0251-implement-v1-type-descriptors-and-schema-model).
> Grounded in [ADR-0008](../../adr/0008-type-system-row-format.md) (type system + row format)
> and [ADR-0002](../../adr/0002-columnar-batch-format.md) (columnar batches consume the
> physical-layout seam). Update it whenever the v1 type matrix, validation rules, JSON format,
> or physical-layout contract changes.

The DeltaSharp **logical type system** is the shared contract that vectors, binary rows,
expressions, the analyzer, and (later) the public API all agree on for a value's *shape*. It
is the foundation of EPIC-02: the columnar `ColumnVector`/`ColumnBatch` contracts
([STORY-02.1.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-02-columnar-memory-type-system.md))
and the binary row format (STORY-02.4.1) both bind a column/field to a `DataType` and consume
its **physical layout** to size buffers.

It lives in the unshipped `DeltaSharp.Engine` assembly under `src/DeltaSharp.Engine/Types/`.
`public` here is an **engine-internal** seam (the assembly is never packed and carries no
PublicAPI baseline — see [api-governance.md](api-governance.md) and
[testing-conventions.md](testing-conventions.md)); the public `df.schema` surface is a later
EPIC-04 concern and is intentionally **not** exposed from `DeltaSharp.Core` here.

## Design goals

1. **Spark SQL parity.** Match Spark's type names, nullability model, and JSON schema format
   so Spark concepts port over and DeltaSharp can read/write the same schema strings Delta
   stores (checklist 15).
2. **Determinism.** Equality and hashing are structural and **process-independent** so
   planning, caching, and tests are reproducible (`.github/copilot-instructions.md`).
3. **A clean physical seam.** A single `DataType → PhysicalLayout` query that the columnar and
   row layers consume, with an explicit *unsupported* path.
4. **AOT/trim cleanliness.** No reflection-based serialization; the engine's trim/AOT analyzers
   stay green (ADR-0014).

## The type hierarchy

`DataType` is an immutable, closed (sealed-leaf) hierarchy that consumers may exhaustively
pattern-match:

| Category | Types | Notes |
| --- | --- | --- |
| Atomic singletons (`AtomicType`) | `BooleanType`, `ByteType`, `ShortType`, `IntegerType`, `LongType`, `FloatType`, `DoubleType`, `StringType`, `BinaryType`, `DateType`, `TimestampType`, `NullType` | Each reached via `.Instance`; equal by type identity. |
| Parameterized scalar | `DecimalType(precision, scale)` | A leaf, but parameterized, so it derives from `DataType` directly. |
| Nested | `ArrayType`, `MapType`, `StructType` | Compose child `DataType`s. |

A **schema** is a top-level `StructType` (Spark parity). `StructField(name, dataType, nullable,
metadata)` carries the field name, type, **nullability**, and `FieldMetadata`.

### Nullability lives on the container, not the type

Following Spark, a `DataType` never carries nullability. Instead it lives on
`StructField.Nullable`, `ArrayType.ContainsNull`, and `MapType.ValueContainsNull`. Map *keys*
are always non-null (Spark's `MapType` has no key-null flag).

### `NullType` (`void`)

`NullType` is the type of a bare `NULL` literal. It is a valid member of the type system —
it participates in equality, validation, and serialization — but has **no physical
representation**. It is the concrete case behind the physical-layout *unsupported* path
(below): a column of `void` must be widened to a concrete type before it can be materialized.

## Equality, comparison, and determinism (AC1)

Equality is **structural**: two types are equal iff they have the same shape (e.g. two
`ArrayType`s are equal iff their element types are equal and `ContainsNull` matches; two
`StructType`s iff they have the same fields, **in the same order**, with equal names,
nullability, and metadata). `FieldMetadata` compares **order-independently**.

`GetHashCode` is derived from an internal FNV-1a (`StableHash`) over the type's canonical
discriminators, so it is **stable across processes and runs** — unlike the CLR's randomized
`string.GetHashCode()`. This makes type hashes safe to use in reproducible planning/caching.
`SimpleString` (Spark's `catalogString`, e.g. `struct<id:bigint,name:string>`) is the
deterministic human-readable form used in diagnostics and parity tests.

## Validation (AC2)

Invalid schemas are rejected at construction with a `SchemaValidationException` whose message
names the offending element precisely:

| Rule | Error |
| --- | --- |
| Duplicate struct field names (case-sensitive) | `Duplicate field name '<n>' … (at positions i and j)` |
| Map key type is `void` or a `map` | `Map key type '<t>' is not supported …` |
| Decimal precision outside `[1, 38]` | `Decimal precision <p> is out of range …` |
| Decimal scale outside `[0, precision]` | `Decimal scale <s> is out of range …` |

**Case sensitivity.** Duplicate detection is case-sensitive, so `struct<a:int,A:int>` is a
valid *type* — matching Spark's `StructType`, where case-insensitive ambiguity is resolved (and
errored) at name-resolution time, not at type construction.

**Map keys.** The key-type check is **intentionally stricter than Spark** (whose `MapType`
permits any key type at the type level, treating key suitability as a value/operation-time
concern) because AC2 mandates an explicit "unsupported map key" error. It is **non-recursive**:
a directly `void`/`map` key is rejected, but a key that merely *contains* a `void` (e.g.
`map<array<void>, …>`) is permitted in v1.

## JSON serialization (AC3)

`DataType.ToJson()` / `DataType.FromJson(string)` round-trip the type tree, nullability, and
string metadata using the **Spark-compatible schema JSON** — the same representation Delta
stores in its transaction log. Atomic and decimal types serialize as a JSON string
(`"integer"`, `"decimal(10,2)"`, `"void"`); `array`/`map`/`struct` serialize as objects:

```json
{"type":"struct","fields":[
  {"name":"id","type":"long","nullable":false,"metadata":{}},
  {"name":"tags","type":{"type":"array","elementType":"string","containsNull":false},
   "nullable":true,"metadata":{}}]}
```

Serialization is **deterministic** (metadata keys are emitted in sorted order) and uses the
reflection-free `Utf8JsonWriter`/`JsonDocument` APIs so the engine stays trim/AOT-clean.
Deserialization re-runs all validation (a malformed or invalid document throws
`SchemaValidationException`). For Spark parity on the read side, `FromJson` accepts **both**
`"void"` and the legacy `"null"` spelling for `NullType` (Spark serializes `"void"` but its
parser also accepts `"null"`).

**Metadata scope (v1).** `FieldMetadata` is **string-valued** (Spark's most common field
metadata, e.g. column comments); a non-string metadata value yields an explicit unsupported
error rather than silent data loss. **Delta-log schema interop is explicitly out of v1 scope:**
real Delta schemas carry *numeric/bool* metadata (`delta.columnMapping.id`, identity-column
`start`/`step`), which v1 cannot read. Because `DeltaSharp.Engine` is unshipped, the
`FieldMetadata` value shape can be widened to a typed model later without an external breaking
change; this reshape is tracked in **#330** and lands before the storage lane consumes Delta
logs (EPIC-05).

## Physical-layout seam (AC4)

`DataType.TryGetPhysicalLayout(out PhysicalLayout)` (and the throwing `GetPhysicalLayout()`)
is the seam the columnar and binary-row builders consume. A `PhysicalLayout` is one of:

| Kind | Types | Detail |
| --- | --- | --- |
| `FixedWidth` | boolean/byte (1), short (2), int/float/date (4), long/double/timestamp (8) | `FixedWidthBytes` is the per-value width. |
| `FixedWidth` (decimal) | `decimal(p,s)` | 8 bytes when `p ≤ 18` (`IsCompact`), else 16. |
| `Variable` | string, binary | Offsets buffer + shared byte buffer. |
| `Nested` | array, map, struct | Consumer recurses on the child types. |
| *(none)* | `void` (`NullType`) | `TryGet…` returns `false` (the `out` value has `Kind == None`); `GetPhysicalLayout()` throws `UnsupportedTypeException`. |

This is intentionally a **descriptor**, not a buffer: it tells a builder how to size and shape
storage. Bit-packing of booleans/validity and the exact offset width are implementation choices
of the vector layer (ADR-0002), not of the logical type.

## v1 scope and deferrals

- **In scope:** the type matrix above; equality/validation/serialization; the physical-layout
  query.
- **Deferred (tracked elsewhere):** type **coercion**, decimal/timestamp arithmetic, and ANSI
  overflow rules (STORY-02.5.2, #142); a session-local **`TimestampNtz`** variant (EPIC-02 open
  question); richer **typed metadata** for Delta-log interop (v1 is string-valued; tracked in
  **#330**). `TimestampType` is a UTC-normalized instant (microseconds since epoch).

## References

- [ADR-0008: Type system and internal row/value representation](../../adr/0008-type-system-row-format.md)
- [ADR-0002: In-memory columnar batch format](../../adr/0002-columnar-batch-format.md)
- [EPIC-02: Columnar Memory & Type System](../../planning/epics/EPIC-02-columnar-memory-type-system.md)
- [API governance](api-governance.md) · [Testing conventions](testing-conventions.md)
- Apache Spark `org.apache.spark.sql.types` (`DataType`, `StructType`, `DataTypes`, schema JSON).
