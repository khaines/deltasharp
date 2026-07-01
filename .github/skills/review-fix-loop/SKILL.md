---
name: review-fix-loop
description: >-
  Orchestrates an iterative review-fix cycle on a pull request. Invokes the review-pr skill,
  evaluates findings, dispatches appropriate agent personas to fix actionable items, commits
  and pushes fixes, then re-reviews. Repeats until no new actionable findings are discovered
  or the safety-valve maximum round limit is reached. Produces a progression report showing
  round-by-round improvement.
---

# Review-Fix Loop Skill — Orchestration Instructions

This skill automates the iterative cycle of reviewing a pull request, fixing discovered issues, and re-reviewing until the PR reaches a clean state. It wraps the existing `review-pr` skill as its evaluation engine and dispatches specialist agent personas to perform fixes.

Read all supporting files before beginning:

- `.github/skills/review-pr/SKILL.md` — the review engine (Phases 1–9)
- `.github/skills/review-pr/agent-map.md` — file → agent mapping
- `.github/skills/review-pr/checklist-map.md` — file → checklist mapping
- `.github/skills/review-pr/rating-rubric.md` — severity and rating definitions
- `.github/skills/review-fix-loop/dismissal-rules.md` — when to dismiss vs. fix findings

---

## Configuration

| Parameter | Default | Description |
|-----------|---------|-------------|
| `max_rounds` | 5 | Maximum review-fix iterations before stopping |
| `target_rating` | 5 | Minimum acceptable rating for the *termination* path — a rubric **rating** (X/5), **not** the `APPROVE` action or council consensus. **Does not relax the PASS/merge gate**, which **always** requires unanimous 5/5 with no exception, allowance, or waiver (a `target_rating < 5` cannot satisfy "zero actionable findings" and is never PASS/merge-ready). |
| `auto_dismiss_low_consensus` | true | Auto-dismiss eligible single-seat (`1/N`) low-consensus findings after protected-domain checks |
| `auto_dismiss_out_of_diff` | true | Auto-dismiss findings referencing code not in the PR diff |

> **Council shape (via `review-pr`).** Each round runs a **scout** (cheap routing), the 4 fixed
> frontier lenses + up to **3 scout-selected specialist seats**, and a **decorrelated red-team
> gate** that executes C7 repros. Termination is the **PASS gate**, not a bare rating — see
> `review-pr/rating-rubric.md` (Rigor battery & the PASS gate).

---

## Phase 1: Initialization

### 1.1 Determine PR Context

Identify the target pull request using the same detection logic as review-pr Phase 1.1:

- **PR number provided explicitly**: Fetch the PR with GitHub tools or `gh pr view`.
- **Current branch has an open PR**: Detect from branch name.
- **No PR exists**: Abort — this skill requires a PR to push fixes to.

### 1.2 Checkout the PR Branch

Ensure you are on the PR's source branch and it is up to date:

```bash
git checkout {branch_name}
git pull origin {branch_name}
```

### 1.3 Initialize Progression Tracker

Create a tracking structure for round-by-round results:

```text
Round | Rating | Critical | High | Medium | Low | Info | Actionable Fixed | Dismissed | Status
```

### 1.4 Verify PR Scope Against Linked Issues

Before the first review round, verify that the PR's changes are scoped correctly to its linked issues. This prevents PRs from silently growing beyond their intended scope.

1. **Collect linked issues** — read the PR description for `Closes #N`, `Fixes #N`, `Resolves #N`, or `Refs #N` references.
2. **Read each linked issue** — fetch the issue title, description, and acceptance criteria.
3. **Build expected scope** — determine what files, components, and behaviors should be changed.
4. **Compare actual changes** — review changed file list against the expected scope.

| Check | Finding |
|---|---|
| Files changed outside expected scope | Flag tangential files as Medium and unrelated domain changes as High |
| Missing expected changes | Flag as Medium |
| No linked issues | Flag as High |
| Scope significantly exceeds issues | Flag as Medium with a recommendation to split the PR |

Scope verification is about traceability and preventing scope creep. It should not block tightly coupled tests, docs, or small supporting fixes.

### 1.5 Capture Run Identity

Before entering the review loop, capture and persist a Run Identity tuple:

- **`PR_NUMBER`** — resolved PR number.
- **`PR_BRANCH`** — current branch name.
- **`PR_URL`** — PR URL.
- **`INITIAL_HEAD_SHA`** — HEAD SHA at loop start.
- **`LOOP_START_UTC`** — loop-start timestamp in ISO-8601 UTC.

These values MUST be used verbatim by final report and verification steps. Do not accept a PR number passed in from outside without resolving against the current branch.

### 1.6 Halt-Marker Check (MANDATORY)

Before entering the review loop, query the PR for any open halt markers left by a prior invocation:

```bash
gh pr view {pr_number} --json comments   --jq '.comments[] | select(.body | contains("<!-- deltasharp-rfl-halt pr={pr_number} "))
          | {url: .url, createdAt: .createdAt, body_preview: (.body[0:300])}'
```

For each halt marker returned, check whether a matching closure comment exists:

```bash
gh pr view {pr_number} --json comments   --jq '.comments[] | select(.body | contains("<!-- deltasharp-rfl-halt-resolved pr={pr_number} halt_head=<halt_head_from_marker> "))'
```

If any halt marker has no matching closure comment, STOP. Surface a hard error listing each unresolved halt marker. The human must acknowledge or verify green CI before a later invocation records the closure comment and proceeds.

---

## Phase 2: Review (Invoke review-pr)

### 2.1 Execute the Review

Run the full `review-pr` skill pipeline — **Phases 1–8** (PR detection + complexity classification + scout → council → red-team gate), skipping only Phase 9 (GitHub Feedback) — against the current state of the PR branch. Use the same complexity classification, scout-driven agent selection (4 lenses + ≤3 specialists), review mode, checklist evaluation, and rigor battery.

**The red-team runs every round.** In round ≥2 its **fixer-diff re-review** treats every hunk changed since the previous round as newly-authored code and re-runs the full battery on it — this is what catches a **fix-induced regression in the same round it is introduced** (e.g. a fixer that widens a validator or adds a forgeable exemption). Fixer output is never pre-trusted.

**Important**: Skip review-pr Phase 9 (GitHub Feedback) on ALL intermediate rounds. GitHub feedback is posted exclusively by this skill's Phase 6 (Final Report) after the loop terminates.

### 2.2 Collect Structured Findings

Capture every finding from the review in structured format (the `review-pr` Finding Body Format Contract):

```json
{
  "id": "R{round}-F{number}",
  "severity": "critical | high | medium | low | info",
  "file": "path/to/file.ext",
  "line": 42,
  "finding": "Description of the issue",
  "recommendation": "Suggested fix",
  "evidence": "ran `<cmd>` → <output>  |  verified by reading <file:line>",
  "consensus": "k/N (N = 4 lenses + scout-selected specialists that reviewed this file)",
  "models_flagging": ["<seat>", "..."],
  "source": "voting-seat | red-team",
  "in_pr_diff": true
}
```

Also record the **red-team verdict** for the round (`MISS-FOUND | NO-MISS-CERTIFIED | provisional | n/a`) and, when `NO-MISS-CERTIFIED`, the **orchestrator's independent re-run** of a sampled C7 repro (command + output) that confirmed it — the gate (Phase 3) requires this self-verification before the loop may terminate.

### 2.3 Record Round Results

Update the progression tracker with the round's rating, finding counts by severity, and review mode used.

---

## Phase 3: Evaluate Findings

### 3.1 Load Dismissal Rules

Read `.github/skills/review-fix-loop/dismissal-rules.md` for the complete dismissal logic.

### 3.2 Categorize Each Finding

For every finding, apply the dismissal rules in priority order to classify it as one of:

| Category | Action | Criteria |
|----------|--------|----------|
| **Actionable** | Fix in this round | In-diff, sufficient consensus or Critical, clear recommendation |
| **Dismissed — Out of scope** | Skip | Finding references code not changed in this PR |
| **Dismissed — Pre-existing** | Skip | Issue exists in base branch, not introduced by this PR |
| **Dismissed — Low consensus** | Skip | Low consensus on non-Critical, non-protected-domain finding |
| **Dismissed — Design choice** | Skip | Finding challenges a documented design decision |
| **Deferred** | Track for follow-up; create tracking issue | Meets ALL Deferral Policy criteria in `dismissal-rules.md` |

### 3.3 Check Termination Conditions

**Stop the loop** if ANY of these are true:

1. **Gate PASS achieved** (per `review-pr/rating-rubric.md` → Rigor battery & the PASS gate) — every voting seat (4 lenses + each specialist) at **5/5** (the PASS gate requires 5/5 unconditionally — `target_rating` only relaxes the separate sub-target *termination* path, never this gate) **with a complete Approve attestation**, **zero** actionable findings, **zero open C1/C2/C4/C5/C6/C7 items**, **every deferred finding tracked by an orchestrator-verified GitHub issue** (`gh issue view <n>` → open, scope matches; see `review-pr/rating-rubric.md` → PASS gate), and the red-team **`NO-MISS-CERTIFIED`** (decorrelated, C7-backed). A red-team `MISS-FOUND` never satisfies this — its findings are actionable; continue. Apply the **anti-impasse rule**: a seat at sub-5/5 with no `file:line` finding is re-prompted once, then is an impasse — never terminate on a reinterpreted "rating with no findings". Proceed to Phase 6.
2. **Max rounds reached** — `current_round >= max_rounds`. Proceed to Phase 6 with a note that the limit was hit.
3. **No progress** — the count of actionable findings has not decreased from the previous round AND no findings were fixed. Proceed to Phase 6.

> The gate is the **PASS gate** (battery + red-team), not the review action — `APPROVE` spans 4/5 and 5/5, and a sub-5/5 with no finding is incoherent (anti-impasse). Terminate only on a real, finding-justified `5/5 ⭐` for every seat **and** a C7-backed red-team `NO-MISS-CERTIFIED`.

**Below-target with no actionable findings — DO NOT terminate without escalation.** Re-examine dismissed High+ findings and all deferred findings. If any deferral or dismissal is weak, reclassify as actionable and continue fixing. If all are legitimate, terminate with a detailed rationale. **A below-5/5 termination is a STOP, never a PASS** — do not label or report it as merge-ready; the PR remains blocked at 5/5 with no waiver.

If none of the termination conditions are met, proceed to Phase 4.

---

## Phase 4: Fix Actionable Findings

### 4.1 Group Findings by Agent

Using the agent-map from `review-pr`, group actionable findings by the agent persona best suited to fix them:

1. Match each finding's `file` path against the agent-map patterns.
2. Group findings by their matched agent.
3. If a finding matches multiple agents, assign it to the primary agent for that file (highest priority match).

### 4.2 Dispatch Agent Fixes

For each agent group, dispatch the appropriate specialist agent to fix the findings.

**Dispatch rules:**

- **Independent agent groups run in parallel.** If findings span multiple agents with no file overlap, dispatch all agents simultaneously.
- **Overlapping files serialize.** If two agents need to edit the same file, dispatch them sequentially to avoid conflicts.
- **Provide complete context** to each agent:
  - The specific findings assigned to them.
  - The relevant file content, not just the diff.
  - The applicable checklist items that were violated.
  - Instructions to make minimal, surgical fixes without introducing new issues.

**Agent dispatch prompt template:**

```text
You are fixing PR review findings for DeltaSharp. You are acting as the {agent_name} specialist.

## Findings to Address

{for each finding}
### {finding.id} — {finding.severity}
- **File**: {finding.file}:{finding.line}
- **Finding**: {finding.finding}
- **Recommendation**: {finding.recommendation}
- **Consensus**: {finding.consensus}
{end for}

## Instructions

1. Read each file that needs changes.
2. Make precise, surgical fixes that address each finding.
3. Do NOT modify code unrelated to the findings.
4. Do NOT introduce new patterns, refactors, or improvements beyond what the findings require.
5. Verify your changes do not break surrounding code.
6. After fixing, briefly confirm which findings were addressed and how.
```

### 4.3 Validate Fixes

After all agents complete:

1. **Check for conflicts** — ensure no file was modified by two agents in incompatible ways.
2. **Verify files are syntactically valid** — run available formatters, builds, and tests for affected file types.
3. **Spot-check critical fixes** — for Critical and High severity fixes, read the changed code to verify correctness.

---

## Phase 5: Commit, Push & Loop

### 5.1 Stage and Commit

Stage all fixed files and commit with a descriptive, **DCO-signed** message. Every fix commit MUST be signed off (`-s`) — the repo's `dco` required check fails unsigned commits, so an unsigned fix commit would prevent the loop from ever reaching green CI:

```text
git add {fixed_files}
git commit -s -m "Review fixes (Round {N}): Address {count} findings

Fixed:
- {finding.id}: {brief description} ({file})
- ...

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

`git commit -s` appends the `Signed-off-by:` trailer; keep the `Co-authored-by: Copilot` trailer as well.

### 5.2 Push

Push the fixes to the PR branch:

```bash
git push origin {branch_name}
```

### 5.3 Resolve PR Comments

If the review-pr skill posted inline comments on the PR for findings that have now been fixed:

- Check for existing review threads on the PR.
- For each fixed finding, if a matching inline comment exists, post a reply indicating what was fixed and resolve the thread.

### 5.4 Loop Back

Return to **Phase 2** for the next review round. Increment the round counter.

---

## Phase 6: Final Report

### 6.1 Execute Final Review

Regardless of termination reason, run one final review-pr pass (including Phase 9 — GitHub Feedback) to post the conclusive review to the PR. If the loop exited due to `max_rounds` or `no_progress`, include a note in the review body explaining why the loop stopped and listing any actionable findings that remain unresolved.

Immediately after the final review pass completes, capture:

- **`FINAL_HEAD_SHA`** — the HEAD SHA at report-generation time.
- **`FINAL_ROUND_COUNT`** — the total number of counted review rounds completed.

### 6.2 Generate Progression Report

Compile the full round-by-round progression:

```markdown
## 🤖 Review-Fix Loop — Final Report

<!-- deltasharp-rfl-report pr={pr_number} head={final_head_sha} rounds={final_round_count} -->

**PR**: #{pr_number} — {title}
**Branch**: {branch_name}
**HEAD SHA**: `{final_head_sha}`
**Rounds completed**: {final_round_count}
**Final rating**: {rating}/5 ⭐ — {label}
**Termination reason**: {Target achieved | Max rounds | No progress | Below target — all dismissals/deferrals verified legitimate}

### Progression

| Round | Rating | 🔴 Critical | 🟠 High | 🟡 Medium | 🔵 Low | ℹ️ Info | Fixed | Dismissed |
|-------|--------|-------------|---------|-----------|--------|---------|-------|-----------|
| R1    | {n}/5  | {c}         | {h}     | {m}       | {l}    | {i}     | —     | {d}       |
| R2    | {n}/5  | {c}         | {h}     | {m}       | {l}    | {i}     | {f}   | {d}       |

### Council Composition Audit

| Round | Slot | `agent_type` | `model` | Dispatch HEAD | Dispatch Timestamp (UTC) | Verification |
|-------|------|--------------|---------|---------------|--------------------------|--------------|
| R1 | Architect | `{agent_type}` | `{model}` | `{sha}` | `{iso8601}` | ✓ |

### Findings Addressed
{For each fixed finding across all rounds}

### Findings Dismissed
{For each dismissed finding and rationale}

### Findings Deferred
{For each deferred finding: description, why it cannot be fixed in this PR, and the **orchestrator-
verified** GitHub tracking issue — `#<n>`, confirmed **open** and scope-matching via `gh issue view`.
A deferral with no filed+verified issue is not allowed: file it, or reclassify as actionable and fix.}

### Findings — Inherent / Won't-Fix
{For each finding adjudicated as inherent to the approach (not separately tracked): description + the
durable in-code/in-PR rationale that records the decision. Not a substitute for deferring fixable work.}

### Validation
- Restore/build/format/test evidence for .NET changes.
- CI status verification evidence if remote PR checks are available.

### Commits
{List of fix commits with SHAs and messages}
```

### 6.3 Post Final Summary

If the PR is on GitHub, post the progression report as a PR comment (not a review) so it serves as a permanent record of the review-fix process.

### 6.4 Verify the Final Report Was Posted (MANDATORY)

The orchestrator MUST not claim the loop terminated successfully until it has verified, on GitHub, that the progression report comment exists on the PR.

Verification procedure:

1. Use the Run Identity captured in §1.5.
2. Query for this run's report by run-identity marker:

   ```bash
   gh pr view {pr_number} --json comments --jq '.comments[]
     | select(.body | contains("<!-- deltasharp-rfl-report pr={pr_number} head={final_head_sha} rounds={final_round_count} "))
     | {url: .url, createdAt: .createdAt, body_preview: (.body[0:300])}'
   ```

3. Validate the matched comment body contains the report heading, progression table, final rating field, council composition audit, and is newer than `LOOP_START_UTC`.
4. On missing or invalid comment, re-render the report to a project-relative scratch path such as `.rfl-report-{PR_NUMBER}.md`, repost with `gh pr comment {PR_NUMBER} --body-file .rfl-report-{PR_NUMBER}.md`, re-verify, then delete the file after success.
5. Capture the verified comment URL and include it in the final response as `RFL Report: <url>`.

### 6.5 Council Composition Audit (MANDATORY)

Before declaring the loop terminated, audit the council composition record produced per `review-pr` §3.1:

1. Enumerate every counted round in the progression report.
2. For each round, locate the per-slot composition row.
3. Verify each row lists an exact `(agent_type, model)` pair from the protocol table in `review-pr` §3.1.
4. Missing or off-protocol composition data invalidates the round and requires corrective review at the current HEAD or the original HEAD when recoverable.
5. After any corrective dispatch, regenerate, repost, and re-verify the report before terminating.

### 6.6 CI Status Verification (MANDATORY WHEN REMOTE PR CHECKS EXIST)

Local build/test/lint output is not sufficient evidence of CI green when the PR has remote required checks. Before declaring the loop terminated, poll the GitHub status check rollup and verify every required check has a passing conclusion at the post-fix HEAD SHA.

Procedure:

1. Resolve `POST_FIX_HEAD=$(git rev-parse HEAD)` and required check names from branch protection when accessible, with a fallback allowlist appropriate for DeltaSharp (Build, Test, Format, Packaging, Integration Tests, Operator Tests, Benchmark Gate).
2. Query the status check rollup:

   ```bash
   gh pr view {pr_number} --json statusCheckRollup,headRefOid
   ```

3. Classify checks: passing, failing, skipped, in-flight, or missing-but-required.
4. Poll in-flight checks with a bounded wait.
5. Treat failures as new findings routed back to Phase 4 unless a human explicitly triages external infrastructure failure.
6. Add a `### CI Status Verification` section to the progression report listing the verified HEAD, timestamp, required-check source, and rollup.
7. On hard-error termination, post a sticky PR comment with marker `<!-- deltasharp-rfl-halt pr={pr_number} head={post_fix_head} reason=<short_slug> -->` and require a closure marker before a future invocation proceeds.
8. Only after every required check is passing may the loop be declared terminated.

### 6.7 Fold-forward (council learning)

After the final round, capture the **miss-classes** the red-team (or a later round) caught that
the voting seats missed, in the progression report's findings sections. When a miss-class recurs
— or an external/frontier review or a later bug catches something this council missed — **fold
the class forward**: sharpen the matching gate in `review-pr/rigor-battery.md` (C1–C7), the
red-team's hunt list in `review-pr/red-team.md`, and/or the relevant checklist, and add a row to
[`regression/README.md`](regression/README.md). Fix the *class*, not just the instance. (Do not
commit per-run scratch logs to the repo — that is itself a C6 hygiene miss.)

---

## Important Notes

- **The review-pr skill is the evaluation engine.** This skill orchestrates the loop; review-pr does the actual reviewing. Do not duplicate review logic.
- **Fixes must be minimal and surgical.** Agent fix dispatches should address specific findings, not refactor or improve beyond what was flagged.
- **Dismissal discipline prevents churn.** Apply dismissal rules consistently.
- **The safety valve is non-negotiable.** Never exceed `max_rounds`.
- **Intermediate rounds skip GitHub feedback.** Only the final round posts to GitHub.
- **Track everything.** The progression report is the primary output of this skill.

---

## Merge Policy — Mandatory Quality Gate

**AI agents MUST NOT merge pull requests.** Merging is a human-only action. The agent's responsibility ends at delivering a PR that meets all quality criteria.

**AI agents MUST NOT push directly to main.** All changes must go through a pull request.

### Pre-Merge Checklist (ALL required before a PR is merge-ready)

1. Full multi-model council RFL completed.
2. **PASS gate met** (`review-pr/rating-rubric.md`): every voting seat 5/5, zero actionable findings, zero open C1/C2/C4/C5/C6/C7 items, and a **decorrelated red-team `NO-MISS-CERTIFIED`** that the orchestrator **independently re-verified** (re-ran a sampled C7 repro). A loop that merely *stopped* below 5/5 is **never** merge-ready — **there is no exception, allowance, or human waiver for a sub-5/5 seat**; fix the finding and re-score to 5/5. `target_rating` governs only the termination path, never the merge gate.
3. Triage verification completed for any round with 5+ dismissals or any dismissed findings in protected domains.
4. Dismissed findings audited and real items tracked as backlog issues.
5. **Every deferred finding has a GitHub tracking issue the orchestrator verified exists** (`gh issue view <n>` → open, scope matches the finding), with its number recorded in the report. **No un-filed deferral may PASS**; any `inherent/won't-fix` residual instead carries a durable in-code/in-PR rationale.
6. Full progression report posted as a PR comment with council composition audit and verified URL.
7. Restore/build/format/test validation green for .NET changes.
8. CI green on all required checks when remote checks exist.

### What is NOT acceptable

- "Quick verification" after a rebase.
- "Already reviewed before rebase".
- "CI will catch it".
- Merging by the agent.
- Skipping the dismissed findings audit.

### When to re-run the full RFL

A full council RFL must be re-run when:

- The PR is rebased with conflict resolution.
- New commits are pushed after the RFL report was posted.
- The PR base branch changed.
- More than 24 hours have passed since the last full RFL.
