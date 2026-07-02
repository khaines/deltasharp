# Analyzer function binding & type coercion (M1)

> **Status:** living document. Created with
> [STORY-04.5.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md#story-0452-function-binding-and-type-coercion)
> (issue [#171](https://github.com/khaines/deltasharp/issues/171)), the second analyzer pass of
> FEAT-04.5. It extends [analyzer-resolution.md](analyzer-resolution.md) (name/schema binding,
> STORY-04.5.1 / #170): once names are bound, this pass **binds functions** and **coerces types**.
> Grounded in [EPIC-04](../../planning/epics/EPIC-04-core-api-logical-plan.md),
> [ADR-0008](../../adr/0008-type-system-row-format.md) (the ADR-0008 type system, ANSI overflow/null
> semantics), [ADR-0016](../../adr/0016-shared-logical-type-model-abstractions.md) (the shared
> `DeltaSharp.Types` model), and [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager
> execution; the interpreter/analyzer is the correctness reference). Honors
> [expression-model.md](expression-model.md) (the expression IR),
> [column-operators.md](column-operators.md), [functions-registry.md](functions-registry.md), and
> [dataframe-transformations.md](dataframe-transformations.md). Update it whenever the function
> registry, the coercion rules, the result-type derivation, the diagnostics, or the CheckAnalysis
> type-validation change.

## 1. What this is (and is not)

This is the analyzer's **binding + type-coercion** sub-pass. It runs **inside**
`ResolveReferences`, immediately after attribute names are bound and before output derivation
(`Analyzer.ResolveReferences`, `src/DeltaSharp.Core/Analysis/Analyzer.cs`, the second
`MapExpressions` at lines 124–125). It does exactly three things:

1. **Function binding (AC1)** — replace each `UnresolvedFunction` with a typed `ResolvedFunction`,
   classified **scalar** or **aggregate**, with a concrete ADR-0008 result type and nullability
   (`FunctionRegistry.Bind`).
2. **Type coercion (AC2)** — coerce the operands of arithmetic, comparison, boolean, and `CaseWhen`
   nodes, and the arguments of functions, to Spark-compatible common types, inserting `Cast` nodes
   for implicit widenings (`ExpressionCoercion`, `FunctionRegistry`, `ArithmeticResultType`).
3. **Type validation (AC3/AC4)** — reject type-invalid operand combinations, non-boolean
   `Filter`/`Join` conditions, misplaced aggregates, and any resolved-but-untyped expression, with a
   precise `AnalysisException`. Operand/argument mismatches are rejected **eagerly** during the
   coercion pass; the plan-scoped rules (condition-is-boolean, aggregate context, the null-typed
   guard) are enforced in the `CheckAnalysis` post-condition walk.

It is **not** physical planning, optimization, constant folding, or execution. Every failure is a
single `AnalysisException` thrown before any backend exists (AC4). All new types are `internal`
(namespace `DeltaSharp.Analysis` / `DeltaSharp.Plans.Expressions`, under `src/DeltaSharp.Core/`), so
this pass adds **no** public API surface (no `PublicAPI.Unshipped.txt` change) and references only
the Core IR plus the shared `DeltaSharp.Types` model — never `DeltaSharp.Engine`.

### 1.1 A note on ANSI overflow/null semantics

This pass is **type-level only**: it decides *result types* and inserts *implicit `Cast` nodes*. The
ANSI overflow / legacy-null runtime behavior of those casts and of arithmetic (ADR-0008;
`AnsiMode` in `DeltaSharp.Abstractions/AnsiMode.cs` — `Ansi` throws
`ArithmeticOverflowException` on overflow, `Legacy` yields SQL `NULL`) is a **runtime** property
carried by the `AnsiMode` value threaded into the interpreter; it is **not** decided here. The
analyzer's job is to produce the correctly-typed plan (widened operands, decimal result types,
inserted casts) that the interpreter then evaluates under the selected mode. This keeps the
analyzer a pure, deterministic type function and the interpreter the ANSI-semantics reference.

## 2. The binding model: `ResolvedFunction` + `FunctionRegistry`

### 2.1 `ResolvedFunction` (the typed contract)

`src/DeltaSharp.Core/Plans/Expressions/ResolvedFunction.cs` — an `internal sealed` expression node
that replaces an `UnresolvedFunction`. It carries:

| Member | Meaning |
| --- | --- |
| `Name` | canonical (lower-case) Spark function name |
| `Kind` | `FunctionKind.Scalar` or `FunctionKind.Aggregate` (`FunctionKind.cs`) |
| `Type` | the concrete ADR-0008 result type (never null) |
| `Nullable` | recorded result nullability |
| `Arguments` | the resolved, **coerced** argument expressions (its `Children`) |
| `IsDistinct` | whether the call carried `DISTINCT` (e.g. `countDistinct`) |

It is **always resolved** (`Expression.Resolved` is true once its children are, and its children are
the resolved arguments). It is produced **only** by the analyzer — the public API always emits an
`UnresolvedFunction` — so downstream stages read a typed, classified call rather than re-deriving it.

### 2.2 `FunctionRegistry.Bind` (the M1 registry)

`src/DeltaSharp.Core/Analysis/FunctionRegistry.cs` — `Bind(UnresolvedFunction) -> ResolvedFunction`.
It lower-cases the name, reads the argument types (each argument is already resolved and typed
because binding runs bottom-up), and dispatches on a closed switch. Unknown name →
`AnalysisException.UnknownFunction`. The **M1 function set** and their contracts:

| Function | Kind | Result type | Nullable | Argument coercion / notes |
| --- | --- | --- | --- | --- |
| `count` | Aggregate | `bigint` | **no** (empty group = 0) | ≥1 arg, any type; honors `IsDistinct` |
| `sum` | Aggregate | integral→`bigint`; `decimal(p,s)`→`decimal(p+10,s)`; float/double→`double` | yes | unary numeric |
| `avg` | Aggregate | `decimal(p,s)`→`decimal(p+4,s+4)`; else `double` | yes | unary numeric |
| `min` / `max` | Aggregate | input type | yes (empty group = NULL) | unary orderable (atomic) |
| `upper` / `lower` / `trim` | Scalar | `string` | follows arg | unary; non-string atomic → `Cast` to string; complex type rejected |
| `length` | Scalar | `int` | follows arg | unary; `string`/`binary` pass through, other atomic → `Cast` to string |
| `concat` | Scalar | `string` | true if any arg is | ≥1 arg; every arg coerced to string |
| `coalesce` | Scalar | `FindWiderCommonType(args)` | true only if **all** args nullable | ≥1 arg; each arg widened to the common type |
| `current_date` | Scalar | `date` | no | nullary |
| `current_timestamp` | Scalar | `timestamp` | no | nullary |
| `year` / `month` / `dayofmonth` | Scalar | `int` | follows arg | unary; `date`/`timestamp` pass through, `string`→`Cast` to date, else rejected |
| `to_date` | Scalar | `date` | **yes** (unparseable string → NULL) | unary; `date` passes through, `string`/`timestamp`→`Cast` to date, else rejected |

The `sum`/`avg` decimal widths (`p+10` / `p+4,s+4`) and the integral→`bigint` accumulation mirror
Spark's `Sum`/`Average` result types; they are computed via `DecimalArithmetic.Bounded`. `min`/`max`
"orderable" is the atomic set (`FunctionRegistry.IsOrderable`).

### 2.3 What is deferred (tracked)

The registry is a **representative** M1 subset, not the full Spark catalog. Deferred and tracked in
[functions-registry.md](functions-registry.md): the `mean` synonym for `avg`, format-string overloads
(`to_date(str, fmt)`), full string→numeric implicit promotion in every context, and the remaining
Spark scalar/aggregate library. Unknown names fail loudly, so a deferred function can never silently
mis-bind.

## 3. Type coercion (ADR-0008)

### 3.1 Where it runs

`ExpressionCoercion.Coerce` (`src/DeltaSharp.Core/Analysis/ExpressionCoercion.cs`) is applied
**bottom-up** via `expression.TransformUp(ExpressionCoercion.Coerce)` inside `ResolveReferences`
(Analyzer.cs:124–125). Because it is bottom-up, a node's operands are already bound, coerced, and
typed when the node itself is visited, so operand types are always known. `Coerce` dispatches:

| Node | Rule |
| --- | --- |
| `UnresolvedFunction` | → `FunctionRegistry.Bind` (§2) |
| `BinaryArithmetic` | operands coerced via `ArithmeticResultType.TryResolve` (§3.2); non-numeric → `DataTypeMismatch` |
| `BinaryComparison` | operands coerced to a common comparable type (§3.3); incomparable → `DataTypeMismatch` |
| `And` / `Or` / `Not` | each operand must be boolean (typed NULL → `Cast` to boolean); else `DataTypeMismatch` |
| `CaseWhen` | each branch condition must be boolean; branch/else values coerced to a common result type (§3.4); else `DataTypeMismatch` |
| anything else | returned unchanged |

Structural sharing: a no-op coercion returns the original node (e.g. `CoerceTo` skips the `Cast`
when the operand already has the target type; `PreserveIfUnchanged` returns the original boolean node
when its rebuilt form is equal), so the transform is idempotent and allocation-light.

### 3.2 Arithmetic result type (`ArithmeticResultType.TryResolve`)

`src/DeltaSharp.Core/Plans/Expressions/ArithmeticResultType.cs` is the **single source of truth**
shared by `BinaryArithmetic.Type` (which derives its result type from its children) and the coercion
pass (which inserts the operand casts). Given the operator and two operand types it returns
`ArithmeticCoercion(LeftTarget, RightTarget, ResultType)`, or `null` when the operation has no
numeric result:

- **Two `NullType` operands** → `null` (no concrete numeric type; rejected upstream).
- **One `NullType` operand** → promotes to the other operand's type (SQL null propagation).
- **Either operand non-numeric** → `null` (the coercion pass turns this into `DataTypeMismatch`).
- **Decimal present, no binary float** → Spark `DecimalPrecision` rules via
  `DecimalArithmetic.ResultType(op, ForType(l), ForType(r))`; both operands widen to the decimal that
  exactly holds their source type.
- **Otherwise (non-decimal numeric)** → both operands widen to `FindWiderTypeForTwo(l, r)`; the
  result is that common type, **except** `Divide` over non-decimal operands yields `double` (both
  targets `double`), matching Spark.

`BinaryArithmetic.Type` (`BinaryOperatorExpressions.cs`) returns `null` until both operands are
resolved and typed, then `ArithmeticResultType.TryResolve(...)?.ResultType`. This is Catalyst parity
(`Add.dataType` is a function of its children): a hand-built arithmetic over two typed literals
already reports its widened type (see `ExpressionTypeModelTests.Arithmetic_ResultTypeDerivesFromResolvedOperands`),
while one with an unresolved operand stays `null`.

### 3.3 Comparison coercion (`ExpressionCoercion.CommonComparableType`)

`BinaryComparison.Type` is **always** `boolean`; only its operands are coerced. The common comparable
type is: equal types → that type; a `NullType` operand → the other operand; otherwise
`FindWiderTypeForTwo`. Cross-family casts Spark allows in some contexts (string↔numeric, etc.) are
**deferred** and documented; an incomparable pair is a `DataTypeMismatch`.

### 3.4 `CaseWhen` common type (`CaseWhen.Type` + `ExpressionCoercion.CoerceCaseWhen`)

`CaseWhen.Type` (`CaseWhen.cs`) derives the common type of the branch values and the else value via
`TypeCoercion.FindWiderCommonType`, returning `null` until every value is typed. The coercion pass
(a) requires every branch **condition** to be boolean (`RequireBoolean`), and (b) widens every branch
**value** and the else value to that common type (`CoerceTo`), rebuilding the flattened
`[c0, v0, c1, v1, …, else?]` child list. No common value type → `DataTypeMismatch` naming the
supplied value types.

### 3.5 Null-type promotion

`NullType` (a typed SQL `NULL`, e.g. `Literal.Null(NullType.Instance)`) participates in coercion as a
first-class type: it promotes to the other operand in arithmetic, comparison, boolean, and `CaseWhen`
contexts (SQL null propagation, ADR-0008), so `int + NULL` derives `int` with a `Cast(NULL as int)`
inserted (see `Resolve_NullArithmetic_PromotesNullToOtherOperand`).

## 4. Diagnostics (AC3)

Every failure is an `AnalysisException` with a structured `Kind`, the failing `Reference`, and a
deterministic message naming the function/operator, the supplied types, and the expected form
(`src/DeltaSharp.Core/Analysis/AnalysisException.cs`). New kinds and their triggers:

| `AnalysisErrorKind` | Trigger | Example message |
| --- | --- | --- |
| `UnresolvedFunction` | unknown function name | `Undefined function: 'no_such_fn'. The function is neither a registered scalar nor an aggregate function in the M1 registry (supplied argument types: [int]).` |
| `InvalidFunctionArgument` | wrong arity or uncoercible argument | `Cannot resolve function 'upper(string, string)': upper requires exactly one argument but got 2.` |
| `DataTypeMismatch` | operator/comparison/boolean/CaseWhen operand type mismatch, or non-boolean condition | `cannot resolve 'Add(b, i)' due to data type mismatch: the 'Add' operator requires numeric operands but got 'boolean' and 'int'.` |
| `MisplacedAggregate` | aggregate outside a valid aggregate context | `Aggregate function 'sum' is not allowed in operator 'Project': aggregate functions are only permitted in the aggregate expressions of a grouped aggregation (groupBy(...).agg(...)).` |
| `UntypedResolvedExpression` | a resolved expression left without a result type | `Resolved expression '…' in operator 'Project' has no result type after type coercion (STORY-04.5.2 / #171); an untyped resolved expression must not reach physical planning.` |

The messages are pure functions of the inputs (no clocks, RNG, or ambient state), so they are
deterministic and assertable (the tests assert `Kind`, `Reference`, and message substrings).

## 5. CheckAnalysis type-validation completeness

`Analyzer.CheckAnalysis` (Analyzer.cs) is the post-condition gate. After it confirms the plan is
**fully resolved** (residual-marker walk unchanged from #170), it enforces the type post-conditions
over the resolved plan, in this order:

1. **`CheckResultTypes` (null-typed-resolved guard).** Walks each expression bottom-up; any node that
   is `Resolved` but whose `Type` is `null` (excluding structural carriers `SortOrder`/`UnresolvedStar`)
   → `UntypedResolvedExpression`. In normal flow this is **defense-in-depth**: the coercion pass
   already types every resolved arithmetic/CaseWhen or throws `DataTypeMismatch` first, so a null
   here means a genuine coercion gap. It is directly exercised by a test-only untyped leaf
   (`CheckAnalysis_ResolvedButUntypedExpression_IsRejected`). The same guard backs
   `Analyzer.ToAttribute`, which now routes a null-typed alias child to `UntypedResolvedExpression`
   (replacing the old "type undetermined until #171" `UnsupportedProjection` message, now that
   coercion delivers the type).
2. **`CheckConditionIsBoolean` (#160).** A `Filter` predicate and an explicit `Join` condition must
   resolve to `BooleanType`; otherwise `DataTypeMismatch` (`the condition of a 'Filter' must be
   boolean but is 'int'`).
3. **`CheckAggregateContext` (#166).** For an `Aggregate`, aggregates are allowed **only** in
   `AggregateExpressions`, not in `GroupingExpressions`. For any **other** operator, an aggregate
   anywhere in its expressions is rejected. Detection is `FindAggregate` (finds the first
   `ResolvedFunction` with `Kind == Aggregate`). Both produce `MisplacedAggregate`. Nested-aggregate
   detection (an aggregate whose argument contains another aggregate) is deferred and tracked.

Because operand-level mismatches throw during the earlier coercion pass, CheckAnalysis's own throws
are the plan-scoped rules plus the defensive guard — together they make "a resolved plan is
well-typed" a **loud** invariant rather than a silent assumption.

## 6. Batch L deferral mapping

This story closes the type-validation findings deferred from Batch L. Each row cites where the
obligation is now discharged:

| Finding | Obligation | Now delivered by |
| --- | --- | --- |
| **#165** | operator operand type-checking (reject bool-in-arithmetic, non-numeric arithmetic) | `ExpressionCoercion.CoerceArithmetic` + `ArithmeticResultType.TryResolve` → `DataTypeMismatch` |
| **#166** (values) | `CaseWhen` branch/else values coerce to a common type | `CaseWhen.Type` + `ExpressionCoercion.CoerceCaseWhen` |
| **#166** (conditions) | `CaseWhen` branch conditions must be boolean | `ExpressionCoercion.RequireBoolean` in `CoerceCaseWhen` |
| **#166** (classification) | aggregate vs scalar classification by canonical name; aggregate-context validation | `FunctionRegistry` (`FunctionKind`) + `CheckAggregateContext` |
| **#160** | `Filter`/`Where`/join conditions must be boolean | `CheckConditionIsBoolean` → `DataTypeMismatch` |
| null-typed-resolved guard | a resolved arithmetic/CaseWhen must get a concrete result Type; a null-typed resolved expr is rejected | node-derived `Type` (concrete after coercion) + `CheckResultTypes` / `ToAttribute` guard |
| alias-over-arithmetic | an alias over arithmetic can derive its output type | node-derived `BinaryArithmetic.Type`, computed before `DeriveOutput` |

**Still deferred (tracked, narrowed):** full Spark cross-family comparison casts (§3.3); the full
Spark function catalog and coercion tables (§2.3); nested-aggregate detection (§5); format-string
function overloads. Each is narrowed to a loud failure (unknown function / incomparable types) rather
than a silent wrong result, so nothing mis-binds while deferred.

## 7. AC → test mapping

Tests live in `tests/DeltaSharp.Core.Tests/Analysis/FunctionBindingCoercionTests.cs` (plus the
updated `AnalyzerTests` and `ExpressionTypeModelTests`).

| AC / finding | Behavior | Test(s) |
| --- | --- | --- |
| AC1 | each M1 function binds to the right kind + result type | `Bind_Count_*`, `Bind_Sum_*`, `Bind_Avg_*`, `Bind_MinMax_*`, `Bind_StringFunctions_*`, `Bind_Length_*`, `Bind_Concat_*`, `Bind_Coalesce_*`, `Bind_CurrentDateAndTimestamp_*`, `Bind_DateParts_*`, `Bind_ToDate_*` |
| AC1 | unknown function → diagnostic | `Bind_UnknownFunction_*`, `AnalyzerTests.CheckAnalysis_UnknownFunction_ThrowsNamingFunction` |
| AC2 | numeric widening (int+long→long) with `Cast` inserted | `Resolve_ArithmeticWidening_IntPlusLong_YieldsLong_WithCastInserted`, `ExpressionTypeModelTests.Arithmetic_ResultTypeDerivesFromResolvedOperands` |
| AC2 | decimal result type; sum/avg decimal widths | `Resolve_DecimalArithmetic_*`, `Bind_Sum_*`, `Bind_Avg_*` |
| AC2 | string/temporal arg coercion; casts inserted | `Bind_Upper_Coerces*`, `Bind_Concat_*`, `Bind_DatePart_ParsesString*`, `Bind_ToDate_*` |
| AC2 | null promotion | `Resolve_NullArithmetic_PromotesNullToOtherOperand` |
| AC3 | wrong arity / unsupported coercion → named diagnostic | `Bind_WrongArity_*`, `Bind_SumOfNonNumeric_*`, `Bind_NullaryWithArguments_*` |
| AC3 / #165 | bool-in-arithmetic rejected | `Resolve_BooleanInArithmetic_ThrowsDataTypeMismatch` |
| AC3 / #160 | non-boolean `Filter` condition rejected; boolean accepted | `Resolve_NonBooleanFilterCondition_*`, `Resolve_BooleanFilterCondition_IsAccepted` |
| AC3 / #166 | `CaseWhen` non-boolean condition + incompatible branches rejected; compatible derive common type | `Resolve_CaseWhen_NonBooleanCondition_*`, `Resolve_CaseWhen_IncompatibleBranchValues_*`, `Resolve_CaseWhen_CompatibleBranches_*` |
| AC4 / #166 | aggregate outside agg context rejected; in agg list accepted | `Resolve_AggregateInProjection_*`, `Resolve_AggregateInGroupingKey_*`, `Resolve_AggregateInAggregateExpressions_*` |
| null-typed guard | resolved-but-untyped rejected by CheckAnalysis | `CheckAnalysis_ResolvedButUntypedExpression_IsRejected` |

**Non-vacuity.** The coercion assertions check inserted `Cast` nodes and derived result types, so
disabling the coercion pass (or the node-derived `Type`) reddens `Resolve_ArithmeticWidening_*`,
`Bind_Upper_Coerces*`, and `Arithmetic_ResultTypeDerivesFromResolvedOperands`; removing a
CheckAnalysis rule reddens the corresponding rejection test.

## 8. Files

| Path | Role |
| --- | --- |
| `src/DeltaSharp.Core/Plans/Expressions/ResolvedFunction.cs` | typed resolved-function node (scalar/aggregate) |
| `src/DeltaSharp.Core/Plans/Expressions/FunctionKind.cs` | scalar vs aggregate classification |
| `src/DeltaSharp.Core/Plans/Expressions/ArithmeticResultType.cs` | shared arithmetic coercion/result-type helper |
| `src/DeltaSharp.Core/Analysis/FunctionRegistry.cs` | M1 function binding + result-type rules + diagnostics |
| `src/DeltaSharp.Core/Analysis/ExpressionCoercion.cs` | bottom-up operand/argument coercion + operand validation |
| `src/DeltaSharp.Core/Analysis/AnalysisException.cs` | new error kinds + factories |
| `src/DeltaSharp.Core/Analysis/Analyzer.cs` | coercion pass wiring + CheckAnalysis type validation |
| `src/DeltaSharp.Core/Plans/Expressions/BinaryOperatorExpressions.cs` | `BinaryArithmetic.Type` derived from children |
| `src/DeltaSharp.Core/Plans/Expressions/CaseWhen.cs` | `CaseWhen.Type` derived from children |
| `tests/DeltaSharp.Core.Tests/Analysis/FunctionBindingCoercionTests.cs` | binding/coercion/validation tests |
