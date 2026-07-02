# DataFrame transformations — Select, Filter/Where, WithColumn (M1)

> **Status:** living document. Created with
> [STORY-04.2.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md)
> (issue [#160](https://github.com/khaines/deltasharp/issues/160), FEAT-04.2 — the first
> `DataFrame` transformation surface). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager execution) and builds directly on
> [column-and-functions.md](column-and-functions.md) (the public `Column`/`Functions` door, #164),
> [logical-plan-nodes.md](logical-plan-nodes.md) (the immutable `Project`/`Filter` IR and structural
> sharing, #167), [analyzer-resolution.md](analyzer-resolution.md) (star expansion and name
> resolution, #170), [lazy-eager-audit.md](lazy-eager-audit.md) (the audit seam that proves laziness,
> #169), and [api-governance.md](api-governance.md) / [repository-layout.md](repository-layout.md).
> Update it whenever the transformation surface, the plan mapping, or the `WithColumn` naming rules
> change.

## 1. What this is (and is not)

This is the **first transformation surface** on `DataFrame` — the .NET-idiomatic mirror of Apache
Spark's `Dataset.select`, `Dataset.filter`/`where`, and `Dataset.withColumn`. It adds five public
methods to `DeltaSharp.DataFrame` (`src/DeltaSharp.Core/Session/DataFrame.cs`):

| Public method | Spark parity | Result plan |
| --- | --- | --- |
| `Select(params Column[] columns)` | `select(cols: Column*)` | `Project(columns, this.Plan)` |
| `Select(string column, params string[] columns)` | `select(col: String, cols: String*)` | `Project(Col(...)…, this.Plan)` |
| `Filter(Column condition)` | `filter(condition: Column)` | `Filter(condition, this.Plan)` |
| `Where(Column condition)` | `where(condition: Column)` | delegates to `Filter` |
| `WithColumn(string colName, Column col)` | `withColumn(colName, col)` | `Project([*, col AS colName], this.Plan)` |

Every method is a **transformation**: it constructs an immutable logical-plan node over `this.Plan`
and returns a **new** `DataFrame`. It is **not** analysis, optimization, physical planning, or
execution. Building the plan consults **no** schema, evaluates **no** predicate, and makes **no**
scan or backend call — the lazy half of the lazy/eager invariant
([ADR-0001](../../adr/0001-execution-strategy.md)). Actions (`Collect`/`Count`/`Show`/`Write`) — the
only things that execute — arrive in later FEAT-04.2 stories and are out of scope here.

The internal IR stays hidden: every method takes and returns only `Column`/`DataFrame`/`string` and
unwraps `Column.Expr` (`internal`) to build the plan node, so `Expression`, `LogicalPlan`, and
`DeltaSharp.Plans.*` never appear on the public surface. The five methods are the only additions to
`PublicAPI.Unshipped.txt`.

## 2. The lazy invariant and how it is proven

A transformation must only **extend** the plan; the source `DataFrame` must be observably unchanged
(immutable plans → structural sharing, #167). Two properties hold for each method:

1. **No work.** The method body only allocates plan nodes; it never touches the source scan, the
   analyzer, or the backend. `DataFrame` is a session-free plan wrapper — it holds no `SparkSession`,
   catalog, or reader (`internal DataFrame(LogicalPlan plan)` is its whole state), so it *cannot*
   resolve or execute even if it wanted to.
2. **Immutability / structural sharing.** The new `Project`/`Filter` takes `this.Plan` as its child
   by reference. The source frame's `Plan` is the same instance before and after; the two frames
   share the entire source subtree.

This is proven two ways:

- **Plan-shape + immutability tests** assert the source `Plan` is `Assert.Same` before/after and is
  the new node's `Child` (`DataFrameTransformationsTests`).
- **The marquee non-vacuity proof** (`DataFrameLazyTransformationTests`) chains all five methods over
  a `ThrowOnReadSource` whose `Read()` throws, and asserts nothing is read (`ReadCount == 0`, no
  exception). Companion tests run a similar transformation chain inside an `ExecutionAudit` scope over
  a `FakeSource` and assert `RecordingAudit.ObservedNoExecution` (no file opened, no row read, no
  analyzer/planner/backend stage entered) — and, specifically, that the **Analyzer** stage is never
  entered, so an eager-analyze regression in any transform reddens. `Analyzer.Resolve` emits the #169
  `ExecutionStage.Analyzer` milestone, and a dedicated non-vacuity test drives the real analyzer
  inside the scope to prove that guard is not a false green. This mirrors the #169
  `LazyEagerAuditTests` pattern but exercises the **real public API** rather than raw node
  construction — the standing guard that #160's transformations stay lazy.

## 3. Select — projection

`Select` maps to a `Project` whose `ProjectList` is the columns, in order, over `this.Plan`.

- **`Select(params Column[] columns)`** unwraps each `Column.Expr` into the project list. A star
  column (`Functions.Col("*")` → `UnresolvedStar`) is preserved **unexpanded**; the analyzer expands
  it to the child's output at resolution (see §5). Aliased columns (`Col("age").As("years")` →
  `Alias`) are preserved so the analyzer keeps the output name.
- **`Select(string column, params string[] columns)`** turns each name into a `Column` via
  `Functions.Col`, so `"*"` becomes a star and every other name an `UnresolvedAttribute`. The rest
  names reuse `Functions.Col`'s null/empty guard per element; the first name (`column`) is guarded
  explicitly first (an `ArgumentException.ThrowIfNullOrEmpty(column)` that names the `column`
  parameter) and then again by `Functions.Col`.

### Overload shape (why the string overload takes a required first name)

Spark's string overload is `select(col: String, cols: String*)` — the first name is **required**.
DeltaSharp mirrors this exactly, and it is also what keeps `df.Select()` unambiguous: with a
`params Column[]` overload and a `params string[]`-only overload, a no-argument `Select()` call would
be an ambiguous method call (CS0121). Making the string overload's first parameter required means
`Select()` binds unambiguously to `Select(params Column[])` with an empty array.

### Empty select

`Select()` (no arguments) builds `Project([], this.Plan)` — a **zero-column** projection — matching
Spark's `select()`, which returns an empty-schema DataFrame. `Project` accepts an empty project list
(`PlanCollections.ToImmutable` permits an empty collection), so this is a well-formed lazy plan; what
an *action* over a zero-column frame yields is a later-story concern.

## 4. Filter / Where — row selection

`Filter(Column condition)` maps to `Filter(condition.Expr, this.Plan)`. The predicate is **recorded
by reference** — never evaluated, never rewritten (tests assert `Assert.Same(condition.Expr,
filter.Condition)`). `Where` is a Spark-parity synonym that delegates to `Filter`, so the two produce
an equal plan.

### Deferred: `Where(string)` SQL-predicate overload

Spark also offers `where(conditionExpr: String)` / `filter(conditionExpr: String)`, which parse a SQL
predicate string. That requires the SQL expression parser (ADR-0007, EPIC-07 SQL frontend —
STORY-07.2.1 / [#217](https://github.com/khaines/deltasharp/issues/217)), which does not exist in M1.
Rather than half-build it, this overload is **omitted**; it lands with the SQL frontend. Only the
`Column`-typed overloads ship here, and the `Filter`/`Where` XML `<remarks>` point users at that
follow-up so the deferral is discoverable from quick-info.

## 5. WithColumn — add or replace a derived column

`WithColumn(colName, col)` maps to `Project([UnresolvedStar, col.Expr AS colName], this.Plan)`: an
`UnresolvedStar` (which the analyzer expands to the frame's full output) followed by the new column
aliased to `colName`. The column is always re-aliased to `colName` (Spark's `withColumn(name, col)`
names the output by `name`), so a pre-aliased `col` gets the `WithColumn` name.

### Spark's naming rule (append vs replace) and where DeltaSharp draws the line

Spark's `withColumn` has two behaviours keyed on the output schema:

- **Append** — `colName` does not match an existing column → the new column is added at the **end**.
- **Replace-in-place** — `colName` matches an existing column → the new column **overwrites** it,
  **preserving the original column's position**.

In Spark this replace-vs-append decision is made **at the API level**, because Spark's `withColumn`
reads `queryExecution.analyzed.output` (running — but caching — analysis) to know the schema and
rewrites the projection explicitly. DeltaSharp's `DataFrame` is a **lazy, session-free plan wrapper**
with no catalog/analyzer handle, so it cannot resolve the schema at `WithColumn`-build time without
triggering analysis — which would break the lazy invariant this story exists to uphold. DeltaSharp
therefore builds the **correct unresolved shape** (`Project([*, col AS colName])`) and treats
replace-vs-append as a **name-resolution concern the analyzer owns** — a deliberate, documented
deviation from Spark's API-level implementation, forced by (and consistent with) our lazy design.

**Consequence for M1:**

- **Append is fully correct end-to-end today.** The analyzer's `ExpandStars`
  (`Analyzer.ExpandStars`, `analyzer-resolution.md` §star expansion) expands `*` in place to the
  child's output and then the aliased new column is appended — exactly Spark's append. This is proven
  by `WithColumn_Append_ResolvesToOriginalColumnsPlusNewColumn`, which registers a schema, calls
  `WithColumn` with a genuinely new name, runs the analyzer, and asserts the resolved `Project`
  output is `[id, name, age, age_plus]`.
- **Replace-on-duplicate is deferred to the analyzer.** The current analyzer expands the star and
  does **not** dedupe against a following same-named alias (`ExpandStars` preserves surrounding
  elements verbatim; there is no replace pass), so `WithColumn("age", …)` would today resolve to two
  `age` outputs. Implementing replace *inside generic star expansion* would be **wrong**, because
  Spark's plain `select($"*", expr.as("age"))` intentionally keeps duplicates — replace is specific
  to the `withColumn` intent, not to star expansion. Adding a faithful replace therefore needs a
  distinct signal (a marker on the projection or a dedicated analyzer rule) and is tracked as
  follow-up analyzer work under FEAT-04.5
  ([#398](https://github.com/khaines/deltasharp/issues/398)).
  This story ships the correct unresolved plan shape and asserts it
  (`WithColumn_ReplacingExistingName_BuildsSameStarPlusAliasShape`).

This choice — **option (b): build the correct unresolved shape, defer replace resolution to the
analyzer, and assert the plan shape** — keeps the change surgical, preserves the lazy invariant, and
avoids corrupting `select`'s duplicate-keeping semantics.

## 6. Public API surface

Five members are added to `src/DeltaSharp.Core/PublicAPI.Unshipped.txt` (RS0016/RS0017 under
`-warnaserror`), grouped with the existing `DeltaSharp.DataFrame` entries:

```
DeltaSharp.DataFrame.Filter(DeltaSharp.Column! condition) -> DeltaSharp.DataFrame!
DeltaSharp.DataFrame.Select(params DeltaSharp.Column![]! columns) -> DeltaSharp.DataFrame!
DeltaSharp.DataFrame.Select(string! column, params string![]! columns) -> DeltaSharp.DataFrame!
DeltaSharp.DataFrame.Where(DeltaSharp.Column! condition) -> DeltaSharp.DataFrame!
DeltaSharp.DataFrame.WithColumn(string! colName, DeltaSharp.Column! col) -> DeltaSharp.DataFrame!
```

No `Expression`/`LogicalPlan`/`DeltaSharp.Plans.*` type appears — the IR stays internal.

## 7. Argument guards

| Method | Guard |
| --- | --- |
| `Select(Column[])` | `columns` non-null; each element non-null (`ArgumentNullException`) |
| `Select(string, string[])` | `column` non-null/empty; `columns` non-null; each name non-null/empty (via `Functions.Col`) |
| `Filter` / `Where` | `condition` non-null (`ArgumentNullException`) |
| `WithColumn` | `colName` non-null/empty (`ArgumentException`); `col` non-null (`ArgumentNullException`) |

`ArgumentException.ThrowIfNullOrEmpty` raises `ArgumentNullException` for null and `ArgumentException`
for empty; the tests assert with `ThrowsAny<ArgumentException>` for the null-or-empty string guards.

## 8. AC → test map

| AC | Requirement | Tests (`DeltaSharp.Core.Tests`) |
| --- | --- | --- |
| **AC1** | `Select` (Column & string) → `Project`; star preserved; aliases preserved; empty select; original unchanged + structural sharing | `Select_Columns_BuildsProjectWithExpressionsInOrder`, `Select_PreservesAliasExpressions`, `Select_StarColumn_IsPreservedUnexpanded`, `Select_Empty_BuildsEmptyProjection`, `Select_LeavesSourceFrameUnchanged_AndSharesChildByReference`, `Select_Names_BuildsProjectOfUnresolvedAttributes`, `Select_SingleName_BuildsSingleAttributeProjection`, `Select_StarName_BuildsUnresolvedStar` |
| **AC2** | `Filter`/`Where` → `Filter`; `Where` == `Filter`; predicate unevaluated; original unchanged | `Filter_BuildsFilterWithConditionExpressionUnevaluated`, `Where_IsEquivalentToFilter`, `Filter_LeavesSourceFrameUnchanged` |
| **AC3** | `WithColumn` builds `Project([*, col AS name])`; append resolves end-to-end; replace builds same shape (in-place replace deferred to #398, with a characterization tripwire) | `WithColumn_BuildsProjectOfStarThenAliasedColumn`, `WithColumn_ReplacingExistingName_BuildsSameStarPlusAliasShape`, `WithColumn_RewrapsAlreadyAliasedColumnWithGivenName`, `WithColumn_Append_ResolvesToOriginalColumnsPlusNewColumn`, `WithColumn_ReplacingExistingName_ResolvesToDuplicateColumns_KnownWrong_Tripwire` |
| **AC4** | chaining over a throw-on-read source triggers no scan/backend and no analysis (lazy proof) | `ChainedTransformations_OverThrowOnReadSource_NeverRead`, `ChainedTransformations_TouchNoAuditSeam`, `ChainedTransformations_NeverEnterTheAnalyzerStage`, `EagerAnalyzeMutation_IsRecordedByTheSeam`, `ChainedTransformations_BuildNestedPlan_LeavingEachStageIntact` |
| Guards | null/empty argument guards | `Select_NullColumnsArray_Throws`, `Select_NullColumnElement_Throws`, `Select_NullFirstName_Throws`, `Select_EmptyFirstName_Throws`, `Select_NullRestName_Throws`, `Select_NullRestElement_Throws`, `Select_EmptyRestElement_Throws`, `Filter_NullCondition_Throws`, `Where_NullCondition_Throws`, `WithColumn_NullName_Throws`, `WithColumn_EmptyName_Throws`, `WithColumn_NullColumn_Throws` |
