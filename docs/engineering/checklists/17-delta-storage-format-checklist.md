# 17 — Delta Storage Format Checklist

> **Scope:** Delta transaction log, Parquet IO, checkpoints, table maintenance, storage adapters, and native Delta table read/write correctness.
> **Priority:** CRITICAL.
> **Owners:** delta-storage-format-engineer. **Grounded in:** ADR-0011, ADR-0004, ADR-0002, ADR-0006, `.github/copilot-instructions.md`.

## How to use
Use this checklist for every change that can affect persisted table state, `_delta_log` semantics, Parquet metadata, or storage-backend durability. Treat data loss, corruption, ACID weakening, and silent Delta incompatibility as Critical findings.

## Checklist
### Delta log protocol
- [ ] `_delta_log` JSON actions are modeled explicitly for `protocol`, `metaData`, `add`, `remove`, `txn`, and other supported actions.
- [ ] Reader and writer protocol versions are negotiated before read/write, and unsupported table features fail closed with a precise protocol error.
- [ ] Snapshot state is derived from committed log versions and checkpoints, never from directory listing alone.
- [ ] Add-file actions include path, partition values, size, modification time, data-change flag, and write-time statistics where available.
- [ ] Remove-file tombstones preserve deletion timestamp, data-change semantics, and retention behavior required by time travel and VACUUM.
- [ ] Metadata changes preserve schema string, partition columns, configuration, created time, and column-mapping identifiers.
- [ ] Checkpoints round-trip all active actions needed to reconstruct the same snapshot as JSON replay.
- [ ] V2 checkpoints and feature-gated checkpoint layouts are accepted only when protocol support is explicit.

### Optimistic commits and ACID
- [ ] Writers use append-data-files-then-commit-log publication; uncommitted data files never become active table state.
- [ ] Commit publication is atomic for each storage adapter, using conditional create or an equivalent single-version put-if-absent primitive.
- [ ] Commit retry distinguishes ambiguous success, definite conflict, transient storage failure, and non-retryable protocol failure.
- [ ] Conflict detection covers concurrent metadata/protocol changes, partition overwrites, deletes, compaction, schema evolution, and read predicates.
- [ ] Transaction identifiers make retry and streaming/micro-batch commits idempotent without duplicating rows.
- [ ] Readers observe snapshot isolation: a read pinned to version N cannot include actions from version N+1.
- [ ] Acknowledged commits remain durable across driver restart, executor loss, storage retry, and checkpoint compaction.
- [ ] Recovery documents orphan-file cleanup without treating orphan files as committed data.
- [ ] Tests include concurrent writers, crash points before/during/after commit, and ambiguous object-store responses.

### Snapshot reconstruction and time travel
- [ ] Snapshot loading selects the latest safe checkpoint at or before the requested version, then replays later JSON commits in order.
- [ ] Version-based time travel pins the exact numeric version and reports missing versions or retention gaps clearly.
- [ ] Timestamp-based time travel resolves to a deterministic version using commit metadata and documented timezone/clock assumptions.
- [ ] Log cleanup never removes files required for configured time-travel retention or active readers.
- [ ] Snapshot APIs expose table version, schema, protocol, metadata, active files, tombstones, and statistics immutably.
- [ ] Large-log replay has bounded memory behavior and metrics for replay depth, checkpoint age, and action counts.

### Schema and advanced Delta features
- [ ] Schema enforcement rejects incompatible writes before files are committed.
- [ ] Schema evolution rules are explicit for nullability, type widening, nested fields, case sensitivity, generated metadata, and partition columns.
- [ ] Column mapping preserves stable column identities for rename/drop and prevents old readers from silently misreading columns.
- [ ] Deletion vectors are recorded, read, validated, and compacted without changing row visibility incorrectly.
- [ ] Change Data Feed records inserts, updates, deletes, and commit versions consistently with data-change flags.
- [ ] Liquid clustering or clustering metadata is persisted and consumed as an optimization, never as a correctness shortcut.
- [ ] Row tracking identifiers survive updates, deletes, compaction, and CDF reads when the feature is enabled.
- [ ] Every table feature has compatibility tests against golden Delta logs and unsupported-feature failure tests.

### Parquet correctness
- [ ] Parquet readers validate footer magic, schema, column chunks, row groups, page headers, encodings, compression, and checksums where present.
- [ ] Writers choose row-group and page sizes deliberately for scan efficiency, memory limits, and predicate pushdown.
- [ ] Dictionary, RLE/bit-packed, delta, plain, and nested encodings preserve Spark-compatible null, decimal, timestamp, and string semantics.
- [ ] Column statistics include min/max/null count/distinct count only when semantically valid for the physical type and collation.
- [ ] Page and row-group indexes are treated as pruning hints; they never drop rows unless residual predicates remain correct.
- [ ] Footer metadata preserves Spark/Delta schema annotations, logical types, partition assumptions, and writer version information.
- [ ] Corrupt or truncated Parquet files fail deterministically and never produce partial successful rows unless the API explicitly allows quarantine.

### Statistics, skipping, and CBO handoff
- [ ] Writers collect row count, file size, partition values, min/max/null counts, and optional histograms while batches are still in memory.
- [ ] Data-skipping statistics are stored in Delta add actions or side metadata with truncation rules documented per type.
- [ ] Statistics feed the CBO/AQE contracts from 16 and ADR-0006 without becoming correctness predicates.
- [ ] Missing, stale, or truncated statistics degrade to scanning more data, never to skipping required rows.
- [ ] Metrics report files skipped, row groups skipped, bytes scanned, stats coverage, and predicate residuals.

### Maintenance and retention
- [ ] OPTIMIZE/compaction creates replacement files and commits remove/add actions atomically; source files remain valid for old snapshots until retention expires.
- [ ] Small-file mitigation has thresholds for file count, average file size, partition skew, and object-store request amplification.
- [ ] Checkpointing and log compaction are idempotent, restartable, and safe under concurrent readers and writers.
- [ ] VACUUM enforces retention safety, stale-reader protection, dry-run output, legal hold hooks, and backend-specific listing delays.
- [ ] Vacuum never deletes files referenced by any retained snapshot or by an in-progress acknowledged reader.
- [ ] Maintenance jobs expose audit records for candidate files, deleted files, skipped files, and retention rationale.

### Storage-backend semantics
- [ ] S3, ADLS, GCS, and PVC adapters document required primitives for conditional create, list, get, delete, rename, multipart upload, and fsync/durability.
- [ ] Object-store paths do not depend on atomic directory rename; PVC fast paths preserve the same Delta correctness model.
- [ ] Partial uploads, eventual listing, throttling, and retry-after behavior are covered by fault-injection tests.
- [ ] Commit code names which backend guarantee made the commit atomic and rejects adapters that cannot provide it.
- [ ] Shuffle/object-store distinctions from 21 and ADR-0004 are respected: dynamic shuffle location resolution is not reused as Delta log truth.

## Anti-patterns (red flags)
- Treating directory listing as the list of active Delta files.
- Publishing Parquet files as visible data before an atomic log commit.
- Weakening conflict detection to make concurrent writers “usually work.”
- Deleting data or logs before proving no retained snapshot or reader needs them.
- Ignoring protocol versions, column mapping, deletion vectors, CDF, clustering, or row tracking while claiming compatibility.
- Using Parquet statistics or data-skipping metadata as a substitute for residual predicate evaluation.
- Assuming object stores support POSIX atomic rename because PVCs often do.
- Silently reading corrupt checkpoints, truncated logs, or unsupported table features.

## References
- [ADR-0011: Delta protocol feature scope](../../adr/0011-delta-protocol-scope.md).
- [ADR-0004: Shuffle architecture](../../adr/0004-shuffle-architecture.md).
- [ADR-0002: In-memory columnar batch format](../../adr/0002-columnar-batch-format.md).
- [ADR-0006: Scheduler, AQE, and CBO](../../adr/0006-scheduler-aqe-cbo.md).
- [19 — Data Source Connectors Checklist](19-data-source-connectors-checklist.md); [21 — Distributed Correctness Checklist](21-distributed-correctness-checklist.md).
- Delta Lake transaction log protocol and Apache Parquet format specifications.
