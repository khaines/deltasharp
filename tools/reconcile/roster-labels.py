#!/usr/bin/env python3
"""Roster ↔ label-taxonomy reconciliation gate for DeltaSharp (STORY-00.6.2, #452).

PR #449 established `docs/planning/label-taxonomy.md` with a *manual* reconciliation
of the persona roster, the `persona:<slug>` GitHub labels, `CODEOWNERS`, and the
feature-request milestone dropdown. This script turns that manual snapshot into a
lightweight, re-runnable gate so the three sources of drift below fail CI instead of
silently rotting:

  1. **Roster ↔ persona labels.** Every `.github/agents/*.agent.md` wrapper must have a
     matching `persona:<slug>` label and vice-versa. The GitHub 50-character label cap forces
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

Exit codes: 0 = reconciled (no drift), 1 = drift detected (a check FAILED), 2 = usage/data
error, OR a required remote check could not run (`--require-remote` with `gh`/the GitHub API
unavailable) — a remote outage, reported distinctly from drift so an outage never reads as
roster drift.
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import re
import shutil
import subprocess
import sys
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

DEFAULT_AGENTS_DIR = os.path.join(".github", "agents")
DEFAULT_FEATURE_FORM = os.path.join(".github", "ISSUE_TEMPLATE", "feature_request.yml")
DEFAULT_TAXONOMY = os.path.join("docs", "planning", "label-taxonomy.md")

# label-taxonomy.md carries a delimited, machine-readable list of the persona labels so the
# gate has a COMMITTED source of truth for them. This lets `--offline` reconcile the roster
# against the labels the project *documents* (catching a removed/added agent) without the
# live GitHub label set; remote mode additionally diffs the roster against the LIVE labels.
PERSONA_LABELS_BEGIN = "<!-- BEGIN persona-labels"
PERSONA_LABELS_END = "<!-- END persona-labels"


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
    """Return (persona slugs, integrity problems) from `.github/agents/*.agent.md`.

    The slug is the front-matter `name:` (canonical); the filename stem must match it, and a
    mismatch is reported as an integrity problem so a mislabeled wrapper cannot hide.
    """
    paths = sorted(glob.glob(os.path.join(agents_dir, "*.agent.md")))
    if not paths:
        raise FileNotFoundError(f"no agent wrappers found under {agents_dir!r}")
    slugs: "set[str]" = set()
    problems: "list[str]" = []
    for path in paths:
        stem = os.path.basename(path)[: -len(".agent.md")]
        name = _read_frontmatter_name(path)
        slug = name if name else stem
        if name and name != stem:
            problems.append(
                f"agent wrapper {path!r} front-matter name {name!r} does not match its "
                f"filename stem {stem!r} — rename one so the persona slug is unambiguous"
            )
        if slug in slugs:
            problems.append(f"duplicate persona slug {slug!r} (from {path!r})")
        slugs.add(slug)
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
            f"— create the label, or remove/rename the .github/agents wrapper"
        )
    for label in sorted(label_only - covered_truncations):
        problems.append(
            f"GitHub label '{PERSONA_PREFIX}{label}' has no matching "
            f".github/agents/{label}.agent.md — add the wrapper, or delete the stale label"
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
        "and the feature-request milestone dropdown against live GitHub state."
    )
    parser.add_argument("--repo", default=None, help="OWNER/REPO (default: env or gh or khaines/deltasharp)")
    parser.add_argument("--agents-dir", default=DEFAULT_AGENTS_DIR)
    parser.add_argument("--feature-form", default=DEFAULT_FEATURE_FORM)
    parser.add_argument("--taxonomy", default=DEFAULT_TAXONOMY)
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
    _log("reconciliation PASSED: roster, labels, CODEOWNERS, and milestones are in step")
    return 0


if __name__ == "__main__":
    sys.exit(main())
