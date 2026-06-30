# Relational operators: aggregate, sort, join, exchange-local (v1)

> **Status:** living document. Created with
> [STORY-03.2.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-03-vectorized-execution-backend.md#story-0322-implement-aggregate-sort-join-and-exchange-local-operators)
> (the first executable relational operators in the vectorized interpreter). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (pluggable vectorized interpreter + optional JIT
> codegen tier), [ADR-0002](../../adr/0002-columnar-batch-format.md) (Arrow-compatible columnar
> batch), [ADR-0004](../../adr/0004-shuffle-architecture.md) (native remote shuffle — the local
> exchange is its in-process seam), and [ADR-0014](../../adr/0014-target-framework-aot.md) (NativeAOT
> executor). Extends [execution-operators.md](execution-operators.md) (the typed operator/metrics
> contract) and reuses [byte-sortable-ordering.md](byte-sortable-ordering.md) (the key encoding),
> [interpreted-expression-evaluator.md](interpreted-expression-evaluator.md) (key/argument evaluation),
> and [memory-model.md](memory-model.md) (reserve-before-allocate). Update it whenever an operator's
> algorithm, semantics, memory discipline, or deferred list changes.

These four operators turn the v1 physical-plan **shapes** from STORY-03.1.1 into running relational
queries — a query can now `GROUP BY`, `ORDER BY`, `JOIN`, and repartition without the compiled tier.
Everything here lives in the unshipped `DeltaSharp.Engine` assembly under
`src/DeltaSharp.Engine/Execution/`; `public` is an engine-internal seam, not shipped surface. The
operators are **correctness-first vectorized**: keys and aggregate arguments are evaluated one column
at a time per batch (vectorized), then folded row-by-row into compact per-group/per-row state. They
are the AOT-clean ground truth the compiled tier must match (ADR-0001); none uses reflection-emit,
`Expression.Compile`, or any `[RequiresDynamicCode]` path.

---

## 1. Where these operators sit

| File | Operator | Class of operator |
| --- | --- | --- |
| `InterpretedAggregateStream.cs` (+ `Aggregators.cs`, `AggregateExpression.cs`) | `Aggregate` | pipeline breaker (blocking) |
| `InterpretedSortStream.cs` | `Sort` | pipeline breaker (blocking) |
| `InterpretedJoinStream.cs` | `Join` | half-blocking (build side blocks, probe streams) |
| `InterpretedExchangeLocalStream.cs` | `ExchangeLocal` | streaming (one input batch in flight) |

Shared machinery: `RowKeyProjection.cs` (key evaluation + canonical encoding), `RowKey.cs` (the
hash-table key + FNV-1a), `KeyBoxing.cs` (storage → `RowData` CLR shape), `ScalarValues.cs`
(MIN/MAX comparison in storage shape), `RowSizeEstimate.cs` (coarse per-row byte estimate for
reservations). The `InterpretedOperators` dispatch (`Supports` + `Open`) wires all four; the
`CompiledBackend` **delegates** operator open to `InterpretedOperators` (it only fuses expressions),
so these operators light up on both backends from one implementation — see §7.

---

## 2. Shared foundations

### 2.1 The pull-based streaming contract

Every operator is an `IBatchStream`: `Open` builds plans and opens children but does **no row work**;
the first `TryGetNext` drives execution; `Dispose` releases reservations and the child(ren). This is
the laziness invariant — *transformations are lazy, actions are eager*. Concretely:

- **Lazy.** `OpenAggregate`/`OpenSort`/`OpenJoin`/`OpenExchangeLocal` build the `RowKeyProjection`,
  aggregators, and schema validations, then open children; they never pull. The blocking operators
  drain their input inside `EnsureBuilt()` on the **first** `TryGetNext`. Tests assert
  `op.Metrics.InputRows == 0` immediately after `Open` and `> 0` only after the first pull.
- **Cancellation at bounded checkpoints.** `TryGetNext` calls `ThrowIfCancellationRequested()` on
  entry; the build loops re-check the token at the top of every input-batch iteration; the join's
  probe re-checks on each `AdvanceLeftBatch`. So a signalled token stops execution at the next batch
  boundary (not mid-row) and `Dispose` then releases buffers — satisfying the cancellation AC.
- **Self-attributed timing.** Each operator times only its **own** work: it samples
  `Stopwatch.GetTimestamp()` *after* the child pull returns and adds `ElapsedNanos` for the slice it
  computed, so child time is not double-counted up the tree.
- **Metrics.** `InputRows`, `OutputRows`, `PeakMemoryBytes` are populated by all four; `ShuffleBytes`
  additionally by exchange-local. Output is emitted in **≤ 1024-row** chunks (`OutputBatchRows`) so a
  huge group set, sort run, or join fan-out flows incrementally and stays bounded.

### 2.2 The key-encoding choice — one canonical byte-sortable encoding

All four operators need a **key**: group keys (aggregate), sort keys (sort), join keys (join),
partition keys (exchange). Rather than maintain a second typed-hash key path, every operator funnels
keys through `RowKeyProjection`, which evaluates the key expressions with the STORY-03.4.1 expression
evaluator and encodes each row into the **single canonical byte-sortable encoding** from EPIC-02
(`SortKeyEncoder` / `ByteSortableEncoding`; see [byte-sortable-ordering.md](byte-sortable-ordering.md)).
`RowKey` then wraps those bytes with bytewise `Equals` and an FNV-1a hash.

Why one encoding for both equality and ordering:

- **Equality consumers (aggregate, join, exchange)** pass `orderings = null`, which selects a **fixed
  ascending / nulls-first** encoding. Direction and null placement are irrelevant when two encodings
  are only compared for byte-*equality*, so fixing them keeps grouping/join/exchange on the exact
  encoding STORY-02.4.2 proved equal to `RowOrderingComparer`. Two rows collide in a group/build
  bucket **iff** Spark treats their keys as equal — including the float canonicalization the encoder
  bakes in: every `NaN` maps to one bit pattern and `-0.0` folds to `+0.0` (Spark **SPARK-26021**).
  NULLs get a distinct present/absent marker, so a null key never byte-equals a present key.
- **The sort consumer** passes one `SortKeyOrdering` per key (mapped from `SortOrder` in
  `OpenSort`), so a plain ascending `memcmp` of the encodings realizes the requested
  asc/desc × nulls-first/last order across all keys with no per-comparison branching. This is the
  whole point of a byte-sortable encoding: ordering becomes `Span<byte>.SequenceCompareTo`.

**Why FNV-1a, not Murmur3.** `RowKey.GetHashCode` and the exchange partitioner both use 32-bit FNV-1a
over the canonical bytes. This is deliberately **not** Spark's Murmur3 partition hash: the local
exchange only needs a deterministic, well-spread, cheap assignment, and the seam that becomes a
network shuffle ([ADR-0004](../../adr/0004-shuffle-architecture.md), STORY-03.5.x) can adopt Murmur3
there without touching these operators. Documented as a deviation in §8.

**Allocation cost (documented).** Encoding boxes each key value (`KeyBoxing`, storage CLR shape →
`RowData` shape) into a reused scratch buffer and allocates one right-sized `byte[]` per row that
escapes into the dictionary/sort buffer. That per-row key array is the v1 correctness-first cost; a
zero-box typed-hash key path is deferred behind this same `RowKeyProjection` contract (§8).

### 2.3 The memory model — reserve before allocate, spill deferred

Per [memory-model.md](memory-model.md) and [execution-operators.md](execution-operators.md), every
operator **reserves before it allocates or mutates** accumulator/buffer/output state via
`context.Memory.TryReserve`. A refusal throws the typed `ExecutionMemoryException`
(`requested`, `available`, `budget`, detail) — it never silently spills, because **spill is deferred
to STORY-03.5.x**. After each successful reserve the operator rolls its high-water mark via
`Metrics.ObservePeakMemory`. Reservation **lifetime** differs by operator class and is the key design
decision:

| Operator | What is reserved | Held until | Why |
| --- | --- | --- | --- |
| Aggregate | accumulator state + output columns + key bytes per **new group** | `Dispose` | the result rows are live until the consumer finishes draining |
| Sort | per-row row bytes + key bytes for **every** buffered row; plus one in-flight chunk's `int[]` selection | buffer → `Dispose`; chunk → next pull | a total sort must see all rows before emitting any |
| Join | per-row build bytes + key bytes for every **build (right)** row | build → `Dispose`; output chunk → next pull | the build hash table is probed for the whole left stream |
| Exchange-local | one input batch's assignment + bucket `int[]`s (~2 ints/row) | next input batch (released in `AdvanceInput`) | only one input batch is in flight at a time |

Blocking operators hold their main reservation to `Dispose` because their materialized state stays
live; streaming/chunked reservations are released at the **top of the next** `TryGetNext` (or when
advancing the input), so steady-state footprint is one batch, not the whole stream. Every operator's
`Dispose` releases any residual reservation and the child(ren), so a cancellation or exception unwinds
cleanly with no leak.

### 2.4 The columnar / vectorized evaluation model

The per-batch shape is identical across operators and is where "vectorized" lives:

1. Pull a child `ColumnBatch`.
2. **Once per batch**, evaluate the key columns (`RowKeyProjection.Evaluate` → `ColumnVector[]`) and,
   for aggregate, each aggregate-argument column (`ExpressionEvaluator.Evaluate` → `ColumnVector`).
   Evaluation uses a `BatchEvaluationMemory` scratch that is **released in a `finally`** after the
   batch is consumed.
3. Walk the batch's logical rows, reading already-evaluated lanes — no per-row expression
   interpretation. Output columns are built with `MutableColumnVector` and `VectorMaterializer`
   (typed `CopyValue`/`AppendIntegral`/`AppendDecimal`), preserving Arrow-compatible storage shape.

Sort and exchange go further and emit **zero-copy** output: instead of copying values, they hand back
a `ColumnBatch.WithSelection(SelectionVector)` view over the buffered/own input columns (a permutation
for sort, a partition's row indices for exchange). Aggregate must materialize (its output rows are new
group rows), and join must materialize (its output rows splice left and right columns), so those copy.

---

## 3. Aggregate operator

### 3.1 Algorithm and data structures

`InterpretedAggregateStream` is a classic in-memory **hash aggregate** with a degenerate single-group
path for global (no-key) aggregates:

- **Group table:** `Dictionary<RowKey, int>` mapping a canonical key encoding → a dense group ordinal
  `0..groupCount`.
- **Key columns:** `MutableColumnVector[keyCount]`, one per grouping key, appended in group-discovery
  order — these become the leading output columns. When a new group is discovered, each key lane is
  copied from the evaluated key vector (`VectorMaterializer.CopyValue`, or `AppendNull`).
- **Aggregate state:** an `Aggregator[]`, one per aggregate term, each owning a dense
  per-group array indexed by group ordinal. `EnsureCapacity(groupCount)` grows arrays by amortized
  doubling as ordinals appear.
- **Result:** `BuildResult()` emits each aggregator's finished value per group into
  `MutableColumnVector[aggregateCount]`, concatenates `[key columns | aggregate columns]` into one
  `ManagedColumnBatch`, and `TryGetNext` slices it into ≤ 1024-row output batches.

The build (`EnsureBuilt`) runs once on the first pull: for a global aggregate it pre-creates group 0
(so empty input still has a group), then for each input batch evaluates argument + key vectors and,
per row, resolves the group ordinal (`ResolveGroup` — dictionary lookup, or reserve-then-insert) and
calls `Accumulate(group, argumentVector, row)` on every aggregator.

### 3.2 The aggregator hierarchy

`Aggregator.Create(AggregateExpression, ...)` is the factory; each concrete aggregator declares its
`BytesPerGroup` (used for reservation) and implements `Accumulate`/`Emit`:

| Aggregator | Function | Buffer | Null handling |
| --- | --- | --- | --- |
| `CountAggregator` | `COUNT(*)`, `COUNT(x)` | `long` | `COUNT(*)` passes a **null argument vector** and counts every row; `COUNT(x)` counts only non-null lanes |
| `SumLongAggregator` | `SUM` of integral | checked `long` | skips nulls; empty/all-null group → NULL |
| `SumDoubleAggregator` | `SUM` of float/double | `double` | skips nulls; empty/all-null group → NULL |
| `SumDecimalAggregator` | `SUM` of decimal | `DecimalValue` | skips nulls; `DecimalValue.Add`; `ToType` to the widened result type |
| `AvgDoubleAggregator` | `AVG` of integral/float/double | `double` sum + `long` count | skips nulls; count 0 → NULL |
| `MinMaxAggregator` | `MIN`/`MAX` of any orderable | boxed best (storage shape) | skips nulls; never-set group → NULL |

### 3.3 Exact null + ANSI semantics (Spark parity)

These follow Spark SQL's aggregate semantics; each row cites the rule it mirrors.

- **`COUNT(*)` counts all rows including nulls; `COUNT(x)` counts non-null `x`.** `CountAggregator`
  increments when `input is null` (the `*` case, no argument) **or** the lane is non-null. An empty
  group counts 0 — `COUNT` is the one aggregate that is **non-nullable** (`LongType`), so it never
  emits SQL NULL.
- **`SUM`/`AVG`/`MIN`/`MAX` skip NULL inputs.** Each aggregator early-returns on `input.IsNull(row)`.
- **`SUM`/`AVG`/`MIN`/`MAX` over an empty or all-null group is SQL NULL** (not 0). Implemented by a
  "has a value?" flag (`_hasValue`/`_counts==0`/boxed-best `null`) consulted at `Emit`. This is the
  classic SQL distinction tested directly: a grouped all-null column yields `COUNT(*) = rows`,
  `COUNT(x) = 0`, and `SUM = AVG = MIN = MAX = NULL`.
- **Global aggregate over empty input emits exactly one row** (`COUNT(*) = 0`, others NULL); a
  **grouped** aggregate over empty input emits **zero rows**. Implemented by pre-creating group 0 only
  when `keyEncoder is null`.
- **Null group keys form their own group.** `ResolveGroup` keeps the null marker in the encoding and
  copies `AppendNull` into the key column, so `GROUP BY k` with null `k` produces one null-keyed group
  (Spark groups nulls together) — distinct from join, which drops null keys.
- **NaN / `-0.0` group canonicalization.** Because the key is the byte-sortable encoding, a
  `GROUP BY` on a double column places all `NaN`s in one group and `0.0`/`-0.0` in one group
  (SPARK-26021), proven by a dedicated test.

**ANSI overflow.** `SUM` of integrals accumulates in a **checked** `long`; on `OverflowException`,
`AnsiMode.Ansi` rethrows as `ArithmeticOverflowException` (the EPIC-02 ANSI contract), while
`AnsiMode.Legacy` **nulls the group** and continues. `SUM` of decimals accumulates with
`DecimalValue.Add` (throws `ArithmeticOverflowException` on `Int128` overflow) and fits the running
sum to the widened result type with `ToType(resultType, mode)` — ANSI throws, Legacy nulls. The
`AnsiMode` is carried on each `AggregateExpression`. Float/double `SUM`/`AVG` follow IEEE-754 and
**never** raise overflow (they saturate to ±∞), matching Spark.

### 3.4 Result-type widening (Spark parity)

Resolved in `AggregateExpression`'s constructor so the plan is well-typed before execution:

- `COUNT` → non-null `bigint`.
- `SUM(integral)` → `bigint`; `SUM(float|double)` → `double`;
  `SUM(decimal(p,s))` → `decimal(min(38, p+10), s)`.
- `AVG(integral|float|double)` → `double` (Spark accumulates non-decimal averages in a double buffer,
  which is exactly what `AvgDoubleAggregator` does — sum and count in `double`/`long`, then divide);
  `AVG(decimal(p,s))` → `decimal(min(38, p+4), min(38, s+4))` — **type resolved but execution
  deferred** (§8): `Aggregator.Create` throws `UnsupportedOperatorException` for `AVG(decimal)`
  because it needs the decimal-divide rounding the type system also defers.
- `MIN`/`MAX` → the input type (must be orderable; nested/void rejected at construction).

`OpenAggregate` cross-checks each grouping-key type against output field `k` and each aggregate's
resolved `Type` against output field `keyCount + i` before building anything, so a malformed plan
fails fast with `ArgumentException` at `Open`.

### 3.5 Memory

`BytesPerGroup` (sum across aggregators) + a coarse output-row estimate + the key byte length are
reserved **per newly discovered group**, before the group is inserted, so a refusal leaves the build
consistent. The whole reservation is held until `Dispose` (the result batch is live during draining).
A tiny budget makes the first group's reserve throw `ExecutionMemoryException` — tested.

---

## 4. Sort operator

### 4.1 Algorithm and data structures

`InterpretedSortStream` is an in-memory **total sort** via an indirect permutation sort over
byte-sortable keys:

1. **Buffer.** `EnsureBuilt` drains the child, copying every input row into a row-major
   `MutableColumnVector[]` (`ColumnVectors.CreateForSchema`) and storing each row's **encoded sort
   key** (`byte[]`) in a parallel `List<byte[]> _keys`. Schema is unchanged (sort is a reorder).
2. **Permute.** Build `_order = [0,1,…,n)`, then `Array.Sort(_order, CompareOrdinals)` where
   `CompareOrdinals(a,b) = _keys[a].SequenceCompareTo(_keys[b])`, tie-broken by `a.CompareTo(b)` (the
   input ordinal). The byte compare is a plain ascending `memcmp` because the encoding already folded
   direction and null placement into the bytes (§2.2).
3. **Emit.** `TryGetNext` slices `_order` into ≤ 1024-index windows and returns
   `_bufferBatch.WithSelection(new SelectionVector(_order.AsSpan(cursor, length)))` — a **zero-copy**
   reordered view, no value copy on the output path.

### 4.2 Comparator parity and the ordering matrix

The sort's correctness rests on **parity by construction**: STORY-02.4.2 proved the `SortKeyEncoder`
byte order equals the `RowOrderingComparer` oracle. Because the operator sorts purely by
`SequenceCompareTo` over those encodings, its output order **is** the comparator's order, for free,
across:

- **NULLs** — placed first or last per the requested `NullOrdering`, independent of direction
  (Spark allows `NULLS FIRST`/`NULLS LAST` on either `ASC`/`DESC`).
- **NaN** — sorts as the **largest** double/float (Spark's total order), and `-0.0`/`+0.0` compare
  equal (SPARK-26021).
- **decimals** — exact ordering across precision/scale.
- **timestamps / dates** — ordered as their integral representation.
- **multi-key** — a single concatenated encoding orders lexicographically, so
  `k1 ASC NULLS FIRST, k2 DESC NULLS LAST` Just Works.

Tests run the operator across the full `{ASC, DESC} × {NULLS FIRST, NULLS LAST}` matrix and per type,
asserting the emitted row identity order equals an independent `RowOrderingComparer` permutation.

### 4.3 Stability / determinism

The `a.CompareTo(b)` tie-break makes the sort **stable**: equal-key rows keep input order, and the
result is deterministic regardless of `Array.Sort`'s internal pivoting. Tested with all-equal keys
(output = input order) and across multiple input batches (global total order with stable ties).

### 4.4 Memory

Per buffered row, the row-byte estimate + key length is reserved **before** the row is stored
(refusal leaves the buffer consistent), held until `Dispose`. Each emitted chunk additionally reserves
`length * sizeof(int)` for its selection window and **releases the previous chunk's** reservation at
the top of the next pull, so only one in-flight chunk's selection is charged. Spill of the sort buffer
is deferred to STORY-03.5.x; until then an over-budget sort fails fast.

---

## 5. Join operator

### 5.1 Algorithm and data structures

`InterpretedJoinStream` is a **hash join**. Per the `JoinOperator` stub the **build side is the right
input** and the **probe side is the left input**:

- **Build (right), blocking.** `EnsureBuilt` drains the right child into a row-major
  `MutableColumnVector[]` and a `Dictionary<RowKey, List<int>>` mapping key → the list of build-row
  ordinals with that key (the `List<int>` carries **multiplicity**). **Null keys are buffered but
  never indexed** (`anyNull` rows are skipped from the table) so RIGHT/FULL OUTER can still emit them
  unmatched, but they never match a probe row. A `bool[] _matched` tracks which build rows were hit.
- **Probe (left), streaming + resumable.** `FillChunk` pulls left batches and, per left row,
  `InitializeLeftRow` encodes the left key and looks up its build-match list (null left key →
  no match). A per-left-row plan sets `_currentMatches` (build ordinals to splice), `_pendingNullRight`
  (emit left + null-right), or `_pendingLeftOnly` (semi/anti). `EmitLeftRow` walks the matches,
  resuming from `_matchPos` when a previous chunk filled at 1024 rows mid-row — the **resumable state
  machine** is what lets join fan-out exceed one batch safely.
- **Unmatched-build pass.** After the probe is exhausted, RIGHT/FULL OUTER walk `_matched` and emit
  each unmatched build row null-padded on the left (`EmitUnmatchedBuild`, cursor `_unmatchedCursor`).

Output rows are assembled by splicing: left columns from the current left batch, right columns from
the buffered build columns (`AppendValueOrNull`), into a fresh `ManagedColumnBatch` per ≤ 1024-row
chunk.

### 5.2 Join-type matrix — what lands in v1

All six `JoinType` cases the stub declares are implemented in v1:

| `JoinType` | Output columns | Behavior |
| --- | --- | --- |
| `Inner` | left ++ right | every matched (left, build) pair |
| `LeftOuter` | left ++ right | matches; unmatched left → null right |
| `RightOuter` | left ++ right | matches; unmatched build → null left (post-probe pass) |
| `FullOuter` | left ++ right | matches; unmatched left → null right; unmatched build → null left |
| `LeftSemi` | left only | each left row with ≥ 1 match, once |
| `LeftAnti` | left only | each left row with no match |

`OpenJoin` validates the output schema is left++right (inner/outer) or left-only (semi/anti) and that
each output field's **type** matches the corresponding input field, failing fast with
`ArgumentException` otherwise. **Sort-merge join is deferred** (§8): v1 ships the single hash-join
strategy, which already satisfies the inner/left/right/full multiplicity and null-key fixtures the AC
requires; physical strategy *selection* (broadcast vs shuffle hash vs SMJ) is a later planner concern.

### 5.3 Null-key semantics (Spark parity)

SQL equi-join treats `NULL = NULL` as **not true**, so **null keys never match**. The build never
indexes null-keyed rows and the probe never matches a null left key. The observable consequences,
all tested:

- `Inner`: null-keyed rows on either side are dropped.
- `LeftOuter`: a null-keyed left row emits once with a null right (it is "unmatched").
- `RightOuter`/`FullOuter`: a null-keyed build row emits once null-padded on the left.
- `LeftAnti`: a null-keyed left row is in the anti output (it has no match).

### 5.4 Memory

Per build row, the build-row-byte estimate + key length is reserved before buffering; the build buffer
+ hash table are held until `Dispose` (probed for the whole left stream). Each emitted output chunk
reserves its output-row bytes and **releases the previous chunk** at the top of the next pull. The
left batch's evaluation scratch (`_leftScratch`) is released when advancing left batches and in
`Dispose`. A tiny budget makes the build's first reserve throw `ExecutionMemoryException` — tested.
Build-side spill / a grace-hash fallback is deferred to STORY-03.5.x.

### 5.5 Metrics

`InputRows` counts **both** sides (build rows during `EnsureBuilt`, left rows as they are pulled), so
a 4-left × 3-right join reports `InputRows = 7`; `OutputRows` is the emitted row count; peak memory is
build reservation + the in-flight output chunk.

---

## 6. Exchange-local operator

### 6.1 Algorithm and data structures

`InterpretedExchangeLocalStream` performs a **local hash repartition** — the in-process boundary a
future remote shuffle ([ADR-0004](../../adr/0004-shuffle-architecture.md), STORY-03.5.x / STORY-03.6)
builds on. It is **streaming**: one input batch is in flight at a time.

For each non-empty input batch (`AdvanceInput`):

1. `Partition` reserves `~2 * rows * sizeof(int)`, then `ComputeAssignment` fills a per-row
   `assignment[r]` partition id and per-partition `counts[]`.
2. Bucket `int[][]` arrays are allocated (`buckets[p]` has length `counts[p]`) and filled with the row
   indices routed to each partition — these are the per-partition selections.
3. `TryGetNext` emits **exactly `PartitionCount` output batches per input batch**, in partition-id
   order `0..N-1` **including empty partitions**, each a zero-copy
   `input.WithSelection(new SelectionVector(buckets[p]))` view. Output batch `i` therefore *is*
   partition `i mod N`.

### 6.2 The positional partition contract (a deliberate v1 decision)

The `ColumnBatch` contract has **no partition tag** yet (the remote-shuffle story adds one), so
partition identity is conveyed **positionally**: per input batch, the operator emits N batches in id
order. This is why empty partitions are emitted as empty batches — dropping them would make the
position no longer equal the partition id. To avoid flooding the stream with N empties for nothing,
**empty input batches are skipped** (they route no rows).

### 6.3 Assignment, preservation, and Spark deviations

- **Keyed:** `partition = FNV-1a(canonicalKeyBytes) mod N`. Equal keys always co-locate (same bytes →
  same hash → same partition), and the assignment is deterministic across runs and across backends.
- **Keyless:** rows are assigned **round-robin starting at partition 0** (`0,1,…,N-1,0,…`).
- **Preservation:** each row lands in exactly one partition, so concatenating an input batch's N
  output batches reproduces the input rows (a multiset) — no loss, no duplication. Null-keyed rows are
  routed (hashed via the null marker), never dropped.
- **Deviations from Spark (documented):** FNV-1a not Murmur3 (§2.2); round-robin **starts at 0**
  whereas Spark randomizes the starting partition for round-robin repartition. Both are acceptable for
  an in-process seam and are the remote-shuffle story's to revisit.

### 6.4 Memory + metrics

One input batch's assignment/bucket arrays are reserved before allocation and **released when the next
input batch is fetched** (and in `Dispose`), so footprint is one batch. `ShuffleBytes` accrues
`rows * rowByteEstimate` per partitioned batch (the bytes that *would* cross a network boundary);
`InputRows` and `OutputRows` track rows in and rows emitted (equal, since nothing is dropped).

---

## 7. Dispatch integration and the compiled tier by delegation

`InterpretedOperators` is the one place that knows these operators. `Supports(kind)` returns true for
all seven `OperatorKind`s; `Open` switches on `Kind` to `OpenAggregate`/`OpenSort`/`OpenJoin`/
`OpenExchangeLocal`. Each `Open*`:

1. Validates the plan shape and builds the `RowKeyProjection`, aggregators, and orderings **before**
   opening children, so a build-time `UnsupportedOperatorException` (e.g. `AVG(decimal)`, a
   non-byte-sortable key) or `ArgumentException` (schema mismatch) **leaks no child stream**.
   `OpenJoin` additionally disposes the already-opened probe if opening the build throws.
2. Opens children via the same recursive `Open`, then constructs the stream.

**Compiled tier inherits these for free.** Per [ADR-0001](../../adr/0001-execution-strategy.md), the
`CompiledBackend` only fuses hot *expressions*; it **delegates operator open to
`InterpretedOperators`**. So the moment `InterpretedOperators` supports these four kinds, both backends
execute them through the **same** operator code — the compiled tier does not (and must not) reimplement
aggregate/sort/join/exchange. This is **parity by construction**: there is one operator implementation,
so interpreted and compiled cannot diverge on operator semantics. Tests assert identical results from
`ExecutionBackends.Select(ForceInterpreted)` and `ExecutionBackends.Select()` for grouped aggregate,
full-outer join, and keyed exchange.

---

## 8. Spark deviations and what is deferred

**Documented deviations from Spark:**

| Area | DeltaSharp v1 | Spark | Why acceptable |
| --- | --- | --- | --- |
| Exchange hash | FNV-1a(canonical bytes) mod N | Murmur3 | local in-process seam; remote shuffle (ADR-0004) adopts Murmur3 |
| Round-robin start | partition 0 | randomized start | deterministic + testable; remote shuffle revisits |
| Join strategy | hash join only | hash + sort-merge + broadcast | hash join satisfies the v1 multiplicity/null fixtures |

**Deferred (with story references):**

- **Spill** for the aggregate hash table, sort buffer, and join build side — today an over-budget
  reservation fails fast with `ExecutionMemoryException`. Spill is **STORY-03.5.x**.
- **`AVG(decimal)`** — the result type is resolved, but execution needs the deferred decimal-divide
  rounding (same deferral as the type system's decimal `/`); fails fast at `Open`. Tracked with the
  decimal-divide work.
- **Zero-box typed-hash keys** — v1 boxes key values and allocates a per-row key array; a zero-box
  fast path is deferred behind the `RowKeyProjection` contract.
- **Murmur3 exchange hashing and a partition-tagged batch** — land with the remote shuffle
  (STORY-03.5.x / STORY-03.6, ADR-0004); the positional contract (§6.2) is the v1 stand-in.
- **Sort-merge / broadcast join strategies and AQE** — physical strategy selection is a later planner
  concern; v1 ships one hash-join strategy.
- **SIMD / whole-stage codegen** acceleration of these operators — FEAT-03.3 / STORY-03.4.2; v1 is
  correctness-first scalar-fold-over-vectorized-columns.

---

## 9. Test and oracle strategy

Tests live in `tests/DeltaSharp.Engine.Tests/Execution/RelationalOperatorsTests.cs` and are written to
**fail on a wrong result, not just on a crash** (mutation-resistant). The oracles:

- **Aggregate** — asserted against hand-computed scalar results and the Spark null/ANSI rules
  directly: `COUNT(*)` vs `COUNT(x)` on null-bearing input; `SUM`/`AVG`/`MIN`/`MAX` skip-null;
  all-null and empty groups → NULL/0; global-over-empty → 1 row, grouped-over-empty → 0 rows; `SUM`
  ANSI overflow throws / Legacy nulls (long **and** decimal); decimal `SUM` precision widening with
  exact unscaled result; `MIN`/`MAX` NaN-as-largest (double) and lexicographic (string); NaN/`-0.0`
  group canonicalization; `AVG(decimal)` → `UnsupportedOperatorException`.
- **Sort** — every output is checked against an **independent `RowOrderingComparer` permutation** (a
  unique id column tracks row identity), across `{ASC,DESC} × {NULLS FIRST,NULLS LAST}` for int,
  double (NaN/`-0.0`/`-∞`), compact decimal, and timestamp; plus multi-key, stable-tie-break,
  multiset-preservation, and multi-batch merge.
- **Join** — hand-computed fixtures for all six types with **key multiplicity** (duplicate keys →
  Cartesian sub-product), null-key policy on every join type, left++right vs left-only output
  assembly, a >1024-row fan-out that forces and verifies the resumable chunk-boundary path, and a
  schema-mismatch fail-fast.
- **Exchange** — a **gold FNV oracle** (the same `RowKeyProjection` + `RowKey.Fnv1a mod N` the
  operator uses) asserts every row lands in its derived partition; plus multiset preservation,
  determinism across runs, round-robin-from-0 for keyless, the positional "N batches per input batch
  including empties" contract, null-key routing, and `ShuffleBytes` accounting.
- **Cross-cutting** — laziness (`InputRows == 0` until first pull), cancellation-before-first-pull
  throws, bounded-memory refusal (`ExecutionMemoryException` with a tiny budget), populated metrics,
  and **interpreted↔compiled parity** for aggregate, join, and exchange.

---

## 10. AC coverage map (STORY-03.2.2)

| Acceptance criterion | Where satisfied |
| --- | --- |
| Grouped + global hash aggregates: keys, values, null, overflow match scalar oracle | §3; aggregate tests |
| Sort output order matches the documented comparator for nulls, NaN, decimals, timestamps | §4; comparer-oracle sort tests |
| Inner/left/right/full join preserve Spark row multiplicity + null-key semantics | §5; join fixtures |
| Exchange-local: partition ids + row counts match the partitioning expr, no loss/duplication | §6; FNV-oracle + preservation tests |
| Cancellation stops at a bounded checkpoint and releases owned buffers | §2.1, §2.3; cancellation + dispose tests |
