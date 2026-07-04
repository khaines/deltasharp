# Write door & local sink contract (M1)

> **Status:** living document. Created with
> [STORY-04.6.3](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md#story-0463-write-action-trigger-and-local-sink-contract)
> (FEAT-04.6, issue #175). It is the **mirror image** of the read door
> ([read-door.md](read-door.md), #158): where the read door turns a source into rows via an action, the
> write door drains a `DataFrame`'s rows into a sink via an action. Builds on the actions/`Row` contract
> and the `IQueryExecutor` seam (#173, [actions-and-row.md](actions-and-row.md)), the physical-planning
> bridge (#174, [physical-planning.md](physical-planning.md)), the execution boundaries / stage
> attribution (#176, [execution-boundaries.md](execution-boundaries.md)), the immutable logical IR
> (#167, [logical-plan-nodes.md](logical-plan-nodes.md)), and the analyzer (#170,
> [analyzer-resolution.md](analyzer-resolution.md)). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager execution) and
> [ADR-0014](../../adr/0014-target-framework-aot.md) (multi-targeting). Update it whenever the writer
> surface, the `WriteToSource`/`SinkDescriptor` nodes, the write-format taxonomy, the local sink seam,
> or the EPIC-05 write boundary changes.

## Why this exists

DeltaSharp's central invariant is:

> **Transformations are lazy; actions are eager.** Building a plan — or *configuring a writer* — does
> **no** work: no plan is analyzed, no row is read, no sink is opened. Only an action triggers the
> engine.

STORY-04.6.3 delivers the first **data-out door** on `DataFrame`:

1. **`df.Write`** returns a fluent `DataFrameWriter` (Spark's `df.write`). `Format`/`Mode`/`Option`/
   `Options`/`PartitionBy` only update the writer's in-memory intent — each returns the writer and does
   nothing else.
2. **`writer.Save()` / `writer.Save(path)`** is the **eager action**: it builds a **write logical
   intent** (a `WriteToSource` node over the frame's analyzed plan), then analyzes, plans, and executes
   it exactly once, draining the result into the configured sink.

The M1 write door executes **one** engine-backed local sink end-to-end — Spark's `memory` format (AC1).
Recognized-but-deferred formats (`delta`/`parquet`) route to a deterministic **EPIC-05** diagnostic
(AC4), exactly as the read door defers Parquet reads. Any other format is a deterministic
unsupported-format diagnostic (AC3). All three routes are decided during **analysis**, before any output
is committed.

## Public API surface (Spark parity)

All additions live in the packable, `net8.0;net10.0` `DeltaSharp.Core` assembly and are tracked in
`PublicAPI.Unshipped.txt` (RS0016/RS0017 are build errors under `-warnaserror`). The execution seam and
every sink type stay **internal** (Core) / **engine-internal** (Executor).

### `DataFrame`

```csharp
// NEW (this story) — the Spark df.write door. A FRESH writer per access.
public DataFrameWriter Write { get; }
```

`Write` is `src/DeltaSharp.Core/Session/DataFrame.cs:555` — `public DataFrameWriter Write => new(this);`.
Each access returns a **new** mutable builder, so staged config on one writer never leaks into another,
and getting/configuring it does **no** work (no session check, no analysis).

### `SaveMode` (Spark `org.apache.spark.sql.SaveMode`)

```csharp
public enum SaveMode { Append, Overwrite, ErrorIfExists, Ignore }
```

`src/DeltaSharp.Core/Session/SaveMode.cs:10`. A purely **logical** intent recorded on the write plan;
`ErrorIfExists` is Spark's default. The member order (`Append=0`, `Overwrite=1`, `ErrorIfExists=2`,
`Ignore=3`) is fixed in `PublicAPI.Unshipped.txt`.

### `DataFrameWriter` (mutable fluent builder)

Spark's `DataFrameWriter` is a mutable builder; DeltaSharp mirrors that shape
(`src/DeltaSharp.Core/Session/DataFrameWriter.cs`).

```csharp
public sealed class DataFrameWriter
{
    // Constructed by DataFrame.Write (constructor stays internal — DataFrameWriter.cs:46).
    public DataFrameWriter Format(string source);                       // Spark format(source)          :54
    public DataFrameWriter Mode(SaveMode saveMode);                     // Spark mode(SaveMode)           :65
    public DataFrameWriter Mode(string saveMode);                       // Spark mode(String)             :80
    public DataFrameWriter Option(string key, string value);           // Spark option(String, String)   :104
    public DataFrameWriter Option(string key, bool value);             // Spark option(String, Boolean)  :118
    public DataFrameWriter Option(string key, long value);             // Spark option(String, Long)     :127
    public DataFrameWriter Option(string key, double value);           // Spark option(String, Double)   :136
    public DataFrameWriter Options(IReadOnlyDictionary<string,string> options); // Spark options(Map)     :145
    public DataFrameWriter PartitionBy(params string[] colNames);      // Spark partitionBy(colNames)    :161
    public void Save();                                                // Spark save()                    :184
    public void Save(CancellationToken cancellationToken);             //                                 :195
    public void Save(string path);                                     // Spark save(path)                :209
}
```

- **Every configuration method returns `this`** (the same writer), so config is chainable and lazy.
- Option keys are **case-insensitive** (`Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)`,
  `DataFrameWriter.cs:38`), matching Spark's case-insensitive option map. The typed overloads coerce to
  invariant-culture strings (`bool` → `"true"`/`"false"` `:118`; `long` → invariant `:127`; `double` →
  round-trippable `"R"` `:136`) so option values never depend on the host locale.
- **Defaults** (`DataFrameWriter.cs:41-42`): a writer that never calls `Format` uses
  `WriteFormats.Default` (`"parquet"`, Spark's `spark.sql.sources.default`); the mode defaults to
  `SaveMode.ErrorIfExists` (Spark parity).
- **`Save(path)`** sets the path then calls `Save()` (`DataFrameWriter.cs:209-215`) — equivalent to
  staging the path and saving, exactly like Spark's `save(path)`.
- `Save()` snapshots the writer's staged intent into an immutable `SinkDescriptor` via `BuildSink`
  (`DataFrameWriter.cs:219`), defensively copying the options/partition-columns so the built plan stays
  immutable even if the writer is mutated afterwards.

## Logical-plan nodes

### `WriteToSource` (the write intent)

`src/DeltaSharp.Core/Plans/Logical/WriteToSource.cs` — `internal sealed class WriteToSource :
LogicalPlan` with a `Child` (the rows to write) and a `Sink` (`SinkDescriptor`). It holds **no** open
writer, stream, file handle, task, or backend object; constructing it performs **no** write (the
lazy/eager invariant). `Save()` builds `new WriteToSource(Plan, sink)` over the frame's plan
(`DataFrame.cs:878`). It is a shape-preserving root: its output attributes are its child's.

### `SinkDescriptor` (immutable logical sink description)

`src/DeltaSharp.Core/Plans/Logical/SinkDescriptor.cs` — an immutable, purely logical description of a
write target: `Format`, `Mode`, optional `Path`/`TableIdentifier`, `PartitionColumns` (by name), and
`Options`. Its `SimpleString` (`:58`) **redacts** a credential-bearing path via
`SecretRedaction.RedactPath(Path)` (`:68`) so stringifying a write node — `Explain` (#179), a log line,
or a diagnostic — never leaks a SAS token / presigned-URL signature / userinfo secret (the write-side
fix mirroring the read door's #424; discharges #432).

## Write-format taxonomy (AC routing)

`src/DeltaSharp.Core/Plans/Logical/WriteFormats.cs` is the single, deterministic, case-insensitive
classifier shared by the analyzer and the writer:

| format | `WriteFormatKind` | route | AC |
| --- | --- | --- | --- |
| `memory` (`WriteFormats.cs:19`) | `Local` | engine-backed local sink, executes end-to-end | AC1 |
| `delta`, `parquet` (`:33`) | `DeferredToEpic05` | deterministic EPIC-05 diagnostic | AC4 |
| anything else | `Unsupported` | deterministic unsupported-format diagnostic | AC3 |

`Classify(format)` (`WriteFormats.cs:40`) returns exactly one kind; the local and deferred sets are
`FrozenSet<string>` with `OrdinalIgnoreCase` comparison. `Default = "parquet"` (`:24`) makes a writer
that never calls `Format` route to the EPIC-05 deferral — matching Spark's default source while keeping
storage out of this story.

## Analyzer gating (before any output)

The analyzer resolves `WriteToSource` **bottom-up** like every other node: its child (an unresolved
scan) is resolved first, then `ResolveRelations` routes the write node to `ValidateWriteSink`
(`src/DeltaSharp.Core/Analysis/Analyzer.cs:133`). `ValidateWriteSink` (`:143`) classifies the sink
format:

- `Local` → the write node passes through unchanged (a local sink has no reader/writer to bind).
- `DeferredToEpic05` → throws `AnalysisException.UnsupportedDataSink(format, path, localFormats)`
  (`AnalysisException.cs:153`), the write-side analog of the read door's `UnsupportedDataSource`. The
  message names the format, the **redacted** path, and EPIC-05 ownership, and points at the working
  local sink (AC4).
- `Unsupported` → throws `AnalysisException.UnsupportedWriteFormat(...)` (`AnalysisException.cs:180`),
  naming the offending format and the recognized local/deferred formats (AC3).

Both factories set `AnalysisErrorKind.UnsupportedDataSink` (`AnalysisException.cs:71`) and redact the
path. Because this fires during **analysis** — before physical planning or any commit — a bad
format/mode produces its diagnostic **before any partial output** (AC3/AC4). `DeriveOutput` handles
`WriteToSource` as a pass-through of the child output (`Analyzer.cs:608`).

`Mode(string)` is validated **eagerly at call time** (`DataFrameWriter.cs:80-101`): an unrecognized
mode string throws `ArgumentException` immediately, before `Save`. This is a deliberate, Spark-faithful
deviation from AC3's literal "when `Save` is called" — Spark's `mode(String)` also throws
`IllegalArgumentException` at the call site — and it still guarantees no output for a bad mode. See
"Deviations".

## The write action (Core → Executor)

`Save()` delegates to the DataFrame's internal write action `ExecuteWrite`
(`src/DeltaSharp.Core/Session/DataFrame.cs:874`), which lives beside the other actions so every eager
crossing shares one lifecycle guard and one analyze pass:

1. **Lifecycle guard.** `RequireSession(nameof(DataFrameWriter.Save))` throws `InvalidOperationException`
   for an unbound frame and `SessionStoppedException` for a stopped/disposed session — the same guard
   `Collect`/`Count` use, so `Save` after `Stop()` fails identically to a read action.
2. **Analyze + optimize.** `AnalyzeForExecution(session, new WriteToSource(Plan, sink))` (`:878`) wraps
   the frame's plan in the write intent and runs the shared analyze→optimize pipeline (emitting the #169
   audit's single `Analyzer` stage). A bad format/mode surfaces its diagnostic here.
3. **Execute (eager).** `session.QueryExecutor.Write(analyzed, ExecutionOptions.From(session, ct))`
   (`:879`) crosses the internal execution seam. The returned rows-written count is discarded (`Save`
   returns `void`).

### Execution seam (`IQueryExecutor.Write`)

`src/DeltaSharp.Core/Execution/IQueryExecutor.cs:90` adds
`long Write(LogicalPlan analyzedPlan, ExecutionOptions options, ExecutionMetricsSink? metricsSink = null)`
alongside `Collect`/`Count`. It drives the SAME analyze→plan→execute pipeline but drains the result into
the sink instead of returning rows; it returns the number of rows written. The default
`UnsupportedQueryExecutor.Write` throws the "no backend registered" diagnostic
(`UnsupportedQueryExecutor.cs:29`), so a Core-only program that never enabled an executor fails
deterministically (identical to the read path).

## Executor: physical write + local sink

The Executor side is engine-internal (non-packable, no PublicAPI baseline).

### Local sink seam (`ILocalSinkFactory` / `ILocalSink`)

`src/DeltaSharp.Executor/Physical/SinkRegistry.cs` is the **data-out** mirror of the `IScanSource`
data-in seam:

- `ILocalSinkFactory.TryCreate(descriptor, schema, out sink)` (`:15`, impl `:59`) resolves a logical
  `SinkDescriptor` to a concrete sink. Only the `memory` format is engine-backed in M1 (`:63`, a
  defense-in-depth check — the analyzer already gated the format), keyed by the write target
  (`TargetKey`, `:176`: `descriptor.Path` → a **case-insensitive** `path` option → table identifier → a
  stable default). The `path`-option lookup is case-insensitive to match the writer's `OrdinalIgnoreCase`
  option contract; the writer additionally reconciles a `path` option into `descriptor.Path` when no
  `Save(path)` is given, so `Option("path", p).Save()` and `Save(p)` resolve to the same target (Spark
  parity — `save(path)` is `option("path", path).save()`).
- `ILocalSink.Commit(schema, rows)` (`:39`, impl `:208`) atomically commits the fully-materialized rows,
  honoring the `SaveMode`, and **returns the authoritative committed row count** (0 when an `Ignore`
  skipped an existing target). This whole-result, row-oriented `Commit(rows)` is an **M1 in-memory
  convenience** — NOT the final storage-sink seam. EPIC-05 introduces a batched/columnar transactional
  contract (`Begin → WriteBatch(ColumnBatch) → Commit/Abort`) rather than extending this row-list
  signature (see [#443](https://github.com/khaines/deltasharp/issues/443)).
- `InMemorySinkRegistry` (`:50`) holds committed tables keyed by target; commits are serialized under a
  monitor (`Commit`, `:113`) so the `SaveMode` check-and-set is atomic even on the process-wide
  `Default` (`:56`). `TryRead` (`:81`) reads a committed target back — the seam the end-to-end tests use
  to prove `Save` materialized the written rows.

`SaveMode` semantics in `Commit` (`SinkRegistry.cs:113`): `ErrorIfExists` throws
`InvalidOperationException` (redacted target) if the target exists; `Ignore` skips (zero rows) if it
exists; `Overwrite` replaces; `Append` concatenates after an equal-schema check (else throws). The
diagnostic paths call `SecretRedaction.RedactPath` so a collision message never leaks a secret embedded
in the target.

### Physical node (`WriteToSinkPlan`)

`src/DeltaSharp.Executor/Physical/PhysicalPlan.cs:487` — the physical mirror of a leaf `ScanPlan`, but a
sink at the top instead of a source, and the only physical node with a side effect. Its `Execute`
(`:515`) drains the child, materializes the child rows once
(`RowMaterializer.Materialize(child, null, null, ct)`), then captures the sink's authoritative committed
count: `CommittedRowCount = _sink.Commit(child.Schema, rows)`. **Materialize-then-commit is atomic**, so a
mode conflict or a mid-write fault leaves no partial output (AC1/AC3). **The commit is the final failure
boundary**: after it succeeds the node does NO cancellable or fault-prone work — a cancel/timeout is
observed before the commit or not at all, so a committed write never surfaces as a cancelled/failed
`Save`. It returns the child `BatchResult` unchanged and exposes `CommittedRowCount` so the driver's
finalize reports the rows actually WRITTEN — not the rows the child produced — without re-executing or
re-polling the token. Its `SimpleString` renders the **redacted** `SinkDescriptor`.

### Planner + output derivation

- `PhysicalPlanner` gains an optional `ILocalSinkFactory? sinkFactory` constructor parameter
  (`PhysicalPlanner.cs:50`) and a `case LogicalWriteToSource` (`:102`) routed to `PlanWrite` (`:335`).
  `PlanWrite` plans the child, resolves the sink through the factory, and — on a factory miss (the sink
  seam not wired) — throws a `Plan`-stage `UnsupportedPlanException`.
- `LogicalOutput.Derive` adds `WriteToSource` to the shape-preserving case
  (`src/DeltaSharp.Executor/Physical/LogicalOutput.cs:114`), so a write node's output attributes are its
  child's.

### Driver (`LocalQueryExecutor.Write`)

`src/DeltaSharp.Executor/Physical/LocalQueryExecutor.cs:140` implements `Write` by reusing the SAME
stage-attributed `Execute` driver as `Collect`/`Count`, with a finalize that returns the sink's
**authoritative committed count** (`WriteToSinkPlan.CommittedRowCount`) — 0 for an `Ignore` that skipped
an existing target, NOT the child's produced row count. The finalize does **no** cancellable or
fault-prone work (no post-commit `CountRows`, no post-commit token poll), so a cancel/timeout in the
post-commit window can never turn a committed write into a failed `Save`. The executor threads the sink
factory into both `PhysicalPlanner` instantiations (Explain and Execute), and the module initializer
wires the process-wide `InMemorySinkRegistry.Default` via the `(IScanSource, SparkSession)` constructor
chain.

## Stage attribution (a write failure is deterministic)

Because `Write` reuses the #176 driver, a write failure is stage-attributed exactly like a read failure:

- **Analysis** faults (bad format/mode) throw `AnalysisException` before the seam — no stage, no output.
- **Sink not registered** → `Plan`-stage `UnsupportedPlanException` (`PhysicalPlanner.PlanWrite`).
- **Commit conflict** (e.g. `ErrorIfExists` onto an existing target) throws inside
  `WriteToSinkPlan.Execute` → caught by the driver's backend-stage catch → `QueryExecutionException`
  with `Stage == QueryExecutionStage.Backend`. The end-to-end test asserts both the stage attribution
  **and** that the original target's rows are intact (no partial output).

### Read vs. write materialization-fault asymmetry (intended)

A **generic** materialization fault during a WRITE is attributed to `Stage == Backend`, whereas the same
fault during a READ is attributed to `Stage == Materialize`. This is intended, not a bug: a write drains
and materializes its child rows *inside* `WriteToSinkPlan.Execute` (materialization is a sub-step of the
sink commit, which is a backend side-effect), so any fault there is caught by the driver's Backend catch
before the read path's separate `Materialize` stage runs. A READ materializes in the driver's finalize,
which has its own `Materialize`-attributed catch. Note the asymmetry applies only to *generic*
materialization faults — a value/type-mapping fault still carries its own embedded `Materialize`-stage
`UnsupportedPlanException` (from `RowMaterializer`), which propagates unwrapped from either path. Per the
QueryExec review this is documented as intended rather than re-architected for exact read/write parity;
the sink commit legitimately owns the write-side materialization boundary.

## Lazy/eager guarantees

- **Getting the writer** (`DataFrame.Write`) constructs a builder; it opens no sink and does no
  analysis (`DataFrame.cs:555`).
- **Configuring the writer** (`Format`/`Mode`/`Option`/`Options`/`PartitionBy`) only mutates in-memory
  intent and returns the writer. The Core tests assert an **empty** #169 audit stage path and zero seam
  crossings while a whole config chain runs (`WriteDoorTests.WriterConfiguration_IsLazy_*`).
- **`Save()`** is the ONLY writer member that executes: it records exactly one `Analyzer` stage followed
  by the `Planner`/`Backend` stages (`WriteDoorTests.Save_RecordsExactlyOneAnalyzerStage_*`), the same
  shape a read action records.

## Seam to #179 (`Explain`)

`ExplainPhysical` plans a write node without executing it (the sink factory is threaded through so a
`WriteToSink` node renders), and `WriteToSinkPlan.SimpleString` / `SinkDescriptor.SimpleString` render
the **redacted** path — so explaining a write plan never opens a sink and never leaks a secret.

## Test plan

**Core (`tests/DeltaSharp.Core.Tests/WriteDoor/WriteDoorTests.cs`, 28 tests)** — a fresh writer per
access; lazy config touches no audit seam and never crosses the seam (AC2); `Mode(string)` case-insensitive
parse + call-time validation of a bad mode; `Save` builds an analyzed `WriteToSource` and crosses the
seam once recording `[Analyzer, Planner, Backend]` (AC1); typed-option coercion on the intent;
unsupported-format (AC3) and Delta/Parquet EPIC-05 (AC4) diagnostics fire **before** the seam;
default-parquet routing; diagnostic path redaction; stopped-session parity; `SinkDescriptor.SimpleString`
redaction; a `path` option (any casing) reconciled into the descriptor path with `Save(path)` precedence;
and `PartitionBy` columns carried onto the built intent (with defensive-copy proof).

**Executor (`tests/DeltaSharp.Executor.Tests/WriteDoorEndToEndTests.cs`, 20 tests)** — `Save`
materializes to the in-memory sink and round-trips rows/nulls; writes the **transformed** result;
`Append`/`Ignore`/`Overwrite` semantics; an `Overwrite` onto an existing target **replaces** (not
appends/ignores); an `Ignore` onto an existing target reports/commits the **authoritative 0** (not the
child's produced count) via the count-returning seam; a cancel firing in the **post-commit** window does
NOT fail the `Save` and leaves the write committed; a `path` option (any casing) routes case-insensitively
through `TargetKey`; `ErrorIfExists` collision is `Backend`-stage-attributed and leaves no partial output;
unsupported/deferred/default formats fail before any output; empty-frame write; and no secret leaks in a
failure diagnostic (both the analysis-deferral and the commit-collision paths).

## Deviations from the story

- **`Mode(string)` validates eagerly at call time**, not at `Save`. AC3 reads "when `Save` is called",
  but Spark's `DataFrameWriter.mode(String)` throws `IllegalArgumentException` at the call site, so
  DeltaSharp matches Spark. The guarantee AC3 cares about — a bad mode never produces partial output —
  holds either way (the throw precedes `Save`).
- **One local sink in M1.** Only Spark's `memory` format executes end-to-end (AC1). File-format writers
  (`delta`/`parquet`) are deferred to EPIC-05 (AC4), mirroring how the read door defers Parquet reads.
- **`PartitionBy` is recorded intent only.** The partition columns are carried on the `SinkDescriptor`
  and honored by future storage sinks; the M1 in-memory sink does not physically partition, and the
  columns are not yet validated against the child schema (Spark errors on an unknown partition column —
  deferred to [#444](https://github.com/khaines/deltasharp/issues/444)).

## Deferred follow-ups (write-door review, PR #441)

- **Unbounded write materialization** — the write drains the whole result into a driver `List<Row>`
  (result bounds intentionally not applied to a write); a tenant-aware write ceiling / streaming staged
  commit is [#442](https://github.com/khaines/deltasharp/issues/442).
- **EPIC-05 sink contract shape** — `Commit(schema, rows)` is an M1 in-memory convenience; the batched/
  columnar transactional seam (`Begin → WriteBatch → Commit/Abort`) is
  [#443](https://github.com/khaines/deltasharp/issues/443).
- **`PartitionBy` schema validation** at analysis — [#444](https://github.com/khaines/deltasharp/issues/444).
- **`Save(string, CancellationToken)` overload** for cancellable path writes —
  [#445](https://github.com/khaines/deltasharp/issues/445) (the honored `path` option already makes
  `Option("path", p).Save(ct)` a cancellable path write).
