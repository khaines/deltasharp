#!/usr/bin/env python3
"""Self-tests for the DeltaSharp coverage gate (FEAT-00.2 / STORY-00.2.3, #106).

The coverage gate (`coverage-gate.py`) is itself a piece of CI-critical logic: a bug that
makes it fail-OPEN would let real coverage regressions merge silently. These tests exercise
its exit-code contract end-to-end against synthetic Cobertura fixtures, so the gate's own
behaviour is regression-tested on every CI run — including the provenance holes a review red-team
found and this change closed:

  * union-inflate — an EXTRA (planted / trivially-100%-covered) assembly must NOT dilute or
    inflate the aggregate; an out-of-allowlist package fails the gate closed (exit 2), so a
    below-floor real set cannot be rescued by a fake one.
  * rounding-boundary — a percentage strictly below the floor that merely ROUNDS to the floor
    (for example 86.999% -> 87.00) must FAIL (exit 1); the pass decision compares the
    unrounded value.
  * empty allowlist (#457) — an emptied/absent expectedAssemblies must fail CLOSED (exit 2), never
    revert to fail-open global accounting.
  * allowlist-vs-src drift (#457) — the allowlist must equal the actual src/ production projects; a
    new src/ assembly left off the list, or a stale entry with no src/ project, fails CLOSED (exit 2).

Each test synthesizes a matching `src/` tree (via `--src-root`) so the drift guard has a hermetic
ground truth that is independent of the real repository layout.

Stdlib only (`unittest`, `tempfile`, `subprocess`) — no third-party dependency, mirroring the
gate's own offline/deterministic design. Run with: `python3 tools/coverage/coverage-gate-selftest.py`
(exit 0 = all pass). CI runs it before the real gate so a broken gate fails the build loudly.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
import uuid
from importlib import util as _importlib_util

_HERE = os.path.dirname(os.path.abspath(__file__))
_GATE = os.path.join(_HERE, "coverage-gate.py")
_DETECT = os.path.join(_HERE, "detect-collect-abort.sh")
_EXPECTED = ["DeltaSharp.Abstractions", "DeltaSharp.Core", "DeltaSharp.Engine", "DeltaSharp.Executor"]

# Load the gate module (hyphenated filename) so the sentinel filename is referenced from the SAME
# constant the gate uses — the self-test must not duplicate the literal (a drift would silently
# disable the #858 fix on the gate side without a red test).
_spec = _importlib_util.spec_from_file_location("_coverage_gate", _GATE)
_gate_mod = _importlib_util.module_from_spec(_spec)
_spec.loader.exec_module(_gate_mod)
_ABORT_SENTINEL = _gate_mod._ABORT_SENTINEL


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
    def _run(self, packages=None, *, threshold=87.0, expected=None, raw_files=None, src_assemblies=None):
        """Write fixtures to a temp dir, invoke the gate, and return its exit code.

        A synthetic `src/` tree is materialized so the fail-closed allowlist-vs-src drift guard has a
        ground truth. By default it mirrors the config's `expectedAssemblies` (so drift passes and the
        test exercises the behaviour it actually targets); pass `src_assemblies` to force a drift.
        """
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
            config_expected = _EXPECTED if expected is None else expected
            # The drift ground truth: default to the config's allowlist so drift passes; a test forces
            # a mismatch by passing src_assemblies explicitly.
            src_names = config_expected if src_assemblies is None else src_assemblies
            src_root = os.path.join(root, "src")
            os.makedirs(src_root, exist_ok=True)
            for name in src_names:
                pdir = os.path.join(src_root, name)
                os.makedirs(pdir, exist_ok=True)
                with open(os.path.join(pdir, f"{name}.csproj"), "w", encoding="utf-8") as fh:
                    fh.write(
                        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                        f"<AssemblyName>{name}</AssemblyName></PropertyGroup></Project>"
                    )
            config = os.path.join(root, "config.json")
            with open(config, "w", encoding="utf-8") as fh:
                json.dump(
                    {
                        "minimumLineCoverage": threshold,
                        "expectedAssemblies": config_expected,
                        "ratchetSuggestSlack": 1.5,
                    },
                    fh,
                )
            proc = subprocess.run(
                [
                    sys.executable, _GATE,
                    "--results-dir", results,
                    "--config", config,
                    "--src-root", src_root,
                ],
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

    def test_empty_allowlist_fails_closed(self):
        # #457 empty-allowlist hole: an emptied/absent expectedAssemblies must NOT revert to
        # fail-OPEN global accounting. Even a healthy, above-floor real set fails closed (exit 2)
        # when the allowlist is empty, so deleting one config line cannot silently disable the
        # provenance guards.
        code, out = self._run([(n, 890, 1000) for n in _EXPECTED], expected=[])
        self.assertEqual(code, 2, out)
        self.assertIn("expectedAssemblies is empty", out)

    def test_new_src_assembly_not_allowlisted_fails_closed(self):
        # #457 drift hole (add): a NEW src/ production assembly that was not added to the allowlist
        # must fail closed (exit 2) — the allowlist cannot silently omit a production assembly, the
        # same "cannot drift" property the centrally-hoisted analyzers have. Allowlist + reports carry
        # the 4 known assemblies; src/ has a 5th.
        code, out = self._run(
            [(n, 890, 1000) for n in _EXPECTED],
            src_assemblies=_EXPECTED + ["DeltaSharp.NewThing"],
        )
        self.assertEqual(code, 2, out)
        self.assertIn("drifted", out)
        self.assertIn("DeltaSharp.NewThing", out)

    def test_stale_allowlist_entry_not_in_src_fails_closed(self):
        # #457 drift hole (remove/typo): an allowlist entry with no matching src/ project must fail
        # closed (exit 2). src/ has the 4 known assemblies; the allowlist has a 5th ghost.
        code, out = self._run(
            [(n, 890, 1000) for n in _EXPECTED],
            expected=_EXPECTED + ["DeltaSharp.Ghost"],
            src_assemblies=_EXPECTED,
        )
        self.assertEqual(code, 2, out)
        self.assertIn("drifted", out)
        self.assertIn("DeltaSharp.Ghost", out)

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

    def test_abort_sentinel_returns_indeterminate_over_passing_set(self):
        # #858: an abort sentinel (instrumented test host crashed on every attempt) makes the
        # gate exit 3 (indeterminate) even over an otherwise-PASSING report set — the reports are
        # truncated, so no verdict may be gated on them.
        code, out = self._run(
            packages=[(n, 890, 1000) for n in _EXPECTED],
            raw_files={_ABORT_SENTINEL: "aborted on all 3 attempts"},
        )
        self.assertEqual(code, 3, out)
        self.assertIn("indeterminate", out.lower())

    def test_abort_sentinel_precedes_truncated_low_coverage(self):
        # The crux: a truncated report set would otherwise read as a phantom low-coverage FAIL
        # (exit 1). With the sentinel present the gate must exit 3 (indeterminate / re-run), NOT 1,
        # so an instrumentation crash is never mistaken for a real coverage regression.
        code, out = self._run(
            packages=[(n, 280, 1000) for n in _EXPECTED],  # ~28% — the observed truncated value
            raw_files={_ABORT_SENTINEL: "aborted on all 3 attempts"},
        )
        self.assertEqual(code, 3, out)
        self.assertNotIn("below threshold", out.lower())

    def test_abort_sentinel_detail_is_injection_sanitized(self):
        # HIGH (round-2/3 red-team + quality): the sentinel body is attacker-influenceable (a PR test
        # runs before the collect step), and the gate echoes a bounded slice into a ::error:: line. A
        # NON-idempotent collapse (str.replace("::",":")) leaves a live "::" token from a "::::"-run,
        # forging a workflow command; and an un-stripped newline could START a new GHA-parsed command
        # line. Pin BOTH halves (idempotent single-pass re.sub(r":{2,}",":") AND newline->space
        # collapse) by asserting the echoed detail equals the exact fully-sanitized form, contiguously
        # (a surviving "::" OR an un-collapsed newline both break the contiguous match). Reverting
        # either sanitize pass leaves every OTHER test green, so without this the HIGH fix could
        # silently regress.
        payload = "a:::::error::title=x::pwn\r\nsecond::set-output::name=y"
        expected_detail = "a:error:title=x:pwn second:set-output:name=y"  # \r\n->space, then :{2,}->:
        code, out = self._run(
            packages=[(n, 890, 1000) for n in _EXPECTED],
            raw_files={_ABORT_SENTINEL: payload},
        )
        self.assertEqual(code, 3, out)
        # The whole sanitized detail must appear on ONE line, contiguously — not split across lines by
        # a leaked newline, and with no surviving "::" run.
        self.assertIn(
            f"Detail: {expected_detail}",
            out,
            f"sanitizer regressed (surviving '::' or un-collapsed newline); got: {out!r}",
        )
        err_line = next((ln for ln in out.splitlines() if "Detail:" in ln), None)
        self.assertIsNotNone(err_line, out)
        detail = err_line.split("Detail:", 1)[1].strip()
        self.assertNotIn("::", detail, f"non-idempotent sanitize leaked a live '::' token: {detail!r}")
        # The mutant that drops the newline collapse splits the payload across log lines: assert the
        # second half never becomes its own line (which would be GHA-parseable).
        self.assertFalse(
            any(ln.strip().startswith("second:") for ln in out.splitlines()),
            f"newline collapse regressed — attacker bytes started a new log line: {out!r}",
        )

    def test_abort_sentinel_precedes_missing_assembly_fail_closed(self):
        # A crashed host frequently DROPS an entire assembly's report, which presents as the
        # fail-closed "missing expected assembly" shape (exit 2), not merely low coverage. The
        # sentinel check must precede THAT path too, so a crash is reported as indeterminate
        # (exit 3 / re-run) rather than misattributed to a provenance failure. Reports omit
        # DeltaSharp.Executor AND the sentinel is present -> exit 3 must win.
        code, out = self._run(
            packages=[(n, 890, 1000) for n in _EXPECTED if n != "DeltaSharp.Executor"],
            raw_files={_ABORT_SENTINEL: "aborted on all 3 attempts"},
        )
        self.assertEqual(code, 3, out)
        self.assertIn("indeterminate", out.lower())

    def test_workflow_writes_the_sentinel_the_gate_reads(self):
        # The gate reads _ABORT_SENTINEL and the CI collect step WRITES it; a self-test cannot see
        # the YAML, so without this the two literals could drift and silently disable the #858 fix
        # (the gate would never fire). Pin the coupling to the EXACT redirection token: an ANCHORED
        # match, not a bare substring — a substring `assertIn("TestResults/.collect-aborted")` is
        # satisfied by a suffix-appending rename (`.collect-aborted.txt`, `.collect-aborted-v2`)
        # that the gate's exact-path os.path.exists would then MISS, silently disabling the fix while
        # the test stayed green (a round-2 mutation proved this bypass).
        ci_yml = os.path.join(_HERE, "..", "..", ".github", "workflows", "ci.yml")
        with open(ci_yml, encoding="utf-8") as handle:
            workflow = handle.read()
        self.assertRegex(
            workflow,
            rf">[ \t]+TestResults/{re.escape(_ABORT_SENTINEL)}(?:\s|$)",
            f"ci.yml must write the same sentinel the gate reads ({_ABORT_SENTINEL!r}) as an exact "
            "redirection target; a rename on either side (including a suffix append) silently "
            "disables the #858 coverage-abort resilience.",
        )

    def test_ci_yml_delegates_classification_to_the_single_source_script(self):
        # The retry loop must delegate the abort/normal decision to detect-collect-abort.sh (the ONE
        # testable place the crash signature lives). If a future edit re-inlines a `grep` in the YAML,
        # the anchored-marker + exit-code hardening (round-2) silently disappears with no red test.
        ci_yml = os.path.join(_HERE, "..", "..", ".github", "workflows", "ci.yml")
        with open(ci_yml, encoding="utf-8") as handle:
            workflow = handle.read()
        self.assertIn(
            "tools/coverage/detect-collect-abort.sh",
            workflow,
            "ci.yml must classify collection aborts via detect-collect-abort.sh (single source of the "
            "crash signature); an inline grep would drift from the tested classifier.",
        )


class DetectCollectAbortSelfTest(unittest.TestCase):
    """Unit-tests for detect-collect-abort.sh — the crash classifier the collect retry loop keys off.

    Exit 0 = ABORT (retry / write sentinel); exit 1 = NOT an abort (let the gate read the reports).
    This is the semantic heart of #858 and the most drift-prone piece, so it is pinned directly with
    canned (exit_code, log) pairs rather than left unverified inline in YAML.
    """

    def _classify(self, code, log_text=""):
        with tempfile.NamedTemporaryFile("w", suffix=".log", delete=False) as fh:
            fh.write(log_text)
            log_path = fh.name
        try:
            proc = subprocess.run(
                ["bash", _DETECT, str(code), log_path],
                capture_output=True,
                text=True,
            )
            return proc.returncode, proc.stdout + proc.stderr
        finally:
            os.unlink(log_path)

    def test_clean_run_is_never_an_abort_even_if_log_mentions_aborted(self):
        # exit 0 short-circuits: a PASSING test that logs the word "aborted" must not trip the retry
        # path (round-2 false-positive concern). Not an abort -> exit 1.
        code, out = self._classify(0, "The active test run was aborted (this is just test output)\n")
        self.assertEqual(code, 1, out)

    def test_ordinary_failure_with_complete_report_is_not_an_abort(self):
        # A plain test failure (exit 1, coverlet wrote complete reports, no crash banner) must be
        # gated normally, never retried/laundered -> exit 1.
        code, out = self._classify(1, "Failed!  - Failed:  3, Passed: 200, Skipped: 0\n")
        self.assertEqual(code, 1, out)

    def test_failure_mentioning_marker_mid_line_is_not_an_abort(self):
        # Anchoring: a failing assertion whose message merely CONTAINS a crash phrase mid-line must
        # not be misclassified as a host crash (else a real regression is laundered to indeterminate).
        code, out = self._classify(
            1, "  Assert.Equal() Failure: expected 'the active test run was aborted' banner\n"
        )
        self.assertEqual(code, 1, out)

    def test_host_crash_banner_at_line_start_is_an_abort(self):
        code, out = self._classify(
            1, "The active test run was aborted. Reason: Test host process crashed : Boom\n"
        )
        self.assertEqual(code, 0, out)

    def test_test_run_aborted_banner_is_an_abort(self):
        # Pin the 'test run aborted' alternative (a surviving mutant in round 3: removing it turned no
        # test red). Every listed banner phrase must have exactly one positive test so the alternation
        # cannot silently shrink and reintroduce #858 for a host that emits only this wording.
        code, out = self._classify(1, "Test Run Aborted.\n")
        self.assertEqual(code, 0, out)

    def test_aborting_the_test_run_banner_is_an_abort(self):
        # Pin the 'aborting( the)? test run' alternative (also a round-3 surviving mutant).
        code, out = self._classify(1, "Aborting the test run.\n")
        self.assertEqual(code, 0, out)

    def test_non_crash_line_starting_with_aborted_word_is_not_an_abort(self):
        # Negative fixture guarding against a future over-broadening of the alternation to a bare
        # 'aborted' term: a legitimate non-crash line that merely STARTS with "Aborted" must stay a
        # normal failure (exit 1), never a spurious abort.
        code, out = self._classify(1, "Aborted run summary: 0 crashes, 3 assertion failures\n")
        self.assertEqual(code, 1, out)

    def test_error_prefixed_crash_banner_is_an_abort(self):
        # dotnet often prefixes the crash line with `Error: `.
        code, out = self._classify(1, "Error: Test host process crashed\n")
        self.assertEqual(code, 0, out)

    def test_signal_death_without_banner_is_an_abort_sigkill(self):
        # A SILENT crash (OOM SIGKILL -> 137) truncates the report with NO banner; the prior
        # marker-only classifier missed this and #858 recurred. Signal death (>128) -> abort.
        code, out = self._classify(137, "")
        self.assertEqual(code, 0, out)

    def test_signal_death_without_banner_is_an_abort_sigsegv(self):
        code, out = self._classify(139, "some partial output before segfault\n")
        self.assertEqual(code, 0, out)

    def test_signal_boundary_128_is_not_an_abort(self):
        # The signal branch is `code > 128`. 128 itself is NOT a signal death; with no banner it must
        # stay a normal failure. Pins the boundary against an off-by-one refactor to `>= 128`.
        code, out = self._classify(128, "ordinary failure output\n")
        self.assertEqual(code, 1, out)

    def test_signal_boundary_129_is_an_abort(self):
        # 129 (SIGHUP) is the first signal-death code above the boundary -> abort even with no banner.
        code, out = self._classify(129, "")
        self.assertEqual(code, 0, out)

    def test_non_numeric_exit_code_degrades_fail_closed(self):
        # dotnet_rc always arrives numeric (PIPESTATUS[0]); defensively a malformed value must degrade
        # to banner-only classification (non-abort here), never crash the classifier under `set -u`.
        code, out = self._classify("nan", "ordinary failure output\n")
        self.assertEqual(code, 1, out)
        # ...and a real anchored banner with a garbage code is still an abort (fail-closed toward retry).
        code, out = self._classify("nan", "Test host process crashed\n")
        self.assertEqual(code, 0, out)
        # The numeric guard's job is to suppress the `[: integer expression expected` stderr a raw
        # `[ nan -eq 0 ]` would emit; assert stderr is clean so removing the guard turns this red.
        with tempfile.NamedTemporaryFile("w", suffix=".log", delete=False) as fh:
            fh.write("ordinary failure output\n")
            log_path = fh.name
        try:
            proc = subprocess.run(["bash", _DETECT, "nan", log_path], capture_output=True, text=True)
        finally:
            os.unlink(log_path)
        self.assertEqual(proc.stderr, "", f"non-numeric code must not emit stderr noise: {proc.stderr!r}")

    def test_non_signal_failure_with_missing_log_is_not_an_abort(self):
        # Defensive: a non-signal non-zero exit with no readable log is treated as a normal failure
        # (fail-closed toward gating the reports), not a spurious abort.
        proc = subprocess.run(
            ["bash", _DETECT, "1", os.path.join(tempfile.gettempdir(), "no-such-log-" + uuid.uuid4().hex)],
            capture_output=True,
            text=True,
        )
        self.assertEqual(proc.returncode, 1, proc.stdout + proc.stderr)

    def test_bad_arity_errors_out(self):
        # The arity guard must reject 0, 1, and 3 args (only <code> <log> is valid) with exit 2, so a
        # miswired caller fails loudly rather than silently mis-parsing. Exit 2 is non-abort to the
        # caller (the `if` in ci.yml treats only exit 0 as abort), so this is also fail-closed.
        for args in ([], ["1"], ["1", "a.log", "extra"]):
            proc = subprocess.run(["bash", _DETECT, *args], capture_output=True, text=True)
            self.assertEqual(proc.returncode, 2, f"args={args}: {proc.stdout + proc.stderr}")


if __name__ == "__main__":
    unittest.main(verbosity=2)
