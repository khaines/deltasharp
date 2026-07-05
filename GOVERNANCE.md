# DeltaSharp Governance

DeltaSharp is an open, meritocratic open-source project. This document describes
how decisions are made and how people take on responsibility. It is intentionally
lightweight for an early-stage project and will evolve (via PR + maintainer
approval) as the community grows.

## Principles

- **Open by default.** Discussion, decisions, and roadmaps happen in public
  (issues, discussions, PRs, RFCs, ADRs).
- **Meritocratic.** Influence and responsibility are earned through sustained,
  high-quality contribution and good judgment.
- **Consensus-seeking.** We prefer consensus; we vote only when needed.

## Roles

- **Users** — people who use DeltaSharp. Feedback, bug reports, and questions are
  valued contributions.
- **Contributors** — anyone who contributes code, docs, tests, triage, or design.
  All contributions are under Apache-2.0 with a [DCO](CONTRIBUTING.md) sign-off.
- **Maintainers** — contributors with review/merge rights for one or more areas.
  They review PRs, mentor contributors, uphold quality and the Code of Conduct, and
  shepherd RFCs. Areas map to the engine subsystems (see `docs/persona/agents/` and
  the ADRs); a `CODEOWNERS` file will route reviews as maintainership grows.
- **Technical Steering Committee (TSC)** — maintainers responsible for overall
  technical direction, cross-cutting and tie-breaking decisions, releases, and
  governance changes. While the project is young, the founding maintainers act as
  the TSC; it formalizes as the project grows.

## Decision-making

1. **Lazy consensus.** Most changes proceed by PR: if there are no sustained
   objections from maintainers within a reasonable review window, the change is
   accepted. At least one maintainer approval is required to merge.
2. **RFCs** ([`docs/rfcs/`](docs/rfcs/README.md)) are required for substantial or
   cross-cutting changes and run a Final Comment Period before disposition.
3. **ADRs** ([`docs/adr/`](docs/adr/README.md)) record foundational decisions and
   are the source of truth; summaries defer to them.
4. **Voting.** When consensus cannot be reached, the TSC decides by simple majority
   of voting members; the TSC chair breaks ties. Voting is public.

## Becoming a maintainer

Existing maintainers nominate contributors who have demonstrated:

- a sustained track record of quality contributions in an area;
- sound technical judgment and constructive code review;
- adherence to the Code of Conduct and collaborative behavior.

Nominations are confirmed by maintainer consensus (or a TSC vote). Maintainers who
become inactive may move to emeritus status; they are always welcome back.

## Code of Conduct

Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md). During the
project's single-maintainer phase, the community leader responsible for enforcement is
the founding maintainer, **[@khaines](https://github.com/khaines)**; reports are handled
confidentially through the reporting channels in the
[Code of Conduct](CODE_OF_CONDUCT.md). As additional maintainers join, the enforcement
group grows with them.

## Security

Vulnerabilities are handled under [SECURITY.md](SECURITY.md) via coordinated
disclosure.

## Licensing and intellectual property

DeltaSharp is licensed under the [Apache License 2.0](LICENSE). Contributions are
accepted under the same license via DCO sign-off; we do not require a CLA.

## Changing this document

Governance changes are proposed by PR and require TSC/maintainer approval. Material
changes should be announced to the community.
