# ADR-0016: Shared logical type-model assembly (DeltaSharp.Abstractions)

- **Status:** Accepted
- **Date:** 2026-06-30
- **Deciders:** @khaines
- **Related:** [ADR-0008](0008-type-system-row-format.md) (type system / row format),
  [ADR-0014](0014-target-framework-aot.md) (target framework / AOT posture),
  [ADR-0012](0012-plan-serialization.md) (plan serialization),
  [`repository-layout.md`](../engineering/design/repository-layout.md),
  [`api-governance.md`](../engineering/design/api-governance.md),
  [`shared-type-model.md`](../engineering/design/shared-type-model.md)

## Context

The ADR-0008 type system (`DataType` hierarchy, complex types, the `DataTypes`
factory, coercion, ANSI mode) currently lives in `src/DeltaSharp.Engine/Types/`,
which is `net10.0`-only and non-packable. `DeltaSharp.Core` — the public, packable
API library that owns the logical plan and expression IR (EPIC-04) — multi-targets
`net8.0;net10.0` and therefore **cannot** reference the Engine type model on its
`net8.0` target. Core and Engine are deliberately independent siblings (neither
references the other), and the packable/TFM rules (ADR-0014) forbid Core taking a
compile-time dependency on the non-packable Engine.

STORY-04.4.1 (#167, merged) shipped a type-FREE, by-name expression base
(`Core/Plans/Expressions/Expression.cs` over `TreeNode<Expression>`) to sidestep
this, and even re-homed Engine's `StableHash` as an internal `PlanHash` duplicate in
Core. STORY-04.4.2 (#168) adds `Literal`/`Cast`/`AttributeReference` and boolean
predicates that MUST carry an ADR-0008 `DataType`. There is no longer a way to avoid
the decision: the logical type model must be reachable from both the `net8.0` public
surface and the `net10.0` engine.

## Decision

1. **Create a new packable assembly `DeltaSharp.Abstractions`** (`net8.0;net10.0`,
   `IsPackable=true`), namespace `DeltaSharp.Types`, holding the **logical** type
   model: the `DataType` hierarchy, `StructType`/`StructField`,
   `Array`/`Map`/`Decimal` types, the atomic singletons, the `DataTypes` factory,
   `TypeCoercion` and the decimal *type-rules*
   (`DecimalArithmetic.ForType`/`Bounded`/`ResultType`), `AnsiMode`, the four
   type/coercion exceptions, and the internal `StableHash`. Both `Core` and `Engine`
   add a `ProjectReference` to it. Core and Engine remain mutually independent.
   (Chosen over hosting the model in Core with Engine→Core, which would invert
   sibling independence and pull the public API into the execution/AOT image.)

2. **Logical/physical split.** The shared assembly is free of Arrow/execution/storage
   dependencies. The following stay in `DeltaSharp.Engine`:
   - `PhysicalLayout` (byte-layout struct/enum). The abstract `TryGetPhysicalLayout`
     is **removed** from the shared `DataType`; layout is resolved by a new Engine
     `PhysicalLayoutResolver.Resolve(DataType)` exhaustive switch over the sealed
     hierarchy.
   - `DecimalValue` (Int128 fixed-point value math), split out of `DecimalArithmetic`.
   - `TemporalValues` (epoch conversion) and `SchemaJson` (Delta-log schema JSON). The
     `DataType.ToJson`/`FromJson` convenience is relocated to an Engine `SchemaJson`
     static.

3. **Public surface.** The shared logical type model ships **public** with a
   PublicAPI baseline (it is the Spark-parity type surface users program against).
   `StableHash` stays `internal`. The Engine-only physical helpers are never part of
   the shipped API.

## Consequences

### Positive

- #168 (and all EPIC-04 typed expressions) can reference a single `DataType` from
  both TFMs. Core's `PlanHash` / Engine's `StableHash` duplication can later be
  unified.
- Clean DAG: `Abstractions ← Core`, `Abstractions ← Engine`; siblings stay
  independent.
- The shipped type surface is reflection-free and trim/AOT-clean; the Executor
  NativeAOT gate is unaffected.

### Negative / costs

- One **atomic** PR must move ~10 logical type files and re-point **73 production**
  Engine files (+49 test files) from `using DeltaSharp.Engine.Types;` to
  `using DeltaSharp.Types;` — Engine does not compile between the move and the
  re-point.
- A new governed public API surface + PublicAPI baseline is created; future changes
  to the type model are now API-governed.
- New assembly/test-project wiring (sln, `Directory.Build.props` lanes, optional
  `Abstractions.Tests` with a lock file).

### Follow-ups and sequencing

- Start: **S0** in-Engine physical decoupling (parallel with **S1a**, this
  scaffold); then the **S1b+S2** atomic move + re-point; then **S3** Core wires the
  reference; then **#168** rebases the typed expressions onto the shared `DataType`.
- Deferred: unifying `PlanHash` ↔ `StableHash`; promoting `SchemaJson` into
  Abstractions if plan-serialization (ADR-0012) later needs schema JSON in Core.

## Alternatives considered

- **B2 — host types in Core, Engine→Core.** No new assembly, but inverts sibling
  independence and couples execution/AOT to the public API package. Rejected.
- **Keep `PhysicalLayout` in the shared assembly.** Lower churn, but leaks a physical
  concept into the public type API. Rejected in favor of an Engine resolver.
- **Ship types `internal` + `InternalsVisibleTo`.** Defers the public commitment but
  needs IVT to Core+Engine+tests and a later breaking public re-annotation; blocks
  the EPIC-04 public surface. Rejected.
- **Status quo (type-free by-name expressions).** Cannot satisfy #168 AC3
  (literals/casts use the ADR-0008 model). Rejected.

## References

- [ADR-0008](0008-type-system-row-format.md) (type system and internal row/value
  representation), [ADR-0012](0012-plan-serialization.md) (plan serialization),
  [ADR-0014](0014-target-framework-aot.md) (target framework and AOT posture).
- [`repository-layout.md`](../engineering/design/repository-layout.md) — reserves
  `DeltaSharp.Abstractions` as the Core↔Engine seam.
- [`shared-type-model.md`](../engineering/design/shared-type-model.md) — the detailed
  design for this decision.
- [`api-governance.md`](../engineering/design/api-governance.md).
- #167 (STORY-04.4.1), #168 (STORY-04.4.2).
- Apache Spark `org.apache.spark.sql.types` (the public type surface this mirrors).
