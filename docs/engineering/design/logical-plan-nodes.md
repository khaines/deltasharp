# Logical plan nodes & invariants (M1)

> **Status:** living document. Created with
> [STORY-04.4.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md#story-0441-core-logical-plan-nodes-and-invariants)
> — the architectural root of [EPIC-04](../../planning/epics/EPIC-04-core-api-logical-plan.md)
> (Core API & Logical Plan). Grounded in [ADR-0008](../../adr/0008-type-system-row-format.md)
> (type system), [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager execution
> strategy), [ADR-0014](../../adr/0014-target-framework-aot.md) (multi-targeting / AOT posture),
> and [ADR-0012](../../adr/0012-plan-serialization.md) (plan serialization scope). Honors
> [repository-layout.md](repository-layout.md), [api-governance.md](api-governance.md), and
> [api-lifecycle.md](api-lifecycle.md). Update it whenever the `TreeNode<T>` contract, the M1
> logical-plan node set, the plan invariants, or the debug-render format changes.

DeltaSharp is a Catalyst-style engine: the public API builds an **immutable, unresolved
logical plan** (a tree of operators) and *does no work*; an **action** later drives the
analyzer → optimizer → physical planner → the EPIC-03 vectorized backend. This document
specifies the foundation that everything in EPIC-04 stands on: the immutable
`TreeNode<T>` base and the M1 unresolved `LogicalPlan` node set, the invariants that make
plan construction lazy and side-effect-free, and the debug renderer that keeps an
unanalyzed plan *visibly* unresolved.

It is the contract that the sibling EPIC-04 lanes follow:

- [STORY-04.4.2](../../planning/epics/EPIC-04-core-api-logical-plan.md#story-0442-expression-tree-model)
  (the expression tree, `Expression : TreeNode<Expression>`) extends the **expression seam**
  defined here (§5).
- FEAT-04.1 (`SparkSession`) and FEAT-04.2 (`DataFrame`/`Dataset<T>` transforms) *build* these
  nodes; FEAT-04.5 (analyzer) and FEAT-04.6 (physical bridge, the `#174` seam) *consume* them.

---

## 1. The layering decision (the EPIC-04 IR/type-model placement contract)

**Decision.** The logical-plan IR — `TreeNode<T>`, the `LogicalPlan` node set, the
`Expression` base, and the unresolved-expression markers — lives in **`DeltaSharp.Core`**
under `src/DeltaSharp.Core/Plans/`, multi-targeted **`net8.0;net10.0`** (ADR-0014), as
**`internal`** types. The IR carries **no reference to `DeltaSharp.Engine`** and holds **no
Engine handles** of any kind.

### 1.1 Why this placement

The constraints are hard and in tension:

- `DeltaSharp.Core` is the public, packable, PublicAPI-governed surface and multi-targets
  `net8.0;net10.0` so current-LTS applications can consume it (ADR-0014).
- The ADR-0008 type system (`DataType`/`StructType`/…) and the EPIC-03 execution backend live
  in `DeltaSharp.Engine`, which is **`net10.0`-only** and **never packed**.
- [repository-layout.md](repository-layout.md) forbids a public `net8.0` library from
  depending on a `net10.0`-only assembly: *"`DeltaSharp.Core` (packable) does not reference
  `DeltaSharp.Engine` (non-packable); the API-to-engine seam is wired at runtime, not via a
  compile-time reference."* It also already designates Core as the home of *"Public API +
  immutable logical plans."*

So **Core's `net8.0` target cannot reference Engine**, and therefore the plan IR cannot bind
to the Engine ADR-0008 type model directly.

Options evaluated:

| Option | Summary | Verdict |
| --- | --- | --- |
| **(a) IR + type/schema model in Core** | Plan IR as `internal` Core types; a `net8.0;net10.0`-compatible **public** type/schema model also in Core; Engine's internal ADR-0008 model is mapped to it at the physical bridge (#174). | **Chosen.** Matches repository-layout.md (Core owns the logical plans), keeps one IR, zero new projects, and reuses the auto-wired `InternalsVisibleTo` to `DeltaSharp.Core.Tests`. |
| (b) New `net8.0;net10.0` IR project | A separate `DeltaSharp.Plans`/`DeltaSharp.Abstractions` assembly. | Rejected for M1. repository-layout.md lists `DeltaSharp.Abstractions` only as an *"if needed"* future seam; an extra packable/governed assembly is unjustified churn while the IR is `internal` and Core already multi-targets correctly. Revisit if the IR must be shared with a non-Core packable assembly. |
| (c) IR in Engine | Co-locate with the ADR-0008 types. | Rejected. Engine is `net10.0`-only; Core's `net8.0` public API (`DataFrame.Plan`, `Explain` — both **forward-looking**: the M1 `DataFrame` is a minimal plan holder and these surfaces arrive in FEAT-04.1/04.7) could never reference it. Violates the TFM rule. |

### 1.2 The type/schema-model seam (scope boundary)

The unresolved M1 IR in this story references columns and functions **by name only**
(`UnresolvedAttribute`, `UnresolvedFunction`) and sources/sinks by **logical descriptor**, so
**no STORY-04.4.1 node needs a concrete `DataType`**. The type/schema model therefore does not
have to be implemented here. The contract for the lanes that *do* need it is:

- A concrete data type enters the plan only via **literals and casts** (STORY-04.4.2 / #168)
  and via **declared schemas** (`CreateDataFrame(rows, schema)` in STORY-04.1.2 and the
  analyzer in FEAT-04.5). Those consumers reference a **public, `net8.0;net10.0` type/schema
  model placed in `DeltaSharp.Core`** (option (a)). When the first such consumer lands it adds
  that public model and the corresponding `PublicAPI.Unshipped.txt` entries.
- The Engine ADR-0008 type system stays the **execution-side** canonical model. The
  **physical-planning bridge (#174)** maps the Core public type model to the Engine model; that
  mapping is **out of scope here** but the IR leaves a clean seam for it: the IR is pure data
  with no Engine coupling, so a downstream pass can translate it without touching these nodes.

### 1.3 What this buys the invariants

Because the IR is plain, immutable, Engine-free data:

- **Lazy/eager (ADR-0001) is structurally guaranteed.** Constructing a node cannot open a
  file, schedule a task, or touch the Engine — there is nothing Engine-shaped to touch (AC3).
- **`net8.0` stays buildable** — no `net10.0`-only dependency leaks into the public surface.
- The seam to EPIC-03 (#174) is a *translation*, not a *reference*, so it can be added later
  without reworking these nodes.

---

## 2. Design goals

1. **Spark/Catalyst parity.** Match Catalyst's `TreeNode`/`LogicalPlan` names and semantics
   (`mapChildren`, `transformDown`/`transformUp`, `withNewChildren`, `treeString`,
   `simpleString`, `nodeName`) and Spark's operator names (`Project`, `Filter`, `Aggregate`,
   `Join`, `Sort`, `Distinct`, `Union`, `UnresolvedRelation`) so Spark/Catalyst knowledge
   ports directly. Deviations are documented in §9.
2. **Immutability & structural sharing.** Nodes are immutable after construction; a transform
   returns a **new** tree that **shares** the unchanged subtrees of the original (AC1, AC2).
3. **Lazy / side-effect-free construction.** Building a plan does **zero** work — no scan, no
   I/O, no Engine, no scheduling (ADR-0001; AC3).
4. **Visibly unresolved before analysis.** The debug renderer marks unresolved attributes,
   unresolved functions, and unresolved relations explicitly (a leading apostrophe), so an
   unanalyzed plan can never be mistaken for an analyzed one (AC4).
5. **Determinism.** Equality and hashing are structural and **process-independent** (the CLR's
   `string.GetHashCode()`/`HashCode.Combine` are per-process randomized), so plan comparison,
   caching, and tests are reproducible.
6. **AOT/trim cleanliness (ADR-0014).** No reflection-based serialization, no
   `Reflection.Emit`, no banned APIs; both `net8.0` and `net10.0` targets stay analyzer-green.

---

## 3. Where the code lives

```
src/DeltaSharp.Core/Plans/
  TreeNode.cs                     internal abstract class TreeNode<TNode>
  PlanHash.cs                     internal deterministic FNV-1a hashing for the IR
  PlanDepthExceededException.cs   internal typed exception for the depth guard
  DataFrame.cs                    internal sealed class DataFrame (minimal M1 plan holder)
  Expressions/
    Expression.cs                 internal abstract class Expression : TreeNode<Expression>
    UnresolvedAttribute.cs        internal sealed class UnresolvedAttribute : Expression
    UnresolvedFunction.cs         internal sealed class UnresolvedFunction : Expression
  Logical/
    LogicalPlan.cs                internal abstract class LogicalPlan : TreeNode<LogicalPlan>
    UnresolvedRelation.cs         leaf — logical source descriptor
    Project.cs  Filter.cs  Aggregate.cs  Join.cs  Sort.cs
    Limit.cs  Distinct.cs  Union.cs
    WriteToSource.cs              write intent (logical sink descriptor)
    SinkDescriptor.cs             immutable logical sink value object
    JoinType.cs  SaveMode.cs      Spark-parity enums
```

Namespaces: `DeltaSharp.Plans` (base + hashing + `DataFrame`), `DeltaSharp.Plans.Expressions`,
`DeltaSharp.Plans.Logical`. All types are `internal`; they are exercised by
`DeltaSharp.Core.Tests` through the repository-wide auto `InternalsVisibleTo` (STORY-01.1.2).
Catalyst's plan IR is likewise not a stable public Spark API (`org.apache.spark.sql.catalyst`),
so `internal` is the Spark-parity choice and keeps `PublicAPI.Unshipped.txt` unchanged.

---

## 4. `TreeNode<T>` — the immutable tree base

```csharp
internal abstract class TreeNode<TNode> : IEquatable<TNode>
    where TNode : TreeNode<TNode>
```

The self-referential constraint (`TNode : TreeNode<TNode>`, the curiously-recurring template
pattern) lets the base return the concrete node type from transforms without casts at call
sites. Both `LogicalPlan : TreeNode<LogicalPlan>` and `Expression : TreeNode<Expression>`
derive from it.

### 4.1 Members

| Member | Kind | Contract |
| --- | --- | --- |
| `IReadOnlyList<TNode> Children` | abstract get | The child nodes, in order. Backed by a defensively-copied array exposed read-only; never the live field. A leaf returns an empty list. |
| `string NodeName` | abstract get | The Catalyst node name (e.g. `"Project"`), a constant per node — no reflection. |
| `string SimpleString` | abstract get | One-line description of **this** node including its inline expression/descriptor arguments but **excluding** child plans (children render as their own tree lines). Prefixed with `'` when the node is not resolved. |
| `TNode WithNewChildren(IReadOnlyList<TNode> newChildren)` | abstract | Rebuild this node with the supplied children (same count and positions), copying all non-child state. The only constructor-equivalent the transforms use. |
| `int Depth` | get | The cached nesting depth: `1` for a leaf, else `1 + max(child depths)`. Computed once at construction (children's depths are themselves cached → O(1) per node) and used by the **construction-time depth guard**: the base constructor throws `PlanDepthExceededException` when `Depth > MaxDepth` (`= 1000`), bounding the recursive traversals (equality, hashing, transforms, rendering) and closing a `StackOverflow` DoS. |
| `TNode MapChildren(Func<TNode,TNode> f)` | non-virtual | Apply `f` to each child. Returns **`this`** (same reference) when no child changed (reference-equal); otherwise a new node via `WithNewChildren`. This is the structural-sharing primitive. |
| `TNode TransformDown(Func<TNode,TNode> rule)` | non-virtual | Apply `rule` to this node, then recurse into the (possibly new) node's children. Pre-order. |
| `TNode TransformUp(Func<TNode,TNode> rule)` | non-virtual | Recurse into children first, then apply `rule` to the rebuilt node. Post-order. |
| `string TreeString()` | non-virtual | Multi-line, indented render of the whole subtree (§6). |
| `bool Equals(TNode?)` / `Equals(object?)` / `GetHashCode()` | mixed | Structural value equality and a deterministic, **memoized** hash (§4.3). |
| `protected abstract bool NodeEquals(TNode other)` | abstract | Compare this node's **own** (non-child) state to another node of the **same concrete type**. |
| `protected abstract int NodeHashCode()` | abstract | Deterministic hash of this node's **own** (non-child) state. |

### 4.2 Transform algorithms (Catalyst parity)

```csharp
TNode MapChildren(Func<TNode,TNode> f)
{
    var children = Children;
    if (children.Count == 0) return Self;
    TNode[]? rebuilt = null;
    for (int i = 0; i < children.Count; i++)
    {
        var nc = f(children[i]);
        if (!ReferenceEquals(nc, children[i]))
        {
            rebuilt ??= children.ToArray();   // copy lazily, only on first change
            rebuilt[i] = nc;
        }
    }
    return rebuilt is null ? Self : WithNewChildren(rebuilt);
}

TNode TransformDown(Func<TNode,TNode> rule)   // pre-order
    => rule(Self).MapChildren(c => c.TransformDown(rule));

TNode TransformUp(Func<TNode,TNode> rule)     // post-order
{
    var rebuilt = MapChildren(c => c.TransformUp(rule));  // rebuild children first
    return rule(rebuilt);
}
```

`MapChildren` returning `Self` on a no-op is the engine of **structural sharing**: a transform
that rewrites one leaf rebuilds only the spine from that leaf to the root; every untouched
subtree is shared by reference with the original tree. The original root is never mutated, so
its reference and content are unchanged (AC2).

### 4.3 Structural equality & deterministic hashing

`Equals` is authoritative and structural: two nodes are equal iff they have the **same concrete
type**, their **own state** matches (`NodeEquals`), and their **children are pairwise equal**.
`GetHashCode` folds `NodeHashCode()` with the node name and each child's hash through
`PlanHash` (a 32-bit FNV-1a, mirroring the Engine's `StableHash` rationale but `internal` to
Core) so hashes are **process-independent**; collisions are correctness-preserving because
`Equals` decides equality. Equality lets tests assert *"the original plan's content is
unchanged"* by comparing the post-transform original against a pre-transform snapshot.

Because nodes are immutable, the hash is **memoized** (computed once, cached in a field) — a
benign race merely recomputes the same number — and `LogicalPlan.Resolved`/`Expression.Resolved`
are memoized the same way, so repeated planning passes do not pay the O(n) walk each time. A
pinned **golden hash** of a fixed sample plan (asserted equal across both TFMs and across
processes) guards the determinism contract.

**Recursion-depth guard.** `Equals`, `GetHashCode`, `TransformDown`/`TransformUp`, and
`TreeString` recurse over the tree. To stop an adversarially deep tree (~thousands of nested
nodes) from triggering an uncatchable `StackOverflowException` (and to bound the O(depth²)
`TreeString` cost), each node computes and caches its `Depth` at construction and the base
constructor throws `PlanDepthExceededException` when `Depth` exceeds `TreeNode<T>.MaxDepth`
(`= 1000`). The limit is generous (M1 plans are a handful of nodes deep) yet well below the
stack-overflow point. Converting the traversals to iterative form to drop the cap is tracked in
[#376](https://github.com/khaines/deltasharp/issues/376) (§9).

---

## 5. The expression seam (for STORY-04.4.2 / #168)

This story owns the **minimal** expression base plus the two **unresolved markers** the M1
plan nodes and AC4 require. STORY-04.4.2 extends this base with the full expression set
(aliases, literals, casts, operators, sort orders, aggregate expressions, unresolved stars).

```csharp
internal abstract class Expression : TreeNode<Expression>
{
    public virtual bool Resolved => Children.All(c => c.Resolved);
}
```

- An `Expression`'s `SimpleString` renders the **whole expression inline** (its children
  folded in), matching Catalyst's `Expression.toString` — distinct from a `LogicalPlan`'s
  `SimpleString`, which excludes child plans.
- `Resolved` defaults to *"all children resolved"*; the unresolved markers override it to
  `false`.

| Node | Fields | `Resolved` | `SimpleString` |
| --- | --- | --- | --- |
| `UnresolvedAttribute` | `IReadOnlyList<string> NameParts` (e.g. `["t","a"]`) | `false` | `'t.a` (apostrophe + dotted name) |
| `UnresolvedFunction` | `string Name`; `IReadOnlyList<Expression> Arguments`; `bool IsDistinct` | `false` | `'name(arg, arg)` or `'name(distinct arg)` |

`UnresolvedFunction`'s children are its `Arguments`; `UnresolvedAttribute` is a leaf. Both stay
unresolved until the analyzer (FEAT-04.5) replaces them — never during construction (AC4).

---

## 6. `LogicalPlan` and the M1 node set

```csharp
internal abstract class LogicalPlan : TreeNode<LogicalPlan>
{
    public abstract IReadOnlyList<Expression> Expressions;   // expressions held directly
    public virtual bool Resolved =>                           // memoized
        IsNodeResolved                                        // node's own state (default true)
        && Children.All(c => c.Resolved) && Expressions.All(e => e.Resolved);

    // A node's own (non-child, non-expression) resolution gate. Defaults to true; Join overrides
    // it to false while it is still an unresolved using/natural join (see §6.1).
    protected virtual bool IsNodeResolved => true;

    // Expression-rewrite substrate (symmetric with the child-rewrite substrate on TreeNode<T>):
    public abstract LogicalPlan WithNewExpressions(IReadOnlyList<Expression> newExpressions);
    public LogicalPlan MapExpressions(Func<Expression,Expression> f);               // no-op ⇒ this
    public LogicalPlan TransformExpressionsDown(Func<Expression,Expression> rule);  // pre-order
    public LogicalPlan TransformExpressionsUp(Func<Expression,Expression> rule);    // post-order
}
```

`SimpleString` for a plan is `(Resolved ? "" : "'") + NodeName + <node args>`, where
`<node args>` inlines the node's expressions/descriptors but **not** its child plans. Each node
overrides `WithNewChildren`, `WithNewExpressions`, `NodeEquals`, `NodeHashCode`, `Expressions`,
and `SimpleString`.

**Expression-rewrite substrate (the analyzer/optimizer seam).** A plan's expressions are *not*
its children, so `WithNewChildren`/`TransformDown` only ever touch child **plans** and never a
`Project.ProjectList`, `Filter.Condition`, or `Aggregate` grouping/aggregate lists. The analyzer
(#171: `UnresolvedAttribute → AttributeReference`, `UnresolvedFunction → bound`) and optimizer
(#172: constant-fold, pushdown) mostly rewrite *plan-held expressions*, so the IR mirrors the
child substrate on the expression axis: `WithNewExpressions(newExpressions)` rebuilds a node from
its directly-held expressions (same count/positions; `Aggregate` honours the grouping ⧺ aggregate
split; `Join` rebuilds its `Condition`; no-expression nodes validate empty and return self), and
`MapExpressions`/`TransformExpressionsDown`/`Up` apply a rule to each held expression (sharing
unchanged expressions and unchanged child plans by reference). A rule rewrites expressions across
a whole plan with `plan.TransformDown(p => p.TransformExpressionsDown(rule))` — Catalyst's
`transformAllExpressions`. This means every analyzer/optimizer rule rewrites expressions through
one primitive instead of `switch`-ing on each node type and hand-rebuilding it.

### 6.1 Node catalogue

| Node | Fields (all immutable) | Children | `Expressions` | Notes / Spark parity |
| --- | --- | --- | --- | --- |
| **`UnresolvedRelation`** | `IReadOnlyList<string> Identifier`; `IReadOnlyDictionary<string,string> Options` | — (leaf) | none | The **logical source descriptor**: a multipart table identifier + read options. No schema (schema-on-read; resolved at analysis), no reader, no handle. `Resolved => false`. |
| **`Project`** | `IReadOnlyList<Expression> ProjectList`; `LogicalPlan Child` | `[Child]` | `ProjectList` | `select`/`withColumn` target. |
| **`Filter`** | `Expression Condition`; `LogicalPlan Child` | `[Child]` | `[Condition]` | `filter`/`where`. |
| **`Aggregate`** | `IReadOnlyList<Expression> GroupingExpressions`; `IReadOnlyList<Expression> AggregateExpressions`; `LogicalPlan Child` | `[Child]` | grouping ⧺ aggregate | `groupBy(...).agg(...)`. `WithNewExpressions` splits the combined list back at `GroupingExpressions.Count`. |
| **`Join`** | `LogicalPlan Left`; `LogicalPlan Right`; `JoinType JoinType`; `Expression? Condition`; `IReadOnlyList<string>? UsingColumns`; `bool IsNatural` | `[Left, Right]` | `Condition` if present, else none | Records type + criteria + both child plans; reads neither side. The three criteria — `Condition`, `UsingColumns` (`df.join(other, Seq("id"))`), `IsNatural` — are **mutually exclusive**; the analyzer desugars using/natural into a resolved equi-`Condition` once both sides resolve. **A using/natural join is never `Resolved`** (it overrides `IsNodeResolved => !(IsNatural \|\| UsingColumns is { Count: > 0 })`): its shared columns live **outside** the expression substrate (`Expressions` is empty), so the generic "children + expressions resolved" check would otherwise flip it to `Resolved` the instant both sides resolve — letting the fixed-point analyzer skip the desugaring rule and emit a physical join with no condition. It becomes resolvable only once desugared into a condition-join (`Condition` set, `UsingColumns` null, `IsNatural` false). |
| **`Sort`** | `IReadOnlyList<Expression> Order`; `bool Global`; `LogicalPlan Child` | `[Child]` | `Order` | `orderBy` (global) / `sortWithinPartitions` (local). `Order` elements are `SortOrder` expressions once #168 lands. |
| **`Limit`** | `int Count` (≥ 0); `LogicalPlan Child` | `[Child]` | none | `limit(n)`. The count is a literal integer, not an expression. |
| **`Distinct`** | `LogicalPlan Child` | `[Child]` | none | `distinct` (analyzer later rewrites to `Aggregate`, as in Spark). |
| **`Union`** | `IReadOnlyList<LogicalPlan> Inputs` (≥ 2) | `Inputs` | none | N-ary `union`/`unionByName`. `WithNewChildren` requires the **same arity** as the original. |
| **`WriteToSource`** | `LogicalPlan Child`; `SinkDescriptor Sink` | `[Child]` | none | **Write intent.** The plan that an action (`DataFrameWriter.Save`, FEAT-04.6) executes. Holds only a logical sink descriptor — no writer, no stream, no commit. |

### 6.2 Logical descriptors and enums (no handles — AC3)

- **`SinkDescriptor`** (immutable value object, **not** a `TreeNode`): `string Format`,
  `SaveMode Mode`, `string? Path`, `IReadOnlyList<string>? TableIdentifier`,
  `IReadOnlyList<string> PartitionColumns`, `IReadOnlyDictionary<string,string> Options`. Value
  equality; `SimpleString` like `[format=parquet, mode=Append, path=/out]`. It is pure data:
  **no `Stream`, `TextWriter`, file handle, task, or backend object** — that is what makes a
  write node a *descriptor only*.
- **`JoinType`** enum: `Inner`, `Cross`, `LeftOuter`, `RightOuter`, `FullOuter`, `LeftSemi`,
  `LeftAnti` (Spark parity).
- **`SaveMode`** enum: `Append`, `Overwrite`, `ErrorIfExists`, `Ignore` (Spark parity).

The same rule applies to the source side: `UnresolvedRelation` carries an identifier and an
options map and nothing executable. Asserting that scan/write nodes expose only these
descriptor types — and that the descriptor types have no member typed as a stream/handle — is
how AC3 is tested (§8).

---

## 7. Immutability & laziness — how the invariants are enforced

| Invariant | Mechanism |
| --- | --- |
| **Nodes immutable after construction (AC1)** | Every concrete node is `sealed`; every field is a get-only auto-property set once in the constructor. Collection fields are defensively **copied** into arrays at construction and exposed only as `IReadOnlyList<>`/`IReadOnlyDictionary<>` (never the live array), exactly as `StructType` does. There is no setter, no mutating method, and no API that returns the backing array. |
| **Transforms return new trees; original unchanged (AC2)** | `WithNewChildren` constructs a **new** node; `MapChildren` returns `this` only when nothing changed and otherwise builds a new spine, sharing untouched subtrees by reference. Nothing writes through an existing node. Tests snapshot the original (deep `Equals`) before a transform and assert it is byte-for-byte unchanged and that the new tree shares the untouched children by reference. |
| **Construction does zero work (AC3)** | Nodes hold only logical descriptors and child references. There is no field of an Engine, reader, writer, stream, task, or scheduler type — the IR does not even reference `DeltaSharp.Engine` (§1) — so a constructor *cannot* perform I/O or start execution. |
| **Unresolved stays unresolved before analysis (AC4)** | The unresolved markers hard-code `Resolved => false`; `LogicalPlan.Resolved`/`Expression.Resolved` are derived (own `IsNodeResolved` gate + all children + expressions resolved), so any plan containing an unresolved attribute/function/relation is itself unresolved, and the renderer prefixes it with `'`. A **using/natural `Join`** additionally overrides `IsNodeResolved` to `false` because its shared columns sit outside the expression substrate: without the gate a using-join would report `Resolved` as soon as both sides resolve — *before* desugaring — and the fixed-point analyzer would skip the desugaring rule, yielding a physical join with no condition. It stays unresolved until the analyzer replaces it with a resolved `Condition`-join. No constructor performs name resolution. |

The **lazy/eager** invariant (ADR-0001) is thus *structural*, not merely a convention: the
public API can only ever build these nodes, and these nodes are inert. Actions (FEAT-04.6) are
the sole trigger for the analyzer/optimizer/backend.

---

## 8. Debug render format (AC4)

`TreeString()` renders the subtree with Spark's connector style: the last child of a node uses
`+- `, earlier children use `:- `, and a non-last child's descendants carry a `:  `
continuation gutter (last child's descendants use three spaces). Each line is one node's
`SimpleString`. Unresolved nodes/attributes/functions/relations carry a leading apostrophe.

An unanalyzed plan built as `Project(['a, 'b])` over `Filter('>('age, '21))` over
`UnresolvedRelation([people])` renders as (at M1 only `UnresolvedAttribute`/`UnresolvedFunction`
render — there are no operator/literal expressions until #168, so the predicate prints in
function **prefix** form, not infix):

```
'Project ['a, 'b]
+- 'Filter ('>('age, '21))
   +- 'UnresolvedRelation [people]
```

and a `Union` of two relations:

```
'Union
:- 'UnresolvedRelation [left]
+- 'UnresolvedRelation [right]
```

The leading `'` on every line (and inside the expression arguments) is the machine-checkable
signal that the plan is **before analysis** — the analyzer (FEAT-04.5) is what removes it by
replacing unresolved markers with resolved attributes (`name#id`) and binding relations.

**Serialization scope.** ADR-0012 scopes binary/protobuf plan serialization to the *physical*
plan on the driver↔executor wire (EPIC-08); it is **out of scope** for M1 and for the
unresolved logical plan. AC4's "serialization or debug rendering … before analysis" is
satisfied by this canonical, deterministic **text** rendering — the same surface EXPLAIN's
logical mode (STORY-04.7.3) will print.

---

## 9. Spark/Catalyst deviations

- **Naming.** Methods use .NET PascalCase (`TransformDown`, `MapChildren`, `WithNewChildren`,
  `TreeString`) versus Catalyst's camelCase; semantics are identical.
- **`Limit`.** A single `Limit(count, child)` node is kept for the unresolved tree; Spark
  splits it into `GlobalLimit(LocalLimit(...))` during planning. The split is a later
  analyzer/optimizer concern and does not change the M1 node shape.
- **`Distinct`.** Kept as a distinct node (parity with Catalyst's `Distinct`), to be rewritten
  to `Aggregate` by the analyzer — not at construction.
- **`Union`.** Modelled N-ary (`Inputs`, ≥ 2) to match `DataFrame.union` chaining without deep
  binary nesting.
- **`Join` using/natural.** `Join` carries `UsingColumns`/`IsNatural` facets (mutually exclusive
  with `Condition`) to round-trip `df.join(other, Seq("id"))` and natural joins before
  resolution; the analyzer desugars them into a resolved equi-`Condition`. (Catalyst threads the
  same information through `UsingJoin`/`NaturalJoin` analyzer plans.)
- **Visibility.** The IR is `internal` (Catalyst's `catalyst` package is not a stable public
  Spark API); the public Spark-parity surface is `DataFrame`/`Column`/functions (later FEATs).

### 9.1 Deferred

- **Iterative tree traversal ([#376](https://github.com/khaines/deltasharp/issues/376)).** The
  hot traversals (`Equals`, `GetHashCode`, `TransformDown`/`TransformUp`, `TreeString`) are
  recursive. M1 mitigates the resulting `StackOverflow`/`O(depth²)` risk with the construction-
  time depth guard (`MaxDepth = 1000`, §4.3); converting the traversals to explicit-stack form —
  removing the cap before deeply-nested machine-generated plans from the SQL frontend land — is
  tracked as a follow-up.

---

## 10. Acceptance criteria → tests

Tests live in `tests/DeltaSharp.Core.Tests/Plans/` and reach the `internal` IR via the
auto `InternalsVisibleTo`.

| AC | Statement | Test(s) |
| --- | --- | --- |
| **AC1** | Each M1 node and its children are immutable after construction. | `ImmutabilityTests`: every node type is `sealed`; mutating a caller's source array/dictionary after construction does not change the node (defensive copy) — covered **per node** (Project list, Aggregate grouping+aggregate, Sort order, Union inputs, Join using-columns, `SinkDescriptor` partition-columns+options, relation options); every collection property/`Expressions` view is a read-only view that **cannot be cast back to a mutable array** (including the empty-collection cases). |
| **AC2** | A transform produces a new plan; the original's reference and content are unchanged (structural sharing). | `StructuralSharingTests`: `TransformUp`/`MapChildren`/`WithNewChildren` on a leaf return a new root while the original root is reference-unchanged and deep-`Equals` to a pre-transform snapshot; the rewritten tree **shares** untouched siblings/subtrees by reference; `MapChildren` with a no-op rule returns the **same** reference; `WithNewChildren` success paths are covered **per node** (Limit, Sort, Aggregate, Distinct, WriteToSource, a binary Join, and arity-checked Union). `ExpressionRewriteTests` covers the expression-rewrite substrate (`WithNewExpressions`/`TransformExpressions*`). A minimal `DataFrame` test asserts `df.Plan` is unchanged after deriving a new `DataFrame`. |
| **AC3** | Scan/write nodes hold logical descriptors only — no readers, writers, tasks, or backend handles. | `DescriptorOnlyTests`: `UnresolvedRelation` exposes only identifier/options; `WriteToSource`/`SinkDescriptor` expose only format/mode/path/identifier/partitions/options; a **reflection-based** structural assertion that no IR member — **public property or public/private field, on the type or any base IR type** — is typed as `Stream`/`TextReader`/`TextWriter`/`IDisposable`/Engine type; constructing a scan/write does no I/O (a path that would throw if opened is never opened). |
| **AC4** | Serialization/debug rendering before analysis keeps unresolved attributes/functions explicitly unresolved. | `TreeRenderTests`: `TreeString()` of an unanalyzed `Project/Filter/UnresolvedRelation` and of a `Union` match the §8 golden strings; every line and every attribute/function is apostrophe-prefixed; `Resolved` is `false` for any plan containing an unresolved marker. |
| (base) | `TreeNode<T>` transform/equality/hash contracts. | `TreeNodeTests`: pre/post-order visit order of `TransformDown`/`TransformUp`; structural `Equals`/`GetHashCode` (equal trees equal & same hash, differing trees not equal); deterministic hash (stable across constructions). |

All gates pass: `dotnet restore --locked-mode`; `dotnet build -c Release -warnaserror`
(0/0, both TFMs); `dotnet test -c Release`; `dotnet format --verify-no-changes`.
