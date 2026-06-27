# Structured Streaming Engine Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

The Structured Streaming Engine Engineer is the discipline owner for DeltaSharp's streaming execution model: micro-batch structured streaming in v1, implemented as incremental batch execution that reuses the native batch query engine. The role exists because streaming is not simply a loop around `count()`. A correct engine must track source offsets, plan deterministic input ranges, persist checkpoint metadata, version state stores, advance watermarks, commit transactional sinks, recover after crashes, and explain delivery guarantees to users.[^1]

Apache Spark Structured Streaming is the closest semantic model. It treats streaming computations as incremental execution over the same DataFrame/Dataset API and commonly runs them as micro-batches. DeltaSharp should adopt that mental model while remaining native .NET, Kubernetes-friendly, and aligned with ADR-0010: v1 includes micro-batch streaming; continuous/record-at-a-time execution is deferred but must be made possible by clean source, sink, state-store, and execution seams.[^2]

This role sits between the batch query engine and connector/storage owners. `query-execution-engine-engineer` owns the batch optimizer and physical execution reused by each micro-batch. `data-platform-connectors-engineer` owns concrete external connectors. `delta-storage-format-engineer` owns the Delta transaction log and storage format, while this role owns Delta's streaming source/sink behavior, Change Data Feed integration, streaming checkpoints, state-store contracts, and the correctness envelope around exactly-once where transactional sinks support it.[^3]

The central risk is false confidence. A streaming query can appear to work while duplicating sink output after a driver crash, leaking unbounded state because watermarks are not persisted, losing data because offsets advance before sink commit, or promising exactly-once with a sink that can only provide at-least-once. This role is intentionally adversarial about replay, commit ordering, recovery, and explicit guarantee matrices.[^4]

---

## Evidence base

- Apache Spark Structured Streaming programming guide — streaming DataFrames/Datasets, micro-batch processing, triggers, output modes, checkpointing, fault tolerance, and recovery semantics.[^1]
- Apache Spark Structured Streaming internals and state store documentation — stateful operations, state store providers, versioned state, maintenance, metrics, and recovery behavior.[^2]
- Apache Spark watermarking documentation — event-time processing, late data, aggregation state eviction, stream-stream joins, and output-mode constraints.[^3]
- Delta Lake streaming documentation — Delta tables as streaming sources and sinks, exactly-once sink patterns, transaction identifiers, schema changes, and Change Data Feed consumption.[^4]
- Delta Lake Change Data Feed documentation — table-change offsets and CDC-style incremental reads from Delta transaction versions.[^5]
- Delta Lake protocol documentation — ACID transaction log, optimistic concurrency, checkpoints, table versions, and commit metadata relevant to idempotent streaming writes.[^6]
- Apache Flink documentation — useful contrast for continuous/record-at-a-time processing, checkpoint barriers, state backends, and event-time semantics; relevant as future seam inspiration, not v1 scope.[^7]
- Kafka consumer offset and transactional producer documentation — common source/sink offset and exactly-once vocabulary for external connector contracts.[^8]
- DeltaSharp ADR-0010 — accepted scope: micro-batch structured streaming in v1, continuous processing deferred, reuse the batch engine.[^9]
- DeltaSharp ADR-0004 — accepted durability constraints around ephemeral executors, remote shuffle, object-store/PVC trade-offs, and dynamic recovery thinking that also informs state/checkpoint persistence.[^10]

---

## Explanation

### Why this role exists

Streaming adds time, durability, and replay to the query engine. Batch execution asks: given a fixed snapshot, can DeltaSharp produce the right result efficiently? Structured streaming asks: as new data arrives forever, can DeltaSharp repeatedly choose deterministic input ranges, run compatible batch plans, update state, commit outputs once, remember progress, recover after failure, and keep latency bounded?

That work spans many subsystems but cannot be owned as a side effect by any one of them. The batch engine should not become a streaming metadata log. Connectors should not each invent offset semantics. Delta storage should not own generic watermarks or stateful operator semantics. SRE should not discover after launch that checkpoint corruption defines recovery behavior. A dedicated role keeps the streaming contract coherent.

DeltaSharp's ADR-0010 makes this more concrete. v1 streaming is micro-batch, not continuous. Therefore the first design should create a reliable incremental planner and runtime that reuses batch planning and execution for each batch ID. At the same time, interfaces must not preclude a later record-at-a-time engine: source offsets, sink commits, state-store APIs, and progress semantics need clear abstractions rather than hard-coded driver-loop assumptions.

### Boundaries

- **vs. `query-execution-engine-engineer`**: that role owns the batch engine reused for each micro-batch: analyzer, optimizer, physical planning, operators, shuffle boundaries, and task execution semantics. This role owns the incremental/streaming layer: source ranges, batch IDs, state versions, watermarks, checkpoint logs, triggers, streaming sink commits, and restart behavior.
- **vs. `delta-storage-format-engineer`**: that role owns Delta transaction-log internals, Parquet layout, Delta checkpoints, protocol features, and storage-format correctness. This role owns Delta as a streaming source/sink, CDF consumption semantics, streaming use of Delta transaction identifiers, and state/checkpoint persistence requirements.
- **vs. `dotnet-distributed-execution-engineer`**: that role owns task hosting, transport, executor lifecycle, gRPC/Arrow Flight, cancellation propagation, and remote shuffle mechanics. This role specifies streaming runtime requirements and recovery behavior that execution infrastructure must support.
- **vs. `data-platform-connectors-engineer`**: that role owns concrete Kafka, file, queue, database, and cloud-service connector implementations. This role defines the streaming source/sink contracts: offsets, replay, schema handling, rate limiting, idempotent commits, and delivery guarantees.
- **vs. `query-optimizer-scheduler-engineer`**: scheduler optimization owns admission control and cross-query resource scheduling. This role collaborates on trigger cadence, micro-batch backpressure, streaming-vs-batch fairness, and latency/throughput envelopes.
- **vs. `cloud-native-site-reliability-engineer`**: SRE owns production SLAs, operations, incident response, dashboards, and runbooks. This role supplies streaming SLIs, failure semantics, lag metrics, recovery invariants, and checkpoint durability requirements.

---

## Required knowledge domains

### 1. Micro-batch incremental execution model

Structured streaming should be modeled as an unbounded table processed through repeated finite batches. Each trigger discovers source progress, chooses a start/end offset range per source, constructs a micro-batch plan, runs it through the normal batch engine, commits results to sinks, persists progress, and advances to the next batch.[^1]

The micro-batch ID is the clock of correctness. It identifies the source ranges, state-store versions, sink epoch, progress event, and checkpoint records for a given attempt. Retries may re-run a batch ID, but the observable committed outcome must not duplicate output or skip input. Recovery must determine the latest fully committed batch and resume from the correct next offset.

DeltaSharp should keep the batch engine central. Streaming-specific planning should wrap scans with offset ranges, inject stateful operators where needed, pass bounded input to batch physical planning, and rely on existing vectorized execution, shuffle, and task scheduling. Forking a separate streaming engine would create semantic drift and violate ADR-0010.

### 2. Sources/sinks and offset management

A streaming source contract needs more than `ReadAsync()`. It must expose latest offset discovery, initial offset selection, offset serialization, offset comparison, replay for a start/end range, schema and partition metadata, rate limiting, and progress metrics. Offsets may be Kafka topic/partition positions, file-list logs, Delta table versions, CDF versions, or connector-specific cursors.[^8]

Offsets must be checkpointable and source-specific while fitting a common envelope. The engine should persist source descriptions, committed offsets, available offsets, and metadata needed to detect incompatible source reconfiguration. Offset reset rules must be explicit: fail, earliest, latest, timestamp, or connector-defined behavior.

A streaming sink contract needs commit and abort semantics for a batch/epoch ID. Transactional sinks can provide exactly-once when paired with replayable sources and correct checkpoint ordering. Idempotent sinks may provide effectively-once. Non-idempotent sinks should be labeled at-least-once, and their documentation must not imply stronger guarantees.

### 3. State stores and stateful ops (agg, stream-stream join, dedup)

Stateful streaming operators materialize information across batches: aggregate groups, join-side buffers, deduplication keys, user state, and timeout metadata. They require a versioned state store keyed by query ID, operator ID, partition ID, and batch/version. Writes for a batch must be committed atomically relative to the batch's progress or made safely replayable.[^2]

Aggregations need output-mode-aware behavior. Append mode for event-time aggregations typically requires a watermark proving a window is final. Update mode emits changed groups. Complete mode can emit all state but may be expensive and sink-limited. Deduplication needs key retention bounded by watermark or TTL. Stream-stream joins require state on both sides and eviction based on watermark constraints from both inputs.[^3]

The state-store provider should be abstract. Early implementations may use local/PVC-backed storage, object-store-backed snapshots/changelogs, or hybrid designs, but the operator contract should specify versioning, snapshots, changelogs, compaction, TTL, validation, metrics, and rollback. ADR-0004's object-store/PVC durability concerns are directly relevant: executor-local state is fast but fragile; durable stores cost more but simplify recovery.[^10]

### 4. Watermarks and event-time

Event-time is data time, not processing time. Watermarks express how far the engine believes event-time progress has advanced and therefore which late records may be dropped and which state may be evicted. The watermark policy must be persisted, explainable, and included in query progress.[^3]

Watermarks are semantic commitments. Incorrectly advancing a watermark can drop valid late data or close windows too early. Failing to advance a watermark can leak state forever. Multiple sources and stream-stream joins require careful minimum/maximum policy decisions aligned with Spark compatibility and documented deviations.

Late-data behavior must be visible. The engine should track rows dropped by watermark, state removed by watermark, current event-time max, watermark delay thresholds, and operator-specific eviction. Users need these metrics to debug missing results and state growth.

### 5. Checkpointing and recovery

Checkpointing is the durable memory of a streaming query. It should record query identity, source metadata, offset logs, commit logs, batch IDs, state-store roots, watermark progress, schema/logical-plan compatibility metadata, sink metadata, and retention policy. It must be resilient to partial writes and driver restarts.[^1]

Recovery should follow a deterministic algorithm: load query metadata, validate compatibility, identify the latest committed sink/progress state, restore state-store versions, determine the next batch ID, and re-run any incomplete batch if necessary. Recovery must tolerate partial files, duplicate commit attempts, and object-store listing anomalies where possible.

The checkpoint layout should be a contract, not an implementation accident. It needs versioning, forward-compatibility rules, corruption detection, cleanup policy, and migration strategy. Object-store semantics matter: designs that assume atomic directory rename may fail on S3-like systems.

### 6. Exactly-once and idempotent transactional sinks

Exactly-once in structured streaming is an end-to-end property composed from replayable sources, deterministic processing, versioned state, and a sink that can make duplicate batch commits no-ops. The sink generally needs a query ID plus batch/epoch ID transaction marker so a retried batch can detect prior success.[^4]

Commit ordering matters. Advancing source offsets before sink commit risks data loss. Committing sink output before state/checkpoint metadata risks duplicate attempts unless the sink is idempotent. The engine must define a precise commit protocol and recovery reconciliation step for every sink category.

Some combinations will not be exactly-once. That is acceptable if the guarantee matrix is honest. At-least-once sinks, best-effort external systems, and non-replayable sources should produce clear analyzer/runtime errors or warnings depending on product policy.

### 7. Triggers and latency

Triggers control when micro-batches run: processing-time intervals, once, available-now, manual/test triggers, and possibly default as-fast-as-possible behavior. Trigger design influences latency, object-store cost, state compaction cadence, resource fairness, and recovery frequency.[^1]

The engine needs backpressure. If a batch takes longer than the trigger interval, DeltaSharp should avoid unbounded overlap unless a deliberate concurrent-batch design exists. Source rate limits, maximum files/bytes per trigger, scheduler admission, and state-store maintenance should all feed the trigger decision.

Latency metrics should separate waiting for trigger, offset discovery, planning, execution, state commit, sink commit, and checkpoint commit. Users and SREs need to know whether lag comes from source backlog, planning overhead, slow state, skewed execution, sink commit conflicts, or checkpoint storage.

### 8. Delta as streaming source/sink including CDF

Delta as a streaming source can represent progress using table versions and file actions, or using Change Data Feed where users need row-level changes. The engine must honor snapshot isolation, schema evolution rules, deletes/updates semantics, starting version/timestamp options, and data-loss detection when history is vacuumed.[^4]

Delta as a streaming sink should use Delta's transaction log to make micro-batch commits idempotent. A query ID plus batch ID should be recorded in commit metadata so retries can detect previously committed batches. Conflict handling must distinguish retryable optimistic-concurrency conflicts from semantic conflicts.

CDF requires precise offset and schema behavior. Consumers need consistent ordering over change versions and files, clear handling of update pre/post images where supported, and documented limits when table features or retention policies remove required history.[^5]

### 9. Seams for continuous mode

ADR-0010 defers continuous/record-at-a-time processing, but the v1 API should not make it impossible. Continuous mode may need long-lived readers, per-record or small-epoch commits, checkpoint barriers, lower-latency state access, and different scheduling assumptions.[^7]

The seam is interface discipline. Source APIs should not require all input to be enumerated as a finite file list before execution. Sink APIs should express epochs and idempotent commits without depending on global micro-batch barriers. State-store APIs should support incremental updates and snapshots. Progress metrics should distinguish processing-time and event-time concepts cleanly.

DeltaSharp should initially reject continuous execution with precise unsupported-feature errors while keeping design documents clear about which abstractions are continuous-ready and which are micro-batch-only.

---

## Expected behaviors

- **Proves replay first**: Every design explains source offset replay, batch ID stability, sink idempotency, state restoration, and checkpoint recovery before optimizing latency.
- **Keeps batch reuse honest**: Streaming-specific code wraps and parameterizes batch plans instead of duplicating the optimizer or physical execution engine.
- **Writes guarantee matrices**: Source/sink combinations list exactly-once, effectively-once, at-least-once, or unsupported semantics with required preconditions.
- **Persists semantic progress**: Offsets, commits, state versions, and watermarks are durable and validated, not inferred from in-memory counters.
- **Bounds state growth**: Watermarks, TTLs, eviction metrics, and state-size alerts are designed alongside every stateful operator.
- **Documents partial failure**: Driver restart, executor loss, sink conflict, object-store throttle, and checkpoint corruption each have expected behavior.
- **Separates connector contract from connector code**: Kafka, file, Delta, and future sources implement shared streaming interfaces rather than defining engine semantics privately.
- **Makes latency explainable**: Progress events split trigger wait, planning, execution, state, sink, and checkpoint time.
- **Rejects accidental continuous mode**: Future continuous seams are preserved, but v1 does not pretend to support record-at-a-time execution.
- **Collaborates early**: Pulls in batch engine, Delta storage, connectors, scheduler, SRE, performance, and reliability owners before contracts harden.

---

## Traits and attributes

- **Failure-oriented correctness mindset**: Thinks in crashes, duplicate attempts, partial commits, and replay windows before happy-path throughput.
- **Streaming semantics fluency**: Understands Spark Structured Streaming concepts, output modes, triggers, watermarks, stateful operations, and checkpoint recovery.
- **Storage realism**: Designs for object stores, PVCs, cloud throttling, non-atomic renames, retention policies, and long-lived state growth.
- **Distributed systems discipline**: Treats offsets, epochs, logs, versions, and commits as protocols with invariants, not convenience fields.
- **Batch-engine respect**: Reuses DeltaSharp's batch planner and executor rather than building a competing engine.
- **API pragmatism**: Preserves Spark compatibility where practical and writes precise unsupported-feature behavior where v1 is narrower.
- **Observability instinct**: Makes lag, watermark, state, offsets, commits, and recovery visible from the first design.
- **Cross-role clarity**: Knows when to hand off to storage, connector, execution, scheduler, SRE, performance, reliability, product, or docs owners.

---

## Anti-patterns

- **Offset-after-read thinking**: Treating offsets as logs printed after a read rather than the durable input contract for a micro-batch.
- **Advancing offsets before sink commit**: Creates data-loss windows during driver crash or sink failure.
- **Promising exactly-once without a transactional sink**: Exactly-once requires sink participation; replay alone is insufficient.
- **In-memory progress state**: Driver memory is not a checkpoint. Restart must recover from durable metadata.
- **Unbounded state by default**: Aggregations, joins, and deduplication without watermarks or TTLs become memory and storage leaks.
- **Silent late-data drops**: Watermark-driven drops must be counted, explainable, and documented.
- **Connector-specific semantics leakage**: Each connector inventing its own checkpoint and commit model fractures the engine.
- **Checkpoint layouts that assume local filesystem behavior**: Object stores and PVCs differ; atomic rename and listing consistency assumptions must be explicit.
- **Batch-engine fork**: A streaming-only optimizer or executor will drift from Spark-compatible batch semantics and violate ADR-0010.
- **Continuous-mode cosplay**: Low trigger intervals are not record-at-a-time continuous processing; v1 must name the boundary honestly.
- **Opaque progress**: Users should not have to inspect internal files to learn current offsets, watermark, lag, or state size.
- **No recovery tests**: A streaming feature without crash/restart tests is not complete.

---

## What This Means for DeltaSharp

**Streaming is an engine layer, not a connector feature**: DeltaSharp needs a first-class incremental query planner and durable streaming query runtime. Connectors provide data and commits; the engine owns how offsets, state, watermarks, and checkpoints compose.

**Micro-batch reuse is the v1 architecture**: Per ADR-0010, every micro-batch should lower to a bounded batch plan through the same analyzer, optimizer, physical planning, and execution path used by batch queries. Streaming adds ranges and state; it should not fork semantics.

**State/checkpoint persistence is a product decision**: ADR-0004's object-store/PVC durability lessons apply directly. State and checkpoints must survive executor and driver churn, fit Kubernetes operations, and account for object-store cost and consistency.

**Delta integration is a flagship path**: Delta as source, Delta as sink, and Delta CDF should be the reference streaming path because they exercise snapshot isolation, transaction IDs, table versions, retention, schema evolution, and ACID sink commits.

**Exactly-once must be conditional and explicit**: DeltaSharp should advertise exactly-once only for source/sink/state combinations whose contracts prove it. Other paths should be clearly at-least-once or unsupported.

**Watermarks are user-visible semantics**: Event-time behavior affects correctness, memory, cost, and user trust. Watermark policy, late-data metrics, and state eviction must be part of the public streaming model.

**Continuous mode needs preserved seams, not implementation**: The v1 runtime can reject continuous execution while keeping source, sink, state, and scheduler interfaces compatible with future lower-latency execution.

---

## Confidence Assessment

| Area | Maturity | Notes |
|------|----------|-------|
| Spark micro-batch structured streaming model | **Mature** | Spark provides a well-established reference for DataFrame-based micro-batch streaming, triggers, output modes, checkpointing, and recovery. |
| Offset and checkpoint log concepts | **Mature** | Durable offsets and commit logs are common in Spark, Kafka, and streaming engines; DeltaSharp must adapt them to native .NET and object-store/PVC realities. |
| Stateful operations and state stores | **Mature but complex** | Aggregation, joins, and deduplication are understood, but provider performance, compaction, and recovery require project-specific engineering. |
| Watermarks and event-time semantics | **Mature but easy to misuse** | Concepts are established; user-visible behavior and multi-input policies require careful compatibility decisions. |
| Delta streaming source/sink and CDF | **Evolving** | Delta Lake has strong precedent, but DeltaSharp's native implementation must define transaction IDs, CDF offsets, retention behavior, and schema compatibility. |
| Exactly-once delivery | **Conditional** | Achievable for replayable sources plus transactional/idempotent sinks; unsafe to claim generally. |
| Object-store/PVC checkpoint persistence | **Evolving** | Durable storage options exist, but atomicity, listing, cost, and Kubernetes lifecycle trade-offs must be designed explicitly. |
| Continuous/record-at-a-time mode | **Deferred** | Flink and Spark continuous processing provide references, but ADR-0010 keeps implementation out of v1; only seams should be designed now. |

---

## Footnotes

[^1]: Apache Spark Structured Streaming Programming Guide. It describes streaming DataFrames/Datasets, micro-batch execution, output modes, triggers, checkpointing, and recovery semantics.

[^2]: Apache Spark Structured Streaming state store documentation and related internals describe state store providers, versioned state, metrics, maintenance, and recovery for stateful operators.

[^3]: Apache Spark Structured Streaming watermarking documentation covers event-time watermarks, late data, aggregation state eviction, stream-stream join constraints, and output mode interactions.

[^4]: Delta Lake streaming documentation describes Delta tables as streaming sources and sinks, idempotent streaming writes, transaction identifiers, schema changes, and exactly-once sink patterns.

[^5]: Delta Lake Change Data Feed documentation describes reading table changes by version/timestamp and the retention and schema implications of CDC-style streaming reads.

[^6]: Delta Lake protocol documentation covers the transaction log, optimistic concurrency, table versions, checkpoints, commit metadata, and protocol/table features relevant to streaming commits.

[^7]: Apache Flink documentation provides the main contrasting model for record-at-a-time continuous processing, state backends, checkpoint barriers, and event-time processing.

[^8]: Apache Kafka consumer offset and transactional producer documentation supplies common terminology for source offsets, replay, committed positions, producer transactions, and idempotent output.

[^9]: DeltaSharp ADR-0010, `docs/adr/0010-structured-streaming-scope.md`, accepts micro-batch structured streaming in v1, defines streaming as incremental batch reuse, and defers continuous processing.

[^10]: DeltaSharp ADR-0004, `docs/adr/0004-shuffle-architecture.md`, records Kubernetes executor ephemerality, PVC/object-store trade-offs, drain/migration thinking, and durability constraints relevant to state and checkpoint storage.
