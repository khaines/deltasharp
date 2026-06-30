# Interpreted vectorized expression evaluator (STORY-03.4.1)

> **Status:** living document. Created with
> [STORY-03.4.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-03-vectorized-execution-backend.md#story-0341-interpreted-batch-expression-evaluator)
> (the AOT-clean interpreted batch expression evaluator). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (pluggable vectorized interpreter is the default
> backend **and** the parity oracle; codegen is an additive tier) and
> [ADR-0014](../../adr/0014-target-framework-aot.md) (NativeAOT executor). Builds on
> [execution-operators.md](execution-operators.md) (the scan/filter/project operators whose
> `ColumnReference`-only restriction this story lifts), [type-system.md](type-system.md) (EPIC-02
> numeric promotion, decimal arithmetic, ANSI overflow), [null-validity-model.md](null-validity-model.md)
> (#143 validity + Kleene 3VL), and [columnar-contracts.md](columnar-contracts.md) (the `ColumnBatch` /
> `ColumnVector` / `SelectionVector` surface). Update it whenever an expression node, a kernel's null /
> ANSI contract, or the cast matrix changes.

STORY-03.2.1 left filter and project able to evaluate only a boolean `ColumnReference` predicate and
`ColumnReference` projections; everything richer raised `UnsupportedOperatorException` and was deferred
here. This story adds an **interpreted, vectorized expression evaluator** that turns a `ColumnBatch`
into a result `ColumnVector` (values **and** validity), selection-aware, with no runtime code
generation. It is the ADR-0001 **correctness ground truth**: the STORY-03.4.2 `CompiledBackend`
expression-fusion tier must match it bit-for-bit.

The model is the standard one for native analytic engines (DuckDB / Velox / DataFusion): pre-built
per-node kernels dispatch on resolved column types and walk a batch lane-by-lane. Everything here lives
in the unshipped `DeltaSharp.Engine` assembly under `src/DeltaSharp.Engine/Execution/` (expression
nodes) and `src/DeltaSharp.Engine/Execution/Expressions/` (the evaluator tier); `public` is an
engine-internal seam, not shipped surface.

## Two layers: immutable nodes, bound kernels

| Layer | Where | Role |
| --- | --- | --- |
| **Expression nodes** | `Execution/{Literal,ArithmeticExpression,ComparisonExpression,LogicalExpression,CastExpression,IsNullExpression}.cs` | Immutable `PhysicalExpression` subtypes. Resolve their result `Type`/`Nullable` (and an internal eval-kind) **in the constructor** — building the tree does **no row work** (the lazy/eager invariant). |
| **Evaluator tier** | `Execution/Expressions/` | One bound `ExpressionEvaluator` kernel per node, plus shared helpers (`ScalarReader`, `VectorMaterializer`, `BatchEvaluationMemory`) and the `ExpressionEvaluators.Build` factory. Produced at operator **Open**; runs at **pull**. |

Separating the nodes (what to compute) from the evaluators (how, over this input schema) keeps the plan
immutable and lets the compiled tier later bind a different kernel to the *same* node without changing
plan semantics.

### Node taxonomy

| Node | Shape | Result type | Nullable when |
| --- | --- | --- | --- |
| `Literal` | constant scalar (or typed SQL `NULL`) broadcast across the batch | the literal's declared type | it is the `NULL` literal |
| `ArithmeticExpression` | `left op right`, `op ∈ {+,-,*,/,%}` | Spark numeric promotion (see below) | either operand nullable, **or** `Legacy` mode (overflow/÷0 → `NULL`) |
| `ComparisonExpression` | `left op right`, `op ∈ {=,<>,<,<=,>,>=}` | nullable `boolean` | either operand nullable |
| `LogicalExpression` | `AND`/`OR` (binary), `NOT` (unary) | nullable `boolean` | any operand nullable |
| `CastExpression` | `CAST(child AS target)` | `target` | child nullable, **or** `Legacy` mode on a non-identity (lossy) cast |
| `IsNullExpression` | `child IS [NOT] NULL` | **non-nullable** `boolean` | **never** (inspects validity; does not propagate) |

`ColumnReference` (STORY-03.2.1) remains the only leaf that reads input; `Literal` is the only other
leaf. Arithmetic/comparison/cast/null-check carry exactly their operand sub-trees, so the evaluator is a
simple recursive bind.

## The vectorized evaluation model

Every kernel implements:

```csharp
ColumnVector Evaluate(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken ct);
```

and obeys one uniform contract:

- **In:** the operator's `ColumnBatch`. **Out:** a `ColumnVector` of exactly `batch.LogicalRowCount`
  rows, in **logical (selection) order**, carrying values **and** validity.
- **Selection- and slice-aware for free.** Leaves read through the batch's *logical* view
  (`batch.SelectedColumn(ordinal)`), and every computed kernel iterates `[0, LogicalRowCount)` using the
  per-row logical API (`GetValue<T>(i)` / `IsNull(i)` / `GetBytes(i)`) — never the contiguous
  `GetValues<T>()` span, which a selected view cannot expose. A selection vector, a slice offset, or a
  composition of both is therefore transparent: only selected logical rows are processed, and output row
  order is the selection order (deterministic).
- **Leaves are zero-copy; computed nodes materialize contiguous output.** `ColumnReference` returns the
  logical view directly (no allocation, no reservation). Arithmetic/comparison/logical/cast/null-check
  build a fresh contiguous `ManagedFixedWidthColumnVector<T>` (or variable-width vector) in append-only
  logical order, so the result re-exposes the fast `GetValues<T>()` path downstream.

### Reading and writing lanes

- **`ScalarReader`** does per-row, no-boxing reads: `ReadInt64`/`ReadDouble`/`ReadSingle`/`ReadDecimal`/
  `ReadBool`/`ReadBytes`. It centralizes two storage subtleties: Spark `tinyint` is **signed** but stored
  as a CLR `byte`, so byte lanes reinterpret through `sbyte`; and a `DecimalType` mantissa is a `long`
  when *compact* (precision ≤ 18) or an `Int128` when wide — both surface as a scale-tagged `DecimalValue`.
- **`VectorMaterializer`** writes results into their storage shape (signed tinyint via `unchecked((byte)`,
  compact vs wide decimal) and gathers a selection-bearing column into a contiguous vector.
- **`BatchEvaluationMemory`** is the per-pass reservation ledger over the run's `IExecutionMemory`
  budget: every materialized intermediate or output reserves its bounded footprint **before** it
  allocates, and the whole pass is released at once when the operator is done with the batch. The
  interpreter materializes a vector per computed node, so its footprint is ~ `tree-size × batch-rows` —
  bounded, but larger than the inputs; STORY-03.4.2's fused tier collapses these intermediates.

### Building the kernel tree (AOT-clean dispatch)

`ExpressionEvaluators.Build(expression, inputSchema, backendName, kind)` recursively binds nodes to
kernels via a plain `switch` on node type — **no reflection, no IL emit, no `Expression.Compile`**. It
validates `ColumnReference` ordinals/types against the input schema and rejects v1 capability gaps at
**Open** time with `UnsupportedOperatorException` (never a silent row-at-a-time fallback, never a
mid-stream failure): decimal `/` and `%` (their result *types* are defined but value rounding is
deferred by the type system), and any cast outside the v1 matrix. Building binds and validates only —
it moves no rows.

## Null and ANSI semantics per node

DeltaSharp **never silently wraps**. Under `AnsiMode.Ansi` an overflow/out-of-range/÷0 **throws**; under
`AnsiMode.Legacy` it yields SQL `NULL`. `AnsiMode` is a value carried on the node (`ArithmeticExpression.Mode`,
`CastExpression.Mode`), not ambient state.

| Node | Null rule | ANSI / numeric contract |
| --- | --- | --- |
| Arithmetic | **propagate-on-any-null** (#143): any null operand → `NULL` | Integral `+ - *` accumulate in `checked` 64-bit then range-check to the result type (overflow → throw/`NULL`). `/` on non-decimals → **double** (Spark), `÷0` → throw/`NULL` (**not** IEEE ∞). `%` keeps operand kind; integral `%` guards `b == -1` so `long.MinValue % -1` is `0`, `÷0` → throw/`NULL`. Float/double `+ - * /` are IEEE (∞/NaN flow through). Decimal `+ - *` are exact via `DecimalValue`, then fitted to the Spark result type (HALF_UP); overflow → throw/`NULL`. |
| Comparison | **propagate-on-any-null** → `NULL` | Numeric lanes compare at the wider common type; **double** lanes use Spark order (`NaN == NaN`, `NaN` greatest, `-0.0 == +0.0`); decimals rescale to a common scale and compare exactly; strings/binary compare unsigned byte-lexicographically; a `date` vs `timestamp` promotes the date to its UTC-midnight micros. |
| Logical | **Kleene 3VL** (#143) — null = "unknown" | `FALSE AND NULL = FALSE`, `TRUE OR NULL = TRUE`, `NOT NULL = NULL`. Delegated to `NullPropagation.KleeneAnd/Or/Not` so the operator and scalar paths share one truth table. |
| Cast | null → null | Lossy conversions follow the EPIC-02 contract (throw/`NULL`); see the matrix below. |
| `IS [NOT] NULL` | **never null** | Reads the validity bit and emits a defined boolean for every lane. |

Arithmetic and comparison reduce nulls to the validity bitmap up front (`HasNulls` short-circuits the
common no-null batch); logical reads each operand as a `bool?` and defers to the Kleene reference.

## The v1 cast matrix

`CastEvaluator.IsSupported(source, target)` gates the matrix at Build; unsupported pairs raise
`UnsupportedOperatorException`.

| Target ← Source | Supported in v1 | Semantics |
| --- | --- | --- |
| identity | yes | copy through |
| numeric/boolean core `{bool, byte, short, int, long, float, double, decimal}` — all directions | yes | `bool` reads as 0/1; `→ bool` is `!= 0`; float/double `→` integral **truncates toward zero** (NaN/∞/out-of-range → throw/`NULL`); decimal `→` integral is `unscaled / 10^scale` (truncate) + range-check; `→ decimal` rounds HALF_UP to the target scale and enforces precision |
| `date ↔ timestamp` | yes | date → `day × MicrosPerDay`; timestamp → `floor(micros / MicrosPerDay)` |
| any string/binary cast | **deferred** | not in v1 |
| numeric ↔ temporal (beyond `date ↔ timestamp`) | **deferred** | not in v1 |
| `float`/`double → decimal` | **deferred** | not in v1 |

Deferred pairs fail fast at Open, so a plan that needs them is rejected before any row work — the same
fail-fast discipline as the operator layer.

## Filter / project integration

The operators keep their STORY-03.2.1 zero-copy fast paths and add a general path, chosen at Open:

- **Filter.** A boolean `ColumnReference` predicate keeps the direct value-span selection path. A richer
  predicate is bound to an `ExpressionEvaluator`; at pull the filter evaluates it into a transient
  boolean vector, collects the **logical** indices where the value is `true` and not null, and exposes
  them via `ColumnBatch.WithSelection` (zero-copy; composes over any prior selection). The predicate
  vector is scratch: its `BatchEvaluationMemory` is released *before* the surviving selection vector is
  reserved, so peak memory is the larger of the two, not their sum. Rows whose predicate is `NULL` do
  not pass (Spark `WHERE`).
- **Project.** An all-`ColumnReference` projection stays a zero-copy reorder/rename (output column *i*
  *is* the referenced input column; input selection preserved). If any projection is computed, project
  emits a **selection-free** batch of `LogicalRowCount` rows: computed columns are materialized by their
  evaluator; a `ColumnReference` column is shared zero-copy when the input has no selection, or gathered
  into a fresh contiguous vector when it does — so the emitted batch carries no selection and stays
  `GetValues<T>`-capable downstream. The pass's reservation is held in a field assigned **before** the
  build loop, so a refused reservation or a throwing evaluator mid-loop is still released by `Dispose` /
  the next pull (no leak).

Both operators preserve the cross-cutting contract: lazy/pull-based (Open moves no rows), per-batch
bounded memory (reserve before allocate; release the previous batch at the top of the next pull),
cancellation at batch boundaries, and fail-fast on unsupported shapes.

## AOT-clean (no-codegen) guarantee

This is the interpreted tier: **no member emits IL or uses reflection.** Dispatch is a `switch` on node
type to a pre-built kernel; the banned `Expression.Compile`, `System.Reflection.Emit`,
`Activator.CreateInstance`, and `Type.GetType` (BannedSymbols.txt) appear nowhere on the path. The Engine
builds with `EnableTrimAnalyzer`/`EnableAotAnalyzer`/`EnableSingleFileAnalyzer` on, so a dynamic-code
dependency would break the build; the authoritative proof is `dotnet publish src/DeltaSharp.Executor
-c Release -p:PublishAot=true -warnaserror`, which links the whole reachable Engine graph through ILC.
No new node or kernel carries `[RequiresDynamicCode]`, and a test (`InterpretedTier_DeclaresNoDynamicCodeRequirement`)
asserts that invariant across the evaluator tier and the expression nodes.

## How STORY-03.4.2 layers compiled fusion on top

The compiled tier is **additive**, not a rewrite:

- The **expression nodes are the contract** and stay immutable. `CompiledBackend` binds a *different*
  evaluator to the same tree — fusing a whole sub-tree into one `Expression.Compile()`'d, delegate-cached
  `Func` over a batch — without changing plan meaning.
- This interpreted evaluator is the **parity oracle**: the compiled path must produce identical values
  and validity (including ANSI throw vs `Legacy` null, NaN ordering, decimal rounding). The shared
  `ExpressionEvaluators.Build` boundary and the uniform `Evaluate` contract make a differential test
  (same node tree → both backends → assert equal vectors) mechanical.
- The compiled tier is gated on `RuntimeFeature.IsDynamicCodeSupported` and annotated/elided under AOT
  (`[RequiresDynamicCode]` / `[FeatureGuard]`), so `PublishAot` keeps only this interpreter. The
  interpreter needs no codegen to be correct — codegen is a throughput fast-path, never a dependency
  (ADR-0001).

## What is deferred

- **Compiled expression fusion** (STORY-03.4.2): `Expression.Compile()` delegate-cached kernels behind
  the dynamic-code feature gate, with a differential parity oracle against this interpreter.
- **More expression families:** general scalar functions/UDFs, `CASE`/`coalesce`/`in`, string/temporal
  functions — and the deferred cast pairs (string/binary, numeric↔temporal beyond `date↔timestamp`,
  float/double→decimal) and decimal `/`,`%` value rounding.
- **SIMD acceleration** of the kernels (FEAT-03.3): today's lane loops are scalar; the columnar layout
  is already SIMD-friendly and is the natural next optimization, parity-checked against this baseline.
