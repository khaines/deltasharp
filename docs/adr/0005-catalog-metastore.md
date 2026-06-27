# ADR-0005: Catalog / metastore

- **Status:** Accepted
- **Date:** 2026-06-27
- **Deciders:** @khaines
- **Related:** ADR-0002, ADR-0007 (SQL frontend), `docs/engineering/design/engine-architecture.md`

## Context

DeltaSharp must track tables, schemas, namespaces, views, and functions, and
resolve them during analysis. Spark exposes a `Catalog` abstraction (V2
`CatalogPlugin`/`TableCatalog`) over a metastore. This subsystem is currently
**unowned**.

## Options under consideration

- **Native catalog** (DeltaSharp-owned store; simplest, full control).
- **Hive Metastore-compatible** (interop with existing lakehouse deployments).
- **Unity-Catalog-style REST catalog** (governance, multi-engine).
- **Pluggable `CatalogPlugin`/`TableCatalog`** (Spark-parity extension point;
  ship native + allow others).

## Decision

A **pluggable catalog** modeled on Spark's V2 `CatalogPlugin`/`TableCatalog`
extension point. Ship a **native DeltaSharp catalog** as the default; provide
**Hive Metastore compatibility as a first-party plugin** for lakehouse interop;
leave **Unity-Catalog-style REST governance** as a later plugin. A dedicated
`catalog-metastore-engineer` seat owns this subsystem.

## Gating / dependencies

Gates the candidate **`catalog-metastore-engineer`** persona. Feeds the SQL
frontend (ADR-0007) and the analyzer's name resolution.
