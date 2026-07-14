# Posting council output as a GitHub code review

> Single source of truth for **how** the `review-pr` and `review-fix-loop` skills publish their
> output to a GitHub PR. Both skills reference this file; keep the mechanics here and the policy
> (what to post, gate rules) in `SKILL.md` / `rating-rubric.md`.

Council output is posted as **GitHub PR reviews** — a review *summary* (the body) plus **inline
`file:line` review comments** for each anchored finding — not as loose issue comments. This puts
findings in the **Files changed** tab against the exact code and puts the summary in the **Reviews**
timeline. Two hard constraints from this repo's branch protection shape how we do it.

## Constraint 1 — self-authored PRs can only post a `COMMENT` review

We open PRs as the repo owner (`@khaines`). **GitHub forbids approving or requesting changes on your
own PR** — `event: APPROVE` / `REQUEST_CHANGES` returns HTTP 422
(`"Can not approve your own pull request"` / `"Can not request changes on your own pull request"`).

Therefore, when the acting identity is the PR author (the normal case here), **always submit the
review with `event: COMMENT`**. The rating and the APPROVE/REQUEST_CHANGES *recommendation* from
`rating-rubric.md` are conveyed **in the review body text**, not in the GitHub review state. A green
"Approved" check is not available on a self-authored PR and is **not** part of the PASS gate anyway
(the gate is unanimous 5/5 + red-team `NO-MISS-CERTIFIED`, per `rating-rubric.md`).

> Determine authorship before choosing the event:
> ```bash
> me=$(gh api user --jq .login)
> author=$(gh pr view {pr} --json author --jq .author.login)
> # if [ "$me" = "$author" ]; then event=COMMENT; else event per review-pr §9.1; fi
> ```

## Constraint 2 — inline comments create threads that gate merge

`required_conversation_resolution=true` on `main`: **every unresolved review thread blocks merge.**
Each inline review comment starts a thread, so posting inline findings adds a resolution obligation.

- A review **summary body** (no inline comments) does **not** create a thread → never blocks. The
  final `review-fix-loop` §6.2 report is posted this way.
- Inline `file:line` findings **do** create threads. Post them only for **actionable** findings the
  loop will fix, then **resolve each thread** once the fix lands (see "Resolving threads"). At PASS,
  every actionable finding is fixed and every thread it opened is resolved, so conversation-
  resolution is satisfied. **Info** findings stay in the body (no thread, nothing to resolve).

## Posting a review with inline comments (one API call)

Submit the body + all inline comments as one `POST …/reviews` call. **Build the JSON payload with
`jq`, never by splicing finding/EVIDENCE text into a raw heredoc** — EVIDENCE quotes attacker-controlled
diff snippets, so an unescaped `"`, `\`, or newline would `422` the call, and a crafted snippet could
**inject JSON structure** (close the string early to forge an extra comment or rewrite the body).
`jq --arg` / `--rawfile` always encodes. Use a scratch file **outside the worktree** and delete it after.

```bash
owner=khaines; repo=deltasharp; pr={pr}; head={HEAD_SHA}
f=$(mktemp -t rfl-review.XXXXXX.json)

# Build the comments[] array — one jq object per anchored finding, so every body (quotes, backticks,
# newlines, diff-quoted EVIDENCE) is JSON-encoded. Read each finding body from a file via --rawfile.
comments=$(jq -n '[]')
comments=$(printf '%s' "$comments" | jq \
  --arg path "src/DeltaSharp.Storage/Delta/Foo.cs" --argjson line 42 --arg side "RIGHT" \
  --rawfile body finding-1.md '. + [{path:$path, line:$line, side:$side, body:$body}]')
# …repeat per finding; for a multi-line range add --argjson start_line 10 and include it in the object…

# Assemble the review; the summary body is encoded via --rawfile too.
jq -n --arg commit "$head" --rawfile body review-summary.md --argjson comments "$comments" \
  '{event:"COMMENT", commit_id:$commit, body:$body, comments:$comments}' > "$f"

gh api -X POST "/repos/$owner/$repo/pulls/$pr/reviews" --input "$f"
rm -f "$f"
```

`event: COMMENT` is required for self-authored PRs (Constraint 1). **Scrub secrets from EVIDENCE before
posting** — EVIDENCE may quote captured command output; never publish a token/credential in a review
body or comment (the C7 no-ambient-credentials isolation in `red-team.md` is the first line of defense).

- **`commit_id`** — pin to the HEAD SHA reviewed so comments anchor to the intended revision.
- **`line` / `side`** — `line` is the line number in the file at that side of the diff; `side:
  RIGHT` = the added/head side (the usual choice), `LEFT` = the base/removed side. For a multi-line
  finding add `start_line` (+ optional `start_side`); `line` is then the **end** line.
- **`event: COMMENT`** — required for self-authored PRs (Constraint 1). The body carries the rating
  and recommendation.

### Line-anchoring & fallback (avoid 422s)

An inline comment's line **must be part of the PR diff** for that side, or the API rejects the whole
review with `422 "… line must be part of the diff"`. The batch `comments[]` accepts only diff-line
anchors — `path` + `line` (or the deprecated `position`) + `side`, plus `start_line`/`start_side` for
a range — there is **no `subject_type`** on this endpoint. Before posting, keep only findings whose
`file:line` is in the diff hunks (`gh pr diff {pr}` / the `pulls/{pr}/files` patch). For a finding
that is **not** on a diff line:

1. **Review body (always works).** Move it into the review body under a "Findings not anchored to a
   diff line" subsection — no thread, so no resolution obligation.
2. **File-level comment (optional).** For a whole-file finding, post a *separate* standalone comment
   via the **Create a review comment** endpoint, which does accept `subject_type`:
   `gh api -X POST /repos/{owner}/{repo}/pulls/{pr}/comments -f path=… -f subject_type=file -f
   commit_id={HEAD_SHA} -f body=…`. This opens its own resolvable thread (resolve it like any other).

Never let one un-anchorable finding fail the whole review call — filter to the body/file-level path
first.

## Resolving threads after a fix (`review-fix-loop` §5.3)

When a fixer resolves an inline finding, reply to the thread with what was fixed and **resolve** it,
so the thread does not block merge. Threads are a GraphQL concept:

```bash
# 1) list open threads (thread id + first comment's path/body to match the finding)
gh api graphql -f query='
  query($owner:String!,$repo:String!,$pr:Int!){
    repository(owner:$owner,name:$repo){ pullRequest(number:$pr){
      reviewThreads(first:100){ nodes{ id isResolved
        comments(first:1){ nodes{ path body } } } } } } }' \
  -F owner=khaines -F repo=deltasharp -F pr={pr}

# 2) reply to the matched thread — pure GraphQL, using the THREAD id from step 1 (no comment id needed).
#    Pass the body with -f (single-quoted) so quotes/backticks/$(...) are NOT shell-expanded or forgeable.
gh api graphql -f query='
  mutation($tid:ID!,$body:String!){
    addPullRequestReviewThreadReply(input:{pullRequestReviewThreadId:$tid, body:$body}){
      comment{ id } } }' \
  -f tid={thread_id} -f body='Fixed in {fix_sha}: what changed.'

# 3) resolve the thread
gh api graphql -f query='
  mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}){ thread{ isResolved } } }' \
  -f id={thread_id}
```

At loop termination, assert **zero unresolved threads remain for fixed findings** — an unresolved
thread on an addressed finding is an un-actioned merge blocker, not PASS.

## Posting the final report as a review (`review-fix-loop` §6.3)

The §6.2 progression report is a **summary body** with no new inline threads (its per-finding detail
is narrative, and the actionable findings already have resolved inline threads from the rounds).
Post it as a **`COMMENT` review** so it lands in the Reviews timeline:

```bash
gh pr review {pr} --comment --body-file {report_file}
```

`gh pr review --comment` is allowed on a self-authored PR (Constraint 1) and creates no thread. Keep
the `<!-- deltasharp-rfl-report pr=… head=… rounds=… -->` HTML marker in the body for the §6.4
verification query, which now reads `.reviews[]` (see below).

## Verifying the posted review (`review-fix-loop` §6.4)

Because the report is now a **review**, verify against `reviews`, not issue `comments`:

```bash
gh pr view {pr} --json reviews --jq '.reviews[]
  | select(.body | contains("<!-- deltasharp-rfl-report pr={pr} head={final_head_sha} rounds={final_round_count} "))
  | { state, submittedAt, body_preview: (.body[0:300]) }'
```

If missing/invalid, re-render to the project-relative `.rfl-report-{pr}.md` (gitignored), repost with
`gh pr review {pr} --comment --body-file .rfl-report-{pr}.md`, re-verify, then delete it — matching the
`review-fix-loop` §6.4 re-render convention.

## Avoiding duplicate comments (`review-pr` §9.3)

Before posting a round's inline comments, list existing review comments
(`gh api /repos/{owner}/{repo}/pulls/{pr}/comments`) and skip any finding whose `(path, line, body
gist)` already has a comment from a prior round. Post a follow-up reply on the existing thread
instead of a new top-level comment when the same finding recurs.
