# Compiled expression fusion (STORY-03.4.2)

> **Status:** living document. Created with
> [STORY-03.4.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-03-vectorized-execution-backend.md#story-0342-add-dynamic-code-gated-compiled-expression-fusion)
> (dynamic-code-gated compiled expression fusion). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (the vectorized interpreter is the default backend
> **and** the parity oracle; codegen is an additive, elidable tier) and
> [ADR-0014](../../adr/0014-target-framework-aot.md) (the NativeAOT executor host). Builds directly on
> [interpreted-expression-evaluator.md](interpreted-expression-evaluator.md) (STORY-03.4.1 — the
> bit-for-bit reference this tier must match), [execution-backend.md](execution-backend.md) (backend
> selection + `AffineInt64Kernel` codegen precedent), [native-aot.md](native-aot.md) (the
> `[RequiresDynamicCode]` / `[FeatureGuard]` elision contract), [type-system.md](type-system.md)
> (numeric promotion, decimal arithmetic, ANSI overflow), [null-validity-model.md](null-validity-model.md)
> (#143 validity + Kleene 3VL), and [columnar-contracts.md](columnar-contracts.md) (the
> `ColumnBatch` / `ColumnVector` / `SelectionVector` surface). Update it whenever a lowering rule, the
> cache key, the fusable surface, or the elision proof changes.

STORY-03.4.1 delivered an **interpreted, vectorized expression evaluator** — pre-built per-node kernels
that walk a `ColumnBatch` lane-by-lane producing values **and** validity, with no runtime code
generation. ADR-0001 makes that interpreter the **correctness ground truth**. This story adds the
*optional* second tier ADR-0001 anticipates: a **compiled expression-fusion** path that lowers the same
`PhysicalExpression` tree into a single JIT-compiled delegate (`Expression<TDelegate>.Compile()`),
caches it per expression shape, and reuses it across batches — so hot filter predicates and projection
expressions run faster on JIT runtimes **without ever becoming a correctness dependency**.

The non-negotiable invariant: the compiled output is **byte-identical** to the interpreter for both
values and validity. The compiled tier is gated on `RuntimeFeature.IsDynamicCodeSupported`, carries
`[RequiresDynamicCode]` + `[FeatureGuard]`, and is **dead-code-eliminated from NativeAOT**, where the
interpreter remains the sole path. Everything here lives in the unshipped `DeltaSharp.Engine` assembly;
`public`/`internal` here is an engine seam, not shipped surface.

## Where the code lives

| File (`src/DeltaSharp.Engine/Execution/…`) | Role |
| --- | --- |
| `CompiledBackend.cs` | The optional backend (ADR-0001). Owns the per-instance `CompiledExpressionCache`; `BuildExpressionEvaluator(expr, schema, kind)` is the fusion entry point; `Open` still **delegates operators** to `InterpretedOperators`. `[RequiresDynamicCode]`. |
| `ExecutionBackends.cs` | Backend selection. `IsCompiledBackendAvailable` is the single `[FeatureGuard]`; `Select` reaches `CreateCompiledBackend()` only behind it. |
| `Expressions/CompiledExpressionEvaluators.cs` | `CanFuse` (the conservative whole-tree gate), `Build` (compiled-or-fallback), and `ValidateColumnReferences` (interpreter-identical rejection). |
| `Expressions/CompiledExpressionLowering.cs` | The lowering heart: each node → two LINQ-`Expression` locals; assembles + compiles the `FusedRowKernel`. `[RequiresDynamicCode]`. |
| `Expressions/CompiledScalarOps.cs` | Scalar helpers that mirror the interpreter's private math **and exception messages** verbatim (integral/float/double/decimal arithmetic, casts). |
| `Expressions/CompiledVectorAccess.cs` | Trim-safe seam: non-generic wrappers around `ColumnVector.GetValue<T>` / `MutableColumnVector.AppendValue<T>` / `IsNull`, each exposed as a cached `MethodInfo`. |
| `Expressions/CompiledExpressionEvaluator.cs` | The driver: derives `ExpressionEvaluator`, gathers referenced columns once per batch, runs the kernel per row. `[RequiresDynamicCode]`. |
| `Expressions/CompiledExpressionCache.cs` | `CompiledFusion`, `CompiledExpressionCacheMetrics`, and the bounded, lock-free `CompiledExpressionCache`. |
| `Expressions/CompiledExpressionKey.cs` | The deterministic structural cache key. |
| `Expressions/FusedRowKernel.cs` | `internal delegate void FusedRowKernel(ColumnVector[] inputs, int row, MutableColumnVector output)`. |

The differential parity suite is `tests/DeltaSharp.Engine.Tests/Execution/CompiledExpressionFusionTests.cs`.

## The two-tier model (ADR-0001)

| Tier | Type | Availability | Role |
| --- | --- | --- | --- |
| **Interpreted reference** | `InterpretedVectorizedBackend` + `ExpressionEvaluators.Build` | **Always** (AOT-clean) | The semantic ground truth — the *parity oracle*. |
| **Compiled fast path** | `CompiledBackend` + `CompiledExpressionEvaluators.Build` | Only when `RuntimeFeature.IsDynamicCodeSupported` | An *optimization*: same results, fewer allocations + less dispatch on JIT runtimes. |

The compiled tier is **additive and elidable**. It never re-implements operators (operator execution is
delegated to the shared `InterpretedOperators` dispatch — see
[execution-backend.md](execution-backend.md)); it only fuses the *expressions* a filter predicate or a
projection evaluates. Because the interpreter is always present, a missing or disabled compiled tier is
never a correctness problem — only a performance one.

### Backend selection flow

`ExecutionBackends.Select(options)`:

1. `options.ForceInterpreted` → `InterpretedVectorizedBackend.Instance`.
2. else `if (IsCompiledBackendAvailable)` → `CreateCompiledBackend()` → `new CompiledBackend()`.
3. else → `InterpretedVectorizedBackend.Instance`.

`IsCompiledBackendAvailable` is an `internal static bool` annotated
`[FeatureGuard(typeof(RequiresDynamicCodeAttribute))]` that simply returns
`RuntimeFeature.IsDynamicCodeSupported`. It is the **sole** gate: `CreateCompiledBackend`,
`CompiledBackend`, and every compiled-tier type are reachable only through it, so on NativeAOT (where
the runtime hard-wires `IsDynamicCodeSupported` to `false`) the whole branch — and the types it reaches
— is eliminated. Selection happens once per query; it is never on the per-batch/per-row path.

## What is fusable: `CanFuse` and transparent fallback

`CompiledExpressionEvaluators.Build` first asks `CanFuse(expression)`. The check is **conservative and
whole-tree**: a single non-fusable lane anywhere routes the *entire* expression to the interpreter, so
fusion is all-or-nothing per expression.

`CanFuse` walks the tree:

- **`ColumnReference` / `Literal`** — fusable iff the type has a fixed-width carrier (`IsFusableType`:
  boolean, byte, short, int, long, float, double, date, timestamp, decimal). **String and binary are
  never fusable** (no fixed-width carrier).
- **`ArithmeticExpression`** — result type fusable **and** both children fusable, **except**
  decimal `Divide`/`Remainder`, which is returned `false` so the interpreter throws the identical
  `UnsupportedOperatorException` (decimal value rounding is deferred by the type system).
- **`ComparisonExpression`** — `EvalKind` is not `String`/`Binary`, and both children fusable.
- **`LogicalExpression`** — children fusable (`Not` has only `Left`).
- **`CastExpression`** — `CastEvaluator.IsSupported(child, target)` **and** both child and target types
  fusable, **and** the child fusable. (So `float`/`double → decimal`, which is outside the interpreter's
  v1 cast matrix, falls back — both tiers reject it identically.)
- **`IsNullExpression`** — child fusable.

When `CanFuse` returns `false`, `Build` returns `ExpressionEvaluators.Build(expression, inputSchema,
backendName, kind)` — the **interpreter**. Crucially, the *same* node shapes that the interpreter
rejects at build time (decimal divide, unsupported cast, an unknown node) raise their
`UnsupportedOperatorException` from that fallback call, so rejection is parity-identical. When `CanFuse`
returns `true`, the only build-time failure left is an invalid column reference, and
`ValidateColumnReferences` mirrors the interpreter's `BuildColumnReference` checks (out-of-range ordinal,
type mismatch) **with the identical messages and `paramName "column"`**.

## Lowering a `PhysicalExpression` to a LINQ Expression

`CompiledExpressionLowering.Lower(expression)` performs a **post-order** walk (children first). Every
node lowers — Spark-codegen style — into exactly **two block locals**: a `bool isNull` validity flag and
a value **carrier**. There are **no per-node intermediate vectors** — that elimination is the fusion win
over the vector-at-a-time interpreter.

The carrier type is a pure function of the `DataType` (`CarrierOf`):

| DataType | Carrier (CLR type) |
| --- | --- |
| boolean | `bool` |
| byte (signed tinyint) | `byte` (stored unchecked from `sbyte`) |
| short | `short` |
| int, date | `int` |
| long, timestamp | `long` |
| float | `float` |
| double | `double` |
| decimal (compact or wide) | `DecimalValue` |

Value computation for *computed* nodes is wrapped in `IfThen(Not(isNull), …)`: a null operand never
runs the value math, so it can never raise a spurious overflow — exactly mirroring how the interpreter
skips computation for null lanes. *Leaf* nodes (column reads, literals) assign their value
unconditionally (the reads are side-effect-free).

### Per-node lowering

| Node | `isNull` | `value` (computed under `if (!isNull)` unless noted) |
| --- | --- | --- |
| `ColumnReference` | `CompiledVectorAccess.IsRowNull(inputs[slot], row)` | `ReadStorage(type)` — a non-generic `GetValue<T>` wrapper (leaf: assigned unconditionally). |
| `Literal` | constant `literal.IsNull` | baked `Expression.Constant` carrier (decimal → `new DecimalValue(unscaled, scale)`); skipped entirely when the literal is null. |
| `ArithmeticExpression` | `OrElse(left.IsNull, right.IsNull)` | by `EvalKind` — see below. |
| `ComparisonExpression` | `OrElse(left.IsNull, right.IsNull)` | `ComparisonToBoolean(op, sign)` where `sign` is `−1/0/+1`. |
| `LogicalExpression` | `!CompiledScalarOps.HasValueBool(kleene)` | `CompiledScalarOps.UnwrapBool(kleene)`; `kleene` = `NullPropagation.Kleene{And,Or,Not}` over `bool?` operands. |
| `CastExpression` | `child.IsNull` (null in → null out) | by target type — see below. |
| `IsNullExpression` | constant `false` (IS [NOT] NULL is never null) | `Negated ? !child.IsNull : child.IsNull`. The child's **full setup still runs** (it may throw on ANSI overflow, exactly as the interpreter materializes the child vector); only its validity bit feeds the result. |

**Arithmetic** dispatches on `ArithmeticEvalKind`:

- **Integral** — `CompiledScalarOps.TryIntegral(op, AsInt64(left), AsInt64(right), min, max, mode)`
  returns `long?`; on success `Narrow` casts (unchecked) to the carrier. The helper performs
  checked `+`/`−`/`×` (catching `OverflowException`), the `Remainder` zero/`-1` guards, and the
  post-op range check, identical to `ArithmeticEvaluator`.
- **Single** — `Add`/`Subtract`/`Multiply` are emitted as primitive `Expression.Add/Subtract/Multiply`
  on `float`; `Remainder` routes through `CompiledScalarOps.TrySingleRemainder` (zero-check).
- **Double** — `Add`/`Subtract`/`Multiply` primitive on `double`; `Divide`/`Remainder` route through
  `CompiledScalarOps.TryDoubleDivideOrRemainder` (zero-check). (Spark `/` over non-decimal operands
  resolves to a `Double` result, so `int / int` lowers here with both operands widened by `AsDouble`.)
- **Decimal** — `CompiledScalarOps.TryDecimal(op, AsDecimal(left), AsDecimal(right), resultType, mode)`
  returns `DecimalValue?` (it calls `DecimalValue.Add/Subtract/Multiply` then `ToType`, nulling on a
  `Legacy`-mode overflow), and the result is appended through `VectorMaterializer.AppendDecimal`.

**Comparison** computes a sign then maps it to a boolean:

- `Int64`/`Date`/`Timestamp`/`Boolean` → `CompiledScalarOps.CompareInt64(AsInt64(l), AsInt64(r))`.
- `Double` → `ScalarReader.CompareDouble` (the **shared** total-order helper — same NaN/`-0.0` policy).
- `Decimal` → `ScalarReader.CompareDecimal` (shared).
- `DateTimestamp` → `CompareInt64` after promoting the `date` operand by `× TemporalValues.MicrosPerDay`.
- `ComparisonToBoolean` maps the sign with `Expression.Equal/NotEqual/LessThan/LessThanOrEqual/
  GreaterThan/GreaterThanOrEqual` against the constant `0`.

**Cast** (`ComputeCast`): identity → copy; `→ bool` → `CastToBoolean`; `→ integral` → `CastToIntegral`
(`TryCastDoubleToIntegral` / `TryCastDecimalToIntegral` / `TryCastIntegralToIntegral`, using the exact
`(min, max, upperExclusive)` boundaries — note `long`'s exclusive bound is `2^63 = 9223372036854775808.0`
because `long.MaxValue` is not representable as a `double`); `→ float`/`→ double` via `ReadAsDouble`;
`→ decimal` → `CompiledScalarOps.TryFitDecimal`; `→ date` → `TemporalValues.TimestampToDate`; `→ timestamp`
→ `TemporalValues.DateToTimestamp`. Operand widening helpers `AsInt64`/`AsDouble`/`AsSingle`/`AsDecimal`
mirror `ScalarReader.ReadInt64/ReadDouble/ReadSingle/ReadDecimal` (e.g. signed-byte widening is
`(long)(sbyte)b`).

### Assembling the kernel

`Lower` finishes by emitting the root append —
`IfThenElse(root.IsNull, CompiledVectorAccess.AppendNull(output), AppendValue(output, root))` — wrapping
all locals and statements in one `Expression.Block`, and building
`Expression.Lambda<FusedRowKernel>(body, inputs, row, output)`. The `Compile()` call is the sanctioned
codegen entry point; it sits under a **scoped** `#pragma warning disable RS0030` (the BannedApi rule)
annotated to ADR-0001, exactly like the existing `CompiledBackend.BuildAffineEvaluator`. No
`Reflection.Emit`, `Activator`, `MakeGenericMethod`, or string/`Type[]` reflection is used anywhere.

### The fused kernel and its driver

`FusedRowKernel(ColumnVector[] inputs, int row, MutableColumnVector output)` evaluates the whole tree
for **one** logical row and appends **one** lane. `inputs` is a **dense slot array**: `LoweringContext.Slot`
assigns each distinct `ColumnReference.Ordinal` a slot in first-encounter order (a repeated ordinal
reuses its slot), and `CompiledFusion.SlotOrdinals` records the slot → batch-ordinal map.

`CompiledExpressionEvaluator.Evaluate` (the driver) therefore:

1. reads `rows = batch.LogicalRowCount`;
2. gathers `inputs[slot] = batch.SelectedColumn(slotOrdinals[slot])` once per batch — `SelectedColumn`
   yields the batch's **logical (selection-applied) view**, so the kernel indexes `row` directly and the
   tier is **selection- and slice-aware** for free, with deterministic logical output order;
3. reserves **only the output** footprint via `memory.ReserveVector(Type, rows)` — because fusion
   eliminates per-node temporaries, the compiled tier reserves *less* memory than the interpreter (this
   is not a parity concern: parity compares the produced result vectors, not the memory ledger);
4. loops `_kernel(inputs, row, result)` over `[0, rows)` and returns `result`.

Because it derives from `ExpressionEvaluator`, the compiled evaluator is a drop-in replacement for the
interpreted one — same base type, same `Evaluate` contract, same `(Type, Nullable)` surface.

## Parity by construction

The compiled tier is engineered so that matching the interpreter is the *default*, not a coincidence:

- **Shared helpers are reused, not re-derived.** Floating order (`ScalarReader.CompareDouble`), decimal
  compare/widen (`ScalarReader.CompareDecimal`, `ScalarReader.ToDouble`), Kleene 3VL
  (`NullPropagation.KleeneAnd/Or/Not`), temporal conversions (`TemporalValues.TimestampToDate/
  DateToTimestamp`), decimal arithmetic (`DecimalValue.Add/Subtract/Multiply` + `ToType`), and the
  compact/wide decimal materialization (`VectorMaterializer.AppendDecimal`) are called directly from the
  emitted IL.
- **New scalar code mirrors the interpreter line-for-line.** `CompiledScalarOps` reproduces
  `ArithmeticEvaluator`/`CastEvaluator`'s private math, including the **exact exception messages**:
  `ArithmeticOverflowException($"Arithmetic '{op}' overflowed '{typeName}'.")`,
  `DivideByZeroException($"Division by zero in arithmetic '{op}'.")`, and
  `ArithmeticOverflowException($"Cast to '{typeName}' is out of range.")`, where `typeName` is the
  result type's `SimpleString` baked into the kernel as a constant. (The parity suite asserts these
  messages are byte-identical across tiers on single-error batches.)
- **MethodInfo handles are trim-safe.** Every `MethodInfo` is obtained from a delegate method-group
  (`((Func<…>)Method).Method`), never `GetMethod(string)`/`MakeGenericMethod`, so `[RequiresDynamicCode]`
  (which suppresses only the IL3xxx AOT warnings, not IL2xxx trim warnings) is sufficient for the whole
  tier to be analyzer-clean.

## The delegate cache

### Key — `CompiledExpressionKey.Of`

A deterministic structural string signature. Two trees share a key — and therefore a compiled kernel —
**exactly when they lower to the same IL**. The signature captures every input to a lowering decision:
node kind (a one-letter tag), resolved `Type.SimpleString`, operator, eval kind, ANSI mode, column
ordinal, and the **baked-in literal value**. Floating literals are keyed by their raw bit pattern
(`SingleToInt32Bits` / `DoubleToInt64Bits`) so `-0.0`, `+0.0`, and distinct `NaN` payloads never collide;
decimal literals key on `unscaled/scale`; a null literal keys as `n`.

The node's `Nullable` flag is **intentionally excluded** from the key. Nullability does not change the
emitted IL — the kernel always computes `isNull` per row — so two trees that differ only in a node's
`Nullable` flag can safely share one kernel. The nullability *contract* is preserved separately: the
wrapping `CompiledExpressionEvaluator` is constructed with `(expression.Type, expression.Nullable)`, so
each evaluator reports the correct `Nullable` to downstream consumers regardless of cache sharing. This
is strictly *more* reuse than STORY-03.4.2's AC2 requires, and the differential suite proves validity
identity holds regardless. Because the traversal is deterministic, an equal key also implies an equal
`SlotOrdinals` map, so a cached kernel is always driven with the correct columns.

### Lifetime and thread-safety

The cache is a field on each `CompiledBackend` instance, so its lifetime is the backend's. It is
consulted **only at expression-build (operator `Open`) time, never per batch or per row** — the
hot path runs the already-compiled delegate — so the engine stays lock-free on the data path.

Internally it is itself lock-free: a `ConcurrentDictionary<string, Lazy<CompiledFusion>>` keyed by the
structural signature, plus a `ConcurrentQueue<string>` recording insertion order, plus `Interlocked`
counters. `GetOrCompile`:

1. `TryGetValue` hit → increment `Hits`, return.
2. else create a `Lazy<CompiledFusion>` (`ExecutionAndPublication`) whose factory lowers + compiles and
   increments `Compilations`, then `GetOrAdd` it.
3. if another thread won the race (`!ReferenceEquals`) → increment `Hits`, return the winner's value.
4. else enqueue the key, force the `Lazy` (compile happens **once**, outside the dictionary lock), and
   evict.

The `Lazy` with `ExecutionAndPublication` guarantees a given shape is JIT-compiled at most once even
under concurrent first-touch; a racing loser counts a hit and reuses the winning kernel.

### Bounded eviction and metrics

`DefaultCapacity` is `1024`. After inserting a new entry, `EvictIfOverCapacity` removes the oldest keys
(FIFO via the insertion queue) while `Count > capacity`, incrementing `Evictions` for each real removal
(stale queue entries are skipped without counting). `Metrics` returns an immutable
`CompiledExpressionCacheMetrics(long Compilations, long Hits, long Evictions, int Count)` snapshot,
surfaced for diagnostics/tests via `CompiledBackend.ExpressionCacheMetrics`. This satisfies AC3's
"compile count, cache hit count, and eviction count" reporting with bounded memory.

## AOT elision mechanics

The compiled tier must vanish from a NativeAOT publish, leaving only the interpreter. Three mechanisms
combine:

1. **`[RequiresDynamicCode]` at every definition.** `CompiledBackend`, `CompiledExpressionEvaluator`,
   `CompiledExpressionLowering`, `CompiledExpressionEvaluators`, and `ExecutionBackends.CreateCompiledBackend`
   are annotated. This documents the dynamic-code dependency and suppresses IL3050 *at the definition*,
   but on its own would still warn at the *call sites*.
2. **A single `[FeatureGuard]` at the reachability root.** `ExecutionBackends.IsCompiledBackendAvailable`
   is `[FeatureGuard(typeof(RequiresDynamicCodeAttribute))]` and returns
   `RuntimeFeature.IsDynamicCodeSupported`. The trim/AOT analyzers treat any branch guarded by it as
   unreachable when dynamic code is unsupported, so `CreateCompiledBackend()` (and everything it
   transitively reaches — `CompiledBackend`, the cache, the lowering, the `Expression.Compile` call, and
   every `CompiledExpression*` / `CompiledScalarOps` / `CompiledVectorAccess` / `FusedRowKernel` type) is
   dead-code-eliminated **without an IL3050 warning**. Because those tier types are reachable *only*
   through this one guarded branch, eliminating the branch eliminates the entire subgraph.
3. **The runtime value is hard-wired on AOT.** `RuntimeFeature.IsDynamicCodeSupported` is `false` under
   NativeAOT, so `Select` returns `InterpretedVectorizedBackend.Instance` and the compiled path is never
   taken at runtime either.

### How the publish proves it

`.github/workflows/aot.yml` publishes the representative executor with
`dotnet publish src/DeltaSharp.Executor -c Release -r linux-x64 -p:PublishAot=true -warnaserror`:

- `-warnaserror` fails the publish on **any** IL2xxx/IL3xxx trim/AOT warning, so an un-guarded reach into
  the compiled tier would break CI.
- The executable is run; its output must contain `interpreted-vectorized (dynamic-code=False)` — proving
  the interpreter is selected under AOT.
- A **positive control** asserts `strings -a <image>` *can* see a reachable backend type name
  (`InterpretedVectorizedBackend`); if it cannot, the elision check would be vacuous and the job fails
  closed.
- The elision assertion then requires `strings -a <image>` to **not** find `CompiledBackend` — proving
  the optional tier (and, transitively, every type only reachable through it) was eliminated from the
  native image.

## The differential parity oracle

`CompiledExpressionFusionTests` is the STORY-03.4.2 proof of AC4. Its method is **differential**: build
the interpreted evaluator (`ExpressionEvaluators.Build(expr, schema, "interpreted-vectorized",
OperatorKind.Project)`) **and** the compiled evaluator (the real entry point —
`new CompiledBackend().BuildExpressionEvaluator(expr, schema, OperatorKind.Project)`), evaluate both over
the *same* batch, and assert the result vectors are **byte-identical** lane-by-lane: equal `Type`, equal
length, equal per-row validity, and equal stored carrier — with **float/double compared by raw IEEE bits**
(`SingleToInt32Bits`/`DoubleToInt64Bits`) so `-0.0` and `NaN` payloads are held to bit equality, and
decimals compared by stored mantissa (compact `long` / wide `Int128`).

**Non-vacuity.** Every parity assertion first asserts the compiled evaluator *is* a
`CompiledExpressionEvaluator` (`Assert.IsType<…>`), proving a real fused delegate — not a silent
fallback — was exercised. So a mutated lowering either changes a produced value (failing the lane
comparison) or disables fusion (failing the type assertion); either way the suite goes red. This was
verified by injecting a fault — swapping the `LessThan` lowering to emit `GreaterThan` — which turned
five parity cases red; reverting restored green.

**Gating.** Every compiled-tier test early-returns when `!RuntimeFeature.IsDynamicCodeSupported` (the
established `ExecutionBackendTests` convention), so on a NativeAOT host — where there is nothing to
compare against — the suite is inert rather than failing. The test project does **not** enable the
trim/AOT analyzers (only `src/` production assemblies do), so it may call the `[RequiresDynamicCode]`
entry points directly without annotation.

**Coverage** (80 tests):

| Area | What it proves |
| --- | --- |
| Arithmetic | `Add/Subtract/Multiply/Remainder` over int/long/short/byte; `/`→double; float `Add/Multiply/Remainder`; double `Add/Subtract/Multiply/Divide/Remainder`; decimal `Add/Subtract` (compact) + `Multiply` (wide `Int128` result); nested trees; integer & null literals. |
| Comparison | all six operators over int; decimal, boolean, and mixed `date↔timestamp` kinds; literal operands. |
| Boolean (Kleene 3VL) | `And`/`Or`/`Not` across the full true/false/null matrix; nested logical; predicates built from comparisons. |
| Cast | int→{long,double,short,bool,decimal}; double→{int(trunc),float}; bool→{int,decimal}; decimal→{long(trunc),wide}; date↔timestamp. |
| Null / null-check | null propagation through every kind; `IsNull`/`IsNotNull`; `IsNotNull` over arithmetic. |
| ANSI errors | integer overflow, cast overflow (the `2^63` boundary), double divide-by-zero, integer remainder-by-zero — **both tiers throw the same type with byte-identical messages** (single-error batches). |
| Legacy (non-ANSI) | overflow/divide-by-zero/cast-overflow → SQL `NULL` on the same lanes. |
| NaN / `-0.0` | arithmetic producing `-0.0`, `NaN`/`Inf` propagation, and comparisons across `NaN`/`±0.0` — bit-exact. |
| Selection / slice | contiguous selection, **unordered** selection, and slice — identical logical-order output. |
| Wide randomized | seeded 257-row integral and 211-row double batches with ~12% nulls over compound expressions — strong, reproducible non-vacuity. |
| Fallback | string comparison, `IsNull` over string, decimal divide, unsupported cast (`double→decimal`), and a bad column reference — fusion declines and the interpreter handles it (or both reject identically). |
| Cache | compile-once-per-shape + reuse across instances; distinct shapes compile separately; bounded eviction; `±0.0` literals key distinctly. |

### Mapping to STORY-03.4.2 acceptance criteria

| AC | Mechanism | Evidence |
| --- | --- | --- |
| **AC1** — dynamic code unsupported ⇒ interpreter, no compiled delegate | `IsCompiledBackendAvailable` feature guard; `Select` returns the interpreter; the whole tier is elided on AOT | `ExecutionBackendTests.Select_Default_TracksRuntimeDynamicCodeCapability`; the AOT elision job. |
| **AC2** — same shape + type/nullability ⇒ cached delegate reused | `CompiledExpressionKey` structural key + `CompiledExpressionCache.GetOrCompile` | `Cache_CompilesOncePerShape_ReusesAcrossInstances`. |
| **AC3** — bounded eviction + compile/hit/eviction metrics | `DefaultCapacity` + `EvictIfOverCapacity` + `CompiledExpressionCacheMetrics` | `Cache_BoundedCapacity_EvictsOldestShapes`, `Cache_DistinctShapes_CompileSeparately`. |
| **AC4** — interpreter vs compiled: values, validity, errors, row counts match | the differential harness (byte-identical, incl. row count via vector length) | the whole parity suite (80 tests). |
| **AC5** — dynamic-code APIs guarded with analyzer-visible attributes/feature switches | `[RequiresDynamicCode]` + `[FeatureGuard]` + `-warnaserror` publish | the AOT publish + `strings` elision proof. |

## Deferred scope

- **Operator wiring is deferred.** `CompiledBackend.Open` still delegates operator execution to
  `InterpretedOperators` — wiring `BuildExpressionEvaluator` into the operator-owned filter/project batch
  streams belongs to the operator layer (the `InterpretedOperators` / `Interpreted{Filter,Project}Stream`
  code that PR #148 owns), which is **out of scope** for this story. STORY-03.4.2 delivers the fusable
  *expression* evaluator, its cache, and the parity proof; the operators adopt it without semantic change
  because the compiled evaluator is a drop-in `ExpressionEvaluator`.
- **Expression fusion, not operator fusion.** This tier fuses the node tree *within* one expression into
  one per-row delegate. Cross-operator fusion (e.g. fusing a filter predicate into a downstream
  projection) is a later optimization and is not attempted here.
- **#154 (STORY-03.5.2) broadens the oracle.** The interpreter-vs-compiled *backend* parity suite adds
  golden physical plans for every operator, randomized-expression property checks over v1 types,
  documented skip reasons when dynamic code is disabled, and richer mismatch diagnostics (plan shape,
  seed, first mismatching row). This story's suite is the per-expression foundation it builds on.
- **Exception-ordering caveat.** The interpreter is column-at-a-time; the compiled kernel is
  row-at-a-time interleaved. For a batch containing **multiple** error rows the *exact* row that throws
  may differ between tiers, though the exception **type** always matches. The error-path parity tests
  therefore use single-error batches, where the throwing row — and thus the message — is identical.
- **Permanently interpreter-only shapes.** Decimal `Divide`/`Remainder` and `float`/`double → decimal`
  casts are deferred by the type system / v1 cast matrix; string and binary have no fixed-width carrier.
  These always take the fallback path and are never fused.

---

Every claim above is asserted against the code as of this story: the lowering rules match
`CompiledExpressionLowering`, the cache semantics match `CompiledExpressionCache`, the key match
`CompiledExpressionKey`, the gate/elision match `ExecutionBackends` + `.github/workflows/aot.yml`, and the
parity strategy matches `CompiledExpressionFusionTests`. Keep them in sync.
