# Functions Registry — the common functions surface (M1)

> **Status:** living document. Created with
> [STORY-04.3.3](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md)
> (issue [#166](https://github.com/khaines/deltasharp/issues/166), FEAT-04.3 — the common functions
> registry: `count`/`sum`/`avg`/`min`/`max`, `when`, `coalesce`, `expr`, and string/date helpers).
> Grounded in [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager execution) and
> [ADR-0008](../../adr/0008-type-system-row-format.md) (type system + row format). Builds directly
> on [column-and-functions.md](column-and-functions.md) (`Col`/`Column`/`Lit`, #164),
> [expression-model.md](expression-model.md) (the internal expression IR, #168),
> [analyzer-resolution.md](analyzer-resolution.md) (how unresolved functions bind, #170/#171), and
> [api-governance.md](api-governance.md) (the `PublicAPI` baseline). Update it whenever the public
> `Functions` surface, the `when`/`CaseWhen` model, the `expr` decision, or the unsupported-feature
> policy changes.

## What this is (and is not)

This is the **common functions library** — the .NET-idiomatic mirror of Apache Spark's
`org.apache.spark.sql.functions` — layered on top of the `Col`/`Column`/`Lit` door
([column-and-functions.md](column-and-functions.md)). It adds a set of static entry points on the
existing `public static class Functions` (`src/DeltaSharp.Core/Functions.cs`, namespace `DeltaSharp`)
plus the conditional-expression builders `Column.When`/`Column.Otherwise`
(`src/DeltaSharp.Core/Column.cs`).

Every entry point builds exactly **one immutable, unresolved node** of the internal expression IR and
returns it wrapped in a `Column`:

- most functions build an
  [`UnresolvedFunction`](../../../src/DeltaSharp.Core/Plans/Expressions/UnresolvedFunction.cs)
  (`'name(args)`), the same node Spark uses (`UnresolvedFunction`);
- `when(...)` builds a new
  [`CaseWhen`](../../../src/DeltaSharp.Core/Plans/Expressions/CaseWhen.cs) node (Spark `CaseWhen`).

It is **not** evaluation, analysis, schema binding, function classification, or type coercion. Those
are the analyzer's job (FEAT-04.5 — name resolution #170, function/aggregate classification and type
coercion #171). Building a function **does zero work**, the lazy half of the lazy/eager invariant
([ADR-0001](../../adr/0001-execution-strategy.md)): an `UnresolvedFunction`/`CaseWhen` over unresolved
inputs stays unresolved (`Resolved == false`, `Type == null`) until the analyzer resolves it.

The `expr(string)` SQL door is intentionally **not** implemented here (see [§ `expr`](#the-expr-decision-ac3)):
the SQL expression parser is the SQL frontend (EPIC-07 / #159).

## The M1 function set (parity matrix)

The M1 registry set below is delivered by this story. "Kind" is the Spark classification the analyzer
(#171) applies by canonical name — this surface only guarantees faithful naming (see
[§ aggregate-vs-scalar](#aggregate-vs-scalar-classification-ac2)). Every function is **lazy** and
builds an unresolved node; no listed function deviates from Spark semantics (deviations are called out
in the Notes column).

| Spark `functions.*` | DeltaSharp `Functions.*` | IR node built | Kind | Status | Notes |
| --- | --- | --- | --- | --- | --- |
| `count(col)` | `Count(Column)` | `'count(x)` | aggregate | ✅ delivered | |
| `count(colName)` | `Count(string)` | `'count(x)` | aggregate | ✅ delivered | pass `"*"` for `count(*)` (builds `'count(*)`) |
| `countDistinct(col, cols…)` / `countDistinct(colName, colNames…)` | `CountDistinct(Column, params Column[])` / `CountDistinct(string, params string[])` | `'count(distinct …)` | aggregate | ✅ delivered | sets `IsDistinct`; tail may be empty |
| `sum(col)` / `sum(colName)` | `Sum(Column)` / `Sum(string)` | `'sum(x)` | aggregate | ✅ delivered | |
| `avg(col)` / `avg(colName)` | `Avg(Column)` / `Avg(string)` | `'avg(x)` | aggregate | ✅ delivered | `mean` synonym deferred |
| `min(col)` / `min(colName)` | `Min(Column)` / `Min(string)` | `'min(x)` | aggregate | ✅ delivered | |
| `max(col)` / `max(colName)` | `Max(Column)` / `Max(string)` | `'max(x)` | aggregate | ✅ delivered | |
| `coalesce(cols…)` | `Coalesce(params Column[])` | `'coalesce(…)` | scalar | ✅ delivered | ≥1 arg |
| `when(cond, val)` | `Functions.When(Column, object?)` | `CaseWhen` | scalar (conditional) | ✅ delivered | chained via `Column.When`/`.Otherwise` |
| `Column.when(cond, val)` | `Column.When(Column, object?)` | extended `CaseWhen` | scalar | ✅ delivered | only after `When(...)` |
| `Column.otherwise(val)` | `Column.Otherwise(object?)` | closed `CaseWhen` | scalar | ✅ delivered | only after `When(...)`, once |
| `upper(col)` | `Upper(Column)` | `'upper(x)` | scalar (string) | ✅ delivered | |
| `lower(col)` | `Lower(Column)` | `'lower(x)` | scalar (string) | ✅ delivered | |
| `length(col)` | `Length(Column)` | `'length(x)` | scalar (string) | ✅ delivered | |
| `trim(col)` | `Trim(Column)` | `'trim(x)` | scalar (string) | ✅ delivered | |
| `concat(cols…)` | `Concat(params Column[])` | `'concat(…)` | scalar (string) | ✅ delivered | ≥1 arg |
| `current_date()` | `CurrentDate()` | `'current_date()` | scalar (date) | ✅ delivered | 0 args |
| `current_timestamp()` | `CurrentTimestamp()` | `'current_timestamp()` | scalar (date) | ✅ delivered | 0 args |
| `year(col)` | `Year(Column)` | `'year(x)` | scalar (date) | ✅ delivered | |
| `month(col)` | `Month(Column)` | `'month(x)` | scalar (date) | ✅ delivered | |
| `dayofmonth(col)` | `DayOfMonth(Column)` | `'dayofmonth(x)` | scalar (date) | ✅ delivered | |
| `to_date(col)` | `ToDate(Column)` | `'to_date(x)` | scalar (date) | ✅ delivered | format-string overload deferred |
| `expr(sqlText)` | `Expr(string)` | — (throws) | — | ⛔ unsupported | throws `NotSupportedException` → EPIC-07 / #159 |

**Deliberately out of M1 (deferred, not built):** `mean` (alias of `avg`), `to_timestamp`,
`date_format`, `substring`, `regexp_*`, arbitrary format-string overloads, and the long tail of the
Spark functions catalog. They are additive and can land in later stories without breaking this
surface. This is a **representative** M1 set — the aggregates and conditional/coalesce named in the
story, plus a focused string/date helper set — not the full catalog, per the "don't over-build"
guidance.

### Method shapes and guards

- **`(Column)` + `(string)` overloads.** The aggregates (`Count`/`Sum`/`Avg`/`Min`/`Max`) provide both
  a `Column` overload and a `string` convenience overload; the `string` overload just calls
  `Col(name)` (which rejects null/empty), mirroring Spark's twin `count(e: Column)` /
  `count(columnName: String)`. `CountDistinct` likewise offers a `string` variadic overload
  (`CountDistinct(string, params string[])`) that resolves each name through `Col(...)`, mirroring
  Spark's `countDistinct(String, String...)`. String/date helpers take a `Column` only.
- **`params Column[]` variadics.** `Coalesce` and `Concat` require a **non-empty** params array —
  the shared `NaryFunction` helper throws `ArgumentException` for an empty array. `CountDistinct`'s
  trailing `params` (`additional` / `additionalColumnNames`) **may be empty**: its mandatory leading
  `column`/`columnName` argument already guarantees **≥1 column total** (see
  `CountDistinct_SingleColumn_HasOneArg`), so only that leading arg is required. In all cases
  `NaryFunction` also throws `ArgumentNullException` for a null array and `ArgumentException` for any
  null element.
- **Unary builders** (`UnaryFunction` helper) throw `ArgumentNullException` for a null `Column`.
- **`When(condition, value)`** rejects a null `condition` (`ArgumentNullException`); `value` is
  wrapped through `Lit(object?)`, so a raw literal becomes a `Literal` and an existing `Column` passes
  through unchanged (Spark's `when(cond, Any)` semantics, reusing `Lit`'s idempotence).
- **`count(*)`** is expressed as `Count("*")`: `Col("*")` builds an `UnresolvedStar`, so the call
  yields `'count(*)` — no special-casing beyond the existing star handling in `Col`.

## Aggregate-vs-scalar classification (AC2)

This surface does **not** classify functions; it only **names** them. `Count`/`Sum`/`Avg`/`Min`/`Max`
build an `UnresolvedFunction` whose `Name` is the canonical Spark aggregate name (`"count"`, `"sum"`,
…), and `coalesce`/string/date helpers build one named with the canonical scalar name. Whether a call
is an aggregate is decided **later, by canonical name**, when the analyzer's function registry
resolves `UnresolvedFunction` inside an `Aggregate` operator (STORY-04.5.2 / #171 — now delivered:
`FunctionRegistry` binds by canonical name and stamps `FunctionKind.Scalar`/`Aggregate`).

This is faithful to Spark and to the analyzer contract: `FunctionRegistry.Bind` binds a known
function during the coercion sub-pass and classifies it by canonical name, and `CheckAnalysis`
rejects an aggregate used outside a valid aggregate context (`MisplacedAggregate`); an unknown
function fails as `UnresolvedFunction` (see [function-binding-coercion.md](function-binding-coercion.md)
§2/§5). So AC2 is satisfied here by producing
**correctly-named** unresolved nodes with the right arity, argument order, and `IsDistinct` flag — the
exact inputs #171 needs to distinguish aggregate from scalar. No node on this surface carries an
"is-aggregate" bit; the name is the contract.

## The `when` / `CaseWhen` model

Spark's conditional is `CaseWhen(branches: Seq[(condition, value)], elseValue: Option[value])`, entered
via `functions.when(cond, val)` and chained with `Column.when(...)` / `Column.otherwise(...)`. No
`CaseWhen` node existed in the IR before this story, so a new **internal** node
`DeltaSharp.Plans.Expressions.CaseWhen` was added, modeled exactly on the existing composite nodes
(`And`/`Or`, `src/DeltaSharp.Core/Plans/Expressions/BooleanExpressions.cs`).

### The node (`CaseWhen.cs`)

- **Children are the flattened branch expressions** in evaluation order:
  `[c0, v0, c1, v1, …, cN, vN, (else?)]`. All state lives in `Children`, so the base `TreeNode`
  machinery (structural sharing, structural equality, deterministic hashing, `TransformUp`/`Down`,
  tree rendering) applies unchanged.
- **Branch/else split derives from parity.** An **odd** child count carries an `ElseValue`
  (`HasElse => (Children.Count & 1) == 1`); `BranchCount => Children.Count / 2`. Because the split is
  derivable from the child count, the node has **no extra own-state**: `NodeEquals` returns `true` and
  `NodeHashCode` returns `PlanHash.Seed` (identical to `And`/`Or`), and two `CaseWhen`s are equal
  exactly when their children are.
- **Lazy hints.** Before analysis `Type` is `null` (the common type across branch values needs
  coercion — an analyzer concern), `Nullable` is **derived** from
  the possible result values — nullable if any branch value is nullable, the else value is nullable, or
  there is **no** else (an unmatched row then yields an implicit SQL `NULL`); a `CASE` with an else and
  all-non-null values is therefore non-nullable (conditions do not affect result nullability). `Resolved`
  follows the default "all children resolved" rule — so a `CaseWhen` over an unresolved condition stays
  unresolved. It is **not** an unresolved *marker*: it becomes resolved once its children are. As of
  STORY-04.5.2 / #171, once resolved `CaseWhen.Type` **derives** the common type of its branch/else
  values via `TypeCoercion.FindWiderCommonType` (returning `null` only while a value is still untyped),
  and the analyzer's coercion pass widens the values and rejects non-boolean conditions or
  incompatible values — so a resolved `CaseWhen` is now typed (or rejected), and the null-typed guard
  in `CheckAnalysis` backstops it. The precedent for this "type is a function of children" shape is
  **`BinaryArithmetic`** (not `And`/`Or`, which override `Type => BooleanType`); see
  [function-binding-coercion.md](function-binding-coercion.md) §3.4.
- **Immutable builders.** `AddBranch(condition, value)` returns a new `CaseWhen` with one branch
  appended; `WithElse(value)` returns a new `CaseWhen` with the else set. Both reject the operation
  once an else is present (`InvalidOperationException`), enforcing Spark's rule that `when` cannot
  follow `otherwise` and `otherwise` cannot be set twice. The minimum-arity guard (≥ one full branch)
  lives in the private constructor.
- **Rendering.** `SimpleString` renders `CASE WHEN <c> THEN <v> … [ELSE <e>] END`, folding children's
  inline renders (so unresolved leaves keep their leading apostrophe), e.g.
  `CASE WHEN 'flag THEN 1 WHEN 'other THEN 2 ELSE 0 END`.

### The public builders

`Functions.When(Column condition, object? value)` returns `new Column(new CaseWhen(condition.Expr,
Lit(value).Expr))`. Chaining lives on `Column` because Spark chains on `Column`:

- `Column.When(Column condition, object? value)` requires the wrapped `Expr` to be a `CaseWhen`
  (else `InvalidOperationException`, matching Spark's "when() can only be applied on a Column
  previously generated by when()"), then returns `caseWhen.AddBranch(...)`.
- `Column.Otherwise(object? value)` likewise requires a `CaseWhen`, then returns `caseWhen.WithElse(...)`.

These two members are added to `Column` (not to #165's operator surface) because they are conditional
builders tightly coupled to `CaseWhen`, and the story assigns them here. They consume only
`Column`/`Column.Expr` and `Lit`, so they carry **no dependency on the #165 operator work** (now merged
into this PR's base): the user builds the boolean `condition` with #165's operators, and this registry
just consumes the resulting `Column`.

## The `expr` decision (AC3)

Spark's `functions.expr(sqlText)` parses a SQL expression string into a `Column`. The SQL expression
**parser is not in M1 Core** — it is the SQL frontend (EPIC-07 / issue #159). Rather than half-build a
parser or ship a placeholder IR node that nothing can consume, `Functions.Expr(string)` is present on
the surface (so the Spark vocabulary is discoverable) but throws a **documented unsupported-feature
diagnostic**: after validating a non-empty argument (`ArgumentException.ThrowIfNullOrEmpty`), it throws
`NotSupportedException` whose message points at EPIC-07 / #159 and recommends the typed builders. This
is the concrete exemplar of the [unsupported-feature policy](#unsupported-feature-policy-ac3) and
satisfies AC3's "a clear `NotSupportedException`" option. When #159 lands, `Expr` becomes the door that
delegates to the SQL expression parser; the signature does not change.

## Unsupported-feature policy (AC3)

A Spark function is "unsupported in M1" in one of two documented ways:

1. **Not on the surface.** The function simply is not a member of `Functions`; the parity matrix lists
   it as deferred (e.g. `mean`, `substring`, `to_timestamp`). The compiler reports the missing member,
   and this doc records why.
2. **On the surface, throws a diagnostic.** Where keeping the Spark name discoverable is valuable but
   the capability is unavailable, the member throws a clear `NotSupportedException` naming the owning
   milestone/issue. `Expr(string)` is the M1 instance.

Argument-validation failures use the repo's standard argument exceptions (`ArgumentNullException`,
`ArgumentException`) matching the `Col`/`Lit` door and the IR constructors — deterministic, no backend
call, no execution.

## Lazy / no-work guarantee

Constructing any function performs **no** schema lookup and **no** evaluation:

- Aggregates and scalar helpers build an `UnresolvedFunction`, whose `Resolved` is always `false` and
  whose `Type` is `null` — only the analyzer (#170/#171) resolves and classifies it.
- `When(...)` builds a `CaseWhen` whose resolution follows its (unresolved) condition/value children;
  no branch is evaluated and no comparison is run.
- `Expr(...)` builds nothing (it throws) — no partial SQL parse.

The IR nodes are immutable (structural sharing from #167/#168): chaining `When(...)`/`Otherwise(...)`
**re-wraps** into a new `CaseWhen`/`Column` and never mutates the source column
(`Chaining_DoesNotMutateSource`). No function on this surface touches a data source or runs a kernel.

## Performance characteristics (`CaseWhen` is a wide, flat node)

`CaseWhen` flattens `N` branches + an optional else into `2N+1` children, so its **depth stays ~2**
regardless of branch count. Two consequences follow, both **acceptable and caller-bounded** for an
in-memory IR builder and tracked for a future optimization in
[#401](https://github.com/khaines/deltasharp/issues/401):

- **Fluent chaining is `O(N²)` time / `O(N)` memory.** Each `.When(...)` calls `AddBranch`, which copies
  the child array to build a new immutable node, so an `N`-branch CASE built by chaining is `O(N²)` total.
- **Equality / hash / render are `O(width)`.** `NodeEquals`/`NodeHashCode`/`SimpleString` iterate all
  `2N+1` children.

This is **not** a DoS surface: `CaseWhen` is bounded by *caller* code (a user authoring `.When()` calls),
has no unconstrained external-input vector at runtime, and the work is horizontal iteration — **not**
recursion, so it carries none of the stack-overflow risk that the `TreeNode.MaxDepth=1000` guard exists to
prevent (that guard bounds depth, not width). It is congruent with the equally-unbounded arity of
`Coalesce`/`Concat` varargs and nested-plan width.

## Public-surface & governance

All new members are `public static` on `Functions` (or public instance on `Column`) returning
`Column`; the IR (`UnresolvedFunction`, `CaseWhen`, `Expression`) stays `internal` and never leaks.
Every new member is registered in `src/DeltaSharp.Core/PublicAPI.Unshipped.txt` (RS0016/RS0017 are
build-breaking under `-warnaserror`; see [api-governance.md](api-governance.md)).

## AC → test mapping

Tests live in `tests/DeltaSharp.Core.Tests/FunctionsRegistryTests.cs` (the public functions surface)
and `tests/DeltaSharp.Core.Tests/Plans/Expressions/CaseWhenTests.cs` (the `CaseWhen` node + chaining).

| AC | Requirement | Tests |
| --- | --- | --- |
| **AC1** | Each M1 function builds a named unresolved node with documented args (name, arity, order, `IsDistinct`), `Resolved == false` | `FunctionsRegistryTests.Count_Column_*`, `Count_ColumnName_*`, `CountStar_*`, `CountDistinct_*`, `Aggregate_Column_*`, `Aggregate_ColumnName_*`, `Coalesce_BuildsScalarFunctionInOrder`, `Concat_*`, `StringHelper_*`, `CurrentDateAndTimestamp_*`, `DateHelper_*`, `UnresolvedFunction_RendersWithApostrophe`; `CaseWhenTests.When_*`, `WhenOtherwise_*`, `ChainedWhen_*`, `CaseWhen_RendersCaseSyntax`, `When_WrapsRawValueAsLiteral_*` |
| **AC2** | Aggregate calls are distinguishable by canonical Spark name (binding is the analyzer's job) | `FunctionsRegistryTests.Aggregate_Column_BuildsNamedUnaryFunction`, `Aggregate_ColumnName_BindsToColReference`, `Count_*`, `CountDistinct_SetsIsDistinctAndOrdersArgs` (name + `IsDistinct` are the classification inputs) |
| **AC3** | Unsupported Spark functions report a documented diagnostic | `FunctionsRegistryTests.Expr_IsUnsupported_ThrowsNotSupported`, `Expr_NullOrEmpty_ThrowsArgument`; guards `Coalesce_Empty/NullElement/NullArray_Throws`, `Concat_Empty_Throws`, `UnaryFunction_NullColumn_Throws` |
| **AC4** | Each function's XML doc states laziness and Spark deviation | XML docs on every member of `Functions.cs`/`Column.cs`; the [parity matrix](#the-m1-function-set-parity-matrix) Notes column |
| `when`/`CaseWhen` model | Chained `when`/`otherwise` build the right structure; Spark-parity misuse guards; immutability; structural equality; `Nullable` derived from branch/else values; changed-child round-trip | `CaseWhenTests.*` (`When_OnNonCaseWhenColumn_Throws`, `Otherwise_OnNonCaseWhenColumn_Throws`, `When_AfterOtherwise_Throws`, `Otherwise_Twice_Throws`, `When_NullCondition_Throws`, `Chaining_DoesNotMutateSource`, `Resolved_FollowsChildren`, `WithNewChildren_*` incl. `WithNewChildren_ChangedChild_RebuildsAndPreservesElseAndBranches`, `MapChildren_SwapsChild_PreservesElse`, `Equality_IsStructural`, `Equality_DifferentNodeType_IsNotEqual`, `Nullable_*` [`NoElse_IsNullable`, `ElseAndAllNonNullValues_IsNotNullable`, `NullableElse_IsNullable`, `NullableBranchValue_IsNullable`, `NullableConditionOnly_IsNotNullable`], `Constructor_SingleBranch_*`) |
| Lazy / determinism | Constructing functions does no work; `current_date`/`current_timestamp` build unresolved nodes capturing **no** wall clock | `FunctionsRegistryTests` (`Resolved == false`/`Type == null` asserted in `AssertFunction`); `CaseWhenTests.When_BuildsSingleBranchCaseWhen` (`Resolved == false`, `Type == null`); `CurrentDate_IsDeterministic_NoWallClockCaptured`, `CurrentTimestamp_IsDeterministic_NoWallClockCaptured` |
