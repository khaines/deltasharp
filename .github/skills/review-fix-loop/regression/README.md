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

```
{"ts":"…","pr":323,"round":2,"complexity":"Complex","seats":["architect","balanced","quality","security"],
 "specialists":[],"findings":{"blocking":0,"major":0,"minor":0},"redteam":"NO-MISS-CERTIFIED",
 "redteam_catches":1,"redteam_model":"gpt-5.5","gate":"PASS",
 "miss_classes":["C2-parity","fixer-diff"]}
```

Never write this under a tracked path.

## Miss-class ledger (curated — append when a class is folded forward)

| Date | Miss-class | Caught by | Folded into |
|------|-----------|-----------|-------------|
| 2026-06-28 | Reviewer withheld a star for inability to *execute* (file-view-only seat) → incoherent "4/5 with zero findings" | Human | rigor-battery C7 (shell-capable red-team mandatory); rating-rubric anti-impasse rule; red-team must use `general-purpose` |
| 2026-06-28 | Fix-induced regression: a fixer introduced a *forgeable* DCO bot-exemption (trusted client-settable author email) — caught a round late | Council R2 | red-team fixer-diff + C2 validation↔enforcement-parity hunt (catch in-round) |

> When you add a row, also update the battery / red-team hunt list so the gate — not just the
> ledger — enforces it.
