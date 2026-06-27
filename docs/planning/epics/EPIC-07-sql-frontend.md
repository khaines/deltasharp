# EPIC-07: SQL Frontend

- **Roadmap milestone:** M2 ([Milestone 2 — Storage & SQL](../../../ROADMAP.md#milestone-2--storage--sql-v0x))
- **Primary persona(s):** `sql-language-frontend-engineer` (+ collaborators `query-execution-engine-engineer`, `catalog-metastore-engineer`, `developer-experience-api-engineer`)
- **Related ADRs:** ADR-0007, ADR-0005
- **Depends on:** EPIC-04, EPIC-06
- **Status:** draft
- **Size:** XL

## Objective

Deliver the ADR-0007 SQL text-to-plan boundary for DeltaSharp: an ANTLR4 C# SQL frontend that mirrors Spark's `SqlBase.g4`, runs in ANSI mode, and lowers supported Spark SQL into logical plans. This epic makes SQL and DataFrame/Dataset entry points converge on the same resolved plan pipeline by adding parser, analyzer, catalog-backed resolution, function binding, diagnostics, and `EXPLAIN` support.

## Scope

**In scope**
- ANTLR4 C# lexer/parser generation and build integration for a Spark `SqlBase.g4`-shaped core dialect.
- Parser lowering from SQL parse trees to unresolved logical plans for v1 query statements, DDL/DML catalog commands, expressions, CTEs, subqueries, joins, grouping, ordering, limits, and set operations.
- Analyzer rules for identifier, attribute, alias, relation, view, type, function, and catalog-backed resolution in ANSI mode.
- Registry-driven built-in function binding, reserved-word behavior, type coercion, overflow behavior, and three-valued logic for the v1 SQL set.
- Source-positioned parse and analysis diagnostics, unsupported-feature errors, and `EXPLAIN` output for parsed, analyzed, and execution-handoff plan boundaries.
- SQL/DataFrame convergence tests proving equivalent API routes produce semantically equivalent resolved logical plans.

**Out of scope** (and where it lives instead)
- Public SparkSession/DataFrame/Dataset API ergonomics, method overloads, and samples → EPIC-04 / persona `developer-experience-api-engineer`.
- Catalog persistence, native/Hive metastore plugins, and catalog object storage internals → EPIC-06 / persona `catalog-metastore-engineer`.
- Physical planning, optimization, operator execution, shuffle, and distributed runtime → EPIC-03, EPIC-08, and EPIC-11 / personas `query-execution-engine-engineer`, `dotnet-distributed-execution-engineer`, `query-optimizer-scheduler-engineer`.
- Delta transaction log, Parquet storage, and table-maintenance command internals → EPIC-05 / persona `delta-storage-format-engineer`.
- Cost-based optimization, adaptive query execution, statistics policy, and scheduler decisions → EPIC-11 / persona `query-optimizer-scheduler-engineer`.

## Exit criteria

- [ ] The v1 core Spark SQL dialect parses and analyzes in ANSI mode with documented support levels and Spark-aligned deviations.
- [ ] Equivalent SQL and DataFrame/Dataset expressions produce semantically equivalent resolved logical plans at the execution handoff boundary.
- [ ] Function resolution, overload selection, type coercion, null behavior, and overflow handling match Spark for the v1 function set or document intentional deviations.
- [ ] Parse and analysis errors include stable error classes, source positions, actionable messages, and Spark-compatible wording where practical.
- [ ] `EXPLAIN` exposes parsed, unresolved, analyzed, and execution-handoff plan forms with parity fixtures for representative v1 queries.

## Features

### FEAT-07.1: ANTLR4 grammar and C# target integration

- **Objective:** Establish the generated parser foundation by mirroring Spark's `SqlBase.g4` shape for the core dialect and making ANTLR4 C# generation reproducible in the build. The grammar must preserve source spans, ANSI keyword behavior, and unsupported-feature boundaries.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators none.
- **Depends on:** EPIC-04

#### Stories

##### STORY-07.1.1: Add Spark-shaped ANTLR4 grammar scaffold

- **As a** SQL language frontend engineer **I want** a Spark `SqlBase.g4`-shaped grammar scaffold **so that** DeltaSharp can evolve SQL coverage without inventing a divergent dialect.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators none.
- **Size:** M. **Depends on:** EPIC-04
- **Acceptance criteria:**
  - [ ] Given representative Spark SQL for SELECT, FROM, WHERE, GROUP BY, HAVING, ORDER BY, LIMIT, joins, CTEs, subqueries, and set operations, When parsed, Then the grammar accepts supported v1 forms and records source spans.
  - [ ] Given unsupported Spark SQL families outside v1, When parsed, Then the parser fails with explicit unsupported-feature or syntax errors rather than silently accepting weaker semantics.
  - [ ] Given ANSI mode reserved words and quoted identifiers, When tokenized, Then reserved, non-reserved, and delimited identifiers follow the documented v1 keyword matrix.
  - [ ] Given grammar changes, When reviewed, Then the hand-authored grammar diff is sufficient to evaluate Spark parity without inspecting generated code.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16` satisfied; docs updated if public API changes.

##### STORY-07.1.2: Integrate ANTLR4 C# generation into the build

- **As a** SQL language frontend engineer **I want** reproducible ANTLR4 C# generated artifacts **so that** parser builds are deterministic across developer machines and CI.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators none.
- **Size:** S. **Depends on:** STORY-07.1.1
- **Acceptance criteria:**
  - [ ] Given a clean checkout, When `dotnet build` runs, Then ANTLR4 C# parser artifacts are generated or verified by a documented deterministic build step.
  - [ ] Given generated parser output changes, When CI validates the repository, Then stale generated artifacts are detected with an actionable failure.
  - [ ] Given NativeAOT/trimming analysis, When the parser package is referenced, Then generated-code dependencies are documented and do not introduce unintended execution-time dynamic code requirements.
  - [ ] Given cancellation is requested during parsing of a large SQL text, When the parser pipeline observes it, Then parsing stops at a bounded checkpoint with a stable error.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16` satisfied; docs updated if public API changes.

##### STORY-07.1.3: Create grammar parity and negative syntax fixtures

- **As a** SQL language frontend engineer **I want** grammar golden tests and negative syntax fixtures **so that** parser compatibility can grow without regressions.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators none.
- **Size:** M. **Depends on:** STORY-07.1.2
- **Acceptance criteria:**
  - [ ] Given the v1 SQL syntax matrix, When parser tests run, Then each supported syntax family has at least one positive golden parse fixture.
  - [ ] Given malformed statements, ambiguous tokens, and incomplete clauses, When parser tests run, Then each case reports a stable syntax error and source position.
  - [ ] Given unsupported Spark syntax, When parser tests run, Then failures distinguish unsupported features from malformed SQL.
  - [ ] Given future grammar edits, When golden fixtures are updated, Then fixture diffs show the changed parse shape and are reviewable.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16` satisfied; docs updated if public API changes.

### FEAT-07.2: Parser lowering to unresolved logical plans

- **Objective:** Convert SQL parse trees into immutable unresolved logical plans that share EPIC-04 expression and plan nodes with DataFrame/Dataset APIs. Lowering must cover v1 queries and route DDL/DML command shapes toward catalog-aware analysis without doing catalog I/O in the parser.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `query-execution-engine-engineer`.
- **Depends on:** FEAT-07.1, EPIC-04

#### Stories

##### STORY-07.2.1: Lower core SELECT statements and expressions

- **As a** SQL language frontend engineer **I want** SELECT parse trees lowered to unresolved logical plans **so that** SQL queries enter the same lazy plan pipeline as DataFrame/Dataset operations.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** FEAT-07.1, EPIC-04
- **Acceptance criteria:**
  - [ ] Given SELECT, projection aliases, WHERE predicates, GROUP BY, HAVING, ORDER BY, LIMIT, and joins, When lowering runs, Then it produces immutable unresolved logical plan nodes with source spans.
  - [ ] Given literals, casts, arithmetic, comparisons, boolean predicates, CASE, null checks, and function calls, When lowered, Then expressions use shared EPIC-04 expression nodes rather than parser-specific objects.
  - [ ] Given star expansion syntax, multipart identifiers, and nested-field references, When lowered, Then unresolved nodes preserve enough structure for analyzer resolution.
  - [ ] Given parser lowering, When inspected, Then it performs no catalog lookup, table data read, optimization, or execution scheduling.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16`, `20` satisfied; docs updated if public API changes.

##### STORY-07.2.2: Lower CTEs, subqueries, and set operations

- **As a** query frontend engineer **I want** complex query forms lowered consistently **so that** analyzer scoping and downstream planning see one logical representation.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-07.2.1
- **Acceptance criteria:**
  - [ ] Given WITH clauses and nested CTEs, When lowering runs, Then CTE definitions, references, and source spans are represented for analyzer scope handling.
  - [ ] Given scalar, IN, EXISTS, and relation subqueries in supported positions, When lowered, Then subquery expressions and relation nodes are preserved without premature evaluation.
  - [ ] Given UNION, UNION ALL, INTERSECT, and EXCEPT in the v1 set, When lowered, Then set-operation nodes preserve child order, distinctness, and source positions.
  - [ ] Given unsupported correlated or lateral forms, When lowering runs, Then the plan contains a precise unsupported marker or error at the owning clause.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16` satisfied; docs updated if public API changes.

##### STORY-07.2.3: Lower DDL and DML statements to catalog command plans

- **As a** SQL language frontend engineer **I want** DDL and DML syntax lowered into command plans **so that** the analyzer and catalog layer own metadata validation and execution handoff.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `catalog-metastore-engineer`.
- **Size:** M. **Depends on:** FEAT-07.1, EPIC-06
- **Acceptance criteria:**
  - [ ] Given CREATE, ALTER, DROP, DESCRIBE, SHOW, USE, INSERT, and supported table command syntax in the v1 set, When parsed, Then lowering emits unresolved command plans with identifiers, properties, and source spans.
  - [ ] Given catalog identifiers with one, two, or three parts and quoted segments, When lowered, Then identifier parts and quoting are preserved for catalog resolution.
  - [ ] Given statement options and table properties, When lowered, Then names, values, and duplicate-key positions are retained for analyzer validation.
  - [ ] Given unsupported DDL/DML variants, When lowering runs, Then users receive a stable unsupported-operation error before catalog mutation.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16`, `19` satisfied; docs updated if public API changes.

### FEAT-07.3: Analyzer and catalog-backed resolution

- **Objective:** Analyze unresolved SQL plans into resolved, typed, catalog-bound logical plans ready for `query-execution-engine-engineer`. Resolution must be deterministic across scopes, aliases, views, case-sensitivity settings, and catalog snapshots.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `catalog-metastore-engineer`.
- **Depends on:** FEAT-07.2, EPIC-06

#### Stories

##### STORY-07.3.1: Resolve relations, namespaces, views, and identifiers

- **As a** SQL language frontend engineer **I want** catalog-backed relation and identifier resolution **so that** SQL analysis uses Spark-compatible catalog semantics.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `catalog-metastore-engineer`.
- **Size:** L. **Depends on:** STORY-07.2.3, EPIC-06
- **Acceptance criteria:**
  - [ ] Given single-part, multipart, quoted, and case-varied table identifiers, When analysis runs against EPIC-06 catalog fixtures, Then resolution follows the documented current-catalog and current-namespace rules.
  - [ ] Given missing, ambiguous, unauthorized, or unsupported catalog objects, When resolution fails, Then the analyzer emits stable catalog-aware errors with source positions.
  - [ ] Given views in the catalog, When analysis expands them, Then the expanded logical plan preserves view identity, dependency metadata, and source attribution.
  - [ ] Given repeated analysis of the same SQL text and catalog snapshot, When plans are compared, Then resolved identifiers and metadata are deterministic.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16`, `19` satisfied; docs updated if public API changes.

##### STORY-07.3.2: Resolve attributes, aliases, stars, and query scopes

- **As a** SQL language frontend engineer **I want** deterministic attribute and scope resolution **so that** SELECT, joins, CTEs, and subqueries bind columns like Spark.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `catalog-metastore-engineer`.
- **Size:** L. **Depends on:** STORY-07.3.1
- **Acceptance criteria:**
  - [ ] Given projections, aliases, relation aliases, duplicate column names, and self-joins, When analysis runs, Then references bind deterministically or fail with an ambiguous-reference error.
  - [ ] Given CTEs and nested subqueries, When analysis resolves names, Then scope shadowing and outer-reference rules match the documented v1 Spark-parity matrix.
  - [ ] Given `*`, qualified stars, and nested-field access, When analysis runs, Then expansion order, output attributes, and source positions are verifiable.
  - [ ] Given invalid alias usage in WHERE, GROUP BY, HAVING, or ORDER BY, When analysis runs, Then errors or bindings follow the v1 ANSI/Spark rules.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16` satisfied; docs updated if public API changes.

##### STORY-07.3.3: Implement type checking, casting, and resolved-plan handoff invariants

- **As a** query execution collaborator **I want** analyzed SQL plans to be typed and semantically checked **so that** execution receives a stable resolved logical plan contract.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-07.3.2, FEAT-07.4
- **Acceptance criteria:**
  - [ ] Given arithmetic, comparison, boolean, aggregate, sort, set-operation, and join expressions, When analysis completes, Then every expression has a resolved type and nullability.
  - [ ] Given invalid casts, incompatible set-operation columns, and invalid aggregate references, When analysis runs, Then failures identify the expression, expected type, actual type, and ANSI context.
  - [ ] Given resolved relations and expressions, When handed to `query-execution-engine-engineer`, Then the plan contains no parser nodes, unresolved attributes, unresolved functions, or unresolved catalog identifiers.
  - [ ] Given analyzer rule replay, When the same unresolved plan is analyzed twice, Then the resolved plan is equivalent and rule order is documented.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16`, `20` satisfied; docs updated if public API changes.

### FEAT-07.4: Function registry and ANSI semantics

- **Objective:** Provide registry-driven function binding and ANSI semantic rules for the v1 SQL set. Built-ins, overloads, type coercion, null propagation, overflow behavior, and three-valued logic must align with Spark where practical.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `query-execution-engine-engineer`.
- **Depends on:** FEAT-07.2, EPIC-04

#### Stories

##### STORY-07.4.1: Define and implement the v1 built-in function registry

- **As a** SQL language frontend engineer **I want** a metadata-driven built-in function registry **so that** function resolution is consistent, testable, and extensible.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-07.2.1, EPIC-04
- **Acceptance criteria:**
  - [ ] Given the v1 scalar, aggregate, predicate, cast, date/time, string, numeric, and collection function list, When registered, Then each function declares name aliases, argument contracts, return type rules, determinism, null behavior, and support status.
  - [ ] Given overloaded functions, When analysis binds a call, Then the selected overload is determined by documented precedence and coercion rules.
  - [ ] Given unknown functions, wrong arity, or ambiguous overloads, When analysis runs, Then diagnostics include function name, candidate signatures, and source position.
  - [ ] Given catalog or temporary functions from EPIC-06, When lookup is configured, Then registry lookup order is documented and verified.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16` satisfied; docs updated if public API changes.

##### STORY-07.4.2: Implement ANSI type coercion, overflow, and null semantics

- **As a** SQL language frontend engineer **I want** ANSI semantic rules applied during analysis **so that** SQL behavior matches Spark ANSI mode for the v1 dialect.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-07.4.1, STORY-07.3.3
- **Acceptance criteria:**
  - [ ] Given numeric, decimal, string, date, timestamp, boolean, and null literals, When expressions are analyzed, Then implicit coercions follow the documented ANSI/Spark precedence table.
  - [ ] Given arithmetic overflow, invalid casts, divide-by-zero, and assignment failures, When analyzed or marked for runtime evaluation, Then ANSI-mode error behavior is explicit and test-covered.
  - [ ] Given predicates involving nulls, NOT, AND, OR, IN, EXISTS, and comparisons, When analyzed, Then three-valued logic and nullability inference match v1 parity fixtures.
  - [ ] Given a documented Spark deviation, When semantic tests run, Then the deviation is named in the parity matrix and covered by a targeted fixture.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16`, `20` satisfied; docs updated if public API changes.

##### STORY-07.4.3: Add Spark-differential function and semantics fixtures

- **As a** SQL frontend maintainer **I want** Spark-differential fixtures for functions and ANSI semantics **so that** compatibility regressions are caught before release.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** STORY-07.4.2
- **Acceptance criteria:**
  - [ ] Given lawful Spark comparison fixtures for the v1 function set, When differential tests run, Then DeltaSharp resolved types, errors, and results-or-runtime-markers match Spark or named deviations.
  - [ ] Given unsupported functions, When tests run, Then each unsupported case fails with a precise unsupported-function error and suggested alternative where available.
  - [ ] Given randomized coercion cases within bounded type domains, When semantic analysis runs, Then inferred types and nullability match the reference table.
  - [ ] Given updates to the function registry, When CI runs, Then missing parity fixtures for new public functions are reported.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16` satisfied; docs updated if public API changes.

### FEAT-07.5: Error reporting and diagnostics

- **Objective:** Make SQL frontend errors stable, source-positioned, actionable, and Spark-aligned where practical. Diagnostics must distinguish syntax, unsupported feature, catalog resolution, attribute resolution, type checking, and function binding failures.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators none.
- **Depends on:** FEAT-07.1, FEAT-07.3, FEAT-07.4

#### Stories

##### STORY-07.5.1: Define SQL error classes and source-span conventions

- **As a** SQL language frontend engineer **I want** stable SQL error classes and source spans **so that** diagnostics are testable, documentable, and useful in CLI or IDE contexts.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators none.
- **Size:** M. **Depends on:** FEAT-07.1
- **Acceptance criteria:**
  - [ ] Given parse, unsupported syntax, unresolved relation, unresolved attribute, ambiguous reference, type mismatch, and function binding failures, When errors are produced, Then each has a stable class, message template, and source range.
  - [ ] Given multiline SQL with comments and quoted identifiers, When an error occurs, Then line, column, and highlighted token positions point to the user-authored SQL text.
  - [ ] Given diagnostics serialization, When exposed to tests and callers, Then machine-readable fields are stable and do not include secrets or table data.
  - [ ] Given Spark has an analogous error wording, When practical, Then DeltaSharp wording and remediation hints are aligned or the deviation is documented.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16`, `20` satisfied; docs updated if public API changes.

##### STORY-07.5.2: Implement parser and analyzer diagnostic emission

- **As a** SQL user **I want** precise parse and analysis errors **so that** I can fix SQL without inspecting engine internals.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `catalog-metastore-engineer`.
- **Size:** L. **Depends on:** STORY-07.5.1, FEAT-07.3
- **Acceptance criteria:**
  - [ ] Given malformed SQL, When parsing fails, Then the diagnostic includes expected-token context where useful, the offending token, and a source position.
  - [ ] Given missing tables, missing columns, ambiguous references, and invalid view expansions, When analysis fails, Then diagnostics include candidate names or catalog context without leaking restricted metadata.
  - [ ] Given invalid types, bad casts, and unresolved functions, When analysis fails, Then diagnostics identify the expression and semantic rule that failed.
  - [ ] Given a diagnostic golden suite, When messages change, Then reviewers can see class, span, and wording diffs independently.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16`, `19`, `20` satisfied; docs updated if public API changes.

##### STORY-07.5.3: Add diagnostic resilience and bounded-work tests

- **As a** SQL frontend maintainer **I want** diagnostics to remain stable under hostile or large SQL input **so that** untrusted query text cannot destabilize the driver.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators none.
- **Size:** M. **Depends on:** STORY-07.5.2
- **Acceptance criteria:**
  - [ ] Given deeply nested expressions, huge identifier lists, and long comments, When parsing and analysis run under configured limits, Then work is bounded and failures are deterministic.
  - [ ] Given cancellation during parse or analysis, When the cancellation token is signaled, Then processing stops and returns a stable cancellation diagnostic.
  - [ ] Given repeated invalid queries, When diagnostics are emitted, Then no unbounded allocations or generated-message growth is observed in regression tests.
  - [ ] Given diagnostics logs or traces, When inspected, Then they omit SQL literals or metadata classified as sensitive by the diagnostic policy.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `05`, `15`, `16` satisfied; docs updated if public API changes.

### FEAT-07.6: SQL/DataFrame convergence and `EXPLAIN` parity

- **Objective:** Prove that SQL and DataFrame/Dataset entry points converge on the same resolved logical plan contract and expose that convergence through `EXPLAIN`. This feature closes the handoff to execution by making plan forms inspectable and parity-tested.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `developer-experience-api-engineer`, `query-execution-engine-engineer`.
- **Depends on:** FEAT-07.2, FEAT-07.3, FEAT-07.4, EPIC-04

#### Stories

##### STORY-07.6.1: Add SQL/DataFrame resolved-plan convergence fixtures

- **As a** program manager **I want** SQL and DataFrame routes to converge on the same resolved plan **so that** v1 users get one engine semantics contract regardless of entry point.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `developer-experience-api-engineer`, `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-07.3.3, EPIC-04
- **Acceptance criteria:**
  - [ ] Given equivalent SQL and DataFrame/Dataset expressions for projection, filtering, joins, grouping, sorting, limits, and functions, When analyzed, Then resolved plans are semantically equivalent after normalization.
  - [ ] Given aliasing, column ordering, type coercion, and nullability differences, When convergence tests normalize plans, Then expected equivalences and intentional differences are documented.
  - [ ] Given a SQL-only or DataFrame-only unsupported feature, When compared, Then the test reports a named capability gap rather than a false semantic match.
  - [ ] Given downstream execution handoff, When the resolved plan is inspected, Then no entry-point-specific parser or API nodes remain.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16`, `20` satisfied; docs updated if public API changes.

##### STORY-07.6.2: Implement SQL `EXPLAIN` parsed and analyzed plan output

- **As a** SQL user **I want** `EXPLAIN` output for parsed and analyzed plan boundaries **so that** I can understand how DeltaSharp interpreted my query before execution.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `query-execution-engine-engineer`, `developer-experience-api-engineer`.
- **Size:** M. **Depends on:** STORY-07.6.1
- **Acceptance criteria:**
  - [ ] Given supported v1 queries, When `EXPLAIN` is requested, Then output includes parsed, unresolved, analyzed, and execution-handoff logical plan sections where applicable.
  - [ ] Given SQL that fails during parsing or analysis, When `EXPLAIN` is requested, Then the output identifies the failing phase and returns the same diagnostic class and source span as normal execution.
  - [ ] Given catalog-bound relations and functions, When `EXPLAIN` renders analyzed plans, Then resolved object identifiers and function names are visible without leaking restricted metadata.
  - [ ] Given equivalent SQL and DataFrame plans, When explain output is normalized, Then shared logical plan structure is visibly aligned.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `15`, `16`, `20` satisfied; docs updated if public API changes.

##### STORY-07.6.3: Publish SQL frontend support matrix and deviations

- **As a** DeltaSharp maintainer **I want** a SQL frontend support matrix **so that** users and implementers can see what is parsed, analyzed, executable, unsupported, or intentionally different from Spark.
- **Implementer persona(s):** Primary `sql-language-frontend-engineer`; Collaborators `developer-experience-api-engineer`.
- **Size:** S. **Depends on:** FEAT-07.1, FEAT-07.2, FEAT-07.3, FEAT-07.4, FEAT-07.5
- **Acceptance criteria:**
  - [ ] Given each v1 syntax family, function family, DDL/DML family, and `EXPLAIN` mode, When the matrix is reviewed, Then support state is recorded as parsed, analyzed, executable, unsupported, or deferred.
  - [ ] Given a Spark deviation, When users consult the matrix, Then they can see the rationale, user impact, and linked test or issue.
  - [ ] Given a new SQL feature is added, When checklist review runs, Then the support matrix and tests must be updated before the feature is considered done.
  - [ ] Given public documentation generation, When examples are included, Then they use real Spark-style SQL fixtures rather than pseudocode.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `11`, `15`, `20` satisfied; docs updated if public API changes.

## Open questions

- Which exact Spark built-in functions belong in the v1 ANSI-mode function set versus EPIC-13 compatibility hardening?
- Which HiveQL-derived syntax forms should parse as unsupported-feature diagnostics in M2 instead of being deferred entirely?
- What normalized plan equivalence rules should SQL/DataFrame convergence tests use for aliases, expression ids, and catalog metadata?
- How much Spark error-class naming should DeltaSharp mirror before a dedicated compatibility pass in EPIC-13?
