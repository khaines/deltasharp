# ADR-0010: Structured Streaming scope

- **Status:** Accepted
- **Date:** 2026-06-27
- **Deciders:** @khaines
- **Related:** ADR-0004 (shuffle), ADR-0008 (type system), `docs/engineering/design/engine-architecture.md`

## Context

Apache Spark provides Structured Streaming (an incremental execution model over
the same DataFrame API). Supporting it is a large subsystem: streaming
sources/sinks, state stores, watermarks, checkpointing, and exactly-once
guarantees. This is a **scope/roadmap gate** for v1.

## Options under consideration

- **Out for v1** — batch-only first; design the plan/execution layers so
  streaming can be added without rework.
- **Micro-batch streaming in v1** (Spark's default model).
- **Continuous processing** — later.

## Decision

**Micro-batch streaming is in scope for v1**, reusing the batch engine (streaming
= incremental batch: source offset management, state stores, watermarks,
checkpointing, exactly-once via transactional sinks). **Continuous /
record-at-a-time processing (Flink-like) is deferred to a later release** — design
the streaming source/sink, state-store, and execution seams so continuous mode can
be added later without reworking the batch engine.

## Gating / dependencies

Gates the candidate **`structured-streaming-engine-engineer`** persona (only if
streaming is in scope). Intersects state-store persistence (object-store/PVC) and
checkpointing.
