# EPIC-05: Delta & Parquet Storage

- **Roadmap milestone:** M2 (link to ../../../ROADMAP.md#milestone-2--storage--sql-v0x)
- **Primary persona(s):** `delta-storage-format-engineer`
- **Related ADRs:** ADR-0011, ADR-0002
- **Depends on:** EPIC-01, EPIC-02
- **Status:** draft
- **Size:** XL

## Objective

Deliver DeltaSharp's native storage layer: Parquet file IO, Delta transaction-log semantics, ACID commits, snapshots, advanced Delta protocol features, and safe table maintenance. This epic makes Delta tables first-class in the .NET engine while preserving the Delta log as the source of truth and integrating with the Arrow-compatible `ColumnVector` model from ADR-0002.

## Scope

**In scope**
- Parquet vectorized reading into `ColumnVector` and Parquet writing with row groups, encodings, dictionary/RLE, statistics, page metadata, and footers.
- Storage backend contracts for object stores and Kubernetes PVC-backed filesystems, including conditional writes, retry behavior, and path/credential isolation.
- Delta `_delta_log` JSON actions, checkpoint Parquet reading, snapshot reconstruction, protocol negotiation, and time travel.
- Delta writes with optimistic concurrency, explicit conflict detection, retry/idempotency semantics, and ACID visibility guarantees.
- Schema enforcement/evolution, column mapping, deletion vectors, Change Data Feed, liquid clustering, row tracking, V2 checkpoints, statistics, OPTIMIZE/compaction, and VACUUM safety.

**Out of scope** (and where it lives instead)
- DataSource V2 public source/sink APIs above Delta storage → EPIC-06 / persona `data-platform-connectors-engineer`.
- SQL syntax for `OPTIMIZE`, `VACUUM`, `DESCRIBE HISTORY`, and time-travel clauses → EPIC-07 / persona `sql-language-frontend-engineer`.
- Distributed task scheduling, retries, and shuffle execution of scans/writes → EPIC-08 / persona `dotnet-distributed-execution-engineer`.
- Cost-model decisions that consume emitted statistics → EPIC-11 / persona `query-optimizer-scheduler-engineer`.
- Security policy and IAM design for object stores → EPIC-00 / persona `cloud-native-security-sme`.

## Exit criteria

- [ ] A Delta table can be read from and written to both an object-store backend and a PVC-backed filesystem using Parquet data files and `_delta_log` as the table source of truth.
- [ ] Concurrent writers commit with optimistic concurrency, deterministic conflict detection, safe retries, idempotent recovery, and ACID snapshot isolation.
- [ ] Readers can time travel by version and timestamp, including checkpoint-assisted snapshot reconstruction and clear protocol-feature errors.
- [ ] Schema enforcement, schema evolution, and column mapping preserve existing data correctness and reject unsafe writes with actionable errors.
- [ ] Deletion vectors, Change Data Feed, liquid clustering metadata, row tracking, and V2 checkpoints are functional behind protocol negotiation.
- [ ] OPTIMIZE/compaction, small-file mitigation, and VACUUM are idempotent, retention-safe, and do not corrupt stale readers or historical snapshots.
- [ ] Write-time statistics are emitted for data skipping and exposed to the optimizer contract without weakening correctness if statistics are absent.

## Features

### FEAT-05.1: Parquet IO and storage backend contracts

- **Objective:** Implement native Parquet read/write paths that bridge storage files to DeltaSharp `ColumnVector` batches and define object-store/PVC semantics required by Delta commits.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Depends on:** EPIC-01, EPIC-02.

#### Stories

##### STORY-05.1.1: Vectorized Parquet reader into `ColumnVector`

- **As a** query reader **I want** Parquet columns decoded into `ColumnVector` batches **so that** scans feed the vectorized engine without row materialization.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** L. **Depends on:** EPIC-02.
- **Acceptance criteria:**
  - [ ] Given Parquet files with primitive, nullable, nested, and dictionary-encoded columns When they are scanned Then decoded values, validity, and ordering match golden fixtures.
  - [ ] Given projection and row-group filters When the reader plans a scan Then only required column chunks and eligible row groups are read.
  - [ ] Given dictionary, RLE/bit-packed, plain, and compressed pages When decoding completes Then `ColumnVector` buffers contain correct values without per-row object allocation on hot paths.
  - [ ] Given malformed or unsupported Parquet metadata When the reader opens the file Then it fails with a deterministic storage error that names the unsupported feature.
- **Definition of done:** builds/tests/format pass; checklists `17`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-05.1.2: Parquet writer with statistics and footer metadata

- **As a** Delta writer **I want** Parquet files written with correct row groups, encodings, compression, statistics, and footers **so that** readers and data skipping can trust file metadata.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`.
- **Size:** L. **Depends on:** STORY-05.1.1.
- **Acceptance criteria:**
  - [ ] Given `ColumnVector` batches with nullable, decimal, timestamp, string, and nested values When the writer flushes a file Then a standards-compliant Parquet reader can read equivalent data.
  - [ ] Given target row-group and file-size settings When writing large input Then row groups, pages, dictionaries, and compression obey configured bounds.
  - [ ] Given min/max/null-count-capable columns When writing completes Then file, row-group, and column statistics are present or explicitly marked unavailable.
  - [ ] Given a writer crash before Delta commit When orphan Parquet files remain Then no reader treats those files as active table state.
- **Definition of done:** builds/tests/format pass; checklists `17`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-05.1.3: Object-store and PVC storage adapter contract

- **As a** storage implementer **I want** explicit backend primitives for object stores and PVCs **so that** Delta commit atomicity is implemented without unsafe rename or listing assumptions.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `cloud-native-security-sme`.
- **Size:** M. **Depends on:** EPIC-01.
- **Acceptance criteria:**
  - [ ] Given an object-store backend When a commit file is created Then the adapter uses conditional create or an equivalent single-winner primitive.
  - [ ] Given a PVC-backed filesystem When commit and data files are written Then required atomicity, fsync/durability, and rename behavior are documented and tested.
  - [ ] Given backend credentials and table paths When operations are executed Then requests are scoped to the configured table root and tenant boundary.
  - [ ] Given transient failures or ambiguous acknowledgments When adapter operations retry Then results are idempotent or surfaced as precise retry-unsafe errors.
- **Definition of done:** builds/tests/format pass; checklists `17`, `14`, `03a`, `04b` satisfied; docs updated if public API changes.

### FEAT-05.2: Delta log reading and snapshot reconstruction

- **Objective:** Reconstruct table state from Delta JSON actions and Parquet checkpoints while enforcing protocol versions and feature gates.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators none.
- **Depends on:** FEAT-05.1.

#### Stories

##### STORY-05.2.1: Delta JSON action parser and validator

- **As a** Delta reader **I want** JSON log actions parsed and validated **so that** table metadata, add/remove files, protocol, and transaction state are authoritative.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators none.
- **Size:** M. **Depends on:** FEAT-05.1.
- **Acceptance criteria:**
  - [ ] Given valid protocol, metadata, add, remove, txn, and commitInfo actions When a JSON commit is read Then typed actions preserve all fields required for replay.
  - [ ] Given malformed JSON, duplicate invalid actions, or unsupported protocol features When parsing occurs Then the reader fails with a versioned Delta protocol error.
  - [ ] Given partition values and file statistics in add actions When parsed Then values round-trip with schema-aware types and null semantics.
  - [ ] Given historical golden logs from multiple table versions When replayed Then action ordering and tombstone handling match expected active files.
- **Definition of done:** builds/tests/format pass; checklists `17`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-05.2.2: Checkpoint Parquet reader and snapshot builder

- **As a** snapshot consumer **I want** checkpoint-assisted replay **so that** large tables can be opened without replaying every JSON log from version zero.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators none.
- **Size:** L. **Depends on:** STORY-05.2.1, STORY-05.1.1.
- **Acceptance criteria:**
  - [ ] Given a table with Parquet checkpoints and later JSON commits When loading the latest version Then the snapshot uses the newest valid checkpoint and replays only newer commits.
  - [ ] Given missing, partial, or corrupt checkpoints When loading a table Then snapshot construction falls back safely or fails without inventing table state.
  - [ ] Given V1 and V2 checkpoint metadata When supported by protocol negotiation Then active files, tombstones, metadata, and transactions reconstruct identically to JSON replay.
  - [ ] Given a table with many files When snapshot loading completes Then metrics report replay depth, checkpoint version, active files, and load duration.
- **Definition of done:** builds/tests/format pass; checklists `17`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-05.2.3: Snapshot isolation API for scan planning

- **As a** query planner **I want** an immutable Delta snapshot contract **so that** scans observe a stable set of files and metadata for the life of an action.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** STORY-05.2.2.
- **Acceptance criteria:**
  - [ ] Given a snapshot version When concurrent commits happen Then existing snapshot file membership and schema remain unchanged.
  - [ ] Given projection, predicates, and partition filters When the planner asks for files Then the API returns candidate files plus residual statistics metadata without claiming correctness from skipping.
  - [ ] Given table metadata changes after snapshot creation When the snapshot is queried Then it reports the original metadata and version token.
  - [ ] Given unsupported table features When constructing a snapshot Then the API returns a deterministic unsupported-protocol result before planning scans.
- **Definition of done:** builds/tests/format pass; checklists `17`, `21`, `03a`, `04a` satisfied; docs updated if public API changes.

### FEAT-05.3: Delta write and commit protocol

- **Objective:** Implement append, overwrite, and transaction commit semantics with optimistic concurrency, explicit conflicts, retry safety, idempotency, and ACID visibility.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators none.
- **Depends on:** FEAT-05.1, FEAT-05.2.

#### Stories

##### STORY-05.3.1: Commit file creation and optimistic concurrency

- **As a** concurrent writer **I want** single-version commit attempts with conflict detection **so that** exactly one writer wins each Delta version and losing writers retry safely.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators none.
- **Size:** L. **Depends on:** STORY-05.1.3, STORY-05.2.1.
- **Acceptance criteria:**
  - [ ] Given two writers targeting the same next version When both commit Then exactly one commit file becomes visible and the loser observes a retryable conflict.
  - [ ] Given intervening metadata, protocol, delete, overwrite, or partition changes When a writer validates conflicts Then unsafe commits are rejected before publishing a new version.
  - [ ] Given append-only compatible concurrent appends When conflict rules allow retry Then the retried writer commits against the new version without duplicating data.
  - [ ] Given ambiguous backend commit acknowledgment When recovery inspects the log Then it determines whether the transaction committed or returns a precise unknown-state error.
- **Definition of done:** builds/tests/format pass; checklists `17`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-05.3.2: Idempotent write transactions and orphan cleanup contract

- **As a** reliable writer **I want** transaction identifiers and recovery rules **so that** retries, task failures, and driver restarts do not duplicate committed output.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators none.
- **Size:** M. **Depends on:** STORY-05.3.1.
- **Acceptance criteria:**
  - [ ] Given a retry with the same application transaction identifier When the prior transaction committed Then the writer reports success without adding duplicate files.
  - [ ] Given data files uploaded before a failed commit When table state is reconstructed Then uncommitted files remain inactive and eligible for safe cleanup.
  - [ ] Given speculative or repeated task outputs When the commit coordinator selects files Then only one logical output per task attempt is committed.
  - [ ] Given cleanup execution When orphan candidates are enumerated Then active files and retention-protected files are excluded.
- **Definition of done:** builds/tests/format pass; checklists `17`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-05.3.3: ACID append and overwrite operations

- **As a** table writer **I want** append and overwrite modes with Delta conflict rules **so that** user writes preserve serializable table semantics.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `data-platform-connectors-engineer`.
- **Size:** L. **Depends on:** STORY-05.3.1, STORY-05.3.2.
- **Acceptance criteria:**
  - [ ] Given append mode When a write commits Then new data files and statistics appear in add actions and previous active files remain active.
  - [ ] Given full-table overwrite When a write commits Then prior active files are removed in the same atomic version as new add actions.
  - [ ] Given dynamic partition overwrite When concurrent changes affect touched partitions Then conflict detection rejects unsafe overwrites.
  - [ ] Given a reader pinned to an old snapshot When overwrite commits Then the reader continues to see the old active file set until it refreshes.
- **Definition of done:** builds/tests/format pass; checklists `17`, `21`, `19`, `03a`, `04b` satisfied; docs updated if public API changes.

### FEAT-05.4: Time travel, schema enforcement, and column mapping

- **Objective:** Provide historical reads and safe schema behavior, including column mapping, without silently corrupting existing data or weakening Delta compatibility.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators none.
- **Depends on:** FEAT-05.2, FEAT-05.3.

#### Stories

##### STORY-05.4.1: Version and timestamp time travel

- **As a** Delta reader **I want** to load snapshots by version or timestamp **so that** historical queries and reproducible reads are possible.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators none.
- **Size:** M. **Depends on:** STORY-05.2.2.
- **Acceptance criteria:**
  - [ ] Given an existing version number When a snapshot is requested Then table state matches that exact log version.
  - [ ] Given a timestamp between commits When a snapshot is requested Then the selected version follows Delta timestamp resolution rules and is reported to the caller.
  - [ ] Given a requested version or timestamp older than retained logs When loading Then the reader fails with a retention-aware error rather than returning current data.
  - [ ] Given checkpoints after the requested version When time travel loads Then later checkpoints and commits are not used to mutate historical state.
- **Definition of done:** builds/tests/format pass; checklists `17`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-05.4.2: Schema enforcement and evolution rules

- **As a** table owner **I want** writes checked against Delta schema rules **so that** incompatible data is rejected and permitted evolution is explicit.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `data-platform-connectors-engineer`.
- **Size:** L. **Depends on:** STORY-05.3.3.
- **Acceptance criteria:**
  - [ ] Given a write with incompatible types, missing required columns, or invalid nullability When schema enforcement runs Then the write is rejected before any commit is published.
  - [ ] Given an allowed merge-schema or type-widening operation When evolution is enabled Then metadata changes and file additions commit atomically.
  - [ ] Given nested structs, decimals, timestamps, and case-sensitive column names When schemas are compared Then DeltaSharp applies deterministic compatibility rules.
  - [ ] Given concurrent schema changes When a writer validates its transaction Then stale-schema writes conflict and require refresh.
- **Definition of done:** builds/tests/format pass; checklists `17`, `19`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-05.4.3: Column mapping for rename and drop

- **As a** Delta table maintainer **I want** id-based column mapping **so that** supported renames and drops do not require unsafe data rewrites.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators none.
- **Size:** L. **Depends on:** STORY-05.4.2.
- **Acceptance criteria:**
  - [ ] Given a column-mapping-enabled table When a column is renamed Then existing Parquet files remain readable through stable column identifiers.
  - [ ] Given a metadata-only column drop When old snapshots are read Then historical schemas and data remain available according to their snapshot version.
  - [ ] Given a writer for a column-mapped table When producing Parquet files Then physical and logical column metadata is written consistently.
  - [ ] Given a table without required protocol features When column mapping is requested Then DeltaSharp rejects the operation with a protocol-upgrade requirement.
- **Definition of done:** builds/tests/format pass; checklists `17`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

### FEAT-05.5: Advanced Delta protocol features

- **Objective:** Implement ADR-0011 advanced features with explicit protocol negotiation: deletion vectors, Change Data Feed, liquid clustering, row tracking, and V2 checkpoints.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators none.
- **Depends on:** FEAT-05.2, FEAT-05.3, FEAT-05.4.

#### Stories

##### STORY-05.5.1: Deletion vectors for merge-on-read deletes

- **As a** Delta reader and writer **I want** deletion vectors respected in scans and commits **so that** row-level deletes and updates are ACID without rewriting every file.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-05.3.3.
- **Acceptance criteria:**
  - [ ] Given a file with a committed deletion vector When scanned Then deleted row positions are excluded from produced `ColumnVector` batches.
  - [ ] Given concurrent deletion-vector updates to the same file When committing Then conflict detection prevents lost deletes or updates.
  - [ ] Given a table without deletion-vector protocol support When a delete-vector operation is requested Then the operation fails or upgrades protocol explicitly.
  - [ ] Given time travel before and after a deletion-vector commit When reading Then row visibility matches the requested snapshot version.
- **Definition of done:** builds/tests/format pass; checklists `17`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-05.5.2: Change Data Feed generation and reads

- **As a** downstream consumer **I want** Change Data Feed records for table changes **so that** incremental consumers can process inserts, updates, and deletes.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `data-platform-connectors-engineer`.
- **Size:** L. **Depends on:** STORY-05.5.1.
- **Acceptance criteria:**
  - [ ] Given CDF-enabled inserts, deletes, and updates When commits complete Then change files or change metadata expose correct change types and commit versions.
  - [ ] Given a CDF read over a version range When changes are requested Then only changes in the inclusive range are returned in commit order.
  - [ ] Given CDF disabled for a table or historical range When a CDF read is requested Then DeltaSharp returns a clear unsupported-range error.
  - [ ] Given schema evolution during a CDF range When changes are read Then schema handling follows documented Delta compatibility rules.
- **Definition of done:** builds/tests/format pass; checklists `17`, `19`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-05.5.3: Liquid clustering, row tracking, and V2 checkpoints

- **As a** storage maintainer **I want** clustering metadata, stable row tracking, and V2 checkpoints **so that** modern Delta tables remain interoperable and efficiently maintainable.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `query-optimizer-scheduler-engineer`.
- **Size:** L. **Depends on:** STORY-05.4.3, STORY-05.5.2.
- **Acceptance criteria:**
  - [ ] Given liquid clustering metadata When files are added or optimized Then clustering columns and domain metadata are recorded and preserved during snapshot replay.
  - [ ] Given row-tracking-enabled writes When rows are committed Then stable row identifiers are generated according to the table protocol and remain valid across time travel.
  - [ ] Given a V2 checkpoint table When loading snapshots Then checkpoint contents reconstruct the same state as JSON replay for supported features.
  - [ ] Given unsupported advanced feature combinations When opening a table Then DeltaSharp fails with exact feature names and required protocol versions.
- **Definition of done:** builds/tests/format pass; checklists `17`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

### FEAT-05.6: Maintenance, compaction, vacuum, and statistics

- **Objective:** Make storage maintenance safe and useful: compact small files, optimize layout, vacuum only retention-safe files, and emit statistics consumed by data skipping and CBO/AQE.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `query-optimizer-scheduler-engineer`.
- **Depends on:** FEAT-05.1, FEAT-05.2, FEAT-05.3, FEAT-05.5.

#### Stories

##### STORY-05.6.1: OPTIMIZE and small-file compaction

- **As a** table maintainer **I want** compaction and OPTIMIZE operations **so that** small-file pressure is reduced without changing table contents.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `query-optimizer-scheduler-engineer`.
- **Size:** L. **Depends on:** STORY-05.3.3.
- **Acceptance criteria:**
  - [ ] Given many small active files When OPTIMIZE runs Then compacted files replace inputs in a single Delta commit and row counts/checksums remain equivalent.
  - [ ] Given concurrent writers touching candidate files When compaction commits Then conflict detection prevents removing newly changed files.
  - [ ] Given partition or clustering filters When OPTIMIZE is scoped Then only eligible files are compacted and unselected active files remain unchanged.
  - [ ] Given a failed compaction before commit When the table is read Then uncommitted compacted files are ignored and may be cleaned as orphans.
- **Definition of done:** builds/tests/format pass; checklists `17`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-05.6.2: VACUUM and retention safety

- **As a** table operator **I want** retention-aware VACUUM **so that** unreachable files are removed without breaking active or historical readers.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `privacy-compliance-grc-lead`.
- **Size:** M. **Depends on:** STORY-05.4.1, STORY-05.6.1.
- **Acceptance criteria:**
  - [ ] Given active files, tombstones, uncommitted orphan files, and retained history When VACUUM dry-run executes Then only deletion-eligible paths are listed.
  - [ ] Given a retention period below the configured safety threshold When VACUUM is requested Then the command is rejected unless an explicit unsafe override is enabled.
  - [ ] Given object-store listing lag or concurrent readers When VACUUM executes Then protected files are not deleted and audit output records decisions.
  - [ ] Given a VACUUM retry after partial deletion When it reruns Then already-deleted files are handled idempotently.
- **Definition of done:** builds/tests/format pass; checklists `17`, `14`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-05.6.3: Write-time statistics and data-skipping contract

- **As an** optimizer consumer **I want** storage-level statistics emitted at write time **so that** scans can skip data and CBO can consume trustworthy metadata.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `query-optimizer-scheduler-engineer`.
- **Size:** M. **Depends on:** STORY-05.1.2, STORY-05.3.3.
- **Acceptance criteria:**
  - [ ] Given supported scalar columns When files are written Then min, max, null count, record count, partition values, and byte sizes are recorded in add actions or related metadata.
  - [ ] Given unsupported, truncated, nested, or privacy-sensitive statistics When writing Then statistics are omitted or bounded according to documented rules.
  - [ ] Given scan predicates When snapshot planning uses statistics Then skipped files are reported as optimization decisions and never as correctness requirements.
  - [ ] Given optimizer requests for table/file statistics When queried Then the storage API returns freshness/version tokens and absence reasons.
- **Definition of done:** builds/tests/format pass; checklists `17`, `21`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

## Open questions

- Which object-store conditional-write primitives are mandatory for v1 support across S3, ADLS, and GCS, and which backends require an adapter-specific compatibility warning?
- What minimum Delta reader/writer protocol versions should DeltaSharp advertise when deletion vectors, CDF, liquid clustering, row tracking, and V2 checkpoints are enabled together?
- What default retention windows should balance time-travel guarantees, VACUUM safety, and storage cost for local/PVC development versus production object stores?
