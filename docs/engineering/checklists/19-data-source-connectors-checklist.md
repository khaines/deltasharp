# 19 — Data Source Connectors Checklist

> **Scope:** DataSource-V2-style readers/writers, catalogs, file-format connectors, external systems, and batch/streaming source-sink boundaries.
> **Priority:** HIGH.
> **Owners:** data-platform-connectors-engineer. **Grounded in:** `.github/copilot-instructions.md`, ADR-0002, ADR-0006, connector persona guidance.

## How to use
Use this checklist when a change exposes external data as DataFrames or writes DeltaSharp results to external systems. Keep connector contracts above Delta internals from 17 and below query-planner implementation from 16.

## Checklist
### Connector boundaries
- [ ] The connector declares whether it is a source, sink, table provider, catalog provider, streaming endpoint, or file-format implementation.
- [ ] Transformations construct plans and connector descriptions only; no data scan, external write, or commit occurs before an action.
- [ ] Delta transaction log, checkpoints, deletion vectors, and physical Delta layout are delegated to 17, not reimplemented in connector code.
- [ ] Analyzer, optimizer, physical planning, join strategy, and pushdown rule implementation are delegated to 16 while contracts remain explicit here.
- [ ] Public read/write options match Spark names where practical and document .NET-specific deviations.

### DataSource V2-style API
- [ ] Scan builders expose schema, partitioning, capabilities, statistics, pushed filters, residual filters, and split planning without executing the scan.
- [ ] Batch readers describe input partitions/splits with deterministic serialization for driver-to-executor transfer.
- [ ] Writers use factory, task writer, commit message, driver commit, and abort phases with idempotent retry behavior.
- [ ] Catalog tables expose identity, namespace, provider, schema, partitioning, properties, capabilities, and authorization context.
- [ ] Capability negotiation is explicit for batch read, batch write, streaming read, streaming write, overwrite, truncate, merge schema, pushdown, and transactions.
- [ ] Unsupported capabilities fail with actionable errors instead of silently falling back to unsafe behavior.

### Partition discovery and splits
- [ ] File and object-store connectors separate metadata listing from data reads and cache metadata only with documented invalidation.
- [ ] Partition discovery handles case sensitivity, type parsing, null/default partition values, hidden files, and malformed paths.
- [ ] Split sizing accounts for file size, Parquet row groups, compressed size, source offsets, executor parallelism, and object-store request costs.
- [ ] Locality hints are advisory and never required for correctness in Kubernetes executor pods.
- [ ] Small-file and many-partition cases report planning overhead and integrate with benchmark gates in 22.
- [ ] Split descriptors include enough information to reproduce failed reads without depending on mutable global connector state.

### Pushdown contracts
- [ ] Projection pushdown reports exactly which columns and nested fields will be read.
- [ ] Predicate pushdown returns accepted predicates and residual predicates; residuals are evaluated by DeltaSharp when source enforcement is incomplete.
- [ ] Null semantics, timestamp zones, decimals, string collation, nested fields, and type conversions are covered before claiming a filter is pushed.
- [ ] Aggregate, limit, sample, sort, and top-N pushdown declare ordering, determinism, and residual obligations.
- [ ] Pushdown never changes query results; it only reduces data transferred or work performed.
- [ ] Statistics returned to CBO/AQE are versioned by snapshot/source state and labelled exact, estimated, stale, or unavailable.

### File-format connectors
- [ ] Parquet file-format connector respects row groups, column chunks, encodings, compression, predicate pruning, and footer metadata while leaving Delta table state to 17.
- [ ] CSV reader/writer defines delimiter, quote, escape, header, multiline, encoding, corrupt-record, null, and type-conversion behavior.
- [ ] JSON reader/writer defines multiline, schema inference, permissive/drop-malformed/fail-fast modes, numeric precision, timestamps, and nested-field behavior.
- [ ] ORC and Avro support matrices state feature parity, logical types, compression, schema evolution, and unsupported features.
- [ ] All formats have deterministic schema inference with sampling limits, user-schema precedence, and diagnostics for conflicting samples.
- [ ] Corrupt-file handling is consistent across formats: fail-fast, permissive, drop-malformed, or quarantine modes are explicit.

### Catalog and schema behavior
- [ ] Catalog integration resolves provider, path, options, schema, partition spec, table properties, and capabilities before execution.
- [ ] Schema-on-read rules are deterministic for case sensitivity, nullability, decimal precision/scale, timestamps, arrays, maps, structs, and binary values.
- [ ] Schema evolution negotiation distinguishes connector-level schema merge from Delta protocol evolution in 17.
- [ ] External catalog credentials and authorization context are scoped, serializable, and safe for executor use without leaking secrets.
- [ ] Catalog cache invalidation is documented for create/drop/alter table, external mutations, and streaming checkpoint recovery.

### Batch and streaming
- [ ] Batch sources define boundedness, offset/file snapshot, split enumeration point, and repeatable-read behavior.
- [ ] Streaming sources define offset format, checkpoint state, watermark input, rate limits, replay behavior, and recovery after driver restart.
- [ ] Streaming sinks define exactly-once, at-least-once, or idempotent semantics and use commit identifiers to avoid duplicate output.
- [ ] Backpressure signals flow from executor readers/writers through bounded queues to source rate limits or sink flush policies.
- [ ] Cancellation and retry paths close readers, abort writers, release leases, and preserve connector-visible commit semantics.
- [ ] Late data, malformed records, schema drift, duplicate records, and quarantine outputs have structured metrics and user-visible diagnostics.

### External connectors
- [ ] JDBC connectors push predicates, projections, aggregates, limits, and partitioned reads only when SQL dialect semantics are proven equivalent.
- [ ] JDBC writes define isolation level, batch size, retry behavior, generated keys, and idempotency limits.
- [ ] Kafka connectors define offset ranges, consumer group behavior, headers, keys, values, timestamps, watermarks, and sink transactional/idempotent semantics.
- [ ] Object-store connectors handle pagination, throttling, retry-after, multipart uploads, conditional writes, credential refresh, and listing cost.
- [ ] PVC/file-system connectors document rename, fsync, locking, permissions, and concurrent-writer assumptions.
- [ ] Connector metrics include read/write rows, bytes, splits, pushed filters, residual filters, throttling, retries, lag, and corrupt records.

### Testing and compatibility
- [ ] Unit tests cover option validation, capability negotiation, schema inference, residual filters, and writer commit/abort paths.
- [ ] Integration tests cover object-store and PVC behavior using test doubles or emulators before live-system tests.
- [ ] Fault tests cover executor loss, task retry, speculative execution, cancelled reads, partial writes, and streaming replay.
- [ ] Golden fixtures cover Parquet, CSV, JSON, ORC, Avro, JDBC type mapping, and Kafka offset recovery where applicable.
- [ ] Coverage targets follow build-test config: connector and storage backend coverage is at least 80% for relevant code.

## Anti-patterns (red flags)
- Executing scans or external writes while building a logical plan.
- Claiming a predicate was pushed down without returning residual filters.
- Mixing `_delta_log` ACID decisions into generic connector code instead of using 17.
- Hiding planner semantics or optimizer rewrites inside a connector instead of using 16.
- Treating schema inference as nondeterministic best effort with no sampling contract.
- Retrying sink writes without idempotency keys, commit messages, or duplicate-output protection.
- Assuming object-store listing and PVC rename semantics are interchangeable.
- Reporting only averages for connector throughput while tail latency, backpressure, or corrupt-record rates regress.

## References
- [17 — Delta Storage Format Checklist](17-delta-storage-format-checklist.md).
- [21 — Distributed Correctness Checklist](21-distributed-correctness-checklist.md).
- [22 — Benchmark Regression Gates Checklist](22-benchmark-regression-gates-checklist.md).
- [ADR-0002: In-memory columnar batch format](../../adr/0002-columnar-batch-format.md).
- [ADR-0006: Scheduler, AQE, and CBO](../../adr/0006-scheduler-aqe-cbo.md).
- Spark DataSource V2 concepts; JDBC, Kafka, Parquet, CSV, JSON, ORC, and Avro format specifications.
