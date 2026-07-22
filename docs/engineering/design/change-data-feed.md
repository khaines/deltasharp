# Change Data Feed (CDF) — generation & reads

> **Status:** Draft (design). Created for **STORY-05.5.2: Change Data Feed generation and reads** ([#193](https://github.com/khaines/deltasharp/issues/193)), Feature #50 (FEAT-05.5), Epic EPIC-05, Milestone **M2**.
> **Issue:** [#193](https://github.com/khaines/deltasharp/issues/193) · **Depends on:** STORY-05.5.1 deletion vectors ([#192](https://github.com/khaines/deltasharp/issues/192), merged).
> **Author:** `design-doc` skill.
> **Reviewers (personas):** `delta-storage-format-engineer` (primary), `cloud-native-distributed-systems-architect`, `data-platform-connectors-engineer`, `query-execution-engine-engineer`, `reliability-test-chaos-engineer`, `performance-benchmarking-engineer`, `cloud-native-security-sme`, `cloud-native-site-reliability-engineer`.
> **Last updated:** 2026-07-22.
> **Related:** [ADR-0011 Delta protocol scope](../../adr/0011-delta-protocol-scope.md), [ADR-0002 columnar batch format](../../adr/0002-columnar-batch-format.md), [ADR-0014 multi-targeting](../../adr/0014-target-framework-aot.md), [storage-delta-architecture.md](storage-delta-architecture.md) §2.14 / §3.2 (CDF is already phased there), [read-door.md](read-door.md), [write-door.md](write-door.md).

---

## 1 · Overview

### 1.1 What this is

**Change Data Feed (CDF)** exposes the row-level changes a Delta table underwent between two versions as a queryable feed. Each change row carries the table's data columns plus three metadata columns — `_change_type` (`insert` / `update_preimage` / `update_postimage` / `delete`), `_commit_version`, and `_commit_timestamp` — so an incremental consumer can process only what changed since it last read, rather than re-scanning the whole table.

This story delivers two halves behind the existing storage doors:

- **Generation (write door).** When a table has CDF enabled (`delta.enableChangeDataFeed = true`, backed by the `changeDataFeed` writer feature), commits that change data at the row level materialize their changes so a later reader can recover them. In DeltaSharp today the operations that produce row changes are **append** (INSERT), **overwrite**, and **merge-on-read DELETE** (deletion-vector-based, STORY-05.5.1 / #192). Append and overwrite need **no** change files — their change data is *derived* at read time from the committed `add`/`remove` actions. DELETE, because it re-adds the same physical file with a new deletion vector, **must materialize explicit `cdc` change files** (see §2.5).
- **Reads (read door).** A new **change-feed read mode** on the storage read facade (`DeltaReadSource`) replays a **version range** `[start, end]` inclusive, yielding change rows in commit order with the three metadata columns stamped.

### 1.2 Why it matters

CDF is a headline Delta feature and a **Spark-parity** surface (`spark.read.format("delta").option("readChangeFeed", true)`). It is the storage-level prerequisite for **incremental / streaming consumers** (EPIC-12 Structured Streaming reads a Delta source's change feed) and for downstream CDC pipelines. ADR-0011 commits v1 to a broad Delta feature set that explicitly includes CDF, protocol-gated and fail-closed. Landing CDF moves the `DeltaSharp.Storage` layer materially closer to feature-complete Delta and unblocks the streaming milestone.

It also matters for **correctness under the merge-on-read model**: DeltaSharp deletes via deletion vectors rather than file rewrites, so a naïve "removed file ⇒ deleted rows" derivation would be *wrong* for a DV delete. CDF forces us to make the generation contract explicit and mechanically checkable (§3.3).

### 1.3 Requirements traceability

No `REQ-*` requirements are referenced; the story is acceptance-criteria-driven. The four acceptance criteria from #193 are mapped to test scenarios in **§3.4**. CDF appears in the storage architecture's feature-phasing table as **Phase P6 / FEAT-05.5 / #193** and in its advanced-feature gating table ([storage-delta-architecture.md](storage-delta-architecture.md) §2.14): *"Change Data Feed (#193) · `changeDataFeed` (writer; `delta.enableChangeDataFeed`) · Unsupported CDF read fails closed; the base table stays readable."* This document refines that entry into an implementable design and **must stay consistent with it**.

---

## 2 · Logical architecture

### 2.1 Where CDF sits in `DeltaSharp.Storage`

CDF adds one new action type, one writer-feature gate, a generation hook on the DELETE path, and a new read mode. It introduces **no** new assembly and **no** Engine→Storage edge; it reuses the Parquet reader/writer, the commit engine, and the snapshot/log machinery already in place.

```mermaid
graph TD
  subgraph Executor["DeltaSharp.Executor (sink / scan-source)"]
    Sink["Delta sink (SaveMode → Append/Overwrite/Delete)"]
    Scan["Delta scan-source (readChangeFeed option)"]
  end

  subgraph Storage["DeltaSharp.Storage — public doors"]
    WT["DeltaWriteTarget<br/>(AppendAsync / OverwriteAsync)"]
    DEL["DeltaDelete<br/>(merge-on-read DELETE)"]
    RS["DeltaReadSource<br/>(+ LoadChangeFeed / ReadChangeBatches)"]
  end

  subgraph Internal["Storage internals"]
    CW["ChangeDataWriter<br/>(_change_data/*.parquet)"]
    CFR["ChangeFeedReader<br/>(version-range replay)"]
    COMM["DeltaCommitter (ACID, OCC)"]
    PROTO["ProtocolSupport<br/>(+ changeDataFeed writer feature)"]
    ACT["DeltaActions<br/>(+ AddCdcFileAction 'cdc')"]
    LOG["DeltaLog / Snapshot"]
    PQ["Parquet reader / writer + DeletionVectors"]
  end

  Sink --> WT
  Sink --> DEL
  Scan --> RS
  DEL -->|CDF on: deleted rows| CW
  CW --> COMM
  DEL --> COMM
  WT --> COMM
  COMM --> ACT
  COMM --> PROTO
  RS --> CFR
  CFR --> LOG
  CFR --> PQ
  CFR -->|cdc files| PQ
```

### 2.2 The change-data model: implicit vs explicit

CDF distinguishes two ways a commit's row changes are recovered on read. This is the single most important semantic in the design.

| Commit shape | How change data is recovered | DeltaSharp operations |
|---|---|---|
| **Implicit (derived)** — commit has **no** `cdc` actions | For each data action with `dataChange = true`: an `add`'s rows are `insert`; a `remove`'s rows are `delete`. Read the referenced Parquet files at read time. | **Append** (add ⇒ insert); **Overwrite** (remove ⇒ delete, add ⇒ insert). |
| **Explicit (materialized)** — commit **has** `cdc` actions | Change data for that version is **exactly** the rows in the `cdc` files (each carries its own `_change_type`). The commit's `add`/`remove` actions still define table *state* but are **not** re-derived for CDF (no double counting). | **Merge-on-read DELETE** (materializes `delete` rows). Future **UPDATE/MERGE** (`update_preimage` + `update_postimage`). |
| **Non-data (skipped)** — actions with `dataChange = false` | Contribute **no** change data. | **OPTIMIZE**/compaction already commit `add`/`remove` with `dataChange = false`. |

**Rule of precedence (Delta parity):** for a given version, *if any `cdc` action exists, the implicit derivation is suppressed for that whole version.* This is what makes the DV-delete correct (§2.5): the same-path `remove(old DV)+add(new DV)` pair would otherwise mis-derive as a spurious delete-then-insert.

### 2.3 The `cdc` (AddCDCFile) action

The action model in `src/DeltaSharp.Storage/Delta/DeltaActions.cs` is a **closed sealed-record set** (`ProtocolAction`, `MetadataAction`, `AddFileAction`, `RemoveFileAction`, `TxnAction`, `CommitInfoAction`) so replay is a total, exhaustive match. CDF adds one subtype:

```csharp
/// <summary>
/// cdc — a Change Data Feed change file (Delta protocol "Add CDC File"). Its Parquet file lives under
/// _change_data/ and holds the table's data columns plus a _change_type column. A cdc file is NEVER part
/// of table state: dataChange is ALWAYS false and snapshot reconstruction ignores it entirely — it is
/// consumed only by an explicit change-feed read (§2.6). Present only when the changeDataFeed writer
/// feature is active.
/// </summary>
internal sealed record AddCdcFileAction(
    string Path,
    ImmutableSortedDictionary<string, string?> PartitionValues,
    long Size,
    ImmutableSortedDictionary<string, string> Tags) : DeltaAction;
```

- Serialization/deserialization: extend `DeltaLogActionWriter` / `DeltaLogActionReader` with the `cdc` key. `dataChange` is written as `false` verbatim (it has no independent value). Unknown top-level keys already round-trip via the forward-compat rule; the reader must now *recognize* `cdc` rather than ignore it.
- **Snapshot reconstruction is unchanged:** `Snapshot`/`DeltaLog` replay must continue to ignore `cdc` actions when building the active-file set (a `cdc` file is never an active data file — §3.3 INV C1). This is the key isolation property that keeps a normal read of a CDF-enabled table byte-identical whether or not CDF is enabled.

### 2.4 Data model — CDF output schema

A change-feed read returns the table's data schema **plus three appended metadata columns**, in this order (Spark parity):

| Column | Type | Source |
|---|---|---|
| *(all table data columns)* | table schema at the version | the data / `cdc` file |
| `_change_type` | `string` (non-null) | embedded in the `cdc` file; or synthesized (`insert`/`delete`) on the implicit path |
| `_commit_version` | `long` (non-null) | the table version being replayed |
| `_commit_timestamp` | `timestamp` | the commit's timestamp (see §2.8) |

`cdc` files on disk store **only** the data columns + `_change_type`; `_commit_version` and `_commit_timestamp` are **stamped at read time** from the commit being replayed (they are constant per version, so materializing them per row would waste space). Under **column mapping**, `cdc` file data columns use physical `col-<uuid>` names exactly like data files; the metadata columns are logical, engine-synthesized, and never column-mapped. **Partition columns** are likewise *not* stored in the `cdc` file body (exactly as for data files): the read door re-derives them from `AddCdcFileAction.PartitionValues` and const/null-fills them into the output batch (§2.6) — the inverse of the write door's partitioner, symmetric with how the snapshot door re-derives partition columns from `add.partitionValues`.

### 2.5 Generation — the write door

**Append / INSERT (`DeltaWriteTarget.AppendAsync`).** No change files. The committed `add` actions (`dataChange = true`) are the change data; a reader derives `insert` rows from them (§2.2). The only write-side obligation is **enablement** (§2.7): a first write to a table configured with `delta.enableChangeDataFeed = true` must publish a protocol that carries the `changeDataFeed` writer feature.

**Overwrite (`DeltaWriteTarget.OverwriteAsync`).** No change files. Static/dynamic overwrite already commits `remove(old, dataChange=true) + add(new, dataChange=true)` atomically; a reader derives `delete` (old rows) + `insert` (new rows). *Caveat (Spark parity nuance):* Spark can emit `cdc` for `replaceWhere`-style predicated overwrites to avoid surfacing unchanged rows; DeltaSharp's overwrite is whole-table/whole-partition, so the derived representation is faithful. Recorded as an open question (§9 Q3) should predicated overwrite land. **Durability caveat (retention).** The `delete` side is *derived* by reading the **removed** data files at CDF-read time, so those files must survive within the CDF-readable window: VACUUM/orphan-cleanup must protect removed data files still referenced by an **in-window implicit-delete derivation**, not only `_change_data/`/`.bin` sidecars (§2.12, §3.3 INV C7, #489). Append is unaffected (its `add` files stay active); DELETE is unaffected (it materializes `cdc`, and precedence suppresses derivation).

**Merge-on-read DELETE (`DeltaDelete`) — the materialization path.** This is the core generation work. `DeltaDelete` (`src/DeltaSharp.Storage/Delta/DeltaDelete.cs`) today builds one atomic commit per affected file: `remove(old add, prior DV) + add(same path, new DV)`, both `dataChange = true`, then inserts `DeltaCommitInfo.Delete()` at index 0 and commits via `DeltaCommitter.CommitAsync`. When CDF is enabled, that same loop additionally:

1. Collects the **newly-deleted rows** for the file — the file-relative row positions matched by *this* DELETE that the *old* DV did not already mask (`DeltaDelete`'s `FileDeletionPlan.NewlyDeletedCount`, which is computed for **both** delete branches). This must cover **both** branches or a mixed commit silently loses deletes (§3.3 INV C2/C3): a **partially-deleted** file (retained with a new DV) contributes `newDV \ oldDV`; a **fully-deleted** file (removed outright via a bare `remove`, no residual `add`/DV — `DeltaDelete.cs` `filesFullyDeleted`) contributes `physical \ oldDV` (its remaining live rows). Both sets are **captured in the same scan pass** that plans the deletion — `PlanFileDeletionAsync`/`FileDeletionPlan` are extended to emit the newly-deleted positions **and their row payloads** (today the plan retains only a *count* plus the merged new-DV set), so there is no second full-file scan (§4.3).
2. Writes those rows (full table schema, physical/column-mapped) to a **`_change_data/`** Parquet file via a new internal `ChangeDataWriter` (whose file names come from the writer's **existing deterministic file-naming seam** — no `Guid.NewGuid`, so golden fixtures stay reproducible), adding a synthesized `_change_type = 'delete'` column.
3. Appends an `AddCdcFileAction` for that file to the same `actions` list, so the `cdc` file is published **atomically** in the DELETE commit (never a separate commit).

Because the commit now carries `cdc` actions, the read-time precedence rule (§2.2) suppresses the implicit derivation for that version — so the same-path `remove+add(new DV)` pair does **not** mis-derive as delete+insert. **Completeness rule (§3.3 INV C3):** *any* commit that emits *any* `cdc` action MUST materialize **every** row change in that version as `cdc` (here, the deletes from **both** the partial- and fully-deleted branches). Because precedence suppression drops *all* implicit derivation for a `cdc`-bearing version, a partial `cdc` set would silently lose the un-materialized changes — a future UPDATE/MERGE (which mixes inserts and updates in one commit) must honor the same rule. Deletion metrics extend `DeltaCommitInfo.Delete()` to emit `operationMetrics` (`numDeletedRows`, `numChangeFilesAdded`), following the exact typed-token pattern `DeltaCommitInfo.Optimize(...)` already uses.

**UPDATE / MERGE.** DeltaSharp has **no** UPDATE or MERGE operation today (only DELETE, append, overwrite). Therefore `update_preimage` / `update_postimage` records cannot be produced by any current operation. This design **defines the contract** (an UPDATE/MERGE would materialize a preimage+postimage `cdc` pair under the same completeness rule above) and leaves generation to the story that introduces those commands — tracked as **[#637](https://github.com/khaines/deltasharp/issues/637)** (§9 Q1). Consequently issue #193's AC1 *update* change-type is only **partially** discharged by this story; see §3.4.

### 2.6 Reads — the change-feed read door

CDF reads are a **distinct read mode** from snapshot reads. They live on the existing public read facade `DeltaReadSource` (`src/DeltaSharp.Storage/Reading/DeltaReadSource.cs`), whose XML doc currently lists *"CDF reads (#193)"* as out of scope — this story removes that exclusion. Two new members parallel the snapshot pair (`LoadSnapshotAsync` / `ReadBatchesAsync`), with **one deliberate deviation** on the read half's return shape (called out below):

- `LoadChangeFeedAsync(range, ct)` — takes a `DeltaChangeFeedRange` (§2.9), resolves and **validates** the version range once (avoiding an analysis→execution TOCTOU exactly like snapshot pinning), and returns a `DeltaChangeFeedInfo` (resolved `[startVersion, endVersion]` + reconciled output schema, §2.8).
- `ReadChangeBatchesAsync(info, ct)` → **`IAsyncEnumerable<ColumnBatch>`** — streams change rows (data columns + the three metadata columns) in **ascending commit order**. This deliberately **deviates** from the snapshot door's materialized `Task<IReadOnlyList<ColumnBatch>>` (`ReadBatchesAsync`, `DeltaReadSource.cs:128`): a version-range replay is **unbounded** in a way a pinned snapshot is not, so the read half is pull-based and consumer-paced (natural backpressure; bounded per-batch decode buffers, §4.3). **Batch-boundary invariant (§3.3 INV C8):** a produced `ColumnBatch` carries exactly **one** `_commit_version` (never spans versions) — which is what lets `_commit_version`/`_commit_timestamp` be constant per batch (§4.2).

```mermaid
sequenceDiagram
  participant C as Executor scan-source
  participant RS as DeltaReadSource
  participant LOG as DeltaLog
  participant PQ as Parquet reader (+DV)
  C->>RS: LoadChangeFeedAsync(start,end)
  RS->>LOG: resolve range, read protocol+metadata per version
  RS->>RS: validate range + CDF enablement + reconcile schema
  RS-->>C: resolved [v0..vN], output schema
  C->>RS: ReadChangeBatchesAsync(resolved)
  loop version v = v0..vN (ascending)
    RS->>LOG: read commit v actions
    alt commit has cdc actions
      RS->>PQ: read _change_data files (has _change_type)
    else no cdc actions (implicit)
      RS->>PQ: read add(dataChange) files ⇒ insert
      RS->>PQ: read remove(dataChange) files ⇒ delete (DV-aware)
    end
    RS->>RS: stamp _commit_version=v, _commit_timestamp
    RS-->>C: ColumnBatch(es) for v
  end
```

**Range resolution & validation (AC2, AC3).** `start`/`end` may each be given as a version or a timestamp (`startingVersion`/`endingVersion`/`startingTimestamp`/`endingTimestamp`, Spark parity); the two endpoints resolve **independently**, so `startingVersion` + `endingTimestamp` is legal (each endpoint is a version *xor* a timestamp — see §2.9). `end` defaults to the latest committed version. The read **fails closed** with a clear, classified error when: `start > end`; `start < 0`; `end >` latest; **CDF was not enabled for every version in `[start, end]`** (§2.7); or **the requested range is no longer available** — the `start` version's commit JSON has aged past `delta.logRetentionDuration`, or a `_change_data`/removed data file the range needs was reclaimed by VACUUM. The **CDF-readable window** is therefore bounded by `min(logRetentionDuration, deletedFileRetentionDuration)`. Only changes within the inclusive range are returned, in commit order (AC2).

**Partition-column reconstruction.** On **both** read paths the change-feed door const/null-fills partition columns into the output batch — from `cdc.partitionValues` on the explicit path and from `add.partitionValues`/`remove.partitionValues` on the implicit path — exactly as the snapshot door does for data files (partition columns live only on the action + Hive path, never in the file body). A partitioned CDF-enabled table therefore surfaces its partition-column values on every change row.

**Pushdown & residuals.** Column/partition pruning and predicate pushdown **into** the CDF storage read are **out of scope for the storage door** (symmetric with the snapshot door, which declares the same): the door returns full-schema change batches and treats **all** predicates as *residual*, to be evaluated by the Executor scan-source / query planner (checklist 16). The three metadata columns (`_change_type`/`_commit_version`/`_commit_timestamp`) are engine-synthesized and never pushable.

**Implicit-path DV awareness.** When deriving `insert`/`delete` from `add`/`remove` on the implicit path, the referenced data files may carry deletion vectors; the existing DV-aware scan is reused so only *live* physical rows are surfaced (a row already deleted by a prior DV must not reappear as an `insert`).

### 2.7 Protocol negotiation & enablement

CDF is a **writer-only** feature: normal reads of a CDF-enabled table need no reader feature (snapshot reconstruction ignores `cdc` actions — §2.3), so `ProtocolSupport.SupportedReaderFeatures` is **unchanged**. The writer side gains one feature:

- Add `ChangeDataFeedFeature.Feature = "changeDataFeed"` to `ProtocolSupport.SupportedWriterFeatures`. CDF writes fail closed **today** because `changeDataFeed` is *absent* from that whitelist — `EnsureWritable` rejects any writer feature it does not contain (fail-closed-by-omission); `change-data-feed` appears only *illustratively* in the `EnsureWritable` doc-comment (a stale example that also lists the now-supported `constraints`/`column mapping`). This story adds `changeDataFeed` to the whitelist, makes the writer actually honor it, and should refresh that stale comment.
- **Enablement seam.** The table property `delta.enableChangeDataFeed=true` lives in `MetadataAction.Configuration`. On CREATE/ALTER with that property set, the published `protocol` must include the `changeDataFeed` writer feature and be at the table-features writer version (7), mirroring how `appendOnly` / `invariants` / `typeWidening` enablement is threaded on the legacy→table-features upgrade (the `TypeWideningFeature.UpgradeProtocol` / #549 pattern).
- **Fail-closed reads (AC3).** A CDF **read** against a table (or version range) where CDF is not enabled throws a precise unsupported-range error naming the offending versions — consistent with the storage doc's EE-10 scenario and the "unsupported fails closed" posture.

**Conservative scope (stricter than Delta).** Delta permits "batch-derivable" CDF reads over versions where CDF was disabled *if* those commits are simple blind append/overwrite/whole-file-delete. DeltaSharp v1 instead **requires CDF enabled across the whole requested range** and fails closed otherwise. Rationale: DeltaSharp's DELETE is DV-based, so a disabled-CDF range that contains a delete cannot be faithfully derived; requiring enablement is the safe, correct default and matches the repo's stricter-than-Delta protocol philosophy. Relaxing to batch-derivable ranges is a future enhancement (§9 Q2).

### 2.8 Schema evolution & commit timestamp

**Schema evolution across a range (AC4).** When the table schema changed within `[start, end]`, each version's change data is read against **that version's** schema, then reconciled to a **single output schema** (default: the schema at `endVersion`, i.e. latest-in-range — Spark parity). Reconciliation reuses `DeltaReadSource`'s existing additive/widening read-compatibility: later-added **nullable** columns are null-filled for earlier versions, and type-widened columns are up-cast. A change that is **not** read-compatible over the range fails closed with the existing `DeltaReadSchemaEvolutionException`, rather than fabricating values. Precisely (matching the read door's contract): a column **dropped before `endVersion`** is simply absent from the reconciled output and is **not** an error (it is not projected); the genuine fail-closed cases are a **required (non-nullable)** output column absent from an earlier version's change data (cannot null-fill) or an **incompatible retype** (a narrowing/lossy change no widening rule covers). The precise reconciliation target (latest-in-range vs. a caller-supplied read schema) is confirmed in §9 Q4.

**`_commit_timestamp` source.** To stay consistent with how DeltaSharp resolves time **today**, `_commit_timestamp` uses the **same monotonic `<N>.json` commit-file modification-time policy** that time-travel-by-timestamp uses (`DeltaLog`) — **not** `commitInfo.timestamp`. `commitInfo.timestamp` *is* stamped deterministically (injected `TimeProvider` in `DeltaCommitter`) but is **provenance-only** and is not currently used for timestamp resolution; adopting it for cross-engine parity is tracked by **#500**, and when it lands it must change stamping **and** range-resolution together so the two never diverge. Until then a §3 oracle pins that a change row's `_commit_timestamp` equals the version's resolved time-travel timestamp. No new clock is introduced; **no** `DateTime.UtcNow`/`Guid.NewGuid` (BannedSymbols).

### 2.9 Public API surface

The storage seam surfaces only Engine types (`ColumnBatch` / `StructType`) across the boundary (ADR-0014); no Core/Executor type crosses it. New public members (final names TBD in review, `<!-- TBD -->`):

- `DeltaReadSource.LoadChangeFeedAsync(DeltaChangeFeedRange range, CancellationToken)` → `Task<DeltaChangeFeedInfo>`.
- `DeltaReadSource.ReadChangeBatchesAsync(DeltaChangeFeedInfo info, CancellationToken)` → `IAsyncEnumerable<ColumnBatch>` (a deliberate streaming deviation from the snapshot door's materialized list — §2.6).
- `DeltaChangeFeedRange` — a `public readonly record struct` (value semantics, matching `DeltaSnapshotInfo`/`DeltaWriteResult`) carrying the start and end bounds. **Each endpoint independently** is a version *xor* a timestamp (a *single* endpoint may not carry both — this mirrors `LoadSnapshotAsync`'s existing `versionAsOf` xor `timestampAsOf` rule); mixing *across* endpoints (`startingVersion` + `endingTimestamp`) is **allowed** (Spark parity).
- `DeltaChangeFeedInfo` — a `public readonly record struct` (`long StartVersion`, `long EndVersion`, `StructType Schema`) — the resolved range + reconciled output schema.
- Metadata-column name constants (`_change_type`, `_commit_version`, `_commit_timestamp`).

The **Spark-facing** `option("readChangeFeed", true).option("startingVersion", …)` surface is wired by the Executor's Delta scan-source (EPIC-06 / #499 territory) onto these storage members; that mapping is out of scope here and referenced only. Public-API additions go through `PublicAPI.Unshipped.txt` and the API-governance gate.

### 2.10 Dependencies

| Depends on | Why |
|---|---|
| STORY-05.5.1 deletion vectors (#192, merged) | DELETE materializes `cdc` from the DV delta; the implicit path is DV-aware. |
| Parquet reader/writer, `ParquetTypeMapping` | Read/write `_change_data/` files; column-mapping physical names. |
| `DeltaCommitter` / OCC | Atomic publication of `cdc` with the DELETE commit. |
| `DeltaLog` / `Snapshot` / time-travel | Version-range replay, `_commit_timestamp` resolution. |
| Column mapping (name+id) | `cdc` data columns are column-mapped like data files. |
| **#489** VACUUM retention protection | `_change_data/` `cdc` files **and** derivation-referenced removed data files must be VACUUM-protected within the CDF window (§2.12); blocks generation GA (§9 Q5). |
| ADR-0011, ADR-0002, ADR-0014 | Protocol scope, columnar batch, target-framework seam. |

No new third-party NuGet dependency is introduced. Two follow-ups are filed rather than in-scope: **[#637](https://github.com/khaines/deltasharp/issues/637)** (update change-types when UPDATE/MERGE lands) and **[#638](https://github.com/khaines/deltasharp/issues/638)** (a constant/run-length `ColumnVector` for O(1)-byte metadata-column materialization, §4.3).

### 2.11 Tenant / storage-backend considerations

`_change_data/` files are written and read through the same pluggable storage backend abstraction as data files (local FS today; S3/ADLS/GCS and PVCs behind the same contract). Path confinement (`#431`/#474 openat/`O_NOFOLLOW`) applies unchanged: `cdc` `path` values are resolved **relative to the table root** and confined exactly like `add.path`, so an attacker-controlled `cdc` path cannot escape the table directory. CDF adds no cross-tenant data flow: a change feed is scoped to a single table path, and the reader never lists directories to find change data — it follows only `cdc`/`add`/`remove` actions committed to that table's log (log-is-truth, INV C1/I12).

### 2.12 Interaction with existing features

- **Deletion vectors:** §2.5 (generation) and §2.6 (DV-aware implicit derivation). The DV delta *is* the delete change set.
- **OPTIMIZE / compaction:** commits with `dataChange = false` contribute no change data — compaction is invisible to CDF (correct; it changes no logical rows). No change needed; add a regression oracle (§3.3 INV C4).
- **VACUUM / retention:** two classes of file must stay within the CDF-readable window and be protected by VACUUM/orphan-cleanup. (1) `_change_data/` `cdc` files referenced by an in-window `cdc` action; (2) **removed data files** still referenced by an in-window **implicit-delete derivation** (the overwrite `delete` side, §2.5). This is the concern filed as **#489** — whose remaining scope is exactly these two classes: the DV `.bin` sidecar half **already landed** (`OrphanCleanup` protects active + tombstone DV sidecars today), so #489 now covers `_change_data/` CDF files **and** derivation-referenced removed data files. This design **depends on** #489 landing with (or before) CDF generation; flagged §9 Q5.
- **Time travel:** orthogonal — time travel reads a single snapshot; CDF reads a range. They share range/version resolution and the `_commit_timestamp` policy.
- **Column mapping:** `cdc` data columns follow the table's physical naming; metadata columns are never mapped.

---

## 3 · Functional test scenarios & correctness-under-fault

> **Proof, not assertion** (governing rule from checklist 21). Every scenario names a **mechanical oracle** — a checkable predicate over `_delta_log`, `_change_data/` bytes, and produced `ColumnBatch`es — a captured **seed/fixture**, and a **reproduction** filter. A change-feed test whose oracle cannot classify a history is *insufficient coverage*, never green. Placement per `testing-conventions.md`: deterministic/unit-tier in `tests/DeltaSharp.Storage.Tests`; cross-engine/emulator work in the integration tier (checklist 04b, 900 s budget).

### 3.1 Happy-path scenarios

| ID | Scenario | Oracle (mechanical pass condition) | AC |
|----|----------|-----------------------------------|----|
| CDF-HP-01 | Append then CDF-read `[v,v]` | Change multiset == appended rows, each `_change_type=insert`, `_commit_version=v`; **no** `_change_data/` file was written | AC1 |
| CDF-HP-02 | DV DELETE then CDF-read `[v,v]` | Change multiset == exactly the newly-deleted rows, each `_change_type=delete`; a `cdc` file exists; the re-added file does **not** surface as `insert` | AC1 |
| CDF-HP-03 | Overwrite then CDF-read `[v,v]` | Old rows `delete` + new rows `insert`, derived (no `cdc` file) | AC1 |
| CDF-HP-04 | Multi-version range `[a,b]` (append, delete, append) | Union of per-version changes, **commit order** ascending, only versions in `[a,b]` present | AC2 |
| CDF-HP-05 | `_commit_timestamp` stamping | Each change row's `_commit_timestamp` == the commit's resolved timestamp (injected `TimeProvider`) | AC1 |
| CDF-HP-06 | Timestamp-bounded range | `startingTimestamp`/`endingTimestamp` resolve to the same versions as the equivalent version bounds | AC2 |
| CDF-HP-07 | Range spanning a **read-compatible schema change** (nullable column added mid-range) | Pre-evolution change rows **null-fill** the added column to match the reconciled `endVersion` schema; post-evolution rows carry real values; output schema == `endVersion` schema | AC4 |
| CDF-HP-08 | **Mixed DELETE commit**: one file partially deleted (new DV) + one file fully deleted (bare `remove`) in the same version | **Both** files' newly-deleted rows appear as `delete` change rows (INV C2); the fully-deleted file's rows are **not** dropped by precedence suppression | AC1 |
| CDF-HP-09 | Partitioned table: CDF-enabled DELETE (explicit) + append (implicit) | Every change row carries its partition-column values (re-derived from `cdc.partitionValues` / `add.partitionValues`) on **both** paths (INV C9) | AC1 |

### 3.2 Edge / error scenarios

Contract: **fail deterministically, name the defect, publish no partial state, fail closed.**

| ID | Scenario | Oracle | AC |
|----|----------|--------|----|
| CDF-EE-01 | CDF read on a table with CDF **disabled** | Clear unsupported error naming the table; no rows produced (aligns with storage-doc EE-10) | AC3 |
| CDF-EE-02 | Range spans a version where CDF was **not yet enabled** | Unsupported-range error naming the offending version(s) | AC3 |
| CDF-EE-03 | Inverted / out-of-bounds range (`start>end`, `end>`latest, `start<0`) | Deterministic invalid-range error; nothing read | AC2 |
| CDF-EE-04 | Both a version bound and a timestamp bound supplied | `ArgumentException` (mirrors `LoadSnapshotAsync`'s XOR rule) | AC2 |
| CDF-EE-05 | Non-read-compatible schema change within the range (dropped/retyped column) | `DeltaReadSchemaEvolutionException`; never fabricates values | AC4 |
| CDF-EE-06 | Writer-feature fail-closed: write to a `changeDataFeed` table on a build without CDF | `DeltaProtocolException` naming the writer feature (regression that the gate is real) | AC3 |
| CDF-EE-07 | Corrupt / truncated `_change_data/` Parquet file | Deterministic storage error; zero partial rows (INV I11 parity) | AC1 |
| CDF-EE-08 | A root-confined `cdc` file whose **embedded schema mismatches** the version's schema (hostile/inconsistent log) | The decoded `cdc` schema is validated against the version's reconciled schema **before** any row is yielded; a mismatch fails closed (`DeltaReadSchemaEvolutionException` / classified CDF error) — never projects attacker-chosen columns (§5.2) | AC1/AC4 |
| CDF-EE-09 | Range whose `start` commit JSON aged past `logRetentionDuration`, or a needed `_change_data`/removed data file was VACUUMed | Deterministic "version outside CDF-readable window" error; never a silent partial/empty feed | AC3 |

### 3.3 Deterministic correctness oracles

New invariants layered on the storage invariant catalogue (I1–I12):

| # | Invariant | Statement |
|---|-----------|-----------|
| C1 | CDF isolation | A `cdc` action is **never** part of the active-file set; a normal snapshot read of a CDF-enabled table is byte-identical to the same table with CDF off. |
| C2 | Delete completeness | For a DELETE at version `v`, the `delete` change set == the **newly-deleted** physical rows of **every** affected file — `newDV \ oldDV` for a partially-deleted file (retained with a new DV) **and** `physical \ oldDV` for a fully-deleted file (removed outright) — no more, no fewer. |
| C3 | Precedence | If version `v` has any `cdc` action, the implicit add/remove derivation is suppressed for `v` (no double counting, no spurious insert from a DV re-add); by C2 that version's `cdc` set is **complete**, so suppression drops nothing. |
| C4 | Compaction invisibility | An OPTIMIZE commit (`dataChange=false`) contributes **zero** change rows over any range that contains it. |
| C5 | Range fidelity | CDF-read`[a,b]` == concatenation of CDF-read`[a,a]` … `[b,b]` (order-preserving), and is disjoint from changes outside `[a,b]`. |
| C6 | Round-trip vs. snapshot | Replaying all `insert`/`delete`/`update_postimage` (minus preimages) from `[0,N]` reconstructs the multiset of `snapshot(N)` rows (a "CDF folds to snapshot" cross-check). |
| C7 | Derivation-file retention | Every file an **in-window implicit derivation** must read (a removed data file behind an overwrite `delete`) is retention-protected for the whole CDF-readable window — VACUUM never reclaims it while a range referencing it is readable (§2.5, #489). |
| C8 | Single-version batch | A produced change `ColumnBatch` carries exactly **one** `_commit_version` (never spans versions), so the two metadata columns are constant per batch (§2.6). |
| C9 | Partition fidelity | Every change row surfaces its partition-column values, re-derived from `cdc.partitionValues` (explicit) or `add`/`remove.partitionValues` (implicit) — never null-dropped for a partitioned table. |

**Oracles:** (a) **golden `_delta_log` + `_change_data` histories** with per-version expected change manifests (curated + generated; cross-engine goldens written by Spark/delta-rs for interop-in); (b) a **model-based state machine** extending oracle (c) of the storage doc — the model tracks, per committed version, the expected change multiset with `_change_type`, and the harness asserts real CDF-read == model over random *legal* command sequences (`Append`, `Overwrite`, `Delete(predicate)`, `Optimize`, `EnableCdf`); (c) the **CDF-folds-to-snapshot** differential (C6) as a strong end-to-end check. Every randomized case emits the standard `[deltasharp-seed]` reproduction line and records `{seed, schema, command-sequence, backend, expected change-manifest}`.

### 3.4 Acceptance-criteria mapping

| # | Acceptance criterion (#193) | Discharged by |
|---|------------------------------|---------------|
| AC1 | CDF-enabled inserts/deletes expose correct change types & commit versions | CDF-HP-01/02/03/05/08/09; CDF-EE-07/08; INV C2/C9. **Updates: partially met** — no UPDATE/MERGE op exists, so `update_*` change-types are a tracked deferral to **[#637](https://github.com/khaines/deltasharp/issues/637)** (see note) |
| AC2 | CDF read over a version range returns only in-range changes in commit order | CDF-HP-04/06; CDF-EE-03/04; INV C5 |
| AC3 | CDF disabled / historical range → clear unsupported-range error | CDF-EE-01/02/06/09; §2.7 |
| AC4 | Schema evolution during a CDF range follows documented compatibility rules | CDF-HP-07; CDF-EE-05/08; §2.8 |

> **Note on AC1 "updates" (partial discharge).** Issue #193's AC1 lists inserts, deletes, **and updates**. DeltaSharp exposes no UPDATE/MERGE operation yet, so no commit can currently produce `update_preimage`/`update_postimage`; AC1 is therefore **fully** discharged for **insert** and **delete** and only **partially** discharged overall. The update change-type is **not** claimed complete here — its contract is defined (§2.5) and its generation is a **tracked, blocking deferral** to **[#637](https://github.com/khaines/deltasharp/issues/637)**, which must land before AC1 is fully met. This is an explicit, reviewed scope boundary with a filed issue, not a silent gap or a false "satisfied" claim.

---

## 4 · Performance

> Scope: the CDF write hot path (materializing delete change files) and the CDF read hot path (version-range replay + metadata-column stamping). Figures are expressed as SLIs (shape and ratios to a measured noise floor); hardware constants are `<!-- TBD: calibrate on ref hardware -->`.

### 4.1 Workload profile

- **Generation** is incremental to DELETE: it writes one `_change_data/` file per affected data file, sized to the *deleted* row count (typically ≪ the data file). Append/overwrite add **zero** generation cost.
- **Read** is proportional to the number of changed rows in `[start, end]`, not table size — CDF's central value. Cost = replay of the commit range (log I/O, already bounded) + Parquet decode of `cdc`/`add`/`remove` files + constant-per-version metadata stamping.

### 4.2 Targets (SLIs)

- **Generation overhead:** a DV DELETE with CDF on writes `O(deletedRows)` extra bytes and one extra file per affected data file; end-to-end DELETE latency stays within **a small constant factor** of the DV-only DELETE `<!-- TBD: calibrate the multiple per delete-fraction fixture -->` (the ratio is delete-fraction-sensitive — a high-selectivity delete produces a `cdc` file approaching the data-file size — so no fixed multiple is committed pre-calibration).
- **Metadata stamping:** `_commit_version`/`_commit_timestamp` are **constant per version** (one value per batch, INV C8). With today's `DeltaSharp.Engine.Columnar` types (which have no constant/run-length vector — every concrete `ColumnVector` allocates an `O(rows)` backing buffer) the achievable budget is **`O(1)` allocations per batch at steady state** (a reused fixed-width vector `Clear()`ed and refilled retains capacity) but **`O(rows)` bytes/writes**. A true `O(1)`-*bytes* constant/run-length column is a future ADR-0002 enhancement tracked as **[#638](https://github.com/khaines/deltasharp/issues/638)** (not required for correctness). See §4.3.
- **Read throughput:** CDF-read of a small change set over a large table must **not** scan unchanged files — verified structurally (§4.4 gate: files opened ⊆ files referenced by in-range `cdc`/`dataChange` actions, and **zero** unchanged/snapshot files opened).

### 4.3 Memory & allocation budgets

- Metadata columns are materialized once per batch into a **reused fixed-width `ColumnVector`** (`Clear()` retains capacity → `O(1)` allocations/batch at steady state; `O(rows)` bytes with today's types — see §4.2 and #638); no per-row boxing of `_commit_version`/`_commit_timestamp`.
- The delete-row selection for `cdc` generation is **captured in the same scan pass** that plans the deletion (`PlanFileDeletionAsync` extended to emit newly-deleted positions + rows, §2.5); no second full-file scan.
- `ChangeDataWriter` streams row groups through the existing Parquet writer's buffers; no whole-file materialization beyond one row-group buffer.

### 4.4 Benchmark methodology & regression gates

Reconciled with the parent doc's **two-tier** posture ([storage-delta-architecture.md](storage-delta-architecture.md) §4.5): the engine/storage assemblies use **in-assembly, allocation-free kernel harnesses that are deliberately NOT BenchmarkDotNet** (BDN's package/lock-file + dynamic-codegen fights the NativeAOT posture); BDN is confined to a **dedicated, non-shipping `bench/` project** referenced by no `src/` assembly.

- **(1) DELETE-with-CDF vs DELETE-only** generation overhead and **(2) CDF-read throughput vs. change-set size** (fixed table size) are **deterministic macro/integration benchmarks** (seeded golden fixtures + adapter-counted I/O, parent §4.5), reporting **p50/p95/p99/p99.9 + end-to-end** against a measured noise floor (checklist 22; parent §4.2.2), not means.
- **(3) metadata-stamping allocations/batch** is a hot-path **micro**, run in the kernel-harness house style (or the isolated BDN project with `[MemoryDiagnoser]`).

**Gates (checklist 22; parent §4.6):** a **structural** gate — *files opened ⊆ in-range changed files, zero unchanged/snapshot files* (§4.2), adapter-counted like the parent's COMMIT-1/MAINT-1 gates; and an **allocation** gate on stamping (`O(1)` allocations/batch — an allocation regression fails even if wall-clock is flat). Comparison is benchstat-style with change-point/drift detection; each run records the pinned-environment fingerprint (parent §4.2.1: SDK/runtime, GC mode, commit SHA). No absolute numbers are committed until calibrated on the reference environment.

---

## 5 · Security

### 5.1 Data classification

| Element | Classification | Handling |
|---|---|---|
| `_change_data/*.parquet` row values | **Same as table data** (may contain PII/restricted) | Same at-rest/in-transit protection as data files; retained/vacuumed under the same policy (§2.12, #489). |
| `cdc.partitionValues` (in the commit log) | **Same as table data** when a partition column carries PII | Persists under **log** retention, not the CDF/VACUUM data window — an erasure-completeness caveat (see privacy note). |
| `_change_type` / `_commit_version` / `_commit_timestamp` | Metadata (low sensitivity) | Non-PII; `_commit_timestamp` is best-effort provenance and forgeable on a hostile log (§6.2). |
| `cdc.path` in the log | Attacker-controllable on a hostile log | Path-confined to the table root (§2.11); redact in fault messages (mirrors #516). |

**Privacy note (GDPR erasure) — the standout CDF concern.** CDF *retains a copy of deleted rows* in `_change_data/` (and their identifying `cdc.partitionValues` in the commit log) until they age out and are VACUUMed. So a "right to be forgotten" delete is **not physically erased until the change files are vacuumed**, and the **effective erasure latency is bounded by `max(CDF-readable window, deletedFileRetentionDuration)`** — operator-configurable and, if retention is long, potentially weeks/months. Two consequences: (1) an operator with an erasure SLA must **bound VACUUM retention** accordingly (CDF *raises the effective erasure floor*); (2) time-based VACUUM offers **no subject-targeted purge** — a specific subject's rows cannot be removed ahead of the retention interval, so a **targeted CDF purge** affordance (rewrite/removal of specific change files) is an open question (§9 Q7). Partition-column PII additionally persists in the **log** under *log* retention, not the data-VACUUM window (§5.1 table). Handoff to `privacy-compliance-grc-lead`; risk in §8.3.

### 5.2 Input validation & fail-closed

- Every `cdc`/`add`/`remove` path read during a CDF replay is confined to the table root before open (openat/`O_NOFOLLOW`, #474); a path escaping the root fails closed.
- Corrupt/truncated `cdc` Parquet fails deterministically with zero partial rows (§3.2 CDF-EE-07).
- **`cdc` content/schema validation.** A well-formed, root-confined `cdc` file whose *embedded schema* mismatches the version's reconciled schema is rejected **before** any row is yielded (fail closed, §3.2 CDF-EE-08) — path confinement bounds only *where* a `cdc` file lives, not *what schema it declares*, so content is validated separately against the version schema.
- Protocol gate (§2.7): an unimplemented CDF write feature fails closed at commit; a CDF read of a disabled or out-of-window range fails closed.

### 5.3 Tenant isolation

A change feed is confined to one table path; no directory listing is used to discover change data (log-is-truth). No executor credential or cross-tenant path is introduced. Reuses the storage adapter's per-table backend scoping.

---

## 6 · Threat model

### 6.1 Trust boundaries

```mermaid
graph LR
  User["Query author / consumer"] -->|readChangeFeed opts| Exec["Executor scan-source"]
  Exec -->|version range| RS["DeltaReadSource (Storage)"]
  RS -->|confined paths| Store[("Object store / PVC")]
  Writer["Delta write door / DELETE"] -->|cdc + data files| Store
  RS -. reads .-> CDC[["_change_data/*.parquet"]]
  RS -. reads .-> Log[["_delta_log/*.json"]]
  classDef b fill:#eee,stroke:#333;
  class Store,CDC,Log b;
```

Trust boundary: everything under the table root on the (untrusted-content) object store is attacker-influenceable if an adversary can write to storage; DeltaSharp treats the **committed log as truth** and confines every path.

### 6.2 STRIDE

| Threat | Vector | Mitigation | Residual |
|---|---|---|---|
| **Tampering** (path) | Forged `cdc.path` pointing outside the table | Path confinement to table root (#474); log-is-truth | Low |
| **Tampering** (content) | Root-confined `cdc` file whose **embedded schema mismatches** the version — surfaces fabricated columns as authentic change data | Validate each `cdc` file's decoded schema against the version's reconciled schema before yielding; fail closed (§5.2, §3.2 CDF-EE-08) | Low |
| **Information disclosure** | Change files leak deleted-row PII beyond erasure SLA | Same-as-data classification; VACUUM protection + **bounded** erasure window (`max(CDF window, VACUUM retention)`) + targeted-purge open question (§5.1, §9 Q7, #489) | Medium → privacy handoff |
| **Denial of service** | Huge/adversarial `cdc` file inflating a CDF read | Reuse `MaxRowGroupDecodedBytes` decode ceiling (#473); bounded per-batch buffers | Low |
| **Repudiation** | Which version produced a change | `_commit_version` is **authoritative** (log ordering); `_commit_timestamp` is **best-effort** and attacker-influenceable on a hostile log (writer-stamped, not crypto-bound) — not for security/audit ordering | Low (version) / accepted (timestamp) |
| **Spoofing / EoP** | — | No new identity/credential surface; reuses storage backend auth | Low |

---

## 7 · Observability

### 7.1 Logging

Bounded, structured logs (own EventId band, e.g. `DeltaChangeFeedLog` 4200–4299) at the two seams: **generation** (version, affected files, `numChangeFilesAdded`, `numDeletedRows`) and **read** (resolved `[start,end]`, versions replayed, cdc-vs-implicit per version, rows produced). Log **counts and versions**, never row values (PII) — a table path/version is evidence; a change row is never a log field or a metric tag.

### 7.2 Metrics

`deltasharp.delta.cdf.*` family: `changefiles_written` (gen), `change_rows_read` (read), `versions_replayed`, `read_range_span`, plus reuse of the commit-latency family for the DELETE commit that now carries `cdc`. `operationMetrics` in `commitInfo` (`numDeletedRows`, `numChangeFilesAdded`) gives `DESCRIBE HISTORY` parity.

### 7.3 Tracing

The CDF read is a single logical operation spanning `LoadChangeFeedAsync` → per-version replay → batch production; propagate the read correlation id across the version-range loop so a slow version is attributable. Generation is a child span of the DELETE commit span.

### 7.4 Alerting

No new SLO alerts at storage tier (CDF is opt-in and read-driven); a spike in CDF-read fail-closed errors (disabled/out-of-range) is a **client-error** signal, not a page. A rise in corrupt-`cdc` decode errors reuses the existing storage-corruption alert.

---

## 8 · Rollout & risk

### 8.1 Rollout strategy

CDF is **opt-in** and protocol-gated: absent `delta.enableChangeDataFeed`, nothing changes — no `cdc` files, no reader impact (INV C1 guarantees byte-identical normal reads). Land in increments behind the writer-feature gate:

1. **Action + protocol** — `AddCdcFileAction`, serializer round-trip, `changeDataFeed` writer feature + enablement; snapshot reconstruction ignores `cdc` (INV C1). *No behavior change yet.*
2. **Generation** — `ChangeDataWriter` + DELETE hook + delete `operationMetrics`.
3. **Read** — `DeltaReadSource` change-feed mode, range validation, implicit + explicit paths, schema reconciliation.
4. **Hardening** — model-based oracle, cross-engine goldens, fuzz.

Each increment is independently reviewable and testable; (1) can merge without (2)/(3).

### 8.2 Rollback

Purely additive and opt-in; rollback = disable the writer feature / revert the increment. Tables written with `cdc` files remain **correct to normal readers** even on a build without CDF read support (INV C1), so a rollback never corrupts or hides table data — it only removes the *ability to read the change feed*.

### 8.3 Risk register

| Risk | Severity | Mitigation |
|---|---|---|
| DELETE change set computed wrong — miss on the **fully-deleted branch** in a mixed commit (double-count / drop) | High (silent wrong CDF) | INV C2 (both branches)/C3 model oracle + CDF-folds-to-snapshot (C6) + mixed-commit scenario CDF-HP-08 |
| Overwrite `delete` side unreadable after removed files VACUUMed | High (silent wrong/empty CDF for overwrite) | INV C7 + #489 removed-file retention protection (§2.5, §2.12); block generation GA (§9 Q5) |
| `_change_data` files vacuumed too early | High (data loss for consumers) | **Depends on #489**; block generation GA on VACUUM protection (§9 Q5) |
| Deleted-row + partition-value retention vs. GDPR erasure | Medium | Erasure latency == `max(CDF window, VACUUM retention)`; targeted-purge open (§9 Q7); privacy handoff (§5.1) |
| `cdc` content/schema-mismatch on a hostile log | Medium | Schema validation before yield (§5.2, §6.2, CDF-EE-08) |
| Batch-derivable-without-CDF gap vs. Spark | Low (stricter, not wrong) | Documented deviation (§2.7, §9 Q2) |
| Updates unsupported (no UPDATE op; AC1 partial) | Low (contract-only) | Tracked deferral #637 (§9 Q1) |

### 8.4 Launch checklist

Builds/tests/`dotnet format` pass; checklists **17** (Delta storage format), **19** (data-source connectors), **03a** (.NET standards), **04b** (integration) satisfied (story DoD); INV **C1–C9** green; #489 VACUUM protection (`cdc` files + derivation-referenced removed data files) landed or explicitly gated; `PublicAPI.Unshipped.txt` updated; follow-ups **#637**/**#638** filed; storage-delta-architecture.md §2.14 cross-reference kept consistent.

---

## 9 · Open questions & decisions

- **Q1 — UPDATE/MERGE change generation.** No UPDATE/MERGE op exists, so `update_preimage`/`update_postimage` cannot be produced yet. **Decision:** define the contract here; **defer** generation. *Status:* tracked deferral **[#637](https://github.com/khaines/deltasharp/issues/637)** (filed, open) — AC1's *update* change-type is only partially discharged until it lands (§3.4). *(Deferral with a filed blocking issue, not won't-fix.)*
- **Q2 — Batch-derivable CDF over disabled ranges.** DeltaSharp requires CDF enabled across the whole range (stricter than Delta). **Decision (proposed):** ship the strict rule in v1; relax later. Confirm with `delta-storage-format-engineer`.
- **Q3 — Predicated overwrite (`replaceWhere`).** If/when predicated overwrite lands, does it need `cdc` to avoid surfacing unchanged rows as delete+insert? Out of scope now; revisit with that feature.
- **Q4 — Schema reconciliation target.** Latest-in-range (proposed default, Spark parity) vs. a caller-supplied read schema. Confirm.
- **Q5 — VACUUM protection sequencing (#489).** CDF generation must not GA before **both** `_change_data/` files **and** derivation-referenced removed data files (the overwrite `delete` side, §2.5, INV C7) are VACUUM-protected. The DV `.bin` half already landed; confirm #489's remaining scope lands with or before increment (2).
- **Q6 — Public API shape.** *Decision (DX review):* keep `ReadChangeBatchesAsync` → `IAsyncEnumerable<ColumnBatch>` (streaming — a version range is unbounded) despite the snapshot door's materialized list; the asymmetry is deliberate and documented (§2.6/§2.9). `DeltaChangeFeedRange`/`DeltaChangeFeedInfo` are `readonly record struct`s; range bounds are **endpoint-scoped** version-xor-timestamp. Final member names settle at `PublicAPI.Unshipped.txt` time. `<!-- TBD: names -->`
- **Q7 — Targeted CDF erasure (privacy).** Time-based VACUUM cannot purge a specific subject's rows from `_change_data/` (or partition-value PII from the log) ahead of the retention interval. A targeted-purge affordance (rewrite/removal of specific change files for GDPR erasure) is out of scope here; confirm scope/priority with `privacy-compliance-grc-lead` and file if pursued.
- **Q8 — Constant/run-length `ColumnVector` (perf).** Metadata-column stamping is `O(rows)` bytes with today's columnar types (`O(1)` allocations/batch at steady state); a true constant/RLE vector would make it `O(1)` bytes too. Tracked as **[#638](https://github.com/khaines/deltasharp/issues/638)** (optional enhancement, not required for CDF correctness).

---

## 10 · References

- **Source issue:** [#193 — STORY-05.5.2: Change Data Feed generation and reads](https://github.com/khaines/deltasharp/issues/193). Parent feature [#50 (FEAT-05.5)](https://github.com/khaines/deltasharp/issues/50); dependency [#192 deletion vectors](https://github.com/khaines/deltasharp/issues/192).
- **Follow-ups filed by this design:** [#637 — CDF `update_preimage`/`update_postimage` generation when UPDATE/MERGE lands](https://github.com/khaines/deltasharp/issues/637); [#638 — constant/run-length `ColumnVector` for O(1)-byte metadata columns](https://github.com/khaines/deltasharp/issues/638).
- **Related issues:** [#489 VACUUM must protect `_change_data/` CDF + derivation-referenced removed files (DV `.bin` already protected)](https://github.com/khaines/deltasharp/issues/489), [#500 commitInfo.timestamp time-travel parity](https://github.com/khaines/deltasharp/issues/500), [#473 decode ceiling](https://github.com/khaines/deltasharp/issues/473), [#474 path confinement](https://github.com/khaines/deltasharp/issues/474), [#516 redact add.path](https://github.com/khaines/deltasharp/issues/516).
- **ADRs:** [ADR-0011 Delta protocol scope](../../adr/0011-delta-protocol-scope.md), [ADR-0002 columnar batch format](../../adr/0002-columnar-batch-format.md), [ADR-0014 target frameworks](../../adr/0014-target-framework-aot.md), [ADR-0006 scheduler/AQE/CBO](../../adr/0006-scheduler-aqe-cbo.md).
- **Design docs:** [storage-delta-architecture.md](storage-delta-architecture.md) (§2.10 log, §2.11 ACID, §2.12 time travel/schema/column-mapping, **§2.14 feature phasing + CDF gating**, §3.2 EE-10), [read-door.md](read-door.md), [write-door.md](write-door.md), [actions-and-row.md](actions-and-row.md), [observability-conventions.md](observability-conventions.md).
- **Code seams:** `src/DeltaSharp.Storage/Delta/DeltaActions.cs` (action model), `ProtocolSupport.cs` (writer-feature gate), `DeltaDelete.cs` (DV DELETE commit assembly), `DeltaCommitInfo.cs` (`Delete()`/`Optimize()` metrics), `DeltaCommitter.cs` (ACID commit + `TimeProvider`), `Writing/DeltaWriteTarget.cs` (write door), `Reading/DeltaReadSource.cs` (read door — CDF exclusion removed here).
- **Checklists:** 17 (Delta storage format), 19 (data-source connectors), 03a (.NET coding standards), 04b (integration testing), 04a (unit testing), 21 (distributed correctness), 22 (benchmark/regression gates), 05 (security), 07 (privacy), 14 (tenant isolation), 09a/09b/09c (logging/metrics/tracing).
- **Delta protocol:** Delta Lake protocol — "Add CDC File" (`cdc`) action, `delta.enableChangeDataFeed`, `changeDataFeed` writer feature, and the `_change_type`/`_commit_version`/`_commit_timestamp` change-feed schema.
