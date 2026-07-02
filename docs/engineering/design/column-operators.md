# Column operators — arithmetic, comparison, boolean & null semantics (M1)

> **Status:** living document. Created with
> [STORY-04.3.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md)
> (issue [#165](https://github.com/khaines/deltasharp/issues/165), FEAT-04.3 — column operators and
> boolean/null semantics). Grounded in [ADR-0008](../../adr/0008-type-system-row-format.md) (type
> system + SQL three-valued logic) and [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager
> execution). Builds directly on [column-and-functions.md](column-and-functions.md) (STORY-04.3.1 #164,
> the public `Column`/`Functions` surface), [expression-model.md](expression-model.md) (the internal
> expression IR, #168), and [api-governance.md](api-governance.md) (the `PublicAPI` baseline). Update
> it whenever the public operator surface, the Spark→DeltaSharp name map, the literal-coercion rules,
> or the "API does not coerce types" contract changes.

## What this is (and is not)

This story adds the **operator surface** to the public `Column` type
(`src/DeltaSharp.Core/Column.cs`, namespace `DeltaSharp`) — the .NET-idiomatic mirror of Apache
Spark's column operators (`col + 1`, `col === lit`, `col.and(...)`, `col.isNull`, `col <=> other`).
STORY-04.3.1 delivered references/aliases/literals and deliberately deferred operators here (see the
`Column` XML remarks and [column-and-functions.md](column-and-functions.md) §"Operator forward-guidance
for #165").

Every operator is a **pure builder**: it wraps a new immutable node of the internal expression IR
(`DeltaSharp.Plans.Expressions`, all `internal`) and returns a new `Column`. It performs:

- **no evaluation** — nothing is computed; the engine evaluates only when an action runs
  ([ADR-0001](../../adr/0001-execution-strategy.md));
- **no schema lookup** — operand references stay unresolved until the analyzer (FEAT-04.5) binds them;
- **no type coercion or validation** — the API never inspects operand types, inserts casts, or rejects
  type mismatches (AC4). Operator *misuse* is the **analyzer's** job to report, under ADR-0008's SQL
  three-valued logic.

It is **not** the expression IR itself (that is `#168`, `internal`), **not** analysis/binding, and
**not** the boolean/null *evaluation* semantics (that is the interpreter, later). This story records
**structure** only; the three-valued-logic contract documented here is what the analyzer/executor will
honor.

The only files changed are `Column.cs` (the operator members), `PublicAPI.Unshipped.txt` (the new
public members), and the tests. `Functions`/`DataFrame` are untouched except that operators *reuse*
`Functions.Lit(object?)` for scalar coercion.

## The operator taxonomy

Operators wrap the pre-existing IR nodes (delivered by #168 — this story does **not** add any node).
The wrapped `Expression` stays `internal`: `Column` exposes only `Column`-returning members, and
`Column.Expr`/`Column(Expression)` remain `internal` (materialized to the test assembly via the
repo-wide `InternalsVisibleTo "<Assembly>.Tests"` in `Directory.Build.props`), so no public member ever
leaks `Expression` or `DeltaSharp.Plans.*`.

| Family | Internal node (`DeltaSharp.Plans.Expressions`) | Result `Type` hint | Nullable hint |
| --- | --- | --- | --- |
| Arithmetic | `BinaryArithmetic(left, right, ArithmeticOperator)` | `null` (analyzer coerces numeric/decimal result) | `left ‖ right` |
| Comparison | `BinaryComparison(left, right, ComparisonOperator)` | `BooleanType` (known pre-analysis) | `left ‖ right` |
| Boolean And/Or | `And(left, right)` / `Or(left, right)` | `BooleanType` | `left ‖ right` |
| Boolean Not | `Not(child)` | `BooleanType` | `child` |
| Null predicate | `IsNull(child)` / `IsNotNull(child)` | `BooleanType` | `false` (never yields NULL) |
| Null-safe eq | `EqualNullSafe(left, right)` | `BooleanType` | `false` (never yields NULL) |

The `ArithmeticOperator` (`Add`/`Subtract`/`Multiply`/`Divide`/`Remainder`) and `ComparisonOperator`
(`Equal`/`NotEqual`/`LessThan`/`LessThanOrEqual`/`GreaterThan`/`GreaterThanOrEqual`) enums are
`internal` (`ExpressionPrimitives.cs`) and never appear on the public surface — the public API selects
the operator by *method identity* (`Plus` → `Add`, `Gt` → `GreaterThan`, …).

### Why `BinaryArithmetic.Type` is `null` but comparisons/booleans are `BooleanType`

Arithmetic result type needs Spark's numeric promotion / decimal result-type coercion, which is the
analyzer's job, so `BinaryArithmetic` reports an unknown (`null`) type hint until analysis
(`BinaryOperatorExpressions.cs:8-11`). Comparisons, `And`/`Or`/`Not`, and the null predicates are
boolean by construction, so their `Type` is `BooleanType` even before analysis — the API records that
directly. This is verified by `ArithmeticMethod_*` (asserts `Type == null`) and `ComparisonMethod_*` /
`And_*` / `Or_*` / `Not_*` (assert `BooleanType`).

## Spark → DeltaSharp name map

Spark uses camelCase Scala/Python method names plus symbolic operators. DeltaSharp mirrors the
**semantics** with .NET **idioms**: PascalCase methods (always available) plus C# operator overloads
where they are unambiguous and safe. Every named method has a `Column` overload **and** an
`object?`-literal overload.

| Spark (Scala `Column`) | DeltaSharp method | C# operator | Internal node |
| --- | --- | --- | --- |
| `plus` / `+` | `Plus` | `+` | `BinaryArithmetic(Add)` |
| `minus` / `-` | `Minus` | `-` | `BinaryArithmetic(Subtract)` |
| `multiply` / `*` | `Multiply` | `*` | `BinaryArithmetic(Multiply)` |
| `divide` / `/` | `Divide` | `/` | `BinaryArithmetic(Divide)` |
| `mod` / `%` | `Mod` | `%` | `BinaryArithmetic(Remainder)` |
| `equalTo` / `===` | `EqualTo` | *(none — see below)* | `BinaryComparison(Equal)` |
| `notEqual` / `=!=` | `NotEqual` | *(none — see below)* | `BinaryComparison(NotEqual)` |
| `lt` / `<` | `Lt` | `<` | `BinaryComparison(LessThan)` |
| `leq` / `<=` | `Leq` | `<=` | `BinaryComparison(LessThanOrEqual)` |
| `gt` / `>` | `Gt` | `>` | `BinaryComparison(GreaterThan)` |
| `geq` / `>=` | `Geq` | `>=` | `BinaryComparison(GreaterThanOrEqual)` |
| `and` / `&&` | `And` | `&` | `And` |
| `or` / `\|\|` | `Or` | `\|` | `Or` |
| `functions.not` / `!col` / `~col` | `Not` | `!` | `Not` |
| `isNull` | `IsNull` | — | `IsNull` |
| `isNotNull` | `IsNotNull` | — | `IsNotNull` |
| `eqNullSafe` / `<=>` | `EqualNullSafe` | — | `EqualNullSafe` |

Ported code reads naturally: Spark `col("a").plus(1).gt(10).and(col("b").isNotNull)` becomes
`Functions.Col("a").Plus(1).Gt(10).And(Functions.Col("b").IsNotNull())`, or with operators
`(Functions.Col("a") + 1 > 10) & Functions.Col("b").IsNotNull()`.

### Method naming choices vs Spark

- **`EqualNullSafe`** (not Spark's `eqNullSafe`) — the method is named after the internal Catalyst node
  (`EqualNullSafe`) for immediate readability and to match the diagnostic `SimpleString` (`<=>`). The
  Spark name and symbol are documented on the member.
- **`Not()` is an instance method** (Spark exposes negation as `functions.not(col)` / Scala `!col` /
  PySpark `~col`, not `Column.not()`). An instance `Not()` is the most discoverable .NET shape and pairs
  with the `!`/`~`-style `operator !` below.

## The `==` landmine: why methods, not `operator ==`/`operator !=`

Equality is exposed **only** as `EqualTo`/`NotEqual` methods (and, in Scala-parity spirit, the
diagnostic `SimpleString` renders `=`/`<>`). DeltaSharp deliberately does **not** overload
`operator ==`/`operator !=` on `Column`. Overloading `==` on a reference type is a well-known .NET
landmine:

1. **`operator ==` must return `bool`.** A column expression `col == lit` must return a `Column` (an
   expression tree), which `operator ==` cannot do. Spark sidesteps this in Scala with `===`; C# has no
   `===`, so the only faithful option is a method.
2. **It collides with reference/null equality.** `col == null`, `ReferenceEquals`-style checks,
   `Dictionary`/`HashSet` identity, and `object.Equals` all rely on `==` meaning identity for reference
   types. Overloading it to build an expression would silently break every `col == null` guard and every
   container keyed on `Column`.
3. **It surprises tooling.** Analyzers, LINQ providers, and pattern matching assume `==` is a boolean
   predicate; returning a `Column` violates that.

Because we do not overload `==`/`!=`, `Column` keeps the default object-identity equality — `col == null`
still means "is this reference null", which the operator null-guards (below) and the analyzer both rely
on. This follows the forward-guidance recorded in
[column-and-functions.md](column-and-functions.md) §"Operator forward-guidance for #165" (issue #164).
Ordering comparisons (`< <= > >=`) have no such collision — they cannot be confused with identity — so
they **are** provided as operators in addition to `Lt/Leq/Gt/Geq`.

### Boolean operator choices (`&`/`|`/`!`, not `&&`/`||`)

C# cannot meaningfully overload `&&`/`||` for lazy user semantics (they require `operator true`/`false`
and short-circuit on the *runtime* value, which does not exist at plan-build time). DeltaSharp therefore
exposes boolean combinators as `And`/`Or`/`Not` methods and provides the **non-short-circuiting**
`operator &`/`operator |`/`operator !` as PySpark-aligned sugar (`col1 & col2`, `~col`). Non-short-
circuiting is correct here: building an expression tree must assemble *both* operands regardless — there
is nothing to short-circuit at construction time; SQL three-valued evaluation happens later in the
engine.

## Literal coercion rules

Every binary operator has two overloads:

- **`Column` overload** — e.g. `Plus(Column other)`. Null-guarded with `ArgumentNullException.ThrowIfNull`
  (a null `Column` operand is a programming error, distinct from a SQL `NULL` value).
- **`object?`-literal overload** — e.g. `Plus(object? value)`. The scalar is coerced through
  `Functions.Lit(value)` (STORY-04.3.1), which maps the .NET scalar to its ADR-0008 `DataType`, treats
  `null` as a **typed SQL `NULL`** literal, and passes an existing `Column` through unchanged
  (idempotence). So `col.Gt(5)` builds `GreaterThan(colExpr, Literal(5:int))`, and `col.Plus(3L)` builds
  `Add(colExpr, Literal(3:long))` — **without** any cast or widening inserted by the API (the analyzer
  promotes types).

The C# operators add a third, reversed form for scalar-on-the-left ergonomics: `operator +(object?,
Column)` (and the same for `- * / % < <= > >=`). Crucially, the reversed form preserves **operand
order**, it does not rewrite the operator: `5 < col` builds `LessThan(Literal(5), colExpr)` — not
`GreaterThan` — matching Spark, where `lit(5) < col` is `LessThan(lit, col)`. This is pinned by
`ComparisonOperator_WithScalar_KeepsOperandOrder`.

### Overload-resolution note: bare `null` binds to the `Column` overload

Because C# prefers the more-specific parameter type, a **bare** `null` argument
(`col.EqualNullSafe(null)`) binds to the `Column` overload — which then null-guards and throws — rather
than to the `object?` literal overload. To pass a SQL `NULL` *literal*, write `(object?)null` or the
explicit `Functions.Lit(null)` (or, idiomatically, use `col.IsNull()` / `col.EqualNullSafe((object?)null)`).
This is an inherent consequence of offering both overloads and is documented on
`EqualNullSafe(object?)`; the tests use `(object?)null` deliberately
(`EqualNullSafe_WithNull_CoercesToNullLiteral`, `ScalarOverload_NullValue_BuildsNullLiteral`).

## Boolean & null semantics (ADR-0008 three-valued logic)

DeltaSharp records the *structure* that the analyzer/executor will evaluate under **SQL three-valued
logic** ([ADR-0008](../../adr/0008-type-system-row-format.md): "SQL three-valued logic"). The node
docs (`BooleanExpressions.cs`, `NullExpressions.cs`) already state the contract this story wires to the
public API:

- **`And`/`Or`** are nullable `BooleanType` (a `NULL` operand can make the result `NULL` — e.g.
  `true AND NULL = NULL`). The nullability *hint* is the OR of operands' hints (conservative).
- **`Not`** follows 3VL: `NOT NULL = NULL`, so `Not.Nullable` follows its child. Pinned by
  `Not_NullabilityFollowsChild` (a nullable comparison child ⇒ nullable `Not`; a non-null `IsNull`
  child ⇒ non-null `Not`).
- **`IsNull`/`IsNotNull`** inspect validity and **never** yield SQL `NULL` — their nullability hint is
  `false`. They are **distinct node kinds** (not one negated by the other), preserving Spark's behavior.
  Pinned by `IsNull_And_IsNotNull_AreDistinctNodeKinds`.
- **`EqualNullSafe` (`<=>`)** treats two `NULL`s as **equal** and `NULL` vs non-`NULL` as not-equal, so
  it never yields `NULL` (hint `false`) — semantically **distinct** from `EqualTo` (`=`), whose
  `NULL = NULL` is the unknown `NULL` under 3VL. Pinned by `EqualNullSafe_IsDistinctFromEqualTo`.

The API does not *evaluate* any of this; it guarantees the correct **node kind** is recorded so the
downstream 3VL contract is unambiguous.

## AC4 — the API does not coerce types; the analyzer reports misuse

This is the load-bearing correctness contract of the story. The `Column` operator API is a **dumb
builder**: it never inspects operand `DataType`s, never inserts a `Cast`, and never rejects a type
mismatch. Consequently:

- A boolean expression used in an arithmetic context (e.g. `col.IsNull().Plus(1)`) **builds
  successfully** — it records `Add(IsNull(col), Literal(1))` with an unknown result type. The API does
  **not** throw and does **not** silently coerce the boolean to a number. Reporting this misuse is the
  analyzer's responsibility (FEAT-04.5) under ADR-0008. Pinned by
  `ArithmeticOverBooleanPredicate_BuildsNodeWithoutThrowing`.
- Arithmetic across differing numeric types (e.g. `int` column `Plus(3L)`) records the raw operands
  with **no cast inserted** — Spark's numeric promotion is deferred to the analyzer. Pinned by
  `Arithmetic_DoesNotInsertCastsOrCoercions`.

Adding type checks in the `Column` API would **mask** analyzer errors (produce a different, earlier, or
wrong-layer diagnostic) and violate the EPIC-04 layer separation (API builds plans; the analyzer
resolves and type-checks them). So the only guard the API applies is the **null-reference** guard on
`Column` operands — a CLR programming error, not a SQL type error.

## Lazy guarantee

Building any operator chain performs no work. Each operator only calls a node constructor over the
existing operands' `Expr` and wraps the result in a new `Column`; the operands' wrapped IR is untouched
(immutability / structural sharing from #167/#168). This is proven two ways:

- **Non-vacuous audit proof** (mirrors #169): a deep chain across every operator family, built inside an
  `ExecutionAudit.BeginScope`, leaves `RecordingAudit.ObservedNoExecution == true` (no file opened, no
  row read, no stage entered) — `OperatorChains_AreLazy_ObserveNoExecution`.
- **No schema lookup**: a chain over unknown column names stays `Resolved == false` and renders its
  `SimpleString` without ever consulting a catalog — `Operators_PerformNoSchemaLookup`.

Immutability is separately pinned: `Operators_ReturnNewColumns_OperandsUnchanged` asserts operators
return **new** `Column` instances whose node reuses the operands' exact `Expr` references, and that the
operands' `Expr` is unchanged.

## Public-API surface & governance

All new members are appended (append-only, grouped) to `src/DeltaSharp.Core/PublicAPI.Unshipped.txt`
(RS0016/RS0017 enforced as errors under `-warnaserror`; lanes #160/#166 append to the same file in
parallel). The added surface is: the 30 named-method overloads (5 arithmetic × 2, 6 comparison × 2,
`And`/`Or` × 2, `Not`/`IsNull`/`IsNotNull`, `EqualNullSafe` × 2) plus the operator overloads
(`+ - * / %` and `< <= > >=` each in `(Column,Column)`, `(Column,object?)`, `(object?,Column)` forms,
and `&`/`|`/`!`). `Column.Expr` and `Column(Expression)` stay `internal` — no public member exposes the
IR. **CA2225** (operator alternate methods) does not fire in this repo's analysis mode, and is satisfied
anyway: every operator has a named method equivalent (`Plus`/`Lt`/`And`/…).

## AC → test mapping

Tests live in `tests/DeltaSharp.Core.Tests/ColumnOperatorTests.cs` (33 tests, run on `net8.0` and
`net10.0`).

| AC | Requirement | Tests |
| --- | --- | --- |
| **AC1** | Arithmetic & comparison record the operator kind + operands with no evaluation | `ArithmeticMethod_BuildsBinaryArithmeticWithOperatorAndOperands` (5 cases), `ComparisonMethod_BuildsBinaryComparisonWithOperatorAndBooleanType` (6 cases), `ArithmeticOperators_BuildSameNodesAsMethods`, `ComparisonOperators_BuildSameNodesAsMethods` |
| **AC2** | Boolean And/Or/Not — SQL 3VL is the recorded/analyzed contract | `And_BuildsAndNode`, `Or_BuildsOrNode`, `Not_BuildsNotNode_OverChild`, `Not_NullabilityFollowsChild`, `BooleanOperators_BuildBooleanNodes` |
| **AC3** | Null checks + null-safe equality are distinct node kinds preserving Spark null behavior | `IsNull_And_IsNotNull_AreDistinctNodeKinds`, `EqualNullSafe_IsDistinctFromEqualTo`, `EqualNullSafe_WithNull_CoercesToNullLiteral` |
| **AC4** | Operator misuse is reported by the analyzer later, not hidden by API coercion (the API does not coerce/validate types) | `ArithmeticOverBooleanPredicate_BuildsNodeWithoutThrowing`, `Arithmetic_DoesNotInsertCastsOrCoercions` |
| Literal coercion | `col ⟨op⟩ scalar` builds `⟨op⟩(colExpr, Lit(scalar))`; order preserved; `Column` passthrough | `Gt_WithScalar_CoercesToLiteral`, `Plus_WithScalar_CoercesToLiteral`, `ScalarOverload_WithColumn_PassesThroughUnchanged`, `ComparisonOperator_WithScalar_KeepsOperandOrder` |
| Null guards | null `Column` operand throws; null scalar is a SQL `NULL` literal | `Method_NullColumnOperand_Throws`, `Operator_NullColumnOperand_Throws`, `ScalarOverload_NullValue_BuildsNullLiteral` |
| Immutability | operators return new `Column`s; operands unchanged | `Operators_ReturnNewColumns_OperandsUnchanged` |
| Lazy | operator chains do no work / no schema lookup | `OperatorChains_AreLazy_ObserveNoExecution`, `Operators_PerformNoSchemaLookup` |
