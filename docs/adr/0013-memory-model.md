# ADR-0013: Memory model for in-memory batches

- **Status:** Accepted
- **Date:** 2026-06-27
- **Deciders:** @khaines
- **Related:** ADR-0002 (columnar format), `docs/engineering/design/engine-architecture.md`

## Context

In-memory columnar batches and execution scratch need a memory strategy. This
intersects ADR-0002 (Arrow C# already allocates off-heap, 64-byte aligned). We
must decide the default and the unified memory management policy.

## Options under consideration

- **Off-heap** (`NativeMemory`, 64-byte aligned; GC-invisible; deterministic
  reclamation; avoids LOH/GC pauses for large buffers) — leaning here.
- **GC-heap + `ArrayPool<T>`** (simpler, but LOH/gen2 pressure for large batches).
- A **unified memory manager** (execution vs storage pools, per-task budgets,
  spill-to-disk/object-store under pressure) — Spark's `UnifiedMemoryManager`.

## Decision

**Off-heap** (`NativeMemory`, 64-byte aligned) for columnar batches and the binary
row buffers (ADR-0008), consistent with Arrow C#'s default off-heap allocator
(ADR-0002) — GC-invisible, deterministic reclamation, SIMD-aligned, no LOH/gen2
pauses on large buffers. A **unified memory manager** governs execution vs storage
pools with per-task budgets and **spills to local disk / object-store** under
pressure (ties to ADR-0004). GC-heap + `ArrayPool<T>` is used only for small,
short-lived scratch. Owned by `dotnet-runtime-performance-engineer` with
`dotnet-vectorized-columnar-compute-engineer`.

## Gating / dependencies

Owned by `dotnet-runtime-performance-engineer` and
`dotnet-vectorized-columnar-compute-engineer`; spill ties to ADR-0004.
