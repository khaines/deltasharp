#!/usr/bin/env python3
"""Software-composition-analysis (SCA) gate for DeltaSharp (STORY-00.3.1, #107).

Parses the JSON emitted by ``dotnet list package --vulnerable --include-transitive
--format json`` and FAILS (exit 1) when any dependency carries a known vulnerability at
or above a documented severity threshold, unless the finding is covered by an unexpired
suppression that carries scope, reason, owner, and expiry.

Design mirrors the coverage gate (``tools/coverage/coverage-gate.py``): the policy lives
in checked-in configuration (``tools/security/sca-policy.json``) so local runs and CI
reach the same verdict, and the gate is Python-3 standard-library only (no third-party
dependency, no network) so it is deterministic and offline. ``dotnet list package
--vulnerable`` always exits 0 (it is a reporting command), so THIS gate — not the dotnet
CLI — is what turns a vulnerability above threshold into a red CI check.

This complements, and does not replace, the build-time NuGet audit already wired in
``Directory.Build.props`` (NU1901/NU1902 LOW/MODERATE are warnings; NU1903/NU1904
HIGH/CRITICAL are build-breaking under TreatWarningsAsErrors). The build audit fails the
compile; this gate produces the explicit, reviewable report (severity + package identity
+ advisory) and enforces the same threshold with auditable suppressions.

Usage:
    # Normal gate (parse a report and enforce the policy):
    dotnet list package --vulnerable --include-transitive --format json > vuln.json
    python3 tools/security/sca-gate.py --input vuln.json

    # Prove the gate's own threshold/suppression/expiry logic before trusting it:
    python3 tools/security/sca-gate.py --selftest

Exit codes: 0 = pass (no blocking findings), 1 = blocking finding(s) or bad input/policy.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import sys
from pathlib import Path
from typing import Any

# Canonical severity ranking. dotnet emits "Low"/"Moderate"/"High"/"Critical"; GitHub
# advisories occasionally say "Medium" — treat it as Moderate. Unknown severities are
# ranked ABOVE Critical so an unrecognized (therefore un-triaged) severity fails closed.
_SEVERITY_RANK = {"low": 1, "moderate": 2, "medium": 2, "high": 3, "critical": 4}
_UNKNOWN_RANK = 99
_REQUIRED_SUPPRESSION_FIELDS = ("package", "scope", "reason", "owner", "expiry")

DEFAULT_POLICY = Path(__file__).with_name("sca-policy.json")


class GateError(Exception):
    """Raised for malformed input or policy — the gate fails closed on these."""


def _rank(severity: str) -> int:
    return _SEVERITY_RANK.get(severity.strip().lower(), _UNKNOWN_RANK)


def _annotate(level: str, message: str) -> None:
    """Emit a GitHub Actions workflow annotation (no-op-safe off CI)."""
    print(f"::{level}::{message}")


class Finding:
    """A single (package, framework, vulnerability) tuple discovered in the report."""

    def __init__(self, project: str, framework: str, kind: str, package: str,
                 version: str, severity: str, advisory: str) -> None:
        self.project = project
        self.framework = framework
        self.kind = kind  # "top-level" | "transitive"
        self.package = package
        self.version = version
        self.severity = severity
        self.advisory = advisory

    @property
    def rank(self) -> int:
        return _rank(self.severity)

    def describe(self) -> str:
        return (f"{self.severity:<8} {self.package}@{self.version} "
                f"({self.kind}, {self.framework}) — {self.advisory or 'no advisory url'}")


def parse_report(doc: dict[str, Any]) -> list[Finding]:
    """Flatten the ``dotnet list package --vulnerable`` JSON into a list of Findings.

    A project with no vulnerabilities appears as ``{"path": ...}`` with no ``frameworks``
    key, so the absence of vulnerabilities is represented as an empty result — never an
    error.
    """
    if not isinstance(doc, dict) or "projects" not in doc:
        raise GateError("report JSON has no 'projects' array — is this "
                        "`dotnet list package --vulnerable --format json` output?")
    findings: list[Finding] = []
    for project in doc.get("projects", []):
        path = project.get("path", "<unknown project>")
        for fw in project.get("frameworks", []) or []:
            framework = fw.get("framework", "<unknown tfm>")
            for kind, key in (("top-level", "topLevelPackages"),
                              ("transitive", "transitivePackages")):
                for pkg in fw.get(key, []) or []:
                    pid = pkg.get("id", "<unknown>")
                    ver = pkg.get("resolvedVersion", pkg.get("requestedVersion", "?"))
                    for vuln in pkg.get("vulnerabilities", []) or []:
                        severity = str(vuln.get("severity", "unknown"))
                        advisory = str(vuln.get("advisoryurl",
                                                vuln.get("advisoryUrl", "")))
                        findings.append(Finding(path, framework, kind, pid, ver,
                                                severity, advisory))
    return findings


def load_policy(path: Path) -> dict[str, Any]:
    try:
        policy = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise GateError(f"policy file not found: {path}") from exc
    except json.JSONDecodeError as exc:
        raise GateError(f"policy file is not valid JSON: {path}: {exc}") from exc
    if policy.get("failOnSeverity", "high").lower() not in _SEVERITY_RANK:
        raise GateError(f"policy failOnSeverity must be one of "
                        f"{sorted(set(_SEVERITY_RANK))}")
    _validate_suppressions(policy.get("suppressions", []))
    return policy


def _validate_suppressions(suppressions: list[Any]) -> None:
    """Every suppression MUST carry scope/reason/owner/expiry (AC #107d). A malformed
    suppression fails the gate closed so an incomplete waiver cannot silently hide a
    vulnerability."""
    if not isinstance(suppressions, list):
        raise GateError("policy 'suppressions' must be an array")
    for i, s in enumerate(suppressions):
        if not isinstance(s, dict):
            raise GateError(f"suppression #{i} is not an object")
        missing = [f for f in _REQUIRED_SUPPRESSION_FIELDS if not s.get(f)]
        if missing:
            raise GateError(f"suppression #{i} ({s.get('package', '?')}) is missing "
                            f"required field(s): {', '.join(missing)}")
        try:
            _dt.date.fromisoformat(str(s["expiry"]))
        except ValueError as exc:
            raise GateError(f"suppression #{i} ({s['package']}) has an invalid "
                            f"expiry (expected YYYY-MM-DD): {s['expiry']}") from exc


def _versions_match(spec: Any, version: str) -> bool:
    """A suppression's ``versions`` is ``"*"`` (any) or a comma-separated exact list."""
    if spec in (None, "*", ""):
        return True
    wanted = {v.strip() for v in str(spec).split(",") if v.strip()}
    return version in wanted


def find_suppression(finding: Finding, suppressions: list[dict[str, Any]],
                     today: _dt.date) -> tuple[dict[str, Any] | None, list[dict]]:
    """Return (active_suppression_or_None, expired_matches). A suppression matches a
    finding when the package id matches (case-insensitively), the version is in range,
    and the advisory (if pinned) matches. An expired suppression does NOT suppress —
    the finding re-surfaces — which is what makes ``expiry`` a real control."""
    expired: list[dict] = []
    for s in suppressions:
        if s["package"].strip().lower() != finding.package.strip().lower():
            continue
        if not _versions_match(s.get("versions"), finding.version):
            continue
        pinned = s.get("advisory")
        if pinned and finding.advisory and pinned.strip() != finding.advisory.strip():
            continue
        if _dt.date.fromisoformat(str(s["expiry"])) < today:
            expired.append(s)
            continue
        return s, expired
    return None, expired


def run_gate(report_path: Path, policy_path: Path, summary_out: Path | None,
             today: _dt.date | None = None) -> int:
    today = today or _dt.date.today()
    try:
        doc = json.loads(report_path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise GateError(f"report file not found: {report_path}") from exc
    except json.JSONDecodeError as exc:
        raise GateError(f"report file is not valid JSON: {report_path}: {exc}") from exc

    policy = load_policy(policy_path)
    fail_rank = _rank(policy.get("failOnSeverity", "high"))
    fail_label = str(policy.get("failOnSeverity", "high")).lower()
    suppressions = policy.get("suppressions", [])

    findings = parse_report(doc)
    blocking: list[Finding] = []
    suppressed: list[tuple[Finding, dict]] = []
    below: list[Finding] = []
    expired_seen: list[tuple[Finding, dict]] = []

    for f in findings:
        active, expired = find_suppression(f, suppressions, today)
        for e in expired:
            expired_seen.append((f, e))
        if active is not None:
            suppressed.append((f, active))
            continue
        if f.rank >= fail_rank:
            blocking.append(f)
        else:
            below.append(f)

    lines: list[str] = []

    def out(text: str = "") -> None:
        print(text)
        lines.append(text)

    out(f"SCA gate — threshold: fail on {fail_label.upper()} and above")
    out(f"  scanned findings: {len(findings)} | blocking: {len(blocking)} | "
        f"suppressed: {len(suppressed)} | below-threshold: {len(below)}")
    out()

    if blocking:
        out("BLOCKING vulnerabilities (>= threshold, not suppressed):")
        for f in blocking:
            out(f"  ✗ {f.describe()}")
            _annotate("error", f"vulnerable dependency {f.package}@{f.version} "
                               f"[{f.severity}] {f.advisory}")
    if suppressed:
        out("Suppressed (accepted risk, unexpired waiver):")
        for f, s in suppressed:
            out(f"  ~ {f.describe()}")
            out(f"      reason={s['reason']} owner={s['owner']} "
                f"scope={s['scope']} expiry={s['expiry']}")
    if below:
        out("Below threshold (reported, not blocking):")
        for f in below:
            out(f"  · {f.describe()}")
    for f, s in expired_seen:
        _annotate("warning", f"EXPIRED SCA suppression for {s['package']} "
                            f"(expiry {s['expiry']}, owner {s['owner']}) no longer "
                            f"suppresses {f.package}@{f.version}")

    if not findings:
        out("No known-vulnerable packages reported. ✓")

    out()
    if blocking:
        out(f"RESULT: FAIL — {len(blocking)} vulnerability(ies) at or above "
            f"{fail_label.upper()}.")
        _annotate("error", f"SCA gate FAIL — {len(blocking)} vulnerability(ies) "
                          f">= {fail_label.upper()}")
        result = 1
    else:
        out("RESULT: PASS")
        result = 0

    if summary_out is not None:
        summary_out.parent.mkdir(parents=True, exist_ok=True)
        summary_out.write_text("```\n" + "\n".join(lines) + "\n```\n", encoding="utf-8")

    return result


# --------------------------------------------------------------------------------------
# Self-test: prove the threshold / suppression / expiry contract before trusting the gate
# on real data (invoked as its own CI step, mirroring the coverage-gate self-test).
# --------------------------------------------------------------------------------------
def _selftest() -> int:
    import tempfile

    failures: list[str] = []

    def check(name: str, cond: bool) -> None:
        if cond:
            print(f"  ok    {name}")
        else:
            print(f"  FAIL  {name}")
            failures.append(name)

    def report_with(severity: str) -> dict:
        return {"version": 1, "projects": [{"path": "P.csproj", "frameworks": [
            {"framework": "net8.0", "topLevelPackages": [
                {"id": "Vuln.Pkg", "resolvedVersion": "1.0.0", "vulnerabilities": [
                    {"severity": severity,
                     "advisoryurl": "https://github.com/advisories/GHSA-test"}]}]}]}]}

    def policy_with(supps: list) -> dict:
        return {"failOnSeverity": "high", "suppressions": supps}

    future = (_dt.date.today() + _dt.timedelta(days=30)).isoformat()
    past = (_dt.date.today() - _dt.timedelta(days=1)).isoformat()

    def run(report: dict, policy: dict) -> int:
        import contextlib
        import io
        with tempfile.TemporaryDirectory() as d:
            rp = Path(d) / "r.json"
            pp = Path(d) / "p.json"
            rp.write_text(json.dumps(report), encoding="utf-8")
            pp.write_text(json.dumps(policy), encoding="utf-8")
            # Silence the gate's own report so the self-test log shows only case results.
            with contextlib.redirect_stdout(io.StringIO()):
                return run_gate(rp, pp, None)

    valid_supp = {"package": "Vuln.Pkg", "versions": "*", "scope": "repository",
                  "reason": "test", "owner": "@x", "expiry": future}

    check("HIGH fails", run(report_with("High"), policy_with([])) == 1)
    check("CRITICAL fails", run(report_with("Critical"), policy_with([])) == 1)
    check("MODERATE passes (below threshold)",
          run(report_with("Moderate"), policy_with([])) == 0)
    check("unknown severity fails closed",
          run(report_with("Bogus"), policy_with([])) == 1)
    check("empty report passes", run({"version": 1, "projects": [
        {"path": "P.csproj"}]}, policy_with([])) == 0)
    check("unexpired suppression suppresses HIGH",
          run(report_with("High"), policy_with([valid_supp])) == 0)
    expired_supp = dict(valid_supp, expiry=past)
    check("expired suppression does NOT suppress",
          run(report_with("High"), policy_with([expired_supp])) == 1)

    # Malformed policy / input must fail closed (raise GateError).
    def raises(fn) -> bool:
        try:
            fn()
            return False
        except GateError:
            return True

    incomplete = {"package": "Vuln.Pkg", "reason": "x"}  # missing scope/owner/expiry
    check("incomplete suppression rejected",
          raises(lambda: run(report_with("High"), policy_with([incomplete]))))
    check("bad expiry rejected", raises(lambda: run(report_with("High"),
          policy_with([dict(valid_supp, expiry="not-a-date")]))))

    print()
    if failures:
        print(f"SCA gate self-test FAILED ({len(failures)} case(s)).")
        return 1
    print("SCA gate self-test passed.")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="DeltaSharp SCA vulnerability gate.")
    parser.add_argument("--input", type=Path,
                        help="path to `dotnet list package --vulnerable --format json`")
    parser.add_argument("--policy", type=Path, default=DEFAULT_POLICY,
                        help=f"policy file (default: {DEFAULT_POLICY.name})")
    parser.add_argument("--summary-out", type=Path,
                        help="optional markdown summary output path")
    parser.add_argument("--selftest", action="store_true",
                        help="run the gate's contract self-test and exit")
    args = parser.parse_args(argv)

    if args.selftest:
        return _selftest()
    if args.input is None:
        parser.error("--input is required unless --selftest is given")
    try:
        return run_gate(args.input, args.policy, args.summary_out)
    except GateError as exc:
        _annotate("error", f"SCA gate could not run: {exc}")
        print(f"SCA gate ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
