# Relational transformations — Join, OrderBy/Sort, Limit, Distinct, Union (M1)

> **Status:** living document. Created with
> [STORY-04.2.3](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md)
> (issue [#162](https://github.com/khaines/deltasharp/issues/162), FEAT-04.2 — the relational
> `DataFrame` transformation surface). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager execution) and builds directly on
> [dataframe-transformations.md](dataframe-transformations.md) (the first `Select`/`Filter`/
> `WithColumn` surface, #160), [column-and-functions.md](column-and-functions.md) (the public
> `Column`/`Functions` door, #164), [logical-plan-nodes.md](logical-plan-nodes.md) (the immutable
> `Join`/`Sort`/`Limit`/`Distinct`/`Union` IR and structural sharing, #167), and
> [analyzer-resolution.md](analyzer-resolution.md) (output derivation and `CheckAnalysis`, #170).
> It relies on the audit seam from [lazy-eager-audit.md](lazy-eager-audit.md) (#169) to prove
> laziness. Update it whenever the relational surface, the plan mapping, the join-type aliases, or
> the union diagnostics change.

## 1. What this is (and is not)

This is the **relational transformation surface** on `DataFrame` — the .NET-idiomatic mirror of
Apache Spark's `Dataset.join`, `orderBy`/`sort`, `limit`, `distinct`, and `union`/`unionAll`. It adds
public methods to `DeltaSharp.DataFrame` (`src/DeltaSharp.Core/Session/DataFrame.cs`) and two
ordering helpers to `DeltaSharp.Column` (`src/DeltaSharp.Core/Column.cs`), each of which only
**extends** the immutable logical plan and returns a **new** `DataFrame`.

| Public method | Spark parity | Result plan |
| --- | --- | --- |
| `Join(DataFrame right)` | `join(right)` | `Join(this.Plan, right.Plan, Inner, condition: null)` |
| `CrossJoin(DataFrame right)` | `crossJoin(right)` | `Join(this.Plan, right.Plan, Cross, condition: null)` |
| `Join(DataFrame right, Column condition)` | `join(right, joinExprs)` | `Join(…, Inner, condition)` (delegates to the string overload with `"inner"`) |
| `Join(DataFrame right, Column condition, string joinType)` | `join(right, joinExprs, joinType)` | `Join(…, JoinTypes.FromSparkString(joinType), condition)` |
| `Join(DataFrame right, string usingColumn)` | `join(right, usingColumn)` | `Join(…, Inner, usingColumns: [usingColumn])` |
| `Join(DataFrame right, IEnumerable<string> usingColumns)` | `join(right, usingColumns)` | `Join(…, Inner, usingColumns)` |
| `Join(DataFrame right, IEnumerable<string> usingColumns, string joinType)` | `join(right, usingColumns, joinType)` | `Join(…, FromSparkString(joinType), usingColumns)` |
| `OrderBy(params Column[] columns)` | `orderBy(cols: Column*)` | `Sort(order, global: true, this.Plan)` |
| `OrderBy(string column, params string[] columns)` | `orderBy(col: String, cols: String*)` | `Sort(order, global: true, this.Plan)` |
| `Sort(params Column[] columns)` | `sort(cols: Column*)` | equivalent to `OrderBy` (both build a global `Sort` via the shared private `BuildSort` helper) |
| `Sort(string column, params string[] columns)` | `sort(col: String, cols: String*)` | equivalent to `OrderBy` (shared `BuildSort` helper) |
| `Limit(int n)` | `limit(n)` | `Limit(n, this.Plan)` |
| `Distinct()` | `distinct()` | `Distinct(this.Plan)` |
| `Union(DataFrame other)` | `union(other)` | `Union([this.Plan, other.Plan])` |
| `UnionAll(DataFrame other)` | `unionAll(other)` | delegates to `Union` (Spark synonym) |
| `Column.Asc()` | `Column.asc` | `Column(SortOrder(expr, Ascending, NullsFirst))` |
| `Column.Desc()` | `Column.desc` | `Column(SortOrder(expr, Descending, NullsLast))` |

Every method is a **transformation**: it constructs an immutable logical-plan node over `this.Plan`
(and, for `Join`/`Union`, over the other frame's plan) and returns a **new** `DataFrame`. It is
**not** analysis, optimization, physical planning, or execution. Building the plan consults **no**
schema, evaluates **no** predicate/condition, and makes **no** scan or backend call — the lazy half
of the lazy/eager invariant ([ADR-0001](../../adr/0001-execution-strategy.md)). Actions
(`Collect`/`Count`/`Show`/`Write`) — the only things that execute — arrive in later stories and are
out of scope.

The internal IR stays hidden. Every method takes and returns only `Column`/`DataFrame`/`string`/
`int`/`IEnumerable<string>` and unwraps `Column.Expr` (`internal`) to build the plan node, so
`Expression`, `LogicalPlan`, `JoinType`, `SortOrder`, and `DeltaSharp.Plans.*` never appear on the
public surface. In particular the `JoinType` enum is `internal`: the public door is the Spark
join-type **string** (`"inner"`, `"left"`, …), mapped to the enum by
`JoinTypes.FromSparkString` (`src/DeltaSharp.Core/Plans/Logical/JoinType.cs`).

## 2. The lazy invariant and how it is proven

Constructing any node here does **zero** work: `LogicalPlan`'s base constructor only stores children
(`src/DeltaSharp.Core/Plans/Logical/LogicalPlan.cs`), and `Join`/`Sort`/`Limit`/`Distinct`/`Union`
constructors only copy their operands into read-only lists. No method calls the analyzer, reads a
schema, or touches a backend.

The **non-vacuity proof** is `DataFrameLazyRelationalTransformationTests`
(`tests/DeltaSharp.Core.Tests/LazyEager/`). `JoinAndChainedRelationalOps_OverThrowOnReadSources_NeverRead`
builds a `Join` whose **both** sides are a `ThrowOnReadSource` (whose `Read()` throws — see
`ThrowOnReadSource.cs`), then chains `Where`/`OrderBy`/`Distinct`/`Union`/`Limit` on top, and asserts
`ReadCount == 0` on both sources. `ChainedRelationalTransformations_TouchNoAuditSeam` runs the same
kind of chain inside an `ExecutionAudit.BeginScope(recording)` and asserts the recorder observed **no**
file open, **no** rows read, and an **empty** stage path — the #169 audit seam that fires at
`Analyzer.Resolve`'s entry (`src/DeltaSharp.Core/Analysis/Analyzer.cs`) is never reached. Together
these prove the relational surface never analyzes and never executes.

Immutability (#167) is proven by the `*_LeavesSourceFrameUnchanged` /
`Join_LeavesBothSourceFramesUnchanged` / `Union_LeavesBothSourceFramesUnchanged` tests: after
deriving a result, each source frame's `Plan` is the **same reference**, and the new node reuses the
source plans as its children (structural sharing).

## 3. Join

`Join` maps onto the existing immutable `Join` node
(`src/DeltaSharp.Core/Plans/Logical/Join.cs`), whose constructor is
`Join(LogicalPlan left, LogicalPlan right, JoinType joinType, Expression? condition = null,
IEnumerable<string>? usingColumns = null, bool isNatural = false)`. The node records the join type
plus **exactly one** of three mutually-exclusive criteria (explicit condition, using-columns, or
natural) and reads neither side.

The public overloads cover Spark's shapes:

- **`Join(right)`** — an **inner** join with **no** condition. Like Spark's `join(right)` the absent
  condition means a Cartesian product; callers add a condition (or use the using-column overloads) to
  avoid it. (DeltaSharp records `JoinType.Inner`, not `Cross`; `Cross` is reachable via the explicit
  `"cross"` string, or the dedicated `CrossJoin` overload below.)
- **`CrossJoin(right)`** — the explicit, intent-revealing Cartesian product (Spark's
  `crossJoin(right)`). It records a `Join(Plan, right.Plan, JoinType.Cross, condition: null)` — the
  safe way to *ask* for a product, distinct from the conditionless `Join(right)` (which records
  `Inner`). Lazy, reads neither side (`CrossJoin_BuildsCrossJoinWithNullCondition_AndBothChildren`).
- **`Join(right, condition)`** — an **inner** join on `condition`; it delegates to
  `Join(right, condition, "inner")` so there is one code path.
- **`Join(right, condition, joinType)`** — the primary overload. It null-checks `right`/`condition`,
  maps `joinType` through `JoinTypes.FromSparkString`, and builds `Join(Plan, right.Plan, type,
  condition.Expr)`. The condition expression is recorded **by reference** — never evaluated or
  rewritten (asserted by `Join_WithCondition_BuildsInnerJoinRecordingConditionByReference`).
- **`Join(right, usingColumn)`** / **`Join(right, usingColumns)`** / **`Join(right, usingColumns,
  joinType)`** — using-joins. The shared column names are recorded in the node's `UsingColumns`.
  **Building the node is supported now, but resolution is deferred:** the analyzer rule that desugars
  the shared columns into an equi-`Condition` is **not yet implemented**, so `Analyzer.Resolve` of a
  plan containing a using/natural join fails fast with a **targeted** `AnalysisException`
  (kind `UsingOrNaturalJoinNotImplemented`, message pointing at the follow-up
  [#405](https://github.com/khaines/deltasharp/issues/405)) rather than the generic
  `UnresolvedOperator("Join")`. Each name is validated non-null/non-empty at the API. See
  [analyzer-resolution.md](analyzer-resolution.md);
  `Resolve_UsingColumnJoin_ThrowsTargetedNotImplemented_NotGenericUnresolvedOperator` pins the
  diagnostic.

### 3.1 The join-type string → enum mapping (Spark parity)

`JoinTypes.FromSparkString` is the single mapping point. It **normalizes** the input — lower-cases
every character and drops `'_'` and `' '` (it does **not** trim) — then matches, so `"LEFT OUTER"`,
`"left_outer"`, and `"leftouter"` are the same kind. This is a **superset** of Spark's
`JoinType.apply(String)` aliases: Spark strips underscores only, whereas DeltaSharp additionally
tolerates spaces for a friendlier UX. The on-stack normalization buffer is capped (≤ 256 chars use
`stackalloc`; longer strings fall back to `ArrayPool<char>`), so a pathologically long
attacker-controlled `joinType` cannot overflow the stack — it fails cleanly with the
`ArgumentException` below (`Join_PathologicallyLongJoinTypeString_ThrowsCleanArgumentException_NotStackOverflow`).

| Spark string alias(es) (after normalization) | `JoinType` |
| --- | --- |
| `inner` | `Inner` |
| `cross` | `Cross` |
| `outer`, `full`, `fullouter` (`full_outer`) | `FullOuter` |
| `left`, `leftouter` (`left_outer`) | `LeftOuter` |
| `right`, `rightouter` (`right_outer`) | `RightOuter` |
| `semi`, `leftsemi` (`left_semi`) | `LeftSemi` |
| `anti`, `leftanti` (`left_anti`) | `LeftAnti` |

The full alias set is verified by the `Join_MapsSparkJoinTypeString_ToEnum` theory (18 cases) and
`Join_JoinTypeString_IsCaseAndSeparatorInsensitive`.

### 3.2 Join output derivation (already in the analyzer)

The analyzer's `DeriveOutput`/`JoinOutput` (`src/DeltaSharp.Core/Analysis/Analyzer.cs`) already
governs a resolved join's output: **left ⧺ right** for value-adding joins, but **left-only** for
`LeftSemi`/`LeftAnti` (which filter the left side and never widen it), matching Spark. This story
adds no analyzer output logic for joins; it is covered by `AnalyzerTests`
(`ResolveReferences_InnerJoin_OutputIsBothSides`, `…_LeftSemiJoin_OutputIsLeftOnly`,
`…_LeftAntiJoin_OutputIsLeftOnly`).

## 4. OrderBy / Sort

`OrderBy` and `Sort` both build a **global** `Sort` node
(`src/DeltaSharp.Core/Plans/Logical/Sort.cs`: `Sort(IEnumerable<Expression> order, bool global,
LogicalPlan child)`) with `global: true`. In Spark `orderBy` and `sort` are exact synonyms (both a
total order). Neither method delegates to the other: `OrderBy` and `Sort` are both thin wrappers over
the **shared private `BuildSort` helper**, so they are equivalent by construction rather than one
calling the other; the per-partition `sortWithinPartitions` (`global: false`) is a later story and
out of scope. This is asserted by `Sort_IsEquivalentToOrderBy`.

Each ordering term is a `SortOrder` expression
(`src/DeltaSharp.Core/Plans/Expressions/SortOrder.cs`: `SortOrder(Expression child, SortDirection
direction, NullOrdering nullOrdering)`). The private `ToSortOrder` helper wraps a bare column as
**ascending, nulls-first** (Spark's default for a plain `orderBy` column) but passes through an
expression that is **already** a `SortOrder` unchanged — so `Col("x").Desc()` keeps its descending,
nulls-last ordering rather than being double-wrapped (`OrderBy_ExplicitAscColumn_IsNotDoubleWrapped`,
`OrderBy_DescColumn_PreservesDescendingNullsLast`).

The direction/null defaults come from `Column.Asc()`/`Column.Desc()`:

| Helper | `SortDirection` | `NullOrdering` | Spark parity |
| --- | --- | --- | --- |
| `Column.Asc()` | `Ascending` | `NullsFirst` | `Column.asc` (= `asc_nulls_first`) |
| `Column.Desc()` | `Descending` | `NullsLast` | `Column.desc` (= `desc_nulls_last`) |
| bare column in `OrderBy` | `Ascending` | `NullsFirst` | plain `orderBy(col)` |

**Deferred to [#405](https://github.com/khaines/deltasharp/issues/405):** the four explicit
null-placement helpers `Column.AscNullsFirst` / `AscNullsLast` / `DescNullsFirst` / `DescNullsLast`.
The IR is already **forward-compatible** — `SortOrder`/`NullOrdering` model all four
direction×null-placement combinations — so this is a public-`Column`-surface addition only, with no
IR change required. `Column.Asc()`/`Desc()` XML remarks carry a one-line pointer to #405.

The string overloads turn each name into `Functions.Col(name)` first (so a required first name keeps
`OrderBy()` unambiguous, exactly as the `Select` overload does — see
[dataframe-transformations.md](dataframe-transformations.md) §3). Ordering is a shape-preserving
unary operator: the analyzer's `DeriveOutput` passes the child's output through unchanged (the
`Sort` case).

## 5. Limit and Distinct

`Limit(int n)` builds `Limit(n, this.Plan)` (`src/DeltaSharp.Core/Plans/Logical/Limit.cs`). The count
is a **literal integer** (not an expression); the node's constructor rejects a negative count with
`ArgumentOutOfRangeException`, so `Limit(-1)` throws and `Limit(0)` is allowed. Spark splits `Limit`
into `GlobalLimit(LocalLimit(...))` during **planning**; DeltaSharp keeps the single unresolved node
and defers that split to physical planning (out of scope).

`Distinct()` builds `Distinct(this.Plan)` (`src/DeltaSharp.Core/Plans/Logical/Distinct.cs`). Spark's
analyzer later rewrites `Distinct` to an `Aggregate`; DeltaSharp keeps the node (parity with
Catalyst's `Distinct`) and defers the rewrite. Both are shape-preserving unary operators whose output
the analyzer passes through unchanged.

## 6. Union

`Union(DataFrame other)` builds `Union([this.Plan, other.Plan])`
(`src/DeltaSharp.Core/Plans/Logical/Union.cs`, which is N-ary and requires ≥ 2 inputs). Two Spark
semantics matter and are documented on the API:

- **By position, not by name.** The *n*th column of each input is unioned regardless of column name.
  Name-aligned union is Spark's separate `unionByName`, deferred and tracked by
  [#405](https://github.com/khaines/deltasharp/issues/405); the `Union` XML `<remarks>` carries the
  same pointer.
- **Row-preserving (bag union), no dedup.** `union` keeps duplicates; call `Distinct()` to dedupe.
  `unionAll` is a deprecated Spark synonym of `union`, so `UnionAll` delegates to `Union`
  (`UnionAll_IsEquivalentToUnion`).

**`UnionAll` deprecation decision (DX-API F4).** Spark marks `unionAll` `@deprecated`. DeltaSharp
**consciously keeps `UnionAll` non-obsolete** rather than attaching `[Obsolete]`: the framework's core
value is letting Spark code port across unchanged, so emitting an obsolete-warning on a
directly-ported `unionAll` call would create migration friction (and, under `-warnaserror`, break
builds of ported code) for no correctness benefit. The method is a one-line delegate to `Union` and
its XML doc already flags Spark's deprecation, so callers who prefer the current name are steered
without a hard warning. Revisit if/when we add analyzer-level migration guidance.

Union output derivation follows the **first input's** attributes (the analyzer's `Union` case in
`DeriveOutput`). Minting fresh output ids and widening nullability across inputs is tracked as
`TODO(#392)` and is out of scope here.

## 7. Diagnostics — what is validated here vs deferred

### 7.1 Unsupported join type → `ArgumentException` at the API (AC3)

An unknown `joinType` string fails fast at the API boundary: `JoinTypes.FromSparkString` throws an
`ArgumentException` whose message names the offending string **and** the supported aliases
(`JoinTypes.SupportedAliases`). This is a programming-error guard, so it is an `ArgumentException`
(not an `AnalysisException`) and never reaches the analyzer. Covered by
`Join_UnsupportedJoinTypeString_ThrowsNamingValidTypes`.

### 7.2 Union arity mismatch → `AnalysisException` in `CheckAnalysis` (AC3)

Incompatible union **column counts** are a schema concern, not an API-shape concern — the arity is
unknown until relations resolve — so the check lives in the analyzer, not the `Union(...)` method.
`CheckAnalysis` (`src/DeltaSharp.Core/Analysis/Analyzer.cs`) walks the resolved plan and, for each
`Union`, compares every input's resolved output **column count** against the first input's via
`CheckUnionArity`; a mismatch raises `AnalysisException.NumberOfColumnsMismatch`
(kind `AnalysisErrorKind.NumberOfColumnsMismatch`) with a Spark-parity message naming both arities.
Covered by `CheckAnalysis_Union_MismatchedColumnCount_ThrowsNumberOfColumnsMismatch` (and the
matching-arity happy path `CheckAnalysis_Union_SameColumnCount_Resolves`).

**Deferred:** deep column **type** compatibility and coercion across union
inputs (e.g. widening `int`/`long`, promoting nullability). This story implements only the
**structural** (arity) half; the type half is explicitly out of scope and tracked by **#392**
(Union set-op output; see the related `TODO(#392)` in `DeriveOutput`).

### 7.3 Using/natural join resolution → targeted `AnalysisException` (deferred, #405)

A using-column or natural join can be **built** today, but the analyzer rule that desugars its shared
columns into an equi-`Condition` is **not yet implemented**. `Join.IsNodeResolved` therefore reports
such a join permanently unresolved, so `CheckAnalysis` catches it. Rather than emit the generic
`UnresolvedOperator("Join")`, `CheckAnalysis` recognises the `UsingColumns`/`IsNatural` shape and
throws a **targeted** `AnalysisException` (kind
`AnalysisErrorKind.UsingOrNaturalJoinNotImplemented`) whose message says the feature is not yet
implemented and links [#405](https://github.com/khaines/deltasharp/issues/405) — deterministic and
actionable. Covered by
`Resolve_UsingColumnJoin_ThrowsTargetedNotImplemented_NotGenericUnresolvedOperator` and
`Resolve_NaturalJoin_ThrowsTargetedNotImplemented`. The desugaring rule itself is tracked by #405.


## 8. Public API surface

The additions to `src/DeltaSharp.Core/PublicAPI.Unshipped.txt` are the two `Column` ordering helpers
(`Asc`/`Desc`) and the `DataFrame` methods `Join` (6 overloads), `CrossJoin`, `OrderBy`/`Sort`
(2 each), `Limit`, `Distinct`, `Union`, and `UnionAll`. No internal IR type (`Expression`, `LogicalPlan`, `JoinType`,
`SortOrder`, `SortDirection`, `NullOrdering`) is added to the public surface. `PublicAPI` entries are
append-only and shared with the parallel FEAT-04.3 lane (#161).

## 9. Argument guards

| Method | Guard |
| --- | --- |
| `Join(right, …)` | `right` non-null (`ArgumentNullException`) |
| `CrossJoin(right)` | `right` non-null (`ArgumentNullException`) |
| `Join(right, condition[, joinType])` | `condition` non-null; `joinType` non-null and a supported alias (`ArgumentException`) |
| `Join(right, usingColumn)` | `usingColumn` non-null/non-empty (`ArgumentException`) |
| `Join(right, usingColumns[, joinType])` | `usingColumns` non-null; each element non-null/non-empty |
| `OrderBy`/`Sort(Column[])` | array non-null; each element non-null |
| `OrderBy`/`Sort(string, string[])` | first name non-null/non-empty; rest array non-null; each rest name non-null/non-empty (via `Functions.Col`) |
| `Limit(n)` | `n` ≥ 0 (`ArgumentOutOfRangeException`, from the node) |
| `Union`/`UnionAll(other)` | `other` non-null |

## 10. AC → test map

| AC | Requirement | Tests |
| --- | --- | --- |
| **AC1** | `Join` records join type + condition + both children without reading either side; Spark join-type strings map to the enum; using-joins record shared columns | `Join_NoCondition_…`, `Join_WithCondition_…`, `Join_MapsSparkJoinTypeString_ToEnum` (theory), `Join_JoinTypeString_IsCaseAndSeparatorInsensitive`, `Join_UsingSingleColumn_…`, `Join_UsingColumns_…`, `Join_UsingColumnsWithJoinType_…`, `Join_LeavesBothSourceFramesUnchanged_…` |
| **AC2** | `OrderBy`/`Sort` (asc default + `Desc`), `Limit(n)`, `Distinct`, `Union` build the corresponding immutable node | `OrderBy_Columns_…`, `OrderBy_DescColumn_…`, `OrderBy_Names_…`, `OrderBy_MixedDirections_…`, `Sort_IsEquivalentToOrderBy`, `Limit_BuildsLimitNodeWithCountOverChild`, `Limit_Zero_IsAllowed`, `Distinct_BuildsDistinctNodeOverChild`, `Union_BuildsUnionOfBothInputsInOrder`, `UnionAll_IsEquivalentToUnion` |
| **AC3** | Unsupported join type → clear diagnostic naming valid types; union arity mismatch → analyzer diagnostic | `Join_UnsupportedJoinTypeString_ThrowsNamingValidTypes`; `CheckAnalysis_Union_MismatchedColumnCount_ThrowsNumberOfColumnsMismatch`, `CheckAnalysis_Union_SameColumnCount_Resolves` |
| **AC4** | Chained relational transforms preserve node order + intent; no execution artifacts (pure logical plan) | `ChainedRelationalTransforms_BuildNestedPlan_PreservingOrder`; lazy proofs `JoinAndChainedRelationalOps_OverThrowOnReadSources_NeverRead`, `ChainedRelationalTransformations_TouchNoAuditSeam` |
| immutability / guards | Source frames unchanged; null/empty/negative guards | `*_LeavesSourceFrameUnchanged`, `Join_Null*_Throws`, `OrderBy_Null*_Throws`, `Limit_Negative_Throws`, `Union_NullOther_Throws`, `Join_EmptyUsingColumn_Throws` |
