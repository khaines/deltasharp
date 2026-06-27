# ADR-0011: Delta protocol feature scope

- **Status:** Accepted
- **Date:** 2026-06-27
- **Deciders:** @khaines
- **Related:** ADR-0002, `docs/engineering/design/engine-architecture.md`

## Context

Delta Lake has versioned reader/writer protocols with optional features. We must
decide which features DeltaSharp supports and in what order. Owned by
`delta-storage-format-engineer`.

## Options under consideration

- **Baseline first:** add/remove/metadata actions, checkpoints, time travel,
  schema enforcement/evolution, `OPTIMIZE`/compaction, `VACUUM`.
- **Then, by demand:** deletion vectors, column mapping, generated columns,
  identity columns, Change Data Feed (CDF), liquid/clustering, V2 checkpoints,
  row tracking.
- Reader/writer protocol-version negotiation and capability gating.

## Decision

**v1 targets a broad Delta feature set.** Baseline: add/remove/metadata actions,
checkpoints, time travel, schema enforcement/evolution, `OPTIMIZE`/compaction, and
`VACUUM`. Plus the advanced writer features: **deletion vectors** (merge-on-read
deletes/updates), **column mapping** (id-based rename/drop without rewrite),
**Change Data Feed (CDF)**, **liquid clustering**, and **row tracking**, with V2
checkpoints. Reader/writer protocol-version negotiation gates capabilities so
tables remain interoperable with other Delta engines. Owned by
`delta-storage-format-engineer`; the write-time statistics that CBO/AQE (ADR-0006)
consume are collected here.

## Gating / dependencies

Roadmap for `delta-storage-format-engineer`; affects parity claims and
interoperability with other Delta engines.
