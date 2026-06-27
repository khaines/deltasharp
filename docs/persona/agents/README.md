# Persona Agents

This directory contains the canonical, research-backed source definitions for
`deltasharp` role agents. The persona library was imported from the sibling
`gamegrid`, `blogflow-pro`, and `labtested-storage` projects and adapted to
DeltaSharp — a .NET-native Apache Spark equivalent with native Delta tables and a
Kubernetes Operator execution model. `labtested-storage` (a multi-tenant storage
+ query engine) is the closest domain match and forms the roster base.

## Why these exist

Persona research documents (in `../research/`) define the skills, behaviors,
traits, and knowledge for each role. These agent specs turn that research into
reusable operating profiles that can be loaded by different AI runtimes (Claude,
GitHub Copilot, etc.) without rewriting the underlying role logic each time. The
library exists to drive **project timelines, implementations, and reviews** with
consistent, high-judgment roles.

## Agents vs. skills

- **Agents** are role-shaped personas with goals, judgment, tone, boundaries, and
  preferred outputs (this directory).
- **Skills** are narrower reusable capabilities that multiple agents share (see
  `.github/skills/`: `design-doc`, `implement-work-item`, `review-fix-loop`,
  `review-pr`).

## Source of truth and wrappers

| Layer | Purpose | Files |
|---|---|---|
| Canonical specs | Human-readable source of truth for the role | `docs/persona/agents/*-agent.md` |
| Research docs | Deep-dive research backing the canonical specs | `docs/persona/research/*.md` |
| Claude wrappers | Claude Code project subagents derived from the canonical role | `.claude/agents/*.md` |
| Copilot wrappers | GitHub Copilot custom agent profiles derived from the canonical role | `.github/agents/*.agent.md` |

Wrappers are lightweight pointers to the canonical spec. If a wrapper and its
canonical spec drift, the canonical spec wins.

## DeltaSharp domain canon

Every persona internalizes the DeltaSharp architecture (see
`.github/copilot-instructions.md`): **transformations are lazy, actions are
eager**; a Catalyst-style pipeline (logical plan → analyzer/optimizer → physical
plan → execution); stages split at shuffle boundaries; Delta tables backed by
Parquet and the `_delta_log` transaction log (ACID, time travel, schema
evolution); a driver coordinating executor pods under a Kubernetes Operator; and
storage on both cloud object stores (S3/ADLS/GCS) and PersistentVolumes (PVCs).
Foundational engineering decisions (execution backend, columnar batch format,
transport, shuffle) are recorded as ADRs in `docs/adr/` and summarized in
`docs/engineering/design/engine-architecture.md`; every persona **defers** to those
ADRs rather than redefining them.

## Agent roster (24 agents)

Cross-cutting and platform:

| Agent | Use when the main need is... | Hands off when the main need becomes... |
|---|---|---|
| Product Manager | product direction, user value, Spark-parity roadmap, requirement framing, product trade-offs | cross-team orchestration, dependencies, governance, execution cadence (Program Manager) |
| Program Manager | multi-workstream planning, sequencing, **timelines**, execution control, dependency/risk management | unresolved product choices, user-value trade-offs, roadmap strategy (Product Manager) |
| Cloud-Native Distributed Systems Architect | engine topology, driver/executor model, DAG scheduler, shuffle, multi-tenant design, K8s operator, Delta integration, reliability/security/observability trade-offs | unresolved product direction or cross-workstream execution governance |
| Cloud-Native Site Reliability Engineer | production SLOs, observability, alerting, rollout safety, disaster recovery, incident response, operator/executor-pod operations | pre-prod benchmarking (Performance), chaos harness design (Reliability/Chaos), or primary security-boundary design (Security SME) |
| Cloud-Native Security SME | zero trust, IAM/authorization, tenant isolation, object-store/PVC secrets, supply-chain integrity | product prioritization or regulatory posture (Privacy/Compliance) |
| Privacy, Compliance & GRC Lead | PII handling in processed data, GDPR/CCPA/SOC 2, retention, data lineage, DSAR/erasure, audit evidence | encryption mechanics (Security SME), incident-response execution (SRE), commercial terms (PM) |
| Technical Writer | documentation architecture, API/SDK reference, runbooks, migration guides, docs-as-code | engineering implementation, product strategy, or research outside docs scope |
| Developer Experience & API Engineer | Spark API parity ergonomics, DataFrame/Dataset/SparkSession surface, source compatibility, PySpark/Scala migration paths, samples, API stability | engine internals (Query/Execution Engine, Delta & Storage Format) or product prioritization (PM) |

Engine and data:

| Agent | Use when the main need is... | Hands off when the main need becomes... |
|---|---|---|
| Delta & Storage Format Engineer | Delta transaction log, Parquet, on-disk layout, write path, compaction, indexing, ACID/durability, time travel, schema evolution | ingest/sources above the engine (Data Sources & Connectors), query planning (Query/Execution Engine), service topology (Architect), production SLOs (SRE) |
| Query & Execution Engine Engineer | SQL/DataFrame semantics, logical/physical planning, Catalyst-style optimizer, codegen, joins, fan-out, caching, shuffle execution, read-time tenant isolation | on-disk internals (Delta & Storage Format), ingest pipelines (Data Sources & Connectors), API ergonomics (Developer Experience) |
| Data Sources & Connectors Engineer | data source/sink API, readers/writers, file formats, catalog integration, ingestion pipelines, schema-on-read | on-disk Delta internals (Delta & Storage Format), query planning (Query/Execution Engine), business-metric definition |
| Performance & Benchmarking Engineer | pre-prod performance methodology, load generators, TPC-DS-style workloads, .NET profiling, regression gates, capacity models | production SLOs (SRE), correctness under fault (Reliability/Chaos), pricing (FinOps via efficiency curves) |
| Reliability Test & Chaos Engineer | data-correctness oracles, Delta ACID/consistency tests, crash-safety, deterministic simulation, fuzzing, Jepsen-style consistency | production gameday execution (SRE), happy-path performance (Performance), red-team (Security SME) |
| Compute & Storage FinOps Engineer | cost modeling, unit economics, executor/compute + object-store cost, compression/tiering ROI, capacity forecasting, per-tenant attribution | pricing strategy (PM), operational capacity ops (SRE), engine micro-benchmarks (Delta & Storage Format / Performance) |
| .NET Framework & Runtime Engineer | C#/.NET service & library design, async/concurrency, memory/GC behavior, interop, API-surface implementation | engine algorithm internals (Query/Execution or Delta & Storage Format), platform topology/SLO/security ownership |

Deep .NET engineering (the four first-class seats; see `docs/adr/`):

| Agent | Use when the main need is... | Hands off when the main need becomes... |
|---|---|---|
| .NET Runtime & Performance Engineer | CLR/GC/JIT/AOT tuning, allocation engineering, `unsafe`/SIMD hot paths, the optional JIT codegen tier + AOT feature-gating, BenchmarkDotNet authorship, EventPipe/`dotnet-trace` diagnosis & fix | benchmark methodology/regression gates (Performance), columnar kernel algorithms (Vectorized Columnar Compute), plan/operator semantics (Query/Execution Engine) |
| .NET Vectorized Columnar Compute Engineer | SIMD kernels over `ColumnVector`/Arrow batches, selection vectors, dictionary peeling, null-aware compute, the default interpreter backend's operators | on-disk Parquet/Delta (Delta & Storage Format), plan/operator semantics (Query/Execution Engine), the SIMD/`unsafe` toolbox & codegen tier (Runtime & Performance) |
| .NET Distributed Execution Engineer | gRPC host + Kestrel HTTP/2, `IHostedService` lifecycle & graceful K8s shutdown, `Channels` task dispatch, the native remote shuffle service (workers, location registry, drain-migration, replication), Arrow Flight `IDataExchange` | topology/CRDs/operator design (Architect), task compute (Query/Execution Engine), runtime GC/JIT tuning (Runtime & Performance), production SLOs (SRE) |
| .NET Library & Package Platform Engineer | NuGet/multi-targeting, build governance, Roslyn analyzers & source generators, public-API enforcement, trim/Native-AOT readiness & feature-switch hygiene | public API shape/docs/migration (Developer Experience & API), runtime/GC behavior (Runtime & Performance) |

Subsystem seats (v1 scope decisions; see `docs/adr/`):

| Agent | Use when the main need is... | Hands off when the main need becomes... |
|---|---|---|
| Catalog & Metastore Engineer | namespaces/tables/views/functions, pluggable `CatalogPlugin` + native catalog, Hive-metastore compatibility, identifier resolution (ADR-0005) | Delta log/Parquet on disk (Delta & Storage Format), external source federation (Data Sources & Connectors), authz (Security SME) |
| SQL Language & Frontend Engineer | ANTLR4 grammar (Spark `SqlBase` parity), parser, ANSI mode, dialect/function parity, name/type resolution → resolved plan (ADR-0007) | optimize/physical/execute (Query/Execution Engine), CBO/AQE (Query Optimizer & Scheduler), DataFrame API surface (Developer Experience) |
| Structured Streaming Engine Engineer | micro-batch incremental execution, sources/sinks, offsets, state stores, watermarks, checkpointing, exactly-once (ADR-0010) | the batch engine it reuses (Query/Execution Engine), Delta source/sink + CDF (Delta & Storage Format), external sources (Data Sources & Connectors) |
| Query Optimizer & Scheduler Engineer | cost-based optimizer + statistics, Adaptive Query Execution (skew/coalesce/strategy), fair scheduler + resource pools (ADR-0006) | rule-based optimizer & execution mechanics (Query/Execution Engine), write-time stats (Delta & Storage Format), task dispatch (Distributed Execution) |
| Kubernetes Operator & Controller Engineer | KubeOps operator, CRDs (Application/Session), reconcilers, webhooks, finalizers, driver/executor/shuffle lifecycle & scaling (ADR-0009) | topology/CRD design (Architect), process hosting/shuffle runtime (Distributed Execution), production SLOs (SRE) |

## Provenance

- **Imported and adapted from `labtested-storage`** (closest domain): Product
  Manager, Program Manager, Distributed Systems Architect, SRE, Security SME,
  Privacy/Compliance/GRC Lead, Technical Writer, Performance & Benchmarking
  Engineer, Reliability Test & Chaos Engineer.
- **Remapped engine roles** (from `labtested-storage`): Delta & Storage Format
  Engineer (from telemetry-storage-engine), Query & Execution Engine Engineer
  (from telemetry-query-engine), Data Sources & Connectors Engineer (from
  data-platform-telemetry), Compute & Storage FinOps Engineer (from
  storage-finops), .NET Framework & Runtime Engineer (from
  cloud-native-systems-engineer).
- **Developer Experience & API Engineer** — new, drawing on `blogflow-pro`'s
  developer-experience-api-engineer and `gamegrid`'s
  game-developer-experience-sdk-engineer.
- **Subsystem seats — from the engine ADRs.** Catalog & Metastore (ADR-0005),
  SQL Language & Frontend (ADR-0007), Structured Streaming Engine (ADR-0010),
  Query Optimizer & Scheduler (ADR-0006), and Kubernetes Operator & Controller
  (ADR-0009) were added as the v1 scope decisions turned them on.
- **Dropped from the lts base:** `game-data-analytics-experimentation-engineer`
  (dashboard-authoring focus, not relevant to a processing framework).

## Design principles

1. **Roles, not tasks.** Each persona is defined by judgment and ownership, not a
   fixed list of deliverables.
2. **Research-backed.** Each canonical spec links back to a research document that
   justifies its competencies and anti-patterns.
3. **Composable.** Agents are expected to collaborate; handoff rules are explicit
   in each spec.
4. **Runtime-agnostic.** The canonical spec is the source of truth; wrappers are
   thin platform adapters.

## Follow-up / known gaps

- **.NET engineering seats — delivered.** The four first-class .NET engineering
  seats (`dotnet-runtime-performance-engineer`,
  `dotnet-vectorized-columnar-compute-engineer`,
  `dotnet-distributed-execution-engineer`, `dotnet-library-platform-engineer`) have
  been added alongside the original `dotnet-framework-runtime-engineer` (retained as
  the general C# service/library-design seat). Their decisions are recorded in
  `docs/adr/` and `docs/engineering/design/engine-architecture.md`. Remaining depth:
  a dedicated NativeAOT/trimming seat may be split out later if a NativeAOT executor
  image is pursued.
- **Go-flavored framing.** Imported specs/research were adapted from Go sources;
  some examples remain runtime-neutral rather than deeply .NET-specific until the
  follow-up above lands.
- **Optional pi runtime wrappers** (`ds-*` in `~/.pi/agent/agents/`) are not part
  of the repo and can be generated separately.
