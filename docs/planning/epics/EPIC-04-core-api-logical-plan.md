# EPIC-04: Core API & Logical Plan

- **Roadmap milestone:** [M1](../../../ROADMAP.md)
- **Primary persona(s):** `developer-experience-api-engineer`, `query-execution-engine-engineer`
- **Related ADRs:** ADR-0008, ADR-0001
- **Depends on:** EPIC-02, EPIC-03
- **Status:** draft
- **Size:** L

## Objective

Deliver DeltaSharp's first Spark-parity user surface for building DataFrame and `Dataset<T>` pipelines, inspecting logical plans, and running local single-node actions. This epic establishes the non-negotiable lazy/eager invariant: transformations only build immutable unresolved logical plans, while actions trigger analyzer, planning, and execution through the EPIC-03 backend. It also gives M1 an end-to-end local path over in-memory and Parquet data so API parity can be validated against real results.

## Scope In/Out

**In scope**
- Spark-compatible `SparkSession`, `DataFrame`, `Dataset<T>`, `Column`, functions, reader, SQL entry point, and action names for the v1 subset.
- Immutable unresolved logical plan nodes for scans, projections, filters, aggregates, joins, sorts, limits, distinct, union, writes, and explain.
- Local analyzer resolution against an in-memory/local catalog, schemas, functions, and ADR-0008 type/null semantics.
- Local single-node execution driver that invokes the EPIC-03 execution backend only from actions.
- Row materialization, encoder contracts for `Dataset<T>`, and `EXPLAIN` rendering for logical, analyzed/optimized, and physical stages.
- Public API parity matrices, XML docs, examples, and documented deviations for the M1 subset.

**Out of scope** (and where it lives instead)
- Distributed execution, shuffle services, executor pods, and Kubernetes lifecycle → EPIC-08 / persona `dotnet-distributed-execution-engineer`.
- Delta transaction log semantics, ACID commits, and full Delta table write durability → EPIC-05 / persona `delta-storage-format-engineer`.
- External catalog/metastore integrations beyond the local catalog contract → EPIC-06 / persona `catalog-metastore-engineer`.
- Full SQL grammar and ANTLR frontend beyond the `SparkSession.Sql` handoff surface → EPIC-07 / persona `sql-language-frontend-engineer`.
- Cost-based optimization, AQE, and scheduler policies → EPIC-11 / persona `query-optimizer-scheduler-engineer`.

## Exit criteria

- [ ] A user can create a `SparkSession`, read in-memory and Parquet data, chain DataFrame transformations, and run `collect`, `count`, `show`, or `write` locally end-to-end.
- [ ] Transformations including `select`, `filter`, `where`, `groupBy`, `agg`, `join`, `withColumn`, `orderBy`, `limit`, `distinct`, and `union` are proven by tests to build plans without scanning, scheduling, or materializing data.
- [ ] Actions are proven by tests to be the only public API calls that invoke analyzer, physical planning, and the EPIC-03 execution backend.
- [ ] Public API names, overload shapes, null/type semantics, and error behavior match Spark for the v1 subset, with every intentional deviation documented in a parity matrix.
- [ ] Logical plan nodes are immutable and analyzer/optimizer steps return new trees instead of mutating existing trees.
- [ ] `EXPLAIN` renders unresolved logical, analyzed/optimized logical, and physical plans with enough detail to identify scans, filters, projections, joins, aggregates, sorts, limits, and write actions.
- [ ] `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass for all projects that implement this epic.

## Features

### FEAT-04.1: SparkSession & entry point

- **Objective:** Provide the Spark-compatible application entry point, builder/config lifecycle, and public doors for `Read` and `Sql` while keeping session creation separate from execution.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Depends on:** EPIC-02, EPIC-03.

#### Stories

##### STORY-04.1.1: SparkSession builder and lifecycle

- **As a** Spark user **I want** a familiar `SparkSession.Builder` with app name, config, `GetOrCreate`, and stop/dispose lifecycle **so that** DeltaSharp applications start like Spark applications.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** EPIC-02.
- **Acceptance criteria:**
  - [ ] Given a builder with app name and key/value config, When `GetOrCreate` is called, Then a usable `SparkSession` exposes the configured values without executing a query.
  - [ ] Given an existing active session, When `GetOrCreate` is called with equivalent config, Then the same active session is returned according to documented lifecycle rules.
  - [ ] Given a stopped or disposed session, When a user calls `Read`, `Sql`, or creates a DataFrame, Then a deterministic public error explains the invalid lifecycle state.
  - [ ] Given a session config for the execution backend, When the session is created, Then backend selection is recorded for later action execution without initializing work during transformations.
- **Definition of done:** builds/tests/format pass; checklists `15`, `20`, `03a`, `04a`, `21` satisfied; XML docs and lifecycle examples updated.

##### STORY-04.1.2: Read door and DataFrame creation from local inputs

- **As a** developer **I want** `spark.Read` and simple in-memory DataFrame creation **so that** I can build M1 pipelines from local data sources.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** STORY-04.1.1, EPIC-02.
- **Acceptance criteria:**
  - [ ] Given an in-memory sequence with an explicit schema, When `CreateDataFrame` is called, Then a DataFrame is returned with a scan logical plan and no rows are materialized by the call.
  - [ ] Given a local Parquet path and reader options, When `spark.Read.Parquet(path)` is called, Then a DataFrame containing an unresolved Parquet scan plan is returned without opening files until an action.
  - [ ] Given unsupported reader options, When the reader is finalized, Then a Spark-parity diagnostic names the unsupported option and the documented alternative.
  - [ ] Given a DataFrame from either source, When `Explain` is called, Then the source appears as a scan node in the logical plan output.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `19`, `20`, `03a`, `04a`, `21` satisfied; reader examples updated.

##### STORY-04.1.3: Sql door into shared plan pipeline

- **As a** Spark user **I want** `SparkSession.Sql` to return a DataFrame through the same planning pipeline **so that** SQL and DataFrame APIs converge after parsing/lowering.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`, `sql-language-frontend-engineer`.
- **Size:** S. **Depends on:** STORY-04.1.1, FEAT-04.4.
- **Acceptance criteria:**
  - [ ] Given a supported M1 SQL string, When `spark.Sql(sql)` is called, Then it returns a DataFrame backed by an unresolved logical plan without executing.
  - [ ] Given SQL parsing is not yet available for a construct, When `spark.Sql` receives it, Then a deterministic unsupported-feature error is returned without invoking execution.
  - [ ] Given equivalent SQL and DataFrame expressions, When their logical plans are inspected after lowering, Then shared node types are used for common operations.
  - [ ] Given an invalid lifecycle state, When `Sql` is called, Then the same session lifecycle error model as `Read` is used.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `20`, `03a`, `04a`, `21` satisfied; SQL handoff notes documented.

### FEAT-04.2: DataFrame and Dataset<T> transformation API surface

- **Objective:** Implement the M1 Spark-compatible DataFrame and `Dataset<T>` transformation surface as lazy plan builders only.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Depends on:** FEAT-04.1, FEAT-04.3, FEAT-04.4.

#### Stories

##### STORY-04.2.1: Projection, filtering, and derived columns

- **As a** Spark user **I want** `select`, `filter`, `where`, and `withColumn` **so that** common DataFrame projections and predicates port directly.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** FEAT-04.3, FEAT-04.4.
- **Acceptance criteria:**
  - [ ] Given a DataFrame, When `Select`/`select` is called with columns or expressions, Then a new DataFrame with a projection logical plan is returned and the original DataFrame remains unchanged.
  - [ ] Given a predicate column, When `Filter`/`Where` is called, Then a filter logical plan is appended without evaluating the predicate.
  - [ ] Given a derived expression, When `WithColumn` is called, Then the plan includes the new or replaced column expression according to Spark-compatible naming rules.
  - [ ] Given a test input whose scan throws if read, When these transformations are chained, Then no scan or backend call occurs.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `20`, `03a`, `04a`, `21` satisfied; parity matrix updated.

##### STORY-04.2.2: Aggregation and grouping API

- **As a** Spark user **I want** `groupBy` and `agg` **so that** aggregation pipelines use familiar Spark shapes.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** STORY-04.2.1, FEAT-04.3, FEAT-04.4.
- **Acceptance criteria:**
  - [ ] Given one or more grouping columns, When `GroupBy`/`groupBy` is called, Then a grouped DataFrame object records grouping expressions without executing.
  - [ ] Given aggregate functions such as `count`, `sum`, `avg`, `min`, and `max`, When `Agg`/`agg` is called, Then an aggregate logical plan is produced with Spark-compatible aliases.
  - [ ] Given invalid aggregate input, When the plan is analyzed, Then the analyzer reports a deterministic error rather than the API executing eagerly.
  - [ ] Given equivalent Spark examples in the M1 parity matrix, When ported to DeltaSharp, Then method names and chaining order remain recognizable.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `20`, `03a`, `04a`, `21` satisfied; aggregation examples updated.

##### STORY-04.2.3: Joins, ordering, limits, distinct, and union

- **As a** Spark user **I want** joins and relational transformations **so that** common batch pipelines can be expressed before execution.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-04.2.1, FEAT-04.3, FEAT-04.4.
- **Acceptance criteria:**
  - [ ] Given two DataFrames and a join expression, When `Join`/`join` is called, Then a join logical plan records join type, condition, and child plans without reading either side.
  - [ ] Given `OrderBy`/`orderBy`, `Limit`/`limit`, `Distinct`/`distinct`, or `Union`/`union`, When called, Then each returns a new DataFrame with the corresponding immutable logical plan node.
  - [ ] Given unsupported join types or incompatible union schemas, When analyzed, Then Spark-parity diagnostics identify the unsupported or incompatible condition.
  - [ ] Given chained relational transformations, When the plan is inspected, Then node order preserves user intent and contains no physical execution artifacts.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `20`, `03a`, `04a`, `21` satisfied; relational API parity tests added.

##### STORY-04.2.4: Dataset<T> typed transformation bridge

- **As a** C# developer **I want** `Dataset<T>` to provide typed access while sharing DataFrame semantics **so that** strongly typed code does not fork the engine pipeline.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** FEAT-04.2, FEAT-04.7.
- **Acceptance criteria:**
  - [ ] Given a `Dataset<T>`, When a supported typed projection or filter is expressed, Then it lowers to the same logical plan model used by DataFrame operations.
  - [ ] Given a `Dataset<T>` conversion to DataFrame, When `ToDF` or equivalent is called, Then schema and plan identity are preserved without materialization.
  - [ ] Given nullable properties on `T`, When the encoder derives schema metadata, Then ADR-0008 nullability semantics are represented in the schema.
  - [ ] Given unsupported typed expressions, When lowering is attempted, Then the user receives a deterministic unsupported-expression diagnostic.
- **Definition of done:** builds/tests/format pass; checklists `15`, `20`, `03a`, `04a`, `21` satisfied; typed API docs updated.

### FEAT-04.3: Column and functions library

- **Objective:** Provide Spark-compatible `Column` expressions, operators, literals, and common functions that build expression trees for analysis and execution.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Depends on:** EPIC-02, FEAT-04.4.

#### Stories

##### STORY-04.3.1: Column references, aliases, and literals

- **As a** Spark user **I want** column references, aliases, and literals **so that** expressions are portable from Spark examples.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** S. **Depends on:** EPIC-02, FEAT-04.4.
- **Acceptance criteria:**
  - [ ] Given a column name, When a user calls `Col`, `Column`, or the documented alias, Then an unresolved attribute expression is created without schema lookup.
  - [ ] Given a scalar literal including null, decimal, date, timestamp, array, map, or struct where supported, When `Lit` is called, Then the expression records an ADR-0008-compatible data type.
  - [ ] Given an alias expression, When it appears in `Select` or `Agg`, Then the alias is preserved in the logical plan for analyzer resolution.
  - [ ] Given invalid literal types, When `Lit` is called, Then a deterministic public error states the unsupported .NET type.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `20`, `03a`, `04a`, `21` satisfied; function docs include lazy expression behavior.

##### STORY-04.3.2: Operators and boolean/null semantics

- **As a** Spark user **I want** arithmetic, comparison, boolean, and null-safe operators **so that** expressions follow Spark SQL semantics.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** STORY-04.3.1, EPIC-02.
- **Acceptance criteria:**
  - [ ] Given arithmetic and comparison operators over columns and literals, When expressions are built, Then expression nodes record operator kind and operands without evaluation.
  - [ ] Given boolean `And`, `Or`, and `Not` expressions, When analyzed and executed later, Then SQL three-valued logic from ADR-0008 is the required semantic contract.
  - [ ] Given null checks and null-safe equality, When expressions are inspected, Then distinct expression node kinds preserve Spark-compatible null behavior.
  - [ ] Given operator misuse such as a boolean expression in an arithmetic context, When analyzed, Then type errors are reported by the analyzer rather than hidden by API coercion.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `20`, `03a`, `04a`, `21` satisfied; null semantics tests added.

##### STORY-04.3.3: Common functions registry surface

- **As a** Spark user **I want** common functions such as `count`, `sum`, `avg`, `min`, `max`, `expr`, `when`, `coalesce`, and string/date helpers **so that** M1 pipelines can be expressed with Spark vocabulary.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-04.3.1, STORY-04.3.2, FEAT-04.5.
- **Acceptance criteria:**
  - [ ] Given each M1 function in the parity matrix, When called from the public functions library, Then it produces a named unresolved function expression with documented arguments.
  - [ ] Given functions with aggregate semantics, When used inside `Agg`, Then analyzer binding distinguishes aggregate from scalar functions.
  - [ ] Given unsupported Spark functions, When a user attempts to call them through the documented surface, Then the API or analyzer reports a documented unsupported-feature diagnostic.
  - [ ] Given function XML docs, When reviewed, Then each function states whether it builds a lazy expression and whether Spark behavior deviates.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `20`, `03a`, `04a`, `21` satisfied; function parity matrix updated.

### FEAT-04.4: Immutable logical plan model

- **Objective:** Define the unresolved logical plan and expression IR that all API and SQL entry points use before analysis, preserving immutability and layer separation.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `developer-experience-api-engineer`.
- **Depends on:** EPIC-02, EPIC-03.

#### Stories

##### STORY-04.4.1: Core logical plan nodes and invariants

- **Implement** immutable plan nodes for scans, projection, filter, aggregate, join, sort, limit, distinct, union, and write intent so every transformation records intent without execution.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `developer-experience-api-engineer`.
- **Size:** L. **Depends on:** EPIC-02.
- **Acceptance criteria:**
  - [ ] Given each M1 transformation, When it constructs a plan, Then the resulting node and its children are immutable after construction.
  - [ ] Given a transformation on an existing DataFrame, When the new DataFrame is produced, Then the original DataFrame's plan reference and content are unchanged.
  - [ ] Given plan nodes for scans and writes, When inspected, Then they contain logical source/sink descriptors only and no open readers, writers, tasks, or backend handles.
  - [ ] Given serialization or debug rendering of a plan, When run before analysis, Then unresolved attributes and functions remain explicitly unresolved.
- **Definition of done:** builds/tests/format pass; checklists `16`, `03a`, `04a`, `21` satisfied; plan-node invariants documented.

##### STORY-04.4.2: Expression tree model

- **Implement** immutable expression nodes for attributes, aliases, literals, casts, functions, operators, sort orders, aggregate expressions, and unresolved stars.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `developer-experience-api-engineer`.
- **Size:** M. **Depends on:** STORY-04.4.1, EPIC-02.
- **Acceptance criteria:**
  - [ ] Given column and function API calls, When expressions are created, Then expression nodes preserve source name, argument order, nullability hints, and unresolved status.
  - [ ] Given nested expressions, When an analyzer rule rewrites a child, Then a new parent expression tree is returned without mutating the original.
  - [ ] Given literals and casts, When types are represented, Then they use the ADR-0008 type-system model.
  - [ ] Given a debug renderer, When it prints expressions, Then aliases, unresolved attributes, functions, and literals are distinguishable.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `03a`, `04a`, `21` satisfied; expression invariants documented.

##### STORY-04.4.3: Plan construction audit hooks for lazy/eager verification

- **As a** reviewer **I want** tests and audit hooks that distinguish plan construction from execution **so that** lazy/eager regressions are caught early.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `developer-experience-api-engineer`.
- **Size:** S. **Depends on:** STORY-04.4.1, STORY-04.4.2.
- **Acceptance criteria:**
  - [ ] Given a fake source that records file opens or row reads, When only transformations are called, Then counters remain zero.
  - [ ] Given a fake execution backend, When only transformations are called, Then no backend method is invoked.
  - [ ] Given an action, When execution starts, Then exactly the expected analyzer/planner/backend invocation path is observable.
  - [ ] Given CI tests, When a transformation accidentally invokes execution, Then at least one lazy/eager regression test fails.
- **Definition of done:** builds/tests/format pass; checklists `16`, `03a`, `04a`, `21` satisfied; lazy/eager regression tests added.

### FEAT-04.5: Local analyzer and resolution

- **Objective:** Resolve unresolved plans against local catalog metadata, schemas, function registry, and ADR-0008 type rules before physical planning.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `developer-experience-api-engineer`, `catalog-metastore-engineer`.
- **Depends on:** FEAT-04.3, FEAT-04.4.

#### Stories

##### STORY-04.5.1: Local catalog and schema resolution

- **As a** query planner **I want** a local catalog and schema resolver **so that** DataFrame and SQL plans can bind table and column references consistently.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `catalog-metastore-engineer`, `developer-experience-api-engineer`.
- **Size:** M. **Depends on:** FEAT-04.4, EPIC-02.
- **Acceptance criteria:**
  - [ ] Given an unresolved scan over a registered in-memory or Parquet source, When analysis runs, Then the scan is resolved to a schema with ADR-0008 data types.
  - [ ] Given valid attribute references, When analysis runs, Then they are replaced with resolved attributes containing stable IDs, names, types, and nullability.
  - [ ] Given missing or ambiguous columns, When analysis runs, Then Spark-compatible diagnostics identify the failing reference and candidate columns.
  - [ ] Given catalog lookup failures, When analysis runs, Then no physical planning or execution backend call is attempted.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `19`, `03a`, `04a`, `21` satisfied; analyzer diagnostics documented.

##### STORY-04.5.2: Function binding and type coercion

- **As a** query planner **I want** function binding and type coercion rules **so that** expressions follow Spark SQL behavior for the M1 subset.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `developer-experience-api-engineer`.
- **Size:** L. **Depends on:** STORY-04.5.1, FEAT-04.3.
- **Acceptance criteria:**
  - [ ] Given scalar and aggregate unresolved function expressions, When analysis runs, Then each supported function binds to a typed expression contract.
  - [ ] Given numeric, string, date/timestamp, decimal, and null inputs in supported combinations, When analysis runs, Then Spark-compatible coercion and ANSI overflow/null semantics from ADR-0008 are applied.
  - [ ] Given unsupported coercions or wrong arity, When analysis runs, Then diagnostics name the function, supplied types, expected forms, and Spark-parity status.
  - [ ] Given aggregate functions outside valid aggregate contexts, When analysis runs, Then the analyzer rejects the plan before physical planning.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `03a`, `04a`, `21` satisfied; function binding tests added.

##### STORY-04.5.3: Minimal logical optimization rules for local execution

- **As a** query planner **I want** a small rule-based optimization pass **so that** M1 physical plans receive clean analyzed input without changing semantics.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `query-optimizer-scheduler-engineer`.
- **Size:** M. **Depends on:** STORY-04.5.1, STORY-04.5.2.
- **Acceptance criteria:**
  - [ ] Given analyzed plans, When optimization runs, Then supported rules such as projection pruning, filter combination, constant folding, and limit pushdown return new immutable plan trees.
  - [ ] Given a rule precondition is not met, When optimization runs, Then the original subtree is preserved semantically.
  - [ ] Given a plan before and after optimization, When `EXPLAIN` is requested, Then both stages can be rendered separately.
  - [ ] Given optimizer tests, When a rule is applied, Then expected output schemas and results remain equivalent to the unoptimized analyzed plan.
- **Definition of done:** builds/tests/format pass; checklists `16`, `03a`, `04a`, `21` satisfied; optimization rule notes documented.

### FEAT-04.6: Actions and local single-node execution driver

- **Objective:** Implement eager actions and a local driver that triggers analysis, physical planning, and EPIC-03 backend execution for M1 pipelines.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`, `developer-experience-api-engineer`.
- **Depends on:** FEAT-04.2, FEAT-04.5, EPIC-03.

#### Stories

##### STORY-04.6.1: Collect, count, and show actions

- **As a** Spark user **I want** `collect`, `count`, and `show` **so that** I can trigger local execution and inspect results with familiar actions.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `developer-experience-api-engineer`, `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** L. **Depends on:** FEAT-04.5, EPIC-03.
- **Acceptance criteria:**
  - [ ] Given a valid DataFrame plan, When `Collect`/`collect` is called, Then analyzer, optimizer, physical planner, and the EPIC-03 backend are invoked exactly once for the action.
  - [ ] Given a valid DataFrame plan, When `Count`/`count` is called, Then the result matches Spark semantics for nulls and filters in the supported M1 subset.
  - [ ] Given a valid DataFrame plan, When `Show`/`show` is called, Then formatted output respects row limits and truncation options without changing the underlying plan.
  - [ ] Given any transformation chain without an action, When observed with the same instrumentation, Then none of the action execution steps occur.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `20`, `03a`, `04a`, `21` satisfied; action docs clearly mark eager behavior.

##### STORY-04.6.2: Physical planning bridge to EPIC-03 backend

- **Implement** the local physical planning bridge from optimized plans to executable EPIC-03 operators for scans, filters, projections, aggregates, joins, sorts, limits, distinct, and union.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** L. **Depends on:** STORY-04.6.1, EPIC-03.
- **Acceptance criteria:**
  - [ ] Given an optimized logical plan, When physical planning runs, Then each supported node maps to an EPIC-03 executable operator or a deterministic unsupported-plan diagnostic.
  - [ ] Given backend selection from `SparkSession` config, When execution starts, Then the default interpreted vectorized backend is used unless a supported ADR-0001 override applies.
  - [ ] Given local Parquet and in-memory scans, When physical plans are executed, Then input is read only during the action and released after action completion.
  - [ ] Given equivalent interpreted and optional compiled backend results where supported, When parity tests run, Then outputs match for the M1 subset.
- **Definition of done:** builds/tests/format pass; checklists `16`, `03a`, `04a`, `21` satisfied; backend bridge tests added.

##### STORY-04.6.3: Write action trigger and local sink contract

- **As a** Spark user **I want** `write` to behave as an action **so that** saving results triggers execution through the plan pipeline rather than API shortcuts.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `developer-experience-api-engineer`, `delta-storage-format-engineer`.
- **Size:** M. **Depends on:** STORY-04.6.2.
- **Acceptance criteria:**
  - [ ] Given a DataFrame writer configured for a supported local sink, When `Save` is called, Then a write logical intent is analyzed, planned, and executed as an eager action.
  - [ ] Given writer configuration before `Save`, When options, mode, format, or path are set, Then each call is lazy and only updates writer intent.
  - [ ] Given unsupported write formats or modes, When `Save` is called, Then a deterministic diagnostic is produced before partial output is committed.
  - [ ] Given Delta-specific write requirements, When they exceed M1 scope, Then the API routes ownership to EPIC-05 contracts without implementing transaction-log semantics here.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `17`, `20`, `03a`, `04a`, `21` satisfied; writer eager/lazy docs updated.

##### STORY-04.6.4: Local execution error, cancellation, and resource boundaries

- **As an** operator of local jobs **I want** bounded action execution with clear errors and cancellation **so that** failed M1 queries are safe and diagnosable.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `cloud-native-site-reliability-engineer`.
- **Size:** M. **Depends on:** STORY-04.6.2.
- **Acceptance criteria:**
  - [ ] Given a cancellation token or configured timeout, When an action is running, Then execution stops and releases local resources deterministically.
  - [ ] Given analyzer, planner, scan, or backend failures, When an action fails, Then the public exception identifies the failed stage and preserves the root cause.
  - [ ] Given configured local memory or row limits, When an action would exceed them, Then execution fails safely before unbounded materialization.
  - [ ] Given metrics for local action execution, When an action completes or fails, Then planning and execution counters are available for diagnostics.
- **Definition of done:** builds/tests/format pass; checklists `16`, `03a`, `04a`, `09a`, `09b`, `21` satisfied; local failure-mode tests added.

### FEAT-04.7: Row, encoders, and EXPLAIN

- **Objective:** Provide user-facing row materialization, typed encoders, and plan explanation across logical, optimized, and physical stages.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Depends on:** FEAT-04.4, FEAT-04.5, FEAT-04.6, EPIC-02.

#### Stories

##### STORY-04.7.1: Row and schema materialization

- **As a** Spark user **I want** collected results as `Row` values with schemas **so that** action results preserve Spark-compatible field access and null behavior.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** FEAT-04.6, EPIC-02.
- **Acceptance criteria:**
  - [ ] Given a collected DataFrame, When rows are materialized, Then field names, ordinal access, typed getters, and null checks follow the documented Spark-parity contract.
  - [ ] Given ADR-0008 primitive and supported complex types, When represented in `Row`, Then values preserve type, nullability, and ANSI semantics.
  - [ ] Given missing fields, invalid casts, or null typed getters, When accessed, Then deterministic public errors match the documented error model.
  - [ ] Given `Show` output, When rows contain nulls or nested values, Then rendering is stable and compatible with the M1 formatting contract.
- **Definition of done:** builds/tests/format pass; checklists `15`, `20`, `03a`, `04a`, `21` satisfied; row API docs updated.

##### STORY-04.7.2: Encoders for Dataset<T>

- **As a** C# developer **I want** encoders between `Row` and `Dataset<T>` records **so that** typed datasets can execute locally without bypassing Spark semantics.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`, `dotnet-runtime-performance-engineer`.
- **Size:** L. **Depends on:** STORY-04.7.1, STORY-04.2.4.
- **Acceptance criteria:**
  - [ ] Given a supported POCO or record type, When an encoder is derived, Then schema names, types, nullability, and property mappings are deterministic.
  - [ ] Given collected rows for a `Dataset<T>`, When decoding occurs, Then values match ADR-0008 type/null semantics and do not mutate the underlying row representation.
  - [ ] Given unsupported property types or ambiguous mappings, When an encoder is derived, Then a deterministic diagnostic identifies the member and unsupported type.
  - [ ] Given NativeAOT constraints, When encoder implementation is reviewed, Then any dynamic-code dependency is absent or explicitly guarded according to ADR-0001.
- **Definition of done:** builds/tests/format pass; checklists `15`, `20`, `03a`, `04a`, `21` satisfied; encoder examples updated.

##### STORY-04.7.3: EXPLAIN for logical, optimized, and physical plans

- **As a** user debugging a query **I want** `EXPLAIN` to show each plan stage **so that** I can understand what DeltaSharp will execute before or during actions.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** FEAT-04.4, FEAT-04.5, FEAT-04.6.
- **Acceptance criteria:**
  - [ ] Given a DataFrame before execution, When `Explain` is called in logical mode, Then unresolved logical plan nodes and expressions are rendered without triggering execution.
  - [ ] Given analyzer and optimizer availability, When `Explain` is called in extended mode, Then analyzed/optimized logical plans are rendered separately from the unresolved plan.
  - [ ] Given a plan that can be physically planned, When `Explain` includes physical mode, Then scans, filters, projections, joins, aggregates, sorts, limits, and write nodes are visible.
  - [ ] Given unsupported or unresolved constructs, When `Explain` is requested, Then the output includes the unresolved or unsupported status without hiding diagnostics.
  - [ ] Given action execution, When metrics are available after the action, Then explain output can include physical execution metadata without changing query results.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `20`, `03a`, `04a`, `21` satisfied; EXPLAIN format docs and samples updated.

## Open questions

- Should DeltaSharp expose both Spark-style lower-case aliases and .NET PascalCase methods for every M1 API, or only for the highest-value migration paths?
- Which exact Parquet read/write capabilities are required in M1 before EPIC-05 completes full Delta storage semantics?
- What is the minimum supported SQL subset for `SparkSession.Sql` in M1 versus the broader EPIC-07 SQL frontend?
- Which Spark functions must be included in the first parity matrix to declare M1 Core API usable for representative workloads?
- Should `EXPLAIN` output mirror Spark text formatting exactly, or use a DeltaSharp-stable format with Spark-compatible sections and documented deviations?
