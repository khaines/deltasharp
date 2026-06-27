# ADR-0006: Scheduler, Adaptive Query Execution, and cost-based optimization

- **Status:** Proposed
- **Date:** 2026-06-27
- **Deciders:** @khaines
- **Related:** ADR-0001, ADR-0004, `docs/engineering/design/engine-architecture.md`

## Context

Two coupled concerns: (1) the driver's DAG scheduler — stages/tasks, data
locality, speculative execution, fair/multi-tenant scheduling; and (2) optimizer
sophistication — rule-based vs cost-based, and whether to do runtime replanning
(Adaptive Query Execution).

## Options under consideration

- **Optimizer:** rule-based only (v1) → add **cost-based optimizer (CBO)** with
  table/column statistics later.
- **Adaptive Query Execution (AQE):** runtime replanning (dynamic partition
  coalescing, skew-join handling, switch join strategy) — in v1 or deferred.
- **Scheduler:** FIFO first vs fair scheduler + per-tenant pools; speculative
  execution; locality-aware task placement.

## Decision

TBD — to be resolved during backlog work.

## Gating / dependencies

Shapes the depth of `query-execution-engine-engineer` and intersects shuffle
(ADR-0004) for skew/partition decisions.
