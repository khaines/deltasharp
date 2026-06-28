# NuGet packaging and SourceLink

> **Status:** living document. Created with FEAT-01.6
> ([STORY-01.6.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-01-project-build-platform.md#story-0161-package-metadata-and-symbol-packages),
> [STORY-01.6.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-01-project-build-platform.md#story-0162-sourcelink-and-deterministic-package-validation),
> [STORY-01.6.3](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-01-project-build-platform.md#story-0163-pack-command-and-artifact-validation-smoke-test)).
> It completes the packaging acceptance criteria deferred when the metadata (#121) and
> package-asset (#124) groundwork was closed. Grounded in
> [ADR-0014](../../adr/0014-target-framework-aot.md) (TFM/AOT),
> [ADR-0015](../../adr/0015-open-source-positioning.md) (Apache-2.0), and checklists
> [03a](../checklists/03a-dotnet-coding-standards.md),
> [05](../checklists/05-security-checklist.md),
> [11](../checklists/11-documentation-support-checklist.md), and
> [20](../checklists/20-developer-experience-api-checklist.md).

DeltaSharp ships its public library as a NuGet package with correct metadata, symbol
packages, SourceLink, deterministic builds, and package validation, so artifacts are
discoverable, legally clear, debuggable, and protected against accidental compatibility
breaks. Shared policy lives in [`Directory.Build.props`](../../../Directory.Build.props)
(the `lane:01.6` section and the packaging defaults above it); per-package metadata lives in
each project.

## What is packable

| Project | Packable | Why |
| --- | --- | --- |
| `DeltaSharp.Core` | yes | The public, user-facing API surface. |
| `DeltaSharp.Engine` | no | Internal engine assembly — never shipped as a package. |
| `tests/**` | no | Test projects. |
| `samples/**` | no | Example apps (added later). |

Projects are **not packable by default** (`IsPackable=false` in `Directory.Build.props`); a
project opts in with `IsPackable=true`. The pack smoke test asserts no test or sample project
produces a package.

## Package metadata (STORY-01.6.1)

Shared metadata is inherited from `Directory.Build.props`: `Authors`, `Product`, `Company`,
`PackageLicenseExpression` (**Apache-2.0**, per ADR-0015), `PackageProjectUrl`,
`RepositoryUrl`, `RepositoryType`, and `PackageTags`. Per-package metadata lives in the
project file: `PackageId`, `Description`, and `PackageReadmeFile` (each packable project
ships a `README.md` packed at the package root).

Symbols are enabled repository-wide: `IncludeSymbols=true` with
`SymbolPackageFormat=snupkg`, so `dotnet pack` produces a `.snupkg` next to the `.nupkg`.

## SourceLink and deterministic builds (STORY-01.6.2)

`PublishRepositoryUrl=true` and `EmbedUntrackedSources=true` are set repository-wide;
`Deterministic=true` is always on and CI sets `ContinuousIntegrationBuild=true`, which turns
on deterministic source-path normalization and SourceLink URL mapping.

**SourceLink is provided in-box by the .NET 8+ SDK**, so DeltaSharp references **no**
`Microsoft.SourceLink.*` package. This is deliberate: a standalone SourceLink package version
is tied to an SDK band, which is the same SDK-version coupling the repository already hit with
`Microsoft.NET.ILLink.Tasks` (see the comment in `DeltaSharp.Core.csproj`). Using the in-box
provider keeps the SDK pinned in one place (`global.json`).

A CI build emits a SourceLink map (`obj/**/<project>.sourcelink.json`) of the form:

```json
{
  "documents": {
    "/_/*": "https://raw.githubusercontent.com/khaines/deltasharp/<commit>/*"
  }
}
```

so a debugger that downloads the `.snupkg` symbols resolves source to the repository at the
exact built revision. The pack workflow asserts the map points at the repository.

## Package validation (STORY-01.6.2)

`EnablePackageValidation=true` runs during pack. For the multi-targeted `DeltaSharp.Core` it
validates that the package is **self-consistent across `net8.0` and `net10.0`** — a
`net10.0`-only public API that the `net8.0` target lacks fails validation with the offending
package, target framework, and rule. A released-version baseline
(`PackageValidationBaselineVersion`) is intentionally **not** set yet: nothing is published,
so there is no prior version to diff against. It is added at the first release so validation
also catches breaking changes versus the previous package.

## Building and inspecting packages locally

```bash
# Produce .nupkg + .snupkg for every packable project into ./artifacts.
dotnet pack DeltaSharp.sln -c Release -o artifacts

# Inspect metadata and assets (a .nupkg is a zip).
unzip -l artifacts/DeltaSharp.Core.0.1.0.nupkg     # lib/net8.0 + lib/net10.0 + README
unzip -p artifacts/DeltaSharp.Core.0.1.0.nupkg '*.nuspec'
unzip -l artifacts/DeltaSharp.Core.0.1.0.snupkg    # net8.0 + net10.0 .pdb
```

Expected artifacts for the M1 skeleton: `DeltaSharp.Core.0.1.0.nupkg` and
`DeltaSharp.Core.0.1.0.snupkg`, and no test or sample package.

## Pack smoke test (STORY-01.6.3)

[`.github/workflows/pack.yml`](../../../.github/workflows/pack.yml) runs on pull requests and
pushes to `main` (and on demand). It restores in locked mode, runs `dotnet pack`, then
asserts: the `.nupkg` and `.snupkg` for `DeltaSharp.Core` exist, no test/sample package is
produced, both target frameworks are packed, the SourceLink map resolves to the repository,
and the package identity/dependency metadata (the `.nuspec`) is printed for SBOM/SCA
visibility. It uploads the packages as a build artifact. It is **not** a branch-protection
required check (CI `build-test-format` and `dco` remain the only required checks), so it never
blocks the merge button.

## Adding a new packable library

1. Set `IsPackable=true`, `PackageId`, `Description`, and `PackageReadmeFile=README.md` in the
   new project, and add a `README.md` packed at the root (`<None Include="README.md"
   Pack="true" PackagePath="\" />`).
2. Inherit the shared symbol/SourceLink/validation policy from `Directory.Build.props` — no
   per-project packaging plumbing is needed.
3. The pack smoke test will automatically produce and validate its package.
