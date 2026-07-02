# Analyzer resolution: local catalog & schema binding (M1)

> **Status:** living document. Created with
> [STORY-04.5.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md#story-0451-local-catalog-and-schema-resolution)
> (issue [#170](https://github.com/khaines/deltasharp/issues/170)), the first analyzer pass of
> FEAT-04.5. Grounded in [EPIC-04](../../planning/epics/EPIC-04-core-api-logical-plan.md),
> [ADR-0008](../../adr/0008-type-system-row-format.md) (the `StructType` schema / type model),
> [ADR-0016](../../adr/0016-shared-logical-type-model-abstractions.md) (the shared type model in
> `DeltaSharp.Abstractions`), and [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager
> execution). Honors [logical-plan-nodes.md](logical-plan-nodes.md) (the `TreeNode<T>` substrate and
> plan invariants), [expression-model.md](expression-model.md) (the expression IR and
> resolved/unresolved markers), and [repository-layout.md](repository-layout.md). Update it whenever
> the catalog seam, the resolver rule set, output derivation, `ExprId` assignment, or the diagnostics
> change.

## 1. What this is (and is not)

This is the **first analyzer pass**: the Catalyst-style bridge that turns an **unresolved**
`LogicalPlan` (a tree the public API builds; see [logical-plan-nodes.md](logical-plan-nodes.md))
into a **resolved** one by binding names to schemas. It does exactly two things:

1. **ResolveRelations** — bind each by-name `UnresolvedRelation` to a concrete ADR-0008 schema via
   a **catalog** lookup, producing a `ResolvedRelation` scan node (AC1).
2. **ResolveReferences** — bind each by-name `UnresolvedAttribute` to a resolved
   `AttributeReference` (stable id, name, type, nullability) against the plan's derived output, and
   expand `UnresolvedStar` (AC2).

It is **not** physical planning, optimization, function/type coercion, or execution. On any
catalog or name-resolution failure it raises a single Spark-compatible `AnalysisException` and stops
— there is no backend to call and none is reached (AC4). All types are `internal` (namespace
`DeltaSharp.Analysis`, under `src/DeltaSharp.Core/Analysis/`), so the analyzer adds **no** public API
surface (no `PublicAPI.Unshipped.txt` change) and references only the Core IR plus the shared
`DeltaSharp.Types` model — never `DeltaSharp.Engine`.

## 2. The catalog seam

```csharp
internal interface ICatalog
{
    bool TryGetRelation(IReadOnlyList<string> identifier, out StructType? schema);
}
```

`ICatalog` is the **only** abstraction between name→schema resolution and where metadata lives.
M1 ships one implementation, `LocalCatalog`, an in-memory registry:

- `Register(string name, StructType schema)` / `Register(IReadOnlyList<string> identifier, StructType schema)`
  register (or replace) a source's schema. It holds **schemas only** — no data, files, or readers —
  so registration and lookup do no I/O.
- Identifiers are joined into a dotted key and matched **case-insensitively**
  (`StringComparer.OrdinalIgnoreCase`), following Spark's default `spark.sql.caseSensitive=false`
  for table names.
- Lookup is **total and side-effect-free**: a miss returns `false` rather than throwing, so the
  analyzer raises exactly one diagnostic at the point of use.

**Metastore seam.** A Hive/Delta/native metastore-backed catalog can replace `LocalCatalog` later
without touching the analyzer, which depends on `ICatalog` alone. Case-sensitive resolution and
multi-level namespaces are deferred behind this seam.

## 3. The resolved-vs-unresolved contract

Resolution is defined by the IR's existing `Resolved` predicate (see
[logical-plan-nodes.md](logical-plan-nodes.md) §resolution and [expression-model.md](expression-model.md)):
a plan is resolved when all children and all directly-held expressions are resolved.

| Before analysis (unresolved) | After analysis (resolved) |
| --- | --- |
| `UnresolvedRelation ["people"]` (leaf, `Resolved=false`) | `ResolvedRelation ["people"], [id#0, name#1, age#2]` (leaf, `Resolved=true`) |
| `UnresolvedAttribute "age"` (`'age`, `Resolved=false`) | `AttributeReference age#2` (`Resolved=true`) |
| `UnresolvedStar *` (`Resolved=false`) | replaced by the child's output attributes |

The analyzer **never mutates** a node. Every rewrite goes through the immutable `TreeNode<T>` /
`LogicalPlan` substrate (`TransformUp`, `MapExpressions`, `Expression.TransformUp`), returning new
trees that share unchanged subtrees by reference. A successful `Resolve` returns a plan whose
`Resolved` is `true` for the M1 node/expression set. (A plan still containing an unresolved
`UnresolvedFunction` — function resolution is a later story — remains unresolved; the analyzer binds
the attribute leaves inside it regardless.)

## 4. The resolver rule set

`Analyzer.Resolve(LogicalPlan)` runs two rule passes in order, each a bottom-up
`plan.TransformUp(...)` over the immutable tree:

### 4.1 ResolveRelations (AC1)

```
plan.TransformUp(node =>
    node is UnresolvedRelation u ? BindRelation(u) : node)
```

`BindRelation` looks the identifier up in the catalog. On a **miss** it throws
`AnalysisException.TableOrViewNotFound` (AC4). On a hit it derives one `AttributeReference` per
schema field — in field order, carrying the field's name, ADR-0008 `DataType`, and `Nullable` flag,
each assigned a fresh `ExprId` — and returns a `ResolvedRelation` carrying the identifier, schema,
that output list, and the read options.

### 4.2 ResolveReferences (AC2)

A single bottom-up `TransformUp` rule that, for each node (whose children are already resolved):

1. **collects the input scope** — the concatenation of the node's resolved children's output
   attributes (empty for a leaf; left ⧺ right for a `Join`);
2. **expands stars** — if the node is a `Project` containing an `UnresolvedStar`, each star is
   replaced (in place, preserving order) by the child's full output attribute list, producing a new
   `Project`;
3. **binds attributes** — `node.MapExpressions(e => e.TransformUp(x => x is UnresolvedAttribute a ? ResolveAttribute(a, input) : x))`
   rewrites every `UnresolvedAttribute` anywhere in the node's directly-held expressions to the
   matching input `AttributeReference`;
4. **memoizes output** — derives and caches the resulting node's output (see §5).

`ResolveAttribute` matches the reference's trailing name part against the input attributes
**case-insensitively**:

- exactly one match → that `AttributeReference` (its id is **reused**, so a column and every
  reference to it share one identity — the key AC2 semantic);
- zero matches → `AnalysisException.UnresolvedColumn` (AC3);
- more than one match → `AnalysisException.AmbiguousReference` (AC3).

Because `TransformUp` is bottom-up, when the rule runs on a node its children are already the final
resolved nodes and their output is already memoized, so the input scope is always available in O(1).

**M1 simplification.** Resolved attributes do not yet carry a qualifier, so a multipart reference
(`t.a`) binds on its trailing column part (`a`); namespace-scoped and qualifier-scoped `t.*`
expansion are deferred behind the catalog seam.

## 5. Output derivation per plan node

Name resolution needs each node's **output** — the attribute list it exposes to its parent. The
analyzer derives it during the ResolveReferences pass and memoizes it in a
`Dictionary<LogicalPlan, IReadOnlyList<AttributeReference>>` keyed by **reference identity**
(`ReferenceEqualityComparer`). Because each node is produced exactly once by the bottom-up pass and
its output is computed immediately, every alias id is allocated exactly once (stability), and a
parent reads its children's output straight from the cache.

| Node | Output |
| --- | --- |
| `ResolvedRelation` | its stored `Output` (one attribute per schema field) |
| `Project` | each project-list element as an attribute (see below) |
| `Aggregate` | each aggregate expression as an attribute |
| `Join` | left output ⧺ right output |
| `Filter`, `Sort`, `Limit`, `Distinct`, `WriteToSource`, `Union` | the (first) child's output, unchanged |

An element becomes an attribute via `ToAttribute`: an `AttributeReference` is itself (id preserved);
an `Alias` becomes a fresh `AttributeReference` named for the alias, typed by the alias's forwarded
type hint, with a newly-allocated `ExprId`. (An alias over an expression whose type is unknown before
type coercion — e.g. bare arithmetic — is out of M1 scope and rejected with a clear error.)

Output is derived **inside the analyzer** rather than added as an abstract member on every existing
`LogicalPlan` node: the intrinsic schema lives on the new `ResolvedRelation` node, and operator
output is a small analyzer-side visitor. This keeps the merged #167/#168 nodes untouched (surgical)
while giving the resolver everything it needs.

## 6. `ExprId` assignment (deterministic, no RNG)

`ExprId`s come from an `ExprIdGenerator` — a monotonic `long` counter **seeded fresh (at 0) per
`Resolve` call**. Ids are allocated in the deterministic order the analyzer walks the tree: scan
columns first (ResolveRelations, field order, left-to-right/bottom-up), then projection/aggregate
aliases (ResolveReferences). Consequences:

- **Deterministic.** Identical input plans resolve to byte-identical output across runs and
  machines — no `Guid.NewGuid`, no `System.Random`, no process-global `AtomicLong`. This keeps the
  analyzer **BannedApiAnalyzers-clean** and makes plan equality/hashing and golden tests
  reproducible.
- **Stable & shared.** A scan column's id is assigned once; every `UnresolvedAttribute` that binds
  to it reuses that id, so identity flows through projections and filters unchanged.

## 7. Diagnostics (Spark-compatible)

One failure type, `AnalysisException`, carries a structured `Kind` (`AnalysisErrorKind`), the failing
`Reference`, and the in-scope `Candidates`, so callers/tests branch on the kind without parsing text.

| Kind | Trigger | Example message |
| --- | --- | --- |
| `TableOrViewNotFound` | catalog miss | `Table or view not found: ghost` |
| `UnresolvedColumn` | no attribute matches | `Cannot resolve column name 'salary' given input columns: [id, name, age]` |
| `AmbiguousReference` | >1 attribute matches | `Reference 'id' is ambiguous, could be: id#0, id#3.` |

Each names the offending reference **and** its candidate columns, matching Spark's analyzer
diagnostics in spirit (AC3).

## 8. The "no execution on failure" guarantee (AC4)

The analyzer is self-contained: it consumes only the Core IR and the shared `DeltaSharp.Types` model
and produces only a rewritten `LogicalPlan`. It performs no I/O, holds no reader/stream/handle, and
has no reference to any physical planner or execution backend (none exists at this layer). A catalog
miss throws from ResolveRelations **before** ResolveReferences runs, and any resolution failure
throws from the analyze pass — so a failure can never trigger physical planning or an execution
backend call. This preserves the lazy/eager invariant: analysis runs at analyze-time (driven by a
future action), never at plan construction.

## 9. AC → test mapping

Tests live in `tests/DeltaSharp.Core.Tests/Analysis/` (`AnalyzerTests`, `LocalCatalogTests`).

| AC | Guarantee | Tests |
| --- | --- | --- |
| **AC1** | unresolved scan → resolved schema with ADR-0008 types | `ResolveRelations_BindsUnresolvedRelation_ToSchemaWithAdr0008Types`, `ResolveRelations_AssignsMonotonicExprIds_StartingAtZero`, `ResolveRelations_IsCaseInsensitive_ForTableNames` |
| **AC2** | valid refs → stable id/name/type/nullability; ids shared | `ResolveReferences_BindsAttributes_WithStableIdsNamesTypesNullability`, `ResolveReferences_ReuseScanExprId_SoColumnAndReferenceShareIdentity`, `ResolveReferences_ResolvesAttributesInsideFilterPredicate`, `ResolveReferences_MatchesColumnNamesCaseInsensitively`, `ResolveReferences_ExpandsStar_ToChildOutput`, `ResolveReferences_ExpandsStar_PreservingSurroundingElements`, `Resolve_IsDeterministic_AcrossRuns` |
| **AC3** | Spark-compatible missing/ambiguous diagnostics naming reference + candidates | `MissingColumn_ThrowsUnresolvedColumn_NamingReferenceAndCandidates`, `AmbiguousColumn_ThrowsAmbiguousReference_ListingBothCandidates` |
| **AC4** | catalog miss → no physical planning/backend call | `CatalogMiss_ThrowsTableOrViewNotFound_NamingIdentifier`, `CatalogMiss_ThrowsBeforeResolvingAnyReferences` |
| catalog seam | register/replace, case-insensitive, multipart, miss | `LocalCatalogTests.*` |

## 10. Files

| File | Role |
| --- | --- |
| `src/DeltaSharp.Core/Analysis/ICatalog.cs` | the catalog seam |
| `src/DeltaSharp.Core/Analysis/LocalCatalog.cs` | M1 in-memory catalog |
| `src/DeltaSharp.Core/Analysis/Analyzer.cs` | ResolveRelations + ResolveReferences + output derivation |
| `src/DeltaSharp.Core/Analysis/ExprIdGenerator.cs` | deterministic monotonic id source |
| `src/DeltaSharp.Core/Analysis/AnalysisException.cs` | Spark-compatible diagnostics |
| `src/DeltaSharp.Core/Plans/Logical/ResolvedRelation.cs` | the resolved scan node (schema + output) |
