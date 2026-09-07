# Label taxonomy and code ownership

> **Scope:** How DeltaSharp issues and pull requests are labeled, how those labels
> map to the planning hierarchy and the persona roster, who owns each part of the
> source tree (`CODEOWNERS`), and how required review is gated today. Companion to
> the [workstream plan](README.md), which stays the source of truth for the roster.
>
> **Status:** active. Reconciled with the repository on 2026-07-04 and enforced by the
> [`reconcile`](../../.github/workflows/reconcile.yml) CI gate.

This document activates STORY-00.6.2. It records the label families the project
uses, the rule that keeps `persona:<slug>` labels in step with the roster, the
`CODEOWNERS` subsystem map, the branch-protection exception for the current
single-maintainer phase, and the CI gate that reconciles all of these against live
GitHub state (see [Automated reconciliation gate (CI)](#automated-reconciliation-gate-ci)).

## Label families

DeltaSharp uses five label families. The first four are project-specific and are
created and kept current **manually** by maintainers (via the `gh` CLI) as the
[workstream plan](README.md) is materialized into issues; there is no automated
plan-sync tool in the repository yet. The fifth is the default GitHub triage set.
As of this snapshot, 56 labels exist in total.

| Family | Pattern | Count | Meaning | Applied by |
|---|---|---|---|---|
| Work-item type | `epic`, `feature`, `story` | 3 | Which plan level an issue represents | Maintainers (triage) |
| Epic | `epic:NN` (`epic:00`–`epic:13`) | 14 | The owning epic (EPIC-00 … EPIC-13) | Maintainers (triage) |
| Persona | `persona:<slug>` | 25 | The implementer persona that owns the work | Maintainers (triage) |
| Size | `size:<XS\|S\|M\|L\|XL>` | 5 | Relative T-shirt estimate | Triage |
| GitHub defaults | `bug`, `enhancement`, `documentation`, … | 9 | Standard triage signals | Issue forms + triage |

The nine GitHub defaults are `bug`, `enhancement`, `documentation`,
`good first issue`, `help wanted`, `question`, `duplicate`, `invalid`, and
`wontfix`. The bug and feature issue forms in
[`.github/ISSUE_TEMPLATE/`](../../.github/ISSUE_TEMPLATE/) auto-apply `bug` and
`enhancement` respectively; maintainers add the project-specific labels during
triage.

### Work-item type, epic, and size

- `epic` / `feature` / `story` mark which level of the Epic → Feature → Story
  hierarchy an issue represents.
- `epic:NN` ties an issue to its epic (and, transitively, its roadmap milestone).
  The 14 values `epic:00`–`epic:13` match the [epic index](README.md#epic-index).
- `size:XS`, `size:S`, `size:M`, `size:L`, and `size:XL` carry the relative
  estimate. STORY-00.6.2 names `size:S`; the full scale is XS–XL, matching the
  plan's sizing convention.

### Persona labels and the roster

- A persona label is the exact roster slug prefixed with `persona:`. The roster
  **list** — 25 roles — is maintained in the [workstream plan](README.md) (the
  source of truth for *which* personas exist); the **canonical per-role specs**
  live in [`docs/persona/agents/README.md`](../persona/agents/README.md) and are
  mirrored by the wrappers in `.claude/agents/*.md`.
- **One slug is truncated in label form.** GitHub caps label names at 50
  characters. `persona:dotnet-vectorized-columnar-compute-engineer` is 51
  characters, so its label drops the redundant trailing `-engineer`:
  `persona:dotnet-vectorized-columnar-compute`. The full slug is always preserved
  in issue bodies and in this taxonomy. Every other slug fits; the next longest,
  `persona:cloud-native-distributed-systems-architect`, is exactly 50 characters.

#### The persona label set (committed source for offline reconciliation)

The complete `persona:` label set is committed below so the
[reconcile gate](#automated-reconciliation-gate-ci) has an offline source of truth for it:
`--offline` reconciles the roster against **this** list (catching a persona added to or
removed from the roster with no network), while remote mode additionally diffs the roster
against the **live** GitHub labels (catching drift introduced in the GitHub UI). Each entry
is the label **exactly as stored on GitHub** — i.e. with the one 50-character truncation
above applied — sorted, one per line. Keep it in step with the roster whenever a persona is
added, removed, or renamed (see
[Keeping persona labels in step with the roster](#keeping-persona-labels-in-step-with-the-roster)).

<!-- BEGIN persona-labels (parsed by tools/reconcile/roster-labels.py — keep sorted, one per line) -->
```text
persona:catalog-metastore-engineer
persona:cloud-native-distributed-systems-architect
persona:cloud-native-security-sme
persona:cloud-native-site-reliability-engineer
persona:compute-storage-finops-engineer
persona:data-platform-connectors-engineer
persona:delta-storage-format-engineer
persona:developer-experience-api-engineer
persona:developer-relations-community-lead
persona:dotnet-distributed-execution-engineer
persona:dotnet-framework-runtime-engineer
persona:dotnet-library-platform-engineer
persona:dotnet-runtime-performance-engineer
persona:dotnet-vectorized-columnar-compute
persona:kubernetes-operator-controller-engineer
persona:performance-benchmarking-engineer
persona:privacy-compliance-grc-lead
persona:product-manager
persona:program-manager
persona:query-execution-engine-engineer
persona:query-optimizer-scheduler-engineer
persona:reliability-test-chaos-engineer
persona:sql-language-frontend-engineer
persona:structured-streaming-engine-engineer
persona:technical-writer
```
<!-- END persona-labels -->

## Story-to-issue label mapping

When a plan item becomes a GitHub issue, labels are applied as follows (from the
[GitHub mapping](README.md#github-mapping-implemented) in the plan):

| Plan level | GitHub artifact | Labels |
|---|---|---|
| Roadmap milestone (M1–M4, v1.0) | Milestone | — |
| Epic | tracking issue (Features are sub-issues) | `epic`, `epic:NN` |
| Feature | issue (Stories are sub-issues) | `feature`, `epic:NN`, `persona:<slug>` |
| Story | issue | `story`, `epic:NN`, `persona:<slug>`, `size:<XS…XL>` |

This satisfies STORY-00.6.2 AC2: a story converted to an issue can carry a
`persona:<slug>` label, a `size:<…>` label, and the epic/feature type labels.

## Keeping persona labels in step with the roster

The roster in [`docs/planning/README.md`](README.md) (and the canonical specs it
links) is the **source of truth**. STORY-00.6.2 AC3 requires roster changes to be
tracked there. When a persona is added, removed, or renamed:

1. Update the roster in [`docs/planning/README.md`](README.md) and the canonical
   spec under `docs/persona/agents/` (plus its `.claude/agents/` wrapper).
2. Add or remove the matching `persona:<slug>` label, honoring the 50-character
   truncation rule above.
3. Update the committed
   [persona label set](#the-persona-label-set-committed-source-for-offline-reconciliation)
   so the offline reconciliation stays accurate.
4. Update this document's counts and the reconciliation snapshot below.

### Reconciliation snapshot (verified)

As of 2026-09-06, with the roster source moved to `.claude/agents/`, the roster
and the `persona:` labels still agree exactly: 25 roster slugs, 25 labels, with
the single documented truncation. Reconcile at any time (no temporary files
needed):

```bash
comm -3 \
  <(grep -l '^name:' .claude/agents/*.md | xargs -n1 sed -n 's/^name: *//p' | sort) \
  <(gh label list --limit 200 \
      | awk -F'\t' '$1 ~ /^persona:/ { sub(/^persona:/,"",$1); print $1 }' | sort)
```

The left half reads the frontmatter `name:` from the top-level wrappers,
approximately as the gate does (it does not enforce the frontmatter fence or
strip quotes); `python3 tools/reconcile/roster-labels.py --offline` is
authoritative.

The only expected difference is the truncated slug — roster-only
`dotnet-vectorized-columnar-compute-engineer` versus label-only
`dotnet-vectorized-columnar-compute`. Any other difference means a label and the
roster have drifted and must be reconciled. This `comm -3` snapshot is now also
enforced automatically — see
[Automated reconciliation gate (CI)](#automated-reconciliation-gate-ci) below.

## Automated reconciliation gate (CI)

The manual snapshots above are backed by a lightweight CI gate so the roster, the
labels, `CODEOWNERS`, and the milestone dropdown cannot silently drift apart
(STORY-00.6.2, #452). The gate is the stdlib-only script
[`tools/reconcile/roster-labels.py`](../../tools/reconcile/roster-labels.py), run by
the [`reconcile`](../../.github/workflows/reconcile.yml) workflow. It fails when any
of three reconciliations or the two local validations (`settings-permissions`,
`command-skill-frontmatter`) breaks:

1. **Roster ↔ persona labels.** Every `.claude/agents/*.md` wrapper must have a
   matching `persona:<slug>` label and vice-versa. `.claude/agents/` holds persona
   wrappers only: the gate counts every `*.md` there that carries a frontmatter
   `name:` and ignores markdown with no front-matter fence at all (e.g. a README);
   a file that *opens* a `---` fence but declares no `name:` is a persona candidate
   and fails. Wrappers must sit directly in `.claude/agents/`; a `name:`-bearing
   file in a subdirectory fails the gate, and hidden (dot-prefixed) subdirectories
   are scanned too, so a wrapper hidden under one cannot slip past. Wrapper front
   matter is held to a policy as well, because Claude Code reads parts of it as
   runtime configuration: `permissionMode` may only be `default` or `plan`
   (`bypassPermissions`/`acceptEdits`/`dontAsk`/`auto` skip the permission prompt),
   `hooks`, `mcpServers`, `isolation` (it runs `git worktree add` unprompted), and
   `env` are rejected by name, and any other unrecognised key fails — a wrapper is
   a persona brief, not an execution-config surface. The front matter is read
   strictly: a quoted key, a space before the colon, a flow mapping, a wholly
   indented mapping, or a duplicate key is reported rather than skipped, because a
   real YAML parser reads the dangerous key out of every one of those spellings.
   The one 50-character truncation
   (`persona:dotnet-vectorized-columnar-compute`) is accepted **only because this
   document records it**: the gate reads both the full slug and the standalone
   truncated label out of this file, so an *undocumented* truncation still fails.
   The roster is reconciled against both the committed
   [persona label set](#the-persona-label-set-committed-source-for-offline-reconciliation)
   above (so `--offline` still catches a removed/added persona) and the **live**
   GitHub labels (so a label renamed/deleted in the UI is caught too). Any other
   roster/label difference fails.
2. **`CODEOWNERS` parse errors.** `GET /repos/<repo>/codeowners/errors` (the same check
   in [Code ownership](#code-ownership-codeowners)) must return an empty `errors`
   array; a syntax or unknown-owner error fails. In CI the check is pinned to the PR
   head/merge commit (`--ref "${{ github.sha }}"`) so a PR that *breaks* `CODEOWNERS`
   fails the PR gate rather than only being caught after merge; locally it falls back to
   the default branch.
3. **Milestone dropdown.** The `id: milestone` options in
   [`feature_request.yml`](../../.github/ISSUE_TEMPLATE/feature_request.yml) must equal
   the live **open** GitHub milestones plus the documented `Unsure / needs triage`
   sentinel. A stale/renamed option, or a live milestone missing from the dropdown,
   fails.
4. **`settings-permissions`** (a validation, not a reconciliation). The file
   must parse as JSON, `permissions` must be an object, and `allow`/`deny` must
   be lists of plain strings (the list type is checked before iteration). It holds
   `.claude/settings.json` to a positive allowlist: the top level may contain only
   `$schema`, `permissions`, and inert keys (`model`, `cleanupPeriodDays`,
   `includeCoAuthoredBy`, `attribution`, `outputStyle`, `language`,
   `spinnerTipsEnabled`), and `permissions` only `allow`, `deny`, and `defaultMode`.
   Any other key fails, with a specific message for each executable-valued one
   (`hooks`, `env`, `apiKeyHelper`, `statusLine`, the AWS/GCP credential and proxy
   helpers) — those run commands as soon as the branch is checked out, `apiKeyHelper`
   at CLI startup before any tool call. Inside `allow`, the enumerated mutating
   `git`/`gh` prefixes (compared case-insensitively), any wildcard, and any tool-wide
   `Bash` grant fail; a `defaultMode` other than `default`/`plan` fails
   (`bypassPermissions`/`dontAsk` skip every prompt, `acceptEdits` auto-approves
   file writes); the destructive-spelling deny entries must be present. Allow
   entries outside that enumeration are *not* policed and still need human review.
   Run it alone with `--validate-settings-only`.
5. **`command-skill-frontmatter`** (also a validation). Slash commands
   (`.claude/commands/**/*.md`) and skill manifests (`.claude/skills/**/SKILL.md`)
   are the third front matter Claude Code reads as runtime configuration: a tracked
   command file carrying `allowed-tools: Bash(<cmd>:*)` *runs* that command with no
   permission prompt, which neither of the policies above can see. The gate reads
   both with the same strict reader (an unparseable fence fails) and rejects
   `allowed-tools`, `permissionMode`, `hooks`, `mcpServers`, `env`, and `isolation`
   by name, plus any key outside `description`/`argument-hint`/`model` (skills may
   also carry `name`). `.claude/commands/` is optional; `.claude/skills/` is not — a
   skills tree that yields zero manifests fails the check, and the passing report
   states how many files were scanned.

**When it runs.** On pull requests and pushes to `main` that touch the governance
files (roster, `.claude/commands/`, `.claude/skills/`, `CODEOWNERS`, the feature
form, `.claude/settings.json`, this document, the script, or the workflow), on a
weekly schedule, and on demand — the schedule catches drift introduced
GitHub-side (a label or milestone renamed in the UI), which no file change would
otherwise trigger. It uses a least-privilege read-only token
(`permissions: contents: read`; the default token suffices for labels, milestones, and
CODEOWNERS on a public repo) and pins its one action by commit SHA.

**Run it locally.**

```bash
python3 tools/reconcile/roster-labels.py            # remote checks need `gh` authenticated
python3 tools/reconcile/roster-labels.py --selftest # prove the gate's logic, no network
```

The live GitHub-API checks degrade gracefully: without `gh` (or a token) they are
**skipped** with a warning so local dev works offline, while the script still parses the
roster and the milestone dropdown **and reconciles the roster against the committed persona
label set above** (so `--offline` still catches roster↔label drift). CI passes
`--require-remote`, so a remote check that could not run there is a hard failure — reported
as a **remote outage (exit 2), distinct from drift**, so a `gh`/API outage is never
miscounted as roster drift. Exit codes: `0` reconciled, `1` drift detected, `2` usage/data
error **or** a required remote check could not run (a remote outage under `--require-remote`).

**Promotion to a required check.** The gate is not a branch-protection required check
today; like `coverage` and the supply-chain scans it can be promoted post-merge once it
has first run on `main` (the same pattern documented in
[supply-chain-security.md](../engineering/design/supply-chain-security.md)).

## Code ownership (CODEOWNERS)

[`.github/CODEOWNERS`](../../.github/CODEOWNERS) maps every path to an owner. In
the single-maintainer phase the owner is `@khaines` everywhere, so any path that
matches a known subsystem resolves to a maintainer (STORY-00.6.2 AC1). Each source
rule carries a trailing `# persona:<slug>` note naming the specialist that takes
the area over as maintainers join.

| Path grouping (see [`CODEOWNERS`](../../.github/CODEOWNERS) for exact patterns) | Owner | Intended persona |
|---|---|---|
| `/src/DeltaSharp.Abstractions/**` | `@khaines` | `dotnet-vectorized-columnar-compute-engineer` |
| `/src/DeltaSharp.Core/**` | `@khaines` | `developer-experience-api-engineer` |
| `/src/DeltaSharp.Core/Analysis/**` | `@khaines` | `query-execution-engine-engineer` |
| `/src/DeltaSharp.Core/Optimization/**` | `@khaines` | `query-optimizer-scheduler-engineer` |
| `/src/DeltaSharp.Core/Plans/**` | `@khaines` | `query-execution-engine-engineer` |
| `/src/DeltaSharp.Core/Sql/**` | `@khaines` | `sql-language-frontend-engineer` |
| `/src/DeltaSharp.Engine/**` | `@khaines` | `dotnet-vectorized-columnar-compute-engineer` |
| `/src/DeltaSharp.Engine/Memory/**` | `@khaines` | `dotnet-runtime-performance-engineer` |
| `/src/DeltaSharp.Engine/Execution/**` | `@khaines` | `query-execution-engine-engineer` |
| `/src/DeltaSharp.Executor/**` | `@khaines` | `dotnet-distributed-execution-engineer` |
| `/tests/**` | `@khaines` | mirrors the `src/` area under test |
| Build files (`*.sln`, `Directory.*.props`, `global.json`, `**/*.csproj`) | `@khaines` | `dotnet-library-platform-engineer` |
| `/.github/workflows/`, `/.github/dependabot.yml` | `@khaines` | `cloud-native-site-reliability-engineer` |
| Governance and docs (`/docs/**`, front-door files) | `@khaines` | maintainer |

Validate the file with GitHub's own parser:

```bash
# Default branch (drop ?ref= for the local check):
gh api repos/khaines/deltasharp/codeowners/errors --jq '.errors | length'
# A specific commit/PR head — what CI validates so a PR that breaks CODEOWNERS is caught:
gh api "repos/khaines/deltasharp/codeowners/errors?ref=$GITHUB_SHA" --jq '.errors | length'
# 0 means no syntax or ownership errors at that ref.
```

The [reconcile gate](#automated-reconciliation-gate-ci) runs this same check in CI, pinned
to the PR head/merge commit (`--ref`), and fails on any non-empty `errors` array.

## Branch protection and required review

STORY-00.6.2 AC4 asks that a protected-file change require code-owner review, or
that the exception be documented. **The exception applies today.** Branch
protection on `main` (verified 2026-07-04) is:

- `required_pull_request_reviews.require_code_owner_reviews`: **false**.
- `required_pull_request_reviews.required_approving_review_count`: **0**.
- Required status checks: `build-test-format`, `dco`, `coverage`, `sca`, `secret-scan`,
  and `sbom` — the FEAT-00.3 supply-chain scans are now required checks too (#461; see
  [supply-chain-security.md](../engineering/design/supply-chain-security.md)).
  `dependency-review` stays advisory PR-only.
- `enforce_admins`: true; `required_linear_history`: true;
  `required_conversation_resolution`: true; force-pushes and deletions blocked.

With a single maintainer, requiring code-owner review would route every PR to
`@khaines` — the sole owner — and block that maintainer's own changes without
adding a second reviewer. Required code-owner review is therefore intentionally
**off**. `CODEOWNERS` still auto-requests review from the owner, and the required
status checks above gate every merge.

**Exit from the exception.** When a second maintainer joins, enable
`require_code_owner_reviews` and set `required_approving_review_count` to at least
1, then replace `@khaines` in `CODEOWNERS` with the area owners recorded in the
trailing persona notes. Changing branch-protection settings is out of scope for
this story and is not done here.

## References

- [Workstream plan and persona roster](README.md)
- [Persona agent roster](../persona/agents/README.md)
- [`.github/CODEOWNERS`](../../.github/CODEOWNERS)
- [Issue and pull-request templates](../../.github/ISSUE_TEMPLATE/)
- [Security Policy](../../SECURITY.md)
