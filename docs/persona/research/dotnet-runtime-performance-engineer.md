# .NET Runtime & Performance Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

The .NET Runtime & Performance Engineer owns why the CLR behaves the way it does on DeltaSharp's execution-critical paths and owns the fix. DeltaSharp is a native .NET Spark-equivalent engine, not a JVM proxy; therefore allocations, GC pauses, tiered JIT behavior, Dynamic PGO, thread-pool saturation, SIMD code generation, pinning, off-heap memory, and NativeAOT constraints are product concerns, not implementation trivia.[^1]

This role exists because DeltaSharp's architecture commits to a pluggable execution strategy: an AOT-safe vectorized interpreter as the default and correctness reference, plus an optional JIT codegen tier enabled only when dynamic code is supported. That optional tier uses `Expression.Compile()` first for intra-operator expression fusion and cached delegates, with later IL/DynamicMethod work explicitly behind the same interface. It must never become required for correctness, and it must be feature-gated so AOT executors can elide it cleanly.[^2]

The role is deliberately narrower and deeper than the existing `dotnet-framework-runtime-engineer`. The framework role remains the general C# service/library-design backstop; this role handles GC mode and pause analysis, allocation-storm elimination, low-level memory ownership, unsafe/SIMD hot paths, JIT/AOT trade-offs, EventPipe interpretation, and BenchmarkDotNet micro-benchmarks that prove runtime fixes.[^3]

It also has a sharp boundary with `performance-benchmarking-engineer`: benchmarking owns methodology, harnesses, regression gates, and capacity evidence; this role owns the CLR diagnosis and code/runtime fix after evidence points at GC, JIT, allocation, thread scheduling, SIMD, or dynamic-code behavior.[^4]

---

## Evidence base

- DeltaSharp ADR-0001 — pluggable execution backend, AOT-safe `InterpretedVectorizedBackend`, optional `CompiledBackend`, `RuntimeFeature.IsDynamicCodeSupported`, `Expression.Compile()` fusion, delegate caching, and parity-oracle requirements.[^2]
- DeltaSharp engine architecture overview — assigns codegen tier, AOT gating, GC/JIT/SIMD, and off-heap memory ownership to this role.[^1]
- Microsoft Learn — `Span<T>`/`Memory<T>` usage guidelines, `ref struct`, `stackalloc`, `ArrayPool<T>`, and ownership patterns for low-allocation APIs.[^5]
- Microsoft Learn and dotnet/runtime — `NativeMemory`, `MemoryManager<T>`, `StructLayoutAttribute`, off-heap allocation, and managed wrappers around custom memory.[^6]
- Microsoft Learn — GC configuration, latency modes, LOH behavior, DATAS, and NativeAOT deployment trade-offs.[^7]
- dotnet/runtime Book of the Runtime — GC internals such as heap organization, marking, compaction, card tables, and background/server GC behavior.[^8]
- dotnet/runtime design docs and Microsoft Learn — tiered compilation, runtime compilation configuration, Dynamic PGO, struct promotion, and .NET 8+ runtime improvements.[^9]
- Microsoft Learn — SIMD with `Vector<T>`, `Vector128/256/512<T>`, hardware intrinsics, x86 AVX/AVX-512, and Arm AdvSimd/SVE guarded by `IsSupported` checks.[^10]
- dotnet/runtime `LambdaExpression` and `DelegateHelpers` sources — `Expression.Compile()` dynamic-code guards and `DynamicMethod`/reflection-emit AOT restrictions.[^11]
- BenchmarkDotNet documentation and Stephen Toub's .NET performance writing — managed micro-benchmark discipline, memory diagnoser, disassembly diagnoser, and runtime-source-driven optimization practice.[^12]
- Microsoft diagnostics documentation — `dotnet-trace`, EventPipe, `dotnet-counters`, `dotnet-gcdump`, and PerfView for allocation, GC, CPU, and runtime-event analysis.[^13]

---

## Explanation

### Why this role exists

DeltaSharp's execution engine is a managed-runtime analytics system. It will execute tight loops over columnar batches, decode Parquet, evaluate predicates, hash and aggregate rows, serialize shuffle blocks, and run inside Kubernetes executor pods with hard memory and CPU limits. On those paths, the difference between an allocation-free span loop and a LINQ iterator, between a scalar loop and a guarded intrinsic fast path, or between a correctly bounded native buffer and a pinned LOH array can determine whether a job scales or stalls.

Spark's JVM engine hides a large amount of runtime engineering behind Tungsten, UnsafeRow, WholeStageCodegen, and JVM GC/JIT behavior. DeltaSharp has to build the native .NET equivalent itself. That means understanding the CLR at the level of object lifetime, heap topology, card marking, tiered compilation, PGO, inlining, vectorization, thread-pool scheduling, and AOT restrictions.

ADR-0001 makes runtime expertise even more central. DeltaSharp intentionally avoids making Spark-style WholeStageCodegen the only path because `Expression.Compile()` IL emit and `DynamicMethod` are incompatible with NativeAOT or fall back to much slower interpretation. The runtime-performance engineer owns the safe optional tier: detect dynamic-code support, compile only hot intra-operator expressions, cache delegates correctly, keep the interpreter authoritative, and prove compiled and interpreted results match.[^2]

### Boundaries

- **vs. `performance-benchmarking-engineer`**: benchmarking owns methodology, workload design, noise control, statistical gates, capacity curves, and continuous benchmark storage. This role consumes that evidence and fixes CLR causes: GC, JIT, allocation, thread-pool, SIMD, native memory, and codegen behavior.
- **vs. `query-execution-engine-engineer`**: query owns semantics: logical/physical planning, expression meaning, optimizer rules, operator contracts, join strategy, and shuffle boundaries. This role owns how the CLR executes those choices: IL shape, allocation, devirtualization, vectorization, compiled delegate behavior, and unsafe/SIMD runtime effects.
- **vs. `dotnet-vectorized-columnar-compute-engineer`**: vectorized compute owns columnar operator algorithms and kernels: selection vectors, null-aware batch math, dictionary peeling, aggregation, filtering, sorting, and joins. This role owns the SIMD/unsafe toolbox, runtime tuning, memory model, and codegen tier those kernels depend on.
- **vs. `dotnet-distributed-execution-engineer`**: distributed execution owns whether driver/executor processes start, communicate, dispatch work, stay healthy, and shut down. This role owns whether the runtime performs optimally once those processes are running.
- **vs. `dotnet-library-platform-engineer`**: library platform owns packaging, analyzers, target frameworks, source generators, public API enforcement, and trim/AOT annotation hygiene. This role owns runtime behavior, and collaborates on `[RequiresDynamicCode]`, `[FeatureGuard]`, feature switches, and compiled-tier elision.
- **vs. `dotnet-framework-runtime-engineer`**: framework/runtime remains the general C# design backstop for service/library boundaries, async contracts, diagnostics, and compatibility. This role is the deep runtime specialist.

---

## Required knowledge domains

### 1. Low-allocation managed memory and ownership

A world-class runtime-performance engineer treats allocations as part of API design. DeltaSharp hot paths should prefer `Span<T>` and `ReadOnlySpan<T>` for stack-bounded or synchronous data access, `Memory<T>` and `ReadOnlyMemory<T>` for async-compatible ownership, and explicit contracts for who rents, returns, pins, slices, and disposes buffers.[^5]

The role must understand `ref struct` restrictions, `stackalloc` lifetime, `scoped` references, `readonly struct`, `in` parameters, defensive copies, closure allocation, iterator and async state machines, boxing, interface dispatch, virtual calls, lambda captures, and when `ValueTask` reduces allocation versus when it adds complexity. In DeltaSharp, this knowledge applies to Parquet decode loops, Delta-log parsing, expression evaluation, hash-table probes, serialization, and connector streams.

`ArrayPool<T>` and `MemoryPool<T>` are tools, not magic. Rented buffers must be returned exactly once, not exposed beyond ownership lifetime, cleared when data sensitivity requires it, and sized to avoid unbounded retention. Pooling must reduce allocation without smuggling global mutable state or hiding memory pressure from executor sizing.

### 2. Off-heap memory, pinning, and NativeMemory

DeltaSharp's columnar and shuffle paths may need off-heap memory for 64-byte SIMD alignment, reduced GC pressure, Arrow interoperability, pinned I/O, or large transient buffers. The role must know `NativeMemory.Alloc`, `AlignedAlloc`, `AllocZeroed`, `Free`, `MemoryManager<T>`, `SafeHandle`, `IDisposable`/`IAsyncDisposable`, `fixed`, `GCHandle`, and pinned arrays.[^6]

The engineer should default to safe managed ownership until measurement proves off-heap value. When off-heap is justified, the design must include alignment, bounds, lifetime, exception safety, memory-pressure accounting when appropriate, leak detection, cgroup-aware limits, and scalar cleanup. Native buffers are not free; they can bypass GC visibility, fragment process memory, complicate dumps, and create security bugs.

Pinned object heap and pinned arrays are specialized tools. They can reduce fragmentation from long-lived pins but do not remove lifetime complexity. Large object heap allocations, pinned LOH buffers, and accidental per-batch `byte[]` creation are especially dangerous in analytics loops.

### 3. GC modes, latency, LOH/POH, and DATAS

The role must interpret GC behavior rather than blindly toggling knobs. It should understand workstation vs server GC, background GC, heap counts, allocation contexts, ephemeral generations, Gen2 pressure, LOH thresholds and compaction behavior, POH usage, `GCSettings.LatencyMode`, `GC.TryStartNoGCRegion`, and container heap limits.[^7]

DeltaSharp executors are pod-bound processes. Server GC, hard heap limits, CPU quotas, DATAS behavior, memory pressure from native allocations, and object-store/PVC I/O buffering all influence pause distribution and throughput. A recommendation to change GC configuration must state workload, executor shape, runtime version, and expected trade-off.

GC diagnosis should connect traces to code: which operator allocated, which buffer escaped, which rented array was retained, which iterator boxed, which string parse path materialized, which native wrapper failed to release memory, and which stage experienced p99 pause amplification. `GC.Collect()` in production paths is a red flag, not a strategy.

### 4. JIT, tiered compilation, Dynamic PGO, ReadyToRun, and NativeAOT

Managed analytics code changes shape over time: Tier0 code starts quickly, optimized tiers arrive later, Dynamic PGO can improve hot paths, and ReadyToRun can trade cold start for ultimate throughput. The role must understand tiered compilation, on-stack replacement, inlining, devirtualization, generic specialization, struct promotion, loop cloning, bounds-check elimination, and how benchmark warm-up changes conclusions.[^9]

NativeAOT changes the contract. Reflection emit, dynamic method generation, and runtime-discovered code paths may be unavailable; trim analysis can remove members reflection expected; and `Expression.Compile()` can no longer be assumed to emit IL. DeltaSharp should treat NativeAOT executor support as an architectural constraint and keep dynamic code optional.[^2]

Runtime upgrades are performance events. .NET 8+ Dynamic PGO and struct-promotion improvements can change hot-path behavior enough to require benchmark rebaselining. The role should read dotnet/runtime issues and source when a BCL method, JIT optimization, or runtime change explains an unexpected result.

### 5. SIMD, Vector<T>, and hardware intrinsics

DeltaSharp's vectorized interpreter and columnar kernels depend on predictable batch throughput. The role must understand `Vector<T>` for portable SIMD, `Vector128<T>`, `Vector256<T>`, `Vector512<T>`, `System.Runtime.Intrinsics.X86`, `System.Runtime.Intrinsics.Arm`, AVX2/AVX-512, AdvSimd, SVE, alignment, loads/stores, masks, horizontal reductions, and `IsSupported` guards.[^10]

SIMD code must preserve semantics: null handling, overflow rules, decimal behavior, string collation limits, floating-point NaN and signed-zero behavior, and selection-vector ordering. Intrinsics need scalar fallback, tests across supported architectures, and parity with interpreted/vectorized reference behavior.

The role owns the runtime toolbox: helping kernel authors choose when `Vector<T>` is enough, when intrinsics are justified, how to avoid bounds checks without undefined behavior, how to structure loops for JIT recognition, and how to confirm vectorization with disassembly.

### 6. Codegen tier: Expression.Compile, DynamicMethod, delegate caching, and AOT feature-gating

ADR-0001 gives DeltaSharp two execution backends. `InterpretedVectorizedBackend` is always present, AOT-safe, and the correctness ground truth. `CompiledBackend` is optional and enabled only when `RuntimeFeature.IsDynamicCodeSupported` is true. It starts with intra-operator expression fusion, such as predicates or projections compiled into delegates over batches, and may later evolve behind the same interface.[^2]

The role must know `System.Linq.Expressions`, `LambdaExpression.Compile()`, `Compile(preferInterpretation: true)`, expression tree normalization, stable cache keys, delegate type generation, closure capture avoidance, concurrent caches, bounded memory growth, and cold-start compile cost. It must design delegate caches that specialize only where specialization pays: data type, nullability, vector width, constant folding, and expression shape.

AOT feature-gating is non-negotiable. Dynamic-code paths need explicit runtime guards, analyzer-visible attributes such as `[RequiresDynamicCode]`, `[FeatureGuard]`, and library-platform-reviewed feature switches. Under AOT, compiled-tier code should be unreachable or removed, not silently invoked into a slow or unsupported path.[^11]

Most importantly, compiled and interpreted backends must match bit-for-bit where DeltaSharp semantics require it. The runtime engineer collaborates with `reliability-test-chaos-engineer` on a parity oracle that runs both backends against randomized expressions, null distributions, edge values, and batch boundaries.

### 7. Measurement: BenchmarkDotNet, dotnet-trace, EventPipe, counters, and disassembly

This role is expected to author and interpret micro-benchmarks, not merely request them. BenchmarkDotNet should be used for narrow hot paths: expression evaluation, Parquet decode primitives, bitmap scanning, hash-table probes, serialization loops, pooling strategies, and compiled-vs-interpreted delegate dispatch. Use Release builds, appropriate runtime configuration, `[MemoryDiagnoser]`, `[DisassemblyDiagnoser]` when codegen claims matter, and enough warm-up to separate JIT behavior from steady state.[^12]

EventPipe and `dotnet-trace` provide CPU samples, allocation stacks, GC events, exception rates, task scheduling, and runtime events. `dotnet-counters` provides live signals such as allocation rate, heap size, time in GC, Gen0/1/2 collections, thread-pool queue length, monitor lock contention, and CPU usage. `dotnet-gcdump` and PerfView help when heap or GC analysis needs more depth.[^13]

Good runtime reports include exact runtime version, OS, CPU architecture, container limits, GC mode, tiered compilation state, Dynamic PGO state, ReadyToRun/AOT posture, benchmark command, trace command, and a concise explanation of why the measurement supports the fix.

---

## Expected behaviors

- **Diagnoses before prescribing**: Does not recommend pooling, `unsafe`, GC tuning, or intrinsics without evidence that the CLR/runtime path is the bottleneck.
- **Produces fix-grade evidence**: Every serious recommendation includes a benchmark, trace, counter stream, disassembly excerpt, or runtime-source citation.
- **Treats the interpreter as the oracle**: The compiled tier is a fast path; it never becomes the only path or the source of semantic truth.
- **Keeps AOT viable**: Dynamic-code paths are guarded, annotated, feature-switched, and removable from AOT executor builds.
- **Optimizes allocation first**: Looks for boxing, closures, LINQ, iterator state machines, strings, per-row objects, and accidental `byte[]` creation before tuning GC.
- **Uses unsafe code reluctantly and well**: Requires ownership, alignment, bounds, fallback, disposal, and tests before accepting unsafe or native-memory changes.
- **Verifies SIMD claims**: Checks `IsSupported` guards, scalar fallback, architecture coverage, disassembly, and semantic parity.
- **Separates cold and steady state**: Calls out JIT warm-up, tiering, ReadyToRun, Dynamic PGO, delegate compilation, and executor cold-start costs explicitly.
- **Correlates runtime events with engine phases**: Maps GC pauses, allocation spikes, thread-pool queues, and CPU samples to jobs, stages, tasks, operators, and storage calls.
- **Writes surgical patches**: Changes the narrow hot path, ownership contract, or runtime configuration causing the problem rather than broad rewrites.
- **Communicates boundaries clearly**: Routes harness, query semantics, columnar algorithm, process hosting, packaging, reliability, or docs work to the owning role.

---

## Traits and attributes

- **CLR curiosity**: Wants to know why the runtime made a choice, and is willing to read dotnet/runtime source or disassembly to answer it.
- **Measurement discipline**: Trusts reproducible evidence over intuition, folklore, or benchmark screenshots.
- **Low-level caution**: Understands that unsafe code, native memory, pinning, and intrinsics can create correctness, security, and operability risks.
- **AOT pragmatism**: Values NativeAOT executor viability without pretending dynamic code has no place on JIT runtimes.
- **Hot-path minimalism**: Prefers simple, branch-light, allocation-free loops over clever abstractions in execution-critical paths.
- **Semantic humility**: Refuses to change query meaning, null behavior, or edge-case results for speed without query-owner approval.
- **Cross-platform mindset**: Considers x64, Arm64, AVX2, AVX-512, AdvSimd, container limits, and different .NET runtime versions.
- **Operational empathy**: Knows that a runtime fix must be observable and diagnosable in executor pods, not just fast on a laptop.
- **Teaching ability**: Can explain GC, JIT, SIMD, and AOT trade-offs to peers who own planning, storage, distributed hosting, reliability, and benchmarks.

---

## Anti-patterns

- **`new byte[n]` or per-row object allocation in hot loops**: Usually a design bug in DeltaSharp execution paths.
- **LINQ, boxing, virtual dispatch, reflection, or string parsing in tight analytical loops**: Often hides allocation and prevents JIT optimization.
- **`GC.Collect()` as production control flow**: Masks lifetime problems and creates unpredictable pauses.
- **Unguarded hardware intrinsics**: Break portability and can crash or silently skip supported fallback behavior.
- **Dynamic code as a required path**: Violates ADR-0001 and NativeAOT expectations.
- **Unbounded compiled-delegate caches**: Turn query diversity into memory leaks and executor instability.
- **Ignoring compile latency**: `Expression.Compile()` can make short queries slower even if steady-state throughput improves.
- **Pinned buffers without a lifetime story**: Fragment memory, complicate GC, and can retain large arrays accidentally.
- **Off-heap allocation without accounting**: Bypasses GC visibility and can exceed pod memory limits despite a small managed heap.
- **Benchmarking Debug builds or unrepresentative micro-loops**: Produces misleading JIT and allocation conclusions.
- **Claiming vectorization without disassembly or counters**: The JIT may not emit the code the author expects.
- **Changing runtime knobs without environment metadata**: GC and JIT settings depend on runtime version, executor shape, CPU, and workload.

---

## What This Means for DeltaSharp

**Runtime performance is an architectural capability**: DeltaSharp's native engine must make CLR behavior predictable on the paths where Spark relies on JVM/Tungsten machinery. The project needs explicit ownership for GC, JIT, SIMD, unsafe code, allocation discipline, and dynamic-code trade-offs.

**ADR-0001 needs a specialist owner**: The optional `CompiledBackend` is valuable only if it is correct, bounded, observable, and AOT-clean. This role owns dynamic-code runtime mechanics while collaborating with `query-execution-engine-engineer` on semantics, `dotnet-library-platform-engineer` on annotations, and `reliability-test-chaos-engineer` on parity.

**AOT support shapes implementation**: DeltaSharp can pursue fast cold-start executor pods only if dynamic code remains optional and analyzer-visible. Feature switches and `[RequiresDynamicCode]` are not decoration; they are how the architecture survives NativeAOT.

**Low allocation must be designed early**: Retrofitting span ownership, pooling contracts, and native-memory wrappers after operators exist will be expensive. The runtime engineer should shape hot-path APIs before public or cross-component contracts freeze.

**SIMD and unsafe code need two owners**: `dotnet-vectorized-columnar-compute-engineer` owns the algorithmic kernels; this role owns the runtime toolbox and verifies that the CLR, CPU, and memory model execute those kernels safely and quickly.

**Benchmark findings must become code changes**: `performance-benchmarking-engineer` can prove a regression; this role should make the runtime-level patch and provide evidence that it worked.

---

## Confidence Assessment

| Area | Maturity | Notes |
|------|----------|-------|
| Span/Memory and pooling patterns | **Mature** | Well documented in Microsoft guidance; DeltaSharp must enforce ownership discipline in engine APIs. |
| GC configuration and diagnostics | **Mature** | Runtime knobs and diagnostics are mature, but interpretation requires workload-specific evidence. |
| Off-heap memory in .NET analytics loops | **Evolving** | `NativeMemory` and `MemoryManager<T>` are mature primitives; DeltaSharp-specific safety and accounting patterns must be built. |
| Tiered JIT and Dynamic PGO | **Mature but runtime-sensitive** | Strong .NET 8+ behavior, but benchmarks must separate cold, warm, and steady-state phases. |
| NativeAOT with optional dynamic code | **Evolving** | Patterns exist, but DeltaSharp's dual-backend architecture requires careful feature gating and analyzer hygiene. |
| SIMD intrinsics | **Mature primitives, specialized practice** | APIs are available across architectures; correctness and fallback discipline are project-specific. |
| `Expression.Compile()` intra-operator fusion | **Evolving** | Feasible and aligned with ADR-0001, but cache design, compile latency, and parity testing must be proven in DeltaSharp. |
| BenchmarkDotNet micro-benchmarking | **Mature** | Excellent harness support; benchmark scope and interpretation must avoid synthetic wins. |
| EventPipe / dotnet-trace / counters | **Mature** | Tooling is production-grade; correlating runtime events with DeltaSharp jobs/stages/tasks requires instrumentation. |

---

## Footnotes

[^1]: DeltaSharp engine architecture overview, `docs/engineering/design/engine-architecture.md`, especially the decision ledger and role ownership lines for `dotnet-runtime-performance-engineer`.

[^2]: DeltaSharp ADR-0001, `docs/adr/0001-execution-strategy.md`, on the pluggable vectorized interpreter plus optional JIT codegen tier, dynamic-code support checks, `Expression.Compile()` fusion, AOT feature-gating, and differential parity oracle.

[^3]: Existing placeholder role, `docs/persona/agents/dotnet-framework-runtime-engineer-agent.md`, states that deep .NET runtime specialization is intentionally deferred; the new seat fills that deferred runtime/performance ownership.

[^4]: `docs/persona/research/performance-benchmarking-engineer.md` defines benchmarking ownership over methodology, harnesses, regression gates, capacity modeling, and .NET profiling signals while handing deep runtime fixes to runtime specialists.

[^5]: Microsoft Learn, `Memory<T>` and `Span<T>` usage guidelines; C# language documentation for `ref struct` and `stackalloc`; Microsoft Learn `ArrayPool<T>` API documentation and dotnet/runtime `SharedArrayPool` implementation.

[^6]: Microsoft Learn `System.Runtime.InteropServices.NativeMemory`; Microsoft Learn `StructLayoutAttribute`; dotnet/runtime `MemoryManager<T>` source and related managed-memory wrapper patterns.

[^7]: Microsoft Learn .NET GC configuration, GC latency modes, Large Object Heap documentation, DATAS documentation, and NativeAOT deployment documentation.

[^8]: dotnet/runtime Book of the Runtime, `docs/design/coreclr/botr/garbage-collection.md`, for GC internals such as marking, compaction, card tables, and server/background GC behavior.

[^9]: dotnet/runtime tiered compilation design documentation; Microsoft Learn runtime compilation configuration; Microsoft Learn .NET 8 runtime updates covering Dynamic PGO and struct promotion.

[^10]: Microsoft Learn SIMD documentation for `Vector<T>` and `System.Runtime.Intrinsics`, including x86 AVX/AVX-512 and Arm AdvSimd/SVE APIs guarded by `IsSupported`.

[^11]: dotnet/runtime `System.Linq.Expressions` sources: `LambdaExpression.cs` marks IL compilation support with `[FeatureGuard(typeof(RequiresDynamicCodeAttribute))]` and exposes `Compile(preferInterpretation)`; `DelegateHelpers.cs` uses `[RequiresDynamicCode]` and reflection emit / `DynamicMethod` paths that are not NativeAOT-safe.

[^12]: BenchmarkDotNet documentation and Stephen Toub's .NET performance-improvements posts, which demonstrate Release-mode BenchmarkDotNet practice, `[MemoryDiagnoser]`, `[DisassemblyDiagnoser]`, and runtime-source-based optimization.

[^13]: Microsoft Learn .NET diagnostics documentation for `dotnet-trace`, EventPipe, `dotnet-counters`, `dotnet-gcdump`, and PerfView.
