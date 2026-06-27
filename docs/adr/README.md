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

## Still open (tracked, not yet decided)

- Memory model for batches: off-heap (`NativeMemory`, 64-byte aligned) vs GC-heap + `ArrayPool` (leaning off-heap).
- Target framework(s) and AOT posture (e.g., `net10.0`, multi-targeting, NativeAOT executor image).
- Codegen granularity beyond intra-operator fusion (cross-stage fusion) — deferred optimization.
