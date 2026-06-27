# ADR-0004: Shuffle architecture — native remote shuffle service with location registry, drain-migration + replication

- **Status:** Accepted
- **Date:** 2026-06-26
- **Deciders:** @khaines
- **Related:** ADR-0002 (columnar format), ADR-0003 (transport), `docs/engineering/design/engine-architecture.md`

## Context

Shuffle (the all-to-all exchange between map and reduce stages) is the hardest
part of a distributed engine on Kubernetes, because **executor pods are
ephemeral** and the project wants **dynamic allocation and spot instances** for
cost. Shuffle data must survive both executor-pod churn and **node/spot
scale-down**.

Mechanics that shaped the decision:

- **In-pod local disk** is fastest but lost on pod death.
- **StatefulSet PVCs** retain data on scale-down but **strand** it (no pod reads it
  until the same-ordinal pod returns), and fast PVCs are **ReadWriteOnce**
  (single-node mount) — which fundamentally fights the **all-to-all** read pattern
  of shuffle, where every reducer must read every mapper's output. Good for an
  executor recovering *its own* state; awkward as a shuffle-exchange medium.
- **Node-local ephemeral NVMe via a DaemonSet** is fast and survives executor
  churn, **but dies with the node** on scale-down/spot — unless data is migrated
  off the node first.
- On **graceful** scale-down (cordon + drain), the node's service gets SIGTERM and
  its `terminationGracePeriodSeconds` window, during which it **can migrate blocks
  to peers/object store** ("shuffle the shuffle"). But the window is bounded vs data
  size, and **spot interruption notice is only ~30 s–2 min** — too short to drain
  large shuffle.
- Executors can only **adapt to data that moves** if shuffle-block **locations are
  resolved dynamically through a registry** (Spark's `MapOutputTracker`; Celeborn's
  master), never pinned, with fetch retry + re-resolve.

Because the project is **native .NET "all the way,"** an existing JVM shuffle
service (e.g., Apache Celeborn) cannot be adopted; the equivalent must be built in
.NET.

## Decision

Build a **.NET-native remote shuffle service** (Celeborn-style), behind a
**`ShuffleManager`** abstraction:

- **Node-local workers** (DaemonSet) hold shuffle blocks on fast node-local
  storage. Executors **write to their local worker** (discovered via the K8s
  downward API `status.hostIP` / a fixed `hostPort` / a hostPath socket).
- A **shuffle location registry** (a `MapOutputTracker`-equivalent, authoritative
  map of `shuffleId/mapId/partitionId → current holder(s)`). Reducers **resolve
  locations dynamically** through the registry and fetch via **Arrow Flight**
  (ADR-0003). On fetch failure, **re-resolve and retry**; never pin a location.
- **Durability policy:**
  - **Drain-migration** on graceful scale-down (migrate blocks to peers/object
    store before the node dies; integrates with the executor/worker SIGTERM/preStop
    path).
  - **Eager replication** to protect against sudden crash/spot loss — write each
    block to **N worker nodes**, with **N configurable** (replication factor).
  - **Object-store fallback** as a **later, togglable** durability tier.
- **Shuffle model:** pull-based first (behind `ShuffleManager`); push-merge
  (Spark Magnet-style) is a later optimization.
- **Block format:** Arrow IPC blocks with **map-side partition merging** (fewer,
  larger files) to mitigate small-file/request cost.

## Consequences

### Positive

- Survives executor churn (local workers), sudden node/spot loss (replication), and
  graceful scale-down (drain-migration) — without stranded PVCs.
- Dynamic location resolution lets reducers adapt to data that moves.
- Fully native; no JVM dependency.

### Negative / costs

- A non-trivial distributed component to build in .NET (workers + location
  registry/master + client integration) — owned by
  `dotnet-distributed-execution-engineer` with `cloud-native-distributed-systems-architect`.
- Eager replication costs ~N× write bandwidth/storage (configurable to trade cost
  vs resilience) — a `compute-storage-finops-engineer` concern (replication cost vs
  recompute cost).

### Follow-ups and sequencing

- Phase 1: workers + location registry + dynamic resolution + drain-migration +
  configurable eager replication; pull-based; Arrow IPC + map-side merge.
- Phase 2: object-store fallback tier (togglable); push-merge; selection of
  replica-promotion vs object-store on sudden loss.

## Alternatives considered

- **In-pod local disk only:** simplest, loses shuffle on pod death. Rejected.
- **StatefulSet PVC per executor:** strands data on scale-down, ReadWriteOnce
  fights all-to-all reads, keeps paying for storage. Rejected for shuffle exchange.
- **Node-local DaemonSet only (no replication/migration):** dies with the node on
  scale-down/spot. Rejected; node-local NVMe is retained as the *fast tier* with
  migration + replication on top.
- **Object-store-only shuffle first:** most durable, simplest, but slowest; chosen
  instead as a *later togglable fallback* rather than the primary.
- **Adopt Apache Celeborn:** JVM-based; conflicts with native-.NET commitment.
  Rejected.

## References

- Spark `MapOutputTracker`; Spark decommissioning + fallback storage
  (`spark.storage.decommission.*`, `fallbackStorage.path`).
- Apache Celeborn (remote shuffle service: master + workers + replication).
- Kubernetes `persistentVolumeClaimRetentionPolicy`, ReadWriteOnce vs
  ReadWriteMany access modes; StatefulSet `volumeClaimTemplates`.
- Cloud spot interruption notices: AWS EC2 Spot (~2 min), GCP/Azure (~30 s).
- Arrow Flight (ADR-0003) for block transfer.
