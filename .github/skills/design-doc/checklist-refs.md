# Design Document — Checklist Cross-References

This file maps each section of the design document template (`docs/engineering/design/000-template.md`) to the engineering checklists that should be consulted during generation. The `design-doc` skill reads the relevant checklists to ensure design decisions align with DeltaSharp engineering standards.

> Some referenced checklist files are DeltaSharp equivalents to be authored under `docs/engineering/checklists/`; cite them as intended references without inventing their contents.

---

## Section-to-Checklist Mapping

| Section | Title | Checklists | Purpose |
|---------|-------|------------|---------|
| §1 | Overview | — | No checklist — populated from issue and requirements |
| §2 | Logical Architecture | 01, 02, 15, 16, 17, 18 | Architecture, engine boundaries, Spark parity, Catalyst planning, Delta storage, Kubernetes execution |
| §2.4 | Data Model / Schema | 16, 17, 19 | Logical plan schema, DataFrame schema semantics, Delta schema evolution, Parquet layout |
| §2.5 | API Surface | 03, 03a, 15, 20 | .NET conventions, public API compatibility, `SparkSession` / `DataFrame` ergonomics |
| §2.7 | Multi-Tenancy | 05, 14 | Tenant isolation and secure data boundaries |
| §3 | Functional Test Scenarios | 04, 04a, 04b, 21 | General testing, unit tests, integration tests, lazy/eager and distributed correctness |
| §4 | Performance | 08, 22 | Performance methodology, benchmark/regression gates, allocation and GC budgets |
| §5 | Security | 05, 07, 14 | Security, privacy, tenant isolation |
| §5.4 | Tenant Isolation | 05, 14 | Security + tenant isolation across plans, storage, tasks, and credentials |
| §6 | Threat Model | 05, 14 | Threat modelling for API, driver/executor, operator, catalog, object-store, and PVC boundaries |
| §7.1 | Logging Strategy | 09a | Logging checklist |
| §7.2 | Metrics & Dashboards | 09b | Metrics checklist |
| §7.3 | Distributed Tracing | 09c | Distributed tracing checklist |
| §7.4 | Alerting Rules | 09b, 10 | Metrics and runtime alerting |
| §8.1 | Rollout Strategy | 10, 13 | Runtime environment and infrastructure automation |
| §8.5 | Launch Checklist | 01, 05, 10, 13, 15, 16, 17, 18, 21, 22 | Architecture, security, runtime, Spark parity, planner, Delta, operator, correctness, performance |
| §9 | Open Questions | — | No checklist — populated during generation |
| §10 | References | — | No checklist — assembled from consulted docs |

---

## Checklist Reference Key

| Number | Filename | Topic |
|--------|----------|-------|
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
| 13 | `13-infrastructure-as-code-checklist.md` | Infrastructure as Code |
| 14 | `14-tenant-isolation-checklist.md` | Multi-tenant isolation |
| 15 | `15-spark-api-parity-checklist.md` | Spark API parity and semantic compatibility |
| 16 | `16-catalyst-planning-checklist.md` | Logical plan, analyzer, optimizer, physical planner |
| 17 | `17-delta-storage-format-checklist.md` | Delta transaction log, Parquet, ACID, time travel, schema evolution |
| 18 | `18-kubernetes-operator-checklist.md` | Operator reconciliation, driver/executor pods, shuffle and lifecycle safety |
| 19 | `19-data-source-connectors-checklist.md` | Data source/sink, catalog, object-store and PVC connectors |
| 20 | `20-developer-experience-api-checklist.md` | Public API ergonomics, samples, docs, migration guides |
| 21 | `21-distributed-correctness-checklist.md` | Lazy/eager behavior, actions, stages, shuffle, consistency and chaos tests |
| 22 | `22-benchmark-regression-gates-checklist.md` | Benchmarks, allocation/GC budgets, throughput and latency gates |

---

## Best-Practices Cross-References

In addition to checklists, the following best-practices docs should be loaded for context during section generation:

| Section | Best-Practices Doc | Purpose |
|---------|-------------------|---------|
| §2 | `01-architecture.md`, `02-distributed-engine.md`, `15-spark-api-parity.md`, `16-catalyst-planning.md`, `17-delta-storage-format.md`, `18-kubernetes-operator.md` | Architectural guardrails |
| §3 | `04-testing.md`, `21-distributed-correctness.md` | Testing philosophy and distributed correctness standards |
| §4 | `08-performance.md`, `22-benchmark-regression-gates.md` | Performance standards, workloads, budgets |
| §5, §6 | `05-security.md`, `07-privacy.md`, `14-tenant-isolation.md` | Security, privacy, and tenant isolation standards |
| §7 | `09-observability.md` | Observability standards |
| §8 | `10-runtime-environment.md`, `13-infrastructure-as-code.md` | Kubernetes runtime and delivery standards |

---

## ADR Cross-References

These ADRs are frequently relevant to design documents and should be loaded when their section is being generated, if authored:

| Section | Relevant ADRs | Topic |
|---------|--------------|-------|
| §2 | ADR-001, ADR-002, ADR-003, ADR-004 | .NET-native architecture, Spark parity, planning pipeline, distributed execution |
| §2.7 | ADR-005 | Tenant isolation model |
| §5.4 | ADR-005 | Tenant isolation model |
| §7 | ADR-006 | Observability stack |
| §8 | ADR-007, ADR-008, ADR-009 | Kubernetes Operator, container runtime, delivery pipeline |
| §8 (IaC) | ADR-010 | Infrastructure as Code |

---

## Loading Instructions

When loading checklists, read the full file from `docs/engineering/checklists/{filename}`. Focus on checklist items relevant to the component being designed, not every item in the checklist. Similarly, load best-practices and ADRs for reference context — the design doc should **reference** these documents, not duplicate their content.
