# DataFrame aggregation — GroupBy, Agg, and RelationalGroupedDataset (M1)

> **Status:** living document. Created with
> [STORY-04.2.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md)
> (issue [#161](https://github.com/khaines/deltasharp/issues/161), FEAT-04.2 — the `DataFrame`
> aggregation surface). Grounded in [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager
> execution) and builds directly on
> [dataframe-transformations.md](dataframe-transformations.md) (the `Select`/`Filter`/`WithColumn`
> surface, #160), [column-and-functions.md](column-and-functions.md) (the public `Column`/`Functions`
> door and the aggregate builders `Count`/`Sum`/`Avg`/`Min`/`Max`, #164/#166),
> [logical-plan-nodes.md](logical-plan-nodes.md) (the immutable `Aggregate` IR and structural
> sharing, #167), [analyzer-resolution.md](analyzer-resolution.md) (output derivation and name
> resolution, #170), [lazy-eager-audit.md](lazy-eager-audit.md) (the audit seam that proves laziness,
> #169), and [api-governance.md](api-governance.md) / [repository-layout.md](repository-layout.md).
> Update it whenever the aggregation surface, the plan mapping, or the aliasing rules change.

## 1. What this is (and is not)

This is the **aggregation surface** on `DataFrame` — the .NET-idiomatic mirror of Apache Spark's
`Dataset.groupBy` / `RelationalGroupedDataset.agg` / `Dataset.agg`. It adds three public methods to
`DeltaSharp.DataFrame` (`src/DeltaSharp.Core/Session/DataFrame.cs`) and one new public type,
`DeltaSharp.RelationalGroupedDataset` (`src/DeltaSharp.Core/Session/RelationalGroupedDataset.cs`)
with two aggregation doors:

| Public member | Spark parity | Result |
| --- | --- | --- |
| `DataFrame.GroupBy(params Column[] columns)` | `groupBy(cols: Column*)` | `RelationalGroupedDataset` recording the grouping expressions |
| `DataFrame.GroupBy(string column, params string[] columns)` | `groupBy(col1: String, cols: String*)` | `RelationalGroupedDataset` (names via `Functions.Col`) |
| `DataFrame.Agg(Column expr, params Column[] exprs)` | `agg(expr: Column, exprs: Column*)` | `DataFrame` wrapping `Aggregate` with **empty** grouping (global) |
| `RelationalGroupedDataset.Agg(Column expr, params Column[] exprs)` | `RelationalGroupedDataset.agg(expr, exprs*)` | `DataFrame` wrapping `Aggregate(grouping, retainedKeys ⧺ aggExprs, plan)` |
| `RelationalGroupedDataset.Count()` | `RelationalGroupedDataset.count()` | `DataFrame` — `Agg(Count(Lit(1L)).As("count"))` |

Every method is a **transformation**: it constructs immutable logical-plan state over `this.Plan`
and returns a **new** `DataFrame` (or the intermediate handle). It is **not** analysis, optimization,
physical planning, or execution. Building the plan consults **no** schema, computes **no** aggregate,
and makes **no** scan or backend call — the lazy half of the lazy/eager invariant
([ADR-0001](../../adr/0001-execution-strategy.md)). Actions (`Collect`/`Count`(action)/`Show`/`Write`)
— the only things that execute — arrive in later FEAT-04.2 stories and are out of scope here.

The internal IR stays hidden: every method takes and returns only `Column`/`DataFrame`/`string`/
`RelationalGroupedDataset` and unwraps `Column.Expr` (`internal`) to build the plan node, so
`Expression`, `LogicalPlan`, and `DeltaSharp.Plans.*` never appear on the public surface. The
additions to `PublicAPI.Unshipped.txt` are the `RelationalGroupedDataset` **type** plus the 5 members
above (6 `PublicAPI` lines in total).

### Deferred Spark surface

Apache Spark's `RelationalGroupedDataset` is considerably wider than the two doors above. The
following are **deferred** (not in M1):

- typed aggregate shortcuts over named columns — `sum`, `avg`/`mean`, `min`, `max`;
- `pivot(pivotColumn[, values])`;
- the string-map/tuple aggregate forms — `agg(Map<string,string>)` and `agg((string, string)*)` —
  and their global `DataFrame.Agg(Map/tuple)` mirrors.

Until they land, the general `Agg(Column, …)` door expresses every one of them, e.g.
`df.GroupBy("dept").Agg(Functions.Sum(Functions.Col("salary")).As("total"))` (M1 workaround). Note a
**real** aggregate also needs aggregate-function resolution + Spark auto-naming
([#171](https://github.com/khaines/deltasharp/issues/171)) **and** the action surface
(`Collect`/`Show`/`count`) to analyze and execute end-to-end — not just naming. The typed-shortcut /
pivot / map-tuple backlog is tracked in
[#406](https://github.com/khaines/deltasharp/issues/406).

## 2. The `GroupBy → RelationalGroupedDataset → Agg` model

Spark's aggregation is a **two-step** chain, and DeltaSharp mirrors it exactly:

```
df.GroupBy("dept").Agg(Functions.Sum(Functions.Col("salary")).As("total"))
   └── step 1: GroupBy → RelationalGroupedDataset (records grouping keys, no plan node yet)
                          └── step 2: Agg → DataFrame wrapping an Aggregate plan
```

### 2.1 Why `RelationalGroupedDataset` is *not* a `DataFrame`

A grouping on its own is **not a scannable relation**. There is no well-defined set of rows to
`Collect`/`Show`/`Write` from `df.GroupBy("dept")` until an aggregation is chosen — Spark models this
by returning a distinct type, `RelationalGroupedDataset`, that carries the grouping but exposes **no
action surface**. DeltaSharp does the same: `RelationalGroupedDataset` holds only the source
`LogicalPlan` and the grouping `Expression` list (both `internal`), and exposes exactly two doors —
`Agg(...)` and `Count()` — each of which produces a `DataFrame` wrapping an `Aggregate` plan. It is a
`public sealed class` with a **non-public constructor**: only `DataFrame.GroupBy` creates it
(`RelationalGroupedDataset.cs`, `internal RelationalGroupedDataset(LogicalPlan, IReadOnlyList<Expression>)`).

This mirrors the same choice made for `DataFrame` itself (a session-free plan wrapper with an
`internal` constructor) and keeps the surface honest: you cannot accidentally treat a half-built
aggregation as a queryable frame.

### 2.2 What `GroupBy` records

`GroupBy(params Column[])` unwraps each `Column.Expr` into a grouping `Expression[]`;
`GroupBy(string, params string[])` turns each name into an `UnresolvedAttribute` via
`Functions.Col`. Both return `new RelationalGroupedDataset(Plan, grouping)` — the source frame's
`Plan` is passed **by reference** (structural sharing, #167), and no `Aggregate` node is built yet.
Grouping by no columns (`GroupBy()` — which binds to the `Column[]` overload) records an **empty
grouping**, i.e. a global aggregation over all rows.

## 3. Mapping to the `Aggregate` logical plan

`RelationalGroupedDataset.Agg(Column expr, params Column[] exprs)` builds

```
Aggregate(
    groupingExpressions = [ recorded grouping keys ],
    aggregateExpressions = [ recorded grouping keys ]  ⧺  [ expr, exprs… ],
    child = source plan)
```

The existing immutable `Aggregate` node
(`src/DeltaSharp.Core/Plans/Logical/Aggregate.cs`) takes
`Aggregate(IEnumerable<Expression> groupingExpressions, IEnumerable<Expression> aggregateExpressions,
LogicalPlan child)` and exposes both lists plus a combined `Expressions` view (grouping ⧺ aggregate)
that `WithNewExpressions` splits back at `GroupingExpressions.Count`. The API constructs it directly;
it never mutates it.

### 3.1 Retained grouping columns (`retainGroupColumns = true`)

Spark's default `spark.sql.retainGroupColumns = true` means the grouping keys are **retained at the
front of the aggregate output**, so `groupBy("k").agg(sum("v"))` yields output `[k, sum(v)]` — the
key column plus the aggregate. DeltaSharp reproduces this by **prepending the recorded grouping
expressions to `aggregateExpressions`** before the user's aggregate expressions
(`RelationalGroupedDataset.Agg`, the `foreach (Expression grouping in _groupingExpressions)` loop).
The grouping keys therefore appear in **both** lists: `GroupingExpressions` (what to group by) and
the front of `AggregateExpressions` (what to output).

This retention is also what makes the analyzer's structural output derivation land on the
Spark-correct shape — see §5.

### 3.2 Global aggregation: `df.Agg(...)` == `groupBy().agg(...)`

Spark defines `Dataset.agg(expr, exprs*)` as exactly `groupBy().agg(expr, exprs*)`. DeltaSharp's
`DataFrame.Agg` is implemented that way verbatim:
`new RelationalGroupedDataset(Plan, Array.Empty<Expression>()).Agg(expr, exprs)`. With an empty
grouping there are no retained keys, so `aggregateExpressions == [expr, exprs…]` and the resulting
`Aggregate` has an **empty** `GroupingExpressions`. `DataFrameAggregationTests.GlobalAgg_*` pins both
the empty-grouping shape and the `df.Agg(x) == df.GroupBy().Agg(x)` equivalence.

## 4. Spark-compatible aliasing (output naming)

Aggregate output names follow Spark:

- **User alias wins.** If an aggregate `Column` is aliased with `Column.As("total")`
  (`Column.cs` → `Alias(Expr, "total")`), the `Alias` flows verbatim into `aggregateExpressions`, so
  the output column is named `total`. Verified by `Agg_HonorsUserAliasOnAggregateOutput`.
- **Bare aggregate → analyzer names it.** A bare aggregate such as `Functions.Sum(Functions.Col("v"))`
  is an `UnresolvedFunction`; the API leaves it **unaliased**. Spark's auto-name (`sum(v)`) is a
  pretty-printed name assigned by the analyzer when aggregate-function resolution and naming land —
  DeltaSharp defers that to STORY-04.5.2 (#171). This is a deliberate **plan-shape divergence** from
  Spark: Spark wraps an unnamed aggregate in an `UnresolvedAlias` naming marker (later resolved by
  `ResolveAliases`), whereas DeltaSharp plants a **raw `UnresolvedFunction`** with no naming marker —
  so #171 must *synthesize* the output name from the expression rather than resolve a pre-planted
  marker. The API never computes the name eagerly (it would have to inspect/execute the expression).
  Verified by `Agg_BareAggregate_IsLeftUnaliased_ForAnalyzerNaming`.
- **`Count()`** is the one door that fixes a name: it is `Agg(Count(Lit(1L)).As("count"))`, matching
  Spark's `count()` which counts rows via `count(1)` and names the column `count`. Verified by
  `Count_BuildsAggregateWithNamedCountAfterRetainedKeys`.

## 5. Analyzer output derivation (already wired) and what it proves

The analyzer already derives `Aggregate` output structurally
(`Analyzer.DeriveOutput`, `src/DeltaSharp.Core/Analysis/Analyzer.cs`):
`case Aggregate aggregate: return ProjectionOutput(aggregate.AggregateExpressions, idGenerator);` —
i.e. **each aggregate expression as an attribute** (see
[analyzer-resolution.md](analyzer-resolution.md) §output table). Because the API retains the grouping
keys at the front of `AggregateExpressions` (§3.1), that derivation produces exactly
**grouping attributes ⧺ aggregate aliases** — the Spark-correct output shape. **No analyzer change is
required by this story**; the retention on the API side is what feeds the existing derivation. (This
keeps the change low-conflict with the parallel #162/#171 analyzer lanes.)

`Analyzer_DerivesAggregateOutput_AsGroupingAttributesThenAggregateAliases` proves this end-to-end
over a registered catalog: `df.GroupBy("dept").Agg(Lit(1L).As("marker"))` resolves to an `Aggregate`
whose `GroupingExpressions` is `[dept:AttributeReference]` and whose `AggregateExpressions` is
`[dept:AttributeReference, marker:Alias]`. It deliberately uses a **resolvable** aliased literal in
aggregate position because aggregate-**function** resolution is out of scope (see §6). The test also
wraps the resolved `Aggregate` in a parent `Project` that reads back **both** the grouping key and the
aggregate alias, asserting they bind against the aggregate output and that the **retained grouping
attribute reuses the child grouping key's `ExprId`** (the retained key at the front of
`AggregateExpressions` is bound to one identity, closing the retainGroupColumns ↔ derivation ↔
ExprId-reuse loop).

## 6. What is deferred to #171 (aggregate type-validation & function resolution)

Per AC3, an **invalid aggregate input** (a non-aggregate expression in aggregate position, an
aggregate outside an aggregate context, or a type-incompatible aggregate argument) is reported by the
**analyzer**, deterministically — never by the API executing or coercing. This story:

- builds only a **well-formed unresolved** `Aggregate`; it neither coerces nor executes;
- leaves aggregate **function resolution** and **pretty-naming** to the analyzer. Today an
  `UnresolvedFunction` (`sum`/`count`/…) is *not* resolved, and the analyzer rejects it
  deterministically along **whichever of two gates fires first**:
  - **output derivation** — for a bare aggregate in output position (the most common call,
    `df.GroupBy("k").Agg(Functions.Sum(Functions.Col("v")))`), the failure fires from
    `DeriveOutput → ToAttribute` **during `ResolveReferences`**, *before* `CheckAnalysis` runs.
    `ToAttribute` cannot mint an output name for a raw `UnresolvedFunction`, so it throws a targeted
    `AnalysisException` (`Kind = UnsupportedProjection`) naming the deferred-resolution boundary and
    the alias workaround. This is the path `Analyzer_RealAggregateFunction_ReportsDeferredResolution_NotApiExecution`
    pins (message asserted verbatim so #171 improves it deliberately);
  - **`CheckAnalysis`** — the post-condition sweep that throws when an `UnresolvedFunction` (or other
    unresolved marker) survives to a position that output derivation does not name (for example an
    aggregate argument, or a function nested where it is not the projected element).

  So the accurate statement is that the analyzer is the gate via **output-derivation
  (`DeriveOutput`/`ToAttribute`) OR `CheckAnalysis`, whichever fires first** — not solely
  `CheckAnalysis`. ("Nothing is read/executed" on this path is proven separately by the §7 lazy
  tests, not by the AC3 boundary test.)

Aggregate function resolution, aggregate input type-validation, and Spark auto-naming land with
STORY-04.5.2 ([#171](https://github.com/khaines/deltasharp/issues/171)). Until then the API surface is
complete and lazy, and the analyzer boundary is explicit. A **complex grouping key** (a non-attribute
expression retained at the front of the aggregate output, e.g. `GroupBy(Col("a").Plus(Col("b")))`)
hits the same output-derivation gate today and is likewise deferred to #171 for naming/aliasing —
pinned by `Analyzer_ComplexGroupingKey_ThrowsDeterministically_TrackedUnder171`.

## 7. The lazy invariant and how it is proven

An aggregation must only **extend** the plan; the source `DataFrame` must be observably unchanged
(immutable plans → structural sharing, #167). Two properties hold:

1. **No work.** `GroupBy`/`Agg`/`Count` bodies only allocate plan state; they never touch the source
   scan, the analyzer, or the backend. `DataFrame` and `RelationalGroupedDataset` are session-free
   plan wrappers — they hold no `SparkSession`, catalog, or reader — so they *cannot* resolve or
   execute even if they wanted to.
2. **Immutability / structural sharing.** The new `Aggregate` takes `this.Plan` as its child by
   reference; the source frame's `Plan` is the same instance before and after.

Proven three ways:

- **Plan-shape + immutability tests** (`DataFrameAggregationTests`) assert the source `Plan` is
  `Assert.Same` before/after and is the `Aggregate.Child`, and that `GroupBy` alone builds no plan
  node.
- **The marquee non-vacuity proof** (`LazyEager/DataFrameAggregationLazyTests`) chains
  `GroupBy(...).Agg(...)`, `GroupBy(...).Count()`, and global `Agg(...)` over a `ThrowOnReadSource`
  whose `Read()` throws, and asserts nothing is read (`ReadCount == 0`).
- **The audit-seam guard** (same file, `GroupByAgg_TouchesNoAuditSeam`) asserts no file opened, no
  rows read, and an **empty stage path** (no Analyzer/planner/backend stage entered), reusing the
  #169 `RecordingAudit`.

## 8. AC → test map

| AC | Requirement | Tests |
| --- | --- | --- |
| **AC1** | `GroupBy` returns a grouped handle recording grouping expressions **without executing**; the handle is not a `DataFrame`. | `GroupBy_Columns_RecordsGroupingExpressionsInOrder`, `GroupBy_Names_RecordsUnresolvedAttributeGroupingExpressions`, `GroupBy_SingleName_RecordsSingleGroupingExpression`, `GroupBy_NoColumns_RecordsEmptyGrouping`, `GroupBy_DoesNotBuildAnAggregate_UntilAggIsChosen` |
| **AC2** | `Agg` (grouped) builds `Aggregate` with grouping + retained keys ⧺ aggregate exprs and Spark aliases; global `df.Agg` builds `Aggregate` with empty grouping. | `Agg_BuildsAggregateWithGroupingAndRetainedKeysThenAggregate` (also pins retained-key `Assert.Same` structural sharing), `Agg_HonorsUserAliasOnAggregateOutput`, `Agg_BareAggregate_IsLeftUnaliased_ForAnalyzerNaming`, `Agg_MultipleAggregates_AppendInOrderAfterRetainedKeys`, `Count_BuildsAggregateWithNamedCountAfterRetainedKeys`, `GlobalAgg_BuildsAggregateWithEmptyGrouping`, `GlobalAgg_IsEquivalentToGroupByNoKeysThenAgg`, `GlobalAgg_MultipleAggregates_AppendInOrder`, `GlobalCount_BuildsAggregateWithEmptyGroupingAndSingleCountColumn` |
| **AC3** | Invalid aggregate input is reported by the **analyzer** (output derivation OR `CheckAnalysis`, whichever fires first), not the API; the API builds a well-formed unresolved plan (deep validation deferred to #171). | `Analyzer_RealAggregateFunction_ReportsDeferredResolution_NotApiExecution` (message pinned); complex-key boundary via `Analyzer_ComplexGroupingKey_ThrowsDeterministically_TrackedUnder171`; structural derivation + parent-`Project` interplay via `Analyzer_DerivesAggregateOutput_AsGroupingAttributesThenAggregateAliases` |
| **AC4** | Method names + chaining order recognizable to Spark users (`df.GroupBy("k").Agg(Functions.Sum(...).As("total"))`). | `Agg_*` (surface + shape), `GroupByAgg_ChainsOverPriorTransformations_LeavingEachStageIntact` |
| **Lazy** | Transformations do no work; the source frame is unchanged. | `Agg_LeavesSourceFrameUnchanged_AndSharesChildByReference`, `DataFrameAggregationLazyTests.*` |
| **Guards** | Null/empty argument rejection. | `GroupBy_Null*`/`GroupBy_Empty*`, `Agg_Null*`, `GlobalAgg_NullFirstExpr_Throws`, `GlobalAgg_NullExprsArray_Throws`, `GlobalAgg_NullExprElement_Throws` |
