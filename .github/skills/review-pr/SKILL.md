---
name: review-pr
description: >-
  Orchestrates world-class pull request reviews using specialist agent personas and engineering checklists.
  Use this when asked to review a PR, review code changes, or assess PR quality.
  A cheap scout triages and routes; a multi-frontier council (Claude Opus 4.8 + GPT-5.5) reviews
  complex changes with up to 3 scout-selected domain specialists; a decorrelated red-team executes
  repros (C7) and gates. Posts findings as GitHub PR comments for remote PRs.
---

# PR Review Skill — Orchestration Instructions

Execute the following phased pipeline (Phase 1 → 1.6 scout → 2–7 council → 8 red-team gate → 9 feedback) to produce a thorough, actionable pull request review. Read all supporting files from this skill directory before beginning.

---

## Phase 1: PR Analysis & Triage

### 1.1 Determine Review Context

Detect whether a GitHub PR is available:

- **PR number provided explicitly**: Use GitHub tools or `gh pr view` to fetch the PR title, description, diff, files changed, and linked issues or work items.
- **Current branch has an open PR**: Run `git branch --show-current` to get the branch name, then find an open PR for that branch. If found, fetch its details as above.
- **No PR exists (local-only changes)**: Use `git diff main...HEAD` (or the appropriate base branch) to collect the changes. Note that **Phase 9 (GitHub Feedback) will be skipped** for local-only reviews.

### 1.2 Collect File List and Diff

- For GitHub PRs: get the full file list and complete diff.
- For local changes: run `git diff --name-only main...HEAD` for the file list and `git diff main...HEAD` for the full diff.

### 1.3 Classify Complexity

Evaluate the change against the following criteria. If **ANY single criterion** falls into the "Complex" column, classify the entire review as **Complex** and use multi-model council mode.

| Criteria | Simple | Complex |
|---|---|---|
| Files changed | 1–5 | 6+ |
| Domains touched | 1 | 2+ |
| Cross-cutting concerns | None | Auth, tenant isolation, storage correctness, operator/runtime, distributed execution |
| ADR / architecture changes | No | Yes |
| New service or component | No | Yes |
| Schema / API changes | No | Yes |
| Delta/Parquet/protocol changes | No | Yes |
| Planner/optimizer/execution changes | No | Yes |

To determine "domains touched," group changed files by functional area (for example, `src/DeltaSharp.Core/`, `src/DeltaSharp.Storage/`, `src/DeltaSharp.Operator/`, `docs/`, `deploy/`). Each distinct area counts as one domain.

### 1.4 Identify Governing Design Document(s)

Before proceeding to agent selection or review dispatch, determine whether a design document governs this change:

1. Read the PR description and linked issues — look for references to design documents, ADRs, or `docs/engineering/design/` paths.
2. Search `docs/engineering/design/` for documents matching the component, service, or package being changed.
3. If the PR itself **is** a design document, note this — Phase 5 will be skipped, but checklist evaluation still applies.

Record the result for use in Phase 3 and Phase 5:

- **Design document(s) found**: List paths and document numbers.
- **No design document**: Note the reason (design doc PR, pure refactor, no doc exists).

### 1.5 Output Triage Summary

```markdown
### Triage Summary
- **Files changed**: {count}
- **Domains touched**: {list of domains}
- **Complexity classification**: Simple | Complex
- **Triggering criteria**: {which criteria triggered Complex, if applicable}
- **Review mode**: Single Model | Multi-Model Council
- **Design document(s)**: {list of governing design docs, or "None applicable — {reason}"}
```

---

## Phase 1.6: Scout Triage — the Review Package

Before selecting agents, dispatch the **scout** (`.github/skills/review-pr/scout.md`) to produce
the **Review Package** — the routing record for the whole review. The scout runs on a cheap
frontier model (`gemini-3.5-flash` / `gpt-5-mini` / `claude-haiku-4.5`; `agent_type: explore` or
`general-purpose`) so the voting seats spend their budget reviewing, not triaging.

It returns: complexity (Simple/Complex), changed files by domain, the recommended `agent_type`
per fixed lens, a roster of **≤3 domain specialist seats** (with verified `CANONICAL_SPEC` paths
from `docs/persona/agents/`), per-seat checklist IDs, the claims to verify (C4/C7), and a red-team
decorrelation hint. Verify each specialist's `CANONICAL_SPEC` exists; drop any that don't.

For a trivial (1–2 file, docs-only) change the orchestrator may inline the scout's logic instead
of dispatching it — but must say so in the triage summary. Phases 2–4 and 8 consume the Review
Package.

---

## Phase 2: Agent Persona Selection

### 2.1 Load Agent Mapping

Read the agent mapping file at `.github/skills/review-pr/agent-map.md`.

### 2.2 Match Files to Agents

For each changed file, determine relevant agent persona(s). The available DeltaSharp agents are:

- `product-manager`
- `program-manager`
- `technical-writer`
- `privacy-compliance-grc-lead`
- `cloud-native-distributed-systems-architect`
- `cloud-native-site-reliability-engineer`
- `cloud-native-security-sme`
- `reliability-test-chaos-engineer`
- `delta-storage-format-engineer`
- `query-execution-engine-engineer`
- `performance-benchmarking-engineer`
- `data-platform-connectors-engineer`
- `compute-storage-finops-engineer`
- `developer-experience-api-engineer`
- `dotnet-framework-runtime-engineer`

### 2.3 Determine Primary and Secondary Agents

- **Primary agent**: The agent whose domain covers the most changed files or the most critical domain in the change.
- **Secondary agents**: All other agents whose domains are touched by the change.

### 2.4 Apply Selection Rules

- If only **1 domain** is touched, use only the primary agent. Do not invoke secondary agents unless the file is in a protected domain.
- If **multiple domains** are touched, each domain gets its own agent review. Dispatch each agent's review separately so domain-specific expertise is applied independently.
- **Fallback**: If no agent matches any changed file, use `technical-writer` for Markdown/documentation files, `dotnet-framework-runtime-engineer` for generic C#/.NET code, or `cloud-native-distributed-systems-architect` for architecture-level changes. Never proceed with zero agents.

---

## Phase 3: Review Mode Decision

Based on the complexity classification from Phase 1, execute one of the following modes.

### Simple Change — Single-Model Review

Use the default model. Run the review through the primary agent persona's lens:

1. Apply the primary agent's system prompt and domain expertise to the diff.
2. Load and apply the relevant checklists (see Phase 4).
3. Produce findings in the structured format: `{severity, file, line, finding, recommendation}`.

### Complex Change — Multi-Model Council

Dispatch **4 parallel reviews** using the `task` tool. Each slot has a fixed **role** and **model**; the `agent_type` is selected per-PR from a closed **allowlist** to bring specialist domain expertise to the slot when the PR's primary content warrants it. All 4 calls **must** be made in parallel, not sequentially.

| Slot | Role | Model | `agent_type` allowlist |
|---|---|---|---|
| **Architect** | Deep reasoning — architecture implications, subtle bugs, design flaws | `claude-opus-4.8` (effort high/max) | `general-purpose`, `cloud-native-distributed-systems-architect`, `query-execution-engine-engineer`, `delta-storage-format-engineer`, `data-platform-connectors-engineer` |
| **Balanced** | Code quality, patterns, maintainability, operational pragmatism | `claude-opus-4.8` (effort high/max) | `general-purpose`, `dotnet-framework-runtime-engineer`, `cloud-native-site-reliability-engineer`, `developer-experience-api-engineer` |
| **Quality** | Testability, measurability, reliability, alternative pattern recognition | `gpt-5.5` (effort high) | `general-purpose`, `reliability-test-chaos-engineer`, `performance-benchmarking-engineer`, `technical-writer` |
| **Security** | Tenant isolation, auth bypass, injection, supply-chain, cryptographic correctness, privacy/compliance | `claude-opus-4.8` (effort high/max) | `cloud-native-security-sme`, `privacy-compliance-grc-lead`, `general-purpose` |

> **Models track the newest top-tier of each family** — currently Claude **Opus 4.8** (in place
> of older Opus 4.7 / Sonnet 4.6) for the deep / balanced / security lenses, and **GPT-5.5** for
> Quality. Update these as families advance. The red-team (Phase 8) runs on a family **distinct
> from the majority spine and ideally used by no voting seat** (here: Gemini) — see Phase 8.

**Models are fixed per slot.** Diverse pattern recognition across the council comes from the model mix; domain specialization comes from the per-slot `agent_type` choice.

**Specialist seats (scout-selected, ≤3).** In addition to the 4 fixed lenses, dispatch each
domain specialist from the scout's Review Package as an **additional voting seat** on a frontier
model (`agent_type` = the specialist persona; model = a top-tier family, e.g. `claude-opus-4.8`),
scoped to its owned files + checklist IDs. The 4 lenses are the spine; specialists add depth for
the domains the diff actually touches (Delta storage, query execution, operator, connectors, …).

**Execution is mandatory for execution-eligible claims (C7).** Any seat verifying an enforcement /
parity / compat / migration / test-efficacy claim must either **run** a repro (see
[`rigor-battery.md`](rigor-battery.md)) and quote command + output, or **explicitly defer the claim
to the red-team** (the canonical C7 executor) — "verified by reading" does not clear a C7-eligible
claim. A seat expected to execute MUST be dispatched **shell-capable** (`agent_type:
general-purpose`, or another tool-capable type); a file-view-only persona seat may only review by
reading and must defer every C7 claim. A seat that silently withholds judgment because it "couldn't
run it" (instead of deferring, or being re-dispatched shell-capable) is a dispatch error, not a
finding.

**Selection rule for `agent_type`:**

1. Identify the PR's primary content domain from changed files and PR description.
2. For each slot, pick the allowlist member whose domain most closely matches the PR's primary content. If multiple match, prefer the more-specific specialist. If none matches better than `general-purpose`, use `general-purpose` (or `cloud-native-security-sme` for the Security slot).
3. Record the choice and a one-sentence justification in the round dispatch record.

Each model receives the same input package:

- The full PR diff.
- The relevant agent persona instructions.
- The applicable checklists.
- The governing design document(s), if any.
- DeltaSharp canon: Spark parity, lazy/eager semantics, Catalyst-style pipeline, Delta tables, Kubernetes driver/executor/operator, storage across object stores and PVCs.
- Instructions to return findings in structured format.

If a model fails or times out, proceed with remaining models and note the gap in the final report.

### 3.1 Council Composition Verification (MANDATORY)

After dispatching the 4 reviewers and **before** reporting any aggregate council result, verify that each slot's actual `(agent_type, model)` pair conforms to the protocol.

1. **Read back each dispatch.** For each reviewer, look at the actual `agent_type` and `model` arguments passed.
2. **Validate against the protocol** verbatim. A round is invalid if any slot's model does not match its fixed value or if any `agent_type` is not in its allowlist.
3. **Correct off-protocol dispatches** by re-dispatching affected slots at the same HEAD SHA when possible; otherwise re-run the full round at current HEAD.
4. **Capture verified composition** per round: slot name, `agent_type`, `model`, justification, dispatch HEAD SHA, and dispatch timestamp.
5. **Composition verification gates aggregate-rating claims.** Do not claim unanimity, consensus, or aggregate rating until verified.
6. **Externalized composition record.** At each round's close, post the verified composition record as a PR comment or persist it in an auditable project-relative path for local-only reviews. Prior records are append-only.

---

## Phase 4: Checklist-Based Quality Review

### 4.1 Load Checklist Mapping

Read `.github/skills/review-pr/checklist-map.md`.

### 4.2 Match Files to Checklists

For each changed file, determine which checklists apply. A single file may trigger multiple checklists (for example, C# code under `src/**/Optimizer/**` may trigger .NET standards, Catalyst planning, tests, and performance).

### 4.3 Load Checklists

Load each applicable checklist from `docs/engineering/checklists/`. If an intended DeltaSharp checklist is not authored yet, record that it was unavailable and apply `.github/copilot-instructions.md` as canon.

### 4.4 Evaluate Changes Against Checklists

Walk through each checklist item and evaluate whether the PR's changes comply:

- **Pass**: The change satisfies the checklist item or the item is not applicable.
- **Fail**: The change violates the checklist item. Create a finding with appropriate severity.
- **Indeterminate**: Cannot tell from the diff alone. Flag as an `info`-level finding suggesting manual verification.

Record every checklist violation as a finding in standard format.

---

## Phase 5: Design Document Conformance

Implementation PRs **must** be cross-checked against their governing design document. A design document is the source of truth for what the implementation should do — checklist compliance alone is insufficient.

### 5.1 Determine if a Design Document Applies

A design document applies when any of the following are true:

- The PR description or linked issues reference a design document.
- The changed files implement a component that has a design document in `docs/engineering/design/`.
- The PR title uses a `feat:` or `fix:` prefix targeting a component covered by a design document.

**Skip this phase** if:

- The PR is a design document.
- No design document exists for the component (flag as an `info` finding).
- The PR is a pure refactor, dependency update, or CI/tooling change with no functional behavior changes.

### 5.2 Locate the Design Document

1. Check linked issues for design document references.
2. Search `docs/engineering/design/` for documents matching the component name or service area.
3. Check the PR description for explicit design document references.

If multiple design documents are relevant, load all of them.

### 5.3 Read and Cross-Check

Load the full design document(s). For each of the following areas, compare the implementation against the spec:

| Area | What to Check |
|---|---|
| **API surface** | Do public types, methods, overloads, and interfaces match the design doc and Spark parity goals? |
| **Behavior** | Does the implementation follow behavioral contracts, especially lazy/eager semantics and action-triggered execution? |
| **Planning** | Do logical/analyzed/optimized/physical plan transformations match the design? |
| **Storage** | Do Delta log, Parquet, schema evolution, time travel, commit, snapshot, and checkpoint behaviors match the design? |
| **Execution** | Are stages, tasks, shuffle boundaries, driver/executor coordination, and failures handled as specified? |
| **Configuration** | Are all configuration options supported and loaded correctly? |
| **Dependencies** | Does the implementation use the specified packages/frameworks and abstractions? |
| **Security controls** | Are security and tenant isolation measures implemented? |
| **Observability** | Are metrics, traces, and logs emitted as specified? |
| **Test scenarios** | Do tests cover functional scenarios and acceptance criteria? |

### 5.4 Generate Conformance Findings

For each deviation from the design document:

- **Missing feature/behavior** specified in the design doc → **High** severity finding.
- **Partial implementation** → **Medium** severity finding.
- **Behavioral divergence** where the implementation works differently than specified → **High** severity finding.
- **API signature mismatch** → **Medium** severity finding, **High** if it breaks Spark parity.
- **Delta/storage correctness divergence** → **High** or **Critical** depending on data integrity risk.

Format each finding with explicit reference to the design document section.

### 5.5 Include in Report

Add a **Design Doc Conformance** section to the final report:

```markdown
### Design Doc Conformance
- Design document(s): {list of design docs checked, or "None applicable — {reason}"}
- Conformance: ✅ Full | ⚠️ Partial ({N} deviations) | ❌ Significant gaps ({N} deviations)
```

---

## Phase 6: PR Metadata Verification

If this is a **local-only review**, skip title and description checks. Instead, verify commit messages are meaningful and properly formatted, then proceed to Phase 7.

For GitHub PRs, check:

### 6.1 PR Title

- Does the title accurately describe the scope of the change?
- Does it follow conventional commit format or project title conventions?
- Is it concise but informative?

### 6.2 PR Description

- Does the description explain what changed and why?
- Is the level of detail proportional to scope?
- Are test instructions and validation evidence present?

### 6.3 Work Item Reference

- Is there a linked issue or work item?
- Does the referenced item match the actual scope?
- Flag missing or mismatched references.

---

## Phase 7: Rating & Consensus Report

### 7.1 Load Rating Rubric

Read `.github/skills/review-pr/rating-rubric.md`.

### 7.2 Calculate Overall Rating

Apply the rubric criteria to the collected findings to determine the overall rating (1–5 stars).

### 7.3 Compile and Order Findings

Sort all findings by severity: **Critical → High → Medium → Low → Info**.

### 7.4 Apply Consensus Scoring (Multi-Model Council Only)

If the review used multi-model council mode:

- Deduplicate semantically identical findings across models.
- Count how many models flagged each unique finding.
- Display consensus as a fraction.
- Apply the consensus and protected-domain rules in the rating rubric.

### 7.5 Generate Final Report

Produce the report in this exact structure:

```markdown
## PR Review Report

**PR**: #{number} — {title}
**Rating**: {1-5} ⭐ — {rating_label}
**Review Mode**: {Single Model | Multi-Model Council}
**Models Used**: {list of models used}
**Agents Applied**: {list of agent personas used}
**Checklists Applied**: {list of checklists evaluated}

### Summary
{2-3 sentence summary of overall quality and key concerns.}

### Findings

#### 🔴 Critical ({count})
{Each finding with: file, line, description, recommendation. Include consensus indicator if multi-model.}

#### 🟠 High ({count})
{findings}

#### 🟡 Medium ({count})
{findings}

#### 🔵 Low ({count})
{findings}

#### ℹ️ Info ({count})
{findings}

### PR Metadata
- Title: ✅/❌ {assessment}
- Description: ✅/❌ {assessment}
- Work Item: ✅/❌ {assessment}

### Design Doc Conformance
- Design document(s): {list of design docs checked, or "None applicable — {reason}"}
- Conformance: ✅ Full | ⚠️ Partial ({N} deviations) | ❌ Significant gaps ({N} deviations)

### Validation Evidence
- Restore/build/format/test: {status and commands observed, if applicable}
- Benchmarks/regression gates: {status, if applicable}

### Recommendation
{APPROVE | REQUEST_CHANGES | COMMENT} — {rationale for the recommendation}
```

For local-only reviews, omit the `**PR**:` line and `### PR Metadata` section. Replace with `**Review Target**: Local changes ({branch_name} vs {base_branch})`.

---

## Phase 8: Adversarial Red-Team Gate

### Skip Condition (Simple changes)

For a **Simple** review (scout says Simple) with **no execution-eligible claim** in the diff, run a
single lightweight/inlined red-team pass — or skip it and record `red-team: n/a — Simple, no
execution-eligible claim`. The **full decorrelated, shell-capable red-team is required for all
Complex changes** and for any change touching a protected domain.

After the rating, dispatch the **red-team** (`.github/skills/review-pr/red-team.md`) — the council's
gate-keeper. It runs **last**, **shell-capable** (`agent_type: general-purpose`), on a frontier
family **distinct from the majority voting spine and ideally used by no voting seat** (if the spine
is Opus + GPT, use `gemini-3.1-pro-preview`; do not reuse a voting seat's family such as `gpt-5.5`).

Give it the diff, the Review Package, and **every voting seat's full verdict + findings**. It
assumes the PR is broken, tries to falsify the seats' approvals, hunts the council's historical
miss-classes, and **executes C7 repros** (it does not reason about them). It returns findings in the
canonical `Critical|High|Medium|Low|Info` set and a verdict:

- `MISS-FOUND` — with new findings (each `file:line` + EVIDENCE). These are **actionable and
  blocking**; in a fix-loop they go back to the fix phase.
- `NO-MISS-CERTIFIED` — only valid with a **fully-populated Falsification-Attempts block** and a C7
  line quoting real commands + output for every execution-eligible claim. A bare "no issues" is
  rejected and **re-prompted once**; if still non-conformant, certification is **denied** (treat as
  `MISS-FOUND` / escalate).

**Independently verify before trusting the certification (anti-forgery).** The red-team's verdict,
attestation, and quoted C7 output are self-asserted signals — before accepting `NO-MISS-CERTIFIED`
the orchestrator MUST re-run at least one sampled C7 repro from its evidence block and confirm the
output matches.

Record which model gated. If the red-team shares the voting spine's family (no decorrelated frontier
available), its certification is **provisional** and does **not** satisfy the gate for protected-
domain changes — flag it and require a decorrelated re-run or a documented human waiver. The gate
(`rating-rubric.md`) requires `NO-MISS-CERTIFIED`.

---

## Phase 9: GitHub PR Feedback

### Skip Condition

Skip this phase entirely if reviewing local-only changes with no GitHub PR. Output the report from Phase 7 directly to the user and stop.

### 9.1 Determine Review Action

Submit the review action determined by **both** the rating and the Phase 8 red-team verdict:

- A red-team **`MISS-FOUND`** (or a denied / withheld certification) → submit as **REQUEST_CHANGES**,
  regardless of the rating. Never post `APPROVE` over an open red-team miss.
- Otherwise (red-team `NO-MISS-CERTIFIED`, independently re-verified per Phase 8): **Rating 4–5** →
  **APPROVE**; **Rating 3** → **COMMENT**; **Rating 1–2** → **REQUEST_CHANGES**.

`APPROVE` is a review action, not a merge gate; the rating and red-team verdict together are the
quality signal, and AI never merges. **The merge/PASS gate is unanimous 5/5 with no exception,
allowance, or waiver — a 4/5 `APPROVE` is review feedback, not PASS, and must never be presented as
merge-ready (see `rating-rubric.md` → PASS gate).**

### 9.2 Post the Review

Use GitHub tools to submit the review:

1. Submit the overall review with the recommendation.
2. Post every finding that has a file and line reference as an inline review comment.
3. Format each inline comment with severity badge, finding description, and recommendation.
4. Info findings remain in the review body only.
5. If a finding references a file but not a specific line, post it as a file-level review comment when supported.

### 9.3 Avoid Duplicates

Before posting, check for existing review comments from previous runs of this skill. Do not post duplicate comments on the same finding at the same file and line.

### 9.4 Track Deferred Findings as Issues (gate-blocking)

Any valid finding that is **not fixed in this PR** must be dispositioned as exactly one of: **fixed**,
**dismissed-with-rationale**, **deferred**, or **inherent/won't-fix-documented** (see
`review-fix-loop/dismissal-rules.md` → Deferral Policy). For **every deferred finding**, file a GitHub
tracking issue and **verify it exists** (`gh issue view <n>` returns an **open** issue whose scope
matches the finding), then record its number in the report. An `inherent/won't-fix` disposition
carries a durable in-code/in-PR rationale instead of an issue. **A deferral with no filed+verified
tracking issue blocks PASS** (`rating-rubric.md` → PASS gate) — file it, or reclassify as actionable
and fix it. This applies whenever a repository is available, including local-only reviews.

---

## Important Notes

- **Always read supporting files first.** Load `scout.md`, `agent-map.md`, `checklist-map.md`, `rating-rubric.md`, `rigor-battery.md`, and `red-team.md` before starting the pipeline.
- **Scout first, red-team last.** Every Complex review is bracketed by a cheap scout (routing) and a decorrelated red-team (adversarial gate). The scout selects ≤3 specialist seats; the red-team must execute C7 repros and certify (`NO-MISS-CERTIFIED`) before the gate can PASS.
- **Execution over reading (C7).** Seats and the red-team must RUN execution-eligible claims; dispatch any executing seat shell-capable (`general-purpose`), never a file-view-only persona.
- **Decorrelate the red-team.** Run it on a frontier family distinct from the majority voting spine; same-family certification is provisional for protected-domain changes.
- **Parallel execution in council mode.** The 4 model reviews must run in parallel, not sequentially.
- **Handle model failures gracefully.** If a model fails or times out, proceed with remaining models and note which were unavailable.
- **DeltaSharp canon is mandatory.** Reviews must enforce Spark parity, lazy/eager semantics, Catalyst-style planning, Delta/Parquet correctness, Kubernetes driver/executor/operator safety, object-store/PVC storage support, and .NET runtime correctness.
- **Professional and constructive tone.** Findings should help the author improve; be specific and suggest concrete fixes.
- **Design document conformance is mandatory for implementation PRs.** The design document is the source of truth.
- **Every deferral is a filed, verified issue.** No finding is left "to track later": each unfixed finding is fixed, dismissed-with-rationale, deferred with an orchestrator-verified GitHub tracking issue (`gh issue view <n>` → open), or dispositioned inherent/won't-fix with a durable rationale. An un-filed deferral blocks PASS (`rating-rubric.md` → PASS gate).
- **No duplicate comments.** Never post duplicate comments on the same finding.
