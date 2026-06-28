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
| `samples/` | Example applications (added later; never packable). |
| `tools/` | Repository scripts (for example `dco-check.sh`). |
| `global.json` | Pins the .NET SDK band (`10.0.1xx`). |
| `Directory.Build.props` | Repository-wide MSBuild policy: nullable, deterministic builds, warnings-as-errors, version, and package-metadata defaults. |
| `Directory.Packages.props` | Central Package Management — all NuGet versions live here. |
| `.editorconfig` | Formatting and code-style policy enforced by `dotnet format`. |

## Projects (M1 skeleton)

| Project | Folder | Target framework(s) | Packable | Role |
| --- | --- | --- | --- | --- |
| `DeltaSharp.Core` | `src/DeltaSharp.Core` | `net8.0;net10.0` | yes | Public, user-facing API surface. Multi-targeted for current-LTS adoption (ADR-0014). Root namespace `DeltaSharp`. |
| `DeltaSharp.Engine` | `src/DeltaSharp.Engine` | `net10.0` | no | Internal engine seam. `net10.0`-only so it can use the newest runtime and NativeAOT. Not referenced by the public `net8.0` surface. |
| `DeltaSharp.Core.Tests` | `tests/DeltaSharp.Core.Tests` | `net8.0;net10.0` | no | xUnit tests for `DeltaSharp.Core`, multi-targeted so both library targets are compiled and executed in CI. |

> The skeleton is intentionally **inert**: it exposes no Apache Spark or Delta Lake
> behavior yet. Real engine and API code lands in later epics.

## Naming conventions

- **Assemblies / packages:** `DeltaSharp.<Area>` (PascalCase, dot-separated).
- **Folders:** match the assembly name exactly (`src/DeltaSharp.<Area>/`).
- **Test projects:** `<Project>.Tests`, placed under `tests/` mirroring `src/`.
- **Namespaces:** rooted at `DeltaSharp`; the public surface uses `DeltaSharp`
  directly, internal areas use `DeltaSharp.<Area>`.
- **Samples:** `samples/<SampleName>/`, not packable.

## Target-framework policy (ADR-0014)

- **Engine / executor** projects target **`net10.0`** only.
- **Public-facing libraries** multi-target **`net8.0;net10.0`** and stay
  trim/AOT-annotation-clean.
- A public `net8.0` library must never depend on a `net10.0`-only assembly.
- Deviations require an ADR or a documented, owner-attributed exception.

## Adding a new project

1. Create `src/DeltaSharp.<Area>/DeltaSharp.<Area>.csproj` (or under `tests/`).
2. Choose target frameworks per the policy above.
3. Reference NuGet packages **without** inline versions — versions live in
   `Directory.Packages.props`.
4. Add it to the solution: `dotnet sln DeltaSharp.sln add <path>`.
5. Add a matching `tests/DeltaSharp.<Area>.Tests` project.
6. Once maintainers and code exist, activate the matching `CODEOWNERS` rule
   (see [`.github/CODEOWNERS`](../../../.github/CODEOWNERS)).
