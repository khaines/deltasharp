# Stacked-PR merge loop — rebase-onto-main mechanics

> Phase 4 of `stacked-pr-chain`. Runs **only after explicit human sign-off**. Merges the stack
> **bottom-up**, one PR at a time, each behind **green CI**. This file is the precise, tested
> mechanics; `SKILL.md` Phase 4 is the summary.

## Why a rebase is needed at all

During the build, each stacked PR targets its **parent branch** (`--base <parent>`), so its diff is
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
gh pr checks <pr1>                          # want all 14 green (see "CI checks" below)
gh pr view <pr1> --json mergeStateStatus    # want "CLEAN"
gh pr merge <pr1> --squash                  # branch auto-deleted; children auto-retarget to main
```

### Step N — for each subsequent member (bottom-up)

After the parent merges and GitHub retargets this PR's base to `main`:

#### N.1 — Identify the fork point (THE critical gotcha)

The rebase must replay **only this PR's own commits** onto `main`. The correct fork point is the
**parent of this branch's _first_ commit** — **NOT** the old parent-branch tip (which may have advanced
past where you forked, or been rewritten).

```bash
git fetch origin
firstcommit=$(git rev-list --reverse origin/main..<branch> | head -1)   # this branch's 1st commit
forkpoint=$(git rev-parse "$firstcommit"~1)                             # its parent = the fork point
```

Sanity-check that the old parent tip is *not* assumed to be an ancestor (it often isn't after the
parent squash-merged):

```bash
git merge-base --is-ancestor <old-parent-tip> <branch> && echo "ancestor" || echo "NOT an ancestor — use \$forkpoint, not the old tip"
```

> **Real example (batch-2):** #529's fork point was `3add740` (#525's *first* commit's parent), **not**
> `7bc0c96` (#525's tip). `--is-ancestor` proved `7bc0c96` was not an ancestor of #529's branch.

#### N.2 — Rebase onto `main`

```bash
git rebase --onto origin/main "$forkpoint" <branch>
```

This replays exactly this branch's commits onto `origin/main`, dropping the parent's already-merged
commits.

#### N.3 — Verify the diff is scoped (before pushing)

The `origin/main..HEAD` diff must contain **only this item's files** — if the parent's changes leak in,
the fork point was wrong; reset and redo N.1.

```bash
git diff --stat origin/main..HEAD          # expect ONLY this PR's files
```

#### N.4 — Rebuild + retest locally, then force-push

```bash
dotnet build -c Release -warnaserror       # both TFMs
dotnet test tests/<touched project>
dotnet format --verify-no-changes
git push --force-with-lease                # never plain --force
```

#### N.5 — Wait for CI, then merge (the hard gate)

Now that the PR is based on `main`, CI runs. **A rebased stack member must pass all CI before it
merges — never merge on local evidence alone.**

```bash
gh pr checks <prN> --watch                  # all 14 green
gh pr view <prN> --json mergeStateStatus    # want "CLEAN"
gh pr merge <prN> --squash                  # branch auto-deleted; next child auto-retargets
```

Repeat N.1–N.5 up the stack.

## CI checks (this repo)

14 required checks = **11 `pass`** contexts + **3 `Analyze (actions / csharp / python)`** (CodeQL).
`gh pr checks <n>` lists them; `mergeStateStatus: CLEAN` confirms mergeability. `BLOCKED` usually means
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
- Confirm every **deferral** issue is still **OPEN** (`gh issue view <n>`).
- Remove the merged lane's worktree + local branch:
  ```bash
  git worktree remove ../deltasharp-<item>
  git branch -D <branch>
  ```
- After the final merge, run a **final integration** build + test on merged `main` to confirm the
  whole stack composes:
  ```bash
  git checkout main && git pull
  dotnet build -c Release -warnaserror
  dotnet test tests/<primary project>
  ```
- Clean up any leaked `/tmp` council clones and `.rfl-*` scratch.
