# .NET Vectorized Columnar Compute Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

DeltaSharp needs a dedicated .NET Vectorized Columnar Compute Engineer because its execution core cannot delegate vectorized analytics to Apache Arrow C#. Arrow C# supplies columnar containers, aligned buffers, validity bitmaps, IPC, and interop, but explicitly does **not** provide a compute/kernel abstraction.[^1] A fully native .NET Spark-equivalent engine therefore needs its own kernels for aggregates, predicates, filters, comparisons, joins, and aggregations over the internal `ColumnBatch`/`ColumnVector` representation.[^2]

This role owns the default vectorized interpreter backend described by ADR-0001: AOT-clean, always present, batch-at-a-time, and the correctness ground truth for any optional compiled tier.[^3] It also owns the mutable, selection-vector-aware `ColumnVector` abstraction described by ADR-0002: Arrow-backed first for velocity and interop, custom off-heap later for hot operators, with Arrow retained at the edges.[^4]

The specialization is distinct from general CLR/runtime performance. `dotnet-runtime-performance-engineer` owns the SIMD/unsafe toolbox, GC/JIT/AOT behavior, runtime tuning, and optional codegen tier mechanics. This role owns the columnar algorithms that use that toolbox: selection vectors, validity-bitmap semantics, dictionary peeling, late materialization, encoding fast paths, kernel contracts, and parity-tested vectorized loops.[^5]

A world-class practitioner in this seat thinks in batches, spans, bitmaps, masks, encodings, and cache lines. They can design scalar references and SIMD fast paths, keep null semantics correct, avoid accidental row materialization, and produce benchmarkable kernels whose behavior is reproducible across hardware widths and across interpreter/compiled execution modes.[^6]

---

## Evidence base

- Apache Arrow C# / arrow-dotnet README and source: `RecordBatch`, `ArrayData`, `ArrowBuffer`, `PrimitiveArray<T>.Values`, validity bitmaps, `BitUtility`, 64-byte aligned `NativeMemoryAllocator`, and the explicit absence of a compute/kernel API.[^1]
- ADR-0001: DeltaSharp's accepted execution strategy is a pluggable backend with an AOT-safe `InterpretedVectorizedBackend` as default/reference and optional `Expression.Compile()` intra-operator fusion when dynamic code is supported.[^3]
- ADR-0002: DeltaSharp's accepted in-memory format is Arrow-compatible but custom, sequenced Arrow-first, with mutable `ColumnBatch`/`ColumnVector` abstractions and selection-vector-aware execution.[^4]
- Velox documentation: vectors, encodings, `SelectivityVector`, expression evaluation, dictionary peeling, memoization, and flat-no-nulls fast paths.[^7]
- DuckDB documentation: columnar-vectorized execution as a core design for analytical query processing.[^8]
- DataFusion documentation/repository: Arrow-native, columnar, streaming, vectorized execution; useful contrast because Rust Arrow has a mature compute crate while Arrow C# does not.[^9]
- .NET SIMD and intrinsics documentation: `Vector<T>`, `Vector128/256/512<T>`, x86 intrinsics, Arm `AdvSimd`, and hardware capability guards.[^10]
- `TensorPrimitives` and .NET numerics APIs: useful BCL precedent for vectorized span-based primitives and hardware-accelerated reductions.[^11]
- Parquet.Net and Microsoft.Data.Analysis: practical .NET bridge points from Parquet/Arrow-style buffers into typed columnar representations.[^12]
- `System.Linq.Expressions` and NativeAOT documentation: `Expression.Compile()`/`DynamicMethod` are dynamic-code features, so codegen must remain optional and the vectorized interpreter must stand alone.[^13]
- FrozenArrow: a .NET reference point for SIMD predicate evaluation over Arrow columns with selection bitmaps.[^14]

---

## Explanation

### Why this role exists

DeltaSharp is a native .NET engine, not a proxy into JVM Spark. Its scan, filter, project, aggregate, join, shuffle, and write paths need real physical execution inside .NET processes. Columnar execution is the practical default: it preserves Spark-like lazy/eager semantics at the plan level while processing executor-local batches through tight loops over typed memory.

Arrow C# is a strong interop substrate but not a compute engine. It can represent primitive arrays, validity bitmaps, dictionaries, IPC payloads, and Arrow Flight data, but it does not ship the equivalent of Arrow Rust/DataFusion compute kernels. If DeltaSharp operators bind directly to Arrow arrays, they inherit immutability and miss the mutable scratch, selection-vector, and late-materialization behavior expected of modern vectorized engines. ADR-0002 therefore inserts a DeltaSharp-owned `ColumnBatch`/`ColumnVector` abstraction between operators and Arrow.

ADR-0001 makes the need sharper: the default backend is a vectorized interpreter, not WholeStageCodegen. That means DeltaSharp's baseline performance and correctness rest on reusable kernels that operate over batches of roughly 1k-8k rows, use SIMD where profitable, and handle nulls, dictionaries, and selection vectors without per-row object overhead. Optional `Expression.Compile()` fusion can improve hot predicates on JIT runtimes, but it must never become the correctness foundation.

### Boundaries

- **vs. `delta-storage-format-engineer`**: Storage owns on-disk Delta/Parquet decode, physical file layout, statistics, checkpoints, ACID write protocol, and data skipping. This role owns the **in-memory** `ColumnBatch`/`ColumnVector` representation and SIMD operators over it.
- **vs. `query-execution-engine-engineer`**: Query execution owns plan/operator semantics, optimizer decisions, physical operator choice, scheduling, shuffle boundaries, and cache semantics. This role owns the physical kernels those operators invoke.
- **vs. `dotnet-runtime-performance-engineer`**: Runtime performance owns the SIMD/unsafe toolbox, GC/JIT/AOT behavior, runtime tuning, and optional codegen tier. This role owns the columnar-algorithm design that uses those tools.
- **vs. `performance-benchmarking-engineer`**: Benchmarking owns methodology, harnesses, regression gates, result storage, and statistical claims. This role supplies benchmarkable kernels, dimensions, scalar references, and expected invariants.
- **vs. `reliability-test-chaos-engineer`**: Reliability owns correctness oracles and fault-injection strategy. This role supplies scalar references, randomized batch invariants, and edge cases for compute parity.

---

## Required knowledge domains

### 1. Arrow columnar memory model & ColumnVector

This role must understand Arrow's logical and physical layout: `RecordBatch`, `ArrayData`, `ArrowBuffer`, typed primitive value buffers, offsets, null counts, validity bitmap buffers, dictionary arrays, boolean bit-packing, and zero-copy slicing.[^1] The important distinction for DeltaSharp is that Arrow is an edge format and initial backing store, while `ColumnVector` is the internal contract operators bind to.[^4]

A good `ColumnVector` design makes length, offset, type, nullability, dictionary state, selection vector, and memory ownership explicit. It supports mutable output buffers and scratch state without leaking Arrow immutability into hot operators. It can wrap Arrow-backed arrays initially and later wrap custom off-heap vectors without changing operator code.

The engineer should define invariants for non-zero Arrow offsets, sliced arrays, batch length, selected row count, output vector capacity, null bitmap alignment, and disposal/ownership. Kernel APIs should accept spans and bitmap views rather than high-level Arrow array objects whenever possible.

### 2. SIMD kernels with Vector<T>/Vector256/512/AdvSimd over ReadOnlySpan<T>

The role must be fluent in .NET's portable and hardware-specific vectorization options: `Vector<T>` for portable JIT-selected width; `Vector128<T>`, `Vector256<T>`, and `Vector512<T>` for explicit width; x86 intrinsics for AVX2/AVX-512; and Arm `AdvSimd` where appropriate.[^10] Kernels should operate over `ReadOnlySpan<T>` input and `Span<T>`/bitmap output, with scalar tails and capability checks.

Core kernel families include sum, min, max, count, count-non-null, comparisons, boolean predicate evaluation, predicate-to-bitmap, bitmap-to-selection, filter/selection, vectorized projection helpers, hash/build/probe helpers for joins, and aggregate update kernels. Every SIMD path needs a scalar reference, randomized equivalence tests, and clear overflow/NaN/ordering semantics where type-specific behavior matters.

The engineer should know when BCL primitives such as `TensorPrimitives` can be used directly, when they are insufficient because of null/selection semantics, and when custom vector loops are required.[^11] They should avoid hidden delegates, virtual dispatch, LINQ, boxing, or allocations inside inner loops.

### 3. Null-aware compute & validity bitmaps

Columnar analytics spends much of its complexity budget on nulls. This role must treat validity bitmaps as first-class inputs and outputs: a kernel may ignore them only on a proven flat-no-nulls fast path. Otherwise, it must apply bitmap offsets, popcount, null propagation rules, and selected-row semantics correctly.[^1]

Null-aware aggregates need explicit behavior for empty input, all-null input, skip-null reductions, null counts, and result validity. Comparison and predicate kernels need clear three-valued logic or engine-specific semantics supplied by `query-execution-engine-engineer`. Filter and selection kernels must define whether invalid rows are rejected, propagated, or evaluated according to predicate semantics.

The best implementations separate fast paths: no nulls/no selection; no nulls/selection; nulls/no selection; nulls/selection; dictionary-encoded variants. Branchless bitmap operations and word-at-a-time popcount loops are usually preferable to checking nulls row by row.

### 4. Selection vectors / dictionary peeling / late materialization

Selection vectors let operators carry row subsets without copying values. This role owns how predicate bitmaps become row-id selections, how selection vectors compose, when they should be compacted, and how downstream kernels consume them. Late materialization should avoid expanding or copying columns until an operator truly needs values.

Velox is the key reference: `SelectivityVector`, dictionary peeling, memoizing dictionary predicate results, and flat-no-nulls fast paths reduce repeated work when many rows share dictionary entries.[^7] DeltaSharp should adopt the same ideas where they fit its `ColumnVector` abstraction: peel dictionary layers for predicate evaluation, evaluate unique dictionary values once, then map results back through indices.

The role must define selection-vector width and ownership, stable ordering requirements, maximum batch lengths, behavior under repeated filtering, and when a dense selected range can be represented without an explicit index vector.

### 5. Encoding fast paths

Dictionary, run-length, boolean bit-packed, and future custom encodings can make kernels faster or much slower depending on whether the engine expands them prematurely. This role should recognize when to operate on encoded form, when to peel to an underlying vector, and when to materialize to a flat vector.

Dictionary fast paths are especially important for equality predicates, group-by keys, joins, and low-cardinality strings. RLE or repeated-value encodings matter for count, min/max, equality, and aggregations. Boolean bitmaps deserve separate treatment from byte-valued booleans because predicate outputs are naturally bitmaps.

Encoding support should be incremental and measurable. Each fast path must preserve semantics across nulls, offsets, selections, and dictionary nullability. If an encoding optimization is too complex to prove, the kernel should fall back to scalar or flat-vector behavior with a benchmark-backed TODO rather than risking silent corruption.

### 6. Intra-operator Expression.Compile fusion

ADR-0001 rejects WholeStageCodegen as the only execution strategy because dynamic code is incompatible with NativeAOT and because per-query runtime plans cannot be fully handled by source generators.[^3] However, it keeps optional intra-operator `Expression.Compile()` fusion for hot predicates or projections when `RuntimeFeature.IsDynamicCodeSupported` is true.[^13]

This role should understand where fusion helps: reducing interpreter overhead for repeated predicate/projection evaluation inside a single operator. It should also understand where fusion must stop: the vectorized interpreter remains the reference implementation and must be AOT-clean. Delegate caches, expression keys, and dynamic-code guards belong in collaboration with `dotnet-runtime-performance-engineer` and `dotnet-library-platform-engineer`.

A healthy fusion design keeps scalar/SIMD/interpreter behavior testable, uses the same null and selection semantics, and has a deterministic fallback when dynamic code is disabled.

### 7. Parquet→Arrow/ColumnVector bridge

DeltaSharp's storage layer reads Parquet and Delta data; this role receives in-memory batches. The bridge must preserve type width, null bitmaps, offsets, dictionary information, and buffer ownership while avoiding unnecessary copies. Parquet.Net `RawColumnData<T>` and hardware-accelerated decoders, Arrow arrays, and Microsoft.Data.Analysis Arrow bridges provide useful .NET precedent.[^12]

The boundary should be contractual. `delta-storage-format-engineer` owns decoding and statistics; this role specifies the shape of input batches that kernels need: contiguous value spans, validity bitmap access, dictionary metadata, selected row contracts, batch size targets, and ownership/disposal rules.

---

## Expected behaviors

- Designs kernel APIs from buffer, bitmap, selection, type, and ownership invariants before writing optimized loops.
- Keeps Arrow-specific types out of operator hot paths except at explicit adapters.
- Writes scalar references and randomized parity tests before trusting SIMD paths.
- Splits no-null, nullable, selected, and dictionary paths deliberately instead of forcing one slow universal loop.
- Treats null handling, offsets, and tail rows as correctness-critical, not as afterthoughts.
- Rejects per-row object allocation, LINQ, boxing, reflection, virtual dispatch, and delegate calls in inner loops.
- Uses hardware intrinsics only behind capability checks and with portable fallbacks.
- Collaborates with benchmarking on realistic dimensions: batch size, type width, selectivity, null density, dictionary cardinality, and hardware width.
- Collaborates with reliability on parity oracles across scalar/SIMD/interpreter/compiled paths.
- Documents when a performance issue belongs to storage decode, query semantics, runtime tuning, or benchmark methodology.

---

## Traits and attributes

- **Columnar systems intuition**: Thinks in vectors, masks, selections, encodings, and cache locality rather than rows and objects.
- **Correctness paranoia**: Assumes SIMD bugs hide in tails, offsets, nulls, NaNs, overflow, all-null batches, and dictionary edge cases.
- **Mechanical sympathy**: Understands contiguous spans, alignment, branch prediction, cache behavior, vector width, and allocation pressure.
- **AOT discipline**: Keeps the default interpreter independent of dynamic code and treats codegen as optional.
- **Benchmark humility**: Requires measured before/after evidence and representative dimensions before accepting a fast path.
- **API restraint**: Designs small, composable kernel contracts rather than broad abstractions that hide hot-loop cost.
- **Cross-role clarity**: Hands off semantics, storage decode, runtime tuning, and benchmark claims to the right owners.
- **Documentation precision**: Writes invariants and edge-case behavior clearly enough for future kernel authors to avoid regressions.

---

## Anti-patterns

- Binding physical operators directly to `Apache.Arrow.PrimitiveArray<T>` instead of DeltaSharp `ColumnVector`.
- Treating Arrow C# as if it had a mature compute layer comparable to Rust Arrow/DataFusion.
- Expanding dictionaries or materializing rows before proving it is necessary.
- Running one generic nullable selected dictionary-aware loop for every case and calling it vectorized.
- Ignoring bitmap offsets or assuming validity bitmaps are byte-aligned with value spans.
- Producing predicate results as `bool[]` when a bitmap or selection vector is the required downstream form.
- Using LINQ, boxing, reflection, interface dispatch, or heap allocation inside kernels.
- Adding hardware-specific intrinsics without scalar fallbacks, capability guards, or CI coverage on unsupported hardware.
- Optimizing for a single batch size, selectivity, or no-null synthetic benchmark.
- Letting optional `Expression.Compile()` fusion become required for correctness or NativeAOT-incompatible by default.
- Reporting throughput without allocation, null-density, selectivity, and hardware context.

---

## What This Means for DeltaSharp

**The interpreter is a product surface.** ADR-0001 makes the vectorized interpreter the default and correctness reference, so kernel quality directly determines DeltaSharp's early credibility. The interpreter must be fast enough to be useful, simple enough to debug, and deterministic enough to validate optional codegen.

**`ColumnVector` is the engine's hot-path contract.** ADR-0002's mutable, selection-vector-aware abstraction is where Arrow interop and custom engine needs meet. If this abstraction leaks Arrow immutability or hides ownership, every operator will pay for it.

**Arrow at the edges is not Arrow in the core.** DeltaSharp can use Arrow IPC, Flight, C Data Interface, and Arrow-backed buffers while still owning compute. That separation is the key to starting quickly without locking hot operators into a weak compute substrate.

**Nulls and selections are not optional features.** Real analytical workloads include nullable columns, filters, dictionaries, and late materialization. Kernels that are only fast on dense, no-null, unselected arrays are baseline prototypes, not finished engine components.

**Benchmark and reliability work must start at the kernel level.** Kernel microbenchmarks and scalar/SIMD parity tests should exist before full SQL suites, because later query failures will otherwise be impossible to localize.

---

## Confidence Assessment

| Area | Maturity | Notes |
|------|----------|-------|
| Arrow C# container and memory model | **Mature enough for interop** | Record batches, buffers, primitive spans, validity bitmaps, and aligned allocation exist; compute kernels are explicitly absent. |
| DeltaSharp ADR alignment | **High** | ADR-0001 and ADR-0002 directly assign this role's core surfaces: interpreter kernels and `ColumnVector`. |
| SIMD span-based kernels in .NET | **Mature but expert-only** | `Vector<T>` and hardware intrinsics are production-grade, but null/selection/dictionary semantics require custom design. |
| Null-aware vectorized compute | **Evolving in DeltaSharp** | Peer engines provide patterns; DeltaSharp must define and test its own bitmap contracts. |
| Selection vectors and dictionary peeling | **Mature in peer engines** | Velox provides strong precedent; adaptation to .NET `ColumnVector` is project-specific. |
| Intra-operator expression fusion | **Medium** | Useful on JIT runtimes, but must remain optional because of NativeAOT constraints. |
| Parquet→Arrow/ColumnVector bridge | **Evolving** | Parquet.Net and Microsoft.Data.Analysis provide references; DeltaSharp-specific ownership and encoding contracts remain to be built. |
| Benchmark and parity practices | **High importance / early implementation** | Methodology exists, but DeltaSharp must create kernel-specific harnesses and randomized oracles. |

---

## Footnotes

[^1]: Apache Arrow C# / arrow-dotnet README lists "Compute — There is currently no API available for a compute / kernel abstraction." Arrow C# source includes `RecordBatch`, `ArrayData`, `ArrowBuffer`, `PrimitiveArray<T>.Values`, `NullBitmapBuffer`, `BitUtility`, and a 64-byte aligned `NativeMemoryAllocator`.

[^2]: Research report, "Seat 2 — `dotnet-vectorized-columnar-compute-engineer`," explains that DeltaSharp must build the entire kernel layer because Arrow C# lacks compute: sum/min/max/count, predicate bitmaps, filter/selection, comparisons, joins, and aggregations over columnar spans.

[^3]: `docs/adr/0001-execution-strategy.md` accepts a pluggable execution backend with `InterpretedVectorizedBackend` as default/reference, optional `CompiledBackend`, batch-at-a-time SIMD kernels, NativeAOT compatibility, and intra-operator `Expression.Compile()` fusion as the first codegen increment.

[^4]: `docs/adr/0002-columnar-batch-format.md` accepts an Arrow-compatible custom format, sequenced Arrow-first, with mutable selection-vector-aware `ColumnBatch`/`ColumnVector` abstractions, Arrow-backed initial implementation, custom off-heap later, and Arrow at the edges.

[^5]: Research report boundaries for Seat 2 distinguish columnar-algorithm ownership from `dotnet-runtime-performance-engineer` runtime/SIMD toolbox ownership, `delta-storage-format-engineer` on-disk decode/statistics ownership, and `query-execution-engine-engineer` plan/operator semantics ownership.

[^6]: `docs/engineering/design/engine-architecture.md` maps `dotnet-vectorized-columnar-compute-engineer` to `ColumnVector`, SIMD kernels, selection vectors, and the interpreter backend, and describes the query path from execution backend to `ColumnBatch`/`ColumnVector`.

[^7]: Velox documentation for vectors and expression evaluation describes `SelectivityVector`, dictionary peeling, memoization, encodings, and flat-no-nulls fast paths.

[^8]: DuckDB documentation describes DuckDB as a columnar-vectorized query execution engine, a peer model for batch-oriented native analytics execution.

[^9]: Apache DataFusion describes a columnar, streaming, multi-threaded, vectorized execution engine built around Arrow; it is a useful contrast because Rust Arrow includes compute capabilities that Arrow C# lacks.

[^10]: Microsoft .NET SIMD documentation covers `Vector<T>` and `System.Runtime.Intrinsics`, including `Vector128<T>`, `Vector256<T>`, `Vector512<T>`, x86 intrinsics, and Arm `AdvSimd`/SVE surfaces.

[^11]: .NET `System.Numerics.Tensors.TensorPrimitives` provides hardware-accelerated span-oriented numerical primitives that can inform reductions and comparisons, though DeltaSharp kernels still need null, selection, and dictionary semantics.

[^12]: Parquet.Net exposes `RawColumnData<T>` and hardware-accelerated decoder paths; Microsoft.Data.Analysis `PrimitiveDataFrameColumn` includes Arrow array bridge behavior useful as .NET production precedent.

[^13]: `dotnet/runtime` `LambdaExpression` marks IL compilation paths with dynamic-code guards and exposes `Compile(preferInterpretation)`; `DelegateHelpers` uses `DynamicMethod` and requires dynamic code. Microsoft NativeAOT documentation explains why these paths cannot be default dependencies for an AOT executor.

[^14]: `JorgeCandeias/FrozenArrow` is a .NET reference for SIMD predicate evaluation over Arrow columns with selection bitmaps.
