# DeltaSharp samples

Runnable example applications that show how to consume DeltaSharp. Samples are
**examples, not products**: they are never published as NuGet packages, and because they
are non-packable they are excluded from production **package validation** — a broken
sample can never block packaging or a release of the real libraries. (A broken sample
*will* still fail the required `build-test-format` gate, which compiles the whole
solution; see [How CI builds samples](#how-ci-builds-samples).)

> **Status:** scaffold created with
> [STORY-01.1.3](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-01-project-build-platform.md#story-0113-samples-directory-scaffold).
> The M1 skeleton is inert (no Apache Spark or Delta behavior yet), so the first sample
> only exercises build/version metadata. Update this document whenever the sample
> conventions change.

## Layout

| Path | Purpose |
| --- | --- |
| `samples/<SampleName>/` | One self-contained sample application per folder. |
| `samples/DeltaSharp.Samples.GettingStarted/` | Minimal console app that references `DeltaSharp.Core`. |

Sample project names are prefixed `DeltaSharp.Samples.` so they are unambiguous in the
solution and so packaging guards (see below) can match them by the `Samples` token.

## Conventions

1. **Not packable.** Samples inherit `IsPackable=false` from
   [`Directory.Build.props`](../Directory.Build.props) and also set it explicitly for
   clarity (#119 AC2). The packaging workflow fails if any `*Samples*` project ever
   produces a `.nupkg` (see [`.github/workflows/pack.yml`](../.github/workflows/pack.yml)).
2. **Reference local projects by `ProjectReference`.** In-repo samples reference the
   working-tree projects (for example `../../src/DeltaSharp.Core/DeltaSharp.Core.csproj`)
   so they always build against current source and surface API breaks immediately. A
   sample that intentionally demonstrates a *released* package may instead use a pinned
   `PackageReference` — call that out in the sample's own header comment.
3. **Target the framework you are demonstrating.** Prefer `net8.0` to show that the
   public `DeltaSharp.Core` surface is consumable from the current .NET LTS (the
   multi-target adoption goal in [ADR-0014](../docs/adr/0014-target-framework-aot.md)).
   Target `net10.0` only when a sample exercises net10.0-only engine features.
4. **Stay warning-clean.** The repository-wide `TreatWarningsAsErrors`, nullable, and
   .NET analyzers apply to samples too. (The `src/`-scoped public-API and banned-API
   analyzers do **not** apply to `samples/`.)

## Preview status and compatibility (#119 AC3)

Samples may demonstrate **preview or experimental** APIs before they stabilize. When a
sample uses a preview API, its status and the expected-compatibility contract must be
visible to a reader **without running the code**:

- State it in the sample's `Program.cs` header comment **and** in a short note in this
  file's *Sample index* below — name the preview API and the milestone/version it is
  expected to stabilize in.
- **Name the diagnostic ID.** Experimental DeltaSharp APIs carry
  `[Experimental("DS####", …)]` (see
  [api-lifecycle.md](../docs/engineering/design/api-lifecycle.md#DS0001) —
  `DeltaSharpInfo.PreviewReleaseChannel` is the first such API, ID `DS0001`). A sample that
  consumes one opts in explicitly and records *why*: either a scoped
  `#pragma warning disable DS####` around the call (preferred) or `<NoWarn>DS####</NoWarn>`
  in the sample's `.csproj`, with a comment pointing at the registry entry.
- Samples in this repository track the current `main`. APIs marked preview can change
  between milestones; a sample that must pin to a specific released version says so in
  its header and uses a pinned `PackageReference` instead of a `ProjectReference`.
- The current samples use only stable metadata APIs, so none is preview today. This
  section documents the policy for the first preview-API sample.

## Sample index

| Sample | Demonstrates | Preview APIs |
| --- | --- | --- |
| `DeltaSharp.Samples.GettingStarted` | Referencing and calling `DeltaSharp.Core` from a net8.0 app. | none |

## Running a sample

```bash
dotnet run --project samples/DeltaSharp.Samples.GettingStarted
```

## How CI builds samples

Two things build the sample, with different consequences:

- The required **`build-test-format`** gate runs `dotnet build DeltaSharp.sln`, which
  compiles the sample because it is listed in the solution. A sample that fails to compile
  is therefore caught and **reported** by the required gate — you should not merge a broken
  example.
- The informational **[`samples.yml`](../.github/workflows/samples.yml)** workflow builds
  each sample project directly and smoke-runs the getting-started sample, giving samples a
  dedicated signal. It is not a branch-protection required check, so it cannot deadlock the
  merge button on its own.

What **is** isolated is production **package validation** (#119 AC4): because the sample is
non-packable, `dotnet pack DeltaSharp.sln` skips it entirely, so it produces no package and
can never block the package-validation diagnostics for `DeltaSharp.Core`. The `pack-validate`
job stays Core-only, and [`pack.yml`](../.github/workflows/pack.yml) additionally fails if any
`*Samples*` project ever produces a package.
