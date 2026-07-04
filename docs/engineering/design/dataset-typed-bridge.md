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
| `DataFrame.As<T>()` | `Dataset.as[T]` | `Dataset<T>` over the **identical** `this.Plan` (`src/DeltaSharp.Core/Session/DataFrame.cs:570`) |
| `Dataset<T>.ToDF()` | `Dataset.toDF()` | `DataFrame` over the **identical** `this.Plan` (`src/DeltaSharp.Core/Session/Dataset.cs:75`) |
| `Dataset<T>.Schema` | `Dataset.schema` | `StructType` derived from `T` (`Dataset.cs:66`) |
| `Dataset<T>.Where(Expression<Func<T,bool>>)` | `filter(FilterFunction<T>)` | `Dataset<T>` wrapping `Filter(lowered, Plan)` (`Dataset.cs:91`) |
| `Dataset<T>.Filter(Expression<Func<T,bool>>)` | `filter(...)` | delegates to `Where` (`Dataset.cs:107`) |
| `Dataset<T>.Where(Column)` | `filter(Column)` | `Dataset<T>` wrapping `Filter(condition, Plan)` (`Dataset.cs:119`) |
| `Dataset<T>.Filter(Column)` | `filter(Column)` | delegates to `Where(Column)` (`Dataset.cs:129`) |
| `Dataset<T>.Select(params Expression<Func<T,object?>>[])` | `select(...)` | `DataFrame` wrapping `Project(lowered, Plan)` (`Dataset.cs:147`) |
| `UnsupportedTypedExpressionException` | (parity: analysis error) | AC4 deterministic diagnostic (`src/DeltaSharp.Core/Session/UnsupportedTypedExpressionException.cs:26`) |

Every typed transformation is a **transformation**: it constructs an immutable logical-plan node over
`this.Plan` and returns a **new** typed/untyped handle. It performs **no** analysis, optimization,
physical planning, or execution; it consults **no** schema on the plan, evaluates **no** predicate,
and makes **no** scan or backend call — the lazy half of the lazy/eager invariant
([ADR-0001](../../adr/0001-execution-strategy.md)).

The internal IR stays hidden: the public members take/return only
`Dataset<T>`/`DataFrame`/`Column`/`StructType`/`System.Linq.Expressions.Expression<...>` and unwrap
`Column.Expr` (`internal`) to build the plan node, so `DeltaSharp.Plans.*` never appears on the public
surface.

**Scope boundary with STORY-04.7.2 (#178).** This story delivers the typed-transformation **bridge**
and **schema derivation from `T`** only. It does **not** implement the `Row`↔`T` **value encoders**
(instantiating an arbitrary `T` from a collected `Row`, or encoding a `T` into row values). That is
the separate deferred [STORY-04.7.2](https://github.com/khaines/deltasharp/issues/178), which
`Depends on: STORY-04.7.1, STORY-04.2.4` (EPIC-04 §STORY-04.7.2). Consequently:

- `Dataset<T>` carries no typed action (`collect(): T[]`); run a typed pipeline by converting back with
  `ToDF()` and using the untyped actions.
- `Dataset<T>.Select(...)` returns a `DataFrame` (an untyped `Dataset<Row>`), not a `Dataset<U>`,
  because reconstructing a typed output would require the output-type value encoder (#178).
- The clean seam #178 completes: `DatasetSchema.Derive<T>()` (the schema) plus the CLR→`DataType`
  mapping, which is already aligned with the executor's value contract (§4).

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
| `p.Prop` (member access on the lambda parameter) | `Functions.Col("Prop")` | `TypedExpressionLowering.cs:72` |
| constant / captured value (subtree not referencing the parameter) | `Functions.Lit(value)` | `TypedExpressionLowering.cs:50` |
| `Convert` / `ConvertChecked` (boxing/lifting) | unwrapped (operand lowered) | `TypedExpressionLowering.cs:57` |
| `Not` | `col.Not()` | `TypedExpressionLowering.cs:59` |
| `Equal` / `NotEqual` | `EqualTo` / `NotEqual` | `TypedExpressionLowering.cs:87` |
| `LessThan` / `LessThanOrEqual` | `Lt` / `Leq` | `TypedExpressionLowering.cs:89` |
| `GreaterThan` / `GreaterThanOrEqual` | `Gt` / `Geq` | `TypedExpressionLowering.cs:91` |
| `AndAlso` / `And` | `And` | `TypedExpressionLowering.cs:93` |
| `OrElse` / `Or` | `Or` | `TypedExpressionLowering.cs:94` |
| `Add` / `Subtract` / `Multiply` / `Divide` / `Modulo` | `Plus` / `Minus` / `Multiply` / `Divide` / `Mod` | `TypedExpressionLowering.cs:95` |

A parameter-independent subtree (a `21` constant, or a captured local like `threshold`) is read to a
value **without compiling** — a `ConstantExpression` is read directly and a captured closure
field/property is read by reflecting the `MemberInfo` off the tree (`EvaluateConstant`,
`TypedExpressionLowering.cs:107`). `ReferencesParameter` (`TypedExpressionLowering.cs:136`) — an
`ExpressionVisitor` that flags the lambda parameter — decides constant-vs-column.

Because the lowered `Column.Expr` matches the untyped one, `Dataset<T>.Where(...)` builds
`new Filter(condition.Expr, Plan)` (`Dataset.cs:95`) — the same node `DataFrame.Filter(Column)` builds
(`DataFrame.cs:124`) — and `Dataset<T>.Select(...)` builds `new Project(lowered, Plan)`
(`Dataset.cs:158`), the same node `DataFrame.Select(Column[])` builds (`DataFrame.cs:77`).
`LogicalPlan` equality is structural (`Filter.NodeEquals` compares `Condition.Equals`,
`src/DeltaSharp.Core/Plans/Logical/Filter.cs:47`), so the tests assert
`Assert.Equal(untyped.Plan, typed.Plan)` — a genuine node-identity gate
(`tests/DeltaSharp.Core.Tests/Typed/DatasetTypedBridgeTests.cs`).

## 4. AC3 — schema derivation from `T` and ADR-0008 nullability

`DatasetSchema.Derive<T>()` (`src/DeltaSharp.Core/Session/DatasetSchema.cs:38`) reflects `T`'s
**public, readable, non-indexer instance properties** into an ordered `StructType`. Properties are
ordered by `MemberInfo.MetadataToken` (`DatasetSchema.cs:45`) — declaration order within the type — so
the derived schema is **deterministic** rather than depending on the unspecified order of
`Type.GetProperties()`. Derivation reads only property **metadata**; it never instantiates `T` or
reads a value, so it materializes nothing.

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
(`DatasetTypedLoweringDiagnosticsTests.TypedChain_OverThrowOnReadSource_NeverReads`) chains
`As<T>().Where(...).Filter(...).Select(...)` over a booby-trapped scan and asserts `ReadCount == 0`.

## 6. AC4 — the deterministic unsupported-expression diagnostic

`UnsupportedTypedExpressionException`
(`src/DeltaSharp.Core/Session/UnsupportedTypedExpressionException.cs:26`) is the single public
diagnostic for both typed-bridge failure modes, raised **eagerly at plan-construction time** (never at
execution):

- **Unsupported property type (schema):** a property whose CLR type has no `DataType` mapping throws
  with the offending `Type.Property` and CLR type named (`DatasetSchema.cs:67`). Test:
  `DatasetSchemaDeriverTests.Derive_UnsupportedPropertyType_ThrowsDeterministicDiagnostic`.
- **Unsupported lambda node:** a node the lowering does not understand — a method call, a nested member
  access (`p.Name.Length`), an unsupported operator — throws with the exact `ExpressionType` /
  member named (`TypedExpressionLowering.cs:75`, `:139`). Tests:
  `DatasetTypedLoweringDiagnosticsTests.Where_MethodCall_*` / `Where_NestedMemberAccess_*` /
  `Select_UnsupportedNode_*`.

Messages name the exact offender so failures are reproducible and greppable rather than surfacing as a
raw reflection or expression-tree error.

## 7. Layering, governance, and parity

- **Core stays `net8.0;net10.0` and does not reference the engine.** The bridge uses only
  `DeltaSharp.Types` (from the packable `DeltaSharp.Abstractions`, already referenced) and
  `System.Linq.Expressions`; it adds no `DeltaSharp.Engine`/`Executor` reference
  ([ADR-0014](../../adr/0014-target-framework-aot.md), repository-layout.md). The CLR→`DataType` mapping is
  duplicated intentionally (not shared code) because the executor's `LocalRelationBatches` lives in the
  net10.0-only executor lane; §4 keeps the two in lockstep by citation.
- **Public API governance.** Every new public member is registered in
  `src/DeltaSharp.Core/PublicAPI.Unshipped.txt` (RS0016/RS0017 gate under `-warnaserror`).
- **AOT/trim.** `T` is annotated `[DynamicallyAccessedMembers(PublicProperties)]` everywhere it flows;
  lowering walks the expression tree as data (no `Expression.Compile`), so the surface is AOT-clean.
- **Spark parity.** `As<T>`/`ToDF`/`Schema` mirror `as[T]`/`toDF()`/`schema`; typed `Where`/`Filter`
  mirror `filter(FilterFunction<T>)`; typed `Select` returning a `DataFrame` mirrors `Dataset.select`.
  The typed value actions (`collect(): T`) are the #178 follow-up.
