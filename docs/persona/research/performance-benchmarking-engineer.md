# Performance & Benchmarking Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

The Performance & Benchmarking Engineer is the discipline owner for pre-production performance methodology, benchmark harnesses, profiling discipline, regression gates, and capacity modeling across DeltaSharp: a .NET-native Spark-equivalent engine with native Delta tables, Catalyst-style planning, lazy transformations, eager actions, shuffle-split stages, Kubernetes driver/executor execution, and storage on S3, ADLS, GCS, and PVCs. The role exists because query micro-benchmarks, storage micro-benchmarks, and production SLOs each answer only part of the question. DeltaSharp also needs a rigorous system-level answer to: how fast is this engine, under which workloads, at which data scales, with which tail latency, at which executor count, on which storage backend, and with what confidence?[^1]

This role combines measurement science with distributed analytics systems knowledge. Measurement science prevents false claims: coordinated omission, hidden cache state, single-run comparisons, noisy CI workers, and untracked environment drift can make a regression look like an improvement. Distributed analytics knowledge prevents irrelevant measurements: a scan-heavy Parquet benchmark says little about a skewed shuffle join; an empty Delta log says little about time travel on a table with thousands of commits; a single-tenant query suite says little about shared executors under a noisy neighbor.[^2]

DeltaSharp's .NET runtime also changes the performance surface. BenchmarkDotNet is appropriate for micro-benchmarks, but system behavior must be diagnosed with dotnet-trace, EventPipe, dotnet-counters, PerfView, and targeted JIT/GC analysis. Allocation rate, GC pause distribution, thread-pool behavior, async overhead, and JIT warm-up are first-class variables, not afterthoughts.[^3] The engineer need not own deep runtime internals, but must know when those variables are distorting p99 stage latency or end-to-end job time and when to pull in `dotnet-framework-runtime-engineer`.

Continuous benchmarking is a deliverable, not a nice-to-have. DeltaSharp needs fast pre-merge checks for changed subsystems, nightly benchmark sweeps across representative query and storage workloads, and release-candidate gates that prove scale, skew, multi-tenant isolation, and capacity claims. Results must be stored with environment fingerprints, dataset versions, commit SHAs, executor configurations, and statistical comparisons so regressions are found as change points rather than argued from screenshots.[^4]

---

## Evidence base

- Gil Tene, "How NOT to Measure Latency," QCon London 2013, InfoQ — coordinated omission, open-loop measurement, and percentile reporting pitfalls.[^1]
- HdrHistogram documentation — high dynamic range latency histograms suitable for p99 and p99.9 distribution capture.[^1]
- Brendan Gregg, *Systems Performance: Enterprise and the Cloud*, 2nd ed. — USE method, profiling methodology, flame graph interpretation, and systematic bottleneck analysis.[^2]
- Brendan Gregg, USE Method reference — utilization, saturation, and errors as the first pass for every resource.[^2]
- Raj Jain, *The Art of Computer Systems Performance Analysis* — experimental design, queueing theory, confidence intervals, and performance-data interpretation.[^4]
- Neil Gunther, *Guerrilla Capacity Planning* — Universal Scalability Law and capacity curves for systems with contention and coherency penalties.[^5]
- Andy Georges, Dries Buytaert, Lieven Eeckhout, "Statistically Rigorous Java Performance Evaluation," OOPSLA 2007 — warm-up, replication, and confidence intervals for managed-runtime benchmarks.[^4]
- BenchmarkDotNet documentation — .NET micro-benchmark harness, diagnosers, exporters, warm-up, iteration control, and allocation reporting.[^3]
- Microsoft diagnostics documentation for dotnet-trace, EventPipe, dotnet-counters, dotnet-gcdump, and PerfView — runtime event collection, counters, CPU samples, allocation, and GC analysis.[^3]
- Transaction Processing Performance Council TPC-H and TPC-DS specifications — industry-standard decision-support query suites and scale-factor methodology.[^6]
- Apache Spark SQL performance and Spark on Kubernetes documentation — relevant comparison model for query execution, shuffle, caching, and driver/executor behavior.[^7]
- Delta Lake protocol documentation — transaction log, optimistic concurrency, checkpoints, time travel, schema evolution, and table features that must be represented in benchmarks.[^8]

---

## Explanation

### Why this role exists

DeltaSharp is greenfield, which makes benchmarking more important rather than less. Early architecture choices define whether later performance claims are measurable. The system spans API design, logical plans, optimizer rules, physical operators, driver scheduling, executor pods, shuffle boundaries, Parquet I/O, Delta commits, cloud object stores, PVCs, and .NET runtime behavior. No single component owner can credibly prove the whole system's performance envelope.

`cloud-native-site-reliability-engineer` owns production SLOs and operations. `delta-storage-format-engineer` owns Delta and Parquet internals. `query-execution-engine-engineer` owns query planning and execution semantics. `compute-storage-finops-engineer` owns unit economics and cost interpretation. This role connects them with reproducible pre-production evidence: benchmark harnesses, result storage, statistical gates, capacity curves, and regression investigations.

### Boundaries

- **vs. `cloud-native-site-reliability-engineer`**: that role defines production SLOs, alerting, rollout safety, and incidents. This role supplies capacity curves, tail-latency baselines, and safe operating envelopes.
- **vs. `delta-storage-format-engineer`**: Storage owns on-disk Delta/Parquet design and micro-benchmarks. This role integrates those measurements into full jobs that include planning, scheduling, scan, shuffle, write, and commit behavior.
- **vs. `query-execution-engine-engineer`**: Query owns semantics, optimizer behavior, physical operators, and query catalogues. This role turns those catalogues into reproducible, statistically defensible system benchmarks.
- **vs. `compute-storage-finops-engineer`**: that role interprets cost, pricing, and commercial efficiency. This role supplies raw efficiency curves and resource-consumption evidence.
- **vs. `reliability-test-chaos-engineer`**: Chaos owns correctness under fault and failure-mode exploration. This role stays focused on performance, handing off when a benchmark reveals correctness or resilience failures.
- **vs. `dotnet-framework-runtime-engineer`**: Runtime owns deep .NET internals and framework implementation guidance. This role recognizes GC/JIT/thread-pool performance signals and requests specialist help when needed.

---

## Required knowledge domains

### 1. Performance methodology fundamentals

**USE Method**: For every resource, measure utilization, saturation, and errors. In DeltaSharp, resources include CPU cores, managed heap, thread pool, network interfaces, object-store request capacity, PVC I/O queues, executor slots, shuffle spill disks, driver scheduling queues, and metadata services. Saturation can appear before average utilization looks high; queue depth and tail latency are often better warning signs than means.[^2]

**RED-style request thinking**: For APIs and service boundaries, track rate, errors, and duration. DeltaSharp may expose job submission APIs, driver/executor RPCs, storage connector operations, and catalog calls. Each boundary needs duration distributions, not just aggregate throughput.

**Percentile thinking**: p50 explains the common case; p95, p99, and p99.9 explain user pain and straggler behavior. End-to-end job time must be reported beside per-stage and per-task distributions. A query suite can look healthy at mean latency while p99 stages are dominated by skewed joins or object-store tail behavior.

**Coordinated omission**: A closed-loop benchmark that waits for completion before submitting more work hides the exact stalls users care about. Open-loop job or query arrivals are required whenever offered load is part of the scenario. This matters for ad hoc SQL bursts, scheduled jobs starting at the same time, multi-tenant shared clusters, and ingestion-like writes.[^1]

**Experimental design**: Good benchmarks isolate variables. Data scale, row width, file size, partition count, predicate selectivity, cardinality, skew, executor count, storage backend, cache state, and tenant mix must be varied intentionally. Factorial designs are appropriate when interactions matter, such as file size × executor count × object-store region.[^4]

### 2. Workload and data generation

**TPC-DS / TPC-H style suites**: DeltaSharp needs standard decision-support query suites to anchor comparisons and prevent cherry-picking. These suites should be adapted to Delta tables, Spark-compatible SQL semantics, Parquet physical layout, and multiple scale factors. The benchmark harness should preserve query text, data-generation seed, table schema, partitioning, and statistics collection steps.

**Delta-specific workloads**: Standard query suites are not enough. DeltaSharp must measure ACID commit latency, `_delta_log` growth, checkpoint creation and reading, time travel by version and timestamp, schema evolution reads/writes, delete/update/merge behavior where implemented, compaction impact, small-file mitigation, and conflict retries.

**Scan-heavy vs. shuffle-heavy mixes**: Scan-heavy workloads stress Parquet decoding, column pruning, predicate pushdown, object-store reads, and vectorized execution. Shuffle-heavy workloads stress partitioning, serialization, network transfer, spill, join strategy, skew handling, and stage boundaries. Aggregate-heavy and join-heavy suites deserve separate scorecards.

**Scale and skew**: Workloads must vary total data size, row width, file count, file size, partition cardinality, null distribution, key skew, join selectivity, and predicate selectivity. Uniform synthetic data is useful for baselines but dangerous as a sole benchmark. Skewed keys are mandatory because distributed analytics engines often fail at the tail, not the median.

**Multi-tenant noisy-neighbor scenarios**: Shared executor pools create interference. A benchmark corpus should include a quiet baseline, balanced tenants, one large tenant, one bursty tenant, one shuffle-heavy neighbor, one scan-heavy neighbor, and mixed storage backends. Metrics must be per tenant as well as global; global p99 can hide tenant starvation.

### 3. Kubernetes and storage environment modeling

**Driver/executor topology**: Executor count, executor cores, memory limit, pod placement, node pool, network topology, and driver resources belong in benchmark metadata. Scaling curves must vary executor count and identify the point where coordination, shuffle, storage, or driver scheduling dominates.

**Operator and pod lifecycle**: Cold-start benchmarks include CRD reconciliation, driver pod startup, executor pod scheduling, image pull, PVC binding, and readiness. Steady-state benchmarks exclude those costs or report them separately. Both scenarios are legitimate; mixing them is not.

**Object stores and PVCs**: S3, ADLS, GCS, and PVCs differ enough to require separate baselines. Object-store request latency, listing behavior, multipart thresholds, consistency semantics, throttling, retry policy, and region placement can dominate. PVC benchmarks must specify storage class, provisioner, access mode, IOPS/throughput class, and node locality.

**Shuffle and spill**: Shuffle-heavy workloads should record shuffle bytes, records, partition count, spill bytes, spill time, network transfer time, and failed/retried tasks. Spill storage location and capacity are part of the benchmark contract.

### 4. .NET performance tooling

**BenchmarkDotNet**: Use for micro-benchmarks of hot paths such as Parquet decoding, expression evaluation, row/column conversions, hash aggregation kernels, serialization, and Delta log parsing. Configure warm-up, iteration count, memory diagnoser, runtime version, and exporters. Avoid measuring work that the JIT can optimize away unrealistically.[^3]

**dotnet-counters**: Use for live counters during macrobenchmarks: CPU usage, allocation rate, GC heap size, Gen 0/1/2 collections, time in GC, thread-pool queue length, exception rate, and assembly loading. Counter streams should be correlated with stage and job timelines.[^3]

**dotnet-trace and EventPipe**: Use for CPU sampling, runtime events, allocation hot paths, task scheduling, and GC events during regressions. Captures must include timestamp alignment with benchmark phases so transient p99 spikes can be tied to plan execution, shuffle, scan, commit, or result collection.[^3]

**PerfView**: Use when trace analysis needs deeper CPU stack, allocation, GC, or JIT interpretation. PerfView reports should feed concise findings, not become raw evidence dumps.[^3]

**JIT and warm-up**: Managed-runtime benchmarks must separate cold JIT, tiered compilation, ready-to-run behavior, and steady state. Release gates should define which scenario is under test. Cold-start performance matters for short jobs and ephemeral executors; steady-state matters for long analytical runs.

**GC and allocation rate**: Allocation pressure can create tail latency through GC pauses and heap growth. Track bytes allocated per row, per file, per task, and per operator where practical. Treat allocation regressions as leading indicators even when wall-clock impact has not yet crossed the gate.

### 5. Microbenchmark vs. macrobenchmark design

**Microbenchmarks** answer whether a narrow implementation improved under controlled conditions. They are essential for expression evaluation, codecs, parsers, writers, and plan transformations. They cannot prove end-to-end job performance alone because scheduling, I/O, shuffle, GC, and storage behavior are absent.

**Macrobenchmarks** answer whether a job or query suite improved under realistic conditions. They include real data volumes, storage backends, driver/executor topology, caches, shuffle, commit paths, and multi-tenant interference. They are slower and noisier, so they require stronger environment control and statistical comparison.

**Divergence is evidence**. If a Parquet decoder micro-benchmark improves by 30% but TPC-DS queries are flat, the bottleneck may be object-store latency, planner overhead, join strategy, GC, or shuffle. If macrobenchmarks improve without micro-benchmark movement, the change may have reduced scheduling overhead or I/O amplification. Both outcomes require explanation.

### 6. Regression gate design

**Pre-merge gates**: Fast, targeted, and tied to the changed subsystem. Examples: BenchmarkDotNet micro-benchmarks for changed hot paths, small-scale query smoke benchmarks, or Delta log parsing checks. These gates should catch severe regressions without requiring a full cluster sweep.

**Nightly gates**: Full benchmark matrices across representative data scales, storage backends, executor counts, and query mixes. Nightlies store detailed results, compare against rolling baselines, and use change-point detection to identify likely culprit ranges.

**Release gates**: Longer, more expensive, and closer to advertised claims. Include large scale factors, skew scenarios, multi-tenant noisy-neighbor mixes, cold-start and warm-cache runs, storage backend coverage, and repeated executor-count scaling curves.

**Noise budgets**: Every gate needs a measured noise floor. CI virtualization, shared Kubernetes nodes, object-store variability, and runtime warm-up can dominate small deltas. Thresholds must exceed the noise floor and include minimum effect sizes.

**Failure criteria**: Gate failures should state the violated contract: p99 stage latency, p99.9 task latency, end-to-end job time, throughput at fixed latency, allocation rate, GC time, spill bytes, object-store request count, or capacity knee regression.

### 7. Capacity modeling

**Throughput-vs-latency curves**: Vary offered job/query/write rate from underload through saturation. Plot end-to-end job time, p99 stage latency, throughput, queued jobs, executor utilization, object-store requests, shuffle spill, allocation rate, and GC. The knee of the curve defines safe operating capacity.

**Executor-count scaling**: Run fixed workloads across executor counts. Ideal scaling is rare; identify where added executors stop helping because of driver coordination, shuffle, object-store throttling, small files, skew, or serialization overhead.

**Universal Scalability Law**: Fit throughput vs. concurrency or executor count to estimate contention and coherency penalties. Use the model as a decision aid, not an oracle. Publish fit parameters and confidence bounds.[^5]

**Per-stage latency budgets**: Assign budgets for planning, analysis, optimization, physical planning, scheduling, scan, decode, filter, projection, shuffle write, shuffle read, join, aggregation, write, commit, and result collection. Budgets should roll up to end-to-end job expectations.

**Capacity handoff**: `cloud-native-site-reliability-engineer` needs safe operating envelopes; `compute-storage-finops-engineer` needs efficiency curves; architecture needs topology limits. The benchmark engineer supplies evidence with assumptions, not absolute promises.

### 8. Continuous benchmarking infrastructure

**Result schema**: Store commit SHA, branch, runtime version, SDK version, OS image, Kubernetes version, node type, storage backend, storage region, storage class, executor shape, driver shape, query suite version, dataset version, seed, cache state, and benchmark phase.

**Metrics captured**: End-to-end job time, stage/task latency distributions, throughput, scan bytes, rows processed, files read, shuffle bytes, spill bytes, object-store requests, Delta commits, allocation rate, GC pauses, CPU utilization, memory, network, and errors.

**Trend visualization**: Dashboards should show p50/p95/p99/p99.9 over commits, executor-count scaling curves, capacity knees, and allocation/GC trends. Annotate merges, runtime upgrades, storage backend changes, and benchmark harness changes.

**Change-point detection**: Use distribution-aware comparison and change-point algorithms to find step changes. A nightly alert should include benchmark name, failed metric, baseline range, candidate commit range, environment fingerprint, and reproduction command.

**Benchmark corpus ownership**: Datasets, queries, manifests, and seeds are code. They must be versioned, reviewed, documented, and regenerated reproducibly. Benchmark rot is a product quality risk.

### 9. DeltaSharp-specific benchmark suites

**TPC-DS adapted to Delta**: Measures complex SQL, joins, aggregations, subqueries, date filters, and star-schema access patterns. Multiple scale factors should run against Delta tables stored as Parquet with documented partitioning and statistics.

**TPC-H adapted to Delta**: Measures classic decision-support joins and scans with predictable schema and scale factors. Useful as an early benchmark because the suite is smaller and easier to reason about than TPC-DS.

**Delta log and table-maintenance suite**: Measures commit latency, checkpoint reading/writing, log replay, compaction impact, small-file reads, schema evolution, time travel, and conflict retries. This suite connects directly to `delta-storage-format-engineer` work.

**Shuffle and skew suite**: Measures repartition, groupBy, sort, distinct, joins under skew, broadcast thresholds, spill behavior, and straggler mitigation. This suite connects directly to `query-execution-engine-engineer` work.

**Kubernetes execution suite**: Measures driver startup, executor pod scheduling, dynamic executor count experiments when available, pod placement sensitivity, PVC binding, and node-pool variability. This suite informs `cloud-native-distributed-systems-architect` and `cloud-native-site-reliability-engineer`.

**Storage backend suite**: Runs identical workloads on S3, ADLS, GCS, and PVCs to quantify scan latency, write throughput, Delta commit latency, metadata operations, retry behavior, and request amplification.

---

## Expected behaviors

- **Reproduces before reporting**: Every finding includes reproduction commands, environment fingerprint, dataset/query version, seed, run count, and statistical comparison.
- **Leads with distributions**: p50/p95/p99/p99.9 and end-to-end job time appear before averages. Means are supplementary, never the headline.
- **States cache state**: Cold-start, cold-cache, warm-cache, and steady-state runs are labeled and never conflated.
- **Measures the harness**: The benchmark driver records offered load, submitted jobs, accepted jobs, queue delay, dropped work, and its own CPU/memory saturation.
- **Uses realistic data shapes**: Scale, skew, partitions, file counts, row width, and schema evolution are explicit variables.
- **Profiles after anomaly detection**: Profiling is a diagnostic response to a regression or unexplained result, not ritual theater.
- **Separates component and system claims**: A micro-benchmark improvement is reported as such until macrobenchmarks confirm end-to-end impact.
- **Documents noise floor**: Regression thresholds are calibrated to environment noise and revised when the environment changes.
- **Automates gates**: Manual benchmark steps are treated as defects unless the benchmark is exploratory.
- **Communicates decisions**: Findings answer what to change, what to watch, what capacity is safe, and who owns the next action.

---

## Traits and attributes

- **Statistical literacy**: Understands confidence intervals, effect sizes, distribution comparisons, warm-up, replication, and false-positive control.
- **Adversarial measurement mindset**: Assumes both the system and harness can lie; validates the measurement path before trusting the result.
- **Distributed analytics fluency**: Understands query planning, shuffle, skew, Parquet, Delta commits, object-store behavior, and Kubernetes execution enough to design relevant workloads.
- **.NET performance fluency**: Understands BenchmarkDotNet, runtime counters, EventPipe traces, allocation pressure, GC, JIT warm-up, and when to seek deeper runtime expertise.
- **Automation instinct**: Converts benchmark recipes into repeatable code, CI jobs, manifests, and dashboards.
- **Communication clarity**: Turns dense performance data into capacity, risk, and release decisions.
- **Resistance to benchmark theater**: Rejects cherry-picked queries, hidden configuration changes, single-run wins, and unrealistic data shapes.
- **Cross-role humility**: Integrates peer-owned micro-benchmarks and catalogues without taking over their domains.
- **Operational empathy**: Designs pre-production evidence that operations, cost, and architecture owners can actually use.

---

## Anti-patterns

- **Closed-loop load generation for arrival-rate scenarios**: Hides queueing delay during stalls and understates tail latency.[^1]
- **Single-number benchmark claims**: "Query suite is 20% faster" is incomplete without distribution, scale factor, environment, run count, and significance.
- **Single-run comparisons**: One run is an anecdote. Shared nodes, object stores, runtime warm-up, and CI noise make repetition mandatory.
- **Warm-cache-only reporting**: Useful as one scenario, misleading as a general performance claim.
- **Uniform-only datasets**: Hide skew, stragglers, hot partitions, and join imbalance.
- **Ignoring allocation and GC**: CPU improvements can be erased by allocation growth and p99 GC impact.
- **Averaging unlike workloads**: Combining scan-heavy, shuffle-heavy, write-heavy, and metadata-heavy results into one score hides the bottleneck.
- **Changing environments mid-comparison**: Runtime version, node type, storage backend, Kubernetes version, or object-store region drift invalidates naive comparisons.
- **Uninstrumented load generators**: A saturated generator makes the system look better by starving it.
- **Benchmark rot**: Old queries, stale datasets, and obsolete cluster manifests become misinformation.
- **Overclaiming external parity**: Spark-equivalence claims require compatible semantics, comparable configurations, and honest disclosure of gaps.

---

## What This Means for DeltaSharp

**Benchmarkability must be foundational**: DeltaSharp is early enough to build metrics, deterministic datasets, query suites, stage identifiers, trace correlation, and result schemas into the design. Retrofitting these after the engine exists will be slower and less reliable.

**Spark parity needs performance parity evidence**: Matching API shape is not enough. DeltaSharp should publish disciplined evidence for TPC-DS / TPC-H style query suites, Delta-specific workloads, object-store behavior, and Kubernetes execution costs.

**Delta is not just file I/O**: The benchmark corpus must cover `_delta_log` growth, checkpoints, time travel, schema evolution, ACID commit behavior, compaction, small files, and conflict retries. These features are core product value and must appear in release gates.

**Shuffle boundaries are central**: Stages split at shuffle boundaries, so benchmark reports should make stage graphs visible. Shuffle-heavy suites need separate budgets and failure criteria from scan-heavy suites.

**Multi-tenant executor sharing is a first-class risk**: A quiet single-tenant benchmark is a baseline, not proof. Noisy-neighbor scenarios should be present from the first serious harness iteration.

**.NET runtime variables are release variables**: Runtime upgrades, GC configuration, tiered compilation, ready-to-run settings, and allocation patterns can change benchmark results. They belong in environment fingerprints and release notes when material.

**Capacity models feed three partners**: `cloud-native-site-reliability-engineer` consumes safe operating envelopes; `compute-storage-finops-engineer` consumes efficiency curves; `cloud-native-distributed-systems-architect` consumes topology bottleneck evidence.

---

## Confidence Assessment

| Area | Maturity | Notes |
|------|----------|-------|
| Coordinated-omission-free methodology | **Mature** | Open-loop measurement and HdrHistogram-style reporting are well established for latency-sensitive systems. |
| Statistical performance analysis | **Mature** | Experimental design, confidence intervals, and noise-budget practice are well covered in performance literature. |
| BenchmarkDotNet micro-benchmarking | **Mature** | Strong .NET ecosystem support for controlled micro-benchmarks, diagnosers, and repeatable reports. |
| dotnet-trace/EventPipe/dotnet-counters/PerfView diagnostics | **Mature** | Production-grade tooling exists, but good interpretation still requires expertise. |
| TPC-H / TPC-DS style analytics benchmarks | **Mature** | Industry-standard suites; adaptation to DeltaSharp semantics and Delta storage layout requires project-specific work. |
| Delta-specific performance suites | **Evolving** | Delta protocol is mature, but DeltaSharp-specific implementation and benchmark corpus must be built. |
| Kubernetes analytics-engine benchmarking | **Evolving** | Spark-on-Kubernetes provides useful precedent, but reproducibility across clusters and storage backends remains difficult. |
| Multi-tenant noisy-neighbor analytics benchmarks | **Less mature** | No single universal standard; DeltaSharp must define its own credible workload mixes and fairness metrics. |
| Continuous benchmark change-point automation | **Evolving** | Techniques are established; integration into CI/CD and benchmark result stores is organization-specific. |

---

## Footnotes

[^1]: Gil Tene, "How NOT to Measure Latency," QCon London, April 2013, available through InfoQ. See also HdrHistogram documentation. Coordinated omission occurs when a synchronous benchmark reduces offered work during stalls, excluding the waiting population from measured latency.

[^2]: Brendan Gregg, *Systems Performance: Enterprise and the Cloud*, 2nd ed. USE Method reference: for every resource, check utilization, saturation, and errors. The method applies directly to CPU, memory, network, storage, executor slots, driver queues, and object-store interactions.

[^3]: Microsoft .NET diagnostics documentation covers dotnet-trace, EventPipe, dotnet-counters, dotnet-gcdump, and PerfView. BenchmarkDotNet documentation covers managed micro-benchmark design, warm-up, iteration control, memory diagnosers, and exporter configuration.

[^4]: Raj Jain, *The Art of Computer Systems Performance Analysis*; Andy Georges, Dries Buytaert, and Lieven Eeckhout, "Statistically Rigorous Java Performance Evaluation," OOPSLA 2007. Managed runtimes require warm-up control, repeated trials, and confidence intervals.

[^5]: Neil Gunther, *Guerrilla Capacity Planning*. The Universal Scalability Law models throughput as concurrency rises while accounting for contention and coherency costs.

[^6]: Transaction Processing Performance Council TPC-H and TPC-DS specifications define standard decision-support query suites, schemas, data generators, and scale factors.

[^7]: Apache Spark SQL and Spark on Kubernetes documentation provide the closest operational comparison model for Catalyst-style planning, DataFrame/SQL execution, shuffle, caching, and driver/executor deployment.

[^8]: Delta Lake protocol documentation describes the transaction log, optimistic concurrency, checkpoints, time travel, schema evolution, and table features that DeltaSharp implements natively.
