#!/usr/bin/env python3
"""Verify issue-state annotations in docs against live GitHub issue states."""

from __future__ import annotations

import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
import re


REPO_ROOT = Path(__file__).resolve().parents[2]
DOCS = [
    REPO_ROOT / "docs/engineering/design/storage-exception-log-routing.md",
    REPO_ROOT / "docs/engineering/design/observability-conventions.md",
]

ISSUE_LINK_RE = re.compile(r"https://github\.com/khaines/deltasharp/issues/(\d+)")
STATE_MARKER_RE = re.compile(r"\)\s*<!--\s*issue-state:(open|closed)\s*-->", re.IGNORECASE)


@dataclass(frozen=True)
class ExpectedState:
    issue: int
    expected: str
    file: Path
    line: int


def parse_expected_states() -> tuple[list[ExpectedState], list[str]]:
    expected: list[ExpectedState] = []
    errors: list[str] = []

    for doc in DOCS:
        text = doc.read_text(encoding="utf-8")
        for match in ISSUE_LINK_RE.finditer(text):
            issue = int(match.group(1))
            line = text.count("\n", 0, match.start()) + 1
            marker = STATE_MARKER_RE.match(text[match.end() : match.end() + 80])
            if marker is None:
                errors.append(
                    f"{doc.relative_to(REPO_ROOT)}:{line}: missing "
                    f"'<!-- issue-state:open|closed -->' after issue link #{issue}"
                )
                continue
            expected.append(
                ExpectedState(
                    issue=issue,
                    expected=marker.group(1).lower(),
                    file=doc,
                    line=line,
                )
            )

    return expected, errors


def live_issue_state(issue: int) -> str:
    try:
        result = subprocess.run(
            ["gh", "issue", "view", str(issue), "--json", "state", "--jq", ".state"],
            check=True,
            capture_output=True,
            text=True,
        )
    except subprocess.CalledProcessError as ex:
        raise RuntimeError(
            f"gh issue view {issue} failed with exit code {ex.returncode}: {ex.stderr.strip()}"
        ) from ex

    return result.stdout.strip().lower()


def main() -> int:
    expected, parse_errors = parse_expected_states()
    if parse_errors:
        print("Doc issue-state annotation errors:")
        for error in parse_errors:
            print(f"- {error}")
        return 1

    live_cache: dict[int, str] = {}
    mismatches: list[str] = []
    for item in expected:
        live = live_cache.get(item.issue)
        if live is None:
            live = live_issue_state(item.issue)
            live_cache[item.issue] = live
        if live != item.expected:
            mismatches.append(
                f"{item.file.relative_to(REPO_ROOT)}:{item.line}: issue #{item.issue} is "
                f"{live.upper()} but annotated as {item.expected.upper()}"
            )

    if mismatches:
        print("Doc issue-state mismatches:")
        for mismatch in mismatches:
            print(f"- {mismatch}")
        return 1

    print(
        f"OK: validated {len(expected)} annotated issue references across "
        f"{len(DOCS)} docs ({len(live_cache)} unique issues)."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
