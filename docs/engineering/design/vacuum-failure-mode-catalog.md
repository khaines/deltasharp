# VACUUM failure-mode catalog

> **Status:** Draft
> **Issue:** [#641](https://github.com/khaines/deltasharp/issues/641) item 5 — record the accepted
> compound-correlated-double-tear residual (and VACUUM's other terminal/decision/abort modes) in a
> failure-mode catalog.
> **Author:** design-doc skill (orchestrated)
> **Reviewers:** cloud-native-site-reliability-engineer, cloud-native-security-sme,
> cloud-native-distributed-systems-architect, delta-storage-format-engineer, reliability-test-chaos-engineer
> **Last Updated:** 2026-08-17
> **Related:** [#489](https://github.com/khaines/deltasharp/issues/489) (single-listing cdc protection),
> [#640](https://github.com/khaines/deltasharp/issues/640) (tail-listing guard),
> [#641](https://github.com/khaines/deltasharp/issues/641) (efficiency/observability follow-ups),
> [#809](https://github.com/khaines/deltasharp/issues/809) (log-derived cdc-scan skip),
> [#712](https://github.com/khaines/deltasharp/issues/712) (`ReplayedMetadataLog` bounded pre-range gate).
> Source of truth: `src/DeltaSharp.Storage/Delta/DeltaVacuum.cs` and
> `src/DeltaSharp.Storage/Diagnostics/DeltaStorageTelemetry.cs`.

---

## 1 · Purpose

This catalog is the operator-facing reference for **how DeltaSharp VACUUM can fail, abort, protect, or leave a
bounded residual**, so an on-call engineer can (a) tell a *retryable* abort from a real fault on metrics/logs
alone, (b) understand why a file was *not* reclaimed, and (c) know the one **accepted, inherent residual**
([#641](https://github.com/khaines/deltasharp/issues/641) item 5) and its exact preconditions and bound.

VACUUM's cdc protection is **fail-closed, window-bounded, single-listing, and tail-guarded** (verified across
PR #640's council rounds + red-team Criticals). Every mode below is either a *safe refusal to delete* or an
*inherent, recency-window-bounded residual of a catastrophic external store fault* — never a routine data-loss
path introduced by DeltaSharp.

The catalog does **not** change behavior; it documents what the code already implements and comments in-place.

---

## 2 · Terminal outcomes (`VacuumOutcome`)

The bounded terminal a VACUUM run reports (behind the shared outcome label — a closed, low-cardinality set safe
as a metric dimension). Source: `DeltaStorageTelemetry.VacuumOutcome`.

| Outcome | Meaning | Operator action | Retryable? |
|---|---|---|---|
| `DryRun` | Listed the deletion-eligible paths without deleting anything. | Informational; review the plan. | n/a |
| `Completed` | Reclaimed the deletion-eligible files idempotently. | None. | n/a |
| `RejectedUnsafeRetention` | Requested retention was below the safety threshold and the unsafe override was not enabled → rejected **before any selection**. | Raise retention, or explicitly opt into the unsafe override *only* if you understand the stale-reader risk. | Yes, after fixing config. |
| `Cancelled` | Cancelled via `CancellationToken` before a terminal outcome. **Not a failure.** | None; re-run when ready. | Yes. |
| `AbortedStaleListing` | Aborted fail-closed because the `_delta_log` listing was **tail-truncated** (the table root listed a version-bearing log artifact *beyond* the version the snapshot resolved to). See §4. | Retry **if the listing is merely stale**; if the guard **recurs on every run**, escalate — see §4.1. | **Conditional** — stale-listing lag is transient; **persistent recurrence is not** (a durably-orphaned / forged log). |
| `Failure` | An unexpected/unclassified failure. **Fail-closed: nothing protected is deleted.** | Inspect logs; treat as a real fault, not routine. | Depends on cause. |

> **Why `AbortedStaleListing` is a distinct terminal (not `Failure`):** from metrics/logs alone an operator must
> be able to tell a *transient, retryable* stale-listing abort from a genuine protocol failure
> ([#641](https://github.com/khaines/deltasharp/issues/641) item 4). Reclaiming under a stale listing could
> delete files referenced by the missing commit(s), so the run aborts and is retryable once the listing
> propagates.

---

## 3 · Per-file protection decisions (`VacuumDecision`)

Why a candidate file was **not** deleted. Source: `DeltaStorageTelemetry.VacuumDecision`.

| Decision | Why the file is protected |
|---|---|
| `Deletable` | Retention-expired **and** unreferenced — the only class actually reclaimed. |
| `Active` | An active file in the current snapshot — never an orphan. |
| `RetentionProtectedTombstone` | A tombstone removed within the retention window (or unknown deletion time, treated as `+∞`) — a stale reader pinned to an older snapshot may still read it. |
| `RecentlyStaged` | Modified within the retention window (`mtime >= cutoff`, inclusive) — it may belong to an in-flight commit, so it is protected against listing lag / a torn view. **This is the recency window that bounds the §5 residual.** |
| `ReferencedChangeData` | Referenced by a `cdc` action in a retained, in-window commit JSON — a Change-Data-Feed `_change_data/` file that is not an active file (INV C1) but is protected while a commit within `delta.logRetentionDuration` still references it ([#489](https://github.com/khaines/deltasharp/issues/489)). |

The `ReferencedChangeData` set comes from the **in-window cdc scan** — VACUUM reads every in-window commit JSON
(bounded by `delta.logRetentionDuration`) to collect `AddCdcFileAction` paths, threaded from the **single**
`_delta_log` listing the snapshot was reconstructed from (never a second, possibly-divergent listing —
[#489](https://github.com/khaines/deltasharp/issues/489)/#640, elevated from perf to correctness). The scan can
be **safely skipped** only when the retained protocol history over the full in-window range never declared
`changeDataFeed` — a **log-derived** predicate, never a candidate-listing or current-enablement one
([#809](https://github.com/khaines/deltasharp/issues/809); the unsafe predicates are catalogued in §6).

---

## 4 · Fail-closed aborts

### 4.1 Stale / tail-truncated log listing → `AbortedStaleListing`

- **Trigger:** the table root lists a version-bearing `_delta_log` artifact **beyond** the version the snapshot
  resolved to (`maxListedLogVersion > snapshot.Version`) — a tail-truncated or stale/partial listing (#640
  red-team).
- **Behaviour:** abort **before** any deletion; emit the structured, sanitized
  `VacuumAbortedStaleListing(maxListedLogVersion, snapshot.Version)` line (both bounded version numbers as
  fields, not an opaque string) and throw `DeltaProtocolException.StaleLogListing`.
- **Why:** reclaiming under a stale listing could delete files referenced by the not-yet-visible commit(s).
- **Operator action:** retryable **only if the listing is merely stale** (object-store list-after-write lag —
  it self-heals and the run succeeds on retry). **Persistent recurrence is NOT transient:** a durably-orphaned
  version-bearing artifact — a forged log, or a foreign writer's stray checkpoint left above the resolvable
  version — trips this guard on **every** run and never resolves. That is an inconsistent/forged-log integrity
  condition (and an unbounded storage-cost condition, since reclamation is blocked indefinitely) and warrants
  operator **escalation, not blind retry**. (Attempt-bounding / auto-escalation is a deliberately un-implemented
  follow-up; the contract today is the distinct terminal + the structured log line so an operator can see the
  recurrence and decide.)
- **Source:** `DeltaVacuum.cs` (`maxListedLogVersion > snapshot.Version` guard); `DeltaVacuumLog.cs`
  (`VacuumAbortedStaleListing`, EventId 4108 — the retry-vs-escalate wording is verbatim there).

### 4.2 Unsafe retention below threshold → `RejectedUnsafeRetention`

- **Trigger:** requested retention below the safety threshold without the explicit unsafe override.
- **Behaviour:** reject fail-closed **before any file selection** (AC2).
- **Operator action:** raise retention; only override if the stale-reader risk is understood.

### 4.3 Unexpected fault → `Failure`

- Any unclassified exception fails closed: **nothing protected is deleted**. Treat as a real fault.

---

## 5 · Accepted residual — compound correlated double-tear (#641 item 5)

This is the **one inherent residual** and the reason this catalog exists. It is an `ACCEPTED RESIDUAL`
documented in-code at `src/DeltaSharp.Storage/Delta/DeltaVacuum.cs`.

### 5.1 What it is

A **compound** double-tear: the **same** commit JSON is invisible to **both** the candidate listing **and** the
`_delta_log` listing, **while** that commit's data file(s) stay listed **and** are already aged past
`delta.deletedFileRetentionDuration`. VACUUM would then delete the still-live file(s), because the commit that
references them is absent from the single listing it consults.

### 5.2 Why it is inherent (not a fixable bug)

- Fully closing it requires a **second, independent full log read** — the very divergence the #489
  **single-listing invariant forbids** (two independent listings can disagree; the staler one drops an in-window
  commit's referenced path → data loss). #640's tail guard (§4.1) closes the case where the *log* listing is
  staler than the *candidate* listing; it **cannot** close the case where the *same* artifact is missing from
  **both**.
- It is **pre-existing and not introduced by #640** — the identical loss exists on the parent commit; #640
  strictly **narrows** the surface.

### 5.3 Why it is bounded (reachable only under a catastrophic list-inconsistent store fault)

- **Recency-window bound:** a fresh, unpropagated commit's files are `RecentlyStaged` (§3) and **never deleted**.
  The residual can only bite an **old, long-propagated** commit.
- **Precondition = a catastrophic store fault:** it requires a store that *consistently* drops the **same old**
  commit JSON from **two independent LISTs** while **retaining its data file**. A store that does this corrupts
  **non-CDF tables too** — it is the same class of catastrophic listing fault that violates read-after-write /
  list consistency guarantees the whole Delta protocol assumes.

### 5.4 Detection / mitigation posture

- **Not separately alertable** by design (it is indistinguishable from a healthy delete at the point of action —
  the referencing commit is simply absent). The defence is upstream: use a **list-consistent** object store, and
  keep `delta.deletedFileRetentionDuration` generous enough that the recency window covers realistic listing lag.
- If store-level list inconsistency is suspected, the tail-listing guard (§4.1) catches the *asymmetric* tear and
  aborts retryably; the *symmetric* compound tear is the residual that remains.

---

## 6 · Rejected (unsafe) skip predicates — do not reintroduce

Recorded so a future "optimize the cdc scan" change never reintroduces a known data-loss predicate
([#641](https://github.com/khaines/deltasharp/issues/641) item 3 / #640 R3 red-team,
[#809](https://github.com/khaines/deltasharp/issues/809)):

| Rejected predicate | Why it is UNSAFE |
|---|---|
| Skip the cdc scan based on the **candidate listing** | `OrphanCleanup` protects a candidate matching **any** referenced `cdc` path regardless of prefix/encoding, and `ParseCdc` does not constrain a cdc path to `_change_data/`; a double-encoded / non-canonical candidate would be *protected-if-scanned* yet *skipped* → data loss. |
| Skip based on the **current snapshot's** `changeDataFeed` enablement | CDF disabled **after** it was enabled still has in-window cdc files; the naive current-enablement gate would skip them → data loss. |
| **Safe** predicate ([#809](https://github.com/khaines/deltasharp/issues/809)) | Skip **only** when the retained **protocol history over the full in-window commit range** never declared `changeDataFeed` — **log-derived**, never candidate/snapshot-derived. |

---

## 7 · Related read-path residual (not a VACUUM mode) — #712

For completeness (cross-referenced from the epic's residual list; **no action owed**): the CDF pre-range
column-mapping identity gate's observer (`ReplayedMetadataLog`) caps its retained observations by **entry count**
(`MaxRetainedObservations`, [#712](https://github.com/khaines/deltasharp/issues/712)) — a *count*, not a *byte*,
bound. Past the cap the observer goes **inert** and **fails closed** (reports every version as unproven → the
pre-range gate degrades to a full disk scan; correctness is preserved, only cost changes). This is a bounded,
already-in-code-documented design choice on the **read** path, not a VACUUM failure mode; it is listed here only
so the epic's two inherent residuals are discoverable from one place.

---

## 8 · Observability quick-reference

> **Two surfaces, don't confuse them.** The **metric instruments** (on the `DeltaSharp.Delta` meter — what you
> query/alert on) are named `deltasharp.delta.vacuum.cdc_scan.*`; the **activity/span tags** (on the vacuum
> span) are named `deltasharp.vacuum.cdc_scan.*`. They overlap in stem but differ in prefix **and units**.

| Signal | Surface | Meaning |
|---|---|---|
| `VacuumOutcome` label (§2) | metric label | terminal outcome — watch `AbortedStaleListing` (retry only if stale; escalate on persistent recurrence, §4.1) vs `Failure` (real fault). |
| `deltasharp.delta.vacuum.cdc_scan.commits` | Histogram `{commit}` | commits read by the in-window cdc scan (cost grows with `delta.logRetentionDuration` depth) — [#641](https://github.com/khaines/deltasharp/issues/641) item 2. |
| `deltasharp.delta.vacuum.cdc_scan.duration` | Histogram **seconds** (OTel base unit) | cdc-scan wall-clock. **Alert on this**, not the span tag. |
| `deltasharp.delta.vacuum.cdc_scan.skipped` | Counter `{scan}` | the scan was **elided** because the log proved CDF inactive across the full in-window range ([#809](https://github.com/khaines/deltasharp/issues/809)); the commits/duration histograms are **not** recorded on a skip, so this aggregate is exactly the skip count. |
| `deltasharp.delta.vacuum.cdc_scan.scanned` | Counter `{scan}` (+ `reason` label) | the scan **ran** (the skip did not fire); the label carries why. |
| `deltasharp.vacuum.cdc_scan.commits` / `.duration_ms` | span tags | per-run cdc-scan cost on the vacuum span. **Note: `.duration_ms` is milliseconds**, unlike the seconds metric above. |
| `deltasharp.vacuum.cdc_scan.completed` | span tag | whether the scan **ran to completion** vs was aborted/threw mid-read (this is *not* the skip signal — that is the `.skipped` counter). |
| `deltasharp.vacuum.decision` — `referenced_change_data` label | metric label value | how many `_change_data/` files were protected by the in-window scan (a **decision-label value**, not a standalone counter). |
| `VacuumAbortedStaleListing` log line (EventId 4108) | log | structured `ListedVersion` + `ResolvedVersion` fields for §4.1 self-diagnosis (and the verbatim retry-vs-escalate guidance). |

---

## 9 · References

- `src/DeltaSharp.Storage/Delta/DeltaVacuum.cs` — the `ACCEPTED RESIDUAL (#641)` comment (§5), the stale-listing
  guard (§4.1), and the cdc-scan / decision logic.
- `src/DeltaSharp.Storage/Diagnostics/DeltaStorageTelemetry.cs` — `VacuumOutcome`, `VacuumDecision`,
  `VacuumDecision`/`VacuumOutcome` labels, and the cdc-scan telemetry keys.
- `docs/engineering/design/vacuum-cdc-scan-skip.md` — the log-derived cdc-scan skip design ([#809](https://github.com/khaines/deltasharp/issues/809)).
- Issues: [#489](https://github.com/khaines/deltasharp/issues/489), [#640](https://github.com/khaines/deltasharp/issues/640), [#641](https://github.com/khaines/deltasharp/issues/641), [#712](https://github.com/khaines/deltasharp/issues/712), [#809](https://github.com/khaines/deltasharp/issues/809).
