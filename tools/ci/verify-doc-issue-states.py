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
EXPECTED_ANNOTATED_COUNTS = {
    "docs/engineering/design/storage-exception-log-routing.md": 6,
    "docs/engineering/design/observability-conventions.md": 4,
}
EXPECTED_ANNOTATED_ISSUE_FREQUENCIES = {
    "docs/engineering/design/storage-exception-log-routing.md": {
        664: 1,
        689: 1,
        746: 1,
        747: 1,
        749: 1,
        750: 1,
    },
    "docs/engineering/design/observability-conventions.md": {
        479: 1,
        747: 1,
        749: 1,
        790: 1,
    },
}
EXPECTED_ANNOTATED_ISSUE_LINE_ANCHORS = {
    "docs/engineering/design/storage-exception-log-routing.md": [
        (4, 689),
        (6, 664),
        (299, 749),
        (569, 750),
        (662, 746),
        (670, 747),
    ],
    "docs/engineering/design/observability-conventions.md": [
        (46, 479),
        (356, 790),
        (364, 747),
        (365, 749),
    ],
}
MAX_MARKER_DISTANCE_CHARS = 200

GH_LINK_RE = re.compile(r"https://github\.com/khaines/deltasharp/(issues|pull)/(\d+)")
STATE_MARKER_RE = re.compile(r"<!--\s*issue-state:(open|closed)\s*-->", re.IGNORECASE)


@dataclass(frozen=True)
class ExpectedState:
    issue: int
    expected: str
    file: Path
    line: int


def parse_docs_from_args(argv: list[str]) -> tuple[list[Path], list[str]]:
    if len(argv) <= 1:
        return DOCS, []

    docs: list[Path] = []
    errors: list[str] = []
    for raw_path in argv[1:]:
        doc = (REPO_ROOT / raw_path).resolve()
        try:
            doc.relative_to(REPO_ROOT)
        except ValueError:
            errors.append(f"{raw_path}: path must be inside repository root")
            continue
        if not doc.exists():
            errors.append(f"{raw_path}: file does not exist")
            continue
        if not doc.is_file():
            errors.append(f"{raw_path}: path is not a file")
            continue
        if doc.suffix.lower() != ".md":
            errors.append(f"{raw_path}: expected a markdown file (*.md)")
            continue
        docs.append(doc)
    return docs, errors


def normalize_doc_key(path: Path) -> str:
    return str(path.relative_to(REPO_ROOT)).replace("\\", "/").lower()


def enforce_tracked_doc_scope(docs: list[Path], explicit_args: bool) -> list[str]:
    if not explicit_args:
        return []
    tracked = {normalize_doc_key(path) for path in DOCS}
    selected = {normalize_doc_key(path) for path in docs}
    if not (tracked & selected):
        return []
    if tracked.issubset(selected):
        return []
    return [
        "tracked-doc mode requires both tracked docs together: "
        "docs/engineering/design/storage-exception-log-routing.md and "
        "docs/engineering/design/observability-conventions.md"
    ]


def line_number(text: str, index: int) -> int:
    return text.count("\n", 0, index) + 1


def marker_after_link(
    text: str, link_end: int, next_link_start: int | None
) -> tuple[re.Match[str] | None, int | None]:
    window_end = next_link_start if next_link_start is not None else len(text)
    window = text[link_end:window_end]
    lines = window.splitlines()
    candidate_lines: list[str] = []
    for i, item in enumerate(lines):
        if i > 0 and item.strip() == "":
            break
        candidate_lines.append(item)
    candidate = "\n".join(candidate_lines)
    marker = STATE_MARKER_RE.search(candidate)
    if marker is None:
        return None, None
    if marker.start() > MAX_MARKER_DISTANCE_CHARS:
        return None, None
    return marker, line_number(text, link_end + marker.start())


def parse_expected_states(docs: list[Path]) -> tuple[list[ExpectedState], list[str]]:
    expected: list[ExpectedState] = []
    errors: list[str] = []
    expected_by_doc = {normalize_doc_key(doc): 0 for doc in docs}
    issue_frequencies_by_doc: dict[str, dict[int, int]] = {normalize_doc_key(doc): {} for doc in docs}
    issue_anchors_by_doc: dict[str, list[tuple[int, int]]] = {normalize_doc_key(doc): [] for doc in docs}

    for doc in docs:
        try:
            text = doc.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError) as ex:
            errors.append(f"{doc.relative_to(REPO_ROOT)}: unable to read markdown file: {ex}")
            continue
        matches = list(GH_LINK_RE.finditer(text))
        for index, match in enumerate(matches):
            kind = match.group(1)
            issue = int(match.group(2))
            link_line = line_number(text, match.start())
            next_link_start = matches[index + 1].start() if index + 1 < len(matches) else None
            marker, marker_line = marker_after_link(text, match.end(), next_link_start)

            if kind == "pull":
                if marker is not None:
                    line = marker_line if marker_line is not None else link_line
                    errors.append(
                        f"{doc.relative_to(REPO_ROOT)}:{line}: issue-state marker cannot be attached "
                        f"to pull request link #{issue}; use the issue URL instead"
                    )
                continue

            if marker is None:
                errors.append(
                    f"{doc.relative_to(REPO_ROOT)}:{link_line}: missing "
                    f"'<!-- issue-state:open|closed -->' after issue link #{issue}"
                )
                continue
            expected.append(
                ExpectedState(
                    issue=issue,
                    expected=marker.group(1).lower(),
                    file=doc,
                    line=link_line,
                )
            )
            doc_key = normalize_doc_key(doc)
            expected_by_doc[doc_key] += 1
            issue_frequencies_by_doc[doc_key][issue] = (
                issue_frequencies_by_doc[doc_key].get(issue, 0) + 1
            )
            issue_anchors_by_doc[doc_key].append((link_line, issue))

    for doc_path, expected_count in EXPECTED_ANNOTATED_COUNTS.items():
        key = doc_path.lower()
        if key in expected_by_doc and expected_by_doc[key] != expected_count:
            errors.append(
                f"{doc_path}: expected {expected_count} annotated issue links, found "
                f"{expected_by_doc[key]}"
            )
    for doc_path, expected_frequencies in EXPECTED_ANNOTATED_ISSUE_FREQUENCIES.items():
        key = doc_path.lower()
        if key in issue_frequencies_by_doc and issue_frequencies_by_doc[key] != expected_frequencies:
            errors.append(
                f"{doc_path}: expected annotated issue frequencies {expected_frequencies}, found "
                f"{issue_frequencies_by_doc[key]}"
            )
    for doc_path, expected_anchors in EXPECTED_ANNOTATED_ISSUE_LINE_ANCHORS.items():
        key = doc_path.lower()
        if key in issue_anchors_by_doc and issue_anchors_by_doc[key] != expected_anchors:
            errors.append(
                f"{doc_path}: expected annotated issue anchors {expected_anchors}, found "
                f"{issue_anchors_by_doc[key]}"
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
    docs, doc_errors = parse_docs_from_args(sys.argv)
    scope_errors = enforce_tracked_doc_scope(docs, explicit_args=len(sys.argv) > 1)
    if doc_errors or scope_errors:
        print("Doc issue-state input errors:")
        for error in [*doc_errors, *scope_errors]:
            print(f"- {error}")
        return 1

    expected, parse_errors = parse_expected_states(docs)
    if parse_errors:
        print("Doc issue-state annotation errors:")
        for error in parse_errors:
            print(f"- {error}")
        return 1

    live_cache: dict[int, str] = {}
    mismatches: list[str] = []
    for item in expected:
        try:
            live = live_cache.get(item.issue)
            if live is None:
                live = live_issue_state(item.issue)
                live_cache[item.issue] = live
        except RuntimeError as ex:
            print(f"Doc issue-state lookup error: {ex}")
            return 1
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
        f"{len(docs)} docs ({len(live_cache)} unique issues)."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
