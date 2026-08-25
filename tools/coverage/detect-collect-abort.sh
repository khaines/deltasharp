#!/usr/bin/env bash
# Classify one `dotnet test` coverage-collection attempt as an instrumentation ABORT
# (the test host CRASHED, truncating the Cobertura reports — #858) versus a normal completion
# (a clean pass, or an ordinary test failure that still wrote complete reports).
#
# This is the SINGLE SOURCE OF TRUTH for the crash signature the collect retry loop keys off
# (ci.yml). It is deliberately a standalone script so it is unit-testable in isolation
# (coverage-gate-selftest.py feeds it canned exit-code/log pairs) — the classifier is the
# semantic heart of #858 and the most drift-prone piece, so it must not live only inline in YAML.
#
# Usage:  detect-collect-abort.sh <dotnet_exit_code> <log_file>
# Exit 0 = ABORT  -> the caller should retry (or, when attempts are exhausted, write the sentinel).
# Exit 1 = NOT an abort -> the caller should stop and let the coverage gate read the reports.
#
# Fail-closed by construction: the ONLY consequence of an abort verdict is a retry and, ultimately,
# the gate reporting exit 3 (indeterminate = a FAIL that asks for a re-run). No classification here
# can turn a real failure into a passing gate — worst case it converts a fail into a re-run.
set -u

if [ "$#" -ne 2 ]; then
  echo "usage: detect-collect-abort.sh <dotnet_exit_code> <log_file>" >&2
  exit 2
fi

code="$1"
log="$2"

# dotnet's exit code always arrives numeric (the caller passes PIPESTATUS[0]); guard defensively so a
# malformed/empty value degrades to fail-closed banner-only classification (code=1 is non-zero and
# non-signal) instead of erroring `[: integer expression expected` under `set -u`. The self-comparison
# is a numeric test that fails (stderr suppressed) unless "$code" is an integer.
[ "$code" -eq "$code" ] 2>/dev/null || code=1

# A clean run (exit 0) is NEVER an abort, regardless of anything a passing test printed to the log.
# This short-circuit is what stops a passing test that merely logs the word "aborted" from tripping
# the retry path (the round-2 red-team / SRE / security false-positive concern).
if [ "$code" -eq 0 ]; then
  exit 1
fi

# Killed by a signal: a shell reports 128+N for signal N. SIGABRT(134), SIGKILL(137 — e.g. an OOM
# kill), SIGSEGV(139), SIGBUS(138), SIGTERM(143 — runner-initiated kill) all truncate the report set
# with NO "aborted" banner in the log. The prior marker-only classifier missed these SILENT crashes
# and the #858 bug recurred; treat any signal death as an abort so the sentinel/indeterminate path
# engages. (Known non-signal gap: GNU `timeout(1)` reports 124 on a kill, which is NOT >128 — if a
# `timeout` wrapper is ever added around `dotnet test`, add 124 here and a matching self-test row.)
if [ "$code" -gt 128 ]; then
  exit 0
fi

# Otherwise (a non-zero, non-signal exit) require an explicit VSTest abort banner, anchored to the
# START of a line (allowing a leading `Error: ` prefix and indentation) and matched case-insensitively.
# Anchoring is what distinguishes a genuine host-crash announcement from an ordinary failing test that
# happens to print one of these phrases mid-message: an ordinary test failure writes COMPLETE reports,
# so it must fall through to exit 1 and be gated normally (never laundered into an indeterminate).
#
# INHERENT, ACCEPTED LIMITATION (round-2/3 red-team): a *failing* test (exit 1) that deliberately
# prints one of these banners AT LINE START is classified as an abort and, after the bounded retries,
# reported as indeterminate (gate exit 3). This is fail-CLOSED — exit 3 still blocks the merge, it can
# never launder a real failure into a PASS — and it is not fully avoidable: a real host crash under
# `dotnet test` frequently exits 1 with exactly this banner (the documented #858 trigger), so dropping
# the exit-1 banner path to reject the spoof would reintroduce the very false-negative #858 fixes.
if [ ! -f "$log" ]; then
  exit 1
fi
if grep -qiE '^[[:space:]]*(error:[[:space:]]*)?(the active test run was aborted|test host process crashed|test run aborted|aborting( the)? test run)' "$log"; then
  exit 0
fi

exit 1
