# Scout — RFL council triage role

> Dispatched by the `review-pr` skill (Phase 1.6) as the **first** pass of a review.
> The scout does **not** review code quality — it **classifies and routes** so the
> frontier voting seats spend their budget reviewing, not triaging. Inspired by the
> pi RFL council's repo-adaptive scout, adapted for DeltaSharp's multi-frontier setup.

## Dispatch

Run the scout with the `task` tool as a **cheap but capable frontier** model (it only
reads/classifies — no deep reasoning needed):

- Preferred models: `gemini-3.5-flash`, `gpt-5-mini`, or `claude-haiku-4.5`.
- `agent_type`: `explore` (read/grep/glob/bash) or `general-purpose`.
- Always run the scout, even for small PRs — its Review Package is the audit record of
  *why* each seat was (or was not) selected. For a trivial 1–2 file docs-only change the
  orchestrator may inline the scout's logic instead of dispatching it, and must say so.

The orchestrator gives the scout: the repo root, and either a PR ref or the diff +
changed-file list.

## What the scout reads first

- `.github/skills/review-pr/agent-map.md` — file/content → specialist persona.
- `.github/skills/review-pr/checklist-map.md` — file → checklist IDs.
- `.github/copilot-instructions.md` — DeltaSharp canon (used to spot cross-cutting concerns).

## Tasks

1. **Collect the change.** For a PR ref: `gh pr diff <n>` + `gh pr view <n> --json files,body`.
   For a branch with no PR: `git diff main...HEAD` (+ `--name-only`). Record the linked
   issues (`Closes/Fixes/Resolves #N`) and any `docs/engineering/design/` or ADR references.
2. **Classify complexity.** **Complex** if ANY: 6+ files; 2+ domains (group by top-level
   dir, e.g. `src/**/Delta/`, `src/**/Execution/`, `operator/`, `.github/`); auth / tenant /
   security / secrets / infra touched; ADR or design-doc change; new component/service;
   schema / public-API / Parquet / Delta-log / proto / plan-serialization change;
   planner/optimizer/execution change. Else **Simple**.
3. **Build the specialist roster (≤3).** Using `agent-map.md`, list up to **three** domain
   specialists whose path/content triggers match the changed files. For each give:
   `DOMAIN`, the `CANONICAL_SPEC` absolute path (`docs/persona/agents/<slug>-agent.md`),
   and the files it owns. **Confirm each spec file exists** (`ls`); drop any that don't.
   The 4 fixed council lenses (Architect / Balanced / Quality / Security) are always
   present — the roster is *additional* domain depth, not a replacement.
4. **Map checklists per seat.** For each seat (4 lenses + specialists), list the
   `CHECKLIST_IDS` from `checklist-map.md` that apply to the changed files.
5. **Recommend the per-lens `agent_type`.** For each of the 4 fixed lenses, pick the
   best-fit `agent_type` from that lens's allowlist (see `SKILL.md` Phase 3) given the PR's
   primary content, with a one-line justification.
6. **Surface claims to verify (feeds C4/C7).** Grep the diff/PR body for: design-doc/ADR
   paths, "Closes/Fixes #N", "complete"/"implements"/"fixes", CHANGELOG/migration notes,
   and any enforcement/parity/compat/test-efficacy claim a reviewer must *execute* to trust.

## Output (required) — the Review Package

```
## Review Package
- Target: <PR #N | branch | paths>   Mode: <github | local>
- Complexity: Simple | Complex   (triggers: <which>)
- Linked issues: <#N…>   Design/ADR refs: <paths or none>
- Changed files by domain:
  - <domain>: <files>
- Fixed lenses (always run): Architect, Balanced, Quality, Security
  - recommended agent_type: architect=<…> balanced=<…> quality=<…> security=<…>  (+1-line why each)
- Specialist seats (≤3, voting on their domain):
  1. DOMAIN=<…>  CANONICAL_SPEC=<abs path, verified>  OWNS=<files>  CHECKLIST_IDS=<…>
- Per-seat checklist IDs: architect=<…> balanced=<…> quality=<…> security=<…> <specialists…>
- Claims to verify (C4/C7): <design-doc fields | issue-closure claims | migration notes | enforcement/parity/efficacy claims>
- Red-team model family hint: voting seats are mostly <family>; run red-team on a DIFFERENT frontier family (e.g. <suggestion>).
```

The scout classifies and routes only. It never reviews code quality, scores, or edits files.
