#!/usr/bin/env python3
"""In-repository secret-scanning tripwire for DeltaSharp (STORY-00.3.1, #107).

The PRIMARY secret-detection control is GitHub-native secret scanning + push protection
(enabled on the repository; see docs/engineering/design/supply-chain-security.md). That
control is org-managed, ships a broad provider pattern set with validity checks, and
blocks pushes. This script is a lightweight, self-contained CI tripwire that:

  * makes the control demonstrably testable on every run via a committed, obviously-fake
    fixture (AC: "a safe fixture is reported by a secret scanner without exposing real
    creds"), including for forks/clones where org secret-scanning results are not surfaced
    as a check;
  * runs identically locally and in CI with NO third-party dependency (Python-3 stdlib
    only), matching the repository's self-contained tooling convention (tools/dco-check.sh,
    tools/coverage/coverage-gate.py; .github/workflows/dco.yml deliberately uses no
    third-party action); and
  * enforces suppression hygiene: every allowlist entry carries scope/reason/owner/expiry
    and EXPIRES (an expired waiver stops suppressing, forcing review).

It deliberately uses a small set of HIGH-SIGNAL provider patterns (plus a project canary)
so false positives are ~zero; broad/entropy-based detection is left to GitHub-native
scanning. Matched values are always MASKED in output, so the scanner never echoes a
credential into CI logs (checklist 05).

Usage:
    python3 tools/security/secret-scan.py            # scan the repo, fail on un-allowlisted
    python3 tools/security/secret-scan.py --selftest # prove detectors + fixture detection

Exit codes: 0 = clean (only allowlisted/expected matches), 1 = un-allowlisted match,
expired waiver, or malformed allowlist/bad input (fails closed).
"""

from __future__ import annotations

import argparse
import datetime as _dt
import fnmatch
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any

# High-signal detectors. Each value is a compiled regex; keys are stable rule ids used by
# the allowlist. Patterns are provider-prefixed (distinct, low false-positive) plus a
# project-owned canary. NOTE: this source file contains only regex *patterns*, never a
# literal secret, so the scanner does not match itself.
_DETECTORS: dict[str, re.Pattern[str]] = {
    "aws-access-key-id": re.compile(r"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"),
    "private-key-block": re.compile(
        r"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----"),
    "github-token": re.compile(r"\bgh[pousr]_[A-Za-z0-9]{36,}\b"),
    "slack-token": re.compile(r"\bxox[baprs]-[0-9A-Za-z-]{10,}\b"),
    "google-api-key": re.compile(r"\bAIza[0-9A-Za-z_\-]{35}\b"),
    # Project canary: a stable, obviously-fake, non-provider pattern that gives the fixture
    # a deterministic, push-protection-safe match independent of any real provider format.
    "deltasharp-test-canary": re.compile(r"DELTASHARP_TEST_SECRET_[0-9A-Za-z_]{16,}"),
}

_REQUIRED_ALLOW_FIELDS = ("path", "scope", "reason", "owner", "expiry")
_MAX_BYTES = 1_000_000  # skip files larger than 1 MB (data/binaries, not source)
DEFAULT_ALLOWLIST = Path(__file__).with_name("secret-scan-allowlist.json")


class ScanError(Exception):
    """Malformed allowlist or environment error — the scanner fails closed."""


def _mask(value: str) -> str:
    """Reveal only a short non-sensitive prefix; mask the rest and cap the length so a
    real credential never lands in logs and lines stay readable."""
    value = value.strip()
    if len(value) <= 6:
        return value[:2] + "*" * (len(value) - 2)
    return value[:4] + "*" * min(len(value) - 4, 12)


def _annotate(level: str, message: str) -> None:
    print(f"::{level}::{message}")


class Match:
    def __init__(self, path: str, line_no: int, rule: str, value: str) -> None:
        self.path = path
        self.line_no = line_no
        self.rule = rule
        self.value = value

    def describe(self) -> str:
        return f"{self.path}:{self.line_no} [{self.rule}] {_mask(self.value)}"


def _repo_root() -> Path:
    try:
        out = subprocess.run(["git", "rev-parse", "--show-toplevel"],
                             capture_output=True, text=True, check=True)
    except (subprocess.CalledProcessError, FileNotFoundError) as exc:
        raise ScanError("not a git repository (git rev-parse failed)") from exc
    return Path(out.stdout.strip())


def _tracked_files(root: Path) -> list[str]:
    out = subprocess.run(["git", "ls-files", "-z"], cwd=root,
                         capture_output=True, text=True, check=True)
    return [p for p in out.stdout.split("\0") if p]


def scan_file(root: Path, rel_path: str) -> list[Match]:
    full = root / rel_path
    try:
        data = full.read_bytes()
    except OSError:
        return []
    if len(data) > _MAX_BYTES or b"\x00" in data:  # skip large/binary files
        return []
    text = data.decode("utf-8", errors="replace")
    matches: list[Match] = []
    for i, line in enumerate(text.splitlines(), start=1):
        for rule, pattern in _DETECTORS.items():
            for m in pattern.finditer(line):
                matches.append(Match(rel_path, i, rule, m.group(0)))
    return matches


def load_allowlist(path: Path) -> list[dict[str, Any]]:
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ScanError(f"allowlist file not found: {path}") from exc
    except json.JSONDecodeError as exc:
        raise ScanError(f"allowlist is not valid JSON: {path}: {exc}") from exc
    allow = doc.get("allow", [])
    if not isinstance(allow, list):
        raise ScanError("allowlist 'allow' must be an array")
    for i, entry in enumerate(allow):
        if not isinstance(entry, dict):
            raise ScanError(f"allow entry #{i} is not an object")
        missing = [f for f in _REQUIRED_ALLOW_FIELDS if not entry.get(f)]
        if missing:
            raise ScanError(f"allow entry #{i} ({entry.get('path', '?')}) is missing "
                            f"required field(s): {', '.join(missing)}")
        try:
            _dt.date.fromisoformat(str(entry["expiry"]))
        except ValueError as exc:
            raise ScanError(f"allow entry #{i} ({entry['path']}) has an invalid expiry "
                            f"(expected YYYY-MM-DD): {entry['expiry']}") from exc
    return allow


def _entry_covers(entry: dict[str, Any], match: Match) -> bool:
    if not fnmatch.fnmatch(match.path, entry["path"]):
        return False
    rules = entry.get("rules", ["*"])
    return "*" in rules or match.rule in rules


def classify(matches: list[Match], allow: list[dict[str, Any]],
             today: _dt.date) -> tuple[list[Match], list[tuple[Match, dict]],
                                       list[tuple[Match, dict]]]:
    """Split matches into (violations, expected[allowlisted+unexpired], expired)."""
    violations: list[Match] = []
    expected: list[tuple[Match, dict]] = []
    expired: list[tuple[Match, dict]] = []
    for m in matches:
        covering = next((e for e in allow if _entry_covers(e, m)), None)
        if covering is None:
            violations.append(m)
        elif _dt.date.fromisoformat(str(covering["expiry"])) < today:
            expired.append((m, covering))
            violations.append(m)  # an expired waiver no longer protects the match
        else:
            expected.append((m, covering))
    return violations, expected, expired


def run_scan(allowlist_path: Path, today: _dt.date | None = None) -> int:
    today = today or _dt.date.today()
    root = _repo_root()
    allow = load_allowlist(allowlist_path)

    all_matches: list[Match] = []
    for rel in _tracked_files(root):
        all_matches.extend(scan_file(root, rel))

    violations, expected, expired = classify(all_matches, allow, today)

    print("Secret scan — high-signal provider patterns + project canary")
    print(f"  tracked-file matches: {len(all_matches)} | violations: {len(violations)} | "
          f"expected (allowlisted): {len(expected)}")
    print()

    if expected:
        print("Reported, expected (safe fixture / allowlisted, unexpired):")
        for m, e in expected:
            print(f"  ~ {m.describe()}")
            print(f"      scope={e['scope']} reason={e['reason']} "
                  f"owner={e['owner']} expiry={e['expiry']}")
    for m, e in expired:
        _annotate("warning", f"EXPIRED secret-scan allowlist entry for {e['path']} "
                            f"(expiry {e['expiry']}, owner {e['owner']}) — {m.describe()}")
    if violations:
        print("VIOLATIONS (un-allowlisted potential secrets):")
        for m in violations:
            print(f"  ✗ {m.describe()}")
            _annotate("error", f"potential secret {m.describe()}")

    print()
    if violations:
        print(f"RESULT: FAIL — {len(violations)} un-allowlisted match(es). If a match is a "
              "false positive or an intentional fixture, add a scoped, owned, expiring "
              "allowlist entry; if it is a real secret, ROTATE it immediately.")
        _annotate("error", f"secret scan FAIL — {len(violations)} un-allowlisted match(es)")
        return 1
    print("RESULT: PASS")
    return 0


# --------------------------------------------------------------------------------------
# Self-test: prove every detector still fires, and that the on-disk fixture is detected.
# Provider sample strings are ASSEMBLED FROM FRAGMENTS at runtime so this source file
# contains no literal secret (nothing for the scanner to match, nothing push protection
# can flag) while still exercising each regex.
# --------------------------------------------------------------------------------------
def _selftest(allowlist_path: Path) -> int:
    failures: list[str] = []

    def check(name: str, cond: bool) -> None:
        if cond:
            print(f"  ok    {name}")
        else:
            print(f"  FAIL  {name}")
            failures.append(name)

    samples = {
        "aws-access-key-id": "AKIA" + "IOSFODNN7EXAMPLE",
        "private-key-block": "-----BEGIN RSA PRIVATE" + " KEY-----",
        "github-token": "ghp" + "_" + "A" * 36,
        "slack-token": "xoxb" + "-" + "1" * 12,
        "google-api-key": "AIza" + "B" * 35,
        "deltasharp-test-canary": "DELTASHARP_TEST_SECRET_" + "0" * 20,
    }
    for rule, pattern in _DETECTORS.items():
        check(f"detector fires: {rule}", bool(pattern.search(samples[rule])))

    # The masker must never reveal a full value.
    check("masker redacts", _mask("AKIA" + "IOSFODNN7EXAMPLE").endswith("*"))

    # This source file must not match any detector (no literal secret embedded here).
    self_matches = scan_file(_repo_root(), str(Path(__file__).relative_to(_repo_root())))
    check("scanner does not match itself", len(self_matches) == 0)

    # The committed fixture must be detected (fail-closed if it is removed or a regex
    # regresses) AND must be fully covered by an unexpired allowlist entry.
    root = _repo_root()
    allow = load_allowlist(allowlist_path)
    fixture_rel = None
    for entry in allow:
        if "fixture" in entry["path"]:
            fixture_rel = entry["path"]
            break
    check("fixture allowlist entry present", fixture_rel is not None)
    if fixture_rel is not None:
        fmatches = scan_file(root, fixture_rel)
        check("fixture is detected by >=1 detector", len(fmatches) > 0)
        _, expected, expired = classify(fmatches, allow, _dt.date.today())
        check("fixture is fully allowlisted & unexpired",
              len(expected) == len(fmatches) and not expired)

    print()
    if failures:
        print(f"Secret-scan self-test FAILED ({len(failures)} case(s)).")
        return 1
    print("Secret-scan self-test passed.")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="DeltaSharp in-repo secret scanner.")
    parser.add_argument("--allowlist", type=Path, default=DEFAULT_ALLOWLIST,
                        help=f"allowlist file (default: {DEFAULT_ALLOWLIST.name})")
    parser.add_argument("--selftest", action="store_true",
                        help="run detector + fixture self-test and exit")
    args = parser.parse_args(argv)
    try:
        if args.selftest:
            return _selftest(args.allowlist)
        return run_scan(args.allowlist)
    except ScanError as exc:
        _annotate("error", f"secret scan could not run: {exc}")
        print(f"secret scan ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
