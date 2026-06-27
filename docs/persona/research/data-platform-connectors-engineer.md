# Data Sources & Connectors Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

A world-class Data Sources & Connectors Engineer owns the data boundary of DeltaSharp: the pluggable source and sink layer that turns files, catalogs, streams, databases, object stores, and PersistentVolumes into reliable DataFrames and write targets. The role is distinct from storage-format ownership and query-planner ownership because it focuses on contracts: schemas, capabilities, splits, pushdowns, offsets, commits, and ingest-side quality before any execution engine consumes the data.[^1][^2][^3][^4]

The strongest source convergence is around a contract-first connector mindset. Modern analytical engines separate table providers from planners through capability negotiation; efficient readers expose partition pruning, column pruning, predicate pushdown, split planning, and statistics without sacrificing correctness. The engineer must know when a source can enforce a predicate and when the engine must apply a residual filter after the scan.[^1][^2][^5][^6]

For DeltaSharp specifically, connectors are foundational because the product promises Spark familiarity, native Delta tables, and Kubernetes-distributed execution across S3, ADLS, GCS, and PVC-backed storage. A weak connector boundary creates silent correctness failures: wrong schema inference, duplicate sink commits after retries, unsafe object-store renames, inconsistent partition discovery, and pushdowns that return incomplete data. The role exists to make those failure modes explicit and testable.[^1][^3][^7][^8]

## Evidence base

This report draws on Apache Spark DataSource V2 design concepts, Spark SQL data source behavior, Delta Lake protocol material, Apache Parquet and Arrow ecosystem guidance, Kafka Connect and JDBC connector practices, schema-evolution literature, and cloud object-store consistency guidance. It also incorporates DeltaSharp's repository canon: lazy transformations, eager actions, Catalyst-style planning, native Delta tables, driver/executor execution, Kubernetes Operator orchestration, and storage on object stores plus PVCs.

- [Apache Spark SQL Data Sources](https://spark.apache.org/docs/latest/sql-data-sources.html)[^1]
- [Apache Spark DataSource V2 API docs](https://spark.apache.org/docs/latest/api/java/org/apache/spark/sql/connector/read/package-summary.html)[^2]
- [Delta Transaction Log Protocol](https://github.com/delta-io/delta/blob/master/PROTOCOL.md)[^3]
- [Apache Parquet documentation](https://parquet.apache.org/docs/)[^4]
- [Spark Parquet, JSON, CSV, ORC, and Avro data source docs](https://spark.apache.org/docs/latest/sql-data-sources-parquet.html)[^5]
- [Kafka Connect documentation](https://kafka.apache.org/documentation/#connect)[^6]
- [Amazon S3 strong consistency documentation](https://aws.amazon.com/s3/consistency/)[^7]
- [Azure Blob Storage data consistency documentation](https://learn.microsoft.com/azure/storage/blobs/concurrency-manage)[^8]
- [Data Mesh Principles and Logical Architecture — Zhamak Dehghani](https://martinfowler.com/articles/data-mesh-principles.html)[^9]
- [Schema Evolution and Compatibility — Confluent](https://docs.confluent.io/platform/current/schema-registry/fundamentals/schema-evolution.html)[^10]
- Internal: `.github/copilot-instructions.md`[^11]
- Internal: `docs/persona/agents/README.md`[^12]

## Explanation

This role is the engineer who designs and reviews the source/sink substrate beneath a Spark-like processing framework. The developer-facing API may say `read.parquet`, `read.csv`, `read.table`, `readStream`, `write`, or `writeStream`, but behind that surface sits a precise connector contract: identify the provider, resolve options, discover or validate schema, enumerate partitions, build scan plans, produce splits, push down safe work, report residual work, and coordinate writes across distributed executors.[^1][^2]

A world-class Data Sources & Connectors Engineer therefore has to be bilingual in engine contracts and external-system behavior. They understand file formats, object-store listing economics, JDBC pushdown limits, Kafka offsets, catalog identity, checkpoint state, cloud credentials, retry semantics, and source-specific type systems. They do not need to own the Catalyst-style optimizer, but they must provide the optimizer with honest capabilities and enough metadata to make good choices.[^1][^2][^6][^7]

The key distinction is boundary ownership. Delta table internals — transaction log actions, ACID commit rules, checkpoint layout, deletion vectors, compaction, and physical durability — belong to storage-format specialists. Query planning — expression normalization, rule ordering, join selection, shuffle placement, physical operators, and code generation — belongs to query/execution specialists. This role owns the connector-facing contracts that let those layers interact safely: source capabilities, schema and partition metadata, split descriptors, pushed predicates, residual predicates, and sink commit messages.[^2][^3]

**Role criticality: High.** DeltaSharp cannot feel like a credible .NET-native Spark equivalent if sources and sinks are inconsistent, non-replayable, or under-specified. Connector correctness is also hard to retrofit: option names, schema inference rules, bad-record behavior, streaming offsets, and write modes become compatibility promises once users depend on them.[^1][^5][^10][^11]

## Required knowledge domains

### 1. DataSource V2-style contracts

The engineer should understand the major abstractions used by modern analytical engines: table providers, table capabilities, scan builders, batch scans, streaming scans, input partitions, partition readers, data writers, writer factories, commit messages, and abort semantics. The exact DeltaSharp API can be .NET-native, but the conceptual contract should preserve Spark-like separation between planning and execution.[^1][^2]

Capability negotiation is central. A source should declare whether it supports projection pushdown, filter pushdown, aggregate pushdown, limit pushdown, sample pushdown, columnar reads, streaming reads, truncate, overwrite by expression, dynamic partition overwrite, append, create, replace, and staged commits. Planner-facing APIs should distinguish accepted predicates from residual predicates and partial aggregates from complete aggregates.

### 2. File-format connectors

Parquet, CSV, JSON, ORC, and Avro require different behavior. Parquet and ORC expose columnar layouts, row groups or stripes, statistics, compression, nested types, and predicate opportunities. CSV and JSON require robust schema inference, permissive parsing, corrupt-record handling, escaping, multiline handling, and clear null semantics. Avro adds schema resolution, unions, logical types, and compatibility expectations.[^4][^5][^10]

World-class file-format design avoids one-off semantics. Users should see consistent options for header handling, timestamp formats, case sensitivity, corrupt-record modes, sampling ratios, partition-column discovery, recursive lookup, file globbing, compression, and schema precedence. Format-specific behavior should be documented where consistency would be misleading.

### 3. Partition discovery and split planning

The role must design how DeltaSharp discovers partitions and produces parallelizable work. For file sources, that includes directory layout, Hive-style partition parsing, recursive listing, hidden files, glob filters, modification-time filters, file size, row-group metadata, locality hints, and max split size. For external systems, it includes ranges, shards, offsets, topic partitions, query predicates, source throttles, and rate limits.[^1][^2][^6]

Split planning must account for distributed execution under a driver/executor model. A split descriptor should be serializable, credential-safe, deterministic, and small enough to ship to executor pods. Planning should minimize expensive object-store listings, avoid assuming atomic rename where it is not guaranteed, and prevent small-file amplification from destroying executor efficiency.[^7][^8]

### 4. Pushdown correctness

Pushdown is one of the highest-leverage and highest-risk connector features. Correct pushdown can avoid scanning entire datasets; incorrect pushdown silently drops rows. The engineer must reason about expression support, null semantics, timestamp zones, decimal precision, string collation, case sensitivity, nested fields, source-specific type conversions, and residual filtering.[^1][^2]

The safe default is explicitness: report which filters were accepted, which were partially accepted, and which remain for engine evaluation. Projection pushdown must preserve columns needed for residual predicates, partition columns, metadata columns, and generated columns. Statistics returned from sources should include uncertainty rather than pretending stale or sampled data is exact.

### 5. Catalog and metastore integration

Connectors do not just read paths; they also integrate with catalogs and metastores. The role should define table identity, namespace rules, provider lookup, option resolution, schema retrieval, partition metadata, authorization context, table properties, capabilities, and refresh semantics. Catalog integrations should make it clear when metadata is authoritative and when data files must still be inspected.[^1][^2][^9]

Catalog design is also a tenancy and compatibility surface. Different tenants, workspaces, or namespaces may have different credentials and retention constraints. A table resolved through a catalog should carry enough context for secure execution without leaking credentials into serialized plans or diagnostic output.

### 6. Batch and streaming sources/sinks

DeltaSharp's source layer needs bounded and unbounded inputs. Batch sources produce finite splits; streaming sources produce offsets, micro-batch ranges, checkpoint metadata, and recovery rules. Sinks need idempotent commits, abort paths, replay tolerance, and clear guarantees around append, complete, update, overwrite, and exactly-once or effectively-once behavior.[^2][^6]

Streaming design should be especially conservative. Source offsets must be durable and comparable; sink commit metadata must survive driver restarts; micro-batch replays must not duplicate rows; and watermarks or event-time assumptions must be explicit. A connector that cannot provide a guarantee should say so in its capability set rather than simulating a stronger guarantee.

### 7. External system connectors

JDBC, Kafka, object stores, and PVC-backed files each impose different constraints. JDBC connectors need partitioned reads, predicate pushdown, type mapping, isolation-level choices, fetch size, write batching, upsert or append semantics, and safe credential handling. Kafka connectors need topic/partition offsets, deserialization, schema registry integration, starting offsets, max offsets per trigger, and replay behavior.[^6][^10]

Object-store connectors must understand listing cost, consistency, credential refresh, encryption options, multipart upload behavior, request throttling, data egress, and retry storms. PVC-backed storage may offer stronger local filesystem semantics but different availability, scheduling, and locality constraints. A good connector abstraction does not pretend these storage types are identical; it hides incidental differences while exposing semantic differences that affect correctness.

### 8. Ingest-side data quality

Data quality belongs at the source boundary as well as downstream validation. The engineer should define bad-record modes, schema-drift handling, duplicate detection hooks, quarantine destinations, row-level diagnostics, sampling checks, required-field validation, and contract tests for known source systems. Users should be able to choose between fail-fast strictness and controlled permissiveness without silent data loss.[^9][^10]

Quality metadata should be structured. Counts of malformed records, discarded rows, inferred fields, widened types, late records, duplicate keys, empty partitions, and quarantined files should be available to callers and tests. Connector warnings should be actionable and stable enough to document.

## Required skills

| Skill | What world-class looks like | Evidence |
|---|---|---|
| Connector API design | Designs DataSource V2-style abstractions that preserve lazy planning, distributed execution, capability negotiation, and source-specific correctness without leaking implementation details. | [^1][^2][^11] |
| File-format expertise | Specifies Parquet, CSV, JSON, ORC, and Avro behavior with correct schema handling, corrupt-record modes, nested types, compression, partition discovery, and pushdown opportunities. | [^4][^5][^10] |
| Pushdown discipline | Distinguishes accepted predicates from residual predicates, handles type/null/time semantics carefully, and never trades correctness for scan reduction. | [^1][^2] |
| Streaming connector design | Defines durable offsets, checkpoint metadata, replay behavior, idempotent sink commits, and failure recovery for structured-streaming-style ingestion. | [^2][^6] |
| Catalog integration | Models namespaces, table identity, provider lookup, capabilities, schema resolution, authorization context, and metadata refresh without coupling to planner internals. | [^1][^2][^9] |
| Object-store and PVC pragmatism | Designs connectors that minimize listings, respect storage semantics, handle credentials safely, avoid unsafe rename assumptions, and expose locality or consistency constraints. | [^7][^8][^11] |
| Data-quality engineering | Builds source-boundary validation, schema-drift policies, quarantine paths, diagnostics, and compatibility checks that prevent silent corruption. | [^9][^10] |

## Behaviors to emulate

- **Contract-first design.** Define table capabilities, scan contracts, write contracts, schema rules, and failure semantics before discussing implementation shortcuts.[^1][^2]
- **Lazy-planning discipline.** Metadata inspection is allowed when needed, but transformations should not perform full scans or commit side effects; actions trigger execution.[^11]
- **Pushdown honesty.** Every accepted pushdown is accompanied by a clear statement of residual work and correctness caveats, especially around nulls, time zones, decimals, and nested fields.[^2]
- **Format-aware consistency.** Preserve consistent user options across formats while respecting the real differences between columnar formats, row-oriented text formats, and schema-carrying formats.[^4][^5]
- **Replay-safe writes.** Treat retries, speculative tasks, driver restarts, and executor loss as normal events; design sink commit protocols that tolerate them.[^2][^3]
- **Source-boundary quality.** Make malformed input, schema drift, duplicates, late records, and incompatible source changes visible and testable, not hidden in best-effort parsing.[^9][^10]
- **Cloud-storage realism.** Optimize listings, plan around throttles, separate metadata reads from data reads, and avoid filesystem assumptions that fail on object stores.[^7][^8]
- **Clean handoffs.** Route Delta transaction internals, optimizer implementation, API ergonomics, production operations, security, compliance, and cost questions to the right roster owner.

## Traits and attributes

The sources imply a consistent trait profile for an excellent connectors engineer: precise, interface-minded, skeptical of implicit semantics, and comfortable working at boundaries between systems.[^1][^2][^9]

- **Boundary architect.** Sees connectors as contracts between external systems and the engine, not as thin file readers. Can explain what each side may assume and what it must verify.
- **Correctness pessimist.** Assumes pushdown, schema inference, offset tracking, and distributed writes are wrong until proven by tests under failure, drift, and replay.
- **Format pragmatist.** Knows that Parquet, CSV, JSON, ORC, Avro, JDBC, Kafka, object stores, and PVCs all need different semantics, but still designs a coherent user experience.
- **Distributed-systems realist.** Understands that executors fail, tasks retry, drivers restart, credentials expire, listings throttle, and cloud stores charge for every request.
- **Compatibility steward.** Treats option names, schema inference, bad-record behavior, and write-mode semantics as long-lived API promises.
- **Collaborative integrator.** Works naturally with storage, planner, security, compliance, performance, runtime, and documentation owners because connector work touches all of them without replacing them.

## Anti-patterns to avoid

- Treating a connector as a simple adapter that reads bytes, while ignoring capabilities, residual predicates, partitioning, schema evolution, retries, and commit semantics.[^1][^2]
- Claiming predicate or projection pushdown without a precise residual-filter plan, creating silent wrong results under nulls, type casts, time zones, or nested fields.[^2]
- Letting schema inference vary by executor, file ordering, culture settings, timestamp defaults, or sampling accidents, making repeated reads nondeterministic.[^5][^10]
- Assuming object stores behave exactly like local filesystems, especially around listing cost, rename patterns, multipart uploads, credentials, and throttling.[^7][^8]
- Building streaming sources without durable offsets or sinks without idempotent commit behavior, causing duplicates or gaps after restart.[^2][^6]
- Collapsing Delta transaction internals into generic connector code, blurring the handoff to storage-format ownership and making ACID behavior harder to reason about.[^3]
- Hiding bad records, malformed files, duplicate source rows, schema drift, or partial reads behind warnings that tests and callers cannot inspect.[^9][^10]
- Designing only for the happy path of a single machine rather than driver/executor execution across Kubernetes pods and storage backends.[^11]

## What this means for DeltaSharp

DeltaSharp's connectors must make Spark users feel at home while remaining native to the project's architecture. That means familiar provider names, options, read/write modes, partition discovery, streaming concepts, and format behavior where practical. It also means strict adherence to DeltaSharp's invariant that transformations are lazy and actions are eager. A `where` followed by a file source may produce a better scan plan through pushdown, but it should not scan data until an action runs.[^1][^2][^11]

The role should prioritize a small set of excellent connector foundations before broad surface area: a stable provider/capability API, Parquet and Delta-adjacent file reading boundaries, CSV/JSON schema inference rules, object-store/PVC storage semantics, catalog resolution, JDBC/Kafka design contracts, and streaming source/sink checkpoint protocols. Once those are coherent, additional formats and systems can reuse the same contracts instead of inventing one-off behavior.

The most important product risk is silent wrongness. Users will forgive an unsupported pushdown, a slower scan, or a clear fail-fast error; they will not forgive missing rows, duplicate writes, nondeterministic schemas, or source options that change meaning across formats. The Data Sources & Connectors Engineer is the person who turns those risks into explicit contracts, tests, diagnostics, and handoffs.

## Confidence Assessment

**High confidence**

- The need for a dedicated connector role is strongly supported by Spark's explicit data-source abstractions and the breadth of behavior hidden behind read/write APIs.[^1][^2]
- Pushdown correctness, schema behavior, and streaming offsets are well-known sources of analytical correctness defects and map directly to DeltaSharp's architecture.[^1][^2][^6]
- Object-store and PVC support is explicitly part of DeltaSharp's canon, making storage-backend semantics a first-class connector concern rather than an implementation detail.[^7][^8][^11]
- The roster boundary is clear: Delta internals, query planning, and API ergonomics already have separate owners in the DeltaSharp persona library.[^12]

**Medium confidence**

- The exact .NET interface names and implementation patterns will evolve once the codebase is scaffolded, but the underlying contracts should remain stable.
- The initial priority among JDBC, Kafka, ORC, Avro, and catalog integrations may shift with product roadmap decisions.
- Some cloud-store consistency behaviors differ by account configuration and provider evolution; connector docs should cite current provider guarantees when implementation begins.

## Footnotes

[^1]: Apache Spark SQL Data Sources, https://spark.apache.org/docs/latest/sql-data-sources.html
[^2]: Apache Spark DataSource V2 API docs, https://spark.apache.org/docs/latest/api/java/org/apache/spark/sql/connector/read/package-summary.html
[^3]: Delta Transaction Log Protocol, https://github.com/delta-io/delta/blob/master/PROTOCOL.md
[^4]: Apache Parquet documentation, https://parquet.apache.org/docs/
[^5]: Apache Spark data source documentation for Parquet, ORC, JSON, CSV, and Avro, https://spark.apache.org/docs/latest/sql-data-sources-parquet.html
[^6]: Apache Kafka Connect documentation, https://kafka.apache.org/documentation/#connect
[^7]: Amazon S3 strong consistency documentation, https://aws.amazon.com/s3/consistency/
[^8]: Azure Blob Storage concurrency and consistency documentation, https://learn.microsoft.com/azure/storage/blobs/concurrency-manage
[^9]: Data Mesh Principles and Logical Architecture, Zhamak Dehghani, https://martinfowler.com/articles/data-mesh-principles.html
[^10]: Schema Evolution and Compatibility, Confluent, https://docs.confluent.io/platform/current/schema-registry/fundamentals/schema-evolution.html
[^11]: `.github/copilot-instructions.md`
[^12]: `docs/persona/agents/README.md`
