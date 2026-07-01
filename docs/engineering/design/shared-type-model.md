# Shared logical type model (`DeltaSharp.Abstractions`)

> **Status:** living document. Created with **STORY-04.T.S1a** (scaffold the shared
> assembly), implementing [ADR-0016](../../adr/0016-shared-logical-type-model-abstractions.md).
> Grounded in [ADR-0008](../../adr/0008-type-system-row-format.md) (type system + row
> format), [ADR-0014](../../adr/0014-target-framework-aot.md) (TFM / AOT posture),
> [repository-layout.md](repository-layout.md), and [api-governance.md](api-governance.md).
> Update it whenever the shared/type split, the dependency DAG, or the sequencing changes.

## Current status (what S1a delivered)

**STORY-04.T.S1a is scaffold + docs only.** It creates the empty, governance-wired
`src/DeltaSharp.Abstractions` assembly (an internal `AbstractionsAssemblyMarker` so it
compiles; a trivially empty PublicAPI baseline) and this design. **No type-model code has
moved yet** — the ADR-0008 types still live in `src/DeltaSharp.Engine/Types/`. The move is
the separate **atomic** S1b+S2 PR (below). This document describes the end-state the
scaffold exists to enable.

## Problem

The ADR-0008 logical type system — the `DataType` hierarchy, complex types, the
`DataTypes` factory, coercion, and ANSI mode — lives in `src/DeltaSharp.Engine/Types/`,
which targets **`net10.0` only** and is **non-packable**. `DeltaSharp.Core` is the public,
packable API library (logical plan + expression IR, EPIC-04); it multi-targets
**`net8.0;net10.0`**.

Two ADR-0014 rules make Core unable to name that type model today:

- A public `net8.0` library must never depend on a `net10.0`-only assembly — so Core's
  `net8.0` target **cannot** reference `DeltaSharp.Engine` (`net8.0 ⊄ net10.0`).
- A **packable** library must not reference a **non-packable** assembly on any TFM — so
  Core (packable) cannot reference Engine (non-packable) even on `net10.0`.

Core and Engine are also deliberately **independent siblings** (neither references the
other). STORY-04.4.1 (#167) worked around the gap with a type-FREE, by-name expression
base (`Core/Plans/Expressions/Expression.cs` over `TreeNode<Expression>`) and even
re-homed Engine's `StableHash` as an internal `PlanHash` duplicate in Core. STORY-04.4.2
(#168) adds `Literal`/`Cast`/`AttributeReference` and boolean predicates that **must**
carry an ADR-0008 `DataType` — the workaround is exhausted. The logical type model must be
reachable from both the `net8.0` public surface and the `net10.0` engine.

## Shape (B1): a new packable `DeltaSharp.Abstractions`

The logical type model moves into a **new packable assembly** `DeltaSharp.Abstractions`
(`net8.0;net10.0`, `IsPackable=true`), root namespace `DeltaSharp`, types under
`DeltaSharp.Types` (mirroring Spark's `org.apache.spark.sql.types`). Both Core and Engine
reference it.

### Dependency DAG

```
        DeltaSharp.Abstractions   (net8.0;net10.0, packable)
                 ▲          ▲
                 │          │
   DeltaSharp.Core        DeltaSharp.Engine
   (net8.0;net10.0,        (net10.0, non-packable)
    packable)

   Core ⟂ Engine  (sibling independence preserved: neither references the other)
```

- **TFMs match.** Abstractions multi-targets `net8.0;net10.0`. Engine (`net10.0`-only)
  resolves its `net10.0` asset; Core (`net8.0;net10.0`) matches on both. No `net8.0`
  consumer ever pulls a `net10.0`-only dependency.
- **Packable rule satisfied.** Core (packable) → Abstractions (packable). Because
  Abstractions ships transitively inside the `DeltaSharp.Core` package, it **must** set
  `IsPackable=true`; otherwise the published package would carry an unresolvable
  dependency.
- **SDK-only ⇒ no lock file.** Abstractions takes no third-party `PackageReference`, so it
  carries **no `packages.lock.json`** — matching Core and Engine (only test projects lock).

### Why not B2 (host the types in Core, Engine → Core)

Putting the types in Core forces **Engine → Core**, which (a) inverts the documented
sibling independence, (b) drags Core's entire public API and `net8.0` baggage into the
`net10.0` execution/AOT executor image, and (c) couples engine execution to public-API
versioning. Worse layering for no assembly saved. Rejected (ADR-0016).

## Logical / physical split

The shared assembly must be free of Arrow/execution/storage dependencies, so the ADR-0008
`Types/` files are split along a logical/physical seam. The classification below is
grounded in the actual cross-file symbol references inside `src/DeltaSharp.Engine/Types/`.

### Moves to `DeltaSharp.Abstractions` (logical) — S1b+S2

| File / symbol | Notes |
| --- | --- |
| `DataType` (base) | Descriptor base. Its abstract `TryGetPhysicalLayout(out PhysicalLayout)` and `ToJson`/`FromJson` couplings are **removed** first (see straddlers). |
| `AtomicTypes` (`Boolean`/`Byte`/`Short`/`Integer`/`Long`/`Float`/`Double`/`String`/`Binary`/`Date`/`Timestamp`/`Null`) | Singletons; reference `StableHash`. |
| `DecimalType` | Parameterized leaf; references `StableHash`, `SchemaValidationException`. |
| `ArrayType`, `MapType` | Nested descriptors; reference `StableHash`, `SchemaValidationException`. |
| `StructType` (+ `StructField`) | Schema descriptor; references `FieldMetadata`, `StableHash`. |
| `FieldMetadata` | String-valued field metadata; references `StableHash` only. Pure. |
| `DataTypes` | Spark-parity factory over all descriptors. Pure. |
| `TypeCoercion` | Coercion rules; references the decimal *type-rules* half of `DecimalArithmetic` and `TypeCoercionException`. |
| `AnsiMode` | Semantic-lens enum (`Ansi` default / `Legacy`); only a `<see cref>` to `ArithmeticOverflowException`. |
| `Exceptions` (`SchemaValidationException`, `UnsupportedTypeException`, `TypeCoercionException`, `ArithmeticOverflowException`) | Dependency-free exception classes. |
| `DecimalArithmetic` **type-rules** (`ForType`/`Bounded`/`ResultType`/`DecimalOp`/`SystemDefault`/consts) | Logical decimal precision/scale rules; consumed by `TypeCoercion` and Engine. |
| `StableHash` | FNV-1a deterministic hash used by **every** descriptor's `GetHashCode`. Ships with the descriptors but stays **`internal`**. |

### Stays in `DeltaSharp.Engine` (physical / storage)

| File / symbol | Notes |
| --- | --- |
| `PhysicalLayout` | Byte-width struct/enum; the columnar & binary-row consumption seam. A physical concept — kept out of the public type API. |
| `PhysicalLayoutResolver` (**new**) | Engine-side exhaustive switch `Resolve(DataType)` over the sealed hierarchy, replacing the removed abstract `DataType.TryGetPhysicalLayout`. Consolidates layout knowledge in one Engine file. |
| `DecimalValue` (**split out of `DecimalArithmetic`**) | Int128 unscaled-mantissa value math (`Add`/`Multiply`/rescale/`Pow10`). Consumed by ~12 execution files; `DecimalValue.Apply` calls the shared `DecimalArithmetic.ResultType` (Engine→shared is allowed). |
| `TemporalValues` | Epoch µs/day conversion + cast/compare. Execution helper (5 Engine files). |
| `SchemaJson` | Delta-log schema JSON (`Utf8JsonWriter`/`JsonDocument`). Storage format; the only `System.Text.Json` user — keeping it in Engine keeps the shared BannedApi surface clean. The `DataType.ToJson`/`FromJson` convenience is relocated to an Engine `SchemaJson` static. |

### Straddler resolutions

1. **`DataType` ↔ `PhysicalLayout`.** Remove the abstract `TryGetPhysicalLayout`/
   `GetPhysicalLayout` from the shared `DataType`; move width knowledge into the Engine
   `PhysicalLayoutResolver.Resolve(DataType)` exhaustive switch (safe: the hierarchy is
   sealed/closed). Keeps the **public** descriptor surface purely logical. Bounded churn:
   ~5 production call sites (`ManagedFixedWidthColumnVector`,
   `ManagedVariableWidthColumnVector`, `RowLayout`, `BatchEvaluationMemory`,
   `InterpretedScan`) + 2 tests switch from `type.GetPhysicalLayout()` to
   `PhysicalLayoutResolver.Resolve(type)`, plus deleting a ~4-line override from the
   descriptors.
2. **`DecimalArithmetic` split.** Type-rules → Abstractions (both `TypeCoercion` and Engine
   consume them); `DecimalValue` → Engine (12 execution consumers).
3. **`DataType.ToJson`/`FromJson` ↔ `SchemaJson`.** `SchemaJson` stays in Engine
   (storage). The convenience becomes an Engine `SchemaJson.ToJson(type)` /
   `SchemaJson.FromJson(string)` static. Zero production consumers today (only 2 Engine
   **test** files call it). If plan-serialization (ADR-0012) later needs schema JSON in
   Core, promote `SchemaJson` to Abstractions then.

## Public surface + governance

The shared logical type model ships **public** with a governed PublicAPI baseline — it is
the Spark-parity type surface EPIC-04 exposes (schema definition, `DataFrame.schema`, cast
targets), and Spark exposes `sql.types` publicly. Shipping `internal` + `InternalsVisibleTo`
would need IVT to Core+Engine+tests plus a later breaking public re-annotation, and would
block the very surface this epic exists to create.

Consequences, all wired in this scaffold (S1a):

- **`DeltaSharpIsProductionAssembly`** — Abstractions lives under `src/`, so it is a
  production assembly: **BannedApiAnalyzers (RS0030)** apply automatically, and
  `InternalsVisibleTo DeltaSharp.Abstractions.Tests` is injected. The surface is BannedApi-
  clean because `SchemaJson` (the only `System.Text.Json` user) stays in Engine.
- **`DeltaSharpTracksPublicApi`** — Abstractions is added to this
  `Directory.Build.props` condition (previously `DeltaSharp.Core` only) so
  **PublicApiAnalyzers (RS0016/RS0017)** run. `PublicAPI.Shipped.txt` (empty) and
  `PublicAPI.Unshipped.txt` (`#nullable enable` header) sit beside the project. Under the
  repo-wide `TreatWarningsAsErrors=true`, these are build gates; the empty baseline is
  valid while the assembly holds only the internal marker, and is populated with the full
  type surface in S1b+S2.
- **Trim/AOT** — `EnableTrimAnalyzer`/`EnableAotAnalyzer`/`EnableSingleFileAnalyzer` are all
  `true` (mirroring Core). The logical model is reflection-free (deterministic FNV
  `StableHash`, no randomized `string.GetHashCode`), so it is trivially trim/AOT-clean —
  which matters because it flows into the Executor NativeAOT image via Engine.
- **Package validation** — `EnablePackageValidation` (CP0001/CP0002) requires the `net8.0`
  and `net10.0` public surfaces to be identical; the type model uses no `net10.0`-only APIs.
- **Lock files** — none for Abstractions (SDK-only). A future `DeltaSharp.Abstractions.Tests`
  (xunit) would carry a `packages.lock.json` per repo policy.

## Sequencing

Sized for ≤3 parallel worktrees; each story is its own PR.

| Story | Scope | Depends on |
| --- | --- | --- |
| **S0** | In-Engine physical decoupling: drop the abstract layout + `ToJson`/`FromJson` from `DataType`, split `DecimalArithmetic` → `+DecimalValue`, add `PhysicalLayoutResolver`, relocate the JSON convenience to `SchemaJson`; update the 5 layout call sites + 2 JSON tests. Isolated to Engine. | — |
| **S1a** *(this story)* | Scaffold the empty `DeltaSharp.Abstractions` (net8;net10, packable, trim/AOT analyzers), empty PublicAPI baseline, sln + `Directory.Build.props` (`DeltaSharpTracksPublicApi += Abstractions`) + ADR-0016 + this doc. | — |
| **S1b+S2** | **Atomic** move + re-point: move ~10 logical files → `Abstractions/` (ns `DeltaSharp.Types`); add `ProjectReference` Engine→Abstractions (+Engine.Tests); re-point the `using` directives; populate `PublicAPI.Unshipped.txt`. | S0 + S1a |
| **S3** | `Core.csproj` `ProjectReference` → Abstractions; (deferred) unify `PlanHash` → `StableHash`. | S1b+S2 |
| **#168** | Rebase typed expressions onto the shared `DataType` (`DeltaSharp.Types.DataType`) — satisfies AC3. | S3 |

**S0 ∥ S1a** run in parallel (S0 = Engine refactor; S1a = empty project + sln/props).
**S1b+S2 is unavoidably atomic and must merge as one PR**: moving the type files breaks
every Engine file that imports them until the namespace is re-pointed and the
`ProjectReference` is added — C# has no whole-namespace alias to bridge a compiling `main`
between "move" and "re-point." **S0 exists precisely to shrink that atomic PR to a
mechanical move.** **S3** and **#168-prep** parallelize after S1b+S2.

## Re-point blast radius (verified)

`grep -rl 'DeltaSharp.Engine.Types'` (excluding the `Types/` directory itself), verified at
this base:

| Area | Files importing `DeltaSharp.Engine.Types` |
| --- | --- |
| `src/DeltaSharp.Engine` (production) | **73** |
| `tests/DeltaSharp.Engine.Tests` | **49** |
| `src/DeltaSharp.Executor` / `.Tests` | **0** |
| `src/DeltaSharp.Core` | **0** real (one doc-comment mention in `PlanHash.cs`) |

So S1b+S2 re-points **73 production + 49 test** files from `using DeltaSharp.Engine.Types;`
to `using DeltaSharp.Types;` — 100% via `using` directives (no fully-qualified references),
making the change a mechanical namespace rename.

## References

- [ADR-0016: Shared logical type-model assembly](../../adr/0016-shared-logical-type-model-abstractions.md)
- [ADR-0008: Type system and internal row/value representation](../../adr/0008-type-system-row-format.md)
- [ADR-0014: Target framework and AOT posture](../../adr/0014-target-framework-aot.md)
- [ADR-0012: Plan serialization](../../adr/0012-plan-serialization.md)
- [Type system & schema model](type-system.md) · [Repository layout](repository-layout.md) ·
  [API governance](api-governance.md)
- Apache Spark `org.apache.spark.sql.types` (`DataType`, `StructType`, `DataTypes`).
