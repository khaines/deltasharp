# `EXPLAIN`: rendering logical, optimized, and physical plans (v1)

> **Status:** living document. Created with
> [STORY-04.7.3](https://github.com/khaines/deltasharp/issues/179) (`EXPLAIN` for logical, optimized,
> and physical plans — the developer-experience lane of Batch O). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (pluggable backends; the interpreted vectorized
> backend is the default and correctness reference) and [ADR-0008](../../adr/0008-shared-type-model.md)
> (shared logical type model). Consumes the Core logical IR
> ([logical-plan-nodes.md](logical-plan-nodes.md), [expression-model.md](expression-model.md)), the
> analyzer ([analyzer-resolution.md](analyzer-resolution.md)), the optimizer
> ([logical-optimizer.md](logical-optimizer.md)), and the physical planner
> ([physical-planning.md](physical-planning.md)). Update it whenever the `Explain` API, the rendered
> section format, the Core↔Executor seam, or the diagnostic behaviour changes.

`DataFrame.Explain(...)` renders a query's plan at each pipeline stage — the unresolved (parsed)
logical plan, the analyzed logical plan, the optimized logical plan, and the physical plan — so a user
debugging a query can see how DeltaSharp will run it. It mirrors Apache Spark's `Dataset.explain`
semantics and, crucially, **preserves the lazy/eager invariant** (ADR-0001): *no* `Explain` mode
triggers execution. Logical/analyzed/optimized rendering happens entirely inside `DeltaSharp.Core`;
physical rendering *plans* the query (through the executor seam) but never *runs* it.

---

## 1. Where the code lives

| Concern | Assembly | Type(s) |
| --- | --- | --- |
| Public `Explain` API + section assembly | `DeltaSharp.Core` (`net8.0;net10.0`, packable) | `DataFrame.Explain*`, `DataFrame.ToExplainString`, `ExplainMode` |
| Tree rendering of the logical IR | `DeltaSharp.Core` | `TreeNode<T>.TreeString()` (reused as-is) |
| Analyze/optimize stages | `DeltaSharp.Core` | `Analyzer`, `Optimizer`/`DataFrame.Optimize` seam |
| Physical-plan string (the seam) | contract in `DeltaSharp.Core`, impl in `DeltaSharp.Executor` | `IQueryExecutor.ExplainPhysical`, `LocalQueryExecutor`, `PhysicalPlan.TreeString()` |

**Core never references the Executor or the Engine.** Physical planning lives in `DeltaSharp.Executor`
(`net10.0`), and Core is a packable `net8.0;net10.0` library, so Core obtains the physical-plan string
through the same dependency-inversion seam its actions already use — the internal
`IQueryExecutor` registered via `SparkSession.RegisterQueryExecutorFactory`
(`src/DeltaSharp.Core/Execution/IQueryExecutor.cs`, `src/DeltaSharp.Executor/Physical/ExecutorRegistration.cs`).
We **extend** that seam with a physical-plan-string capability rather than making Core depend on the
Executor.

---

## 2. Public API (Spark parity)

The public surface mirrors Spark's `Dataset.explain` overloads. Each `Explain` method prints to the
console (Spark's `explain` returns `void`); an `internal string ExplainString(ExplainMode)` backs the
console methods so the rendered text is unit-testable (the same pattern as `Show`/`ShowString`). Unlike
`Show`, `Explain` also exposes a **public** string-returning form, `ToExplainString(mode)`, a zero-logic
wrapper over `ExplainString` (a matching `ToShowString` for `Show` is a separate follow-up, out of scope
here).

```csharp
public void Explain();                       // Spark explain()          -> physical plan only
public void Explain(bool extended);          // Spark explain(extended)  -> all four sections when true
public void Explain(string mode);            // Spark explain(mode)      -> "simple"/"extended"/...
public void Explain(ExplainMode mode);       // strongly-typed .NET overload
public string ToExplainString(ExplainMode mode = ExplainMode.Simple); // returns the text instead of printing
```

`Explain(...)` prints to the console (Spark's `explain` returns `void`); `ToExplainString(mode)` is a thin
public wrapper over the same internal `ExplainString(mode)` that **returns** the rendered text instead of
writing it, so callers can capture the plan in logs, tests, or diagnostics without redirecting
`Console.Out`. Like `Explain`, it renders **without executing**.

`ExplainMode` mirrors Spark's `org.apache.spark.sql.execution.ExplainMode` exactly:

```csharp
public enum ExplainMode
{
    Simple = 0,     // physical plan only (the Spark default)
    Extended = 1,   // parsed + analyzed + optimized + physical
    Codegen = 2,    // whole-stage codegen — not part of the M1 interpreted backend (diagnostic)
    Cost = 3,       // plan + statistics — statistics are not collected in M1 (diagnostic)
    Formatted = 4,  // physical plan (per-node detail sections deferred with metrics, #176)
}
```

`Explain(string mode)` accepts the Spark mode strings (case-insensitive): `"simple"`, `"extended"`,
`"codegen"`, `"cost"`, `"formatted"`. An unrecognised string throws `ArgumentException` listing the
valid modes — this mirrors Spark's `IllegalArgumentException` and is *API misuse*, distinct from the
plan-diagnostic behaviour of §6 (which concerns plan **content**, not the mode argument).

`Explain(bool extended)` maps `true` → `Extended` and `false` → `Simple` (Spark parity).

### Sample usage

```csharp
SparkSession spark = SparkSession.Builder().AppName("explain-demo").GetOrCreate();
DataFrame df = spark.Sql("SELECT name, age FROM people")   // any DataFrame door (Sql/Read/CreateDataFrame)
    .Filter(Col("age").Gt(21))
    .Select(Col("name"), Col("age"));

df.Explain();                                  // physical plan only (Spark default)
df.Explain(extended: true);                    // parsed + analyzed + optimized + physical
df.Explain("extended");                        // same, via the Spark mode string
df.Explain(ExplainMode.Simple);                // strongly-typed overload
string text = df.ToExplainString(ExplainMode.Extended); // capture instead of print
```

> **M1 door note.** `spark.Sql(...)` returns a DataFrame backed by an unresolved plan without executing
> (the public file read-door `Read.Parquet`/`CreateDataFrame` is STORY-04.1.2 / #158); there is **no**
> `spark.Read.Table(...)` in M1. Any door that yields a `DataFrame` works — `Explain` only builds and
> renders plans.

None of these executes the query — `Explain` is a debugging aid, not an action.

### Story-criteria mapping

The story's acceptance criteria describe rendered **content**; the modes above deliver them:

| Story criterion | Delivered by |
| --- | --- |
| AC1 — *logical mode*: unresolved plan rendered without execution | The **Parsed Logical Plan** section (present in `Extended`), rendered from the raw unresolved `LogicalPlan`; never executes. |
| AC2 — *extended mode*: analyzed/optimized rendered separately from unresolved | `Extended`'s four separately-headed sections. |
| AC3 — *physical mode*: scans/filters/projections/joins/aggregates/sorts/limits/writes visible | The **Physical Plan** section (present in `Simple`, `Extended`, `Formatted`). |
| AC4 — unsupported/unresolved status without hiding diagnostics; never a raw exception | §6. |
| AC5 — physical execution metadata after an action, without changing results | §7 (optional metrics seam). |

---

## 3. Rendered format

`Extended` produces four sections, each with a Spark-style `== <Title> ==` header, in pipeline order:

```
== Parsed Logical Plan ==
'Project ['name, 'age]
+- 'Filter (('age > 21))
   +- 'UnresolvedRelation [people]

== Analyzed Logical Plan ==
Project [name#0, age#1]
+- Filter ((age#1 > 21))
   +- ResolvedRelation [people], [name#0, age#1]

== Optimized Logical Plan ==
Project [name#0, age#1]
+- Filter ((age#1 > 21))
   +- ResolvedRelation [people], [name#0, age#1]

== Physical Plan ==
Project [name#0, age#1]
+- Filter ((age#1 > 21))
   +- Scan [name, age]
```

`Simple` emits only the `== Physical Plan ==` section. `Formatted` emits the physical section **plus** a
diagnostic note that its per-node detail sections arrive with execution metrics (#176). `Cost` emits the
four logical/physical sections plus a diagnostic note that statistics are unavailable in M1. `Codegen`
emits the physical section plus a diagnostic note that whole-stage codegen is not part of the M1
interpreted backend (ADR-0001).

The logical trees are rendered by the **existing** `TreeNode<T>.TreeString()`
(`src/DeltaSharp.Core/Plans/TreeNode.cs`), which already produces Spark's indented tree with
`+-`/`:-` connectors and the leading-apostrophe unresolved marker (`LogicalPlan.UnresolvedPrefix`). We
**reuse** it verbatim — no new logical renderer. Physical rendering (§5) mirrors the same connector
format. (Neither renderer numbers nodes.)

---

## 4. How each stage is obtained (Core)

`ExplainString(ExplainMode)` **analyzes the plan once** (a single `Resolve` pass via the private
`TryAnalyze` helper), then reuses that one analyzed plan for *both* the logical analyzed/optimized
sections *and* the physical section — so the rendered analyzed plan and the rendered physical plan can
never come from divergent analyses. It resolves and optimizes on the same seam the actions use, so what
`Explain` shows is what would run:

1. **Parsed Logical Plan** — `this.Plan` (the unresolved `LogicalPlan` the DataFrame wraps).
   `Plan.TreeString()`. No session, no analysis, no execution.
2. **Analyzed Logical Plan** — the single `TryAnalyze` result, i.e. `new Analyzer(session.Catalog).Resolve(Plan)`
   — the same `Resolve` call `Collect`/`Count` make (they route it through `DataFrame.AnalyzeForExecution`;
   `Explain` calls it directly in `TryAnalyze` so it can capture a failure as a diagnostic). Wrapped in
   `try/catch` (§6).
3. **Optimized Logical Plan** — `DataFrame.Optimize(analyzed)` over that same analyzed plan — the **same
   seam** the action pipeline drives. In M1 that seam is an intentional identity pass
   (`DataFrame.Optimize`, documented at `src/DeltaSharp.Core/Session/DataFrame.cs`), so the Optimized
   section is structurally identical to the Analyzed section. Spark also prints both sections even when
   they are equal; keeping `Explain` on the same seam guarantees the Optimized section always reflects
   what the executor will actually plan (when #415/#174 wire the real optimizer into the action
   pipeline, `Explain` follows automatically because it shares the seam).
4. **Physical Plan** — `session.QueryExecutor.ExplainPhysical(Optimize(analyzed))` over the **same**
   analyzed plan (§5). Planned, not executed.

The whole output is normalized to end with exactly one newline: sections are separated by a blank line,
but no trailing blank line is emitted after the final section (Spark layout parity).

`ExplainString` obtains the session through the same `RequireSession` guard as the actions (so
`Explain` after `Stop()` throws `SessionStoppedException`, Spark parity). A session-less internal frame
is not a supported public scenario (public DataFrames always come from a `SparkSession` door).

---

## 5. The Core↔Executor seam for physical mode

Core cannot see `PhysicalPlanner`/`PhysicalPlan` (they live in `DeltaSharp.Executor`). We extend the
internal `IQueryExecutor` with a physical-plan-string capability:

```csharp
internal interface IQueryExecutor
{
    IReadOnlyList<Row> Collect(LogicalPlan analyzedPlan);
    long Count(LogicalPlan analyzedPlan);
    string ExplainPhysical(LogicalPlan analyzedPlan);   // NEW — plans but never executes
}
```

Implementations:

- **`LocalQueryExecutor.ExplainPhysical`** (`DeltaSharp.Executor`) runs
  `new PhysicalPlanner(_scanSource).Plan(analyzedPlan)` — the *same* planner `Collect`/`Count` use —
  and returns `physicalPlan.TreeString()`. It does **not** call `PhysicalPlan.Execute`, so no operator
  opens, no batch is read, no backend runs. Two failure modes are caught and rendered as a diagnostic
  line (§6), never rethrown: a specific `catch (UnsupportedPlanException)` (an operator/expression with
  no M1 mapping, e.g. a `CrossJoin` or a `WriteToSource`) preserves the planner's precise message, and a
  **broad** `catch (Exception ex)` fallback renders `<cannot plan physically: {ex.Message}>`. The broad
  catch is required because `PhysicalPlanner.Plan` eagerly constructs Engine expressions during planning,
  and some Engine expression constructors throw a raw `ArgumentException` for operand-type combinations
  the analyzer *accepts* (e.g. `lit(null) == lit(null)` on `void`/`void`, or `array<int> == array<int>`).
  The `IQueryExecutor.ExplainPhysical` contract is non-throwing (AC4), so those must degrade to a
  diagnostic rather than escape.
- **`UnsupportedQueryExecutor.ExplainPhysical`** (`DeltaSharp.Core`, the fail-closed default when no
  Executor is registered) returns a diagnostic line explaining that no execution backend is registered
  — it does **not** throw (unlike `Collect`/`Count`, which do). Physical mode therefore degrades
  gracefully to a diagnostic when Core is used without `DeltaSharp.Executor` (AC4).

### Physical-plan rendering (`PhysicalPlan.TreeString()`)

`PhysicalPlan` gains a `NodeName`, a `SimpleString`, and a `TreeString()` that mirrors the Core
`TreeNode` connector format (`+-`/`:-`). Each node renders its type and light, always-available
metadata (output field names, join type, limit count, sort keys), plus a defensive expression renderer
(`PhysicalPlanText.Expr`) for predicates/projections/keys:

| Physical node | `SimpleString` example |
| --- | --- |
| `ScanPlan` | `Scan [id, dept, salary]` |
| `FilterPlan` | `Filter ((salary#2 > 21))` |
| `ProjectPlan` | `Project [id#0, salary#2]` |
| `AggregatePlan` | `Aggregate keys=[dept#1], functions=[sum(salary#2)]` |
| `JoinPlan` | `Join Inner [deptId#1] = [dId#0]` |
| `SortPlan` | `Sort [salary#2 DESC] global` |
| `LimitPlan` | `Limit 5` |
| `UnionPlan` | `Union [id, dept, salary]` |

`ProjectPlan` renders the projection **expressions** (not just the output names), consistent with how
Filter/Aggregate/Sort render theirs: a computed projection gets an `AS name` suffix
(`Select((id+id).As("twice"))` → `Project [(id#0 + id#0) AS twice]`), while a bare column reference
renders as just `name#ordinal`. `SortPlan` appends ` global` for a top-level (non-partition-local) sort,
matching Spark's global/local distinction.

`PhysicalPlanText.Expr` handles the known Engine expression leaves/nodes (`ColumnReference` →
`name#ordinal` when the node's input `StructType` is available, else a bare `#ordinal`;
`Literal` → its value, `ComparisonExpression`/`ArithmeticExpression`/`LogicalExpression` →
`(l op r)`, `IsNullExpression` → `isnull(x)`/`isnotnull(x)`, `CastExpression` → `cast(x as T)`,
`AggregateExpression` → `fn(x)`), and falls back to `TypeName(children…)` for anything unknown. It
**never throws** — meaningful physical output must not become a crash (AC4). The `name#ordinal` form is
best-effort: each physical node threads its child/input `OutputSchema` into `Expr`, so where the schema
is cleanly available the ordinal is resolved to a name; where it is not, the bare `#ordinal` remains.

> **Write nodes (AC3).** `WriteToSource` is visible in the logical/analyzed/optimized sections (it has
> a `SimpleString`). The M1 physical planner has no write strategy yet (there is no public
> `DataFrameWriter` in M1), so physical mode of a write plan renders the AC4 diagnostic rather than a
> `WriteExec` node. A physical write operator is deferred to the write-execution lane; `Explain`
> already renders it in the logical sections and will pick up the physical node automatically once the
> planner maps it. This is the single documented deviation from AC3's node list.

---

## 6. Diagnostics — never throw in place of a diagnostic (AC4)

`Explain` must surface unresolved/unsupported status as text, never as an escaping exception:

- **Parsed section** is always renderable (it is a pure `TreeString` of the unresolved plan) and shows
  the leading-apostrophe markers for unresolved attributes/functions/relations
  (`TreeRenderTests` already pin this format).
- **Analyzed/Optimized sections**: only `Resolve` is wrapped in `try/catch (AnalysisException)`
  (`Optimize` is the M1 identity seam and is invoked *outside* the guard — it cannot fail in M1). A
  resolution failure renders a single diagnostic line — e.g. `<cannot analyze plan: <message>>` — under
  the section header instead of propagating. Because the plan is analyzed **once** (§4), a resolution
  failure yields the *same* diagnostic under both the Analyzed and Optimized headers, and the physical
  section renders its own no-plan diagnostic. The Parsed section above still shows the offending
  unresolved plan, so diagnostics are *not hidden*.
- **Physical section**: obtained via the seam, which is contractually non-throwing (§5). Unsupported
  operators/expressions and the no-backend case both become diagnostic lines.

The only exceptions `Explain` may throw are *API-misuse* signals shared with the other members:
`InvalidOperationException` (frame not bound to a session), `SessionStoppedException` (session stopped),
and `ArgumentException` (unrecognised mode string). These are not "plan constructs" and are outside
AC4's scope.

---

## 7. Optional execution-metrics seam (AC5)

A sibling lane ([#176](https://github.com/khaines/deltasharp/issues/176)) adds per-operator execution
metrics (rows, time) collected during an action. `Explain` is designed with an **optional** hook for
this and **does not depend on #176 landing**:

- The physical-plan string is produced *entirely* by the Executor's `ExplainPhysical`, and the Executor
  is also the sole owner of physical planning **and** execution (`LocalQueryExecutor` runs
  `PhysicalPlanner` and drives the backend). It is therefore the natural — and only — place metrics get
  woven in: when #176 lands, `LocalQueryExecutor.ExplainPhysical` can append per-node metrics to each
  physical line if a metrics source is available for the plan, exactly as Spark's `formatted`/`cost`
  modes annotate operators.
- No Core change is required to add metrics: the seam signature (`string ExplainPhysical(LogicalPlan)`)
  is already the whole contract, and metrics are additive text on the returned string. Rendering
  **without** metrics (M1) and **with** metrics (post-#176) both satisfy AC5's "without changing query
  results" — `Explain` never executes and never mutates the plan.

This keeps the seam minimal (one method) while giving #176 a clear, non-breaking insertion point.

---

## 8. Lazy/eager guarantees

- Logical/analyzed/optimized rendering is pure plan manipulation in Core: `TreeString`, `Analyzer`,
  and the identity `Optimize` seam do no I/O and touch no backend (ADR-0001, lazy/eager invariant).
- Physical rendering calls `PhysicalPlanner.Plan` only; it never calls `PhysicalPlan.Execute`. Planning
  reads batch *references* to build a `ScanPlan` but iterates no rows and opens no operator.
- The `#169` execution audit seam (`ExecutionAudit`) records no *execution* during any `Explain` mode.
  `Explain` does enter the **Analyzer** stage (it calls `Resolve`, exactly as an action would), so it is
  **not** true that zero stages are observed — `RecordingAudit.ObservedNoExecution` (which requires
  `stageCount == 0`) would be *false* and must **not** be asserted. The real, testable guarantee is
  narrower: `Explain` records the Analyzer stage but **no files or rows are read** (`OnFileOpened`/
  `OnRowsRead` never fire), **no Planner or Backend stage** is entered, and the fake executor's
  `Collect`/`Count` counters stay at zero. The Core suite asserts exactly that.

---

## 9. Test plan

**`DeltaSharp.Core.Tests`** (logical/extended, no execution — via `FakeQueryExecutor`):

- AC1: `Explain(Extended)` Parsed section shows apostrophe-prefixed unresolved nodes; `Collect`/`Count`
  never called; the audit records the Analyzer stage but no files/rows read and no Planner/Backend stage
  (§8) — `RecordingAudit.ObservedNoExecution` is **not** asserted (Explain enters the Analyzer stage).
- AC2: `Extended` emits the four `== … ==` headers in order; the Analyzed section has no apostrophe
  markers (resolved), and is rendered separately from the Parsed section.
- AC3 (Core view): `Simple`/`Extended` include a `== Physical Plan ==` section carrying the fake
  executor's physical string.
- AC4: a frame over an unknown relation/column renders a `<cannot analyze plan: …>` diagnostic (no
  throw), and the no-backend path (default `UnsupportedQueryExecutor`) renders a physical diagnostic (no
  throw).
- AC5: the physical string flows through unchanged; documents the metrics seam (no metrics in M1).
- API: `Explain(true)`/`Explain(false)`, `Explain("extended")`/`Explain("simple")` parity (pinned by a
  Console-capture / `ToExplainString` mapping test); an unknown mode string throws `ArgumentException`;
  `Explain` after `Stop()` throws `SessionStoppedException`.
- `ToExplainString(mode)` returns the same text `Explain(mode)` prints, for every mode (public wrapper).
- `Codegen`/`Cost`/`Formatted` render their documented sections + diagnostic notes.

**`DeltaSharp.Executor.Tests`** (physical mode via the registered executor + `InMemoryRelationFixture`):

- AC3: for each supported node (scan/filter/project/aggregate/join/sort/limit/union/distinct) the
  physical string contains the expected operator line; nested trees use `+-`/`:-` connectors. `Project`
  renders projection expressions (with `AS name` for computed columns).
- No-execution: a rigorous oracle (`ExecutionSentinelScanSource`, whose batch list throws / increments a
  counter on any access) drives `ExplainPhysical` and asserts **zero** batch accesses and no
  `<cannot plan physically …>` diagnostic — the test fails if `ExplainPhysical` were changed to execute.
  A subsequent `Collect` through the same source proves the sentinel *does* trip on real execution.
- AC4: an unsupported node (`CrossJoin`, or a `WriteToSource` plan) renders a diagnostic line rather
  than throwing `UnsupportedPlanException`; and a plan whose Engine expressions throw during planning
  (`lit(null) == lit(null)`, `array<int> == array<int>`) renders a `<cannot plan physically: …>`
  diagnostic via the real `LocalQueryExecutor` (broad-catch path) rather than throwing.
- Seam: `session.QueryExecutor.ExplainPhysical(analyzed)` returns the physical string (proves the
  registered `LocalQueryExecutor` implements the extended seam).
