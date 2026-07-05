# Test harness conventions and deterministic seed policy

> **Status:** living document. Created with
> [STORY-00.5.1](../../planning/epics/EPIC-00-engineering-foundations.md#story-0051-xunit-harness-and-deterministic-seed-policy).
> Grounded in [`.github/copilot-instructions.md`](../../../.github/copilot-instructions.md),
> [testing-conventions.md](testing-conventions.md), [repository-layout.md](repository-layout.md),
> [quality-gates.md](quality-gates.md), the [`coverlet.runsettings`](../../../coverlet.runsettings)
> coverage filter, and checklists [04](../checklists/04-testing-checklist.md),
> [04a](../checklists/04a-unit-testing-checklist.md), and
> [11](../checklists/11-documentation-support-checklist.md). Update it whenever the shared test
> settings, the seed helper, or the parallel-safety rule change.

This document defines the xUnit **execution harness** every DeltaSharp test project shares: the
centralized test settings, the deterministic-seed policy for randomized tests, and the
collection/fixture boundary rule that keeps parallel tests from interfering. It complements — and
does not restate — [testing-conventions.md](testing-conventions.md) (naming, test-access policy,
unit-vs-integration split) and [repository-layout.md](repository-layout.md) (project layout, target
frameworks). It is a distinct concern (how tests *run*), so it lives in its own page and is
cross-linked from both.

## What the harness provides

| Concern | Mechanism | Acceptance criterion |
| --- | --- | --- |
| Shared test settings | [`tests/Directory.Build.props`](../../../tests/Directory.Build.props) | #112(d) |
| Reproducible randomized tests | `TestSeed` + `SeededRandom` (linked shared source) | #112(a), (c) |
| Parallel-safety boundary | `EnvironmentSensitiveTestCollection` + the collection rule | #112(b) |

All three are compiled into each `*.Tests` assembly from a single shared location
(`tests/Shared/`), so a new test project inherits them with no per-project wiring.

## Shared test settings (`tests/Directory.Build.props`)

MSBuild auto-imports only the **nearest** `Directory.Build.props`, so
[`tests/Directory.Build.props`](../../../tests/Directory.Build.props) first chains to the
repository-root policy and then layers the settings common to **all** `.Tests` projects:

- `IsTestProject=true`, `IsPackable=false`, and `RestorePackagesWithLockFile=true` (CI restores in
  locked mode against the committed `packages.lock.json`).
- The shared toolchain package references — `Microsoft.NET.Test.Sdk`, `xunit`,
  `xunit.runner.visualstudio`, and `coverlet.collector` — with versions from
  [`Directory.Packages.props`](../../../Directory.Packages.props) (central package management). The
  runner and collector are dev-time only (`PrivateAssets=all`).
- The deterministic-seed harness, linked from `tests/Shared/**/*.cs`.

A new `tests/DeltaSharp.<Area>.Tests` project therefore declares only its own target framework(s)
and `ProjectReference`. Project-specific needs stay in the `.csproj`: `DeltaSharp.Core.Tests`
multi-targets `net8.0;net10.0`; `DeltaSharp.Engine.Tests` adds `Apache.Arrow` and
`AllowUnsafeBlocks`.

> **Note.** Double dashes (`--`) are illegal inside XML comments, so a comment describing a
> command flag such as locked-mode restore must spell it out in prose. A malformed
> `Directory.Build.props` fails to load and its package references silently vanish from the
> project.

## Deterministic seed policy

Randomized tests must be reproducible: a failure — especially in CI — has to be replayable
byte-for-byte on a developer machine. The harness formalizes the seed practice already used
ad hoc across the engine tests (a fixed literal per test) into one overridable, self-logging knob.

### `TestSeed` — resolving the base seed

`DeltaSharp.TestSupport.TestSeed` (an `internal static` helper) resolves the **base seed**:

- `TestSeed.EnvironmentVariable` is `"DELTASHARP_TEST_SEED"`. Setting it (from a shell or CI
  configuration) overrides the seed.
- `TestSeed.Default` is a fixed constant (`0x0DE17A5D`) used when the variable is unset or invalid,
  so unattended runs are reproducible with no configuration.
- `TestSeed.Resolve()` returns `Parse(Environment.GetEnvironmentVariable(EnvironmentVariable))`.
  `TestSeed.Parse(string?)` accepts a valid invariant-culture 32-bit integer and otherwise falls
  back to `Default` (null, blank, non-numeric, and out-of-range input all fall back). A **negative**
  value is a valid 32-bit seed and is accepted (for example `DELTASHARP_TEST_SEED=-1` resolves to
  `-1`); only non-integer, out-of-range (overflow), blank, or unset input falls back to `Default`.
- `TestSeed.Combine(int baseSeed, string scope)` mixes a per-scope salt into the base seed with
  FNV-1a over the scope characters, deliberately **not** `string.GetHashCode()` (which is
  randomized per process and therefore not reproducible across runs or machines).

### `SeededRandom` — the per-test stream

`DeltaSharp.TestSupport.SeededRandom` (an `internal sealed` facade over `System.Random`) is what a
test draws from. The **effective seed** that feeds the stream is `TestSeed.Combine(baseSeed, scope)`
of the resolved base seed and a per-test `Scope`, so every test gets an independent-but-reproducible
stream from the single override knob.

- `SeededRandom.Create(ITestOutputHelper output)` is the entry point for a **randomized** test.
  `scope` defaults to the calling test method name via `[CallerMemberName]`, and it **always** logs
  `SeedAnnouncement` to the test output, so a randomized test can never silently lose its seed on
  failure. There is deliberately **no** no-output `Create()` overload: an environment-resolved seed
  that was not logged would be unreproducible precisely when a test failed.
- `SeededRandom.ForSeed(int baseSeed)` bypasses configuration with an explicit base seed for
  **deterministic** self-tests. The seed is pinned in source, so the stream is reproducible by
  construction and needs no logging.
- The RNG surface is intentionally small: `Next()`, `Next(max)`, `Next(min, max)`, `NextDouble()`,
  `NextBool()`, and `NextBytes(byte[])`. The underlying `System.Random` is not exposed. A
  `SeededRandom` is **not thread-safe** (it wraps a single `System.Random`); create one instance per
  test rather than sharing it across threads.
- `BaseSeed`, `Seed` (effective), and `Scope` are readable; `ReproductionCommand` and
  `SeedAnnouncement` render the reproduction guidance.

The announcement is a single line:

```text
[deltasharp-seed] scope=<Scope> baseSeed=<BaseSeed> effectiveSeed=<Seed> | reproduce: DELTASHARP_TEST_SEED=<BaseSeed> dotnet test --filter "FullyQualifiedName~<Scope>"
```

The VSTest console logger surfaces a failing test's output messages (a "Standard Output Messages"
section), so on failure the seed and the reproduction command are visible in CI logs
(#112(a), (c)). Passing tests capture the same line without printing it at normal verbosity.

> **Caveat — `scope` defaults to the method name.** Because `scope` defaults to `[CallerMemberName]`,
> the effective seed and stream are keyed on the calling **method name**. Two different methods that
> share a name (across classes), or the multiple rows of a single `[Theory]`, therefore resolve to the
> **same** effective seed/stream, and the reproduction filter — `FullyQualifiedName~<method>`, a
> contains-match — can over-select (replay more than the one test you meant). For a `[Theory]` or any
> duplicate-named method, pass an **explicit** `scope` (for example include the case parameters) to
> give each row an independent stream and an unambiguous filter.
>
> An explicit `scope` must be **filter-safe** — identifier-like. Avoid spaces, quotes, commas, and
> other characters that would malform the VSTest `--filter` expression in `ReproductionCommand`.

### Writing a randomized test

```csharp
using DeltaSharp.TestSupport;
using Xunit;
using Xunit.Abstractions;

public sealed class ShuffleTests
{
    private readonly ITestOutputHelper _output;

    public ShuffleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Shuffle_PreservesTheMultiset()
    {
        SeededRandom random = SeededRandom.Create(_output);
        // ... draw from `random`; assert a property that must hold for every seed.
    }
}
```

Prefer **property** assertions that hold for every seed (as in the shipped
`FisherYatesShuffle_IsAlwaysAPermutation` demonstration): they pass deterministically, stay robust
to a seed override, and still pin down a regression to an exact, replayable seed. This matches
checklist 04a — "Property-based tests use deterministic seeds and print the seed ... on failure".

## How the harness is shared, and why coverage is unaffected

The harness is a **linked shared source file** (`tests/Shared/*.cs` compiled into each `.Tests`
assembly via the `<Compile>` item in `tests/Directory.Build.props`), not a standalone assembly.
This is deliberate:

- The coverage gate measures only production assemblies and excludes `[*.Tests]*` **by
  assembly-name suffix** (see [`coverlet.runsettings`](../../../coverlet.runsettings) and
  [quality-gates.md](quality-gates.md)). Because the harness compiles *into* the `.Tests`
  assemblies, it is excluded automatically and adds no production lines to the denominator — no
  change to `coverlet.runsettings` or `tools/coverage/coverage-config.json` is required, and the
  gate's fail-closed `expectedAssemblies` allowlist stays exactly the four production assemblies.
- A standalone `DeltaSharp.TestSupport` assembly would instead match the `[DeltaSharp.*]` include
  and **leak into the coverage denominator** unless explicitly excluded in both files (the caveat
  called out in `coverlet.runsettings`). Linking the source avoids that coupling entirely.

The files live outside every project cone (`tests/Shared/`), so the SDK default `**/*.cs` glob never
double-includes them. The helper types are `internal`, so each `.Tests` assembly owns its own copy
with no cross-assembly leakage; the collection definition below is `public` because xUnit discovers
collection definitions by reflection.

## Parallel-safety: the collection/fixture boundary rule

xUnit runs distinct **collections** in parallel and serializes the classes within one collection. A
`SeededRandom` is local to a test and shares nothing, so randomized tests parallelize freely.

When a test reads or mutates **process-wide** state — environment variables, ambient configuration,
the current directory, or a static slot — it must join a collection marked
`DisableParallelization=true` so it cannot race other tests or perturb them. This is the same rule
`SparkSessionTestCollection` already applies to the process-wide active/default `SparkSession` slots
([`tests/DeltaSharp.Core.Tests/SparkSessionTestCollection.cs`](../../../tests/DeltaSharp.Core.Tests/SparkSessionTestCollection.cs)).

The harness provides a ready-made boundary for the common case:
`DeltaSharp.TestSupport.EnvironmentSensitiveTestCollection` (collection name
`"DeltaSharp environment-sensitive"`). A test that touches an environment variable — for example
the `DELTASHARP_TEST_SEED` override tests — joins it and restores the prior value in a `finally`:

```csharp
[Collection(EnvironmentSensitiveTestCollection.Name)]
public sealed class SeedEnvironmentOverrideTests
{
    [Fact]
    public void Resolve_UsesEnvironmentOverride_WhenSet()
    {
        string? original = Environment.GetEnvironmentVariable(TestSeed.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(TestSeed.EnvironmentVariable, "13579");
            Assert.Equal(13579, TestSeed.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestSeed.EnvironmentVariable, original);
        }
    }
}
```

Since xUnit collections are per-assembly, both the collection and the seed helper are defined in
every `.Tests` assembly (from the linked source), so any test project can use the boundary without
extra wiring.

## `.Tests` naming and shared settings

Test-project naming and placement (`tests/DeltaSharp.<Area>.Tests`, suffixed `.Tests`, mirroring
`src/`) and the test-access policy are owned by [testing-conventions.md](testing-conventions.md).
The addition here is that a conforming project also **inherits the shared settings** from
`tests/Directory.Build.props` automatically — that is the "uses the shared test settings" half of
#112(d). Adding a test project is unchanged from the checklist in
[repository-layout.md](repository-layout.md#adding-a-new-project); the shared props simply removes
the per-project toolchain boilerplate.

## Reproducing a CI failure

1. Find the failing test's output in the CI log. The `[deltasharp-seed]` line names the base seed
   and the exact command.
2. Copy the `reproduce:` command, for example:

   ```bash
   DELTASHARP_TEST_SEED=232880733 dotnet test --filter "FullyQualifiedName~Shuffle_PreservesTheMultiset"
   ```

3. Run it locally. `TestSeed.Resolve()` reads the override, `TestSeed.Combine` recomputes the same
   effective seed from the (stable) scope, and the stream replays exactly.

To force a specific seed across an entire run — for example to hunt for a rare failure — set the
environment variable before `dotnet test`:

```bash
DELTASHARP_TEST_SEED=12345 dotnet test DeltaSharp.sln -c Release
```

## References

- [Test project conventions and test-access policy](testing-conventions.md)
- [Repository layout and project conventions](repository-layout.md)
- [Quality gates and coverage policy](quality-gates.md)
- [`coverlet.runsettings`](../../../coverlet.runsettings) — the coverage include/exclude filter
- [04 — Testing Checklist](../checklists/04-testing-checklist.md)
- [04a — Unit Testing Checklist](../checklists/04a-unit-testing-checklist.md)
- [11 — Documentation Support Checklist](../checklists/11-documentation-support-checklist.md)
- [ADR-0014: Target framework and AOT posture](../../adr/0014-target-framework-aot.md)
