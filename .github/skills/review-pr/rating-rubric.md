# PR Review Rating Rubric

This document defines the scoring system, severity definitions, and consensus algorithm used by the `review-pr` skill to produce objective, repeatable PR ratings. AI review agents parse this rubric to assign findings, calculate scores, and format GitHub PR comments.

---

## Overall Rating Scale

| Rating | Label | Criteria | Action |
|--------|-------|----------|--------|
| ⭐⭐⭐⭐⭐ 5 | **Exceptional** | Zero Critical/High findings. Follows all applicable checklist items. Clean metadata. Well-structured, idiomatic .NET code or clear documentation. Adds value beyond the minimum (good tests, clear docs, thoughtful error handling, Spark/Delta semantics verified). | `APPROVE` |
| ⭐⭐⭐⭐ 4 | **Good** | Zero Critical findings. At most 1–2 High findings (minor). Follows most checklist items. Metadata is accurate. Solid code that meets quality standards. | `APPROVE` |
| ⭐⭐⭐ 3 | **Acceptable** | Zero Critical findings. 3–5 High findings. Some checklist gaps. Metadata may need minor updates. Code works but has room for improvement. | `COMMENT` |
| ⭐⭐ 2 | **Needs Work** | Zero Critical findings, but 5+ High findings OR multiple significant checklist violations. Metadata issues. Code has patterns that should be corrected before merge. | `REQUEST_CHANGES` |
| ⭐ 1 | **Significant Issues** | 1+ Critical findings: security vulnerability, data loss/corruption risk, broken Spark semantics, Delta ACID violation, tenant isolation breach, or unsafe operator/runtime behavior. PR should not merge in current state. | `REQUEST_CHANGES` |

> **`APPROVE` spans two ratings.** Ratings 4 and 5 share the `APPROVE` action, so a workflow that gates on a target rating must read the **rating**, never the action. Council consensus is a separate axis from rating.

---

## Finding Severity Levels

### 🔴 Critical — Must fix before merge. Blocks approval.

- Security vulnerabilities (injection, auth bypass, credential exposure, supply-chain risk).
- Data loss or corruption risk, especially Delta log, Parquet, catalog, checkpoint, or commit-protocol corruption.
- Tenant isolation breach or cross-tenant access through plans, storage credentials, executors, catalogs, or logs.
- Broken Spark API semantics or lazy/eager behavior that would surprise users or execute work during transformations.
- Unsound analyzer/optimizer/physical planner behavior that changes query meaning.
- Shuffle/stage/partitioning bugs that produce wrong results.
- Kubernetes Operator reconcile behavior that can delete live workloads or corrupt state.
- .NET runtime bug with high blast radius: deadlock, unbounded memory growth, disposed-resource use in critical paths.
- Breaking changes to public APIs without migration path.

> **Example:** "Optimizer rule pushes a predicate below an outer join and changes null-preserving semantics."
>
> **Example:** "Delta writer exposes object-store credentials to executor logs."

### 🟠 High — Should fix before merge. Strongly recommended.

- Logic errors that affect correctness but are not immediately catastrophic.
- Missing or inadequate test coverage for new functionality.
- Performance issues that would violate benchmark/regression gates or cause excessive allocation in hot paths.
- Missing error handling on important code paths.
- Missing cancellation, disposal, retry, or idempotency behavior in driver/executor/operator/storage code.
- Checklist violations for security, testing, storage correctness, distributed correctness, or observability.

> **Example:** "New Delta time-travel reader has no tests for timestamp-to-version resolution."
>
> **Example:** "Executor task cancellation token is ignored during object-store reads."

### 🟡 Medium — Should address. May be acceptable to defer with justification.

- Pattern inconsistencies with project conventions.
- Missing documentation for public APIs or complex logic.
- Non-critical checklist gaps.
- Suboptimal but correct design that should be improved before broader use.
- Incomplete observability for non-critical paths.

> **Example:** "Public DataFrame method lacks XML docs explaining Spark compatibility caveats."
>
> **Example:** "Metric names are internally consistent but not aligned with the documented prefix scheme."

### 🔵 Low — Nice to have. Improvement suggestions.

- Minor style preferences not covered by formatter/analyzers.
- Alternative approaches that may be slightly clearer.
- Documentation wording improvements.
- Test coverage for secondary edge cases that do not affect primary behavior.

> **Example:** "Consider renaming the local variable to make the plan rewrite easier to read."

### ℹ️ Info — Observations. No action required.

- Positive callouts.
- Context for reviewers explaining why something unusual is correct.
- FYI notes about related areas that may need attention in future PRs.

> **Example:** "Good separation between API plan construction and action-triggered execution."

---

## Finding Body Format Contract

When a council reviewer emits a finding into a PR comment or council report, the quoted-finding line MUST use the following parenthesised-prefix format, and EVERY finding MUST carry a `file:line` anchor and an `EVIDENCE:` clause:

```text
**Finding ({Severity}, {ReviewerSlot} {Round}):** <finding text>
- File: path:line
- Recommendation: <fix>
- EVIDENCE: "ran `<cmd>` → <output>" (required for C7-eligible claims) or "verified by reading <file:line>"
```

- `{Severity}` — one of `Critical | High | Medium | Low | Info`. **All voting seats AND the red-team use this same set** (the red-team does NOT use `blocking/major/minor`).
- `{ReviewerSlot}` — one of `Architect | Balanced | Quality | Security | Red-Team`, a scout-selected specialist's domain label, or another slot label this skill defines.
- `{Round}` — `R{N}` where `N` is the 1-based review-fix round index.

**Example:** `**Finding (High, Architect R1):** Physical planner drops the required shuffle before hash aggregation.`

This is a stable contract consumed by `review-fix-loop` validation. Any change to this format MUST be made in lockstep with that consumer. The drop-rule (see "Approve attestation") removes only findings that carry **neither** a `file:line` anchor **nor** any EVIDENCE clause — a well-formed finding is never dropped.

---

## DeltaSharp-Specific Checklist Emphasis

Reviewers must explicitly consider these correctness dimensions when applicable:

- **Spark API parity & semantics** — public API names, overloads, null behavior, expression semantics, SQL/DataFrame compatibility.
- **Lazy/eager correctness** — transformations build plans only; actions trigger execution.
- **Catalyst plan correctness** — unresolved logical plans, analyzer resolution, optimizer semantic preservation, physical strategy selection.
- **Delta ACID/commit-protocol correctness** — optimistic concurrency, transaction log actions, snapshots, checkpoints, idempotency, conflict detection.
- **Parquet/format correctness** — schema mapping, column stats, partition values, file metadata, compatibility.
- **Shuffle/partitioning correctness** — stage boundaries, repartition/coalesce, joins, aggregations, ordering assumptions.
- **Kubernetes Operator reconcile safety** — idempotent reconciles, finalizers, status updates, rollout/rollback, driver/executor lifecycle.
- **Tenant isolation** — plan analysis, file listing, executor credentials, catalogs, logs, metrics.
- **.NET memory/async correctness** — cancellation tokens, `IAsyncDisposable`, streams, channels, tasks, locks, allocation hot paths.
- **Benchmark/regression-gate adherence** — BenchmarkDotNet or equivalent evidence, throughput/latency/allocation budgets.

---

## Multi-Model Consensus Scoring

When the `review-pr` skill runs in **multi-model council mode**, each finding is tagged with a consensus label derived from how many of the **`N` voting seats whose scope covered the relevant file/domain** flagged the same issue. `N = 4 fixed lenses + each scout-selected specialist seat` (so `N` is 4–7). Consensus is computed **per finding** over the seats eligible to see it (a specialist scoped to a subset of files only counts toward findings in those files).

### Consensus Thresholds

Express agreement as a fraction `k/N` of eligible voting seats and map by ratio:

| Agreement `k/N` | Consensus Label | Confidence | Weight |
|-----------------|-----------------|------------|--------|
| = `N/N` | ✅ **Unanimous** | Very High | 1.0× severity |
| ≥ ~⅔ of `N` | ✅ **High consensus** | High | 1.0× severity |
| ~½ of `N` | ⚠️ **Split** | Moderate | 0.75× severity |
| single seat (`1/N`) | ⚠️ **Low consensus** | Low | 0.5× severity |

(For the canonical 4-lens council with no specialists, `N=4`, i.e. 4/4, 3/4, 2/4, 1/4. The `models_flagging` list in the `review-fix-loop` structured finding is variable-length, one entry per eligible seat that flagged the finding.)

### Consensus Rules

1. **Critical safety net** — A Critical finding from ANY single model is always included, regardless of consensus. Safety findings are never suppressed.
2. **Protected-domain safety net** — Findings in Spark semantics, Delta correctness, distributed correctness, security, tenant isolation, or .NET runtime safety are investigated even at low consensus.
3. **High finding threshold** — A High finding needs 2+ models to be reported at High severity unless it is Critical-adjacent or protected-domain; if only 1 model flags it, the finding may be downgraded to Medium after investigation.
4. **Medium/Low inclusion** — Medium and Low findings normally need 2+ models to be included in the report. Findings flagged by only 1 model are mentioned under Info unless protected-domain rules apply.
5. **Unanimous highlighting** — Unanimous findings are highlighted prominently in the report with a ✅ badge.
6. **Severity disagreement** — If models disagree on severity, use the **higher** severity but note the disagreement.

---

## Rating Calculation Algorithm

### Base Algorithm

```text
1. Start at rating 5
2. For each Critical finding:  rating = 1  (immediate — short-circuit)
3. For each High finding:      rating = min(rating, rating - 0.5)  [floor at 2]
4. For each Medium finding:    rating = min(rating, rating - 0.2)  [floor at 3]
5. Low and Info findings do not affect the rating
6. Round to nearest integer
7. Apply consensus weighting in multi-model mode
```

### Special Rules

| Condition | Effect |
|-----------|--------|
| Any Critical finding | Automatic rating **1** |
| Any Critical protected-domain finding | Automatic rating **1** and blocks merge until fixed |
| 5+ High findings | Rating capped at **2** |
| 3–5 High findings | Rating capped at **3** |
| 1–2 High findings | Rating capped at **4** |
| Missing tests for new behavior | Rating capped at **4**, or **3** if behavior is storage/engine/operator critical |
| Perfect score (5) | Requires: zero High+, all relevant checklists pass, metadata clean, validation evidence present |

### Consensus Weighting (Multi-Model Mode)

In multi-model mode, apply consensus weights **before** the base algorithm:

1. Multiply each finding's severity impact by its consensus weight.
2. A Critical finding at 0.5× weight still triggers rating = 1.
3. After weighting, fractional severity impacts below 0.1 are discarded unless protected-domain rules apply.

---

## GitHub PR Comment Templates

> **Note**: These templates are for the **review body** only. All findings with a file/line reference (Critical through Low) are posted as **inline review comments** on the specific files and lines in the PR. The review body contains the summary, rating, metadata assessment, info-level observations, and recommendation.

### APPROVE Template

Used for ratings 4–5. Action: `APPROVE`.

```markdown
## ✅ PR Review — Rating: {rating}/5 ⭐

{summary}

### Findings Summary
{count} findings posted as inline comments ({breakdown by severity}).

### Info
{info_observations — positive callouts and context notes}

### Checklists Passed
{checklist_summary}

**Recommendation**: Approved — {rationale}
```

### REQUEST_CHANGES Template

Used for ratings 1–2. Action: `REQUEST_CHANGES`.

```markdown
## 🔴 PR Review — Rating: {rating}/5

{summary}

### Findings Summary
{count} findings posted as inline comments ({breakdown by severity}).
See inline comments for details on each finding.

### Info
{info_observations}

### Checklists
{checklist_violations}

**Recommendation**: Changes requested — {rationale}
```

### COMMENT Template

Used for rating 3. Action: `COMMENT`.

```markdown
## 🟡 PR Review — Rating: {rating}/5

{summary}

### Findings Summary
{count} findings posted as inline comments ({breakdown by severity}).
See inline comments for details on each finding.

### Info
{info_observations}

### Checklists
{checklist_summary}

**Recommendation**: Looks good with suggested improvements — {rationale}
```

---

## Tie-Breaking Rules

When the calculated rating falls between two integers, apply these rules in priority order:

1. **Round DOWN** if any findings are in security, tenant isolation, data integrity, query correctness, Delta commit correctness, or distributed execution correctness domains.
2. **Round DOWN** if validation evidence is missing for restore/build/format/test on code changes.
3. **Round UP** if all Critical/High findings have mitigations already in progress and none are protected-domain findings.
4. **Round to nearest** otherwise.

---

## Rigor battery & the PASS gate

A rating answers "how good is this PR"; the **gate** answers "may the loop terminate / may this
merge." They are separate. The gate consumes the [`rigor-battery.md`](rigor-battery.md) (C1–C7)
and the red-team verdict.

**PASS** requires ALL of:

- every voting seat (the 4 lenses + each scout-selected specialist) at **5/5**, with a complete
  Approve attestation (below). **5/5 from every seat is mandatory — there is NO exception, allowance,
  tolerance, or human waiver for any seat below 5/5.** `target_rating` does not relax it; it only
  governs the separate "below-target" *termination* path in `review-fix-loop` (which may STOP the loop
  but is **never** a PASS and **never** merge-ready). A sub-5/5 seat implies ≥1 actionable item: fix it
  and re-score — do not present, label, or merge a sub-5/5 PR as PASS;
- **zero actionable** (Critical/High) findings open;
- **zero open C1 / C2 / C4 / C5 / C6 / C7 items** — no proven-vacuous test, no dead/un-wired
  control or validation↔enforcement gap, no unbacked PR/spec claim, no unmigrated compat break, no
  committed scratch/cruft, and no execution-eligible claim lacking executed evidence. (C3
  observability is enforced via CI + the red-team rather than the seat-gate.)
- red-team **`NO-MISS-CERTIFIED`** — **and the orchestrator has independently re-run at least one
  sampled C7 repro from the red-team's evidence block and confirmed the quoted output**, because the
  council's own `NO-MISS-CERTIFIED` / attestations / quoted C7 output are **self-asserted signals**
  and therefore subject to the same C2 anti-forgeability rule the council applies to reviewed code
  (never trust a self-settable signal — the #323 class, applied reflexively);
- the certifying red-team ran on a **decorrelated** frontier family. A **same-family (provisional)
  certification does NOT satisfy PASS for protected-domain changes** — it blocks termination pending
  a decorrelated re-run or a documented human waiver recorded in the composition audit.

**Green CI is necessary but not sufficient** — C1/C2/C7 routinely catch defects that pass green
CI (vacuous tests, validator↔consumer mismatches, migration notes the code contradicts).

## Decorrelated red-team gate

- The red-team runs **last** on a frontier family **distinct from the majority voting spine**, and
  **ideally a family used by no voting seat** (true decorrelation). If the spine is Opus + GPT, use
  Gemini — do **not** dispatch the red-team on a family a voting seat already uses (e.g. don't use
  `gpt-5.5` while the Quality lens is `gpt-5.5`). A red-team `MISS-FOUND` **always blocks** — its
  findings are actionable, in the canonical `Critical|High|Medium|Low|Info` set (the red-team does
  not emit `blocking/major/minor`).
- `NO-MISS-CERTIFIED` is valid only with a fully-populated Falsification-Attempts block and a C7
  line quoting real commands + output for every execution-eligible claim, AND the orchestrator's
  independent re-run of a sampled C7 repro (above) must match. A bare "no issues", or a "verified
  by reading" on a C7-eligible claim, is rejected and **re-prompted once**; if the second attempt is
  still non-conformant, certification is **denied** and the gate cannot PASS (treat as `MISS-FOUND`
  / escalate to a human).
- Protected domains (security / tenant isolation / auth / data integrity / Delta commit / query
  correctness / privacy) may not be dismissed without an explicit `protected-domain assessment:
  none`.

## Approve attestation & the anti-impasse rule

These exist to stop two specific failure modes the council has hit:

- **Approve attestation.** Every seat that emits `APPROVE` / 5/5 MUST carry a completed
  **Mandatory Checks** attestation — each applicable battery line is a real result or an explicit
  `n/a — <reason>`. A finding is dropped before scoring **only if it carries neither a `file:line`
  anchor nor any EVIDENCE clause**; a finding missing just one is re-prompted/normalized, not dropped
  (matching the Finding Body Format Contract).
  If an APPROVE lacks the attestation, **re-prompt that one seat** for it before counting the vote.
- **Anti-impasse (sub-5/5 with no actionable finding).** A rating below 5/5 MUST be justified by
  at least one concrete `file:line` finding (per the scale, 4/5 means 1–2 High findings). A seat
  that returns **sub-5/5 but lists no actionable finding is incoherent** — re-prompt it **once**
  for the specific `file:line` change that would make it 5/5. If it still names none and won't
  revise to 5/5 → **impasse** (blocks PASS; escalate to a human). Never record a sub-5/5 "that
  found nothing" as a substantive result, and never paper over it by reinterpreting the number.
  A reviewer that down-rates only because it could not *execute* a check is a **dispatch error**
  (re-dispatch it shell-capable), not a finding.
