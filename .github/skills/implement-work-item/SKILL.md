---
name: implement-work-item
description: >-
  Implements a GitHub issue end-to-end: verifies a design document exists, dispatches specialist
  agent personas to write code and tests, runs a build-test-fix loop until all unit and functional
  tests pass locally, opens a PR, and runs a custom review-fix-test loop (via `review-pr`) that
  re-validates tests after each fix round. Chains into the design-doc skill when no design document exists.
---

# Implement Work Item Skill — Orchestration Instructions

Implement a GitHub issue by writing production code and tests, validating the build, and opening a reviewed pull request. Read all supporting files before beginning:

- `.github/skills/implement-work-item/service-agent-map.md` — maps service labels to agent personas and checklists
- `.github/skills/implement-work-item/build-test-config.md` — build and test configuration for the .NET toolchain
- `.github/skills/review-pr/agent-map.md` — file-pattern-to-agent mapping (used by fallback rules)
- `.github/skills/review-fix-loop/SKILL.md` — overview of review-fix loop; consensus and round structure referenced in Phase 8
- `.github/skills/review-fix-loop/dismissal-rules.md` — dismissal and consensus rules applied in Phase 8 Step B
- `.github/skills/review-pr/SKILL.md` — the PR review skill invoked during Phase 8
- `.github/skills/design-doc/SKILL.md` — the design-doc skill invoked in Phase 2 if no design doc exists

---

## Configuration

| Parameter | Default | Description |
|-----------|---------|-------------|
| `max_build_attempts` | 5 | Maximum build-fix iterations before aborting |
| `max_test_fix_rounds` | 5 | Maximum test-fix iterations per test stage before aborting |
| `require_design_doc` | true | Abort if no design doc exists and auto-generation is declined |
| `auto_generate_design_doc` | true | Invoke the `design-doc` skill when no design doc is found |

---

## Phase 1: Issue Analysis & Readiness Check

### 1.1 Identify the Source Issue

Determine the GitHub issue that defines the work to be implemented:

- **Issue number provided explicitly**: Use GitHub issue tools or `gh issue view` to fetch the issue title, body, labels, linked issues, comments, and sub-issues.
- **Issue URL provided**: Extract the owner, repo, and issue number from the URL, then fetch as above.
- **No issue provided**: Ask the user to provide an issue number or URL. This skill requires a source issue to proceed.

### 1.2 Validate Issue Readiness

Check that the issue meets the readiness criteria defined in `docs/engineering/issue-hierarchy-and-lifecycle.md` when present:

1. **Required labels**: The issue should have at least one `type:*` label, one `priority:*` label, and one `service:*` label. If missing, infer cautiously from title/body and document the assumption.
2. **Acceptance criteria**: The issue body must contain testable acceptance criteria (checkboxes, numbered criteria, or a clearly marked section). If absent, stop and request criteria.
3. **Size**: Check the issue's size field or `size:*` label. Reject `XL` (> 5 days) — issues must be decomposed into smaller units per the issue-hierarchy rules. S, M, and L are acceptable.
4. **Not blocked**: Check for `status:blocked` label and `blocked-by #NNN` references in the issue body. If dependencies are unresolved, abort with a list of blocking issues.
5. **Not a duplicate**: Check if any open PRs already reference this issue. If a PR exists, warn and ask whether to proceed.

### 1.3 Extract Implementation Context

From the issue, extract:

1. **Component name**: Derive from the issue title or `service:*` label.
2. **Service domain**: The `service:*` label value (for example, `dataframe`, `delta-log`, `optimizer`, `operator`).
3. **Layer**: API, logical/analyzer/optimizer, physical/execution, Delta/storage, connectors, Kubernetes/operator, runtime, or cross-cutting governance.
4. **Issue type**: The `type:*` label (feature, bug, spike, tech-debt).
5. **Acceptance criteria**: Parse into a structured list for test mapping in Phase 5.

### 1.4 Output Readiness Summary

```text
🔍 Issue Readiness Check
━━━━━━━━━━━━━━━━━━━━━━━━
Issue:       #NNN — [Title]
Type:        [type:feature | type:bug | ...]
Priority:    [priority:p0–p3]
Service:     [service:XXX]
Layer:       [API | Planning | Execution | Storage | Operator | Cross-cutting]
Size:        [S | M | L]
Status:      ✅ Ready | ❌ [reason]

Acceptance Criteria: N items found
Blocking Issues:     None | [list]
Existing PRs:        None | [list]
```

---

## Phase 2: Design Doc Verification

### 2.1 Search for Existing Design Doc

Search `docs/engineering/design/` for a design document matching the component:

- Search by component name: `docs/engineering/design/**/*<component-slug>*.md`
- Search by service name: `docs/engineering/design/**/*<service>*.md`
- Check the issue body for a direct link to a design doc.

### 2.2 Handle Design Doc State

- **Design doc exists**: Read the full document. Extract the following sections for use as implementation context:
  - §2 Logical Architecture — overall structure, boundaries, data flow. This section contains subsections for plan/data model, API surface, dependencies, storage backends, and tenant considerations.
  - §3 Functional Test Scenarios — happy-path, edge cases, integration boundaries, acceptance criteria mapping.
  - §5 Security — authentication/authorization model, data classification, input validation, tenant isolation approach.
  - §7 Observability — logging, metrics, and tracing instrumentation plans.
  - §10 References — ADRs, requirements, and checklists.

  Note: Subsection numbering within each top-level section may vary between design documents. Locate content by topic heading rather than relying on specific subsection numbers.

- **No design doc exists and `auto_generate_design_doc` is true**: Invoke the `design-doc` skill with the same issue number. Wait for the skill to complete (it will generate the doc, open a PR, and run review-fix-loop). After completion:
  1. Wait for the design-doc PR to merge into `main`. If the PR is still open, ask the user to merge it before proceeding.
  2. Pull `main` to ensure the implementation branch includes the design document.
  3. Read the merged design doc.
  If the design-doc skill fails or aborts, output a diagnostic message and abort the implement-work-item skill. Do not proceed to Phase 3 without a design document.

- **No design doc exists and auto-generation is declined**: Abort with a message explaining that a design doc is required. Suggest running the `design-doc` skill first.

### 2.3 Output Design Context Summary

```text
📐 Design Document Context
━━━━━━━━━━━━━━━━━━━━━━━━━━
Design Doc:     docs/engineering/design/<path>
Status:         Approved | In Review | Draft
Architecture:   [brief summary from §2]
API Surface:    [N public APIs / contracts from §2]
Data Model:     [N plan/data/storage entities from §2]
Test Scenarios: [N scenarios from §3]
Security:       [key concern from §5]
```

---

## Phase 3: Agent Selection & Context Assembly

### 3.1 Determine Implementation Agent

Read the service-agent-map (`.github/skills/implement-work-item/service-agent-map.md`) and match the issue's `service:*` label to determine:

- **Primary implementation agent**: The specialist persona that writes the code.
- **Secondary agents**: Additional personas needed for multi-domain work.
- **Language/framework**: C#/.NET, Markdown, YAML, or a combination.
- **Build and test commands**: From the build-test-config.

If the `service:*` label does not match any entry in the service-agent-map, apply the **Fallback Rules** defined in `service-agent-map.md` §Fallback Rules.

### 3.2 Load Engineering Context

Load the following reference documents for the implementation agent:

- **Always load**:
  - The design document (from Phase 2).
  - .NET coding standards: `docs/engineering/checklists/03a-dotnet-coding-standards.md`.
  - Testing checklists: `docs/engineering/checklists/04a-unit-testing-checklist.md`, `docs/engineering/checklists/04b-integration-testing-checklist.md`.
  - Security checklist: `docs/engineering/checklists/05-security-checklist.md`.
  - Tenant isolation checklist: `docs/engineering/checklists/14-tenant-isolation-checklist.md`.

- **Load if applicable** (based on service-agent-map checklists and design doc):
  - Spark API parity: `docs/engineering/checklists/15-spark-api-parity-checklist.md`.
  - Catalyst planning: `docs/engineering/checklists/16-catalyst-planning-checklist.md`.
  - Delta storage format: `docs/engineering/checklists/17-delta-storage-format-checklist.md`.
  - Kubernetes Operator: `docs/engineering/checklists/18-kubernetes-operator-checklist.md`.
  - Connectors: `docs/engineering/checklists/19-data-source-connectors-checklist.md`.
  - Developer experience: `docs/engineering/checklists/20-developer-experience-api-checklist.md`.
  - Distributed correctness: `docs/engineering/checklists/21-distributed-correctness-checklist.md`.
  - Benchmark gates: `docs/engineering/checklists/22-benchmark-regression-gates-checklist.md`.
  - Logging, metrics, tracing: `docs/engineering/checklists/09a-logging-checklist.md`, `09b`, `09c` if §7 defines them.
  - ADRs referenced in the design doc's §10.

- **Load existing code context**: If the service directory already exists (for example, `src/DeltaSharp.Core/`, `src/DeltaSharp.Storage/`, `src/DeltaSharp.Operator/`), read existing files to understand patterns, namespaces, imports, and conventions already established.

Referenced checklist files may not exist yet in the greenfield repository. If a referenced file is absent, note that it is an intended DeltaSharp checklist and proceed using the design doc and canon instructions as the source of truth.

### 3.3 Output Agent Selection Summary

```text
🤖 Agent Selection
━━━━━━━━━━━━━━━━━━
Primary Agent:   [agent persona name]
Language:        [C#/.NET | Markdown | YAML | Mixed]
Restore:         dotnet restore
Build Command:   dotnet build -c Release
Test Command:    dotnet test
Format Gate:     dotnet format --verify-no-changes
Checklists:      [list of loaded checklists]
ADRs:            [list of loaded ADRs]
Existing Code:   [N files in component directory | New component]
```

---

## Phase 4: Implementation

### 4.1 Create Worktree Branch

Create a git worktree for the implementation work:

```bash
SERVICE="<service-label>"
SLUG="<issue-slug>"
BRANCH_NAME="feat/${SERVICE}/${SLUG}"
git worktree add ../deltasharp-impl-${SLUG} -b "$BRANCH_NAME" main
```

### 4.2 Dispatch Implementation Agent

Using the selected agent persona, implement the issue by translating the design document into working code. Provide the agent with:

1. **The full design document** — this is the primary technical specification.
2. **The issue body** — for acceptance criteria and business context.
3. **The coding standards checklist** — for C#/.NET conventions.
4. **Existing code** — for pattern consistency with the component.

The agent must:

- **Follow the design doc's architecture** (§2): create files in the correct projects/directories, respect component boundaries, and preserve API → logical plan → optimizer → physical plan → execution separation.
- **Implement the API surface** from §2: define public types, methods, options, and errors with Spark parity unless an intentional .NET deviation is documented.
- **Implement the plan/data/storage model** from §2: define immutable plan nodes, schema objects, Delta log models, Parquet metadata, connector abstractions, or CRD objects as specified.
- **Preserve lazy/eager semantics**: transformations only build plans; actions trigger execution.
- **Add structured logging** per §7 and checklist 09a: include run/job ID, tenant ID when applicable, stage/task/table identifiers, request/correlation ID, and trace ID.
- **Add metrics** per §7 and checklist 09b: instrument driver, executor, storage IO, Delta commits, shuffles, and operator reconcile loops when relevant.
- **Add tracing** per §7 and checklist 09c: create spans for significant operations and propagate trace context across driver/executor/storage boundaries.
- **Implement tenant isolation** per §5 and checklist 14: enforce tenant context in analysis, scheduling, storage credentials, file listing, and read/write paths as applicable.
- **Follow security requirements** from §5: implement auth/authz, input validation, data classification, secret handling, and supply-chain constraints as specified.

### 4.3 Multi-Domain Implementation

If the issue spans multiple domains:

1. **Contract first**: Implement shared interfaces and public contracts first (API shape, plan nodes, connector contracts, CRD schema).
2. **Core engine/storage second**: Implement internals behind those contracts.
3. **Operational or documentation layers last**: Update operator manifests, samples, docs, or runbooks after contracts compile.
4. Each agent works in the same worktree branch.

---

## Phase 5: Test Writing

### 5.1 Dispatch Test Writing

Using the same implementation agent or `reliability-test-chaos-engineer` for cross-cutting correctness, write tests:

**Unit tests** (from design doc §3 — happy-path and edge-case scenarios):

- One or more tests per happy-path scenario defined in §3.
- One or more tests per edge case / error scenario defined in §3.
- Tests for lazy transformations and eager actions: transformations must not execute; actions must trigger the engine.
- Tests for immutable plan node rewrites and semantic equivalence of optimizer rules.
- Follow the unit testing checklist (04a): scope boundaries, isolation, assertions, coverage targets.

**Functional / integration tests** (from design doc §3 — integration boundaries and acceptance criteria):

- Tests for integration boundaries defined in §3.
- Use local fakes, emulators, test containers, or Kubernetes test harnesses for storage backends and operator behavior where feasible.
- Cover Delta ACID commit conflicts, time travel, schema evolution, Parquet scan/write behavior, shuffle boundaries, connector contracts, and driver/executor interactions when applicable.

**Acceptance criteria mapping**:

- Every acceptance criterion from the issue must map to at least one test.
- If a criterion cannot be tested locally (for example, requires a managed cloud object store or full Kubernetes cluster), flag it in the completion report and add a targeted comment in the test plan explaining the external validation needed.

### 5.2 Test File Placement

Follow project conventions:

- Source projects live under `src/<ProjectName>/`.
- Test projects live under `tests/<ProjectName>.Tests/`, one `.Tests` project per `src` project.
- Unit tests should live in the relevant `.Tests` project with names matching the component under test.
- Integration tests may live in the same `.Tests` project with categories/traits or in a dedicated integration test project if the solution already has that convention.

---

## Phase 6: Build-Test-Fix Loop

This phase ensures the implementation compiles and all tests pass before opening a PR. Read the build-test-config (`.github/skills/implement-work-item/build-test-config.md`) for commands and configuration.

### 6.1 Restore and Build

Run restore and build:

```bash
cd ../deltasharp-impl-<slug>
dotnet restore
dotnet build -c Release
```

- **Build succeeds** → proceed to §6.2.
- **Build fails** → enter build-fix loop.

**Build-Fix Loop**:

1. Parse the restore/build error output. Extract: file/project, line, error code, error message, error type.
2. Dispatch the implementation agent with the error output and instructions to fix only the compilation errors.
3. Re-run `dotnet restore` if project/package files changed; otherwise re-run `dotnet build -c Release`.
4. Repeat up to `max_build_attempts` (default: 5).
5. If the build still fails after max attempts → **abort** with a diagnostic report.

### 6.2 Format / Analyzer Gate

Run:

```bash
dotnet format --verify-no-changes
```

- **Passes** → proceed to §6.3.
- **Fails** → run `dotnet format`, inspect changes, rebuild, then re-run `dotnet format --verify-no-changes`.

### 6.3 Tests

Run the full test command:

```bash
dotnet test
```

Use focused filters while iterating on a failure:

```bash
dotnet test --filter "FullyQualifiedName~X"
dotnet test --filter "Name=Y"
```

- **All tests pass** → proceed to §6.4.
- **Tests fail** → enter test-fix loop.

**Test-Fix Loop**:

1. Parse the test output. Extract: test name, file, line, assertion, expected value, actual value, exception, error message.
2. Classify each failure:
   - **Implementation bug**: The test expectation is correct but the code does not satisfy it. Fix the implementation.
   - **Test bug**: The test expectation is incorrect. Fix the test.
   - **Heuristic**: If the test was written from the design doc's acceptance criteria or Spark/Delta semantics, prefer fixing the implementation. If the test contradicts the design doc, prefer fixing the test.
3. Dispatch the implementation agent with the failure details and classification.
4. Re-run the build, format gate if needed, focused tests, then the full `dotnet test`.
5. Repeat up to `max_test_fix_rounds` (default: 5).
6. If tests still fail after max rounds → **abort** with diagnostic report.

### 6.4 Output Build-Test Summary

```text
✅ Build & Test — All Green
━━━━━━━━━━━━━━━━━━━━━━━━━━━
Restore:         ✅ Passed
Build:           ✅ Passed (attempt N)
Format Gate:     ✅ Passed
Tests:           ✅ N passed (fix rounds: N)
Total Duration:  ~N minutes
```

---

## Phase 7: Git Workflow & PR

### 7.1 Commit Changes

Stage and commit all changes with a descriptive message:

```bash
cd ../deltasharp-impl-<slug>
git add -A
git commit -m "feat(<service>): <brief summary from issue title>

Implements <component> per design doc and issue #NNN.
Includes tests for all acceptance criteria.

- <key implementation detail 1>
- <key implementation detail 2>
- <key implementation detail 3>

Refs #NNN

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### 7.2 Push and Open PR

Push the branch and create a PR:

```bash
git push -u origin "$BRANCH_NAME"
```

Create the PR with:

- **Title**: `feat(<service>): <summary from issue title>`
- **Body**: include summary, key changes, acceptance criteria checklist, test coverage, design document link, and build/test results.
- **Labels**: Copy labels from the source issue.
- **Linked issue**: `Refs #NNN`.

---

## Phase 8: Review-Fix-Test Loop

This phase implements a custom review-fix-test loop rather than delegating directly to the `review-fix-loop` skill, because test re-validation must occur between each fix-and-review cycle.

### 8.1 Review-Fix-Test Cycle

Repeat the following cycle until termination (§8.2):

**Step A — Review**: Invoke the `review-pr` skill on the PR. Collect findings.

**Step B — Evaluate**: Apply the review-fix-loop's dismissal and consensus rules (from `.github/skills/review-fix-loop/dismissal-rules.md`) to filter actionable findings. If no actionable findings remain AND the unconditional 5/5 PASS gate is met, terminate with success. If no actionable findings remain but the PR is below 5/5, it is a STOP (never a PASS).

**Step C — Fix**: Dispatch the appropriate agent persona(s) to fix actionable findings. Agent selection follows the same routing used in Phase 3.

**Step D — Build-Test Validation**: Re-run the full build-test sequence from Phase 6:

1. Run `dotnet restore` if project or package files changed.
2. Run `dotnet build -c Release`.
3. Run `dotnet format --verify-no-changes`.
4. Run `dotnet test`; use focused filters only for iteration.
5. If a review fix causes a regression, revert the problematic fix or repair the implementation without weakening tests.

**Step E — Commit & Push**: Commit all fixes with message:

```text
Review fixes (Round N): Address N findings

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

Push to the PR branch.

**Step F — Loop**: Return to Step A for the next review round.

### 8.2 Termination

The review-fix-test loop terminates when:

- Unconditional 5/5 PASS gate achieved AND all tests pass → **success**.
- No new actionable findings but below 5/5 → **stop** (never a PASS/merge-ready).
- Max rounds reached (use the review-fix-loop's `max_rounds` default of 5) → **stop** with current state (tests must still be green).
- A review fix cannot be reconciled with passing tests after `max_review_regression_fixes` attempts → **stop** and revert the problematic fix.

---

## Phase 9: Completion Report

After all phases complete, output a final summary:

```text
🚀 Implementation Complete
━━━━━━━━━━━━━━━━━━━━━━━━━
Issue:       #NNN — [Title]
Component:   [Component Name]
Service:     [service:XXX]
Branch:      feat/<service>/<slug>
PR:          #NNN
Design Doc:  docs/engineering/design/<path>

Implementation:
  Files created:     N
  Files modified:    N
  Lines added:       N

Validation:
  Restore:           ✅ Passed
  Build:             ✅ Passed
  Format gate:       ✅ Passed
  Tests:             N passed, 0 failed
  Build-fix rounds:  N
  Test-fix rounds:   N

Review:
  Review rounds:     N
  Final rating:      ⭐⭐⭐⭐⭐

Acceptance Criteria:
  ✅ Criterion 1
  ✅ Criterion 2
  ✅ Criterion 3
  ⚠️ Criterion 4 — requires external validation

Commits:
  abc1234 — feat(dataframe): implement select plan node
  def5678 — Review fixes (Round 1): Address N findings
```

### 9.1 Worktree Cleanup

After the completion report, remove the worktree:

```bash
git worktree remove ../deltasharp-impl-<slug>
```

---

## Important Notes

- **Design doc is required.** Every implementation must have a design document. If none exists, the skill invokes the `design-doc` skill to generate one.
- **XL issues are rejected.** Issues sized XL (> 5 days) must be decomposed into smaller units per the issue-hierarchy rules.
- **Tests must pass before PR.** Restore, build, format gate, and tests are hard gates. No PR is opened until they are green locally.
- **Review fixes must not break tests.** Phase 8 re-runs build + tests after every fix round. If a fix introduces a regression, the fix is reverted rather than the test being weakened.
- **Prefer fixing implementation over tests.** When a test fails, assume the test expectation (derived from design doc, acceptance criteria, Spark semantics, or Delta protocol semantics) is correct.
- **Multi-domain sequencing.** Establish contracts first, then internals, then operational/docs layers. Never implement consumers against assumed contracts.
- **Issue lifecycle.** The skill links the PR to the issue via `Refs #NNN` so the issue can be tracked through the PR lifecycle.
- **Worktree cleanup.** Always remove the worktree after completion or abort.
- **Abort is acceptable.** If the build or tests cannot be fixed within configured max attempts, abort cleanly with a diagnostic report rather than opening a broken PR.
