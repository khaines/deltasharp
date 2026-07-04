#!/usr/bin/env python3
"""Coverage gate for DeltaSharp (FEAT-00.2 / STORY-00.2.3, #106).

Reads the Cobertura reports emitted by `coverlet.collector` (one per test assembly
per target framework), MERGES them by unioning per-(file, line) hits, computes the
overall line-coverage percentage, and fails (exit 1) when it is below the configured
threshold. Prints a per-assembly breakdown and the measured-vs-threshold result.

Why a merge is required
-----------------------
Coverage is collected with Include=[DeltaSharp.*], so every test assembly instruments
*all* DeltaSharp production assemblies it loads. The same production line therefore
appears in several reports with different hit counts (for example DeltaSharp.Core is
well covered by DeltaSharp.Core.Tests but barely touched by DeltaSharp.Executor.Tests,
and DeltaSharp.Core.Tests runs on both net8.0 and net10.0). Summing per-report
lines-covered/lines-valid would double- or quadruple-count those lines and produce a
meaningless number. The correct metric unions coverage: a line is covered if hits > 0
in ANY report, and the denominator is the set of distinct (file, line) pairs. This is
the same merge semantics ReportGenerator uses, implemented here with the Python stdlib
only (no third-party or network dependency, so the gate is deterministic and offline).

Usage
-----
    python3 tools/coverage/coverage-gate.py --results-dir TestResults \
        [--config tools/coverage/coverage-config.json] \
        [--threshold 78.0] [--summary-out TestResults/coverage-summary.md]

Exit codes: 0 = at/above threshold, 1 = below threshold, 2 = usage/data error.
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def _log(msg: str = "") -> None:
    print(msg, flush=True)


def _gha() -> bool:
    return os.environ.get("GITHUB_ACTIONS", "").lower() == "true"


def find_reports(results_dir: str) -> list[str]:
    """All coverage.cobertura.xml files under results_dir (recursive)."""
    pattern = os.path.join(results_dir, "**", "coverage.cobertura.xml")
    return sorted(glob.glob(pattern, recursive=True))


def merge_reports(report_paths):
    """Union per-(package, file, line) hit state across every report.

    Returns (per_package, covered_lines, total_lines) where per_package maps an
    assembly name to (covered, total). A line counts as covered when hits > 0 in
    ANY report; the denominator is the count of distinct (file, line) pairs.
    """
    # (package, filename, line_number) -> covered? (True once any report hits it)
    line_covered: dict[tuple[str, str, str], bool] = defaultdict(bool)

    for path in report_paths:
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as exc:
            _log(f"::error::failed to parse coverage report {path}: {exc}")
            raise SystemExit(2)
        for package in root.iter("package"):
            pkg_name = package.get("name", "(unknown)")
            for cls in package.iter("class"):
                filename = cls.get("filename", "")
                lines = cls.find("lines")
                if lines is None:
                    continue
                for line in lines.findall("line"):
                    number = line.get("number", "")
                    try:
                        hits = int(line.get("hits", "0"))
                    except ValueError:
                        hits = 0
                    key = (pkg_name, filename, number)
                    if hits > 0:
                        line_covered[key] = True
                    elif key not in line_covered:
                        line_covered[key] = False

    per_package_total: dict[str, int] = defaultdict(int)
    per_package_covered: dict[str, int] = defaultdict(int)
    for (pkg_name, _filename, _number), covered in line_covered.items():
        per_package_total[pkg_name] += 1
        if covered:
            per_package_covered[pkg_name] += 1

    total = len(line_covered)
    covered = sum(1 for c in line_covered.values() if c)
    per_package = {
        name: (per_package_covered[name], per_package_total[name])
        for name in sorted(per_package_total)
    }
    return per_package, covered, total


def pct(covered: int, total: int) -> float:
    return 100.0 * covered / total if total else 0.0


def render_summary(per_package, covered, total, threshold, measured, passed) -> str:
    lines = []
    lines.append("# Coverage summary")
    lines.append("")
    lines.append(
        "Merged (TFM-deduplicated) line coverage across all Cobertura reports. "
        "A line is covered if hit by any test assembly on any target framework."
    )
    lines.append("")
    lines.append("| Assembly | Covered | Lines | Line % |")
    lines.append("| --- | ---: | ---: | ---: |")
    for name, (cov, tot) in per_package.items():
        lines.append(f"| {name} | {cov} | {tot} | {pct(cov, tot):.2f}% |")
    lines.append(f"| **TOTAL** | **{covered}** | **{total}** | **{measured:.2f}%** |")
    lines.append("")
    lines.append(f"- **Threshold (minimum line coverage):** {threshold:.2f}%")
    lines.append(f"- **Measured line coverage:** {measured:.2f}%")
    lines.append(f"- **Result:** {'PASS' if passed else 'FAIL'}")
    lines.append("")
    return "\n".join(lines)


def load_threshold(config_path: str) -> tuple[float, float]:
    with open(config_path, "r", encoding="utf-8") as handle:
        config = json.load(handle)
    threshold = float(config["minimumLineCoverage"])
    slack = float(config.get("ratchetSuggestSlack", 0.0))
    return threshold, slack


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="DeltaSharp coverage gate")
    parser.add_argument("--results-dir", default="TestResults")
    parser.add_argument(
        "--config",
        default=os.path.join("tools", "coverage", "coverage-config.json"),
        help="JSON config holding minimumLineCoverage (single source of truth).",
    )
    parser.add_argument(
        "--threshold",
        type=float,
        default=None,
        help="Override the configured threshold (for local dry runs only).",
    )
    parser.add_argument("--summary-out", default=None)
    args = parser.parse_args(argv)

    if args.threshold is not None:
        threshold, slack = args.threshold, 0.0
    else:
        try:
            threshold, slack = load_threshold(args.config)
        except (OSError, ValueError, KeyError) as exc:
            _log(f"::error::could not read threshold from {args.config}: {exc}")
            return 2

    reports = find_reports(args.results_dir)
    if not reports:
        _log(f"::error::no coverage.cobertura.xml found under {args.results_dir!r}")
        return 2

    per_package, covered, total = merge_reports(reports)
    if total == 0:
        _log("::error::coverage reports contained no measurable production lines")
        return 2

    measured = round(pct(covered, total), 2)

    _log(f"Coverage gate — merged {len(reports)} Cobertura report(s), TFM-deduplicated")
    _log("")
    name_w = max((len(n) for n in per_package), default=10)
    header = f"  {'Assembly'.ljust(name_w)}   Covered    Lines    Line%"
    _log(header)
    _log("  " + "-" * (len(header) - 2))
    for name, (cov, tot) in per_package.items():
        _log(f"  {name.ljust(name_w)}   {cov:>7}  {tot:>7}   {pct(cov, tot):6.2f}%")
    _log("  " + "-" * (len(header) - 2))
    _log(f"  {'TOTAL'.ljust(name_w)}   {covered:>7}  {total:>7}   {measured:6.2f}%")
    _log("")

    passed = measured >= threshold
    detail = f"measured line coverage {measured:.2f}% vs threshold {threshold:.2f}%"
    if passed:
        _log(f"PASS: {detail}")
        if _gha():
            _log(f"::notice::coverage gate PASS — {detail}")
        headroom = measured - threshold
        if slack > 0 and headroom >= slack:
            hint = (
                f"coverage has {headroom:.2f}% headroom over the floor; ratchet "
                f"minimumLineCoverage up toward {measured:.2f}% (policy: threshold only rises)"
            )
            _log(f"RATCHET SUGGESTION: {hint}")
            if _gha():
                _log(f"::notice::{hint}")
    else:
        _log(f"FAIL: {detail}")
        if _gha():
            _log(f"::error::coverage gate FAIL — {detail}")

    if args.summary_out:
        summary = render_summary(per_package, covered, total, threshold, measured, passed)
        os.makedirs(os.path.dirname(os.path.abspath(args.summary_out)), exist_ok=True)
        with open(args.summary_out, "w", encoding="utf-8") as handle:
            handle.write(summary)
        _log(f"Wrote summary to {args.summary_out}")
        step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if step_summary:
            with open(step_summary, "a", encoding="utf-8") as handle:
                handle.write(summary + "\n")

    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
