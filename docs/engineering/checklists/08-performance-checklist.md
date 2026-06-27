# 08 — Performance Checklist

> **Scope:** Hot-path implementation, allocation control, .NET runtime behavior, scan/shuffle/codec efficiency, and evidence-backed performance fixes.
> **Priority:** STANDARD.
> **Owners:** dotnet-runtime-performance-engineer, performance-benchmarking-engineer. **Grounded in:** ADR-0001, ADR-0002, ADR-0013, ADR-0004, benchmark gates in 22.

## How to use
Use this checklist for code that can affect throughput, latency, allocation, GC, scan efficiency, shuffle efficiency, or executor resource usage. Do not optimize without a benchmark or profiling signal that can be enforced by 22.

## Checklist
### Evidence before optimization
- [ ] The change names the measured symptom: wall time, p99 latency, allocation rate, GC pause, CPU sample, thread-pool starvation, I/O wait, or benchmark regression.
- [ ] A before/after BenchmarkDotNet benchmark, macro benchmark, trace, or counter stream exists for the affected path.
- [ ] The benchmark or trace uses Release settings and records runtime, GC mode, CPU architecture, OS, container limits, and dataset shape.
- [ ] Performance conclusions are tied to 22 regression gates so the improvement or budget cannot silently regress.
- [ ] Correctness tests remain primary; performance fixes never change Spark semantics, Delta ACID, or distributed correctness.
- [ ] Surprising macro results are profiled with dotnet-trace, EventPipe, dotnet-counters, PerfView, or equivalent before guessing.

### Allocation discipline
- [ ] Hot loops avoid avoidable allocations, boxing, iterator allocations, closure captures, `params` arrays, string materialization, and LINQ.
- [ ] Batch-oriented APIs prefer `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>`, `ReadOnlyMemory<T>`, `ref struct`, `in`, and `readonly struct` where lifetimes are safe.
- [ ] `ArrayPool<T>` or `MemoryPool<T>` rentals have explicit ownership, return paths, clearing policy, and exception-safe cleanup.
- [ ] Off-heap `NativeMemory` or Arrow buffers follow ADR-0013 with alignment, accounting, disposal, and memory-pressure limits.
- [ ] Large buffers avoid LOH churn unless the lifetime and GC impact are measured and accepted.
- [ ] Per-row object models are not introduced into columnar scan, expression, aggregation, join, shuffle, or codec paths.
- [ ] Async state machines are avoided in per-row/per-batch hot loops unless I/O awaits justify the overhead.

### GC and runtime configuration
- [ ] Executor benchmarks record Server GC, heap hard limits, DATAS/container-aware settings, thread-pool settings, tiered compilation, Dynamic PGO, ReadyToRun, and AOT/JIT mode.
- [ ] GC tuning follows allocation/lifetime evidence; knobs are not used to hide allocation storms.
- [ ] Long-lived pools have bounded capacity and metrics for rented, returned, retained, and discarded buffers.
- [ ] Native/off-heap memory counts against executor budgets and spills before causing pod OOM.
- [ ] Thread-pool starvation, sync-over-async, lock contention, and blocking I/O are diagnosed separately.
- [ ] Runtime configuration changes are documented with the workload assumption and rollback signal.

### Vectorization and codegen
- [ ] SIMD intrinsics are guarded by `IsSupported` and provide scalar or `Vector<T>` fallbacks with identical null, overflow, NaN, and decimal behavior.
- [ ] Vectorized kernels operate on columnar batches and selection vectors from ADR-0002 rather than materializing rows.
- [ ] Bounds-check elimination, inlining, devirtualization, and vectorization claims are backed by disassembly or JIT evidence.
- [ ] Unsafe code documents ownership, alignment, lifetime, pinning, bounds, and exception-safety assumptions.
- [ ] ADR-0001 compiled/codegen paths are optional, guarded by dynamic-code support, bounded in cache growth, and parity-tested against the interpreter.
- [ ] Delegate caches key on stable expression shape, type, nullability, culture/collation, and overflow behavior.

### Scan, codec, and storage paths
- [ ] Parquet decode paths minimize copies from compressed pages to `ColumnVector` and preserve Arrow-at-the-edges boundaries from ADR-0002.
- [ ] Dictionary/RLE/bit-packed decoding, decompression, UTF-8 parsing, decimal conversion, and timestamp conversion have focused benchmarks.
- [ ] Row-group and page pruning avoid reading unnecessary bytes while preserving residual predicate correctness from 17 and 19.
- [ ] Object-store reads batch metadata requests, respect throttling, and avoid per-file/per-row awaits in tight loops.
- [ ] Delta log replay and checkpoint reads avoid unbounded JSON/object allocation and expose replay-depth metrics.
- [ ] Compaction, OPTIMIZE, and VACUUM performance changes are tied to storage-correctness safety in 17.

### Shuffle and distributed paths
- [ ] Shuffle serialization, Arrow IPC block layout, compression, checksums, and transfer buffers are benchmarked separately from query operators.
- [ ] Shuffle fetch, replication, drain-migration, and registry calls use bounded concurrency and backpressure.
- [ ] Reducers re-resolve shuffle locations on failure per 21; performance shortcuts never pin stale endpoints.
- [ ] gRPC and Arrow Flight settings are treated as capacity controls with documented stream/window/message-size assumptions.
- [ ] Channels, queues, and task dispatch paths are bounded and expose depth, wait time, dequeue latency, and dropped/canceled work.
- [ ] Executor pod startup, image pull, JIT warm-up, and connection-pool warm-up are separated from steady-state job performance.

### Async and I/O efficiency
- [ ] I/O APIs propagate cancellation tokens and deadlines without creating unbounded task chains or fire-and-forget work.
- [ ] High-throughput reads and writes use buffers sized by measurement, not arbitrary constants.
- [ ] Streams, readers, writers, compression codecs, and pooled buffers are disposed deterministically.
- [ ] Backpressure reaches sources/sinks instead of accumulating unbounded in memory.
- [ ] Retries use bounded backoff and jitter and expose request amplification metrics.

### Review and gate linkage
- [ ] Every performance-motivated PR states which 22 gate, benchmark, trace, or budget it improves or protects.
- [ ] A performance optimization that touches 17, 19, or 21 includes correctness evidence for storage, connectors, or distributed execution respectively.
- [ ] Allocation/GC changes include budgets and thresholds, not only “faster locally” claims.
- [ ] New hot-path abstractions document whether they are on the critical path and what measurement will catch regressions.
- [ ] Performance documentation avoids public claims until benchmark methodology from 22 is satisfied.

## Anti-patterns (red flags)
- Optimizing without a benchmark, trace, counter, or regression gate.
- Introducing LINQ, boxing, reflection, per-row objects, or string parsing inside scan/operator/shuffle hot loops.
- Using unsafe/SIMD/codegen shortcuts without scalar fallback and parity tests.
- Moving data on heap/off-heap without ownership, disposal, or memory-budget accounting.
- Tuning GC or thread-pool knobs to mask unbounded allocation or blocking.
- Measuring in Debug, on a changed environment, or with a benchmark that the JIT can optimize away.
- Pinning shuffle endpoints or weakening correctness to save retries.
- Reporting a mean latency improvement while p99, allocation, or GC budget regresses.

## References
- [22 — Benchmark Regression Gates Checklist](22-benchmark-regression-gates-checklist.md).
- [17 — Delta Storage Format Checklist](17-delta-storage-format-checklist.md); [19 — Data Source Connectors Checklist](19-data-source-connectors-checklist.md); [21 — Distributed Correctness Checklist](21-distributed-correctness-checklist.md).
- [ADR-0001: Execution strategy](../../adr/0001-execution-strategy.md).
- [ADR-0002: In-memory columnar batch format](../../adr/0002-columnar-batch-format.md).
- [ADR-0013: Memory model for in-memory batches](../../adr/0013-memory-model.md).
- [ADR-0004: Shuffle architecture](../../adr/0004-shuffle-architecture.md).
- BenchmarkDotNet, dotnet-trace, EventPipe, dotnet-counters, PerfView, and .NET GC/JIT diagnostics documentation.
