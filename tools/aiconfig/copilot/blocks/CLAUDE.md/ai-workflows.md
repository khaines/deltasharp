> **AI-assisted workflows.** Specialist personas live in `.github/agents/` (canonical
> role specs in `docs/persona/agents/`), and the orchestration skills — `design-doc`,
> `implement-work-item`, `review-pr`, `review-fix-loop`, `stacked-pr-chain` — live in
> `.github/skills/`.
>
> **This tree is GENERATED.** `.claude/**` and `CLAUDE.md` are canonical; this file,
> `.github/agents/` and `.github/skills/` are produced from them by
> `tools/aiconfig/generate-copilot.py` and committed. Edit the canonical tree and re-run
> the generator with `--write`; a hand-edit here is reverted by the next run and fails the
> `reconcile` workflow's sync check in the meantime.
