# ADR-0015: Open-source positioning and community governance

- **Status:** Accepted
- **Date:** 2026-06-27
- **Deciders:** @khaines
- **Related:** `docs/persona/agents/README.md`, `.github/copilot-instructions.md`

## Context

DeltaSharp aims to be a Spark-class engine. Like Apache Spark, Flink, DuckDB, and
DataFusion, mindshare and durability depend heavily on an **open-source
community** — contributors, ecosystem integrations, and public trust.

## Decision

**DeltaSharp is open-source** with an active community/adoption strategy. It is
licensed under the **Apache License 2.0** (the lakehouse-ecosystem norm — Spark,
Delta, Iceberg, and Arrow all use it — with an explicit patent grant). Establish
OSS governance: `LICENSE` + `NOTICE`, `CONTRIBUTING`, a code of conduct, an
RFC/proposal process, a public roadmap, issue/PR triage, and release
communications. Add a dedicated **`developer-relations-community-lead`** persona to
own community, contributor experience, evangelism, and governance — distinct from
`developer-experience-api-engineer` (API ergonomics/samples) and `technical-writer`
(the docs themselves).

## Consequences

### Positive

- Community-driven adoption, a contributor pipeline, and ecosystem partnerships —
  the only realistic path to competing with Spark for mindshare.

### Negative / costs

- Governance overhead, public process, and a coordinated security-disclosure path.

### Follow-ups

- **License chosen: Apache-2.0** — `LICENSE` and `NOTICE` added; `CONTRIBUTING`,
  `CODE_OF_CONDUCT`, `SECURITY`, and the RFC process (`docs/rfcs/`) authored.
- `GOVERNANCE.md` (maintainership model) and `ROADMAP.md` (public, ADR-grounded
  roadmap) authored. A top-level project `README.md` and a `CODEOWNERS` file remain
  as follow-ups.

## Alternatives considered

- **Proprietary / internal** — rejected; the goal is a Spark-competing community.
- **OSS without a dedicated DevRel seat** — rejected in favour of a dedicated owner.

## References

- Community/governance models of Apache Spark, Apache Flink, Apache DataFusion, and
  DuckDB.
