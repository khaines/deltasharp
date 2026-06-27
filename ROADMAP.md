# DeltaSharp Roadmap

This roadmap is **directional, not a commitment** — scope and sequencing change as
we learn and as the community contributes. It is grounded in the accepted
[Architecture Decision Records](docs/adr/README.md); each milestone links to the
ADRs that define it. Track concrete work in issues, milestones, and
[RFCs](docs/rfcs/README.md).

**Status:** pre-release / greenfield. Versions below are themes, not dates.

## Pillars

1. **Apache Spark parity** — `SparkSession`/`DataFrame`/`Dataset`/SQL and execution
   semantics.
2. **Native Delta tables** — first-class Delta Lake, implemented in .NET.
3. **Kubernetes-native** — distributed execution via a custom Operator.
4. **Open source** — an Apache-2.0, community-driven project ([ADR-0015](docs/adr/0015-open-source-positioning.md)).

## Milestone 1 — Engine foundations (v0.1)

- Pluggable **vectorized interpreter** execution backend; optional JIT codegen tier
  gated on dynamic-code support ([ADR-0001](docs/adr/0001-execution-strategy.md)).
- **Arrow-compatible `ColumnBatch`/`ColumnVector`**, Arrow at the edges ([ADR-0002](docs/adr/0002-columnar-batch-format.md)).
- **Off-heap** buffers + a unified memory manager with spill ([ADR-0013](docs/adr/0013-memory-model.md)).
- **Row + columnar** type system with Spark type parity + ANSI ([ADR-0008](docs/adr/0008-type-system-row-format.md)).
- `net10.0` engine + **NativeAOT** executor posture; multi-targeted libraries ([ADR-0014](docs/adr/0014-target-framework-aot.md)).
- Core `SparkSession`/`DataFrame`/`Dataset` API; local single-node execution.

## Milestone 2 — Storage & SQL (v0.x)

- Native **Delta** read/write with a broad protocol feature set — deletion vectors,
  column mapping, CDF, clustering, row tracking ([ADR-0011](docs/adr/0011-delta-protocol-scope.md)); Parquet I/O.
- **Pluggable catalog** + native default + Hive-metastore plugin ([ADR-0005](docs/adr/0005-catalog-metastore.md)).
- **ANTLR4 SQL frontend** mirroring Spark, ANSI mode, core dialect ([ADR-0007](docs/adr/0007-sql-frontend.md)).

## Milestone 3 — Distributed execution (v0.x)

- Driver/executor over **gRPC control + Arrow Flight data** ([ADR-0003](docs/adr/0003-data-plane-transport.md)).
- **Native remote shuffle service**: node-local workers + location registry +
  drain-migration + configurable replication ([ADR-0004](docs/adr/0004-shuffle-architecture.md)).
- **Protobuf** plan serialization ([ADR-0012](docs/adr/0012-plan-serialization.md)).
- **Kubernetes Operator** (KubeOps): `DeltaSharpApplication` + `DeltaSharpSession`
  CRDs ([ADR-0009](docs/adr/0009-kubernetes-operator-crds.md)).

## Milestone 4 — Optimization (v0.x)

- **Cost-based optimizer + statistics**, **Adaptive Query Execution** (skew,
  partition coalescing, join-strategy switching), and a **fair scheduler** with
  resource pools ([ADR-0006](docs/adr/0006-scheduler-aqe-cbo.md)).

## v1.0 — Spark-parity batch + streaming on Kubernetes

- Robust batch + SQL at scale on Kubernetes across object storage and PVCs.
- **Micro-batch Structured Streaming** ([ADR-0010](docs/adr/0010-structured-streaming-scope.md)).
- JIT **codegen** fast-path; broad Spark API/SQL compatibility matrix.
- Delta interoperability with other engines.

## Beyond 1.0 (candidates)

- **Continuous / low-latency streaming** (Flink-like) — deferred in [ADR-0010](docs/adr/0010-structured-streaming-scope.md).
- **Substrait** cross-engine plan interop — deferred in [ADR-0012](docs/adr/0012-plan-serialization.md).
- **Unity-Catalog-style** governance plugin — [ADR-0005](docs/adr/0005-catalog-metastore.md).
- **Dynamic allocation / executor autoscaling** and spot-aware scheduling.
- Wider connector ecosystem and zero-copy Arrow interop with Python/other engines.

## Influencing the roadmap

Open a GitHub Discussion or issue, or submit an [RFC](docs/rfcs/README.md) for
substantial proposals. Roadmap stewardship is shared by the product/program
management and developer-relations roles described in `docs/persona/agents/`.
