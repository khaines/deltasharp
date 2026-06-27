# ADR-0006: Scheduler, Adaptive Query Execution, and cost-based optimization

- **Status:** Accepted
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

**v1 includes the full optimizer/scheduler stack:** a rule-based optimizer **plus a
cost-based optimizer (CBO)** driven by table/column statistics, **Adaptive Query
Execution (AQE)** — runtime re-optimization at stage boundaries (dynamic partition
coalescing, skew-join handling, join-strategy switching) using live shuffle
statistics — and a **fair scheduler with resource pools** (weighted pools,
min-share, preemption) for multi-tenant scheduling.

This requires a **statistics subsystem**: write-time table/column stats (row/NDV
counts, min/max, histograms) collected by `delta-storage-format-engineer` into
Delta/Parquet stats and the catalog (ADR-0005), plus runtime shuffle statistics
feeding AQE. Owned primarily by `query-execution-engine-engineer` (optimizer + AQE);
the fair scheduler spans `cloud-native-distributed-systems-architect` and
`dotnet-distributed-execution-engineer`. Given the scope, a dedicated
**scheduler/optimizer/statistics** seat is a candidate (tracked in the backlog).

## Gating / dependencies

Shapes the depth of `query-execution-engine-engineer` and intersects shuffle
(ADR-0004) for skew/partition decisions.
