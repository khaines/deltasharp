# PR Review — Agent Mapping Reference

This file maps file patterns and content signals to the specialist agent personas used during pull-request reviews. The `review-pr` skill consults this map to decide which agents should participate in a given review.

**How it works:**

1. For every changed file in a PR, match its path against the **File Pattern → Agent** table.
2. Apply the **Priority Rules** to decide which agents are included vs. advisory.
3. Run the **Content-Based Detection** heuristics for additional agent triggers that path patterns alone cannot capture.
4. Assemble the final reviewer set — every matched agent reviews the files that triggered it; the agent with the most file matches is designated the **primary** reviewer.

---

## 1. File Pattern → Agent Mapping

### dotnet-framework-runtime-engineer

> C#/.NET service and library design, async/concurrency, memory/GC behavior, package layout, runtime integration.

| Pattern | Priority | Notes |
|---|---|---|
| `*.cs` | High | C# source files |
| `*.csproj`, `*.sln`, `Directory.Build.*`, `Directory.Packages.props`, `NuGet.config`, `global.json` | High | .NET project and SDK configuration |
| `src/**/Runtime/**`, `src/**/Common/**`, `src/**/Hosting/**` | High | Runtime plumbing and shared framework code |
| `**/*Async*`, `**/*Channel*`, `**/*Pool*`, `**/*Buffer*` | High | Async/memory-sensitive runtime code |
| `tests/**/*.cs` | Standard | Test code also reviewed by domain owner |

### developer-experience-api-engineer

> Spark API parity ergonomics, DataFrame/Dataset/SparkSession surface, migration paths, samples, API stability.

| Pattern | Priority | Notes |
|---|---|---|
| `src/**/Api/**` | High | Public API surface |
| `src/**/*SparkSession*.cs` | High | Session entry point |
| `src/**/*DataFrame*.cs`, `src/**/*Dataset*.cs`, `src/**/*Column*.cs` | High | DataFrame/Dataset/Column APIs |
| `src/**/*Functions*.cs`, `src/**/*Sql*.cs` | High | Functions and SQL-facing APIs |
| `samples/**`, `examples/**` | High | Example applications |
| `docs/**/api/**`, `docs/**/migration/**`, `docs/**/quickstart/**` | High | User-facing API docs |

### query-execution-engine-engineer

> SQL/DataFrame semantics, logical/physical planning, Catalyst-style analyzer/optimizer, joins, fan-out, caching, shuffle execution, read-time tenant isolation.

| Pattern | Priority | Notes |
|---|---|---|
| `src/**/LogicalPlan/**`, `src/**/Plans/**` | High | Logical plan code |
| `src/**/Analyzer/**`, `src/**/Analysis/**` | High | Name/type resolution |
| `src/**/Optimizer/**`, `src/**/Rules/**` | High | Optimizer rules |
| `src/**/PhysicalPlan/**`, `src/**/Planner/**` | High | Physical planning |
| `src/**/Execution/**`, `src/**/Executor/**` | High | Execution operators |
| `src/**/Shuffle/**`, `src/**/Partitioning/**`, `src/**/Stage/**`, `src/**/Task/**`, `src/**/Scheduler/**` | High | Shuffle, partitioning, stage/task scheduling |
| `src/**/Sql/**`, `src/**/Expressions/**`, `src/**/Joins/**`, `src/**/Aggregations/**` | High | Query semantics |
| `tests/**/*Plan*.cs`, `tests/**/*Execution*.cs`, `tests/**/*Optimizer*.cs` | High | Engine tests |

### delta-storage-format-engineer

> Delta transaction log, Parquet, on-disk layout, write path, compaction, indexing, ACID/durability, time travel, schema evolution.

| Pattern | Priority | Notes |
|---|---|---|
| `src/**/Delta/**`, `src/**/*Delta*.cs` | High | Delta table code |
| `src/**/Parquet/**`, `src/**/*Parquet*.cs` | High | Parquet format code |
| `src/**/Storage/**`, `src/**/Snapshots/**`, `src/**/Checkpoints/**` | High | Storage internals |
| `src/**/*Transaction*.cs`, `src/**/*Commit*.cs`, `src/**/*Action*.cs`, `src/**/*Log*.cs` | High | Delta transaction log and commit protocol |
| `src/**/Compaction/**`, `src/**/Vacuum/**`, `src/**/SchemaEvolution/**`, `src/**/TimeTravel/**` | High | Delta maintenance and table features |
| `tests/**/*Delta*.cs`, `tests/**/*Parquet*.cs`, `tests/**/*Storage*.cs` | High | Storage tests |

### data-platform-connectors-engineer

> Data source/sink API, readers/writers, file formats, catalog integration, ingestion pipelines, schema-on-read.

| Pattern | Priority | Notes |
|---|---|---|
| `src/**/Connectors/**`, `src/**/Sources/**`, `src/**/Sinks/**` | High | Data source/sink APIs |
| `src/**/Catalog/**`, `src/**/*Catalog*.cs` | High | Catalog integration |
| `src/**/ObjectStore/**`, `src/**/S3/**`, `src/**/ADLS/**`, `src/**/GCS/**` | High | Cloud object stores |
| `src/**/PVC/**`, `src/**/PersistentVolume/**`, `src/**/FileSystem/**` | High | PVC/local filesystem adapters |
| `src/**/Csv/**`, `src/**/Json/**`, `src/**/Arrow/**` | High | Non-Delta data formats |
| `tests/**/*Connector*.cs`, `tests/**/*Catalog*.cs`, `tests/**/*ObjectStore*.cs` | High | Connector tests |

### cloud-native-distributed-systems-architect

> Engine topology, driver/executor model, DAG scheduler, shuffle, multi-tenant design, Kubernetes Operator, Delta integration, reliability/security/observability trade-offs.

| Pattern | Priority | Notes |
|---|---|---|
| `docs/engineering/adr/**` | Critical | Architecture Decision Records |
| `docs/engineering/design/**` | Critical | Component design documents |
| `docs/**/architecture/**`, `docs/**/topology/**`, `docs/**/system-design/**` | Critical | Architecture documents |
| `src/**/Driver/**`, `src/**/Cluster/**`, `src/**/Distributed/**` | High | Driver/executor topology |
| _(new service or component)_ | Critical | Any file that introduces a new major component |
| _(cross-layer contract changes)_ | Critical | API/planner/execution/storage/operator contract changes |

### cloud-native-site-reliability-engineer

> Production SLOs, observability, alerting, rollout safety, disaster recovery, incident response, operator/executor-pod operations.

| Pattern | Priority | Notes |
|---|---|---|
| `src/**/Operator/**`, `operator/**` | High | Kubernetes Operator code |
| `k8s/**/*.yaml`, `k8s/**/*.yml`, `kubernetes/**/*.yaml`, `kubernetes/**/*.yml` | High | Kubernetes manifests |
| `deploy/**`, `helm/**`, `charts/**` | High | Deployment manifests/charts |
| `Dockerfile*`, `Containerfile*`, `docker-compose*` | High | Container definitions |
| `.github/workflows/*` | High | CI/CD workflows |
| `**/monitoring/**`, `**/alerting/**`, `**/grafana/**`, `**/prometheus/**`, `**/observability/**` | High | Observability configuration |
| `**/terraform/**`, `**/pulumi/**`, `**/opentofu/**` | High | Infrastructure automation (also triggers architect) |

### cloud-native-security-sme

> Zero trust, IAM/authorization, tenant isolation, object-store/PVC secrets, secure delivery, supply-chain integrity.

| Pattern | Priority | Notes |
|---|---|---|
| `**/auth/**`, `**/authorization/**`, `**/security/**` | Critical | Auth/security modules |
| `**/tenant/**`, `**/tenancy/**`, `**/*Tenant*.cs` | Critical | Tenant isolation |
| `**/crypto/**`, `**/encryption/**` | Critical | Cryptographic code |
| `**/*secret*`, `**/*credential*`, `**/*token*`, `.env*`, `**/secrets/**` | Critical | Secrets/credential handling |
| `**/rbac/**`, `**/iam/**` | Critical | Identity and access management |
| `.github/workflows/*` | High | Workflow permissions and supply-chain review |

### privacy-compliance-grc-lead

> PII handling in processed data, GDPR/CCPA/SOC 2, retention, data lineage, DSAR/erasure, audit evidence.

| Pattern | Priority | Notes |
|---|---|---|
| `**/privacy/**`, `**/compliance/**`, `docs/compliance/**` | Critical | Privacy/compliance code and docs |
| `**/dsar/**`, `**/erasure/**`, `**/redact*` | Critical | Data-subject rights and redaction |
| `**/retention/**`, `**/legal-hold*` | Critical | Retention/legal hold |
| `**/audit/**`, `**/audit-log*`, `**/audit_log*` | Critical | Audit evidence/logging |
| `**/pii/**`, `**/lineage/**` | Critical | PII and data lineage |

### performance-benchmarking-engineer

> Pre-prod performance methodology, load generators, TPC-DS-style workloads, .NET profiling, regression gates, capacity models.

| Pattern | Priority | Notes |
|---|---|---|
| `benchmarks/**`, `**/benchmarks/**`, `**/bench/**` | High | Benchmark suites |
| `**/*Benchmark*.cs`, `**/*Perf*.cs` | High | Benchmark/performance code |
| `**/loadgen/**`, `**/load-gen*` | High | Load generators |
| `**/perf/**`, `**/performance/**` | High | Performance harnesses/reports |
| `**/profiling/**`, `**/trace/**`, `**/counters/**` | High | Profiling/tooling output |
| `**/capacity-model*`, `**/capacity_plan*` | High | Capacity models/plans |

### reliability-test-chaos-engineer

> Data-correctness oracles, Delta ACID/consistency tests, crash-safety, deterministic simulation, fuzzing, Jepsen-style consistency.

| Pattern | Priority | Notes |
|---|---|---|
| `**/chaos/**`, `**/fault-injection*`, `**/fault_injection*` | High | Fault-injection harnesses |
| `**/fuzz/**`, `**/*Fuzz*.cs` | High | Fuzzing harnesses/corpora |
| `**/simulation/**`, `**/sim/**` | High | Deterministic simulation |
| `**/jepsen/**` | High | Consistency tests |
| `**/property*test*`, `**/*Property*Test*.cs` | High | Property-based tests |
| `**/oracles/**`, `**/invariants/**` | High | Correctness oracles/invariant checks |
| `**/recovery-test*`, `**/crash-test*` | High | Recovery/crash-safety tests |

### compute-storage-finops-engineer

> Cost modeling, unit economics, executor/compute + object-store cost, compression/tiering ROI, capacity forecasting, per-tenant attribution.

| Pattern | Priority | Notes |
|---|---|---|
| `**/finops/**`, `**/cost/**` | High | Cost modeling code/docs |
| `**/metering/**`, `**/usage/**` | High | Usage metering primitives |
| `**/billing*` (when engineering-cost, not commercial) | High | Cost attribution |
| `**/tiering/**`, `**/compression/**`, `**/caching/**` | High | Storage/compute efficiency choices |
| `**/budget*`, `**/quota*` (when cost-related) | High | Cost budgets and guardrails |

### technical-writer

> Documentation architecture, docs-as-code, tutorials, troubleshooting guides.

| Pattern | Priority | Notes |
|---|---|---|
| `docs/**/*.md` | Standard | Documentation files except ADR/design when architect is primary |
| `**/*.md` | Standard | Any Markdown fallback |
| `*.md` (root) | Standard | Root-level docs |
| `**/api-docs/**`, `**/reference/**` | Standard | API documentation |
| `CHANGELOG*`, `RELEASE*` | Standard | Changelog/release notes |

### product-manager

> Product direction, user value, Spark-parity roadmap, requirement framing, product trade-offs.

| Pattern | Priority | Notes |
|---|---|---|
| `docs/requirements/**`, `docs/product/**` | Standard | Requirements and product framing |
| `**/roadmap*` | Standard | Product roadmap when primarily product value |

### program-manager

> Multi-workstream planning, sequencing, timelines, execution control, dependency/risk management.

| Pattern | Priority | Notes |
|---|---|---|
| `**/execution-plan*`, `**/program-plan*` | Standard | Execution plans |
| `**/roadmap*` | Standard | Roadmap documents when primarily sequencing/dependencies |
| `docs/persona/**` | Standard | Persona library coordination docs |

---

## 2. Priority Rules

### Priority Levels

| Level | Behaviour | Agents |
|---|---|---|
| **Critical** | **Always** included when patterns match, regardless of how many other agents are active. | `cloud-native-security-sme`, `cloud-native-distributed-systems-architect`, `privacy-compliance-grc-lead` |
| **High** | Included when their patterns match. These are domain-specialist agents. | `dotnet-framework-runtime-engineer`, `developer-experience-api-engineer`, `query-execution-engine-engineer`, `delta-storage-format-engineer`, `data-platform-connectors-engineer`, `cloud-native-site-reliability-engineer`, `performance-benchmarking-engineer`, `reliability-test-chaos-engineer`, `compute-storage-finops-engineer` |
| **Standard** | Included only when the file falls within their primary domain or no higher-priority agent also claims it. | `technical-writer`, `product-manager`, `program-manager` |

### Multi-Agent Resolution

1. **All matching agents review.** If a file matches patterns for more than one agent, every matching agent is added to the reviewer set for that file.
2. **Primary agent designation.** The agent with the most file matches across the entire PR is designated the primary reviewer and provides the top-level summary.
3. **Tie-breaking.** When two agents match the same number of files, prefer the agent with the higher priority level (Critical > High > Standard).
4. **Cross-triggers.** Some patterns explicitly trigger a second agent:
   - Delta storage changes → `delta-storage-format-engineer` + `reliability-test-chaos-engineer` for ACID/correctness risk.
   - Planner/optimizer changes → `query-execution-engine-engineer` + `developer-experience-api-engineer` for Spark semantics.
   - Shuffle/stage/scheduler changes → `query-execution-engine-engineer` + `cloud-native-distributed-systems-architect`.
   - Operator/CRD changes → `cloud-native-site-reliability-engineer` + `cloud-native-distributed-systems-architect`.
   - Object-store/PVC credential changes → `data-platform-connectors-engineer` + `cloud-native-security-sme`.
   - Benchmark/load changes → `performance-benchmarking-engineer` + the engine/storage/API owner being benchmarked.
   - Cost changes → `compute-storage-finops-engineer` + `performance-benchmarking-engineer`.

---

## 3. Content-Based Detection

File-path patterns catch most cases, but some agent assignments require inspecting the **content** of changed files. Apply these heuristics after pattern matching.

### Import & Dependency Signals

| Signal | Example | Triggers Agent |
|---|---|---|
| Kubernetes client usage | `KubernetesClient`, `k8s` models, CRD reconciliation | `cloud-native-site-reliability-engineer` |
| Security / crypto APIs | `System.Security.Cryptography`, JWT, OAuth/OIDC, secret providers | `cloud-native-security-sme` |
| Parquet / Arrow / columnar APIs | Parquet reader/writer, Arrow arrays, column chunks | `delta-storage-format-engineer` |
| Object-store SDKs | AWS S3, Azure Storage, Google Cloud Storage clients | `data-platform-connectors-engineer` + `cloud-native-security-sme` |
| Query parser / expression tree APIs | SQL parser, expression visitors, logical plan nodes | `query-execution-engine-engineer` |
| Benchmarking/profiling APIs | BenchmarkDotNet, EventPipe, counters, trace APIs | `performance-benchmarking-engineer` + `dotnet-framework-runtime-engineer` |
| Property/fuzz testing APIs | FsCheck, fuzz harnesses, invariant frameworks | `reliability-test-chaos-engineer` |
| Async/runtime primitives | `Task`, `ValueTask`, `IAsyncEnumerable`, `Channel`, `SemaphoreSlim`, `ArrayPool` | `dotnet-framework-runtime-engineer` |
| Observability libraries | structured logging, metrics, tracing packages | `cloud-native-site-reliability-engineer` |

### Operation Signals

| Signal | Example | Triggers Agent |
|---|---|---|
| New public API method or overload | `DataFrame.Select`, `SparkSession.Builder` | `developer-experience-api-engineer` |
| New or changed optimizer rule | predicate pushdown, column pruning, constant folding | `query-execution-engine-engineer` + `performance-benchmarking-engineer` |
| Delta commit action changes | add/remove file actions, metadata/protocol updates | `delta-storage-format-engineer` + `reliability-test-chaos-engineer` |
| Schema evolution changes | merge schema, overwrite schema, type widening | `delta-storage-format-engineer` + `data-platform-connectors-engineer` |
| Shuffle or partitioning change | repartition, coalesce, exchange planning | `query-execution-engine-engineer` + `cloud-native-distributed-systems-architect` |
| Operator reconcile or finalizer change | CRD status, pod lifecycle, cleanup | `cloud-native-site-reliability-engineer` |
| Secret or credential construction | hardcoded token, key material, environment secret | `cloud-native-security-sme` |
| PII, retention, erasure, lineage | personal data classification or deletion | `privacy-compliance-grc-lead` |
| Cost / metering / quota logic | executor-hour attribution, storage IO cost | `compute-storage-finops-engineer` |
| Benchmark gate update | new threshold, workload, profiler artifact | `performance-benchmarking-engineer` |
| Chaos / fault / oracle change | crash simulation, conflict test, invariant | `reliability-test-chaos-engineer` |

### Notes

- Content-based detection is **additive** — it adds agents to the reviewer set; it never removes an agent that was already matched by path.
- When content detection triggers an agent that was not matched by path, annotate the review with the specific signal that caused the addition so reviewers understand why the agent was included.
- For large PRs (50+ changed files), prioritise content scanning on files that did **not** already match a path pattern to keep review latency manageable.
