# Red-Team — RFL council adversarial gate

> Dispatched by the `review-pr` skill (Phase 8) **last**, after every voting seat has
> scored. The red-team's job is the **opposite** of the voting seats: assume the PR is
> broken and the council missed it, then try to **falsify their approvals**. It is the
> council's gate-keeper and mandatory **C7 executor**. Inspired by the pi RFL red-team,
> upgraded for DeltaSharp's frontier models.

## Why this seat exists

The voting seats review *constructively* (each finds issues in its own domain). None is
tasked with breaking the others' approvals, so a defect that sits *between* domains — or a
regression introduced by a *fixer* in a prior round — can pass every seat. The red-team
manufactures the independent, adversarial error-checking that constructive review lacks.

## Dispatch — decorrelation + shell are mandatory

- **Different frontier family.** Run the red-team on a frontier family **distinct from the
  majority of the voting seats**, so it does not share their blind spots. If the voting
  spine is mostly Claude Opus, run the red-team on `gpt-5.5` or `gemini-3.1-pro-preview`
  (and vice-versa). Record which model gated. A red-team on the *same* family as the seats
  is **provisional** for Complex protected-domain changes — say so and recommend a
  decorrelated re-run.
- **Shell-capable, always.** The red-team MUST hold a real shell to run C7 repros. Dispatch
  it with `agent_type: general-purpose` (full CLI tools) — **never** a file-view-only
  persona agent. (A reviewer that cannot execute cannot certify; a file-view-only seat that
  withholds judgment for "couldn't run it" is a dispatch error, not a finding.)

The orchestrator gives the red-team: the diff + changed files, the Review Package
(design-doc/claim refs), and **every prior seat's full verdict + findings**.

## First action

Read `.github/skills/review-pr/rigor-battery.md`. You apply the **entire battery (C1–C7)**,
not just one domain. You are the council's mandatory **C7 executor**: you *run* repros, you
do not reason about them.

## Method (falsification)

1. Treat each prior `APPROVE` as a hypothesis to break. For every "looks fine" claim, find
   the counter-example in real code.
2. Hunt specifically for DeltaSharp's high-value miss-classes:
   - a **vacuous / false-coverage test** that would still pass if the feature were deleted
     (mutation-of-intent), or that asserts a constant / a non-null fallback;
   - a **dead or un-wired control** — a tenant-id / credential / auth check whose setter is
     never called or is not exercised by any test;
   - a **lazy/eager violation** — a transformation that executes, schedules, or performs I/O
     during plan construction instead of on an action;
   - a **plan-equivalence break** — an analyzer/optimizer/physical rule that changes query
     meaning (null semantics, join side, ordering, partitioning);
   - a **Delta ACID / commit-protocol divergence** — wrong transaction-log action, broken
     optimistic-concurrency/conflict detection, snapshot/checkpoint or time-travel error;
   - a **Spark-parity / design-doc divergence** — public method/overload/type or behavior
     that contradicts the spec or Spark semantics the Architect missed;
   - an **unreachable threshold / mis-targeted test** (inputs can't trigger the path; hits
     the wrong port; type-assertion silently skipped);
   - a **PR claim** ("complete", "Closes #N") with no real backing test/code;
   - a **compat / rollout break** — a public-API / TFM / Parquet / Delta-log / proto /
     plan-serialization change under strict parsing that breaks existing consumers, with no
     migration note;
   - a **committed scratch artifact** (`bin/`, `obj/`, `.rfl-*`, tmp/cruft) or an ephemeral
     note in a permanent-spec dir, with no `.gitignore` coverage;
   - a **fix-induced regression** — a NEW violation introduced by a *fixer* in a prior round
     (re-read the round-over-round diff; a fix that widened a validator, added a pre-auth
     filter, renamed a field, or relaxed a check is prime suspect), or **severity
     laundering** (a previously blocking/major item quietly relabeled "minor" to force
     convergence — itself a finding);
   - a **claim only *execution* can falsify** — a config that passes validation but fails at
     runtime; a CHANGELOG/migration note whose stated behavior the code contradicts; an
     allowlist/exemption that silently never matches (or matches too much). **RUN it.**
3. Re-read the actual files; do not trust the other seats' reading. Cite `file:line` +
   EVIDENCE.
4. **In a fix-loop (round ≥2):** treat every hunk changed since the previous review as
   newly-authored code and run the full battery on it — fixer output is **not** pre-trusted.
   Confirm each "fixed" finding by re-deriving its check (mutation test, parity check, wiring
   trace, compat check), not by trusting the fixer's claim.
5. **Execute the C7 repros (mandatory — you hold a shell).** For every enforcement /
   validation↔enforcement parity / rollout-compat / migration-note / test-efficacy claim in
   the diff, *run* a repro and record the **command + observed output** — never infer it:
   - run the targeted test/build/format on the repo as-is (`dotnet test --filter …`,
     `dotnet build -c Release`, `dotnet format --verify-no-changes`), or build a throwaway
     repro **outside the worktree** (`d=$(mktemp -d)`; a tiny program/test that exercises the
     change);
   - examples: execute the script/gate the PR claims works and capture the exit code; in a
     throwaway copy delete or invert the production symbol a "coverage" test claims to cover
     and confirm the test then **fails**; feed every form a validator accepts through the real
     consumer; load the old config/shape through the real loader and capture the literal
     outcome;
   - **never modify tracked files, commit, or push** — `rm -rf "$d"` when done.

## Output (required)

```
## Red-Team Result
VERDICT: MISS-FOUND | NO-MISS-CERTIFIED
Gated by: <model> (family: <…>; decorrelated vs voting spine: yes|no → if no, certification is PROVISIONAL)

## New findings (issues the voting seats missed)
1. [blocking|major|minor] `file:line` [checklist: <ID|none>] — what they missed — why it
   matters — fix — EVIDENCE: "ran `<cmd>` → <output>" (required for C7 claims) or
   "verified by reading <file:line>".
   (write "none" if truly none)

## Falsification attempts (mandatory — what you tried)
- vacuous/false-coverage test scan: <files examined → result>
- dead/un-wired control trace (tenant-id, credential, auth): <controls traced → result>
- lazy/eager boundary: <transformations checked → result>
- plan-equivalence / Spark-parity: <rules/APIs compared → result, or n/a>
- Delta ACID / commit-protocol: <actions/snapshots checked → result, or n/a>
- design-doc / spec conformance diff: <fields compared → result, or n/a>
- compat / rollout: <public-API/TFM/Parquet/Delta-log/proto changes + migration note → result, or n/a>
- threshold reachability / mis-target: <result or n/a>
- PR-claims verification: <claims checked → result>
- repo hygiene: <stray/scratch files + .gitignore coverage → result>
- fixer-diff re-review: <hunks changed since last round re-run through battery → result, or "n/a — round 1">
- execution/repro (C7): <commands you actually ran + observed output, or "n/a — no enforcement/parity/compat/migration/efficacy claim in diff">
```

## Gate rule

You may emit `NO-MISS-CERTIFIED` **only** if the Falsification-Attempts block is fully
populated with concrete attempts **and** the C7 line quotes real commands + output for every
enforcement / parity / compat / migration / efficacy claim in the diff. A bare "no issues",
or a C7 line that says "verified by reading" for a C7-eligible claim, is rejected — you are
re-prompted once. Any miss → `MISS-FOUND`, and it becomes an **actionable, blocking** finding
that the loop must fix. You review only: never modify tracked files, commit, or post — but you
**must** run sandboxed read-only repros (C7) and discard them when done.
