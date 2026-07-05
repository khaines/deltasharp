#!/usr/bin/env python3
"""Roster ↔ label-taxonomy reconciliation gate for DeltaSharp (STORY-00.6.2, #452).

PR #449 established `docs/planning/label-taxonomy.md` with a *manual* reconciliation
of the persona roster, the `persona:<slug>` GitHub labels, `CODEOWNERS`, and the
feature-request milestone dropdown. This script turns that manual snapshot into a
lightweight, re-runnable gate so the three sources of drift below fail CI instead of
silently rotting:

  1. **Roster ↔ persona labels.** Every `.github/agents/*.agent.md` wrapper must have a
     matching `persona:<slug>` GitHub label and vice-versa. The GitHub 50-character label
     cap forces exactly one documented truncation (the trailing `-engineer` is dropped from
     `persona:dotnet-vectorized-columnar-compute-engineer`); that truncation is allowed ONLY
     when `label-taxonomy.md` records it. Any other difference is drift and FAILS.
  2. **CODEOWNERS parse errors.** `GET /repos/{repo}/codeowners/errors` must report an empty
     `errors` array — a syntax or unknown-owner error on the default branch FAILS.
  3. **Milestone dropdown ↔ live milestones.** The `id: milestone` dropdown in
     `.github/ISSUE_TEMPLATE/feature_request.yml` must offer exactly the live GitHub
     milestones, plus the documented "needs triage" sentinel. A stale/renamed option or a
     live milestone missing from the dropdown FAILS.

Design constraints
------------------
* **Stdlib only.** No PyYAML / no third-party imports, so the gate is deterministic and
  installs nothing. The milestone dropdown is parsed with a small, targeted reader for the
  GitHub issue-form structure (see `parse_milestone_options`).
* **Degrades gracefully off-network.** The roster read and the dropdown parse are local
  (filesystem) and always run. The three GitHub-API lookups (labels, milestones,
  codeowners/errors) shell out to `gh`; if `gh` is missing or unauthenticated they are
  SKIPPED with a warning so local dev works without a token. Pass `--require-remote`
  (CI does) to turn a skipped remote check into a hard failure, and `--offline` to force
  every remote check to skip.

Usage
-----
    # Full gate (CI): all three checks must run and pass.
    python3 tools/reconcile/roster-labels.py --repo khaines/deltasharp --require-remote

    # Local dev: remote checks run if `gh` is authenticated, otherwise skip.
    python3 tools/reconcile/roster-labels.py

    # Prove the gate's own reconciliation logic with in-memory fixtures (no network):
    python3 tools/reconcile/roster-labels.py --selftest

Exit codes: 0 = reconciled (no drift), 1 = drift detected (a check FAILED), 2 = usage/data
error or a required remote check could not run (`--require-remote` with `gh` unavailable).
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


# --- Output helpers ----------------------------------------------------------------------

def _log(msg: str = "") -> None:
    print(msg, flush=True)


def _error(msg: str) -> None:
    # GitHub Actions annotation; renders inline on the PR.
    print(f"::error::{msg}", flush=True)


def _warning(msg: str) -> None:
    print(f"::warning::{msg}", flush=True)


# --- Repo resolution ---------------------------------------------------------------------

def resolve_repo(explicit: "str | None") -> str:
    """Resolve OWNER/REPO from --repo, the Actions env, `gh`, or the default."""
    if explicit:
        return explicit
    env = os.environ.get("GITHUB_REPOSITORY")
    if env:
        return env
    ok, out, _ = _gh(["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"])
    if ok and out.strip():
        return out.strip()
    return DEFAULT_REPO


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
    """The documented truncation for an over-long persona label drops trailing `-engineer`."""
    if slug.endswith(TRUNCATION_SUFFIX):
        return slug[: -len(TRUNCATION_SUFFIX)]
    return None


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
                continue
        problems.append(
            f"roster persona {slug!r} has no matching '{PERSONA_PREFIX}{slug}' GitHub label "
            f"— create the label, or remove/rename the .github/agents wrapper"
        )
    for label in sorted(label_only):
        problems.append(
            f"GitHub label '{PERSONA_PREFIX}{label}' has no matching "
            f".github/agents/{label}.agent.md — add the wrapper, or delete the stale label"
        )
    return problems, allowed


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


def fetch_codeowners_errors(repo: str) -> "tuple[bool, list, str]":
    ok, out, err = _gh(["api", f"repos/{repo}/codeowners/errors"])
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
    """Handle the offline/skip/require-remote plumbing common to the three API checks.

    Returns (early_result, payload). When early_result is not None the caller should use it
    directly (the remote data is unavailable); otherwise payload holds the fetched data.
    """
    if offline:
        return Result(name, "skip", ["--offline: remote lookup skipped"]), None
    ok, payload, err = fetch()
    if not ok:
        status = "fail" if require_remote else "skip"
        prefix = "required remote check could not run" if require_remote else "skipped"
        return Result(name, status, [f"{prefix}: {err}"]), None
    return None, payload


def run_checks(args: argparse.Namespace) -> "list[Result]":
    repo = resolve_repo(args.repo)
    _log(f"Reconciling roster ↔ labels for repo: {repo}")
    _log("")

    roster, integrity = read_roster(args.agents_dir)
    _log(f"Roster: {len(roster)} persona wrapper(s) under {args.agents_dir}")

    taxonomy_text = ""
    if os.path.exists(args.taxonomy):
        with open(args.taxonomy, "r", encoding="utf-8") as handle:
            taxonomy_text = handle.read()
    else:
        _warning(f"label taxonomy {args.taxonomy!r} not found — truncations cannot be verified")

    results: "list[Result]" = []

    # --- Check 1: roster <-> persona labels ---
    early, labels = _remote_or_skip(
        "roster<->labels", args.offline, lambda: fetch_persona_labels(repo), args.require_remote
    )
    if early is not None:
        # Even when labels are unavailable we still surface local roster integrity problems.
        if integrity:
            results.append(Result("roster-integrity", "fail", integrity))
        results.append(early)
    else:
        problems, allowed = reconcile_roster_labels(roster, labels, taxonomy_text)
        problems = integrity + problems
        detail = [f"{len(roster)} roster slug(s), {len(labels)} persona label(s)"]
        for slug, trunc in allowed:
            detail.append(f"allowed documented truncation: {slug} -> {PERSONA_PREFIX}{trunc}")
        results.append(
            Result("roster<->labels", "fail" if problems else "pass", problems or detail)
        )

    # --- Check 2: CODEOWNERS parse errors ---
    early, errors = _remote_or_skip(
        "codeowners-errors", args.offline, lambda: fetch_codeowners_errors(repo), args.require_remote
    )
    if early is not None:
        results.append(early)
    else:
        if errors:
            lines = [
                f"{err.get('path', '?')}: {err.get('kind', 'error')} — {err.get('message', '')}".strip()
                for err in errors
            ]
            results.append(Result("codeowners-errors", "fail", lines))
        else:
            results.append(Result("codeowners-errors", "pass", ["0 CODEOWNERS errors"]))

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
        "--offline", action="store_true", help="skip all GitHub-API checks (local file checks only)"
    )
    parser.add_argument(
        "--require-remote",
        action="store_true",
        help="fail (exit 2) if a GitHub-API check cannot run — CI uses this to enforce the gate",
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
        _error(
            f"reconciliation could not complete: {len(skipped)} required remote check(s) were "
            f"skipped (is `gh` authenticated with a token?)"
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
