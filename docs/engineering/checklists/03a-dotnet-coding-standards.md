# 03a — .NET Coding Standards Checklist

> **Scope:** C# projects, public .NET APIs, engine/runtime hot paths, analyzers, packaging settings, and NativeAOT/trim-sensitive code.
> **Priority:** HIGH.
> **Owners:** dotnet-library-platform-engineer, dotnet-runtime-performance-engineer, dotnet-framework-runtime-engineer. **Grounded in:** `CONTRIBUTING.md`, `.github/copilot-instructions.md`, ADR-0001, ADR-0013, ADR-0014, .NET persona research.

## How to use
Use this checklist for every C# change after applying 03. Escalate as High or Critical when a .NET issue can cause deadlocks, unbounded memory growth, AOT publish failure, disposed-resource use, or incorrect query results.

## Checklist
### Project and build policy
- [ ] Engine and executor projects target `net10.0`; public-facing libraries multi-target `net8.0;net10.0` unless an ADR says otherwise.
- [ ] Nullable reference types are enabled and nullable warnings are fixed rather than suppressed globally.
- [ ] Shared policy belongs in `Directory.Build.props`, `Directory.Build.targets`, `.editorconfig`, `global.json`, or `Directory.Packages.props` instead of per-project drift.
- [ ] Package versions are centrally governed; project-local package versions are justified and reviewed.
- [ ] Public API changes are tracked through public API baselines or equivalent analyzer gates.
- [ ] Banned APIs cover unguarded dynamic code, reflection emit, nondeterministic generator I/O, and trim/AOT-unsafe shortcuts.
- [ ] `dotnet format --verify-no-changes` passes for formatting and analyzer policy.

### Naming, shape, and visibility
- [ ] Public types, methods, properties, and events use PascalCase; private fields use `_camelCase`.
- [ ] Types are `sealed` by default unless inheritance is an intentional extension point with tests.
- [ ] Mutable state is private and narrowly scoped; immutable records or readonly types are used where plan or configuration identity matters.
- [ ] `readonly struct`, `readonly` fields, and `in` parameters are used for hot value types when they avoid defensive copies without harming clarity.
- [ ] Public XML docs describe Spark compatibility, cancellation, ownership, and exception behavior for user-facing APIs.
- [ ] `InternalsVisibleTo` additions are scarce, named consistently, and do not turn internals into an unstable public contract.

### Async, cancellation, and disposal
- [ ] I/O-bound operations use `async`/`await`; synchronous blocking over async work is absent from driver, executor, storage, and operator paths.
- [ ] `CancellationToken` flows from public action APIs through planning, storage, RPC, executor, and shuffle boundaries.
- [ ] Cancellation, timeout, validation, transient failure, storage failure, Delta conflict, and programmer error remain distinguishable.
- [ ] `ValueTask` is used only on measured hot paths or frequently synchronous async APIs, and consumption rules are documented by tests.
- [ ] Streams, channels, clients, native buffers, pooled buffers, and handles are deterministically disposed with `IDisposable` or `IAsyncDisposable`.
- [ ] Async enumerables and channels are bounded or backpressure-aware; unbounded buffering is justified and tested.
- [ ] Tests cover cancellation and disposal for long-running or resource-owning paths.

### Memory and hot-path performance
- [ ] Hot loops avoid LINQ, closures, iterator allocations, boxing, reflection, virtual dispatch, and per-row object allocation unless benchmarks justify them.
- [ ] `Span<T>`/`ReadOnlySpan<T>` are used for synchronous stack-bounded access; `Memory<T>`/`ReadOnlyMemory<T>` are used when async ownership is required.
- [ ] `ArrayPool<T>` or `MemoryPool<T>` usage states who rents, returns, clears, slices, and owns each buffer.
- [ ] Off-heap `NativeMemory` or Arrow-backed memory follows ADR-0013 with alignment, bounds, lifetime, exception safety, leak detection, and pod memory accounting.
- [ ] Pinned buffers and `GCHandle` usage have a lifetime story and do not retain large arrays accidentally.
- [ ] SIMD or hardware-intrinsic code has `IsSupported` guards, scalar fallbacks, and parity tests for nulls, NaN, overflow, decimal, and ordering semantics.
- [ ] BenchmarkDotNet or trace evidence accompanies changes that claim hot-path performance improvements.

### AOT, trimming, code generation, and analyzers
- [ ] The vectorized interpreter remains AOT-clean, always available, and the correctness reference required by ADR-0001.
- [ ] The optional compiled backend is enabled only behind `RuntimeFeature.IsDynamicCodeSupported` and feature switches.
- [ ] `Expression.Compile()`, `DynamicMethod`, reflection emit, and dynamic assembly loading are isolated from NativeAOT executor publishes.
- [ ] Dynamic-code paths carry `[RequiresDynamicCode]`, trim-unsafe paths carry `[RequiresUnreferencedCode]`, and reflection dataflow uses `[DynamicallyAccessedMembers]` where needed.
- [ ] Suppressions are local, include the invariant that makes them safe, and are not used to silence project-wide trim/AOT warnings.
- [ ] Representative `dotnet publish /p:PublishAot=true` evidence exists for packages or executors that claim AOT support.
- [ ] Source generators use `IIncrementalGenerator`, deterministic inputs, stable ordering, cancellation, diagnostics, and tests.

## Anti-patterns (red flags)
- Nullable is disabled or warnings are globally suppressed.
- A transformation path blocks on `.Result`, `.Wait()`, or sync-over-async I/O.
- `CancellationToken` is accepted publicly but ignored by storage, RPC, executor, or shuffle work.
- Hot operators allocate per row through LINQ, closures, boxing, strings, or virtual dispatch.
- Pooled or native buffers lack single-owner rent/return/free rules.
- `Expression.Compile()` or reflection emit is reachable from NativeAOT executor code.
- AOT/trim warnings are hidden by broad suppressions or treated as future cleanup.
- Public API changes bypass analyzers, API baselines, package validation, or migration notes.
- Source generators read the network, depend on wall-clock time, or attempt runtime query-plan codegen.

## References
- [03 — Coding Conventions Checklist](03-coding-conventions-checklist.md)
- [08 — Performance Checklist](08-performance-checklist.md)
- [21 — Distributed Correctness Checklist](21-distributed-correctness-checklist.md)
- [22 — Benchmark Regression Gates Checklist](22-benchmark-regression-gates-checklist.md)
- ADR-0001: Execution strategy
- ADR-0013: Memory model for in-memory batches
- ADR-0014: Target framework and AOT posture
- `docs/persona/research/dotnet-runtime-performance-engineer.md`
- `docs/persona/research/dotnet-library-platform-engineer.md`
- `docs/persona/research/dotnet-framework-runtime-engineer.md`
- `CONTRIBUTING.md`
