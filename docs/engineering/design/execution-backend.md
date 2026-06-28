# Execution backend seam & dynamic-code feature switch (v1)

> **Status:** living document. Created with
> [STORY-01.4.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-01-project-build-platform.md#story-0142-dynamic-code-feature-switch-and-codegen-elision).
> Grounded in [ADR-0001](../../adr/0001-execution-strategy.md) (pluggable vectorized
> interpreter + optional JIT codegen tier) and
> [ADR-0014](../../adr/0014-target-framework-aot.md) (NativeAOT executor). See
> [api-governance.md](api-governance.md) for the banned-API exception this relies on. Update it
> whenever the backend seam or the feature-switch mechanics change.

DeltaSharp evaluates physical operators and expressions through a **pluggable execution
backend** (ADR-0001). Two backends exist behind one interface:

- **`InterpretedVectorizedBackend`** — the default, always present, AOT- and trim-clean
  batch-at-a-time interpreter. It is the **correctness ground truth**.
- **`CompiledBackend`** — an optional codegen tier that fuses expressions into JIT-compiled
  delegates via `Expression.Compile()`. It emits IL at runtime, so it is NativeAOT-incompatible
  and is **selected only when the runtime supports dynamic code**. It is never required for
  correctness — it must match the interpreter bit-for-bit.

These contracts live in the unshipped `DeltaSharp.Engine` assembly under
`src/DeltaSharp.Engine/Execution/`; `public` here is an engine-internal seam, not a shipped
surface (see [testing-conventions.md](testing-conventions.md)). They do **not** expand
`DeltaSharp.Core`'s public API.

## The seam surface

| Type | Role |
| --- | --- |
| `IExecutionBackend` | Backend contract: `Name`, `UsesDynamicCode`, and the evaluation entry point(s). |
| `InterpretedVectorizedBackend` | Default backend; stateless `Instance`; `UsesDynamicCode == false`. |
| `CompiledBackend` | Optional `[RequiresDynamicCode]` tier; `UsesDynamicCode == true`. |
| `ExecutionBackends` | Static selector (`Select()` / `Select(options)`) + the feature guard. |
| `ExecutionBackendOptions` | `ForceInterpreted` (determinism/debugging/parity); `Default`. |
| `AffineInt64Kernel` | The first **representative** kernel exercised by the seam (see below). |

### `AffineInt64Kernel` is a representative placeholder

M1 has no general expression/operator model yet (it arrives in later EPIC-02 stories), so the
seam is exercised by a single faithful stand-in: the affine transform
`y = (Multiplier * value) + Addend` over `Int64`. It is small enough to be obviously correct yet
matches ADR-0001's "fuse a scalar expression into a delegate" granularity, and — crucially — it
gives the **differential parity oracle** something concrete to compare. `AffineInt64Kernel` is
**not** a public expression API; it will be generalized (not preserved) once real expressions
exist. Its arithmetic is `unchecked` (wrapping), matching both C# operators and
`Expression.Multiply`/`Expression.Add`, so both backends agree on overflow.

## Backend selection

`ExecutionBackends.Select()` decides the backend at the call site:

```text
if options.ForceInterpreted      -> InterpretedVectorizedBackend.Instance
else if IsCompiledBackendAvailable -> CompiledBackend     (dynamic-code runtimes)
else                              -> InterpretedVectorizedBackend.Instance
```

`ForceInterpreted` pins the interpreter on a JIT runtime for determinism, debugging, and parity
testing. Absent that, selection follows the runtime's real capability.

## How the compiled tier is elided under NativeAOT

The compiled tier must vanish from NativeAOT publishes without IL trim/AOT warnings. Three
mechanisms combine (each maps to a STORY-01.4.2 acceptance criterion):

1. **`[RequiresDynamicCode]` on `CompiledBackend`** (AC1) — marks the whole type (construction
   and members, including the `Expression.Compile()` call) as dynamic-code-requiring.
2. **A single feature guard** (AC1/AC3) — `ExecutionBackends.IsCompiledBackendAvailable` is
   annotated `[FeatureGuard(typeof(RequiresDynamicCodeAttribute))]` and forwards
   `RuntimeFeature.IsDynamicCodeSupported`. It is the **sole** path to the compiled tier:
   `Select` reaches `CreateCompiledBackend()` (itself `[RequiresDynamicCode]`) only inside
   `if (IsCompiledBackendAvailable)`. The analyzer treats that branch as unreachable when
   dynamic code is unsupported, so no IL3050 is raised and the branch — with `CompiledBackend`
   and `Expression.Compile` — is dead-code-eliminated. NativeAOT hard-wires
   `IsDynamicCodeSupported` to `false`.
3. **The banned-API exception** (AC4) — `Expression.Compile` is in `BannedSymbols.txt`; the call
   is wrapped in a scoped `#pragma warning disable RS0030` justified by ADR-0001 (the documented
   exception pattern in [api-governance.md](api-governance.md)). Removing either the pragma or
   the annotations reproduces the analyzer failure — that is the AC4 guarantee, not a defect.

### Verification (executable)

- **Build:** `dotnet build -c Release` — 0 warnings (the feature-guard pattern is AOT-analyzer
  clean).
- **AOT elision (AC3):** the executor calls `ExecutionBackends.Select()` from `Main`, and
  `aot.yml` runs `dotnet publish -p:PublishAot=true -warnaserror`. The publish succeeds with no
  IL2xxx/IL3xxx warnings, the native image **omits** `CompiledBackend`, and the binary prints
  `interpreted-vectorized (dynamic-code=False)` — proving the interpreter remains available and
  correct on a true no-dynamic-code host (AC2/AC5).
- **Parity oracle (AC5):** `ExecutionBackendTests.Parity_CompiledMatchesInterpreted` asserts the
  compiled and interpreted evaluators produce identical results (and match the reference) across
  a sweep including overflow inputs, on JIT runtimes where the compiled tier is live.

## What is deferred

- The general expression / operator model and the real SIMD kernel library (later EPIC-02).
- Intra-operator `Expression.Compile()` fusion of real predicates/projections, then optional
  cross-stage WholeStageCodegen (a separate ADR if pursued) — ADR-0001 "Follow-ups".
- Backend-selection configuration beyond `ForceInterpreted` (e.g. a config/env override surfaced
  to users) and codegen caching of compiled delegates.
