# EPIC-12: Structured Streaming (micro-batch)

- **Roadmap milestone:** v1.0 (link to ../../../ROADMAP.md#v10--spark-parity-batch--streaming-on-kubernetes)
- **Primary persona(s):** `structured-streaming-engine-engineer` (+ collaborators)
- **Related ADRs:** ADR-0010, ADR-0004
- **Depends on:** EPIC-04, EPIC-05, EPIC-08
- **Status:** draft
- **Size:** XL

## Objective

Deliver Spark-compatible Structured Streaming for DeltaSharp v1 through a micro-batch execution model that reuses the batch logical, physical, and distributed execution engine. This epic makes streaming queries durable, restartable, stateful, observable, and correct over replayable sources and transactional or idempotent sinks while preserving clean seams for a future continuous-processing engine.

## Scope

**In scope**
- `readStream` / `writeStream` query lifecycle, triggers, `StreamingQuery` control, progress events, and micro-batch planning that incrementally drives the batch engine.
- Streaming source and sink contracts with serializable offsets, replayable input ranges, output modes, epoch IDs, commit/abort semantics, and delivery-guarantee metadata.
- File and rate streaming sources, Kafka source integration through connector contracts, idempotent test sinks, and transactional Delta streaming source/sink support including Change Data Feed offsets.
- Versioned state stores, checkpoint metadata, offset logs, commit logs, watermark logs, recovery, compaction, retention, and object-store/PVC persistence semantics.
- Event-time watermarks, windowed aggregation, stream-stream joins, deduplication, state eviction, late-data accounting, and exactly-once correctness for supported source/sink combinations.

**Out of scope** (and where it lives instead)
- Continuous / record-at-a-time processing execution → post-v1 streaming work / persona `structured-streaming-engine-engineer`.
- Batch physical operator internals, optimizer ownership, and stage/task execution mechanics → EPIC-04, EPIC-08, EPIC-11 / persona `query-execution-engine-engineer` and `dotnet-distributed-execution-engineer`.
- Delta transaction-log protocol internals, Parquet layout, table maintenance, and CDF storage mechanics → EPIC-05 / persona `delta-storage-format-engineer`.
- External Kafka connector protocol implementation beyond the streaming source contract → EPIC-06 / persona `data-platform-connectors-engineer`.
- Kubernetes operator rollout policy and production SLO runbooks → EPIC-10, EPIC-13 / persona `kubernetes-operator-controller-engineer` and `cloud-native-site-reliability-engineer`.

## Exit criteria

- [ ] A streaming query can start, run repeated micro-batches with a configured trigger, report progress, stop gracefully, and reuse the batch engine for each planned batch.
- [ ] Source offsets, committed batches, watermarks, and state versions are checkpointed durably and recover after driver failure without skipping or duplicating committed input.
- [ ] Supported transactional or idempotent sinks provide exactly-once semantics across task retries, duplicate driver commits, and restart replay; unsupported sinks are explicitly labeled at-least-once.
- [ ] Watermarked windowed aggregation, deduplication, and stream-stream join produce Spark-compatible results for on-time, late, duplicate, and out-of-order input.
- [ ] File, rate, Kafka-through-connector, Delta table-version, Delta CDF, and Delta sink paths satisfy their documented replay, schema, and delivery-guarantee contracts.
- [ ] State stores and checkpoints persist on object-store and PVC backends with versioning, compaction, corruption detection, retention, and recovery tests.
- [ ] Streaming interfaces expose seams for a future continuous engine without reworking the batch engine, source/sink contracts, or state-store provider boundary.

## Features

### FEAT-12.1: Micro-batch incremental execution model

- **Objective:** Define the streaming query lifecycle, triggers, progress reporting, and micro-batch planner that turns streaming logical plans into repeatable batch executions with stable epoch IDs.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `query-execution-engine-engineer`.
- **Depends on:** EPIC-04, EPIC-08.

#### Stories

##### STORY-12.1.1: Streaming query lifecycle and trigger controls

- **As a** streaming application author **I want** `StreamingQuery` start, stop, await, and trigger behavior **so that** long-running queries are controllable and observable.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** EPIC-04.
- **Acceptance criteria:**
  - [ ] Given a streaming write plan When `start()` is invoked Then a `StreamingQuery` is created without executing transformations before the start action.
  - [ ] Given processing-time, once, available-now, and manual test triggers When configured Then query execution schedules batches according to documented trigger semantics.
  - [ ] Given a running query When `stop()` or cancellation is requested Then the active batch reaches a safe interruption point and the query reports a terminal state.
  - [ ] Given query progress listeners When each batch completes Then progress includes batch ID, offsets, input rows, processed rows, duration, watermark, state metrics, and sink commit status.
- **Definition of done:** builds/tests/format pass; checklists `15`, `20`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-12.1.2: Incremental micro-batch planner over batch execution

- **As a** streaming engine **I want** analyzed streaming plans lowered to per-batch batch plans **so that** streaming reuses DeltaSharp's existing batch planner and executor.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-12.1.1, EPIC-08.
- **Acceptance criteria:**
  - [ ] Given a streaming logical plan with one or more sources When a new epoch starts Then the planner binds deterministic start and end offsets for that batch.
  - [ ] Given supported stateless transformations When planned for a batch Then the resulting physical plan uses normal batch operators without a forked execution engine.
  - [ ] Given unsupported streaming operators or output modes When analysis runs Then DeltaSharp returns precise unsupported-feature errors before query start.
  - [ ] Given a retried epoch with the same offsets When planned again Then the physical inputs, epoch ID, and sink epoch token are stable.
- **Definition of done:** builds/tests/format pass; checklists `16`, `21`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-12.1.3: Streaming progress, metrics, and explainability

- **As an** operator **I want** streaming progress and explain output **so that** lag, trigger latency, state growth, and sink commits are diagnosable.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** STORY-12.1.2.
- **Acceptance criteria:**
  - [ ] Given `EXPLAIN` on a streaming query When requested Then output identifies streaming scans, stateful operators, watermarks, sink, and micro-batch execution wrapper.
  - [ ] Given completed batches When metrics are emitted Then source lag, trigger delay, planning time, execution time, commit time, and backpressure decisions are recorded.
  - [ ] Given a failed batch When progress is inspected Then the failure reason identifies source, planning, execution, state, checkpoint, or sink phase.
  - [ ] Given metrics from multiple inputs When reported Then per-source offsets and lag are distinguishable.
- **Definition of done:** builds/tests/format pass; checklists `09b`, `09c`, `21`, `08`, `03a`, `04b` satisfied; docs updated if public API changes.

### FEAT-12.2: Streaming sources, sinks, and offset management

- **Objective:** Provide source/sink contracts and initial source implementations that make offsets serializable, replayable, and compatible with exactly-once sink commit protocols where available.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `data-platform-connectors-engineer`.
- **Depends on:** FEAT-12.1, EPIC-06.

#### Stories

##### STORY-12.2.1: Source offset and replay contract

- **As a** connector implementer **I want** a streaming source contract for offsets and replay **so that** every source can deterministically produce an input range for a batch ID.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `data-platform-connectors-engineer`.
- **Size:** M. **Depends on:** FEAT-12.1.
- **Acceptance criteria:**
  - [ ] Given a source implementation When offsets are discovered Then initial, latest, start, and end offsets serialize into a versioned checkpoint envelope.
  - [ ] Given a checkpointed offset range When the source is asked to replay it Then records are produced deterministically or the source reports a non-replayable error before execution.
  - [ ] Given configured max files, rows, bytes, or records per trigger When planning offsets Then the source respects rate limits and reports remaining lag.
  - [ ] Given schema changes between offsets When analysis runs Then the source either applies documented compatible evolution or fails with a schema-change error.
- **Definition of done:** builds/tests/format pass; checklists `19`, `21`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-12.2.2: Sink commit, abort, and delivery guarantees

- **As a** sink implementer **I want** epoch-aware commit and abort contracts **so that** retries and restarts do not duplicate output for transactional or idempotent sinks.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `data-platform-connectors-engineer`.
- **Size:** M. **Depends on:** STORY-12.2.1.
- **Acceptance criteria:**
  - [ ] Given duplicate task attempts for the same epoch When sink output is committed Then only one logical result per task partition becomes visible.
  - [ ] Given a driver crash after sink commit but before checkpoint commit When the epoch is replayed Then an idempotent sink recognizes the prior commit and reports success without duplicate output.
  - [ ] Given an abortable sink When batch execution fails Then uncommitted outputs are cleaned up or marked unreachable by the sink contract.
  - [ ] Given a non-transactional sink When configured Then the query metadata and user-facing diagnostics label the guarantee as at-least-once.
- **Definition of done:** builds/tests/format pass; checklists `19`, `21`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-12.2.3: File, rate, and Kafka-through-connector sources

- **As a** streaming developer **I want** file, rate, and Kafka source paths **so that** DeltaSharp supports local tests, deterministic benchmarks, and common production ingestion.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `data-platform-connectors-engineer`.
- **Size:** L. **Depends on:** STORY-12.2.1, STORY-12.2.2.
- **Acceptance criteria:**
  - [ ] Given a file source directory When new files arrive Then each eligible file is assigned to exactly one committed batch according to source ordering rules.
  - [ ] Given a rate source with rows-per-second settings When triggers run Then generated offsets and rows are deterministic for tests and benchmarks.
  - [ ] Given a Kafka connector source When partitions advance Then topic, partition, and offset ranges serialize and replay through the common source contract.
  - [ ] Given source-specific credentials or options When query metadata is logged Then sensitive values are redacted while non-sensitive diagnostics remain visible.
- **Definition of done:** builds/tests/format pass; checklists `19`, `21`, `08`, `03a`, `04b` satisfied; docs updated if public API changes.

### FEAT-12.3: State store and persistence

- **Objective:** Implement versioned, persistent state stores that align state versions with committed batch IDs and recover safely across object-store and PVC-backed deployments.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Depends on:** FEAT-12.1, EPIC-08, ADR-0004.

#### Stories

##### STORY-12.3.1: Versioned state store provider contract

- **As a** stateful operator author **I want** a versioned state store API **so that** operators can read committed state and write a new batch version atomically.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Size:** L. **Depends on:** STORY-12.1.2.
- **Acceptance criteria:**
  - [ ] Given a committed state version N When batch N+1 starts Then operators read only version N and write to an isolated pending version.
  - [ ] Given put, get, delete, range, prefix, and iterator operations When applied to typed keys and values Then results are deterministic across retries.
  - [ ] Given duplicate or speculative task attempts When state updates commit Then only the winning attempt contributes to the committed version.
  - [ ] Given an unsupported provider capability When a stateful operator requires it Then planning fails with a precise provider-capability error.
- **Definition of done:** builds/tests/format pass; checklists `21`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-12.3.2: State persistence on object-store and PVC backends

- **As an** operator **I want** state persisted outside executor memory **so that** driver or executor failure does not lose committed streaming state.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Size:** L. **Depends on:** STORY-12.3.1, EPIC-08.
- **Acceptance criteria:**
  - [ ] Given object-store persistence When a state version commits Then durable files and manifests do not rely on unsafe atomic directory rename assumptions.
  - [ ] Given PVC-backed persistence When a pod is replaced Then committed versions remain discoverable according to documented placement and retention rules.
  - [ ] Given partial writes, missing files, or checksum mismatches When state loads Then the provider detects corruption and recovers the latest complete version or fails safely.
  - [ ] Given state snapshots and changelogs When compaction runs Then active state remains readable and obsolete files become retention-eligible.
- **Definition of done:** builds/tests/format pass; checklists `21`, `10`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-12.3.3: State lifecycle, TTL, and metrics

- **As an** operator **I want** state lifecycle controls and metrics **so that** unbounded state growth is visible and bounded by policy.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** STORY-12.3.2.
- **Acceptance criteria:**
  - [ ] Given TTL or watermark eviction policy When a batch commits Then expired keys are removed from the next committed state version.
  - [ ] Given state compaction thresholds When thresholds are exceeded Then compaction is scheduled without blocking unrelated stateless queries.
  - [ ] Given progress reporting When a stateful batch completes Then rows, bytes, versions, compaction time, and eviction counts are emitted.
  - [ ] Given configured memory and storage limits When a state store exceeds them Then the query applies backpressure or fails with an actionable limit error.
- **Definition of done:** builds/tests/format pass; checklists `21`, `08`, `22`, `03a`, `04b` satisfied; docs updated if public API changes.

### FEAT-12.4: Watermarks and stateful streaming operators

- **Objective:** Implement event-time watermarks and stateful operators for windows, joins, and deduplication with explicit late-data semantics and state eviction rules.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `query-execution-engine-engineer`.
- **Depends on:** FEAT-12.3.

#### Stories

##### STORY-12.4.1: Event-time watermark semantics

- **As a** streaming query author **I want** event-time watermarks **so that** late data handling and state cleanup are deterministic and Spark-compatible.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** FEAT-12.3.
- **Acceptance criteria:**
  - [ ] Given a query with an event-time column and delay When batches advance Then the persisted watermark never moves backward.
  - [ ] Given late records older than the watermark When processed Then records are dropped or routed according to documented operator semantics and counted in metrics.
  - [ ] Given multiple streaming inputs When watermarks combine Then the global watermark follows documented min/max policy for the operator type.
  - [ ] Given query restart When recovery completes Then the last committed watermark is restored before new offsets are planned.
- **Definition of done:** builds/tests/format pass; checklists `15`, `21`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-12.4.2: Watermarked windowed aggregation

- **As an** analytics developer **I want** tumbling and sliding window aggregations **so that** streaming metrics can be computed over bounded event-time windows.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-12.4.1, STORY-12.3.1.
- **Acceptance criteria:**
  - [ ] Given tumbling and sliding windows When on-time records arrive Then aggregates match equivalent Spark-compatible batch results over the same windows.
  - [ ] Given append, update, and complete output modes When configured Then emitted rows follow each mode's documented watermark and state requirements.
  - [ ] Given late records after a window is finalized When processed Then finalized results are not duplicated or mutated incorrectly.
  - [ ] Given state eviction after watermark advancement When old windows expire Then state rows are removed and eviction counts are reported.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `21`, `08`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-12.4.3: Stream-stream join and deduplication

- **As a** streaming application author **I want** stream-stream joins and deduplication **so that** common enrichment and de-dup workflows work on unbounded input.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-12.4.1, STORY-12.3.1.
- **Acceptance criteria:**
  - [ ] Given two keyed streams with bounded event-time conditions When joined Then output rows match Spark-compatible inner-join semantics for on-time records.
  - [ ] Given join state older than both input watermarks When eviction runs Then expired join state is removed without dropping future-matchable rows.
  - [ ] Given duplicate records within a deduplication key and watermark horizon When processed Then only the first committed occurrence is emitted.
  - [ ] Given missing watermarks for a state-growing join or dedup plan When analysis runs Then DeltaSharp rejects the plan with an error explaining the required watermark.
- **Definition of done:** builds/tests/format pass; checklists `15`, `16`, `21`, `08`, `03a`, `04b` satisfied; docs updated if public API changes.

### FEAT-12.5: Checkpointing, recovery, and exactly-once correctness

- **Objective:** Define durable checkpoint layout and commit ordering that ties offsets, state, watermarks, and sink commits into a restartable exactly-once protocol for supported combinations.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `reliability-test-chaos-engineer`.
- **Depends on:** FEAT-12.2, FEAT-12.3, FEAT-12.4.

#### Stories

##### STORY-12.5.1: Checkpoint metadata layout and commit ordering

- **As a** streaming runtime **I want** a versioned checkpoint layout **so that** recovery can identify the latest fully committed batch and ignore partial work.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `reliability-test-chaos-engineer`.
- **Size:** L. **Depends on:** FEAT-12.2, FEAT-12.3.
- **Acceptance criteria:**
  - [ ] Given a completed batch When checkpoint files are written Then query metadata, offsets, state version, watermark, sink commit, and commit log records are durably correlated by batch ID.
  - [ ] Given a crash before sink commit When recovery runs Then the batch is replayed from the prior committed offsets and no checkpoint marks it complete.
  - [ ] Given a crash after sink commit but before checkpoint commit When recovery runs Then the sink idempotency token is reused and the checkpoint advances only after confirming sink success.
  - [ ] Given partial, corrupt, or future-version checkpoint files When loading Then recovery selects the latest valid committed batch or fails with a precise checkpoint error.
- **Definition of done:** builds/tests/format pass; checklists `21`, `04b`, `03a` satisfied; docs updated if public API changes.

##### STORY-12.5.2: Failure recovery and chaos test matrix

- **As a** reliability engineer **I want** adversarial recovery tests **so that** exactly-once claims are validated under realistic crash and storage failures.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `reliability-test-chaos-engineer`.
- **Size:** L. **Depends on:** STORY-12.5.1.
- **Acceptance criteria:**
  - [ ] Given driver crashes before source planning, during execution, after state write, after sink commit, and after checkpoint commit When tests restart the query Then outputs and committed offsets match the exactly-once oracle.
  - [ ] Given executor loss or task retry during a stateful batch When recovery completes Then no duplicate state updates or sink outputs are committed.
  - [ ] Given object-store throttling, delayed listings, and ambiguous write acknowledgments When checkpoint and state writes retry Then recovery remains deterministic.
  - [ ] Given corrupted checkpoint or state files When tests run Then the query either recovers a prior complete version or fails safely without silent data loss.
- **Definition of done:** builds/tests/format pass; checklists `21`, `04b`, `03a` satisfied; docs updated if public API changes.

##### STORY-12.5.3: Exactly-once guarantee matrix and diagnostics

- **As a** platform owner **I want** a delivery-guarantee matrix **so that** users understand which source/sink combinations are exactly-once, effectively-once, or at-least-once.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `reliability-test-chaos-engineer`.
- **Size:** M. **Depends on:** STORY-12.5.1, STORY-12.2.2.
- **Acceptance criteria:**
  - [ ] Given each supported source and sink pair When documented Then its guarantee names replay requirements, sink commit behavior, and known downgrade cases.
  - [ ] Given a query using a weaker sink When started Then progress and query metadata expose the weaker guarantee instead of implying exactly-once.
  - [ ] Given a failed commit or recovery decision When diagnostics are inspected Then the log names the phase, epoch ID, checkpoint path, and recommended operator action.
  - [ ] Given future sink implementations When they register capabilities Then the matrix can be generated or validated from declared source/sink contracts.
- **Definition of done:** builds/tests/format pass; checklists `21`, `19`, `11`, `03a`, `04b` satisfied; docs updated if public API changes.

### FEAT-12.6: Delta streaming source, sink, and Change Data Feed

- **Objective:** Integrate Delta tables as replayable streaming sources and transactional streaming sinks, including table-version offsets, Change Data Feed consumption, schema handling, and idempotent micro-batch commits.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `delta-storage-format-engineer`.
- **Depends on:** FEAT-12.2, FEAT-12.5, EPIC-05.

#### Stories

##### STORY-12.6.1: Delta table-version streaming source

- **As a** streaming reader **I want** Delta table versions consumed incrementally **so that** appends to a Delta table can feed downstream streaming queries.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `delta-storage-format-engineer`.
- **Size:** L. **Depends on:** STORY-12.2.1, EPIC-05.
- **Acceptance criteria:**
  - [ ] Given a Delta table with append commits When a streaming source plans offsets Then each batch maps to stable table-version and file-action ranges.
  - [ ] Given a restart from checkpointed Delta offsets When the source replays Then input files and rows match the original committed batch.
  - [ ] Given metadata, protocol, or incompatible schema changes between versions When planning occurs Then the query applies documented compatible handling or fails with a precise Delta streaming error.
  - [ ] Given vacuum or log retention removing required versions When recovery runs Then the query fails with a retention-aware data-loss error unless an explicit unsafe option is configured.
- **Definition of done:** builds/tests/format pass; checklists `17`, `19`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-12.6.2: Delta Change Data Feed streaming source

- **As a** CDC consumer **I want** Delta Change Data Feed as a streaming source **so that** inserts, updates, and deletes can be processed incrementally with commit ordering.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `delta-storage-format-engineer`.
- **Size:** L. **Depends on:** STORY-12.6.1, EPIC-05.
- **Acceptance criteria:**
  - [ ] Given CDF-enabled Delta commits When the source reads changes Then emitted rows include change type, commit version, and commit timestamp according to Delta CDF contracts.
  - [ ] Given update and delete commits When consumed incrementally Then before/after image behavior matches the supported CDF protocol level.
  - [ ] Given checkpointed CDF offsets When the query restarts Then no committed change rows are skipped or duplicated.
  - [ ] Given CDF disabled, unavailable, or retained-history gaps When planning occurs Then the source fails with an actionable Delta CDF error.
- **Definition of done:** builds/tests/format pass; checklists `17`, `19`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-12.6.3: Delta transactional streaming sink

- **As a** streaming writer **I want** micro-batches committed to Delta transactionally **so that** supported streams write exactly once to Delta tables.
- **Implementer persona(s):** Primary `structured-streaming-engine-engineer`; Collaborators `delta-storage-format-engineer`.
- **Size:** L. **Depends on:** STORY-12.2.2, STORY-12.5.1, EPIC-05.
- **Acceptance criteria:**
  - [ ] Given a micro-batch write to Delta When commit succeeds Then the Delta log records an idempotent transaction keyed by query ID and epoch ID.
  - [ ] Given a replayed epoch after driver restart When the Delta sink observes a prior transaction Then it reports success without adding duplicate files.
  - [ ] Given concurrent table modifications When a streaming batch commits Then Delta conflict detection accepts safe appends and rejects unsafe schema, protocol, or overwrite conflicts.
  - [ ] Given output modes supported by the Delta sink When configured Then append, update, or complete behavior is either implemented with correct transaction semantics or rejected before query start.
- **Definition of done:** builds/tests/format pass; checklists `17`, `19`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

## Open questions

- What are the v1 latency and throughput gates for micro-batch triggers, state-store operations, and recovery time, and should they become EPIC-13 release criteria?
- Which Kafka capabilities are mandatory for v1 beyond source replay, such as headers, timestamp policies, consumer-group integration, and sink support?
- Which state-store provider is the default for v1 deployments, and what object-store/PVC durability and cost trade-offs require a follow-up ADR?
- Which Spark output modes for stream-stream joins and Delta sinks are explicitly supported in v1 versus rejected with compatibility errors?
- What compatibility promise should DeltaSharp make for reusing Spark Structured Streaming checkpoint directories, if any?
