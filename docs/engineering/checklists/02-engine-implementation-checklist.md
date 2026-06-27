# 02 — Engine Implementation Checklist

> **Scope:** Query engine internals, plan IRs, analyzer/optimizer components, physical operators, execution backends, columnar/row formats, and driver-to-executor plan contracts.
> **Priority:** STANDARD.
> **Owners:** query-execution-engine-engineer, dotnet-vectorized-columnar-compute-engineer, dotnet-runtime-performance-engineer. **Grounded in:** ADR-0001, ADR-0002, ADR-0008, ADR-0012, `docs/engineering/design/engine-architecture.md`, [01](01-architecture-checklist.md).

## How to use
Use this checklist for engine component designs and implementation PRs. Verify each component is deterministic, testable, layer-correct, and compatible with the accepted execution, batch, type, and serialization ADRs.

## Checklist
### Plan tree contracts
- [ ] Logical, analyzed, optimized, physical, and adaptive plans are represented as immutable trees or immutable snapshots.
- [ ] Analyzer and optimizer rules create new plan trees rather than mutating existing nodes in place.
- [ ] Plan nodes carry stable operator identity, child ordering, output schema, expression metadata, and source/span information where diagnostics need it.
- [ ] Equality, hashing, and debug formatting are deterministic enough for golden tests, plan caches, and `EXPLAIN` output.
- [ ] Unsupported features fail with explicit analyzer/planner errors instead of producing weaker semantics.
- [ ] Plan caches and memo structures include catalog snapshot/version, configuration, ANSI mode, tenant, and relevant statistics provenance.
- [ ] API and SQL frontends produce unresolved plans only; analysis, optimization, physical planning, and execution remain engine responsibilities.
- [ ] Cross-link [16](16-catalyst-planning-checklist.md) when a change affects analyzer, optimizer, physical strategy selection, or `EXPLAIN` stages.

### Execution backend boundary
- [ ] `IExecutionBackend` or equivalent separates operator/expression evaluation from logical and physical planning.
- [ ] The vectorized interpreter is the default, always-present, AOT-safe correctness reference from ADR-0001.
- [ ] Optional JIT/codegen is gated on `RuntimeFeature.IsDynamicCodeSupported` and can be disabled for deterministic testing.
- [ ] Codegen entry points are annotated and packaged so NativeAOT builds can elide dynamic-code paths (`[RequiresDynamicCode]`, feature guards, or equivalent).
- [ ] Compiled delegates are cache-keyed by expression shape, types, ANSI/null semantics, and runtime configuration.
- [ ] Interpreter and compiled tiers are validated by a differential parity oracle over the same physical plans and batches.
- [ ] Codegen failures fall back to the interpreter without changing results or swallowing diagnostics.
- [ ] Benchmark evidence covers both backend selection overhead and hot expression/operator paths before making codegen the default for any scenario.

### Columnar batch boundary
- [ ] Operators bind to internal `ColumnBatch`/`ColumnVector` abstractions, not directly to `Apache.Arrow` arrays in hot paths.
- [ ] `ColumnBatch` and `ColumnVector` support mutable scratch, validity/null bitmaps, selection vectors, late materialization, and batch-size metadata.
- [ ] Arrow is used at edges such as Parquet, Flight, IPC, and interop; conversion points are explicit and measured.
- [ ] Kernels operate over spans/validity buffers with clear ownership, lifetime, disposal, and alignment assumptions.
- [ ] Selection-vector semantics are tested for filters, projections, joins, aggregations, and nested-field access.
- [ ] Batch operators define how they handle empty batches, all-null vectors, dictionary-like encodings, and large strings/binary values.
- [ ] Custom off-heap vectors can be introduced later without changing operator contracts.
- [ ] Allocation, pooling, and disposal behavior is observable and safe under cancellation, failure, and retries.

### Row and type-system integration
- [ ] Engine types model Spark SQL primitives, `decimal`, date/timestamp, intervals where supported, and complex `array`/`map`/`struct` types.
- [ ] Null semantics follow ANSI SQL three-valued logic, including validity bitmaps in columnar form and null bitsets in row form.
- [ ] Decimal precision/scale, timestamp behavior, overflow, casts, comparisons, and type coercion are specified and tested against Spark/ANSI expectations.
- [ ] The binary row format is compact, 8-byte-aligned, null-bitset-aware, byte-sortable where required, and suitable for shuffle/spill keys per ADR-0008.
- [ ] Row <-> columnar conversion preserves type, nullability, ordering, nested values, and ANSI error behavior.
- [ ] Sort, join, aggregation, shuffle, and spill paths document whether they operate on row, columnar, or hybrid representations.
- [ ] Public `Dataset<T>` typing does not leak internal row representation or weaken DataFrame/SQL semantics.
- [ ] Type-system changes update function resolution, analyzer checks, execution kernels, serialization, and parity matrices together.

### Protobuf plan serialization
- [ ] Driver-to-executor tasks use a versioned protobuf-defined physical plan boundary as required by ADR-0012.
- [ ] Serialized plans contain only portable physical-plan data, not live delegates, process-local object references, or unversioned closures.
- [ ] Expression, type, schema, partitioning, ordering, resource, and configuration fields are versioned with forward/backward compatibility rules.
- [ ] Executors validate serialized plans before execution and return precise errors for unsupported versions or missing capabilities.
- [ ] Plan serialization tests include round-trip, unknown-field, version-skew, and invalid-message cases.
- [ ] Codegen artifacts are re-derived on executors or explicitly modeled; compiled runtime delegates are not assumed to cross the wire.
- [ ] Tenant, catalog snapshot, table version, and authorization context are included or referenced safely when needed for execution.
- [ ] Serialization preserves enough structure for executor metrics and failure diagnostics to map back to `EXPLAIN` and driver plan IDs.

### Determinism, testability, and ownership
- [ ] Components accept explicit dependencies for clocks, random seeds, catalogs, statistics, storage, schedulers, and backend selection.
- [ ] Rule ordering, physical strategy selection, partition IDs, task attempts, and retry behavior are deterministic in tests.
- [ ] Engine tests cover success, nulls, ANSI failures, cancellation, disposal, retries, skew, empty inputs, and unsupported features.
- [ ] Shared contract tests validate every implementation behind an abstraction.
- [ ] Metrics and traces are emitted from stable plan/operator IDs with tenant-safe dimensions.
- [ ] Ownership boundaries are clear between query execution, optimizer/scheduler intelligence, SQL frontend, Delta storage, distributed runtime, and developer experience.
- [ ] Cross-link [01](01-architecture-checklist.md) for architecture guardrails and [15](15-spark-api-parity-checklist.md) for public semantic parity.

## Anti-patterns (red flags)
- Mutable plan nodes reused across analyzer, optimizer, or adaptive phases.
- API, parser, or optimizer code that directly invokes execution kernels.
- Operators bound directly to Arrow internals instead of `ColumnBatch`/`ColumnVector`.
- JIT/codegen paths required for correctness or enabled under NativeAOT without guards.
- Serialized physical plans that contain process-local callbacks, delegates, or unversioned objects.
- Type coercion, null behavior, or overflow behavior implemented only in kernels and not in analyzer rules.
- Tests that validate only the interpreter or only the compiled backend.
- Ownership gaps where CBO/AQE, SQL resolution, or Delta commit behavior is implemented in the wrong seat.

## References
- [01 — Architecture Checklist](01-architecture-checklist.md)
- [15 — Spark API Parity Checklist](15-spark-api-parity-checklist.md)
- [16 — Catalyst Planning Checklist](16-catalyst-planning-checklist.md)
- [Engine Architecture Overview](../design/engine-architecture.md)
- [ADR-0001: Execution strategy](../../adr/0001-execution-strategy.md)
- [ADR-0002: In-memory columnar batch format](../../adr/0002-columnar-batch-format.md)
- [ADR-0008: Type system and internal row/value representation](../../adr/0008-type-system-row-format.md)
- [ADR-0012: Plan serialization](../../adr/0012-plan-serialization.md)
- [Query & Execution Engine Engineer Agent](../../persona/agents/query-execution-engine-engineer-agent.md)
