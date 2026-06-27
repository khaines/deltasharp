# DeltaSharp

> A fully native **.NET reimplementation of Apache Spark** — with first-class
> **Delta tables** and **Kubernetes-native** distributed execution. No JVM.

[![License: Apache 2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
![Status: early development](https://img.shields.io/badge/status-early%20development-orange.svg)

> 🚧 **Early development.** DeltaSharp is greenfield: the architecture and roadmap
> are defined (see [`docs/adr/`](docs/adr/README.md)), but there is **no released
> code yet**. The API snippets below show the **target** API. Contributions and
> design feedback are very welcome — see [Contributing](#contributing).

## What is DeltaSharp?

DeltaSharp brings the Apache Spark programming model — `SparkSession`,
`DataFrame`/`Dataset<T>`, columns, and SQL — to **idiomatic C#/.NET**, executing
**natively** (no JVM bridge). It is built around four pillars:

1. **Apache Spark parity** — match Spark's API and execution semantics so code and
   concepts port over directly.
2. **Native Delta tables** — Delta Lake (transaction log, ACID, time travel, schema
   evolution, deletion vectors, CDF) implemented in .NET.
3. **Kubernetes-native** — distributed driver/executor execution managed by a
   custom Operator and CRDs.
4. **Open source** — Apache-2.0, community-driven ([ADR-0015](docs/adr/0015-open-source-positioning.md)).

## Why DeltaSharp?

[.NET for Apache Spark](https://github.com/dotnet/spark) is a **JVM bridge**: every
DataFrame call is a round-trip into a JVM-hosted Spark, and all execution, memory
management, shuffle, and Parquet I/O happen in the JVM. DeltaSharp is a **native
engine** — it implements the optimizer, vectorized execution, shuffle, Delta, and
Parquet **in .NET**, enabling:

- **Native AOT** executors with fast cold start and low memory for ephemeral
  Kubernetes pods ([ADR-0014](docs/adr/0014-target-framework-aot.md)).
- **Vectorized columnar execution** with SIMD kernels over Arrow-compatible batches
  ([ADR-0001](docs/adr/0001-execution-strategy.md), [ADR-0002](docs/adr/0002-columnar-batch-format.md)).
- A **.NET-native remote shuffle service** designed for spot/scale-down resilience
  ([ADR-0004](docs/adr/0004-shuffle-architecture.md)).

## The big idea

DeltaSharp follows Spark's layered model: the API builds an immutable **logical
plan** (lazy), a Catalyst-style **analyzer + optimizer** (rule-based plus a
cost-based optimizer and Adaptive Query Execution) produces a **physical plan**,
and **actions** trigger distributed execution across executor pods. The defining
invariant: **transformations are lazy, actions are eager.**

## Example (target API)

```csharp
using DeltaSharp.Sql;
using static DeltaSharp.Sql.Functions;

using var spark = SparkSession.Builder()
    .AppName("quickstart")
    .GetOrCreate();

// Read a Delta table, transform lazily, act eagerly.
var df = spark.Read().Format("delta").Load("/data/events");

df.Filter(Col("country") == "US")
  .GroupBy("device")
  .Agg(Count("*").As("events"))
  .OrderBy(Col("events").Desc())
  .Show();

// SQL is a first-class door into the same engine.
spark.Sql("SELECT device, COUNT(*) AS n FROM delta.`/data/events` GROUP BY device")
     .Show();
```

## Architecture & design

Decisions are recorded as **Architecture Decision Records** — the source of truth:

- [`docs/adr/`](docs/adr/README.md) — 15 ADRs (execution, columnar format,
  transport, shuffle, catalog, optimizer/AQE, SQL, types, operator, streaming,
  Delta protocol, plan serialization, memory, target framework, OSS).
- [`docs/engineering/design/engine-architecture.md`](docs/engineering/design/engine-architecture.md)
  — the overview with diagrams.
- [`.github/copilot-instructions.md`](.github/copilot-instructions.md) — the
  conventions summary.

## Roadmap

See [ROADMAP.md](ROADMAP.md) — milestones mapped to the ADRs.

## Building from source

DeltaSharp targets **.NET 10** (engine) and multi-targets public libraries
(`net8.0;net10.0`). Once the solution is scaffolded:

```bash
dotnet restore
dotnet build -c Release
dotnet test
dotnet format --verify-no-changes
```

## Contributing

We welcome code, tests, docs, triage, and design proposals.

- [CONTRIBUTING.md](CONTRIBUTING.md) — dev setup, DCO sign-off, PR process.
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) — Contributor Covenant.
- [GOVERNANCE.md](GOVERNANCE.md) — how decisions are made and how to become a
  maintainer.
- [docs/rfcs/](docs/rfcs/README.md) — the RFC process for substantial changes.

## Security

Please report vulnerabilities privately — see [SECURITY.md](SECURITY.md).

## License

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE). Contributions are
accepted under the same license via DCO sign-off.
