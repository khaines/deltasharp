Model values are the task tool's model names, which always override the `model:` line in a
persona's frontmatter. The full council shape:

| Seat | Model | Notes |
|---|---|---|
| Scout (Phase 1.6) | a cheap tier | Routes only; never scores or gates |
| 4 fixed lenses | a frontier Claude model | The voting spine |
| Specialist seats (≤3) | same as the spine | Never the gate's family |
| Red-team gate (Phase 8) | `gemini-3.1-pro-preview` | A **vendor no voting seat uses**; blind-first; shell-capable |

> **Models track the newest model in each tier.** Update these as the tiers advance, keeping
> the gate on a **frontier family distinct from every voting seat**.
>
> **The voting spine is Claude; the gate is Gemini.** The Phase 8 red-team runs on a
> **different vendor from every voting seat**, starts **blind** (it forms its own findings and
> runs its own C7 repros before it sees any seat verdict), and holds a **shell**. Gate
> decorrelation here is **vendor + information + execution** — all three axes, which is the
> strongest form this council supports.
>
> **This council is the vendor-decorrelated gate (2026-09).** The Claude Code council
> dispatches Claude models only, so its red-team decorrelates by *tier* (`fable` against an
> all-`opus` spine) and records the vendor axis as an opt-in out-of-band step that does not
> yet exist there. It exists here. For **protected-domain** changes (security, tenant
> isolation, privacy), running *this* skill is what discharges the vendor half of the gate;
> a Claude-council certification on tier decorrelation alone remains valid for everything
> else. Keep both councils' verdicts in the review record and say which gated.
>
> **What the vendor axis is and is not worth.** The 2026-07 record showed Claude and Gemini
> seats producing materially identical adversarial content, and the verdicts actually
> overturned came from **execution** (the 106-RED mutation experiment) and from a seat
> **willing to disagree** — protocol properties, not model properties. So vendor
> decorrelation is a real but *third* line of defense: it catches a family-wide blind spot
> that execution evidence and a blind first pass would both miss, and nothing else here does.
> Never trade blind-first or C7 execution away to get it.
>
> **Why the gate is not GPT.** The 2026-07 measurement stands (5/5 empty first responses,
> 4/5 never discharged C7); see `red-team.md` → Dispatch. Re-measure before reconsidering.
