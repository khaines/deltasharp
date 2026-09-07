<!-- GENERATED from CLAUDE.md by tools/aiconfig/generate-copilot.py — do not edit; edit the source and re-run with --write. -->

# DeltaSharp — Copilot Instructions

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
assumed; see [ADR-0015](../docs/adr/0015-open-source-positioning.md)).

> **Status: active implementation.** `DeltaSharp.sln` holds `src/DeltaSharp.Abstractions`,
> `src/DeltaSharp.Core`, `src/DeltaSharp.Engine`, `src/DeltaSharp.Executor`,
> `src/DeltaSharp.Storage`, matching `tests/` projects, and `samples/`. The
> architecture below is the target that steers ongoing implementation; where code
> and this file disagree, the ADRs in `docs/adr/` win. See
> [repository layout & project conventions](../docs/engineering/design/repository-layout.md),
> and keep these instructions in sync as engine code lands.
>
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

GitHub Copilot has no repository permission file — there is no `.github` equivalent of
Claude Code's `settings.json`, no allow/deny list, and no `defaultMode`. What Copilot *does*
share with Claude Code is the part that actually matters for trust: **the configuration in
this repository is read and executed on the machine that checks it out**, so a change to
`.github/agents/`, `.github/skills/` or this file is a change to what runs on your laptop.
The rules below are therefore the same rules, minus the settings surface that does not exist
here.

**The C7 trust boundary.** `dotnet restore`/`build`/`test`/`format` execute PR-supplied
MSBuild targets, analyzers, source generators, and `nuget.config` sources in-process. On
another author's branch, run them from a throwaway copy outside the worktree, per
`.github/skills/review-pr/rigor-battery.md` (C7). Reviewing someone else's branch means
their persona wrappers and skill manifests are the ones on disk, so they sit inside the same
boundary as the build itself.

**Front matter is policed.** The `reconcile` workflow's `copilot-frontmatter` check walks
every tracked `.github/agents/*.agent.md` and `.github/skills/**/SKILL.md` with a strict
reader. A wrapper may carry only `name`, `description` and `tools`; a skill manifest only
`name` and `description`. `allowed-tools` is rejected outright — it grants unprompted tool
use — and so are `permissionMode`, `hooks`, `mcpServers`, `env`, `isolation`, `model`, and
any key outside those lists. Front matter the strict reader cannot parse (a quoted key,
`key :`, a flow or indented mapping, a duplicate key) fails the same way, and a skills tree
with no manifest is drift, not a pass. A wrapper whose filename stem does not match its
`name:` is an integrity error.

**No links.** Any *tracked* symlink **or submodule** (git mode `120000`/`160000`) at
`.github` itself, or anywhere under `.github/agents/`, `.github/skills/`, or at
`.github/copilot-instructions.md`, fails the gate. The loader follows such a link; the
gate's walks deliberately do not, so a filesystem-only check would ship unreviewed
configuration. The rule is decided by the **git index mode**, not by what the path resolves
to on your machine, so neither shape can hide: a **dangling** link (one pointing at `bin/`
or `obj/`) is absent in a checkout-only CI job and resolves to real configuration on any
machine that has built, and a **submodule** is checked out *empty* by CI while
`git submodule update --init` loads whatever it contains on a developer machine.

**Spelling is policed too.** The index is listed once from the work-tree root, and every
entry that **case- or normalization-folds** onto a policed path is compared with the
canonical spelling: on an APFS/NTFS checkout a tracked `.GitHub/agents/x.agent.md`,
`.github/agents/x.AGENT.MD` or `.github/skills/<x>/ſkill.md` (U+017F) materializes at the
path the loader reads, so it fails as "rename it" whatever its index mode. The same fold
decides file *names* in the walks as well as the index, and a tracked path whose bytes are
not valid UTF-8 is reported rather than decoded.

**Scope.** Only the three AI-config children are policed here — `.github/agents`,
`.github/skills` and `.github/copilot-instructions.md` — plus the `.github` root entry
itself, so a link or submodule *at* `.github` cannot hide the tree behind it.
`.github/workflows/`, `CODEOWNERS`, `ISSUE_TEMPLATE/` and `dependabot.yml` are deliberately
**out** of this gate's scope: they are reviewed on their own path, and policing them here
would red unrelated pull requests on a rule that was never about them.

**Coverage is not assumed.** A `.github` the checkout's index does not own — an export
dropped inside an enclosing checkout, whose index tracks nothing at or under any policed
child — is reported as *unverified*, not clean. An untracked subtree inside a `.github` the
checkout *does* own is ordinary work in progress, so it is walked, policed and noted rather
than skipped. Non-git checkouts skip with a reason, and the run's success line is qualified
accordingly.
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

- **Execution backend ([ADR-0001](../docs/adr/0001-execution-strategy.md)):**
  pluggable — an AOT-safe **vectorized interpreter** is the default and the
  correctness reference; an **optional JIT codegen tier** (intra-operator
  `Expression.Compile` fusion) is enabled only when
  `RuntimeFeature.IsDynamicCodeSupported`. Keep the codegen tier AOT-elidable
  (`[RequiresDynamicCode]`/`[FeatureGuard]`); both backends must produce identical
  results (parity oracle).
- **Columnar batches ([ADR-0002](../docs/adr/0002-columnar-batch-format.md)):**
  operators bind to an internal **mutable `ColumnBatch`/`ColumnVector`**
  (selection-vector-aware), **Arrow-backed initially**, custom off-heap later —
  **Arrow at the edges** (Parquet, Flight, interop).
- **Transport ([ADR-0003](../docs/adr/0003-data-plane-transport.md)):** **gRPC
  control plane + Arrow Flight data plane** behind `IDataExchange`.
- **Shuffle ([ADR-0004](../docs/adr/0004-shuffle-architecture.md)):** a
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
  [repository layout](../docs/engineering/design/repository-layout.md).
