# EPIC-00: Engineering Foundations

- **Roadmap milestone:** M1 (link to ../../../ROADMAP.md#milestone-1--engine-foundations-v01)
- **Primary persona(s):** `cloud-native-site-reliability-engineer`, `dotnet-library-platform-engineer`, `cloud-native-security-sme`
- **Related ADRs:** ADR-0014, ADR-0015
- **Depends on:** none
- **Status:** draft
- **Size:** L

## Objective

Establish the cross-cutting engineering scaffolding required for DeltaSharp's M1 engine foundations and later v1 workstreams. This epic makes build, quality, security, observability, testing, and contributor workflows repeatable for an Apache-2.0 .NET-native Spark reimplementation, while honoring ADR-0014's NativeAOT posture and ADR-0015's open-source governance expectations.

## Scope

**In scope**
- GitHub Actions CI/CD for restore, build, test, format verification, target-framework matrix coverage, dependency caching, and DCO checks.
- Repository quality gates for analyzers, formatting, warnings-as-errors policy, coverage collection, and threshold enforcement.
- Security and supply-chain baseline for secret scanning, dependency/SCA scanning, SBOM generation, deterministic/signed build posture, and branch-protection requirements.
- OpenTelemetry-oriented logging, metrics, tracing, and correlation conventions for future driver, executor, and operator components.
- Test harness conventions, object-store emulators, Kubernetes test harness direction, and deterministic seed policy.
- Contributor automation including issue templates, PR template, CODEOWNERS activation, labels, and DCO bot wiring.

**Out of scope** (and where it lives instead)
- Concrete solution/project skeleton and NuGet packaging mechanics → EPIC-01 / persona `dotnet-library-platform-engineer`.
- Engine runtime, vectorized execution, and Spark API implementation → EPIC-02 through EPIC-04 / personas `dotnet-vectorized-columnar-compute-engineer`, `query-execution-engine-engineer`, `developer-experience-api-engineer`.
- Kubernetes Operator CRDs and production reconciliation logic → EPIC-10 / persona `kubernetes-operator-controller-engineer`.
- GA compatibility hardening and release-candidate readiness campaigns → EPIC-13 / personas `performance-benchmarking-engineer`, `reliability-test-chaos-engineer`, `developer-relations-community-lead`.

## Exit criteria

- [ ] A GitHub Actions sample CI run completes restore, build, test, format verification, target-framework matrix jobs, dependency caching, and DCO validation successfully.
- [ ] Formatting, analyzer warnings-as-errors, and coverage threshold gates are enforced and fail the workflow on a deliberate sample violation.
- [ ] Secret scanning, dependency/SCA scanning, SBOM generation, and branch-protection requirements are wired with documented evidence paths.
- [ ] OpenTelemetry .NET conventions for `ILogger`, `Meter`, `ActivitySource`, and correlation identifiers are documented for downstream epics.
- [ ] Emulator-backed object-store tests and Kubernetes harness smoke tests are runnable from documented commands with deterministic seeds.
- [ ] Issue templates, PR template, CODEOWNERS, label taxonomy, and DCO bot automation are present and aligned with ADR-0015.

## Features

### FEAT-00.1: CI/CD pipeline baseline

- **Objective:** Provide a repeatable GitHub Actions pipeline that proves every change can restore, build, test, and format-check the M1 skeleton across supported target frameworks. The pipeline encodes ADR-0014 target-framework expectations and ADR-0015 open-source contribution hygiene.
- **Implementer persona(s):** Primary `cloud-native-site-reliability-engineer`; Collaborators `dotnet-library-platform-engineer`, `developer-relations-community-lead`.
- **Depends on:** none.

#### Stories

##### STORY-00.1.1: Restore-build-test-format workflow

- **As a** maintainer **I want** a GitHub Actions workflow that runs restore, build, test, and `dotnet format --verify-no-changes` **so that** every pull request receives a consistent baseline signal.
- **Implementer persona(s):** Primary `cloud-native-site-reliability-engineer`; Collaborators `dotnet-library-platform-engineer`.
- **Size:** M. **Depends on:** none.
- **Acceptance criteria:**
  - [ ] Given a pull request targets the default branch, When the CI workflow starts, Then it runs `dotnet restore`, `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` as separate visible steps.
  - [ ] Given a build or test command exits non-zero, When the workflow completes, Then the pull request check is marked failed.
  - [ ] Given only documentation files change, When the workflow runs, Then required repository policy checks still execute or are explicitly skipped by documented path filters.
  - [ ] Given a local contributor reads the workflow, When they inspect the commands, Then they can run equivalent commands from the repository root.
- **Definition of done:** builds/tests/format pass; checklists `03`, `03a`, `04`, `11` satisfied; docs updated if public API changes.

##### STORY-00.1.2: Target-framework matrix and dependency caching

- **As a** maintainer **I want** CI to exercise `net8.0` and `net10.0` where applicable with deterministic dependency caching **so that** ADR-0014 compatibility failures surface before merge.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-site-reliability-engineer`.
- **Size:** S. **Depends on:** STORY-00.1.1.
- **Acceptance criteria:**
  - [ ] Given a multi-targeted library project exists, When CI runs, Then matrix jobs or build properties cover both `net8.0` and `net10.0` targets.
  - [ ] Given only NuGet lock inputs are unchanged, When CI repeats, Then dependency cache restore is attempted using keys derived from project and package-management files.
  - [ ] Given a target-framework-specific compilation error is introduced, When CI runs, Then the failing TFM is visible in the workflow output.
  - [ ] Given an engine/executor project targets `net10.0`, When CI runs, Then no `net8.0` build is required for that project.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `04`, `10`, `11` satisfied; docs updated if public API changes.

##### STORY-00.1.3: DCO and contribution compliance checks

- **As a** community maintainer **I want** DCO validation in pull-request automation **so that** ADR-0015's open-source contribution process is enforceable.
- **Implementer persona(s):** Primary `developer-relations-community-lead`; Collaborators `cloud-native-site-reliability-engineer`, `cloud-native-security-sme`.
- **Size:** S. **Depends on:** STORY-00.1.1.
- **Acceptance criteria:**
  - [ ] Given a pull request contains a commit without a `Signed-off-by` trailer, When checks run, Then the DCO check fails.
  - [ ] Given every commit contains a valid `Signed-off-by` trailer, When checks run, Then the DCO check passes.
  - [ ] Given a contributor opens the PR template, When they read it, Then DCO expectations and remediation instructions are visible.
  - [ ] Given maintainers inspect branch protection settings, When required checks are reviewed, Then the DCO check is listed or documented as required before merge.
- **Definition of done:** builds/tests/format pass; checklists `05`, `11` satisfied; docs updated if public API changes.

### FEAT-00.2: Quality gates and coverage policy

- **Objective:** Encode repository-wide quality gates so formatting, analyzers, warnings-as-errors, and coverage expectations fail fast. These gates protect the future engine and library surface from drift as M1 implementation begins.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-site-reliability-engineer`, `reliability-test-chaos-engineer`.
- **Depends on:** FEAT-00.1.

#### Stories

##### STORY-00.2.1: Analyzer and warnings-as-errors gate

- **As a** library platform owner **I want** analyzers and warnings-as-errors enforced in CI **so that** coding, AOT, trimming, and packaging policy violations cannot accumulate.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-site-reliability-engineer`.
- **Size:** M. **Depends on:** STORY-00.1.1.
- **Acceptance criteria:**
  - [ ] Given a C# analyzer warning is introduced in production code, When CI builds the repository, Then the build fails because warnings are treated as errors.
  - [ ] Given generated code is present, When analyzers run, Then any documented analyzer exclusions are scoped to generated files only.
  - [ ] Given a developer runs the documented local command, When analyzer violations exist, Then the same class of failures appears locally and in CI.
  - [ ] Given ADR-0014 AOT posture applies, When trim or AOT analyzer warnings are emitted, Then they are not silently suppressed without documented justification.
- **Definition of done:** builds/tests/format pass; checklists `03`, `03a`, `08`, `11` satisfied; docs updated if public API changes.

##### STORY-00.2.2: Formatting gate

- **As a** contributor **I want** deterministic formatting enforcement **so that** pull requests fail on style drift before review time.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `developer-relations-community-lead`.
- **Size:** S. **Depends on:** STORY-00.1.1.
- **Acceptance criteria:**
  - [ ] Given a C# file contains a formatting violation, When `dotnet format --verify-no-changes` runs in CI, Then the workflow fails.
  - [ ] Given repository formatting configuration changes, When the formatting gate runs, Then it uses the checked-in `.editorconfig` and build configuration.
  - [ ] Given a contributor reads contribution documentation, When they look for formatting remediation, Then the local command to fix formatting is documented.
- **Definition of done:** builds/tests/format pass; checklists `03`, `03a`, `11` satisfied; docs updated if public API changes.

##### STORY-00.2.3: Coverage collection and thresholds

- **As a** test owner **I want** coverage collection and threshold enforcement **so that** new code is not accepted without measurable tests.
- **Implementer persona(s):** Primary `reliability-test-chaos-engineer`; Collaborators `dotnet-library-platform-engineer`, `cloud-native-site-reliability-engineer`.
- **Size:** M. **Depends on:** STORY-00.1.1.
- **Acceptance criteria:**
  - [ ] Given tests run in CI, When coverage collection is enabled, Then coverage artifacts are produced in a documented location or uploaded as workflow artifacts.
  - [ ] Given coverage drops below the configured threshold, When CI completes, Then the workflow fails with the measured threshold visible.
  - [ ] Given a project has no executable code, When coverage thresholds are evaluated, Then documented exclusions prevent false failures.
  - [ ] Given a maintainer reviews the threshold configuration, When they inspect it, Then the initial threshold and ratcheting policy are recorded in build/test configuration.
- **Definition of done:** builds/tests/format pass; checklists `04`, `04a`, `11` satisfied; docs updated if public API changes.

### FEAT-00.3: Security and supply-chain baseline

- **Objective:** Establish the first layer of security controls for an open-source data platform: secret detection, dependency risk visibility, SBOM output, artifact integrity posture, and protected merge requirements. This grounds ADR-0015 community trust and prepares later Kubernetes and storage work for secure defaults.
- **Implementer persona(s):** Primary `cloud-native-security-sme`; Collaborators `cloud-native-site-reliability-engineer`, `dotnet-library-platform-engineer`.
- **Depends on:** FEAT-00.1.

#### Stories

##### STORY-00.3.1: Secret scanning and dependency/SCA scanning

- **As a** security maintainer **I want** secret scanning and dependency/SCA scanning wired into repository workflows **so that** leaked credentials and vulnerable dependencies are detected early.
- **Implementer persona(s):** Primary `cloud-native-security-sme`; Collaborators `cloud-native-site-reliability-engineer`.
- **Size:** M. **Depends on:** STORY-00.1.1.
- **Acceptance criteria:**
  - [ ] Given a test secret pattern is added in a safe fixture, When secret scanning runs, Then the scanner reports the fixture without exposing real credentials.
  - [ ] Given NuGet dependencies are restored, When dependency/SCA scanning runs, Then package vulnerabilities are reported with severity and package identity.
  - [ ] Given a vulnerability exceeds the documented failure threshold, When CI completes, Then the security workflow fails.
  - [ ] Given a false positive is suppressed, When the suppression is reviewed, Then it includes scope, reason, owner, and expiry or review criteria.
- **Definition of done:** builds/tests/format pass; checklists `05`, `07`, `11` satisfied; docs updated if public API changes.

##### STORY-00.3.2: SBOM and deterministic build evidence

- **As a** release steward **I want** SBOM generation and deterministic build evidence **so that** downstream users can audit what DeltaSharp artifacts contain.
- **Implementer persona(s):** Primary `cloud-native-security-sme`; Collaborators `dotnet-library-platform-engineer`.
- **Size:** M. **Depends on:** STORY-00.1.1.
- **Acceptance criteria:**
  - [ ] Given CI builds packable projects, When the supply-chain workflow runs, Then an SBOM artifact is generated for built packages or assemblies.
  - [ ] Given deterministic build settings are enabled, When the same inputs are built twice in CI-compatible environments, Then documented evidence shows reproducible assembly metadata inputs.
  - [ ] Given package metadata is inspected, When SourceLink and repository metadata are expected, Then the generated artifacts include source provenance fields.
  - [ ] Given a maintainer downloads workflow artifacts, When they inspect them, Then SBOM files are retained with a documented naming convention.
- **Definition of done:** builds/tests/format pass; checklists `03a`, `05`, `11` satisfied; docs updated if public API changes.

##### STORY-00.3.3: Signing and branch-protection policy

- **As a** security owner **I want** signed-artifact posture and branch-protection requirements documented and enforced **so that** releases and merges have a trustworthy baseline.
- **Implementer persona(s):** Primary `cloud-native-security-sme`; Collaborators `cloud-native-site-reliability-engineer`, `developer-relations-community-lead`.
- **Size:** S. **Depends on:** STORY-00.3.1.
- **Acceptance criteria:**
  - [ ] Given maintainers inspect branch protection, When required checks are listed, Then CI, security scans, and DCO are required or documented as required before enabling.
  - [ ] Given artifact signing is not yet fully automated, When the policy is reviewed, Then the interim posture, owner, and activation criteria are documented.
  - [ ] Given a release workflow is introduced later, When it references this baseline, Then signing, provenance, and SBOM expectations are traceable.
  - [ ] Given an administrator attempts to bypass required checks, When repository rules are reviewed, Then bypass permissions are limited to documented maintainers or teams.
- **Definition of done:** builds/tests/format pass; checklists `05`, `11`, `13` satisfied; docs updated if public API changes.

### FEAT-00.4: Observability scaffolding conventions

- **Objective:** Define lightweight OpenTelemetry .NET conventions before engine, driver, executor, and operator code exists. The goal is consistent logging, metrics, traces, and correlation identifiers that make future reliability work observable by design.
- **Implementer persona(s):** Primary `cloud-native-site-reliability-engineer`; Collaborators `dotnet-framework-runtime-engineer`, `technical-writer`.
- **Depends on:** none.

#### Stories

##### STORY-00.4.1: Logging and correlation identifier conventions

- **As an** operator **I want** logging and correlation ID conventions **so that** future job, stage, task, executor, and Delta-table events can be connected during incidents.
- **Implementer persona(s):** Primary `cloud-native-site-reliability-engineer`; Collaborators `dotnet-framework-runtime-engineer`, `technical-writer`.
- **Size:** S. **Depends on:** none.
- **Acceptance criteria:**
  - [ ] Given a component emits logs, When conventions are applied, Then log fields include stable names for job, stage, task, executor, table version, and correlation identifiers where applicable.
  - [ ] Given a request or action boundary is created, When downstream work is scheduled, Then correlation identifiers are propagated or an explicit non-propagation rule is documented.
  - [ ] Given sensitive storage paths or credentials could appear, When logging conventions are reviewed, Then redaction rules prohibit secrets and sensitive tokens in logs.
  - [ ] Given maintainers add a new component, When they read the conventions, Then required logger categories and event naming guidance are available.
- **Definition of done:** builds/tests/format pass; checklists `05`, `09a`, `11` satisfied; docs updated if public API changes.

##### STORY-00.4.2: Metrics and ActivitySource conventions

- **As an** SRE **I want** `Meter` and `ActivitySource` naming conventions **so that** future telemetry is queryable, low-cardinality, and consistent across components.
- **Implementer persona(s):** Primary `cloud-native-site-reliability-engineer`; Collaborators `dotnet-framework-runtime-engineer`, `performance-benchmarking-engineer`.
- **Size:** M. **Depends on:** STORY-00.4.1.
- **Acceptance criteria:**
  - [ ] Given a new component defines a `Meter`, When conventions are applied, Then the meter name, versioning policy, and instrument naming pattern are documented.
  - [ ] Given a new trace span is created, When conventions are applied, Then `ActivitySource` names and span attributes follow documented cardinality limits.
  - [ ] Given future job success, latency, throughput, and storage I/O metrics are required, When the conventions are reviewed, Then concrete metric families and ownership guidance are defined.
  - [ ] Given telemetry export is disabled, When components run locally, Then instrumentation calls remain safe no-ops without requiring a collector.
- **Definition of done:** builds/tests/format pass; checklists `09b`, `09c`, `10`, `11` satisfied; docs updated if public API changes.

### FEAT-00.5: Test infrastructure and emulators

- **Objective:** Provide shared testing infrastructure for deterministic unit tests, emulator-backed integration tests, and Kubernetes control-plane smoke tests. This enables later storage, operator, and distributed runtime epics to validate behavior before real cloud infrastructure is required.
- **Implementer persona(s):** Primary `reliability-test-chaos-engineer`; Collaborators `cloud-native-site-reliability-engineer`, `dotnet-library-platform-engineer`, `kubernetes-operator-controller-engineer`.
- **Depends on:** FEAT-00.1.

#### Stories

##### STORY-00.5.1: xUnit harness and deterministic seed policy

- **As a** test engineer **I want** an xUnit harness with deterministic seed conventions **so that** failures can be reproduced locally and in CI.
- **Implementer persona(s):** Primary `reliability-test-chaos-engineer`; Collaborators `dotnet-library-platform-engineer`.
- **Size:** S. **Depends on:** STORY-00.1.1.
- **Acceptance criteria:**
  - [ ] Given randomized tests are added, When they run, Then the seed is logged on failure and can be overridden from configuration.
  - [ ] Given tests run in parallel, When shared resources are required, Then collection or fixture boundaries prevent nondeterministic interference.
  - [ ] Given a test fails in CI, When the failure output is inspected, Then the command and seed required to reproduce are visible.
  - [ ] Given a test project is created, When repository conventions are followed, Then its name ends in `.Tests` and it uses the shared test settings.
- **Definition of done:** builds/tests/format pass; checklists `04`, `04a`, `11` satisfied; docs updated if public API changes.

##### STORY-00.5.2: Object-store emulator integration harness

- **As a** storage-facing implementer **I want** MinIO and/or Azurite-backed integration-test harnesses **so that** object-store behavior can be exercised without cloud credentials.
- **Implementer persona(s):** Primary `reliability-test-chaos-engineer`; Collaborators `delta-storage-format-engineer`, `cloud-native-site-reliability-engineer`, `cloud-native-security-sme`.
- **Size:** M. **Depends on:** STORY-00.5.1.
- **Acceptance criteria:**
  - [ ] Given emulator integration tests are enabled, When the documented command runs, Then the required emulator starts or is detected without requiring real cloud credentials.
  - [ ] Given emulator credentials are configured, When logs and test output are inspected, Then credentials are known test values and are not treated as production secrets.
  - [ ] Given emulator tests are unavailable on a contributor machine, When tests are skipped, Then skip reasons are explicit and CI coverage remains enforced.
  - [ ] Given future Delta storage tests need object-store endpoints, When they use the harness, Then endpoint, bucket/container, and cleanup conventions are documented.
- **Definition of done:** builds/tests/format pass; checklists `04b`, `05`, `17`, `11` satisfied; docs updated if public API changes.

##### STORY-00.5.3: Kubernetes kind/envtest smoke harness

- **As an** operator implementer **I want** a kind or envtest smoke harness **so that** future Kubernetes controller behavior can be tested before a full cluster dependency is introduced.
- **Implementer persona(s):** Primary `kubernetes-operator-controller-engineer`; Collaborators `reliability-test-chaos-engineer`, `cloud-native-site-reliability-engineer`, `cloud-native-security-sme`.
- **Size:** M. **Depends on:** STORY-00.5.1.
- **Acceptance criteria:**
  - [ ] Given Kubernetes smoke tests are enabled, When the documented command runs, Then a local kind/envtest-compatible control plane is used or a clear prerequisite failure is emitted.
  - [ ] Given future CRD tests are added, When they use the harness, Then namespace, cleanup, and timeout conventions are documented.
  - [ ] Given cluster credentials are absent, When the harness runs, Then it does not require access to a developer's production kubeconfig.
  - [ ] Given CI executes smoke tests, When failures occur, Then Kubernetes events or controller logs are retained as artifacts where supported.
- **Definition of done:** builds/tests/format pass; checklists `04b`, `05`, `10`, `18`, `11` satisfied; docs updated if public API changes.

### FEAT-00.6: Contributor automation and community workflow

- **Objective:** Activate the contribution workflow required by ADR-0015 so open-source participants get clear issue, PR, ownership, and labeling guidance. Automation should route work to the correct persona labels and reduce maintainer triage toil.
- **Implementer persona(s):** Primary `developer-relations-community-lead`; Collaborators `program-manager`, `cloud-native-site-reliability-engineer`, `cloud-native-security-sme`.
- **Depends on:** FEAT-00.1.

#### Stories

##### STORY-00.6.1: Issue and pull-request templates

- **As a** contributor **I want** issue and pull-request templates **so that** maintainers receive enough information to triage bugs, features, security concerns, and documentation changes.
- **Implementer persona(s):** Primary `developer-relations-community-lead`; Collaborators `program-manager`, `cloud-native-security-sme`.
- **Size:** S. **Depends on:** none.
- **Acceptance criteria:**
  - [ ] Given a contributor opens a bug report, When they choose a template, Then expected behavior, actual behavior, reproduction steps, environment, and logs are requested.
  - [ ] Given a contributor opens a feature request, When they choose a template, Then roadmap milestone, affected personas, acceptance criteria, and alternatives are requested.
  - [ ] Given a contributor opens a pull request, When they use the template, Then test evidence, checklist references, DCO reminder, and documentation impact are requested.
  - [ ] Given a potential vulnerability is discovered, When a template is reviewed, Then it directs private security reporting rather than public exploit details.
- **Definition of done:** builds/tests/format pass; checklists `05`, `11` satisfied; docs updated if public API changes.

##### STORY-00.6.2: CODEOWNERS and label taxonomy activation

- **As a** maintainer **I want** CODEOWNERS and labels aligned to planning personas **so that** work is routed to the right specialists from the first triage pass.
- **Implementer persona(s):** Primary `developer-relations-community-lead`; Collaborators `program-manager`, `cloud-native-site-reliability-engineer`.
- **Size:** S. **Depends on:** STORY-00.6.1.
- **Acceptance criteria:**
  - [ ] Given a file path matches a known subsystem, When CODEOWNERS is evaluated, Then at least one owning team or maintainer group is assigned.
  - [ ] Given a story is converted to a GitHub issue, When labels are applied, Then `persona:<slug>`, `size:<S>`, and epic/feature labels can be represented.
  - [ ] Given the valid persona roster changes, When labels are reviewed, Then additions and removals are tracked against `docs/planning/README.md`.
  - [ ] Given branch protection requires owner review, When a protected file changes, Then CODEOWNERS review is required or the exception is documented.
- **Definition of done:** builds/tests/format pass; checklists `05`, `11` satisfied; docs updated if public API changes.

## Open questions

- What initial coverage threshold should the repository enforce before substantial production code exists, and what ratcheting cadence should apply after M1?
- Which SBOM and artifact signing tools should be standardized for releases versus pull-request validation?
- Should emulator integration tests run on every pull request or on a scheduled/label-triggered workflow until the suite is stable?
