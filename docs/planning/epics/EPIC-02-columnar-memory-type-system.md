# EPIC-02: Columnar Memory & Type System

- **Roadmap milestone:** M1 ([Roadmap](../../../ROADMAP.md))
- **Primary persona(s):** `dotnet-vectorized-columnar-compute-engineer`, `dotnet-runtime-performance-engineer` (+ collaborators `query-execution-engine-engineer`, `developer-experience-api-engineer`, `dotnet-distributed-execution-engineer`)
- **Related ADRs:** ADR-0002, ADR-0013, ADR-0008
- **Depends on:** EPIC-01
- **Status:** draft
- **Size:** XL

## Objective

Establish DeltaSharp's native in-memory representation for Spark-compatible execution: mutable columnar batches, off-heap memory, binary rows, and a Spark SQL type model. This epic turns ADR-0002, ADR-0013, and ADR-0008 into executable foundations for vectorized operators, shuffle/spill paths, and query semantics in M1.

## Scope

**In scope**
- `ColumnVector` and `ColumnBatch` contracts that are mutable, selection-vector-aware, nullable, and independent of direct `Apache.Arrow` operator binding.
- Arrow-backed vectors and Arrow-at-the-edges conversion for initial interop with Arrow IPC, Flight, Parquet bridges, and C Data Interface scenarios.
- Off-heap `NativeMemory` allocation, 64-byte alignment, deterministic ownership, execution/storage pools, per-task budgets, and spill hooks.
- Binary row format compatible with ADR-0008 requirements: 8-byte alignment, null bitset, byte-sortable keys, and shuffle/spill serialization.
- Spark SQL v1 type parity for primitives, decimal, date/timestamp, array, map, struct, ANSI overflow behavior, and SQL three-valued null semantics.

**Out of scope** (and where it lives instead)
- Physical operator implementation and backend dispatch → EPIC-03 / persona `dotnet-vectorized-columnar-compute-engineer`.
- Public DataFrame/Dataset API ergonomics and samples → EPIC-04 / persona `developer-experience-api-engineer`.
- Remote shuffle service protocol and durable shuffle block management → EPIC-09 / persona `dotnet-distributed-execution-engineer`.
- Delta transaction log and Parquet file-format internals → EPIC-05 / persona `delta-storage-format-engineer`.

## Exit criteria

- [ ] `ColumnVector` and `ColumnBatch` support all v1 Spark SQL types, nulls, offsets, mutable output buffers, and selection vectors without operators binding directly to `Apache.Arrow` arrays.
- [ ] Arrow-backed vectors round-trip to Arrow IPC-compatible `RecordBatch` data while preserving type, offset, validity bitmap, and ownership metadata at the engine boundary.
- [ ] Off-heap allocation and spill behavior are verified under per-task memory pressure with 64-byte alignment, deterministic disposal, pool accounting, and no unbounded GC-heap fallback.
- [ ] Binary rows round-trip all v1 fixed-width and variable-width values, preserve null semantics, serialize for shuffle/spill, and sort bytewise according to the documented ordering contract.
- [ ] Type coercion, null behavior, decimal overflow, date/timestamp handling, and nested type validation pass Spark parity tests for the v1 type matrix.

## Features

### FEAT-02.1: Mutable `ColumnVector` and `ColumnBatch` abstraction

- **Objective:** Define the ADR-0002 internal batch API that operators and kernels bind to instead of Arrow arrays. The abstraction must expose mutability, selection vectors, validity bitmaps, offsets, and ownership without leaking implementation details.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `dotnet-runtime-performance-engineer`, `query-execution-engine-engineer`.
- **Depends on:** EPIC-01

#### Stories

##### STORY-02.1.1: Define vector and batch contracts

- **As a** vectorized operator author **I want** stable `ColumnVector` and `ColumnBatch` interfaces **so that** execution kernels can consume batches without depending on Arrow implementation classes.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** EPIC-01
- **Acceptance criteria:**
  - [ ] Given a typed vector for each v1 primitive type, When a kernel requests length, logical offset, nullability, and data span access, Then the contract exposes those values without boxing per row.
  - [ ] Given a batch containing multiple vectors, When the batch is sliced, Then child vectors retain consistent row count, offset, and validity metadata.
  - [ ] Given mutable output vectors, When values and null bits are written, Then subsequent reads observe the written values and no immutable Arrow API is required on the hot path.
  - [ ] Given a future non-Arrow implementation, When it implements the contracts, Then no operator-facing type names require `Apache.Arrow`.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `21` satisfied; docs updated if public API changes.

##### STORY-02.1.2: Add selection-vector-aware batch views

- **As a** vectorized compute engineer **I want** selection-vector-aware views **so that** filters, projections, joins, and aggregates can avoid early row materialization.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** STORY-02.1.1
- **Acceptance criteria:**
  - [ ] Given a batch and a selection vector, When a selected view is created, Then logical row count equals selected cardinality and physical vector buffers are not copied.
  - [ ] Given nested selected views, When selections are composed, Then the resulting view resolves to the same logical rows as applying selections in sequence.
  - [ ] Given an empty, all-selected, and partially selected vector, When kernels enumerate selected rows, Then they produce deterministic row order and no out-of-range accesses.
  - [ ] Given a selected nullable vector, When validity is queried for selected rows, Then the null result matches the underlying validity bitmap at each selected physical index.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `21` satisfied; docs updated if public API changes.

### FEAT-02.2: Arrow-backed vectors and Arrow-at-the-edges interop

- **Objective:** Implement the initial ADR-0002 Arrow-backed storage layer while preserving the internal abstraction boundary. Arrow remains the interop substrate for IPC, Flight, Parquet bridges, and external boundaries, not the operator contract.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `delta-storage-format-engineer`, `data-platform-connectors-engineer`.
- **Depends on:** FEAT-02.1

#### Stories

##### STORY-02.2.1: Implement Arrow-backed `ColumnVector`

- **As a** storage bridge implementer **I want** Arrow-backed `ColumnVector` implementations **so that** DeltaSharp can reach working columnar execution quickly while preserving swappable hot-path vectors.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `delta-storage-format-engineer`.
- **Size:** M. **Depends on:** STORY-02.1.1
- **Acceptance criteria:**
  - [ ] Given an Arrow primitive array with a non-zero offset, When it is wrapped as a `ColumnVector`, Then value and validity access account for the Arrow offset correctly.
  - [ ] Given Arrow validity bitmaps, When nulls are read through the vector contract, Then null positions match Arrow bit ordering for all tested offsets.
  - [ ] Given an Arrow-backed vector used as immutable input, When a mutable output vector is requested, Then writes target DeltaSharp-owned buffers rather than mutating Arrow arrays.
  - [ ] Given unsupported Arrow features for v1, When conversion is attempted, Then a precise unsupported-type error is returned instead of silent data loss.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `17` satisfied; docs updated if public API changes.

##### STORY-02.2.2: Add Arrow boundary conversion tests

- **As a** connector engineer **I want** verified Arrow conversion boundaries **so that** IPC, Flight, and Parquet bridges preserve DeltaSharp type and null metadata.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `data-platform-connectors-engineer`.
- **Size:** S. **Depends on:** STORY-02.2.1, FEAT-02.5
- **Acceptance criteria:**
  - [ ] Given a v1 primitive, decimal, date, timestamp, and nested schema, When a batch converts to Arrow and back, Then the schema and values are unchanged.
  - [ ] Given all-null, no-null, and mixed-null Arrow arrays, When conversion round-trips, Then validity bitmaps and null counts are preserved.
  - [ ] Given sliced Arrow arrays, When converted to DeltaSharp vectors, Then logical row order and offset handling are preserved.
  - [ ] Given ownership transfer at an interop boundary, When the batch is disposed, Then buffers are released exactly once according to the documented ownership mode.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `17` satisfied; docs updated if public API changes.

### FEAT-02.3: Unified off-heap memory manager and spill hooks

- **Objective:** Implement ADR-0013 off-heap memory management with 64-byte-aligned `NativeMemory`, execution/storage pools, per-task budgets, accounting, and spill hooks. The manager must bound executor memory use and cooperate with distributed spill paths.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-distributed-execution-engineer`, `dotnet-vectorized-columnar-compute-engineer`.
- **Depends on:** FEAT-02.1

#### Stories

##### STORY-02.3.1: Implement aligned off-heap allocation ownership

- **As a** runtime performance engineer **I want** deterministic aligned native-buffer ownership **so that** columnar batches and binary rows avoid LOH pressure while remaining safe to dispose.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** M. **Depends on:** STORY-02.1.1
- **Acceptance criteria:**
  - [ ] Given allocation requests for vector buffers and row buffers, When memory is allocated, Then each base address is 64-byte aligned and the requested usable byte count is available.
  - [ ] Given successful and failing allocation paths, When ownership objects are disposed, Then native memory is released exactly once and leak counters return to zero.
  - [ ] Given an exception during vector construction, When cleanup runs, Then all already allocated native buffers are reclaimed.
  - [ ] Given small scratch allocations below the configured threshold, When GC-heap fallback is used, Then the allocation is accounted separately and never used for large batch buffers.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `22` satisfied; docs updated if public API changes.

##### STORY-02.3.2: Enforce execution/storage pools and spill triggers

- **As a** task runtime owner **I want** per-task memory budgets with spill triggers **so that** execution remains bounded under memory pressure.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Size:** L. **Depends on:** STORY-02.3.1
- **Acceptance criteria:**
  - [ ] Given execution and storage pool limits, When allocations are charged, Then available bytes, used bytes, and task ownership are reported accurately.
  - [ ] Given a task that exceeds its execution budget, When spillable reservations exist, Then the manager invokes spill callbacks before rejecting the allocation.
  - [ ] Given insufficient memory after spilling, When an allocation is retried, Then a deterministic budget-exceeded error includes task id, requested bytes, and pool state.
  - [ ] Given concurrent tasks with separate budgets, When one task spills, Then another task's accounting is not decremented or leaked.
  - [ ] Given local disk and object-store spill targets supplied by later runtime layers, When a spill callback succeeds, Then the manager releases the corresponding memory reservation.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `21`, `22` satisfied; docs updated if public API changes.

### FEAT-02.4: Binary row format for sort, shuffle, and spill

- **Objective:** Implement the ADR-0008 `UnsafeRow` analog for key materialization, shuffle/spill serialization, and sort ordering. The format must be compact, 8-byte aligned, null-bitset based, and compatible with the v1 type system.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`, `query-execution-engine-engineer`.
- **Depends on:** FEAT-02.3, FEAT-02.5

#### Stories

##### STORY-02.4.1: Define and implement binary row layout

- **As a** runtime engineer **I want** an 8-byte-aligned binary row layout **so that** shuffle keys, join keys, and materialized rows can be stored compactly off heap.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** L. **Depends on:** STORY-02.3.1, STORY-02.5.1
- **Acceptance criteria:**
  - [ ] Given a schema with fixed-width, variable-width, and nullable fields, When a row is encoded, Then the null bitset, fixed region, variable region, and total size are 8-byte aligned.
  - [ ] Given null and non-null values in every field position, When the row is decoded, Then decoded values match the source values and null bits match the source nulls.
  - [ ] Given nested array, map, and struct values in the v1 subset, When encoded and decoded, Then element order, key/value pairing, and nested nulls are preserved.
  - [ ] Given off-heap row buffers, When ownership transfers to shuffle or spill serialization, Then disposal responsibility is explicit and double-free tests pass.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `21` satisfied; docs updated if public API changes.

##### STORY-02.4.2: Implement byte-sortable ordering and serialization

- **As a** query execution engineer **I want** binary rows to sort and serialize deterministically **so that** sort, join, spill, and shuffle paths share one row representation.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** STORY-02.4.1
- **Acceptance criteria:**
  - [ ] Given encoded rows for supported sort-key types, When bytewise comparison is used, Then ordering matches the documented Spark-compatible ascending/null ordering for the configured key directions.
  - [ ] Given rows containing decimals, timestamps, NaN, negative values, and nulls, When sorted, Then the result matches the scalar comparator oracle.
  - [ ] Given a row serialized to spill storage and read back, When decoded, Then values, nulls, and schema version metadata match the original row.
  - [ ] Given malformed or truncated row bytes, When deserialization runs, Then it fails with a bounded validation error and does not read outside the buffer.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `21`, `22` satisfied; docs updated if public API changes.

### FEAT-02.5: Spark SQL v1 type system and ANSI semantics

- **Objective:** Define Spark SQL-compatible types and semantic rules from ADR-0008 for primitives, decimal, date/timestamp, and complex structures. The type system becomes the shared contract for vectors, rows, expressions, analyzer rules, and public API surfaces.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `developer-experience-api-engineer`, `dotnet-vectorized-columnar-compute-engineer`.
- **Depends on:** EPIC-01

#### Stories

##### STORY-02.5.1: Implement v1 type descriptors and schema model

- **As a** query engine engineer **I want** canonical type descriptors and schemas **so that** vectors, rows, expressions, and APIs agree on value shape and nullability.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `developer-experience-api-engineer`.
- **Size:** M. **Depends on:** EPIC-01
- **Acceptance criteria:**
  - [ ] Given v1 primitive, decimal, date, timestamp, array, map, and struct definitions, When schemas are constructed, Then type equality, nullability, field names, and metadata compare deterministically.
  - [ ] Given invalid schemas such as duplicate struct fields or unsupported map keys, When validation runs, Then precise validation errors are returned.
  - [ ] Given schema serialization for test fixtures, When serialized and deserialized, Then the same type tree and nullability flags are produced.
  - [ ] Given vector and binary-row builders, When they request physical layout for a type, Then the type descriptor returns a supported physical representation or an explicit unsupported error.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `20` satisfied; docs updated if public API changes.

##### STORY-02.5.2: Implement coercion, decimal, timestamp, and ANSI overflow rules

- **As a** Spark-compatible query planner **I want** deterministic coercion and ANSI semantic rules **so that** DeltaSharp matches Spark behavior for v1 expressions and schema validation.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `developer-experience-api-engineer`.
- **Size:** L. **Depends on:** STORY-02.5.1
- **Acceptance criteria:**
  - [ ] Given numeric operands with different widths, When coercion is requested, Then the selected common type matches the Spark parity matrix for v1 numeric types.
  - [ ] Given decimal operations that exceed configured precision or scale, When ANSI mode is enabled, Then the operation reports overflow instead of truncating silently.
  - [ ] Given date and timestamp values at boundary instants, When cast and comparison rules are evaluated, Then results match Spark parity fixtures for time zone and precision assumptions documented for v1.
  - [ ] Given unsupported coercions for nested types, When analysis runs, Then errors identify the source type, target type, and expression path.
  - [ ] Given null inputs to coercion-sensitive expressions, When evaluated, Then the result follows SQL null propagation rather than CLR default values.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `21` satisfied; docs updated if public API changes.

### FEAT-02.6: Null and validity model for columnar compute

- **Objective:** Make Arrow-compatible validity bitmaps and SQL three-valued logic first-class across vectors, rows, and kernels. The model must enable branchless null handling where safe while preserving correctness for null propagation and null-aware comparisons.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `query-execution-engine-engineer`, `dotnet-runtime-performance-engineer`.
- **Depends on:** FEAT-02.1, FEAT-02.5

#### Stories

##### STORY-02.6.1: Specify validity bitmap and null propagation contracts

- **As a** kernel author **I want** a single validity bitmap contract **so that** scalar, SIMD, Arrow-backed, and future off-heap vectors agree on null positions.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** STORY-02.1.1, STORY-02.5.1
- **Acceptance criteria:**
  - [ ] Given a vector with no validity bitmap, When null status is queried, Then the vector is treated as no-null without allocating a synthetic bitmap.
  - [ ] Given a vector with a validity bitmap and logical offset, When any row's null status is queried, Then the bit index uses Arrow-compatible bit ordering plus the logical offset.
  - [ ] Given unary and binary expressions, When null propagation rules are applied, Then output validity matches SQL three-valued logic fixtures.
  - [ ] Given all-null, no-null, empty, and sliced vectors, When bitmap utilities compute null counts, Then counts are correct and deterministic.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `15` satisfied; docs updated if public API changes.

##### STORY-02.6.2: Add branchless null helper primitives

- **As a** vectorized compute engineer **I want** reusable null-aware helper primitives **so that** kernels can combine validity, selection vectors, and predicate results without branch-heavy per-row code.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `dotnet-runtime-performance-engineer`.
- **Size:** M. **Depends on:** STORY-02.6.1
- **Acceptance criteria:**
  - [ ] Given input validity bitmaps for unary and binary kernels, When validity is combined, Then output bitmap bits match scalar reference results for all tested offsets and tail lengths.
  - [ ] Given a selection vector, When validity helpers operate on selected rows, Then only selected physical rows contribute to the output bitmap.
  - [ ] Given hardware popcount support and scalar fallback environments, When null counts are computed, Then both paths return identical counts.
  - [ ] Given null density benchmark cases, When helper primitives are measured, Then allocations remain zero on the hot path and benchmark metadata records batch size and null density.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `22` satisfied; docs updated if public API changes.

## Open questions

- Should v1 timestamp semantics standardize on UTC-normalized physical storage only, or include a session-local timestamp variant before SQL frontend work begins?
- What minimum spill-storage abstraction is required in M1 so ADR-0013 can prove object-store spill hooks without depending on the full remote shuffle service?
- Which nested type encodings must be implemented in custom off-heap vectors before the Arrow-backed implementation becomes a performance bottleneck?
