# EPIC-13: Spark Compatibility, Hardening & GA Release

- **Roadmap milestone:** v1.0 ([v1.0 — Spark-parity batch + streaming on Kubernetes](../../../ROADMAP.md#v10--spark-parity-batch--streaming-on-kubernetes))
- **Primary persona(s):** `performance-benchmarking-engineer`, `reliability-test-chaos-engineer`, `technical-writer`, `developer-relations-community-lead` (+ collaborators `developer-experience-api-engineer`, `cloud-native-security-sme`, `dotnet-library-platform-engineer`)
- **Related ADRs:** ADR-0001, ADR-0002, ADR-0003, ADR-0004, ADR-0005, ADR-0006, ADR-0007, ADR-0008, ADR-0009, ADR-0010, ADR-0011, ADR-0012, ADR-0013, ADR-0014, ADR-0015
- **Depends on:** EPIC-01, EPIC-02, EPIC-03, EPIC-04, EPIC-05, EPIC-06, EPIC-07, EPIC-08, EPIC-09, EPIC-10, EPIC-11, EPIC-12
- **Status:** draft
- **Size:** XL

## Objective

Deliver the cross-cutting v1.0 hardening push that proves DeltaSharp is ready for a GA release as a fully native, Apache-2.0 .NET reimplementation of Apache Spark. This epic converts the prior feature epics into release evidence: Spark API and SQL compatibility, codegen parity, Delta interoperability, performance gates, reliability under fault, security readiness, published documentation, and community launch artifacts.

## Scope

**In scope**
- Published Spark API and SQL compatibility matrix for the v1.0 support surface, backed by automated parity tests against reference Spark behavior.
- Optional JIT codegen fast-path validation at parity with the interpreter on dynamic-code runtimes, with AOT executor paths remaining interpreter-only and analyzer-clean.
- Delta table interoperability validation with at least one other Delta engine, including protocol-version and table-feature compatibility evidence.
- TPC-DS/TPC-H-style benchmark suite, continuous regression gates, allocation budgets, throughput and latency scorecards, and release performance summaries.
- Reliability and chaos hardening for consistency, data correctness, Delta commit safety, shuffle loss, executor/pod/node failures, deterministic simulation, and correctness oracles.
- Reference documentation, migration guides, runbooks, release notes, and community-facing launch materials grounded in verified compatibility and readiness evidence.
- Security hardening, SBOM and supply-chain evidence, coordinated disclosure readiness, and release artifact validation for NuGet and NativeAOT container images.

**Out of scope** (and where it lives instead)
- New Spark APIs, SQL syntax, optimizer features, storage capabilities, streaming modes, or Kubernetes CRDs beyond the agreed v1.0 surface → originating epics EPIC-04 through EPIC-12 / owning engineering personas.
- Continuous or low-latency streaming beyond micro-batch scope → future epic / persona `structured-streaming-engine-engineer`.
- Substrait or non-Spark plan interoperability → future epic / persona `cloud-native-distributed-systems-architect`.
- Production incident command, on-call operation, and live customer rollout ownership → operations readiness work / persona `cloud-native-site-reliability-engineer`.
- Legal approval of trademark, foundation, or licensing changes beyond the Apache-2.0 posture in ADR-0015 → governance decision outside this epic.

## Exit criteria

- [ ] Published compatibility matrix lists every v1.0 API, SQL feature, Delta feature, runtime mode, and support level, and the automated parity suite is green for all features marked supported.
- [ ] Optional codegen tier produces interpreter-identical results for the GA parity corpus, is enabled only when dynamic code is supported, and the NativeAOT executor publish path remains free of dynamic-code requirements.
- [ ] Delta interoperability is verified by round-tripping v1.0 tables with at least one other Delta engine, including protocol versions, table features, checkpoints, schema evolution, and time travel.
- [ ] Benchmark suite runs in CI with enforced pre-merge, nightly, and release regression gates for throughput, latency, allocation, GC, cold-start, and scale-out budgets.
- [ ] Chaos and deterministic reliability suites pass release gates with no data loss, duplicate committed rows, illegal Delta histories, or wrong query results under approved v1.0 fault scenarios.
- [ ] Reference API docs, conceptual docs, PySpark/Scala-to-DeltaSharp migration guides, Kubernetes/Delta runbooks, and benchmark/reliability evidence pages are published and link-validated.
- [ ] Security review, threat-model closure, SBOM generation, dependency/provenance validation, and coordinated-disclosure rehearsal are complete with no open critical vulnerabilities.
- [ ] v1.0 NuGet packages and NativeAOT container images are published, smoke-tested, signed/provenanced as required, and accompanied by release notes, known limitations, and a community announcement.

## Features

### FEAT-13.1: Spark API and SQL compatibility matrix with parity suite

- **Objective:** Publish the authoritative v1.0 compatibility matrix and keep it mechanically tied to a Spark-reference parity suite. Users and maintainers must be able to distinguish supported, partial, intentionally different, and unsupported behavior with test evidence.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`, `sql-language-frontend-engineer`, `technical-writer`, `reliability-test-chaos-engineer`.
- **Depends on:** EPIC-04, EPIC-07, EPIC-11, EPIC-12.

#### Stories

##### STORY-13.1.1: Publish v1.0 compatibility matrix schema and content

- **As a** Spark migrant **I want** a compatibility matrix for API, SQL, streaming, Delta, and runtime behavior **so that** I can decide whether a workload is within DeltaSharp v1.0 scope.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `sql-language-frontend-engineer`, `technical-writer`, `product-manager`.
- **Size:** M. **Depends on:** EPIC-04, EPIC-07, EPIC-12.
- **Acceptance criteria:**
  - [ ] Given the published matrix When a reviewer filters by API, SQL, Delta, streaming, and runtime areas Then each entry shows support level, version scope, owner, test identifier, and documented limitation.
  - [ ] Given a feature marked supported When its linked test identifier is inspected Then an automated parity or contract test exists and passes in the release branch.
  - [ ] Given a feature marked partial or unsupported When the docs render Then the limitation includes an issue/RFC link or explicit rationale and no unsupported behavior is advertised as GA.
  - [ ] Given generated docs When link checking runs Then all matrix links to APIs, SQL references, Delta docs, and known limitations resolve.
- **Definition of done:** builds/tests/format pass; checklists `15`, `20`, `11`, `markdown-style-guide`, `03a` satisfied; matrix ownership metadata documented.

##### STORY-13.1.2: Automate Spark-reference parity tests for the supported surface

- **Implement** a parity test suite that compares DeltaSharp outputs, schemas, errors, null semantics, ordering contracts, and SQL behavior against a pinned reference Spark version for every v1.0 supported matrix entry.
- **Implementer persona(s):** Primary `developer-experience-api-engineer`; Collaborators `query-execution-engine-engineer`, `sql-language-frontend-engineer`, `reliability-test-chaos-engineer`.
- **Size:** L. **Depends on:** STORY-13.1.1, EPIC-04, EPIC-07.
- **Acceptance criteria:**
  - [ ] Given a supported DataFrame or Dataset operation When parity tests run Then DeltaSharp and reference Spark produce equivalent schema, row values, null behavior, and documented ordering behavior.
  - [ ] Given supported SQL syntax and functions When parity tests run Then analyzed plans, results, and expected errors match the reference Spark contract or documented DeltaSharp deviation.
  - [ ] Given nondeterministic ordering, floating-point values, time zones, or errors When comparisons execute Then the oracle applies documented tolerances and fails on unapproved differences.
  - [ ] Given CI executes the parity suite When a supported entry fails Then the build reports the matrix entry, owning persona, minimal reproduction, and reference Spark output.
  - [ ] Given a matrix entry changes support level When tests and docs are checked Then the matrix and parity inventory stay synchronized.
- **Definition of done:** builds/tests/format pass; checklists `15`, `21`, `04b`, `11`, `03a` satisfied; parity fixtures and reproduction commands documented.

### FEAT-13.2: Codegen fast-path GA validation

- **Objective:** Validate the optional ADR-0001 JIT codegen tier as a GA fast path without making it a correctness dependency. The interpreter remains the ground truth and NativeAOT executors remain cleanly interpreter-only.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`, `performance-benchmarking-engineer`, `reliability-test-chaos-engineer`, `dotnet-library-platform-engineer`.
- **Depends on:** EPIC-03, EPIC-01.

#### Stories

##### STORY-13.2.1: Gate codegen by runtime capability and AOT publish validation

- **As a** platform maintainer **I want** codegen enabled only on dynamic-code runtimes **so that** NativeAOT executors remain supported and deterministic.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-library-platform-engineer`, `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** EPIC-03, EPIC-01.
- **Acceptance criteria:**
  - [ ] Given `RuntimeFeature.IsDynamicCodeSupported` is false When DeltaSharp starts Then interpreter execution is selected and no compiled delegates are created.
  - [ ] Given dynamic code is supported and configuration enables codegen When DeltaSharp starts Then compiled expression fusion is enabled and reports capability metrics.
  - [ ] Given force-interpreter configuration is set When dynamic code is supported Then the interpreter path runs and codegen metrics show no delegate creation.
  - [ ] Given NativeAOT publish validation runs When artifacts are inspected Then dynamic-code APIs are feature-gated or eliminated and the executor image starts successfully.
- **Definition of done:** builds/tests/format pass; checklists `10`, `08`, `03a`, `04b` satisfied; runtime-mode docs updated.

##### STORY-13.2.2: Prove compiled-backend parity and performance budget compliance

- **Implement** interpreter-vs-compiled differential testing and release benchmark gates for supported codegen expressions and operator fast paths.
- **Implementer persona(s):** Primary `dotnet-runtime-performance-engineer`; Collaborators `dotnet-vectorized-columnar-compute-engineer`, `performance-benchmarking-engineer`, `reliability-test-chaos-engineer`.
- **Size:** L. **Depends on:** STORY-13.2.1, FEAT-13.4.
- **Acceptance criteria:**
  - [ ] Given the GA parity corpus When run with interpreter and compiled backends Then results, errors, nulls, schema, and row counts are identical for all supported codegen shapes.
  - [ ] Given unsupported or unsafe expression shapes When codegen is requested Then execution falls back to the interpreter with an observable metric and no semantic change.
  - [ ] Given codegen benchmarks run on supported JIT runtimes When compared with baseline Then throughput, latency, allocation, compile cost, and cache hit budgets meet release thresholds or block GA.
  - [ ] Given a compiled-tier parity failure When the suite reports it Then the minimal expression shape, seed, input batch, interpreter output, and compiled output are preserved.
- **Definition of done:** builds/tests/format pass; checklists `15`, `22`, `08`, `21`, `03a` satisfied; benchmark and parity evidence linked from release readiness.

### FEAT-13.3: Delta interoperability validation

- **Objective:** Prove v1.0 DeltaSharp tables interoperate with other Delta engines within the ADR-0011 protocol scope. Compatibility claims must cover table features, protocol negotiation, checkpoints, schema evolution, time travel, and failure-safe handoff.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `reliability-test-chaos-engineer`, `technical-writer`, `data-platform-connectors-engineer`.
- **Depends on:** EPIC-05, EPIC-06, EPIC-12.

#### Stories

##### STORY-13.3.1: Round-trip Delta tables with another Delta engine

- **As a** lakehouse engineer **I want** DeltaSharp tables to round-trip with another Delta engine **so that** v1.0 data remains portable across the Delta ecosystem.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `data-platform-connectors-engineer`, `reliability-test-chaos-engineer`.
- **Size:** L. **Depends on:** EPIC-05, EPIC-06.
- **Acceptance criteria:**
  - [ ] Given a DeltaSharp-created table with v1.0 baseline features When another Delta engine reads it Then schema, data files, partition metadata, checkpoints, and table history are accepted and results match expected data.
  - [ ] Given another Delta engine writes supported changes When DeltaSharp reads the table Then snapshot, time travel, schema evolution, and data skipping metadata are interpreted correctly.
  - [ ] Given deletion vectors, column mapping, CDF, liquid clustering, row tracking, and V2 checkpoints are enabled within supported scope When round-trip tests run Then protocol features are negotiated or rejected according to ADR-0011.
  - [ ] Given unsupported table features are present When DeltaSharp opens the table Then it fails closed with a deterministic compatibility error and does not mutate the table.
- **Definition of done:** builds/tests/format pass; checklists `17`, `15`, `21`, `04b`, `03a` satisfied; interop matrix and reproduction scripts documented.

##### STORY-13.3.2: Validate Delta protocol compatibility and release evidence

- **Implement** a release evidence bundle for Delta protocol versions, table features, commit logs, checkpoints, and interop limitations.
- **Implementer persona(s):** Primary `delta-storage-format-engineer`; Collaborators `technical-writer`, `reliability-test-chaos-engineer`.
- **Size:** M. **Depends on:** STORY-13.3.1.
- **Acceptance criteria:**
  - [ ] Given release candidates When protocol validation runs Then min reader/writer versions and enabled table features match the documented v1.0 compatibility matrix.
  - [ ] Given commit logs and checkpoint artifacts from interop tests When validators inspect them Then required actions, stats, metadata, deletion vectors, and checkpoint rows conform to the Delta protocol scope.
  - [ ] Given time-travel and schema-evolution scenarios When replayed across engines Then version visibility, schema projection, and row counts match expected histories.
  - [ ] Given documentation is published When a user reads Delta compatibility guidance Then supported features, unsafe combinations, and downgrade/upgrade behavior are explicit.
- **Definition of done:** builds/tests/format pass; checklists `17`, `11`, `markdown-style-guide`, `21`, `03a` satisfied; release evidence archived with the GA checklist.

### FEAT-13.4: Performance benchmark suite and regression gates

- **Objective:** Establish continuous benchmark evidence for v1.0 performance claims and regression prevention. The suite must cover TPC-DS/TPC-H-style workloads, Delta-specific workloads, cold-start, steady-state, scale-out, allocation, GC, and storage-backend behavior.
- **Implementer persona(s):** Primary `performance-benchmarking-engineer`; Collaborators `query-execution-engine-engineer`, `delta-storage-format-engineer`, `dotnet-runtime-performance-engineer`, `cloud-native-site-reliability-engineer`, `technical-writer`.
- **Depends on:** EPIC-03, EPIC-05, EPIC-08, EPIC-09, EPIC-10, EPIC-11, EPIC-12.

#### Stories

##### STORY-13.4.1: Build reproducible TPC-style and Delta workload harness

- **As a** release owner **I want** reproducible benchmark workloads **so that** v1.0 performance claims are grounded in stable datasets, environments, and methodology.
- **Implementer persona(s):** Primary `performance-benchmarking-engineer`; Collaborators `query-execution-engine-engineer`, `delta-storage-format-engineer`, `dotnet-runtime-performance-engineer`.
- **Size:** L. **Depends on:** EPIC-03, EPIC-05, EPIC-11.
- **Acceptance criteria:**
  - [ ] Given the benchmark cookbook When a maintainer provisions the documented environment Then datasets, scale factors, table layouts, seeds, storage backend, executor count, and runtime version are reproducible.
  - [ ] Given TPC-DS/TPC-H-style query mixes When the harness runs Then scan-heavy, join-heavy, shuffle-heavy, aggregate-heavy, write-heavy, and metadata-heavy scenarios are reported separately.
  - [ ] Given Delta-specific workloads When benchmarks run Then commit latency, checkpoint replay, compaction, time travel, schema evolution, small-file behavior, and object-store/PVC variants are measured.
  - [ ] Given benchmark results are stored When reports are generated Then p50/p95/p99, end-to-end time, throughput, spill, shuffle bytes, allocation rate, GC metrics, and environment fingerprints are included.
- **Definition of done:** builds/tests/format pass; checklists `22`, `08`, `17`, `10`, `03a` satisfied; benchmark cookbook and caveats documented.

##### STORY-13.4.2: Enforce pre-merge, nightly, and release regression gates

- **Implement** statistical regression gates that block release-impacting performance regressions while avoiding noisy single-run decisions.
- **Implementer persona(s):** Primary `performance-benchmarking-engineer`; Collaborators `dotnet-runtime-performance-engineer`, `cloud-native-site-reliability-engineer`.
- **Size:** M. **Depends on:** STORY-13.4.1.
- **Acceptance criteria:**
  - [ ] Given pre-merge benchmarks run When metrics exceed configured quick-gate thresholds Then CI fails with the scenario, metric, baseline, confidence, and owner label.
  - [ ] Given nightly benchmarks run When distribution changes exceed the noise budget Then drift alerts identify workload, environment fingerprint, and probable subsystem.
  - [ ] Given release candidate benchmarks run When throughput, latency, allocation, GC, cold-start, or scale-out budgets regress beyond thresholds Then GA promotion is blocked until waived or fixed.
  - [ ] Given benchmark claims are included in release notes When reviewed Then each claim links to environment, dataset, methodology, caveats, and result artifacts.
- **Definition of done:** builds/tests/format pass; checklists `22`, `08`, `09b`, `10`, `03a` satisfied; gate thresholds and escalation path documented.

### FEAT-13.5: Reliability and chaos hardening

- **Objective:** Prove DeltaSharp preserves correctness under the highest-risk v1.0 partial-failure scenarios. Chaos tests must have mechanical oracles and deterministic reproduction artifacts rather than merely demonstrating process survival.
- **Implementer persona(s):** Primary `reliability-test-chaos-engineer`; Collaborators `delta-storage-format-engineer`, `query-execution-engine-engineer`, `dotnet-distributed-execution-engineer`, `kubernetes-operator-controller-engineer`, `cloud-native-site-reliability-engineer`, `technical-writer`.
- **Depends on:** EPIC-05, EPIC-08, EPIC-09, EPIC-10, EPIC-11, EPIC-12.

#### Stories

##### STORY-13.5.1: Establish deterministic correctness oracles and simulation gates

- **Implement** deterministic simulation and oracle suites for Delta commits, scheduler/task retry, shuffle exchange, executor loss, streaming micro-batch recovery, and query-result correctness.
- **Implementer persona(s):** Primary `reliability-test-chaos-engineer`; Collaborators `delta-storage-format-engineer`, `query-execution-engine-engineer`, `structured-streaming-engine-engineer`.
- **Size:** L. **Depends on:** EPIC-05, EPIC-08, EPIC-11, EPIC-12.
- **Acceptance criteria:**
  - [ ] Given generated plans, schemas, Delta actions, and fault schedules When deterministic simulation runs Then each scenario records seed, input, history, oracle result, and reproduction command.
  - [ ] Given driver, executor, storage, scheduler, shuffle, and clock faults When simulations complete Then no acknowledged data is lost, duplicated, or made visible in an illegal snapshot.
  - [ ] Given query-result equivalence checks When retries or cancellations occur Then DeltaSharp results match the Spark/SQL oracle or fail with a documented retryable/terminal error.
  - [ ] Given a confirmed reliability bug When fixed Then its minimized seed and scenario become a permanent regression test.
- **Definition of done:** builds/tests/format pass; checklists `21`, `17`, `15`, `04b`, `03a` satisfied; invariant catalogue and seed corpus documented.

##### STORY-13.5.2: Run Kubernetes chaos suite for GA fault scenarios

- **As a** release owner **I want** Kubernetes chaos gates for executor, pod, node, network, storage, and shuffle failures **so that** v1.0 correctness survives realistic distributed failures.
- **Implementer persona(s):** Primary `reliability-test-chaos-engineer`; Collaborators `kubernetes-operator-controller-engineer`, `dotnet-distributed-execution-engineer`, `cloud-native-site-reliability-engineer`.
- **Size:** L. **Depends on:** STORY-13.5.1, EPIC-09, EPIC-10.
- **Acceptance criteria:**
  - [ ] Given chaos scenarios for pod kill, node drain, DNS failure, object-store throttling, PVC I/O errors, clock skew, and shuffle loss When run in the bounded test namespace Then blast-radius controls prevent effects outside the suite.
  - [ ] Given executor or shuffle loss during actions When the workload completes or fails Then no rows are dropped, duplicated, or incorrectly aggregated and Delta commit history remains legal.
  - [ ] Given object-store or PVC failures during writes When recovery runs Then partial files, failed commits, checkpoint interruption, and ambiguous writes converge to documented safe outcomes.
  - [ ] Given release chaos gates run When any safety invariant fails Then GA promotion is blocked and artifacts include history, logs, seed, workload, and likely owner.
- **Definition of done:** builds/tests/format pass; checklists `21`, `17`, `10`, `09b`, `03a` satisfied; chaos scenario library and runbook links updated.

### FEAT-13.6: Documentation and migration guides

- **Objective:** Publish the v1.0 documentation set that makes DeltaSharp understandable, adoptable, operable, and supportable. Documentation must be versioned, validated, and grounded in the compatibility, benchmark, reliability, Delta, and runtime evidence produced by this epic.
- **Implementer persona(s):** Primary `technical-writer`; Collaborators `developer-experience-api-engineer`, `developer-relations-community-lead`, `performance-benchmarking-engineer`, `reliability-test-chaos-engineer`, `cloud-native-site-reliability-engineer`.
- **Depends on:** FEAT-13.1, FEAT-13.3, FEAT-13.4, FEAT-13.5.

#### Stories

##### STORY-13.6.1: Publish generated reference docs and conceptual guides

- **As a** DeltaSharp user **I want** complete reference and conceptual documentation **so that** I can learn APIs, SQL, Delta behavior, execution semantics, and Kubernetes operation without reading source code.
- **Implementer persona(s):** Primary `technical-writer`; Collaborators `developer-experience-api-engineer`, `query-execution-engine-engineer`, `delta-storage-format-engineer`.
- **Size:** L. **Depends on:** FEAT-13.1, FEAT-13.3.
- **Acceptance criteria:**
  - [ ] Given XML documentation comments and public API metadata When reference docs are generated Then `SparkSession`, DataFrame, Dataset, Column, functions, SQL, readers, writers, configuration, and errors are discoverable.
  - [ ] Given conceptual docs are reviewed When headings are inspected Then lazy transformations, eager actions, logical/analyzed/optimized/physical plans, stages, shuffle, Delta transactions, and Kubernetes execution are explained consistently.
  - [ ] Given public examples are compiled or tested When docs validation runs Then snippets either execute successfully or are explicitly marked non-executable with rationale.
  - [ ] Given docs are published When link checking and markdown validation run Then internal links, code fences, headings, and accessibility-oriented text pass project standards.
- **Definition of done:** builds/tests/format pass; checklists `11`, `markdown-style-guide`, `20`, `15`, `03a` satisfied; docs ownership and version labels included.

##### STORY-13.6.2: Publish PySpark/Scala migration guides and operational runbooks

- **As a** Spark user or platform operator **I want** migration guides and runbooks **so that** I can port workloads and operate v1.0 safely on Kubernetes and supported storage backends.
- **Implementer persona(s):** Primary `technical-writer`; Collaborators `developer-experience-api-engineer`, `developer-relations-community-lead`, `cloud-native-site-reliability-engineer`, `reliability-test-chaos-engineer`.
- **Size:** M. **Depends on:** STORY-13.6.1, FEAT-13.5.
- **Acceptance criteria:**
  - [ ] Given migration guides are published When a Spark user compares common PySpark and Scala patterns Then each example shows the DeltaSharp equivalent, intentional .NET idioms, limitations, and verification steps.
  - [ ] Given Kubernetes and Delta runbooks are reviewed When operators follow procedures Then driver/executor lifecycle, storage credentials, failure recovery, compatibility checks, and rollback/cleanup steps are explicit.
  - [ ] Given benchmark and reliability pages are reviewed When claims appear Then each claim links to owner-approved evidence, caveats, and reproduction instructions.
  - [ ] Given community feedback channels are linked When docs render Then users can report docs bugs, compatibility gaps, security issues, and migration friction through the correct routes.
- **Definition of done:** builds/tests/format pass; checklists `11`, `markdown-style-guide`, `10`, `21`, `03a` satisfied; migration and runbook pages linked from release notes.

### FEAT-13.7: Security hardening and disclosure readiness

- **Objective:** Complete GA security readiness for the open-source project, runtime, release artifacts, dependencies, and community vulnerability handling. v1.0 must ship with no open critical vulnerabilities and a clear private disclosure path.
- **Implementer persona(s):** Primary `cloud-native-security-sme`; Collaborators `dotnet-library-platform-engineer`, `developer-relations-community-lead`, `cloud-native-site-reliability-engineer`, `technical-writer`.
- **Depends on:** EPIC-00, EPIC-01, EPIC-10, FEAT-13.8.

#### Stories

##### STORY-13.7.1: Complete GA security review, pen-test closure, and threat-model evidence

- **As a** security owner **I want** release-blocking security findings closed or explicitly risk-accepted **so that** v1.0 does not ship with known critical exposure.
- **Implementer persona(s):** Primary `cloud-native-security-sme`; Collaborators `dotnet-library-platform-engineer`, `cloud-native-site-reliability-engineer`.
- **Size:** L. **Depends on:** EPIC-00, EPIC-10.
- **Acceptance criteria:**
  - [ ] Given threat models for APIs, drivers, executors, operator, shuffle, storage credentials, and release pipeline When reviewed Then all critical and high risks have mitigation, owner, status, or documented risk acceptance.
  - [ ] Given dependency, container, IaC, secret, and static-analysis scans run on the release candidate When results are triaged Then no critical vulnerabilities remain open and highs have approved disposition.
  - [ ] Given penetration-test findings When release readiness is reviewed Then every confirmed critical finding is fixed and regression coverage or compensating controls are linked.
  - [ ] Given security-sensitive docs are published When reviewed Then they do not expose secrets, unsafe defaults, or public vulnerability-reporting anti-patterns.
- **Definition of done:** builds/tests/format pass; checklists `05`, `10`, `13`, `11`, `03a` satisfied; security evidence attached to GA readiness.

##### STORY-13.7.2: Produce SBOM, provenance, and coordinated-disclosure runbook

- **Implement** supply-chain and disclosure artifacts for NuGet packages, NativeAOT images, dependency metadata, vulnerability intake, embargo workflow, and advisory publication.
- **Implementer persona(s):** Primary `cloud-native-security-sme`; Collaborators `dotnet-library-platform-engineer`, `developer-relations-community-lead`, `technical-writer`.
- **Size:** M. **Depends on:** STORY-13.7.1, FEAT-13.8.
- **Acceptance criteria:**
  - [ ] Given v1.0 packages and container images are built When release artifacts are inspected Then SBOM, dependency inventory, signatures or provenance metadata, and checksums are published or linked.
  - [ ] Given a simulated private vulnerability report When the disclosure runbook is exercised Then intake, triage, embargo, fix, advisory, credit, and release communication steps complete without using public issue disclosure.
  - [ ] Given community-facing security guidance is published When users read it Then private reporting channels, supported versions, expected response flow, and what not to post publicly are clear.
  - [ ] Given release readiness is checked When SBOM and disclosure artifacts are missing or stale Then GA promotion is blocked.
- **Definition of done:** builds/tests/format pass; checklists `05`, `10`, `11`, `markdown-style-guide`, `03a` satisfied; disclosure rehearsal outcome documented.

### FEAT-13.8: Release engineering and GA launch

- **Objective:** Ship v1.0 artifacts and public launch communications with versioning, packaging, smoke tests, release notes, contributor recognition, and community feedback channels. Release engineering must prove NuGet packages and NativeAOT images are usable before announcement.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `developer-relations-community-lead`, `cloud-native-site-reliability-engineer`, `technical-writer`, `cloud-native-security-sme`, `program-manager`.
- **Depends on:** FEAT-13.1, FEAT-13.2, FEAT-13.3, FEAT-13.4, FEAT-13.5, FEAT-13.6, FEAT-13.7.

#### Stories

##### STORY-13.8.1: Publish v1.0 NuGet packages and NativeAOT container images

- **As a** .NET developer or platform operator **I want** signed and smoke-tested v1.0 artifacts **so that** I can install DeltaSharp libraries and run NativeAOT executors from supported registries.
- **Implementer persona(s):** Primary `dotnet-library-platform-engineer`; Collaborators `cloud-native-site-reliability-engineer`, `cloud-native-security-sme`.
- **Size:** L. **Depends on:** FEAT-13.2, FEAT-13.7.
- **Acceptance criteria:**
  - [ ] Given release packaging runs When artifacts are produced Then NuGet packages target the documented TFMs and container images include the NativeAOT executor image required by ADR-0014.
  - [ ] Given packages are installed in a clean consumer project When smoke tests run Then a basic SparkSession/DataFrame/Delta workflow builds, executes, and references only supported public APIs.
  - [ ] Given container images are deployed to the documented Kubernetes smoke environment When a sample application runs Then driver, executor, and storage interactions complete successfully with expected status.
  - [ ] Given artifact metadata is inspected When release validation runs Then version, SemVer tags, checksums, SBOM/provenance links, license, and release notes URLs are present.
- **Definition of done:** builds/tests/format pass; checklists `10`, `05`, `20`, `03a` satisfied; artifact verification commands documented.

##### STORY-13.8.2: Publish GA release notes and community launch package

- **As a** community member **I want** clear v1.0 launch communication **so that** I understand what is GA, how to try it, what limitations remain, and how to contribute feedback.
- **Implementer persona(s):** Primary `developer-relations-community-lead`; Collaborators `technical-writer`, `dotnet-library-platform-engineer`, `performance-benchmarking-engineer`, `reliability-test-chaos-engineer`, `cloud-native-security-sme`.
- **Size:** M. **Depends on:** FEAT-13.1, FEAT-13.4, FEAT-13.5, FEAT-13.6, STORY-13.8.1.
- **Acceptance criteria:**
  - [ ] Given GA release notes are published When reviewed Then they include user impact, compatibility matrix link, migration guidance, performance summary, reliability evidence, security posture, known limitations, and upgrade/install steps.
  - [ ] Given the community announcement is drafted When owner review completes Then all API, performance, reliability, security, and roadmap claims are approved by the responsible personas.
  - [ ] Given contributor recognition is included When release notes render Then contributors, benchmark authors, bug reporters, and documentation contributors are credited according to project norms.
  - [ ] Given feedback channels are linked When users read the launch package Then discussions, issues, security reporting, docs feedback, and RFC routes are distinct and actionable.
- **Definition of done:** builds/tests/format pass; checklists `11`, `markdown-style-guide`, `05`, `22`, `03a` satisfied; launch checklist and announcement artifacts published.

## Open questions

- Which reference Spark version is the canonical v1.0 parity target, and how long is that version pinned after GA?
- Which non-DeltaSharp Delta engine is required for the GA interop gate, and are additional engines release-blocking or advisory?
- What exact benchmark thresholds define v1.0 release blockers versus post-GA performance backlog items?
- Which security provenance/signing standard is mandatory for v1.0 artifacts versus documented as a follow-up?
- What public support policy and patch cadence apply to v1.0 once security advisories and community issue reports begin?
