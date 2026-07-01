# DeltaSharp.Abstractions

Shared logical contracts for **DeltaSharp**, a .NET-native reimplementation of Apache Spark —
the packable seam between the public `DeltaSharp.Core` API surface and the `DeltaSharp.Engine`
runtime. It holds the **shared logical Spark-parity type model**: the `DataType` hierarchy
(`StructType`/`StructField`, `Array`/`Map`/`Decimal` types, the atomic singletons), the
`DataTypes` factory, coercion, and ANSI mode (ADR-0008), under the `DeltaSharp.Types`
namespace (mirroring Spark's `org.apache.spark.sql.types`).

> **Status: M1 (pre-1.0).** The ADR-0008 **logical type model now lives here**: the shared
> Spark-parity `DataType` hierarchy and factory listed above, consumed by both
> `DeltaSharp.Core` and `DeltaSharp.Engine` (moved out of `DeltaSharp.Engine` via the atomic
> ADR-0016 S1b+S2 migration). The internal `StableHash` is an implementation detail, not part
> of the public surface. Do not take a production dependency on M1 behavior.

## Target frameworks

`DeltaSharp.Abstractions` multi-targets **`net8.0`** and **`net10.0`** (ADR-0014) so it can be
referenced by BOTH the public `net8.0;net10.0` `DeltaSharp.Core` surface AND the
`net10.0`-only `DeltaSharp.Engine`, without inverting their sibling independence
(Core ⟂ Engine). It ships as its own package that `DeltaSharp.Core` depends on. The library is
kept trim/AOT-annotation-clean.

## Links

- Source & issues: <https://github.com/khaines/deltasharp>
- Design: [ADR-0016](https://github.com/khaines/deltasharp/blob/main/docs/adr/0016-shared-logical-type-model-abstractions.md)
- License: Apache-2.0

## Versioning

Pre-release M1 (`0.1.0`). Public API changes are tracked with Roslyn public-API baselines, so
additions and removals are intentional and reviewable.
