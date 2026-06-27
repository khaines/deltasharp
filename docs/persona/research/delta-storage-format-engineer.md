# Delta & Storage Format Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

A world-class Delta & Storage Format Engineer owns the physical truth of DeltaSharp tables: the transaction log, Parquet files, table metadata, maintenance operations, and backend storage contracts that determine whether a Spark-like .NET engine can provide ACID writes, snapshot isolation, time travel, schema evolution, and efficient scans without depending on a JVM runtime. The governing principle is that Delta tables are not just directories of Parquet files. They are versioned state machines whose authoritative state is recorded in `_delta_log`; Parquet files become table data only when referenced by committed log actions, and removed files remain relevant to historical snapshots until retention rules make them safely disposable.[^1][^2]

The role must reason simultaneously about two layers that are often treated separately: the Delta transaction protocol and the Parquet physical format. Delta log design controls atomic visibility, conflict detection, metadata evolution, checkpoint cadence, and time-travel behavior. Parquet design controls scan throughput, predicate pushdown, column pruning, compression ratio, vectorized decoding, and data-skipping quality through row groups, column chunks, encodings, page statistics, and footers.[^3][^4] A storage engineer who understands only the log can create correct but slow tables; one who understands only Parquet can create fast files that are unsafe under concurrent writes.

For DeltaSharp specifically, this persona is foundational because the project intends native Delta support across cloud object stores and Kubernetes PersistentVolumes. Object stores and PVCs expose different failure modes: object stores favor immutable writes and conditional creation but lack atomic directory rename and have higher operation latency; PVCs may offer POSIX-like rename and lower latency but vary by storage class, replication mode, and multi-pod access behavior. DeltaSharp's storage layer must define a small, testable set of required primitives so correctness does not depend on accidental behavior of one backend.[^5][^6]

## Evidence base

This report draws on Delta protocol documentation, Parquet and Arrow specifications, object-store consistency guidance, database-system literature, and production lessons from lakehouse table formats.

- Delta Lake protocol documentation — transaction log actions, protocol versions, checkpoints, metadata, statistics, deletion vectors, column mapping, and reader/writer compatibility.[^1]
- Delta Lake transaction log and concurrency-control documentation — optimistic transactions, conflict checking, isolation semantics, checkpoints, vacuum, and time travel.[^2]
- Apache Parquet file format documentation — row groups, column chunks, pages, encodings, compression, file metadata, and statistics.[^3]
- Apache Arrow columnar format specification — in-memory columnar layout, buffers, validity bitmaps, nested data, and zero-copy interoperability concepts.[^4]
- Cloud object store documentation for S3, ADLS, and GCS — conditional writes, listing consistency, multipart upload behavior, object immutability patterns, and operation latency.[^5][^6][^7]
- Kubernetes PersistentVolume documentation — access modes, reclaim policy, storage classes, volume durability, and filesystem semantics exposed to pods.[^8]
- Apache Iceberg and Apache Hudi documentation — alternative table-format approaches to snapshots, manifests, metadata scaling, and commit protocols.[^9][^10]
- Kleppmann, *Designing Data-Intensive Applications* — durability, concurrency, distributed consistency, snapshots, and failure-mode reasoning.[^11]
- Petrov, *Database Internals* — storage layout, write amplification, indexing, page/block design, checksums, and recovery thinking.[^12]
- Apache Spark SQL and Parquet documentation — column pruning, predicate pushdown, partition discovery, statistics use, and scan planning expectations.[^13]

## Explanation

This role exists because DeltaSharp's claim of native Delta tables depends on storage details that cannot be delegated to the query planner or a generic file abstraction. A naive implementation that writes Parquet files and lists a directory on read will appear to work in single-writer tests, then fail under concurrent writers, overwrite operations, schema changes, stale readers, partial uploads, or object-store retry ambiguity. The transaction log is the serialization point for table state; if commit creation, conflict detection, checkpoint replay, or retention cleanup are wrong, the table can silently lose data or expose inconsistent snapshots.

The role is distinct from the Data Sources & Connectors role, which owns how external systems feed or consume DeltaSharp; from the Query & Execution Engine role, which owns how logical plans become scans, joins, shuffles, and actions; and from the Cloud-Native Distributed Systems Architect, which owns the driver/executor topology and Kubernetes control plane. The Delta & Storage Format Engineer owns the storage contract below those layers: the precise snapshot model, the commit protocol, the Parquet layout, the maintenance operations, and the backend capability matrix.

The role is also distinct from SRE. SRE owns production SLOs, incident response, and operational runbooks. The storage engineer owns the implementation choices that make those SLOs physically possible: bounded checkpoint replay, safe vacuum, predictable commit latency, idempotent maintenance jobs, and file layouts that avoid pathological object-store request amplification.

**Council consensus: unanimous · Criticality: Foundational.** In a greenfield Spark-equivalent engine, the storage layer must be correct before higher layers can be trusted. Fixing a public API mismatch is usually straightforward; fixing a table protocol bug after users have written data may require table repair, migration, or permanent compatibility debt. DeltaSharp needs this persona early, before the first durable table version is produced.

## Required knowledge domains

### 1. Delta transaction protocol: log actions, snapshots, and checkpoints

The engineer must understand Delta tables as versioned state machines. Each committed JSON file in `_delta_log` represents one monotonically increasing table version and contains actions such as protocol, metadata, add-file, remove-file, transaction, commit-info, and domain-specific extensions. A snapshot is reconstructed by replaying actions from the latest usable checkpoint and subsequent JSON commits; active files are the result of add/remove action reduction, not a directory listing.[^1][^2]

Checkpoint design is central to metadata scalability. JSON commit replay is simple and auditable but becomes expensive as table history grows. Periodic Parquet checkpoints compact table state into columnar metadata so readers can construct snapshots without replaying the full log. DeltaSharp must specify checkpoint cadence, multipart checkpoint naming if adopted, checksum behavior, recovery from incomplete checkpoints, and compatibility behavior when a reader encounters protocol features it does not support.[^1]

Protocol versioning is a product contract, not an implementation detail. Reader and writer protocol versions protect older clients from silently misinterpreting tables with features such as column mapping, deletion vectors, generated columns, or new statistics. The storage engineer must define how DeltaSharp upgrades protocols, how it rejects unsupported tables, and how compatibility is tested against golden transaction logs.

### 2. Optimistic commits, conflict detection, and durability

Delta's write model is optimistic concurrency. A writer reads a snapshot, writes data files outside the log, then attempts to create the next log version atomically. If another writer wins first, the losing writer must inspect intervening commits, detect semantic conflicts, and either retry or fail. Append-only writes may commute; overwrites, deletes, metadata changes, partition replacement, protocol upgrades, and compaction can conflict depending on read predicates and touched partitions.[^2]

The hard engineering problem is making "create version N exactly once" portable. Object stores usually support conditional object creation or lease/etag mechanisms, not atomic rename of a prepared file into place. PVC-backed filesystems may support rename, but distributed volume semantics vary. DeltaSharp should express commits in terms of a storage-adapter primitive such as conditional create for a specific log path, then test every backend against that primitive rather than assuming local filesystem behavior.[^5][^8]

Durability also includes retry ambiguity. If a client times out while creating a commit object, the operation may have succeeded even though the caller did not observe success. The commit protocol must be idempotent enough to distinguish "my commit landed" from "someone else won" and must avoid creating duplicate data files as active table state. Orphan files are tolerable when discoverable and vacuumable; duplicate committed data is not.

### 3. Parquet physical format: row groups, column chunks, encodings, and statistics

Parquet is DeltaSharp's data-file format, so the engineer must be fluent in its internals. Files are organized into row groups, each row group contains one column chunk per column, and column chunks are composed of pages encoded and compressed independently. The footer contains schema, row-group metadata, column statistics, encodings, compression information, and offsets that allow readers to skip irrelevant data without scanning the file from the beginning.[^3]

Row-group sizing is a first-order performance decision. Larger row groups improve compression and reduce footer overhead but weaken selective predicate pruning and increase memory pressure during writes. Smaller row groups improve skipping granularity but increase metadata and object-store request pressure. DeltaSharp must choose defaults that match distributed scan planning and executor memory limits, then expose expert tuning without making every user a storage engineer.

Encoding choices affect both speed and size. Dictionary encoding helps low-cardinality columns; run-length and bit-packing help definition/repetition levels and repeated values; delta encodings can help sorted or slowly changing values. Statistics must be computed carefully for strings, decimals, timestamps, nested data, nulls, NaN values, and truncated min/max values. The query engine may use statistics to skip data, but correctness must never depend on a statistic being complete or present.

### 4. File layout, partitioning, data skipping, and clustering

DeltaSharp storage must turn logical table writes into files that are efficient to plan and scan. Partitioning is useful when predicates frequently filter by low- or moderate-cardinality columns and when partition directories remain balanced. Over-partitioning creates small files, expensive listing, and high metadata cardinality; under-partitioning weakens pruning. The storage engineer must define partition value encoding, null partition representation, path normalization, and case-sensitivity behavior.

Data skipping extends pruning below partitions. Add-file actions can record partition values, record counts, file size, min/max/null-count statistics, and optional extended statistics. Z-ordering and clustering place related values near each other so file-level and row-group-level statistics become selective for multidimensional filters. This is only valuable if the writer gathers statistics consistently and if compaction preserves or improves clustering rather than randomizing it.

Small-file handling is unavoidable in a distributed engine. Many tasks can each produce small Parquet files, especially under streaming-like ingestion, partition skew, or low-volume partitions. The storage engineer must define target file sizes, writer coalescing, post-write compaction, OPTIMIZE behavior, bin-packing heuristics, statistics preservation, and how compaction commits remove old files while adding replacement files atomically.

### 5. Time travel, snapshot isolation, and retention

Time travel by version is straightforward when all log and data files required for a version are retained. Time travel by timestamp is subtler: it requires mapping timestamps to commit versions using commit metadata and object timestamps while avoiding ambiguity from clock skew and delayed visibility. DeltaSharp should make the mapping deterministic and document its precision.

Snapshot isolation requires readers to operate against a stable table version while writers continue committing later versions. A reader that planned files from version 100 must not have those files physically removed by vacuum before it finishes. This couples retention policy to operational reality: maximum query duration, streaming reader lag if supported, checkpoint retention, object-store consistency, and compliance requirements.

Vacuum must be conservative. Remove-file tombstones mark files as no longer active for new snapshots, but physical deletion can occur only after the retention interval and only when no supported time-travel or active-reader guarantee needs the file. The engineer must prevent unsafe "vacuum now" shortcuts from becoming easy footguns and must expose dry-run, audit, and recovery guidance.

### 6. Schema enforcement, schema evolution, and column mapping

Schema handling is one of Delta's core differentiators over loose file lakes. Writes must enforce table schema unless explicitly allowed to evolve it. Evolution rules include adding nullable columns, widening compatible types, changing metadata, and rejecting incompatible changes that would make existing files ambiguous. Nullability and decimal/timestamp precision require special care because Parquet physical types and logical annotations must match Delta metadata.[^1][^3]

Column mapping is required when physical Parquet column names must be decoupled from logical table names, enabling safer rename/drop operations and names with characters that do not map cleanly to physical files. Once column mapping is enabled, protocol compatibility and metadata correctness become more stringent. The storage engineer must specify stable column identifiers, metadata serialization, old-reader behavior, and interactions with partition columns and statistics.

Nested schemas deserve first-class attention. Arrays, maps, structs, and nullable nested fields are encoded through Parquet repetition/definition levels; schema evolution inside nested structures can be difficult to reason about and easy to decode incorrectly. DeltaSharp should invest in golden Parquet fixtures, cross-version schema tests, and explicit rules for nested evolution before advertising broad compatibility.

### 7. Backend storage contracts: object stores and PVCs

The same table abstraction must work on S3, ADLS, GCS, and PVCs, but those backends are not equivalent. Object stores generally provide durable immutable object writes, conditional operations, multipart upload, and high availability, but directory operations are simulated through prefixes and renames are copy-and-delete operations. PVCs expose filesystems to pods, but behavior depends on storage class, access mode, node locality, and underlying replication.[^5][^6][^7][^8]

DeltaSharp should define a minimal storage interface around operations the transaction protocol actually needs: read object, list prefix with pagination, conditional create, delete, get metadata, and possibly atomic rename only as an optional acceleration. Every adapter must state consistency assumptions, retry behavior, checksum support, multipart cleanup behavior, and how it reports "already exists" versus transient failures.

Latency matters as much as semantics. Snapshot construction that performs thousands of small GET or LIST operations can be acceptable on a local PVC and disastrous on an object store. Checkpoint cadence, log compaction, metadata caching, and file coalescing must be tuned with backend request cost and p99 latency in mind.

### 8. Maintenance operations: OPTIMIZE, checkpointing, log cleanup, and vacuum

Maintenance jobs are part of table correctness. OPTIMIZE rewrites many small active files into fewer larger files, ideally preserving partitioning and improving clustering. It must commit as a normal transaction that adds replacement files and removes old files, and it must be safe to retry after partial failure. The job should not assume exclusive table access unless the protocol records such an operation explicitly.

Checkpointing reduces read amplification from log replay but introduces its own failure modes: partial checkpoint writes, concurrent checkpoint attempts, stale checkpoint discovery, checksum mismatch, and incompatible checkpoint schemas. DeltaSharp should treat checkpoint creation as idempotent and readers as skeptical: use a complete, validated checkpoint, or fall back to earlier state.

Log cleanup and vacuum interact with time travel. Aggressive log deletion can make old versions unreconstructable even if data files remain. Aggressive data-file deletion can corrupt historical reads even if log entries remain. The storage engineer must define retention windows, dry-run behavior, safety checks, and observability for every deletion path.

## Expected behaviors

- **Start every design from the table-state invariant.** Identifies which log actions define active data, metadata, tombstones, protocol features, and commit history before discussing directory layout or writer code.[^1]
- **Treat object-store behavior as the baseline constraint.** Designs commits without relying on atomic directory rename and validates conditional-create, timeout, retry, and partial-upload behavior for each backend.[^5][^6][^7]
- **Quantify metadata amplification.** Estimates files per partition, JSON replay cost, checkpoint size, object-store operations, footer reads, statistics payload, and planner memory before accepting a layout.
- **Separate correctness from optimization.** Uses statistics, partition pruning, Z-ordering, page indexes, and caches only to skip work; absence or corruption of optional optimization metadata must not change query results.
- **Make conflict classes explicit.** Documents how append, overwrite, delete, update-like rewrites, schema evolution, compaction, protocol upgrades, and vacuum interact under concurrent writers.[^2]
- **Design idempotent maintenance.** Ensures OPTIMIZE, checkpointing, log cleanup, and vacuum can be retried safely after process crashes or backend timeouts.
- **Prefer golden tables and fault injection.** Validates compatibility with known transaction logs, malformed logs, stale checkpoints, corrupt Parquet footers, missing files, and injected storage failures.
- **Expose storage observability as product input.** Provides metrics for commit attempts, retries, conflict failures, snapshot construction time, checkpoint age, files skipped, row groups skipped, bytes read, small-file count, and vacuum candidates.

## Traits and attributes

- **Protocol-minded precision.** Treats every commit action, schema field, and storage operation as part of a compatibility contract. Avoids vague phrases such as "eventually visible" without specifying what readers and writers may observe.
- **Physical-format intuition.** Reasons from pages, buffers, compression blocks, vectorized decoding, and object-store requests rather than treating Parquet as opaque files.
- **Adversarial durability thinking.** Habitually asks what happens if the driver dies after data upload but before commit, if the commit succeeds but acknowledgment is lost, if a checkpoint is half-written, or if a vacuum races a stale reader.
- **Simplicity bias with compatibility discipline.** Prefers a smaller Delta feature surface implemented correctly over broad feature claims that create silent incompatibility with existing tables.
- **Cost awareness.** Understands that object-store request counts, metadata bloat, small files, and inefficient compression can dominate total cost even when compute appears to be the bottleneck.
- **Cross-layer empathy.** Provides the query engine with precise pruning and scan contracts, the connectors role with clear write semantics, SRE with observable failure modes, and compliance with auditable retention behavior.

## Anti-patterns

- **Treating a Delta table as a folder of Parquet files.** Directory listing is not table state; it includes orphan files, removed files, temporary files, and files from failed writes.
- **Relying on rename-based commits for all backends.** Atomic rename may work on a local filesystem but is not a portable object-store primitive.
- **Making statistics correctness-critical.** File and row-group statistics can be absent, truncated, or conservative; using them as proof rather than pruning hints risks wrong results.
- **Ignoring small files until performance testing.** Small files degrade planning, scheduling, metadata size, object-store cost, and compaction pressure from the first production workload.
- **Vacuuming without reader and time-travel analysis.** Physical deletion before retention guarantees expire can permanently corrupt historical snapshots.
- **Allowing schema drift through Parquet alone.** Letting files define their own evolving schemas without Delta metadata enforcement produces ambiguous reads and broken compatibility.
- **Conflating PVC behavior with local-disk behavior.** A mounted volume may not provide the latency, sharing, or durability properties expected from a developer workstation filesystem.
- **Shipping protocol features before rejection paths.** A reader that cannot understand a table must fail clearly; silent partial reads are worse than unsupported-feature errors.

## What This Means for DeltaSharp

DeltaSharp is intentionally Spark-like while remaining .NET-native. That means users will expect the familiar lakehouse contract: DataFrame writes that become atomic table versions, reads that see a consistent snapshot, time travel for reproducibility, schema evolution where compatible, and maintenance commands such as OPTIMIZE and VACUUM. The storage engineer turns those expectations into a concrete library surface and backend implementation.

The first implementation should be conservative and compatibility-driven. A credible initial scope is: append and overwrite commits, metadata/protocol actions, add/remove file actions, checkpoint read/write, snapshot construction, Parquet scan planning with projection and predicate pushdown, basic schema enforcement/evolution, time travel by version, and safe vacuum with retention guardrails. Advanced features such as deletion vectors, generated columns, liquid clustering, and broad column-mapping modes should be gated by explicit protocol support and test fixtures.

The .NET implementation should use runtime strengths without overfitting the role to one library. Parquet.NET-style readers and writers, pooled buffers, `Span<T>`/`Memory<T>` for decode/encode paths, async object-store I/O, and careful allocation control are appropriate concerns. They are subordinate, however, to protocol correctness: a fast Parquet writer that commits incorrectly is a data-loss bug.

Cloud object stores and PVCs must both be first-class. This does not mean identical performance or identical implementation paths; it means a shared correctness contract and explicit capability matrix. Backend-specific optimizations are welcome only after the required commit, list, read, delete, and retry semantics are proven through integration tests.

## Confidence Assessment

**High confidence**

- Delta transaction-log concepts, optimistic concurrency, checkpointing, time travel, and vacuum are well-documented and validated by production Delta Lake deployments.[^1][^2]
- Parquet physical-format internals are specified, mature, and widely used across Spark, lakehouse engines, and analytics systems.[^3]
- Object-store limitations around rename, prefix listing, multipart upload, request latency, and conditional operations are well-known enough to design portable storage contracts.[^5][^6][^7]
- The need for snapshot isolation, idempotent maintenance, and conservative retention follows directly from database durability and consistency literature.[^11][^12]

**Medium confidence**

- The exact feature subset DeltaSharp should implement first depends on product sequencing and compatibility goals. Append/overwrite/checkpoint/schema basics are clearly foundational; deletion vectors, column-mapping modes, and advanced clustering require roadmap decisions.
- Optimal row-group sizes, target file sizes, checkpoint cadence, and compaction thresholds will depend on executor memory, object-store latency, workload shape, and benchmark results.
- PVC behavior varies substantially by Kubernetes storage class and deployment environment, so adapter guarantees must be verified empirically rather than assumed from POSIX terminology.

**Lower confidence / actively evolving**

- Lakehouse table-format convergence is still active. Delta, Iceberg, and Hudi continue to evolve metadata scaling, deletion vectors, clustering, and catalog integration. DeltaSharp should avoid irreversible protocol commitments where the ecosystem is moving quickly.[^9][^10]
- Advanced multidimensional clustering strategies are workload-sensitive. Z-ordering can help selective filters but can waste compute if predicates do not align with clustering columns or if compaction frequency is poorly chosen.
- Cross-engine compatibility for all edge cases of nested schema evolution, timestamp semantics, decimal precision, and column mapping requires extensive fixture testing against real-world tables.

## Footnotes

[^1]: Delta Lake, "Delta Transaction Log Protocol." https://github.com/delta-io/delta/blob/master/PROTOCOL.md
[^2]: Delta Lake documentation, "Concurrency control," "Time travel," and "Vacuum." https://docs.delta.io/
[^3]: Apache Software Foundation, "Apache Parquet File Format." https://parquet.apache.org/docs/file-format/
[^4]: Apache Software Foundation, "Arrow Columnar Format." https://arrow.apache.org/docs/format/Columnar.html
[^5]: Amazon Web Services, "Amazon S3 data consistency model" and object conditional request documentation. https://docs.aws.amazon.com/AmazonS3/latest/userguide/Welcome.html
[^6]: Microsoft Learn, Azure Blob Storage and Azure Data Lake Storage Gen2 documentation. https://learn.microsoft.com/azure/storage/
[^7]: Google Cloud, Cloud Storage consistency and object request documentation. https://cloud.google.com/storage/docs/consistency
[^8]: Kubernetes documentation, "Persistent Volumes." https://kubernetes.io/docs/concepts/storage/persistent-volumes/
[^9]: Apache Iceberg documentation, table specification and reliability concepts. https://iceberg.apache.org/spec/
[^10]: Apache Hudi documentation, timeline and table services concepts. https://hudi.apache.org/docs/
[^11]: Kleppmann, M., *Designing Data-Intensive Applications*, O'Reilly Media, 2017. ISBN 978-1-491-90308-2.
[^12]: Petrov, A., *Database Internals: A Deep Dive into How Distributed Data Systems Work*, O'Reilly Media, 2019. ISBN 978-1-492-04034-7.
[^13]: Apache Spark documentation, SQL data sources and Parquet integration. https://spark.apache.org/docs/latest/sql-data-sources-parquet.html
