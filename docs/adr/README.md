# Architecture Decision Records (ADRs)

This directory is the **authoritative source of truth** for DeltaSharp's
foundational engineering decisions. Each ADR captures one decision with its
context, the decision itself, consequences, and the alternatives that were
weighed and rejected.

Summaries elsewhere — the [engine architecture overview](../engineering/design/engine-architecture.md),
`.github/copilot-instructions.md`, and the persona specs in
`docs/persona/agents/` — **defer to these ADRs and must not redefine them**. If a
summary and an ADR disagree, the ADR wins.

## Conventions

- One decision per file, numbered `NNNN-kebab-title.md`.
- ADRs are **append-only**: to change a decision, add a new ADR that supersedes
  the old one (update the old ADR's Status to `Superseded by ADR-XXXX`).
- Use [`0000-adr-template.md`](0000-adr-template.md) as the starting point.

## Index

| ADR | Title | Status |
|---|---|---|
| [0001](0001-execution-strategy.md) | Execution strategy: pluggable vectorized interpreter + optional JIT codegen tier | Accepted |
| [0002](0002-columnar-batch-format.md) | In-memory columnar batch format: Arrow-compatible custom (Arrow-first) | Accepted |
| [0003](0003-data-plane-transport.md) | Transport split: gRPC control plane + Arrow Flight data plane | Accepted |
| [0004](0004-shuffle-architecture.md) | Shuffle: native remote shuffle service with location registry, drain-migration + replication | Accepted |
| [0005](0005-catalog-metastore.md) | Catalog / metastore | Proposed |
| [0006](0006-scheduler-aqe-cbo.md) | Scheduler, Adaptive Query Execution, cost-based optimization | Proposed |
| [0007](0007-sql-frontend.md) | SQL frontend — parser and dialect | Proposed |
| [0008](0008-type-system-row-format.md) | Type system and internal row/value representation | Proposed |
| [0009](0009-kubernetes-operator-crds.md) | Kubernetes Operator and CRD design | Proposed |
| [0010](0010-structured-streaming-scope.md) | Structured Streaming scope | Proposed |
| [0011](0011-delta-protocol-scope.md) | Delta protocol feature scope | Proposed |
| [0012](0012-plan-serialization.md) | Plan serialization (driver ↔ executor) | Proposed |
| [0013](0013-memory-model.md) | Memory model for in-memory batches | Proposed |
| [0014](0014-target-framework-aot.md) | Target framework and AOT posture | Proposed |

## Backlog (Proposed ADRs)

ADR-0005 through ADR-0014 are **Proposed** — tracked decisions not yet made.
Several **gate candidate personas**: catalog/metastore (0005), SQL language &
frontend (0007), Kubernetes operator & controller (0009), and structured
streaming engine (0010). Codegen granularity beyond intra-operator fusion
(cross-stage) is tracked as a follow-up under ADR-0001.
