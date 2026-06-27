# 22 — Benchmark Regression Gates Checklist

> **Scope:** Benchmark authorship, continuous performance gates, statistical comparison, workload design, and release performance evidence.
> **Priority:** STANDARD.
> **Owners:** performance-benchmarking-engineer. **Grounded in:** performance persona research, build-test config, ADR-0001, ADR-0004, ADR-0006.

## How to use
Use this checklist when adding benchmarks, changing performance gates, or making performance claims. Benchmarks must be reproducible, statistically compared, and tied to the hot-path guidance in 08.

## Checklist
### Benchmark contract
- [ ] Each benchmark states workload, dataset scale, table layout, query mix, executor count, storage backend, runtime version, OS image, node shape, and cache state.
- [ ] The benchmark names whether it measures cold start, cold cache, warm cache, or steady state.
- [ ] Reproduction commands, seeds, manifests, data generator versions, and expected variance are stored with the benchmark.
- [ ] Environment fingerprints include .NET SDK/runtime, GC mode, CPU architecture, Kubernetes version, storage class, object-store region, and DeltaSharp commit.
- [ ] Benchmark harness overhead is measured or bounded so the load generator and result collector are not bottlenecks.
- [ ] Results include enough metadata to compare only like-for-like runs unless an explicit calibration says otherwise.

### BenchmarkDotNet discipline
- [ ] Micro-benchmarks use Release configuration and avoid Debug, attached debugger, or unoptimized test runners.
- [ ] BenchmarkDotNet jobs record runtime, JIT/AOT mode, ServerGC/workstation GC, warm-up, iteration count, launch count, and invocation count.
- [ ] `[MemoryDiagnoser]` is enabled for hot paths where allocations matter.
- [ ] `[DisassemblyDiagnoser]` or equivalent JIT evidence is used when claiming inlining, SIMD, bounds-check elimination, or devirtualization.
- [ ] Benchmarks prevent dead-code elimination and constant folding from removing the work being measured.
- [ ] Parameter sets cover realistic row counts, batch sizes, schema widths, null densities, encodings, and skew.
- [ ] Benchmark output is exported in machine-readable form for trend storage and comparison.

### Workload suites
- [ ] TPC-DS/TPC-H-style suites cover scan-heavy, shuffle-heavy, join-heavy, aggregate-heavy, sort-heavy, and write-heavy queries.
- [ ] Delta-specific workloads include `_delta_log` replay, checkpoints, time travel, schema evolution, deletion vectors, CDF, OPTIMIZE, VACUUM, and commit contention.
- [ ] Parquet workloads cover row groups, encodings, dictionary/RLE, compression, footer reads, predicate pruning, nested columns, and wide schemas.
- [ ] Connector workloads include JDBC, Kafka, object-store listing, CSV/JSON parsing, streaming offsets, and source/sink backpressure where implemented.
- [ ] Distributed workloads cover executor scaling, shuffle replication, drain-migration, registry re-resolution, skew, stage retries, and Kubernetes pod startup.
- [ ] Multi-tenant workloads include quiet baseline, balanced tenants, bursty tenants, scan-heavy neighbor, shuffle-heavy neighbor, and mixed storage backends.
- [ ] Both ADR-0001 execution backends are benchmarked when the compiled backend is available; interpreter remains the baseline correctness/performance reference.

### Arrival-rate and latency methodology
- [ ] Job-submission, ad hoc SQL, streaming-like, and mixed-tenant workloads use open-loop offered-load models when arrival rate matters.
- [ ] Coordinated-omission-prone harnesses are rejected for latency or capacity claims.
- [ ] Reports lead with p50, p95, p99, and p99.9 for stage, task, operator, shuffle, scan, commit, and end-to-end latency where relevant.
- [ ] Means are supplementary and never the only headline metric for user-visible performance.
- [ ] Throughput-vs-latency curves vary offered load through saturation and identify the knee of the curve.
- [ ] Tail-latency outliers are linked to traces, counters, storage events, GC pauses, or scheduler events before being dismissed as noise.

### Regression gates
- [ ] Pre-merge gates are fast, targeted to changed subsystems, and fail on severe regressions in hot-path micro-benchmarks or smoke query suites.
- [ ] Nightly gates run broader TPC-style, Delta, connector, shuffle, and multi-tenant workloads at stable scale.
- [ ] Release gates include full-scale, backend-matrix, cold-start, steady-state, and capacity-curve evidence.
- [ ] Gate thresholds are explicit for end-to-end job time, p99/p99.9 latency, throughput, allocation rate, GC time, spill bytes, object-store requests, and capacity knee.
- [ ] Statistical comparison is benchstat-style: repeated runs, confidence intervals or nonparametric comparison, noise floor, and clear pass/fail criteria.
- [ ] Change-point detection flags trend breaks across commits, runtime upgrades, dataset changes, and environment drift.
- [ ] Gate failures identify owner, metric, threshold, reproduction command, likely subsystem, and whether rollback or investigation is required.

### Allocation and GC budgets
- [ ] Hot-path benchmarks include allocation bytes per row, bytes per batch, Gen0/Gen1/Gen2 counts, LOH/POH pressure, and GC pause distribution.
- [ ] Allocation budget regressions are treated as performance failures even when wall-clock time looks unchanged.
- [ ] Server GC, heap limits, DATAS or container-aware settings, and thread-pool configuration are recorded for executor benchmarks.
- [ ] Native/off-heap memory and pooled-buffer usage report peak, retained, leaked, and returned bytes where measurable.
- [ ] Benchmark gates integrate findings from 08 before accepting allocation-heavy optimizations.

### Results and reporting
- [ ] Dashboards show trends for p50/p95/p99/p99.9, throughput, job time, allocation, GC, spill, shuffle bytes, scan bytes, and object-store requests.
- [ ] Results are segmented by workload family instead of averaged into one misleading score.
- [ ] Public performance claims include environment, dataset, semantics, caveats, and reproduction path.
- [ ] Benchmark data avoids secrets, regulated data, and tenant-identifying metadata.
- [ ] A regression that exposes wrong results, lost data, or retry corruption is handed to 21 as correctness, not explained away as performance.

## Anti-patterns (red flags)
- Single-run comparisons used to approve or reject a performance change.
- Reporting averages while p99 or p99.9 regresses.
- Closed-loop latency tests used for arrival-rate-sensitive systems.
- Comparing benchmarks across changed node types, runtime versions, storage regions, datasets, or cache states without calibration.
- BenchmarkDotNet tests that allocate unexpectedly but omit `[MemoryDiagnoser]`.
- Claiming SIMD/JIT wins without disassembly, counters, or a realistic parameter set.
- Optimizing a hot path in 08 without adding or updating a benchmark gate here.
- Hiding benchmark harness overhead, noisy-neighbor effects, or object-store throttling from results.

## References
- [08 — Performance Checklist](08-performance-checklist.md).
- [17 — Delta Storage Format Checklist](17-delta-storage-format-checklist.md); [19 — Data Source Connectors Checklist](19-data-source-connectors-checklist.md); [21 — Distributed Correctness Checklist](21-distributed-correctness-checklist.md).
- [ADR-0001: Execution strategy](../../adr/0001-execution-strategy.md); [ADR-0004: Shuffle architecture](../../adr/0004-shuffle-architecture.md); [ADR-0006: Scheduler, AQE, and CBO](../../adr/0006-scheduler-aqe-cbo.md).
- BenchmarkDotNet documentation, including diagnosers, warm-up, exporters, and runtime configuration.
- Gil Tene, “How NOT to Measure Latency”; HdrHistogram percentile methodology; benchstat-style statistical comparison.
