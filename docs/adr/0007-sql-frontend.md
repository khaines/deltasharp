# ADR-0007: SQL frontend — parser and dialect

- **Status:** Accepted
- **Date:** 2026-06-27
- **Deciders:** @khaines
- **Related:** ADR-0005 (catalog), ADR-0006 (optimizer), `docs/engineering/design/engine-architecture.md`

## Context

DeltaSharp needs a SQL frontend (lexer/parser → unresolved logical plan) and a
target SQL dialect. Spark separates the ANTLR4-based parser (`sql/catalyst`) from
the optimizer. Question: how to build the parser and how much dialect parity.

## Options under consideration

- **Parser tech:** ANTLR4 grammar (Spark-style; can mirror Spark's grammar shape)
  vs a hand-rolled recursive-descent parser.
- **Dialect:** ANSI SQL mode; degree of Spark SQL / HiveQL compatibility;
  reserved keywords; function library parity.

## Decision

Build the SQL frontend on an **ANTLR4 grammar that mirrors Spark's `SqlBase.g4`**
(ANTLR4 C# target) with **ANSI SQL mode**, to maximize Spark SQL dialect parity.
Ship a **core dialect first** and grow function/syntax coverage over time. A
dedicated **`sql-language-frontend-engineer`** seat owns the grammar, parser,
ANSI/dialect semantics, function registry, and name-resolution rules, and hands
the resolved logical plan to `query-execution-engine-engineer`
(optimize → physical → execute).

## Gating / dependencies

Gates the candidate **`sql-language-frontend-engineer`** persona (a possible
split from `query-execution-engine-engineer`). Depends on the catalog (ADR-0005)
for resolution and the type system (ADR-0008).
