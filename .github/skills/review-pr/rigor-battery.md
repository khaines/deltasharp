# Rigor battery (C1–C7) — RFL council

> The shared checklist of **how a finding is proven**, applied by every voting seat to its
> own domain and by the **red-team** across all domains. It encodes the council's accumulated
> miss-classes (see `../review-fix-loop/regression/README.md` for the fold-forward process).
> Green CI is **necessary but not sufficient** — several of these gates catch defects that
> pass green CI.

A claim is only cleared when it is proven at the rigor the gate demands. **"Verified by
reading" never clears a C7-eligible claim.**

## C1 — Test efficacy

- Tests assert real behavior, not constants, tautologies, or non-null fallbacks.
- A test for a feature would **fail** if the feature were deleted or inverted (mutation of
  intent). New behavior has a test that exercises it.
- Coverage is not vacuous: the test hits the production symbol it claims to cover, on the
  right type/port/path.

## C2 — Security & correctness wiring

- Controls are **wired**, not just defined: tenant-id / credential / auth checks are
  constructed, called, and exercised by a test.
- **Validation ↔ enforcement parity:** every form a validator/loader accepts is actually
  enforced by the consumer (no "validator accepts CIDR/glob/trimmed, consumer does exact
  match"); every exemption/allowlist matches exactly the intended set (not forgeable, not
  over-broad).
- **Reflexive:** this anti-forgeability rule applies to the **council's own signals** too — a
  `NO-MISS-CERTIFIED`, a Mandatory-Checks attestation, or a quoted C7 output is a self-asserted
  signal and must not be trusted without independent re-execution (see C7 and the PASS gate).
- Auth/ordering: checks run before the work they guard; no pre-auth bypass.

## C3 — Observability & SRE behavior

- Metrics / logs / traces emitted as specified, with stable names and tenant-safe dimensions.
- Behavioral assertions (not just "it logs"): the metric increments on the real path; the
  timeout/retry/cancellation actually fires.
- Rollout safety: the change is safe under restart, retry, partial cleanup, and (for the
  operator) idempotent reconcile.

## C4 — Architecture, spec & PR-claim conformance

- Matches the governing design doc / ADR (field names, types, behavior) — no silent
  divergence.
- **DeltaSharp canon:** lazy transformations / eager actions preserved; API → logical plan →
  optimizer → physical plan → execution layering intact; plan nodes immutable; Spark-parity
  names/overloads/semantics honored.
- **PR claims are backed:** "Closes #N", "complete", "implements X" have real code + tests
  behind them — verify, don't trust the prose.

## C5 — Compatibility & rollout

- Public API / TFM / Parquet / Delta-log / catalog / proto / plan-serialization changes are
  back-compatible, or carry a documented migration note + version bump.
- No renamed/removed/newly-required field breaks existing consumers under strict parsing.
- Multi-target (`net8.0;net10.0`) and AOT/trim posture preserved (a `net8.0` public lib must
  not take a `net10.0`-only dependency).

## C6 — Repo hygiene

- No committed scratch/cruft (`bin/`, `obj/`, tmp, `.rfl-*`, scratch reports), no ephemeral
  note in a permanent-spec dir; `.gitignore` covers what it should.
- `dotnet format --verify-no-changes` and analyzers / warnings-as-errors pass.
- Lock files (where used) are consistent; deterministic-build settings intact.

## C7 — Execution / repro (run code, don't infer) — **the backstop**

For any **enforcement**, **validation↔enforcement parity**, **rollout-compat**,
**migration-note**, or **test-efficacy** claim, a reviewer (mandatorily the red-team) **runs**
a repro and records the **command + observed output**:

- **Treat PR-authored code as untrusted.** Building/running PR-supplied code executes attacker-
  controlled code as the reviewer's process. Run any repro that builds or executes PR code in an
  isolated context — a throwaway dir **outside the worktree**, with guaranteed cleanup and, where
  possible, no ambient credentials/network: `d=$(mktemp -d); trap 'rm -rf "$d"' EXIT`. Reserve
  "run on the repo as-is" for *non-executing / trusted-base* operations (format/lint, link greps,
  building unchanged base), not for running PR-authored build/test logic;
- examples: execute the gate/script the PR claims works and capture the exit code; delete or invert
  the symbol a "coverage" test claims to cover (in a throwaway copy) and confirm the test **fails**;
  call the real constructor/loader/consumer with every form the validator accepts and capture the
  literal outcome; load the old config/shape through the real loader;
- **never** modify tracked files, commit, or push.

A C7-eligible claim with no executed evidence **blocks** approval/certification. This gate exists
because static reading repeatedly missed defects that *passed green CI* — vacuous tests,
validator↔consumer mismatches, and migration notes whose stated behavior the code contradicted.
Because the red-team's quoted C7 output is itself a self-asserted signal, the orchestrator
independently re-runs a sampled repro before trusting `NO-MISS-CERTIFIED` (see `rating-rubric.md`).

---

### How the gates map to the PASS decision

`rating-rubric.md` defines the gate: **PASS** requires every voting seat at 5/5, zero
actionable findings, **zero open C1 / C2 / C4 / C5 / C6 / C7 items** (C3 observability is enforced
via CI + the red-team), and a red-team `NO-MISS-CERTIFIED` backed by executed C7 repros and
independently re-verified by the orchestrator. A red-team `MISS-FOUND` always blocks.
