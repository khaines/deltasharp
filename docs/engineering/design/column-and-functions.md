# Column & Functions — the public column-expression API (M1)

> **Status:** living document. Created with
> [STORY-04.3.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md)
> (issue [#164](https://github.com/khaines/deltasharp/issues/164), FEAT-04.3 — column references,
> aliases, and literals). Grounded in [ADR-0008](../../adr/0008-type-system-row-format.md) (type
> system + row format), [ADR-0016](../../adr/0016-shared-logical-type-model-abstractions.md) (the
> shared logical type model in `DeltaSharp.Abstractions`), and
> [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager execution). Builds directly on
> [expression-model.md](expression-model.md) (the internal expression IR, #168),
> [logical-plan-nodes.md](logical-plan-nodes.md) (#167), [api-governance.md](api-governance.md),
> and [repository-layout.md](repository-layout.md). Update it whenever the public `Column`/`Functions`
> surface, the `Lit` type mapping, or the lazy/no-lookup guarantee changes. Operators layer on in
> STORY-04.3.2 (#165) — out of scope here.

## What this is (and is not)

This is the **first public door to column expressions** — the .NET-idiomatic mirror of Apache
Spark's `col("x")`, `col("x").as("y")`, and `lit(value)`. It gives users two public types in
namespace `DeltaSharp`:

- **`Column`** (`src/DeltaSharp.Core/Column.cs`) — a lazy, immutable handle over one node of the
  internal logical expression IR. It exposes only alias builders (`As`/`Alias`/`Name`) and a
  diagnostic `ToString`; the wrapped IR is **never** exposed publicly.
- **`Functions`** (`src/DeltaSharp.Core/Functions.cs`) — the static entry points `Col`/`Column`
  (references) and `Lit` (literals) that construct a `Column`.

It is **not** the expression IR itself (that is `DeltaSharp.Plans.Expressions`, all `internal`,
delivered by #168), and it is **not** evaluation, analysis, or schema binding. Building a `Column`
does **zero work** — it only records intent, the lazy half of the lazy/eager invariant
([ADR-0001](../../adr/0001-execution-strategy.md)). Operators (`+`, `==`, comparisons, boolean
combinators) are STORY-04.3.2 (#165); `Column` is shaped so they slot in without changing the
wrapping contract.

## How the public surface wraps the internal IR

Every public entry point maps 1:1 onto an immutable node of the merged expression IR
([expression-model.md](expression-model.md)). The IR stays hidden behind `Column`:

| Public API | Internal IR node (`DeltaSharp.Plans.Expressions`) | Resolved? | Type hint |
| --- | --- | --- | --- |
| `Functions.Col("x")` / `Functions.Column("x")` | `UnresolvedAttribute("x")` | `false` | `null` (bound at analysis) |
| `Functions.Col("*")` | `UnresolvedStar(target: null)` | `false` | `null` |
| `Functions.Col("t.*")` | `UnresolvedStar(target: ["t"])` | `false` | `null` |
| `Functions.Lit(value)` | `Literal` (via `Literal.Of*`/`Literal.Null`) | `true` | the mapped `DataType` |
| `column.As("y")` / `.Alias("y")` / `.Name("y")` | `Alias(child, "y")` | mirrors child | mirrors child |

- **`Column` wrapping.** `Column` holds an `internal Expression Expr { get; }` and an
  `internal Column(Expression)` constructor. Both are `internal` (materialized to the test project
  via the repo-wide `InternalsVisibleTo "<Assembly>.Tests"` in `Directory.Build.props`), so the
  `DataFrame` API and the analyzer can unwrap a column while the IR never leaks onto the public
  surface — keeping `Core⊄Engine` and the `PublicAPI` baseline honest.
- **`Col`/`Column`.** A bare `"*"` and a qualified `"t.*"` build an `UnresolvedStar` (Spark resolves
  the star at the `col` door); every other name builds a single-part `UnresolvedAttribute`. Parsing
  multi-part/backtick-quoted identifiers is analyzer-side (FEAT-04.5) and deliberately out of scope.
- **`Lit`.** Dispatches on the runtime type of the boxed `object?` value through a pattern `switch`
  onto the existing `Literal.Of*`/`Literal.Null` factories (which already own the CLR→`DataType`
  mapping and the CLR storage shape). `Lit` adds only the type dispatch, the `decimal`→unscaled
  conversion, the temporal→epoch conversions, and the unsupported-type guard.
- **`As`/`Alias`/`Name`.** All three build `new Alias(Expr, name)`; they are Spark parity synonyms
  (`Column.as`/`Column.alias`/`Column.name`). The `Alias` node is what the analyzer preserves as the
  output name during name resolution (AC3).

## The `Lit` .NET-type → `DataType` mapping

`Lit(object?)` maps each supported .NET scalar to its ADR-0008 `DataType` via the internal `Literal`
factories. The value is stored in the `Literal`'s natural CLR storage shape (epoch-day `int` for
dates, epoch-microsecond `long` for timestamps, unscaled `Int128` for decimals, `byte[]` for binary).

| .NET type | `Literal` factory | `DataType` | Stored value shape |
| --- | --- | --- | --- |
| `bool` | `OfBoolean` | `BooleanType` | `bool` |
| `sbyte` | `OfByte` | `ByteType` (signed `tinyint`) | `sbyte` |
| `byte` | `OfShort` | **`ShortType`** (widened) | `short` |
| `short` | `OfShort` | `ShortType` | `short` |
| `int` | `OfInt` | `IntegerType` | `int` |
| `long` | `OfLong` | `LongType` | `long` |
| `float` | `OfFloat` | `FloatType` | `float` |
| `double` | `OfDouble` | `DoubleType` | `double` |
| `string` | `OfString` | `StringType` | `string` |
| `byte[]` | `OfBinary` | `BinaryType` | `byte[]` (defensively copied) |
| `decimal` | `OfDecimal` | `DecimalType(precision, scale)` | unscaled `Int128` |
| `DateOnly` | `OfDate` | `DateType` | epoch-day `int` |
| `DateTime` | `OfTimestamp` | **`TimestampType`** (full instant) | epoch-microsecond `long` |
| `DateTimeOffset` | `OfTimestamp` | `TimestampType` | epoch-microsecond `long` |
| `Column` | — (returned as-is) | mirrors the column | — (idempotent passthrough) |
| `null` | `Null(NullType.Instance)` | `NullType` | — (typed SQL `NULL`) |
| *anything else* | — | — | throws `ArgumentException` (AC4) |

### The `byte` decision (signed vs unsigned)

Spark's `ByteType` is a **signed** 8-bit integer — .NET `sbyte` (range −128…127). A .NET `byte` is
**unsigned** (0…255), so values 128…255 do not fit in `ByteType` and mapping them there would
silently wrap/truncate. To honor "no silent truncation", **`Lit((byte)x)` widens to `ShortType`**,
which losslessly holds every `byte`. Users who want a `ByteType` literal pass an `sbyte`. This is the
only widening in the table; every other supported type maps to its exact Spark-parity `DataType`.

### The `decimal` conversion

`decimal` carries its own scale, so `Lit(123.45m)` records `DecimalType(precision: 5, scale: 2)` with
the unscaled `Int128` `12345`. The conversion reads the four 32-bit words from `decimal.GetBits`:
the low/mid/high words form the 96-bit unsigned magnitude (assembled into a `UInt128`), the flags
word carries the sign bit and the scale (bits 16–22). `precision` is
`max(digit-count(magnitude), scale, 1)` so `precision ≥ scale` always holds (e.g. `-0.001m` →
`DecimalType(3, 3)`, unscaled `-1`). A .NET `decimal` has at most 29 significant digits and scale
≤ 28, so the result always fits inside `DecimalType.MaxPrecision` (38). The conversion is allocation-
free (`Span<int>` on the stack) and calls no banned API.

### The temporal decision: `DateOnly` → date, `DateTime`/`DateTimeOffset` → timestamp

`DeltaSharp` maps the three .NET temporal types to match **Spark's own `lit`** semantics, where a
Python `datetime.date` becomes a `DateType` but a `datetime.datetime` becomes a `TimestampType`:

| .NET type | `DataType` | Rationale |
| --- | --- | --- |
| `DateOnly` | `DateType` | a pure calendar date, no time-of-day — the analogue of `datetime.date`. |
| `DateTime` | **`TimestampType`** | .NET `DateTime` is the analogue of Python `datetime.datetime` and carries a time-of-day (`DateTime.UtcNow`); it maps to a timestamp so the clock is **not silently dropped**. |
| `DateTimeOffset` | `TimestampType` | an explicit instant with an offset; the UTC instant is stored. |

A `DateTime`/`DateTimeOffset` is stored as a UTC **epoch-microsecond** `long`
(`(instant − DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerMicrosecond`) — the same factory
(`Literal.OfTimestamp`) and computation both temporal-with-time types share.

**Earlier design note (superseded).** STORY-04.3.1 originally mapped `DateTime` → `DateType` (date
component only). That silently discarded the time-of-day of the type most users hold
(`DateTime.Now`/`DateTime.UtcNow`), a hard-to-reverse public-API data-loss bug, and it diverged from
Spark. It was corrected to `DateTime` → `TimestampType` (issue [#164](https://github.com/khaines/deltasharp/issues/164) DX-API finding F1).

#### `DateTime.Kind` handling (deterministic)

A `DateTime` may be UTC, Local, or Unspecified. `Lit` normalizes each to a UTC instant
**deterministically**:

| `DateTime.Kind` | Handling | Note |
| --- | --- | --- |
| `Utc` | used directly (epoch-micros from the UTC instant) | fully deterministic. |
| `Local` | `ToUniversalTime()` then epoch-micros | machine-time-zone dependent — inherent to a Local value; Spark has the same session-time-zone behavior. |
| `Unspecified` | treated as **UTC** | the deterministic choice: a naive value never depends on the machine time zone. Documented explicitly so callers can rely on it. |

#### Timestamp microsecond truncation

The epoch-microsecond conversion uses integer division, which **truncates toward zero**. Two
consequences are pinned by tests:

- **Sub-microsecond ticks are dropped.** .NET `DateTime`/`DateTimeOffset` have 100-ns tick
  resolution; the sub-microsecond remainder is discarded (e.g. epoch + 5 ticks → `0` µs).
- **Pre-1970 instants** produce a negative epoch-micros count, also truncated toward zero
  (e.g. one second before the epoch → `-1_000_000` µs). Truncation toward zero (not floor) means a
  pre-1970 sub-microsecond remainder rounds toward the epoch, consistent with the post-1970 direction.

### Why `Lit(object?)` and not typed overloads

`Lit` takes a single `object?` rather than a family of typed overloads (`Lit(int)`, `Lit(string)`, …)
for two reasons: (1) it mirrors Spark's `lit(Any)` / PySpark `lit(value)` single-parameter shape, so
Spark code ports directly and generic `object?` call sites (e.g. row-value plumbing) work uniformly;
and (2) a single boxed parameter gives one place to handle `null` (→ typed SQL `NULL`) and the
idempotent `Column` passthrough. Typed overloads can be added **additively** later (they would bind
more specifically than `object?` without breaking existing callers) if profiling shows the boxing
matters on a hot path.

### `Lit(Column)` idempotence

Passing an existing `Column` to `Lit` returns it **unchanged** (the first arm of the dispatch, before
the type `switch`), mirroring Spark's `lit(col)`. This lets generic `object?` code paths call `Lit`
uniformly whether a cell holds a raw value or an already-built `Column` (issue
[#164](https://github.com/khaines/deltasharp/issues/164) DX-API finding F2).

### Operator forward-guidance for #165

Comparison and equality operators arrive in STORY-04.3.2 (#165). The recommendation there is to expose
**explicit methods** — `col.EqualTo(...)`, `col.Gt(...)`, `col.Lt(...)`, etc. (optionally a
`===`-style `EqNullSafe`) — **rather than overloading `operator ==`/`operator !=`**. Overloading `==`
on a reference type is a known .NET landmine: it must return `bool` (so it cannot return a `Column`
expression), it collides with reference/null-equality checks (`col == null`) and with
`object.Equals`, and it surprises analyzers and LINQ. Explicit builder methods keep the expression-
returning semantics unambiguous and Spark-portable.

### Dotted-name handling (`Col("t.*")`)

`Col` client-parses only the **star qualifier**: a bare `"*"` and a trailing `"t.*"` build an
`UnresolvedStar` (with `target: ["t"]` for the qualified form); every other name — including a dotted
`"t.c"` — becomes a single-part `UnresolvedAttribute` verbatim. Parsing multi-part / backtick-quoted
identifiers into qualifiers is **analyzer-side** (FEAT-04.5). When #160/FEAT-04.5 land, keep this
client-side star parsing consistent with how the analyzer splits qualified names so `Col("t.*")` and
analyzer-resolved qualifiers agree.

### The unsupported-type error (AC4)

Any .NET type not in the table (for example `char`, `Guid`, `uint`, `ulong`, `object`, or an array /
collection) throws a deterministic public `ArgumentException` (paramName `value`) whose message
**names the offending CLR type** (via `value.GetType()`) and lists the supported types.
`ArgumentException` matches the repo's argument-validation convention
(`ArgumentException.ThrowIfNullOrEmpty`, `ArgumentNullException.ThrowIfNull`) used across the
expression IR constructors. `uint`/`ulong` are not Spark types and `object`/array-literals are out of
scope for M1, so throwing (rather than guessing a mapping) is the correct M1 behavior.

### Whitespace-only column names (intentional)

`Col`/`Column` reject only **null or empty** names (`ArgumentException.ThrowIfNullOrEmpty`); a
whitespace-only name (e.g. `Col(" ")`) is **deliberately allowed**. Spark permits quoted whitespace
identifiers (`` col("` `") ``), and DeltaSharp defers identifier lexing/quoting to the analyzer
(FEAT-04.5), so trimming or rejecting whitespace at the `Col` door would be stricter than Spark and
could reject a legitimate quoted name. This is an intentional non-fix.

## Lazy / no-lookup guarantee

Constructing a `Column` — a reference, a literal, or an alias — performs **no** schema lookup and
**no** evaluation:

- `Col`/`Column` build an `UnresolvedAttribute`/`UnresolvedStar` whose `Resolved` is `false` and
  whose `Type` hint is `null`. Only the analyzer (FEAT-04.5) — never construction — resolves a name
  against a catalog/schema (AC1).
- `Lit` builds an always-resolved `Literal` with a known `Type` but touches no data source and runs
  no kernel (AC2).
- `As`/`Alias`/`Name` wrap the child in an `Alias` node; the alias is resolved exactly when its
  child is, and the node survives untouched in the tree for later name resolution (AC3).

The IR nodes are immutable (structural sharing from #167/#168): an alias re-wraps rather than
mutating, and `Column` never rebuilds its `Expr`.

## Spark-parity naming rationale

Spark uses lowercase `col`/`lit`/`functions`. .NET's public-member convention is PascalCase, and the
rest of DeltaSharp's public surface already follows it (`SparkSession`, `DataFrame`,
`SparkSession.Sql`). So DeltaSharp mirrors Spark **semantics** with .NET **idioms**:

| Spark | DeltaSharp | Notes |
| --- | --- | --- |
| `functions` | `Functions` | static class of entry points |
| `col(name)` / `column(name)` | `Functions.Col(name)` / `Functions.Column(name)` | synonyms, as in Spark |
| `lit(value)` | `Functions.Lit(value)` | |
| `Column.as(name)` | `Column.As(name)` | `as` is a C# keyword → `As` |
| `Column.alias(name)` | `Column.Alias(name)` | synonym |
| `Column.name(name)` | `Column.Name(name)` | synonym |

Code ports directly: Spark `df.select(col("salary").as("s"))` becomes
`df.Select(Functions.Col("salary").As("s"))` (the `Select` consumer arrives with #160).

## AC → test mapping

Tests live in `tests/DeltaSharp.Core.Tests/ColumnTests.cs` and `FunctionsLitTests.cs`.

| AC | Requirement | Tests |
| --- | --- | --- |
| **AC1** | `Col`/`Column`/star create an **unresolved** attribute with no schema lookup (lazy; `Resolved == false`, `Type == null`) | `ColumnTests.Col_WrapsUnresolvedAttribute_WithoutSchemaLookup`, `Column_IsAliasForCol`, `Col_Star_WrapsBareUnresolvedStar`, `Col_QualifiedStar_WrapsTargetedUnresolvedStar`, `Col_NullOrEmptyName_Throws` |
| **AC2** | `Lit(value)` for scalars incl. null/decimal/date/timestamp records an ADR-0008 `DataType` | `FunctionsLitTests.Lit_*` (one per supported type: bool, sbyte, byte-widen, short, int, long, float, double, string, empty-string, bytes, decimal, zero/large/high-scale-negative decimal, DateOnly, DateTime→Timestamp incl. Unspecified/Local Kind + pre-1970 + sub-µs truncation, DateTimeOffset, `Column` passthrough, null) |
| **AC3** | An alias (`col.As("x")`) is preserved as an `Alias` node for analyzer resolution | `ColumnTests.As_WrapsExpressionInAliasPreservingChildAndName`, `Alias_And_Name_AreEquivalentToAs`, `As_PreservedInExpressionTree`, `As_NullOrEmptyAlias_Throws` |
| **AC4** | Invalid literal .NET type → deterministic public error naming the unsupported type | `FunctionsLitTests.Lit_UnsupportedType_ThrowsNamingType`, `Lit_UnsupportedChar_ThrowsNamingType`, `Lit_UnsupportedTypes_ThrowNamingType` (uint/object/array) |
