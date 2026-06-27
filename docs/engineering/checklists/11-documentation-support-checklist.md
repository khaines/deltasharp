# 11 — Documentation Support Checklist

> **Scope:** Public docs, API reference, XML comments, tutorials, how-to guides, conceptual docs, runbooks, migration guides, samples, release notes, and support enablement content.
> **Priority:** SUPPLEMENTARY.
> **Owners:** technical-writer, developer-experience-api-engineer, cloud-native-site-reliability-engineer. **Grounded in:** `.github/copilot-instructions.md`, ADRs, 09a, 09b, 09c, markdown-style-guide, technical-writer persona docs.

## How to use
Use this checklist for documentation changes and for code changes that alter public APIs, operational behavior, examples, diagnostics, or migration guidance. Defer product, architecture, and storage decisions to ADRs and owner docs rather than redefining them in narrative pages.

## Checklist
### Docs-as-code workflow
- [ ] Documentation lives in the repository, is reviewed through pull requests, and changes with the code or ADR that made it necessary.
- [ ] Each page has a clear reader, task, owner, and expected maintenance trigger.
- [ ] New docs choose the correct form: tutorial, how-to, reference, explanation, runbook, troubleshooting article, migration guide, or release note.
- [ ] Docs update the source of truth by linking to ADRs, API docs, or design docs instead of duplicating decisions.
- [ ] Stale content is removed or marked with version, status, and replacement guidance.
- [ ] Support questions, incidents, migration friction, and confusing errors feed back into docs improvements.

### Public API and generated reference
- [ ] Every public API has XML documentation that states purpose, parameters, return behavior, exceptions, cancellation, and Spark compatibility where relevant.
- [ ] XML docs are accurate enough to generate API reference without requiring users to inspect implementation code.
- [ ] Public API docs distinguish transformation methods that are lazy from actions that trigger execution.
- [ ] Reference docs include nullable behavior, ownership or disposal rules, async/cancellation expectations, and preview or unsupported behavior.
- [ ] Public API changes include migration notes when behavior, names, overloads, or compatibility expectations change.
- [ ] Generated reference tooling, when present, is part of the build or documentation validation path.

### Conceptual coverage
- [ ] Concepts explain DeltaSharp as a .NET-native Spark-equivalent without a JVM and define where compatibility is exact, approximate, or intentionally .NET-oriented.
- [ ] Lazy transformations and eager actions are explained consistently in tutorials, API examples, migration guides, and troubleshooting docs.
- [ ] Catalyst-style planning docs cover logical plans, analyzer resolution, optimizer rules, physical planning, stages, tasks, and shuffle boundaries.
- [ ] Delta docs cover Parquet data files, `_delta_log`, ACID commits, optimistic concurrency, time travel, schema evolution, checkpoints, retention, and compaction.
- [ ] Kubernetes Operator docs cover CRDs, driver pods, executor pods, lifecycle, status, rollout safety, shutdown, and recovery.
- [ ] Storage docs compare S3, ADLS, GCS, and PVC behavior without assuming one backend is universal.

### Tutorials, examples, and migration guides
- [ ] Quickstarts lead a capable user from installation to `SparkSession`, DataFrame or Dataset creation, transformation, action, and Delta table write.
- [ ] Examples compile or are clearly marked as conceptual; code snippets use current API names and package versions.
- [ ] Samples include expected output, validation steps, cleanup, and common failure notes where appropriate.
- [ ] Migration guides translate PySpark and Scala Spark concepts to DeltaSharp and separate syntax changes from semantic differences.
- [ ] Spark parity gaps, preview features, unsupported APIs, and intentional .NET idioms are explicit.
- [ ] Performance claims link to benchmarks or state the measurement context and limitations.

### Operations, observability, and support
- [ ] Runbooks start from symptoms and include impact, diagnosis, safe mitigation, escalation, verification, and rollback or cleanup.
- [ ] Runbooks use 09a logs, 09b metrics, and 09c traces as concrete evidence instead of vague inspection steps.
- [ ] Troubleshooting docs distinguish user error, cancellation, timeout, transient infrastructure failure, Delta conflict, tenant isolation failure, and product bug.
- [ ] Operational docs cover stuck drivers, executor churn, failed shuffles, degraded object storage, PVC pressure, commit conflicts, and operator reconcile failures.
- [ ] Error-message guidance tells users what happened, why it matters, and what to do next.
- [ ] Security, privacy, tenant isolation, and credential-handling guidance link to 05, 07, and 14 instead of inventing local policy.

### Releases, compatibility, and maintenance
- [ ] Changelog and release notes state user impact, compatibility, action required, deprecations, risk, and verification steps.
- [ ] Breaking changes include migration path, timeline, and examples.
- [ ] ADR updates, design changes, API changes, CRD changes, and operational changes include docs impact review.
- [ ] Link integrity, anchors, images, and cross-references are checked before merge where tooling exists.
- [ ] Docs are version-aware when behavior differs by DeltaSharp release, Delta protocol support, Kubernetes version, or .NET target.
- [ ] Documentation style follows `markdown-style-guide-checklist.md`.

### Accessibility and findability
- [ ] Headings are descriptive and scannable; links use meaningful text rather than “click here.”
- [ ] Images have alt text and do not carry essential information without adjacent text.
- [ ] Tables have clear headers and are not used when a list is more accessible.
- [ ] Terminology is consistent for DeltaSharp, Delta, Parquet, Spark, driver, executor, stage, task, shuffle, and Operator.
- [ ] Pages include keywords users are likely to search for, including Spark and .NET equivalents.

## Anti-patterns (red flags)
- Publishing polished prose that contradicts or redefines an ADR, API contract, or owner-approved design.
- Leaving public APIs undocumented or documenting transformations as if they execute eagerly.
- Shipping examples that do not compile, use stale names, or omit expected output and verification.
- Mixing tutorial, reference, explanation, and runbook content until the page satisfies none of them.
- Writing runbooks around internal component names without symptoms, impact, mitigation, or recovery verification.
- Making security, privacy, reliability, or performance claims without owner review and evidence.
- Leaving release notes, migration guides, or operational docs stale after behavior changes.

## References
- [09a — Logging Checklist](09a-logging-checklist.md)
- [09b — Metrics Checklist](09b-metrics-checklist.md)
- [09c — Distributed Tracing Checklist](09c-distributed-tracing-checklist.md)
- [Markdown Style Guide Checklist](markdown-style-guide-checklist.md)
- `.github/copilot-instructions.md`
- `.github/skills/review-pr/rating-rubric.md`
- `docs/persona/agents/technical-writer-agent.md`
- `docs/persona/research/technical-writer.md`
- `docs/persona/agents/cloud-native-site-reliability-engineer-agent.md`
- Diataxis documentation framework
- Microsoft Writing Style Guide and developer documentation guidance
- Write the Docs docs-as-code guidance
