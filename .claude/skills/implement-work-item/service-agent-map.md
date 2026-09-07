# Implement Work Item — Service-to-Agent Mapping

This file maps `service:*` issue labels to the specialist agent personas responsible for implementation. The `implement-work-item` skill consults this map to determine which agent writes the code, what language to use, and which checklists to load.

> **Status: starter map.** DeltaSharp is greenfield. Services listed below are intended component areas; refine as design decisions land.

---

## How It Works

1. Read the issue's `service:*` label.
2. Match against the **Service → Agent** table below.
3. Load the agent's coding standards, checklists, and reference docs.
4. If no `service:*` label matches, use the **Fallback Rules** at the bottom.

---

## Service → Agent Mapping

### Public API and developer experience

| Service Label | Primary Agent | Secondary Agent | Language | Checklists |
|---------------|---------------|-----------------|----------|------------|
| `service:api` | `developer-experience-api-engineer` | `dotnet-framework-runtime-engineer` | C#/.NET | 03a, 04a, 04b, 15, 20 |
| `service:spark-session` | `developer-experience-api-engineer` | `query-execution-engine-engineer` | C#/.NET | 03a, 04a, 04b, 15, 20 |
| `service:dataframe` | `developer-experience-api-engineer` | `query-execution-engine-engineer` | C#/.NET | 03a, 04a, 04b, 15, 16, 20 |
| `service:dataset` | `developer-experience-api-engineer` | `dotnet-framework-runtime-engineer` | C#/.NET | 03a, 04a, 04b, 15, 20 |
| `service:functions` | `developer-experience-api-engineer` | `query-execution-engine-engineer` | C#/.NET | 03a, 04a, 04b, 15, 16 |
| `service:samples` | `developer-experience-api-engineer` | `technical-writer` | C#/.NET | 03a, 04a, 11, 20 |

### Query planning and execution engine

| Service Label | Primary Agent | Secondary Agent | Language | Checklists |
|---------------|---------------|-----------------|----------|------------|
| `service:logical-plan` | `query-execution-engine-engineer` | `developer-experience-api-engineer` | C#/.NET | 03a, 04a, 04b, 15, 16, 21 |
| `service:analyzer` | `query-execution-engine-engineer` | `delta-storage-format-engineer` | C#/.NET | 03a, 04a, 04b, 16, 17, 21 |
| `service:optimizer` | `query-execution-engine-engineer` | `performance-benchmarking-engineer` | C#/.NET | 03a, 04a, 04b, 08, 16, 21, 22 |
| `service:physical-planner` | `query-execution-engine-engineer` | `cloud-native-distributed-systems-architect` | C#/.NET | 03a, 04a, 04b, 16, 18, 21 |
| `service:execution-engine` | `query-execution-engine-engineer` | `dotnet-framework-runtime-engineer` | C#/.NET | 03a, 04a, 04b, 16, 18, 21 |
| `service:scheduler` | `query-execution-engine-engineer` | `cloud-native-site-reliability-engineer` | C#/.NET | 03a, 04a, 04b, 18, 21 |
| `service:shuffle` | `query-execution-engine-engineer` | `performance-benchmarking-engineer` | C#/.NET | 03a, 04a, 04b, 08, 18, 21, 22 |

### Delta storage and connectors

| Service Label | Primary Agent | Secondary Agent | Language | Checklists |
|---------------|---------------|-----------------|----------|------------|
| `service:delta-log` | `delta-storage-format-engineer` | `reliability-test-chaos-engineer` | C#/.NET | 03a, 04a, 04b, 17, 21 |
| `service:parquet` | `delta-storage-format-engineer` | `performance-benchmarking-engineer` | C#/.NET | 03a, 04a, 04b, 08, 17, 22 |
| `service:delta-writer` | `delta-storage-format-engineer` | `cloud-native-security-sme` | C#/.NET | 03a, 04a, 04b, 05, 14, 17, 21 |
| `service:delta-reader` | `delta-storage-format-engineer` | `query-execution-engine-engineer` | C#/.NET | 03a, 04a, 04b, 14, 16, 17 |
| `service:schema-evolution` | `delta-storage-format-engineer` | `data-platform-connectors-engineer` | C#/.NET | 03a, 04a, 04b, 16, 17, 19 |
| `service:time-travel` | `delta-storage-format-engineer` | `reliability-test-chaos-engineer` | C#/.NET | 03a, 04a, 04b, 17, 21 |
| `service:connectors` | `data-platform-connectors-engineer` | `delta-storage-format-engineer` | C#/.NET | 03a, 04a, 04b, 17, 19 |
| `service:catalog` | `data-platform-connectors-engineer` | `query-execution-engine-engineer` | C#/.NET | 03a, 04a, 04b, 16, 19 |
| `service:object-store` | `data-platform-connectors-engineer` | `cloud-native-security-sme` | C#/.NET | 03a, 04a, 04b, 05, 10, 14, 19 |
| `service:pvc-storage` | `data-platform-connectors-engineer` | `cloud-native-site-reliability-engineer` | C#/.NET | 03a, 04a, 04b, 10, 18, 19 |

### Kubernetes, runtime, reliability, and operations

| Service Label | Primary Agent | Secondary Agent | Language | Checklists |
|---------------|---------------|-----------------|----------|------------|
| `service:operator` | `cloud-native-site-reliability-engineer` | `cloud-native-distributed-systems-architect` | C#/.NET | 03a, 04a, 04b, 10, 13, 18 |
| `service:crds` | `cloud-native-site-reliability-engineer` | `technical-writer` | YAML + C#/.NET | 03a, 10, 11, 13, 18 |
| `service:driver` | `cloud-native-distributed-systems-architect` | `query-execution-engine-engineer` | C#/.NET | 03a, 04a, 04b, 10, 18, 21 |
| `service:executor` | `query-execution-engine-engineer` | `dotnet-framework-runtime-engineer` | C#/.NET | 03a, 04a, 04b, 10, 18, 21 |
| `service:runtime` | `dotnet-framework-runtime-engineer` | `performance-benchmarking-engineer` | C#/.NET | 03a, 04a, 04b, 08, 22 |
| `service:observability` | `cloud-native-site-reliability-engineer` | `performance-benchmarking-engineer` | C#/.NET | 03a, 09a, 09b, 09c, 10 |
| `service:chaos` | `reliability-test-chaos-engineer` | `delta-storage-format-engineer` | C#/.NET | 03a, 04a, 04b, 17, 21 |
| `service:benchmarks` | `performance-benchmarking-engineer` | `query-execution-engine-engineer` | C#/.NET | 03a, 04a, 04b, 08, 22 |

### Cross-cutting governance

| Service Label | Primary Agent | Secondary Agent | Language | Checklists |
|---------------|---------------|-----------------|----------|------------|
| `service:architecture` | `cloud-native-distributed-systems-architect` | `program-manager` | C#/.NET + docs | 01, 02, 15, 16, 17, 18 |
| `service:security` | `cloud-native-security-sme` | `privacy-compliance-grc-lead` | C#/.NET | 03a, 04a, 04b, 05, 07, 14 |
| `service:privacy` | `privacy-compliance-grc-lead` | `cloud-native-security-sme` | C#/.NET + docs | 05, 07, 14 |
| `service:finops` | `compute-storage-finops-engineer` | `performance-benchmarking-engineer` | C#/.NET + docs | 08, 19, 22 |
| `service:docs` | `technical-writer` | `developer-experience-api-engineer` | Markdown + C#/.NET samples | 11, 15, 20 |
| `service:product` | `product-manager` | `program-manager` | Markdown | 11 |
| `service:program` | `program-manager` | `product-manager` | Markdown | 11 |

---

## Checklist Reference Key

| Number | Filename | Topic |
|--------|----------|-------|
| 01 | `01-architecture-checklist.md` | Architecture patterns |
| 02 | `02-engine-implementation-checklist.md` | Engine/component design |
| 03a | `03a-dotnet-coding-standards.md` | C#/.NET coding standards |
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
| 16 | `16-catalyst-planning-checklist.md` | Logical/analyzed/optimized/physical plan correctness |
| 17 | `17-delta-storage-format-checklist.md` | Delta log, Parquet, ACID, schema evolution, time travel |
| 18 | `18-kubernetes-operator-checklist.md` | Operator, CRD, driver/executor, reconcile safety |
| 19 | `19-data-source-connectors-checklist.md` | Sources, sinks, catalogs, object stores, PVCs |
| 20 | `20-developer-experience-api-checklist.md` | Public API ergonomics and migration guidance |
| 21 | `21-distributed-correctness-checklist.md` | Lazy/eager behavior, stages, shuffle, fault correctness |
| 22 | `22-benchmark-regression-gates-checklist.md` | Benchmarks and performance regression gates |

---

## Fallback Rules

When the issue's `service:*` label does not match any entry above:

1. **Infer from code path**: `src/**/Planning/**`, `src/**/Optimizer/**`, `src/**/Execution/**`, or `src/**/Shuffle/**` defaults to `query-execution-engine-engineer`; `src/**/Delta/**`, `src/**/Parquet/**`, or `src/**/Storage/**` to `delta-storage-format-engineer`; `src/**/Connectors/**`, `src/**/Catalog/**`, or object-store/PVC adapters to `data-platform-connectors-engineer`; `src/**/Operator/**`, `deploy/**`, `helm/**`, or `k8s/**` to `cloud-native-site-reliability-engineer`; `src/**/Api/**`, `samples/**`, or `examples/**` to `developer-experience-api-engineer`; cross-cutting runtime/concurrency/memory work to `dotnet-framework-runtime-engineer`.
2. **Infer from issue type**:
   - `type:bug` in engine internals → `query-execution-engine-engineer` or `delta-storage-format-engineer` by path.
   - `type:spike` or `type:adr` → `cloud-native-distributed-systems-architect`.
   - `type:perf` or perf-regression → `performance-benchmarking-engineer`.
   - `type:chaos` or correctness-regression → `reliability-test-chaos-engineer`.
   - `type:privacy` or DSAR/erasure → `privacy-compliance-grc-lead`.
   - `type:cost` or capacity/cost guardrail → `compute-storage-finops-engineer`.
   - `type:docs` → `technical-writer`.
3. **Last resort**: Use `cloud-native-distributed-systems-architect` for architecture-level work, `dotnet-framework-runtime-engineer` for generic C#/.NET plumbing, or `technical-writer` for Markdown-only changes.

---

## Secondary Agent Rules

Secondary agents are dispatched when the implementation touches their domain:

- **Security SME** (`cloud-native-security-sme`): Always involved when the change handles auth, secrets, object-store/PVC credentials, tenant-scoped data, or code execution boundaries.
- **Architect** (`cloud-native-distributed-systems-architect`): Pulled in for driver/executor topology, stage/shuffle architecture, storage abstraction boundaries, or cross-component contract changes.
- **Privacy/Compliance Lead** (`privacy-compliance-grc-lead`): Pulled in for PII, lineage, retention, DSAR/erasure, audit evidence, or compliance posture.
- **Delta Storage Format Engineer** (`delta-storage-format-engineer`): Pulled in by query or connector work that touches Delta log, Parquet layout, snapshots, schema evolution, or commit protocol.
- **Query/Execution Engine Engineer** (`query-execution-engine-engineer`): Pulled in by API, storage, or connector work that changes logical/physical plan semantics, predicate/column pushdown, shuffle, actions, or task execution.
- **Performance Engineer** (`performance-benchmarking-engineer`): Pulled in when a change requires benchmark updates, regression gates, capacity models, memory allocation analysis, or profiler evidence.
- **Chaos Engineer** (`reliability-test-chaos-engineer`): Pulled in when a change requires correctness oracles, fault scenarios, fuzzing, crash safety, or commit-conflict testing.
- **FinOps** (`compute-storage-finops-engineer`): Pulled in when executor sizing, object-store/PVC IO, compression, caching, compaction, or retention choices have cost implications.
- **Developer Experience** (`developer-experience-api-engineer`): Pulled in when a change affects public API names, Spark parity, samples, migration guidance, or user-facing errors.
- **Runtime** (`dotnet-framework-runtime-engineer`): Pulled in for async, cancellation, concurrency, memory/GC, stream disposal, package layout, or .NET runtime integrations.

Secondary agents participate in implementation only when the issue explicitly requires their domain. Otherwise, they participate during the Phase 8 review-fix-test loop via the review-pr agent-map.
