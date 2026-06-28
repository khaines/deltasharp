# Test project conventions & test-access policy

> **Status:** living document. Created with
> [STORY-01.1.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-01-project-build-platform.md#story-0112-test-project-conventions).
> Grounded in [`.github/copilot-instructions.md`](../../../.github/copilot-instructions.md),
> [repository-layout.md](repository-layout.md), [api-governance.md](api-governance.md),
> and checklists [04](../checklists/04-testing-checklist.md),
> [04a](../checklists/04a-unit-testing-checklist.md),
> [04b](../checklists/04b-integration-testing-checklist.md), and
> [11](../checklists/11-documentation-support-checklist.md). Update it whenever the test
> layout, access policy, or unit/integration split changes.

This document defines how DeltaSharp test projects are named, placed, referenced, and how
tests reach implementation details. It is the policy later epics rely on — for example the
EPIC-02 engine contracts (`src/DeltaSharp.Engine/Types/`, `src/DeltaSharp.Engine/Columnar/`)
are exercised by `DeltaSharp.Engine.Tests` under this policy.

## Naming and placement (#118 AC1)

- Every production project under `src/DeltaSharp.<Area>/` has a matching test project at
  `tests/DeltaSharp.<Area>.Tests/`. The test project name **ends with `.Tests`** and the
  folder mirrors the `src/` layout.
- The test assembly name equals the project name (`DeltaSharp.<Area>.Tests`). This 1:1
  mapping is what the test-access policy below keys on.
- Test projects are `net10.0` to match the engine/executor TFM, except
  `DeltaSharp.Core.Tests`, which multi-targets `net8.0;net10.0` so the public surface is
  compiled and executed on both of `DeltaSharp.Core`'s target frameworks
  ([ADR-0014](../../adr/0014-target-framework-aot.md)).

| Production project | Test project | TFM(s) |
| --- | --- | --- |
| `DeltaSharp.Core` | `tests/DeltaSharp.Core.Tests` | `net8.0;net10.0` |
| `DeltaSharp.Engine` | `tests/DeltaSharp.Engine.Tests` | `net10.0` |
| `DeltaSharp.Executor` | `tests/DeltaSharp.Executor.Tests` | `net10.0` |

## Project reference, not package (#118 AC2)

Test projects reference the code under test with a `ProjectReference`, never a
`PackageReference` to a built artifact, so `dotnet test` always exercises the working tree:

```xml
<ProjectReference Include="../../src/DeltaSharp.Engine/DeltaSharp.Engine.csproj" />
```

Third-party test dependencies (xUnit, `Microsoft.NET.Test.Sdk`) are versioned centrally in
[`Directory.Packages.props`](../../../Directory.Packages.props) and referenced without inline
versions. Test projects set `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`
because they take real third-party packages; CI restores in `--locked-mode`.

## Test-access policy (#118 AC3)

DeltaSharp uses **two complementary mechanisms** so tests can reach implementation detail
without weakening the shipped public surface. Choose per member:

### 1. Public-in-unshipped-assembly — for engine contracts

`DeltaSharp.Engine` is an **internal assembly that is never packed** and carries **no
PublicAPI baseline** (only the packable `DeltaSharp.Core` is tracked by
`PublicApiAnalyzers` — see [api-governance.md](api-governance.md)). Declaring an engine
contract `public` therefore exposes it to other engine assemblies and to
`DeltaSharp.Engine.Tests`, **without** adding anything to a shipped NuGet surface.

Use `public` for the stable contracts operators, kernels, rows, and APIs bind to across
assemblies — the `DataType`/`Schema` model (#141) and the `ColumnVector`/`ColumnBatch`
contracts (#133). This keeps cross-assembly seams real `public` types while remaining
engine-internal in the product sense.

### 2. `InternalsVisibleTo` — for genuinely-internal details

Implementation details that should **not** be part of any cross-assembly seam stay
`internal`, and each production assembly grants internals-visibility to its own test
assembly. This is wired **once, centrally**, in
[`Directory.Build.props`](../../../Directory.Build.props) (the `lane:01.1` block) for every
production assembly under `src/`:

```xml
<ItemGroup Condition="'$(DeltaSharpIsProductionAssembly)' == 'true'">
  <InternalsVisibleTo Include="$(MSBuildProjectName).Tests" />
</ItemGroup>
```

`$(MSBuildProjectName)` is a reserved property known when the props file is imported, and
equals the assembly name by our naming convention, so it resolves to
`<AssemblyName>.Tests` (for example `DeltaSharp.Engine` → `DeltaSharp.Engine.Tests`). The
SDK's `GenerateAssemblyInfo` target materializes the
`System.Runtime.CompilerServices.InternalsVisibleToAttribute`. Assemblies are not
strong-named, so the bare assembly name (no public key) is correct.

**How later lanes follow it:** declare cross-assembly engine contracts `public`; keep
helpers, validators, and layout internals `internal` and unit-test them directly through
the friend grant. No per-project wiring is needed — adding a new `src/` project enrolls it
automatically.

> A regression guard test (`TestAccessPolicyTests`) asserts that each production assembly
> grants internals-visibility to its `.Tests` assembly, so the policy cannot silently
> regress if `GenerateAssemblyInfo` or the gate changes.

## Unit vs integration scopes (#118 AC4)

Unit and integration tests are distinguishable **by project name** and, within a project,
**by category trait**:

- **Unit tests** live in `tests/DeltaSharp.<Area>.Tests`. They are fast, in-process, and
  use no external resources (checklist [04a](../checklists/04a-unit-testing-checklist.md)).
- **Integration tests** (added when behavior crosses process, storage, filesystem,
  object-store, or Kubernetes boundaries — checklist
  [04b](../checklists/04b-integration-testing-checklist.md)) live in a separate
  `tests/DeltaSharp.<Area>.IntegrationTests` project. The distinct `.IntegrationTests`
  suffix keeps the slow/resource-bound scope obvious in the solution and on disk.
- Where a finer split is useful inside a project, tag integration-style tests with
  `[Trait("Category", "Integration")]`.

Both scopes run through `dotnet test` with documented filters:

```bash
dotnet test --filter Category!=Integration   # unit-only (fast inner loop)
dotnet test --filter Category=Integration     # integration-only
```

Long-running integration suites use the documented **900s** integration timeout budget
rather than arbitrary sleeps. No `DeltaSharp.*.IntegrationTests` project exists yet (the
skeleton has no cross-boundary behavior), so today `--filter Category=Integration` simply
matches no tests (a clean no-op) and `Category!=Integration` runs the full unit suite; this
names the convention so the first integration project slots in without churn.

## References

- [Repository layout & project conventions](repository-layout.md)
- [API governance: public-API baselines and banned APIs](api-governance.md)
- [04 — Testing Checklist](../checklists/04-testing-checklist.md)
- [04a — Unit Testing Checklist](../checklists/04a-unit-testing-checklist.md)
- [04b — Integration Testing Checklist](../checklists/04b-integration-testing-checklist.md)
- [ADR-0014: Target framework and AOT posture](../../adr/0014-target-framework-aot.md)
