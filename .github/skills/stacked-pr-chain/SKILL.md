---
name: stacked-pr-chain
description: >-
  Executes an ordered set of GitHub issues as a stack of dependent pull requests: each item branches
  off the previous item's tip (item 1 off main), opens a PR scoped to its parent branch, and is driven
  to the PASS bar via the review-fix-loop skill — then left open, not merged. main stays put for the
  whole build. After human sign-off, a bottom-up merge loop rebases each stacked PR onto main and
  requires green CI before each squash-merge. Produces a morning-review stack summary. Use for a
  prioritized backlog of related follow-ups where each item builds on the previous one.
---

# Stacked-PR Chain Skill — Orchestration Instructions

Turn an **ordered list of work items** into a **stack of dependent PRs** that a human reviews and
merges as a set. Each item is implemented and driven to PASS on top of the previous item, but **no
item is merged during the build** — the human signs off first, then a disciplined merge loop lands
them bottom-up. Read all supporting files before beginning:

- `.github/skills/stacked-pr-chain/merge-loop.md` — the rebase-onto-main + CI-gate + cleanup mechanics (Phase 4)
- `.github/skills/implement-work-item/SKILL.md` — implements one issue end-to-end (used per item in Phase 2)
- `.github/skills/review-fix-loop/SKILL.md` — the per-PR quality gate driven to the PASS bar
- `.github/skills/review-pr/rating-rubric.md` — the PASS gate (unanimous 5/5 + red-team `NO-MISS-CERTIFIED`)
- `.github/skills/review-pr/github-review-posting.md` — how each PR's review/report is published
- `.github/copilot-instructions.md` — DeltaSharp canon + the repo bar (build/test both TFMs, format, locked restore, DCO, determinism)

---

## The one hard rule — STACK, don't merge

**Each item branches off the previous item's branch tip; item 1 branches off `main`.** Open each PR
with `--base {parent_branch}` so its diff is scoped to just that item. Drive it to PASS, then **leave
it OPEN and branch the next item off its tip.** `main` does not move for the entire build phase. Only
after explicit human sign-off does Phase 4 merge the stack.

> **AI never merges without sign-off.** The build phase (Phases 1–3) is fully autonomous; the merge
> phase (Phase 4) is **gated on an explicit human go-ahead** and merges one PR at a time, each behind
> green CI.

---

## Configuration

| Parameter | Default | Description |
|-----------|---------|-------------|
| `items` | — | Ordered list of issues (highest priority first). Item N depends on item N−1. |
| `base` | `main` | The base of item 1. The stack root; stays fixed through the build. |
| `pass_bar` | unanimous 5/5 + red-team `NO-MISS-CERTIFIED` | Per-PR gate (`review-fix-loop` / `rating-rubric.md`). "PASS bar" is the target; the "PASS gate" in `rating-rubric.md` is its enforcement — same criterion. |
| `max_worktrees` | 3 | Max concurrent lane worktrees (independent items can build in parallel). |
| `merge` | **manual** | Phase 4 runs **only** after explicit human sign-off. |

---

## Phase 1: Plan the stack

1. **Order the items.** Confirm the dependency order (top-down priority; each item builds on the
   previous). Record it; this order is the stack from bottom (item 1, off `main`) to top.
2. **Resolve the root.** Capture the current `main` SHA (`git rev-parse origin/main`). Every "main
   stays at `{root_sha}`" assertion in the summary references it.
3. **Create the state table** (see "State tracking" below) with one row per item.
4. **Per-item design check.** For each item, confirm a governing design doc / issue scope exists. If
   an item needs a design doc, the per-item `implement-work-item` run will chain `design-doc` first.
5. **Decide lane parallelism.** Items that are *truly independent* (no shared files, no ordering
   dependency) may build concurrently in separate worktrees (≤ `max_worktrees`). Items that build on
   each other are **sequential** (item N's branch does not exist until item N−1's tip does).

---

## Phase 2: Build loop (per item, bottom-up)

For each item in order, do **not** merge — build on the previous tip and leave the PR open.

### 2.1 Branch off the parent tip

- **Item 1:** branch off `main` — `git worktree add ../deltasharp-{item} -b {branch} origin/main`.
- **Item N (N>1):** branch off **item N−1's branch tip** — `git worktree add ../deltasharp-{item} -b
  {branch} {parent_branch}`. The parent branch must already be at its PASS tip.

**Record the fork point now**, while the parent branch is exactly where you fork from: capture
`fork_sha = $(git rev-parse {parent_branch})` (for item 1, `origin/main`) into `stack.fork_sha`.
Phase 4's rebase depends on this recorded value — it **cannot** be reliably reverse-engineered after the
parent squash-merges (see `merge-loop.md` N.1).

Use the branch convention `khaines/{type}-{issue}-{slug}` (author-prefixed; see repo memory). One
worktree per lane (sibling dir), so lanes don't collide.

### 2.2 Implement the item

Run the `implement-work-item` skill for the issue **inside this lane's worktree**, with one override:
the PR is opened against the **parent branch**, not `main` (Phase 2.4). `implement-work-item` writes
the code + tests, runs its build-test-fix loop to green locally, and opens the PR.

### 2.3 Honor the repo bar (before opening the PR)

Every item must pass the full repo bar locally (CI won't run on a stacked PR — see 2.5):

- `dotnet build -c Release -warnaserror` on **both** TFMs; `dotnet test` for the touched project(s);
- `dotnet format --verify-no-changes`; `dotnet restore --locked-mode`;
- **NativeAOT gate** when the write/executor path is touched;
- **determinism ban** — no `Guid.NewGuid` / `DateTime.UtcNow` / `System.Random` in `src/`;
- commits are **DCO-signed** (`-s`) with the Copilot co-author trailer.

### 2.4 Open the PR scoped to its parent

```bash
gh pr create --base {parent_branch} --head {branch} --title "…" --body "…"
```

`--base {parent_branch}` is what keeps the PR's diff scoped to **only this item** (not the whole
stack). Item 1's base is `main`.

### 2.5 Drive to PASS — then STOP (do not merge)

Run `review-fix-loop` on the PR to the PASS bar (unanimous 5/5 across all voting seats + decorrelated
GPT-5.6 Sol red-team `NO-MISS-CERTIFIED` + orchestrator anti-forgery re-verification; every deferral a
verified-OPEN issue; canonical §6.2 report posted per `github-review-posting.md`).

> **CI reality.** CI only runs on `pull_request` targeting `main`, so a stacked PR (base = parent
> branch) gets **no GitHub CI** until Phase 4 rebases it onto `main`. During the build, the **local
> repo bar (2.3) is the CI stand-in** — record it in the PASS evidence as "full local CI-equivalent
> green (GitHub CI deferred — stacked base)".

Record the PASS status + head SHA in the state table. **Leave the PR open.** Branch the next item off
this tip (2.1).

---

## Phase 3: Deferral tracking & stack summary

1. **Every deferral is a filed, verified issue.** Before marking any item PASS, audit its in-code
   fail-closed guards for untracked deferrals; file a GitHub tracking issue for each deferred finding
   and verify it (`gh issue view {n}` → open, scope-matching). An un-tracked deferral blocks that
   item's PASS (`rating-rubric.md` → PASS gate).
2. **Write the morning-review summary** — a top-level artifact the human reads first. One row per
   item: `issue → branch → PR# → PASS status → base`, plus the fixed `main @ {root_sha}` line and the
   list of OPEN deferral issues. Persist it to the session files dir (not committed).

Example:

```
Stack root: main @ 3bbb864 (unchanged all build)
| item | issue | branch                         | PR  | base                    | PASS |
|------|-------|--------------------------------|-----|-------------------------|------|
| 1    | #497  | khaines/feat-497-…             | 531 | main                    | ✅ 5/5 + red-team NO-MISS |
| 2    | #496  | khaines/feat-496-…             | 532 | khaines/feat-497-…      | ✅ 5/5 + red-team NO-MISS |
Deferrals (all OPEN): #541 #542 #545 …
```

---

## Phase 4: Merge loop (GATED on human sign-off)

**Do not start Phase 4 until the human explicitly signs off.** Then merge **bottom-up**, one PR at a
time, each behind green CI, following [`merge-loop.md`](merge-loop.md). The essential shape:

1. **Merge item 1** (base is already `main`) once its 14/14 CI is green.
2. **For each subsequent item:** when its parent merges, GitHub **auto-retargets** its base to `main`
   (the parent branch is deleted). **Rebase it onto `main`** using the **recorded `fork_sha`**
   (`merge-loop.md` N.1 — never derive the fork point from `origin/main..{branch}`, which under
   squash-only merges replays the parent's orphaned commits and conflicts), verify the
   `origin/main..HEAD` diff is scoped, build+test locally, `--force-with-lease` push, then **wait for
   14/14 CI green** before squash-merge.
3. Repeat to the top of the stack.

> **The rebased-stacked-PR rule:** a stacked PR that has been rebased onto `main` **must pass all CI
> before it merges** — never merge a rebased stack member on local evidence alone.

After the last merge: feature issues auto-close on squash; confirm all deferral issues remain OPEN;
run a final integration build+test on merged `main`.

---

## State tracking

Track the stack in the session SQLite DB so recovery after an interruption is deterministic:

```sql
CREATE TABLE IF NOT EXISTS stack (
  item      INTEGER PRIMARY KEY,   -- 1-based stack position (bottom = 1)
  issue     TEXT,                  -- "#497"
  branch    TEXT,
  pr        INTEGER,
  base      TEXT,                  -- parent branch (or "main" for item 1)
  fork_sha  TEXT,                  -- parent-branch tip at fork time (captured in Phase 2.1; the Phase 4 rebase base)
  head_sha  TEXT,
  pass_status TEXT,                -- "" | "PASS: unanimous 5/5 + red-team NO-MISS + …"
  merge_state TEXT DEFAULT 'open'  -- open | rebased | ci-green | merged
);
```

On resume: `SELECT * FROM stack ORDER BY item` reconstructs where the build/merge loop left off. The
lowest `item` whose `merge_state != 'merged'` is the next merge target.

---

## Cleanup

- After each item merges: remove its lane worktree (`git worktree remove`) and delete its local branch
  (the remote is auto-deleted on squash-merge).
- Council seats run C7 in **isolated `/tmp` clones outside the shared worktree** (never mutate a lane's
  worktree). After each red-team round, verify + remove any scratch dirs/patch files it left in a
  worktree.
- At the end: only the `main` worktree remains; no leaked `/tmp` clones or `.rfl-*` scratch.

---

## Recovery (resume after interruption)

The computer or session can die mid-stack. To resume:

1. `git worktree list` + `git branch --list 'khaines/*'` + `gh pr list --state open` — enumerate live
   lanes and PRs.
2. Reconcile against the `stack` state table (rebuild it from `gh pr list`/`gh pr view` if the DB was
   lost). For each item read its PR's CI + review state (`gh pr checks`, `gh pr view --json reviews`).
3. Continue the **build loop** from the lowest item without a PASS, or — if all PASS and the human has
   signed off — continue the **merge loop** from the lowest un-merged item.

---

## Important notes

- **The stack is the unit of review.** The human reviews the whole stack at once; keep each PR's diff
  minimal (scoped by `--base parent`) so the set reads cleanly.
- **Never merge without sign-off, and never merge a rebased member on red CI.** These two rules are the
  point of the skill.
- **Independent lanes are cheap; dependent lanes are strict.** Only parallelize items with no shared
  files and no ordering dependency; a dependent item's branch cannot exist before its parent's tip.
- **`strict:false` branch protection** means a PR need not be up-to-date with `main` to merge, so
  independent rebased lanes merge cleanly onto a newer `main` — but the PASS gate + green CI still
  apply per PR.
