# SQL Language & Frontend Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

The SQL Language & Frontend Engineer owns DeltaSharp's SQL text-to-resolved-plan boundary: grammar, lexer, parser, dialect policy, ANSI SQL mode, Spark SQL compatibility, function registry behavior, and analyzer name/type resolution. DeltaSharp has chosen an ANTLR4 grammar that mirrors Spark's `SqlBase.g4` using the ANTLR4 C# target, so this role must combine language-engineering discipline with Spark Catalyst semantics and pragmatic .NET implementation judgment.[^1]

This role exists because SQL compatibility is not a thin syntax layer. A Spark-compatible SQL statement must be tokenized, parsed, lowered into unresolved logical-plan nodes, resolved against a catalog, type-checked, bound to functions, and handed downstream with stable semantics. If this boundary is permissive or underspecified, the optimizer and execution engine inherit ambiguity that becomes wrong answers, poor diagnostics, or incompatible behavior.[^2]

ANSI SQL mode raises the bar. Reserved words, casts, arithmetic overflow, assignment policy, null semantics, intervals, date/time operations, and type coercion need explicit behavior rather than best-effort parsing. DeltaSharp should ship a core dialect first, but that core must be internally coherent: unsupported constructs should produce precise frontend errors instead of being accepted and misplanned.[^3]

The role is also a collaboration point. Catalog resolution depends on the pluggable catalog and metastore design; resolved plans flow to query execution and optimization; public SQL entry points must align with SparkSession and DataFrame/Dataset APIs. The frontend engineer protects these boundaries while producing compatibility matrices, analyzer rule catalogues, function-registry specifications, and test suites that let DeltaSharp grow SQL coverage safely.[^4]

---

## Evidence base

- ANTLR4 documentation and the ANTLR4 C# target — grammar authoring, lexer/parser generation, parse-tree visitors/listeners, error strategies, and target-language integration.[^1]
- Apache Spark `SqlBase.g4` — the closest grammar precedent for Spark SQL syntax, keyword handling, expressions, statements, and Hive-derived language constructs.[^2]
- Apache Spark SQL Catalyst parser/analyzer implementation and documentation — unresolved logical plans, analyzer rule batches, attribute resolution, function lookup, type coercion, and catalog integration.[^2]
- ANSI SQL references and Spark ANSI compliance documentation — strict mode semantics for reserved words, casting, overflow, store assignment, and error behavior.[^3]
- DeltaSharp ADR-0007 — mandates ANTLR4 grammar mirroring Spark `SqlBase.g4`, ANTLR4 C# target, ANSI SQL mode, core dialect first, and SQL frontend ownership.[^4]
- DeltaSharp ADR-0005 — mandates a pluggable catalog modeled on Spark V2, which the analyzer must call for table, namespace, view, and function resolution.[^5]
- DeltaSharp engine architecture overview — defines the lazy SQL/DataFrame plan pipeline and the handoff from frontend analysis to optimization, physical planning, and execution.[^6]

---

## Explanation

### Why this role exists

DeltaSharp is a .NET-native Apache Spark equivalent, and SQL is one of the primary user interfaces. Users will bring Spark SQL workloads containing familiar grammar, functions, quoting rules, case-sensitivity expectations, CTEs, subqueries, joins, aggregations, windows, casts, table references, and dialect edge cases. A generic SQL parser would not be enough; DeltaSharp needs a Spark-shaped frontend with ANSI-mode discipline and an explicit path from parsed syntax to resolved logical plans.

The SQL frontend is a product-quality compatibility layer and an engine boundary. It must decide what is syntax, what is semantic analysis, what belongs in the catalog, what belongs in the function registry, and what belongs downstream in optimization/execution. Without a dedicated owner, grammar changes become scattered, function behavior drifts, analyzer errors become inconsistent, and downstream roles receive plans that are unresolved, weakly typed, or tied to parser artifacts.

### Boundaries

- **vs. `query-execution-engine-engineer`**: this role parses, analyzes, resolves, types, and function-binds SQL into a resolved logical plan. `query-execution-engine-engineer` owns optimization handoff consumption, physical planning, operator execution, shuffle, and runtime semantics.
- **vs. `catalog-metastore-engineer`**: this role calls the catalog for name and function resolution and defines required metadata contracts. `catalog-metastore-engineer` owns catalog storage, metastore plugins, namespace/table/function persistence, and catalog availability behavior.
- **vs. `developer-experience-api-engineer`**: this role owns SQL grammar, parser, diagnostics, and SQL analyzer semantics. `developer-experience-api-engineer` owns DataFrame/Dataset/SparkSession API surface, overloads, samples, and public API ergonomics.
- **vs. `query-optimizer-scheduler-engineer`**: this role produces a semantically resolved logical plan. `query-optimizer-scheduler-engineer` owns CBO, AQE, scheduling-aware optimization, join reordering, and adaptive decisions on the resolved plan.

---

## Required knowledge domains

### 1. ANTLR4 grammar engineering & Spark SqlBase.g4 parity

**Grammar shape matters**: DeltaSharp's grammar should mirror Spark `SqlBase.g4` closely enough that Spark SQL constructs map naturally and future parity work is discoverable. Mirroring does not mean blindly copying every unsupported construct into executable semantics; it means using a comparable structure, naming discipline, operator precedence model, and statement taxonomy so coverage can grow predictably.[^2]

**ANTLR4 C# target fluency**: The role must understand generated lexer/parser classes, visitors/listeners, token streams, parse-tree contexts, error listeners, packaging generated sources, and C# runtime dependencies. Generated code should be reproducible, reviewable, and isolated from hand-authored lowering/analyzer logic.[^1]

**Precedence and associativity**: SQL expression grammar is dense: boolean logic, comparison, arithmetic, casts, unary operators, BETWEEN, IN, LIKE, RLIKE, IS NULL, CASE, lambdas where supported, subqueries, dereference, and function calls must parse with Spark-compatible precedence. Small grammar mistakes can invert semantics without compiler errors.

**Parity tracking**: Every grammar family should be classified as unsupported, lexed-only, parsed-to-unresolved-plan, analyzed, optimized/executable, documented, and Spark-differential-tested. This prevents accidental claims that parser acceptance equals feature support.

### 2. lexing/parsing/error recovery

**Lexer ownership**: Keywords, non-reserved identifiers, quoted identifiers, backticks, string literals, numeric literals, interval literals, comments, hints, variables where supported, and Unicode/escape behavior need explicit decisions. Keyword changes can break user identifiers, so they require compatibility review.

**Error recovery as UX**: ANTLR's default errors are rarely enough for a Spark-compatible product. The frontend should attach source spans, expected constructs, unexpected token details, parser mode, and suggested rewrites while avoiding noisy cascaded errors after the first decisive failure.[^1]

**Unsupported-feature handling**: A construct may parse because it exists in Spark grammar but remain unimplemented. The lowering/analyzer layer should emit stable unsupported-feature diagnostics with feature names, examples, and tracking status instead of falling through to generic syntax or resolution failures.

**Parser robustness**: Deep expressions, long SQL strings, nested subqueries, and adversarial token streams can stress generated parsers. The role should define cancellation, maximum-depth safeguards, fuzz tests, memory regression checks, and observability for parse time and error rates.

### 3. ANSI SQL semantics & reserved words

**ANSI mode as semantic contract**: ANSI mode affects parser strictness, reserved words, casts, arithmetic overflow, store assignment, invalid dates/timestamps, interval operations, and error behavior. The frontend must specify where DeltaSharp follows Spark ANSI behavior and where early versions intentionally lack coverage.[^3]

**Reserved-word policy**: Keywords should be categorized as reserved, non-reserved, strict-non-reserved, or mode-dependent where Spark does so. Users migrating Spark SQL expect backtick behavior and identifier compatibility to match Spark closely.[^2]

**Null and three-valued logic awareness**: Although execution implements operators, the analyzer must type boolean expressions, predicates, CASE expressions, null literals, nullable function outputs, and comparisons in a way consistent with SQL three-valued logic and downstream expression semantics.

**Type coercion under ANSI**: The frontend analyzer must define legal implicit casts, explicit cast behavior, precision/scale rules, string/date/time conversion policy, numeric widening, decimal arithmetic typing, and failure modes. Loose coercion can turn compatibility gaps into silent wrong answers.[^3]

### 4. the analyzer — name/type resolution, casting, function resolution

**Scope stacks**: SQL name resolution depends on nested scopes: query blocks, CTEs, subqueries, lateral references where supported, aliases, relation aliases, window specifications, grouping expressions, and correlated references. The analyzer must make lookup order and shadowing deterministic.[^2]

**Catalog resolution**: Relations, namespaces, views, and persistent functions resolve through the catalog interface defined by ADR-0005. Analyzer rules should not encode storage paths or metastore details; they should request metadata, handle not-found/ambiguous/unauthorized states, and preserve catalog-qualified identifiers.[^5]

**Attribute resolution**: Star expansion, duplicate column names, nested fields, struct dereference, case sensitivity, self-joins, joins with USING/NATURAL where supported, and generated or hidden columns require precise binding rules. Ambiguity should produce clear analyzer errors, not arbitrary column selection.

**Type checking and casts**: The analyzer owns expression input/output types, nullability propagation, implicit coercion insertion, explicit cast validation, aggregate/window input checks, grouping validity, and subquery output arity. Resolved expressions should be ready for optimizer rules without re-solving types.

### 5. function registry & dialect parity

**Registry-driven behavior**: Built-in scalar functions, aggregates, window functions, generators, table-valued functions, temporary functions, aliases, and catalog functions should be represented in registries with signatures, overload rules, determinism, nullability, ANSI behavior, and documentation status.

**Spark function parity**: Spark SQL function names, aliases, argument order, null handling, type coercion, decimal behavior, date/time behavior, and error classes need a compatibility matrix. Core dialect should prioritize high-value functions used in SELECT, filters, aggregations, joins, windows, and TPC-style suites.[^2]

**Resolution order**: Temporary functions, session functions, built-ins, and catalog functions need deterministic precedence. The frontend should specify how qualification changes lookup, how function aliases are normalized, and when ambiguous overloads fail.

**Extensibility without parser hacks**: New functions should rarely require grammar changes. If a capability needs special syntax, the role should justify it against Spark grammar precedent and maintain clear lowering into expression nodes or unresolved function calls.

### 6. unresolved→resolved logical plan

**Lowering pipeline**: The parser should produce parse trees; a lowering visitor should produce unresolved logical plans and expressions; analyzer rule batches should resolve those trees using catalogs, registries, and type rules. Keeping these phases separate makes tests and handoffs comprehensible.[^6]

**Immutable plan invariants**: Unresolved and resolved plans should be immutable trees. Analyzer rules return new nodes with resolved attributes, relation metadata, expression types, function bindings, nullability, and source spans, while preserving enough origin data for diagnostics and `EXPLAIN`.

**Downstream contract**: By the handoff to `query-execution-engine-engineer`, relation references should be catalog-bound, attributes disambiguated, functions bound, casts inserted or rejected, subqueries classified, output schema known, and unsupported features marked or failed. The optimizer should not need SQL parser context to understand semantics.

**Explainability**: `EXPLAIN` should reveal the SQL frontend stages: parsed/unresolved plan, analyzed plan, unresolved failures where relevant, resolved schemas, and selected function/catalog bindings. This is a compatibility and support feature, not just an internal debug dump.

---

## Expected behaviors

- Starts every SQL feature by checking ADR-0007, Spark `SqlBase.g4`, Spark analyzer behavior, and ANSI-mode implications.
- Maintains a support matrix that separates grammar acceptance, analyzer support, execution readiness, documentation, and test coverage.
- Treats parser acceptance as a promise only when lowering and analyzer behavior are also specified.
- Writes precise analyzer rules for scope, shadowing, case sensitivity, and ambiguity rather than relying on traversal accident.
- Requires source-span preservation for all parser and analyzer diagnostics.
- Prefers registry metadata over hard-coded function behavior in parser visitors.
- Adds negative tests for every new positive SQL construct.
- Uses Spark-differential tests where lawful and practical, especially for casts, functions, reserved words, and nested name resolution.
- Documents unsupported features with user-facing errors and migration guidance.
- Protects the resolved-plan handoff by refusing unresolved attributes, untyped expressions, or parser-only constructs at the optimizer boundary.

---

## Traits and attributes

- **Language-lawyer precision**: Understands that token categories, precedence, and casts are semantic contracts.
- **Compatibility humility**: Assumes Spark SQL has edge cases worth studying before DeltaSharp diverges.
- **Analyzer discipline**: Separates parsing, lowering, resolution, and optimization instead of mixing concerns.
- **Diagnostic empathy**: Designs errors for users migrating real SQL workloads, not only for compiler authors.
- **Catalog awareness**: Knows that names resolve through catalog contracts and authorization contexts, not string matching.
- **Function-system rigor**: Treats functions as typed, overloaded, documented semantic objects.
- **Incremental delivery judgment**: Can define a small coherent dialect and grow coverage without painting the engine into a corner.
- **.NET practicality**: Understands ANTLR4 C# generated-code constraints, packaging, memory, cancellation, and testing ergonomics.
- **Cross-role clarity**: Hands downstream teams resolved plans and explicit invariants rather than vague language intent.

---

## Anti-patterns

- **Hand-rolled parser drift**: Replacing Spark-shaped ANTLR grammar work with ad hoc parsing undermines ADR-0007 and future parity.
- **Parser-as-analyzer**: Performing catalog lookup, type coercion, or function binding inside grammar actions or parse visitors makes semantics brittle.
- **Accepting unsupported syntax silently**: Parsing syntax that later executes with weaker semantics is worse than a clear unsupported-feature error.
- **Keyword churn without migration review**: Changing reserved words can break valid identifiers and should be treated as compatibility risk.
- **Hard-coded function shortcuts**: Special-casing functions in parser code bypasses overload, nullability, catalog, and documentation rules.
- **Unstable diagnostics**: Errors without spans, error classes, or consistent wording cannot support tests, docs, or IDE integrations.
- **Case-sensitivity accidents**: Letting dictionary defaults decide resolution behavior creates migration bugs and security surprises.
- **Optimizer boundary leakage**: Passing parser contexts, unresolved names, or untyped expressions to downstream optimizer/execution layers violates the handoff contract.
- **Coverage theater**: Counting parsed grammar alternatives as supported SQL features misleads product planning and users.
- **Ignoring adversarial SQL**: Deep nesting, huge `IN` lists, wide schemas, and ambiguous joins can become denial-of-service or support issues.

---

## What This Means for DeltaSharp

**ADR-0007 needs a dedicated owner**: The ANTLR4 grammar, ANSI mode, Spark SQL parity, function registry, and analyzer resolution rules should evolve under one coherent compatibility strategy. Splitting them casually across parser, catalog, API, and execution work would create semantic drift.

**Core dialect first means complete vertical slices**: Early support should prioritize complete parse -> unresolved plan -> analyzed plan -> executable handoff for common query forms over broad grammar acceptance. A small trustworthy dialect accelerates downstream engine work more than a large ambiguous parser.

**Catalog contracts must be designed with analysis in mind**: ADR-0005's catalog feeds SQL name and function resolution. The frontend should define exact metadata needs, lookup order, error surfaces, and authorization hooks so catalog storage can evolve independently.

**Function parity is a registry problem**: Spark function compatibility will grow for years. A metadata-rich registry makes additions testable, documentable, and analyzable without grammar churn.

**Diagnostics are part of Spark migration**: Users will paste existing SQL and need to know whether failures are syntax gaps, ANSI strictness, unresolved catalog objects, ambiguous columns, type mismatches, unsupported functions, or downstream execution limitations.

**Resolved plans are the clean handoff**: The optimizer and execution roles should receive immutable logical plans with schemas, attributes, types, functions, catalog bindings, and source spans. SQL parser internals should disappear before physical planning.

---

## Confidence Assessment

| Area | Maturity | Notes |
|------|----------|-------|
| ANTLR4 grammar generation and C# target | **Mature** | ANTLR4 is widely used; C# target support is established, though generated-code packaging and performance need project-specific discipline. |
| Spark `SqlBase.g4` as grammar precedent | **Mature** | Spark SQL's grammar is production-proven and directly aligned with DeltaSharp's parity goal. |
| Spark Catalyst analyzer concepts | **Mature** | Unresolved/resolved plans, rule batches, function binding, and catalog resolution are well-established patterns. |
| ANSI SQL mode compatibility | **Mature but nuanced** | Standards and Spark behavior are documented, but exact DeltaSharp choices need compatibility matrices and tests. |
| DeltaSharp catalog integration | **Emerging** | ADR-0005 sets direction; concrete interfaces, metadata shapes, and authorization/error behavior must be implemented. |
| DeltaSharp SQL function registry | **Emerging** | The registry can follow Spark precedent, but DeltaSharp-specific signatures, type system, and execution bindings must be built. |
| SQL frontend performance and robustness | **Evolving** | Parser/analyzer measurement, fuzzing, and denial-of-service safeguards need intentional design from the start. |

---

## Footnotes

[^1]: ANTLR4 documentation describes lexer/parser grammars, adaptive LL(*) parsing, parse-tree visitors/listeners, error listeners, and target runtimes including C#. These are directly relevant because ADR-0007 selects ANTLR4 with the C# target.

[^2]: Apache Spark's `SqlBase.g4` and Catalyst SQL parser/analyzer provide the closest behavioral precedent for Spark SQL syntax, unresolved logical plans, analyzer rules, attribute resolution, function binding, and dialect compatibility.

[^3]: ANSI SQL standards and Apache Spark ANSI compliance documentation define the strictness areas DeltaSharp must address: reserved words, casts, overflow, store assignment, invalid data conversions, and SQL error behavior.

[^4]: DeltaSharp ADR-0007, "SQL frontend — parser and dialect," accepts ANTLR4 grammar mirroring Spark `SqlBase.g4`, ANTLR4 C# target, ANSI SQL mode, core dialect first, and a dedicated `sql-language-frontend-engineer` owner.

[^5]: DeltaSharp ADR-0005, "Catalog / metastore," accepts a pluggable catalog modeled on Spark V2 and states that the SQL frontend and analyzer name resolution depend on the catalog.

[^6]: `docs/engineering/design/engine-architecture.md` defines DeltaSharp's query path as API/SQL -> unresolved logical plan -> analyzer + optimizer -> physical plan -> execution backend, with lazy transformations and eager actions.
