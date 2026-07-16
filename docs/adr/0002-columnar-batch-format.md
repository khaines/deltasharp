# ADR-0002: In-memory columnar batch format — Arrow-compatible custom (Arrow-first)

- **Status:** Accepted
- **Date:** 2026-06-26
- **Deciders:** @khaines
- **Related:** ADR-0001 (execution), ADR-0003 (transport), ADR-0004 (shuffle), `docs/engineering/design/engine-architecture.md`

## Context

Both execution backends (ADR-0001), the shuffle path (ADR-0004), the Arrow Flight
data plane (ADR-0003), and the Parquet/Delta bridge all sit on an **in-memory
columnar batch representation**. The format choice is load-bearing.

Options span a spectrum: adopt **Apache Arrow C# (`Apache.Arrow`)** directly;
design a **fully custom off-heap columnar format**; or a **custom-but-Arrow-compatible**
format. Key facts:

- Arrow C# provides off-heap, 64-byte-aligned buffers (`PrimitiveArray<T>.Values`
  is a ready `ReadOnlySpan<T>`), validity bitmaps, IPC, the C Data Interface
  (zero-copy cross-runtime), and Arrow Flight — but **explicitly ships no
  compute/kernel layer** ("There is currently no API available for a compute /
  kernel abstraction"), and has gaps (StringView/`>2 GiB`/Run-End incomplete) plus
  an over-allocation strategy that wastes memory on small buffers.
- **Arrow arrays are immutable.** High-performance vectorized engines need
  **mutable column vectors + selection vectors + late materialization** — which is
  why **Velox and DuckDB built their own vectors** ("similar to Arrow but more
  encodings and a different string layout"), while **DataFusion went Arrow-native**
  on the far more mature Rust `arrow-rs` (which *has* a compute crate).

We must build all kernels ourselves regardless of format (Arrow C# has none).

## Decision

Adopt an **Arrow-compatible custom format, sequenced "Arrow-first":**

1. Define a thin internal **`ColumnBatch` / `ColumnVector`** abstraction —
   **mutable** and **selection-vector-aware** — that the operators and kernels bind
   to. Operators must **not** bind directly to `Apache.Arrow.PrimitiveArray<T>`.
2. **Back `ColumnVector` with Arrow C# initially** to get Parquet.Net bridging,
   Arrow IPC, the C Data Interface, and Arrow Flight transport "for free" and reach
   a working engine fast.
3. **Swap in custom off-heap vectors for hot operators later** without rewriting
   operators, keeping **Arrow at the edges** (Parquet read, shuffle/Flight, Python
   interop).

## Consequences

### Positive

- Best compute ergonomics (mutable scratch, selection vectors, late
  materialization) while retaining Arrow ecosystem interop where it pays.
- Matches the project pattern: an abstraction with swappable implementations
  (cf. ADR-0001's execution backends).
- Avoids a risky big-bang custom format design up front.

### Negative / costs

- A mapping/abstraction layer to maintain; risk of it becoming a perf tax if leaky.
- Arrow C#'s immutability and maturity gaps must be managed at the boundary.

### Follow-ups and sequencing

- Phase 1: Arrow-backed `ColumnVector`; kernels operate over `ReadOnlySpan<T>` +
  validity bitmaps.
- Phase 2: custom off-heap `ColumnVector` (mutable, our string layout, selection
  vectors) for hot operators; Arrow conversion only at the edges.
- Memory model (off-heap `NativeMemory` vs GC-heap + `ArrayPool`) is tracked as an
  open decision; Arrow's default off-heap 64-byte-aligned allocator is the early
  baseline.

### Nullability enforcement in nested vectors (#570 / #577 — decided)

The nested reference vectors (`StructColumnVector`, `ListColumnVector`, `MapColumnVector`)
enforce only **structural** type invariants; **flag-based** nullability is treated as
**advisory**, consistent with DeltaSharp's Spark-parity convention (a null in a non-nullable
field is *represented*, not rejected — see `LocalRelationBatches`, which encodes a null in a
non-nullable field as SQL NULL).

- **Enforced (structural):** `MapType` keys are always non-null — the type has no key-null
  flag — so a null key in the *referenced* offset range fails closed at construction.
- **Advisory (not enforced):** `MapType.ValueContainsNull=false`, `ArrayType.ContainsNull=false`,
  and non-nullable struct fields. A null in such a position is represented faithfully. Turning
  these into hard fail-closed constraints would be a deliberate, **uniform** future change
  (applied across nested *and* the flat / `createDataFrame` ingestion path), not something the
  representation layer imposes unilaterally.

## Alternatives considered

- **Pure Arrow C# as the engine format:** fastest start + best interop, but
  immutability fights vectorized execution and the C# library is the least mature
  link (no compute, StringView/large-array gaps, over-allocation). Rejected as the
  internal hot-path representation; retained at the edges.
- **Pure custom off-heap format:** maximal control, but forfeits free interop and
  pays the largest build cost with high early-design risk. Rejected as the starting
  point.

## References

- Apache Arrow C# (`apache/arrow` `csharp/`, `apache/arrow-dotnet`) — `RecordBatch`,
  `ArrayData`, `ArrowBuffer`, `PrimitiveArray<T>.Values`, `BitUtility`,
  `NativeMemoryAllocator` (64-byte aligned), C Data Interface; README "Not
  Implemented: Compute".
- Velox vectors — https://facebookincubator.github.io/velox/develop/vectors.html.
- DuckDB vectorized execution — https://duckdb.org/why_duckdb.
- DataFusion (Arrow-native) — https://github.com/apache/datafusion.
- Parquet.Net (`aloneguid/parquet-dotnet`) — `RawColumnData<T>`, AVX2/`Vector256`
  decoders; `dotnet/machinelearning` `Microsoft.Data.Analysis` Arrow bridge.
