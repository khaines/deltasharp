# Dataset&lt;T&gt; typed transformation bridge (M1)

> **Status:** living document. Created with
> [STORY-04.2.4](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md)
> (issue [#163](https://github.com/khaines/deltasharp/issues/163), FEAT-04.2 — the typed
> transformation bridge). Grounded in [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager
> execution) and [ADR-0008](../../adr/0008-type-system-row-format.md) (type system / nullability), and
> builds directly on [dataframe-transformations.md](dataframe-transformations.md) (the untyped
> `Select`/`Filter`/`Where` surface, #160), [column-and-functions.md](column-and-functions.md) (the
> `Column`/`Functions` door, #164), [logical-plan-nodes.md](logical-plan-nodes.md) (the immutable
> `Project`/`Filter` IR and structural sharing, #167), [read-door.md](read-door.md) (the CLR→`DataType`
> value contract), [type-system.md](type-system.md), and [api-governance.md](api-governance.md) /
> [repository-layout.md](repository-layout.md). Update it whenever the typed surface, the lowering
> table, or the schema-derivation rules change.

## 1. What this is (and is not)

This adds DeltaSharp's **typed transformation bridge**: a strongly typed `Dataset<T>` view over the
same immutable logical plan a `DataFrame` wraps, so typed code **shares** the engine pipeline rather
than forking it. It mirrors Apache Spark's `Dataset[T]` / `df.as[T]` / `ds.toDF()`.

New public surface (`src/DeltaSharp.Core/`):

| Public member | Spark parity | Result |
| --- | --- | --- |
| `DataFrame.As<T>()` | `Dataset.as[T]` | `Dataset<T>` over the **identical** `this.Plan` (`src/DeltaSharp.Core/Session/DataFrame.cs`) |
| `Dataset<T>.ToDF()` | `Dataset.toDF()` | `DataFrame` over the **identical** `this.Plan` (`Dataset.cs`) |
| `Dataset<T>.Schema` | `Dataset.schema` | `StructType` derived from `T`, cached per `T` (`Dataset.cs`) |
| `Dataset<T>.Filter(Expression<Func<T,bool>>)` | `filter(FilterFunction<T>)` | **primary**; `Dataset<T>` wrapping `Filter(lowered, Plan)` (`Dataset.cs`) |
| `Dataset<T>.Where(Expression<Func<T,bool>>)` | `where(...)` | delegates to `Filter` (`Dataset.cs`) |
| `Dataset<T>.Filter(Column)` | `filter(Column)` | **primary**; `Dataset<T>` wrapping `Filter(condition, Plan)` (`Dataset.cs`) |
| `Dataset<T>.Where(Column)` | `where(Column)` | delegates to `Filter(Column)` (`Dataset.cs`) |
| `Dataset<T>.Select(params Expression<Func<T,object?>>[])` | `select(...)` | `DataFrame` wrapping `Project(lowered, Plan)` (`Dataset.cs`) |
| `UnsupportedTypedException` | (parity: analysis error) | abstract base of the two AC4 diagnostics (`src/DeltaSharp.Core/Session/UnsupportedTypedException.cs`) |
| `UnsupportedTypedExpressionException` | (parity: analysis error) | AC4 **lambda-lowering** diagnostic (`src/DeltaSharp.Core/Session/UnsupportedTypedExpressionException.cs`) |
| `UnsupportedTypedSchemaException` | (parity: analysis error) | AC4 **schema-mapping** diagnostic (`src/DeltaSharp.Core/Session/UnsupportedTypedSchemaException.cs`) |

Every typed transformation is a **transformation**: it constructs an immutable logical-plan node over
`this.Plan` and returns a **new** typed/untyped handle. It performs **no** analysis, optimization,
physical planning, or execution; it consults **no** schema on the plan, evaluates **no** predicate,
and makes **no** scan or backend call — the lazy half of the lazy/eager invariant
([ADR-0001](../../adr/0001-execution-strategy.md)).

The internal IR stays hidden: the public members take/return only
`Dataset<T>`/`DataFrame`/`Column`/`StructType`/`System.Linq.Expressions.Expression<...>` and unwrap
`Column.Expr` (`internal`) to build the plan node, so `DeltaSharp.Plans.*` never appears on the public
surface.

**Scope boundary with STORY-04.7.2 (#178).** This story (#163) delivers the typed-transformation
**bridge** and **schema derivation from `T`** only; it does not itself decode rows. The `Row`→`T` value
**decoder** and the typed `Collect()` action were subsequently landed by
[STORY-04.7.2](https://github.com/khaines/deltasharp/issues/178) — see
[dataset-encoders.md](dataset-encoders.md). Note therefore:

- The typed **decode**/`collect()` now exists: `Dataset<T>.Collect()` runs the plan through the same
  executor seam `DataFrame.Collect()` uses and decodes each `Row` into a `T` (#178). The reverse
  `T`→`Row` **encode** (materializing rows *from* `T` values) remains deferred.
- `Dataset<T>.Select(...)` returns a `DataFrame` (an untyped `Dataset<Row>`), not a `Dataset<U>`,
  because reconstructing a typed output would require the output-type value encoder (deferred).
- The seam #178 built on: `DatasetSchema.Derive<T>()` (the schema) plus the CLR→`DataType` mapping,
  already aligned with the executor's value contract (§4), reused by the decoder's property↔ordinal
  binding.

## 2. The `Dataset<T>` model

`Dataset<T>` (`src/DeltaSharp.Core/Session/Dataset.cs:39`) is a `sealed` class that wraps exactly what
`DataFrame` wraps — an internal `LogicalPlan Plan` and a `SparkSession? Session` — plus a public
`StructType Schema` derived from `T`. It is **not** user-constructible: the constructor is `internal`
(`Dataset.cs:46`), reached only through `DataFrame.As<T>()`. The type parameter is annotated
`[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]` (`Dataset.cs:39`,
`DataFrame.cs:570`, `DatasetSchema.cs:38`) so the trim/AOT analyzers
(`EnableTrimAnalyzer`/`EnableAotAnalyzer`, `DeltaSharp.Core.csproj`) preserve `T`'s properties for the
reflection deriver and the whole surface stays warning-clean under `-warnaserror` on `net8.0;net10.0`.

`As<T>()` is a **lazy reinterpretation**: it forwards the identical plan reference and session
(`new(Session, Plan)`, `DataFrame.cs:571`); its only eager work is deriving `T`'s schema
(metadata only — see §4). No row is read.

## 3. AC1 — typed transformations lower to the shared logical plan model

A typed `Where`/`Select` lambda is a `System.Linq.Expressions.Expression<...>` — an **expression
tree**, i.e. inspectable data. `TypedExpressionLowering.Lower`
(`src/DeltaSharp.Core/Session/TypedExpressionLowering.cs:32`) **walks and translates** that tree into a
`Column`; it never calls `Expression.Compile()` and never executes the lambda, keeping the bridge
lazy (ADR-0001) and AOT/trim-safe. The produced `Column` wraps the same expression IR
`Functions`/`Column` build, so the resulting plan node is **structurally identical** to the untyped
equivalent.

Lowering table (`TypedExpressionLowering.cs`):

| LINQ `ExpressionType` node | Lowers to | Cite |
| --- | --- | --- |
| `p.Prop` (member access on the lambda parameter) | `Functions.Col("Prop")` | `TypedExpressionLowering.cs` (`LowerMember`) |
| constant / captured value / captured arithmetic subtree (not referencing the parameter) | `Functions.Lit(value)` (folded) | `TypedExpressionLowering.cs` (`EvaluateConstant`) |
| `Convert` / `ConvertChecked` — **value-preserving unwrap** (boxing to `object`, `Nullable<T>` lift/unlift, identity) | unwrapped (operand lowered) | `TypedExpressionLowering.cs` (`LowerConvert`) |
| `Convert` — **numeric promotion or explicit numeric cast** (e.g. `short + short` → `int`, `int op long`, `float op double`, `(double)intCol`, enum `==` → underlying `int`) | **unwrapped** (operand lowered) — the plan carries the bare column and Spark's Catalyst performs type coercion, byte-identical to the untyped `Col`/`Column` API (**no fork**). A C# cast is not independently represented in M1 (a typed-cast facility is future). | `TypedExpressionLowering.cs` (`LowerConvert`) |
| `ConvertChecked` — **value-changing numeric** on a column (e.g. `checked((int)p.LongCol)`) | deterministic throw (`checked`/`unchecked` not honored per-expression on column operands — M1) | `TypedExpressionLowering.cs` (`LowerConvert`) |
| `Not` | `col.Not()` | `TypedExpressionLowering.cs` (`LowerNode`) |
| `Equal` / `NotEqual` (operand a NULL literal) | `col.IsNull()` / `col.IsNotNull()` | `TypedExpressionLowering.cs` (`LowerBinary`) |
| `Equal` / `NotEqual` (otherwise) | `EqualTo` / `NotEqual` | `TypedExpressionLowering.cs` (`LowerBinary`) |
| `LessThan` / `LessThanOrEqual` | `Lt` / `Leq` | `TypedExpressionLowering.cs` (`LowerBinary`) |
| `GreaterThan` / `GreaterThanOrEqual` | `Gt` / `Geq` | `TypedExpressionLowering.cs` (`LowerBinary`) |
| `AndAlso` / `OrElse`, and `And` / `Or` **with boolean operands** | `And` / `Or` | `TypedExpressionLowering.cs` (`LowerBinary`) |
| `And` / `Or` with **non-boolean** operands (bitwise `&`/`\|`) | deterministic throw (not supported in M1) | `TypedExpressionLowering.cs` (`RequireBooleanLogical`) |
| `Add` / `Subtract` / `Multiply` / `Divide` / `Modulo` | `Plus` / `Minus` / `Multiply` / `Divide` / `Mod` | `TypedExpressionLowering.cs` (`LowerBinary`) |
| `AddChecked` / `SubtractChecked` / `MultiplyChecked` on a column (e.g. `checked(p.A + p.B)`) | deterministic throw (`checked`/`unchecked` not honored per-expression on column operands — M1) | `TypedExpressionLowering.cs` (`LowerBinary`) |

**Fidelity where C# and SQL diverge.** Four cases are handled explicitly so a typed predicate is not
silently mis-lowered:

- **`== null` / `!= null`.** SQL three-valued logic makes `col = NULL` / `col <> NULL` UNKNOWN for
  every row (matching nothing), so a C# null comparison lowers to the dedicated `IsNull` / `IsNotNull`
  predicates instead of a comparison against a NULL literal.
- **Bitwise `&` / `|`.** C# emits the *same* `And`/`Or` `ExpressionType` for boolean `&`/`|` and integer
  *bitwise* `&`/`|`; the bridge maps to logical `And`/`Or` only when the binary result is `bool`/`bool?`
  and otherwise throws (`&&`/`||` always emit `AndAlso`/`OrElse`, so no supported functionality is
  lost).
- **C# numeric `Convert` promotions and casts.** A boxing/lifting/identity convert carries no value
  change and is unwrapped. Every value-changing numeric conversion the C# compiler inserts — an
  implicit promotion (`short + short` → both `Convert`ed to `int`; `int op long`; `float op double`;
  enum `==` → `Convert` to the underlying `int`) *and* an explicit cast (`(double)intCol`) — is **also
  unwrapped**: the lowered plan carries the **bare column reference**, byte-identical to the untyped
  `Functions.Col`/`Column` API, and Spark's analyzer (Catalyst) performs the type coercion at analysis
  time. Emitting an explicit `Cast` IR node here would bake C#'s coercion rules into the logical plan,
  pre-empt Catalyst's `TypeCoercion`, and **fork** the typed plan from the untyped one
  (`Plus(Cast(Col,Int), Cast(Col,Int))` vs `Plus(Col, Col)`), so it is deliberately not done. A C# cast
  in a typed lambda is therefore **not** independently represented in M1 (a typed-cast facility is
  future work). *(Note: DeltaSharp's `/` is already fractional and returns `DOUBLE` — matching Spark —
  so a `(double)` cast on an int-division operand is redundant, not load-bearing.)*
- **`checked` / `unchecked` on column operands.** `checked(p.A + p.B)` / `checked((int)p.LongCol)` reach
  the `*Checked` arms with a column operand; the `checked`/`unchecked` keyword has **no faithful
  per-expression Spark-plan mapping** (Spark overflow behavior is session-config governed), so the
  bridge **rejects** it deterministically instead of silently dropping the guard and emitting a plain
  (unchecked) `Plus`/`Cast`. Overflow instead follows the session ANSI mode (see below). A
  *parameter-independent* `checked(...)` subtree still folds to a constant — and still **throws** on
  genuine overflow — in `EvaluateConstant`/`ApplyArithmetic`; only column operands are rejected.

### Expression semantics: Spark SQL, not C#

A typed `Where`/`Select`/`Filter` lambda is **translated** (lowered) to Spark Column IR — it is **not**
executed as C#. The lowered `Column.Expr` is *identical* to what the untyped `Functions`/`Column` API
builds (structural equality; **no fork**), so the plan carries **Spark SQL** execution semantics, exactly
as EF Core / LINQ-to-SQL carry their provider's semantics. **Where C# and Spark SQL operator semantics
differ, Spark SQL semantics apply.** A C# developer must not assume C# runtime behavior. The notable
divergences:

- **Integer `/` is fractional division returning `DOUBLE`.** `p => p.A / p.B` over two `int` columns
  lowers to `ArithmeticOperator.Divide` — the **same** node as `Functions.Col("A") / Functions.Col("B")`
  — which per Spark SQL yields a fractional `DOUBLE` (`5 / 2` → `2.5`), **not** C# integer truncation
  (`2`). This is Spark-faithful and intentional; it is not a defect and is not "fixed". Spark's
  non-decimal `/` result type is `DoubleType`
  (`src/DeltaSharp.Core/Plans/Expressions/ArithmeticResultType.cs:71`). To get integer semantics, cast
  or use an integer-division function once available.
- **C# numeric casts and promotions are unwrapped, not represented.** A cast or implicit promotion
  inside a typed lambda (`(double)p.A`, `short + short`, `int op long`, enum `==`) does **not** add a
  `Cast` node to the plan; it is unwrapped so the plan carries the **bare column reference** and Spark's
  Catalyst performs type coercion — exactly matching the untyped `Column` API (**no fork**). A C# cast
  is therefore not independently representable in a typed lambda in M1; a typed-cast facility is future
  work.
- **`checked` / `unchecked` are not honored on column operands.** They are rejected (see the fidelity
  list above); arithmetic overflow follows the session ANSI mode — **ANSI throws
  `ArithmeticOverflowException`, Legacy yields `NULL`, and it never wraps** — not the C# per-expression
  `checked`/`unchecked` context.

**Typed result-type materialization is a #178 (encoder) concern.** In M1 typed `Select`/`Where`/`Filter`
only **lower to expressions**; they do **not** materialize a typed C# result. So the fact that Spark `/`
produces a `double` (vs. the C# `int` a developer might expect) is a *plan/result-type* observation, not
a typed-output-encoding one: reconstructing a typed `Dataset<U>` (and its CLR result types) is the
deferred [STORY-04.7.2](https://github.com/khaines/deltasharp/issues/178) value-encoder work (§1 scope
boundary). The pin test `DatasetTypedLoweringTests.IntegerDivision_LowersToSamePlanAsUntyped` asserts the
typed and untyped division lower to the **same** IR, documenting the fractional-division mapping as
intentional and proving there is no fork.

A parameter-independent subtree is folded to a value **without compiling** — a `ConstantExpression` is
read directly, a captured closure field/property is read by reflecting the `MemberInfo` off the tree,
and a parameter-independent arithmetic/`Convert` subtree (for example `threshold + 1`) is folded by
recursively evaluating its operands (`EvaluateConstant`, `TypedExpressionLowering.cs`). A
parameter-independent subtree the bridge cannot fold (e.g. a method call) throws an honest
`UnsupportedTypedExpressionException` telling the caller to hoist it into a local.
`ReferencesParameter` — an `ExpressionVisitor` that flags the lambda parameter — decides
constant-vs-column.

Because the lowered `Column.Expr` matches the untyped one, `Dataset<T>.Where(...)` builds
`new Filter(condition.Expr, Plan)` (`Dataset.cs:95`) — the same node `DataFrame.Filter(Column)` builds
(`DataFrame.cs:124`) — and `Dataset<T>.Select(...)` builds `new Project(lowered, Plan)`
(`Dataset.cs:158`), the same node `DataFrame.Select(Column[])` builds (`DataFrame.cs:77`).
`LogicalPlan` equality is structural (`Filter.NodeEquals` compares `Condition.Equals`,
`src/DeltaSharp.Core/Plans/Logical/Filter.cs:47`), so the tests assert
`Assert.Equal(untyped.Plan, typed.Plan)` — a genuine node-identity gate
(`tests/DeltaSharp.Core.Tests/Typed/DatasetTypedBridgeTests.cs`).

## 4. AC3 — schema derivation from `T` and ADR-0008 nullability

`DatasetSchema.Derive<T>()` (`src/DeltaSharp.Core/Session/DatasetSchema.cs`) reflects `T`'s
**public, readable, non-indexer instance properties** — **including inherited public properties**
(matching Spark's JavaBean/product encoders, which project inherited getters) — into an ordered
`StructType`. Properties are ordered **base-class first** (by inheritance depth, most-derived last) and,
within a single declaring type, by `MemberInfo.MetadataToken` (declaration order). Metadata-token order
is only stable *within* one type's metadata scope, so combining it with inheritance depth keeps the
derived schema **deterministic** even when a base type lives in another module or assembly, rather than
depending on the unspecified order of `Type.GetProperties()`. Derivation reads only property
**metadata**; it never instantiates `T` or reads a value, so it materializes nothing. The result is
cached per `T` in `TypedSchemaCache<T>` (a per-closed-generic slot) so a long typed chain derives the
schema once; a `T` with an unmappable property still throws `UnsupportedTypedSchemaException` directly
(the cache defers derivation through a `Lazy<StructType>`, so the exception is never wrapped in a
`TypeInitializationException`).

**Nullability (ADR-0008).** [ADR-0008](../../adr/0008-type-system-row-format.md) and
[type-system.md](type-system.md) place nullability on `StructField.Nullable`, not on the `DataType`.
`ToField` (`DatasetSchema.cs:53`) applies:

| CLR property shape | `Nullable` | Rationale |
| --- | --- | --- |
| non-nullable value type (`int`, `bool`, `DateOnly`, …) | `false` | a value type cannot hold SQL `NULL` |
| `Nullable<U>` (`int?`, `DateOnly?`, …) | `true` (maps `U`) | `Nullable.GetUnderlyingType` unwraps to `U` |
| reference type (`string`, `byte[]`) | `true` | may hold null |

C# nullable-reference-type annotations (`string` vs `string?`) are **intentionally not consulted** in
v1 (`DatasetSchema.cs:57` comment): Spark's product/bean encoders treat every object field as
nullable, and reading annotations would add a trim/AOT-sensitive `NullabilityInfoContext` dependency
for no parity gain. This is a documented, revisitable deviation.

**CLR → `DataType` mapping.** `MapClrType` (`DatasetSchema.cs:81`) mirrors the read-door **value
contract** enforced by the executor's `LocalRelationBatches.Append`
(`src/DeltaSharp.Executor/Physical/LocalRelationBatches.cs:139`) and documented in
[read-door.md](read-door.md) §"In-memory source", so a schema derived here binds to values a `Row` can
carry once #178 lands:

| CLR type | `DataType` | Executor cite |
| --- | --- | --- |
| `bool` | `BooleanType` | `LocalRelationBatches.cs:141` |
| `sbyte` | `ByteType` (Spark signed tinyint) | `LocalRelationBatches.cs:146` |
| `short` | `ShortType` | `LocalRelationBatches.cs:150` |
| `int` | `IntegerType` | `LocalRelationBatches.cs:154` |
| `long` | `LongType` | `LocalRelationBatches.cs:158` |
| `float` | `FloatType` | `LocalRelationBatches.cs:162` |
| `double` | `DoubleType` | `LocalRelationBatches.cs:166` |
| `decimal` | `DecimalType(38,18)` (Spark `SYSTEM_DEFAULT`) | `LocalRelationBatches.cs:186` |
| `string` | `StringType` | `LocalRelationBatches.cs:170` |
| `byte[]` | `BinaryType` | `LocalRelationBatches.cs:174` |
| `DateOnly` | `DateType` | `LocalRelationBatches.cs:178` |
| `DateTime` | `TimestampType` | `LocalRelationBatches.cs:182` |

Widening is intentionally **not** performed (an `int` property maps to `IntegerType`, never
`LongType`), matching the executor's exact-CLR-type value contract (no silent widening,
`LocalRelationBatches.cs:198`). `decimal` maps to Spark's `DecimalType.SYSTEM_DEFAULT` = `decimal(38,18)`
so an unparameterised CLR `decimal` has a well-defined precision/scale.

## 5. AC2 — `ToDF()` / `As<T>()` preserve plan identity without materialization

`As<T>()` forwards the identical plan (`DataFrame.cs:570`); `ToDF()` returns `new DataFrame(Session,
Plan)` over the identical plan (`Dataset.cs:75`). Neither reads data. The round trip therefore
satisfies `ReferenceEquals(df.Plan, df.As<T>().ToDF().Plan)`, asserted with `Assert.Same` in
`DatasetTypedBridgeTests.As_And_ToDF_PreservePlanIdentity`. The schema travels as the `Dataset<T>`'s
`Schema` property (derived from `T`, not from executing the plan), so "schema and plan identity are
preserved without materialization" holds end-to-end. The marquee lazy proof
(`DatasetTypedLoweringDiagnosticsTests.TypedChain_BuildsPlanWithoutReading_WhileAnActionWouldRead`)
builds the full `As<T>().Where(...).Filter(...).Select(...)`/`ToDF()` chain over a `FakeSource` inside
an `ExecutionAudit` scope and asserts **zero** reads while building, then runs an action over the built
plan and observes the source **does** read — so the zero-read assertion is proven non-vacuous (the scan
is genuinely reachable; only an action reaches it).

## 6. AC4 — the deterministic unsupported-expression diagnostics

The typed bridge has **two** deterministic diagnostics, both raised **eagerly at plan-construction
time** (never at execution) and both derived from the shared abstract base
`UnsupportedTypedException` (`src/DeltaSharp.Core/Session/UnsupportedTypedException.cs`) so a caller can
`catch` one precisely or the base to handle both:

- **`UnsupportedTypedSchemaException` — unsupported property type (schema):** a property whose CLR type
  has no `DataType` mapping throws with the offending `Type.Property` and CLR type named
  (`DatasetSchema.cs`). Test:
  `DatasetSchemaDeriverTests.Derive_UnsupportedPropertyType_ThrowsDeterministicDiagnostic` and
  `DatasetTypedLoweringDiagnosticsTests.As_WithUnmappableProperty_ThrowsSchemaExceptionDirectly`.
- **`UnsupportedTypedExpressionException` — unsupported lambda node:** a node the lowering does not
  understand — a method call, a nested member access (`p.Name.Length`), an unsupported operator (a
  bitwise `&`/`|`), or an unfoldable parameter-independent subexpression — throws with the exact
  `ExpressionType` / member named (`TypedExpressionLowering.cs`). Tests:
  `DatasetTypedLoweringDiagnosticsTests.Where_MethodCall_*` / `Where_NestedMemberAccess_*` /
  `Select_UnsupportedNode_*` and `DatasetTypedLoweringTests.BitwiseAnd_*` /
  `UnfoldableParameterIndependentSubtree_*`.

The two are **distinct types** (not a single conflated exception) so a `catch` for a bad lambda does
not also swallow an unmappable POCO property, and vice versa. Messages name the exact offender so
failures are reproducible and greppable rather than surfacing as a raw reflection or expression-tree
error.

## 7. Layering, governance, and parity

- **Core stays `net8.0;net10.0` and does not reference the engine.** The bridge uses only
  `DeltaSharp.Types` (from the packable `DeltaSharp.Abstractions`, already referenced) and
  `System.Linq.Expressions`; it adds no `DeltaSharp.Engine`/`Executor` reference
  ([ADR-0014](../../adr/0014-target-framework-aot.md), repository-layout.md). The CLR→`DataType` mapping is
  duplicated intentionally (not shared code) because the executor's `LocalRelationBatches` lives in the
  net10.0-only executor lane; §4 keeps the two in lockstep by citation.
- **Public API governance.** Every new public member is registered in
  `src/DeltaSharp.Core/PublicAPI.Unshipped.txt` (RS0016/RS0017 gate under `-warnaserror`).
- **AOT/trim.** `T` is annotated `[DynamicallyAccessedMembers(...)]` everywhere it flows (the typed
  transformation surface preserves `PublicProperties`; `Dataset<T>`/`As<T>` widen to
  `PublicProperties | PublicParameterlessConstructor` for the #178 decoder, which constructs `T`).
  Lowering walks the expression tree as data (no `Expression.Compile`), so the transformation surface is
  AOT-clean; the decoder's optional compiled tier is guarded (see [dataset-encoders.md](dataset-encoders.md) §4).
- **Spark parity.** `As<T>`/`ToDF`/`Schema` mirror `as[T]`/`toDF()`/`schema`; typed `Where`/`Filter`
  mirror `filter(FilterFunction<T>)`; typed `Select` returning a `DataFrame` mirrors `Dataset.select`.
  The typed value action `Dataset<T>.Collect(): IReadOnlyList<T>` (mirroring `Dataset.collect()`) landed
  in #178 ([dataset-encoders.md](dataset-encoders.md)).
