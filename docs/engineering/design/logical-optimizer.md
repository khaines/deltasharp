# Logical optimizer: minimal rule-based optimization (M1)

> **Status:** living document. Created with
> [STORY-04.5.3](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md#story-0453-minimal-logical-optimization-rules-for-local-execution)
> (issue [#172](https://github.com/khaines/deltasharp/issues/172)), the first optimizer pass of
> FEAT-04.5. Grounded in [EPIC-04](../../planning/epics/EPIC-04-core-api-logical-plan.md),
> [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager execution, layered Catalyst-style
> pipeline) and [ADR-0008](../../adr/0008-type-system-row-format.md) (the ANSI type/overflow
> semantics constant folding must honor). Consumes the resolved plan produced by
> [analyzer-resolution.md](analyzer-resolution.md) and the expression IR of
> [expression-model.md](expression-model.md) over the `TreeNode<T>` substrate of
> [logical-plan-nodes.md](logical-plan-nodes.md). Honors
> [repository-layout.md](repository-layout.md). Update it whenever a rule, batch, ordering, or the
> termination argument changes — **reviews verify this document against the code line-by-line.**

## 1. Where the optimizer sits

The optimizer is the **third** stage of the Catalyst-style pipeline:

```
public API  →  unresolved LogicalPlan
analyzer    →  resolved (analyzed) LogicalPlan          (analyzer-resolution.md, #170/#171)
OPTIMIZER   →  optimized LogicalPlan                    (THIS DOC, #172)
planner     →  physical plan                            (#174, later)
backend     →  execution                                (EPIC-03)
```

It takes a **resolved** `LogicalPlan` (every name bound, every expression typed) and returns an
**equivalent, cheaper** resolved `LogicalPlan`. It is a pure tree-to-tree function: it performs no
I/O, touches no catalog, and triggers no execution — building or optimizing a plan is still lazy
(ADR-0001). The optimizer is **not** yet wired into an action driver; that bridge is #173/#174. This
story delivers the optimizer as a standalone, testable component (`new Optimizer().Optimize(plan)`).

Everything here is `internal` (namespace `DeltaSharp.Optimization`, under
`src/DeltaSharp.Core/Optimization/`). It references only the Core IR plus the shared
`DeltaSharp.Types` model (`DeltaSharp.Abstractions`) — never `DeltaSharp.Engine`/`Executor`. It adds
**no** public API surface (no `PublicAPI.Unshipped.txt` delta), keeping Core packable and
Core⟂Engine independence intact.

### 1.1 EXPLAIN: analyzed and optimized render separately (AC3)

Because every stage is a plain `LogicalPlan` and `TreeNode<T>.TreeString()` renders any plan on
demand, the analyzed plan and the optimized plan are **independently renderable** — an EXPLAIN can
print each stage side by side simply by holding the analyzer's output and the optimizer's output and
calling `TreeString()` on each. The optimizer introduces no rendering surface of its own; it only
returns a new tree. (A user-facing `EXPLAIN` command is a later story; the substrate it needs — two
independently renderable stages — exists today.)

## 2. The rule / batch / fixpoint framework

Three small abstractions mirror Catalyst's `Rule`/`Batch`/`RuleExecutor`:

```csharp
internal abstract class Rule
{
    public abstract string Name { get; }
    public abstract LogicalPlan Apply(LogicalPlan plan);   // LogicalPlan -> LogicalPlan, pure
}

internal enum RuleStrategy { Once, FixedPoint }

internal sealed class RuleBatch          // an ordered list of rules + a strategy
{
    public string Name { get; }
    public RuleStrategy Strategy { get; }
    public int MaxIterations { get; }    // safety valve for FixedPoint
    public IReadOnlyList<Rule> Rules { get; }
}

internal sealed class Optimizer
{
    public const int DefaultMaxIterations = 100;
    public LogicalPlan Optimize(LogicalPlan analyzedPlan);
}
```

A **rule** is a total function `LogicalPlan → LogicalPlan`. It must return the *same instance* when
it changes nothing (so structural sharing and fixpoint detection stay cheap) and a **new tree** when
it rewrites (plan nodes are immutable — §4).

`Optimizer.Optimize` runs each **batch in order**; each batch runs its rules in a fixed order,
either **`Once`** (a single sweep) or to a **`FixedPoint`** (repeat the whole sweep until the plan
stops changing or `MaxIterations` is hit). Fixpoint is detected with **structural equality**
(`LogicalPlan.Equals`, Catalyst's `fastEquals`): after a full sweep, if the result is structurally
equal to the plan the sweep started from, the batch has converged.

```
RunBatch(batch, plan):
    current = plan
    for iteration in 1..:
        start = current
        for rule in batch.Rules:            # deterministic order
            current = rule.Apply(current)
        if batch.Strategy == Once:            break
        if current.Equals(start):             break   # fixpoint
        if iteration >= batch.MaxIterations:  break   # safety valve
    return current
```

### 2.1 Batches and ordering (deterministic)

| # | Batch | Strategy | Rules (in order) |
|---|-------|----------|------------------|
| 1 | `Operator Optimization` | FixedPoint | `ConstantFolding`, `CombineFilters`, `PushPredicateThroughProject`, `ColumnPruning` |

`ConstantFolding` runs **first within the batch** so later rules in the same sweep see simplified
predicates (e.g. a folded `Filter(false)`). Crucially it is **co-located inside** the operator batch
rather than in a separate earlier batch, so the whole pipeline reaches a **global** fixpoint: when
`CombineFilters` synthesizes an `And(c1, c2)` of two boolean literals, the *next* sweep's
`ConstantFolding` folds it, and the sweep after that is a no-op. The batch then combines adjacent
filters, pushes predicates toward the scan, and prunes unused scan columns; running these together to
a fixpoint lets pushdown expose more pruning and folding expose more combination, and vice versa. The
batch list, the rule order within the batch, and every rule's internal logic are **fixed and
deterministic**, so `Optimize` is a deterministic function of its input (§5).

## 3. The M1 rules

Each rule is **semantics-preserving**: it never changes the plan's output schema or the multiset of
result rows. Where a precondition is not met the rule returns the subtree **unchanged** (AC2). Each
rule is **conservative and Spark/Catalyst-faithful** — it fires only in cases it can prove safe and
explicitly defers the rest to a later milestone.

### 3.1 `ConstantFolding`

**What it does.** Rewrites every expression across the plan bottom-up
(`plan.TransformUp(node => node.TransformExpressionsUp(Fold))`) and replaces a fully-constant
subexpression with the `Literal` it evaluates to. Bottom-up order means nested constants collapse in
one pass (`(1 + 2) + 3 → 3 + 3 → 6`).

Two families are folded, and **only** when *every* relevant operand is a `Literal`:

1. **Arithmetic** — `Add`/`Subtract`/`Multiply` over two non-null literals of the **same** numeric
   type:
   - Integral (`byte`/`short`/`int`/`long`): computed in a **`checked`** context. **If the result
     would overflow the type, the rule does not fold** — it leaves the original node so execution
     raises the ANSI `ArithmeticOverflowException` (ADR-0008: *never wrap*). This is mode-independent:
     declining to fold is correct under both ANSI (would throw) and Legacy (would null).
   - Floating (`float`/`double`): folded with IEEE-754 arithmetic (deterministic, total — overflow
     yields `±Infinity`, matching Spark).
2. **Boolean logic** — `And`/`Or`/`Not` over boolean literals, evaluated under SQL **three-valued
   logic** (a SQL `NULL` boolean literal is a valid operand): `Not(true)→false`, `Not(null)→null`,
   `And(true,false)→false`, `And(null,true)→null`, `Or(true,null)→true`, etc. This is the
   "boolean literals in conditions" case (e.g. `Filter(And(true, false)) → Filter(false)`).

**Spark/Catalyst parity.** This is a deliberately narrow slice of Catalyst's `ConstantFolding` +
`NullPropagation`: fold a node whose inputs are all foldable literals.

**Deliberately NOT in M1** (documented so reviewers can confirm the code matches):
`Divide`/`Remainder` (division-by-zero and result-type nuances), decimal folding (unscaled-`Int128`
+ scale rules), `Cast`-of-literal folding, comparison folding (`1 < 2 → true`), and *partial*
boolean simplification (`And(true, x) → x`, which is Catalyst's separate `BooleanSimplification`).
None of these fire; each is a future rule.

### 3.2 `CombineFilters`

**Trigger.** A `Filter` whose child is a `Filter`, where **both** predicates are deterministic.
**Transform.** `Filter(outer, Filter(inner, g)) → Filter(And(inner, outer), g)`. A row survives two
nested filters iff both predicates are TRUE, which is exactly `inner AND outer` under 3VL — so this is
semantics-preserving. The conjunction is emitted **inner (child) predicate first** to match Spark:
under short-circuit ANSI evaluation the operand order is observable, and keeping the child predicate
first preserves a guard the inner filter provided (e.g. `age != 0`) ahead of a predicate that depends
on it (e.g. `1 / age > 0`), so the combined filter never raises an error the original nested plan
could not. Because the rule runs `TransformUp` (**post-order**), an N-filter chain collapses in a
**single bottom-up sweep** — not over successive fixpoint iterations. The deterministic-only guard is
inert in M1 (every M1 predicate is deterministic) and correct once `rand`/`uuid` land (#413). (AC:
"filter combination".)

### 3.3 `PushPredicateThroughProject`

**Trigger.** `Filter(cond, Project(list, child))` where **every** attribute the condition references
is a **pass-through** column of the projection — i.e. a `list` element that is a bare
`AttributeReference` (not an `Alias`/computed column). **Transform.**
`Filter(cond, Project(list, child)) → Project(list, Filter(cond, child))`.

**Why the pass-through restriction is exactly right.** The analyzer's output derivation gives a
pass-through `AttributeReference` projection element the **same `ExprId`** as the child attribute it
forwards, whereas an `Alias`/function element gets a **fresh** output `ExprId` that does not exist in
`child`. So "`cond` references only pass-through columns" ⇔ "every `ExprId` in `cond` is present in
`child`'s output", which is precisely the condition under which the pushed `Filter(cond, child)` is
well-formed and evaluates identically. No expression substitution is needed. Filters that reference a
computed alias are left in place (precondition not met → unchanged, AC2). Fixpoint pushes a predicate
through a stack of projections one level per iteration.

**Deliberately NOT in M1:** pushing a filter into/through `Aggregate`, `Join` (either side),
`Union`, `Window`, or a `Project` with aliases (would require rewriting the predicate in terms of the
alias definitions). Conservative and Spark-faithful: pushing through an aggregate or join can change
semantics, so M1 refuses. The push also requires the predicate and its pass-through landing targets
to be deterministic — inert in M1, correct once non-deterministic expressions land (#413).

### 3.4 `ColumnPruning`

**What it does.** Drops columns a `ResolvedRelation` (scan) exposes that no operator above it ever
uses, so the scan produces a narrower row. It is implemented as a single **top-down** pass that
threads a *required-attribute set* (`HashSet<ExprId>?`) from each parent into its child:

- `null` means **"output is not yet cut by a projection — every column flowing through reaches the
  plan result, so keep them all."** The pass starts at the root with `null`.
- A non-null set means "the parent needs exactly these attribute ids from me."

Per operator:

| Operator | Required passed to child | Rationale |
|---|---|---|
| `Project(list)` | `refs(list)` | The projection **cuts** output: below it, only what `list` references survives. |
| `Filter(cond)` | `required ∪ refs(cond)` (or `null` if `required` is `null`) | Output = child output (pass-through); the predicate also needs its columns. |
| `Sort(order)`  | `required ∪ refs(order)` (or `null`) | Output = child output; ordering needs its keys. |
| `Limit`        | `required` unchanged | Positional truncation; column values are irrelevant. |
| `Distinct`     | **`null`** | Dedup key is *all* child columns; dropping any would change the row multiset. |
| `ResolvedRelation` | — | If `required` is `null`, keep all. Otherwise keep only `Output` attributes whose `ExprId ∈ required`, in original order, and prune `Schema` to the matching fields. |
| any other (`Aggregate`, `Join`, `Union`, `WriteToSource`, …) | prune each child with **`null`** | Conservative: M1 does not model these operators' outputs, so it resets `required` to keep-all before recursing — it never prunes their **direct inputs**. |

Only **`ResolvedRelation` leaves are ever rewritten**; the pruned relation reuses the very same
`AttributeReference` instances (identical `ExprId`/type/nullability) for the kept columns, so every
reference above it stays valid and the plan's final output schema is unchanged. Because a bare
`Filter`/`ResolvedRelation` at (or near) the root is reached with `required == null`, columns that
flow to the result un-projected are never pruned (that would drop result columns) — the pass only
prunes below a `Project` that has already cut them.

Precisely, the "any other" reset means pruning does **not** apply to the *direct* input of an
`Aggregate`/`Join`/`Union` (their child is pruned with `required == null`, i.e. keep-all). It does
**not** mean pruning stops for the whole subtree: a **deeper `Project`** re-establishes a fresh
required set from its projection list, so scan pruning resumes below that inner projection. One
degenerate case is worth noting: when a projection references nothing usable (`keptOutput.Count == 0`
at a scan — an empty required intersection), `PruneRelation` keeps **all** columns rather than
producing a zero-column scan, preserving a well-formed relation.

**Deliberately NOT in M1:** pruning `Aggregate`/`Join`/`Union` inputs, collapsing adjacent
projections, and introducing new intermediate `Project` nodes. Only scan-column pruning below an
existing projection is performed.

## 4. Immutability and structural sharing

Plan nodes are immutable (`TreeNode<T>`); no rule mutates a node in place. Every rewrite produces a
**new** tree through the existing structural transforms — the M1 rules use exactly `TransformUp`,
`TransformExpressionsUp`, `MapChildren`, and `WithNewChildren` — all of which **share unchanged
subtrees by reference** (they return the same instance when a transform is a no-op, short-circuiting
on `ReferenceEquals`). `ColumnPruning` rebuilds through `MapChildren`, so a unary node is rebuilt only
when its pruned child is not reference-equal to the original, otherwise the original node is returned.
Net effect: a rule that changes nothing returns the input instance, and a rule that changes one leaf
shares the entire untouched remainder of the tree.

## 5. Determinism, idempotence, and termination

**Deterministic.** The batch list, the rule order in each batch, and each rule's logic are fixed. All
iteration that affects output order is over ordered lists (`ProjectList`, `Output`, children);
`HashSet<ExprId>` is used only for *membership* tests, never to produce ordered output. No banned
nondeterministic API is used (no `Guid.NewGuid`, `DateTime.Now/UtcNow`, `Random`, or
`Reflection.Emit`); the optimizer never mints an `ExprId`. So `Optimize(p)` is a pure function of
`p`.

**Idempotent.** The single operator-optimization batch runs to a **global** fixpoint. This is what
makes idempotence hold *across* rules that feed each other: `CombineFilters` may synthesize an
`And(c1, c2)` that only `ConstantFolding` can then fold, and because both rules live in the *same*
fixpoint batch the batch keeps sweeping until neither fires — for example
`Optimize(Filter(true, Filter(true, r)))` converges to `Filter(true, r)` (combine → `And(true, true)`
→ fold → `true`). A second `Optimize` re-runs the batch, finds the plan already at that fixpoint (the
first sweep reproduces a structurally-equal tree), and stops — `Optimize(Optimize(p)).Equals(Optimize(p))`.
Had constant folding stayed a separate earlier batch, the synthesized `And(true, true)` would survive
the first `Optimize` and only fold on the second, breaking idempotence.

**Terminating.** `Once` batches are a single sweep. Each `FixedPoint` batch stops as soon as a sweep
is a no-op *or* after `MaxIterations` (default `100`), so termination is guaranteed regardless (a
`FixedPoint` batch that exits via the cap rather than a no-op sweep is a rule/ordering bug and is
surfaced as an exception in DEBUG/test builds). The rules also converge on their own: `ConstantFolding`
strictly reduces expression-node count; `CombineFilters` strictly reduces the count of adjacent
`Filter` **operators** (2 → 1 — note it *adds* an `And` expression node, so the decreasing measure is
the number of `Filter` operators, not the total tree-node count); `PushPredicateThroughProject`
strictly increases a filter's depth, bounded by the (finite) number of `Project` ancestors;
`ColumnPruning` strictly reduces the total scan-column count. Each measure is bounded below, so the
combined sweep reaches a fixpoint well within the safety valve for the shallow M1 plans.

**Semantics-preserving.** No rule changes the plan's output schema or its result multiset: folding
replaces an expression with its own value; filter combination/pushdown preserves the surviving-row
set; and column pruning only removes scan columns that are provably unreferenced above the projection
that already dropped them (§3.4).

**Nullability note.** Boolean 3VL folding can *tighten* a result's nullability hint toward a
provably-correct **non-nullable** literal (e.g. `And(x, false) → false`, which is never `NULL`); it
never **widens** nullability. This is Spark-faithful — a folded constant carries its own exact
nullability. It also means the user-facing schema must be derived from the **analyzed** plan, not the
optimized one, so folding can never surprise a caller by reporting a narrower/wider column
nullability than analysis promised; the `analyzed → optimized → planner` bridge (#174) derives the
reported schema from the analyzed plan.

## 6. Acceptance criteria → tests

Tests live in `tests/DeltaSharp.Core.Tests/Optimization/` (`OptimizerTests.cs`,
`ConstantFoldingTests.cs`, `ColumnPruningTests.cs`, `PredicatePushdownTests.cs`,
`RuleFrameworkTests.cs`). Names below are `Class.Method` (the classes carry the `Optimizer`/rule
context, so method names are unprefixed).

| Acceptance criterion | Tests |
|---|---|
| **AC1** — rules (pruning, filter combination, constant folding) return **new immutable** plan trees | `ConstantFoldingTests.FoldsIntegerArithmetic_ToLiteral`; `PredicatePushdownTests.CombineFilters_MergesNestedFilters_IntoConjunction`; `PredicatePushdownTests.PushPredicate_MovesFilterBelowProject_ForPassThroughColumn`; `ColumnPruningTests.DropsUnreferencedScanColumns_BelowProjection`; `OptimizerTests.ReturnsNewTree_WithoutMutatingInput` (input tree unchanged; result is a distinct tree) |
| **AC2** — precondition not met ⇒ subtree preserved | `ConstantFoldingTests.DoesNotFold_NonConstantExpression`; `ConstantFoldingTests.DoesNotFold_OnAnsiOverflow`; `PredicatePushdownTests.PushPredicate_DoesNotPush_ThroughAliasColumn`; `ColumnPruningTests.DoesNotPrune_BelowDistinct`; `ColumnPruningTests.NoOp_WhenAllColumnsUsed`; `OptimizerTests.NoOp_ReturnsReferenceEqualPlan` |
| **AC3** — analyzed and optimized render separately (EXPLAIN) | `OptimizerTests.AnalyzedAndOptimizedPlans_RenderIndependently` (both `TreeString()`s differ and each renders standalone) |
| **AC4** — output schema/results equivalent to the analyzed plan | `ColumnPruningTests.PreservesTopLevelOutputSchema`; `OptimizerTests.PreservesPlanOutput_ForAnalyzedPlan`; `ConstantFoldingTests.PreservesResultType_ForLongMultiply` |
| **M1** — `CombineFilters` emits inner (child) conjunct first (Spark ANSI short-circuit parity) | `PredicatePushdownTests.CombineFilters_EmitsInnerConjunctFirst_ForAnsiShortCircuitSafety`; `PredicatePushdownTests.CombineFilters_MergesNestedFilters_IntoConjunction` |
| **M2** — global-fixpoint idempotence on stacked constant filters | `OptimizerTests.IsIdempotent_ForStackedConstantTrueFilters`; `OptimizerTests.IsIdempotent_ForStackedConstantFilters_FoldingToFalse` |
| **L3** — `Optimize` rejects an unresolved plan | `OptimizerTests.Optimize_RejectsUnresolvedPlan` |
| Framework — batch/fixpoint/ordering, idempotence, termination | `RuleFrameworkTests.RunsFixpointBatch_UntilNoFurtherChange`; `RuleFrameworkTests.RunsBatchesInOrder`; `RuleFrameworkTests.HonorsMaxIterations_ForNonConvergingBatch`; `OptimizerTests.IsIdempotent`; `OptimizerTests.IsDeterministic_AcrossRuns` |

## 7. Follow-ups (post-M1)

Comparison/`Cast`/decimal constant folding and `NullPropagation`; `BooleanSimplification`;
`CollapseProject`; predicate pushdown through joins/aggregates and into scans as data-source filters;
limit pushdown (`LocalLimit`/`GlobalLimit`); a cost-based layer; and wiring `Optimize` into the
action driver's `analyzed → optimized → planner` path (#173/#174) with an EXPLAIN command.

**Determinism-guard, dedup, and alias-substitution follow-ups are tracked under
[#413](https://github.com/khaines/deltasharp/issues/413).** The `Expression.Deterministic` seam
already gates `CombineFilters` and `PushPredicateThroughProject` (inert in M1, since every M1
expression is deterministic); #413 lands the first non-deterministic expressions (`rand`/`uuid`/
`current_row_timestamp`) that override `Deterministic => false`, plus common-subexpression dedup and
the alias-substitution needed to push predicates through aliasing projections.
