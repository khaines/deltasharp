# ADR-0008: Type system and internal row/value representation

- **Status:** Accepted
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

A **row + columnar hybrid**: the mutable `ColumnBatch`/`ColumnVector` (ADR-0002)
for scans and vectorized compute, plus a compact **binary row format (an
`UnsafeRow` analog: 8-byte-aligned, null-bitset, byte-sortable, shuffle/spill
serializable)** for shuffle partition/sort/join keys and materialized result rows.
Full **Spark SQL type-system parity** — primitives, `decimal`, date/timestamp, and
complex `array`/`map`/`struct` — with **ANSI** null and overflow semantics and SQL
three-valued logic. Owned jointly by `dotnet-vectorized-columnar-compute-engineer`
(columnar) and `dotnet-runtime-performance-engineer` (binary row layout/sort), with
`query-execution-engine-engineer` and `sql-language-frontend-engineer` consuming
the type system.

## Gating / dependencies

Foundational for `query-execution-engine-engineer`,
`dotnet-vectorized-columnar-compute-engineer`, and `delta-storage-format-engineer`.
