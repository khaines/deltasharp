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
| `DateTime` | `OfDate` | `DateType` (date component) | epoch-day `int` |
| `DateTimeOffset` | `OfTimestamp` | `TimestampType` | epoch-microsecond `long` |
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
word carries the sign bit and the scale (bits 16–23). `precision` is
`max(digit-count(magnitude), scale, 1)` so `precision ≥ scale` always holds (e.g. `-0.001m` →
`DecimalType(3, 3)`, unscaled `-1`). A .NET `decimal` has at most 29 significant digits and scale
≤ 28, so the result always fits inside `DecimalType.MaxPrecision` (38). The conversion is allocation-
free (`Span<int>` on the stack) and calls no banned API.

### The `DateTime` decision

Per the story mapping, `DateTime` → `DateType` using its **date component** (`DateOnly.FromDateTime`);
`DateType` does not represent a time-of-day, so the time is not carried. Users who need a timestamp
literal pass a `DateTimeOffset`, which maps to `TimestampType` as a UTC epoch-microsecond instant
(`(value − DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerMicrosecond`).

### The unsupported-type error (AC4)

Any .NET type not in the table (for example `char`, `Guid`, `uint`, `ulong`) throws a deterministic
public `ArgumentException` (paramName `value`) whose message **names the offending CLR type** (via
`value.GetType()`) and lists the supported types. `ArgumentException` matches the repo's argument-
validation convention (`ArgumentException.ThrowIfNullOrEmpty`, `ArgumentNullException.ThrowIfNull`)
used across the expression IR constructors.

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
| **AC2** | `Lit(value)` for scalars incl. null/decimal/date/timestamp records an ADR-0008 `DataType` | `FunctionsLitTests.Lit_*` (one per supported type: bool, sbyte, byte-widen, short, int, long, float, double, string, bytes, decimal, negative-decimal, DateOnly, DateTime, DateTimeOffset, null) |
| **AC3** | An alias (`col.As("x")`) is preserved as an `Alias` node for analyzer resolution | `ColumnTests.As_WrapsExpressionInAliasPreservingChildAndName`, `Alias_And_Name_AreEquivalentToAs`, `As_PreservedInExpressionTree`, `As_NullOrEmptyAlias_Throws` |
| **AC4** | Invalid literal .NET type → deterministic public error naming the unsupported type | `FunctionsLitTests.Lit_UnsupportedType_ThrowsNamingType`, `Lit_UnsupportedChar_ThrowsNamingType` |
