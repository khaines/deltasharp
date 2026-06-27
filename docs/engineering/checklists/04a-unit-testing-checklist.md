# 04a — Unit Testing Checklist

> **Scope:** Fast isolated xUnit-style tests for C# APIs, plan nodes, analyzer/optimizer rules, expressions, type coercion, async/disposal units, and storage/operator test doubles.
> **Priority:** HIGH.
> **Owners:** reliability-test-chaos-engineer, query-execution-engine-engineer, dotnet-framework-runtime-engineer. **Grounded in:** build-test-config, `.github/copilot-instructions.md`, ADR-0001, ADR-0013, 04 testing checklist.

## How to use
Use unit tests to lock down semantics before introducing distributed or storage infrastructure. A unit test should run quickly, isolate one behavior, and fail with enough context to fix the implementation without guessing.

## Checklist
### Command and structure
- [ ] Unit tests run with `dotnet test` from the root or with a specific `tests/<Project>.Tests` project.
- [ ] Focused iteration uses `dotnet test --filter "FullyQualifiedName~X"` for a class/namespace or `dotnet test --filter "Name=Y"` for one method.
- [ ] Test projects are under `tests/` and suffixed `.Tests` to match the corresponding `src/` project.
- [ ] Test names state behavior and condition, for example `Select_BuildsLogicalPlanWithoutExecuting`.
- [ ] Tests avoid real object stores, Kubernetes clusters, wall-clock sleeps, network calls, and local user-specific paths.
- [ ] Shared fixtures are minimal and cannot leak mutable state between tests.

### Plan, expression, and optimizer units
- [ ] DataFrame/Dataset transformation tests assert logical plan construction without triggering execution.
- [ ] Action tests assert that execution is invoked only through the action boundary.
- [ ] Logical plan node tests verify immutability: rewrites return new trees and leave original trees unchanged.
- [ ] Analyzer tests cover unresolved names, ambiguous columns, catalog resolution, case behavior, and clear diagnostics.
- [ ] Optimizer rule tests assert semantic equivalence, not only a preferred internal tree shape.
- [ ] Rule tests include nulls, outer joins, ordering, determinism, type coercion, constants, and unsupported expressions where relevant.
- [ ] Expression tests cover Spark-compatible null, NaN, signed zero, decimal, overflow, string, and timestamp behavior.
- [ ] Golden tests for rules or plans have stable serialization and an explicit update review path.

### Property-based and differential checks
- [ ] Property-based tests use deterministic seeds and print the seed, generated schema, and minimized input on failure.
- [ ] Optimizer properties verify idempotence, semantic preservation, and no mutation of source trees.
- [ ] Type-coercion properties verify symmetric and asymmetric coercion cases against Spark-compatible expectations.
- [ ] ADR-0001 differential unit tests compare interpreted and compiled expression paths where dynamic code is available.
- [ ] Compiled-backend tests skip or assert feature gating when `RuntimeFeature.IsDynamicCodeSupported` is false.
- [ ] Random data includes null distributions, empty batches, single-row batches, boundary sizes, and mixed column types.

### Test doubles and isolation
- [ ] Storage tests use in-memory or deterministic test doubles for object stores unless integration behavior is the subject.
- [ ] Delta log unit tests isolate transaction-state transitions from real filesystem or object-store consistency.
- [ ] RPC, executor, and operator units use fakes that expose calls, cancellation, retry, and disposal deterministically.
- [ ] Test doubles enforce tenant/catalog/storage boundaries rather than accepting any path or credential.
- [ ] Failure injection covers transient errors, conflicts, cancellation, timeout, and malformed metadata.
- [ ] No unit test requires external credentials, a running cluster, a container runtime, or a cloud account.

### .NET correctness units
- [ ] Nullable annotations are exercised for public APIs that accept or reject nulls.
- [ ] Async units prove awaited behavior, exception propagation, cancellation, and no sync-over-async blocking.
- [ ] `IAsyncDisposable` and `IDisposable` units verify resources are released on success, failure, and cancellation.
- [ ] Pooling units verify buffers are returned exactly once and not used after return.
- [ ] Native or off-heap memory wrappers have bounds, disposal, double-dispose, and leak-detection tests.
- [ ] `ValueTask` APIs have tests that consume them correctly and prevent multiple-await misuse where applicable.

### Assertions and diagnostics
- [ ] Assertions compare semantic rows, schemas, expressions, diagnostics, or public contracts rather than brittle private fields.
- [ ] Internal shape assertions are used only when the shape is the contract of a rule or plan node.
- [ ] Tests include expected error codes/messages for validation, analysis, Delta conflict, cancellation, and timeout cases.
- [ ] Failure messages include query text, plan fragment, seed, schema, or table version when useful.
- [ ] Tests are independent of execution order and can run in parallel unless the fixture explicitly disables it with justification.

## Anti-patterns (red flags)
- A unit test starts a real Kubernetes cluster, object store, or executor process.
- Tests assert incidental private tree shape instead of logical equivalence.
- Randomized tests fail without printing the seed or generated input.
- Optimizer tests mutate the input plan to make assertions easier.
- Async tests use sleeps instead of deterministic signaling.
- Disposal and cancellation paths are left to integration tests only.
- Test doubles accept impossible states that production abstractions would reject.

## References
- [04 — Testing Checklist](04-testing-checklist.md)
- [04b — Integration Testing Checklist](04b-integration-testing-checklist.md)
- [03a — .NET Coding Standards Checklist](03a-dotnet-coding-standards.md)
- [15 — Spark API Parity Checklist](15-spark-api-parity-checklist.md)
- [16 — Catalyst Planning Checklist](16-catalyst-planning-checklist.md)
- [21 — Distributed Correctness Checklist](21-distributed-correctness-checklist.md)
- `.github/skills/implement-work-item/build-test-config.md`
- `.github/copilot-instructions.md`
- ADR-0001: Execution strategy
- ADR-0013: Memory model for in-memory batches
