# ADR-0001: Execution strategy — pluggable vectorized interpreter + optional JIT codegen tier

- **Status:** Accepted
- **Date:** 2026-06-26
- **Deciders:** @khaines
- **Related:** ADR-0002 (columnar format), `docs/engineering/design/engine-architecture.md`

## Context

DeltaSharp is a **fully native** .NET re-implementation of the Spark engine (no
JVM bridge). A core decision is *how a physical plan executes each operator*
(scan → filter → project → aggregate → join). Two reference models exist:

- **WholeStageCodegen (Spark Tungsten):** generate and compile fused operator
  code at runtime so a whole stage becomes one tight loop with no virtual
  dispatch. Highest steady-state throughput (Spark measured ~0.9 ns/row for a sum
  vs ~14 ns interpreted).
- **Vectorized interpretation (DuckDB / Velox / DataFusion):** pre-built SIMD
  kernels process **batches** (~1–8k rows) of columnar data; operators dispatch
  on column types.

The decision driver is the project commitment to **native .NET "all the way,"
including Native AOT capability** for executors (fast cold start, low memory —
valuable for ephemeral Kubernetes executor pods). Runtime code generation
(`Expression.Compile()` IL-emit, `System.Reflection.Emit.DynamicMethod`) is
**NativeAOT-incompatible**: `DynamicMethod` throws `PlatformNotSupportedException`
under AOT, and `Expression.Compile()` silently falls back to a ~5–10× slower
interpreter. So WholeStageCodegen would forfeit AOT. Separately, query plans are
constructed **at runtime** from user SQL/DataFrame calls, so compile-time source
generators cannot generate per-query fused code.

## Decision

Adopt a **pluggable execution backend** (`IExecutionBackend` for operator and
expression evaluation) with two implementations:

1. **`InterpretedVectorizedBackend` (default and reference)** — always present,
   AOT-clean, batch-at-a-time SIMD kernels over columnar batches. This is the
   correctness ground truth.
2. **`CompiledBackend` (optional tier)** — enabled **only when**
   `RuntimeFeature.IsDynamicCodeSupported` is true; uses `Expression.Compile()`
   (and, later, optionally `DynamicMethod`/IL) to fuse hot expressions into cached
   delegates.

Backend selection is decided at startup via `RuntimeFeature.IsDynamicCodeSupported`,
with a config override (force-interpreter for determinism/debugging). Codegen is
introduced at **intra-operator expression-fusion granularity first** (e.g., fuse a
filter predicate or projection into a `Func` over a batch); full cross-stage
WholeStageCodegen remains an optional later escalation behind the same interface.

## Consequences

### Positive

- AOT-clean by default; codegen is an additive fast-path, never a dependency.
- Mirrors how .NET's own libraries (`System.Text.Json`, `System.Linq.Expressions`)
  handle the JIT/AOT split — proven pattern.
- Vectorized interpretation is the modern SOTA for native analytic engines
  (DuckDB, Velox, DataFusion, Photon) and is simpler and more debuggable than IR→IL
  lowering.

### Negative / costs

- Two backends to maintain, which **requires a differential parity oracle**: the
  interpreter is ground truth and the compiled tier must match it bit-for-bit
  (owned by `reliability-test-chaos-engineer` + the .NET compute seats).
- The compiled tier must be cleanly elided under AOT via `[RequiresDynamicCode]` /
  `[FeatureGuard]` / `FeatureSwitchDefinition` so `PublishAot` dead-code-eliminates
  it (AOT-hygiene owned by `dotnet-library-platform-engineer`).
- Regression gates must benchmark **both** backends (`performance-benchmarking-engineer`).

### Follow-ups and sequencing

- Start with the interpreted vectorized backend + a rich SIMD kernel library.
- Add intra-operator `Expression.Compile()` fusion (delegate-cached) as the first
  codegen increment, gated on dynamic-code support.
- Cross-stage fusion is a later, optional optimization (separate ADR if pursued).

## Alternatives considered

- **WholeStageCodegen only (JIT):** highest throughput, but forfeits Native AOT,
  is very complex (IR→IL lowering), and is hard to debug. Rejected as the *only*
  backend; retained as an optional tier.
- **Source generators for query codegen:** AOT-safe but cannot see runtime-built
  plans; usable only for fixed-schema accessors, not per-query execution. Rejected
  for the execution path.
- **Interpreter only, no codegen ever:** simplest, but leaves ~15–30% throughput on
  the table on JIT runtimes. Rejected in favor of the optional tier.

## References

- Spark Tungsten WholeStageCodegen — Databricks, "Apache Spark as a Compiler"
  (https://www.databricks.com/blog/2016/05/23/apache-spark-as-a-compiler-joining-a-billion-rows-per-second-on-a-laptop.html).
- Velox expression evaluation (SelectivityVector, dictionary peeling) —
  https://facebookincubator.github.io/velox/develop/expression-evaluation.html.
- DuckDB vectorized execution — https://duckdb.org/why_duckdb.
- `dotnet/runtime` `LambdaExpression.cs` (`CanCompileToIL` is
  `[FeatureGuard(RequiresDynamicCode)]`; `Compile(preferInterpretation)`) and
  `DelegateHelpers.cs` (`[RequiresDynamicCode]`, `DynamicMethod` under AOT).
- MS Learn — Native AOT: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/.
