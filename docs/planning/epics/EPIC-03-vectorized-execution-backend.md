# EPIC-03: Vectorized Execution Backend

- **Roadmap milestone:** M1 ([Roadmap](../../../ROADMAP.md))
- **Primary persona(s):** `dotnet-vectorized-columnar-compute-engineer`, `query-execution-engine-engineer` (+ collaborators `dotnet-runtime-performance-engineer`, `reliability-test-chaos-engineer`)
- **Related ADRs:** ADR-0001
- **Depends on:** EPIC-02
- **Status:** draft
- **Size:** XL

## Objective

Deliver the ADR-0001 execution backend foundation: an AOT-clean vectorized interpreter as the correctness reference plus an optional dynamic-code tier for hot expression fusion. This epic converts the EPIC-02 memory and type contracts into executable batch-at-a-time operators, SIMD kernels, spill-aware behavior, and parity tests needed before public Spark APIs can run local plans.

## Scope

**In scope**
- `IExecutionBackend` abstraction, startup backend selection, config override, and dynamic-code gating with `RuntimeFeature.IsDynamicCodeSupported`.
- Interpreted vectorized physical operators for v1 scan, filter, project, hash aggregate, sort, hash join, sort-merge join, and local exchange.
- SIMD kernel library for aggregates, comparisons, bitmap production, selection, and scalar fallback across x64 and Arm capability guards.
- Optional intra-operator `Expression.Compile()` fusion with delegate caching, AOT elision, and interpreter fallback.
- Differential parity oracle for interpreter vs compiled execution and scalar vs SIMD kernel behavior.
- Spill-aware operator contracts that cooperate with the EPIC-02 unified memory manager.

**Out of scope** (and where it lives instead)
- Columnar vector, binary row, type-system, and memory-manager primitives → EPIC-02 / personas `dotnet-vectorized-columnar-compute-engineer`, `dotnet-runtime-performance-engineer`.
- Public DataFrame/Dataset and SQL API surfaces → EPIC-04 and EPIC-07 / personas `developer-experience-api-engineer`, `sql-language-frontend-engineer`.
- Distributed executor RPC, Kubernetes pod lifecycle, and remote shuffle service → EPIC-08 and EPIC-09 / persona `dotnet-distributed-execution-engineer`.
- Cost-based optimization and adaptive query execution → EPIC-11 / persona `query-optimizer-scheduler-engineer`.

## Exit criteria

- [ ] `InterpretedVectorizedBackend` executes all v1 physical operators correctly over EPIC-02 batches and remains AOT-clean with no dynamic-code dependency.
- [ ] Backend startup selects compiled capabilities only when `RuntimeFeature.IsDynamicCodeSupported` is true, honors a force-interpreter override, and avoids compiled-tier references in AOT publish output.
- [ ] SIMD kernels match scalar oracle results for nulls, selection vectors, NaN, overflow, tails, unsupported hardware fallback, and representative batch sizes.
- [ ] `CompiledBackend` expression fusion produces results identical to the interpreter in the differential parity oracle and is safely elided under `PublishAot`.
- [ ] Operators request, release, and spill memory through the unified memory manager and complete deterministic memory-pressure tests under configured budgets.

## Features

### FEAT-03.1: `IExecutionBackend` abstraction and startup selection

- **Objective:** Define the ADR-0001 backend boundary and startup selection policy. The interpreter is always present and correct; the compiled tier is selected only when dynamic code is supported and configuration permits it.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `dotnet-runtime-performance-engineer`, `dotnet-vectorized-columnar-compute-engineer`.
- **Depends on:** EPIC-02

#### Stories

##### STORY-03.1.1: Define backend and operator execution contracts

- **As a** query execution engineer **I want** a pluggable backend contract **so that** physical plans can execute through either the interpreter or optional compiled fast paths without semantic divergence.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** M. **Depends on:** EPIC-02
- **Acceptance criteria:**
  - [ ] Given a v1 physical plan node, When execution starts, Then the backend contract accepts EPIC-02 schemas, batches, expressions, cancellation, and memory context.
  - [ ] Given scan, filter, project, aggregate, sort, join, and exchange-local nodes, When contracts are reviewed, Then each node has a typed input/output batch contract and metrics surface.
  - [ ] Given unsupported operator shapes, When execution is requested, Then the backend returns a precise unsupported-operator error without falling back to row-at-a-time execution.
  - [ ] Given NativeAOT build analysis, When the interpreter backend is used, Then no dynamic-code APIs are required for correctness.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `16`, `21` satisfied; docs updated if public API changes.

##### STORY-03.1.2: Implement dynamic-code gated backend selection

- **As a** runtime owner **I want** backend selection gated by dynamic-code support **so that** DeltaSharp remains AOT-clean while enabling codegen on JIT runtimes.
- **Implementer persona(s):** Primary `query-execution-engine-engineer`; Collaborators `dotnet-runtime-performance-engineer`.
- **Size:** S. **Depends on:** STORY-03.1.1
- **Acceptance criteria:**
  - [ ] Given `RuntimeFeature.IsDynamicCodeSupported` is false, When the engine starts, Then `InterpretedVectorizedBackend` is selected regardless of compiled-tier availability.
  - [ ] Given dynamic code is supported and config allows compiled execution, When the engine starts, Then compiled expression fusion is enabled while interpreter fallback remains available.
  - [ ] Given a force-interpreter configuration override, When dynamic code is supported, Then the interpreter is selected and compiled delegates are not created.
  - [ ] Given an AOT publish validation, When compiled-tier code is feature-gated, Then dynamic-code paths are analyzer-visible and elidable.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `10` satisfied; docs updated if public API changes.

### FEAT-03.2: Batch-at-a-time vectorized interpreter operators

- **Objective:** Implement the default `InterpretedVectorizedBackend` operators over `ColumnBatch` and `ColumnVector`. Operators must process batches, preserve Spark-compatible semantics, and avoid direct Arrow binding or row materialization except where required by binary-row sort/shuffle keys.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `query-execution-engine-engineer`, `dotnet-runtime-performance-engineer`.
- **Depends on:** FEAT-03.1, EPIC-02

#### Stories

##### STORY-03.2.1: Implement scan, filter, and project operators

- **As a** vectorized backend engineer **I want** scan, filter, and project operators over columnar batches **so that** simple Spark-compatible plans execute batch-at-a-time.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-03.1.1, EPIC-02
- **Acceptance criteria:**
  - [ ] Given an input batch from a scan source, When scan executes, Then output schema, row count, column ordering, null metadata, and selected rows match the physical plan contract.
  - [ ] Given a filter predicate that produces a bitmap or selection vector, When filter executes, Then only passing rows are exposed downstream without copying unneeded columns.
  - [ ] Given a projection with aliases, casts, and nullable expressions, When project executes, Then output vectors contain the expected values and validity bitmap.
  - [ ] Given empty batches, all-null columns, and non-zero offsets, When scan/filter/project run, Then results match scalar reference fixtures.
  - [ ] Given operator metrics collection, When these operators complete, Then input rows, output rows, selected rows, and elapsed time are reported.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `16`, `21` satisfied; docs updated if public API changes.

##### STORY-03.2.2: Implement aggregate, sort, join, and exchange-local operators

- **As a** query execution engineer **I want** core relational operators in the vectorized interpreter **so that** v1 physical plans can run without the compiled tier.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `query-execution-engine-engineer`, `dotnet-runtime-performance-engineer`.
- **Size:** XL. **Depends on:** STORY-03.2.1, FEAT-02.4
- **Acceptance criteria:**
  - [ ] Given grouped and global hash aggregates, When batches execute, Then group keys, aggregate values, null behavior, and overflow behavior match scalar oracle results.
  - [ ] Given sort keys encoded with EPIC-02 binary rows, When sort executes, Then output order matches the documented comparator for nulls, NaN, decimals, and timestamps.
  - [ ] Given hash join and sort-merge join inputs, When joins execute, Then inner, left, right, and full join fixtures preserve Spark-compatible row multiplicity and null-key semantics for v1.
  - [ ] Given an exchange-local boundary, When batches are partitioned locally, Then partition ids and row counts match the partitioning expression and no rows are lost or duplicated.
  - [ ] Given cancellation during a long operator, When the cancellation token is signaled, Then execution stops at a bounded checkpoint and releases owned buffers.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `16`, `21`, `22` satisfied; docs updated if public API changes.

### FEAT-03.3: SIMD kernel library with scalar fallback

- **Objective:** Build reusable kernels for aggregates, comparisons, bitmap generation, and selection. Each fast path must be guarded by hardware capability checks and share one semantic contract with scalar fallback.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `dotnet-runtime-performance-engineer`, `performance-benchmarking-engineer`.
- **Depends on:** EPIC-02, FEAT-03.2

#### Stories

##### STORY-03.3.1: Implement aggregate and comparison kernels

- **As a** columnar compute engineer **I want** scalar and SIMD kernels for common aggregates and comparisons **so that** operators can reuse verified hot-loop primitives.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `dotnet-runtime-performance-engineer`.
- **Size:** L. **Depends on:** FEAT-02.6, STORY-03.2.1
- **Acceptance criteria:**
  - [ ] Given numeric vectors with nulls, selections, tails, and no-null fast paths, When sum, min, max, and count kernels run, Then SIMD and scalar results match exactly or within documented floating-point tolerance.
  - [ ] Given comparison predicates over supported primitive, decimal, date, and timestamp types, When kernels run, Then output bitmaps match scalar reference results including null and NaN cases.
  - [ ] Given x64 Vector256/Vector512, Arm AdvSimd, and scalar-only environments, When capability guards are evaluated, Then only supported intrinsic paths execute.
  - [ ] Given integer overflow cases under ANSI semantics, When aggregate kernels run, Then overflow behavior matches the EPIC-02 type-system contract.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `15`, `22` satisfied; docs updated if public API changes.

##### STORY-03.3.2: Implement bitmap-to-selection and selection-aware kernels

- **As a** vectorized operator author **I want** reusable bitmap and selection kernels **so that** filters and joins compose without row materialization.
- **Implementer persona(s):** Primary `dotnet-vectorized-columnar-compute-engineer`; Collaborators `dotnet-runtime-performance-engineer`.
- **Size:** M. **Depends on:** STORY-03.3.1
- **Acceptance criteria:**
  - [ ] Given predicate bitmaps with arbitrary offsets and tail lengths, When converted to selection vectors, Then selected indexes are ordered, unique, and within the input row count.
  - [ ] Given an existing selection vector and a new predicate bitmap, When composed, Then output selection matches applying both predicates in order.
  - [ ] Given empty, all-pass, all-fail, and sparse predicates, When selection kernels execute, Then scalar and SIMD paths produce identical selected counts and indexes.
  - [ ] Given benchmark runs across selectivity and batch size, When kernels are measured, Then allocations are zero on the hot path and throughput metrics are recorded for regression gating.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `22` satisfied; docs updated if public API changes.

### FEAT-03.4: Expression evaluation and intra-operator compiled fusion

- **Objective:** Implement ADR-0001 expression evaluation with interpreter-first semantics and optional `Expression.Compile()` fusion. The compiled tier must cache delegates by stable expression shape, preserve interpreter parity, and remain absent under AOT.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`, `query-execution-engine-engineer`.
- **Depends on:** FEAT-03.1, FEAT-03.2

#### Stories

##### STORY-03.4.1: Implement interpreted expression evaluator

- **As a** query execution engineer **I want** interpreted batch expression evaluation **so that** filters and projections have an AOT-clean semantic baseline.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `query-execution-engine-engineer`, `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** L. **Depends on:** STORY-03.2.1, FEAT-02.5
- **Acceptance criteria:**
  - [ ] Given arithmetic, comparison, boolean, cast, and null-check expressions, When evaluated over a batch, Then output values and validity bitmaps match Spark parity fixtures.
  - [ ] Given selected rows and sliced vectors, When expressions evaluate, Then only selected logical rows are processed and output row order is deterministic.
  - [ ] Given decimal overflow, timestamp casts, NaN comparisons, and null inputs, When expressions evaluate, Then behavior follows EPIC-02 ANSI and null contracts.
  - [ ] Given NativeAOT validation, When interpreted expressions execute, Then no runtime code generation or reflection emit APIs are required.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `15`, `21` satisfied; docs updated if public API changes.

##### STORY-03.4.2: Add dynamic-code gated compiled expression fusion

- **As a** runtime performance engineer **I want** delegate-cached `Expression.Compile()` fusion **so that** hot predicates and projections can run faster on JIT runtimes without becoming correctness dependencies.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** L. **Depends on:** STORY-03.4.1, STORY-03.1.2
- **Acceptance criteria:**
  - [ ] Given dynamic code is unsupported, When a fusible expression is executed, Then the interpreter path runs and no compiled delegate is created.
  - [ ] Given dynamic code is supported and fusion is enabled, When the same expression shape and type/nullability contract executes repeatedly, Then a cached delegate is reused.
  - [ ] Given cache size limits, When many expression shapes are compiled, Then eviction is bounded and metrics report compile count, cache hit count, and eviction count.
  - [ ] Given interpreter and compiled execution for the same batch, When the parity oracle compares outputs, Then values, validity, errors, and metrics-relevant row counts match.
  - [ ] Given AOT publish analysis, When compiled-tier annotations are inspected, Then dynamic-code APIs are guarded with analyzer-visible attributes or feature switches.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `10`, `22` satisfied; docs updated if public API changes.

### FEAT-03.5: Differential parity oracle

- **Objective:** Build the correctness oracle required by ADR-0001 so the interpreter remains the ground truth and compiled/SIMD paths cannot diverge silently. The oracle must combine golden fixtures, randomized batches, and property checks over v1 semantics.
- **Implementer persona(s):** Primary `reliability-test-chaos-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`, `dotnet-runtime-performance-engineer`, `query-execution-engine-engineer`.
- **Depends on:** FEAT-03.2, FEAT-03.3, FEAT-03.4

#### Stories

##### STORY-03.5.1: Add scalar-vs-SIMD kernel parity suite

- **As a** reliability engineer **I want** scalar and SIMD kernels compared across generated inputs **so that** hardware fast paths cannot change query results.
- **Implementer persona(s):** Primary `reliability-test-chaos-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** M. **Depends on:** FEAT-03.3
- **Acceptance criteria:**
  - [ ] Given generated batches varying type, null density, selection density, offset, and batch size, When scalar and SIMD kernels run, Then outputs match for every generated case.
  - [ ] Given floating-point inputs including NaN, infinities, signed zero, and normal values, When kernels compare outputs, Then comparisons use the documented tolerance and NaN policy.
  - [ ] Given unsupported hardware in CI simulation or scalar-only configuration, When tests run, Then scalar fallback coverage still validates every kernel family.
  - [ ] Given a failing generated case, When the test reports failure, Then it prints the seed, schema, expression, hardware path, and minimal reproduction data.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `21`, `22` satisfied; docs updated if public API changes.

##### STORY-03.5.2: Add interpreter-vs-compiled backend parity suite

- **As a** reliability engineer **I want** compiled backend outputs compared to the interpreter **so that** optional codegen remains an optimization only.
- **Implementer persona(s):** Primary `reliability-test-chaos-engineer`; Collaborators `dotnet-runtime-performance-engineer`, `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** FEAT-03.4
- **Acceptance criteria:**
  - [ ] Given golden physical plans for scan, filter, project, aggregate, sort, join, and exchange-local, When both backends are enabled, Then outputs and errors match the interpreter ground truth.
  - [ ] Given randomized expressions over v1 types, When interpreter and compiled evaluators run over the same batches, Then values and validity bitmaps are identical.
  - [ ] Given dynamic code is disabled, When the parity suite runs, Then compiled cases are reported as skipped for a documented capability reason and interpreter coverage remains green.
  - [ ] Given a parity mismatch, When test diagnostics are emitted, Then they include plan shape, backend selection, seed, schema, expression tree, and first mismatching row.
  - [ ] Given CI execution, When parity suites complete, Then results are deterministic across repeated runs with fixed seeds.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `21`, `22` satisfied; docs updated if public API changes.

### FEAT-03.6: Spill-aware operator execution

- **Objective:** Make vectorized operators cooperate with the EPIC-02 unified memory manager. Operators must reserve memory, release it deterministically, and spill state for aggregation, sort, join, and local exchange under budget pressure.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`, `dotnet-distributed-execution-engineer`, `query-execution-engine-engineer`.
- **Depends on:** FEAT-03.2, FEAT-02.3

#### Stories

##### STORY-03.6.1: Add operator memory reservations and release discipline

- **As a** runtime performance engineer **I want** every operator to reserve and release memory through the unified manager **so that** execution memory is bounded and observable.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** M. **Depends on:** STORY-03.2.1, FEAT-02.3
- **Acceptance criteria:**
  - [ ] Given an operator that allocates output vectors or scratch state, When execution begins, Then memory is reserved against the task budget before use.
  - [ ] Given normal completion, cancellation, and exception paths, When the operator exits, Then all reservations and buffers are released exactly once.
  - [ ] Given operator metrics, When execution completes, Then peak reserved bytes, current reserved bytes, spill bytes, and allocation count are reported.
  - [ ] Given nested operator pipelines, When downstream operators consume upstream batches, Then ownership transfer and release responsibilities are explicit in tests.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `21`, `22` satisfied; docs updated if public API changes.

##### STORY-03.6.2: Implement spill paths for stateful operators

- **As a** query execution engineer **I want** aggregation, sort, join, and exchange-local state to spill under pressure **so that** large inputs complete within configured budgets.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`, `dotnet-distributed-execution-engineer`.
- **Size:** L. **Depends on:** STORY-03.6.1, STORY-03.2.2
- **Acceptance criteria:**
  - [ ] Given hash aggregate state exceeds its budget, When spill is requested, Then partial state is serialized and later merged to produce the same result as no-spill execution.
  - [ ] Given sort input exceeds its budget, When spill is requested, Then sorted runs are written and merged in global order matching the no-spill comparator result.
  - [ ] Given hash join build state exceeds its budget, When spill is requested, Then partitioned spill/probe behavior preserves join cardinality and null-key semantics.
  - [ ] Given local exchange buffers exceed budget, When spill is requested, Then partitions are recoverable and row counts per partition match no-spill execution.
  - [ ] Given injected spill I/O failure, When an operator handles the failure, Then it releases memory and returns a deterministic execution error without partial success.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04a`, `08`, `21`, `22` satisfied; docs updated if public API changes.

## Open questions

- What threshold policy should promote an interpreted expression to compiled fusion: static expression shape, observed row count, elapsed time, or a configurable combination?
- Should the v1 interpreter include window operator stubs with explicit unsupported errors, or should window execution remain entirely out of EPIC-03 until SQL frontend scope is finalized?
- Which spill file format should stateful operators use before the remote shuffle service defines its final block format?
