# Contributing to DeltaSharp

Thanks for your interest in DeltaSharp — a fully native .NET reimplementation of
Apache Spark with first-class Delta tables and Kubernetes-native execution.
DeltaSharp is open source under the [Apache License 2.0](LICENSE), and we welcome
contributions of all kinds: code, tests, docs, design proposals, and triage.

Please read and follow our [Code of Conduct](CODE_OF_CONDUCT.md).

## Ways to contribute

- **Report bugs** and **request features** via GitHub Issues.
- **Discuss ideas** in GitHub Discussions before large changes.
- **Pick up a `good first issue`** if you are new.
- **Improve docs** — including the design docs under `docs/`.
- **Propose substantial changes** via the [RFC process](docs/rfcs/README.md).

## Licensing and the Developer Certificate of Origin (DCO)

All contributions are accepted under the Apache License 2.0. We use the
[Developer Certificate of Origin](https://developercertificate.org/) (DCO) — not a
CLA. Sign off every commit to certify you wrote the code or have the right to
submit it under the project license:

```bash
git commit -s -m "Your message"
```

This appends a `Signed-off-by: Your Name <you@example.com>` trailer. The DCO check
will fail PRs with unsigned commits.

## Development setup

DeltaSharp targets **.NET 10** for the engine and multi-targets public libraries
(`net8.0;net10.0`). Install the .NET SDK (10.0+), then:

```bash
dotnet restore                          # restore dependencies
dotnet build -c Release                 # build the solution (warnings are errors)
dotnet test                             # run all tests
dotnet format                           # auto-fix formatting + code style (remediation)
dotnet format --verify-no-changes       # lint gate: fail if unformatted
```

Run a single test project or test:

```bash
dotnet test tests/DeltaSharp.Core.Tests
dotnet test --filter "FullyQualifiedName~DataFrameTests"
dotnet test --filter "Name=Select_ProjectsColumns"
```

> The repository is greenfield; these are the intended commands as the solution is
> scaffolded. See `.github/copilot-instructions.md` and `docs/adr/` for the current
> architecture.

## Project layout

- `src/` — framework projects; `tests/` — one `.Tests` project per `src` project.
- `samples/` — example applications.
- `docs/adr/` — Architecture Decision Records (the **source of truth** for design).
- `docs/engineering/design/` — the architecture overview.
- `docs/rfcs/` — the proposal process for substantial changes.
- `docs/persona/` — role/persona library used by AI-assisted workflows.

## Coding standards

- Enable nullable reference types; PascalCase public members, `_camelCase` private
  fields; `async`/`await` for I/O.
- Formatting and analyzer rules are enforced via `.editorconfig` and `dotnet
  format`; keep public-AOT/trim annotations clean (see the
  `dotnet-library-platform-engineer` conventions). Run `dotnet format` to fix
  formatting locally before pushing.
- Mirror the Apache Spark public API where practical (see
  `.github/copilot-instructions.md`); document any deliberate deviation.
- Preserve the engine invariant: **transformations are lazy, actions are eager.**

## Quality gates

Every pull request must pass three automated gates across two CI jobs (see
[docs/engineering/design/quality-gates.md](docs/engineering/design/quality-gates.md) for the
full policy):

- **`build-test-format`** enforces gates 1 and 2 and is the authoritative test-correctness run
  (tests run **uninstrumented** here).
- **`coverage`** runs in parallel and enforces gate 3 (line-coverage threshold); it is
  measurement-only so coverage instrumentation can never change the correctness verdict. It becomes
  **merge-blocking** once added to the repository's branch-protection required checks — a post-merge
  step, since a check can only be required after it has first run on `main`.

Each gate has a local command that reproduces the CI result:

1. **Warnings-as-errors** — `dotnet build -c Release`. `TreatWarningsAsErrors=true` plus the
   .NET, trim/AOT, and API analyzers mean any warning fails the build. Suppressions must be
   **scoped and justified** (never a project-wide `<NoWarn>`).
2. **Formatting** — `dotnet format --verify-no-changes` checks; **`dotnet format`** fixes.
3. **Coverage** — the `coverage` job collects line coverage and fails when it drops below the floor
   recorded in
   [`tools/coverage/coverage-config.json`](tools/coverage/coverage-config.json). Reproduce with:

   ```bash
   dotnet test -c Release --collect:"XPlat Code Coverage" \
     --settings coverlet.runsettings --results-directory TestResults
   python3 tools/coverage/coverage-gate.py --results-dir TestResults
   ```

## Tests

- New behavior needs tests; bug fixes need a regression test.
- Performance-sensitive changes should include or update a BenchmarkDotNet
  benchmark and cite the before/after (see `performance-benchmarking-engineer`).

## Pull requests

1. Fork and branch from `main` (e.g. `your-username/short-topic`).
2. Keep PRs small and focused; write a clear description and link the issue.
3. Ensure `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes`
   pass, and commits are DCO-signed.
4. A maintainer review is required to merge.

> **What happens after you open a PR.** An automated AI review council (the "RFL" — a scout, a
> multi-model council, and a decorrelated red-team gate) may comment on your PR with a structured
> report (`PASS` / `MISS-FOUND`, severities, and `C1–C7` rigor checks). It is **advisory** and helps
> reviewers — it **never merges**; humans decide. You don't need to do anything special for it; just
> keep your commits DCO-signed and address the findings you agree with.

## Design changes, ADRs, and RFCs

- **Architecture Decision Records (`docs/adr/`)** record foundational decisions.
  They are append-only — to change a decision, add a new ADR that supersedes the
  old one.
- **RFCs (`docs/rfcs/`)** are for *substantial* or cross-cutting changes — new
  public APIs, wire protocols, engine subsystems, or anything that warrants
  community discussion before implementation. Accepted RFCs frequently produce an
  ADR. See [docs/rfcs/README.md](docs/rfcs/README.md).

If in doubt whether a change needs an RFC, open a Discussion or a draft RFC and
ask.
