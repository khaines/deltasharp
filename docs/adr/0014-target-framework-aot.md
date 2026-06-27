# ADR-0014: Target framework and AOT posture

- **Status:** Proposed
- **Date:** 2026-06-27
- **Deciders:** @khaines
- **Related:** ADR-0001 (codegen tier / AOT gating), `docs/engineering/design/engine-architecture.md`

## Context

The target framework(s) and Native AOT posture affect the whole codebase and gate
the ADR-0001 codegen-tier elision. Decision needed early.

## Options under consideration

- **TFM:** single-target `net10.0` (simplest, newest perf) vs multi-target
  (`netstandard2.0`/`net8.0`/`net10.0`) for broad library consumption.
- **AOT:** ship a **NativeAOT executor image** (fast cold start, low memory for
  ephemeral pods; requires the codegen tier to be cleanly elided) vs JIT-only
  executors with tiered compilation + dynamic PGO.
- Trimming posture for libraries (annotation-clean for AOT consumers).

## Decision

TBD — to be resolved during backlog work.

## Gating / dependencies

Owned by `dotnet-library-platform-engineer`; directly determines whether the
ADR-0001 codegen tier ships and how `dotnet-runtime-performance-engineer` tunes
the runtime.
