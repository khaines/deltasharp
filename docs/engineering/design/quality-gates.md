# Quality gates: warnings-as-errors, formatting, and coverage

> **Status:** living document. Created with FEAT-00.2 —
> [STORY-00.2.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-00-engineering-foundations.md#story-0021-analyzer-and-warnings-as-errors-gate)
> (analyzers & warnings-as-errors),
> [STORY-00.2.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-00-engineering-foundations.md#story-0022-formatting-gate)
> (formatting), and
> [STORY-00.2.3](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-00-engineering-foundations.md#story-0023-coverage-collection-and-thresholds)
> (coverage). Grounded in [ADR-0014](../../adr/0014-target-framework-aot.md) (TFM + AOT
> posture), [api-governance.md](api-governance.md) (analyzer enforcement model),
> [testing-conventions.md](testing-conventions.md), and checklists
> [03](../checklists/03-coding-conventions-checklist.md),
> [03a](../checklists/03a-dotnet-coding-standards.md),
> [04](../checklists/04-testing-checklist.md),
> [04a](../checklists/04a-unit-testing-checklist.md),
> [08](../checklists/08-performance-checklist.md), and
> [11](../checklists/11-documentation-support-checklist.md). Update it whenever a gate, its
> configuration, or the coverage threshold changes.

DeltaSharp enforces **three automated quality gates** on every pull request and every push to
`main`, across two CI jobs in [`.github/workflows/ci.yml`](../../../.github/workflows/ci.yml) so a
policy violation surfaces on the pull request rather than accumulating as debt:

- **`build-test-format`** — the analyzer/warnings-as-errors gate and the formatting gate, and the
  authoritative **correctness** (test pass/fail) run. Tests run here **uninstrumented**.
- **`coverage`** — the line-coverage measurement and threshold gate. It runs in parallel and is
  **measurement-only**: because coverage instrumentation perturbs execution timing, it must never
  be able to change the correctness verdict, which `build-test-format` owns (see
  [Why coverage is a separate job](#why-coverage-is-a-separate-job)).

> **Which checks are merge-blocking.** A CI job blocks the merge button once it is listed in
> the repository's **branch-protection required status checks**. On `main` the required checks
> are **`build-test-format`**, **`coverage`**, and **`dco`**, so **all three gates block merge**
> — including **`coverage`**, which was wired into the required set after it first ran on `main`
> (#456; a status check can only be made required once it has executed on the default branch).
> The FEAT-00.3 supply-chain security scans (`sca`, `secret-scan`, `sbom`,
> `dependency-review`) run on every PR and are documented for the same post-merge promotion in
> [supply-chain-security.md](supply-chain-security.md).

| # | Gate | Job | Enforced by | Fails when |
| --- | --- | --- | --- | --- |
| 1 | Analyzers & warnings-as-errors | `build-test-format` | `TreatWarningsAsErrors=true` + the .NET / trim / AOT / API analyzers | any analyzer or compiler **warning** is emitted by a build |
| 2 | Formatting | `build-test-format` | `dotnet format --verify-no-changes` against the checked-in `.editorconfig` | a file does not match the formatting/style rules |
| 3 | Coverage | `coverage` | `coverlet.collector` + [`tools/coverage/coverage-gate.py`](../../../tools/coverage/coverage-gate.py) | merged line coverage is below the configured floor |

Every gate has a **local command that reproduces the CI result exactly**, because the policy
lives in checked-in configuration (`Directory.Build.props`, `.editorconfig`,
`coverlet.runsettings`, `tools/coverage/coverage-config.json`) that both local builds and CI
consume — CI adds no hidden flags.

| Gate | CI step (`ci.yml`) | Local command that reproduces it |
| --- | --- | --- |
| 1. Warnings-as-errors | `build-test-format` › `dotnet build DeltaSharp.sln -c Release --no-restore` | `dotnet build -c Release` |
| 2. Formatting | `build-test-format` › `dotnet format DeltaSharp.sln --verify-no-changes --no-restore` | `dotnet format --verify-no-changes` (fix with `dotnet format`) |
| 3. Coverage | `coverage` › `dotnet test … --collect:"XPlat Code Coverage" --settings coverlet.runsettings` then `python3 tools/coverage/coverage-gate.py` | see [Reproducing the coverage gate locally](#reproducing-the-coverage-gate-locally) |

---

## Gate 1 — Analyzers and warnings-as-errors (STORY-00.2.1)

### What it enforces

All of the following are wired centrally in
[`Directory.Build.props`](../../../Directory.Build.props) so no project can drift:

- **`TreatWarningsAsErrors=true`** — the keystone. Every compiler warning **and** every
  analyzer warning becomes a build error. Relaxing it would weaken all the analyzers below at
  once, so it is set once, repository-wide.
- **`EnableNETAnalyzers=true`** with **`AnalysisLevel=latest`** — the full current .NET
  analyzer set (the `CAxxxx` rules) runs on every project.
- **Trim / AOT / single-file analyzers** — `EnableTrimAnalyzer`, `EnableAotAnalyzer`, and
  `EnableSingleFileAnalyzer` are enabled **once, centrally, in `Directory.Build.props`** under the
  `DeltaSharpIsProductionAssembly` condition, so they apply to **every** production assembly under
  `src/` (`DeltaSharp.Abstractions`, `DeltaSharp.Core`, `DeltaSharp.Engine`, `DeltaSharp.Executor`)
  and **no project can drift** — a new `src/` assembly inherits them automatically, and none can
  silently opt out. They surface `IL2xxx`/`IL3xxx` dataflow warnings, which — under
  warnings-as-errors — fail the build. This is the standing enforcement of the
  [ADR-0014](../../adr/0014-target-framework-aot.md)
  AOT posture. The analyzers (not `IsAotCompatible`/`IsTrimmable`) are used so no real
  trim/AOT rewrite happens at build; a real AOT publish is verified separately by the
  `aot.yml` workflow. Enabling the analyzers does, however, make the SDK inject an
  implicit, SDK-patch-tied `Microsoft.NET.ILLink.Tasks` (it ships the trim analyzer); for
  the production assemblies that commit a lock file (`DeltaSharp.Engine`,
  `DeltaSharp.Storage`) its version is pinned via `VersionOverride` in `Directory.Build.props`
  ([#468](https://github.com/khaines/deltasharp/issues/468)) so locked-mode restore does not
  drift across SDK patches.
- **`BannedApiAnalyzers` (`RS0030`) and `PublicApiAnalyzers` (`RS0016`/`RS0017`/`RS0025`)** —
  API-governance analyzers that also ride on warnings-as-errors. Their scope and ban list are
  documented in [api-governance.md](api-governance.md); this gate is what makes their
  diagnostics build-breaking.

The one deliberate relaxation is **`WarningsNotAsErrors=$(WarningsNotAsErrors);NU1901;NU1902`**:
a newly-published **low/moderate** NuGet advisory (`NU1901`/`NU1902`) does **not** break an
otherwise-unrelated build, while **high/critical** advisories (`NU1903`/`NU1904`) remain
build-breaking. This is a scoped, documented exception — not a blanket suppression.

### The local command reproduces CI (AC3)

CI builds with `dotnet build DeltaSharp.sln -c Release`. Because warnings-as-errors is the
`TreatWarningsAsErrors` **MSBuild property** (not a CLI flag CI adds), a plain local
`dotnet build -c Release` produces the **same** errors. The `dotnet build -c Release
-warnaserror` form used in some docs is a redundant explicit equivalent — it does not add
anything the property has not already set.

**Verified.** Introducing a determinism-banned call into production code
(`private static readonly Guid _probe = Guid.NewGuid();` in `DeltaSharp.Core`) fails the build
identically on both target frameworks:

```text
error RS0030: The symbol 'Guid.NewGuid()' is banned in this project: [determinism] …
  …DeltaSharp.Core.csproj::TargetFramework=net8.0
  …DeltaSharp.Core.csproj::TargetFramework=net10.0
Build FAILED.
```

### Suppression convention: every suppression is scoped **and** justified (AC4)

A trim/AOT (or any other) analyzer warning is **never silently suppressed**. Each suppression
in production code must be both:

1. **Scoped** to the smallest possible region — a single statement (`#pragma warning
   disable`/`restore` around one call), a single member (`[UnconditionalSuppressMessage]` /
   `[SuppressMessage]` on one method), or a single target framework (`#if !NET9_0_OR_GREATER`).
   Never a whole file, and never a project-wide `<NoWarn>`.
2. **Justified** inline — a comment stating the invariant that makes it safe and linking to the
   ADR that allows it.

Every suppression currently present in `src/` follows this convention:

| Location | Suppressed | Scope | Justification |
| --- | --- | --- | --- |
| `DeltaSharp.Core/Session/RowDecoderFactory.cs` | `IL3050` (AOT) | `[UnconditionalSuppressMessage]` on one method, gated `#if !NET9_0_OR_GREATER` | Compiled setter tier runs only when `RuntimeFeature.IsDynamicCodeSupported`; net8.0 lacks `[FeatureGuard]` to prove elision (ADR-0001) |
| `DeltaSharp.Core/Session/RowDecoderFactory.cs` | `RS0030` (`Expression.Compile`) | `#pragma` around one statement | Scoped ADR-0001 codegen tier, guarded by `UseCompiledSetters` |
| `DeltaSharp.Engine/Execution/CompiledBackend.cs` | `RS0030` | `#pragma` around one statement | Scoped ADR-0001 codegen tier |
| `DeltaSharp.Engine/Execution/Expressions/CompiledExpressionLowering.cs` | `RS0030` | `#pragma` around one statement | Scoped ADR-0001 codegen tier |

**AOT suppression reference pattern.** For a `[RequiresDynamicCode]` call reached only behind a
runtime feature guard, the correct annotation differs by target framework, and this matters
because the Native AOT compiler (ILC) honors attributes but **not** `#pragma`:

- **net9.0+** — annotate the guard property with **`[FeatureGuard(typeof(RequiresDynamicCodeAttribute))]`**
  so the analyzer proves the call is elided from an AOT image (see
  `DeltaSharp.Engine/Execution/ExecutionBackends.cs`).
- **net8.0** (no `[FeatureGuard]`) — assert the guarantee with
  **`[UnconditionalSuppressMessage("AOT", "IL3050", Justification = …)]`**, which both the build
  analyzer and the ILC publish step honor (see `DeltaSharp.Core/Session/RowDecoderFactory.cs`).

A bare `#pragma warning disable IL3050` is **insufficient** for AOT because ILC ignores it; use
it only for build-time-only analyzers such as `RS0030`. Reviewers reject unscoped or
unjustified suppressions. See [api-governance.md](api-governance.md#requesting-a-scoped-exception)
for the banned-API exception process.

### Generated-code exclusions are scoped to generated files only (AC2)

The repository contains **no generated code** today and therefore no generated-code analyzer
exclusion. Two facts keep this AC satisfied as generated code arrives:

- **Roslyn auto-detects generated code.** Files ending in `.g.cs`/`.Designer.cs`, or carrying a
  `// <auto-generated/>` header, are treated as generated and most analyzers skip them by
  default — automatically scoped, no repo-wide switch involved.
- **The only analyzer exclusion in the repo is already path-scoped.** `.editorconfig` sets
  `dotnet_diagnostic.CA1707.severity = none` **under a `[tests/**/*.cs]` section only**, so
  test methods may use `Method_Scenario_Expectation` underscores while **production code is
  unaffected**. This is the pattern any future exclusion must follow: scope it with an
  `.editorconfig` path glob (e.g. `[**/*.g.cs]`) or the `generated_code = true` key — never a
  repository-wide severity downgrade or `<NoWarn>`.

---

## Gate 2 — Formatting (STORY-00.2.2)

### What it enforces

CI runs **`dotnet format DeltaSharp.sln --verify-no-changes --no-restore`**, which checks
whitespace **and** the .NET code-style rules defined in the checked-in
[`.editorconfig`](../../../.editorconfig) (AC2) — using directives sorted and placed outside
the namespace, file-scoped namespaces, Allman braces, `_camelCase` private fields, UTF-8/LF
with a final newline, and the per-file-type indent sizes. `--verify-no-changes` makes the
command **report** violations and exit non-zero instead of rewriting files, so the gate never
mutates the tree.

### Local remediation is documented (AC3)

The fix command is the same tool **without** `--verify-no-changes`:

```bash
dotnet format                       # auto-fix formatting + code style (remediation)
dotnet format --verify-no-changes   # the CI check: report violations, exit non-zero
```

This is documented in [`CONTRIBUTING.md`](../../../CONTRIBUTING.md) so contributors find it
without reading CI internals.

**Verified.** A file with collapsed/extra whitespace fails the check with actionable
diagnostics and a non-zero exit:

```text
error WHITESPACE: Fix whitespace formatting. Delete 4 characters. …_ScratchFmtProbe.cs(5,5)
```

---

## Gate 3 — Coverage collection and thresholds (STORY-00.2.3)

### Collection

Tests run with the **`coverlet.collector`** data collector
([`Directory.Packages.props`](../../../Directory.Packages.props), pinned `10.0.1`, referenced
`PrivateAssets=all` in each test project):

```bash
dotnet test DeltaSharp.sln -c Release --no-build \
  --collect:"XPlat Code Coverage" \
  --settings coverlet.runsettings \
  --results-directory TestResults
```

The **`coverage`** CI job runs this after its own restore + build (adding `--verbosity normal`; see
[Why coverage is a separate job](#why-coverage-is-a-separate-job)).
The collector instruments the already-built assemblies at run time and writes one **Cobertura**
report per test assembly per target framework under `TestResults/<guid>/coverage.cobertura.xml`.

[`coverlet.runsettings`](../../../coverlet.runsettings) defines **what** is measured:

- **`<Include>[DeltaSharp.*]*</Include>`** then
  **`<Exclude>[*.Tests]*,[DeltaSharp.Samples.*]*</Exclude>`** — measure the DeltaSharp production
  libraries under `src/` only, and remove both the test assemblies and the `samples/` example
  apps from the denominator (neither is a shippable library).
- **`<ExcludeByAttribute>`** — never count `[GeneratedCode]`, `[CompilerGenerated]`,
  `[ExcludeFromCodeCoverage]`, or `[Obsolete]` members (attribute-scoped, so any future
  generated code is excluded at the member level, not repo-wide).
- **`<SkipAutoProps>true</SkipAutoProps>`** — trivial auto-property accessors are not counted,
  keeping the metric on real logic and stable across runs.
- **`<SingleHit>true</SingleHit>`** — record each line as a single hit rather than an incrementing
  count. The gate measures hit **presence** (`hits > 0`), never counts, so this is
  coverage-identical (verified: the merged total is byte-for-byte the same either way). It also
  removes the per-execution counter write from every instrumented line, lowering the overhead of a
  hot multi-threaded loop. It is **not**, on its own, relied on to keep the correctness verdict
  stable — that guarantee comes from running the correctness tests uninstrumented in a separate job
  (see [Why coverage is a separate job](#why-coverage-is-a-separate-job)).

**Artifacts (AC1).** CI publishes the reports and the merged summary as the **`coverage-report`**
workflow artifact (`TestResults/**/coverage.cobertura.xml` + `TestResults/coverage-summary.md`),
uploaded with `if: ${{ !cancelled() }}` so the report is available **even when the gate fails**.

### Why coverage is a separate job

Coverage is collected in the **`coverage`** job, **not** in `build-test-format`, and that
separation is deliberate. coverlet instruments the production assemblies at run time, which changes
execution timing, so a test that flakes *only under instrumentation* must never be able to fail the
build.

What the job split does:

- **`build-test-format` runs the tests uninstrumented** and is the single source of truth for
  correctness — exactly as before coverage existed, and exactly as on `main`.
- **The `coverage` job's collect step is measurement-only.** It carries `continue-on-error: true`
  so a test that flakes *only under instrumentation* can never fail the build — correctness is
  already gated by `build-test-format` — while the threshold gate still runs on the emitted reports
  (a genuinely missing report fails the gate, so the tolerance is fail-safe).

> **History (resolved).** The `coverage` job previously excluded one `SparkSession` lifecycle test
> — `SparkSessionConcurrencyTests.GetOrCreate_RacingStop_NeverReusesAStoppedSession` — with a
> coverage-neutral `--filter`, because it was a **25,000-iteration stress test** whose oracle read
> `IsActive` *after* `GetOrCreate` returned and released `_globalLock`; that read is inherently racy
> against a legitimate post-return `Stop`, and coverlet's widened timing windows amplified it into
> rare false failures ([#454](https://github.com/khaines/deltasharp/issues/454)). That test has been
> **rewritten to be deterministic**: it drives the race through the internal `SparkSession.ReuseRaceProbe`
> seam (the sibling of `RuntimeConfig.StopRaceProbe`, which the F1 test uses), pausing the getter at
> the exact in-lock reuse window and asserting an **exact oracle** — the session it committed to reuse
> stayed active for the *entire in-lock decision window* — rather than sampling mutable state after
> the lock is released. The oracle can no longer false-positive on a legitimate post-return `Stop`, so
> the test now runs **instrumented in the `coverage` job** with **no `--filter` exclusion**, and there
> is no residual oracle caveat to track.

### The merge: why per-report sums are wrong

Because `Include` measures *all* DeltaSharp assemblies that each test host loads, the **same
production line appears in several reports with different hit counts** — `DeltaSharp.Core` is
well covered by `DeltaSharp.Core.Tests` but barely touched by `DeltaSharp.Executor.Tests`, and
`DeltaSharp.Core.Tests` runs on **both** net8.0 and net10.0. Summing each report's
`lines-covered`/`lines-valid` would double- and quadruple-count those lines and yield a
meaningless number.

[`tools/coverage/coverage-gate.py`](../../../tools/coverage/coverage-gate.py) instead **merges by
unioning per-`(file, line)` hits**: a line counts as covered if `hits > 0` in **any** report,
and the denominator is the set of **distinct** `(file, line)` pairs. This de-duplicates the
multi-TFM and multi-suite overlap — the same semantics ReportGenerator uses — implemented with
the Python 3 standard library only (no third-party package, no network), so the gate is
deterministic and offline.

### The gate: threshold enforcement with the measured value visible (AC2)

The enforced floor and the ratcheting policy live in **one machine-readable single source of
truth**, [`tools/coverage/coverage-config.json`](../../../tools/coverage/coverage-config.json):

```jsonc
"minimumLineCoverage": 87.0,
"ratchetPolicy": "monotonic-non-decreasing"
```

The gate reads that file, prints a per-assembly breakdown and the **measured value against the
threshold**, and **exits non-zero when merged line coverage is below the floor** (emitting a
GitHub `::error::` annotation so the failure is visible in the checks UI):

```text
FAIL: measured line coverage 82.10% vs threshold 87.00%
::error::coverage gate FAIL — measured line coverage 82.10% vs threshold 87.00%
```

### Fail-closed on a partial report set (provenance)

Merging *whatever reports were globbed* is **fail-open**: if one test assembly's report is lost
(a crashed or dropped suite) or deleted, that assembly silently vanishes from the denominator, which
can make coverage **rise** and the gate **pass** while a whole suite went missing. It is also an
injection vector — a report could be removed (or a fake one planted) to move the number. The gate
closes this two ways:

- **Expected-assembly provenance allowlist (exact set).** `coverage-config.json` carries an
  **`expectedAssemblies`** list (`DeltaSharp.Abstractions`, `DeltaSharp.Core`, `DeltaSharp.Engine`,
  `DeltaSharp.Executor`), treated as the **exact** set of production assemblies — fail-closed in
  **both** directions. Every listed assembly **must** appear in the merged report set with
  measurable lines; if any is **missing** the gate **exits non-zero (fail-closed)** with a
  `::error::` naming it, instead of letting it drop out of the denominator. Symmetrically, any
  **unexpected** assembly present in the reports (a planted/leaked or trivially-100%-covered
  package) also **fails closed** — an out-of-allowlist assembly must not dilute or inflate the
  aggregate. Coverage is computed over **only** the allowlisted set, so no extra package can move
  the number. Adding a genuine new `src/` production assembly is a deliberate governance event:
  add it to this list when it lands (mirrors the PublicAPI baseline).
- **The allowlist itself cannot drift (fail-closed provenance).** A defense-in-depth layer removes
  the two ways the allowlist could silently become untrustworthy. First, an **empty or absent**
  `expectedAssemblies` **fails closed** (`::error::` + non-zero exit) instead of reverting to
  fail-open global accounting over every globbed package — emptying the list can no longer disarm
  the gate. Second, the gate **derives the ground-truth production set from `src/`** (every
  `src/**/*.csproj`'s `AssemblyName`, matching the `DeltaSharpIsProductionAssembly` path rule in
  [`Directory.Build.props`](../../../Directory.Build.props)) and requires `expectedAssemblies` to
  **exactly equal** it. If the two diverge — a new `src/` assembly was added but not allowlisted, or
  a stale allowlist entry no longer exists in `src/` — the gate **fails closed** naming the drift.
  This gives the allowlist the same **cannot-drift** property the analyzer/formatting hoist has:
  forgetting to enroll a new assembly is caught mechanically, not left to reviewer vigilance.
- **In-run reports only.** The `coverage` job deletes any pre-existing `TestResults` **before**
  collecting, so the gate trusts only reports generated by the collect step in that run (closing
  stale-report reuse and a planted-report injection).
- **Unrounded pass decision.** The measured percentage is compared to the floor **unrounded** — a
  value strictly below the floor that merely rounds up to it (for example 86.999% → `87.00`)
  **fails**; only the displayed value is rounded.

**Reproduced.** Dropping the single `DeltaSharp.Executor` report from the merged set removes that
assembly from the denominator and makes measured coverage **rise 89.16% → 89.50%**; the *old*
fail-open gate **passed** on that partial set. With the provenance check the gate now **exits
non-zero** — `::error::coverage report is missing expected production assembly 'DeltaSharp.Executor'`
— so a lost suite fails the gate rather than being masked. Symmetrically, planting a
100%-covered `Fake.Assembly` into the report set (which would inflate a below-floor real set to a
false PASS) is rejected — `::error::coverage report contains unexpected production assembly … not
in the expectedAssemblies allowlist`. The existing all-missing / malformed / zero-line cases still
exit `2` as before. These exit-code contracts (missing, unexpected/inflate, rounding-boundary,
malformed, empty-allowlist, allowlist-vs-`src/` drift) are regression-tested by
`tools/coverage/coverage-gate-selftest.py`, which CI runs before the gate.

**Residual (documented, not fully closeable).** The provenance allowlist authenticates an assembly
by its Cobertura `<package name>`; a report that *forges* an allowlisted name (an **in-list
synthetic spoof**) is still trusted on its face. This is not closeable by content inspection alone —
line counts are legitimately unbounded — so the primary control is the **in-run wipe**: because the
`coverage` job `rm -rf TestResults` before collecting and the gate reads only that run's freshly
generated reports, planting a forged report requires arbitrary code execution *inside* the CI job,
which already subverts the gate itself (a strictly larger compromise). We therefore accept the
residual rather than add brittle magic-number line bounds that would false-fail on legitimate growth.

### Measured baseline

Merged, TFM-de-duplicated line coverage at the time this gate was introduced (all 4,856 tests
green):

| Assembly | Covered | Lines | Line % |
| --- | ---: | ---: | ---: |
| DeltaSharp.Abstractions | 389 | 424 | 91.75% |
| DeltaSharp.Core | 3310 | 3818 | 86.69% |
| DeltaSharp.Engine | 6337 | 6931 | 91.43% |
| DeltaSharp.Executor | 1090 | 1305 | 83.52% |
| **TOTAL** | **11126** | **12478** | **89.16%** |

### Exclusions prevent false failures (AC3)

- **Test projects** carry no shippable code and are removed from the measurement by the
  `Exclude [*.Tests]*` filter, so they can never dilute or inflate the percentage. **Caveat:** that
  filter keys on the **`.Tests` assembly-name suffix**. A test-*support* assembly that does **not**
  end in `.Tests` (e.g. a shared `DeltaSharp.TestFixtures` helper) would match `[DeltaSharp.*]` and
  leak into the denominator; if such an assembly is added, exclude it explicitly in
  `coverlet.runsettings` (e.g. `[DeltaSharp.TestFixtures]*`) or give it a `.Tests`-matching name.
- **`samples/` example apps** (`[DeltaSharp.Samples.*]*`) are excluded — they are illustrative
  application code, not shippable libraries, so they stay out of the library-coverage denominator.
- **Generated / no-op members** are removed by `ExcludeByAttribute` and `SkipAutoProps`.
- **A future assembly with no executable code** (for example a pure generated-model or
  contracts-only assembly) is excluded by marking it `[assembly: ExcludeFromCodeCoverage]` or by
  adding an `Exclude` entry to `coverlet.runsettings` — document the reason next to the change.

### Ratcheting policy (AC4)

The threshold is **monotonic non-decreasing — it only ever goes up.** The floor is **87.0%**, set
deliberately below the measured **89.16%**: a **~2-point** buffer absorbs cross-platform variance
(the baseline was measured on arm64/macOS; CI runs on x64/Linux) and run-to-run nondeterminism, so
a red gate means a **genuine** regression, not noise — while still actively guarding the range a
wider buffer left unenforced (the old 85.0 floor let coverage regress ~4 points, from 89 down to 85,
without failing). As CI-observed coverage stabilizes and new work lands, **raise
`minimumLineCoverage` toward (never above) the measured value** in the same or a follow-up PR; the
gate prints a ratchet suggestion once headroom exceeds `ratchetSuggestSlack` (**1.5**, below the
current ~2.16-point headroom, so the nudge actually fires at the baseline rather than lying dormant
as the old `5.0` slack did). **Never lower the number to make a red build pass** — add or fix tests
instead.

**Narrow override — a floor decrease tied to code removal.** The only sanctioned way to *lower* the
floor is when a PR **removes well-tested code**, which legitimately lowers the measured percentage;
a coverage dip from deleting code that was covered is not a regression. Such a decrease is allowed
**only** when the removed component and its justification are recorded in the `ratchetOverride`
block of `coverage-config.json` (and the PR), so the change is auditable and a genuine dip is not
mistaken for a red gate. Absent code removal, the floor never decreases. Both the numbers and this
policy are recorded in `coverage-config.json`.

### Reproducing the coverage gate locally

```bash
# 1. build, then run tests with coverage collection
dotnet build DeltaSharp.sln -c Release
dotnet test DeltaSharp.sln -c Release --no-build \
  --collect:"XPlat Code Coverage" --settings coverlet.runsettings \
  --results-directory TestResults

# 2. evaluate the same gate CI runs (reads the threshold from coverage-config.json)
python3 tools/coverage/coverage-gate.py --results-dir TestResults \
  --summary-out TestResults/coverage-summary.md
```

`--threshold <n>` overrides the configured floor for local experiments; CI never passes it, so
the JSON file remains the single enforced source of truth.

> The full test run above measures coverage faithfully (89.16%). The `SparkSession` lifecycle TOCTOU
> tests are deterministic (they drive the race through the internal `ReuseRaceProbe` /
> `StopRaceProbe` seams — see [#454](https://github.com/khaines/deltasharp/issues/454)), so they no
> longer flake under instrumentation and need no coverage-time `--filter`.

---

## Local command summary

```bash
dotnet build -c Release              # Gate 1: warnings-as-errors (same as CI)
dotnet format                        # Gate 2: fix formatting (remediation)
dotnet format --verify-no-changes    # Gate 2: the CI check
dotnet test -c Release --collect:"XPlat Code Coverage" --settings coverlet.runsettings --results-directory TestResults
python3 tools/coverage/coverage-gate.py --results-dir TestResults   # Gate 3: coverage floor
```

## References

- [API governance: public-API baselines and banned APIs](api-governance.md)
- [Test project conventions & test-access policy](testing-conventions.md)
- [Repository layout & project conventions](repository-layout.md)
- [ADR-0014: Target framework and AOT posture](../../adr/0014-target-framework-aot.md)
- [ADR-0001: Execution strategy (optional codegen tier)](../../adr/0001-execution-strategy.md)
- [03a — .NET coding standards checklist](../checklists/03a-dotnet-coding-standards.md)
- [04a — Unit testing checklist](../checklists/04a-unit-testing-checklist.md)
- [08 — Performance checklist](../checklists/08-performance-checklist.md)
- [`.github/workflows/ci.yml`](../../../.github/workflows/ci.yml) ·
  [`Directory.Build.props`](../../../Directory.Build.props) ·
  [`.editorconfig`](../../../.editorconfig) ·
  [`coverlet.runsettings`](../../../coverlet.runsettings) ·
  [`tools/coverage/`](../../../tools/coverage/)
