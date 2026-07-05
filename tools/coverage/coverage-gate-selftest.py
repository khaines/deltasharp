#!/usr/bin/env python3
"""Self-tests for the DeltaSharp coverage gate (FEAT-00.2 / STORY-00.2.3, #106).

The coverage gate (`coverage-gate.py`) is itself a piece of CI-critical logic: a bug that
makes it fail-OPEN would let real coverage regressions merge silently. These tests exercise
its exit-code contract end-to-end against synthetic Cobertura fixtures, so the gate's own
behaviour is regression-tested on every CI run — including the two provenance holes a review
red-team found and this change closed:

  * union-inflate — an EXTRA (planted / trivially-100%-covered) assembly must NOT dilute or
    inflate the aggregate; an out-of-allowlist package fails the gate closed (exit 2), so a
    below-floor real set cannot be rescued by a fake one.
  * rounding-boundary — a percentage strictly below the floor that merely ROUNDS to the floor
    (for example 86.999% -> 87.00) must FAIL (exit 1); the pass decision compares the
    unrounded value.

Stdlib only (`unittest`, `tempfile`, `subprocess`) — no third-party dependency, mirroring the
gate's own offline/deterministic design. Run with: `python3 tools/coverage/coverage-gate-selftest.py`
(exit 0 = all pass). CI runs it before the real gate so a broken gate fails the build loudly.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
import uuid

_HERE = os.path.dirname(os.path.abspath(__file__))
_GATE = os.path.join(_HERE, "coverage-gate.py")
_EXPECTED = ["DeltaSharp.Abstractions", "DeltaSharp.Core", "DeltaSharp.Engine", "DeltaSharp.Executor"]


def _report_xml(packages) -> str:
    """A minimal Cobertura report for `packages` = [(name, covered, total), ...].

    Each package gets one class with `total` lines, the first `covered` of which are hit.
    Filenames are unique per package so no (file, line) key collides across packages.
    """
    parts = ['<?xml version="1.0"?>', "<coverage><packages>"]
    for name, covered, total in packages:
        parts.append(f'<package name="{name}"><classes>')
        parts.append(f'<class filename="{name}.cs"><lines>')
        for i in range(1, total + 1):
            hits = 1 if i <= covered else 0
            parts.append(f'<line number="{i}" hits="{hits}"/>')
        parts.append("</lines></class></classes></package>")
    parts.append("</packages></coverage>")
    return "".join(parts)


class CoverageGateSelfTest(unittest.TestCase):
    def _run(self, packages=None, *, threshold=87.0, expected=None, raw_files=None):
        """Write fixtures to a temp dir, invoke the gate, and return its exit code."""
        with tempfile.TemporaryDirectory() as root:
            results = os.path.join(root, "TestResults")
            os.makedirs(results, exist_ok=True)
            if packages is not None:
                # one report per package so find_reports globs them all
                for pkg in packages:
                    d = os.path.join(results, uuid.uuid4().hex)
                    os.makedirs(d)
                    with open(os.path.join(d, "coverage.cobertura.xml"), "w", encoding="utf-8") as fh:
                        fh.write(_report_xml([pkg]))
            for rel, content in (raw_files or {}).items():
                d = os.path.join(results, os.path.dirname(rel))
                os.makedirs(d, exist_ok=True)
                with open(os.path.join(results, rel), "w", encoding="utf-8") as fh:
                    fh.write(content)
            config = os.path.join(root, "config.json")
            with open(config, "w", encoding="utf-8") as fh:
                json.dump(
                    {
                        "minimumLineCoverage": threshold,
                        "expectedAssemblies": _EXPECTED if expected is None else expected,
                        "ratchetSuggestSlack": 1.5,
                    },
                    fh,
                )
            proc = subprocess.run(
                [sys.executable, _GATE, "--results-dir", results, "--config", config],
                capture_output=True,
                text=True,
            )
            return proc.returncode, proc.stdout + proc.stderr

    def test_baseline_at_or_above_floor_passes(self):
        # ~89% across the exact expected set -> PASS.
        code, out = self._run([(n, 890, 1000) for n in _EXPECTED])
        self.assertEqual(code, 0, out)

    def test_below_floor_fails(self):
        # 80% real coverage -> FAIL (exit 1), the ordinary regression signal.
        code, out = self._run([(n, 800, 1000) for n in _EXPECTED])
        self.assertEqual(code, 1, out)

    def test_missing_expected_assembly_fails_closed(self):
        # Drop DeltaSharp.Executor -> a lost suite must fail closed (exit 2), not inflate.
        code, out = self._run([(n, 890, 1000) for n in _EXPECTED if n != "DeltaSharp.Executor"])
        self.assertEqual(code, 2, out)
        self.assertIn("DeltaSharp.Executor", out)

    def test_unexpected_assembly_does_not_inflate_and_fails_closed(self):
        # Red-team union-inflate: 4 real assemblies at 80% (below floor) + a planted
        # Fake.Assembly at 100% that would lift the naive aggregate to 90% and PASS.
        # The gate must reject the out-of-allowlist package (exit 2), so the fake cannot
        # rescue the below-floor real set.
        packages = [(n, 2000, 2500) for n in _EXPECTED] + [("Fake.Assembly", 10000, 10000)]
        code, out = self._run(packages)
        self.assertEqual(code, 2, out)
        self.assertIn("Fake.Assembly", out)
        self.assertIn("unexpected", out.lower())

    def test_rounding_boundary_below_floor_fails(self):
        # Red-team rounding-boundary: aggregate 86.999% is strictly below the 87.0 floor but
        # round(86.999, 2) == 87.00. The OLD gate compared the rounded value and PASSED; the
        # fixed gate compares the unrounded value and FAILS (exit 1).
        # 86999 / 100000 = 86.999%: three expected assemblies at 1/1, Core carries the rest.
        packages = [
            ("DeltaSharp.Abstractions", 1, 1),
            ("DeltaSharp.Engine", 1, 1),
            ("DeltaSharp.Executor", 1, 1),
            ("DeltaSharp.Core", 86996, 99997),
        ]
        # sanity: 3 + 86996 = 86999 covered, 3 + 99997 = 100000 total -> 86.999%
        code, out = self._run(packages)
        self.assertEqual(code, 1, out)

    def test_rounding_boundary_at_floor_passes(self):
        # The mirror case: exactly 87.0% must PASS (the fix must not over-correct).
        packages = [
            ("DeltaSharp.Abstractions", 1, 1),
            ("DeltaSharp.Engine", 1, 1),
            ("DeltaSharp.Executor", 1, 1),
            ("DeltaSharp.Core", 86997, 99997),
        ]  # 87000 / 100000 = 87.000%
        code, out = self._run(packages)
        self.assertEqual(code, 0, out)

    def test_malformed_report_fails_closed(self):
        code, out = self._run(
            packages=[(n, 890, 1000) for n in _EXPECTED],
            raw_files={os.path.join("bad", "coverage.cobertura.xml"): "<coverage><not-closed>"},
        )
        self.assertEqual(code, 2, out)

    def test_no_reports_fails_closed(self):
        code, out = self._run(packages=[])
        self.assertEqual(code, 2, out)


if __name__ == "__main__":
    unittest.main(verbosity=2)
