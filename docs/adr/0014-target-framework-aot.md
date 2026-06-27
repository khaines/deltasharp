# ADR-0014: Target framework and AOT posture

- **Status:** Accepted
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

**Engine and executor target `net10.0`** and ship a **NativeAOT executor image**
(fast cold start, low memory for ephemeral pods); the vectorized interpreter runs
under AOT and the optional codegen tier (ADR-0001) is dead-code-eliminated via
feature switches. **Public-facing libraries multi-target `net8.0;net10.0`** for
broad adoption and are kept **trim/AOT-annotation-clean** so AOT consumers (and the
AOT executor image) build cleanly. Owned by `dotnet-library-platform-engineer`
(packaging/TFM/trim hygiene) with `dotnet-runtime-performance-engineer` (AOT runtime
trade-offs).

## Gating / dependencies

Owned by `dotnet-library-platform-engineer`; directly determines whether the
ADR-0001 codegen tier ships and how `dotnet-runtime-performance-engineer` tunes
the runtime.
