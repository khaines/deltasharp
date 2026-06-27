# ADR-0012: Plan serialization (driver ↔ executor)

- **Status:** Proposed
- **Date:** 2026-06-27
- **Deciders:** @khaines
- **Related:** ADR-0003 (transport), ADR-0001, `docs/engineering/design/engine-architecture.md`

## Context

Physical plan fragments / tasks must be serialized from the driver to executors
over the gRPC control plane (ADR-0003). The wire format for plans is a decision.

## Options under consideration

- **Protobuf-defined plan** (versioned, language-neutral, fits gRPC).
- **Substrait** (cross-engine standard plan IR) compatibility — interop with
  other engines at the cost of mapping overhead.
- **Custom binary** (max control).

## Decision

TBD — to be resolved during backlog work.

## Gating / dependencies

Owned jointly by `query-execution-engine-engineer` (plan shape) and
`dotnet-distributed-execution-engineer` (wire/transport). Intersects the codegen
tier (ADR-0001) if compiled fragments are shipped vs re-derived on executors.
