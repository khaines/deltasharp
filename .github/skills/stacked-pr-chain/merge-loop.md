# Stacked-PR merge loop — rebase-onto-main mechanics

> Phase 4 of `stacked-pr-chain`. Runs **only after explicit human sign-off**. Merges the stack
> **bottom-up**, one PR at a time, each behind **green CI**. This file is the precise, tested
> mechanics; `SKILL.md` Phase 4 is the summary.

## Why a rebase is needed at all

During the build, each stacked PR targets its **parent branch** (`--base {parent_branch}`), so its diff is
scoped to one item and CI does **not** run (CI is `pull_request` → `main` only). To merge, each member
must be retargeted to `main` and rebased so its commits sit directly on `main` — then CI runs and the
scoped squash-merge is clean.

Two GitHub behaviors make this work:

- **Auto-retarget on parent merge.** When you squash-merge a PR, its branch is deleted
  (`delete_branch_on_merge`), and GitHub **automatically retargets any open PR whose base was that
  branch** to the merged PR's base (`main`). So a stacked child's base becomes `main` for free — but
  its *commits* still include the parent's (now-merged) commits until you rebase.
- **`strict:false`.** A PR need not be up-to-date with `main` to merge. Independent lanes rebased onto
  an older `main` still merge cleanly onto a newer `main`. (The PASS gate + green CI still apply.)

## The loop

### Step 0 — merge item 1 (already based on `main`)

Item 1's base is `main` from the start, so CI has been running on it.

```bash
gh pr checks {pr1}                          # want all 14 green (see "CI checks" below)
gh pr view {pr1} --json mergeStateStatus    # want "CLEAN"
gh pr merge {pr1} --squash                  # branch auto-deleted; children auto-retarget to main
```

### Step N — for each subsequent member (bottom-up)

After the parent merges and GitHub retargets this PR's base to `main`:

#### N.1 — Use the recorded fork point (THE critical gotcha)

The rebase must replay **only this PR's own commits** onto `main`. **Do not derive the fork point from
`origin/main..{branch}`.** Under this repo's **squash-only** merges, the parent's squash-merge creates a
*new* commit, so the parent's original commits are **not** ancestors of `main` and still live in this
stacked child's history. `git rev-list --reverse origin/main..{branch} | head -1` therefore returns a
*lower stack item's* commit (not this item's first commit), its parent resolves to the **stack root**,
and `git rebase --onto origin/main {stack_root}` replays the parent's already-merged commits → add/add
conflict + parent-file leak. (Empirically reproduced: a stacked child after its parent squash-merged
conflicts on the parent's files.)

**Instead, use the fork point recorded when this branch was created.** Phase 2.1 stored `stack.fork_sha`
= the parent branch's tip at fork time; that value is immune to both squash-orphaning and parent
advancement:

```bash
git fetch origin
forkpoint={stack.fork_sha for this item}          # captured at Phase 2.1 branch creation
```

If `fork_sha` was not recorded but the **local** parent branch still exists (it survives locally even
after the remote is deleted on merge), recompute the true divergence commit instead — never from
`origin/main`:

```bash
forkpoint=$(git merge-base {parent_branch} {branch})   # child's real fork commit
```

Guard before rebasing — the fork point MUST be an ancestor of this branch (by construction it is), and
`forkpoint..{branch}` must equal exactly this item's own commit count:

```bash
git merge-base --is-ancestor "$forkpoint" {branch} \
  && echo "ok: fork point is an ancestor" \
  || echo "STOP — not an ancestor; do NOT rebase. Recover fork_sha from Phase 2.1 / reflog first."
git rev-list --count "$forkpoint..{branch}"            # sanity: == this item's own commit count
```

> **Why not the *current* parent tip?** A parent branch can **advance** after a child forks from it
> (e.g. it gains commits during its own council fix rounds), so its *final* tip is not where the child
> forked. In batch-2, `git merge-base --is-ancestor <#525-final-tip> <#529>` was **false** for exactly
> this reason — #529 forked from an *earlier* #525 tip. The recorded `fork_sha` is the only value immune
> to both parent-advancement and squash-orphaning; do not reverse-engineer it after the fact.

#### N.2 — Rebase onto `main`

```bash
git rebase --onto origin/main "$forkpoint" {branch}
```

This replays exactly `"$forkpoint"..{branch}` — this branch's own commits — onto `origin/main`. (Should
any parent commit still appear during replay, git drops it as *already upstream*; N.3 confirms the
result is scoped.)

#### N.3 — Verify the diff is scoped (before pushing)

The `origin/main..HEAD` diff must contain **only this item's files** — if the parent's changes leak in,
the fork point was wrong: `git rebase --abort`, recover the recorded `fork_sha` (do **not** blindly
re-run a derivation), and redo N.1.

```bash
git diff --stat origin/main..HEAD          # expect ONLY this PR's files
```

#### N.4 — Rebuild + retest locally, then force-push

```bash
dotnet build -c Release -warnaserror       # both TFMs
dotnet test tests/{touched_project}
dotnet format --verify-no-changes
git push --force-with-lease                # never plain --force
```

#### N.5 — Wait for CI, then merge (the hard gate)

Now that the PR is based on `main`, CI runs. **A rebased stack member must pass all CI before it
merges — never merge on local evidence alone.**

```bash
gh pr checks {prN} --watch                  # all 14 green
gh pr view {prN} --json mergeStateStatus    # want "CLEAN"
gh pr merge {prN} --squash                  # branch auto-deleted; next child auto-retargets
```

Repeat N.1–N.5 up the stack.

## CI checks (this repo)

14 required checks = **11 `pass`** contexts + **3 `Analyze (actions / csharp / python)`** (CodeQL) —
this is the same gate `review-fix-loop` §6.6 lists by name as a 7-context fallback allowlist (Build,
Test, Format, Packaging, Integration Tests, Operator Tests, Benchmark Gate), at full 14-context fidelity.
`gh pr checks {n}` lists them; `mergeStateStatus: CLEAN` confirms mergeability. `BLOCKED` usually means
an unresolved review thread (`required_conversation_resolution`) or missing green CI — resolve threads
(see `../review-pr/github-review-posting.md`) and re-check.

## Branch protection recap

- **Squash-only** (`allow_squash_merge`, no merge-commit/rebase-merge); `delete_branch_on_merge`.
- **`enforce_admins=true`** — no direct pushes to `main`, even for the owner; everything via PR.
- **`required_conversation_resolution=true`** — every inline review thread must be resolved before
  merge; the §6.2 report is a `COMMENT` review (no thread) so it never blocks.
- **`required_approving_review_count=0`, `strict:false`** — no human approval count required and no
  up-to-date-with-main requirement; the **quality gate is the council PASS + green CI**, not a GitHub
  approval.

## Post-merge

- Feature issues referenced by `Closes/Fixes #N` **auto-close** on squash-merge.
- Confirm every **deferral** issue is still **OPEN** (`gh issue view {n}`).
- Remove the merged lane's worktree + local branch:
  ```bash
  git worktree remove ../deltasharp-{item}
  git branch -D {branch}
  ```
- After the final merge, run a **final integration** build + test on merged `main` to confirm the
  whole stack composes:
  ```bash
  git checkout main && git pull
  dotnet build -c Release -warnaserror
  dotnet test tests/{primary_project}
  ```
- Clean up any leaked `/tmp` council clones and `.rfl-*` scratch.
