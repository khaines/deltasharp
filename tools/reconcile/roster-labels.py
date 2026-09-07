#!/usr/bin/env python3
"""Roster ↔ label-taxonomy reconciliation gate for DeltaSharp (STORY-00.6.2, #452).

PR #449 established `docs/planning/label-taxonomy.md` with a *manual* reconciliation
of the persona roster, the `persona:<slug>` GitHub labels, `CODEOWNERS`, and the
feature-request milestone dropdown. This script turns that manual snapshot into a
lightweight, re-runnable gate so the three reconciliations below — plus a validation of the
`.claude/settings.json` permission surface — fail CI instead of silently rotting:

  1. **Roster ↔ persona labels.** Every `.claude/agents/*.md` wrapper (a markdown file
     whose front matter carries a `name:`; other markdown there is ignored) must have a
     matching `persona:<slug>` label and vice-versa. The roster is NON-RECURSIVE — only
     wrappers directly in `.claude/agents/` are roster entries; a `name:`-bearing file
     nested in a subdirectory is an INTEGRITY ERROR (the workflow path filter would still
     ship it), not a silently ignored file. The GitHub 50-character label cap forces
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
`settings-permissions` (:func:`validate_settings`): `.claude/settings.json` must parse, its
`permissions.allow` / `permissions.deny` must be LISTS OF STRINGS, no allow entry may
auto-allow a mutating command (`gh api`, `git push`, `gh pr merge`, ... — including via a
broad `Bash(gh:*)`/`Bash(git:*)`/`Bash(*)`), and the required deny entries must all be
present. It is a policy check on a security-relevant file rather than a reconciliation
between two sources, but it shares the gate's report/exit contract. Run it alone with
`--validate-settings-only`.

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
roster drift.
"""

from __future__ import annotations

import argparse
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
# these is a token-prefix of it (`gh api repos/x` IS a `gh api` call). Wildcards are rejected
# outright.
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

def _read_frontmatter_name(path: str) -> "str | None":
    """Return the `name:` value from a YAML front-matter block, or None if absent."""
    try:
        with open(path, "r", encoding="utf-8") as handle:
            lines = handle.read().splitlines()
    except OSError:
        return None
    if not lines or lines[0].strip() != "---":
        return None
    for line in lines[1:]:
        if line.strip() == "---":
            break
        match = re.match(r"^name:\s*(.+?)\s*$", line)
        if match:
            value = match.group(1).strip()
            if len(value) >= 2 and value[0] in "\"'" and value[-1] == value[0]:
                value = value[1:-1]
            return value
    return None


def read_roster(agents_dir: str) -> "tuple[set[str], list[str]]":
    """Return (persona slugs, integrity problems) from `.claude/agents/*.md`.

    "Is a persona wrapper" is a property of the FILE, not of its name: only a
    `.claude/agents/*.md` file whose front matter carries a `name:` is a roster entry. Plain
    markdown that lives alongside the wrappers (a README, a template) has no front-matter
    `name:` and is ignored rather than being mistaken for a persona slug — there is no
    fall-back to the filename stem, so a stray README.md cannot red the gate.

    The slug is the front-matter `name:` (canonical); the filename stem must match it, and a
    mismatch is reported as an integrity problem so a mislabeled wrapper cannot hide.

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
    first_seen: "dict[str, str]" = {}
    for path in all_paths:
        name = _read_frontmatter_name(path)
        if not name:
            continue  # not a persona wrapper (no front-matter `name:`) — ignore
        relative = os.path.relpath(path, agents_dir)
        if os.sep in relative or (os.altsep and os.altsep in relative):
            # A wrapper below the top level is NOT a roster entry (the roster is flat) but
            # must not vanish silently — the workflow's path filter would still ship it.
            problems.append(
                f"persona wrapper {path!r} is nested; wrappers must sit directly in "
                f"{agents_dir}"
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
        if problems:
            # Everything collected here is a wrapper the roster could not accept (a nested
            # one). Naming it turns "the directory looks empty" into "your wrappers are one
            # level too deep", which is the actual fix.
            was = "wrapper was" if len(problems) == 1 else "wrappers were"
            message += (
                f"; {len(problems)} nested {was} found and ignored: "
                + "; ".join(problems)
            )
        raise FileNotFoundError(message)
    return slugs, problems


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
    argument wildcard are stripped and INTERNAL WHITESPACE IS COLLAPSED, so an entry cannot
    dodge the policy below by padding the command with extra spaces or a tab. Non-``Bash``
    entries (``Read(...)``, ``WebFetch(...)``) return None — this policy is about shell
    commands.
    """
    match = re.match(r"^Bash\((.*)\)$", entry.strip(), re.DOTALL)
    if not match:
        return None
    inner = match.group(1)
    if inner.endswith(":*"):
        inner = inner[: -len(":*")]
    return " ".join(inner.split())


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
      ``Bash(git:*)`` as well as the direct ``Bash(gh api:*)``;
    * no blanket wildcard (``Bash(*)``, ``Bash(:*)``, an empty command);
    * every entry of :data:`REQUIRED_DENY_ENTRIES` is present verbatim.

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
        command = _bash_command(entry)
        if command is None:
            continue  # not a Bash(...) rule
        if command in ("", "*") or command.startswith("*"):
            problems.append(
                f"{path}: allow entry {entry!r} is a blanket wildcard — it auto-allows every "
                f"shell command; list the specific read-only commands instead"
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
        f"{path}: allow/deny are string lists, no mutating command auto-allowed, "
        f"{len(REQUIRED_DENY_ENTRIES)} required deny entries present"
    ]
    return Result("settings-permissions", "fail" if problems else "pass", problems or detail)


def run_checks(args: argparse.Namespace) -> "list[Result]":
    repo = resolve_repo(args.repo, args.offline)
    ref = resolve_ref(args.ref)
    _log(f"Reconciling roster ↔ labels for repo: {repo}")
    if ref:
        _log(f"CODEOWNERS validated at ref: {ref}")
    _log("")

    roster, integrity = read_roster(args.agents_dir)
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
        f"label(s) in {os.path.basename(args.taxonomy)}"
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
            slugs, problems = read_roster(directory)
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

    # (c) Non-persona markdown (no front-matter `name:`) is ignored, not counted as a slug.
    with tempfile.TemporaryDirectory() as tmp:
        _wrapper(tmp, "product-manager.md", "---\nname: product-manager\n---\n")
        _wrapper(tmp, "README.md", "# Agents\n\nThis folder holds persona wrappers.\n")
        _wrapper(tmp, "TEMPLATE.md", "---\ndescription: no name key\n---\n")
        _ok, roster_slugs, roster_problems = _read_roster_safe(tmp)
        check(
            _ok and roster_slugs == {"product-manager"} and roster_problems == [],
            "read_roster ignores non-persona markdown (README/TEMPLATE, no `name:`)",
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
        _wrapper(os.path.join(tmp, ".hidden"), "h.md", "---\nname: hidden-persona\n---\n")
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
        _log(f"settings validation PASSED: {args.settings} permission surface is intact")
        return 0

    try:
        results = run_checks(args)
    except (FileNotFoundError, ValueError) as exc:
        _error(str(exc))
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
