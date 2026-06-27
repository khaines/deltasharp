# ADR-0011: Delta protocol feature scope

- **Status:** Proposed
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

TBD — to be resolved during backlog work.

## Gating / dependencies

Roadmap for `delta-storage-format-engineer`; affects parity claims and
interoperability with other Delta engines.
