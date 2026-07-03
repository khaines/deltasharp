# Actions and Row materialization (M1)

> **Status:** living document. Created with
> [STORY-04.6.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md#story-0461-collect-count-and-show-actions)
> (FEAT-04.6, issue #173) and
> [STORY-04.7.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md#story-0471-row-and-schema-materialization)
> (FEAT-04.7, issue #177). Depends on the immutable logical IR (#167), the expression IR (#168), the
> plan-construction audit seam (#169, [lazy-eager-audit.md](lazy-eager-audit.md)), the analyzer
> (#171, [analyzer-resolution.md](analyzer-resolution.md)), and the ADR-0008 logical type model
> ([shared-type-model.md](shared-type-model.md)). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (pluggable execution backend) and
> [ADR-0014](../../adr/0014-target-framework-aot.md) (multi-targeting). Update it whenever the action
> set, the `IQueryExecutor` seam shape, the `Row` contract, or the `show` formatting changes.

## Why this exists

DeltaSharp's single most important invariant is:

> **Transformations are lazy; actions are eager.** Building a plan does **no** work. Only an action
> triggers the engine.

This document specifies the **first three actions** — `Collect`, `Count`, `Show` — and the public
**`Row`** materialization contract their results carry. It also defines the
**dependency-inversion seam** (`IQueryExecutor`) through which `DeltaSharp.Core` drives execution
*without* referencing the engine, so the Core ⟂ Engine sibling independence (ADR-0014) is preserved.

The sibling lane STORY-04.6.2 (#174, physical planning in `DeltaSharp.Executor`) implements the seam
and materializes engine `ColumnBatch` results into `Row`s. This story defines `IQueryExecutor` and
`Row` cleanly and ships a **default** backend so Core is self-contained and testable until #174 lands.

## The action set (Spark parity)

All three live on `DeltaSharp.DataFrame` (`src/DeltaSharp.Core/Session/DataFrame.cs`) and are the
**only** members of that type that execute.

| Member | Signature | Spark equivalent | Semantics |
| --- | --- | --- | --- |
| `Collect` | `IReadOnlyList<Row> Collect()` | `Dataset.collect()` | Analyze → optimize → execute; materialize every result row. |
| `Count` | `long Count()` | `Dataset.count()` | Analyze → optimize → execute; return the row count without materializing rows. |
| `Show` | `void Show(int numRows = 20, bool truncate = true)` | `Dataset.show(numRows, truncate)` | Execute (bounded) and print a Spark-style table to the console. |

`Show`'s formatting is factored into an **internal, testable** helper:

```csharp
internal string ShowString(int numRows, bool truncate)   // returns the rendered table string
```

`Show(...)` is `Console.Out.Write(ShowString(numRows, truncate))`. The string form is what the unit
tests assert against, so console rendering has no untested logic.

### The eager pipeline

Every action follows the same path, and it is the only place a `DataFrame` crosses from lazy plan
construction into eager execution:

```
DataFrame.Collect/Count/Show
  └─ RequireSession()                         // frame bound to an ACTIVE SparkSession (not stopped)
  └─ AnalyzeForExecution(session, plan)
        ├─ new Analyzer(session.Catalog).Resolve(plan)   // emits ExecutionAudit.StageEntered(Analyzer)
        └─ Optimize(analyzed)                            // #172 seam — INTENTIONAL identity pass in M1
  └─ session.QueryExecutor.Collect/Count(analyzedPlan)   // the IQueryExecutor seam (eager)
  └─ (Show only) FormatTable(...)                        // render, header from the analyzed output schema
```

- **Analyze** reuses the existing `Analyzer.Resolve` (#171). Resolution is idempotent for an
  already-resolved plan, so re-analysis is safe. The analyzer already emits the `Analyzer` audit
  milestone (#169), which is what makes "an action executed" observable.
- **Optimize** is a **seam**: `DataFrame.Optimize(LogicalPlan)` is an **intentional identity pass in
  M1**. The standalone rule-based optimizer (STORY-04.5.3 / #172) is **already merged** — it ships as a
  standalone `internal Optimizer` (`new Optimizer().Optimize(plan)`), *not* wired into this pipeline.
  Wiring it into the action driver is deferred to the **#174** physical-planning bridge and is **gated
  on [#415](https://github.com/khaines/deltasharp/issues/415)**: the engine currently evaluates
  `And`/`Or` **eagerly** with no per-lane short-circuit, so an optimizer-combined filter could raise
  ANSI errors on guard-excluded rows. Keeping `Optimize` as an explicit, named step is the point — #174
  can wire it without reshaping the action pipeline or the `IQueryExecutor` contract.
- **Execute** is delegated to the session's `IQueryExecutor` (below).
- **RequireSession** also asserts the owning session is **active**: an action on a stopped/disposed
  session throws `SessionStoppedException` (Spark parity), before the analyzer or executor is reached.

### `Show` formatting contract (M1)

`ShowString` collects **`numRows + 1`** rows (a bound pushed onto a *derived* `Limit` plan — this
frame's own `Plan` is never mutated, satisfying #173 AC3) so it can tell whether more rows exist. It
then renders:

- A bordered box using `+`, `-`, `|` with per-column widths (minimum 3, Spark parity).
- The **header** from the **analyzed plan's output schema** (not `collected[0].Schema`), so an **empty**
  result still renders real column headers (Spark parity) instead of a degenerate `++`/`++` box. The
  schema is captured from the *same* analyze pass the execution uses (`Analyzer.Resolve(plan, out
  schema)`), so deriving it costs no extra `Analyzer` audit stage.
- Cells rendered by `Row.Render`: `null` → `null`, booleans lower-cased, numeric/temporal values via
  `IFormattable.ToString(null, CultureInfo.InvariantCulture)` so output is locale-independent and
  deterministic.
- **Truncation**: when `truncate` is true, cells wider than 20 characters are cut to 17 chars + `...`
  (for widths < 4, a hard cut). Cells are **right-justified** when truncating and **left-justified**
  when not, matching Spark.
- A footer `only showing top N rows` (singular `row` for `N == 1`) when the result has more than
  `numRows` rows.

Example — `df.ShowString(2, truncate: true)` over `[name:string, age:int]`:

```
+-----+---+
| name|age|
+-----+---+
|Alice| 30|
|  Bob| 25|
+-----+---+
only showing top 2 rows
```

**Guard ordering.** `ShowString` validates `numRows` first — the `numRows < 0` check throws
`ArgumentOutOfRangeException` **before** `RequireSession()` — so a bad argument fails the same way on a
session-free or stopped frame as on a bound one.

**Empty results.** Because the header comes from the analyzed output schema, a zero-row result renders
the full header and a closed (data-less) box rather than the degenerate `++`/`++`. A genuinely
zero-*column* result still renders `++`/`++`.

## The lazy → eager boundary and how #169 proves it

The [plan-construction audit seam](lazy-eager-audit.md) (`ExecutionAudit`, `IExecutionAudit`,
`ExecutionStage`, all `internal` in `DeltaSharp.Abstractions`) is the substrate that makes the
boundary **observable**:

- **Transformations** (`Select`, `Filter`, `WithColumn`, `GroupBy`, `Join`, `Limit`, `Distinct`,
  `Union`, `Sort`, …) only build immutable plan nodes and **never** enter a stage. A `RecordingAudit`
  sink installed while a transformation chain runs observes **no** stages
  (`recording.ObservedNoExecution` is true).
- **Actions** enter exactly one `Analyzer` stage per call (from `Analyzer.Resolve`). The registered
  `IQueryExecutor` then enters `Planner` and `Backend` (the real #174 backend bridge does this; the
  test `FakeQueryExecutor` reproduces it). So a single `Collect`/`Count` produces the ordered path
  `Analyzer → Planner → Backend`, and *N* actions produce *N* `Analyzer` stages.

The oracle the tests assert:

- transformation-only chain ⇒ **zero** `Analyzer` stages (and zero files/rows), and
- each action ⇒ **exactly one** `Analyzer` stage.

This directly encodes #173 AC1 (the analyzer and planner/backend are invoked exactly once per action;
the optimizer is an **intentional identity pass in M1**, not an audited stage — the cited test asserts
the `[Analyzer, Planner, Backend]` path) and AC4 ("no action execution steps occur for a
transformation chain").

## The `IQueryExecutor` dependency-inversion seam

### Why Core cannot execute directly

`DeltaSharp.Core` is a **packable `net8.0;net10.0`** public library that may reference only
`DeltaSharp.Abstractions` (ADR-0014). The vectorized backend lives in `DeltaSharp.Engine` /
`DeltaSharp.Executor`, both **`net10.0`-only** and non-packable. Core therefore **cannot** reference
the engine and cannot execute a plan itself. The resolution is **dependency inversion**: Core defines
the contract; the executor lane implements it.

### The contract

`src/DeltaSharp.Core/Execution/IQueryExecutor.cs`, namespace `DeltaSharp.Execution`:

```csharp
internal interface IQueryExecutor
{
    IReadOnlyList<Row> Collect(LogicalPlan analyzedPlan);
    long Count(LogicalPlan analyzedPlan);
}
```

- It is **`internal`** because it references the internal `LogicalPlan` IR; it can never be public.
- `analyzedPlan` is the analyzer-resolved plan. The `Optimize` seam sits *before* this call, so the
  executor always receives the final logical plan; because that seam is an **intentional identity pass
  in M1** (the standalone #172 optimizer is not wired in — see above), the resolved plan and the plan
  the executor runs are currently the same tree. All rows an implementation returns from `Collect`
  share the **analyzed plan's output schema**.
- An implementation owns physical planning, the EPIC-03 backend invocation, and `ColumnBatch → Row`
  materialization. It is the sole eager-execution owner.

`DeltaSharp.Core` grants the executor lane access so #174 can implement it across the assembly
boundary, via `DeltaSharp.Core.csproj`:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="DeltaSharp.Executor" />
</ItemGroup>
```

(The `DeltaSharp.Core.Tests` grant is auto-injected by `Directory.Build.props`.)

### How execution is injected into a session

`SparkSession` (`src/DeltaSharp.Core/Session/SparkSession.cs`) holds the executor and exposes two
injection points:

```csharp
internal LocalCatalog Catalog { get; }                    // analyzer catalog for actions
internal IQueryExecutor QueryExecutor { get; set; }       // lazily created; per-session override
internal static void RegisterQueryExecutorFactory(Func<SparkSession, IQueryExecutor>? factory);
```

- `QueryExecutor` is created lazily from the registered **factory**, defaulting to
  `UnsupportedQueryExecutor` when no factory is registered. The lazy publish is **atomic**
  (`Interlocked.CompareExchange` on a `Volatile`-read field): `SparkSession` is explicitly
  multi-threaded, and a #174 factory may be stateful, so the getter never double-invokes the factory or
  hands two threads two different executors. The per-session **setter** lets a test (or a caller)
  install a double for one session.
- `RegisterQueryExecutorFactory` is the **process-wide** wiring point the executor lane (#174) calls
  once at startup so every subsequently-resolved session gets the real backend — without inverting
  Core ⟂ Engine independence. The backing static is `volatile`. It is a process-global, so **register
  the factory at startup, before any session is used concurrently**; it affects only sessions that have
  not yet resolved their executor. Passing `null` resets to the unsupported default (tests reset in a
  `finally`).
- `Catalog` is the session's in-memory `LocalCatalog`; an action constructs `new Analyzer(Catalog)` to
  resolve its plan. It is internal — the catalog seam is not yet public API.

`DataFrame` carries its owning session so an action can reach the executor and catalog:

```csharp
internal DataFrame(LogicalPlan plan)                 // session-free frame
internal DataFrame(SparkSession? session, LogicalPlan plan)
internal SparkSession? Session { get; }
```

Every transformation **propagates** the session so a whole chain remains bound to one session and stays
executable. Single-source transformations (`Select`/`Filter`/`Where`/`WithColumn`/`Sort`/`OrderBy`/
**`Limit`**/**`Distinct`**) pass `Session`; the binary ones pass `Session ?? other.Session` (for
`Union`, whose parameter is `other`) or `Session ?? right.Session` (for `Join`/`CrossJoin`);
`GroupBy`/`Agg` thread it through `RelationalGroupedDataset`. A table-driven guard test
(`EveryTransformation_ThreadsTheSameNonNullSession_AndStaysExecutable`) applies every transformation to
a bound frame and asserts each result keeps the same non-null session and stays executable, so a
future transformation that drops the session (as `Limit`/`Distinct` once did, #412) reddens. A
session-free frame (constructed directly in tests without a session) throws a deterministic
`InvalidOperationException` from an action.

### The default-unsupported backend

`src/DeltaSharp.Core/Execution/UnsupportedQueryExecutor.cs` is the default a session holds until #174
registers a real backend. Every method throws a deterministic public
`QueryExecutionException` (`src/DeltaSharp.Core/Execution/QueryExecutionException.cs`):

> No execution backend is registered, so this DataFrame action cannot run. Reference the
> DeltaSharp.Executor package to enable query execution (collect/count/show); it ships in
> STORY-04.6.2 (#174).

So Core is self-contained: an action fails fast with a clear diagnostic rather than silently returning
empty results.

### The error model

| Stage | Failure | Exception |
| --- | --- | --- |
| Analyze | unknown table/column, unresolved plan | `AnalysisException` (internal; raised before execution) |
| Bind | frame not bound to a session | `InvalidOperationException` |
| Bind | owning session stopped/disposed | `SessionStoppedException` (public) |
| Execute | no backend registered / backend runtime fault | `QueryExecutionException` (public) |

A resolution error is raised during analysis, *before* the executor is reached (so it can never hit
the backend). `QueryExecutionException` is the single public error type for the execution stage and is
also what #174 throws for a backend-reported fault.

## The `Row` model (#177)

`src/DeltaSharp.Core/Session/Row.cs`, namespace `DeltaSharp`, `public sealed class Row`. A `DataFrame`
is conceptually an untyped `Dataset<Row>`, so `Collect` returns `Row`s and `Show` renders them. A row
is **immutable** (values are copied at construction and never exposed as a mutable array) and
**self-describing** (it carries its own `StructType`).

### Public surface (Spark parity)

```csharp
public Row(StructType schema, params object?[] values)
public Row(StructType schema, IReadOnlyList<object?> values)

public StructType Schema { get; }        // Spark: Row.schema
public int Length { get; }               // Spark: Row.length
public int Size { get; }                 // Spark: Row.size (alias)

public object? this[int ordinal] { get; }        // Spark: Row.apply(i)/get(i)
public object? this[string fieldName] { get; }   // by-name (via schema)

public bool IsNullAt(int ordinal)        // Spark: Row.isNullAt
public bool AnyNull { get; }             // Spark: Row.anyNull
public int FieldIndex(string name)       // Spark: Row.fieldIndex (case-sensitive)

public T GetAs<T>(int ordinal)           // Spark: Row.getAs[T](i)
public T GetAs<T>(string fieldName)      // Spark: Row.getAs[T](name)

public override bool Equals(object? obj) // Spark: Row is value-equal (same schema + values)
public override int GetHashCode()        // consistent with Equals (schema + ordered values)
public override string ToString()        // Spark: Row.toString => "[a,null,c]"
```

**Value equality (Spark parity).** `Row` overrides `Equals`/`GetHashCode` for **structural value
equality**: two rows are equal iff their `Schema`s are equal (`StructType` value equality) and every
value is equal in ordinal order (null-aware — two SQL `NULL`s at the same ordinal are equal). This is
the first thing a Spark user does after `collect()`: `Assert.Equal(expectedRow, actualRow)`,
`Contains`, `Distinct`, and `HashSet<Row>` de-duplication all work as expected. `Render` (used by
`ToString`/`Show`) has an `IFormattable`-first switch with a `_ => value.ToString() ?? "null"`
catch-all so any CLR value renders deterministically.

### Type and null semantics (ADR-0008)

- A field's runtime value is the CLR representation of its `StructField.DataType` (`int` for `int`,
  `string` for `string`, …), or `null` for SQL `NULL`.
- **By-name** lookup (`this[string]`, `GetAs<T>(string)`, `FieldIndex`) is **case-sensitive**
  (ordinal), matching Spark and the `StructType` field index.
- `GetAs<T>` on a **null** value returns `default(T)` when `T` is a reference type or `Nullable<U>`,
  and **throws** `InvalidOperationException` when `T` is a non-nullable value type — deterministic and
  documented, so a null typed-getter never silently yields `0`/`false`.

### Deterministic error model (#177 AC3)

| Access | Condition | Exception |
| --- | --- | --- |
| `this[int]`, `IsNullAt`, `GetAs<T>(int)` | ordinal `< 0` or `≥ Length` | `ArgumentOutOfRangeException` |
| `this[string]`, `GetAs<T>(string)`, `FieldIndex` | no such field | `ArgumentException` |
| `FieldIndex(null)` | null name | `ArgumentNullException` |
| `GetAs<T>` | value present but not assignable to `T` | `InvalidCastException` |
| `GetAs<T>` | value null, `T` non-nullable value type | `InvalidOperationException` |
| constructor | value count ≠ schema field count | `ArgumentException` |
| constructor | null schema/values | `ArgumentNullException` |

### Value equality (#412 council)

| Behavior | Test(s) |
| --- | --- |
| schema- and value-equal rows compare equal + hash equal | `Equals_And_GetHashCode_AreValueBased`, `Equals_NullAwareAcrossOrdinals` |
| differing value / nullness / schema compare unequal | `Equals_DifferentValue_IsNotEqual`, `Equals_DifferentNullness_IsNotEqual`, `Equals_DifferentSchema_IsNotEqual` |
| `Assert.Equal(new[]{row}, collected)` and `HashSet<Row>` de-dup | `Equals_AgainstCollectedArray_Works`, `HashSet_DedupsValueEqualRows` |

### The #174 materialization seam

#174 (physical planning, `DeltaSharp.Executor`) is the producer of `Row`s: it materializes engine
`ColumnBatch`/`ColumnVector` results into `Row(schema, values)`. It codes against the **exact** shapes
above — `IQueryExecutor.Collect` returns `IReadOnlyList<Row>`, each `Row` built with the analyzed
plan's output schema and the ADR-0008 CLR values. This document is the contract #174 implements.

## AC → test map

Tests live in `tests/DeltaSharp.Core.Tests/Actions/` (`DataFrameActionTests`, `RowTests`,
`FakeQueryExecutor`) and reuse the #169 doubles in `tests/DeltaSharp.Core.Tests/LazyEager/`
(`RecordingAudit`).

### STORY-04.6.1 (#173)

| AC | Test(s) |
| --- | --- |
| AC1 — `Collect` invokes analyzer + planner/backend exactly once (the optimizer is an intentional identity pass in M1, not an audited stage) | `Collect_ReturnsExecutorRows_AndInvokesBackendOnce`, `Collect_PassesAnAnalyzedPlanToTheExecutor`, `Collect_RecordsExactlyOneAnalyzerStage_AndTheFullBackendPath` |
| AC2 — `Count` matches Spark semantics | `Count_ReturnsExecutorCount_WithoutMaterializing`, `Count_RecordsExactlyOneAnalyzerStagePerAction` |
| AC3 — `Show` respects row limits/truncation without changing the plan; empty result still renders headers | `ShowString_RendersSparkStyleTable_WithTruncationFooter`, `ShowString_NoFooter_WhenResultFitsWithinNumRows`, `ShowString_TruncateFalse_LeftJustifiesAndKeepsFullValue`, `ShowString_Truncate_CutsLongCellsWithEllipsis`, `ShowString_NegativeNumRows_Throws`, `Show_DoesNotChangeTheUnderlyingPlan`, `ShowString_OnEmptyResult_StillRendersColumnHeaders` |
| AC4 — no action steps occur for a transformation chain | `TransformationChain_TriggersNoExecution` |
| (seam) default-unsupported backend + injection | `Collect_WithNoBackendRegistered_ThrowsClearDiagnostic`, `Count_WithNoBackendRegistered_ThrowsClearDiagnostic`, `Action_OnSessionFreeFrame_ThrowsInvalidOperation`, `RegisterQueryExecutorFactory_InstallsBackendForNewSessions` |
| (#412) transformations thread the session; actions reject a stopped session | `Limit_ThreadsSession_SoAFollowingActionRuns`, `Distinct_ThreadsSession_SoCountRuns`, `SelectThenLimit_ThreadsSession_SoShowRuns`, `EveryTransformation_ThreadsTheSameNonNullSession_AndStaysExecutable`, `Collect_OnStoppedSession_ThrowsSessionStopped` |

### STORY-04.7.1 (#177)

| AC | Test(s) |
| --- | --- |
| AC1 — names, ordinal access, typed getters, null checks | `Schema_And_Length_ReflectFields`, `OrdinalIndexer_ReturnsValues`, `NameIndexer_ResolvesViaSchema`, `FieldIndex_ReturnsOrdinal_AndIsCaseSensitive`, `GetAs_ByOrdinalAndName_ReturnsTypedValue`, `IsNullAt_And_AnyNull_TrackNulls` |
| AC2 — preserve type, nullability, ANSI semantics | `Constructor_CopiesValues_SoRowIsImmutable`, `IReadOnlyListConstructor_MatchesParamsConstructor`, `GetAs_NullValue_NullableValueType_ReturnsNull`, `ToString_UsesInvariantCulture_ForNumbers` |
| AC3 — deterministic errors for missing fields, bad casts, null typed getters | `Constructor_RejectsNullSchemaAndValues`, `Constructor_RejectsValueCountMismatch`, `OrdinalIndexer_OutOfRange_Throws`, `NameIndexer_MissingField_Throws`, `FieldIndex_NullName_Throws`, `IsNullAt_OutOfRange_Throws`, `GetAs_WrongType_ThrowsInvalidCast`, `GetAs_ByOrdinal_OutOfRange_Throws`, `GetAs_ByName_MissingField_Throws`, `GetAs_NullValue_ReferenceType_ReturnsNull`, `GetAs_NullValue_NonNullableValueType_Throws` |
| AC4 — `Show` rendering stable with nulls | `ShowString_RendersNullAsLiteral`, `ToString_RendersBracketedCsv_WithNullAsLiteral` |

## Deferred (M1)

The following are intentionally out of scope for M1 and tracked as open issues; the seams above are
shaped so each lands without reshaping the action pipeline:

- **Optimizer wiring** — the standalone rule-based optimizer (#172) is merged but **not** wired into
  the action `Optimize` seam; wiring is deferred to **#174** and gated on
  [#415](https://github.com/khaines/deltasharp/issues/415) (engine `And`/`Or` per-lane short-circuit
  for ANSI filter safety once filters are combined).
- **[#416](https://github.com/khaines/deltasharp/issues/416)** — an `IQueryExecutor` seam
  `CancellationToken` and result/resource bounds (a `collect()` of an unbounded result is unbounded in
  M1).
- **[#417](https://github.com/khaines/deltasharp/issues/417)** — `DataFrame` analyzed-plan memoization
  (each action re-analyzes from scratch today).
- **[#418](https://github.com/khaines/deltasharp/issues/418)** — the `Row`/`Show` Spark-parity backlog:
  typed getters (`getInt`/`getString`/…), `toSeq`/`mkString`, complex-type getters, a `Show`
  truncate-width overload, and CJK display-width handling.
