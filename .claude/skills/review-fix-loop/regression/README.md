# Regression & fold-forward — RFL council learning loop

> How the council gets **better over time** instead of re-making the same misses. Modeled on
> the pi RFL validation-log / regression loop, adapted for DeltaSharp.

## The principle: fold forward, not last-war

When something slips past the council and is caught later — by the **red-team**, by an
**external/frontier review**, or by a **production bug** — do not just fix the instance. Add
the **class** as a new gate so the council can never miss that *kind* of defect again:

1. Identify the **miss-class** (e.g. "validator accepts a form the consumer doesn't enforce",
   "vacuous test that passes green CI", "fix-induced regression from a prior round").
2. Fold it into the durable rigor: add or sharpen a bullet in
   [`../../review-pr/rigor-battery.md`](../../review-pr/rigor-battery.md) (C1–C7) and/or the
   relevant checklist in `docs/engineering/checklists/`, and add it to the red-team's
   miss-class hunt list in [`../../review-pr/red-team.md`](../../review-pr/red-team.md).
3. Record it in the ledger below so the trend is visible.

## Per-round miss-class capture

Each review-fix-loop round records, in the **final progression report** posted to the PR, the
miss-classes the red-team (or a later round) caught that the voting seats missed. That report
is the durable per-PR record — there is no separate per-run log file committed to the repo
(keeping the tree clean is itself C6).

Optionally, when running locally, the orchestrator may append one JSON line per round to a
**gitignored / out-of-tree** scratch path for trend analysis, e.g.:

```json
{"ts":"2026-09-06T12:00:00Z","pr":323,"round":2,"complexity":"Complex","seats":["architect","balanced","quality","security"],"specialists":[],"findings":{"critical":0,"high":0,"medium":0,"low":0,"info":0},"redteam":"NO-MISS-CERTIFIED","redteam_catches":1,"redteam_model":"fable","redteam_blind_first":true,"redteam_forked":false,"redteam_dispatched":"2026-09-06T11:42:30Z","redteam_blind_returned":"2026-09-06T11:58:12Z","redteam_verdicts_released":"2026-09-06T11:59:04Z","gate":"PASS","miss_classes":["C2-parity","fixer-diff"]}
```

Never write this under a tracked path.

## Miss-class ledger (curated — append when a class is folded forward)

| Date (UTC) | PR | Miss-class | Caught by | Folded into |
|------------|-----|-----------|-----------|-------------|
| 2026-06-28 | #323 | Reviewer withheld a star for inability to *execute* (file-view-only seat) → incoherent "4/5 with zero findings" | Human | rigor-battery C7 (shell-capable red-team mandatory); rating-rubric anti-impasse rule; red-team must use `general-purpose` |
| 2026-06-28 | #323 | Fix-induced regression: a fixer introduced a *forgeable* DCO bot-exemption (trusted client-settable author email) — caught a round late | Council R2 | red-team fixer-diff + C2 validation↔enforcement-parity hunt (catch in-round) |
| 2026-06-28 | #324 | Council trusted its own forgeable self-asserted signals (`NO-MISS-CERTIFIED` / attestation / quoted C7 output) — the #323 class reflexively | Dogfood council (Security) | rating-rubric PASS gate: orchestrator independently re-runs a sampled C7 repro; rigor-battery C2 reflexive bullet |
| 2026-06-28 | #324 | Process self-consistency: red-team output format broke the loop parser; fix-commit template lacked `-s` (DCO); `.gitignore` lacked `.rfl-*` | Dogfood council + red-team | canonical severities in `red-team.md`; `git commit -s` template; `.gitignore .rfl-*` |
| 2026-09-07 | #901 | **Tooling-config trust boundary**: a gate that polices a startup-config surface (`.claude/`, `.mcp.json`) by *filesystem* walk while git can track a symlink / gitlink / case- or Unicode-folded spelling that is absent or empty in CI yet loads on a developer machine; and a query that trusts ambient `GIT_*` env, a pathspec-magic prefix, or `.lower()` where the filesystem folds `casefold()` | Red-team (fable), eight consecutive certification rounds; two seats attested closure on partial evidence | red-team hunt list "config-surface bypass" bullet; rigor-battery C2/C7 already required execution — the seats' miss was testing one half (index or walk) of a two-sided claim |
| 2026-09-07 | #901 | **Half-closed fix**: a fold/normalisation fix applied to the index scan but not to the walk (`ſkill.md`), and a regression where a coverage failure *discarded* findings git had already returned — each certified 5/5 by two seats who executed only the shape the fixer named | Red-team fixer-diff re-review | red-team hunt list: "for every fold/scope/coverage claim execute BOTH halves and the previous tip's fixture set"; seats re-run the previous round's repro before closing a finding |
| 2026-09-07 | #901 | **Selftest that cannot lose**: `all assertions passed` with no count, guarded fixture groups silently reducing coverage, and a count floor that disarmed on any skip | Quality + red-team | selftest prints executed/skipped counts; CI passes `--require-full-coverage`; rubric: a green selftest must state what ran |

> When you add a row, also update the battery / red-team hunt list so the gate — not just the
> ledger — enforces it.
