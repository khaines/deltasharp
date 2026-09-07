# Implement Work Item — Build & Test Configuration

This file defines the build and test commands, thresholds, and failure-parsing patterns used by the `implement-work-item` skill during Phase 6 (build-test-fix loop) and Phase 8 (post-review-fix test validation).

---

## Language Configurations

### C# / .NET

DeltaSharp is a greenfield .NET solution. Assume a single `*.sln` at the repository root, framework projects under `src/`, and one `.Tests` project under `tests/` for each source project.

| Command | Purpose | Default |
|---------|---------|---------|
| **Restore** | `dotnet restore` | Restore NuGet packages for the solution |
| **Build** | `dotnet build -c Release` | Compile the full solution in Release configuration |
| **Format** | `dotnet format` | Apply formatting and analyzer fixes |
| **Lint / format gate** | `dotnet format --verify-no-changes` | CI-style formatting/analyzer verification |
| **All tests** | `dotnet test` | Run every test project referenced by the solution |
| **Single test by fully-qualified name** | `dotnet test --filter "FullyQualifiedName~X"` | Run tests whose fully-qualified name contains `X` |
| **Single test by name** | `dotnet test --filter "Name=Y"` | Run a specific test named `Y` |
| **Coverage** | `dotnet test --collect:"XPlat Code Coverage"` | Generate coverage when the collector is configured |

**.NET-specific notes:**

- Run `dotnet restore` before the first build or after project/package changes.
- Run `dotnet build -c Release` from the repo root so the single solution catches project-reference, nullable, analyzer, and packaging errors.
- Run `dotnet test` from the repo root for the full validation gate. Use filters only while iterating on a specific failure.
- Use `dotnet format --verify-no-changes` as the lint gate; use `dotnet format` to fix formatting/analyzer issues when needed.
- Preserve nullable reference types, async correctness, cancellation token flow, deterministic disposal, and memory/GC safety in engine hot paths.
- Tests should cover Spark API parity, lazy transformations, eager actions, Catalyst-style planning, Delta ACID commits, Parquet layout, shuffle/stage behavior, operator reconciliation, and tenant isolation as applicable.

---

## Loop Configuration

### Restore/Build-Fix Loop

| Parameter | Default | Description |
|-----------|---------|-------------|
| `max_restore_attempts` | 3 | Maximum restore-fix iterations after project or package changes |
| `max_build_attempts` | 5 | Maximum build-fix iterations. Each iteration: fix → rebuild |
| `restore_timeout` | 180s | Maximum time for package restore |
| `build_timeout` | 240s | Maximum time for a Release build |

### Format/Test-Fix Loop

| Parameter | Default | Description |
|-----------|---------|-------------|
| `max_format_attempts` | 3 | Maximum format/analyzer-fix iterations |
| `max_test_fix_rounds` | 5 | Maximum test-fix iterations per stage. Each round: fix → rebuild → retest |
| `unit_test_timeout` | 300s | Maximum time for focused/unit test runs |
| `integration_test_timeout` | 900s | Maximum time for distributed/storage/operator integration suites |
| `prefer_fix_implementation` | true | When a test fails, prefer fixing implementation over changing test expectations |

### Post-Review Test Validation (Phase 8)

| Parameter | Default | Description |
|-----------|---------|-------------|
| `retest_after_review_fix` | true | Re-run build + tests after each review-fix round |
| `max_review_regression_fixes` | 3 | Maximum attempts to fix a test regression caused by a review fix before reverting |

---

## Failure Parsing Patterns

### Restore Errors

```text
Pattern: <project>.csproj : error NU<code>: <message>
Example: src/DeltaSharp.Core/DeltaSharp.Core.csproj : error NU1101: Unable to find package Example.Package

Extract:
  - project: src/DeltaSharp.Core/DeltaSharp.Core.csproj
  - code: NU1101
  - message: Unable to find package Example.Package
  - type: restore
```

### Build Errors

```text
Pattern: <file>(<line>,<col>): error <code>: <message> [<project>]
Example: src/DeltaSharp.Core/DataFrame.cs(42,17): error CS0246: The type or namespace name 'LogicalPlan' could not be found [src/DeltaSharp.Core/DeltaSharp.Core.csproj]

Extract:
  - file: src/DeltaSharp.Core/DataFrame.cs
  - line: 42
  - col: 17
  - code: CS0246
  - message: The type or namespace name 'LogicalPlan' could not be found
  - project: src/DeltaSharp.Core/DeltaSharp.Core.csproj
  - type: compilation
```

### Analyzer / Formatting Failures

```text
Pattern: <file>(<line>,<col>): error|warning <code>: <message>
Example: src/DeltaSharp.Core/Plan.cs(12,1): error IDE0055: Fix formatting

Extract:
  - file: src/DeltaSharp.Core/Plan.cs
  - line: 12
  - col: 1
  - code: IDE0055
  - message: Fix formatting
  - type: formatting_or_analyzer
```

### Test Failures

```text
Pattern: Failed <TestName> [<duration>]
Followed by assertion message and stack frame
Example:
  Failed DeltaSharp.Core.Tests.DataFrameTests.Select_BuildsLogicalPlan [12 ms]
  Error Message:
   Assert.Equal() Failure
  Stack Trace:
     at DeltaSharp.Core.Tests.DataFrameTests.Select_BuildsLogicalPlan() in tests/DeltaSharp.Core.Tests/DataFrameTests.cs:line 58

Extract:
  - test_name: DeltaSharp.Core.Tests.DataFrameTests.Select_BuildsLogicalPlan
  - file: tests/DeltaSharp.Core.Tests/DataFrameTests.cs
  - line: 58
  - message: Assert.Equal() Failure
  - duration: 12 ms
  - type: assertion_failure
```

### Runtime / Async Failures

```text
Signals:
  - System.InvalidOperationException
  - System.OperationCanceledException used incorrectly
  - ObjectDisposedException
  - deadlock or timeout in async test
  - unobserved task exception

Extract:
  - exception_type
  - stack trace frames
  - cancellation/disposal context
  - type: runtime_or_async_failure
```

### Memory / Resource Failures

```text
Signals:
  - OutOfMemoryException
  - excessive allocation in benchmark gate
  - undisposed stream/file/client in analyzer output
  - lingering executor/operator test process

Extract:
  - resource type
  - file/line or benchmark name
  - impact: memory, file handle, socket, process, object-store client
  - type: resource_management
```

---

## Fix Classification Heuristics

When a test fails, classify the failure to determine what to fix:

| Signal | Classification | Action |
|--------|---------------|--------|
| Test expects behavior from design doc acceptance criteria | **Implementation bug** | Fix the implementation to match the expected behavior |
| Test expects a value not mentioned in the design doc or issue | **Test bug** | Fix the test to align with the design doc |
| Test setup fails (missing fixture, container, storage emulator, Kubernetes test harness) | **Test infrastructure** | Fix the test setup, not the implementation |
| Build error in test project | **Test compilation** | Fix the test project or references |
| Build error in source project | **Implementation compilation** | Fix the source project |
| Async deadlock, missing cancellation, or disposal bug | **Implementation bug** | Fix the async/resource lifecycle unless the test is demonstrably invalid |
| Multiple tests fail with the same root cause | **Shared implementation bug** | Fix the root cause once, re-run all affected tests |
| Spark parity test mismatch | **Semantic bug** | Align with Spark behavior or document a deliberate .NET-specific deviation |
| Delta commit/time-travel/schema-evolution test mismatch | **Storage correctness bug** | Fix Delta/Parquet behavior; do not weaken ACID expectations |

---

## Coverage and Quality Targets

Reference the unit testing checklist (04a) and benchmark gates for coverage expectations:

| Layer | Target | Notes |
|-------|--------|-------|
| Logical plan, analyzer, optimizer rules | ≥ 90% | Pure transformations, immutable plan rewrites, semantic edge cases |
| Delta transaction log and Parquet IO | ≥ 90% | ACID, conflicts, schema evolution, time travel, file metadata |
| Public API surface | ≥ 85% | Spark parity behavior and documented .NET deviations |
| Kubernetes Operator and executor coordination | ≥ 80% | Reconcile safety, idempotence, failure handling |
| Connectors and storage backends | ≥ 80% | S3/ADLS/GCS/PVC behavior via abstractions and test doubles |

Coverage is tracked but not the only gate. Correctness, API semantics, storage safety, lazy/eager behavior, and regression-benchmark adherence are blocking quality signals.
