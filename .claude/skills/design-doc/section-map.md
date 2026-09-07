# Design Document — Section-to-Agent Mapping

This file maps each section of the design document template (`docs/engineering/design/000-template.md`) to the specialist agent persona responsible for generating its content. The `design-doc` skill consults this map to dispatch the right agent for each section.

---

## Section Ownership

| Section | Title | Primary Agent | Advisory Agents | Phase |
|---------|-------|---------------|-----------------|-------|
| §1 | Overview | `cloud-native-distributed-systems-architect` | `product-manager`, `program-manager` | 2 |
| §2 | Logical Architecture | `cloud-native-distributed-systems-architect` | `query-execution-engine-engineer`, `delta-storage-format-engineer`, `data-platform-connectors-engineer`, `developer-experience-api-engineer`, `dotnet-framework-runtime-engineer` | 2 |
| §3 | Functional Test Scenarios | `reliability-test-chaos-engineer` | `query-execution-engine-engineer`, `delta-storage-format-engineer`, `developer-experience-api-engineer` | 3 |
| §4 | Performance | `performance-benchmarking-engineer` | `query-execution-engine-engineer`, `delta-storage-format-engineer`, `compute-storage-finops-engineer`, `dotnet-framework-runtime-engineer` | 3 |
| §5 | Security | `cloud-native-security-sme` | `privacy-compliance-grc-lead`, `cloud-native-site-reliability-engineer` | 4 |
| §6 | Threat Model | `cloud-native-security-sme` | `cloud-native-distributed-systems-architect`, `privacy-compliance-grc-lead`, `cloud-native-site-reliability-engineer` | 4 |
| §7 | Observability | `cloud-native-site-reliability-engineer` | `query-execution-engine-engineer`, `delta-storage-format-engineer`, `performance-benchmarking-engineer` | 5 |
| §8 | Rollout & Risk | `cloud-native-site-reliability-engineer` | `cloud-native-distributed-systems-architect`, `reliability-test-chaos-engineer`, `compute-storage-finops-engineer` | 5 |
| §9 | Open Questions & Decisions | `cloud-native-distributed-systems-architect` | All participating agents | 2–5 |
| §10 | References | `design-doc` skill (automated) | `technical-writer` | 6 |

---

## Ownership Rules

1. **Primary agent** generates the section content. The section should reflect that agent's expertise, vocabulary, and decision heuristics.
2. **Advisory agents** are listed for reference context. During initial generation, the primary agent handles all content. Advisory agents participate during the Phase 8 review-fix loop, where the `review-pr` skill may route findings to them for specialist review. Advisory agents may also be explicitly consulted when the primary agent encounters a question in the advisory agent's domain — for example, the Architect may consult `query-execution-engine-engineer` on Catalyst-style plan boundaries or `delta-storage-format-engineer` on commit protocol implications.
3. **§9 (Open Questions)** is populated incrementally by every agent. Each agent adds questions surfaced during their section generation.
4. **§10 (References)** is assembled automatically by the skill during Phase 6 (Assembly & Validation) from all docs consulted during generation, then reviewed for clarity by `technical-writer` when needed.

---

## Agent Persona References

| Agent | Canonical Spec | Research |
|-------|---------------|----------|
| `product-manager` | `docs/persona/agents/product-manager-agent.md` | `docs/persona/research/product-manager-vs-program-manager.md` |
| `program-manager` | `docs/persona/agents/program-manager-agent.md` | `docs/persona/research/product-manager-vs-program-manager.md` |
| `technical-writer` | `docs/persona/agents/technical-writer-agent.md` | `docs/persona/research/technical-writer.md` |
| `privacy-compliance-grc-lead` | `docs/persona/agents/privacy-compliance-grc-lead-agent.md` | `docs/persona/research/privacy-compliance-grc-lead.md` |
| `cloud-native-distributed-systems-architect` | `docs/persona/agents/cloud-native-distributed-systems-architect-agent.md` | `docs/persona/research/cloud-native-distributed-systems-architect.md` |
| `cloud-native-site-reliability-engineer` | `docs/persona/agents/cloud-native-site-reliability-engineer-agent.md` | `docs/persona/research/cloud-native-site-reliability-engineer.md` |
| `cloud-native-security-sme` | `docs/persona/agents/cloud-native-security-sme-agent.md` | `docs/persona/research/cloud-native-security-sme.md` |
| `reliability-test-chaos-engineer` | `docs/persona/agents/reliability-test-chaos-engineer-agent.md` | `docs/persona/research/reliability-test-chaos-engineer.md` |
| `delta-storage-format-engineer` | `docs/persona/agents/delta-storage-format-engineer-agent.md` | `docs/persona/research/delta-storage-format-engineer.md` |
| `query-execution-engine-engineer` | `docs/persona/agents/query-execution-engine-engineer-agent.md` | `docs/persona/research/query-execution-engine-engineer.md` |
| `performance-benchmarking-engineer` | `docs/persona/agents/performance-benchmarking-engineer-agent.md` | `docs/persona/research/performance-benchmarking-engineer.md` |
| `data-platform-connectors-engineer` | `docs/persona/agents/data-platform-connectors-engineer-agent.md` | `docs/persona/research/data-platform-connectors-engineer.md` |
| `compute-storage-finops-engineer` | `docs/persona/agents/compute-storage-finops-engineer-agent.md` | `docs/persona/research/compute-storage-finops-engineer.md` |
| `developer-experience-api-engineer` | `docs/persona/agents/developer-experience-api-engineer-agent.md` | `docs/persona/research/developer-experience-api-engineer.md` |
| `dotnet-framework-runtime-engineer` | `docs/persona/agents/dotnet-framework-runtime-engineer-agent.md` | `docs/persona/research/dotnet-framework-runtime-engineer.md` |
