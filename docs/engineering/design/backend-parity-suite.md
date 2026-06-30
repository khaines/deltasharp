# Interpreter-vs-compiled backend parity suite (STORY-03.5.2)

> **Status:** living document. Created with
> [STORY-03.5.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-03-vectorized-execution-backend.md)
> (interpreter-vs-compiled backend parity suite). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md): the vectorized **interpreter is the default backend
> *and* the parity oracle**, and the compiled tier is "an additive fast-path, never a dependency" that
> "must match it bit-for-bit." Builds directly on
> [compiled-expression-fusion.md](compiled-expression-fusion.md) (STORY-03.4.2, #152 — the compiled
> tier under test and its existing curated expression-level differential tests — 61 differential cases
> within an 82-test fusion suite), the interpreted evaluator
> (STORY-03.4.1 — the reference), and [relational-operators.md](relational-operators.md) (the seven v1
> operator shapes). Update it whenever the seam (where the tiers can diverge), the fusable surface, the
> exception-parity scope, the generator grammar, or the skip policy changes.
>
> **Owner:** reliability-test-chaos-engineer. **Suite:** `tests/DeltaSharp.Engine.Tests/Execution/Parity/`.

## 1. Why this suite exists (the ADR-0001 obligation)

ADR-0001 adopts two execution backends behind `IExecutionBackend`: the always-present
`InterpretedVectorizedBackend` ("default and reference … the correctness ground truth") and the
optional `CompiledBackend`, enabled only when `RuntimeFeature.IsDynamicCodeSupported` is true. The ADR
is explicit that this "**requires a differential parity oracle**: the interpreter is ground truth and
the compiled tier must match it bit-for-bit." This suite is that oracle at the **operator/plan** and
**randomized-expression** levels — the codegen tier remains an *optimization only*, never a correctness
dependency, or this suite fails.

The acceptance bar is **value + validity byte-identity** (bit-exact for `float`/`double`, so `-0.0` and
`NaN` payloads are held identical, not merely numerically equal) plus a **scoped** exception-parity
guarantee (§5).

## 2. The seam: where the two backends can actually diverge

Operator execution is **shared**. `CompiledBackend.Open(op, ctx)` delegates verbatim to
`InterpretedOperators.Open(Name, op, ctx)` — the same dispatch the interpreter runs (see
`CompiledBackend.cs` and `InterpretedOperators.cs`). The compiled tier's only value-add is fusing hot
**scalar expressions** into a JIT-compiled `FusedRowKernel` (`CompiledExpressionLowering`,
`CompiledExpressionEvaluators`). Therefore:

> Interpreter-vs-compiled divergence can only arise in **expression evaluation**, never in operator
> control flow, grouping, sorting, join matching, or partition routing — those are literally the same
> code in both backends.

Two consequences shape the suite:

1. **Expression-bearing plans are mandatory for non-vacuity.** A golden plan whose predicate/projection/
   key is a bare `ColumnReference` exercises a path that is identical *by construction* and proves
   nothing about codegen. Every golden plan here carries a **non-trivial fusable expression** (a filter
   predicate, a computed projection, an aggregate input/group key, a sort key, a join key, an exchange
   key) so the differential path is real.
2. **Today the operator layer still binds the *interpreted* evaluator for both backends.**
   `InterpretedOperators.Open` builds expression evaluators via `ExpressionEvaluators.Build` (the
   interpreted path) regardless of which backend opened it; wiring the compiled evaluator into the
   operator-owned filter/project/key streams is deferred (noted in `CompiledBackend.BuildExpressionEvaluator`'s
   remarks). So the suite asserts parity on **two complementary fronts**:
   - **Operator-output parity** — run the whole plan under `ExecutionBackends.Select(ForceInterpreted)`
     and under `ExecutionBackends.Select()` (compiled-selected on a JIT host) and assert byte-identical
     output rows. This validates the plan executes and pins the interpreter ground truth.
   - **Expression-path parity** — extract each operator's bearing expression and evaluate it on both the
     interpreted evaluator (`ExpressionEvaluators.Build`) **and** the compiled evaluator
     (`CompiledBackend.BuildExpressionEvaluator`) over the operator's actual input batch, asserting
     value/validity identity **and** non-vacuity (the compiled side is a real
     `CompiledExpressionEvaluator`). This is the front an injected lowering bug trips, and the front the
     operator layer will run once it wires the compiled evaluator.

This is also why the suite is robust against a *future* change that wires the compiled evaluator into
operators: the expression-path differential already covers exactly those expressions, and the
operator-output comparison flips from "identical by construction" to "identical because the fused kernel
matches" without any test change.

## 3. Coverage matrix

### 3.1 Golden operator plans (AC1) — `BackendParityGoldenPlanTests`

| Operator kind   | Plan (expression-bearing)                                   | Bearing expression(s) differentially checked |
|-----------------|-------------------------------------------------------------|----------------------------------------------|
| `Scan`          | `InMemoryScanOperator` over a fixed batch                   | none (expression-free; faithful passthrough) |
| `Filter`        | `(a + b) > c`                                               | the predicate                                |
| `Project`       | `a + b`, `cast(a → long)`, `c > 0`                          | each projection element                      |
| `Aggregate`     | group by `k % 4`; `SUM(a*2)`, `COUNT(*)`, `MAX(d)`          | group key + the `SUM` input expression       |
| `Sort`          | `(a + b)` DESC NULLS LAST, then `c` ASC                     | the computed sort key                        |
| `Join`          | inner join on `(la + lb) == (ra + 0)`                       | left key (over left batch) + right key       |
| `ExchangeLocal` | hash-partition by `(a * b)` into 4 partitions               | the partition key                            |

Scan is the one expression-free operator; its parity is faithful source passthrough and is asserted as
operator-output identity only. Every other plan carries a fusable expression so the compiled path is
genuinely exercised.

Error-parity golden cases (AC1 "errors match"):

| Case                                  | Batch shape          | Assertion (scope per §5)                                   |
|---------------------------------------|----------------------|-----------------------------------------------------------|
| Filter ANSI `(a+b) > 0` overflow      | single-error         | both tiers throw the **identical** `ArithmeticOverflowException` (type + message) |
| Project ANSI `(a+b)+(c%d)`            | multi-error-kind     | both tiers raise **some** ANSI arithmetic error (type/message may differ) |
| `(a + b)` with out-of-range column    | build-time           | both tiers reject with the **identical** `ArgumentException` |

### 3.2 Randomized expressions over v1 types (AC2) — `BackendParityRandomizedExpressionTests`

A seeded generator (`BackendParityGenerator`, §4) synthesizes a schema + expression tree + batch over
the v1 fixed-width carrier types (`bool, tinyint, short, int, long, float, double, decimal(10,2), date,
timestamp`). 250 fixed seeds drive three theories:

- `Randomized_CompiledEqualsInterpreter_ValueAndValidity` — the core differential (compiled-gated).
- `Randomized_CompiledEvaluatorIsNonVacuous` — proves each generated tree is served by a real
  `CompiledExpressionEvaluator`, not a silent fallback (compiled-gated).
- `Randomized_InterpreterAlwaysProducesWellFormedVector` — interpreter-only ground-truth coverage that
  runs on **every** host (AC3, §6).

### 3.3 Relationship to #152

The #152 `CompiledExpressionFusionTests` are **expression-level** differential tests over a *curated*
case table (61 differential cases within an 82-test fusion suite: arithmetic/comparison/boolean/cast/null,
NaN/`-0.0` edges, decimal carriers, selection/slice views, ANSI single- and multi-error). This suite
**complements** them by adding the two things they do not cover:

1. **Operator/plan-level golden parity** across all seven v1 operator kinds (§3.1) — proving the
   expression seam is exercised through the actual `PhysicalOperator` shapes a planner emits.
2. A **deterministic randomized** expression+batch generator (§4) — structure-aware fuzzing that finds
   shapes a hand-written table never enumerates.

It deliberately does **not** re-curate #152's arithmetic/decimal tables; the randomized generator runs
in Legacy ANSI mode (overflow → NULL) precisely so it is a pure value/validity differential and leaves
ANSI exception parity to the golden cases and #152.

## 4. Deterministic generator design (AC5)

`DeterministicRng` is a **SplitMix64** integer recurrence — *not* `System.Random`, whose sequence is not
contractually stable across .NET versions. The same `seed` therefore yields byte-identical draws on
every runtime and CI run, so a run reduces entirely to its seed, which is emitted in every diagnostic
(AC4) and is the replay key for a regression.

`BackendParityGenerator.Generate(seed)` is a pure function of the seed:

- **Schema** — a fixed 13-column schema spanning every v1 fixed-width type, with one **non-nullable**
  column (`i2`) to exercise the no-null fast path and the rest nullable to exercise validity.
- **Batch** — 1–256 rows; ~1/6 nulls per nullable cell; integer/long **extremes** (`MaxValue`/`MinValue`)
  to drive the Legacy overflow→NULL path; and `NaN`/`±∞`/`±0.0` for float/double so bit-exact
  comparison is meaningfully tested.
- **Expression tree** — a **type-directed** grammar (max depth 3) that emits only trees satisfying
  `CompiledExpressionEvaluators.CanFuse`, so the compiled evaluator is always a real
  `CompiledExpressionEvaluator` (non-vacuous):
  - numeric subtrees: arithmetic `+ - * / %` over numeric operands, and core numeric casts;
  - boolean subtrees: comparisons (numeric/decimal/temporal), `AND`/`OR`/`NOT`, `IS [NOT] NULL`,
    `cast(numeric → bool)`;
  - **every arithmetic node is built in `AnsiMode.Legacy`**, so overflow and zero-division become SQL
    `NULL` rather than throwing — making each randomized case a pure value+validity comparison with no
    exception non-determinism.
  - decimal divide/remainder (rejected by both tiers) and string/binary lanes (interpreter-only) are
    intentionally excluded from the grammar — they are covered by #152 and the golden fallback cases.

Determinism is itself tested: `Generator_IsDeterministic_SameSeedSameTreeSchemaAndBatch` rebuilds a case
twice from one seed and asserts identical tree, row count, and every cell; and
`Differential_IsDeterministic_RepeatedRunsAgreeForFixedSeed` re-runs the full differential 3× per seed.

## 5. Exception-parity scope (the engine's actual guarantee)

The parity oracle guarantees value + validity byte-identity and **single-error-batch** exception parity.
For **multi-error-kind** ANSI batches the throwing row and exception type/message **may differ** across
tiers: the interpreter is subtree/child-major (it evaluates a whole subtree across all rows before the
next) while the compiled kernel is row-major (per row: left then right), so which fault is reached first
is eval-order-dependent. This is documented and intended in
[compiled-expression-fusion.md](compiled-expression-fusion.md). The suite scopes its assertions exactly
to that guarantee — asserting more would assert an invariant the engine does not hold:

| Batch shape          | Oracle method                          | Assertion                                              |
|----------------------|----------------------------------------|--------------------------------------------------------|
| No error (Legacy)    | `AssertValueParity`                    | value + validity byte-identical                        |
| Single error (ANSI)  | `AssertSingleErrorParity<T>`           | **identical** exception type **and** message           |
| Multi-error (ANSI)   | `AssertBothRaiseAnsiArithmeticError`   | both raise **some** ANSI arithmetic error (no type-eq) |
| Invalid plan         | `AssertIdenticalBuildRejection<T>`     | identical build-time exception type + message          |

## 6. Skip-on-no-dynamic-code policy (AC3)

Compiled-tier cases are gated by `DynamicCodeFactAttribute` / `DynamicCodeTheoryAttribute`. Each sets
xUnit's `Skip` **at discovery** from `RuntimeFeature.IsDynamicCodeSupported` — the *same* gate
`ExecutionBackends.Select()` uses in production:

- On a **dynamic-code host** (CoreCLR JIT, this CI host): `Skip == null`, the compiled cases **run**, and
  the differential is real (proven non-vacuous by the `IsType<CompiledExpressionEvaluator>` assertions).
- On a **NativeAOT / dynamic-code-disabled host**: the compiled cases are reported **Skipped** with a
  documented capability reason (`DynamicCodeSkip.Reason`, which names the gate), exactly mirroring the
  runtime that would have *elided* the compiled tier — while the interpreter-only theory
  (`Randomized_InterpreterAlwaysProducesWellFormedVector`) and the generator/determinism facts always
  run, so interpreter coverage stays green.

This is verified by `BackendParitySkipPolicyTests` (the gate tracks `RuntimeFeature`, the reason is
documented and names the gate, and the attributes set `Skip` iff dynamic code is unsupported). No
external skippable-fact package is used; the policy is a few lines in `DynamicCodeFactAttribute.cs`.

## 7. Mismatch diagnostics format (AC4)

Every mismatch raises a `XunitException` whose message is a replay record. `BackendParityOracle.Mismatch`
emits, in order: **summary, plan shape, backend selection, seed** (hex, or "n/a (fixed golden plan)"),
**schema, expression tree** (an S-expression rendering via `Describe`), **row count**, and the **first
mismatching row** — its index, the full **input row** (every column, bit-exact), and the **interpreted vs
compiled** produced values. Operator-output mismatches additionally carry the one-line **plan shape**
(`DescribePlan`) and the offending output column. A reader can reconstruct the exact failing case from
the seed alone (randomized) or the named golden plan (golden).

Example skeleton:

```
Backend parity mismatch (interpreter is the ADR-0001 oracle; compiled tier must match).
  summary         : value differs at row 7
  plan shape      : value(numeric)
  backend select  : interpreted-vectorized (oracle) vs compiled (CompiledBackend.BuildExpressionEvaluator)
  seed            : 0x0123456789ABCDEF
  schema          : b0:boolean,i0:int,...,by0:tinyint
  expression tree : (* (+ col[1]:int col[2]:int [Legacy]) col[5]:double [Legacy]):double
  rows            : 193
  first mismatch  : row 7
      input row   : { b0=true, i0=5, i1=-3, ..., by0=-12 }
      interpreted : -6 (bits 0x...)
      compiled    : -7 (bits 0x...)
```

## 8. CI determinism (AC5)

Seeds are fixed in `Seeds()`; SplitMix64 is runtime-independent; the generator and oracle hold no
ambient state (no clock, no `Guid`, no default `Random`). Repeated runs with the same seeds are
byte-identical — verified locally by running the suite three times and diffing the per-test outcomes,
and asserted in-suite by the determinism facts in §4.

## 9. AC coverage

| AC | Requirement | Where satisfied |
|----|-------------|-----------------|
| **AC1** | Golden plans for scan/filter/project/aggregate/sort/join/exchange-local: with both backends enabled, outputs **and** errors match the interpreter ground truth | `BackendParityGoldenPlanTests` — operator-output parity (`AssertPlanOutputsIdentical`) for all 7 kinds + expression-path parity per plan; error parity via single-error / multi-error / build-rejection cases (§3.1, §5) |
| **AC2** | Randomized expressions over v1 types: interpreter and compiled evaluators over the same batches → values **and** validity bitmaps identical | `BackendParityRandomizedExpressionTests.Randomized_CompiledEqualsInterpreter_ValueAndValidity` over 250 seeds via `BackendParityGenerator` + `BackendParityOracle.AssertValueParity` (bit-exact carriers) |
| **AC3** | Dynamic code disabled: compiled cases reported **Skipped** with a documented reason; interpreter coverage stays green | `DynamicCodeFactAttribute`/`DynamicCodeTheoryAttribute` set `Skip` from `RuntimeFeature.IsDynamicCodeSupported`; interpreter-only theory + generator facts always run; verified by `BackendParitySkipPolicyTests` (§6) |
| **AC4** | Parity mismatch diagnostics include plan shape, backend selection, seed, schema, expression tree, and first mismatching row | `BackendParityOracle.Mismatch` / `DescribePlan` / `Describe` (§7) |
| **AC5** | CI determinism: results deterministic across repeated runs with fixed seeds | `DeterministicRng` (SplitMix64), fixed `Seeds()`, `Generator_IsDeterministic_*` + `Differential_IsDeterministic_*` (§4, §8) |

## 10. Files

- `Execution/Parity/DeterministicRng.cs` — seeded SplitMix64 PRNG.
- `Execution/Parity/BackendParityGenerator.cs` — deterministic schema + expression-tree + batch generator.
- `Execution/Parity/BackendParityOracle.cs` — the differential oracle: value/validity comparison,
  non-vacuity, exception scope, and replay diagnostics (`Describe`/`DescribePlan`).
- `Execution/Parity/DynamicCodeFactAttribute.cs` — the dynamic-code skip policy (`DynamicCodeSkip`,
  `DynamicCodeFactAttribute`, `DynamicCodeTheoryAttribute`).
- `Execution/Parity/BackendParityGoldenPlanTests.cs` — AC1 (seven operator kinds + error parity).
- `Execution/Parity/BackendParityRandomizedExpressionTests.cs` — AC2/AC3/AC4/AC5 (randomized + determinism).
- `Execution/Parity/BackendParitySkipPolicyTests.cs` — verifies the AC3 skip wiring.
