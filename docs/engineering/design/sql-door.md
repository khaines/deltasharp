# SQL door into the shared plan pipeline (M1)

> **Status:** living document. Created with
> [STORY-04.1.3](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-04-core-api-logical-plan.md#story-0413-sql-door-into-shared-plan-pipeline)
> (FEAT-04.1, issue #159). Builds on STORY-04.1.1 (#157, the `SparkSession` doors and lifecycle —
> [sparksession-lifecycle.md](sparksession-lifecycle.md)), STORY-04.1.2 (#158, the read door and
> `DataFrame`-over-plan construction — [read-door.md](read-door.md)), the immutable logical/expression
> IR (#167/#168, [logical-plan-nodes.md](logical-plan-nodes.md)), and the analyzer (#170,
> [analyzer-resolution.md](analyzer-resolution.md)). Grounded in
> [ADR-0001](../../adr/0001-execution-strategy.md) (lazy/eager execution),
> [ADR-0007](../../adr/0007-sql-frontend.md) (SQL frontend & dialect),
> [ADR-0008](../../adr/0008-type-system-row-format.md) (type system), and
> [ADR-0014](../../adr/0014-target-framework-aot.md) (multi-targeting / AOT posture). Update it
> whenever the supported subset, the parser, the lowering table, or the diagnostic model changes.

## Why this exists

DeltaSharp's central invariant is:

> **Transformations are lazy; actions are eager.** Building a plan does **no** work — no catalog is
> consulted, no schema is bound, no backend runs. Only an action triggers the engine.

STORY-04.1.3 delivers the **SQL data-in door** on `SparkSession`:

- **`spark.Sql(sqlText)`** parses a SQL string and lowers it into the **same** unresolved logical
  plan the DataFrame API builds, then wraps it in a `DataFrame`. SQL and DataFrame code therefore
  **converge after parsing/lowering** onto one shared IR — one analyzer, one optimizer, one execution
  path (AC3). The door is a **transformation**: it constructs a plan and returns; it resolves nothing
  and executes nothing (AC1).

The M1 door implements a deliberately **small** SQL subset. Everything outside it fails at **parse
time** with a deterministic `SqlParseException` that names the offending construct — before any
analysis or execution (AC2). The full ANTLR4 frontend (ADR-0007) supersedes this focused door in
EPIC-07.

## Public API surface (Spark parity)

All additions live in the packable, `net8.0;net10.0` `DeltaSharp.Core` assembly and are tracked in
`PublicAPI.Unshipped.txt` (RS0016/RS0017 are build errors under `-warnaserror`).

```csharp
namespace DeltaSharp;

public sealed class SparkSession
{
    // Existing (STORY-04.1.1) signature — now parses & lowers instead of throwing NotSupportedException.
    public DataFrame Sql(string sqlText);
}

// New public error surface for the door.
public enum SqlParseErrorKind { SyntaxError, UnsupportedFeature }

public sealed class SqlParseException : Exception
{
    public SqlParseException();
    public SqlParseException(string message);
    public SqlParseException(string message, Exception? innerException);
    public SqlParseErrorKind ErrorKind { get; } // structured reason (SyntaxError | UnsupportedFeature)
    public string? Construct { get; }            // named construct for UnsupportedFeature, else null
}
```

`Sql` is defined at `src/DeltaSharp.Core/Session/SparkSession.cs:220`. `SqlParseException` /
`SqlParseErrorKind` live in `src/DeltaSharp.Core/Sql/SqlParseException.cs` and
`src/DeltaSharp.Core/Sql/SqlParseErrorKind.cs`.

### `Sql` entry point (`SparkSession.cs:220-226`)

```csharp
public DataFrame Sql(string sqlText)
{
    EnsureNotStopped(nameof(Sql));            // AC4: same lifecycle guard as Read (line 222)
    ArgumentNullException.ThrowIfNull(sqlText);
    LogicalPlan plan = SqlParser.Parse(sqlText); // AC2: parse/lower; throws SqlParseException on failure
    return new DataFrame(this, plan);         // AC1: lazy DataFrame over an unresolved plan
}
```

The lifecycle guard runs **first**, so a stopped session throws before the null check or any parse
work (`SparkSession.cs:222`) — identical to `Read` (`SparkSession.cs:189`).

## Parser choice: focused recursive descent (not ANTLR yet)

[ADR-0007](../../adr/0007-sql-frontend.md) selects an **ANTLR4 grammar mirroring Spark's
`SqlBase.g4`** for the SQL frontend and grows coverage over time under a dedicated
`sql-language-frontend-engineer` seat. That is the target for **EPIC-07**.

For this **Size-S M1 door** we deliberately ship a **small hand-written recursive-descent parser**
instead, because:

- The repository has **no ANTLR toolchain today** — `Directory.Packages.props` declares no
  `Antlr4.Runtime` / grammar-generation package. Adding a code-generation build step and a runtime
  dependency to a packable, trim/AOT-annotation-clean `net8.0;net10.0` library (ADR-0014) is a
  significant, hard-to-review change that is out of proportion to a Size-S story.
- The M1 subset is tiny (one statement shape, a handful of operators). A ~450-line hand-written
  lexer + parser is easier to review line-by-line, has zero new dependencies, and is trivially
  AOT-safe (no reflection, no `Guid`, no dynamic code).
- The lowering **target** — the shared logical/expression IR — is identical regardless of parser
  technology, so replacing this door with the ANTLR frontend later changes only the front half; the
  lowering table below and the shared nodes it targets are stable.

This is a documented, temporary deviation from ADR-0007's *implementation* (not its *decision*): the
ANTLR4 frontend remains the accepted direction and supersedes this door in EPIC-07.

## Supported M1 subset (grammar)

The parser (`src/DeltaSharp.Core/Sql/SqlParser.cs`) accepts exactly this grammar (EBNF; keywords are
case-insensitive, ANSI parity):

```ebnf
statement      := SELECT selectList FROM relation [ WHERE booleanExpr ] EOF
selectList     := '*' | selectItem (',' selectItem)*
selectItem     := expr [ [AS] identifier ] | qualifiedStar
qualifiedStar  := identifier ('.' identifier)* '.' '*'
relation       := identifier ('.' identifier)*          (* multipart table id, e.g. db.t *)
booleanExpr    := orExpr
orExpr         := andExpr (OR andExpr)*
andExpr        := notExpr (AND notExpr)*
notExpr        := NOT notExpr | comparison
comparison     := additive (compOp additive)?
compOp         := '=' | '<>' | '!=' | '<' | '<=' | '>' | '>='
additive       := multiplicative (('+' | '-') multiplicative)*
multiplicative := unary (('*' | '/' | '%') unary)*
unary          := ('+' | '-') numericLiteral | primary
primary        := literal | columnRef | '(' expr ')'
columnRef      := identifier ('.' identifier)*
literal        := integer | decimal | string | TRUE | FALSE | NULL
identifier     := unquotedIdent | '`' backtickIdent '`'
```

**Precedence** (lowest → highest): `OR` < `AND` < `NOT` < comparison < `+`/`-` < `*`/`/`/`%` <
unary sign < primary. This mirrors Spark/ANSI SQL and matches the DataFrame `Column` operator
composition so equivalent expressions build identical trees (AC3).

**Lexer** (`SqlLexer.cs`): skips whitespace; recognizes the keywords `SELECT FROM WHERE AS AND OR NOT
TRUE FALSE NULL`; unquoted identifiers `[A-Za-z_][A-Za-z0-9_]*` and backtick-quoted identifiers
(with `` `` `` escaping a backtick); single-quoted string literals (with `''` escaping a quote);
integer vs. decimal numeric literals (a `.` or exponent makes it decimal); and the punctuation /
operator glyphs. Every token carries its 1-based source position for deterministic diagnostics.

### Literal typing (matches `Functions.Lit`)

| SQL literal            | Lowered node                          | Rationale (DataFrame parity)                    |
| ---------------------- | ------------------------------------- | ----------------------------------------------- |
| `1` (fits `int`)       | `Literal.OfInt`                       | same as `Functions.Lit(1)`                      |
| `9999999999` (> `int`) | `Literal.OfLong`                      | widens like a CLR `long` literal                |
| `1.5`, `2e3`           | `Literal.OfDouble`                    | same as `Functions.Lit(1.5)`; DECIMAL → EPIC-07 |
| `'abc'`                | `Literal.OfString`                    | same as `Functions.Lit("abc")`                  |
| `TRUE` / `FALSE`       | `Literal.OfBoolean`                   | same as `Functions.Lit(true)`                   |
| `NULL`                 | `Literal.Null(NullType.Instance)`     | same as `Functions.Lit(null)`                   |
| `-1` (unary on number) | `Literal.OfInt(-1)` (sign folded)     | same as `Functions.Lit(-1)`                     |

Unary `+`/`-` is accepted **only** directly in front of a numeric literal and folded into the literal
(`SqlParser.cs:329-350`); a general negation node is out of the M1 subset (there is no `UnaryMinus`
IR node yet).

## Lowering table (SQL construct → shared logical/expression node) — AC3

Every construct lowers into the **same** internal IR node the DataFrame/`Column` API builds, so the
two front ends converge. Nodes live in `src/DeltaSharp.Core/Plans/Logical/` and
`src/DeltaSharp.Core/Plans/Expressions/`.

| SQL construct                    | Shared node built                                            | Parser site           | DataFrame-API twin                          |
| -------------------------------- | ----------------------------------------------------------- | --------------------- | ------------------------------------------- |
| `SELECT list …`                  | `Project(projectList, child)`                               | `SqlParser.cs:101`    | `DataFrame.Select(...)`                     |
| `WHERE predicate`                | `Filter(predicate, child)`                                  | `SqlParser.cs:97`     | `DataFrame.Filter(...)`                     |
| `FROM t` / `FROM db.t`           | `UnresolvedRelation([parts])`                               | `SqlParser.cs:206`    | (table door; #158 relation family)          |
| bare `*`                         | `UnresolvedStar()`                                          | `SqlParser.cs:135`    | `Functions.Col("*")`                        |
| `t.*`                            | `UnresolvedStar([target])`                                  | `SqlParser.cs:417`    | `Functions.Col("t.*")`                      |
| column `a` / `t.a`               | `UnresolvedAttribute([parts])`                              | `SqlParser.cs:430`    | `Functions.Col("a")`                        |
| `expr AS x` / `expr x`           | `Alias(expr, "x")`                                          | `SqlParser.cs:149,157`| `Column.As("x")`                            |
| `= <> != < <= > >=`              | `BinaryComparison(l, r, op)`                                | `SqlParser.cs:271`    | `Column.EqualTo/Lt/Gt/...`                  |
| `+ - * / %`                      | `BinaryArithmetic(l, r, op)`                                | `SqlParser.cs:297,325`| `Column` `+ - * / %` operators              |
| `AND` / `OR` / `NOT`             | `And` / `Or` / `Not`                                        | `SqlParser.cs:247,235,258` | `Column.And/Or/Not`                    |
| literals                         | `Literal.*` (see table above)                               | `SqlParser.cs:433`    | `Functions.Lit(...)`                        |

A `SELECT list FROM t WHERE p` therefore lowers to `Project(list, Filter(p, UnresolvedRelation([t])))`
— the exact tree `df.Filter(p).Select(list)` builds. The tests assert this by **structural equality**
(`LogicalPlan.Equals`, which compares node type + descriptor + children recursively —
`Plans/TreeNode.cs:177`) against both a hand-built expected plan and the live
`Functions.Col`/`Column`-operator expressions (`SqlDoorTests.cs`).

Nothing here resolves: `UnresolvedRelation.Resolved` and `UnresolvedAttribute.Resolved` are `false`,
so the returned plan is unresolved and the analyzer binds names later (AC1). `spark.Sql(...)` touches
no catalog and no executor.

## Unsupported-feature diagnostic model — AC2

Anything outside the subset raises a deterministic `SqlParseException` at **parse time**, so it can
never reach analysis or a backend. The exception carries a structured `ErrorKind` and, for
unsupported constructs, the named `Construct` — so callers branch without matching message text (the
same rationale as the analyzer's `AnalysisException.Kind`). Messages are built only from deterministic
inputs (the construct name / offending token and its 1-based position), so they are stable and
catchable (`SqlParseException.cs`).

| Rejected input                         | `ErrorKind`         | `Construct`                                 | Detected at                    |
| -------------------------------------- | ------------------- | ------------------------------------------- | ------------------------------ |
| `… JOIN …`, `INNER/LEFT/… JOIN`        | `UnsupportedFeature`| `JOIN`                                      | `ExpectEnd` → `MapTrailingConstruct` |
| `FROM t, u`                            | `UnsupportedFeature`| `comma-separated table list (implicit join)`| `ExpectEnd`                    |
| `GROUP BY` / `ORDER BY` / `HAVING`     | `UnsupportedFeature`| `GROUP BY` / `ORDER BY` / `HAVING`          | `ExpectEnd`                    |
| `LIMIT` / `OFFSET` / `WINDOW`          | `UnsupportedFeature`| `LIMIT` / `OFFSET` / `WINDOW`               | `ExpectEnd`                    |
| `UNION` / `INTERSECT` / `EXCEPT`       | `UnsupportedFeature`| `set operation (UNION/INTERSECT/EXCEPT)`    | `ExpectEnd`                    |
| `SELECT DISTINCT …`                    | `UnsupportedFeature`| `SELECT DISTINCT`                           | `RejectSetQuantifier` (`:104`) |
| `count(a)`, any `name(...)`            | `UnsupportedFeature`| `function call`                             | `ParseColumnReference` (`:405`)|
| `FROM (SELECT …)`, `(SELECT …)` operand| `UnsupportedFeature`| `subquery`                                  | `ParseRelation`/`ParsePrimary` |
| `INSERT/UPDATE/DELETE/MERGE`           | `UnsupportedFeature`| `INSERT` / `UPDATE` / …                     | `MapStatementKeyword` (`:69`)  |
| `CREATE/DROP/ALTER/TRUNCATE/…`         | `UnsupportedFeature`| `CREATE` / `DROP` / …                       | `MapStatementKeyword`          |
| `WITH`, `VALUES`, `SHOW`, `EXPLAIN`, … | `UnsupportedFeature`| `WITH (common table expression)` / …        | `MapStatementKeyword`          |
| empty / not `SELECT` / missing `FROM`  | `SyntaxError`       | `null`                                      | statement / relation parse     |
| unterminated string / bad char         | `SyntaxError`       | `null`                                      | `SqlLexer`                     |
| `SELECT a FROM t WHERE` (dangling)     | `SyntaxError`       | `null`                                      | expression parse               |

"Recognizable SQL we do not implement yet" → `UnsupportedFeature` (names the construct);
"not well-formed against the grammar" → `SyntaxError`. Both are thrown from `SqlParser.Parse` before
`Sql` constructs any `DataFrame`, so **no execution is invoked** (AC2). Tests register a
`ThrowingQueryExecutor` and confirm the parse error surfaces as `SqlParseException`, never a
`QueryExecutionException`.

## Lifecycle parity with `Read` — AC4

`Sql` calls the **same** `EnsureNotStopped(nameof(Sql))` guard `Read` uses
(`SparkSession.cs:222` vs. `:189`), which throws the shared public `SessionStoppedException` built by
`SessionStoppedException.ForMember(memberName, appName)` (`SessionStoppedException.cs:51`). On a
stopped session, `spark.Sql(...)` and `spark.Read` therefore throw the **same exception type** with
the **same deterministic per-member message shape**; the only difference is the member name embedded
in the text. The guard runs before the null-argument check, so even `spark.Sql(null!)` on a stopped
session throws `SessionStoppedException`, not `ArgumentNullException` (asserted in `SqlDoorTests`).

## Layering & governance

- **Core stays engine-free.** The parser and door live entirely in `DeltaSharp.Core` and reference
  only the internal IR + `DeltaSharp.Abstractions` (`DataType`/`NullType`). No `DeltaSharp.Engine` /
  `DeltaSharp.Executor` (net10.0-only) reference is introduced; Core remains packable `net8.0;net10.0`
  (ADR-0014).
- **AOT/trim clean.** The parser is plain recursive descent — no reflection, `Guid.NewGuid`, or
  dynamic code — so it passes the standing trim/AOT/BannedApi analyzers under `-warnaserror`.
- **Public API tracked.** `SqlParseException`, `SqlParseErrorKind`, and their members are in
  `PublicAPI.Unshipped.txt`; the parser/lexer and IR nodes stay `internal`.

## Testing

`tests/DeltaSharp.Core.Tests/SqlDoor/SqlDoorTests.cs` (31 cases across both TFMs):

- **AC1 (lazy):** `SELECT * FROM t` and `SELECT a FROM t WHERE b > 1` build `Project`/`Filter`/
  `UnresolvedRelation` with `Plan.Resolved == false`; a `ThrowingQueryExecutor` is installed to prove
  the door never executes.
- **AC3 (shared nodes):** the lowered plan is **structurally equal** to a hand-built expected tree and
  the lowered expressions **equal** the live `Functions.Col`/`Column`-operator expressions
  (projection, filter, alias, qualified star, boolean/arithmetic precedence, negative-literal folding).
- **AC2 (unsupported):** a table of constructs (joins, aggregates, `GROUP BY`/`ORDER BY`/`HAVING`/
  `LIMIT`, subqueries, function calls, set operations, DDL/DML) each throw `UnsupportedFeature` naming
  the construct; malformed inputs throw `SyntaxError`; determinism is asserted (same input → identical
  message).
- **AC4 (lifecycle):** a stopped session throws `SessionStoppedException` from `Sql` with the same
  `ForMember` message model as `Read`, and the guard precedes the null check.

End-to-end execution of a supported `spark.Sql(...).Collect()` is **not** covered here: the M1 catalog
registers only table *schemas*, not data-backed tables, so there is no executable table source for a
SQL `FROM t` yet (data-in tables arrive with the catalog/table door in a later story). The shared
pipeline below the door is already exercised end-to-end by the DataFrame action tests, which run over
the identical IR nodes this door produces.
