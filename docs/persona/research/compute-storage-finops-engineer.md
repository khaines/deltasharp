# Compute & Storage FinOps Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

The Compute & Storage FinOps Engineer is the technical owner of DeltaSharp's engineering cost-of-goods model. DeltaSharp is a .NET-native Spark-equivalent system: users build lazy DataFrame, Dataset, and SQL transformations; actions trigger a Catalyst-style planning pipeline; stages split at shuffle boundaries; a driver coordinates executor pods under a Kubernetes Operator; and data lands in native Delta tables backed by Parquet files and `_delta_log` metadata on object stores and PVCs. This role translates that architecture into a precise economic model: what does it cost to run a job, scan a TB, answer a query, compact a table, retain a version for time travel, or serve a tenant for a day?

The role exists because DeltaSharp's cost surface is not a single cloud bill line. Executor pod CPU, memory, and elapsed time interact with Parquet compression, file size, partition layout, Delta checkpoints, object-store LIST/GET/PUT request rates, PVC capacity, shuffle spill, retries, and retention. A design that looks cheap in storage can be expensive in compute; a design that reduces CPU can explode object counts; a design that improves Spark parity can allow queries that scan far more data than users expect. The Compute & Storage FinOps Engineer keeps these trade-offs visible, quantified, and actionable.

The most important economic pattern is coupling. Compression lowers GB-months and scan bytes but consumes writer and reader CPU. Larger files reduce LIST/GET costs and task overhead but can increase scan waste for selective queries. Fine-grained partitioning can reduce scan bytes but create a small-file problem that raises request costs, planning overhead, and compaction demand. Delta time travel is a product capability, but retained historical files and transaction log checkpoints carry real cost. Kubernetes gives elastic execution, but requested CPU/memory, pod startup latency, retry storms, and queueing policy determine the actual unit economics.

Per-tenant attribution is a first-order design requirement. DeltaSharp should be able to join job, plan, stage, task, lineage, table, version, and storage-operation metadata into a cost ledger. Without that ledger, showback becomes a guess, chargeback becomes contentious, budget guardrails are blunt, and product decisions lack credible cost basis. The FinOps Foundation's Inform/Optimize/Operate cycle and FOCUS-style normalized exports provide the practice frame, but DeltaSharp must embed the needed primitives into its engine and metadata design.

---

## Evidence base

- FinOps Foundation Framework — phases, principles, capabilities, KPIs: https://www.finops.org/framework/phases/
- FOCUS (FinOps Open Cost & Usage Specification) — specification overview and column taxonomy: https://focus.finops.org/what-is-focus/
- AWS S3 Storage Classes documentation — storage class properties, minimum durations, durability, and retrieval characteristics: https://aws.amazon.com/s3/storage-classes/
- AWS S3 Pricing page — storage tier rates, PUT/GET/LIST/lifecycle-transition request pricing: https://aws.amazon.com/s3/pricing/
- GCS storage classes — Standard, Nearline, Coldline, Archive, retrieval fees, minimum durations: https://cloud.google.com/storage/docs/storage-classes
- Azure Blob Storage access tiers overview — Hot, Cool, Cold, Archive, Smart Tier, early-deletion penalties: https://learn.microsoft.com/en-us/azure/storage/blobs/access-tiers-overview
- AWS EC2 On-Demand Pricing — instance-hour billing, data-transfer rates, EBS-optimized instances: https://aws.amazon.com/ec2/pricing/on-demand/
- AWS EC2 Reserved Instances — discount mechanics, scoped vs. regional reservations, capacity reservations: https://aws.amazon.com/ec2/pricing/reserved-instances/
- Kubernetes Resource Management documentation — CPU/memory requests and limits, scheduling, QoS classes: https://kubernetes.io/docs/concepts/configuration/manage-resources-containers/
- Kubernetes Persistent Volumes documentation — PV/PVC lifecycle, storage classes, reclaim policy, volume expansion: https://kubernetes.io/docs/concepts/storage/persistent-volumes/
- Delta Lake Protocol documentation — transaction log, actions, checkpoints, protocol evolution: https://github.com/delta-io/delta/blob/master/PROTOCOL.md
- Delta Lake Best Practices — compaction, file sizing, partitioning, VACUUM and retention guidance: https://docs.delta.io/latest/best-practices.html
- Apache Spark tuning guidance — partition sizing, shuffle behavior, caching, serialization, memory pressure: https://spark.apache.org/docs/latest/tuning.html
- Apache Parquet documentation — columnar layout, row groups, compression encodings, predicate pushdown: https://parquet.apache.org/docs/
- Zstandard compression — speed/ratio trade-off and dictionary mode: https://facebook.github.io/zstd/
- J.R. Storment & Mike Fuller, *Cloud FinOps* (O'Reilly, 2nd ed. 2023) — unit economics, showback/chargeback, anomaly detection patterns

---

## Explanation

### Why this role exists

Spark-style systems hide cost behind a friendly API. A user writes `filter`, `join`, `groupBy`, or `write`, and the engine expands that into a plan, stages, tasks, network movement, storage operations, commits, and metadata updates. DeltaSharp must preserve that ergonomic model while making the supply-side economics observable. No adjacent role owns the complete picture. The SRE owns production reliability and provisioning. The query engineer owns planner and execution correctness. The Delta storage engineer owns file format, transaction log, and write path. The performance engineer owns benchmark methodology and efficiency curves. The product manager owns packaging and pricing. This persona owns the cost model that connects all of them.

The role matters most in greenfield design because the cheapest time to add cost primitives is before APIs and metadata schemas harden. Job IDs, tenant IDs, table IDs, logical-plan fingerprints, physical-plan metrics, stage/task counters, file statistics, storage operation counts, and executor pod resource usage should be joinable by design. If they are not, future cost attribution will depend on sampling, log scraping, or provider-bill allocation rules that are too coarse to explain specific jobs or design choices.

### Why DeltaSharp has unique cost dynamics

**Lazy transformations and eager actions concentrate cost at action time.** Transformations build plans without execution, so users may compose expensive operations without noticing. The first action can trigger a full table scan, shuffle, repartition, cache fill, Delta commit, or compaction-like write. Cost guardrails therefore need plan-time estimates before execution and actuals after execution.

**Stage boundaries shape compute cost.** Narrow transformations can run with little network movement; shuffle stages consume CPU, memory, network, and spill storage. Executor pods have requested resources even when tasks are blocked on remote reads or shuffle dependencies. Retry storms multiply task cost and may also duplicate object-store requests.

**Delta storage has both data and metadata cost.** Parquet files store data; `_delta_log` JSON and checkpoint files store transaction history. Time travel requires retaining old files. Schema evolution and commit frequency can increase metadata. VACUUM reduces retained data but must respect safety windows. Checkpoint interval changes can trade metadata scan cost against checkpoint write cost.

**Object-store request cost is material.** Many teams treat object storage as cheap because GB-month rates are low. DeltaSharp must also model LIST, GET, PUT, COPY, lifecycle transition, and delete request counts. The small-file problem can raise request costs, planning latency, and task overhead at the same time. Compaction may have a short payback period even when GB-month savings are small, because it reduces object count and request fan-out.

**PVC economics differ from object-store economics.** PVCs can offer locality, predictable I/O, and lower request overhead, but users pay for provisioned capacity, IOPS, throughput, snapshots, and sometimes unused headroom. Object stores offer elastic durability and lifecycle tiering but charge per request and can have higher latency. DeltaSharp needs a backend-neutral model that still exposes backend-specific cost terms.

**Compression and layout shift cost between compute and storage.** Parquet encodings, dictionary pages, row-group sizing, zstd levels, sorting, clustering, and partitioning can reduce bytes scanned and stored. They can also increase writer CPU, reader CPU, memory pressure, or compaction cost. The right policy depends on query selectivity, retention, hardware pricing, executor sizing, and object-store request rates.

### Boundaries with peer roles

| Peer Role | This persona produces | Peer produces |
|---|---|---|
| `delta-storage-format-engineer` | Cost model for file layout, checkpoints, compaction, retention, and request counts | Delta protocol correctness, Parquet layout decisions, compaction implementation, storage metrics |
| `query-execution-engine-engineer` | Cost guardrail requirements, scan attribution, shuffle cost framing | Logical/physical planning, optimizer rules, query execution, cache semantics |
| `performance-benchmarking-engineer` | Cost-per-unit curves and economic regression gates | Benchmark harnesses, latency/throughput/CPU/scan efficiency curves |
| `cloud-native-site-reliability-engineer` | Capacity forecast, cost anomaly signals, savings-plan assumptions | Operational provisioning, SLOs, alerting, incident response |
| `product-manager` | Cost basis, sensitivity, break-even ranges | Pricing strategy, commercial packaging, customer-facing trade-offs |

---

## Required knowledge domains

### 1. Cloud-storage and PVC cost models

The practitioner must understand object-store pricing at the same level as compute pricing. S3, ADLS, and GCS charge for storage capacity, requests, retrieval, lifecycle transitions, replication, and egress. LIST operations are not free; in S3 they are charged like write-class requests. GET operations accumulate quickly when a query or planner touches many files. Lifecycle transitions can charge per object, so transitioning millions of small files can be worse than leaving data in a warmer class until compaction reduces object count.

Key object-store primitives include bytes stored per storage class, object count, average object size, PUT/COPY count, GET count, LIST count, lifecycle transition count, delete count, cross-region replication factor, cross-region egress, and minimum-duration penalty risk. DeltaSharp should meter these by table, version range, tenant, job, and storage backend where feasible.

PVCs have a different shape. A PVC bill often follows provisioned capacity and performance class rather than actual bytes used. A job that needs 2 TB of shuffle spill for one hour may force capacity that remains allocated if not reclaimed. Snapshots, expansion, reclaim policy, and storage-class parameters affect both cost and operational risk. For DeltaSharp, PVC cost is relevant for executor local spill, durable shuffle designs, local Delta tables, development clusters, and deployments where object stores are not the primary storage layer.

A mature model compares backends by workload: append-heavy writes, metadata-heavy planning, scan-heavy analytics, compaction, time-travel reads, and recovery. The goal is not to declare one backend cheaper universally, but to identify the backend and policy that minimizes total cost for a workload while preserving correctness and SLOs.

### 2. Kubernetes executor compute cost models

DeltaSharp's distributed execution runs through driver and executor pods. Pod economics start with requested CPU and memory multiplied by elapsed time, but real cost requires more detail: task parallelism, queue time, idle time, pod startup latency, image pull latency, JVM-free .NET startup behavior, CPU throttling, memory pressure, GC pauses, shuffle spill, network transfer, retries, and failed attempts.

For each job, the cost model should separate driver cost from executor cost. Driver cost includes planning, scheduling, metadata reads, result collection, and coordination time. Executor cost includes task CPU, reader/writer CPU, shuffle, decompression, serialization, spill, cache fill, and commit participation. Failed tasks should be charged to retry waste and surfaced as a reliability-cost signal.

Reserved commitments, savings plans, on-demand capacity, and spot/preemptible pools should be modeled as rate inputs. Steady interactive workloads may justify committed capacity. Burst compaction or backfill workloads may be spot-eligible if they checkpoint progress and tolerate interruption. Low-latency actions with user-visible SLAs usually need reliable capacity. The FinOps role provides the economic model; SRE operates the capacity plan.

### 3. Delta, Parquet, and file-layout economics

DeltaSharp's storage economics are dominated by file layout. Parquet row-group size, target file size, compression codec, statistics, min/max values, partition columns, clustering, and deletion vectors all change the ratio between bytes stored, bytes scanned, files touched, and CPU spent. Delta log checkpoint interval and commit frequency change planning and metadata scan costs.

Target file size is a central lever. Files that are too small increase LIST/GET requests, metadata scans, task scheduling overhead, and transaction-log actions. Files that are too large increase scan waste and reduce parallelism for selective queries. The FinOps model should express an acceptable file-size band by table class and workload, then compute the payback period for compaction.

Compaction ROI should include: executor pod cost for reading and rewriting files, object-store GET/PUT/COPY cost, additional Delta commit/checkpoint cost, temporary storage, data rewritten, request-count savings, future planning-time reduction, future query-task reduction, and lower metadata growth. The payback window differs for hot, frequently queried tables versus cold, rarely queried tables.

Partitioning is equally economic. Over-partitioning creates directories and files that raise request cost and planning overhead. Under-partitioning scans too much data. Good DeltaSharp guidance should tie partition choices to query predicates, tenant isolation, write batch size, and expected table growth rather than copying generic rules.

### 4. Compression economics

Compression is never free. Parquet compression and encoding can lower storage and scan bytes while raising CPU, memory, and latency. zstd level changes are especially easy to overfit: a higher level may reduce GB-months but increase write CPU enough to raise total cost for short-retention or frequently rewritten data.

The FinOps model should separate writer CPU, reader CPU, storage saved, scan bytes avoided, cache effects, and cold-query latency. It should also distinguish default table policy from table-specific policy. A table queried hourly with selective predicates may favor faster decompression and richer statistics. A table retained for years and queried rarely may favor denser compression if recall latency allows.

Parquet's columnar structure makes per-column thinking important. Low-cardinality string columns may benefit from dictionary encoding; high-cardinality columns may not. Sorted or clustered data may improve run-length and delta encodings. Nested structures may carry overhead. The role does not implement encoding policy, but it should require cost curves from storage and benchmark peers before approving broad defaults.

### 5. Storage tiering, retention, and time travel

Delta time travel changes retention economics. Old Parquet files cannot be deleted while users need historical versions or while safety windows protect against failed writers and stale readers. VACUUM policy must balance storage cost against correctness, recovery, audit, and product expectations. The FinOps role quantifies the cost of each extra day of retention by table class and tenant.

Tiering policy should model hot, warm, cold, and archive states. Hot data needs low-latency reads and frequent writes. Warm data supports common historical queries. Cold data is accessed rarely but still may need bounded response times. Archive data may support legal hold or long-range historical access with high latency. A tiering transition is economic only if storage savings exceed request, transition, retrieval, minimum-duration, and operational costs.

The model must account for Delta metadata separately from data files. Transaction logs and checkpoints are small relative to data in many cases, but high commit frequency or many small files can make metadata material. Checkpoint placement can reduce planning cost at the expense of periodic writes. These trade-offs should be visible before defaults are chosen.

### 6. Unit-economics modeling

Core DeltaSharp unit costs should include:

- **Cost-per-job:** driver cost + executor pod cost + shuffle/spill storage + storage requests + output write cost + retry waste.
- **Cost-per-TB-scanned:** read executor CPU + decompression CPU + object-store GET/LIST cost + cache cost + scan bytes from object store/PVC.
- **Cost-per-query:** planning cost + metadata cost + scan cost + shuffle cost + result materialization + cache effects.
- **Cost-per-Delta-table-version:** data files written + log entries + checkpoint amortization + retained historical files + metadata requests.
- **Cost-per-tenant-per-day:** tenant-attributed compute, storage, requests, table growth, query load, and shared overhead allocation.
- **Cost-per-compaction:** pods + requests + bytes rewritten + temporary capacity, offset by future storage/request/planning savings.

Each unit cost should decompose into drivers: file size, object count, compression ratio, partition selectivity, query selectivity, executor utilization, retry rate, storage tier mix, and backend price. Decomposition is what turns a bill into engineering action.

### 7. Per-tenant and per-job attribution methodologies

Attribution should start with a canonical cost ledger. Each record should be joinable across job ID, tenant ID, user or service principal, table ID, table version, plan fingerprint, stage ID, task attempt, executor pod, storage backend, and operation type. The ledger should support both estimated costs before execution and actual costs after completion.

Three attribution approaches are practical:

1. **Exact metering for material operations.** Track CPU time, memory time, bytes read/written, request counts, and storage growth at job/stage/table granularity. Highest accuracy, highest implementation cost. Best for large tenants, expensive jobs, and chargeback-grade reporting.
2. **Metadata-based attribution.** Use Delta file statistics, table ownership, lineage metadata, and storage inventory to allocate storage and request costs. High accuracy for storage; moderate accuracy for shared compute.
3. **Sampling and allocation factors.** Sample detailed operations, then allocate shared costs by bytes, CPU, task time, or table ownership. Lower overhead; acceptable for showback or small tenants when error bounds are explicit.

The model must also allocate shared overhead: drivers, catalog reads, metadata caches, compaction serving multiple tenants, idle warm capacity, and control-plane components. Allocation rules should be documented and stable enough for audit.

FOCUS-style exports should map internal records into normalized fields such as effective cost, resource, service, region, usage quantity, usage unit, and charge category. DeltaSharp does not need to become a billing system, but its cost data should be portable to standard FinOps workflows.

### 8. Capacity forecasting

Capacity forecasting combines compute and storage. Compute forecasts should project executor CPU-hours, memory-hours, driver hours, queue depth, pod count, spot-eligible batch work, retry overhead, and peak concurrency. Storage forecasts should project Parquet bytes, Delta log bytes, object count, request count, PVC capacity, shuffle spill, snapshots, and replication.

The time horizons should be explicit: 30 days for tactical capacity, 90 days for commitment and storage-class decisions, 180/365 days for product and budget planning. Forecasts should include confidence intervals and scenarios: tenant growth, new connector onboarding, increased retention, schema changes, backfills, higher concurrency, and region expansion.

For Kubernetes, headroom has cost. Over-reserving executor memory prevents failures but can waste cluster capacity. Under-reserving triggers OOM retries that increase cost and reduce reliability. The model should quantify the cost of headroom against the cost of failure for each workload class.

### 9. FinOps Foundation framework

The FinOps Foundation's Inform/Optimize/Operate cycle maps cleanly to DeltaSharp:

- **Inform:** collect cost and usage by tenant, job, table, backend, and region; produce unit-cost dashboards; normalize provider bills and internal metrics.
- **Optimize:** prioritize compaction, compression, partitioning, caching, tiering, executor sizing, and commitment opportunities by ROI.
- **Operate:** make cost a regular engineering signal through design reviews, capacity reviews, budget alerts, guardrails, and regression gates.

The Inform phase is gated by metadata quality. The Optimize phase is gated by trusted unit costs. The Operate phase is gated by clear ownership and safe guardrails. A cost dashboard without design feedback loops is passive reporting, not FinOps.

Important KPIs include cost-per-job, cost-per-TB-scanned, cost-per-query, cost-per-tenant-day, request cost per table, average files per table partition, compaction payback period, executor utilization, retry-waste percentage, tiering savings ratio, storage growth forecast error, and reserved/spot coverage.

### 10. FOCUS specification for cost data normalization

FOCUS provides a vendor-neutral structure for cloud and platform cost data. DeltaSharp's internal records should be designed so they can be exported into normalized cost datasets rather than requiring one-off translation for each provider or tenant team. This is especially important for organizations running across S3, ADLS, GCS, PVC-backed clusters, and multiple regions.

Relevant concepts include billed cost, effective cost after discounts, charge category, service name, region, resource identifier, usage quantity, usage unit, consumed quantity, consumed unit, and pricing unit. DeltaSharp-specific resources might include executor pod, driver pod, Delta table, table version, storage backend, and job.

A useful export supports both showback and later chargeback without changing core schemas. Showback can tolerate broader allocation rules; chargeback needs tighter accuracy, documented error bounds, and stable rules.

### 11. Query-cost economics

Query cost is often less visible than storage growth, but it can dominate for interactive analytics. A single broad scan across many years of Delta data can generate thousands or millions of object-store requests, many executor pod seconds, and heavy shuffle. A high-concurrency dashboard can keep hot capacity busy even if each query is individually cheap.

Cost controls should exist at planning and execution time. Planning can estimate files touched, bytes scanned, partition selectivity, shuffle width, expected executor count, and storage tier access. Execution can measure actual bytes, requests, CPU, memory, spill, retries, and output size. The delta between estimate and actual is itself a model-quality signal.

Caching has explicit ROI. Metadata caches can reduce LIST/GET calls. Data caches can reduce scan bytes and latency. Shuffle caches or materialized intermediate results can prevent repeated work. Each cache also consumes memory or storage, so it needs an avoided-cost model and eviction policy tied to workload value.

Query guardrails should be humane and predictable: warnings for expensive plans, approval for large scans, tenant budgets, concurrency caps, and clear error messages. Guardrails must preserve Spark-like semantics while preventing accidental economic harm.

### 12. Small-file and compaction economics

The small-file problem deserves its own cost model. Many small Parquet files create more Delta log entries, more file metadata, more object-store requests, more tasks, more scheduler overhead, worse compression, and poorer read locality. They also increase the cost of metadata listing and planning before a single row is read.

Compaction is not automatically good. It reads and rewrites data, consumes executor pods, creates new files, writes a Delta commit, may trigger checkpoints, and can contend with user workloads. It pays off when future savings in request cost, planning cost, query cost, and storage efficiency exceed the compaction cost within an acceptable window.

A compaction ROI report should include current file-size distribution, object count, queries per day, files touched per query, GET/LIST cost, task count, scan waste, proposed target size, bytes rewritten, pod cost, request cost, expected savings, and payback period. For cold tables with low query frequency, compaction may be less urgent unless object-count costs or metadata growth are severe.

### 13. Retention-cost economics

Retention is one of the highest-leverage cost knobs. Each additional day of retaining Parquet files, Delta logs, checkpoints, and snapshots adds cost. Time travel and legal hold add value, but the value must be explicit. A default retention window should have a quantified cost and a clear owner.

VACUUM policy connects storage cost to correctness. Too aggressive a policy can break stale readers or remove needed historical versions. Too conservative a policy retains data indefinitely. The FinOps role should quantify the cost of each safety window while the relevant correctness and policy owners set the minimum safe bounds.

Deletion is not free in practice. Delete operations, log growth, compaction after deletes, retention of tombstones, and downstream cache invalidation all carry costs. The model should treat erasure and retention compliance as costed operations, not assumptions.

---

## Expected behaviors

- Maintains a living unit-cost model updated as provider rates, executor sizing, compression ratios, workload mix, and storage classes change.
- Adds cost-impact analysis to designs affecting shuffle, executor sizing, partitioning, compaction, file sizing, caching, retention, tiering, and connector write behavior.
- Requires metering primitives at design time: tenant/job IDs, plan fingerprints, stage/task metrics, file stats, table versions, storage operation counts, and backend identifiers.
- Runs compaction and tiering break-even analyses before recommending defaults.
- Communicates in business-legible units such as cost-per-job, cost-per-TB-scanned, monthly tenant forecast, and compaction payback period.
- Treats cost anomalies as production signals: runaway jobs, retry storms, small-file explosions, unexpected LIST growth, and table growth spikes should alert with owner and mitigation.
- Challenges features that increase retained data, duplicate writes, widen scans, or hide expensive defaults without a quantified cost model.
- Tracks reserved, savings-plan, and spot/preemptible coverage and initiates procurement conversations before expected demand steps up.
- Produces attribution reports accurate enough for internal audit and suitable as a cost basis for product decisions.
- Participates in Inform, Optimize, and Operate; does not stop at passive reporting.

---

## Traits and attributes

- **Quantitative rigor:** Uses both fast estimates and detailed models, with clear precision appropriate to the decision.
- **Systems fluency:** Understands how lazy plans become stages, tasks, pods, storage operations, and Delta commits.
- **Economic skepticism:** Does not accept “object storage is cheap” or “compression saves money” without total-cost proof.
- **First-principles modeling:** Derives costs from bytes, requests, CPU-seconds, memory-seconds, pod-hours, and retained days.
- **Ownership orientation:** Treats the cost model as a production artifact that must remain correct and maintained.
- **Collaborative without surrendering judgment:** Works with engine, storage, SRE, PM, security, compliance, and docs peers while keeping cost consequences explicit.
- **Communication clarity:** Produces models engineers, PMs, and finance partners can read without translation.
- **Long time horizon:** Thinks across retention windows, procurement cycles, table growth, and migration costs.
- **Honest uncertainty:** Publishes confidence intervals, sensitivity bands, and known unknowns.

---

## Anti-patterns

- **Optimizing one category in isolation:** Reducing GB-month storage while increasing executor CPU, request count, or query latency can raise total cost.
- **Ignoring request costs:** LIST and GET across many small files can be a material bill line and a planning-latency driver.
- **Treating compaction as free:** Compaction consumes pods, requests, temporary storage, and write bandwidth; ROI must be proven.
- **Using one target file size for every table:** Workload, partitioning, query selectivity, and retention determine the economic target.
- **Ignoring time-travel retention:** Old files and log history are valuable but costed; retention defaults need economic visibility.
- **Over-partitioning for theoretical pruning:** Fine partitions that produce small files can cost more than they save.
- **Over-committing compute discounts:** Long commitments for workloads that may shift to different instance classes, regions, or execution models can destroy savings.
- **Conflating showback and chargeback:** Informational allocation and billable allocation require different accuracy and governance.
- **Letting metering overhead exceed value:** Per-row cost records or excessive metadata can cost more than the attribution precision is worth.
- **Hiding guardrails:** Unexpected query failures or write throttles erode trust; budget controls must be explainable and predictable.
- **Ignoring cross-region and egress costs:** Replication, remote reads, and customer data movement can dominate otherwise efficient designs.
- **Using stale pricing:** Provider rates and discount programs change; hardcoded assumptions decay.

---

## What This Means for DeltaSharp

**DeltaSharp needs a first-class cost ledger.** The engine should emit or persist enough metadata to attribute executor usage, storage growth, requests, and query scan cost by tenant, job, table, and version. The ledger should connect logical and physical plan fingerprints to actual stage/task costs so teams can compare estimated and actual economics.

**Small files are a launch-critical risk.** Delta tables with many tiny Parquet files will degrade performance and raise request costs. File-count budgets, target file-size bands, compaction triggers, and request-cost dashboards should exist early, not after the first large tenant creates millions of files.

**Compaction should be ROI-driven.** DeltaSharp should avoid both extremes: never compacting and compacting blindly. A good policy uses query frequency, file-size distribution, request costs, planning overhead, and pod cost to decide when compaction pays back.

**Storage backends need explicit economic profiles.** S3, ADLS, GCS, and PVC-backed deployments have different cost equations. The storage abstraction should not hide the metrics needed to compare them: request counts, object counts, capacity, IOPS, lifecycle transitions, snapshots, and egress.

**Plan-time estimates should feed guardrails.** Because transformations are lazy, users need warnings before actions trigger expensive scans, shuffles, or writes. Estimate files touched, bytes scanned, executor resources, shuffle width, and storage tier access before execution when possible.

**Attribution should be designed, not inferred.** Tenant and job identity should flow through API calls, logical plans, physical plans, executor tasks, Delta commits, file statistics, and storage operations. Retrofitting this later will be expensive and less accurate.

**Cost must become a design-review property.** Any ADR touching partitioning, file layout, checkpoints, caching, executor sizing, shuffle, retention, tiering, or connector write patterns should include a quantified cost-impact section.

---

## Confidence Assessment

**High confidence (well-established, stable):**

- FinOps Inform/Optimize/Operate and unit-economics methods are mature practice.
- Object-store pricing includes request, storage, retrieval, lifecycle, and egress dimensions; request costs are material at high object counts.
- Spark-style systems are sensitive to partition size, shuffle, caching, and file layout.
- Delta tables require explicit retention and compaction policies to balance performance, correctness, and storage cost.
- Parquet compression and columnar layout create measurable trade-offs between CPU and bytes.

**Moderate confidence (established but workload-dependent):**

- Optimal target file sizes vary by query pattern, executor sizing, storage backend, and partitioning strategy.
- Compaction payback windows depend heavily on query frequency and object-store request rates.
- Spot/preemptible suitability depends on job checkpointability, retry behavior, and SLA class.
- PVC vs. object-store economics vary by provider, storage class, cluster utilization, and locality.
- Chargeback-grade attribution requirements depend on customer contracts and internal governance.

**Lower confidence (evolving or design-specific):**

- FOCUS continues to evolve; exports should identify the supported specification version and upgrade path.
- Future serverless, disaggregated shuffle, or remote execution designs could materially change compute cost equations.
- Provider egress and discount programs may shift under regulatory or competitive pressure.
- DeltaSharp-specific workload mix is greenfield; early models should publish confidence bands until benchmark and production data exist.

---

## Footnotes

[^1]: FinOps Foundation, "FinOps Framework," https://www.finops.org/framework/ — defines cloud financial accountability practices, including the Inform/Optimize/Operate cycle and unit-economics mindset.

[^2]: FOCUS Project, "What is FOCUS?," https://focus.finops.org/what-is-focus/ — vendor-neutral cost and usage schema for normalizing cloud and platform billing records.

[^3]: Amazon Web Services, "Amazon S3 Storage Classes," https://aws.amazon.com/s3/storage-classes/ and "Amazon S3 Pricing," https://aws.amazon.com/s3/pricing/ — authoritative source for storage classes, minimum durations, request pricing, lifecycle transitions, retrieval fees, and LIST/GET/PUT cost structure.

[^4]: GCS storage classes, https://cloud.google.com/storage/docs/storage-classes — Standard, Nearline, Coldline, and Archive classes, including retrieval fees and minimum storage durations.

[^5]: Microsoft Azure, "Azure Blob Storage access tiers overview," https://learn.microsoft.com/en-us/azure/storage/blobs/access-tiers-overview — Hot, Cool, Cold, Archive, and Smart Tier behavior, including early-deletion penalties and rehydration latency.

[^6]: Amazon Web Services, "Amazon EC2 On-Demand Pricing," https://aws.amazon.com/ec2/pricing/on-demand/ and "Reserved Instances," https://aws.amazon.com/ec2/pricing/reserved-instances/ — instance billing, data transfer, and commitment discount mechanics.

[^7]: Kubernetes, "Resource Management for Pods and Containers," https://kubernetes.io/docs/concepts/configuration/manage-resources-containers/ — CPU and memory requests, limits, and scheduling behavior that shape executor pod economics.

[^8]: Kubernetes, "Persistent Volumes," https://kubernetes.io/docs/concepts/storage/persistent-volumes/ — PV/PVC lifecycle, storage classes, expansion, and reclaim policies relevant to PVC cost models.

[^9]: Delta Lake Protocol, https://github.com/delta-io/delta/blob/master/PROTOCOL.md — transaction log actions, checkpoints, metadata, and protocol evolution that determine Delta metadata and retention economics.

[^10]: Delta Lake Best Practices, https://docs.delta.io/latest/best-practices.html — compaction, file sizing, partitioning, and VACUUM guidance relevant to cost and performance trade-offs.

[^11]: Apache Spark tuning guide, https://spark.apache.org/docs/latest/tuning.html — partition sizing, shuffle, caching, serialization, and memory guidance for Spark-style execution economics.

[^12]: Apache Parquet documentation, https://parquet.apache.org/docs/ — columnar storage layout, row groups, encodings, compression, and predicate pushdown.

[^13]: Meta, "Zstandard compression," https://facebook.github.io/zstd/ — fast compression algorithm with speed/ratio trade-offs and dictionary mode.

[^14]: J.R. Storment and Mike Fuller, *Cloud FinOps: Collaborative, Real-Time Cloud Financial Management* (O'Reilly Media, 2nd ed., 2023) — practitioner reference for unit economics, showback/chargeback, anomaly detection, and commitment optimization.
