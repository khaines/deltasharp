# Dismissal Rules — Review-Fix Loop

This document defines when a finding from the `review-pr` skill should be **dismissed** rather than fixed during the review-fix loop. Dismissal is not ignoring a problem — it is a structured decision that a finding does not warrant a code change in this PR. Every dismissal must have a clear reason.

---

## Dismissal Categories

### 1. Out of PR Diff

**Rule**: Dismiss any finding that references code NOT changed in this PR.

**Rationale**: The review-fix loop addresses issues introduced or exposed by the current PR. Pre-existing problems in untouched files are out of scope, regardless of severity. They should be tracked as follow-up items, not fixed in unrelated PRs.

**How to detect**:

- Compare the finding's `file:line` against the PR diff (`git diff main...HEAD`).
- If the finding points to a line that was not added or modified in the diff, it is out of scope.
- **Exception**: If the PR deletes code that was the only guard against a problem, the finding IS in scope even though the problematic line is in untouched code.

**Action**: Categorize as "Dismissed — Out of scope" and add to the deferred list for follow-up.

---

### 2. Low Consensus (Single Model)

**Rule**: Dismiss non-Critical findings flagged by only **1 of the `N` eligible voting seats** (`N` = 4 fixed lenses + scout-selected specialists; see `review-pr/rating-rubric.md` → Consensus Scoring) when the full council participated, **unless the finding touches a protected domain** (see §2.1 below). The thresholds below are written for the canonical `N=4` lens-only council (`1/4`, `3/4`, …); for a larger `N`, read `1/4` as "single seat (`1/N`)" and scale the fractions proportionally. Apply stricter thresholds when the council is degraded.

**Rationale**: A 1/4 consensus finding has a high probability of being a false positive, a subjective style preference, or a pattern disagreement rather than a genuine issue. The multi-model council's power comes from convergence — single-model findings lack that validation. However, when fewer models participate, the consensus denominator shrinks and the signal-to-noise ratio of single-model findings increases.

**How to apply**:

#### Full council (4/4 models responded)
- If `consensus = "1/4"` AND `severity` is Low or Info → dismiss.
- If `consensus = "1/4"` AND `severity` is Medium → dismiss, **unless** the finding touches a protected domain (§2.1).
- If `consensus = "1/4"` AND `severity` is High → **DO NOT auto-dismiss**. Investigate the finding. If confirmed as false positive or design choice, dismiss under §3 or §4 with documented rationale. Otherwise, treat as actionable.
- If `consensus = "1/4"` AND `severity` is Critical → **NEVER dismiss** — investigate manually.

#### Degraded council (3/4 models responded)
- If `consensus = "1/3"` AND `severity` is Low or Info → dismiss.
- If `consensus = "1/3"` AND `severity` is Medium → dismiss with **explicit rationale required** after brief investigation.
- If `consensus = "1/3"` AND `severity` is High → **DO NOT auto-dismiss**. Investigate the finding.
- If `consensus = "1/3"` AND `severity` is Critical → **NEVER dismiss** — investigate manually.

#### Severely degraded council (2/4 or fewer models responded)
- **Do not apply low-consensus dismissal at all.** With only 1-2 models, there is insufficient signal to determine consensus. All findings are evaluated on merit regardless of consensus count.
- Consider re-running the review round to achieve full council coverage before proceeding.

**Action**: Categorize as "Dismissed — Low consensus" with a note about which model flagged it and the council coverage.

#### 2.1 Protected Domains — No Auto-Dismiss

Findings in the following domains are **never auto-dismissed** based on low consensus alone, regardless of severity. They must be investigated and dismissed only under §3 (pre-existing), §4 (design choice), or §5 (style) with explicit documented rationale:

- **Security** — authentication, authorization, credential handling, injection risks, supply-chain integrity.
- **Tenant isolation** — tenant ID propagation, object-store/PVC credential scoping, cross-tenant data access, catalog isolation, driver/executor task assignment.
- **Data integrity** — Delta ACID commit protocol, `_delta_log` correctness, Parquet write/read correctness, schema evolution, time travel, catalog consistency.
- **Query correctness** — Spark API semantics, lazy/eager boundaries, logical/analyzed/optimized/physical plan equivalence, shuffle/partitioning correctness.
- **Cryptography and secrets** — key management, token validation, signature verification, secret mounting and rotation.
- **Runtime safety** — async deadlocks, cancellation misuse, resource leaks, memory blowups in driver/executor hot paths.

**Rationale**: These domains have outsized blast radius. A single missed tenant-isolation gap, broken Delta commit invariant, incorrect optimizer rule, or async resource leak can corrupt data, leak data, or destabilize distributed execution.

---

### 3. Pre-existing in Base Branch

**Rule**: Dismiss findings that describe issues already present in the base branch (`main`) and not worsened by this PR.

**Rationale**: The PR did not introduce the problem. Fixing pre-existing issues in an unrelated PR creates scope creep, makes the PR harder to review, and muddies the commit history.

**How to detect**:

- Check out or compare against the base branch and verify whether the same issue exists there.
- If the finding describes a pattern that exists identically in the base branch and the PR did not modify it, it is pre-existing.
- If the PR copied a problematic pattern to a new location, the NEW instance IS in scope.

**Action**: Categorize as "Dismissed — Pre-existing" and add to the deferred list for follow-up.

---

### 4. Intentional Design Choice

**Rule**: Dismiss findings that challenge a documented design decision.

**Rationale**: Some findings are technically valid observations but conflict with intentional architectural or design choices. If the choice is documented in an ADR, design doc, PR description, or code comment, the finding is not actionable in this context.

**How to detect**:

- Check whether the finding conflicts with an ADR in `docs/engineering/adr/`.
- Check whether the governing design doc explicitly addresses the pattern.
- Check whether the PR description explicitly addresses the pattern the finding questions.
- Check for code comments explaining the design choice near the flagged code.

**How to apply**:

- If documentation exists justifying the choice, dismiss the finding.
- If no documentation exists but the pattern appears intentional, ask before dismissing. Ambiguous cases should be flagged for the PR author rather than unilaterally dismissed.

**Action**: Categorize as "Dismissed — Design choice" with a reference to the supporting documentation.

---

### 5. Style or Formatting Only

**Rule**: Dismiss findings that are purely about code style, formatting, or naming conventions when the code follows the project's established patterns.

**Rationale**: Style preferences vary between models. If the code follows the project's coding conventions and `dotnet format --verify-no-changes`, a model's alternative style suggestion is not actionable.

**How to detect**:

- The finding's recommendation suggests an alternative style rather than a correctness fix.
- The existing code matches patterns found elsewhere in the codebase.
- The relevant coding conventions checklist does not flag the pattern as a violation.
- The formatter/analyzer gate passes.

**Action**: Categorize as "Dismissed — Style preference" with a brief note.

---

## Dismissal Priority Order

**A red-team `MISS-FOUND` finding is never dismissed** — it is actionable and blocking by
construction, closed only by a fix or by the red-team re-certifying `NO-MISS-CERTIFIED`. For all
other findings, apply dismissal rules in this order. Stop at the first match:

1. **Critical safety net** — If severity is Critical, skip ALL dismissal rules and investigate manually. Critical findings are never auto-dismissed.
2. **Protected domain safety net** — If the finding touches a protected domain (§2.1), skip low-consensus auto-dismissal.
3. **High severity safety net** — If severity is High, skip low-consensus auto-dismissal. The finding must be investigated regardless of consensus count.
4. **Out of PR diff** — Is the finding outside the changed code?
5. **Pre-existing in base** — Does the issue exist identically in `main`?
6. **Low consensus** — Does it meet the consensus threshold for the current council coverage?
7. **Intentional design** — Is it documented as a design choice?
8. **Style only** — Is it a pure style preference?
9. **Deferral (last resort)** — Is fixing genuinely impossible in this PR? See Deferral Policy below. If deferring, create a tracking issue.

If no dismissal rule matches, the finding is **actionable** and should be fixed.

---

## Deferral Policy — High Bar

Deferral is the last resort for findings that are genuinely valid but cannot be fixed within the current PR. **The bar for deferral is very high** — the goal is 5/5 ⭐ on every PR, and deferred findings that affect the rating prevent that.

### When deferral is acceptable

A finding may ONLY be deferred if ALL of these conditions are true:

1. **The finding is confirmed valid** — it is not a false positive, style preference, or design disagreement.
2. **Fixing it is impossible in this PR** — not merely difficult or time-consuming, but genuinely requires:
   - Changes to a different repository or independent subsystem.
   - An upstream dependency change or version bump.
   - An ADR decision that has not been made yet.
   - Infrastructure that does not exist yet.
3. **The finding does not affect security, tenant isolation, data integrity, query correctness, or Delta commit correctness** — Critical findings in these domains are NEVER deferred. They block the PR.

### When deferral is NOT acceptable

- "It would take too long" — fix it or increase max rounds.
- "It's an edge case" — edge cases cause production incidents and data bugs. Fix it.
- "We'll get to it later" — create a durable issue only if the deferral policy is met.
- "It's only Medium severity" — Medium findings that are in-diff and fixable must be fixed.
- "The code works without it" — working code is not the bar. Correct, maintainable, safe code is the bar.

### Deferral reporting

Every deferred finding MUST include:

- A specific explanation of WHY it cannot be fixed in this PR.
- A **GitHub issue number** tracking the follow-up — mandatory, not optional, and **filed before the
  gate can PASS** (not "to be filed later").
- **Orchestrator verification** that the issue actually exists and is open, whose scope matches the
  finding — confirmed with `gh issue view <n>` and its number recorded in the progression report. An
  un-filed, closed, or unfindable tracking issue makes the deferral **un-tracked**, which **blocks
  PASS** (reclassify as actionable and fix, or file the issue).
- An assessment of risk: what happens if this is never fixed?

A finding that is genuinely **inherent to the approach** (cannot be fixed at all, e.g. a limitation
of static analysis covered by a stronger primary guarantee) is not a deferral: disposition it as
**inherent/won't-fix** with a durable in-code or in-PR rationale instead of a tracking issue. It must
still appear in the report so the decision is auditable.

---

## Dismissal Reporting

Every dismissed finding MUST appear in the final progression report with:

- The finding ID, severity, and description.
- The dismissal category.
- A one-sentence rationale explaining why it was dismissed.
- Whether it was added to the deferred/follow-up list.

This ensures transparency — the PR author and future reviewers can see what was dismissed and why, and can challenge any dismissal they disagree with.

---

## Re-evaluation Between Rounds

Findings dismissed in Round N are NOT automatically dismissed in Round N+1. Each round's findings are evaluated independently because:

- A fix in Round N might have changed the context that made a finding dismissible.
- A previously out-of-diff finding might become in-scope if Round N's fixes touched that area.
- Consensus can shift if the code changes alter what models flag.

However, if a finding was dismissed for "intentional design choice" or "style preference" and the same finding recurs with the same rationale in the next round, it can be dismissed immediately without re-investigation.

---

## Triage Verification Round

After all dismissals are finalized in a round, run a **triage verification** on dismissed findings to catch false dismissals. This step is mandatory when **5 or more findings** are dismissed in a single round, and mandatory for any dismissed finding in protected domains (§2.1).

### How it works

1. **Collect** all dismissed findings from the round into a single list.
2. **Present** each dismissed finding to 2-3 models different from the one that originally flagged it with a focused prompt: "Read the actual code and determine — is this a real issue that should be fixed, or was the dismissal correct?"
3. **Compile** a triage consensus:
   - If **2/3+ triage models** say "REAL ISSUE" → **promote** the finding back to actionable and fix it.
   - If **2/3+ triage models** say "CORRECTLY DISMISSED" → the dismissal stands.
   - Include the triage verdict in the progression report for transparency.

### When to run triage

| Condition | Triage Required? |
|-----------|-----------------|
| 5+ findings dismissed in one round | **Mandatory** |
| Any dismissed finding touches a protected domain (§2.1) | **Mandatory** |
| 3-4 findings dismissed, none in protected domains | Recommended |
| 1-2 findings dismissed, none in protected domains | Optional |

### Triage prompt template

```text
You are triaging a dismissed finding from a PR review. The finding was
dismissed as "low consensus (1/4)" but we need to verify it is not a
real issue that only one model caught.

Finding: {description}
File: {file}:{line}
Original severity: {severity}
Dismissal reason: {reason}

Read the actual code at {file_path} and determine:
- REAL ISSUE (should fix) — explain why in 1-2 sentences
- CORRECTLY DISMISSED — explain why in 1-2 sentences
```

### Rationale

Protected domains in DeltaSharp include data correctness and distributed execution semantics, not just application security. A false dismissal can corrupt Delta logs, violate Spark parity, or make optimizer rules unsound, so triage is mandatory when dismissals touch those areas.
