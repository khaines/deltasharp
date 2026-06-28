# planning-sync

Idempotent tooling that materializes the [workstream plan](../../docs/planning/README.md)
(`docs/planning/epics/EPIC-*.md`) into GitHub **milestones**, **labels**, and a nested
**sub-issue** tree (Epic → Feature → Story).

It is **idempotent and resumable**: every generated issue body carries a hidden
`<!-- ds-plan-id: ... -->` marker, and local state is kept in `state.db`. Re-running
reconciles against existing issues by marker, so nothing is duplicated — safe to re-run
after adding stories or recovering from a rate-limit/network interruption.

## Requirements

- `gh` authenticated with `repo` scope (`gh auth status`).
- Python 3.9+.

## Usage

```bash
cd tools/planning-sync

python3 parse.py            # parse epics -> model.json (+ prints counts/personas/sizes)
python3 build.py labels     # create/update ~47 labels (type/persona/size/epic)
python3 build.py preview    # print sample epic/feature/story issue bodies (no writes)
python3 build.py create     # create epic, feature, story issues (idempotent, throttled)
python3 build.py link       # nest features under epics, stories under features (sub-issues)
python3 build.py verify     # reconcile + print counts
```

Milestones (the 5 roadmap phases) are created once via `gh api`; `build.py` reads them
back and assigns every issue to its phase milestone.

## Mapping

| Plan level | GitHub artifact | Labels |
|---|---|---|
| Roadmap phase (M1–M4, v1.0) | Milestone | — |
| Epic | Issue; Features are sub-issues | `epic`, `epic:NN` |
| Feature | Issue; Stories are sub-issues | `feature`, `epic:NN`, `persona:<slug>` |
| Story | Issue | `story`, `epic:NN`, `persona:<slug>`, `size:<S>` |

Persona labels use the exact roster slug. GitHub caps label names at 50 chars; the one
slug that overflows (`dotnet-vectorized-columnar-compute-engineer`) drops its trailing
`-engineer` in label form only — the exact slug is always present in the issue body.

## Throttling

Set `DS_THROTTLE` (seconds between mutating calls; default `1.2`). The script backs off
and retries on secondary-rate-limit and transient (401/5xx/network) errors.

## Generated / ignored files

`model.json`, `state.db`, and `*.log` are run artifacts and are git-ignored.
