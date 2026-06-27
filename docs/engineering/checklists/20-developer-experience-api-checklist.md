# 20 — Developer Experience & API Checklist

> **Scope:** Public API ergonomics, IntelliSense, XML docs, nullable annotations, async action APIs, diagnostics, lifecycle attributes, samples, quickstarts, migration guides, and API stability gates.
> **Priority:** STANDARD.
> **Owners:** developer-experience-api-engineer, dotnet-library-platform-engineer, technical-writer. **Grounded in:** `.github/copilot-instructions.md`, `developer-experience-api-engineer-agent.md`, [15](15-spark-api-parity-checklist.md), [16](16-catalyst-planning-checklist.md).

## How to use
Use this checklist for any PR that changes user-facing APIs or developer-facing documentation. Review Spark parity and idiomatic .NET ergonomics together; packaging mechanics remain in 03a/library-platform unless they directly gate API stability.

## Checklist
### Discoverability and IntelliSense
- [ ] Core surfaces are discoverable from `SparkSession`, `DataFrame`, `Dataset<T>`, `Column`, functions, `DataFrameReader`, `DataFrameWriter`, and SQL entry points.
- [ ] Overload sets are small, ordered, and named so IntelliSense shows the most common Spark-compatible path first.
- [ ] Parameter names use Spark terminology where it helps migration and .NET clarity where it prevents ambiguity.
- [ ] Extension methods and static helpers are placed in namespaces that are easy to import and hard to conflict with unrelated .NET APIs.
- [ ] Fluent chains read like Spark transformations while making eager actions visually distinct.
- [ ] `Dataset<T>` APIs feel strongly typed without making DataFrame/SQL users learn a separate engine model.
- [ ] Configuration and options expose common Spark keys or documented aliases with completion-friendly constants where useful.
- [ ] Public API shape hides internal planner, protobuf, executor, shuffle, and storage implementation details except through explicit diagnostics.

### XML docs, examples, and nullable annotations
- [ ] Public types and members have XML docs that explain purpose, lazy/eager behavior, Spark compatibility, parameters, return value, exceptions, and examples where helpful.
- [ ] XML docs identify deliberate Spark deviations and link to migration or parity notes when behavior differs.
- [ ] Nullable reference annotations are enabled and accurately model user contracts without weakening Spark SQL null semantics.
- [ ] Examples compile once code exists and avoid pseudo-code that cannot become tests or snippets.
- [ ] Docs distinguish local argument validation errors from analysis/action-time catalog, type, and execution errors.
- [ ] XML docs for actions state cancellation, async availability, materialization size, ordering expectations, and resource implications.
- [ ] Function and expression docs include null propagation, type coercion, ANSI mode caveats, and unsupported cases.
- [ ] Terminology is consistent across XML docs, SQL docs, errors, samples, logs, metrics, and `EXPLAIN`.

### Async ergonomics and action APIs
- [ ] I/O-bound actions provide idiomatic `async`/`await` variants with `CancellationToken` parameters.
- [ ] Async APIs do not create asynchronous-looking transformations that secretly execute work.
- [ ] Synchronous and asynchronous action variants have equivalent semantics, errors, cancellation behavior, and ordering guarantees.
- [ ] Streaming/materializing actions document memory implications and provide bounded alternatives where practical.
- [ ] Cancellation flows from public action APIs through planning, scheduling, storage, shuffle, and executor work.
- [ ] `IAsyncDisposable`, streams, channels, and readers/writers are used where resource ownership crosses async boundaries.
- [ ] Exceptions preserve actionable user context without exposing secrets, tenant data, or internal stack-only jargon.
- [ ] Samples demonstrate cancellation and disposal for long-running or distributed actions.

### Error messages and diagnostics
- [ ] User-facing errors name the invalid API call, SQL fragment, expression, option, table, or column involved.
- [ ] Diagnostics state the violated Spark/ANSI/DeltaSharp rule and provide the next corrective action.
- [ ] Analyzer and planner errors distinguish unresolved name, ambiguous name, unsupported feature, type mismatch, ANSI failure, and execution failure.
- [ ] Error messages preserve source spans for SQL and expression context for DataFrame/Dataset calls where possible.
- [ ] Diagnostics avoid leaking credentials, unauthorized paths, cross-tenant schema, data values, or secret-bearing configuration.
- [ ] `EXPLAIN` and plan diagnostics are accessible from APIs without requiring users to know internal classes.
- [ ] Repeated support questions are converted into better diagnostics, docs, examples, or analyzers.
- [ ] Error classes or codes are stable enough for tests, IDE integration, docs, and migration tooling.

### API lifecycle and stability
- [ ] New public APIs have an explicit stability state: stable, `[Experimental]`, preview, obsolete, deprecated, or internal.
- [ ] `[Experimental]` APIs include a reason, tracking issue or milestone, and expected compatibility risk.
- [ ] `[Obsolete]` APIs include actionable replacement guidance and timeline when removal is planned.
- [ ] Breaking changes include migration notes, analyzer support where practical, and review against [15](15-spark-api-parity-checklist.md).
- [ ] Public API stability is gated by analyzers or equivalent checks; defer package layout and mechanics to 03a/library-platform.
- [ ] API additions update compatibility baselines, docs, samples, and parity matrices together.
- [ ] Preview APIs do not make unreviewed semantic promises about Spark parity, Delta ACID, Kubernetes behavior, or performance.
- [ ] Versioning policy distinguishes public framework contracts from internal engine implementation details.

### Samples, quickstarts, and migration guides
- [ ] Quickstarts cover first session, first read, first lazy transformation chain, first action, first `EXPLAIN`, and first Delta write.
- [ ] Samples use realistic Delta paths, object-store or PVC examples, schema handling, credentials guidance, and cleanup expectations without hard-coding secrets.
- [ ] Migration guides map PySpark and Scala Spark snippets to DeltaSharp C# equivalents and call out syntax and semantic caveats.
- [ ] Examples show both SQL and DataFrame/Dataset routes converging on the same engine behavior.
- [ ] Samples identify actions that execute work and transformations that only build plans.
- [ ] Unsupported Spark features in examples are marked with alternatives or tracking references.
- [ ] Performance, cost, and Kubernetes claims in samples are modest, measurable, and linked to relevant docs.
- [ ] Sample code can become automated tests or validation snippets as the repository matures.

### Parity vs idiomatic .NET ownership
- [ ] API review notes explicitly decide whether Spark parity or idiomatic .NET should dominate for each contested surface.
- [ ] Spark-compatible names are preserved for high-migration-value APIs unless a documented .NET safety reason outweighs them.
- [ ] Idiomatic .NET additions are additive or clearly mapped to Spark equivalents rather than silent replacements.
- [ ] PascalCase/lower-case naming choices are consistent with the adopted migration strategy and documented for users.
- [ ] Public APIs do not invent abstractions that obscure Spark concepts users rely on: sessions, DataFrames, columns, plans, actions, readers, writers, and SQL.
- [ ] Developer-experience decisions are coordinated with [16](16-catalyst-planning-checklist.md) when API shape affects plan semantics, `EXPLAIN`, or analyzer behavior.
- [ ] API surface work coordinates with engine, SQL, Delta, connector, security, and SRE owners for facts while keeping public shape owned by `developer-experience-api-engineer`.
- [ ] Cross-link [15](15-spark-api-parity-checklist.md) for Spark compatibility and [16](16-catalyst-planning-checklist.md) for planning-visible behavior.

## Anti-patterns (red flags)
- Public methods with no XML docs, nullable annotations, examples, or lazy/eager explanation.
- Clever .NET abstractions that make Spark migration harder or hide familiar Spark concepts.
- Async transformation methods that imply non-blocking execution but actually blur lazy/eager semantics.
- Error messages that say only "invalid operation" without expression, rule, and corrective action.
- Experimental APIs shipped without lifecycle, compatibility risk, or migration guidance.
- Samples that hard-code credentials, hide actions, omit cleanup, or show unsupported behavior as if supported.
- Breaking API changes without analyzers, deprecation, migration notes, or parity-matrix updates.
- Public APIs leaking internal execution backends, vector formats, protobuf messages, or Kubernetes plumbing by accident.

## References
- [DeltaSharp Copilot Instructions](../../../.github/copilot-instructions.md)
- [15 — Spark API Parity Checklist](15-spark-api-parity-checklist.md)
- [16 — Catalyst Planning Checklist](16-catalyst-planning-checklist.md)
- [Developer Experience & API Engineer Agent](../../persona/agents/developer-experience-api-engineer-agent.md)
- [Query & Execution Engine Engineer Agent](../../persona/agents/query-execution-engine-engineer-agent.md)
- [SQL Language & Frontend Engineer Agent](../../persona/agents/sql-language-frontend-engineer-agent.md)
- [Review PR rating rubric](../../../.github/skills/review-pr/rating-rubric.md)
- [.NET nullable reference types](https://learn.microsoft.com/dotnet/csharp/nullable-references)
- [.NET API analyzer and compatibility guidance](https://learn.microsoft.com/dotnet/fundamentals/apicompat/overview)
