# Read door & in-memory DataFrame creation (M1)

> **Status:** living document. Created with
> [STORY-04.1.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md#story-0412-read-door-and-dataframe-creation-from-local-inputs)
> (FEAT-04.1, issue #158). Builds on STORY-04.1.1 (#157, the `SparkSession` doors and the
> `DataFrameReader`/`CreateDataFrame` placeholders — [sparksession-lifecycle.md](sparksession-lifecycle.md)),
> the immutable logical IR (#167, [logical-plan-nodes.md](logical-plan-nodes.md)), the analyzer
> (#170, [analyzer-resolution.md](analyzer-resolution.md)), the actions/`Row` contract
> (#173/#177, [actions-and-row.md](actions-and-row.md)), and the physical-planning bridge
> (#174, [physical-planning.md](physical-planning.md), esp. §9). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager execution), [ADR-0002](../../adr/0002-columnar-batch-format.md)
> (columnar batches), [ADR-0008](../../adr/0008-type-system-row-format.md) (type system), and
> [ADR-0014](../../adr/0014-target-framework-aot.md) (multi-targeting / AOT posture). Update it
> whenever the reader surface, `CreateDataFrame`, the `LocalRelation`/`UnresolvedFileRelation`
> nodes, the reader-option policy, or the EPIC-05 execution boundary changes.

## Why this exists

DeltaSharp's central invariant is:

> **Transformations are lazy; actions are eager.** Building a plan does **no** work — no file is
> opened, no row is read, no backend runs. Only an action triggers the engine.

STORY-04.1.2 delivers the first two **data-in doors** on `SparkSession`:

1. **`spark.CreateDataFrame(rows, schema)`** — wrap an in-memory sequence (with an explicit schema)
   in a **scan logical plan**. This is executable end-to-end in M1: an action materializes the rows
   through the engine.
2. **`spark.Read.Parquet(path)`** — the fluent `DataFrameReader` door that records an **unresolved
   Parquet scan** in the plan. The Parquet *reader* itself is EPIC-05 (Delta/Parquet storage); in M1
   the node only *describes* the read. Attempting to execute it fails with a deterministic,
   Spark-parity diagnostic that names EPIC-05 ownership.

Both doors preserve the lazy invariant: constructing the `DataFrame` opens no file and materializes
no row. Only `Collect`/`Count`/`Show` (STORY-04.6.1 / #173) cross into execution.

## Public API surface (Spark parity)

All additions live in the packable, `net8.0;net10.0` `DeltaSharp.Core` assembly and are tracked in
`PublicAPI.Unshipped.txt` (RS0016/RS0017 are build errors under `-warnaserror`).

### `SparkSession`

```csharp
// Existing (STORY-04.1.1) — signature unchanged; now returns a live reader instead of throwing.
public DataFrameReader Read { get; }

// Existing (STORY-04.1.1) untyped door — still deferred (schema inference is out of M1 scope; see
// "Deviations"). Its message now points at the schema overload.
public DataFrame CreateDataFrame(System.Collections.IEnumerable data);

// NEW (this story) — the Spark createDataFrame(rows, schema) door.
public DataFrame CreateDataFrame(IEnumerable<Row> data, DeltaSharp.Types.StructType schema);
```

`CreateDataFrame(data, schema)` mirrors Spark's
`createDataFrame(rows: java.util.List[Row], schema: StructType)`: the **supplied `schema` is
authoritative**, and each `Row`'s values are read **positionally** by ordinal (a `Row`'s own
`Schema` is not consulted, matching Spark, which accepts schema-less `RowFactory` rows). It returns a
session-bound `DataFrame` whose plan is a single `LocalRelation` leaf. **It does not enumerate
`data`** — see "Lazy/eager guarantees".

### `DataFrameReader` (mutable fluent builder)

Spark's `DataFrameReader` is a mutable builder; DeltaSharp mirrors that shape.

```csharp
public sealed class DataFrameReader
{
    // Constructed by SparkSession.Read (constructor stays internal).
    public DataFrameReader Schema(DeltaSharp.Types.StructType schema);   // user-specified read schema
    public DataFrameReader Option(string key, string value);            // Spark option(String, String)
    public DataFrameReader Option(string key, bool value);              // Spark option(String, Boolean)
    public DataFrameReader Option(string key, long value);              // Spark option(String, Long)
    public DataFrameReader Option(string key, double value);            // Spark option(String, Double)
    public DataFrame Parquet(string path);                             // Spark read.parquet(path)
}
```

- Option keys are **case-insensitive** (Spark parity: options are stored under a case-insensitive
  map). `Option` overloads coerce their value to a string with the invariant culture
  (`bool` → `"true"`/`"false"`, `long`/`double` round-trippable), exactly as Spark serialises them.
- `Schema` records a user schema that EPIC-05's reader will honour (avoiding schema inference).
- `Parquet(path)` is the **finalizer**: it validates the accumulated options (see
  "Reader-option diagnostics"), then returns a session-bound `DataFrame` wrapping an
  `UnresolvedFileRelation`. It opens **no file**.

`Format`/`Load`, the `csv`/`json`/`orc` doors, and the numeric/boolean option *parsing* the future
reader performs are intentionally out of scope for this story (see "Deviations").

## Logical-plan nodes

Two new internal leaves join the M1 node set ([logical-plan-nodes.md](logical-plan-nodes.md)). Both
are immutable, do zero work at construction, and render as visible **scan nodes** (AC4).

### `LocalRelation` (Spark `LocalRelation`)

A leaf that carries an **explicit `StructType` schema** and the **in-memory rows** an action
materializes:

```
LocalRelation(StructType schema, IEnumerable<Row> data)   // unresolved (Output == null, Resolved == false)
local.WithResolvedOutput(IReadOnlyList<AttributeReference> output) -> LocalRelation  // resolved (Output != null)
```

- **Two-phase, mirroring `UnresolvedRelation` → `ResolvedRelation`.** As built by `CreateDataFrame`
  the node is **unresolved** (`Output == null`, `Resolved == false`) because DeltaSharp mints
  `ExprId`s **fresh per analyze pass** ([ExprIdGenerator](../../../src/DeltaSharp.Core/Analysis/ExprIdGenerator.cs));
  a self-assigned `ExprId` would collide with the analyzer's counter. The analyzer's
  `ResolveRelations` rule mints the output `AttributeReference`s from the shared per-pass id
  generator (identical to `BindRelation` for a catalog table) and calls `WithResolvedOutput` to
  produce the resolved form. The single-argument constructor is the only public one; the resolved
  form is reached exclusively through `WithResolvedOutput` (the underlying three-argument constructor
  is private), which carries the same data reference through unchanged.
- **`Data` is a memoizing snapshot, never enumerated by Core (M1).** The caller's sequence is wrapped
  in a `MemoizedRowSequence` at construction, which enumerates **nothing** (preserving AC1 laziness).
  The **first** enumeration — at the first action's execution, in the Executor — copies the source
  into an immutable `IReadOnlyList<Row>` snapshot; every later enumeration (a second action, a
  multi-scan, a self-join) **replays that same snapshot**. This gives Spark's
  `createDataFrame(List, schema)` stable semantics *without* eager materialization: after the first
  action every action observes identical rows, so `Count` and `Collect` can never diverge, a
  single-use iterator replays correctly, and mutating the source *after* the first action changes
  nothing. (Mutating the source *before* the first action is still observed, since the snapshot is
  deferred to that first action.) `NodeEquals`/`NodeHashCode` compare `Data` by **reference identity**
  (plus schema/output by value): value-comparing the rows would force enumeration and mishandle
  one-shot iterators. `Wrap` is idempotent, so the resolved form shares the unresolved form's snapshot
  and reference identity holds. Two `CreateDataFrame` calls with distinct sequences are distinct plans
  (safe for plan caching / structural sharing #167).
- Render (actual `TreeString()` output — no row count, which would violate laziness):
  - unresolved: `'LocalRelation [id: bigint, name: string, age: int]` (each field as `name: type`)
  - resolved: `LocalRelation [id#0, name#1, age#2]` (each output attribute as `name#exprId`)

  The leading `'` marks the node unresolved
  ([TreeNode](../../../src/DeltaSharp.Core/Plans/TreeNode.cs)).

### `UnresolvedFileRelation` (Spark's path-based `UnresolvedRelation`)

A leaf recording a **file-format read** before EPIC-05 resolves it:

```
UnresolvedFileRelation(string format, string path,
                       IReadOnlyDictionary<string,string> options, StructType? userSchema)
```

- Always **`Resolved == false`** — it holds no reader, no file handle, no schema binding; it is a
  pure descriptor (AC2).
- Render: `'UnresolvedRelation parquet [<redacted path>], options=[k1, k2, …]` (a visible scan node —
  AC4). To avoid leaking credentials the moment a node is stringified (Explain #179, logging), the
  render shows option **keys only** (never values) and a **redacted path** — userinfo passwords and
  credential-bearing query-string values (SAS `?sig=`, presigned-URL signatures, `token`/`secret`/…)
  are masked as `<redacted>` by `SecretRedaction.RedactPath`. The same redaction is applied to the
  EPIC-05 analysis diagnostic.
- Its `Options`/`UserSchema` are what EPIC-05's reader will consume; recording them on the node (not
  acting on them) is exactly Spark's lazy `DataFrameReader` behaviour.

## In-memory source → engine wiring (`CreateDataFrame` execution path)

Core cannot build `ColumnBatch`es (ADR-0002 columnar types live in the `net10.0`-only
`DeltaSharp.Engine`; Core is packable `net8.0;net10.0` and references only `DeltaSharp.Abstractions`
— ADR-0014). So the in-memory rows travel to the engine **inside the `LocalRelation` node**, and the
Executor converts them to batches during physical planning. This reuses the existing scan seam shape
described in [physical-planning.md §9](physical-planning.md):

1. `CreateDataFrame(data, schema)` → `new DataFrame(session, new LocalRelation(schema, data))` (the
   constructor wraps `data` in a `MemoizedRowSequence`; nothing is enumerated).
2. An action calls `DataFrame.AnalyzeForExecution` → `Analyzer.Resolve`. The `ResolveRelations` rule
   turns the unresolved `LocalRelation` into its resolved form (minting output attributes from the
   shared id generator). `DeriveOutput` returns `local.Output`.
3. `IQueryExecutor.Collect/Count` (the `LocalQueryExecutor`, #174) runs the `PhysicalPlanner`. A
   `case LocalRelation` lowers it to a `ScanPlan` carrying a **lazy thunk** — `PhysicalPlanner.Plan`
   performs **no** enumeration or encoding (so #179 `Explain`, which also runs `Plan`, never touches
   the user source or does IO). The thunk (`() => LocalRelationBatches.Build(schema, data)`) runs on
   the **first `ScanPlan.Execute`**, iterating the memoized snapshot **once** and encoding each column
   into an engine `MutableColumnVector` (the exact inverse of
   [`RowMaterializer.ReadValue`](../../../src/DeltaSharp.Executor/Physical/RowMaterializer.cs)),
   producing the same physical leaf a catalog scan produces.
4. `RowMaterializer` turns the executed batches back into `Row`s, so
   `CreateDataFrame([...]).Collect()` **round-trips** the input values.

Because the resolved `LocalRelation` exposes its own `Output`, the Executor's `LogicalOutput`
attribute-id reconstruction (which mirrors the analyzer's numbering — see
[LogicalOutput](../../../src/DeltaSharp.Executor/Physical/LogicalOutput.cs)) is extended to (a) count
`LocalRelation` output attributes in its phase-1 relation-attribute total and (b) return
`local.Output` for the node — identical to how it already treats `ResolvedRelation`. This keeps the
`ExprId`s the analyzer minted and the ids the bridge reconstructs in lock-step.

The `LocalRelation` path does **not** use `InMemoryScanSource`: the data is inline in the node, so no
identifier-keyed registration is needed. `InMemoryScanSource` remains the fixture seam for
catalog-style relations (#174 tests).

### Enabling execution from a Core-only program (the Executor bootstrap)

`CreateDataFrame(data, schema).Collect()` reaches an action through **only `DeltaSharp.Core` public
types**. The execution backend registers itself via a `[ModuleInitializer]` in `DeltaSharp.Executor`,
but that fires only the first time **an Executor type is used** — a pure-Core program never touches
one, so the action would otherwise fail with the deterministic *"No execution backend is registered"*
`QueryExecutionException`. To make the read door usable end-to-end, `DeltaSharp.Executor` exposes a
public, idempotent bootstrap:

```csharp
using DeltaSharp.Executor;

DeltaSharpExecutor.Enable();   // once at startup — wires the backend onto SparkSession
long n = spark.CreateDataFrame(rows, schema).Count();   // now runs
```

`DeltaSharpExecutor.Enable()` calls the same registration the module initializer does and is safe to
call any number of times, from any thread. `DeltaSharp.Executor` is a **non-packable, engine-internal**
assembly (its `public` types carry no governed `PublicAPI.*.txt` baseline — ADR-0014), so this entry
point is documented here rather than tracked as shipped public API. The `UnsupportedQueryExecutor`
"no backend registered" diagnostic names this bootstrap so the failure is self-explaining. The
read-door end-to-end test collection calls `Enable()` in a collection fixture, so those tests pass
**in isolation** (e.g. `--filter ~ReadDoorEndToEnd`) rather than relying on a sibling test to load an
Executor type first.

### Supported cell types

`LocalRelationBatches` encodes every ADR-0008 atomic type `RowMaterializer` can read, so the two are
symmetric: `Boolean`, `Byte`, `Short`, `Integer`, `Long`, `Float`, `Double`, `String`, `Binary`,
`Date` (`DateOnly` ↔ epoch-day), `Timestamp` (`DateTime` UTC ↔ epoch-micros), and `Decimal`
(`decimal` ↔ unscaled `long`/`Int128` at the declared scale). A `null` cell becomes SQL `NULL`
(`AppendNull`) — including a `null` in a field declared **non-nullable**, since Spark treats schema
nullability as advisory at `createDataFrame` (see "Deviations"). A cell whose CLR type does not match
the field (checked **exactly** — no silent widening), a row shorter/longer than the schema, or a value
outside the type's representable range fails with a deterministic `UnsupportedPlanException` naming the
offending field/ordinal (numeric values formatted with `InvariantCulture`) — mirroring the
row-materialization error style. Complex types (`Array`/`Map`/`Struct`) are deferred with the same
deferral as `Row` deep-equality ([#418](https://github.com/khaines/deltasharp/issues/418)).

## Parquet scan node + deferred-execution boundary (EPIC-05)

AC2 requires `Read.Parquet(path)` to build an **unresolved Parquet scan plan** without opening files,
and the story requires that *executing* it yields a deterministic, Spark-parity diagnostic naming
EPIC-05 ownership (mirroring the `UnsupportedPlanException` pattern).

**Where the boundary fires.** File-source resolution is an *analyzer* concern in Spark (the
`DataSource` is resolved during analysis, before physical planning). DeltaSharp mirrors that: the
analyzer's `ResolveRelations` rule recognises `UnresolvedFileRelation` and throws a deterministic
`AnalysisException` (`AnalysisErrorKind.UnsupportedDataSource`) that names the format, the path, and
EPIC-05 ownership, and points at the working alternative (`CreateDataFrame`). This is the
**analysis-time analog of `UnsupportedPlanException`**: a named, deterministic "no M1 mapping, tracked
by EPIC-05" failure rather than a raw exception. It fires only on an **action** (which analyzes), so:

- Building the `DataFrame` (`Read.Parquet(path)`) opens no file and does not analyze (AC2).
- The first action (`Collect`/`Count`/`Show`) throws the EPIC-05 diagnostic — deterministic,
  file-free, and actionable.

When EPIC-05 lands, `ResolveRelations` will instead resolve `UnresolvedFileRelation` (using
`UserSchema` or inferred schema) into a resolved file relation and the `PhysicalPlanner` will build a
real Parquet scan — this node and its recorded options/schema are the forward-compatible seam.

## Reader-option diagnostics (AC3)

`DataFrameReader` maintains an allow-list of recognised, forward-compatible Spark Parquet read
options (case-insensitive): `mergeSchema`, `recursiveFileLookup`, `pathGlobFilter`, `modifiedBefore`,
`modifiedAfter`, `datetimeRebaseMode`, `int96RebaseMode`. These are **recorded** onto the
`UnresolvedFileRelation` for EPIC-05's reader to honour (Spark also merely records reader options into
the plan). Option keys are matched case-insensitively (Spark parity) and stored under their **canonical
recognised spelling** on the node, so `Option("MERGESCHEMA", …)` is recorded as `mergeSchema` and two
readers differing only in option-key case build identical (equal) plans.

At **finalization** (`Parquet(path)`), any option key **outside** the allow-list is *unsupported*:
`Parquet` throws a deterministic `ArgumentException` (the .NET idiom for Spark's
`IllegalArgumentException`) that **names the offending option and the documented alternative** — the
recognised option set, plus `DataFrameReader.Schema(StructType)` for schema control. Example:

```
Unsupported Parquet reader option 'inferSchema'. DeltaSharp M1 recognizes these Parquet read
options: [datetimeRebaseMode, int96RebaseMode, mergeSchema, modifiedAfter, modifiedBefore,
pathGlobFilter, recursiveFileLookup]. To fix a read schema, call DataFrameReader.Schema(StructType)
instead of an option.
```

Validating at finalize (not at `Option`) matches the AC wording ("when the reader is finalized") and
lets a caller stage options in any order before the terminal `Parquet` call. No file is opened.

## Lazy/eager guarantees

| Call | Work performed | Lazy proof |
| --- | --- | --- |
| `CreateDataFrame(data, schema)` | Wrap `data` in a memoizing `LocalRelation`. **`data` is not enumerated.** No analyze, no scan, no backend. | An `IEnumerable<Row>` whose `GetEnumerator` throws is accepted without throwing; the #169 `ExecutionAudit` records an empty stage path. |
| `Read` / `Option` / `Schema` | Mutate the builder. | Pure in-memory; no audit stage. |
| `Parquet(path)` | Validate options; wrap in `UnresolvedFileRelation`. No file opened. | Audit stage path stays empty; no `FileOpened`. |
| `Collect` / `Count` / `Show` | Analyze → optimize (identity seam, #174) → physical-plan (no enumeration, M3) → execute (first action snapshots + encodes `data`). | The **only** members that record an Analyzer stage / run the engine. |

The `CreateDataFrame` non-enumeration guarantee is the strongest reading of AC1 ("no rows are
materialized by the call"): the rows the user already holds are neither iterated, copied, nor scanned
until an action pulls them through the engine — and even physical planning stays enumeration-free (the
row→batch encoding is deferred into the `ScanPlan` thunk, M3), so #179 `Explain` never touches the
source. This is consistent with the existing transformation laziness proof
([DataFrameLazyTransformationTests](../../../tests/DeltaSharp.Core.Tests/LazyEager/DataFrameLazyTransformationTests.cs))
that asserts an empty `ExecutionAudit` stage path. The first action then takes a **stable snapshot**
(see `LocalRelation` above), so subsequent actions replay identical rows.

## Seam to #179 (`Explain`)

AC4 ("the source appears as a scan node in the logical plan output") is delivered jointly with the
sibling **EXPLAIN** lane (#179); this story does **not** implement `DataFrame.Explain`. The
contract this story owns is that both new leaves render as sane, visibly-labelled scan nodes through
the existing `TreeNode.TreeString()`/`SimpleString` machinery, so #179 can consume them unchanged:

- `LocalRelation` → `'LocalRelation [id: bigint, name: string, …]` (unresolved) /
  `LocalRelation [id#0, name#1, …]` (resolved)
- `UnresolvedFileRelation` → `'UnresolvedRelation parquet [<redacted path>], options=[k1, k2, …]`

Because `PhysicalPlanner.Plan` (which `Explain`'s physical mode runs) does **no** enumeration for a
`LocalRelation` (M3), explaining an in-memory frame never reads the user source. The renders redact
secrets (keys-only options, masked path), so `Explain`/logging cannot leak credentials.

## Test plan

**Core tests (`DeltaSharp.Core.Tests`)** — plan construction, laziness, diagnostics, rendering (no
execution, since Core.Tests does not reference the Executor):

- **AC1 (lazy):** `CreateDataFrame(throwOnEnumerate, schema)` does not throw and records an empty
  audit stage path; the returned plan is a `LocalRelation` over the given schema; the source sequence
  is never enumerated.
- **AC2 (lazy Parquet):** `Read.Parquet(path)` returns a `DataFrame` whose plan is an unresolved
  `UnresolvedFileRelation` recording the path/options/user-schema; no audit stage; `Resolved == false`.
- **AC3 (option diagnostics):** an unsupported option → `ArgumentException` naming the option and the
  alternative at `Parquet(path)`; recognised options are accepted and recorded; determinism (same
  message twice); no file opened.
- **AC4 (render):** each door's plan `TreeString()` contains its scan node; option/path/schema appear.
- **EPIC-05 boundary:** analyzing a `Read.Parquet` plan throws the deterministic
  `UnsupportedDataSource` `AnalysisException` naming EPIC-05 + `CreateDataFrame`.
- **Reader ergonomics:** `Option` overloads coerce values; case-insensitive keys; `Schema` recorded;
  stopped-session doors (`Read`, `CreateDataFrame`) throw `SessionStoppedException`.
- **Analyzer:** a `LocalRelation` resolves; output attributes carry the schema's names/types; a
  `Project`/`Filter` over a `LocalRelation` resolves against its output.

**Executor tests (`DeltaSharp.Executor.Tests`)** — end-to-end materialization through the public API.
The read-door end-to-end collection calls `DeltaSharpExecutor.Enable()` in a collection fixture, so it
does not depend on a sibling test loading an Executor type first (M2):

- **AC1 (eager):** `spark.CreateDataFrame(rows, schema).Collect()` returns rows equal to the input
  (round-trip) across the common types; `.Count()` equals the row count; an empty sequence yields no
  rows / count 0.
- **Transformations over a local relation:** `CreateDataFrame(...).Filter(...).Select(...).Collect()`
  returns the expected rows (the `LocalRelation` scan feeds the operator pipeline).
- **Nulls & types:** null cells round-trip as SQL `NULL`; date/timestamp/decimal round-trip.
- **M1 stable snapshot:** on the same frame `Count()` and `Collect()` agree; a single-use iterator
  replays across two actions; mutating the source *after* the first action does not change results.
- **M2 Core-only bootstrap:** a `CreateDataFrame(...).Count()` reached through only Core public types
  runs (passes under `--filter ~ReadDoorEndToEnd` in isolation).
- **M3 no-work planning:** `PhysicalPlanner.Plan` over a `LocalRelation` (the path `Explain` runs)
  does **not** enumerate the source (a counting sequence stays at zero enumerations).
- **Negative paths (`LocalRelationBatches`):** a null row, a row/schema arity mismatch, a declared-type
  vs Row-value CLR mismatch, and decimal scale/precision violations each throw a deterministic,
  field-named `UnsupportedPlanException` with a pinned message; a `null` in a non-nullable field is
  silently encoded as SQL `NULL` (pinned deviation).
- **EPIC-05 boundary end-to-end:** `spark.Read.Parquet(path).Collect()` throws the deterministic
  EPIC-05 diagnostic without opening a file.

## Deviations from the story

- **`CreateDataFrame(IEnumerable)` (schema-less) stays deferred.** Spark infers a schema from the
  element type via reflection; reflection-based inference is out of the M1 scope (and in tension with
  the ADR-0014 trim/AOT posture). The untyped door keeps throwing `NotSupportedException`, now
  pointing at the `(data, schema)` overload. Schema inference is a follow-up.
- **`Explain` is out of scope** (owned by #179) — this story only guarantees scan-node rendering, as
  the story instructs.
- **No Parquet reader** — the Parquet *node* is delivered; the reader is EPIC-05. Executing a Parquet
  plan is the deterministic EPIC-05 diagnostic, not a partial read.
- **Nullability is advisory at encode time.** A `null` supplied for a field declared non-nullable is
  silently encoded as SQL `NULL` rather than rejected — matching Spark, whose `createDataFrame` treats
  `StructField.nullable` as advisory metadata, not an insertion-time constraint. (Pinned by a test so a
  future nullability-enforcement change is a conscious one.)
- **Exact CLR types (no widening).** `LocalRelationBatches` requires each cell's CLR type to match the
  field's ADR-0008 encoding type exactly (an `int` is not accepted for a `bigint`/`LongType` lane); a
  mismatch fails loudly rather than silently widening, so a schema/value drift surfaces immediately.
- **`Timestamp` truncates toward zero (not floor).** `EncodeTimestamp` divides ticks-from-epoch by
  ticks-per-microsecond, which truncates **toward zero**. For pre-epoch instants carrying
  sub-microsecond ticks this rounds *up* toward the epoch, whereas Spark floors (rounds toward negative
  infinity). Only sub-microsecond precision below the Unix epoch is affected.
- **`DateTimeKind.Local` is shifted to UTC.** A `DateTime` with `Kind == Local` is converted with
  `ToUniversalTime()` before encoding (a `Utc`/`Unspecified` value is treated as the instant as-is), so
  the stored epoch-microsecond instant depends on the machine's time zone for `Local` inputs. Supply
  `Utc` (or `Unspecified`) timestamps for machine-independent results.
