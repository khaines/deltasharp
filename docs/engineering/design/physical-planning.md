# Physical planning: the bridge to the EPIC-03 backend (v1)

> **Status:** living document. Created with
> [STORY-04.6.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md)
> (the physical-planning bridge — the lane that finally makes a DeltaSharp query run end-to-end).
> Grounded in [ADR-0001](../../adr/0001-execution-strategy.md) (pluggable vectorized interpreter +
> optional JIT codegen tier — the interpreted backend is the default and correctness reference) and
> [ADR-0002](../../adr/0002-columnar-batch-format.md) (mutable `ColumnBatch`/`ColumnVector`,
> selection-vector-aware). Consumes the Core logical IR
> ([logical-plan-nodes.md](logical-plan-nodes.md), [expression-model.md](expression-model.md)) and the
> analyzer's output derivation ([analyzer-resolution.md](analyzer-resolution.md)); targets the EPIC-03
> executable operators ([execution-operators.md](execution-operators.md),
> [relational-operators.md](relational-operators.md)) and the execution backend
> ([execution-backend.md](execution-backend.md)). Update it whenever the node→operator mapping,
> materialization, diagnostics, or the Core↔Executor seam changes.

This bridge lives in the **`DeltaSharp.Executor`** assembly (`net10.0`, non-packable) under
`src/DeltaSharp.Executor/Physical/`. Executor is the only project that references **both** Core (the
`LogicalPlan` IR) and Engine (the executable operators), so it is the natural home for the mapping.
Core never references Engine.

It closes the loop opened by the rest of EPIC-04: a user builds a `DataFrame` (lazy logical plan),
the analyzer resolves it, the optimizer rewrites it (#172), and this bridge turns the analyzed plan
into a running query whose results come back as Core `Row`s.

---

## 1. The end-to-end pipeline

```
DataFrame (lazy)                       Core, public API — builds a LogicalPlan, does no work
   │  action (Collect/Count)           Core, STORY-04.6.1 (#173) — IQueryExecutor seam
   ▼
Analyzer.Resolve                       Core — resolved plan (names/types, ExprIds)
   │
Optimizer (#172)                       Core — equivalent, cheaper plan (predicate pushdown, pruning)
   ▼
IQueryExecutor.Collect/Count           Core interface  ─────────────►  DeltaSharp.Executor
                                                                          │
   PhysicalPlanner.Plan  ──────────────────────────────────────────────► PhysicalPlan tree
   │      (one strategy per supported node → an EPIC-03 operator)
   ▼
   PhysicalPlan.Execute(runtime)  ────► IExecutionBackend.Open  ────────► ColumnBatch stream
   │      (ADR-0001 interpreted vectorized backend by default)
   ▼
   RowMaterializer  ──────────────────────────────────────────────────► IReadOnlyList<Row>
```

Everything below the `IQueryExecutor` line is this story. The public read-door that supplies input
data (`createDataFrame`, file/Delta scans) is **STORY-04.1.2 (#158)** and is *not* in this batch; M1
feeds input through an in-memory relation fixture (§9).

---

## 2. The Core ↔ Executor seam (STORY-04.6.1 / #173, STORY-04.7.1 / #177)

Two sibling lanes own the Core types this lane implements the executor behind: **#173** owns the
`IQueryExecutor` seam and the `SparkSession` registration hook; **#177** (STORY-04.7.1) owns the public
`DeltaSharp.Row`. Both are now **merged into `main`**, so this branch consumes the **real** Core seam
directly (its earlier minimal stand-ins were dropped at rebase — see §10); this PR adds **no** Core-side
changes.

| Core type | Role | Owner |
| --- | --- | --- |
| `public sealed class DeltaSharp.Row` | A materialized result row: `Schema`, ordinal/by-name access, `GetAs<T>`, `IsNullAt`, null-aware. | #177 (merged) |
| `internal interface DeltaSharp.Execution.IQueryExecutor` | `IReadOnlyList<Row> Collect(LogicalPlan)` + `long Count(LogicalPlan)`. | #173 (merged) |
| `SparkSession` registration hook | `RegisterQueryExecutorFactory(Func<SparkSession,IQueryExecutor>?)`; a default `UnsupportedQueryExecutor` throws "no execution backend registered". | #173 (merged) |

`SparkSession.QueryExecutor` resolves the registered factory once per session (else the throwing
default). Executor registers `LocalQueryExecutor` via a `[ModuleInitializer]`
(`ExecutorRegistration`), so **any process that references `DeltaSharp.Executor` executes for real**
the moment an Executor type is touched (including a test host loading the assembly). Core grants
`[InternalsVisibleTo("DeltaSharp.Executor")]` so the executor can implement the internal interface and
read the internal logical IR.

---

## 3. The `PhysicalPlan` model

`PhysicalPlan` (in `PhysicalPlan.cs`) is an immutable tree of physical operator nodes. Each node knows
its `OutputSchema` (a `StructType`) and can `Execute(PhysicalRuntime) → BatchResult` (schema + a fully
materialized `IReadOnlyList<ColumnBatch>`).

| Node | Role | Backing |
| --- | --- | --- |
| `ScanPlan` | leaf over in-memory batches from an `IScanSource` | returns the batches (wrapped for parents via `InMemoryScanOperator`) |
| `FilterPlan` | row filter | EPIC-03 `FilterOperator` |
| `ProjectPlan` | projection | EPIC-03 `ProjectOperator` |
| `AggregatePlan` | grouped aggregation | EPIC-03 `AggregateOperator` |
| `JoinPlan` | equi-join | EPIC-03 `JoinOperator` |
| `SortPlan` | sort | EPIC-03 `SortOperator` |
| `LimitPlan` | row limit (**bridge**) | truncates child batches (EPIC-03 has no limit operator) |
| `UnionPlan` | UNION ALL (**bridge**) | concatenates child batches (EPIC-03 has no union operator) |

### 3.1 Execution/composition model (M1)

Every node executes to **fully materialized batches**. An EPIC-03-backed node builds a *shallow*
Engine operator over an `InMemoryScanOperator` of its child's already-materialized batches, then
`Open`s it on the backend and drains it (`PhysicalRuntime.Run`). This materializes at each operator
boundary — it sacrifices intra-subtree streaming/fusion, but it is correct, uniform, and lets bridge
nodes (limit/union) and Engine nodes compose freely. Selection vectors left on a batch by a filter or
limit flow through unchanged because `InMemoryScanOperator` yields batches verbatim and downstream
operators read via `ColumnBatch.SelectedColumn`. **Fused streaming is a future optimization.**

The Engine `ExecutionContext` uses `BoundedExecutionMemory.Unbounded` for M1 (no spill), threading the
backend options and cancellation token. Engine operator construction is wrapped so an ill-typed
build surfaces as a deterministic `UnsupportedPlanException` rather than a raw `ArgumentException`.

---

## 4. The node → EPIC-03 operator mapping (`PhysicalPlanner`)

`PhysicalPlanner.Plan` derives per-node output attributes (§5), then walks the analyzed tree mapping
each supported node to exactly one strategy (no cost-based choice in M1).

| LogicalPlan node | PhysicalPlan node | EPIC-03 operator / mechanism | Notes |
| --- | --- | --- | --- |
| `ResolvedRelation` | `ScanPlan` | `InMemoryScanOperator` | batches from `IScanSource`; public read-door is #158 |
| `Filter` | `FilterPlan` | `FilterOperator` | predicate translated to a `PhysicalExpression` |
| `Project` | `ProjectPlan` | `ProjectOperator` | one projection per output field |
| `Aggregate` | `AggregatePlan` | `AggregateOperator` | grouping keys ⧺ aggregates (§6) |
| `Join` | `JoinPlan` | `JoinOperator` | equi-keys extracted from the condition (§4.1) |
| `Sort` | `SortPlan` | `SortOperator` | translated `SortOrder`s (direction + null ordering) |
| `Limit` | `LimitPlan` | *bridge* (batch truncation) | EPIC-03 has no limit operator |
| `Distinct` | `ProjectPlan`(`AggregatePlan`) | `AggregateOperator` + `ProjectOperator` | **lowered** to GROUP BY all columns + `COUNT(*)`, then drop the count (§4.2) |
| `Union` | `UnionPlan` | *bridge* (batch concatenation) | EPIC-03 has no union operator (§4.3) |
| anything else | — | — | deterministic `UnsupportedPlanException` (§8) |

### 4.1 Join key extraction

Core's `Join` carries a single `Condition` expression, but EPIC-03's `JoinOperator` wants explicit
equi-join key lists. The planner flattens the condition's top-level `AND` conjunction and requires
each conjunct to be an equality (`=`) whose two sides reference **exactly one input each**. It emits
the left-referencing side as a left key and the right-referencing side as a right key (swapping when
written right-first). `JoinType` maps Core→Engine (`Inner`/`LeftOuter`/`RightOuter`/`FullOuter`/
`LeftSemi`/`LeftAnti`). Deferred to a deterministic `UnsupportedPlanException`: **`Cross`** (no Engine
equivalent), a **missing condition** (cartesian product), and any **non-equi / residual (theta)**
conjunct.

### 4.2 Distinct lowering

`Distinct` has no dedicated Engine operator. The planner lowers it to a fully Engine-backed subtree:
group by **all** child columns with a single `COUNT(*)` probe (`AggregateOperator`), then a
`ProjectOperator` that drops the probe column. Spark's `DISTINCT` null-equality matches GROUP BY key
equality, so this is semantics-preserving. The physical shape of a planned `Distinct` is therefore a
`ProjectPlan` over an `AggregatePlan`.

### 4.3 Union semantics

`UnionPlan` implements UNION **ALL** (no dedup) by concatenating its children's batches. The output
takes the **first** input's schema (Core's analyzer reuses the first input's attributes; Spark set-op
widening is `TODO(#392)`). A later input whose column **types** differ raises
`UnsupportedPlanException` (union type coercion is deferred); one whose **names** differ is renamed to
the target schema via an identity `ProjectOperator` so downstream operators see one consistent schema.

---

## 5. Output derivation: reconstructing ExprIds (`LogicalOutput`)

> **⚠ This is the most fragile part of the bridge and the top rebase-reconciliation point.**

Core's `LogicalPlan` does **not** store output attributes (unlike Catalyst). Only `ResolvedRelation`
exposes its `Output`. The analyzer computes each node's output attribute list — minting **fresh
`ExprId`s** for `Project`/`Aggregate` alias and function outputs — into a *transient* map that is
discarded after analysis. Parent nodes reference those fresh ids, but the ids live nowhere in the
tree, so the planner cannot recover alias/aggregate output ExprIds from the analyzed plan alone. (The
analyzer is also **not idempotent**: re-running it re-mints those ids from zero, breaking consistency
with references already in the tree.)

`LogicalOutput.Derive` reconstructs the derivation by **mirroring the analyzer's exact numbering**:

1. The analyzer seeds one `ExprIdGenerator` per `Resolve` call. Phase 1 (ResolveRelations) assigns
   ids `0..(k-1)` to the `k` relation output attributes across the whole tree. Phase 2
   (ResolveReferences) mints alias/function output ids **continuing** from `k`, bottom-up and
   left-to-right.
2. So the reconstruction seeds its minter at `k = Σ ResolvedRelation.Output.Count` and re-derives
   outputs in the same post-order, minting `Project`/`Aggregate` alias/function ids in the same order
   (`ProjectionOutput`/`ToAttribute` logic, including the Spark auto-name via the analyzer's
   `CoercionHelpers.PrettyReference`). Pass-through nodes (`Filter`/`Sort`/`Limit`/`Distinct`/`Union`)
   reuse the child's list; `Join` is left⧺right (or left-only for semi/anti); relations reuse their
   stored attributes.

Because the analyzed tree structure is identical and the traversal is deterministic, the
reconstructed ids equal the ids the tree's own `AttributeReference`s already carry. **Resolution is
self-checking:** the expression translator resolves each reference by `ExprId → ordinal` against the
reconstructed child output; if a reference does not resolve (e.g. an optimizer pass minted or
reordered outputs and the reconstruction drifted), it raises a deterministic
`UnsupportedPlanException` naming the attribute — never a silently wrong plan.

**Rebase note:** this duplicates analyzer internals. #172 (optimizer) and #173 are now merged, so the
durable analyzer/optimizer **output** seam is still pending (**#421**); when it lands, replace
`LogicalOutput` with the real seam and delete the reconstruction (see §10). The self-check is also
strengthened: a reference that resolves by id but whose reconstructed input attribute disagrees on
name/type (a valid-but-wrong-ordinal bind) raises `UnsupportedPlanException` rather than emitting a
wrong plan.

---

## 6. Expression translation (`PhysicalExpressionTranslator`)

The translator maps a resolved Core `Expression` to an Engine `PhysicalExpression`, resolving each
`AttributeReference` to a column ordinal against a supplied input attribute list.

| Core expression | Engine expression |
| --- | --- |
| `AttributeReference` | `ColumnReference(ordinal, type, nullable)` |
| `Literal` | `Literal.Of…` / `Literal.Null(type)` (by logical type) |
| `Alias` | translate child (name comes from the output schema) |
| `BinaryComparison` | `ComparisonExpression` (operator mapped) |
| `BinaryArithmetic` | `ArithmeticExpression` (operator mapped, ANSI mode) |
| `And`/`Or` | `LogicalExpression` (binary) |
| `Not` | `LogicalExpression` (unary) |
| `IsNull`/`IsNotNull` | `IsNullExpression(child, negated)` |
| `Cast` | `CastExpression(child, targetType, ANSI mode)` |
| aggregate `ResolvedFunction` (`count`/`sum`/`min`/`max`/`avg`) | `AggregateExpression(function, input)` |
| `CaseWhen`, scalar `ResolvedFunction`, `EqualNullSafe`, … | `UnsupportedPlanException` |

Core and Engine define **separate** arithmetic/comparison/sort/null-ordering enums (Core internal,
Engine public) with identical members; the translator maps between them. M1 evaluates under
`AnsiMode.Ansi` (the Engine default). Aggregate translation: `COUNT(*)`/`COUNT(literal)` → Engine
`COUNT` with a null input; `COUNT(col)`/`SUM`/`MIN`/`MAX`/`AVG` → the function over the translated
argument. `DISTINCT` aggregates and multi-argument aggregates are deferred.

For an `Aggregate`, `AggregateExpressions = [grouping…, aggFuncs…]` (Spark `retainGroupColumns=true`),
so the planner translates the **leading** entries as Engine grouping keys and the **trailing** entries
as Engine aggregates; the Engine output (`groupingKeys ++ aggregates`) aligns with Core's order.

---

## 7. Row materialization (`RowMaterializer`)

Materialization converts each executed `ColumnBatch` to Core `Row`s, reading through
`ColumnBatch.SelectedColumn` (so any selection vector is honored) and mapping each `ColumnVector` lane
to the natural CLR value for its logical type, null-aware:

| Logical type | CLR value | Storage read |
| --- | --- | --- |
| Boolean | `bool` | `GetValue<bool>` |
| Byte (signed tinyint) | `sbyte` | `GetValue<byte>` → `(sbyte)` |
| Short | `short` | `GetValue<short>` |
| Integer | `int` | `GetValue<int>` |
| Long | `long` | `GetValue<long>` |
| Float / Double | `float` / `double` | `GetValue<…>` |
| Decimal | `decimal` (scale-preserving) | compact → `long`, else `Int128`; reconstructed via `new decimal(lo, mid, hi, sign, scale)` |
| Date | `DateOnly` | `GetValue<int>` epoch-day → `DateOnly` |
| Timestamp | `DateTime` (UTC) | `GetValue<long>` epoch-micros → `DateTime` |
| String | `string` | UTF-8 decode of `GetBytes` |
| Binary | `byte[]` | `GetBytes` |

`Count` sums each batch's `LogicalRowCount` **without** building `Row` objects (avoids full
materialization). **Date/Timestamp** surface as the CLR temporal types `lit()` round-trips — a
`DateType` epoch-day as a `DateOnly`, a `TimestampType` epoch-microsecond instant as a UTC `DateTime`
(the inverse of `lit(DateOnly)`/`lit(DateTime)`/`lit(DateTimeOffset)`) — so `Collect()`/`GetAs<T>` and
`Show` yield a calendar date/instant rather than the raw epoch number. **Decimal** reconstructs
`System.Decimal` from the unscaled magnitude **preserving the declared scale** (so `decimal(5,2)`
`100.00` keeps scale 2 and renders `100.00`, not `100`); a value that genuinely cannot be represented
as `System.Decimal` — `scale > 28`, or an unscaled magnitude wider than 96 bits — raises a
deterministic `UnsupportedPlanException` rather than a raw `OverflowException` (§8).

---

## 8. Deterministic unsupported-plan diagnostics

The bridge never silently produces a wrong plan. Every unmapped node or expression raises
`UnsupportedPlanException` (public in `DeltaSharp.Executor`) naming the offender:

- an unmodelled logical operator (e.g. a future `Intersect`/`Except`) — `PhysicalPlanner`;
- an unmodelled expression (`CaseWhen`, scalar function, `EqualNullSafe`) — the translator;
- a `Cross` join, a conditionless join, or a non-equi/residual join predicate — `PhysicalPlanner`;
- a `Union` with mismatched column counts/types — `UnionPlan`;
- a **duplicate output column name** (e.g. `Select(col, col)`, or an equi-join whose sides share a
  column name) — `PhysicalPlanner.SchemaOf`. Spark permits duplicate output names; full support is
  deferred to **#419**, so M1 fails deterministically naming the duplicated column instead of leaking
  the `StructType` `SchemaValidationException`;
- an **`Aggregate` with no aggregate functions** (grouping-only) — `PhysicalPlanner` (it is not an
  EPIC-03 aggregate; grouping-only dedup goes through `Distinct`);
- a **decimal value not representable as `System.Decimal`** (`scale > 28`, or an unscaled magnitude
  wider than 96 bits) — `RowMaterializer`, in place of a raw `OverflowException`;
- an attribute that does not resolve against its input, **or resolves by id to an input column that
  disagrees on name/type** (ExprId reconstruction drift) — the translator;
- an ill-typed Engine operator build — wrapped by `PhysicalPlan.BuildOperator`.

---

## 9. In-memory relation fixture & the #158 read-door

M1 has no public data-in door (that is STORY-04.1.2 / #158). The bridge takes data through an
`IScanSource` seam: `bool TryGetBatches(ResolvedRelation, out batches)`. The in-memory implementation,
`InMemoryScanSource`, keys batches by relation identifier and validates every batch against the
registered schema. Tests register a relation's schema + `ColumnBatch`es, build an analyzed
`LogicalPlan` over it (through the **real** analyzer), and either run the planner directly or go
through `SparkSession` (the `[ModuleInitializer]`-registered `LocalQueryExecutor` reads
`InMemoryScanSource.Default`). When #158 lands, `createDataFrame`/file scans register batches (or a
real reader) through this same `IScanSource` shape.

---

## 10. Rebase reconciliation points (#172 optimizer, #173 IQueryExecutor, #177 Row) & deferrals

This PR was implemented against the #173 contract before #172/#173/#177 merged. All are now merged into
`main` and this branch has been rebased onto them; the reconciliation below is **complete**:

1. **Row / IQueryExecutor / SparkSession hook — done.** The Core stand-ins this branch once carried
   (`Row`, `IQueryExecutor`, `UnregisteredQueryExecutor`, the registration hook, the `InternalsVisibleTo`
   grant, and the `PublicAPI.Unshipped.txt` `Row` entries) were **dropped**; the branch now binds to the
   merged seam: `DeltaSharp.Row` (#177), `DeltaSharp.Execution.IQueryExecutor` and
   `SparkSession.RegisterQueryExecutorFactory` (#173), and the fail-closed `UnsupportedQueryExecutor`
   default. The `PublicAPI.*.txt` diff owned by *this* PR is empty (those entries belong to #173/#177).
2. **`LogicalOutput` ExprId reconstruction (§5).** Replace with the real analyzer/optimizer output
   seam once available (**#421**). Verify the optimizer (#172) preserves attribute ExprIds; if it
   mints/reorders outputs, the self-checking resolution will flag drift loudly (by design — it now also
   rejects a same-id bind to a name/type-mismatched column) — that is the signal to switch to the
   exposed seam.
3. **AnsiMode / backend selection.** Confirm the merged `SparkSession` execution-backend config
   (`spark.deltasharp.execution.backend`) still maps as in `LocalQueryExecutor.OptionsFor`. Both
   selections share the interpreted `InterpretedOperators` dispatch; `Default` resolves to
   `CompiledBackend` (ADR-0001 codegen tier, **STORY-03.4.2**), which fuses scalar expressions via
   `Expression.Compile` when `RuntimeFeature.IsDynamicCodeSupported` — so the backend-parity check is a
   genuine interpreted-vs-compiled **expression**-evaluation differential where dynamic code is
   available, degrading to identical under AOT. Operator-level codegen remains out of scope (ADR-0001
   §Follow-ups / **EPIC-13**, **#309/#310**).

**Tracked deferrals.** Full **duplicate output-name** support (Spark permits duplicate names) is
**#419**; the streaming/pooling seam (batch-ownership copy-out and per-operator `ExecutionContext`
disposal, both benign in M1's fresh-batch / no-spill model) is **#420**; the durable analyzer/optimizer
**output seam** that retires `LogicalOutput`'s reconstruction is **#421**.
