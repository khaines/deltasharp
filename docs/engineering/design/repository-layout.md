# Repository layout & project conventions

> **Status:** living document. Created with the M1 solution skeleton
> ([STORY-01.1.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-01-project-build-platform.md#story-0111-root-solution-and-source-tree-skeleton)).
> Update it whenever the layout or naming conventions change. Grounded in
> [ADR-0014](../../adr/0014-target-framework-aot.md) and
> [`.github/copilot-instructions.md`](../../../.github/copilot-instructions.md).

## Top-level layout

| Path | Purpose |
| --- | --- |
| `DeltaSharp.sln` | Root solution; references every `src/` and `tests/` project. |
| `src/` | Production projects — one folder per assembly. |
| `tests/` | Test projects — one per production project, suffixed `.Tests`. |
| `samples/` | Runnable example applications — **never packable**, isolated from production package validation. See [`samples/README.md`](../../../samples/README.md) and [samples-conventions](#samples). |
| `tools/` | Repository scripts (for example `dco-check.sh`). |
| `global.json` | Pins the .NET SDK band (`10.0.1xx`). |
| `Directory.Build.props` | Repository-wide MSBuild policy: nullable, deterministic builds, warnings-as-errors, version, and package-metadata defaults. |
| `Directory.Packages.props` | Central Package Management — all NuGet versions live here. |
| `.editorconfig` | Formatting and code-style policy enforced by `dotnet format`. |

## Projects (M1 skeleton)

| Project | Folder | Target framework(s) | Packable | Role |
| --- | --- | --- | --- | --- |
| `DeltaSharp.Core` | `src/DeltaSharp.Core` | `net8.0;net10.0` | yes | Public, user-facing API surface. Multi-targeted for current-LTS adoption (ADR-0014). Root namespace `DeltaSharp`. Trim/AOT-annotation-clean (trim/AOT analyzers). |
| `DeltaSharp.Abstractions` | `src/DeltaSharp.Abstractions` | `net8.0;net10.0` | yes | Shared **packable** logical contracts — the `Core`↔`Engine` seam (ADR-0016). Multi-targeted so both `Core` and `Engine` can reference it while staying independent siblings. Root namespace `DeltaSharp`; types under `DeltaSharp.Types`. Trim/AOT-annotation-clean; PublicAPI-governed. **Scaffolded empty** in STORY-04.T.S1a; the ADR-0008 logical type model moves in atomically in S1b+S2. |
| `DeltaSharp.Engine` | `src/DeltaSharp.Engine` | `net10.0` | no | Engine internals — **not part of the published package surface** (the assembly is never shipped as NuGet). `net10.0`-only so it can use the newest runtime and NativeAOT; not referenced by the public `net8.0` surface. |
| `DeltaSharp.Executor` | `src/DeltaSharp.Executor` | `net10.0` | no | Representative NativeAOT executor host process. `PublishAot=true` is local to this executable project so AOT publish settings do not leak to public libraries. |
| `DeltaSharp.Storage` | `src/DeltaSharp.Storage` | `net10.0` | no | Native Delta/Parquet storage internals (EPIC-05). `net10.0`-only, non-packable, sibling to Engine; references Engine + Abstractions (Engine never references it). First assembly with a third-party codec (Parquet.Net) → carries a committed `packages.lock.json`. |
| `DeltaSharp.Core.Tests` | `tests/DeltaSharp.Core.Tests` | `net8.0;net10.0` | no | xUnit tests for `DeltaSharp.Core`, multi-targeted so both library targets are compiled and executed in CI. |
| `DeltaSharp.Engine.Tests` | `tests/DeltaSharp.Engine.Tests` | `net10.0` | no | xUnit tests for `DeltaSharp.Engine` (matches the engine's single TFM). |
| `DeltaSharp.Executor.Tests` | `tests/DeltaSharp.Executor.Tests` | `net10.0` | no | xUnit tests for `DeltaSharp.Executor` (matches the executor's single TFM). |
| `DeltaSharp.Storage.Tests` | `tests/DeltaSharp.Storage.Tests` | `net10.0` | no | xUnit tests for `DeltaSharp.Storage` (Parquet round-trip parity, adapter conditional-put, malformed handling). |
| `DeltaSharp.Samples.GettingStarted` | `samples/DeltaSharp.Samples.GettingStarted` | `net8.0` | no | Minimal example consuming `DeltaSharp.Core` from the current LTS. Compiled by the solution build; non-packable, so excluded from package validation. |

> The skeleton is intentionally **inert**: it exposes no Apache Spark or Delta Lake
> behavior yet. Real engine and API code lands in later epics.

Test-project naming/placement, the **test-access policy** (public-in-unshipped-assembly
vs. `InternalsVisibleTo`), and the unit-vs-integration split are documented in
[testing-conventions.md](testing-conventions.md). The sample conventions (non-packable,
preview-status visibility, CI isolation) are documented in
[`samples/README.md`](../../../samples/README.md).

## Intended assembly map (active and planned)

The M1 scaffold starts with Core, Engine, and the representative NativeAOT
Executor host. As later epics land, engine internals are expected to split into
focused assemblies along the layer and **control-plane / data-plane** boundaries (see the
[architecture checklist](../checklists/01-architecture-checklist.md)). Names are
indicative for assemblies not yet created; each planned assembly is created only when
its code arrives:

| Planned assembly | Plane | Responsibility |
| --- | --- | --- |
| `DeltaSharp.Core` | — | Public API + immutable logical plans (the only packable, multi-targeted surface). |
| `DeltaSharp.Abstractions` | — | Shared **packable** contracts bridging the public surface and the engine — the `Core`↔`Engine` seam (ADR-0016). **Active** (`src/DeltaSharp.Abstractions`, `net8.0;net10.0`): scaffolded empty in STORY-04.T.S1a to hold the ADR-0008 logical type model, which moves in atomically in S1b+S2. See [shared-type-model.md](shared-type-model.md). |
| `DeltaSharp.Engine` | data | Analyzer/optimizer, physical planning, vectorized execution internals. |
| `DeltaSharp.Storage` | data | Delta transaction log, Parquet, object-store / PVC backends. **Active** (`src/DeltaSharp.Storage`, `net10.0`, non-packable): EPIC-05 / FEAT-05.1 landed the vectorized Parquet reader/writer and the storage-adapter contract (Parquet.Net codec). See [storage-delta-architecture.md](storage-delta-architecture.md). |
| `DeltaSharp.Distributed` | data | Driver/executor coordination and the native remote shuffle service (CODEOWNERS anticipates `/src/**/Shuffle/`). |
| `DeltaSharp.Operator` | **control** | Kubernetes Operator, CRDs, reconcilers — kept out of executor hot paths. |
| `DeltaSharp.Executor` (host exe) | data | Active representative NativeAOT executor host process (driver/executor task execution). |

Two rules govern this map: the **control plane** (Operator / CRDs / reconciliation)
must never sit in per-task data-plane hot paths; and the **packable public surface**
must not depend on a non-packable engine assembly (see the TFM policy below).
CODEOWNERS already anticipates several of these seams (`/src/**/Sql/`,
`/src/**/Execution/`, `/operator/`, …) and is activated as each lands.

## Naming conventions

- **Assemblies / packages:** `DeltaSharp.<Area>` (PascalCase, dot-separated).
- **Folders:** match the assembly name exactly (`src/DeltaSharp.<Area>/`).
- **Test projects:** `<Project>.Tests`, placed under `tests/` mirroring `src/`.
- **Namespaces:** rooted at `DeltaSharp`; the public surface uses `DeltaSharp`
  directly, internal areas use `DeltaSharp.<Area>`.
- **Samples:** `samples/<SampleName>/`, not packable. See [Samples](#samples).

## Samples

Example applications live under `samples/` and are **never published**. Because they are
non-packable, `dotnet pack` skips them, so a broken sample can never block the production
**package-validation** diagnostics for `DeltaSharp.Core` (`pack-validate` stays Core-only);
[`pack.yml`](../../../.github/workflows/pack.yml) also fails if any `*Samples*` project ever
produces a package. Samples *are* listed in `DeltaSharp.sln`, so the required
`build-test-format` gate compiles them (a broken sample is caught and reported there), and a
dedicated, informational [`samples.yml`](../../../.github/workflows/samples.yml) workflow
builds and smoke-runs them as an extra signal (STORY-01.1.3). In-repo samples reference
production projects with a `ProjectReference` so they track the working tree. Full
conventions — including how a sample makes **preview-API
status and expected compatibility** visible — are in
[`samples/README.md`](../../../samples/README.md).

## Target-framework policy (ADR-0014)

- **Engine / executor** projects target **`net10.0`** only.
- **Public-facing libraries** multi-target **`net8.0;net10.0`** and stay
  trim/AOT-annotation-clean (enforced by the `EnableTrimAnalyzer` /
  `EnableAotAnalyzer` / `EnableSingleFileAnalyzer` Roslyn analyzers under
  warnings-as-errors). Full assembly-level `IsAotCompatible` / `PublishAot`
  verification is deferred to STORY-01.4.1 — note that `IsAotCompatible`/`IsTrimmable`
  pull the SDK-patch-tied `Microsoft.NET.ILLink.Tasks` package, which would destabilize
  the committed lock file under locked-mode restore.
- A public `net8.0` library must never depend on a `net10.0`-only assembly.
- A **packable** library must not reference a **non-packable** assembly on **any**
  target framework — the published package would ship an unresolvable dependency.
  So `DeltaSharp.Core` (packable) does not reference `DeltaSharp.Engine`
  (non-packable); the API-to-engine seam is wired at runtime, not via a compile-time
  package dependency. This is finalized when EPIC-04 connects the public API to the
  engine (e.g. a runtime abstraction, or a shared packable `DeltaSharp.Abstractions`).
- Deviations require an ADR or a documented, owner-attributed exception.

## Adding a new project

1. Create `src/DeltaSharp.<Area>/DeltaSharp.<Area>.csproj` (or under `tests/`).
2. Choose target frameworks per the policy above.
3. Reference NuGet packages **without** inline versions — versions live in
   `Directory.Packages.props`.
4. Add it to the solution: `dotnet sln DeltaSharp.sln add <path>`.
5. Add a matching `tests/DeltaSharp.<Area>.Tests` project. It inherits the xUnit toolchain and the
   deterministic-seed harness from [`tests/Directory.Build.props`](../../../tests/Directory.Build.props)
   — see [test-harness-conventions.md](test-harness-conventions.md).
6. Enable a NuGet lock file (`<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`)
   on any project that takes a real third-party `PackageReference` — every test project (inherited
   from [`tests/Directory.Build.props`](../../../tests/Directory.Build.props)),
   and any production project (including the packable `DeltaSharp.Core`) the moment it
   gains its first third-party dependency. CI restores in locked mode and fails on an
   out-of-date lock file. `DeltaSharp.Engine` (Apache.Arrow) and `DeltaSharp.Storage`
   (Parquet.Net) therefore carry a committed `packages.lock.json`; the still-SDK-only
   `DeltaSharp.Core`/`DeltaSharp.Abstractions` deliberately omit one (their only locked
   package would be the patch-tied `ILLink.Tasks` — the NU1004 hazard). Enabling the
   lock file is NU1004-safe for Engine/Storage because they enable the trim/AOT
   *analyzers* rather than `IsTrimmable`/`IsAotCompatible`, so no `ILLink.Tasks` is pulled.
7. Once maintainers and code exist, activate the matching `CODEOWNERS` rule
   (see [`.github/CODEOWNERS`](../../../.github/CODEOWNERS)).
