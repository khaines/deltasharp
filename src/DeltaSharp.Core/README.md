# DeltaSharp.Core

Public API surface for **DeltaSharp**, a .NET-native reimplementation of Apache Spark —
the Spark programming model (`SparkSession`, `DataFrame`/`Dataset<T>`, columns, SQL), native
Delta Lake tables, and Kubernetes-native distributed execution, without a JVM.

> **Status: M1 skeleton.** This package is intentionally **inert** — it exposes build/version
> metadata (`DeltaSharp.DeltaSharpInfo`) and **no** Spark or Delta behavior yet. It exists so
> the package, multi-targeting, and API-governance pipeline are in place before the engine
> lands. Do not take a production dependency on M1 behavior.

## Target frameworks

`DeltaSharp.Core` multi-targets **`net8.0`** and **`net10.0`** so current-LTS applications can
consume it while the engine runtime moves to `net10.0` (see ADR-0014). The library is kept
trim/AOT-annotation-clean.

## Links

- Source & issues: <https://github.com/khaines/deltasharp>
- License: Apache-2.0

## Versioning

Pre-release M1 (`0.1.0`). Public API changes are tracked with Roslyn public-API baselines, so
additions and removals are intentional and reviewable.
