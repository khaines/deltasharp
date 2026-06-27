# EPIC-01: Project & Build Platform

- **Roadmap milestone:** M1 (link to ../../../ROADMAP.md#milestone-1--engine-foundations-v01)
- **Primary persona(s):** `dotnet-library-platform-engineer`
- **Related ADRs:** ADR-0014, ADR-0001
- **Depends on:** none
- **Status:** draft
- **Size:** L

## Objective

Create the .NET solution skeleton, build governance, target-framework policy, AOT posture, public API enforcement, and packaging foundation for DeltaSharp. This epic turns ADR-0014's `net10.0` engine / `net8.0;net10.0` library policy and ADR-0001's optional dynamic-code tier into enforceable build and package mechanics.

## Scope

**In scope**
- Root solution, `src/`, `tests/`, and `samples/` layout for initial engine, library, analyzer, and test projects.
- Central build governance through `global.json`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, and `.editorconfig`.
- Target-framework and multi-targeting policy for engine/executor projects, public-facing libraries, tests, and samples.
- NativeAOT and trimming publish verification, including feature-switch hygiene that elides ADR-0001 dynamic-code paths from AOT publishes.
- Public API governance using PublicApiAnalyzers, BannedApiAnalyzers, baselines, and preview/deprecation policy.
- NuGet package metadata, SourceLink, symbols, deterministic builds, and package validation for packable projects.

**Out of scope** (and where it lives instead)
- CI workflow orchestration, branch protection, and contributor templates → EPIC-00 / personas `cloud-native-site-reliability-engineer`, `developer-relations-community-lead`.
- Columnar memory structures, Spark type system, and execution kernels → EPIC-02 and EPIC-03 / personas `dotnet-vectorized-columnar-compute-engineer`, `query-execution-engine-engineer`.
- Public Spark API ergonomics, samples, and user-facing API shape → EPIC-04 / persona `developer-experience-api-engineer`.
- Runtime performance tuning of JIT, NativeAOT, SIMD, or GC behavior → EPIC-03 and EPIC-13 / personas `dotnet-runtime-performance-engineer`, `performance-benchmarking-engineer`.
- Release process, signing policy, and security provenance ownership → EPIC-00 and EPIC-13 / persona `cloud-native-security-sme`.

## Exit criteria

- [ ] `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` pass on the skeleton from a clean checkout.
- [ ] Central Package Management, SDK pinning, repository-wide analyzer settings, nullable settings, and warnings-as-errors are enforced by shared build files.
- [ ] Engine/executor projects target `net10.0`, public-facing libraries target `net8.0;net10.0`, and exceptions are documented with owners.
- [ ] A representative `dotnet publish /p:PublishAot=true` succeeds with ADR-0001 codegen-tier assets elided or unreachable under AOT.
- [ ] Public API and banned API baselines exist and fail the build on unapproved public-surface or policy violations.
- [ ] Packable projects produce valid NuGet packages and symbol packages with SourceLink, deterministic build metadata, and package validation enabled.

## Features

### FEAT-01.1: Solution and project layout

- **Objective:** Establish the repository's physical .NET layout so future M1 components land in predictable assemblies, tests, and samples. The layout should separate public libraries, engine internals, tests, and samples without pre-implementing later epics.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-distributed-systems-architect`, `developer-experience-api-engineer`.
- **Depends on:** none.

#### Stories

##### STORY-01.1.1: Root solution and source tree skeleton

- **As a** library platform owner **I want** a root solution and canonical `src/` project layout **so that** all future engine and library work has stable assembly homes.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-distributed-systems-architect`.
- **Size:** M. **Depends on:** none.
- **Acceptance criteria:**
  - [ ] Given a clean checkout, When `dotnet sln list` runs, Then the root solution contains the initial `src/` projects.
  - [ ] Given ADR-0014 is applied, When project files are inspected, Then engine/executor projects are separated from public-facing multi-targeted libraries.
  - [ ] Given a future component needs a project, When maintainers inspect the layout, Then naming and folder conventions are documented and unambiguous.
  - [ ] Given no implementation exists yet for later epics, When the skeleton builds, Then scaffold code does not expose misleading Spark or Delta behavior.
- **Definition of done:** builds/tests/format pass; checklists `01`, `03a`, `11` satisfied; docs updated if public API changes.

##### STORY-01.1.2: Test project conventions

- **As a** test contributor **I want** a `tests/` project per production project convention **so that** unit and integration tests are discoverable and consistently named.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `reliability-test-chaos-engineer`.
- **Size:** S. **Depends on:** STORY-01.1.1.
- **Acceptance criteria:**
  - [ ] Given a production project exists under `src/`, When a matching test project is required, Then the test project name ends with `.Tests` and is placed under `tests/`.
  - [ ] Given test projects reference production projects, When `dotnet test` runs, Then references resolve without relying on package artifacts.
  - [ ] Given test-only helpers are needed, When project references are inspected, Then helper visibility is controlled by documented friend-assembly or internal-test policy.
  - [ ] Given integration tests need external resources, When project naming is reviewed, Then unit and integration test scopes are distinguishable.
- **Definition of done:** builds/tests/format pass; checklists `04`, `04a`, `04b`, `11` satisfied; docs updated if public API changes.

##### STORY-01.1.3: Samples directory scaffold

- **As a** future user **I want** a `samples/` scaffold **so that** examples can be added without affecting production package boundaries.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `dotnet-library-platform-engineer`, `technical-writer`.
- **Size:** S. **Depends on:** STORY-01.1.1.
- **Acceptance criteria:**
  - [ ] Given a sample project is added, When it builds, Then it references local projects or packages using documented sample conventions.
  - [ ] Given samples are not production libraries, When package settings are inspected, Then sample projects are not packable by default.
  - [ ] Given a sample demonstrates a preview API later, When documentation is reviewed, Then preview status and expected compatibility are visible.
  - [ ] Given CI builds samples, When a sample fails, Then the failure is reported without blocking package validation diagnostics from production projects.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `20`, `11` satisfied; docs updated if public API changes.

### FEAT-01.2: Central build governance

- **Objective:** Make repository-wide build behavior explicit and deterministic through central SDK, package, analyzer, and editor configuration. These files become the policy surface that EPIC-00 CI enforces.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-site-reliability-engineer`, `cloud-native-security-sme`.
- **Depends on:** FEAT-01.1.

#### Stories

##### STORY-01.2.1: SDK pinning with global.json

- **As a** contributor **I want** SDK pinning through `global.json` **so that** local and CI builds use an intentional .NET SDK compatible with ADR-0014.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-site-reliability-engineer`.
- **Size:** S. **Depends on:** STORY-01.1.1.
- **Acceptance criteria:**
  - [ ] Given a contributor runs `dotnet --version` from the repository root, When `global.json` is present, Then the selected SDK matches the documented supported SDK band.
  - [ ] Given CI installs the SDK, When builds run, Then the SDK version is compatible with `net10.0` projects.
  - [ ] Given the SDK version changes, When the diff is reviewed, Then the compatibility risk and rollback note are documented.
  - [ ] Given an unsupported SDK is used, When restore or build fails, Then the error points contributors to SDK setup documentation.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `10`, `11` satisfied; docs updated if public API changes.

##### STORY-01.2.2: Directory.Build props and targets policy

- **As a** platform maintainer **I want** shared `Directory.Build.props` and `.targets` **so that** nullable, deterministic builds, analyzer severity, and packability defaults are centralized.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-security-sme`.
- **Size:** M. **Depends on:** STORY-01.2.1.
- **Acceptance criteria:**
  - [ ] Given any production project builds, When MSBuild properties are evaluated, Then nullable, deterministic build, continuous integration build, and warnings-as-errors settings are inherited.
  - [ ] Given a project needs an exception, When its project file is reviewed, Then the exception is local, justified, and linked to an owner or exit criteria.
  - [ ] Given package metadata defaults are applied, When packable projects build, Then repository URL, license, symbol, and SourceLink settings are inherited unless explicitly overridden.
  - [ ] Given analyzer configuration changes, When builds run, Then analyzer severity is controlled centrally rather than duplicated across projects.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `05`, `11` satisfied; docs updated if public API changes.

##### STORY-01.2.3: Central Package Management and editor configuration

- **As a** maintainer **I want** Central Package Management and `.editorconfig` **so that** dependency versions and source formatting stay consistent across projects.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-security-sme`, `developer-relations-community-lead`.
- **Size:** M. **Depends on:** STORY-01.2.2.
- **Acceptance criteria:**
  - [ ] Given a project references a NuGet package, When the project file is inspected, Then package versions are controlled through `Directory.Packages.props`.
  - [ ] Given a dependency version changes, When the diff is reviewed, Then the change appears in the central package file and can be scanned by SCA tooling.
  - [ ] Given formatting or analyzer rules are evaluated, When `dotnet format --verify-no-changes` runs, Then `.editorconfig` supplies the repository rules.
  - [ ] Given a package version is duplicated in a project file, When restore or build runs, Then the duplication is rejected or flagged by documented policy.
- **Definition of done:** builds/tests/format pass; checklists `03`, `03a`, `05`, `11` satisfied; docs updated if public API changes.

### FEAT-01.3: Target-framework and TFM policy

- **Objective:** Implement ADR-0014 target-framework policy so the engine and executor can use `net10.0` and NativeAOT capabilities while public libraries remain consumable from `net8.0;net10.0`. The policy must be checkable in project files and CI.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `dotnet-runtime-performance-engineer`, `developer-experience-api-engineer`.
- **Depends on:** FEAT-01.2.

#### Stories

##### STORY-01.3.1: Engine and executor net10.0 targeting

- **As a** runtime maintainer **I want** engine and executor projects to target `net10.0` **so that** M1 can use the runtime capabilities assumed by ADR-0014.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `dotnet-runtime-performance-engineer`, `dotnet-distributed-execution-engineer`.
- **Size:** S. **Depends on:** STORY-01.2.2.
- **Acceptance criteria:**
  - [ ] Given engine or executor project files are inspected, When target frameworks are reviewed, Then they declare `net10.0` and do not multi-target `net8.0` unless an exception is documented.
  - [ ] Given `dotnet build` runs, When engine/executor projects compile, Then build output identifies `net10.0` target artifacts.
  - [ ] Given a `net8.0`-only API is accidentally required by engine code, When the project builds, Then the incompatibility is caught at compile or analyzer time.
  - [ ] Given downstream library projects reference engine abstractions, When references are reviewed, Then public `net8.0` libraries do not depend on `net10.0`-only assemblies.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `08`, `10`, `11` satisfied; docs updated if public API changes.

##### STORY-01.3.2: Public library net8.0 and net10.0 multi-targeting

- **As a** package consumer **I want** public-facing libraries to target `net8.0;net10.0` **so that** DeltaSharp can be adopted by current LTS applications while remaining ready for the engine runtime.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `developer-experience-api-engineer`, `data-platform-connectors-engineer`.
- **Size:** M. **Depends on:** STORY-01.3.1.
- **Acceptance criteria:**
  - [ ] Given a public-facing library project is inspected, When target frameworks are reviewed, Then it declares `net8.0;net10.0`.
  - [ ] Given TFM-specific APIs are used, When the project builds, Then conditional compilation or target-specific references are explicit and documented.
  - [ ] Given package assets are produced, When the package is inspected, Then both `net8.0` and `net10.0` compile assets are present for multi-targeted libraries.
  - [ ] Given an API is only available on `net10.0`, When a `net8.0` consumer builds, Then the API is absent or guarded by a documented compatibility path.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `20`, `11` satisfied; docs updated if public API changes.

### FEAT-01.4: NativeAOT and trim publish hygiene

- **Objective:** Prove that the skeleton can publish representative AOT-compatible artifacts and that ADR-0001's optional codegen tier is guarded, feature-switched, and elided from NativeAOT publishes. This prevents dynamic-code assumptions from leaking into the default vectorized interpreter path.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `dotnet-runtime-performance-engineer`, `query-execution-engine-engineer`, `cloud-native-security-sme`.
- **Depends on:** FEAT-01.3.

#### Stories

##### STORY-01.4.1: Representative NativeAOT publish profile

- **As an** executor image maintainer **I want** a representative `PublishAot` profile **so that** NativeAOT compatibility failures are visible before runtime code accumulates.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `dotnet-runtime-performance-engineer`, `dotnet-distributed-execution-engineer`.
- **Size:** M. **Depends on:** STORY-01.3.1.
- **Acceptance criteria:**
  - [ ] Given the representative publish project exists, When `dotnet publish /p:PublishAot=true` runs, Then it completes successfully on a supported SDK/runtime environment.
  - [ ] Given trim or AOT warnings occur, When publish output is reviewed, Then warnings fail the publish or are justified with documented suppressions.
  - [ ] Given the publish profile is inspected, When properties are reviewed, Then NativeAOT settings are local to executable artifacts and not forced onto public libraries unexpectedly.
  - [ ] Given CI or local docs describe AOT verification, When a maintainer follows them, Then the exact publish command and expected output path are clear.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `08`, `10`, `11` satisfied; docs updated if public API changes.

##### STORY-01.4.2: Dynamic-code feature switch and codegen elision

- **As a** platform owner **I want** ADR-0001 dynamic-code paths feature-switched and guarded **so that** AOT publishes remove or avoid the optional codegen tier.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `dotnet-runtime-performance-engineer`, `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-01.4.1.
- **Acceptance criteria:**
  - [ ] Given dynamic-code code paths are introduced, When source is reviewed, Then they are annotated or guarded with `[RequiresDynamicCode]`, feature guards, or documented feature switches.
  - [ ] Given `RuntimeFeature.IsDynamicCodeSupported` is false, When backend selection is evaluated, Then the interpreted vectorized backend remains available and compiled backend activation is blocked.
  - [ ] Given `dotnet publish /p:PublishAot=true` runs, When publish output and warnings are inspected, Then codegen-tier dependencies are elided or unreachable without trim/AOT warnings.
  - [ ] Given a developer attempts to call dynamic-code APIs from an AOT-clean assembly, When analyzers run, Then the usage fails or requires an explicit documented exception.
  - [ ] Given ADR-0001 is reviewed, When the implementation is compared, Then codegen remains optional and never required for correctness.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `08`, `21`, `11` satisfied; docs updated if public API changes.

### FEAT-01.5: Public API and banned API enforcement

- **Objective:** Establish compatibility controls before public APIs expand: API baselines, banned APIs, and preview/deprecation policy. These controls keep DeltaSharp package surfaces stable and AOT-safe as Spark-parity work begins.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `developer-experience-api-engineer`, `cloud-native-security-sme`.
- **Depends on:** FEAT-01.2.

#### Stories

##### STORY-01.5.1: PublicApiAnalyzers baselines

- **As a** package maintainer **I want** PublicApiAnalyzers baselines **so that** public surface changes are intentional and reviewable.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `developer-experience-api-engineer`.
- **Size:** M. **Depends on:** STORY-01.2.2.
- **Acceptance criteria:**
  - [ ] Given a public API is added to a packable library, When the build runs, Then the change requires an update to the unshipped public API baseline.
  - [ ] Given an already shipped API baseline exists, When a public member is removed or changed, Then analyzer output identifies the compatibility break.
  - [ ] Given internal-only projects are inspected, When API baselines are reviewed, Then public API enforcement is applied only where intended.
  - [ ] Given a public API change is reviewed, When the PR template is used, Then release-note or compatibility impact is requested.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `20`, `11` satisfied; docs updated if public API changes.

##### STORY-01.5.2: BannedApiAnalyzers policy

- **As an** AOT and security maintainer **I want** banned API rules **so that** dynamic-code, nondeterministic, or unsafe APIs cannot enter protected assemblies accidentally.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-security-sme`, `dotnet-runtime-performance-engineer`.
- **Size:** M. **Depends on:** STORY-01.5.1.
- **Acceptance criteria:**
  - [ ] Given banned symbols are configured, When protected projects use unguarded dynamic-code APIs, Then the build fails with a clear diagnostic.
  - [ ] Given a banned API exception is needed, When it is reviewed, Then the exception is scoped, justified, and linked to ADR-0001 or ADR-0014 rationale.
  - [ ] Given security-sensitive APIs are banned or restricted, When analyzer output appears, Then remediation guidance points to safe alternatives or required owners.
  - [ ] Given banned-symbol files are inspected, When rules are reviewed, Then they include categories for AOT, trimming, determinism, and security-sensitive APIs.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `05`, `08`, `11` satisfied; docs updated if public API changes.

##### STORY-01.5.3: Experimental and obsolete API lifecycle

- **As an** API maintainer **I want** `[Experimental]` and `[Obsolete]` policy **so that** preview APIs and deprecations have clear diagnostics and stabilization paths.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `developer-experience-api-engineer`, `technical-writer`.
- **Size:** S. **Depends on:** STORY-01.5.1.
- **Acceptance criteria:**
  - [ ] Given an API is marked experimental, When source is inspected, Then it includes a diagnostic ID, documentation URL, owner, and expected review point.
  - [ ] Given an API is marked obsolete, When a consumer builds, Then the diagnostic message states replacement guidance or removal timeline.
  - [ ] Given preview APIs appear in samples, When samples are reviewed, Then preview status is clearly documented.
  - [ ] Given analyzer diagnostics are documented, When maintainers add a diagnostic ID, Then it is unique and traceable to policy documentation.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `20`, `11` satisfied; docs updated if public API changes.

### FEAT-01.6: NuGet packaging and SourceLink

- **Objective:** Make packable projects produce trustworthy NuGet packages with metadata, symbols, SourceLink, deterministic outputs, and package validation. This supports ADR-0015 open-source consumption while aligning with ADR-0014's multi-target package posture.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-security-sme`, `developer-relations-community-lead`, `technical-writer`.
- **Depends on:** FEAT-01.2, FEAT-01.3, FEAT-01.5.

#### Stories

##### STORY-01.6.1: Package metadata and symbol packages

- **As a** NuGet consumer **I want** packages with correct metadata and symbol packages **so that** DeltaSharp artifacts are discoverable, legally clear, and debuggable.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `developer-relations-community-lead`, `technical-writer`.
- **Size:** M. **Depends on:** STORY-01.2.2.
- **Acceptance criteria:**
  - [ ] Given a packable project runs `dotnet pack`, When the `.nupkg` is inspected, Then package ID, description, authors, license expression, repository URL, tags, and readme settings are present.
  - [ ] Given symbols are enabled, When packing completes, Then a `.snupkg` is produced for packable projects.
  - [ ] Given samples and test projects are inspected, When pack settings are evaluated, Then they are not packable by default.
  - [ ] Given Apache-2.0 licensing is required by ADR-0015, When package metadata is reviewed, Then license metadata references Apache-2.0 consistently.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `05`, `11`, `20` satisfied; docs updated if public API changes.

##### STORY-01.6.2: SourceLink and deterministic package validation

- **As a** support engineer **I want** SourceLink and package validation **so that** users can debug packages and maintainers can catch packaging regressions before release.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-security-sme`.
- **Size:** M. **Depends on:** STORY-01.6.1.
- **Acceptance criteria:**
  - [ ] Given a package is built in CI, When SourceLink validation runs, Then source paths resolve to repository URLs for the commit being built.
  - [ ] Given package validation is enabled, When a public API or TFM compatibility regression is introduced, Then validation fails with actionable diagnostics.
  - [ ] Given deterministic build settings are active, When package contents are inspected, Then repository commit metadata and deterministic build properties are present.
  - [ ] Given symbols are downloaded by a debugger, When SourceLink is followed, Then source retrieval points to the correct repository revision.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `05`, `11` satisfied; docs updated if public API changes.

##### STORY-01.6.3: Pack command and artifact validation smoke test

- **As a** release maintainer **I want** a pack and artifact-validation smoke test **so that** NuGet packages and symbols can be produced before release automation exists.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-site-reliability-engineer`, `cloud-native-security-sme`.
- **Size:** S. **Depends on:** STORY-01.6.2.
- **Acceptance criteria:**
  - [ ] Given a clean checkout, When the documented `dotnet pack` command runs, Then packable projects produce `.nupkg` and `.snupkg` artifacts without test or sample packages.
  - [ ] Given package validation fails, When the command output is inspected, Then the failing package, TFM, and validation rule are visible.
  - [ ] Given generated packages are scanned by the supply-chain workflow, When SBOM or SCA tooling runs, Then package identity and dependency metadata are available.
  - [ ] Given a maintainer deletes build outputs, When pack is rerun, Then artifacts are regenerated from source rather than reused from stale outputs.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `05`, `11`, `20` satisfied; docs updated if public API changes.

## Open questions

- Which exact .NET 10 SDK band should `global.json` pin while .NET 10 support is still moving toward general availability?
- Should the first package set use a single aggregate package plus internals, or multiple narrowly scoped packages from the start?
- What package signing mechanism should be mandatory for pre-release artifacts before the full release pipeline exists?
