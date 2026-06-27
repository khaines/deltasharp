---
name: design-doc
description: >-
  Generates comprehensive component design documents from GitHub issues. Orchestrates specialist
  agent personas — Architect for structure and logic, Security SME for threat modelling, SRE for
  observability and rollout — to produce a complete design document following the standard template.
  Creates a worktree branch, commits the design doc, opens a PR, and invokes the review-fix-loop
  skill for automated quality improvement.
---

# Design Document Skill — Orchestration Instructions

Generate a comprehensive design document for a DeltaSharp component or feature. Read all supporting files before beginning:

- `docs/engineering/design/000-template.md` — the design document template (all sections must be populated)
- `docs/engineering/design/README.md` — design doc conventions and file placement rules
- `.github/skills/design-doc/section-map.md` — which agent persona owns which template section
- `.github/skills/design-doc/checklist-refs.md` — which engineering checklists to cross-reference per section
- `.github/skills/review-fix-loop/SKILL.md` — the review-fix loop used in Phase 8

---

## Phase 1: Issue Analysis & Context Gathering

### 1.1 Identify the Source Issue

Determine the GitHub issue that defines the work being designed:

- **Issue number provided explicitly**: Use GitHub issue tools or `gh issue view` to fetch the issue title, body, labels, linked issues, and comments.
- **Issue URL provided**: Extract the owner, repo, and issue number from the URL, then fetch as above.
- **No issue provided**: Ask the user to provide an issue number or URL. This skill requires a source issue to proceed.

### 1.2 Extract Design Context

From the issue, extract:

1. **Component name**: Derive from the issue title or body. This becomes the document title and filename slug.
2. **Feature category**: Determine from issue labels, referenced requirements, or the component's domain. Use these DeltaSharp categories as a guide:
   - `api-surface` — `SparkSession`, `DataFrame`, `Dataset<T>`, `Column`, functions, SQL surface, samples
   - `query-planning` — logical plan, analyzer, optimizer, physical planner, Catalyst-style rules
   - `execution-engine` — actions, stages, tasks, shuffle boundaries, scheduler, executor protocol
   - `delta-storage` — Delta transaction log, Parquet, ACID commits, time travel, schema evolution
   - `connectors` — data sources/sinks, catalogs, object-store/PVC adapters
   - `operator-runtime` — Kubernetes Operator, CRDs, driver and executor pods, rollout and operations
   - `cross-cutting` — security, tenant isolation, privacy, performance, reliability, cost, documentation
   - If the component spans categories or does not fit, place the doc flat in `docs/engineering/design/`.
3. **Referenced requirements**: Scan the issue body for `REQ-*` identifiers. If found, read the corresponding requirements doc from `docs/product/requirements/` to gather acceptance criteria, priority, and dependencies.
4. **Related ADRs**: Scan the issue body and referenced requirements for ADR references. Read any linked ADRs from `docs/engineering/adr/`.
5. **Existing design docs**: Check `docs/engineering/design/` for any existing design doc for this component. If one exists, this is an **update** operation — see the Update Mode Reconciliation note in Important Notes.

### 1.3 Determine File Placement

Based on the extracted context:

- If the component clearly belongs to a single feature category, place the doc at `docs/engineering/design/<category>/<component-slug>.md`. Create the subdirectory if it does not exist.
- If the component spans categories or is a platform-level concern, place it at `docs/engineering/design/<component-slug>.md`.
- Use lowercase kebab-case for the filename: `spark-session-api.md`, `delta-commit-protocol.md`, `shuffle-stages.md`.

### 1.4 Output Context Summary

Before proceeding, output a context block:

```text
📋 Design Document Context
━━━━━━━━━━━━━━━━━━━━━━━━━
Issue:       #NNN — [Title]
Component:   [Component Name]
Category:    [Feature Category or "Platform-level"]
File:        docs/engineering/design/[path]
Mode:        New | Update
Requirements: [REQ-XXX-001, REQ-XXX-002, ...] or "None referenced"
ADRs:        [ADR-NNN, ...] or "None referenced"
```

---

## Phase 2: Architect — Structure & Logic

### 2.1 Load Architect Context

Read the section map (`.github/skills/design-doc/section-map.md`) to confirm which sections the Architect agent owns. Load the following reference docs when present:

- `docs/engineering/best-practices/01-architecture.md`
- `docs/engineering/best-practices/02-distributed-engine.md`
- `docs/engineering/checklists/01-architecture-checklist.md`
- `docs/engineering/checklists/02-engine-implementation-checklist.md`
- Any ADRs referenced by the issue

Referenced checklist and best-practices files may be DeltaSharp equivalents that are not authored yet; cite them as intended references without inventing their contents.

### 2.2 Generate Architecture Sections

Using the `cloud-native-distributed-systems-architect` agent persona, generate:

- **§1 · Overview** — What the component is, why it matters for Spark parity / native Delta tables / Kubernetes execution, and requirements traceability.
- **§2 · Logical Architecture** — High-level architecture diagram (Mermaid `graph`), component boundaries table, data flow (Mermaid `sequenceDiagram`), plan/data model, API surface, dependencies table, and tenant/storage-backend considerations.
- **§9 · Open Questions & Decisions** — Initial questions surfaced during architecture analysis.

### 2.3 Architecture Quality Gates

Validate the generated architecture sections against the architecture checklist:

- [ ] API, logical plan, analyzer/optimizer, physical plan, execution, storage, and operator boundaries are explicit and non-overlapping.
- [ ] Lazy transformations and eager actions are preserved; API code builds plans and does not execute work directly.
- [ ] Stage splitting at shuffle boundaries is represented where execution is involved.
- [ ] Delta table interactions identify Parquet files, `_delta_log`, ACID commit protocol, time travel, and schema evolution implications.
- [ ] Storage backends cover cloud object stores (S3/ADLS/GCS) and PersistentVolumes through an abstraction.
- [ ] Kubernetes driver/executor/operator responsibilities are separated when relevant.

---

## Phase 3: Functional Design & Performance

### 3.1 Load Functional Context

Read the checklist references (`.github/skills/design-doc/checklist-refs.md`) for sections §3 and §4. Load when present:

- `docs/engineering/best-practices/04-testing.md`
- `docs/engineering/best-practices/08-performance.md`
- `docs/engineering/checklists/04-testing-checklist.md`
- `docs/engineering/checklists/04a-unit-testing-checklist.md`
- `docs/engineering/checklists/04b-integration-testing-checklist.md`
- `docs/engineering/checklists/08-performance-checklist.md`

### 3.2 Generate Functional Test Scenarios

Using the `cloud-native-distributed-systems-architect` agent persona with input from `reliability-test-chaos-engineer`, generate:

- **§3 · Functional Test Scenarios** — Happy-path scenarios, edge cases and error scenarios, integration boundaries, deterministic correctness oracles, and acceptance criteria mapping from the source issue.

Every acceptance criterion from the issue **must** map to at least one test scenario. If an acceptance criterion cannot be mapped, flag it in §9 (Open Questions).

### 3.3 Generate Performance Section

Using the `performance-benchmarking-engineer` persona, generate:

- **§4 · Performance** — Workload profile, latency/throughput/resource targets, memory and allocation budgets, distributed scaling strategy, benchmark methodology, regression gates, and profiler plan.

Calibrate against DeltaSharp workloads such as DataFrame transformations, SQL queries, joins and shuffles, Delta reads/writes, Parquet scans, optimizer rules, and driver/executor scheduling. Reference the performance best-practices and checklist.

---

## Phase 4: Security SME — Security & Threat Model

### 4.1 Load Security Context

Read the section map to confirm Security SME ownership of §5 and §6. Load when present:

- `docs/engineering/best-practices/05-security.md`
- `docs/engineering/best-practices/07-privacy.md`
- `docs/engineering/checklists/05-security-checklist.md`
- `docs/engineering/checklists/07-privacy-checklist.md`
- `docs/engineering/checklists/14-tenant-isolation-checklist.md`

### 4.2 Generate Security Sections

Using the `cloud-native-security-sme` agent persona, generate:

- **§5 · Security** — Authentication & authorization model, data classification table, input validation strategy, tenant isolation approach, object-store/PVC secret handling, and supply-chain considerations.
- **§6 · Threat Model** — Trust boundary diagram (Mermaid), threat actors and attack surfaces, STRIDE analysis table, and mitigations with residual risks.

### 4.3 Security Quality Gates

Validate against the security checklist:

- [ ] Every data element and metadata artifact is classified, including Delta log entries and object-store paths.
- [ ] Encryption in transit and at rest is specified for confidential/restricted data.
- [ ] STRIDE table covers public API, driver/executor RPC, Kubernetes control loops, storage backends, and catalog/connectors.
- [ ] Tenant isolation covers plan analysis, file listing, task scheduling, executor credentials, and read/write paths.
- [ ] Trust boundary diagram includes user process, driver, executor pods, operator, Kubernetes API, object stores, PVCs, and catalogs.

---

## Phase 5: SRE — Observability & Rollout

### 5.1 Load SRE Context

Read the section map to confirm SRE ownership of §7 and §8. Load when present:

- `docs/engineering/best-practices/09-observability.md`
- `docs/engineering/best-practices/10-runtime-environment.md`
- `docs/engineering/checklists/09a-logging-checklist.md`
- `docs/engineering/checklists/09b-metrics-checklist.md`
- `docs/engineering/checklists/09c-distributed-tracing-checklist.md`
- `docs/engineering/checklists/10-runtime-environment-checklist.md`
- `docs/engineering/checklists/13-infrastructure-as-code-checklist.md`

### 5.2 Generate Observability Sections

Using the `cloud-native-site-reliability-engineer` agent persona, generate:

- **§7 · Observability** — Structured logging, metrics, traces, run identifiers, stage/task/shuffle/commit correlation, dashboards, and alerting rules with severity and escalation.

### 5.3 Generate Rollout Sections

Generate:

- **§8 · Rollout & Risk** — Rollout strategy, rollback plan, risk register, dependency sequencing, Kubernetes Operator migration safety, and launch checklist.

### 5.4 SRE Quality Gates

Validate against observability and runtime checklists:

- [ ] Logs include application/job/run ID, tenant ID when applicable, stage/task IDs, table path/version, request/correlation ID, and trace ID.
- [ ] Metrics cover driver scheduling, executor task execution, shuffle, Delta commit latency/conflicts, storage IO, and operator reconciliation.
- [ ] Traces propagate across driver/executor/storage boundaries for distributed actions.
- [ ] Rollback plan includes trigger criteria, expected recovery time, and data/metadata safety for writes.
- [ ] Launch checklist references all relevant engineering checklists.

---

## Phase 6: Assembly & Validation

### 6.1 Merge Agent Outputs

Combine the outputs from Phases 2–5 into a single design document following the `000-template.md` structure. Ensure:

1. **Metadata blockquote** is populated: Status = "Draft", Issue = source issue link, Author = "design-doc skill", Reviewers = agent personas used, Last Updated = today's date.
2. **All 10 sections** are present in order (§1–§10).
3. **§10 · References** links to the source issue, all referenced requirements, ADRs, and engineering docs consulted during generation.

### 6.2 Completeness Check

Verify every template section is populated. For each section:

- **Populated**: Content is present and substantive.
- **Partially populated**: Content exists but is incomplete. Add a `<!-- TODO: [what's missing] -->` comment.
- **Not applicable**: Section is explicitly marked "Not applicable — [reason]."
- **Missing**: Section has no content. Add `<!-- TBD — needs input: [what information is needed] -->`.

Output a completeness summary:

```text
✅ §1 Overview — Complete
✅ §2 Logical Architecture — Complete
⚠️ §3 Functional Test Scenarios — Partial (edge cases need domain input)
✅ §4 Performance — Complete
✅ §5 Security — Complete
✅ §6 Threat Model — Complete
✅ §7 Observability — Complete
✅ §8 Rollout & Risk — Complete
⚠️ §9 Open Questions — 3 questions pending
✅ §10 References — Complete
```

### 6.3 Mermaid Validation

Verify all Mermaid diagrams are syntactically valid:

- Each diagram block starts with ` ```mermaid ` and ends with ` ``` `.
- Diagram type is declared (`graph`, `sequenceDiagram`, `flowchart`, etc.).
- No orphaned nodes or broken references.

### 6.4 Cross-Reference Validation

Verify that:

- All REQ-* IDs mentioned in the doc correspond to real requirements.
- All ADR references link to existing ADRs.
- All checklist references use correct checklist numbers or are clearly marked as DeltaSharp equivalents to be authored.

---

## Phase 7: Git Workflow & PR

### 7.1 Create Worktree Branch

Create a git worktree for the design doc work:

```bash
BRANCH_NAME="design/$(echo '<component-slug>' | tr '[:upper:]' '[:lower:]')"
git worktree add ../deltasharp-design-${BRANCH_NAME##*/} -b "$BRANCH_NAME" main
```

### 7.2 Write the Design Document

Write the assembled design document to the determined file path within the worktree. If a categorized subdirectory is needed, create it first.

### 7.3 Commit and Push

```bash
cd ../deltasharp-design-<slug>
git add docs/engineering/design/
git commit -m "Design: <Component Name> design document

Generates design document for <Component Name> from issue #NNN.
Covers architecture, security, threat model, observability, and rollout.

Refs #NNN

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git push -u origin "$BRANCH_NAME"
```

### 7.4 Open Pull Request

Use GitHub CLI or MCP tools to create a PR:

- **Title**: `Design: <Component Name> design document`
- **Body**: Include a summary of the design doc with links to each section, the completeness summary from Phase 6, and a link to the source issue.
- **Labels**: `design-doc`, `documentation`
- **Linked issue**: Reference the source issue

---

## Phase 8: Review-Fix Loop

### 8.1 Invoke Review-Fix Loop

After the PR is open, invoke the `review-fix-loop` skill to automatically review and improve the design document:

1. The review-fix loop will invoke the `review-pr` skill to review the design doc PR.
2. Design documents require specialist review. Explicitly include all agents that participated in generation: `cloud-native-distributed-systems-architect`, `cloud-native-security-sme`, `cloud-native-site-reliability-engineer`, `performance-benchmarking-engineer`, and any relevant engine/storage/API specialist, in addition to pattern-matched agents.
3. Findings will be evaluated and actionable items will be fixed automatically.
4. The loop repeats until no new actionable findings are discovered or the max round limit is reached.

### 8.2 Final Report

After the review-fix loop completes, output a summary:

```text
📄 Design Document — Complete
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Component:   <Component Name>
File:        docs/engineering/design/<path>
PR:          #NNN
Issue:       #NNN
Status:      Ready for human review

Review Rounds: N
Final Rating:  ⭐⭐⭐⭐⭐

Sections:
  §1 Overview                    ✅
  §2 Logical Architecture        ✅
  §3 Functional Test Scenarios   ✅
  §4 Performance                 ✅
  §5 Security                    ✅
  §6 Threat Model                ✅
  §7 Observability               ✅
  §8 Rollout & Risk              ✅
  §9 Open Questions              ⚠️ 2 questions pending
  §10 References                 ✅
```

---

## Important Notes

- **Issue is the source of truth.** The GitHub issue defines what is being designed. Requirements and feature research docs are supplementary context referenced by the issue.
- **Update mode reconciliation.** If a design doc already exists for the component, do not regenerate from scratch. Instead: read the existing doc, compare section by section, preserve stronger human-authored content, merge new material into incomplete sections, and flag conflicts in §9.
- **Agent persona depth.** Each agent persona should draw on its full knowledge: architecture, native Delta/Parquet storage, query planning/execution, .NET runtime behavior, security, reliability, observability, and rollout.
- **Mermaid diagrams are required.** Sections §2.1 (architecture), §2.3 (data flow), and §6.1 (trust boundaries) must include Mermaid diagrams. Other sections may include diagrams where they aid understanding.
- **TBD is acceptable.** Not every section can be fully populated from an issue alone. Mark gaps with `<!-- TBD -->` comments so human reviewers know what needs input.
- **Design docs are living documents.** They should be updated as implementation reveals new information. The initial generation is a starting point, not a final artifact.
- **Worktree cleanup.** After Phase 8 completes or exits early, remove the worktree: `git worktree remove ../deltasharp-design-<slug>`.
