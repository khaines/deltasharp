# Checklist Map — PR Review Skill

This file maps file patterns to engineering checklists so the `review-pr` skill knows which checklists to apply when reviewing a pull request. The skill reads this map, matches changed files against the patterns below, and loads the corresponding checklists from `docs/engineering/checklists/`.

> Some referenced checklist files are DeltaSharp equivalents to be authored; when absent, note the missing checklist and apply the project canon from `.github/copilot-instructions.md`.

---

## Primary Mapping

| File Pattern | Checklists | Notes |
|---|---|---|
| **C# / .NET source code** | | |
| `*.cs` | 03, 03a | All C# files get general conventions + .NET standards |
| `src/**/*.cs` | 03, 03a, 04 | Framework/library code also gets testing expectations |
| `tests/**/*.cs`, `*.Tests/**/*.cs` | 03, 03a, 04, 04a | Test code gets unit-testing checklist |
| `tests/**/*Integration*.cs`, `tests/**/*EndToEnd*.cs` | 03, 03a, 04, 04b | Integration tests get integration checklist |
| `*.csproj`, `*.sln`, `Directory.Build.*`, `Directory.Packages.props`, `NuGet.config`, `global.json` | 03a, 10 | Project, packaging, SDK/runtime configuration |
| `*.cs` with async I/O, streams, channels, tasks, locks, or cancellation | 03a, 08, 21, 22 | Runtime, concurrency, resource, and performance-sensitive code |
| `*.cs` hot path / algorithmic code | 03a, 08, 22 | Add performance checklist for optimizer, scan, shuffle, execution, and Delta paths |
| **Public API / Spark parity** | | |
| `src/**/Api/**`, `src/**/*SparkSession*.cs`, `src/**/*DataFrame*.cs`, `src/**/*Dataset*.cs`, `src/**/*Column*.cs`, `src/**/*Functions*.cs` | 15, 20, 03a, 04a | Spark API parity, ergonomics, public API stability |
| `samples/**`, `examples/**` | 15, 20, 11 | Samples and migration guidance |
| **Query planning and execution** | | |
| `src/**/LogicalPlan/**`, `src/**/Analyzer/**`, `src/**/Optimizer/**`, `src/**/PhysicalPlan/**`, `src/**/Execution/**`, `src/**/Planner/**` | 16, 21, 03a, 04a, 04b | Catalyst-style planning and distributed execution correctness |
| `src/**/Shuffle/**`, `src/**/Scheduler/**`, `src/**/Stage/**`, `src/**/Task/**` | 16, 18, 21, 08, 22 | Shuffle/partitioning, stage boundaries, scheduler correctness |
| **Delta storage and formats** | | |
| `src/**/Delta/**`, `src/**/Parquet/**`, `src/**/Storage/**`, `src/**/*DeltaLog*.cs`, `src/**/*Transaction*.cs`, `src/**/*Snapshot*.cs` | 17, 21, 03a, 04a, 04b | Delta log, Parquet, ACID, time travel, schema evolution |
| `src/**/Compaction/**`, `src/**/Checkpoint/**`, `src/**/SchemaEvolution/**` | 17, 08, 21, 22 | Delta maintenance and storage performance |
| **Connectors and catalogs** | | |
| `src/**/Connectors/**`, `src/**/Sources/**`, `src/**/Sinks/**`, `src/**/Catalog/**` | 19, 15, 16, 17 | Data sources/sinks, catalogs, predicate pushdown and schema-on-read |
| `src/**/S3/**`, `src/**/ADLS/**`, `src/**/GCS/**`, `src/**/ObjectStore/**`, `src/**/PVC/**`, `src/**/PersistentVolume/**` | 19, 05, 10, 14 | Cloud object stores and PersistentVolumes |
| **Kubernetes and operations** | | |
| `k8s/**`, `kubernetes/**`, `deploy/**`, `helm/**`, `charts/**`, `operator/**` | 10, 13, 18 | Kubernetes manifests, Operator, CRDs, deployment safety |
| `Dockerfile*`, `docker-compose*`, `Containerfile*` | 10, 05 | Container definitions and runtime security |
| `.github/workflows/*` | 10, 05 | CI/CD workflow definitions, permissions, secrets |
| `**/*.tf`, `**/*.tfvars`, `**/terraform/**`, `**/pulumi/**`, `**/opentofu/**` | 10, 13 | Infrastructure automation |
| **Security / privacy / tenancy** | | |
| `**/auth/**`, `**/authorization/**`, `**/security/**`, `**/iam/**`, `**/rbac/**` | 05, 07, 14 | **CRITICAL** — security and tenant isolation |
| `**/tenant/**`, `**/tenancy/**`, `**/*Tenant*.cs` | 05, 14 | **CRITICAL** — tenant isolation |
| `**/crypto/**`, `**/encryption/**`, `**/*secret*`, `**/*credential*`, `**/*token*`, `.env*`, `**/secrets/**` | 05 | **CRITICAL** — secrets, crypto, credentials |
| `**/privacy/**`, `**/compliance/**`, `**/audit/**`, `**/dsar/**`, `**/erasure/**`, `**/retention/**`, `**/redact*` | 07, 05, 14 | Privacy, audit, retention, erasure |
| **Observability** | | |
| `**/observability/**`, `**/logging/**`, `**/metrics/**`, `**/tracing/**`, `**/monitoring/**`, `**/alerting/**`, `**/prometheus/**`, `**/grafana/**` | 09a, 09b, 09c, 10 | Observability code/config |
| C# files with logging calls | 09a, 03a | Add logging checklist when log statements are present |
| C# files with metrics instrumentation | 09b, 03a | Add metrics checklist when metrics code is present |
| C# files with tracing spans | 09c, 03a | Add tracing checklist when tracing code is present |
| **Benchmarks and reliability** | | |
| `benchmarks/**`, `perf/**`, `performance/**`, `**/*Benchmark*.cs` | 08, 22, 03a | Benchmark suites and regression gates |
| `**/chaos/**`, `**/fault-injection/**`, `**/fuzz/**`, `**/simulation/**`, `**/oracles/**`, `**/invariants/**`, `**/*Property*Test*.cs` | 21, 04a, 04b | Chaos, fuzzing, deterministic simulation, correctness oracles |
| **Documentation** | | |
| `**/*.md` | 11, markdown-style-guide | Markdown documentation and skill files |
| `docs/engineering/adr/**` | 01, 11 | Architecture decision records |
| `docs/engineering/design/**` | 01, 11, 15, 16, 17, 18 | Design docs for engine/storage/operator concerns |
| `docs/product/**`, `docs/requirements/**` | 11 | Product and requirement framing |

### Checklist Number Key

| Number | Filename | Topic |
|---|---|---|
| 01 | `01-architecture-checklist.md` | Architecture patterns |
| 02 | `02-engine-implementation-checklist.md` | Engine/component design |
| 03 | `03-coding-conventions-checklist.md` | General coding conventions |
| 03a | `03a-dotnet-coding-standards.md` | C#/.NET coding standards |
| 04 | `04-testing-checklist.md` | General testing |
| 04a | `04a-unit-testing-checklist.md` | Unit testing |
| 04b | `04b-integration-testing-checklist.md` | Integration testing |
| 05 | `05-security-checklist.md` | Security |
| 07 | `07-privacy-checklist.md` | Privacy / GDPR |
| 08 | `08-performance-checklist.md` | Performance |
| 09a | `09a-logging-checklist.md` | Logging |
| 09b | `09b-metrics-checklist.md` | Metrics |
| 09c | `09c-distributed-tracing-checklist.md` | Distributed tracing |
| 10 | `10-runtime-environment-checklist.md` | Runtime / containers / Kubernetes |
| 11 | `11-documentation-support-checklist.md` | Documentation |
| 13 | `13-infrastructure-as-code-checklist.md` | Infrastructure as Code |
| 14 | `14-tenant-isolation-checklist.md` | Multi-tenant isolation |
| 15 | `15-spark-api-parity-checklist.md` | Spark API parity and semantics |
| 16 | `16-catalyst-planning-checklist.md` | Catalyst-style planning correctness |
| 17 | `17-delta-storage-format-checklist.md` | Delta and Parquet correctness |
| 18 | `18-kubernetes-operator-checklist.md` | Operator, CRDs, driver/executor safety |
| 19 | `19-data-source-connectors-checklist.md` | Data sources, sinks, catalogs, object stores, PVCs |
| 20 | `20-developer-experience-api-checklist.md` | Public API ergonomics and samples |
| 21 | `21-distributed-correctness-checklist.md` | Lazy/eager, stages, shuffle, fault correctness |
| 22 | `22-benchmark-regression-gates-checklist.md` | Benchmarks and regression gates |
| — | `markdown-style-guide-checklist.md` | Markdown formatting |

---

## Universal Checklists

These checklists apply to **every PR** regardless of which files changed:

- **03 — Coding Conventions** applies to all code files. Even if a more specific language checklist (03a) also applies, the general conventions checklist is always included.
- **05 — Security** receives a lightweight scan on all changes. The full security checklist is applied when security-sensitive patterns match.
- **DeltaSharp canon** from `.github/copilot-instructions.md` applies to all code and docs: Spark parity, native Delta tables, Kubernetes-native execution, lazy transformations/eager actions, and layer separation.

---

## Checklist Priority

Priority determines whether a checklist can be deprioritized when a PR touches many areas. Higher priority checklists are never skipped.

| Priority | Checklists | Rule |
|---|---|---|
| **CRITICAL** | 05 (Security), 14 (Tenant Isolation), 17 (Delta Storage), 21 (Distributed Correctness) | Never skipped when patterns match. Blocking issues must be resolved before merge. |
| **HIGH** | 03a (.NET), 04/04a/04b (Testing), 15 (Spark API), 16 (Catalyst), 18 (Operator), 19 (Connectors) | Applied to all matching code changes. Findings are expected to be addressed. |
| **STANDARD** | 01 (Architecture), 02 (Engine), 07 (Privacy), 08 (Performance), 09a/09b/09c (Observability), 10 (Runtime), 13 (IaC), 20 (Developer Experience), 22 (Benchmarks) | Applied when file patterns match. Findings are recommendations unless they affect correctness/safety. |
| **SUPPLEMENTARY** | 11 (Documentation), markdown-style-guide | Applied to documentation files. Non-blocking unless docs are factually incorrect or misleading. |

---

## Loading Instructions

When loading checklists, read the full file from `docs/engineering/checklists/{filename}` when it exists. Each checklist is a Markdown file with structured sections. Focus on checklist items relevant to the specific changes in the PR, not every item in the checklist. If an intended DeltaSharp checklist is not authored yet, state that it was unavailable and apply the project canon instead.
