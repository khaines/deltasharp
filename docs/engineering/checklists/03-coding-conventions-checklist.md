# 03 — Coding Conventions Checklist

> **Scope:** General DeltaSharp code, tests, samples, docs-adjacent code, project layout, and PR hygiene that is not .NET-specific enough for 03a.
> **Priority:** HIGH.
> **Owners:** dotnet-framework-runtime-engineer, developer-experience-api-engineer, query-execution-engine-engineer. **Grounded in:** `.github/copilot-instructions.md`, `CONTRIBUTING.md`, `review-pr/rating-rubric.md`, ADR-0001, ADR-0014.

## How to use
Apply this checklist before using the deeper 03a .NET rules. Treat Critical and High findings from the review rubric as blockers when a convention failure can break Spark semantics, Delta correctness, tenant isolation, or lazy/eager execution.

## Checklist
### Repository layout and ownership
- [ ] Framework code lives under `src/`; tests live under `tests/` with one `.Tests` project per source project.
- [ ] The repository remains buildable from a single root `*.sln` with `dotnet restore`, `dotnet build -c Release`, and `dotnet test`.
- [ ] Samples or examples are isolated under `samples/` or `examples/` and do not become hidden production dependencies.
- [ ] New cross-cutting design decisions reference an ADR or RFC instead of embedding policy in ad hoc code comments.
- [ ] Package, namespace, and folder names reinforce API, logical-plan, optimizer, physical-plan, execution, storage, connector, and Kubernetes boundaries.
- [ ] File names match the primary type, operator, rule, or test fixture they contain.
- [ ] Publicly visible names use Spark-compatible vocabulary unless a documented .NET deviation is required.

### Layer separation
- [ ] API-layer methods construct logical plans and never scan files, contact executors, commit Delta logs, or trigger Kubernetes work.
- [ ] Transformations (`select`, `filter`, `groupBy`, `join`, `withColumn`, and equivalents) only extend immutable plans.
- [ ] Actions (`collect`, `count`, `show`, `write`, and equivalents) are the only paths that trigger planning and execution.
- [ ] Logical plan nodes are immutable; analyzer and optimizer rules return new trees instead of mutating existing nodes.
- [ ] Analyzer code resolves names and catalogs without performing optimizer rewrites or physical execution.
- [ ] Optimizer rules preserve query meaning and document rule preconditions for nulls, joins, ordering, and type coercion.
- [ ] Physical planning chooses executable strategies without changing analyzed logical semantics.
- [ ] Execution code depends on physical/operator contracts, not on public API shortcuts or unresolved logical-plan details.

### Naming and API consistency
- [ ] Public API names, overload shapes, and behavior mirror Apache Spark where practical.
- [ ] Any intentional Spark deviation is documented in XML docs, design notes, or tests that state the compatibility reason.
- [ ] Terms such as driver, executor, stage, task, shuffle, partition, catalog, snapshot, checkpoint, and transaction log are used consistently.
- [ ] Error names and result types identify validation, semantic, storage, transient infrastructure, conflict, timeout, cancellation, and programmer-error cases distinctly.
- [ ] Tenant, catalog, storage credential, and execution-context identifiers are explicit in APIs that cross trust or resource boundaries.
- [ ] Public names do not expose implementation-only terms such as temporary codegen internals or storage-emulator details.

### Simplicity, maintainability, and reviewability
- [ ] PRs are small enough that reviewers can verify Spark semantics, storage safety, and tests without reconstructing unrelated changes.
- [ ] Dead code, unused parameters, TODO-only scaffolding, and speculative extension points are removed or tracked by an issue and isolated.
- [ ] Abstractions have at least two credible implementations or a documented seam required by an ADR.
- [ ] Comments explain invariants, ownership, algorithmic constraints, or non-obvious compatibility decisions; they do not narrate obvious code.
- [ ] Complex methods are split by responsibility so plan construction, validation, optimization, execution, and I/O are separately testable.
- [ ] Global mutable state is avoided unless the lifetime, concurrency model, and tenant boundary are explicitly documented.
- [ ] Public exceptions or diagnostics include actionable context without leaking credentials, tenant data, or large row payloads.

### Metadata and contribution hygiene
- [ ] Commits are DCO-signed as required by `CONTRIBUTING.md`.
- [ ] PR descriptions identify affected layers and link issues, RFCs, or ADRs for substantial behavior changes.
- [ ] New behavior includes tests or a clear reason why the change is documentation-only.
- [ ] Formatting and analyzer expectations are satisfied by `dotnet format --verify-no-changes` when code changes are present.
- [ ] Build, test, and format evidence is included or reproducible from the root solution.
- [ ] Review findings use the rubric severities: Critical, High, Medium, Low, and Info.

## Anti-patterns (red flags)
- API methods execute work during a transformation; this is a Critical lazy/eager violation.
- Optimizer rules mutate existing plan nodes or change null-preserving join semantics.
- Storage or executor code is called directly from DataFrame/Dataset construction.
- A PR mixes public API, storage format, optimizer, operator, and Kubernetes changes without a reviewable boundary.
- Names diverge from Spark without documentation or tests proving the intended .NET deviation.
- Generic catch-all errors hide Delta commit conflicts, cancellation, timeout, or tenant-isolation failures.
- Comments restate syntax while important invariants, ownership, or compatibility rules remain undocumented.
- Dead scaffolding is merged because it may be useful later.
- Commit history lacks the required DCO sign-off.

## References
- [03a — .NET Coding Standards Checklist](03a-dotnet-coding-standards.md)
- [15 — Spark API Parity Checklist](15-spark-api-parity-checklist.md)
- [16 — Catalyst Planning Checklist](16-catalyst-planning-checklist.md)
- [17 — Delta Storage Format Checklist](17-delta-storage-format-checklist.md)
- [21 — Distributed Correctness Checklist](21-distributed-correctness-checklist.md)
- `.github/copilot-instructions.md`
- `CONTRIBUTING.md`
- `.github/skills/review-pr/rating-rubric.md`
- ADR-0001: Execution strategy
- ADR-0014: Target framework and AOT posture
