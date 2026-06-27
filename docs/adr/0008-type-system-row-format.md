# ADR-0008: Type system and internal row/value representation

- **Status:** Proposed
- **Date:** 2026-06-27
- **Deciders:** @khaines
- **Related:** ADR-0002 (columnar format), `docs/engineering/design/engine-architecture.md`

## Context

The engine needs a type system (primitives, `decimal`, date/timestamp, and
complex `array`/`map`/`struct`) with Spark SQL parity and well-defined null
semantics, plus an internal value/row representation. This intersects ADR-0002
(columnar batches): Spark uses **both** a row form (`InternalRow`/`UnsafeRow`) and
a columnar form (`ColumnarBatch`).

## Options under consideration

- **Columnar-only internally** (simpler; everything is `ColumnVector`).
- **Row + columnar hybrid** (row for narrow point ops/shuffle keys, columnar for
  scans/vectorized ops) — Spark's model; needs an `UnsafeRow`-equivalent.
- Null semantics: SQL three-valued logic; validity bitmaps (ADR-0002).
- Decimal/timestamp precision and overflow behavior (ANSI).

## Decision

TBD — to be resolved during backlog work.

## Gating / dependencies

Foundational for `query-execution-engine-engineer`,
`dotnet-vectorized-columnar-compute-engineer`, and `delta-storage-format-engineer`.
