- **Different VENDOR from every voting seat.** Run the red-team on
  **`gemini-3.1-pro-preview`** (Gemini 3.1 Pro) — a frontier family **no voting seat uses**.
  Every voting seat (Architect/Balanced/Quality/Security + specialists) runs on Claude, so the
  gate does not share their family blind spots. Record which model gated. A red-team on a
  voting seat's family is **provisional** and does **not** satisfy the gate for
  protected-domain changes — say so and require a decorrelated re-run or a documented human
  waiver.
- **This is the decorrelated gate.** The Claude Code council cannot dispatch a non-Claude
  model in-session, so its red-team decorrelates by *tier* (`fable` against an all-`opus`
  spine) and records vendor decorrelation as an opt-in, out-of-band step that does not yet
  exist. **Here it does exist.** Copilot can dispatch across vendors, so this council is the
  **vendor-decorrelated re-run** for protected-domain changes (security, tenant isolation,
  privacy): when the Claude council certifies a protected-domain change on tier
  decorrelation alone, re-running *this* skill discharges the vendor half of the gate.
- **Blind-first.** The red-team is dispatched with the diff, changed files, and Review Package
  **only**. It produces its **Blind findings** block and runs its C7 repros **before** the
  orchestrator releases any seat verdict, rating, or finding to it; the orchestrator then continues
  the same agent with the seats' verdicts for the falsification pass. A red-team that saw seat
  verdicts before returning its blind block is **provisional** (same consequence as above).
  Blind-first and execution are required *in addition to* vendor decorrelation here, not
  instead of it — the 2026-07 record found that the verdicts actually overturned came from
  **execution** and from a seat **willing to disagree**, so the vendor axis buys least when
  the other two are weak.
