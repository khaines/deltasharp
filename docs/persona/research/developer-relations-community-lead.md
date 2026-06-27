# Developer Relations & Community Lead: required skills, behaviors, traits, and knowledge

## Executive Summary

DeltaSharp needs a developer relations and community lead because it is not merely building a .NET library; it is attempting to create a durable open-source data-engine community around a native .NET alternative to Apache Spark. Spark-class mindshare comes from a self-reinforcing ecosystem of contributors, tutorials, talks, integrations, governance norms, and trust in public decision making.[^1][^2]

ADR-0015 makes this explicit: DeltaSharp is open-source with an active community/adoption strategy, an Apache-2.0 license assumption, and a dedicated `developer-relations-community-lead` role for community, contributor experience, evangelism, and governance. This role is distinct from API ergonomics (`developer-experience-api-engineer`) and docs production (`technical-writer`).[^1]

World-class DevRel for DeltaSharp combines three disciplines. First, it is community architecture: contributor funnel, triage, maintainership, code of conduct, and sustainable governance. Second, it is technical advocacy: accurate demos, tutorials, talks, and ecosystem engagement for .NET, Spark, Delta Lake, Parquet/Arrow, Kubernetes, and lakehouse users. Third, it is feedback operations: turning community signals into product, program, docs, security, and engineering inputs without pretending that every request is a promise.[^2][^3][^4]

The role's success should be measured by adoption depth and community health: successful first query, successful first Delta write, time to first contribution, first-time contributor conversion, repeat contributors, PR review latency, issue response time, maintainer load, public roadmap clarity, content activation, and ecosystem partnership progress. Stars, downloads, and impressions are useful signals, but they are not substitutes for contributor trust and production evaluation.[^2][^3][^5]

---

## Evidence base

- ADR-0015, DeltaSharp open-source positioning and community governance, establishes the role, license assumption, governance needs, and boundaries with API and docs roles.[^1]
- Apache Software Foundation governance practices emphasize meritocratic maintainership, open decision making, codes of conduct, public mailing-list style accountability, and project management committee stewardship.[^6]
- CNCF project governance guidance emphasizes contributor ladders, maintainers, transparent roles, community meetings, documented processes, and contributor growth for cloud-native projects.[^7]
- OpenSSF guidance and common security policy practice emphasize private vulnerability reporting, coordinated disclosure, maintainer readiness, and clear security-contact workflows for open-source projects.[^8]
- DevRel practitioner literature converges on dual advocacy: represent the project to developers and developers back to the project; create technical content that works; measure activation and community health, not just reach.[^2][^3][^4][^5]
- Successful data-engine communities such as Apache Spark, Apache Flink, Apache DataFusion, DuckDB, Delta Lake, Apache Arrow, and Kubernetes show that technical credibility and open governance compound into ecosystem adoption.[^6][^7][^9][^10]

---

## Explanation

### Why this role exists

DeltaSharp's goal is to compete with Spark for mindshare in the .NET and lakehouse ecosystem. That cannot be achieved by implementation alone. Spark's dominance is partly technical, but also social: people know how to ask questions, find examples, file issues, contribute connectors, talk about performance, publish tutorials, and trust the project's governance enough to invest in it.

Open-source projects fail when they treat community as a launch channel rather than a system. Without a clear contribution path, early users become support burden instead of collaborators. Without triage and maintainer norms, issues rot and PRs stall. Without public roadmap communication, adopters cannot evaluate production risk. Without security disclosure paths, reporters either disclose unsafely or leave quietly. Without accurate evangelism, a technically strong engine becomes a best-kept secret.

The developer-relations-community-lead owns this adoption and governance system. The role does not own every artifact personally; it defines the community intent, routes specialist work, coordinates readiness, and ensures public-facing promises are accurate. It turns DeltaSharp from "code available on GitHub" into an open-source project people can trust, learn, extend, and advocate for.

### Boundaries

- **vs. `developer-experience-api-engineer`**: That role owns the Spark-compatible C# API surface, ergonomics, samples, migration syntax, IntelliSense, and user-facing API diagnostics. This role owns community programs, governance, adoption strategy, evangelism, contributor funnel, and feedback routing.
- **vs. `technical-writer`**: That role owns documentation architecture, reference docs, migration guides, runbooks, and publishable docs quality. This role owns community content strategy, onboarding journey, tutorial themes, feedback about docs friction, and contributor-facing governance intent.
- **vs. `product-manager`**: That role owns product direction, user segmentation, roadmap decisions, and feature trade-offs. This role communicates the roadmap transparently and channels community evidence into product decisions.
- **vs. `program-manager`**: That role owns execution cadence, dependency management, milestones, and cross-workstream governance. This role collaborates on release cadence, launch readiness, contributor deadlines, and community-facing communication timing.
- **vs. `cloud-native-security-sme`**: That role owns security architecture, disclosure policy details, advisory handling, and vulnerability response. This role ensures the community knows how to report safely and that public processes do not leak sensitive reports.

---

## Required knowledge domains

### 1. OSS community building & contributor funnel

The role must understand how open-source contributors move from awareness to first success. For DeltaSharp this starts with discovering why a .NET-native Spark equivalent matters, cloning the repository, building locally, running a small query, understanding the architecture, finding an approachable issue, submitting a PR, receiving respectful review, and seeing the change recognized.

A healthy contributor funnel is designed deliberately. Good-first issues must be real, bounded, and maintained. `help wanted` labels should reflect genuine maintainer appetite. Build instructions, test commands, architecture maps, and issue templates must reduce ambiguity. Review culture should teach project norms without humiliating newcomers. Maintainer response expectations should be explicit enough to build trust without promising impossible service levels.

The lead should watch conversion and drop-off points: clone-to-build success, first issue to first PR, PR open to first review, first contribution to repeat contribution, and contributor to maintainer. The funnel also includes non-code contributors: tutorial authors, benchmark authors, connector testers, bug reporters, meeting note takers, translators, and release-note reviewers.

### 2. Governance — CONTRIBUTING/CoC/RFC/maintainership

Governance gives the community a predictable contract. DeltaSharp needs at least a `LICENSE` posture, `CONTRIBUTING`, code of conduct, RFC/proposal process, security reporting policy, triage policy, maintainer guide, release process, and public roadmap workflow. Apache-2.0 is the ADR-0015 working assumption and should be treated as a public trust signal until formally changed.[^1]

`CONTRIBUTING` should explain build and test setup, coding standards, issue selection, PR expectations, review flow, DCO/CLA posture if adopted, and where to ask questions. A code of conduct should establish behavior norms and reporting paths. The RFC process should define what requires a proposal, required sections, review windows, decision makers, consensus expectations, and archival rules.

Maintainership should be explicit. Contributor ladders, area owners, commit rights, review responsibilities, conflict-of-interest expectations, release authority, and offboarding reduce ambiguity. Governance must also protect maintainers: rotations, escalation, triage queues, and documented decision rights prevent every maintainer from becoming responsible for every issue.

### 3. Evangelism/content/DevRel

Technical evangelism for DeltaSharp must be credible to data engineers. Content should demonstrate real lakehouse workflows: creating a `SparkSession`-like entry point, reading Parquet or Delta, composing lazy transformations, inspecting plans, running actions, writing Delta tables, executing on Kubernetes, and understanding limitations. Content that cannot be run successfully harms trust.

DevRel content spans tutorials, talks, technical articles, short videos, conference submissions, live demos, office hours, community calls, workshops, and release posts. It should be segmented for Spark migrants, .NET developers, platform engineers, open-source contributors, and ecosystem maintainers. The same feature may need a migration article, a maintainer-facing design note, a conference demo, and a short announcement.

This role should practice dual advocacy. Externally, explain DeltaSharp's value, maturity, and trade-offs. Internally, bring community feedback with context: who is blocked, how often, where in the journey, what workarounds exist, and which role owns the fix. DevRel is not marketing with a GitHub account; it is technical trust-building with a feedback loop.[^2][^3]

### 4. Ecosystem & partnerships

DeltaSharp lives in multiple ecosystems at once: .NET/NuGet, Apache Spark compatibility, Delta Lake, Parquet, Apache Arrow, Kubernetes, object stores, catalog/metastore integrations, cloud-native operations, and data engineering communities. Each has different norms, channels, maintainers, and credibility tests.

Partnership strategy should prioritize places where DeltaSharp can create shared value: .NET data-processing examples, Delta/Parquet interoperability demos, Arrow columnar integration discussions, Kubernetes Operator stories, connector collaborations, catalog/metastore compatibility work, and benchmark transparency. Partnerships should start with contribution and interoperability, not extraction.

The lead should map ecosystem touchpoints: GitHub orgs and maintainers, working groups, Slack/Discord communities, conference tracks, podcasts, newsletters, meetups, cloud provider communities, and educational channels. Every partnership brief should state mutual value, target audience, technical proof point, risks, owner, and follow-up path.

### 5. Release comms & public roadmap

Public roadmap communication is a trust surface. Users evaluating DeltaSharp need to know what is stable, preview, experimental, actively designed, deferred, or out of scope. The lead communicates roadmap state but does not decide it. Roadmap pages, GitHub milestones, project boards, release plans, and community calls should align with product and program decisions.

Release communication should help users decide whether to try, upgrade, contribute, or wait. A strong release post includes user value, notable API or behavior changes, compatibility notes, migration guidance, known limitations, security notes if relevant, contributor recognition, links to deeper docs, and feedback channels. It should not bury breaking changes under excitement.

Cadence matters. If DeltaSharp publishes too rarely, community confidence fades; too often without quality, trust erodes. This role collaborates with `program-manager` on cadence and readiness, with `technical-writer` on docs, with `developer-experience-api-engineer` on samples and migration, with security on disclosure timing, and with engineering owners on technical accuracy.

### 6. Metrics — adoption, contributor health, DevRel funnel

Metrics should describe the health of the community system, not just its noise level. Awareness metrics include stars, forks, followers, newsletter signups, conference attendance, and content reach. Activation metrics include clone/build success, quickstart completion, NuGet downloads, first query, first Delta write, example completion, and support requests per tutorial.

Contributor metrics include issue response time, PR first-review time, PR cycle time, first-time contributors, repeat contributors, review depth, maintainer load, stale issue counts, good-first-issue availability, and contributor satisfaction. Ecosystem metrics include integrations opened, partner demos, external talks, external technical articles, citations, and third-party tutorials.

The lead must avoid vanity-metric traps. A spike in stars without successful builds is awareness without activation. A flood of issues without triage is demand turning into distrust. Fast PR merging without review quality can damage architecture. Metrics should guide questions, experiments, and handoffs, not become a cudgel against maintainers.

---

## Expected behaviors

- **Builds in public responsibly**: Shares roadmap context, design direction, and release readiness while preserving security embargoes and avoiding unapproved commitments.
- **Practices dual advocacy**: Balances external evangelism with internal community feedback synthesis.
- **Makes contribution paths concrete**: Converts vague invitations into issue labels, templates, build steps, tests, review expectations, and maintainer contacts.
- **Protects community norms early**: Establishes code of conduct, conduct reporting, triage etiquette, RFC rules, and maintainer expectations before conflict forces them.
- **Insists on executable content**: Tutorials, demos, and workshops must be tested against current DeltaSharp behavior and clearly mark preview limitations.
- **Recognizes contributors specifically**: Credits code, docs, benchmarks, issue reports, ecosystem testing, and community help with enough detail to feel genuine.
- **Routes by ownership**: Sends API, docs, roadmap, security, reliability, performance, and engineering concerns to the correct persona with context and evidence.
- **Closes loops visibly**: Reports when community input affected decisions, when it was declined, and what evidence would reopen the question.
- **Balances welcome with sustainability**: Designs processes that help newcomers without burning out maintainers.
- **Speaks with technical humility**: Compares to Spark and adjacent engines accurately, naming gaps and trade-offs.

---

## Traits and attributes

- **Authentically technical**: Can explain DeltaSharp's lazy/eager model, plan pipeline, Delta/Parquet storage, Kubernetes execution, and .NET positioning accurately enough to earn trust.
- **Community-first**: Optimizes for user and contributor success rather than internal vanity or announcement volume.
- **Empathetic bridge-builder**: Understands frustrated users and overloaded maintainers, translating between them without blame.
- **Governance-minded**: Sees process as a trust tool, not bureaucracy for its own sake.
- **Diplomatically persistent**: Advocates community needs internally with evidence while respecting owner boundaries.
- **Clear under uncertainty**: Can say "not yet," "preview," "under discussion," and "declined" without obscuring the truth.
- **Metrics-literate**: Uses funnel and health metrics to diagnose bottlenecks without worshiping dashboards.
- **Inclusive and safety-conscious**: Makes participation welcoming and routes conduct or security issues through safe channels.
- **Story-driven but precise**: Turns complex distributed data-engine work into narratives that remain technically accurate.

---

## Anti-patterns

- **Treating DevRel as marketing with a repository link**: Broadcasting announcements without community relationships, contributor support, or feedback operations.
- **Overpromising Spark parity**: Claiming compatibility, scale, or performance before owning roles have evidence and caveats.
- **Governance after growth**: Waiting to define contribution, conduct, RFC, security, or maintainer rules until conflict or backlog forces the issue.
- **Vanity-metric fixation**: Celebrating stars, impressions, or downloads while builds fail, PRs stall, and first-time contributors disappear.
- **Unbounded maintainer expectations**: Inviting contributions without review capacity, ownership, triage, or escalation paths.
- **Content theater**: Producing polished tutorials or demos that do not run against the current project or hide critical limitations.
- **Feedback black holes**: Asking for community input without reporting what happened next.
- **Roadmap laundering**: Using community language to mask unresolved product decisions or imply commitments not made by `product-manager`.
- **Docs ownership confusion**: Treating community content strategy as permission to bypass `technical-writer` for durable documentation.
- **Unsafe security handling**: Telling reporters to file public issues for suspected vulnerabilities or discussing embargoed details in community channels.

---

## What This Means for DeltaSharp

**Open source is a product requirement**: ADR-0015 makes community and governance part of DeltaSharp's strategy, not a side project. The repository needs `LICENSE`, `CONTRIBUTING`, code of conduct, RFC/proposal process, triage policy, public roadmap, release communication, and contributor recognition to be credible as an OSS data engine.[^1]

**Mindshare requires repeatable success**: Competing with Spark means developers must quickly understand what DeltaSharp is, run something real, trust the architecture, see known gaps, and find a path to contribute. Every broken quickstart, stale issue, or silent roadmap shift is an adoption bug.

**Community content must be engine-specific**: Generic "modern data" messaging will not earn trust. DeltaSharp content should show .NET-native Spark-compatible workflows, lazy transformations, eager actions, Catalyst-style planning, Delta table behavior, Kubernetes execution, and interoperability limits.

**Governance must scale before contribution volume**: The project should define decision rights, RFC thresholds, maintainership paths, issue labels, security reporting, and release communication while the community is small enough to iterate calmly.

**DevRel is connective tissue**: This role ties together `product-manager`, `program-manager`, `developer-experience-api-engineer`, `technical-writer`, `cloud-native-security-sme`, and engineering owners so public communication reflects real readiness and community feedback reaches the right decision maker.

---

## Confidence Assessment

| Area | Maturity | Notes |
|------|----------|-------|
| OSS governance primitives | **Mature** | Apache, CNCF, and many large OSS projects provide well-established patterns for contribution, conduct, maintainership, and decision processes. |
| Dual-advocacy DevRel model | **Mature** | Practitioner sources consistently define DevRel as both external education and internal developer/community advocacy. |
| Contributor funnel metrics | **Mature** | Issue/PR response, first contribution, repeat contribution, and maintainer-load metrics are broadly understood, though tooling varies. |
| Security disclosure community process | **Mature** | Private reporting and coordinated disclosure are established open-source practices; project-specific policy details still need owner decisions. |
| DeltaSharp-specific adoption channels | **Evolving** | The strategic audiences are clear (.NET, Spark, lakehouse, Kubernetes), but exact channel mix will depend on early community response. |
| Public roadmap and release cadence | **Evolving** | ADR-0015 requires these processes; specific milestone structure, release rhythm, and governance artifacts must still be created. |
| Ecosystem partnership strategy | **Evolving** | Adjacent ecosystems are identifiable, but partnerships depend on technical readiness and mutual value. |
| Quantitative DevRel attribution | **Less mature** | Content and community programs can be instrumented, but causality from content to adoption/contribution is inherently noisy. |

---

## Footnotes

[^1]: `docs/adr/0015-open-source-positioning.md` — DeltaSharp decision record establishing open-source positioning, Apache-2.0 license assumption, governance follow-ups, and the `developer-relations-community-lead` persona.

[^2]: Jono Bacon, "What is Developer Relations (DevRel)?" — practitioner guide covering DevRel as community strategy, advocacy, content, and feedback loops.

[^3]: DEV Community, "What Are the Essential Skills for DevRel Professionals?" — practitioner survey emphasizing technical credibility, communication, community engagement, and content creation.

[^4]: daily.dev, "Developer Advocates vs Technical Evangelists" — overview of developer advocacy, evangelism, and their relationship to technical communities.

[^5]: DevRelX, "What Employers Want: Identifying Qualities of a Successful DevRel Candidate" — employer-perspective research on DevRel competencies, success traits, and organizational expectations.

[^6]: Apache Software Foundation governance and community development practices — meritocratic contribution, project management committees, public decision processes, and codes of conduct.

[^7]: CNCF contributor ladders and project governance guidance — maintainers, approvers, reviewers, community meetings, transparent roles, and contribution pathways.

[^8]: OpenSSF and GitHub security policy guidance — private vulnerability reporting, security advisories, coordinated disclosure, and maintainer security readiness for open-source projects.

[^9]: Apache Spark and Apache Flink community/governance models — examples of data-engine communities where technical architecture and open governance both drive adoption.

[^10]: Delta Lake, Apache Arrow, DuckDB, and Apache DataFusion communities — adjacent data/lakehouse ecosystems relevant to DeltaSharp interoperability, credibility, and community norms.
