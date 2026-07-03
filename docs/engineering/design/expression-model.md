# Logical expression tree model (M1)

> **Status:** living document. Created with
> [STORY-04.4.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md#story-0442-expression-tree-model)
> (issue [#168](https://github.com/khaines/deltasharp/issues/168)). Grounded in
> [EPIC-04](../../planning/epics/EPIC-04-core-api-logical-plan.md) (FEAT-04.4 — the immutable
> logical plan + expression IR), [ADR-0008](../../adr/0008-type-system-row-format.md) (type system
> + row format), [ADR-0016](../../adr/0016-shared-logical-type-model-abstractions.md) (the shared
> logical type model in `DeltaSharp.Abstractions`), and [ADR-0001](../../adr/0001-execution-strategy.md)
> (lazy/eager execution strategy). Honors [logical-plan-nodes.md](logical-plan-nodes.md) (the
> `TreeNode<T>` base and plan invariants it builds on), [shared-type-model.md](shared-type-model.md),
> [repository-layout.md](repository-layout.md), and [api-governance.md](api-governance.md). Update it
> whenever the M1 node set, the resolution markers, the type/nullability hint model, or the inline
> render format changes.

## What this is (and is not)

This is the **logical / Catalyst-style expression tree** — the immutable, unresolved-then-analyzed
expression IR that the public `Column`/functions API (FEAT-04.2/04.3, *later*) builds when a user
writes `col("x") + lit(1)` or `count(col("id"))`. Building an expression does **zero work**: it only
records intent (the lazy half of the lazy/eager invariant, [ADR-0001](../../adr/0001-execution-strategy.md)).
The analyzer (FEAT-04.5) later resolves names and types and rewrites the tree; the optimizer rewrites
it again. Every rewrite returns a **new** tree (structural sharing) — the original is never mutated.

The model lives in `src/DeltaSharp.Core/Plans/Expressions/`, namespace
`DeltaSharp.Plans.Expressions`, and **every type is `internal`**. It sits alongside the M1 logical
`LogicalPlan` node set (see [logical-plan-nodes.md](logical-plan-nodes.md)) and shares the same
immutable `TreeNode<T>` base.

It is deliberately **distinct from** the execution-side expression contract in
`src/DeltaSharp.Engine/Execution/` (`PhysicalExpression` and its `ExpressionEvaluator` kernels). Those
are the **physical, already-resolved** evaluator inputs the EPIC-03 backend runs: their result
`Type`/`Nullable` are already decided and they name kernels. The logical tree defined here is what the
API builds *before* analysis: it carries **unresolved markers** and only **type/nullability hints**,
names no kernel, and has **no execution or columnar coupling**. The analyzer is the bridge that lowers
a resolved logical tree into the physical contract; that lowering is FEAT-04.5 work, out of scope here.

| Concern | Logical tree (this story, #168) | Physical tree (EPIC-03, `Execution/`) |
| --- | --- | --- |
| Built by | public `Column`/functions API (lazy) | analyzer / physical planner |
| Names/types | **unresolved** attributes/functions; type/null **hints** | fully **resolved** `Type` + `Nullable` |
| Coupling | none (no `Columnar`/`Execution` types) | binds kernels, `ColumnBatch`, selection vectors |
| Base | `Expression : TreeNode<Expression>` (#167) | `PhysicalExpression` |

## Dependencies: the `TreeNode<T>` base (#167) and the shared type model (ADR-0016)

Two foundations are **merged** and this model builds directly on them — there is no interim
stand-in and no rebase pending.

1. **The `TreeNode<Expression>` immutable base (#167).** `TreeNode<TNode>` lives in
   `src/DeltaSharp.Core/Plans/TreeNode.cs` and is the Catalyst-style base for *both* the logical
   plan and the expression IR. `Expression` derives from it (`Expression : TreeNode<Expression>`) and
   **inherits all tree machinery** — `Children`, `WithNewChildren`, `MapChildren`, `TransformDown`,
   `TransformUp`, structural equality (`Equals`/`GetHashCode` over node kind + local state + children),
   the deterministic FNV-1a `PlanHash`, the construction-time depth guard, and the multi-line
   `TreeString`/`ToString`. No tree machinery is hand-rolled on `Expression`.
2. **The shared logical type model (ADR-0016).** The type hints use the shared
   `DeltaSharp.Types.DataType` hierarchy, which — after STORY S0/S1/S2 — lives in the packable,
   multi-targeted `DeltaSharp.Abstractions` assembly (see [shared-type-model.md](shared-type-model.md)).
   **This PR folds STORY S3:** `DeltaSharp.Core` gains a `ProjectReference` to
   `DeltaSharp.Abstractions` so the internal IR can name `DataType` for literal/cast/attribute type
   hints. Abstractions is **not** the Engine, so Core's independence from the Engine is preserved: the
   `CoreAssemblyDoesNotReferenceTheEngineAssembly` guard (in
   `tests/DeltaSharp.Core.Tests/Plans/DescriptorOnlyTests.cs`) stays green. Because the whole IR is
   `internal`, the reference does not widen Core's public API surface (`PublicAPI.Unshipped.txt` is
   unchanged).

## The `Expression` base — the `TreeNode<Expression>` contract

`Expression` (`Plans/Expressions/Expression.cs`) is an immutable node in a tree of `Expression`
children. Beyond the inherited `TreeNode<T>` contract, it adds a small **resolution + hint** surface,
all expressed as `virtual` members a node overrides (there is no constructor hint field — matching
Catalyst):

| Member | Meaning |
| --- | --- |
| `bool Resolved` | `true` iff this node and all children are resolved. The base folds over children (all-children-resolved); the three unresolved markers override it to `false`. **Memoized** (safe because nodes are immutable). The analyzer (FEAT-04.5) — never construction — is what makes an expression resolved. |
| `DataType? Type` | the result-type **hint** (the shared `DataType`), or `null` when unknown until analysis. Base default is `null`; nodes override it where the type is analysis-independent. |
| `bool Nullable` | the **nullability hint**. Base default is conservative `true`; nodes override it where nullability is known before analysis. |

`Expression` also provides three `private protected` helpers its nodes reuse: `Unary(child)` and
`Binary(left, right)` build null-checked child lists, and `RequireArity(newChildren, expected,
nodeName)` validates a `WithNewChildren` call supplies exactly the expected number of non-null
children.

### Rendering — `SimpleString` folds children inline (AC4)

Unlike a `LogicalPlan` (whose `SimpleString` excludes child plans, which render as their own tree
lines), an expression's inherited `SimpleString` renders the **whole expression inline** — each node
composes its children's `SimpleString` — matching Catalyst's `Expression.toString`. This inline render
**subsumes** the need for a separate expression debug renderer: aliases, literals, unresolved
attributes, and functions are distinguishable directly. The inherited multi-line `TreeString`
(`ToString`) is used for whole-tree diagnostics.

### Immutability & structural sharing (AC2)

Nodes are immutable: all fields are set at construction and children are exposed as a read-only view.
There are no mutators. Rewrites go exclusively through the inherited
`WithNewChildren`/`MapChildren`/`Transform*`, each of which **allocates a new parent** only for the
changed spine and **shares** untouched subtrees by reference. `TransformUp(rule)` rewriting one leaf
therefore returns a new root whose unchanged sibling subtrees are the *same instances* as in the
original, and the original root is unchanged. This matches Catalyst's `TreeNode.transformUp`/
`transformDown`. Each node's `WithNewChildren` returns `this` when every new child is reference-equal,
so a no-op rewrite is allocation-free.

## The M1 node set

All nodes are `internal sealed`, mirror Apache Spark/Catalyst `expressions` names, and live in
`DeltaSharp.Plans.Expressions`. **Source name, positional argument order, the nullability hint, and
unresolved status are recorded at construction and never lost** (AC1). The table below matches the
code exactly.

### Leaves

| Node (file) | Catalyst analog | Fields | `Resolved` | `Type` hint | `Nullable` hint |
| --- | --- | --- | --- | --- | --- |
| `Literal` (`Literal.cs`) | `Literal` | `DataType Type`, `object? Value`, `bool IsNull` | `true` | its `DataType` | `IsNull` |
| `UnresolvedAttribute` (`UnresolvedAttribute.cs`) | `UnresolvedAttribute` | `IReadOnlyList<string> NameParts` (multipart, ordered), `string Name` | **`false`** | `null` (base) | `true` (base) |
| `AttributeReference` (`AttributeExpressions.cs`) | `AttributeReference` | `string Name`, `DataType Type`, `bool Nullable`, `ExprId ExprId` | `true` | its `DataType` | recorded flag |
| `UnresolvedStar` (`AttributeExpressions.cs`) | `UnresolvedStar` | `IReadOnlyList<string>? Target` (`null` ⇒ bare `*`, else `t.*`) | **`false`** | `null` (base) | `true` (base) |

`Literal` exposes Spark-parity typed factories — `OfBoolean`, `OfByte` (signed `sbyte`), `OfShort`,
`OfInt`, `OfLong`, `OfFloat`, `OfDouble`, `OfString`, `OfBinary` (bytes copied defensively), `OfDate`
(epoch-day `int`), `OfTimestamp` (UTC epoch-micros `long`), `OfDecimal(Int128 unscaled, DecimalType)`,
and `Null(DataType)` — each recording a shared `DataType` and the value in its natural CLR storage
shape. It is always resolved. A value literal's `Nullable` hint is `false`; the typed SQL-`NULL`
literal from `Null(type)` is `Nullable == true` (this is AC3 for literals).

`UnresolvedAttribute` and `UnresolvedStar` are the **unresolved markers** for column references;
`AttributeReference` is their **resolved** form, demonstrating the unresolved→resolved transition the
analyzer performs. `ExprId` (`ExpressionPrimitives.cs`) is an `internal readonly record struct
ExprId(long Value)` — a stable identity assigned by the analyzer (FEAT-04.5); this story constructs it
explicitly and does **not** allocate ids (no `Guid.NewGuid`, no reflection — BannedApiAnalyzers-clean).

### Inner nodes

| Node (file) | Catalyst analog | Children (order) + extra state | `Resolved` | `Type` hint | `Nullable` hint |
| --- | --- | --- | --- | --- | --- |
| `Alias` (`Alias.cs`) | `Alias` | `[Child]` + `string Name` | folds child | child's | child's |
| `Cast` (`Cast.cs`) | `Cast` | `[Child]` + `DataType TargetType` | folds child | **`TargetType`** | child's (pre-analysis hint; analyzer widens for null-introducing casts) |
| `UnresolvedFunction` (`UnresolvedFunction.cs`) | `UnresolvedFunction` | `Arguments` (ordered) + `string Name` + `bool IsDistinct` | **`false`** | `null` (base) | `true` (base) |
| `BinaryArithmetic` (`BinaryOperatorExpressions.cs`) | `Add`/`Subtract`/`Multiply`/`Divide`/`Remainder` | `[Left, Right]` + `ArithmeticOperator Operator` | folds children | `null` until operands are typed, then **derived** from children (Catalyst `Add.dataType`; #171) | `Left ‖ Right` |
| `BinaryComparison` (`BinaryOperatorExpressions.cs`) | `EqualTo`/`LessThan`/… | `[Left, Right]` + `ComparisonOperator Operator` | folds children | `BooleanType` | `Left ‖ Right` |
| `And`, `Or` (`BooleanExpressions.cs`) | `And`, `Or` | `[Left, Right]` | folds children | `BooleanType` | `Left ‖ Right` |
| `Not` (`BooleanExpressions.cs`) | `Not` | `[Child]` | folds child | `BooleanType` | child's |
| `IsNull`, `IsNotNull` (`NullExpressions.cs`) | `IsNull`, `IsNotNull` | `[Child]` | folds child | `BooleanType` | **`false`** (never null) |
| `EqualNullSafe` (`NullExpressions.cs`) | `EqualNullSafe` (`<=>`) | `[Left, Right]` | folds children | `BooleanType` | **`false`** (never null) |
| `SortOrder` (`SortOrder.cs`) | `SortOrder` | `[Child]` + `SortDirection` + `NullOrdering` | folds child | child's | child's |

**Aggregate expressions.** In the unresolved tree an aggregate call (`count(id)`, `sum(x)`) is an
`UnresolvedFunction` carrying `IsDistinct` — exactly Catalyst, where aggregate vs scalar is decided at
*analysis* (FEAT-04.5 function binding). Modelling aggregates as `UnresolvedFunction` here keeps the
logical tree honest about what is known before binding. Its children are its `Arguments`.

**Operators are decoupled from execution.** The enums live in `ExpressionPrimitives.cs`, **in the
logical namespace**: `ArithmeticOperator { Add, Subtract, Multiply, Divide, Remainder }`,
`ComparisonOperator { Equal, NotEqual, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual }`,
`SortDirection { Ascending, Descending }`, and `NullOrdering { NullsFirst, NullsLast }`. Their members
mirror the eventual execution-side operators for **parity only**; the logical tree **does not
reference** `DeltaSharp.Engine` (EPIC-04 layer-separation rule). Boolean `And`/`Or`/`Not` and the null
predicates are distinct node classes (Catalyst parity) rather than an operator enum.

## Resolution markers (unresolved → resolved)

`Resolved` is the single observable marker. A freshly built tree from the API is unresolved wherever
it contains an `UnresolvedAttribute`, `UnresolvedStar`, or `UnresolvedFunction`; `Resolved` is `false`
for the whole tree because the base property folds over children. The analyzer rewrites those nodes
into `AttributeReference` (and bound function/aggregate forms) via `TransformUp`, after which
`Resolved` becomes `true`. Because resolution is a `Transform*` rewrite, it produces a **new** tree and
never mutates the unresolved one (AC2). The unresolved attribute and function markers render with a
leading apostrophe (the star renders as `*`/`t.*`), so an unanalyzed tree is *visibly* unresolved (AC4).

## Inline render (AC4)

The inherited `SimpleString` produces a compact, fully-nested render in which the four required kinds
are **unambiguously distinguishable** (documented, parity-leaning conventions):

| Kind | Render | Example |
| --- | --- | --- |
| **Literal** | the value; strings double-quoted; `null` for SQL NULL; binary as `0x…` | `42`, `"ok"`, `true`, `null` |
| **Unresolved attribute** | a **single leading quote**, no closing quote (Catalyst) | `'salary`, `'t.salary` |
| **Resolved attribute** | `name#exprId` | `salary#7` |
| **Unresolved function** | a leading quote + name + parens; `distinct` shown | `'sqrt('x)`, `'count(distinct 'id)` |
| **Alias** | `child AS name` | `('a + 'b) AS total` |
| `Cast` | `cast(child as <TargetType.SimpleString>)` | `cast('x as bigint)` |
| `BinaryArithmetic`/`BinaryComparison`/`And`/`Or` | `(left ⟨op⟩ right)` | `('a + 'b)`, `('x >= 10)`, `('p AND 'q)` |
| `Not` / `IsNull` / `IsNotNull` / `EqualNullSafe` | `(NOT c)` / `(c IS NULL)` / `(c IS NOT NULL)` / `(l <=> r)` | |
| `UnresolvedStar` | `*` or `t.*` | `*`, `t.*` |
| `SortOrder` | `child ⟨ASC│DESC⟩ ⟨NULLS FIRST│NULLS LAST⟩` | `'x DESC NULLS LAST` |

The leading-single-quote convention marks **everything unresolved** (attributes and functions); a
string **literal** is double-quoted, so it never collides with an unresolved attribute. A **resolved**
attribute carries `#exprId` and no quote, so resolved/unresolved attributes are distinguishable too.
The inherited `TreeString` composes each node's `SimpleString` into the indented multi-line form for
whole-tree diagnostics without this story duplicating tree-walking.

## Acceptance-criteria → test mapping

Tests live in `tests/DeltaSharp.Core.Tests/Plans/Expressions/`, namespace
`DeltaSharp.Core.Tests.Plans`, and reach the `internal` tree via Core.Tests's
`InternalsVisibleTo`. xUnit.

| AC | Requirement | Tests (file → method) |
| --- | --- | --- |
| **AC1** | nodes preserve source name, argument order, nullability hint, unresolved status | `ExpressionPreservationTests`: `UnresolvedAttribute_PreservesMultipartNameAndUnresolvedStatus`, `UnresolvedFunction_PreservesNameArgumentOrderDistinctAndUnresolvedStatus`, `Alias_PreservesOutputNameAndChild`, `UnresolvedStar_PreservesQualifierAndUnresolvedStatus`, `NullabilityHint_LiteralValueIsNonNull_NullLiteralIsNullable`, `NullabilityHint_PropagatesOnAnyNullAcrossOperands`, `NullabilityHint_NullPredicatesAreNeverNull`, `ResolvedStatus_FoldsOverChildren`, `SortOrder_PreservesDirectionAndNullOrdering`. |
| **AC2** | analyzer rule rewriting a child returns a new parent without mutating the original; structural sharing | `ExpressionImmutabilityTests`: `TransformUp_RewritingChild_ReturnsNewParent_OriginalUnchanged`, `TransformUp_SharesUntouchedSubtreesByReference`, `TransformDown_RewritesAndPreservesOriginal`, `WithNewChildren_NoOp_ReturnsSameInstance`, `WithNewChildren_ChangedChild_ReturnsNewInstance`, `WithNewChildren_WrongArity_Throws`, `TransformUp_NoMatch_ReturnsSameRootInstance`, `StructuralEquality_IsValueBasedAndHashStable`. |
| **AC3** | literals and casts represent types via the shared ADR-0008/ADR-0016 model | `ExpressionTypeModelTests`: `Literals_RecordSharedTypeAndStorageShape`, `DecimalLiteral_RecordsDecimalTypeAndUnscaledInt128`, `BinaryLiteral_CopiesBytesDefensively`, `NullLiteral_CarriesTypedNullOfSharedType`, `Cast_TargetTypeIsSharedTypeAndIsTheResultType`, `Comparison_IsAlwaysBooleanTyped`, `Arithmetic_ResultTypeDerivesFromResolvedOperands`, `Arithmetic_TypeIsNullWhileOperandUnresolved`, `UnresolvedMarkers_HaveNoKnownType`, `Literal_StructuralEquality_IsTypeAware`. |
| **AC4** | render distinguishes aliases, unresolved attrs, functions, literals | `ExpressionRenderTests`: `UnresolvedAttribute_RendersWithLeadingQuote`, `ResolvedAttribute_RendersNameWithExprId`, `Literal_RendersValue_StringsDoubleQuoted_NullAsNull`, `UnresolvedFunction_RendersWithLeadingQuoteAndDistinct`, `Alias_RendersChildAsName`, `Cast_RendersTargetTypeSimpleString`, `Operators_RenderInfix`, `SortOrder_RendersDirectionAndNulls`, `Star_RendersBareAndQualified`, `FourRequiredKinds_ArePairwiseDistinguishable`, `TreeString_RendersIndentedMultilineTree_AndToStringMatches`. |

## Conventions & governance

- **Lazy/immutable, no execution coupling.** Construction does zero row work; no `Columnar`/
  `Execution`/kernel references; rewrites return new trees ([ADR-0001](../../adr/0001-execution-strategy.md)
  invariant).
- **Spark/Catalyst parity.** Node and operator names mirror `org.apache.spark.sql.catalyst`.
  Deviations are documented here (aggregates-as-`UnresolvedFunction`; logical operator enums decoupled
  from any execution enums; double-quoted string literals in the inline render).
  - **`!=`/`<>` is a first-class node.** DeltaSharp models `!=`/`<>` as a `BinaryComparison` with
    `ComparisonOperator.NotEqual` (NodeName `NotEqualTo`) rather than Catalyst's `Not(EqualTo(l, r))`
    desugaring — a cleaner, flatter IR that avoids an extra `Not` wrapper (a defensible .NET idiom).
    Consequence: analyzer/optimizer rules that match `Not(EqualTo(...))` must **also** handle the
    standalone `NotEqualTo`. Likewise `EqualNullSafe` (`<=>`) is a standalone node rather than a
    `BinaryComparison` subclass as in Catalyst.
  - **Cast nullability is a pre-analysis hint.** `Cast.Nullable` forwards the child's nullability as a
    hint; the analyzer (FEAT-04.5) widens it for null-introducing (lossy/non-ANSI) casts, e.g.
    `string → int`, which can yield `NULL` from a non-null child.
- **Layering.** The IR lives in `DeltaSharp.Core` and references only `DeltaSharp.Abstractions` for the
  shared type model — never `DeltaSharp.Engine`. The `CoreAssemblyDoesNotReferenceTheEngineAssembly`
  guard enforces this.
- **Style/governance.** Nullable reference types on; the whole IR is `internal` (no
  `PublicAPI.Unshipped.txt` churn); deterministic `PlanHash` (FNV-1a) for hashing; BannedApiAnalyzers-
  clean (no `Guid.NewGuid`, no reflection). XML docs on the types for reviewability.
