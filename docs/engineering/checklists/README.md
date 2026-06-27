# DeltaSharp Engineering Checklists

Authoritative engineering checklists used during design, implementation, and code
review. The `review-pr` and `design-doc` skills load these by number via
`.github/skills/review-pr/checklist-map.md` and
`.github/skills/design-doc/checklist-refs.md`.

These checklists **operationalize** the project canon; they defer to the
[ADRs](../../adr/README.md) (source of truth for decisions), the
[engine architecture overview](../design/engine-architecture.md), the persona
specs in [`docs/persona/agents/`](../../persona/agents/README.md), and
[`.github/copilot-instructions.md`](../../../.github/copilot-instructions.md). If a
checklist and an ADR disagree, the ADR wins.

## How to use

- Reviewers/designers match changed files against the patterns in
  `checklist-map.md` and apply the matching checklists.
- Each item is phrased as a **verifiable check**. Focus on items relevant to the
  change, not every item.
- Severity and priority language follows
  [`review-pr/rating-rubric.md`](../../../.github/skills/review-pr/rating-rubric.md):
  **Critical → High → Medium → Low → Info**, with **CRITICAL / HIGH / STANDARD /
  SUPPLEMENTARY** priority tiers.

## Checklist format

Every checklist follows the same shape:

```markdown
# NN — <Title> Checklist

> **Scope:** when this applies (file patterns / situations).
> **Priority:** CRITICAL | HIGH | STANDARD | SUPPLEMENTARY.
> **Owners:** <persona slug(s)>. **Grounded in:** <ADRs / docs>.

## How to use
One or two sentences.

## Checklist
### <Category>
- [ ] Verifiable item …

## Anti-patterns (red flags)
- …

## References
- ADRs, persona docs, external authoritative sources.
```

## Index

| # | File | Topic | Priority |
|---|---|---|---|
| 01 | [01-architecture-checklist.md](01-architecture-checklist.md) | Architecture patterns | STANDARD |
| 02 | [02-engine-implementation-checklist.md](02-engine-implementation-checklist.md) | Engine/component design | STANDARD |
| 03 | [03-coding-conventions-checklist.md](03-coding-conventions-checklist.md) | General coding conventions | HIGH |
| 03a | [03a-dotnet-coding-standards.md](03a-dotnet-coding-standards.md) | C#/.NET coding standards | HIGH |
| 04 | [04-testing-checklist.md](04-testing-checklist.md) | General testing | HIGH |
| 04a | [04a-unit-testing-checklist.md](04a-unit-testing-checklist.md) | Unit testing | HIGH |
| 04b | [04b-integration-testing-checklist.md](04b-integration-testing-checklist.md) | Integration testing | HIGH |
| 05 | [05-security-checklist.md](05-security-checklist.md) | Security | CRITICAL |
| 07 | [07-privacy-checklist.md](07-privacy-checklist.md) | Privacy / GDPR | STANDARD |
| 08 | [08-performance-checklist.md](08-performance-checklist.md) | Performance | STANDARD |
| 09a | [09a-logging-checklist.md](09a-logging-checklist.md) | Logging | STANDARD |
| 09b | [09b-metrics-checklist.md](09b-metrics-checklist.md) | Metrics | STANDARD |
| 09c | [09c-distributed-tracing-checklist.md](09c-distributed-tracing-checklist.md) | Distributed tracing | STANDARD |
| 10 | [10-runtime-environment-checklist.md](10-runtime-environment-checklist.md) | Runtime / containers / Kubernetes | STANDARD |
| 11 | [11-documentation-support-checklist.md](11-documentation-support-checklist.md) | Documentation | SUPPLEMENTARY |
| 13 | [13-infrastructure-as-code-checklist.md](13-infrastructure-as-code-checklist.md) | Infrastructure as Code | STANDARD |
| 14 | [14-tenant-isolation-checklist.md](14-tenant-isolation-checklist.md) | Multi-tenant isolation | CRITICAL |
| 15 | [15-spark-api-parity-checklist.md](15-spark-api-parity-checklist.md) | Spark API parity & semantics | HIGH |
| 16 | [16-catalyst-planning-checklist.md](16-catalyst-planning-checklist.md) | Catalyst-style planning correctness | HIGH |
| 17 | [17-delta-storage-format-checklist.md](17-delta-storage-format-checklist.md) | Delta & Parquet correctness | CRITICAL |
| 18 | [18-kubernetes-operator-checklist.md](18-kubernetes-operator-checklist.md) | Operator, CRDs, lifecycle safety | HIGH |
| 19 | [19-data-source-connectors-checklist.md](19-data-source-connectors-checklist.md) | Data sources, sinks, catalogs | HIGH |
| 20 | [20-developer-experience-api-checklist.md](20-developer-experience-api-checklist.md) | Public API ergonomics & samples | STANDARD |
| 21 | [21-distributed-correctness-checklist.md](21-distributed-correctness-checklist.md) | Lazy/eager, stages, shuffle, fault correctness | CRITICAL |
| 22 | [22-benchmark-regression-gates-checklist.md](22-benchmark-regression-gates-checklist.md) | Benchmarks & regression gates | STANDARD |
| — | [markdown-style-guide-checklist.md](markdown-style-guide-checklist.md) | Markdown formatting | SUPPLEMENTARY |

> Numbers `06` and `12` are intentionally unused (reserved gaps in the taxonomy
> inherited from the shared review framework).
