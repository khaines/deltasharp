# Dataset&lt;T&gt; value encoders (M1)

> **Status:** living document. Created with
> [STORY-04.7.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md)
> (issue [#178](https://github.com/khaines/deltasharp/issues/178), FEAT-04.7 — the `Row`→`T` value
> encoders). Grounded in [ADR-0001](../../adr/0001-execution-strategy.md) (AOT-safe interpreter +
> optional guarded codegen tier), [ADR-0008](../../adr/0008-type-system-row-format.md) (type system /
> nullability), and builds directly on [dataset-typed-bridge.md](dataset-typed-bridge.md) (the typed
> transformation bridge and schema derivation, #163), [actions-and-row.md](actions-and-row.md) (the
> `Row` materialization contract, #177), [read-door.md](read-door.md) (the CLR→`DataType` value
> contract), [native-aot.md](native-aot.md), and [compiled-expression-fusion.md](compiled-expression-fusion.md)
> (the engine's ADR-0001 codegen-tier exemplar). Update it whenever the encoder, the decode semantics,
> or the tier-selection rules change.

## 1. What this is (and is not)

This completes the `Dataset<T>` typed bridge by adding the **decode half** of its value encoder: turning
the `Row`s a plan produces into instances of the encoded type `T`, so a typed `Dataset<T>` can run a
**typed collect** locally without leaving Spark semantics. It mirrors Apache Spark's
`Dataset[T].collect(): T[]` (a case-class / bean `Encoder[T]`), and it is the piece the
[dataset-typed-bridge.md](dataset-typed-bridge.md) §1 "Scope boundary with STORY-04.7.2 (#178)"
explicitly deferred.

New public surface (`src/DeltaSharp.Core/`):

| Public member | Spark parity | Result |
| --- | --- | --- |
| `Dataset<T>.Collect()` | `Dataset.collect()` | `IReadOnlyList<T>` — runs the plan, decodes each `Row`→`T` (`src/DeltaSharp.Core/Session/Dataset.cs`) |
| `Dataset<T>.Collect(CancellationToken)` | `Dataset.collect()` | cancellable overload; cancellation is observed by the underlying execution before decoding (`Dataset.cs`) |

Everything else the encoder needs is **engine-internal** (`internal`), so the public surface grows by
exactly the two `Collect` entry points AC2 requires:

| Internal type | File | Role |
| --- | --- | --- |
| `RowDecoder<T>` | `Session/RowDecoder.cs` | the built decoder: schema + instance factory + ordered bindings; `Decode(Row) → T` |
| `PropertyDecodeBinding` | `Session/RowDecoder.cs` | one column↔property binding; owns ADR-0008 value preparation and the per-tier setter delegate |
| `RowDecoderFactory` | `Session/RowDecoderFactory.cs` | builds a decoder for `T`; owns validation and tier selection (the AOT crux) |
| `TypedRowDecoderCache<T>` | `Session/RowDecoderFactory.cs` | per-`T` `Lazy` cache of the built decoder (deterministic diagnostic re-throw) |
| `DatasetSchema.MappableProperties<T>()` | `Session/DatasetSchema.cs` | the ordered property list shared by the schema deriver and the decoder |

`Collect` is an **action** ([ADR-0001](../../adr/0001-execution-strategy.md) lazy/eager invariant): it
executes the plan through the **same** `IQueryExecutor` seam `DataFrame.Collect()` uses
(`Dataset<T>.Collect` calls `ToDF().Collect(ct)`), then decodes. Building a `Dataset<T>` or chaining a
typed transformation still does no work.

### Scope: M1 vs deferred

**In scope (M1).** Decoding a collected `Row` into `T` where `T` is a **bean**: a non-abstract
**reference type** (class or record) with a **public parameterless constructor** and, for each mapped
column, a **public settable** (`set`/`init`) property whose type is one of the ADR-0008 supported
scalars (§3). Nullable value types, reference types, and `byte[]` are handled per ADR-0008.

**Deferred (tracked in [#447](https://github.com/khaines/deltasharp/issues/447) — "Dataset&lt;T&gt; encoder: non-bean shapes &amp; `T`→`Row`
encode").**

- **`T`→`Row` encode** (materializing rows *from* `T` values, e.g. `createDataset(IEnumerable<T>)`). This
  story is decode-only; there is no user-facing typed source in M1.
- **Positional records** (`record Person(string Name, int Age)`) and any type whose only constructor
  takes parameters — needs a product/primary-constructor encoder. Their **schema** still derives
  (so `As<T>()` succeeds), but `Collect()` raises a guiding diagnostic (§5).
- **Value-type / `struct` `T`.**
- **Nested / complex types**: struct/`Row`-valued columns, arrays, maps, collections. Only the flat
  scalar set maps in M1 (consistent with the derived schema, [dataset-typed-bridge.md](dataset-typed-bridge.md) §4).
- **By-name **case-insensitivity** and schema reconciliation** (column reordering, missing/extra
  columns, widening). Binding is by exact name against the row (§2), matching `Row`'s case-sensitive
  contract.
- **Typed `Select` → `Dataset<U>`** still returns a `DataFrame` (unchanged from #163); reconstructing a
  typed output type is a separate output-encoder concern.

## 2. The `Row` → `T` mapping model

The decoder is a **bean model**. For a supported `T` it holds three things (all computed once, at build
time) and is then immutable:

1. an **instance factory** — `Func<object>` that calls `T`'s public parameterless constructor
   (`typeof(T).GetConstructor(Type.EmptyTypes)`, invoked `ctor.Invoke(null)`), **shared by both setter
   tiers** so construction is identical between them;
2. an ordered array of **`PropertyDecodeBinding`**, one per mapped property; and
3. the derived **`Schema`** (for introspection and tests).

`Decode(Row)` (`RowDecoder.cs`) is: construct a fresh instance, then for each binding read-and-assign one
column, then return `(T)instance`. Every call allocates a fresh `T` and shares no mutable state, so a
cached decoder is safe to reuse across rows and threads.

**Binding is by column name.** Each `PropertyDecodeBinding.ColumnName` is the property's `Name`, and the
decoder reads the cell via the `Row` **string indexer** `row[ColumnName]` (`Row.cs`), which resolves the
ordinal through `Row.FieldIndex` (case-sensitive, Spark-parity) and throws a deterministic
`ArgumentException` naming the missing column if absent. Binding by name (rather than ordinal) matches
Spark's case-class/bean encoders and is robust to a plan that reorders columns.

**Ordering is shared with the schema, never duplicated.** Both the derived schema and the decoder's
binding order come from one method, `DatasetSchema.MappableProperties<T>()` (`DatasetSchema.cs`):

```csharp
typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
    .OrderByDescending(p => InheritanceDepth(typeof(T), p.DeclaringType)) // base-class first
    .ThenBy(p => p.MetadataToken)                                         // declaration order within a type
    .ToArray();
```

`DatasetSchema.Derive<T>()` maps `MappableProperties<T>()[i] → Schema[i]`, and the factory binds
`MappableProperties<T>()[i]` to that same field, so **property index `i` ⇒ schema field `i` ⇒ binding
`i`**: a single deterministic property↔ordinal map that cannot drift between the schema and the decoder
(AC1).

## 3. Decode semantics (ADR-0008)

`PropertyDecodeBinding.PrepareValue(Row)` (`RowDecoder.cs`) reads and validates one cell **without
mutating the row**, following [ADR-0008](../../adr/0008-type-system-row-format.md):

- **Null handling.** `AllowsNull` is `Nullable.GetUnderlyingType(propertyType) is not null ||
  !propertyType.IsValueType` — i.e. a `Nullable<U>` value type or any reference type accepts SQL `NULL`;
  a non-nullable value type does not. This is the **same** rule `DatasetSchema.ToField` uses for the
  schema's `Nullable` flag, so a nullable **column** always corresponds to a null-accepting **property**.
  A `NULL` cell for a non-nullable value-typed property throws a deterministic
  `InvalidOperationException` naming the member (mirroring `Row.GetAs<T>`).
- **Type check.** `ExpectedType` is the `Nullable<>`-unwrapped property type. A non-null cell must
  already be an instance of `ExpectedType` (`ExpectedType.IsInstanceOfType(cell)`); otherwise a
  deterministic `InvalidCastException` is thrown. The decoder performs **no widening or coercion** — an
  `int` column decodes to an `int`/`int?` property, never a `long` one — consistent with the read-door
  value contract ([read-door.md](read-door.md), [dataset-typed-bridge.md](dataset-typed-bridge.md) §4) and
  with the non-widening `MapClrType`.
- **Supported scalars.** Exactly the ADR-0008 set `MapClrType` maps: `bool`, `sbyte`, `short`, `int`,
  `long`, `float`, `double`, `decimal`, `string`, `byte[]`, `System.DateOnly`, `System.DateTime` (and
  their `Nullable<>` forms). An unsupported property type is rejected at **schema derivation** (§5), so
  the decoder only ever sees supported cells.
- **`byte[]` (BinaryType) is copied.** A non-null `byte[]` cell is cloned (`bytes.AsSpan().ToArray()`)
  into `T`, so the decoded value never **aliases** the row's array — mutating the decoded array cannot
  reach back into the `Row` (value semantics). Every other supported cell is an immutable value shared by
  reference.

**No `Row` mutation.** Decoding only calls the `Row` indexer (a read) and writes into the freshly
constructed `T`. The `Row`'s backing values are never written, and the only reference the decoder copies
out (a `byte[]`) is cloned, so a decoded `T` cannot be used to mutate the source row. A
mutation-sensitive test snapshots every cell before decoding and asserts each is the same object
afterwards (§8).

## 4. AOT-safe reflection default + optional guarded compiled tier (ADR-0001) — the crux

`T`'s members are assigned through a per-property **setter delegate** `Action<object, object?>`. This is
the **only** thing that differs between the two tiers; construction (§2) and value preparation (§3) are
shared, which is what makes the tiers a clean **parity oracle**.

- **Reflection tier (default, AOT-safe).** `ReflectionSetter` returns `(obj, val) =>
  property.SetValue(obj, val)`. Pure reflection member access, no dynamic code. This is the
  correctness reference and the **only** tier reachable under NativeAOT.
- **Compiled tier (optional, guarded).** `CompileSetter` builds a LINQ expression
  `((TDeclaring)instance).set_Property((TProperty)value)` and compiles it to an
  `Action<object, object?>`. It is annotated `[RequiresDynamicCode]` and reached **only** through the
  feature guard, so the trim/AOT analyzers prove it is elided from an AOT image. It calls the property's
  **`set`/`init` accessor** (init-only setters are invocable via both `PropertyInfo.SetValue` and
  `Expression.Call(setMethod)`), assigning the **same** prepared value the reflection tier would.

**The feature gate is a single property** (`RowDecoderFactory.UseCompiledSetters`), mirroring the
engine's `ExecutionBackends.IsCompiledBackendAvailable`
([compiled-expression-fusion.md](compiled-expression-fusion.md), ADR-0001):

```csharp
#if NET9_0_OR_GREATER
    [FeatureGuard(typeof(RequiresDynamicCodeAttribute))]
#endif
    internal static bool UseCompiledSetters => RuntimeFeature.IsDynamicCodeSupported;
```

The guarded call site takes the compiled tier through a **direct** `if (UseCompiledSetters)`
(`BuildSetters`) so the .NET 9+ feature-guard analyzer recognizes it and raises no IL3050.

**Cross-TFM note.** `DeltaSharp.Core` multi-targets `net8.0;net10.0` (ADR-0014), but
`FeatureGuardAttribute` only exists on **.NET 9+**. So the attribute is `#if NET9_0_OR_GREATER`, and on
`net8.0` — where the analyzer cannot see the guard — the guarded call to the `[RequiresDynamicCode]`
`CompileSetters` locally suppresses IL3050 (`#if !NET9_0_OR_GREATER #pragma warning disable IL3050`),
because the **same** `RuntimeFeature.IsDynamicCodeSupported` runtime guard still makes the call safe (it
falls back to reflection under AOT). The `Expression.Compile()` call itself is a banned API (RS0030) and
carries a scoped `#pragma warning disable RS0030`, exactly as the engine's `CompiledBackend` does. The
whole file builds `-warnaserror` clean (0/0) on **both** TFMs.

**Parity.** Because both tiers share construction and `PrepareValue` and differ only in the setter, they
must decode any row to an identical `T`. Tests assert this directly (`forceReflectionSetters:true` vs the
default compiled tier on the JIT test runtime; §8). `RowDecoder<T>.UsesCompiledSetters` exposes which
tier is live for the tier/guard assertions.

## 5. Diagnostic contract (AC3)

All build-time diagnostics reuse **`UnsupportedTypedSchemaException`** (the #163 schema-mapping
diagnostic) and are **deterministic** (same input → identical message). They are thrown while building
the decoder and cached by `TypedRowDecoderCache<T>` (a `Lazy`, which caches and re-throws the same
exception), so a repeated typed collect re-throws byte-for-byte.

Raised by `RowDecoderFactory.Create<T>` (structural encodability):

| Condition | Message names | Notes |
| --- | --- | --- |
| `T` is a value type | the type | "…is a value type." |
| `T` is abstract / an interface | the type | distinguishes "an interface" vs "abstract" |
| `T` has no public parameterless constructor | the type | includes positional-record guidance |
| a mapped property has no public setter | the member + type | "…has no public setter." |

Raised by `DatasetSchema.Derive<T>` (mapping validity — so the **same** message surfaces from both
`As<T>()` and `Collect()`):

| Condition | Message names | Notes |
| --- | --- | --- |
| a property's CLR type has no ADR-0008 mapping | the member + CLR type | lists the supported types |
| two mapped properties share a name (ambiguous) | the type + duplicate name | e.g. a base property re-declared with `new` at a different type; detected **before** `StructType` would raise its lower-level `SchemaValidationException` |

Runtime decode diagnostics (per row, §3): `InvalidOperationException` (SQL `NULL` → non-nullable value
type, names the member), `InvalidCastException` (cell runtime type ≠ property type), and
`ArgumentException` (a mapped column is absent from the row — from the `Row` indexer).

Because schema derivation runs at `As<T>()` (via `TypedSchemaCache<T>`), an unsupported **type** or
ambiguous **name** fails when the `Dataset<T>` is created; a structural problem that only affects
decoding (no parameterless ctor, no setter) fails on the first `Collect()`, **before** the plan is
executed — the decoder is built first, so a bad `T` never drives the executor (a fail-fast test asserts
`CollectCallCount == 0`).

## 6. Public API added

`src/DeltaSharp.Core/PublicAPI.Unshipped.txt` gains exactly two entries (RS0016/RS0017 gate under
`-warnaserror` on **both** TFMs):

```text
DeltaSharp.Dataset<T>.Collect() -> System.Collections.Generic.IReadOnlyList<T>!
DeltaSharp.Dataset<T>.Collect(System.Threading.CancellationToken cancellationToken) -> System.Collections.Generic.IReadOnlyList<T>!
```

`Dataset<T>` and `DataFrame.As<T>()` widen their `T` annotation from
`[DynamicallyAccessedMembers(PublicProperties)]` to
`[DynamicallyAccessedMembers(PublicProperties | PublicParameterlessConstructor)]`, because a typed
`Collect()` now **constructs** `T`. `DynamicallyAccessedMembers` is not part of the RS0016 PublicAPI
format, so this widening does **not** change any PublicAPI entry, and every concrete call (`As<Person>()`)
is unaffected.

## 7. Determinism (AC1)

For a given `T`, the schema (names, types, nullability) and the property↔ordinal binding are fully
determined by `MappableProperties<T>()`'s ordering (§2) — inheritance depth then `MetadataToken` — which
is stable across derivations and independent of the unspecified order of `Type.GetProperties()`.
`RowDecoder<T>.Schema` equals `DatasetSchema.Derive<T>()` and `RowDecoder<T>.BoundColumns` equals the
schema's field names in order; repeated `Create<T>()` yields equal schema and bindings.

## 8. Testing (parity oracle + mutation sensitivity)

`tests/DeltaSharp.Core.Tests/Typed/DatasetEncoderTests.cs` (decoder unit tests) and
`DatasetEncoderCollectTests.cs` (end-to-end typed collect through the `FakeQueryExecutor` seam), both
multi-targeted `net8.0;net10.0`:

- **AC1** — `Schema`/`BoundColumns` match `Derive<T>()` with pinned names/types/nullability; repeated
  derivation is stable.
- **AC2** — decode round-trip for a POCO and an `init`-record; every supported scalar; nullable value
  types (null and non-null); reference-type null; `InvalidOperationException` for `NULL`→non-nullable
  value type; `InvalidCastException` for a wrong-typed cell; a **no-mutation** test that snapshots the
  row's cells and asserts each is unchanged (same object) after decode, plus a `byte[]`-clone
  (non-aliasing) assertion; and an integration test proving `Dataset<T>.Collect()` runs the plan once
  (`CollectCallCount == 1`) via the same executor seam and decodes each row.
- **AC3** — each unsupported shape/type/ambiguity throws `UnsupportedTypedSchemaException` naming the
  member/type; determinism (two derivations give identical messages); the unsupported-type message is
  identical from the schema (`Derive`) and encoder (`Create`) paths; fail-fast (`CollectCallCount == 0`)
  for an undecodable `T`.
- **AC4** — `UseCompiledSetters == RuntimeFeature.IsDynamicCodeSupported` (the guard is the feature
  flag); the reflection tier (the dynamic-code-unavailable path) always works; the default tier uses
  compiled iff dynamic code is supported; and a **parity oracle** — reflection vs compiled decode the
  same rows to identical `T` (values and nulls). On the JIT test runtime dynamic code is supported, so
  the default decoder exercises the compiled tier while `forceReflectionSetters:true` exercises the
  AOT-default tier.

## 9. Summary of files

| File | Change |
| --- | --- |
| `src/DeltaSharp.Core/Session/RowDecoder.cs` | **new** — `RowDecoder<T>` + `PropertyDecodeBinding` (decode + ADR-0008 semantics) |
| `src/DeltaSharp.Core/Session/RowDecoderFactory.cs` | **new** — `RowDecoderFactory` (validation + tier selection) + `TypedRowDecoderCache<T>` |
| `src/DeltaSharp.Core/Session/DatasetSchema.cs` | extract `MappableProperties<T>()`; add ambiguous-name detection to `Derive<T>()` |
| `src/DeltaSharp.Core/Session/Dataset.cs` | widen `T` DAM; add `Collect()` / `Collect(CancellationToken)` |
| `src/DeltaSharp.Core/Session/DataFrame.cs` | widen `As<T>()` `T` DAM |
| `src/DeltaSharp.Core/PublicAPI.Unshipped.txt` | +2 `Collect` entries |
| `tests/DeltaSharp.Core.Tests/Typed/DatasetEncoderTests.cs` | **new** — AC1–AC4 decoder tests |
| `tests/DeltaSharp.Core.Tests/Typed/DatasetEncoderCollectTests.cs` | **new** — typed collect through the executor seam |
