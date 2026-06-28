# DeltaSharp samples

Runnable example applications that show how to consume DeltaSharp. Samples are
**examples, not products**: they are never published as NuGet packages and they are
isolated from the production package-validation gate, so a broken sample never blocks a
release of the real libraries.

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

Samples are built by a dedicated, **informational** workflow,
[`.github/workflows/samples.yml`](../.github/workflows/samples.yml) — not by the required
`build-test-format` gate's packaging path and not by the `pack-validate` job. A sample
failure is therefore **reported** by the samples workflow without blocking the
production package-validation diagnostics for `DeltaSharp.Core` (#119 AC4). The samples
workflow is not a branch-protection required check, so it can never deadlock the merge
button.

Samples are still listed in `DeltaSharp.sln` for IDE discovery and local builds; because
they are non-packable they contribute no package to `pack-validate`.
