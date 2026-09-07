# DeltaSharp — Claude Code Instructions

## Project overview

DeltaSharp is a **.NET-native reimplementation of Apache Spark**: an application
framework library (plus example applications) that mirrors Spark's functions,
processing model, and public API surface in C#/.NET — without a JVM. It is built
around three pillars:

1. **Apache Spark parity** — match Spark's API (`SparkSession`, `DataFrame`/
   `Dataset<T>`, columns, SQL) and its execution semantics so Spark users feel at
   home and code/concepts port over directly.
2. **Native Delta tables** — first-class Delta Lake support (transaction log,
   ACID writes, time travel, schema evolution) implemented natively in .NET.
3. **Kubernetes-native** — distributed execution designed for Kubernetes from the
   start, managed by a custom Operator and CRDs.

DeltaSharp is **open-source** with a community-driven adoption strategy (Apache-2.0
assumed; see [ADR-0015](docs/adr/0015-open-source-positioning.md)).

> **Status: active implementation.** `DeltaSharp.sln` holds `src/DeltaSharp.Abstractions`,
> `src/DeltaSharp.Core`, `src/DeltaSharp.Engine`, `src/DeltaSharp.Executor`,
> `src/DeltaSharp.Storage`, matching `tests/` projects, and `samples/`. The
> architecture below is the target that steers ongoing implementation; where code
> and this file disagree, the ADRs in `docs/adr/` win. See
> [repository layout & project conventions](docs/engineering/design/repository-layout.md),
> and keep these instructions in sync as engine code lands.
>
> **AI-assisted workflows.** Specialist personas live in `.claude/agents/` (canonical
> role specs in `docs/persona/agents/`), and the orchestration skills — `design-doc`,
> `implement-work-item`, `review-pr`, `review-fix-loop`, `stacked-pr-chain` — live in
> `.claude/skills/`.

## Build, test, and lint

DeltaSharp is a standard .NET solution; use the .NET SDK CLI.

```bash
dotnet restore                         # restore NuGet dependencies
dotnet build -c Release                # build the whole solution
dotnet test                            # run all test projects
dotnet format                          # apply formatting / analyzer fixes
dotnet format --verify-no-changes      # CI lint gate: fail if unformatted
```

Run a **single** test project or a **single** test:

```bash
dotnet test tests/DeltaSharp.Core.Tests                 # one test project
dotnet test --filter "FullyQualifiedName~DataFrameTests" # one class
dotnet test --filter "Name=Select_ProjectsColumns"       # one test method
```

Prefer keeping the solution buildable with `dotnet build` from the repo root
(i.e. a single `*.sln` at the root that references all `src/` and `tests/`
projects).

## Agent permissions

`.claude/settings.json` pre-approves read-only inspection commands:
`git status`, `diff`, `log`, `show`, `rev-parse`, `rev-list`, `merge-base`,
`ls-files`, branch/worktree listings, `gh pr view`/`diff`/`list`/`checks`,
`gh issue view`/`list`, and `dotnet restore`/`build`/`test`/`format`.

Most are `Bash(prefix:*)` grants (a few branch listings are exact-match), and a
prefix match covers everything typed after it, so `--output=<path>` on
`git diff`, `git log`, or `git show` can still write a file — as can any
per-user override, including the gitignored `.claude/settings.local.json`. Treat
`--output` on another author's branch exactly like the `dotnet` grant below.

The deny list covers only the explicit destructive spellings — branch
delete/rename short flags, `git push -f`, `git push --force`, `gh api` with
`-X POST`/`PUT`/`PATCH`/`DELETE` or `--method`, `gh pr merge`, `gh release`, and
`gh secret`. Deny matching is word-boundary prefix matching on the *leading*
spelling, so variant or non-leading flags (`-X post`, `-XPOST`, `--method=POST`,
`git push origin main -f`) and everything else that writes (`git push`, commit,
`git worktree add`/`remove`, PR/issue creation, reviews) prompt instead.
Prompt-on-write is the design, not an omission. The `reconcile` workflow's
`settings-permissions` check enforces a positive allowlist: top level may hold
only `$schema`, `permissions`, and inert keys (`model`, `cleanupPeriodDays`,
`includeCoAuthoredBy`, `attribution`, `outputStyle`, `language`,
`spinnerTipsEnabled`), `permissions` only `allow`/`deny`/`defaultMode`; every
other key is rejected, executable-valued ones by name. So are the enumerated
mutating `git`/`gh` prefixes (case-insensitive), wildcard or tool-wide `Bash`
grants, and any `defaultMode` outside `default`/`plan` (`acceptEdits`
auto-approves file writes); destructive-spelling deny entries are required.
Any other `allow` entry (`curl`, `git commit`, a new shell wrapper) is
unpoliced and needs human review.

The `dotnet` grant is intended only for the maintainer's own branches; nothing
enforces that. `dotnet restore`/`build`/`test`/`format` execute PR-supplied
MSBuild targets, analyzers, source generators, and `nuget.config` sources
in-process, so on someone else's branch run them from a throwaway copy outside
the worktree per `.claude/skills/review-pr/rigor-battery.md` (C7).
Project-level `hooks`, `env`, `apiKeyHelper`, and the other command-bearing
keys in another author's branch execute once it is checked out — `apiKeyHelper`
at startup, before any tool call — so they fall under the same C7 trust
boundary as the `dotnet` grant; the gate rejects them in the tracked file.
Persona wrappers in `.claude/agents/` sit inside that boundary too: the gate
lets them set only a `default`/`plan` `permissionMode` and rejects `hooks`,
`mcpServers`, `isolation` (it runs `git worktree add` unprompted), and `env`.
Any other front-matter key, and front matter the strict reader cannot parse
(quoted key, `key :`, flow or indented mapping, duplicate key), fails the gate.
Slash-command files in `.claude/commands/` and `SKILL.md` front matter can grant
unprompted tool use via `allowed-tools`, so the gate rejects that key (and
`permissionMode`, `hooks`, `mcpServers`, `env`, `isolation`) in tracked command
and skill files, along with any key outside `description`/`argument-hint`/`model`
(skills also `name`); front matter the strict reader cannot parse fails the same
way, and a skills tree with no manifest is drift, not a pass.

Three more startup surfaces sit inside the same boundary and the gate rejects all
three. **Links:** any *tracked* symlink **or submodule** (git mode `120000`/`160000`)
anywhere under `.claude/` — including `.claude` itself — or at `.mcp.json`,
`.claude/settings.local.json`, or `.claude/settings.json`, fails. The rule is decided
by the **git index mode**, not by what the path resolves to here, so neither shape can
hide: a **dangling** link (one pointing at `bin/` or `obj/`) is absent in a
checkout-only CI job and resolves to real configuration on any machine that has built,
and a **submodule** is checked out *empty* by CI while `git submodule update --init`
loads whatever it contains on a developer machine. Git is asked from the directory
that *contains* `.claude`, never from inside it — a submodule `.claude` would answer
from the nested repository and a symlinked one from outside the checkout, both
"clean". The index is **listed once from the work-tree root**, with no pathspec (so
no pathspec magic — `GIT_LITERAL_PATHSPECS` and friends are scrubbed along with
`GIT_DIR`) and every entry that **case- or normalisation-folds** onto `.claude/…` or
`.mcp.json` is compared with the canonical spelling: on an APFS/NTFS checkout a
tracked `.Claude/hooks`, `.claude/COMMANDS/evil.md`, `.claude/Settings.local.json`
or `.mcp.jſon` (U+017F) materialises at the path the CLI reads, so it fails as
"rename it" whatever its index mode — and "is it tracked?" is asked of the
checked-out path the same way, so a committed `.claude/Settings.local.json` is not
filed as harmless per-machine state. The `.claude` question is asked by all three
local checks, so a finding there is named wherever the reader looks (`.mcp.json` is
owned by `tracked-startup-config`). Git's answer must also *cover* what the walks
read: a path outside the work tree git answered from, a directory holding files no
index entry folds under (an export dropped inside an enclosing checkout), and a
sparse index are all *unverified*, not clean.
The CLI follows such a link; the gate's walks deliberately do not, so
it would otherwise ship unreviewed configuration. **A tracked root `.mcp.json`:** each
`mcpServers[*].command` is started when the CLI launches, before any tool call and
with no prompt. **A tracked `.claude/settings.local.json`:** it is honoured exactly
like `settings.json`, so the per-machine grants this file keeps recommending there
are only safe while it stays untracked and gitignored (both paths are in
`.gitignore`).

## Architecture — the big picture

DeltaSharp follows Spark's layered execution model. Keep these layers separate:
the API builds plans, it must **never** execute directly.

- **API layer** — `SparkSession` is the entry point; `DataFrame`/`Dataset<T>`,
  `Column`, and the functions library are the user-facing surface. Mirror Spark's
  names and semantics here.
- **Logical plan** — user operations build an unresolved logical plan (an
  immutable tree of operators). Building a plan does **no** work.
- **Analyzer + optimizer (Catalyst-style)** — resolve names against the catalog,
  then apply rule-based optimizations (predicate pushdown, column pruning,
  constant folding). Optimizer rules transform plan trees into equivalent,
  cheaper plan trees.
- **Physical planning** — translate the optimized logical plan into a physical
  plan of executable operators, choosing strategies (e.g. join algorithms).
- **Execution engine** — an **action** triggers execution; work is divided into
  stages/tasks and distributed to executors. Shuffle boundaries split stages.
- **Storage / Delta layer** — Parquet readers/writers plus the Delta transaction
  log (`_delta_log`). Provides ACID, time travel (by version or timestamp), and
  schema evolution. Storage backends are pluggable across **cloud object stores
  (S3 / ADLS / GCS)** and **Kubernetes PersistentVolumes (PVCs)**.
- **Cluster / Kubernetes layer** — a driver coordinates execution; executors run
  as Kubernetes pods (see below).

**The single most important invariant: transformations are lazy, actions are
eager.** Transformations (`select`, `filter`, `groupBy`, `join`, `withColumn`, …)
only extend the plan. Actions (`collect`, `count`, `show`, `write`, …) are the
only things that trigger the engine. Every new operator must preserve this.

## Kubernetes execution model

- **Operator + CRDs.** A custom Kubernetes Operator reconciles custom resources
  (CRDs) that declare jobs/applications, and manages their lifecycle.
- **Driver + executor pods.** For each application the operator provisions a
  driver pod that coordinates and executor pods that run tasks (Spark-on-K8s
  style).
- **Storage.** Executors read/write Delta tables on both cloud object storage and
  PVCs; keep storage access behind the pluggable storage abstraction so a job can
  target either without code changes.
- **Dynamic allocation / executor autoscaling is a future goal.** Assume a fixed
  executor count per job for now; design interfaces so dynamic allocation can be
  added later without reworking the operator.

## Engine architecture decisions

Foundational engineering decisions are recorded as **ADRs in `docs/adr/`** (the
source of truth) and summarized in
`docs/engineering/design/engine-architecture.md`. Honor these and keep each
abstraction swappable:

- **Execution backend ([ADR-0001](docs/adr/0001-execution-strategy.md)):**
  pluggable — an AOT-safe **vectorized interpreter** is the default and the
  correctness reference; an **optional JIT codegen tier** (intra-operator
  `Expression.Compile` fusion) is enabled only when
  `RuntimeFeature.IsDynamicCodeSupported`. Keep the codegen tier AOT-elidable
  (`[RequiresDynamicCode]`/`[FeatureGuard]`); both backends must produce identical
  results (parity oracle).
- **Columnar batches ([ADR-0002](docs/adr/0002-columnar-batch-format.md)):**
  operators bind to an internal **mutable `ColumnBatch`/`ColumnVector`**
  (selection-vector-aware), **Arrow-backed initially**, custom off-heap later —
  **Arrow at the edges** (Parquet, Flight, interop).
- **Transport ([ADR-0003](docs/adr/0003-data-plane-transport.md)):** **gRPC
  control plane + Arrow Flight data plane** behind `IDataExchange`.
- **Shuffle ([ADR-0004](docs/adr/0004-shuffle-architecture.md)):** a
  **.NET-native remote shuffle service** — node-local workers + a **location
  registry** with **dynamic resolution** (never pin a location; re-resolve +
  retry), **drain-migration + configurable eager replication**, object-store
  fallback later.

## Key conventions

- **Mirror the Spark API.** Match Spark's public method names, argument shapes,
  and semantics wherever practical so users can port Spark code. Deviate only
  where a .NET idiom strongly requires it, and document the deviation.
- **Preserve lazy/eager semantics.** New transformations must not execute; only
  actions invoke the engine.
- **Keep layers separate.** API → logical plan → optimizer → physical plan →
  execution. API code constructs plan nodes; it does not run them.
- **Plan nodes are immutable.** Optimizer/analyzer rules produce new trees rather
  than mutating in place.
- **C#/.NET style.** Enable nullable reference types; PascalCase for public
  members, `_camelCase` for private fields; `async`/`await` for I/O.
- **Repo layout:** `src/` for framework projects, `tests/` for test projects
  (one per `src` project, suffixed `.Tests`), `samples/` for example applications,
  and a single `DeltaSharp.sln` at the root. Engine/executor
  projects target `net10.0`; public libraries multi-target `net8.0;net10.0`
  (ADR-0014). Full conventions:
  [repository layout](docs/engineering/design/repository-layout.md).
